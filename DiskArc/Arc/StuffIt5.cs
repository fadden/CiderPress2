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
    /// StuffIt v5 archive handling.
    /// </summary>
    public class StuffIt5 : IArchiveExt {
        private const string FILENAME_RULES = "Partial paths separated by ':'.";
        private static readonly ArcCharacteristics sCharacteristics = new ArcCharacteristics(
            name: "StuffIt5",
            canWrite: false,
            hasSingleEntry: false,
            hasResourceForks: true,
            hasDiskImages: false,
            hasArchiveComment: true,
            hasRecordComments: true,
            defaultDirSep: StuffIt5_FileEntry.SEP_CHAR,
            fnSyntax: FILENAME_RULES,
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
            public const int FIXED_LEN = 100;

            // Archive header signature.  There's more, but this should do.
            public static readonly byte[] SIGNATURE = Encoding.ASCII.GetBytes("StuffIt (c)1997-");
            public const int HEADER_TEXT_LEN = 80;

            public const byte FLAG_14 = 0x10;
            public const byte FLAG_COMMENT = 0x20;
            public const byte FLAG_STRUCT20 = 0x40;
            public const byte FLAG_ENCRYPTED = 0x80;

            public const int EXPECTED_VERSION = 5;


            // Fields we recognize.
            public byte[] HeaderText { get; set; } = new byte[HEADER_TEXT_LEN];
            public ushort Unknown1 { get; set; }
            public byte Version { get; set; }
            public byte Flags { get; set; }
            public uint FileLength { get; set; }
            public uint Unknown2 { get; set; }
            public ushort NumRootEntries { get; set; }
            public uint FirstOffset { get; set; }
            public ushort HeaderCRC { get; set; }
            public byte[] CommentText { get; set; } = RawData.EMPTY_BYTE_ARRAY;

            public string Comment { get; set; } = string.Empty;
            public long TotalHeaderLen { get; set; }

            public ArchiveHeader() { }

            /// <summary>
            /// Unpacks the archive header from the stream.  Performs basic validation checks.
            /// </summary>
            public bool Load(Stream stream) {
                try {
                    long startPosn = stream.Position;

                    if (stream.Length < FIXED_LEN) {
                        return false;
                    }
                    byte[] buf = new byte[FIXED_LEN];
                    stream.ReadExactly(buf);

                    //
                    // Start by reading the fixed portion.
                    //
                    int offset = 0;
                    for (int i = 0; i < SIGNATURE.Length; i++) {
                        if (buf[offset + i] != SIGNATURE[i]) {
                            return false;
                        }
                    }
                    Array.Copy(buf, offset, HeaderText, 0, HEADER_TEXT_LEN);
                    offset += HEADER_TEXT_LEN;
                    Unknown1 = RawData.ReadU16BE(buf, ref offset);
                    Version = RawData.ReadU8(buf, ref offset);
                    Flags = RawData.ReadU8(buf, ref offset);
                    FileLength = RawData.ReadU32BE(buf, ref offset);
                    Unknown2 = RawData.ReadU32BE(buf, ref offset);
                    NumRootEntries = RawData.ReadU16BE(buf, ref offset);
                    FirstOffset = RawData.ReadU32BE(buf, ref offset);
                    HeaderCRC = RawData.ReadU16BE(buf, ref offset);

                    if (Version != EXPECTED_VERSION) {
                        return false;
                    }
                    if (FileLength < FIXED_LEN) {
                        return false;
                    }
                    Debug.Assert(offset == FIXED_LEN);

                    //
                    // Now handle the optional parts.
                    //
                    if ((Flags & FLAG_14) != 0) {
                        stream.Seek(14, SeekOrigin.Current);
                    }
                    int commentLen = 0, commentPadLen = 0;
                    if ((Flags & FLAG_COMMENT) != 0) {
                        // Get the values for the comment length and comment buffer size.  The
                        // comment body comes later.
                        stream.ReadExactly(buf, 0, 4);
                        commentLen = RawData.GetU16BE(buf, 0);
                        commentPadLen = RawData.GetU16BE(buf, 2);
                    }
                    if ((Flags & FLAG_ENCRYPTED) != 0) {
                        int hashSize = stream.ReadByte();
                        if (hashSize < 0) {
                            return false;
                        }
                        stream.Seek(hashSize, SeekOrigin.Current);
                    }
                    if ((Flags & FLAG_STRUCT20) != 0) {
                        stream.ReadExactly(buf, 0, 2);
                        int count = RawData.GetU16BE(buf, 0);
                        stream.Seek(count * 20, SeekOrigin.Current);
                    }
                    if ((Flags & FLAG_COMMENT) != 0) {
                        // Read the comment.
                        CommentText = new byte[commentLen];
                        stream.ReadExactly(CommentText);
                        stream.Seek(commentPadLen, SeekOrigin.Current);

                        Comment = MacChar.MacToUnicode(CommentText, 0, commentLen,
                            MacChar.Encoding.Roman);
                        Debug.WriteLine("Comment: '" + Comment + "'");
                    }

                    long endPosn = stream.Position;
                    if (endPosn - startPosn != FirstOffset) {
                        Debug.WriteLine("Warning: StuffIt5 header consumed " +
                            (endPosn - startPosn) + " bytes, expected " + FirstOffset);
                    }

                    TotalHeaderLen = endPosn - startPosn;
                } catch (IOException ex) {
                    Debug.WriteLine("Failed reading StuffIt 5 header: " + ex);
                    return false;
                }
                return true;
            }
        }
        private ArchiveHeader mArchiveHeader;

        public ArcCharacteristics Characteristics => sCharacteristics;
        public static ArcCharacteristics SCharacteristics => sCharacteristics;

        public Stream? DataStream { get; internal set; }

        public bool IsValid { get; private set; } = true;
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

        internal List<StuffIt5_FileEntry> RecordList { get; private set; } =
            new List<StuffIt5_FileEntry>();

        /// <summary>
        /// True if a MacBinary wrapper is present.
        /// </summary>
        private bool mHasMacBinary;

        /// <summary>
        /// Open stream tracker.
        /// </summary>
        private OpenStreamTracker mStreamTracker = new OpenStreamTracker();


        /// <summary>
        /// Tests a stream to see if it contains a StuffIt5 archive.
        /// </summary>
        /// <param name="stream">Stream to test.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <returns>True if this looks like StuffIt5.</returns>
        public static bool TestKind(Stream stream, AppHook appHook) {
            return AnalyzeFile(stream, appHook, out bool unused1, out ArchiveHeader? unused2);
        }

        /// <summary>
        /// Private constructor.
        /// </summary>
        /// <param name="stream">Data stream if existing file, null if new archive.</param>
        /// <param name="appHook">Application hook reference.</param>
        private StuffIt5(Stream? stream, AppHook appHook, ArchiveHeader header) {
            DataStream = stream;
            AppHook = appHook;
            mArchiveHeader = header;
        }

        /// <summary>
        /// Creates an empty archive object.
        /// </summary>
        /// <param name="appHook">Application hook reference.</param>
        /// <returns>Archive object.</returns>
        public static StuffIt5 CreateArchive(AppHook appHook) {
            return new StuffIt5(null, appHook, new ArchiveHeader());
        }

        /// <summary>
        /// Opens an existing archive.
        /// </summary>
        /// <param name="stream">Archive data stream.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <returns>Archive instance.</returns>
        /// <exception cref="NotSupportedException">Stream is not a StuffIt archive.</exception>
        public static StuffIt5 OpenArchive(Stream stream, AppHook appHook) {
            if (!AnalyzeFile(stream, appHook, out bool hasMacBinary, out ArchiveHeader? header)) {
                throw new NotSupportedException("Incompatible data stream");
            }
            StuffIt5 archive = new StuffIt5(stream, appHook, header);
            archive.mHasMacBinary = hasMacBinary;
            long headerPosn = stream.Position;      // should be positioned just past archive header
            long streamLen;
            if (hasMacBinary) {
                // We know that MacBinary headers are 128 bytes, and the data fork starts
                // immediately, so there's no need to formally treat it as archive-in-archive.
                // Use the MacBinary length in case there's extra junk at the end of the file.
                stream.Position = MacBinary.DATA_FORK_LEN_OFFSET;
                streamLen = RawData.ReadU32BE(stream, out bool ok);
                if (!ok) {
                    // Not expected.
                    throw new NotSupportedException("Failed reading data fork len");
                }
                stream.Position = headerPosn;
            } else {
                streamLen = stream.Length - archive.mArchiveHeader.TotalHeaderLen;
            }
            archive.Scan(streamLen);
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
                    AppHook.LogW("StuffIt5 disposed while " + mStreamTracker.Count +
                        " streams are open");
                    mStreamTracker.CloseAll();
                }
            }
            //if (mIsTransactionOpen) {
            //    AppHook.LogW("Disposing of StuffIt5 while transaction open");
            //    CancelTransaction();
            //}
            IsValid = false;
        }

        /// <summary>
        /// Analyzes a data stream to see if it contains a StuffIt5 archive.
        /// </summary>
        /// <param name="stream">Archive data stream.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <param name="hasMacBinary">Result: true if file has MacBinary wrapper.</param>
        /// <param name="header">Result: archive header record.</param>
        /// <returns>True if this appears to be a StuffIt5 archive.</returns>
        private static bool AnalyzeFile(Stream stream, AppHook appHook, out bool hasMacBinary,
                [NotNullWhen(true)] out ArchiveHeader? header) {
            hasMacBinary = false;
            if (stream.Length < ArchiveHeader.FIXED_LEN) {
                header = null;
                return false;
            }

            stream.Position = 0;
            if (TestHeader(stream, out header)) {
                return true;
            }
            stream.Position = MacBinary.HEADER_LEN;
            if (TestHeader(stream, out header)) {
                hasMacBinary = true;
                return true;
            }
            return false;
        }

        private static bool TestHeader(Stream stream,
                [NotNullWhen(true)] out ArchiveHeader? header) {
            // Read the header from the current stream position, and validate it.
            header = new ArchiveHeader();
            if (!header.Load(stream)) {
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
        /// <param name="remainingLen">Bytes remaining in stream (equal to length of archive minus
        ///   the length of the archive header chunk).</param>
        private void Scan(long remainingLen) {
            Debug.Assert(DataStream != null);
            Debug.Assert(remainingLen >= 0);
            long startPosn = DataStream.Position;
            long endPosn = startPosn + remainingLen;

            Dictionary<long, StuffIt5_FileEntry> dirs = new Dictionary<long, StuffIt5_FileEntry>();

            // The archive header lists the number of entries at the root level.  To find all
            // entries, we want to update the count with the sub-folder counts.
            int entryCount = mArchiveHeader.NumRootEntries;
            while (entryCount > 0) {
                //Notes.AddI("count=" + entryCount + " posn=$" + DataStream.Position.ToString("X"));
                StuffIt5_FileEntry? newEntry = StuffIt5_FileEntry.ReadNextRecord(this);
                if (newEntry == null || DataStream.Position > endPosn) {
                    IsDubious = true;
                    break;
                }
                //Notes.AddI("  got " + newEntry);

                if (!newEntry.IsIgnorable) {
                    RecordList.Add(newEntry);

                    if (newEntry.IsDirectory) {
                        dirs.Add(newEntry.HeaderOffset, newEntry);
                        // Add contained folder count.
                        //
                        // We +1 because we anticipate finding an endFolder record.  If for
                        // some reason the archive doesn't have those, we'll try to read too
                        // much and complain about it, but everything will still work.
                        // (We should be able to test the next-entry offset?)
                        entryCount += newEntry.FolderEntryCount + 1;
                    }
                }
                entryCount--;
            }
            //Notes.AddI("final posn=$" + DataStream.Position.ToString("X"));

            if (DataStream.Position != endPosn) {
                Notes.AddW("Consumed " + (DataStream.Position - startPosn) +
                    " bytes, but expected " + (endPosn - startPosn));
                // keep going
            }

            // Generate the directory prefix strings.  I don't know if directories must appear
            // before the entries they contain, so it's safest to do this after everything has
            // been loaded.
            foreach (StuffIt5_FileEntry entry in RecordList) {
                entry.GeneratePathPrefix(dirs);
            }
        }

        // IArchive
        public ArcReadStream OpenPart(IFileEntry ientry, FilePart part) {
            if (!IsValid) {
                throw new InvalidOperationException("Archive object is invalid");
            }
            if (ientry.IsDamaged) {
                throw new IOException("Entry is too damaged to open");
            }
            StuffIt5_FileEntry entry = (StuffIt5_FileEntry)ientry;
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
        public void DeleteRecord(IFileEntry ientry) {
            throw new InvalidOperationException("This archive format is read-only");
        }

        // IArchive
        public void AddPart(IFileEntry ientry, FilePart part, IPartSource partSource,
                CompressionFormat compressFmt) {
            throw new InvalidOperationException("This archive format is read-only");
        }

        // IArchive
        public void DeletePart(IFileEntry ientry, FilePart part) {
            throw new InvalidOperationException("This archive format is read-only");
        }

        // IArchive
        public string? DebugDump() { return null; }
    }
}
