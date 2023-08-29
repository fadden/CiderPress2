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
    /// <summary>
    /// Simple MacBinary test.
    /// </summary>
    public class TestMacBinary_Prefab : ITest {
        private const string FILE1 = "macbinary/MCUS  Free Software Disk.img.bin";
        private static List<Helper.FileAttr> sMCUSList = new List<Helper.FileAttr>() {
            new Helper.FileAttr("MCUS  Free Software Disk.img",
                409684, 389, 128+409728+512, 0x00, 0x0000)
        };

        // Test file recognition and basic parsing.
        public static void TestSimple(AppHook appHook) {
            // v2 "bad Mac" archive
            using (Stream dataFile = Helper.OpenTestFile(FILE1, true, appHook)) {
                FileAnalyzer.Analyze(dataFile, ".bin", appHook, out FileKind kind,
                    out SectorOrder orderHint);
                if (kind != FileKind.MacBinary) {
                    throw new Exception("Failed to identify archive");
                }
                using (IArchive archive = FileAnalyzer.PrepareArchive(dataFile, kind, appHook)!) {
                    Helper.ValidateContents(archive, sMCUSList);
                    Helper.CheckNotes(archive, 0, 0);
                }
            }
        }
    }
}
