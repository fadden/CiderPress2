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
using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;

using CommonUtil;
using static DiskArc.Defs;

namespace DiskArc.Arc {
    /// <summary>
    /// StuffIt archive handling.  Only v1.5-era archives are handled.
    /// </summary>
    public class StuffIt : IArchiveExt {
        private const string FILENAME_RULES = "Filename only, Mac OS character set, avoid ':'.";

        private static readonly ArcCharacteristics sCharacteristics = new ArcCharacteristics(
            name: "StuffIt",
            canWrite: false,
            hasSingleEntry: false,
            hasResourceForks: true,
            hasDiskImages: false,
            hasArchiveComment: false,
            hasRecordComments: false,
            defaultDirSep: StuffIt_FileEntry.SEP_CHAR,
            fnSyntax: FILENAME_RULES,
            // If we supported higher-resolution timestamps, this range would be larger.
            tsStart: TimeStamp.HFS_MIN_TIMESTAMP,
            tsEnd: TimeStamp.HFS_MAX_TIMESTAMP
        );

        //
        // IArchive interfaces.
        //

        /// <summary>
        /// Archive header record.
        /// </summary>
        private class ArchiveHeader {
            public const int LENGTH = 22;
            // Archive header signature #1, "SIT!".
            public static readonly byte[] SIGNATURE1 = new byte[] { 0x53, 0x49, 0x54, 0x21 };
            // Archive header signature #2, "rLau".
            public static readonly byte[] SIGNATURE2 = new byte[] { 0x72, 0x4c, 0x61, 0x75 };

            public ushort EntryCount { get; set; }
            public uint FileLength { get; set; }
            public byte Version { get; set; }
            public byte[] Reserved { get; set; } = new byte[7];

            public ArchiveHeader() { }

            /// <summary>
            /// Copy constructor.
            /// </summary>
            public ArchiveHeader(ArchiveHeader src) {
                byte[] buf = new byte[LENGTH];
                src.Store(buf, 0);
                Load(buf, 0);
            }

            /// <summary>
            /// Unpacks the archive header from the buffer.
            /// </summary>
            public bool Load(byte[] buf, int offset) {
                int startOffset = offset;
                for (int i = 0; i < SIGNATURE1.Length; i++) {
                    if (buf[offset++] != SIGNATURE1[i]) {
                        return false;
                    }
                }
                EntryCount = RawData.ReadU16BE(buf, ref offset);
                FileLength = RawData.ReadU32BE(buf, ref offset);
                for (int i = 0; i < SIGNATURE2.Length; i++) {
                    if (buf[offset++] != SIGNATURE2[i]) {
                        return false;
                    }
                }
                Version = RawData.ReadU8(buf, ref offset);
                Array.Copy(buf, offset, Reserved, 0, Reserved.Length);
                offset += Reserved.Length;
                Debug.Assert(offset - startOffset == LENGTH);
                return true;
            }

            /// <summary>
            /// Serializes the archive header into the buffer.
            /// </summary>
            public void Store(byte[] buf, int offset) {
                int startOffset = offset;
                Array.Copy(SIGNATURE1, buf, SIGNATURE1.Length);
                offset += SIGNATURE1.Length;
                RawData.WriteU16BE(buf, ref offset, EntryCount);
                RawData.WriteU32BE(buf, ref offset, FileLength);
                Array.Copy(SIGNATURE2, buf, SIGNATURE2.Length);
                offset += SIGNATURE2.Length;
                RawData.WriteU8(buf, ref offset, Version);
                Array.Copy(Reserved, 0, buf, offset, Reserved.Length);
                offset += Reserved.Length;
                Debug.Assert(offset - startOffset == LENGTH);
            }
        }
        private ArchiveHeader mArchiveHeader;

        public ArcCharacteristics Characteristics => sCharacteristics;
        public static ArcCharacteristics SCharacteristics => sCharacteristics;

        public Stream? DataStream { get; internal set; }

        public bool IsValid { get; private set; } = true;
        public bool IsReadOnly => IsDubious || (DataStream != null && !DataStream.CanWrite);
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

        internal List<StuffIt_FileEntry> RecordList { get; private set; } =
            new List<StuffIt_FileEntry>();

        /// <summary>
        /// True if a transaction is open.
        /// </summary>
        private bool mIsTransactionOpen;

        /// <summary>
        /// Modified entry list.  This will become the new list of entries when the transaction is
        /// committed.
        /// </summary>
        private List<StuffIt_FileEntry> mEditList = new List<StuffIt_FileEntry>();

        /// <summary>
        /// Open stream tracker.
        /// </summary>
        private OpenStreamTracker mStreamTracker = new OpenStreamTracker();


        /// <summary>
        /// Tests a stream to see if it contains a StuffIt archive.
        /// </summary>
        /// <param name="stream">Stream to test.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <returns>True if this looks like StuffIt.</returns>
        public static bool TestKind(Stream stream, AppHook appHook) {
            stream.Position = 0;
            return AnalyzeFile(stream, appHook, out ArchiveHeader? unused);
        }

        /// <summary>
        /// Private constructor.
        /// </summary>
        /// <param name="stream">Data stream if existing file, null if new archive.</param>
        /// <param name="appHook">Application hook reference.</param>
        private StuffIt(Stream? stream, AppHook appHook, ArchiveHeader header) {
            DataStream = stream;
            AppHook = appHook;
            mArchiveHeader = header;
        }

        /// <summary>
        /// Creates an empty archive object.
        /// </summary>
        /// <param name="appHook">Application hook reference.</param>
        /// <returns>Archive object.</returns>
        public static StuffIt CreateArchive(AppHook appHook) {
            return new StuffIt(null, appHook, new ArchiveHeader());
        }

        /// <summary>
        /// Opens an existing archive.
        /// </summary>
        /// <param name="stream">Archive data stream.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <returns>Archive instance.</returns>
        /// <exception cref="NotSupportedException">Stream is not a StuffIt archive.</exception>
        public static StuffIt OpenArchive(Stream stream, AppHook appHook) {
            stream.Position = 0;
            if (!AnalyzeFile(stream, appHook, out ArchiveHeader? header)) {
                throw new NotSupportedException("Incompatible data stream");
            }
            StuffIt archive = new StuffIt(stream, appHook, header);
            archive.Scan();
            return archive;
        }

        // IArchive

        public Stream? ReopenStream(Stream newStream) {
            Debug.Assert(DataStream != null);
            Stream? oldStream = DataStream;
            DataStream = newStream;
            return oldStream;
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
                    AppHook.LogW("StuffIt disposed while " + mStreamTracker.Count +
                        " streams are open");
                    mStreamTracker.CloseAll();
                }
            }
            if (mIsTransactionOpen) {
                AppHook.LogW("Disposing of StuffIt while transaction open");
                CancelTransaction();
            }
            IsValid = false;
        }

        /// <summary>
        /// Analyzes a data stream to see if it contains a StuffIt archive.
        /// </summary>
        /// <param name="stream">Archive data stream.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <param name="header">Result: archive header record.</param>
        /// <returns>True if this appears to be a StuffIt archive.</returns>
        private static bool AnalyzeFile(Stream stream, AppHook appHook,
                [NotNullWhen(true)] out ArchiveHeader? header) {
            if (stream.Length < ArchiveHeader.LENGTH) {
                header = null;
                return false;
            }
            // TODO: automatically detect and skip past MacBinary?

            // Read the header and test the two signature words.
            byte[] buf = new byte[ArchiveHeader.LENGTH];
            stream.ReadExactly(buf);

            header = new ArchiveHeader();
            if (!header.Load(buf, 0)) {
                return false;
            }
            return true;
        }

        /// <summary>
        /// Scans the records out of the archive.  The file should be positioned just past the
        /// archive header record.
        /// </summary>
        /// <remarks>
        /// <para>This should not throw an exception on bad data.  Damaged records should be
        /// flagged instead.</para>
        /// </remarks>
        private void Scan() {
            Debug.Assert(DataStream != null);

            for (int i = 0; i < mArchiveHeader.EntryCount; i++) {
                StuffIt_FileEntry? newEntry = StuffIt_FileEntry.ReadNextRecord(this);
                if (newEntry == null) {
                    Notes.AddE("Read " + i + " of " + mArchiveHeader.EntryCount + " records");
                    IsDubious = true;
                    break;
                }
                // TODO: handle HMF
                RecordList.Add(newEntry);
            }

            if (!IsDubious && DataStream.Position != mArchiveHeader.FileLength) {
                Notes.AddW("Consumed " + DataStream.Position +
                    " bytes, but total length field value is " + mArchiveHeader.FileLength);
                // keep going
            }
        }

        // IArchive
        public ArcReadStream OpenPart(IFileEntry ientry, FilePart part) {
            if (!IsValid) {
                throw new InvalidOperationException("Archive object is invalid");
            }
            if (mIsTransactionOpen) {
                throw new InvalidOperationException("Cannot open parts while transaction is open");
            }
            if (ientry.IsDamaged) {
                throw new IOException("Entry is too damaged to open");
            }
            StuffIt_FileEntry entry = (StuffIt_FileEntry)ientry;
            if (entry.Archive != this) {
                throw new ArgumentException("Entry is not part of this archive");
            }
            ArcReadStream newStream = entry.CreateReadStream(part);
            mStreamTracker.Add(entry, newStream);
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
            if (!IsValid) {
                throw new InvalidOperationException("Archive object is invalid");
            }
            if (IsDubious) {
                throw new InvalidOperationException("Cannot modify a dubious archive");
            }
            if (mStreamTracker.Count != 0) {
                throw new InvalidOperationException("One or more entries are open");
            }
            if (mIsTransactionOpen) {
                throw new InvalidOperationException("Transaction is already open");
            }
            Debug.Assert(mEditList.Count == 0);
            foreach (StuffIt_FileEntry entry in RecordList) {
                // Create an object to receive changes.
                StuffIt_FileEntry newEntry = StuffIt_FileEntry.CreateChangeObject(entry);
                entry.ChangeObject = newEntry;
                newEntry.OrigObject = entry;
                mEditList.Add(entry);
            }
            mIsTransactionOpen = true;
        }

        // IArchive
        public void CommitTransaction(Stream? outputStream) {
            if (!mIsTransactionOpen) {
                throw new InvalidOperationException("Must start transaction first");
            }

            if (outputStream != null) {
                if (!outputStream.CanWrite || !outputStream.CanSeek) {
                    throw new ArgumentException("Invalid output stream");
                }
                if (outputStream == DataStream) {
                    throw new InvalidOperationException("Cannot commit to source stream");
                }
            } else {
                throw new ArgumentException("Output stream cannot be null");
            }
            Debug.Assert(DataStream == null || DataStream.CanRead);

            Notes.Clear();

            //
            throw new Exception("TODO");
            //
        }

        // IArchive
        public void CancelTransaction() {
            if (!mIsTransactionOpen) {
                return;
            }
            DisposeSources(mEditList);
            mEditList.Clear();
            foreach (StuffIt_FileEntry entry in RecordList) {
                entry.ChangeObject = null;
            }
            mIsTransactionOpen = false;
        }

        private void DisposeSources(List<StuffIt_FileEntry> entryList) {
            foreach (StuffIt_FileEntry entry in entryList) {
                entry.DisposeSources();
            }
        }

        // IArchive
        public IFileEntry CreateRecord() {
            if (!mIsTransactionOpen) {
                throw new InvalidOperationException("Must start transaction first");
            }
            StuffIt_FileEntry newEntry = StuffIt_FileEntry.CreateNew(this);
            newEntry.ChangeObject = newEntry;       // changes are made directly to this object
            newEntry.OrigObject = newEntry;
            mEditList.Add(newEntry);
            return newEntry;
        }

        // IArchive
        public void DeleteRecord(IFileEntry ientry) {
            CheckModAccess(ientry);
            StuffIt_FileEntry entry = (StuffIt_FileEntry)ientry;
            if (!mEditList.Remove(entry)) {
                throw new FileNotFoundException("Entry not found");
            }
            entry.DisposeSources();
            entry.ChangeObject = null;      // mark as deleted
        }

        // IArchive
        public void AddPart(IFileEntry ientry, FilePart part, IPartSource partSource,
                CompressionFormat compressFmt) {
            CheckModAccess(ientry);
            if (partSource == IPartSource.NO_SOURCE) {
                throw new ArgumentException("Invalid part source");
            }
            ((StuffIt_FileEntry)ientry).AddPart(part, partSource, compressFmt);
        }

        // IArchive
        public void DeletePart(IFileEntry ientry, FilePart part) {
            CheckModAccess(ientry);
            ((StuffIt_FileEntry)ientry).DeletePart(part);
        }

        // IArchive
        public string? DebugDump() { return null; }

        /// <summary>
        /// Confirms that we are allowed to modify this entry.
        /// </summary>
        /// <param name="ientry">Entry that will be modified.</param>
        private void CheckModAccess(IFileEntry ientry) {
            if (!mIsTransactionOpen) {
                throw new InvalidOperationException("Must start transaction first");
            }
            if (ientry == IFileEntry.NO_ENTRY) {
                throw new ArgumentException("Cannot operate on NO_ENTRY");
            }
            if (ientry.IsDamaged) {
                // TODO: don't check this if we're trying to delete the record?
                throw new ArgumentException("Record '" + ientry.FileName +
                    "' is too damaged to access");
            }
            StuffIt_FileEntry? entry = ientry as StuffIt_FileEntry;
            if (entry == null || entry.Archive != this) {
                if (entry != null && entry.Archive == null) {
                    throw new ArgumentException("Entry is invalid");
                } else {
                    throw new FileNotFoundException("Entry is not part of this archive");
                }
            } else {
                if (entry.ChangeObject == null) {
                    throw new ArgumentException("Can't modify deleted record");
                }
            }
        }

        /// <summary>
        /// Adjusts a filename to be compatible with the archive format.
        /// </summary>
        /// <param name="fileName">Filename (not a pathname).</param>
        /// <returns>Adjusted filename.</returns>
        public static string AdjustFileName(string fileName) {
            // We're limited to Mac OS Roman, so replace anything that can't be represented.
            char[] chars = fileName.ToCharArray();
            for (int i = 0; i < chars.Length; i++) {
                if (chars[i] == StuffIt_FileEntry.SEP_CHAR ||
                        !MacChar.IsCharValid(chars[i], MacChar.Encoding.RomanShowCtrl)) {
                    chars[i] = '?';
                }
            }
            return new string(chars);
        }
    }
}
