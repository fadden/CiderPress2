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

using CommonUtil;
using DiskArc;
using static DiskArc.Defs;
using static DiskArc.IFileSystem;

namespace DiskArcTests {
    /// <summary>
    /// Tests basic features of DOS filesystem implementation.  All tests are done with
    /// disk images created by DOS 3.3 in an emulator.
    /// </summary>
    public class TestDOS_Prefab : ITest {
        private const bool DO_SCAN = true;
        private const char FSSEP = IFileEntry.NO_DIR_SEP;

        private const int BAS_AUX = 0x0801;
        private static List<Helper.FileAttr> sSizeTests = new List<Helper.FileAttr>() {
            new Helper.FileAttr("HELLO", 37, -1, 2 * SECTOR_SIZE, FileAttribs.FILE_TYPE_BAS, BAS_AUX),
            new Helper.FileAttr("BAS BIG", 5762, -1, 24 * SECTOR_SIZE, FileAttribs.FILE_TYPE_BAS, BAS_AUX),
            new Helper.FileAttr("BAS OVERSIZED", 12, -1, 24 * SECTOR_SIZE, FileAttribs.FILE_TYPE_BAS, BAS_AUX),
            new Helper.FileAttr("BAS SMALL", 12, -1, 2 * SECTOR_SIZE, FileAttribs.FILE_TYPE_BAS, BAS_AUX),
            new Helper.FileAttr("MK-SPARSE-TEXT", 323, -1, 3 * SECTOR_SIZE, FileAttribs.FILE_TYPE_BAS, BAS_AUX),
            new Helper.FileAttr("SPARSE-TEXT", 0, -1, 9 * SECTOR_SIZE, FileAttribs.FILE_TYPE_TXT, 0x0000),
            new Helper.FileAttr("MK-BIG BIN", 255, -1, 3 * SECTOR_SIZE, FileAttribs.FILE_TYPE_BAS, BAS_AUX),
            new Helper.FileAttr("BIG BIN", 8184, -1, 33 * SECTOR_SIZE, FileAttribs.FILE_TYPE_BIN, 0x2000),
            new Helper.FileAttr("OVERSIZED BIN", 8, -1, 33 * SECTOR_SIZE, FileAttribs.FILE_TYPE_BIN, 0x2000),
            new Helper.FileAttr("SMALL BIN", 8, -1, 2 * SECTOR_SIZE, FileAttribs.FILE_TYPE_BIN, 0x2000),
            new Helper.FileAttr("MK-TXT", 547, -1, 4 * SECTOR_SIZE, FileAttribs.FILE_TYPE_BAS, BAS_AUX),
            new Helper.FileAttr("TXT SMALL", 23, -1, 2 * SECTOR_SIZE, FileAttribs.FILE_TYPE_TXT, 0x0000),
            new Helper.FileAttr("TXT BIG", 2790, -1, 12 * SECTOR_SIZE, FileAttribs.FILE_TYPE_TXT, 0x0000),
            new Helper.FileAttr("TXT NOTRIM", 2790, -1, 12 * SECTOR_SIZE, FileAttribs.FILE_TYPE_TXT, 0x0000),
            new Helper.FileAttr("CASE TEST", 1, -1, 2 * SECTOR_SIZE, FileAttribs.FILE_TYPE_BIN, 0x0000),
            new Helper.FileAttr("case test", 2, -1, 2 * SECTOR_SIZE, FileAttribs.FILE_TYPE_BIN, 0x0000),
        };

        public static void TestSimpleSparse(AppHook appHook) {
            using (Stream dataFile = Helper.OpenTestFile("dos33/simple-sparse.do", true, appHook)) {
                using (IDiskImage diskImage = FileAnalyzer.PrepareDiskImage(dataFile,
                        FileKind.UnadornedSector, appHook)!) {
                    diskImage.AnalyzeDisk();
                    IFileSystem fs = (IFileSystem)diskImage.Contents!;
                    fs.PrepareFileAccess(DO_SCAN);
                    Helper.CheckNotes(fs, 0, 0);

                    IFileEntry volDir = fs.GetVolDirEntry();
                    Helper.ValidateDirContents(volDir, sSizeTests);

                    TestInterface(fs);
                    TestSimpleRead(fs);
                    TestSparse(fs);
                }
            }
        }

        private static void TestInterface(IFileSystem fs) {
            // Pick a nice file.
            IFileEntry entry = Helper.EntryFromPathThrow(fs, "BAS OVERSIZED", FSSEP);

            // Open file correctly...
            using (DiskFileStream desc = fs.OpenFile(entry, FileAccessMode.ReadOnly,
                    FilePart.DataFork)) {
                // ...then try to open it again.
                fs.OpenFile(entry, FileAccessMode.ReadOnly, FilePart.DataFork);
                // ...and again in "raw" mode.
                fs.OpenFile(entry, FileAccessMode.ReadOnly, FilePart.RawData);
            }

            // Open resource fork of non-extended file.
            try {
                fs.OpenFile(entry, FileAccessMode.ReadOnly, FilePart.RsrcFork);
                throw new Exception("File does not have a resource fork");
            } catch (IOException) { /*expected*/ }

            // Open file read-write on read-only filesystem.
            try {
                fs.OpenFile(entry, FileAccessMode.ReadWrite, FilePart.DataFork);
                throw new Exception("Should not be allowed to open file read-write");
            } catch (IOException) { /*expected*/ }
        }

        // 100 TEXT : NORMAL : HOME
        private static byte[] sTextNormalHome = {
            0x0c, 0x00,     // length word
            0x0b, 0x08, 0x64, 0x00, 0x89, 0x3a, 0x9d, 0x3a, 0x97, 0x00, 0x00, 0x00
        };

        private static void TestSimpleRead(IFileSystem fs) {
            IFileEntry volDir = fs.GetVolDirEntry();
            IFileEntry file1 = fs.FindFileEntry(volDir, "BAS SMALL");
            IFileEntry file2 = fs.FindFileEntry(volDir, "BAS OVERSIZED");
            IFileEntry file3 = fs.FindFileEntry(volDir, "BAS BIG");

            byte[] buf1 = new byte[SECTOR_SIZE * 2];
            byte[] buf2 = new byte[SECTOR_SIZE * 2];
            byte[] buf3 = new byte[SECTOR_SIZE * 2];

            DiskFileStream fd1 = fs.OpenFile(file1, FileAccessMode.ReadOnly, FilePart.DataFork);
            DiskFileStream fd2 = fs.OpenFile(file2, FileAccessMode.ReadOnly, FilePart.DataFork);
            DiskFileStream fd3 = fs.OpenFile(file3, FileAccessMode.ReadOnly, FilePart.DataFork);

            int length1 = fd1.Read(buf1, 0, buf1.Length);
            int length2 = fd2.Read(buf2, 0, buf2.Length);
            int length3 = fd3.Read(buf3, 0, buf3.Length);

            // In "cooked" data mode, SMALL and OVERSIZED should be identical, but different
            // from BIG.
            if (length1 != length2 || length1 == length3) {
                throw new Exception("Unexpected lengths: " +
                    length1 + "/" + length2 + "/" + length3);
            }

            if (!RawData.CompareBytes(buf1, buf2, buf2.Length)) {
                throw new Exception("small / oversized don't match");
            }

            // Test against the expected value, offsetting by 2 to skip the BAS length word.
            if (!RawData.CompareBytes(sTextNormalHome, 2, buf1, 0, sTextNormalHome.Length - 2)) {
                throw new Exception("BAS SMALL contents not as expected");
            }
            // Try again, reading one byte at a time.
            fd1.Seek(0, SeekOrigin.Begin);
            for (int i = 2; i < sTextNormalHome.Length - 2; i++) {
                fd1.Read(buf1, 0, 1);
                if (buf1[0] != sTextNormalHome[i]) {
                    throw new Exception("Single byte read failed");
                }
            }

            fs.CloseAll();
            Array.Clear(buf1);
            Array.Clear(buf2);
            Array.Clear(buf3);

            // Try to use a closed descriptor.
            try {
                fd1.Seek(0, SeekOrigin.Begin);
                throw new Exception("Seek on closed fail succeeded");
            } catch (ObjectDisposedException) {
                // expected
            }

            // Now try SMALL and OVERSIZED in raw data mode.  SMALL should be a single sector,
            // OVERSIZED should be larger.
            fd1 = fs.OpenFile(file1, FileAccessMode.ReadOnly, FilePart.RawData);
            fd2 = fs.OpenFile(file2, FileAccessMode.ReadOnly, FilePart.RawData);
            length1 = fd1.Read(buf1, 0, buf1.Length);
            length2 = fd2.Read(buf2, 0, buf2.Length);
            if (length1 == length2 || length1 != SECTOR_SIZE) {
                throw new Exception("Unexpected raw lengths: " + length1 + "/" + length2);
            }
            if (RawData.CompareBytes(buf1, buf2, buf2.Length)) {
                throw new Exception("small / oversized raw match");
            }
            if (!RawData.CompareBytes(sTextNormalHome, 0, buf1, 0, sTextNormalHome.Length)) {
                throw new Exception("BAS SMALL raw contents not as expected");
            }

            fs.CloseAll();
        }

        private static void TestSparse(IFileSystem fs) {
            IFileEntry volDir = fs.GetVolDirEntry();
            IFileEntry file = fs.FindFileEntry(volDir, "SPARSE-TEXT");

            byte[] buf = new byte[SECTOR_SIZE];
            using (DiskFileStream fd = fs.OpenFile(file, FileAccessMode.ReadOnly,
                    FilePart.DataFork)) {
                if (fd.Read(buf, 0, buf.Length) != 0) {
                    throw new Exception("Read data from zeroed text file");
                }
            }

            // Random-access text file with 256-byte records.  Entries 2, 8, 122, and 488
            // were written.
            int[] sSparseRecs = new int[] { 2, 8, 122, 488 };
            using (DiskFileStream fd = fs.OpenFile(file, FileAccessMode.ReadOnly,
                    FilePart.RawData)) {
                long fileLength = fd.Seek(0, SeekOrigin.End);
                fd.Seek(0, SeekOrigin.Begin);
                if (fileLength != (488 + 1) * SECTOR_SIZE) {
                    throw new Exception("Unexpected raw data length: " + fileLength);
                }

                long fileOffset = 0;
                long newPos;

                int expIndex = 0;
                while (fileOffset < fileLength) {
                    newPos = fd.Seek(fileOffset, SEEK_ORIGIN_HOLE);
                    if (newPos != fileOffset) {
                        throw new Exception("Should have been in hole at " + fileOffset);
                    }

                    newPos = fd.Seek(fileOffset, SEEK_ORIGIN_DATA);
                    if (newPos == fileOffset) {
                        throw new Exception("Failed to move to data");
                    }
                    fileOffset = newPos;

                    Array.Clear(buf);
                    fd.Read(buf, 0, buf.Length / 2);    // don't read full sector

                    // The block index is printed into the record, followed by CR.
                    string foundStr = RawData.GetNullTermString(buf, 0, buf.Length, 0x7f);
                    string expStr = sSparseRecs[expIndex].ToString() + '\r';
                    if (foundStr != expStr) {
                        throw new Exception("Expected " + expStr + ", found " + foundStr);
                    }
                    expIndex++;

                    newPos = fd.Seek(fileOffset, SEEK_ORIGIN_HOLE);
                    if (newPos != fileOffset + SECTOR_SIZE) {
                        throw new Exception("Failed to move to next hole");
                    }
                    fileOffset = newPos;
                }
            }

            // The data/hole seek modes should only work in "cooked" mode.
            using (DiskFileStream fd =
                    fs.OpenFile(file, FileAccessMode.ReadOnly, FilePart.DataFork)) {
                long fileLength = fd.Seek(0, SeekOrigin.End);
                fd.Seek(0, SeekOrigin.Begin);
                if (fd.Seek(0, SEEK_ORIGIN_HOLE) != fileLength) {
                    throw new Exception("Found a hole in DataFork mode");
                }
            }
        }
    }
}
