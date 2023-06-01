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
using System.IO;
using System.Linq;

using CommonUtil;
using AppCommon;
using DiskArc;
using DiskArc.Arc;
using static DiskArc.Defs;

namespace DiskArcTests {
    /// <summary>
    /// Exercise ZIP archives.
    /// </summary>
    public class TestZip_Creation : ITest {
        private const int GRINDER_ITERATIONS = 20;

        // Validate the contents of the "standard archive".
        public static void TestStdArchive(AppHook appHook) {
            MemoryStream stdStream = GetStandardArchive(appHook);
            using (Zip stdArc = Zip.OpenArchive(stdStream, appHook)) {
                CheckStandardArchive(stdArc);

                //Debug.WriteLine("Std archive:");
                //Debug.WriteLine(Helper.GetArchiveSummary(stdArc));
            }
        }

        // Confirm that empty archives work.
        public static void TestEmpty(AppHook appHook) {
            MemoryStream arcStream = new MemoryStream();
            using (IArchive archive = Zip.CreateArchive(appHook)) {
                archive.StartTransaction();
                archive.CommitTransaction(arcStream);
            }
            using (IArchive archive = Zip.OpenArchive(arcStream, appHook)) {
                Helper.CheckNotes(archive, 0, 0);
                try {
                    archive.CommitTransaction(new MemoryStream());
                    throw new Exception("Able to commit without opening");
                } catch (InvalidOperationException) { /*expected*/ }
            }
        }

        // Perform multiple edits.
        public static void TestEdit(AppHook appHook) {
            const string ARCHIVE_COMMENT = "Testing 1 2 3 \u25a0";
            const string FILE_COMMENT = "wa\r\nhoo";
            MemoryStream origStream = GetStandardArchive(appHook);

            using (IArchive archive = Zip.OpenArchive(origStream, appHook)) {
                MemoryStream newStream = new MemoryStream();

                // Open a file part, then try to start a transaction.
                IFileEntry hello = archive.FindFileEntry("HELLO");
                ArcReadStream testStream = archive.OpenPart(hello, FilePart.DataFork);
                testStream.CopyTo(Stream.Null);
                if (testStream.ChkResult != ArcReadStream.CheckResult.Success) {
                    throw new Exception("Checksum not verified");
                }
                try {
                    archive.StartTransaction();
                    throw new Exception("Started transaction while file open");
                } catch (InvalidOperationException) { /*expected*/ }
                testStream.Dispose();

                // File is closed, start a transaction.
                archive.StartTransaction();
                archive.Comment = ARCHIVE_COMMENT;
                archive.FindFileEntry("foo/bar/random").Comment = FILE_COMMENT;

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

                // Repeat, with a duplicate filename.  This is allowed.
                newEntry1.FileName = "HELLO";
                archive.CommitTransaction(newStream);
                origStream.Dispose();
                origStream = newStream;
                if (archive.Notes.ErrorCount != 0 || archive.Notes.WarningCount != 0) {
                    throw new Exception("Got unexpected complaint about duplicate name");
                }

                // Resolve the duplication by deleting the first instance.
                newEntry1 = archive.FindFileEntry("HELLO");
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
                origStream.Dispose();

                archive.StartTransaction();
                archive.Comment = "should not stick";
                archive.CreateRecord();
                archive.CreateRecord();
                archive.CancelTransaction();

                // Create an entry and try to add the same part twice.
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
                try {
                    archive.AddPart(newEntry2, FilePart.RsrcFork, partSource2,
                        CompressionFormat.Default);
                    throw new Exception("Added resource fork to ZIP file");
                } catch (ArgumentException) { /*expected*/ }
                archive.CommitTransaction(newStream = new MemoryStream());
                origStream.Dispose();
                origStream = newStream;

                // Remove the new entry.
                newEntry2 = archive.FindFileEntry("FOO/BAR/ACK");
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

                // Create an entry with a long name, then delete it before committing.
                archive.StartTransaction();
                int beforeCount = archive.Count;
                IFileEntry newEntry3 = archive.CreateRecord();
                newEntry3.FileName = "VERY/LONG/BUT/VALID/ENTRY/NAME";
                archive.DeleteRecord(newEntry3);
                archive.CommitTransaction(newStream = new MemoryStream());
                if (archive.Count != beforeCount) {
                    throw new Exception("Archive grew even with deleted entry");
                }

                // Delete an early record, then verify that the archive didn't break.
                archive.StartTransaction();
                archive.DeleteRecord(archive.FindFileEntry("Ulysses"));
                archive.CommitTransaction(newStream = new MemoryStream());

                byte[] rndBuf = new byte[12345];
                Patterns.GenerateUncompressible(rndBuf, 0, rndBuf.Length);
                Helper.CompareFileToStream(archive, "foo/bar/random", FilePart.DataFork,
                    rndBuf, rndBuf.Length, new byte[16384]);

                if (archive.Comment != ARCHIVE_COMMENT) {
                    throw new Exception("Lost the archive comment?");
                }
                if (archive.FindFileEntry("foo/bar/random").Comment != FILE_COMMENT) {
                    throw new Exception("Lost the file comment?");
                }
            }
        }

        public static void TestParts(AppHook appHook) {
            MemoryStream arcStream = GetStandardArchive(appHook);

            using (IArchive archive = Zip.OpenArchive(arcStream, appHook)) {
                // Replace a file's contents in the middle of the archive.
                IFileEntry ulysses = archive.FindFileEntry("ULYSSES");  // case-insensitive name
                archive.StartTransaction();
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
                archive.AddPart(ulysses, FilePart.DataFork,
                    new SimplePartSource(new MemoryStream(Patterns.sGettysburgBytes)),
                    CompressionFormat.Default);
                SimplePartSource excSource =
                    new SimplePartSource(new MemoryStream(Patterns.sGettysburgBytes));
                try {
                    archive.AddPart(ulysses, FilePart.DataFork, excSource,
                        CompressionFormat.Default);
                    throw new Exception("Allowed to add same part twice");
                } catch (ArgumentException) {
                    /*expected*/
                    excSource.Dispose();
                }
                archive.CommitTransaction(arcStream = new MemoryStream());
            }

            // Open updated stream in entirely new archive instance to verify.
            using (IArchive archive = Zip.OpenArchive(arcStream, appHook)) {
                byte[] tmpBuf = new byte[32768];
                Helper.CheckNotes(archive, 0, 0);

                // See if it matches.
                Helper.CompareFileToStream(archive, "ULYSSES", FilePart.DataFork,
                    Patterns.sGettysburgBytes, Patterns.sGettysburgBytes.Length, tmpBuf);

                // Check a file at the end too.
                Helper.CompareFileToStream(archive, "foo/bar/crushably-delicious-datastuffs",
                    FilePart.DataFork, Patterns.sRunPattern, Patterns.sRunPattern.Length, tmpBuf);
            }

            arcStream.Close();
        }

        public static void TestGrind(AppHook appHook) {
            const int SEED = 12345;

            using (IArchive archive = Zip.CreateArchive(appHook)) {
                Grindarc grinder = new Grindarc(archive, SEED);
                grinder.Execute(GRINDER_ITERATIONS);
            }
        }

        // Failed I/O test.
        public static void TestReadFailure(AppHook appHook) {
            MemoryStream origStream = GetStandardArchive(appHook);
            MemoryStream newStream = new MemoryStream();
            using (IArchive archive = Zip.OpenArchive(origStream, appHook)) {
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
            // data" function will grab the wrong chunk of the file, and this will fail.
            using (IArchive archive = Zip.OpenArchive(newStream, appHook)) {
                Helper.CheckNotes(archive, 0, 0);
                Helper.CompareFileToStream(archive, "ULYSSES", FilePart.DataFork,
                    Patterns.sUlyssesBytes, Patterns.sUlysses.Length, new byte[16384]);
            }
        }

        #region Utilities

        // NOTE: the compressed sizes are subject to change, because Deflate is performed
        // by a .NET library rather than our code.  It's also dependent on what value is
        // used for CompressionLevel.
        private static List<Helper.FileAttr> sStdContents = new List<Helper.FileAttr>() {
            new Helper.FileAttr("Hello", 14, 14),
            new Helper.FileAttr("Empty!", 0, 0),
            new Helper.FileAttr("Ulysses", 2987, -1 /*1606 or 1586*/),
            new Helper.FileAttr("foo/bar/random", 12345, 12345),
            new Helper.FileAttr("foo/bar/crushably-delicious-datastuffs", 32000, -1 /*259*/),
        };

        private static readonly DateTime TEST_DATE1 = new DateTime(1977, 6, 1);
        private static readonly DateTime TEST_DATE2 = new DateTime(1986, 9, 15);

        /// <summary>
        /// Original copy of the "standard" test archive.  Do not modify.
        /// </summary>
        private static readonly MemoryStream? sStandardArchive = null;

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
            using (IArchive archive = Zip.CreateArchive(appHook)) {
                archive.StartTransaction();
                archive.Comment = Patterns.sHelloWorld;

                // #1: "hello, world!" file with a comment
                IFileEntry newEntry1 = archive.CreateRecord();
                newEntry1.FileName = "Hello";
                newEntry1.FileType = FileAttribs.FILE_TYPE_TXT;
                newEntry1.CreateWhen = TEST_DATE1;
                newEntry1.ModWhen = TEST_DATE2;
                SimplePartSource partSource1 =
                    new SimplePartSource(new MemoryStream(Patterns.sHelloWorldBytes));
                archive.AddPart(newEntry1, FilePart.DataFork, partSource1,
                    CompressionFormat.Default);
                // Add a comment.
                newEntry1.Comment = Patterns.sAbyss;

                // #2: empty file
                IFileEntry newEntry2 = archive.CreateRecord();
                newEntry2.FileName = "Empty!";
                SimplePartSource partSource2 = new SimplePartSource(new MemoryStream(0));
                archive.AddPart(newEntry2, FilePart.DataFork, partSource2,
                    CompressionFormat.Default);

                // #3: compressible poetry
                IFileEntry newEntry3 = archive.CreateRecord();
                newEntry3.FileName = "Ulysses";
                newEntry3.FileType = FileAttribs.FILE_TYPE_TXT;
                newEntry3.CreateWhen = new DateTime(1833, 1, 1);    // not representable
                newEntry3.ModWhen = TEST_DATE2;
                SimplePartSource partSource3 =
                    new SimplePartSource(new MemoryStream(Patterns.sUlyssesBytes));
                archive.AddPart(newEntry3, FilePart.DataFork, partSource3,
                    CompressionFormat.Default);

                // #4: un-compressible data fork, with partial pathname
                IFileEntry newEntry4 = archive.CreateRecord();
                newEntry4.FileName = "foo/bar/random";
                newEntry4.FileType = FileAttribs.FILE_TYPE_BIN;
                newEntry4.AuxType = 0x1234;
                newEntry4.CreateWhen = TEST_DATE1;
                newEntry4.ModWhen = TEST_DATE1;
                byte[] rndBuf = new byte[12345];
                Patterns.GenerateUncompressible(rndBuf, 0, rndBuf.Length);
                MemoryStream ms4 = new MemoryStream(rndBuf, false);
                SimplePartSource partSource4 = new SimplePartSource(ms4);
                archive.AddPart(newEntry4, FilePart.DataFork, partSource4,
                    CompressionFormat.Default);

                // #5: compressible file, with long partial pathname
                IFileEntry newEntry5 = archive.CreateRecord();
                newEntry5.FileName = "foo/bar/crushably-delicious-datastuffs";
                newEntry5.FileType = FileAttribs.FILE_TYPE_BIN;
                newEntry5.AuxType = 0xf00d;
                newEntry5.CreateWhen = TEST_DATE2;
                newEntry5.ModWhen = TEST_DATE2;
                MemoryStream ms5 = new MemoryStream(Patterns.sRunPattern, false);
                SimplePartSource partSource5 = new SimplePartSource(ms5);
                archive.AddPart(newEntry5, FilePart.DataFork, partSource5,
                    CompressionFormat.Default);

                archive.CommitTransaction(output);

                // Test archive in its post-commit state, to verify that we properly updated
                // file offsets and other internal values.
                CheckStandardArchive(archive);
            }
            //CopyToFile(output, "std-archive.zip");
            return output;
        }

        /// <summary>
        /// Verifies that the contents of the standard archive match expectations.
        /// </summary>
        private static void CheckStandardArchive(IArchive stdArc) {
            Helper.CheckNotes(stdArc, 0, 0);

            // Check table of contents.
            Helper.ValidateContents(stdArc, sStdContents);

            // Check some dates.
            IFileEntry entry;
            entry = stdArc.FindFileEntry("HeLlO");
            if (entry.ModWhen != TEST_DATE2) {
                throw new Exception("Bad date on HELLO");
            }
            entry = stdArc.FindFileEntry("ULYSSES");
            if (entry.ModWhen != TEST_DATE2) {
                throw new Exception("Bad dates on ULYSSES");
            }

            // Extract individual entries and compare to original.
            byte[] readBuf = new byte[32768];
            MemoryStream ms1 = new MemoryStream();
            using StreamWriter writer1 = new StreamWriter(ms1, System.Text.Encoding.ASCII);
            writer1.Write(Patterns.sHelloWorld);
            writer1.Flush();
            Helper.CompareFileToStream(stdArc, "Hello", FilePart.DataFork,
                ms1.GetBuffer(), ms1.Length, readBuf);
            Helper.ExpectString(Patterns.sAbyss, stdArc.FindFileEntry("Hello").Comment,
                "comment did not match");

            MemoryStream ms2 = new MemoryStream(0);
            Helper.CompareFileToStream(stdArc, "EMPTY!", FilePart.DataFork,
                ms2.GetBuffer(), ms2.Length, readBuf);

            MemoryStream ms3 = new MemoryStream();
            using StreamWriter writer3 = new StreamWriter(ms3, System.Text.Encoding.ASCII);
            writer3.Write(Patterns.sUlysses);
            writer3.Flush();
            Helper.CompareFileToStream(stdArc, "ULYSSES", FilePart.DataFork,
                ms3.GetBuffer(), ms3.Length, readBuf);

            byte[] rndBuf = new byte[12345];
            Patterns.GenerateUncompressible(rndBuf, 0, rndBuf.Length);
            Helper.CompareFileToStream(stdArc, "foo/bar/random", FilePart.DataFork,
                rndBuf, rndBuf.Length, readBuf);

            IFileEntry check;
            check = stdArc.FindFileEntry("foo/bar/crushably-delicious-datastuffs", '/');
            check = stdArc.FindFileEntry("foo:bar:crushably-delicious-datastuffs", ':');

            Helper.CompareFileToStream(stdArc, "foo/bar/crushably-delicious-datastuffs",
                FilePart.DataFork, Patterns.sRunPattern, Patterns.sRunPattern.Length, readBuf);
        }

        public static void CopyToFile(Stream source, string fileName) {
            string pathName = Helper.DebugFileDir + fileName;
            using (FileStream output =
                    new FileStream(pathName, FileMode.Create, FileAccess.Write)) {
                source.Position = 0;
                source.CopyTo(output);
            }
        }

        #endregion Utilities
    }
}
