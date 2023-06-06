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
using DiskArc;
using FileConv;
using static DiskArc.Defs;

namespace cp2 {
    internal static class Add {
        /// <summary>
        /// Handles the "add" command.
        /// </summary>
        /// <remarks>
        /// <para>Many file archivers will silently create a new archive file if it doesn't
        /// already exist.  In some cases, an empty archive doesn't make sense, so creating
        /// as part of adding is natural.</para>
        /// </remarks>
        public static bool HandleAdd(string cmdName, string[] args, ParamsBag parms) {
            if (args.Length <= 1) {
                CP2Main.ShowUsage(cmdName);
                return false;
            }

            // Copy the list of paths to add, skipping past the ext-archive argument.
            string[] paths = new string[args.Length - 1];
            for (int i = 1; i < args.Length; i++) {
                paths[i - 1] = args[i];
            }

            return HandleAddImport(args[0], paths, null, parms);
        }

        /// <summary>
        /// Handles the "import" command.
        /// </summary>
        public static bool HandleImport(string cmdName, string[] args, ParamsBag parms) {
            if (args.Length <= 2) {
                CP2Main.ShowUsage(cmdName);
                return false;
            }

            ConvConfig.FileConvSpec? spec = ConvConfig.CreateSpec(args[1]);
            if (spec == null) {
                Console.Error.WriteLine("Error: invalid import spec '" + args[1] + "'");
                return false;
            }
            ConvConfig.FileConvSpec? cfgSpec = parms.GetImportSpec(args[1]);
            if (cfgSpec != null) {
                spec.MergeSpec(cfgSpec);    // merge items from config file
            }

            // Check options against what the importer lists.  Requires instantiation.  Also
            // provides an early exit if they're requesting an unknown exporter.  We don't
            // try to validate the value, just the tag.
            Importer? tmpConv = ImportFoundry.GetConverter(spec.Tag, parms.AppHook);
            if (tmpConv == null) {
                Console.Error.WriteLine("Error: unknown importer '" + spec.Tag + "'");
                return false;
            }
            foreach (string optTag in spec.Options.Keys) {
                if (!tmpConv.HasOption(optTag)) {
                    Console.Error.WriteLine("Warning: config option '" + optTag +
                        "' not valid for importer '" + spec.Tag + "'");
                    // continue; unknown options will be ignored
                }
            }
            if (parms.Debug) {
                Console.WriteLine("+++ import spec: " + spec);
            }

            // Copy the list of paths to import.
            string[] paths = new string[args.Length - 2];
            for (int i = 2; i < args.Length; i++) {
                paths[i - 2] = args[i];
            }

            return HandleAddImport(args[0], paths, spec, parms);
        }

        private static bool HandleAddImport(string extArchive, string[] paths,
                ConvConfig.FileConvSpec? importSpec, ParamsBag parms) {
            bool isSimple = false;
            int lastPosn = extArchive.LastIndexOf(ExtArchive.SPLIT_CHAR);
            if (lastPosn < 0 || lastPosn == 1) {
                // No ':', or part of a path root like "C:\...".  We should probably only accept
                // the second form on Windows, in case somebody has an archive named "x" and
                // they want to add to an archive inside it.  (Seems unlikely.)
                isSimple = true;
            }
            if (isSimple) {
                // Simple filename.  Does it exist?
                if (!File.Exists(extArchive)) {
                    // No, try to create it.
                    if (!CreateArchiveByName(extArchive, parms)) {
                        return false;
                    }
                    if (parms.Verbose) {
                        Console.WriteLine("Created file archive '" + extArchive + "'");
                    }
                }
            }

            if (!ExtArchive.OpenExtArc(extArchive, true, false, parms, out DiskArcNode? rootNode,
                    out DiskArcNode? leafNode, out object? leaf, out IFileEntry endDirEntry)) {
                return false;
            }
            using (rootNode) {
                AddFileSet.AddOpts addOpts = ConfigureAddOpts(importSpec != null, parms);

                // Generate the set of files to be added or imported.
                AddFileSet addSet;
                try {
                    addSet = new AddFileSet(Environment.CurrentDirectory, paths, addOpts,
                        importSpec, parms.AppHook);
                } catch (FileNotFoundException ex) {
                    Console.Error.WriteLine("Error: unable to open '" + ex.FileName + "': " +
                        ex.Message);
                    return false;
                } catch (ArgumentException ex) {
                    Console.Error.WriteLine("Error: " + ex.Message);
                    return false;
                }
                if (addSet.Count == 0) {
                    Console.Error.WriteLine("No files found");
                    return false;
                }
                string opStr = (importSpec == null) ? "Adding " : "Importing ";
                if (parms.Verbose) {
                    Console.WriteLine(opStr + addSet.Count + " file" +
                        (addSet.Count == 1 ? "" : "s"));
                }

                opStr = (importSpec == null) ? "adding" : "importing";
                AddFileWorker.CallbackFunc cbFunc = delegate (CallbackFacts what) {
                    return Misc.HandleCallback(what, opStr, parms);
                };
                AddFileWorker worker = new AddFileWorker(addSet, cbFunc,
                    doCompress: parms.Compress, macZip: parms.MacZip, stripPaths: parms.StripPaths,
                    rawMode: parms.Raw, parms.AppHook);

                if (leaf is IArchive) {
                    IArchive arc = (IArchive)leaf;
                    if (!Misc.StdChecks(arc, needWrite: true, needMulti: true)) {
                        return false;
                    }
                    try {
                        arc.StartTransaction();
                        worker.AddFilesToArchive(arc, out bool isCancelled);
                        if (isCancelled) {
                            Console.Error.WriteLine("Cancelled.");
                            return false;
                        }
                        leafNode.SaveUpdates(parms.Compress);
                    } catch (ConversionException ex) {
                        Console.Error.WriteLine("Import error: " + ex.Message);
                        return false;
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
                    try {
                        worker.AddFilesToDisk(fs, endDirEntry, out bool isCancelled);
                        if (isCancelled) {
                            Console.Error.WriteLine("Cancelled.");
                            // continue; some changes may have been made
                            success = false;
                        }
                    } catch (ConversionException ex) {
                        Console.Error.WriteLine("Import error: " + ex.Message);
                        success = false;
                    } catch (Exception ex) {
                        Console.Error.WriteLine("Error: " + ex.Message);
                        success = false;
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
        /// Creates and configures the file-add options.
        /// </summary>
        private static AddFileSet.AddOpts ConfigureAddOpts(bool isImport, ParamsBag parms) {
            AddFileSet.AddOpts addOpts = new AddFileSet.AddOpts();
            if (isImport) {
                addOpts.ParseADF = addOpts.ParseAS = addOpts.ParseNAPS = addOpts.CheckNamed =
                    addOpts.CheckFinderInfo = false;
            } else {
                addOpts.ParseADF = parms.FromADF;
                addOpts.ParseAS = parms.FromAS;
                addOpts.ParseNAPS = parms.FromNAPS;
                addOpts.CheckNamed = parms.FromHost;
                addOpts.CheckFinderInfo = parms.FromHost;
            }
            addOpts.Recurse = parms.Recurse;
            addOpts.StripExt = parms.StripExt;
            return addOpts;
        }

        /// <summary>
        /// Creates an appropriate file archive, given a partial pathname.
        /// </summary>
        /// <param name="pathName">Full or partial pathname.</param>
        /// <returns>True on success.</returns>
        private static bool CreateArchiveByName(string pathName, ParamsBag parms) {
            string? ext = Path.GetExtension(pathName);
            if (ext == null || !FileAnalyzer.ExtensionToKind(ext, out FileKind kind,
                    out SectorOrder unused1, out FileKind unsued2, out bool unused)) {
                Console.Error.WriteLine("Unable to determine file type for '" + pathName + "'");
                return false;
            }
            if (Defs.IsDiskImageFile(kind)) {
                Console.Error.WriteLine("Disk image file not found: '" + pathName + "'");
                return false;
            } else {
                // Let the create-file-archive handler do the work.
                return ArcUtil.HandleCreateFileArchive("cfa", new string[] { pathName }, parms);
            }
        }
    }
}
