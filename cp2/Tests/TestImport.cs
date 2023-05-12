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

namespace cp2.Tests {
    /// <summary>
    /// Tests the "import" command.
    /// </summary>
    /// <remarks>
    /// Much of what "import" does is shared with "add", so we don't need to go too deep here.
    /// </remarks>
    internal static class TestImport {
        public static void RunTest(ParamsBag parms) {
            Controller.Stdout.WriteLine("  Import...");
            Controller.PrepareTestTmp(parms);

            parms.EraseImportExportSpecs();

            //
            // Scenarios to test:
            // - Export Applesoft to text, then import it back, and compare to original.
            // - Test needs to be done for both disk images and file archives, because the
            //   code paths are very different (IPartSource).
            // - Test import of bad file to verify errors are caught.
            //
            // We shouldn't need a "turducken" test since the ext-archive handling is shared.
            //
            // We don't need to exercise the importers themselves very much.  That's what the
            // FileConvTests library is for.
            //

            TestSimple(parms);
            TestBadImport(parms);

            Controller.RemoveTestTmp(parms);
        }

        private const string OUTPUT_NAME = "Output";
        private static readonly string OUTPUT_DIR = Path.Combine(Controller.TEST_TMP, OUTPUT_NAME);

        private static void TestSimple(ParamsBag parms) {
            parms.StripPaths = true;
            parms.Overwrite = false;
            parms.Preserve = AppCommon.ExtractFileWorker.PreserveMode.None;

            string arcPath =
                Path.GetFullPath(Path.Join(Controller.TEST_DATA, "fileconv", "test-files.sdk"));
            string testArchive = "test.shk";
            string testDisk = "test.po";
            // Applesoft test file.  Because we're doing a binary comparison of the results, we
            // need to pick one that doesn't have an extra byte on the end.
            string testFile = "Code/ALL.TOKENS";
            string extBasName = "ALL.TOKENS";
            string origBasName = "ALL.TOKENS.orig";
            string basSourceFile = "ALL.TOKENS.txt";
            string exportSpec = FileConv.Code.Applesoft.TAG + ",hi=false";
            string importSpec = FileConv.Code.ApplesoftImport.TAG;

            CreateOutputDir();
            string oldCurrentDir = Environment.CurrentDirectory;
            try {
                Environment.CurrentDirectory = OUTPUT_DIR;

                // Extract a copy of the BASIC program.  Rename to ".orig" for later.
                if (!Extract.HandleExtract("x", new string[] { arcPath, testFile }, parms)) {
                    throw new Exception("Failed to extract original BAS " + testFile);
                }
                File.Move(extBasName, origBasName);
                // Export the BASIC program as text.
                if (!Extract.HandleExport("xp",
                        new string[] { arcPath, exportSpec, testFile }, parms)) {
                    throw new Exception("Failed to export " + exportSpec + " " + testFile);
                }

                // Import the text file into a new file archive (will be created automatically).
                if (!Add.HandleImport("ip",
                        new string[] { testArchive, importSpec, basSourceFile }, parms)) {
                    throw new Exception("Failed to import " + importSpec + " " + basSourceFile +
                        " to archive");
                }
                // Extract the imported version.
                if (!Extract.HandleExtract("x", new string[] { testArchive, extBasName }, parms)) {
                    throw new Exception("Failed to extract imported " + extBasName);
                }
                // Compare.
                int cmp;
                if ((cmp = FileUtil.CompareFiles(origBasName, extBasName)) != 0) {
                    throw new Exception("Files are not the same (" + cmp + "): " +
                        origBasName + " " + extBasName);
                }

                // Remove the extracted file.
                File.Delete(extBasName);

                // Create a disk image.
                if (!DiskUtil.HandleCreateDiskImage("cdi",
                        new string[] { testDisk, "140k", "dos" }, parms)) {
                    throw new Exception("Unable to create test disk image " + testDisk);
                }
                // Import the text file to the disk image.
                if (!Add.HandleImport("ip",
                        new string[] { testDisk, importSpec, basSourceFile }, parms)) {
                    throw new Exception("Failed to import " + importSpec + " " + basSourceFile +
                        " to disk");
                }
                // Extract the imported version.
                if (!Extract.HandleExtract("x", new string[] { testDisk, extBasName }, parms)) {
                    throw new Exception("Failed to extract imported " + extBasName);
                }
                // Compare.
                if ((cmp = FileUtil.CompareFiles(origBasName, extBasName)) != 0) {
                    throw new Exception("Files are not the same (" + cmp + "): " +
                        origBasName + " " + extBasName);
                }
            } finally {
                Environment.CurrentDirectory = oldCurrentDir;
            }
            RemoveOutputDir();
        }

        private static void TestBadImport(ParamsBag parms) {
            string testArchive = "test.shk";
            string testDisk = "test.po";
            string importSpec = FileConv.Code.ApplesoftImport.TAG;

            CreateOutputDir();
            string oldCurrentDir = Environment.CurrentDirectory;
            try {
                Environment.CurrentDirectory = OUTPUT_DIR;

                string testInFile = "bad.bas.txt";
                using (FileStream stream = new FileStream(testInFile, FileMode.CreateNew)) {
                    for (int i = 0; i < 50; i++) {
                        stream.WriteByte((byte)':');
                    }
                }

                // Import the text file into a new file archive (will be created automatically).
                if (Add.HandleImport("ip",
                        new string[] { testArchive, importSpec, testInFile }, parms)) {
                    throw new Exception("Successfully imported to archive " + testInFile);
                }

                // Create a disk image.
                if (!DiskUtil.HandleCreateDiskImage("cdi",
                        new string[] { testDisk, "140k", "dos" }, parms)) {
                    throw new Exception("Unable to create test disk image " + testDisk);
                }
                if (Add.HandleImport("ip",
                        new string[] { testDisk, importSpec, testInFile }, parms)) {
                    throw new Exception("Successfully imported to disk " + testInFile);
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
