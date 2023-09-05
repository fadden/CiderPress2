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
using System.Collections;
using System.Diagnostics;
using System.Text;

using CommonUtil;
using static DiskArc.Defs;

namespace DiskArc.Arc {
    /// <summary>
    /// AppleLink Personal Edition Package Format, usually named ".acu" after the AppleLink
    /// Compression Utility software.
    /// </summary>
    public class AppleLink : IArchiveExt {
        public const int HEADER_LEN = 20;
        internal const int MAX_RECORDS_EXPECTED = 512;      // arbitrary
        private static readonly byte[] SIGNATURE = new byte[] {
            (byte)'f', (byte)'Z', (byte)'i', (byte)'n', (byte)'k'
        };

        private const string FILENAME_RULES = "Filesystem-specific.";
        private static readonly ArcCharacteristics sCharacteristics = new ArcCharacteristics(
            name: "AppleLink",
            canWrite: false,
            hasSingleEntry: false,
            hasResourceForks: true,
            hasDiskImages: false,
            hasArchiveComment: false,
            hasRecordComments: false,
            defaultDirSep: '/',
            fnSyntax: FILENAME_RULES,
            tsStart: TimeStamp.PRODOS_MIN_TIMESTAMP,
            tsEnd: TimeStamp.PRODOS_MAX_TIMESTAMP
        );

        //
        // IArchive interfaces.
        //

        public ArcCharacteristics Characteristics => sCharacteristics;
        public static ArcCharacteristics SCharacteristics => sCharacteristics;

        public Stream? DataStream { get; internal set; }

        public bool IsReadOnly => true;
        public bool IsDubious { get; internal set; }

        public Notes Notes { get; } = new Notes();

        public bool IsReconstructionNeeded => true;

        public string Comment { get => string.Empty; set { } }

        public IEnumerator<IFileEntry> GetEnumerator() {
            return RecordList.GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() {
            return RecordList.GetEnumerator();
        }
        public int Count => RecordList.Count;

        //
        // Internal interfaces.
        //
        /// <summary>
        /// Application hook reference.
        /// </summary>
        internal AppHook AppHook { get; private set; }

        /// <summary>
        /// List of records.
        /// </summary>
        internal List<AppleLink_FileEntry> RecordList { get; private set; } =
            new List<AppleLink_FileEntry>();

        /// <summary>
        /// Open stream tracker.
        /// </summary>
        private OpenStreamTracker mStreamTracker = new OpenStreamTracker();

        /// <summary>
        /// Tests a stream to see if it contains an AppleLink archive.
        /// </summary>
        /// <param name="stream">Stream to test.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <returns>True if this looks like a match.</returns>
        public static bool TestKind(Stream stream, AppHook appHook) {
            stream.Position = 0;
            return ReadFileHeader(stream, out ushort unused);
        }

        /// <summary>
        /// Private constructor.
        /// </summary>
        /// <param name="stream">Data stream if existing file, null if new archive.</param>
        /// <param name="appHook">Application hook reference.</param>
        private AppleLink(Stream? stream, AppHook appHook) {
            DataStream = stream;
            AppHook = appHook;
        }

        /// <summary>
        /// Opens an existing archive.
        /// </summary>
        /// <param name="stream">Archive data stream.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <returns>Archive instance.</returns>
        /// <exception cref="NotSupportedException">Data stream is not compatible.</exception>
        public static AppleLink OpenArchive(Stream stream, AppHook appHook) {
            stream.Position = 0;
            if (!ReadFileHeader(stream, out ushort numRecords)) {
                throw new NotSupportedException("Not an AppleLink archive");
            }
            AppleLink archive = new AppleLink(stream, appHook);
            archive.ScanRecords(numRecords);
            return archive;
        }

        /// <summary>
        /// Reads the ACU file header from the current stream position.  The file will be left
        /// positioned immediately past the file header.
        /// </summary>
        /// <param name="numRecords">Result: number of records reported to be stored in the
        ///   archive.</param>
        /// <returns>True if this is an ACU file, false if not.</returns>
        private static bool ReadFileHeader(Stream stream, out ushort numRecords) {
            const int RESERVED_LEN = 7;

            numRecords = 0;
            if (stream.Length < HEADER_LEN) {
                return false;
            }
            byte[] buf = new byte[HEADER_LEN];
            stream.ReadExactly(buf, 0, HEADER_LEN);

            int offset = 0;
            numRecords = RawData.ReadU16LE(buf, ref offset);
            ushort fsID = RawData.ReadU16LE(buf, ref offset);
            byte[] signature = new byte[SIGNATURE.Length];
            Array.Copy(buf, offset, signature, 0, SIGNATURE.Length);
            offset += SIGNATURE.Length;
            byte acuVersion = buf[offset++];
            ushort fileHdrLen = RawData.ReadU16LE(buf, ref offset);
            byte[] reserved = new byte[RESERVED_LEN];
            Array.Copy(buf, offset, reserved, 0, RESERVED_LEN);
            offset += RESERVED_LEN;
            byte magic = buf[offset++];
            Debug.Assert(offset == HEADER_LEN);

            // Does this look like ACU?
            if (RawData.MemCmp(signature, SIGNATURE, SIGNATURE.Length) != 0) {
                return false;       // wrong signature, definitely not ACU
            }
            if (acuVersion != 1 || fileHdrLen != AppleLink_FileEntry.Header.LENGTH) {
                return false;       // weird length, could be updated version?
            }
            if (numRecords > MAX_RECORDS_EXPECTED) {
                return false;       // unreasonable value, probably garbage
            }
            // could check magic==0xdd, but I don't know what that field actually means

            return true;
        }

        /// <summary>
        /// Scans the records out of the file, from the current stream position.
        /// </summary>
        /// <remarks>
        /// This should not throw an exception on bad data.  Damage should be flagged instead.
        /// </remarks>
        /// <param name="expectedRecords">The expected number of records (from file header).</param>
        private void ScanRecords(ushort expectedRecords) {
            Debug.Assert(DataStream != null);
            Debug.Assert(RecordList.Count == 0);

            while (DataStream.Position < DataStream.Length) {
                long startPosn = DataStream.Position;
                AppleLink_FileEntry? newEntry = AppleLink_FileEntry.ReadNextRecord(this);
                if (newEntry == null) {
                    Notes.AddW("Found extra data at end of file (" +
                        (DataStream.Length - startPosn) + " bytes)");
                    break;
                }
                RecordList.Add(newEntry);
            }

            if (RecordList.Count != expectedRecords) {
                Notes.AddW("Number of records found (" + RecordList.Count +
                    ") does not match value from file header (" + expectedRecords + ")");
                IsDubious = true;
            }
        }

        // IArchive
        public Stream? ReopenStream(Stream newStream) {
            throw new NotSupportedException();
        }

        // IDisposable generic Dispose() implementation.
        public void Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        protected virtual void Dispose(bool disposing) {
            if (disposing) {
                // We're being disposed explicitly (not by the GC).  Dispose of all open entries,
                // so that attempts to continue to use them will start failing immediately.
                if (mStreamTracker.Count != 0) {
                    AppHook.LogW("AppleLink disposed while " + mStreamTracker.Count +
                        " streams are open");
                    mStreamTracker.CloseAll();
                }
            }
            //if (mIsTransactionOpen) {
            //    AppHook.LogW("Disposing of AppleLink while transaction open");
            //    CancelTransaction();
            //}
        }

        // IArchive
        public ArcReadStream OpenPart(IFileEntry ientry, FilePart part) {
            if (ientry.IsDamaged) {
                throw new IOException("Entry is too damaged to open");
            }
            AppleLink_FileEntry entry = (AppleLink_FileEntry)ientry;
            if (entry.Archive != this) {
                throw new ArgumentException("Entry is not part of this archive");
            }
            ArcReadStream newStream = entry.CreateReadStream(part);
            mStreamTracker.Add(ientry, newStream);
            return newStream;
        }

        // IArchiveExt
        public void StreamClosing(ArcReadStream stream) {
            if (!mStreamTracker.RemoveDescriptor(stream)) {
                Debug.Assert(false, "Got StreamClosing for unknown stream");
                // continue on
            }
        }

        // IArchive
        public void StartTransaction() {
            throw new InvalidOperationException("This archive format is read-only");
        }

        // IArchive
        public void CommitTransaction(Stream? outputStream) {
            throw new InvalidOperationException("This archive format is read-only");
        }

        // IArchive
        public void CancelTransaction() { }

        // IArchive
        public IFileEntry CreateRecord() {
            throw new InvalidOperationException("This archive format is read-only");
        }

        // IArchive
        public void DeleteRecord(IFileEntry entry) {
            throw new InvalidOperationException("This archive format is read-only");
        }

        // IArchive
        public void AddPart(IFileEntry entry, FilePart part, IPartSource partSource,
                CompressionFormat compressFmt) {
            throw new InvalidOperationException("This archive format is read-only");
        }

        // IArchive
        public void DeletePart(IFileEntry entry, FilePart part) {
            throw new InvalidOperationException("This archive format is read-only");
        }
    }
}
