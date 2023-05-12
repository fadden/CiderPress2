/*
 * Copyright 2023 faddenSoft
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

using CommonUtil;
using DiskArc;
using DiskArc.Disk;
using DiskArc.FS;
using static DiskArc.Defs;
using static DiskArc.IFileSystem;

namespace DiskArcTests {
    /// <summary>
    /// Tests creation of 2IMG disk images.
    /// </summary>
    public class TestTwoIMG_Creation : ITest {
        private const string HELLO_WORLD = "Hello\r\nworld!";
        private const string HELLO_WORLD_N = "Hello\nworld!";
        private static readonly byte[] SIMPLE_DATA = new byte[] { 1, 2, 3, 4, 5 };

        public static void TestDOS525(AppHook appHook) {
            // Create a 40-track DOS-ordered disk.
            MemoryStream diskStream = new MemoryStream();
            using (TwoIMG disk = TwoIMG.CreateDOSSectorImage(diskStream, 40, appHook)) {
                using (IFileSystem newFs = new DOS(disk.ChunkAccess!, appHook)) {
                    newFs.Format(string.Empty, 101, true);      // format with DOS image
                }
            }

            using (TwoIMG disk = TwoIMG.OpenDisk(diskStream, appHook)) {
                disk.AnalyzeDisk();
                IFileSystem fs = (IFileSystem)disk.Contents!;
                fs.PrepareFileAccess(true);
                Helper.CheckNotes(fs, 0, 0);

                if (disk.WriteProtected || disk.VolumeNumber != -1 || disk.Comment.Length != 0 ||
                        disk.CreatorData.Length != 0) {
                    throw new Exception("Unexpected metadata");
                }
            }
        }

        public static void TestProDOS35(AppHook appHook) {
            // Create an 800KB ProDOS-ordered disk.
            MemoryStream diskStream = new MemoryStream();
            using (TwoIMG disk = TwoIMG.CreateProDOSBlockImage(diskStream, 1600, appHook)) {
                using (IFileSystem newFs = new ProDOS(disk.ChunkAccess!, appHook)) {
                    newFs.Format("TESTING", 0, true);           // format with ProDOS image
                }

                disk.Creator = "WHEE";
                disk.WriteProtected = true;
                disk.VolumeNumber = 100;
                disk.Comment = HELLO_WORLD;
                disk.CreatorData = SIMPLE_DATA;
                try {
                    disk.VolumeNumber = 300;
                    throw new Exception("Invalid volume number allowed");
                } catch (ArgumentException) { /*expected*/ }
            }

            using (TwoIMG disk = TwoIMG.OpenDisk(diskStream, appHook)) {
                disk.AnalyzeDisk();
                IFileSystem fs = (IFileSystem)disk.Contents!;
                fs.PrepareFileAccess(true);
                Helper.CheckNotes(fs, 0, 0);

                // Comment will be converted to local EOL convention.
                if (disk.Creator != "WHEE" || !disk.WriteProtected || disk.VolumeNumber != 100 ||
                        (disk.Comment != HELLO_WORLD && disk.Comment != HELLO_WORLD_N) ||
                        !RawData.CompareBytes(disk.CreatorData, SIMPLE_DATA, SIMPLE_DATA.Length)) {
                    throw new Exception("Incorrect metadata");
                }

                // remove the comment
                disk.Comment = string.Empty;
            }

            using (TwoIMG disk = TwoIMG.OpenDisk(diskStream, appHook)) {
                disk.AnalyzeDisk();
                IFileSystem fs = (IFileSystem)disk.Contents!;
                fs.PrepareFileAccess(true);
                Helper.CheckNotes(fs, 0, 0);

                if (disk.Comment.Length != 0 ||
                        !RawData.CompareBytes(disk.CreatorData, SIMPLE_DATA, SIMPLE_DATA.Length)) {
                    throw new Exception("Incorrect metadata after comment removed");
                }
            }
        }

        public static void TestNibble525(AppHook appHook) {
            // Create a 35-track nibble disk image.
            MemoryStream diskStream = new MemoryStream();
            SectorCodec codec = StdSectorCodec.GetCodec(StdSectorCodec.CodecIndex525.Std_525_16);
            using (TwoIMG disk = TwoIMG.CreateNibbleImage(diskStream, codec, 101, appHook)) {
                using (IFileSystem newFs = new ProDOS(disk.ChunkAccess!, appHook)) {
                    newFs.Format("NIBBLISH", 0, true);

                    newFs.PrepareFileAccess(true);
                    IFileEntry volDir = newFs.GetVolDirEntry();
                    newFs.CreateFile(volDir, "NEWFILE", CreateMode.File);
                }
            }

            // Verify disk, create a file and write to it.
            using (TwoIMG disk = TwoIMG.OpenDisk(diskStream, appHook)) {
                disk.AnalyzeDisk();
                IFileSystem fs = (IFileSystem)disk.Contents!;
                Helper.ExpectInt(0, fs.RawAccess.CountUnreadableChunks(), "bad chunks found");
                fs.PrepareFileAccess(true);
                Helper.CheckNotes(fs, 0, 0);

                // Modify the disk.
                IFileEntry volDir = fs.GetVolDirEntry();
                IFileEntry entry = fs.FindFileEntry(volDir, "NEWFILE");
                using (Stream stream = fs.OpenFile(entry, FileAccessMode.ReadWrite,
                        FilePart.DataFork)) {
                    stream.Write(SIMPLE_DATA, 0, SIMPLE_DATA.Length);
                }

                if (disk.WriteProtected || disk.VolumeNumber != -1 || disk.Comment.Length != 0 ||
                        disk.CreatorData.Length != 0) {
                    throw new Exception("Unexpected metadata");
                }
            }

            // Verify disk and file contents.
            using (TwoIMG disk = TwoIMG.OpenDisk(diskStream, appHook)) {
                disk.AnalyzeDisk();
                IFileSystem fs = (IFileSystem)disk.Contents!;
                Helper.ExpectInt(0, fs.RawAccess.CountUnreadableChunks(), "bad chunks found");
                fs.PrepareFileAccess(true);
                Helper.CheckNotes(fs, 0, 0);

                IFileEntry volDir = fs.GetVolDirEntry();
                IFileEntry entry = fs.FindFileEntry(volDir, "NEWFILE");
                using (Stream stream = fs.OpenFile(entry, FileAccessMode.ReadOnly,
                        FilePart.DataFork)) {
                    byte[] buf = new byte[stream.Length];
                    stream.ReadExactly(buf, 0, (int)stream.Length);
                    if (stream.Length != SIMPLE_DATA.Length ||
                            !RawData.CompareBytes(buf, SIMPLE_DATA, SIMPLE_DATA.Length)) {
                        throw new Exception("didn't find data written earlier");
                    }
                }
            }
        }

        // Damage the comment header value and see if it gets handled correctly.
        public static void TestDamageComment(AppHook appHook) {
            MemoryStream diskStream = new MemoryStream();
            using (TwoIMG disk = TwoIMG.CreateDOSSectorImage(diskStream, 35, appHook)) {
                using (IFileSystem newFs = new DOS(disk.ChunkAccess!, appHook)) {
                    newFs.Format(string.Empty, 101, true);      // format with DOS image
                }
                disk.Comment = "uh oh";
            }

            diskStream.Position = 0x24;     // comment length
            RawData.WriteU32LE(diskStream, 0x11223344);

            using (TwoIMG disk = TwoIMG.OpenDisk(diskStream, appHook)) {
                if (!disk.IsDubious || !disk.IsReadOnly) {
                    throw new Exception("comment damage failed to mark disk as dubious");
                }
                if (disk.Comment.Length != 0) {
                    throw new Exception("comment still exists, len=" + disk.Comment.Length);
                }

                // Everything else should be fine.
                disk.AnalyzeDisk();
                IFileSystem fs = (IFileSystem)disk.Contents!;
                fs.PrepareFileAccess(true);
                Helper.CheckNotes(fs, 0, 0);

                if (!fs.IsReadOnly) {
                    throw new Exception("FS is read-write on read-only disk");
                }
            }
        }
    }
}
