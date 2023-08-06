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
                }
            }
        }
    }
}
