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
using DiskArc.Comp;
using static DiskArc.Defs;

namespace DiskArcTests {
    /// <summary>
    /// Binary II archive tests.
    /// </summary>
    public class TestBinary2_Prefab : ITest {
        private static List<Helper.FileAttr> sSampleContents = new List<Helper.FileAttr>() {
            new Helper.FileAttr("BNYARCHIVE.OL.H", 8190, 8192),
            new Helper.FileAttr("BNYARCHIVE.H", 9601, 9728),
            new Helper.FileAttr("KFEST", 0, 0),
            new Helper.FileAttr("HP", 0, 0),
            new Helper.FileAttr("SQUEEZE", 0, 0),
            new Helper.FileAttr("KFEST/KFEST.REGISTR", 4249, 4352),
            new Helper.FileAttr("HP/HARDPRESSED.CDA", 1816, 1920),
            new Helper.FileAttr("SQUEEZE/BNYARCHIVE.H.QQ", -1, 6400),
            new Helper.FileAttr("SQUEEZE/BNYARCHIVE.O.QQ", -1, 5376),
        };

        public static void TestSimple(AppHook appHook) {
            using (Stream dataFile = Helper.OpenTestFile("bny/SAMPLE.BQY", true, appHook)) {
                using (IArchive archive = FileAnalyzer.PrepareArchive(dataFile,
                        FileKind.Binary2, appHook)!) {
                    CheckTOC(archive, appHook);
                    CheckFiles(archive, appHook);
                }
            }
        }

        private static void CheckTOC(IArchive archive, AppHook appHook) {
            // Quick test to see if we get the expected set of files.
            Helper.ValidateContents(archive, sSampleContents);

            // Check directory identification.
            IFileEntry squeezeDir = archive.FindFileEntry("SQUEEZE");
            if (!squeezeDir.IsDirectory) {
                throw new Exception("Directory not identified as such");
            }
            if (squeezeDir.DataLength != 0 || squeezeDir.StorageSize != 0) {
                throw new Exception("Directory has nonzero length");
            }

            IFileEntry hpcda = archive.FindFileEntry("HP/HARDPRESSED.CDA");
            if (hpcda.IsDirectory) {
                throw new Exception("Non-directory identified as dir");
            }
            if (hpcda.FileType != 0xb9 /*CDA*/ || hpcda.AuxType != 0x0100) {
                throw new Exception("Incorrect type/auxtype");
            }
        }

        // Exercise file extraction, with and without squeezing.
        private static void CheckFiles(IArchive archive, AppHook appHook) {
            IFileEntry file1 = archive.FindFileEntry("BNYARCHIVE.H");
            IFileEntry file2 = archive.FindFileEntry("SQUEEZE/BNYARCHIVE.H.QQ");
            ArcReadStream stream1 = archive.OpenPart(file1, FilePart.DataFork);
            ArcReadStream stream2 = archive.OpenPart(file2, FilePart.DataFork);
            byte[] buf1 = new byte[10240];
            byte[] buf2 = new byte[10240];
            int actual1 = stream1.Read(buf1, 0, buf1.Length);
            int actual2 = stream2.Read(buf2, 0, buf2.Length);
            if (actual1 != actual2) {
                throw new Exception("Different lengths: " + actual1 + " vs. " + actual2);
            }
            if (!RawData.CompareBytes(buf1, buf2, buf2.Length)) {
                throw new Exception("File contents do not match");
            }

            stream1.Close();
            stream2.Close();
        }
    }
}
