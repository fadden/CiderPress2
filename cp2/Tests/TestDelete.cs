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
    /// Tests the "delete" command.
    /// </summary>
    internal static class TestDelete {
        public static void RunTest(ParamsBag parms) {
            Controller.Stdout.WriteLine("  Delete...");
            Controller.PrepareTestTmp(parms);

            //
            // Scenarios to test:
            // - archive: remove single file
            // - archive: remove multiple files (wildcard)
            // - archive: remove MacZip main entry with MacZip enabled/disabled
            // - disk: remove single file
            // - disk: remove non-empty directory (recurse enabled / disabled)
            // - (fail) execute command with no filespec
            //

            parms.MacZip = true;
            parms.Recurse = true;
            parms.StripPaths = false;

            TestFileArchive(parms);
            TestMacZip(parms);
            TestDiskImage(parms);
            TestTurducken(parms);

            Controller.RemoveTestTmp(parms);
        }

        private static void TestFileArchive(ParamsBag parms) {
            // Copy the simple-dir-test disk image contents into a NuFX archive.
            string srcFile = Path.Join(Controller.TEST_DATA, "prodos", "simple-dir-test.po");
            string nufxTestName = "test.shk";
            string arcFile = Path.Join(Controller.TEST_TMP, nufxTestName);
            if (!ArcUtil.HandleCreateFileArchive("cfa", new string[] { arcFile }, parms)) {
                throw new Exception("cfa " + arcFile + " failed");
            }
            if (!Copy.HandleCopy("cp", new string[] { srcFile, arcFile }, parms)) {
                throw new Exception("cp " + srcFile + " -> " + arcFile + " failed");
            }

            string oldCurrentDir = Environment.CurrentDirectory;
            try {
                Environment.CurrentDirectory = Controller.TEST_TMP;

                // Try with no filespec.  Should fail (for safety).
                parms.Recurse = true;
                if (Delete.HandleDelete("rm", new string[] { nufxTestName }, parms)) {
                    throw new Exception("Unexpectedly deleted everything");
                }

                // Try to delete a directory with recursion disabled.  We don't create directory
                // entries for NuFX archives, so this should fail.
                parms.Recurse = false;
                if (Delete.HandleDelete("rm",
                        new string[] { nufxTestName, "SUBDIR1/SUBDIR2/SUBDIR3" }, parms)) {
                    throw new Exception("Removed directory with !recurse");
                }
                // Retry with recursion enabled.  Should succeed.
                parms.Recurse = true;
                if (!Delete.HandleDelete("rm",
                        new string[] { nufxTestName, "SUBDIR1/SUBDIR2/SUBDIR3" }, parms)) {
                    throw new Exception("Failed to remove directory with recurse");
                }

                // Remove multiple files with a wildcard.
                if (!Delete.HandleDelete("rm", new string[] {
                            nufxTestName, "SUBDIR1:SUBDIR2:A?", "SUBDIR1/SUBDIR2/A1?", "SUBDIR1/?"
                        }, parms)) {
                    throw new Exception("Failed to remove A? A1?");
                }

                // List the contents and check them.
                MemoryStream stdout = Controller.CaptureConsoleOut();
                if (!Catalog.HandleList("list", new string[] { nufxTestName }, parms)) {
                    throw new Exception("Error: list '" + nufxTestName + "' failed");
                }
                string[] expected = new string[] {
                    "SUBDIR1:SUBDIR2:A20",
                    "SUBDIR1:SUBDIR2:A21",
                    "SUBDIR1:SUBDIR2:A22",
                    "SUBDIR1:SUBDIR2:A23",
                    "SUBDIR1:SUBDIR2:A24",
                    "SUBDIR1:SUBDIR2:A25",
                    "SUBDIR1:SUBDIR2:A26",
                    "FILES.ADD.WITH",
                    "PRODOS.1.1.1",
                };
                Controller.CompareLines(expected, stdout);
            } finally {
                Environment.CurrentDirectory = oldCurrentDir;
            }
        }

        private static void TestMacZip(ParamsBag parms) {
            // Grab a copy of the ZIP archive with MacZip contents.
            string zipTestName = "GSHK11.zip";
            FileUtil.CopyFile(Path.Join(Controller.TEST_DATA, "zip", zipTestName),
                Path.Join(Controller.TEST_TMP, zipTestName));

            string oldCurrentDir = Environment.CurrentDirectory;
            try {
                Environment.CurrentDirectory = Controller.TEST_TMP;

                // Remove a MacZip entry with the feature enabled.  Should remove both parts.
                parms.MacZip = true;
                if (!Delete.HandleDelete("rm", new string[] { zipTestName, "GSHK" }, parms)) {
                    throw new Exception("rm " + zipTestName + " GSHK failed");
                }

                // Remove a "header" entry with the feature enabled.  This currently succeeds,
                // but leaves the primary entry intact.  (This may not be desirable.)
                if (!Delete.HandleDelete("rm",
                        new string[] { zipTestName, "__MACOSX/._gshk.docs" }, parms)) {
                    throw new Exception("rm " + zipTestName + " ._gshk.docs failed");
                }

                // Remove a MacZip entry with the feature disabled.  Should leave the ._ file.
                parms.MacZip = false;
                if (!Delete.HandleDelete("rm",
                        new string[] { zipTestName, "Release.Notes" }, parms)) {
                    throw new Exception("rm " + zipTestName + " Release.Notes failed");
                }

                // List the contents and check them.
                MemoryStream stdout = Controller.CaptureConsoleOut();
                if (!Catalog.HandleList("list", new string[] { zipTestName }, parms)) {
                    throw new Exception("Error: list '" + zipTestName + "' failed");
                }
                string[] expected = new string[] {
                    "gshk.docs",
                    "__MACOSX/._gshk.docs",
                    "__MACOSX/._Release.Notes",
                };
                Controller.CompareLines(expected, stdout);
            } finally {
                Environment.CurrentDirectory = oldCurrentDir;
            }
        }

        private static void TestDiskImage(ParamsBag parms) {
            // Grab a copy of a ProDOS disk image.
            string proTestName = "simple-dir-test.po";
            FileUtil.CopyFile(Path.Join(Controller.TEST_DATA, "prodos", proTestName),
                Path.Join(Controller.TEST_TMP, proTestName));

            string oldCurrentDir = Environment.CurrentDirectory;
            try {
                Environment.CurrentDirectory = Controller.TEST_TMP;

                // Try with no filespec.  Should fail (for safety).
                parms.Recurse = true;
                if (Delete.HandleDelete("rm", new string[] { proTestName }, parms)) {
                    throw new Exception("Unexpectedly deleted everything");
                }

                // Try to delete a non-empty directory with recursion disabled.
                parms.Recurse = false;
                if (Delete.HandleDelete("rm",
                        new string[] { proTestName, "SUBDIR1/SUBDIR2/SUBDIR3" }, parms)) {
                    throw new Exception("Removed directory with !recurse");
                }
                // Retry with recursion enabled.  Should succeed.
                parms.Recurse = true;
                if (!Delete.HandleDelete("rm",
                        new string[] { proTestName, "SUBDIR1/SUBDIR2/SUBDIR3" }, parms)) {
                    throw new Exception("Failed to remove directory with recurse");
                }

                // Remove multiple files with a wildcard.
                if (!Delete.HandleDelete("rm", new string[] {
                            proTestName, "SUBDIR1:SUBDIR2:A?", "SUBDIR1/SUBDIR2/A1?", "SUBDIR1/?"
                        }, parms)) {
                    throw new Exception("Failed to remove A? A1?");
                }

                // List the contents and check them.
                MemoryStream stdout = Controller.CaptureConsoleOut();
                if (!Catalog.HandleList("list", new string[] { proTestName }, parms)) {
                    throw new Exception("Error: list '" + proTestName + "' failed");
                }
                string[] expected = new string[] {
                    "SUBDIR1",
                    "SUBDIR1:SUBDIR2",
                    "SUBDIR1:SUBDIR2:A20",
                    "SUBDIR1:SUBDIR2:A21",
                    "SUBDIR1:SUBDIR2:A22",
                    "SUBDIR1:SUBDIR2:A23",
                    "SUBDIR1:SUBDIR2:A24",
                    "SUBDIR1:SUBDIR2:A25",
                    "SUBDIR1:SUBDIR2:A26",
                    "FILES.ADD.WITH",
                    "PRODOS.1.1.1",
                };
                Controller.CompareLines(expected, stdout);
            } finally {
                Environment.CurrentDirectory = oldCurrentDir;
            }
        }

        private static void TestTurducken(ParamsBag parms) {
            // Make a copy of a turducken file.
            string inputFile = Path.Join(Controller.TEST_DATA, "turducken", "MultiPart.hdv");
            string tdnFile = Path.Join(Controller.TEST_TMP, "tdn-test.hdv");
            FileUtil.CopyFile(inputFile, tdnFile);
            parms.SkipSimple = true;

            // Delete a file from the gzip-compressed disk volume.
            string extArc = tdnFile + ExtArchive.SPLIT_CHAR + "2" + ExtArchive.SPLIT_CHAR +
                "small.woz.gz";
            if (!Delete.HandleDelete("rm", new string[] { extArc, "hello.txt" }, parms)) {
                throw new Exception("add " + extArc + " hello.txt failed");
            }

            // List the contents.
            MemoryStream stdout = Controller.CaptureConsoleOut();
            if (!Catalog.HandleList("list", new string[] { extArc }, parms)) {
                throw new Exception("list '" + extArc + "' failed");
            }
            string[] expected = new string[] {
                "again.txt",
            };
            Controller.CompareLines(expected, stdout);

            // Repeat the procedure with a ZIP archive.
            extArc = tdnFile + ExtArchive.SPLIT_CHAR + "2" + ExtArchive.SPLIT_CHAR +
                "Some.Files.zip";
            if (!Delete.HandleDelete("rm", new string[] { extArc, "subdir/file3.txt" }, parms)) {
                throw new Exception("rm " + extArc + "subdir/file3.txt failed");
            }
            stdout = Controller.CaptureConsoleOut();
            if (!Catalog.HandleList("list", new string[] { extArc }, parms)) {
                throw new Exception("list '" + extArc + "' failed");
            }
            expected = new string[] {
                "file1.txt",
                "file2.txt"
            };
            Controller.CompareLines(expected, stdout);
        }
    }
}
