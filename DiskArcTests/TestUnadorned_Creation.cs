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
using DiskArc.Disk;
using DiskArc.FS;
using static DiskArc.Defs;
using static DiskArc.IDiskImage;
using static DiskArc.IFileSystem;

namespace DiskArcTests {
    /// <summary>
    /// Test creation of unadorned sector and nibble images.
    /// </summary>
    public class TestUnadorned_Creation : ITest {
        // Test a 13-sector DOS 3.2 disk image (.d13).
        public static void Test13Sectors(AppHook appHook) {
            MemoryStream stream = new MemoryStream();
            using (IDiskImage disk = UnadornedSector.CreateSectorImage(stream, 35, 13,
                    SectorOrder.DOS_Sector, appHook)) {
                using (IFileSystem fs = new DOS(disk.ChunkAccess!, appHook)) {
                    fs.Format(string.Empty, 123, true);         // format with DOS image
                }
            }

            Helper.ExpectLong(35 * 13 * SECTOR_SIZE, stream.Length, "Unexpected size");

            using (IDiskImage disk = UnadornedSector.OpenDisk(stream, appHook)) {
                disk.AnalyzeDisk();
                IFileSystem fs = (IFileSystem)disk.Contents!;
                fs.PrepareFileAccess(true);
                if (fs.FreeSpace != (35 - 4) * 13 * SECTOR_SIZE) {
                    // Should be 4 tracks used: catalog and T0-T2
                    throw new Exception("Incorrect space available");
                }
                if (((DOS)fs).VolumeNum != 123) {
                    throw new Exception("Incorrect volume number");
                }

                Helper.CheckNotes(disk, 0, 0);
                Helper.CheckNotes(fs, 0, 0);
            }
        }

        // Test a 16-sector DOS 3.3 disk image (.po).
        public static void Test16Sectors(AppHook appHook) {
            MemoryStream stream = new MemoryStream();
            using (IDiskImage disk = UnadornedSector.CreateSectorImage(stream, 35, 16,
                    SectorOrder.ProDOS_Block, appHook)) {
                using (IFileSystem fs = new DOS(disk.ChunkAccess!, appHook)) {
                    fs.Format(string.Empty, 231, false);         // format without DOS image
                }

                disk.AnalyzeDisk();
                if (!(disk.Contents is DOS)) {
                    throw new Exception("Analysis failed");
                }
            }

            Helper.ExpectLong(35 * 16 * SECTOR_SIZE, stream.Length, "Unexpected size");

            using (IDiskImage disk = UnadornedSector.OpenDisk(stream, appHook)) {
                disk.AnalyzeDisk();
                IFileSystem fs = (IFileSystem)disk.Contents!;
                fs.PrepareFileAccess(true);
                if (fs.FreeSpace != (35 - 2) * 16 * SECTOR_SIZE) {
                    // Should be 2 tracks used: catalog and T0
                    throw new Exception("Incorrect space available");
                }
                if (((DOS)fs).VolumeNum != 231) {
                    throw new Exception("Incorrect volume number");
                }

                Helper.CheckNotes(disk, 0, 0);
                Helper.CheckNotes(fs, 0, 0);
            }
        }

        // Test a ProDOS block image (.po).
        public static void TestBlocks(AppHook appHook) {
            MemoryStream stream = new MemoryStream();
            using (IDiskImage disk = UnadornedSector.CreateBlockImage(stream, 800, appHook)) {
                using (IFileSystem fs = new ProDOS(disk.ChunkAccess!, appHook)) {
                    fs.Format("TEST", 0, false);
                }

                disk.AnalyzeDisk();
                IFileSystem pfs = (IFileSystem)disk.Contents!;
                if (!(pfs is ProDOS)) {
                    throw new Exception("Analysis failed");
                }
                pfs.PrepareFileAccess(true);
                if (pfs.FreeSpace != (800 - 7) * BLOCK_SIZE) {  // 2x boot, 4x root, 1x bitmap
                    throw new Exception("Incorrect space available");
                }

                Helper.CheckNotes(disk, 0, 0);
                Helper.CheckNotes(pfs, 0, 0);
            }

            Helper.ExpectLong(800 * BLOCK_SIZE, stream.Length, "Unexpected size");
        }

        // Test a 16-sector 5.25" DOS nibble image (.NIB).
        public static void TestNibble525_16(AppHook appHook) {
            MemoryStream stream = new MemoryStream();
            SectorCodec codec = StdSectorCodec.GetCodec(StdSectorCodec.CodecIndex525.Std_525_16);
            using (IDiskImage disk = UnadornedNibble525.CreateDisk(stream, codec, 254, appHook)) {
                using (IFileSystem fs = new DOS(disk.ChunkAccess!, appHook)) {
                    fs.Format(string.Empty, 1, false);

                    fs.PrepareFileAccess(true);
                    if (fs.FreeSpace != (35 - 2) * 16 * SECTOR_SIZE) {
                        // Should be 2 tracks used: catalog and T0
                        throw new Exception("Incorrect space available");
                    }
                    IFileEntry volDir = fs.GetVolDirEntry();
                    IFileEntry newEntry = fs.CreateFile(volDir, "TEST", CreateMode.File);
                    newEntry.FileType = FileAttribs.FILE_TYPE_BIN;
                    using (DiskFileStream fileStream =
                            fs.OpenFile(newEntry, FileAccessMode.ReadWrite, FilePart.DataFork)) {
                        // Write a byte pattern that uses all 256 values.
                        fileStream.Write(Patterns.sRunPattern, 0, Patterns.sRunPattern.Length);
                    }
                }
            }

            Helper.ExpectLong(35 * UnadornedNibble525.NIB_TRACK_LENGTH, stream.Length,
                "Unexpected size");

            using (IDiskImage disk = UnadornedNibble525.OpenDisk(stream, appHook)) {
                disk.AnalyzeDisk();
                IFileSystem fs = (IFileSystem)disk.Contents!;
                fs.PrepareFileAccess(true);
                if (((DOS)fs).VolumeNum != 1) {
                    throw new Exception("Incorrect volume number");     // this is VTOC number
                }

                IFileEntry volDir = fs.GetVolDirEntry();
                IFileEntry entry = fs.FindFileEntry(volDir, "TEST");
                byte[] buffer = new byte[entry.DataLength];
                using (DiskFileStream fileStream =
                        fs.OpenFile(entry, FileAccessMode.ReadOnly, FilePart.DataFork)) {
                    fileStream.ReadExactly(buffer, 0, (int)entry.DataLength);
                }
                if (!RawData.CompareBytes(Patterns.sRunPattern, buffer, buffer.Length)) {
                    throw new Exception("Data mismatch");
                }

                Helper.CheckNotes(disk, 0, 0);
                Helper.CheckNotes(fs, 0, 0);
            }
        }

        // Test a 13-sector 5.25" DOS nibble image (.NIB).
        public static void TestNibble525_13(AppHook appHook) {
            MemoryStream stream = new MemoryStream();
            SectorCodec codec = StdSectorCodec.GetCodec(StdSectorCodec.CodecIndex525.Std_525_13);
            using (IDiskImage disk = UnadornedNibble525.CreateDisk(stream, codec, 253, appHook)) {
                using (IFileSystem fs = new DOS(disk.ChunkAccess!, appHook)) {
                    fs.Format(string.Empty, 2, false);

                    bool writable;
                    if (!fs.RawAccess.TestSector(17, 0, out writable) || !writable) {
                        // Should be read + write.
                        throw new Exception("T17 S0 not readable or not writable");
                    }
                    if (fs.RawAccess.TestSector(34, 0, out writable) || !writable) {
                        // Should be able to write but not read.
                        throw new Exception("T34 S0 readable or not writable");
                    }

                    fs.PrepareFileAccess(true);
                    if (fs.FreeSpace != (35 - 2) * 13 * SECTOR_SIZE) {
                        // Should be 2 tracks used: catalog and T0
                        throw new Exception("Incorrect space available");
                    }

                    IFileEntry volDir = fs.GetVolDirEntry();
                    IFileEntry newEntry = fs.CreateFile(volDir, "TEST", CreateMode.File);
                    newEntry.FileType = FileAttribs.FILE_TYPE_BIN;
                    using (DiskFileStream fileStream =
                            fs.OpenFile(newEntry, FileAccessMode.ReadWrite, FilePart.DataFork)) {
                        // Write a byte pattern that uses all 256 values.
                        fileStream.Write(Patterns.sRunPattern, 0, Patterns.sRunPattern.Length);
                    }
                }
            }

            Helper.ExpectLong(35 * UnadornedNibble525.NIB_TRACK_LENGTH, stream.Length,
                "Unexpected size");

            using (IDiskImage disk = UnadornedNibble525.OpenDisk(stream, appHook)) {
                disk.AnalyzeDisk();
                IFileSystem fs = (IFileSystem)disk.Contents!;
                fs.PrepareFileAccess(true);
                if (((DOS)fs).VolumeNum != 2) {
                    throw new Exception("Incorrect volume number");     // this is VTOC number
                }

                IFileEntry volDir = fs.GetVolDirEntry();
                IFileEntry entry = fs.FindFileEntry(volDir, "TEST");
                byte[] buffer = new byte[entry.DataLength];
                using (DiskFileStream fileStream =
                        fs.OpenFile(entry, FileAccessMode.ReadOnly, FilePart.DataFork)) {
                    fileStream.ReadExactly(buffer, 0, (int)entry.DataLength);
                }
                if (!RawData.CompareBytes(Patterns.sRunPattern, buffer, buffer.Length)) {
                    throw new Exception("Data mismatch");
                }

                Helper.CheckNotes(disk, 0, 0);
                Helper.CheckNotes(fs, 0, 0);
            }
        }

        // Test a 16-sector 5.25" ProDOS block nibble image (.NIB).
        public static void TestNibble525_Block(AppHook appHook) {
            MemoryStream stream = new MemoryStream();
            SectorCodec codec = StdSectorCodec.GetCodec(StdSectorCodec.CodecIndex525.Std_525_16);
            using (IDiskImage disk = UnadornedNibble525.CreateDisk(stream, codec, 254, appHook)) {
                using (IFileSystem fs = new ProDOS(disk.ChunkAccess!, appHook)) {
                    fs.Format("TEST.DISK", 0, false);

                    fs.PrepareFileAccess(true);
                    if (fs.FreeSpace != (280 - 7) * BLOCK_SIZE) {
                        // Should be 7 blocks used: 2 boot, 4 root, 1 bitmap
                        throw new Exception("Incorrect space available");
                    }
                    IFileEntry volDir = fs.GetVolDirEntry();
                    IFileEntry newEntry = fs.CreateFile(volDir, "TEST", CreateMode.Extended);
                    newEntry.FileType = FileAttribs.FILE_TYPE_BIN;
                    using (DiskFileStream fileStream =
                            fs.OpenFile(newEntry, FileAccessMode.ReadWrite, FilePart.RsrcFork)) {
                        // Write a byte pattern that uses all 256 values.
                        fileStream.Write(Patterns.sRunPattern, 0, Patterns.sRunPattern.Length);
                    }
                }
            }

            Helper.ExpectLong(35 * UnadornedNibble525.NIB_TRACK_LENGTH, stream.Length,
                "Unexpected size");

            using (IDiskImage disk = UnadornedNibble525.OpenDisk(stream, appHook)) {
                disk.AnalyzeDisk();
                IFileSystem fs = (IFileSystem)disk.Contents!;
                fs.PrepareFileAccess(true);
                IFileEntry volDir = fs.GetVolDirEntry();
                if (volDir.FileName != "TEST.DISK") {
                    throw new Exception("Incorrect volume name");
                }
                IFileEntry entry = fs.FindFileEntry(volDir, "TEST");
                byte[] buffer = new byte[entry.DataLength];
                using (DiskFileStream fileStream =
                        fs.OpenFile(entry, FileAccessMode.ReadOnly, FilePart.RsrcFork)) {
                    fileStream.ReadExactly(buffer, 0, (int)entry.DataLength);
                }
                if (!RawData.CompareBytes(Patterns.sRunPattern, buffer, buffer.Length)) {
                    throw new Exception("Data mismatch");
                }

                Helper.CheckNotes(disk, 0, 0);
                Helper.CheckNotes(fs, 0, 0);
            }
        }

        // Confirm that modifying a track causes the disk image to be written on next Flush().
        // Also tests damage and read-only behavior.
        public static void TestDirtyTrack(AppHook appHook) {
            MemoryStream stream = new MemoryStream();
            SectorCodec codec = StdSectorCodec.GetCodec(StdSectorCodec.CodecIndex525.Std_525_16);
            using (IDiskImage disk = UnadornedNibble525.CreateDisk(stream, codec, 254, appHook)) {
                using (IFileSystem fs = new DOS(disk.ChunkAccess!, appHook)) {
                    fs.Format(string.Empty, 254, false);
                }

                disk.Flush();
                UnadornedNibble525 un525 = (UnadornedNibble525)disk;
                if (un525.IsDirty) {
                    throw new Exception("Flushing didn't clean disk");
                }

                INibbleDataAccess nda = (INibbleDataAccess)disk;
                if (!nda.GetTrackBits(17, 0, out CircularBitBuffer? cbb)) {
                    throw new Exception("Unable to get track 17");
                }
                // T17 PS3 (DOS T17 S6)
                byte[] seq = new byte[] { 0xd5, 0xaa, 0x96, 0xff, 0xfe, 0xaa, 0xbb, 0xab, 0xab };
                int bitPosn = cbb.FindNextLatchSequence(seq, seq.Length, -1);
                if (bitPosn == -1) {
                    throw new Exception("Track/sector not found");
                }
                if (un525.IsDirty) {
                    throw new Exception("Queries dirtied disk");
                }

                // Overwrite the first byte of the address prolog.  This may not be visible
                // as an unreadable sector immediately because the IChunkAccess object caches
                // the sector locations.
                cbb.BitPosition = bitPosn;
                cbb.WriteOctet(0xd4);
                if (!un525.IsDirty) {
                    throw new Exception("Modification didn't dirty disk");
                }

                // Changes should be flushed by Dispose() from "using".
            }

            // Make a copy of the .NIB stream into a read-only MemoryStream.
            byte[] dataCopy = new byte[stream.Length];
            stream.Position = 0;
            stream.ReadExactly(dataCopy, 0, (int)stream.Length);
            MemoryStream roStream = new MemoryStream(dataCopy, false);
            Debug.Assert(!roStream.CanWrite);

            // Confirm that changes were flushed and that a sector is bad.
            using (IDiskImage disk = UnadornedNibble525.OpenDisk(roStream, appHook)) {
                disk.AnalyzeDisk(null, SectorOrder.Physical, AnalysisDepth.Full);

                UnadornedNibble525 un525 = (UnadornedNibble525)disk;
                if (un525.IsDirty) {
                    throw new Exception("Disk image started out dirty");
                }

                // Verify that we can still find DOS, but one sector is bad.
                IFileSystem fs = (IFileSystem)disk.Contents!;
                if (fs.RawAccess.TestSector(17, 6, out bool writable)) {
                    throw new Exception("Disk damage didn't take");
                }
                int count = fs.RawAccess.CountUnreadableChunks();
                if (count != 1) {
                    throw new Exception("Too much damage");
                }

                // Should be one error in the filesystem, because of the bad block.
                fs.PrepareFileAccess(true);
                Helper.CheckNotes(disk, 0, 0);
                Helper.CheckNotes(fs, 0, 1);
                if (!fs.IsDubious || !fs.IsReadOnly) {
                    throw new Exception("DOS damage not handled correctly");
                }

                // Confirm that the nibble data is read-only.
                INibbleDataAccess nda = (INibbleDataAccess)disk;
                if (!nda.GetTrackBits(17, 0, out CircularBitBuffer? cbb)) {
                    throw new Exception("Unable to get track 17");
                }
                try {
                    cbb.WriteOctet(0xd4);
                    throw new Exception("Able to write to read-only stream");
                } catch (NotSupportedException) { /*expected*/ }
            }
        }

        // Test re-formatting and re-analysis of disk.
        public static void TestReformat(AppHook appHook) {
            MemoryStream stream = new MemoryStream();
            SectorCodec codec = StdSectorCodec.GetCodec(StdSectorCodec.CodecIndex525.Std_525_16);
            using (IDiskImage disk = UnadornedNibble525.CreateDisk(stream, codec, 254, appHook)) {
                disk.AnalyzeDisk();
                if (disk.ChunkAccess == null || disk.Contents != null) {
                    throw new Exception("Unexpected analysis result");
                }

                disk.FormatDisk(FileSystemType.ProDOS, "Test1", 0, true, appHook);
                disk.AnalyzeDisk();

                IFileSystem fs = (IFileSystem)disk.Contents!;
                fs.PrepareFileAccess(true);
                Helper.CheckNotes(fs, 0, 0);
                if (!(fs is ProDOS)) {
                    throw new Exception("Unexpected filesystem type");
                }
            }

            // Reformat as DOS.
            using (IDiskImage disk = UnadornedNibble525.OpenDisk(stream, appHook)) {
                // Analyze disk, chunks only.  We're about to replace the filesystem.
                disk.AnalyzeDisk(null, SectorOrder.Unknown, AnalysisDepth.ChunksOnly);
                if (disk.ChunkAccess == null) {
                    throw new Exception("Unable to analyze disk sector format");
                }

                // Reformat as DOS.
                using (IFileSystem newFs = new DOS(disk.ChunkAccess, appHook)) {
                    newFs.Format(string.Empty, 254, true);
                }

                // Let the disk image object find the new filesystem.
                disk.AnalyzeDisk();
                IFileSystem fs = (IFileSystem)disk.Contents!;
                fs.PrepareFileAccess(true);
                Helper.CheckNotes(fs, 0, 0);
                if (!(fs is DOS)) {
                    throw new Exception("Unexpected filesystem type");
                }
            }

            // Back to ProDOS, slightly different approach.
            using (IDiskImage disk = UnadornedNibble525.OpenDisk(stream, appHook)) {
                // Analyze disk, chunks only.  We're about to replace the filesystem.
                disk.AnalyzeDisk(null, SectorOrder.Unknown, AnalysisDepth.ChunksOnly);
                if (disk.ChunkAccess == null) {
                    throw new Exception("Unable to analyze disk sector format");
                }

                // Reformat as ProDOS, then redo the disk analysis.
                using (IFileSystem newFs = new ProDOS(disk.ChunkAccess, appHook)) {
                    disk.ChunkAccess.Initialize();
                    newFs.Format("Test2", 1, true);

                    newFs.PrepareFileAccess(true);
                    Helper.CheckNotes(newFs, 0, 0);
                    if (!(newFs is ProDOS)) {
                        throw new Exception("Unexpected filesystem type");
                    }
                }
            }
        }

        // Test creation of a "copy protected" disk image.
        public static void TestCustomCodec(AppHook appHook) {
            MemoryStream stream = new MemoryStream();
            SectorCodec codec = new CustomSectorCodec();
            using (IDiskImage disk = UnadornedNibble525.CreateDisk(stream, codec, 254, appHook)) {
                int unCount = disk.ChunkAccess!.CountUnreadableChunks();
                if (unCount != 0) {
                    throw new Exception("Custom codec formatting failed");
                }
                using (IFileSystem newFS = new ProDOS(disk.ChunkAccess!, appHook)) {
                    newFS.Format("CUSTOM", 0, true);
                }
                disk.AnalyzeDisk(codec, SectorOrder.Unknown, AnalysisDepth.Full);

                IFileSystem fs = (IFileSystem)disk.Contents!;
                fs.PrepareFileAccess(true);
                Helper.CheckNotes(fs, 0, 0);
                if (!(fs is ProDOS)) {
                    throw new Exception("Unexpected filesystem type");
                }
            }

            using (IDiskImage disk = UnadornedNibble525.OpenDisk(stream, appHook)) {
                disk.AnalyzeDisk();
                if (disk.ChunkAccess != null || disk.Contents != null) {
                    throw new Exception("Magically read disk with altered prologs");
                }
            }
        }
    }
}
