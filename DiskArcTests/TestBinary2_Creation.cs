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
    /// Exercise Binary II archives.
    /// </summary>
    public class TestBinary2_Creation : ITest {
        // Validate the contents of the "standard archive".
        public static void TestStdArchive(AppHook appHook) {
            MemoryStream stdStream = GetStandardArchive(appHook);
            using (Binary2 stdArc = Binary2.OpenArchive(stdStream, appHook)) {
                CheckStandardArchive(stdArc);
            }
        }

        // Confirm that empty archives work.
        public static void TestEmpty(AppHook appHook) {
            MemoryStream arcStream = new MemoryStream();
            using (IArchive archive = Binary2.CreateArchive(appHook)) {
                archive.StartTransaction();
                archive.CommitTransaction(arcStream);
            }
            using (IArchive archive = Binary2.OpenArchive(arcStream, appHook)) {
                Helper.CheckNotes(archive, 0, 0);
            }
        }

        public static void TestCreate(AppHook appHook) {
            MemoryStream archiveStream = new MemoryStream();

            using (IArchive archive = Binary2.CreateArchive(appHook)) {
                archive.StartTransaction();
                IFileEntry entry = archive.CreateRecord();
                entry.FileName = "TEST";
                SimplePartSource partSource = new SimplePartSource(new MemoryStream(0));
                archive.AddPart(entry, FilePart.DataFork, partSource, CompressionFormat.Default);
                archive.CommitTransaction(archiveStream);
            }

            // Result should be a 128-byte file with a single, zero-byte entry.
            Helper.ExpectLong(128, archiveStream.Length, "file length");
            using (IArchive archive = Binary2.OpenArchive(archiveStream, appHook)) {
                List<IFileEntry> entries = archive.ToList();
                Helper.ExpectInt(1, entries.Count, "number of entries");
                IFileEntry entry = entries[0];
                if (entry.FileName != "TEST" || entry.DataLength != 0) {
                    throw new Exception("Entry not what was expected");
                }
            }
        }

        public static void TestEdit(AppHook appHook) {
            MemoryStream origStream = GetStandardArchive(appHook);

            using (IArchive archive = Binary2.OpenArchive(origStream, appHook)) {
                MemoryStream newStream = new MemoryStream();

                // Open a file part, then try to start a transaction.
                IFileEntry hello = archive.FindFileEntry("HELLO");
                ArcReadStream testStream = archive.OpenPart(hello, FilePart.DataFork);
                try {
                    archive.StartTransaction();
                    throw new Exception("Started transaction while file open");
                } catch (InvalidOperationException) { /*expected*/ }
                testStream.Dispose();

                archive.StartTransaction();

                // Try to open the same file part again.
                try {
                    archive.OpenPart(hello, FilePart.DataFork);
                    throw new Exception("Opened file while transaction started");
                } catch (InvalidOperationException) { /*expected*/ }

                // Create a record, and commit without setting a filename.
                IFileEntry newEntry1 = archive.CreateRecord();
                SimplePartSource partSource1 = new SimplePartSource(new MemoryStream(0));
                archive.AddPart(newEntry1, FilePart.DataFork, partSource1,
                    CompressionFormat.Default);
                try {
                    archive.CommitTransaction(newStream);
                    throw new Exception("Added entry with empty filename");
                } catch (InvalidOperationException) { /*expected*/ }

                // Repeat, with a duplicate filename.  This should succeed but cause a warning.
                newEntry1.FileName = "HELLO";
                archive.CommitTransaction(newStream);
                origStream.Dispose();
                origStream = newStream;
                if (archive.Notes.ErrorCount != 0 || archive.Notes.WarningCount == 0) {
                    throw new Exception("Didn't find expected duplicate-name warning");
                }

                // Resolve the duplication by deleting the first instance.
                //newEntry1 = archive.FindFileEntry("HELLO");
                Helper.ExpectString("HELLO", newEntry1.FileName, "incorrect filename");
                archive.StartTransaction();
                partSource1 = new SimplePartSource(new MemoryStream(0));
                try {
                    // Add a part to an existing entry that already has the part.
                    archive.AddPart(newEntry1, FilePart.DataFork, partSource1,
                        CompressionFormat.Default);
                } catch (ArgumentException) {
                    /*expected*/
                    partSource1.Dispose();
                }
                archive.DeleteRecord(newEntry1);
                archive.CommitTransaction(newStream = new MemoryStream());
                origStream.Dispose();       // not strictly necessary for MemoryStream
                if (archive.Notes.ErrorCount != 0 || archive.Notes.WarningCount != 0) {
                    throw new Exception("Deleting duplicate didn't make warning go away");
                }

                try {
                    string foo = newEntry1.FileName;
                    throw new Exception("Got filename from deleted entry");
                } catch (InvalidOperationException) { /*expected*/ }

                archive.StartTransaction();
                IFileEntry newEntry2 = archive.CreateRecord();
                newEntry2.FileName = "FOO/BAR/ACK";
                SimplePartSource partSource2 = new SimplePartSource(new MemoryStream(0));
                archive.AddPart(newEntry2, FilePart.DataFork, partSource2,
                    CompressionFormat.Default);
                try {
                    archive.AddPart(newEntry2, FilePart.DataFork, partSource2,
                        CompressionFormat.Default);
                    throw new Exception("Added the same part twice");
                } catch (ArgumentException) { /*expected*/ }
                archive.CommitTransaction(newStream = new MemoryStream());
                origStream.Dispose();
                origStream = newStream;
                if (archive.Notes.ErrorCount != 0 || archive.Notes.WarningCount == 0) {
                    throw new Exception("Didn't find expected missing-dir warning");
                }

                // Remove the problematic new entry.
                //newEntry2 = archive.FindFileEntry("FOO/BAR/ACK");
                Helper.ExpectString("FOO/BAR/ACK", newEntry2.FileName, "incorrect filename");
                archive.StartTransaction();
                archive.DeleteRecord(newEntry2);
                partSource2 = new SimplePartSource(new MemoryStream(0));
                try {
                    archive.AddPart(newEntry2, FilePart.DataFork, partSource2,
                        CompressionFormat.Default);
                    throw new Exception("Added part to deleted entry");
                } catch (ArgumentException) {
                    /*expected*/
                    partSource2.Dispose();
                }
                archive.CommitTransaction(newStream = new MemoryStream());
                if (archive.Notes.ErrorCount != 0 || archive.Notes.WarningCount != 0) {
                    throw new Exception("Deleting didn't make warning go away");
                }

                // Play with very long file names.
                archive.StartTransaction();
                int beforeCount = archive.ToList().Count;
                IFileEntry newEntry3 = archive.CreateRecord();
                newEntry3.FileName = "VERY/LONG/BUT/VALID/ENTRY/NAME";
                try {
                    newEntry3.FileName = "VERY.LONG.FILE.NAME";
                    throw new Exception("Excessively long name allowed");
                } catch (ArgumentException) { /*expected*/ }
                try {
                    newEntry3.FileName =
                        "SUM.OF.PARTS/IS.TOO.LONG/TO.BE.VALID/SIXTY.FOUR.CHARS/MAX.LENGTH/ALLOWED";
                    throw new Exception("Excessively long path allowed");
                } catch (ArgumentException) { /*expected*/ }

                // Now delete it before committing.
                archive.DeleteRecord(newEntry3);
                archive.CommitTransaction(newStream = new MemoryStream());
                if (archive.ToList().Count != beforeCount) {
                    throw new Exception("Archive grew even with deleted entry");
                }

                // Try to add directories.
                archive.StartTransaction();
                IFileEntry newEntry4 = archive.CreateRecord();
                newEntry4.FileName = "FOO";
                newEntry4.FileType = FileAttribs.FILE_TYPE_DIR;
                // dir doesn't need data part
                IFileEntry newEntry5 = archive.CreateRecord();
                newEntry5.FileName = "BAR";
                newEntry5.FileType = FileAttribs.FILE_TYPE_DIR;
                SimplePartSource partSource5 = new SimplePartSource(new MemoryStream(1));
                archive.AddPart(newEntry5, FilePart.DataFork, partSource5,
                    CompressionFormat.Default);
                try {
                    archive.CommitTransaction(newStream = new MemoryStream());
                    throw new Exception("Directory with data allowed");
                } catch (InvalidOperationException) { /*expected*/ }
                archive.CancelTransaction();

                // Add one dir and one regular file.
                archive.StartTransaction();
                newEntry4 = archive.CreateRecord();
                newEntry4.FileName = "FOO";
                newEntry4.FileType = FileAttribs.FILE_TYPE_DIR;
                newEntry5 = archive.CreateRecord();
                newEntry5.FileName = "FOO/BAR";
                partSource5 = new SimplePartSource(new MemoryStream(0));
                archive.AddPart(newEntry5, FilePart.DataFork, partSource5,
                    CompressionFormat.Default);
                archive.CommitTransaction(newStream = new MemoryStream());
                if (archive.Notes.ErrorCount != 0 || archive.Notes.WarningCount != 0) {
                    throw new Exception("Something is fishy");
                }

                // Add a file that uses a non-dir in a partial path.
                archive.StartTransaction();
                IFileEntry newEntry6 = archive.CreateRecord();
                newEntry6.FileName = "FOO/BAR/ACK";
                SimplePartSource partSource6 = new SimplePartSource(new MemoryStream(0));
                archive.AddPart(newEntry6, FilePart.DataFork, partSource6,
                    CompressionFormat.Default);
                archive.CommitTransaction(newStream = new MemoryStream());
                if (archive.Notes.ErrorCount != 0 || archive.Notes.WarningCount == 0) {
                    throw new Exception("No warning for partial path with non-dir");
                }

                archive.StartTransaction();
                archive.DeleteRecord(archive.FindFileEntry("FOO/BAR/ACK"));
                archive.CommitTransaction(newStream = new MemoryStream());
                if (archive.Notes.ErrorCount != 0 || archive.Notes.WarningCount != 0) {
                    throw new Exception("Deleting bad entry didn't fix warning");
                }

                // Delete an early record, then verify that the archive didn't break.
                archive.StartTransaction();
                archive.DeleteRecord(archive.FindFileEntry("ULYSSES"));
                archive.CommitTransaction(newStream = new MemoryStream());

                Helper.CompareFileToStream(archive, "SUBDIR/NOT.SQUEEZABLE.", FilePart.DataFork,
                    Patterns.sBytePattern, Patterns.sBytePattern.Length, new byte[32768]);
            }
        }

        public static void TestParts(AppHook appHook) {
            MemoryStream origStream = GetStandardArchive(appHook);

            // Format a text sample.
            using MemoryStream gascii = new MemoryStream();
            using StreamWriter writer = new StreamWriter(gascii, System.Text.Encoding.ASCII);
            writer.Write(Patterns.sGettysburg);
            writer.Flush();

            using (IArchive archive = Binary2.OpenArchive(origStream, appHook)) {
                // Replace a file's contents in the middle of the archive.
                IFileEntry ulysses = archive.FindFileEntry("ULYSSES");
                archive.StartTransaction();
                try {
                    archive.DeletePart(ulysses, FilePart.RsrcFork);
                    throw new Exception("Deleted non-existent part");
                } catch (FileNotFoundException) { /*expected*/ }

                archive.DeletePart(ulysses, FilePart.DataFork);
                long length, storageSize;
                CompressionFormat cfmt;
                if (!ulysses.GetPartInfo(FilePart.DataFork, out length, out storageSize, out cfmt)) {
                    // This should still work, since we haven't done the commit yet.
                    throw new Exception("Data fork gone before commit");
                }
                Helper.ExpectLong(ulysses.DataLength, length, "length mismatch");
                try {
                    archive.DeletePart(ulysses, FilePart.DataFork);
                    throw new Exception("Deleted same part twice");
                } catch (FileNotFoundException) { /*expected*/ }

                // Replace Ulysses with Gettysburg.
                archive.AddPart(ulysses, FilePart.DataFork, new SimplePartSource(gascii),
                    CompressionFormat.Default);

                // Try to do it again.
                SimplePartSource excSource = new SimplePartSource(new MemoryStream());
                try {
                    archive.AddPart(ulysses, FilePart.DataFork, excSource,
                        CompressionFormat.Default);
                    throw new Exception("Allowed to add same part twice");
                } catch (ArgumentException) {
                    /*expected*/
                    excSource.Dispose();
                }
                // Update the name to match.
                ulysses.FileName = "GETTYSBURG";
                MemoryStream newArcStream = new MemoryStream();
                archive.CommitTransaction(newArcStream);

                origStream = newArcStream;
            }

            // Open updated stream in entirely new archive instance to verify.

            using (IArchive archive = Binary2.OpenArchive(origStream, appHook)) {
                byte[] tmpBuf = new byte[32768];

                // See if it matches.
                Helper.CompareFileToStream(archive, "GETTYSBURG", FilePart.DataFork,
                    Patterns.sGettysburgBytes, Patterns.sGettysburgBytes.Length, tmpBuf);

                // Check a file at the end.
                Helper.CompareFileToStream(archive, "SUBDIR/NOT.SQUEEZABLE.", FilePart.DataFork,
                    Patterns.sBytePattern, Patterns.sBytePattern.Length, tmpBuf);

                // Change it to a directory.
                archive.StartTransaction();
                IFileEntry getty = archive.FindFileEntry("GETTYSBURG");
                archive.DeletePart(getty, FilePart.DataFork);
                getty.FileType = FileAttribs.FILE_TYPE_DIR;

                // Put something in it.
                IFileEntry fourScore = archive.CreateRecord();
                fourScore.FileName = "GETTYSBURG/FOUR.SCORE";
                fourScore.FileType = FileAttribs.FILE_TYPE_DIR;

                archive.CommitTransaction(new MemoryStream());
                if (archive.Notes.WarningCount != 0 || archive.Notes.ErrorCount != 0) {
                    throw new Exception("Unexpected warnings/errors");
                }
            }

            origStream.Close();
        }

        public static void TestBigSmall(AppHook appHook) {
            MemoryStream empty = new MemoryStream(0);
            using (Binary2 archive = Binary2.OpenArchive(empty, appHook)) {
                if (archive.ToList().Count != 0) {
                    throw new Exception("Should have been empty");
                }

                // Add 256 entries.
                archive.StartTransaction();
                for (int i = 0; i < Binary2.MAX_RECORDS; i++) {
                    SimplePartSource emptySource = new SimplePartSource(new MemoryStream(0));
                    IFileEntry newEntry = archive.CreateRecord();
                    newEntry.FileName = "FILE." + i;
                    archive.AddPart(newEntry, FilePart.DataFork, emptySource,
                        CompressionFormat.Default);
                }
                MemoryStream newStream = new MemoryStream();
                archive.CommitTransaction(newStream);
                if (archive.ToList().Count != Binary2.MAX_RECORDS) {
                    throw new Exception("Should be full");
                }

                // Add one too many.  Should not fail until we try to commit.
                archive.StartTransaction();
                IFileEntry waferThinEntry = archive.CreateRecord();
                SimplePartSource emptySource1 = new SimplePartSource(new MemoryStream(0));
                archive.AddPart(waferThinEntry, FilePart.DataFork, emptySource1,
                    CompressionFormat.Default);
                try {
                    archive.CommitTransaction(newStream = new MemoryStream());
                    throw new Exception("Allowed to create too many files");
                } catch (InvalidOperationException) {
                    // expected
                }
                archive.CancelTransaction();

                // Delete everything.
                archive.StartTransaction();
                List<IFileEntry> entries = archive.ToList();
                foreach (IFileEntry entry in entries) {
                    archive.DeleteRecord(entry);
                }
                archive.CommitTransaction(newStream = new MemoryStream());

                if (newStream.Length != 0) {
                    throw new Exception("Deleting all records did not result in empty file");
                }
            }
        }

        public static void TestModInPlace(AppHook appHook) {
            MemoryStream origStream = GetStandardArchive(appHook);

            using (IArchive archive = Binary2.OpenArchive(origStream, appHook)) {
                IFileEntry entry = archive.FindFileEntry("HELLO");
                try {
                    entry.CreateWhen = DateTime.Now;
                    throw new Exception("Changed date without transaction");
                } catch (InvalidOperationException) { /*expected*/ }

                archive.StartTransaction();
                entry.CreateWhen = DateTime.Now;

                entry = archive.FindFileEntry("ULYSSES");
                entry.FileType = FileAttribs.FILE_TYPE_BIN;

                entry = archive.FindFileEntry("SUBDIR/NOT.SQUEEZABLE.");
                entry.FileName = "FOO";

                if (archive.IsReconstructionNeeded) {
                    throw new Exception("Reconstruction should not be needed");
                }

                archive.CommitTransaction(null);

                // Confirm flag gets set.
                archive.StartTransaction();
                entry = archive.FindFileEntry("FOO");
                archive.DeletePart(entry, FilePart.DataFork);
                if (!archive.IsReconstructionNeeded) {
                    throw new Exception("Reconstruction should be needed");
                }
                archive.CancelTransaction();
                if (archive.IsReconstructionNeeded) {
                    throw new Exception("Reconstruction flag didn't reset");
                }

                archive.StartTransaction();
                entry = archive.FindFileEntry("FOO");
                archive.DeletePart(entry, FilePart.DataFork);
                // Dispose of archive with transaction pending.  Should cancel.
            }
        }

        // Archive damage test.
        public static void TestTruncated(AppHook appHook) {
            MemoryStream origStream = GetStandardArchive(appHook);
            // Shave a chunk off the last file.
            origStream.SetLength(origStream.Length - 128);

            using (IArchive archive = Binary2.OpenArchive(origStream, appHook)) {
                List<IFileEntry> entries = archive.ToList();
                if (!entries[entries.Count - 1].IsDamaged && !archive.IsDubious) {
                    throw new Exception("No indication of truncation");
                }
            }

            // Binary II does not include checksums.  Files compressed with Squeeze will have
            // one, but we verified that in the compression test code.
        }

        // Failed I/O test.
        public static void TestReadFailure(AppHook appHook) {
            MemoryStream origStream = GetStandardArchive(appHook);
            MemoryStream newStream = new MemoryStream();
            using (IArchive archive = Binary2.OpenArchive(origStream, appHook)) {
                archive.StartTransaction();
                // Delete the first record.
                archive.DeleteRecord(archive.FindFileEntry("HELLO"));

                // Add a new record with a defective part source.
                IFileEntry entry = archive.CreateRecord();
                entry.FileName = "FAIL";
                FailingPartSource failSource = new FailingPartSource(new MemoryStream(40000));
                archive.AddPart(entry, FilePart.DataFork, failSource, CompressionFormat.Default);

                // Exception from part source should come all the way out.
                try {
                    archive.CommitTransaction(newStream);
                    throw new Exception("Failed to fail");
                } catch (FakeException) {/*expected*/}

                // The partially-written stream should have been truncated.
                Helper.ExpectLong(0, newStream.Length, "new stream should have been reset");

                // "Fix" the part source and commit.
                archive.DeletePart(entry, FilePart.DataFork);
                SimplePartSource goodSource =
                    new SimplePartSource(new MemoryStream(Patterns.sHelloWorldBytes));
                archive.AddPart(entry, FilePart.DataFork, goodSource, CompressionFormat.Default);
                archive.CommitTransaction(newStream);
                Helper.CheckNotes(archive, 0, 0);
            }

            // Check it.  If we updated file offsets in the wrong place, the "copy existing record
            // data" function will grab the wrong chunk of the file.
            using (IArchive archive = Binary2.OpenArchive(newStream, appHook)) {
                Helper.CheckNotes(archive, 0, 0);
                Helper.CompareFileToStream(archive, "ULYSSES", FilePart.DataFork,
                    Patterns.sUlyssesBytes, Patterns.sUlysses.Length, new byte[16384]);
            }
        }

        #region Utilities

        private static List<Helper.FileAttr> sStdContents = new List<Helper.FileAttr>() {
            new Helper.FileAttr("HELLO", 14, -1, 128,
                FileAttribs.FILE_TYPE_TXT, 0x0000),
            new Helper.FileAttr("EMPTY", 0, -1, 0,
                FileAttribs.FILE_TYPE_NON, 0x0000),
            new Helper.FileAttr("ULYSSES", -1, -1, 1920,
                FileAttribs.FILE_TYPE_TXT, 0x0000),
            new Helper.FileAttr("SUBDIR", 0, -1, 0,
                FileAttribs.FILE_TYPE_DIR, 0x0000),
            new Helper.FileAttr("SUBDIR/NOT.SQUEEZABLE.", 16384, -1, 16384,
                FileAttribs.FILE_TYPE_BIN, 0x1234),
            new Helper.FileAttr("SUBDIR/SQUEEZE.ME", -1, -1, 896,
                FileAttribs.FILE_TYPE_BIN, 0xf00d),
        };

        private static readonly DateTime TEST_DATE1 = new DateTime(1977, 6, 1);
        private static readonly DateTime TEST_DATE2 = new DateTime(1986, 9, 15);

        /// <summary>
        /// Original copy of the "standard" test archive.  Do not modify.
        /// </summary>
        private static readonly MemoryStream? sStandardArchive = null;

        /// <summary>
        /// Generates a "standard" archive with a bunch of files in it.
        /// </summary>
        internal static MemoryStream GetStandardArchive(AppHook appHook) {
            if (sStandardArchive != null) {
                // Already created, just make a copy.
                MemoryStream newStream = new MemoryStream((int)sStandardArchive.Length);
                sStandardArchive.Position = 0;
                sStandardArchive.CopyTo(newStream);
                newStream.Position = 0;
                return newStream;
            }

            MemoryStream output = new MemoryStream();
            using (IArchive archive = Binary2.CreateArchive(appHook)) {
                archive.StartTransaction();

                // #1: simple "hello, world!" file
                IFileEntry newEntry1 = archive.CreateRecord();
                newEntry1.FileName = "HELLO";
                newEntry1.FileType = FileAttribs.FILE_TYPE_TXT;
                newEntry1.CreateWhen = TEST_DATE1;
                newEntry1.ModWhen = TEST_DATE2;
                SimplePartSource partSource1 =
                    new SimplePartSource(new MemoryStream(Patterns.sHelloWorldBytes));
                archive.AddPart(newEntry1, FilePart.DataFork, partSource1,
                    CompressionFormat.Default);

                // #2: empty file
                IFileEntry newEntry2 = archive.CreateRecord();
                newEntry2.FileName = "EMPTY";
                SimplePartSource partSource2 = new SimplePartSource(new MemoryStream(0));
                archive.AddPart(newEntry2, FilePart.DataFork, partSource2,
                    CompressionFormat.Default);

                // #3: compressible poetry
                IFileEntry newEntry3 = archive.CreateRecord();
                newEntry3.FileName = "ULYSSES";
                newEntry3.FileType = FileAttribs.FILE_TYPE_TXT;
                newEntry3.CreateWhen = new DateTime(1833, 1, 1);    // not representable
                newEntry3.ModWhen = TEST_DATE2;
                SimplePartSource partSource3 =
                    new SimplePartSource(new MemoryStream(Patterns.sUlyssesBytes));
                archive.AddPart(newEntry3, FilePart.DataFork, partSource3,
                    CompressionFormat.Default);

                // #4: subdirectory
                IFileEntry newEntry4 = archive.CreateRecord();
                newEntry4.FileName = "SUBDIR";
                newEntry4.FileType = FileAttribs.FILE_TYPE_DIR;
                newEntry4.CreateWhen = TEST_DATE1;
                newEntry4.ModWhen = TEST_DATE1;

                // #5: un-squeezable file, in subdir
                IFileEntry newEntry5 = archive.CreateRecord();
                newEntry5.FileName = "SUBDIR/NOT.SQUEEZABLE.";
                newEntry5.FileType = FileAttribs.FILE_TYPE_BIN;
                newEntry5.AuxType = 0x1234;
                newEntry5.CreateWhen = TEST_DATE1;
                newEntry5.ModWhen = TEST_DATE1;
                MemoryStream ms5 = new MemoryStream(Patterns.sBytePattern, false);
                SimplePartSource partSource5 = new SimplePartSource(ms5);
                archive.AddPart(newEntry5, FilePart.DataFork, partSource5,
                    CompressionFormat.Default);

                // #6: squeezable file, in subdir
                IFileEntry newEntry6 = archive.CreateRecord();
                newEntry6.FileName = "SUBDIR/SQUEEZE.ME";
                newEntry6.FileType = FileAttribs.FILE_TYPE_BIN;
                newEntry6.AuxType = 0xf00d;
                newEntry6.CreateWhen = TEST_DATE2;
                newEntry6.ModWhen = TEST_DATE2;
                MemoryStream ms6 = new MemoryStream(Patterns.sRunPattern, false);
                SimplePartSource partSource6 = new SimplePartSource(ms6);
                archive.AddPart(newEntry6, FilePart.DataFork, partSource6,
                    CompressionFormat.Default);

                archive.CommitTransaction(output);

                // Test archive in its post-commit state, to verify that we properly updated
                // file offsets and other internal values.
                CheckStandardArchive(archive);
            }
            //CopyToFile(output, "std-archive");
            return output;
        }

        /// <summary>
        /// Verifies that the contents of the standard archive match expectations.
        /// </summary>
        private static void CheckStandardArchive(IArchive stdArc) {
            if (stdArc.Notes.WarningCount != 0 || stdArc.Notes.ErrorCount != 0) {
                throw new Exception("Standard archive isn't clean");
            }

            // Check table of contents.
            Helper.ValidateContents(stdArc, sStdContents);

            // Check some dates.
            IFileEntry entry;
            entry = stdArc.FindFileEntry("HELLO");
            if (entry.CreateWhen != TEST_DATE1 || entry.ModWhen != TEST_DATE2) {
                throw new Exception("Bad date on HELLO");
            }
            entry = stdArc.FindFileEntry("ULYSSES");
            if (entry.CreateWhen != TimeStamp.NO_DATE || entry.ModWhen != TEST_DATE2) {
                throw new Exception("Bad dates on ULYSSES");
            }

            // Extract individual entries and compare to original.
            byte[] readBuf = new byte[32768];
            MemoryStream ms1 = new MemoryStream();
            using StreamWriter writer1 = new StreamWriter(ms1, System.Text.Encoding.ASCII);
            writer1.Write(Patterns.sHelloWorld);
            writer1.Flush();
            Helper.CompareFileToStream(stdArc, "HELLO", FilePart.DataFork,
                ms1.GetBuffer(), ms1.Length, readBuf);

            MemoryStream ms2 = new MemoryStream(0);
            Helper.CompareFileToStream(stdArc, "EMPTY", FilePart.DataFork,
                ms2.GetBuffer(), ms2.Length, readBuf);

            MemoryStream ms3 = new MemoryStream();
            using StreamWriter writer3 = new StreamWriter(ms3, System.Text.Encoding.ASCII);
            writer3.Write(Patterns.sUlysses);
            writer3.Flush();
            Helper.CompareFileToStream(stdArc, "ULYSSES", FilePart.DataFork,
                ms3.GetBuffer(), ms3.Length, readBuf);

            IFileEntry dirEntry = stdArc.FindFileEntry("SUBDIR");
            if (!dirEntry.IsDirectory || dirEntry.DataLength != 0 ||
                    dirEntry.FileType != FileAttribs.FILE_TYPE_DIR) {
                throw new Exception("Bad directory entry");
            }

            Helper.CompareFileToStream(stdArc, "SUBDIR/NOT.SQUEEZABLE.", FilePart.DataFork,
                Patterns.sBytePattern, Patterns.sBytePattern.Length, readBuf);

            Helper.CompareFileToStream(stdArc, "SUBDIR/SQUEEZE.ME", FilePart.DataFork,
                Patterns.sRunPattern, Patterns.sRunPattern.Length, readBuf);
        }

        public static void CopyToFile(Stream source, string fileName) {
            string pathName = Helper.DebugFileDir + fileName + ".bny";
            using (FileStream output =
                    new FileStream(pathName, FileMode.Create, FileAccess.Write)) {
                source.Position = 0;
                source.CopyTo(output);
            }
        }

        #endregion Utilities
    }
}
