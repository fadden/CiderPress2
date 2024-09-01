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
using System.IO;
using System.Text;

using CommonUtil;
using DiskArc;
using static DiskArc.Defs;
using static DiskArc.IFileSystem;

namespace DiskArcTests {
    /// <summary>
    /// DOS filesystem I/O tests.
    /// </summary>
    public class TestDOS_FileIO : ITest {
        private const bool DO_SCAN = true;

        public static void TestSimpleWrite(AppHook appHook) {
            using (IFileSystem fs = Make525Floppy(10, appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();

                byte[] wrBuf = new byte[1100];
                for (int i = 0; i < wrBuf.Length; i++) {
                    wrBuf[i] = (byte)i;
                }

                // Make a short file.  Leave the file type set to default.
                string name = "FOO";
                IFileEntry entry = fs.CreateFile(volDir, name, CreateMode.File);
                DiskFileStream fd = fs.OpenFile(entry, FileAccessMode.ReadWrite, FilePart.DataFork);
                fd.Write(wrBuf, 0, wrBuf.Length);
                fd.Close();     // explicit close

                try {
                    fd.Write(wrBuf, 0, wrBuf.Length);
                    throw new Exception("Wrote to closed file");
                } catch (ObjectDisposedException) {
                    // expected
                }

                // The length isn't stored explicitly, so will be rounded up to a sector boundary.
                int expectedLen = (wrBuf.Length + (SECTOR_SIZE - 1)) & ~(SECTOR_SIZE - 1);
                if (entry.DataLength != expectedLen) {
                    throw new Exception("Bad length #1: " + entry.DataLength + ", expected " +
                        expectedLen);
                }

                fs.PrepareRawAccess();
                fs.PrepareFileAccess(DO_SCAN);
                Helper.CheckNotes(fs, 0, 0);

                volDir = fs.GetVolDirEntry();
                entry = fs.FindFileEntry(volDir, name);

                // Read data back, compare to original.
                byte[] rdBuf = new byte[wrBuf.Length];
                using (fd = fs.OpenFile(entry, FileAccessMode.ReadOnly, FilePart.DataFork)) {
                    fd.Read(rdBuf, 0, rdBuf.Length);
                    if (!RawData.CompareBytes(wrBuf, rdBuf, rdBuf.Length)) {
                        throw new Exception("Read data doesn't match written");
                    }
                }   // implicit close
            }
        }

        // Test zero-length 'B' files.
        public static void TestCreateB(AppHook appHook) {
            const string FILENAME = "BINNY";

            using (IFileSystem fs = Make525Floppy(10, appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();
                IFileEntry entry = fs.CreateFile(volDir, FILENAME, CreateMode.File);
                entry.FileType = FileAttribs.FILE_TYPE_BIN;
                entry.AuxType = 0x12ab;
                entry.SaveChanges();

                fs.PrepareRawAccess();
                fs.PrepareFileAccess(true);
                volDir = fs.GetVolDirEntry();
                entry = fs.FindFileEntry(volDir, FILENAME);

                // A newly-created file doesn't have the first sector allocated, which means
                // there's nowhere to put the 'B' length or load address (aux type).  So the
                // file type should be set but the aux type should be zero.
                if (entry.FileType != FileAttribs.FILE_TYPE_BIN || entry.AuxType != 0) {
                    throw new Exception("Unexpected type values");
                }
                if (entry.DataLength != 0) {
                    throw new Exception("Unexpected length");
                }

                // Cause the first sector to be allocated by writing zero bytes.
                using (DiskFileStream stream = fs.OpenFile(entry, FileAccessMode.ReadWrite,
                        FilePart.DataFork)) {
                    stream.Write(new byte[0], 0, 0);
                }
                entry.AuxType = 0xab12;
                entry.SaveChanges();

                fs.PrepareRawAccess();
                fs.PrepareFileAccess(true);
                Helper.CheckNotes(fs, 0, 0);
                volDir = fs.GetVolDirEntry();
                entry = fs.FindFileEntry(volDir, FILENAME);

                if (entry.FileType != FileAttribs.FILE_TYPE_BIN || entry.AuxType != 0xab12) {
                    throw new Exception("Aux type didn't stick");
                }
                if (entry.DataLength != 0) {
                    throw new Exception("Unexpected length");
                }

                // Test deletion of a 'B' file, which have special handling.
                fs.DeleteFile(entry);
            }
        }

        public static void TestWriteB(AppHook appHook) {
            using (IFileSystem fs = Make525Floppy(10, appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();

                byte[] wrBuf = new byte[1100];
                for (int i = 0; i < wrBuf.Length; i++) {
                    wrBuf[i] = (byte)i;
                }

                string name = "FOO";
                IFileEntry entry = fs.CreateFile(volDir, name, CreateMode.File);

                // Change the file's type to 'B', and open in "cooked" mode.
                entry.FileType = FileAttribs.FILE_TYPE_BIN;
                using (DiskFileStream fd = fs.OpenFile(entry, FileAccessMode.ReadWrite,
                        FilePart.DataFork)) {
                    for (int i = 0; i < wrBuf.Length; i++) {
                        fd.Write(wrBuf, i, 1);
                    }
                }

                // Length should match exactly.
                if (entry.DataLength != wrBuf.Length) {
                    throw new Exception("Lengths don't match: got " + entry.DataLength +
                        ", expected " + wrBuf.Length);
                }
                // Should be 5 data sectors + 1 T/S.
                if (entry.StorageSize != 6 * SECTOR_SIZE) {
                    throw new Exception("Unexpected storage size " + entry.StorageSize);
                }

                // Read data back, compare to original.
                byte[] rdBuf = new byte[wrBuf.Length];
                using (DiskFileStream fd = fs.OpenFile(entry, FileAccessMode.ReadOnly,
                        FilePart.DataFork)) {
                    for (int i = 0; i < rdBuf.Length; i++) {
                        int actual = fd.Read(rdBuf, i, 1);
                        if (actual != 1) {
                            throw new Exception("Single-byte read failed");
                        }
                    }
                    if (!RawData.CompareBytes(wrBuf, rdBuf, rdBuf.Length)) {
                        throw new Exception("Read data doesn't match written");
                    }
                    if (fd.Read(rdBuf, 0, 1) != 0) {
                        throw new Exception("Read past end of file in cooked mode");
                    }
                }

                // Update the load address (aux type).
                entry.AuxType = 0x1234;
                entry.SaveChanges();

                // Re-scan the filesystem.
                fs.PrepareRawAccess();
                fs.PrepareFileAccess(DO_SCAN);
                Helper.CheckNotes(fs, 0, 0);
                volDir = fs.GetVolDirEntry();
                entry = fs.FindFileEntry(volDir, name);

                // Edit the length and load address in raw mode.
                using (DiskFileStream fd = fs.OpenFile(entry, FileAccessMode.ReadWrite,
                        FilePart.RawData)) {
                    fd.Read(rdBuf, 0, 4);
                    if (RawData.GetU16LE(rdBuf, 0) != 0x1234 ||
                            RawData.GetU16LE(rdBuf, 2) != rdBuf.Length) {
                        throw new Exception("Unexpected data in first sector");
                    }

                    RawData.SetU16LE(rdBuf, 0, 0x5678);
                    RawData.SetU16LE(rdBuf, 2, 0x0111);
                    fd.Seek(0, SeekOrigin.Begin);
                    fd.Write(rdBuf, 0, 4);
                }

                if (entry.AuxType != 0x5678) {
                    throw new Exception("Bin load addr change not picked up");
                }
                if (entry.DataLength != 0x0111) {
                    throw new Exception("Bin length change not picked up (len=" +
                        entry.DataLength + ")");
                }
                if (entry.StorageSize != 6 * SECTOR_SIZE) {
                    throw new Exception("Storage length changed");
                }

                // Make sure the changes were permanent, not just held in local storage.
                fs.PrepareRawAccess();
                fs.PrepareFileAccess(DO_SCAN);
                Helper.CheckNotes(fs, 0, 0);
                if (entry.AuxType != 0x5678) {
                    throw new Exception("Bin load addr change was lost");
                }
                if (entry.DataLength != 0x0111) {
                    throw new Exception("Bin length change was lost");
                }
            }
        }

        public static void TestWriteI(AppHook appHook) {
            using (IFileSystem fs = Make525Floppy(10, appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();
                string name = "INT!GER";
                IFileEntry entry = fs.CreateFile(volDir, name, CreateMode.File);

                byte[] buf = new byte[SECTOR_SIZE];
                using (DiskFileStream fd = fs.OpenFile(entry, FileAccessMode.ReadWrite,
                        FilePart.RawData)) {
                    // Set the length to 1, and set one byte.
                    RawData.SetU16LE(buf, 0, 1);
                    buf[2] = 0xcc;
                    fd.Write(buf, 0, 3);
                }

                if (entry.DataLength != SECTOR_SIZE) {
                    throw new Exception("Unexpected len " + entry.DataLength);
                }
                // Change the type to Integer BASIC, length should change immediately.
                entry.FileType = FileAttribs.FILE_TYPE_INT;
                if (entry.DataLength != 1) {
                    throw new Exception("Int length not picked up #1 (len=" +
                        entry.DataLength + ")");
                }
                entry.SaveChanges();

                // Check filesystem, verify changes stuck.
                fs.PrepareRawAccess();
                fs.PrepareFileAccess(DO_SCAN);
                Helper.CheckNotes(fs, 0, 0);
                volDir = fs.GetVolDirEntry();
                entry = fs.FindFileEntry(volDir, name);

                if (entry.FileType != FileAttribs.FILE_TYPE_INT) {
                    throw new Exception("Incorrect file type");
                }
                using (DiskFileStream fd = fs.OpenFile(entry, FileAccessMode.ReadOnly,
                        FilePart.DataFork)) {
                    int actual = fd.Read(buf, 0, buf.Length);
                    if (actual != 1 || buf[0] != 0xcc) {
                        throw new Exception("Got more than one byte from int file");
                    }
                }

                using (DiskFileStream fd = fs.OpenFile(entry, FileAccessMode.ReadWrite,
                        FilePart.RawData)) {
                    // Set oversized length.
                    RawData.SetU16LE(buf, 0, 0x3000);
                    fd.Write(buf, 0, 3);
                }
                if (entry.DataLength != 0x3000) {
                    throw new Exception("Int length not picked up #2 (len=" +
                        entry.DataLength + ")");
                }
                // Should get one warning about the oversized 'I' length.
                fs.PrepareRawAccess();
                fs.PrepareFileAccess(DO_SCAN);
                Helper.CheckNotes(fs, 1, 0);
            }
        }

        public static void TestWriteT(AppHook appHook) {
            using (IFileSystem fs = Make525Floppy(10, appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();
                string name = "Test Text File";
                IFileEntry entry = fs.CreateFile(volDir, name, IFileSystem.CreateMode.File);
                entry.FileType = FileAttribs.FILE_TYPE_TXT;

                string text1 = "No man is an island, entire of itself; every man is a piece of " +
                    "the continent, a part of the main.  If a clod be washed away by the sea, " +
                    "Europe is the less, as well as if a promontory were; as well as if a manor " +
                    "of they friend's or of thine own were.  Any man's death diminishes me, " +
                    "because I am involved in mankind.  And therefore never send to know for " +
                    "whom the bell tolls; it tolls for thee.";
                byte[] bin1 = Encoding.ASCII.GetBytes(text1);
                string text2 = "To be, that is the question: whether 'tis nobler in the mind " +
                    "to suffer the slings and arrows of outrageous fortune, or take arms " +
                    "against a sea of troubles, and by opposing, end them: to die, to sleep " +
                    "no more; and by a sleep to say we end the heart-ache, and the thousand " +
                    "natural shocks that flesh is heir to?  'Tis a consummation devoutly to " +
                    "be wished.";
                byte[] bin2 = Encoding.ASCII.GetBytes(text2);

                using (DiskFileStream fd = fs.OpenFile(entry, FileAccessMode.ReadWrite,
                        FilePart.RawData)) {
                    fd.Write(bin1, 0, bin1.Length);
                    fd.Flush();
                    if (entry.DataLength != bin1.Length) {
                        throw new Exception("Text length not recomputed on flush #1: " +
                            entry.DataLength);
                    }
                }
                using (DiskFileStream fd = fs.OpenFile(entry, FileAccessMode.ReadWrite,
                        FilePart.DataFork)) {
                    fd.Seek(0, SeekOrigin.End);
                    fd.Write(bin2, 0, bin2.Length);
                    fd.Flush();
                    if (entry.DataLength != bin1.Length + bin2.Length) {
                        throw new Exception("Text length not recomputed on flush #2: " +
                            entry.DataLength);
                    }
                }
                using (DiskFileStream fd = fs.OpenFile(entry, FileAccessMode.ReadWrite,
                        FilePart.DataFork)) {
                    // Write $00 8 bytes back from the end.
                    byte[] smallBuf = new byte[1];
                    fd.Seek(-8, SeekOrigin.End);
                    fd.Write(smallBuf, 0, 1);
                    fd.Flush();
                    if (entry.DataLength != bin1.Length + bin2.Length - 8) {
                        throw new Exception("Text length not recomputed on flush #3: " +
                            entry.DataLength);
                    }
                }

                fs.PrepareRawAccess();
                fs.PrepareFileAccess(DO_SCAN);
                Helper.CheckNotes(fs, 0, 0);
            }
        }

        public static void TestSeek(AppHook appHook) {
            using (IFileSystem fs = Make525Floppy(10, appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();
                string name = "Test File";
                IFileEntry entry = fs.CreateFile(volDir, name, CreateMode.File);
                entry.FileType = FileAttribs.FILE_TYPE_F4;  // BB, should work same as "raw"

                byte[] wrBuf = new byte[1100];
                for (int i = 0; i < wrBuf.Length; i++) {
                    wrBuf[i] = (byte)i;
                }
                byte[] rdBuf = new byte[wrBuf.Length];

                using (DiskFileStream fd = fs.OpenFile(entry, FileAccessMode.ReadWrite,
                        FilePart.DataFork)) {
                    // Write back to front, in 100-byte chunks.
                    for (int i = 1100 - 100; i >= 0; i -= 100) {
                        fd.Seek(i, SeekOrigin.Begin);
                        fd.Write(wrBuf, i, 100);
                    }

                    fd.Seek(0, SEEK_ORIGIN_DATA);
                    for (int i = 1100 - 50; i >= 0; i -= 50) {
                        fd.Seek(i, SeekOrigin.Begin);
                        fd.Read(rdBuf, i, 50);
                    }
                }

                if (!RawData.CompareBytes(rdBuf, wrBuf, wrBuf.Length)) {
                    throw new Exception("Read/write mismatch");
                }

                fs.PrepareRawAccess();
                fs.PrepareFileAccess(DO_SCAN);
                Helper.CheckNotes(fs, 0, 0);
            }
        }

        public static void TestSparse(AppHook appHook) {
            using (IFileSystem fs = Make525Floppy(10, appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();
                string name = "Sparsely";
                IFileEntry entry = fs.CreateFile(volDir, name, CreateMode.File);
                // In normal DOS usage, only 'T' files are sparse, so lets do that.
                entry.FileType = FileAttribs.FILE_TYPE_TXT;

                byte[] smallBuf = new byte[] { 0xcc };

                using (DiskFileStream fd = fs.OpenFile(entry, FileAccessMode.ReadWrite,
                        FilePart.RawData)) {
                    fd.Seek(SECTOR_SIZE * 4, SeekOrigin.Begin);
                    fd.Write(smallBuf, 0, 1);
                    fd.Seek(SECTOR_SIZE * 10, SeekOrigin.Begin);
                    fd.Write(smallBuf, 0, 1);
                }

                if (entry.DataLength != 0) {
                    throw new Exception("Text file size changed inappropriately");
                }
                if (entry.StorageSize != 3 * SECTOR_SIZE) {
                    throw new Exception("Incorrect storage size: " + entry.StorageSize);
                }

                // Must use "raw data" mode for DATA/HOLE seeks.
                using (DiskFileStream fd = fs.OpenFile(entry, FileAccessMode.ReadWrite,
                       FilePart.RawData)) {
                    long offset = fd.Seek(0, SEEK_ORIGIN_HOLE);
                    if (offset != 0) {
                        throw new Exception("Bad hole seek #1: " + offset);
                    }
                    offset = fd.Seek(offset, SEEK_ORIGIN_DATA);
                    if (offset != SECTOR_SIZE * 4) {
                        throw new Exception("Bad data seek #1: " + offset);
                    }
                    offset = fd.Seek(offset, SEEK_ORIGIN_HOLE);
                    if (offset != SECTOR_SIZE * 5) {
                        throw new Exception("Bad hole seek #2: " + offset);
                    }
                    offset = fd.Seek(offset, SEEK_ORIGIN_DATA);
                    if (offset != SECTOR_SIZE * 10) {
                        throw new Exception("Bad data seek #2: " + offset);
                    }
                    offset = fd.Seek(offset, SEEK_ORIGIN_HOLE);
                    if (offset != SECTOR_SIZE * 11) {
                        throw new Exception("Bad hole seek #3: " + offset);
                    }
                    if (offset != fd.Seek(0, SeekOrigin.End)) {
                        throw new Exception("Bad end seek: " + offset);
                    }
                }

                fs.PrepareRawAccess();
                fs.PrepareFileAccess(DO_SCAN);
                Helper.CheckNotes(fs, 0, 0);
            }
        }

        public static void TestBigSparse(AppHook appHook) {
            using (IFileSystem fs = Make525Floppy(11, appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();
                string name = "BIG SPARSE TEXT";
                // Use extension method to create as 'T' file.
                IFileEntry entry = fs.CreateFile(volDir, name, CreateMode.File,
                    FileAttribs.FILE_TYPE_TXT);
                byte[] smallBuf = new byte[] { 0xcc };
                // Each T/S list spans 122 sectors.  We skip the first 512 bytes to ensure
                // that skipping the start works, then leave the entirety of the regions at
                // 244 and 366 empty to ensure that skipping multiple T/S lists works.
                int[] sSparseRecs = new int[] { 2, 8, 122, 488 };
                using (DiskFileStream fd = fs.OpenFile(entry, FileAccessMode.ReadWrite,
                        FilePart.RawData)) {
                    foreach (int num in sSparseRecs) {
                        fd.Seek(SECTOR_SIZE * num, SeekOrigin.Begin);
                        fd.Write(smallBuf, 0, 1);
                    }
                }

                // Should have zero length (as sequential text file).
                Helper.ExpectLong(0, entry.DataLength, "incorrect data length");
                // One sector for each of the 4 data blocks, and one for each of the 5 T/S
                // lists (starting 0, 122, 244, 366, 488).
                Helper.ExpectLong(9 * SECTOR_SIZE, entry.StorageSize, "incorrect storage size");

                using (DiskFileStream fd = fs.OpenFile(entry, FileAccessMode.ReadOnly,
                        FilePart.RawData)) {
                    long fileLength = fd.Seek(0, SeekOrigin.End);
                    fd.Seek(0, SeekOrigin.Begin);
                    Helper.ExpectLong((488 + 1) * SECTOR_SIZE, fileLength,
                        "unexpected raw data length: " + fileLength);

                    long fileOffset = 0;
                    int expIndex = 0;
                    while (fileOffset < fileLength) {
                        long holePos = fd.Seek(fileOffset, SEEK_ORIGIN_HOLE);
                        if (holePos != fileOffset) {
                            throw new Exception("Should have been in hole at " + fileOffset);
                        }

                        long dataPos = fd.Seek(fileOffset, SEEK_ORIGIN_DATA);
                        if (dataPos == fileOffset) {
                            throw new Exception("Failed to move to data");
                        }
                        if (dataPos != sSparseRecs[expIndex] * SECTOR_SIZE) {
                            throw new Exception("Did not find data at expected offset: exp=" +
                                sSparseRecs[expIndex] * SECTOR_SIZE + " act=" + dataPos);
                        }
                        expIndex++;

                        if (fd.ReadByte() != 0xcc) {
                            throw new Exception("Did not find expected byte value at: " + dataPos);
                        }

                        holePos = fd.Seek(dataPos, SEEK_ORIGIN_HOLE);
                        if (holePos != dataPos + SECTOR_SIZE) {
                            throw new Exception("Failed to move to next hole");
                        }
                        fileOffset = holePos;
                    }
                }

                fs.PrepareRawAccess();
                fs.PrepareFileAccess(DO_SCAN);
                Helper.CheckNotes(fs, 0, 0);
            }
        }

        public static void TestHugeSparse(AppHook appHook) {
            // Need ~135KB, so a 140KB volume won't work.  Create a 200KB volume.
            using (IFileSystem fs = Helper.CreateTestImage(string.Empty, FileSystemType.DOS33,
                    50, 16, 20, false, appHook, out MemoryStream memFile)) {

                byte[] smallBuf = new byte[] { 0xcc };

                IFileEntry volDir = fs.GetVolDirEntry();
                string name = "SUPER*SPARSE\u2407";

                // Create an absurdly sparse file.
                IFileEntry entry = fs.CreateFile(volDir, name, CreateMode.File);
                using (DiskFileStream fd = fs.OpenFile(entry, FileAccessMode.ReadWrite,
                        FilePart.RawData)) {
                    fd.Seek(DiskArc.FS.DOS.MAX_FILE_LEN - 1, SeekOrigin.Begin);
                    fd.Write(smallBuf, 0, 1);
                }

                const int maxLen = DiskArc.FS.DOS.MAX_FILE_LEN;
                const int bytesPerTS = DiskArc.FS.DOS.MAX_TS_PER_TSLIST * SECTOR_SIZE;

                int dataSct = 1;                        // should need one actual data sector...
                int ovhdSct = (maxLen + bytesPerTS - 1) / bytesPerTS;   // ...plus 538 of these
                if (entry.StorageSize != (dataSct + ovhdSct) * SECTOR_SIZE) {
                    throw new Exception("Unexpected storage size: " + entry.StorageSize);
                }
                if (entry.DataLength != DiskArc.FS.DOS.MAX_FILE_LEN) {
                    throw new Exception("Unexpected data length: " + entry.DataLength);
                }

                fs.PrepareRawAccess();
                fs.PrepareFileAccess(DO_SCAN);
                Helper.CheckNotes(fs, 0, 0);

                // Delete file and verify that space has been reclaimed.
                volDir = fs.GetVolDirEntry();
                entry = fs.FindFileEntry(volDir, name);
                fs.DeleteFile(entry);

                if (fs.FreeSpace != (50 - 2) * 16 * SECTOR_SIZE) {
                    throw new Exception("Not all space reclaimed: " + fs.FreeSpace);
                }

                fs.PrepareRawAccess();
                fs.PrepareFileAccess(DO_SCAN);
                Helper.CheckNotes(fs, 0, 0);
            }
        }

        public static void TestBigB(AppHook appHook) {
            using (IFileSystem fs = Make525Floppy(10, appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();
                string name = "BIGLY";
                IFileEntry entry = fs.CreateFile(volDir, name, CreateMode.File);
                entry.FileType = FileAttribs.FILE_TYPE_BIN;
                entry.AuxType = 0x4321;     // must be set after file type

                using (DiskFileStream fd = fs.OpenFile(entry, FileAccessMode.ReadWrite,
                        FilePart.DataFork)) {
                    fd.Write(sFullPattern, 0, DiskArc.FS.DOS.MAX_IAB_FILE_LEN);
                }

                if (entry.DataLength != DiskArc.FS.DOS.MAX_IAB_FILE_LEN) {
                    throw new Exception("Incorrect DataLength: " + entry.DataLength);
                }
                if (entry.StorageSize != (3 + 257) * SECTOR_SIZE) {
                    // 3 T/S list sectors, 256 data sectors... plus one extra sector because
                    // the four bytes of address/length at the start push us into the next.
                    throw new Exception("Incorrect storage size: " + entry.StorageSize);
                }

                fs.PrepareRawAccess();
                fs.PrepareFileAccess(DO_SCAN);
                Helper.CheckNotes(fs, 0, 0);

                volDir = fs.GetVolDirEntry();
                entry = fs.FindFileEntry(volDir, name);
                if (entry.AuxType != 0x4321) {
                    throw new Exception("Aux type didn't take");
                }
                if (entry.DataLength != DiskArc.FS.DOS.MAX_IAB_FILE_LEN) {
                    throw new Exception("Incorrect DataLength #2: " + entry.DataLength);
                }
                if (entry.StorageSize != (3 + 257) * SECTOR_SIZE) {
                    throw new Exception("Incorrect storage size #2: " + entry.StorageSize);
                }

                using (DiskFileStream fd = fs.OpenFile(entry, FileAccessMode.ReadOnly,
                        FilePart.DataFork)) {
                    byte[] rdBuf = new byte[DiskArc.FS.DOS.MAX_IAB_FILE_LEN];
                    int actual = fd.Read(rdBuf, 0, DiskArc.FS.DOS.MAX_IAB_FILE_LEN);
                    if (actual != rdBuf.Length) {
                        throw new Exception("Partial read: " + actual);
                    }

                    if (!RawData.CompareBytes(rdBuf, sFullPattern, sFullPattern.Length)) {
                        throw new Exception("Read data doesn't match");
                    }
                }
            }
        }

        // There are 560 - (4 * 16) = 496 sectors available on a bootable 140K disk image.
        // We need 1 T/S sector for every 122 data sectors.  We should be able to write
        // (122 * 4) + 3 = 491 sectors, with 5 T/S sectors of overhead, before
        // the disk fills.
        private const int MAX_525_FILE_SECTORS = 491;

        public static void TestDiskFull(AppHook appHook) {
            using (IFileSystem fs = Make525Floppy(10, appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();
                string name = "TOOBIG2FIT!";
                IFileEntry entry = fs.CreateFile(volDir, name, CreateMode.File);

                byte[] sectorBuf = new byte[SECTOR_SIZE];
                using (DiskFileStream fd = fs.OpenFile(entry, FileAccessMode.ReadWrite,
                        FilePart.RawData)) {
                    for (int i = 0; i < MAX_525_FILE_SECTORS; i++) {
                        fd.Write(sectorBuf, 0, SECTOR_SIZE);
                    }

                    // Next one should fail.
                    try {
                        fd.Write(sectorBuf, 0, SECTOR_SIZE);
                        throw new Exception("Over-filled disk; now at " + fs.FreeSpace);
                    } catch (DiskFullException) {
                        // expected
                    }
                }

                if (fs.FreeSpace != 0) {
                    throw new Exception("Disk full but isn't: " + fs.FreeSpace);
                }

                fs.PrepareRawAccess();
                fs.PrepareFileAccess(DO_SCAN);
                Helper.CheckNotes(fs, 0, 0);
                if (fs.FreeSpace != 0) {
                    throw new Exception("Disk no longer full: " + fs.FreeSpace);
                }

                // For the next test, we want the disk to be full at a point where we're trying
                // to allocate two sectors for one call (data sector and new T/S list sector).
                // That happens when we write the 123rd sector, so we want to have 124 sectors
                // free.  Reducing the size of the file by 123 data sectors will free up a total
                // of 124.
                volDir = fs.GetVolDirEntry();
                entry = fs.FindFileEntry(volDir, name);
                long beforeLen = entry.DataLength;
                using (DiskFileStream fd = fs.OpenFile(entry, FileAccessMode.ReadWrite,
                        FilePart.RawData)) {
                    fd.SetLength(entry.DataLength - 123 * SECTOR_SIZE);

                    if (fs.FreeSpace != 124 * SECTOR_SIZE) {
                        throw new Exception("Test setup fail #1: " + (fs.FreeSpace / SECTOR_SIZE));
                    }
                }

                string name2 = "MORE";
                IFileEntry entry2 = fs.CreateFile(volDir, name2, CreateMode.File);
                using (DiskFileStream fd = fs.OpenFile(entry2, FileAccessMode.ReadWrite,
                        FilePart.RawData)) {
                    for (int i = 0; i < 122; i++) {
                        fd.Write(sectorBuf, 0, SECTOR_SIZE);
                    }
                    fd.Flush();
                    if (entry2.StorageSize != 123 * SECTOR_SIZE) {
                        throw new Exception("File mis-allocated: " + entry2.StorageSize);
                    }
                    if (fs.FreeSpace != 1 * SECTOR_SIZE) {
                        throw new Exception("Test setup fail #2: " + (fs.FreeSpace / SECTOR_SIZE));
                    }

                    // The next sector we write should try to allocate two sectors.
                    try {
                        fd.Write(sectorBuf, 0, SECTOR_SIZE);
                        throw new Exception("Sector write only allocated one");
                    } catch (DiskFullException) {
                        // expected
                    }
                    if (fs.FreeSpace != 1 * SECTOR_SIZE) {
                        throw new Exception("Sector not released after failed write");
                    }
                }

                fs.PrepareRawAccess();
                fs.PrepareFileAccess(DO_SCAN);
                Helper.CheckNotes(fs, 0, 0);
            }
        }

        // Test file truncation with SetLength.
        public static void TestSetLengthTrunc(AppHook appHook) {
            using (IFileSystem fs = Make525Floppy(11, appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();

                // Test a medium-sized 'B' file, in "cooked" mode.
                IFileEntry file1 = fs.CreateFile(volDir, "B1", CreateMode.File);
                file1.FileType = FileAttribs.FILE_TYPE_BIN;

                using (DiskFileStream fd = fs.OpenFile(file1, FileAccessMode.ReadWrite,
                        FilePart.DataFork)) {
                    fd.Write(sFullPattern, 0, DiskArc.FS.DOS.MAX_IAB_FILE_LEN);
                    fd.Flush();
                    // 256 data sectors + 1 because of 4-byte header, 3 T/S list sectors
                    if (file1.DataLength != DiskArc.FS.DOS.MAX_IAB_FILE_LEN ||
                            file1.StorageSize != (256 + 4) * SECTOR_SIZE) {
                        throw new Exception("Incorrect initial file size #1");
                    }

                    // Cut it down to just under 8KB.
                    fd.SetLength(0x1ffd);
                    if (file1.DataLength != 0x1ffd || file1.StorageSize != 34 * SECTOR_SIZE) {
                        throw new Exception("Incorrect post-set size #1a");
                    }

                    // Cut four bytes (from the hi-res screen "hole") to make it fit.
                    fd.SetLength(0x1ffc);
                    if (file1.DataLength != 0x1ffc || file1.StorageSize != 33 * SECTOR_SIZE) {
                        throw new Exception("Incorrect post-set size #1b");
                    }

                    // Full truncation.  Does not remove the length/address words.
                    fd.SetLength(0);
                    if (file1.DataLength != 0 || file1.StorageSize != 2 * SECTOR_SIZE) {
                        throw new Exception("Incorrect post-set size #1c");
                    }
                }

                // Test a medium-sized 'B' file, in "raw" mode.
                IFileEntry file2 = fs.CreateFile(volDir, "B2", CreateMode.File);
                file2.FileType = FileAttribs.FILE_TYPE_BIN;

                using (DiskFileStream fd = fs.OpenFile(file2, FileAccessMode.ReadWrite,
                        FilePart.RawData)) {
                    byte[] twoBuf = new byte[2];
                    RawData.SetU16LE(twoBuf, 0, 0x1122);

                    fd.Write(twoBuf, 0, 2);     // write 'B' load address
                    fd.Write(twoBuf, 0, 2);     // write 'B' length
                    fd.Write(sFullPattern, 0, DiskArc.FS.DOS.MAX_IAB_FILE_LEN);
                    fd.Flush();
                    if (file2.DataLength != 0x1122 ||
                            file2.StorageSize != (256 + 4) * SECTOR_SIZE) {
                        throw new Exception("Incorrect initial file size #2");
                    }

                    // Cut it down to just under 8KB.  Does not change 'B' size.
                    fd.SetLength(0x2001);
                    if (file2.DataLength != 0x1122 || file2.StorageSize != 34 * SECTOR_SIZE) {
                        throw new Exception("Incorrect post-set size #2a");
                    }

                    // Cut a few bytes to make it fit.
                    fd.SetLength(0x2000);
                    if (file2.DataLength != 0x1122 || file2.StorageSize != 33 * SECTOR_SIZE) {
                        throw new Exception("Incorrect post-set size #2b");
                    }

                    // Full truncation.  Removes the length/address words, which should cause
                    // the length to be re-evaluated (and default to zero).
                    fd.SetLength(0);
                    if (file2.DataLength != 0 || file2.StorageSize != 1 * SECTOR_SIZE) {
                        throw new Exception("Incorrect post-set size #2c");
                    }
                }

                // Test a medium-sized 'S' file, in "cooked" mode (should work the same as "raw").
                IFileEntry file3 = fs.CreateFile(volDir, "R3", CreateMode.File);
                file3.FileType = FileAttribs.FILE_TYPE_F2;

                using (DiskFileStream fd = fs.OpenFile(file3, FileAccessMode.ReadWrite,
                        FilePart.DataFork)) {
                    fd.Write(sFullPattern, 0, DiskArc.FS.DOS.MAX_IAB_FILE_LEN);
                    fd.Flush();
                    if (file3.DataLength != 256 * SECTOR_SIZE ||
                            file3.StorageSize != (256 + 3) * SECTOR_SIZE) {
                        throw new Exception("Incorrect initial file size #3");
                    }

                    // Cut it right at the T/S boundary.
                    fd.SetLength(122 * 2 * SECTOR_SIZE);
                    if (file3.DataLength != 122 * 2 * SECTOR_SIZE ||
                            file3.StorageSize != (122 * 2 + 2) * SECTOR_SIZE) {
                        throw new Exception("Incorrect file size #3a");
                    }

                    // One byte past next T/S boundary.
                    fd.SetLength(122 * 1 * SECTOR_SIZE + 1);
                    if (file3.DataLength != (122 + 1) * SECTOR_SIZE ||
                            file3.StorageSize != (122 + 1 + 2) * SECTOR_SIZE) {
                        throw new Exception("Incorrect file size #3b");
                    }

                    // Exactly on first T/S boundary.
                    fd.SetLength(122 * 1 * SECTOR_SIZE);
                    if (file3.DataLength != 122 * SECTOR_SIZE ||
                            file3.StorageSize != (122 + 1) * SECTOR_SIZE) {
                        throw new Exception("Incorrect file size #3c");
                    }

                    // Full truncation.
                    fd.SetLength(0);
                    if (file3.DataLength != 0 || file3.StorageSize != 1 * SECTOR_SIZE) {
                        throw new Exception("Incorrect post-set size #3d");
                    }
                }

                // Test a massively sparse 'T' file, in "cooked" mode (should be same as "raw").
                IFileEntry file4 = fs.CreateFile(volDir, "T4", CreateMode.File);
                file4.FileType = FileAttribs.FILE_TYPE_TXT;

                using (DiskFileStream fd = fs.OpenFile(file4, FileAccessMode.ReadWrite,
                        FilePart.DataFork)) {
                    byte[] smallBuf = new byte[1];
                    smallBuf[0] = (byte)'*';

                    fd.Seek(SECTOR_SIZE * 3, SeekOrigin.Begin);
                    fd.Write(smallBuf, 0, 1);
                    fd.Seek(SECTOR_SIZE * 100, SeekOrigin.Begin);
                    fd.Write(smallBuf, 0, 1);
                    fd.Flush();

                    if (file4.DataLength != 0 || file4.StorageSize != SECTOR_SIZE * 3) {
                        throw new Exception("Incorrect initial file size #4");
                    }
                    if (((DiskArc.FS.DOS_FileEntry)file4).RawDataLength != SECTOR_SIZE * 101) {
                        throw new Exception("Incorrect initial raw data length #4");
                    }

                    // Drop the bytes in sector 100.  This should collapse down, because we
                    // ignore the empty trailing T/S lists.  We need to look at the "raw" length
                    // to really see the effects.
                    fd.SetLength(SECTOR_SIZE * 99);
                    if (file4.StorageSize != SECTOR_SIZE * 2) {
                        throw new Exception("Incorrect file size #4a");
                    }
                    if (((DiskArc.FS.DOS_FileEntry)file4).RawDataLength != SECTOR_SIZE * 4) {
                        throw new Exception("Incorrect raw data length #4a");
                    }
                }

                // Verify that the file mark doesn't change.
                IFileEntry file5 = fs.CreateFile(volDir, "R5", CreateMode.File);
                file5.FileType = FileAttribs.FILE_TYPE_BIN;

                using (DiskFileStream fd = fs.OpenFile(file5, FileAccessMode.ReadWrite,
                        FilePart.DataFork)) {
                    // Write ~32KB.
                    fd.Write(sFullPattern, 0, 32000);
                    fd.Flush();
                    if (file5.DataLength != 32000 || file5.StorageSize != (126+2) * SECTOR_SIZE) {
                        throw new Exception("Incorrect initial file size #5");
                    }

                    // Truncate to ~16KB.
                    fd.SetLength(16000);
                    if (file5.DataLength != 16000 || file5.StorageSize != (63 + 1) * SECTOR_SIZE) {
                        throw new Exception("Incorrect file size #5a");
                    }

                    // Write a byte.  Should put us at 32001.  The sectors between 16K and 32K
                    // are sparse.  We need one new T/S sector and one new data block at the end.
                    fd.Write(sFullPattern, 0, 1);
                    fd.Flush();
                    if (file5.DataLength != 32001 || file5.StorageSize != (64 + 2) * SECTOR_SIZE) {
                        throw new Exception("Incorrect file size #5b");
                    }
                    if (((DiskArc.FS.DOS_FileEntry)file5).RawDataLength != 126 * SECTOR_SIZE) {
                        throw new Exception("Incorrect raw data length #5b");
                    }
                }

                // Scan the filesystem.
                fs.PrepareRawAccess();
                fs.PrepareFileAccess(DO_SCAN);
                Helper.CheckNotes(fs, 0, 0);
            }
        }

        // Test file extension with SetLength.  The actual effects vary with the mode and file type.
        public static void TestSetLengthExtend(AppHook appHook) {
            using (IFileSystem fs = Make525Floppy(11, appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();

                IFileEntry file1 = fs.CreateFile(volDir, "B1", CreateMode.File);
                file1.FileType = FileAttribs.FILE_TYPE_BIN;
                using (DiskFileStream fd = fs.OpenFile(file1, FileAccessMode.ReadWrite,
                        FilePart.DataFork)) {
                    // This should have no effect, because the first sector (which holds the
                    // length) doesn't exist.
                    fd.SetLength(2048);
                    fd.Flush();
                    if (file1.DataLength != 0 || file1.StorageSize != 1 * SECTOR_SIZE) {
                        throw new Exception("Bad extension #1a");
                    }

                    // Write a byte.  This allocates the first sector, and allows the file's
                    // length to be set.
                    fd.Write(sFullPattern, 0, 1);
                    fd.Flush();
                    if (file1.DataLength != 2048 || file1.StorageSize != 2 * SECTOR_SIZE) {
                        throw new Exception("Bad extension #1b");
                    }

                    // Drop it back down so the filesystem scan doesn't throw a warning at us.
                    fd.SetLength(256 - 4);
                }

                // B file in "raw" mode.
                IFileEntry file2 = fs.CreateFile(volDir, "B2", CreateMode.File);
                file2.FileType = FileAttribs.FILE_TYPE_BIN;
                using (DiskFileStream fd = fs.OpenFile(file2, FileAccessMode.ReadWrite,
                        FilePart.RawData)) {
                    // This should have no effect.
                    fd.SetLength(2048);
                    fd.Flush();
                    if (file2.DataLength != 0 || file2.StorageSize != 1 * SECTOR_SIZE) {
                        throw new Exception("Bad extension #2a");
                    }

                    // Write a byte.  Seek past the addr/len area so we can confirm that SetLength
                    // doesn't change the length.
                    fd.Seek(4, SeekOrigin.Begin);
                    fd.Write(sFullPattern, 0, 1);
                    fd.Flush();
                    if (file2.DataLength != 0 || file2.StorageSize != 2 * SECTOR_SIZE) {
                        throw new Exception("Bad extension #2b");
                    }

                    fd.SetLength(4096);
                    fd.Flush();
                    if (file2.DataLength != 0 || file2.StorageSize != 2 * SECTOR_SIZE) {
                        throw new Exception("Bad extension #2c");
                    }
                }

                // Non-I/A/B file in "cooked" mode.
                IFileEntry file3 = fs.CreateFile(volDir, "S3", CreateMode.File);
                file3.FileType = FileAttribs.FILE_TYPE_F2;
                using (DiskFileStream fd = fs.OpenFile(file3, FileAccessMode.ReadWrite,
                        FilePart.DataFork)) {
                    // This should have no effect.
                    fd.SetLength(2048);
                    fd.Flush();
                    if (file3.DataLength != 0 || file3.StorageSize != 1 * SECTOR_SIZE) {
                        throw new Exception("Bad extension #3");
                    }
                }

                // Scan the filesystem.
                fs.PrepareRawAccess();
                fs.PrepareFileAccess(DO_SCAN);
                Helper.CheckNotes(fs, 0, 0);
            }
        }

        #region Utilities

        private static readonly byte[] sFullPattern = MakeFullPattern();

        private static byte[] MakeFullPattern() {
            byte[] buf = new byte[DiskArc.FS.DOS.MAX_IAB_FILE_LEN];

            for (int i = 0; i < buf.Length; i += SECTOR_SIZE) {
                // Put a nonzero / unique value in each sector.
                RawData.SetU16LE(buf, i, (ushort)i);
                buf[i + 2] = 0xcc;
            }
            return buf;
        }

        private static IFileSystem Make525Floppy(byte volNum, AppHook appHook) {
            return Helper.CreateTestImage(string.Empty, FileSystemType.DOS33, 35, 16, volNum,
                true, appHook, out MemoryStream memFile);
        }

        #endregion Utilities
    }
}
