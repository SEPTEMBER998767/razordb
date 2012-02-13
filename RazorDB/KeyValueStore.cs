﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.IO;
using System.Diagnostics;

namespace RazorDB {
    
    public class KeyValueStore : IDisposable {

        public KeyValueStore(string baseFileName) {
            if (!Directory.Exists(baseFileName)) {
                Directory.CreateDirectory(baseFileName);
            }
            _manifest = new Manifest(baseFileName);
            _currentJournaledMemTable = new JournaledMemTable(_manifest.BaseFileName, _manifest.CurrentVersion(0));
            _tableManager = new TableManager(_manifest);
            _blockIndexCache = new Cache();
        }

        ~KeyValueStore() {
            Dispose();
        }

        private Manifest _manifest;
        private TableManager _tableManager;
        private Cache _blockIndexCache;

        public Manifest Manifest { get { return _manifest; } }

        private volatile JournaledMemTable _currentJournaledMemTable;

        public void Truncate() {
            _currentJournaledMemTable.Close();
            _tableManager.Close();

            string basePath = Path.GetFullPath(Manifest.BaseFileName);
            foreach (string file in Directory.GetFiles(basePath, "*.*", SearchOption.TopDirectoryOnly)) {
                File.Delete(file);
            }

            _manifest = new Manifest(basePath);
            _currentJournaledMemTable = new JournaledMemTable(_manifest.BaseFileName, _manifest.CurrentVersion(0));
            _tableManager = new TableManager(_manifest);
            _blockIndexCache = new Cache();
        }

        public void Set(byte[] key, byte[] value) {
            var k = new ByteArray(key);
            var v = new ByteArray(value);

            int adds = 10;
            while (!_currentJournaledMemTable.Add(k, v)) {
                adds--;
                if (adds <= 0)
                    throw new InvalidOperationException("Failed too many times trying to add an item to the JournaledMemTable");
            }

            if (_currentJournaledMemTable.Full) {
                RotateMemTable();
            }
        }

        public byte[] Get(byte[] key) {
            ByteArray lookupKey = new ByteArray(key);
            ByteArray output;
            // First check the current memtable
            if (_currentJournaledMemTable.Lookup(lookupKey, out output)) {
                return output.Length == 0 ? null : output.InternalBytes;
            }
            // Capture copy of the rotated table if there is one
            var rotatedMemTable = _rotatedJournaledMemTable;
            if (rotatedMemTable != null) {
                if (rotatedMemTable.Lookup(lookupKey, out output)) {
                    return output.Length == 0 ? null : output.InternalBytes;
                }
            }
            // Now check the files on disk
            using (var manifestSnapshot = _manifest.GetSnapshot()) {
                // Must check all pages on level 0
                var zeroPages = manifestSnapshot.Manifest.GetPagesAtLevel(0);
                foreach (var page in zeroPages) {
                    if (SortedBlockTable.Lookup(manifestSnapshot.Manifest.BaseFileName, page.Level, page.Version, _blockIndexCache, lookupKey, out output)) {
                        return output.Length == 0 ? null : output.InternalBytes;
                    }
                }
                // If not found, must check pages on the higher levels, but we can use the page index to make the search quicker
                for (int level = 1; level < manifestSnapshot.Manifest.NumLevels; level++) {
                    var page = manifestSnapshot.Manifest.FindPageForKey(level, lookupKey);
                    if (page != null && SortedBlockTable.Lookup(manifestSnapshot.Manifest.BaseFileName, page.Level, page.Version, _blockIndexCache, lookupKey, out output)) {
                        return output.Length == 0 ? null : output.InternalBytes;
                    }
                }
            }
            // OK, not found anywhere, return null
            return null;
        }

        public void Delete(byte[] key) {
            Set(key, new byte[0]);
        }

        public IEnumerable<KeyValuePair<byte[], byte[]>> Enumerate() {

            var enumerators = new List<IEnumerable<KeyValuePair<ByteArray, ByteArray>>>();
            
            // Now check the files on disk
            using (var manifestSnapshot = _manifest.GetSnapshot()) {
                // Main MemTable
                enumerators.Add(_currentJournaledMemTable.EnumerateSnapshot());

                // Capture copy of the rotated table if there is one
                var rotatedMemTable = _rotatedJournaledMemTable;
                if (rotatedMemTable != null) {
                    enumerators.Add(rotatedMemTable.EnumerateSnapshot());
                }

                for (int i = 0; i < manifestSnapshot.Manifest.NumLevels; i++) {
                    var pages = manifestSnapshot.Manifest.GetPagesAtLevel(i)
                        .OrderByDescending(page => page.Level)
                        .Select(page => new SortedBlockTable(manifestSnapshot.Manifest.BaseFileName, page.Level, page.Version).Enumerate());
                    enumerators.AddRange(pages);
                }

                foreach (var pair in MergeEnumerator.Merge(enumerators, t => t.Key)) {
                    yield return new KeyValuePair<byte[], byte[]>(pair.Key.InternalBytes, pair.Value.InternalBytes);
                }
            }
        }

        public IEnumerable<KeyValuePair<byte[], byte[]>> EnumerateFromKey(byte[] startingKey) {

            var enumerators = new List<IEnumerable<KeyValuePair<ByteArray, ByteArray>>>();
            ByteArray key = new ByteArray(startingKey);

            // Now check the files on disk
            using (var manifestSnapshot = _manifest.GetSnapshot()) {
                // Main MemTable
                enumerators.Add(_currentJournaledMemTable.EnumerateSnapshotFromKey(key));

                // Capture copy of the rotated table if there is one
                var rotatedMemTable = _rotatedJournaledMemTable;
                if (rotatedMemTable != null) {
                    enumerators.Add(rotatedMemTable.EnumerateSnapshotFromKey(key));
                }

                for (int i = 0; i < manifestSnapshot.Manifest.NumLevels; i++) {
                    var pages = manifestSnapshot.Manifest.GetPagesAtLevel(i)
                        .OrderByDescending(page => page.Level)
                        .Select(page => new SortedBlockTable(manifestSnapshot.Manifest.BaseFileName, page.Level, page.Version).EnumerateFromKey(_blockIndexCache, key));
                    enumerators.AddRange(pages);
                }

                foreach (var pair in MergeEnumerator.Merge(enumerators, t => t.Key)) {
                    yield return new KeyValuePair<byte[], byte[]>(pair.Key.InternalBytes, pair.Value.InternalBytes);
                }
            }
        }


        private object memTableRotationLock = new object();
        private JournaledMemTable _rotatedJournaledMemTable;
        private Semaphore _rotationSemaphore = new Semaphore(1, 1);

#pragma warning disable 420
        public void RotateMemTable() {
            lock (memTableRotationLock) {
                // Double check the flag in case we have multiple threads that make it into this routine
                if (_currentJournaledMemTable.Full) {
                    _rotationSemaphore.WaitOne();    // Wait for the rotation gate to be open, and automatically reset once a single thread gets through.

                    _rotatedJournaledMemTable = Interlocked.Exchange<JournaledMemTable>(ref _currentJournaledMemTable, new JournaledMemTable(_manifest.BaseFileName, _manifest.NextVersion(0)));

                    ThreadPool.QueueUserWorkItem((o) => {
                        try {
                            _rotatedJournaledMemTable.WriteToSortedBlockTable(_manifest);
                            _rotatedJournaledMemTable = null;
                        } finally {
                            _rotationSemaphore.Release(); // Open the gate for the next rotation
                        }
                    });
                }
            }
        }
#pragma warning restore 420

        public void Dispose() {
            Close();
        }

        public void Close() {
            // Make sure any inflight rotations have occurred before shutting down.
            if (!_rotationSemaphore.WaitOne(30000))
                throw new TimeoutException("Timed out waiting for table rotation to complete.");
            // Release again in case another thread tries to close it again.
            _rotationSemaphore.Release();

            if (_tableManager != null) {
                _tableManager.Close();
                _tableManager = null;
            }
            if (_currentJournaledMemTable != null) {
                _currentJournaledMemTable.Close();
                _currentJournaledMemTable = null;
            }
            // Don't finalize since we already closed it.
            GC.SuppressFinalize(this);
        }
    }

}
