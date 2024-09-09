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

using AppCommon;
using CommonUtil;
using DiskArc;
using DiskArc.Multi;
using static DiskArc.Defs;
using static DiskArc.IMetadata;

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

            string keyName = args.Length > 1 ? args[1] : string.Empty;

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
                    if (leaf is IMetadata) {
                        if (string.IsNullOrEmpty(keyName)) {
                            return PrintAllMeta((IMetadata)leaf, parms);
                        } else {
                            return PrintMetaEntry((IMetadata)leaf, keyName, parms);
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

            string keyName = args[1];
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
                    if (leaf is IMetadata) {
                        if (newValue == null) {
                            success = DeleteMetaEntry((IMetadata)leaf, keyName, parms);
                        } else {
                            success = SetMetaValue((IMetadata)leaf, keyName, newValue, parms);
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

        /// <summary>
        /// Prints all metadata keys.
        /// </summary>
        private static bool PrintAllMeta(IMetadata mdo, ParamsBag parms) {
            List<MetaEntry> entries = mdo.GetMetaEntries();
            foreach (MetaEntry met in entries) {
                string? value = mdo.GetMetaValue(met.Key, parms.Verbose);
                if (value == null) {
                    // Shouldn't be possible.
                    value = "!NOT FOUND!";
                }
                Console.WriteLine(met.Key + "=" + value);
            }
            return true;
        }

        /// <summary>
        /// Prints the value of one metadata entry.
        /// </summary>
        public static bool PrintMetaEntry(IMetadata mdo, string key, ParamsBag parms) {
            string? value = mdo.GetMetaValue(key, parms.Verbose);
            if (value == null) {
                Console.Error.WriteLine("Error: key " + key + " not found");
                return false;
            }
            Console.WriteLine(value);
            return true;
        }

        /// <summary>
        /// Sets the value of a metadata entry.
        /// </summary>
        private static bool SetMetaValue(IMetadata mdo, string key, string newValue,
                ParamsBag parms) {
            try {
                mdo.SetMetaValue(key, newValue);
            } catch (ArgumentException ex) {
                Console.Error.WriteLine("Error: " + ex.Message);
                return false;
            } catch (InvalidOperationException ex) {
                Console.Error.WriteLine("Error: " + ex.Message);
                return false;
            }
            return true;
        }

        private static bool DeleteMetaEntry(IMetadata mdo, string key, ParamsBag parms) {
            if (!mdo.DeleteMetaEntry(key)) {
                Console.Error.WriteLine("Error: key '" + key + "' cannot be deleted");
                return false;
            }
            return true;
        }
    }
}
