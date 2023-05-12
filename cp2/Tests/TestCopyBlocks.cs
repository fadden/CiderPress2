/*
 * Copyright 2023 faddenSoft
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */
using CommonUtil;
using System;

using static DiskArc.Defs;

namespace cp2.Tests {
    /// <summary>
    /// Tests "copy-blocks".
    /// </summary>
    internal static class TestCopyBlocks {
        public static void RunTest(ParamsBag parms) {
            Controller.Stdout.WriteLine("  CopyBlocks...");
            Controller.PrepareTestTmp(parms);

            //
            // Test scenarios:
            // - Simple 800K to WOZ conversion, no range specified.
            // - Try 140K to 800K w/o range; should fail.  Repeat with range (creates oversized).
            // - Reconstruct MultiPart.hdv by copying chunks; result should swap partitions
            //   (which will screw up the APM tags, but that's fine).
            //
            TestDiskCopy(parms);
            TestPartialCopy(parms);
            TestTurducken(parms);

            Controller.RemoveTestTmp(parms);
        }

        private static void TestDiskCopy(ParamsBag parms) {
            string po800File = Path.Join(Controller.TEST_DATA, "prodos", "simple-sparse.po");
            string wozFile = Path.Join(Controller.TEST_TMP, "test800.woz");

            // Create unformatted 800K WOZ.
            if (!DiskUtil.HandleCreateDiskImage("cdi", new string[] { wozFile, "800k" }, parms)) {
                throw new Exception("cdi " + wozFile + " failed");
            }
            if (Catalog.HandleList("list", new string[] { wozFile }, parms)) {
                throw new Exception("list " + wozFile + " succeeded");
            }
            // Copy the full volume.
            if (!DiskUtil.HandleCopyBlocks("copy-blocks",
                    new string[] { po800File, wozFile }, parms)) {
                throw new Exception("copy-blocks " + po800File + " -> " + wozFile + " failed");
            }
            if (!Catalog.HandleList("list", new string[] { wozFile }, parms)) {
                throw new Exception("list " + wozFile + " failed");
            }
            // don't really need to check contents
        }

        private static void TestPartialCopy(ParamsBag parms) {
            string po140File = Path.Join(Controller.TEST_DATA, "prodos", "simple-dir-test.po");
            string wozFile = Path.Join(Controller.TEST_TMP, "test800p.woz");

            // Create unformatted 800K WOZ.
            if (!DiskUtil.HandleCreateDiskImage("cdi", new string[] { wozFile, "800k" }, parms)) {
                throw new Exception("cdi " + wozFile + " failed");
            }
            if (Catalog.HandleList("list", new string[] { wozFile }, parms)) {
                throw new Exception("list " + wozFile + " succeeded");
            }
            // Try to copy with simple command; should fail.
            if (DiskUtil.HandleCopyBlocks("copy-blocks",
                    new string[] { po140File, wozFile }, parms)) {
                throw new Exception("copy-blocks " + po140File + " -> " + wozFile + " succeeded");
            }
            // Copy with explicit range specified.
            if (!DiskUtil.HandleCopyBlocks("copy-blocks",
                    new string[] { po140File, wozFile, "0", "0", "280" }, parms)) {
                throw new Exception("copy-blocks " + po140File + " -> " + wozFile + " failed");
            }
            if (!Catalog.HandleList("list", new string[] { wozFile }, parms)) {
                throw new Exception("list " + wozFile + " failed");
            }
            // don't really need to check contents
        }

        private static void TestTurducken(ParamsBag parms) {
            // Make a copy of a turducken file.
            string inputFile = Path.Join(Controller.TEST_DATA, "turducken", "MultiPart.hdv");
            string tdnFile = Path.Join(Controller.TEST_TMP, "tdn-test.hdv");
            FileUtil.CopyFile(inputFile, tdnFile);
            parms.SkipSimple = true;

            // We're going to swap the ProDOS and HFS parts.  We could open the IMultiPart and
            // dig through it, but we happen to know that the partitions are 1600 blocks each,
            // starting after the 4-block APM header and map.
            FileInfo info = new FileInfo(tdnFile);
            const int DUCK_LEN = (4 + 1600 + 1600) * BLOCK_SIZE;
            if (info.Length != DUCK_LEN) {
                throw new Exception(tdnFile + " does not have expected geometry");
            }

            // Create an unformatted disk image of the appropriate size.
            string dstFile = Path.Combine(Controller.TEST_TMP, "rewritten.po");
            if (!DiskUtil.HandleCreateDiskImage("cdi",
                    new string[] { dstFile, DUCK_LEN.ToString() }, parms)) {
                throw new Exception("cdi " + dstFile + " failed");
            }

            // Copy the bits over.  We're not rewriting the partition map entries, so the
            // name and type will be wrong, but that shouldn't cause problems for this test.
            // The disk becomes recognizable as APM after the first copy, which changes the
            // way it's handled; probably good to test it this way.
            if (!DiskUtil.HandleCopyBlocks("copy-blocks",
                    new string[] { tdnFile, dstFile, "0", "0", "4" }, parms)) {
                throw new Exception("copy-blocks " + dstFile + " 0 0 4 failed");
            }
            if (!DiskUtil.HandleCopyBlocks("copy-blocks",
                    new string[] { tdnFile, dstFile, "4", "1604", "1600" }, parms)) {
                throw new Exception("copy-blocks " + dstFile + " 4 1604 1600 failed");
            }
            if (!DiskUtil.HandleCopyBlocks("copy-blocks",
                    new string[] { tdnFile, dstFile, "1604", "4", "1600" }, parms)) {
                throw new Exception("copy-blocks " + dstFile + " 1604 4 1600 failed");
            }
            // Throw in a bad destination range.  Should fail without doing anything.
            if (DiskUtil.HandleCopyBlocks("copy-blocks",
                    new string[] { tdnFile, dstFile, "0", "1605", "1600" }, parms)) {
                throw new Exception("copy-blocks " + dstFile + " 0 1605 1600 succeeded");
            }

            // Check something in each partition.
            string extArc = dstFile;
            MemoryStream stdout = Controller.CaptureConsoleOut();
            if (!Catalog.HandleList("list", new string[] { extArc }, parms)) {
                throw new Exception("list '" + extArc + "' failed");
            }
            string[] expected = new string[] {
                "1",
                "2",
                "3",
            };
            Controller.CompareLines(expected, stdout);

            extArc = dstFile + ExtArchive.SPLIT_CHAR + "1" + ExtArchive.SPLIT_CHAR + "small.woz.gz";
            stdout = Controller.CaptureConsoleOut();
            if (!Catalog.HandleList("list", new string[] { extArc }, parms)) {
                throw new Exception("list '" + extArc + "' failed");
            }
            expected = new string[] {
                "hello.txt",
                "again.txt",
            };
            Controller.CompareLines(expected, stdout);

            extArc = dstFile + ExtArchive.SPLIT_CHAR + "2";
            stdout = Controller.CaptureConsoleOut();
            if (!Catalog.HandleList("list", new string[] { extArc }, parms)) {
                throw new Exception("list '" + extArc + "' failed");
            }
            expected = new string[] {
                "PRODOS",
                "BASIC.SYSTEM",
                "DOS.3.3",
            };
            Controller.CompareLines(expected, stdout);
        }
    }
}
