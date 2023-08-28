/*
 * Copyright 2022 faddenSoft
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
using System.IO;
using System.Linq;

using CommonUtil;
using DiskArc;
using static DiskArc.Defs;
using static DiskArc.IFileSystem;

namespace DiskArcTests {
    /// <summary>
    /// Tests IFileSystem interfaces in a filesystem-independent way.  We can't exercise the
    /// interesting instance-specific edge cases, but we can confirm that everything fails
    /// the same way when bad arguments are passed.  These tests are less about exercising
    /// the success cases and more about checking the simple failure cases for consistency.
    /// </summary>
    /// <remarks>
    /// <para>Tests are split into read-only and read-write, because not all filesystems are
    /// fully implemented.  Tests that require hierarchical structure are also separated.</para>
    /// </remarks>
    public class TestFSInterfaces : ITest {
        private static bool DUMP_TO_FILE = false;

        #region FS Drivers

        public static void TestCPM(AppHook appHook) {
            using (Stream dataFile = Helper.OpenTestFile("cpm/CPAM51a.do", true, appHook)) {
                using (IDiskImage diskImage = FileAnalyzer.PrepareDiskImage(dataFile,
                        FileKind.UnadornedSector, appHook)!) {
                    diskImage.AnalyzeDisk();
                    IFileSystem fs = (IFileSystem)diskImage.Contents!;

                    TestReadOnly(fs, "COPY.COM", "NO.SUCH.FILE", appHook);
                }
            }

            using (IFileSystem wfs = Helper.CreateTestImage("TESTVOL", FileSystemType.CPM,
                    35, 16, 254, false, appHook, out MemoryStream memFile)) {
                wfs.PrepareRawAccess();
                TestReadWrite(wfs, "TEST.TXT", "INVAL?ID", appHook);

                wfs.PrepareRawAccess();
                wfs.PrepareFileAccess(true);
                Helper.CheckNotes(wfs, 0, 0);
            }
        }

        public static void TestDOS(AppHook appHook) {
            using (Stream dataFile = Helper.OpenTestFile("dos33/system-master-1983.po",
                    true, appHook)) {
                using (IDiskImage diskImage = FileAnalyzer.PrepareDiskImage(dataFile,
                        FileKind.UnadornedSector, appHook)!) {
                    diskImage.AnalyzeDisk();
                    IFileSystem fs = (IFileSystem)diskImage.Contents!;

                    TestReadOnly(fs, "HELLO", "NO.SUCH.FILE", appHook);
                }
            }

            using (IFileSystem wfs = Helper.CreateTestImage(string.Empty, FileSystemType.DOS33,
                    35, 16, 254, true, appHook, out MemoryStream memFile)) {
                wfs.PrepareRawAccess();
                TestReadWrite(wfs, "TESTFILE", "OnlyInvalidIfTooLongForDOSDirectory", appHook);

                wfs.PrepareRawAccess();
                wfs.PrepareFileAccess(true);
                Helper.CheckNotes(wfs, 0, 0);
            }
        }

        public static void TestGutenberg(AppHook appHook) {
            using (Stream dataFile = Helper.OpenTestFile("gutenberg/gjr-data.do",
                    true, appHook)) {
                using (IDiskImage diskImage = FileAnalyzer.PrepareDiskImage(dataFile,
                        FileKind.UnadornedSector, appHook)!) {
                    diskImage.AnalyzeDisk();
                    IFileSystem fs = (IFileSystem)diskImage.Contents!;

                    TestReadOnly(fs, "DIR", "NO.SUCH.FILE", appHook);
                }
            }
        }

        public static void TestHFS(AppHook appHook) {
            using (Stream dataFile = Helper.OpenTestFile("hfs/simple-gsos.po", true, appHook)) {
                using (IDiskImage diskImage = FileAnalyzer.PrepareDiskImage(dataFile,
                        FileKind.UnadornedSector, appHook)!) {
                    diskImage.AnalyzeDisk();
                    IFileSystem fs = (IFileSystem)diskImage.Contents!;

                    TestReadOnly(fs, "Finder.Data", "NO.SUCH.FILE", appHook);
                    TestDirectoryReadOnly(fs, "SIZES");
                }
            }

            using (IFileSystem wfs = Helper.CreateTestImage("Testing", FileSystemType.HFS, 1600,
                    appHook, out MemoryStream memFile)) {
                wfs.PrepareRawAccess();
                TestReadWrite(wfs, "Finder.Data", "INVAL:D", appHook);

                wfs.PrepareRawAccess();
                wfs.PrepareFileAccess(true);
                Helper.CheckNotes(wfs, 0, 0);
            }
            using (IFileSystem wfs = Helper.CreateTestImage("Testing", FileSystemType.HFS, 1600,
                    appHook, out MemoryStream memFile)) {
                wfs.PrepareRawAccess();
                TestDirectoryReadWrite(wfs, appHook);

                wfs.PrepareRawAccess();
                wfs.PrepareFileAccess(true);
                Helper.CheckNotes(wfs, 0, 0);
            }
        }

        public static void TestMFS(AppHook appHook) {
            using (Stream dataFile = Helper.OpenTestFile("mfs/Workstation Installer.IMG",
                    true, appHook)) {
                using (IDiskImage diskImage = FileAnalyzer.PrepareDiskImage(dataFile,
                        FileKind.DiskCopy, appHook)!) {
                    diskImage.AnalyzeDisk();
                    IFileSystem fs = (IFileSystem)diskImage.Contents!;

                    TestReadOnly(fs, "Desktop", "NO.SUCH.FILE", appHook);
                }
            }
        }

        public static void TestPascal(AppHook appHook) {
            using (Stream dataFile = Helper.OpenTestFile("pascal/Apple Pascal1.po",
                    true, appHook)) {
                using (IDiskImage diskImage = FileAnalyzer.PrepareDiskImage(dataFile,
                        FileKind.UnadornedSector, appHook)!) {
                    diskImage.AnalyzeDisk();
                    IFileSystem fs = (IFileSystem)diskImage.Contents!;

                    TestReadOnly(fs, "SYSTEM.APPLE", "NO.SUCH.FILE", appHook);
                }
            }

            using (IFileSystem wfs = Helper.CreateTestImage("TESTVOL", FileSystemType.Pascal,
                    35, 16, 254, true, appHook, out MemoryStream memFile)) {
                wfs.PrepareRawAccess();
                TestReadWrite(wfs, "TESTFILE", "INVAL?ID", appHook);

                wfs.PrepareRawAccess();
                wfs.PrepareFileAccess(true);
                Helper.CheckNotes(wfs, 0, 0);
            }
        }

        public static void TestProDOS(AppHook appHook) {
            using (Stream dataFile = Helper.OpenTestFile("prodos/simple-sparse.po", true,
                    appHook)) {
                using (IDiskImage diskImage = FileAnalyzer.PrepareDiskImage(dataFile,
                        FileKind.UnadornedSector, appHook)!) {
                    diskImage.AnalyzeDisk();
                    IFileSystem fs = (IFileSystem)diskImage.Contents!;

                    TestReadOnly(fs, "PRODOS", "NO.SUCH.FILE", appHook);
                    TestDirectoryReadOnly(fs, "GEN");
                }
            }

            using (IFileSystem wfs = Helper.CreateTestImage("FSTEST", FileSystemType.ProDOS, 1600,
                    appHook, out MemoryStream memFile)) {
                wfs.PrepareRawAccess();
                TestReadWrite(wfs, "Finder.Data", "INVAL:D", appHook);

                wfs.PrepareRawAccess();
                wfs.PrepareFileAccess(true);
                Helper.CheckNotes(wfs, 0, 0);
            }
            using (IFileSystem wfs = Helper.CreateTestImage("FSTEST", FileSystemType.ProDOS, 1600,
                    appHook, out MemoryStream memFile)) {
                wfs.PrepareRawAccess();
                TestDirectoryReadWrite(wfs, appHook);

                wfs.PrepareRawAccess();
                wfs.PrepareFileAccess(true);
                Helper.CheckNotes(wfs, 0, 0);
            }
        }

        public static void TestRDOS(AppHook appHook) {
            using (Stream dataFile = Helper.OpenTestFile("rdos/rdos32-shiloh-save.woz",
                    true, appHook)) {
                using (IDiskImage diskImage = FileAnalyzer.PrepareDiskImage(dataFile,
                        FileKind.Woz, appHook)!) {
                    diskImage.AnalyzeDisk();
                    IFileSystem fs = (IFileSystem)diskImage.Contents!;

                    TestReadOnly(fs, "MYSAVE", "NO.SUCH.FILE", appHook);
                }
            }
        }

        #endregion FS Drivers

        #region Tests

        private static void TestReadOnly(IFileSystem fs, string goodFileName, string nonFileName,
                AppHook appHook) {
            // Create a different filesystem for cross-system checks.  FS type doesn't matter.
            IFileSystem otherFs = Helper.CreateTestImage("OTHERVOL", FileSystemType.ProDOS, 280,
                appHook, out MemoryStream otherStream);
            IFileEntry otherFile = otherFs.CreateFile(otherFs.GetVolDirEntry(), "OTHER",
                CreateMode.File);

            if (fs.RawAccess.AccessLevel != GatedChunkAccess.AccessLvl.Open) {
                throw new Exception("Initial chunk access level should be open");
            }
            fs.PrepareFileAccess(true);
            if (!fs.IsReadOnly) {
                throw new Exception("Read-only test needs read-only fs");
            }
            if (fs.IsDubious) {
                throw new Exception("Read-only test expects healthy image");
            }
            if (fs.RawAccess.AccessLevel != GatedChunkAccess.AccessLvl.ReadOnly) {
                throw new Exception("Chunk access should be read-only");
            }

            try {
                fs.Format("NAME", 0, true);
                throw new Exception("Formatted on RO");
            } catch (IOException) { /*expected*/ }

            IFileEntry volDir = fs.GetVolDirEntry();
            if (volDir == IFileEntry.NO_ENTRY) {
                throw new Exception("Should be a volume dir entry");
            }
            try {
                fs.OpenFile(volDir, FileAccessMode.ReadWrite, FilePart.DataFork);
                throw new Exception("Opened vol dir read-write");
            } catch (IOException) { /* expected*/ }
            if (volDir.ContainingDir != IFileEntry.NO_ENTRY) {
                throw new Exception("Volume dir must not have parent");
            }

            try {
                fs.CreateFile(volDir, "NAME", CreateMode.File);
                throw new Exception("Created on RO");
            } catch (IOException) { /*expected*/ }
            IFileEntry goodFile = fs.FindFileEntry(volDir, goodFileName);
            try {
                fs.AddRsrcFork(goodFile);
                throw new Exception("Added rsrc on RO");
            } catch (IOException) { /*expected*/ }
            try {
                fs.MoveFile(goodFile, volDir, "NAME2");
                throw new Exception("Moved on RO");
            } catch (IOException) { /*expected*/ }
            try {
                fs.DeleteFile(goodFile);
                throw new Exception("Deleted from RO");
            } catch (IOException) { /*expected*/ }

            using (fs.OpenFile(goodFile, FileAccessMode.ReadOnly, FilePart.DataFork)) {
                try {
                    fs.PrepareRawAccess();      // should fail when files are open
                    throw new Exception("FS closed w/open");
                } catch (DAException) { /*expected*/ }
            }
            try {
                fs.OpenFile(goodFile, FileAccessMode.ReadWrite, FilePart.DataFork);
                throw new Exception("Opened RW");
            } catch (IOException) { /*expected*/ }
            try {
                fs.OpenFile(goodFile, FileAccessMode.ReadOnly, FilePart.DiskImage);
                throw new Exception("Opened disk image");
            } catch (ArgumentException) { /*expected*/ }

            try {
                fs.FindFileEntry(volDir, nonFileName);
                throw new Exception("Opened nonexistent");
            } catch (FileNotFoundException) { /*expected*/ }

            fs.CloseAll();

            try {
                fs.OpenFile(otherFile, FileAccessMode.ReadOnly, FilePart.DataFork);
                throw new Exception("Opened other file");
            } catch (IOException) { /*expected*/ }
        }

        private static void TestReadWrite(IFileSystem fs, string fileName, string illegalFileName,
                AppHook appHook) {
            // Create a different filesystem for cross-system checks.  FS type doesn't matter.
            IFileSystem otherFs = Helper.CreateTestImage("OTHERVOL", FileSystemType.ProDOS, 280,
                appHook, out MemoryStream otherStream);
            IFileEntry otherFile = otherFs.CreateFile(otherFs.GetVolDirEntry(), "OTHER",
                CreateMode.File);

            if (fs.RawAccess.AccessLevel != GatedChunkAccess.AccessLvl.Open) {
                throw new Exception("Initial chunk access level should be open");
            }
            fs.PrepareFileAccess(true);
            if (fs.IsReadOnly) {
                throw new Exception("Read-write test needs read-write fs");
            }
            if (fs.IsDubious) {
                throw new Exception("Read-write test expects healthy image");
            }
            if (fs.RawAccess.AccessLevel != GatedChunkAccess.AccessLvl.ReadOnly) {
                throw new Exception("Chunk access should be read-only");
            }

            IFileEntry volDir = fs.GetVolDirEntry();
            IFileEntry newFile = fs.CreateFile(volDir, fileName, CreateMode.File);
            if (newFile.ContainingDir == IFileEntry.NO_ENTRY) {
                throw new Exception("file has no containing dir");
            }
            using (fs.OpenFile(newFile, FileAccessMode.ReadWrite, FilePart.DataFork)) {
                // just want to confirm that we can open read-write
            }
            using (fs.OpenFile(newFile, FileAccessMode.ReadOnly, FilePart.DataFork)) {
                try {
                    fs.DeleteFile(newFile);
                    throw new Exception("Deleted open file");
                } catch (IOException) { /*expected*/ }
            }
            try {
                fs.MoveFile(newFile, newFile.ContainingDir, illegalFileName);
                throw new Exception("Renamed file to illegal name");
            } catch (ArgumentException) { /*expected*/ }

            try {
                fs.CreateFile(volDir, illegalFileName, CreateMode.File);
                throw new Exception("File with illegal name created");
            } catch (ArgumentException) { /*expected*/ }
            try {
                fs.CreateFile(volDir, "NAME", CreateMode.Unknown);
                throw new Exception("File created with unknown mode");
            } catch (ArgumentException) { /*expected*/ }

            try {
                fs.AddRsrcFork(volDir);
                throw new Exception("Added rsrc to volume directory");
            } catch (IOException) { /*expected*/ }

            try {
                fs.MoveFile(otherFile, volDir, "NAME");
                throw new Exception("Moved file from other FS");
            } catch (IOException) { /*expected*/ }

            try {
                fs.OpenFile(otherFile, FileAccessMode.ReadOnly, FilePart.DataFork);
                throw new Exception("Opened file from other FS");
            } catch (IOException) { /*expected*/ }

            fs.DeleteFile(newFile);
            try {
                fs.DeleteFile(newFile);
                throw new Exception("Deleted file twice");
            } catch (IOException) { /*expected*/ }
            try {
                fs.DeleteFile(otherFile);
                throw new Exception("Deleted file from other FS");
            } catch (IOException) { /*expected*/ }
            try {
                fs.DeleteFile(volDir);
                throw new Exception("Deleted volume dir");
            } catch (IOException) { /*expected*/ }
            try {
                fs.OpenFile(newFile, FileAccessMode.ReadOnly, FilePart.DataFork);
                throw new Exception("Opened deleted file");
            } catch (IOException) { /*expected*/ }

            try {
                fs.Format("WHOOPS", 254, false);
                throw new Exception("Formatted filesystem in file-access mode");
            } catch (IOException) { /*expected*/ }

            ExerciseMultiOpen(fs);

            // Check a file entry that was scanned, rather than newly-created.
            fs.CreateFile(volDir, "CHECK", CreateMode.File);
            fs.PrepareRawAccess();
            fs.PrepareFileAccess(true);
            volDir = fs.GetVolDirEntry();
            IFileEntry checkFile = fs.FindFileEntry(volDir, "CHECK");
            if (checkFile.ContainingDir == IFileEntry.NO_ENTRY) {
                throw new Exception("Scanned file has no parent");
            }

            // This is a static method accessed via extension.  Confirm that it exists.
            fs.AdjustFileName("TEST");
        }

        private static void TestDirectoryReadOnly(IFileSystem fs, string dirName) {
            IFileEntry volDir = fs.GetVolDirEntry();
            IFileEntry subDir = fs.FindFileEntry(volDir, dirName);
            if (!subDir.IsDirectory || subDir.ContainingDir != volDir) {
                throw new Exception("Unexpected directory configuration");
            }
            try {
                fs.OpenFile(subDir, FileAccessMode.ReadWrite, FilePart.DataFork);
                throw new Exception("Opened subdir read-write");
            } catch (IOException) { /* expected*/ }
            try {
                fs.OpenFile(subDir, FileAccessMode.ReadOnly, FilePart.RsrcFork);
                throw new Exception("Opened subdir resource fork");
            } catch (IOException) { /* expected*/ }
        }

        private static void TestDirectoryReadWrite(IFileSystem fs, AppHook appHook) {
            // Create a different filesystem for cross-system checks.  FS type doesn't matter.
            IFileSystem otherFs = Helper.CreateTestImage("OTHERVOL", FileSystemType.ProDOS, 280,
                appHook, out MemoryStream otherStream);
            IFileEntry otherFile = otherFs.CreateFile(otherFs.GetVolDirEntry(), "OTHER",
                CreateMode.File);

            fs.PrepareFileAccess(true);
            ExerciseFileMoves(fs);

            IFileEntry volDir = fs.GetVolDirEntry();
            IFileEntry subDir = fs.CreateFile(volDir, "SUBDIR", CreateMode.Directory);
            IFileEntry subFile1 = fs.CreateFile(subDir, "SUBFILE1", CreateMode.File);
            IFileEntry subFile2 = fs.CreateFile(subDir, "SUBFILE2", CreateMode.File);
            IFileEntry subSubDir = fs.CreateFile(subDir, "SUBSUB", CreateMode.Directory);
            IFileEntry subSubDirD = fs.CreateFile(subDir, "SUBSUBD", CreateMode.Directory);
            fs.DeleteFile(subSubDirD);      // confirm can delete empty directory
            try {
                fs.DeleteFile(subDir);
                throw new Exception("Deleted non-empty directory");
            } catch (IOException) { /* expected*/ }

            try {
                fs.MoveFile(subDir, subSubDir, subDir.FileName);
                throw new Exception("Moved directory into child of itself");
            } catch (IOException) { /* expected*/ }
            try {
                fs.MoveFile(subDir, subFile1, subDir.FileName);
                throw new Exception("Moved file into non-directory");
            } catch (ArgumentException) { /* expected*/ }
            try {
                fs.MoveFile(volDir, subDir, "NEWNAME");
                throw new Exception("Moved volume directory");
            } catch (ArgumentException) { /* expected*/ }
            try {
                fs.MoveFile(subFile1, volDir.ContainingDir, "NEWNAME");
                throw new Exception("Moved file to volDir's parent");
            } catch (ArgumentException) { /* expected*/ }
            try {
                fs.MoveFile(otherFile, volDir, "NEWNAME");
                throw new Exception("Moved file from other FS");
            } catch (IOException) { /* expected*/ }
            try {
                fs.MoveFile(subFile1, subFile1.ContainingDir, subFile2.FileName);
                throw new Exception("Renamed file to name already in use");
            } catch (IOException) { /* expected*/ }
        }

        // Exercises file movement on hierarchical filesystems.  Start with blank FS.
        private static void ExerciseFileMoves(IFileSystem fs) {
            // Start by creating some files:
            //  ROOT.FILE
            //  DIR1
            //    FILE1
            //  DIR2
            //    FILE2
            //    DIR3
            //      FILE3
            IFileEntry volDir = fs.GetVolDirEntry();
            IFileEntry rootFile = fs.CreateFile(volDir, "ROOT.FILE", CreateMode.File);
            IFileEntry dir1 = fs.CreateFile(volDir, "DIR1", CreateMode.Directory);
            IFileEntry dir2 = fs.CreateFile(volDir, "DIR2", CreateMode.Directory);
            IFileEntry dir3 = fs.CreateFile(dir2, "DIR3", CreateMode.Directory);
            IFileEntry file1 = fs.CreateFile(dir1, "FILE1", CreateMode.File);
            IFileEntry file2 = fs.CreateFile(dir2, "FILE2", CreateMode.File);
            IFileEntry file3 = fs.CreateFile(dir3, "FILE3", CreateMode.File);

            // Rearrange it.  We want to move a file and a directory to/from the root dir, and
            // to/from an arbitrary dir.  Rename files and directories, and cause valence changes.
            // Rename in place and while moving.
            fs.MoveFile(rootFile, dir3, "NOT.ROOT.FILE");       // file from root to dir
            fs.MoveFile(file3, volDir, "NEW.ROOT.FILE");        // file from dir to root
            fs.MoveFile(dir3, volDir, dir3.FileName);           // dir from dir to root
            fs.MoveFile(dir1, dir2, dir1.FileName);             // dir from root to dir
            fs.MoveFile(file2, dir1, file2.FileName);           // file from dir to dir
            fs.MoveFile(dir1, dir3, "NEW.DIR1");                // dir from dir to dir
            fs.MoveFile(rootFile, rootFile.ContainingDir, "NOT.ROOT.FILE1");

            // Should now be:
            //  NEW.ROOT.FILE (was FILE3)
            //  DIR2
            //  DIR3
            //    NOT.ROOT.FILE1 (was ROOT.FILE and NOT.ROOT.FILE)
            //    NEW.DIR1 (was DIR1)
            //      FILE1
            //      FILE2
            if (rootFile.ContainingDir != dir3 ||
                    dir1.ContainingDir != dir3 ||
                    dir2.ContainingDir != volDir ||
                    dir3.ContainingDir != volDir ||
                    file1.ContainingDir != dir1 ||
                    file2.ContainingDir != dir1 ||
                    file3.ContainingDir != volDir) {
                throw new Exception("Moves didn't work right");
            }
            if (dir1.FileName != "NEW.DIR1" || rootFile.FileName != "NOT.ROOT.FILE1" ||
                    file3.FileName != "NEW.ROOT.FILE") {
                throw new Exception("Renames didn't work right");
            }

            // Create a second file called "FILE1" in DIR2, and a file called "FILE1.COPY" in DIR3.
            // Then move the existing "FILE1" from DIR3 to DIR2, renaming it to "FILE1.COPY" as
            // part of the move.  We can't rename it before, or move then rename it after,
            // because it will clash.
            IFileEntry dupFile1 = fs.CreateFile(dir2, "FILE1", CreateMode.File);
            IFileEntry file1Copy1 = fs.CreateFile(file1.ContainingDir, "FILE1.COPY",
                    CreateMode.File);
            fs.MoveFile(file1, dir2, "FILE1.COPY");

            // Use MoveFile() to do a simple rename.
            Debug.Assert(file3.ContainingDir == volDir);
            fs.MoveFile(file3, volDir, "BEST.ROOT.FILE");

            // Should now be:
            //  NEW.ROOT.FILE (was FILE3)
            //  DIR2
            //    FILE1 (new)
            //    FILE1.COPY (was FILE1)
            //  DIR3
            //    NOT.ROOT.FILE1 (was ROOT.FILE and NOT.ROOT.FILE)
            //    NEW.DIR1 (was DIR1)
            //      FILE1.COPY (new)
            //      FILE2

            if (dupFile1.ContainingDir != dir2 ||
                    file1.ContainingDir != dir2 ||
                    file1Copy1.ContainingDir != dir1 ||
                    file1.FileName != "FILE1.COPY" ||
                    file3.ContainingDir != volDir ||
                    file3.FileName != "BEST.ROOT.FILE") {
                throw new Exception("Move+rename failed");
            }

            if (DUMP_TO_FILE) {
                CopyToFile(fs, "TEST-file-moves");
            }
        }

        // Tests permutations of opening files more than once.
        private static void ExerciseMultiOpen(IFileSystem fs) {
            bool hasRsrc = fs.Characteristics.HasResourceForks;
            // These work on every filesystem we support.
            const string NAME1 = "FILE1";
            const string NAME2 = "FILE2";

            // Create files.  Create extended files if possible.
            IFileEntry volDir = fs.GetVolDirEntry();
            CreateMode createMode = hasRsrc ? CreateMode.Extended : CreateMode.File;
            IFileEntry file1 = fs.CreateFile(volDir, NAME1, createMode);
            IFileEntry file2 = fs.CreateFile(volDir, NAME2, createMode);

            // Open file1 data fork read-only.
            using (fs.OpenFile(file1, FileAccessMode.ReadOnly, FilePart.DataFork)) {
                // Open file2 data fork read-write.  Should work.
                using (fs.OpenFile(file2, FileAccessMode.ReadWrite, FilePart.DataFork)) { }

                if (hasRsrc) {
                    // Open file1 rsrc fork read-write.  Should work.
                    using (fs.OpenFile(file1, FileAccessMode.ReadWrite, FilePart.RsrcFork)) { }
                }

                // Open file1 data fork read-only for a second time.  Should work.
                using (fs.OpenFile(file1, FileAccessMode.ReadOnly, FilePart.DataFork)) { }
                // Repeat, but with "raw data" part (should be equivalent to data fork).
                using (fs.OpenFile(file1, FileAccessMode.ReadOnly, FilePart.RawData)) { }

                // Open file1 data fork read-write; should fail.
                try {
                    fs.OpenFile(file1, FileAccessMode.ReadWrite, FilePart.DataFork);
                    throw new Exception("file1 opened ro and rw");
                } catch (IOException) { /*expected*/ }
                // Open read-write using "raw data" part.
                try {
                    fs.OpenFile(file1, FileAccessMode.ReadWrite, FilePart.RawData);
                    throw new Exception("file1 opened ro and rw (raw)");
                } catch (IOException) { /*expected*/ }
            }

            // Open file1 data fork read-write.  (Will fail if "using" isn't closing previous.)
            using (fs.OpenFile(file1, FileAccessMode.ReadWrite, FilePart.DataFork)) {
                // Open file2 data fork read-write.  Should work.
                using (fs.OpenFile(file2, FileAccessMode.ReadWrite, FilePart.DataFork)) { }

                if (hasRsrc) {
                    // Open file1 rsrc fork read-write.  Should work.
                    using (fs.OpenFile(file1, FileAccessMode.ReadWrite, FilePart.RsrcFork)) { }
                }

                // Open file1 data fork read-only, should fail.
                try {
                    fs.OpenFile(file1, FileAccessMode.ReadOnly, FilePart.DataFork);
                    throw new Exception("Re-opened rw file");
                } catch (IOException) { /*expected*/ }
                // Repeat, but with "raw data" part.
                try {
                    fs.OpenFile(file1, FileAccessMode.ReadOnly, FilePart.RawData);
                    throw new Exception("Re-opened rw file (raw)");
                } catch (IOException) { /*expected*/ }

                // Open file1 data fork read-write; should fail.
                try {
                    fs.OpenFile(file1, FileAccessMode.ReadWrite, FilePart.DataFork);
                    throw new Exception("file1 opened ro and rw");
                } catch (IOException) { /*expected*/ }
            }
        }

        #endregion Tests

        #region Utilities

        public static void CopyToFile(IFileSystem fs, string fileName) {
            fs.DumpToFile(Helper.DebugFileDir + fileName + "_" + fs.GetType().Name + ".po");
        }

        #endregion Utilities
    }
}
