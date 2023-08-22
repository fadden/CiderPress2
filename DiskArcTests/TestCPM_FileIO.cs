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
using System.Text;

using CommonUtil;
using DiskArc;
using DiskArc.FS;
using static DiskArc.Defs;
using static DiskArc.IFileSystem;

// TODO: rename a file with multiple extents

namespace DiskArcTests {
    /// <summary>
    /// CP/M filesystem I/O tests.
    /// </summary>
    public class TestCPM_FileIO : ITest {
        private struct StringPair {
            public string String1 { get; }
            public string String2 { get; }
            public StringPair(string str1, string str2) {
                String1 = str1;
                String2 = str2;
            }
        }
        private static readonly StringPair[] sAdjustNames = new StringPair[] {
            new StringPair("FULLNAME.TXT", "FULLNAME.TXT"),
            new StringPair("SHORT.T", "SHORT.T"),
            new StringPair("NOEXT", "NOEXT"),
            new StringPair("bad[]chr.<x>", "BAD__CHR._X_"),
            new StringPair("", "Q"),
            new StringPair(".Foo", "Q.FOO"),
            new StringPair(" SPACES . z ", "_SPACES_._Z_"),
            new StringPair("Ctrl\r\n.\t", "CTRL__._"),
            new StringPair("NameTooLong.Extended", "NAME_ONG.EXT"),
            new StringPair("LOTS.OF.DOTS", "LOTS_OF.DOT"),
        };

        public static void TestAdjustName(AppHook appHook) {
            foreach (StringPair sp in sAdjustNames) {
                string adjusted = CPM_FileEntry.AdjustFileName(sp.String1);
                if (adjusted != sp.String2) {
                    throw new Exception("Adjustment failed: '" + sp.String1 + "' -> '" +
                        adjusted + "', expected '" + sp.String2 + "'");
                }
            }
        }

        // Simple create / delete test.
        public static void TestCreateDelete(AppHook appHook) {
            using (IFileSystem fs = Make525Floppy(appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();

                IFileEntry fileX = fs.CreateFile(volDir, "FILEx", CreateMode.File);
                try {
                    fs.CreateFile(volDir, "FILEx", CreateMode.File);
                    throw new Exception("Created a second file with same name");
                } catch (IOException) { /*expected*/ }
                IFileEntry file0 = fs.CreateFile(volDir, "FILE0", CreateMode.File);
                IFileEntry fileY = fs.CreateFile(volDir, "FILEy", CreateMode.File);
                IFileEntry file1 = fs.CreateFile(volDir, "FILE1", CreateMode.File);
                IFileEntry fileZ = fs.CreateFile(volDir, "FILEz", CreateMode.File);
                fs.DeleteFile(fileX);
                fs.DeleteFile(fileY);
                fs.DeleteFile(fileZ);
                IFileEntry file2 = fs.CreateFile(volDir, "FILE2", CreateMode.File);
                IFileEntry file3 = fs.CreateFile(volDir, "FILE3", CreateMode.File);
                IFileEntry file4 = fs.CreateFile(volDir, "FILE4", CreateMode.File);

                // Even though we're re-using extents, we currently just add files to the end
                // of the list.  If the filesystem is reloaded the order will be different.
                int index = 0;
                foreach (IFileEntry entry in volDir) {
                    string expName = "FILE" + index;
                    Helper.ExpectString(expName, entry.FileName, "file out of order");
                    index++;
                }
            }
        }

        public static void TestAttributes(AppHook appHook) {
            using (IFileSystem fs = Make525Floppy(appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();
                IFileEntry file0 = fs.CreateFile(volDir, "FILE0", CreateMode.File);
                IFileEntry file1 = fs.CreateFile(volDir, "FILE1.$$$", CreateMode.File);
                IFileEntry file2 = fs.CreateFile(volDir, "FILE2", CreateMode.File);

                Helper.ExpectString("FILE1.$$$", file1.FileName, "wrong filename");
                byte[] raw = file1.RawFileName;
                if (raw.Length != 11 || Encoding.ASCII.GetString(raw) != "FILE1   $$$") {
                    throw new Exception("Incorrect raw filename");
                }
                Helper.ExpectByte(0, file1.FileType, "wrong file type");
                Helper.ExpectInt(0, file1.AuxType, "wrong aux type");
                // not currently handling timestamps
                if (file1.CreateWhen != TimeStamp.NO_DATE || file1.ModWhen != TimeStamp.NO_DATE) {
                    throw new Exception("Incorrect create/mod date");
                }

                file0.RawFileName = new byte[11] {
                    (byte)'F', (byte)'i', (byte)'l', (byte)'e', (byte)'0', (byte)'A',
                    (byte)' ', (byte)' ', (byte)'W', (byte)' ', (byte)' '
                };
                Helper.ExpectString("File0A.W", file0.FileName, "failed to set raw filename");
                file1.FileName = "FILE1A.X";
                Helper.ExpectString("FILE1A.X", file1.FileName, "failed to set filename");
                try {
                    file1.FileName = "ARG<>!";
                    throw new Exception("bad name accepted");
                } catch (ArgumentException) { /*expected*/ }

                Helper.ExpectByte(FileAttribs.FILE_ACCESS_UNLOCKED, file0.Access,
                    "bad initial access flags");
                file1.Access = (byte)(FileAttribs.AccessFlags.Invisible |
                    FileAttribs.AccessFlags.Backup);    // read, rename, delete always present
                Helper.ExpectByte((byte)(FileAttribs.AccessFlags.Read |
                    FileAttribs.AccessFlags.Rename | FileAttribs.AccessFlags.Delete |
                    FileAttribs.AccessFlags.Invisible | FileAttribs.AccessFlags.Backup),
                    file1.Access, "failed to change access flags");

                fs.MoveFile(file2, volDir, "FILE2A");

                // Bounce the filesystem without calling SaveChanges().
                fs.PrepareRawAccess();
                fs.PrepareFileAccess(true);
                Helper.CheckNotes(fs, 0, 0);

                Helper.ExpectString("File0A.W", file0.FileName, "failed to keep raw filename");
                Helper.ExpectString("FILE1A.X", file1.FileName, "failed to keep filename");
                Helper.ExpectByte((byte)(FileAttribs.AccessFlags.Read |
                    FileAttribs.AccessFlags.Rename | FileAttribs.AccessFlags.Delete |
                    FileAttribs.AccessFlags.Invisible | FileAttribs.AccessFlags.Backup),
                    file1.Access, "failed to keep access flags");
            }
        }

        // Create files of various sizes, one byte at a time, and confirm that they retain
        // their size and contents.
        public static void TestByteRW_140(AppHook appHook) {
            const int ALLOC_SIZE = BLOCK_SIZE * 2;

            using (IFileSystem fs = Make525Floppy(appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();
                IFileEntry file0 = fs.CreateFile(volDir, "FILE_0", CreateMode.File);
                Helper.CheckFill(fs, file0, 0);
                Helper.ExpectLong(0, file0.StorageSize, "wrong storage size");

                IFileEntry file1 = fs.CreateFile(volDir, "FILE_1", CreateMode.File);
                Helper.FillFile(fs, file1, 1);
                Helper.CheckFill(fs, file1, 1);
                Helper.ExpectLong(ALLOC_SIZE, file1.StorageSize, "wrong storage size");

                // Before, at, and after the allocation block threshold.
                IFileEntry file1023 = fs.CreateFile(volDir, "FILE1023", CreateMode.File);
                Helper.FillFile(fs, file1023, 1023);
                Helper.CheckFill(fs, file1023, 1023);
                Helper.ExpectLong(ALLOC_SIZE, file1023.StorageSize, "wrong storage size");

                IFileEntry file1024 = fs.CreateFile(volDir, "FILE1024", CreateMode.File);
                Helper.FillFile(fs, file1024, 1024);
                Helper.CheckFill(fs, file1024, 1024);
                Helper.ExpectLong(ALLOC_SIZE, file1024.StorageSize, "wrong storage size");

                IFileEntry file1025 = fs.CreateFile(volDir, "FILE1025", CreateMode.File);
                Helper.FillFile(fs, file1025, 1025);
                Helper.CheckFill(fs, file1025, 1025);
                Helper.ExpectLong(ALLOC_SIZE * 2, file1025.StorageSize, "wrong storage size");

                // Before, at, and after the extent threshold.
                IFileEntry file16383 = fs.CreateFile(volDir, "FIL16383", CreateMode.File);
                Helper.FillFile(fs, file16383, 16383);
                Helper.CheckFill(fs, file16383, 16383);
                Helper.ExpectLong(ALLOC_SIZE * 16, file16383.StorageSize, "wrong storage size");

                IFileEntry file16384 = fs.CreateFile(volDir, "FIL16384", CreateMode.File);
                Helper.FillFile(fs, file16384, 16384);
                Helper.CheckFill(fs, file16384, 16384);
                Helper.ExpectLong(ALLOC_SIZE * 16, file16384.StorageSize, "wrong storage size");

                IFileEntry file16385 = fs.CreateFile(volDir, "FIL16385", CreateMode.File);
                Helper.FillFile(fs, file16385, 16385);
                Helper.CheckFill(fs, file16385, 16385);
                Helper.ExpectLong(ALLOC_SIZE * 17, file16385.StorageSize, "wrong storage size");

                // Bounce the filesystem to verify that everything is actually on disk.
                fs.PrepareRawAccess();
                fs.PrepareFileAccess(true);
                Helper.CheckNotes(fs, 0, 0);
                volDir = fs.GetVolDirEntry();

                Helper.CheckFill(fs, fs.FindFileEntry(volDir, "FILE_0"), 0);
                Helper.CheckFill(fs, fs.FindFileEntry(volDir, "FILE_1"), 1);
                Helper.CheckFill(fs, fs.FindFileEntry(volDir, "FILE1023"), 1023);
                Helper.CheckFill(fs, fs.FindFileEntry(volDir, "FILE1024"), 1024);
                Helper.CheckFill(fs, fs.FindFileEntry(volDir, "FILE1025"), 1025);
                Helper.CheckFill(fs, fs.FindFileEntry(volDir, "FIL16383"), 16383);
                Helper.CheckFill(fs, fs.FindFileEntry(volDir, "FIL16384"), 16384);
                Helper.CheckFill(fs, fs.FindFileEntry(volDir, "FIL16385"), 16385);

                IFileEntry overFill = fs.CreateFile(volDir, "OVERFILL.!", CreateMode.File);
                try {
                    Helper.PopulateFile(fs, overFill, 0xcc, 140 * 1024 / BLOCK_SIZE, 0);
                } catch (DiskFullException) { /*expected*/ }

                Helper.ExpectLong(0, fs.FreeSpace, "some space still free");

                // Delete a one-block file that is sandwiched between other files, create a new
                // one, write 1024 bytes into it to verify that we don't over-alBlocate.
                file1023 = fs.FindFileEntry(volDir, "FILE1023");
                Helper.ExpectLong(ALLOC_SIZE, file1023.StorageSize, "wrong storage size");
                fs.DeleteFile(file1023);
                IFileEntry new1024 = fs.CreateFile(volDir, "FILE1024.NEW", CreateMode.File);
                Helper.FillFile(fs, new1024, 1024);
                Helper.CheckFill(fs, new1024, 1024);

                // Try to append another byte.  We should get "disk full".
                using (Stream stream = fs.OpenFile(new1024, FileAccessMode.ReadWrite,
                        FilePart.DataFork)) {
                    stream.Position = 1024;
                    try {
                        stream.WriteByte(0xaa);
                        throw new Exception("expanded file beyond available space");
                    } catch (DiskFullException) { /*expected*/ }
                }

                //fs.DumpToFile(@"C:\src\CiderPress2\test140.co");
            }
        }

        // Repeat for 800KB disks, which have 2048-byte alloc blocks and 16-bit block numbers.
        public static void TestByteRW_800(AppHook appHook) {
            const int ALLOC_SIZE = BLOCK_SIZE * 4;

            using (IFileSystem fs = Make35Floppy(appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();
                IFileEntry file0 = fs.CreateFile(volDir, "FILE_0", CreateMode.File);
                Helper.CheckFill(fs, file0, 0);
                Helper.ExpectLong(0, file0.StorageSize, "wrong storage size");

                IFileEntry file1 = fs.CreateFile(volDir, "FILE_1", CreateMode.File);
                Helper.FillFile(fs, file1, 1);
                Helper.CheckFill(fs, file1, 1);
                Helper.ExpectLong(ALLOC_SIZE, file1.StorageSize, "wrong storage size");

                // Before, at, and after the allocation block threshold.
                IFileEntry file2047 = fs.CreateFile(volDir, "FILE2047", CreateMode.File);
                Helper.FillFile(fs, file2047, 2047);
                Helper.CheckFill(fs, file2047, 2047);
                Helper.ExpectLong(ALLOC_SIZE, file2047.StorageSize, "wrong storage size");

                IFileEntry file2048 = fs.CreateFile(volDir, "FILE2048", CreateMode.File);
                Helper.FillFile(fs, file2048, 2048);
                Helper.CheckFill(fs, file2048, 2048);
                Helper.ExpectLong(ALLOC_SIZE, file2048.StorageSize, "wrong storage size");

                IFileEntry file2049 = fs.CreateFile(volDir, "FILE2049", CreateMode.File);
                Helper.FillFile(fs, file2049, 2049);
                Helper.CheckFill(fs, file2049, 2049);
                Helper.ExpectLong(ALLOC_SIZE * 2, file2049.StorageSize, "wrong storage size");

                // Before, at, and after the extent threshold.
                IFileEntry file16383 = fs.CreateFile(volDir, "FIL16383", CreateMode.File);
                Helper.FillFile(fs, file16383, 16383);
                Helper.CheckFill(fs, file16383, 16383);
                Helper.ExpectLong(ALLOC_SIZE * 8, file16383.StorageSize, "wrong storage size");

                IFileEntry file16384 = fs.CreateFile(volDir, "FIL16384", CreateMode.File);
                Helper.FillFile(fs, file16384, 16384);
                Helper.CheckFill(fs, file16384, 16384);
                Helper.ExpectLong(ALLOC_SIZE * 8, file16384.StorageSize, "wrong storage size");

                IFileEntry file16385 = fs.CreateFile(volDir, "FIL16385", CreateMode.File);
                Helper.FillFile(fs, file16385, 16385);
                Helper.CheckFill(fs, file16385, 16385);
                Helper.ExpectLong(ALLOC_SIZE * 9, file16385.StorageSize, "wrong storage size");

                // Bounce the filesystem to verify that everything is actually on disk.
                fs.PrepareRawAccess();
                fs.PrepareFileAccess(true);
                Helper.CheckNotes(fs, 0, 0);
                volDir = fs.GetVolDirEntry();

                Helper.CheckFill(fs, fs.FindFileEntry(volDir, "FILE_0"), 0);
                Helper.CheckFill(fs, fs.FindFileEntry(volDir, "FILE_1"), 1);
                Helper.CheckFill(fs, fs.FindFileEntry(volDir, "FILE2047"), 2047);
                Helper.CheckFill(fs, fs.FindFileEntry(volDir, "FILE2048"), 2048);
                Helper.CheckFill(fs, fs.FindFileEntry(volDir, "FILE2049"), 2049);
                Helper.CheckFill(fs, fs.FindFileEntry(volDir, "FIL16383"), 16383);
                Helper.CheckFill(fs, fs.FindFileEntry(volDir, "FIL16384"), 16384);
                Helper.CheckFill(fs, fs.FindFileEntry(volDir, "FIL16385"), 16385);

                IFileEntry overFill = fs.CreateFile(volDir, "OVERFILL.!", CreateMode.File);
                try {
                    Helper.PopulateFile(fs, overFill, 0xcc, 800 * 1024 / BLOCK_SIZE, 0);
                } catch (DiskFullException) { /*expected*/ }

                Helper.ExpectLong(0, fs.FreeSpace, "some space still free");

                // Delete a one-block file that is sandwiched between other files, create a new
                // one, write 1024 bytes into it to verify that we don't over-allocate.
                file2047 = fs.FindFileEntry(volDir, "FILE2047");
                Helper.ExpectLong(ALLOC_SIZE, file2047.StorageSize, "wrong storage size");
                fs.DeleteFile(file2047);
                IFileEntry new2048 = fs.CreateFile(volDir, "FILE2048.NEW", CreateMode.File);
                Helper.FillFile(fs, new2048, 2048);
                Helper.CheckFill(fs, new2048, 2048);

                // Try to append another byte.  We should get "disk full".
                using (Stream stream = fs.OpenFile(new2048, FileAccessMode.ReadWrite,
                        FilePart.DataFork)) {
                    stream.Position = 2048;
                    try {
                        stream.WriteByte(0xaa);
                        throw new Exception("expanded file beyond available space");
                    } catch (DiskFullException) { /*expected*/ }
                }

                //fs.DumpToFile(@"C:\src\CiderPress2\test800.co");
            }
        }

        // Test different ways of changing the file length.
        public static void TestLengthChange(AppHook appHook) {
            const int TEST_SIZE = 20000;
            using (IFileSystem fs = Make525Floppy(appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();
                IFileEntry file1 = fs.CreateFile(volDir, "FILE1", CreateMode.File);

                byte[] patBuf = Helper.SeqByteTestPattern(TEST_SIZE);
                using (Stream stream = fs.OpenFile(file1, FileAccessMode.ReadWrite,
                        FilePart.DataFork)) {
                    stream.Write(patBuf, 0, TEST_SIZE);
                }

                byte[] readBuf = new byte[TEST_SIZE];
                using (Stream stream = fs.OpenFile(file1, FileAccessMode.ReadWrite,
                        FilePart.DataFork)) {
                    stream.ReadExactly(readBuf, 0, TEST_SIZE);
                    if (RawData.MemCmp(patBuf, readBuf, TEST_SIZE) != 0) {
                        throw new Exception("Data read doesn't match data written");
                    }

                    // Truncate file, confirm additional space is zeroed.
                    const int PARTIAL = 16000;
                    stream.SetLength(PARTIAL);
                    stream.Flush();
                    Helper.ExpectLong(PARTIAL, file1.DataLength, "incorrect length / PARTIAL");
                    stream.SetLength(TEST_SIZE);
                    stream.Flush();
                    Helper.ExpectLong(TEST_SIZE, file1.DataLength, "incorrect length / TEST_SIZE");
                    stream.Position = 0;
                    stream.ReadExactly(readBuf, 0, TEST_SIZE);
                    if (RawData.MemCmp(patBuf, readBuf, PARTIAL) != 0) {
                        throw new Exception("First part of data doesn't match what was written");
                    }
                    for (int i = PARTIAL; i < TEST_SIZE; i++) {
                        if (readBuf[i] != CPM.NO_DATA) {
                            throw new Exception("Truncation didn't erase data");
                        }
                    }

                    // Try an excessively large length extension.  When it fails, the length of
                    // the file and the stream should be unchanged.
                    try {
                        stream.Position = 123;
                        stream.SetLength(140 * 1024);
                        throw new Exception("extended file length beyond disk limits");
                    } catch (DiskFullException) { /*expected*/ }
                    Helper.ExpectLong(123, stream.Position, "SetLength didn't restore position");
                    Helper.ExpectLong(TEST_SIZE, stream.Length, "bad setlen altered stream len");
                    Helper.ExpectLong(TEST_SIZE, file1.DataLength, "bad setlen altered file len");
                }

                // Try a few positions.
                using (Stream stream = fs.OpenFile(file1, FileAccessMode.ReadWrite,
                        FilePart.DataFork)) {
                    stream.SetLength(100 * 1024);
                    stream.SetLength(16385);
                    stream.Flush();
                    Helper.ExpectLong(16385, stream.Length, "bad stream len");
                    Helper.ExpectLong(16385, file1.DataLength, "bad file len");
                    Helper.ExpectLong(16384 + 1024, file1.StorageSize, "bad storage size");

                    stream.SetLength(16384);
                    stream.Flush();
                    Helper.ExpectLong(16384, stream.Length, "bad stream len");
                    Helper.ExpectLong(16384, file1.DataLength, "bad file len");
                    Helper.ExpectLong(16384, file1.StorageSize, "bad storage size");

                    stream.SetLength(0);
                    stream.Flush();
                    Helper.ExpectLong(0, stream.Length, "bad stream len");
                    Helper.ExpectLong(0, file1.DataLength, "bad file len");
                    Helper.ExpectLong(0, file1.StorageSize, "bad storage size");
                }
            }
        }

        public static void TestSparse(AppHook appHook) {
            const int ALLOC_SIZE = 1024;
            const int SPARSE1 = 16384 * 4 + 100;
            const int SPARSE2 = 16384 * 5 + 100;
            const int SPARSE3 = 16384 * 6 + 100;

            using (IFileSystem fs = Make525Floppy(appHook)) {
                long initialFree = fs.FreeSpace;
                IFileEntry volDir = fs.GetVolDirEntry();
                IFileEntry file1 = fs.CreateFile(volDir, "FILE1", CreateMode.File);

                using (Stream stream = fs.OpenFile(file1, FileAccessMode.ReadWrite,
                        FilePart.DataFork)) {
                    stream.Position = SPARSE1;
                    stream.WriteByte(0xaa);
                    stream.Flush();
                    Helper.ExpectLong(SPARSE1 + 1, stream.Length, "incorrect stream len #1");
                    Helper.ExpectLong(SPARSE1 + 1, file1.DataLength, "incorrect file len #1");
                    Helper.ExpectLong(ALLOC_SIZE, file1.StorageSize, "incorrect stor size #1");

                    stream.Position = SPARSE3;
                    stream.WriteByte(0xcc);
                    stream.Flush();
                    Helper.ExpectLong(SPARSE3 + 1, stream.Length, "incorrect stream len #2");
                    Helper.ExpectLong(SPARSE3 + 1, file1.DataLength, "incorrect file len #2");
                    Helper.ExpectLong(ALLOC_SIZE * 2, file1.StorageSize, "incorrect stor size #2");

                    // Put one more between the other two.
                    stream.Position = SPARSE2;
                    stream.WriteByte(0xbb);
                    stream.Flush();
                    Helper.ExpectLong(SPARSE3 + 1, stream.Length, "incorrect stream len #3");
                    Helper.ExpectLong(SPARSE3 + 1, file1.DataLength, "incorrect file len #3");
                    Helper.ExpectLong(ALLOC_SIZE * 3, file1.StorageSize, "incorrect stor size #3");
                }

                // Test reading across sparse extents and sparse blocks.
                using (Stream stream = fs.OpenFile(file1, FileAccessMode.ReadOnly,
                        FilePart.DataFork)) {
                    int foundCount = 0;
                    for (int i = 0; i < stream.Length; i++) {
                        int val = stream.ReadByte();
                        if (val != 0xe5) {
                            // Checking vs. 0xe5 only works because the volume is newly formatted.
                            // The unwritten parts of non-sparse blocks hold the previous contents.
                            if (val == 0xaa) {
                                Helper.ExpectInt(SPARSE1, i, "found 0xaa at wrong place");
                                foundCount++;
                            } else if (val == 0xbb) {
                                Helper.ExpectInt(SPARSE2, i, "found 0xbb at wrong place");
                                foundCount++;
                            } else if (val == 0xcc) {
                                Helper.ExpectInt(SPARSE3, i, "found 0xcc at wrong place");
                                foundCount++;
                            } else {
                                throw new Exception("Found non-0xe5 at " + i);
                            }
                        }
                    }
                    Helper.ExpectInt(3, foundCount, "didn't find all bytes");

                    // Test seeking to data or holes.
                    const int EXT_MASK = ~0x3fff;       // mask down to multiple of 16384
                    Helper.ExpectLong(0, stream.Seek(0, SEEK_ORIGIN_HOLE),
                        "bad hole 0");
                    Helper.ExpectLong(SPARSE1 & EXT_MASK, stream.Seek(0, SEEK_ORIGIN_DATA),
                        "bad hole 0");
                    Helper.ExpectLong(SPARSE1, stream.Seek(SPARSE1, SEEK_ORIGIN_DATA),
                        "bad data 1");
                    Helper.ExpectLong((SPARSE1 & EXT_MASK) + ALLOC_SIZE,
                        stream.Seek(SPARSE1, SEEK_ORIGIN_HOLE),
                        "bad hole 1");
                    Helper.ExpectLong(SPARSE2 & EXT_MASK,
                        stream.Seek(SPARSE1 + ALLOC_SIZE, SEEK_ORIGIN_DATA),
                        "bad data 1a");
                    Helper.ExpectLong(SPARSE1 + ALLOC_SIZE,
                        stream.Seek(SPARSE1 + ALLOC_SIZE, SEEK_ORIGIN_HOLE),
                        "bad hole 1a");
                    Helper.ExpectLong(SPARSE2, stream.Seek(SPARSE2, SEEK_ORIGIN_DATA),
                        "bad data 2");

                    Helper.ExpectLong(stream.Length, stream.Seek(stream.Length, SEEK_ORIGIN_DATA),
                        "bad data at EOF");
                    Helper.ExpectLong(stream.Length, stream.Seek(stream.Length, SEEK_ORIGIN_DATA),
                        "bad hole at EOF");
                }

                // Change the filename, now that it has multiple extents.
                file1.FileName = "FILE1A";
                file1.Access |= (byte)FileAttribs.AccessFlags.Invisible;

                // Let the validity checker poke at it.
                fs.PrepareRawAccess();
                //fs.DumpToFile(@"C:\src\CiderPress2\test-sparse.co");
                fs.PrepareFileAccess(true);
                Helper.CheckNotes(fs, 0, 0);

                volDir = fs.GetVolDirEntry();
                file1 = fs.FindFileEntry(volDir, "FILE1A");
                if ((file1.Access & (byte)FileAttribs.AccessFlags.Invisible) == 0) {
                    throw new Exception("lost invisible flag");
                }
                fs.DeleteFile(file1);

                Helper.ExpectLong(initialFree, fs.FreeSpace, "deletion left stuff");
                fs.PrepareRawAccess();
                fs.PrepareFileAccess(true);
                Helper.CheckNotes(fs, 0, 0);
            }
        }

        // Confirm we get "disk full" when the volume dir fills up.
        public static void TestVolDirFull(AppHook appHook) {
            const int MAX_FILE_COUNT = 64;      // 2048 / 32
            using (IFileSystem fs = Make525Floppy(appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();
                for (int i = 0; i < MAX_FILE_COUNT; i++) {
                    fs.CreateFile(volDir, "FILE_" + i, CreateMode.File);
                }

                try {
                    fs.CreateFile(volDir, "STRAW", CreateMode.File);
                    throw new Exception("created too many files");
                } catch (DiskFullException) { /*expected*/ }
            }
        }

        #region Utilities

        private static IFileSystem Make525Floppy(AppHook appHook) {
            return Helper.CreateTestImage(string.Empty, FileSystemType.CPM, 280, appHook,
                out MemoryStream memFile);
        }

        private static IFileSystem Make35Floppy(AppHook appHook) {
            return Helper.CreateTestImage(string.Empty, FileSystemType.CPM, 1600, appHook,
                out MemoryStream memFile);
        }

        #endregion Utilities
    }
}
