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
    public class TestRDOS_Prefab : ITest {
        private const string FILE1 = "rdos/rdos3-rss-save.woz";
        private const string FILE2 = "rdos/rdos32-shiloh-save.woz";
        private const string FILE3 = "rdos/rdos33-rw2000-save.woz";

        private static List<Helper.FileAttr> sFile1List = new List<Helper.FileAttr>() {
            new Helper.FileAttr(" >-SSI GAME SAVE DISK-<", 6656, 6656),
            new Helper.FileAttr("FIGHTMENU", 12800, 12800),
            new Helper.FileAttr("DISKCHECK", 256, 256),
        };

        private static List<Helper.FileAttr> sFile2List = new List<Helper.FileAttr>() {
            new Helper.FileAttr(" >-SSI GAME SAVE DISK-<", 6656, 6656),
            new Helper.FileAttr("MYSAVE", 2304, 2304),
        };

        private static List<Helper.FileAttr> sFile3List = new List<Helper.FileAttr>() {
            new Helper.FileAttr("SSI SAVE GAME DISK RDOS", 8192, 8192),
            new Helper.FileAttr("SSI.INIT", 1280, 1280),
            new Helper.FileAttr("GAME", 4690, 4864),
            new Helper.FileAttr("MAP", 2145, 2304),
        };

        public static void TestSimple(AppHook appHook) {
            CheckDisk(FILE1, sFile1List, appHook);
            CheckDisk(FILE2, sFile2List, appHook);
            CheckDisk(FILE3, sFile3List, appHook);
        }

        private static void CheckDisk(string fileName, List<Helper.FileAttr> expFiles,
                AppHook appHook) {
            using (Stream dataFile = Helper.OpenTestFile(fileName, true, appHook)) {
                using (IDiskImage diskImage = FileAnalyzer.PrepareDiskImage(dataFile,
                        FileKind.Woz, appHook)!) {
                    diskImage.AnalyzeDisk();
                    IFileSystem fs = (IFileSystem)diskImage.Contents!;
                    fs.PrepareFileAccess(true);
                    Helper.CheckNotes(fs, 0, 0);

                    IFileEntry volDir = fs.GetVolDirEntry();
                    Helper.ValidateDirContents(volDir, expFiles);
                }
            }

        }
    }
}
