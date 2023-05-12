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
using DiskArc.FS;
using static DiskArc.Defs;

namespace cp2 {
    /// <summary>
    /// Handles the "print" command.
    /// </summary>
    internal static class Print {
        public static bool HandlePrint(string cmdName, string[] args, ParamsBag parms) {
            if (args.Length < 1) {
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

                    return PrintArchiveFiles(arc, entries, parms);
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

                    return PrintDiskFiles(fs, entries, parms);
                }
            }
        }

        private enum CharEncoding { Unknown = 0, ASCII, HighASCII, MacOSRoman };

        private static bool PrintArchiveFiles(IArchive arc, List<IFileEntry> entries,
                ParamsBag parms) {
            foreach (IFileEntry entry in entries) {
                if (!entry.HasDataFork) {
                    continue;
                }
                if (arc is Zip && parms.MacZip) {
                    // Skip header entries.
                    if (entry.IsMacZipHeader()) {
                        continue;
                    }
                }

                using (Stream stream = arc.OpenPart(entry, FilePart.DataFork)) {
                    if (!PrintStream(stream, entry.FullPathName, CharEncoding.ASCII)) {
                        return false;
                    }
                }
            }
            return true;
        }

        private static bool PrintDiskFiles(IFileSystem fs, List<IFileEntry> entries,
                ParamsBag parms) {
            foreach (IFileEntry entry in entries) {
                if (!entry.HasDataFork) {
                    continue;
                }

                CharEncoding enc;
                if (fs is DOS) {
                    enc = CharEncoding.HighASCII;
                } else if (fs is HFS) {
                    enc = CharEncoding.MacOSRoman;
                } else {
                    enc = CharEncoding.ASCII;
                }
                using (Stream stream = fs.OpenFile(entry, IFileSystem.FileAccessMode.ReadOnly,
                        FilePart.DataFork)) {
                    if (!PrintStream(stream, entry.FullPathName, enc)) {
                        return false;
                    }
                }
            }
            return true;
        }

        private static bool PrintStream(Stream stream, string pathName, CharEncoding enc) {
            const char CR = '\r';
            const char LF = '\n';

            StringBuilder line = new StringBuilder();
            try {
                bool lastWasCR = false;

                while (true) {
                    int ic = stream.ReadByte();
                    if (ic < 0) {
                        break;
                    }
                    if (enc == CharEncoding.HighASCII) {
                        ic &= 0x7f;
                    }
                    if (ic == CR) {
                        Console.WriteLine(line.ToString());
                        line.Clear();
                    } else if (ic == LF) {
                        if (lastWasCR) {
                            // Found second half of CRLF, do nothing.
                        } else {
                            Console.WriteLine(line.ToString());
                            line.Clear();
                        }
                    } else if (ic < 0x20) {
                        line.Append((char)(ic + ASCIIUtil.CTRL_PIC_START));
                    } else if (ic == 0x7f) {
                        line.Append(ASCIIUtil.CTRL_PIC_DEL);
                    } else {
                        switch (enc) {
                            case CharEncoding.ASCII:
                            case CharEncoding.HighASCII:
                                line.Append((char)ic);
                                break;
                            case CharEncoding.MacOSRoman:
                                line.Append(MacChar.MacToUnicode((byte)ic, MacChar.Encoding.Roman));
                                break;
                            default:
                                Debug.Assert(false);
                                break;
                        }
                    }
                    lastWasCR = (ic == CR);
                }
                if (line.Length > 0) {
                    // Didn't end with an EOL marker.  Output the leftovers.
                    Console.WriteLine(line.ToString());
                }
            } catch (IOException ex) {
                // Bad blocks on disk, bad compression in archive, ...
                Console.Error.WriteLine("Error in '" + pathName + "': " + ex.Message);
                return false;
            }
            return true;
        }
    }
}
