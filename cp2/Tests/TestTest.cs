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
using DiskArc.FS;
using static DiskArc.Defs;

namespace cp2.Tests {
    /// <summary>
    /// Tests the "test" command.
    /// </summary>
    internal static class TestTest {
        public static void RunTest(ParamsBag parms) {
            Controller.Stdout.WriteLine("  Test...");
            Controller.PrepareTestTmp(parms);

            TestDOS321(parms);
            TestDamage(parms);
            TestTurducken(parms);

            Controller.RemoveTestTmp(parms);
        }

        // Confirm that we don't report an error for newly-formatted DOS 3.2.1 disks.
        private static void TestDOS321(ParamsBag parms) {
            string testFile = Path.Join(Controller.TEST_DATA, "nib", "aw-init.nib");
            if (!Test.HandleTest("test", new string[] { testFile, }, parms)) {
                throw new Exception("test " + testFile + " failed");
            }
        }

        private static void TestDamage(ParamsBag parms) {
            string inputFile = Path.Join(Controller.TEST_DATA, "dos33", "system-master-1983.po");
            string testFile = Path.Join(Controller.TEST_TMP, "damage-test.po");
            FileUtil.CopyFile(inputFile, testFile);

            // Test disk image; should succeed.
            if (!Test.HandleTest("test", new string[] { testFile, }, parms)) {
                throw new Exception("test " + testFile + " failed");
            }

            // Damage the filesystem.
            using (Stream stream = new FileStream(testFile, FileMode.Open)) {
                using (IDiskImage disk = UnadornedSector.OpenDisk(stream, parms.AppHook)) {
                    disk.AnalyzeDisk();
                    IFileSystem fs = (IFileSystem)disk.Contents!;
                    IChunkAccess chunks = fs.RawAccess;
                    byte[] sectorBuf = new byte[SECTOR_SIZE];
                    // Read the first catalog sector, damage the T/S value for the 2nd entry.
                    chunks.ReadSector(DOS.VTOC_TRACK, 15, sectorBuf, 0);
                    sectorBuf[0x2e] = 0xee;
                    chunks.WriteSector(DOS.VTOC_TRACK, 15, sectorBuf, 0);
                }
            }

            // Test disk image; should fail.
            if (Test.HandleTest("test", new string[] { testFile, }, parms)) {
                throw new Exception("test " + testFile + " succeeded");
            }
        }

        private static void TestTurducken(ParamsBag parms) {
            // Make a copy of a turducken file.
            string inputFile = Path.Join(Controller.TEST_DATA, "turducken", "MultiPart.hdv");
            string tdnFile = Path.Join(Controller.TEST_TMP, "tdn-test.hdv");
            FileUtil.CopyFile(inputFile, tdnFile);
            parms.SkipSimple = true;

            // Test a disk image.
            string extArc = tdnFile + ExtArchive.SPLIT_CHAR + "2" + ExtArchive.SPLIT_CHAR +
                "small.woz.gz";

            if (!Test.HandleTest("test", new string[] { extArc, }, parms)) {
                throw new Exception("test " + extArc + " failed");
            }

            // Repeat the procedure with a ZIP archive.
            extArc = tdnFile + ExtArchive.SPLIT_CHAR + "2" + ExtArchive.SPLIT_CHAR +
                "Some.Files.zip";
            if (!Test.HandleTest("test", new string[] { extArc, }, parms)) {
                throw new Exception("test " + extArc + " failed");
            }
        }
    }
}
