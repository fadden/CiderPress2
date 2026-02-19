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
using static DiskArc.Defs;
using static DiskArc.IFileSystem;

namespace cp2 {
    internal static class Mkdir {
        /// <summary>
        /// Handles the "mkdir" command.
        /// </summary>
        public static bool HandleMkdir(string cmdName, string[] args, ParamsBag parms) {
            if (args.Length != 2) {
                CP2Main.ShowUsage(cmdName);
                return false;
            }

            string newDirPath = args[1];

            if (!ExtArchive.OpenExtArc(args[0], true, false, parms, out DiskArcNode? rootNode,
                    out DiskArcNode? leafNode, out object? leaf, out IFileEntry endDirEntry)) {
                return false;
            }
            using (rootNode) {
                if (leaf is IArchive) {
                    Console.Error.WriteLine("Error: cannot create directories in file archives");
                    return false;
                }

                IFileSystem? fs = Misc.GetTopFileSystem(leaf);
                if (!Misc.StdChecks(fs, needWrite: true, parms.FastScan)) {
                    return false;
                }
                if (!fs.Characteristics.IsHierarchical) {
                    Console.Error.WriteLine("Error: cannot create directories in this filesystem");
                    return false;
                }
                bool success = DoMkdir(fs, newDirPath, endDirEntry, parms);

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
            return true;
        }

        /// <summary>
        /// Creates a new directory in a filesystem.
        /// </summary>
        private static bool DoMkdir(IFileSystem fs, string newDirPath, IFileEntry curDirEnt,
                ParamsBag parms) {
            List<string> dirs;
            try {
                dirs = PathName.SplitPartialPath(newDirPath, Glob.STD_DIR_SEP_CHARS, false);
            } catch (ArgumentException ex) {
                Console.Error.WriteLine("Error: " + ex.Message);
                return false;
            }
            if (curDirEnt == IFileEntry.NO_ENTRY) {
                curDirEnt = fs.GetVolDirEntry();
            }
            bool didCreate = false;
            foreach (string dir in dirs) {
                string adjDirName = fs.AdjustFileName(dir);
                if (fs.TryFindFileEntry(curDirEnt, adjDirName, out IFileEntry nextDirEnt)) {
                    // Exists; is it a directory?
                    if (!nextDirEnt.IsDirectory) {
                        Console.Error.WriteLine("Error: exists but not a directory: '" +
                            adjDirName + "'");
                        return false;
                    }
                    curDirEnt = nextDirEnt;
                } else {
                    // Not found, create new.
                    try {
                        curDirEnt = fs.CreateFile(curDirEnt, adjDirName, CreateMode.Directory);
                    } catch (IOException ex) {
                        throw new IOException("Error: unable to create directory '" +
                            adjDirName + "': " + ex.Message);
                    }
                    didCreate = true;
                }
            }
            if (parms.Verbose) {
                if (didCreate) {
                    CallbackFacts facts = new CallbackFacts(CallbackFacts.Reasons.Progress,
                        curDirEnt.FullPathName, curDirEnt.DirectorySeparatorChar);
                    facts.ProgressPercent = 0;
                    Misc.HandleCallback(facts, "creating", parms);
                } else {
                    Console.WriteLine("Directory '" + curDirEnt.FullPathName + "' already exists");
                }
            }
            return true;
        }
    }
}
