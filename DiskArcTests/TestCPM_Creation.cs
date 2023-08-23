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
using System.Text;

using CommonUtil;
using DiskArc;
using DiskArc.Disk;
using DiskArc.FS;
using static DiskArc.Defs;
using static DiskArc.IFileSystem;

namespace DiskArcTests {
    /// <summary>
    /// Basic volume and file creation tests.
    /// </summary>
    public class TestCPM_Creation : ITest {
        public static void TestCreateVol(AppHook appHook) {
            const int DIR_LEN_525 = 2048;       // 1KB * 2
            const int DIR_LEN_35 = 8192;        // 2KB * 4

            // 140KB floppy, with boot area reserved.
            using (IFileSystem fs = Helper.CreateTestImage(string.Empty, FileSystemType.CPM,
                    35, 16, 1, true, appHook, out MemoryStream memFile)) {
                // 1 warning for reserved-space file (because of wrap-around).
                if (fs.Notes.WarningCount != 1 || fs.Notes.ErrorCount != 0) {
                    throw new Exception("Source image isn't clean: " + fs.Notes.ToString());
                }

                // Subtract the three tracks and the 5.25" directory.
                if (fs.FreeSpace != (35 - 3) * 16 * SECTOR_SIZE - DIR_LEN_525) {
                    throw new Exception("Incorrect free space #1: " + fs.FreeSpace);
                }
            }

            // 140KB floppy, no reserved space.
            using (IFileSystem fs = Helper.CreateTestImage(string.Empty, FileSystemType.CPM,
                    35, 16, 1, false, appHook, out MemoryStream memFile)) {
                // No reserved-space file.
                if (fs.Notes.WarningCount != 0 || fs.Notes.ErrorCount != 0) {
                    throw new Exception("Source image isn't clean: " + fs.Notes.ToString());
                }

                if (fs.FreeSpace != 35 * 16 * SECTOR_SIZE - DIR_LEN_525) {
                    throw new Exception("Incorrect free space #2: " + fs.FreeSpace);
                }
            }

            // 800KB floppy.
            using (IFileSystem fs = Helper.CreateTestImage(string.Empty, FileSystemType.CPM,
                    1600, appHook, out MemoryStream memFile)) {
                // System space is present but disk doesn't wrap around, so no reserved-space file.
                if (fs.Notes.WarningCount != 0 || fs.Notes.ErrorCount != 0) {
                    throw new Exception("Source image isn't clean: " + fs.Notes.ToString());
                }

                // 32 blocks are reserved for OS area.
                if (fs.FreeSpace != (1600 - 32) * BLOCK_SIZE - DIR_LEN_35) {
                    throw new Exception("Incorrect free space #3: " + fs.FreeSpace);
                }
            }
        }

        public static void TestCreateUserFiles(AppHook appHook) {
            string[] name = new string[CPM.MAX_USER_NUM + 1];
            byte[][] contents = new byte[CPM.MAX_USER_NUM + 1][];

            using (IFileSystem fs = Helper.CreateTestImage(string.Empty, FileSystemType.CPM,
                    280, appHook, out MemoryStream memFile)) {
                IFileEntry volDir = fs.GetVolDirEntry();
                for (int i = 0; i <= CPM.MAX_USER_NUM; i++) {
                    name[i] = "USER" + i + ".TXT";
                    IFileEntry newFile = fs.CreateFile(volDir, name[i], CreateMode.File);
                    ((CPM_FileEntry)newFile).UserNumber = (byte)i;
                    if (i != 0) {
                        name[i] += "," + i;
                    }

                    contents[i] =
                        Encoding.ASCII.GetBytes("Hello, world!\r\nUser #" + i + "\r\n\x1a");
                    using (Stream stream = fs.OpenFile(newFile,
                            FileAccessMode.ReadWrite, FilePart.DataFork)) {
                        stream.Write(contents[i]);
                    }
                }
                //fs.DumpToFile(@"C:\src\CiderPress2\cpm-users.co");

                fs.PrepareRawAccess();
                fs.PrepareFileAccess(true);
                volDir = fs.GetVolDirEntry();
                byte[] buf = new byte[128];
                for (int i = 0; i <= CPM.MAX_USER_NUM; i++) {
                    IFileEntry file = fs.FindFileEntry(volDir, name[i]);
                    if (((CPM_FileEntry)file).UserNumber != i) {
                        throw new Exception("User number mismatch on " + name[i]);
                    }

                    using (Stream stream = fs.OpenFile(file, FileAccessMode.ReadOnly,
                            FilePart.DataFork)) {
                        int actual = stream.Read(buf, 0, (int)stream.Length);
                        if (actual != contents[i].Length ||
                                !RawData.CompareBytes(buf, contents[i], actual)) {
                            throw new Exception("Content mismatch on " + name[i]);
                        }
                    }
                }
            }
        }
    }
}
