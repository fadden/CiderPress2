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
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

using CommonUtil;
using DiskArc;
using DiskArc.FS;
using static DiskArc.Defs;
using static DiskArc.IDiskImage;
using static DiskArc.IFileSystem;

namespace DiskArcTests {
    /// <summary>
    /// Test creation and deletion of files and directories.
    /// </summary>
    /// <remarks>
    /// General test plan:
    /// <list type="bullet">
    /// <item>Create empty volume, and format it.</item>
    /// <item>Create various files and directories.</item>
    /// <item>Verify contents.</item>
    /// <item>Close filesystem, reopen image.</item>
    /// <item>Verify file structure (via initial scan).</item>
    /// </list>
    /// </remarks>
    public class TestProDOS_Creation : ITest {
        private static bool DO_SCAN = true;

        public static void TestCreateVol(AppHook appHook) {
            try {
                Make525Floppy("!BadVolName!", appHook);
                throw new Exception("Formatted with bad name");
            } catch (ArgumentException) {
                // expected
            }

            string goodName = "Good.Vol.Name";
            using (IFileSystem fs = Make525Floppy(goodName, appHook)) {
                if (fs.Notes.WarningCount != 0 || fs.Notes.ErrorCount != 0) {
                    Debug.WriteLine("NOTES:");
                    Debug.WriteLine(fs.Notes.ToString());
                    throw new Exception("Formatted image isn't clean");
                }
                IFileEntry volDir = fs.GetVolDirEntry();
                if (volDir.FileName != goodName) {
                    throw new Exception("Volume name is wrong");
                }
            }

            using (IFileSystem fs = Make525Floppy("NEW.DISK", appHook)) {
                // try to format while in file-access mode
                try {
                    fs.Format("new", 0, false);
                    throw new Exception("Allowed to format while in file-access mode");
                } catch (IOException) { /*expected */ }
            }
        }

        // Test a ProDOS filesystem in a DOS-ordered disk image.
        public static void TestSwapped(AppHook hook) {
            uint size = 35 * 16 * SECTOR_SIZE;      // 140KB floppy
            Helper.CreateParams parms = new Helper.CreateParams() {
                FSType = FileSystemType.ProDOS,
                Size = size,
                VolumeNum = 254,
                VolumeName = "Order.Swap",
                SectorsPerTrack = 16,
                MakeBootable = false,
                FileOrder = SectorOrder.DOS_Sector,     // use sector order for blocks
            };
            byte[] buffer = new byte[size];
            MemoryStream memStream = new MemoryStream(buffer, 0, (int)size);
            IFileSystem fs = Helper.CreateNewFS(memStream, parms, hook);
            fs.PrepareFileAccess(true);     // verify working

            // Put a reasonably large file on it, to exercise the chunk accessor's behavior
            // for blocks-on-sectors.
            IFileEntry volDir = fs.GetVolDirEntry();
            IFileEntry newEntry = fs.CreateFile(volDir, "STUFF", CreateMode.File);
            using (DiskFileStream stuff = fs.OpenFile(newEntry,
                    FileAccessMode.ReadWrite, FilePart.DataFork)) {
                stuff.Write(Patterns.sRunPattern, 0, Patterns.sRunPattern.Length);
            }
            fs.Dispose();

            // Now open it with the wrong order, which the analyzer should correct.
            using IDiskImage? diskImage = FileAnalyzer.PrepareDiskImage(memStream,
                FileKind.UnadornedSector, hook);
            if (diskImage == null || !diskImage.AnalyzeDisk(null,
                    SectorOrder.ProDOS_Block, AnalysisDepth.Full)) {
                throw new Exception("Failed to open image created previously");
            }
            fs = (IFileSystem)diskImage.Contents!;
            if (fs.RawAccess.FileOrder != SectorOrder.DOS_Sector) {
                throw new Exception("Disk image wasn't created with correct order");
            }

            diskImage.AnalyzeDisk();
            fs = (IFileSystem)diskImage.Contents!;
            fs.PrepareFileAccess(true);
            volDir = fs.GetVolDirEntry();
            newEntry = fs.FindFileEntry(volDir, "STUFF");
            using (DiskFileStream stuff = fs.OpenFile(newEntry, FileAccessMode.ReadOnly,
                    FilePart.DataFork)) {
                byte[] buf = new byte[Patterns.sRunPattern.Length];
                stuff.Read(buf, 0, buf.Length);
                if (!RawData.CompareBytes(buf, Patterns.sRunPattern, Patterns.sRunPattern.Length)) {
                    throw new Exception("data mismatch");
                }
            }
        }

        public static void TestCreateFile(AppHook appHook) {
            List<Helper.FileAttr> expected = new List<Helper.FileAttr>() {
                new Helper.FileAttr("Plain", 0, -1, 1 * BLOCK_SIZE, 0x00, 0x0000),
                new Helper.FileAttr("Ext", 0, 0, 3 * BLOCK_SIZE, 0x00, 0x0000),
            };

            using (IFileSystem fs = Make525Floppy("Create.Test", appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();

                IFileEntry plain = fs.CreateFile(volDir, "Plain", CreateMode.File);
                IFileEntry ext = fs.CreateFile(volDir, "Ext", CreateMode.Extended);

                Helper.ValidateDirContents(volDir, expected);

                try {
                    IFileEntry fail = fs.CreateFile(volDir, "OneTooLong123456",
                        CreateMode.File);
                    throw new Exception("bad filename worked #1");
                } catch (ArgumentException) {
                    // expected
                }
                try {
                    IFileEntry fail = fs.CreateFile(volDir, "1NumberStart",
                        CreateMode.File);
                    throw new Exception("bad filename worked #2");
                } catch (ArgumentException) {
                    // expected
                }
            }
        }

        public static void TestVolDir(AppHook appHook) {
            // Overflow the volume directory.
            using (IFileSystem fs = Make525Floppy("Vol.Dir.Test", appHook)) {
                List<Helper.FileAttr> attrs = new List<Helper.FileAttr>();
                IFileEntry volDir = fs.GetVolDirEntry();

                // Vol dir should hold 51 files.
                for (int i = 1; i <= 51; i++) {
                    string name = "FOO" + i;
                    fs.CreateFile(volDir, name, CreateMode.File);
                    attrs.Add(new Helper.FileAttr(name, 0, 1 * BLOCK_SIZE));
                }

                try {
                    // 52nd should break it.
                    fs.CreateFile(volDir, "FULL", CreateMode.File);
                    throw new Exception("Vol dir accepted 52 files");
                } catch (DiskArc.DiskFullException) {
                    // expected
                }

                // See if the managed list matches.
                Helper.ValidateDirContents(volDir, attrs);

                fs.PrepareRawAccess();
                fs.PrepareFileAccess(DO_SCAN);
                Helper.CheckNotes(fs, 0, 0);

                // See if it still matches after being reloaded.
                volDir = fs.GetVolDirEntry();
                Helper.ValidateDirContents(volDir, attrs);
            }
        }

        public static void TestCreateDir(AppHook appHook) {
            using (IFileSystem fs = Make525Floppy("Dir.Test", appHook)) {
                List<Helper.FileAttr> attrs = new List<Helper.FileAttr>();
                IFileEntry volDir = fs.GetVolDirEntry();
                IFileEntry subDir = fs.CreateFile(volDir, "Sub.Dir", CreateMode.Directory);

                // First block of the directory holds header + 12 entries.
                int num;
                string name = string.Empty;
                for (num = 1; num <= 12; num++) {
                    name = "FOO" + num;
                    fs.CreateFile(subDir, name, CreateMode.File);
                    attrs.Add(new Helper.FileAttr(name, 0, 1 * BLOCK_SIZE));
                }
                Helper.ExpectLong(1 * BLOCK_SIZE, subDir.StorageSize, "subdir storage 1");

                try {
                    fs.CreateFile(subDir, name, CreateMode.File);
                    throw new Exception("Should not have accepted duplicate name");
                } catch (IOException) {
                    // expected
                }

                // Next entry should expand the dir.
                name = "FOO" + num++;
                fs.CreateFile(subDir, name, CreateMode.File);
                attrs.Add(new Helper.FileAttr(name, 0, 1 * BLOCK_SIZE));
                Helper.ExpectLong(2 * BLOCK_SIZE, subDir.StorageSize, "subdir storage 2");

                Helper.ValidateDirContents(subDir, attrs);

                // Confirm all is well.
                fs.PrepareRawAccess();
                fs.PrepareFileAccess(DO_SCAN);
                Helper.CheckNotes(fs, 0, 0);

                // Refresh file entries, recheck contents.
                volDir = fs.GetVolDirEntry();
                subDir = fs.FindFileEntry(volDir, "Sub.Dir");
                Helper.ValidateDirContents(subDir, attrs);
            }
        }

        public static void TestDelDir(AppHook appHook) {
            using (IFileSystem fs = Make525Floppy("Dir.Test", appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();
                IFileEntry subDir = fs.CreateFile(volDir, "SubDir",
                    IFileSystem.CreateMode.Directory);

                IFileEntry file1 = fs.CreateFile(subDir, "File1", CreateMode.File);
                IFileEntry file2 = fs.CreateFile(subDir, "File2", CreateMode.File);
                try {
                    fs.DeleteFile(subDir);
                    throw new Exception("Allowed to delete non-empty directory #1");
                } catch (IOException) {
                    // expected
                }

                fs.DeleteFile(file1);
                try {
                    fs.DeleteFile(subDir);
                    throw new Exception("Allowed to delete non-empty directory #2");
                } catch (IOException) {
                    // expected
                }
                try {
                    fs.OpenFile(file1, FileAccessMode.ReadOnly, FilePart.DataFork);
                    throw new Exception("Successfully opened deleted file");
                } catch (IOException) {
                    // expected
                }

                fs.DeleteFile(file2);
                fs.DeleteFile(subDir);

                try {
                    fs.DeleteFile(volDir);
                    throw new Exception("Allowed to delete volume dir");
                } catch (IOException) {
                    // expected
                }

                // Confirm all is well.
                fs.PrepareRawAccess();
                fs.PrepareFileAccess(DO_SCAN);
                Helper.CheckNotes(fs, 0, 0);
            }
        }

        public static void TestDirSpam(AppHook appHook) {
            using (IFileSystem fs = Make525Floppy("Dir.Test", appHook)) {
                List<Helper.FileAttr> attrs = new List<Helper.FileAttr>();
                IFileEntry volDir = fs.GetVolDirEntry();
                IFileEntry subDir = fs.CreateFile(volDir, "SubDir", CreateMode.Directory);

                int count = 0;
                try {
                    while (true) {
                        fs.CreateFile(subDir, "File" + count, IFileSystem.CreateMode.Directory);
                        count++;
                    }
                } catch (DiskFullException) {
                    // expected
                }

                // 280 blocks, but 2+4+1=7 blocks of system overhead leaves 273 free.
                // Each new entry requires 1/13th of a directory block plus one key block.
                // 20 blocks of directories hold (20*13-1)=259 files.  We should have room
                // for (273-20)=253 subdirectories.
                if (count != 253) {
                    throw new Exception("Failed to create 253 subdirs (" + count + ")");
                }

                // Confirm all is well.
                fs.PrepareRawAccess();
                fs.PrepareFileAccess(DO_SCAN);
                Helper.CheckNotes(fs, 0, 0);

                volDir = fs.GetVolDirEntry();
                subDir = fs.FindFileEntry(volDir, "SubDir");
                while (--count >= 0) {
                    IFileEntry entry = fs.FindFileEntry(subDir, "File" + count);
                    fs.DeleteFile(entry);
                }

                // All files should be gone, but directory should not have shrunk.
                List<IFileEntry> children = subDir.ToList();
                if (children.Count != 0 || subDir.StorageSize != 20 * BLOCK_SIZE) {
                    throw new Exception("Unexpected count/size: " +
                        children.Count + "/" + subDir.StorageSize);
                }

                // Confirm all is well.
                fs.PrepareRawAccess();
                fs.PrepareFileAccess(DO_SCAN);
                Helper.CheckNotes(fs, 0, 0);
            }
        }

        public static void TestCreateDelete(AppHook appHook) {
            using (IFileSystem fs = Make525Floppy("CD.Test", appHook)) {
                List<Helper.FileAttr> attrs = new List<Helper.FileAttr>();
                IFileEntry volDir = fs.GetVolDirEntry();

                IFileEntry file1 = fs.CreateFile(volDir, "File1", CreateMode.File);
                fs.CreateFile(volDir, "File2", CreateMode.File);
                fs.DeleteFile(file1);
                attrs.Add(new Helper.FileAttr("File2", 0, 1 * BLOCK_SIZE));

                // Confirm all is well.
                fs.PrepareRawAccess();
                fs.PrepareFileAccess(DO_SCAN);
                Helper.CheckNotes(fs, 0, 0);
                volDir = fs.GetVolDirEntry();

                // Confirm the vol dir contents.
                Helper.ValidateDirContents(volDir, attrs);


                attrs.Clear();
                const string TEST_DIR_NAME = "TEST.DIR";
                IFileEntry subDir = fs.CreateFile(volDir, TEST_DIR_NAME, CreateMode.Directory);

                for (int num = 0; num < 20; num++) {
                    string name = "FIRST" + num;
                    fs.CreateFile(subDir, name, CreateMode.File);
                    attrs.Add(new Helper.FileAttr(name, 0, 1 * BLOCK_SIZE));
                }
                for (int num = 20; num < 30; num++) {
                    string name = "FIRST.R" + num;
                    fs.CreateFile(subDir, name, CreateMode.Extended);
                    attrs.Add(new Helper.FileAttr(name, 0, 0, 3 * BLOCK_SIZE, 0x00, 0x0000));
                }
                Helper.ValidateDirContents(subDir, attrs);

                // delete 5, 13, 21, 29
                for (int num = 5; num < 30; num += 8) {
                    IFileEntry delFile = fs.FindFileEntry(subDir, attrs[num].FileName);
                    fs.DeleteFile(delFile);
                }

                // Confirm all is well.
                fs.PrepareRawAccess();
                fs.PrepareFileAccess(DO_SCAN);
                Helper.CheckNotes(fs, 0, 0);

                // Get these back.
                volDir = fs.GetVolDirEntry();
                subDir = fs.FindFileEntry(volDir, TEST_DIR_NAME);

                // Add four files.  They should go into the recently-deleted slots.
                fs.CreateFile(subDir, "NEW100", CreateMode.File);
                attrs[5] = new Helper.FileAttr("NEW100", 0, 1 * BLOCK_SIZE);
                fs.CreateFile(subDir, "NEW101", CreateMode.File);
                attrs[13] = new Helper.FileAttr("NEW101", 0, 1 * BLOCK_SIZE);
                fs.CreateFile(subDir, "NEW102", CreateMode.File);
                attrs[21] = new Helper.FileAttr("NEW102", 0, 1 * BLOCK_SIZE);
                fs.CreateFile(subDir, "NEW103", CreateMode.File);
                attrs[29] = new Helper.FileAttr("NEW103", 0, 1 * BLOCK_SIZE);

                Helper.ValidateDirContents(subDir, attrs);
            }
        }

        private static DateTime TEST_DATE1 = new DateTime(1977, 6, 1);
        private static DateTime TEST_DATE2 = new DateTime(1986, 9, 15);

        public static void TestAttribs(AppHook appHook) {
            using (IFileSystem fs = Make525Floppy("AttribTest", appHook)) {
                const string SUBDIR_NAME = "Sub.Dir";
                const string NEW_NAME = "New.Name";

                IFileEntry volDir = fs.GetVolDirEntry();

                IFileEntry subDir = fs.CreateFile(volDir, SUBDIR_NAME, CreateMode.Directory);
                IFileEntry file = fs.CreateFile(subDir, "FILE", CreateMode.File);


                file.FileName = NEW_NAME;
                file.FileType = FileAttribs.FILE_TYPE_TXT;
                file.AuxType = 0x1234;
                file.Access = 0x21;
                file.CreateWhen = TEST_DATE1;
                file.ModWhen = TEST_DATE2;

                // Verify name change with a case-sensitive comparison.
                if (file.FileName != NEW_NAME ||
                        file.FileType != FileAttribs.FILE_TYPE_TXT ||
                        file.AuxType != 0x1234 ||
                        file.Access != 0x21 ||
                        file.CreateWhen != TEST_DATE1 ||
                        file.ModWhen != TEST_DATE2) {
                    throw new Exception("Not all changes were saved - file1");
                }

                // Flush to disk, then bounce the filesystem.
                file.SaveChanges();

                fs.PrepareRawAccess();
                fs.PrepareFileAccess(DO_SCAN);
                Helper.CheckNotes(fs, 0, 0);

                volDir = fs.GetVolDirEntry();
                subDir = fs.FindFileEntry(volDir, SUBDIR_NAME);
                file = fs.FindFileEntry(subDir, NEW_NAME);

                if (file.FileName != NEW_NAME ||
                        file.FileType != FileAttribs.FILE_TYPE_TXT ||
                        file.AuxType != 0x1234 ||
                        file.Access != 0x21 ||
                        file.CreateWhen != TEST_DATE1 ||
                        file.ModWhen != TEST_DATE2) {
                    throw new Exception("Not all changes were saved - file2");
                }

                // Now do the same thing with a subdirectory.  Some of the fields should be
                // impossible to set.  While we're at it, rename the volume.

                const string NEW_DIR_NAME = "New.Sub.Dir";
                subDir.FileName = NEW_DIR_NAME;
                subDir.FileType = FileAttribs.FILE_TYPE_TXT;    // should be ignored
                subDir.AuxType = 0x1234;                        // should be ignored
                subDir.Access = 0x21;
                subDir.CreateWhen = TEST_DATE2;
                subDir.ModWhen = TEST_DATE1;

                if (subDir.FileName != NEW_DIR_NAME ||
                        subDir.FileType != FileAttribs.FILE_TYPE_DIR ||
                        subDir.AuxType != 0x0000 ||
                        subDir.Access != 0x21 ||
                        subDir.CreateWhen != TEST_DATE2 ||
                        subDir.ModWhen != TEST_DATE1) {
                    throw new Exception("Not all changes were saved - dir1");
                }
                subDir.SaveChanges();

                if (volDir.HasHFSTypes || subDir.HasHFSTypes || file.HasHFSTypes) {
                    throw new Exception("Should not have HFS types");
                }

                fs.PrepareRawAccess();
                fs.PrepareFileAccess(DO_SCAN);
                Helper.CheckNotes(fs, 0, 0);

                volDir = fs.GetVolDirEntry();
                subDir = fs.FindFileEntry(volDir, NEW_DIR_NAME);

                if (subDir.FileName != NEW_DIR_NAME ||
                        subDir.FileType != FileAttribs.FILE_TYPE_DIR ||
                        subDir.AuxType != 0x0000 ||
                        subDir.Access != 0x21 ||
                        subDir.CreateWhen != TEST_DATE2 ||
                        subDir.ModWhen != TEST_DATE1) {
                    throw new Exception("Not all changes were saved - dir2");
                }

                if (subDir.FileName.Length != subDir.RawFileName.Length) {
                    throw new Exception("Raw filename length disagreement #1");
                }

                subDir.RawFileName = new byte[] { (byte)'R', (byte)'a', (byte)'w' };
                subDir.SaveChanges();
                if (subDir.FileName != "Raw") {
                    throw new Exception("Raw filename change failed");
                }
                if (subDir.FileName.Length != subDir.RawFileName.Length) {
                    throw new Exception("Raw filename length disagreement #2");
                }
                try {
                    subDir.RawFileName = new byte[] { (byte)'1' };
                    throw new Exception("Raw interface accepted illegal name #1");
                } catch (ArgumentException) {
                    // expected
                }
                try {
                    subDir.RawFileName = new byte[] { (byte)'A', (byte)' ' };
                    throw new Exception("Raw interface accepted illegal name #2");
                } catch (ArgumentException) {
                    // expected
                }

                // Try to create/rename a file to have a duplicate name.
                IFileEntry file1 = fs.CreateFile(subDir, "FILE1", CreateMode.File);
                try {
                    fs.CreateFile(subDir, "FILE1", CreateMode.File);
                    throw new Exception("Allowed to create duplicate file");
                } catch (IOException) {
                    // expected
                }
                IFileEntry file2 = fs.CreateFile(subDir, "FILE2", CreateMode.File);
                try {
                    file2.FileName = "FILE1";
                    throw new Exception("Allowed to rename duplicate file");
                } catch (IOException) {
                    // expected
                }

                // Try setting it equal to itself.
                file2.FileName = "FILE2";
                file2.SaveChanges();

                // Try renaming them both to a third name without saving the changes first.
                file2.FileName = "FILE3";
                try {
                    file1.FileName = "FILE3";
                    throw new Exception("Double rename succeeded");
                } catch (IOException) {
                    // expected
                }

                file1.SaveChanges();
                file2.SaveChanges();
            }
        }

        public static void TestVolDirAttribs(AppHook appHook) {
            using (IFileSystem fs = Make525Floppy("AttribTest", appHook)) {
                string NEW_VOL_NAME = "New.Vol";
                IFileEntry volDir = fs.GetVolDirEntry();
                volDir.FileName = NEW_VOL_NAME;
                volDir.Access = 0x11;
                volDir.CreateWhen = TEST_DATE1;
                volDir.ModWhen = TEST_DATE2;
                volDir.FileType = FileAttribs.FILE_TYPE_TXT;
                volDir.AuxType = 0x1234;

                if (volDir.FileName != NEW_VOL_NAME ||
                        volDir.Access != 0x11 ||
                        volDir.CreateWhen != TEST_DATE1 ||
                        volDir.ModWhen != TEST_DATE2) {
                    throw new Exception("Volume dir changes weren't saved #1");
                }
                if (volDir.FileType != FileAttribs.FILE_TYPE_DIR ||
                        volDir.AuxType != 0x0000) {
                    throw new Exception("Volume dir accepted bad changes");
                }

                volDir.SaveChanges();
                fs.PrepareRawAccess();
                fs.PrepareFileAccess(DO_SCAN);
                Helper.CheckNotes(fs, 0, 0);

                volDir = fs.GetVolDirEntry();
                if (volDir.FileName != NEW_VOL_NAME ||
                        volDir.Access != 0x11 ||
                        volDir.CreateWhen != TEST_DATE1 ||
                        volDir.ModWhen != TEST_DATE2) {
                    throw new Exception("Volume dir changes weren't saved #2");
                }
            }
        }

        public static void TestHFSAttribs(AppHook appHook) {
            const string FILENAME = "HFS.Types";
            const string FILENAME2 = "Types.HFS";

            using (IFileSystem fs = Make525Floppy("HFSAttribTest", appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();
                IFileEntry extFile = fs.CreateFile(volDir, FILENAME, CreateMode.Extended);

                if (!extFile.HasHFSTypes) {
                    throw new Exception("HFS types not supported");
                }

                extFile.HFSCreator = 0x88776655;
                extFile.HFSFileType = 0x11223344;

                if (extFile.HFSCreator != 0x88776655 ||
                        extFile.HFSFileType != 0x11223344) {
                    throw new Exception("Failed to store HFS types #1");
                }

                extFile.SaveChanges();
                fs.PrepareRawAccess();
                fs.PrepareFileAccess(DO_SCAN);
                Helper.CheckNotes(fs, 0, 0);

                volDir = fs.GetVolDirEntry();
                extFile = fs.FindFileEntry(volDir, FILENAME);

                if (extFile.HFSCreator != 0x88776655 ||
                        extFile.HFSFileType != 0x11223344) {
                    throw new Exception("Failed to store HFS types #2");
                }


                IFileEntry file2 = fs.CreateFile(volDir, FILENAME2,
                    IFileSystem.CreateMode.File);
                try {
                    fs.OpenFile(file2, FileAccessMode.ReadWrite, FilePart.RsrcFork);
                    throw new Exception("Opened resource fork of non-extended file");
                } catch (IOException) {
                    // expected
                }

                // Create a sapling data fork.
                using (DiskFileStream fd2 = fs.OpenFile(file2, FileAccessMode.ReadWrite,
                        FilePart.DataFork)) {
                    byte[] oneOne = new byte[] { 1 };
                    for (int i = 0; i < 513; i++) {
                        fd2.Write(oneOne, 0, 1);
                    }
                }

                if (file2.HasHFSTypes) {
                    throw new Exception("Should not have HFS type storage");
                }

                // Add a resource fork.
                fs.AddRsrcFork(file2);

                if (!file2.HasHFSTypes) {
                    throw new Exception("Should have HFS type storage");
                }

                // Make sure the data fork is still there.
                using (DiskFileStream fd2 = fs.OpenFile(file2, FileAccessMode.ReadOnly,
                        FilePart.DataFork)) {
                    byte[] oneBuf = new byte[1];
                    for (int i = 0; i < 513; i++) {
                        int actual = fd2.Read(oneBuf, 0, 1);
                        if (actual != 1 || oneBuf[0] != 1) {
                            throw new Exception("Ran out of ones at " + i +
                                " (actual=" + actual + ")");
                        }
                    }
                }

                // Confirm that the resource fork is empty.
                using (DiskFileStream fd2 = fs.OpenFile(file2, FileAccessMode.ReadOnly,
                        FilePart.RsrcFork)) {
                    byte[] noBuf = new byte[1];
                    int actual = fd2.Read(noBuf, 0, 1);
                    if (actual != 0) {
                        throw new Exception("Read data from empty resource fork");
                    }
                }

                // Verify the filesystem.
                fs.PrepareRawAccess();
                fs.PrepareFileAccess(DO_SCAN);
                Helper.CheckNotes(fs, 0, 0);
            }
        }

        public static void TestNoScan(AppHook appHook) {
            const string SUBDIR = "SubDir";
            const string LAST_FILE = "LastFile";

            using (IFileSystem fs = Make525Floppy("NoScanCheck", appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();
                fs.CreateFile(volDir, "File1", CreateMode.Extended);
                fs.CreateFile(volDir, SUBDIR, CreateMode.Directory);

                fs.PrepareRawAccess();
                fs.PrepareFileAccess(false);
                Helper.CheckNotes(fs, 0, 0);

                volDir = fs.GetVolDirEntry();
                fs.CreateFile(volDir, "File2", CreateMode.Extended);
                IFileEntry subDir = fs.FindFileEntry(volDir, SUBDIR);
                fs.CreateFile(subDir, LAST_FILE, CreateMode.Extended);

                fs.PrepareRawAccess();
                fs.PrepareFileAccess(false);
                Helper.CheckNotes(fs, 0, 0);

                volDir = fs.GetVolDirEntry();
                subDir = fs.FindFileEntry(volDir, SUBDIR);

                List<Helper.FileAttr> attrs = new List<Helper.FileAttr>();
                attrs.Add(new Helper.FileAttr(LAST_FILE, 0, 3 * BLOCK_SIZE));
                Helper.ValidateDirContents(subDir, attrs);
            }
        }

        public static IFileSystem Make525Floppy(string volName, AppHook appHook) {
            return Helper.CreateTestImage(volName, FileSystemType.ProDOS, 280, appHook,
                out MemoryStream memFile);
        }
    }
}
