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

using CommonUtil;
using DiskArc.Multi;
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
            public ushort[] bLength = RawData.EMPTY_USHORT_ARRAY;

            /// <summary>
            /// Length of the header area.
            /// </summary>
            public int Length { get { return 4 + bLength.Length * 2; } }

            public Header() { }

            public void Load(byte[] buf, int offset) {
                int startOffset = offset;
                mSrcCmp = buf[offset++];
                mSrcType = buf[offset++];
                mSrcSize = RawData.ReadU16BE(buf, ref offset);
                int lenCount = (mSrcSize <= 800) ? SD_COUNT : HD_COUNT;
                bLength = new ushort[lenCount];
                for (int i = 0; i < lenCount; i++) {
                    bLength[i] = RawData.ReadU16BE(buf, ref offset);
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
            Debug.Assert(hdr.mSrcCmp == 1);

            DART disk = new DART(stream, hdr, appHook);
            stream.Position = hdr.Length;

            // TODO: read
            disk.mUserData = new byte[hdr.mSrcSize * 2 * 512];
            disk.mTagData = new byte[hdr.mSrcSize * 2 * 12];

            // TODO: compute checksum

            return disk;
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
