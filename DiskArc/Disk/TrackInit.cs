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

namespace DiskArc.Disk {
    /// <summary>
    /// Generates an initialized track of nibble data for a 5.25" or 3.5" disk.
    /// </summary>
    /// <remarks>
    /// <para>On physical hardware, the length of the track is determined by the rotational
    /// speed, and the initialization code reduces the inter-sector gap sizes until the
    /// desired size is reached.  Here we just generate reasonably-sized tracks.</para>
    /// <para>We could change this so that the gap2/gap3 sizes are specified by the codec,
    /// and have the caller pass the desired track length in.</para>
    /// </remarks>
    public static class TrackInit {
        // Buffer of zeroes to use when generating empty sector data.
        private static readonly byte[] ZEROES = new byte[524];

        /// <summary>
        /// Default 5.25" track length, in bytes.
        /// </summary>
        private const int DEFAULT_LENGTH_525 = 6336;    // $18c0

        /// <summary>
        /// Generates a newly-initialized GCR track for a 5.25" floppy disk.  The returned buffer
        /// will be DEFAULT_LENGTH_525 bytes long.
        /// </summary>
        /// <remarks>
        /// <para>The length required to hold the sector data is returned in "bitCount".  The
        /// remainder of the buffer is filled with self-sync bytes (gap 1).</para>
        /// </remarks>
        /// <param name="codec">Sector data codec.</param>
        /// <param name="isByteAligned">True if the underlying storage is byte-aligned.</param>
        /// <param name="volume">Low-level volume number to encode in the address field.</param>
        /// <param name="track">Track number to encode in the address field.</param>
        /// <param name="bitCount">Result: length, in bits, of formatted data.</param>
        /// <returns>Initialized track data array.</returns>
        public static byte[] GenerateTrack525(SectorCodec codec, bool isByteAligned,
                byte volume, byte track, out int bitCount) {
            // Not handling these configurations yet.
            if (codec.AddressProlog.Length != 3 ||
                    codec.AddressEpilog.Length != 3 ||
                    codec.DataProlog.Length != 3 ||
                    codec.DataEpilog.Length != 3) {
                // YAGNI? May need to adjust track length.
                throw new DAException("Invalid prolog/epilog length");
            }
            if (track >= SectorCodec.MAX_TRACK_525) {
                throw new ArgumentOutOfRangeException("Invalid track: " + track);
            }
            if (codec.DataEncoding == SectorCodec.NibbleEncoding.GCR53) {
                return GenerateTrack525_13(codec, isByteAligned, volume, track, out bitCount);
            } else {
                return GenerateTrack525_16(codec, isByteAligned, volume, track, out bitCount);
            }
        }

        internal const int GAP2_525_13_LEN = 10;
        private static byte[] GenerateTrack525_13(SectorCodec codec, bool isByteAligned,
                byte volume, byte track, out int bitCount) {
            // Each sector has the following format, assuming 3-byte prolog/epilog:
            // - address field (14 bytes: prolog*3, volume*2, track*2, sector*2, checksum*2,
            //   epilog*3)
            // - gap 2 (10 self-sync $ff)
            // - data field (prolog*3, 410 data bytes, checksum, epilog*3)
            // - inter-sector gap 3 (35 self-sync $ff)
            //
            // Ignoring gap 1, this yields:
            //   (14 + 10 + (3 + 410 + 1 + 3) + 35) * 13 = 6188 ($182c)
            // If long bytes are represented, add ((10 + 35) * 13 / 8) ~= 73 bytes ($1875)

            const int GAP2_LEN = GAP2_525_13_LEN;
            const int DATA_LEN = 3 + 410 + 1 + 3;
            const int GAP3_LEN = 35;

            byte[] buffer = new byte[DEFAULT_LENGTH_525];
            CircularBitBuffer circ = new CircularBitBuffer(buffer, 0, 0, buffer.Length * 8);
            int selfSyncWidth = isByteAligned ? 8 : 9;
            // Fill track; anything we don't subsequently overwrite will be gap 1.
            circ.Fill(0xff, selfSyncWidth);
            for (byte sector = 0; sector < 13; sector++) {
                for (int i = 0; i < GAP3_LEN; i++) {
                    circ.WriteByte(0xff, selfSyncWidth);
                }
                codec.WriteAddressField_525(circ, volume, track, sector);
                for (int i = 0; i < GAP2_LEN; i++) {
                    circ.WriteByte(0xff, selfSyncWidth);
                }
                // DOS 3.2 doesn't write the data field when initializing the track, so we
                // mimic the behavior here.  Leave enough space for the data area.
                for (int i = 0; i < DATA_LEN; i++) {
                    circ.WriteOctet(0xff);
                }
            }
            bitCount = circ.BitPosition;
            return buffer;
        }

        private static byte[] GenerateTrack525_16(SectorCodec codec, bool isByteAligned,
                byte volume, byte track, out int bitCount) {
            // Each sector has the following format, assuming 3-byte prolog/epilog:
            // - address field (14 bytes: prolog*3, volume*2, track*2, sector*2, checksum*2,
            //   epilog*3)
            // - gap 2 (5 self-sync $ff)
            // - data field (prolog*3, 342 bytes, checksum, epilog*3)
            // - inter-sector gap 3 (20 self-sync $ff)
            //
            // Ignoring gap 1, this yields:
            //   (14 + 5 + (3 + 342 + 1 + 3) + 20) * 16 = 6208 ($1840)
            // If long bytes are represented, add ((5 + 20) * 16 * 2 / 8) = 100 bytes ($18a4)

            const int GAP2_LEN = 5;
            const int GAP3_LEN = 20;

            byte[] buffer = new byte[DEFAULT_LENGTH_525];
            CircularBitBuffer circ = new CircularBitBuffer(buffer, 0, 0, buffer.Length * 8);
            int selfSyncWidth = isByteAligned ? 8 : 10;
            // Fill track; anything we don't subsequently overwrite will be gap 1.
            circ.Fill(0xff, selfSyncWidth);

            // Write sectors.  Start with gap 3, so that the start of the track is self-sync.  This
            // might help if the wrap point isn't a whole nibble.
            for (byte sector = 0; sector < 16; sector++) {
                for (int i = 0; i < GAP3_LEN; i++) {
                    circ.WriteByte(0xff, selfSyncWidth);
                }
                codec.WriteAddressField_525(circ, volume, track, sector);
                for (int i = 0; i < GAP2_LEN; i++) {
                    circ.WriteByte(0xff, selfSyncWidth);
                }
                circ.WriteOctets(codec.DataProlog);
                codec.EncodeSector62_256(circ, circ.BitPosition, ZEROES, 0);
                circ.WriteOctets(codec.DataEpilog);
            }
            bitCount = circ.BitPosition;
            return buffer;
        }

        /// <summary>
        /// Generates a newly-initialized GCR track for a 3.5" disk.  The length of the track will
        /// vary by speed zone.
        /// </summary>
        /// <remarks>
        /// <para>The length required to hold the sector data is returned in "bitCount".  The
        /// remainder of the buffer is filled with self-sync bytes (gap 1).</para>
        /// </remarks>
        /// <param name="codec">Sector data codec.</param>
        /// <param name="isByteAligned">True if the underlying storage is byte-aligned.</param>
        /// <param name="track">Track number to generate.</param>
        /// <param name="side">Disk side (1 or 2).</param>
        /// <param name="format">Format byte; low nibble indicates interleave (2:1 or 4:1).</param>
        /// <param name="bitCount">Result: length, in bits, of formatted data.</param>
        /// <returns>Initialized track data array.</returns>
        public static byte[] GenerateTrack35(SectorCodec codec, bool isByteAligned,
                byte track, int side, byte format, out int bitCount) {
            // Each sector has the following format, assuming 3-byte prolog/epilog:
            // - address field (10 bytes: prolog*3, track, sector, side, format, checksum,
            //   epilog*2)
            // - pad byte
            // - gap 2 (5 self-sync $ff)
            // - data field (prolog*3, sector, 699 bytes, checksum*4, epilog*2)
            // - pad byte
            // - inter-sector gap 3 (36 self-sync $ff)
            //
            // Ignoring gap 1, this yields:
            //   (10 + 1 + 5 + (3 + 1 + 699 + 4 + 2) + 1 + 36) = 762 per sector
            // If long bytes are represented, add ((5 + 36) * 2 / 8) = 10.25 bytes
            //
            // Tracks have 8 to 12 sectors, requiring 6178-9267 ($1822-$2433) octets.

            if (codec.AddressProlog.Length != 3 ||
                    codec.AddressEpilog.Length != 2 ||
                    codec.DataProlog.Length != 3 ||
                    codec.DataEpilog.Length != 2) {
                // YAGNI?
                throw new DAException("Invalid prolog/epilog length");
            }

            const int GAP1_LEN = 64;                    // not sure if this ought to be larger
            const int GAP2_LEN = 5;
            const int GAP3_LEN = 36;
            const int OCTETS_PER_SECTOR = 762 + 11;     // size buffer assuming "wide" bytes

            int interleave = format & 0x0f;
            if (interleave != 2 && interleave != 4) {
                throw new ArgumentException("Unexpected interleave value in format: " + interleave);
            }
            if (side != 0 && side != 1) {
                throw new ArgumentException("Unexpected side: " + side);
            }
            if (track >= SectorCodec.MAX_TRACK_35) {
                throw new ArgumentOutOfRangeException("Invalid track number: " + track);
            }

            int speedZone = track / 16;                 // 0-4
            int numSectors = 8 + (4 - speedZone);
            byte[] buffer = new byte[GAP1_LEN + numSectors * OCTETS_PER_SECTOR];
            CircularBitBuffer circ = new CircularBitBuffer(buffer, 0, 0, buffer.Length * 8);

            int selfSyncWidth = isByteAligned ? 8 : 10;
            // Fill track; anything we don't subsequently overwrite will be gap 1.
            circ.Fill(0xff, selfSyncWidth);

            // Write sectors.  Start with gap 3, so that the start of the track is self-sync.  This
            // might help if the wrap point isn't a whole nibble.
            byte[] itable = GetInterleaveTable(numSectors, interleave);

            for (int sctIdx = 0; sctIdx < numSectors; sctIdx++) {
                byte sector = itable[sctIdx];

                for (int i = 0; i < GAP3_LEN; i++) {
                    circ.WriteByte(0xff, selfSyncWidth);
                }
                codec.WriteAddressField_35(circ, track, sector, side, format);
                for (int i = 0; i < GAP2_LEN; i++) {
                    circ.WriteByte(0xff, selfSyncWidth);
                }
                circ.WriteOctets(codec.DataProlog);
                circ.WriteOctet(SectorCodec.To62(sector));
                codec.EncodeSector62_524(circ, circ.BitPosition, ZEROES, 0);
                circ.WriteOctets(codec.DataEpilog);
                circ.WriteOctet(0xff);
            }
            bitCount = circ.BitPosition;
            return buffer;
        }


        /// <summary>
        /// Generates a sector interleave table.
        /// </summary>
        /// <param name="numSectors">Number of sectors per track.</param>
        /// <param name="interleave">Interleave.</param>
        /// <returns>Interleave table.</returns>
        private static byte[] GetInterleaveTable(int numSectors, int interleave) {
            Debug.Assert(interleave > 0);
            byte[] table = new byte[numSectors];
            for (int i = 0; i < numSectors; i++) {
                table[i] = 0xff;
            }
            int offset = 0;
            for (uint i = 0; i < numSectors; i++) {
                if (table[offset] != 0xff) {
                    // Collided, move one forward.
                    offset = (offset + 1) % numSectors;
                    Debug.Assert(table[offset] == 0xff);
                }
                table[offset] = (byte)i;

                offset = (offset + interleave) % numSectors;
            }
            return table;
        }

        public static bool DebugCheckInterleave() {
            // Not checking anything explicitly.  Just running the algorithm to see if the
            // assertions trip, and providing an opportunity to debugger-break here for a peek.
            for (int i = 8; i <= 12; i++) {
                byte[] table2 = GetInterleaveTable(i, 2);   // 3.5" 2:1
                byte[] table4 = GetInterleaveTable(i, 4);   // 3.5" 4:1
            }
            byte[] proTab = GetInterleaveTable(16, 2);      // "2 ascending", e.g. ProDOS
            return true;
        }
    }
}
