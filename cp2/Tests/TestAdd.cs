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
using System.Text;

using AppCommon;
using CommonUtil;
using DiskArc;
using DiskArc.Arc;
using DiskArc.Disk;
using DiskArc.FS;
using static DiskArc.Defs;

namespace cp2.Tests {
    /// <summary>
    /// Tests the "add" command.
    /// </summary>
    internal static class TestAdd {
        private const string SAMPLE_NAME = "Samples";
        private static readonly string SAMPLE_DIR = Path.Combine(Controller.TEST_TMP, SAMPLE_NAME);

        private static readonly string PLAIN_DATA = Path.Combine(SAMPLE_DIR, "PlainFile");
        private static readonly string AS_DATA = Path.Combine(SAMPLE_DIR, "AS_Data.as");
        private static readonly string AS_RSRC = Path.Combine(SAMPLE_DIR, "AS_Rsrc.as");
        private static readonly string AS_BOTH = Path.Combine(SAMPLE_DIR, "AS_Both.as");
        private static readonly string ADF_DATA = Path.Combine(SAMPLE_DIR, "ADF_Data");
        private static readonly string ADF_BOTH = Path.Combine(SAMPLE_DIR, "ADF_Both");
        private static readonly string NAPS_DATA = Path.Combine(SAMPLE_DIR, "NAPS_Data");
        private static readonly string NAPS_DATA_X = NAPS_DATA + "#0612ab";
        private static readonly string NAPS_RSRC = Path.Combine(SAMPLE_DIR, "NAPS_Rsrc");
        private static readonly string NAPS_RSRC_X = NAPS_RSRC + "#0612abR";
        private static readonly string NAPS_BOTH = Path.Combine(SAMPLE_DIR, "NAPS_Both");
        private static readonly string NAPS_BOTH_XD = NAPS_BOTH + "#0612ab";
        private static readonly string NAPS_BOTH_XR = NAPS_BOTH + "#0612ABr";
        private static readonly string HOST_DATA = Path.Combine(SAMPLE_DIR, "Host_Data");
        private static readonly string HOST_BOTH = Path.Combine(SAMPLE_DIR, "Host_Both");

        private const string AS_DATA_NAME = "Colon:name!";
        private const string AS_RSRC_NAME = "SomethingVeryLongToTestTruncationWhenFileIsAdded"; //48
        private const string AS_BOTH_NAME = "Part\u2022Unicode\ufffd?";
        private static readonly string AS_DATA_IN = Path.Combine(SAMPLE_DIR, AS_DATA_NAME);
        private static readonly string AS_RSRC_IN = Path.Combine(SAMPLE_DIR, AS_RSRC_NAME);
        private static readonly string AS_BOTH_IN = Path.Combine(SAMPLE_DIR, AS_BOTH_NAME);

        private const int DATA_LEN = 1024;
        private const byte DATA_VALUE = 0x01;
        private const int RSRC_LEN = 2048;
        private const byte RSRC_VALUE = 0x02;

        private const byte PRODOS_FILETYPE = 0x06;
        private const ushort PRODOS_AUXTYPE = 0x12ab;
        private const uint HFS_FILETYPE = 0x54455354;       // 'TEST'
        private const uint HFS_CREATOR = 0x23435032;        // '#CP2'
        private const uint HFS_PDOS_FILETYPE = 0x700612ab;
        private const uint HFS_PDOS_CREATOR = FileAttribs.CREATOR_PDOS;
        private static readonly string HFS_NAPS =
            '#' + HFS_FILETYPE.ToString("x8") + HFS_CREATOR.ToString("x8");

        // Leave seconds at zero so it can be stored by ProDOS.
        private static readonly DateTime CREATE_WHEN = new DateTime(1977, 6, 1, 1, 2, 0);
        private static readonly DateTime MOD_WHEN = new DateTime(1986, 9, 15, 4, 5, 0);
        private static readonly DateTime RECENT_WHEN = DateTime.Now.AddSeconds(-60);


        public static void RunTest(ParamsBag parms) {
            Controller.Stdout.WriteLine("  Add...");
            Controller.PrepareTestTmp(parms);

            //
            // We need to add files with specific properties:
            // - plain file
            // - AppleSingle (data only)
            // - AppleSingle (resource only)
            // - AppleSingle (data + resource)
            // - AppleDouble (data only, with file types)
            // - AppleDouble (data + resource)
            // - NAPS (data only)
            // - NAPS (rsrc only)
            // - NAPS (data + resource)
            // - [if available] host (data only, with file types)
            // - [if available] host (data + resource)
            //
            // The full set should be added to each type of archive and each filesystem, to
            // exercise filename adjustments, file type handling, access permissions, resource
            // fork handling, etc.  AppleSingle can be useful for testing handling of invalid
            // filename characters, as the format has no restrictions and v2 is interpreted
            // as UTF-8.
            //
            // Using --no-from-x should result in the files themselves being added without being
            // interpreted as ADF/AS/NAPS etc.  We only need to test this with one type of archive.
            //
            // We also need to exercise relevant options:
            //  --compress
            //  --mac-zip
            //  --overwrite
            //  --raw (with a DOS disk image)
            //  --recurse
            //  --strip-paths
            //
            // We want to exercise failure cases (should return cleanly, not throw uncaught):
            // - disk full during add
            // - disk full while adding files to an archive on the disk
            //

            // Configure parameters.
            parms.Compress = false;     // Binary II + Squeeze makes data length unknowable
            parms.FromADF = true;
            parms.FromAS = true;
            parms.FromHost = true;
            parms.FromNAPS = true;
            parms.MacZip = true;
            parms.Overwrite = true;
            parms.Raw = false;
            parms.Recurse = true;
            parms.Sectors = 16;
            parms.StripPaths = false;

            CreateSamples(parms);

            string fileName;

            // Test Binary2
            fileName = Path.Combine(Controller.TEST_TMP, "addtest.bny");
            if (!ArcUtil.HandleCreateFileArchive("cfa", new string[] { fileName }, parms)) {
                throw new Exception("cfa " + fileName + " failed");
            }
            CallAdd(fileName, SAMPLE_DIR, parms);
            VerifyContents(fileName, sAddTestBinary2, parms);

            // Test NuFX
            fileName = Path.Combine(Controller.TEST_TMP, "addtest.shk");
            if (!ArcUtil.HandleCreateFileArchive("cfa", new string[] { fileName }, parms)) {
                throw new Exception("cfa " + fileName + " failed");
            }
            CallAdd(fileName, SAMPLE_DIR, parms);
            VerifyContents(fileName, sAddTestNuFX, parms);

            // Test ZIP
            fileName = Path.Combine(Controller.TEST_TMP, "addtest-mz.zip");
            if (!ArcUtil.HandleCreateFileArchive("cfa", new string[] { fileName }, parms)) {
                throw new Exception("cfa " + fileName + " failed");
            }
            CallAdd(fileName, SAMPLE_DIR, parms);
            VerifyContents(fileName, sAddTestMacZip, parms);

            // Test ZIP, without MacZip
            parms.MacZip = false;
            fileName = Path.Combine(Controller.TEST_TMP, "addtest.zip");
            if (!ArcUtil.HandleCreateFileArchive("cfa", new string[] { fileName }, parms)) {
                throw new Exception("cfa " + fileName + " failed");
            }
            CallAdd(fileName, SAMPLE_DIR, parms);
            VerifyContents(fileName, sAddTestZip, parms);
            parms.MacZip = true;

            // Test DOS 3.3
            fileName = Path.Combine(Controller.TEST_TMP, "addtest-dos33.po");
            if (!DiskUtil.HandleCreateDiskImage("cdi", new string[] { fileName, "140k", "DOS" },
                    parms)) {
                throw new Exception("cdi " + fileName + " failed");
            }
            CallAdd(fileName, SAMPLE_DIR, parms);
            VerifyContents(fileName, sAddTestDOS, parms);

            // Test HFS
            fileName = Path.Combine(Controller.TEST_TMP, "addtest-hfs.po");
            if (!DiskUtil.HandleCreateDiskImage("cdi", new string[] { fileName, "800k", "HFS" },
                    parms)) {
                throw new Exception("cdi " + fileName + " failed");
            }
            CallAdd(fileName, SAMPLE_DIR, parms);
            VerifyContents(fileName, sAddTestHFS, parms);

            // Test ProDOS
            fileName = Path.Combine(Controller.TEST_TMP, "addtest-prodos.po");
            if (!DiskUtil.HandleCreateDiskImage("cdi", new string[] { fileName, "800k", "ProDOS" },
                    parms)) {
                throw new Exception("cdi " + fileName + " failed");
            }
            CallAdd(fileName, SAMPLE_DIR, parms);
            VerifyContents(fileName, sAddTestProDOS, parms);

            // Test ZIP, with all "from" options disabled.  MacZip shouldn't matter (make sure
            // it's on so we can confirm that).  Turn on path-stripping while we're at it.
            parms.FromADF = parms.FromAS = parms.FromHost = parms.FromNAPS = false;
            parms.StripPaths = true;
            Debug.Assert(parms.MacZip);

            fileName = Path.Combine(Controller.TEST_TMP, "addtest-nofrom.zip");
            if (!ArcUtil.HandleCreateFileArchive("cfa", new string[] { fileName }, parms)) {
                throw new Exception("cfa " + fileName + " failed");
            }
            CallAdd(fileName, SAMPLE_DIR, parms);
            if (ParamsBag.HasHostPreservation) {
                VerifyContents(fileName, sAddTestZipNoFromMac, parms);
            } else {
                VerifyContents(fileName, sAddTestZipNoFrom, parms);
            }
            parms.FromADF = parms.FromAS = parms.FromHost = parms.FromNAPS = true;
            parms.StripPaths = false;


            TestCompressOverwrite(parms);
            TestRawMode(parms);
            TestDiskFull(parms);
            TestAddToDir(parms);
            TestRecurse(parms);
            TestNAPSUnescape(parms);
            TestTurducken(parms);

            //throw new Exception("CHECK IT");
            Controller.RemoveTestTmp(parms);
        }

        /// <summary>
        /// Confirms basic function of [no-]overwrite and [no-]compress flags.
        /// </summary>
        private static void TestCompressOverwrite(ParamsBag parms) {
            string fileName = Path.Combine(Controller.TEST_TMP, "addtest-cov.shk");
            if (!ArcUtil.HandleCreateFileArchive("cfa", new string[] { fileName }, parms)) {
                throw new Exception("cfa " + fileName + " failed");
            }

            parms.Compress = false;
            parms.Overwrite = true;
            CallAdd(fileName, PLAIN_DATA, parms);
            long storageSize = CheckArcFirstLen(fileName, DATA_LEN, parms);
            if (storageSize != DATA_LEN) {
                throw new Exception("Wrong initial storage length: " + storageSize);
            }

            // Do again with compression.  Overwrite is enabled, so it should get smaller.
            parms.Compress = true;
            CallAdd(fileName, PLAIN_DATA, parms);
            long compStorageSize = CheckArcFirstLen(fileName, DATA_LEN, parms);
            if (compStorageSize >= DATA_LEN) {
                throw new Exception("Compression failed to reduce: " + storageSize);
            }

            // Do again without compression, and overwrite disabled.  So it should stay small.
            parms.Compress = false;
            parms.Overwrite = false;
            CallAdd(fileName, PLAIN_DATA, parms);
            storageSize = CheckArcFirstLen(fileName, DATA_LEN, parms);
            if (storageSize != compStorageSize) {
                throw new Exception("File was incorrectly overwritten");
            }
        }

        /// <summary>
        /// Checks the data length of the first entry in the archive.
        /// </summary>
        /// <returns>Entry storage size.</returns>
        private static long CheckArcFirstLen(string fileName, int expDataLen, ParamsBag parms) {
            using (Stream stream = new FileStream(fileName, FileMode.Open)) {
                using (IArchive archive = NuFX.OpenArchive(stream, parms.AppHook)) {
                    IFileEntry entry = archive.GetFirstEntry();
                    if (entry.DataLength != expDataLen) {
                        throw new Exception("Unexpected data len");
                    }
                    return entry.StorageSize;
                }
            }
        }

        /// <summary>
        /// Tests adding files in DOS "raw" mode.
        /// </summary>
        private static void TestRawMode(ParamsBag parms) {
            Debug.Assert(parms.FromNAPS);
            Debug.Assert(DATA_LEN % SECTOR_SIZE == 0);

            string fileName = Path.Combine(Controller.TEST_TMP, "addtest-rawdos.do");
            if (!DiskUtil.HandleCreateDiskImage("cdi", new string[] { fileName, "140k", "DOS" },
                    parms)) {
                throw new Exception("cdi " + fileName + " failed");
            }

            // Mark the file read-only.
            FileAttributes attr = File.GetAttributes(NAPS_DATA_X);
            Debug.Assert((attr & FileAttributes.ReadOnly) == 0);
            File.SetAttributes(NAPS_DATA_X, attr | FileAttributes.ReadOnly);

            // Add it.
            CallAdd(fileName, NAPS_DATA_X, parms);
            // Should need two extra sectors to store the file: one for the T/S list, one
            // because it's a 'B' file and the header adds 4 bytes.  Note this deletes the file
            // (don't want to have to trust the delete feature).
            long storageSize = CheckDiskFileStuff(fileName, "NAPS_DATA", DATA_LEN,
                FileAttribs.FILE_ACCESS_LOCKED /*| (byte)AccessFlags.BackupNeeded*/, true, parms);
            if (storageSize != DATA_LEN + 256 * 2) {
                throw new Exception("Unexpected cooked storage size: " + storageSize);
            }

            // Change it back to read/write.
            File.SetAttributes(NAPS_DATA_X, attr);

            // In raw mode we write the length word, which for our data file will be 0x0101 (257).
            parms.Raw = true;
            CallAdd(fileName, NAPS_DATA_X, parms);
            storageSize = CheckDiskFileStuff(fileName, "NAPS_DATA", 0x0101,
                FileAttribs.FILE_ACCESS_UNLOCKED /*| (byte)AccessFlags.BackupNeeded*/, true, parms);
            if (storageSize != DATA_LEN + 256 * 1) {
                throw new Exception("Unexpected cooked storage size: " + storageSize);
            }

            parms.Raw = false;
        }

        private static long CheckDiskFileStuff(string diskFileName, string entryName,
                int expDataLen, byte expAccess, bool doDelete, ParamsBag parms) {
            using (Stream stream = new FileStream(diskFileName, FileMode.Open)) {
                using (IDiskImage disk = UnadornedSector.OpenDisk(stream, parms.AppHook)) {
                    disk.AnalyzeDisk();
                    IFileSystem fs = (IFileSystem)disk.Contents!;
                    fs.PrepareFileAccess(true);
                    IFileEntry volDir = fs.GetVolDirEntry();
                    IFileEntry entry = fs.FindFileEntry(volDir, entryName);
                    if (entry.DataLength != expDataLen) {
                        throw new Exception("Unexpected data len: " + entry.DataLength);
                    }
                    if (entry.Access != expAccess) {
                        throw new Exception("Unexpected access: $" + entry.Access.ToString("x2"));
                    }

                    if (doDelete) {
                        fs.DeleteFile(entry);
                    }
                    return entry.StorageSize;
                }
            }
        }

        /// <summary>
        /// Tests a couple of disk-full scenarios.
        /// </summary>
        private static void TestDiskFull(ParamsBag parms) {
            parms.StripPaths = true;

            string fileName = Path.Combine(Controller.TEST_TMP, "addtest-full.po");
            if (!DiskUtil.HandleCreateDiskImage("cdi", new string[] { fileName, "140k", "ProDOS" },
                    parms)) {
                throw new Exception("cdi " + fileName + " failed");
            }

            const int FILE_SIZE = 100000;
            string testName1 = Path.Combine(SAMPLE_DIR, "BigTest1");
            using (Stream stream = new FileStream(testName1, FileMode.CreateNew)) {
                for (int i = 0; i < FILE_SIZE + 1; i++) {
                    stream.WriteByte(0x02);
                }
            }
            string testName2 = Path.Combine(SAMPLE_DIR, "BigTest2");
            using (Stream stream = new FileStream(testName2, FileMode.CreateNew)) {
                for (int i = 0; i < FILE_SIZE + 2; i++) {
                    stream.WriteByte(0x02);
                }
            }

            // Try to add both files.  The second should fail, leaving the first intact.
            if (Add.HandleAdd("add", new string[] { fileName, testName1, testName2 }, parms)) {
                throw new Exception("Error: add '" + fileName + "' '" + testName1 + "'/'" +
                    testName2 + "' succeeded");
            }

            // Confirm that the first file is present, and the second file isn't.
            MemoryStream stdout = Controller.CaptureConsoleOut();
            if (!Catalog.HandleList("list", new string[] { fileName }, parms)) {
                throw new Exception("Error: list '" + fileName + "' failed");
            }
            int count;
            if ((count = Controller.CountLines(stdout)) != 1) {
                throw new Exception("Too many files present: " + count);
            }

            // Create an empty file archive.
            string arcFileName = "AddArchive.SHK";
            string arcPathName = Path.Combine(Controller.TEST_TMP, arcFileName);
            if (!ArcUtil.HandleCreateFileArchive("cfa", new string[] { arcPathName }, parms)) {
                throw new Exception("Error: cfa '" + arcPathName + "' failed");
            }

            // Add it to the disk.  This should succeed.  Note we're still stripping paths.
            if (!Add.HandleAdd("add", new string[] { fileName, arcPathName }, parms)) {
                throw new Exception("Error: add '" + fileName + "' '" + arcPathName + "' failed");
            }

            string extArcName = fileName + ExtArchive.SPLIT_CHAR + arcFileName;

            // Try to add the first file to the archive, with compression.  Should work.
            parms.Compress = true;
            if (!Add.HandleAdd("add", new string[] { extArcName, testName1 }, parms)) {
                throw new Exception("Error: add '" + extArcName + "' '" + testName1 + "' failed");
            }

            // Try to add the second file to the archive, without compression.  Should fail.
            parms.Compress = false;
            if (Add.HandleAdd("add", new string[] { extArcName, testName2 }, parms)) {
                throw new Exception("Error: add '" + extArcName + "' '" + testName2 +
                    "' succeeded");
            }

            // Confirm that the first file is present, and the second file isn't.
            stdout = Controller.CaptureConsoleOut();
            if (!Catalog.HandleList("list", new string[] { extArcName }, parms)) {
                throw new Exception("Error: list '" + extArcName + "' failed");
            }
            if ((count = Controller.CountLines(stdout)) != 1) {
                throw new Exception("Too many files present: " + count);
            }

            // Try to add the second file to the archive, with compression.  Should work.
            parms.Compress = true;
            if (!Add.HandleAdd("add", new string[] { extArcName, testName2 }, parms)) {
                throw new Exception("Error: add '" + extArcName + "' '" + testName2 + "' failed");
            }

            parms.StripPaths = false;
        }

        /// <summary>
        /// Tests specification of a directory in the ext-archive string.
        /// </summary>
        private static void TestAddToDir(ParamsBag parms) {
            Debug.Assert(!parms.StripPaths);

            const string testName1 = "WithDir1";
            string testFileName1 = Path.Combine(SAMPLE_DIR, testName1);
            using (Stream stream = new FileStream(testFileName1, FileMode.CreateNew)) {
                for (int i = 0; i < 11; i++) {
                    stream.WriteByte(0x03);
                }
            }
            const string testName2 = "WithDir2";
            string testFileName2 = Path.Combine(SAMPLE_DIR, testName2);
            using (Stream stream = new FileStream(testFileName2, FileMode.CreateNew)) {
                for (int i = 0; i < 12; i++) {
                    stream.WriteByte(0x03);
                }
            }

            const string fileName = "addtest-dir.po";
            string diskFileName = Path.Combine(Controller.TEST_TMP, fileName);
            if (!DiskUtil.HandleCreateDiskImage("cdi",
                    new string[] { diskFileName, "140k", "ProDOS" }, parms)) {
                throw new Exception("cdi " + diskFileName + " failed");
            }

            // Add a file that lives in the TestTmp/Samples subdirectory.  This creates the
            // "TestTmp" and "Samples" directories on the ProDOS volume.
            if (!Add.HandleAdd("add", new string[] { diskFileName, testFileName1 }, parms)) {
                throw new Exception("Error: add '" + diskFileName + "' '" + testFileName1 +
                    "' failed");
            }

            // Switch to TestTmp/Samples directory on host, and add a file from there.  This
            // should add the file to the same subdirectory, even though we're adding it as a
            // simple filename, because we're adding ":TestTmp:Samples" to the ext-archive name.
            string oldCurrentDir = Environment.CurrentDirectory;
            try {
                Environment.CurrentDirectory = SAMPLE_DIR;

                // Open "../addtest-dir.po:TestTmp:Samples"
                string extArcName = Path.Combine("..", fileName) + ExtArchive.SPLIT_CHAR +
                    ProDOS_FileEntry.AdjustFileName(Controller.TEST_TMP) + ExtArchive.SPLIT_CHAR +
                    ProDOS_FileEntry.AdjustFileName(SAMPLE_NAME);

                // Add "WithDir2"
                if (!Add.HandleAdd("add", new string[] { extArcName, testName2 }, parms)) {
                    throw new Exception("Error: add '" + extArcName + "' '" + testName2 +
                        "' failed");
                }
            } finally {
                Environment.CurrentDirectory = oldCurrentDir;
            }

            // Verify results with the output from "list".
            MemoryStream stdout = Controller.CaptureConsoleOut();
            if (!Catalog.HandleList("list", new string[] { diskFileName }, parms)) {
                throw new Exception("Error: list '" + diskFileName + "' failed");
            }
            char sep = ExtArchive.SPLIT_CHAR;       // used by "list" to separate dir names
            string[] expected = new string[] {
                Controller.TEST_TMP,
                Controller.TEST_TMP + sep + SAMPLE_NAME,
                Controller.TEST_TMP + sep + SAMPLE_NAME + sep + testName1,
                Controller.TEST_TMP + sep + SAMPLE_NAME + sep + testName2,
            };
            Controller.CompareLines(expected, stdout);
        }

        /// <summary>
        /// Tests adding a directory with and without "recurse" option set.
        /// </summary>
        private static void TestRecurse(ParamsBag parms) {
            string RECURSE_DIR = Path.Combine(SAMPLE_DIR, "Recurse");
            string RECURSE_SUBDIR = Path.Combine(RECURSE_DIR, "SubDir");

            // Create TestTmp/Samples/Recurse/Subdir
            Directory.CreateDirectory(RECURSE_SUBDIR);

            const string testName1 = "TestFile";
            string testFileName1 = Path.Combine(RECURSE_SUBDIR, testName1);
            using (Stream stream = new FileStream(testFileName1, FileMode.CreateNew)) {
                for (int i = 0; i < 11; i++) {
                    stream.WriteByte(0x03);
                }
            }

            string arcName = "test-recurse.zip";
            string arcFileName = Path.Combine(Controller.TEST_TMP, arcName);
            if (!ArcUtil.HandleCreateFileArchive("cfa", new string[] { arcFileName }, parms)) {
                throw new Exception("cfa " + arcFileName + " failed");
            }

            // Add a directory without recursion.  Should have no effect, and return false.
            parms.Recurse = false;
            if (Add.HandleAdd("add", new string[] { arcFileName, RECURSE_DIR }, parms)) {
                throw new Exception("Error: add '" + arcFileName + "' '" + RECURSE_DIR +
                    "' failed");
            }

            // With recurse enabled, should succeed.
            parms.Recurse = true;
            if (!Add.HandleAdd("add", new string[] { arcFileName, RECURSE_DIR }, parms)) {
                throw new Exception("Error: add '" + arcFileName + "' '" + RECURSE_DIR +
                    "' failed");
            }

            // Probably no need to check actual file contents for this trivial test.

            // Everything is in Recurse dir; no need to fuss over cleanup.
        }

        private static void TestNAPSUnescape(ParamsBag parms) {
            string escSubName = "%%sub%7cdir";
            string escSubPath = Path.Combine(SAMPLE_DIR, escSubName);
            Directory.CreateDirectory(escSubPath);
            string escName = Path.Combine(escSubName, "test%3fthis%2f%%!#ffcccc");

            string diskFileName = Path.Combine(Controller.TEST_TMP, "test-naps-unesc.po");
            diskFileName = Path.GetFullPath(diskFileName);
            parms.FromNAPS = true;

            string oldCurrentDir = Environment.CurrentDirectory;
            try {
                Environment.CurrentDirectory = SAMPLE_DIR;

                Controller.CreateSampleFile(escName, 0x11, 100);
                if (!DiskUtil.HandleCreateDiskImage("cdi",
                        new string[] { diskFileName, "800k", "HFS" }, parms )) {
                    throw new Exception("Error: cdi failed");
                }
                if (!Add.HandleAdd("add", new string[] { diskFileName, escName }, parms)) {
                    throw new Exception("Error: add " + diskFileName + " '" + escName + "' failed");
                }
            } finally {
                Environment.CurrentDirectory = oldCurrentDir;
            }

            // Verify.  Should have restored the %3f '?' and %2f '/'.  Should not have unescaped
            // the directory name.
            MemoryStream stdout = Controller.CaptureConsoleOut();
            if (!Catalog.HandleList("list", new string[] { diskFileName }, parms)) {
                throw new Exception("Error: list '" + diskFileName + "' failed");
            }
            string[] expected = new string[] {
                "%%sub%7cdir",
                "%%sub%7cdir:test?this/%!"
            };
            Controller.CompareLines(expected, stdout);
        }

        private static void TestTurducken(ParamsBag parms) {
            // Make a copy of a turducken file.
            string inputFile = Path.Join(Controller.TEST_DATA, "turducken", "MultiPart.hdv");
            string tdnFile = Path.Join(Controller.TEST_TMP, "tdn-test.hdv");
            FileUtil.CopyFile(inputFile, tdnFile);
            parms.SkipSimple = true;

            // Add a file to the gzip-compressed disk volume.
            string extArc = tdnFile + ExtArchive.SPLIT_CHAR + "2" + ExtArchive.SPLIT_CHAR +
                "small.woz.gz";
            parms.StripPaths = true;
            if (!Add.HandleAdd("add", new string[] { extArc, PLAIN_DATA }, parms)) {
                throw new Exception("add " + extArc + " " + PLAIN_DATA + " failed");
            }

            // List the contents.
            MemoryStream stdout = Controller.CaptureConsoleOut();
            if (!Catalog.HandleList("list", new string[] { extArc }, parms)) {
                throw new Exception("list '" + extArc + "' failed");
            }
            string[] expected = new string[] {
                "hello.txt",
                "again.txt",
                "PlainFile"
            };
            Controller.CompareLines(expected, stdout);

            // Repeat the procedure with a ZIP archive.
            extArc = tdnFile + ExtArchive.SPLIT_CHAR + "2" + ExtArchive.SPLIT_CHAR +
                "Some.Files.zip";
            if (!Add.HandleAdd("add", new string[] { extArc, PLAIN_DATA }, parms)) {
                throw new Exception("add " + extArc + " " + PLAIN_DATA + " failed");
            }
            stdout = Controller.CaptureConsoleOut();
            if (!Catalog.HandleList("list", new string[] { extArc }, parms)) {
                throw new Exception("list '" + extArc + "' failed");
            }
            expected = new string[] {
                "file1.txt",
                "file2.txt",
                "subdir/file3.txt",
                "PlainFile"
            };
            Controller.CompareLines(expected, stdout);
        }

        #region Setup

        /// <summary>
        /// Creates sample files, in the "TestTmp/Samples" directory.
        /// </summary>
        private static void CreateSamples(ParamsBag parms) {
            Directory.CreateDirectory(SAMPLE_DIR);

            using (FileStream file = new FileStream(AS_DATA, FileMode.CreateNew)) {
                using (AppleSingle arc = AppleSingle.CreateArchive(2, parms.AppHook)) {
                    arc.StartTransaction();
                    AddTestEntry(arc, AS_DATA_NAME, FilePart.DataFork);
                    arc.CommitTransaction(file);
                }
            }

            using (FileStream file = new FileStream(AS_RSRC, FileMode.CreateNew)) {
                using (AppleSingle arc = AppleSingle.CreateArchive(2, parms.AppHook)) {
                    arc.StartTransaction();
                    AddTestEntry(arc, AS_RSRC_NAME, FilePart.RsrcFork);
                    arc.CommitTransaction(file);
                }
            }

            using (FileStream file = new FileStream(AS_BOTH, FileMode.CreateNew)) {
                using (AppleSingle arc = AppleSingle.CreateArchive(2, parms.AppHook)) {
                    arc.StartTransaction();
                    AddTestEntry(arc, AS_BOTH_NAME, FilePart.DataFork);
                    AddTestEntry(arc, AS_BOTH_NAME, FilePart.RsrcFork);
                    arc.CommitTransaction(file);
                }
            }

            // Data-only ADF has a ._file with the file type info.
            using (FileStream file = new FileStream(ADF_DATA, FileMode.CreateNew)) {
                Controller.WriteByteValue(file, DATA_LEN, DATA_VALUE);
            }
            string adfName = Path.Combine(Path.GetDirectoryName(ADF_DATA)!,
                AppleSingle.ADF_PREFIX + Path.GetFileName(ADF_DATA));
            using (FileStream file = new FileStream(adfName, FileMode.CreateNew)) {
                using (AppleSingle arc = AppleSingle.CreateDouble(2, parms.AppHook)) {
                    arc.StartTransaction();
                    IFileEntry entry = arc.GetFirstEntry();
                    SetAttributes(entry, ADF_DATA);             // no data, just attributes
                    arc.CommitTransaction(file);
                }
            }

            using (FileStream file = new FileStream(ADF_BOTH, FileMode.CreateNew)) {
                Controller.WriteByteValue(file, DATA_LEN, DATA_VALUE);
            }
            adfName = Path.Combine(Path.GetDirectoryName(ADF_BOTH)!,
                AppleSingle.ADF_PREFIX + Path.GetFileName(ADF_BOTH));
            using (FileStream file = new FileStream(adfName, FileMode.CreateNew)) {
                using (AppleSingle arc = AppleSingle.CreateDouble(2, parms.AppHook)) {
                    arc.StartTransaction();
                    AddTestEntry(arc, ADF_BOTH, FilePart.RsrcFork);
                    arc.CommitTransaction(file);
                }
            }

            using (FileStream file = new FileStream(PLAIN_DATA, FileMode.CreateNew)) {
                Controller.WriteByteValue(file, DATA_LEN, DATA_VALUE);
            }

            using (FileStream file = new FileStream(NAPS_DATA_X, FileMode.CreateNew)) {
                Controller.WriteByteValue(file, DATA_LEN, DATA_VALUE);
            }

            using (FileStream file = new FileStream(NAPS_RSRC_X, FileMode.CreateNew)) {
                Controller.WriteByteValue(file, RSRC_LEN, RSRC_VALUE);
            }

            using (FileStream file = new FileStream(NAPS_BOTH_XD, FileMode.CreateNew)) {
                Controller.WriteByteValue(file, DATA_LEN, DATA_VALUE);
            }
            using (FileStream file = new FileStream(NAPS_BOTH_XR, FileMode.CreateNew)) {
                Controller.WriteByteValue(file, RSRC_LEN, RSRC_VALUE);
            }

            if (ParamsBag.HasHostPreservation) {
                using (FileStream file = new FileStream(HOST_DATA, FileMode.CreateNew)) {
                    Controller.WriteByteValue(file, DATA_LEN, DATA_VALUE);
                    SetHostAttributes(HOST_DATA);
                }

                using (FileStream file = new FileStream(HOST_BOTH, FileMode.CreateNew)) {
                    Controller.WriteByteValue(file, DATA_LEN, DATA_VALUE);
                    SetHostAttributes(HOST_BOTH);
                }
                string rsrcName = HOST_BOTH + AddFileSet.RSRC_FORK_SUFFIX;
                using (FileStream file = new FileStream(rsrcName, FileMode.CreateNew)) {
                    Controller.WriteByteValue(file, RSRC_LEN, RSRC_VALUE);
                }
            } else {
                // Fake it, using NAPS.  This is so we don't have to have special cases in the
                // verification code.
                string fileName = HOST_DATA + HFS_NAPS;
                using (FileStream file = new FileStream(fileName, FileMode.CreateNew)) {
                    Controller.WriteByteValue(file, DATA_LEN, DATA_VALUE);
                }

                fileName = HOST_BOTH + HFS_NAPS;
                using (FileStream file = new FileStream(fileName, FileMode.CreateNew)) {
                    Controller.WriteByteValue(file, DATA_LEN, DATA_VALUE);
                }
                using (FileStream file = new FileStream(fileName + 'r', FileMode.CreateNew)) {
                    Controller.WriteByteValue(file, RSRC_LEN, RSRC_VALUE);
                }
            }
        }

        /// <summary>
        /// Adds an entry to an archive, with data and/or resource forks.
        /// </summary>
        private static IFileEntry AddTestEntry(IArchive archive, string fileName, FilePart part) {
            IFileEntry entry;
            if (archive is AppleSingle || archive is GZip) {
                entry = archive.GetFirstEntry();
            } else {
                entry = archive.CreateRecord();
            }
            if (part == FilePart.DataFork) {
                archive.AddPart(entry, part, Controller.CreateSimpleSource(DATA_LEN, DATA_VALUE),
                    CompressionFormat.Default);
            } else if (part == FilePart.RsrcFork) {
                archive.AddPart(entry, part, Controller.CreateSimpleSource(RSRC_LEN, RSRC_VALUE),
                    CompressionFormat.Default);
            } else {
                throw new Exception("whoops");
            }
            SetAttributes(entry, fileName);
            return entry;
        }

        /// <summary>
        /// Sets entry attributes to the test values.
        /// </summary>
        private static void SetAttributes(IFileEntry entry, string fileName) {
            entry.FileName = fileName;
            entry.FileType = PRODOS_FILETYPE;
            entry.AuxType = PRODOS_AUXTYPE;
            entry.HFSFileType = HFS_FILETYPE;
            entry.HFSCreator = HFS_CREATOR;
            entry.CreateWhen = CREATE_WHEN;
            entry.ModWhen = MOD_WHEN;
            // handle Access here too?
        }

        /// <summary>
        /// Sets host attributes (Finder info) to the test values.
        /// </summary>
        private static void SetHostAttributes(string fileName) {
            byte[] attrBuf = new byte[SystemXAttr.FINDERINFO_LENGTH];
            RawData.SetU32BE(attrBuf, 0, HFS_FILETYPE);
            RawData.SetU32BE(attrBuf, 4, HFS_CREATOR);
            if (SystemXAttr.SetXAttr(fileName,
                    SystemXAttr.XATTR_FINDERINFO_NAME, attrBuf, 0) != 0) {
                throw new Exception("Unable to set host attributes");
            }
        }

        /// <summary>
        /// Converts a partial path to a ZIP+MacOS AppleDouble path.
        /// </summary>
        /// <remarks>
        /// For example, given "foo/bar/file.txt", this returns "__MACOSX/foo/bar/._file.txt".
        /// </remarks>
        /// <param name="partialPath">Partial pathname.</param>
        /// <returns>Modified partial pathanme.</returns>
        private static string MacZipIfy(string partialPath) {
            string dir = Path.Combine(Zip.MAC_ZIP_RSRC_PREFIX, Path.GetDirectoryName(partialPath)!);
            return Path.Combine(dir, AppleSingle.ADF_PREFIX + Path.GetFileName(partialPath));
        }

        #endregion Setup

        #region Basic add test

        private class ExpectedContents {
            public string FileName { get; private set; }
            public int MinDataLength { get; private set; }
            public int MaxDataLength { get; private set; }
            public int MinRsrcLength { get; private set; }
            public int MaxRsrcLength { get; private set; }
            public bool HasProType { get; private set; }
            public bool HasHfsType { get; private set; }
            public bool HasCreateDate { get; private set; }     // true if date is stored, not
            public bool HasModDate { get; private set; }        // if date is simply present

            public ExpectedContents(string fileName, int dataLength, int rsrcLength,
                    bool hasProType, bool hasHfsType, bool hasCreateDate, bool hasModDate)
                : this(fileName, dataLength, dataLength, rsrcLength, rsrcLength,
                      hasProType, hasHfsType, hasCreateDate, hasModDate) {
            }
            public ExpectedContents(string fileName, int minDataLength, int maxDataLength,
                    int minRsrcLength, int maxRsrcLength, bool hasProType, bool hasHfsType,
                    bool hasCreateDate, bool hasModDate) {
                FileName = fileName;
                MinDataLength = minDataLength;
                MaxDataLength = maxDataLength;
                MinRsrcLength = minRsrcLength;
                MaxRsrcLength = maxRsrcLength;
                HasProType = hasProType;
                HasHfsType = hasHfsType;
                HasCreateDate = hasCreateDate;
                HasModDate = hasModDate;
            }
        }

        // Maximum expected AppleSingle overhead.  Should be less than DATA_LEN so that we can
        // tell if an extra fork has been included.
        private const int AS_OVHD = 512;

        // Expected contents of Binary II archive after files added.
        private static ExpectedContents[] sAddTestBinary2 = new ExpectedContents[] {
            new ExpectedContents(Path.Combine(SAMPLE_DIR, "ADF.Both"), DATA_LEN, -1,
                hasProType: true, hasHfsType: false, hasCreateDate: true, hasModDate: true),
            new ExpectedContents(Path.Combine(SAMPLE_DIR, "ADF.Data"), DATA_LEN, -1,
                hasProType: true, hasHfsType: false, hasCreateDate: true, hasModDate: true),
            new ExpectedContents(Path.Combine(SAMPLE_DIR, "Part.Unicode.."), DATA_LEN, -1,
                hasProType: true, hasHfsType: false, hasCreateDate: true, hasModDate: true),
            new ExpectedContents(Path.Combine(SAMPLE_DIR, "Colon.name."), DATA_LEN, -1,
                hasProType: true, hasHfsType: false, hasCreateDate: true, hasModDate: true),
            new ExpectedContents(Path.Combine(SAMPLE_DIR, "Somethi..sAdded"), 0, -1,
                hasProType: true, hasHfsType: false, hasCreateDate: true, hasModDate: true),
            new ExpectedContents(Path.Combine(SAMPLE_DIR, "Host.Both"), DATA_LEN, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents(Path.Combine(SAMPLE_DIR, "Host.Data"), DATA_LEN, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents(Path.Combine(SAMPLE_DIR, "NAPS.Both"), DATA_LEN, -1,
                hasProType: true, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents(Path.Combine(SAMPLE_DIR, "NAPS.Data"), DATA_LEN, -1,
                hasProType: true, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            //new ExpectedContents(Path.Combine(SAMPLE_DIR, "NAPS.Rsrc"), 0, -1,
            //    hasProType: true, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents(PLAIN_DATA, DATA_LEN, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: false),
        };

        // Expected contents of NuFX archive after files added.
        private static ExpectedContents[] sAddTestNuFX = new ExpectedContents[] {
            new ExpectedContents(ADF_BOTH, DATA_LEN, RSRC_LEN,
                hasProType: true, hasHfsType: true, hasCreateDate: true, hasModDate: true),
            new ExpectedContents(ADF_DATA, DATA_LEN, -1,
                hasProType: true, hasHfsType: true, hasCreateDate: true, hasModDate: true),
            new ExpectedContents(Path.Combine(SAMPLE_DIR, "Part\u2022Unicode??"), DATA_LEN, RSRC_LEN,
                hasProType: true, hasHfsType: true, hasCreateDate: true, hasModDate: true),
            new ExpectedContents(Path.Combine(SAMPLE_DIR, "Colon?name!"), DATA_LEN, -1,
                hasProType: true, hasHfsType: true, hasCreateDate: true, hasModDate: true),
            new ExpectedContents(AS_RSRC_IN, 0, RSRC_LEN,
                hasProType: true, hasHfsType: true, hasCreateDate: true, hasModDate: true),
            new ExpectedContents(HOST_BOTH, DATA_LEN, RSRC_LEN,
                hasProType: false, hasHfsType: true, hasCreateDate: false, hasModDate: false),
            new ExpectedContents(HOST_DATA, DATA_LEN, -1,
                hasProType: false, hasHfsType: true, hasCreateDate: false, hasModDate: false),
            new ExpectedContents(NAPS_BOTH, DATA_LEN, RSRC_LEN,
                hasProType: true, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents(NAPS_DATA, DATA_LEN, -1,
                hasProType: true, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents(NAPS_RSRC, 0, RSRC_LEN,
                hasProType: true, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents(PLAIN_DATA, DATA_LEN, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: false),
        };

        // Expected contents of ZIP archive after files added, with --mac-zip enabled.
        private static ExpectedContents[] sAddTestMacZip = new ExpectedContents[] {
            new ExpectedContents(ADF_BOTH, DATA_LEN, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: true),
            new ExpectedContents(MacZipIfy(ADF_BOTH), RSRC_LEN+1, RSRC_LEN+AS_OVHD, -1, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: true),
            new ExpectedContents(ADF_DATA, DATA_LEN, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: true),
            new ExpectedContents(MacZipIfy(ADF_DATA), 1, AS_OVHD, -1, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: true),
            new ExpectedContents(AS_BOTH_IN, DATA_LEN, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: true),
            new ExpectedContents(MacZipIfy(AS_BOTH_IN), RSRC_LEN, RSRC_LEN+AS_OVHD, -1, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: true),
            new ExpectedContents(AS_DATA_IN, DATA_LEN, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: true),
            new ExpectedContents(MacZipIfy(AS_DATA_IN), 1, AS_OVHD, -1, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: true),
            new ExpectedContents(AS_RSRC_IN, 0, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: true),
            new ExpectedContents(MacZipIfy(AS_RSRC_IN), RSRC_LEN+1, RSRC_LEN+AS_OVHD, -1, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: true),
            new ExpectedContents(HOST_BOTH, DATA_LEN, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents(MacZipIfy(HOST_BOTH), RSRC_LEN+1, RSRC_LEN+AS_OVHD, -1, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents(HOST_DATA, DATA_LEN, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents(MacZipIfy(HOST_DATA), 1, AS_OVHD, -1, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents(NAPS_BOTH, DATA_LEN, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents(MacZipIfy(NAPS_BOTH), RSRC_LEN+1, RSRC_LEN+AS_OVHD, -1, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents(NAPS_DATA, DATA_LEN, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents(MacZipIfy(NAPS_DATA), 1, AS_OVHD, -1, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents(NAPS_RSRC, 0, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents(MacZipIfy(NAPS_RSRC), RSRC_LEN+1, RSRC_LEN+AS_OVHD, -1, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents(PLAIN_DATA, DATA_LEN, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: false),
        };

        private static ExpectedContents[] sAddTestZip = new ExpectedContents[] {
            new ExpectedContents(ADF_BOTH, DATA_LEN, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: true),
            new ExpectedContents(ADF_DATA, DATA_LEN, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: true),
            new ExpectedContents(AS_BOTH_IN, DATA_LEN, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: true),
            new ExpectedContents(AS_DATA_IN, DATA_LEN, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: true),
            new ExpectedContents(AS_RSRC_IN, 0, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: true),
            new ExpectedContents(HOST_BOTH, DATA_LEN, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents(HOST_DATA, DATA_LEN, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents(NAPS_BOTH, DATA_LEN, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents(NAPS_DATA, DATA_LEN, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            //new ExpectedContents(NAPS_RSRC, 0, -1,
            //    hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents(PLAIN_DATA, DATA_LEN, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: false),
        };

        // Expected contents of ZIP archive when added with all "from" options disabled.
        private static ExpectedContents[] sAddTestZipNoFrom = new ExpectedContents[] {
            new ExpectedContents("._ADF_Both", RSRC_LEN+1, RSRC_LEN+AS_OVHD, -1, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents("._ADF_Data", 1, AS_OVHD, -1, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents("ADF_Both", DATA_LEN, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents("ADF_Data", DATA_LEN, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents("AS_Both.as", DATA_LEN+RSRC_LEN+1, DATA_LEN+RSRC_LEN+AS_OVHD, -1, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents("AS_Data.as", DATA_LEN+1, DATA_LEN+AS_OVHD, -1, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents("AS_Rsrc.as", RSRC_LEN+1, RSRC_LEN+AS_OVHD, -1, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents("Host_Both" + HFS_NAPS, DATA_LEN, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents("Host_Both" + HFS_NAPS + 'r', RSRC_LEN, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents("Host_Data" + HFS_NAPS, DATA_LEN, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents("NAPS_Both#0612ab", DATA_LEN, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents("NAPS_Both#0612ABr", RSRC_LEN, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents("NAPS_Data#0612ab", DATA_LEN, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents("NAPS_Rsrc#0612abR", RSRC_LEN, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents("PlainFile", DATA_LEN, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: false),
        };
        // A copy of the above without the Host_Both resource fork.  Since we have actual
        // resource forks, a from=none add will miss it.  Also, we won't have NAPS tags on them.
        private static ExpectedContents[] sAddTestZipNoFromMac = new ExpectedContents[] {
            new ExpectedContents("._ADF_Both", RSRC_LEN+1, RSRC_LEN+AS_OVHD, -1, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents("._ADF_Data", 1, AS_OVHD, -1, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents("ADF_Both", DATA_LEN, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents("ADF_Data", DATA_LEN, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents("AS_Both.as", DATA_LEN+RSRC_LEN+1, DATA_LEN+RSRC_LEN+AS_OVHD, -1, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents("AS_Data.as", DATA_LEN+1, DATA_LEN+AS_OVHD, -1, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents("AS_Rsrc.as", RSRC_LEN+1, RSRC_LEN+AS_OVHD, -1, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents("Host_Both", DATA_LEN, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            //new ExpectedContents("Host_Both" + HFS_NAPS + 'r', RSRC_LEN, -1,
            //    hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents("Host_Data", DATA_LEN, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents("NAPS_Both#0612ab", DATA_LEN, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents("NAPS_Both#0612ABr", RSRC_LEN, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents("NAPS_Data#0612ab", DATA_LEN, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents("NAPS_Rsrc#0612abR", RSRC_LEN, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents("PlainFile", DATA_LEN, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: false),
        };

        // Expected contents of DOS filesystem after files added.
        private static ExpectedContents[] sAddTestDOS = new ExpectedContents[] {
            new ExpectedContents("ADF_BOTH", DATA_LEN, -1,
                hasProType: true, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents("ADF_DATA", DATA_LEN, -1,
                hasProType: true, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents("PART?UNICODE??", DATA_LEN, -1,
                hasProType: true, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents("COLON:NAME!", DATA_LEN, -1,
                hasProType: true, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents("SOMETHINGVERYLO..ENFILEISADDED", 0, -1,
                hasProType: true, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents("HOST_BOTH", DATA_LEN, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents("HOST_DATA", DATA_LEN, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents("NAPS_BOTH", DATA_LEN, -1,
                hasProType: true, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents("NAPS_DATA", DATA_LEN, -1,
                hasProType: true, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            //new ExpectedContents("NAPS_RSRC", 0, -1,
            //    hasProType: true, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents("PLAINFILE", DATA_LEN, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: false),
        };

        // Expected contents of HFS filesystem after files added.
        private static ExpectedContents[] sAddTestHFS = new ExpectedContents[] {
            new ExpectedContents(ADF_BOTH, DATA_LEN, RSRC_LEN,
                hasProType: false, hasHfsType: true, hasCreateDate: true, hasModDate: true),
            new ExpectedContents(ADF_DATA, DATA_LEN, 0,
                hasProType: false, hasHfsType: true, hasCreateDate: true, hasModDate: true),
            new ExpectedContents(Path.Combine(SAMPLE_DIR, "Colon?name!"), DATA_LEN, 0,
                hasProType: false, hasHfsType: true, hasCreateDate: true, hasModDate: true),
            new ExpectedContents(HOST_BOTH, DATA_LEN, RSRC_LEN,
                hasProType: false, hasHfsType: true, hasCreateDate: false, hasModDate: false),
            new ExpectedContents(HOST_DATA, DATA_LEN, 0,
                hasProType: false, hasHfsType: true, hasCreateDate: false, hasModDate: false),
            new ExpectedContents(NAPS_BOTH, DATA_LEN, RSRC_LEN,
                hasProType: false, hasHfsType: true, hasCreateDate: false, hasModDate: false),
            new ExpectedContents(NAPS_DATA, DATA_LEN, 0,
                hasProType: false, hasHfsType: true, hasCreateDate: false, hasModDate: false),
            new ExpectedContents(NAPS_RSRC, 0, RSRC_LEN,
                hasProType: false, hasHfsType: true, hasCreateDate: false, hasModDate: false),
            new ExpectedContents(Path.Combine(SAMPLE_DIR, "Part\u2022Unicode??"), DATA_LEN, RSRC_LEN,
                hasProType: false, hasHfsType: true, hasCreateDate: true, hasModDate: true),
            new ExpectedContents(PLAIN_DATA, DATA_LEN, 0,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents(Path.Combine(SAMPLE_DIR, "SomethingVeryLo..henFileIsAdded"), 0, RSRC_LEN,
                hasProType: false, hasHfsType: true, hasCreateDate: true, hasModDate: true),
        };

        // Expected contents of ProDOS filesystem after files added.
        private static ExpectedContents[] sAddTestProDOS = new ExpectedContents[] {
            new ExpectedContents(Path.Combine(SAMPLE_DIR, "ADF.Both"), DATA_LEN, RSRC_LEN,
                hasProType: true, hasHfsType: true, hasCreateDate: true, hasModDate: true),
            new ExpectedContents(Path.Combine(SAMPLE_DIR, "ADF.Data"), DATA_LEN, -1,
                hasProType: true, hasHfsType: false, hasCreateDate: true, hasModDate: true),
            new ExpectedContents(Path.Combine(SAMPLE_DIR, "Part.Unicode.."), DATA_LEN, RSRC_LEN,
                hasProType: true, hasHfsType: true, hasCreateDate: true, hasModDate: true),
            new ExpectedContents(Path.Combine(SAMPLE_DIR, "Colon.name."), DATA_LEN, -1,
                hasProType: true, hasHfsType: false, hasCreateDate: true, hasModDate: true),
            new ExpectedContents(Path.Combine(SAMPLE_DIR, "Somethi..sAdded"), 0, RSRC_LEN,
                hasProType: true, hasHfsType: true, hasCreateDate: true, hasModDate: true),
            new ExpectedContents(Path.Combine(SAMPLE_DIR, "Host.Both"), DATA_LEN, RSRC_LEN,
                hasProType: false, hasHfsType: true, hasCreateDate: false, hasModDate: false),
            new ExpectedContents(Path.Combine(SAMPLE_DIR, "Host.Data"), DATA_LEN, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents(Path.Combine(SAMPLE_DIR, "NAPS.Both"), DATA_LEN, RSRC_LEN,
                hasProType: true, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents(Path.Combine(SAMPLE_DIR, "NAPS.Data"), DATA_LEN, -1,
                hasProType: true, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents(Path.Combine(SAMPLE_DIR, "NAPS.Rsrc"), 0, RSRC_LEN,
                hasProType: true, hasHfsType: false, hasCreateDate: false, hasModDate: false),
            new ExpectedContents(PLAIN_DATA, DATA_LEN, -1,
                hasProType: false, hasHfsType: false, hasCreateDate: false, hasModDate: false),
        };

        /// <summary>
        /// Compares the contents of an archive or filesystem to the expected values.
        /// </summary>
        private static void VerifyContents(string fileName, ExpectedContents[] expCont,
                ParamsBag parms) {
            if (!ExtArchive.OpenExtArc(fileName, true, false, parms, out DiskArcNode? rootNode,
                    out DiskArcNode? leafNode, out object? leaf, out IFileEntry endDirEntry)) {
                throw new Exception("Unable to open file: " + fileName);
            }

            using (rootNode) {
                if (leaf is IArchive) {
                    IArchive arc = (IArchive)leaf;
                    List<IFileEntry> entries = arc.ToList();
                    if (entries.Count != expCont.Length) {
                        throw new Exception("Incorrect file count (exp=" + expCont.Length +
                            " act=" + entries.Count + ") in " + fileName);
                    }
                    for (int i = 0; i < expCont.Length; i++) {
                        VerifyEntry(i, expCont[i], entries[i], true);
                    }
                } else {
                    IFileSystem? fs = Misc.GetTopFileSystem(leaf);
                    if (fs == null) {
                        throw new Exception("Unable to find filesystem in " + fileName);
                    }
                    fs.PrepareFileAccess(true);
                    List<IFileEntry> entries = new List<IFileEntry>();
                    GatherFileEntries(fs, fs.GetVolDirEntry(), entries);

                    for (int i = 0; i < expCont.Length; i++) {
                        VerifyEntry(i, expCont[i], entries[i], false);
                    }
                }
            }
        }

        /// <summary>
        /// Compares the values in a single entry to the expected values.
        /// </summary>
        private static void VerifyEntry(int index, ExpectedContents exp, IFileEntry entry,
                bool isArchive) {
            string hdr = entry.GetType().Name + " " + index + " '" + exp.FileName + "': ";

            if (isArchive) {
                // File archive.  We have the full name, so we can use the path compare function
                // to check for a match.
                if (entry.CompareFileName(exp.FileName, Path.DirectorySeparatorChar) != 0) {
                    throw new Exception(hdr + "incorrect filename: expect='" +
                        exp.FileName + "' actual='" + entry.FileName + "'");
                }
            } else {
                // Disk image.  We need to construct the full path and compare it to the
                // expected value.  We use the host path separator so we can just compare the
                // string directly.  This limits us to filenames that don't include '/' or '\'.
                string fullPath = BuildPathName(entry, Path.DirectorySeparatorChar);
                if (fullPath != exp.FileName) {
                    throw new Exception(hdr + "incorrect filename: expect='" +
                        exp.FileName + "' actual='" + fullPath + "'");
                }
            }
            if (exp.MinDataLength < 0) {
                // No data fork expected.
                if (entry.HasDataFork) {
                    throw new Exception(hdr + "unexpected data fork");
                }
            } else {
                if (entry.DataLength < exp.MinDataLength || entry.DataLength > exp.MaxDataLength) {
                    throw new Exception(hdr + "unexpected data length: " +
                        entry.DataLength);
                }
            }
            if (exp.MinRsrcLength < 0) {
                // No resource fork expected.
                if (entry.HasRsrcFork) {
                    throw new Exception(hdr + "unexpected rsrc fork");
                }
            } else {
                if (entry.RsrcLength < exp.MinRsrcLength || entry.RsrcLength > exp.MaxRsrcLength) {
                    throw new Exception(hdr + "unexpected rsrc length: " +
                        entry.RsrcLength);
                }
            }
            if (exp.HasProType) {
                if (entry.FileType != PRODOS_FILETYPE || entry.AuxType != PRODOS_AUXTYPE) {
                    throw new Exception(hdr + "incorrect ProDOS file type (" +
                        entry.FileType.ToString("x2") + "/" +
                        entry.AuxType.ToString("x4") + ")");
                }
            } else {
                if (entry is DOS_FileEntry) {
                    // DOS encodes $00/NON as type 'S', which is $F2.
                    if (entry.FileType != FileAttribs.FILE_TYPE_F2 || entry.AuxType != 0) {
                        throw new Exception(hdr + "unexpected (Pro)DOS file type (" +
                            entry.FileType.ToString("x2") + "/" +
                            entry.AuxType.ToString("x4") + ")");
                    }
                } else if (entry.FileType != 0 || entry.AuxType != 0) {
                    throw new Exception(hdr + "unexpected ProDOS file type (" +
                        entry.FileType.ToString("x2") + "/" +
                        entry.AuxType.ToString("x4") + ")");
                }
            }
            if (exp.HasHfsType) {
                if (entry.HFSFileType != HFS_FILETYPE || entry.HFSCreator != HFS_CREATOR) {
                    if (entry.HFSFileType == HFS_PDOS_FILETYPE &&
                            entry.HFSCreator == HFS_PDOS_CREATOR) {
                        // We don't really have an HFS file type; we're storing the PrODOS file
                        // type in the HFS fields.  TODO: make the distinction in the test data
                    } else {
                        throw new Exception(hdr + "incorrect HFS file type (" +
                            entry.HFSFileType.ToString("x8") + "/" +
                            entry.HFSCreator.ToString("x8") + ")");
                    }
                }
            } else {
                uint expFileType = 0;
                uint expCreator = 0;
                if (entry.HasHFSTypes && !entry.HasProDOSTypes) {
                    expFileType = FileAttribs.TYPE_BINA;
                    expCreator = FileAttribs.CREATOR_CPII;
                }
                if (entry.HFSFileType != expFileType || entry.HFSCreator != expCreator) {
                    throw new Exception(hdr + "unexpected HFS file type (" +
                        entry.HFSFileType.ToString("x8") + "/" +
                        entry.HFSCreator.ToString("x8") + ")");
                }
            }
            if (exp.HasCreateDate) {
                if (entry.CreateWhen != CREATE_WHEN) {
                    throw new Exception(hdr + "incorrect CreateWhen: " + entry.CreateWhen);
                }
            } else {
                // Date should either be unset or based off of the host timestamp.
                if (entry.CreateWhen != TimeStamp.NO_DATE && entry.CreateWhen < RECENT_WHEN) {
                    throw new Exception(hdr + "unexpected CreateWhen: " + entry.CreateWhen + " vs " + RECENT_WHEN);
                }
            }
            if (exp.HasModDate) {
                if (entry.ModWhen != MOD_WHEN) {
                    throw new Exception(hdr + "incorrect ModWhen: " + entry.ModWhen);
                }
            } else {
                // Date should either be unset or based off of the host timestamp.
                if (entry.ModWhen != TimeStamp.NO_DATE && entry.ModWhen < RECENT_WHEN) {
                    throw new Exception(hdr + "unexpected ModWhen: " + entry.ModWhen + " vs " + RECENT_WHEN);
                }
            }
        }

        private static string BuildPathName(IFileEntry entry, char pathSep) {
            StringBuilder sb = new StringBuilder();
            BuildPathName(entry, pathSep, sb);
            return sb.ToString();
        }
        private static void BuildPathName(IFileEntry entry, char dirSep,
                StringBuilder sb) {
            if (entry.ContainingDir != IFileEntry.NO_ENTRY) {
                BuildPathName(entry.ContainingDir, dirSep, sb);
                if (sb.Length > 0) {
                    sb.Append(dirSep);
                }
                string fileName = entry.FileName;
                sb.Append(fileName);
            }
        }

        /// <summary>
        /// Recursively gathers all the non-directory entries into a list.
        /// </summary>
        private static void GatherFileEntries(IFileSystem fs, IFileEntry dirEntry,
                List<IFileEntry> entries) {
            Debug.Assert(dirEntry.IsDirectory);
            foreach (IFileEntry entry in dirEntry) {
                if (entry.IsDirectory) {
                    GatherFileEntries(fs, entry, entries);
                } else {
                    entries.Add(entry);
                }
            }
        }

        #endregion Basic add test

        /// <summary>
        /// Executes an "add" command.  Uses the internal entry point so parms are not reset.
        /// </summary>
        private static MemoryStream CallAdd(string extArchive, string fileOrDir, ParamsBag parms) {
            MemoryStream stdout = Controller.CaptureConsoleOut();
            Controller.CaptureConsoleError();
            if (!Add.HandleAdd("add", new string[] { extArchive, fileOrDir }, parms)) {
                throw new Exception("Error: add '" + extArchive + "' '" + fileOrDir + "' failed");
            }
            return stdout;
        }
    }
}
