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
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

using CommonUtil;
using DiskArc;
using static DiskArc.Defs;
using static DiskArc.IFileSystem;

namespace DiskArcTests {
    /// <summary>
    /// Tests basic features of CP/M filesystem implementation.  All tests are done with
    /// disk images created by native systems.
    /// </summary>
    public class TestCPM_Prefab : ITest {
        private static List<Helper.FileAttr> sCPAMList = new List<Helper.FileAttr>() {
            new Helper.FileAttr("-10/7/87.R21", 0, 0),
            new Helper.FileAttr("AEXIT.COM", 384, 1024),
            new Helper.FileAttr("APATCH12.COM", 2944, 3072),
            new Helper.FileAttr("AUTOPC.COM", 256, 1024),
            new Helper.FileAttr("AUTORUN.COM", 256, 1024),
            new Helper.FileAttr("CONFIGIO.COM", 4736, 5120),
            new Helper.FileAttr("COPY.COM", 3840, 4096),
            new Helper.FileAttr("CPAM60F.COM", 13056, 13312),
            new Helper.FileAttr("CPAM60S.COM", 13056, 13312),
            new Helper.FileAttr("CPAM60UF.COM", 16384, 16384),
            new Helper.FileAttr("CPAM60US.COM", 16384, 16384),
            new Helper.FileAttr("CWS33X.COM", 384, 1024),
            new Helper.FileAttr("FMTUNI.COM", 1664, 2048),
            new Helper.FileAttr("FORMAT.COM", 3840, 4096),
            new Helper.FileAttr("MEGDRIVE.COM", 2048, 2048),
            new Helper.FileAttr("NSWEEP.COM", 11776, 12288),
            new Helper.FileAttr("PC.COM", 7552, 8192),
            new Helper.FileAttr("PIP.COM", 7168, 7168),
            new Helper.FileAttr("RAMBOOT.COM", 512, 1024),
            new Helper.FileAttr("RESTOR.COM", 896, 1024),
            new Helper.FileAttr("SAMPLE.SUB", 128, 1024),
            new Helper.FileAttr("SD.COM", 2048, 2048),
            new Helper.FileAttr("STAT.COM", 6144, 6144),
            new Helper.FileAttr("SUBMIT.COM", 1536, 2048),
        };

        public static void TestSimple(AppHook appHook) {
            using (Stream dataFile = Helper.OpenTestFile("cpm/CPAM51a.do", true,
                    appHook)) {
                using (IDiskImage diskImage = FileAnalyzer.PrepareDiskImage(dataFile,
                        FileKind.UnadornedSector, appHook)!) {
                    diskImage.AnalyzeDisk();
                    IFileSystem fs = (IFileSystem)diskImage.Contents!;
                    fs.PrepareFileAccess(true);
                    Helper.CheckNotes(fs, 1, 0);    // 1 warning for secretly used alloc blocks

                    IFileEntry volDir = fs.GetVolDirEntry();
                    Helper.ValidateDirContents(volDir, sCPAMList);

                    TestInterface(fs);
                    TestSimpleRead(fs);
                }
            }
        }

        private static void TestInterface(IFileSystem fs) {
            IFileEntry entry =
                Helper.EntryFromPathThrow(fs, "APATCH12.COM", IFileEntry.NO_DIR_SEP);

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
            const string MATCH_STR = "Installation program for";
            const int MATCH_OFF = 0x1f3;

            IFileEntry volDir = fs.GetVolDirEntry();
            IFileEntry file1 = fs.FindFileEntry(volDir, "APATCH12.COM");

            byte[] buf = new byte[MATCH_STR.Length];

            DiskFileStream fd1 = fs.OpenFile(file1, FileAccessMode.ReadOnly, FilePart.DataFork);
            fd1.Seek(MATCH_OFF, SeekOrigin.Begin);
            fd1.Read(buf, 0, MATCH_STR.Length);
            string conv = Encoding.ASCII.GetString(buf, 0, MATCH_STR.Length);
            if (conv != MATCH_STR) {
                throw new Exception("Read did not find correct data");
            }

            fd1.Position = 0xb70;
            int actual = fd1.Read(buf, 0, buf.Length);
            if (actual != 16) {
                throw new Exception("Read past end of file");
            }
        }
    }
}
