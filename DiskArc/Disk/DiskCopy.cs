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
using System.Diagnostics;
using System.IO;

using CommonUtil;
using DiskArc.Multi;
using static DiskArc.Defs;
using static DiskArc.IDiskImage;
using static DiskArc.IMetadata;

namespace DiskArc.Disk {
    /// <summary>
    /// <para>DiskCopy disk image file.  This has a fixed-length header followed by the disk data,
    /// stored as 512-byte blocks.  These are only used to hold floppy disk images, so
    /// keeping the entire file in memory is viable.</para>
    /// </summary>
    /// <remarks>
    /// <para>The header includes a data checksum, so if we pass the block reads straight through
    /// we create the possibility of "corrupting" a disk if we crash.  Writing the entire file in
    /// one shot on every "flush" is expensive, but not unduly so, and we need to keep the data
    /// in memory for checksum computation.</para>
    /// <para>For best performance, we should use a write-back cache, where each block write
    /// updates the stream and our in-memory copy.  The flush operation then just recalculates
    /// the checksum.  We could implement this with a ChunkAccess wrapper, which we will need
    /// if we want to support 524-byte block I/O.</para>
    /// <para>A software crash could leave us with a disk image with an invalid checksum.  If
    /// we raise the IsDubious flag we won't be able to correct it.  Instead, we just create a
    /// warning note, and fix the checksum when the next modifications are made.</para>
    /// </remarks>
    public class DiskCopy : IDiskImage, IMetadata {
        public const string DESCRIPTION_NAME = "description";
        public const int TAG_SIZE = 12;

        //
        // IDiskImage properties.
        //

        public bool IsReadOnly { get { return IsDubious || !mStream.CanWrite; } }
        public bool IsDubious { get; protected set; }

        // This indicates un-flushed data.  Currently only set when the metadata changes.
        public bool IsDirty {
            get {
                return (ChunkAccess != null && ChunkAccess.WriteCount != mLastFlushWriteCount) ||
                    mDescriptionChanged;
            }
        }

        public virtual bool IsModified {
            get {
                if (ChunkAccess != null) {
                    return ChunkAccess.IsModified;
                } else {
                    return false;
                }
            }
            set {
                // A little weird to set it to true, but that's fine.
                if (ChunkAccess != null) {
                    ChunkAccess.IsModified = value;
                }
            }
        }


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
        private bool mDescriptionChanged;
        private long mLastFlushWriteCount;

        protected class Header {
            public const int LENGTH = 84;
            public const int DISK_NAME_SIZE = 64;       // size of field, including length byte
            public const ushort EXP_PRIVATE = 0x0100;

            public byte[] mDiskName = new byte[DISK_NAME_SIZE];
            public uint mDataSize;
            public uint mTagSize;
            public uint mDataChecksum;
            public uint mTagChecksum;
            public byte mDiskFormat;
            public byte mFormatByte;
            public ushort mPrivate;

            public Header() { }

            public static Header Create(MediaKind kind) {
                Header hdr = new Header();

                // Leave mDiskName set to all zeroes.

                hdr.mPrivate = 0x100;

                switch (kind) {
                    case MediaKind.GCR_SSDD35:
                        hdr.mDataSize = 400 * 1024;
                        hdr.mTagSize = 400 * 12;
                        hdr.mDiskFormat = 0;
                        hdr.mFormatByte = 0x02;     // FTN says 0x12, but that's for Lisa?
                        break;
                    case MediaKind.GCR_DSDD35:
                        hdr.mDataSize = 800 * 1024;
                        hdr.mTagSize = 800 * 12;
                        hdr.mDiskFormat = 1;
                        hdr.mFormatByte = 0x22;     // or 0x24 for Apple IIgs (for 4:1)
                        break;
                    case MediaKind.MFM_DSDD35:
                        hdr.mDataSize = 720 * 1024;
                        hdr.mTagSize = 0;
                        hdr.mDiskFormat = 2;
                        hdr.mFormatByte = 0x22;
                        break;
                    case MediaKind.MFM_DSHD35:
                        hdr.mDataSize = 1440 * 1024;
                        hdr.mTagSize = 0;
                        hdr.mDiskFormat = 3;
                        hdr.mFormatByte = 0x22;
                        break;
                    default:
                        throw new NotImplementedException("can't create: " + kind);
                }
                return hdr;
            }

            /// <summary>
            /// Converts a value from the "diskFormat" field to the MediaKind enumeration.
            /// </summary>
            public static Defs.MediaKind FormatToKind(byte diskFormat) {
                switch (diskFormat) {
                    case 0: return MediaKind.GCR_SSDD35;
                    case 1: return MediaKind.GCR_DSDD35;
                    case 2: return MediaKind.MFM_DSDD35;
                    case 3: return MediaKind.MFM_DSHD35;
                    default: return MediaKind.Unknown;
                }
            }

            /// <summary>
            /// Converts a MediaKind to a "diskFormat" field value.
            /// </summary>
            /// <returns>Value, or -1 if the media kind is not supported.</returns>
            public static int KindToFormat(MediaKind mediaKind) {
                switch (mediaKind) {
                    case MediaKind.GCR_SSDD35: return 0;
                    case MediaKind.GCR_DSDD35: return 1;
                    case MediaKind.MFM_DSDD35: return 2;
                    case MediaKind.MFM_DSHD35: return 3;
                    default: return -1;
                }
            }

            public void Load(byte[] buf, int offset) {
                int startOffset = offset;
                for (int i = 0; i < DISK_NAME_SIZE; i++) {
                    mDiskName[i] = buf[offset++];
                }
                mDataSize = RawData.ReadU32BE(buf, ref offset);
                mTagSize = RawData.ReadU32BE(buf, ref offset);
                mDataChecksum = RawData.ReadU32BE(buf, ref offset);
                mTagChecksum = RawData.ReadU32BE(buf, ref offset);
                mDiskFormat = buf[offset++];
                mFormatByte = buf[offset++];
                mPrivate = RawData.ReadU16BE(buf, ref offset);

                Debug.Assert(offset == startOffset + LENGTH);
            }
            public void Store(byte[] buf, int offset) {
                int startOffset = offset;
                for (int i = 0; i < DISK_NAME_SIZE; i++) {
                    buf[offset++] = mDiskName[i];
                }
                RawData.WriteU32BE(buf, ref offset, mDataSize);
                RawData.WriteU32BE(buf, ref offset, mTagSize);
                RawData.WriteU32BE(buf, ref offset, mDataChecksum);
                RawData.WriteU32BE(buf, ref offset, mTagChecksum);
                buf[offset++] = mDiskFormat;
                buf[offset++] = mFormatByte;
                RawData.WriteU16BE(buf, ref offset, mPrivate);
                Debug.Assert(offset == startOffset + LENGTH);
            }
        }

        // IDiskImage-required delegate
        public static bool TestKind(Stream stream, AppHook appHook) {
            return ValidateHeader(stream, appHook, null, out Header unused1);
        }

        /// <summary>
        /// Validates the DiskCopy header.
        /// </summary>
        /// <param name="stream">File data stream.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <param name="notes">Object that receives notes.</param>
        /// <param name="hdr">Result: validated header.</param>
        /// <returns>True if this looks like a DiskCopy file.</returns>
        private static bool ValidateHeader(Stream stream, AppHook appHook, Notes? notes,
                out Header hdr) {
            hdr = new Header();

            // Read the header into memory.
            stream.Position = 0;
            if (stream.Length < Header.LENGTH) {
                return false;
            }
            byte[] hdrBuf = new byte[Header.LENGTH];
            stream.ReadExactly(hdrBuf, 0, Header.LENGTH);
            hdr.Load(hdrBuf, 0);

            // Validate fields.  Not checking "formatByte", which seems to have some variation.
            if (hdr.mPrivate != Header.EXP_PRIVATE ||
                    hdr.mDiskName[0] >= Header.DISK_NAME_SIZE) {
                return false;
            }
            if (Header.FormatToKind(hdr.mDiskFormat) == MediaKind.Unknown) {
                return false;
            }
            if (hdr.mDataSize == 0 || (hdr.mDataSize % BLOCK_SIZE) != 0 ||
                    (hdr.mTagSize % TAG_SIZE) != 0) {
                return false;
            }

            // Make sure all of the expected data is present.
            uint expectedLen = Header.LENGTH + hdr.mDataSize + hdr.mTagSize;
            if (stream.Length < expectedLen) {
                Debug.WriteLine("File looks like DiskCopy, but is too short (expected " +
                    expectedLen);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Private constructor.
        /// </summary>
        private DiskCopy(Stream stream, Header hdr, AppHook appHook) {
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
        public static DiskCopy OpenDisk(Stream stream, AppHook appHook) {
            Notes tmpNotes = new Notes();
            if (!ValidateHeader(stream, appHook, tmpNotes, out Header hdr)) {
                throw new NotSupportedException("Incompatible data stream");
            }
            DiskCopy disk = new DiskCopy(stream, hdr, appHook);
            disk.Notes.MergeFrom(tmpNotes);
            disk.mUserData = new byte[hdr.mDataSize];
            stream.Position = Header.LENGTH;
            stream.ReadExactly(disk.mUserData, 0, (int)hdr.mDataSize);
            if (hdr.mTagSize != 0) {
                disk.mTagData = new byte[hdr.mTagSize];
                stream.ReadExactly(disk.mTagData, 0, (int)hdr.mTagSize);
            }

            uint checksum = ComputeChecksum(disk.mUserData, 0, (int)hdr.mDataSize);
            if (checksum != hdr.mDataChecksum) {
                disk.Notes.AddW("Data checksum mismatch: expected 0x" +
                    hdr.mDataChecksum.ToString("x8") + ", got 0x" + checksum.ToString("x8"));
            }

            // According to https://www.discferret.com/wiki/Apple_DiskCopy_4.2, the first 12
            // bytes of the tag area are not included in the checksum, to maintain backward
            // compatibility with an older, broken version of the program.
            if (hdr.mTagSize > 12) {
                checksum = ComputeChecksum(disk.mTagData, 12, (int)hdr.mTagSize - 12);
                if (checksum != hdr.mTagChecksum) {
                    disk.Notes.AddW("Tag checksum mismatch: expected 0x" +
                        hdr.mTagChecksum.ToString("x8") + ", got 0x" + checksum.ToString("x8"));
                }
            }

            return disk;
        }

        /// <summary>
        /// Determines whether the disk configuration is supported.
        /// </summary>
        /// <param name="mediaKind">Kind of disk to create.</param>
        /// <param name="errMsg">Result: error message, or empty string on success.</param>
        /// <returns>True on success.</returns>
        public static bool CanCreateDisk(MediaKind mediaKind, out string errMsg) {
            if (Header.KindToFormat(mediaKind) < 0) {
                errMsg = "unsupported value for media kind: " + mediaKind;
                return false;
            } else {
                errMsg = string.Empty;
                return true;
            }
        }

        public static DiskCopy CreateDisk(Stream stream, MediaKind mediaKind, AppHook appHook) {
            if (!stream.CanRead || !stream.CanWrite || !stream.CanSeek) {
                throw new ArgumentException("Invalid stream capabilities");
            }
            if (!CanCreateDisk(mediaKind, out string errMsg)) {
                throw new ArgumentException(errMsg);
            }

            Header hdr = Header.Create(mediaKind);
            stream.SetLength(0);
            stream.SetLength(Header.LENGTH + hdr.mDataSize + hdr.mTagSize);
            DiskCopy disk = new DiskCopy(stream, hdr, appHook);
            try {
                disk.mUserData = new byte[hdr.mDataSize];
                disk.mTagData = new byte[hdr.mTagSize];
                disk.SetChunkAccess();
                disk.mDescriptionChanged = true;
                disk.Flush();
                return disk;
            } catch {
                disk.Dispose();
                throw;
            }
        }

        // IDiskImage
        public bool AnalyzeDisk(SectorCodec? codec, SectorOrder orderHint, AnalysisDepth depth) {
            // Discard existing FileSystem and ChunkAccess objects (filesystem will be flushed).
            CloseContents();
            if (ChunkAccess != null) {
                // We're discarding the current ChunkAccess object, which will cause us to lose
                // the signal that tells us about un-flushed changes.  So we need to check for
                // that situation here.
                if (mLastFlushWriteCount != ChunkAccess.WriteCount) {
                    // Flush the pending changes now.  Alternatively, we could set the count to
                    // long.MaxValue as a signal that our buffer is dirty, and let the write
                    // happen the next time a flush happens naturally.
                    Debug.Assert(false, "DiskCopy reanalyzed with unflushed changes");
                    Flush();
                }
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
            uint blockCount = mHeader.mDataSize / BLOCK_SIZE;
            MemoryStream userDataStream = new MemoryStream(mUserData);
            IChunkAccess chunks = new GeneralChunkAccess(userDataStream, 0, blockCount);
            ChunkAccess = new GatedChunkAccess(chunks);
            // Init our "is dirty" test.
            mLastFlushWriteCount = ChunkAccess.WriteCount;
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

        ~DiskCopy() {
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
        public void Flush() {
            if (IsReadOnly) {
                Debug.Assert(!IsDirty);
                return;
            }

            // Flush any pending filesystem changes.
            if (Contents is IFileSystem) {
                ((IFileSystem)Contents).Flush();
            } else if (Contents is IMultiPart) {
                foreach (Partition part in (IMultiPart)Contents) {
                    part.FileSystem?.Flush();
                }
            }

            if (IsDirty) {
                Debug.WriteLine("DiskCopy flush (dirty) ...");
                // Update checksums.  The checksum might not match if it was damaged before.
                uint dataChecksum = ComputeChecksum(mUserData, 0, mUserData.Length);
                uint tagChecksum = ComputeChecksum(mTagData, 0, mTagData.Length);
                if (dataChecksum != mHeader.mDataChecksum) {
                    Debug.WriteLine("DiskCopy: data checksum updated");
                    mHeader.mDataChecksum = dataChecksum;
                }
                if (tagChecksum != mHeader.mTagChecksum) {
                    Debug.WriteLine("DiskCopy: tag checksum updated");
                    mHeader.mTagChecksum = tagChecksum;
                }

                // Serialize the header and write it out.
                byte[] hdrBuf = new byte[Header.LENGTH];
                mHeader.Store(hdrBuf, 0);

                mStream.Position = 0;
                mStream.Write(hdrBuf, 0, Header.LENGTH);
                mDescriptionChanged = false;

                // If the chunk write count has been incremented, write the disk data.
                Debug.Assert(ChunkAccess != null);
                if (mLastFlushWriteCount != ChunkAccess.WriteCount) {
                    Debug.WriteLine("DiskCopy: writing disk data");
                    mStream.Write(mUserData, 0, mUserData.Length);
                    mStream.Write(mTagData, 0, mTagData.Length);
                    mLastFlushWriteCount = ChunkAccess.WriteCount;
                } else {
                    Debug.WriteLine("DiskCopy: skipping disk data write");
                }
            }

            mStream.Flush();
            Debug.Assert(!IsDirty);
        }

        /// <summary>
        /// Computes the DiskCopy 4.2 checksum.
        /// </summary>
        private static uint ComputeChecksum(byte[] data, int offset, int length) {
            uint checksum = 0;
            for (int i = 0; i < length; i += 2) {
                ushort val = RawData.GetU16BE(data, offset + i);
                checksum += val;
                // rotate right
                checksum = checksum >> 1 | ((checksum & 0x00000001) << 31);
            }
            return checksum;
        }

        #region Metadata

        private static readonly MetaEntry[] sMetaEntries = new MetaEntry[] {
            new MetaEntry("description", MetaEntry.ValType.String,
                "Disk description.", "Mac OS Roman string, 63 characters max.", canEdit: true),
        };

        // IMetadata
        public bool CanAddNewEntries => false;

        // IMetadata
        public List<IMetadata.MetaEntry> GetMetaEntries() {
            List<MetaEntry> list = new List<MetaEntry>(sMetaEntries.Length);
            foreach (MetaEntry met in sMetaEntries) {
                list.Add(met);
            }
            return list;
        }

        // IMetadata
        public string? GetMetaValue(string key, bool verbose) {
            if (key != "description") {
                return null;
            }
            return MacChar.MacToUnicode(mHeader.mDiskName, 1, mHeader.mDiskName[0],
                MacChar.Encoding.Roman);
        }

        private static byte[]? StringToDiskName(string value) {
            if (value.Length >= Header.DISK_NAME_SIZE) {
                return null;
            }
            return MacChar.UnicodeToMacStr(value, MacChar.Encoding.Roman);
        }

        // IMetadata
        public bool TestMetaValue(string key, string value) {
            if (key != "description" || value == null) {
                return false;
            }
            return StringToDiskName(value) != null;
        }

        // IMetadata
        public void SetMetaValue(string key, string value) {
            if (key == null || value == null) {
                throw new ArgumentException("key and value must not be null");
            }
            if (key != "description") {
                throw new ArgumentException("invalid key '" + key + "'");
            }
            byte[]? description = StringToDiskName(value);
            if (description == null) {
                throw new ArgumentException("invalid value '" + value + "'");
            }
            Array.Clear(mHeader.mDiskName);
            Array.Copy(description, mHeader.mDiskName, description.Length);
            mDescriptionChanged = true;
        }

        // IMetadata
        public bool DeleteMetaEntry(string key) {
            return false;
        }

        #endregion Metadata
    }
}
