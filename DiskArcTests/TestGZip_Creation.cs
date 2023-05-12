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

using AppCommon;
using CommonUtil;
using DiskArc;
using DiskArc.Arc;
using static DiskArc.Defs;

namespace DiskArcTests {
    /// <summary>
    /// Exercise gzip archives.
    /// </summary>
    public class TestGZip_Creation : ITest {
        private static readonly DateTime TEST_DATE1 = new DateTime(1977, 6, 1);
        private static readonly DateTime TEST_DATE2 = new DateTime(1986, 9, 15);

        public static void TestSimple(AppHook appHook) {
            const string FILENAME = "SimpleTest";
            byte[] tmpBuf = new byte[16384];

            MemoryStream arcStream;
            using (IArchive archive = GZip.CreateArchive(appHook)) {
                try {
                    IFileEntry blankEntry = archive.FindFileEntry(string.Empty);
                    Stream stream = archive.OpenPart(blankEntry, FilePart.DataFork);
                    throw new Exception("Opened non-existent data fork");
                } catch (FileNotFoundException) { /*expected*/ }

                archive.StartTransaction();
                // We should not be allowed to commit an empty record.
                try {
                    archive.CommitTransaction(arcStream = new MemoryStream());
                    throw new Exception("Committed empty record");
                } catch (InvalidOperationException) { /*expected*/ }

                IFileEntry entry = archive.FindFileEntry(string.Empty);
                if (entry.ModWhen != TimeStamp.NO_DATE) {
                    throw new Exception("Bad first date");
                }
                try {
                    entry.FileName = "abc\0def";
                    throw new Exception("Null byte allowed (cooked)");
                } catch (ArgumentException) { /*expected*/ }
                try {
                    entry.RawFileName = new byte[] { (byte)'a', 0x00 };
                    throw new Exception("Null byte allowed (raw)");
                } catch (ArgumentException) { /*expected*/ }

                entry.FileName = FILENAME;
                entry.ModWhen = TEST_DATE1;
                SimplePartSource partSource =
                    new SimplePartSource(new MemoryStream(Patterns.sUlyssesBytes));
                archive.AddPart(entry, FilePart.DataFork, partSource, CompressionFormat.Default);
                archive.CommitTransaction(arcStream = new MemoryStream());

                Helper.ExpectString(FILENAME, entry.FileName, "wrong filename");
            }

            // Do a header-only change to exercise copy behavior.
            using (IArchive archive = GZip.OpenArchive(arcStream, appHook)) {
                IFileEntry entry = archive.FindFileEntry(FILENAME);
                if (entry.ModWhen != TEST_DATE1) {
                    throw new Exception("Bad second date");
                }
                Helper.CompareFileToStream(archive, entry.FileName, FilePart.DataFork,
                    Patterns.sUlyssesBytes, Patterns.sUlyssesBytes.Length, tmpBuf);

                archive.StartTransaction();
                entry.ModWhen = TEST_DATE2;
                archive.CommitTransaction(arcStream = new MemoryStream());

                Helper.CompareFileToStream(archive, entry.FileName, FilePart.DataFork,
                    Patterns.sUlyssesBytes, Patterns.sUlyssesBytes.Length, tmpBuf);
            }

            using (IArchive archive = GZip.OpenArchive(arcStream, appHook)) {
                IFileEntry entry = archive.FindFileEntry(FILENAME);
                if (entry.ModWhen != TEST_DATE2) {
                    throw new Exception("Bad third date");
                }
                Helper.CompareFileToStream(archive, entry.FileName, FilePart.DataFork,
                    Patterns.sUlyssesBytes, Patterns.sUlyssesBytes.Length, tmpBuf);
            }
        }

        public static void TestEdit(AppHook appHook) {
            const string FILENAME = "!test-NAME";
            byte[] tmpBuf = new byte[16384];

            MemoryStream arcStream;
            using (IArchive archive = GZip.CreateArchive(appHook)) {
                // Test cancel.
                archive.StartTransaction();
                IFileEntry entry = archive.FindFileEntry(string.Empty);
                entry.ModWhen = TEST_DATE1;
                entry.FileName = "nope";
                SimplePartSource partSource =
                    new SimplePartSource(new MemoryStream(Patterns.sUlyssesBytes));
                try {
                    archive.AddPart(entry, FilePart.DataFork, partSource,
                        CompressionFormat.Uncompressed);
                    throw new Exception("Added uncompressed part");
                } catch (ArgumentException) { /*expected*/ }
                archive.AddPart(entry, FilePart.DataFork, partSource, CompressionFormat.Default);
                archive.CancelTransaction();

                archive.StartTransaction();
                entry.ModWhen = TEST_DATE2;
                entry.FileName = FILENAME;
                partSource = new SimplePartSource(new MemoryStream(Patterns.sHelloWorldBytes));
                archive.AddPart(entry, FilePart.DataFork, partSource, CompressionFormat.Default);
                archive.CommitTransaction(arcStream = new MemoryStream());
            }

            using (IArchive archive = GZip.OpenArchive(arcStream, appHook)) {
                IFileEntry entry = archive.FindFileEntry("!TEST-name");     // flipped case
                if (entry.ModWhen != TEST_DATE2) {
                    throw new Exception("wrong date");
                }
                Helper.CompareFileToStream(archive, entry.FileName, FilePart.DataFork,
                    Patterns.sHelloWorldBytes, Patterns.sHelloWorldBytes.Length, tmpBuf);

                // Delete the part and add a new one.
                archive.StartTransaction();
                archive.DeletePart(entry, FilePart.DataFork);
                long length, storageSize;
                CompressionFormat cfmt;
                if (!entry.GetPartInfo(FilePart.DataFork, out length, out storageSize, out cfmt)) {
                    // This should still work, since we haven't done the commit yet.
                    throw new Exception("Data fork gone before commit");
                }
                SimplePartSource partSource =
                    new SimplePartSource(new MemoryStream(Patterns.sAbyssBytes));
                archive.AddPart(entry, FilePart.DataFork, partSource, CompressionFormat.Deflate);

                try {
                    archive.AddPart(entry, FilePart.DataFork, partSource,
                        CompressionFormat.Default);
                    throw new Exception("Added same part twice");
                } catch (ArgumentException) { /*expected*/ }
                try {
                    archive.AddPart(entry, FilePart.RsrcFork, partSource,
                        CompressionFormat.Default);
                    throw new Exception("Added resource fork");
                } catch (NotSupportedException) { /*expected*/ }
                archive.CommitTransaction(arcStream = new MemoryStream());

                if (!entry.GetPartInfo(FilePart.DataFork, out length, out storageSize, out cfmt)) {
                    throw new Exception("Unable to get data fork info");
                }
                Helper.ExpectLong(Patterns.sAbyssBytes.Length, length, "wrong length");
                if (entry.ModWhen != TEST_DATE2) {
                    throw new Exception("wrong date");
                }

                archive.StartTransaction();
                entry.RawFileName = new byte[0];
                archive.CommitTransaction(arcStream = new MemoryStream());
                entry = archive.FindFileEntry(string.Empty);
            }
        }

        // Confirm that damage is detected.
        public static void TestDamage(AppHook appHook) {
            MemoryStream arcStream;
            using (IArchive archive = GZip.CreateArchive(appHook)) {
                archive.StartTransaction();
                IFileEntry entry = archive.FindFileEntry(string.Empty);
                entry.FileName = "Damaged";
                IPartSource partSource =
                    new SimplePartSource(new MemoryStream(Patterns.sHelloWorldBytes));
                archive.AddPart(entry, FilePart.DataFork, partSource, CompressionFormat.Default);
                archive.CommitTransaction(arcStream = new MemoryStream());
            }

            // Increment the last byte of the CRC.
            byte[] buf = arcStream.GetBuffer();
            buf[arcStream.Length - 5] = (byte)(buf[arcStream.Length - 5] + 1);

            using (IArchive archive = GZip.OpenArchive(arcStream, appHook)) {
                IFileEntry entry = archive.FindFileEntry("Damaged");
                using Stream stream = archive.OpenPart(entry, FilePart.DataFork);
                Stream outputStream = new MemoryStream();
                try {
                    stream.CopyTo(outputStream);
                    throw new Exception("Failed to detect bad CRC");
                } catch (InvalidDataException) { /*expected*/
                    // GZipStream reports this as "... using an unsupported compression method"
                    //Debug.WriteLine("GOT: " + ex);
                }
            }
        }
    }
}
