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
using DiskArc.Disk;

namespace cp2.Tests {
    /// <summary>
    /// Tests "copy-sectors".
    /// </summary>
    internal static class TestCopySectors {
        public static void RunTest(ParamsBag parms) {
            Controller.Stdout.WriteLine("  CopySectors...");
            Controller.PrepareTestTmp(parms);

            //
            // Test scenarios:
            // - Copy from .po to .do, confirm order change
            // - Copy from 13-sector NIB to WOZ
            // - Copy into turducken WOZ
            //
            TestOrderChange(parms);
            TestD13(parms);
            TestTurducken(parms);

            Controller.RemoveTestTmp(parms);
        }

        private static void TestOrderChange(ParamsBag parms) {
            string poFile = Path.Join(Controller.TEST_DATA, "prodos", "simple-dir-test.po");
            string doFile = Path.Join(Controller.TEST_TMP, "order-test.do");
            string bigFile = Path.Join(Controller.TEST_TMP, "big-test.po");

            // Create unformatted 140K and 160K (40-track) disks.
            if (!DiskUtil.HandleCreateDiskImage("cdi", new string[] { doFile, "140k" }, parms)) {
                throw new Exception("cdi " + doFile + " failed");
            }
            if (!DiskUtil.HandleCreateDiskImage("cdi", new string[] { bigFile, "40trk" }, parms)) {
                throw new Exception("cdi " + bigFile + " failed");
            }
            if (!DiskUtil.HandleCopySectors("copy-sectors",
                    new string[] { poFile, doFile }, parms)) {
                throw new Exception("copy-sectors " + poFile + " -> " + doFile + " failed");
            }
            // Copying to a different size disk should fail.
            if (DiskUtil.HandleCopySectors("copy-sectors",
                    new string[] { poFile, bigFile }, parms)) {
                throw new Exception("copy-sectors " + poFile + " -> " + bigFile + " succeeded");
            }

            // Confirm the disk ordering and filesystem of the new file.
            using (Stream stream = new FileStream(doFile, FileMode.Open)) {
                using (IDiskImage disk = UnadornedSector.OpenDisk(stream, parms.AppHook)) {
                    disk.AnalyzeDisk();
                    IFileSystem fs = (IFileSystem)disk.Contents!;
                    if (fs.RawAccess.FileOrder != Defs.SectorOrder.DOS_Sector) {
                        throw new Exception("New disk order is wrong");
                    }
                    fs.PrepareFileAccess(true);
                    if (fs.Notes.WarningCount != 0 || fs.Notes.ErrorCount != 0) {
                        throw new Exception("Filesystem didn't scan cleanly");
                    }
                }
            }
        }

        private static void TestD13(ParamsBag parms) {
            string nibFile = Path.Join(Controller.TEST_DATA, "nib", "aw-init.nib");
            string wozFile = Path.Join(Controller.TEST_TMP, "d13-test.woz");

            // Create unformatted 113K WOZ.
            parms.Sectors = 13;
            if (!DiskUtil.HandleCreateDiskImage("cdi", new string[] { wozFile, "35trk" }, parms)) {
                throw new Exception("cdi " + wozFile + " failed");
            }
            parms.Sectors = 16;
            // The aw-init file has lots of uninitialized sectors that will fail to read.  Should
            // copy fully anyway.
            if (!DiskUtil.HandleCopySectors("copy-sectors",
                    new string[] { nibFile, wozFile }, parms)) {
                throw new Exception("copy-sectors " + nibFile + " -> " + wozFile + " failed");
            }

            // Check the copy.
            using (Stream stream = new FileStream(wozFile, FileMode.Open)) {
                using (IDiskImage disk = Woz.OpenDisk(stream, parms.AppHook)) {
                    disk.AnalyzeDisk();
                    IFileSystem fs = (IFileSystem)disk.Contents!;
                    fs.PrepareFileAccess(true);
                    if (fs.Notes.WarningCount != 0 || fs.Notes.ErrorCount != 0) {
                        throw new Exception("Filesystem didn't scan cleanly");
                    }
                }
            }
        }

        private static void TestTurducken(ParamsBag parms) {
            // Make a copy of a turducken file.
            string inputFile = Path.Join(Controller.TEST_DATA, "turducken", "MultiPart.hdv");
            string tdnFile = Path.Join(Controller.TEST_TMP, "tdn-test.hdv");
            FileUtil.CopyFile(inputFile, tdnFile);
            parms.SkipSimple = true;

            string extArc = tdnFile + ExtArchive.SPLIT_CHAR + "2" + ExtArchive.SPLIT_CHAR +
                "small.woz.gz";

            // Copy a minimal DOS 3.3 disk onto the compressed WOZ file in the turducken.
            string srcDisk = Path.Join(Controller.TEST_DATA, "dos33", "minimal.img");
            if (!DiskUtil.HandleCopySectors("copy-sectors",
                    new string[] { srcDisk, extArc }, parms)) {
                throw new Exception("copy-sectors " + srcDisk + " -> " + extArc + " failed");
            }

            // List the contents.
            MemoryStream stdout = Controller.CaptureConsoleOut();
            if (!Catalog.HandleList("list", new string[] { extArc }, parms)) {
                throw new Exception("list '" + extArc + "' failed");
            }
            string[] expected = new string[] {
                "HELLO",
            };
            Controller.CompareLines(expected, stdout);
        }
    }
}
