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
using DiskArc.Arc;
using static DiskArc.Defs;

namespace cp2 {
    internal static class Move {
        /// <summary>
        /// Handles the "move" command.
        /// </summary>
        public static bool HandleMove(string cmdName, string[] args, ParamsBag parms) {
            if (args.Length < 3) {
                CP2Main.ShowUsage(cmdName);
                return false;
            }

            string newPathName = args[args.Length - 1];
            if (newPathName == "/" || newPathName == ":") {
                // This is a reference to the volume directory.
                newPathName = string.Empty;
            }
            string[] oldPaths = new string[args.Length - 2];
            for (int i = 1; i < args.Length - 1; i++) {
                oldPaths[i - 1] = args[i];
            }

            Console.CancelKeyPress += new ConsoleCancelEventHandler(Misc.SignalHandler);
            if (!ExtArchive.OpenExtArc(args[0], true, false, parms, out DiskArcNode? rootNode,
                    out DiskArcNode? leafNode, out object? leaf, out IFileEntry endDirEntry)) {
                return false;
            }
            using (rootNode) {
                List<Glob> globList;
                bool doRenameVol = false;

                // Check if source arg is "/" or ":", indicating a volume directory rename.
                // The user can escape the character to let the glob stuff run if they really
                // want to rename a file in the root directory named ":".
                if (oldPaths.Length == 1 && (oldPaths[0] == "/" || oldPaths[0] == ":")) {
                    if (leaf is IArchive) {
                        Console.Error.WriteLine("Error: file archives don't have a volume name");
                        return false;
                    }
                    if (endDirEntry != IFileEntry.NO_ENTRY) {
                        Console.Error.WriteLine("Error: can't specify directory in ext-archive");
                        return false;
                    }
                    globList = new List<Glob>();
                    doRenameVol = true;
                } else {
                    // Allow slash or colon as separators.  Ignore case.
                    try {
                        globList = Glob.GenerateGlobSet(oldPaths, Glob.STD_DIR_SEP_CHARS, true);
                    } catch (ArgumentException ex) {
                        Console.Error.WriteLine("Error: " + ex.Message);
                        return false;
                    }
                }

                if (leaf is IArchive) {
                    IArchive arc = (IArchive)leaf;
                    if (!Misc.StdChecks(arc, needWrite: true, needMulti: false)) {
                        return false;
                    }
                    // Do the wildcard match so they can reference single files with wildcards,
                    // e.g. use '?' for inconvenient characters.
                    List<IFileEntry> entries =
                        FindEntries.FindMatchingEntries(arc, globList, parms.Recurse);
                    if (!Misc.CheckGlobList(globList)) {
                        return false;
                    }
                    if (entries.Count != 1) {
                        // TODO: make this work like it does for disk archives.  This requires
                        // transforming each individual entry pathname, removing parts of the name
                        // and prepending the new "directory name".  This need to work correctly
                        // when files are moved from multiple "source directories".
                        Console.Error.WriteLine("Error: can only rename individual files " +
                            "in file archives");
                        return false;
                    }
                    try {
                        arc.StartTransaction();
                        if (!RenameArchiveEntry(arc, entries[0], newPathName, parms)) {
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
                    bool success = true;
                    if (doRenameVol) {
                        success = RenameDiskVol(fs, newPathName, parms);
                    } else {
                        // Do the match, with recursion disabled.  If they specify a directory,
                        // we want to move that directory as a unit.
                        List<IFileEntry> entries =
                            FindEntries.FindMatchingEntries(fs, endDirEntry, globList, false);
                        if (!Misc.CheckGlobList(globList)) {
                            return false;
                        }
                        // If the previous check succeeded, there must be entries to move.
                        Debug.Assert(entries.Count > 0);
                        success = MoveDiskEntries(fs, endDirEntry, entries, newPathName, parms,
                            out bool isCancelled);
                        if (isCancelled) {
                            Console.Error.WriteLine("Cancelled.");
                            Debug.Assert(!success);
                            // continue; some changes may have been made
                        }
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
                }
            }

            return true;
        }

        /// <summary>
        /// Renames an entry in a file archive.
        /// </summary>
        /// <remarks>
        /// <para>The only complication is that we want to normalize the path separator characters
        /// in the new name to whatever the archive entry is using.</para>
        /// <para>If MacZip is enabled, we need to check for the AppleDouble header file.</para>
        /// </remarks>
        private static bool RenameArchiveEntry(IArchive arc, IFileEntry entry, string newPathName,
                ParamsBag parms) {
            if (string.IsNullOrEmpty(newPathName) && arc is not AppleSingle) {
                // This is a reference to the volume directory, which we don't have.  We make
                // an exception for AppleSingle, which does not allow partial paths, but does
                // allow empty filenames.
                Console.Error.WriteLine("Error: file archives don't have a volume directory");
                return false;
            }
            // Normalize new pathname.
            string newNormal;
            if (entry.DirectorySeparatorChar != IFileEntry.NO_DIR_SEP) {
                StringBuilder sb = new StringBuilder(newPathName.Length);
                List<string> parts = PathName.SplitPartialPath(newPathName, Glob.STD_DIR_SEP_CHARS);
                for (int i = 0; i < parts.Count; i++) {
                    sb.Append(arc.AdjustFileName(parts[i]));
                    if (i != parts.Count - 1) {
                        sb.Append(entry.DirectorySeparatorChar);
                    }
                }
                newNormal = sb.ToString();
            } else {
                newNormal = newPathName;
            }

            IFileEntry adfEntry = IFileEntry.NO_ENTRY;
            string newAdfName = string.Empty;
            if (arc is Zip && parms.MacZip) {
                if (entry.IsMacZipHeader()) {
                    // ? Might as well allow it.
                }

                // Check to see if we have a paired entry.
                string? macZipName = Zip.GenerateMacZipName(entry.FullPathName);
                if (!string.IsNullOrEmpty(macZipName)) {
                    if (arc.TryFindFileEntry(macZipName, out adfEntry)) {
                        newAdfName = Zip.GenerateMacZipName(newNormal);
                    }
                }
            }

            // See if it already exists.
            if (arc.TryFindFileEntry(newNormal, out IFileEntry unused)) {
                Console.Error.WriteLine("Error: file already exists: '" + newNormal + "'");
                return false;
            }
            if (adfEntry != IFileEntry.NO_ENTRY &&
                    arc.TryFindFileEntry(newAdfName, out IFileEntry unused1)) {
                Console.Error.WriteLine("Error: file already exists: '" + newAdfName + "'");
                return false;
            }

            // Use the standard callback mechanism to report progress.
            CallbackFacts facts = new CallbackFacts(CallbackFacts.Reasons.Progress,
                entry.FullPathName, entry.DirectorySeparatorChar);
            facts.NewPathName = newNormal;
            facts.NewDirSep = entry.DirectorySeparatorChar;
            facts.ProgressPercent = 0;
            Misc.HandleCallback(facts, "renaming", parms);

            // Finally, do the actual rename.
            entry.FileName = newNormal;
            if (adfEntry != IFileEntry.NO_ENTRY) {
                adfEntry.FileName = newAdfName;
            }
            return true;
        }

        /// <summary>
        /// Renames the disk volume.
        /// </summary>
        private static bool RenameDiskVol(IFileSystem fs, string newName, ParamsBag parms) {
            if (string.IsNullOrEmpty(newName)) {
                Console.Error.WriteLine("Error: must specify a name");
                return false;
            }
            string? adjNewName = fs.AdjustVolumeName(newName);
            if (adjNewName == null) {
                Console.Error.WriteLine("Error: filesystem doesn't have a volume name");
                return false;
            }

            IFileEntry volDir = fs.GetVolDirEntry();
            CallbackFacts facts = new CallbackFacts(CallbackFacts.Reasons.Progress,
                volDir.FullPathName, volDir.DirectorySeparatorChar);
            facts.NewPathName = adjNewName;
            facts.NewDirSep = volDir.DirectorySeparatorChar;
            facts.ProgressPercent = 0;
            Misc.HandleCallback(facts, "renaming", parms);

            volDir.FileName = adjNewName;
            volDir.SaveChanges();
            return true;
        }

        /// <summary>
        /// Renames or moves one or more entries in a disk filesystem.
        /// </summary>
        private static bool MoveDiskEntries(IFileSystem fs, IFileEntry baseDirEnt,
                List<IFileEntry> entries, string newPathName, ParamsBag parms,
                out bool isCancelled) {
            // We need to locate the directory in which "newPathName" lives, i.e. if the target
            // is "foo/bar" then we need to locate "foo".  We start from the base directory
            // specified by the ext-archive, or from the volume dir.
            IFileEntry curDirEnt = baseDirEnt;
            if (curDirEnt == IFileEntry.NO_ENTRY) {
                curDirEnt = fs.GetVolDirEntry();
            }

            if (string.IsNullOrEmpty(newPathName)) {
                // Moving files into the root dir, though the root may not be the vol dir.
                return MoveMultiple(fs, entries, curDirEnt, parms, out isCancelled);
            }
            isCancelled = false;

            List<string> parts = PathName.SplitPartialPath(newPathName, Glob.STD_DIR_SEP_CHARS);
            for (int i = 0; i < parts.Count - 1; i++) {
                string adjDirName = fs.AdjustFileName(parts[i]);
                if (!fs.TryFindFileEntry(curDirEnt, adjDirName, out IFileEntry nextDirEnt)) {
                    // Not found.
                    Console.Error.WriteLine("Error: directory not found: '" + adjDirName + "'");
                    return false;
                }
                // Exists; is it a directory?
                if (!nextDirEnt.IsDirectory) {
                    Console.Error.WriteLine("Error: exists but is not a directory: '" +
                        nextDirEnt.FullPathName + "'");
                    return false;
                }
                curDirEnt = nextDirEnt;
            }

            // Test the last component.
            string adjFileName = fs.AdjustFileName(parts[parts.Count - 1]);
            if (fs.TryFindFileEntry(curDirEnt, adjFileName, out IFileEntry fileEnt) &&
                    entries.Count == 1) {
                // Found an entry with a matching name; test to see if it's the same as the
                // first and only entry in the list.  If so, we're doing a no-op rename,
                // possibly to change the file's case.
                if (fileEnt == entries[0]) {
                    fileEnt = IFileEntry.NO_ENTRY;
                }
            }
            if (fileEnt != IFileEntry.NO_ENTRY) {
                // Last component exists; is it a directory?
                if (!fileEnt.IsDirectory) {
                    Console.Error.WriteLine("Error: file already exists: '" +
                        fileEnt.FullPathName + "'");
                    return false;
                }
                // Yes, move all entries into this directory.
                if (!MoveMultiple(fs, entries, fileEnt, parms, out isCancelled)) {
                    return false;
                }
            } else {
                // Doesn't exist, this is a single-file move and/or rename.
                if (entries.Count != 1) {
                    Console.Error.WriteLine("ERROR: target '" + adjFileName +
                        "' is not a directory");
                    return false;
                }
                CallbackFacts facts = new CallbackFacts(CallbackFacts.Reasons.Progress,
                    entries[0].FullPathName, entries[0].DirectorySeparatorChar);
                if (curDirEnt.ContainingDir == IFileEntry.NO_ENTRY) {
                    facts.NewPathName = adjFileName;     // don't show vol dir name
                } else {
                    facts.NewPathName = curDirEnt.FullPathName + fs.Characteristics.DirSep +
                        adjFileName;
                }
                facts.NewDirSep = fs.Characteristics.DirSep;
                facts.ProgressPercent = 0;
                Misc.HandleCallback(facts, "moving", parms);

                try {
                    // This is unlikely to fail.
                    fs.MoveFile(entries[0], curDirEnt, adjFileName);
                } catch (IOException ex) {
                    Console.Error.WriteLine("Error: " + ex.Message);
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Moves multiple files into a directory, retaining their current filenames.
        /// </summary>
        private static bool MoveMultiple(IFileSystem fs, List<IFileEntry> entries,
                IFileEntry targetEnt, ParamsBag parms, out bool isCancelled) {
            MoveFileWorker.CallbackFunc cbFunc = delegate (CallbackFacts what) {
                return Misc.HandleCallback(what, "moving", parms);
            };
            MoveFileWorker worker = new MoveFileWorker(cbFunc, parms.AppHook);
            return worker.MoveDiskEntries(fs, entries, targetEnt, out isCancelled);
        }
    }
}
