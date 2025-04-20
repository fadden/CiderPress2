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
using System.Diagnostics;

using AppCommon;
using CommonUtil;
using DiskArc;
using DiskArc.Arc;

namespace cp2.Tests {
    /// <summary>
    /// Tests the "extract" command.
    /// </summary>
    /// <remarks>
    /// This relies on the proper functioning of the "add" command.  Those tests should be
    /// performed first.
    /// </remarks>
    internal static class TestExtract {
        public static void RunTest(ParamsBag parms) {
            Controller.Stdout.WriteLine("  Extract...");
            Controller.PrepareTestTmp(parms);

            //
            // We need to extract files from various things:
            // - simple data file from NuFX and ProDOS
            // - file with both data and rsrc from NuFX ProDOS
            // - HFS file with zero-length resource fork (extract WITHOUT rsrc)
            // - ProDOS file with zero-length resource fork (extract WITH rsrc)
            // - AppleSingle file with neither fork
            //
            // It should not be necessary to test all formats.  If one doesn't work, that's an
            // issue with that specific class, which should have been caught by the DiskArc
            // lib tests.
            //
            // Need to test ext-archive specifier with trailing directory (disk image only).
            //
            // We need to exercise all --preserve= options:
            // - none, adf, as, host [if available], naps
            // Each should be tested with data-only, rsrc-only, data+rsrc.
            //
            // This is a good place to exercise wildcard matching.  The glob code exercises
            // various patterns, so we just need the basics.  These need to be done on both
            // disk images and file archives:
            // - extract with no files specified
            // - extract with wildcard
            // - extract with wildcard that matches nothing
            //
            // We also need to exercise relevant options:
            // --mac-zip
            // --overwrite
            // --raw (with a DOS disk image)
            // --recurse (note disk image and file archive handling is independent)
            // --strip-paths
            //

            // Configure parameters.
            parms.ExDir = string.Empty;
            parms.MacZip = true;
            parms.Overwrite = true;
            parms.Raw = false;
            parms.Recurse = true;
            parms.Sectors = 16;
            parms.StripPaths = false;

            CreateSamples(parms);

            parms.Preserve = ExtractFileWorker.PreserveMode.NAPS;
            TestExtractSamples(NUFX_TEST_NAME, sNuFXFiles_NAPS, parms);
            TestExtractSamples(ZIP_TEST_NAME, sZipFiles_NAPS, parms);
            TestExtractSamples(HFS_TEST_NAME, sHFSFiles_NAPS, parms);
            TestExtractSamples(PRODOS_TEST_NAME, sProDOSFiles_NAPS, parms);

            parms.Preserve = ExtractFileWorker.PreserveMode.ADF;
            TestExtractSamples(HFS_TEST_NAME, sHFSFiles_ADF, parms);
            parms.Preserve = ExtractFileWorker.PreserveMode.AS;
            TestExtractSamples(HFS_TEST_NAME, sHFSFiles_AS, parms);
            if (ParamsBag.HasHostPreservation) {
                parms.Preserve = ExtractFileWorker.PreserveMode.Host;
                TestExtractSamples(HFS_TEST_NAME, sHFSFiles_Host, parms);
            }
            parms.Preserve = ExtractFileWorker.PreserveMode.None;
            TestExtractSamples(HFS_TEST_NAME, sHFSFiles_None, parms);

            // Extract the MacZip-encoded data with MacZip disabled.
            parms.Preserve = ExtractFileWorker.PreserveMode.NAPS;
            parms.MacZip = false;
            TestExtractSamples(ZIP_TEST_NAME, sZipFiles_NAPS_NoMZ, parms);
            parms.MacZip = true;

            // Test the --overwrite flag.
            parms.StripPaths = true;
            parms.Preserve = ExtractFileWorker.PreserveMode.None;
            parms.Overwrite = true;
            TestExtractSamples(ZIP_TEST_NAME, sZipFiles_None_StripO, parms);

            parms.Overwrite = false;
            TestExtractSamples(HFS_TEST_NAME, sHFSFiles_None_StripNO, parms);
            parms.Overwrite = true;
            parms.StripPaths = false;

            TestNoName(parms);
            TestTrailingDir(parms);
            TestRaw(parms);
            TestWildNoRecurse(parms);
            TestNAPSEscape(parms);
            TestEmptyDir(parms);
            TestExDir(parms);

            Controller.RemoveTestTmp(parms);
        }

        private static void TestNoName(ParamsBag parms) {
            // Create an AppleSingle file with nothing in it.  We'll get a zero-length data
            // fork, with no filename.
            string arcFileName = "Empty.as";
            string arcPath = Path.Combine(Controller.TEST_TMP, arcFileName);
            using (Stream arcStream = new FileStream(arcPath, FileMode.CreateNew)) {
                AppleSingle archive = AppleSingle.CreateArchive(2, parms.AppHook);
                archive.StartTransaction();
                archive.CommitTransaction(arcStream);
            }

            // Extract everything.  Should extract a single file, which will be given the
            // Miranda name "A".
            CreateOutputDir();
            string oldCurrentDir = Environment.CurrentDirectory;
            try {
                Environment.CurrentDirectory = OUTPUT_DIR;
                parms.Preserve = ExtractFileWorker.PreserveMode.None;
                arcPath = Path.Combine("..", arcFileName);
                if (!Extract.HandleExtract("x", new string[] { arcPath }, parms)) {
                    throw new Exception("Empty extract failed");
                }

                FileInfo info = new FileInfo(PathName.MIRANDA_FILENAME);
                if (!info.Exists || info.Length != 0) {
                    throw new Exception("Failed to find zero-length file named " +
                        PathName.MIRANDA_FILENAME);
                }
            } finally {
                Environment.CurrentDirectory = oldCurrentDir;
            }
            RemoveOutputDir();
        }

        private static void TestTrailingDir(ParamsBag parms) {
            CreateOutputDir();
            string oldCurrentDir = Environment.CurrentDirectory;
            try {
                Environment.CurrentDirectory = OUTPUT_DIR;

                // Using HFS disk image, extract a file from "test.woz:subdir1".
                string arcFileName = "test.woz";
                string arcPath = Path.Combine("..", arcFileName);
                arcPath += ExtArchive.SPLIT_CHAR + SAMPLE_SUBDIR1;

                string extractPath = "subdir2/data-only";

                parms.Preserve = ExtractFileWorker.PreserveMode.None;
                if (!Extract.HandleExtract("x", new string[] { arcPath, extractPath }, parms)) {
                    throw new Exception("Trailing dir extract failed");
                }

                FileInfo info = new FileInfo(extractPath);
                if (!info.Exists || info.Length != DATA_ONLY_DLEN + SUBDIR2_ADD_LEN) {
                    throw new Exception("Failed to find '" + extractPath + "' with len=" +
                        (DATA_ONLY_DLEN + SUBDIR2_ADD_LEN));
                }
            } finally {
                Environment.CurrentDirectory = oldCurrentDir;
            }
            RemoveOutputDir();
        }

        private static void TestRaw(ParamsBag parms) {
            CreateOutputDir();
            string oldCurrentDir = Environment.CurrentDirectory;
            try {
                Environment.CurrentDirectory = OUTPUT_DIR;
                string arcPath = "dos.do";
                if (!DiskUtil.HandleCreateDiskImage("cdi", new string[] { arcPath, "140k", "DOS" },
                        parms)) {
                    throw new Exception("cdi " + arcPath + " failed");
                }

                // Add a file with a 'B' type.  The pathname will be stripped for DOS.
                string fileToAdd = Path.Join("..", SAMPLE_NAME, DATA_ONLY_NAME);
                if (!Add.HandleAdd("add", new string[] { arcPath, fileToAdd }, parms)) {
                    throw new Exception("add " + arcPath + " '" + fileToAdd + "' failed");
                }

                // Extract in "cooked" mode.  Output length should be exact match.
                string dosName = "DATA-ONLY";
                if (!Extract.HandleExtract("x", new string[] { arcPath, dosName }, parms)) {
                    throw new Exception("extract " + arcPath + " failed");
                }
                FileInfo info = new FileInfo(dosName);
                if (info.Length != DATA_ONLY_DLEN) {
                    throw new Exception("Unexpected cooked length: " + info.Length);
                }

                // Extract again, in "raw" mode.  Output will be (len + 4), rounded up to nearest
                // multiple of 256.
                parms.Overwrite = true;
                parms.Raw = true;
                if (!Extract.HandleExtract("x", new string[] { arcPath, dosName }, parms)) {
                    throw new Exception("extract " + arcPath + " failed");
                }
                info = new FileInfo(dosName);
                if (info.Length != ((DATA_ONLY_DLEN + 4 + 255) & ~255)) {
                    throw new Exception("Unexpected raw length: " + info.Length);
                }
            } finally {
                Environment.CurrentDirectory = oldCurrentDir;
                parms.Raw = false;
            }
            RemoveOutputDir();
        }

        private static void TestWildNoRecurse(ParamsBag parms) {
            CreateOutputDir();
            string oldCurrentDir = Environment.CurrentDirectory;
            try {
                Environment.CurrentDirectory = OUTPUT_DIR;
                parms.Recurse = false;
                parms.Overwrite = true;
                parms.Preserve = ExtractFileWorker.PreserveMode.None;

                string arcPath = Path.Combine("..", ZIP_TEST_NAME);
                string extFiles = "subdir1:*";
                if (!Extract.HandleExtract("x", new string[] { arcPath, extFiles }, parms)) {
                    throw new Exception("Extract from " + arcPath + " of '" + extFiles +
                        "' failed");
                }

                if (Extract.HandleExtract("x", new string[] { arcPath, extFiles, "NOPE" }, parms)) {
                    throw new Exception("Extract from " + arcPath + " of 'NOPE' succeeded");
                }

                arcPath = Path.Combine("..", HFS_TEST_NAME);
                extFiles = "subdir1:subdir2:*";
                if (!Extract.HandleExtract("x", new string[] { arcPath, extFiles }, parms)) {
                    throw new Exception("Extract from " + arcPath + " of '" + extFiles +
                        "' failed");
                }

                // Compare to expected file list.
                List<string> allFiles = new List<string>();
                GatherFileEntries(".", allFiles, parms);
                allFiles.Sort(StringComparer.Ordinal);
                VerifyList(allFiles, sMixedFiles_None_WNR);

            } finally {
                Environment.CurrentDirectory = oldCurrentDir;
            }
            RemoveOutputDir();
        }

        private static void TestNAPSEscape(ParamsBag parms) {
                        CreateOutputDir();
            string illPath =
                Path.GetFullPath(Path.Join(Controller.TEST_DATA, "as", "illegal-chars.as"));

            CreateOutputDir();
            string oldCurrentDir = Environment.CurrentDirectory;
            try {
                Environment.CurrentDirectory = OUTPUT_DIR;
                parms.Preserve = ExtractFileWorker.PreserveMode.NAPS;
                parms.FromNAPS = true;

                // Extract from archive.
                if (!Extract.HandleExtract("x", new string[] { illPath }, parms)) {
                    throw new Exception("Extract from " + illPath + "' failed");
                }

                // One of these should exist.
                string expected_win32 = @"face%2foff%3adir%5cname#000000";
                string expected_unix = @"face%2foff:dir\name#000000";
                if (!File.Exists(expected_win32) && !File.Exists(expected_unix)) {
                    throw new Exception("NAPS quoting failed: not found: '" + expected_win32 +
                        "' or '" + expected_unix + "'");
                }


                // Create an HFS disk, and add a file that has a '/' in the name, in a directory
                // that looks like it has escape codes.
                string escSubName = "%%sub%7cdir";
                string escName = "file%2ftest%%#040000";
                string escPath = Path.Combine(escSubName, escName);
                Directory.CreateDirectory(escSubName);
                Controller.CreateSampleFile(escPath, 0x22, 200);

                string diskFileName = "test-naps-esc.po";
                if (!DiskUtil.HandleCreateDiskImage("cdi",
                    new string[] { diskFileName, "800k", "HFS" }, parms)) {
                    throw new Exception("Error: cdi failed");
                }
                if (!Add.HandleAdd("add", new string[] { diskFileName, escPath }, parms)) {
                    throw new Exception("Error: add " + diskFileName + " '" + escPath + "' failed");
                }

                // Check it.
                MemoryStream stdout = Controller.CaptureConsoleOut();
                if (!Catalog.HandleList("list", new string[] { diskFileName }, parms)) {
                    throw new Exception("Error: list '" + diskFileName + "' failed");
                }
                string[] expected = {
                    "%%sub%7cdir",
                    "%%sub%7cdir:file/test%",
                };
                Controller.CompareLines(expected, stdout);

                // Delete what we created earlier, then extract to re-create it.  Extracting
                // from a disk image uses a different code path than we used earlier.
                File.Delete(escPath);
                Directory.Delete(escSubName);
                if (!Extract.HandleExtract("x", new string[] { diskFileName }, parms)) {
                    throw new Exception("Extract from '" + diskFileName + "' failed");
                }

                // Should be exactly the same.
                if (!File.Exists(escPath)) {
                    throw new Exception("NAPS quoting failed: not found: '" + escPath + "'");
                }
            } finally {
                Environment.CurrentDirectory = oldCurrentDir;
            }
            RemoveOutputDir();
        }

        private static void TestEmptyDir(ParamsBag parms) {
            CreateOutputDir();
            string oldCurrentDir = Environment.CurrentDirectory;
            try {
                Environment.CurrentDirectory = OUTPUT_DIR;
                parms.Recurse = true;
                parms.Preserve = ExtractFileWorker.PreserveMode.NAPS;

                string diskFileName = "test-empty-dir.po";
                string dirName = "SUBDIR1";
                if (!DiskUtil.HandleCreateDiskImage("cdi",
                        new string[] { diskFileName, "140K", "PRODOS" }, parms)) {
                    throw new Exception("Error: cdi failed");
                }
                if (!Mkdir.HandleMkdir("mkdir", new string[] { diskFileName, dirName }, parms)) {
                    throw new Exception("mkdir failed");
                }

                parms.StripPaths = true;
                if (!Extract.HandleExtract("x", new string[] { diskFileName }, parms)) {
                    throw new Exception("Extract from '" + diskFileName + "' failed");
                }
                if (Directory.Exists(dirName)) {
                    throw new Exception("Extraction of empty dir succeeded with stripping on");
                }

                parms.StripPaths = false;
                if (!Extract.HandleExtract("x", new string[] { diskFileName }, parms)) {
                    throw new Exception("Extract from '" + diskFileName + "' failed");
                }
                if (!Directory.Exists(dirName)) {
                    throw new Exception("Extraction of empty dir failed");
                }

                string dirName2 = "SUBDIR1/SUBDIR2";
                if (!Mkdir.HandleMkdir("mkdir", new string[] { diskFileName, dirName2 }, parms)) {
                    throw new Exception("mkdir 2 failed");
                }
                if (!Extract.HandleExtract("x", new string[] { diskFileName }, parms)) {
                    throw new Exception("Extract 2 from '" + diskFileName + "' failed");
                }
                if (!Directory.Exists(dirName2)) {
                    throw new Exception("Extraction of empty dir 2 failed");
                }
            } finally {
                Environment.CurrentDirectory = oldCurrentDir;
            }
            RemoveOutputDir();
        }

        private static void TestExDir(ParamsBag parms) {
            string diskPath =
                Path.GetFullPath(Path.Join(Controller.TEST_DATA, "prodos", "extended.do"));
            string testFileName = "Helvetica";
            string dataOut = "Helvetica#c80001";
            string rsrcOut = dataOut + "r";

            CreateOutputDir();
            string oldCurrentDir = Environment.CurrentDirectory;
            try {
                const string SUBDIR_NAME = "subdir";
                Environment.CurrentDirectory = OUTPUT_DIR;
                string subDir = Path.Combine(OUTPUT_DIR, SUBDIR_NAME);
                Directory.CreateDirectory(subDir);

                parms.Preserve = ExtractFileWorker.PreserveMode.NAPS;

                // Extract to current dir.
                if (!Extract.HandleExtract("x", new string[] { diskPath, testFileName }, parms)) {
                    throw new Exception("Extract from '" + diskPath + "' failed");
                }
                if (!File.Exists(dataOut) || !File.Exists(rsrcOut)) {
                    throw new Exception("Extract " + testFileName + " to cwd failed");
                }

                // Extract to subdir, partial path.
                parms.ExDir = SUBDIR_NAME;
                if (!Extract.HandleExtract("x", new string[] { diskPath, testFileName }, parms)) {
                    throw new Exception("Extract from '" + diskPath + "' failed");
                }
                if (!File.Exists(Path.Combine(SUBDIR_NAME, dataOut)) ||
                        !File.Exists(Path.Combine(SUBDIR_NAME, rsrcOut))) {
                    throw new Exception("Extract " + testFileName + " to subdir failed");
                }

                parms.ExDir = string.Empty;
            } finally {
                Environment.CurrentDirectory = oldCurrentDir;
            }
            RemoveOutputDir();
        }

        #region Setup

        private const string OUTPUT_NAME = "Output";
        private static readonly string OUTPUT_DIR = Path.Combine(Controller.TEST_TMP, OUTPUT_NAME);

        private const string SAMPLE_NAME = "Samples";
        private static readonly string SAMPLE_DIR = Path.Combine(Controller.TEST_TMP, SAMPLE_NAME);
        private static readonly string SAMPLE_SUBDIR1 = "subdir1";
        private static readonly string SAMPLE_SUBDIR2 = Path.Combine(SAMPLE_SUBDIR1, "subdir2");

        private const string NAPS_TAG = "#06c0de";
        private const string NAPS_HFS_TAG = "#5445535423435032";    // 'TEST' '#CP2'

        private const string PLAIN_NAME = "Plain.File";
        private const string DATA_ONLY_NAME = "data-only" + NAPS_TAG;
        private const string RSRC_ONLY_NAME = "rsrc-only" + NAPS_TAG;
        private const string DATA_RSRC_NAME = "data-rsrc" + NAPS_TAG;
        private const string DATA_RSRC_Z_NAME = "data-rsrcZ" + NAPS_TAG;
        private const string DATA_Z_RSRC_NAME = "dataZ-rsrc" + NAPS_TAG;
        private const string HFS_FILE = "HFS.file" + NAPS_HFS_TAG;
        private const int PLAIN_DLEN = 1100;
        private const int DATA_ONLY_DLEN = 1200;
        private const int RSRC_ONLY_RLEN = 1330;
        private const int DATA_RSRC_DLEN = 1400;
        private const int DATA_RSRC_RLEN = 1550;
        private const int DATA_RSRC_Z_DLEN = 1600;
        private const int DATA_Z_RSRC_RLEN = 1770;
        private const int HFS_FILE_DLEN = 1800;
        private const int HFS_FILE_RLEN = 1990;
        private const int SUBDIR1_ADD_LEN = 2000;
        private const int SUBDIR2_ADD_LEN = 4000;

        private const string NUFX_TEST_NAME = "test.shk";
        private const string ZIP_TEST_NAME = "test.zip";
        private const string PRODOS_TEST_NAME = "test.po";
        private const string HFS_TEST_NAME = "test.woz";

        private static readonly DateTime MOD_WHEN = new DateTime(1986, 9, 15, 4, 5, 0);

        private class SampleFile {
            public string PathName { get; private set; }
            public int DataLength { get; private set; }
            public int RsrcLength { get; private set; }

            public SampleFile(string pathName, int dataLength, int rsrcLength) {
                PathName = pathName;
                DataLength = dataLength;
                RsrcLength = rsrcLength;
            }
        }

        private static SampleFile[] sSampleFiles = new SampleFile[] {
            new SampleFile(PLAIN_NAME, PLAIN_DLEN, -1),
            new SampleFile(DATA_ONLY_NAME, DATA_ONLY_DLEN, -1),
            new SampleFile(RSRC_ONLY_NAME, -1, RSRC_ONLY_RLEN),
            new SampleFile(DATA_RSRC_NAME, DATA_RSRC_DLEN, DATA_RSRC_RLEN),
            new SampleFile(DATA_RSRC_Z_NAME, DATA_RSRC_Z_DLEN, 0),
            new SampleFile(DATA_Z_RSRC_NAME, 0, DATA_Z_RSRC_RLEN),
            new SampleFile(HFS_FILE, HFS_FILE_DLEN, HFS_FILE_RLEN),
            new SampleFile(Path.Combine(SAMPLE_SUBDIR1, DATA_ONLY_NAME),
                DATA_ONLY_DLEN + SUBDIR1_ADD_LEN, -1),
            new SampleFile(Path.Combine(SAMPLE_SUBDIR1, DATA_RSRC_NAME),
                DATA_RSRC_DLEN + SUBDIR1_ADD_LEN, DATA_RSRC_RLEN + SUBDIR1_ADD_LEN),
            new SampleFile(Path.Combine(SAMPLE_SUBDIR2, DATA_ONLY_NAME),
                DATA_ONLY_DLEN + SUBDIR2_ADD_LEN, -1),
            new SampleFile(Path.Combine(SAMPLE_SUBDIR2, DATA_RSRC_NAME),
                DATA_RSRC_DLEN + SUBDIR2_ADD_LEN, DATA_RSRC_RLEN + SUBDIR2_ADD_LEN),
        };

        /// <summary>
        /// Creates sample files.  Individual files are created in "TestTmp/Samples", then
        /// added to new archives in the "TestTmp" directory.
        /// </summary>
        private static void CreateSamples(ParamsBag parms) {
            List<string> addArgsList = new List<string>();
            addArgsList.Add("ext-archive-placeholder");

            foreach (SampleFile sf in sSampleFiles) {
                // Create directories.  This will fail if we skip a level.
                string fullPath = Path.Combine(SAMPLE_DIR, sf.PathName);
                string dirName = Path.GetDirectoryName(fullPath)!;
                if (!Directory.Exists(dirName)) {
                    Directory.CreateDirectory(dirName);
                }
                if (sf.DataLength >= 0) {
                    Controller.CreateSampleFile(fullPath, 0xcc, sf.DataLength);
                    File.SetLastWriteTime(fullPath, MOD_WHEN);
                    addArgsList.Add(sf.PathName);
                }
                if (sf.RsrcLength >= 0) {
                    Controller.CreateSampleFile(fullPath + "r", 0xee, sf.RsrcLength);
                    File.SetLastWriteTime(fullPath + "r", MOD_WHEN);
                    addArgsList.Add(sf.PathName + "r");
                }
            }
            string[] addArgs = addArgsList.ToArray();

            // Use the "add" command handler to create the archives.
            string oldCurrentDir = Environment.CurrentDirectory;
            try {
                Environment.CurrentDirectory = SAMPLE_DIR;

                // File archives can be created and added to in a single operation.
                addArgs[0] = Path.Combine("..", NUFX_TEST_NAME);
                if (!Add.HandleAdd("add", addArgs, parms)) {
                    throw new Exception("Error: add '" + addArgs[0] + "' failed");
                }
                addArgs[0] = Path.Combine("..", ZIP_TEST_NAME);
                if (!Add.HandleAdd("add", addArgs, parms)) {
                    throw new Exception("Error: add '" + addArgs[0] + "' failed");
                }

                // Disk archives must be created then populated.
                addArgs[0] = Path.Combine("..", PRODOS_TEST_NAME);
                if (!DiskUtil.HandleCreateDiskImage("cdi",
                        new string[] { addArgs[0], "140k", "prodos" }, parms)) {
                    throw new Exception("Error: cdi '" + addArgs[0] + "' failed");
                }
                if (!Add.HandleAdd("add", addArgs, parms)) {
                    throw new Exception("Error: add '" + addArgs[0] + "' failed");
                }
                addArgs[0] = Path.Combine("..", HFS_TEST_NAME);
                if (!DiskUtil.HandleCreateDiskImage("cdi",
                        new string[] { addArgs[0], "800k", "hfs" }, parms)) {
                    throw new Exception("Error: cdi '" + addArgs[0] + "' failed");
                }
                if (!Add.HandleAdd("add", addArgs, parms)) {
                    throw new Exception("Error: add '" + addArgs[0] + "' failed");
                }
            } finally {
                Environment.CurrentDirectory = oldCurrentDir;
            }
        }

        #endregion Setup

        #region Basic extract test

        private class ExpectedFile {
            public string FileName { get; private set; }
            public int MinLength { get; private set; }
            public int MaxLength { get; private set; }

            public ExpectedFile(string fileName, int length) {
                FileName = fileName;
                MinLength = MaxLength = length;
            }
            public ExpectedFile(string fileName, int minLength, int maxLength) {
                FileName = fileName;
                MinLength = minLength;
                MaxLength = maxLength;
            }
        }

        // Maximum expected AppleSingle overhead.
        private const int AS_OVHD = 512;
        // Resource fork suffix.
        private const string RSRC_SFX = "/..namedfork/rsrc";
        private const string NAPS_NO_TYPE = "#000000";
        private const string NAPS_HFS_DEFAULT_TYPE = "#42494e4143504949";

        // NuFX: can store files that only have resource forks
        private static ExpectedFile[] sNuFXFiles_NAPS = new ExpectedFile[] {
            new ExpectedFile(HFS_FILE, HFS_FILE_DLEN),
            new ExpectedFile(HFS_FILE + "r", HFS_FILE_RLEN),
            new ExpectedFile(PLAIN_NAME + NAPS_NO_TYPE, PLAIN_DLEN),
            new ExpectedFile(DATA_ONLY_NAME, DATA_ONLY_DLEN),
            new ExpectedFile(DATA_RSRC_NAME, DATA_RSRC_DLEN),
            new ExpectedFile(DATA_RSRC_NAME + "r", DATA_RSRC_RLEN),
            new ExpectedFile(DATA_RSRC_Z_NAME, DATA_RSRC_Z_DLEN),
            new ExpectedFile(DATA_RSRC_Z_NAME + "r", 0),
            new ExpectedFile(DATA_Z_RSRC_NAME, 0),
            new ExpectedFile(DATA_Z_RSRC_NAME + "r", DATA_Z_RSRC_RLEN),
            //new ExpectedFile(RSRC_ONLY_NAME, 0),
            new ExpectedFile(RSRC_ONLY_NAME + "r", RSRC_ONLY_RLEN),
            new ExpectedFile(Path.Combine(SAMPLE_SUBDIR1, DATA_ONLY_NAME),
                DATA_ONLY_DLEN + SUBDIR1_ADD_LEN),
            new ExpectedFile(Path.Combine(SAMPLE_SUBDIR1, DATA_RSRC_NAME),
                DATA_RSRC_DLEN + SUBDIR1_ADD_LEN),
            new ExpectedFile(Path.Combine(SAMPLE_SUBDIR1, DATA_RSRC_NAME + "r"),
                DATA_RSRC_RLEN + SUBDIR1_ADD_LEN),
            new ExpectedFile(Path.Combine(SAMPLE_SUBDIR2, DATA_ONLY_NAME),
                DATA_ONLY_DLEN + SUBDIR2_ADD_LEN),
            new ExpectedFile(Path.Combine(SAMPLE_SUBDIR2, DATA_RSRC_NAME),
                DATA_RSRC_DLEN + SUBDIR2_ADD_LEN),
            new ExpectedFile(Path.Combine(SAMPLE_SUBDIR2, DATA_RSRC_NAME + "r"),
                DATA_RSRC_RLEN + SUBDIR2_ADD_LEN),
        };
        // ZIP: keep everything
        private static ExpectedFile[] sZipFiles_NAPS = new ExpectedFile[] {
            new ExpectedFile(HFS_FILE, HFS_FILE_DLEN),
            new ExpectedFile(HFS_FILE + "r", HFS_FILE_RLEN),
            new ExpectedFile(PLAIN_NAME + NAPS_NO_TYPE, PLAIN_DLEN),
            new ExpectedFile(DATA_ONLY_NAME, DATA_ONLY_DLEN),
            new ExpectedFile(DATA_RSRC_NAME, DATA_RSRC_DLEN),
            new ExpectedFile(DATA_RSRC_NAME + "r", DATA_RSRC_RLEN),
            new ExpectedFile(DATA_RSRC_Z_NAME, DATA_RSRC_Z_DLEN),
            //new ExpectedFile(DATA_RSRC_Z_NAME + "r", 0),
            new ExpectedFile(DATA_Z_RSRC_NAME, 0),
            new ExpectedFile(DATA_Z_RSRC_NAME + "r", DATA_Z_RSRC_RLEN),
            new ExpectedFile(RSRC_ONLY_NAME, 0),
            new ExpectedFile(RSRC_ONLY_NAME + "r", RSRC_ONLY_RLEN),
            new ExpectedFile(Path.Combine(SAMPLE_SUBDIR1, DATA_ONLY_NAME),
                DATA_ONLY_DLEN + SUBDIR1_ADD_LEN),
            new ExpectedFile(Path.Combine(SAMPLE_SUBDIR1, DATA_RSRC_NAME),
                DATA_RSRC_DLEN + SUBDIR1_ADD_LEN),
            new ExpectedFile(Path.Combine(SAMPLE_SUBDIR1, DATA_RSRC_NAME + "r"),
                DATA_RSRC_RLEN + SUBDIR1_ADD_LEN),
            new ExpectedFile(Path.Combine(SAMPLE_SUBDIR2, DATA_ONLY_NAME),
                DATA_ONLY_DLEN + SUBDIR2_ADD_LEN),
            new ExpectedFile(Path.Combine(SAMPLE_SUBDIR2, DATA_RSRC_NAME),
                DATA_RSRC_DLEN + SUBDIR2_ADD_LEN),
            new ExpectedFile(Path.Combine(SAMPLE_SUBDIR2, DATA_RSRC_NAME + "r"),
                DATA_RSRC_RLEN + SUBDIR2_ADD_LEN),
        };
        // HFS: files with zero-length resource forks are considered to not have a resource fork
        private static ExpectedFile[] sHFSFiles_NAPS = new ExpectedFile[] {
            new ExpectedFile(HFS_FILE, HFS_FILE_DLEN),
            new ExpectedFile(HFS_FILE + "r", HFS_FILE_RLEN),
            new ExpectedFile(PLAIN_NAME + NAPS_HFS_DEFAULT_TYPE, PLAIN_DLEN),
            new ExpectedFile(DATA_ONLY_NAME, DATA_ONLY_DLEN),
            new ExpectedFile(DATA_RSRC_NAME, DATA_RSRC_DLEN),
            new ExpectedFile(DATA_RSRC_NAME + "r", DATA_RSRC_RLEN),
            new ExpectedFile(DATA_RSRC_Z_NAME, DATA_RSRC_Z_DLEN),
            //new ExpectedFile(DATA_RSRC_Z_NAME + "r", 0),
            new ExpectedFile(DATA_Z_RSRC_NAME, 0),
            new ExpectedFile(DATA_Z_RSRC_NAME + "r", DATA_Z_RSRC_RLEN),
            new ExpectedFile(RSRC_ONLY_NAME, 0),
            new ExpectedFile(RSRC_ONLY_NAME + "r", RSRC_ONLY_RLEN),
            new ExpectedFile(Path.Combine(SAMPLE_SUBDIR1, DATA_ONLY_NAME),
                DATA_ONLY_DLEN + SUBDIR1_ADD_LEN),
            new ExpectedFile(Path.Combine(SAMPLE_SUBDIR1, DATA_RSRC_NAME),
                DATA_RSRC_DLEN + SUBDIR1_ADD_LEN),
            new ExpectedFile(Path.Combine(SAMPLE_SUBDIR1, DATA_RSRC_NAME + "r"),
                DATA_RSRC_RLEN + SUBDIR1_ADD_LEN),
            new ExpectedFile(Path.Combine(SAMPLE_SUBDIR2, DATA_ONLY_NAME),
                DATA_ONLY_DLEN + SUBDIR2_ADD_LEN),
            new ExpectedFile(Path.Combine(SAMPLE_SUBDIR2, DATA_RSRC_NAME),
                DATA_RSRC_DLEN + SUBDIR2_ADD_LEN),
            new ExpectedFile(Path.Combine(SAMPLE_SUBDIR2, DATA_RSRC_NAME + "r"),
                DATA_RSRC_RLEN + SUBDIR2_ADD_LEN),
        };
        // ProDOS: converts '-' to '.'
        private static ExpectedFile[] sProDOSFiles_NAPS = new ExpectedFile[] {
            new ExpectedFile(HFS_FILE, HFS_FILE_DLEN),
            new ExpectedFile(HFS_FILE + "r", HFS_FILE_RLEN),
            new ExpectedFile(PLAIN_NAME + NAPS_NO_TYPE, PLAIN_DLEN),
            new ExpectedFile("data.only#06c0de", DATA_ONLY_DLEN),
            new ExpectedFile("data.rsrc#06c0de", DATA_RSRC_DLEN),
            new ExpectedFile("data.rsrc#06c0der", DATA_RSRC_RLEN),
            new ExpectedFile("data.rsrcZ#06c0de", DATA_RSRC_Z_DLEN),
            new ExpectedFile("data.rsrcZ#06c0der", 0),
            new ExpectedFile("dataZ.rsrc#06c0de", 0),
            new ExpectedFile("dataZ.rsrc#06c0der", DATA_Z_RSRC_RLEN),
            new ExpectedFile("rsrc.only#06c0de", 0),
            new ExpectedFile("rsrc.only#06c0der", RSRC_ONLY_RLEN),
            new ExpectedFile(Path.Combine(SAMPLE_SUBDIR1, "data.only#06c0de"),
                DATA_ONLY_DLEN + SUBDIR1_ADD_LEN),
            new ExpectedFile(Path.Combine(SAMPLE_SUBDIR1, "data.rsrc#06c0de"),
                DATA_RSRC_DLEN + SUBDIR1_ADD_LEN),
            new ExpectedFile(Path.Combine(SAMPLE_SUBDIR1, "data.rsrc#06c0der"),
                DATA_RSRC_RLEN + SUBDIR1_ADD_LEN),
            new ExpectedFile(Path.Combine(SAMPLE_SUBDIR2, "data.only#06c0de"),
                DATA_ONLY_DLEN + SUBDIR2_ADD_LEN),
            new ExpectedFile(Path.Combine(SAMPLE_SUBDIR2, "data.rsrc#06c0de"),
                DATA_RSRC_DLEN + SUBDIR2_ADD_LEN),
            new ExpectedFile(Path.Combine(SAMPLE_SUBDIR2, "data.rsrc#06c0der"),
                DATA_RSRC_RLEN + SUBDIR2_ADD_LEN),
        };

        // ADF creates a ._ file if there is a resource fork *or* nonzero file type attributes,
        // and creates the data file even if it's empty.  The length of the AppleDouble file
        // is variable but should be small.
        private static ExpectedFile[] sHFSFiles_ADF = new ExpectedFile[] {
            new ExpectedFile("._HFS.file", HFS_FILE_RLEN + 1, HFS_FILE_RLEN + AS_OVHD),
            new ExpectedFile("._Plain.File", 0 + 1, 0 + AS_OVHD),   // because of HFS default type
            new ExpectedFile("._data-only", 0 + 1, 0 + AS_OVHD),
            new ExpectedFile("._data-rsrc", DATA_RSRC_RLEN + 1, DATA_RSRC_RLEN + AS_OVHD),
            new ExpectedFile("._data-rsrcZ", 0 + 1, 0 + AS_OVHD),
            new ExpectedFile("._dataZ-rsrc", DATA_Z_RSRC_RLEN + 1, DATA_Z_RSRC_RLEN + AS_OVHD),
            new ExpectedFile("._rsrc-only", RSRC_ONLY_RLEN + 1, RSRC_ONLY_RLEN + AS_OVHD),
            new ExpectedFile("HFS.file", HFS_FILE_DLEN),
            new ExpectedFile("Plain.File", PLAIN_DLEN),
            new ExpectedFile("data-only", DATA_ONLY_DLEN),
            new ExpectedFile("data-rsrc", DATA_RSRC_DLEN),
            new ExpectedFile("data-rsrcZ", DATA_RSRC_Z_DLEN),
            new ExpectedFile("dataZ-rsrc", 0),
            new ExpectedFile("rsrc-only", 0),
            new ExpectedFile(Path.Combine(SAMPLE_SUBDIR1, "._data-only"),
                0 + 1, 0 + AS_OVHD),
            new ExpectedFile(Path.Combine(SAMPLE_SUBDIR1, "._data-rsrc"),
                DATA_RSRC_RLEN + SUBDIR1_ADD_LEN + 1, DATA_RSRC_RLEN + SUBDIR1_ADD_LEN + AS_OVHD),
            new ExpectedFile(Path.Combine(SAMPLE_SUBDIR1, "data-only"),
                DATA_ONLY_DLEN + SUBDIR1_ADD_LEN),
            new ExpectedFile(Path.Combine(SAMPLE_SUBDIR1, "data-rsrc"),
                DATA_RSRC_DLEN + SUBDIR1_ADD_LEN),
            new ExpectedFile(Path.Combine(SAMPLE_SUBDIR2, "._data-only"),
                0 + 1, 0 + AS_OVHD),
            new ExpectedFile(Path.Combine(SAMPLE_SUBDIR2, "._data-rsrc"),
                DATA_RSRC_RLEN + SUBDIR2_ADD_LEN + 1, DATA_RSRC_RLEN + SUBDIR2_ADD_LEN + AS_OVHD),
            new ExpectedFile(Path.Combine(SAMPLE_SUBDIR2, "data-only"),
                DATA_ONLY_DLEN + SUBDIR2_ADD_LEN),
            new ExpectedFile(Path.Combine(SAMPLE_SUBDIR2, "data-rsrc"),
                DATA_RSRC_DLEN + SUBDIR2_ADD_LEN),
        };

        // AS extracts both forks to a single file.
        private static ExpectedFile[] sHFSFiles_AS = new ExpectedFile[] {
            new ExpectedFile("HFS.file.as",
                HFS_FILE_DLEN + HFS_FILE_RLEN + 1, HFS_FILE_DLEN + HFS_FILE_RLEN + AS_OVHD),
            new ExpectedFile("Plain.File.as",
                PLAIN_DLEN + 1, PLAIN_DLEN + AS_OVHD),
            new ExpectedFile("data-only.as",
                DATA_ONLY_DLEN + 1, DATA_ONLY_DLEN + AS_OVHD),
            new ExpectedFile("data-rsrc.as",
                DATA_RSRC_DLEN + DATA_RSRC_RLEN + 1, DATA_RSRC_DLEN + DATA_RSRC_RLEN + AS_OVHD),
            new ExpectedFile("data-rsrcZ.as",
                DATA_RSRC_Z_DLEN + 1, DATA_RSRC_Z_DLEN + AS_OVHD),
            new ExpectedFile("dataZ-rsrc.as",
                DATA_Z_RSRC_RLEN + 1, DATA_Z_RSRC_RLEN + AS_OVHD),
            new ExpectedFile("rsrc-only.as",
                RSRC_ONLY_RLEN + 1, RSRC_ONLY_RLEN + AS_OVHD),
            new ExpectedFile(Path.Combine(SAMPLE_SUBDIR1, "data-only.as"),
                DATA_ONLY_DLEN + SUBDIR1_ADD_LEN + 1, DATA_ONLY_DLEN + SUBDIR1_ADD_LEN + AS_OVHD),
            new ExpectedFile(Path.Combine(SAMPLE_SUBDIR1, "data-rsrc.as"),
                DATA_RSRC_DLEN + DATA_RSRC_RLEN + SUBDIR1_ADD_LEN * 2 + 1,
                DATA_RSRC_DLEN + DATA_RSRC_RLEN + SUBDIR1_ADD_LEN * 2 + AS_OVHD),
            new ExpectedFile(Path.Combine(SAMPLE_SUBDIR2, "data-only.as"),
                DATA_ONLY_DLEN + SUBDIR2_ADD_LEN + 1, DATA_ONLY_DLEN + SUBDIR2_ADD_LEN + AS_OVHD),
            new ExpectedFile(Path.Combine(SAMPLE_SUBDIR2, "data-rsrc.as"),
                DATA_RSRC_DLEN + DATA_RSRC_RLEN + SUBDIR2_ADD_LEN * 2 + 1,
                DATA_RSRC_DLEN + DATA_RSRC_RLEN + SUBDIR2_ADD_LEN * 2 + AS_OVHD),
        };

        // Host preservation attaches the resource fork to the file.
        private static ExpectedFile[] sHFSFiles_Host = new ExpectedFile[] {
            new ExpectedFile("HFS.file", HFS_FILE_DLEN),
            new ExpectedFile("HFS.file" + RSRC_SFX, HFS_FILE_RLEN),
            new ExpectedFile("Plain.File", PLAIN_DLEN),
            new ExpectedFile("data-only", DATA_ONLY_DLEN),
            new ExpectedFile("data-rsrc", DATA_RSRC_DLEN),
            new ExpectedFile("data-rsrc" + RSRC_SFX, DATA_RSRC_RLEN),
            new ExpectedFile("data-rsrcZ", DATA_RSRC_Z_DLEN),
            new ExpectedFile("dataZ-rsrc", 0),
            new ExpectedFile("dataZ-rsrc" + RSRC_SFX, DATA_Z_RSRC_RLEN),
            new ExpectedFile("rsrc-only", 0),
            new ExpectedFile("rsrc-only" + RSRC_SFX, RSRC_ONLY_RLEN),
            new ExpectedFile(Path.Combine(SAMPLE_SUBDIR1, "data-only"),
                DATA_ONLY_DLEN + SUBDIR1_ADD_LEN),
            new ExpectedFile(Path.Combine(SAMPLE_SUBDIR1, "data-rsrc"),
                DATA_RSRC_DLEN + SUBDIR1_ADD_LEN),
            new ExpectedFile(Path.Combine(SAMPLE_SUBDIR1, "data-rsrc" + RSRC_SFX),
                DATA_RSRC_RLEN + SUBDIR1_ADD_LEN),
            new ExpectedFile(Path.Combine(SAMPLE_SUBDIR2, "data-only"),
                DATA_ONLY_DLEN + SUBDIR2_ADD_LEN),
            new ExpectedFile(Path.Combine(SAMPLE_SUBDIR2, "data-rsrc"),
                DATA_RSRC_DLEN + SUBDIR2_ADD_LEN),
            new ExpectedFile(Path.Combine(SAMPLE_SUBDIR2, "data-rsrc" + RSRC_SFX),
                DATA_RSRC_RLEN + SUBDIR2_ADD_LEN),
        };

        // Ignores resource forks.
        private static ExpectedFile[] sHFSFiles_None = new ExpectedFile[] {
            new ExpectedFile("HFS.file", HFS_FILE_DLEN),
            new ExpectedFile("Plain.File", PLAIN_DLEN),
            new ExpectedFile("data-only", DATA_ONLY_DLEN),
            new ExpectedFile("data-rsrc", DATA_RSRC_DLEN),
            new ExpectedFile("data-rsrcZ", DATA_RSRC_Z_DLEN),
            new ExpectedFile("dataZ-rsrc", 0),
            new ExpectedFile("rsrc-only", 0),
            new ExpectedFile(Path.Combine(SAMPLE_SUBDIR1, "data-only"),
                DATA_ONLY_DLEN + SUBDIR1_ADD_LEN),
            new ExpectedFile(Path.Combine(SAMPLE_SUBDIR1, "data-rsrc"),
                DATA_RSRC_DLEN + SUBDIR1_ADD_LEN),
            new ExpectedFile(Path.Combine(SAMPLE_SUBDIR2, "data-only"),
                DATA_ONLY_DLEN + SUBDIR2_ADD_LEN),
            new ExpectedFile(Path.Combine(SAMPLE_SUBDIR2, "data-rsrc"),
                DATA_RSRC_DLEN + SUBDIR2_ADD_LEN),
        };

        // Extract MacZip-encoded ZIP file with MacZip disabled.
        private static ExpectedFile[] sZipFiles_NAPS_NoMZ = new ExpectedFile[] {
            new ExpectedFile("HFS.file" + NAPS_NO_TYPE, HFS_FILE_DLEN),
            new ExpectedFile("Plain.File" + NAPS_NO_TYPE, PLAIN_DLEN),
            new ExpectedFile(Path.Combine(Zip.MAC_ZIP_RSRC_PREFIX, "._HFS.file" + NAPS_NO_TYPE),
                HFS_FILE_RLEN + 1, HFS_FILE_RLEN + AS_OVHD),
            new ExpectedFile(Path.Combine(Zip.MAC_ZIP_RSRC_PREFIX, "._data-only" + NAPS_NO_TYPE),
                0 + 1, 0 + AS_OVHD),
            new ExpectedFile(Path.Combine(Zip.MAC_ZIP_RSRC_PREFIX, "._data-rsrc" + NAPS_NO_TYPE),
                DATA_RSRC_RLEN + 1, DATA_RSRC_RLEN + AS_OVHD),
            new ExpectedFile(Path.Combine(Zip.MAC_ZIP_RSRC_PREFIX, "._data-rsrcZ" + NAPS_NO_TYPE),
                0 + 1, 0 + AS_OVHD),
            new ExpectedFile(Path.Combine(Zip.MAC_ZIP_RSRC_PREFIX, "._dataZ-rsrc" + NAPS_NO_TYPE),
                DATA_Z_RSRC_RLEN + 1, DATA_Z_RSRC_RLEN + AS_OVHD),
            new ExpectedFile(Path.Combine(Zip.MAC_ZIP_RSRC_PREFIX, "._rsrc-only" + NAPS_NO_TYPE),
                RSRC_ONLY_RLEN + 1, RSRC_ONLY_RLEN + AS_OVHD),
            new ExpectedFile(Path.Join(Zip.MAC_ZIP_RSRC_PREFIX, SAMPLE_SUBDIR1, "._data-only" + NAPS_NO_TYPE),
                0 + 1, 0 + AS_OVHD),
            new ExpectedFile(Path.Join(Zip.MAC_ZIP_RSRC_PREFIX, SAMPLE_SUBDIR1, "._data-rsrc" + NAPS_NO_TYPE),
                DATA_RSRC_RLEN + SUBDIR1_ADD_LEN + 1, DATA_RSRC_RLEN + SUBDIR1_ADD_LEN + AS_OVHD),
            new ExpectedFile(Path.Join(Zip.MAC_ZIP_RSRC_PREFIX, SAMPLE_SUBDIR2, "._data-only" + NAPS_NO_TYPE),
                0 + 1, 0 + AS_OVHD),
            new ExpectedFile(Path.Join(Zip.MAC_ZIP_RSRC_PREFIX, SAMPLE_SUBDIR2, "._data-rsrc" + NAPS_NO_TYPE),
                DATA_RSRC_RLEN + SUBDIR2_ADD_LEN + 1, DATA_RSRC_RLEN + SUBDIR2_ADD_LEN + AS_OVHD),
            new ExpectedFile("data-only" + NAPS_NO_TYPE, DATA_ONLY_DLEN),
            new ExpectedFile("data-rsrc" + NAPS_NO_TYPE, DATA_RSRC_DLEN),
            new ExpectedFile("data-rsrcZ" + NAPS_NO_TYPE, DATA_RSRC_Z_DLEN),
            new ExpectedFile("dataZ-rsrc" + NAPS_NO_TYPE, 0),
            new ExpectedFile("rsrc-only" + NAPS_NO_TYPE, 0),
            new ExpectedFile(Path.Combine(SAMPLE_SUBDIR1, "data-only" + NAPS_NO_TYPE),
                DATA_ONLY_DLEN + SUBDIR1_ADD_LEN),
            new ExpectedFile(Path.Combine(SAMPLE_SUBDIR1, "data-rsrc" + NAPS_NO_TYPE),
                DATA_RSRC_DLEN + SUBDIR1_ADD_LEN),
            new ExpectedFile(Path.Combine(SAMPLE_SUBDIR2, "data-only" + NAPS_NO_TYPE),
                DATA_ONLY_DLEN + SUBDIR2_ADD_LEN),
            new ExpectedFile(Path.Combine(SAMPLE_SUBDIR2, "data-rsrc" + NAPS_NO_TYPE),
                DATA_RSRC_DLEN + SUBDIR2_ADD_LEN),
        };

        // Extract ZIP, preserve=None, path stripping enabled, overwrite enabled
        private static ExpectedFile[] sZipFiles_None_StripO = new ExpectedFile[] {
            new ExpectedFile("HFS.file", HFS_FILE_DLEN),
            new ExpectedFile("Plain.File", PLAIN_DLEN),
            new ExpectedFile("data-only", DATA_ONLY_DLEN + SUBDIR2_ADD_LEN),
            new ExpectedFile("data-rsrc", DATA_RSRC_DLEN + SUBDIR2_ADD_LEN),
            new ExpectedFile("data-rsrcZ", DATA_RSRC_Z_DLEN),
            new ExpectedFile("dataZ-rsrc", 0),
            new ExpectedFile("rsrc-only", 0),
        };

        // Extract HFS, preserve=None, path stripping enabled, overwrite disabled
        private static ExpectedFile[] sHFSFiles_None_StripNO = new ExpectedFile[] {
            new ExpectedFile("HFS.file", HFS_FILE_DLEN),
            new ExpectedFile("Plain.File", PLAIN_DLEN),
            new ExpectedFile("data-only", DATA_ONLY_DLEN),
            new ExpectedFile("data-rsrc", DATA_RSRC_DLEN),
            new ExpectedFile("data-rsrcZ", DATA_RSRC_Z_DLEN),
            new ExpectedFile("dataZ-rsrc", 0),
            new ExpectedFile("rsrc-only", 0),
        };

        // Extract "subdir1:*" from ZIP, no preservation, no recursion.
        // Extract "subdir2:*" from HFS, no preservation, no recursion.
        private static ExpectedFile[] sMixedFiles_None_WNR = new ExpectedFile[] {
            new ExpectedFile(Path.Combine(SAMPLE_SUBDIR1, "data-only"),
                DATA_ONLY_DLEN + SUBDIR1_ADD_LEN),
            new ExpectedFile(Path.Combine(SAMPLE_SUBDIR1, "data-rsrc"),
                DATA_RSRC_DLEN + SUBDIR1_ADD_LEN),
            new ExpectedFile(Path.Combine(SAMPLE_SUBDIR2, "data-only"),
                DATA_ONLY_DLEN + SUBDIR2_ADD_LEN),
            new ExpectedFile(Path.Combine(SAMPLE_SUBDIR2, "data-rsrc"),
                DATA_RSRC_DLEN + SUBDIR2_ADD_LEN),
        };

        /// <summary>
        /// Tests extraction of the sample files.  This is a general exercise of the feature,
        /// testing some of the difference in behavior with different archives.
        /// </summary>
        /// <param name="testFileName">Archive filename.</param>
        /// <param name="expFiles">Expected output from extraction.</param>
        /// <param name="parms">Parameters.</param>
        private static void TestExtractSamples(string testFileName, ExpectedFile[] expFiles,
                ParamsBag parms) {
            CreateOutputDir();
            string oldCurrentDir = Environment.CurrentDirectory;
            try {
                Environment.CurrentDirectory = OUTPUT_DIR;
                testFileName = Path.Combine("..", testFileName);

                // Extract all files.  We're primarily interested in seeing how data and
                // resource forks are managed, so extract in NAPS mode.
                if (!Extract.HandleExtract("x", new string[] { testFileName }, parms)) {
                    throw new Exception("Error: extract '" + testFileName + "' failed");
                }

                // Generate a list of all files.  We can't depend on the host filesystem storing
                // files in a specific order, so we need to sort them.  The case-sensitive
                // "Ordinal" sort order is the best match for what "ls" does.
                List<string> allFiles = new List<string>();
                GatherFileEntries(".", allFiles, parms);
                allFiles.Sort(StringComparer.Ordinal);

                // Compare to expected file list.
                VerifyList(allFiles, expFiles);
            } finally {
                Environment.CurrentDirectory = oldCurrentDir;
            }
            RemoveOutputDir();
        }

        /// <summary>
        /// Recursively gathers a list of files from the host filesystem.
        /// </summary>
        private static void GatherFileEntries(string curDirName, List<string> entries,
                ParamsBag parms) {
            string[] fileList = Directory.GetFiles(curDirName);
            foreach (string fileName in fileList) {
                // Remove the leading "./".
                int sepIndex = fileName.IndexOf(Path.DirectorySeparatorChar);
                Debug.Assert(sepIndex > 0);
                string fileName1 = fileName.Substring(sepIndex + 1);
                entries.Add(fileName1);

                if (parms.Preserve == ExtractFileWorker.PreserveMode.Host) {
                    string rsrcForkName = fileName1 + RSRC_SFX;
                    if (File.Exists(rsrcForkName)) {
                        entries.Add(rsrcForkName);
                    }
                }
            }

            string[] dirList = Directory.GetDirectories(curDirName);
            foreach (string dirName in dirList) {
                GatherFileEntries(dirName, entries, parms);
            }
        }

        /// <summary>
        /// Verifies the files found in the host filesystem against the expected files.
        /// </summary>
        private static void VerifyList(List<string> fileNames, ExpectedFile[] expFiles) {
            if (fileNames.Count != expFiles.Length) {
                throw new Exception("File count mismatch: found=" + fileNames.Count +
                    ", expected=" + expFiles.Length);
            }
            for (int i = 0; i < expFiles.Length; i++) {
                if (fileNames[i] != expFiles[i].FileName) {
                    throw new Exception("Filename mismatch: found '" + fileNames[i] +
                        "', expected '" + expFiles[i].FileName);
                }

                FileInfo info = new FileInfo(fileNames[i]);
                if (info.Length < expFiles[i].MinLength || info.Length > expFiles[i].MaxLength) {
                    throw new Exception("Length mismatch on '" + fileNames[i] + "': found " +
                        info.Length + ", expected [" + expFiles[i].MinLength + "," +
                        expFiles[i].MaxLength + "]");
                }
                if (info.LastWriteTime != MOD_WHEN) {
                    throw new Exception("Mod date not set on '" + fileNames[i] + "'");
                }
            }
        }

        #endregion Basic extract test

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
