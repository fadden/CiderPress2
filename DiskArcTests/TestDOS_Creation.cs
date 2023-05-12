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
using System.Text;

using CommonUtil;
using DiskArc;
using static DiskArc.Defs;
using static DiskArc.IDiskImage;
using static DiskArc.IFileSystem;

namespace DiskArcTests {
    public class TestDOS_Creation : ITest {
        private const bool DO_SCAN = true;

        // Basic volume creation tests.
        public static void TestCreateVol(AppHook appHook) {
            // DOS 3.3, bootable
            using (IFileSystem fs = Helper.CreateTestImage(string.Empty, FileSystemType.DOS33,
                    35, 16, 1, true, appHook, out MemoryStream memFile)) {
                if (fs.Notes.WarningCount != 0 || fs.Notes.ErrorCount != 0) {
                    throw new Exception("Source image isn't clean: " + fs.Notes.ToString());
                }

                if (fs.FreeSpace != (35 - 4) * 16 * SECTOR_SIZE) {
                    throw new Exception("Incorrect free space #1: " + fs.FreeSpace);
                }
            }

            // DOS 3.3, non-bootable
            using (IFileSystem fs = Helper.CreateTestImage(string.Empty, FileSystemType.DOS33,
                    35, 16, 2, false, appHook, out MemoryStream memFile)) {
                if (fs.Notes.WarningCount != 0 || fs.Notes.ErrorCount != 0) {
                    throw new Exception("Source image isn't clean: " + fs.Notes.ToString());
                }

                if (fs.FreeSpace != (35 - 2) * 16 * SECTOR_SIZE) {
                    throw new Exception("Incorrect free space #2: " + fs.FreeSpace);
                }
            }

            // DOS 3.2, bootable
            using (IFileSystem fs = Helper.CreateTestImage(string.Empty, FileSystemType.DOS33,
                    35, 13, 3, true, appHook, out MemoryStream memFile)) {
                if (fs.Notes.WarningCount != 0 || fs.Notes.ErrorCount != 0) {
                    throw new Exception("Source image isn't clean: " + fs.Notes.ToString());
                }

                if (fs.FreeSpace != (35 - 4) * 13 * SECTOR_SIZE) {
                    throw new Exception("Incorrect free space #3: " + fs.FreeSpace);
                }
            }

            // try to format while in file-access mode
            using (IFileSystem fs = Make525Floppy(4, appHook)) {
                try {
                    fs.Format(string.Empty, 0, false);
                    throw new Exception("Allowed to format while in file-access mode");
                } catch (IOException) { /*expected */ }
            }

            // try a weirdly-sized disk
            try {
                Helper.CreateTestImage(string.Empty, FileSystemType.DOS33, 37, 16, 5, true,
                    appHook, out MemoryStream memFile);
                throw new Exception("Allowed to create weirdly-sized DOS disk");
            } catch (ArgumentException) { /*expected*/ }
        }

        public static void TestSwapped(AppHook hook) {
            uint size = 35 * 16 * SECTOR_SIZE;      // 140KB floppy
            Helper.CreateParams parms = new Helper.CreateParams() {
                FSType = FileSystemType.DOS33,
                Size = size,
                VolumeNum = 254,
                SectorsPerTrack = 16,
                MakeBootable = false,
                FileOrder = SectorOrder.ProDOS_Block,       // use block order for sectors
            };
            byte[] buffer = new byte[size];
            MemoryStream memStream = new MemoryStream(buffer, 0, (int)size);
            IFileSystem fs = Helper.CreateNewFS(memStream, parms, hook);
            fs.PrepareFileAccess(true);     // verify working
            fs.Dispose();

            // Now open it with the wrong order, which the analyzer should correct.
            using IDiskImage? diskImage = FileAnalyzer.PrepareDiskImage(memStream,
                FileKind.UnadornedSector, hook);
            if (diskImage == null ||
                    !diskImage.AnalyzeDisk(null, SectorOrder.DOS_Sector, AnalysisDepth.Full)) {
                throw new Exception("Failed to open image created previously");
            }
            fs = (IFileSystem)diskImage.Contents!;
            if (fs.RawAccess.FileOrder != SectorOrder.ProDOS_Block) {
                throw new Exception("Disk image wasn't created with correct order");
            }
        }

        // File creation tests.
        public static void TestCreateFile(AppHook appHook) {
            using (IFileSystem fs = Make525Floppy(1, appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();
                IFileEntry file1 = fs.CreateFile(volDir, "FILE1", CreateMode.File);
                using (DiskFileStream fd = fs.OpenFile(file1, FileAccessMode.ReadOnly,
                        FilePart.RawData)) {
                    if (fd.Seek(0, SeekOrigin.End) != 0) {
                        throw new Exception("New file has nonzero length");
                    }
                }

                IFileEntry file2 = fs.CreateFile(volDir, "FILE2", CreateMode.File);

                // Try to create another file with the same name.
                try {
                    fs.CreateFile(volDir, "FILE1", CreateMode.File);
                    throw new Exception("Created same file twice");
                } catch (IOException) {
                    // expected
                }
                // Try to create a directory.
                try {
                    fs.CreateFile(volDir, "FILE-D", CreateMode.Directory);
                    throw new Exception("Created directory");
                } catch (ArgumentException) {
                    // expected
                }
                // Try to create an extended file.
                try {
                    fs.CreateFile(volDir, "FILE-X", CreateMode.Extended);
                    throw new Exception("Created extended file");
                } catch (ArgumentException) {
                    // expected
                }

                // Try to create an invalid filename (name too long).
                try {
                    fs.CreateFile(volDir, "Q12345678901234567890123456789A", CreateMode.File);
                    throw new Exception("Created bad filename #1");
                } catch (ArgumentException) {
                    // expected
                }
                // Try to create an invalid filename (illegal char).
                try {
                    fs.CreateFile(volDir, "FOO\u2022", CreateMode.File);
                    throw new Exception("Created bad filename #2");
                } catch (ArgumentException) {
                    // expected
                }
                // Try to create an invalid filename (illegal initial char).
                try {
                    fs.CreateFile(volDir, "0FOO", CreateMode.File);
                    throw new Exception("Created bad filename #3");
                } catch (ArgumentException) {
                    // expected
                }
                // Try to create an invalid filename (comma not allowed).
                try {
                    fs.CreateFile(volDir, "FOO,", CreateMode.File);
                    throw new Exception("Created bad filename #4");
                } catch (ArgumentException) {
                    // expected
                }
                // Try to create an invalid filename (trailing spaces).
                try {
                    fs.CreateFile(volDir, "FOO ", CreateMode.File);
                    throw new Exception("Created bad filename #5");
                } catch (ArgumentException) {
                    // expected
                }

                // Create a max-length filename with DEL and a Ctrl+G in it.
                fs.CreateFile(volDir, "Q123456789012345678901234567\u2421\u2407",
                        CreateMode.File);
                // This is valid.
                fs.CreateFile(volDir, string.Empty, CreateMode.File);

                // See if things look right.
                List<Helper.FileAttr> expected = new List<Helper.FileAttr>() {
                    new Helper.FileAttr("FILE1", 0, 1 * SECTOR_SIZE),
                    new Helper.FileAttr("FILE2", 0, 1 * SECTOR_SIZE),
                    new Helper.FileAttr("Q123456789012345678901234567\u2421\u2407",
                        0, 1 * SECTOR_SIZE),
                    new Helper.FileAttr(string.Empty, 0, 1 * SECTOR_SIZE),
                };
                Helper.ValidateDirContents(volDir, expected);

                // Bounce the filesystem and check again.
                fs.PrepareRawAccess();
                fs.PrepareFileAccess(DO_SCAN);
                Helper.CheckNotes(fs, 0, 0);
                volDir = fs.GetVolDirEntry();
                Helper.ValidateDirContents(volDir, expected);
            }
        }

        public static void TestRawName(AppHook appHook) {
            using (IFileSystem fs = Make525Floppy(66, appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();
                IFileEntry entry = fs.CreateFile(volDir, "Basic Name", CreateMode.File);
                entry.RawFileName = new byte[] {
                    // "* HELLO *", where the spaces and text are inverse
                    0xaa, 0x20, 0x08, 0x05, 0x0c, 0x0c, 0x0f, 0x20, 0xaa
                };
                if (entry.FileName != "* HELLO *") {
                    throw new Exception("Failed to generate cooked name");
                }

                string helloWorld = "HELLO, WORLD!";
                try {
                    entry.FileName = helloWorld;
                    throw new Exception("Invalid filename was accepted");
                } catch (ArgumentException) { /*expected*/ }

                byte[] rawHelloWorld = Encoding.ASCII.GetBytes(helloWorld);
                for (int i = 0; i < rawHelloWorld.Length; i++) {
                    rawHelloWorld[i] |= 0x80;
                }
                entry.RawFileName = rawHelloWorld;
                if (entry.FileName != helloWorld) {
                    throw new Exception("Raw filename not accepted; got '" + entry.FileName + "'");
                }
            }
        }

        public static void TestCreateDelete(AppHook appHook) {
            using (IFileSystem fs = Make525Floppy(1, appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();

                List<Helper.FileAttr> expected = new List<Helper.FileAttr>();

                // Create 20 files.
                for (int i = 0; i < 20; i++) {
                    string name = "FILE-" + i.ToString("D2");
                    fs.CreateFile(volDir, name, CreateMode.File);
                    expected.Add(new Helper.FileAttr(name, 0, 1 * SECTOR_SIZE));
                }
                Helper.ValidateDirContents(volDir, expected);

                // Delete files 0, 5, and 10.
                IFileEntry entry = fs.FindFileEntry(volDir, "FILE-00");
                fs.DeleteFile(entry);
                entry = fs.FindFileEntry(volDir, "FILE-05");
                fs.DeleteFile(entry);
                entry = fs.FindFileEntry(volDir, "FILE-10");
                fs.DeleteFile(entry);

                expected.RemoveAt(10);
                expected.RemoveAt(5);
                expected.RemoveAt(0);
                Helper.ValidateDirContents(volDir, expected);

                // Create two replacements, which should fill the recently-deleted slots.
                fs.CreateFile(volDir, "FILE-NEW00", CreateMode.File);
                expected.Insert(0, new Helper.FileAttr("FILE-NEW00", 0, 1 * SECTOR_SIZE));
                fs.CreateFile(volDir, "FILE-NEW05", CreateMode.File);
                expected.Insert(5, new Helper.FileAttr("FILE-NEW05", 0, 1 * SECTOR_SIZE));
                Helper.ValidateDirContents(volDir, expected);

                // Run a filesystem check, and check the files again.
                fs.PrepareRawAccess();
                fs.PrepareFileAccess(DO_SCAN);
                Helper.CheckNotes(fs, 0, 0);

                volDir = fs.GetVolDirEntry();
                Helper.ValidateDirContents(volDir, expected);
            }
        }

        public static void TestFullDir(AppHook appHook) {
            using (IFileSystem fs = Make525Floppy(1, appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();

                List<Helper.FileAttr> expected = new List<Helper.FileAttr>();

                // We should be able to create 105 files.
                int maxFiles = (SECTOR_SIZE / DiskArc.FS.DOS.CATALOG_ENTRY_LEN) * 15;
                for (int i = 0; i < maxFiles; i++) {
                    string name = "FILE-" + i.ToString("D3");
                    fs.CreateFile(volDir, name, CreateMode.File);
                    expected.Add(new Helper.FileAttr(name, 0, 1 * SECTOR_SIZE));
                }
                Helper.ValidateDirContents(volDir, expected);

                try {
                    fs.CreateFile(volDir, "TOO MANY", CreateMode.File);
                    throw new Exception("Created too many files");
                } catch (DiskFullException) {
                    // expected
                }
            }
        }

        public static void TestAttribs(AppHook appHook) {
            DateTime TEST_DATE1 = new DateTime(1977, 6, 1);
            DateTime TEST_DATE2 = new DateTime(1986, 9, 15);

            using (IFileSystem fs = Make525Floppy(1, appHook)) {
                const string NEW_NAME = "NEW NAME";

                IFileEntry volDir = fs.GetVolDirEntry();
                IFileEntry file = fs.CreateFile(volDir, "FILE", CreateMode.File);

                // Can't set BIN aux type, because the file doesn't have a data sector.  Make
                // it a text file for now.
                file.FileName = NEW_NAME;
                file.FileType = FileAttribs.FILE_TYPE_TXT;
                file.AuxType = 0x1234;
                file.Access = 0x21;
                file.CreateWhen = TEST_DATE1;
                file.ModWhen = TEST_DATE2;

                // Verify name change with a case-sensitive comparison.
                if (file.FileName != NEW_NAME ||
                        file.FileType != FileAttribs.FILE_TYPE_TXT ||
                        (file.Access != 0x21 && file.Access != 0x01)) {
                    throw new Exception("Not all changes were saved #1 (access=$" +
                        file.Access.ToString("x2") + ")");
                }

                // Flush to disk, then bounce the filesystem.
                file.SaveChanges();

                fs.PrepareRawAccess();
                fs.PrepareFileAccess(DO_SCAN);
                Helper.CheckNotes(fs, 0, 0);

                volDir = fs.GetVolDirEntry();
                file = fs.FindFileEntry(volDir, NEW_NAME);

                if (file.FileName != NEW_NAME ||
                        file.FileType != FileAttribs.FILE_TYPE_TXT ||
                        (file.Access != 0x21 && file.Access != 0x01)) {
                    throw new Exception("Not all changes were saved #2 (access=$" +
                        file.Access.ToString("x2") + ")");
                }
            }
        }

        // Create a 'B' file with a sparse section.
        public static void TestSparseBin(AppHook appHook) {
            using (IFileSystem fs = Make525Floppy(1, appHook)) {
                const string FILENAME = "SPARSE-BIN";

                IFileEntry volDir = fs.GetVolDirEntry();
                IFileEntry entry = fs.CreateFile(volDir, FILENAME, CreateMode.File);
                entry.FileType = FileAttribs.FILE_TYPE_BIN;
                using (Stream stream = fs.OpenFile(entry, FileAccessMode.ReadWrite,
                       FilePart.RawData)) {
                    RawData.WriteU16LE(stream, 0x2000);
                    RawData.WriteU16LE(stream, 0x2000 - 8);
                    for (int i = 0; i < 4096 - 4; i++) {
                        stream.WriteByte(0xff);
                    }
                    stream.Seek(512, SeekOrigin.Current);
                    for (int i = 0; i < 4096 - 512 - 8; i++) {
                        stream.WriteByte(0xff);
                    }
                }

                //fs.DumpToFile(Helper.DebugFileDir + "TEST-dos-sparse.do");

                // File should be accepted, and should NOT reflect the sparse block in its
                // length (because it's a 'B' file).
                fs.PrepareRawAccess();
                fs.PrepareFileAccess(true);
                Helper.CheckNotes(fs, 0, 0);

                volDir = fs.GetVolDirEntry();
                entry = fs.FindFileEntry(volDir, FILENAME);
                if (entry.DataLength != 0x2000 - 8) {
                    throw new Exception("Unexpected length");
                }
                // Storage size is 0x2000 for the data, plus one sector for the T/S list, minus
                // two sectors for the 512-byte gap in the middle.
                if (entry.StorageSize != 0x1f00) {
                    throw new Exception("Unexpected storage");
                }
            }
        }

        public static IFileSystem Make525Floppy(byte volNum, AppHook appHook) {
            return Helper.CreateTestImage(string.Empty, FileSystemType.DOS33, 35, 16, volNum,
                true, appHook, out MemoryStream memFile);
        }
    }
}
