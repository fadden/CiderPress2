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

            DeleteFileWorker.CallbackFunc cbFunc = delegate (CallbackFacts what) {
                return Misc.HandleCallback(what, "deleting", parms);
            };
            DeleteFileWorker worker = new DeleteFileWorker(cbFunc, macZip: parms.MacZip,
                parms.AppHook);

            Console.CancelKeyPress += new ConsoleCancelEventHandler(Misc.SignalHandler);
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
                        if (!worker.DeleteFromArchive(arc, entries, out bool isCancelled)) {
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
                    bool success = worker.DeleteFromDisk(fs, entries, out bool isCancelled);
                    if (isCancelled) {
                        Console.Error.WriteLine("Cancelled.");
                        Debug.Assert(!success);
                        // continue; some changes may have been made
                    }
                    try {
                        // Save the deletions we managed to handle.
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
    }
}
