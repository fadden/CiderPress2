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
using static DiskArc.IMetadata;

// TODO: add Get/SetTagBytes(uint block, byte[], int) calls to manage tag bytes on 800KB volumes.
//   Could be useful in conjunction with DiskCopy.  Probably not important unless we start
//   handling MFS filesystems.  (Might be part of IChunkAccess since the filesystem code will
//   need access to it.)
// TODO: support creation of 80-track 5.25" disk images.  Requires adding an 80-track mode to
//   NibbleChunkAccess.

// This is an IDiskImage instance, which means we modify the original file in place (vs. an
// IArchive that gets rewritten for every change).  To reduce the risk of a crash causing data
// loss, due to mid-save program errors leading to truncation or a bad CRC, we always write the
// file in a single operation.  For efficiency we want to keep the entire thing in memory anyway,
// and we only operate on floppy disk images, so this is reasonably inexpensive.
//
// The tricky part is that we don't want to be copying track data in and out constantly, so
// we need to hand out direct references to the data buffer.  This means we can't generate a new
// byte[] and switch to it every time we generate file data for writing.  Wrapping the track data
// with a CircularBitBuffer ensures the application can't write outside the track data area.
// Tracking changes to the CBB-wrapped data requires setting a "dirty" flag in the CBB.
//
// The INFO+TMAP+TRKS chunks don't change size, so we modify that in place, generate a new META
// chunk, and copy any other chunks without modification.  The optional chunks don't have a
// strict ordering, so it's okay if this causes META to move to the end of the file.  The whole
// thing is written to a memory stream, which is then written to the original file in one shot.
//
// This approach has more overhead than simply writing the modified tracks and CRC-32, but it's
// much simpler and we only need to do it when the application calls IDiskImage.Flush().

namespace DiskArc.Disk {
    /// <summary>
    /// WOZ disk image format.
    /// </summary>
    /// <remarks>
    /// <para>This format is designed to hold low-level captures of Apple II 5.25" and 3.5"
    /// floppy disks.  It features variable-length tracks, non-byte-aligned nibble data, and
    /// support for half/quarter tracks.</para>
    /// <para>The format includes a "write protected" flag that allows emulators to handle
    /// floppy disk write protection.  The flag is ignored here.</para>
    /// <para>We have "experimental" support for flux data: we convert the flux data to bit
    /// data using the timing value in the INFO block.  The presence of flux data will force
    /// the disk image into read-only mode.</para>
    /// </remarks>
    public class Woz : IDiskImage, INibbleDataAccess, IMetadata {
        // 3.5" disk images are about 1.2MB.  As flux images they'd be about 6x(?) larger.
        // Figure the max size for an 800KB floppy is 7.2MB.  Set the "max reasonable" value
        // to 16MB so we have lots of headroom.
        private const int MAX_FILE_LEN = 16 * 1024 * 1024;

        private const int HEADER_LEN = 12;
        private const int CHUNK_HDR_LEN = 8;
        private static readonly byte[] SIGNATURE1 = new byte[8] {
            0x57, 0x4f, 0x5a, 0x31, 0xff, 0x0a, 0x0d, 0x0a      // WOZ1
        };
        private static readonly byte[] SIGNATURE2 = new byte[8] {
            0x57, 0x4f, 0x5a, 0x32, 0xff, 0x0a, 0x0d, 0x0a      // WOZ2
        };
        private const uint ID_INFO = 0x4f464e49;
        private const int INFO_CHUNK_START = 20;
        internal const int INFO_LENGTH = 60;
        private const uint ID_TMAP = 0x50414d54;
        private const int TMAP_CHUNK_START = 88;
        private const int TMAP_LENGTH = 160;
        private const uint ID_TRKS = 0x534b5254;
        private const int TRKS_CHUNK_START = 256;
        private const int TRKS_DATA_START = 1536;
        private const uint ID_FLUX = 0x58554c46;
        private const int FLUX_LENGTH = 160;
        private const uint ID_WRIT = 0x54495257;
        private const uint ID_META = 0x4154454d;

        private const byte NO_TRACK = 0xff;
        private const int WOZ1_TRKS_ENTRY_LEN = 6656;
        private const int WOZ1_TRKS_BIT_COUNT_OFFSET = 6648;
        private const int WOZ2_TRKS_ENTRY_LEN = 8;
        private const int WOZ_BLOCK_SIZE = 512;

        internal static int CREATOR_ID_LEN = 32;
        private static readonly string WOZ_CREATOR_NAME = Defs.NAME_AND_VERSION;

        private static readonly NibCharacteristics sCharacteristics = new NibCharacteristics(
            name: "WOZ",
            isByteAligned: false,
            isFixedLength: false,
            hasPartial525Tracks: true);

        //
        // IDiskImage properties.
        //

        public bool IsReadOnly {
            get { return HasFlux || IsDubious || !mDataStream.CanWrite; }
        }

        public bool IsDirty {
            get {
                // Has the file data been modified at the bit level?
                if (mBufferModFlag.IsSet) {
                    return true;
                }
                // Metadata changes?
                if (MetaChunk != null && MetaChunk.IsDirty) {
                    return true;
                }
                // Info chunk changes?
                return Info.IsDirty;
            }
        }

        public bool IsModified {
            get {
                // Capture any dirty flags.
                if (mBufferModFlag.IsSet || (MetaChunk != null && MetaChunk.IsDirty) ||
                        Info.IsDirty) {
                    mIsModified = true;
                }
                // Check chunk status.
                if (ChunkAccess != null && ChunkAccess.IsModified) {
                    mIsModified = true;
                }
                return mIsModified;
            }
            set {
                // Clear our flag.  If the bits or metadata are dirty, the flag will get set
                // again next query.
                mIsModified = false;
                if (value == false && ChunkAccess != null) {
                    // Clear the "is modified" flag in the chunk.  DO NOT touch dirty flags.
                    ChunkAccess.IsModified = false;
                }
            }
        }
        private bool mIsModified;

        public bool IsDubious { get; private set; }

        public Notes Notes { get; } = new Notes();

        public GatedChunkAccess? ChunkAccess { get; private set; }

        public IDiskContents? Contents { get; private set; }

        //
        // INibbleDataAccess properties.
        //

        public NibCharacteristics Characteristics => sCharacteristics;

        public MediaKind DiskKind { get; private set; }

        //
        // WOZ-specific properties.
        //

        public enum WozKind { Unknown = 0, Woz1 = 1, Woz2 = 2 }    // file revision

        /// <summary>
        /// File revision (1 or 2).  Determines how some of the chunks are parsed.
        /// </summary>
        public WozKind FileRevision { get; private set; } = WozKind.Unknown;

        /// <summary>
        /// True if the image has a META chunk.
        /// </summary>
        public bool HasMeta { get { return MetaChunk != null; } }

        /// <summary>
        /// True if the image has one or more FLUX track entries.
        /// </summary>
        public bool HasFlux { get; private set; }

        /// <summary>
        /// INFO chunk data holder.
        /// </summary>
        private Woz_Info Info { get; set; }

        /// <summary>
        /// META data holder.  Will be null if the disk image doesn't have a META chunk.
        /// </summary>
        private Woz_Meta? MetaChunk { get; set; }

        //
        // Innards.
        //

        /// <summary>
        /// Modification tracker.
        /// </summary>
        private GroupBool mBufferModFlag = new GroupBool();

        /// <summary>
        /// Application hook reference.
        /// </summary>
        private AppHook mAppHook;

        /// <summary>
        /// WOZ data stream.  Updates to the file are written here.
        /// </summary>
        private Stream mDataStream;

        /// <summary>
        /// Byte buffer with full original file contents.  Modifications to the track data are
        /// made here.  This buffer is not replaced when the file is rewritten for a Flush(),
        /// so the contents of the INFO and META chunks in here may be stale.
        /// </summary>
        /// <remarks>
        /// <para>This may be generated from a MemoryStream, so the length of the buffer is
        /// not a reliable measure of the length of the contents.  Traverse the chunk list to
        /// determine the full extent.</para>
        /// </remarks>
        private byte[] mBaseData = RawData.EMPTY_BYTE_ARRAY;

        /// <summary>
        /// Length of data in mBaseData.
        /// </summary>
        private int mBaseLength = -1;

        /// <summary>
        /// File offset of FLUX chunk in mBaseData, or -1 if it doesn't exist.
        /// </summary>
        private int mFluxChunkOffset = -1;


        // IDiskImage-required delegate
        public static bool TestKind(Stream stream, AppHook appHook) {
            stream.Position = 0;
            if (stream.Length < 256 || stream.Length > MAX_FILE_LEN) {
                // Too big, or too small to hold signature/INFO/TMAP, much less TRKS.
                return false;
            }
            byte[] sigBuf = new byte[SIGNATURE1.Length];
            stream.ReadExactly(sigBuf, 0, SIGNATURE1.Length);
            if (RawData.CompareBytes(sigBuf, SIGNATURE1, SIGNATURE1.Length) ||
                    RawData.CompareBytes(sigBuf, SIGNATURE2, SIGNATURE2.Length)) {
                return true;
            }
            return false;
        }

        /// <summary>
        /// Private constructor.
        /// </summary>
        private Woz(Stream stream, AppHook appHook) {
            mDataStream = stream;
            mAppHook = appHook;
            Info = new Woz_Info();
        }

        /// <summary>
        /// Opens a WOZ disk image file.
        /// </summary>
        /// <param name="stream">Disk image stream.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <returns>WOZ instance.</returns>
        /// <exception cref="NotSupportedException">Data stream is not a WOZ disk image.</exception>
        public static Woz OpenDisk(Stream stream, AppHook appHook) {
            if (!TestKind(stream, appHook)) {
                throw new NotSupportedException("Incompatible data stream");
            }
            Woz newDisk = new Woz(stream, appHook);
            newDisk.Parse();

            // Propagate read-only flag.  It depends on the stream and IsDubious (but not on the
            // presence of the FLUX section), so we have to defer this until the file is parsed.
            // TODO: there's some logic that assumes files with FLUX can't be modified.  We need
            // to separate read-only image (stream / dubious) from read-only tracks (FLUX).  Until
            // then we block META updates when FLUX is present.
            bool metaReadOnly = newDisk.IsReadOnly;
            newDisk.Info.IsReadOnly = newDisk.IsReadOnly;
            if (newDisk.MetaChunk != null) {
                newDisk.MetaChunk.IsReadOnly = newDisk.IsReadOnly;
            }
            return newDisk;
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
        public virtual void CloseContents() {
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

        ~Woz() {
            Dispose(false);     // cleanup check
        }
        public void Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        protected virtual void Dispose(bool disposing) {
            if (ChunkAccess != null) {
                ChunkAccess.AccessLevel = GatedChunkAccess.AccessLvl.Closed;
            }
            if (disposing) {
                Flush();
                if (Contents is IFileSystem) {
                    ((IFileSystem)Contents).Dispose();
                } else if (Contents is IMultiPart) {
                    ((IMultiPart)Contents).Dispose();
                }
                Contents = null;
            } else {
                mAppHook.LogW("GC disposing Woz, FileSystem=" + Contents);
                Debug.Assert(false, "GC disposing WOZ, dirty=" + IsDirty +
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

            MemoryStream newFile = GenerateStream();
#if DEBUG
            {
                // Parse the new file to verify that we haven't broken anything too badly.
                using (Woz verify = new Woz(newFile, mAppHook)) {
                    verify.Parse();
                    if (verify.IsDubious) {
                        // If previous contents were dubious, we'd be in read-only mode.  Looks
                        // like we broke something.
                        throw new DAException("New WOZ failed verification");
                    }
                }
            }
#endif

            // Write the data to the stream.
            mDataStream.Position = 0;
            mDataStream.Write(newFile.GetBuffer(), 0, (int)newFile.Length);
            mDataStream.SetLength(newFile.Length);
            mDataStream.Flush();

            // DO NOT switch to the new copy.  There may be CircularBitBuffers with
            // references to the old buffer.

            // Clear the dirty flags.
            mBufferModFlag.IsSet = false;
            if (Info != null) {
                Info.IsDirty = false;
            }
            if (MetaChunk != null) {
                MetaChunk.IsDirty = false;
            }
            Debug.Assert(!IsDirty);
        }

        /// <summary>
        /// Determines whether the disk configuration is supported.
        /// </summary>
        /// <param name="numTracks">Number of tracks.</param>
        /// <param name="errMsg">Error message, or empty string on success.</param>
        /// <returns>True on success.</returns>
        public static bool CanCreateDisk525(uint numTracks, out string errMsg) {
            errMsg = string.Empty;
            if (numTracks != 35 && numTracks != 40) {
                errMsg = "Number of tracks must be 35 or 40";
            }
            return errMsg == string.Empty;
        }

        /// <summary>
        /// Creates a new 5.25" disk image.  The disk will be fully formatted.
        /// </summary>
        /// <remarks>
        /// <para>This interface supports a limited number of configurations.  We want to support
        /// creation of blank images for testing, and conversion of other images to WOZ format,
        /// not provide a fully general WOZ creation mechanism.</para>
        /// </remarks>
        /// <param name="stream">Stream into which the new disk image will be written.</param>
        /// <param name="numTracks">Number of whole tracks; must be 35 or 40.</param>
        /// <param name="codec">Sector codec.</param>
        /// <param name="volume">Low-level volume number.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <returns>Newly-created disk image.</returns>
        public static Woz CreateDisk525(Stream stream, uint numTracks, SectorCodec codec,
                byte volume, AppHook appHook) {
            if (!CanCreateDisk525(numTracks, out string errMsg)) {
                throw new ArgumentException(errMsg);
            }

            // Generate track data.
            byte[][] tracks = new byte[numTracks][];
            int[] lengths = new int[numTracks];
            ushort lrgTrkBlkCnt = 0;
            for (byte trk = 0; trk < tracks.Length; trk++) {
                // Generate the tracks.  We can use either the exact bit length, or the full
                // length as generated by the init code.  The former has a zero-length gap 1,
                // which is undesirable.
                tracks[trk] = TrackInit.GenerateTrack525(codec, false, volume, trk, out int bLen);
                lengths[trk] = tracks[trk].Length * 8;

                // For a 5.25" disk all tracks should be about the same length, but check it just
                // to be sure.
                int blockCount = (lengths[trk] + 4095) / 4096;
                if (blockCount > lrgTrkBlkCnt) {
                    lrgTrkBlkCnt = (ushort)blockCount;    // 13 blocks covers [6145,6656] octets
                }
            }

            stream.Position = 0;
            stream.SetLength(0);
            Woz newDisk = new Woz(stream, appHook);
            MemoryStream tmpStream = new MemoryStream();
            tmpStream.SetLength(TRKS_DATA_START);   // enough for header+INFO+TMAP+TRKS descriptors
            byte[] tmpBuf = tmpStream.GetBuffer();  // access the sized buffer directly

            newDisk.FileRevision = WozKind.Woz2;
            newDisk.DiskKind = MediaKind.GCR_525;
            newDisk.Info = new Woz_Info(WOZ_CREATOR_NAME, MediaKind.GCR_525, lrgTrkBlkCnt);

            // Write header and INFO.
            newDisk.WriteHeaderAndInfo(tmpBuf);

            // Write the TMAP.
            int offset = TMAP_CHUNK_START - CHUNK_HDR_LEN;
            RawData.WriteU32LE(tmpBuf, ref offset, ID_TMAP);
            RawData.WriteU32LE(tmpBuf, ref offset, TMAP_LENGTH);
            for (int i = 0; i < SectorCodec.MAX_TRACK_525; i++) {
                // Set the full track and adjacent quarter-tracks to the track index.  For
                // tracks 35-39 on a 35-track disk, mark them not present.
                byte trackIndex = (i < 35 || numTracks > 35) ? (byte)i : (byte)0xff;
                if (i > 0) {
                    tmpBuf[offset + i * 4 - 1] = trackIndex;
                }
                tmpBuf[offset + i * 4] = tmpBuf[offset + i * 4 + 1] = trackIndex;
                tmpBuf[offset + i * 4 + 2] = 0xff;      // mark half-track as not present
            }

            // Write the TRKS descriptors, 8 bytes per track.  The TRKS section holds 160 tracks,
            // so when we're done we should be at byte 256+(160*8)=1536.  The allocate the same
            // number of 512-byte blocks for each track, so we can write the descriptors first.
            offset = TRKS_CHUNK_START;
            ushort startBlock = TRKS_DATA_START / 512;         // block 3
            for (int i = 0; i < numTracks; i++) {
                RawData.WriteU16LE(tmpBuf, ref offset, startBlock);
                RawData.WriteU16LE(tmpBuf, ref offset, lrgTrkBlkCnt);
                RawData.WriteU32LE(tmpBuf, ref offset, (uint)lengths[i]);
                startBlock += lrgTrkBlkCnt;
            }

            // Copy the track data over.
            offset = TRKS_DATA_START;
            for (int i = 0; i < numTracks; i++) {
                Debug.Assert((lengths[i] + 7) / 8 < lrgTrkBlkCnt * 512);
                tmpStream.Position = offset;
                tmpStream.Write(tracks[i], 0, (lengths[i] + 7) / 8);
                offset += lrgTrkBlkCnt * 512;
            }
            tmpStream.SetLength(offset);        // make sure we fill out the last block

            // Get a new reference to the underlying byte buffer, which likely got reallocated.
            tmpBuf = tmpStream.GetBuffer();

            // Go back and set the TRKS chunk header.  Leave the stream positioned at the end.
            Debug.Assert(tmpStream.Length % 512 == 0);
            uint trksLength = (uint)(tmpStream.Length - TRKS_CHUNK_START);
            offset = TRKS_CHUNK_START - 8;
            RawData.WriteU32LE(tmpBuf, ref offset, ID_TRKS);
            RawData.WriteU32LE(tmpBuf, ref offset, trksLength);

            // Use AddMETA() instead.
            //// Add a default META chunk to the end.
            //newDisk.Metadata = Woz_Meta.CreateMETA();
            //byte[] metaData = newDisk.Metadata.Serialize(out int metaOffset, out int metaLength);
            //RawData.WriteU32LE(tmpStream, ID_META);
            //RawData.WriteU32LE(tmpStream, (uint)metaLength);
            //tmpStream.Write(metaData, metaOffset, metaLength);

            // Compute the full-file CRC.  Use the stream length, not the buffer length,
            // because MemoryStream will oversize while in expand mode.
            uint crc = CRC32.OnBuffer(0, tmpBuf, HEADER_LEN, (int)(tmpStream.Length - HEADER_LEN));
            RawData.SetU32LE(tmpBuf, 8, crc);

            // Copy the whole thing to the output stream.
            stream.Write(tmpBuf, 0, (int)tmpStream.Length);

            // Finish setting up the object, and return it.
            newDisk.mBaseData = tmpBuf;
            newDisk.mBaseLength = (int)tmpStream.Length;
            newDisk.ChunkAccess = new GatedChunkAccess(new NibbleChunkAccess(newDisk, codec));
            return newDisk;
        }

        /// <summary>
        /// Determines whether the disk configuration is supported.
        /// </summary>
        /// <param name="mediaKind">Kind of disk to create.</param>
        /// <param name="interleave">Disk interleave (2 or 4).</param>
        /// <param name="errMsg">Error message, or empty string on success.</param>
        /// <returns>True on success.</returns>
        public static bool CanCreateDisk35(MediaKind mediaKind, int interleave, out string errMsg) {
            errMsg = string.Empty;
            if (mediaKind != MediaKind.GCR_SSDD35 && mediaKind != MediaKind.GCR_DSDD35) {
                errMsg = "Unsupported value for MediaKind: " + mediaKind;
            }
            if (interleave != 2 && interleave != 4) {
                errMsg = "Interleave must be 2:1 or 4:1";
            }
            return errMsg == string.Empty;
        }

        /// <summary>
        /// Creates a new 400KB or 800KB 3.5" disk image.  The disk will be fully formatted.
        /// </summary>
        /// <param name="stream">Stream into which the new disk image will be written.</param>
        /// <param name="mediaKind">Type of disk to create (SSDD or DSDD).</param>
        /// <param name="interleave">Disk interleave (2 or 4).</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <returns>Newly-created disk image.</returns>
        public static Woz CreateDisk35(Stream stream, MediaKind mediaKind, int interleave,
                SectorCodec codec, AppHook appHook) {
            if (!CanCreateDisk35(mediaKind, interleave, out string errMsg)) {
                throw new ArgumentException(errMsg);
            }
            int numSides;
            switch (mediaKind) {
                case MediaKind.GCR_SSDD35:
                    numSides = 1;
                    break;
                case MediaKind.GCR_DSDD35:
                    numSides = 2;
                    break;
                default:
                    throw new NotImplementedException();
            }

            byte formatVal = (byte)(numSides << 4 | interleave);
            Debug.Assert(formatVal == 0x12 || formatVal == 0x14 ||
                         formatVal == 0x22 || formatVal == 0x24);

            byte[][] tracks = new byte[SectorCodec.MAX_TRACK_35 * numSides][];
            int[] lengths = new int[SectorCodec.MAX_TRACK_35 * numSides];
            ushort lrgTrkBlkCnt = 0;
            for (byte trk = 0; trk < SectorCodec.MAX_TRACK_35; trk++) {
                for (int i = 0; i < numSides; i++) {
                    byte[] trkData =  TrackInit.GenerateTrack35(codec, false, trk, i, formatVal,
                        out int bitLength);
                    tracks[trk * numSides + i] = trkData;
                    lengths[trk * numSides + i] = trkData.Length * 8;

                    int blockCount = (trkData.Length + 511) / 512;
                    if (blockCount > lrgTrkBlkCnt) {
                        lrgTrkBlkCnt = (ushort)blockCount;
                    }
                }
            }


            stream.Position = 0;
            stream.SetLength(0);
            Woz newDisk = new Woz(stream, appHook);
            MemoryStream tmpStream = new MemoryStream();
            tmpStream.SetLength(TRKS_DATA_START);   // enough for header+INFO+TMAP+TRKS descriptors
            byte[] tmpBuf = tmpStream.GetBuffer();  // access the sized buffer directly

            newDisk.FileRevision = WozKind.Woz2;
            newDisk.DiskKind = mediaKind;
            newDisk.Info = new Woz_Info(WOZ_CREATOR_NAME, mediaKind, lrgTrkBlkCnt);

            // Write header and INFO.
            newDisk.WriteHeaderAndInfo(tmpBuf);

            // Write the TMAP.
            int offset = TMAP_CHUNK_START - CHUNK_HDR_LEN;
            RawData.WriteU32LE(tmpBuf, ref offset, ID_TMAP);
            RawData.WriteU32LE(tmpBuf, ref offset, TMAP_LENGTH);
            for (int i = 0; i < SectorCodec.MAX_TRACK_35; i++) {
                tmpBuf[offset + i * 2] = (byte)(i * numSides);
                if (numSides == 1) {
                    tmpBuf[offset + i * 2 + 1] = 0xff;
                } else {
                    tmpBuf[offset + i * 2 + 1] = (byte)(i * numSides + 1);
                }
            }

            // Write the TRKS chunk.  For each track we write the descriptor into the
            // pre-allocated area, then append the track data to the file.  If sides==1 we
            // only write 80 entries.
            offset = TRKS_CHUNK_START;                          // +256
            ushort startBlock = TRKS_DATA_START / 512;          // block 3
            tmpStream.Position = startBlock * 512;
            for (int i = 0; i < tracks.Length; i++) {
                ushort blockCount = (ushort)((lengths[i] + 4095) / 4096);
                RawData.WriteU16LE(tmpBuf, ref offset, startBlock);
                RawData.WriteU16LE(tmpBuf, ref offset, blockCount);
                RawData.WriteU32LE(tmpBuf, ref offset, (uint)lengths[i]);

                tmpStream.Write(tracks[i], 0, (lengths[i] + 7) / 8);

                startBlock += blockCount;

                // Update the length to ensure we fill out the final block.
                tmpStream.SetLength(startBlock * 512);
                tmpStream.Position = tmpStream.Length;

                // Reacquire the byte[] in case the length changed caused a reallocation
                // inside the MemoryStream.
                tmpBuf = tmpStream.GetBuffer();
            }

            // Go back and set the TRKS chunk header.  Leave the stream positioned at the end.
            Debug.Assert(tmpStream.Length % 512 == 0);
            uint trksLength = (uint)(tmpStream.Length - TRKS_CHUNK_START);
            offset = TRKS_CHUNK_START - 8;
            RawData.WriteU32LE(tmpBuf, ref offset, ID_TRKS);
            RawData.WriteU32LE(tmpBuf, ref offset, trksLength);

            // Use AddMETA() instead.
            //// Add a default META chunk to the end.
            //newDisk.Metadata = Woz_Meta.CreateMETA();
            //byte[] metaData = newDisk.Metadata.Serialize(out int metaOffset, out int metaLength);
            //RawData.WriteU32LE(tmpStream, ID_META);
            //RawData.WriteU32LE(tmpStream, (uint)metaLength);
            //tmpStream.Write(metaData, metaOffset, metaLength);

            // Compute the full-file CRC.  Use the stream length, not the buffer length,
            // because MemoryStream will oversize while in expand mode.
            tmpBuf = tmpStream.GetBuffer();
            uint crc = CRC32.OnBuffer(0, tmpBuf, HEADER_LEN, (int)(tmpStream.Length - HEADER_LEN));
            RawData.SetU32LE(tmpBuf, 8, crc);

            // Copy the whole thing to the output stream.
            stream.Write(tmpBuf, 0, (int)tmpStream.Length);

            // Finish setting up the object, and return it.
            newDisk.mBaseData = tmpBuf;
            newDisk.mBaseLength = (int)tmpStream.Length;
            newDisk.ChunkAccess = new GatedChunkAccess(new NibbleChunkAccess(newDisk, codec));
            return newDisk;
        }

        /// <summary>
        /// Writes the 12-byte header, then the INFO chunk, at the start of the buffer.
        /// </summary>
        private void WriteHeaderAndInfo(byte[] buffer) {
            // Write signature, leave room for CRC.
            if (FileRevision == WozKind.Woz1) {
                Array.Copy(SIGNATURE1, buffer, SIGNATURE1.Length);
            } else {
                Array.Copy(SIGNATURE2, buffer, SIGNATURE2.Length);
            }

            int offset = HEADER_LEN;
            RawData.WriteU32LE(buffer, ref offset, ID_INFO);
            RawData.WriteU32LE(buffer, ref offset, INFO_LENGTH);

            byte[] data = Info.GetData();
            Array.Copy(data, 0, buffer, INFO_CHUNK_START, INFO_LENGTH);
        }

        /// <summary>
        /// Parses the contents of a WOZ file.
        /// </summary>
        private void Parse() {
            if (mDataStream.Length < 256 || mDataStream.Length > MAX_FILE_LEN) {
                // Too big, or too small to hold signature/INFO/TMAP, much less TRKS.
                throw new InvalidDataException("Incompatible data stream");
            }

            // Read the entire file into memory.
            mBaseData = new byte[mDataStream.Length];
            mBaseLength = (int)mDataStream.Length;
            mDataStream.Position = 0;
            mDataStream.ReadExactly(mBaseData, 0, (int)mDataStream.Length);
            if (RawData.CompareBytes(mBaseData, SIGNATURE1, SIGNATURE1.Length)) {
                FileRevision = WozKind.Woz1;
            } else if (RawData.CompareBytes(mBaseData, SIGNATURE2, SIGNATURE2.Length)) {
                FileRevision = WozKind.Woz2;
            } else {
                throw new InvalidDataException("Incompatible data stream");
            }

            // Test the file CRC.
            uint crc32 = RawData.GetU32LE(mBaseData, 8);
            uint calcCrc = CRC32.OnBuffer(0, mBaseData, HEADER_LEN, mBaseData.Length - HEADER_LEN);
            if (crc32 != calcCrc) {
                Notes.AddE("File CRC mismatch");
                IsDubious = true;
                // keep going
            }

            int metaChunkOffset = -1, metaChunkLength = -1;

            // Walk the chunk list, verifying the layout.
            int posn = INFO_CHUNK_START - CHUNK_HDR_LEN;
            while (posn < mBaseData.Length) {
                if (mBaseData.Length - posn < CHUNK_HDR_LEN) {
                    Notes.AddW("Found extra bytes at end of file");
                    break;
                }
                uint id = RawData.GetU32LE(mBaseData, posn + 0);
                uint length = RawData.GetU32LE(mBaseData, posn + 4);
                if (length != (int)length) {
                    // The length gets cast to an int later on, so if this doesn't hold some
                    // things will break.  We're not expecting files to exceed a few MB so
                    // this should never happen.
                    throw new InvalidDataException("Excessively large chunk: " + length);
                }
                posn += 8;

                Debug.WriteLine(" Chunk " + RawData.StringifyU32LE(id) + " len=" + length);

                // Verify placement of standard chunks.
                if ((id == ID_INFO && posn != INFO_CHUNK_START) ||
                        (id == ID_TMAP && posn != TMAP_CHUNK_START) ||
                        (id == ID_TRKS && posn != TRKS_CHUNK_START)) {
                    Notes.AddE("INFO/TMAP/TRKS not at expected positions");
                    IsDubious = true;
                    break;
                }
                // Record the position of interesting optional chunks.
                if (id == ID_META) {
                    metaChunkOffset = posn;
                    metaChunkLength = (int)length;
                } else if (id == ID_FLUX) {
                    mFluxChunkOffset = posn;
                    if (length != FLUX_LENGTH) {
                        Notes.AddE("Unexpected length on FLUX chunk: " + length);
                        IsDubious = true;
                    }
                }

                if (posn > mBaseData.Length - length) {
                    Notes.AddE("Found bad chunk at file offset " + (posn - CHUNK_HDR_LEN));
                    IsDubious = true;
                    break;
                }
                posn += (int)length;
            }

            // Extract the INFO chunk.  Validate the items that are critical for us.
            Info = new Woz_Info(mBaseData, INFO_CHUNK_START);
            if (Info.DiskType != 1 & Info.DiskType != 2) {
                throw new InvalidDataException("Unknown disk type: " + Info.DiskType);
            }
            if (Info.Version > 1) {
                if (Info.DiskSides != 1 && Info.DiskSides != 2) {
                    throw new InvalidDataException("Unexpected number of sides: " + Info.DiskSides);
                }
            }
            if (Info.DiskType == 1) {
                DiskKind = MediaKind.GCR_525;
            } else {
                if (Info.Version > 1 && Info.DiskSides == 1) {
                    DiskKind = MediaKind.GCR_SSDD35;
                } else {
                    DiskKind = MediaKind.GCR_DSDD35;
                }
            }
            if (Info.Version >= 3) {
                if (Info.FluxBlock != 0 && Info.LargestFluxTrack != 0) {
                    Notes.AddI("Found FLUX data, writing will be disabled");
                    HasFlux = true;
                }
            }

            // Extract the META chunk, if it exists.  If parsing fails, the object may be null.
            if (metaChunkOffset >= 0) {
                MetaChunk = Woz_Meta.ParseMETA(mBaseData, metaChunkOffset, metaChunkLength,
                    mAppHook);
            }
        }

        /// <summary>
        /// Generates a MemoryStream with the full WOZ file contents.  We need to generate new
        /// INFO and META chunks, but otherwise we're just copying data over.
        /// </summary>
        private MemoryStream GenerateStream() {
            // Compute the size of the first 3 chunks (INFO, TMAP, TRKS).  The first two are
            // fixed, so really we just need the TRKS length value.
            int offset = TRKS_CHUNK_START - CHUNK_HDR_LEN;
            uint trksId = RawData.ReadU32LE(mBaseData, ref offset);
            Debug.Assert(trksId == ID_TRKS);
            uint trksLength = RawData.ReadU32LE(mBaseData, ref offset);
            int baseLength = offset + (int)trksLength;
            if (FileRevision == WozKind.Woz1) {
                // TRKS entries are 13*512=6656 bytes each, starting at offset 256.
                Debug.Assert(baseLength % 512 == 256);
            } else {
                // TRKS entries are variable-length, but aligned and padded to 512-byte boundaries.
                Debug.Assert(baseLength % 512 == 0);
            }

            MemoryStream newStream = new MemoryStream();
            newStream.SetLength(baseLength);        // increase size to span INFO+TMAP+TRKS

            // Write the signature and INFO block.
            byte[] bufPtr = newStream.GetBuffer();
            WriteHeaderAndInfo(bufPtr);

            // Copy TMAP and TRKS.
            offset = TMAP_CHUNK_START - CHUNK_HDR_LEN;
            Array.Copy(mBaseData, offset, bufPtr, offset, baseLength - offset);

            // Append any non-META chunks to the end of the file.
            newStream.Position = baseLength;
            offset = baseLength;
            while (offset < mBaseLength) {
                uint id = RawData.GetU32LE(mBaseData, offset);
                uint chunkLen = RawData.GetU32LE(mBaseData, offset + 4);
                Debug.Assert(chunkLen != 0);

                if (id != ID_META) {
                    Debug.WriteLine("Copying ID=" + RawData.StringifyU32LE(id));
                    newStream.Write(mBaseData, offset, (int)chunkLen + CHUNK_HDR_LEN);
                }

                offset += (int)chunkLen + CHUNK_HDR_LEN;
            }

            // Generate and append the current META.
            if (MetaChunk != null) {
                byte[] data = MetaChunk.Serialize(out int metaOffset, out int metaLength);
                RawData.WriteU32LE(newStream, ID_META);
                RawData.WriteU32LE(newStream, (uint)metaLength);
                newStream.Write(data, metaOffset, metaLength);
            }

            // Finally, compute the CRC.
            bufPtr = newStream.GetBuffer();
            uint calcCrc =
                CRC32.OnBuffer(0, bufPtr, HEADER_LEN, (int)newStream.Length - HEADER_LEN);
            RawData.SetU32LE(bufPtr, 8, calcCrc);
            return newStream;
        }

        // INibbleDataAccess
        public bool GetTrackBits(uint trackNum, uint trackFraction,
                [NotNullWhen(true)] out CircularBitBuffer? cbb) {
            Debug.Assert(FileRevision == WozKind.Woz1 || FileRevision == WozKind.Woz2);

            int tmapIndex;
            switch (DiskKind) {
                case MediaKind.GCR_525:
                    if (trackNum < 0 || trackNum > SectorCodec.MAX_TRACK_525) {
                        throw new ArgumentOutOfRangeException("Invalid track: " + trackFraction);
                    }
                    if (trackFraction < 0 || trackFraction > 3) {
                        throw new ArgumentOutOfRangeException("Invalid trackFraction: " +
                            trackFraction);
                    }
                    tmapIndex = (int)(trackNum * 4 + trackFraction);
                    break;
                case MediaKind.GCR_SSDD35:
                case MediaKind.GCR_DSDD35:
                    if (trackNum < 0 || trackNum > SectorCodec.MAX_TRACK_35) {
                        throw new ArgumentOutOfRangeException("Invalid track: " + trackFraction);
                    }
                    int diskSides = (Info.Version > 1) ? Info.DiskSides : 1;
                    if (trackFraction < 0 || trackFraction >= diskSides) {
                        throw new ArgumentOutOfRangeException("Invalid trackFraction: " +
                            trackFraction);
                    }
                    if (FileRevision == WozKind.Woz1) {
                        tmapIndex = (int)(trackNum + SectorCodec.MAX_TRACK_35 * trackFraction);
                    } else {
                        tmapIndex = (int)(trackNum * 2 + trackFraction);
                    }
                    break;
                default:
                    throw new InvalidOperationException("Invalid disk type: " + DiskKind);
            }

            if (HasFlux && mFluxChunkOffset != -1) {
                // Check the FLUX chunk, which is essentially the same as TMAP.  If it has a
                // non-0xff entry, we prefer it to TMAP.
                int fluxIndex = mBaseData[mFluxChunkOffset + tmapIndex];
                if (fluxIndex != NO_TRACK) {
                    // Find the flux data for this track.
                    Debug.WriteLine("Using FLUX track (" + fluxIndex + ") for T" +
                        trackNum + "/" + trackFraction);
                    int trkOffset = TRKS_CHUNK_START + fluxIndex * WOZ2_TRKS_ENTRY_LEN;
                    ushort startBlock = RawData.GetU16LE(mBaseData, trkOffset);
                    int fluxOffset = startBlock * WOZ_BLOCK_SIZE;
                    int fluxCount = (int)RawData.GetU32LE(mBaseData, trkOffset + 4);
                    byte[]? bitData = FluxToBits(fluxOffset, fluxCount, out int fbitCount);
                    if (bitData == null) {
                        cbb = null;
                        return false;
                    }
                    cbb = new CircularBitBuffer(bitData, 0, 0, fbitCount, mBufferModFlag, true);
                    return true;
                }
            }

            int trksIndex = mBaseData[TMAP_CHUNK_START + tmapIndex];
            if (trksIndex == NO_TRACK) {
                cbb = null;
                return false;
            }

            int nibOffset, bitCount;
            if (FileRevision == WozKind.Woz1) {
                // Each TRK is 6646 bytes for nibble data storage, followed by 10 bytes of
                // length and write-hint data.
                nibOffset = TRKS_CHUNK_START + trksIndex * WOZ1_TRKS_ENTRY_LEN;
                bitCount = RawData.GetU16LE(mBaseData, nibOffset + WOZ1_TRKS_BIT_COUNT_OFFSET);
            } else {
                // Each TRK entry holds 8 bytes of data.  The track data location is specified
                // by absolute file offset / 512.
                int trkOffset = TRKS_CHUNK_START + trksIndex * WOZ2_TRKS_ENTRY_LEN;
                ushort startBlock = RawData.GetU16LE(mBaseData, trkOffset);
                nibOffset = startBlock * WOZ_BLOCK_SIZE;
                bitCount = (int)RawData.GetU32LE(mBaseData, trkOffset + 4);
            }

            // Sometimes a disk has a zero-length track (e.g. ultima_iii_1a_program.woz).
            if (bitCount < CircularBitBuffer.MIN_BITS) {
                cbb = null;
                return false;
            }

            // Return a new CBB for each request.
            cbb = new CircularBitBuffer(mBaseData, nibOffset, 0, bitCount, mBufferModFlag,
                    IsReadOnly);
            return true;
        }

        /// <summary>
        /// Converts a flux track to a bit track.
        /// </summary>
        /// <remarks>
        /// <para>This destroys the value of having a flux image, but since we're not an emulator
        /// there wasn't much in it for us anyway.</para>
        /// <para>Consider this "experimental".</para>
        /// </remarks>
        private byte[]? FluxToBits(int offset, int count, out int bitCount) {
            // Sample from a 5.25" disk:
            // 068b60: 3c 23 3f 3c 21 1f 21 21 3e 41 3c 23 5c 5e 21 1f
            // 068b70: 1f 1f 24 3c 23 3e 3c 23 59 26 3c 41 5c 23 1f 1f
            // 068b80: 1f 23 5c 3e 23 3e 5e 3c 26 3a 23 3c 21 1f 1f 21
            //  [...]
            // 080c60: 3f 3e 3f 3b 21 21 3e 3c 21 1f 1f 20 1f 1f 1f 22
            // 080c70: ff ff 76 1e 1f 1f 1f 1f 20 22 3a 21 1f 1f 20 1f
            // 080c80: 1f 22 3b 21 1f 1f 20 1f 1f 23 3a 21 1f 1f 1f 20

            const int VERY_LONG_TRACK = 20000;      // 2x byte count of longest track on 3.5" disk

            int bitTime = Info.OptimalBitTiming;
            if (bitTime < 8 || bitTime > 64) {
                // Something is horribly wrong.
                bitCount = -1;
                return null;
            }

            // Allocate oversized buffer.  Every flux transition is a '1' bit with a variable
            // number of '0' bits, so it's hard to make an accurate output size guess.
            byte[] outBuf = new byte[VERY_LONG_TRACK];
            CircularBitBuffer cbb = new CircularBitBuffer(outBuf, 0, 0, VERY_LONG_TRACK * 8);
            byte[] data = mBaseData;

            int fluxTime = 0;
            while (count-- != 0) {
                byte time = data[offset++];
                fluxTime += time;
                if (time == 255) {      // flux time count to be continued next byte
                    continue;
                }

                cbb.WriteBit(1);
                fluxTime -= bitTime;
                while (fluxTime > bitTime / 2) {
                    cbb.WriteBit(0);
                    fluxTime -= bitTime;
                }

                fluxTime = 0;
            }

            // We could copy the data into a correctly-sized buffer, but no real need.
            // TODO: ought to check for wrap-around, though if the track is that long it
            //   wouldn't have fit on a physical disk anyway
            bitCount = cbb.BitPosition;
            return outBuf;
        }

        /// <summary>
        /// Throws an exception if the disk is not writable.
        /// </summary>
        private void CheckAccess() {
            if (IsReadOnly) {
                throw new NotSupportedException("Disk is read-only");
            }
        }

        #region Metadata

        /// <summary>
        /// Adds a META chunk to a file that doesn't have one.  No effect if the chunk exists.
        /// </summary>
        public void AddMETA() {
            CheckAccess();
            if (MetaChunk == null) {
                MetaChunk = Woz_Meta.CreateMETA();
            }
        }

        private const string INFO_PFX = "info:";
        private const string META_PFX = "meta:";

        // IMetadata
        public List<MetaEntry> GetMetaEntries() {
            List<MetaEntry> entries = new List<MetaEntry>();

            // We need to generate the list of Info entries that are appropriate for the
            // Info.Version.
            foreach (MetaEntry met in Woz_Info.sInfo1List) {
                entries.Add(new MetaEntry(INFO_PFX + met.Key, met.ValueType,
                    met.Description, met.ValueSyntax, met.CanEdit, false));
            }
            if (Info.Version > 1) {
                foreach (MetaEntry met in Woz_Info.sInfo2List) {
                    entries.Add(new MetaEntry(INFO_PFX + met.Key, met.ValueType,
                        met.Description, met.ValueSyntax, met.CanEdit, false));
                }
            }
            if (Info.Version > 2) {
                foreach (MetaEntry met in Woz_Info.sInfo3List) {
                    entries.Add(new MetaEntry(INFO_PFX + met.Key, met.ValueType,
                        met.Description, met.ValueSyntax, met.CanEdit, false));
                }
            }

            // We want to add the META entries in the order in which they appear in the
            // specification, because it's nice to have title/subtitle at the top.  The
            // values are stored in a key/value dictionary with no defined sort order, so
            // we want to pick them out one at a time.  Whatever is left are the user-supplied
            // values, which we can present in any order.
            if (MetaChunk != null) {
                Dictionary<string, string> metaEntries = MetaChunk.GetEntryDict();
                foreach (MetaEntry met in Woz_Meta.sStandardEntries) {
                    if (metaEntries.ContainsKey(met.Key)) {
                        entries.Add(new MetaEntry(META_PFX + met.Key, met.ValueType,
                            met.Description, met.ValueSyntax, met.CanEdit, false));
                        metaEntries.Remove(met.Key);
                    }
                }
                // Now copy whatever is left.
                foreach (string key in metaEntries.Keys) {
                    entries.Add(new MetaEntry(META_PFX + key));
                }
            }

            return entries;
        }

        // IMetadata
        public string? GetMetaValue(string key, bool verbose) {
            string? value = null;
            if (key.StartsWith(INFO_PFX)) {
                value = Info.GetValue(key.Substring(INFO_PFX.Length), verbose);
            } else if (key.StartsWith(META_PFX)) {
                if (MetaChunk != null) {
                    value = MetaChunk.GetValue(key.Substring(META_PFX.Length));
                }
            } else {
                Debug.WriteLine("Unknown prefix on WOZ meta key: " + key);
            }
            return value;
        }

        // IMetadata
        public bool TestMetaValue(string key, string value) {
            if (key.StartsWith(INFO_PFX)) {
                return Info.TestValue(key.Substring(INFO_PFX.Length), value);
            } else if (key.StartsWith(META_PFX)) {
                if (MetaChunk != null) {
                    return MetaChunk.TestValue(key.Substring(META_PFX.Length), value,
                        out string errMsg);
                } else {
                    return false;
                }
            } else {
                return false;
            }
        }

        // IMetadata
        public void SetMetaValue(string key, string value) {
            if (key.StartsWith(INFO_PFX)) {
                Info.SetValue(key.Substring(INFO_PFX.Length), value);
            } else if (key.StartsWith(META_PFX)) {
                if (MetaChunk != null) {
                    MetaChunk.SetValue(key.Substring(META_PFX.Length), value);
                }
                // else ignore attempt
            } else {
                throw new ArgumentException("unknown prefix on key '" + key + "'");
            }
        }

        // IMetadata
        public bool DeleteMetaEntry(string key) {
            // Cannot delete "info:" keys.
            if (key.StartsWith(META_PFX) && MetaChunk != null) {
                return MetaChunk.DeleteEntry(key.Substring(META_PFX.Length));
            }
            return false;
        }

        #endregion Metadata

        public override string ToString() {
            return "[WOZ" + FileRevision + " " + DiskKind + "]";
        }
    }
}
