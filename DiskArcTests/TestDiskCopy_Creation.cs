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
using static DiskArc.IFileSystem;

namespace DiskArcTests {
    public class TestDiskCopy_Creation : ITest {
        private static readonly byte[] SIMPLE_DATA = new byte[] { 1, 2, 3, 4, 5 };
        private const string TEST_DESC_STR = "This is a test!";

        // Test creation of an 800KB ProDOS disk.
        public static void Test800KB(AppHook appHook) {
            MemoryStream diskStream = new MemoryStream();
            using (DiskCopy disk = DiskCopy.CreateDisk(diskStream, MediaKind.GCR_DSDD35, appHook)) {
                using (IFileSystem newFs = new ProDOS(disk.ChunkAccess!, appHook)) {
                    newFs.Format("DC42.800", 0, true);

                    newFs.PrepareFileAccess(true);
                    IFileEntry volDir = newFs.GetVolDirEntry();
                    newFs.CreateFile(volDir, "NEWFILE", CreateMode.File);
                }
            }

            // Modify the file.
            using (DiskCopy disk = DiskCopy.OpenDisk(diskStream, appHook)) {
                disk.AnalyzeDisk();
                IFileSystem fs = (IFileSystem)disk.Contents!;
                Helper.ExpectInt(0, fs.RawAccess.CountUnreadableChunks(), "bad chunks found");
                fs.PrepareFileAccess(true);
                Helper.CheckNotes(fs, 0, 0);

                // Modify the disk.
                IFileEntry volDir = fs.GetVolDirEntry();
                IFileEntry entry = fs.FindFileEntry(volDir, "NEWFILE");
                using (Stream stream = fs.OpenFile(entry, FileAccessMode.ReadWrite,
                        FilePart.DataFork)) {
                    stream.Write(SIMPLE_DATA, 0, SIMPLE_DATA.Length);
                }
            }

            // Modify metadata only.
            using (DiskCopy disk = DiskCopy.OpenDisk(diskStream, appHook)) {
                disk.AnalyzeDisk();
                disk.SetMetaValue(DiskCopy.DESCRIPTION_NAME, TEST_DESC_STR);
            }

            // Verify disk and file contents.
            using (DiskCopy disk = DiskCopy.OpenDisk(diskStream, appHook)) {
                disk.AnalyzeDisk();
                IFileSystem fs = (IFileSystem)disk.Contents!;
                Helper.ExpectInt(0, fs.RawAccess.CountUnreadableChunks(), "bad chunks found");
                fs.PrepareFileAccess(true);
                Helper.CheckNotes(fs, 0, 0);

                IFileEntry volDir = fs.GetVolDirEntry();
                IFileEntry entry = fs.FindFileEntry(volDir, "NEWFILE");
                using (Stream stream = fs.OpenFile(entry, FileAccessMode.ReadOnly,
                        FilePart.DataFork)) {
                    byte[] buf = new byte[stream.Length];
                    stream.ReadExactly(buf, 0, (int)stream.Length);
                    if (stream.Length != SIMPLE_DATA.Length ||
                            !RawData.CompareBytes(buf, SIMPLE_DATA, SIMPLE_DATA.Length)) {
                        throw new Exception("didn't find data written earlier");
                    }
                }

                string? meta = disk.GetMetaValue(DiskCopy.DESCRIPTION_NAME, false);
                Helper.ExpectString(TEST_DESC_STR, meta, "wrong metadata");
            }
        }
    }
}
