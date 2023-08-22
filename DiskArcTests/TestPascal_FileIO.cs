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
using System.IO;
using System.Text;

using CommonUtil;
using DiskArc;
using DiskArc.FS;
using static DiskArc.Defs;
using static DiskArc.IFileSystem;

namespace DiskArcTests {
    /// <summary>
    /// Pascal filesystem I/O tests.
    /// </summary>
    public class TestPascal_FileIO : ITest {
        private const int SYSTEM_BLOCKS = 6;        // two for boot, four for volume dir

        // Simple create / delete test.
        public static void TestCreateDelete(AppHook appHook) {
            using (IFileSystem fs = Make525Floppy("Tester", appHook)) {
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

                // Files are added to the largest gap, which means they will always be added
                // to the end.
                int index = 0;
                foreach (IFileEntry entry in volDir) {
                    string expName = "FILE" + index;
                    Helper.ExpectString(expName, entry.FileName, "file out of order");
                    index++;
                }
            }
        }

        public static void TestCreateDeleteExpand(AppHook appHook) {
            using (IFileSystem fs = Make525Floppy("Tester", appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();

                IFileEntry fileX = fs.CreateFile(volDir, "FILEx", CreateMode.File);
                IFileEntry file1 = fs.CreateFile(volDir, "FILE1", CreateMode.File);
                IFileEntry fileY = fs.CreateFile(volDir, "FILEy", CreateMode.File);
                IFileEntry file3 = fs.CreateFile(volDir, "FILE3", CreateMode.File);
                IFileEntry fileZ = fs.CreateFile(volDir, "FILEz", CreateMode.File);
                IFileEntry file5 = fs.CreateFile(volDir, "FILE5", CreateMode.File);
                // Six files have been created.  Expand the last to fill the disk.
                long expectedFree = (280 - SYSTEM_BLOCKS - 6) * BLOCK_SIZE;
                long fillDiskLen = expectedFree + BLOCK_SIZE;
                Helper.ExpectLong(expectedFree, fs.FreeSpace, "free space not right");
                using (Stream stream = fs.OpenFile(file5, FileAccessMode.ReadWrite,
                        FilePart.DataFork)) {
                    stream.SetLength(fillDiskLen);
                }
                Helper.ExpectLong(fillDiskLen, file5.DataLength, "file didn't fully expand");
                Helper.ExpectLong(0, fs.FreeSpace, "disk isn't full");

                try {
                    fs.CreateFile(volDir, "FULL", CreateMode.File);
                    throw new Exception("Overfilled the disk");
                } catch (DiskFullException) { /*expected*/ }

                fs.DeleteFile(fileX);
                fs.DeleteFile(fileY);
                fs.DeleteFile(fileZ);
                IFileEntry file0 = fs.CreateFile(volDir, "FILE0", CreateMode.File);
                IFileEntry file2 = fs.CreateFile(volDir, "FILE2", CreateMode.File);
                IFileEntry file4 = fs.CreateFile(volDir, "FILE4", CreateMode.File);

                // No space at end of disk, so new files should fill in the gaps, in order.
                int index = 0;
                foreach (IFileEntry entry in volDir) {
                    string expName = "FILE" + index;
                    Helper.ExpectString(expName, entry.FileName, "file out of order");
                    index++;
                }
            }
        }

        public static void TestAttributes(AppHook appHook) {
            using (IFileSystem fs = Make525Floppy("Tester", appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();
                IFileEntry file0 = fs.CreateFile(volDir, "FILE0", CreateMode.File);
                IFileEntry file1 = fs.CreateFile(volDir, "FILE1", CreateMode.File);
                IFileEntry file2 = fs.CreateFile(volDir, "FILE2", CreateMode.File);

                Helper.ExpectString("FILE0", file0.FileName, "wrong filename");
                byte[] raw = file0.RawFileName;
                if (raw.Length != 5 || Encoding.ASCII.GetString(raw) != "FILE0") {
                    throw new Exception("Incorrect raw filename");
                }
                Helper.ExpectByte(FileAttribs.FILE_TYPE_PDA, file0.FileType, "wrong default type");
                Helper.ExpectInt(0, file0.AuxType, "wrong aux type");
                if (file0.CreateWhen != TimeStamp.NO_DATE) {
                    throw new Exception("Incorrect create date");
                }

                DateTime testDate = new DateTime(1977, 06, 01);
                file1.FileName = "New/FILE1";
                file1.FileType = FileAttribs.FILE_TYPE_FOT;
                file1.ModWhen = testDate;
                if (file1.FileName != "NEW/FILE1" || file1.FileType != FileAttribs.FILE_TYPE_FOT ||
                        file1.ModWhen != testDate) {
                    throw new Exception("file attribute changes didn't take");
                }
                try {
                    file1.FileName = "BAD NAME";
                    throw new Exception("Bad name accepted");
                } catch (ArgumentException) { /*expected*/ }

                fs.MoveFile(file2, volDir, "NEW^FILE2");
                Helper.ExpectString("NEW^FILE2", file2.FileName, "MoveFile rename failed");

                volDir.FileName = "MY~Vol";
                volDir.ModWhen = testDate;
                if (volDir.FileName != "MY~VOL" || volDir.ModWhen != testDate) {
                    throw new Exception("volume attribute changes didn't take");
                }

                // Bounce the filesystem without calling SaveChanges().
                fs.PrepareRawAccess();
                fs.PrepareFileAccess(true);
                Helper.CheckNotes(fs, 0, 0);

                volDir = fs.GetVolDirEntry();
                file1 = fs.FindFileEntry(volDir, "NEW/FILE1");
                if (file1.FileType != FileAttribs.FILE_TYPE_FOT || file1.ModWhen != testDate) {
                    throw new Exception("file attribute changes were lost");
                }
                if (volDir.FileName != "MY~VOL" || volDir.ModWhen != testDate) {
                    throw new Exception("volume attribute changes were lost");
                }
            }
        }

        // Create files of various sizes, one byte at a time, and confirm that they retain
        // their size and contents.
        public static void TestByteRW(AppHook appHook) {
            using (IFileSystem fs = Make525Floppy("RdWr", appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();
                IFileEntry file0 = fs.CreateFile(volDir, "FILE_0", CreateMode.File);
                Helper.CheckFill(fs, file0, 0);
                Helper.ExpectLong(BLOCK_SIZE, file0.StorageSize, "wrong storage size");

                IFileEntry file1 = fs.CreateFile(volDir, "FILE_1", CreateMode.File);
                Helper.FillFile(fs, file1, 1);
                Helper.CheckFill(fs, file1, 1);
                Helper.ExpectLong(BLOCK_SIZE, file1.StorageSize, "wrong storage size");

                IFileEntry file511 = fs.CreateFile(volDir, "FILE_511", CreateMode.File);
                Helper.FillFile(fs, file511, 511);
                Helper.CheckFill(fs, file511, 511);
                Helper.ExpectLong(BLOCK_SIZE, file511.StorageSize, "wrong storage size");

                IFileEntry file512 = fs.CreateFile(volDir, "FILE_512", CreateMode.File);
                Helper.FillFile(fs, file512, 512);
                Helper.CheckFill(fs, file512, 512);
                Helper.ExpectLong(BLOCK_SIZE, file512.StorageSize, "wrong storage size");

                IFileEntry file513 = fs.CreateFile(volDir, "FILE_513", CreateMode.File);
                Helper.FillFile(fs, file513, 513);
                Helper.CheckFill(fs, file513, 513);
                Helper.ExpectLong(BLOCK_SIZE * 2, file513.StorageSize, "wrong storage size");

                IFileEntry file4096 = fs.CreateFile(volDir, "FILE_4096", CreateMode.File);
                Helper.FillFile(fs, file4096, 4096);
                Helper.CheckFill(fs, file4096, 4096);
                Helper.ExpectLong(BLOCK_SIZE * 8, file4096.StorageSize, "wrong storage size");

                fs.PrepareRawAccess();
                fs.PrepareFileAccess(true);
                Helper.CheckNotes(fs, 0, 0);
                volDir = fs.GetVolDirEntry();

                Helper.CheckFill(fs, fs.FindFileEntry(volDir, "FILE_0"), 0);
                Helper.CheckFill(fs, fs.FindFileEntry(volDir, "FILE_1"), 1);
                Helper.CheckFill(fs, fs.FindFileEntry(volDir, "FILE_511"), 511);
                Helper.CheckFill(fs, fs.FindFileEntry(volDir, "FILE_512"), 512);
                Helper.CheckFill(fs, fs.FindFileEntry(volDir, "FILE_513"), 513);
                Helper.CheckFill(fs, fs.FindFileEntry(volDir, "FILE_4096"), 4096);

                IFileEntry overFill = fs.CreateFile(volDir, "OVER!FILL", CreateMode.File);
                try {
                    Helper.FillFile(fs, overFill, 140 * 1024);
                } catch (DiskFullException) { /*expected*/ }

                Helper.ExpectLong(0, fs.FreeSpace, "some space still free");

                // Delete a one-block file that is sandwiched between other files, create a new
                // one, write 512 bytes into it to verify that we don't over-allocate.
                file511 = fs.FindFileEntry(volDir, "FILE_511");
                Helper.ExpectLong(BLOCK_SIZE, file511.StorageSize, "wrong storage size");
                fs.DeleteFile(file511);
                IFileEntry new512 = fs.CreateFile(volDir, "FILE_512_NEW", CreateMode.File);
                Helper.FillFile(fs, new512, 512);
                Helper.CheckFill(fs, new512, 512);

                // Try to append another byte.  We should get "disk full".
                using (Stream stream = fs.OpenFile(new512, FileAccessMode.ReadWrite,
                        FilePart.DataFork)) {
                    stream.Position = 512;
                    try {
                        stream.WriteByte(0xaa);
                        throw new Exception("expanded file into next file");
                    } catch (DiskFullException) { /*expected*/ }
                }
            }
        }

        // Test different ways of changing the file length.
        public static void TestLengthChange(AppHook appHook) {
            const int TEST_SIZE = 16384;
            using (IFileSystem fs = Make525Floppy("@LENGT%", appHook)) {
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

                    // Truncate/expand file, confirm additional space is zeroed.
                    const int PARTIAL = 768;        // halfway into second block
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
                        if (readBuf[i] != 0) {
                            throw new Exception("Truncation didn't zero out data");
                        }
                    }

                    // Seek past the end and write a byte.  This may or may not zero the
                    // intervening storage (currently it doesn't, but it's not required to).
                    stream.SetLength(0);
                    Helper.ExpectLong(0, stream.Length, "incorrect trunc stream len");
                    stream.Flush();
                    Helper.ExpectLong(0, file1.DataLength, "incorrect truncated length");
                    Helper.ExpectLong(BLOCK_SIZE, file1.StorageSize, "incorrect trunc size");

                    stream.Position = 0;
                    stream.WriteByte(0xaa);
                    Helper.ExpectLong(1, stream.Length, "incorrect stream length (1)");
                    stream.Flush();
                    Helper.ExpectLong(1, file1.DataLength, "incorrect file length (1)");

                    stream.Position = TEST_SIZE - 1;
                    stream.WriteByte(0xff);
                    Helper.ExpectLong(TEST_SIZE, stream.Length, "incorrect stream length (TS)");
                    stream.Flush();
                    Helper.ExpectLong(TEST_SIZE, file1.DataLength, "incorrect file length (TS)");

                    // Try an excessively large length extension.  When it fails, the length of
                    // the file and the stream should be unchanged.
                    try {
                        stream.SetLength(140 * 1024);
                        throw new Exception("extended file length beyond disk limits");
                    } catch (DiskFullException) { /*expected*/ }
                    Helper.ExpectLong(TEST_SIZE, stream.Length, "bad setlen altered stream len");
                    Helper.ExpectLong(TEST_SIZE, file1.DataLength, "bad setlen altered file len");
                }
            }
        }

        public static void TestVolDirFull(AppHook appHook) {
            const int MAX_FILE_COUNT = 77;      // floor(2048/26) - 1
            using (IFileSystem fs = Make525Floppy("FILLER", appHook)) {
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

        public static void TestAdjust(AppHook appHook) {
            Helper.ExpectString("HELLO",
                Pascal_FileEntry.AdjustFileName("HELLO"), "err");
            Helper.ExpectString("NAME_TO..G.TEXT",
                Pascal_FileEntry.AdjustFileName("NAME_TOO_LONG.TEXT"), "err");
            Helper.ExpectString("NO_SPACES_PLS",
                Pascal_FileEntry.AdjustFileName("No Spaces Pls"), "err");
            Helper.ExpectString("________",
                Pascal_FileEntry.AdjustFileName("$=?, #:]"), "err");

            Helper.ExpectString("VER..RT",
                Pascal_FileEntry.AdjustVolumeName("Very, very Short"), "err");
        }

        public static void TestDefragment(AppHook appHook) {
            using (IFileSystem fs = Make525Floppy("FILLER", appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();
                // Create some files.  Use increasing sizes so that the destination range
                // overlaps with the source range.
                IFileEntry file1 = fs.CreateFile(volDir, "FILE1", CreateMode.File);
                Helper.PopulateFile(fs, file1, 0x11, 8, 0);
                IFileEntry file2 = fs.CreateFile(volDir, "FILE2", CreateMode.File);
                Helper.PopulateFile(fs, file2, 0x22, 10, 0);
                IFileEntry file3 = fs.CreateFile(volDir, "FILE3", CreateMode.File);
                Helper.PopulateFile(fs, file3, 0x33, 12, 0);
                IFileEntry file4 = fs.CreateFile(volDir, "FILE4", CreateMode.File);
                Helper.PopulateFile(fs, file4, 0x44, 14, 0);
                IFileEntry file5 = fs.CreateFile(volDir, "FILE5", CreateMode.File);
                Helper.PopulateFile(fs, file5, 0x55, 14, 0);

                fs.DeleteFile(file1);
                fs.DeleteFile(file3);

                long freeSpace = fs.FreeSpace;

                IFileEntry filler = fs.CreateFile(volDir, "filler", CreateMode.File);
                using (Stream stream = fs.OpenFile(filler, FileAccessMode.ReadWrite,
                        FilePart.DataFork)) {
                    try {
                        stream.SetLength(freeSpace);
                        throw new Exception("disk wasn't fragmented?");
                    } catch (DiskFullException) { /*expected*/ }
                }
                fs.DeleteFile(filler);

                fs.PrepareRawAccess();
                if (!((Pascal)fs).Defragment()) {
                    throw new Exception("Unable to defragment");
                }

                fs.PrepareFileAccess(true);
                Helper.CheckNotes(fs, 0, 0);
                Helper.ExpectLong(freeSpace, fs.FreeSpace, "free space changed");
                volDir = fs.GetVolDirEntry();

                // If we defragmented successfully, we should be able to create a single file
                // that fills all of the free space.
                filler = fs.CreateFile(volDir, "filler", CreateMode.File);
                using (Stream stream = fs.OpenFile(filler, FileAccessMode.ReadWrite,
                        FilePart.DataFork)) {
                    stream.SetLength(freeSpace);
                }
                Helper.ExpectLong(0, fs.FreeSpace, "didn't fill space");

                // Verify the contents of the files that were moved.
                file2 = fs.FindFileEntry(volDir, "FILE2");
                Helper.CheckPopulatedFile(fs, file2, 0x22, 10, 0);
                file4 = fs.FindFileEntry(volDir, "FILE4");
                Helper.CheckPopulatedFile(fs, file4, 0x44, 14, 0);
                file5 = fs.FindFileEntry(volDir, "FILE5");
                Helper.CheckPopulatedFile(fs, file5, 0x55, 14, 0);

                // Try it on a completely full volume.
                fs.PrepareRawAccess();
                if (!((Pascal)fs).Defragment()) {
                    throw new Exception("Unable to defragment");
                }
            }
        }


        #region Utilities

        private static IFileSystem Make525Floppy(string volName, AppHook appHook) {
            return Helper.CreateTestImage(volName, FileSystemType.Pascal, 280, appHook,
                out MemoryStream memFile);
        }

        #endregion Utilities
    }
}
