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
    /// system clipboard.</para>
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
        private ExtractFileWorker.PreserveMode mPreserveMode;
        [NonSerialized]
        private ConvConfig.FileConvSpec? mExportSpec;

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
        /// <param name="archiveOrFileSystem">IArchive or IFileSystem instance.</param>
        /// <param name="entries">List of entries to add.</param>
        /// <param name="baseDir">For disk images, the effective root of the filesystem.</param>
        /// <param name="preserveMode">Preservation mode to use for extracted files.</param>
        /// <param name="useRawData">If true, use "raw" mode on appropriate disks.</param>
        /// <param name="stripPaths">If true, strip paths while adding to set.</param>
        /// <param name="exportSpec">Export specification.  If non-null, this will do an export
        ///   operation rather than extract.</param>
        /// <param name="appHook">Application hook reference.</param>
        public ClipFileSet(object archiveOrFileSystem, List<IFileEntry> entries,
                IFileEntry baseDir, ExtractFileWorker.PreserveMode preserveMode,
                bool useRawData, bool stripPaths, ConvConfig.FileConvSpec? exportSpec,
                AppHook appHook) {

            // Stuff the simple items into the object so we don't have to pass them around.
            mBaseDir = baseDir;
            mPreserveMode = preserveMode;
            mUseRawData = useRawData;
            mStripPaths = stripPaths;
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

        private void GenerateFromArchive(IArchive arc, List<IFileEntry> entries, AppHook appHook) {
            Debug.Assert(Entries.Count == 0);

            // TODO:
            // - synthesize directory entries as needed; maintain dict with dirs
            // - okay if we create multiple copies with different upper/lower case, so
            //   long as the receiver ignores the duplicates on case-insensitive fs
        }

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
                AddMissingDirectories(fs, entry, aboveRootEntry);
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
                    CreateForExtract(fs, entry, attrs, extractPath);
                } else {
                    CreateForExport(fs, entry, attrs, extractPath);
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
        private void AddMissingDirectories(IFileSystem fs, IFileEntry entry,
                IFileEntry aboveRootEntry) {
            if (entry.ContainingDir == aboveRootEntry) {
                return;
            }
            // Recursively check parents.
            AddMissingDirectories(fs, entry.ContainingDir, aboveRootEntry);
            if (entry.IsDirectory && !mAddedDirs.ContainsKey(entry)) {
                // Add this directory to the output list.
                FileAttribs attrs = new FileAttribs(entry);
                attrs.FullPathName = ReRootedPathName(entry, aboveRootEntry);
                string extractPath = ExtractFileWorker.GetAdjPathName(entry, aboveRootEntry,
                    Path.DirectorySeparatorChar);
                Entries.Add(new ClipFileEntry(fs, entry, FilePart.DataFork, attrs, extractPath,
                    mPreserveMode, null));
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
                FileAttribs attrs, string extractPath) {
            FilePart dataPart = mUseRawData ? FilePart.RawData : FilePart.DataFork;

            // All we really need to do here is create entries for one or both forks with
            // the appropriate extract paths.  Filling in the contents happens later.
            switch (mPreserveMode) {
                case ExtractFileWorker.PreserveMode.None:
                    // TODO: create entry for data fork, nothing for rsrc, no name change
                case ExtractFileWorker.PreserveMode.ADF:
                    // TODO: create separate entries for data / rsrc, modified names
                case ExtractFileWorker.PreserveMode.AS:
                    // TODO: create single entry for both forks, modified name
                case ExtractFileWorker.PreserveMode.Host:
                    // TODO: create single entry for both forks, no name change
                case ExtractFileWorker.PreserveMode.NAPS:
                    // TODO: create separate entries for data / rsrc, modified names
                    Entries.Add(new ClipFileEntry(archiveOrFilesystem, entry, FilePart.DataFork,
                        attrs, extractPath, mPreserveMode, null));
                    break;
                default:
                    throw new NotImplementedException("mode not implemented: " + mPreserveMode);
            }
        }

        private void CreateForExport(object archiveOrFilesystem, IFileEntry entry,
                FileAttribs attrs, string extractPath) {
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
