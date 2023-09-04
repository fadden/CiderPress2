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
using static DiskArc.Defs;

namespace DiskArc.Arc {
    /// <summary>
    /// NuFX archive handling.
    /// </summary>
    /// <remarks>
    /// <para>Missing features:</para>
    /// <list type="bullet">
    ///   <item>Add or remove Binary II and self-extracting archive wrappers.</item>
    /// </list>
    /// </remarks>
    public class NuFX : IArchiveExt {
        public const int MASTER_HEADER_LEN = 48;
        private const int NEW_MASTER_VERSION = 2;

        private const int MAX_JUNK_SKIP = 1024;

        // Master header signature: "NuFile" in alternating low/high ASCII.
        private static readonly byte[] MASTER_SIG =
            new byte[] { 0x4e, 0xf5, 0x46, 0xe9, 0x6c, 0xe5 };

        // GSHK self-extracting archive constants.  None of this is documented.
        public const int SELF_EXT_HDR_LEN = 12005;              // length of data before NuFX
        private const int SELF_EXT_SIG_OFF = 55;                // offset to SEGNAME, + 1
        private const string SELF_EXT_SIG = "sea_file_110v1";   // OMF segment name

        private const string FILENAME_RULES = "System-specific partial pathname.";
        private static readonly ArcCharacteristics sCharacteristics = new ArcCharacteristics(
            name: "NuFX (ShrinkIt)",
            canWrite: true,
            hasSingleEntry: false,
            hasResourceForks: true,
            hasDiskImages: true,
            hasArchiveComment: false,
            hasRecordComments: true,
            defaultDirSep: NuFX_FileEntry.DEFAULT_FSSEP,
            fnSyntax: FILENAME_RULES,
            tsStart: TimeStamp.IIGS_MIN_TIMESTAMP,
            tsEnd: TimeStamp.IIGS_MAX_TIMESTAMP
        );

        //
        // IArchive interfaces.
        //

        public ArcCharacteristics Characteristics => sCharacteristics;
        public static ArcCharacteristics SCharacteristics => sCharacteristics;

        public Stream? DataStream { get; private set; }

        public bool IsReadOnly => IsDubious;
        public bool IsDubious { get; internal set; }

        public Notes Notes { get; } = new Notes();

        // Tracking in-place vs. rebuild is annoying and of dubious benefit.
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

        internal List<NuFX_FileEntry> RecordList { get; private set; } = new List<NuFX_FileEntry>();

        // Temporary buffers, used for stream copying and compression, allocated on first use.
        internal byte[]? TmpBuf1 { get; private set; }
        internal byte[]? TmpBuf2 { get; private set; }

        /// <summary>
        /// True if a transaction is open.
        /// </summary>
        private bool mIsTransactionOpen;

        /// <summary>
        /// Modified entry list.  This will become the new list of entries when the transaction is
        /// committed.
        /// </summary>
        private List<NuFX_FileEntry> mEditList = new List<NuFX_FileEntry>();

        /// <summary>
        /// True if this archive has a Binary II wrapper.
        /// </summary>
        private bool mHasBinary2Wrapper;

        /// <summary>
        /// True if this archive has a GS/ShrinkIt self-extracting archive wrapper.
        /// </summary>
        private bool mHasSelfExtWrapper;

        /// <summary>
        /// Start offset of NuFX archive.  Usually zero, but may be offset by BXY/SEA headers
        /// and junk.
        /// </summary>
        private long mNuFXStartOffset;

        // Values from master header.
        private uint mTotalRecords;
        private ulong mArchiveCreateWhen;
        private ulong mArchiveModWhen;

        /// <summary>
        /// Open stream tracker.
        /// </summary>
        private OpenStreamTracker mStreamTracker = new OpenStreamTracker();

        /// <summary>
        /// Tests a stream to see if it contains a NuFX archive.
        /// </summary>
        /// <param name="stream">Stream to test.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <returns>True if this looks like NuFX.</returns>
        public static bool TestKind(Stream stream, AppHook appHook) {
            stream.Position = 0;
            return AnalyzeFile(stream, appHook, out bool unused1, out bool unused2,
                out long unused3);
        }

        /// <summary>
        /// Private constructor.
        /// </summary>
        /// <param name="stream">Data stream if existing file, null if new archive.</param>
        /// <param name="appHook">Application hook reference.</param>
        private NuFX(Stream? stream, AppHook appHook) {
            DataStream = stream;
            AppHook = appHook;
        }

        /// <summary>
        /// Creates an empty archive object.
        /// </summary>
        /// <param name="appHook">Application hook reference.</param>
        /// <returns>Archive object.</returns>
        public static NuFX CreateArchive(AppHook appHook) {
            NuFX newArc = new NuFX(null, appHook);
            newArc.mArchiveCreateWhen = TimeStamp.ConvertDateTime_GS(DateTime.Now);
            return newArc;
        }

        /// <summary>
        /// Opens an existing archive.
        /// </summary>
        /// <param name="stream">Archive data stream.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <returns>Archive instance.</returns>
        /// <exception cref="NotSupportedException">Data stream is not a NuFX archive.</exception>
        public static NuFX OpenArchive(Stream stream, AppHook appHook) {
            stream.Position = 0;
            if (!AnalyzeFile(stream, appHook, out bool hasBinary2, out bool hasSelfExt,
                    out long startOffset)) {
                throw new NotSupportedException("Incompatible data stream");
            }
            NuFX archive = new NuFX(stream, appHook);
            archive.mHasBinary2Wrapper = hasBinary2;
            archive.mHasSelfExtWrapper = hasSelfExt;
            archive.mNuFXStartOffset = startOffset;
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

        /// <summary>
        /// Analyzes a data stream to see if it contains a NuFX archive.
        /// </summary>
        /// <remarks>
        /// <para>In addition to the basic case of a simple ".shk" file, we also need to
        /// look for archives with a Binary II header (".bxy") and/or GSHK self-extracting
        /// archives (".sea"/".bse").  We also want to skip past "junk", like MacBinary headers,
        /// that are found at the front of some archives.</para>
        /// </remarks>
        /// <param name="stream">Archive data stream.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <param name="hasBinary2">Result: file starts with a Binary II wrapper.</param>
        /// <param name="hasSelfExt">Result: archive is preceded by a GSHK self-extracting
        ///   archive header.</param>
        /// <param name="startOffset">Result: start offset of archive data.</param>
        /// <returns>True if a NuFX archive is in here somewhere.</returns>
        private static bool AnalyzeFile(Stream stream, AppHook appHook,
                out bool hasBinary2, out bool hasSelfExt, out long startOffset) {
            hasBinary2 = hasSelfExt = false;
            startOffset = 0;

            // Start by checking for a plain archive.
            if (TestNuFXSig(stream, 0)) {
                return true;
            }

            // Look for a Binary II header (.bxy).
            if (Binary2.TestKind(stream, appHook)) {
                // It's important to distinguish between a Binary II wrapper, and a Binary II
                // file that just happens to start with a NuFX archive.  We can test this by
                // checking the "files to follow" byte in the Binary II header.
                stream.Position = Binary2.FILES_TO_FOLLOW_OFFSET;
                int count = stream.ReadByte();
                if (count != 0) {
                    // This looks like a Binary II archive.  If we treat it as NuFX we'll trash
                    // the other entries when we reconstruct the archive.
                    appHook.LogI("Tested NuFX stream is actually multi-file Binary II");
                    return false;
                }

                // See if NuFX follows immediately.
                if (TestNuFXSig(stream, Binary2.CHUNK_LEN)) {
                    startOffset = Binary2.CHUNK_LEN;
                    hasBinary2 = true;
                    return true;
                }

                // Nope, check for self-extraction header (.bse).
                if (TestSelfExtractor(stream, Binary2.CHUNK_LEN)) {
                    if (TestNuFXSig(stream, Binary2.CHUNK_LEN + SELF_EXT_HDR_LEN)) {
                        startOffset = Binary2.CHUNK_LEN + SELF_EXT_HDR_LEN;
                        hasBinary2 = hasSelfExt = true;
                        return true;
                    }
                }

                // Probably a Binary II file.
            } else {
                // Check for a self-extraction header (.sea).
                if (TestSelfExtractor(stream, 0)) {
                    if (TestNuFXSig(stream, SELF_EXT_HDR_LEN)) {
                        startOffset = SELF_EXT_HDR_LEN;
                        hasSelfExt = true;
                        return true;
                    }
                }

                // Seek past arbitrary junk.  Useful for archives that were gifted with
                // MacBinary or HTTP headers.  If a Binary II wrapper is present, it will also
                // be skipped over.
                for (int i = 1; i < MAX_JUNK_SKIP; i++) {
                    if (TestNuFXSig(stream, i)) {
                        Debug.WriteLine("Found NuFX header after " + i + " bytes of junk");
                        startOffset = i;
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TestNuFXSig(Stream stream, long offset) {
            if (offset > stream.Length - MASTER_HEADER_LEN) {
                // File isn't long enough to hold an archive starting from this offset.
                return false;
            }
            stream.Position = offset;
            for (int i = 0; i < MASTER_SIG.Length; i++) {
                if (stream.ReadByte() != MASTER_SIG[i]) {
                    return false;
                }
            }
            return true;
        }

        private static bool TestSelfExtractor(Stream stream, long offset) {
            if (offset > stream.Length - (SELF_EXT_HDR_LEN + MASTER_HEADER_LEN)) {
                // File isn't long enough to hold an archive starting from this offset.
                return false;
            }

            stream.Position = offset + SELF_EXT_SIG_OFF;
            for (int i = 0; i < SELF_EXT_SIG.Length; i++) {
                if (stream.ReadByte() != (byte)SELF_EXT_SIG[i]) {
                    return false;
                }
            }
            return true;
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
                    AppHook.LogW("NuFX disposed while " + mStreamTracker.Count +
                        " streams are open");
                    mStreamTracker.CloseAll();
                }
            }
            if (mIsTransactionOpen) {
                AppHook.LogW("Disposing of NuFX while transaction open");
                CancelTransaction();
            }
        }

        /// <summary>
        /// Scans the records out of the archive.
        /// </summary>
        /// <remarks>
        /// <para>This should not throw an exception on bad data.  Damaged records should be
        /// flagged instead.</para>
        /// <para>When this completes, we should have all record and thread info loaded into
        /// memory.  The only thing we need from the original archive is the actual data,
        /// which is identified by absolute file position and length.</para>
        /// </remarks>
        private void Scan() {
            Debug.Assert(DataStream != null);

            if (mNuFXStartOffset != 0 && !mHasBinary2Wrapper && !mHasSelfExtWrapper) {
                Notes.AddI("Skipped " + mNuFXStartOffset + " bytes of leading junk");
            }

            // Read the full header into memory.
            DataStream.Position = mNuFXStartOffset;
            byte[] buf = new byte[MASTER_HEADER_LEN];
            try {
                DataStream.ReadExactly(buf, 0, MASTER_HEADER_LEN);
            } catch (EndOfStreamException) {
                throw new IOException("Failed to read master header");
            }

            // Check the CRC.
            ushort calcCrc = CRC16.XMODEM_OnBuffer(0x0000, buf, 8, MASTER_HEADER_LEN - 8);
            ushort fileCrc = RawData.GetU16LE(buf, 6);
            if (calcCrc != fileCrc) {
                // Not a particularly big deal, but if this simple CRC is wrong then it's likely
                // other things are trashed as well.
                Notes.AddW("Master header CRC did not match");
                IsDubious = true;
            }

            // The only important field in the master header is the number of records.
            // The date/time stamps aren't very useful, and the master_eof is only good for
            // doing a trivial file truncation test.
            mTotalRecords = RawData.GetU32LE(buf, 0x08);
            mArchiveCreateWhen = RawData.GetU64LE(buf, 0x0c);
            mArchiveModWhen = RawData.GetU64LE(buf, 0x14);
            ushort masterVersion = RawData.GetU16LE(buf, 0x1c);
            if (masterVersion > 0) {
                uint masterEof = RawData.GetU32LE(buf, 0x26);
                long diff = DataStream.Length - mNuFXStartOffset;
                if (masterEof == 0) {
                    Notes.AddI("Master header EOF is zero");
                } else if (diff < masterEof) {
                    // If the archive was actually truncated, we'll figure it out when processing
                    // the records.  Still, log a warning.
                    Notes.AddW("Master header EOF indicates archive was truncated by " +
                        diff + " bytes");
                } else if (diff == masterEof + 1 && mHasSelfExtWrapper) {
                    // SEA adds one byte to the end.
                } else if (diff > masterEof) {
                    // Extra stuff at end.  A small amount is expected if this is wrapped in
                    // Binary II.
                    if (!mHasBinary2Wrapper || (DataStream.Length % Binary2.CHUNK_LEN) != 0) {
                        Notes.AddI("Master header EOF (" + masterEof + ") indicates there are " +
                            (diff - masterEof) + " bytes of junk at the end of the file");
                    }
                }
            }

            // Read records, creating a file entry object for each.
            for (int i = 0; i < mTotalRecords; i++) {
                NuFX_FileEntry? newEntry = NuFX_FileEntry.ReadNextRecord(this);
                if (newEntry == null) {
                    IsDubious = true;
                    Notes.AddE("Read " + i + " of " + mTotalRecords + " records");
                    break;
                }
                RecordList.Add(newEntry);
            }
        }

        // IArchive
        public ArcReadStream OpenPart(IFileEntry ientry, FilePart part) {
            if (mIsTransactionOpen) {
                throw new InvalidOperationException("Cannot open parts while transaction is open");
            }
            NuFX_FileEntry entry = (NuFX_FileEntry)ientry;
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
            foreach (NuFX_FileEntry entry in RecordList) {
                // Create an object to receive changes.
                NuFX_FileEntry newEntry = NuFX_FileEntry.CreateChangeObject(entry);
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

            // Allocate temporary buffers for stream data if we haven't yet.
            if (TmpBuf1 == null) {
                const int size = 32768;
                Debug.Assert(size > SELF_EXT_HDR_LEN && size > Binary2.CHUNK_LEN);
                TmpBuf1 = new byte[size];
                TmpBuf2 = new byte[size];
            }

            // Do some quick validity checks on entries in the "edit" list before we start
            // crunching through files.
            foreach (NuFX_FileEntry oentry in mEditList) {
                NuFX_FileEntry entry = oentry.ChangeObject!;
                if (entry.FileName.Length == 0) {
                    throw new InvalidOperationException("Record has an empty filename");
                }
                if (entry.PartCount == 0) {
                    throw new InvalidOperationException("Record is empty: " + entry.FileName);
                }
                if (entry.PartCount > NuFX_FileEntry.BIG_THREAD_COUNT) {
                    throw new InvalidOperationException("Too many parts defined (" +
                        entry.PartCount + "): " + entry.FileName);
                }
                if (!entry.HasEditPart(FilePart.DiskImage) &&
                        !entry.HasEditPart(FilePart.DataFork) &&
                        !entry.HasEditPart(FilePart.RsrcFork)) {
                    // We created Miranda parts for missing items when opening, so if nothing is
                    // present something was deleted and not replaced, or never created.  (This
                    // would be wrong for directory-creation control threads, if such things were
                    // actually used.)
                    throw new InvalidOperationException(
                        "Record has no data items in it: " + entry.FileName);
                }
            }

            // Leave room for the various headers.  We don't attempt to preserve "junk".
            outputStream.Position = 0;
            outputStream.SetLength(0);
            if (mHasBinary2Wrapper) {
                outputStream.Position += Binary2.CHUNK_LEN;
            }
            if (mHasSelfExtWrapper) {
                outputStream.Position += SELF_EXT_HDR_LEN;
            }
            long nufxStartPosition = outputStream.Position;
            outputStream.Position += MASTER_HEADER_LEN;

            try {
                // Walk through the list of change objects.  We generate a new v1 or v3 record
                // header based on the record version, and then output the threads.  Threads
                // may be copied whole from the original archive or generated from a source.
                //
                // It would be slightly more efficient to copy whole records when nothing has
                // changed, but the notion of "change" must encompass the bug fixes applied at
                // different points, e.g. providing missing data threads and fixing disk image
                // block sizes.  Simpler to just generate the record/thread headers.
                foreach (NuFX_FileEntry oentry in mEditList) {
                    NuFX_FileEntry centry = oentry.ChangeObject!;
                    centry.WriteRecord(DataStream, outputStream);
                }

                // Go back and write the master header.
                long length = outputStream.Position - nufxStartPosition;
                byte[] buf = new byte[MASTER_HEADER_LEN];
                Array.Copy(MASTER_SIG, buf, MASTER_SIG.Length);
                RawData.SetU32LE(buf, 0x08, (uint)mEditList.Count);
                int bufOffset = 0x0c;
                RawData.WriteU64LE(buf, ref bufOffset, mArchiveCreateWhen);
                mArchiveModWhen = TimeStamp.ConvertDateTime_GS(DateTime.Now);
                RawData.WriteU64LE(buf, ref bufOffset, mArchiveModWhen);
                RawData.SetU16LE(buf, 0x1c, NEW_MASTER_VERSION);
                RawData.SetU32LE(buf, 0x26, (uint)length);
                ushort crc = CRC16.XMODEM_OnBuffer(0x0000, buf, 8, MASTER_HEADER_LEN - 8);
                RawData.SetU16LE(buf, 0x06, crc);
                outputStream.Position = nufxStartPosition;
                outputStream.Write(buf, 0, MASTER_HEADER_LEN);

                // Transfer BXY/SEA wrappers, if present.
                TransferWrappers(outputStream, length);
                mNuFXStartOffset = nufxStartPosition;
            } catch {
                // Failed; truncate output stream so we don't have a partial archive sitting around.
                outputStream.Position = 0;
                outputStream.SetLength(0);
                throw;
            }

            // Success!  Switch to the new archive stream and swap record sets.  We don't own
            // the archive stream, so we don't dispose of it.

            // Switch to the new stream.
            DataStream = outputStream;
            DataStream.Flush();
            // Invalidate deleted records.
            foreach (NuFX_FileEntry oldEntry in RecordList) {
                if (oldEntry.ChangeObject == null) {
                    oldEntry.Invalidate();
                }
            }
            RecordList.Clear();

            DisposeSources(mEditList);

            // Copy updates back into original objects, so application can keep using them.
            foreach (NuFX_FileEntry entry in mEditList) {
                entry.CommitChange();
            }

            // Swap lists.
            List<NuFX_FileEntry> tmp = RecordList;
            RecordList = mEditList;
            mEditList = tmp;

            mIsTransactionOpen = false;
        }

        /// <summary>
        /// Transfers the BXY/SEA wrappers, if any, updating their contents to reflect the
        /// updated archive.
        /// </summary>
        /// <remarks>
        /// </remarks>
        /// <param name="outputStream">Stream that is receiving the output.</param>
        /// <param name="nufxLength">Length of the updated NuFX archive.</param>
        private void TransferWrappers(Stream outputStream, long nufxLength) {
            if (DataStream == null) {
                return;
            }

            // We don't look for headers when skipping junk, so if we've identified headers
            // then they must appear at the start of the file.
            outputStream.Position = 0;
            DataStream.Position = 0;

            if (mHasBinary2Wrapper) {
                // We wrap the full executable if this is BSE.
                long entryLength = nufxLength;
                if (mHasSelfExtWrapper) {
                    entryLength += SELF_EXT_HDR_LEN + 1;
                }

                // Read the original header.
                Debug.Assert(TmpBuf1 != null && TmpBuf1.Length >= Binary2.CHUNK_LEN);
                byte[] buf = TmpBuf1;
                try {
                    DataStream.ReadExactly(buf, 0, Binary2.CHUNK_LEN);
                } catch (EndOfStreamException) {
                    throw new IOException("Failed to read Binary II header from archive");
                }

                // Set the new length value.
                RawData.SetU24LE(buf, 0x14, (uint)(entryLength & 0x00ffffff));
                buf[0x74] = (byte)(entryLength >> 24);

                // Set approximate size in blocks for this file.  These values don't really
                // matter, but we might as well set them while we're here.
                uint blockCount = (uint)((entryLength + BLOCK_SIZE - 1) / BLOCK_SIZE);
                RawData.SetU16LE(buf, 0x08, (ushort)blockCount);
                RawData.SetU16LE(buf, 0x72, (ushort)(blockCount >> 16));
                // Set approximate size in blocks for entire archive.
                RawData.SetU32LE(buf, 0x75, blockCount);

                // Save it.
                outputStream.Write(buf, 0, Binary2.CHUNK_LEN);
            }

            if (mHasSelfExtWrapper) {
                Debug.Assert(TmpBuf2 != null && TmpBuf2.Length >= SELF_EXT_HDR_LEN);
                byte[] buf = TmpBuf2;
                try {
                    DataStream.ReadExactly(buf, 0, SELF_EXT_HDR_LEN);
                } catch (EndOfStreamException) {
                    throw new IOException("Failed to read SEA header from archive");
                }

                // Adjust the length values in the OMF header and LCONST opcode.
                const int OMF_HDR_LEN = 68;
                RawData.SetU32LE(buf, 0x2ea2, (uint)(nufxLength + OMF_HDR_LEN));
                RawData.SetU32LE(buf, 0x2eaa, (uint)nufxLength);
                RawData.SetU32LE(buf, 0x2ee1, (uint)nufxLength);

                outputStream.Write(buf, 0, SELF_EXT_HDR_LEN);

                // Add an OMF "END" opcode to the end of the file.
                //
                // Some strange behavior is documented in the comment for the
                // Nu_AdjustWrapperPadding() function in NufxLib with regard to the extra
                // byte and BXY padding.  Not emulated here.
                outputStream.Position = outputStream.Length;
                outputStream.WriteByte(0x00);
            }

            // Binary II archives should be a multiple of 128 bytes, so we may need to add
            // some padding to the end.
            if (mHasBinary2Wrapper) {
                outputStream.Position = outputStream.Length;
                int partial = (int)(outputStream.Position % Binary2.CHUNK_LEN);
                if (partial != 0) {
                    for (int i = partial; i < Binary2.CHUNK_LEN; i++) {
                        outputStream.WriteByte(0x00);
                    }
                }
            }
        }

        // IArchive
        public void CancelTransaction() {
            if (!mIsTransactionOpen) {
                return;
            }
            DisposeSources(mEditList);
            mEditList.Clear();
            foreach (NuFX_FileEntry entry in RecordList) {
                entry.ChangeObject = null;
            }
            mIsTransactionOpen = false;
        }

        private void DisposeSources(List<NuFX_FileEntry> entryList) {
            foreach (NuFX_FileEntry entry in entryList) {
                entry.DisposeSources();
            }
        }

        // IArchive
        public IFileEntry CreateRecord() {
            if (!mIsTransactionOpen) {
                throw new InvalidOperationException("Must start transaction first");
            }
            NuFX_FileEntry newEntry = NuFX_FileEntry.CreateNew(this);
            newEntry.ChangeObject = newEntry;       // changes are made directly to this object
            newEntry.OrigObject = newEntry;
            mEditList.Add(newEntry);
            return newEntry;
        }

        // IArchive
        public void DeleteRecord(IFileEntry ientry) {
            CheckModAccess(ientry);
            NuFX_FileEntry entry = (NuFX_FileEntry)ientry;
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
            ((NuFX_FileEntry)ientry).AddPart(part, partSource, compressFmt);
        }

        // IArchive
        public void DeletePart(IFileEntry ientry, FilePart part) {
            CheckModAccess(ientry);
            ((NuFX_FileEntry)ientry).DeletePart(part);
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
                // TODO: don't check this if we're trying to delete the record?
                throw new ArgumentException("Record '" + ientry.FileName +
                    "' is too damaged to access");
            }
            NuFX_FileEntry? entry = ientry as NuFX_FileEntry;
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
            // This is called while adding records, so it's reasonable to assume they're using
            // the default fssep char.  Wouldn't be terrible to replace all of [/:\].
            //
            // We're limited to Mac OS Roman, so replace anything that can't be represented.
            char[] chars = fileName.ToCharArray();
            for (int i = 0; i < chars.Length; i++) {
                if (chars[i] == NuFX_FileEntry.DEFAULT_FSSEP ||
                        !MacChar.IsCharValid(chars[i], MacChar.Encoding.RomanShowCtrl)) {
                    chars[i] = '?';
                }
            }
            return new string(chars);
        }
    }
}
