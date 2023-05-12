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

namespace cp2 {
    internal static class FileAttr {
        /// <summary>
        /// Handles the "set-attr" command.
        /// </summary>
        public static bool HandleSetAttr(string cmdName, string[] args, ParamsBag parms) {
            if (args.Length < 2) {
                CP2Main.ShowUsage(cmdName);
                return false;
            }

            // Volume directories have dates, ZIP archives have top-level comments, so it's
            // useful to be able to reference the top.  Currently not needed though.
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

            List<AttrEdit> editList;
            if (!ParseAttrEdits(args, 2, out editList)) {
                return false;
            }
            bool doViewOnly = (editList.Count == 0);

            if (!ExtArchive.OpenExtArc(args[0], true, doViewOnly, parms, out DiskArcNode? rootNode,
                    out DiskArcNode? leafNode, out object? leaf, out IFileEntry endDirEntry)) {
                return false;
            }
            using (rootNode) {
                if (leaf is IArchive) {
                    IArchive arc = (IArchive)leaf;
                    if (!Misc.StdChecks(arc, needWrite: !doViewOnly, needMulti: false)) {
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
                                "Error: can only set attributes on a single file");
                            return false;
                        }
                        entry = entries[0];
                    }

                    if (parms.MacZip && arc is Zip &&
                            HasMacZipHeader(arc, entry, out IFileEntry adfEntry)) {
                        try {
                            if (!HandleMacZip(arc, adfEntry, editList, doViewOnly, parms,
                                    out IFileEntry adfArchiveEntry)) {
                                return false;
                            }
                            if (!doViewOnly) {
                                leafNode.SaveUpdates(parms.Compress);
                                if (parms.Verbose) {
                                    PrintAttrs(adfArchiveEntry, parms);
                                }
                            }
                        } catch (Exception ex) {
                            Console.Error.WriteLine("Error: " + ex.Message);
                            return false;
                        } finally {
                            arc.CancelTransaction();    // no effect if transaction isn't open
                        }
                    } else {
                        if (doViewOnly) {
                            PrintAttrs(entry, parms);
                        } else {
                            try {
                                arc.StartTransaction();
                                if (!SetAttrs(entry, editList, parms)) {
                                    return false;
                                }

                                leafNode.SaveUpdates(parms.Compress);
                                if (parms.Verbose) {
                                    PrintAttrs(entry, parms);
                                }
                            } catch (Exception ex) {
                                Console.Error.WriteLine("Error: " + ex.Message);
                                return false;
                            } finally {
                                arc.CancelTransaction();    // no effect if transaction isn't open
                            }
                        }
                    }
                } else {
                    IFileSystem? fs = Misc.GetTopFileSystem(leaf);
                    if (!Misc.StdChecks(fs, needWrite: !doViewOnly, parms.FastScan)) {
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
                    if (doViewOnly) {
                        PrintAttrs(entry, parms);
                    } else {
                        bool success = SetAttrs(entry, editList, parms);
                        try {
                            leafNode.SaveUpdates(parms.Compress);
                        } catch (Exception ex) {
                            Console.Error.WriteLine("Error: update failed: " + ex.Message);
                            return false;
                        }
                        if (!success) {
                            return false;
                        }

                        if (parms.Verbose) {
                            PrintAttrs(entry, parms);
                        }
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Looks for a MacZip header that pairs with the current file.
        /// </summary>
        /// <param name="arc">Archive object.</param>
        /// <param name="entry">Primary entry.</param>
        /// <param name="adfEntry">Result: AppleDouble header file entry, or NO_ENTRY if one
        ///   wasn't found.</param>
        /// <returns>True if an entry was found.</returns>
        private static bool HasMacZipHeader(IArchive arc, IFileEntry entry,
                out IFileEntry adfEntry) {
            adfEntry = IFileEntry.NO_ENTRY;
            if (arc is not Zip) {
                return false;
            }
            if (entry.IsMacZipHeader()) {
                // This is the header entry for a different record.
                return false;
            }
            // Generate header entry name and do a lookup.
            string macZipName = Zip.GenerateMacZipName(entry.FullPathName);
            if (string.IsNullOrEmpty(macZipName)) {
                return false;
            }
            return arc.TryFindFileEntry(macZipName, out adfEntry);
        }

        /// <summary>
        /// Handles all processing for a MacZip entry.
        /// </summary>
        private static bool HandleMacZip(IArchive arc, IFileEntry adfEntry, List<AttrEdit> editList,
                bool doViewOnly, ParamsBag parms, out IFileEntry adfArchiveEntry) {
            // We need to extract the AppleDouble "header" data as an archive, update the first
            // record, and record it back to the parent archive.  This currently holds the
            // contents in a memory stream.  Might want to use a delete-on-exit temp file instead.
            MemoryStream tmpMem;

            // Extract the AppleSingle file, and display or update the attributes..
            using (Stream adfStream = ArcTemp.ExtractToTemp(arc, adfEntry, FilePart.DataFork)) {
                using (IArchive adfArchive = AppleSingle.OpenArchive(adfStream, parms.AppHook)) {
                    adfArchiveEntry = adfArchive.GetFirstEntry();
                    if (doViewOnly) {
                        PrintAttrs(adfArchiveEntry, parms);
                        return true;
                    }

                    // Make the edits.
                    adfArchive.StartTransaction();
                    SetAttrs(adfArchiveEntry, editList, parms);
                    adfArchive.CommitTransaction(tmpMem = new MemoryStream());
                }
            }

            // Replace AppleSingle file.  Compression isn't relevant for AS.
            arc.StartTransaction();
            arc.DeletePart(adfEntry, FilePart.DataFork);
            arc.AddPart(adfEntry, FilePart.DataFork, new SimplePartSource(tmpMem),
                CompressionFormat.Default);
            return true;
        }


        private enum AttrID {
            Unknown = 0, Type, AuxType, HFSFileType, HFSCreator, Access, CreateWhen, ModWhen
        }
        private class AttrEdit {
            public AttrID ID { get; private set; }
            public object Value { get; private set; }

            public AttrEdit(AttrID id, object value) {
                ID = id;
                Value = value;
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
        /// <param name="args">List of edit arguments.</param>
        /// <param name="firstArg">Index of first valid argument.</param>
        /// <param name="editList">Result: list of edit instructions.</param>
        /// <returns>True if parsing was successful.</returns>
        private static bool ParseAttrEdits(string[] args, int firstArg,
                out List<AttrEdit> editList) {
            editList = new List<AttrEdit>();

            for (int i = firstArg; i < args.Length; i++) {
                MatchCollection matches = sNameValueRegex.Matches(args[i]);
                if (matches.Count != 1) {
                    Console.Error.WriteLine("Error: bad attribute '" + args[i] + "'");
                    return false;
                }
                string nameStr = matches[0].Groups[1].Value;
                string valueStr = matches[0].Groups[2].Value;

                AttrID id;
                object? value = null;
                long lconv;
                switch (nameStr.ToLower()) {
                    case "type":
                        id = AttrID.Type;
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
                    case "aux":
                        id = AttrID.AuxType;
                        lconv = ConvertHex(valueStr, 4);
                        if (lconv >= 0) {
                            value = (ushort)lconv;
                        }
                        break;
                    case "hfstype":
                        id = AttrID.HFSFileType;
                        if (valueStr.Length == 4) {
                            value = MacChar.IntifyMacConstantString(valueStr);
                        } else {
                            lconv = ConvertHex(valueStr, 8);
                            if (lconv >= 0) {
                                value = (uint)lconv;
                            }
                        }
                        break;
                    case "creator":
                        id = AttrID.HFSCreator;
                        if (valueStr.Length == 4) {
                            value = MacChar.IntifyMacConstantString(valueStr);
                        } else {
                            lconv = ConvertHex(valueStr, 8);
                            if (lconv >= 0) {
                                value = (uint)lconv;
                            }
                        }
                        break;
                    case "access":
                        id = AttrID.Access;
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
                    case "cdate":
                        id = AttrID.CreateWhen;
                        if (DateTime.TryParse(valueStr, out DateTime cwhen)) {
                            value = cwhen;
                        }
                        break;
                    case "mdate":
                        id = AttrID.ModWhen;
                        if (DateTime.TryParse(valueStr, out DateTime mwhen)) {
                            value = mwhen;
                        }
                        break;
                    default:
                        Console.Error.WriteLine("Error: unknown attribute '" + nameStr + "'");
                        Console.Error.WriteLine(
                            " (try: type, aux, hfstype, creator, access, cdate, mdate)");
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
        /// Converts a fixed-length hexadecimal string to an integer.
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

        /// <summary>
        /// Sets the attributes specified in the edit list.
        /// </summary>
        private static bool SetAttrs(IFileEntry entry, List<AttrEdit> editList, ParamsBag parms) {
            foreach (AttrEdit edit in editList) {
                switch (edit.ID) {
                    case AttrID.Type:
                        entry.FileType = (byte)edit.Value;
                        break;
                    case AttrID.AuxType:
                        entry.AuxType = (ushort)edit.Value;
                        break;
                    case AttrID.HFSFileType:
                        entry.HFSFileType = (uint)edit.Value;
                        break;
                    case AttrID.HFSCreator:
                        entry.HFSCreator = (uint)edit.Value;
                        break;
                    case AttrID.Access:
                        entry.Access = (byte)edit.Value;
                        break;
                    case AttrID.CreateWhen:
                        entry.CreateWhen = (DateTime)edit.Value;
                        break;
                    case AttrID.ModWhen:
                        entry.ModWhen = (DateTime)edit.Value;
                        break;
                    default:
                        Console.Error.WriteLine("Unknown attr ID: " + edit.ID);
                        return false;
                }
            }
            entry.SaveChanges();
            return true;
        }

        /// <summary>
        /// Prints the entry's attributes.
        /// </summary>
        private static void PrintAttrs(IFileEntry entry, ParamsBag parms) {
            const string LFMT = "  {0,-12}: ";
            Console.WriteLine("Attributes for '" + entry.FullPathName + "':");
            Console.WriteLine(string.Format(LFMT + "{1,-3} ${2:x2}",
                "File Type", FileTypes.GetFileTypeAbbrev(entry.FileType), entry.FileType));
            Console.WriteLine(string.Format(LFMT + "${1:x4}",
                "Aux Type", entry.AuxType));
            Console.WriteLine(string.Format(LFMT + "'{1,4}' ${2:x8}",
                "HFS Type", MacChar.StringifyMacConstant(entry.HFSFileType), entry.HFSFileType));
            Console.WriteLine(string.Format(LFMT + "'{1,4}' ${2:x8}",
                "HFS Creator", MacChar.StringifyMacConstant(entry.HFSCreator), entry.HFSCreator));
            Console.WriteLine(string.Format(LFMT + "${1:x2} [{2}]",
                "Access", entry.Access, Catalog.GetAccessString(entry.Access)));
            Console.WriteLine(string.Format(LFMT + "{1}",
                "Create Date", FormatDate(entry.CreateWhen)));
            Console.WriteLine(string.Format(LFMT + "{1}",
                "Mod Date", FormatDate(entry.ModWhen)));
            Console.WriteLine(string.Format(LFMT + "{1}",
                "Comment", string.IsNullOrEmpty(entry.Comment) ?
                    "(none)" : "\"" + entry.Comment + "\""));   // TODO: replace ctrl / MOR chars?
        }

        private static string FormatDate(DateTime when) {
            if (!TimeStamp.IsValidDate(when)) {
                return "[No Date]";
            } else {
                return when.ToString("dd-MMM-yyyy HH:mm:ss");   // add K for timezone
            }
        }
    }
}
