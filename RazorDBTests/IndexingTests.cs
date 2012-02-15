﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NUnit.Framework;
using RazorDB;
using System.Diagnostics;
using System.IO;

namespace RazorDBTests {
    
    [TestFixture]
    public class IndexingTests {

        [Test]
        public void TruncateTest() {

            string path = Path.GetFullPath("TestData\\TruncateTest");
            using (var db = new KeyValueStore(path)) {
                var indexed = new SortedDictionary<string, byte[]>();
                for ( int i=0; i < 15000; i++) {
                    indexed["RandomIndex"] = ByteArray.Random(20).InternalBytes;
                    var randKey = ByteArray.Random(40);
                    var randValue = ByteArray.Random(256);
                    db.Set(randKey.InternalBytes, randValue.InternalBytes, indexed);
                }
            }
            using (var db = new KeyValueStore(path)) {
                db.Truncate();
            }
            var files = Directory.GetFiles(path, "*.*", SearchOption.AllDirectories);
            Assert.AreEqual(new string[] { Path.GetFullPath(Path.Combine(path,"0.jf")) }, files);
            var dirs = Directory.GetDirectories(path, "*.*", SearchOption.AllDirectories);
            Assert.AreEqual(new string[0], dirs);
        }

        [Test]
        public void AddObjectsAndLookup() {

            string path = Path.GetFullPath("TestData\\AddObjectsAndLookup");
            var timer = new Stopwatch();

            using (var db = new KeyValueStore(path)) {
                db.Truncate();

                var indexed = new SortedDictionary<string, byte[]>();
                indexed["NumberType"] = Encoding.UTF8.GetBytes("Fib");
                db.Set(BitConverter.GetBytes(112), Encoding.UTF8.GetBytes("112"), indexed);
                db.Set(BitConverter.GetBytes(1123), Encoding.UTF8.GetBytes("1123"), indexed);
                db.Set(BitConverter.GetBytes(11235), Encoding.UTF8.GetBytes("11235"), indexed);
                db.Set(BitConverter.GetBytes(112358), Encoding.UTF8.GetBytes("112358"), indexed);

                indexed["NumberType"] = Encoding.UTF8.GetBytes("Seq");
                db.Set(BitConverter.GetBytes(1), Encoding.UTF8.GetBytes("1"), indexed);
                db.Set(BitConverter.GetBytes(2), Encoding.UTF8.GetBytes("2"), indexed);
                db.Set(BitConverter.GetBytes(3), Encoding.UTF8.GetBytes("3"), indexed);
                db.Set(BitConverter.GetBytes(4), Encoding.UTF8.GetBytes("4"), indexed);

                indexed["NumberType"] = Encoding.UTF8.GetBytes("Zero");
                db.Set(BitConverter.GetBytes(0), Encoding.UTF8.GetBytes("0"), indexed);
            }
            using (var db = new KeyValueStore(path)) {
                var zeros = db.Find("NumberType", Encoding.UTF8.GetBytes("Zero")).ToList();
                Assert.AreEqual(1, zeros.Count() );
                Assert.AreEqual("0", Encoding.UTF8.GetString(zeros[0]));

                var seqs = db.Find("NumberType", Encoding.UTF8.GetBytes("Seq")).ToList();
                Assert.AreEqual(4, seqs.Count());
                Assert.AreEqual("1", Encoding.UTF8.GetString(seqs[0]));
                Assert.AreEqual("2", Encoding.UTF8.GetString(seqs[1]));
                Assert.AreEqual("3", Encoding.UTF8.GetString(seqs[2]));
                Assert.AreEqual("4", Encoding.UTF8.GetString(seqs[3]));

                var fib = db.Find("NumberType", Encoding.UTF8.GetBytes("Fib")).ToList();
                Assert.AreEqual(4, seqs.Count());
                Assert.AreEqual("1123", Encoding.UTF8.GetString(fib[0]));
                Assert.AreEqual("112", Encoding.UTF8.GetString(fib[1]));
                Assert.AreEqual("11235", Encoding.UTF8.GetString(fib[2]));
                Assert.AreEqual("112358", Encoding.UTF8.GetString(fib[3]));

                var non = db.Find("NoIndex", new byte[] { 23 }).ToList();
                Assert.AreEqual(0, non.Count());
                non = db.Find("NumberType", Encoding.UTF8.GetBytes("Unfound")).ToList();
                Assert.AreEqual(0, non.Count());
            }
        }

        [Test]
        public void AddObjectsAndLookupWhileMerging() {

            string path = Path.GetFullPath("TestData\\AddObjectsAndLookup");
            var timer = new Stopwatch();

            using (var db = new KeyValueStore(path)) {
                db.Truncate();
                int totalSize = 0;
                db.Manifest.Logger = msg => Console.WriteLine(msg);

                var indexed = new SortedDictionary<string, byte[]>();
                int num_items = 1000000;
                timer.Start();
                for (int i = 0; i < num_items; i++) {
                    indexed["Mod"] = BitConverter.GetBytes(i % 100);
                    db.Set(BitConverter.GetBytes(i), BitConverter.GetBytes(i * 1000), indexed);
                    totalSize += 8 + 4;
                }
                timer.Stop();

                Console.WriteLine("Wrote data (with indexing) at a throughput of {0} MB/s", (double)totalSize / timer.Elapsed.TotalSeconds / (1024.0 * 1024.0));

                timer.Reset();
                timer.Start();
                var ctModZeros = db.Find("Mod", BitConverter.GetBytes((int)0)).Count();
                timer.Stop();
                Assert.AreEqual(10000, ctModZeros);
                Console.WriteLine("Scanned index at a throughput of {0} items/s", (double) ctModZeros / timer.Elapsed.TotalSeconds);
            }
        }

    }
}