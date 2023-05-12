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
using System.Text;

using CommonUtil;
using DiskArc;
using static DiskArc.Defs;

namespace DiskArcTests {
    /// <summary>
    /// GZip compressed file tests.
    /// </summary>
    public class TestGZip_Prefab : ITest {
        private static List<Helper.FileAttr> sHelloList = new List<Helper.FileAttr>() {
            new Helper.FileAttr("hello\u2022\u2197.as", -1 /*167*/, 117)     // "hello•↗.as"
        };
        private static List<Helper.FileAttr> sNoNameList = new List<Helper.FileAttr>() {
            new Helper.FileAttr(string.Empty, -1 /*37*/, 52)
        };

        // Test file recognition.
        public static void TestSimple(AppHook appHook) {
            using (Stream dataFile = Helper.OpenTestFile("gzip/hello__.as.gz", true, appHook)) {
                FileAnalyzer.Analyze(dataFile, ".gz", appHook, out FileKind kind,
                    out SectorOrder orderHint);
                if (kind != FileKind.GZip) {
                    throw new Exception("Failed to identify archive - " + kind);
                }
                using (IArchive archive = FileAnalyzer.PrepareArchive(dataFile, kind, appHook)!) {
                    Helper.ValidateContents(archive, sHelloList);
                    Helper.CheckNotes(archive, 0, 0);
                }
            }
            using (Stream dataFile = Helper.OpenTestFile("gzip/no-filename.gz", true, appHook)) {
                FileAnalyzer.Analyze(dataFile, ".gz", appHook, out FileKind kind,
                    out SectorOrder orderHint);
                if (kind != FileKind.GZip) {
                    throw new Exception("Failed to identify archive");
                }
                using (IArchive archive = FileAnalyzer.PrepareArchive(dataFile, kind, appHook)!) {
                    Helper.ValidateContents(archive, sNoNameList);
                    Helper.CheckNotes(archive, 0, 0);
                }
            }
        }

        private const string MULTI_TEXT = "three part file\n";
        private static readonly byte[] sMultiTextBytes = Encoding.ASCII.GetBytes(MULTI_TEXT);

        public static void TestMultiMember(AppHook appHook) {
            using (Stream dataFile = Helper.OpenTestFile("gzip/multi-member.gz", true, appHook)) {
                FileAnalyzer.Analyze(dataFile, ".gz", appHook, out FileKind kind,
                    out SectorOrder orderHint);
                if (kind != FileKind.GZip) {
                    throw new Exception("Failed to identify archive - " + kind);
                }

                using (IArchive archive = FileAnalyzer.PrepareArchive(dataFile, kind, appHook)!) {
                    Helper.CheckNotes(archive, 0, 0);
                    IFileEntry entry = archive.FindFileEntry("one");    // name of first member
                    using (Stream stream = archive.OpenPart(entry, FilePart.DataFork)) {
                        // DataLength might be wrong for multi-part, so pull the whole thing.
                        MemoryStream varOut = new MemoryStream();
                        stream.CopyTo(varOut);
                        if (varOut.Length != sMultiTextBytes.Length) {
                            throw new Exception("Multi-member length incorrect");
                        }
                        if (!RawData.CompareBytes(sMultiTextBytes, 0, varOut.GetBuffer(), 0,
                                sMultiTextBytes.Length)) {
                            throw new Exception("Multi-member data mismatch");
                        }
                    }
                }
            }
        }
    }
}
