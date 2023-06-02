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

//
// Notes on working with two ext-archive specifiers...
//
// If the source and destination archives are in the same host file, the correct thing to do is
// to open the file once and open the various items in a single DiskArcNode tree.  This ensures
// internal consistency.  With care, it's even possible to have both source and destination
// reference the same volume.  For example, if you gather the full list of entries up front, you
// avoid the possibility of an infinitely recursive copy where the source reader descends into
// newly-created directories.
//
// If the host file is opened twice, bad things can happen even when reading and writing from
// different disks/archives inside.  For example, suppose we're copying from an HFS volume into
// a ZIP archive that lives on that HFS volume.  When the archive update completes, the HFS
// volume is updated.  If we have opened the HFS volume twice, any cached data on the "read"
// side will be out of sync with the "write" side.
//
// We can get away with this for a command-line copy operation because we can close the "read"
// side before we flush the changes to the "write" side.  However, this doesn't save us if
// source and destination are a single filesystem, unless the source has fully cached the
// source disk, because filesystems like HFS will do some rearranging as the data is being written.
// (Archives are easier because they're written to a temp file first; filesystems are read and
// written "live".)
//
// If a single DiskArcNode tree manages both archives, the update mechanism will manage all of
// the flushing and stream-replacing for us.  Copying to and from a single filesystem or file
// archive works fine (though the latter would always involve copying files onto themselves,
// because we don't currently allow subdirectory specification for file archives).  The trick
// then is opening a second ext-archive specification that partially overlaps the first.  The
// ext-archive code must recognize the situation and correctly identify the overlapping objects.
//

namespace cp2 {
    internal static class Copy {
        /// <summary>
        /// Handles the "copy" command.
        /// </summary>
        public static bool HandleCopy(string cmdName, string[] args, ParamsBag parms) {
            if (args.Length < 2) {
                CP2Main.ShowUsage(cmdName);
                return false;
            }

            string srcExtArchive = args[0];
            string dstExtArchive = args[args.Length - 1];
            if (!ExtArchive.CheckSameHostFile(srcExtArchive, dstExtArchive, out bool isSame)) {
                Console.Error.WriteLine("Error: source or destination file does not exist");
                return false;
            }
            if (isSame) {
                Console.Error.WriteLine("Error: source and destination must not be the same file");
                return false;
            }

            // Copy the list of paths to extract.  If no paths were specified, we want to
            // extract everything.  We can't do this with a simple wildcard, so just use the
            // empty list as a signal.
            string[] paths = new string[args.Length - 2];
            for (int i = 1; i < args.Length - 1; i++) {
                paths[i - 1] = args[i];
            }

            if (parms.Debug) {
                Console.WriteLine("++ " + srcExtArchive + " / " + dstExtArchive);
                for (int i = 0; i < paths.Length; i++) {
                    Console.WriteLine(" " + paths[i]);
                }
            }

            // Allow slash or colon as separators.  Ignore case.
            List<Glob> globList;
            try {
                globList = Glob.GenerateGlobSet(paths, Glob.STD_DIR_SEP_CHARS, true);
            } catch (ArgumentException ex) {
                Console.Error.WriteLine("Error: " + ex.Message);
                return false;
            }

            CopyFileWorker.CallbackFunc cbFunc = delegate (CallbackFacts what, object? obj) {
                return Misc.HandleCallback(what, "copying", parms);
            };
            CopyFileWorker worker = new CopyFileWorker(cbFunc, convertDOSText: parms.ConvertDOSText,
                doCompress: parms.Compress, macZip: parms.MacZip, stripPaths: parms.StripPaths,
                parms.AppHook);

            DiskArcNode? srcRootNode = null;
            DiskArcNode? dstRootNode = null;

            try {
                if (!ExtArchive.OpenExtArc(dstExtArchive, true, false, parms, out dstRootNode,
                        out DiskArcNode? dstLeafNode,out object? dstLeaf,
                        out IFileEntry dstDirEntry)) {
                    return false;
                }
                if (!ExtArchive.OpenExtArc(srcExtArchive, true, true, parms, out srcRootNode,
                        out DiskArcNode? srcLeafNode, out object? srcLeaf,
                        out IFileEntry srcDirEntry)) {
                    return false;
                }

                object srcObj;
                List<IFileEntry> entries;
                if (srcLeaf is IArchive) {
                    IArchive srcArchive = (IArchive)srcLeaf;
                    if (!Misc.StdChecks(srcArchive, needWrite: false, needMulti: false)) {
                        return false;
                    }
                    entries = FindEntries.FindMatchingEntries(srcArchive, globList,
                        parms.Recurse);
                    srcObj = srcLeaf;
                } else {
                    IFileSystem? srcFs = Misc.GetTopFileSystem(srcLeaf);
                    if (!Misc.StdChecks(srcFs, needWrite: false, parms.FastScan)) {
                        return false;
                    }
                    entries = FindEntries.FindMatchingEntries(srcFs, srcDirEntry, globList,
                        parms.Recurse);
                    srcObj = srcFs;
                }

                if (!Misc.CheckGlobList(globList)) {
                    return false;
                }
                if (entries.Count == 0) {
                    // Should only be possible if globList is empty, i.e. copy all, so the
                    // source archive must be empty.
                    if (parms.Verbose) {
                        Console.WriteLine("Source archive is empty");
                    }
                    return true;
                }

                if (dstLeaf is IArchive) {
                    IArchive dstArchive = (IArchive)dstLeaf;
                    if (!Misc.StdChecks(dstArchive, needWrite: true, needMulti: true)) {
                        return false;
                    }
                    try {
                        dstArchive.StartTransaction();
                        if (!worker.CopyToArchive(srcObj, entries, dstArchive)) {
                            return false;
                        }
                        dstLeafNode.SaveUpdates(parms.Compress);
                    } catch (Exception ex) {
                        Console.Error.WriteLine("Error: " + ex.Message);
                        return false;
                    } finally {
                        dstArchive.CancelTransaction();    // no effect if transaction isn't open
                    }
                } else {
                    IFileSystem? dstFs = Misc.GetTopFileSystem(dstLeaf);
                    if (!Misc.StdChecks(dstFs, needWrite: true, parms.FastScan)) {
                        return false;
                    }
                    bool success = worker.CopyToDisk(srcObj, srcDirEntry, entries, dstFs,
                        dstDirEntry);

                    try {
                        dstLeafNode.SaveUpdates(parms.Compress);
                    } catch (Exception ex) {
                        Console.Error.WriteLine("Error: update failed: " + ex.Message);
                        return false;
                    }
                    if (!success) {
                        return false;
                    }
                }
            } finally {
                srcRootNode?.Dispose();
                dstRootNode?.Dispose();
            }

            return true;
        }
    }
}
