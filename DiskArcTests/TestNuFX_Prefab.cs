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

using AppCommon;
using CommonUtil;
using DiskArc;
using DiskArc.Arc;
using static DiskArc.Defs;

namespace DiskArcTests {
    /// <summary>
    /// NuFX archive tests.
    /// </summary>
    public class TestNuFX_Prefab : ITest {
        //string fileName, int dataLength, int rsrcLength, int storageSize,
        //int fileType, int auxType
        private static List<Helper.FileAttr> sSampleContents = new List<Helper.FileAttr>() {
            new Helper.FileAttr("d0", 0, -1, 0, FileAttribs.FILE_TYPE_TXT, 0x0000),
            new Helper.FileAttr("d0r0", 0, 0, 0, FileAttribs.FILE_TYPE_TXT, 0x0000),
            new Helper.FileAttr("d0rN", 0, 10, 10, FileAttribs.FILE_TYPE_TXT, 0x0000),
            new Helper.FileAttr("dN", 8, -1, 8, FileAttribs.FILE_TYPE_TXT, 0x0000),
            new Helper.FileAttr("dNr0", 8, 0, 8, FileAttribs.FILE_TYPE_TXT, 0x0000),
            new Helper.FileAttr("dNrN", 8, 10, 18, FileAttribs.FILE_TYPE_TXT, 0x0000),
        };

        public static void TestSimple(AppHook appHook) {
            string dataVal = "testing\n";
            string rsrcVal = "r-testing\n";
            byte[] buf = new byte[16];

            using (Stream dataFile = Helper.OpenTestFile("nufx/gshk-empty-forks.shk", true,
                    appHook)) {
                using (IArchive archive = FileAnalyzer.PrepareArchive(dataFile,
                        FileKind.NuFX, appHook)!) {
                    Helper.ValidateContents(archive, sSampleContents);
                    Helper.CheckNotes(archive, 0, 0);

                    // These entries are malformed, created by GSHK for empty files.  The
                    // library creates "Miranda" entries for the missing file data threads,
                    // based on the storage_type.
                    IFileEntry d0 = archive.FindFileEntry("d0");
                    Helper.ExpectBool(true, d0.HasDataFork, "has data fork");
                    Helper.ExpectBool(false, d0.HasRsrcFork, "!has rsrc fork");
                    IFileEntry d0r0 = archive.FindFileEntry("d0r0");
                    Helper.ExpectBool(false, d0r0.HasDataFork, "!has data fork");
                    Helper.ExpectBool(true, d0r0.HasRsrcFork, "has rsrc fork");

                    IFileEntry dnrn = archive.FindFileEntry("dNrN");
                    using (ArcReadStream stream = archive.OpenPart(dnrn, FilePart.DataFork)) {
                        int actual = stream.Read(buf, 0, buf.Length);
                        Helper.ExpectInt(dataVal.Length, actual, "dNrN data fork length");
                        if (stream.ChkResult != ArcReadStream.CheckResult.Success) {
                            throw new Exception("Checksum not verified");
                        }
                        for (int i = 0; i < actual; i++) {
                            Helper.ExpectByte((byte)dataVal[i], buf[i], "data contents");
                        }
                    }
                    using (ArcReadStream stream = archive.OpenPart(dnrn, FilePart.RsrcFork)) {
                        int actual = stream.Read(buf, 0, buf.Length);
                        Helper.ExpectInt(rsrcVal.Length, actual, "dNrN rsrc fork length");
                        Debug.Assert(stream.ChkResult == ArcReadStream.CheckResult.Success);
                        for (int i = 0; i < actual; i++) {
                            Helper.ExpectByte((byte)rsrcVal[i], buf[i], "rsrc contents");
                        }
                    }
                }
            }
        }

        // Extract some files.  This relies on the archive's CRC-16 to check validity.
        public static void TestExtract(AppHook appHook) {
            byte[] dataBuf = new byte[139776];

            using (Stream dataFile = Helper.OpenTestFile("nufx/PatchHFS.shk", true, appHook)) {
                using (IArchive archive = FileAnalyzer.PrepareArchive(dataFile,
                        FileKind.NuFX, appHook)!) {
                    // Small file compressed with LZW/2, fits in a single 4KB chunk.
                    IFileEntry file1 = archive.FindFileEntry("patchhfs:PatchHFS.c");
                    Helper.CheckNotes(archive, 0, 0);
                    using (ArcReadStream stream = archive.OpenPart(file1, FilePart.DataFork)) {
                        int actual = stream.Read(dataBuf, 0, dataBuf.Length);
                        Helper.ExpectInt(1730, actual, "PatchHFS.c length");
                        if (stream.ChkResult != ArcReadStream.CheckResult.Success) {
                            throw new Exception("Checksum not verified");
                        }
                    }

                    // Larger file compressed with LZW/2, three 4KB chunks (one table clear).
                    IFileEntry file2 = archive.FindFileEntry("patchhfs:PatchHFS");
                    using (ArcReadStream stream = archive.OpenPart(file2, FilePart.DataFork)) {
                        int actual = stream.Read(dataBuf, 0, dataBuf.Length);
                        Helper.ExpectInt(11253, actual, "PatchHFS length");
                        if (stream.ChkResult != ArcReadStream.CheckResult.Success) {
                            throw new Exception("Checksum not verified");
                        }
                    }
                }
            }

            using (Stream dataFile = Helper.OpenTestFile("nufx/GSHK11.SEA", true, appHook)) {
                using (IArchive archive = FileAnalyzer.PrepareArchive(dataFile,
                        FileKind.NuFX, appHook)!) {
                    // Large forked file, compressed with LZW/2.  At least one chunk fails to
                    // compress with LZW.
                    //
                    // Confirm it's what we expect.
                    IFileEntry file1 = archive.FindFileEntry("GSHK");
                    Helper.CheckNotes(archive, 0, 0);
                    if (file1.FileType != FileAttribs.FILE_TYPE_S16 ||
                            file1.AuxType != 0xdb07) {
                        throw new Exception("Wrong file/aux type");
                    }
                    if (file1.StorageSize != 87926) {
                        throw new Exception("Unexpected storage size");
                    }

                    // Unpack both forks.
                    using (ArcReadStream stream = archive.OpenPart(file1, FilePart.DataFork)) {
                        int actual = stream.Read(dataBuf, 0, dataBuf.Length);
                        Helper.ExpectInt(112443, actual, "GSHK data fork length");
                    }
                    using (ArcReadStream stream = archive.OpenPart(file1, FilePart.RsrcFork)) {
                        int actual = stream.Read(dataBuf, 0, dataBuf.Length);
                        Helper.ExpectInt(18063, actual, "GSHK rsrc fork length");
                    }
                }
            }
        }

        public static void TestWrapperUpdate(AppHook appHook) {
            // Test update of a BXY file.
            using (Stream dataFile = Helper.OpenTestFile("nufx/Samples.BXY", false, appHook)) {
                MemoryStream newStream = new MemoryStream();

                using (IArchive archive = NuFX.OpenArchive(dataFile, appHook)) {
                    archive.StartTransaction();
                    IFileEntry newEntry = archive.CreateRecord();
                    newEntry.FileName = "Ulysses";
                    SimplePartSource usource =
                        new SimplePartSource(new MemoryStream(Patterns.sUlyssesBytes));
                    archive.AddPart(newEntry, FilePart.RsrcFork, usource,
                        CompressionFormat.Default);
                    archive.CommitTransaction(newStream);
                    Helper.CheckNotes(archive, 0, 0);
                }

                Debug.Assert(newStream.Length > dataFile.Length);

                using (IArchive archive = Binary2.OpenArchive(newStream, appHook)) {
                    List<IFileEntry> entries = archive.ToList();
                    Helper.CheckNotes(archive, 0, 0);
                    Helper.ExpectInt(1, entries.Count, "number of entries in BXY");

                    // Confirm that the length matches.  The Binary II file will be padded
                    // to the nearest 128-byte boundary.
                    IFileEntry shkEntry = entries[0];
                    long expLen = shkEntry.DataLength + Binary2.CHUNK_LEN;
                    if (expLen % Binary2.CHUNK_LEN != 0) {
                        expLen += Binary2.CHUNK_LEN - expLen % Binary2.CHUNK_LEN;
                    }
                    Helper.ExpectLong(expLen, newStream.Length, "length of SHK in BXY");
                }
            }

            // Test update of an SEA file.
            using (Stream dataFile = Helper.OpenTestFile("nufx/GSHK11.SEA", false, appHook)) {
                MemoryStream newStream = new MemoryStream();
                using (IArchive archive = NuFX.OpenArchive(dataFile, appHook)) {
                    Helper.CheckNotes(archive, 0, 0);
                    archive.StartTransaction();
                    IFileEntry newEntry = archive.CreateRecord();
                    newEntry.FileName = "Ulysses";
                    SimplePartSource usource =
                        new SimplePartSource(new MemoryStream(Patterns.sUlyssesBytes));
                    archive.AddPart(newEntry, FilePart.RsrcFork, usource,
                        CompressionFormat.Default);
                    archive.CommitTransaction(newStream);
                }
                Debug.Assert(newStream.Length > dataFile.Length);
                using (IArchive archive = NuFX.OpenArchive(newStream, appHook)) {
                    Helper.CheckNotes(archive, 0, 0);
                }
                //TestNuFX_Creation.CopyToFile(newStream, "test-sea.sea");
            }

            // Test update of a BSE file.
            using (Stream dataFile = Helper.OpenTestFile("nufx/DIcEd.BSE", false, appHook)) {
                MemoryStream newStream = new MemoryStream();
                using (IArchive archive = NuFX.OpenArchive(dataFile, appHook)) {
                    Helper.CheckNotes(archive, 0, 0);
                    archive.StartTransaction();
                    IFileEntry newEntry = archive.CreateRecord();
                    newEntry.FileName = "Ulysses";
                    SimplePartSource usource =
                        new SimplePartSource(new MemoryStream(Patterns.sUlyssesBytes));
                    archive.AddPart(newEntry, FilePart.RsrcFork, usource,
                        CompressionFormat.Default);
                    archive.CommitTransaction(newStream);
                }
                Debug.Assert(newStream.Length > dataFile.Length);
                using (IArchive archive = NuFX.OpenArchive(newStream, appHook)) {
                    Helper.CheckNotes(archive, 0, 0);
                }
                using (IArchive archive = Binary2.OpenArchive(newStream, appHook)) {
                    List<IFileEntry> entries = archive.ToList();
                    Helper.CheckNotes(archive, 0, 0);
                    Helper.ExpectInt(1, entries.Count, "number of entries in BSE");
                }
                //TestNuFX_Creation.CopyToFile(newStream, "test-bse.bse");
            }

            // Create an archive with some junk at the front.  This will cause the Binary II
            // header to be ignored as well.
            MemoryStream junkStream = new MemoryStream();
            for (int i = 0; i < 50; i++) {
                junkStream.WriteByte((byte)i);
            }
            using (Stream dataFile = Helper.OpenTestFile("nufx/Samples.BXY", true, appHook)) {
                dataFile.CopyTo(junkStream);
            }
            MemoryStream noJunkStream = new MemoryStream();
            int itemCount;
            using (IArchive archive = NuFX.OpenArchive(junkStream, appHook)) {
                Helper.CheckNotes(archive, 0, 0);
                List<IFileEntry> entries = archive.ToList();
                itemCount = entries.Count;
                // Empty transaction; still causes rewrite.
                archive.StartTransaction();
                archive.CommitTransaction(noJunkStream);
            }
            // See if the file got smaller.  This is not generally guaranteed, as our rewrite
            // could result in an expansion (due to Miranda threads and filename padding), but
            // junk + BXY header + block padding at end should be 200+ bytes.
            long deltaLength = junkStream.Length - noJunkStream.Length;
            if (deltaLength < 0) {
                throw new Exception("Didn't remove junk?");
            }
            using (IArchive archive = NuFX.OpenArchive(noJunkStream, appHook)) {
                Helper.CheckNotes(archive, 0, 0);
                List<IFileEntry> entries = archive.ToList();
                if (entries.Count != itemCount) {
                    throw new Exception("Reconstruction failed");
                }
            }
        }

        public static void TestSimpleDiskImage(AppHook appHook) {
            using (Stream dataFile = Helper.OpenTestFile("nufx/SIMPLE.DOS.SDK", true, appHook)) {
                using (IArchive archive = NuFX.OpenArchive(dataFile, appHook)) {
                    // Should be a single entry with the disk name.  This is a DOS disk, so
                    // it used the value we typed in.
                    Helper.CheckNotes(archive, 0, 0);
                    IFileEntry entry = archive.FindFileEntry("NEW.DISK");
                    Helper.ExpectInt(140 * 1024, (int)entry.DataLength, "disk image length");
                }
            }
        }
    }
}
