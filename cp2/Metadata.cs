/*
 * Copyright 2023 faddenSoft
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

using AppCommon;
using CommonUtil;
using DiskArc;
using DiskArc.Disk;
using DiskArc.Multi;
using static DiskArc.Defs;

// TODO: there's too much type-specific code here.  2IMG and WOZ (and anything else that
//   comes along) need to present a list of keys and work with strings, similar to the way
//   that Woz_Meta currently works.  We can keep the properties like WriteProtected exposed
//   as booleans; the goal is just to present an alternative interface that apps can use
//   for general get/set interfaces.  Classes can implement IMetadata to advertise.  For
//   each field, static parameters: key-name, can-change, can-delete.  Parsing and syntax
//   checking is done per-key by the class, with useful exception message thrown on error.

namespace cp2 {
    internal static class Metadata {
        /// <summary>
        /// Handles the "get-metadata" command.
        /// </summary>
        public static bool HandleGetMetadata(string cmdName, string[] args, ParamsBag parms) {
            if (args.Length < 1 || args.Length > 2) {
                CP2Main.ShowUsage(cmdName);
                return false;
            }

            string fieldName = args.Length > 1 ? args[1] : string.Empty;

            if (!ExtArchive.OpenExtArc(args[0], false, true, parms, out DiskArcNode? rootNode,
                    out DiskArcNode? leafNode, out object? leaf, out IFileEntry endDirEntry)) {
                return false;
            }
            using (rootNode) {
                if (leaf is IArchive) {
                    IArchive arc = (IArchive)leaf;
                    if (!Misc.StdChecks(arc, needWrite: false, needMulti: false)) {
                        return false;
                    }

                    // Currently no supported file archive formats.
                    Console.Error.WriteLine("No metadata for this file archive format");
                    return false;
                } else if (leaf is IDiskImage) {
                    if (!Misc.StdChecks((IDiskImage)leaf, needWrite: false)) {
                        return false;
                    }
                    if (leaf is Woz) {
                        if (string.IsNullOrEmpty(fieldName)) {
                            return PrintAllWoz((Woz)leaf, parms);
                        } else {
                            return PrintWoz((Woz)leaf, fieldName, parms);
                        }
                    } else if (leaf is TwoIMG) {
                        if (string.IsNullOrEmpty(fieldName)) {
                            return PrintAllTwoIMG((TwoIMG)leaf, parms);
                        } else {
                            return PrintTwoIMG((TwoIMG)leaf, fieldName, parms);
                        }
                    } else {
                        Console.Error.WriteLine("No metadata for this disk image format");
                        return false;
                    }
                } else {
                    Debug.Assert(leaf is Partition);
                    // Currently no supported partition formats.
                    Console.Error.WriteLine("No metadata is supported for this format");
                    return false;
                }
            }
        }

        /// <summary>
        /// Handles the "set-metadata" command.
        /// </summary>
        public static bool HandleSetMetadata(string cmdName, string[] args, ParamsBag parms) {
            if (args.Length < 2 || args.Length > 3) {
                CP2Main.ShowUsage(cmdName);
                return false;
            }

            string fieldName = args[1];
            string? newValue = args.Length > 2 ? args[2] : null;

            if (!ExtArchive.OpenExtArc(args[0], false, false, parms, out DiskArcNode? rootNode,
                    out DiskArcNode? leafNode, out object? leaf, out IFileEntry endDirEntry)) {
                return false;
            }
            using (rootNode) {
                if (leaf is IArchive) {
                    IArchive arc = (IArchive)leaf;
                    if (!Misc.StdChecks(arc, needWrite: true, needMulti: false)) {
                        return false;
                    }

                    // Currently no supported file archive formats.
                    Console.Error.WriteLine("No metadata for this file archive format");
                } else if (leaf is IDiskImage) {
                    if (!Misc.StdChecks((IDiskImage)leaf, needWrite: true)) {
                        return false;
                    }
                    bool success;
                    if (leaf is Woz) {
                        if (newValue == null) {
                            success = DeleteWoz((Woz)leaf, fieldName, parms);
                        } else {
                            success = SetWoz((Woz)leaf, fieldName, newValue, parms);
                        }
                    } else if (leaf is TwoIMG) {
                        if (newValue == null) {
                            success = DeleteTwoIMG((TwoIMG)leaf, fieldName, parms);
                        } else {
                            success = SetTwoIMG((TwoIMG)leaf, fieldName, newValue, parms);
                        }
                    } else {
                        Console.Error.WriteLine("No metadata for this disk image format");
                        return false;
                    }

                    try {
                        leafNode.SaveUpdates(parms.Compress);
                    } catch (Exception ex) {
                        Console.Error.WriteLine("Error: update failed: " + ex.Message);
                        return false;
                    }
                    if (!success) {
                        return false;
                    }
                } else {
                    Debug.Assert(leaf is Partition);
                    // Currently no supported partition formats.
                    Console.Error.WriteLine("No metadata is supported for this format");
                }
            }
            return true;
        }

        #region TwoIMG

        private static readonly string[] sTwoIMGList = new string[] {
            "comment",
            "creator",
            "format",
            "write_protected",
            "volume_number",
        };

        /// <summary>
        /// Prints all meta-data values, as name=value pairs.
        /// </summary>
        private static bool PrintAllTwoIMG(TwoIMG image, ParamsBag parms) {
            foreach (string key in sTwoIMGList) {
                string? value = GetTwoIMGValue(image, key);
                if (value != null) {
                    Console.WriteLine(key + "=" + value);
                } else {
                    Debug.Assert(false);
                }
            }
            return true;
        }

        /// <summary>
        /// Prints the value of the requested key.
        /// </summary>
        private static bool PrintTwoIMG(TwoIMG image, string keyName, ParamsBag parms) {
            string? value = GetTwoIMGValue(image, keyName);
            if (value != null) {
                Console.WriteLine(value);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Obtains the value of a metadata key from the disk image.
        /// </summary>
        private static string? GetTwoIMGValue(TwoIMG image, string fieldName) {
            string? value = null;
            switch (fieldName) {
                case "comment":
                    value = image.Comment;      // may be multi-line
                    break;
                case "creator":
                    value = image.Creator;
                    break;
                case "format":
                    value = image.ImageFormat.ToString();
                    break;
                case "write_protected":
                    value = image.WriteProtected.ToString();
                    break; ;
                case "volume_number":
                    int volNum = image.VolumeNumber;
                    if (volNum < 0) {
                        value = "(not set)";
                    } else {
                        value = volNum.ToString();
                    }
                    break;
                default:
                    Console.Error.WriteLine("Field " + fieldName + " not found");
                    break;
            }
            return value;
        }

        /// <summary>
        /// Deletes a key, if possible.
        /// </summary>
        private static bool DeleteTwoIMG(TwoIMG image, string fieldName, ParamsBag parms) {
            switch (fieldName) {
                case "comment":
                    image.Comment = string.Empty;
                    return true;
                case "volume_number":
                    image.VolumeNumber = -1;
                    return true;
                default:
                    Console.Error.WriteLine("Field " + fieldName + " cannot be deleted");
                    return false;
            }
        }

        /// <summary>
        /// Sets the value of a key, if possible.
        /// </summary>
        private static bool SetTwoIMG(TwoIMG image, string fieldName, string newValue,
                ParamsBag parms) {
            try {
                switch (fieldName) {
                    case "comment":
                        image.Comment = newValue;
                        return true;
                    case "write_protected":
                        if (bool.TryParse(newValue, out bool boolVal)) {
                            image.WriteProtected = boolVal;
                            return true;
                        } else {
                            Console.Error.WriteLine("Error: invalid value");
                            return false;
                        }
                    case "volume_number":
                        if (int.TryParse(newValue, out int intVal)) {
                            image.VolumeNumber = intVal;
                            return true;
                        } else {
                            Console.Error.WriteLine("Error: invalid value");
                            return false;
                        }
                    default:
                        Console.Error.WriteLine("Error: unknown field '" + fieldName + "'");
                        return false;
                }
            } catch (ArgumentException ex) {
                Console.Error.WriteLine("Error: " + ex.Message);
                return false;
            }
        }

        #endregion TwoIMG

        #region Woz

        private static readonly string[] sWozInfoList = new string[] {
            "version",
            "disk_type",
            "write_protected",
            "synchronized",
            "cleaned",
            "creator",
            "disk_sides",
            "boot_sector_format",
            "optimal_bit_timing",
            "compatible_hardware",
            "required_ram",
        };

        /// <summary>
        /// Prints all meta-data values, as name=value pairs.
        /// </summary>
        private static bool PrintAllWoz(Woz image, ParamsBag parms) {
            foreach (string key in sWozInfoList) {
                string? value = GetWozInfo(image, key);
                if (value != null) {
                    Console.WriteLine("info:" + key + "=" + value);
                } else {
                    Debug.Assert(false);
                }
            }
            if (image.Metadata != null) {
                foreach (string key in image.Metadata) {
                    string? value = GetWozMeta(image, key);
                    if (value != null) {
                        Console.WriteLine("meta:" + key + "=" + value);
                    }
                }
            }
            return true;
        }

        /// <summary>
        /// Prints the value of the requested key.
        /// </summary>
        private static bool PrintWoz(Woz image, string keyName, ParamsBag parms) {
            if (keyName.StartsWith("info:")) {
                string? value = GetWozInfo(image, keyName.Substring(5));
                if (value != null) {
                    Console.WriteLine(value);
                    return true;
                }
            } else if (keyName.StartsWith("meta:")) {
                string? value = GetWozMeta(image, keyName.Substring(5));
                if (value != null) {
                    Console.WriteLine(value);
                    return true;
                }
            } else {
                Console.Error.WriteLine("Error: unknown prefix for key '" + keyName + "'");
            }
            return false;
        }

        private static string? GetWozMeta(Woz image, string fieldName) {
            Woz_Meta? meta = image.Metadata;
            if (meta == null) {
                Console.Error.WriteLine("Error: disk image has no META section");
                return null;
            }
            string? value = meta.GetValue(fieldName);
            if (value == null) {
                Console.Error.WriteLine("Error: field not found");
                return null;
            }
            return value;
        }

        private static string? GetWozInfo(Woz image, string fieldName) {
            Woz_Info info = image.Info;
            string? value = null;
            switch (fieldName) {
                case "version":
                    value = info.Version.ToString();
                    break;
                case "disk_type":
                    if (info.DiskType == 1) {
                        value = "5.25";
                    } else if (info.DiskType == 2) {
                        value = "3.5";
                    } else {
                        value = "???";
                    }
                    break;
                case "write_protected":
                    if (info.WriteProtected == 0) {
                        value = "false";
                    } else {
                        value = "true";
                    }
                    break;
                case "synchronized":
                    if (info.Synchronized == 0) {
                        value = "false";
                    } else {
                        value = "true";
                    }
                    break;
                case "cleaned":
                    if (info.Cleaned == 0) {
                        value = "false";
                    } else {
                        value = "true";
                    }
                    break;
                case "creator":
                    value = info.Creator;
                    break;
                case "disk_sides":
                    value = info.DiskSides.ToString();
                    break;
                case "boot_sector_format":
                    switch (info.BootSectorFormat) {
                        case 0:
                            value = "unknown";
                            break;
                        case 1:
                            value = "16-sector";
                            break;
                        case 2:
                            value = "13-sector";
                            break;
                        case 3:
                            value = "13/16-sector";
                            break;
                        default:
                            value = "???";
                            break;
                    }
                    break;
                case "optimal_bit_timing":
                    value = info.OptimalBitTiming.ToString();
                    break;
                case "compatible_hardware":
                    StringBuilder compatStr = new StringBuilder();
                    ushort chBits = info.CompatibleHardware;
                    for (int i = 0; i < sWozInfoCompat.Length; i++) {
                        if ((chBits & (1 << i)) != 0) {
                            if (compatStr.Length != 0) {
                                compatStr.Append('|');
                            }
                            compatStr.Append(sWozInfoCompat[i]);
                        }
                    }
                    value = info.CompatibleHardware.ToString() + " (" + compatStr.ToString() + ")";
                    break;
                case "required_ram":
                    value = info.RequiredRAM.ToString();
                    break;
                default:
                    Console.Error.WriteLine("Field " + fieldName + " not found");
                    break;
            }
            return value;
        }

        private static readonly string[] sWozInfoCompat = new string[] {
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

        /// <summary>
        /// Deletes a key, if possible.
        /// </summary>
        private static bool DeleteWoz(Woz image, string fieldName, ParamsBag parms) {
            if (!fieldName.StartsWith("meta:")) {
                Console.Error.WriteLine("Error: only 'meta:' fields may be deleted");
                return false;
            }
            if (image.Metadata == null) {
                Console.Error.WriteLine("Error: disk image has no META section");
                return false;
            }
            try {
                if (!image.Metadata.DeleteValue(fieldName.Substring(5))) {
                    Console.Error.WriteLine("Error: field not found");
                    return false;
                }
            } catch (ArgumentException) {
                Console.Error.WriteLine("Error: standard fields may not be deleted");
                return false;
            }
            if (parms.Verbose) {
                Console.WriteLine("Field deleted");
            }
            return true;
        }

        /// <summary>
        /// Sets the value of a key, if possible.
        /// </summary>
        private static bool SetWoz(Woz image, string fieldName, string newValue, ParamsBag parms) {
            if (fieldName.StartsWith("info:")) {
                return SetWozInfo(image, fieldName, newValue, parms);
            } else if (fieldName.StartsWith("meta:")) {
                if (image.Metadata == null) {
                    Console.WriteLine("Disk image has no META section; adding one");
                    image.AddMETA();
                    Debug.Assert(image.Metadata != null);
                }
                string subField = fieldName.Substring(5);
                try {
                    image.Metadata.SetValue(subField, newValue);
                } catch (ArgumentException ex) {
                    Console.Error.WriteLine("Error: " + ex.Message);
                    return false;
                }
                if (parms.Verbose) {
                    Console.WriteLine("Set " + fieldName + "=" + image.Metadata.GetValue(subField));
                }
            } else {
                Console.Error.WriteLine("Error: unknown field '" + fieldName + "'");
                return false;
            }
            return true;
        }

        private static bool SetWozInfo(Woz image, string fieldName, string newValue,
                ParamsBag parms) {
            string subField = fieldName.Substring(5);
            if (!sWozInfoList.Contains(subField)) {
                Console.Error.WriteLine("Error: unknown INFO key '" + subField + "'");
            }

            try {
                switch (subField) {
                    case "write_protected":
                        if (bool.TryParse(newValue, out bool boolVal)) {
                            image.Info.WriteProtected = boolVal ? (byte)1 : (byte)0;
                        } else {
                            Console.Error.WriteLine("Error: invalid value");
                            return false;
                        }
                        break;
                    case "compatible_hardware":
                        if (!ushort.TryParse(newValue, out ushort chVal)) {
                            Console.Error.WriteLine("Error: invalid value");
                            return false;
                        }
                        image.Info.CompatibleHardware = chVal;
                        break;
                    case "required_ram":
                        if (!ushort.TryParse(newValue, out ushort rrVal)) {
                            Console.Error.WriteLine("Error: invalid value");
                            return false;
                        }
                        image.Info.RequiredRAM = rrVal;
                        break;
                    default:
                        Console.Error.WriteLine("Error: field '" + subField +
                            "' cannot be modified");
                        return false;
                }
            } catch (ArgumentException) {
                Console.Error.WriteLine("Error: invalid value for '" + subField + "'");
                return false;
            }
            if (parms.Verbose) {
                Console.WriteLine("Set " + fieldName + "=" + GetWozInfo(image, subField));
            }
            return true;
        }

        #endregion Woz
    }
}
