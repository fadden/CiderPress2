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

using AppCommon;
using CommonUtil;
using DiskArc;
using DiskArc.Arc;
using DiskArc.Multi;
using static DiskArc.Defs;
using static DiskArc.IFileSystem;

//
// Implementation notes - working with two ext-archive specifiers for "copy" command
//
// I started playing with this but didn't like my initial attempts.  We need an "OpenSecondExtArc"
// method that can walk through the DiskArcNode tree based on the component names.  Storing and
// comparing the component names felt a little wobbly, and got tricky when partial pathnames
// came in to play.  It also needs to replicate the skip-simple behavior.  Having multiple
// methods with duplicated logic seems unwise here.
//
// I think the best approach will be to just have the code do the same processing, but also
// pass the current DiskArcNode into the various sub-methods.  When we get to the point where we
// find the IFileEntry that will become the EntryInParent of a new node, we check to see if the
// node already has a child with that entry.  If so, we use that node instead of creating a
// new one, and avoid opening it as a stream.
//
// This should get us to the right place even if the strings don't match exactly, or when one
// specifier uses an APM partition number while the other uses the partition name.  Changes to
// the logic affect both paths equally.
//
// Simple test: add a "debug-double" flag to the parms (not accessible from the command line) that
// causes the "list" operation to open the archive twice, and verifies that the leafNode and
// leafObj are identical.  That will verify that the second ext-archive specifier was successfully
// traced to the same place.
//
// The various other interesting situations can be tested as part of the "copy" command tests.
//
// Copying to a tree ancestor should work; either the ancestor is a disk image with an open file
// for the child (which is fine so long as the ancestor itself is not the copy target), or the
// ancestor is an archive and the child was extracted to a temporary file, so the child archive can
// be accessed even when the parent is mid-transaction.
//

namespace cp2 {
    /// <summary>
    /// Code for processing an "ext-archive" argument.
    /// </summary>
    public static class ExtArchive {
        /// <summary>
        /// True if we number partitions and embedded volumes from 1, instead of 0.
        /// </summary>
        public const bool ONE_BASED_INDEX = WorkTree.ONE_BASED_INDEX;


        /// <summary>
        /// Character that divides components.
        /// </summary>
        /// <remarks>
        /// <para>We can't use '/' or '\' because those will be part of the host pathname.  Most
        /// shells understand ':' as being part of a compound path and won't interfere.</para>
        /// <para>We also want to use this when naming elements in ZIP and NuFX archives, because
        /// we made ':' the canonical path separator.  That requires an iterative search over
        /// increasingly long paths.</para>
        /// </remarks>
        internal const char SPLIT_CHAR = ':';

        /// <summary>
        /// Opens a disk image or file archive, and descends into it according to the
        /// component path.
        /// </summary>
        /// <remarks>
        /// <para>If the leaf object is a disk image or partition, the filesystem will already
        /// have been analyzed.</para>
        /// <para>The <paramref name="rootNode"/> must be disposed.</para>
        /// </remarks>
        /// <param name="extPath">Path to file and components.</param>
        /// <param name="isDirOkay">If true, the path may end with a disk image
        ///   subdirectory.</param>
        /// <param name="isReadOnly">If true, the file is opened read-only.</param>
        /// <param name="parms">Application parameters.</param>
        /// <param name="rootNode">Result: root of storage tree, or null on failure.</param>
        /// <param name="leafNode">Result: leaf node in storage tree.</param>
        /// <param name="leafObj">Result: leaf object; may be an IArchive, IDiskImage, or
        ///   Partition.</param>
        /// <param name="endDirEntry">Result: file entry for last component, if the last
        ///   component was a directory in a disk image; otherwise holds NO_ENTRY.</param>
        /// <returns>True on success.</returns>
        internal static bool OpenExtArc(string extPath, bool isDirOkay, bool isReadOnly,
                ParamsBag parms,
                [NotNullWhen(true)] out DiskArcNode? rootNode,
                [NotNullWhen(true)] out DiskArcNode? leafNode,
                [NotNullWhen(true)] out object? leafObj,
                out IFileEntry endDirEntry) {
            if (parms.Debug) {
                Console.WriteLine("+++ ExtArc path is '" + extPath + "'");
            }
            leafObj = rootNode = leafNode = null;
            endDirEntry = IFileEntry.NO_ENTRY;

            List<string>? components = SplitComponents(extPath);
            if (components == null) {
                return false;
            }
            if (parms.Debug) {
                for (int i = 0; i < components.Count; i++) {
                    Console.WriteLine("+++ "+ i + ": " + components[i]);
                }
            }

            try {
                if (OpenComponents(components, isReadOnly, parms, out rootNode, out leafNode,
                        out leafObj, out endDirEntry)) {
                    if (!isDirOkay && endDirEntry != IFileEntry.NO_ENTRY) {
                        Console.Error.WriteLine("Must specify a file, not a directory");
                        rootNode?.Dispose();
                        rootNode = leafNode = null;
                        return false;
                    }

                    if (parms.Debug) {
                        Console.WriteLine("+++ branch: " + leafNode!.DebugDumpBranch());
                    }

                    // Success!
                    return true;
                }
            } catch (Exception ex) {
                Console.Error.WriteLine("Caught exception: " + ex);
            }
            // Failure or exception.
            rootNode?.Dispose();
            rootNode = leafNode = null;
            return false;
        }

#if false
        /// <summary>
        /// Opens a component within an already-open node tree.
        /// </summary>
        /// <param name="extPath">Path to file and components.</param>
        /// <param name="isDirOkay">If true, the path may end with a disk image
        ///   subdirectory.</param>
        /// <param name="parms">Application parameters.</param>
        /// <param name="rootNode">Root of storage tree.</param>
        /// <param name="leafNode">Result: leaf node in storage tree.</param>
        /// <param name="leafObj">Result: leaf object; may be an IArchive, IDiskImage, or
        ///   Partition.</param>
        /// <param name="endDirEntry">Result: file entry for last component, if the last
        ///   component was a directory in a disk image; otherwise holds NO_ENTRY.</param>
        /// <returns>True on success.</returns>
        internal static bool OpenSecondExtArc(string extPath, bool isDirOkay, ParamsBag parms,
                DiskArcNode rootNode,
                [NotNullWhen(true)] out DiskArcNode? leafNode,
                [NotNullWhen(true)] out object? leafObj,
                out IFileEntry endDirEntry) {
            if (parms.Debug) {
                Console.WriteLine("+++ ExtArc path2 is '" + extPath + "'");
            }
            leafObj = leafNode = null;
            endDirEntry = IFileEntry.NO_ENTRY;

            List<string>? components = SplitComponents(extPath);
            if (components == null) {
                return false;
            }
            if (parms.Debug) {
                for (int i = 0; i < components.Count; i++) {
                    Console.WriteLine(" " + i + ": " + components[i]);
                }
            }

            HostFileNode curNode = (HostFileNode)rootNode;
            Debug.Assert(components[0] == curNode.PathName);

            // TODO

            return false;
        }
#endif

        /// <summary>
        /// Walks through the list of components, opening each part.
        /// </summary>
        /// <param name="components">List of component names.</param>
        /// <param name="isReadOnly">True if we want to open as read-only.</param>
        /// <param name="parms">Command options.</param>
        /// <param name="rootNode">Result: root of storage tree.</param>
        /// <param name="leafNode">Result: leaf node in storage tree.</param>
        /// <param name="leafObj">Result: leaf object; may be an IArchive, IDiskImage, or
        ///   Partition.</param>
        /// <param name="endDirEntry">Result: file entry for last component, if the last
        ///   component was a directory in a disk image; otherwise holds NO_ENTRY.</param>
        /// <returns>True on success.</returns>
        private static bool OpenComponents(List<string> components, bool isReadOnly,
                ParamsBag parms,
                [NotNullWhen(true)] out DiskArcNode? rootNode,
                [NotNullWhen(true)] out DiskArcNode? leafNode,
                [NotNullWhen(true)] out object? leafObj,
                out IFileEntry endDirEntry) {
            rootNode = leafNode = null;
            leafObj = null;
            endDirEntry = IFileEntry.NO_ENTRY;

            // Open the host file.
            string hostFileName = components[0];
            if (Directory.Exists(hostFileName)) {
                Console.Error.WriteLine("'" + hostFileName + "' is a directory");
                return false;
            }
            string label = hostFileName;
            string ext = Path.GetExtension(hostFileName);
            leafNode = rootNode = new HostFileNode(hostFileName, parms.AppHook);
            FileStream hostStream;
            try {
                hostStream = new FileStream(hostFileName, FileMode.Open,
                    isReadOnly ? FileAccess.Read : FileAccess.ReadWrite, FileShare.Read);
            } catch (IOException ex) {
                Console.Error.WriteLine("Unable to open '" + hostFileName + "': " + ex.Message);
                return false;
            }

            int nextComponent = 1;
            Stream? stream = hostStream;
            IFileEntry entryInParent = IFileEntry.NO_ENTRY;

            // Walk through the list of components.
            while (true) {
                // If we have a file stream for the next part, figure out what it holds, based
                // on the stream contents and "ext" file extension.  We won't have a file stream
                // if this is a partition/sub-volume.
                if (stream != null) {
                    Debug.Assert(leafObj == null);
                    leafObj = WorkTree.IdentifyStreamContents(stream, ext, label, parms.AppHook,
                        out string errorMsg, out bool unused1, out SectorOrder unused2);
                    if (leafObj == null) {
                        Console.Error.WriteLine("Error: " + errorMsg);
                        return false;
                    }

                    // Add a node for this item.
                    if (leafObj is IArchive) {
                        leafNode = new ArchiveNode(leafNode, stream, (IArchive)leafObj,
                            entryInParent, parms.AppHook);
                    } else if (leafObj is IDiskImage) {
                        leafNode = new DiskImageNode(leafNode, stream, (IDiskImage)leafObj,
                            entryInParent, parms.AppHook);
                    } else {
                        Debug.Assert(false);
                        return false;
                    }
                }

                // Set "noComponent" flag if gzip or .SDK.
                bool noComponent = false;
                if (parms.SkipSimple) {
                    if (leafObj is GZip) {
                        noComponent = true;
                    } else if (leafObj is NuFX) {
                        IEnumerator<IFileEntry> numer = ((IArchive)leafObj).GetEnumerator();
                        if (numer.MoveNext()) {
                            // has at least one entry
                            if (numer.Current.IsDiskImage && !numer.MoveNext()) {
                                // single entry, is disk image
                                noComponent = true;
                            }
                        }
                    }
                }

                // Check if we've reached the end.
                Debug.Assert(nextComponent <= components.Count);
                if (nextComponent == components.Count && !noComponent) {
                    Debug.Assert(leafObj != null);
                    return true;        // done
                }
                int startComponent = nextComponent;

                // Handle an intermediate (non-leaf) component.
                if (leafObj is IArchive) {
                    // Component is implied, or archive entry name.
                    if (!ProcessArchive((IArchive)leafObj, label, components, ref nextComponent,
                            noComponent, parms.AppHook, out stream, out ext, out entryInParent)) {
                        return false;
                    }
                    leafObj = null;
                } else if (leafObj is IDiskImage) {
                    // Component is partition #, embed #, APM partition name, or FS entry filename.
                    if (!ProcessDiskImage((IDiskImage)leafObj, components, ref nextComponent,
                            parms.AppHook, out stream, out ext, out Partition? part,
                            out entryInParent, out endDirEntry)) {
                        // No filesystem or partition, so we can't traverse it.
                        return false;
                    }
                    if (endDirEntry != IFileEntry.NO_ENTRY) {
                        return true;        // ended on a dir
                    }
                    Debug.Assert(stream != null ^ part != null);
                    if (stream == null) {
                        leafObj = part;       // got partition, so no file stream
                    } else {
                        leafObj = null;       // got file stream, figure out what it is
                    }
                } else {
                    Debug.Assert(leafObj is Partition);
                    // Component is embed #, or FS entry filename.
                    if (!ProcessPartition((Partition)leafObj, components, ref nextComponent,
                            parms.AppHook, out stream, out ext, out Partition? part,
                            out entryInParent, out endDirEntry)) {
                        return false;
                    }
                    if (endDirEntry != IFileEntry.NO_ENTRY) {
                        return true;        // ended on a dir
                    }
                    Debug.Assert(stream != null ^ part != null);
                    if (stream == null) {
                        leafObj = part;       // got partition, so no file stream
                    } else {
                        leafObj = null;       // got file stream, figure out what it is
                    }
                }

                // Generate a label for the stream / partition by concatenating the names of
                // the components used.
                label = string.Empty;
                if (startComponent < components.Count) {
                    label = components[startComponent];
                    while (++startComponent < nextComponent) {
                        label += ':';
                        label += components[startComponent];
                    }
                }
            }
        }

        /// <summary>
        /// Process an IArchive component.  File archives hold file entries, but we have special
        /// handling for NuFX .SDK disk images.
        /// </summary>
        private static bool ProcessArchive(IArchive archive, string name, List<string> components,
                ref int curIndex, bool implied, AppHook appHook, out Stream? stream,
                out string ext, out IFileEntry entryInParent) {
            stream = null;
            ext = string.Empty;
            entryInParent = IFileEntry.NO_ENTRY;

            IFileEntry entry;

            if (implied) {
                // We don't want or need a filename for GZIP, and NuFX disk archives are
                // treated as disks rather than file archives.  Use the first and only entry.
                entry = archive.GetFirstEntry();
                if (archive is GZip) {
                    if (name.EndsWith(".gz", StringComparison.InvariantCultureIgnoreCase)) {
                        name = name.Substring(0, name.Length - 3);
                    }
                    ext = Path.GetExtension(name);
                } else if (archive is NuFX) {
                    // Must be a disk image in a .SDK.
                    Debug.Assert(entry.IsDiskImage);
                    ext = ".po";
                } else {
                    Debug.Assert(false);        // shouldn't be here?
                }
            } else {
                const char ARC_SEP = SPLIT_CHAR;

                // We need to find an entry that matches.  Entries can have directory separators,
                // which we want to handle the same way we do directories on a disk image, but
                // we can't just query the parts because not all archives have entries for
                // directory path components.  We need to cons up components to form increasingly
                // long names until we find a match or run out of components.
                int lastUsed;
                IFileEntry? matchEntry = null;
                StringBuilder sb = new StringBuilder();
                for (lastUsed = curIndex; lastUsed < components.Count; lastUsed++) {
                    sb.Clear();
                    for (int i = curIndex; i <= lastUsed; i++) {
                        if (i != curIndex) {
                            sb.Append(ARC_SEP);
                        }
                        sb.Append(components[i]);
                    }
                    string matchStr = sb.ToString();
                    //Console.WriteLine("TESTING " + matchStr);
                    foreach (IFileEntry candidate in archive) {
                        if (candidate.CompareFileName(matchStr, ARC_SEP) == 0 &&
                                !candidate.IsDirectory) {
                            matchEntry = candidate;
                            //Console.WriteLine("MATCH on " + matchStr);
                            break;
                        }
                    }
                    if (matchEntry != null) {
                        break;
                    }
                }
                if (matchEntry == null) {
                    Console.Error.WriteLine("Unable to find match for component that starts " +
                        "with '" + components[curIndex] + "'");
                    return false;
                }

                entry = matchEntry;
                curIndex = lastUsed + 1;

                // Use the extension of the thing inside the archive, unless it's a disk image
                // record.
                if (entry.IsDiskImage) {
                    ext = ".po";
                } else {
                    ext = Path.GetExtension(matchEntry.FileName);
                }
            }

            FilePart part = entry.IsDiskImage ? FilePart.DiskImage : FilePart.DataFork;
            try {
                stream = ArcTemp.ExtractToTemp(archive, entry, part);
            } catch (InvalidDataException ex) {
                Console.Error.WriteLine("Error: extraction of '" + entry.FileName + "' failed: " +
                    ex.Message);
                return false;
            }

            entryInParent = entry;
            return true;
        }

        /// <summary>
        /// Processes an IDiskImage component.  Disk images hold filesystems or multi-partition
        /// images.
        /// </summary>
        /// <remarks>
        /// <para><paramref name="stream"/> will be non-null if we found a matching
        /// non-directory file in a filesystem.  It will be null if we found a partition, a
        /// directory, or didn't find a match at all.</para>
        /// </remarks>
        /// <returns>True if we found a partition or filesystem.</returns>
        private static bool ProcessDiskImage(IDiskImage disk, List<string> components,
                ref int curIndex, AppHook appHook, out Stream? stream,
                out string ext, out Partition? part, out IFileEntry entryInParent,
                out IFileEntry endDirEntry) {
            entryInParent = endDirEntry = IFileEntry.NO_ENTRY;
            // Disk was analyzed when we first examined the stream.
            //disk.AnalyzeDisk(null, SectorOrder.Unknown, IDiskImage.AnalysisDepth.Full);
            if (disk.Contents is IFileSystem) {
                return ProcessFileSystem((IFileSystem)disk.Contents, components, ref curIndex,
                    appHook, out stream, out ext, out part, out entryInParent, out endDirEntry);
            } else if (disk.Contents is IMultiPart) {
                stream = null;
                ext = string.Empty;
                return ProcessMultiPart((IMultiPart)disk.Contents, components, ref curIndex,
                    appHook, out part);
            } else {
                // No recognizable partitions or filesystem.  We can't traverse this.
                Console.Error.WriteLine("Unable to process disk image: " + components[curIndex]);
                stream = null;
                ext = string.Empty;
                part = null;
                return false;
            }
        }

        /// <summary>
        /// Processes an IMultiPart, found inside an IDiskImage.  Select the appropriate partition.
        /// </summary>
        private static bool ProcessMultiPart(IMultiPart parts, List<string> components,
                ref int curIndex, AppHook appHook, out Partition? part) {
            int partIndex;
            if (!int.TryParse(components[curIndex], out partIndex)) {
                partIndex = -1;
            }
            if (ONE_BASED_INDEX) {
                partIndex--;        // using a 1-based count in the interface
            }
            if (partIndex >= 0 && partIndex < parts.Count) {
                part = parts[partIndex];
                curIndex++;
                return true;
            }

            // Not a number, or number out of range.  See if it matches an APM partition name.
            if (parts is APM) {
                foreach (APM_Partition apmPart in parts) {
                    if (string.Equals(apmPart.PartitionName, components[curIndex],
                            StringComparison.InvariantCultureIgnoreCase)) {
                        part = apmPart;
                        curIndex++;
                        return true;
                    }
                }
            }

            Console.Error.WriteLine("Unable to find partition: " + components[curIndex]);
            part = null;
            return false;
        }

        /// <summary>
        /// Processes a Partition component.  Partitions hold filesystems.
        /// </summary>
        private static bool ProcessPartition(Partition part, List<string> components,
                ref int curIndex, AppHook appHook, out Stream? stream, out string ext,
                out Partition? subPart, out IFileEntry entryInParent, out IFileEntry endDirEntry) {
            part.AnalyzePartition();
            if (part.FileSystem != null) {
                return ProcessFileSystem(part.FileSystem, components, ref curIndex, appHook,
                    out stream, out ext, out subPart, out entryInParent, out endDirEntry);
            } else {
                Console.Error.WriteLine("Unable to process partition: " + components[curIndex]);
                stream = null;
                ext = string.Empty;
                subPart = null;
                entryInParent = endDirEntry = IFileEntry.NO_ENTRY;
                return false;
            }
        }

        /// <summary>
        /// Processes a filesystem, found inside an IDiskImage or Partition.  Filesystems
        /// hold files and embedded volumes.
        /// </summary>
        /// <remarks>
        /// <para><paramref name="stream"/> will be non-null if we found a matching
        /// non-directory file.  It will be null if we found a numbered partition, or
        /// a filesystem directory.</para>
        /// </remarks>
        private static bool ProcessFileSystem(IFileSystem fs, List<string> components,
                ref int curIndex, AppHook appHook, out Stream? stream, out string ext,
                out Partition? subPart, out IFileEntry entryInParent, out IFileEntry endDirEntry) {
            subPart = null;
            stream = null;
            ext = string.Empty;
            entryInParent = endDirEntry = IFileEntry.NO_ENTRY;

            fs.PrepareFileAccess(true);

            // Check to see if this is a reference to an embedded volume.
            int partIndex;
            if (!int.TryParse(components[curIndex], out partIndex)) {
                partIndex = -1;
            }
            if (ONE_BASED_INDEX) {
                partIndex--;        // using a 1-based count in the interface
            }
            if (partIndex >= 0) {
                // It's a number.  Look for embedded volumes.  If we don't find any, assume it's
                // actually a filename.
                //
                // This might make a file with a numeric name inaccessible, but currently that
                // would only be an issue for disks/archives stored on DOS disks.
                IMultiPart? embeds = fs.FindEmbeddedVolumes();
                if (embeds != null && partIndex < embeds.Count) {
                    subPart = embeds[partIndex];
                    curIndex++;
                    return true;
                }
            }

            IFileEntry dirEntry = fs.GetVolDirEntry();
            while (true) {
                try {
                    IFileEntry entry = fs.FindFileEntry(dirEntry, components[curIndex]);
                    if (entry.IsDirectory) {
                        dirEntry = entry;
                        curIndex++;
                        if (curIndex == components.Count) {
                            // Ran out of components without finding a file.  It's up to the
                            // caller to decide if this is okay.
                            endDirEntry = dirEntry;
                            return true;
                        }
                    } else {
                        // Found a file.  Return it as the stream.
                        entryInParent = entry;
                        curIndex++;
                        stream = fs.OpenFile(entry,
                            fs.IsReadOnly ? FileAccessMode.ReadOnly : FileAccessMode.ReadWrite,
                            FilePart.DataFork);
                        ext = Path.GetExtension(entry.FileName);
                        return true;
                    }
                } catch (FileNotFoundException) {
                    Console.Error.WriteLine("Unable to find file '" + components[curIndex] + "'");
                    return false;
                }
            }
        }

        /// <summary>
        /// Splits an extended archive name into individual components.
        /// </summary>
        /// <remarks>
        /// <para>We have an important special case to consider: "C:/foo/bar" should not split
        /// the drive letter into a separate component.</para>
        /// </remarks>
        private static List<string>? SplitComponents(string extArcName) {
            // Split the name into components.  Escape with a backslash.
            List<string> components = new List<string>();
            StringBuilder sb = new StringBuilder();

            bool esc = false;
            for (int i = 0; i < extArcName.Length; i++) {
                char ch = extArcName[i];
                if (esc) {
                    // Previous char was '\'.  If this is a ':', just output that.  If not,
                    // output the backslash too.  Otherwise "C:\blah" becomes "C:blah".
                    esc = false;
                    if (ch == SPLIT_CHAR) {
                        sb.Append(ch);
                    } else {
                        sb.Append('\\');
                        sb.Append(ch);
                    }
                } else if (ch == '\\') {
                    // Found a backslash.  Don't output it, but escape the next thing.  If it's
                    // at the end of the string, we just ignore it.
                    esc = true;
                } else if (ch == SPLIT_CHAR) {
                    if (i == 1) {
                        // Special case for drive letter followed by colon.  We may want to
                        // make this system-specific, or at least confirm that it has the
                        // form "[A-Za-z]:[/\]".
                        sb.Append(ch);
                    } else {
                        // Add what we've gathered, then start a new string.
                        components.Add(sb.ToString());
                        sb.Clear();
                    }
                } else {
                    sb.Append(ch);
                }
            }
            components.Add(sb.ToString());

            // Check for empty parts.
            for (int i = 0; i < components.Count; i++) {
                if (string.IsNullOrEmpty(components[i])) {
                    Console.Error.WriteLine("Error: found empty component in '" + extArcName + "'");
                    return null;
                }
            }

            return components;
        }

        /// <summary>
        /// Determines whether two ext-archive specifiers reference the same host file.
        /// </summary>
        /// <param name="extArc1">First ext-archive specifier.</param>
        /// <param name="extArc2">Second ext-archive specifier.</param>
        /// <param name="isSame">Result: true if they reference the same file.</param>
        /// <returns>True on success.</returns>
        internal static bool CheckSameHostFile(string extArc1, string extArc2, out bool isSame) {
            isSame = false;
            List<string>? set1 = SplitComponents(extArc1);
            List<string>? set2 = SplitComponents(extArc2);
            if (set1 == null || set2 == null) {
                return false;
            }
            return Misc.IsSameFile(set1[0], set2[0], out isSame);
        }
    }
}
