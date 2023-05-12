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

using AppCommon;
using CommonUtil;
using DiskArc;
using DiskArc.Arc;
using static DiskArc.Defs;

namespace DiskArcTests {
    /// <summary>
    /// Exercise NuFX archives.
    /// </summary>
    public class TestNuFX_Creation : ITest {
        private const int GRINDER_ITERATIONS = 20;

        // Validate the contents of the "standard archive".
        public static void TestStdArchive(AppHook appHook) {
            MemoryStream stdStream = GetStandardArchive(appHook);
            using (NuFX stdArc = NuFX.OpenArchive(stdStream, appHook)) {
                CheckStandardArchive(stdArc);

                //Debug.WriteLine("Std archive:");
                //Debug.WriteLine(Helper.GetArchiveSummary(stdArc));
            }
        }

        // Confirm that empty archives work.
        public static void TestEmpty(AppHook appHook) {
            MemoryStream arcStream = new MemoryStream();
            using (IArchive archive = NuFX.CreateArchive(appHook)) {
                archive.StartTransaction();
                archive.CommitTransaction(arcStream);
            }
            using (IArchive archive = NuFX.OpenArchive(arcStream, appHook)) {
                Helper.CheckNotes(archive, 0, 0);
                try {
                    archive.CommitTransaction(new MemoryStream());
                    throw new Exception("Able to commit without starting");
                } catch (InvalidOperationException) { /*expected*/ }
            }
        }

        // Perform multiple edits.
        public static void TestEdit(AppHook appHook) {
            const string FILE_COMMENT = "wa\r\nhoo";
            const string FILE_COMMENT_AFTER = "wa\rhoo";        // CRLF -> CR conversion
            MemoryStream origStream = GetStandardArchive(appHook);

            using (IArchive archive = NuFX.OpenArchive(origStream, appHook)) {
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
                archive.FindFileEntry("foo:bar:random").Comment = FILE_COMMENT;

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
                newEntry1.Access = FileAttribs.FILE_ACCESS_LOCKED;
                archive.CommitTransaction(newStream);
                origStream.Dispose();
                origStream = newStream;
                if (archive.Notes.ErrorCount != 0 || archive.Notes.WarningCount != 0) {
                    throw new Exception("Got unexpected complaint about duplicate name");
                }

                // Resolve the duplication by deleting the first instance.
                //newEntry1 = archive.FindFileEntry("HELLO");
                Helper.ExpectString("HELLO", newEntry1.FileName, "wrong filename");
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
                archive.CommitTransaction(newStream = new MemoryStream());
                origStream.Dispose();
                origStream = newStream;

                // Remove the new entry.
                //newEntry2 = archive.FindFileEntry("FOO/BAR/ACK");
                Helper.ExpectString("FOO/BAR/ACK", newEntry2.FileName, "wrong filename");
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
                int beforeCount = archive.ToList().Count;
                IFileEntry newEntry3 = archive.CreateRecord();
                newEntry3.FileName = "VERY/LONG/BUT/VALID/ENTRY/NAME";
                newEntry3.DirectorySeparatorChar = '/';
                archive.DeleteRecord(newEntry3);
                archive.CommitTransaction(newStream = new MemoryStream());
                if (archive.ToList().Count != beforeCount) {
                    throw new Exception("Archive grew even with deleted entry");
                }

                // Delete an early record, then verify that the archive didn't break.
                archive.StartTransaction();
                archive.DeleteRecord(archive.FindFileEntry("Ulysses"));
                archive.CommitTransaction(newStream = new MemoryStream());

                Helper.CompareFileToStream(archive, "foo:bar:random", FilePart.RsrcFork,
                    Patterns.sBytePattern, Patterns.sBytePattern.Length, new byte[16384]);
                if (archive.FindFileEntry("foo:bar:random").Comment != FILE_COMMENT_AFTER) {
                    throw new Exception("Lost the file comment?");
                }
            }
        }

        public static void TestCreateBXY(AppHook appHook) {
            const string NUFX_NAME = "ARCHIVE.SHK";

            // Create an empty NuFX archive stream.
            MemoryStream nufxStream = new MemoryStream();
            using (IArchive archive = NuFX.CreateArchive(appHook)) {
                archive.StartTransaction();
                archive.CommitTransaction(nufxStream);
            }

            // Create a Binary II archive stream and add the empty NuFX archive.
            MemoryStream bnyStream = new MemoryStream();
            using (IArchive archive = Binary2.CreateArchive(appHook)) {
                archive.StartTransaction();
                IFileEntry entry = archive.CreateRecord();
                entry.FileName = NUFX_NAME;
                entry.FileType = FileAttribs.FILE_TYPE_LBR;
                entry.AuxType = 0x8002;
                SimplePartSource partSource = new SimplePartSource(nufxStream);
                archive.AddPart(entry, FilePart.DataFork, partSource,
                    CompressionFormat.Uncompressed);
                archive.CommitTransaction(bnyStream);
            }

            // Open the combined stream as NuFX BXY, and add a couple of files.
            MemoryStream outputStream = new MemoryStream();
            using (IArchive archive = NuFX.OpenArchive(bnyStream, appHook)) {
                archive.StartTransaction();
                IFileEntry newEntry1 = archive.CreateRecord();
                newEntry1.FileName = "Ulysses";
                SimplePartSource partSource1 =
                    new SimplePartSource(new MemoryStream(Patterns.sUlyssesBytes));
                archive.AddPart(newEntry1, FilePart.DataFork, partSource1,
                    CompressionFormat.Default);
                MemoryStream tmpStream1 = new MemoryStream();
                archive.CommitTransaction(tmpStream1);

                // Add another file to make sure we're still maintaining the BXY header.
                archive.StartTransaction();
                IFileEntry newEntry2 = archive.CreateRecord();
                newEntry2.FileName = "Gettysburg";
                SimplePartSource partSource2 =
                    new SimplePartSource(new MemoryStream(Patterns.sGettysburgBytes));
                archive.AddPart(newEntry2, FilePart.DataFork, partSource2,
                    CompressionFormat.Default);
                archive.CommitTransaction(outputStream);
            }

            // Verify the BXY header is still with us.
            using (IArchive archive = Binary2.OpenArchive(outputStream, appHook)) {
                List<IFileEntry> entries = archive.ToList();
                Helper.ExpectInt(1, entries.Count, "BNY entries");
                if (entries[0].FileName != NUFX_NAME) {
                    throw new Exception("Wrong name found");
                }
            }
            // Verify that we didn't forget how to be NuFX.
            using (IArchive archive = NuFX.OpenArchive(outputStream, appHook)) {
                List<IFileEntry> entries = archive.ToList();
                Helper.ExpectInt(2, entries.Count, "NuFX entries");
                if (entries[0].FileName != "Ulysses" || entries[1].FileName != "Gettysburg") {
                    throw new Exception("Wrong names found");
                }
            }

            //Helper.CopyToFile(outputStream, "bxy-test.bxy");
        }

        public static void TestDiskImage(AppHook appHook) {
            string FILENAME = "NEW.DISK";

            MemoryStream firstStream;
            using (IArchive archive = NuFX.CreateArchive(appHook)) {
                archive.StartTransaction();
                IFileEntry entry = archive.CreateRecord();
                entry.FileName = FILENAME;
                entry.FileType = 0x12;
                entry.AuxType = 0x3456;

                // Create a blank unadorned 140KB disk image.
                MemoryStream fakeDisk = new MemoryStream(new byte[140 * 1024]);
                fakeDisk.Position = 55;     // make sure we're seeking to zero on PartSource.Open
                archive.AddPart(entry, FilePart.DiskImage, new SimplePartSource(fakeDisk),
                    CompressionFormat.Default);
                archive.CommitTransaction(firstStream = new MemoryStream());

                archive.StartTransaction();
                // "entry" was a newly-created record, so it's still valid post-commit.  Test the
                // behavior of the extra_type field.
                Helper.ExpectInt(0, entry.FileType, "disk image file type should be zero");
                uint blockCount = ((NuFX_FileEntry)entry).ExtraType;
                Helper.ExpectInt(280, (int)blockCount, "disk image extra_type not set");
                entry.AuxType = 0x1234;
                blockCount = ((NuFX_FileEntry)entry).ExtraType;
                Helper.ExpectInt(280, (int)blockCount, "disk image extra_type change allowed");

                // Bounce around a bit, then check the block count again.
                archive.CancelTransaction();
                archive.StartTransaction();
                archive.CommitTransaction(new MemoryStream());
                //entry = archive.FindFileEntry(FILENAME);
                Helper.ExpectString(FILENAME, entry.FileName, "wrong filename");
                blockCount = ((NuFX_FileEntry)entry).ExtraType;
                Helper.ExpectInt(280, (int)blockCount, "disk image extra_type didn't stick");

                // Try to add another disk image to the same record.
                archive.StartTransaction();
                SimplePartSource excSource = new SimplePartSource(fakeDisk);
                try {
                    archive.AddPart(entry, FilePart.DiskImage, excSource,
                        CompressionFormat.Default);
                    throw new Exception("Allowed to add second disk image thread");
                } catch (ArgumentException) {
                    /*expected*/
                    excSource.Dispose();
                }

                // Try to add a disk image whose length isn't a multiple of 512.
                IFileEntry badDisk = archive.CreateRecord();
                badDisk.FileName = "NEW.DISK2";
                MemoryStream badFakeDisk = new MemoryStream(new byte[12345]);
                archive.AddPart(badDisk, FilePart.DiskImage, new SimplePartSource(badFakeDisk),
                    CompressionFormat.Default);
                try {
                    archive.CommitTransaction(new MemoryStream());
                    throw new Exception("Allowed to add non-512-byte disk image");
                } catch (InvalidDataException) { /*expected*/ }

                // fall out of "using" block with transaction open
            }

            // Make sure the initial version checks out.
            using (IArchive archive = NuFX.OpenArchive(firstStream, appHook)) {
                IFileEntry entry = archive.FindFileEntry(FILENAME);
                Helper.ExpectInt(0, entry.FileType, "disk image file type not zeroed");
                uint blockCount = ((NuFX_FileEntry)entry).ExtraType;
                Helper.ExpectInt(280, (int)blockCount, "disk image extra_type not set");
            }
        }

        public static void TestParts(AppHook appHook) {
            MemoryStream origStream = GetStandardArchive(appHook);

            using (IArchive archive = NuFX.OpenArchive(origStream, appHook)) {
                // Replace a file's contents in the middle of the archive.
                IFileEntry ulysses = archive.FindFileEntry("ULYSSES");  // case-insensitive name
                archive.StartTransaction();
                try {
                    archive.DeletePart(ulysses, FilePart.RsrcFork);
                    throw new Exception("Deleted non-existent part");
                } catch (FileNotFoundException) { /*expected*/ }

                archive.DeletePart(ulysses, FilePart.DataFork);
                long length, storageSize;
                CompressionFormat cfmt;
                if (!ulysses.GetPartInfo(FilePart.DataFork, out length, out storageSize,out cfmt)) {
                    // This should still work, since we haven't done the commit yet.
                    throw new Exception("Data fork gone before commit");
                }
                Helper.ExpectLong(ulysses.DataLength, length, "length mismatch");
                if (ulysses.GetPartInfo(FilePart.RsrcFork, out length, out storageSize, out cfmt)) {
                    throw new Exception("Got part info from absent rsrc fork");
                }
                try {
                    archive.DeletePart(ulysses, FilePart.DataFork);
                    throw new Exception("Deleted same part twice");
                } catch (FileNotFoundException) { /*expected*/ }

                // Replace Ulysses with Gettysburg.
                SimplePartSource excSource =
                    new SimplePartSource(new MemoryStream(Patterns.sGettysburgBytes));
                archive.AddPart(ulysses, FilePart.DataFork,
                    new SimplePartSource(new MemoryStream(Patterns.sGettysburgBytes)),
                    CompressionFormat.Default);
                try {
                    archive.AddPart(ulysses, FilePart.DataFork, excSource,
                        CompressionFormat.Default);
                    throw new Exception("Allowed to add same part twice");
                } catch (ArgumentException) {
                    /*expected*/
                    excSource.Dispose();
                }
                // Update the name to match.
                ulysses.FileName = "Gettysburg";
                MemoryStream newArcStream = new MemoryStream();
                archive.CommitTransaction(newArcStream);

                origStream = newArcStream;
            }

            // Open updated stream in entirely new archive instance to verify.
            using (IArchive archive = NuFX.OpenArchive(origStream, appHook)) {
                byte[] tmpBuf = new byte[32768];

                // See if it matches.
                Helper.CompareFileToStream(archive, "GETTYSBURG", FilePart.DataFork,
                    Patterns.sGettysburgBytes, Patterns.sGettysburgBytes.Length, tmpBuf);

                // Check a file at the end too.
                Helper.CompareFileToStream(archive, "foo:bar:random", FilePart.RsrcFork,
                    Patterns.sBytePattern, Patterns.sBytePattern.Length, new byte[16384]);

                // Add a resource fork.
                archive.StartTransaction();
                IFileEntry entry = archive.FindFileEntry("Gettysburg");
                archive.AddPart(entry, FilePart.RsrcFork,
                    new SimplePartSource(new MemoryStream(Patterns.sAbyssBytes)),
                    CompressionFormat.Default);

                archive.CommitTransaction(new MemoryStream());

                if (archive.Notes.WarningCount != 0 || archive.Notes.ErrorCount != 0) {
                    throw new Exception("Unexpected warnings/errors");
                }
            }

            origStream.Close();
        }

        // Test a resource-fork-only record.  The actual "Miranda" threads only get added for
        // badly-formed archives.
        public static void TestMiranda(AppHook appHook) {
            const string FILE_NAME = "TEST";

            MemoryStream newStream = new MemoryStream();
            using (IArchive archive = NuFX.CreateArchive(appHook)) {
                archive.StartTransaction();
                IFileEntry newEntry = archive.CreateRecord();
                newEntry.FileName = FILE_NAME;
                archive.AddPart(newEntry, FilePart.RsrcFork,
                    new SimplePartSource(new MemoryStream(Patterns.sHelloWorldBytes)),
                    CompressionFormat.Default);
                archive.CommitTransaction(newStream);
            }
            using (IArchive archive = NuFX.OpenArchive(newStream, appHook)) {
                IFileEntry entry = archive.FindFileEntry(FILE_NAME);
                if (entry.DataLength != 0 || entry.RsrcLength != Patterns.sHelloWorldBytes.Length) {
                    throw new Exception("Incorrect length");
                }
                Helper.ExpectBool(false, entry.HasDataFork, "data fork was added");
            }
        }

        public static void TestHiBitName(AppHook appHook) {
            // Create an archive with an entry whose filename has all high bits set.
            MemoryStream newStream = new MemoryStream();
            using (IArchive archive = NuFX.CreateArchive(appHook)) {
                archive.StartTransaction();
                IFileEntry entry = archive.CreateRecord();
                ((NuFX_FileEntry)entry).FileSysID = FileSystemType.ProDOS;
                entry.RawFileName = new byte[] { 0xc8, 0xe5, 0xec, 0xec, 0xef };
                archive.AddPart(entry, FilePart.DataFork,
                    new SimplePartSource(new MemoryStream(new byte[1])),
                    CompressionFormat.Uncompressed);

                archive.CommitTransaction(newStream);

                try {
                    IFileEntry maybe = archive.FindFileEntry("Hello");
                    // It's okay if it does the fix early.
                } catch (FileNotFoundException) { /*expected*/ }
            }

            // Reload the archive to trigger the "fix" code.
            using (IArchive archive = NuFX.OpenArchive(newStream, appHook)) {
                IFileEntry entry = archive.FindFileEntry("Hello");
            }
        }

        // Test Mac OS Roman conversions for filenames and comments.
        public static void TestCharXlate(AppHook appHook) {
            const string FILENAME = "Comment\u240a\u240dTest";      // embedded printable CRLF
            const string COMMENT_IN = "This\r is\n a\r\n test\r\r\n\n.";
            const string COMMENT_OUT = "This\r is\r a\r test\r\r\r.";

            MemoryStream newStream = new MemoryStream();
            using (IArchive archive = NuFX.CreateArchive(appHook)) {
                archive.StartTransaction();
                IFileEntry entry = archive.CreateRecord();
                try {
                    entry.FileName = "This\u2422is invalid";
                    throw new Exception("Invalid filename characters allowed");
                } catch (ArgumentException) { /*expected*/ }
                entry.FileName = FILENAME;
                entry.Comment = COMMENT_IN;
                ((NuFX_FileEntry)entry).CommentFieldLength = 200;
                archive.AddPart(entry, FilePart.DataFork,
                    new SimplePartSource(new MemoryStream(0)),
                    CompressionFormat.Uncompressed);
                archive.CommitTransaction(newStream);

                // Confirm the changes are immediately visible.
                Helper.ExpectString(COMMENT_OUT, entry.Comment,
                    "comment not transformed correctly #1");
                Helper.ExpectInt(200, (int)((NuFX_FileEntry)entry).CommentFieldLength,
                    "Comment field length #1");
            }

            // Confirm they were written to the archive.
            MemoryStream newStream2 = new MemoryStream();
            using (IArchive archive = NuFX.OpenArchive(newStream, appHook)) {
                IFileEntry entry = archive.FindFileEntry(FILENAME);
                Helper.ExpectString(COMMENT_OUT, entry.Comment,
                    "comment not transformed correctly #2");
                Helper.ExpectInt(200, (int)((NuFX_FileEntry)entry).CommentFieldLength,
                    "Comment field length #2");

                // Delete the comment by setting it to be empty, then set the comment field
                // length so it's not actually gone.
                archive.StartTransaction();
                entry.Comment = string.Empty;
                ((NuFX_FileEntry)entry).CommentFieldLength = 300;
                archive.CommitTransaction(newStream2);
            }

            MemoryStream newStream3 = new MemoryStream();
            using (IArchive archive = NuFX.OpenArchive(newStream2, appHook)) {
                IFileEntry entry = archive.FindFileEntry(FILENAME);
                Helper.ExpectString(string.Empty, entry.Comment, "comment not erased #1");
                Helper.ExpectInt(300, (int)((NuFX_FileEntry)entry).CommentFieldLength,
                    "Comment field length #3");

                // Delete it entirely.
                archive.StartTransaction();
                entry.Comment = string.Empty;
                archive.CommitTransaction(newStream3);
            }

            using (IArchive archive = NuFX.OpenArchive(newStream3, appHook)) {
                IFileEntry entry = archive.FindFileEntry(FILENAME);
                Helper.ExpectString(string.Empty, entry.Comment, "comment not erased #2");
                Helper.ExpectInt(0, (int)((NuFX_FileEntry)entry).CommentFieldLength,
                    "Comment field length #4");
                Helper.CheckNotes(archive, 0, 0);
            }
        }

        // Play with HFS metadata.
        public static void TestHFSMetaData(AppHook appHook) {
            const string FILENAME = "TestFile";
            const uint TYPE1 = 0x12345678;
            const uint TYPE2 = 0x87654321;

            MemoryStream stream1 = new MemoryStream();
            using (IArchive archive = NuFX.CreateArchive(appHook)) {
                archive.StartTransaction();
                IFileEntry entry = archive.CreateRecord();
                entry.FileName = FILENAME;
                ((NuFX_FileEntry)entry).FileSysID = FileSystemType.DOS33;
                // "Has types" is immediately false, because this is a newly-created record.
                Helper.ExpectBool(false, entry.HasHFSTypes, "has types");
                try {
                    entry.HFSFileType = TYPE1;
                    throw new Exception("allowed to set HFS type on DOS entry");
                } catch (ArgumentException) { /*expected*/ }

                ((NuFX_FileEntry)entry).FileSysID = FileSystemType.ProDOS;
                entry.HFSFileType = TYPE1;
                entry.HFSCreator = TYPE2;
                archive.AddPart(entry, FilePart.DataFork,
                    new SimplePartSource(new MemoryStream(0)),
                    CompressionFormat.Uncompressed);
                archive.CommitTransaction(stream1);
            }

            MemoryStream stream2 = new MemoryStream();
            using (IArchive archive = NuFX.OpenArchive(stream1, appHook)) {
                IFileEntry entry = archive.FindFileEntry(FILENAME);
                Helper.ExpectUInt(TYPE1, entry.HFSFileType, "wrong HFS type value");
                Helper.ExpectUInt(TYPE2, entry.HFSCreator, "wrong HFS creator value");

                // Setting both to zero should remove the option list from the record.
                archive.StartTransaction();
                ((NuFX_FileEntry)entry).FileSysID = FileSystemType.DOS33;
                // "Has types" is still true, because we query on the pre-edit copy.  It should
                // have zeroed out the option list.
                Helper.ExpectBool(true, entry.HasHFSTypes, "has types");
                // Switch it back to HFS.
                ((NuFX_FileEntry)entry).FileSysID = FileSystemType.HFS;
                archive.CommitTransaction(stream2);
            }

            MemoryStream stream3 = new MemoryStream();
            using (IArchive archive = NuFX.OpenArchive(stream2, appHook)) {
                IFileEntry entry = archive.FindFileEntry(FILENAME);
                Helper.ExpectBool(true, entry.HasHFSTypes, "has types");
                // Confirm that unset values are reported as zero.
                Helper.ExpectUInt(0, entry.HFSFileType, "wrong HFS type value");
                Helper.ExpectUInt(0, entry.HFSCreator, "wrong HFS creator value");

                // Set nonzero to create the option list, then zero it to watch it disappear.
                archive.StartTransaction();
                entry.HFSFileType = TYPE1;
                entry.HFSFileType = 0;
                archive.CommitTransaction(stream3);
            }

            using (IArchive archive = NuFX.OpenArchive(stream3, appHook)) {
                IFileEntry entry = archive.FindFileEntry(FILENAME);
                // No interface to check option list size.  Not that important; just want to
                // exercise the code to make sure it's not doing anything horrible.
                Helper.CheckNotes(archive, 0, 0);
            }
        }

        public static void TestGrind(AppHook appHook) {
            const int SEED = 12345;

            using (IArchive archive = NuFX.CreateArchive(appHook)) {
                Grindarc grinder = new Grindarc(archive, SEED);
                grinder.Execute(GRINDER_ITERATIONS);
            }
        }

        // Archive damage test.
        public static void TestTruncated(AppHook appHook) {
            MemoryStream origStream = GetStandardArchive(appHook);
            // Shave a chunk off the last file.
            origStream.SetLength(origStream.Length - 128);

            using (IArchive archive = NuFX.OpenArchive(origStream, appHook)) {
                List<IFileEntry> entries = archive.ToList();
                if (!entries[entries.Count - 1].IsDamaged && !archive.IsDubious) {
                    throw new Exception("No indication of truncation");
                }
            }
        }

        // Failed I/O test.
        public static void TestReadFailure(AppHook appHook) {
            MemoryStream origStream = GetStandardArchive(appHook);
            MemoryStream newStream = new MemoryStream();
            using (IArchive archive = NuFX.OpenArchive(origStream, appHook)) {
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
            using (IArchive archive = NuFX.OpenArchive(newStream, appHook)) {
                Helper.CheckNotes(archive, 0, 0);
                Helper.CompareFileToStream(archive, "ULYSSES", FilePart.DataFork,
                    Patterns.sUlyssesBytes, Patterns.sUlysses.Length, new byte[16384]);
            }
        }

        #region Utilities

        private static List<Helper.FileAttr> sStdContents = new List<Helper.FileAttr>() {
            new Helper.FileAttr("Hello", 14, -1, 14,
                FileAttribs.FILE_TYPE_TXT, 0x0000),
            new Helper.FileAttr("Empty!",  0, -1, 0,
                FileAttribs.FILE_TYPE_NON, 0x0000),
            new Helper.FileAttr("Ulysses", 2987, -1, 1839,
                FileAttribs.FILE_TYPE_TXT, 0x0000),
            new Helper.FileAttr("foo:bar:random", 12345, 16384, 16167,
                FileAttribs.FILE_TYPE_BIN, 0x1234),
            new Helper.FileAttr("foo/bar/crushably-delicious-datastuffs", 32000, -1, 337,
                FileAttribs.FILE_TYPE_BIN, 0xf00d),
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
            using (IArchive archive = NuFX.CreateArchive(appHook)) {
                archive.StartTransaction();

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

                // #4: un-compressible data fork, with partial pathname and a resource fork
                IFileEntry newEntry4 = archive.CreateRecord();
                newEntry4.FileName = "foo:bar:random";
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
                MemoryStream ms4r = new MemoryStream(Patterns.sBytePattern, false);
                SimplePartSource partSource4r = new SimplePartSource(ms4r);
                archive.AddPart(newEntry4, FilePart.RsrcFork, partSource4r,
                    CompressionFormat.Default);

                // #5: compressible file, with long partial pathname
                IFileEntry newEntry5 = archive.CreateRecord();
                newEntry5.FileName = "foo/bar/crushably-delicious-datastuffs";
                newEntry5.DirectorySeparatorChar = '/';
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
            //Helper.CopyToFile(output, "std-archive.shk");
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
            Helper.CompareFileToStream(stdArc, "foo:bar:random", FilePart.DataFork,
                rndBuf, rndBuf.Length, readBuf);
            Helper.CompareFileToStream(stdArc, "foo:bar:random", FilePart.RsrcFork,
                Patterns.sBytePattern, Patterns.sBytePattern.Length, readBuf);

            IFileEntry check;
            check = stdArc.FindFileEntry("foo/bar/crushably-delicious-datastuffs", '/');
            check = stdArc.FindFileEntry("foo:bar:crushably-delicious-datastuffs", ':');

            Helper.CompareFileToStream(stdArc, "foo/bar/crushably-delicious-datastuffs",
                FilePart.DataFork, Patterns.sRunPattern, Patterns.sRunPattern.Length, readBuf);
        }

        #endregion Utilities
    }
}
