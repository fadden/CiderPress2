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
using System.Runtime.InteropServices;
using System.Text;

using AppCommon;
using CommonUtil;
using DiskArc;
using DiskArc.Arc;
using DiskArc.Disk;
using DiskArc.FS;
using DiskArc.Multi;
using static DiskArc.Defs;
using static DiskArc.IFileSystem;

namespace cp2 {
    /// <summary>
    /// Disk catalog routines.
    /// </summary>
    internal static class Catalog {
        #region List

        /// <summary>
        /// Handles the "list" command.
        /// </summary>
        /// <remarks>
        /// This is a simple list that could be piped into some other command, so we want one
        /// filename per line with no embellishment.  Pathnames in disk filesystems should be
        /// displayed with ':' so they can be parsed by the ExtArchive code.
        /// </remarks>
        public static bool HandleList(string cmdName, string[] args, ParamsBag parms) {
            Debug.Assert(args.Length == 1);
            if (!ExtArchive.OpenExtArc(args[0], false, true, parms, out DiskArcNode? rootNode,
                    out DiskArcNode? leafNode,  out object? leaf, out IFileEntry endDirEntry)) {
                return false;
            }
            Debug.Assert(rootNode != null && leaf != null);

            using (rootNode) {
                if (leaf is IArchive) {
                    IArchive archive = (IArchive)leaf;
                    foreach (IFileEntry entry in archive) {
                        if (leaf is Zip && parms.MacZip && !entry.IsDirectory) {
                            char zipDirSep = archive.Characteristics.DefaultDirSep;     // '/'
                            if (entry.IsMacZipHeader()) {
                                // Don't show this one; we'll include it with the main entry.
                                // TODO: show it if there is no main entry.
                                continue;
                            }
                        }
                        Console.WriteLine(entry.FileName);
                    }
                    return true;
                } else if (leaf is IDiskImage) {
                    IDiskImage disk = (IDiskImage)leaf;
                    if (disk.Contents is IFileSystem) {
                        return ListFileSystem((IFileSystem)disk.Contents, parms);
                    } else if (disk.Contents is IMultiPart) {
                        // Just show the numbers.  (APM names might be nicer, but they're not
                        // required to be unique.)
                        IMultiPart parts = (IMultiPart)disk.Contents;
                        for (int i = 0; i < parts.Count; i++) {
                            Console.WriteLine(i + (ExtArchive.ONE_BASED_INDEX ? 1 : 0));
                        }
                        return true;
                    } else {
                        // Can't figure this one out.  We need to differentiate this from the
                        // case of an empty volume, so report an error.
                        Console.Error.WriteLine("Error: disk image contents not recognized");
                        return false;
                    }
                } else if (leaf is Partition) {
                    Partition part = (Partition)leaf;
                    if (part.FileSystem == null) {
                        part.AnalyzePartition();
                    }
                    return ListFileSystem(part.FileSystem, parms);
                } else {
                    Console.Error.WriteLine("Internal issue: what is " + leaf);
                    Debug.Assert(false);
                    return false;
                }
            }
        }

        /// <summary>
        /// Lists the contents of a filesystem.  Does not descend into embedded volumes.
        /// </summary>
        private static bool ListFileSystem(IFileSystem? fs, ParamsBag parms) {
            if (fs == null) {
                return false;
            }
            fs.PrepareFileAccess(!parms.FastScan);
            ListDirContents(fs, fs.GetVolDirEntry(), string.Empty);
            return true;
        }
        private static void ListDirContents(IFileSystem fs, IFileEntry dirEntry, string prefix) {
            Debug.Assert(dirEntry.IsDirectory);
            foreach (IFileEntry entry in dirEntry) {
                //string fileName =
                //    AWName.ChangeAppleWorksCase(entry.FileName, entry.FileType, entry.AuxType);
                string fileName = entry.FileName;
                Console.WriteLine(prefix + fileName);
                if (entry.IsDirectory) {
                    // Use the ExtArchive SPLIT_CHAR so the output can be used to form names.
                    ListDirContents(fs, entry, prefix + entry.FileName + ExtArchive.SPLIT_CHAR);
                }
            }
        }

        #endregion List

        #region Catalog

        private const string INDENT_START = "";

        /// <summary>
        /// Indentation level.
        /// </summary>
        private const string INDENT_MORE = "  ";

        /// <summary>
        /// Pathname separator character, to use when displaying files in disk archives.
        /// </summary>
        internal const char PATH_SEP_CHAR = ':';


        /// <summary>
        /// Handles the "catalog" command.
        /// </summary>
        public static bool HandleCatalog(string cmdName, string[] args, ParamsBag parms) {
            Debug.Assert(args.Length == 1);
            string extArcName = args[0];

            if (!ExtArchive.OpenExtArc(extArcName, false, true, parms, out DiskArcNode? rootNode,
                    out DiskArcNode? leafNode, out object? leaf, out IFileEntry endDirEntry)) {
                return false;
            }
            Debug.Assert(rootNode != null && leaf != null);

            using (rootNode) {
                Console.WriteLine("File: " + extArcName);
                if (leaf is IArchive) {
                    CatalogArchive((IArchive)leaf, extArcName, INDENT_START, parms);
                } else if (leaf is IDiskImage) {
                    CatalogDiskImage((IDiskImage)leaf, INDENT_START, parms);
                } else if (leaf is Partition) {
                    CatalogPartition((Partition)leaf, INDENT_START, "Partition", parms);
                } else {
                    Console.Error.WriteLine("Internal issue: what is " + leaf);
                    Debug.Assert(false);
                    return false;
                }
            }

            return true;
        }

        private const string ARCHIVE_HDR =
            "Type Auxtyp Modified        Format    Length Size Name                          ";
        private const string ARCHIVE_LINES =
            "---- ------ --------------- ------- -------- ---- ------------------------------";
        // 0=name, 1=type, 2=aux, 3=mod, 4=fmt, 5=len, 6=comp%
        private const string ARCHIVE_FMT =
            "{1,-5}{2,-6} {3,-15} {4,-7} {5,8} {6,4} {0}";

        private const string ARCHIVE_HDR_WIDE =
            "Typ Aux  HTyp Crea Access Created         Modified        " +
            "D-Length   D-Size D-Fmt   R-Length   R-Size R-Fmt   Name";
        private const string ARCHIVE_LINES_WIDE =
            "--- ---- ---- ---- ------ --------------- --------------- " +
            "-------- -------- ------- -------- -------- ------- ----";
        // 0=name, 1=type, 2=aux, 3=hfstype, 4=hfscre, 5=acc, 6=creat, 7=mod,
        //   8=d-len, 9=d-size, 10=d-fmt, 11=r-len, 12=r-size, 13=r-fmt
        private const string ARCHIVE_FMT_WIDE =
            "{1,-3} {2,-4:x4} {3,-4} {4,-4} {5,-6} {6,-15} {7,-15} " +
            "{8,8} {9,8} {10,-7} {11,8} {12,8} {13,-7} {0}";

        /// <summary>
        /// Formats the contents of a file archive.
        /// </summary>
        private static void CatalogArchive(IArchive archive, string gzipName, string indent,
                ParamsBag parms) {
            if (parms.ShowNotes) {
                if (archive.Notes.Count != 0) {
                    Console.WriteLine(indent + "  Archive notes:");
                    List<Notes.Note> noteList = archive.Notes.GetNotes();
                    foreach (Notes.Note note in noteList) {
                        Console.WriteLine(indent + "   " + note.ToString());
                    }
                }
            }

            if (archive.IsDubious) {
                Console.WriteLine(indent + "  Warning: archive flagged as dubious");
            }
            if (archive is not GZip || !parms.SkipSimple) {
                if (parms.Wide) {
                    Console.WriteLine(indent + ARCHIVE_HDR_WIDE);
                    Console.WriteLine(indent + ARCHIVE_LINES_WIDE);
                } else {
                    Console.WriteLine(indent + ARCHIVE_HDR);
                    Console.WriteLine(indent + ARCHIVE_LINES);
                }

                foreach (IFileEntry entry in archive) {
                    string name = entry.FileName;

                    // Copy attributes into a FileAttribs, so we can override them for MacZip.
                    FileAttribs attrs = new FileAttribs(entry);

                    // Handle MacZip archives, if enabled.
                    IFileEntry adfEntry = IFileEntry.NO_ENTRY;
                    if (archive is Zip && parms.MacZip && !entry.IsDirectory) {
                        char zipDirSep = archive.Characteristics.DefaultDirSep;     // '/'

                        // Only show __MACOSX/ entries when paired with other entries.
                        // TODO: show them anyway if the main entry doesn't exist.
                        if (entry.IsMacZipHeader()) {
                            continue;
                        }
                        string macZipName = Zip.GenerateMacZipName(entry.FullPathName);
                        if (!string.IsNullOrEmpty(macZipName) &&
                                archive.TryFindFileEntry(macZipName, out adfEntry)) {
                            try {
                                // Can't use un-seekable archive stream as archive source.
                                using (Stream adfStream = ArcTemp.ExtractToTemp(archive,
                                        adfEntry, FilePart.DataFork)) {
                                    attrs.GetFromAppleSingle(adfStream, parms.AppHook);
                                }
                            } catch (Exception ex) {
                                // Never mind.
                                Console.Error.WriteLine("Unable to get ADF attrs for '" +
                                    entry.FullPathName + "': " + ex.Message);
                            }
                        }
                    }

                    //if (name.Length > 32) {
                    //    name = ".." + name.Substring(name.Length - 30);
                    //}

                    byte access;
                    byte proType;
                    ushort proAux;
                    uint hfsType, hfsCreator;
                    string type, aux;
                    if (adfEntry == IFileEntry.NO_ENTRY) {
                        proType = entry.FileType;
                        proAux = entry.AuxType;
                        hfsType = entry.HFSFileType;
                        hfsCreator = entry.HFSCreator;
                        access = entry.Access;
                        GetFileTypeStrings(entry, out type, out aux);
                    } else {
                        // Use the file type information from the AppleDouble header.
                        proType = attrs.FileType;
                        proAux = attrs.AuxType;
                        hfsType = attrs.HFSFileType;
                        hfsCreator = attrs.HFSCreator;
                        access = attrs.Access;
                        GetFileTypeStrings(attrs, attrs.RsrcLength > 0, out type, out aux);
                    }

                    string creat = FormatDateTime(attrs.CreateWhen);
                    string mod = FormatDateTime(attrs.ModWhen);

                    long dataLen, rsrcLen, dataSize, rsrcSize;
                    CompressionFormat dataFmt, rsrcFmt, bestFmt;

                    if (adfEntry == IFileEntry.NO_ENTRY) {
                        if (!entry.GetPartInfo(FilePart.DataFork, out dataLen, out dataSize,
                                out dataFmt)) {
                            if (!entry.GetPartInfo(FilePart.DiskImage, out dataLen, out dataSize,
                                    out dataFmt)) {
                                dataLen = dataSize = 0;
                                dataFmt = CompressionFormat.Unknown;
                            }
                        }
                        if (!entry.GetPartInfo(FilePart.RsrcFork, out rsrcLen, out rsrcSize,
                                out rsrcFmt)) {
                            rsrcLen = rsrcSize = 0;
                            rsrcFmt = CompressionFormat.Unknown;
                        }
                    } else {
                        if (!entry.GetPartInfo(FilePart.DataFork, out dataLen, out dataSize,
                                out dataFmt)) {
                            dataLen = dataSize = 0;
                            dataFmt = CompressionFormat.Unknown;
                        }
                        // Use the length/format of the ADF header file in the ZIP archive as
                        // the compressed length/format, since that's the part that's Deflated.
                        if (!adfEntry.GetPartInfo(FilePart.DataFork, out long unused,
                                out rsrcSize, out rsrcFmt)) {
                            // not expected
                            rsrcSize = 0;
                            rsrcFmt = CompressionFormat.Unknown;
                        }
                        rsrcLen = attrs.RsrcLength;      // ADF is not compressed
                    }
                    string dataFmtStr = (dataFmt == CompressionFormat.Unknown ? "(n/a)" :
                        ThingString.CompressionFormat(dataFmt));
                    string rsrcFmtStr = (rsrcFmt == CompressionFormat.Unknown ? "(n/a)" :
                        ThingString.CompressionFormat(rsrcFmt));
                    // Pick the "best" format to display in the basic catalog output.
                    string fmt = "(empty)";
                    bestFmt = CompressionFormat.Unknown;
                    if (dataFmt != CompressionFormat.Unknown) {
                        bestFmt = dataFmt;
                    }
                    if (rsrcFmt != CompressionFormat.Unknown) {
                        if (bestFmt == CompressionFormat.Unknown ||
                                bestFmt == CompressionFormat.Uncompressed) {
                            bestFmt = rsrcFmt;
                        }
                    }
                    fmt = ThingString.CompressionFormat(bestFmt);
                    if (entry.IsDamaged || entry.IsDubious) {
                        fmt = '!' + fmt;
                    }


                    long fullLen = dataLen + rsrcLen;
                    long compLen = dataSize + rsrcSize;
                    string len = fullLen.ToString();

                    string cmpr;
                    if (fullLen < 0) {
                        cmpr = "??%";
                    } else if (fullLen == 0) {
                        if (compLen == 0) {
                            cmpr = "--%";
                        } else {
                            cmpr = "999%";
                        }
                    } else {
                        int compPerc = (fullLen == 0) ? 0 :
                            (int)Math.Round(compLen * 100.0 / fullLen);
                        if (compPerc > 999) {
                            cmpr = "999%";
                        } else if (compPerc == 0 && compLen != 0) {
                            cmpr = "<1%";
                        } else {
                            cmpr = compPerc.ToString() + '%';
                        }
                    }

                    string outStr;
                    if (parms.Wide) {
                        outStr = string.Format(ARCHIVE_FMT_WIDE, name,
                            FileTypes.GetFileTypeAbbrev(proType), proAux,
                            MacChar.StringifyMacConstant(hfsType),
                            MacChar.StringifyMacConstant(hfsCreator),
                            FormatAccessFlags(access), creat, mod,
                            dataLen, dataSize, dataFmtStr,
                            rsrcLen, rsrcSize, rsrcFmtStr);
                    } else {
                        outStr = string.Format(ARCHIVE_FMT, name, type, aux, mod, fmt, len, cmpr);
                    }
                    Console.WriteLine(indent + outStr);
                }
                Console.WriteLine();
            }

            // Dig deeper into ZIP archives, or if Depth=max.
            if ((parms.Depth > ParamsBag.ScanDepth.Shallow && (archive is Zip || archive is GZip))||
                    parms.Depth >= ParamsBag.ScanDepth.Max) {
                // We want to find disk images inside file archives.  We can check by filename
                // extension and filetype/auxtype.
                string newIndent =
                    (archive is GZip && parms.SkipSimple) ? indent : indent + INDENT_MORE;
                foreach (IFileEntry entry in archive) {
                    if (!TryAsDiskImage(archive, entry, gzipName, newIndent, parms)) {
                        if (parms.Depth > ParamsBag.ScanDepth.SubVol) {
                            // Max depth, find archives inside archives.
                            if (!TryAsArchive(archive, entry, gzipName, newIndent, parms)) {
                                if (parms.Debug) {
                                    Console.WriteLine("+++ not recognized as disk or archive");
                                }
                            }
                        }
                    }
                }
            }
        }


        /// <summary>
        /// Attempts to process as a file archive.
        /// </summary>
        /// <param name="arcOrFs">IArchive or IFileSystem instance.</param>
        /// <param name="entry">File entry.</param>
        /// <param name="gzipName">Filename of enclosing archive, required for gzip.</param>
        /// <param name="indent">Indentation level.</param>
        /// <param name="parms">Parameter set.</param>
        /// <returns>True on success.</returns>
        private static bool TryAsArchive(object arcOrFs, IFileEntry entry, string gzipName,
                string indent, ParamsBag parms) {
            if (entry.IsDubious || entry.IsDamaged) {
                if (parms.Debug) {
                    Console.WriteLine("+++ rejected as archive (damage): " + entry.FullPathName);
                }
                return false;
            }
            if (!FileIdentifier.HasFileArchiveAttribs(entry, gzipName, out string ext)) {
                if (parms.Debug) {
                    Console.WriteLine("+++ rejected as archive (attribs): " + entry.FullPathName);
                }
                return false;
            }

            if (parms.Debug) {
                Console.WriteLine("+++ investigating as archive: " + entry.FullPathName);
            }
            try {
                Stream stream;
                if (arcOrFs is IArchive) {
                    stream = ArcTemp.ExtractToTemp((IArchive)arcOrFs, entry, FilePart.DataFork);
                } else if (arcOrFs is IFileSystem && entry.HasDataFork) {
                    stream = ((IFileSystem)arcOrFs).OpenFile(entry, FileAccessMode.ReadOnly,
                        FilePart.DataFork);
                } else {
                    return false;
                }
                using (stream) {
                    using (IDisposable? obj = WorkTree.IdentifyStreamContents(stream, ext,
                            parms.AppHook)) {
                        // Assume we already did TryAsDiskImage, so we won't be finding NuFX disk
                        // images here.
                        if (obj is IArchive) {
                            if (obj is not GZip) {
                                if (arcOrFs is GZip) {
                                    Console.WriteLine(indent + "Catalog of \"" +
                                        GZip.StripGZExtension(gzipName) + "\"");
                                } else {
                                    Console.WriteLine(indent + "Catalog of \"" +
                                        entry.FullPathName + "\"");
                                }
                            }
                            CatalogArchive((IArchive)obj, entry.FileName, indent, parms);
                        }
                    }
                }
            } catch (Exception ex) when
                    (ex is IOException ||
                    ex is InvalidDataException ||
                    ex is NotImplementedException) {
                Console.WriteLine(indent + "Unable to investigate '" +
                    entry.FullPathName + "': " + ex.Message);
                return false;
            } catch (Exception ex) {
                Console.WriteLine(indent + "Unexpected exception examining archive '" +
                    entry.FullPathName + "': " + ex);
                return false;
            }
            return true;
        }

        /// <summary>
        /// Attempts to process an entry as a disk image.
        /// </summary>
        /// <param name="arcOrFs">IArchive or IFileSystem instance.</param>
        /// <param name="entry">File entry.</param>
        /// <param name="gzipName">Filename of enclosing archive, required for gzip.</param>
        /// <param name="indent">Indentation level.</param>
        /// <param name="parms">Parameter set.</param>
        /// <returns>True on success.</returns>
        private static bool TryAsDiskImage(object arcOrFs, IFileEntry entry, string gzipName,
                string indent, ParamsBag parms) {
            if (entry.IsDubious || entry.IsDamaged) {
                if (parms.Debug) {
                    Console.WriteLine("+++ rejected as disk (damage): " + entry.FullPathName);
                }
                return false;
            }
            if (!FileIdentifier.HasDiskImageAttribs(entry, gzipName, out string ext)) {
                if (parms.Debug) {
                    Console.WriteLine("+++ rejected as disk (attribs): " + entry.FullPathName);
                }
                return false;
            }

            if (parms.Debug) {
                Console.WriteLine("+++ investigating as disk: " + entry.FullPathName);
            }
            try {
                Stream stream;
                if (arcOrFs is IArchive) {
                    FilePart part = entry.IsDiskImage ? FilePart.DiskImage : FilePart.DataFork;
                    stream = ArcTemp.ExtractToTemp((IArchive)arcOrFs, entry, part);
                } else if (arcOrFs is IFileSystem && entry.HasDataFork) {
                    stream = ((IFileSystem)arcOrFs).OpenFile(entry, FileAccessMode.ReadOnly,
                        FilePart.DataFork);
                } else {
                    return false;
                }
                using (stream) {
                    using (IDisposable? obj = WorkTree.IdentifyStreamContents(stream, ext,
                            parms.AppHook)) {
                        if (obj is IDiskImage) {
                            if (arcOrFs is GZip) {
                                Console.WriteLine(indent + "Catalog of \"" +
                                    GZip.StripGZExtension(gzipName) + "\"");
                            } else {
                                Console.WriteLine(indent + "Catalog of \"" +
                                    entry.FullPathName + "\"");
                            }
                            CatalogDiskImage((IDiskImage)obj, indent, parms);
                        } else if (obj is NuFX) {
                            if (!parms.SkipSimple) {
                                // TODO:
                                // If we're not skipping simple, we should return false here and
                                // let the archive-dump code display the NuFX, and then dump the
                                // disk when it drops one level down.  However, the "is it an
                                // archive" code rejects .SDK, because we try to treat .SDK as
                                // a disk image since that's what people expect.  This yields
                                // different behavior for .SDK in the host FS vs. in an archive.
                                // Thought: have Has*Attribs() take a flag to toggle .SDK handling.
                            }
                            // If this is a NuFX-compressed disk image, handle it like a disk image.
                            NuFX arc = (NuFX)obj;
                            IEnumerator<IFileEntry> numer = arc.GetEnumerator();
                            if (!numer.MoveNext()) {
                                return false;   // no entries
                            }
                            IFileEntry subEntry = numer.Current;
                            if (!subEntry.IsDiskImage || numer.MoveNext()) {
                                return false;   // not disk image, or multiple entries
                            }
                            if (subEntry.DataLength > 800 * 1024) {
                                // Too big to be an 800K disk; probably a large file archive.
                                return false;
                            }
                            using (Stream subStream = ArcTemp.ExtractToTemp(arc, subEntry,
                                    FilePart.DiskImage)) {
                                using (IDisposable? subDisk = WorkTree.IdentifyStreamContents(
                                      subStream, ".po", parms.AppHook)) {
                                    if (subDisk is IDiskImage) {
                                        Console.WriteLine(indent + "Catalog of \"" +
                                            entry.FullPathName + "\"");
                                        CatalogDiskImage((IDiskImage)subDisk, indent, parms);
                                    }
                                }
                            }
                        }
                    }
                }
            } catch (Exception ex) when
                    (ex is IOException ||
                    ex is InvalidDataException ||
                    ex is NotImplementedException) {
                Console.WriteLine(indent + "Unable to investigate '" +
                    entry.FullPathName + "': " + ex.Message);
                return false;
            } catch (Exception ex) {
                Console.WriteLine(indent + "Unexpected exception examining disk '" +
                    entry.FullPathName + "': " + ex);
                return false;
            }
            return true;
        }

        /// <summary>
        /// Lists the contents of a disk image, which may hold a filesystem or a multi-partition
        /// layout.
        /// </summary>
        private static void CatalogDiskImage(IDiskImage disk, string indent, ParamsBag parms) {
            if (parms.ShowNotes) {
                if (disk.Notes.Count != 0) {
                    Console.WriteLine(indent + "  Disk image notes:");
                    List<Notes.Note> noteList = disk.Notes.GetNotes();
                    foreach (Notes.Note note in noteList) {
                        Console.WriteLine(indent + "   " + note.ToString());
                    }
                }
            }

            if (disk.Contents is IFileSystem) {
                IChunkAccess chunks = ((IFileSystem)disk.Contents).RawAccess;
                StringBuilder sb = new StringBuilder();
                sb.Append("Disk image (");
                sb.Append(ThingString.IDiskImage(disk));
                if (disk is INibbleDataAccess) {
                    if (chunks.NibbleCodec != null) {
                        sb.Append(", codec=" + chunks.NibbleCodec.Name);
                    }
                } else if (chunks.HasSectors && chunks.NumSectorsPerTrack == 16) {
                    sb.Append(", order=" + ThingString.SectorOrder(chunks.FileOrder));
                }
                sb.Append(')');
                ListFileSystem((IFileSystem)disk.Contents, indent, sb.ToString(), parms);
                return;
            } else if (disk.Contents is IMultiPart) {
                IMultiPart parts = (IMultiPart)disk.Contents;
                IChunkAccess chunks = parts.RawAccess;
                StringBuilder sb = new StringBuilder();
                sb.Append("Disk image (");
                sb.Append(ThingString.IDiskImage(disk));
                sb.Append(") ");
                sb.Append(FormatSizeOnDisk(chunks.FormattedLength, 1024));
                sb.Append(" multi-partition (");
                sb.Append(ThingString.IMultiPart(parts));
                sb.Append(')');
                Console.WriteLine(indent + sb.ToString());
                ListMultiPart(parts, indent, parms);
            } else {
                Console.WriteLine(indent + "Unable to recognize disk image contents");
                Console.WriteLine();
                return;
            }
        }

        private const string MULTI_HDR =
            "#  Start block    Count Name             Type";
        private const string MULTI_LINES =
            "-- ----------- -------- ---------------- -------------------";
        // 0=index, 1=start, 2=len, 3=name, 4=type
        private const string MULTI_FMT =
            "{0,2}    {1,8} {2,8} {3,-16} {4,-16}";

        /// <summary>
        /// Displays the contents of an IMultiPart.
        /// </summary>
        private static void ListMultiPart(IMultiPart parts, string indent, ParamsBag parms) {
            if (parts.IsDubious) {
                Console.WriteLine(indent + "  Warning: partition layout flagged as dubious");
            }
            Console.WriteLine(indent + MULTI_HDR);
            Console.WriteLine(indent + MULTI_LINES);

            for (int i = 0; i < parts.Count; i++) {
                Partition part = parts[i];
                int index = i + (ExtArchive.ONE_BASED_INDEX ? 1 : 0);
                string name, type;
                name = type = "-";
                if (part is APM_Partition) {
                    name = ((APM_Partition)part).PartitionName;
                    type = ((APM_Partition)part).PartitionType;
                }
                string outStr = string.Format(MULTI_FMT, index, part.StartOffset / BLOCK_SIZE,
                    part.Length / BLOCK_SIZE, name, type);
                Console.WriteLine(indent + outStr);
            }

            if (parms.Depth >= ParamsBag.ScanDepth.SubVol) {
                Console.WriteLine();
                for (int i = 0; i < parts.Count; i++) {
                    Partition part = parts[i];
                    int index = i + (ExtArchive.ONE_BASED_INDEX ? 1 : 0);
                    CatalogPartition(part, indent + INDENT_MORE, "Partition #" + index, parms);
                }
            }
        }

        /// <summary>
        /// Displays the contents of a Partition.  The IMultiPart may be part of an IDiskImage
        /// (representing a multi-partition disk) or an IFileSystem (for embedded/hybrid volumes).
        /// </summary>
        private static void CatalogPartition(Partition part, string indent, string header,
                ParamsBag parms) {
            // We need to be careful with AnalyzePartition() here, as that could re-analyze an
            // embedded partition, which could be problematic for a DOS hybrid.
            if (part.FileSystem == null) {
                part.AnalyzePartition();
            }
            if (part.FileSystem == null) {
                Console.WriteLine(indent + header + " - " +
                    FormatSizeOnDisk(part.Length, 1024) +
                    " - unable to recognize partition contents");
                Console.WriteLine();
            } else {
                ListFileSystem(part.FileSystem, indent, header, parms);
            }
        }

        /// <summary>
        /// Recursively prints the contents of a filesystem.
        /// </summary>
        private static void ListFileSystem(IFileSystem fs, string indent, string header,
                ParamsBag parms) {
            fs.PrepareFileAccess(!parms.FastScan);
            if (parms.ShowNotes) {
                if (fs.Notes.Count != 0) {
                    Console.WriteLine(indent + "  Filesystem notes:");
                    List<Notes.Note> noteList = fs.Notes.GetNotes();
                    foreach (Notes.Note note in noteList) {
                        Console.WriteLine(indent + "   " + note.ToString());
                    }
                }
            }

            string osString;
            int baseUnit = BLOCK_SIZE;
            if (fs is DOS) {
                if (fs.RawAccess.NumSectorsPerTrack == 13) {
                    osString = "DOS 3.2 Vol " + ((DOS)fs).VolumeNum.ToString("D3");
                } else {
                    osString = "DOS 3.3 Vol " + ((DOS)fs).VolumeNum.ToString("D3");
                }
                baseUnit = SECTOR_SIZE;
            } else if (fs is ProDOS) {
                osString = "ProDOS \"" + fs.GetVolDirEntry().FileName + "\"";
            } else if (fs is HFS) {
                osString = "HFS \"" + fs.GetVolDirEntry().FileName + "\"";
                baseUnit = KBLOCK_SIZE;     // KB is more appropriate than blocks for HFS
            } else {
                osString = ThingString.IFileSystem(fs);
            }

            // TODO(maybe): this shows the total space available in the disk image.  If the ProDOS
            //   volume is smaller than the chunk area, we might want to display that instead.
            long totalLen = fs.RawAccess.FormattedLength;
            long freeLen = fs.FreeSpace;
            StringBuilder sb = new StringBuilder();
            sb.Append(indent);
            sb.Append(header);
            sb.Append(" - ");
            sb.Append(FormatSizeOnDisk(fs.RawAccess.FormattedLength, 1024));
            sb.Append(' ');
            sb.Append(osString);
            sb.Append(", ");
            sb.Append(FormatSizeOnDisk(freeLen, baseUnit));
            sb.Append(" free");
            Console.WriteLine(sb.ToString());
            if (fs.IsDubious) {
                Console.WriteLine(indent + "  Warning: filesystem flagged as dubious");
            }
            if (parms.Wide) {
                Console.WriteLine(indent + FS_HDR_WIDE);
                Console.WriteLine(indent + FS_LINES_WIDE);
            } else {
                Console.WriteLine(indent + FS_HDR);
                Console.WriteLine(indent + FS_LINES);
            }

            // Recursively dump all entries.
            PrintDirContents(fs, fs.GetVolDirEntry(), string.Empty, indent, parms);
            Console.WriteLine();

            // If Depth=max, open up file archives and disk images.
            if (parms.Depth >= ParamsBag.ScanDepth.Max) {
                FindDiskArcInDir(fs, fs.GetVolDirEntry(), string.Empty, indent + INDENT_MORE,
                    parms);
            }

            // Check for embedded volumes.
            if (parms.Depth >= ParamsBag.ScanDepth.SubVol) {
                IMultiPart? embeds = fs.FindEmbeddedVolumes();
                if (embeds != null) {
                    for (int i = 0; i < embeds.Count; i++) {
                        int index = i + (ExtArchive.ONE_BASED_INDEX ? 1 : 0);
                        CatalogPartition(embeds[i], indent + INDENT_MORE, "Embedded #" + index,
                            parms);
                    }
                }
            }
        }

        /// <summary>
        /// Recursively displays the contents of a directory and its subdirectories.
        /// </summary>
        /// <param name="fs">Filesystem object.</param>
        /// <param name="dirEntry">Directory entry to display.</param>
        /// <param name="prefix">String to prefix to output.</param>
        /// <param name="parms">Parameters.</param>
        private static void PrintDirContents(IFileSystem fs, IFileEntry dirEntry,
                string prefix, string indent, ParamsBag parms) {
            Debug.Assert(dirEntry.IsDirectory);
            foreach (IFileEntry entry in dirEntry) {
                PrintFileEntry(fs, entry, prefix, indent, parms);
                if (entry.IsDirectory) {
                    PrintDirContents(fs, entry, prefix + entry.FileName + PATH_SEP_CHAR, indent,
                        parms);
                }
            }
        }

        /// <summary>
        /// Finds disk images and archives in a directory, and formats them.  Used for deeper
        /// scans.
        /// </summary>
        private static void FindDiskArcInDir(IFileSystem fs, IFileEntry dirEntry,
                string prefix, string indent, ParamsBag parms) {
            Debug.Assert(dirEntry.IsDirectory);
            foreach (IFileEntry entry in dirEntry) {
                if (!entry.IsDirectory) {
                    if (!TryAsDiskImage(fs, entry, string.Empty, indent, parms)) {
                        if (parms.Depth > ParamsBag.ScanDepth.SubVol) {
                            TryAsArchive(fs, entry, string.Empty, indent, parms);
                        }
                    }
                } else {
                    FindDiskArcInDir(fs, entry, prefix + entry.FileName + PATH_SEP_CHAR, indent,
                        parms);
                }
            }
        }

        private const string FS_HDR =
            "Type Auxtyp Modified          Length  Storage *Name                             ";
        private const string FS_LINES =
            "---- ------ --------------- -------- -------- ----------------------------------";
        // 0=lck, 1=name, 2=type, 3=aux, 4=mod, 5=len, 6=storage
        private const string FS_FMT =
            "{2,-5}{3,-6} {4,-15} {5,8} {6,8} {0}{1}";

        private const string FS_HDR_WIDE =
            "Typ Aux  HTyp Crea Access Created         Modified        " +
            "D-Length   D-Size R-Length   R-Size Name";
        private const string FS_LINES_WIDE =
            "--- ---- ---- ---- ------ --------------- --------------- " +
            "-------- -------- -------- -------- ----";
        // 0=name, 1=type, 2=aux, 3=hfstype, 4=hfscre, 5=acc, 6=creat, 7=mod,
        //   8=d-len, 9=d-size, 10=r-len, 11=r-size
        private const string FS_FMT_WIDE =
            "{1,-3} {2,-4:x4} {3,-4} {4,-4} {5,-6} {6,-15} {7,-15} " +
            "{8,8} {9,8} {10,8} {11,8} {0}";

        /// <summary>
        /// Formats a filesystem file entry.
        /// </summary>
        private static void PrintFileEntry(IFileSystem fs, IFileEntry entry, string prefix,
                string indent, ParamsBag parms) {
            char lck;
            if (entry.IsDubious) {
                lck = '?';
            } else if (entry.IsDamaged) {
                lck = '!';
            } else {
                lck = (entry.Access & (int)FileAttribs.AccessFlags.Write) == 0 ? '*' : ' ';
            }

            //string name = prefix +
            //    AWName.ChangeAppleWorksCase(entry.FileName, entry.FileType, entry.AuxType);
            string name = prefix + entry.FileName;
            //if (name.Length > 32) {
            //    name = ".." + name.Substring(name.Length - 30);
            //}
            GetFileTypeStrings(entry, out string type, out string aux);

            string creat = FormatDateTime(entry.CreateWhen);
            string mod = FormatDateTime(entry.ModWhen);

            string outStr;
            if (parms.Wide) {
                long dataLen, rsrcLen;
                FilePart dataPart = parms.Raw ? FilePart.RawData : FilePart.DataFork;
                if (!entry.GetPartInfo(dataPart, out dataLen, out long dataSize,
                        out CompressionFormat unusedFmtD)) {
                    dataLen = -1;
                }
                if (!entry.GetPartInfo(FilePart.RsrcFork, out rsrcLen, out long rsrcSize,
                        out CompressionFormat unusedFmtR)) {
                    rsrcLen = -1;
                }
                string hfsType = entry.IsDirectory ? "----" :
                    MacChar.StringifyMacConstant(entry.HFSFileType);
                string hfsCreator = entry.IsDirectory ? "----" :
                    MacChar.StringifyMacConstant(entry.HFSCreator);
                string proType = entry.IsDirectory ? "DIR" :
                    FileTypes.GetFileTypeAbbrev(entry.FileType);
                outStr = string.Format(FS_FMT_WIDE, name,
                    proType, entry.AuxType, hfsType, hfsCreator,
                    FormatAccessFlags(entry.Access), creat, mod,
                    dataLen, dataSize, rsrcLen, rsrcSize);
            } else {
                long dataLen;
                FilePart dataPart = parms.Raw ? FilePart.RawData : FilePart.DataFork;
                string len;
                if (entry.GetPartInfo(dataPart, out dataLen, out long unusedSize,
                        out CompressionFormat unusedFmtD)) {
                    len = (dataLen + entry.RsrcLength).ToString();
                } else {
                    dataLen = -1;
                    len = entry.RsrcLength.ToString();
                }
                string stor = entry.StorageSize.ToString();

                outStr = string.Format(FS_FMT, lck, name, type, aux, mod, len, stor);
            }
            Console.WriteLine(indent + outStr);
        }

        #endregion Catalog

        #region MDC

        /// <summary>
        /// Handles the "multi-disk-catalog" command.
        /// </summary>
        /// <remarks>
        /// Most error messages will go to stdout, because we want them to appear in context
        /// with the rest of the output.
        /// </remarks>
        public static bool HandleMultiDiskCatalog(string cmdName, string[] args, ParamsBag parms) {
            DateTime startWhen = DateTime.Now;
            Console.WriteLine("CiderPress II v" + CP2Main.APP_VERSION + " Multi-Disk Catalog (" +
                RuntimeInformation.RuntimeIdentifier + ")");
            string nonStdOpts = string.Empty;
            if (parms.Depth != ParamsBag.ScanDepth.SubVol) {
                nonStdOpts += " --depth=" + parms.Depth;
            }
            if (parms.Wide) {
                nonStdOpts += " --wide" + parms.Depth;
            }
            if (!parms.SkipSimple) {
                nonStdOpts += " --no-skip-simple";
            }
            if (!string.IsNullOrEmpty(nonStdOpts)) {
                Console.WriteLine("Extra options:" + nonStdOpts);
            }
            Console.WriteLine();
            foreach (string fileName in args) {
                ProcessFile(fileName, parms);
            }
            DateTime endWhen = DateTime.Now;
            Console.WriteLine("Scan completed at " + endWhen.ToString("yyyy-MMM-dd HH:mm:ss") +
                ", elapsed time " +
                (endWhen - startWhen).TotalSeconds.ToString("N2") + " seconds.");
            return true;
        }

        /// <summary>
        /// Process a file on disk, for the multi-disk catalog command.
        /// </summary>
        private static bool ProcessFile(string pathName, ParamsBag parms) {
            if (Directory.Exists(pathName)) {
                ProcessDirectory(pathName, parms);
            } else if (File.Exists(pathName)) {
                try {
                    using FileStream arcFile = new FileStream(pathName, FileMode.Open,
                        FileAccess.Read, FileShare.Read);
                    string ext = Path.GetExtension(pathName)!;
                    if (parms.Classic) {
                        // Try to mimic the original.  Unfortunately the Linux and Windows
                        // versions generated different failure messages.
                        const string FAIL_PFX1 = "File: ";
                        const string FAIL_PFX2 = "Failed: ";
                        ClassicFails result = PrintCatalogClassic(arcFile, pathName, ext, parms);
                        switch (result) {
                            case ClassicFails.Success:
                                break;
                            case ClassicFails.NotRecognizedFormat:
                                Console.WriteLine(FAIL_PFX1 + pathName);
                                Console.WriteLine(FAIL_PFX2 + "Unable to open '" + pathName +
                                    "': not a recognized disk image format");
                                break;
                            case ClassicFails.IsFileArchive:
                                Console.WriteLine(FAIL_PFX1 + pathName);
                                Console.WriteLine(FAIL_PFX2 + "Unable to open '" + pathName +
                                    "': this looks like a file archive, not a disk archive");
                                break;
                            case ClassicFails.NotRecognizedFileSystem:
                                Console.WriteLine(FAIL_PFX1 + pathName);
                                Console.WriteLine(FAIL_PFX2 + "Unable to identify filesystem on '"
                                    + pathName + "'");
                                break;
                            default:
                                Console.WriteLine("ERROR: unknown failure");
                                break;
                        }
                        Console.WriteLine();
                    } else {
                        PrintCatalog(arcFile, pathName, ext, parms);
                    }
                } catch (Exception ex) {
                    Console.WriteLine("ERROR: failed processing '" + pathName + "': " + ex.Message);
                }
            } else {
                Console.Error.WriteLine("ERROR: '" + pathName + "' not found");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Process a directory on disk, for the multi-disk catalog command.
        /// </summary>
        private static bool ProcessDirectory(string pathName, ParamsBag parms) {
            // Get a list of all the files in the directory.  Sort them so that we have
            // consistent results across runs.
            string[] names = Directory.GetFileSystemEntries(pathName);
            Array.Sort(names, StringComparer.OrdinalIgnoreCase);

            foreach (string entryPath in names) {
                ProcessFile(entryPath, parms);
            }
            return true;
        }

        /// <summary>
        /// Prints the catalog for the current level.
        /// </summary>
        private static bool PrintCatalog(Stream arcStream, string pathName, string ext,
                ParamsBag parms) {
            using (IDisposable? thing = WorkTree.IdentifyStreamContents(arcStream, ext,
                    parms.AppHook)) {
                // Peel away gzip and .SDK wrapping.
                if (parms.SkipSimple) {
                    if (thing is GZip) {
                        return SkipLayer(thing, pathName, parms);
                    } else if (thing is NuFX) {
                        IEnumerator<IFileEntry> numer = ((IArchive)thing).GetEnumerator();
                        if (numer.MoveNext()) {
                            if (numer.Current.IsDiskImage && !numer.MoveNext()) {
                                // single entry, is disk image
                                return SkipLayer(thing, pathName, parms);
                            }
                        }
                    }
                }

                Console.WriteLine("File: " + pathName);
                if (thing is null) {
                    Console.WriteLine("  File not recognized");
                    Console.WriteLine();
                } else if (thing is IArchive) {
                    CatalogArchive((IArchive)thing, pathName, INDENT_START, parms);
                } else if (thing is IDiskImage) {
                    CatalogDiskImage((IDiskImage)thing, INDENT_START, parms);
                } else if (thing is Partition) {
                    CatalogPartition((Partition)thing, INDENT_START, "Partition", parms);
                } else {
                    Console.Error.WriteLine("Internal issue: what is " + thing);
                    Debug.Assert(false);
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Skips over a .gz or .sdk layer.
        /// </summary>
        private static bool SkipLayer(IDisposable thing, string pathName, ParamsBag parms) {
            IArchive archive = (IArchive)thing;
            IFileEntry entry = archive.GetFirstEntry();
            if (entry == IFileEntry.NO_ENTRY) {
                return false;
            }
            FilePart part = FilePart.DataFork;
            string ext;
            if (archive is GZip) {
                pathName = GZip.StripGZExtension(pathName);
                ext = Path.GetExtension(pathName);
            } else if (archive is NuFX) {
                Debug.Assert(entry.IsDiskImage);
                part = FilePart.DiskImage;
                ext = ".po";
            } else {
                Debug.Assert(false);
                return false;
            }

            // Extract the part.
            Stream stream = ArcTemp.ExtractToTemp(archive, entry, part);

            return PrintCatalog(stream, pathName, ext, parms);
        }

        #endregion MDC

        #region MDC Classic

        private enum ClassicFails {
            Success = 0,
            NotRecognizedFormat,
            IsFileArchive,
            NotRecognizedFileSystem,
        }

        /// <summary>
        /// Generates output that mimics the original MDC.
        /// </summary>
        /// <remarks>
        /// The CiderPress diskimg library was organized differently, so this is a little awkward
        /// in places.
        /// </remarks>
        /// <param name="arcStream">Stream with disk image or file archive.</param>
        /// <param name="pathName">Full or partial path to archive file.</param>
        /// <param name="ext">Filename extension.  Will not match pathName's extension if
        ///   this is extracted content, e.g. an uncompressed gzip file.</param>
        /// <param name="parms">Options, etc.</param>
        /// <returns>True on success, false if unable to display something useful.</returns>
        private static ClassicFails PrintCatalogClassic(Stream arcStream, string pathName,
                string ext, ParamsBag parms) {
            FileAnalyzer.AnalysisResult result = FileAnalyzer.Analyze(arcStream, ext,
                parms.AppHook, out FileKind kind, out SectorOrder orderHint);
            if (result != FileAnalyzer.AnalysisResult.Success) {
                return ClassicFails.NotRecognizedFormat;
            }
            return PrintCatalogClassic2(arcStream, pathName, ext, kind, orderHint, parms);
        }

        private static ClassicFails PrintCatalogClassic2(Stream arcStream, string pathName,
                string? ext, FileKind kind, SectorOrder orderHint, ParamsBag parms) {
            if (IsDiskImageFile(kind)) {
                if (kind == FileKind.Woz) {
                    if (arcStream.Length % 512 == 0) {
                        return ClassicFails.NotRecognizedFileSystem;
                    } else {
                        return ClassicFails.NotRecognizedFormat;
                    }
                }
                using IDiskImage? diskImage =
                    FileAnalyzer.PrepareDiskImage(arcStream, kind, parms.AppHook);
                if (diskImage == null) {
                    Console.WriteLine("ERROR: unable to prepare disk image");
                    return ClassicFails.NotRecognizedFormat;
                }
                diskImage.AnalyzeDisk(null, orderHint, IDiskImage.AnalysisDepth.Full);
                return PrintDiskImageClassic(diskImage, pathName, parms);
            } else {
                // For "classic" mode, we only handle ZIP/NuFX/gzip with a single disk image.
                if (kind != FileKind.Zip && kind != FileKind.NuFX && kind != FileKind.GZip) {
                    return ClassicFails.NotRecognizedFormat;
                }

                using IArchive? archive =
                    FileAnalyzer.PrepareArchive(arcStream, kind, parms.AppHook);
                if (archive == null) {
                    return ClassicFails.NotRecognizedFormat;
                }
                List<IFileEntry> entries = archive.ToList();
                if (entries.Count != 1) {
                    return ClassicFails.IsFileArchive;
                }

                // ZIP/gzip only have data forks.  For NuFX it should be a disk image.
                IFileEntry entry = entries[0];
                FilePart part = FilePart.DataFork;
                if (archive is NuFX) {
                    if (!entry.IsDiskImage) {
                        return ClassicFails.IsFileArchive;
                    }
                    part = FilePart.DiskImage;
                }

                // Extract the entry and catalog it.
                using (Stream tmpStream = ArcTemp.ExtractToTemp(archive, entry, part)) {
                    if (archive is NuFX) {
                        return PrintCatalogClassic2(tmpStream, pathName, string.Empty,
                            FileKind.UnadornedSector, SectorOrder.ProDOS_Block, parms);
                    } else if (archive is GZip) {
                        // Strip the ".gz" off so we can find the "inner" filename extension.
                        string newPathName = GZip.StripGZExtension(pathName);
                        return PrintCatalogClassic(tmpStream, pathName,
                            Path.GetExtension(newPathName), parms);
                    } else {
                        // ZIP archive, could hold anything.
                        return PrintCatalogClassic(tmpStream, entry.FileName,
                            Path.GetExtension(entry.FileName), parms);
                    }
                }
            }
        }

        private const string CLASSIC_HDR =
            " Name                             Type Auxtyp Modified         Format   Length";
        private const string CLASSIC_LINES =
            "------------------------------------------------------------------------------";
        // 0=lock, 1=name, 2=type, 3=aux, 4=mod, 5=fmt, 6=len
        private const string CLASSIC_FILE_FMT =
            "{0}{1,-32} {2,-5}{3,-6} {4,-15}  {5,-6} {6,8}";

        private static ClassicFails PrintDiskImageClassic(IDiskImage diskImage,
                string pathName, ParamsBag parms) {
            string prefix = string.Empty;
            Console.WriteLine("File: " + pathName);
            StringBuilder sb = new StringBuilder();
            sb.Append("Disk: ");

            if (diskImage.Contents is IFileSystem) {
                IFileSystem fs = (IFileSystem)diskImage.Contents;
                fs.PrepareFileAccess(!parms.FastScan);

                if (fs is DOS) {
                    if (fs.RawAccess.NumSectorsPerTrack == 13) {
                        sb.Append("DOS 3.2 ");
                    } else {
                        sb.Append("DOS 3.3 ");
                    }
                    sb.Append("Volume ");
                    sb.Append(((DOS)fs).VolumeNum.ToString("D3"));
                } else if (fs is ProDOS) {
                    sb.Append("ProDOS /");
                    sb.Append(fs.GetVolDirEntry().FileName);
                } else if (fs is HFS) {
                    sb.Append("HFS ");
                    sb.Append(fs.GetVolDirEntry().FileName);
                } else {
                    sb.Append("[???]");
                }
                if (fs.IsDubious) {
                    sb.Append(" [*]");
                }
                sb.AppendFormat(" ({0}KB)", fs.RawAccess.FormattedLength / 1024);
                Console.WriteLine(sb.ToString());
                Console.WriteLine(CLASSIC_HDR);
                Console.WriteLine(CLASSIC_LINES);

                PrintFileSystemClassic(fs, prefix, parms);
            } else if (diskImage.Contents is IMultiPart) {
                IMultiPart mp = (IMultiPart)diskImage.Contents;
                if (mp is APM) {
                    sb.Append("[MacPartition]");
                } else if (mp is AmUniDOS) {
                    sb.Append("[UNIDOS]");
                } else if (mp is OzDOS) {
                    sb.Append("[OzDOS]");
                } else {
                    sb.Append("[???]");
                }
                if (mp.IsDubious) {
                    sb.Append(" [*]");
                }
                sb.AppendFormat(" ({0}KB)", mp.RawAccess.FormattedLength / 1024);
                Console.WriteLine(sb.ToString());
                Console.WriteLine(CLASSIC_HDR);
                Console.WriteLine(CLASSIC_LINES);

                for (int i = 0; i < mp.Count; i++) {
                    Partition part = mp[i];
                    part.AnalyzePartition();
                    if (part.FileSystem != null) {
                        IFileSystem fs = part.FileSystem;
                        fs.PrepareFileAccess(!parms.FastScan);
                        prefix = GetPrefixClassic(fs, i);
                        PrintFileSystemClassic(fs, prefix, parms);
                    }
                }
            } else {
                // Filesystem analysis failed.
                return ClassicFails.NotRecognizedFileSystem;
            }
            Console.WriteLine(CLASSIC_LINES);
            return ClassicFails.Success;
        }

        private static string GetPrefixClassic(IFileSystem fs, int index) {
            string prefix;
            if (fs is DOS) {
                if (index >= 0) {
                    prefix = string.Format("_DOS" + (index+1).ToString("D3") + ":");
                } else {
                    prefix = string.Format("_DOS" +
                        ((DOS)fs).VolumeNum.ToString("D3") + ":");
                }
            } else /*ProDOS, HFS, Pascal*/ {
                prefix = "_" + fs.GetVolDirEntry().FileName + ":";
            }
            return prefix;
        }

        private static void PrintFileSystemClassic(IFileSystem fs, string prefix, ParamsBag parms) {
            PrintDirContentsClassic(fs, fs.GetVolDirEntry(), prefix, parms);

            IMultiPart? embeds = fs.FindEmbeddedVolumes();
            if (embeds != null) {
                for (int i = 0; i < embeds.Count; i++) {
                    Partition part = embeds[i];
                    IFileSystem efs = part.FileSystem!;
                    efs.PrepareFileAccess(!parms.FastScan);
                    string eprefix = GetPrefixClassic(efs, i);
                    PrintDirContentsClassic(efs, efs.GetVolDirEntry(), eprefix, parms);
                }
            }
        }

        /// <summary>
        /// Recursively displays the contents of a directory and its subdirectories.
        /// </summary>
        /// <param name="fs">Filesystem object.</param>
        /// <param name="dirEntry">Directory entry to display.</param>
        /// <param name="prefix">String to prefix to output.</param>
        /// <param name="parms">Parameters.</param>
        private static void PrintDirContentsClassic(IFileSystem fs, IFileEntry dirEntry,
                string prefix, ParamsBag parms) {
            Debug.Assert(dirEntry.IsDirectory);
            foreach (IFileEntry entry in dirEntry) {
                PrintFileEntryClassic(fs, entry, prefix, parms);
                if (entry.IsDirectory) {
                    PrintDirContentsClassic(fs, entry, prefix + entry.FileName + ':', parms);
                }
            }
        }

        private static void PrintFileEntryClassic(IFileSystem fs, IFileEntry entry,
                string prefix, ParamsBag parms) {
            char lck = (entry.Access == 0x01 || entry.Access == 0x21) ? '*' : ' ';

            //string name = prefix +
            //    AWName.ChangeAppleWorksCase(entry.FileName, entry.FileType, entry.AuxType);
            string name = prefix + entry.FileName;
            if (name.Length > 32) {
                name = ".." + name.Substring(name.Length - 30);
            }
            string type, aux;
            if (entry.IsDiskImage) {
                type = "Disk";
                aux = string.Format("{0}k", entry.DataLength / 1024);
            } else if (entry.IsDirectory) {
                type = "DIR ";
                if (fs is HFS) {
                    aux = string.Empty;
                } else {
                    // some ProDOS directories have a nonzero auxtype
                    aux = string.Format("${0:X4}", entry.AuxType);
                }
            } else if (entry.HasProDOSTypes) {
                type = FileTypes.GetFileTypeAbbrev(entry.FileType) +
                    (entry.HasRsrcFork ? '+' : ' ');
                aux = string.Format("${0:X4}", entry.AuxType);
            } else if (entry.HasHFSTypes) {
                if (FileAttribs.ProDOSFromHFS(entry.HFSFileType, entry.HFSCreator,
                        out byte proType, out ushort proAux)) {
                    type = FileTypes.GetFileTypeAbbrev(proType) +
                        (entry.RsrcLength > 0 ? '+' : ' ');
                    aux = string.Format("${0:X4}", proAux);
                } else if (entry.HFSCreator == 0 && entry.HFSFileType == 0) {
                    type = FileTypes.GetFileTypeAbbrev(0x00) +
                        (entry.RsrcLength > 0 ? '+' : ' ');
                    aux = "$0000";
                } else {
                    type = MacChar.StringifyMacConstant(entry.HFSFileType) +
                        (entry.RsrcLength > 0 ? '+' : ' ');
                    aux = ' ' + MacChar.StringifyMacConstant(entry.HFSCreator);
                }
            } else {
                type = "????";
                aux = "!!!!";
            }
            string mod = FormatDateTime(entry.ModWhen);

            string fmt;     // can't use ThingString; must match original
            switch (fs.GetFileSystemType()) {
                case FileSystemType.ProDOS:
                    fmt = "ProDOS";
                    break;
                case FileSystemType.DOS33:
                case FileSystemType.DOS32:
                    fmt = "DOS   ";
                    break;
                case FileSystemType.Pascal:
                    fmt = "Pascal";
                    break;
                case FileSystemType.HFS:
                    fmt = "HFS   ";
                    break;
                case FileSystemType.CPM:
                    fmt = "CP/M  ";
                    break;
                case FileSystemType.MSDOS:
                    fmt = "MS-DOS";
                    break;
                case FileSystemType.RDOS33:
                case FileSystemType.RDOS32:
                case FileSystemType.RDOS3:
                    fmt = "RDOS  ";
                    break;
                default:
                    fmt = "???   ";
                    break;
            }
            if (entry.IsDamaged || entry.IsDubious) {
                fmt = "BROKEN";
            }
            string len = (entry.DataLength + entry.RsrcLength).ToString();

            string outStr = string.Format(CLASSIC_FILE_FMT, lck, name, type, aux, mod, fmt, len);
            Console.WriteLine(outStr);
        }

        #endregion MDC Classic

        #region Utilities

        /// <summary>
        /// Generates file type strings for the file entry.  HFS file types are given priority
        /// over ProDOS file types.  ProDOS types stored in HFS fields are converted.
        /// </summary>
        /// <param name="entry">File entry.</param>
        /// <param name="type">Result: file type string (ProDOS type / HFS file type).</param>
        /// <param name="aux">Result: aux type string (ProDOS auxtype / HFS creator).</param>
        private static void GetFileTypeStrings(IFileEntry entry, out string type, out string aux) {
            if (entry.IsDiskImage) {
                // NuFX disk image, with disk block count in the auxtype field.
                type = "Disk";
                aux = string.Format("{0}k", entry.DataLength / 1024);
            } else if (entry.IsDirectory) {
                // Sometimes ProDOS directories have nonzero aux types.  No need to display them.
                type = "DIR";
                aux = string.Empty;
            } else if (entry is DOS_FileEntry) {
                type = " " + FileTypes.GetDOSTypeAbbrev(entry.FileType);
                aux = string.Format("${0:X4}", entry.AuxType);
            } else if (entry.HasHFSTypes) {
                // See if ProDOS types are buried in the HFS types.
                if (FileAttribs.ProDOSFromHFS(entry.HFSFileType, entry.HFSCreator,
                        out byte proType, out ushort proAux)) {
                    type = FileTypes.GetFileTypeAbbrev(proType) +
                        (entry.RsrcLength > 0 ? '+' : ' ');
                    aux = string.Format("${0:X4}", proAux);
                } else if (entry.HFSCreator == 0 && entry.HFSFileType == 0) {
                    if (entry.HasProDOSTypes) {
                        // Use the ProDOS types instead.  GSHK does this for ProDOS files.
                        type = FileTypes.GetFileTypeAbbrev(entry.FileType) +
                            (entry.HasRsrcFork ? '+' : ' ');
                        aux = string.Format("${0:X4}", entry.AuxType);
                    } else {
                        type = FileTypes.GetFileTypeAbbrev(0x00) +
                            (entry.RsrcLength > 0 ? '+' : ' ');
                        aux = "$0000";
                    }
                } else {
                    // Stringify the HFS types.  No need to show as hex.
                    // All HFS files have a resource fork, so only show a '+' if it has data in it.
                    type = MacChar.StringifyMacConstant(entry.HFSFileType) +
                        (entry.RsrcLength > 0 ? '+' : ' ');
                    aux = ' ' + MacChar.StringifyMacConstant(entry.HFSCreator);
                }
            } else if (entry.HasProDOSTypes) {
                // Show a '+' if a resource fork is present, whether or not it has data.
                type = FileTypes.GetFileTypeAbbrev(entry.FileType) +
                    (entry.HasRsrcFork ? '+' : ' ');
                aux = string.Format("${0:X4}", entry.AuxType);
            } else {
                type = aux = "----";
            }
        }

        /// <summary>
        /// Generates file type strings from an attribute holder.
        /// </summary>
        /// <remarks>
        /// <para>This is used for MacZip archive entries, when we want to show the values
        /// extracted from the AppleDouble header.</para>
        /// </remarks>
        /// <param name="attrs">Entry attributes.</param>
        /// <param name="hasRsrc">True if the entry has a resource fork.</param>
        /// <param name="type">Result: file type string (ProDOS type / HFS file type).</param>
        /// <param name="aux">Result: aux type string (ProDOS auxtype / HFS creator).</param>
        private static void GetFileTypeStrings(FileAttribs attrs, bool hasRsrc,
                out string type, out string aux) {
            // See if ProDOS types are buried in the HFS types.
            if (FileAttribs.ProDOSFromHFS(attrs.HFSFileType, attrs.HFSCreator,
                    out byte proType, out ushort proAux)) {
                type = FileTypes.GetFileTypeAbbrev(proType) + (hasRsrc ? '+' : ' ');
                aux = string.Format("${0:X4}", proAux);
            } else if (attrs.HFSCreator == 0 && attrs.HFSFileType == 0) {
                // No useful HFS type data.  Show as ProDOS.
                type = FileTypes.GetFileTypeAbbrev(attrs.FileType) + (hasRsrc ? '+' : ' ');
                aux = string.Format("${0:X4}", attrs.AuxType);
            } else {
                // Stringify the HFS types.  No need to show as hex.
                type = MacChar.StringifyMacConstant(attrs.HFSFileType) + (hasRsrc ? '+' : ' ');
                aux = ' ' + MacChar.StringifyMacConstant(attrs.HFSCreator);
            }
        }

        private static Formatter.FormatConfig sFmtCfg = new Formatter.FormatConfig() {
            SpaceBeforeUnits = false
        };
        private static Formatter sFormatter = new Formatter(sFmtCfg);

        /// <summary>
        /// Formats ProDOS access flags.
        /// </summary>
        internal static string FormatAccessFlags(byte access) {
            return sFormatter.FormatAccessFlags(access);
        }

        /// <summary>
        /// Formats a date/time stamp.  We use the standard ProDOS format, with two digits for
        /// the year and no seconds.
        /// </summary>
        internal static string FormatDateTime(DateTime when) {
            return sFormatter.FormatDateTime(when);
        }

        /// <summary>
        /// Formats a value that represents the size of something stored on a disk, such as a
        /// file or a partition.
        /// </summary>
        /// <param name="length">Value to format.</param>
        /// <param name="baseUnit">Base units: sectors, blocks, or KiB.</param>
        /// <returns>Formatted string.</returns>
        internal static string FormatSizeOnDisk(long length, int baseUnit) {
            return sFormatter.FormatSizeOnDisk(length, baseUnit);
        }

        #endregion Utilities
    }
}
