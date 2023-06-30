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
using static DiskArc.Defs;

namespace cp2.Tests {
    /// <summary>
    /// Tests the "move" command.
    /// </summary>
    internal static class TestMove {
        public static void RunTest(ParamsBag parms) {
            Controller.Stdout.WriteLine("  Move...");
            Controller.PrepareTestTmp(parms);

            //
            // Scenarios to test:
            // - File archive (single-file rename only).
            //   - Special case for MacZip.
            //   - Special case for AppleSingle with empty filename.
            // - Disk image, volume rename.
            // - Disk image, single-file move/rename.
            // - Disk image, multi-file move to subdirectory.
            // - (fail) Disk image, move directory into its own subdirectory.
            //

            parms.MacZip = true;
            parms.Overwrite = false;

            TestFileArchive(parms);
            TestVolumeRename(parms);
            TestDiskImage(parms);
            TestTurducken(parms);

            Controller.RemoveTestTmp(parms);
        }

        /// <summary>
        /// Tests operations on file archives.
        /// </summary>
        private static void TestFileArchive(ParamsBag parms) {
            // Copy some prefab archives in.
            string zipTest = "GSHK11.zip";
            FileUtil.CopyFile(Path.Join(Controller.TEST_DATA, "zip", zipTest),
                Path.Join(Controller.TEST_TMP, zipTest));
            string asTest = "MacIP.RES.as";
            FileUtil.CopyFile(Path.Join(Controller.TEST_DATA, "as", asTest),
                Path.Join(Controller.TEST_TMP, asTest));

            string oldCurrentDir = Environment.CurrentDirectory;
            try {
                Environment.CurrentDirectory = Controller.TEST_TMP;

                //
                // Do ZIP renames, with and without MacZip.
                //
                parms.MacZip = true;
                if (!Move.HandleMove("mv",
                        new string[] { zipTest, "GSHK", "GSHK-new" }, parms)) {
                    throw new Exception("mv " + zipTest + " GSHK failed");
                }
                parms.MacZip = false;
                if (!Move.HandleMove("mv",
                        new string[] { zipTest, "gshk.docs", "gshk.docs-new" }, parms)) {
                    throw new Exception("mv " + zipTest + " gshk.docs failed");
                }
                MemoryStream stdout = Controller.CaptureConsoleOut();
                if (!Catalog.HandleList("list", new string[] { zipTest }, parms)) {
                    throw new Exception("Error: list '" + zipTest + "' failed");
                }
                string[] expected = new string[] {
                    "GSHK-new",
                    "__MACOSX/._GSHK-new",          // should have updated
                    "gshk.docs-new",
                    "__MACOSX/._gshk.docs",         // should NOT have updated
                    "Release.Notes",
                    "__MACOSX/._Release.Notes",
                };
                Controller.CompareLines(expected, stdout);

                //
                // Attempt a multi-rename in ZIP.
                //
                if (Move.HandleMove("mv", new string[] { zipTest, "*", "dir" }, parms)) {
                    throw new Exception("Multi-move in archive succeeded");
                }

                //
                // With an AppleSingle file, rename a blank entry, then put it back.
                //
                if (!Move.HandleMove("mv", new string[] { asTest, string.Empty, "named" }, parms)) {
                    throw new Exception("mv " + asTest + " from empty failed");
                }
                if (!Move.HandleMove("mv", new string[] { asTest, "named", string.Empty }, parms)) {
                    throw new Exception("mv " + asTest + " to empty failed");
                }
                stdout = Controller.CaptureConsoleOut();
                if (!Catalog.HandleList("list", new string[] { asTest }, parms)) {
                    throw new Exception("Error: list '" + asTest + "' failed");
                }
                expected = new string[] { string.Empty };
                Controller.CompareLines(expected, stdout);
            } finally {
                Environment.CurrentDirectory = oldCurrentDir;
            }
        }

        /// <summary>
        /// Tests renaming a volume.
        /// </summary>
        private static void TestVolumeRename(ParamsBag parms) {
            string hfsTest = "blank-800k.po";
            FileUtil.CopyFile(Path.Join(Controller.TEST_DATA, "hfs", hfsTest),
                Path.Join(Controller.TEST_TMP, hfsTest));

            string oldCurrentDir = Environment.CurrentDirectory;
            try {
                Environment.CurrentDirectory = Controller.TEST_TMP;

                // Rename the volume on an HFS disk, which has slightly different rules for
                // file and volume names (27 vs. 31 chars).
                string longVolName = "012345678901234567890123456";
                if (!Move.HandleMove("mv", new string[] { hfsTest, ":", longVolName }, parms)) {
                    throw new Exception("mv " + hfsTest + " " + longVolName + " failed");
                }
                if (GetVolName(hfsTest, parms) != longVolName) {
                    throw new Exception("Incorrect volume name #1");
                }

                // Rename to an over-length name and confirm the adjustment.  (This will break if
                // the HFS name adjuster implementation changes.)
                string tooLongVolName = "0123456789012345678901234567";
                if (!Move.HandleMove("mv", new string[] { hfsTest, ":", tooLongVolName }, parms)) {
                    throw new Exception("mv " + hfsTest + " " + tooLongVolName + " succeeded");
                }
                if (GetVolName(hfsTest, parms) != "0123456789012..678901234567") {
                    throw new Exception("Incorrect volume name #2");
                }

                //
                // Try to rename a DOS disk.
                //
                string dosTest = "dos-test.do";
                if (!DiskUtil.HandleCreateDiskImage("cdi",
                        new string[] { dosTest, "140k", "dos"}, parms)) {
                    throw new Exception("cdi " + dosTest + " failed");
                }
                if (!Move.HandleMove("mv", new string[] { dosTest, ":", "123" }, parms)) {
                    throw new Exception("DOS volume rename failed");
                }
            } finally {
                Environment.CurrentDirectory = oldCurrentDir;
            }
        }

        /// <summary>
        /// Tests renaming files on a disk image.
        /// </summary>
        private static void TestDiskImage(ParamsBag parms) {
            string proTest = "simple-dir-test.po";
            FileUtil.CopyFile(Path.Join(Controller.TEST_DATA, "prodos", proTest),
                Path.Join(Controller.TEST_TMP, proTest));

            string oldCurrentDir = Environment.CurrentDirectory;
            try {
                Environment.CurrentDirectory = Controller.TEST_TMP;

                // Do all of the operations, then check the final result.  Works great until
                // something goes wrong.

                // Rename a single file.  We use an invalid name that will get remapped.
                if (!Move.HandleMove("mv",
                        new string[] { proTest, "PRODOS.1.1.1", "2PRODOS" }, parms)) {
                    throw new Exception("mv " + proTest + " PRODOS failed");
                }
                // Rename a single directory.
                if (!Move.HandleMove("mv", new string[] {
                        proTest, "SUBDIR1:SUBDIR2:SUBDIR3", "SUBDIR1:SUBDIR2:SUBDIR3A" }, parms)) {
                    throw new Exception("mv " + proTest + " SUBDIR3 failed");
                }
                // Move a single file to a subdir.
                if (!Move.HandleMove("mv",
                        new string[] { proTest, "FILES.ADD.WITH", "SUBDIR1" }, parms)) {
                    throw new Exception("mv " + proTest + " FILES.ADD.WITH failed");
                }
                // Move a single file to the volume dir.
                if (!Move.HandleMove("mv", new string[] { proTest, "SUBDIR1:H", "/" }, parms)) {
                    throw new Exception("mv " + proTest + " SUBDIR1:H failed");
                }
                // Move a single file with an ext-archive root, from a subdir to the root.
                string extArc = proTest + ExtArchive.SPLIT_CHAR + "SUBDIR1";
                if (!Move.HandleMove("mv", new string[] { extArc, "SUBDIR2:A12", ":" }, parms)) {
                    throw new Exception("mv " + extArc + " failed");
                }
                // Move multiple files into a subdir.
                if (!Move.HandleMove("mv", new string[] {
                        proTest, "SUBDIR1:SUBDIR2:A2?", "SUBDIR1:SUBDIR2:SUBDIR3A" }, parms)) {
                    throw new Exception("mv " + proTest + " A2? failed");
                }
                // Try an illegal move.
                if (Move.HandleMove("mv",
                        new string[] { proTest, "SUBDIR1", "SUBDIR1:SUBDIR2" }, parms)) {
                    throw new Exception("mv " + proTest + " into self succeeded");
                }

                // List the contents and check them.
                MemoryStream stdout = Controller.CaptureConsoleOut();
                if (!Catalog.HandleList("list", new string[] { proTest }, parms)) {
                    throw new Exception("Error: list '" + proTest + "' failed");
                }
                string[] expected = new string[] {
                    "SUBDIR1",
                    "SUBDIR1:A",
                    "SUBDIR1:B",
                    "SUBDIR1:C",
                    "SUBDIR1:D",
                    "SUBDIR1:E",
                    "SUBDIR1:F",
                    "SUBDIR1:G",
                    "SUBDIR1:A12",      // H moved out, A12 moved in
                    "SUBDIR1:I",
                    "SUBDIR1:J",
                    "SUBDIR1:K",
                    "SUBDIR1:L",
                    "SUBDIR1:M",
                    "SUBDIR1:N",
                    "SUBDIR1:O",
                    "SUBDIR1:SUBDIR2",
                    "SUBDIR1:SUBDIR2:A1",
                    "SUBDIR1:SUBDIR2:A2",
                    "SUBDIR1:SUBDIR2:A3",
                    "SUBDIR1:SUBDIR2:A4",
                    "SUBDIR1:SUBDIR2:A5",
                    "SUBDIR1:SUBDIR2:A6",
                    "SUBDIR1:SUBDIR2:A7",
                    "SUBDIR1:SUBDIR2:A8",
                    "SUBDIR1:SUBDIR2:A9",
                    "SUBDIR1:SUBDIR2:A10",
                    "SUBDIR1:SUBDIR2:A11",
                    "SUBDIR1:SUBDIR2:A13",
                    "SUBDIR1:SUBDIR2:A14",
                    "SUBDIR1:SUBDIR2:A15",
                    "SUBDIR1:SUBDIR2:A16",
                    "SUBDIR1:SUBDIR2:A17",
                    "SUBDIR1:SUBDIR2:A18",
                    "SUBDIR1:SUBDIR2:A19",
                    "SUBDIR1:SUBDIR2:SUBDIR3A",
                    "SUBDIR1:SUBDIR2:SUBDIR3A:LEAF",
                    "SUBDIR1:SUBDIR2:SUBDIR3A:A20",
                    "SUBDIR1:SUBDIR2:SUBDIR3A:A21",
                    "SUBDIR1:SUBDIR2:SUBDIR3A:A22",
                    "SUBDIR1:SUBDIR2:SUBDIR3A:A23",
                    "SUBDIR1:SUBDIR2:SUBDIR3A:A24",
                    "SUBDIR1:SUBDIR2:SUBDIR3A:A25",
                    "SUBDIR1:SUBDIR2:SUBDIR3A:A26",
                    "SUBDIR1:FILES.ADD.WITH",
                    "H",
                    "X2PRODOS",         // this will break if ProDOS filename adjuster changes
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

            // Rename a file in the gzip-compressed disk volume.
            string extArc = tdnFile + ExtArchive.SPLIT_CHAR + "2" + ExtArchive.SPLIT_CHAR +
                "small.woz.gz";
            if (!Move.HandleMove("mv", new string[] { extArc, "hello.txt", "wahoo.txt" }, parms)) {
                throw new Exception("mv " + extArc + " hello.txt failed");
            }

            // List the contents.
            MemoryStream stdout = Controller.CaptureConsoleOut();
            if (!Catalog.HandleList("list", new string[] { extArc }, parms)) {
                throw new Exception("list '" + extArc + "' failed");
            }
            string[] expected = new string[] {
                "wahoo.txt",
                "again.txt",
            };
            Controller.CompareLines(expected, stdout);

            // Repeat the procedure with a ZIP archive.
            extArc = tdnFile + ExtArchive.SPLIT_CHAR + "2" + ExtArchive.SPLIT_CHAR +
                "Some.Files.zip";
            if (!Move.HandleMove("mv",
                    new string[] { extArc, "subdir/file3.txt", "dir1/dir2/file3.bin" }, parms)) {
                throw new Exception("mv " + extArc + "subdir/file3.txt failed");
            }
            stdout = Controller.CaptureConsoleOut();
            if (!Catalog.HandleList("list", new string[] { extArc }, parms)) {
                throw new Exception("list '" + extArc + "' failed");
            }
            expected = new string[] {
                "file1.txt",
                "file2.txt",
                "dir1/dir2/file3.bin"
            };
            Controller.CompareLines(expected, stdout);
        }

        /// <summary>
        /// Gets the volume name from a disk image file.
        /// </summary>
        /// <param name="pathName">Path to disk image file.</param>
        /// <param name="parms">Application parameters.</param>
        /// <returns>Volume name, or null if it could not be found.</returns>
        private static string? GetVolName(string pathName, ParamsBag parms) {
            using (FileStream stream = new FileStream(pathName, FileMode.Open, FileAccess.Read)) {
                FileAnalyzer.AnalysisResult result = FileAnalyzer.Analyze(stream,
                    Path.GetExtension(pathName), parms.AppHook, out FileKind kind,
                    out SectorOrder unused);
                if (result != FileAnalyzer.AnalysisResult.Success) {
                    return null;
                }
                if (!Defs.IsDiskImageFile(kind)) {
                    return null;
                }
                IDiskImage disk = FileAnalyzer.PrepareDiskImage(stream, kind, parms.AppHook)!;
                using (disk) {
                    disk.AnalyzeDisk();
                    if (disk.Contents == null) {
                        return null;
                    }
                    IFileSystem fs = (IFileSystem)disk.Contents;
                    fs.PrepareFileAccess(true);
                    IFileEntry volDir = fs.GetVolDirEntry();
                    return volDir.FileName;
                }
            }
        }
    }
}
