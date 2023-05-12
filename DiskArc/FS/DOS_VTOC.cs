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

using CommonUtil;
using static DiskArc.Defs;

namespace DiskArc.FS {
    /// <summary>
    /// Class for managing the Volume Table of Contents.  The sector holds the basic
    /// volume characteristics, a pointer to the start of the disk catalog, and the
    /// sector in-use bitmap.
    /// </summary>
    /// <remarks>
    /// The VTOC is expected to live on track 17 sector 0.  We leave that assumption to the
    /// caller, to make it easier to handle non-standard disks.
    /// </remarks>
    internal class DOS_VTOC {
        internal const int MAX_TRACK = 50;      // max tracks on standard disk; may be doubled

        // Data field accessors.
        public byte FirstCatTrk { get { return mData[0x01]; } }
        public byte FirstCatSct { get { return mData[0x02]; } }
        public byte VolumeNum {
            get { return mData[0x06]; }
            set { mData[0x06] = value; IsDirty = true; }
        }
        public byte LastAllocTrk {
            get { return mData[0x30]; }
            set { mData[0x30] = value; IsDirty = true; }
        }
        public int LastAllocDir {
            get { if (mData[0x31] < 0x80) { return 1; } else { return -1; } }
            set {
                if (value >= 0) { mData[0x31] = 0x01; } else { mData[0x31] = 0xff; }
                IsDirty = true;
            }
        }
        public byte NumTrks { get { return mData[0x34]; } }
        public byte NumScts { get { return mData[0x35]; } }

        /// <summary>
        /// Volume usage map.
        /// </summary>
        public VolumeUsage VolUsage { get; private set; }

        private IChunkAccess mChunkAccess;
        public byte VTOCTrk { get; private set; }
        public byte VTOCSct { get; private set; }

        private byte[] mData = new byte[SECTOR_SIZE];
        public bool IsDirty { get; private set; }

        /// <summary>
        /// Special handling for 80-track disks (from e.g. Basis 108).  The volume bitmap uses
        /// two bytes per track instead of 4, limiting the max sector count to 16.
        /// </summary>
        private bool IsDoubled { get { return (NumTrks > MAX_TRACK); } }


        /// <summary>
        /// Constructor.
        /// </summary>
        /// <remarks>
        /// <para>The caller will generally have validated the VTOC before creating this object,
        /// so we take the buffer as an argument to avoid re-reading the same sector.</para>
        /// </remarks>
        /// <param name="chunkAccess">Chunk data source.</param>
        /// <param name="trk">VTOC track.</param>
        /// <param name="sct">VTOC sector.</param>
        /// <param name="buf">VTOC sector data.</param>
        public DOS_VTOC(IChunkAccess chunkAccess, byte trk, byte sct, byte[] buf) {
            mChunkAccess = chunkAccess;
            VTOCTrk = trk;
            VTOCSct = sct;

            // Make a local copy of the sector contents.
            Array.Copy(buf, mData, SECTOR_SIZE);

            // Allocate the volume usage tracker, using the geometry specified in the VTOC.
            VolUsage = new VolumeUsage(NumTrks * NumScts);

            // Set the "in use" bits in the tracker.
            for (byte utrk = 0; utrk < NumTrks; utrk++) {
                for (byte usct = 0; usct < NumScts; usct++) {
                    if (IsSectorInUse(utrk, usct)) {
                        VolUsage.MarkInUse(TSToChunk(utrk, usct));
                    }
                }
            }
        }

#if DEBUG
        ~DOS_VTOC() { Debug.Assert(!IsDirty); }
#endif

        private uint TSToChunk(byte trk, byte sct) {
            return (uint)(trk * NumScts + sct);
        }

        /// <summary>
        /// Determines whether the specified disk sector appears to hold a valid VTOC.
        /// </summary>
        /// <param name="chunkAccess">Data source; holds disk geometry.</param>
        /// <param name="dataBuf">Buffer with sector data.</param>
        /// <returns>True if the VTOC looks valid.</returns>
        public static bool ValidateVTOC(IChunkAccess chunkAccess, byte[] dataBuf) {
            // Get the disk geometry.
            uint expectedScts = chunkAccess.NumSectorsPerTrack;
            uint expectedTrks = chunkAccess.NumTracks;

            // Get the format parameters from the VTOC.
            byte firstCatTrk = dataBuf[0x01];
            byte firstCatSct = dataBuf[0x02];
            byte numTrks = dataBuf[0x34];
            byte numScts = dataBuf[0x35];

            // We could weaken the requirement on having the number of tracks/sectors match
            // the expected geometry, but there isn't much else to test.
            if (dataBuf[0x27] != DOS.MAX_TS_PER_TSLIST ||
                    numTrks != expectedTrks || numScts != expectedScts ||
                    firstCatTrk == 0 || firstCatTrk >= numTrks || firstCatSct >= numScts) {
                return false;
            }

            // We support up to 50 tracks and 32 sectors with the standard definition.  However,
            // there are 80-track disks that handle the bitmap differently.
            if ((numScts > 16 && numTrks > MAX_TRACK) ||
                    (numScts <= 16 && numTrks > MAX_TRACK * 2)) {
                return false;
            }

            // TODO(maybe): check for garbage in the sector in-use map.  We can trivially mask
            //  it off, so it doesn't really matter, but it could indicate damage.
            return true;
        }

        /// <summary>
        /// Flushes VTOC changes to disk.
        /// </summary>
        public void Flush() {
            if (IsDirty) {
                mChunkAccess.WriteSector(VTOCTrk, VTOCSct, mData, 0);
                IsDirty = false;
            }
        }

        /// <summary>
        /// Calculates the number of available sectors.
        /// </summary>
        public int CalcFreeSectors() {
            // Mask off the unavailable sectors, just in case there's garbage.
            int freeSectors = 0;
            for (int i = 0; i < NumTrks; i++) {
                if (IsDoubled) {
                    ushort trackMask = (ushort)(0xffff << (16 - NumScts));
                    ushort mapBits = RawData.GetU16BE(mData, 0x38 + i * 2);
                    freeSectors += BitTwiddle.CountOneBits(mapBits & trackMask);
                } else {
                    uint trackMask = 0xffffffff << (32 - NumScts);
                    uint mapBits = RawData.GetU32BE(mData, 0x38 + i * 4);
                    freeSectors += BitTwiddle.CountOneBits((int)(mapBits & trackMask));
                }
            }
            return freeSectors;
        }

        /// <summary>
        /// Determines whether the track/sector is in use.
        /// </summary>
        /// <returns>True if the sector is in use.</returns>
        public bool IsSectorInUse(byte trk, byte sct) {
            if (IsDoubled) {
                ushort trkBits = RawData.GetU16BE(mData, 0x38 + trk * 2);
                ushort mask = (ushort)(1U << (16 - NumScts + sct));
                return (trkBits & mask) == 0;
            } else {
                uint trkBits = RawData.GetU32BE(mData, 0x38 + trk * 4);
                uint mask = 1U << (32 - NumScts + sct);
                return (trkBits & mask) == 0;   // '1' means "free"
            }
        }

        public void MarkSectorUsed(byte trk, byte sct, IFileEntry entry) {
            SetSectorState(trk, sct, true);
            VolUsage.AllocChunk(TSToChunk(trk, sct), entry);
        }

        public void MarkSectorUnused(byte trk, byte sct) {
            SetSectorState(trk, sct, false);
            VolUsage.FreeChunk(TSToChunk(trk, sct));
        }

        private void SetSectorState(byte trk, byte sct, bool inUse) {
            if (IsDoubled) {
                ushort trkBits = RawData.GetU16BE(mData, 0x38 + trk * 2);
                ushort mask = (ushort)(1U << (16 - NumScts + sct));
                if (inUse) {
                    trkBits &= (ushort)~mask;
                } else {
                    trkBits |= mask;    // '1' means "free"
                }
                RawData.SetU16BE(mData, 0x38 + trk * 2, trkBits);
            } else {
                uint trkBits = RawData.GetU32BE(mData, 0x38 + trk * 4);
                uint mask = 1U << (32 - NumScts + sct);
                if (inUse) {
                    trkBits &= ~mask;
                } else {
                    trkBits |= mask;    // '1' means "free"
                }
                RawData.SetU32BE(mData, 0x38 + trk * 4, trkBits);
            }
            IsDirty = true;
        }

        /// <summary>
        /// Allocates a new sector.
        /// </summary>
        /// <param name="entry">File entry that the block is assigned to, or NO_ENTRY for
        ///   system storage.</param>
        /// <param name="allocTrk">Result: allocated track number.</param>
        /// <param name="allocSct">Result: allocated sector number./param>
        /// <exception cref="DiskFullException">Unable to allocate sector.</exception>
        public void AllocSector(IFileEntry entry, out byte allocTrk, out byte allocSct) {
            byte trk, sct;
            if (IsDoubled) {
                // 0xfff8 for 13-sector
                // 0xffff for 16-sector
                ushort trackMask = (ushort)(0xffff << (16 - NumScts));

                ushort trkBits = 0;
                for (trk = VTOCTrk; trk > 0; trk--) {
                    trkBits = RawData.GetU16BE(mData, 0x38 + trk * 2);
                    if ((trkBits & trackMask) != 0) {
                        break;
                    }
                }
                if (trk == 0) {
                    for (trk = (byte)(VTOCTrk + 1); trk < NumTrks; trk++) {
                        trkBits = RawData.GetU16BE(mData, 0x38 + trk * 2);
                        if ((trkBits & trackMask) != 0) {
                            break;
                        }
                    }
                }
                if (trk == NumTrks) {
                    throw new DiskFullException("Disk full");
                }
                Debug.Assert(trkBits != 0);

                sct = (byte)(NumScts - 1);
                while (true) {
                    if ((trkBits & 0x8000) != 0) {
                        break;
                    }
                    trkBits <<= 1;
                    sct--;
                }
            } else {
                // 0xfff80000 for 13-sector
                // 0xffff0000 for 16-sector
                // 0xffffffff for 32-sector
                uint trackMask = 0xffffffff << (32 - NumScts);

                // Find a track with a free sector.  To mimic DOS behavior, we start in the
                // VTOC track and scan toward 0, then scan toward the end of the disk.  Don't
                // assume the catalog track has no free sectors.
                uint trkBits = 0;
                for (trk = VTOCTrk; trk > 0; trk--) {
                    trkBits = RawData.GetU32BE(mData, 0x38 + trk * 4);
                    if ((trkBits & trackMask) != 0) {
                        break;
                    }
                }
                if (trk == 0) {
                    // Try the other direction.
                    for (trk = (byte)(VTOCTrk + 1); trk < NumTrks; trk++) {
                        trkBits = RawData.GetU32BE(mData, 0x38 + trk * 4);
                        if ((trkBits & trackMask) != 0) {
                            break;
                        }
                    }
                }
                if (trk == NumTrks) {
                    throw new DiskFullException("Disk full");
                }
                Debug.Assert(trkBits != 0);

                // Find the first free sector.  Following DOS behavior, start with the highest.
                sct = (byte)(NumScts - 1);
                while (true) {
                    if ((trkBits & 0x80000000) != 0) {
                        break;
                    }
                    trkBits <<= 1;
                    sct--;
                }
            }
            Debug.Assert(sct < NumScts);

            // Update the VTOC allocation entry.
            mData[0x30] = trk;
            if (trk < VTOCTrk) {
                mData[0x31] = 0xff;     // descending
            } else {
                mData[0x31] = 0x01;     // ascending
            }
            MarkSectorUsed(trk, sct, entry);
            allocTrk = trk;
            allocSct = sct;
        }

        /// <summary>
        /// Marks all sectors on a track as free or in use.  This is used by the disk formatter,
        /// before a DOS_VTOC object has been created.
        /// </summary>
        /// <param name="vtocBuf">Buffer with prototype VTOC.</param>
        /// <param name="trk">Track to edit.</param>
        /// <param name="isInUse">If true, mark as in use; if false, mark as free.</param>
        /// <param name="numTrks">Total number of tracks on this disk.</param>
        /// <param name="numScts">Number of sectors per track.</param>
        internal static void MarkTrack(byte[] vtocBuf, byte trk, bool isInUse,
                uint numTrks, uint numScts) {
            if (numTrks > MAX_TRACK) {
                ushort val = 0;
                if (!isInUse) {
                    val = (ushort)(0xffff << (16 - (int)numScts));
                }
                RawData.SetU16LE(vtocBuf, 0x38 + trk * 2, val);
            } else {
                uint val = 0;
                if (!isInUse) {
                    val = 0xffffffff << (32 - (int)numScts);
                }
                RawData.SetU32BE(vtocBuf, 0x38 + trk * 4, val);
            }
        }
    }
}
