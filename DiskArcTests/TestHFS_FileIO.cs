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
using System.IO;

using CommonUtil;
using DiskArc;
using static DiskArc.Defs;
using static DiskArc.IFileSystem;

namespace DiskArcTests {
    public class TestHFS_FileIO : ITest {
        private const int GRINDER_ITERATIONS = 500;     // increase to 50K-100K for full testing
        private const bool DO_SCAN = true;
        private static bool DUMP_TO_FILE = false;


        public static void TestGrind(AppHook appHook) {
            const int SEED = 12345;

            using (IFileSystem fs = Helper.CreateTestImage("Finely Ground Files",
                    FileSystemType.HFS, 65535, appHook, out MemoryStream memFile)) {
                long initialFree = fs.FreeSpace;
                Grinder grinder = new Grinder(fs, SEED);
                grinder.Execute(GRINDER_ITERATIONS);
                grinder.FinalCheck(initialFree);

                fs.PrepareRawAccess();
                if (DUMP_TO_FILE) {
                    CopyToFile(fs, "TEST-grinder-hfs");
                }
                fs.PrepareFileAccess(DO_SCAN);
                Helper.CheckNotes(fs, 0, 0);
            }
        }

        public static void TestFourOpen(AppHook appHook) {
            string FILE = "File";
            const int COUNT = 100;

            using (IFileSystem fs = Make35Floppy("Test4", appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();
                IFileEntry file1 = fs.CreateFile(volDir, FILE + "1", CreateMode.File);
                IFileEntry file2 = fs.CreateFile(volDir, FILE + "2", CreateMode.File);

                DiskFileStream file1d = fs.OpenFile(file1, FileAccessMode.ReadWrite, FilePart.DataFork);
                DiskFileStream file1r = fs.OpenFile(file1, FileAccessMode.ReadWrite, FilePart.RsrcFork);
                DiskFileStream file2d = fs.OpenFile(file2, FileAccessMode.ReadWrite, FilePart.DataFork);
                DiskFileStream file2r = fs.OpenFile(file2, FileAccessMode.ReadWrite, FilePart.RsrcFork);

                for (int i = 0; i < COUNT; i++) {
                    WriteTag(file1d, 1, false, i);
                    WriteTag(file1r, 1, true, i);
                    WriteTag(file2d, 2, false, i);
                    WriteTag(file2r, 2, true, i);
                }

                fs.CloseAll();

                fs.PrepareRawAccess();
                if (DUMP_TO_FILE) {
                    CopyToFile(fs, "TEST-four-open-1");
                }
                fs.PrepareFileAccess(DO_SCAN);
                Helper.CheckNotes(fs, 0, 0);

                volDir = fs.GetVolDirEntry();
                file1 = fs.FindFileEntry(volDir, FILE + "1");
                file2 = fs.FindFileEntry(volDir, FILE + "2");
                file1d = fs.OpenFile(file1, FileAccessMode.ReadOnly, FilePart.DataFork);
                file1r = fs.OpenFile(file1, FileAccessMode.ReadOnly, FilePart.RsrcFork);
                file2d = fs.OpenFile(file2, FileAccessMode.ReadOnly, FilePart.DataFork);
                file2r = fs.OpenFile(file2, FileAccessMode.ReadOnly, FilePart.RsrcFork);

                for (int i = 0; i < COUNT; i++) {
                    CheckTag(file1d, 1, false, i);
                    CheckTag(file1r, 1, true, i);
                    CheckTag(file2d, 2, false, i);
                    CheckTag(file2r, 2, true, i);
                }

                // let filesystem dispose close the files
            }
        }
        private static byte[] sTagBuf = new byte[6];
        private const int TAG_MULT = 503;
        private static void WriteTag(DiskFileStream fd, int fileNum, bool isRsrc, int iter) {
            sTagBuf[0] = (byte)fileNum;
            sTagBuf[1] = isRsrc ? (byte)1 : (byte)0;
            RawData.SetU32BE(sTagBuf, 2, (uint)iter);
            fd.Seek(TAG_MULT * iter, SeekOrigin.Begin);
            fd.Write(sTagBuf, 0, sTagBuf.Length);
        }
        private static void CheckTag(DiskFileStream fd, int fileNum, bool isRsrc, int iter) {
            fd.Seek(TAG_MULT * iter, SeekOrigin.Begin);
            fd.Read(sTagBuf, 0, sTagBuf.Length);
            int readFileNum = sTagBuf[0];
            bool readIsRsrc = sTagBuf[1] != 0;
            uint readIter = RawData.GetU32BE(sTagBuf, 2);
            if (fileNum != readFileNum || isRsrc != readIsRsrc || iter != readIter) {
                throw new Exception("Bad values read: fn=" + readFileNum + " isr=" + readIsRsrc +
                    " itr=" + readIter);
            }
        }


        // Verify that using SetLength() to expand a file zeroes out the new region.
        public static void TestExtendZero(AppHook appHook) {
            string FILE = "File";

            using (IFileSystem fs = Make35Floppy("TestEZ", appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();
                IFileEntry file1 = fs.CreateFile(volDir, FILE, CreateMode.File);

                byte[] buf = new byte[4000];
                RawData.MemSet(buf, 0, buf.Length, 0xcc);

                using (DiskFileStream fd = fs.OpenFile(file1, FileAccessMode.ReadWrite,
                        FilePart.DataFork)) {
                    const int KEEP = 8;
                    fd.Write(buf, 0, buf.Length);

                    // Truncate and re-extend the file.
                    fd.SetLength(KEEP);
                    fd.SetLength(buf.Length);
                    fd.Seek(0, SeekOrigin.Begin);

                    // Verify that the extended area is zeroed out.
                    Array.Clear(buf);
                    fd.Read(buf, 0, buf.Length);

                    for (int i = 0; i < buf.Length; i++) {
                        byte expected;
                        if (i < KEEP) {
                            expected = 0xcc;
                        } else {
                            expected = 0x00;
                        }
                        if (buf[i] != expected) {
                            throw new Exception("Found bad value $" + buf[i].ToString("x2") +
                                " at " + i);
                        }
                    }
                }

                if (file1.DataLength != buf.Length || file1.StorageSize != 4096) {
                    throw new Exception("Unexpected logical/physical length");
                }
            }
        }


        #region Utilities

        // Make a 3.5" floppy image (800KB).
        public static IFileSystem Make35Floppy(string volName, AppHook appHook) {
            return Helper.CreateTestImage(volName, FileSystemType.HFS, 1600, appHook,
                out MemoryStream memFile);
        }
        public static void CopyToFile(IFileSystem fs, string fileName) {
            fs.DumpToFile(Helper.DebugFileDir + fileName + ".po");
        }

        #endregion Utilities
    }
}
