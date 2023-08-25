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

namespace DiskArc.Disk {
    /// <summary>
    /// "Standard" disk sector codec.  Supports some variety in values and parameters, but
    /// expects the data to be generally similar to standard Apple formats.
    /// </summary>
    public class StdSectorCodec : SectorCodec {
        public enum CodecIndex525 {
            Std_525_16,
            Std_525_13,
            RDOS33_525_16,
            RDOS32_525_13,
            Muse_525_13,
        }
        public enum CodecIndex35 {
            Std_35,
        }

        // Special handling for specific types of disks.  Only useful for disks that are mostly
        // standard but just slightly off.
        private enum Special {
            None = 0,
            RDOS33,
            Muse13,
        }
        private Special mSpecial = Special.None;

        // Some address/data patterns used on specific disks.
        private static readonly byte[] sD4AA96 = { 0xd4, 0xaa, 0x96 };
        private static readonly byte[] sD4AAB7 = { 0xd4, 0xaa, 0xb7 };


        /// <summary>
        /// Generates a codec for a 5.25" nibble disk image.
        /// </summary>
        /// <param name="index">Codec index.</param>
        /// <returns>New codec instance.</returns>
        public static SectorCodec GetCodec(CodecIndex525 index) {
            StdSectorCodec codec = new StdSectorCodec();

            codec.DataEncoding = NibbleEncoding.GCR62;
            codec.DecodedSectorSize = 256;
            codec.EncodedSectorSize = 342 + 1;
            codec.mAddressProlog = sD5AA96;
            codec.mAddressEpilog = sDEAAEB;
            codec.mDataProlog = sD5AAAD;
            codec.mDataEpilog = sDEAAEB;
            codec.AddrEpilogReadCount = 2;
            codec.AddrChecksumSeed = 0x00;
            codec.DoTestAddrTrack = true;
            codec.DoTestAddrChecksum = true;
            codec.DataEpilogReadCount = 2;
            codec.DataChecksumSeed = 0x00;
            codec.DoTestDataChecksum = true;

            switch (index) {
                case CodecIndex525.Std_525_16:
                    codec.Name = "Std 16-sector";
                    break;
                case CodecIndex525.RDOS33_525_16:
                    codec.Name = "RDOS 16-sector";
                    codec.AddrEpilogReadCount = 0;
                    codec.mSpecial = Special.RDOS33;
                    break;
                case CodecIndex525.Std_525_13:
                    codec.Name = "Std 13-sector";
                    codec.Set13();
                    break;
                case CodecIndex525.RDOS32_525_13:
                    codec.Name = "RDOS 13-sector";
                    codec.Set13();
                    codec.mAddressProlog = sD4AAB7;     // $D4 not legal?
                    break;
                case CodecIndex525.Muse_525_13:
                    codec.Name = "Muse 13-sector";
                    codec.Set13();
                    codec.mSpecial = Special.Muse13;
                    break;
                default:
                    throw new NotImplementedException("Unknown codec index: " + index);
            }

            // Set read-only if we don't know how to compute checksums, or we're doing
            // "special" things.
            // TODO: make the "special" things work when writing
            codec.ReadOnly = (!codec.DoTestAddrChecksum || !codec.DoTestDataChecksum
                || codec.mSpecial != Special.None);

            return codec;
        }

        private void Set13() {
            DataEncoding = NibbleEncoding.GCR53;
            EncodedSectorSize = 410 + 1;
            mAddressProlog = sD5AAB5;
        }

        /// <summary>
        /// Generates a codec for a 3.5" nibble disk image.
        /// </summary>
        /// <param name="index">Codec index.</param>
        /// <returns>New codec instance.</returns>
        public static SectorCodec GetCodec(CodecIndex35 index) {
            StdSectorCodec codec = new StdSectorCodec();

            codec.DataEncoding = NibbleEncoding.GCR62;
            codec.DecodedSectorSize = 524;
            codec.EncodedSectorSize = 699 + 4;
            codec.mAddressProlog = sD5AA96;
            codec.mAddressEpilog = sDEAA;
            codec.mDataProlog = sD5AAAD;
            codec.mDataEpilog = sDEAA;
            codec.AddrEpilogReadCount = 2;
            codec.AddrChecksumSeed = 0x00;
            codec.DoTestAddrTrack = true;
            codec.DoTestAddrChecksum = true;
            codec.DataEpilogReadCount = 2;
            codec.DataChecksumSeed = 0x00;
            codec.DoTestDataChecksum = true;

            switch (index) {
                case CodecIndex35.Std_35:
                    codec.Name = "Std 3.5\" disk";
                    break;
                default:
                    throw new NotImplementedException("Unknown codec index: " + index);
            }

            return codec;
        }

        protected override void PreAdjust(uint trackNum, uint trackFraction) {
            if (mSpecial == Special.RDOS33) {
                // RDOS 3.3 uses d4/aa/96 on odd tracks, d5/aa/96 on even tracks.
                if ((trackNum & 0x01) == 1) {
                    mAddressProlog = sD4AA96;
                } else {
                    mAddressProlog = sD5AA96;
                }
            }
        }

        protected override void AddrPostAdjust(SectorPtr ptr) {
            if (mSpecial == Special.Muse13) {
                // 13-sector games published by Muse have doubled sector numbers on tracks > 2.
                if (ptr.Track > 2) {
                    byte sct = ptr.Sector;
                    //Debug.Assert((sct & 0x01) == 0);
                    ptr.Sector = (byte)(sct >> 1);
                }
            }
        }
    }
}
