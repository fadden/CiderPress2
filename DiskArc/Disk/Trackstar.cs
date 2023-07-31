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
        private const int DATA_START_OFFSET = 0x81;
        private const int MAX_TRACK_LENGTH = TRACK_AREA_LENGTH - (DATA_START_OFFSET + 2);
        private const int LEN_OFFSET = 0x19fe;
        private const int MIN_TRACKS = 40;
        private const int MAX_TRACKS = 80;

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

        public bool IsDirty { get { return mBufferModFlag.IsSet; } }

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
        private int mNumTracks;

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

            public TrackData(byte[] revBuffer, int offset, int length) {
                FwdBuffer = new byte[length];
                for (int i = 0; i < length; i++) {
                    FwdBuffer[i] = revBuffer[offset + length - i - 1];
                }
                CircBuf = new CircularBitBuffer(FwdBuffer, 0, 0, length * 8);
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
            disk.CreateBitBuffers();
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
            throw new NotImplementedException();        // TODO

            // Clear dirty flags.
            mBufferModFlag.IsSet = false;
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
                mTrackData[trk] = new TrackData(trackBuf, 0, trackLen);
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

        // IMetadata
        public bool CanAddNewEntries => false;

        // IMetadata
        public List<IMetadata.MetaEntry> GetMetaEntries() {
            return new List<IMetadata.MetaEntry>();
        }

        // IMetadata
        public string? GetMetaValue(string key, bool verbose) {
            throw new NotImplementedException();
        }

        // IMetadata
        public bool TestMetaValue(string key, string value) {
            throw new NotImplementedException();
        }

        // IMetadata
        public void SetMetaValue(string key, string value) {
            throw new NotImplementedException();
        }

        // IMetadata
        public bool DeleteMetaEntry(string key) {
            throw new NotImplementedException();
        }

        #endregion
    }
}
