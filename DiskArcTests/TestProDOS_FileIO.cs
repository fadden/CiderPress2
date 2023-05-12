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
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

using CommonUtil;
using DiskArc;
using static DiskArc.Defs;
using static DiskArc.IFileSystem;

namespace DiskArcTests {
    public class TestProDOS_FileIO : ITest {
        private const int GRINDER_ITERATIONS = 500;
        private const bool DO_SCAN = true;

        //
        // Some constants for full 32MB volumes.
        //

        // Volume size and OS usage.
        private const int VOLUME_SIZE = 65535;                  // 32MB, minus one block
        private const int FS_OVERHEAD = 2 + 4 + 16;             // 22 blocks of overhead on 32MB vol
        // Fullest possible file fork.
        private const int FULL_FORK_LEN = 256 * 256 * 256 - 1;  // in bytes; data blocks only
        private const int FULL_FORK_OVHD = 128 + 1;             // index blocks + master index
        private const int FULL_FORK_BLOCKS = 128 * 256 + FULL_FORK_OVHD;

        public static void TestGrind(AppHook appHook) {
            const int SEED = 12345;

            using (IFileSystem fs = Helper.CreateTestImage("FileGrounds",
                    FileSystemType.ProDOS, 65535, appHook, out MemoryStream memFile)) {
                Grinder grinder = new Grinder(fs, SEED);
                grinder.Execute(GRINDER_ITERATIONS);

                fs.PrepareRawAccess();
                //fs.DumpToFile(Helper.DebugFileDir + "check-grind.po");
                fs.PrepareFileAccess(DO_SCAN);
                Helper.CheckNotes(fs, 0, 0);
            }
        }

        public static void TestSimpleWrite(AppHook appHook) {
            using (IFileSystem fs = Make525Floppy("Create.Test", appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();

                byte[] wrBuf = new byte[1024];
                for (int i = 0; i < wrBuf.Length; i++) {
                    wrBuf[i] = (byte)i;
                }

                // Make a short (1024-byte) file.
                string name = "FOO";
                IFileEntry entry = fs.CreateFile(volDir, name, CreateMode.File);
                DiskFileStream fd = fs.OpenFile(entry, FileAccessMode.ReadWrite,
                    FilePart.DataFork);
                fd.Write(wrBuf, 0, wrBuf.Length);
                fd.Close();     // explicit close

                if (entry.DataLength != wrBuf.Length || entry.StorageSize != 3 * BLOCK_SIZE) {
                    throw new Exception("bad len/size #1: " + entry.DataLength + "/" +
                        entry.StorageSize);
                }

                fs.PrepareRawAccess();
                fs.PrepareFileAccess(DO_SCAN);
                Helper.CheckNotes(fs, 0, 0);

                volDir = fs.GetVolDirEntry();
                entry = fs.FindFileEntry(volDir, name);

                // Read data back, compare to original.
                byte[] rdBuf = new byte[wrBuf.Length];
                using (fd = fs.OpenFile(entry, FileAccessMode.ReadOnly,
                        FilePart.DataFork)) {
                    fd.Read(rdBuf, 0, rdBuf.Length);
                    if (!RawData.CompareBytes(wrBuf, rdBuf, rdBuf.Length)) {
                        throw new Exception("Read data doesn't match written");
                    }
                }

                // Expand file to 16MB tree, sparse, by writing a single byte at the very end.
                fd = fs.OpenFile(entry, FileAccessMode.ReadWrite, FilePart.DataFork);
                using (fd) {
                    fd.Seek(256 * 256 * 256 - 2, SeekOrigin.Begin);
                    fd.Write(wrBuf, 4, 1);        // write $04
                    try {
                        // Try to write past max size.
                        fd.Write(wrBuf, 4, 1);
                        throw new Exception("Failed to fail");
                    } catch (IOException) {
                        // expected
                    }
                    // let "using" statement do the close
                }

                // Should be 6 blocks: master index, index for first two blocks, index for
                // last block, and three data blocks.
                if (entry.DataLength != 256*256*256-1 || entry.StorageSize != 6 * BLOCK_SIZE) {
                    throw new Exception("bad len/size #2: " + entry.DataLength + "/" +
                        entry.StorageSize);
                }

                fs.PrepareRawAccess();
                fs.PrepareFileAccess(DO_SCAN);
                Helper.CheckNotes(fs, 0, 0);

                // Read data back, compare to original.
                volDir = fs.GetVolDirEntry();
                entry = fs.FindFileEntry(volDir, name);
                fd = fs.OpenFile(entry, FileAccessMode.ReadWrite, FilePart.DataFork);
                using (fd) {
                    fd.Seek(-1, SeekOrigin.End);
                    fd.Read(rdBuf, 0, 1);
                    if (rdBuf[0] != 0x04) {
                        throw new Exception("Read byte doesn't match written (got $" +
                            rdBuf[0].ToString("x2") + ")");
                    }

                    fd.Seek(-1, SeekOrigin.End);
                    if (fd.Read(rdBuf, 0, 2) != 1) {
                        throw new Exception("Read two bytes, should only have read one");
                    }

                    fd.Seek(0, SeekOrigin.End);   // should work
                    try {
                        fd.Seek(1, SeekOrigin.End);   // should fail
                        throw new Exception("failed to fail");
                    } catch (IOException) {
                        // expected
                    }

                    fd.Close();     // explicit close, to be followed by "using" close
                }
            }
        }

        public static void TestSparsify(AppHook appHook) {
            // If we try to fill up a second fork, we will fall short because the volume
            // isn't quite big enough.  Compute the maximum number of blocks by taking the
            // total volume size, subtracting the size of the data fork, and subtracting
            // one more for the extended info block.
            const int MAX_FILE_BLOCKS = VOLUME_SIZE - FS_OVERHEAD;
            const int MAX_RSRC_BLOCKS = MAX_FILE_BLOCKS - FULL_FORK_BLOCKS - 1;     // 32615
            // We need to figure out how many of those blocks will hold data.  If the file
            // were full we'd need 1 for the master index block and 128 for the index blocks,
            // leaving 32615-128-1=32486.  However, that much data doesn't require the last
            // index block, so we actually fill 32615-127-1=32487 data blocks.
            const int MAX_RSRC_DATA_BLOCKS = MAX_RSRC_BLOCKS - (FULL_FORK_OVHD - 1);

            using (IFileSystem fs = MakeBigHD("BigHD", appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();

                // Create the largest possible data fork.
                string name = "FOO";
                IFileEntry entry = fs.CreateFile(volDir, name, CreateMode.Extended);
                DiskFileStream fd = fs.OpenFile(entry, FileAccessMode.ReadWrite, FilePart.DataFork);
                FillFileBlocks(fd, true);
                fd.Close();

                // Try to do the same for the resource fork.
                fd = fs.OpenFile(entry, FileAccessMode.ReadWrite, FilePart.RsrcFork);
                try {
                    FillFileBlocks(fd, true);
                    throw new Exception("Should not have fit");
                } catch (DiskFullException) {
                    // expected
                    // len d=16777215 / r=16633344 / bu=65513
                }
                fd.Close();

                if (entry.DataLength != FULL_FORK_LEN ||
                        entry.RsrcLength != MAX_RSRC_DATA_BLOCKS * BLOCK_SIZE ||
                        entry.StorageSize != MAX_FILE_BLOCKS * BLOCK_SIZE) {
                    // actual 16777215/16633344/33542656
                    throw new Exception("bad full len/size #1: " + entry.DataLength + "/" +
                        entry.RsrcLength + "/" + entry.StorageSize);
                }

                // See if it came out okay.
                fs.PrepareRawAccess();
                fs.PrepareFileAccess(DO_SCAN);
                Helper.CheckNotes(fs, 0, 0);

                // Reopen files.
                volDir = fs.GetVolDirEntry();
                entry = fs.FindFileEntry(volDir, name);

                // Make sure these came back the right way.
                if (entry.DataLength != FULL_FORK_LEN ||
                        entry.RsrcLength != MAX_RSRC_DATA_BLOCKS * BLOCK_SIZE ||
                        entry.StorageSize != MAX_FILE_BLOCKS * BLOCK_SIZE) {
                    throw new Exception("bad full len/size #2: " + entry.DataLength + "/" +
                        entry.RsrcLength + "/" + entry.StorageSize);
                }

                // Check the file contents to see if they match.  When that's done, fill the
                // file with zeroes.  This should cause most of the data and
                // index blocks to disappear, but the storage type will still be Tree.
                fd = fs.OpenFile(entry, FileAccessMode.ReadWrite, FilePart.DataFork);
                CheckFileBlocks(fd, true);
                fd.Seek(0, SeekOrigin.Begin);
                FillFileBlocks(fd, false);
                fd.Close();

                fd = fs.OpenFile(entry, FileAccessMode.ReadWrite, FilePart.RsrcFork);
                CheckFileBlocks(fd, true);
                fd.Seek(0, SeekOrigin.Begin);
                FillFileBlocks(fd, false);
                fd.Close();

                // We should have two tree forks, each of which is master + index + data,
                // plus one for ext info, should be 7 blocks.
                if (entry.DataLength != FULL_FORK_LEN ||
                        entry.RsrcLength != FULL_FORK_LEN ||
                        entry.StorageSize != 7 * BLOCK_SIZE) {
                    throw new Exception("bad sparse len/size: " + entry.DataLength + "/" +
                        entry.RsrcLength + "/" + entry.StorageSize);
                }

                // Confirm it's all zeroes.
                fd = fs.OpenFile(entry, FileAccessMode.ReadWrite, FilePart.DataFork);
                CheckFileBlocks(fd, false);
                fd.Close();
                fd = fs.OpenFile(entry, FileAccessMode.ReadWrite, FilePart.RsrcFork);
                CheckFileSingle(fd, false);
                fd.Close();

                // See if the sparsed versions look right.
                fs.PrepareRawAccess();
                fs.PrepareFileAccess(DO_SCAN);
                Helper.CheckNotes(fs, 0, 0);
            }
        }

        public static void TestSparsifyFlush(AppHook appHook) {
            using (IFileSystem fs = MakeBigHD("BigHD", appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();

                // Create the largest possible data fork.
                string name = "BAR";
                IFileEntry entry = fs.CreateFile(volDir, name, CreateMode.File);
                DiskFileStream fd = fs.OpenFile(entry, FileAccessMode.ReadWrite, FilePart.DataFork);
                FillFileSingle(fd, true);
                fd.Flush();

                if (entry.DataLength != FULL_FORK_LEN ||
                        entry.StorageSize != FULL_FORK_BLOCKS * BLOCK_SIZE) {
                    // 0x8101 (33025)
                    throw new Exception("bad full len/size #1: " + entry.DataLength + "/" +
                        entry.StorageSize);
                }

                // Now zero it out without closing it first.
                fd.Seek(0, SeekOrigin.Begin);
                FillFileSingle(fd, false);
                fd.Flush();

                if (entry.DataLength != FULL_FORK_LEN || entry.StorageSize != 3 * BLOCK_SIZE) {
                    throw new Exception("bad full len/size #2: " + entry.DataLength + "/" +
                        entry.StorageSize);
                }

                // Now fill it up again.
                fd.Seek(0, SeekOrigin.Begin);
                FillFileSingle(fd, true);
                fd.Flush();

                if (entry.DataLength != FULL_FORK_LEN ||
                        entry.StorageSize != FULL_FORK_BLOCKS * BLOCK_SIZE) {
                    throw new Exception("bad full len/size #3: " + entry.DataLength + "/" +
                        entry.StorageSize);
                }

                try {
                    // Try to switch to raw mode before closing the file.
                    fs.PrepareRawAccess();
                    throw new Exception("failed to fail");
                } catch (DAException) {
                    // expected
                }

                fd.Close();

                // Check the filesystem.
                fs.PrepareRawAccess();
                fs.PrepareFileAccess(DO_SCAN);
                Helper.CheckNotes(fs, 0, 0);
            }
        }

        // Make sure this stuff works for the sapling case.
        public static void TestSparsifySapling(AppHook appHook) {
            const int MAX_SAPLING = 256 * BLOCK_SIZE;       // 128KB

            using (IFileSystem fs = Make525Floppy("Medium.Sparse", appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();

                // Create the largest possible data fork.
                string name = "MED";
                IFileEntry entry = fs.CreateFile(volDir, name, CreateMode.File);
                DiskFileStream fd = fs.OpenFile(entry, FileAccessMode.ReadWrite, FilePart.DataFork);
                fd.Write(sFullPattern, 0, MAX_SAPLING);
                fd.Flush();

                if (entry.DataLength != MAX_SAPLING || entry.StorageSize != 257 * BLOCK_SIZE) {
                    throw new Exception("bad sapling len/size #1: " + entry.DataLength + "/" +
                        entry.StorageSize);
                }

                fd.Seek(0, SeekOrigin.Begin);
                fd.Write(sFullZero, 0, MAX_SAPLING);
                fd.Flush();

                if (entry.DataLength != MAX_SAPLING || entry.StorageSize != 2 * BLOCK_SIZE) {
                    throw new Exception("bad sapling len/size #2: " + entry.DataLength + "/" +
                        entry.StorageSize);
                }

                fd.Close();

                // Check the filesystem.
                fs.PrepareRawAccess();
                fs.PrepareFileAccess(DO_SCAN);
                Helper.CheckNotes(fs, 0, 0);
            }
        }

        public static void TestTruncate(AppHook appHook) {
            const int MAX_SEEDLING = BLOCK_SIZE;
            const int MAX_SAPLING = 256 * BLOCK_SIZE;       // 128KB
            const int MIN_TREE = MAX_SAPLING + 3;           // need a nonzero byte in last block

            using (IFileSystem fs = Make525Floppy("Trunc.Test", appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();

                // Create the largest possible data fork.
                string name = "TRU";
                IFileEntry entry = fs.CreateFile(volDir, name, CreateMode.File);
                DiskFileStream fd = fs.OpenFile(entry, FileAccessMode.ReadWrite, FilePart.DataFork);
                fd.Write(sFullPattern, 0, MIN_TREE);
                fd.Flush();

                // 257 data blocks, 2 index blocks, 1 master
                if (entry.DataLength != MIN_TREE || entry.StorageSize != 260 * BLOCK_SIZE) {
                    throw new Exception("bad trunc len/size #1: " + entry.DataLength + "/" +
                        entry.StorageSize);
                }

                fd.SetLength(MIN_TREE - 1);    // should do nothing
                if (entry.DataLength != MIN_TREE - 1 || entry.StorageSize != 260 * BLOCK_SIZE) {
                    throw new Exception("bad trunc len/size #1a: " + entry.DataLength + "/" +
                        entry.StorageSize);
                }

                fd.SetLength(MAX_SAPLING);     // downsize to sapling
                if (entry.DataLength != MAX_SAPLING || entry.StorageSize != 257 * BLOCK_SIZE) {
                    throw new Exception("bad trunc len/size #2: " + entry.DataLength + "/" +
                        entry.StorageSize);
                }

                fd.SetLength(MAX_SEEDLING);    // downsize to seedling
                if (entry.DataLength != MAX_SEEDLING || entry.StorageSize != 1 * BLOCK_SIZE) {
                    throw new Exception("bad trunc len/size #3: " + entry.DataLength + "/" +
                        entry.StorageSize);
                }

                fd.Seek(0, SeekOrigin.Begin);
                fd.Write(sFullPattern, 0, MIN_TREE);    // fill it up...
                fd.SetLength(0);                           // ...full truncation
                if (entry.DataLength != 0 || entry.StorageSize != 1 * BLOCK_SIZE) {
                    throw new Exception("bad trunc len/size #4: " + entry.DataLength + "/" +
                        entry.StorageSize);
                }
                fd.Close();

                // Check the filesystem.
                fs.PrepareRawAccess();
                fs.PrepareFileAccess(DO_SCAN);
                Helper.CheckNotes(fs, 0, 0);

                volDir = fs.GetVolDirEntry();
                IFileEntry entry1 = fs.CreateFile(volDir, "Expand", CreateMode.File);
                using (DiskFileStream fd1 = fs.OpenFile(entry1, FileAccessMode.ReadWrite,
                        FilePart.DataFork)) {
                    fd1.Write(sFullPattern, 0, 65536);      // write 64KB
                    fd1.SetLength(262144);                     // set EOF to 256KB
                    fd1.Flush();

                    // Length should be extended, but storage size still 64KB.
                    if (entry1.DataLength != 262144 ||
                            entry1.StorageSize != (128 + 1) * BLOCK_SIZE) {
                        throw new Exception("Bad SetLength expansion");
                    }

                    // Truncate and start over.  Verify that the mark doesn't change, and that
                    // the intervening blocks are sparse.
                    fd1.SetLength(0);
                    fd1.Seek(0, SeekOrigin.Begin);
                    fd1.Write(sFullPattern, 0, 65536);      // write 64KB
                    fd1.SetLength(16384);                      // cut to 16KB
                    byte[] nonZeroByte = new byte[] { 0xcc };
                    fd1.Write(nonZeroByte, 0, 1);           // write a byte at 64K mark
                    fd1.Flush();

                    // Should be 32 blocks for first 16KB, 1 index block, 1 block at end.
                    if (entry1.DataLength != 65537 || entry1.StorageSize != 34 * BLOCK_SIZE) {
                        throw new Exception("Bad SetLength / seek / write");
                    }
                }
            }
        }

        // Make sure that a file type change doesn't get overwritten when the updated file EOF
        // gets stored.
        public static void TestAttribChange(AppHook appHook) {
            const string FILE_NAME = "theFile";
            using (IFileSystem fs = Make525Floppy("Check.Attr", appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();
                IFileEntry file = fs.CreateFile(volDir, FILE_NAME, CreateMode.File);
                file.FileType = 0x01;
                file.SaveChanges();

                DiskFileStream fd = fs.OpenFile(file, FileAccessMode.ReadWrite, FilePart.DataFork);

                byte[] someData = new byte[] { 1, 2, 3, 4, 5, 6 };
                fd.Write(someData, 0, someData.Length);
                file.FileType = 0x02;
                fd.Write(someData, 0, someData.Length);
                file.SaveChanges();
                fd.Write(someData, 0, someData.Length);
                fd.Close();

                if (file.DataLength != someData.Length * 3) {
                    throw new Exception("weird");
                }

                fs.PrepareRawAccess();
                fs.PrepareFileAccess(DO_SCAN);
                Helper.CheckNotes(fs, 0, 0);
                volDir = fs.GetVolDirEntry();
                file = fs.FindFileEntry(volDir, FILE_NAME);
                if (file.FileType != 0x02) {
                    throw new Exception("file type change lost");
                }
            }
        }

        public static void TestFullFix(AppHook appHook) {
            using (IFileSystem fs = Make525Floppy("OVER.FIX.AZ09..", appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();
                IFileEntry file1 = fs.CreateFile(volDir, "File1", CreateMode.File);
                DiskFileStream fd1 = fs.OpenFile(file1, FileAccessMode.ReadWrite, FilePart.DataFork);

                // Should be 2 boot, 4 voldir, 1 volbitmap, 1 seedling == 8.
                if (fs.FreeSpace != (280 - 8) * BLOCK_SIZE) {
                    throw new Exception("Unexpected free space: " + (fs.FreeSpace / BLOCK_SIZE));
                }

                // Fill the entire disk.
                try {
                    FillFileSingle(fd1, true);
                    throw new Exception("failed to fill disk");
                } catch (DiskFullException) {
                    // expected
                }

                if (fs.FreeSpace != 0) {
                    throw new Exception("Failed to fill all blocks: " +
                        (fs.FreeSpace / BLOCK_SIZE));
                }

                // Free one block.
                long len = fd1.Seek(0, SeekOrigin.End);
                fd1.SetLength(len - BLOCK_SIZE);
                fd1.Flush();

                if (fs.FreeSpace != 1 * BLOCK_SIZE) {
                    throw new Exception("Failed to free one block: " + (fs.FreeSpace / BLOCK_SIZE));
                }

                // Try, and fail, to create an extended file.
                try {
                    fs.CreateFile(volDir, "FailEx", CreateMode.Extended);
                    throw new Exception("Created extended file somehow");
                } catch (DiskFullException) {
                    // expected
                }

                // Create another simple data file.
                IFileEntry file2 = fs.CreateFile(volDir, "File2", CreateMode.File);

                // Try, and fail, to create a directory.
                try {
                    fs.CreateFile(volDir, "FailDir", CreateMode.Directory);
                    throw new Exception("Created directory somehow");
                } catch (DiskFullException) {
                    // expected
                }

                // Write a full non-empty block.  This should succeed, since it's in the key block.
                DiskFileStream fd2 = fs.OpenFile(file2, FileAccessMode.ReadWrite, FilePart.DataFork);
                fd2.Write(sFullPattern, 0, BLOCK_SIZE);

                // Write a single zero byte.  This should work according to the end-of-file
                // sparse rules.
                byte[] oneZero = new byte[1];
                fd2.Write(oneZero, 0, 1);

                // Write one more nonzero byte.  This should fail.
                try {
                    byte[] oneOne = new byte[] { 1 };
                    fd2.Write(oneOne, 0, 1);
                    throw new Exception("Was able to write more into file2");
                } catch (DiskFullException) {
                    // expected
                }

                // Remove 8 blocks from file #1.
                len = fd1.Seek(0, SeekOrigin.End);
                fd1.SetLength(len - BLOCK_SIZE * 8);
                fd1.Flush();

                // Write 16 blocks to file #2.  We freed 8, so this should add 1 for the
                // sapling index block and 7 more for the data.
                try {
                    fd2.Write(sFullPattern, 0, BLOCK_SIZE * 16);
                    throw new Exception("Was able to write even more into file2");
                } catch (DiskFullException) {
                    // expected
                }

                // See if fd2's data length matches expectations.  Should be 8 blocks total.
                len = fd2.Seek(0, SeekOrigin.End);
                if (len != BLOCK_SIZE * 8) {
                    throw new Exception("Unexpected file2 length: " + len);
                }

                try {
                    fs.PrepareRawAccess();
                    throw new Exception("Allowed to go raw with files open");
                } catch (DAException) {
                    // expected
                }

                fd1.Close();
                fd2.Close();

                // Verify the filesystem.
                fs.PrepareRawAccess();
                fs.PrepareFileAccess(DO_SCAN);
                Helper.CheckNotes(fs, 0, 0);
            }
        }

        public static void TestReadDir(AppHook appHook) {
            const int DIR_ENTRY_LEN = 0x27;     // assume standard layout

            byte[] data = new byte[BLOCK_SIZE];

            using (IFileSystem fs = Make525Floppy("Read.Dir", appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();
                IFileEntry subDir = fs.CreateFile(volDir, "SubDir1",
                    IFileSystem.CreateMode.Directory);

                try {
                    fs.OpenFile(volDir, FileAccessMode.ReadWrite, FilePart.DataFork);
                    throw new Exception("Opened vol dir read-write");
                } catch (ArgumentException) {
                    // expected
                }

                using (DiskFileStream fd = fs.OpenFile(volDir, FileAccessMode.ReadOnly,
                        FilePart.DataFork)) {
                    if (fd.Seek(0, SeekOrigin.End) != 4 * BLOCK_SIZE) {
                        throw new Exception("Unexpected volume directory size");
                    }
                    fd.Seek(0, SeekOrigin.Begin);
                    fd.Read(data, 0, BLOCK_SIZE / 2);
                    if (RawData.GetU16LE(data, 0) != 0 || RawData.GetU16LE(data, 2) != 3) {
                        throw new Exception("Unexpected data in vol dir header");
                    }
                }

                // Create 20 files.  Should have 12 in the first block, 8 in the second.
                for (int i = 0; i < 20; i++) {
                    fs.CreateFile(subDir, "File." + i, CreateMode.File);
                }

                try {
                    fs.OpenFile(subDir, FileAccessMode.ReadWrite, FilePart.DataFork);
                    throw new Exception("Opened subdir read-write");
                } catch (ArgumentException) {
                    // expected
                }

                if (subDir.DataLength != 2 * BLOCK_SIZE) {
                    throw new Exception("Incorrect subdir length");
                }
                using (DiskFileStream fd = fs.OpenFile(subDir, FileAccessMode.ReadOnly,
                        FilePart.DataFork)) {
                    fd.Seek(BLOCK_SIZE + 4 + 7 * DIR_ENTRY_LEN, SeekOrigin.Begin);
                    fd.Read(data, 0, DIR_ENTRY_LEN);
                    // Should be entry for "File.19".
                    if ((data[0] & 0xf0) != 0x10 || data[1] != 'F') {
                        throw new Exception("Didn't find expected data #1");
                    }
                    // Should be first unused entry.
                    fd.Read(data, 0, DIR_ENTRY_LEN);
                    if (data[0] != 0) {
                        throw new Exception("Didn't find expected data #2");
                    }
                }
            }
        }

        // Test performing I/O with both forks open.
        public static void TestBothForks(AppHook appHook) {
            const string FILE_NAME = "Ext";
            using (IFileSystem fs = Make525Floppy("Both.Forks", appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();
                IFileEntry extFile = fs.CreateFile(volDir, FILE_NAME, CreateMode.Extended);

                // Write some data to the data fork.
                Helper.PopulateFile(fs, extFile, 0xff, 8, 0);

                // Make sure we're starting with nothing cached.
                fs.PrepareRawAccess();
                fs.PrepareFileAccess(DO_SCAN);
                volDir = fs.GetVolDirEntry();
                extFile = fs.FindFileEntry(volDir, FILE_NAME);

                if (extFile.DataLength != 8 * BLOCK_SIZE || extFile.RsrcLength != 0 ||
                        extFile.StorageSize != (1 + 8+1 + 1) * BLOCK_SIZE) {
                    throw new Exception("File was not expected size");
                }

                // Open both forks.
                DiskFileStream dataFd = fs.OpenFile(extFile, FileAccessMode.ReadWrite,
                    FilePart.DataFork);
                DiskFileStream rsrcFd = fs.OpenFile(extFile, FileAccessMode.ReadWrite,
                    FilePart.RsrcFork);

                // Try to open each a second time.
                try {
                    fs.OpenFile(extFile, FileAccessMode.ReadWrite, FilePart.DataFork);
                    throw new Exception("Opened data fork twice");
                } catch (IOException) {
                    // expected
                }
                try {
                    fs.OpenFile(extFile, FileAccessMode.ReadOnly, FilePart.RsrcFork);
                    throw new Exception("Opened rsrc fork twice");
                } catch (IOException) {
                    // expected
                }

                // Write 2K of data to the resource fork, to change its size.
                byte[] pattern = new byte[] { 0x11, 0x22, 0x33, 0x44 };
                for (int i = 0; i < 512; i++) {
                    rsrcFd.Write(pattern, 0, pattern.Length);
                }

                // Write a little data to the data fork, then truncate it to a seedling to
                // change the storage size.
                dataFd.Write(pattern, 0, pattern.Length);
                dataFd.SetLength(512);

                // Write another 512 bytes to the resource fork.
                for (int i = 0; i < 128; i++) {
                    rsrcFd.Write(pattern, 0, pattern.Length);
                }

                // Close one fork, and try to delete the file.
                rsrcFd.Close();
                try {
                    fs.DeleteFile(extFile);
                    throw new Exception("Deleted file with open fork");
                } catch (IOException) {
                    // expected
                }

                // Reopen fork.
                rsrcFd = fs.OpenFile(extFile, FileAccessMode.ReadOnly, FilePart.RsrcFork);

                // Close the files to flush the changes, then re-scan the filesystem.
                dataFd.Close();
                rsrcFd.Close();
                fs.PrepareRawAccess();
                fs.PrepareFileAccess(DO_SCAN);
                volDir = fs.GetVolDirEntry();
                extFile = fs.FindFileEntry(volDir, FILE_NAME);

                if (extFile.DataLength != 512 || extFile.RsrcLength != 2560 ||
                        extFile.StorageSize != (1 + 1 + 1+5) * BLOCK_SIZE) {
                    throw new Exception("Post-write file was not expected size");
                }
            }
        }

        // Solve Towers of Hanoi with MoveFile().
        public static void TestFileMoves(AppHook appHook) {
            using (IFileSystem fs = Make525Floppy("Renames", appHook)) {
                // ProDOS directory structures have references to the parent directory's key
                // block (for regular files) or specific directory block (for directories).  We
                // want to test both situations, so we need to create enough dummy entries to
                // push the activity into the second and third directory blocks.
                const int ENTRIES_PER_BLOCK = 0x0d;     // true for standard format
                const int NUM_DUMMIES = ENTRIES_PER_BLOCK * 2 - 5;
                const int NUM_RINGS = 8;

                IFileEntry volDir = fs.GetVolDirEntry();
                IFileEntry peg1 = fs.CreateFile(volDir, "PEG1", CreateMode.Directory);
                IFileEntry peg2 = fs.CreateFile(volDir, "PEG2", CreateMode.Directory);
                IFileEntry peg3 = fs.CreateFile(volDir, "PEG3", CreateMode.Directory);
                for (int i = 0; i < NUM_DUMMIES; i++) {
                    fs.CreateFile(peg1, "DUMMY" + i, CreateMode.File);
                    fs.CreateFile(peg2, "DUMMY" + i, CreateMode.File);
                    fs.CreateFile(peg3, "DUMMY" + i, CreateMode.File);
                }
                // Create a mix of files and directories to move around.
                for (int i = NUM_RINGS - 1; i >= 0; i--) {
                    fs.CreateFile(peg1, "RING" + i,
                        (i & 0x01) == 0 ? CreateMode.File : CreateMode.Directory);
                }

                // Move 8 rings from peg1 to peg2, using peg3 as spare.
                SolveHanoi(fs, NUM_RINGS - 1, peg1, peg2, peg3);

                List<IFileEntry> entries = peg1.ToList();
                if (entries.Count != NUM_DUMMIES) {
                    throw new Exception("Extra stuff on peg1");
                }
                entries = peg3.ToList();
                if (entries.Count != NUM_DUMMIES) {
                    throw new Exception("Extra stuff on peg3");
                }
                entries = peg2.ToList();
                if (entries.Count != NUM_DUMMIES + NUM_RINGS) {
                    throw new Exception("Wrong count on peg2");
                }

                for (int i = 0; i < NUM_RINGS; i++) {
                    // Should be the dummies followed by RING7, RING6, ... RING0.  ProDOS doesn't
                    // sort, and should be using first available open slot.
                    IFileEntry ring = entries[NUM_DUMMIES + i];
                    if (ring.FileName != "RING" + (7 - i)) {
                        throw new Exception("File in unexpected order");
                    }
                }

                // Bounce the filesystem to scan for damage.
                fs.PrepareRawAccess();
                fs.PrepareFileAccess(true);
                Helper.CheckNotes(fs, 0, 0);
            }
        }

        private static void SolveHanoi(IFileSystem fs, int fileNum, IFileEntry sourceDir,
                IFileEntry destDir, IFileEntry spareDir) {
            IFileEntry ring = fs.FindFileEntry(sourceDir, "RING" + fileNum);
            Debug.Assert(ring.ContainingDir == sourceDir);
            if (fileNum == 0) {
                fs.MoveFile(ring, destDir, ring.FileName);
            } else {
                // Move stack above target ring from "source" peg to "spare" peg.
                SolveHanoi(fs, fileNum - 1, sourceDir, spareDir, destDir);
                // Move target ring to "dest" peg.
                fs.MoveFile(ring, destDir, ring.FileName);
                // Move stack from "spare" peg to "dest" peg.
                SolveHanoi(fs, fileNum - 1, spareDir, destDir, sourceDir);
            }
        }


        #region Utilities

        private static readonly byte[] sFullPattern = MakeFullPattern();
        private static readonly byte[] sFullZero = new byte[DiskArc.FS.ProDOS.MAX_FILE_LEN];

        private static byte[] MakeFullPattern() {
            byte[] buf = new byte[DiskArc.FS.ProDOS.MAX_FILE_LEN];

            for (int i = 0; i < buf.Length; i += BLOCK_SIZE) {
                // Copy the block number, so the data is unique.
                RawData.SetU16LE(buf, i, (ushort)i);
                buf[i + 2] = 0xcc;
            }
            return buf;
        }

        private static void FillFileBlocks(DiskFileStream fd, bool nonZero) {
            byte[] buf = sFullZero;
            if (nonZero) {
                buf = sFullPattern;
            }

            // Write one block at a time.
            for (int i = 0; i < 256 * 128; i++) {
                if (i == 256 * 128 - 1) {
                    // Last block is one byte short.
                    fd.Write(buf, i * BLOCK_SIZE, BLOCK_SIZE - 1);
                } else {
                    fd.Write(buf, i * BLOCK_SIZE, BLOCK_SIZE);
                }
            }
        }

        private static void CheckFileBlocks(DiskFileStream fd, bool nonZero) {
            byte[] cmpBuf = sFullZero;
            if (nonZero) {
                cmpBuf = sFullPattern;
            }
            byte[] rdBuf = new byte[BLOCK_SIZE];

            // Read one block at a time.
            for (int i = 0; i < 256 * 128; i++) {
                int actual = fd.Read(rdBuf, 0, rdBuf.Length);
                if (actual == 0) {
                    // Reached EOF.  We assume the caller verified that the EOF had the
                    // expected value.
                    break;
                } else if (actual == 511) {
                    // Last block is expected to be one byte short.
                    if (i != 256 * 128 - 1) {
                        throw new Exception("short on block #" + i);
                    }
                } else if (actual != 512) {
                    throw new Exception("partial read on block #" + i);
                }
                if (!RawData.CompareBytes(rdBuf, 0, cmpBuf, i * BLOCK_SIZE, actual)) {
                    throw new Exception("Data read mismatch in block #" + i);
                }
            }
        }

        private static void FillFileSingle(DiskFileStream fd, bool nonZero) {
            byte[] buf = sFullZero;
            if (nonZero) {
                buf = sFullPattern;
            }
            // Write the entire thing in one shot.
            fd.Write(buf, 0, buf.Length);
        }

        private static void CheckFileSingle(DiskFileStream fd, bool nonZero) {
            byte[] cmpBuf = sFullZero;
            if (nonZero) {
                cmpBuf = sFullPattern;
            }
            byte[] rdBuf = new byte[DiskArc.FS.ProDOS.MAX_FILE_LEN];

            // Read the entire thing in one shot.  Compare however much we actually got back.
            int actual = fd.Read(rdBuf, 0, rdBuf.Length);
            if (!RawData.CompareBytes(rdBuf, 0, cmpBuf, 0, actual)) {
                throw new Exception("Data read mismatch (got " + actual + " bytes)");
            }
        }

        private static IFileSystem Make525Floppy(string volName, AppHook appHook) {
            return Helper.CreateTestImage(volName, FileSystemType.ProDOS, 280, appHook,
                out MemoryStream memFile);
        }
        private static IFileSystem MakeBigHD(string volName, AppHook appHook) {
            return Helper.CreateTestImage(volName, FileSystemType.ProDOS, 65535, appHook,
                out MemoryStream memFile);
        }

        #endregion Utilities
    }
}
