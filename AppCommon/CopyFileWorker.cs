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
using static DiskArc.Defs;
using static DiskArc.IFileSystem;

namespace AppCommon {
    /// <summary>
    /// File copy manager.  This does most of the work.
    /// </summary>
    public class CopyFileWorker {
        /// <summary>
        /// If set, text files copied to and from DOS volumes will be converted to or from
        /// high ASCII.
        /// </summary>
        public bool ConvertDOSText { get; set; } = true;

        /// <summary>
        /// If set, files added to archives with compression features will be compressed using
        /// the default compression format.
        /// </summary>
        public bool DoCompress { get; set; } = true;

        /// <summary>
        /// If set, files added to a ZIP archive that have resource forks or HFS types will
        /// be stored as AppleDouble with a "__MACOSX" prefix.
        /// </summary>
        public bool EnableMacOSZip { get; set; } = false;

        /// <summary>
        /// If set, don't do a full scan on filesystems.
        /// </summary>
        public bool FastScan { get; set; } = false;

        /// <summary>
        /// If set, strip pathnames off of files before adding them.  For a filesystem, all
        /// files will be added to the target directory.
        /// </summary>
        public bool StripPaths { get; set; } = false;

        /// <summary>
        /// Callback function interface definition.
        /// </summary>
        public delegate CallbackFacts.Results CallbackFunc(CallbackFacts what, object? obj);

        private CallbackFunc mFunc;
        private AppHook mAppHook;
        private byte[]? mCopyBuf = null;


        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="func">Callback function, for progress messages and file overwrite
        ///   queries.</param>
        /// <param name="appHook">Application hook reference.</param>
        public CopyFileWorker(CallbackFunc func, AppHook appHook) {
            mFunc = func;
            mAppHook = appHook;
        }

        #region To IArchive

        /// <summary>
        /// Copies entries from a file archive or filesystem to a file archive.  Because the
        /// destination is an archive, this method just sets up the transactions for the caller
        /// to commit.
        /// </summary>
        /// <param name="srcObj">Source (IArchive or IFileSystem).</param>
        /// <param name="entries">Entries to copy.</param>
        /// <param name="dstArchive">Destination archive.</param>
        /// <returns>True on success, false on error or user cancellation.</returns>
        public bool CopyToArchive(object srcObj, List<IFileEntry> entries, IArchive dstArchive) {
            Debug.Assert(!dstArchive.Characteristics.HasSingleEntry);
            bool canRsrcFork = dstArchive.Characteristics.HasResourceForks ||
                (dstArchive is Zip && EnableMacOSZip);
            bool doStripPaths = StripPaths ||
                dstArchive.Characteristics.DefaultDirSep == IFileEntry.NO_DIR_SEP;

            // Fix number of entries to copy, removing directories and MacZip headers.
            int entryCount = entries.Count;
            foreach (IFileEntry entry in entries) {
                if (entry.IsDirectory) {
                    entryCount--;
                } else if (srcObj is Zip && EnableMacOSZip) {
                    if (entry.IsMacZipHeader()) {
                        entryCount--;
                    }
                }
            }
            if (entryCount == 0) {
                return true;        // must have been nothing but empty directories
            }

            // Generate a list of archive contents, used when checking for duplicate entries.  We
            // want to do case-insensitive comparisons for general consistency.
            Dictionary<string, IFileEntry> dupCheck =
                new Dictionary<string, IFileEntry>(StringComparer.InvariantCultureIgnoreCase);
            foreach (IFileEntry entry in dstArchive) {
                // Archive might already have duplicates, so don't call Add().
                dupCheck[entry.FileName] = entry;
            }

            int doneCount = 0;
            foreach (IFileEntry srcEntry in entries) {
                if (srcEntry.IsDirectory) {
                    continue;
                }
                if (!srcEntry.HasDataFork && srcEntry.HasRsrcFork && !canRsrcFork) {
                    // Nothing but a resource fork, and we can't store those.  Report that
                    // we're skipping it, and move on.
                    CallbackFacts facts =
                        new CallbackFacts(CallbackFacts.Reasons.ResourceForkIgnored,
                        srcEntry.FullPathName, srcEntry.DirectorySeparatorChar);
                    facts.Part = FilePart.RsrcFork;
                    mFunc(facts, null);
                    doneCount++;
                    continue;
                }

                // Handle MacZip in source archives, if enabled.
                IFileEntry adfEntry = IFileEntry.NO_ENTRY;
                if (srcObj is Zip && EnableMacOSZip) {
                    // Only handle __MACOSX/ entries when paired with other entries.
                    if (srcEntry.IsMacZipHeader()) {
                        continue;
                    }
                    // Check to see if we have a paired entry.
                    string macZipName = Zip.GenerateMacZipName(srcEntry.FullPathName);
                    if (!string.IsNullOrEmpty(macZipName)) {
                        ((Zip)srcObj).TryFindFileEntry(macZipName, out adfEntry);
                    }
                }

                // Fix the individual path components as needed.
                string? adjPath = AdjustPathForArchive(srcObj, srcEntry, dstArchive, doStripPaths);
                if (adjPath == null) {
                    CallbackFacts facts = new CallbackFacts(CallbackFacts.Reasons.PathTooLong,
                        srcEntry.FullPathName, srcEntry.DirectorySeparatorChar);
                    CallbackFacts.Results result = mFunc(facts, null);
                    if (result == CallbackFacts.Results.Skip) {
                        continue;
                    } else {
                        return false;
                    }
                }

                if (dupCheck.TryGetValue(adjPath, out IFileEntry? dupEntry)) {
                    CallbackFacts facts = new CallbackFacts(CallbackFacts.Reasons.FileNameExists,
                        adjPath, dstArchive.Characteristics.DefaultDirSep);
                    CallbackFacts.Results result = mFunc(facts, null);
                    switch (result) {
                        case CallbackFacts.Results.Cancel:
                            return false;
                        case CallbackFacts.Results.Skip:
                            adjPath = null;
                            break;
                        case CallbackFacts.Results.Overwrite:
                            break;
                        default:
                            Debug.Assert(false);
                            break;
                    }
                }
                if (adjPath == null) {
                    continue;
                }
                if (dupEntry != null) {
                    dstArchive.DeleteRecord(dupEntry);
                }

                Debug.Assert(srcEntry.HasDataFork || srcEntry.HasRsrcFork);

                // Create new record.
                IFileEntry newEntry = dstArchive.CreateRecord();
                newEntry.FileName = adjPath;
                CopyAttributes(srcEntry, newEntry);

                // Collect attributes from source entry.  If this is MacZip we will overwrite.
                FileAttribs srcAttrs = new FileAttribs(srcEntry);

                // Handle paired MacZip entry.  If found, we want to use the attributes stored
                // in the AppleDouble file rather than the ZIP file entry.  We also need to see
                // if there's a resource fork.
                if (adfEntry != IFileEntry.NO_ENTRY) {
                    AppleSingle? adfArchive = null;
                    Stream? adfStream = null;
                    try {
                        // Extract to temp file; can't use unseekable stream as archive source.
                        adfStream =
                            ArcTemp.ExtractToTemp((Zip)srcObj, adfEntry, FilePart.DataFork);
                        adfArchive = AppleSingle.OpenArchive(adfStream, mAppHook);
                        IFileEntry adfArchiveEntry = adfArchive.GetFirstEntry();
                        // Copy attributes from ADF to the new entry, overwriting values except
                        // for the filename.  Keep the ZIP dates if they're better than the ADF.
                        CopyAttributes(adfArchiveEntry, newEntry);
                        // Make a copy of the attributes in case the destination is MacZip.  The
                        // new entry object won't retain all of the values.
                        srcAttrs.GetFromAppleSingle(adfArchiveEntry);
                    } finally {
                        adfArchive?.Dispose();
                        adfStream?.Dispose();
                    }
                }

                CompressionFormat fmt =
                    DoCompress ? CompressionFormat.Default : CompressionFormat.Uncompressed;
                int dataPerc = (100 * doneCount) / entryCount;
                int rsrcPerc = (int)((100 * (doneCount + 0.5)) / entryCount);

                // Set up data fork part.
                if (srcEntry.HasDataFork) {
                    CopyFileSource cpSource = new CopyFileSource(srcObj, srcEntry,
                        FilePart.DataFork, false, mFunc, dataPerc, mAppHook);
                    dstArchive.AddPart(newEntry, FilePart.DataFork, cpSource, fmt);
                }

                // Set up rsrc fork part.
                if (dstArchive is Zip && EnableMacOSZip) {
                    if (!srcEntry.HasDataFork) {
                        // AppleDouble needs both parts, and we didn't find one earlier.
                        Debug.Assert(srcEntry.HasRsrcFork); // shouldn't be here otherwise
                        SimplePartSource zeroSource =
                            new SimplePartSource(new MemoryStream(new byte[0]));
                        dstArchive.AddPart(newEntry, FilePart.DataFork, zeroSource, fmt);
                    }
                    // Create the "header" file if there's a resource fork, or if we just need
                    // to preserve the file type info.
                    bool hasRsrcFork = srcEntry.HasRsrcFork && (srcEntry.RsrcLength > 0 ||
                        (srcEntry is ProDOS_FileEntry || srcEntry is NuFX_FileEntry));
                    if (hasRsrcFork || adfEntry != IFileEntry.NO_ENTRY || srcAttrs.HasTypeInfo) {
                        AddMacZipRecord(srcObj, srcEntry, adjPath, dstArchive, adfEntry,
                            srcAttrs, rsrcPerc, fmt);
                    }
                } else if (srcEntry.HasRsrcFork || adfEntry != IFileEntry.NO_ENTRY) {
                    if (!canRsrcFork) {
                        // Nowhere to put this resource fork.  Report it and move on.
                        Debug.Assert(srcEntry.HasDataFork);
                        CallbackFacts facts = new CallbackFacts(
                            CallbackFacts.Reasons.ResourceForkIgnored,
                            srcEntry.FullPathName, srcEntry.DirectorySeparatorChar);
                        facts.Part = FilePart.RsrcFork;
                        mFunc(facts, null);
                    } else if (srcEntry.RsrcLength != 0 ||
                            (srcEntry is ProDOS_FileEntry || srcEntry is NuFX_FileEntry)) {
                        // Add resource fork to record created earlier.
                        CopyFileSource cpSource;
                        if (adfEntry == IFileEntry.NO_ENTRY) {
                            // Source has an actual resource fork.
                            cpSource = new CopyFileSource(srcObj, srcEntry, FilePart.RsrcFork,
                                false, mFunc, rsrcPerc, mAppHook);
                        } else {
                            // Source is a MacZip AppleDouble file.
                            cpSource = new CopyFileSource(srcObj, adfEntry, FilePart.RsrcFork,
                                true, mFunc, rsrcPerc, mAppHook);
                        }
                        dstArchive.AddPart(newEntry, FilePart.RsrcFork, cpSource, fmt);
                    }
                }

                doneCount++;
            }

            return true;
        }

        /// <summary>
        /// Adjusts a path to be compatible with the destination file archive.
        /// </summary>
        /// <param name="srcObj">Source, may be IArchive or IFileSystem.</param>
        /// <param name="entry">Entry to adjust.</param>
        /// <param name="dstArchive">Destination IArchive.</param>
        /// <param name="doStripPaths">If set, remove the directory name.</param>
        /// <returns>New path, or null if it didn't fit.</returns>
        private static string? AdjustPathForArchive(object srcObj, IFileEntry entry,
                IArchive dstArchive, bool doStripPaths) {
            string result;
            if (srcObj is IArchive) {
                if (entry.DirectorySeparatorChar == IFileEntry.NO_DIR_SEP) {
                    // Source archive doesn't support partial paths.
                    result = dstArchive.AdjustFileName(entry.FullPathName);
                } else {
                    string[] parts = entry.FullPathName.Split(entry.DirectorySeparatorChar,
                        StringSplitOptions.RemoveEmptyEntries);
                    if (doStripPaths) {
                        result = dstArchive.AdjustFileName(parts[parts.Length - 1]);
                    } else {
                        StringBuilder sb = new StringBuilder(entry.FullPathName.Length);
                        char dstDirSep = dstArchive.Characteristics.DefaultDirSep;
                        for (int i = 0; i < parts.Length; i++) {
                            sb.Append(dstArchive.AdjustFileName(parts[i]));
                            if (i != parts.Length - 1) {
                                sb.Append(dstDirSep);
                            }
                        }
                        result = sb.ToString();
                    }
                }
            } else {
                if (doStripPaths) {
                    result = dstArchive.AdjustFileName(entry.FileName);
                } else {
                    StringBuilder sb = new StringBuilder();
                    AdjustFileSystemPath(entry, dstArchive, sb);
                    return sb.ToString();
                }
            }
            if (!dstArchive.CheckStorageName(result)) {
                return null;
            }
            return result;
        }

        /// <summary>
        /// Recursively builds the full pathname, adjusting components to make them suitable for
        /// use in the destination archive.
        /// </summary>
        /// <param name="entry">Current file entry.</param>
        /// <param name="dstArchive">Target archive.</param>
        /// <param name="sb">Pathname, in progress.</param>
        private static void AdjustFileSystemPath(IFileEntry entry, IArchive dstArchive,
                StringBuilder sb) {
            if (entry.ContainingDir == IFileEntry.NO_ENTRY) {
                // We've hit the volume directory.  Don't add a path component for it.
                return;
            }
            AdjustFileSystemPath(entry.ContainingDir, dstArchive, sb);
            if (sb.Length > 0) {
                sb.Append(dstArchive.Characteristics.DefaultDirSep);
            }
            string fileName = dstArchive.AdjustFileName(entry.FileName);
            sb.Append(fileName);
        }

        /// <summary>
        /// Adds a special MacOSX ZIP record that will hold file type data and/or resource fork.
        /// </summary>
        /// <param name="srcObj">Source (IArchive or IFileSystem).</param>
        /// <param name="srcEntry">Source entry.  This entry may hold the resource fork, or
        ///   may be the data fork of a file that has nonzero type information.</param>
        /// <param name="adjPath">Adjusted pathname.</param>
        /// <param name="dstArchive">Destination ZIP archive.</param>
        /// <param name="adfEntry">If the source is also MacZip, this is the entry for the ADF
        ///   header record.</param>
        /// <param name="srcAttrs">Attributes for the source file.  This may have come from
        ///   srcEntry, or may have come out of the ADF record contents.</param>
        /// <param name="rsrcPerc">Percent completion to show when processing this record.</param>
        /// <param name="fmt">Compression format for the new record.</param>
        private void AddMacZipRecord(object srcObj, IFileEntry srcEntry, string adjPath,
                IArchive dstArchive, IFileEntry adfEntry, FileAttribs srcAttrs,
                int rsrcPerc, CompressionFormat fmt) {
            Debug.Assert(dstArchive is Zip);
            Debug.Assert(srcEntry.HasRsrcFork || srcAttrs.HasTypeInfo ||
                adfEntry != IFileEntry.NO_ENTRY);

            // Create a new record in the destination ZIP archive.
            IFileEntry rsrcEntry = dstArchive.CreateRecord();

            // Form __MACOSX/path/._filename string.
            StringBuilder sb = new StringBuilder();
            sb.Append(Zip.MAC_ZIP_RSRC_PREFIX);
            sb.Append(dstArchive.Characteristics.DefaultDirSep);
            int fileNameSplit = adjPath.LastIndexOf(dstArchive.Characteristics.DefaultDirSep);
            if (fileNameSplit < 0) {
                // Just the filename.
                sb.Append(AppleSingle.ADF_PREFIX);
                sb.Append(adjPath);
            } else {
                sb.Append(adjPath.Substring(0, fileNameSplit + 1));
                sb.Append(AppleSingle.ADF_PREFIX);
                sb.Append(adjPath.Substring(fileNameSplit + 1));
            }

            rsrcEntry.FileName = sb.ToString();
            rsrcEntry.DirectorySeparatorChar = dstArchive.Characteristics.DefaultDirSep;
            rsrcEntry.ModWhen = srcEntry.ModWhen;        // match the modification date

            // Add the part.  This will be an AppleDouble "header" file that has the file
            // type info and (if present) the resource fork.  This will be generated on the fly
            // so we don't have to read the input file up front and hold onto it while we work.
            CopyFileSource cpSource;
            if (adfEntry == IFileEntry.NO_ENTRY) {
                // From a normal source.  Might not have a resource fork.
                cpSource = new CopyFileSource(srcObj, srcEntry, FilePart.RsrcFork,
                    false, mFunc, rsrcPerc, srcAttrs, mAppHook);
            } else {
                // From a MacZip source.  (We don't have the AppleDouble entry handy, but it's
                // captured in "adfAttrs".)
                cpSource = new CopyFileSource(srcObj, adfEntry, FilePart.RsrcFork,
                    true, mFunc, rsrcPerc, srcAttrs, mAppHook);
            }
            dstArchive.AddPart(rsrcEntry, FilePart.DataFork, cpSource, fmt);
        }

        #endregion To IArchive

        #region To IFileSystem

        /// <summary>
        /// Copies entries from a file archive or filesystem to an IFileSystem.  All actions
        /// are performed immediately.
        /// </summary>
        /// <param name="srcObj">Source (IArchive or IFileSystem).</param>
        /// <param name="entries">Entries to copy.</param>
        /// <param name="dstFs">Destination filesystem.</param>
        /// <param name="dstDirEntry">Effective root directory in destination filesystem.</param>
        /// <returns>True on success, false on error or user cancellation.</returns>
        public bool CopyToDisk(object srcObj, IFileEntry srcDirEntry, List<IFileEntry> entries,
                IFileSystem dstFs, IFileEntry dstDirEntry) {
            if (dstDirEntry != IFileEntry.NO_ENTRY) {
                Debug.Assert(dstDirEntry.IsDirectory);
                Debug.Assert(dstDirEntry.GetFileSystem() == dstFs);
            }

            bool canRsrcFork = dstFs.Characteristics.HasResourceForks;
            bool doStripPaths = StripPaths || !dstFs.Characteristics.IsHierarchical;
            bool doRawXfer = srcObj is DOS && dstFs is DOS;

            // Fix number of entries to copy, removing directories and MacZip headers.
            int entryCount = entries.Count;
            foreach (IFileEntry entry in entries) {
                if (entry.IsDirectory) {
                    entryCount--;
                } else if (srcObj is Zip && EnableMacOSZip) {
                    if (entry.IsMacZipHeader()) {
                        entryCount--;
                    }
                }
            }
            if (entryCount == 0) {
                return true;        // must have been nothing but empty directories
            }

            IFileEntry targetDirEnt =
                (dstDirEntry == IFileEntry.NO_ENTRY) ? dstFs.GetVolDirEntry() : dstDirEntry;

            int doneCount = 0;
            foreach (IFileEntry srcEntry in entries) {
                if (srcEntry.IsDirectory) {
                    continue;
                }
                if (!srcEntry.HasDataFork && srcEntry.HasRsrcFork && !canRsrcFork) {
                    CallbackFacts facts = new CallbackFacts(
                        CallbackFacts.Reasons.ResourceForkIgnored, srcEntry.FullPathName,
                        srcEntry.DirectorySeparatorChar);
                    facts.Part = FilePart.RsrcFork;
                    mFunc(facts, null);
                    doneCount++;
                    continue;
                }

                // Handle MacZip in source archives, if enabled.
                IFileEntry adfEntry = IFileEntry.NO_ENTRY;
                if (srcObj is Zip && EnableMacOSZip) {
                    // Only handle __MACOSX/ entries when paired with other entries.
                    if (srcEntry.IsMacZipHeader()) {
                        continue;
                    }
                    // Check to see if we have a paired entry.
                    string macZipName = Zip.GenerateMacZipName(srcEntry.FullPathName);
                    if (!string.IsNullOrEmpty(macZipName)) {
                        ((Zip)srcObj).TryFindFileEntry(macZipName, out adfEntry);
                    }
                }

                IFileEntry subDirEnt;
                if (doStripPaths) {
                    // No need to create directories.  Everything goes into the target dir.
                    subDirEnt = targetDirEnt;
                } else {
                    try {
                        // TODO: this would be more efficient for multiple consecutive adds to
                        // the same directory if we remembered the path / entry from the previous
                        // iteration.
                        subDirEnt = CreateSubdirectories(dstFs, targetDirEnt, srcObj, srcDirEntry,
                            srcEntry);
                    } catch (IOException ex) {
                        ReportFailure(ex.Message);
                        return false;
                    }
                }

                string adjFileName = dstFs.AdjustFileName(FileNameOnly(srcObj, srcEntry));
                int dataPerc = (100 * doneCount) / entryCount;
                int rsrcPerc = (int)((100 * (doneCount + 0.5)) / entryCount);
                CompressionFormat fmt =
                    DoCompress ? CompressionFormat.Default : CompressionFormat.Uncompressed;
                FileAttribs srcAttrs = new FileAttribs(srcEntry);

                Stream? dataStream = null;
                Stream? rsrcStream = null;
                AppleSingle? adfArchive = null;
                Stream? adfStream = null;
                try {
                    // Open streams for the data and resource forks.
                    if (srcObj is IArchive) {
                        IArchive srcArchive = (IArchive)srcObj;
                        if (srcEntry.HasDataFork) {
                            dataStream = srcArchive.OpenPart(srcEntry, FilePart.DataFork);
                        }

                        // Handle paired MacZip entry.  Need to extract attributes and check
                        // for a resource fork.
                        if (adfEntry != IFileEntry.NO_ENTRY) {
                            Debug.Assert(srcArchive is Zip);
                            try {
                                // Copy to temp file; can't use unseekable stream as archive source.
                                adfStream =
                                    ArcTemp.ExtractToTemp(srcArchive, adfEntry, FilePart.DataFork);
                                adfArchive = AppleSingle.OpenArchive(adfStream, mAppHook);
                                IFileEntry adfArchiveEntry = adfArchive.GetFirstEntry();
                                srcAttrs.GetFromAppleSingle(adfArchiveEntry);
                                if (adfArchiveEntry.HasRsrcFork && adfArchiveEntry.RsrcLength > 0) {
                                    rsrcStream =
                                        adfArchive.OpenPart(adfArchiveEntry, FilePart.RsrcFork);
                                }
                            } catch (Exception ex) {
                                // Never mind.
                                ReportFailure("Unable to get ADF attrs for '" +
                                    srcEntry.FullPathName + "': " + ex.Message);
                                // keep going
                                Debug.Assert(rsrcStream == null);
                            }
                        }

                        // If we didn't get a resource fork from ADF, see if the entry has one.
                        // When extracting from NuFX, extract the resource fork regardless of
                        // length, to preserve the fact that the file was extended (it probably
                        // came from ProDOS).
                        if (rsrcStream == null) {
                            if (srcEntry.HasRsrcFork &&
                                    (srcEntry is NuFX_FileEntry || srcEntry.RsrcLength > 0)) {
                                rsrcStream = srcArchive.OpenPart(srcEntry, FilePart.RsrcFork);
                            }
                        }
                    } else {
                        IFileSystem srcFs = (IFileSystem)srcObj;
                        if (srcEntry.HasDataFork) {
                            dataStream = srcFs.OpenFile(srcEntry, FileAccessMode.ReadOnly,
                                doRawXfer ? FilePart.RawData : FilePart.DataFork);
                        }
                        // Keep zero-length ProDOS/NuFX resource forks to preserve "extended"
                        // storage type, otherwise ignore them.
                        if (srcEntry.HasRsrcFork && (srcEntry is ProDOS_FileEntry ||
                                srcEntry is NuFX_FileEntry || srcEntry.RsrcLength > 0)) {
                            rsrcStream = srcFs.OpenFile(srcEntry, FileAccessMode.ReadOnly,
                                FilePart.RsrcFork);
                        }
                    }

                    // If we have resource fork data but can't store it, report and discard.
                    if (rsrcStream != null && !canRsrcFork) {
                        CallbackFacts facts =
                            new CallbackFacts(CallbackFacts.Reasons.ResourceForkIgnored,
                                srcEntry.FullPathName, srcEntry.DirectorySeparatorChar);
                        facts.Part = FilePart.RsrcFork;
                        mFunc(facts, null);
                        rsrcStream.Dispose();
                        rsrcStream = null;
                    }

                    // Add the new file to subDirEnt.  See if it already exists.
                    if (dstFs.TryFindFileEntry(subDirEnt, adjFileName, out IFileEntry existEntry)) {
                        // File exists.  Skip or overwrite.
                        bool doSkip = false;
                        CallbackFacts facts = new CallbackFacts(
                            CallbackFacts.Reasons.FileNameExists,
                            existEntry.FullPathName, existEntry.DirectorySeparatorChar);
                        CallbackFacts.Results result = mFunc(facts, null);
                        switch (result) {
                            case CallbackFacts.Results.Cancel:
                                return false;
                            case CallbackFacts.Results.Skip:
                                doSkip = true;
                                break;
                            case CallbackFacts.Results.Overwrite:
                                break;
                            default:
                                Debug.Assert(false);
                                break;
                        }
                        if (doSkip) {
                            doneCount++;
                            continue;
                        }

                        if (existEntry.IsDubious || existEntry.IsDamaged) {
                            ReportFailure("Error: cannot overwrite damaged file: " +
                                existEntry.FullPathName);
                            return false;
                        }

                        // Delete existing entry, recreate it below.
                        try {
                            dstFs.DeleteFile(existEntry);
                        } catch (IOException ex) {
                            ReportFailure("Error: unable to delete existing file '" +
                                existEntry.FullPathName + "': " + ex.Message);
                            return false;
                        }
                    }

                    IFileEntry newEntry;
                    try {
                        // Create file and set file type now, so DOS "cooked" mode works.
                        CreateMode mode =
                            (rsrcStream == null) ? CreateMode.File : CreateMode.Extended;
                        newEntry =
                            dstFs.CreateFile(subDirEnt, adjFileName, mode, srcAttrs.FileType);
                    } catch (IOException ex) {
                        ReportFailure("Error: unable to create file '" + adjFileName +
                            "': " + ex.Message);
                        return false;
                    }

                    // Copy the fork data.
                    try {
                        // Enable the DOS text conversion if:
                        // (1) DOS text conversion is enabled
                        // (2) We're copying to or from a file of type TXT
                        // (3) The source or destination is DOS
                        // (4) The source and destination are not both DOS.
                        CallbackFacts.DOSConvMode dosConvMode = CallbackFacts.DOSConvMode.None;
                        bool isTextCopy = srcEntry.FileType == FileAttribs.FILE_TYPE_TXT ||
                                          newEntry.FileType == FileAttribs.FILE_TYPE_TXT;
                        if (ConvertDOSText && isTextCopy &&
                                (srcObj is DOS || dstFs is DOS) &&
                                !(srcObj is DOS && dstFs is DOS)) {
                            if (srcObj is DOS) {
                                dosConvMode = CallbackFacts.DOSConvMode.FromDOS;
                            } else {
                                dosConvMode = CallbackFacts.DOSConvMode.ToDOS;
                            }
                        }

                        if (dataStream != null) {
                            using (DiskFileStream outStream = dstFs.OpenFile(newEntry,
                                    FileAccessMode.ReadWrite,
                                    doRawXfer ? FilePart.RawData : FilePart.DataFork)) {
                                CopyStreamToDisk(dataStream, outStream, srcEntry, newEntry,
                                    FilePart.DataFork, dataPerc, dosConvMode, doRawXfer);
                            }
                        }
                        if (rsrcStream != null) {
                            using (DiskFileStream outStream = dstFs.OpenFile(newEntry,
                                    FileAccessMode.ReadWrite, FilePart.RsrcFork)) {
                                CopyStreamToDisk(rsrcStream, outStream, srcEntry, newEntry,
                                    FilePart.RsrcFork, rsrcPerc, CallbackFacts.DOSConvMode.None,
                                    doRawXfer);
                            }
                        }
                    } catch (IOException ex) {
                        // Copy failed, clean up.
                        ReportFailure(ex.Message);
                        dstFs.DeleteFile(newEntry);
                        return false;
                    }

                    // Set types, dates, and access flags.
                    CopyAttributes(srcAttrs, newEntry);
                    newEntry.SaveChanges();
                } finally {
                    dataStream?.Dispose();
                    rsrcStream?.Dispose();
                    adfArchive?.Dispose();
                    adfStream?.Dispose();
                }

                doneCount++;
            }

            return true;
        }

        /// <summary>
        /// Ensures that all of the directories in the path exist.  If they don't exist, they
        /// will be created.
        /// </summary>
        /// <param name="dstFs">Filesystem to modify.</param>
        /// <param name="targetDirEnt">Base directory.</param>
        /// <param name="srcObj">Source (IArchive or IFileSystem)</param>
        /// <param name="srcBase">Base directory in source (disk only).</param>
        /// <param name="srcEntry">File entry we're trying to copy.</param>
        /// <returns>File entry for destination directory.</returns>
        /// <exception cref="IOException">Something failed.</exception>
        private static IFileEntry CreateSubdirectories(IFileSystem dstFs, IFileEntry targetDirEnt,
                object srcObj, IFileEntry srcBase, IFileEntry srcEntry) {
            string[] parts;
            if (srcObj is IArchive) {
                // Split the pathname into parts.
                if (srcEntry.DirectorySeparatorChar == IFileEntry.NO_DIR_SEP) {
                    parts = new string[] { srcEntry.FileName };
                } else {
                    parts = srcEntry.FullPathName.Split(srcEntry.DirectorySeparatorChar,
                        StringSplitOptions.RemoveEmptyEntries);
                }
            } else {
                // Generate list from hierarchy.
                List<string> partList = new List<string>(8);
                IFileEntry aboveRootEntry;
                if (srcBase == IFileEntry.NO_ENTRY) {
                    aboveRootEntry = IFileEntry.NO_ENTRY;       // same as start==volDir;
                } else {
                    aboveRootEntry = srcBase.ContainingDir;
                }
                GenerateNameList(aboveRootEntry, srcEntry, partList);
                parts = partList.ToArray();
            }
            if (parts.Length == 0) {
                // Shouldn't be possible.
                throw new IOException("Couldn't find path in '" + srcEntry.FullPathName);
            }

            // Create all necessary subdirectories, adjusting the names as we go.
            // We have a list of names that includes the filename, so stop before the end.
            IFileEntry subDirEnt = targetDirEnt;
            for (int i = 0; i < parts.Length - 1; i++) {
                string dirName = parts[i];
                string adjDirName = dstFs.AdjustFileName(parts[i]);
                if (dstFs.TryFindFileEntry(subDirEnt, adjDirName, out IFileEntry nextDirEnt)) {
                    // Exists; is it a directory?
                    if (!nextDirEnt.IsDirectory) {
                        throw new IOException("Error: path component '" + adjDirName +
                            "' (" + dirName + ") is not a directory");
                    }
                    subDirEnt = nextDirEnt;
                } else {
                    // Not found, create new.
                    try {
                        subDirEnt = dstFs.CreateFile(subDirEnt, adjDirName, CreateMode.Directory);
                    } catch (IOException ex) {
                        throw new IOException("Error: unable to create directory '" +
                            adjDirName + "': " + ex.Message);
                    }
                }
            }
            return subDirEnt;
        }

        /// <summary>
        /// Recursively generates a list of pathname components from a filesystem entry.
        /// </summary>
        /// <param name="srcEntry">File entry.</param>
        /// <param name="partList">List to add parts to.</param>
        private static void GenerateNameList(IFileEntry aboveRootEntry, IFileEntry srcEntry,
                List<string> partList) {
            if (srcEntry.ContainingDir == aboveRootEntry) {
                // We've hit the top directory.  Don't add a path component for it.
                return;
            }
            GenerateNameList(aboveRootEntry, srcEntry.ContainingDir, partList);
            partList.Add(srcEntry.FileName);
        }

        /// <summary>
        /// Extracts the filename from the file entry.  For a file on disk this is trivial, for
        /// an archive we need to split the path.
        /// </summary>
        /// <param name="srcObj">Source (IArchive or IFileSystem).</param>
        /// <param name="srcEntry">File entry.</param>
        /// <returns>Filename string.</returns>
        private static string FileNameOnly(object srcObj, IFileEntry srcEntry) {
            if (srcObj is IFileSystem || srcEntry.DirectorySeparatorChar == IFileEntry.NO_DIR_SEP) {
                // Sometimes a filename is just a filename.
                return srcEntry.FileName;
            }
            Debug.Assert(srcObj is IArchive);
            string[] names = srcEntry.FileName.Split(srcEntry.DirectorySeparatorChar,
                StringSplitOptions.RemoveEmptyEntries);
            Debug.Assert(names.Length > 0);
            return names[names.Length - 1];     // return the filename part
        }

        /// <summary>
        /// Copies a stream from a file archive or disk image to a disk image.  Special behavior
        /// is enabled for disk-to-disk copies.
        /// </summary>
        private void CopyStreamToDisk(Stream srcStream, DiskFileStream dstStream,
                IFileEntry srcEntry, IFileEntry newEntry, FilePart part, int progressPercent,
                CallbackFacts.DOSConvMode dosConvMode, bool doSparseXfer) {
            CallbackFacts facts = new CallbackFacts(CallbackFacts.Reasons.Progress,
                srcEntry.FullPathName, srcEntry.DirectorySeparatorChar);
            facts.NewPathName = newEntry.FullPathName;
            facts.NewDirSep = newEntry.DirectorySeparatorChar;
            facts.DOSConv = dosConvMode;
            facts.ProgressPercent = progressPercent;
            facts.Part = part;
            mFunc(facts, null);

            if (doSparseXfer) {
                // Seek around holes in the file.  This is required to preserve the sparse
                // structure in DOS-to-DOS transfers.  It's not needed for ProDOS, and could
                // actually cause problems in ProDOS-to-DOS transfers because only DOS text
                // files are expected to be sparse.
                long dataStart = srcStream.Seek(0, SEEK_ORIGIN_DATA);
                while (dataStart < srcStream.Length) {
                    // Find the end of the next data part.
                    long dataEnd = srcStream.Seek(dataStart, SEEK_ORIGIN_HOLE);
                    Debug.Assert(dataEnd > dataStart);
                    // Copy this section.
                    srcStream.Position = dataStart;
                    dstStream.Position = dataStart;
                    CopySection((DiskFileStream)srcStream, dstStream, dataEnd - dataStart,
                        dosConvMode);
                    // Advance to the next section.
                    dataStart = srcStream.Seek(dataEnd, SEEK_ORIGIN_DATA);
                }
            } else {
                CopySection(srcStream, dstStream, -1, dosConvMode);
            }
        }

        /// <summary>
        /// Copies a section of a file.  Useful for copying from disk images with sparse
        /// sections.  Source and destination must be seeked before calling (usually to the
        /// same file offset).
        /// </summary>
        /// <param name="srcStream">Source stream.  May be disk or archive.</param>
        /// <param name="dstStream">Destination stream.</param>
        /// <param name="count">Number of bytes to copy.  Pass -1 for an unseekable stream
        ///   from an archive.</param>
        /// <param name="dosConvMode">DOS text conversion mode.</param>
        private void CopySection(Stream srcStream, DiskFileStream dstStream, long count,
                CallbackFacts.DOSConvMode dosConvMode) {
            if (mCopyBuf == null) {
                mCopyBuf = new byte[32768];
            }
            while (count != 0) {
                int readLen;
                if (count < 0) {
                    // Source is an archive, no length available; copy entire stream.
                    readLen = srcStream.Read(mCopyBuf, 0, mCopyBuf.Length);
                    if (readLen == 0) {
                        return;     // EOF reached
                    }
                } else {
                    readLen = (count > mCopyBuf.Length) ? mCopyBuf.Length : (int)count;
                    srcStream.ReadExactly(mCopyBuf, 0, readLen);
                }
                switch (dosConvMode) {
                    case CallbackFacts.DOSConvMode.FromDOS:
                        for (int i = 0; i < readLen; i++) {
                            mCopyBuf[i] &= 0x7f;            // clear high bit on all values
                        }
                        break;
                    case CallbackFacts.DOSConvMode.ToDOS:
                        for (int i = 0; i < readLen; i++) {
                            if (mCopyBuf[i] != 0x00) {      // set high bit on everything but NULs
                                mCopyBuf[i] |= 0x80;
                            }
                        }
                        break;
                    default:
                        break;
                }
                dstStream.Write(mCopyBuf, 0, readLen);
                if (count > 0) {
                    count -= readLen;
                }
            }
        }

        #endregion To IFileSystem

        private void ReportFailure(string msg) {
            CallbackFacts facts = new CallbackFacts(CallbackFacts.Reasons.Failure);
            facts.FailMessage = msg;
            mFunc(facts, null);
        }

        /// <summary>
        /// Copies attributes between entries.  Does not set FileName or FileNameSeparator (which
        /// must be adjusted for target).
        /// </summary>
        /// <param name="srcEntry">Source file entry.</param>
        /// <param name="dstEntry">Destination file entry.</param>
        private void CopyAttributes(IFileEntry srcEntry, IFileEntry dstEntry) {
            // Copy the entry into a FileAttribs so we can share the code.
            FileAttribs attrs = new FileAttribs(srcEntry);
            CopyAttributes(attrs, dstEntry);
        }

        /// <summary>
        /// Copies attributes from a FileAttribs structure.  Generates HFS types from ProDOS
        /// types and vice-versa when appropriate.  Does not set FileName or FileNameSeparator.
        /// </summary>
        /// <param name="srcAttrs">Source file attributes.</param>
        /// <param name="dstEntry">Destination file entry.</param>
        private void CopyAttributes(FileAttribs srcAttrs, IFileEntry dstEntry) {
            bool hasHfsTypesOnly = dstEntry.HasHFSTypes && !dstEntry.HasProDOSTypes;

            if (hasHfsTypesOnly && srcAttrs.HFSFileType == 0 && srcAttrs.HFSCreator == 0 &&
                    (srcAttrs.FileType != 0 || srcAttrs.AuxType != 0)) {
                // We're adding files to an HFS disk (or other format that only has HFS types),
                // but there's no HFS file type information.  We do have ProDOS file type info,
                // so encode it in the HFS type fields.
                FileAttribs.ProDOSToHFS(srcAttrs.FileType, srcAttrs.AuxType, out uint hfsFileType,
                    out uint hfsCreator);
                dstEntry.HFSFileType = hfsFileType;
                dstEntry.HFSCreator = hfsCreator;
            } else {
                dstEntry.HFSFileType = srcAttrs.HFSFileType;
                dstEntry.HFSCreator = srcAttrs.HFSCreator;
            }

            if (srcAttrs.FileType == 0 && srcAttrs.AuxType == 0) {
                // Look for a ProDOS type in the HFS type.  This could be an actual encoding
                // (creator='pdos') or just a handy conversion (any 'TEXT' becomes TXT).
                FileAttribs.ProDOSFromHFS(dstEntry.HFSFileType, dstEntry.HFSCreator,
                    out byte proType, out ushort proAux);
                dstEntry.FileType = proType;
                dstEntry.AuxType = proAux;
            } else {
                // Nonzero values found, keep them.
                dstEntry.FileType = srcAttrs.FileType;
                dstEntry.AuxType = srcAttrs.AuxType;
            }

            // Some filesystems will default new file dates to the current date/time.  We want to
            // copy NO_DATE when copying files from e.g. DOS volumes.
            if (srcAttrs.CreateWhen != TimeStamp.INVALID_DATE) {
                dstEntry.CreateWhen = srcAttrs.CreateWhen;
            }
            if (srcAttrs.ModWhen != TimeStamp.INVALID_DATE) {
                dstEntry.ModWhen = srcAttrs.ModWhen;
            }
            dstEntry.Access = srcAttrs.Access;
            //dstEntry.Comment = srcAttrs.Comment;
        }
    }
}
