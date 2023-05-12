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
using System.Diagnostics;
using System.IO;
using System.Text;

using CommonUtil;
using DiskArc;
using DiskArc.Arc;
using static DiskArc.Defs;

namespace DiskArcTests {
    /// <summary>
    /// AppleSingle wrapper tests.
    /// </summary>
    public class TestAppleSingle_Prefab : ITest {
        private static List<Helper.FileAttr> sBadmacList = new List<Helper.FileAttr>() {
            new Helper.FileAttr("nl-test\u2013\ufb01_\u2021_\xa9\uf8ff!",  // "nl-test–ﬁ_‡_©!"
                14, 0, 14, 0x00, 0x0000)
        };
        private static List<Helper.FileAttr> sGshkList = new List<Helper.FileAttr>() {
            new Helper.FileAttr("Teach File \xf4",                       // "Teach File ô"
                29, 600, 629, 0x50, 0x5445)
        };
        private static List<Helper.FileAttr> sHelloList = new List<Helper.FileAttr>() {
            new Helper.FileAttr("hello\u2022\u2197",                     // "hello•↗"
                14, 0, 14, 0x00, 0x0000)
        };
        private static List<Helper.FileAttr> sMacIPList = new List<Helper.FileAttr>() {
            new Helper.FileAttr(string.Empty,
                0, 1375, 1375, 0x00, 0x0000)
        };

        // Test file recognition and basic parsing.
        public static void TestSimple(AppHook appHook) {
            // v2 "bad Mac" archive
            using (Stream dataFile = Helper.OpenTestFile("as/badmac-utf8name.as", true, appHook)) {
                FileAnalyzer.Analyze(dataFile, ".as", appHook, out FileKind kind,
                    out SectorOrder orderHint);
                if (kind != FileKind.AppleSingle) {
                    throw new Exception("Failed to identify archive");
                }
                using (IArchive archive = FileAnalyzer.PrepareArchive(dataFile, kind, appHook)!) {
                    Helper.ValidateContents(archive, sBadmacList);
                    Helper.CheckNotes(archive, 1, 0);
                }
            }

            // v1 GSHK archive
            using (Stream dataFile = Helper.OpenTestFile("as/gshk.hfs.as", true, appHook)) {
                FileAnalyzer.Analyze(dataFile, ".as", appHook, out FileKind kind,
                    out SectorOrder orderHint);
                if (kind != FileKind.AppleSingle) {
                    throw new Exception("Failed to identify archive");
                }
                using (IArchive archive = FileAnalyzer.PrepareArchive(dataFile, kind, appHook)!) {
                    Helper.ValidateContents(archive, sGshkList);
                    Helper.CheckNotes(archive, 0, 0);
                }
            }

            // v2 Mac OS archive
            using (Stream dataFile = Helper.OpenTestFile("as/hello__.as", true, appHook)) {
                FileAnalyzer.Analyze(dataFile, ".as", appHook, out FileKind kind,
                    out SectorOrder orderHint);
                if (kind != FileKind.AppleSingle) {
                    throw new Exception("Failed to identify archive");
                }
                using (IArchive archive = FileAnalyzer.PrepareArchive(dataFile, kind, appHook)!) {
                    Helper.ValidateContents(archive, sHelloList);
                    Helper.CheckNotes(archive, 0, 0);
                }
            }

            // v2 download archive, ProDOS-in-HFS file types, no filename
            using (Stream dataFile = Helper.OpenTestFile("as/MacIP.RES.as", true, appHook)) {
                FileAnalyzer.Analyze(dataFile, ".as", appHook, out FileKind kind,
                    out SectorOrder orderHint);
                if (kind != FileKind.AppleSingle) {
                    throw new Exception("Failed to identify archive");
                }
                using (IArchive archive = FileAnalyzer.PrepareArchive(dataFile, kind, appHook)!) {
                    Helper.ValidateContents(archive, sMacIPList);
                    Helper.CheckNotes(archive, 0, 0);
                    foreach (IFileEntry entry in archive) {
                        // Should only be one... but it has no name.
                        Helper.ExpectUInt(0x70bc4083, entry.HFSFileType, "wrong HFS file type");
                        Helper.ExpectUInt(0x70646f73, entry.HFSCreator, "wrong HFS creator");
                    }
                }
            }
        }

        private const string sFileData = "This is a test!\r\rWhee\r\rWahoo\r";
        private static readonly byte[] sFileDataBytes = Encoding.ASCII.GetBytes(sFileData);

        public static void TestRead(AppHook appHook) {
            using (Stream dataFile = Helper.OpenTestFile("as/gshk.hfs.as", true, appHook)) {
                using (IArchive archive = AppleSingle.OpenArchive(dataFile, appHook)) {
                    IFileEntry entry = archive.FindFileEntry("Teach File \xf4");
                    Helper.ExpectInt(sFileDataBytes.Length, (int)entry.DataLength,
                        "data fork length");
                    byte[] dataBuf = new byte[entry.DataLength + 1];
                    using (Stream stream = archive.OpenPart(entry, FilePart.DataFork)) {
                        if (stream.Read(dataBuf, 0, (int)entry.DataLength + 1) !=
                                entry.DataLength) {
                            throw new Exception("Wrong number of data bytes read");
                        }
                    }
                    if (!RawData.CompareBytes(sFileDataBytes, 0, dataBuf, 0,
                            (int)entry.DataLength)) {
                        throw new Exception("data fork contents don't match");
                    }

                    dataBuf = new byte[entry.RsrcLength + 1];
                    using (Stream stream = archive.OpenPart(entry, FilePart.RsrcFork)) {
                        if (stream.Read(dataBuf, 0, (int)entry.RsrcLength + 1) !=
                                entry.RsrcLength) {
                            throw new Exception("Wrong number of rsrc bytes read");
                        }
                    }
                }
            }
        }

        // Quick test of an AppleDouble file created on modern Mac OS.
        public static void TestMacOSXDouble(AppHook appHook) {
            using (Stream dataFile = Helper.OpenTestFile("adf/._GSHK", true, appHook)) {
                using (IArchive archive = AppleSingle.OpenArchive(dataFile, appHook)) {
                    IFileEntry entry = archive.GetFirstEntry();

                    Helper.ExpectUInt(0x70b3db07, entry.HFSFileType, "wrong HFS type");
                    Helper.ExpectUInt(0x70646f73, entry.HFSCreator, "wrong HFS auxtype");
                    Helper.ExpectBool(true, entry.HasRsrcFork, "missing resource fork");
                    Helper.ExpectBool(false, entry.HasDataFork, "found data fork");
                }
            }
        }
    }
}
