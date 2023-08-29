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
    /// One record in a MacBinary file.
    /// </summary>
    public class MacBinary_FileEntry : IFileEntry {
        //
        // IFileEntry interfaces.
        //

        public bool IsValid { get { return Archive != null; } }

        public bool IsDubious { get; private set; }

        public bool IsDamaged { get; private set; }

        public bool IsDirectory => false;

        public bool HasDataFork => mHeader.mDataForkLen != 0;
        public bool HasRsrcFork => mHeader.mRsrcForkLen != 0;

        public bool IsDiskImage => false;

        public IFileEntry ContainingDir => IFileEntry.NO_ENTRY;
        public int Count => 0;

        public string FileName {
            get => mHeader.FileName;
            set => throw new InvalidOperationException("Cannot modify object without transaction");
        }
        public char DirectorySeparatorChar { get => IFileEntry.NO_DIR_SEP; set { } }
        public byte[] RawFileName {
            get {
                byte[] buf = new byte[mHeader.mRawFileName.Length];
                Array.Copy(mHeader.mRawFileName, buf, mHeader.mRawFileName.Length);
                return buf;
            }
            set => throw new InvalidOperationException("Cannot modify object without transaction");
        }

        public string FullPathName { get { return FileName; } }

        public bool HasProDOSTypes => false;
        public byte FileType { get { return 0; } set { } }
        public ushort AuxType { get { return 0; } set { } }

        public bool HasHFSTypes => true;
        public uint HFSFileType {
            get => mHeader.mFileType;
            set => throw new InvalidOperationException("Cannot modify object without transaction");
        }
        public uint HFSCreator {
            get => mHeader.mCreator;
            set => throw new InvalidOperationException("Cannot modify object without transaction");
        }
        public byte Access {
            get {
                if ((mHeader.mProtected & 0x01) != 0) {
                    return FileAttribs.FILE_ACCESS_LOCKED;
                } else {
                    return FileAttribs.FILE_ACCESS_UNLOCKED;
                }
            }
            set => throw new InvalidOperationException("Cannot modify object without transaction");
        }
        public DateTime CreateWhen {
            get => TimeStamp.ConvertDateTime_HFS(mHeader.mCreateWhen);
            set => throw new InvalidOperationException("Cannot modify object without transaction");
        }
        public DateTime ModWhen {
            get => TimeStamp.ConvertDateTime_HFS(mHeader.mModWhen);
            set => throw new InvalidOperationException("Cannot modify object without transaction");
        }

        public long StorageSize => mHeader.ExpectedFileLen;

        public long DataLength => mHeader.DataForkLen;

        public long RsrcLength => mHeader.RsrcForkLen;

        public string Comment {
            get => string.Empty;        // TODO(maybe): look for info field
            set => throw new InvalidOperationException("Cannot modify object without transaction");
        }

        private static readonly List<IFileEntry> sEmptyList = new List<IFileEntry>();
        public IEnumerator<IFileEntry> GetEnumerator() {
            return sEmptyList.GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() {
            return sEmptyList.GetEnumerator();
        }

        //
        // MacBinary-specific fields.
        //

        internal class Header {
            public const uint SIGNATURE = 0x6d42494e;      // 'mBIN'
            public const int FILE_NAME_FIELD_LEN = 64;
            public const byte MB2_VERS = 129;
            public const byte MB3_VERS = 130;

            //
            // Header fields.
            //

            public byte mVersion;
            public byte[] mRawFileName = new byte[FILE_NAME_FIELD_LEN];
            public uint mFileType;
            public uint mCreator;
            public byte mHighFlags;
            public byte mReserved4A;
            public ushort mLocationV;
            public ushort mLocationH;
            public ushort mFolderID;
            public byte mProtected;
            public byte mReserved52;
            public uint mDataForkLen;
            public uint mRsrcForkLen;
            public uint mCreateWhen;
            public uint mModWhen;
            public ushort mInfoLen;         // v2
            public byte mLowFlags;          // v2
            public uint mSignature;         // v3
            public byte mScript;            // v3
            public byte mXFlags;            // v3
            public byte[] mReserved6C = new byte[8];
            public uint mUncompressedLen;   // v2
            public ushort mHeader2Len;      // v2
            public byte mCreatorVers;       // v2
            public byte mMinimumVers;       // v2
            public ushort mCRC;             // v2
            public ushort mReserved7E;

            //
            // Generated fields.
            //

            public string FileName { get; private set; } = string.Empty;
            public long DataForkOffset { get; private set; } = -1;
            public int DataForkLen => (int)mDataForkLen;
            public long RsrcForkOffset { get; private set; } = -1;
            public int RsrcForkLen => (int)mRsrcForkLen;
            public long InfoStart { get; private set; } = -1;
            public int InfoLen => mInfoLen;
            public bool HasCRCMismatch { get; private set; }

            public ushort CalculatedCRC { get; private set; }
            public long ExpectedFileLen { get; private set; }

            public Header() { }

            public void Load(byte[] buf, int offset) {
                int startOffset = offset;
                mVersion = buf[offset++];
                Array.Copy(buf, offset, mRawFileName, 0, FILE_NAME_FIELD_LEN);
                offset += FILE_NAME_FIELD_LEN;
                mFileType = RawData.ReadU32BE(buf, ref offset);
                mCreator = RawData.ReadU32BE(buf, ref offset);
                mHighFlags = buf[offset++];
                mReserved4A = buf[offset++];
                mLocationV = RawData.ReadU16BE(buf, ref offset);
                mLocationH = RawData.ReadU16BE(buf, ref offset);
                mFolderID = RawData.ReadU16BE(buf, ref offset);
                mProtected = buf[offset++];
                mReserved52 = buf[offset++];
                mDataForkLen = RawData.ReadU32BE(buf, ref offset);
                mRsrcForkLen = RawData.ReadU32BE(buf, ref offset);
                mCreateWhen = RawData.ReadU32BE(buf, ref offset);
                mModWhen = RawData.ReadU32BE(buf, ref offset);
                mInfoLen = RawData.ReadU16BE(buf, ref offset);
                mLowFlags = buf[offset++];
                mSignature = RawData.ReadU32BE(buf, ref offset);
                mScript = buf[offset++];
                mXFlags = buf[offset++];
                Array.Copy(buf, offset, mReserved6C, 0, mReserved6C.Length);
                offset += mReserved6C.Length;
                mUncompressedLen = RawData.ReadU32BE(buf, ref offset);
                mHeader2Len = RawData.ReadU16BE(buf, ref offset);
                mCreatorVers = buf[offset++];
                mMinimumVers = buf[offset++];
                mCRC = RawData.ReadU16BE(buf, ref offset);
                mReserved7E = RawData.ReadU16BE(buf, ref offset);
                Debug.Assert(offset - startOffset == MacBinary.HEADER_LEN);

                CalculatedCRC = CRC16.XMODEM_OnBuffer(0x0000, buf, startOffset,
                    MacBinary.HEADER_LEN - 4);
            }

            /// <summary>
            /// Processes and validates the loaded data.
            /// </summary>
            /// <remarks>
            /// <para><see href="https://entropymine.wordpress.com/2019/02/13/detecting-macbinary-format/"/>
            /// has some suggestions for auto-detecting MacBinary.  The trouble is that, if you
            /// want to handle the earlier versions, fields like "signature" don't help you
            /// at all.</para>
            /// </remarks>
            /// <returns>True if it looks good, false if not.</returns>
            public bool Process(long fileLength) {
                // The MacBinary II docs explicitly recommend checking these bytes.
                if (mVersion != 0 || mReserved4A != 0 || mReserved52 != 0) {
                    return false;
                }

                // Test the filename length.
                int fileNameLen = mRawFileName[0];
                if (fileNameLen == 0 || fileNameLen >= FILE_NAME_FIELD_LEN) {
                    return false;
                }
                if (mRawFileName[0] < FILE_NAME_FIELD_LEN) {
                    // Could be valid.  Convert it.
                    FileName = MacChar.MacStrToUnicode(mRawFileName, 0,
                        MacChar.Encoding.RomanShowCtrl);
                }
                if (mDataForkLen == 0 && mRsrcForkLen == 0) {
                    // This might be MacBinary, but it's empty, so it's useless.  Rather than
                    // chance a false-positive, just reject it.  (Could allow it if the signature
                    // field is present, but... why?)
                    return false;
                }
                if ((int)mDataForkLen < 0 || (int)mRsrcForkLen < 0) {
                    // Effectively puts a 2GB cap on each fork.  This is fine.  Mac resource
                    // forks were limited to 16MB on HFS.
                    return false;
                }

                // Compute the starting offsets of each fork's data.  While we're at it, compute
                // a minimum size for the file.  The minimum size differs from the padded size
                // because some programs supposedly failed to pad whatever was added last.  Of
                // course, it's also possible that something threw a bunch of garbage on the end.
                //
                // It's important to get this right, because the combination of these fields is
                // a strong indication of whether this is a MacBinary file.  It also ensures
                // that the extraction process won't try to read off the end of the stream.
                long offset = MacBinary.HEADER_LEN;
                long minSize = offset;
                if (mHeader2Len != 0) {
                    minSize = offset + mHeader2Len;
                    offset += RoundUpChunkLen(mHeader2Len);
                }
                DataForkOffset = offset;
                if (mDataForkLen != 0) {
                    minSize = offset + mDataForkLen;
                    offset += RoundUpChunkLen((int)mDataForkLen);
                }
                RsrcForkOffset = offset;
                if (mRsrcForkLen != 0) {
                    minSize = offset + mRsrcForkLen;
                    offset += RoundUpChunkLen((int)mRsrcForkLen);
                }
                InfoStart = offset;
                if (mInfoLen != 0) {
                    minSize = offset + mInfoLen;
                    offset += RoundUpChunkLen(mInfoLen);
                }
                // "minSize" is smallest possible valid file, "offset" is expected length
                ExpectedFileLen = offset;

                const int MAX_GARBAGE = 8192;
                if (fileLength < minSize) {
                    return false;
                }
                if (fileLength > offset + MAX_GARBAGE) {
                    return false;
                }

                return true;
            }
        }

        /// <summary>
        /// Reference to containing archive object.
        /// </summary>
        internal MacBinary Archive { get; private set; }

        /// <summary>
        /// Header record for this entry.
        /// </summary>
        private Header mHeader = new Header();


        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="archive">Containing archive object.</param>
        internal MacBinary_FileEntry(MacBinary archive) {
            Archive = archive;
        }

        /// <summary>
        /// Scans the contents of the archive.
        /// </summary>
        /// <returns>True if parsing was successful, false if errors were found.</returns>
        internal bool Scan() {
            Stream stream = Archive.DataStream!;
            Debug.Assert(stream.Position == 0);     // one entry per file

            byte[] buf = new byte[MacBinary.HEADER_LEN];
            stream.ReadExactly(buf, 0, MacBinary.HEADER_LEN);
            mHeader.Load(buf, 0);
            if (!mHeader.Process(stream.Length)) {
                return false;
            }
            if (mHeader.mCRC != 0 && mHeader.mCRC != mHeader.CalculatedCRC) {
                Archive.Notes.AddW("Header CRC mismatch: expected=0x" +
                    mHeader.mCRC.ToString("x4") + ", actual=0x" +
                    mHeader.CalculatedCRC.ToString("x4"));
                IsDubious = true;
            }
            if (stream.Length != mHeader.ExpectedFileLen) {
                Archive.Notes.AddW("File should be " + mHeader.ExpectedFileLen +
                    " bytes long, but is actually " + stream.Length);
                // probably junk on end, no need to mark as dubious
            }

            return true;
        }

        // IFileEntry
        public bool GetPartInfo(FilePart part, out long length, out long storageSize,
                out CompressionFormat format) {
            format = CompressionFormat.Uncompressed;
            switch (part) {
                case FilePart.DataFork:
                    length = mHeader.mDataForkLen;
                    storageSize = RoundUpChunkLen((int)mHeader.mDataForkLen);
                    return HasDataFork;
                case FilePart.RsrcFork:
                    length = mHeader.mRsrcForkLen;
                    storageSize = RoundUpChunkLen((int)mHeader.mRsrcForkLen);
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

            long start, len;
            switch (part) {
                case FilePart.DataFork:
                    if (mHeader.mDataForkLen == 0) {
                        throw new FileNotFoundException("No data fork");
                    }
                    start = mHeader.DataForkOffset;
                    len = mHeader.mDataForkLen;
                    break;
                case FilePart.RsrcFork:
                    if (mHeader.mRsrcForkLen == 0) {
                        throw new FileNotFoundException("No rsrc fork");
                    }
                    start = mHeader.RsrcForkOffset;
                    len = mHeader.mRsrcForkLen;
                    break;
                default:
                    throw new FileNotFoundException("No part of type " + part);
            }

            Archive.DataStream.Position = start;
            return new ArcReadStream(Archive, len, null);
        }

        // IFileEntry
        public void SaveChanges() { /* not used for archives */ }

        // IFileEntry
        public int CompareFileName(string fileName) {
            return MacChar.CompareHFSFileNames(mHeader.FileName, fileName);
        }

        // IFileEntry
        public int CompareFileName(string fileName, char fileNameSeparator) {
            return CompareFileName(fileName);
        }


        /// <summary>
        /// Rounds up to the nearest multiple of the chunk length.
        /// </summary>
        internal static int RoundUpChunkLen(int len) {
            // assume CHUNK_LEN is a power of 2
            return (len + MacBinary.CHUNK_LEN - 1) & ~(MacBinary.CHUNK_LEN - 1);
        }
    }
}
