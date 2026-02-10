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
using System.Diagnostics;

using CommonUtil;

namespace cp2.Tests {
    /// <summary>
    /// Test the "un-binscii" command.
    /// </summary>
    internal static class TestBinSCII {
        public static void RunTest(ParamsBag parms) {
            Controller.Stdout.WriteLine("  BinSCII...");
            Controller.PrepareTestTmp(parms);

            parms.Overwrite = false;
            TestDecode(parms);

            Controller.RemoveTestTmp(parms);
        }

        private const string OUTPUT_NAME = "Output";
        private static readonly string OUTPUT_DIR = Path.Combine(Controller.TEST_TMP, OUTPUT_NAME);


        private static void TestDecode(ParamsBag parms) {
            // Process multiple input files into multiple output files, in scrambled order.
            string[] args = {
                Path.GetFullPath(Path.Join(Controller.TEST_DATA, "base64", "zlink-03.bsq")),
                Path.GetFullPath(Path.Join(Controller.TEST_DATA, "base64", "zlink-05.bsq")),
                Path.GetFullPath(Path.Join(Controller.TEST_DATA, "base64", "shrinkit.bsc")),
                Path.GetFullPath(Path.Join(Controller.TEST_DATA, "base64", "zlink-02.bsq")),
                Path.GetFullPath(Path.Join(Controller.TEST_DATA, "base64", "zlink-01.bsq")),
                Path.GetFullPath(Path.Join(Controller.TEST_DATA, "base64", "zlink-04.bsq")),
            };

            CreateOutputDir();
            string oldCurrentDir = Environment.CurrentDirectory;
            try {
                Environment.CurrentDirectory = OUTPUT_DIR;

                if (!MiscUtil.HandleUnBinSCII("unbsc", args, parms)) {
                    throw new Exception("decode test failed");
                }

                // Output files are SHRINKIT and Z.LINK.SHK.  We can test the latter easily.
                if (!Test.HandleTest("test", new string[] { "Z.LINK.SHK" }, parms)) {
                    throw new Exception("Z.LINK.SHK test failed");
                }

                // Just check the length on the SYS file.
                FileInfo fileInfo = new FileInfo("SHRINKIT");
                if (fileInfo.Length != 40649) {
                    throw new Exception("bad length for SHRINKIT");
                }
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
