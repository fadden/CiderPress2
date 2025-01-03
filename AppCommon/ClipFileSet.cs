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

using CommonUtil;
using DiskArc;
using DiskArc.Arc;
using DiskArc.FS;
using FileConv;
using static DiskArc.Defs;
using static DiskArc.IFileSystem;

namespace AppCommon {
    /// <summary>
    /// <para>This generates a set of information used when transferring data to the clipboard.
    /// There are two modes: transfer to foreign programs, such as Windows Explorer and Chrome,
    /// and direct transfer to an instance of CiderPress II.</para>
    /// </summary>
    public class ClipFileSet {
        /// <summary>
        /// List of entries for the clipboard.  This will be serialized.
        /// </summary>
        public List<ClipFileEntry> XferEntries { get; set; } = new List<ClipFileEntry>();

        /// <summary>
        /// List of entries that will be streamed to other applications.  This is used to
        /// generate the clipboard data (e.g. FILEDESCRIPTORW and FILECONTENTS in Windows), but
        /// is not directly serialized onto it.
        /// </summary>
        public List<ClipFileEntry> ForeignEntries { get; set; } = new List<ClipFileEntry>();

        //
        // Innards.
        //

        private bool mAddExportExt;
        private bool mStripPaths;
        private bool mEnableMacZip;
        private ExtractFileWorker.PreserveMode mPreserveMode;

        /// <summary>
        /// Export specification, or null for extract.
        /// </summary>
        private ConvConfig.FileConvSpec? mExportSpec;

        /// <summary>
        /// Default export specifications, for "best" export mode.
        /// </summary>
        private Dictionary<string, ConvConfig.FileConvSpec>? mDefaultSpecs;

        /// <summary>
        /// Application hook reference.
        /// </summary>
        private AppHook mAppHook;

        //
        // Additional innards for archives.
        //

        private Dictionary<string, string> mSynthDirs = new Dictionary<string, string>();

        //
        // Additional innards for filesystems.
        //

        private IFileEntry mBaseDir = IFileEntry.NO_ENTRY;
        private bool mUseRawData;

        // Keep track of directories we've already added to the output.
        private Dictionary<IFileEntry, IFileEntry> mAddedFiles =
            new Dictionary<IFileEntry, IFileEntry>();


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
        /// <param name="addExportExt">If true, add a file extension to exported files.</param>
        /// <param name="useRawData">If true, use "raw" mode on appropriate disks.</param>
        /// <param name="stripPaths">If true, strip paths while adding to set.</param>
        /// <param name="enableMacZip">If true, handle Mac ZIP entries.</param>
        /// <param name="exportSpec">Export specification.  If non-null, this will do an export
        ///   operation rather than extract.</param>
        /// <param name="appHook">Application hook reference.</param>
        public ClipFileSet(object archiveOrFileSystem, List<IFileEntry> entries,
                IFileEntry baseDir, ExtractFileWorker.PreserveMode preserveMode,
                bool addExportExt, bool useRawData, bool stripPaths, bool enableMacZip,
                ConvConfig.FileConvSpec? exportSpec,
                Dictionary<string, ConvConfig.FileConvSpec>? defaultSpecs, AppHook appHook) {

            // Stuff the simple items into the object so we don't have to pass them around.
            mBaseDir = baseDir;
            mPreserveMode = preserveMode;
            mAddExportExt = addExportExt;
            mUseRawData = useRawData;
            mStripPaths = stripPaths;
            mEnableMacZip = enableMacZip;
            mExportSpec = exportSpec;
            mDefaultSpecs = defaultSpecs;
            mAppHook = appHook;

            if (archiveOrFileSystem is IArchive) {
                Debug.Assert(baseDir == IFileEntry.NO_ENTRY);
                GenerateFromArchive((IArchive)archiveOrFileSystem, entries);
            } else if (archiveOrFileSystem is IFileSystem) {
                GenerateFromDisk((IFileSystem)archiveOrFileSystem, entries);
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
        private void GenerateFromArchive(IArchive arc, List<IFileEntry> entries) {
            Debug.Assert(XferEntries.Count == 0);
            Debug.Assert(ForeignEntries.Count == 0);

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

                // Synthesize entries for directories.
                if (!mStripPaths) {
                    AddPathDirEntries(arc, entry.FullPathName, entry.DirectorySeparatorChar);
                }

                // Generate file attributes.  The same object will be used for both forks.
                FileAttribs attrs = new FileAttribs(entry);
                if (mStripPaths) {
                    // Configure extract path and attribute path.
                    extractPath = Path.GetFileName(extractPath);
                    attrs.FullPathName = PathName.GetFileName(attrs.FullPathName,
                        attrs.FullPathSep);
                }

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
                            // Update the attributes with values from the ADF header.  This will
                            // set RsrcLength if a resource fork is included.
                            GetMacZipAttribs(arc, adfEntry, attrs, mAppHook);
                        }
                    }
                }

                if (mExportSpec == null) {
                    CreateForExtract(arc, entry, adfEntry, attrs, extractPath);
                } else {
                    CreateForExport(arc, entry, adfEntry, attrs, extractPath);
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
                // Get the attributes from AppleSingle, but don't replace the filename.
                string fileName = attrs.FileNameOnly;
                attrs.GetFromAppleSingle(adfArchiveEntry);
                attrs.FileNameOnly = fileName;
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
        private void AddPathDirEntries(IArchive arc, string pathName, char dirSep) {
            Debug.Assert(!mStripPaths);

            // Remove the filename.
            string? dirName = GetDirectoryName(pathName, dirSep);
            if (string.IsNullOrEmpty(dirName)) {
                // This was nothing but filename.  Nothing for us to do.
                return;
            }
            // See if there's another level.
            AddPathDirEntries(arc, dirName, dirSep);

            // Case-sensitive string comparison.
            if (!mSynthDirs.ContainsKey(dirName)) {
                // Generate an entry for the directory.
                FileAttribs attrs = new FileAttribs();
                attrs.FullPathName = dirName;
                attrs.FullPathSep = dirSep;
                attrs.FileNameOnly = PathName.GetFileName(dirName, dirSep);
                attrs.IsDirectory = true;
                string extractPath = PathName.AdjustPathName(dirName, dirSep,
                    PathName.DEFAULT_REPL_CHAR);
                ForeignEntries.Add(new ClipFileEntry(arc, IFileEntry.NO_ENTRY, IFileEntry.NO_ENTRY,
                    FilePart.DataFork, attrs, extractPath, mPreserveMode, null, null, null,
                    mAppHook));
                if (mExportSpec == null) {
                    XferEntries.Add(new ClipFileEntry(arc, IFileEntry.NO_ENTRY, IFileEntry.NO_ENTRY,
                        FilePart.DataFork, attrs, mAppHook));
                }
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
        /// <param name="entries">List of entries to process.</param>
        private void GenerateFromDisk(IFileSystem fs, List<IFileEntry> entries) {
            Debug.Assert(XferEntries.Count == 0);
            Debug.Assert(ForeignEntries.Count == 0);
            fs.PrepareFileAccess(true);

            IFileEntry aboveRootEntry;
            if (mBaseDir == IFileEntry.NO_ENTRY) {
                aboveRootEntry = IFileEntry.NO_ENTRY;       // same as start==volDir
            } else {
                aboveRootEntry = mBaseDir.ContainingDir;
            }

            foreach (IFileEntry entry in entries) {
                GenerateDiskEntries(fs, entry, aboveRootEntry);
            }
        }

        /// <summary>
        /// Generates an entry in the ClipFileEntry set for the specified entry.  If the entry is
        /// a directory, we recursively generate clip objects for the entry's children.
        /// </summary>
        /// <remarks>
        /// <para>We need to avoid creating two entries for the same file.  This can happen if
        /// the entry list includes "dir1" and "dir1/foo.txt"; note either of these could
        /// appear first.</para>
        /// </remarks>
        /// <param name="fs">Filesystem reference.</param>
        /// <param name="entry">File entry to process.  May be a file or directory.</param>
        /// <param name="aboveRootEntry">Entry above the root, used to limit the partial path
        ///   prefix.</param>
        private void GenerateDiskEntries(IFileSystem fs, IFileEntry entry,
                IFileEntry aboveRootEntry) {
            if (!mStripPaths) {
                // Ensure we have entries for the directories that make up the path.  Windows
                // Explorer doesn't actually require this -- it generates missing paths as
                // needed -- but other systems might be different.  It also gives us a way to
                // create empty directories, and a way to pass the directory file dates along.
                AddMissingDirectories(fs, entry, aboveRootEntry);
            }
            if (mAddedFiles.ContainsKey(entry)) {
                // Already added.
            } else if (entry.IsDirectory) {
                // Current directory, if not stripped, was added above.
                foreach (IFileEntry child in entry) {
                    GenerateDiskEntries(fs, child, aboveRootEntry);
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

                // Generate file attributes.  The same object will be used for both forks.
                FileAttribs attrs = new FileAttribs(entry);
                attrs.FullPathName = ReRootedPathName(entry, aboveRootEntry);
                if (string.IsNullOrEmpty(attrs.FullPathName)) {
                    mAppHook.LogW("Not adding below-root entry: " + entry);
                    return;
                }
                if (mStripPaths) {
                    extractPath = Path.GetFileName(extractPath);
                    attrs.FullPathName =
                        PathName.GetFileName(attrs.FullPathName, attrs.FullPathSep);
                }

                if (mExportSpec == null) {
                    CreateForExtract(fs, entry, IFileEntry.NO_ENTRY, attrs, extractPath);
                } else {
                    CreateForExport(fs, entry, IFileEntry.NO_ENTRY, attrs, extractPath);
                }
                mAddedFiles.Add(entry, entry);
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
                IFileEntry aboveRootEntry) {
            if (entry.ContainingDir == aboveRootEntry) {
                return;
            }
            // Recursively check parents.
            AddMissingDirectories(fs, entry.ContainingDir, aboveRootEntry);
            // Add this one if it's not already present.
            if (entry.IsDirectory && !mAddedFiles.ContainsKey(entry)) {
                // Add this directory to the output list.
                FileAttribs attrs = new FileAttribs(entry);
                attrs.FullPathName = ReRootedPathName(entry, aboveRootEntry);
                attrs.FullPathSep = entry.DirectorySeparatorChar;
                attrs.FileNameOnly = PathName.GetFileName(attrs.FullPathName, attrs.FullPathSep);
                Debug.Assert(attrs.IsDirectory);
                Debug.Assert(!string.IsNullOrEmpty(attrs.FullPathName));
                string extractPath = ExtractFileWorker.GetAdjPathName(entry, aboveRootEntry,
                    Path.DirectorySeparatorChar);
                ForeignEntries.Add(new ClipFileEntry(fs, entry, IFileEntry.NO_ENTRY,
                    FilePart.DataFork, attrs, extractPath, mPreserveMode, null, null, null,
                    mAppHook));
                if (mExportSpec == null) {
                    XferEntries.Add(new ClipFileEntry(fs, entry, IFileEntry.NO_ENTRY,
                        FilePart.DataFork, attrs, mAppHook));
                }
                mAddedFiles.Add(entry, entry);
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
        /// <returns>Partial pathname, or the empty string if the above-root entry is not actually
        ///   above the entry.</returns>
        private static string ReRootedPathName(IFileEntry entry, IFileEntry aboveRootEntry) {
            // Test to see if entry is at or above the root.  If it's at the same level as the
            // root, or above it, it shouldn't be part of the set.
            if (entry.ContainingDir == aboveRootEntry) {
                return string.Empty;
            }
            IFileEntry scanEntry = entry;
            while (scanEntry.ContainingDir != aboveRootEntry) {
                scanEntry = scanEntry.ContainingDir;
                if (scanEntry == IFileEntry.NO_ENTRY) {
                    // Walked to the top without finding it.
                    return string.Empty;
                }
            }

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

        /// <summary>
        /// Creates one or two entries in each of the file sets for the specified file.
        /// </summary>
        private void CreateForExtract(object archiveOrFileSystem, IFileEntry entry,
                IFileEntry adfEntry, FileAttribs attrs, string extractPath) {
            FilePart dataPart;
            bool hasRsrcFork = HasRsrcFork(entry, adfEntry, attrs);

            // Create entries for direct transfer.
            if (entry.HasDataFork) {
                dataPart = mUseRawData ? FilePart.RawData : FilePart.DataFork;
                XferEntries.Add(new ClipFileEntry(archiveOrFileSystem, entry, adfEntry,
                    dataPart, attrs, mAppHook));
            } else if (entry.IsDiskImage) {
                dataPart = FilePart.DiskImage;
                XferEntries.Add(new ClipFileEntry(archiveOrFileSystem, entry, adfEntry,
                    FilePart.DiskImage, attrs, mAppHook));
            } else {
                dataPart = FilePart.Unknown;
            }
            if (hasRsrcFork) {
                XferEntries.Add(new ClipFileEntry(archiveOrFileSystem, entry, adfEntry,
                    FilePart.RsrcFork, attrs, mAppHook));
            }

            // All we really need to do here is create entries for one or both forks with
            // the appropriate extract paths.  Filling in the contents happens later.
            switch (mPreserveMode) {
                case ExtractFileWorker.PreserveMode.None:
                    if (dataPart != FilePart.Unknown) {
                        ForeignEntries.Add(new ClipFileEntry(archiveOrFileSystem, entry, adfEntry,
                            dataPart, attrs, extractPath, mPreserveMode, null, null, null,
                            mAppHook));
                    }
                    // Ignore resource fork.
                    break;
                case ExtractFileWorker.PreserveMode.ADF:
                    if (dataPart != FilePart.Unknown) {
                        ForeignEntries.Add(new ClipFileEntry(archiveOrFileSystem, entry, adfEntry,
                            dataPart, attrs, extractPath, mPreserveMode, null, null, null,
                            mAppHook));
                    }
                    if (hasRsrcFork || attrs.HasTypeInfo) {
                        // Form ADF header file name.  Tag it as "resource fork".
                        string adfPath = Path.Combine(Path.GetDirectoryName(extractPath)!,
                            AppleSingle.ADF_PREFIX + Path.GetFileName(extractPath));
                        ForeignEntries.Add(new ClipFileEntry(archiveOrFileSystem, entry, adfEntry,
                            FilePart.RsrcFork, attrs, adfPath, mPreserveMode, null, null, null,
                            mAppHook));
                    }
                    break;
                case ExtractFileWorker.PreserveMode.AS:
                    // Form AppleSingle file name.  Output single file for both forks.
                    string asPath = extractPath + AppleSingle.AS_EXT;
                    ForeignEntries.Add(new ClipFileEntry(archiveOrFileSystem, entry, adfEntry,
                        dataPart, attrs, asPath, mPreserveMode, null, null, null, mAppHook));
                    break;
                case ExtractFileWorker.PreserveMode.Host:
                    // Output separate files for each fork.
                    if (dataPart != FilePart.Unknown) {
                        ForeignEntries.Add(new ClipFileEntry(archiveOrFileSystem, entry, adfEntry,
                            dataPart, attrs, extractPath, mPreserveMode, null, null, null,
                            mAppHook));
                    }
                    if (hasRsrcFork) {
                        // Generate name for filesystem resource fork (assume Mac OS naming).
                        string rsrcPath = Path.Combine(extractPath, "..namedfork");
                        rsrcPath = Path.Combine(rsrcPath, "rsrc");
                        ForeignEntries.Add(new ClipFileEntry(archiveOrFileSystem, entry, adfEntry,
                            FilePart.RsrcFork, attrs, rsrcPath, mPreserveMode, null, null, null,
                            mAppHook));
                    }
                    break;
                case ExtractFileWorker.PreserveMode.NAPS:
                    string napsExt = attrs.GenerateNAPSExt();
                    if (dataPart != FilePart.Unknown) {
                        ForeignEntries.Add(new ClipFileEntry(archiveOrFileSystem, entry, adfEntry,
                            dataPart, attrs, extractPath + napsExt, mPreserveMode,
                            null, null, null, mAppHook));
                    }
                    if (hasRsrcFork) {
                        ForeignEntries.Add(new ClipFileEntry(archiveOrFileSystem, entry, adfEntry,
                            FilePart.RsrcFork, attrs, extractPath + napsExt + "r", mPreserveMode,
                            null, null, null, mAppHook));
                    }
                    break;
                default:
                    throw new NotImplementedException("mode not implemented: " + mPreserveMode);
            }
        }

        /// <summary>
        /// Creates a new entry in the "foreign" set for the converted file.  If the conversion
        /// isn't possible, no entry is created.
        /// </summary>
        /// <remarks>
        /// No entries are added for the "xfer" set.
        /// </remarks>
        private void CreateForExport(object archiveOrFileSystem, IFileEntry entry,
                IFileEntry adfEntry, FileAttribs attrs, string extractPath) {
            // We need to establish what sort of conversion will take place so that we can
            // set the appropriate filename extension.  This requires opening all forks, with
            // seekable streams.
            //
            // TODO? We don't necessarily need to extract the entire stream; for applicability
            // tests we should be able to just read the first several KB.  (We may need to set
            // the length to match the original to satisfy bounds checks; for certain types of
            // compression the length isn't knowable without extracting the full thing.)

            Debug.Assert(mExportSpec != null);

            Type? expectedType = DoClipExport(archiveOrFileSystem, entry, adfEntry, attrs,
                mUseRawData, mExportSpec, mDefaultSpecs, null, mAppHook);
            if (expectedType == null) {
                return;
            }
            Debug.Assert(!string.IsNullOrEmpty(attrs.FileNameOnly));
            string ext;
            if (!mAddExportExt) {
                ext = string.Empty;
            } else if (expectedType == typeof(SimpleText)) {
                ext = TXTGenerator.FILE_EXT;
            } else if (expectedType == typeof(FancyText)) {
                ext = RTFGenerator.FILE_EXT;
            } else if (expectedType == typeof(CellGrid)) {
                ext = CSVGenerator.FILE_EXT;
            } else if (expectedType == typeof(IBitmap)) {
                ext = PNGGenerator.FILE_EXT;
            } else if (expectedType == typeof(HostConv)) {
                ext = string.Empty;
            } else {
                Debug.Assert(false);
                ext = ".UNK";
            }

            // Create the clip entry.  We can't just pass the Converter instance in because
            // it has references to the input streams, which will be closed before this
            // function returns.  The only thing that really matters is that the output type
            // matches, because if it doesn't then the file extension we set here will be
            // incorrect.
            ForeignEntries.Add(new ClipFileEntry(archiveOrFileSystem, entry, adfEntry,
                FilePart.DataFork, attrs, extractPath + ext, mPreserveMode,
                mExportSpec, mDefaultSpecs, expectedType, mAppHook));
        }

        /// <summary>
        /// Performs all or part of a file export operation.  For the "copy" side we evaluate
        /// the file and return converter type information.  For the "paste" side we also do
        /// the actual conversion.
        /// </summary>
        /// <param name="outStream">Stream to write the output to.  If null, the conversion is
        ///   not performed.</param>
        /// <returns>Type of Converter subclass that will be used.</returns>
        public static Type? DoClipExport(object archiveOrFileSystem, IFileEntry entry,
                IFileEntry adfEntry, FileAttribs attrs, bool useRawData,
                ConvConfig.FileConvSpec exportSpec,
                Dictionary<string, ConvConfig.FileConvSpec>? defaultSpecs,
                Stream? outStream, AppHook appHook) {
            FilePart dataPart = useRawData ? FilePart.RawData : FilePart.DataFork;
            bool hasRsrcFork = HasRsrcFork(entry, adfEntry, attrs);

            Stream? dataStream = null;
            Stream? rsrcStream = null;
            AppleSingle? adfArchive = null;
            Stream? adfStream = null;
            Stream? dataCopy = null;
            Stream? rsrcCopy = null;
            try {
                if (entry.HasDataFork) {
                    dataStream = OpenEntryPart(archiveOrFileSystem, entry, dataPart);
                } else if (entry.IsDiskImage) {
                    // Exporting a disk image?  Most unexpected.
                    dataStream = OpenEntryPart(archiveOrFileSystem, entry, FilePart.DiskImage);
                }

                if (adfEntry != IFileEntry.NO_ENTRY) {
                    // Handle paired MacZip entry.  Need to extract attributes and check for
                    // a resource fork.
                    try {
                        IArchive arc = (IArchive)archiveOrFileSystem;
                        // Copy to temp file; can't use unseekable stream as archive source.
                        adfStream = ArcTemp.ExtractToTemp(arc, adfEntry, dataPart);
                        adfArchive = AppleSingle.OpenArchive(adfStream, appHook);
                        IFileEntry adfArchiveEntry = adfArchive.GetFirstEntry();
                        // Copy the ADF attributes out, but retain the original filename.
                        string fileName = attrs.FileNameOnly;
                        attrs.GetFromAppleSingle(adfArchiveEntry);
                        attrs.FileNameOnly = fileName;
                        if (adfArchiveEntry.HasRsrcFork && adfArchiveEntry.RsrcLength > 0) {
                            rsrcStream = adfArchive.OpenPart(adfArchiveEntry, FilePart.RsrcFork);
                        }
                    } catch (Exception ex) {
                        // Never mind.
                        appHook.LogW("Unable to get ADF attrs for '" +
                            entry.FullPathName + "': " + ex.Message);
                        // keep going
                        Debug.Assert(rsrcStream == null);
                    }
                }

                // If we didn't get a resource fork from ADF, see if the entry has one.
                if (rsrcStream == null && hasRsrcFork) {
                    rsrcStream = OpenEntryPart(archiveOrFileSystem, entry, FilePart.RsrcFork);
                }

                // Copy to seekable stream if necessary.
                if (dataStream != null && !dataStream.CanSeek) {
                    dataCopy = TempFile.CopyToTemp(dataStream, attrs.DataLength);
                } else {
                    dataCopy = dataStream;
                    dataStream = null;      // don't close twice
                }
                if (rsrcStream != null && !rsrcStream.CanSeek) {
                    rsrcCopy = TempFile.CopyToTemp(rsrcStream, attrs.RsrcLength);
                } else {
                    rsrcCopy = rsrcStream;
                    rsrcStream = null;      // don't close twice
                }

                // Find the converter.  If we don't find one, don't export the file.  This allows
                // the user to drag a big pile of stuff and only get the stuff that matches.
                Converter? conv;
                if (exportSpec.Tag == ConvConfig.BEST) {
                    List<Converter> applics = ExportFoundry.GetApplicableConverters(attrs,
                        dataCopy, rsrcCopy, appHook);
                    conv = applics[0];
                    // Apply default options, if any.
                    exportSpec = new ConvConfig.FileConvSpec(conv.Tag);
                    if (defaultSpecs!.TryGetValue(conv.Tag,
                            out ConvConfig.FileConvSpec? defaults)) {
                        exportSpec.MergeSpec(defaults);
                    }
                } else {
                    // One specific converter.
                    conv = ExportFoundry.GetConverter(exportSpec.Tag, attrs, dataCopy, rsrcCopy,
                        appHook);
                    if (conv == null) {
                        appHook.LogE("no converter found for tag '" + exportSpec.Tag + "'");
                        return null;
                    }
                    if (conv.Applic <= Converter.Applicability.Not) {
                        appHook.LogW("converter " + conv + " is not suitable for '" +
                            attrs.FileNameOnly + "'");
                        return null;
                    }
                }

                // If an output stream is provided, do the conversion and generate the output.
                if (outStream != null) {
                    IConvOutput convOutput = conv.ConvertFile(exportSpec.Options);
                    if (convOutput is ErrorText) {
                        appHook.LogW("conversion failed: " +
                            ((ErrorText)convOutput).Text.ToString());
                        return null;
                    } else if (convOutput is FancyText && !((FancyText)convOutput).PreferSimple) {
                        RTFGenerator.Generate((FancyText)convOutput, outStream);
                    } else if (convOutput is SimpleText) {
                        TXTGenerator.Generate((SimpleText)convOutput, outStream);
                    } else if (convOutput is CellGrid) {
                        CSVGenerator.Generate((CellGrid)convOutput, outStream);
                    } else if (convOutput is IBitmap) {
                        PNGGenerator.Generate((IBitmap)convOutput, outStream);
                    } else if (convOutput is HostConv) {
                        // Copy directly to output.
                        if (dataCopy != null) {
                            dataCopy.Position = 0;
                            dataCopy.CopyTo(outStream);
                        }
                    } else {
                        Debug.Assert(false, "unknown IConvOutput impl " + convOutput);
                        return null;
                    }
                    if (convOutput is IBitmap) {
                        return typeof(IBitmap);
                    } else if (convOutput is FancyText && ((FancyText)convOutput).PreferSimple) {
                        return typeof(SimpleText);
                    } else {
                        return convOutput.GetType();
                    }
                } else {
                    Type expectedType = conv.GetExpectedType(exportSpec.Options);
                    return expectedType;
                }
            } finally {
                dataStream?.Dispose();
                rsrcStream?.Dispose();
                adfArchive?.Dispose();
                adfStream?.Dispose();
                dataCopy?.Dispose();
                rsrcCopy?.Dispose();
            }
        }

        /// <summary>
        /// Determines whether the entry has a resource fork.  This is complicated by MacZip
        /// and the desire to preserve ProDOS extended storage type status.
        /// </summary>
        private static bool HasRsrcFork(IFileEntry entry, IFileEntry adfEntry, FileAttribs attrs) {
            if (adfEntry != IFileEntry.NO_ENTRY) {
                // MacZip entry.  See if the ADF header has a non-empty resource fork.
                Debug.Assert(entry is Zip_FileEntry);
                return attrs.RsrcLength > 0;
            }
            if (!entry.HasRsrcFork) {
                return false;
            }
            // We want to return true for zero-length resource forks if we find them in ProDOS
            // volumes or NuFX archives.  In these formats the resource fork is optional, and
            // we don't want to lose the fact that the file is extended.
            if (attrs.RsrcLength == 0) {
                return entry is ProDOS_FileEntry || entry is NuFX_FileEntry;
            }
            return attrs.RsrcLength > 0;
        }

        /// <summary>
        /// Opens a part of a file, in an archive or filesystem.
        /// </summary>
        private static Stream OpenEntryPart(object archiveOrFileSystem, IFileEntry entry,
                FilePart part) {
            if (archiveOrFileSystem is IArchive) {
                IArchive arc = (IArchive)archiveOrFileSystem;
                return arc.OpenPart(entry, part);
            } else if (archiveOrFileSystem is IFileSystem) {
                IFileSystem fs = (IFileSystem)archiveOrFileSystem;
                return fs.OpenFile(entry, FileAccessMode.ReadOnly, part);
            } else {
                throw new NotImplementedException("Unexpected: " + archiveOrFileSystem);
            }
        }
    }
}
