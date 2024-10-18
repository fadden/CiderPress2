/*
 * Copyright 2024 faddenSoft
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
    public class TestAudioRecording_Prefab : ITest {
        // Three sets of two files, all binary.  The second file in the first set is damaged.
        private const string FILE1 = "audio/k7_apple_600202100_microchess2.wav.zip";
        private const string EMBED1 = "k7_apple_600202100_microchess2.wav";
        private static List<Helper.FileAttr> sFile1List = new List<Helper.FileAttr>() {
            new Helper.FileAttr("File00", 513, -1, 513, 0x06, 0x1000),
            new Helper.FileAttr("File01", 755, -1, 755, 0x06, 0x1000),
            new Helper.FileAttr("File02", 513, -1, 513, 0x06, 0x1000),
            new Helper.FileAttr("File03", 7681, -1, 7681, 0x06, 0x1000),
            new Helper.FileAttr("File04", 513, -1, 513, 0x06, 0x1000),
            new Helper.FileAttr("File05", 7681, -1, 7681, 0x06, 0x1000),
        };
        private static List<Helper.FileAttr> sFile1AltList = new List<Helper.FileAttr>() {
            new Helper.FileAttr("File00", 0, -1, 0, 0x06, 0x1000),
            new Helper.FileAttr("File01", 513, -1, 513, 0x06, 0x1000),
            new Helper.FileAttr("File02", 755, -1, 755, 0x06, 0x1000),
            new Helper.FileAttr("File03", 513, -1, 513, 0x06, 0x1000),
            new Helper.FileAttr("File04", 7681, -1, 7681, 0x06, 0x1000),
            new Helper.FileAttr("File05", 513, -1, 513, 0x06, 0x1000),
            new Helper.FileAttr("File06", 7681, -1, 7681, 0x06, 0x1000),
            new Helper.FileAttr("File07", 0, -1, 0, 0x06, 0x1000),
        };

        // Three sets of two files, Applesoft BASIC and a SHLOADed shape table.
        private const string FILE2 = "audio/k7_muse_globalwar.wav.zip";
        private const string EMBED2 = "k7_muse_globalwar.wav";
        private static List<Helper.FileAttr> sFile2List = new List<Helper.FileAttr>() {
            new Helper.FileAttr("File00", 10620, -1, 10620, 0xfc, 0x0801),
            new Helper.FileAttr("File01", 3241, -1, 3241, 0x06, 0x1000),
            new Helper.FileAttr("File02", 10620, -1, 10620, 0xfc, 0x0801),
            new Helper.FileAttr("File03", 3241, -1, 3241, 0x06, 0x1000),
            new Helper.FileAttr("File04", 10620, -1, 10620, 0xfc, 0x0801),
            new Helper.FileAttr("File05", 3241, -1, 3241, 0x06, 0x1000),
        };

        // Test file recognition and basic parsing.
        public static void TestSimple(AppHook appHook) {
            // Make sure this hasn't been set to something else.
            appHook.SetOptionEnum(DAAppHook.AUDIO_DEC_ALG, CassetteDecoder.Algorithm.ZeroCross);
            TestInZip(FILE1, EMBED1, sFile1List, appHook);
            TestInZip(FILE2, EMBED2, sFile2List, appHook);

            // Try with alternate algorithm, just to make sure the option is taking effect.
            appHook.SetOptionEnum(DAAppHook.AUDIO_DEC_ALG, CassetteDecoder.Algorithm.SharpPeak);
            TestInZip(FILE1, EMBED1, sFile1AltList, appHook);
        }

        // The WAV files are kept in ZIP to make them a little smaller.
        private static void TestInZip(string zipName, string embedName,
                List<Helper.FileAttr> fileList, AppHook appHook) {
            using (Stream dataFile = Helper.OpenTestFile(zipName, true, appHook)) {
                using (IArchive archive = FileAnalyzer.PrepareArchive(dataFile,
                        FileKind.Zip, appHook)!) {
                    IFileEntry entry = archive.FindFileEntry(embedName);
                    using Stream wavFile = archive.OpenPart(entry, FilePart.DataFork);
                    using Stream tmpFile = TempFile.CopyToTemp(wavFile);
                    FileAnalyzer.Analyze(tmpFile, ".wav", appHook, out FileKind kind,
                        out SectorOrder orderHint);
                    if (kind != FileKind.AudioRecording) {
                        throw new Exception("Failed to identify archive");
                    }
                    using (IArchive wavArc = FileAnalyzer.PrepareArchive(tmpFile, kind, appHook)!) {
                        Helper.ValidateContents(wavArc, fileList);
                        Helper.CheckNotes(archive, 0, 0);
                    }
                }
            }
        }
    }
}
