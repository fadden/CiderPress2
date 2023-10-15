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
using System.Diagnostics;
using System.IO;
using System.Linq;

using AppCommon;
using CommonUtil;
using DiskArc;
using DiskArc.Arc;
using static DiskArc.Defs;
using static DiskArc.IArchive;

namespace DiskArcTests {
    /// <summary>
    /// Tests archives in an archive-independent way.  We can't exercise the
    /// interesting instance-specific edge cases, but we can confirm that everything fails
    /// the same way when bad arguments are passed.  These tests are less about exercising
    /// the success cases and more about checking the simple failure cases for consistency.
    /// </summary>
    public class TestArcInterfaces : ITest {
        #region Arc Drivers

        public static void TestAppleSingle(AppHook appHook) {
            using (IArchive archive = AppleSingle.CreateArchive(1, appHook)) {
                TestGeneral(archive, appHook);
            }
            using (IArchive archive = AppleSingle.CreateArchive(2, appHook)) {
                TestSingleFile(archive, appHook);
            }
        }

        public static void TestBinary2(AppHook appHook) {
            using (IArchive archive = Binary2.CreateArchive(appHook)) {
                TestGeneral(archive, appHook);
            }
            using (IArchive archive = Binary2.CreateArchive(appHook)) {
                TestMultiFile(archive, appHook);
            }
        }

        public static void TestGZip(AppHook appHook) {
            using (IArchive archive = GZip.CreateArchive(appHook)) {
                TestGeneral(archive, appHook);
            }
            using (IArchive archive = GZip.CreateArchive(appHook)) {
                TestSingleFile(archive, appHook);
            }
        }

        // No tests for MacBinary, because it's read-only.

        public static void TestNuFX(AppHook appHook) {
            using (IArchive archive = NuFX.CreateArchive(appHook)) {
                TestGeneral(archive, appHook);
            }
            using (IArchive archive = NuFX.CreateArchive(appHook)) {
                TestMultiFile(archive, appHook);
            }
        }

        public static void TestZip(AppHook appHook) {
            using (IArchive archive = Zip.CreateArchive(appHook)) {
                TestGeneral(archive, appHook);
            }
            using (IArchive archive = Zip.CreateArchive(appHook)) {
                TestMultiFile(archive, appHook);
            }
        }

        #endregion Arc Drivers

        #region Tests

        private static void TestGeneral(IArchive archive, AppHook appHook) {
            MemoryStream newStream;
            try {
                archive.CommitTransaction(newStream = new MemoryStream());
                throw new Exception("Commit without start");
            } catch (InvalidOperationException) { /*expected*/ }

            archive.StartTransaction();
            try {
                archive.StartTransaction();
                throw new Exception("Transaction started twice");
            } catch (InvalidOperationException) { /*expected*/ }

            archive.CancelTransaction();

            // cancel with nothing in progress is allowed
            archive.CancelTransaction();

            // Confirm we can't use an archive after it has been disposed.
            archive.Dispose();
            try {
                archive.StartTransaction();
                throw new Exception("Transaction started twice");
            } catch (InvalidOperationException) { /*expected*/ }
        }

        private static void TestSingleFile(IArchive archive, AppHook appHook) {
            Debug.Assert(archive.Characteristics.HasSingleEntry);
            if (archive.Count != 1) {
                throw new Exception("Empty archive is empty");
            }

            // Empty transactions may or may not succeed, because the archive is created with
            // a single entry with no parts.  AppleSingle allows, gzip does not.  Behavior is
            // not mandated.

            try {
                archive.CreateRecord();
                throw new Exception("Created a new record in single-entry archive");
            } catch (NotSupportedException) { /*expected*/ }

            // Should be one entry, with an empty filename.
            IFileEntry soleEntry = archive.FindFileEntry(string.Empty);

            try {
                archive.DeleteRecord(soleEntry);
                throw new Exception("Deleted record in single-entry archive");
            } catch (NotSupportedException) { /*expected*/ }

            MemoryStream tinyStream = new MemoryStream();
            tinyStream.SetLength(8);
            SimplePartSource source = new SimplePartSource(tinyStream);
            archive.StartTransaction();
            archive.AddPart(soleEntry, FilePart.DataFork, source, CompressionFormat.Default);

            MemoryStream newStream;
            archive.CommitTransaction(newStream = new MemoryStream());

            archive.StartTransaction();
            SimplePartSource source1 = new SimplePartSource(new MemoryStream(new byte[1]));
            archive.DeletePart(soleEntry, FilePart.DataFork);   // remove existing part
            archive.AddPart(soleEntry, FilePart.DataFork, source1, CompressionFormat.Default);
            archive.DeletePart(soleEntry, FilePart.DataFork);   // should dispose of source1
            SimplePartSource source2 = new SimplePartSource(new MemoryStream(new byte[2]));
            archive.AddPart(soleEntry, FilePart.DataFork, source2, CompressionFormat.Default);
            archive.CommitTransaction(newStream = new MemoryStream());
            soleEntry = archive.GetFirstEntry();
            soleEntry.GetPartInfo(FilePart.DataFork, out long length, out long unused1,
                out CompressionFormat unused2);
            if (length != 2) {
                throw new Exception("Found unexpected length: " + soleEntry.DataLength);
            }

            // Try to commit a transaction using the source stream as the destination stream.
            archive.StartTransaction();
            try {
                archive.CommitTransaction(newStream);
                throw new Exception("Commit to source stream");
            } catch (InvalidOperationException) { /*expected*/ }

            // exit with transaction open; should close (with complaints) when disposed by driver
        }

        private static void TestMultiFile(IArchive archive, AppHook appHook) {
            // This is a static method accessed via extension.  Confirm that it exists.
            // Currently not needed for single-file archives (AppleSingle/gzip).
            archive.AdjustFileName("TEST");

            using IArchive otherArchive = NuFX.CreateArchive(appHook);
            otherArchive.StartTransaction();
            IFileEntry otherEntry = otherArchive.CreateRecord();
            MemoryStream newStream;

            Debug.Assert(!archive.Characteristics.HasSingleEntry);
            if (archive.Count != 0) {
                throw new Exception("Empty archive isn't empty");
            }

            // Empty transaction on archive with no entries is allowed.
            archive.StartTransaction();
            archive.CommitTransaction(newStream = new MemoryStream());

            archive.StartTransaction();
            IFileEntry newRec = archive.CreateRecord();
            archive.DeleteRecord(newRec);
            try {
                archive.DeleteRecord(newRec);
                throw new Exception("Deleted record twice");
            } catch (ArgumentException) { /*expected*/ }
            try {
                archive.DeleteRecord(IFileEntry.NO_ENTRY);
                throw new Exception("Deleted NO_ENTRY");
            } catch (ArgumentException) { /*expected*/ }
            try {
                archive.DeleteRecord(otherEntry);
                throw new Exception("Deleted record from other archive");
            } catch (FileNotFoundException) { /*expected*/ }
            archive.CommitTransaction(newStream = new MemoryStream());
            if (archive.Count != 0) {
                throw new Exception("Deleted record didn't go away");
            }

            // Create record, add part, delete part, add part again, commit.
            archive.StartTransaction();
            IFileEntry rec1 = archive.CreateRecord();
            rec1.FileName = "REC1";
            SimplePartSource source1a = new SimplePartSource(new MemoryStream(new byte[1]));
            archive.AddPart(rec1, FilePart.DataFork, source1a, CompressionFormat.Default);
            archive.DeletePart(rec1, FilePart.DataFork);    // should dispose of source1
            SimplePartSource source1b = new SimplePartSource(new MemoryStream(new byte[2]));
            archive.AddPart(rec1, FilePart.DataFork, source1b, CompressionFormat.Default);
            archive.CommitTransaction(newStream = new MemoryStream());
            rec1 = archive.FindFileEntry("REC1");
            if (rec1.DataLength != 2) {
                throw new Exception("Found unexpected length: " + rec1.DataLength);
            }

            // Create record, add part, delete entire record.
            archive.StartTransaction();
            IFileEntry rec2 = archive.CreateRecord();
            rec2.FileName = "REC2";
            SimplePartSource source2a = new SimplePartSource(new MemoryStream(new byte[1]));
            archive.AddPart(rec2, FilePart.DataFork, source2a, CompressionFormat.Default);
            archive.DeleteRecord(rec2);
            archive.CommitTransaction(newStream = new MemoryStream());
            try {
                archive.FindFileEntry("REC2");
                throw new Exception("Found deleted record");
            } catch (FileNotFoundException) { /*expected*/ }

            archive.StartTransaction();
            newRec = archive.CreateRecord();
            newRec.FileName = "NAME";
            archive.AddPart(newRec, FilePart.DataFork, new SimplePartSource(new MemoryStream(0)),
                CompressionFormat.Default);
            try {
                // Try to commit back to the same stream.
                archive.CommitTransaction(newStream);
                throw new Exception("Commit to source stream");
            } catch (InvalidOperationException) { /*expected*/ }

            archive.CommitTransaction(newStream = new MemoryStream());
            try {
                archive.DeleteRecord(newRec);
                throw new Exception("Deleted record outside transaction");
            } catch (InvalidOperationException) { /*expected*/ }

            // otherArchive will be Disposed on method exit, canceling the transaction
        }

        #endregion Tests
    }
}
