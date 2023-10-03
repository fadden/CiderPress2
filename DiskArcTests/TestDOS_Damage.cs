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

using CommonUtil;
using DiskArc;
using static DiskArc.Defs;
using static DiskArc.IFileSystem;

namespace DiskArcTests {
    /// <summary>
    /// Test behavior when various problems are encountered.  The disk images are damaged
    /// via the raw data interface.
    /// </summary>
    public class TestDOS_Damage : ITest {
        // Damage the VTOC.
        public static void TestBadVol(AppHook appHook) {
            using (IFileSystem fs = Make525Floppy(128, appHook)) {
                fs.PrepareRawAccess();

                // Nuke the VTOC.
                byte[] sectorBuf = new byte[SECTOR_SIZE];
                fs.RawAccess.WriteSector(DiskArc.FS.DOS.DEFAULT_VTOC_TRACK,
                    DiskArc.FS.DOS.DEFAULT_VTOC_SECTOR, sectorBuf, 0);

                try {
                    fs.PrepareFileAccess(true);
                    throw new Exception("VTOC destruction failed");
                } catch (DAException) {
                    // expected
                }
            }
        }

        // Confirm detection of overlapping files.
        public static void TestOverlap(AppHook appHook) {
            const string FILE1 = "File1";
            const string FILE2 = "File2";
            const string FILE3 = "File3";

            using (IFileSystem fs = Make525Floppy(128, appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();

                // Create three 'S' type files.
                IFileEntry file1 = fs.CreateFile(volDir, FILE1, CreateMode.File);
                Helper.PopulateFile(fs, file1, 0xcc, 8, 0);
                IFileEntry file2 = fs.CreateFile(volDir, FILE2, CreateMode.File);
                Helper.PopulateFile(fs, file2, 0xdd, 16, 0);
                IFileEntry file3 = fs.CreateFile(volDir, FILE3, CreateMode.File);
                Helper.PopulateFile(fs, file3, 0xee, 24, 0);

                fs.PrepareRawAccess();

                // Read the first catalog sector, and copy the T/S entry from the first to
                // the second.
                byte[] sectorBuf = new byte[SECTOR_SIZE];
                fs.RawAccess.ReadSector(17, 15, sectorBuf, 0);

                int offset1 = DiskArc.FS.DOS.CATALOG_ENTRY_START;
                int offset2 = offset1 + DiskArc.FS.DOS.CATALOG_ENTRY_LEN;
                sectorBuf[offset2 + 0x00] = sectorBuf[offset1 + 0x00];
                sectorBuf[offset2 + 0x01] = sectorBuf[offset1 + 0x01];
                fs.RawAccess.WriteSector(17, 15, sectorBuf, 0);

                fs.PrepareFileAccess(true);
                if (fs.IsDubious) {
                    throw new Exception("FS was marked as damaged");
                }

                volDir = fs.GetVolDirEntry();
                file1 = fs.FindFileEntry(volDir, FILE1);
                file2 = fs.FindFileEntry(volDir, FILE2);
                file3 = fs.FindFileEntry(volDir, FILE3);

                // Confirm the problem was detected and flags raised.
                if (!file1.IsDubious || !file2.IsDubious) {
                    throw new Exception("Overlapping files not marked as dubious");
                }
                if (file1.IsDamaged || file2.IsDamaged) {
                    throw new Exception("Overlapping files marked as damaged");
                }
                if (file3.IsDubious || file3.IsDamaged) {
                    throw new Exception("File3 got conflicted/damaged somehow");
                }

                // Compare file attributes, to verify that our testing is doing what we think.
                if (file1.DataLength != file2.DataLength) {
                    throw new Exception("DataLength doesn't match");
                }
                if (file1.StorageSize != file2.StorageSize) {
                    throw new Exception("Storage size doesn't match");
                }
            }
        }

        public static void TestBadVtoc(AppHook appHook) {
            using (IFileSystem fs = Make525Floppy(128, appHook)) {
                fs.PrepareRawAccess();

                byte[] sectorBuf = new byte[SECTOR_SIZE];
                fs.RawAccess.ReadSector(DiskArc.FS.DOS.DEFAULT_VTOC_TRACK,
                    DiskArc.FS.DOS.DEFAULT_VTOC_SECTOR, sectorBuf, 0);
                // Set the first catalog sector to track 17, sector 0 (self-referential).  An
                // invalid track/sector would cause the entire volume to be rejected.
                sectorBuf[0x02] = 0x00;
                fs.RawAccess.WriteSector(DiskArc.FS.DOS.DEFAULT_VTOC_TRACK,
                    DiskArc.FS.DOS.DEFAULT_VTOC_SECTOR, sectorBuf, 0);
                fs.PrepareFileAccess(true);
                if (!fs.IsDubious) {
                    throw new Exception("Volume not reported as damaged");
                }
            }
        }

        public static void TestBadCatalog(AppHook appHook) {
            const string FILE1 = "FILE1";
            byte[] sectorBuf = new byte[SECTOR_SIZE];

            using (IFileSystem fs = Make525Floppy(128, appHook)) {
                fs.PrepareRawAccess();
                fs.RawAccess.ReadSector(17, 1, sectorBuf, 0);
                // Set the "next sector" field equal to the first catalog sector (creates a loop).
                sectorBuf[0x01] = 17;
                sectorBuf[0x02] = 15;
                fs.RawAccess.WriteSector(17, 1, sectorBuf, 0);

                fs.PrepareFileAccess(true);
                if (!fs.IsDubious) {
                    throw new Exception("Volume not reported as damaged");
                }
            }

            using (IFileSystem fs = Make525Floppy(128, appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();
                IFileEntry file1 = fs.CreateFile(volDir, FILE1, CreateMode.File);
                Helper.PopulateFile(fs, file1, 0xcc, 8, 0);

                // Trash the T/S link in the catalog entry.
                fs.PrepareRawAccess();
                fs.RawAccess.ReadSector(17, 15, sectorBuf, 0);
                sectorBuf[DiskArc.FS.DOS.CATALOG_ENTRY_START + 0x00] = 0x80;
                fs.RawAccess.WriteSector(17, 15, sectorBuf, 0);

                fs.PrepareFileAccess(true);

                // The volume isn't damaged -- we can safely modify files other than this one.
                if (fs.IsDubious) {
                    throw new Exception("Volume reported as damaged");
                }
                volDir = fs.GetVolDirEntry();
                file1 = fs.FindFileEntry(volDir, FILE1);
                if (!file1.IsDamaged) {
                    throw new Exception("File not reported as damaged");
                }
            }

            using (IFileSystem fs = Make525Floppy(128, appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();
                IFileEntry file1 = fs.CreateFile(volDir, FILE1, CreateMode.File);
                Helper.PopulateFile(fs, file1, 0xcc, 8, 0);

                // Get the first T/S list sector.
                fs.PrepareRawAccess();
                fs.RawAccess.ReadSector(17, 15, sectorBuf, 0);
                byte tsTrk = sectorBuf[DiskArc.FS.DOS.CATALOG_ENTRY_START + 0x00];
                byte tsSct = sectorBuf[DiskArc.FS.DOS.CATALOG_ENTRY_START + 0x01];

                // Read the T/S list, and modify the "next" link to be a loop.
                fs.RawAccess.ReadSector(tsTrk, tsSct, sectorBuf, 0);
                sectorBuf[0x01] = tsTrk;
                sectorBuf[0x02] = tsSct;
                fs.RawAccess.WriteSector(tsTrk, tsSct, sectorBuf, 0);

                fs.PrepareFileAccess(true);

                // The volume isn't damaged -- we can safely modify files other than this one.
                if (fs.IsDubious) {
                    throw new Exception("Volume reported as damaged");
                }
                // The file is too damaged to access.
                volDir = fs.GetVolDirEntry();
                file1 = fs.FindFileEntry(volDir, FILE1);
                if (!file1.IsDamaged) {
                    throw new Exception("File not reported as damaged");
                }

                // Undo the damage.
                fs.PrepareRawAccess();
                sectorBuf[0x01] = sectorBuf[0x02] = 0;
                // Add a bad T/S entry.
                sectorBuf[0x80] = 0x80;
                fs.RawAccess.WriteSector(tsTrk, tsSct, sectorBuf, 0);

                fs.PrepareFileAccess(true);
                if (fs.IsDubious) {
                    throw new Exception("Volume reported as damaged");
                }
                // The file is partially accessible.  It may be read but not written.
                volDir = fs.GetVolDirEntry();
                file1 = fs.FindFileEntry(volDir, FILE1);
                if (file1.IsDamaged) {
                    throw new Exception("File reported as damaged");
                }
                if (!file1.IsDubious) {
                    throw new Exception("File not reported as dubious");
                }

                // Read access is allowed.
                using (DiskFileStream fd = fs.OpenFile(file1, FileAccessMode.ReadOnly,
                        FilePart.RawData)) {
                    // yay
                }
                // Write access is not.
                try {
                    fs.OpenFile(file1, FileAccessMode.ReadWrite, FilePart.RawData);
                    throw new Exception("Allowed to open dubious file read-write");
                } catch (IOException) {
                    // expected
                }
                try {
                    file1.FileType = FileAttribs.FILE_TYPE_BAS;
                    throw new Exception("Allowed to change type of dubious file");
                } catch (IOException) {
                    // expected
                }
            }

        }

        public static void TestBadSize(AppHook appHook) {
            const string FILE1 = "FILE1";
            byte[] sectorBuf = new byte[SECTOR_SIZE];

            using (IFileSystem fs = Make525Floppy(128, appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();
                IFileEntry file1 = fs.CreateFile(volDir, FILE1, CreateMode.File);
                Helper.PopulateFile(fs, file1, 0xcc, 8, 0);

                // Change the sector count.
                fs.PrepareRawAccess();
                fs.RawAccess.ReadSector(17, 15, sectorBuf, 0);
                sectorBuf[DiskArc.FS.DOS.CATALOG_ENTRY_START + 0x21] = 0x80;
                fs.RawAccess.WriteSector(17, 15, sectorBuf, 0);

                fs.PrepareFileAccess(true);
                volDir = fs.GetVolDirEntry();
                file1 = fs.FindFileEntry(volDir, FILE1);

                if (file1.StorageSize != 17 * SECTOR_SIZE) {
                    throw new Exception("Initial size is wrong: " + file1.StorageSize);
                }

                // The size in sectors, stored in the catalog entry, isn't supposed to change
                // unless the file is modified.  Sometimes disk images have deliberately
                // incorrect sizes for aesthetic reasons, and we don't want to mess with them
                // unless they get in our way.  (We're not too particular though; simply opening
                // the file read-write is enough to cause a fix.)
                using (DiskFileStream fd = fs.OpenFile(file1, FileAccessMode.ReadOnly,
                        FilePart.RawData)) {
                    // do nothing
                }
                file1.SaveChanges();

                fs.PrepareRawAccess();
                fs.RawAccess.ReadSector(17, 15, sectorBuf, 0);
                if (sectorBuf[DiskArc.FS.DOS.CATALOG_ENTRY_START + 0x21] != 0x80) {
                    throw new Exception("Size was changed");
                }

                fs.PrepareFileAccess(true);
                volDir = fs.GetVolDirEntry();
                file1 = fs.FindFileEntry(volDir, FILE1);
                using (DiskFileStream fd = fs.OpenFile(file1, FileAccessMode.ReadWrite,
                        FilePart.RawData)) {
                    // Overwrite first byte of file.  Doesn't actually change size, but it
                    // does cause a change.
                    byte[] smallBuf = new byte[] { 0xcc };
                    fd.Write(smallBuf, 0, 1);
                }

                fs.PrepareRawAccess();
                fs.RawAccess.ReadSector(17, 15, sectorBuf, 0);
                if (sectorBuf[DiskArc.FS.DOS.CATALOG_ENTRY_START + 0x21] != 17) {
                    throw new Exception("Size wasn't fixed: " +
                        sectorBuf[DiskArc.FS.DOS.CATALOG_ENTRY_START + 0x21]);
                }
            }
        }

        private static IFileSystem Make525Floppy(byte volNum, AppHook appHook) {
            return Helper.CreateTestImage(string.Empty, FileSystemType.DOS33, 35, 16, volNum,
                true, appHook, out MemoryStream memFile);
        }
    }
}
