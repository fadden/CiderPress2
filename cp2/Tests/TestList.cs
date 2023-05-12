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

namespace cp2.Tests {
    /// <summary>
    /// Tests the "list" command.
    /// </summary>
    /// <remarks>
    /// This also exercises the ExtArchive class and its ability to select specific parts
    /// of nested archives and disk images.
    /// </remarks>
    internal static class TestList {
        private static readonly string BASE_DIR = Path.Combine(Controller.TEST_DATA, "turducken");

        private class ExpectedCount {
            public string ExtArchiveName { get; set; }
            public int ExpCount { get; set; }

            public ExpectedCount(string command, int expCount) {
                ExtArchiveName = command;
                ExpCount = expCount;
            }
        }

        /// <summary>
        /// A series of tests for turduckens.  This isn't a very thorough test, since it just
        /// checks the counts, but it should detect regressions in ExtArchive.
        /// </summary>
        private static ExpectedCount[] sExtArcTest = {
            new ExpectedCount("MultiPart.hdv", 3),
            new ExpectedCount("MultiPart.hdv:1", 3),
            new ExpectedCount("MultiPart.hdv:1:1", 1),
            new ExpectedCount("MultiPart.hdv:ProDOS_Part:5", 1),
            new ExpectedCount("MultiPart.hdv:2", 8),
            new ExpectedCount("MultiPart.hdv:HFS_Part:dir1:dir2:new-init.do", 1),
            new ExpectedCount("MultiPart.hdv:2:dir1:dir2:Samples.BXY", 6),
            new ExpectedCount("MultiPart.hdv:2:small.woz.gz", 2),
            new ExpectedCount("MultiPart.hdv:2:Some.Files.zip", 3),
            //new ExpectedCount("MultiPart.hdv:3", 0),

            new ExpectedCount("Shrunk.zip.gz", 1),
            new ExpectedCount("Shrunk.zip.gz:Binary2.SHK", 1),
            new ExpectedCount("Shrunk.zip.gz:Binary2.SHK:SAMPLE.BQY", 9),

            new ExpectedCount("subdirs.zip", 3),
            new ExpectedCount("subdirs.zip:zdir1:zdir2:wrapdirhello.shk", 1),
            new ExpectedCount("subdirs.zip:zdir1:zdir2:wrapdirhello.shk:ndir1:ndir2:dirhello.shk", 1),

            new ExpectedCount("WrappedSDK.zip", 1),
            new ExpectedCount("WrappedSDK.zip:simple.dos.sdk.gz", 1),
        };

        private static string[] sSampleBQY = {
            "BNYARCHIVE.OL.H",
            "BNYARCHIVE.H",
            "KFEST",
            "HP",
            "SQUEEZE",
            "KFEST/KFEST.REGISTR",
            "HP/HARDPRESSED.CDA",
            "SQUEEZE/BNYARCHIVE.H.QQ",
            "SQUEEZE/BNYARCHIVE.O.QQ"
        };

        public static void RunTest(ParamsBag parms) {
            Controller.Stdout.WriteLine("  List...");
            MemoryStream stdout;

            parms.SkipSimple = true;

            // Exercise ExtArchive.
            foreach (ExpectedCount exp in sExtArcTest) {
                stdout = ExecList(exp.ExtArchiveName, parms);
                int lineCount = Controller.CountLines(stdout);
                if (lineCount != exp.ExpCount) {
                    throw new Exception("Incorrect line count for " + exp.ExtArchiveName +
                        ", expected=" + exp.ExpCount + " actual=" + lineCount);
                }
            }

            // Check output.
            stdout = ExecList("Shrunk.zip.gz:Binary2.SHK:SAMPLE.BQY", parms);
            Controller.CompareLines(sSampleBQY, stdout);

            // Repeat a couple of tests with --no-skip-simple specified.  This requires us to
            // explicitly list the contents of gzip archives and NuFX disk images.
            parms.SkipSimple = false;
            stdout = ExecList("Shrunk.zip.gz:Shrunk.zip:Binary2.shk:sample.bqy", parms);
            Controller.CompareLines(sSampleBQY, stdout);
            stdout = ExecList("WrappedSDK.zip:simple.dos.sdk.gz:simple.dos.sdk:NEW.DISK", parms);
            Controller.CompareLines(new string[] { "HELLO" }, stdout);
            parms.SkipSimple = true;
        }

        /// <summary>
        /// Executes a "list" command.
        /// </summary>
        /// <param name="extArchive">Archive name.  Will be prefixed with "turducken" dir.</param>
        /// <param name="parms">Command options.</param>
        private static MemoryStream ExecList(string extArchive, ParamsBag parms) {
            MemoryStream stdout = Controller.CaptureConsoleOut();
            string extArchiveName = Path.Combine(BASE_DIR, extArchive);
            if (!Catalog.HandleList("list", new string[] { extArchiveName }, parms)) {
                throw new Exception("Error: list '" + extArchive +
                    "' failed: exitCode=" + Environment.ExitCode);
            }
            return stdout;
        }
    }
}
