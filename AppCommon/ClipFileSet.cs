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
using FileConv;
using static DiskArc.Defs;
using static DiskArc.IFileSystem;

namespace AppCommon {
    /// <summary>
    /// <para>This holds a set of file entry information that can be serialized and placed on the
    /// system clipboard.  It effectively comes in two varieties: instances used to generate
    /// the list of entries, which have various fields populated, and instances used to receive
    /// serialized data, which only has the ClipFileEntry list set.  It would be best to avoid
    /// using this class as anything but a list-holder once it has been constructed.</para>
    /// </summary>
    [Serializable]
    public class ClipFileSet {
        public int Count => Entries.Count;

        public ClipFileEntry this[int index] {
            get { return Entries[index]; }
        }

        /// <summary>
        /// List of entries for the clipboard.  This will be serialized.
        /// </summary>
        public List<ClipFileEntry> Entries { get; set; } = new List<ClipFileEntry>();

        //
        // Innards.
        //

        [NonSerialized]
        private bool mStripPaths;
        [NonSerialized]
        private bool mEnableMacZip;
        [NonSerialized]
        private ExtractFileWorker.PreserveMode mPreserveMode;
        [NonSerialized]
        private ConvConfig.FileConvSpec? mExportSpec;

        //
        // Additional innards for archives.
        //

        [NonSerialized]
        private Dictionary<string, string> mSynthDirs = new Dictionary<string, string>();

        //
        // Additional innards for filesystems.
        //

        [NonSerialized]
        private IFileEntry mBaseDir = IFileEntry.NO_ENTRY;
        [NonSerialized]
        private bool mUseRawData;

        // Keep track of directories we've already added to the output.
        [NonSerialized]
        private Dictionary<IFileEntry, IFileEntry> mAddedDirs =
            new Dictionary<IFileEntry, IFileEntry>();


        /// <summary>
        /// Nullary constructor, for the deserializer.
        /// </summary>
        public ClipFileSet() { }

        /// <summary>
        /// Constructs a set of ClipFileEntries that contains the entries provided.  Additional
        /// entries may be added for directories.
        /// </summary>
        /// <remarks>
        /// <para>This is generally fast, though if we're exporting data from a file archive
        /// we may end up having to uncompress parts to determine the converter to use.  We may
        /// also need to peek inside Mac ZIP header files.  We might want to update a progress
        /// meter.</para>
        /// </remarks>
        /// <param name="archiveOrFileSystem">IArchive or IFileSystem instance.</param>
        /// <param name="entries">List of entries to add.</param>
        /// <param name="baseDir">For disk images, the effective root of the filesystem.</param>
        /// <param name="preserveMode">Preservation mode to use for extracted files.</param>
        /// <param name="useRawData">If true, use "raw" mode on appropriate disks.</param>
        /// <param name="stripPaths">If true, strip paths while adding to set.</param>
        /// <param name="enableMacZip">If true, handle Mac ZIP entries.</param>
        /// <param name="exportSpec">Export specification.  If non-null, this will do an export
        ///   operation rather than extract.</param>
        /// <param name="appHook">Application hook reference.</param>
        public ClipFileSet(object archiveOrFileSystem, List<IFileEntry> entries,
                IFileEntry baseDir, ExtractFileWorker.PreserveMode preserveMode,
                bool useRawData, bool stripPaths, bool enableMacZip,
                ConvConfig.FileConvSpec? exportSpec, AppHook appHook) {

            // Stuff the simple items into the object so we don't have to pass them around.
            mBaseDir = baseDir;
            mPreserveMode = preserveMode;
            mUseRawData = useRawData;
            mStripPaths = stripPaths;
            mEnableMacZip = enableMacZip;
            mExportSpec = exportSpec;

            if (archiveOrFileSystem is IArchive) {
                Debug.Assert(baseDir == IFileEntry.NO_ENTRY);
                GenerateFromArchive((IArchive)archiveOrFileSystem, entries, appHook);
            } else if (archiveOrFileSystem is IFileSystem) {
                GenerateFromDisk((IFileSystem)archiveOrFileSystem, entries, appHook);
            } else {
                throw new ArgumentException("Invalid archive/fs object");
            }
        }

        #region Archive

        /// <summary>
        /// Generates ClipFileEntry set from a file archive.
        /// </summary>
        /// <param name="arc">File archive reference.</param>
        /// <param name="entries">List of entries to add.</param>
        /// <param name="appHook">Application hook reference.</param>
        private void GenerateFromArchive(IArchive arc, List<IFileEntry> entries, AppHook appHook) {
            Debug.Assert(Entries.Count == 0);

            foreach (IFileEntry entry in entries) {
                if (entry.IsDirectory) {
                    // Ignore directories stored in archives.  (Might want to handle them so
                    // that we reconstruct empty directories.)
                    continue;
                }

                string extractPath;
                if (mPreserveMode == ExtractFileWorker.PreserveMode.NAPS) {
                    extractPath = PathName.AdjustEscapePathName(entry.FullPathName,
                        entry.DirectorySeparatorChar, PathName.DEFAULT_REPL_CHAR);
                } else {
                    extractPath = PathName.AdjustPathName(entry.FullPathName,
                        entry.DirectorySeparatorChar, PathName.DEFAULT_REPL_CHAR);
                }
                if (mStripPaths) {
                    extractPath = Path.GetFileName(extractPath);
                }

                // Synthesize entries for directories.
                if (!mStripPaths) {
                    AddPathDirEntries(arc, entry.FullPathName, entry.DirectorySeparatorChar,
                        appHook);
                }

                // Generate file attributes.  The same object will be used for both forks.
                FileAttribs attrs = new FileAttribs(entry);

                // Handle MacZip archives, if enabled.
                IFileEntry adfEntry = IFileEntry.NO_ENTRY;
                if (arc is Zip && mEnableMacZip) {
                    // Only handle __MACOSX/ entries when paired with other entries.
                    if (entry.IsMacZipHeader()) {
                        continue;
                    }
                    // Check to see if we have a paired entry.
                    string macZipName = Zip.GenerateMacZipName(entry.FullPathName);
                    if (!string.IsNullOrEmpty(macZipName)) {
                        if (arc.TryFindFileEntry(macZipName, out adfEntry)) {
                            // Update the attributes with values from the ADF header.
                            GetMacZipAttribs(arc, adfEntry, attrs, appHook);
                        }
                    }
                }

                if (mExportSpec == null) {
                    CreateForExtract(arc, entry, adfEntry, attrs, extractPath, appHook);
                } else {
                    CreateForExport(arc, entry, adfEntry, attrs, extractPath, appHook);
                }
            }
        }

        /// <summary>
        /// Extracts the file attributes from a MacZip ADF header entry.
        /// </summary>
        private static void GetMacZipAttribs(IArchive arc, IFileEntry adfEntry,
                FileAttribs attrs, AppHook appHook) {
            try {
                // Copy to temp file; can't use unseekable stream as archive source.
                using Stream adfStream = ArcTemp.ExtractToTemp(arc, adfEntry, FilePart.DataFork);
                using AppleSingle adfArchive = AppleSingle.OpenArchive(adfStream, appHook);
                IFileEntry adfArchiveEntry = adfArchive.GetFirstEntry();
                attrs.GetFromAppleSingle(adfArchiveEntry);
            } catch (Exception ex) {
                // Never mind.
                appHook.LogW("Failed to get attributes from ADF header (" + adfEntry +
                    "): " + ex.Message);
            }
        }

        /// <summary>
        /// Recursively adds ClipFileEntry objects for the directories in the path.
        /// </summary>
        /// <remarks>
        /// <para>The idea is to have explicit directory entries for entries with partial
        /// paths, similar to what we generate when copying from a disk image.  This is not
        /// required for all targets, e.g. Windows Explorer creates directories as needed.</para>
        /// <para>This uses case-sensitive comparisons when determining if a directory has
        /// already been added to the list.  We do this because the target is either
        /// case-sensitive, in which case all variants are needed, or it's case-insensitive and
        /// is expected to ignore requests to create a directory that already exists.</para>
        /// <para>This is complicated slightly by the "adjustment" of paths for compatible with
        /// the host filesystem.  If the source is an HFS volume with "sub/dir" and "sub_dir",
        /// they will remain separate when pasted to another HFS volume, but merged when
        /// extracting to Windows.  The uniqueness test is based on the source name, not the
        /// adjusted name.</para>
        /// </remarks>
        /// <param name="arc">File archive we're working on.</param>
        /// <param name="pathName">Partial pathname.</param>
        /// <param name="dirSep">Directory separator character.</param>
        /// <param name="appHook">Application hook reference.</param>
        private void AddPathDirEntries(IArchive arc, string pathName, char dirSep, AppHook appHook) {
            Debug.Assert(!mStripPaths);

            // Remove the filename.
            string? dirName = GetDirectoryName(pathName, dirSep);
            if (string.IsNullOrEmpty(dirName)) {
                // This was nothing but filename.  Nothing for us to do.
                return;
            }
            // See if there's another level.
            AddPathDirEntries(arc, dirName, dirSep, appHook);

            // Case-sensitive string comparison.
            if (!mSynthDirs.ContainsKey(dirName)) {
                // Generate an entry for the directory.
                FileAttribs attrs = new FileAttribs();
                attrs.FullPathName = dirName;
                attrs.IsDirectory = true;
                string extractPath = PathName.AdjustPathName(dirName, dirSep,
                    PathName.DEFAULT_REPL_CHAR);
                Entries.Add(new ClipFileEntry(arc, IFileEntry.NO_ENTRY, IFileEntry.NO_ENTRY,
                    FilePart.DataFork, attrs, extractPath, mPreserveMode, null, appHook));
                mSynthDirs.Add(dirName, dirName);
            }
        }

        /// <summary>
        /// Removes the filename, leaving just the directory name.
        /// </summary>
        /// <remarks>
        /// <para>The filenames come from file archives, and could be damaged in various
        /// ways.  Since our goal is just to create directories helpfully, it's best to give
        /// up early if we find weird damage.</para>
        /// <para>If the pathname has a trailing separator, or two separators in a row, this will
        /// assume it has found the end of the chain.</para>
        /// </remarks>
        private static string? GetDirectoryName(string pathName, char dirSep) {
            int lastIndex = pathName.LastIndexOf(dirSep);
            if (lastIndex <= 0) {
                // Not found, or leading '/'.
                return null;
            }
            return pathName.Substring(0, lastIndex);
        }

        #endregion Archive

        #region Disk

        /// <summary>
        /// Populates the ClipFileEntry set from a list of IFileEntry objects.  Directory entries
        /// will be descended into.
        /// </summary>
        /// <param name="fs">Filesystem reference.</param>
        private void GenerateFromDisk(IFileSystem fs, List<IFileEntry> entries, AppHook appHook) {
            Debug.Assert(Entries.Count == 0);
            fs.PrepareFileAccess(true);

            IFileEntry aboveRootEntry;
            if (mBaseDir == IFileEntry.NO_ENTRY) {
                aboveRootEntry = IFileEntry.NO_ENTRY;       // same as start==volDir
            } else {
                aboveRootEntry = mBaseDir.ContainingDir;
            }

            foreach (IFileEntry entry in entries) {
                GenerateDiskEntries(fs, entry, aboveRootEntry, appHook);
            }
        }

        /// <summary>
        /// Generates an entry in the ClipFileEntry set for the specified entry.  If the entry is
        /// a directory, we recursively generate clip objects for the entry's children.
        /// </summary>
        /// <param name="fs">Filesystem reference.</param>
        /// <param name="entry">File entry to process.</param>
        /// <param name="aboveRootEntry">Entry above the root, used to limit the partial path
        ///   prefix.</param>
        /// <param name="appHook">Application hook reference.</param>
        private void GenerateDiskEntries(IFileSystem fs, IFileEntry entry,
                IFileEntry aboveRootEntry, AppHook appHook) {
            if (!mStripPaths) {
                // Ensure we have entries for the directories that make up the path.  Windows
                // Explorer doesn't actually require this -- it generates missing paths as
                // needed -- but other systems might be different.  It also gives us a way to
                // create empty directories, and a way to pass the directory file dates along.
                AddMissingDirectories(fs, entry, aboveRootEntry, appHook);
            }
            if (entry.IsDirectory) {
                // Current directory, if not stripped, was added above.
                foreach (IFileEntry child in entry) {
                    GenerateDiskEntries(fs, child, aboveRootEntry, appHook);
                }
            } else {
                // We pass the original pathname through the file attribute serialization, and
                // a platform-specific pathname though the clipboard mechanism.  Both pathnames
                // need to be re-rooted.
                string extractPath;
                if (mPreserveMode == ExtractFileWorker.PreserveMode.NAPS && !entry.IsDirectory) {
                    extractPath = ExtractFileWorker.GetAdjEscPathName(entry, aboveRootEntry,
                        Path.DirectorySeparatorChar);
                } else {
                    extractPath = ExtractFileWorker.GetAdjPathName(entry, aboveRootEntry,
                        Path.DirectorySeparatorChar);
                }
                if (mStripPaths) {
                    extractPath = Path.GetFileName(extractPath);
                }

                // Generate file attributes.  The same object will be used for both forks.
                FileAttribs attrs = new FileAttribs(entry);
                attrs.FullPathName = ReRootedPathName(entry, aboveRootEntry);

                if (mExportSpec == null) {
                    CreateForExtract(fs, entry, IFileEntry.NO_ENTRY, attrs, extractPath, appHook);
                } else {
                    CreateForExport(fs, entry, IFileEntry.NO_ENTRY, attrs, extractPath, appHook);
                }
            }
        }


        /// <summary>
        /// Adds entries for the directory hierarchy that contains the specified entry, if
        /// they haven't yet been added.  If the entry is itself a directory, it will be added.
        /// </summary>
        /// <param name="fs">Filesystem reference.</param>
        /// <param name="entry">Entry to add.</param>
        /// <param name="aboveRootEntry">Entry above the root, used to limit the partial path
        ///   prefix.</param>
        /// <param name="appHook">Application hook reference.</param>
        private void AddMissingDirectories(IFileSystem fs, IFileEntry entry,
                IFileEntry aboveRootEntry, AppHook appHook) {
            if (entry.ContainingDir == aboveRootEntry) {
                return;
            }
            // Recursively check parents.
            AddMissingDirectories(fs, entry.ContainingDir, aboveRootEntry, appHook);
            if (entry.IsDirectory && !mAddedDirs.ContainsKey(entry)) {
                // Add this directory to the output list.
                FileAttribs attrs = new FileAttribs(entry);
                attrs.FullPathName = ReRootedPathName(entry, aboveRootEntry);
                Debug.Assert(attrs.IsDirectory);
                string extractPath = ExtractFileWorker.GetAdjPathName(entry, aboveRootEntry,
                    Path.DirectorySeparatorChar);
                Entries.Add(new ClipFileEntry(fs, entry, IFileEntry.NO_ENTRY, FilePart.DataFork,
                    attrs, extractPath, mPreserveMode, null, appHook));
                mAddedDirs.Add(entry, entry);
            }
        }

        /// <summary>
        /// Generates a full pathname for a filesystem entry, the way
        /// <see cref="IFileEntry.FullPathName"/> would, but potentially stopping before traversing
        /// all the way to the root.
        /// </summary>
        /// <param name="entry">File entry.</param>
        /// <param name="aboveRootEntry">Entry above the root directory.  For the volume dir,
        ///   or non-hierarchical filesystems, this will be NO_ENTRY.</param>
        private static string ReRootedPathName(IFileEntry entry, IFileEntry aboveRootEntry) {
            StringBuilder sb = new StringBuilder();
            ReRootedPathName(entry, aboveRootEntry, sb);
            return sb.ToString();
        }

        /// <summary>
        /// Recursively generates a full pathname for a filesystem entry, potentially stopping
        /// before reaching the root.
        /// </summary>
        /// <param name="entry">File entry.</param>
        /// <param name="aboveRootEntry">Entry above the root directory.  For the volume dir,
        ///   or non-hierarchical filesystems, this will be NO_ENTRY.</param>
        /// <param name="sb">Pathname under construction.</param>
        /// <returns>Partial pathname.</returns>
        private static void ReRootedPathName(IFileEntry entry, IFileEntry aboveRootEntry,
                StringBuilder sb) {
            if (entry.ContainingDir != aboveRootEntry) {
                ReRootedPathName(entry.ContainingDir, aboveRootEntry, sb);
                if (sb.Length > 0) {
                    sb.Append(entry.DirectorySeparatorChar);
                }
                sb.Append(entry.FileName);
            }
        }

        #endregion Disk

        private void CreateForExtract(object archiveOrFilesystem, IFileEntry entry,
                IFileEntry adfEntry, FileAttribs attrs, string extractPath, AppHook appHook) {
            FilePart dataPart = mUseRawData ? FilePart.RawData : FilePart.DataFork;

            // Don't generate output for zero-length resource forks, except on ProDOS volumes,
            // so that we don't lose track of which files are extended.
            bool hasRsrcFork = (adfEntry != IFileEntry.NO_ENTRY && attrs.RsrcLength > 0) ||
                (entry.HasRsrcFork && (entry is ProDOS_FileEntry || entry.RsrcLength > 0));

            // All we really need to do here is create entries for one or both forks with
            // the appropriate extract paths.  Filling in the contents happens later.
            switch (mPreserveMode) {
                case ExtractFileWorker.PreserveMode.None:
                    if (entry.HasDataFork) {
                        Entries.Add(new ClipFileEntry(archiveOrFilesystem, entry, adfEntry,
                            dataPart, attrs, extractPath, mPreserveMode, null, appHook));
                    }
                    // Ignore resource fork.
                    break;
                case ExtractFileWorker.PreserveMode.ADF:
                    if (entry.HasDataFork) {
                        Entries.Add(new ClipFileEntry(archiveOrFilesystem, entry, adfEntry,
                            dataPart, attrs, extractPath, mPreserveMode, null, appHook));
                    }
                    if (hasRsrcFork || attrs.HasTypeInfo) {
                        // Form ADF header file name.  Tag it as "resource fork".
                        string adfPath = Path.Combine(Path.GetDirectoryName(extractPath)!,
                            AppleSingle.ADF_PREFIX + Path.GetFileName(extractPath));
                        Entries.Add(new ClipFileEntry(archiveOrFilesystem, entry, adfEntry,
                            FilePart.RsrcFork, attrs, adfPath, mPreserveMode, null, appHook));
                    }
                    break;
                case ExtractFileWorker.PreserveMode.AS:
                    // Form AppleSingle file name.  Output single file for both forks.
                    string asPath = extractPath + AppleSingle.AS_EXT;
                    Entries.Add(new ClipFileEntry(archiveOrFilesystem, entry, adfEntry,
                        dataPart, attrs, asPath, mPreserveMode, null, appHook));
                    break;
                case ExtractFileWorker.PreserveMode.Host:
                    // Output separate files for each fork.
                    if (entry.HasDataFork) {
                        Entries.Add(new ClipFileEntry(archiveOrFilesystem, entry, adfEntry,
                            dataPart, attrs, extractPath, mPreserveMode, null, appHook));
                    }
                    if (hasRsrcFork) {
                        // Generate name for filesystem resource fork (assume Mac OS naming).
                        string rsrcPath = Path.Combine(extractPath, "..namedfork");
                        rsrcPath = Path.Combine(rsrcPath, "rsrc");
                        Entries.Add(new ClipFileEntry(archiveOrFilesystem, entry, adfEntry,
                            FilePart.RsrcFork, attrs, rsrcPath, mPreserveMode, null, appHook));
                    }
                    break;
                case ExtractFileWorker.PreserveMode.NAPS:
                    string napsExt = attrs.GenerateNAPSExt();
                    if (entry.HasDataFork) {
                        Entries.Add(new ClipFileEntry(archiveOrFilesystem, entry, adfEntry,
                            dataPart, attrs, extractPath + napsExt, mPreserveMode,
                            null, appHook));
                    }
                    if (hasRsrcFork) {
                        Entries.Add(new ClipFileEntry(archiveOrFilesystem, entry, adfEntry,
                            FilePart.RsrcFork, attrs, extractPath + napsExt + "r", mPreserveMode,
                            null, appHook));
                    }
                    break;
                default:
                    throw new NotImplementedException("mode not implemented: " + mPreserveMode);
            }
        }

        private void CreateForExport(object archiveOrFilesystem, IFileEntry entry,
                IFileEntry? adfEntry, FileAttribs attrs, string extractPath, AppHook appHook) {
            // We need to establish what sort of conversion will take place so that we can
            // set the appropriate filename extension.  To simplify things a bit, we pass the
            // converter we got into the ClipFileEntry so it's ready to go.

            // need to open data/rsrc streams, extracting copies as needed, so we can
            // do GetApplicableConverters; this could be very slow
            // --> just grab first 64KB for applicability check?  to avoid length check
            //   issues we can set size to full length but only read the first 64KB

            throw new NotImplementedException();
        }
    }
}
