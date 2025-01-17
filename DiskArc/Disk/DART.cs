/*
 * Copyright 2025 faddenSoft
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
using System.IO.Compression;

using CommonUtil;
using DiskArc.Comp;
using static DiskArc.Defs;
using static DiskArc.IDiskImage;

namespace DiskArc.Disk {
    /// <summary>
    /// <para>DART disk image file.  This has a fixed-length header followed by disk data with
    /// optional compression.  These are only used for floppy disks up to 1400KB in size, so
    /// keeping the entire file in memory is viable.</para>
    /// </summary>
    /// <remarks>
    /// <para>The format includes a checksum, but it's stored in the resource fork.</para>
    /// </remarks>
    public class DART : IDiskImage {
        //
        // IDiskImage properties.
        //

        public bool IsReadOnly => true;
        public bool IsDubious { get; protected set; }

        // This indicates un-flushed data.  Currently only set when the metadata changes.
        public bool IsDirty => false;

        public virtual bool IsModified { get => false; set { } }

        public Notes Notes { get; } = new Notes();
        public GatedChunkAccess? ChunkAccess { get; protected set; }
        public IDiskContents? Contents { get; protected set; }

        //
        // Innards.
        //

        protected Stream mStream;
        protected AppHook mAppHook;
        protected Header mHeader;

        private byte[] mUserData;
        private byte[] mTagData;

        private const int BLOCK_DATA_LEN = 512 * 40;
        private const int BLOCK_TAG_LEN = 12 * 40;
        private const int BLOCK_TOTAL_LEN = BLOCK_DATA_LEN + BLOCK_TAG_LEN;
        private enum CompressionType { Fast = 0, Best = 1, None = 2 }

        protected class Header {
            private const int SD_COUNT = 40;
            private const int HD_COUNT = 72;
            public const int SD_LENGTH = 4 + SD_COUNT * 2;
            public const int HD_LENGTH = 4 + HD_COUNT * 2;
            public const int COMP_MIN = 0;
            public const int COMP_MAX = 2;

            public byte mSrcCmp;
            public byte mSrcType;
            public ushort mSrcSize;
            public ushort[] mBLength = RawData.EMPTY_USHORT_ARRAY;

            /// <summary>
            /// Length of the header area.
            /// </summary>
            public int Length { get { return 4 + mBLength.Length * 2; } }

            public Header() { }

            public void Load(byte[] buf, int offset) {
                int startOffset = offset;
                mSrcCmp = buf[offset++];
                mSrcType = buf[offset++];
                mSrcSize = RawData.ReadU16BE(buf, ref offset);
                int lenCount = (mSrcSize <= 800) ? SD_COUNT : HD_COUNT;
                mBLength = new ushort[lenCount];
                for (int i = 0; i < lenCount; i++) {
                    mBLength[i] = RawData.ReadU16BE(buf, ref offset);
                }
                Debug.Assert(offset == startOffset + SD_LENGTH ||
                    offset == startOffset + HD_LENGTH);
            }

            /// <summary>
            /// Uses srcType and srcSize to determine the media kind.
            /// </summary>
            public static Defs.MediaKind FormatToKind(byte srcType, ushort srcSize) {
                switch (srcType) {
                    case 1:     // kMacDisk
                    case 2:     // kLisaDisk
                    case 3:     // kAppleIIDisk
                        if (srcSize == 400) {
                            return MediaKind.GCR_SSDD35;
                        } else if (srcSize == 800) {
                            return MediaKind.GCR_DSDD35;
                        } else {
                            return MediaKind.Unknown;
                        }
                    case 16:    // kMacHiDDisk
                    case 17:    // kMSDOSLowDDisk
                    case 18:    // kMSDOSHiDDisk
                        if (srcSize == 720) {
                            return MediaKind.MFM_DSDD35;
                        } else if (srcSize == 1440) {
                            return MediaKind.MFM_DSHD35;
                        } else {
                            return MediaKind.Unknown;
                        }
                    default:
                        return MediaKind.Unknown;
                }
            }
        }

        // IDiskImage-required delegate
        public static bool TestKind(Stream stream, AppHook appHook) {
            return ValidateHeader(stream, appHook, out Header unused1);
        }

        /// <summary>
        /// Validates the DART file header.
        /// </summary>
        /// <param name="stream">File data stream.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <param name="hdr">Result: validated header.</param>
        /// <returns>True if this looks like a DiskCopy file.</returns>
        private static bool ValidateHeader(Stream stream, AppHook appHook, out Header hdr) {
            hdr = new Header();

            // Read the header into memory.  For an SD image we'll be reading too much, but
            // with the compression used there's no risk of reading past EOF.
            const int LEN = Header.HD_LENGTH;
            stream.Position = 0;
            if (stream.Length < LEN) {
                return false;
            }
            byte[] hdrBuf = new byte[LEN];
            stream.ReadExactly(hdrBuf, 0, LEN);
            hdr.Load(hdrBuf, 0);

            // Validate fields.
            if (Header.FormatToKind(hdr.mSrcType, hdr.mSrcSize) == MediaKind.Unknown) {
                return false;
            }
            if (hdr.mSrcCmp < Header.COMP_MIN || hdr.mSrcCmp > Header.COMP_MAX) {
                return false;
            }
            // Check the block-length entries.  We should have nonzero values for the parts
            // of the disk that exist, zero values for the rest (e.g. last half of 400KB disk).
            int expected = hdr.mSrcSize / 20;
            Debug.Assert(hdr.mBLength.Length >= expected);
            for (int i = 0; i < hdr.mBLength.Length; i++) {
                if (i < expected && hdr.mBLength[i] == 0) {
                    Debug.WriteLine("Bad length in block " + i);
                    return false;
                } else if (i >= expected && hdr.mBLength[i] != 0) {
                    Debug.WriteLine("Unexpected data in block " + i);
                    return false;
                }

                if (i < expected && hdr.mSrcCmp == (int)CompressionType.None &&
                        (hdr.mBLength[i] != BLOCK_TOTAL_LEN && (short)hdr.mBLength[i] != -1)) {
                    Debug.WriteLine("Unexpected length on uncompressed data: " + hdr.mBLength[i]);
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Private constructor.
        /// </summary>
        private DART(Stream stream, Header hdr, AppHook appHook) {
            mStream = stream;
            mHeader = hdr;
            mAppHook = appHook;

            mUserData = mTagData = RawData.EMPTY_BYTE_ARRAY;
        }

        /// <summary>
        /// Opens a disk image file.
        /// </summary>
        /// <param name="stream">Disk image data stream.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <returns>Disk image reference.</returns>
        /// <exception cref="NotSupportedException">Incompatible data stream.</exception>
        public static DART OpenDisk(Stream stream, AppHook appHook) {
            if (!ValidateHeader(stream, appHook, out Header hdr)) {
                throw new NotSupportedException("Incompatible data stream");
            }

            DART disk = new DART(stream, hdr, appHook);
            stream.Position = hdr.Length;

            byte[] tmpBuf = new byte[32768];
            byte[] uncBuf = new byte[BLOCK_TOTAL_LEN];

            disk.mUserData = new byte[hdr.mSrcSize * 2 * 512];
            disk.mTagData = new byte[hdr.mSrcSize * 2 * 12];
            int userOffset = 0;
            int tagOffset = 0;
            for (int i = 0; i < hdr.mBLength.Length; i++) {
                if (hdr.mBLength[i] == 0) {
                    break;
                }

                // Skip the range checks and just catch the exception if bad data causes failure.
                try {
                    //Debug.WriteLine("Unpack chunk " + i);
                    switch ((CompressionType)hdr.mSrcCmp) {
                        case CompressionType.None:
                            stream.ReadExactly(uncBuf, 0, BLOCK_TOTAL_LEN);
                            break;
                        case CompressionType.Fast:
                            int wordCount = hdr.mBLength[i];
                            if (wordCount == 0xffff) {
                                stream.ReadExactly(uncBuf, 0, BLOCK_TOTAL_LEN);
                            } else {
                                stream.ReadExactly(tmpBuf, 0, wordCount * 2);
                                UnpackRLE(tmpBuf, wordCount, uncBuf);
                            }
                            break;
                        case CompressionType.Best:
                            int byteCount = hdr.mBLength[i];
                            if (byteCount == 0xffff) {
                                stream.ReadExactly(uncBuf, 0, BLOCK_TOTAL_LEN);
                            } else {
                                stream.ReadExactly(tmpBuf, 0, byteCount);
                                UnpackLZH(tmpBuf, byteCount, uncBuf);
                            }
                            break;
                        default:
                            Debug.Assert(false);
                            break;
                    }
                } catch (IndexOutOfRangeException ex) {
                    Debug.WriteLine("Decompression failed: " + ex.Message);
                    disk.IsDubious = true;
                    break;
                } catch (ArgumentException ex) {
                    Debug.WriteLine("Disk image read failed: " + ex.Message);
                    disk.IsDubious = true;
                    break;
                }

                Array.Copy(uncBuf, 0, disk.mUserData, userOffset, BLOCK_DATA_LEN);
                userOffset += BLOCK_DATA_LEN;
                Array.Copy(uncBuf, BLOCK_DATA_LEN, disk.mTagData, tagOffset, BLOCK_TAG_LEN);
                tagOffset += BLOCK_TAG_LEN;
            }
            if (userOffset != disk.mUserData.Length || tagOffset != disk.mTagData.Length) {
                disk.Notes.AddE("Failed to unpack complete file: data=" + userOffset + " of " +
                    disk.mUserData.Length + ", tag=" + tagOffset + " of " + disk.mTagData.Length);
                disk.IsDubious = true;
            }

            // Compute checksums.  The checksum on the original data is stored in the disk image's
            // resource fork, which we don't have access to.
            uint dataSum = DiskCopy.ComputeChecksum(disk.mUserData, 0, disk.mUserData.Length);
            uint tagSum = DiskCopy.ComputeChecksum(disk.mTagData, 0, disk.mTagData.Length);
            disk.Notes.AddI("Data checksum 0x" + dataSum.ToString("x8") +
                ", tag checksum 0x" + tagSum.ToString("x8"));

            return disk;
        }

        /// <summary>
        /// Unpacks a block of DART RLE data into a 20960-byte buffer.  An exception is thrown
        /// if bad data is encountered (should be very rare).
        /// </summary>
        /// <param name="inBuf">Buffer with input data.</param>
        /// <param name="wordCount">Length of input data, in 16-bit words.</param>
        /// <param name="outBuf">Output buffer</param>
        /// <exception cref="IndexOutOfRangeException">Bad data encountered.</exception>
        private static void UnpackRLE(byte[] inBuf, int wordCount, byte[] outBuf) {
            //Debug.WriteLine(" RLE wordCount=" + wordCount);
            int inOffset = 0;
            int outOffset = 0;
            while (inOffset < wordCount * 2) {
                short count = (short)RawData.ReadU16BE(inBuf, ref inOffset);
                if (count > 0) {
                    for (int j = 0; j < count * 2; j++) {
                        outBuf[outOffset++] = inBuf[inOffset++];
                    }
                } else if (count < 0) {
                    ushort pattern = RawData.ReadU16BE(inBuf, ref inOffset);
                    byte hi = (byte)(pattern >> 8);
                    byte lo = (byte)pattern;
                    for (int j = 0; j < -count; j++) {
                        outBuf[outOffset++] = hi;
                        outBuf[outOffset++] = lo;
                    }
                } else {
                    Debug.WriteLine("Found zero count in RLE stream");
                    throw new IndexOutOfRangeException("zero count");
                }
            }
            if (outOffset != BLOCK_TOTAL_LEN) {
                throw new IndexOutOfRangeException("partial RLE expansion: " + outOffset);
            }
        }

        /// <summary>
        /// Unpacks a block of DART LZH data into a 20960-byte buffer.  An exception is thrown
        /// if bad data is encountered.
        /// </summary>
        /// <param name="inBuf">Buffer with input data.</param>
        /// <param name="byteCount">Length of input data, in bytes.</param>
        /// <param name="outBuf">Output buffer.</param>
        /// <exception cref="IndexOutOfRangeException">Bad data encountered.</exception>
        private static void UnpackLZH(byte[] inBuf, int byteCount, byte[] outBuf) {
            MemoryStream inBufStream = new MemoryStream(inBuf);
            using (Stream codec = new LZHufStream(inBufStream, CompressionMode.Decompress,
                    true, false, 0x00, BLOCK_TOTAL_LEN)) {
                try {
                    codec.ReadExactly(outBuf, 0, BLOCK_TOTAL_LEN);
                } catch (Exception ex) {
                    // Convert exception to what the RLE code throws so we only have to
                    // catch one thing.
                    throw new IndexOutOfRangeException("failed: " + ex.Message);
                }
            }
        }

        // IDiskImage
        public bool AnalyzeDisk(SectorCodec? codec, SectorOrder orderHint, AnalysisDepth depth) {
            // Discard existing FileSystem and ChunkAccess objects (filesystem will be flushed).
            CloseContents();
            if (ChunkAccess != null) {
                ChunkAccess.AccessLevel = GatedChunkAccess.AccessLvl.Closed;
                ChunkAccess = null;
            }

            // Create chunk access, mapped to our in-memory data.
            IChunkAccess chunks = SetChunkAccess();
            Debug.Assert(ChunkAccess != null);

            if (depth == AnalysisDepth.ChunksOnly) {
                return true;
            }
            // Figure out the disk layout.
            if (!FileAnalyzer.AnalyzeFileSystem(chunks, true, mAppHook,
                    out IDiskContents? contents)) {
                return false;
            }
            // Got the disk layout, disable direct chunk access.
            Contents = contents;
            ChunkAccess.AccessLevel = GatedChunkAccess.AccessLvl.ReadOnly;
            return true;
        }

        private IChunkAccess SetChunkAccess() {
            Debug.Assert(mUserData.Length > 0);
            uint blockCount = (uint)(mUserData.Length / BLOCK_SIZE);
            MemoryStream userDataStream = new MemoryStream(mUserData);
            IChunkAccess chunks = new GeneralChunkAccess(userDataStream, 0, blockCount);
            ChunkAccess = new GatedChunkAccess(chunks);
            return chunks;
        }

        // IDiskImage
        public void CloseContents() {
            if (Contents is IFileSystem) {
                ((IFileSystem)Contents).Dispose();
            } else if (Contents is IMultiPart) {
                ((IMultiPart)Contents).Dispose();
            }
            Contents = null;
            if (ChunkAccess != null) {
                ChunkAccess.AccessLevel = GatedChunkAccess.AccessLvl.Open;
            }
        }

        ~DART() {
            Dispose(false);     // cleanup check
        }
        // IDisposable generic Dispose() implementation.
        public void Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        protected virtual void Dispose(bool disposing) {
            Debug.Assert(!mDisposed, this + " disposed twice");

            // If we're being disposed explicitly, e.g. by a using statement or declaration,
            // dispose of the filesystem object.  If we're being finalized, don't, because
            // the order of finalization is undefined.
            if (disposing) {
                Flush();
                if (Contents is IFileSystem) {
                    ((IFileSystem)Contents).Dispose();
                } else if (Contents is IMultiPart) {
                    ((IMultiPart)Contents).Dispose();
                }
                Contents = null;
            } else {
                Debug.Assert(false, "GC disposing DiskCopy, dirty=" + IsDirty +
                    " created:\r\n" + mCreationStackTrace);
            }
            mDisposed = true;
        }
        private bool mDisposed = false;
#if DEBUG
        private string mCreationStackTrace = Environment.StackTrace + Environment.NewLine + "---";
#else
        private string mCreationStackTrace = "  (stack trace not enabled)";
#endif

        // IDiskImage
        public void Flush() { }
    }
}
