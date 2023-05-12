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
using DiskArc.Disk;

namespace DiskArc {
    /// <summary>
    /// Disk sector encoder and decoder, for nibble images.
    /// </summary>
    /// <remarks>
    /// <para>This is an abstract class.  Sub-classes should configure the various properties,
    /// and can override methods for completely non-standard formats.</para>
    /// </remarks>
    public abstract class SectorCodec {
        // Max number of tracks on a 5.25" floppy disk.
        public const int MAX_TRACK_525 = 40;

        // Number of tracks on a 400KB/800KB 3.5" floppy disk.
        public const int MAX_TRACK_35 = 80;
        //public const int MAX_SECTOR_35 = 12;


        // Max bytes between addr epilog / data prolog.  Gap is larger if sync bytes are 9 bits.
        private const int MAX_ADDR_DATA_GAP = 24;

        // 5&3 encoding byte values.  $d5/$aa are bitwise legal but not included.
        protected static readonly byte[] sDiskBytes53 = new byte[32] {
            0xab, 0xad, 0xae, 0xaf, 0xb5, 0xb6, 0xb7, 0xba,
            0xbb, 0xbd, 0xbe, 0xbf, 0xd6, 0xd7, 0xda, 0xdb,
            0xdd, 0xde, 0xdf, 0xea, 0xeb, 0xed, 0xee, 0xef,
            0xf5, 0xf6, 0xf7, 0xfa, 0xfb, 0xfd, 0xfe, 0xff
        };
        // 6&2 encoding byte values.  $d5/$aa are bitwise legal but not included.
        protected static readonly byte[] sDiskBytes62 = new byte[64] {
            0x96, 0x97, 0x9a, 0x9b, 0x9d, 0x9e, 0x9f, 0xa6,
            0xa7, 0xab, 0xac, 0xad, 0xae, 0xaf, 0xb2, 0xb3,
            0xb4, 0xb5, 0xb6, 0xb7, 0xb9, 0xba, 0xbb, 0xbc,
            0xbd, 0xbe, 0xbf, 0xcb, 0xcd, 0xce, 0xcf, 0xd3,
            0xd6, 0xd7, 0xd9, 0xda, 0xdb, 0xdc, 0xdd, 0xde,
            0xdf, 0xe5, 0xe6, 0xe7, 0xe9, 0xea, 0xeb, 0xec,
            0xed, 0xee, 0xef, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6,
            0xf7, 0xf9, 0xfa, 0xfb, 0xfc, 0xfd, 0xfe, 0xff
        };
        // Inverse 5&3 table; all values are [0,31].
        protected static readonly byte[] sInvDiskBytes53 = GenerateInv(sDiskBytes53);
        // Inverse 6&2 table; all values are [0,63].
        protected static readonly byte[] sInvDiskBytes62 = GenerateInv(sDiskBytes62);
        protected const byte INVALID_INV = 0xff;

        // Popular sequences.
        protected static readonly byte[] sD5AA96 = new byte[] { 0xd5, 0xaa, 0x96 };
        protected static readonly byte[] sD5AAAD = new byte[] { 0xd5, 0xaa, 0xad };
        protected static readonly byte[] sD5AAB5 = new byte[] { 0xd5, 0xaa, 0xb5 };
        protected static readonly byte[] sDEAAEB = new byte[] { 0xde, 0xaa, 0xeb };
        protected static readonly byte[] sDEAA   = new byte[] { 0xde, 0xaa };

        /// <summary>
        /// Sector data encoding form.
        /// </summary>
        public enum NibbleEncoding {
            Unknown = 0,
            GCR53,                      // 5&3 encoding (13-sector 5.25" floppy disks)
            GCR62,                      // 6&2 encoding
        }


        /// <summary>
        /// Instance name, mostly for debugging purposes.
        /// </summary>
        public string Name { get; protected set; } = string.Empty;

        /// <summary>
        /// Can we write with this codec?
        /// </summary>
        /// <remarks>
        /// <para>Should be true for we're ignoring checksums, since that means we don't know
        /// how to write them.</para>
        /// </remarks>
        public bool ReadOnly { get; protected set; }

        /// <summary>
        /// Size of the data in a sector.  Usually 256, 512, or 524.
        /// </summary>
        public int DecodedSectorSize { get; protected set; }

        /// <summary>
        /// Size of the data after it's encoded.  Includes the checksum byte(s).
        /// </summary>
        public int EncodedSectorSize { get; protected set; }

        /// <summary>
        /// Method used to encode the sector data.
        /// </summary>
        public NibbleEncoding DataEncoding { get; protected set; }

        protected byte[] mAddressProlog = RawData.EMPTY_BYTE_ARRAY;
        protected byte[] mAddressEpilog = RawData.EMPTY_BYTE_ARRAY;
        protected byte[] mDataProlog = RawData.EMPTY_BYTE_ARRAY;
        protected byte[] mDataEpilog = RawData.EMPTY_BYTE_ARRAY;

        public byte[] AddressProlog {
            get {
                // Make a copy so caller can't modify our internal state.
                byte[] retVal = new byte[mAddressProlog.Length];
                Array.Copy(mAddressProlog, retVal, retVal.Length);
                return retVal;
            }
        }
        public byte[] AddressEpilog {
            get {
                byte[] retVal = new byte[mAddressEpilog.Length];
                Array.Copy(mAddressEpilog, retVal, retVal.Length);
                return retVal;
            }
        }
        public byte[] DataProlog {
            get {
                byte[] retVal = new byte[mDataProlog.Length];
                Array.Copy(mDataProlog, retVal, retVal.Length);
                return retVal;
            }
        }
        public byte[] DataEpilog {
            get {
                byte[] retVal = new byte[mDataEpilog.Length];
                Array.Copy(mDataEpilog, retVal, retVal.Length);
                return retVal;
            }
        }

        /// <summary>
        /// Number of bytes of the address epilog to read.  Must be less than or equal to the
        /// length of the address epilog.
        /// </summary>
        public int AddrEpilogReadCount { get; protected set; }

        /// <summary>
        /// Address field checksum seed.  Value is limited by the way the address checksum is
        /// encoded, e.g. 3.5" disks must use a value in the range [0x00,0x3f].
        /// </summary>
        public byte AddrChecksumSeed { get; protected set; }

        /// <summary>
        /// If true, verify the track number.
        /// </summary>
        public bool DoTestAddrTrack { get; protected set; }

        /// <summary>
        /// If true, verify the address field checksum.
        /// </summary>
        public bool DoTestAddrChecksum { get; protected set; }

        /// <summary>
        /// Number of bytes of the data epilog to read.  Must be less than or equal to the
        /// length of the data epilog.
        /// </summary>
        public int DataEpilogReadCount { get; protected set; }

        /// <summary>
        /// Data field checksum seed.  Value is limited by the way the data checksum is encoded,
        /// e.g. 16-sector 5.25" disks must use a value in the range [0x00,0x3f].
        /// </summary>
        public byte DataChecksumSeed { get; protected set; }

        /// <summary>
        /// If true, verify the data checksum.
        /// </summary>
        public bool DoTestDataChecksum { get; protected set; }

        // Temporary buffers for encoding and decoding.
        private byte[] mTmpBuf1 = new byte[512];
        private byte[] mTmpBuf2 = new byte[512];
        private byte[] mTmpBuf3 = new byte[512];


        /// <summary>
        /// Finds all of the sectors in the track.
        /// </summary>
        /// <param name="trackNum">Track number.</param>
        /// <param name="trackFraction">Track fraction (partial track or disk side).</param>
        /// <param name="trackData">Nibble data buffer.</param>
        /// <returns>List of sectors found.</returns>
        public virtual List<SectorPtr> FindSectors(uint trackNum, uint trackFraction,
                CircularBitBuffer trackData) {
            Debug.Assert(mAddressProlog.Length > 0);
            Debug.Assert(mDataProlog.Length > 0);
            Debug.Assert(EncodedSectorSize > 0);

            List<SectorPtr> sectors = new List<SectorPtr>();

            while (true) {
                // Find the next matching sector.
                SectorPtr? nextSector;
                if (DecodedSectorSize == 256) {
                    nextSector = FindNextSector525(trackNum, trackFraction, trackData);
                } else {
                    nextSector = FindNextSector35(trackNum, trackFraction, trackData);
                }
                if (nextSector == null) {
                    // We scanned the entire track and nothing matched.  This usually means
                    // that there's nothing to find, but rarely we find a (bad) sector header
                    // when out of sync, and then lose it after finding some self-sync bytes.
                    // Should only be possible on bit-stream formats like WOZ.
                    if (sectors.Count != 0) {
                        Debug.WriteLine("Found a vanishing sector");
                        sectors.Clear();
                    }
                    return sectors;
                }

                // Test for wrap-around sector.
                // (This turned out to be a bad idea -- sometimes a valid sector actually does
                // wrap around the end of the fixed-length buffer.)
                //if (isFixedLength && nextSector.DataEndBitOffset != -1 &&
                //        nextSector.DataEndBitOffset < nextSector.AddrPrologBitOffset) {
                //    Debug.WriteLine("Wrap-around detected, stopping after " + sectors.Count +
                //        " sectors");
                //    return sectors;
                //}

                AddrPostAdjust(nextSector);

                // See if we've already found this one.  It's a short list, O(n^2) is fine.
                foreach (SectorPtr oldPtr in sectors) {
                    if (nextSector.Sector == oldPtr.Sector) {
                        if (nextSector.AddrPrologBitOffset == oldPtr.AddrPrologBitOffset) {
                            // We looped back to the start.  Done with track.
                            //Debug.WriteLine("Found " + sectors.Count + " sectors on track " +
                            //    trackNum + "/" + trackFraction);
                            return sectors;
                        } else {
                            // Found duplicate.  Maybe we don't understand the format, maybe this
                            // is vestigial stuff at the end of a fixed-length capture.  Maybe
                            // the sector just happens to be there twice?  (This has been seen
                            // on .NIB images, e.g. RDOS 3.3 "Phantasie III side B"; both sectors
                            // appear valid.)
                            //
                            // Options: (1) ignore new; (2) replace old; (3) keep both.  If
                            // they're both valid then we want them to be visible to a nibble
                            // editor.
                            //
                            // This is expected when probing the Muse 13-sector codec.

                            //Debug.WriteLine("Found duplicate of T" + trackNum + " S" +
                            //    nextSector.Sector + " at " + nextSector.AddrPrologBitOffset +
                            //    ", new dmg=" + nextSector.IsDamaged +
                            //    ", old dmg=" + oldPtr.IsDamaged);
                            if (nextSector.DataEndBitOffset != -1 &&
                                    nextSector.DataEndBitOffset < nextSector.AddrPrologBitOffset) {
                                // It's a duplicate whose body wraps around the end.  If this is
                                // a fixed-length capture (e.g. .NIB) then this is probably not
                                // valid... but it could be.
                                //Debug.WriteLine("Duplicate wraps around");
                                //nextSector = null;
                            } else if (nextSector.IsAddrDamaged) {
                                nextSector = null;      // discard new
                            } else if (oldPtr.IsAddrDamaged) {
                                // TODO? remove old?
                            }
                            break;
                        }
                    }
                }

                // New entry, add to list.
                if (nextSector != null) {
                    sectors.Add(nextSector);
                }
            }
        }

        /// <summary>
        /// Finds the next sector on a 5.25" disk track.
        /// </summary>
        /// <param name="trackNum">Track number.</param>
        /// <param name="trackFraction">Fractional track number or disk side.</param>
        /// <param name="trkData">Track data buffer.</param>
        /// <returns>Sector pointer or null if no sector found.</returns>
        protected virtual SectorPtr? FindNextSector525(uint trackNum, uint trackFraction,
                CircularBitBuffer trkData) {
            PreAdjust(trackNum, trackFraction);

            int addrStartPosn =
                trkData.FindNextLatchSequence(mAddressProlog, mAddressProlog.Length, -1);
            if (addrStartPosn == -1) {
                // No matching address field found.
                return null;
            }

            SectorPtr newPtr = new SectorPtr();
            newPtr.AddrPrologBitOffset = addrStartPosn;
            newPtr.Volume = GetNext44Value(trkData);
            newPtr.Track = GetNext44Value(trkData);
            newPtr.Sector = GetNext44Value(trkData);
            byte addrChecksum = GetNext44Value(trkData);

            // Check the track number.
            if (DoTestAddrTrack && newPtr.Track != trackNum) {
                Debug.WriteLine("Address field track number mismatch in T" + trackNum +
                    " found=" + newPtr.Track);
                newPtr.IsAddrDamaged = true;
            }

            // Test the address checksum.
            byte calcChecksum = (byte)(AddrChecksumSeed ^ newPtr.Volume ^ newPtr.Track ^
                newPtr.Sector);
            if (DoTestAddrChecksum && calcChecksum != addrChecksum) {
                Debug.WriteLine("Address field checksum mismatch in T" + newPtr.Track +
                    " S" + newPtr.Sector);
                newPtr.IsAddrDamaged = true;
            }
            newPtr.AddrChecksumResult = (byte)(addrChecksum ^ calcChecksum);

            // Verify the epilog.
            if (!trkData.ExpectLatchSequence(mAddressEpilog, AddrEpilogReadCount)) {
                Debug.WriteLine("Address field epilog not found");
                newPtr.IsAddrDamaged = true;
            }

            // We only want to look a short distance forward.  Otherwise we risk finding the
            // next sector's data field.
            newPtr.DataPrologBitOffset = trkData.FindNextLatchSequence(mDataProlog,
                mDataProlog.Length, (MAX_ADDR_DATA_GAP + mDataProlog.Length) * 8);
            if (newPtr.DataPrologBitOffset != -1) {
                // We have what appears to be a valid data field.  It's possible this is just
                // a stub, and there's an address field inside the data area.  We'll figure this
                // out if we encounter an illegal value ($d5/$aa) while skimming the data.
                newPtr.DataFieldBitOffset = trkData.BitPosition;

                byte[] invTable =
                    (DataEncoding == NibbleEncoding.GCR53) ? sInvDiskBytes53 : sInvDiskBytes62;
                for (int i = 0; i < EncodedSectorSize; i++) {
                    byte val = trkData.LatchNextByte();
                    byte decodedVal = invTable[val];
                    if (decodedVal == INVALID_INV) {
                        Debug.WriteLine("Found invalid disk byte in data area; rewinding");
                        // Back up to the start of the data field.  The search for the next
                        // address field should start there.  This can happen with custom
                        // formats that use a nonstandard encoding but standard address/data
                        // prologs (e.g. "Germany 1985").
                        trkData.BitPosition = newPtr.DataFieldBitOffset;
                        // Return the sector, but flagged as damaged and dataless.
                        newPtr.DataFieldBitOffset = -1;
                        newPtr.IsDataDamaged = true;
                        return newPtr;
                    }
                }
                newPtr.DataEndBitOffset = trkData.BitPosition;

                // Verify the epilog, if configured.
                if (!trkData.ExpectLatchSequence(mDataEpilog, DataEpilogReadCount)) {
                    Debug.WriteLine("Data field epilog not found");
                    newPtr.IsDataDamaged = true;
                }
            } else {
                // No data field.  For a newly-formatted DOS 3.2 disk, this is normal.
                if (DataEncoding != NibbleEncoding.GCR53) {
                    newPtr.IsDataDamaged = true;
                }
            }

            return newPtr;
        }

        // Tweakers for special situations.
        protected virtual void PreAdjust(uint trackNum, uint trackFraction) { }
        protected virtual void AddrPostAdjust(SectorPtr ptr) { }

        /// <summary>
        /// Finds the next sector on a 3.5" disk track.
        /// </summary>
        /// <param name="trackNum">Track number.</param>
        /// <param name="trackFraction">Fractional track number or disk side.</param>
        /// <param name="trkData">Track data buffer.</param>
        /// <returns>Sector pointer or null if no sector found.</returns>
        protected virtual SectorPtr? FindNextSector35(uint trackNum, uint trackFraction,
                CircularBitBuffer trkData) {
            int addrStartPosn =
                trkData.FindNextLatchSequence(mAddressProlog, mAddressProlog.Length, -1);
            if (addrStartPosn == -1) {
                // No matching address field found.
                return null;
            }

            SectorPtr newPtr = new SectorPtr();
            newPtr.AddrPrologBitOffset = addrStartPosn;
            newPtr.Track = GetNext62Value(trkData);
            newPtr.Sector = GetNext62Value(trkData);
            newPtr.Side = GetNext62Value(trkData);
            newPtr.Format = GetNext62Value(trkData);
            byte addrChecksum = GetNext62Value(trkData);

            // Check the track/side number.
            if (DoTestAddrTrack) {
                int addrTrack = ((newPtr.Side & 0x01f) << 6) | newPtr.Track;
                int addrSide = (newPtr.Side & 0x20) >> 5;
                if (addrTrack != trackNum  || addrSide != trackFraction) {
                    Debug.WriteLine("Address field track number mismatch: wanted T" + trackNum +
                        "/" + trackFraction + ", found T" + addrTrack + "/" + addrSide);
                    newPtr.IsAddrDamaged = true;
                }
            }

            // Test the address checksum.
            byte calcChecksum = (byte)((AddrChecksumSeed ^ newPtr.Track ^ newPtr.Sector ^
                newPtr.Side ^ newPtr.Format) & 0x3f);
            if (DoTestAddrChecksum && calcChecksum != addrChecksum) {
                Debug.WriteLine("Address field checksum mismatch in T" + newPtr.Track +
                    " S" + newPtr.Sector);
                newPtr.IsAddrDamaged = true;
            }
            newPtr.AddrChecksumResult = (byte)(addrChecksum ^ calcChecksum);

            // Verify the epilog.
            if (!trkData.ExpectLatchSequence(mAddressEpilog, AddrEpilogReadCount)) {
                Debug.WriteLine("Address field epilog not found");
                newPtr.IsAddrDamaged = true;
            }

            // We only want to look a short distance forward.  Otherwise we risk finding the
            // next sector's data field.
            newPtr.DataPrologBitOffset = trkData.FindNextLatchSequence(mDataProlog,
                mDataProlog.Length, (MAX_ADDR_DATA_GAP + mDataProlog.Length) * 8);
            if (newPtr.DataPrologBitOffset != -1) {
                byte sector = trkData.LatchNextByte();       // consume the duplicate sector num
                newPtr.DataFieldBitOffset = trkData.BitPosition;

                // We're not attempting to verify data fields now, so just skip past it.  We
                // would like to verify the end point to be sure that this isn't a partial
                // sector sitting at the end of a .nib fixed-length capture.
                for (int i = 0; i < EncodedSectorSize; i++) {
                    trkData.LatchNextByte();
                }
                newPtr.DataEndBitOffset = trkData.BitPosition;

                // Verify the epilog, if configured.
                if (!trkData.ExpectLatchSequence(mDataEpilog, DataEpilogReadCount)) {
                    Debug.WriteLine("Data field epilog not found");
                    newPtr.IsDataDamaged = true;
                }
            } else {
                newPtr.IsDataDamaged = true;
            }

            return newPtr;
        }

        /// <summary>
        /// Reads a sector from nibble track data.  The decoded data is copied into the supplied
        /// buffer.  If an error is encountered, such as a bad checksum, this returns false (as
        /// opposed to throwing an exception).
        /// </summary>
        /// <remarks>
        /// <para>The buffer must be able to hold DecodedSectorSize bytes.</para>
        /// </remarks>
        /// <param name="trkData">Nibble track data.</param>
        /// <param name="sctPtr">Sector data pointer.</param>
        /// <param name="buffer">Buffer that will receive data.</param>
        /// <param name="offset">Offset within buffer to place data.</param>
        /// <returns>True on success.</returns>
        public virtual bool ReadSector(CircularBitBuffer trkData, SectorPtr sctPtr,
                byte[] buffer, int offset) {
            if (buffer == null) {
                throw new ArgumentNullException("buffer is null");
            }
            if (offset < 0 || offset >= buffer.Length) {
                throw new ArgumentOutOfRangeException("Invalid offset");
            }
            if (offset + DecodedSectorSize > buffer.Length) {
                throw new ArgumentOutOfRangeException("Not enough room in buffer");
            }

            bool decOk;
            if (DataEncoding == NibbleEncoding.GCR53 && DecodedSectorSize == 256) {
                decOk = DecodeSector53_256(trkData, sctPtr.DataFieldBitOffset, buffer, offset);
            } else if (DataEncoding == NibbleEncoding.GCR62 && DecodedSectorSize == 256) {
                decOk = DecodeSector62_256(trkData, sctPtr.DataFieldBitOffset, buffer, offset);
            } else if (DataEncoding == NibbleEncoding.GCR62 && DecodedSectorSize == 524) {
                decOk = DecodeSector62_524(trkData, sctPtr.DataFieldBitOffset, buffer, offset);
            } else {
                throw new InvalidDataException("Don't know how to read sector with enc=" +
                    DataEncoding + " size=" + DecodedSectorSize);
            }

            // Double-check our position.
            if (decOk && trkData.BitPosition != sctPtr.DataEndBitOffset) {
                Debug.WriteLine("Didn't end up at start of data epilog");
                Debug.Assert(false);
            }
            return decOk;
        }

        /// <summary>
        /// Writes a sector to nibble track data.  The data in the buffer is encoded and
        /// written into the track stream.
        /// </summary>
        /// <remarks>
        /// <para>The buffer must hold at least DecodedSectorSize bytes.</para>
        /// </remarks>
        /// <param name="trkData">Nibble track data.</param>
        /// <param name="sctPtr">Sector data pointer.</param>
        /// <param name="buffer">Buffer of data to write.</param>
        /// <param name="offset">Offset of start of data in buffer.</param>
        /// <returns>True if the write succeeded.</returns>
        public virtual bool WriteSector(CircularBitBuffer trkData, SectorPtr sctPtr,
                byte[] buffer, int offset) {
            if (buffer == null) {
                throw new ArgumentNullException("buffer is null");
            }
            if (offset < 0 || offset >= buffer.Length) {
                throw new ArgumentOutOfRangeException("Invalid offset");
            }
            if (offset + DecodedSectorSize > buffer.Length) {
                throw new ArgumentOutOfRangeException("Not enough data in buffer");
            }

            bool encOk;
            if (DataEncoding == NibbleEncoding.GCR53 && DecodedSectorSize == 256) {
                if (sctPtr.DataFieldBitOffset == -1) {
                    WriteDataField53_256(trkData, sctPtr);
                }
                Debug.Assert(sctPtr.DataFieldBitOffset != -1);
                encOk = EncodeSector53_256(trkData, sctPtr.DataFieldBitOffset, buffer, offset);
            } else if (DataEncoding == NibbleEncoding.GCR62 && DecodedSectorSize == 256) {
                encOk = EncodeSector62_256(trkData, sctPtr.DataFieldBitOffset, buffer, offset);
            } else if (DataEncoding == NibbleEncoding.GCR62 && DecodedSectorSize == 524) {
                encOk = EncodeSector62_524(trkData, sctPtr.DataFieldBitOffset, buffer, offset);
            } else {
                throw new InvalidDataException("Don't know how to write sector with enc=" +
                    DataEncoding + " size=" + DecodedSectorSize);
            }
            if (encOk) {
                // Write the data field epilog bytes if we've slipped.  This should usually be
                // unnecessary, as we are just overwriting the epilog with itself, but if there
                // was a long byte in the data field then we could be off by a bit or two.
                if (trkData.BitPosition != sctPtr.DataEndBitOffset) {
                    Debug.WriteLine("Data epilog slipped: " +
                        (trkData.BitPosition - sctPtr.DataEndBitOffset));
                    Debug.Assert(false);    // remove me
                    trkData.WriteOctets(mDataEpilog);
                }
            }
            return encOk;
        }

        /// <summary>
        /// Writes the sector address header on a 5.25" disk track.
        /// </summary>
        /// <param name="trkData">Nibble data.  Must be positioned at the place where the address
        ///   prolog will begin.</param>
        /// <param name="volume">Volume number to encode.</param>
        /// <param name="track">Track number to encode.</param>
        /// <param name="sector">Sector number to encode.</param>
        public void WriteAddressField_525(CircularBitBuffer trkData, byte volume, byte track,
                byte sector) {
            if (ReadOnly) {
                throw new NotSupportedException("Can't write with this codec");
            }
            // Don't need to range-check args because all 256 possible values are allowed.
            trkData.WriteOctets(mAddressProlog);
            trkData.WriteOctet(To44Lo(volume));
            trkData.WriteOctet(To44Hi(volume));
            trkData.WriteOctet(To44Lo(track));
            trkData.WriteOctet(To44Hi(track));
            trkData.WriteOctet(To44Lo(sector));
            trkData.WriteOctet(To44Hi(sector));
            byte checksum = (byte)(AddrChecksumSeed ^ volume ^ track ^ sector);
            trkData.WriteOctet(To44Lo(checksum));
            trkData.WriteOctet(To44Hi(checksum));
            trkData.WriteOctets(mAddressEpilog);
        }

        /// <summary>
        /// Writes the sector data framing for a 5&3 sector.  Updates the sector data pointer
        /// with the new locations.
        /// </summary>
        /// <param name="trkData">Nibble data.</param>
        /// <param name="sctPtr">Sector data pointer.</param>
        private void WriteDataField53_256(CircularBitBuffer trkData, SectorPtr sctPtr) {
            //Debug.WriteLine("WriteDataField53_256 T" + sctPtr.Track + " S" + sctPtr.Sector);
            // Start at the start of the address field.
            trkData.BitPosition = sctPtr.AddrPrologBitOffset;
            // Skip past the address field (prolog, contents, epilog; usually 14 bytes).
            int skipLen = mAddressProlog.Length + (4 * 2) + mAddressEpilog.Length;
            for (int i = 0; i < skipLen; i++) {
                trkData.LatchNextByte();
            }
            // There should be about ten 9-bit self-sync bytes in gap 2.  We don't know if the
            // nibble storage format is byte-aligned, so it's important to latch the bytes.
            for (int i = 0; i < TrackInit.GAP2_525_13_LEN; i++) {
                trkData.LatchNextByte();
            }
            // Record this position.
            sctPtr.DataPrologBitOffset = trkData.BitPosition;
            // Set the buffer position and write the prolog.
            trkData.WriteOctets(mDataProlog);
            // Record this position.
            sctPtr.DataFieldBitOffset = trkData.BitPosition;
            // Leave room for 411 bytes of data.
            trkData.AdjustBitPosition(411 * 8);
            // Record the position and write the epilog.
            sctPtr.DataEndBitOffset = trkData.BitPosition;
            trkData.WriteOctets(mDataEpilog);
        }

        /// <summary>
        /// Writes the data field on a 5.25" disk track.
        /// </summary>
        /// <param name="trkData">Nibble data.  Must be positioned at the place where the data
        ///   prolog will begin.</param>
        /// <param name="buffer">Data to write.</param>
        /// <param name="offset">Offset of start of data in buffer.</param>
        public void WriteDataField_525(CircularBitBuffer trkData, byte[] buffer, int offset) {
            if (ReadOnly) {
                throw new NotSupportedException("Can't write with this codec");
            }
            if (DecodedSectorSize != 256) {
                throw new DAException("Incorrect sector size for 525: " + DecodedSectorSize);
            }

            trkData.WriteOctets(mDataProlog);
            if (DataEncoding == NibbleEncoding.GCR53) {
                EncodeSector53_256(trkData, trkData.BitPosition, buffer, offset);
            } else if (DataEncoding == NibbleEncoding.GCR62) {
                EncodeSector62_256(trkData, trkData.BitPosition, buffer, offset);
            } else {
                throw new NotImplementedException("Unknown encoding: " + DataEncoding);
            }
            trkData.WriteOctets(mDataEpilog);
        }

        private const int CHUNK_SIZE_53 = 51;                       // 0x33
        private const int THREE_SIZE = (CHUNK_SIZE_53) * 3 + 1;     // same as 410 - 256

        /// <summary>
        /// Encodes 256 bytes as 411 bytes of 5&3-encoded data.
        /// </summary>
        /// <param name="trkData">Nibble track data.</param>
        /// <param name="dataFieldBitOffset">Offset of start of data.</param>
        /// <param name="buffer">Data to write.</param>
        /// <param name="offset">Offset of start of data.</param>
        public bool EncodeSector53_256(CircularBitBuffer trkData, int dataFieldBitOffset,
                byte[] buffer, int offset) {
            if (dataFieldBitOffset == -1) {
                return false;
            }
            trkData.BitPosition = dataFieldBitOffset;

            byte[] top = mTmpBuf1;          // CHUNK_SIZE_53 * 5 + 1 == 256
            byte[] threes = mTmpBuf2;       // CHUNK_SIZE_53 * 3 + 1 == 154

            // Split the bytes into sections.
            int chunk = CHUNK_SIZE_53 - 1;
            for (int i = 0; i < CHUNK_SIZE_53 * 5; i += 5) {
                byte three0 = buffer[offset++];
                byte three1 = buffer[offset++];
                byte three2 = buffer[offset++];
                byte three3 = buffer[offset++];
                byte three4 = buffer[offset++];

                top[chunk + CHUNK_SIZE_53 * 0] = (byte)(three0 >> 3);
                top[chunk + CHUNK_SIZE_53 * 1] = (byte)(three1 >> 3);
                top[chunk + CHUNK_SIZE_53 * 2] = (byte)(three2 >> 3);
                top[chunk + CHUNK_SIZE_53 * 3] = (byte)(three3 >> 3);
                top[chunk + CHUNK_SIZE_53 * 4] = (byte)(three4 >> 3);

                threes[chunk + CHUNK_SIZE_53 * 0] = (byte)
                    ((three0 & 0x07) << 2 | (three3 & 0x04) >> 1 | (three4 & 0x04) >> 2);
                threes[chunk + CHUNK_SIZE_53 * 1] = (byte)
                    ((three1 & 0x07) << 2 | (three3 & 0x02)      | (three4 & 0x02) >> 1);
                threes[chunk + CHUNK_SIZE_53 * 2] = (byte)
                    ((three2 & 0x07) << 2 | (three3 & 0x01) << 1 | (three4 & 0x01));

                chunk--;
            }
            Debug.Assert(chunk == -1);

            // Handle the last byte.
            byte val = buffer[offset++];
            top[255] = (byte)(val >> 3);
            threes[THREE_SIZE - 1] = (byte)(val & 0x07);

            // Output the bytes.
            int checksum = DataChecksumSeed;
            for (int i = CHUNK_SIZE_53 * 3; i >= 0; i--) {
                trkData.WriteOctet(sDiskBytes53[threes[i] ^ checksum]);
                checksum = threes[i];
            }
            for (int i = 0; i < 256; i++) {
                trkData.WriteOctet(sDiskBytes53[top[i] ^ checksum]);
                checksum = top[i];
            }

            // Output the checksum.
            if (checksum >= sDiskBytes53.Length) {
                // Checksum seed has created an un-encodable value.
                Debug.WriteLine("Bad data checksum seed");
                Debug.Assert(false);
                checksum = 0x1f;
            }
            trkData.WriteOctet(sDiskBytes53[checksum]);

            return true;
        }

        /// <summary>
        /// Decodes 256 bytes of 5&3-encoded sector data.
        /// </summary>
        /// <param name="trkData">Nibble track data.</param>
        /// <param name="dataFieldBitOffset">Offset of start of data.</param>
        /// <param name="buffer">Buffer that will receive 256 bytes of data.</param>
        /// <param name="offset">Offset within buffer to place data.</param>
        /// <returns>True on success.</returns>
        public bool DecodeSector53_256(CircularBitBuffer trkData, int dataFieldBitOffset,
                byte[] buffer, int offset) {
            Debug.Assert(mTmpBuf1.Length >= THREE_SIZE);        // 154
            Debug.Assert(mTmpBuf2.Length >= 256);

            if (dataFieldBitOffset == -1) {
                return false;
            }
            trkData.BitPosition = dataFieldBitOffset;

            byte[] threes = mTmpBuf1;
            byte[] bases = mTmpBuf2;

            byte checksum = DataChecksumSeed;

            // Read all 410 bytes, convert them from disk nibbles to 5-bit values, and arrange
            // them into a DOS-like pair of buffers.
            for (int i = THREE_SIZE - 1; i >= 0; i--) {
                byte val = trkData.LatchNextByte();
                byte decodedVal = sInvDiskBytes53[val];
                if (decodedVal == INVALID_INV) {
                    Debug.WriteLine("Found an invalid 5&3 disk byte: $" + val.ToString("x4"));
                    return false;
                }

                checksum ^= decodedVal;
                threes[i] = checksum;
            }
            for (int i = 0; i < 256; i++) {
                byte val = trkData.LatchNextByte();
                byte decodedVal = sInvDiskBytes53[val];
                if (decodedVal == INVALID_INV) {
                    Debug.WriteLine("Found an invalid 5&3 disk byte: $" + val.ToString("x4"));
                    return false;
                }

                checksum ^= decodedVal;
                bases[i] = (byte)(checksum << 3);
            }

            // Grab the 411th byte (checksum) and see if it matches.
            byte chkVal = trkData.LatchNextByte();
            byte decChkVal = sInvDiskBytes53[chkVal];
            if (DoTestDataChecksum && decChkVal != checksum) {
                Debug.WriteLine("Bad data checksum");
                return false;
            }

            // Convert the 5-byte values to 255 8-bit values.
            int initialOffset = offset;
            for (int i = CHUNK_SIZE_53 - 1; i >= 0; i--) {
                byte three1, three2, three3, three4, three5;

                three1 = threes[i];
                three2 = threes[CHUNK_SIZE_53 + i];
                three3 = threes[CHUNK_SIZE_53 * 2 + i];
                three4 = (byte)((three1 & 0x02) << 1 | (three2 & 0x02) | (three3 & 0x02) >> 1);
                three5 = (byte)((three1 & 0x01) << 2 | (three2 & 0x01) << 1 | (three3 & 0x01));

                buffer[offset++] = (byte)(bases[i] | ((three1 >> 2) & 0x07));
                buffer[offset++] = (byte)(bases[CHUNK_SIZE_53 + i] | ((three2 >> 2) & 0x07));
                buffer[offset++] = (byte)(bases[CHUNK_SIZE_53 * 2 + i] | ((three3 >> 2) & 0x07));
                buffer[offset++] = (byte)(bases[CHUNK_SIZE_53 * 3 + i] | (three4 & 0x07));
                buffer[offset++] = (byte)(bases[CHUNK_SIZE_53 * 4 + i] | (three5 & 0x07));
            }

            // Convert the last byte, which is handled specially.
            buffer[offset++] = (byte)(bases[255] | (threes[THREE_SIZE - 1] & 0x07));
            Debug.Assert(offset == initialOffset + 256);

            return true;
        }

        private const int CHUNK_SIZE_62_256 = 86;                       // 0x56

        /// <summary>
        /// Encodes 256 bytes as 343 bytes of 6&2-encoded data.
        /// </summary>
        /// <param name="trkData">Nibble track data.</param>
        /// <param name="dataFieldBitOffset">Offset of start of data.</param>
        /// <param name="buffer">Data to write.</param>
        /// <param name="offset">Offset of start of data.</param>
        public bool EncodeSector62_256(CircularBitBuffer trkData, int dataFieldBitOffset,
                byte[] buffer, int offset) {
            if (dataFieldBitOffset == -1) {
                return false;
            }
            trkData.BitPosition = dataFieldBitOffset;

            byte[] top = mTmpBuf1;
            byte[] twos = mTmpBuf2;
            Array.Clear(twos);

            int twoShift = 0;
            for (int i = 0, twoPosn = CHUNK_SIZE_62_256 - 1; i < 256; i++) {
                byte val = buffer[offset + i];
                top[i] = (byte)(val >> 2);
                twos[twoPosn] |= (byte)(((val & 0x01) << 1 | (val & 0x02) >> 1) << twoShift);
                if (twoPosn == 0) {
                    twoPosn = CHUNK_SIZE_62_256;
                    twoShift += 2;
                }
                twoPosn--;
            }

            byte checksum = DataChecksumSeed;
            for (int i = CHUNK_SIZE_62_256 - 1; i >= 0; i--) {
                Debug.Assert(twos[i] < sDiskBytes62.Length);
                byte chkVal = (byte)(twos[i] ^ checksum);
                if (chkVal >= sDiskBytes62.Length) {
                    // Checksum seed has created an un-encodable value.
                    Debug.WriteLine("Bad data checksum seed");
                    Debug.Assert(false);
                    chkVal = 0x3f;
                }
                trkData.WriteOctet(sDiskBytes62[chkVal]);
                checksum = twos[i];
            }
            for (int i = 0; i < 256; i++) {
                Debug.Assert(top[i] < sDiskBytes62.Length);
                trkData.WriteOctet(sDiskBytes62[top[i] ^ checksum]);
                checksum = top[i];
            }

            trkData.WriteOctet(sDiskBytes62[checksum]);
            return true;
        }

        /// <summary>
        /// Decodes 256 bytes of 6&2-encoded sector data.
        /// </summary>
        /// <param name="trkData">Nibble track data.</param>
        /// <param name="dataFieldBitOffset">Offset of start of data.</param>
        /// <param name="buffer">Buffer that will receive 256 bytes of data.</param>
        /// <param name="offset">Offset within buffer to place data.</param>
        /// <returns>True on success.</returns>
        public bool DecodeSector62_256(CircularBitBuffer trkData, int dataFieldBitOffset,
                byte[] buffer, int offset) {
            if (dataFieldBitOffset == -1) {
                return false;
            }
            trkData.BitPosition = dataFieldBitOffset;

            Debug.Assert(mTmpBuf1.Length >= CHUNK_SIZE_62_256 * 3);     // 258
            byte[] twos = mTmpBuf1;

            byte checksum = DataChecksumSeed;

            // Pull the "twos" values out and arrange them in a parallel buffer.
            for (int i = 0; i < CHUNK_SIZE_62_256; i++) {
                byte val = trkData.LatchNextByte();
                byte decodedVal = sInvDiskBytes62[val];
                if (decodedVal == INVALID_INV) {
                    Debug.WriteLine("Found an invalid 6&2 disk byte: $" + val.ToString("x4"));
                    return false;
                }

                checksum ^= decodedVal;
                twos[i] =
                    (byte)(((checksum & 0x01) << 1) | ((checksum & 0x02) >> 1));
                twos[i + CHUNK_SIZE_62_256] =
                    (byte)(((checksum & 0x04) >> 1) | ((checksum & 0x08) >> 3));
                twos[i + CHUNK_SIZE_62_256 * 2] =
                    (byte)(((checksum & 0x10) >> 3) | ((checksum & 0x20) >> 5));
            }

            // Pull the "sixes" values out and merge them with the "twos".
            for (int i = 0; i < 256; i++) {
                byte val = trkData.LatchNextByte();
                byte decodedVal = sInvDiskBytes62[val];
                if (decodedVal == INVALID_INV) {
                    Debug.WriteLine("Found an invalid 6&2 disk byte: $" + val.ToString("x4"));
                    return false;
                }
                checksum ^= decodedVal;
                buffer[offset + i] = (byte)((checksum << 2) | twos[i]);
            }

            // Finally, read the checksum byte and see if it matches.
            byte chkVal = trkData.LatchNextByte();
            byte decChkVal = sInvDiskBytes62[chkVal];
            if (DoTestDataChecksum && decChkVal != checksum) {
                Debug.WriteLine("Bad data checksum");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Writes the sector address header on a 5.25" disk track.
        /// </summary>
        /// <param name="trkData">Nibble data.  Must be positioned at the place where the address
        ///   prolog will begin.</param>
        /// <param name="track">Track number to encode (0-79).</param>
        /// <param name="sector">Sector number to encode (0-11).</param>
        /// <param name="side">Disk side (0 or 1).</param>
        /// <param name="format">Format value to encode.</param>
        public void WriteAddressField_35(CircularBitBuffer trkData, byte track, byte sector,
                int side, byte format) {
            if (ReadOnly) {
                throw new NotSupportedException("Can't write with this codec");
            }
            // All values must fit in 6 bits.
            if (track >= MAX_TRACK_35 || sector > 0x3f || (side != 0 && side != 1) ||
                    format > 0x3f) {
                throw new ArgumentException("Bad value");
            }
            byte trkLow = (byte)(track & 0x3f);
            byte trkSide = (byte)(side << 5 | track >> 6);
            byte checksum = (byte)(trkLow ^ sector ^ trkSide ^ format);
            Debug.Assert(checksum <= 0x3f);
            trkData.WriteOctets(mAddressProlog);
            trkData.WriteOctet(sDiskBytes62[trkLow]);
            trkData.WriteOctet(sDiskBytes62[sector]);
            trkData.WriteOctet(sDiskBytes62[trkSide]);
            trkData.WriteOctet(sDiskBytes62[format]);
            trkData.WriteOctet(sDiskBytes62[checksum]);
            trkData.WriteOctets(mAddressEpilog);
            trkData.WriteOctet(0xff);
        }

        private const int CHUNK_SIZE_62_524 = 175;      // ceil(524 / 3)

        /// <summary>
        /// Encodes 524 bytes as 703 bytes of 6&2-encoded data.
        /// </summary>
        /// <param name="trkData">Nibble track data.</param>
        /// <param name="dataFieldBitOffset">Offset of start of data.</param>
        /// <param name="buffer">Data to write.</param>
        /// <param name="offset">Offset of start of data.</param>
        public bool EncodeSector62_524(CircularBitBuffer trkData, int dataFieldBitOffset,
                byte[] buffer, int offset) {
            if (dataFieldBitOffset == -1) {
                return false;
            }
            trkData.BitPosition = dataFieldBitOffset;
            int startOffset = offset;

            byte[] part0 = mTmpBuf1;
            byte[] part1 = mTmpBuf2;
            byte[] part2 = mTmpBuf3;
            uint chk0, chk1, chk2;

            // Compute checksum, and split the input into 3 parts.
            chk0 = chk1 = chk2 = 0;
            int idx = 0;
            while (true) {
                chk0 = (chk0 & 0xff) << 1;
                if ((chk0 & 0x0100) != 0) {
                    chk0++;
                }

                byte val = buffer[offset++];
                chk2 += val;
                if ((chk0 & 0x0100) != 0) {
                    chk2++;
                    chk0 &= 0xff;
                }
                part0[idx] = (byte)(val ^ chk0);

                val = buffer[offset++];
                chk1 += val;
                if (chk2 > 0xff) {
                    chk1++;
                    chk2 &= 0xff;
                }
                part1[idx] = (byte)(val ^ chk2);

                if (offset - startOffset == 524) {
                    break;
                }

                val = buffer[offset++];
                chk0 += val;
                if (chk1 > 0xff) {
                    chk0++;
                    chk1 &= 0xff;
                }
                part2[idx] = (byte)(val ^ chk1);
                idx++;
            }
            part2[CHUNK_SIZE_62_524 - 1] = 0x00;    // gets merged into the "twos"

            Debug.Assert(idx == CHUNK_SIZE_62_524 - 1);

            // Output the nibble data.
            for (int i = 0; i < CHUNK_SIZE_62_524; i++) {
                byte twos = (byte)(((part0[i] & 0xc0) >> 2) |
                                   ((part1[i] & 0xc0) >> 4) |
                                   ((part2[i] & 0xc0) >> 6));
                trkData.WriteOctet(sDiskBytes62[twos]);
                trkData.WriteOctet(sDiskBytes62[part0[i] & 0x3f]);
                trkData.WriteOctet(sDiskBytes62[part1[i] & 0x3f]);
                if (i != CHUNK_SIZE_62_524 - 1) {
                    trkData.WriteOctet(sDiskBytes62[part2[i] & 0x3f]);
                }
            }

            // Output the checksum.
            byte chktwos = 
                (byte)(((chk0 & 0xc0) >> 6) | ((chk1 & 0xc0) >> 4) | ((chk2 & 0xc0) >> 2));
            trkData.WriteOctet(sDiskBytes62[chktwos]);
            trkData.WriteOctet(sDiskBytes62[chk2 & 0x3f]);
            trkData.WriteOctet(sDiskBytes62[chk1 & 0x3f]);
            trkData.WriteOctet(sDiskBytes62[chk0 & 0x3f]);

            Debug.Assert(trkData.BitPosition == dataFieldBitOffset + EncodedSectorSize * 8);
            return true;
        }

        /// <summary>
        /// Decodes 524 bytes of 6&2-encoded sector data.
        /// </summary>
        /// <param name="trkData">Nibble track data.</param>
        /// <param name="dataFieldBitOffset">Offset of start of data.</param>
        /// <param name="buffer">Buffer that will receive 524 bytes of data.</param>
        /// <param name="offset">Offset within buffer to place data.</param>
        /// <returns>True on success.</returns>
        public bool DecodeSector62_524(CircularBitBuffer trkData, int dataFieldBitOffset,
                byte[] buffer, int offset) {
            if (dataFieldBitOffset == -1) {
                return false;
            }
            trkData.BitPosition = dataFieldBitOffset;

            byte[] part0 = mTmpBuf1;
            byte[] part1 = mTmpBuf2;
            byte[] part2 = mTmpBuf3;
            byte twos, nib0, nib1, nib2;

            // Assemble 8-bit bytes from 6&2 encoded values.
            for (int i = 0; i < CHUNK_SIZE_62_524; i++) {
                twos = sInvDiskBytes62[trkData.LatchNextByte()];
                nib0 = sInvDiskBytes62[trkData.LatchNextByte()];
                nib1 = sInvDiskBytes62[trkData.LatchNextByte()];
                nib2 = 0;
                if (i != CHUNK_SIZE_62_524 - 1) {
                    nib2 = sInvDiskBytes62[trkData.LatchNextByte()];
                }
                if (twos == INVALID_INV || nib0 == INVALID_INV || nib1 == INVALID_INV ||
                        nib2 == INVALID_INV) {
                    Debug.WriteLine("Invalid disk byte found");
                    return false;
                }

                part0[i] = (byte)(nib0 | ((twos << 2) & 0xc0));
                part1[i] = (byte)(nib1 | ((twos << 4) & 0xc0));
                part2[i] = (byte)(nib2 | ((twos << 6) & 0xc0));
            }

            // Grab the checksum.
            twos = sInvDiskBytes62[trkData.LatchNextByte()];
            nib2 = sInvDiskBytes62[trkData.LatchNextByte()];
            nib1 = sInvDiskBytes62[trkData.LatchNextByte()];
            nib0 = sInvDiskBytes62[trkData.LatchNextByte()];
            if (twos == INVALID_INV || nib0 == INVALID_INV || nib1 == INVALID_INV ||
                    nib2 == INVALID_INV) {
                Debug.WriteLine("Invalid disk byte found");
                return false;
            }
            byte readChk2 = (byte)(nib2 | ((twos << 2) & 0xc0));
            byte readChk1 = (byte)(nib1 | ((twos << 4) & 0xc0));
            byte readChk0 = (byte)(nib0 | ((twos << 6) & 0xc0));

            // Output data and compute the checksum.  This is a little nuts.
            uint chk0, chk1, chk2;
            chk0 = chk1 = chk2 = 0;
            int outCount = 0;
            int index = 0;
            while (true) {
                chk0 = (chk0 & 0xff) << 1;
                if ((chk0 & 0x0100) != 0) {
                    chk0++;
                }

                byte val = (byte)(part0[index] ^ chk0);
                chk2 += val;
                if ((chk0 & 0x0100) != 0) {
                    chk2++;
                    chk0 &= 0xff;
                }
                buffer[offset + outCount] = val;
                outCount++;

                val = (byte)(part1[index] ^ chk2);
                chk1 += val;
                if (chk2 > 0xff) {
                    chk1++;
                    chk2 &= 0xff;
                }
                buffer[offset + outCount] = val;
                outCount++;

                if (outCount == 524)
                    break;

                val = (byte)(part2[index] ^ chk1);
                chk0 += val;
                if (chk1 > 0xff) {
                    chk0++;
                    chk1 &= 0xff;
                }
                buffer[offset + outCount] = val;
                outCount++;

                index++;
                Debug.Assert(index < CHUNK_SIZE_62_524);
            }

            if (DoTestDataChecksum &&
                    (readChk0 != (byte)chk0 || readChk1 != (byte)chk1 || readChk2 != (byte)chk2)) {
                Debug.WriteLine(string.Format("Bad data checksum: calc=${0:x2} ${1:x2} ${2:x2} " +
                    "read=${3:x2} {4:x2} {5:x2}",
                    (byte)chk0, (byte)chk1, (byte)chk2, readChk0, readChk1, readChk2));
                return false;
            }

#if DEBUG
            // We tested this when we were evaluating the sector, so we don't need to do it here.
            // If debugging, double-check it to make sure we ended up in the right place.
            int savedPosn = trkData.BitPosition;
            if (!trkData.ExpectLatchSequence(mDataEpilog, DataEpilogReadCount)) {
                Debug.Assert(false, "Data field epilog not found");
            }
            trkData.BitPosition = savedPosn;
#endif
            return true;
        }

        #region Utilities

        /// <summary>
        /// Latches the next two bytes from the track, and converts them to a single byte value
        /// using the 4&4 decoder.
        /// </summary>
        /// <param name="track">Buffer of track data.</param>
        /// <returns>Decoded value.</returns>
        public static byte GetNext44Value(CircularBitBuffer track) {
            byte val1 = track.LatchNextByte();
            byte val2 = track.LatchNextByte();
            return From44(val1, val2);
        }

        /// <summary>
        /// Converts a pair of 4&4-encoded bytes back to a single byte.
        /// </summary>
        /// <param name="val1">First byte.</param>
        /// <param name="val2">Second byte.</param>
        /// <returns>Decoded value.</returns>
        public static byte From44(byte val1, byte val2) {
            return (byte)(((val1 << 1) | 0x01) & val2);
        }

        /// <summary>
        /// Converts a byte value to the low part of a 4&4-encoded byte pair.
        /// </summary>
        public static byte To44Lo(byte val) {
            return (byte)((val >> 1) | 0xaa);
        }

        /// <summary>
        /// Converts a byte value to the high part of a 4&4-encoded byte pair.
        /// </summary>
        public static byte To44Hi(byte val) {
            return (byte)(val | 0xaa);
        }

        /// <summary>
        /// Converts a value in the range [0,63] to a 6&2 disk nibble.
        /// </summary>
        public static byte To62(byte val) {
            if (val > 0x3f) {
                throw new ArgumentOutOfRangeException("Cannot convert to 6&2 value: 0x"
                    + val.ToString("x2"));
            }
            return sInvDiskBytes62[val];
        }

        /// <summary>
        /// Latches the next byte from the track, and converts it to [0,63] with the 6&2 decoder.
        /// </summary>
        /// <param name="track">Buffer of track data.</param>
        /// <returns>Decoded value.</returns>
        public static byte GetNext62Value(CircularBitBuffer track) {
            byte val = track.LatchNextByte();
            return sInvDiskBytes62[val];
        }

        /// <summary>
        /// Generates an inverse disk byte translation table.
        /// </summary>
        private static byte[] GenerateInv(byte[] table) {
            byte[] invTable = new byte[256];
            RawData.MemSet(invTable, 0, invTable.Length, INVALID_INV);
            for (byte i = 0; i < table.Length; i++) {
                Debug.Assert(table[i] >= 0x96);
                invTable[table[i]] = i;
            }
            return invTable;
        }

        // Mapping of block number to cylinder/head/sector on GCR 3.5" disks with 1 or 2 sides.
        // Each entry has the form 0x00CCHHSS.
        const int BLOCKS_PER_SIDE_GCR35 = 800;
        private static readonly uint[] sCHSTable_GCR35_1 = GenerateCHSTable_GCR35(1);
        private static readonly uint[] sCHSTable_GCR35_2 = GenerateCHSTable_GCR35(2);

        private static uint[] GenerateCHSTable_GCR35(int numSides) {
            uint[] table = new uint[BLOCKS_PER_SIDE_GCR35 * numSides];
            int sctPerTrk = 12;                     // initially 12 sectors/track
            uint block = 0;
            for (uint cyl = 0; cyl < 80; cyl++) {   // 80 cylinders
                for (uint head = 0; head < numSides; head++) {
                    for (uint sct = 0; sct < sctPerTrk; sct++) {
#if DEBUG               // compare to iterative method
                        BlockToCylHeadSct_GCR35_Chk(block, numSides,
                            out uint ccyl, out uint chead, out uint csct);
                        Debug.Assert(cyl == ccyl && head == chead && sct == csct);
#endif
                        table[block++] = (cyl << 16) | (head << 8) | sct;
                    }
                }

                if ((cyl & 0x0f) == 0x0f) {
                    sctPerTrk--;            // ZCAV drops 1 sector every 16 cylinders
                }
            }
            Debug.Assert(block == BLOCKS_PER_SIDE_GCR35 * numSides);
            return table;
        }

        /// <summary>
        /// Maps a block number to cylinder/head/sector values for a SSDD/DSDD GCR 3.5" floppy.
        /// </summary>
        /// <param name="block">Block number to map (0-1599).</param>
        /// <param name="numSides">Number of sides the disk has (1 or 2).</param>
        /// <param name="cyl">Result: cylinder (0-79).</param>
        /// <param name="head">Result: head (0 or 1).</param>
        /// <param name="sct">Result: sector (0-11).</param>
        public static void BlockToCylHeadSct_GCR35(uint block, int numSides,
                out uint cyl, out uint head, out uint sct) {
            if (numSides != 1 && numSides != 2) {
                throw new ArgumentOutOfRangeException("Bad numSides: " + numSides);
            }
            if (block > BLOCKS_PER_SIDE_GCR35 * numSides) {
                throw new ArgumentOutOfRangeException("Bad block: " + block);
            }
            uint entry;
            if (numSides == 1) {
                entry = sCHSTable_GCR35_1[block];
            } else {
                entry = sCHSTable_GCR35_2[block];
            }
            cyl = (entry >> 16) & 0xff;
            head = (entry >> 8) & 0xff;
            sct = entry & 0xff;
        }

#if DEBUG
        private static void BlockToCylHeadSct_GCR35_Chk(uint block, int numSides,
                out uint cyl, out uint head, out uint sct) {
            // 12 sectors/track at the outside, reduced by 1 every 16 cylinders.
            // This may iterate up to 160x.
            uint sctPerTrk = 12;
            cyl = 0;
            head = 0;
            while (block >= sctPerTrk) {
                block -= sctPerTrk;
                head++;
                if (head == numSides) {
                    head = 0;
                    cyl++;
                    if ((cyl & 0x0f) == 0) {
                        sctPerTrk--;
                    }
                }
            }
            sct = block;
        }
#endif

        #endregion Utilities

        public override string ToString() {
            return "[SectorCodec '" + Name + "']";
        }
    }
}
