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
using System.Text;

using CommonUtil;
using DiskArc.Multi;
using static DiskArc.Defs;
using static DiskArc.IDiskImage;
using static DiskArc.IMetadata;

namespace DiskArc.Disk {
    /// <summary>
    /// Trackstar disk image (".app").  Holds 40-track or 80-track 5.25" floppy disk images.
    /// </summary>
    /// <remarks>
    /// <para>Very similar to <see cref="UnadornedNibble525"/>, but with variable-length track
    /// data and a short header on each track.</para>
    /// <para>A short ASCII description is included at the start of every track.  We can provide
    /// access to this through the metadata interface.</para>
    /// </remarks>
    public class Trackstar : IDiskImage, INibbleDataAccess, IMetadata {
        private const int TRACK_AREA_LENGTH = 6656;
        private const int DESCRIPTION_LENGTH = 46;
        private const int RESERVED_LENGTH = 82;
        private const int DATA_START_OFFSET = 0x81;
        private const int MAX_TRACK_LENGTH = TRACK_AREA_LENGTH - (DATA_START_OFFSET + 2);
        private const int LEN_OFFSET = 0x19fe;
        private const int MIN_TRACKS = 40;
        private const int MAX_TRACKS = 80;
        public const string DESCRIPTION_NAME = "description";

        // 82 zero bytes.
        private static readonly byte[] sReserved = new byte[RESERVED_LENGTH];

        private static readonly NibCharacteristics sCharacteristics = new NibCharacteristics(
            name: "Trackstar",
            isByteAligned: true,
            isFixedLength: false,
            hasPartial525Tracks: true);

        //
        // IDiskImage properties.
        //

        public bool IsReadOnly { get; private set; }

        public bool IsDubious => false;

        public bool IsDirty { get { return mBufferModFlag.IsSet || mDescriptionChanged; } }

        public bool IsModified {
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

        public Notes Notes { get; } = new Notes();

        public GatedChunkAccess? ChunkAccess { get; private set; }

        public IDiskContents? Contents { get; private set; }

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

        private Stream mStream;
        private AppHook mAppHook;

        private int mNumTracks;     // 40 or 80
        private byte[] mDescription;
        private bool mDescriptionChanged;

        /// <summary>
        /// Class for managing the data in a single track.
        /// </summary>
        private class TrackData {
            /// <summary>
            /// Track length.
            /// </summary>
            public int Length { get; private set; }

            /// <summary>
            /// Copy of track data, with bytes in "forward" order.  This is the opposite of
            /// how it is stored in the file.
            /// </summary>
            public byte[] FwdBuffer { get; private set; }

            /// <summary>
            /// Circular bit buffer.  This uses FwdBuffer as storage.
            /// </summary>
            public CircularBitBuffer CircBuf { get; private set; }

            /// <summary>
            /// Constructor.
            /// </summary>
            /// <param name="inBuffer">Buffer with input data.  May be forward or reverse.</param>
            /// <param name="offset">Offset to data within buffer.</param>
            /// <param name="length">Length of data.</param>
            /// <param name="isRev">True if order of bytes is reversed.</param>
            /// <param name="modFlag">Shared modification flag.</param>
            /// <param name="isReadOnly">True if writes should not be allowed.</param>
            public TrackData(byte[] inBuffer, int offset, int length, bool isRev,
                    GroupBool modFlag, bool isReadOnly) {
                Debug.Assert(length <= MAX_TRACK_LENGTH);
                Length = length;

                FwdBuffer = new byte[length];
                if (isRev) {
                    for (int i = 0; i < length; i++) {
                        FwdBuffer[i] = inBuffer[offset + length - i - 1];
                    }
                } else {
                    for (int i = 0; i < length; i++) {
                        FwdBuffer[i] = inBuffer[offset + i];
                    }
                }
                CircBuf = new CircularBitBuffer(FwdBuffer, 0, 0, length * 8, modFlag, isReadOnly);
            }

            /// <summary>
            /// Writes the contents of the data buffer to the stream, in reverse byte order.
            /// </summary>
            /// <param name="stream">Stream to write data to.</param>
            public void WriteReversed(Stream stream) {
                for (int i = 0; i < Length; i++) {
                    stream.WriteByte(FwdBuffer[Length - i - 1]);
                }
            }
        }
        private TrackData[] mTrackData;


        // IDiskImage-required delegate
        public static bool TestKind(Stream stream, AppHook appHook) {
            // 40 or 80 tracks, each stored in a fixed amount of space.
            if (stream.Length != TRACK_AREA_LENGTH * MIN_TRACKS &&
                    stream.Length != TRACK_AREA_LENGTH * MAX_TRACKS) {
                return false;
            }

            // Run through and check a few bytes.
            int numTracks = (int)(stream.Length / TRACK_AREA_LENGTH);
            byte expected = (numTracks == 40) ? (byte)0x00 : (byte)0x01;
            for (int i = 0; i < numTracks; i++) {
                stream.Position = i * TRACK_AREA_LENGTH + 0x7f;
                if (stream.ReadByte() != 0x00) {
                    return false;
                }
                if (stream.ReadByte() != expected) {
                    return false;
                }
                stream.Position = i * TRACK_AREA_LENGTH + LEN_OFFSET;
                ushort len = RawData.ReadU16LE(stream, out bool ok);
                if (len > MAX_TRACK_LENGTH) {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Private constructor.
        /// </summary>
        private Trackstar(Stream stream, AppHook appHook) {
            mStream = stream;
            mAppHook = appHook;

            mNumTracks = (int)(stream.Length / TRACK_AREA_LENGTH);
            mTrackData = new TrackData[mNumTracks];
            mDescription = new byte[DESCRIPTION_LENGTH];
            RawData.MemSet(mDescription, 0, mDescription.Length, 0x20);

            IsReadOnly = !stream.CanWrite;
        }

        /// <summary>
        /// Opens a disk image file.
        /// </summary>
        /// <param name="stream">Disk image data stream.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <returns>Disk image reference.</returns>
        /// <exception cref="NotSupportedException">Incompatible data stream.</exception>
        public static Trackstar OpenDisk(Stream stream, AppHook appHook) {
            if (!TestKind(stream, appHook)) {
                throw new NotSupportedException("Incompatible data stream");
            }

            Trackstar disk = new Trackstar(stream, appHook);
            appHook.LogI("Opening Trackstar image, numTracks=" + disk.mNumTracks);
            stream.Position = 0;
            stream.Read(disk.mDescription, 0, disk.mDescription.Length);
            disk.CreateBitBuffers();
            return disk;
        }

        /// <summary>
        /// Creates a 5.25" nibble image, formatted with the supplied codec.
        /// </summary>
        /// <param name="stream">Stream into which the new image will be written.  The stream
        ///   must be readable, writable, and seekable.</param>
        /// <param name="codec">Disk sector codec.</param>
        /// <param name="volume">Volume number to use for low-level format.</param>
        /// <param name="fmtTracks">Number of tracks to format.  Must be 35 or 40.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <returns>Disk image reference.</returns>
        public static Trackstar CreateDisk(Stream stream, SectorCodec codec,
                byte volume, uint fmtTracks, AppHook appHook) {
            if (!stream.CanRead || !stream.CanWrite || !stream.CanSeek) {
                throw new ArgumentException("Invalid stream capabilities");
            }
            uint numTracks;
            if (fmtTracks == 35 || fmtTracks == 40) {
                numTracks = 40;
            } else if (fmtTracks == 80) {
                numTracks = 80;
                throw new ArgumentException("Not handling 80 tracks yet");
            } else {
                throw new ArgumentException("Invalid track count: " + fmtTracks);
            }

            stream.Position = 0;
            stream.SetLength(numTracks * TRACK_AREA_LENGTH);
            Trackstar disk = new Trackstar(stream, appHook);

            for (byte trk = 0; trk < numTracks; trk++) {
                byte[] data;
                if (trk < fmtTracks) {
                    data = TrackInit.GenerateTrack525(codec, true, volume, trk, out int bitCount);
                    Debug.Assert(bitCount < data.Length * 8);
                } else {
                    // We don't want this track to be recognized as habitable.  We can set the
                    // length to zero or fill it with junk.  The original Trackstar apparently
                    // just copied track 34 in here.  I'm not sure which approach is best.
                    data = new byte[MAX_TRACK_LENGTH];
                    RawData.MemSet(data, 0, data.Length, 0xff);
                }
                if (data.Length > MAX_TRACK_LENGTH) {
                    throw new DAException("Generated track is too long: " + data.Length);
                }
                disk.mTrackData[trk] = new TrackData(data, 0, data.Length, false,
                    disk.mBufferModFlag, false);
            }
            disk.mBufferModFlag.IsSet = true;
            disk.Flush();

            disk.ChunkAccess = new GatedChunkAccess(new NibbleChunkAccess(disk, codec));
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

            // Figure out the sector format.
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
            ChunkAccess.AccessLevel = GatedChunkAccess.AccessLvl.ReadOnly;
            return true;
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

        ~Trackstar() {
            Dispose(false);     // cleanup check
        }
        // IDisposable generic Dispose() implementation.
        public void Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        protected virtual void Dispose(bool disposing) {
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
                mAppHook.LogW("GC disposing Trackstar, dirty=" + IsDirty);
                Debug.Assert(false, "GC disposing Trackstar, dirty=" + IsDirty +
                    " created:\r\n" + mCreationStackTrace);
            }
        }
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
            if (!IsDirty) {
                return;
            }

            // We're about to clear the dirty flags, so set this in case it hasn't been yet.
            mIsModified = true;

            // Write entire file.  Length won't change.
            mStream.Position = 0;
            for (int trk = 0; trk < mNumTracks; trk++) {
                Debug.Assert(mStream.Position == trk * TRACK_AREA_LENGTH);
                mStream.Write(mDescription, 0, mDescription.Length);
                if (!mBufferModFlag.IsSet) {
                    // Track data didn't change, leave it alone.
                    mStream.Position = (trk + 1) * TRACK_AREA_LENGTH;
                    continue;
                }
                mStream.Write(sReserved, 0, sReserved.Length);
                if (mNumTracks == MAX_TRACKS) {
                    mStream.WriteByte(0x01);
                } else {
                    mStream.WriteByte(0x00);
                }
                TrackData td = mTrackData[trk];
                td.WriteReversed(mStream);
                // Pad out the remaining space with FF.  To be true to the original we should
                // write data from the start of the track in forward order.
                for (int i = 0; i < MAX_TRACK_LENGTH - td.Length; i++) {
                    mStream.WriteByte(0xff);
                }
                RawData.WriteU16LE(mStream, (ushort)td.Length);
            }
            mStream.Flush();

            // Clear dirty flags.
            mBufferModFlag.IsSet = false;
            mDescriptionChanged = false;
            Debug.Assert(!IsDirty);
        }

        /// <summary>
        /// Creates a CircularBitBuffer object for each track.
        /// </summary>
        private void CreateBitBuffers() {
            byte[] trackBuf = new byte[TRACK_AREA_LENGTH];
            for (int trk = 0; trk < mNumTracks; trk++) {
                mStream.Position = trk * TRACK_AREA_LENGTH + LEN_OFFSET;
                ushort trackLen = RawData.ReadU16LE(mStream, out bool ok);
                Debug.Assert(trackLen <= MAX_TRACK_LENGTH);     // should've been caught initially
                if (trackLen == 0) {
                    trackLen = MAX_TRACK_LENGTH;
                }

                mStream.Position = trk * TRACK_AREA_LENGTH + DATA_START_OFFSET;
                mStream.Read(trackBuf, 0, trackLen);
                mTrackData[trk] =
                    new TrackData(trackBuf, 0, trackLen, true, mBufferModFlag, IsReadOnly);
            }
        }

        public bool GetTrackBits(uint trackNum, uint trackFraction,
                [NotNullWhen(true)] out CircularBitBuffer? cbb) {
            if (trackNum >= SectorCodec.MAX_TRACK_525) {
                throw new ArgumentOutOfRangeException("Invalid track number: " + trackNum + "/" +
                    trackFraction);
            }
            if (trackFraction == 1 || trackFraction == 3 ||
                    (trackFraction == 2 && mNumTracks != MAX_TRACKS)) {
                // Never have quarter tracks, and only have half tracks for 80-track images.
                cbb = null;
                return false;
            }

            int trackIndex;
            if (mNumTracks == MAX_TRACKS) {
                trackIndex = (int)trackNum * 2 + (trackFraction == 0 ? 0 : 1);
            } else {
                trackIndex = (int)trackNum;
            }

            // Make a copy of the CBB object, so multiple instances don't share the bit position.
            cbb = new CircularBitBuffer(mTrackData[trackIndex].CircBuf);
            return true;
        }

        #region Metadata

        private static readonly MetaEntry[] sMetaEntries = new MetaEntry[] {
            new MetaEntry("description", MetaEntry.ValType.String,
                "Disk description.", "ASCII string, 46 characters max.", canEdit: true),
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
            try {
                return Encoding.ASCII.GetString(mDescription).TrimEnd();
            } catch (Exception ex) {
                Debug.WriteLine("ASCII conversion failed: " + ex.Message);
                return null;
            }
        }

        private static byte[]? StringToDescription(string value) {
            try {
                byte[] ascii = Encoding.ASCII.GetBytes(value.TrimEnd());
                if (ascii.Length > DESCRIPTION_LENGTH) {
                    return null;
                }

                byte[] description = new byte[DESCRIPTION_LENGTH];
                Array.Copy(ascii, description, ascii.Length);
                // Pad the field with spaces.
                for (int i = ascii.Length; i < description.Length; i++) {
                    description[i] = 0x20;
                }
                return description;
            } catch {
                return null;
            }
        }

        // IMetadata
        public bool TestMetaValue(string key, string value) {
            if (key != "description" || value == null) {
                return false;
            }
            return StringToDescription(value) != null;
        }

        // IMetadata
        public void SetMetaValue(string key, string value) {
            if (key == null || value == null) {
                throw new ArgumentException("key and value must not be null");
            }
            if (key != "description") {
                throw new ArgumentException("invalid key '" + key + "'");
            }
            byte[]? description = StringToDescription(value);
            if (description == null) {
                throw new ArgumentException("invalid value '" + value + "'");
            }
            mDescription = description;
            mDescriptionChanged = true;
        }

        // IMetadata
        public bool DeleteMetaEntry(string key) {
            return false;
        }

        #endregion Metadata
    }
}
