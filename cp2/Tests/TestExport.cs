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

using AppCommon;
using CommonUtil;
using FileConv;

namespace cp2.Tests {
    /// <summary>
    /// Tests the "export" command.
    /// </summary>
    /// <remarks>
    /// Much of what "export" does is shared with "extract".  Test that first so we can assume
    /// the common parts are all working.
    /// </remarks>
    internal static class TestExport {
        public static void RunTest(ParamsBag parms) {
            Controller.Stdout.WriteLine("  Export...");
            Controller.PrepareTestTmp(parms);

            parms.EraseImportExportSpecs();
            parms.AddExt = true;

            //
            // Scenarios to test:
            // - Export at least one each of .TXT, .RTF, .PNG, .CSV, using "best" mode.  Not too
            //   worried about the contents; this is not a test of the converters themselves.
            // - Export an Applesoft program with and without "hi" set.
            // - Internal mechanics are slightly different for disk image vs. file archive,
            //   so testing both may be wise.
            //
            // We shouldn't need a "turducken" test since the ext-archive handling is shared.
            //

            TestExportGeneral(parms);
            TestBASConfig(parms);

            Controller.RemoveTestTmp(parms);
        }

        private const string OUTPUT_NAME = "Output";
        private static readonly string OUTPUT_DIR = Path.Combine(Controller.TEST_TMP, OUTPUT_NAME);

        private static readonly string[] SAMPLE_LIST = new string[] {
            "Graphics:HRBARS",      // BAS/$0801 "bas" -> .txt
            "Graphics:BARS.HR",     // BIN/$2000 "hgr" -> .png
            "Docs:TeachTest",       // GWP/$5445 "teach" -> .rtf
            "Docs:RANDOM.TXT",      // TXT/$0800 "rtext" -> .csv
        };
        private static readonly string[] SAMPLE_OUT_LIST = new string[] {
            "HRBARS.txt",
            "BARS.HR.png",
            "TeachTest.rtf",
            "RANDOM.TXT.csv",
        };

        private static void TestExportGeneral(ParamsBag parms) {
            parms.StripPaths = true;
            string arcPath =
                Path.GetFullPath(Path.Join(Controller.TEST_DATA, "fileconv", "test-files.sdk"));

            CreateOutputDir();
            string oldCurrentDir = Environment.CurrentDirectory;
            try {
                Environment.CurrentDirectory = OUTPUT_DIR;
                // Do several in one go to ensure that multiple args are handled.
                List<string> argList = new List<string>() { arcPath, ConvConfig.BEST };
                foreach (string name in SAMPLE_LIST) {
                    argList.Add(name);
                }
                foreach (string name in SAMPLE_LIST) {
                    if (!Extract.HandleExport("xp", argList.ToArray(), parms)) {
                        throw new Exception("export multiple items failed");
                    }
                }
                // Confirm that all files exist.
                foreach (string name in SAMPLE_OUT_LIST) {
                    if (!File.Exists(name)) {
                        throw new Exception("Did not find " + name);
                    }
                }
            } finally {
                Environment.CurrentDirectory = oldCurrentDir;
            }
            RemoveOutputDir();
        }

        private static void TestBASConfig(ParamsBag parms) {
            parms.StripPaths = true;
            string arcPath =
                Path.GetFullPath(Path.Join(Controller.TEST_DATA, "fileconv", "test-files.sdk"));
            string testFile = "Graphics:HRBARS";

            CreateOutputDir();
            string oldCurrentDir = Environment.CurrentDirectory;
            try {
                Environment.CurrentDirectory = OUTPUT_DIR;

                string convSpec = FileConv.Code.Applesoft.TAG + ",hi=";
                if (!Extract.HandleExport("xp",
                        new string[] { arcPath, convSpec + "false", testFile }, parms)) {
                    throw new Exception("export spec=" + convSpec + " " + testFile + " failed");
                }
                if (!File.Exists("HRBARS.txt")) {
                    throw new Exception("Failed to extract BAS as .txt");
                }
                if (!Extract.HandleExport("xp",
                        new string[] { arcPath, convSpec + "true", testFile }, parms)) {
                    throw new Exception("export spec=" + convSpec + " " + testFile + " failed");
                }
                if (!File.Exists("HRBARS.rtf")) {
                    throw new Exception("Failed to extract BAS as .rtf");
                }

                // Test the --no-add-ext option.
                parms.AddExt = false;
                if (!Extract.HandleExport("xp",
                        new string[] { arcPath, convSpec + "false", testFile }, parms)) {
                    throw new Exception("export spec=" + convSpec + " " + testFile +
                        " --no-add-ext failed");
                }
                if (!File.Exists("HRBARS")) {
                    throw new Exception("Failed to extract BAS without extension");
                }
                parms.AddExt = true;
            } finally {
                Environment.CurrentDirectory = oldCurrentDir;
            }
            RemoveOutputDir();
        }

        /// <summary>
        /// Creates the "TestTmp/Output" directory.
        /// </summary>
        private static void CreateOutputDir() {
            Directory.CreateDirectory(OUTPUT_DIR);
        }

        /// <summary>
        /// Removes the "TestTmp/Output" directory, and all of its contents.
        /// </summary>
        private static void RemoveOutputDir() {
            FileUtil.DeleteDirContents(OUTPUT_DIR);
            Directory.Delete(OUTPUT_DIR);
        }
    }
}
