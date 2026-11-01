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
    public class TestStuffIt_Prefab : ITest {
        private const string FILE1 = "stuffit/simple_15.sit";
        private static List<Helper.FileAttr> sFile1List = new List<Helper.FileAttr>() {
            new Helper.FileAttr("huff-sample", 41, -1, 18, 0, 0),
            new Helper.FileAttr("lzw-sample", 731, -1, 252, 0, 0),
            new Helper.FileAttr("rle-sample", 1213, -1, 293, 0, 0),
        };
        private const string FILE2 = "stuffit/fancy_45.sit";

        // Test file recognition and basic parsing.
        public static void TestSimple(AppHook appHook) {
            using (Stream dataFile = Helper.OpenTestFile(FILE1, true, appHook)) {
                FileAnalyzer.Analyze(dataFile, ".sit", appHook, out FileKind kind,
                    out SectorOrder orderHint);
                if (kind != FileKind.StuffIt) {
                    throw new Exception("Failed to identify archive");
                }
                using (IArchive archive = FileAnalyzer.PrepareArchive(dataFile, kind, appHook)!) {
                    Helper.ValidateContents(archive, sFile1List);
                    Helper.CheckNotes(archive, 0, 0);
                }
            }
        }

        // Simple extraction test.  Relies on file CRC to spot problems.
        public static void TestExtract(AppHook appHook) {
            const int GSHK_DFORK_LEN = 112443;
            byte[] dataBuf = new byte[GSHK_DFORK_LEN + 1];

            using (Stream dataFile = Helper.OpenTestFile(FILE2, true, appHook)) {
                using (IArchive archive = FileAnalyzer.PrepareArchive(dataFile,
                        FileKind.StuffIt, appHook)!) {
                    IFileEntry file1 = archive.FindFileEntry("Dir1:GSHK");
                    Helper.CheckNotes(archive, 0, 0);
                    using (ArcReadStream stream = archive.OpenPart(file1, FilePart.DataFork)) {
                        int actual = stream.Read(dataBuf, 0, dataBuf.Length);
                        Helper.ExpectInt(GSHK_DFORK_LEN, actual, "GSHK data fork length");
                        if (stream.ChkResult != ArcReadStream.CheckResult.Success) {
                            throw new Exception("Checksum not verified");
                        }
                    }
                }
            }
        }
    }
}
