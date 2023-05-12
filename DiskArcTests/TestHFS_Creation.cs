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

using CommonUtil;
using DiskArc;
using static DiskArc.Defs;
using static DiskArc.IFileSystem;

namespace DiskArcTests {
    /// <summary>
    /// Test creation and deletion of files and directories.
    /// </summary>
    public class TestHFS_Creation : ITest {
        private static bool DUMP_TO_FILE = false;
        private static bool DO_SCAN = true;

        public static void TestCreateVol(AppHook appHook) {
            try {
                Make35Floppy("Bad:VolName", appHook);
                throw new Exception("Formatted with bad name");
            } catch (ArgumentException) {
                // expected
            }

            string goodName = "Good.Vol.Name";
            using (IFileSystem fs = Make35Floppy(goodName, appHook)) {
                if (fs.Notes.WarningCount != 0 || fs.Notes.ErrorCount != 0) {
                    Debug.WriteLine("NOTES:");
                    Debug.WriteLine(fs.Notes.ToString());
                    throw new Exception("Formatted image isn't clean");
                }
                IFileEntry volDir = fs.GetVolDirEntry();
                if (volDir.FileName != goodName) {
                    throw new Exception("Volume name is wrong");
                }

                if (DUMP_TO_FILE) {
                    CopyToFile(fs, "TEST-create-vol-1");
                }
            }

            using (IFileSystem fs = Make35Floppy("foo", appHook)) {
                // try to format while in file-access mode
                try {
                    fs.Format("new", 0, false);
                    throw new Exception("Allowed to format while in file-access mode");
                } catch (IOException) { /*expected */ }
            }
        }

        public static void TestCreateFile(AppHook appHook) {
            using (IFileSystem fs = Make35Floppy("FileTestVol", appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();

                // Create one file.
                string fileName0 = "File0";
                IFileEntry file1 = fs.CreateFile(volDir, fileName0, CreateMode.File);

                if (DUMP_TO_FILE) {
                    CopyToFile(fs, "TEST-create-file-1");
                    fs.PrepareFileAccess(DO_SCAN);
                    Helper.CheckNotes(fs, 0, 0);
                    volDir = fs.GetVolDirEntry();
                }

                // Create another with the same name.
                try {
                    fs.CreateFile(volDir, fileName0, CreateMode.Directory);
                    throw new Exception("Created directory with same name as file");
                } catch (IOException) {
                    // expected
                }

                // Create a file with an illegal name.
                try {
                    fs.CreateFile(volDir, "illegal:name", CreateMode.File);
                    throw new Exception("Created file with illegal name");
                } catch (ArgumentException) {
                    // expected
                }

                List<Helper.FileAttr> attrs = new List<Helper.FileAttr>();
                attrs.Add(new Helper.FileAttr(fileName0, 0, 0 * BLOCK_SIZE));

                // Create 9 more files.  This will fit in the initial catalog extent.
                for (int i = 1; i < 10; i++) {
                    string fileName = "File" + i;
                    fs.CreateFile(volDir, fileName, CreateMode.File);
                    attrs.Add(new Helper.FileAttr(fileName, 0, 0 * BLOCK_SIZE));
                }
                Helper.ValidateDirContents(volDir, attrs);

                // See if they're still there after the filesystem is bounced.
                fs.PrepareRawAccess();
                if (DUMP_TO_FILE) {
                    CopyToFile(fs, "TEST-create-file-2");
                }
                fs.PrepareFileAccess(DO_SCAN);
                Helper.CheckNotes(fs, 0, 0);
                volDir = fs.GetVolDirEntry();

                Helper.ValidateDirContents(volDir, attrs);
            }

            // Create 20 files, in reverse filename order.  This will just barely fit in
            // the initial catalog extent.
            using (IFileSystem fs = Make35Floppy("FileTestVolR", appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();

                List<Helper.FileAttr> attrs = new List<Helper.FileAttr>();

                for (int i = 19; i >= 0; i--) {
                    string fileName = "File" + i.ToString("d2");
                    fs.CreateFile(volDir, fileName, CreateMode.File);
                    attrs.Add(new Helper.FileAttr(fileName, 0, 0 * BLOCK_SIZE));
                }
                attrs.Sort();   // match what HFS does
                Helper.ValidateDirContents(volDir, attrs);

                // See if they're still there after the filesystem is bounced.
                fs.PrepareRawAccess();
                if (DUMP_TO_FILE) {
                    CopyToFile(fs, "TEST-create-file-3");
                }
                fs.PrepareFileAccess(DO_SCAN);
                Helper.CheckNotes(fs, 0, 0);
                volDir = fs.GetVolDirEntry();

                Helper.ValidateDirContents(volDir, attrs);
            }
        }

        public static void TestCreateMany(AppHook appHook) {
            const string SUBDIR_NAME = "Sub.Dir";

            List<Helper.FileAttr> attrs = new List<Helper.FileAttr>();

            // Create 100 files.  This will require expansion of the catalog tree, but not
            // to the point that we need to add extent overflow records.
            using (IFileSystem fs = Make35Floppy("FileTest100", appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();
                IFileEntry subDir = fs.CreateFile(volDir, SUBDIR_NAME, CreateMode.Directory);

                for (int i = 0; i < 100; i++) {
                    string fileName = "File" + i.ToString("d3");
                    fs.CreateFile(subDir, fileName, CreateMode.File);
                    attrs.Add(new Helper.FileAttr(fileName, 0, 0 * BLOCK_SIZE));
                }
                attrs.Sort();
                Helper.ValidateDirContents(subDir, attrs);

                // Bounce the filesystem.
                fs.PrepareRawAccess();
                if (DUMP_TO_FILE) {
                    CopyToFile(fs, "TEST-create-many-1");
                }
                fs.PrepareFileAccess(DO_SCAN);
                Helper.CheckNotes(fs, 0, 0);
                volDir = fs.GetVolDirEntry();
                subDir = fs.FindFileEntry(volDir, SUBDIR_NAME);

                Helper.ValidateDirContents(subDir, attrs);
            }

            // Create 4000 files.  This requires a deeper tree, but because we're not
            // writing to any files the catalog tree still lives in one extent.  The disk
            // will be almost entirely full.
            attrs.Clear();
            using (IFileSystem fs = Make35Floppy("FileTest4000", appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();
                IFileEntry subDir = fs.CreateFile(volDir, SUBDIR_NAME, CreateMode.Directory);

                for (int i = 0; i < 4000; i++) {
                    string fileName = "File" + i.ToString("d4");
                    fs.CreateFile(subDir, fileName, CreateMode.File);
                    attrs.Add(new Helper.FileAttr(fileName, 0, 0 * BLOCK_SIZE));
                }
                attrs.Sort();
                Helper.ValidateDirContents(subDir, attrs);

                // Bounce the filesystem.
                fs.PrepareRawAccess();
                if (DUMP_TO_FILE) {
                    CopyToFile(fs, "TEST-create-many-2");
                }
                fs.PrepareFileAccess(DO_SCAN);
                Helper.CheckNotes(fs, 0, 0);
                volDir = fs.GetVolDirEntry();
                subDir = fs.FindFileEntry(volDir, SUBDIR_NAME);

                Helper.ValidateDirContents(subDir, attrs);
            }
        }

        public static void TestCreateAndWrite(AppHook appHook) {
            const string SUBDIR_NAME = "Sub.Dir";

            List<Helper.FileAttr> attrs = new List<Helper.FileAttr>();

            // Create 200 files, writing one byte to each.  This should fragment the
            // extent records in the catalog tree, causing it to need to allocate
            // additional extents in the overflow tree (twice).
            using (IFileSystem fs = Make35Floppy("Cr/Wr-200", appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();
                IFileEntry subDir = fs.CreateFile(volDir, SUBDIR_NAME, CreateMode.Directory);
                byte[] buf = new byte[1] { 0xcc };

                for (int i = 0; i < 200; i++) {
                    string fileName = "File" + i.ToString("d3");
                    IFileEntry ent = fs.CreateFile(subDir, fileName, CreateMode.File);
                    attrs.Add(new Helper.FileAttr(fileName, 1, 1 * BLOCK_SIZE));

                    using (DiskFileStream fd = fs.OpenFile(ent, FileAccessMode.ReadWrite,
                            FilePart.DataFork)) {
                        fd.Write(buf, 0, 1);
                    }
                }
                attrs.Sort();
                Helper.ValidateDirContents(subDir, attrs);

                // Bounce the filesystem.
                fs.PrepareRawAccess();
                if (DUMP_TO_FILE) {
                    CopyToFile(fs, "TEST-create-write-1");
                }
                fs.PrepareFileAccess(DO_SCAN);
                Helper.CheckNotes(fs, 0, 0);

                volDir = fs.GetVolDirEntry();
                subDir = fs.FindFileEntry(volDir, SUBDIR_NAME);
                Helper.ValidateDirContents(subDir, attrs);
            }
        }

        public static void TestCreatePingPong(AppHook appHook) {
            const string FILE1 = "file1";
            const string FILE2 = "file2";

            // Create two files and alternately write a block of data to them.  They
            // should fragment extravagantly, especially if we're not trimming excess
            // allocations off the end when the file is closed.
            using (IFileSystem fs = Make35Floppy("Cr-PP", appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();
                IFileEntry file1 = fs.CreateFile(volDir, FILE1, CreateMode.File);
                IFileEntry file2 = fs.CreateFile(volDir, FILE2, CreateMode.File);
                byte[] blockBuf = new byte[Defs.BLOCK_SIZE];

                for (int i = 0; i < 500; i++) {
                    RawData.SetU16BE(blockBuf, 0, (ushort)i);
                    using (DiskFileStream fd = fs.OpenFile(file1, FileAccessMode.ReadWrite,
                            FilePart.DataFork)) {
                        fd.Seek(0, SeekOrigin.End);
                        fd.Write(blockBuf, 0, blockBuf.Length);
                    }
                    using (DiskFileStream fd = fs.OpenFile(file2, FileAccessMode.ReadWrite,
                            FilePart.DataFork)) {
                        fd.Seek(0, SeekOrigin.End);
                        fd.Write(blockBuf, 0, blockBuf.Length);
                    }
                }

                // Check the result.
                fs.PrepareRawAccess();
                if (DUMP_TO_FILE) {
                    CopyToFile(fs, "TEST-create-ping-pong-1");
                }
                fs.PrepareFileAccess(DO_SCAN);
                Helper.CheckNotes(fs, 0, 0);

                volDir = fs.GetVolDirEntry();
                file1 = fs.FindFileEntry(volDir, FILE1);
                file2 = fs.FindFileEntry(volDir, FILE2);

                // Continue until we blow out the extents file (around 575).
                try {
                    for (int i = 500; i < 600; i++) {
                        RawData.SetU16BE(blockBuf, 0, (ushort)i);
                        using (DiskFileStream fd = fs.OpenFile(file1, FileAccessMode.ReadWrite,
                                FilePart.DataFork)) {
                            fd.Seek(0, SeekOrigin.End);
                            fd.Write(blockBuf, 0, blockBuf.Length);
                        }
                        using (DiskFileStream fd = fs.OpenFile(file2, FileAccessMode.ReadWrite,
                                FilePart.DataFork)) {
                            fd.Seek(0, SeekOrigin.End);
                            fd.Write(blockBuf, 0, blockBuf.Length);
                        }
                    }
                    throw new Exception("Failed to fill the disk");
                } catch (DiskFullException) {
                    // expected
                }

                // Make sure it's not corrupted.
                fs.PrepareRawAccess();
                if (DUMP_TO_FILE) {
                    CopyToFile(fs, "TEST-create-ping-pong-2");
                }
                fs.PrepareFileAccess(DO_SCAN);
                Helper.CheckNotes(fs, 0, 0);


                // Remove the files.  This should completely empty the extents tree.
                volDir = fs.GetVolDirEntry();
                file1 = fs.FindFileEntry(volDir, FILE1);
                file2 = fs.FindFileEntry(volDir, FILE2);
                fs.DeleteFile(file1);
                fs.DeleteFile(file2);

                fs.PrepareRawAccess();
                if (DUMP_TO_FILE) {
                    CopyToFile(fs, "TEST-create-ping-pong-3");
                }
                fs.PrepareFileAccess(DO_SCAN);
                Helper.CheckNotes(fs, 0, 0);
            }
        }

        public static void TestCreateDelete(AppHook appHook) {
            const string SUBDIR_NAME = "Sub.Dir";

            List<Helper.FileAttr> attrs = new List<Helper.FileAttr>();

            // Create 20 files to make an interesting tree, then delete half.
            using (IFileSystem fs = Make35Floppy("Cr/Del-20", appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();
                IFileEntry subDir = fs.CreateFile(volDir, SUBDIR_NAME, CreateMode.Directory);

                for (int i = 0; i < 20; i++) {
                    string fileName = "File" + i.ToString("d2");
                    fs.CreateFile(subDir, fileName, CreateMode.File);
                    attrs.Add(new Helper.FileAttr(fileName, 0, 0 * BLOCK_SIZE));
                }
                Helper.ValidateDirContents(subDir, attrs);

                for (int i = 0; i < 10; i++) {
                    string fileName = "File" + i.ToString("d2");
                    IFileEntry entry = fs.FindFileEntry(subDir, fileName);
                    fs.DeleteFile(entry);

                    attrs.RemoveAt(0);
                }
                Helper.ValidateDirContents(subDir, attrs);

                // Bounce the filesystem.
                fs.PrepareRawAccess();
                if (DUMP_TO_FILE) {
                    CopyToFile(fs, "TEST-create-delete-1");
                }
                fs.PrepareFileAccess(DO_SCAN);
                Helper.CheckNotes(fs, 0, 0);

                volDir = fs.GetVolDirEntry();
                subDir = fs.FindFileEntry(volDir, SUBDIR_NAME);
                Helper.ValidateDirContents(subDir, attrs);


                // Try to delete the subdirectory while it still has files.
                try {
                    fs.DeleteFile(subDir);
                    throw new Exception("Deleted non-empty subdirectory");
                } catch (IOException) {
                    // Expected.
                }


                // Delete the other 10, leaving only the subdir.  Tests collapse of tree to
                // single node.
                for (int i = 10; i < 20; i++) {
                    string fileName = "File" + i.ToString("d2");
                    IFileEntry entry = fs.FindFileEntry(subDir, fileName);
                    fs.DeleteFile(entry);

                    attrs.RemoveAt(0);
                }
                Helper.ValidateDirContents(subDir, attrs);
                fs.PrepareRawAccess();
                if (DUMP_TO_FILE) {
                    CopyToFile(fs, "TEST-create-delete-2");
                }
                fs.PrepareFileAccess(DO_SCAN);
                Helper.CheckNotes(fs, 0, 0);

                volDir = fs.GetVolDirEntry();
                subDir = fs.FindFileEntry(volDir, SUBDIR_NAME);
                Helper.ValidateDirContents(subDir, attrs);


                // Delete the subdir.  Tests return to minimal tree (root dir only), as well as
                // deletion of a subdirectory.
                fs.DeleteFile(subDir);
                fs.PrepareRawAccess();
                if (DUMP_TO_FILE) {
                    CopyToFile(fs, "TEST-create-delete-3");
                }
                fs.PrepareFileAccess(DO_SCAN);
                Helper.CheckNotes(fs, 0, 0);
            }
        }

        public static void TestBigVol(AppHook appHook) {
            const string SUBDIR_NAME = "Sub.Dir";

            // Create a 256MB volume.  (In memory... yay for 64-bit apps!)
            // This will require an additional map block for the tree files.
            using (IFileSystem fs = Helper.CreateTestImage("BigVol!",
                    FileSystemType.HFS, 256 * 1024 * 2, appHook, out MemoryStream memFile)) {

                IFileEntry volDir = fs.GetVolDirEntry();
                IFileEntry subDir = fs.CreateFile(volDir, SUBDIR_NAME, CreateMode.Directory);

                // Fill up the catalog tree by creating lots of empty files.  At about 3
                // entries per node, we'll need around 6,000 files to expand past the
                // initial 2048-entry node usage bitmap.
                for (int i = 0; i < 6000; i++) {
                    string fileName = "File" + i.ToString("d4");
                    fs.CreateFile(subDir, fileName, CreateMode.File);
                }

                // Bounce the filesystem.
                fs.PrepareRawAccess();
                if (DUMP_TO_FILE) {
                    CopyToFile(fs, "TEST-create-big-1");
                }
                fs.PrepareFileAccess(DO_SCAN);
                Helper.CheckNotes(fs, 0, 0);

                // Delete half the records to verify that our bitmap code is working.  They
                // need to be clumped together to ensure nodes get released.  Do some from the
                // start and some from the end.
                volDir = fs.GetVolDirEntry();
                subDir = fs.FindFileEntry(volDir, SUBDIR_NAME);
                for (int i = 0; i < 1500; i++) {
                    string fileName = "File" + i.ToString("d4");
                    IFileEntry entry = fs.FindFileEntry(subDir, fileName);
                    fs.DeleteFile(entry);
                    fileName = "File" + (5999-i).ToString("d4");
                    entry = fs.FindFileEntry(subDir, fileName);
                    fs.DeleteFile(entry);
                }

                fs.PrepareRawAccess();
                if (DUMP_TO_FILE) {
                    CopyToFile(fs, "TEST-create-big-2");
                }
                fs.PrepareFileAccess(DO_SCAN);
                Helper.CheckNotes(fs, 0, 0);
            }
        }

        public static void TestRename(AppHook appHook) {
            // Mix some remapped control characters in.
            const string VOL = "Rename\u240aStuff";
            const string SUB = "Sub";
            const string SUB_SUB = "SubSub";
            const string SUB_SUB_SUB = "SubSub\u240dSub";
            const string FILE = "File";

            // Create some files and directories.  Test both to ensure that the appropriate
            // catalog entries and thread records are being updated.
            using (IFileSystem fs = Make35Floppy(VOL, appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();
                IFileEntry subDir = fs.CreateFile(volDir, SUB, CreateMode.Directory);
                IFileEntry subSubDir = fs.CreateFile(subDir, SUB_SUB, CreateMode.Directory);
                IFileEntry subSubSubDir = fs.CreateFile(subDir, SUB_SUB_SUB, CreateMode.Directory);
                IFileEntry file = fs.CreateFile(volDir, FILE, CreateMode.File);
                IFileEntry subFile = fs.CreateFile(subDir, FILE, CreateMode.File);
                IFileEntry subSubFile = fs.CreateFile(subSubDir, FILE, CreateMode.File);

                volDir.FileName += "1";
                volDir.SaveChanges();
                subDir.FileName += "2";
                subDir.SaveChanges();
                subSubDir.FileName += "3";
                subSubDir.SaveChanges();
                subSubSubDir.FileName += "4";
                subSubSubDir.SaveChanges();
                file.FileName += "5";
                file.SaveChanges();
                subFile.FileName += "6";
                subFile.SaveChanges();
                // Try one with the raw interface.
                byte[] raw = new byte[subSubFile.FileName.Length + 1];
                MacChar.UnicodeToMac(subSubFile.FileName, raw, 0, MacChar.Encoding.RomanShowCtrl);
                raw[raw.Length - 1] = (byte)'7';
                subSubFile.RawFileName = raw;
                subSubFile.SaveChanges();

                // Try to sneak a bad change in via raw.
                try {
                    byte[] badBuf = new byte[3] { (byte)'A', (byte)':', (byte)'Z' };
                    subDir.RawFileName = badBuf;
                    throw new Exception("Bad raw filename allowed");
                } catch (ArgumentException) {
                    // expected
                }

                try {
                    byte[] badVolBuf = new byte[28] {   // one byte too long for volume name
                        0x2a, 0x2a, 0x2a, 0x2a, 0x2a, 0x2a, 0x2a, 0x2a, 0x2a, 0x2a,
                        0x2a, 0x2a, 0x2a, 0x2a, 0x2a, 0x2a, 0x2a, 0x2a, 0x2a, 0x2a,
                        0x2a, 0x2a, 0x2a, 0x2a, 0x2a, 0x2a, 0x2a, 0x2a
                    };
                    volDir.RawFileName = badVolBuf;
                    throw new Exception("Bad raw volume name allowed");
                } catch (ArgumentException) {
                    // expected
                }

                // Bounce the filesystem.
                fs.PrepareRawAccess();
                if (DUMP_TO_FILE) {
                    CopyToFile(fs, "TEST-rename-1");
                }
                fs.PrepareFileAccess(DO_SCAN);
                Helper.CheckNotes(fs, 0, 0);

                volDir = fs.GetVolDirEntry();
                subDir = fs.FindFileEntry(volDir, SUB + "2");
                subSubDir = fs.FindFileEntry(subDir, SUB_SUB + "3");
                subSubSubDir = fs.FindFileEntry(subDir, SUB_SUB_SUB + "4");
                file = fs.FindFileEntry(volDir, FILE + "5");
                subFile = fs.FindFileEntry(subDir, FILE + "6");
                subSubFile = fs.FindFileEntry(subSubDir, FILE + "7");

                if (volDir.FileName != VOL + "1") {
                    throw new Exception("Incorrect volume name");
                }
            }
        }

        private static DateTime TEST_DATE1 = new DateTime(1977, 6, 1);
        private static DateTime TEST_DATE2 = new DateTime(1983, 1, 1);
        private static DateTime TEST_DATE3 = new DateTime(1986, 9, 15);

        public static void TestAttribs(AppHook appHook) {
            const string SUBDIR = "SübDïr";
            const string SUBDIR_NEW = "SübDïrSübDïrSübDïrSübDïrSübDïr!";
            const string FILE = "File";
            const string FILE_NEW = "F";

            using (IFileSystem fs = Make35Floppy("Attr.Test", appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();
                IFileEntry subDir = fs.CreateFile(volDir, SUBDIR, CreateMode.Directory);
                IFileEntry file = fs.CreateFile(volDir, FILE, CreateMode.File);

                file.FileName = FILE_NEW;
                file.HFSCreator = 0x11223344;
                file.HFSFileType = 0x88776655;
                file.Access = (byte)ACCESS_UNWRITABLE;
                file.CreateWhen = TEST_DATE1;
                file.ModWhen = TEST_DATE2;
                ((DiskArc.FS.HFS_FileEntry)file).BackupWhen = TEST_DATE3;

                subDir.FileName = SUBDIR_NEW;
                subDir.HFSCreator = 0x55223344;             // no effect
                subDir.HFSFileType = 0x99776655;            // no effect
                subDir.Access = (byte)ACCESS_LOCKED;        // no effect
                subDir.CreateWhen = TEST_DATE1;
                subDir.ModWhen = TEST_DATE2;
                ((DiskArc.FS.HFS_FileEntry)subDir).BackupWhen = TEST_DATE3;

                // Verify that changes are visible immediately.
                if (file.FileName != FILE_NEW ||
                        file.HFSCreator != 0x11223344 ||
                        file.HFSFileType != 0x88776655 ||
                        file.Access != (byte)ACCESS_UNWRITABLE ||
                        file.CreateWhen != TEST_DATE1 ||
                        file.ModWhen != TEST_DATE2 ||
                        ((DiskArc.FS.HFS_FileEntry)file).BackupWhen != TEST_DATE3) {
                    throw new Exception("Not all changes were saved - file1");
                }
                if (subDir.FileName != SUBDIR_NEW ||
                        subDir.HFSCreator != 0 ||
                        subDir.HFSFileType != 0 ||
                        subDir.Access != (byte)ACCESS_UNLOCKED ||
                        subDir.CreateWhen != TEST_DATE1 ||
                        subDir.ModWhen != TEST_DATE2 ||
                        ((DiskArc.FS.HFS_FileEntry)subDir).BackupWhen != TEST_DATE3) {
                    throw new Exception("Not all changes were saved - subdir1");
                }

                // Save changes, then bounce filesystem and check again.
                file.SaveChanges();
                subDir.SaveChanges();

                fs.PrepareRawAccess();
                if (DUMP_TO_FILE) {
                    CopyToFile(fs, "TEST-attribs-1");
                }
                fs.PrepareFileAccess(DO_SCAN);
                Helper.CheckNotes(fs, 0, 0);

                volDir = fs.GetVolDirEntry();
                subDir = fs.FindFileEntry(volDir, SUBDIR_NEW);
                file = fs.FindFileEntry(volDir, FILE_NEW);

                // Verify that changes were written to disk.
                if (file.FileName != FILE_NEW ||
                        file.HFSCreator != 0x11223344 ||
                        file.HFSFileType != 0x88776655 ||
                        file.Access != (byte)ACCESS_UNWRITABLE ||
                        file.CreateWhen != TEST_DATE1 ||
                        file.ModWhen != TEST_DATE2 ||
                        ((DiskArc.FS.HFS_FileEntry)file).BackupWhen != TEST_DATE3) {
                    throw new Exception("Not all changes were saved - file2");
                }
                if (subDir.FileName != SUBDIR_NEW ||
                        subDir.HFSCreator != 0 ||
                        subDir.HFSFileType != 0 ||
                        subDir.Access != (byte)ACCESS_UNLOCKED ||
                        subDir.CreateWhen != TEST_DATE1 ||
                        subDir.ModWhen != TEST_DATE2 ||
                        ((DiskArc.FS.HFS_FileEntry)subDir).BackupWhen != TEST_DATE3) {
                    throw new Exception("Not all changes were saved - subdir2");
                }
            }
        }

        public static void TestNoScan(AppHook appHook) {
            const string SUBDIR = "SubDir";
            const string LAST_FILE = "LastFile";

            using (IFileSystem fs = Make35Floppy("NoScanCheck", appHook)) {
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
                attrs.Add(new Helper.FileAttr(LAST_FILE, 0, 0 * BLOCK_SIZE));
                Helper.ValidateDirContents(subDir, attrs);
            }
        }


        // Make a 3.5" floppy image (800KB).
        public static IFileSystem Make35Floppy(string volName, AppHook appHook) {
            return Helper.CreateTestImage(volName, FileSystemType.HFS, 1600, appHook,
                out MemoryStream memFile);
        }
        public static void CopyToFile(IFileSystem fs, string fileName) {
            fs.DumpToFile(Helper.DebugFileDir + fileName + ".po");
        }
    }
}
