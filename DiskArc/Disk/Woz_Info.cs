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
using System.Text;

using CommonUtil;
using static DiskArc.Defs;
using static DiskArc.IMetadata;

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
        /// Strings for the bits in the info:compatible_hardware value.
        /// </summary>
        public static readonly string[] sWozInfoCompat = new string[] {
            "2",    // 0x0001
            "2+",   // 0x0002
            "2e",   // 0x0004
            "2c",   // 0x0008
            "2e+",  // 0x0010
            "2gs",  // 0x0020
            "2c+",  // 0x0040
            "3",    // 0x0080
            "3+",   // 0x0100
        };

        private static readonly string NL = Environment.NewLine;
        internal static readonly MetaEntry[] sInfo1List = new MetaEntry[] {
            new MetaEntry("info_version", MetaEntry.ValType.Int,
                "Version number of the INFO chunk.", "", canEdit:false),
            new MetaEntry("disk_type", MetaEntry.ValType.Int,
                "Disk type (1=5.25\", 2=3.5\").", "", canEdit:false),
            new MetaEntry("write_protected", MetaEntry.ValType.Bool,
                "Should emulators treat the disk as write-protected?", MetaEntry.BOOL_DESC,
                canEdit:true),
            new MetaEntry("synchronized", MetaEntry.ValType.Bool,
                "Was cross-track sync used during imaging?", "", canEdit:false),
            new MetaEntry("cleaned", MetaEntry.ValType.Bool,
                "Have MC3470 fake bits been removed?", "", canEdit:false),
            new MetaEntry("creator", MetaEntry.ValType.String,
                "Name of software that created the WOZ file.", "", canEdit:false),
        };
        internal static readonly MetaEntry[] sInfo2List = new MetaEntry[] {
            new MetaEntry("disk_sides", MetaEntry.ValType.Int,
                "Number of disk sides in this image.", "", canEdit:false),
            new MetaEntry("boot_sector_format", MetaEntry.ValType.Int,
                "Type of boot sector on this disk (5.25\" only).",
                "0=unknown, 1=16-sector, 2=13-sector, 3=both", canEdit:true),
            new MetaEntry("optimal_bit_timing", MetaEntry.ValType.Int,
                "Ideal disk controller bit rate.", "", canEdit:false),
            new MetaEntry("compatible_hardware", MetaEntry.ValType.Int,
                "Compatible hardware bit field.",
                "0x0001 = Apple ][" + NL +
                "0x0002 = Apple ][ Plus" + NL +
                "0x0004 = Apple //e (unenhanced)" + NL +
                "0x0008 = Apple //c" + NL +
                "0x0010 = Apple //e (enhanced)" + NL +
                "0x0020 = Apple IIgs" + NL +
                "0x0040 = Apple //c Plus" + NL +
                "0x0080 = Apple ///" + NL +
                "0x0100 = Apple /// Plus",
                canEdit:true),
            new MetaEntry("required_ram", MetaEntry.ValType.Int,
                "Minimum RAM required to run this software, in KiB.",
                "Integer value, 0-65535.", canEdit:true),
            new MetaEntry("largest_track", MetaEntry.ValType.Int,
                "Number of blocks used by the largest track.", "", canEdit:false),
        };
        internal static readonly MetaEntry[] sInfo3List = new MetaEntry[] {
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
            WriteProtected = false;
            Synchronized = false;
            Cleaned = true;
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
                            value += " (5.25\")";
                        } else if (DiskType == 2) {
                            value += " (3.5\")";
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
                case "cleaned":
                    value = Cleaned ? TRUE_LOWER : FALSE_LOWER;
                    break;
                case "creator":
                    value = Creator.TrimEnd();  // trim the trailing space padding
                    break;
                case "disk_sides":
                    value = DiskSides.ToString();
                    break;
                case "boot_sector_format":
                    value = BootSectorFormat.ToString();
                    if (verbose) {
                        switch (BootSectorFormat) {
                            case 0:
                                value += " (unknown)";
                                break;
                            case 1:
                                value += " (16-sector)";
                                break;
                            case 2:
                                value += " (13-sector)";
                                break;
                            case 3:
                                value += " (13/16-sector)";
                                break;
                            default:
                                value = " (?)";
                                break;
                        }
                    }
                    break;
                case "optimal_bit_timing":
                    value = OptimalBitTiming.ToString();
                    break;
                case "compatible_hardware":
                    value = CompatibleHardware.ToString();
                    if (verbose) {
                        StringBuilder compatStr = new StringBuilder();
                        ushort chBits = CompatibleHardware;
                        for (int i = 0; i < sWozInfoCompat.Length; i++) {
                            if ((chBits & (1 << i)) != 0) {
                                if (compatStr.Length != 0) {
                                    compatStr.Append('|');
                                }
                                compatStr.Append(sWozInfoCompat[i]);
                            }
                        }
                        value += " (" + compatStr.ToString() + ")";
                    }
                    break;
                case "required_ram":
                    value = RequiredRAM.ToString();
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
                    Debug.WriteLine("Woz info key '" + key + "' not recognized");
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
                case "boot_sector_format":
                    return byte.TryParse(value, out byte bsVal);
                case "compatible_hardware":
                    // We want to allow hex for this one.
                    if (StringToValue.TryParseInt(value, out int val, out int intBase)) {
                        if (val >= 0 && val <= 65535) {
                            return true;
                        }
                    }
                    return false;
                case "required_ram":
                    return ushort.TryParse(value, out ushort rrVal);
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
                case "boot_sector_format":
                    if (!byte.TryParse(value, out byte bsVal)) {
                        throw new ArgumentException("invalid value '" + value + "'");
                    }
                    BootSectorFormat = bsVal;
                    break;
                case "compatible_hardware":
                    if (!StringToValue.TryParseInt(value, out int chVal, out int intBase) ||
                            chVal < 0 || chVal > 65535) {
                        throw new ArgumentException("invalid value '" + value + "'");
                    }
                    CompatibleHardware = (ushort)chVal;
                    break;
                case "required_ram":
                    if (!ushort.TryParse(value, out ushort rrVal)) {
                        throw new ArgumentException("invalid value '" + value + "'");
                    }
                    RequiredRAM = rrVal;
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
        public bool WriteProtected {
            get { return mData[2] != 0; }
            set {
                CheckAccess();
                mData[2] = (byte)(value ? 1 : 0);
                IsDirty = true;
            }
        }

        /// <summary>
        /// Synchronized (v1): 0 = not synchronized, 1 = cross-track sync was used during imaging.
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
        /// Cleaned (v1): 0 = not cleaned, 1 = MC3470 fake bits have been removed.
        /// </summary>
        public bool Cleaned {
            get { return mData[4] != 0; }
            internal set {
                CheckAccess();
                mData[4] = (byte)(value ? 1 : 0);
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
