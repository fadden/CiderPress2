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
using System.Collections;
using System.Diagnostics;

using CommonUtil;
using DiskArc.FS;
using static DiskArc.Defs;

namespace DiskArc.Arc {
    /// <summary>
    /// Binary II file handling.
    /// </summary>
    public class Binary2 : IArchiveExt {
        public const int CHUNK_LEN = 128;               // archive is a series of 128-byte chunks
        public const int MAX_FILE_NAME_LEN = 64;        // max length of stored pathname
        public const int MAX_RECORDS = 256;             // limited by "files to follow" field
        public const byte ID0 = 0x0a;
        public const byte ID1 = 0x47;
        public const byte ID2 = 0x4c;
        public const byte ID3 = 0x02;
        public const int FILES_TO_FOLLOW_OFFSET = 127;

        private static readonly ArcCharacteristics sCharacteristics = new ArcCharacteristics(
            name: "Binary II",
            canWrite: true,
            hasSingleEntry: false,
            hasResourceForks: false,
            hasDiskImages: false,
            hasArchiveComment: false,
            hasRecordComments: false,
            defaultDirSep: '/');

        //
        // IArchive interfaces.
        //

        public ArcCharacteristics Characteristics => sCharacteristics;
        public static ArcCharacteristics SCharacteristics => sCharacteristics;

        public bool IsReadOnly => IsDubious;
        public bool IsDubious { get; internal set; }

        public Notes Notes { get; } = new Notes();

        public bool IsReconstructionNeeded {
            get {
                if (DataStream != null && !DataStream.CanWrite) {
                    return true;
                }
                return mIsReconstructionNeeded;
            }
            private set {
                mIsReconstructionNeeded = value;
            }
        }
        private bool mIsReconstructionNeeded;

        public string Comment { get => string.Empty; set { } }

        public IEnumerator<IFileEntry> GetEnumerator() {
            return RecordList.GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() {
            return RecordList.GetEnumerator();
        }
        public int Count => RecordList.Count;

        public Stream? DataStream { get; private set; }

        //
        // Internal interfaces.
        //

        /// <summary>
        /// Application hook reference.
        /// </summary>
        internal AppHook AppHook { get; private set; }

        /// <summary>
        /// List of records in the file.
        /// </summary>
        internal List<Binary2_FileEntry> RecordList {
            get {
                //// Note to self: the debugger's property evaluator will trigger this, potentially
                //// leading to situations where the behavior changes under the debugger.
                //if (!mIsScanned) {
                //    ScanRecords();
                //}
                return mRecords;
            }
        }
        private List<Binary2_FileEntry> mRecords = new List<Binary2_FileEntry>();

        /// <summary>
        /// True if a transaction is open.
        /// </summary>
        private bool mIsTransactionOpen;

        /// <summary>
        /// Modified entry list.  This will become the new list of entries when the transaction is
        /// committed.
        /// </summary>
        private List<Binary2_FileEntry> mEditList = new List<Binary2_FileEntry>();

        /// <summary>
        /// True if the archive contents have been scanned.
        /// </summary>
        //private bool mIsScanned;

        /// <summary>
        /// Open-file tracking.
        /// </summary>
        private class OpenFileRec {
            public Binary2_FileEntry Entry { get; private set; }
            public ArcReadStream ReadStream { get; private set; }

            public OpenFileRec(Binary2_FileEntry entry, ArcReadStream stream) {
                Entry = entry;
                ReadStream = stream;
            }

            public override string ToString() {
                return "[Binary2 open: '" + Entry.FullPathName + "]";
            }
        }

        /// <summary>
        /// List of open files.
        /// </summary>
        private List<OpenFileRec> mOpenFiles = new List<OpenFileRec>();


        /// <summary>
        /// Tests a stream to see if it contains a Binary II file.
        /// </summary>
        /// <remarks>
        /// <para>Just check a few fields in the record header for the first entry.  While
        /// zero-length files are technically valid Binary II, we don't accept them here.</para>
        /// </remarks>
        /// <param name="stream">Stream to test.</param>
        /// <returns>True if this looks like Binary II.</returns>
        public static bool TestKind(Stream stream, AppHook appHook) {
            stream.Position = 0;

            byte[] readBuf = new byte[CHUNK_LEN];
            try {
                stream.ReadExactly(readBuf, 0, CHUNK_LEN);
            } catch (EndOfStreamException) {
                return false;
            }

            // Check the ID bytes.
            if (readBuf[0x00] != ID0 || readBuf[0x01] != ID1 || readBuf[0x02] != ID2 ||
                    readBuf[0x12] != ID3) {
                Debug.WriteLine("ID bytes don't match Binary II");
                return false;
            }

            // As an added test, check the filename length byte.
            byte fileNameLen = readBuf[0x17];
            if (fileNameLen == 0 || fileNameLen > MAX_FILE_NAME_LEN) {
                Debug.WriteLine("Filename in first record is invalid");
                return false;
            }

            // We could also insist that the file be a multiple of 128 bytes, but that would
            // prevent us from recovering partial data from files that were truncated or had
            // a few stray bytes added to the end.

            return true;
        }

        /// <summary>
        /// Private constructor.
        /// </summary>
        /// <param name="stream">Data stream if existing file, null if new archive.</param>
        /// <param name="appHook">Application hook reference.</param>
        private Binary2(Stream? stream, AppHook appHook) {
            DataStream = stream;
            AppHook = appHook;
        }

        /// <summary>
        /// Creates an empty archive object.
        /// </summary>
        /// <param name="appHook">Application hook reference.</param>
        /// <returns>Archive object.</returns>
        public static Binary2 CreateArchive(AppHook appHook) {
            Binary2 newArc = new Binary2(null, appHook);
            //newArc.mIsScanned = true;
            return newArc;
        }

        /// <summary>
        /// Creates an archive object from an existing archive stream.
        /// </summary>
        /// <param name="stream">Data stream, must be readable and seekable.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <returns>Archive object.</returns>
        /// <exception cref="NotSupportedException">Not a Binary II archive.</exception>
        public static Binary2 OpenArchive(Stream stream, AppHook appHook) {
            // An empty Binary II file is zero bytes long, so we allow empty files.
            if (stream.Length > 0 && !TestKind(stream, appHook)) {
                throw new NotSupportedException("Incompatible data stream");
            }
            Binary2 archive = new Binary2(stream, appHook);
            archive.ScanRecords();
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
                if (mOpenFiles.Count != 0) {
                    AppHook.LogW("Binary2 disposed while " + mOpenFiles.Count + " files are open");
                    // Walk through from end to start so we don't trip when entries are removed.
                    for (int i = mOpenFiles.Count - 1; i >= 0; --i) {
                        // This will call back into our StreamClosing function, which will
                        // remove it from the list.
                        mOpenFiles[i].ReadStream.Close();
                    }
                }
            }
            if (mIsTransactionOpen) {
                AppHook.LogW("Disposing of Binary2 while transaction open");
                CancelTransaction();
            }
        }

        /// <summary>
        /// Scans the records out of the file.
        /// </summary>
        /// <remarks>
        /// This should not throw an exception on bad data.  Damage should be flagged instead.
        /// </remarks>
        private void ScanRecords() {
            Debug.Assert(DataStream != null);
            Debug.Assert(mRecords.Count == 0);

            DataStream.Position = 0;

            while (DataStream.Position < DataStream.Length) {
                Binary2_FileEntry? newEntry = Binary2_FileEntry.ReadNextRecord(this);
                if (newEntry == null) {
                    // Unexpected.  Act like we hit end of file.
                    Notes.AddW("Archive contents ended early; junk at end of file?");
                    break;
                }
                mRecords.Add(newEntry);
            }
            //mIsScanned = true;      // must set before ValidateRecords tries to iterate

            ValidateRecords();
        }

        /// <summary>
        /// Validates intra-record consistency.
        /// </summary>
        private void ValidateRecords() {
            Dictionary<string, Binary2_FileEntry> allRecs = new();
            Dictionary<string, Binary2_FileEntry> dirRecs = new();

            int toFollow = RecordList.Count;
            foreach (Binary2_FileEntry entry in RecordList) {
                Debug.Assert(entry.ChangeObject == null);
                Debug.Assert(!entry.HeaderDirty);

                if (entry.FilesToFollow != --toFollow) {
                    Notes.AddW("Files to follow is incorrect: " + entry.FileName);
                }

                if (allRecs.ContainsKey(entry.FileName)) {
                    // Whoa, deja BLU.
                    Notes.AddW("Found record with duplicate filename: " + entry.FileName);
                    continue;
                }

                if (!Binary2_FileEntry.IsFileNameValid(entry.FileName)) {
                    // This would have been reported during the record scan.  No value in
                    // doing additional filename checks.
                    continue;
                }

                // If the filename is a partial pathname, we need to confirm that all paths
                // are explicitly defined in the archive.  If this is foo/bar/ack.txt, then
                // we need to verify that foo/bar exists.  (Checking for "foo" is redundant:
                // either we checked it when we encountered foo/bar, or we're just complaining
                // twice when once will do.)
                int slashIndex = entry.FileName.LastIndexOf('/');
                if (slashIndex >= 0) {
                    string baseName = entry.FileName.Substring(0, slashIndex);
                    if (!dirRecs.ContainsKey(baseName)) {
                        Notes.AddW("Record has unprepared partial path: " + entry.FileName);
                    }
                }

                if (entry.IsDirectory) {
                    dirRecs.Add(entry.FileName, entry);
                }
                allRecs.Add(entry.FileName, entry);
            }
        }

        // IArchive
        public ArcReadStream OpenPart(IFileEntry ientry, FilePart part) {
            if (mIsTransactionOpen) {
                throw new InvalidOperationException("Cannot open parts while transaction is open");
            }
            if (part != FilePart.DataFork) {
                throw new FileNotFoundException("Only data forks here");
            }
            Binary2_FileEntry entry = (Binary2_FileEntry)ientry;
            if (entry.Archive != this) {
                throw new ArgumentException("Entry is not part of this archive");
            }
            ArcReadStream newStream = entry.CreateReadStream();
            mOpenFiles.Add(new OpenFileRec(entry, newStream));
            return newStream;
        }

        // IArchiveExt
        public void StreamClosing(ArcReadStream stream) {
            // Start looking at the end, because if we're doing a Dispose-time mass close
            // we'll be doing it in that order.
            for (int i = mOpenFiles.Count - 1; i >= 0; --i) {
                if (mOpenFiles[i].ReadStream == stream) {
                    mOpenFiles.RemoveAt(i);
                    return;
                }
            }
            Debug.Assert(false, "Got StreamClosing for unknown stream");
        }

        // IArchive
        public void StartTransaction() {
            if (IsDubious) {
                throw new InvalidOperationException("Cannot modify a dubious archive");
            }
            if (mOpenFiles.Count != 0) {
                throw new InvalidOperationException("One or more entries are open");
            }
            if (mIsTransactionOpen) {
                throw new InvalidOperationException("Transaction is already open");
            }
            //if (!mIsScanned) {
            //    ScanRecords();
            //}
            Debug.Assert(mEditList.Count == 0);
            foreach (Binary2_FileEntry entry in RecordList) {
                // Create an object to receive changes.
                Binary2_FileEntry newEntry = Binary2_FileEntry.CreateChangeObject(entry);
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
            } else if (IsReconstructionNeeded) {
                throw new ArgumentException("Reconstruction needed, output stream cannot be null");
            }
            if (mEditList.Count > MAX_RECORDS) {
                throw new InvalidOperationException("Too many records (max " + MAX_RECORDS + ")");
            }
            Debug.Assert(DataStream == null || DataStream.CanRead);

            Notes.Clear();

            // We could do some intra-record integrity checks here, but that might be a bad idea,
            // because it would prevent the application from fixing problems unless it were able
            // to fix them all at once.
            foreach (Binary2_FileEntry oentry in mEditList) {
                Binary2_FileEntry entry = oentry.ChangeObject!;
                if (entry.FileName.Length == 0) {
                    throw new InvalidOperationException("Record has an empty filename");
                }
                if (!entry.HasDataPart && !entry.IsDirectory) {
                    throw new InvalidOperationException("Data fork part not populated: " +
                        entry.FileName);
                }
            }

            if (outputStream != null) {
                // Seek to start of stream.  We could write the archive to the current position,
                // but that gets a little strange when we rotate to the new stream.
                outputStream.Position = 0;
                outputStream.SetLength(0);
            }

            // Set the "files to follow" value in the headers.  If it changed, we will need to
            // rewrite the header rather than just blindly copying it.
            int toFollow = mEditList.Count;
            foreach (Binary2_FileEntry entry in mEditList) {
                entry.FilesToFollow = --toFollow;
            }

            // Allocate these once per update.
            byte[] headerBuf = new byte[CHUNK_LEN];
            byte[] tmpBuf1 = new byte[16384];
            byte[] tmpBuf2 = new byte[16384];

            try {
                // Walk through the list of change objects.  For each entry, there are three
                // possible courses of action:
                // (1) Entry is unmodified.  Copy header and body.
                // (2) Header changed, body unmodified.  Output new header and copy body.
                // (3) Header and body changed.  Output new versions of both.
                //
                // The header contents may not be known until the body has been written, e.g. when
                // working with compressed files.
                //
                // The archive data stream will be null if this is a new archive.
                foreach (Binary2_FileEntry origEntry in mEditList) {
                    Binary2_FileEntry changeEntry = origEntry.ChangeObject!;
                    if (changeEntry.HasPartChanges || DataStream == null) {
                        // New data.  We need to leave room for the header.
                        Debug.Assert(outputStream != null);
                        long headerPosn = outputStream.Position;

                        // Write the parts, compressing data if desired and possible.
                        outputStream.Position = headerPosn + CHUNK_LEN;
                        changeEntry.WriteParts(outputStream, tmpBuf1, tmpBuf2);

                        // Generate a header with the updated values, and seek back and write it.
                        changeEntry.SerializeHeader(headerBuf);
                        long curPosn = outputStream.Position;
                        outputStream.Position = headerPosn;
                        outputStream.Write(headerBuf, 0, CHUNK_LEN);
                        outputStream.Position = curPosn;

                        changeEntry.HeaderFileOffset = headerPosn;        // update file position
                    } else if (changeEntry.HeaderDirty) {
                        // Only the header changed.  Serialize and write it.  If we're writing to
                        // a new file, also copy the data.
                        Debug.Assert(DataStream != null);
                        changeEntry.SerializeHeader(headerBuf);
                        if (outputStream != null) {
                            long headerPosn = outputStream.Position;
                            outputStream.Write(headerBuf, 0, CHUNK_LEN);
                            DataStream.Position = origEntry.HeaderFileOffset + CHUNK_LEN;
                            FileUtil.CopyStream(DataStream, outputStream,
                                (int)changeEntry.StorageSize, tmpBuf1);
                            changeEntry.HeaderFileOffset = headerPosn;    // update file position
                        } else {
                            // Just edit in place.
                            DataStream.Position = origEntry.HeaderFileOffset;
                            DataStream.Write(headerBuf, 0, CHUNK_LEN);
                        }
                    } else {
                        // Absolutely nothing changed.  Copy header and body if we're not updating
                        // in place.
                        Debug.Assert(DataStream != null);
                        if (outputStream != null) {
                            long headerPosn = outputStream.Position;
                            DataStream.Position = origEntry.HeaderFileOffset;
                            FileUtil.CopyStream(DataStream, outputStream,
                                (int)changeEntry.StorageSize + CHUNK_LEN, tmpBuf1);
                            changeEntry.HeaderFileOffset = headerPosn;    // update file position
                        }
                    }

                    // We need to set this for directories, which don't have a data part when
                    // they're being added, but effectively become zero-byte files afterward.
                    changeEntry.HasDataPart = true;
                }
            } catch {
                // Failed; reset output stream.
                if (outputStream != null) {
                    outputStream.Position = 0;
                    outputStream.SetLength(0);
                }
                throw;
            }

            // Success!  Switch to the new archive stream and swap record sets.  We don't own
            // the archive stream, so we don't dispose of it.

            if (outputStream != null) {
                // Not an in-place edit.  Switch to the new stream.
                DataStream = outputStream;
            }
            Debug.Assert(DataStream != null);
            DataStream.Flush();

            // Invalidate and clear the old record list.
            foreach (Binary2_FileEntry oldEntry in RecordList) {
                if (oldEntry.ChangeObject == null) {
                    oldEntry.Invalidate();
                }
            }
            mRecords.Clear();

            DisposeSources(mEditList);

            // Copy updates back into original objects, so application can keep using them.
            foreach (Binary2_FileEntry newEntry in mEditList) {
                newEntry.CommitChange();
            }

            // Swap lists.
            List<Binary2_FileEntry> tmp = mRecords;
            mRecords = mEditList;
            mEditList = tmp;

            mIsTransactionOpen = false;
            IsReconstructionNeeded = false;
            //Debug.Assert(mIsScanned);

            // Validate the contents, mostly as a self-check.
            ValidateRecords();
        }

        // IArchive
        public void CancelTransaction() {
            if (!mIsTransactionOpen) {
                return;
            }
            DisposeSources(mEditList);
            mEditList.Clear();
            foreach (Binary2_FileEntry entry in RecordList) {
                entry.ChangeObject = null;
            }
            mIsTransactionOpen = false;
            IsReconstructionNeeded = false;
        }

        private void DisposeSources(List<Binary2_FileEntry> entryList) {
            foreach (Binary2_FileEntry entry in entryList) {
                entry.DisposeSources();
            }
        }

        // IArchive
        public IFileEntry CreateRecord() {
            if (!mIsTransactionOpen) {
                throw new InvalidOperationException("Must start transaction first");
            }
            // Not checking MAX_RECORDS here, because they might remove some before committing.
            Binary2_FileEntry newEntry = Binary2_FileEntry.CreateNew(this);
            newEntry.ChangeObject = newEntry;       // changes are made directly to this object
            newEntry.OrigObject = newEntry;
            mEditList.Add(newEntry);
            IsReconstructionNeeded = true;
            return newEntry;
        }

        // IArchive
        public void DeleteRecord(IFileEntry ientry) {
            CheckModAccess(ientry);
            Binary2_FileEntry entry = (Binary2_FileEntry)ientry;
            if (!mEditList.Remove(entry)) {
                throw new FileNotFoundException("Entry not found");
            }
            entry.DisposeSources();
            entry.ChangeObject = null;      // mark as deleted
            IsReconstructionNeeded = true;
        }

        // IArchive
        public void AddPart(IFileEntry ientry, FilePart part, IPartSource partSource,
                CompressionFormat compressFmt) {
            CheckModAccess(ientry);
            if (part != FilePart.DataFork) {
                throw new NotSupportedException("Invalid part: " + part);
            }
            if (partSource == IPartSource.NO_SOURCE) {
                throw new ArgumentException("Invalid part source");
            }
            ((Binary2_FileEntry)ientry).AddPart(partSource, compressFmt);
            IsReconstructionNeeded = true;
        }

        // IArchive
        public void DeletePart(IFileEntry ientry, FilePart part) {
            CheckModAccess(ientry);
            if (part != FilePart.DataFork) {
                throw new FileNotFoundException("No such part: " + part);
            }
            ((Binary2_FileEntry)ientry).DeletePart();
            IsReconstructionNeeded = true;
        }

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
                throw new ArgumentException("Record '" + ientry.FileName +
                    "' is too damaged to access");
            }
            Binary2_FileEntry? entry = ientry as Binary2_FileEntry;
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
        /// Rounds up to the nearest multiple of the chunk length.
        /// </summary>
        internal static int RoundUpChunkLen(int len) {
            return (len + CHUNK_LEN - 1) & ~(CHUNK_LEN - 1);    // assuming power of 2
        }


        /// <summary>
        /// Adjusts a filename to be compatible with the archive format.  Call this on individual
        /// pathname components.
        /// </summary>
        /// <param name="fileName">Filename (not a pathname).</param>
        /// <returns>Adjusted filename.</returns>
        public static string AdjustFileName(string fileName) {
            // Filenames must be ProDOS format, upper-case only.
            return ProDOS_FileEntry.AdjustFileName(fileName).ToUpper();
        }

        public override string ToString() {
            return "[Binary2: records=" + mRecords.Count +
                ", transaction=" + (mIsTransactionOpen ? "OPEN" : "closed") + "]";
        }
    }
}
