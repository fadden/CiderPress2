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
using System.Diagnostics.CodeAnalysis;

using CommonUtil;
using DiskArc.Multi;
using static DiskArc.Defs;
using static DiskArc.IDiskImage;

namespace DiskArc.Disk {
    /// <summary>
    /// 2IMG file with nibble data.
    /// </summary>
    /// <remarks>
    /// <para>This class should generally not be accessed directly.  The TwoIMG OpenDisk()
    /// method should be used to open all images.  It will create an object of the appropriate
    /// type.</para>
    /// </remarks>
    public class TwoIMG_Nibble : TwoIMG, INibbleDataAccess {
        public const int NIB_TRACK_LENGTH = 6656;
        internal const int NUM_TRACKS = 35;

        private static readonly NibCharacteristics sCharacteristics = new NibCharacteristics(
            name: "2IMG (nibble)",
            isByteAligned: true,
            isFixedLength: true,
            hasPartial525Tracks: false);

        //
        // IDiskImage property overrides.
        //

        public override bool IsDirty { get { return mBufferModFlag.IsSet || mMetadataDirty; } }

        public override bool IsModified {
            get {
                // Capture dirty flags.
                if (mBufferModFlag.IsSet) {
                    mIsModified = true;
                }
                // Check chunk status.
                if (ChunkAccess != null && ChunkAccess.IsModified) {
                    mIsModified = true;
                }
                return mIsModified;
            }
            set {
                // Clear our flag.  If the bits are dirty, the flag will get set again next query.
                mIsModified = value;
                if (value == false && ChunkAccess != null) {
                    // Clear the "is modified" flag in the chunk.  DO NOT touch dirty flags.
                    ChunkAccess.IsModified = false;
                }
            }
        }
        private bool mIsModified;

        //
        // INibbleDataAccess properties.
        //

        public NibCharacteristics Characteristics => sCharacteristics;

        public MediaKind DiskKind => MediaKind.GCR_525;

        //
        // Innards.
        //

        /// <summary>
        /// Modification flag, shared with the CircularBitBuffer instances for all tracks.  If
        /// any writes are performed, this flag will be set.
        /// </summary>
        private GroupBool mBufferModFlag = new GroupBool();

        private int mTrackLength;
        private byte[] mFileData;       // holds track data; does not include header/footers

        private CircularBitBuffer[] mTrackBuffers = new CircularBitBuffer[NUM_TRACKS];


        /// <summary>
        /// Constructor.
        /// </summary>
        internal TwoIMG_Nibble(Stream stream, Header hdr, AppHook appHook)
                : base(stream, hdr, appHook) {
            mFileData = RawData.EMPTY_BYTE_ARRAY;
            // Track length is actually constant.  I'm keeping it in a field to maintain the
            // parallel with UnadornedNibble525.
            mTrackLength = NIB_TRACK_LENGTH;
        }

        /// <summary>
        /// Opens a nibble disk image.  Called from the base class <see cref="TwoIMG.OpenDisk"/>.
        /// </summary>
        /// <param name="stream">Stream with disk image data.</param>
        /// <param name="hdr">File header object.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <returns>New TwoIMG object (actually a TwoIMG_Nibble).</returns>
        internal static TwoIMG OpenNibbleDisk(Stream stream, Header hdr, AppHook appHook) {
            TwoIMG_Nibble disk = new TwoIMG_Nibble(stream, hdr, appHook);

            // Read the entire file into memory.
            disk.mFileData = new byte[stream.Length];
            stream.Position = hdr.mDataOffset;
            stream.ReadExactly(disk.mFileData, 0, (int)hdr.mDataLen);

            // Make sure the disk nibbles all have their high bits set.
            UnadornedNibble525.FixBytes(disk.mFileData, NUM_TRACKS, disk.mTrackLength, disk.Notes);
            disk.CreateBitBuffers();
            return disk;
        }

        /// <summary>
        /// Creates an unadorned 35-track nibble image, with fixed 6656 bytes per track and
        /// byte-aligned track nibbles.
        /// </summary>
        /// <param name="stream">Stream into which the new image will be written.  The stream
        ///   must be readable, writable, and seekable.</param>
        /// <param name="codec">Disk sector codec.</param>
        /// <param name="volume">Volume number to use for low-level format.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <returns>Disk image reference.</returns>
        internal static TwoIMG_Nibble CreateDisk(Stream stream, SectorCodec codec,
                byte volume, AppHook appHook) {
            if (!stream.CanRead || !stream.CanWrite || !stream.CanSeek) {
                throw new ArgumentException("Invalid stream capabilities");
            }

            // Set the length of the stream.
            long dataLen = NIB_TRACK_LENGTH * NUM_TRACKS;
            stream.SetLength(0);
            stream.SetLength(Header.LENGTH + dataLen);

            // Create the header.  It will be written to the stream by Flush(), later.
            Header hdr = Header.Create(Header.ImageFormat.Nibble_Floppy, (uint)dataLen);

            stream.SetLength(NUM_TRACKS * NIB_TRACK_LENGTH);
            TwoIMG_Nibble disk = new TwoIMG_Nibble(stream, hdr, appHook);

            // Create track data storage.  This does not include the header or footers.
            disk.mFileData = new byte[dataLen];

            int offset = 0;
            for (byte trk = 0; trk < NUM_TRACKS; trk++) {
                byte[] data =
                    TrackInit.GenerateTrack525(codec, true, volume, trk, out int bitCount);
                Debug.Assert(bitCount < data.Length * 8);
                if (data.Length > NIB_TRACK_LENGTH) {
                    throw new DAException("Generated track is too long: " + data.Length);
                }
                Array.Copy(data, 0, disk.mFileData, offset, data.Length);
                // Fill the rest of the track with faux-sync FFs.
                for (int i = data.Length; i < NIB_TRACK_LENGTH; i++) {
                    disk.mFileData[offset + i] = 0xff;
                }
                offset += NIB_TRACK_LENGTH;
            }
            Debug.Assert(offset == stream.Length);

            disk.CreateBitBuffers();

            // Save the whole thing to the stream.
            disk.mBufferModFlag.IsSet = true;
            disk.Flush();

            disk.ChunkAccess = new GatedChunkAccess(new NibbleChunkAccess(disk, codec));
            return disk;
        }

        // IDiskImage
        public override bool AnalyzeDisk(SectorCodec? codec, SectorOrder orderHint,
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

            // Figure out the sector format.
            Debug.Assert(mHeader.mImageFormat == (uint)Header.ImageFormat.Nibble_Floppy);
            IChunkAccess? chunks = FileAnalyzer.AnalyzeDiskFormat(this, codec, mAppHook);
            if (chunks == null) {
                //Debug.WriteLine("Disk format analysis failed");
                return false;
            }
            ChunkAccess = new GatedChunkAccess(chunks);
            if (depth == AnalysisDepth.ChunksOnly) {
                return true;
            }

            // Figure out the disk layout.
            if (!FileAnalyzer.AnalyzeFileSystem(chunks, false, mAppHook,
                    out IDiskContents? contents)) {
                return false;
            }
            // Got the disk layout, disable direct chunk access.
            Contents = contents;
            ChunkAccess.AccessLevel = GatedChunkAccess.AccessLvl.Closed;    // could be ReadOnly
            return true;
        }

        // IDiskImage
        public override void Flush() {
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
            if (!IsDirty) {
                return;
            }

            // We're about to clear the dirty flags, so set this in case it hasn't been yet.
            mIsModified = true;

            // Write data portion of file.
            mStream.Position = mHeader.mDataOffset;
            mStream.Write(mFileData, 0, (int)mHeader.mDataLen);
            mStream.Flush();

            WriteHeaderFooter();

            // Clear dirty flags.
            mBufferModFlag.IsSet = false;
            Debug.Assert(!IsDirty);
        }

        /// <summary>
        /// Creates a CircularBitBuffer object for each track.
        /// </summary>
        private void CreateBitBuffers() {
            for (int i = 0; i < NUM_TRACKS; i++) {
                mTrackBuffers[i] = new CircularBitBuffer(mFileData, i * mTrackLength, 0,
                    mTrackLength * 8, mBufferModFlag, IsReadOnly);
            }
        }

        // INibbleDataAccess
        public bool GetTrackBits(uint trackNum, uint trackFraction,
                [NotNullWhen(true)] out CircularBitBuffer? cbb) {
            if (trackNum < 0 || trackNum >= SectorCodec.MAX_TRACK_525) {
                throw new ArgumentOutOfRangeException("Invalid track number: " + trackNum + "/" +
                    trackFraction);
            }
            if (trackNum >= NUM_TRACKS || trackFraction != 0) {
                // Track number is valid for this type of disk, but we don't have it.
                cbb = null;
                return false;
            }

            // Make a copy of the CBB object, so multiple instances don't share the bit position.
            cbb = new CircularBitBuffer(mTrackBuffers[trackNum]);
            return true;
        }
    }
}
