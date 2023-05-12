/*
 * Copyright 2022 faddenSoft
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
using static DiskArc.Defs;
using static DiskArc.IFileSystem;

namespace DiskArcTests {
    /// <summary>
    /// Tests a few .WOZ files to ensure we can successfully open images created by others.
    /// </summary>
    public class TestWoz_Prefab : ITest {
        // Open images, check file count.  The deep scan performed by DOS and ProDOS will touch
        // a lot of the disk, and a bad-block scan will help spot any nibble decoding issues.
        public static void TestVarious(AppHook appHook) {
            // Basic DOS 3.2/3.3 in WOZ1 and WOZ2.
            Helper.SimpleDiskCheck("woz/dos33master_1.woz", FileKind.Woz, 19, appHook);
            Helper.SimpleDiskCheck("woz/dos33master_2.woz", FileKind.Woz, 19, appHook);
            Helper.SimpleDiskCheck("woz/dos32master_2.woz", FileKind.Woz, 14, appHook);

            // ProDOS on a 3.5" disk.
            Helper.SimpleDiskCheck("woz/iigs-system-35.woz", FileKind.Woz, 13, appHook);

            // Flux encoding.
            Helper.SimpleDiskCheck("woz/prodos-flux.woz", FileKind.Woz, 8, appHook);
        }

        // Test writing to an 800KB disk created by another program.
        public static void TestWrite800KB(AppHook appHook) {
            // Open read/write (creates a copy in memory).
            using (Stream dataFile = Helper.OpenTestFile("woz/iigs-system-35.woz", false,
                    appHook)) {
                using (IDiskImage diskImage = FileAnalyzer.PrepareDiskImage(dataFile,
                        FileKind.Woz, appHook)!) {
                    diskImage.AnalyzeDisk();
                    IFileSystem fs = (IFileSystem)diskImage.Contents!;
                    fs.PrepareFileAccess(true);
                    Helper.CheckNotes(fs, 0, 0);

                    // Write a file and read it back, to make sure we're not trashing things.
                    IFileEntry volDir = fs.GetVolDirEntry();
                    IFileEntry newEntry = fs.CreateFile(volDir, "TEST", CreateMode.File);
                    newEntry.FileType = FileAttribs.FILE_TYPE_BIN;
                    using (DiskFileStream fileStream =
                            fs.OpenFile(newEntry, FileAccessMode.ReadWrite, FilePart.DataFork)) {
                        // Write a byte pattern that uses all 256 values.
                        fileStream.Write(Patterns.sRunPattern, 0, Patterns.sRunPattern.Length);
                    }

                    fs.PrepareRawAccess();
                    fs.PrepareFileAccess(true);
                    Helper.CheckNotes(fs, 0, 0);

                    volDir = fs.GetVolDirEntry();
                    IFileEntry entry = fs.FindFileEntry(volDir, "TEST");
                    byte[] buffer = new byte[entry.DataLength];
                    using (DiskFileStream fileStream =
                            fs.OpenFile(entry, FileAccessMode.ReadOnly, FilePart.DataFork)) {
                        fileStream.ReadExactly(buffer, 0, (int)entry.DataLength);
                    }
                    if (!RawData.CompareBytes(Patterns.sRunPattern, buffer, buffer.Length)) {
                        throw new Exception("Data mismatch");
                    }
                }
            }
        }

        // Test writing to a WOZ1.  It should either not upgrade to WOZ2, or do so cleanly (by
        // rewriting TMAP and TRKS).
        public static void TestNoUpgrade(AppHook appHook) {
            using (Stream dataFile = Helper.OpenTestFile("woz/dos33master_1.woz", false, appHook)) {
                using (Woz diskImage = Woz.OpenDisk(dataFile, appHook)) {
                    if (diskImage.FileRevision != Woz.WozKind.Woz1) {
                        throw new Exception("Need a WOZ1 for this test");
                    }
                    diskImage.AnalyzeDisk();
                    IFileSystem fs = (IFileSystem)diskImage.Contents!;
                    fs.PrepareFileAccess(true);
                    Helper.CheckNotes(fs, 0, 0);

                    // Modify the disk in some way.
                    IFileEntry volDir = fs.GetVolDirEntry();
                    fs.CreateFile(volDir, "TEST.FILE", CreateMode.File);
                }

                // Reopen the disk, make sure the filesystem is still there.
                using (Woz diskImage = Woz.OpenDisk(dataFile, appHook)) {
                    if (diskImage.FileRevision != Woz.WozKind.Woz1) {
                        throw new Exception("Disk image was upgraded");
                    }
                    diskImage.AnalyzeDisk();
                    IFileSystem? fs = diskImage.Contents as IFileSystem;
                    if (fs == null) {
                        throw new Exception("Unable to find filesystem on updated WOZ");
                    }
                    fs.PrepareFileAccess(true);
                    Helper.CheckNotes(fs, 0, 0);
                }
            }
        }
    }
}
