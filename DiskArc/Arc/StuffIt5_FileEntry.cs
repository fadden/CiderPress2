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
using System.IO.Compression;
using System.Text;

using CommonUtil;
using DiskArc.Comp;
using static DiskArc.Defs;

namespace DiskArc.Arc {
    /// <summary>
    /// One record in a StuffIt v5 archive.
    /// </summary>
    /// <remarks>
    /// <para>This leans heavily on the XADStuffIt5Parser implementation.</para>
    /// </remarks>
    public class StuffIt5_FileEntry : IFileEntry {
        internal const char SEP_CHAR = ':';

        private const uint HEADER_SIGNATURE = 0xa5a5a5a5;
        private const byte FLAGS_RSRC_FORK = 0x10;
        private const byte FLAGS_ENCRYPTED = 0x20;
        private const byte FLAGS_DIRECTORY = 0x40;
        private const int KEY_LENGTH = 5;

        //
        // IFileEntry interfaces.
        //

        public bool IsValid { get { return Archive != null; } }
        public bool IsDubious { get; private set; }
        public bool IsDamaged { get; private set; }

        public bool IsDirectory => mIsFolder;

        public bool HasDataFork => true;
        public bool HasRsrcFork => mHasRsrcFork;
        public bool IsDiskImage => false;

        public IFileEntry ContainingDir => IFileEntry.NO_ENTRY;
        public int Count => 0;

        public string FileName {
            get => mPathPrefix + mFileName;
            set => throw new InvalidOperationException("Cannot modify object without transaction");
        }
        private string mFileName = string.Empty;
        private string mPathPrefix = string.Empty;

        public char DirectorySeparatorChar {
            get => SEP_CHAR;
            set { }
        }

        public byte[] RawFileName {
            get {
                byte[] result = new byte[mRawFileName[0]];
                Array.Copy(mRawFileName, result, result.Length);
                return result;
            }
            set => throw new InvalidOperationException("Cannot modify object without transaction");
        }

        public string FullPathName { get { return FileName; } }

        public bool HasProDOSTypes => false;

        public byte FileType {
            get => 0;
            set { }
        }
        public ushort AuxType {
            get => 0;
            set { }
        }

        public bool HasHFSTypes => true;

        public uint HFSFileType {
            get => mFileType;
            set => throw new InvalidOperationException("Cannot modify object without transaction");
        }
        public uint HFSCreator {
            get => mCreator;
            set => throw new InvalidOperationException("Cannot modify object without transaction");
        }

        public byte Access {
            get => FileAttribs.FILE_ACCESS_UNLOCKED;    // TODO: check flags?
            set => throw new InvalidOperationException("Cannot modify object without transaction");
        }
        public DateTime CreateWhen {
            get => TimeStamp.ConvertDateTime_HFS(mCreateWhen);
            set => throw new InvalidOperationException("Cannot modify object without transaction");
        }
        public DateTime ModWhen {
            get => TimeStamp.ConvertDateTime_HFS(mModWhen);
            set => throw new InvalidOperationException("Cannot modify object without transaction");
        }

        public long StorageSize { get { return mDataCompLen + mRsrcCompLen; } }

        public long DataLength => mDataUncompLen;

        public long RsrcLength => mRsrcUncompLen;

        public string Comment {
            get => MacChar.MacToUnicode(mRawComment, 0, mRawComment.Length, MacChar.Encoding.Roman);
            set { }
        }

        // Entries do not have children.
        private static readonly List<IFileEntry> sEmptyList = new List<IFileEntry>();
        public IEnumerator<IFileEntry> GetEnumerator() {
            return sEmptyList.GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() {
            return sEmptyList.GetEnumerator();
        }

        //
        // Implementation-specific fields.
        //

        /// <summary>
        /// Reference to archive object.
        /// </summary>
        internal StuffIt5 Archive { get; private set; }

        /// <summary>
        /// True if a change has been made that requires writing the record.
        /// </summary>
        /// <remarks>
        /// If not set, we can just copy the old record to the new archive stream.
        /// </remarks>
        internal bool IsDirty => false;

        /// <summary>
        /// True if we think this should be ignored.
        /// </summary>
        internal bool IsIgnorable { get; private set; }

        /// <summary>
        /// For folder entries, this is the number of entries contained within it.
        /// </summary>
        internal int FolderEntryCount { get; private set; }

        /// <summary>
        /// File offset of header.  Each record has a reference to the containing directory object
        /// (if any), so we can use this to find the directory parents for a given entry.
        /// </summary>
        internal long HeaderOffset => mHeaderOffset;

        //
        // Interesting fields from the file header.
        //
        private byte[] mRawFileName = RawData.EMPTY_BYTE_ARRAY;
        private uint mFileType;
        private uint mCreator;
        private bool mIsFolder;
        private uint mCreateWhen;
        private uint mModWhen;
        private uint mPrevEntry;
        private uint mNextEntry;
        private uint mDirOffset;
        private ushort mFinderFlags;
        private byte[] mRawComment = RawData.EMPTY_BYTE_ARRAY;

        private uint mDataUncompLen;
        private uint mDataCompLen;
        private byte mDataMethod;
        private ushort mDataCRC;

        private bool mHasRsrcFork;
        private uint mRsrcUncompLen;
        private uint mRsrcCompLen;
        private byte mRsrcMethod;
        private ushort mRsrcCRC;

        /// <summary>
        /// Offset of file header.
        /// </summary>
        private long mHeaderOffset;

        /// <summary>
        /// Offset to compressed data for data fork.
        /// </summary>
        private long mDataForkOffset;

        /// <summary>
        /// Offset to compressed data for rsrc fork.
        /// </summary>
        private long mRsrcForkOffset;


        /// <summary>
        /// Private constructor.
        /// </summary>
        private StuffIt5_FileEntry(StuffIt5 archive) {
            Archive = archive;
        }

        /// <summary>
        /// Creates a new, empty record.
        /// </summary>
        internal static StuffIt5_FileEntry CreateNew(StuffIt5 archive) {
            StuffIt5_FileEntry newEntry = new StuffIt5_FileEntry(archive);
            return newEntry;
        }

        /// <summary>
        /// Reads the next record from the StuffIt archive.
        /// </summary>
        internal static StuffIt5_FileEntry? ReadNextRecord(StuffIt5 archive) {
            StuffIt5_FileEntry entry = new StuffIt5_FileEntry(archive);
            if (!entry.ReadRecord()) {
                return null;
            }
            return entry;
        }

        /// <summary>
        /// Reads a record from the current position in the archive data stream.
        /// On return, the stream will be positioned at the start of the following record, or EOF.
        /// </summary>
        /// <returns>True on success.</returns>
        private bool ReadRecord() {
            const int FIXED_LEN = 46;
            try {
                //
                // Start with the fixed-size part.
                //
                Stream stream = Archive.DataStream!;
                mHeaderOffset = stream.Position;

                byte[] hdrBuf = new byte[FIXED_LEN];
                stream.ReadExactly(hdrBuf, 0, FIXED_LEN);

                int offset = 0;
                uint signature = RawData.ReadU32BE(hdrBuf, ref offset);
                if (signature != HEADER_SIGNATURE) {
                    Debug.WriteLine("Did not find expected header signature");
                    return false;
                }
                byte version = RawData.ReadU8(hdrBuf, ref offset);
                //Debug.WriteLine("Found StuffIt5 record version " + version);
                offset++;       // unknown
                ushort headerLen = RawData.ReadU16BE(hdrBuf, ref offset);
                offset++;       // unknown
                byte flags = RawData.ReadU8(hdrBuf, ref offset);
                mCreateWhen = RawData.ReadU32BE(hdrBuf, ref offset);
                mModWhen = RawData.ReadU32BE(hdrBuf, ref offset);
                mPrevEntry = RawData.ReadU32BE(hdrBuf, ref offset);
                mNextEntry = RawData.ReadU32BE(hdrBuf, ref offset);
                mDirOffset = RawData.ReadU32BE(hdrBuf, ref offset);
                // NOTE: stuffit-lib.rs XORs with 0xa5a5a5a5 when (archive_flags & 0x10) == 0
                ushort fileNameLen = RawData.ReadU16BE(hdrBuf, ref offset);
                ushort headerCrc = RawData.ReadU16BE(hdrBuf, ref offset);
                mDataUncompLen = RawData.ReadU32BE(hdrBuf, ref offset);
                mDataCompLen = RawData.ReadU32BE(hdrBuf, ref offset);
                mDataCRC = RawData.ReadU16BE(hdrBuf, ref offset);
                offset += 2;    // unknown
                Debug.Assert(offset == FIXED_LEN);

                //
                // Now process the variable-size sections.
                //
                if ((flags & FLAGS_DIRECTORY) != 0) {
                    mIsFolder = true;
                    stream.ReadExactly(hdrBuf, 0, 2);
                    FolderEntryCount = RawData.GetU16BE(hdrBuf, 0);
                    if (mDataUncompLen == 0xffffffff) {
                        // endFolder entry?
                        IsIgnorable = true;
                        return true;
                    }
                    // Zero out the length field to avoid confusion and weirdness.
                    mDataUncompLen = mDataCompLen = 0;
                } else {
                    stream.ReadExactly(hdrBuf, 0, 2);
                    mDataMethod = hdrBuf[0];
                    int dKeyLen = hdrBuf[1];
                    if ((flags & FLAGS_ENCRYPTED) != 0 && mDataUncompLen != 0) {
                        if (dKeyLen != KEY_LENGTH) {
                            return false;
                        }
                        stream.Seek(KEY_LENGTH, SeekOrigin.Current);
                    } else if (dKeyLen != 0) {
                        // Should not be storing a key if not encrypted.
                        return false;
                    }
                }

                // Get filename.
                mRawFileName = new byte[fileNameLen];
                stream.ReadExactly(mRawFileName);

                // Check for comment.
                long consumed = stream.Position - mHeaderOffset;
                if (consumed < headerLen) {
                    // There's space for a file comment.  Extract it.
                    stream.ReadExactly(hdrBuf, 0, 2);
                    ushort commentLen = RawData.GetU16BE(hdrBuf, 0);
                    stream.Seek(2, SeekOrigin.Current);     // skip unknown value
                    mRawComment = new byte[commentLen];
                    stream.ReadExactly(mRawComment);
                }

                //
                // Read second section, starting with the fixed-length part.
                //
                const int HEADER2_FIXED_LEN = 14;
                offset = 0;
                stream.ReadExactly(hdrBuf, 0, HEADER2_FIXED_LEN);
                ushort flags2 = RawData.ReadU16BE(hdrBuf, ref offset);
                offset += 2;    // unknown
                mFileType = RawData.ReadU32BE(hdrBuf, ref offset);
                mCreator = RawData.ReadU32BE(hdrBuf, ref offset);
                mFinderFlags = RawData.ReadU16BE(hdrBuf, ref offset);
                Debug.Assert(offset == HEADER2_FIXED_LEN);
                if (version == 1) {
                    stream.Seek(22, SeekOrigin.Current);
                } else {
                    stream.Seek(18, SeekOrigin.Current);
                }

                // Get resource fork information, if present.
                if ((flags2 & 0x01) != 0) {
                    mHasRsrcFork = true;

                    const int RSRC_FIXED_LEN = 14;
                    offset = 0;
                    stream.ReadExactly(hdrBuf, 0, RSRC_FIXED_LEN);
                    mRsrcUncompLen = RawData.ReadU32BE(hdrBuf, ref offset);
                    mRsrcCompLen = RawData.ReadU32BE(hdrBuf, ref offset);
                    mRsrcCRC = RawData.ReadU16BE(hdrBuf, ref offset);
                    offset += 2;    // unknown
                    mRsrcMethod = RawData.ReadU8(hdrBuf, ref offset);
                    int rKeyLen = RawData.ReadU8(hdrBuf, ref offset);
                    Debug.Assert(offset == RSRC_FIXED_LEN);

                    if ((flags & FLAGS_ENCRYPTED) != 0 && mRsrcUncompLen != 0) {
                        if (rKeyLen != KEY_LENGTH) {
                            return false;
                        }
                        stream.Seek(KEY_LENGTH, SeekOrigin.Current);
                    } else if (rKeyLen != 0) {
                        // Should not be storing a key if not encrypted.
                        return false;
                    }
                }

                mDataForkOffset = stream.Position;
                if (mHasRsrcFork) {
                    mRsrcForkOffset = mDataForkOffset + mDataCompLen;
                } else {
                    mRsrcForkOffset = -1;
                }

                // Set stream position past the file data.
                if (!mIsFolder) {
                    stream.Seek(mDataCompLen + mRsrcCompLen, SeekOrigin.Current);
                }

                // Cook the filename.
                mFileName = Encoding.UTF8.GetString(mRawFileName);

            } catch (EndOfStreamException) {
                Archive.Notes.AddE("Ran off end of archive while reading file header");
                return false;
            }
            return true;
        }

        /// <summary>
        /// Generates the path prefix by walking up the chain of parents.
        /// </summary>
        internal void GeneratePathPrefix(Dictionary<long, StuffIt5_FileEntry> dirs) {
            Debug.Assert(string.IsNullOrEmpty(mPathPrefix));

            uint parentOff = mDirOffset;
            while (parentOff != 0) {
                if (!dirs.TryGetValue(parentOff, out StuffIt5_FileEntry? parent)) {
                    return;
                }
                mPathPrefix = parent.mFileName + SEP_CHAR + mPathPrefix;
                parentOff = parent.mDirOffset;
            }
        }

        /// <summary>
        /// Creates an object for reading the contents of the entry out of the archive.
        /// </summary>
        /// <exception cref="FileNotFoundException">Part does not exist.</exception>
        /// <exception cref="NotSupportedException">Compression format not supported.</exception>
        internal ArcReadStream CreateReadStream(FilePart part) {
            long fileOffset;
            uint uncompLen;
            uint compLen;
            ushort crc;
            CompressionFormat format;
            if (part == FilePart.RsrcFork && mHasRsrcFork) {
                fileOffset = mRsrcForkOffset;
                uncompLen = mRsrcUncompLen;
                compLen = mRsrcCompLen;
                crc = mRsrcCRC;
                format = StuffIt_FileEntry.GetCompressionFormat(mRsrcMethod);
            } else if (part == FilePart.DataFork) {
                fileOffset = mDataForkOffset;
                uncompLen = mDataUncompLen;
                compLen = mDataCompLen;
                crc = mDataCRC;
                format = StuffIt_FileEntry.GetCompressionFormat(mDataMethod);
            } else {
                throw new FileNotFoundException("Record does not have part: " + part);
            }

            Checker checker = new CheckARC(0x0000, crc);

            // Seek to start of compressed or uncompressed data.
            Debug.Assert(Archive.DataStream != null);
            Archive.DataStream.Position = fileOffset;

            if (format == CompressionFormat.Uncompressed) {
                return new ArcReadStream(Archive, compLen, checker);
            }

            Stream expander;
            switch (format) {
                case CompressionFormat.StuffRLE:
                    expander = new RLE90Stream(Archive.DataStream, CompressionMode.Decompress,
                        true, compLen);
                    break;
                case CompressionFormat.StuffLZC:
                    expander = new LZCStream(Archive.DataStream, CompressionMode.Decompress,
                        true, compLen, true, 14, true);
                    break;
                case CompressionFormat.StuffHuff:
                    expander = new StuffHuffStream(Archive.DataStream, CompressionMode.Decompress,
                        true, uncompLen);
                    break;
                case CompressionFormat.StuffLZHuff:
                    expander = new StuffLZHuffStream(Archive.DataStream, CompressionMode.Decompress,
                        true, compLen, -1);
                    break;

                default:
                    throw new NotSupportedException("Compression format not supported");
            }
            return new ArcReadStream(Archive, uncompLen, checker, expander);
        }

        // IFileEntry
        public bool GetPartInfo(FilePart part, out long length, out long storageSize,
                out CompressionFormat format) {
            if (part == FilePart.RsrcFork && mHasRsrcFork) {
                length = mRsrcUncompLen;
                storageSize = mRsrcCompLen;
                format = StuffIt_FileEntry.GetCompressionFormat(mRsrcMethod);
                return true;
            } else if (part == FilePart.DataFork) {
                length = mDataUncompLen;
                storageSize = mDataCompLen;
                format = StuffIt_FileEntry.GetCompressionFormat(mDataMethod);
                return true;
            } else {
                length = -1;
                storageSize = 0;
                format = CompressionFormat.Uncompressed;
                return false;
            }
        }

        // IFileEntry
        public void SaveChanges() { /* not used for archives */ }

        #region Filenames

        // IFileEntry
        public int CompareFileName(string fileName) {
            return string.Compare(mFileName, fileName, StringComparison.OrdinalIgnoreCase);
        }

        // IFileEntry
        public int CompareFileName(string fileName, char fileNameSeparator) {
            return PathName.ComparePathNames(mFileName, SEP_CHAR, fileName,
                fileNameSeparator, PathName.CompareAlgorithm.OrdinalIgnoreCase);
        }

        #endregion Filenames

        public override string ToString() {
            StringBuilder sb = new StringBuilder();
            sb.Append("[StuffIt5-entry '");
            sb.Append(FileName);
            sb.Append("' ");
            if (IsDirectory) {
                sb.Append(" DIR");
            }
            if (IsIgnorable) {
                sb.Append("-END");
            }
            sb.Append("]");
            return sb.ToString();
        }
    }
}