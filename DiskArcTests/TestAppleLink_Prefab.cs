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

using CommonUtil;
using DiskArc;
using DiskArc.Arc;
using static DiskArc.Defs;


namespace DiskArcTests {
    public class TestAppleLink_Prefab : ITest {
        private const string FILE1 = "acu/IconEd.ACU";
        private static List<Helper.FileAttr> sFile1List = new List<Helper.FileAttr>() {
            new Helper.FileAttr("ICONED", 34614, -1, 29911, 0xb3, 0x0100),
            new Helper.FileAttr("ICONED.ICONS", 1184, -1, 700, 0xca, 0x0000),
            new Helper.FileAttr("ICONED.DOC", 21686, -1, 12692, 0x04, 0x0000),
            new Helper.FileAttr("ICONED.REV", 4113, -1, 2515, 0x04, 0x0000),
        };

        // Test file recognition and basic parsing.
        public static void TestSimple(AppHook appHook) {
            using (Stream dataFile = Helper.OpenTestFile(FILE1, true, appHook)) {
                FileAnalyzer.Analyze(dataFile, ".aCu", appHook, out FileKind kind,
                    out SectorOrder orderHint);
                if (kind != FileKind.AppleLink) {
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
