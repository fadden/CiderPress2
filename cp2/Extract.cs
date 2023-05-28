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
using FileConv;

namespace cp2 {
    internal static class Extract {
        /// <summary>
        /// Handles the "extract" command.
        /// </summary>
        public static bool HandleExtract(string cmdName, string[] args, ParamsBag parms) {
            if (args.Length == 0) {
                CP2Main.ShowUsage(cmdName);
                return false;
            }

            // Copy the list of paths to extract.  If no paths were specified, we want to
            // extract everything.  We can't do this with a simple wildcard, so just use the
            // empty list as a signal.
            string[] paths = new string[args.Length - 1];
            for (int i = 1; i < args.Length; i++) {
                paths[i - 1] = args[i];
            }
            if (parms.Debug) {
                Console.WriteLine("+++ extract list:");
                for (int i = 0; i < paths.Length; i++) {
                    Console.WriteLine("+++ " + i + ": '" + paths[i] + "', regex='" +
                        Glob.GenerateGlobExpression(paths[i]) + "'");
                }
                Console.WriteLine();
            }

            return HandleExtractExport(args[0], paths, null, parms);
        }

        /// <summary>
        /// Handles the "export" command.
        /// </summary>
        public static bool HandleExport(string cmdName, string[] args, ParamsBag parms) {
            if (args.Length < 2) {
                CP2Main.ShowUsage(cmdName);
                return false;
            }

            ConvHelper.FileConvSpec? spec = ConvHelper.CreateSpec(args[1]);
            if (spec == null) {
                Console.Error.WriteLine("Error: invalid export spec '" + args[1] + "'");
                return false;
            }
            ConvHelper.FileConvSpec? cfgSpec = parms.GetExportSpec(args[1]);
            if (cfgSpec != null) {
                spec.MergeSpec(cfgSpec);    // merge items from config file
            }

            // Check options against what the exporter lists.  Requires instantiation.  Also
            // provides an early exit if they're requesting an unknown exporter.  We don't
            // try to validate the value, just the tag.
            if (spec.Tag == ConvHelper.BEST) {
                if (spec.Options.Count > 0) {
                    Console.Error.WriteLine("Warning: providing export options with '" +
                        ConvHelper.BEST + "' is not recommended");
                    // allow it?
                }
            } else {
                Converter? tmpConv = ExportFoundry.GetConverter(spec.Tag,
                    new FileAttribs(), null, null, parms.AppHook);
                if (tmpConv == null) {
                    Console.Error.WriteLine("Error: unknown exporter '" + spec.Tag + "'");
                    return false;
                }
                foreach (string optTag in spec.Options.Keys) {
                    if (!tmpConv.HasOption(optTag)) {
                        Console.Error.WriteLine("Warning: config option '" + optTag +
                            "' not valid for exporter '" + spec.Tag + "'");
                        // continue; unknown options will be ignored
                    }
                }
            }
            if (parms.Debug) {
                Console.WriteLine("+++ export spec: " + spec);
            }

            // Copy the list of paths to export.
            string[] paths = new string[args.Length - 2];
            for (int i = 2; i < args.Length; i++) {
                paths[i - 2] = args[i];
            }
            if (parms.Debug) {
                Console.WriteLine("+++ export list:");
                for (int i = 0; i < paths.Length; i++) {
                    Console.WriteLine("+++ " + i + ": '" + paths[i] + "', regex='" +
                        Glob.GenerateGlobExpression(paths[i]) + "'");
                }
                Console.WriteLine();
            }

            return HandleExtractExport(args[0], paths, spec, parms);
        }

        /// <summary>
        /// Common code for extracting and exporting.
        /// </summary>
        private static bool HandleExtractExport(string extArchive, string[] paths,
                ConvHelper.FileConvSpec? exportSpec, ParamsBag parms) {
            // Allow slash or colon as separators.  Ignore case.
            List<Glob> globList;
            try {
                globList = Glob.GenerateGlobSet(paths, Glob.STD_DIR_SEP_CHARS, true);
            } catch (ArgumentException ex) {
                Console.Error.WriteLine("Error: " + ex.Message);
                return false;
            }

            string opStr = (exportSpec == null) ? "extracting" : "exporting";
            ExtractFileWorker worker = new ExtractFileWorker(
                delegate (CallbackFacts what) {
                    return Misc.HandleCallback(what, opStr, parms);
                },
                parms.AppHook);
            worker.IsMacZipEnabled = parms.MacZip;
            worker.Preserve = parms.Preserve;
            worker.RawMode = parms.Raw;
            worker.StripPaths = parms.StripPaths;

            if (!ExtArchive.OpenExtArc(extArchive, true, true, parms, out DiskArcNode? rootNode,
                    out DiskArcNode? leafNode, out object? leaf, out IFileEntry endDirEntry)) {
                return false;
            }
            using (rootNode) {
                if (leaf is IArchive) {
                    IArchive arc = (IArchive)leaf;
                    if (!Misc.StdChecks(arc, needWrite: false, needMulti: false)) {
                        return false;
                    }
                    List<IFileEntry> entries =
                        FindEntries.FindMatchingEntries(arc, globList, parms.Recurse);
                    if (!Misc.CheckGlobList(globList)) {
                        return false;
                    }
                    if (entries.Count == 0) {
                        // We didn't fail to match, so this should only be possible if there
                        // was no filespec, i.e. the archive is empty.
                        if (parms.Verbose) {
                            Console.WriteLine("Archive is empty");
                        }
                        return true;
                    }
                    if (!worker.ExtractFromArchive(arc, entries, exportSpec,
                            out bool wasCancelled)) {
                        if (wasCancelled) {
                            Console.Error.WriteLine("Cancelled.");
                        }
                        return false;
                    }
                } else {
                    IFileSystem? fs = Misc.GetTopFileSystem(leaf);
                    if (!Misc.StdChecks(fs, needWrite: false, parms.FastScan)) {
                        return false;
                    }
                    List<IFileEntry> entries =
                        FindEntries.FindMatchingEntries(fs, endDirEntry, globList, parms.Recurse);
                    if (!Misc.CheckGlobList(globList)) {
                        return false;
                    }
                    if (entries.Count == 0) {
                        if (parms.Verbose) {
                            Console.WriteLine("Filesystem is empty");
                        }
                        return true;
                    }
                    if (!worker.ExtractFromDisk(fs, entries, endDirEntry, exportSpec,
                            out bool wasCancelled)) {
                        if (wasCancelled) {
                            Console.Error.WriteLine("Cancelled.");
                        }
                        return false;
                    }
                }
            }

            return true;
        }
    }
}
