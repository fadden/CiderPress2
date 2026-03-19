/*
 * Copyright 2026 faddenSoft
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
using System.Text;

using CommonUtil;
using static DiskArc.Defs;
using static DiskArc.IMetadata;

namespace DiskArc.Disk {
    /// <summary>
    /// Class for managing the contents of the MOOF file INFO chunk.  Some pieces are fixed when
    /// the disk image is created and may not be changed, others can be modified by applications.
    /// </summary>
    /// <remarks>
    /// <para>INFO values in a read-only disk image can be edited, but the changes will be
    /// discarded.</para>
    /// </remarks>
    public class Moof_Info {
        /// <summary>
        /// True if modifications should be blocked.
        /// </summary>
        public bool IsReadOnly { get; internal set; }

        /// <summary>
        /// True if one or more fields have been modified.
        /// </summary>
        public bool IsDirty { get; internal set; }


        internal static readonly MetaEntry[] sInfoList = new MetaEntry[] {
            new MetaEntry("info_version", MetaEntry.ValType.Int,
                "Version number of the INFO chunk.", "", canEdit:false),
            new MetaEntry("disk_type", MetaEntry.ValType.Int,
                "Disk type (1=SSDD GCR, 2=DSDD GCR, 3=DSHD MFM, 4=Twiggy).", "", canEdit:false),
            new MetaEntry("write_protected", MetaEntry.ValType.Bool,
                "Should emulators treat the disk as write-protected?", MetaEntry.BOOL_DESC,
                canEdit:true),
            new MetaEntry("synchronized", MetaEntry.ValType.Bool,
                "Was cross-track sync used during imaging?", "", canEdit:false),
            new MetaEntry("optimal_bit_timing", MetaEntry.ValType.Int,
                "Ideal disk controller bit rate.", "", canEdit:false),
            new MetaEntry("creator", MetaEntry.ValType.String,
                "Name of software that created the MOOF file.", "", canEdit:false),
            new MetaEntry("largest_track", MetaEntry.ValType.Int,
                "Number of blocks used by the largest track.", "", canEdit:false),
            new MetaEntry("flux_block", MetaEntry.ValType.Int,
                "Block number where the FLUX chunk resides.", "", canEdit:false),
            new MetaEntry("largest_flux_track", MetaEntry.ValType.Int,
                "Number of blocks used by the largest FLUX track.", "", canEdit:false),
        };

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
        public Moof_Info(string creator, MediaKind diskKind, ushort lrgTrkBlkCnt) {
            mData = new byte[Woz.INFO_LENGTH];

            Version = 1;
            switch (diskKind) {
                case MediaKind.GCR_SSDD35:
                    DiskType = 1;
                    OptimalBitTiming = 16;
                    break;
                case MediaKind.GCR_DSDD35:
                    DiskType = 2;
                    OptimalBitTiming = 16;
                    break;
                case MediaKind.MFM_DSHD35:
                    DiskType = 3;
                    OptimalBitTiming = 8;
                    break;
                case MediaKind.GCR_Twiggy:
                    DiskType = 4;
                    OptimalBitTiming = 16;
                    break;
                default:
                    throw new NotImplementedException("Unknown disk kind: " + diskKind);
            }
            WriteProtected = false;
            Synchronized = false;
            Creator = creator;
            LargestTrack = lrgTrkBlkCnt;
            // leave other fields set to zero

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
        public Moof_Info(byte[] buffer, int offset) {
            mData = new byte[Woz.INFO_LENGTH];
            Array.Copy(buffer, offset, mData, 0, Woz.INFO_LENGTH);
        }

        /// <summary>
        /// Constructs an empty object.
        /// </summary>
        public Moof_Info() {
            mData = new byte[Woz.INFO_LENGTH];
        }

        /// <summary>
        /// Obtains the raw chunk data buffer, for output to the disk image file.
        /// </summary>
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
        /// Gets the value of a metadata entry as a string.
        /// </summary>
        /// <param name="key">Entry key.</param>
        /// <param name="verbose">True if we want the verbose version.</param>
        /// <returns>Entry value, or null if the key wasn't recognized.</returns>
        public string? GetValue(string key, bool verbose) {
            const string TRUE_LOWER = "true";       // return "true" instead of "True"
            const string FALSE_LOWER = "false";     // (bool.ToString() capitalizes the value)

            string? value;
            switch (key) {
                case "info_version":
                    value = Version.ToString();
                    break;
                case "disk_type":
                    value = DiskType.ToString();
                    if (verbose) {
                        if (DiskType == 1) {
                            value += " (400KB GCR SSDD)";
                        } else if (DiskType == 2) {
                            value += " (800KB GCR DSDD)";
                        } else if (DiskType == 3) {
                            value += " (1.44MB MFM DSHD)";
                        } else if (DiskType == 4) {
                            value += " (Twiggy)";
                        } else {
                            value += " (?)";
                        }
                    }
                    break;
                case "write_protected":
                    value = WriteProtected ? TRUE_LOWER : FALSE_LOWER;
                    break;
                case "synchronized":
                    value = Synchronized ? TRUE_LOWER : FALSE_LOWER;
                    break;
                case "optimal_bit_timing":
                    value = OptimalBitTiming.ToString();
                    break;
                case "creator":
                    value = Creator.TrimEnd();  // trim the trailing space padding
                    break;
                case "largest_track":
                    value = LargestTrack.ToString();
                    break;
                case "flux_block":
                    value = FluxBlock.ToString();
                    break;
                case "largest_flux_track":
                    value = LargestFluxTrack.ToString();
                    break;
                default:
                    Debug.WriteLine("MOOF info key '" + key + "' not recognized");
                    value = null;
                    break;
            }
            return value;
        }

        /// <summary>
        /// Tests whether a value would be accepted by the "set" call.
        /// </summary>
        /// <param name="key">Entry key.</param>
        /// <param name="value">Value to test.</param>
        /// <returns>True if a set call would succeed.</returns>
        public bool TestValue(string key, string value) {
            switch (key) {
                case "write_protected":
                    return bool.TryParse(value, out bool wpVal);
                default:
                    return false;
            }
        }

        /// <summary>
        /// Sets the value of a metadata entry as a string.
        /// </summary>
        /// <param name="key">Entry key.</param>
        /// <param name="value">Value to set.</param>
        /// <exception cref="ArgumentException">Invalid key or value.</exception>
        public void SetValue(string key, string value) {
            // Parse the string to a numeric or boolean value, and then set the field.  The
            // field setter may apply additional restrictions.
            switch (key) {
                case "write_protected":
                    if (!bool.TryParse(value, out bool wpVal)) {
                        throw new ArgumentException("invalid value '" + value + "'");
                    }
                    WriteProtected = wpVal;
                    break;
                default:
                    throw new ArgumentException("unable to modify '" + key + "'");
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
                if (value != 1) {
                    throw new ArgumentException("Invalid version");
                }
                mData[0] = value;
                IsDirty = true;
            }
        }

        /// <summary>
        /// Disk type: 1=SSDD GCR (400K), 2=DSDD GCR (800K), 3=DSHD MFM (1.44M), 4=Twiggy.
        /// </summary>
        public byte DiskType {
            get { return mData[1]; }
            internal set {
                CheckAccess();
                if (value < 1 || value > 4) {
                    throw new ArgumentException("Invalid disk type");
                }
                mData[1] = value;
                IsDirty = true;
            }
        }

        /// <summary>
        /// Write protected: 0 = unprotected, 1 = protected.
        /// </summary>
        public bool WriteProtected {
            get { return mData[2] != 0; }
            set {
                CheckAccess();
                mData[2] = (byte)(value ? 1 : 0);
                IsDirty = true;
            }
        }

        /// <summary>
        /// Synchronized: 0 = not synchronized, 1 = cross-track sync was used during imaging.
        /// </summary>
        public bool Synchronized {
            get { return mData[3] != 0; }
            internal set {
                CheckAccess();
                mData[3] = (byte)(value ? 1 : 0);
                IsDirty = true;
            }
        }

        /// <summary>
        /// Optimal bit timing: disk bit timing in 125-ns increments.
        /// </summary>
        /// <remarks>
        /// <para>Typical values are 16 (2us) for GCR and 8 (1us) for MFM.</para>
        /// </remarks>
        public byte OptimalBitTiming {
            get { return mData[4]; }
            internal set {
                CheckAccess();
                if (value < 4 || value > 24) {      // typically 16 for GCR and 8 for MFM
                    throw new ArgumentException("Invalid bit timing");
                }
                mData[4] = value;
                IsDirty = true;
            }
        }

        /// <summary>
        /// Creator: name of software that created the MOOF file.  UTF-8, padded to 32 bytes.
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

        // value at +37 is unused, must be zero

        /// <summary>
        /// Largest track: number of 512-byte blocks required to hold the largest track.
        /// </summary>
        public ushort LargestTrack {
            get { return RawData.GetU16LE(mData, 38); }
            internal set {
                CheckAccess();
                RawData.SetU16LE(mData, 38, value);
                IsDirty = true;
            }
        }

        /// <summary>
        /// FLUX block: absolute file position where the FLUX chunk lives, as a 512-byte
        /// block number.
        /// </summary>
        public ushort FluxBlock {
            get { return RawData.GetU16LE(mData, 40); }
            internal set {
                CheckAccess();
                RawData.SetU16LE(mData, 40, value);
                IsDirty = true;
            }
        }

        /// <summary>
        /// Largest flux track: number of 512-byte blocks required to hold the largest flux
        /// track.
        /// </summary>
        public ushort LargestFluxTrack {
            get { return RawData.GetU16LE(mData, 42); }
            internal set {
                CheckAccess();
                RawData.SetU16LE(mData, 42, value);
                IsDirty = true;
            }
        }

        /// <summary>
        /// Converts the string to UTF-8 bytes, and pads the end with spaces.  The encoded
        /// form is limited to 32 bytes.  Excess characters will be dropped.
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
