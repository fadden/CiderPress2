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
using static DiskArc.Defs;
using static DiskArc.IFileSystem;

namespace AppCommon {
    /// <summary>
    /// <para>Adds a collection of files to an IArchive or IFileSystem object, where the files are
    /// provided as a pairing of metadata and open-on-demand read-only streams.  Uses callbacks
    /// to display progress and warning messages, and to query for handling of conflicts.</para>
    /// <para>This fills the same role as <see cref="AddFileWorker"/>, but for a platform-specific
    /// clipboard paste function.</para>
    /// </summary>
    public class ClipPasteWorker {
        /// <summary>
        /// Callback function interface definition.
        /// </summary>
        public delegate CallbackFacts.Results CallbackFunc(CallbackFacts what);

        /// <summary>
        /// Input stream generator function interface definition.  This is invoked on the
        /// receiving side to receive input from the sender.
        /// </summary>
        /// <param name="clipEntry">Entry to generate a stream for.</param>
        /// <returns>Read-only, non-seekable output stream, or null if no stream is
        ///   available for the specified entry.</returns>
        public delegate Stream? ClipStreamGenerator(ClipFileEntry clipEntry);

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
        /// If set, convert text files when transferring them to or from DOS filesystems.
        /// </summary>
        public bool ConvertDOSText { get; set; } = true;

        /// <summary>
        /// If set, strip pathnames off of files before adding them.  For a filesystem, all
        /// files will be added to the target directory.
        /// </summary>
        public bool StripPaths { get; set; } = false;

        /// <summary>
        /// If set, use raw mode when adding files to filesystems (notably DOS 3.x).
        /// </summary>
        public bool RawMode { get; set; } = false;

        /// <summary>
        /// Callback function, for progress updates, warnings, and problem resolution.
        /// </summary>
        private CallbackFunc mFunc;

        /// <summary>
        /// Used to determine if we're copying files onto themselves (files can only be open in
        /// one process at a time, so if the PID matches then we can use the object hashes).
        /// </summary>
        private bool mIsSameProcess;

        private List<ClipFileEntry> mClipEntries;

        private ClipStreamGenerator mClipStreamGen;

        private byte[]? mCopyBuf = null;

        /// <summary>
        /// Application hook reference.
        /// </summary>
        private AppHook mAppHook;


        public ClipPasteWorker(List<ClipFileEntry> clipEntries, ClipStreamGenerator clipStreamGen,
                CallbackFunc func, bool doCompress, bool macZip, bool convDosText, bool stripPaths,
                bool rawMode, bool isSameProcess, AppHook appHook) {
            mClipEntries = clipEntries;
            mClipStreamGen = clipStreamGen;
            mFunc = func;
            DoCompress = doCompress;
            EnableMacOSZip = macZip;
            ConvertDOSText = convDosText;
            StripPaths = stripPaths;
            RawMode = rawMode;
            mIsSameProcess = isSameProcess;
            mAppHook = appHook;
        }

        public void AddFilesToArchive(IArchive archive, out bool isCancelled) {
            Debug.Assert(!archive.Characteristics.HasSingleEntry);
            bool canRsrcFork = archive.Characteristics.HasResourceForks ||
                (archive is Zip && EnableMacOSZip);
            bool doStripPaths = StripPaths ||
                archive.Characteristics.DefaultDirSep == IFileEntry.NO_DIR_SEP;

            if (archive.IsDubious) {
                throw new Exception("target archive is read-only (damage)");
            }

            // Generate a list of archive contents, used when checking for duplicate entries.  We
            // want to do case-insensitive comparisons for general consistency.
            Dictionary<string, IFileEntry> dupCheck =
                new Dictionary<string, IFileEntry>(StringComparer.InvariantCultureIgnoreCase);
            foreach (IFileEntry entry in archive) {
                // Archive might already have duplicates, so don't call Add().
                dupCheck[entry.FileName] = entry;
            }

            for (int idx = 0; idx < mClipEntries.Count; idx++) {
                ClipFileEntry clipEntry = mClipEntries[idx];
                if (clipEntry.Attribs.IsDirectory) {
                    // We don't add explicit directory entries to archives.
                    continue;
                }

                // Find the parts for this entry.  If the entry has both data and resource forks,
                // the data fork will come first, and the resource fork will be in the following
                // entry and have an identical filename.  (We could make this absolutely
                // unequivocal by adding a file serial number on the source side.)
                ClipFileEntry? dataPart = null;
                ClipFileEntry? rsrcPart = null;
                if (clipEntry.Part == FilePart.DataFork || clipEntry.Part == FilePart.RawData ||
                        clipEntry.Part == FilePart.DiskImage) {
                    dataPart = clipEntry;
                } else if (clipEntry.Part == FilePart.RsrcFork) {
                    rsrcPart = clipEntry;
                }
                int dataIdx = idx;      // used for progress counter
                if (rsrcPart == null && idx < mClipEntries.Count - 1) {
                    ClipFileEntry checkEntry = mClipEntries[idx + 1];
                    if (checkEntry.Part == FilePart.RsrcFork &&
                            checkEntry.Attribs.FullPathName == clipEntry.Attribs.FullPathName) {
                        rsrcPart = checkEntry;
                        idx++;
                    }
                }
                if (dataPart == null && rsrcPart == null) {
                    Debug.Assert(false, "no valid parts?");
                    continue;
                }

                if (dataPart == null && !canRsrcFork) {
                    // Nothing but a resource fork, and we can't store those.  Complain and move on.
                    Debug.Assert(rsrcPart == null);
                    CallbackFacts facts = new CallbackFacts(
                        CallbackFacts.Reasons.ResourceForkIgnored,
                        clipEntry.Attribs.FullPathName, Path.DirectorySeparatorChar);
                    facts.Part = FilePart.RsrcFork;
                    mFunc(facts);
                    continue;
                }

                // Adjust the storage name to what the archive can handle.
                string storageDir;
                if (doStripPaths) {
                    storageDir = string.Empty;
                } else {
                    storageDir = PathName.GetDirectoryName(clipEntry.Attribs.FullPathName,
                        clipEntry.Attribs.FullPathSep);
                }
                string storageName = PathName.GetFileName(clipEntry.Attribs.FullPathName,
                        clipEntry.Attribs.FullPathSep);
                string? adjPath = AddFileWorker.AdjustArchivePath(archive, storageDir,
                    clipEntry.Attribs.FullPathSep, storageName);
                if (adjPath == null) {
                    // Unable to adjust; assume total path is too long.
                    CallbackFacts facts = new CallbackFacts(CallbackFacts.Reasons.PathTooLong,
                        storageDir + Path.DirectorySeparatorChar + storageName,
                        Path.DirectorySeparatorChar);
                    CallbackFacts.Results result = mFunc(facts);
                    if (result == CallbackFacts.Results.Skip) {
                        continue;
                    } else {
                        isCancelled = true;
                        return;
                    }
                }

                // Check for a duplicate.  If it exists, ask the user if they want to overwrite
                // or skip.
                if (dupCheck.TryGetValue(adjPath, out IFileEntry? dupEntry)) {
                    CallbackFacts facts = new CallbackFacts(CallbackFacts.Reasons.FileNameExists);
                    facts.OrigPathName = dupEntry.FullPathName;
                    facts.OrigDirSep = dupEntry.DirectorySeparatorChar;
                    facts.OrigModWhen = dupEntry.ModWhen;
                    facts.NewPathName = clipEntry.Attribs.FullPathName;
                    facts.NewDirSep = clipEntry.Attribs.FullPathSep;
                    facts.NewModWhen = clipEntry.Attribs.ModWhen;
                    CallbackFacts.Results result = mFunc(facts);
                    switch (result) {
                        case CallbackFacts.Results.Cancel:
                            isCancelled = true;
                            return;
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
                    archive.DeleteRecord(dupEntry);
                }

                // Create new record.
                IFileEntry newEntry = archive.CreateRecord();
                clipEntry.Attribs.FullPathName = adjPath;
                clipEntry.Attribs.FullPathSep = archive.Characteristics.DefaultDirSep;
                clipEntry.Attribs.CopyAttrsTo(newEntry, false);

                CompressionFormat fmt =
                    DoCompress ? CompressionFormat.Default : CompressionFormat.Uncompressed;
                int progressPerc = (100 * dataIdx) / mClipEntries.Count;
                if (dataPart != null) {
                    // TODO? handle DOS text conversions
                    ClipFileSource clSource = new ClipFileSource(adjPath,
                        archive.Characteristics.DefaultDirSep, dataPart, dataPart.Part,
                        mFunc, progressPerc, mClipStreamGen, mAppHook);
                    archive.AddPart(newEntry, dataPart.Part, clSource, fmt);
                }

                progressPerc = (100 * idx) / mClipEntries.Count;
                if (archive is Zip && EnableMacOSZip) {
                    if (dataPart == null) {
                        // We want to keep the data fork part, since AppleDouble requires both.
                        // We didn't give it a data part earlier, so do it now.
                        SimplePartSource zeroSource =
                            new SimplePartSource(new MemoryStream(new byte[0]));
                        archive.AddPart(newEntry, FilePart.DataFork, zeroSource, fmt);
                    }
                    // Create the "header" file if there's a resource fork, or if we just need
                    // to preserve the file type info.
                    if (rsrcPart != null) {
                        AddMacZipRecord(archive, rsrcPart, adjPath, progressPerc, fmt);
                    } else if (clipEntry.Attribs.HasTypeInfo) {
                        AddMacZipRecord(archive, clipEntry, adjPath, progressPerc, fmt);
                    }
                } else if (rsrcPart != null) {
                    if (!canRsrcFork) {
                        // Nowhere to put this resource fork.  Report it and move on.
                        Debug.Assert(dataPart != null);
                        CallbackFacts facts = new CallbackFacts(
                            CallbackFacts.Reasons.ResourceForkIgnored,
                            clipEntry.Attribs.FullPathName,
                            clipEntry.Attribs.FullPathSep);
                        facts.Part = FilePart.RsrcFork;
                        mFunc(facts);
                    } else {
                        // Add resource fork to record created earlier.
                        ClipFileSource clSource = new ClipFileSource(adjPath,
                            archive.Characteristics.DefaultDirSep, rsrcPart, rsrcPart.Part,
                            mFunc, progressPerc, mClipStreamGen, mAppHook);
                        archive.AddPart(newEntry, rsrcPart.Part, clSource, fmt);
                    }
                }
            }
            isCancelled = false;
        }

        /// <summary>
        /// Adds a special MacOSX ZIP record that will hold file type data and/or resource fork.
        /// </summary>
        /// <param name="archive">ZIP archive we're working on.</param>
        /// <param name="clipEntry">Clip entry for the resource fork if it's available, otherwise
        ///   for the data fork which has nonzero type info.</param>
        private void AddMacZipRecord(IArchive archive, ClipFileEntry clipEntry,
                string adjPath, int progressPercent, CompressionFormat fmt) {
            Debug.Assert(archive is Zip);
            Debug.Assert(clipEntry.Part == FilePart.RsrcFork || clipEntry.Attribs.HasTypeInfo);

            // Create a new record.
            IFileEntry rsrcEntry = archive.CreateRecord();

            // Form __MACOSX/path/._filename string.
            StringBuilder sb = new StringBuilder();
            sb.Append(Zip.MAC_ZIP_RSRC_PREFIX);
            sb.Append(archive.Characteristics.DefaultDirSep);
            int fileNameSplit =
                adjPath.LastIndexOf(archive.Characteristics.DefaultDirSep);
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
            rsrcEntry.DirectorySeparatorChar = archive.Characteristics.DefaultDirSep;
            rsrcEntry.ModWhen = clipEntry.Attribs.ModWhen;        // match the modification date

            // Add the part.  This will be an AppleDouble "header" file that has the file
            // type info and (if present) the resource fork.  This will be generated on the fly
            // so we don't have to read the input file up front and hold onto it while we work.
            ClipFileSourceMZ clSource = new ClipFileSourceMZ(adjPath,
                archive.Characteristics.DefaultDirSep, clipEntry, mFunc, progressPercent,
                mClipStreamGen, mAppHook);
            archive.AddPart(rsrcEntry, FilePart.DataFork, clSource, fmt);
        }

        /// <summary>
        /// Adds files to a filesystem.
        /// </summary>
        /// <param name="fileSystem">Filesystem to add files to.</param>
        /// <param name="targetDir">Base directory where files are added, or NO_ENTRY if none
        ///   was specified.</param>
        /// <param name="isCancelled">Result: true if operation was cancelled.</param>
        /// <exception cref="IOException">File I/O error occurred.</exception>
        public void AddFilesToDisk(IFileSystem fileSystem, IFileEntry targetDir,
                out bool isCancelled) {
            if (targetDir != IFileEntry.NO_ENTRY) {
                Debug.Assert(targetDir.IsDirectory);
                Debug.Assert(targetDir.GetFileSystem() == fileSystem);
            }
            if (fileSystem.IsReadOnly) {
                // Should have been caught be caller.
                throw new Exception("target filesystem is read-only" +
                    (fileSystem.IsDubious ? " (damage)" : ""));
            }

            bool canRsrcFork = fileSystem.Characteristics.HasResourceForks;
            bool doStripPaths = StripPaths || !fileSystem.Characteristics.IsHierarchical;
            bool useRawMode = RawMode;

            IFileEntry targetDirEnt = (targetDir == IFileEntry.NO_ENTRY) ?
                fileSystem.GetVolDirEntry() : targetDir;

            for (int idx = 0; idx < mClipEntries.Count; idx++) {
                // Find the parts for this entry.  If the entry has both data and resource forks,
                // the data fork will come first, and the resource fork will be in the following
                // entry and have an identical filename.  (We could make this absolutely
                // unequivocal by adding a file serial number on the source side.)
                ClipFileEntry clipEntry = mClipEntries[idx];
                ClipFileEntry? dataPart = null;
                ClipFileEntry? rsrcPart = null;
                if (clipEntry.Part == FilePart.DataFork || clipEntry.Part == FilePart.RawData ||
                        clipEntry.Part == FilePart.DiskImage) {
                    dataPart = clipEntry;
                } else if (clipEntry.Part == FilePart.RsrcFork) {
                    rsrcPart = clipEntry;
                }
                int dataIdx = idx;      // used for progress counter
                if (rsrcPart == null && idx < mClipEntries.Count - 1) {
                    ClipFileEntry checkEntry = mClipEntries[idx + 1];
                    if (checkEntry.Part == FilePart.RsrcFork &&
                            checkEntry.Attribs.FullPathName == clipEntry.Attribs.FullPathName) {
                        rsrcPart = checkEntry;
                        idx++;
                    }
                }
                if (dataPart == null && rsrcPart == null) {
                    Debug.Assert(false, "no valid parts?");
                    continue;
                }

                if (doStripPaths && clipEntry.Attribs.IsDirectory) {
                    Debug.Assert(rsrcPart == null);
                    continue;
                }

                if (dataPart == null && !canRsrcFork) {
                    // Nothing but a resource fork, and we can't store those.  Complain and move on.
                    Debug.Assert(rsrcPart == null);
                    CallbackFacts facts = new CallbackFacts(
                        CallbackFacts.Reasons.ResourceForkIgnored,
                        clipEntry.Attribs.FullPathName, Path.DirectorySeparatorChar);
                    facts.Part = FilePart.RsrcFork;
                    mFunc(facts);
                    continue;
                }

                // Find the destination directory for this file, creating directories as
                // needed.
                string storageDir;
                if (doStripPaths) {
                    storageDir = string.Empty;
                } else {
                    storageDir = PathName.GetDirectoryName(clipEntry.Attribs.FullPathName,
                        clipEntry.Attribs.FullPathSep);
                }
                string storageName = PathName.GetFileName(clipEntry.Attribs.FullPathName,
                        clipEntry.Attribs.FullPathSep);
                IFileEntry subDirEnt;
                subDirEnt = fileSystem.CreateSubdirectories(targetDirEnt, storageDir,
                    clipEntry.Attribs.FullPathSep);

                // Add the new file to subDirEnt.  See if it already exists.
                string adjName = fileSystem.AdjustFileName(storageName);
                if (fileSystem.TryFindFileEntry(subDirEnt, adjName, out IFileEntry newEntry)) {
                    if (mIsSameProcess && newEntry.GetHashCode() == clipEntry.EntryHashCode) {
                        throw new Exception("Cannot ovewrite entry with itself");
                    }
                    if (clipEntry.Attribs.IsDirectory && !newEntry.IsDirectory) {
                        throw new Exception("Cannot replace non-directory '" + newEntry.FileName +
                            "' with directory");
                    } else if (!clipEntry.Attribs.IsDirectory && newEntry.IsDirectory) {
                        throw new Exception("Cannot replace directory '" + newEntry.FileName +
                            "' with non-directory");
                    } else if (!clipEntry.Attribs.IsDirectory && !newEntry.IsDirectory) {
                        // File exists.  Skip or overwrite.
                        bool doSkip = false;
                        CallbackFacts facts =
                            new CallbackFacts(CallbackFacts.Reasons.FileNameExists);
                        facts.OrigPathName = newEntry.FullPathName;
                        facts.OrigDirSep = newEntry.DirectorySeparatorChar;
                        facts.OrigModWhen = newEntry.ModWhen;
                        facts.NewPathName = clipEntry.Attribs.FullPathName;
                        facts.NewDirSep = clipEntry.Attribs.FullPathSep;
                        facts.NewModWhen = clipEntry.Attribs.ModWhen;

                        CallbackFacts.Results result = mFunc(facts);
                        switch (result) {
                            case CallbackFacts.Results.Cancel:
                                isCancelled = true;
                                return;
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
                            continue;
                        }

                        if (newEntry.IsDubious || newEntry.IsDamaged) {
                            throw new Exception("cannot overwrite damaged file: " +
                                newEntry.FullPathName);
                        }

                        // We can either delete the existing entry and create a new one, or merge
                        // with the current contents, which might be helpful if the file on disk is
                        // extended and we're only adding one fork.  The merge would retain the
                        // current file attributes, so that adding a data fork wouldn't change the
                        // file type.  (I think somebody asked about this once for CiderPress, but
                        // I never did anything about it.  I can't find the request.)
                        //
                        // For now, just delete and recreate the entry.
                        fileSystem.DeleteFile(newEntry);
                        newEntry = IFileEntry.NO_ENTRY;
                    } else {
                        // Adding a directory that already exists.
                        Debug.Assert(clipEntry.Attribs.IsDirectory && newEntry.IsDirectory);
                    }
                }

                if (newEntry == IFileEntry.NO_ENTRY) {
                    CreateMode mode = CreateMode.File;
                    if (rsrcPart != null && canRsrcFork) {
                        mode = CreateMode.Extended;
                    } else if (clipEntry.Attribs.IsDirectory) {
                        mode = CreateMode.Directory;
                    }
                    // Create file and set file type, so DOS "cooked" mode works.
                    newEntry = fileSystem.CreateFile(subDirEnt, adjName, mode,
                        clipEntry.Attribs.FileType);
                }

                if (clipEntry.Attribs.IsDirectory) {
                    // Directory already exists or we just created it.  Nothing else to do.
                    continue;
                }

                // Add data fork.
                if (dataPart != null) {
                    int progressPerc = (100 * dataIdx) / mClipEntries.Count;
                    try {
                        // Make sure we use the FilePart from the ClipFileEntry, as that selects
                        // "raw data" mode when needed.
                        using (DiskFileStream outStream = fileSystem.OpenFile(newEntry,
                                FileAccessMode.ReadWrite, dataPart.Part)) {
                            CopyFilePart(dataPart, progressPerc, newEntry.FullPathName,
                                newEntry.DirectorySeparatorChar,
                                GetDOSConvMode(dataPart, fileSystem is DiskArc.FS.DOS), outStream);
                        }
                    } catch {
                        // Copy or conversion failed, clean up.
                        fileSystem.DeleteFile(newEntry);
                        throw;
                    }
                }

                if (rsrcPart != null && canRsrcFork) {
                    int progressPerc = (100 * idx) / mClipEntries.Count;
                    try {
                        Debug.Assert(rsrcPart.Part == FilePart.RsrcFork);
                        using (DiskFileStream outStream = fileSystem.OpenFile(newEntry,
                                FileAccessMode.ReadWrite, rsrcPart.Part)) {
                            CopyFilePart(rsrcPart, progressPerc, newEntry.FullPathName,
                                newEntry.DirectorySeparatorChar,
                                CallbackFacts.DOSConvMode.None, outStream);
                        }
                    } catch {
                        // Copy failed, clean up.
                        fileSystem.DeleteFile(newEntry);
                        throw;
                    }
                }

                // Set types, dates, and access flags.
                clipEntry.Attribs.FileNameOnly = adjName;
                clipEntry.Attribs.CopyAttrsTo(newEntry, true);
                newEntry.SaveChanges();
            }

            isCancelled = false;
        }

        /// <summary>
        /// Calculates the DOS conversion mode.
        /// </summary>
        /// <param name="srcEntry">Entry for file source.</param>
        /// <param name="targetIsDOS">True if the destination is a DOS filesystem.</param>
        /// <returns>DOS text conversion mode.</returns>
        private CallbackFacts.DOSConvMode GetDOSConvMode(ClipFileEntry srcEntry, bool targetIsDOS) {
            if (!ConvertDOSText || srcEntry.Attribs.FileType != FileAttribs.FILE_TYPE_TXT) {
                // Not enabled, or not a text file.
                return CallbackFacts.DOSConvMode.None;
            }
            bool srcIsDOS = (srcEntry.FSType == FileSystemType.DOS32 ||
                srcEntry.FSType == FileSystemType.DOS33);
            if (srcIsDOS && !targetIsDOS) {
                return CallbackFacts.DOSConvMode.FromDOS;
            } else if (!srcIsDOS && targetIsDOS) {
                return CallbackFacts.DOSConvMode.ToDOS;
            } else {
                return CallbackFacts.DOSConvMode.None;
            }
        }

        /// <summary>
        /// Copies a part (i.e fork) of a file to a disk image file stream.
        /// </summary>
        /// <param name="clipEntry">Clip file entry for this file part.</param>
        /// <param name="progressPercent">Percent complete for progress update.</param>
        /// <param name="storageName">Name of file as it appears in the filesystem.</param>
        /// <param name="storageDirSep">Directory separator char for storageName.</param>
        /// <param name="outStream">Destination stream.</param>
        /// <exception cref="IOException">Error opening source stream.</exception>
        private void CopyFilePart(ClipFileEntry clipEntry, int progressPercent,
                string storageName, char storageDirSep, CallbackFacts.DOSConvMode dosConvMode,
                DiskFileStream outStream) {
            CallbackFacts facts = new CallbackFacts(CallbackFacts.Reasons.Progress);
            facts.OrigPathName = clipEntry.Attribs.FullPathName;
            facts.OrigDirSep = clipEntry.Attribs.FullPathSep;
            facts.NewPathName = storageName;
            facts.NewDirSep = storageDirSep;
            facts.ProgressPercent = progressPercent;
            facts.Part = clipEntry.Part;
            facts.DOSConv = dosConvMode;
            mFunc(facts);

            if (mCopyBuf == null) {
                mCopyBuf = new byte[32768];
            }

            using (Stream? inStream = mClipStreamGen(clipEntry)) {
                if (inStream == null) {
                    throw new IOException("Unable to open source stream.  " +
                        "Make sure source archive is open and files have not been deleted.");
                }
                while (true) {
                    int actual = inStream.Read(mCopyBuf, 0, mCopyBuf.Length);
                    if (actual == 0) {
                        break;      // EOF
                    }

                    switch (dosConvMode) {
                        case CallbackFacts.DOSConvMode.FromDOS:
                            for (int i = 0; i < actual; i++) {
                                mCopyBuf[i] &= 0x7f;        // clear high bit on all values
                            }
                            break;
                        case CallbackFacts.DOSConvMode.ToDOS:
                            for (int i = 0; i < actual; i++) {
                                if (mCopyBuf[i] != 0x00) {  // set high bit on everything but NULs
                                    mCopyBuf[i] |= 0x80;
                                }
                            }
                            break;
                        default:
                            break;
                    }

                    outStream.Write(mCopyBuf, 0, actual);
                }
            }
        }
    }
}
