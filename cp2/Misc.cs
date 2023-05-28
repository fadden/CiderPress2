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
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;

using AppCommon;
using CommonUtil;
using DiskArc;
using DiskArc.Multi;
using static DiskArc.Defs;

namespace cp2 {
    /// <summary>
    /// Miscellaneous helper functions.
    /// </summary>
    internal static class Misc {
        /// <summary>
        /// Finds the relevant filesystem in a disk image or partition object.
        /// </summary>
        public static IFileSystem? GetTopFileSystem(object? leaf) {
            if (leaf == null || leaf is IArchive) {
                return null;
            }
            if (leaf is IDiskImage) {
                IDiskImage disk = (IDiskImage)leaf;
                if (disk.Contents is IFileSystem) {
                    return ((IFileSystem)disk.Contents);
                } else if (disk.Contents is IMultiPart) {
                    Console.Error.WriteLine("Error: can't perform operation on partition map");
                    return null;
                } else {
                    Console.Error.WriteLine("Error: filesystem could not be identified");
                    return null;
                }
            } else if (leaf is Partition) {
                Partition part = (Partition)leaf;
                if (part.FileSystem == null) {
                    part.AnalyzePartition();
                }
                if (part.FileSystem == null) {
                    Console.Error.WriteLine("Error: partition filesystem not identified");
                }
                return part.FileSystem;
            } else {
                Console.Error.WriteLine("Internal issue: what is " + leaf);
                Debug.Assert(false);
                return null;
            }
        }

        /// <summary>
        /// Attempts to determine whether two filenames reference the same file.  The file(s)
        /// must exist.
        /// </summary>
        /// <remarks>
        /// <para>For our purposes, treating a single file as two different files is the bad
        /// case (because it means we could open it in two separate instances), so we want to
        /// err on the side of declaring two things as being equal.  For that reason, the
        /// filename comparison is done in a case-insensitive fashion, even for case-sensitive
        /// filesystems.</para>
        /// </remarks>
        /// <param name="pathName1">First pathname.</param>
        /// <param name="pathName2">Second pathname.</param>
        /// <param name="isSame">Result: true if the files are the same.</param>
        /// <returns>True if a determination could be made, false if one or both files don't
        ///   exist.</returns>
        public static bool IsSameFile(string pathName1, string pathName2, out bool isSame) {
            isSame = false;
            if (!File.Exists(pathName1) || !File.Exists(pathName2)) {
                return false;
            }
            // This normalizes all of the components.  Apparently it doesn't strip off
            // trailing slashes, so "foo/bar" and "foo/bar/" would appear to be different
            // files, but the previous test for file (not directory) existence should have
            // handled that.  This normalizes the directory separator characters and
            // resolves things like "..".
            string full1 = Path.GetFullPath(pathName1);
            string full2 = Path.GetFullPath(pathName2);
            isSame = full1.Equals(full2, StringComparison.InvariantCultureIgnoreCase);
            return true;
        }

        /// <summary>
        /// Checks the entries in the Glob list to see if they have been successfully matched.
        /// Reports an error on console if an unmatched pattern is found.
        /// </summary>
        /// <param name="globList"></param>
        public static bool CheckGlobList(List<Glob> globList) {
            foreach (Glob glob in globList) {
                if (!glob.HasMatched) {
                    Console.Error.WriteLine("No matches found for '" + glob.Pattern + "'");
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Handles callback from the various worker classes.  This is responsible for notifying
        /// the user of progress and collecting input when in interactive mode.
        /// </summary>
        public static CallbackFacts.Results HandleCallback(CallbackFacts what, string actionStr,
                ParamsBag parms) {
            CallbackFacts.Results result = CallbackFacts.Results.Unknown;
            switch (what.Reason) {
                case CallbackFacts.Reasons.Progress:
                    if (parms.Verbose) {
                        string origPath = what.OrigPathName;
                        if (string.IsNullOrEmpty(origPath)) {
                            origPath = "''";
                        }
                        string fmtStr = "{1} {2}{3}";
                        //string fmtStr = "{0,3:D}% {1} {2}{3}";    // show progress percent
                        string msg = string.Format(fmtStr, what.ProgressPercent, actionStr,
                            origPath, what.Part == FilePart.RsrcFork ? " (rsrc)" : "");
                        if (what.NewPathName.Length > 0 &&
                                PathName.ComparePathNames(what.OrigPathName, what.OrigDirSep,
                                    what.NewPathName, what.NewDirSep,
                                PathName.CompareAlgorithm.OrdinalIgnoreCase) != 0) {
                            msg += " -> " + what.NewPathName;
                        }

                        // Report DOS conversion; used when copying to/from DOS disks.
                        if (what.DOSConv == CallbackFacts.DOSConvMode.FromDOS) {
                            msg += " (-> low ASCII)";
                        } else if (what.DOSConv == CallbackFacts.DOSConvMode.ToDOS) {
                            msg += " (-> high ASCII)";
                        } else {
                            Debug.Assert(what.DOSConv == CallbackFacts.DOSConvMode.None ||
                                what.DOSConv == CallbackFacts.DOSConvMode.Unknown);
                        }

                        if (!string.IsNullOrEmpty(what.ConvTag)) {
                            msg += " [" + what.ConvTag + "]";
                        }

                        Console.WriteLine(msg);
                    }
                    break;
                case CallbackFacts.Reasons.ResourceForkIgnored:
                    Console.Error.WriteLine("Ignoring resource fork: '" + what.OrigPathName + "'");
                    break;
                case CallbackFacts.Reasons.PathTooLong:
                    Console.Error.WriteLine("Name too long: '" + what.OrigPathName + "'");
                    result = CallbackFacts.Results.Cancel;
                    break;
                case CallbackFacts.Reasons.FileNameExists:
                    // This one requires a result code.
                    if (parms.Overwrite) {
                        if (parms.Verbose) {
                            Console.WriteLine("Overwriting '" + what.OrigPathName + "'");
                        }
                        result = CallbackFacts.Results.Overwrite;
                    } else if (parms.Interactive) {
                        // TODO: can implement overwrite-all or skip-all by changing the values
                        //   of parms.Overwrite and parms.Interactive
                        Console.WriteLine("Overwrite '" + what.OrigPathName + "'");
                        Console.Write("  Yes, no, cancel? ");
                        string? input = Console.ReadLine();
                        char action;
                        if (string.IsNullOrEmpty(input)) {
                            action = 'n';
                        } else {
                            action = char.ToLower(input[0]);
                        }
                        switch (action) {
                            case 'c':
                                result = CallbackFacts.Results.Cancel;
                                break;
                            case 'y':
                                result = CallbackFacts.Results.Overwrite;
                                break;
                            case 'n':
                            default:
                                result = CallbackFacts.Results.Skip;
                                break;
                        }
                    } else {
                        Console.WriteLine("File exists, not overwriting '" +
                            what.OrigPathName + "'");
                        result = CallbackFacts.Results.Skip;
                    }
                    break;
                case CallbackFacts.Reasons.AttrFailure:
                    Console.Error.WriteLine("Unable to set all file attributes: " +
                        what.OrigPathName);
                    break;
                case CallbackFacts.Reasons.OverwriteFailure:
                    Console.Error.WriteLine("Unable to overwrite existing file: " +
                        what.FailMessage + ": '" + what.OrigPathName + "'");
                    break;
                case CallbackFacts.Reasons.Failure:
                    Console.Error.WriteLine("Failed: " + what.FailMessage);
                    break;
                default:
                    Console.Error.WriteLine("Unknown callback reason: " + what.Reason);
                    break;
            }
            return result;
        }

        /// <summary>
        /// Performs a few checks on an IArchive.
        /// </summary>
        /// <param name="archive">Archive to check.</param>
        /// <param name="needWrite">If true, confirm that the archive is modifiable.</param>
        /// <param name="needMulti">If true, confirm that the archive is multi-file.</param>
        /// <returns>True if all is well.</returns>
        public static bool StdChecks(IArchive archive, bool needWrite, bool needMulti) {
            if (needWrite) {
                if (!archive.Characteristics.CanWrite) {
                    Console.Error.WriteLine("Error: cannot modify this type of archive");
                    return false;
                }
                if (archive.IsDubious) {
                    Console.Error.WriteLine("Error: archive is damaged, cannot modify");
                    return false;
                }
            }
            if (needMulti) {
                if (archive.Characteristics.HasSingleEntry) {
                    Console.Error.WriteLine(
                        "Error: cannot perform this operation on this type of archive");
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Performs a few checks on an IDiskImage.
        /// </summary>
        /// <param name="image">Disk image reference.</param>
        /// <param name="needWrite">If true, confirm that the disk image is modifiable.</param>
        /// <returns>True if all is well.</returns>
        public static bool StdChecks(IDiskImage image, bool needWrite) {
            if (needWrite) {
                if (image.IsReadOnly) {
                    Console.Error.WriteLine("Error: disk image is read-only");
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Performs a few checks on an IFileSystem.  Prepares it for file access.
        /// </summary>
        /// <param name="fs">Filesystem reference.  Will be checked for null.</param>
        /// <param name="needWrite">If true, confirm that the filesystem is modifiable.</param>
        /// <param name="fastScan">If true, suppress detailed filesystem scan.</param>
        /// <returns>True if all is well.</returns>
        public static bool StdChecks([NotNullWhen(true)] IFileSystem? fs, bool needWrite,
                bool fastScan) {
            if (fs == null) {
                // Console message should have been written earlier.
                return false;
            }
            fs.PrepareFileAccess(!fastScan);
            if (needWrite) {
                if (!fs.Characteristics.CanWrite) {
                    Console.Error.WriteLine("Error: cannot modify this type of filesystem");
                    return false;
                }
                if (fs.IsDubious) {
                    Console.Error.WriteLine("Error: filesystem is damaged, cannot modify");
                    return false;
                }
                if (fs.IsReadOnly) {
                    // Can happen with HFS if disk was not cleanly unmounted.
                    Console.Error.WriteLine("Error: filesystem is read-only");
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Generates a hex dump of the contents of a stream, and writes it to the console.
        /// The output does not include ASCII data.
        /// </summary>
        /// <param name="stream">Stream to display.</param>
        public static void OutputStreamHexDump(Stream stream) {
            int addr = 0;
            int val;
            StringBuilder sb = new StringBuilder();
            while ((val = stream.ReadByte()) >= 0) {
                if ((addr & 0x0f) == 0) {
                    // New line.
                    if (sb.Length != 0) {
                        Console.WriteLine(sb.ToString());
                        sb.Clear();
                    }
                    sb.Append(addr.ToString("x4"));
                    sb.Append(":");
                }
                sb.Append(' ');
                sb.Append(val.ToString("x2"));
                addr++;
            }
            if ((addr & 0x0f) != 0) {
                Console.WriteLine(sb.ToString());
            }
        }

        /// <summary>
        /// Reads a hex dump from a file, converting it back to binary data.
        /// </summary>
        /// <param name="pathName">Path to file, or "-" to read from stdin.</param>
        /// <param name="expectedLength">Expected length of data.</param>
        /// <param name="buf">Result: buffer with data.</param>
        /// <returns>Number of bytes actually read, or -1 on error.</returns>
        public static int ReadHexDump(string pathName, int expectedLength, out byte[] buf) {
            Debug.Assert(expectedLength > 0);       // could allow -1 to mean "undefined length"
            buf = new byte[expectedLength];

            TextReader? reader = null;
            int bufIndex = 0;
            try {
                if (pathName == "-") {
                    reader = Console.In;
                } else {
                    try {
                        reader = new StreamReader(new FileStream(pathName, FileMode.Open));
                    } catch (Exception ex) {
                        Console.Error.WriteLine("Error: unable to open input file: " + ex.Message);
                        return -1;
                    }
                }
                string? line;
                while ((line = reader.ReadLine()) != null) {
                    if (line.Length == 0 || line[0] == '#') {
                        continue;       // skip blank lines and comments
                    }
                    if (bufIndex >= expectedLength) {
                        Console.WriteLine("Ignoring excess text: '" + line + "'");
                    } else {
                        int count = ParseHexDumpLine(line, buf, bufIndex, out int unused);
                        if (count > 0) {
                            bufIndex += count;
                        } else {
                            Console.WriteLine("Ignoring garbage: '" + line + "'");
                        }
                    }
                }
            } finally {
                reader?.Dispose();
            }

            return bufIndex;
        }

        /// <summary>
        /// <para>This captures two groups:</para>
        /// <list type="number">
        ///   <item>Four- to six-digit hexadecimal address.</item>
        ///   <item>One to 16 occurrences of " XX" hex values.</item>
        /// </list>
        /// <para>The address must be followed by ":" (non-capturing group).</para>
        /// <para>All of the bytes are captured as a single group, which can trivially be
        /// split into individual bytes with Split().</para>
        /// <para>The count limit in the regex should hex-byte-like values in the ASCII area
        /// from being interpreted as bytes.  Lines with fewer than 16 sets of bytes need to
        /// have more than one space between the bytes and the ASCII data.</para>
        /// </summary>
        private static readonly Regex sHexDumpRegex = new Regex(HEX_DUMP_PATTERN);
        private const string HEX_DUMP_PATTERN =
            @"^([0-9A-Fa-f]{4,6}):((?: [0-9A-Fa-f]{2}){1,16}).*$";
        private const int BYTES_PER_LINE = 16;

        /// <summary>
        /// Parses a line from a hex dump.
        /// </summary>
        /// <remarks>
        /// Sample input:
        /// <code>000020: 3a b0 0e a9 03 8d 00 08 e6 3d a5 49 48 a9 5b 48  :0.)....f=%IH)[H</code>
        /// The number of bytes ranges from 1 to 16.
        /// </remarks>
        /// <param name="line">Line to parse.</param>
        /// <param name="buf">Buffer to receive up to 16 bytes.</param>
        /// <param name="offset">Starting offset within buffer.</param>
        /// <param name="address">Result: address found.</param>
        /// <returns>Number of bytes found, or -1 on parsing error.</returns>
        public static int ParseHexDumpLine(string line, byte[] buf, int offset, out int address) {
            address = -1;

            MatchCollection matches = sHexDumpRegex.Matches(line);
            if (matches.Count != 1) {
                return -1;
            }
            string addrStr = matches[0].Groups[1].Value;
            string bytesString = matches[0].Groups[2].Value;

            try {
                address = Convert.ToInt32(addrStr, 16);

                string[] byteStrs = bytesString.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (byteStrs.Length == 0 || byteStrs.Length > BYTES_PER_LINE) {
                    return -1;
                }
                if (offset + byteStrs.Length > buf.Length) {
                    Debug.WriteLine("Hex parse buffer overrun");
                    return -1;
                }
                for (int i = 0; i < byteStrs.Length; i++) {
                    buf[offset + i] = Convert.ToByte(byteStrs[i], 16);
                }
                return byteStrs.Length;
            } catch {
                // Not expected, since the regex checked everything out for us.
                Debug.Assert(false, "hex parsing conversion error");
                return -1;
            }
        }
    }
}
