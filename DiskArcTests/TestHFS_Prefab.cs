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
    /// Tests basic features of HFS filesystem implementation.  Tests are done with disk images
    /// created by the GS/OS FST, libhfs, or the Linux HFS implementation.
    /// </summary>
    public class TestHFS_Prefab : ITest {
        private const char FSSEP = ':';

        public static void TestSizes(AppHook appHook) {
            using (Stream dataFile = Helper.OpenTestFile("hfs/simple-gsos.po", true, appHook)) {
                using (IDiskImage diskImage = FileAnalyzer.PrepareDiskImage(dataFile,
                        FileKind.UnadornedSector, appHook)!) {
                    diskImage.AnalyzeDisk();
                    IFileSystem fs = (IFileSystem)diskImage.Contents!;
                    fs.PrepareFileAccess(true);

                    DoInterfaceTest(fs);

                    // Confirm that the expected files are present and have the correct attributes.
                    IFileEntry dirEntry = Helper.EntryFromPathThrow(fs, "SIZES", FSSEP);
                    Helper.ValidateDirContents(dirEntry, sSizeTests);

                    DoSizeTest(fs, 1, 2);
                    DoSizeTest(fs, 512, 1024);
                    DoSizeTest(fs, 8193, 16385);
                }
            }
        }

        private static void DoInterfaceTest(IFileSystem fs) {
            // Pick a file at random.
            IFileEntry entry = Helper.EntryFromPathThrow(fs, "SIZES" + FSSEP + "L512", FSSEP);

            // Open file correctly...
            using (DiskFileStream desc = fs.OpenFile(entry, FileAccessMode.ReadOnly,
                    FilePart.DataFork)) {
                // ...then try to open it again.
                fs.OpenFile(entry, FileAccessMode.ReadOnly, FilePart.DataFork);
            }

            // Open resource fork.  (All HFS files have a resource fork.)
            using (DiskFileStream desc = fs.OpenFile(entry, FileAccessMode.ReadOnly,
                    FilePart.RsrcFork)) {
                // do nothing
            }

            // Open file read-write on read-only filesystem.
            try {
                fs.OpenFile(entry, FileAccessMode.ReadWrite, FilePart.DataFork);
                throw new Exception("Should not be allowed to open file read-write");
            } catch (IOException) {
                // expected
            }
        }

        private static List<Helper.FileAttr> sSizeTests = new List<Helper.FileAttr>() {
            new Helper.FileAttr("L0", 0, 0 * BLOCK_SIZE),
            new Helper.FileAttr("L1", 1, 1 * BLOCK_SIZE),
            new Helper.FileAttr("L131072", 131072, 256 * BLOCK_SIZE),
            new Helper.FileAttr("L131073", 131073, 257 * BLOCK_SIZE),
            new Helper.FileAttr("L2", 2, 1 * BLOCK_SIZE),
            new Helper.FileAttr("L511", 511, 1 * BLOCK_SIZE),
            new Helper.FileAttr("L512", 512, 1 * BLOCK_SIZE),
            new Helper.FileAttr("L513", 513, 2 * BLOCK_SIZE),
            new Helper.FileAttr("L8192", 8192, 16 * BLOCK_SIZE),
        };

        private static void DoSizeTest(IFileSystem fs, int count1, int count2) {
            const int MAX_SIZE = 131073;
            byte[] testPattern = Helper.SeqByteTestPattern(MAX_SIZE);
            byte[] buffer = new byte[MAX_SIZE];

            foreach (Helper.FileAttr attrib in sSizeTests) {
                Array.Clear(buffer);

                string pathName = "SIZES" + FSSEP + attrib.FileName;
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

        public static void TestChunks(AppHook appHook) {
            using (Stream dataFile = Helper.OpenTestFile("hfs/simple-linux.po", true, appHook)) {
                using (IDiskImage diskImage = FileAnalyzer.PrepareDiskImage(dataFile,
                        FileKind.UnadornedSector, appHook)!) {
                    diskImage.AnalyzeDisk();
                    IFileSystem fs = (IFileSystem)diskImage.Contents!;

                    const int NUM_CHUNKS = 38;      // see chunks.cpp (on the HFS volume)

                    fs.PrepareFileAccess(true);

                    // Confirm that the expected files are present and have the correct attributes.
                    IFileEntry volDir = fs.GetVolDirEntry();
                    IFileEntry file1 = fs.FindFileEntry(volDir, "chunks1");
                    IFileEntry file2 = fs.FindFileEntry(volDir, "chunks2");

                    if (file1.DataLength != NUM_CHUNKS * BLOCK_SIZE ||
                            file2.DataLength != NUM_CHUNKS * BLOCK_SIZE) {
                        throw new Exception("Unexpected file lengths");
                    }

                    DiskFileStream fd1 = fs.OpenFile(file1, FileAccessMode.ReadOnly, FilePart.DataFork);
                    DiskFileStream fd2 = fs.OpenFile(file2, FileAccessMode.ReadOnly, FilePart.DataFork);

                    byte[] blockBuf = new byte[BLOCK_SIZE];
                    for (int i = 0; i < NUM_CHUNKS; i++) {
                        if (fd1.Read(blockBuf, 0, BLOCK_SIZE) != BLOCK_SIZE) {
                            throw new Exception("Short read A");
                        }
                        if (blockBuf[0] != 'A' || blockBuf[1] != i) {
                            throw new Exception("Unexpected data found (A" + i + "): '" +
                                (char)blockBuf[0] + "' " + blockBuf[1]);
                        }

                        if (fd2.Read(blockBuf, 0, BLOCK_SIZE) != BLOCK_SIZE) {
                            throw new Exception("Short read B");
                        }
                        if (blockBuf[0] != 'B' || blockBuf[1] != i) {
                            throw new Exception("Unexpected data found (B" + i + "): '" +
                                (char)blockBuf[0] + "' " + blockBuf[1]);
                        }
                    }

                    fd1.Close();
                    fd2.Close();
                }
            }
        }
    }
}
