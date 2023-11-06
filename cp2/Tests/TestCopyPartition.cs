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
using System;

using CommonUtil;
using DiskArc;
using DiskArc.FS;
using static DiskArc.Defs;

namespace cp2.Tests {
    internal static class TestCopyPartition {
        public static void RunTest(ParamsBag parms) {
            Controller.Stdout.WriteLine("  CopyPartition...");
            Controller.PrepareTestTmp(parms);

            //
            // Extract-partition scenarios:
            // - Copy 140KB DOS partition to .do and .sdk.
            // - Copy 800KB ProDOS partition to .po and .sdk.
            // - Copy 32MB ProDOS partition to .po.
            // - (fail) copy 800KB ProDOS partition to .do.
            //
            // Replace-partition scenarios:
            // - Copy 140KB .do into 140KB area.
            // - Copy 800KB .po into 800KB area.
            // - Copy 800KB .po into 32MB area (requires --overwrite).
            // - Copy 32MB .po into 32MB area.
            // - (fail) copy 32MB partition into 800KB area.
            //

            parms.SkipSimple = true;
            parms.Overwrite = false;

            CreateTestFiles(parms);
            TestExtract(parms);
            TestReplace(parms);

            Controller.RemoveTestTmp(parms);
        }

        private static string TURDUCKEN_SRC_PATH =
            Path.Join(Controller.TEST_DATA, "turducken", "MultiPart.hdv");
        private static string TURDUCKEN_PATH =
            Path.Join(Controller.TEST_TMP, "tdn-test.hdv");
        private static string CFFA_PATH =
            Path.Join(Controller.TEST_TMP, "cffa-test.po");
        private const string PRO_32MB_1 = "Pro32MB.1";
        private const string PRO_32MB_2 = "Pro32MB.2";
        private const string FILENAME = ".FILE";

        private static string OUT140DO = Path.Join(Controller.TEST_TMP, "out-140.do");
        private static string OUT140SDK = Path.Join(Controller.TEST_TMP, "out-140.sdk");
        private static string OUT800PO = Path.Join(Controller.TEST_TMP, "out-800.po");
        private static string OUT800SDK = Path.Join(Controller.TEST_TMP, "out-800.sdk");
        private static string OUT32MPO = Path.Join(Controller.TEST_TMP, "out-32m.po");

        private static readonly string[] FILE_SET_140DO =
            new string[] { "HELLO2" };
        private static readonly string[] FILE_SET_OUT800PO =
            new string[] { "PRODOS", "BASIC.SYSTEM", "DOS.3.3" };
        private static readonly string[] FILE_SET_32MPO =
            new string[] { PRO_32MB_2 + FILENAME };


        // Create some files to use as both inputs and outputs.
        private static void CreateTestFiles(ParamsBag parms) {
            // Use MultiPart.hdv as the source/target of 800KB and 140KB images.
            FileUtil.CopyFile(TURDUCKEN_SRC_PATH, TURDUCKEN_PATH);

            using (FileStream stream = new FileStream(CFFA_PATH, FileMode.CreateNew)) {
                // Allocate space for two 32MB ProDOS volumes.
                stream.SetLength(32 * 1024 * 1024 * 2);

                // Format both volumes so this will be recognized as CFFA.
                IChunkAccess part1 = new GeneralChunkAccess(stream, 0, 65535);
                using (IFileSystem fs = new ProDOS(part1, parms.AppHook)) {
                    fs.Format(PRO_32MB_1, 0, true);
                    fs.PrepareFileAccess(true);
                    fs.CreateFile(fs.GetVolDirEntry(), PRO_32MB_1 + FILENAME,
                        IFileSystem.CreateMode.File);
                }
                IChunkAccess part2 = new GeneralChunkAccess(stream, 65536 * BLOCK_SIZE, 65535);
                using (IFileSystem fs = new ProDOS(part2, parms.AppHook)) {
                    fs.Format(PRO_32MB_2, 0, true);
                    fs.PrepareFileAccess(true);
                    fs.CreateFile(fs.GetVolDirEntry(), PRO_32MB_2 + FILENAME,
                        IFileSystem.CreateMode.File);
                }
            }
        }

        // Test partition extraction.
        private static void TestExtract(ParamsBag parms) {
            TestExtractPartition(TURDUCKEN_PATH + ":1", OUT140DO, false, new string[] { }, parms);

            TestExtractPartition(TURDUCKEN_PATH + ":1:2", OUT140DO, true, FILE_SET_140DO, parms);
            TestExtractPartition(TURDUCKEN_PATH + ":1:2", OUT140SDK, true, FILE_SET_140DO, parms);
            TestExtractPartition(TURDUCKEN_PATH + ":1", OUT800PO, true, FILE_SET_OUT800PO, parms);
            TestExtractPartition(TURDUCKEN_PATH + ":1", OUT800SDK, true, FILE_SET_OUT800PO, parms);
            TestExtractPartition(CFFA_PATH + ":2", OUT32MPO, true, FILE_SET_32MPO, parms);
        }

        private static void TestExtractPartition(string srcExtArc, string dstPath,
                bool expectedResult, string[] expectedFiles, ParamsBag parms) {
            bool result = DiskUtil.HandleExtractPartition("expart",
                new string[] { srcExtArc, dstPath }, parms);
            if (result != expectedResult) {
                throw new Exception("expart " + srcExtArc + " " + dstPath +
                    (expectedResult ? " failed" : " succeeded"));
            }
            if (expectedResult) {
                MemoryStream stdout = Controller.CaptureConsoleOut();
                if (!Catalog.HandleList("list", new string[] { dstPath }, parms)) {
                    throw new Exception("list '" + dstPath + "' failed");
                }
                Controller.CompareLines(expectedFiles, stdout);
            }
        }

        // Test partition replacement.
        private static void TestReplace(ParamsBag parms) {
            TestReplacePartition(OUT32MPO, TURDUCKEN_PATH + ":2", false, new string[] { }, parms);

            TestReplacePartition(OUT140DO, TURDUCKEN_PATH + ":1:4", true, FILE_SET_140DO, parms);
            TestReplacePartition(OUT800PO, TURDUCKEN_PATH + ":2", true, FILE_SET_OUT800PO, parms);
            TestReplacePartition(OUT32MPO, CFFA_PATH + ":1", true, FILE_SET_32MPO, parms);

            parms.Overwrite = false;
            TestReplacePartition(OUT800PO, CFFA_PATH + ":2", false, new string[] { }, parms);
            parms.Overwrite = true;
            TestReplacePartition(OUT800PO, CFFA_PATH + ":2", true, FILE_SET_OUT800PO, parms);
        }

        private static void TestReplacePartition(string srcExtArc, string dstExtArc,
                bool expectedResult, string[] expectedFiles, ParamsBag parms) {
            bool result = DiskUtil.HandleReplacePartition("repart",
                new string[] { srcExtArc, dstExtArc }, parms);
            if (result != expectedResult) {
                throw new Exception("repart " + srcExtArc + " " + dstExtArc +
                    (expectedResult ? " failed" : " succeeded"));
            }
            if (expectedResult) {
                MemoryStream stdout = Controller.CaptureConsoleOut();
                if (!Catalog.HandleList("list", new string[] { dstExtArc }, parms)) {
                    throw new Exception("list '" + dstExtArc + "' failed");
                }
                Controller.CompareLines(expectedFiles, stdout);
            }
        }
    }
}
