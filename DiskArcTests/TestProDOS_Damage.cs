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

namespace DiskArcTests {
    /// <summary>
    /// Test behavior when various problems are encountered.  The disk images are damaged
    /// via the raw data interface.
    /// </summary>
    public class TestProDOS_Damage : ITest {
        // Confirm detection of overlapping files.
        public static void TestOverlap(AppHook appHook) {
            const string FILE1 = "File1";
            const string FILE2 = "File2";
            const string FILE3 = "File3";

            using (IFileSystem fs = Make525Floppy("Create.Test", appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();

                IFileEntry file1 = fs.CreateFile(volDir, FILE1, IFileSystem.CreateMode.File);
                Helper.PopulateFile(fs, file1, 0xcc, 8, 0);
                IFileEntry file2 = fs.CreateFile(volDir, FILE2, IFileSystem.CreateMode.File);
                Helper.PopulateFile(fs, file2, 0xdd, 16, 0);
                IFileEntry file3 = fs.CreateFile(volDir, FILE3, IFileSystem.CreateMode.File);
                Helper.PopulateFile(fs, file3, 0xee, 24, 0);

                fs.PrepareRawAccess();

                byte[] blockBuf = new byte[BLOCK_SIZE];

                // Read the first block of the volume directory.
                fs.RawAccess.ReadBlock(DiskArc.FS.ProDOS.VOL_DIR_START_BLK, blockBuf, 0);
                byte dirEntryLen = blockBuf[4 + 0x1f];
                // Copy file1's key block pointer to file2's entry.
                ushort keyBlock1 = RawData.GetU16LE(blockBuf, 4 + dirEntryLen + 0x11);
                RawData.SetU16LE(blockBuf, 4 + dirEntryLen * 2 + 0x11, keyBlock1);
                // Write the block back to disk.
                fs.RawAccess.WriteBlock(DiskArc.FS.ProDOS.VOL_DIR_START_BLK, blockBuf, 0);

                // Switch back to file mode.  Do the full scan.
                fs.PrepareFileAccess(true);
                if (fs.IsDubious) {
                    throw new Exception("FS marked as damaged (just dubious files)");
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

                // Check the file attributes.  Length comes from the directory, storage
                // size is computed based on actual usage when a "deep scan" is done.
                if (file1.DataLength == file2.DataLength) {
                    throw new Exception("EOF changed to match, but should not have");
                }
                if (file1.StorageSize != file2.StorageSize) {
                    throw new Exception("Storage size doesn't match");
                }

                try {
                    file1.AuxType = 0xccdd;
                    throw new Exception("Changed aux type on dubious file");
                } catch (IOException) {
                    // expected
                }

                using (DiskFileStream fd = fs.OpenFile(file1, IFileSystem.FileAccessMode.ReadOnly,
                        FilePart.DataFork)) {
                    blockBuf[0] = 0xff;     // make sure we're actually reading
                    fd.Read(blockBuf, 0, 1);
                    if (blockBuf[0] != 0xcc) {
                        throw new Exception("Found wrong data in file1" +
                            blockBuf[0].ToString("x2"));
                    }
                }
                using (DiskFileStream fd = fs.OpenFile(file2, IFileSystem.FileAccessMode.ReadOnly,
                        FilePart.DataFork)) {
                    blockBuf[0] = 0xff;
                    fd.Read(blockBuf, 0, 1);
                    if (blockBuf[0] != 0xcc) {
                        throw new Exception("Found wrong (original?) data in file1: " +
                            blockBuf[0].ToString("x2"));
                    }
                }
                // Confirm file3 is fine; use read-write mode to check that.
                using (DiskFileStream fd = fs.OpenFile(file3, IFileSystem.FileAccessMode.ReadWrite,
                        FilePart.DataFork)) {
                    blockBuf[0] = 0xff;
                    fd.Read(blockBuf, 0, 1);
                    if (blockBuf[0] != 0xee) {
                        throw new Exception("Found wrong data in file3: " +
                            blockBuf[0].ToString("x2"));
                    }
                }

                try {
                    fs.OpenFile(file1, IFileSystem.FileAccessMode.ReadWrite, FilePart.DataFork);
                    throw new Exception("Opened file1 read-write");
                } catch {
                    // Expected
                }

                try {
                    fs.DeleteFile(file1);
                    throw new Exception("Successfully deleted dubious file");
                } catch {
                    // Expected
                }

                // This should work.
                fs.DeleteFile(file3);
            }
        }

        public static void TestDirLoop(AppHook appHook) {
            // Create a simple infinite loop.
            using (IFileSystem fs = Make525Floppy("Dir.Loop.1", appHook)) {
                fs.PrepareRawAccess();

                // Change the "next" pointer in the second directory block to point back to first.
                byte[] blockBuf = new byte[BLOCK_SIZE];
                fs.RawAccess.ReadBlock(DiskArc.FS.ProDOS.VOL_DIR_START_BLK + 1, blockBuf, 0);
                RawData.SetU16LE(blockBuf, 2, DiskArc.FS.ProDOS.VOL_DIR_START_BLK);
                fs.RawAccess.WriteBlock(DiskArc.FS.ProDOS.VOL_DIR_START_BLK + 1, blockBuf, 0);

                fs.PrepareFileAccess(true);
                IFileEntry volDir = fs.GetVolDirEntry();
                if (!volDir.IsDamaged) {
                    throw new Exception("Damage not found");
                }
            }

            // Create three directories sub1->sub2->sub3, then change the keyblock for sub3's
            // directory entry in sub2 to point back to sub1, forming a loop.
            using (IFileSystem fs = Make525Floppy("Dir.Loop.2", appHook)) {
                const string NAME1 = "SubDir1";
                const string NAME2 = "SubDir2";
                const string NAME3 = "SubDir3";

                IFileEntry volDir = fs.GetVolDirEntry();
                IFileEntry subDir1 = fs.CreateFile(volDir, NAME1, IFileSystem.CreateMode.Directory);
                IFileEntry subDir2 = fs.CreateFile(subDir1, NAME2, IFileSystem.CreateMode.Directory);
                IFileEntry subDir3 = fs.CreateFile(subDir2, NAME3, IFileSystem.CreateMode.Directory);

                fs.PrepareRawAccess();

                byte[] blockBuf = new byte[BLOCK_SIZE];

                // Read the first block of the volume directory.
                fs.RawAccess.ReadBlock(DiskArc.FS.ProDOS.VOL_DIR_START_BLK, blockBuf, 0);
                byte dirEntryLen = blockBuf[4 + 0x1f];
                // Get the key block pointer for SubDir1.
                ushort keyBlock1 = RawData.GetU16LE(blockBuf, 4 + dirEntryLen + 0x11);

                // Read the first block of SubDir1
                fs.RawAccess.ReadBlock(keyBlock1, blockBuf, 0);
                dirEntryLen = blockBuf[4 + 0x1f];
                // Get the key block pointer for SubDir2.
                ushort keyBlock2 = RawData.GetU16LE(blockBuf, 4 + dirEntryLen + 0x11);

                // Read the first block of SubDir2
                fs.RawAccess.ReadBlock(keyBlock2, blockBuf, 0);
                dirEntryLen = blockBuf[4 + 0x1f];
                // Replace the key block pointer for SubDir3 with the value for SubDir1.
                ushort keyBlock3 = RawData.GetU16LE(blockBuf, 4 + dirEntryLen + 0x11);
                Debug.WriteLine("Replacing key block " + keyBlock3 + " with " + keyBlock1);
                RawData.SetU16LE(blockBuf, 4 + dirEntryLen + 0x11, keyBlock1);
                fs.RawAccess.WriteBlock(keyBlock2, blockBuf, 0);

                // Scan the volume.
                fs.PrepareFileAccess(true);

                // Verify the altered directory structure.  The code will mark the deepest
                // directory damaged but leave the rest intact, so we will just appear to
                // have a very deep hierarchy (currently 256).
                volDir = fs.GetVolDirEntry();
                subDir1 = fs.FindFileEntry(volDir, NAME1);
                subDir2 = fs.FindFileEntry(subDir1, NAME2);
                subDir3 = fs.FindFileEntry(subDir2, NAME3);
                IFileEntry subDir2a = fs.FindFileEntry(subDir3, NAME2);
                IFileEntry subDir3a = fs.FindFileEntry(subDir2a, NAME3);

                // It is the same dir, however, so the file overlap detector should go nuts.
                if (!subDir1.IsDubious || !subDir2.IsDubious || !subDir3.IsDubious) {
                    throw new Exception("failed to detect overlap");
                }
            }
        }

        // Replace a key block pointer and an index block entry with invalid block numbers.
        public static void TestInvalidBlockRef(AppHook appHook) {
            using (IFileSystem fs = Make525Floppy("BadRef", appHook)) {
                const string NAME1 = "File1";
                const string NAME2 = "File2";

                IFileEntry volDir = fs.GetVolDirEntry();
                IFileEntry entry1 = fs.CreateFile(volDir, NAME1, IFileSystem.CreateMode.File);
                IFileEntry entry2 = fs.CreateFile(volDir, NAME2, IFileSystem.CreateMode.File);
                Helper.PopulateFile(fs, entry2, 0xff, 8, 0);

                fs.PrepareRawAccess();

                byte[] blockBuf = new byte[BLOCK_SIZE];

                // Read the first block of the volume directory.
                fs.RawAccess.ReadBlock(DiskArc.FS.ProDOS.VOL_DIR_START_BLK, blockBuf, 0);
                byte dirEntryLen = blockBuf[4 + 0x1f];
                // Set the key block on the first entry to a value outside the volume.
                RawData.SetU16LE(blockBuf, 4 + dirEntryLen + 0x11, 0x1000);
                fs.RawAccess.WriteBlock(DiskArc.FS.ProDOS.VOL_DIR_START_BLK, blockBuf, 0);

                // Read the index block of the second file.
                ushort keyBlock2 = RawData.GetU16LE(blockBuf, 4 + dirEntryLen * 2 + 0x11);
                fs.RawAccess.ReadBlock(keyBlock2, blockBuf, 0);
                // Sabotage the second entry.
                blockBuf[1] = blockBuf[257] = 0xcc;
                fs.RawAccess.WriteBlock(keyBlock2, blockBuf, 0);

                fs.PrepareFileAccess(true);
                volDir = fs.GetVolDirEntry();
                entry1 = fs.FindFileEntry(volDir, NAME1);
                entry2 = fs.FindFileEntry(volDir, NAME2);

                if (!entry1.IsDamaged || !entry2.IsDamaged) {
                    throw new Exception("Damage not discovered: " + entry1.IsDamaged +
                        " / " + entry2.IsDamaged);
                }
            }
        }

        public static void TestIndexJunk(AppHook appHook) {
            const string FILE_NAME = "TEST.FILE";

            using (IFileSystem fs = Make525Floppy("JunkInTrunk", appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();
                IFileEntry file = fs.CreateFile(volDir, FILE_NAME, IFileSystem.CreateMode.File);
                Helper.PopulateFile(fs, file, 0xff, 8, 0);

                fs.PrepareRawAccess();

                byte[] blockBuf = new byte[BLOCK_SIZE];

                // Read the first block of the volume directory.
                fs.RawAccess.ReadBlock(DiskArc.FS.ProDOS.VOL_DIR_START_BLK, blockBuf, 0);
                byte dirEntryLen = blockBuf[4 + 0x1f];
                // Get the key (index) block, read it.
                ushort keyBlock = RawData.GetU16LE(blockBuf, 4 + dirEntryLen + 0x11);
                fs.RawAccess.ReadBlock(keyBlock, blockBuf, 0);
                // Add some junk at the end.
                blockBuf[511] = 0xff;
                fs.RawAccess.WriteBlock(keyBlock, blockBuf, 0);

                fs.PrepareFileAccess(true);

                // File should be marked "dubious".
                volDir = fs.GetVolDirEntry();
                file = fs.FindFileEntry(volDir, FILE_NAME);
                if (!file.IsDubious || file.IsDamaged) {
                    throw new Exception("Unexpected state: dub=" + file.IsDubious +
                        ", dam=" + file.IsDamaged);
                }
            }
        }

        public static void TestStorageSizeFix(AppHook appHook) {
            const string FILE_NAME = "TEST.FILE";

            using (IFileSystem fs = Make525Floppy("Storage.Size", appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();
                IFileEntry file = fs.CreateFile(volDir, FILE_NAME, IFileSystem.CreateMode.File);
                Helper.PopulateFile(fs, file, 0xff, 8, 0);

                fs.PrepareRawAccess();

                byte[] blockBuf = new byte[BLOCK_SIZE];
                // Read the first block of the volume directory.
                fs.RawAccess.ReadBlock(DiskArc.FS.ProDOS.VOL_DIR_START_BLK, blockBuf, 0);
                byte dirEntryLen = blockBuf[4 + 0x1f];
                // Mangle the storage size.
                RawData.SetU16LE(blockBuf, 4 + dirEntryLen + 0x13, 0x1234);
                fs.RawAccess.WriteBlock(DiskArc.FS.ProDOS.VOL_DIR_START_BLK, blockBuf, 0);

                // Should not be a warning or error.
                fs.PrepareFileAccess(true);
                Helper.CheckNotes(fs, 0, 0);

                // Reopen the filesystem, without scan.  Should see the damaged value.
                // (This is a little bogus, since we don't guarantee that we *won't* fix
                // the storage size even without a scan.)
                fs.PrepareRawAccess();
                fs.PrepareFileAccess(false);
                volDir = fs.GetVolDirEntry();
                file = fs.FindFileEntry(volDir, FILE_NAME);
                if (file.StorageSize != 0x1234 * BLOCK_SIZE) {
                    throw new Exception("Problem was fixed?");
                }

                // Reopen the filesystem, with scan.  Should see the fixed value.
                fs.PrepareRawAccess();
                fs.PrepareFileAccess(true);
                volDir = fs.GetVolDirEntry();
                file = fs.FindFileEntry(volDir, FILE_NAME);
                if (file.StorageSize != 9 * BLOCK_SIZE) {
                    throw new Exception("Problem not fixed by scanner");
                }

                // Change the filetype, to cause file attributes to be written.
                file.FileType = FileAttribs.FILE_TYPE_SYS;
                file.SaveChanges();

                // Reopen the filesystem, without scan.  Should see the fixed value.
                fs.PrepareRawAccess();
                fs.PrepareFileAccess(false);
                volDir = fs.GetVolDirEntry();
                file = fs.FindFileEntry(volDir, FILE_NAME);
                if (file.StorageSize != 9 * BLOCK_SIZE) {
                    throw new Exception("Problem not fixed (size=" +
                        (file.StorageSize / BLOCK_SIZE) + " blocks)");
                }
            }
        }

        #region Utilities

        private static IFileSystem Make525Floppy(string volName, AppHook appHook) {
            return Helper.CreateTestImage(volName, FileSystemType.ProDOS, 280, appHook,
                out MemoryStream memFile);
        }
        private static IFileSystem MakeBigHD(string volName, AppHook appHook) {
            return Helper.CreateTestImage(volName, FileSystemType.ProDOS, 65535, appHook,
                out MemoryStream memFile);
        }

        #endregion Utilities
    }
}
