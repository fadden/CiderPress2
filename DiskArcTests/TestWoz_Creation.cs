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
using static DiskArc.IFileSystem;

namespace DiskArcTests {
    /// <summary>
    /// Tests creation of .WOZ disk images.
    /// </summary>
    public class TestWoz_Creation : ITest {
        // Tests initialization of a 5.25" disk image.
        public static void TestInit525(AppHook appHook) {
            MemoryStream diskStream = new MemoryStream();
            SectorCodec codec = StdSectorCodec.GetCodec(StdSectorCodec.CodecIndex525.Std_525_16);
            using (IDiskImage disk = Woz.CreateDisk525(diskStream, 35, codec,
                    DEFAULT_525_VOLUME_NUM, appHook)) {
                using (IFileSystem newfs = new DOS(disk.ChunkAccess!, appHook)) {
                    newfs.Format(string.Empty, 101, true);          // format with DOS image
                }
                disk.AnalyzeDisk();
                IFileSystem fs = (IFileSystem)disk.Contents!;
                Helper.ExpectInt(0, fs.RawAccess.CountUnreadableChunks(), "bad chunks found");
                fs.PrepareFileAccess(true);
                Helper.CheckNotes(fs, 0, 0);
            }
        }

        // Tests initialization of a 3.5" disk image.
        public static void TestInit35(AppHook appHook) {
            MemoryStream diskStream = new MemoryStream();
            SectorCodec codec = StdSectorCodec.GetCodec(StdSectorCodec.CodecIndex35.Std_35);

            // Start with an 800KB DS/DD image, 4:1 interleave.
            using (IDiskImage disk = Woz.CreateDisk35(diskStream, MediaKind.GCR_DSDD35, 4,
                    codec, appHook)) {
                disk.FormatDisk(FileSystemType.ProDOS, "New.Disk8", 0, true, appHook);
                IFileSystem fs = (IFileSystem)disk.Contents!;
                Helper.ExpectInt(0, fs.RawAccess.CountUnreadableChunks(), "bad chunks found");
                fs.PrepareFileAccess(true);
                Helper.CheckNotes(fs, 0, 0);
            }

            // Repeat with a 400KB SS/DD image, 2:1 interleave.
            using (IDiskImage disk = Woz.CreateDisk35(diskStream, MediaKind.GCR_SSDD35, 2,
                    codec, appHook)) {
                using (IFileSystem newfs = new HFS(disk.ChunkAccess!, appHook)) {
                    newfs.Format("New.Disk4", 0, true);             // format with HFS image
                }
                disk.AnalyzeDisk();
                IFileSystem fs = (IFileSystem)disk.Contents!;
                Helper.ExpectInt(0, fs.RawAccess.CountUnreadableChunks(), "bad chunks found");
                fs.PrepareFileAccess(true);
                Helper.CheckNotes(fs, 0, 0);
            }
        }

        // Create a file, then modify it several times to ensure changes are being noticed
        // and flushed.
        public static void TestUpdates(AppHook appHook) {
            MemoryStream diskStream = new MemoryStream();
            SectorCodec codec = StdSectorCodec.GetCodec(StdSectorCodec.CodecIndex35.Std_35);

            // Start with an 800KB DS/DD image, 4:1 interleave.
            using (IDiskImage disk = Woz.CreateDisk35(diskStream, MediaKind.GCR_DSDD35, 4,
                    codec, appHook)) {
                Woz wdisk = (Woz)disk;
                if (wdisk.HasMeta) {
                    throw new Exception("Non-null metadata");
                }
                wdisk.AddMETA();
                if (!wdisk.HasMeta) {
                    throw new Exception("Metadata not found");
                }
                Helper.ExpectBool(true, wdisk.IsDirty, "not dirty after adding META");
                disk.Flush();
                Helper.ExpectBool(false, wdisk.IsDirty, "still dirty after META flush");

                wdisk.SetMetaValue("meta:custom_key", "superior_value!");
                Helper.ExpectBool(true, wdisk.IsDirty, "not dirty after adding meta item");
                disk.Flush();
                Helper.ExpectBool(false, wdisk.IsDirty, "still dirty after item flush");

                try {
                    wdisk.SetMetaValue("meta:language", "Klingon");
                    throw new Exception("Allowed to set invalid language");
                } catch (ArgumentException) { /*expected*/ }

                wdisk.SetMetaValue("meta:language", "English");
                if (wdisk.DeleteMetaEntry("meta:language")) {
                    throw new Exception("Allowed to delete meta:language");
                }

                // Let the Dispose do the final update.
            }

            MemoryStream cloneStream = new MemoryStream();

            using (IDiskImage disk = Woz.OpenDisk(diskStream, appHook)) {
                Woz wdisk = (Woz)disk;
                if (!wdisk.HasMeta) {
                    throw new Exception("No metadata");
                }
                Helper.ExpectString("superior_value!", wdisk.GetMetaValue("meta:custom_key", false),
                    "incorrect custom key value");
                Helper.ExpectString("English", wdisk.GetMetaValue("meta:language", false),
                    "incorrect language");

                disk.AnalyzeDisk();

                // Set values in INFO.
                Helper.ExpectBool(false, wdisk.IsDirty, "dirty before INFO set");
                wdisk.SetMetaValue("info:boot_sector_format", "3");
                wdisk.SetMetaValue("info:write_protected", "true");
                Helper.ExpectBool(true, wdisk.IsDirty, "not dirty after INFO set");

                // Create a filesystem and add a file.
                disk.FormatDisk(FileSystemType.ProDOS, "Flusher", 0, true, appHook);
                IFileSystem fs = (IFileSystem)disk.Contents!;
                fs.PrepareFileAccess(true);
                IFileEntry volDir = fs.GetVolDirEntry();
                IFileEntry newEntry = fs.CreateFile(volDir, "New.File", CreateMode.File);
                using (DiskFileStream stream = fs.OpenFile(newEntry, FileAccessMode.ReadWrite,
                        FilePart.DataFork)) {
                    stream.WriteByte(0x11);

                    newEntry.FileType = FileAttribs.FILE_TYPE_BIN;
                    newEntry.AuxType = 0x1234;

                    // Flush the disk while the file is still open, and capture a copy of the
                    // WOZ stream.  This exercises the IFileSystem.Flush for ProDOS.
                    disk.Flush();
                    diskStream.Position = 0;
                    diskStream.CopyTo(cloneStream);
                }
            }

            // See if the post-Flush version matches.  We can do this trivially by comparing the
            // length and CRC-32 (at +8).
            if (cloneStream.Length != diskStream.Length) {
                throw new Exception("Clone stream length is different");
            }
            uint diskCrc = RawData.GetU32LE(diskStream.GetBuffer(), 8);
            uint cloneCrc = RawData.GetU32LE(cloneStream.GetBuffer(), 8);
            if (cloneCrc != diskCrc) {
                throw new Exception("Clone stream CRC-32 is different");
            }

            // They match; see if they're correct.
            using (IDiskImage disk = Woz.OpenDisk(cloneStream, appHook)) {
                Woz wdisk = (Woz)disk;
                Helper.ExpectString("3", wdisk.GetMetaValue("info:boot_sector_format", false),
                    "info bsf not set");
                Helper.ExpectString("true", wdisk.GetMetaValue("info:write_protected", false),
                    "info wp not set");

                disk.AnalyzeDisk();
                IFileSystem fs = (IFileSystem)disk.Contents!;
                fs.PrepareFileAccess(true);
                IFileEntry volDir = fs.GetVolDirEntry();
                IFileEntry entry = fs.FindFileEntry(volDir, "New.File");
                if (entry.FileType != FileAttribs.FILE_TYPE_BIN || entry.AuxType != 0x1234) {
                    throw new Exception("type mismatch");
                }
                using (DiskFileStream stream = fs.OpenFile(entry, FileAccessMode.ReadWrite,
                        FilePart.DataFork)) {
                    int val = stream.ReadByte();
                    Helper.ExpectInt(0x11, val, "file content mismatch");
                }

                Helper.CheckNotes(disk, 0, 0);
                Helper.CheckNotes(fs, 0, 0);
            }
        }

        // Confirm that modifying a track causes the disk image to be written on next Flush().
        // Also tests damage, read-only behavior, and 40-track disks.
        public static void TestDirtyTrack(AppHook appHook) {
            MemoryStream stream = new MemoryStream();
            SectorCodec codec = StdSectorCodec.GetCodec(StdSectorCodec.CodecIndex525.Std_525_16);
            using (IDiskImage disk = Woz.CreateDisk525(stream, 40, codec, 254, appHook)) {
                using (IFileSystem fs = new ProDOS(disk.ChunkAccess!, appHook)) {
                    fs.Format("Dirty.Flush", 0, false);
                }

                disk.Flush();
                Woz woz = (Woz)disk;
                if (woz.IsDirty) {
                    throw new Exception("Flushing didn't clean disk");
                }
                if (!woz.IsModified) {
                    throw new Exception("Modified flag not set");
                }
                woz.IsModified = false;
                if (woz.IsModified) {
                    throw new Exception("Modified flag failed to clear");
                }

                INibbleDataAccess nda = (INibbleDataAccess)disk;
                if (!nda.GetTrackBits(0, 0, out CircularBitBuffer? cbb)) {
                    throw new Exception("Unable to get track 0");
                }
                // T0 PS3 (ProDOS T0 S9 == second half of block 4)
                byte[] seq = new byte[] { 0xd5, 0xaa, 0x96, 0xff, 0xfe, 0xaa, 0xaa, 0xab, 0xab };
                int bitPosn = cbb.FindNextLatchSequence(seq, seq.Length, -1);
                if (bitPosn == -1) {
                    throw new Exception("Track/sector not found");
                }
                if (woz.IsDirty || woz.IsModified) {
                    throw new Exception("Queries dirtied disk");
                }

                // Overwrite the first byte of the address prolog.  This may not be visible
                // as an unreadable sector immediately because the IChunkAccess object caches
                // the sector locations.
                cbb.BitPosition = bitPosn;
                cbb.WriteOctet(0xd4);
                if (!woz.IsDirty || !woz.IsModified) {
                    throw new Exception("Modification didn't dirty disk");
                }

                // Changes should be flushed by Dispose() from "using".
            }

            // Make a copy of the .WOZ stream into a read-only MemoryStream.
            byte[] dataCopy = new byte[stream.Length];
            stream.Position = 0;
            stream.ReadExactly(dataCopy, 0, (int)stream.Length);
            MemoryStream roStream = new MemoryStream(dataCopy, false);
            Debug.Assert(!roStream.CanWrite);

            // Confirm that changes were flushed and that a sector is bad.
            using (IDiskImage disk = Woz.OpenDisk(roStream, appHook)) {
                disk.AnalyzeDisk();

                Woz woz = (Woz)disk;
                if (woz.IsDirty) {
                    throw new Exception("Disk image started out dirty");
                }

                // Verify that we can still find ProDOS, but one sector is bad.
                IFileSystem fs = (IFileSystem)disk.Contents!;
                if (fs.RawAccess.TestSector(0, 6, out bool writable)) {     // DOS sector 6
                    throw new Exception("Disk damage didn't take");
                }
                int count = fs.RawAccess.CountUnreadableChunks();
                if (count != 1) {
                    throw new Exception("Too much damage");
                }

                // Should be one error in the filesystem, because of the bad block.  Also one
                // warning about unused blocks marked used because the directory scan halted.
                fs.PrepareFileAccess(true);
                Helper.CheckNotes(disk, 0, 0);
                Helper.CheckNotes(fs, 1, 1);
                if (!fs.IsDubious || !fs.IsReadOnly) {
                    throw new Exception("ProDOS damage not handled correctly");
                }
                if (((ProDOS)fs).TotalBlocks != 40 * 8) {
                    throw new Exception("ProDOS didn't use all of 40-track disk");
                }

                // Confirm that the nibble data is read-only, and that track 39 exists.
                INibbleDataAccess nda = (INibbleDataAccess)disk;
                if (!nda.GetTrackBits(39, 0, out CircularBitBuffer? cbb)) {
                    throw new Exception("Unable to get track 39");
                }
                try {
                    cbb.WriteOctet(0xd4);
                    throw new Exception("Able to write to read-only stream");
                } catch (NotSupportedException) { /*expected*/ }
            }
        }

        public static void TestGrind(AppHook appHook) {
            const int GRINDER_ITERATIONS = 500;
            const int SEED = 0;

            MemoryStream diskStream = new MemoryStream();
            SectorCodec codec = StdSectorCodec.GetCodec(StdSectorCodec.CodecIndex35.Std_35);

            // 800KB is a little small for grinding, but the intent here is to read and write
            // a lot of disk blocks rather than exercise ProDOS.
            using (IDiskImage disk = Woz.CreateDisk35(diskStream, MediaKind.GCR_DSDD35, 4,
                    codec, appHook)) {
                using (IFileSystem newfs = new ProDOS(disk.ChunkAccess!, appHook)) {
                    newfs.Format("Grind.Test", 0, true);
                }
                disk.AnalyzeDisk();
                IFileSystem fs = (IFileSystem)disk.Contents!;
                Helper.ExpectInt(0, fs.RawAccess.CountUnreadableChunks(), "bad chunks found");
                fs.PrepareFileAccess(true);
                Helper.CheckNotes(fs, 0, 0);

                Grinder grinder = new Grinder(fs, SEED);
                grinder.Execute(GRINDER_ITERATIONS);
            }

            // Confirm all is still well.
            using (IDiskImage disk = Woz.OpenDisk(diskStream, appHook)) {
                Helper.CheckNotes(disk, 0, 0);
                disk.AnalyzeDisk();
                IFileSystem fs = (IFileSystem)disk.Contents!;
                fs.PrepareFileAccess(true);
                Helper.CheckNotes(fs, 0, 0);
            }
        }

        // Tests a file with a damaged CRC.
        public static void TestDamage(AppHook appHook) {
            MemoryStream diskStream = new MemoryStream();
            SectorCodec codec = StdSectorCodec.GetCodec(StdSectorCodec.CodecIndex525.Std_525_16);
            using (IDiskImage disk = Woz.CreateDisk525(diskStream, 35, codec,
                    DEFAULT_525_VOLUME_NUM, appHook)) {
                using (IFileSystem newfs = new DOS(disk.ChunkAccess!, appHook)) {
                    newfs.Format(string.Empty, 101, true);          // format with DOS image
                }
            }

            diskStream.Position = 0x08;     // CRC
            RawData.WriteU32LE(diskStream, 0x11223344);     // almost certainly wrong

            using (IDiskImage disk = Woz.OpenDisk(diskStream, appHook)) {
                if (!disk.IsDubious || !disk.IsReadOnly) {
                    throw new Exception("CRC damage failed to mark disk as dubious");
                }

                // Everything else should be fine.
                disk.AnalyzeDisk();
                IFileSystem fs = (IFileSystem)disk.Contents!;
                Helper.ExpectInt(0, fs.RawAccess.CountUnreadableChunks(), "bad chunks found");
                fs.PrepareFileAccess(true);
                Helper.CheckNotes(fs, 0, 0);

                if (!fs.IsReadOnly) {
                    throw new Exception("FS is read-write on read-only disk");
                }
            }
        }
    }
}
