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

using AppCommon;
using CommonUtil;
using DiskArc;
using DiskArc.Arc;
using static DiskArc.Defs;

namespace DiskArcTests {
    /// <summary>
    /// Exercise AppleSingle archives.
    /// </summary>
    public class TestAppleSingle_Creation : ITest {
        private static readonly DateTime TEST_DATE1 = new DateTime(1977, 6, 1);
        private static readonly DateTime TEST_DATE2 = new DateTime(1986, 9, 15);

        // Confirm that empty archives work.
        public static void TestEmpty(AppHook appHook) {
            MemoryStream arcStream = new MemoryStream();
            using (IArchive archive = AppleSingle.CreateArchive(2, appHook)) {
                archive.StartTransaction();
                archive.CommitTransaction(arcStream);

                if (AppleSingle_FileEntry.ALWAYS_DATA) {
                    long length, storageSize;
                    CompressionFormat cfmt;
                    IFileEntry entry = archive.FindFileEntry(string.Empty);
                    if (!entry.GetPartInfo(FilePart.DataFork, out length, out storageSize,
                            out cfmt)) {
                        throw new Exception("Unable to get data fork info");
                    }
                    Helper.ExpectLong(0, length, "wrong length");
                    Helper.ExpectLong(0, storageSize, "wrong storage size");
                }
            }
            using (IArchive archive = AppleSingle.OpenArchive(arcStream, appHook)) {
                Helper.CheckNotes(archive, 0, 0);
                try {
                    archive.CommitTransaction(new MemoryStream());
                    throw new Exception("Able to commit without starting");
                } catch (InvalidOperationException) { /*expected*/ }
            }
        }

        // Quick test with a v1 archive.
        public static void TestSimple1(AppHook appHook) {
            const string FILENAME = "SIMPLE.TEST";

            MemoryStream arcStream;
            using (IArchive archive = AppleSingle.CreateArchive(1, appHook)) {
                archive.StartTransaction();
                IFileEntry entry = archive.FindFileEntry(string.Empty);
                entry.FileName = FILENAME;
                entry.FileType = 0x12;
                entry.AuxType = 0x3456;
                entry.Access = FileAttribs.FILE_ACCESS_UNLOCKED;
                entry.ModWhen = TEST_DATE1;
                entry.CreateWhen = TEST_DATE2;
                ((AppleSingle_FileEntry)entry).HomeFS = FileSystemType.ProDOS;
                SimplePartSource partSource =
                    new SimplePartSource(new MemoryStream(Patterns.sUlyssesBytes));
                archive.AddPart(entry, FilePart.DataFork, partSource, CompressionFormat.Default);
                archive.CommitTransaction(arcStream = new MemoryStream());
            }

            using (IArchive archive = AppleSingle.OpenArchive(arcStream, appHook)) {
                IFileEntry entry = archive.FindFileEntry(FILENAME);
                Helper.ExpectByte(0x12, entry.FileType, "filetype mismatch");
                Helper.ExpectInt(0x3456, entry.AuxType, "auxtype mismatch");
                Helper.ExpectInt(FileAttribs.FILE_ACCESS_UNLOCKED, entry.Access, "access mismatch");
                if (entry.ModWhen != TEST_DATE1 || entry.CreateWhen != TEST_DATE2) {
                    throw new Exception("Date mismatch");
                }
                Helper.ExpectBool(true, entry.HasDataFork, "should have data fork");
                Helper.ExpectBool(false, entry.HasRsrcFork, "should not have rsrc fork");
                Helper.ExpectLong(0, entry.RsrcLength, "wrong rsrc length");
                Helper.CompareFileToStream(archive, FILENAME, FilePart.DataFork,
                    Patterns.sUlyssesBytes, Patterns.sUlyssesBytes.Length,
                    new byte[Patterns.sUlyssesBytes.Length]);
            }

            //Helper.CopyToFile(arcStream, "as-test1.as");
        }

        // Quick test with a v2 archive.
        public static void TestSimple2(AppHook appHook) {
            const string FILENAME = "Simple Test";

            MemoryStream arcStream;
            using (IArchive archive = AppleSingle.CreateArchive(2, appHook)) {
                archive.StartTransaction();
                IFileEntry entry = archive.FindFileEntry(string.Empty);
                entry.FileName = FILENAME;
                entry.FileType = 0x12;
                entry.AuxType = 0x3456;
                entry.Access = FileAttribs.FILE_ACCESS_UNLOCKED;
                entry.ModWhen = TEST_DATE1;
                entry.CreateWhen = TEST_DATE2;
                entry.HFSFileType = 0x11223344;
                entry.HFSCreator = 0x55667788;
                SimplePartSource partSource =
                    new SimplePartSource(new MemoryStream(Patterns.sGettysburgBytes));
                archive.AddPart(entry, FilePart.RsrcFork, partSource,
                    CompressionFormat.Uncompressed);
                archive.CommitTransaction(arcStream = new MemoryStream());
            }

            using (IArchive archive = AppleSingle.OpenArchive(arcStream, appHook)) {
                IFileEntry entry = archive.FindFileEntry(FILENAME);
                Helper.ExpectByte(0x12, entry.FileType, "filetype mismatch");
                Helper.ExpectInt(0x3456, entry.AuxType, "auxtype mismatch");
                Helper.ExpectInt(FileAttribs.FILE_ACCESS_UNLOCKED, entry.Access, "access mismatch");
                if (entry.ModWhen != TEST_DATE1 || entry.CreateWhen != TEST_DATE2) {
                    throw new Exception("Date mismatch");
                }
                Helper.ExpectUInt(0x11223344, entry.HFSFileType, "HFS filetype mismatch");
                Helper.ExpectUInt(0x55667788, entry.HFSCreator, "HFS creator mismatch");
                Helper.ExpectLong(0, entry.DataLength, "wrong data length");
                // current implementation always outputs a data fork entry
                Helper.ExpectBool(true, entry.HasDataFork, "should have data fork");
                Helper.ExpectBool(true, entry.HasRsrcFork, "should have rsrc fork");
                Helper.CompareFileToStream(archive, FILENAME, FilePart.RsrcFork,
                    Patterns.sGettysburgBytes, Patterns.sGettysburgBytes.Length,
                    new byte[Patterns.sGettysburgBytes.Length]);
            }

            //Helper.CopyToFile(arcStream, "as-test2.as");
        }

        public static void TestEdit(AppHook appHook) {
            const string FILENAME = "Edit Test!";
            byte[] testBuf = new byte[16384];

            MemoryStream arcStream;
            using (IArchive archive = AppleSingle.CreateArchive(2, appHook)) {
                // No data, no filename, many attributes.
                archive.StartTransaction();
                IFileEntry entry = archive.GetFirstEntry();
                entry.FileType = 0x12;
                entry.AuxType = 0x3456;
                entry.Access = FileAttribs.FILE_ACCESS_UNLOCKED;
                entry.ModWhen = TEST_DATE1;
                entry.CreateWhen = TEST_DATE2;
                entry.HFSFileType = 0x11223344;
                entry.HFSCreator = 0x55667788;
                archive.CommitTransaction(arcStream = new MemoryStream());
            }

            using (IArchive archive = AppleSingle.OpenArchive(arcStream, appHook)) {
                IFileEntry entry = archive.FindFileEntry(string.Empty);
                if (entry.FileType != 0x12 || entry.AuxType != 0x3456 ||
                        entry.Access != FileAttribs.FILE_ACCESS_UNLOCKED ||
                        entry.ModWhen != TEST_DATE1 || entry.CreateWhen != TEST_DATE2 ||
                        entry.HFSFileType != 0x11223344 || entry.HFSCreator != 0x55667788) {
                    throw new Exception("Attribute changed");
                }
                Helper.ExpectBool(false, ((AppleSingle)archive).IsAppleDouble, "is double");

                // Add filename, data fork, resource fork.
                archive.StartTransaction();
                entry.FileName = FILENAME;
                SimplePartSource uPartSource =
                    new SimplePartSource(new MemoryStream(Patterns.sUlyssesBytes));
                if (AppleSingle_FileEntry.ALWAYS_DATA) {
                    archive.DeletePart(entry, FilePart.DataFork);
                }
                archive.AddPart(entry, FilePart.DataFork, uPartSource, CompressionFormat.Default);
                SimplePartSource gPartSource =
                    new SimplePartSource(new MemoryStream(Patterns.sGettysburgBytes));
                archive.AddPart(entry, FilePart.RsrcFork, gPartSource, CompressionFormat.Default);
                archive.CommitTransaction(arcStream = new MemoryStream());

                Helper.CompareFileToStream(archive, FILENAME, FilePart.DataFork,
                    Patterns.sUlyssesBytes, Patterns.sUlyssesBytes.Length, testBuf);
                Helper.CompareFileToStream(archive, FILENAME, FilePart.RsrcFork,
                    Patterns.sGettysburgBytes, Patterns.sGettysburgBytes.Length, testBuf);

                // Remove resource fork, but cancel.
                archive.StartTransaction();
                archive.DeletePart(entry, FilePart.RsrcFork);
                archive.CancelTransaction();

                // Remove data fork.
                archive.StartTransaction();
                archive.DeletePart(entry, FilePart.DataFork);
                long length, storageSize;
                CompressionFormat cfmt;
                if (!entry.GetPartInfo(FilePart.DataFork, out length, out storageSize, out cfmt)) {
                    // This should still work, since we haven't done the commit yet.
                    throw new Exception("Data fork gone before commit");
                }
                archive.CommitTransaction(arcStream = new MemoryStream());

                if (AppleSingle_FileEntry.ALWAYS_DATA) {
                    Helper.ExpectLong(0, entry.DataLength, "wrong always-data length");
                }
                if (!AppleSingle_FileEntry.ALWAYS_DATA &&
                        entry.GetPartInfo(FilePart.DataFork, out length, out storageSize,
                            out cfmt)) {
                    throw new Exception("Found missing data fork");
                }
                if (!entry.GetPartInfo(FilePart.RsrcFork, out length, out storageSize, out cfmt)) {
                    throw new Exception("Couldn't find rsrc fork");
                }
                Helper.ExpectLong(length, Patterns.sGettysburgBytes.Length, "bad length");
                Helper.ExpectLong(storageSize, Patterns.sGettysburgBytes.Length,"bad storage size");
                if (cfmt != CompressionFormat.Uncompressed) {
                    throw new Exception("Wrong compression format");
                }

                Helper.CompareFileToStream(archive, FILENAME, FilePart.RsrcFork,
                    Patterns.sGettysburgBytes, Patterns.sGettysburgBytes.Length, testBuf);
                if (entry.FileType != 0x12 || entry.AuxType != 0x3456 ||
                        entry.Access != FileAttribs.FILE_ACCESS_UNLOCKED ||
                        entry.ModWhen != TEST_DATE1 || entry.CreateWhen != TEST_DATE2 ||
                        entry.HFSFileType != 0x11223344 || entry.HFSCreator != 0x55667788) {
                    throw new Exception("Attribute changed");
                }

                // Add a new data fork.
                archive.StartTransaction();
                //entry = archive.FindFileEntry(FILENAME);
                Helper.ExpectString(FILENAME, entry.FileName, "incorrect filename");
                SimplePartSource aPartSource =
                    new SimplePartSource(new MemoryStream(Patterns.sAbyssBytes));
                if (AppleSingle_FileEntry.ALWAYS_DATA) {
                    archive.DeletePart(entry, FilePart.DataFork);
                }
                archive.AddPart(entry, FilePart.DataFork, aPartSource, CompressionFormat.Default);
                archive.CommitTransaction(arcStream = new MemoryStream());
            }

            using (IArchive archive = AppleSingle.OpenArchive(arcStream, appHook)) {
                Helper.CompareFileToStream(archive, FILENAME, FilePart.DataFork,
                    Patterns.sAbyssBytes, Patterns.sAbyssBytes.Length, testBuf);
                Helper.CompareFileToStream(archive, FILENAME, FilePart.RsrcFork,
                    Patterns.sGettysburgBytes, Patterns.sGettysburgBytes.Length, testBuf);

                archive.StartTransaction();
                IFileEntry entry = archive.FindFileEntry(FILENAME);
                entry.RawFileName = RawData.EMPTY_BYTE_ARRAY;       // remove filename
                entry.FileType = 0x34;
                entry.AuxType = 0x5678;
                entry.Access = FileAttribs.FILE_ACCESS_LOCKED;
                entry.ModWhen = TEST_DATE2;
                entry.CreateWhen = TEST_DATE1;
                entry.HFSFileType = 0;                              // remove HFS attributes
                entry.HFSCreator = 0;
                archive.CommitTransaction(arcStream = new MemoryStream());

                //entry = archive.FindFileEntry(string.Empty);
                Helper.ExpectString(string.Empty, entry.FileName, "incorrect filename");
                if (entry.FileType != 0x34 || entry.AuxType != 0x5678 ||
                        entry.Access != FileAttribs.FILE_ACCESS_LOCKED ||
                        entry.ModWhen != TEST_DATE2 || entry.CreateWhen != TEST_DATE1 ||
                        entry.HFSFileType != 0 || entry.HFSCreator != 0) {
                    throw new Exception("Attribute changed");
                }
            }
        }

        public static void TestDouble(AppHook appHook) {
            MemoryStream arcStream;
            using (IArchive archive = AppleSingle.CreateDouble(2, appHook)) {
                // No data, no filename, many attributes.
                archive.StartTransaction();
                IFileEntry entry = archive.GetFirstEntry();
                entry.FileType = 0x12;
                entry.AuxType = 0x3456;
                entry.Access = FileAttribs.FILE_ACCESS_UNLOCKED;
                entry.ModWhen = TEST_DATE1;
                entry.CreateWhen = TEST_DATE2;
                entry.HFSFileType = 0x11223344;
                entry.HFSCreator = 0x55667788;
                archive.CommitTransaction(arcStream = new MemoryStream());
            }

            using (IArchive archive = AppleSingle.OpenArchive(arcStream, appHook)) {
                IFileEntry entry = archive.FindFileEntry(string.Empty);
                if (entry.FileType != 0x12 || entry.AuxType != 0x3456 ||
                        entry.Access != FileAttribs.FILE_ACCESS_UNLOCKED ||
                        entry.ModWhen != TEST_DATE1 || entry.CreateWhen != TEST_DATE2 ||
                        entry.HFSFileType != 0x11223344 || entry.HFSCreator != 0x55667788) {
                    throw new Exception("Attribute changed");
                }
                Helper.ExpectBool(true, ((AppleSingle)archive).IsAppleDouble, "not double");
            }
        }
    }
}
