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
using System.Text;

using CommonUtil;
using static DiskArc.Defs;

namespace DiskArc.Arc {
    /// <summary>
    /// ZIP archive handling.
    /// </summary>
    /// <remarks>
    /// <para>.NET provides the ZipFile, ZipArchive, and ZipArchiveEntry classes for working
    /// with ZIP archives.  These don't provide everything we want, so we manage the file
    /// directly.</para>
    /// <para>We could offer a "partial reconstruction" mode for things like filename and
    /// timestamp changes, where instead of rewriting the entire archive we just rewrite the
    /// central directory.  This doesn't seem worthwhile, and could potentially confuse a
    /// utility that (incorrectly) relies on the local file headers.</para>
    /// <para>Zip64 is not supported.  While it's possible that someone might have a disk image
    /// larger than 4GB, it's unlikely that they would want to re-compress it after every time
    /// they use it, so requiring the use of an external ZIP tool to unpack it is not too
    /// burdensome.</para>
    /// </remarks>
    //
    // This could be simpler if we just discarded and re-loaded the archive info after an update,
    // and would be pretty quick because of the central directory structure.  This isn't the
    // case for the other formats we support, though, and I want to keep to the same pattern.
    //
    // TODO: interpret some of the "extra" data fields, like the extended date/time value.
    public class Zip : IArchiveExt {
        public const string MAC_ZIP_RSRC_PREFIX = "__MACOSX";
        public const string MAC_ZIP_RSRC_PREFIX_DIR = MAC_ZIP_RSRC_PREFIX + "/";

        private const int MAX_COMMENT_LENGTH = 65535;       // max length of end-of-archive comment
        private const int MAX_RECORDS = 65535;              // max number of records in archive

        private const string FILENAME_RULES = "Partial pathname.";
        private static readonly ArcCharacteristics sCharacteristics = new ArcCharacteristics(
            name: "ZIP",
            canWrite: true,
            hasSingleEntry: false,
            hasResourceForks: false,
            hasDiskImages: false,
            hasArchiveComment: true,
            hasRecordComments: true,
            defaultDirSep: Zip_FileEntry.SEP_CHAR,
            fnSyntax: FILENAME_RULES,
            // If we supported higher-resolution timestamps, this range would be larger.
            tsStart: TimeStamp.MSDOS_MIN_TIMESTAMP,
            tsEnd: TimeStamp.MSDOS_MAX_TIMESTAMP
        );

        //
        // IArchive interfaces.
        //

        public ArcCharacteristics Characteristics => sCharacteristics;
        public static ArcCharacteristics SCharacteristics => sCharacteristics;

        public Stream? DataStream { get; internal set; }

        public bool IsValid { get; private set; } = true;
        public bool IsReadOnly => IsDubious || (DataStream != null && !DataStream.CanWrite);
        public bool IsDubious { get; internal set; }

        public Notes Notes { get; } = new Notes();

        public bool IsReconstructionNeeded => true;

        public string Comment {
            get {
                return mComment;
            }
            set {
                if (!mIsTransactionOpen) {
                    throw new InvalidOperationException(
                        "Cannot modify archive without transaction");
                }
                // TODO: scan for presence of EOCD signature when setting
                mNewComment = value;
            }
        }
        private string mComment = string.Empty;
        private string mNewComment = string.Empty;

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
        /// End of Central Directory record.
        /// </summary>
        private class EndOfCentralDir {
            public const int LENGTH = 22;
            public static readonly byte[] SIGNATURE = new byte[] { 0x50, 0x4b, 0x05, 0x06 };

            public ushort DiskNumber { get; set; }
            public ushort DiskWithCentralDir { get; set; }
            public ushort NumEntriesHere { get; set; }
            public ushort NumEntries { get; set; }
            public uint CentralDirLength { get; set; }
            public uint CentralDirOffset { get; set; }
            public ushort CommentLength { get; set; }

            // File offset of the start of the EOCD record.
            public long FileOffset { get; set; }

            public EndOfCentralDir() { }

            /// <summary>
            /// Copy constructor.
            /// </summary>
            public EndOfCentralDir(EndOfCentralDir src) {
                byte[] buf = new byte[LENGTH];
                src.Store(buf, 0);
                Load(buf, 0);
                FileOffset = src.FileOffset;
            }

            /// <summary>
            /// Loads the fixed-length portion of the EOCD record from the buffer.
            /// </summary>
            public void Load(byte[] buf, int offset) {
                int startOffset = offset;
                for (int i = 0; i < 4; i++) {
                    if (buf[offset++] != SIGNATURE[i]) {
                        throw new InvalidDataException("Signature does not match");
                    }
                }
                DiskNumber = RawData.ReadU16LE(buf, ref offset);
                DiskWithCentralDir = RawData.ReadU16LE(buf, ref offset);
                NumEntriesHere = RawData.ReadU16LE(buf, ref offset);
                NumEntries = RawData.ReadU16LE(buf, ref offset);
                CentralDirLength = RawData.ReadU32LE(buf, ref offset);
                CentralDirOffset = RawData.ReadU32LE(buf, ref offset);
                CommentLength = RawData.ReadU16LE(buf, ref offset);
                Debug.Assert(offset - startOffset == LENGTH);
            }

            /// <summary>
            /// Stores the fixed-length portion of the EOCD record into the buffer.
            /// </summary>
            public void Store(byte[] buf, int offset) {
                int startOffset = offset;
                Array.Copy(SIGNATURE, buf, SIGNATURE.Length);
                offset += SIGNATURE.Length;
                RawData.WriteU16LE(buf, ref offset, DiskNumber);
                RawData.WriteU16LE(buf, ref offset, DiskWithCentralDir);
                RawData.WriteU16LE(buf, ref offset, NumEntriesHere);
                RawData.WriteU16LE(buf, ref offset, NumEntries);
                RawData.WriteU32LE(buf, ref offset, CentralDirLength);
                RawData.WriteU32LE(buf, ref offset, CentralDirOffset);
                RawData.WriteU16LE(buf, ref offset, CommentLength);
                Debug.Assert(offset - startOffset == LENGTH);
            }
        }
        private EndOfCentralDir mEOCD;

        /// <summary>
        /// Application hook reference.
        /// </summary>
        internal AppHook AppHook { get; private set; }

        /// <summary>
        /// List of records.
        /// </summary>
        internal List<Zip_FileEntry> RecordList { get; private set; } = new List<Zip_FileEntry>();

        /// <summary>
        /// True if a transaction is open.
        /// </summary>
        private bool mIsTransactionOpen;

        // Temporary buffer, used for stream copying and compression, allocated on first use.
        internal byte[]? TmpBuf { get; private set; }

        /// <summary>
        /// Modified entry list.  This will become the new list of entries when the transaction is
        /// committed.
        /// </summary>
        private List<Zip_FileEntry> mEditList = new List<Zip_FileEntry>();

        /// <summary>
        /// Open stream tracker.
        /// </summary>
        private OpenStreamTracker mStreamTracker = new OpenStreamTracker();


        /// <summary>
        /// Tests a stream to see if it contains a ZIP archive.
        /// </summary>
        /// <param name="stream">Stream to test.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <returns>True if this looks like ZIP.</returns>
        public static bool TestKind(Stream stream, AppHook appHook) {
            stream.Position = 0;
            return AnalyzeFile(stream, appHook, out EndOfCentralDir? unused);
        }

        /// <summary>
        /// Private constructor.
        /// </summary>
        /// <param name="stream">Data stream if existing file, null if new archive.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <param name="eocd">End of Central Directory object.</param>
        private Zip(Stream? stream, AppHook appHook, EndOfCentralDir eocd) {
            DataStream = stream;
            AppHook = appHook;
            mEOCD = eocd;
        }

        /// <summary>
        /// Creates an empty archive object.
        /// </summary>
        /// <param name="appHook">Application hook reference.</param>
        /// <returns>Archive object.</returns>
        public static Zip CreateArchive(AppHook appHook) {
            return new Zip(null, appHook, new EndOfCentralDir());
        }

        /// <summary>
        /// Opens an existing archive.
        /// </summary>
        /// <param name="stream">Archive data stream.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <returns>Archive instance.</returns>
        /// <exception cref="NotSupportedException">Data stream is not a ZIP archive.</exception>
        public static Zip OpenArchive(Stream stream, AppHook appHook) {
            stream.Position = 0;
            if (!AnalyzeFile(stream, appHook, out EndOfCentralDir? eocd)) {
                throw new NotSupportedException("Incompatible data stream");
            }
            Debug.Assert(eocd != null);
            Zip archive = new Zip(stream, appHook, eocd);
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
                    AppHook.LogW("Zip disposed while " + mStreamTracker.Count +
                        " streams are open");
                    mStreamTracker.CloseAll();
                }
            }
            if (mIsTransactionOpen) {
                AppHook.LogW("Disposing of Zip while transaction open");
                CancelTransaction();
            }
            IsValid = false;
        }

        /// <summary>
        /// Analyzes a data stream to see if contains a ZIP archive.
        /// </summary>
        /// <remarks>
        /// <para>ZIP archives aren't required to start with something specific, e.g. they can
        /// be a self-extracting executable.  Instead, we need to search backward from the end
        /// for the EOCD marker.  We check the values in the EOCD record for validity.</para>
        /// </remarks>
        /// <param name="stream">Archive data stream.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <param name="eocd">Result: EOCD record.</param>
        /// <returns>True if this appears to be a ZIP archive.</returns>
        private static bool AnalyzeFile(Stream stream, AppHook appHook, out EndOfCentralDir? eocd) {
            eocd = null;
            if (stream.Length < EndOfCentralDir.LENGTH) {
                return false;
            }

            // Read up to 64KB from the end of the file.
            int readLen = (int)Math.Min(stream.Length, EndOfCentralDir.LENGTH + MAX_COMMENT_LENGTH);
            byte[] scanBuf = new byte[readLen];
            long readPosn = stream.Position = stream.Length - readLen;
            try {
                stream.ReadExactly(scanBuf, 0, readLen);
            } catch (EndOfStreamException) {
                throw new IOException("Failed to read expected amount");
            }

            // Search for the signature, starting from the end.
            int offset = readLen - EndOfCentralDir.LENGTH;
            while (offset >= 0) {
                int i;
                for (i = 0; i < EndOfCentralDir.SIGNATURE.Length; i++) {
                    if (scanBuf[offset + i] != EndOfCentralDir.SIGNATURE[i]) {
                        break;
                    }
                }
                if (i == EndOfCentralDir.SIGNATURE.Length) {
                    break;
                }
                offset--;
            }
            if (offset < 0) {
                return false;
            }

            eocd = new EndOfCentralDir();
            eocd.Load(scanBuf, offset);
            eocd.FileOffset = readPosn + offset;

            // Check some values.
            if (eocd.CentralDirOffset + eocd.CentralDirLength != eocd.FileOffset) {
                appHook.LogI("File offsets don't line up: CD off " + eocd.CentralDirOffset +
                    " + len " + eocd.CentralDirLength + " = " +
                    eocd.CentralDirOffset + eocd.CentralDirLength + " != found offset " +
                    eocd.FileOffset);
                return false;
            }
            if (eocd.DiskNumber != 0 || eocd.DiskWithCentralDir != 0 ||
                    eocd.NumEntries != eocd.NumEntriesHere) {
                appHook.LogI("Looks like a piece of a multi-part ZIP archive");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Scans the records out of the archive.
        /// </summary>
        /// <remarks>
        /// <para>This should not throw an exception on bad data.  Damaged records should be
        /// flagged instead.</para>
        /// </remarks>
        /// <param name="cdStart">File offset of start of Central Directory.</param>
        private void Scan() {
            Debug.Assert(DataStream != null);

            // Extract the EOCD comment, if any.
            if (mEOCD.CommentLength != 0) {
                DataStream.Position = mEOCD.FileOffset + EndOfCentralDir.LENGTH;
                byte[] rawComment = new byte[mEOCD.CommentLength];
                try {
                    DataStream.ReadExactly(rawComment, 0, rawComment.Length);
                    // Archive comments are always encoded with code page 437.
                    mComment = CP437.ToUnicode(rawComment, 0, rawComment.Length);
                } catch (EndOfStreamException) {
                    Notes.AddE("Archive comment runs off end of archive");
                    IsDubious = true;
                }
            }

            // Read the central dir entries.
            DataStream.Position = mEOCD.CentralDirOffset;
            for (int i = 0; i < mEOCD.NumEntries; i++) {
                if (DataStream.Position >= mEOCD.CentralDirOffset + mEOCD.CentralDirLength) {
                    Notes.AddE("Records overran central directory area");
                    IsDubious = true;
                    break;
                }
                Zip_FileEntry? newEntry = Zip_FileEntry.ReadNextRecord(this);
                if (newEntry == null) {
                    Notes.AddE("Read " + i + " of " + mEOCD.NumEntries + " records");
                    IsDubious = true;
                    break;
                }
                Debug.Assert(DataStream.Position > mEOCD.CentralDirOffset);
                RecordList.Add(newEntry);
            }
            if (DataStream.Position != mEOCD.CentralDirOffset + mEOCD.CentralDirLength) {
                // Not really a problem, but it does suggest that there are entries beyond
                // what was specified in NumEntries.
                Notes.AddW("Records did not fill central directory area (off by " +
                    (mEOCD.CentralDirOffset + mEOCD.CentralDirLength - DataStream.Position) +
                    " bytes)");
                IsDubious = true;
            }

            // Double-check the file record length field.
            long endOffset = mEOCD.CentralDirOffset;
            for (int i = RecordList.Count - 1; i >= 0; i--) {
                long startOffset = RecordList[i].LocalHeaderOffsetCheck;
                if (endOffset - startOffset != RecordList[i].FileRecordLengthCheck) {
                    Notes.AddW("Length discrepancy in entry " + i + ": scanned=" +
                        RecordList[i].FileRecordLengthCheck + ", diff=" + (endOffset - startOffset));
                    IsDubious = true;
                }
                endOffset = startOffset;
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
            if (part != FilePart.DataFork) {
                throw new FileNotFoundException("Archive only stores data forks");
            }
            Zip_FileEntry entry = (Zip_FileEntry)ientry;
            if (entry.Archive != this) {
                throw new ArgumentException("Entry is not part of this archive");
            }
            ArcReadStream newStream = entry.CreateReadStream();
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
            foreach (Zip_FileEntry entry in RecordList) {
                // Create a clone, add the clone to our list, then add a reference to the clone
                // to the original object.
                Zip_FileEntry newEntry = Zip_FileEntry.CreateChangeObject(entry);
                entry.ChangeObject = newEntry;
                newEntry.OrigObject = entry;
                mEditList.Add(entry);
            }
            mNewComment = mComment;         // clone the archive comment
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

            // Allocate temporary buffer for stream data if we haven't yet.
            if (TmpBuf == null) {
                const int size = 32768;
                TmpBuf = new byte[size];
            }

            // Do some quick validity checks on entries in the "edit" list.
            if (mEditList.Count > MAX_RECORDS) {
                throw new InvalidOperationException("Too many records (max " + MAX_RECORDS + ")");
            }
            foreach (Zip_FileEntry oentry in mEditList) {
                Zip_FileEntry entry = oentry.ChangeObject!;
                if (entry.FileName.Length == 0) {
                    throw new InvalidOperationException("Record has an empty filename");
                }
                if (!entry.HasDataPart) {
                    throw new InvalidOperationException("Record is empty: " + entry.FileName);
                }
            }

            // Clone the EOCD record.
            EndOfCentralDir newEocd = new EndOfCentralDir(mEOCD);

            outputStream.Position = 0;
            outputStream.SetLength(0);
            try {
                // Write the LFH and file data.
                foreach (Zip_FileEntry oentry in mEditList) {
                    Zip_FileEntry entry = oentry.ChangeObject!;
                    if (entry.IsDirty) {
                        entry.WriteRecord(outputStream);
                    } else {
                        entry.CopyRecord(outputStream);
                    }
                }

                long cdStartOffset = outputStream.Position;

                // Write the CDFH entries.
                foreach (Zip_FileEntry oentry in mEditList) {
                    Zip_FileEntry entry = oentry.ChangeObject!;
                    entry.WriteCentralDirEntry(outputStream);
                }
                long cdEndOffset = outputStream.Position;

                // Write the EOCD record.
                newEocd.FileOffset = cdEndOffset;
                newEocd.DiskNumber = newEocd.DiskWithCentralDir = 0;
                newEocd.NumEntriesHere = newEocd.NumEntries = (ushort)mEditList.Count;
                newEocd.CentralDirLength = (uint)(cdEndOffset - cdStartOffset);
                newEocd.CentralDirOffset = (uint)cdStartOffset;
                byte[] rawComment = CP437.FromUnicode(mNewComment);
                if (rawComment.Length > MAX_COMMENT_LENGTH) {
                    throw new InvalidOperationException("Archive comment is too long");
                }
                newEocd.CommentLength = (ushort)rawComment.Length;
                newEocd.Store(TmpBuf, 0);
                outputStream.Write(TmpBuf, 0, EndOfCentralDir.LENGTH);
                outputStream.Write(rawComment, 0, rawComment.Length);
            } catch {
                // Failed; truncate output stream so we don't have a partial archive sitting around.
                outputStream.Position = 0;
                outputStream.SetLength(0);
                throw;
            }

            // Success!  Switch to the new archive stream and swap record sets.  We don't own
            // the archive stream, so we don't dispose of it.

            mEOCD = newEocd;
            mComment = mNewComment;

            // Switch to the new stream.
            DataStream = outputStream;
            DataStream.Flush();
            // Invalidate deleted records.
            foreach (Zip_FileEntry oldEntry in RecordList) {
                if (oldEntry.ChangeObject == null) {
                    oldEntry.Invalidate();
                }
            }
            RecordList.Clear();

            DisposeSources(mEditList);

            // Copy updates back into original objects, so application can keep using them.
            foreach (Zip_FileEntry newEntry in mEditList) {
                newEntry.CommitChange();
            }

            // Swap lists.
            List<Zip_FileEntry> tmp = RecordList;
            RecordList = mEditList;
            mEditList = tmp;

            mIsTransactionOpen = false;
        }

        // IArchive
        public void CancelTransaction() {
            if (!mIsTransactionOpen) {
                return;
            }
            DisposeSources(mEditList);
            mEditList.Clear();
            foreach (Zip_FileEntry entry in RecordList) {
                entry.ChangeObject = null;
            }
            mNewComment = string.Empty;
            mIsTransactionOpen = false;
        }

        private void DisposeSources(List<Zip_FileEntry> entryList) {
            foreach (Zip_FileEntry entry in entryList) {
                entry.DisposeSources();
            }
        }

        // IArchive
        public IFileEntry CreateRecord() {
            if (!mIsTransactionOpen) {
                throw new InvalidOperationException("Must start transaction first");
            }
            Zip_FileEntry newEntry = Zip_FileEntry.CreateNew(this);
            newEntry.ChangeObject = newEntry;       // changes are made directly to this object
            newEntry.OrigObject = newEntry;
            newEntry.CreateWhen = newEntry.ModWhen = DateTime.Now;
            mEditList.Add(newEntry);
            return newEntry;
        }

        // IArchive
        public void DeleteRecord(IFileEntry ientry) {
            CheckModAccess(ientry);
            Zip_FileEntry entry = (Zip_FileEntry)ientry;
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
            if (part != FilePart.DataFork) {
                throw new ArgumentException("Invalid part: " + part);
            }
            //if (ientry.IsDirectory) {
            //    throw new ArgumentException("Can't add data to directory");
            //}
            ((Zip_FileEntry)ientry).AddPart(partSource, compressFmt);
        }

        // IArchive
        public void DeletePart(IFileEntry ientry, FilePart part) {
            CheckModAccess(ientry);
            ((Zip_FileEntry)ientry).DeletePart();
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
            Zip_FileEntry? entry = ientry as Zip_FileEntry;
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
        /// Adjusts a filename to be compatible with the archive format.  Call this on individual
        /// pathname components.
        /// </summary>
        /// <param name="fileName">Filename (not a pathname).</param>
        /// <returns>Adjusted filename.</returns>
        public static string AdjustFileName(string fileName) {
            // The only thing we don't allow in a filename is '/', since that could confuse us
            // into treating it as two parts of the path.  Otherwise, we automatically switch to
            // UTF-8 filename encoding if CP437 doesn't cover it.
            return fileName.Replace(Zip_FileEntry.SEP_CHAR, '?');
        }

        #region Mac ZIP Utilities

        /// <summary>
        /// Generates the __MACOSX/ AppleDouble filename.
        /// </summary>
        /// <param name="fullName">Zip_FileEntry.FullPathName for the entry.</param>
        /// <returns>Modified filename, or an empty string if it couldn't be generated.</returns>
        public static string GenerateMacZipName(string fullName) {
            return GenerateMacZipName(fullName, Zip_FileEntry.SEP_CHAR);
        }

        /// <summary>
        /// Generates the __MACOSX/ AppleDouble filename.
        /// </summary>
        /// <param name="fullName">Full pathname for the entry.</param>
        /// <param name="dirSep">Directory separator character used in the pathname.</param>
        /// <returns>Modified filename, or an empty string if it couldn't be generated.</returns>
        public static string GenerateMacZipName(string fullName, char dirSep) {
            StringBuilder sb = new StringBuilder();
            sb.Append(MAC_ZIP_RSRC_PREFIX);
            sb.Append(dirSep);
            // Separate the filename.
            int lastSlash = fullName.LastIndexOf(dirSep);
            if (lastSlash == fullName.Length - 1) {
                // Slash is at end; unexpected for a non-directory file.  Reject.
                return string.Empty;
            }
            if (lastSlash >= 0) {
                // Copy the directory name portion.
                sb.Append(fullName.Substring(0, lastSlash));
                sb.Append(dirSep);
            }
            // Add the "._" and copy the filename.
            sb.Append(AppleSingle.ADF_PREFIX);
            sb.Append(fullName.Substring(lastSlash + 1));
            return sb.ToString();
        }

        /// <summary>
        /// Looks for a MacZip header that pairs with the specified entry.
        /// </summary>
        /// <param name="arc">Archive object.  It's okay for this not to be ZIP.</param>
        /// <param name="entry">Primary entry.</param>
        /// <param name="adfEntry">Result: AppleDouble header file entry, or NO_ENTRY if one
        ///   wasn't found.</param>
        /// <returns>True if an entry was found.</returns>
        public static bool HasMacZipHeader(IArchive arc, IFileEntry entry,
                out IFileEntry adfEntry) {
            adfEntry = IFileEntry.NO_ENTRY;
            if (arc is not Zip) {
                return false;
            }
            if (entry.IsMacZipHeader()) {
                // This is the header entry for a different record.
                return false;
            }
            // Generate header entry name and do a lookup.
            string macZipName = GenerateMacZipName(entry.FullPathName);
            if (string.IsNullOrEmpty(macZipName)) {
                return false;
            }
            return arc.TryFindFileEntry(macZipName, out adfEntry);
        }

        #endregion Mac ZIP Utilities
    }
}
