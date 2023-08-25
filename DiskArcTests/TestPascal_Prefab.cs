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
using System.Text;

using CommonUtil;
using DiskArc;
using static DiskArc.Defs;
using static DiskArc.IFileSystem;

namespace DiskArcTests {
    /// <summary>
    /// Tests basic features of Pascal filesystem implementation.  All tests are done with
    /// disk images created by Apple Pascal.
    /// </summary>
    public class TestPascal_Prefab : ITest {
        private static List<Helper.FileAttr> sApple1List = new List<Helper.FileAttr>() {
            new Helper.FileAttr("SYSTEM.APPLE", 16384, -1, 16384, FileAttribs.FILE_TYPE_PDA, 0),
            new Helper.FileAttr("SYSTEM.PASCAL", 20992, -1, 20992, FileAttribs.FILE_TYPE_PCD, 0),
            new Helper.FileAttr("SYSTEM.MISCINFO", 512, -1, 512, FileAttribs.FILE_TYPE_PDA, 0),
            new Helper.FileAttr("SYSTEM.EDITOR", 24064, -1, 24064, FileAttribs.FILE_TYPE_PCD, 0),
            new Helper.FileAttr("SYSTEM.FILER", 14336, -1, 14336, FileAttribs.FILE_TYPE_PCD, 0),
            new Helper.FileAttr("SYSTEM.LIBRARY", 17408, -1, 17408, FileAttribs.FILE_TYPE_PDA, 0),
            new Helper.FileAttr("SYSTEM.CHARSET", 1024, -1, 1024, FileAttribs.FILE_TYPE_PDA, 0),
            new Helper.FileAttr("SYSTEM.SYNTAX", 7168, -1, 7168, FileAttribs.FILE_TYPE_PDA, 0),
        };

        public static void TestSimple(AppHook appHook) {
            using (Stream dataFile = Helper.OpenTestFile("pascal/Apple Pascal1.po", true,
                    appHook)) {
                using (IDiskImage diskImage = FileAnalyzer.PrepareDiskImage(dataFile,
                        FileKind.UnadornedSector, appHook)!) {
                    diskImage.AnalyzeDisk();
                    IFileSystem fs = (IFileSystem)diskImage.Contents!;
                    fs.PrepareFileAccess(true);
                    Helper.CheckNotes(fs, 0, 0);

                    IFileEntry volDir = fs.GetVolDirEntry();
                    Helper.ValidateDirContents(volDir, sApple1List);

                    TestInterface(fs);
                    TestSimpleRead(fs);
                }
            }
        }

        private static void TestInterface(IFileSystem fs) {
            IFileEntry entry =
                Helper.EntryFromPathThrow(fs, "SYSTEM.SYNTAX", IFileEntry.NO_DIR_SEP);

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

        private static void TestSimpleRead(IFileSystem fs) {
            const string MATCH_STR = "1: Error in simple type\r";
            const int MATCH_OFF = 0x402;

            IFileEntry volDir = fs.GetVolDirEntry();
            IFileEntry file1 = fs.FindFileEntry(volDir, "SYSTEM.SYNTAX");

            byte[] buf = new byte[MATCH_STR.Length];

            DiskFileStream fd1 = fs.OpenFile(file1, FileAccessMode.ReadOnly, FilePart.DataFork);
            fd1.Seek(MATCH_OFF, SeekOrigin.Begin);
            fd1.Read(buf, 0, MATCH_STR.Length);
            string conv = Encoding.ASCII.GetString(buf, 0, MATCH_STR.Length);
            if (conv != MATCH_STR) {
                throw new Exception("Read did not find correct data");
            }

            fd1.Position = 0x1bf0;
            int actual = fd1.Read(buf, 0, buf.Length);
            if (actual != 16) {
                throw new Exception("Read past end of file");
            }
        }
    }
}
