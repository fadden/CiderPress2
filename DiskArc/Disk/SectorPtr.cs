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

namespace DiskArc.Disk {
    /// <summary>
    /// Sector metadata holder.  Holds decoded address field values and offsets to the address and
    /// data fields.
    /// </summary>
    public class SectorPtr {
        /// <summary>
        /// True if something is wrong with the address field: bad checksum, missing epilog bytes,
        /// incorrect track.  Values are only checked if specified by the SectorCodec.
        /// </summary>
        public bool IsAddrDamaged { get; set; }

        /// <summary>
        /// True if something is wrong with the data field: bad checksum, missing epilog bytes,
        /// invalid disk nibbles.  Values are only checked if specified by the SectorCodec.
        /// </summary>
        public bool IsDataDamaged { get; set; }

        /// <summary>
        /// Bit offset of start of address field prolog bytes.
        /// </summary>
        public int AddrPrologBitOffset { get; set; }

        /// <summary>
        /// Bit offset of start of data field prolog bytes.
        /// </summary>
        /// <remarks>
        /// <para>Will be -1 if the sector only has an address field, e.g. newly-formatted
        /// 13-sector disks.</para>
        /// </remarks>
        public int DataPrologBitOffset { get; set; }

        /// <summary>
        /// Bit offset of start of encoded sector data.
        /// </summary>
        /// <remarks>
        /// <para>Normally this will be (DataPrologBitOffset + (DataProlog.Length * 8)), but
        /// could be slightly more if some of the nibbles were written long.</para>
        /// </remarks>
        public int DataFieldBitOffset { get; set; }

        /// <summary>
        /// Bit offset of the first byte past the end of the data field.  If the sector has
        /// a data epilog, it will be here.
        /// </summary>
        public int DataEndBitOffset { get; set; }

        /// <summary>
        /// Track number from address field.
        /// On a 3.5" disk this will be the [0,63] part.
        /// </summary>
        public byte Track { get; set; }

        /// <summary>
        /// Sector number from address field.
        /// </summary>
        /// <remarks>
        /// Some copy-protected disks double the sector value to confuse standard DOS.
        /// </remarks>
        public byte Sector { get; set; }

        /// <summary>
        /// Disk side and high bit of track number, from address field.
        /// Only set for 3.5" floppies; 0 for 5.25" disks.
        /// </summary>
        public byte Side { get; set; }

        /// <summary>
        /// Format value from address field, usually 0x22 or 0x24.
        /// Only set for 3.5" floppies; 0 for 5.25" disks.
        /// </summary>
        public byte Format { get; set; }

        /// <summary>
        /// Volume number from address field.
        /// Only set for 5.25" floppies; 0 for 3.5" disks.
        /// </summary>
        public byte Volume { get; set; }

        /// <summary>
        /// Address field checksum result.  Will be zero if the checksum matched.
        /// </summary>
        public byte AddrChecksumResult { get; set; }

        public SectorPtr() {
            AddrPrologBitOffset = DataPrologBitOffset = DataFieldBitOffset = DataEndBitOffset = -1;
        }

        public override string ToString() {
            return "[Sector T" + Track.ToString("D2") + "/" + Side +
                " S" + Sector.ToString("D2") +
                " addrOff=" + AddrPrologBitOffset +
                " (+$" + (AddrPrologBitOffset / 8).ToString("x4") + ")" +
                (IsAddrDamaged ? " (DMGAddr)" : "") +
                (IsDataDamaged ? " (DMGData)" : "") +
                "]";
        }
    }
}
