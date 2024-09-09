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
using DiskArc;
using DiskArc.Arc;
using DiskArc.Disk;

namespace cp2.Tests {
    /// <summary>
    /// Tests the "get-attr" and "set-attr" commands.
    /// </summary>
    internal static class TestFileAttr {
        public static void RunTest(ParamsBag parms) {
            Controller.Stdout.WriteLine("  FileAttr...");
            Controller.PrepareTestTmp(parms);

            //
            // Test scenarios:
            // - Simple NuFX test with all edits, hex.
            // - Simple NuFX test with all edits, strings.
            // - Set mod date on ProDOS volume dir.
            // - MacZip edit
            //
            // Use get-attr to verify that changes were made.
            //
            // TODO: test recursion on/off
            //

            parms.MacZip = true;
            parms.Recurse = true;

            TestEdits(parms);
            TestVolDir(parms);
            TestMacZip(parms);
            TestTurducken(parms);

            Controller.RemoveTestTmp(parms);
        }

        private static void TestEdits(ParamsBag parms) {
            string oldCurrentDir = Environment.CurrentDirectory;
            try {
                Environment.CurrentDirectory = Controller.TEST_TMP;

                string nufxTest = "test.shk";
                if (!ArcUtil.HandleCreateFileArchive("cfa", new string[] { nufxTest }, parms)) {
                    throw new Exception("cfa " + nufxTest + " failed");
                }

                string sampleFile = "sample.txt";
                using (File.Create(sampleFile)) { }

                if (!Add.HandleAdd("add", new string[] { nufxTest, sampleFile }, parms)) {
                    throw new Exception("add " + nufxTest + " " + sampleFile + " failed");
                }

                string[] args = new string[] {
                    nufxTest,
                    "Type=$12," +
                    "aux=0x3456," +
                    "hfstype=$112233aA," +
                    "creator=0x334455Ee," +
                    "access=$03," +
                    "CDATE=01-Jun-1977 09:05:25," +
                    "mdate=1986-09-15T17:05:27Z",
                    sampleFile
                };
                if (!FileAttr.HandleSetAttr("sa", args, parms)) {
                    throw new Exception("sa " + nufxTest + " hex args failed");
                }
                using (Stream arcFile = new FileStream(nufxTest, FileMode.Open)) {
                    using (IArchive arc = NuFX.OpenArchive(arcFile, parms.AppHook)) {
                        IFileEntry entry = arc.GetFirstEntry();
                        if (entry.FileType != 0x12 ||
                                entry.AuxType != 0x3456 ||
                                entry.HFSFileType != 0x112233aa ||
                                entry.HFSCreator != 0x334455ee ||
                                entry.Access != 0x03 ||
                                entry.CreateWhen != DateTime.Parse("01-Jun-1977 09:05:25") ||
                                entry.ModWhen != DateTime.Parse("1986-09-15T17:05:27Z")) {
                            throw new Exception("Entry attributes don't match #1");
                        }
                    }
                }

                args = new string[] {
                    nufxTest,
                    "type=LBR," +
                    "hfstype=THIS," +
                    "creator=ROCK," +
                    "access=unlocked",
                    sampleFile,
                };
                if (!FileAttr.HandleSetAttr("sa", args, parms)) {
                    throw new Exception("sa " + nufxTest + " text args failed");
                }
                using (Stream arcFile = new FileStream(nufxTest, FileMode.Open)) {
                    using (IArchive arc = NuFX.OpenArchive(arcFile, parms.AppHook)) {
                        IFileEntry entry = arc.GetFirstEntry();
                        if (entry.FileType != 0xe0 ||
                                entry.HFSFileType != 0x54484953 ||
                                entry.HFSCreator != 0x524f434b ||
                                entry.Access != FileAttribs.FILE_ACCESS_UNLOCKED) {
                            throw new Exception("Entry attributes don't match #2");
                        }
                    }
                }

                // Try a get-attr with an attribute.  Output should be a single line.
                args = new string[] {
                    nufxTest,
                    sampleFile,
                    "type"
                };
                MemoryStream stdout = Controller.CaptureConsoleOut();
                if (!FileAttr.HandleGetAttr("ga", args, parms)) {
                    throw new Exception("ga " + nufxTest + " single check failed");
                }
                Controller.CompareLines(new string[] { "0xe0" }, stdout);
            } finally {
                Environment.CurrentDirectory = oldCurrentDir;
            }
        }

        private static void TestVolDir(ParamsBag parms) {
            string oldCurrentDir = Environment.CurrentDirectory;
            try {
                Environment.CurrentDirectory = Controller.TEST_TMP;

                string proTest = "prodos-test.po";
                if (!DiskUtil.HandleCreateDiskImage("cdi",
                        new string[] { proTest, "140k", "prodos" }, parms)) {
                    throw new Exception("cdi " + proTest + " failed");
                }
                string[] args = new string[] {
                    proTest,
                    "type=TXT," +               // not expected to have an effect
                    "mdate=1986-09-15T17:06Z",  // can't represent seconds
                    "/",
                };
                if (!FileAttr.HandleSetAttr("sa", args, parms)) {
                    throw new Exception("sa " + proTest + " root failed");
                }

                using (Stream diskFile = new FileStream(proTest, FileMode.Open)) {
                    using (IDiskImage disk = UnadornedSector.OpenDisk(diskFile, parms.AppHook)) {
                        disk.AnalyzeDisk();
                        IFileSystem fs = (IFileSystem)disk.Contents!;
                        fs.PrepareFileAccess(true);
                        IFileEntry entry = fs.GetVolDirEntry();

                        if (entry.FileType != FileAttribs.FILE_TYPE_DIR ||
                                entry.ModWhen != DateTime.Parse("1986-09-15T17:06Z")) {
                            throw new Exception("Root dir attributes don't match");
                        }
                    }
                }
            } finally {
                Environment.CurrentDirectory = oldCurrentDir;
            }
        }

        private static void TestMacZip(ParamsBag parms) {
            string inputFile = Path.Join(Controller.TEST_DATA, "zip", "GSHK11.zip");
            string zipFile = "mac-zip-test.zip";
            FileUtil.CopyFile(inputFile, Path.Join(Controller.TEST_TMP, zipFile));

            string oldCurrentDir = Environment.CurrentDirectory;
            try {
                Environment.CurrentDirectory = Controller.TEST_TMP;
                const string TEST_TYPE_STR = "0x18675309";

                string[] args = new string[] {
                    zipFile,
                    "creator=" + TEST_TYPE_STR,
                    "GSHK"
                };
                parms.MacZip = true;
                if (!FileAttr.HandleSetAttr("sa", args, parms)) {
                    throw new Exception("sa " + zipFile + " enabled failed");
                }

                // Run without arg to dump the full output, then scan for the string.
                args = new string[] {
                    zipFile,
                    "GSHK"
                };
                MemoryStream stdout = Controller.CaptureConsoleOut();
                if (!FileAttr.HandleGetAttr("ga", args, parms)) {
                    throw new Exception("ga " + zipFile + " enabled check failed");
                }
                if (!Controller.HasText(TEST_TYPE_STR, stdout)) {
                    throw new Exception("Unable to find " + TEST_TYPE_STR);
                }
                if (Controller.HasText("2010", stdout)) {
                    throw new Exception("Found 2010 in output, next test won't work");
                }

                // Try again with MacZip off.  The only field that we can change in a ZIP
                // archive is the modification date.  Normally the ZIP date is overruled by
                // the copy in the ADF header.
                args = new string[] {
                    zipFile,
                    "type=S16," +
                    "mdate=28-Mar-2010",
                    "GSHK"
                };
                parms.MacZip = false;
                if (!FileAttr.HandleSetAttr("sa", args, parms)) {
                    throw new Exception("sa " + zipFile + " enabled failed");
                }

                args = new string[] {
                    zipFile,
                    "GSHK"
                };
                stdout = Controller.CaptureConsoleOut();
                if (!FileAttr.HandleGetAttr("ga", args, parms)) {
                    throw new Exception("ga " + zipFile + " enabled check failed");
                }
                if (Controller.HasText(TEST_TYPE_STR, stdout)) {
                    throw new Exception("Was able to find " + TEST_TYPE_STR);
                }
                if (!Controller.HasText("2010", stdout)) {
                    throw new Exception("Did not find new date");
                }
                if (Controller.HasText("S16", stdout)) {
                    throw new Exception("Should not be able to see S16");
                }
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

            // Set an attribute in the gzip-compressed disk volume.
            string extArc = tdnFile + ExtArchive.SPLIT_CHAR + "2" + ExtArchive.SPLIT_CHAR +
                "small.woz.gz";
            parms.StripPaths = true;
            string[] args = new string[] {
                extArc,
                "AUX=0X5150",
                "hello.txt",
            };
            if (!FileAttr.HandleSetAttr("sa", args, parms)) {
                throw new Exception("sa " + extArc + " failed");
            }

            // Run with no attr to dump contents, then search for the string.
            args = new string[] {
                extArc,
                "hello.txt",
            };
            MemoryStream stdout = Controller.CaptureConsoleOut();
            if (!FileAttr.HandleGetAttr("ga", args, parms)) {
                throw new Exception("ga " + extArc + " check disk failed");
            }
            if (!Controller.HasText("5150", stdout)) {
                throw new Exception("Did not find new aux type");
            }

            // Repeat the procedure with a ZIP archive.
            extArc = tdnFile + ExtArchive.SPLIT_CHAR + "2" + ExtArchive.SPLIT_CHAR +
                "Some.Files.zip";
            args = new string[] {
                extArc,
                "MDATE=28-mar-2010",
                "file2.txt",
            };
            if (!FileAttr.HandleSetAttr("sa", args, parms)) {
                throw new Exception("sa " + extArc + " failed");
            }
            args = new string[] {
                extArc,
                "file2.txt",
            };
            stdout = Controller.CaptureConsoleOut();
            if (!FileAttr.HandleGetAttr("ga", args, parms)) {
                throw new Exception("ga " + extArc + " check arc failed");
            }
            if (!Controller.HasText("2010", stdout)) {
                throw new Exception("Did not find new date");
            }
        }
    }
}
