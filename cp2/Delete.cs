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

using AppCommon;
using CommonUtil;
using DiskArc;
using DiskArc.Arc;

namespace cp2 {
    internal static class Delete {
        /// <summary>
        /// Handles the "delete" command.
        /// </summary>
        public static bool HandleDelete(string cmdName, string[] args, ParamsBag parms) {
            if (args.Length < 2) {
                CP2Main.ShowUsage(cmdName);
                return false;
            }

            string[] paths = new string[args.Length - 1];
            for (int i = 1; i < args.Length; i++) {
                paths[i - 1] = args[i];
            }

            // Allow slash or colon as separators.  Ignore case.
            List<Glob> globList;
            try {
                globList = Glob.GenerateGlobSet(paths, Glob.STD_DIR_SEP_CHARS, true);
            } catch (ArgumentException ex) {
                Console.Error.WriteLine("Error: " + ex.Message);
                return false;
            }

            if (!ExtArchive.OpenExtArc(args[0], true, false, parms, out DiskArcNode? rootNode,
                    out DiskArcNode? leafNode, out object? leaf, out IFileEntry endDirEntry)) {
                return false;
            }
            using (rootNode) {
                if (leaf is IArchive) {
                    IArchive arc = (IArchive)leaf;
                    if (!Misc.StdChecks(arc, needWrite: true, needMulti: true)) {
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
                        arc.StartTransaction();
                        if (!DeleteFromArchive(arc, entries, parms)) {
                            return false;
                        }
                        leafNode.SaveUpdates(parms.Compress);
                    } catch (Exception ex) {
                        Console.Error.WriteLine("Error: " + ex.Message);
                        return false;
                    } finally {
                        arc.CancelTransaction();    // no effect if transaction isn't open
                    }

                } else {
                    IFileSystem? fs = Misc.GetTopFileSystem(leaf);
                    if (!Misc.StdChecks(fs, needWrite: true, parms.FastScan)) {
                        return false;
                    }
                    List<IFileEntry> entries =
                        FindEntries.FindMatchingEntries(fs, endDirEntry, globList, parms.Recurse);
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
                    bool success = DeleteFromDisk(fs, entries, parms);

                    try {
                        leafNode.SaveUpdates(parms.Compress);
                    } catch (Exception ex) {
                        Console.Error.WriteLine("Error: update failed: " + ex.Message);
                        return false;
                    }
                    if (!success) {
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool DeleteFromArchive(IArchive arc, List<IFileEntry> entries,
                ParamsBag parms) {
            int doneCount = 0;
            foreach (IFileEntry entry in entries) {
                CallbackFacts facts = new CallbackFacts(CallbackFacts.Reasons.Progress,
                    entry.FullPathName, entry.DirectorySeparatorChar);
                facts.ProgressPercent = (100 * doneCount) / entries.Count;
                Misc.HandleCallback(facts, "deleting", parms);

                IFileEntry adfEntry = IFileEntry.NO_ENTRY;
                if (arc is Zip && parms.MacZip) {
                    // Only handle __MACOSX/ entries when paired with other entries.
                    if (entry.IsMacZipHeader()) {
                        continue;
                    }
                    // Check to see if we have a paired entry.
                    string? macZipName = Zip.GenerateMacZipName(entry.FullPathName);
                    if (!string.IsNullOrEmpty(macZipName)) {
                        arc.TryFindFileEntry(macZipName, out adfEntry);
                    }
                }

                arc.DeleteRecord(entry);
                if (adfEntry != IFileEntry.NO_ENTRY) {
                    arc.DeleteRecord(adfEntry);
                }
                doneCount++;
            }
            return true;
        }

        private static bool DeleteFromDisk(IFileSystem fs, List<IFileEntry> entries,
                ParamsBag parms) {
            // We need to delete the files that live in a directory before we delete that
            // directory.  The recursive glob matcher generated the list in exactly the
            // wrong order, so we need to walk it from back to front.
            int doneCount = 0;
            for (int i = entries.Count - 1; i >= 0; i--) {
                IFileEntry entry = entries[i];
                try {
                    CallbackFacts facts = new CallbackFacts(CallbackFacts.Reasons.Progress,
                        entry.FullPathName, entry.DirectorySeparatorChar);
                    facts.ProgressPercent = (100 * doneCount) / entries.Count;
                    Misc.HandleCallback(facts, "deleting", parms);

                    fs.DeleteFile(entry);
                    doneCount++;
                } catch (IOException ex) {
                    Console.Error.WriteLine("Error: unable to delete '" + entry.FullPathName +
                        "': " + ex.Message);
                    return false;
                }
            }
            return true;
        }
    }
}
