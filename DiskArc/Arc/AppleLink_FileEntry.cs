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
using System.IO.Compression;
using System.Text;

using CommonUtil;
using DiskArc.Comp;
using DiskArc.FS;
using static DiskArc.Defs;

namespace DiskArc.Arc {
    internal class AppleLink_FileEntry : IFileEntry {
        //
        // IFileEntry interfaces.
        //

        public bool IsValid { get { return Archive != null; } }
        public bool IsDubious { get; private set; }
        public bool IsDamaged { get; private set; }

        public bool IsDirectory => mHeader.mStorageType == (byte)ProDOS.StorageType.Directory;

        public bool HasDataFork => mHeader.mDataCompLen != 0;

        public bool HasRsrcFork => mHeader.mRsrcCompLen != 0;

        public bool IsDiskImage => false;

        public IFileEntry ContainingDir => IFileEntry.NO_ENTRY;
        public int Count => 0;

        public string FileName {
            get => mHeader.FileName;
            set => throw new InvalidOperationException("Cannot modify object without transaction");
        }
        public char DirectorySeparatorChar {
            get => '/';     // might need to switch this on filesystem type
            set { }
        }
        public byte[] RawFileName {
            get {
                byte[] buf = new byte[mHeader.mRawFileName.Length];
                Array.Copy(mHeader.mRawFileName, buf, mHeader.mRawFileName.Length);
                return buf;
            }
            set => throw new InvalidOperationException("Cannot modify object without transaction");
        }

        public string FullPathName { get { return FileName; } }

        public bool HasProDOSTypes => true;
        public byte FileType {
            get => (byte)mHeader.mFileType;
            set => throw new InvalidOperationException("Cannot modify object without transaction");
        }
        public ushort AuxType {
            get => (ushort)mHeader.mAuxType;
            set => throw new InvalidOperationException("Cannot modify object without transaction");
        }

        public bool HasHFSTypes => false;
        public uint HFSFileType { get => 0; set { } }
        public uint HFSCreator { get => 0; set { } }

        public byte Access {
            get => (byte)mHeader.mAccess;
            set => throw new InvalidOperationException("Cannot modify object without transaction");
        }
        public DateTime CreateWhen {
            get => TimeStamp.ConvertDateTime_ProDOS(mHeader.mCreateWhen);
            set => throw new InvalidOperationException("Cannot modify object without transaction");
        }
        public DateTime ModWhen {
            get => TimeStamp.ConvertDateTime_ProDOS(mHeader.mModWhen);
            set => throw new InvalidOperationException("Cannot modify object without transaction");
        }

        public long StorageSize => mHeader.mRsrcCompLen + mHeader.mDataCompLen;

        public long DataLength => mHeader.mDataUncompLen;

        public long RsrcLength => mHeader.mRsrcUncompLen;

        public string Comment { get => string.Empty; set { } }

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

        internal class Header {
            public const int LENGTH = 0x36;     // length of fixed portion, before the filename

            public CompressionFormat mRsrcAlg;
            public CompressionFormat mDataAlg;
            public ushort mRsrcCRC;
            public ushort mDataCRC;
            public uint mRsrcBlocks;
            public uint mDataBlocks;
            public uint mRsrcCompLen;
            public uint mDataCompLen;
            public ushort mAccess;
            public ushort mFileType;
            public uint mAuxType;
            public ushort mReserved;
            public ushort mStorageType;
            public uint mRsrcUncompLen;
            public uint mDataUncompLen;
            public uint mCreateWhen;
            public uint mModWhen;
            public ushort mFileNameLen;
            public ushort mHeaderCRC;

            public ushort mCalcCRC;
            public byte[] mRawFileName = RawData.EMPTY_BYTE_ARRAY;
            public string FileName { get; private set; } = string.Empty;

            public void Load(byte[] buf, int offset) {
                int startOffset = offset;
                mRsrcAlg = ConvertCompressionAlg(buf[offset++]);
                mDataAlg = ConvertCompressionAlg(buf[offset++]);
                mRsrcCRC = RawData.ReadU16LE(buf, ref offset);
                mDataCRC = RawData.ReadU16LE(buf, ref offset);
                mRsrcBlocks = RawData.ReadU32LE(buf, ref offset);
                mDataBlocks = RawData.ReadU32LE(buf, ref offset);
                mRsrcCompLen = RawData.ReadU32LE(buf, ref offset);
                mDataCompLen = RawData.ReadU32LE(buf, ref offset);
                mAccess = RawData.ReadU16LE(buf, ref offset);
                mFileType = RawData.ReadU16LE(buf, ref offset);
                mAuxType = RawData.ReadU32LE(buf, ref offset);
                mReserved = RawData.ReadU16LE(buf, ref offset);
                mStorageType = RawData.ReadU16LE(buf, ref offset);
                mRsrcUncompLen = RawData.ReadU32LE(buf, ref offset);
                mDataUncompLen = RawData.ReadU32LE(buf, ref offset);
                mCreateWhen = RawData.ReadU32LE(buf, ref offset);
                mModWhen = RawData.ReadU32LE(buf, ref offset);
                mFileNameLen = RawData.ReadU16LE(buf, ref offset);
                mHeaderCRC = RawData.ReadU16LE(buf, ref offset);
                Debug.Assert(offset - startOffset == LENGTH);

                // Compute the first part of the CRC.
                mCalcCRC = CRC16.XMODEM_OnBuffer(0x0000, buf, startOffset, LENGTH - 2);
            }

            public bool ReadFileName(Stream stream) {
                if (stream.Length - stream.Position < mFileNameLen) {
                    return false;
                }
                mRawFileName = new byte[mFileNameLen];
                stream.ReadExactly(mRawFileName, 0, mFileNameLen);
                // Finish computing the CRC.
                mCalcCRC = CRC16.XMODEM_OnBuffer(mCalcCRC, mRawFileName, 0, mFileNameLen);
                FileName = Encoding.ASCII.GetString(mRawFileName);
                return true;
            }

            public bool ValidateHeader(Notes notes) {
                // check header CRC
                if (mCalcCRC != mHeaderCRC) {
                    //notes.AddW("Header CRC mismatch: expected=$" + mHeaderCRC.ToString("x4") +
                    //    " actual=$" + mCalcCRC.ToString("x4"));
                    // keep going
                }
                return true;
            }

            private static CompressionFormat ConvertCompressionAlg(byte alg) {
                if (alg == 0x00) {
                    return CompressionFormat.Uncompressed;
                } else if (alg == 0x03) {
                    return CompressionFormat.Squeeze;
                } else {
                    return CompressionFormat.Unknown;
                }
            }
        }

        /// <summary>
        /// Reference to archive object.
        /// </summary>
        internal AppleLink Archive { get; private set; }

        /// <summary>
        /// File record header contents.
        /// </summary>
        private Header mHeader;

        private long mHeaderFileOffset;

        private long mDataFileOffset;

        /// <summary>
        /// Filename.
        /// </summary>
        private string mFileName = string.Empty;


        /// <summary>
        /// Private constructor.
        /// </summary>
        private AppleLink_FileEntry(AppleLink archive, Header header) {
            Archive = archive;
            mHeader = header;
        }

        /// <summary>
        /// Creates an object from the stream at the current position.  On completion,
        /// the stream will be positioned at the start of the following record, or EOF.
        /// </summary>
        /// <param name="archive">Archive that this entry will be a part of.</param>
        /// <returns>New entry object, or null if a record wasn't found.</returns>
        internal static AppleLink_FileEntry? ReadNextRecord(AppleLink archive) {
            Stream dataStream = archive.DataStream!;

            if (dataStream.Position + Header.LENGTH >= dataStream.Length) {
                // Can't possibly be a valid entry here.
                return null;
            }

            long headerOffset = dataStream.Position;
            byte[] headerBuf = new byte[Header.LENGTH];
            dataStream.ReadExactly(headerBuf, 0, Header.LENGTH);

            Header header = new Header();
            header.Load(headerBuf, 0);
            if (!header.ValidateHeader(archive.Notes)) {
                archive.IsDubious = true;
                return null;
            }
            if (!header.ReadFileName(dataStream)) {
                return null;
            }

            AppleLink_FileEntry newEntry = new AppleLink_FileEntry(archive, header);
            newEntry.mHeaderFileOffset = headerOffset;
            newEntry.mDataFileOffset = dataStream.Position;

            // Skip past file data.
            dataStream.Position += header.mRsrcCompLen + header.mDataCompLen;
            if (dataStream.Position > dataStream.Length) {
                archive.Notes.AddW("Entry truncated: '" + header.FileName + "'");
                newEntry.IsDamaged = true;
            }
            return newEntry;
        }

        // IFileEntry
        public bool GetPartInfo(FilePart part, out long length, out long storageSize,
                out CompressionFormat format) {
            switch (part) {
                case FilePart.DataFork:
                    length = mHeader.mDataUncompLen;
                    storageSize = mHeader.mDataCompLen;
                    format = mHeader.mDataAlg;
                    // won't actually have a data fork for empty files or directories
                    return true;
                case FilePart.RsrcFork:
                    length = mHeader.mRsrcUncompLen;
                    storageSize = mHeader.mRsrcCompLen;
                    format = mHeader.mRsrcAlg;
                    return HasRsrcFork;
                default:
                    length = -1;
                    storageSize = 0;
                    format = CompressionFormat.Unknown;
                    return false;
            }
        }

        /// <summary>
        /// Creates an object for reading the contents of the entry out of the archive.
        /// </summary>
        internal ArcReadStream CreateReadStream(FilePart part) {
            Debug.Assert(Archive.DataStream != null);

            long start, compLen, uncompLen;
            ushort expCRC;
            CompressionFormat fmt;
            switch (part) {
                case FilePart.DataFork:
                    if (mHeader.mDataCompLen == 0) {
                        throw new FileNotFoundException("No data fork");
                    }
                    start = mDataFileOffset + mHeader.mRsrcCompLen;
                    compLen = mHeader.mDataCompLen;
                    uncompLen = mHeader.mDataUncompLen;
                    fmt = mHeader.mDataAlg;
                    expCRC = mHeader.mDataCRC;
                    break;
                case FilePart.RsrcFork:
                    if (mHeader.mRsrcCompLen == 0) {
                        throw new FileNotFoundException("No rsrc fork");
                    }
                    start = mDataFileOffset;
                    compLen = mHeader.mRsrcCompLen;
                    uncompLen = mHeader.mRsrcUncompLen;
                    fmt = mHeader.mRsrcAlg;
                    expCRC = mHeader.mRsrcCRC;
                    break;
                default:
                    throw new FileNotFoundException("No part of type " + part);
            }

            // The CRC only matches on files up to 256 bytes.  At 257 bytes the results diverge.
            // We can't really use this.
            Checker? checker = null; //new CheckXMODEM16(0x0000, expCRC);
            Archive.DataStream.Position = start;
            Debug.Assert(Archive.DataStream.Length - Archive.DataStream.Position >= compLen);
            if (fmt == CompressionFormat.Uncompressed) {
                return new ArcReadStream(Archive, compLen, checker);
            } else if (fmt == CompressionFormat.Squeeze) {
                Stream expander = new SqueezeStream(Archive.DataStream,
                    CompressionMode.Decompress, true, false, string.Empty);
                return new ArcReadStream(Archive, uncompLen, checker, expander);
            } else {
                throw new NotImplementedException("AppleLink compression format \"" +
                    fmt + "\" is not supported");
            }
        }

        // IFileEntry
        public void SaveChanges() { }

        /// <summary>
        /// Invalidates the file entry object.  Called on the original set of entries after a
        /// successful commit.
        /// </summary>
        internal void Invalidate() {
            Debug.Assert(Archive != null); // harmless, but we shouldn't be invalidating twice
#pragma warning disable CS8625
            Archive = null;
#pragma warning restore CS8625
        }

        #region Filenames

        // IFileEntry
        public int CompareFileName(string fileName) {
            return string.Compare(mFileName, fileName, StringComparison.OrdinalIgnoreCase);
        }

        // IFileEntry
        public int CompareFileName(string fileName, char fileNameSeparator) {
            return PathName.ComparePathNames(mFileName, DirectorySeparatorChar, fileName,
                fileNameSeparator, PathName.CompareAlgorithm.OrdinalIgnoreCase);
        }

        #endregion Filenames
    }
}
