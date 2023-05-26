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
using System.Text;

using CommonUtil;
using DiskArc.Multi;
using static DiskArc.Defs;
using static DiskArc.IDiskImage;
using static DiskArc.IMetadata;

// This is an IDiskImage instance, which means we modify the original file in place (vs. an
// IArchive that gets rewritten for every change).  To reduce the risk of a crash causing data
// loss, we'd ideally like to write the entire file in a single operation.
//
// The 2MG file format uses 32-bit lengths for each chunk, so the file could be up to 6GB if
// we limit ourselves to signed values.  In practice, the comment and creator data will be short
// or absent, but a 2GB data chunk for a hard drive image is entirely plausible.  We don't want
// to hold that in memory.
//
// Fortunately, the header and data chunks are fixed-size and don't move, and there's no checksum,
// so the only thing that requires special handling is updating the comment and creator data
// sections.  We can do a disk update like this:
//
//  1. Flush all changes to the data area.  For a sector image this is a no-op.
//  2. Truncate the file past the data section.
//  3. Write the updated file header.
//  4. Write the updated comment and creator data (if we didn't pull these into the object,
//     we'll need to copy them out right before the truncation).
//
// If we fail while writing the comment or creator, the header will be inconsistent with the
// file contents, but we can work around that if we need to recover the data from the file.

namespace DiskArc.Disk {
    /// <summary>
    /// 2IMG Universal Disk Image file.  This is similar to the UnadornedSector and
    /// UnadornedNibble525 formats, but it has a fixed-length header that defines the file
    /// ordering, and a place to put a comment.
    /// </summary>
    /// <remarks>
    /// 2IMG files can hold either sector data or nibble data.  We only want to present one
    /// set of interfaces, so we use a subclass for the nibble form.
    /// </remarks>
    public class TwoIMG : IDiskImage, IMetadata {
        public static readonly byte[] SIGNATURE = new byte[] { 0x32, 0x49, 0x4d, 0x47 };
        public const int MAX_COMMENT_LEN = 256 * 1024;      // should be a few bytes, this is huge
        public const int MAX_CREATOR_DATA_LEN = 256 * 1024;
        private const char COMMENT_EOL = '\r';

        private static readonly byte[] CREATOR_CPII = new byte[] { 0x43, 0x50, 0x49, 0x49 };
        private static readonly byte[] CREATOR_WOOF = new byte[] { 0x57, 0x4f, 0x4f, 0x46 };

        //
        // IDiskImage properties.
        //

        public bool IsReadOnly { get { return IsDubious || !mStream.CanWrite; } }
        public bool IsDubious { get; protected set; }

        // Data changes are written straight through, so our state is only dirty if the comment or
        // creator data changes.
        public virtual bool IsDirty { get { return mMetadataDirty; } }

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
        // Metadata properties.
        //

        private const uint VOL_FLAG = 0x0100;
        private const uint WRITE_PROT_FLAG = 0x80000000;

        /// <summary>
        /// Disk volume number (0-254), or -1 if not set.
        /// </summary>
        public int VolumeNumber {
            get {
                if ((mHeader.mFlags & 0x0100) != 0) {
                    // volume # flag is set, volume number is in low 8 bits
                    return (byte)mHeader.mFlags;
                } else {
                    // volume # not specified, return default
                    return -1;
                }
            }
            set {
                CheckMetaWriteAccess();
                if (value == -1) {
                    // Zero out the volume number in the low 8 bits, and clear the "is set" flag.
                    mHeader.mFlags = mHeader.mFlags & ~(VOL_FLAG | 0xff);
                    mMetadataDirty = true;
                } else if (value >= 0 && value <= 254) {
                    // Set the "is set" flag and put the volume number in the low 8 bits.
                    mHeader.mFlags = (uint)(mHeader.mFlags & ~0xff) | VOL_FLAG | (byte)value;
                    mMetadataDirty = true;
                } else {
                    throw new ArgumentException("volume number must be 0-254");
                }
            }
        }

        /// <summary>
        /// Write-protected flag.
        /// </summary>
        public bool WriteProtected {
            get { return (mHeader.mFlags & WRITE_PROT_FLAG) != 0; }
            set {
                CheckMetaWriteAccess();
                if (value) {
                    mHeader.mFlags |= WRITE_PROT_FLAG;
                } else {
                    mHeader.mFlags &= ~WRITE_PROT_FLAG;
                }
                mMetadataDirty = true;
            }
        }

        /// <summary>
        /// Image format: 0=DOS order, 1=ProDOS order, 2=nibble
        /// </summary>
        public int ImageFormat {
            get { return (int)mHeader.mImageFormat; }
        }

        /// <summary>
        /// Four-character creator signature string.
        /// </summary>
        /// <remarks>
        /// If this is changed, the creator data should be discarded.
        /// </remarks>
        public string Creator {
            get { return Encoding.ASCII.GetString(mHeader.mCreator); }
            set {
                CheckMetaWriteAccess();
                if (value.Length != 4) {
                    throw new ArgumentException("Creator string must be 4 chars");
                }
                for (int i = 0; i < 4; i++) {
                    char ch = value[i];
                    if (ch < 0x20 || ch >= 0x7f) {
                        // Don't necessarily need to enforce this, but I think it's prudent.
                        throw new ArgumentException("Creator string must be ASCII");
                    }
                    mHeader.mCreator[i] = (byte)ch;
                }
                mMetadataDirty = true;
            }
        }

        /// <summary>
        /// Optional comment.  This is a multi-line ASCII string that fills the entire buffer.
        /// </summary>
        /// <remarks>
        /// Changes here aren't reflected in mHeader until the data is flushed.  This copies
        /// the data to avoid exposing the array.
        /// </remarks>
        public byte[] RawComment {
            get {
                byte[] result = new byte[mRawComment.Length];
                Array.Copy(mRawComment, result, result.Length);
                return result;
            }
            set {
                CheckMetaWriteAccess();
                if (value.Length > MAX_COMMENT_LEN) {
                    throw new ArgumentException("Invalid comment");
                }
                mRawComment = new byte[value.Length];
                Array.Copy(value, mRawComment, value.Length);
                mMetadataDirty = true;
            }
        }
        private byte[] mRawComment;

        /// <summary>
        /// Optional comment.  This is a multi-line string, with EOL markers adjusted to match
        /// the host system.
        /// </summary>
        /// <remarks>
        /// This is requested infrequently, so we don't cache the stringified form.
        /// </remarks>
        public string Comment {
            get { return CommentFromRaw(mRawComment); }
            set {
                CheckMetaWriteAccess();
                mRawComment = CommentToRaw(value);
                mMetadataDirty = true;
            }
        }

        /// <summary>
        /// Optional creator data.  Arbitrary format, determined by creator signature.
        /// </summary>
        /// <remarks>
        /// Changes here aren't reflected in mHeader until the data is flushed.  This copies
        /// the data to avoid exposing the array.
        /// </remarks>
        public byte[] CreatorData {
            get {
                byte[] result = new byte[mCreatorData.Length];
                Array.Copy(mCreatorData, result, result.Length);
                return result;
            }
            set {
                CheckMetaWriteAccess();
                if (value.Length > MAX_CREATOR_DATA_LEN) {
                    throw new ArgumentException("Invalid creator data");
                }
                mCreatorData = new byte[value.Length];
                Array.Copy(value, mCreatorData, value.Length);
                mMetadataDirty = true;
            }
        }
        private byte[] mCreatorData;

        private void CheckMetaWriteAccess() {
            if (IsReadOnly) {
                throw new IOException("Cannot set metadata: disk image is read-only");
            }
            Debug.Assert(!IsDubious);
        }

        //
        // Innards.
        //

        protected Stream mStream;
        protected AppHook mAppHook;
        protected bool mMetadataDirty;
        internal Header mHeader;

        /// <summary>
        /// 2IMG header.
        /// </summary>
        /// <remarks>
        /// <para>Field names are based on the original magnet.ch specification.</para>
        /// </remarks>
        internal class Header {
            public const int LENGTH = 64;

            public byte[] mMagic = new byte[4];
            public byte[] mCreator = new byte[4];
            public ushort mHeaderLen;
            public ushort mVersion;
            public uint mImageFormat;
            public uint mFlags;
            public uint mNumBlocks;
            public uint mDataOffset;
            public uint mDataLen;
            public uint mCmntOffset;
            public uint mCmntLen;
            public uint mCreatorOffset;
            public uint mCreatorLen;
            public byte[] mSpare = new byte[16];

            public enum ImageFormat { DOS_Floppy = 0, ProDOS_Disk = 1, Nibble_Floppy = 2 };

            /// <summary>
            /// Creates a Header object for a new disk image.
            /// </summary>
            /// <param name="format">Image format.</param>
            /// <param name="dataLen">Data chunk length.</param>
            /// <returns>Newly-created object.</returns>
            public static Header Create(ImageFormat format, int dataLen) {
                Header hdr = new Header();
                Array.Copy(SIGNATURE, hdr.mMagic, SIGNATURE.Length);
                Array.Copy(CREATOR_CPII, hdr.mCreator, CREATOR_CPII.Length);
                hdr.mHeaderLen = LENGTH;
                hdr.mVersion = 1;
                hdr.mImageFormat = (ushort)format;
                hdr.mDataOffset = LENGTH;
                if (format == ImageFormat.ProDOS_Disk) {
                    Debug.Assert(dataLen % BLOCK_SIZE == 0);
                    hdr.mNumBlocks = (uint)(dataLen / BLOCK_SIZE);
                } else if (format == ImageFormat.DOS_Floppy) {
                    Debug.Assert(dataLen % SECTOR_SIZE == 0);
                } else if (format == ImageFormat.Nibble_Floppy) {
                    Debug.Assert(dataLen % TwoIMG_Nibble.NIB_TRACK_LENGTH == 0);
                }
                hdr.mDataLen = (uint)dataLen;
                // All other fields are zero.
                return hdr;
            }

            public void Load(byte[] buf, int offset) {
                int startOffset = offset;
                for (int i = 0; i < 4; i++) {
                    mMagic[i] = buf[offset++];
                }
                for (int i = 0; i < 4; i++) {
                    mCreator[i] = buf[offset++];
                }
                mHeaderLen = RawData.ReadU16LE(buf, ref offset);
                mVersion = RawData.ReadU16LE(buf, ref offset);
                mImageFormat = RawData.ReadU32LE(buf, ref offset);
                mFlags = RawData.ReadU32LE(buf, ref offset);
                mNumBlocks = RawData.ReadU32LE(buf, ref offset);
                mDataOffset = RawData.ReadU32LE(buf, ref offset);
                mDataLen = RawData.ReadU32LE(buf, ref offset);
                mCmntOffset = RawData.ReadU32LE(buf, ref offset);
                mCmntLen = RawData.ReadU32LE(buf, ref offset);
                mCreatorOffset = RawData.ReadU32LE(buf, ref offset);
                mCreatorLen = RawData.ReadU32LE(buf, ref offset);
                for (int i = 0; i < 16; i++) {
                    mSpare[i] = buf[offset++];
                }
                Debug.Assert(offset == startOffset + LENGTH);
            }
            public void Store(byte[] buf, int offset) {
                int startOffset = offset;
                for (int i = 0; i < 4; i++) {
                    buf[offset++] = mMagic[i];
                }
                for (int i = 0; i < 4; i++) {
                    buf[offset++] = mCreator[i];
                }
                RawData.WriteU16LE(buf, ref offset, mHeaderLen);
                RawData.WriteU16LE(buf, ref offset, mVersion);
                RawData.WriteU32LE(buf, ref offset, mImageFormat);
                RawData.WriteU32LE(buf, ref offset, mFlags);
                RawData.WriteU32LE(buf, ref offset, mNumBlocks);
                RawData.WriteU32LE(buf, ref offset, mDataOffset);
                RawData.WriteU32LE(buf, ref offset, mDataLen);
                RawData.WriteU32LE(buf, ref offset, mCmntOffset);
                RawData.WriteU32LE(buf, ref offset, mCmntLen);
                RawData.WriteU32LE(buf, ref offset, mCreatorOffset);
                RawData.WriteU32LE(buf, ref offset, mCreatorLen);
                for (int i = 0; i < 16; i++) {
                    mSpare[i] = buf[offset++];
                }
                Debug.Assert(offset == startOffset + LENGTH);
            }
        }

        // IDiskImage-required delegate
        public static bool TestKind(Stream stream, AppHook appHook) {
            return ValidateHeader(stream, appHook, null, out Header unused1, out bool unused2);
        }

        /// <summary>
        /// Validates the 2IMG header.
        /// </summary>
        /// <param name="stream">File data stream.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <param name="notes">Object that receives notes.</param>
        /// <param name="hdr">Result: validated header.</param>
        /// <param name="isDubious">Result: set if some damage was found.</param>
        /// <returns>True if everything looks good.</returns>
        private static bool ValidateHeader(Stream stream, AppHook appHook, Notes? notes,
                out Header hdr, out bool isDubious) {
            hdr = new Header();
            isDubious = false;

            // Read the header into memory.
            stream.Position = 0;
            if (stream.Length < Header.LENGTH) {
                return false;
            }
            byte[] hdrBuf = new byte[Header.LENGTH];
            stream.ReadExactly(hdrBuf, 0, Header.LENGTH);
            hdr.Load(hdrBuf, 0);

            // Check signature bytes.
            if (!RawData.CompareBytes(hdr.mMagic, SIGNATURE, SIGNATURE.Length)) {
                return false;
            }

            // It's got the 2IMG signature, so it's probably one of ours.  Validate the
            // various fields.
            if (RawData.CompareBytes(hdr.mCreator, CREATOR_WOOF, CREATOR_WOOF.Length) &&
                    hdr.mDataLen == 0 &&
                    hdr.mImageFormat != (uint)Header.ImageFormat.Nibble_Floppy) {
                // There are some bad disk images floating around.
                appHook.LogI("TwoIMG: Fixing zero-datalen 'WOOF' image");
                hdr.mDataLen = hdr.mNumBlocks * BLOCK_SIZE;
            }

            if (hdr.mHeaderLen < Header.LENGTH) {
                appHook.LogW("TwoIMG: rejecting image with short declared header length");
                return false;
            }
            if (hdr.mVersion != 1) {
                appHook.LogW("TwoIMG: rejecting image with unknown version (" + hdr.mVersion + ")");
            }
            if (hdr.mDataOffset == 0 || hdr.mDataLen == 0) {
                appHook.LogW("TwoIMG: rejecting image with no data");
                return false;
            }
            if (hdr.mNumBlocks != 0 && hdr.mDataLen != hdr.mNumBlocks * BLOCK_SIZE) {
                if (hdr.mImageFormat == (uint)Header.ImageFormat.ProDOS_Disk) {
                    appHook.LogW("TwoIMG: rejecting image with inconsistent block/data len");
                    return false;
                } else {
                    // The num_blocks field isn't required to be valid for anything but ProDOS.
                    // It should be zero for other formats, but isn't always, e.g. for nibble
                    // images it may be set to 280.  We could set it to zero here if we wanted
                    // to "clean" the image.
                    appHook.LogI("TwoIMG: num_blocks field doesn't match data_len on non-ProDOS");
                }
            }
            if (hdr.mDataOffset < hdr.mHeaderLen) {
                appHook.LogW("TwoIMG: rejecting image with header/data overlap");
                return false;
            }
            long expectedLen = hdr.mDataOffset + hdr.mDataLen;
            if (expectedLen > stream.Length) {
                appHook.LogW("TwoIMG: file truncated (data overrun)");
                return false;
            }

            // Check format-specific conditions.
            switch ((Header.ImageFormat)hdr.mImageFormat) {
                case Header.ImageFormat.DOS_Floppy:
                    // This is meant for 5.25" DOS disks.  For disks of other sizes, "DOS order"
                    // has no meaning, though technically anything that is a multiple of 4096
                    // bytes can be handled as a 16-sector disk with a large number of tracks.
                    if (hdr.mDataLen % (16 * SECTOR_SIZE) != 0) {
                        appHook.LogW("TwoIMG: rejecting DOS-order image with odd size");
                        return false;
                    }
                    if (hdr.mDataLen != 35 * 16 * SECTOR_SIZE &&
                            hdr.mDataLen != 40 * 16 * SECTOR_SIZE) {
                        // Unusual, but not invalid.
                        appHook.LogI("TwoIMG: found DOS image that isn't 140K or 160K");
                    }
                    break;
                case Header.ImageFormat.ProDOS_Disk:
                    if (hdr.mNumBlocks == 0) {
                        appHook.LogW("TwoIMG: rejecting ProDOS image without block size value");
                        return false;       // this isn't strictly necessary; mDataLen will work
                    }
                    if (hdr.mDataLen % BLOCK_SIZE != 0) {
                        appHook.LogW("TwoIMG: rejecting ProDOS image with non-512 size");
                        return false;
                    }
                    break;
                case Header.ImageFormat.Nibble_Floppy:
                    // Must be a 35-track nibble image with 6656 bytes/track.
                    if (hdr.mDataLen != TwoIMG_Nibble.NUM_TRACKS * TwoIMG_Nibble.NIB_TRACK_LENGTH) {
                        appHook.LogW("TwoIMG: rejecting nibble image with unexpected size");
                        return false;
                    }
                    break;
                default:
                    appHook.LogW("TwoIMG: rejecting image with unknown format: " +
                        hdr.mImageFormat);
                    return false;
            }

            // We have what looks like valid file data.  If the comment or creator fields are
            // messed up, we want to flag the file as dubious, but we don't have to abandon it.

            if (hdr.mCmntOffset != 0 && hdr.mCmntLen != 0) {
                if (hdr.mCmntOffset < hdr.mDataOffset + hdr.mDataLen) {
                    notes?.AddE("Comment starts before end of data");
                    hdr.mCmntOffset = hdr.mCmntLen = 0;     // prevent attempts to read
                    isDubious = true;                       // disallow writes
                }
                if (hdr.mCmntLen > MAX_COMMENT_LEN) {
                    notes?.AddE("Comment is huge (" + hdr.mCmntLen + ")");
                    hdr.mCmntOffset = hdr.mCmntLen = 0;     // don't try to load into a string
                    isDubious = true;
                }
                expectedLen = hdr.mCmntOffset + hdr.mCmntLen;
                if (expectedLen > stream.Length) {
                    notes?.AddE("Comment overruns end of file");
                    hdr.mCmntOffset = hdr.mCmntLen = 0;
                    isDubious = true;
                }
            }
            if (hdr.mCreatorOffset != 0 && hdr.mCreatorLen != 0) {
                if (hdr.mCreatorOffset < hdr.mDataOffset + hdr.mDataLen) {
                    notes?.AddE("Creator data starts before end of data");
                    hdr.mCreatorOffset = hdr.mCreatorLen = 0;
                    isDubious = true;
                }
                if (hdr.mCmntOffset != 0 && hdr.mCreatorOffset < hdr.mCmntOffset + hdr.mCmntLen) {
                    notes?.AddE("Creator data starts before end of comment");
                    hdr.mCreatorOffset = hdr.mCreatorLen = 0;
                    isDubious = true;
                }
                if (hdr.mCreatorLen > MAX_CREATOR_DATA_LEN) {
                    notes?.AddE("Creator data is huge (" + hdr.mCreatorLen + ")");
                    hdr.mCreatorOffset = hdr.mCreatorLen = 0;
                    isDubious = true;
                }
                expectedLen = hdr.mCreatorOffset + hdr.mCreatorLen;
                if (expectedLen > stream.Length) {
                    notes?.AddE("Creator data overruns end of file");
                    hdr.mCreatorOffset = hdr.mCreatorLen = 0;
                    isDubious = true;
                }
            }
            if (expectedLen != stream.Length) {
                notes?.AddI("Found extra data at end of file (" +
                    (stream.Length - expectedLen) + ")");
                // We will lose the excess data if the file is updated.  Could be worth setting
                // IsDubious, but most likely this is just some disk tool failing to truncate
                // the file after editing a comment.
            }

            return true;
        }

        /// <summary>
        /// Private constructor.
        /// </summary>
        internal TwoIMG(Stream stream, Header hdr, AppHook appHook) {
            mStream = stream;
            mHeader = hdr;
            mAppHook = appHook;

            // Read the comment, if the file has one.
            if (hdr.mCmntOffset != 0 && hdr.mCmntLen != 0) {
                mRawComment = new byte[hdr.mCmntLen];
                mStream.Position = hdr.mCmntOffset;
                mStream.ReadExactly(mRawComment, 0, (int)hdr.mCmntLen);

#if DEBUG
                string comment = CommentFromRaw(mRawComment);
                byte[] conv = CommentToRaw(comment);
                if (mRawComment.Length != conv.Length ||
                        !RawData.CompareBytes(mRawComment, conv, conv.Length)) {
                    // This can happen if the comment has non-ASCII junk in it, or used LF or
                    // CRLF for EOL markers (CiderPress uses CRLF), but it's unexpected for
                    // valid data.
                    Debug.WriteLine("TwoIMG: comment conversion is non-reversible");
                }
#endif
            } else {
                mRawComment = RawData.EMPTY_BYTE_ARRAY;
            }

            // Read the creator data, if present.
            if (hdr.mCreatorOffset != 0 && hdr.mCreatorLen != 0) {
                mCreatorData = new byte[hdr.mCreatorLen];
                mStream.Position = hdr.mCreatorOffset;
                mStream.ReadExactly(mCreatorData, 0, (int)hdr.mCreatorLen);
            } else {
                mCreatorData = RawData.EMPTY_BYTE_ARRAY;
            }
        }

        /// <summary>
        /// Open a disk image file.
        /// </summary>
        /// <param name="stream">Disk image data stream.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <exception cref="NotSupportedException">Incompatible data stream.</exception>
        public static TwoIMG OpenDisk(Stream stream, AppHook appHook) {
            Notes notes = new Notes();
            if (!ValidateHeader(stream, appHook, notes, out Header hdr, out bool isDubious)) {
                throw new NotSupportedException("Incompatible data stream");
            }
            TwoIMG result;
            if (hdr.mImageFormat == (uint)Header.ImageFormat.Nibble_Floppy) {
                result = TwoIMG_Nibble.OpenNibbleDisk(stream, hdr, appHook);
            } else {
                result = new TwoIMG(stream, hdr, appHook);
            }
            result.Notes.MergeFrom(notes);
            result.IsDubious = isDubious;
            return result;
        }

        /// <summary>
        /// Creates a new disk image as a collection of 16-sector tracks in DOS order.
        /// </summary>
        /// <remarks>
        /// <para>The disk will be zeroed out.  It will have an IChunkAccess configured for the
        /// specified file order, but no filesystem.</para>
        /// </remarks>
        /// <param name="stream">Stream into which the new image will be written.  The stream
        ///   must be readable, writable, and seekable.</param>
        /// <param name="numTracks">Number of tracks, must be > 0.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <returns>Disk image object.</returns>
        public static TwoIMG CreateDOSSectorImage(Stream stream, uint numTracks,
                AppHook appHook) {
            if (!stream.CanRead || !stream.CanWrite || !stream.CanSeek) {
                throw new ArgumentException("Invalid stream capabilities");
            }
            if (numTracks <= 0 || numTracks > 255) {
                throw new ArgumentException("Invalid number of tracks: " + numTracks);
            }

            // Set the length of the stream.
            int dataLen = (int)numTracks * 16 * SECTOR_SIZE;
            stream.SetLength(0);
            stream.SetLength(Header.LENGTH + dataLen);

            // Create the header and write it to the stream.
            Header hdr = Header.Create(Header.ImageFormat.DOS_Floppy, dataLen);
            byte[] buf = new byte[Header.LENGTH];
            hdr.Store(buf, 0);
            stream.Position = 0;
            stream.Write(buf, 0, buf.Length);

            TwoIMG disk = new TwoIMG(stream, hdr, appHook);
            disk.ChunkAccess = new GatedChunkAccess(new GeneralChunkAccess(stream,
                Header.LENGTH, numTracks, 16, SectorOrder.DOS_Sector));
            return disk;
        }

        /// <summary>
        /// Creates a new disk image as a collection of 512-byte blocks.
        /// </summary>
        /// <remarks>
        /// <para>The disk will be zeroed out.  It will have an IChunkAccess configured for
        /// ProDOS block order, but no filesystem.</para>
        /// </remarks>
        /// <param name="stream">Stream into which the new image will be written.  The stream
        ///   must be readable, writable, and seekable.</param>
        /// <param name="numBlocks">Number of blocks in the disk image.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <returns>Disk image object.</returns>
        public static TwoIMG CreateProDOSBlockImage(Stream stream, uint numBlocks,
                AppHook appHook) {
            if (!stream.CanRead || !stream.CanWrite || !stream.CanSeek) {
                throw new ArgumentException("Invalid stream capabilities");
            }
            if (numBlocks == 0 || numBlocks >= 8 * 1024 * 1024) {       // arbitrary 4GB limit
                throw new ArgumentOutOfRangeException("block count: " + numBlocks);
            }

            // Set the length of the stream.
            int dataLen = (int)numBlocks * BLOCK_SIZE;
            stream.SetLength(0);
            stream.SetLength(Header.LENGTH + dataLen);

            // Create the header and write it to the stream.
            Header hdr = Header.Create(Header.ImageFormat.ProDOS_Disk, dataLen);
            byte[] buf = new byte[Header.LENGTH];
            hdr.Store(buf, 0);
            stream.Position = 0;
            stream.Write(buf, 0, buf.Length);

            TwoIMG disk = new TwoIMG(stream, hdr, appHook);
            disk.ChunkAccess = new GatedChunkAccess(
                new GeneralChunkAccess(stream, Header.LENGTH, numBlocks));
            return disk;
        }

        /// <summary>
        /// Creates a new 35-track nibble image, with 6656 bytes per track.,
        /// </summary>
        /// <param name="stream">Stream into which the new image will be written.  The stream
        ///   must be readable, writable, and seekable.</param>
        /// <param name="codec">Disk sector codec.</param>
        /// <param name="volume">Volume number to use for low-level format.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <returns>Disk image reference.</returns>
        public static TwoIMG CreateNibbleImage(Stream stream, SectorCodec codec,
                byte volume, AppHook appHook) {
            return TwoIMG_Nibble.CreateDisk(stream, codec, volume, appHook);
        }

        // IDiskImage
        public virtual bool AnalyzeDisk(SectorCodec? codec, SectorOrder orderHint,
                AnalysisDepth depth) {
            // Discard existing FileSystem and ChunkAccess objects (filesystem will be flushed).
            if (Contents is IFileSystem) {
                ((IFileSystem)Contents).Dispose();
            } else if (Contents is IMultiPart) {
                ((IMultiPart)Contents).Dispose();
            }
            Contents = null;
            if (ChunkAccess != null) {
                ChunkAccess.AccessLevel = GatedChunkAccess.AccessLvl.Closed;
                ChunkAccess = null;
            }

            GeneralChunkAccess chunks;
            if (mHeader.mImageFormat == (int)Header.ImageFormat.DOS_Floppy) {
                // DOS-ordered sectors.  The validation code ensures we have 16-sector tracks.
                const int TRACK_LEN = SECTOR_SIZE * 16;
                Debug.Assert(mHeader.mDataLen % TRACK_LEN == 0);
                uint numTracks = mHeader.mDataLen / TRACK_LEN;
                chunks = new GeneralChunkAccess(mStream, mHeader.mDataOffset, numTracks, 16,
                    SectorOrder.DOS_Sector);
            } else if (mHeader.mImageFormat == (int)Header.ImageFormat.ProDOS_Disk) {
                Debug.Assert(mHeader.mDataLen % BLOCK_SIZE == 0);
                if (mHeader.mDataLen == 35 * 16 * SECTOR_SIZE) {
                    // Create this as a track/sector image so that we can use sector utilities.
                    chunks = new GeneralChunkAccess(mStream, mHeader.mDataOffset, 35, 16,
                        SectorOrder.ProDOS_Block);
                } else {
                    chunks = new GeneralChunkAccess(mStream, mHeader.mDataOffset,
                        mHeader.mDataLen / BLOCK_SIZE);
                }
            } else if (mHeader.mImageFormat == (int)Header.ImageFormat.Nibble_Floppy) {
                Debug.Assert(false, "should be in subclass");
                return false;
            } else {
                Debug.Assert(false, "unknown image format " + mHeader.mImageFormat);
                return false;
            }
            if (IsDubious) {
                chunks.ReduceAccess();
            }

            ChunkAccess = new GatedChunkAccess(chunks);
            if (depth == AnalysisDepth.ChunksOnly) {
                return true;
            }

            if (!FileAnalyzer.AnalyzeFileSystem(chunks, true, mAppHook,
                    out IDiskContents? contents)) {
                return false;
            }
            // Got the disk layout, disable direct chunk access.
            Contents = contents;
            ChunkAccess.AccessLevel = GatedChunkAccess.AccessLvl.ReadOnly;
            return true;
        }

        ~TwoIMG() {
            Dispose(false);     // cleanup check
        }
        public void Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        protected virtual void Dispose(bool disposing) {
            if (disposing) {
                Flush();
                if (Contents is IFileSystem) {
                    ((IFileSystem)Contents).Dispose();
                } else if (Contents is IMultiPart) {
                    ((IMultiPart)Contents).Dispose();
                }
                Contents = null;
            } else {
                Debug.Assert(false, "GC disposing " + GetType().Name + ", dirty=" + IsDirty +
                    " created:\r\n" + mCreationStackTrace);
            }
        }
#if DEBUG
        protected string mCreationStackTrace = Environment.StackTrace + Environment.NewLine + "---";
#else
        protected string mCreationStackTrace = "  (stack trace not enabled)";
#endif

        // IDiskImage
        public virtual void Flush() {
            // Flush any pending filesystem changes.
            if (Contents is IFileSystem) {
                ((IFileSystem)Contents).Flush();
            } else if (Contents is IMultiPart) {
                foreach (Partition part in (IMultiPart)Contents) {
                    part.FileSystem?.Flush();
                }
            }

            if (!IsDirty) {
                return;
            }

            // Write header, comment, creator data.
            WriteHeaderFooter();
            mStream.Flush();
            Debug.Assert(!IsDirty);
        }

        /// <summary>
        /// Writes the 2IMG header and the optional comment and creator-data sections.
        /// </summary>
        protected void WriteHeaderFooter() {
            long postDataOffset = mHeader.mDataOffset + mHeader.mDataLen;

            // Add/remove/update comment offset+length in the header.
            mHeader.mCmntLen = (uint)mRawComment.Length;
            if (mRawComment.Length == 0) {
                mHeader.mCmntOffset = 0;
            } else {
                mHeader.mCmntOffset = mHeader.mDataOffset + mHeader.mDataLen;
                postDataOffset = mHeader.mCmntOffset + mHeader.mCmntLen;
            }

            // Add/remove/update creator data offset+length in the header.
            mHeader.mCreatorLen = (uint)mCreatorData.Length;
            if (mCreatorData.Length == 0) {
                mHeader.mCreatorOffset = 0;
            } else {
                mHeader.mCreatorOffset = (uint)postDataOffset;
            }

            byte[] hdrBuf = new byte[Header.LENGTH];
            mHeader.Store(hdrBuf, 0);

            // Update the file.  Start by truncating it to the end of the data area, then
            // writing the header.
            mStream.SetLength(mHeader.mDataOffset + mHeader.mDataLen);
            mStream.Position = 0;
            mStream.Write(hdrBuf, 0, hdrBuf.Length);

            // Move past the data, and write the comment if we have one.
            mStream.Position = mHeader.mDataOffset + mHeader.mDataLen;
            if (mHeader.mCmntOffset > 0) {
                Debug.Assert(mStream.Position == mHeader.mCmntOffset);
                mStream.Write(mRawComment, 0, mRawComment.Length);
            }

            // Write the creator data if we have that.
            if (mHeader.mCreatorOffset > 0) {
                Debug.Assert(mStream.Position == mHeader.mCreatorOffset);
                mStream.Write(mCreatorData, 0, mCreatorData.Length);
            }

            mMetadataDirty = false;
        }

        /// <summary>
        /// Converts raw data from the comment area to a string.  The data is expected to be
        /// plain ASCII, so we just need to normalize the end-of-line markers for the host.
        /// </summary>
        private static string CommentFromRaw(byte[] data) {
            string newline = Environment.NewLine;
            StringBuilder sb = new StringBuilder();
            bool lastWasCR = false;
            foreach (byte b in data) {
                if (b >= 0x20 && b < 0x7f) {
                    sb.Append((char)b);
                } else if (b >= 0x80) {
                    // Unexpected; treat as MOR?  High ASCII?
                    sb.Append('?');
                } else {
                    if (b == '\r') {
                        sb.Append(newline);
                    } else if (b == '\n') {
                        if (!lastWasCR) {
                            sb.Append(newline);
                        }
                    } else {
                        // Convert control codes to Control Pictures.
                        sb.Append(ASCIIUtil.MakePrintable((char)b));
                    }
                }
                lastWasCR = (b == '\r');
            }
            return sb.ToString();
        }

        /// <summary>
        /// Converts string data to raw.
        /// </summary>
        private static byte[] CommentToRaw(string str) {
            // The conversion is 1:1 except for CRLF -> CR.  Count up CRLF instances to get an
            // accurate count for the buffer size.
            bool lastWasCR = false;
            int count = 0;
            foreach (char ch in str) {
                if (ch == '\n' && lastWasCR) {
                    // don't count this one
                } else {
                    count++;
                }
                lastWasCR = (ch == '\r');
            }

            byte[] result = new byte[count];
            int index = 0;
            foreach (char ch in str) {
                if (ch == '\n' && lastWasCR) {
                    // skip this
                } else {
                    if (ch == '\r' || ch == '\n') {
                        result[index++] = (byte)COMMENT_EOL;
                    } else {
                        if (ch < 0x20 || ch >= 0x7f) {
                            // Convert Control Pictures back to control codes.  Leave control
                            // codes alone.
                            result[index++] = (byte)(ASCIIUtil.MakeUnprintable(ch) & 0x7f);
                        } else {
                            // Convert all values to ASCII.
                            result[index++] = (byte)ASCIIUtil.ReduceToASCII(ch, '?');
                        }
                    }
                }
                lastWasCR = (ch == '\r');
            }
            return result;
        }

        #region Metadata

        private static readonly MetaEntry[] sTwoIMGEntries = new MetaEntry[] {
            new MetaEntry("comment", MetaEntry.ValType.String, canEdit:true),
            new MetaEntry("creator", MetaEntry.ValType.String, canEdit:false),
            new MetaEntry("format", MetaEntry.ValType.Int, canEdit:false),
            new MetaEntry("write_protected", MetaEntry.ValType.Bool, canEdit:true),
            new MetaEntry("volume_number", MetaEntry.ValType.Int, canEdit:true),
        };

        // IMetadata
        public List<MetaEntry> GetMetaEntries() {
            List<MetaEntry> list = new List<MetaEntry>(sTwoIMGEntries.Length);
            foreach (MetaEntry met in sTwoIMGEntries) {
                list.Add(met);
            }
            return list;
        }

        // IMetadata
        public string? GetMetaValue(string key, bool verbose) {
            string? value;
            switch (key) {
                case "comment":
                    value = Comment;      // may be multi-line
                    break;
                case "creator":
                    value = Creator;
                    break;
                case "format":
                    value = ImageFormat.ToString();
                    if (verbose) {
                        switch (ImageFormat) {
                            case 0:
                                value += " (DOS order)";
                                break;
                            case 1:
                                value += " (ProDOS order)";
                                break;
                            case 2:
                                value += " (nibbles)";
                                break;
                            default:
                                value += " (?)";
                                break;
                        }
                    }
                    break;
                case "write_protected":
                    value = WriteProtected ? "true" : "false";  // all lower case
                    break;
                case "volume_number":
                    int volNum = VolumeNumber;
                    if (volNum < 0) {
                        if (verbose) {
                            value = "-1 (not set)";
                        } else {
                            value = "-1";
                        }
                    } else {
                        value = volNum.ToString();
                    }
                    break;
                default:
                    Debug.WriteLine("Key " + key + " not found");
                    value = null;
                    break;
            }
            return value;
        }

        // IMetadata
        public void SetMetaValue(string key, string value) {
            switch (key) {
                case "comment":
                    Comment = value;
                    break;
                case "write_protected":
                    if (!bool.TryParse(value, out bool wpVal)) {
                        throw new ArgumentException("invalid value '" + value + "'");
                    }
                    WriteProtected = wpVal;
                    break;
                case "volume_number":
                    if (!int.TryParse(value, out int intVal)) {
                        throw new ArgumentException("invalid value '" + value + "'");
                    }
                    VolumeNumber = intVal;
                    break;
                default:
                    throw new ArgumentException("unable to modify '" + key + "'");
            }
        }

        // IMetadata
        public bool DeleteMetaEntry(string key) {
            switch (key) {
                case "comment":
                    Comment = string.Empty;
                    return true;
                case "volume_number":
                    VolumeNumber = -1;
                    return true;
                default:
                    return false;
            }
        }

        #endregion Metadata
    }
}
