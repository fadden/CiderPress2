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

using AppCommon;
using CommonUtil;
using DiskArc;
using DiskArc.Disk;
using static DiskArc.Defs;
using static DiskArc.IFileSystem;

namespace cp2.Tests {
    /// <summary>
    /// Tests miscellaneous behaviors.
    /// </summary>
    internal static class TestMisc {
        public static void RunTest(ParamsBag parms) {
            Controller.Stdout.WriteLine("  Misc...");
            Controller.PrepareTestTmp(parms);

            parms.FromNAPS = true;

            TestCtrlNames(parms);
            TestReadOnly(parms);

            // AppleWorks filename formatting has been disabled.
            //TestAppleWorksName(parms);

            Controller.RemoveTestTmp(parms);
        }

        /// <summary>
        /// Tests extract/add of a file with control characters in the filename.
        /// </summary>
        /// <remarks>
        /// <para>DOS and HFS are reasonable candidates for this.  Both allow the full set of
        /// control characters, though HFS discourages '\0'.  Some host filesystems, such as
        /// Windows, disallow control characters, so we want to add it "manually" to the disk
        /// image, extract it, and add it.  (NAPS-escaping would also work, but that's part of
        /// what we're testing here, so best to add it directly.)</para>
        /// </remarks>
        private static void TestCtrlNames(ParamsBag parms) {
            string baseName = "Ctrl\tFile\nName\u007f!";
            string ctrlFileName = PathName.PrintifyControlChars(baseName);
            string napsFileName = PathName.EscapeFileName(baseName) + "#000000";

            string oldCurrentDir = Environment.CurrentDirectory;
            try {
                Environment.CurrentDirectory = Controller.TEST_TMP;

                // Create an HFS volume.
                string diskPathName = "ctrl-test.po";
                if (!DiskUtil.HandleCreateDiskImage("cdi",
                        new string[] { diskPathName, "800k", "hfs" }, parms)) {
                    throw new Exception("cdi " + diskPathName + " failed");
                }

                // Add a file with some control characters (converted to printable).
                using (FileStream stream = new FileStream(diskPathName, FileMode.Open)) {
                    using (IDiskImage disk = UnadornedSector.OpenDisk(stream, parms.AppHook)) {
                        disk.AnalyzeDisk();
                        IFileSystem fs = (IFileSystem)disk.Contents!;
                        fs.PrepareFileAccess(true);
                        IFileEntry volDir = fs.GetVolDirEntry();

                        IFileEntry entry = fs.CreateFile(volDir, ctrlFileName, CreateMode.File);
                        using (Stream dataStream = fs.OpenFile(entry, FileAccessMode.ReadWrite,
                                FilePart.DataFork)) {
                            for (byte b = (byte)'A'; b <= 'Z'; b++) {
                                dataStream.WriteByte(b);
                            }
                        }
                    }
                }

                // Extract the file.  It will be extracted with the Control Pictures values.
                parms.Preserve = ExtractFileWorker.PreserveMode.None;
                if (!Extract.HandleExtract("x",
                        new string[] { diskPathName, ctrlFileName }, parms)) {
                    throw new Exception("Failed to extract " + ctrlFileName);
                }

                // Add it to a new NuFX archive.
                string arcPathName = "from-hfs.shk";
                if (!Add.HandleAdd("a", new string[] { arcPathName, ctrlFileName }, parms)) {
                    throw new Exception("Failed to create/add " + arcPathName + " '" +
                        ctrlFileName + "'");
                }

                // List the contents of the NuFX archive and confirm it matches.
                MemoryStream stdout = Controller.CaptureConsoleOut();
                if (!Catalog.HandleList("list", new string[] { arcPathName }, parms)) {
                    throw new Exception("Unable to list " + arcPathName);
                }
                string[] expected = new string[] {
                    ctrlFileName
                };
                Controller.CompareLines(expected, stdout);

                // Extract the file with NAPS preservation.
                parms.Preserve = ExtractFileWorker.PreserveMode.NAPS;
                if (!Extract.HandleExtract("x",
                        new string[] { diskPathName, ctrlFileName }, parms)) {
                    throw new Exception("Failed to extract " + ctrlFileName);
                }

                // Create a DOS 3.3 disk, and add the NAPS-escaped file.
                string dosPathName = "ctrl-test2.do";
                if (!DiskUtil.HandleCreateDiskImage("cdi",
                        new string[] { dosPathName, "140k", "dos" }, parms)) {
                    throw new Exception("cdi " + dosPathName + " failed");
                }
                if (!Add.HandleAdd("a", new string[] { dosPathName, napsFileName }, parms)) {
                    throw new Exception("Failed to add " + dosPathName + " '" +
                        napsFileName + "'");
                }

                // Check it.  DOS will keep the control chars, but convert it to upper case.
                stdout = Controller.CaptureConsoleOut();
                if (!Catalog.HandleList("list", new string[] { dosPathName }, parms)) {
                    throw new Exception("Unable to list " + dosPathName);
                }
                expected = new string[] {
                    ctrlFileName.ToUpperInvariant()
                };
                Controller.CompareLines(expected, stdout);
            } finally {
                Environment.CurrentDirectory = oldCurrentDir;
            }
        }

        // Test AppleWorks filename conversion handling.
        private static void TestAppleWorksName(ParamsBag parms) {
            string oldCurrentDir = Environment.CurrentDirectory;
            try {
                Environment.CurrentDirectory = Controller.TEST_TMP;

                // File should be treated as "AW.Test File" on ProDOS.
                const int TEST_FILE_LEN = 500;
                string sampleName = "aw test.file#197f0f";
                string sampleNameNoTag = "aw test.file";
                string sampleAWName = "AW.Test File";
                Controller.CreateSampleFile(sampleName, 0xaa, TEST_FILE_LEN);

                string diskFileName = "AWTest.po";
                if (!DiskUtil.HandleCreateDiskImage("cdi",
                        new string[] { diskFileName, "140k", "prodos" }, parms)) {
                    throw new Exception("Error: cdi " + diskFileName + " failed");
                }
                if (!Add.HandleAdd("add", new string[] { diskFileName, sampleName }, parms)) {
                    throw new Exception("Error: add " + diskFileName + " '" + sampleName +
                        "' failed");
                }

                MemoryStream stdout = Controller.CaptureConsoleOut();
                if (!Catalog.HandleList("list", new string[] { diskFileName }, parms)) {
                    throw new Exception("Error: list '" + diskFileName + "' failed");
                }
                string[] expected = new string[] {
                    sampleAWName
                };
                Controller.CompareLines(expected, stdout);

                // Extract to confirm that the extraction code preserved the filename.
                parms.Preserve = ExtractFileWorker.PreserveMode.None;
                if (!Extract.HandleExtract("x", new string[] { diskFileName }, parms)) {
                    throw new Exception("Error: extract " + diskFileName + " failed");
                }
                FileInfo info = new FileInfo(sampleAWName);
                if (!info.Exists || !(info.Length == TEST_FILE_LEN)) {
                    throw new Exception("Error: bad extract of '" + sampleAWName + "'");
                }

                // Repeat the procedure with a NuFX archive, which should *not* adjust the name.
                // (This isn't a requirement; we're just verifying expected behavior.)
                string arcFileName = "AWTest.shk";
                if (!Add.HandleAdd("add", new string[] { arcFileName, sampleName }, parms)) {
                    throw new Exception("Error: add " + arcFileName + " failed");
                }

                stdout = Controller.CaptureConsoleOut();
                if (!Catalog.HandleList("list", new string[] { arcFileName }, parms)) {
                    throw new Exception("Error: list '" + arcFileName + "' failed");
                }
                expected = new string[] {
                    sampleNameNoTag
                };
                Controller.CompareLines(expected, stdout);
            } finally {
                Environment.CurrentDirectory = oldCurrentDir;
            }
        }

        private static void TestReadOnly(ParamsBag parms) {
            // Grab a copy of a ProDOS disk image.
            string proTestName = "simple-dir-test.po";
            FileUtil.CopyFile(Path.Join(Controller.TEST_DATA, "prodos", proTestName),
                Path.Join(Controller.TEST_TMP, proTestName));

            string oldCurrentDir = Environment.CurrentDirectory;
            try {
                Environment.CurrentDirectory = Controller.TEST_TMP;

                // Delete a file to make sure things are set up correctly.
                if (!Delete.HandleDelete("rm",
                        new string[] { proTestName, "FILES.ADD.WITH" }, parms)) {
                    throw new Exception("Initial rm failed");
                }

                // Mark the disk read-only.
                FileInfo finfo = new FileInfo(proTestName);
                finfo.IsReadOnly = true;

                // Try another deletion, should fail cleanly.
                if (Delete.HandleDelete("rm",
                        new string[] { proTestName, "FILES.ADD.WITH" }, parms)) {
                    throw new Exception("Read-only rm succeeded");
                }

                // The read-only "list" operation should succeed.
                if (!Catalog.HandleList("list", new string[] { proTestName }, parms)) {
                    throw new Exception("Read-only list failed");
                }
            } finally {
                Environment.CurrentDirectory = oldCurrentDir;
            }
        }
    }
}
