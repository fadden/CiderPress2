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
    /// Tests the "mkdir" command.
    /// </summary>
    internal static class TestMkdir {
        public static void RunTest(ParamsBag parms) {
            Controller.Stdout.WriteLine("  Mkdir...");
            Controller.PrepareTestTmp(parms);

            DoTests(parms);
            TestTurducken(parms);

            Controller.RemoveTestTmp(parms);
        }

        private static void DoTests(ParamsBag parms) {
            string oldCurrentDir = Environment.CurrentDirectory;
            try {
                Environment.CurrentDirectory = Controller.TEST_TMP;

                string newDirName = "SUBDIR";

                // Test ZIP.  Technically these allow directories, as zero-length records that
                // end in '/', but we don't currently support that.
                string zipTest = "zip-test.zip";
                if (!ArcUtil.HandleCreateFileArchive("cfa", new string[] { zipTest }, parms)) {
                    throw new Exception("cfa " + zipTest + " failed");
                }
                if (Mkdir.HandleMkdir("mkdir", new string[] { zipTest, newDirName }, parms)) {
                    throw new Exception("mkdir in ZIP succeeded");
                }

                string dosTest = "dos-test.do";
                if (!DiskUtil.HandleCreateDiskImage("cdi",
                        new string[] { dosTest, "140k", "dos" }, parms)) {
                    throw new Exception("cdi " + dosTest + " failed");
                }
                if (Mkdir.HandleMkdir("mkdir", new string[] { dosTest, newDirName }, parms)) {
                    throw new Exception("mkdir in DOS disk succeeded");
                }

                string proTest = "pro-test.po";
                if (!DiskUtil.HandleCreateDiskImage("cdi",
                        new string[] { proTest, "140k", "prodos" }, parms)) {
                    throw new Exception("cdi " + proTest + " failed");
                }
                if (!Mkdir.HandleMkdir("mkdir", new string[] { proTest, newDirName }, parms)) {
                    throw new Exception("mkdir in ProDOS disk failed");
                }
                // Creating an existing directory should do nothing, and report success.
                if (!Mkdir.HandleMkdir("mkdir", new string[] { proTest, newDirName }, parms)) {
                    throw new Exception("repeat mkdir failed");
                }

                // Create multiple levels at once.
                string deepDirName = newDirName + "/DIR2/DIR3/DIR4";
                if (!Mkdir.HandleMkdir("mkdir", new string[] { proTest, deepDirName }, parms)) {
                    throw new Exception("deep mkdir in ProDOS disk failed");
                }

                // This should fail cleanly.
                string badDirName = "a::b";
                if (Mkdir.HandleMkdir("mkdir", new string[] { proTest, badDirName }, parms)) {
                    throw new Exception("invalid partial path mkdir succeeded");
                }

                // Verify results with the output from "list", which uses ':' to separate names.
                MemoryStream stdout = Controller.CaptureConsoleOut();
                if (!Catalog.HandleList("list", new string[] { proTest }, parms)) {
                    throw new Exception("Error: list '" + proTest + "' failed");
                }
                string[] expected = new string[] {
                    newDirName,
                    newDirName + ":DIR2",
                    newDirName + ":DIR2:DIR3",
                    newDirName + ":DIR2:DIR3:DIR4"
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

            // Create a directory in the gzip-compressed disk volume.
            string extArc = tdnFile + ExtArchive.SPLIT_CHAR + "2" + ExtArchive.SPLIT_CHAR +
                "small.woz.gz";
            if (!Mkdir.HandleMkdir("mkdir", new string[] { extArc, "NEWDIR" }, parms)) {
                throw new Exception("mkdir " + extArc + " NEWDIR failed");
            }

            // List the contents.
            MemoryStream stdout = Controller.CaptureConsoleOut();
            if (!Catalog.HandleList("list", new string[] { extArc }, parms)) {
                throw new Exception("list '" + extArc + "' failed");
            }
            string[] expected = new string[] {
                "hello.txt",
                "again.txt",
                "NEWDIR"
            };
            Controller.CompareLines(expected, stdout);

            // Can't mkdir in a file archive.
        }
    }
}
