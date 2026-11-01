/*
 * Copyright 2026 faddenSoft
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

using CommonUtil;
using DiskArc;
using static DiskArc.Defs;

namespace DiskArcTests {
    /// <summary>
    /// Test basic archive recognition.
    /// </summary>
    public class TestStuffIt5_Prefab : ITest {
        private const string FILE1 = "stuffit/simple_5.sit";
        private static List<Helper.FileAttr> sFile1List = new List<Helper.FileAttr>() {
            new Helper.FileAttr("huff-sample", 41, -1, 29, 0, 0),
            new Helper.FileAttr("lzw-sample", 731, -1, 41, 0, 0),
            new Helper.FileAttr("rle-sample", 1213, -1, 177, 0, 0),
        };

        // Test file recognition and basic parsing.
        public static void TestSimple(AppHook appHook) {
            using (Stream dataFile = Helper.OpenTestFile(FILE1, true, appHook)) {
                FileAnalyzer.Analyze(dataFile, ".sit", appHook, out FileKind kind,
                    out SectorOrder orderHint);
                if (kind != FileKind.StuffIt5) {
                    throw new Exception("Failed to identify archive");
                }
                using (IArchive archive = FileAnalyzer.PrepareArchive(dataFile, kind, appHook)!) {
                    Helper.ValidateContents(archive, sFile1List);
                    Helper.CheckNotes(archive, 0, 0);
                }
            }
        }
    }
}
