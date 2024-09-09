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
using System.Text.RegularExpressions;

using AppCommon;
using CommonUtil;
using DiskArc;
using DiskArc.Arc;
using static DiskArc.Defs;
using AttrID = AppCommon.SetAttrWorker.AttrID;
using AttrEdit = AppCommon.SetAttrWorker.AttrEdit;

namespace cp2 {
    internal static class FileAttr {
        /// <summary>
        /// Handles the "get-attr" command.
        /// </summary>
        public static bool HandleGetAttr(string cmdName, string[] args, ParamsBag parms) {
            if (args.Length < 2 || args.Length > 3) {
                CP2Main.ShowUsage(cmdName);
                return false;
            }

            string attrName = args.Length > 2 ? args[2] : string.Empty;

            // Volume directories have dates, ZIP archives have top-level comments, so it's
            // useful to be able to reference the top.
            bool isVolumeRef = false;
            string targetPath = args[1];
            if (targetPath == "/" || targetPath == ":") {
                isVolumeRef = true;
            }

            // Allow slash or colon as separators.  Ignore case.
            List<Glob> globList;
            try {
                globList = Glob.GenerateGlobSet(new string[] { targetPath },
                    Glob.STD_DIR_SEP_CHARS, true);
            } catch (ArgumentException ex) {
                Console.Error.WriteLine("Error: " + ex.Message);
                return false;
            }

            if (!ExtArchive.OpenExtArc(args[0], true, true, parms, out DiskArcNode? rootNode,
                    out DiskArcNode? leafNode, out object? leaf, out IFileEntry endDirEntry)) {
                return false;
            }
            using (rootNode) {
                if (leaf is IArchive) {
                    IArchive arc = (IArchive)leaf;
                    if (!Misc.StdChecks(arc, needWrite: false, needMulti: false)) {
                        return false;
                    }

                    IFileEntry entry;
                    if (isVolumeRef) {
                        // NuFX has dates in the header, but they're not currently exposed (and
                        // frankly they're not all that interesting).  This could be used to
                        // access the archive comment in a ZIP file.
                        Console.Error.WriteLine("Operation not supported for file archive");
                        return false;
                    } else {
                        // Do the wildcard match so they can reference single files with wildcards,
                        // e.g. use '?' for inconvenient characters.
                        List<IFileEntry> entries =
                            FindEntries.FindMatchingEntries(arc, globList, parms.Recurse);
                        if (!Misc.CheckGlobList(globList)) {
                            return false;
                        }
                        if (entries.Count != 1) {
                            Console.Error.WriteLine(
                                "Error: can only get attributes on a single file");
                            return false;
                        }
                        entry = entries[0];
                    }

                    if (parms.MacZip && arc is Zip &&
                            Zip.HasMacZipHeader(arc, entry, out IFileEntry adfEntry)) {
                        try {
                            if (!HandleMacZip(arc, adfEntry, attrName, parms)) {
                                return false;
                            }
                        } catch (Exception ex) {
                            Console.Error.WriteLine("Error: " + ex.Message);
                            return false;
                        }
                    } else {
                        PrintAttrs(entry, attrName, parms);
                    }
                } else {
                    IFileSystem? fs = Misc.GetTopFileSystem(leaf);
                    if (!Misc.StdChecks(fs, needWrite: false, parms.FastScan)) {
                        return false;
                    }
                    IFileEntry entry;
                    if (isVolumeRef) {
                        entry = fs.GetVolDirEntry();
                    } else {
                        // Do the match, with recursion disabled.  If they specify a directory,
                        // we want to move that directory as a unit.
                        List<IFileEntry> entries =
                        FindEntries.FindMatchingEntries(fs, endDirEntry, globList, false);
                        if (!Misc.CheckGlobList(globList)) {
                            return false;
                        }
                        if (entries.Count != 1) {
                            Console.Error.WriteLine(
                                "Error: can only set attributes on a single file");
                            return false;
                        }
                        entry = entries[0];
                    }
                    PrintAttrs(entry, attrName, parms);
                }
            }

            return true;
        }

        /// <summary>
        /// Prints attributes for a MacZip entry.
        /// </summary>
        private static bool HandleMacZip(IArchive arc, IFileEntry adfEntry,
                string attrName, ParamsBag parms) {
            // Extract the AppleSingle file, and display or update the attributes.
            using (Stream adfStream = ArcTemp.ExtractToTemp(arc, adfEntry, FilePart.DataFork)) {
                using (IArchive adfArchive = AppleSingle.OpenArchive(adfStream, parms.AppHook)) {
                    IFileEntry adfArchiveEntry = adfArchive.GetFirstEntry();
                    PrintAttrs(adfArchiveEntry, attrName, parms);
                }
            }
            return true;
        }

        /// <summary>
        /// Prints the entry's attributes.
        /// </summary>
        private static void PrintAttrs(IFileEntry entry, string attrName, ParamsBag parms) {
            const string LFMT = "  {0,-12}: ";

            if (attrName == string.Empty) {
                Console.WriteLine("Attributes for '" + entry.FullPathName + "':");
                string fileKindStr = entry.IsDirectory ? "directory" : "plain file";
                if (entry.IsDubious) {
                    fileKindStr += " [dubious]";
                }
                if (entry.IsDamaged) {
                    fileKindStr += " [DAMAGED]";
                }
                Console.WriteLine(string.Format(LFMT + "{1}",
                    "File Kind", fileKindStr));
                Console.WriteLine(string.Format(LFMT + "{1,-3} 0x{2:x2}",
                    "File Type", FileTypes.GetFileTypeAbbrev(entry.FileType), entry.FileType));
                Console.WriteLine(string.Format(LFMT + "0x{1:x4}",
                    "Aux Type", entry.AuxType));
                Console.WriteLine(string.Format(LFMT + "'{1,4}' 0x{2:x8}",
                    "HFS Type", MacChar.StringifyMacConstant(entry.HFSFileType), entry.HFSFileType));
                Console.WriteLine(string.Format(LFMT + "'{1,4}' 0x{2:x8}",
                    "HFS Creator", MacChar.StringifyMacConstant(entry.HFSCreator), entry.HFSCreator));
                Console.WriteLine(string.Format(LFMT + "0x{1:x2} [{2}]",
                    "Access", entry.Access, Catalog.FormatAccessFlags(entry.Access)));
                Console.WriteLine(string.Format(LFMT + "{1}",
                    "Create Date", FormatDate(entry.CreateWhen)));
                Console.WriteLine(string.Format(LFMT + "{1}",
                    "Mod Date", FormatDate(entry.ModWhen)));
                // TODO: remap ctrl / MOR chars?
                Console.WriteLine(string.Format(LFMT + "{1}",
                    "Comment", string.IsNullOrEmpty(entry.Comment) ?
                        "(none)" : "\"" + entry.Comment + "\""));
            } else {
                AttrID id = ParseAttrName(attrName);
                switch (id) {
                    case AttrID.Type:
                        Console.WriteLine("0x" + entry.FileType.ToString("x2"));
                        break;
                    case AttrID.AuxType:
                        Console.WriteLine("0x" + entry.AuxType.ToString("x4"));
                        break;
                    case AttrID.HFSFileType:
                        Console.WriteLine("0x" + entry.HFSFileType.ToString("x8"));
                        break;
                    case AttrID.HFSCreator:
                        Console.WriteLine("0x" + entry.HFSCreator.ToString("x8"));
                        break;
                    case AttrID.Access:
                        Console.WriteLine("0x" + entry.Access.ToString("x2"));
                        break;
                    case AttrID.CreateWhen:
                        Console.WriteLine(FormatDate(entry.CreateWhen));
                        break;
                    case AttrID.ModWhen:
                        Console.WriteLine(FormatDate(entry.ModWhen));
                        break;
                }
            }
        }

        private static string FormatDate(DateTime when) {
            if (!TimeStamp.IsValidDate(when)) {
                return "[No Date]";
            } else {
                return when.ToString("dd-MMM-yyyy HH:mm:ss");   // add K for timezone
            }
        }


        /// <summary>
        /// Handles the "set-attr" command.
        /// </summary>
        public static bool HandleSetAttr(string cmdName, string[] args, ParamsBag parms) {
            if (args.Length < 2) {
                CP2Main.ShowUsage(cmdName);
                return false;
            }

            List<AttrEdit> editList;
            if (!ParseAttrEdits(args[1], out editList)) {
                return false;
            }

            string[] paths = new string[args.Length - 2];
            for (int i = 2; i < args.Length; i++) {
                paths[i - 2] = args[i];
            }
            bool isVolumeRef = false;
            if (paths.Length == 1 && (paths[0] == "/" || paths[0] == ":")) {
                isVolumeRef = true;
            }

            // Allow slash or colon as separators.  Ignore case.
            List<Glob> globList;
            try {
                globList = Glob.GenerateGlobSet(paths, Glob.STD_DIR_SEP_CHARS, true);
            } catch (ArgumentException ex) {
                Console.Error.WriteLine("Error: " + ex.Message);
                return false;
            }

            SetAttrWorker.CallbackFunc cbFunc = delegate (CallbackFacts what) {
                return Misc.HandleCallback(what, "setting attr", parms);
            };
            SetAttrWorker worker = new SetAttrWorker(cbFunc, macZip: parms.MacZip,
                parms.AppHook);
            Console.CancelKeyPress += new ConsoleCancelEventHandler(Misc.SignalHandler);

            if (!ExtArchive.OpenExtArc(args[0], true, false, parms, out DiskArcNode? rootNode,
                    out DiskArcNode? leafNode, out object? leaf, out IFileEntry endDirEntry)) {
                return false;
            }
            using (rootNode) {
                if (leaf is IArchive) {
                    IArchive arc = (IArchive)leaf;
                    if (!Misc.StdChecks(arc, needWrite: true, needMulti: false)) {
                        return false;
                    }

                    if (isVolumeRef) {
                        // NuFX has dates in the header, but they're not currently exposed (and
                        // frankly they're not all that interesting).  This could be used to
                        // access the archive comment in a ZIP file.
                        Console.Error.WriteLine("Operation not supported for file archive");
                        return false;
                    }

                    List<IFileEntry> entries =
                        FindEntries.FindMatchingEntries(arc, globList, parms.Recurse);
                    if (!Misc.CheckGlobList(globList)) {
                        return false;
                    }
                    if (globList.Count == 0) {
                        // Should not be possible.
                        Console.Error.WriteLine("Must specify files");
                        return false;
                    }
                    if (entries.Count == 0) {
                        // Should not be possible if globList matched anything.
                        Console.Error.WriteLine("Weird: nothing found");
                        return false;
                    }
                    try {
                        //arc.StartTransaction();
                        if (!worker.SetAttrInArchive(arc, entries, editList,
                                out bool isCancelled)) {
                            if (isCancelled) {
                                Console.Error.WriteLine("Cancelled.");
                            }
                            return false;
                        }
                        Debug.Assert(!isCancelled);
                        leafNode.SaveUpdates(parms.Compress);
                    } catch (Exception ex) {
                        Console.Error.WriteLine("Error: " + ex.Message);
                        return false;
                    } finally {
                        arc.CancelTransaction();    // no effect if transaction isn't open
                    }

                    // This is wrong for MacZip.
                    //if (parms.Verbose && entries.Count == 1) {
                    //    PrintAttrs(entries[0], string.Empty, parms);
                    //}
                } else {
                    IFileSystem? fs = Misc.GetTopFileSystem(leaf);
                    if (!Misc.StdChecks(fs, needWrite: true, parms.FastScan)) {
                        return false;
                    }
                    List<IFileEntry> entries;
                    if (isVolumeRef) {
                        // TODO: would be better to handle this in FindMatchingEntries, but
                        //   we'd need to go back and make sure it doesn't mess up other things.
                        entries = new List<IFileEntry>() { fs.GetVolDirEntry() };
                    } else {
                        entries = FindEntries.FindMatchingEntries(fs, endDirEntry, globList,
                            parms.Recurse);
                        if (!Misc.CheckGlobList(globList)) {
                            return false;
                        }
                        if (globList.Count == 0) {
                            // Should not be possible.
                            Console.Error.WriteLine("Error: must specify files");
                            return false;
                        }
                        if (entries.Count == 0) {
                            // Should not be possible if globList matched anything.
                            Console.Error.WriteLine("Weird: nothing found");
                            return false;
                        }
                    }
                    bool success =
                        worker.SetAttrInDisk(fs, entries, editList, out bool isCancelled);
                    try {
                        leafNode.SaveUpdates(parms.Compress);
                    } catch (Exception ex) {
                        Console.Error.WriteLine("Error: update failed: " + ex.Message);
                        return false;
                    }
                    if (!success) {
                        return false;
                    }

                    //if (parms.Verbose && entries.Count == 1) {
                    //    PrintAttrs(entries[0], string.Empty, parms);
                    //}
                }
            }

            return true;
        }

        private const string ARG_TYPE = "type";
        private const string ARG_AUX = "aux";
        private const string ARG_HFSTYPE = "hfstype";
        private const string ARG_CREATOR = "creator";
        private const string ARG_ACCESS = "access";
        private const string ARG_CDATE = "cdate";
        private const string ARG_MDATE = "mdate";

        /// <summary>
        /// Converts an attribute name string to an attribute ID.
        /// </summary>
        private static AttrID ParseAttrName(string nameStr) {
            switch (nameStr.ToLower()) {
                case ARG_TYPE:
                    return AttrID.Type;
                case ARG_AUX:
                    return AttrID.AuxType;
                case ARG_HFSTYPE:
                    return AttrID.HFSFileType;
                case ARG_CREATOR:
                    return AttrID.HFSCreator;
                case ARG_ACCESS:
                    return AttrID.Access;
                case ARG_CDATE:
                    return AttrID.CreateWhen;
                case ARG_MDATE:
                    return AttrID.ModWhen;
                default:
                    Console.Error.WriteLine("Error: unknown attribute '" + nameStr + "'");
                    Console.Error.WriteLine(
                        " (try: type, aux, hfstype, creator, access, cdate, mdate)");
                    return AttrID.Unknown;
            }
        }

        // Pattern to use when parsing "name=value" patterns.  This will match into two groups:
        // 1. Name: one or more letters
        // 2. Value: one or more characters; anything is allowed, including whitespace
        private static readonly Regex sNameValueRegex = new Regex(NAME_VALUE_PATTERN);
        private const string NAME_VALUE_PATTERN = @"^([A-Za-z]+)=(.+)$";

        /// <summary>
        /// Parses the name/value attribute edit arguments.
        /// </summary>
        /// <param name="arg">List of comma-separated edit arguments.</param>
        /// <param name="editList">Result: list of edit instructions.</param>
        /// <returns>True if parsing was successful.</returns>
        private static bool ParseAttrEdits(string arg, out List<AttrEdit> editList) {
            editList = new List<AttrEdit>();
            string[] args = arg.Split(',');

            for (int i = 0; i < args.Length; i++) {
                MatchCollection matches = sNameValueRegex.Matches(args[i]);
                if (matches.Count != 1) {
                    Console.Error.WriteLine("Error: bad attribute '" + args[i] + "'");
                    return false;
                }
                string nameStr = matches[0].Groups[1].Value;
                string valueStr = matches[0].Groups[2].Value;

                AttrID id = ParseAttrName(nameStr);
                object? value = null;
                long lconv;
                switch (id) {
                    case AttrID.Type:
                        int proType = FileTypes.GetFileTypeByAbbrev(valueStr);
                        if (proType >= 0) {
                            value = (byte)proType;
                        } else {
                            lconv = ConvertHex(valueStr, 2);
                            if (lconv >= 0) {
                                value = (byte)lconv;
                            }
                        }
                        break;
                    case AttrID.AuxType:
                        lconv = ConvertHex(valueStr, 4);
                        if (lconv >= 0) {
                            value = (ushort)lconv;
                        }
                        break;
                    case AttrID.HFSFileType:
                        if (valueStr.Length == 4) {
                            value = MacChar.IntifyMacConstantString(valueStr);
                        } else {
                            lconv = ConvertHex(valueStr, 8);
                            if (lconv >= 0) {
                                value = (uint)lconv;
                            }
                        }
                        break;
                    case AttrID.HFSCreator:
                        if (valueStr.Length == 4) {
                            value = MacChar.IntifyMacConstantString(valueStr);
                        } else {
                            lconv = ConvertHex(valueStr, 8);
                            if (lconv >= 0) {
                                value = (uint)lconv;
                            }
                        }
                        break;
                    case AttrID.Access:
                        if (valueStr.ToLower() == "locked") {
                            value = FileAttribs.FILE_ACCESS_LOCKED;
                        } else if (valueStr.ToLower() == "unlocked") {
                            value = FileAttribs.FILE_ACCESS_UNLOCKED;
                        } else {
                            lconv = ConvertHex(valueStr, 2);
                            if (lconv >= 0) {
                                value = (byte)lconv;
                            }
                        }
                        break;
                    case AttrID.CreateWhen:
                        if (DateTime.TryParse(valueStr, out DateTime cwhen)) {
                            value = cwhen;
                        }
                        break;
                    case AttrID.ModWhen:
                        if (DateTime.TryParse(valueStr, out DateTime mwhen)) {
                            value = mwhen;
                        }
                        break;
                    case AttrID.Unknown:
                        return false;
                    default:
                        Debug.Assert(false, "missing case for " + id);
                        return false;
                }

                if (value == null) {
                    Console.Error.WriteLine("Error: invalid value: " + args[i]);
                    return false;
                }
                AttrEdit editItem = new AttrEdit(id, value);
                editList.Add(editItem);
            }

            return true;
        }

        /// <summary>
        /// Converts a fixed-length hexadecimal string to an integer.  The string may be prefixed
        /// with "$" or "0x".
        /// </summary>
        /// <param name="str">String to convert.</param>
        /// <param name="numDigits">Required number of digits.</param>
        /// <returns>Converted integer, or -1 if conversion fails.</returns>
        private static long ConvertHex(string str, int numDigits) {
            if (str.Length < 2) {
                return -1;
            }
            if (str[0] == '$') {
                str = str.Substring(1);
            } else if (str[0] == '0' && (str[1] == 'x' || str[1] == 'X')) {
                str = str.Substring(2);
            } else {
                // We have two choices: accept a "naked" hex string, or reject it.  The former
                // is convenient, but if we ever want to accept decimal strings we can't do it.
                return -1;
            }
            if (str.Length != numDigits) {
                return -1;
            }
            try {
                return Convert.ToInt64(str, 16);
            } catch (Exception ex) {
                Debug.WriteLine("Convert failed: " + ex.Message);
                return -1;
            }
        }
    }
}
