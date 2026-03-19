/*
 * Copyright 2026 faddenSoft
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
    /// Tests creation of MOOF disk images.
    /// </summary>
    public class TestMoof_Creation : ITest {
        // Create a file, then modify it several times to ensure changes are being noticed
        // and flushed.
        // (Cloned from TestWoz_Creation)
        public static void TestUpdates(AppHook appHook) {
            MemoryStream diskStream = new MemoryStream();
            SectorCodec codec = StdSectorCodec.GetCodec(StdSectorCodec.CodecIndex35.Std_35);

            // Start with an 800KB DS/DD image, 4:1 interleave.
            using (IDiskImage disk = Moof.CreateDisk35(diskStream, MediaKind.GCR_DSDD35, 4,
                    codec, appHook)) {
                Moof mdisk = (Moof)disk;
                if (mdisk.HasMeta) {
                    throw new Exception("Non-null metadata");
                }
                mdisk.AddMETA();
                if (!mdisk.HasMeta) {
                    throw new Exception("Metadata not found");
                }
                Helper.ExpectBool(true, mdisk.IsDirty, "not dirty after adding META");
                disk.Flush();
                Helper.ExpectBool(false, mdisk.IsDirty, "still dirty after META flush");

                mdisk.SetMetaValue("meta:custom_key", "superior_value!");
                Helper.ExpectBool(true, mdisk.IsDirty, "not dirty after adding meta item");
                disk.Flush();
                Helper.ExpectBool(false, mdisk.IsDirty, "still dirty after item flush");

                try {
                    mdisk.SetMetaValue("meta:language", "Klingon");
                    throw new Exception("Allowed to set invalid language");
                } catch (ArgumentException) { /*expected*/ }

                mdisk.SetMetaValue("meta:language", "English");
                if (mdisk.DeleteMetaEntry("meta:language")) {
                    throw new Exception("Allowed to delete meta:language");
                }

                // Let the Dispose do the final update.
            }

            MemoryStream cloneStream = new MemoryStream();

            using (IDiskImage disk = Moof.OpenDisk(diskStream, appHook)) {
                Moof mdisk = (Moof)disk;
                if (!mdisk.HasMeta) {
                    throw new Exception("No metadata");
                }
                Helper.ExpectString("superior_value!", mdisk.GetMetaValue("meta:custom_key", false),
                    "incorrect custom key value");
                Helper.ExpectString("English", mdisk.GetMetaValue("meta:language", false),
                    "incorrect language");

                disk.AnalyzeDisk();

                // Set values in INFO.
                Helper.ExpectBool(false, mdisk.IsDirty, "dirty before INFO set");
                mdisk.SetMetaValue("info:write_protected", "true");
                Helper.ExpectBool(true, mdisk.IsDirty, "not dirty after INFO set");

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
                    // MOOF stream.  This exercises the IFileSystem.Flush for ProDOS.
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
            using (IDiskImage disk = Moof.OpenDisk(cloneStream, appHook)) {
                Moof mdisk = (Moof)disk;
                Helper.ExpectString("true", mdisk.GetMetaValue("info:write_protected", false),
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
    }
}
