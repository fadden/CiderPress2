﻿/*
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

using CommonUtil;
using DiskArc.Disk;
using static DiskArc.Defs;

namespace DiskArc {
    /// <summary>
    /// Chunk access for nibble images.  Presents the raw nibble data as tracks/sectors or blocks.
    /// Used for images of 5.25" and 3.5" floppy disks.
    /// </summary>
    internal class NibbleChunkAccess : IChunkAccess {
        protected const int TAG_BYTE_COUNT = 12;

        //
        // IChunkAccess properties.
        //

        public bool IsReadOnly {
            get {
                // If we don't know how to calculate the checksums, we definitely shouldn't
                // be writing to the disk.  (Strictly speaking, we only need to calculate the
                // data checksums unless we're formatting the disk.)
                return !NibbleCodec.DoTestAddrChecksum || !NibbleCodec.DoTestDataChecksum ||
                    mNibbleAccess.IsReadOnly;
            }
        }

        public bool IsModified { get; set; }
        public long ReadCount { get; protected set; }
        public long WriteCount { get; protected set; }

        public long FormattedLength { get; protected set; }

        public uint NumTracks { get; protected set; }
        public uint NumSectorsPerTrack { get; protected set; }
        public bool HasSectors { get; protected set; }
        public bool HasBlocks { get; protected set; }

        public SectorOrder FileOrder { get { return SectorOrder.Physical; } set { } }

        public SectorCodec NibbleCodec { get; protected set; }

        //
        // Innards.
        //

        /// <summary>
        /// Nibble data provider.
        /// </summary>
        protected INibbleDataAccess mNibbleAccess;

        /// <summary>
        /// Number of disk sides (for 3.5" disks).
        /// </summary>
        protected int mNumSides;

        /// <summary>
        /// Temporary storage for a disk access unit.  Holds at least 524 bytes.
        /// </summary>
        protected byte[] mTmpBuf524 = RawData.EMPTY_BYTE_ARRAY;

        /// <summary>
        /// 40 tracks * 4 half/quarter for 5.25", 80 tracks * 2 sides for 3.5"
        /// </summary>
        protected const int MAX_TRACK_ENTRIES = 160;

        /// <summary>
        /// Cached track/sector data.
        /// </summary>
        protected class TrackEntry {
            public CircularBitBuffer mCirc;
            public List<SectorPtr> mSectors;

            public TrackEntry(CircularBitBuffer circ, List<SectorPtr> sectors) {
                mCirc = circ;
                mSectors = sectors;
            }
            public SectorPtr? GetSector(uint sct) {
                foreach (SectorPtr sctPtr in mSectors) {
                    if (sctPtr.Sector == sct) {
                        return sctPtr;
                    }
                }
                return null;
            }
            public SectorPtr? GetUndamagedSector(uint sct, bool isNoDataOkay, out string msg) {
                SectorPtr? sctPtr = GetSector(sct);
                if (sctPtr != null) {
                    msg = string.Empty;
                    if (sctPtr.IsAddrDamaged || sctPtr.IsDataDamaged) {
                        msg = "Sector is damaged";
                    }
                    if (sctPtr.DataFieldBitOffset == -1 && !isNoDataOkay) {
                        msg = "Sector has no data field";
                    }
                } else {
                    msg = "Sector not found";
                }
                return sctPtr;
            }
        }
        protected TrackEntry?[] mTrackEntries = new TrackEntry[MAX_TRACK_ENTRIES];

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="nibbles">Nibble data provider.</param>
        /// <param name="codec">Sector encoder/decoder.</param>
        public NibbleChunkAccess(INibbleDataAccess nibbles, SectorCodec codec) {
            // TODO: add a 5.25" 80-track mode that remaps track numbers to use the half-tracks.
            mNibbleAccess = nibbles;
            NibbleCodec = codec;

            mTmpBuf524 = new byte[Math.Max(524, codec.DecodedSectorSize)];

            HasBlocks = true;
            if (nibbles.DiskKind == MediaKind.GCR_525) {
                // 5.25" disks may be captured as 35 or 40 tracks.  If we can read some sectors
                // from track 39, treat it as a 40-track image.  Measuring at a finer granularity
                // (e.g. 37 tracks) may confuse the filesystem detectors.  Some formats, like
                // Trackstar, store 40 tracks even if the last 5 are just garbage, so we can't
                // rely on the number of tracks presented by the nibble data source.
                //
                HasSectors = true;
                NumTracks = 35;
                if (nibbles.GetTrackBits(39, 0, out CircularBitBuffer? trackData)) {
                    // See what the codec can make of it.
                    List<SectorPtr> sectorPtrs = codec.FindSectors(39, 0, trackData);

                    // Some Trackstar images seem to copy track 34 into the storage for
                    // tracks 35-39, so we find sectors but the address fields consistently
                    // have the wrong track value.  By checking for address field damage we
                    // can correctly ignore those tracks.
                    int goodCount = 0;
                    foreach (SectorPtr ptr in sectorPtrs) {
                        if (!ptr.IsAddrDamaged && !ptr.IsDataDamaged) {
                            goodCount++;
                        }
                    }
                    if (goodCount > 4) {
                        // Seems like there's stuff here.
                        NumTracks = 40;
                    }
                }

                if (codec.DataEncoding == SectorCodec.NibbleEncoding.GCR53) {
                    NumSectorsPerTrack = 13;
                    HasBlocks = false;
                } else {
                    NumSectorsPerTrack = 16;
                }

                FormattedLength = NumTracks * NumSectorsPerTrack * SECTOR_SIZE;
                mNumSides = 1;
            } else {
                // GCR 3.5" disks have a fixed number of tracks, but they don't have a fixed number
                // of sectors per track, so there's no value in filling out those geometry values.
                // To the calling code, the disk is strictly block-addressable.

                if (nibbles.DiskKind == MediaKind.GCR_SSDD35) {
                    FormattedLength = 400 * 1024;
                    mNumSides = 1;
                } else if (nibbles.DiskKind == MediaKind.GCR_DSDD35) {
                    FormattedLength = 800 * 1024;
                    mNumSides = 2;
                } else {
                    Debug.Assert(false);
                    throw new InvalidOperationException("Unsupported media type");
                }
                Debug.Assert(codec.DecodedSectorSize == BLOCK_SIZE + TAG_BYTE_COUNT);
            }
        }

        protected void CheckSectorArgs(uint trk, uint sct, bool isWrite) {
            if (!HasSectors) {
                throw new InvalidOperationException("No sectors");
            }
            if (trk > NumTracks) {
                throw new ArgumentOutOfRangeException("Track out of range: " + trk +
                    " (max " + NumTracks + ")");
            }
            if (sct > NumSectorsPerTrack) {
                throw new ArgumentOutOfRangeException("Sector out of range: " + sct +
                    " (max " + NumSectorsPerTrack + ")");
            }
            if (isWrite && IsReadOnly) {
                throw new InvalidOperationException("Chunk access is read-only");
            }
        }

        protected void CheckBlockArgs(uint block, bool isWrite) {
            if (!HasBlocks) {
                throw new InvalidOperationException("No blocks");
            }
            if (block >= FormattedLength / BLOCK_SIZE) {
                throw new ArgumentOutOfRangeException("Block out of range: " + block);
            }
            if (isWrite && IsReadOnly) {
                throw new InvalidOperationException("Chunk access is read-only");
            }
        }

        /// <summary>
        /// Returns the track entry for the specified track, or null if the track isn't available.
        /// Finds the sectors on first use.
        /// </summary>
        protected TrackEntry? GetTrackEntry(uint trackNum, uint trackFraction) {
            uint index;
            if (mNibbleAccess.DiskKind == MediaKind.GCR_525) {
                index = trackNum * 4 + trackFraction;       // quarter tracks
            } else {
                index = trackNum * 2 + trackFraction;       // two sides
            }
            if (mTrackEntries[index] != null) {
                return mTrackEntries[index];
            }
            // Don't have it yet, grab it.
            if (!mNibbleAccess.GetTrackBits(trackNum, trackFraction, out CircularBitBuffer? circ)) {
                // Requested track does not exist.
                return null;
            }
            List<SectorPtr> sectors = NibbleCodec.FindSectors(trackNum, trackFraction, circ);
            // Create an entry for it.
            TrackEntry newEntry = new TrackEntry(circ, sectors);
            mTrackEntries[index] = newEntry;
            return newEntry;
        }

        // IChunkAccess
        public void ReadSector(uint trk, uint sct, byte[] data, int offset) {
            ReadSector(trk, sct, data, offset, SectorOrder.DOS_Sector);
        }

        // IChunkAccess
        public void ReadSector(uint trk, uint sct, byte[] data, int offset,
                SectorOrder requestOrder) {
            CheckSectorArgs(trk, sct, false);
            uint physSector = SectorSetup(trk, sct, requestOrder, out TrackEntry trkEnt);
            DoReadSector(trk, physSector, trkEnt, data, offset);
            ReadCount++;
        }

        /// <summary>
        /// Finds the TrackEntry for the specified track, and determines the physical sector.
        /// </summary>
        /// <param name="trk">Track number.</param>
        /// <param name="sct">Sector number.</param>
        /// <param name="requestOrder">Requested sector order.</param>
        /// <param name="trkEnt">Result: TrackEntry for this track.</param>
        /// <returns>Physical sector number.</returns>
        /// <exception cref="IOException">No data exists for this track.</exception>
        protected uint SectorSetup(uint trk, uint sct, SectorOrder requestOrder,
                out TrackEntry trkEnt) {
            uint physSector = sct;
            if (NumSectorsPerTrack == 16) {
                // Apply requested sector skew.
                //physSector = GeneralChunkAccess.sDos2Phys[sct];
                uint[] skewMap = GeneralChunkAccess.GetSkew2PhysMap(requestOrder);
                physSector = skewMap[sct];
            }
            TrackEntry? te = GetTrackEntry(trk, 0);
            if (te == null) {
                // TODO: maybe return null trkEnt and check it instead?  OTOH this should be rare.
                throw new IOException("Track " + trk + " not found");
            }
            trkEnt = te;

            return physSector;
        }

        /// <summary>
        /// Finds the sector and issues the codec read call.
        /// </summary>
        protected void DoReadSector(uint trk, uint physSct, TrackEntry trkEnt,
                byte[] data, int offset) {
            SectorPtr? sctPtr = trkEnt.GetUndamagedSector(physSct, true, out string msg);
            if (sctPtr == null) {
                throw new BadBlockException(msg, trk, 0, physSct, true);
            }
            if (!NibbleCodec.ReadSector(trkEnt.mCirc, sctPtr, data, offset)) {
                throw new BadBlockException("Unable to read sector", trk, 0, physSct, true);
            }
            //Debug.WriteLine("Read T" + trk + " S" + physSct);
        }

        // IChunkAccess
        public void ReadBlock(uint block, byte[] data, int offset) {
            DoReadBlock(block, data, offset, GeneralChunkAccess.sProdos2Phys);
        }

        // IChunkAccess
        public void ReadBlock(uint block, byte[] data, int offset, SectorOrder order) {
            uint[] skewMap = GeneralChunkAccess.GetSkew2PhysMap(order);
            DoReadBlock(block, data, offset,  skewMap);
        }

        protected void DoReadBlock(uint block, byte[] data, int offset, uint[] skewMap) {
            CheckBlockArgs(block, false);
            if (HasSectors) {
                // Read the block as a pair of sectors, using the requested skewing.
                if (NumSectorsPerTrack != 16) {
                    throw new InvalidOperationException("Wrong number of sectors to be here");
                }
                uint trk = block >> 3;              // block / (NumSectorsPerTrack/2)
                uint sct = (block & 0x07) << 1;     // (block % (NumSectorsPerTrack/2)) * 2
                if (trk >= NumTracks) {
                    // Shouldn't be possible; we checked block arg earlier.
                    throw new IOException("Block too large: " + block);
                }
                TrackEntry? trkEnt = GetTrackEntry(trk, 0);
                if (trkEnt == null) {
                    throw new IOException("Track " + trk + " not found");
                }
                DoReadSector(trk, skewMap[sct], trkEnt, data, offset);
                DoReadSector(trk, skewMap[sct + 1], trkEnt, data, offset + SECTOR_SIZE);
            } else {
                // Must be a 3.5" disk.
                uint cyl, head, sct;
                SectorCodec.BlockToCylHeadSct_GCR35(block, mNumSides, out cyl, out head, out sct);
                TrackEntry? trkEnt = GetTrackEntry(cyl, head);
                if (trkEnt == null) {
                    throw new IOException("Track " + cyl + "/" + head + " not found");
                }
                SectorPtr? sctPtr = trkEnt.GetUndamagedSector(sct, false, out string msg);
                if (sctPtr == null) {
                    throw new BadBlockException(msg, cyl, head, sct, false);
                }
                // The disk sector has 524 bytes, but we want to skip the first 12.  Read it
                // into a temporary buffer and copy the useful part out.
                Debug.Assert(NibbleCodec.DecodedSectorSize == 524);
                if (!NibbleCodec.ReadSector(trkEnt.mCirc, sctPtr, mTmpBuf524, 0)) {
                    throw new BadBlockException("Unable to read block", cyl, head, sct, false);
                }
                Array.Copy(mTmpBuf524, TAG_BYTE_COUNT, data, offset, BLOCK_SIZE);
            }
            ReadCount++;
        }

        // IChunkAccess
        public void WriteSector(uint trk, uint sct, byte[] data, int offset) {
            WriteSector(trk, sct, data, offset, SectorOrder.DOS_Sector);
        }

        // IChunkAccess
        public void WriteSector(uint trk, uint sct, byte[] data, int offset,
                SectorOrder requestOrder) {
            CheckSectorArgs(trk, sct, true);
            uint physSector = SectorSetup(trk, sct, requestOrder, out TrackEntry trkEnt);
            IsModified = true;
            DoWriteSector(trk, physSector, trkEnt, data, offset);
            WriteCount++;
        }

        /// <summary>
        /// Finds the sector and issues the codec write call.
        /// </summary>
        protected void DoWriteSector(uint trk, uint physSct, TrackEntry trkEnt,
                byte[] data, int offset) {
            // Don't check DataFieldBitOffset here; let the codec decide if it needs it.
            SectorPtr? sctPtr = trkEnt.GetUndamagedSector(physSct, true, out string msg);
            if (sctPtr == null) {
                throw new BadBlockException(msg, trk, 0, physSct, true);
            }
            if (!NibbleCodec.WriteSector(trkEnt.mCirc, sctPtr, data, offset)) {
                throw new BadBlockException("Unable to write sector", trk, 0, physSct, true);
            }
        }

        // IChunkAccess
        public void WriteBlock(uint block, byte[] data, int offset) {
            DoWriteBlock(block, data, offset, GeneralChunkAccess.sProdos2Phys);
        }

        // IChunkAccess
        public void WriteBlock(uint block, byte[] data, int offset, SectorOrder order) {
            uint[] skewMap = GeneralChunkAccess.GetSkew2PhysMap(order);
            DoWriteBlock(block, data, offset, skewMap);
        }

        protected void DoWriteBlock(uint block, byte[] data, int offset, uint[] skewMap) {
            CheckBlockArgs(block, true);
            IsModified = true;
            if (HasSectors) {
                // Write the block as a pair of sectors, using the requested skewing.
                if (NumSectorsPerTrack != 16) {
                    throw new InvalidOperationException("Wrong number of sectors to be here");
                }
                uint trk = block >> 3;              // block / (NumSectorsPerTrack/2)
                uint sct = (block & 0x07) << 1;     // (block % (NumSectorsPerTrack/2)) * 2
                if (trk >= NumTracks) {
                    throw new IOException("Block too large: " + block);
                }
                TrackEntry? trkEnt = GetTrackEntry(trk, 0);
                if (trkEnt == null) {
                    throw new IOException("Track " + trk + " not found");
                }
                DoWriteSector(trk, skewMap[sct], trkEnt, data, offset);
                DoWriteSector(trk, skewMap[sct + 1], trkEnt, data, offset + SECTOR_SIZE);
            } else {
                // Must be a 3.5" disk.
                uint cyl, head, sct;
                SectorCodec.BlockToCylHeadSct_GCR35(block, mNumSides, out cyl, out head, out sct);
                TrackEntry? trkEnt = GetTrackEntry(cyl, head);
                if (trkEnt == null) {
                    throw new IOException("Track " + cyl + "/" + head + " not found");
                }
                SectorPtr? sctPtr = trkEnt.GetUndamagedSector(sct, false, out string msg);
                if (sctPtr == null) {
                    throw new BadBlockException(msg, cyl, head, sct, false);
                }
                Debug.Assert(NibbleCodec.DecodedSectorSize == 524);
                //for (int i = 0; i < TAG_BYTE_COUNT; i++) {
                //    mTmpBuf524[i] = 0x00;
                //}
                // Read the current block, to preserve the tag bytes.  This slows us down (vs.
                // just zeroing them out), but if we're not operating on physical media then it
                // shouldn't be noticeable.
                if (!NibbleCodec.ReadSector(trkEnt.mCirc, sctPtr, mTmpBuf524, 0)) {
                    throw new BadBlockException("Unable to read block before write",
                        cyl, head, sct, false);
                }
                Array.Copy(data, offset, mTmpBuf524, TAG_BYTE_COUNT, BLOCK_SIZE);
                if (!NibbleCodec.WriteSector(trkEnt.mCirc, sctPtr, mTmpBuf524, 0)) {
                    throw new BadBlockException("Unable to write block", cyl, head, sct, false);
                }
            }
            WriteCount++;
        }

        // IChunkAccess
        public bool TestSector(uint trk, uint sct, out bool writable) {
            CheckSectorArgs(trk, sct, false);
            writable = false;
            uint physSct;
            TrackEntry trkEnt;
            try {
                physSct = SectorSetup(trk, sct, SectorOrder.DOS_Sector, out trkEnt);
            } catch (IOException) {
                return false;       // track does not exist
            }
            SectorPtr? sctPtr = trkEnt.GetUndamagedSector(physSct, true, out string msg);
            if (sctPtr == null) {
                return false;       // sector not found
            }
            if (sctPtr.DataFieldBitOffset == -1 &&
                    NibbleCodec.DataEncoding == SectorCodec.NibbleEncoding.GCR53) {
                writable = true;    // no data field; allowed for 13-sector disks
                return false;
            }
            if (!NibbleCodec.ReadSector(trkEnt.mCirc, sctPtr, mTmpBuf524, 0)) {
                return false;   // probably a bad checksum
            }
            writable = true;
            return true;
        }

        // IChunkAccess
        public bool TestBlock(uint block, out bool writable) {
            CheckBlockArgs(block, false);
            // Just read the block.  Read vs. write should only be a thing for GCR53, which is
            // only for 13-sector disks, which can't be handled as blocks.
            // TODO: this is inefficient if there are many errors because of the exceptions
            // being thrown.  We can avoid that by refactoring the code.
            try {
                ReadBlock(block, mTmpBuf524, 0);
                writable = true;
                return true;
            } catch (IOException) {
                writable = false;
                return false;
            }
        }

        // IChunkAccess
        public void Initialize() {
            if (IsReadOnly) {
                throw new InvalidOperationException("Chunk access is read-only");
            }
            IsModified = true;
            if (HasSectors) {
                for (uint trk = 0; trk < NumTracks; trk++) {
                    for (uint sct = 0; sct < NumSectorsPerTrack; sct++) {
                        WriteSector(trk, sct, GeneralChunkAccess.ZEROES, 0);
                    }
                }
            } else {
                for (uint blk = 0; blk < FormattedLength / BLOCK_SIZE; blk++) {
                    WriteBlock(blk, GeneralChunkAccess.ZEROES, 0);
                }
            }
        }
    }
}
