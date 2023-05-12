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
using System.IO;

using CommonUtil;
using DiskArc;
using static DiskArc.Defs;
using static DiskArc.IFileSystem;

namespace DiskArcTests {
    /// <summary>
    /// Tests basic features of ProDOS filesystem implementation.  All tests are done with
    /// disk images created by ProDOS 8 or the ProDOS FST, so we can verify that we can
    /// handle images other than the ones we create ourselves.
    /// </summary>
    public class TestProDOS_Prefab : ITest {
        private const bool DO_SCAN = true;

        public static void TestSimpleSparse(AppHook appHook) {
            using (Stream dataFile = Helper.OpenTestFile("prodos/simple-sparse.po", true,
                    appHook)) {
                using (IDiskImage diskImage = FileAnalyzer.PrepareDiskImage(dataFile,
                        FileKind.UnadornedSector, appHook)!) {
                    diskImage.AnalyzeDisk();
                    IFileSystem fs = (IFileSystem)diskImage.Contents!;
                    fs.PrepareFileAccess(DO_SCAN);
                    Helper.CheckNotes(fs, 0, 0);

                    TestInterface(fs);
                    TestSizes(fs);
                    TestDir(fs);
                    TestSparse(fs);
                }
            }
        }

        private static List<Helper.FileAttr> sSizeTests = new List<Helper.FileAttr>() {
            new Helper.FileAttr("L0", 0, -1, 1 * BLOCK_SIZE, FileAttribs.FILE_TYPE_BIN, 0x2000),
            new Helper.FileAttr("L1", 1, 1 * BLOCK_SIZE),
            new Helper.FileAttr("L2", 2, 1 * BLOCK_SIZE),
            new Helper.FileAttr("L511", 511, 1 * BLOCK_SIZE),
            new Helper.FileAttr("L512", 512, 1 * BLOCK_SIZE),
            new Helper.FileAttr("L513", 513, 3 * BLOCK_SIZE),
            new Helper.FileAttr("L8192", 8192, 17 * BLOCK_SIZE),
            new Helper.FileAttr("L131072", 131072, 257 * BLOCK_SIZE),
            new Helper.FileAttr("L131073", 131073, 260 * BLOCK_SIZE),
        };

        private const char FSSEP = ':';

        private static void TestInterface(IFileSystem fs) {
            // Pick a file at random.
            IFileEntry entry = Helper.EntryFromPathThrow(fs, "SIZES" + FSSEP + "L512", FSSEP);

            // Open file correctly...
            using (DiskFileStream desc = fs.OpenFile(entry, FileAccessMode.ReadOnly,
                    FilePart.DataFork)) {
                // ...then try to open it again.
                fs.OpenFile(entry, FileAccessMode.ReadOnly, FilePart.DataFork);
            }

            // Open resource fork of non-extended file.
            try {
                fs.OpenFile(entry, FileAccessMode.ReadOnly, FilePart.RsrcFork);
                throw new Exception("File does not have a resource fork");
            } catch (IOException) {
                // expected
            }

            // Open file read-write on read-only filesystem.
            try {
                fs.OpenFile(entry, FileAccessMode.ReadWrite, FilePart.DataFork);
                throw new Exception("Should not be allowed to open file read-write");
            } catch (IOException) {
                // expected
            }
        }

        private static void TestSizes(IFileSystem fs) {
            // Confirm that the expected files are present and have the correct attributes.
            IFileEntry dirEntry = Helper.EntryFromPathThrow(fs, "SIZES", FSSEP);
            Helper.ValidateDirContents(dirEntry, sSizeTests);

            DoSizeTest(fs, 1, 2);
            DoSizeTest(fs, 512, 1024);
            DoSizeTest(fs, 8193, 16385);
        }

        private static void DoSizeTest(IFileSystem fs, int count1, int count2) {
            const int MAX_SIZE = 131073;
            byte[] testPattern = Helper.SeqByteTestPattern(MAX_SIZE);
            byte[] buffer = new byte[MAX_SIZE];

            foreach (Helper.FileAttr attrib in sSizeTests) {
                Array.Clear(buffer);

                string pathName = "SIZES:" + attrib.FileName;
                IFileEntry entry = Helper.EntryFromPathThrow(fs, pathName, FSSEP);

                using (DiskFileStream desc = fs.OpenFile(entry, FileAccessMode.ReadOnly,
                        FilePart.DataFork)) {
                    int actual = Helper.ReadFileChunky(desc, buffer, count1, count2);
                    if (actual != attrib.DataLength) {
                        throw new Exception("Failed to read entire file: got " + actual +
                            ", expected " + attrib.DataLength);
                    }
                }
                if (!RawData.CompareBytes(testPattern, 0, buffer, 0, attrib.DataLength)) {
                    throw new Exception("Data read did not match test pattern");
                }
            }
        }

        private static void TestDir(IFileSystem fs) {
            IFileEntry dirEntry = Helper.EntryFromPathThrow(fs, "SIZES", FSSEP);
            using (DiskFileStream desc = fs.OpenFile(dirEntry, FileAccessMode.ReadOnly,
                    FilePart.DataFork)) {
                int len = (int)dirEntry.DataLength + 15;
                byte[] buffer = new byte[len];
                // Read it in two parts, just because.
                int actual = desc.Read(buffer, 0, len / 2);
                actual += desc.Read(buffer, actual, len - actual);
                if (actual != dirEntry.DataLength) {
                    throw new Exception("Dir read len was " + actual + ", expected " +
                        dirEntry.DataLength);
                }
                if (RawData.GetU16LE(buffer, 0) != 0 ||
                        (buffer[0x04] >> 4) != (int)DiskArc.FS.ProDOS.StorageType.SubdirHeader) {
                    throw new Exception("Doesn't look like a dir header");
                }

                // TODO(maybe): walk through the directory to confirm we got it all.
            }
        }

        private static List<Helper.FileAttr> sSparseTests = new List<Helper.FileAttr>() {
            new Helper.FileAttr("Max.Seedling", 16777215, 1 * BLOCK_SIZE),
            new Helper.FileAttr("Max.Sapling", 16777215, 3 * BLOCK_SIZE),
            new Helper.FileAttr("MIN.MAX.TREE", 16777215, 5 * BLOCK_SIZE),
            new Helper.FileAttr("SPARSE.BIN", 10241, 12 * BLOCK_SIZE),
            new Helper.FileAttr("RANDOM.TXT", 18434, 11 * BLOCK_SIZE),
        };

        private static void TestSparse(IFileSystem fs) {
            // Confirm that the expected files are present and have the correct attributes.
            IFileEntry dirEntry = Helper.EntryFromPathThrow(fs, "SPARSE", FSSEP);
            Helper.ValidateDirContents(dirEntry, sSparseTests);

            TestBigSparseFile(fs);
            TestSparseChunk(fs);
        }

        private static void TestBigSparseFile(IFileSystem fs) {
            const int maxLen = DiskArc.FS.ProDOS.MAX_FILE_LEN;
            byte[] readBuffer = new byte[maxLen];
            byte[] cmpBuffer = new byte[maxLen];

            IFileEntry entry = Helper.EntryFromPathThrow(fs, "SPARSE:Max.Seedling", FSSEP);
            using (DiskFileStream desc = fs.OpenFile(entry, FileAccessMode.ReadOnly,
                    FilePart.DataFork)) {
                int actual = desc.Read(readBuffer, 0, maxLen);
                if (actual != maxLen) {
                    throw new Exception("Partial read in Max.Seedling");
                }
                // Should be nothing but zeroes.
                if (!RawData.CompareBytes(readBuffer, cmpBuffer, cmpBuffer.Length)) {
                    throw new Exception("Bad data in Max.Seedling");
                }

                // First block holds data, rest is a hole.
                Helper.ExpectLong(0, desc.Seek(0, SeekOrigin.Begin), "seedling seek start");
                Helper.ExpectLong(0, desc.Seek(0, SEEK_ORIGIN_DATA), "seedling seek data-0");
                Helper.ExpectLong(0x000200, desc.Seek(0, SEEK_ORIGIN_HOLE), "seedling seek hole");
                Helper.ExpectLong(maxLen, desc.Seek(0x000400, SEEK_ORIGIN_DATA), "seedling seek data-max");
                Helper.ExpectLong(maxLen, desc.Seek(maxLen, SEEK_ORIGIN_HOLE), "seedling seek hole-max");
            }

            entry = Helper.EntryFromPathThrow(fs, "SPARSE:Max.Sapling", FSSEP);
            using (DiskFileStream desc = fs.OpenFile(entry, FileAccessMode.ReadOnly,
                    FilePart.DataFork)) {
                int actual = desc.Read(readBuffer, 0, maxLen);
                if (actual != maxLen) {
                    throw new Exception("Partial read in Max.Sapling");
                }
                // One nonzero value, at +0x000200.
                cmpBuffer[0x200] = 207;
                if (!RawData.CompareBytes(readBuffer, cmpBuffer, cmpBuffer.Length)) {
                    throw new Exception("Bad data in Max.Sapling");
                }

                // First two blocks hold data, rest is a hole.
                Helper.ExpectLong(0, desc.Seek(-maxLen, SeekOrigin.Current), "sapling seek start");
                Helper.ExpectLong(0x000400, desc.Seek(0, SEEK_ORIGIN_HOLE), "sapling seek hole");
                Helper.ExpectLong(maxLen, desc.Seek(0x000400, SEEK_ORIGIN_DATA), "seedling seek data-max");
            }
            cmpBuffer[0x200] = 0;

            entry = Helper.EntryFromPathThrow(fs, "SPARSE:MIN.MAX.TREE", FSSEP);
            using (DiskFileStream desc = fs.OpenFile(entry, FileAccessMode.ReadOnly,
                    FilePart.DataFork)) {
                int actual = desc.Read(readBuffer, 0, maxLen);
                if (actual != maxLen) {
                    throw new Exception("Partial read in MIN.MAX.TREE");
                }
                // One nonzero value, at +0xfffffe.
                cmpBuffer[0xfffffe] = 100;
                if (!RawData.CompareBytes(readBuffer, cmpBuffer, cmpBuffer.Length)) {
                    throw new Exception("Bad data in MIN.MAX.TREE");
                }

                // First and last blocks hole data, rest is a hole.
                Helper.ExpectLong(0, desc.Seek(-maxLen, SeekOrigin.End), "tree seek start");
                Helper.ExpectLong(0, desc.Seek(0, SEEK_ORIGIN_DATA), "tree seek data-0");
                Helper.ExpectLong(0x000200, desc.Seek(0, SEEK_ORIGIN_HOLE), "tree seek hole");
                Helper.ExpectLong(0xfffe00, desc.Seek(0x000400, SEEK_ORIGIN_DATA), "tree seek data-far");
                Helper.ExpectLong(maxLen, desc.Seek(0xfffe00, SEEK_ORIGIN_HOLE), "tree seek hole-max");
            }
        }

        private static void TestSparseChunk(IFileSystem fs) {
            IFileEntry entry = Helper.EntryFromPathThrow(fs, "SPARSE:SPARSE.BIN", FSSEP);
            byte[] buffer = new byte[2];
            using (DiskFileStream desc = fs.OpenFile(entry, FileAccessMode.ReadOnly,
                    FilePart.DataFork)) {
                long posn = 0;
                for (int i = 0; i < 10; i++) {
                    desc.Read(buffer, 1, 1);
                    if (buffer[1] != i) {
                        throw new Exception("Bad value in sparse bin, i=" + i + ", val=" +
                            buffer[1]);
                    }

                    // Find the next hole (at the end of this block).
                    posn = desc.Seek(posn, SEEK_ORIGIN_HOLE);
                    // Find the next bit of data.
                    posn = desc.Seek(posn, SEEK_ORIGIN_DATA);
                }
            }

            entry = Helper.EntryFromPathThrow(fs, "SPARSE:RANDOM.TXT", FSSEP);
            using (DiskFileStream desc = fs.OpenFile(entry, FileAccessMode.ReadOnly,
                    FilePart.DataFork)) {
                long posn = 0;
                for (int i = 0; i < 9; i++) {
                    desc.Read(buffer, 0, 2);
                    if (buffer[0] != i + 0x30 || buffer[1] != 0x0d) {
                        throw new Exception("Bad value in random txt, i=" + i + ", val=" +
                            buffer[0]);
                    }

                    // Find the next hole (at the end of this block).
                    posn = desc.Seek(posn, SEEK_ORIGIN_HOLE);
                    // Find the next bit of data.
                    posn = desc.Seek(posn, SEEK_ORIGIN_DATA);
                }
            }
        }

        public static void TestExtended(AppHook appHook) {
            using (Stream dataFile = Helper.OpenTestFile("prodos/extended.do", true, appHook)) {
                using (IDiskImage diskImage = FileAnalyzer.PrepareDiskImage(dataFile,
                        FileKind.UnadornedSector, appHook)!) {
                    diskImage.AnalyzeDisk(null, SectorOrder.ProDOS_Block,
                        IDiskImage.AnalysisDepth.Full);   // wrong order
                    IFileSystem fs = (IFileSystem)diskImage.Contents!;
                    fs.PrepareFileAccess(DO_SCAN);
                    Helper.CheckNotes(fs, 0, 0);
                    TestHfsTypes(fs);
                }
            }
        }

        // Confirm HFS types are extracted from the extended info block.
        private static void TestHfsTypes(IFileSystem fs) {
            IFileEntry volDir = fs.GetVolDirEntry();
            IFileEntry extFile = fs.FindFileEntry(volDir, "ExtText");
            if (extFile.FileType != FileAttribs.FILE_TYPE_TXT ||
                    extFile.AuxType != 0x0000 ||
                    extFile.HFSCreator != 0x74747874 ||
                    extFile.HFSFileType != 0x54455854) {
                throw new Exception("Incorrect type");
            }
        }
    }
}
