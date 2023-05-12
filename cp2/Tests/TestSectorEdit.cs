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
using System.Text;

using CommonUtil;

namespace cp2.Tests {
    /// <summary>
    /// Tests "read-sector", "write-sector", "read-block", "write-block".
    /// </summary>
    internal static class TestSectorEdit {
        public static void RunTest(ParamsBag parms) {
            Controller.Stdout.WriteLine("  SectorEdit...");
            Controller.PrepareTestTmp(parms);

            //
            // Test scenarios:
            // - read sector, modify, write, read back
            // - read block, modify, write, read back
            //

            TestReadTrack(parms);
            TestTurduckenSectors(parms);
            TestTurduckenBlocks(parms);

            Controller.RemoveTestTmp(parms);
        }

        private static void TestReadTrack(ParamsBag parms) {
            // Try reading side 1 of a 3.5" disk.
            string inputFile = Path.Join(Controller.TEST_DATA, "woz", "iigs-system-35.woz");
            if (!SectorEdit.HandleReadTrack("rt", new string[] { inputFile, "53.1" }, parms)) {
                throw new Exception("rt " + inputFile + " failed");
            }

            // Read a quarter-track from a 5.25" disk, with and without --latch.  The size
            // should change.
            parms.Latch = true;
            MemoryStream latchOut = Controller.CaptureConsoleOut();
            inputFile = Path.Join(Controller.TEST_DATA, "woz", "dos33master_2.woz");
            if (!SectorEdit.HandleReadTrack("rt", new string[] { inputFile, "17.25" }, parms)) {
                throw new Exception("rt " + inputFile + " failed");
            }
            parms.Latch = false;
            MemoryStream noLatchOut = Controller.CaptureConsoleOut();
            inputFile = Path.Join(Controller.TEST_DATA, "woz", "dos33master_2.woz");
            if (!SectorEdit.HandleReadTrack("rt", new string[] { inputFile, "17.25" }, parms)) {
                throw new Exception("rt " + inputFile + " failed");
            }
            if (latchOut.Length >= noLatchOut.Length) {
                throw new Exception("Latched data was larger");
            }
        }

        private static void TestTurduckenSectors(ParamsBag parms) {
            // Make a copy of a turducken file.
            string inputFile = Path.Join(Controller.TEST_DATA, "turducken", "MultiPart.hdv");
            string tdnFile = Path.Join(Controller.TEST_TMP, "tdn-stest.hdv");
            FileUtil.CopyFile(inputFile, tdnFile);
            parms.SkipSimple = true;

            string extArc = tdnFile + ExtArchive.SPLIT_CHAR + "2" + ExtArchive.SPLIT_CHAR +
                "small.woz.gz";

            // Read T0S1 and capture the hex dump.
            MemoryStream stdout = Controller.CaptureConsoleOut();
            if (!SectorEdit.HandleReadSector("rs", new string[] { extArc, "0", "1" }, parms)) {
                throw new Exception("rs " + extArc + " 0 1 failed");
            }
            string asString = ConvertStreamToString(stdout);
            // Replace all occurrences of $6c with $4c.  (Should be three.)
            string replString = asString.Replace("6c", "4c");
            if (replString == asString) {
                throw new Exception("Test source no longer works");     // nothing changed
            }

            // Redirecting stdout is awkward here, so write it to a file.
            string dataFile = Path.Combine(Controller.TEST_TMP, "sout.hex");
            using (StreamWriter sw = new StreamWriter(
                    new FileStream(dataFile, FileMode.CreateNew))) {
                sw.Write(replString);
            }

            if (!SectorEdit.HandleWriteSector("ws",
                    new string[] { extArc, "0", "1", dataFile }, parms)) {
                throw new Exception("ws " + extArc + " 0 1 " + dataFile + " failed");
            }

            stdout = Controller.CaptureConsoleOut();
            if (!SectorEdit.HandleReadSector("rs", new string[] { extArc, "0", "1" }, parms)) {
                throw new Exception("rs " + extArc + " 0 1 (vfy) failed");
            }
            string modString = ConvertStreamToString(stdout);
            // Need to update the ASCII so they'll match.
            if (modString != replString.Replace('l', 'L')) {
                throw new Exception("Unexpected output");
            }
        }

        private static void TestTurduckenBlocks(ParamsBag parms) {
            // Make a copy of a turducken file.
            string inputFile = Path.Join(Controller.TEST_DATA, "turducken", "MultiPart.hdv");
            string tdnFile = Path.Join(Controller.TEST_TMP, "tdn-btest.hdv");
            FileUtil.CopyFile(inputFile, tdnFile);
            parms.SkipSimple = true;

            string extArc = tdnFile + ExtArchive.SPLIT_CHAR + "2" + ExtArchive.SPLIT_CHAR +
                "small.woz.gz";

            // Read block 2 and capture the hex dump.
            MemoryStream stdout = Controller.CaptureConsoleOut();
            if (!SectorEdit.HandleReadBlock("rb", new string[] { extArc, "2" }, parms)) {
                throw new Exception("rb " + extArc + " 2 failed");
            }
            string asString = ConvertStreamToString(stdout);
            // Replace all occurrences of $48 with $4a.  (Should be one.)
            string replString = asString.Replace("48", "4a");
            if (replString == asString) {
                throw new Exception("Test source no longer works");     // nothing changed
            }

            // Redirecting stdout is awkward here, so write it to a file.
            string dataFile = Path.Combine(Controller.TEST_TMP, "bout.hex");
            using (StreamWriter sw = new StreamWriter(
                    new FileStream(dataFile, FileMode.CreateNew))) {
                sw.Write(replString);
            }

            if (!SectorEdit.HandleWriteBlock("wb", new string[] { extArc, "2", dataFile }, parms)) {
                throw new Exception("wb " + extArc + " 2 " + dataFile + " failed");
            }

            // We should have changed "hello.txt" to "jello.txt".
            // List the contents.
            stdout = Controller.CaptureConsoleOut();
            if (!Catalog.HandleList("list", new string[] { extArc }, parms)) {
                throw new Exception("list '" + extArc + "' failed");
            }
            string[] expected = new string[] {
                "jello.txt",
                "again.txt",
            };
            Controller.CompareLines(expected, stdout);
        }

        private static string ConvertStreamToString(Stream stream) {
            StringBuilder sb = new StringBuilder();
            stream.Position = 0;
            int val;
            while ((val = stream.ReadByte()) >= 0) {
                sb.Append((char)val);       // assume ASCII
            }
            return sb.ToString();
        }
    }
}
