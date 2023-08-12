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
using System.Diagnostics;
using System.IO;

using CommonUtil;
using DiskArc;
using static DiskArc.Defs;
using DiskArc.FS;
using static DiskArc.IFileSystem;

namespace DiskArcTests {
    /// <summary>
    /// Test behavior when various problems are encountered.  The disk images are damaged
    /// via the raw data interface.
    /// </summary>
    public class TestPascal_Damage : ITest {
        // Use this disk image as our starting point.  It's known to be problem-free.
        private const string TEST_FILE = "pascal/Apple Pascal1.po";

        // Test handling of overlapping files.
        public static void TestOverlap(AppHook appHook) {
            using (Stream dataFile = Helper.OpenTestFile(TEST_FILE, false, appHook)) {
                using (IDiskImage diskImage = FileAnalyzer.PrepareDiskImage(dataFile,
                        FileKind.UnadornedSector, appHook)!) {
                    diskImage.AnalyzeDisk();
                    IFileSystem fs = (IFileSystem)diskImage.Contents!;

                    byte[] buf = new byte[BLOCK_SIZE];
                    fs.RawAccess.ReadBlock(Pascal.VOL_DIR_START_BLOCK, buf, 0);

                    // Disk is crunched, so just add one to an existing file's "next block" value.
                    int offset = Pascal_FileEntry.DIR_ENTRY_LEN * 2;
                    ushort nextBlock = RawData.GetU16LE(buf, offset + 2);
                    nextBlock++;
                    RawData.SetU16LE(buf, offset + 2, nextBlock);

                    fs.RawAccess.WriteBlock(Pascal.VOL_DIR_START_BLOCK, buf, 0);
                    fs.PrepareFileAccess(true);
                    // Currently 3 warnings: 1 for each overlapping file, 1 block conflict summary.
                    Helper.CheckNotes(fs, 3, 0);

                    foreach (IFileEntry entry in fs.GetVolDirEntry()) {
                        bool overlap = (entry.FileName == "SYSTEM.PASCAL" ||
                                entry.FileName == "SYSTEM.MISCINFO");
                        if (entry.IsDamaged) {
                            throw new Exception("shouldn't be damaged");
                        }
                        if (overlap) {
                            if (!entry.IsDubious) {
                                throw new Exception("should be dubious: " + entry.FileName);
                            }
                        } else {
                            if (entry.IsDubious) {
                                throw new Exception("should not be dubious: " + entry.FileName);
                            }
                        }
                    }

                    IFileEntry volDir = fs.GetVolDirEntry();
                }
            }
        }

        // Test files not in sorted order.
        public static void TestWrongOrder(AppHook appHook) {
            using (Stream dataFile = Helper.OpenTestFile(TEST_FILE, false, appHook)) {
                using (IDiskImage diskImage = FileAnalyzer.PrepareDiskImage(dataFile,
                        FileKind.UnadornedSector, appHook)!) {
                    diskImage.AnalyzeDisk();
                    IFileSystem fs = (IFileSystem)diskImage.Contents!;

                    byte[] buf = new byte[BLOCK_SIZE];
                    fs.RawAccess.ReadBlock(Pascal.VOL_DIR_START_BLOCK, buf, 0);

                    int offset1 = Pascal_FileEntry.DIR_ENTRY_LEN * 2;
                    ushort startBlock1 = RawData.GetU16LE(buf, offset1);
                    ushort nextBlock1 = RawData.GetU16LE(buf, offset1 + 2);
                    int offset2 = Pascal_FileEntry.DIR_ENTRY_LEN * 3;
                    ushort startBlock2 = RawData.GetU16LE(buf, offset2);
                    ushort nextBlock2 = RawData.GetU16LE(buf, offset2 + 2);
                    // swap
                    RawData.SetU16LE(buf, offset1, startBlock2);
                    RawData.SetU16LE(buf, offset1 + 2, nextBlock2);
                    RawData.SetU16LE(buf, offset2, startBlock1);
                    RawData.SetU16LE(buf, offset2 + 2, nextBlock1);

                    fs.RawAccess.WriteBlock(Pascal.VOL_DIR_START_BLOCK, buf, 0);
                    fs.PrepareFileAccess(true);
                    Helper.CheckNotes(fs, 0, 1);
                    if (!fs.IsDubious) {
                        throw new Exception("filesystem should be dubious");
                    }
                }
            }
        }

        // Test file with "next" before "start".  We allow start==next.
        public static void TestBackwardStorage(AppHook appHook) {
            using (Stream dataFile = Helper.OpenTestFile(TEST_FILE, false, appHook)) {
                using (IDiskImage diskImage = FileAnalyzer.PrepareDiskImage(dataFile,
                        FileKind.UnadornedSector, appHook)!) {
                    diskImage.AnalyzeDisk();
                    IFileSystem fs = (IFileSystem)diskImage.Contents!;

                    byte[] buf = new byte[BLOCK_SIZE];
                    fs.RawAccess.ReadBlock(Pascal.VOL_DIR_START_BLOCK, buf, 0);

                    int offset = Pascal_FileEntry.DIR_ENTRY_LEN * 3;
                    ushort startBlock = RawData.GetU16LE(buf, offset);
                    RawData.SetU16LE(buf, offset + 2, (ushort)(startBlock - 1));

                    fs.RawAccess.WriteBlock(Pascal.VOL_DIR_START_BLOCK, buf, 0);
                    fs.PrepareFileAccess(true);
                    Helper.CheckNotes(fs, 1, 0);
                    if (!fs.IsDubious) {
                        throw new Exception("filesystem should be dubious");
                    }
                    IFileEntry entry = fs.EntryFromPath("SYSTEM.MISCINFO", IFileEntry.NO_DIR_SEP);
                    Helper.ExpectBool(true, entry.IsDamaged, "file should be marked damaged");
                }
            }
        }

        // Test directory with fewer files than expected.
        public static void TestShortDir(AppHook appHook) {
            using (Stream dataFile = Helper.OpenTestFile(TEST_FILE, false, appHook)) {
                using (IDiskImage diskImage = FileAnalyzer.PrepareDiskImage(dataFile,
                        FileKind.UnadornedSector, appHook)!) {
                    diskImage.AnalyzeDisk();
                    IFileSystem fs = (IFileSystem)diskImage.Contents!;

                    byte[] buf = new byte[BLOCK_SIZE];
                    fs.RawAccess.ReadBlock(Pascal.VOL_DIR_START_BLOCK, buf, 0);

                    ushort fileCount = RawData.GetU16LE(buf, 0x10);
                    fileCount++;
                    RawData.SetU16LE(buf, 0x10, fileCount);

                    fs.RawAccess.WriteBlock(Pascal.VOL_DIR_START_BLOCK, buf, 0);
                    fs.PrepareFileAccess(true);
                    Helper.CheckNotes(fs, 1, 0);
                    if (!fs.IsDubious) {
                        throw new Exception("filesystem should be dubious");
                    }
                }
            }
        }

        // Test directory with more files than expected.
        public static void TestLongDir(AppHook appHook) {
            using (Stream dataFile = Helper.OpenTestFile(TEST_FILE, false, appHook)) {
                using (IDiskImage diskImage = FileAnalyzer.PrepareDiskImage(dataFile,
                        FileKind.UnadornedSector, appHook)!) {
                    diskImage.AnalyzeDisk();
                    IFileSystem fs = (IFileSystem)diskImage.Contents!;

                    byte[] buf = new byte[BLOCK_SIZE];
                    fs.RawAccess.ReadBlock(Pascal.VOL_DIR_START_BLOCK, buf, 0);

                    ushort fileCount = RawData.GetU16LE(buf, 0x10);
                    fileCount--;
                    RawData.SetU16LE(buf, 0x10, fileCount);

                    fs.RawAccess.WriteBlock(Pascal.VOL_DIR_START_BLOCK, buf, 0);
                    fs.PrepareFileAccess(true);
                    // This isn't an error, as some otherwise healthy disks look this way.
                    Helper.CheckNotes(fs, 0, 0);
                    if (fs.IsDubious) {
                        throw new Exception("filesystem should not be dubious");
                    }
                }
            }
        }
    }
}
