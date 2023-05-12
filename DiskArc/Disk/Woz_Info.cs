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
using System.Text;

using CommonUtil;
using static DiskArc.Defs;

namespace DiskArc.Disk {
    /// <summary>
    /// Class for managing the contents of the WOZ file INFO chunk.  Some pieces are fixed when
    /// the disk image is created and may not be changed, others can be modified by applications.
    /// </summary>
    /// <remarks>
    /// <para>INFO values in a read-only disk image can be edited, but the changes will be
    /// discarded.</para>
    /// </remarks>
    public class Woz_Info {
        /// <summary>
        /// True if modifications should be blocked.
        /// </summary>
        public bool IsReadOnly { get; internal set; }

        /// <summary>
        /// True if one or more fields have been modified.
        /// </summary>
        public bool IsDirty { get; internal set; }

        /// <summary>
        /// Raw INFO chunk data.  All values are read from and written to this.
        /// </summary>
        private byte[] mData;

        /// <summary>
        /// Constructs a new object for a new disk.
        /// </summary>
        /// <param name="creator">Creator ID string.</param>
        /// <param name="diskKind">What type of media this is.</param>
        /// <param name="lrgTrkBlkCnt">Largest track block count.</param>
        public Woz_Info(string creator, MediaKind diskKind, ushort lrgTrkBlkCnt) {
            mData = new byte[Woz.INFO_LENGTH];

            Version = 2;
            switch (diskKind) {
                case MediaKind.GCR_525:
                    DiskType = 1;
                    DiskSides = 1;
                    OptimalBitTiming = 32;
                    break;
                case MediaKind.GCR_SSDD35:
                    DiskType = 2;
                    DiskSides = 1;
                    OptimalBitTiming = 16;
                    break;
                case MediaKind.GCR_DSDD35:
                    DiskType = 2;
                    DiskSides = 2;
                    OptimalBitTiming = 16;
                    break;
                default:
                    throw new NotImplementedException("Unknown disk kind: " + diskKind);
            }
            WriteProtected = 0;
            Synchronized = 0;
            Cleaned = 1;
            Creator = creator;
            BootSectorFormat = 0;
            CompatibleHardware = 0;
            RequiredRAM = 0;
            LargestTrack = lrgTrkBlkCnt;

            // Leave v3 fields set to zero.

            IsDirty = true;
        }

        /// <summary>
        /// Constructs the object from an existing INFO chunk.
        /// </summary>
        /// <remarks>
        /// <para>This does not attempt to validate the data read.</para>
        /// </remarks>
        /// <param name="buffer">Buffer of INFO data.</param>
        /// <param name="offset">Offset of start of INFO data (past the chunk header).</param>
        public Woz_Info(byte[] buffer, int offset) {
            mData = new byte[Woz.INFO_LENGTH];
            Array.Copy(buffer, offset, mData, 0, Woz.INFO_LENGTH);
        }

        /// <summary>
        /// Constructs an empty object.
        /// </summary>
        public Woz_Info() {
            mData = new byte[Woz.INFO_LENGTH];
        }

        /// <summary>
        /// Obtains the raw chunk data buffer.
        /// </summary>
        /// <returns></returns>
        internal byte[] GetData() {
            // This is internal-only, so no need to make a copy.
            return mData;
        }

        private void CheckAccess() {
            if (IsReadOnly) {
                throw new InvalidOperationException("Image is read-only");
            }
        }

        /// <summary>
        /// Version number of the INFO chunk.
        /// Not editable. (But it could be if we wanted to update the various fields.)
        /// </summary>
        public byte Version {
            get { return mData[0]; }
            internal set {
                CheckAccess();
                if (value < 1 || value > 3) {
                    throw new ArgumentException("Invalid version");
                }
                mData[0] = value;
                IsDirty = true;
            }
        }

        /// <summary>
        /// Disk type (v1): 1 = 5.25, 2 = 3.5.
        /// </summary>
        public byte DiskType {
            get { return mData[1]; }
            internal set {
                CheckAccess();
                if (value < 1 || value > 2) {
                    throw new ArgumentException("Invalid disk type");
                }
                mData[1] = value;
                IsDirty = true;
            }
        }

        /// <summary>
        /// Write protected (v1): 0 = unprotected, 1 = protected.
        /// </summary>
        public byte WriteProtected {
            get { return mData[2]; }
            set {
                CheckAccess();
                if (value > 1) {
                    throw new ArgumentException("Invalid WP status");
                }
                mData[2] = value;
                IsDirty = true;
            }
        }

        /// <summary>
        /// Synchronized (v1): 0 = not synchronized, 1 = cross-track sync was used during imaging.
        /// </summary>
        public byte Synchronized {
            get { return mData[3]; }
            internal set {
                CheckAccess();
                if (value > 1) {
                    throw new ArgumentException("Invalid sync status");
                }
                mData[3] = value;
                IsDirty = true;
            }
        }

        /// <summary>
        /// Cleaned (v1): 0 = not cleaned, 1 = MC3470 fake bits have been removed.
        /// </summary>
        public byte Cleaned {
            get { return mData[4]; }
            internal set {
                CheckAccess();
                if (value > 1) {
                    throw new ArgumentException("Invalid cleaned status");
                }
                mData[4] = value;
                IsDirty = true;
            }
        }

        /// <summary>
        /// Creator (v1): name of software that created the WOZ file.  UTF-8, padded to 32 bytes.
        /// </summary>
        public string Creator {
            get { return Encoding.UTF8.GetString(mData, 5, Woz.CREATOR_ID_LEN); }
            set {
                CheckAccess();
                if (value == null) {
                    throw new ArgumentNullException("Can't be null");
                }
                byte[] newVal = EncodeCreator(value);
                Array.Copy(newVal, 0, mData, 5, Woz.CREATOR_ID_LEN);
                IsDirty = true;
            }
        }

        /// <summary>
        /// Disk sides (v2): number of sides; will be 1 for 5.25, 1 or 2 for 3.5.
        /// </summary>
        public byte DiskSides {
            get { return mData[37]; }
            internal set {
                CheckAccess();
                if (value < 1 || value > 2) {
                    throw new ArgumentException("Invalid disk sides");
                }
                mData[37] = value;
                IsDirty = true;
            }
        }

        /// <summary>
        /// Boot sector format (v2): 0 = unknown, 1 = 16-sector, 2 = 13-sector, 3 = both.
        /// </summary>
        public byte BootSectorFormat {
            get { return mData[38]; }
            set {
                CheckAccess();
                if (value > 3) {
                    throw new ArgumentException("Invalid boot sector format");
                }
                mData[38] = value;
                IsDirty = true;
            }
        }

        /// <summary>
        /// Optimal bit timing (v2): disk bit timing in 125-ns increments.
        /// </summary>
        /// <remarks>
        /// <para>Standard values are 32 (4us) for 5.25" disks, 16 (2us) for 3.5" disks.</para>
        /// </remarks>
        public byte OptimalBitTiming {
            get { return mData[39]; }
            internal set {
                CheckAccess();
                // Use range restriction from Wozardry.
                if (DiskType == 1) {
                    if (value < 24 || value > 40) {
                        throw new ArgumentException("Invalid bit timing for 5.25");
                    }
                } else {
                    if (value < 8 || value > 24) {
                        throw new ArgumentException("Invalid bit timing for 3.5");
                    }
                }
                mData[39] = value;
                IsDirty = true;
            }
        }

        /// <summary>
        /// Compatible hardware (v2): bit flags indicating hardware compatibility.
        /// </summary>
        public ushort CompatibleHardware {
            get { return RawData.GetU16LE(mData, 40); }
            set {
                CheckAccess();
                if (value >= 0x0200) {
                    throw new ArgumentException("Invalid compatible hardware");
                }
                RawData.SetU16LE(mData, 40, value);
                IsDirty = true;
            }
        }

        /// <summary>
        /// Required RAM (v2): minimum RAM required, in KB.
        /// </summary>
        public ushort RequiredRAM {
            get { return RawData.GetU16LE(mData, 42); }
            set {
                CheckAccess();
                if (value > 8192) {     // 8MB
                    throw new ArgumentException("Invalid required RAM");
                }
                RawData.SetU16LE(mData, 42, value);
                IsDirty = true;
            }
        }

        /// <summary>
        /// Largest track (v2): number of 512-byte blocks required to hold the largest track.
        /// </summary>
        public ushort LargestTrack {
            get { return RawData.GetU16LE(mData, 44); }
            internal set {
                CheckAccess();
                RawData.SetU16LE(mData, 44, value);
                IsDirty = true;
            }
        }

        /// <summary>
        /// FLUX block (v3): absolute file position where the FLUX chunk lives, as a 512-byte
        /// block number.
        /// </summary>
        public ushort FluxBlock {
            get { return RawData.GetU16LE(mData, 46); }
            internal set {
                CheckAccess();
                RawData.SetU16LE(mData, 46, value);
                IsDirty = true;
            }
        }

        /// <summary>
        /// Largest flux track (v3): number of 512-byte blocks required to hold the largest flux
        /// track.
        /// </summary>
        public ushort LargestFluxTrack {
            get { return RawData.GetU16LE(mData, 48); }
            internal set {
                CheckAccess();
                RawData.SetU16LE(mData, 48, value);
                IsDirty = true;
            }
        }

        /// <summary>
        /// Converts the string to UTF-8 bytes, and pads the end with spaces.  The encoded
        /// form is limited to 32 bytes.
        /// </summary>
        /// <param name="name">String to encode.</param>
        /// <returns>32-element byte array, end padded with ASCII spaces.</returns>
        private static byte[] EncodeCreator(string name) {
            byte[] enc = Encoding.UTF8.GetBytes(name);
            int i;
            byte[] result = new byte[Woz.CREATOR_ID_LEN];
            // TODO: this will produce bad data if a multi-byte encoded value starts at +31
            for (i = 0; i < Math.Min(Woz.CREATOR_ID_LEN, enc.Length); i++) {
                result[i] = enc[i];
            }
            for (; i < Woz.CREATOR_ID_LEN; i++) {
                result[i] = (byte)' ';
            }
            return result;
        }
    }
}
