/*
 * Copyright 2022 faddenSoft
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
    /// Plain nibble disk image (".nib"/".nb2").  Only used for 35-track 5.25" floppy disk images.
    /// </summary>
    /// <remarks>
    /// <para>This format has no CRC or other metadata, so we could write changes directly to
    /// the file as they are made.  There's little value in doing so, however, so as with other
    /// nibble formats, all writes are deferred until an explicit Flush.</para>
    /// </remarks>
    public class UnadornedNibble525 : IDiskImage, INibbleDataAccess {
        public const int NIB_TRACK_LENGTH = 6656;
        public const int NB2_TRACK_LENGTH = 6384;
        private const int NUM_TRACKS = 35;

        private static readonly NibCharacteristics sCharacteristics = new NibCharacteristics(
            name: "Unadorned 5.25",
            isByteAligned: true,
            isFixedLength: true,
            hasPartial525Tracks: false);

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

        private AppHook mAppHook;
        private Stream mStream;
        private int mTrackLength;
        private byte[] mFileData;

        private CircularBitBuffer[] mTrackBuffers = new CircularBitBuffer[NUM_TRACKS];

        // IDiskImage-required delegate
        public static bool TestKind(Stream stream, AppHook appHook) {
            // The contents of an unadorned image are not restricted.  All we have to go on
            // is the length of the file.

            return stream.Length == NIB_TRACK_LENGTH * NUM_TRACKS ||
                   stream.Length == NB2_TRACK_LENGTH * NUM_TRACKS;
        }

        /// <summary>
        /// Private constructor.
        /// </summary>
        private UnadornedNibble525(Stream stream, AppHook appHook) {
            mStream = stream;
            mAppHook = appHook;

            if (stream.Length == NIB_TRACK_LENGTH * NUM_TRACKS) {
                mTrackLength = NIB_TRACK_LENGTH;
            } else if (stream.Length == NB2_TRACK_LENGTH * NUM_TRACKS) {
                mTrackLength = NB2_TRACK_LENGTH;
            } else {
                throw new NotSupportedException("Incompatible data stream");
            }
            IsReadOnly = !stream.CanWrite;
            mFileData = RawData.EMPTY_BYTE_ARRAY;
        }

        /// <summary>
        /// Open a disk image file.
        /// </summary>
        /// <param name="stream">Disk image data stream.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <returns>Disk image reference.</returns>
        /// <exception cref="NotSupportedException">Incompatible data stream.</exception>
        public static UnadornedNibble525 OpenDisk(Stream stream, AppHook appHook) {
            if (!TestKind(stream, appHook)) {
                throw new NotSupportedException("Incompatible data stream");
            }

            UnadornedNibble525 disk = new UnadornedNibble525(stream, appHook);

            // Read the entire file into memory.
            disk.mFileData = new byte[stream.Length];
            stream.Position = 0;
            stream.ReadExactly(disk.mFileData, 0, (int)stream.Length);

            // Make sure the disk nibbles all have their high bits set.
            FixBytes(disk.mFileData, NUM_TRACKS, disk.mTrackLength, disk.Notes);
            disk.CreateBitBuffers();
            return disk;
        }

        /// <summary>
        /// Creates an unadorned 35-track nibble image, with fixed 6656 bytes per track and
        /// byte-aligned track nibbles (in other words, a .NIB file).
        /// </summary>
        /// <param name="stream">Stream into which the new image will be written.  The stream
        ///   must be readable, writable, and seekable.</param>
        /// <param name="codec">Disk sector codec.</param>
        /// <param name="volume">Volume number to use for low-level format.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <returns>Disk image reference.</returns>
        public static UnadornedNibble525 CreateDisk(Stream stream, SectorCodec codec,
                byte volume, AppHook appHook) {
            if (!stream.CanRead || !stream.CanWrite || !stream.CanSeek) {
                throw new ArgumentException("Invalid stream capabilities");
            }

            stream.Position = 0;
            stream.SetLength(NUM_TRACKS * NIB_TRACK_LENGTH);
            UnadornedNibble525 disk = new UnadornedNibble525(stream, appHook);
            disk.mFileData = new byte[stream.Length];

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

#if DEBUG
            foreach (byte nib in disk.mFileData) {
                if ((nib & 0x80) == 0) {
                    Debug.Assert(false, "Found byte with high bit clear");
                }
            }
#endif

            disk.CreateBitBuffers();

            // Save the whole thing to the stream.
            disk.mBufferModFlag.IsSet = true;
            disk.Flush();

            disk.ChunkAccess = new GatedChunkAccess(new NibbleChunkAccess(disk, codec));
            return disk;
        }

        // IDiskImage
        public bool AnalyzeDisk(SectorCodec? codec, SectorOrder orderHint, AnalysisDepth depth) {
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

        ~UnadornedNibble525() {
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
                mAppHook.LogW("GC disposing UnadornedNibble525, dirty=" + IsDirty);
                Debug.Assert(false, "GC disposing UnadornedNibble525, dirty=" + IsDirty +
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

            // Write entire file.
            mStream.Position = 0;
            mStream.Write(mFileData, 0, mFileData.Length);
            mStream.Flush();

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

        /// <summary>
        /// Confirms that all disk nibbles have their high bit set.  Any that don't will be
        /// modified.  If we don't do this, values might look like sync bytes, and adjust the
        /// bit synchronization of the data stream.
        /// </summary>
        /// <remarks>
        /// A more rigorous test would confirm that all bytes are valid 6&2 values (with $d5/$aa
        /// included).
        /// </remarks>
        internal static void FixBytes(byte[] buffer, int numTracks, int trackLength, Notes notes) {
            bool zeroesLogged = false;

            int offset = 0;
            for (int track = 0; track < NUM_TRACKS; track++) {
                Debug.Assert(offset == track * trackLength);
                int badNibs = 0;
                int zeroNibs = 0;
                for (int i = 0; i < trackLength; i++) {
                    if (buffer[offset] == 0x00) {
                        // AppleWin fills out gap 1 with 0x00 bytes to work around issues with
                        // the ProDOS formatter, which concludes that the drive is spinning too
                        // slowly if all 6656 bytes are used.  The 0x00 bytes are passed through
                        // the emulated disk controller, and act as a really, really long
                        // self-sync byte.  We can handle it the same way -- so long as no bits
                        // are set, it doesn't throw the nibble latching off.
                        // https://github.com/AppleWin/AppleWin/issues/1151
                        // https://github.com/AppleWin/AppleWin/issues/125#issuecomment-342977995
                        zeroNibs++;
                    } else if ((buffer[offset] & 0x80) == 0) {
                        buffer[offset] |= 0x80;
                        badNibs++;
                    }
                    offset++;
                }
                if (zeroNibs != 0 && !zeroesLogged) {
                    notes.AddI("Found " + zeroNibs + " zeroed nibbles on track " + track +
                        (zeroNibs == 336 ? " (probably formatted by emulator)" : ""));
                    zeroesLogged = true;    // no need to log this for every track
                }
                if (badNibs != 0) {
                    Debug.WriteLine("Found " + badNibs + " bad nibbles");
                    notes.AddW("Found " + badNibs + " invalid nibbles on track " + track);
                }
            }
        }
    }
}
