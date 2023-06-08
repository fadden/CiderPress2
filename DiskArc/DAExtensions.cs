/*
 * Copyright 2022 faddenSoft
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

using CommonUtil;
using DiskArc.Arc;
using DiskArc.FS;

namespace DiskArc {
    /// <summary>
    /// Extensions to DiskArc library interfaces.  These are common activities that can be
    /// performed with public APIs.
    /// </summary>
    public static class DAExtensions {

        #region IArchive

        /// <summary>
        /// Finds an IFileEntry for the named file in the archive.
        /// </summary>
        /// <param name="archive">Archive object.</param>
        /// <param name="fileName">Name of file to search for.</param>
        /// <returns>The entry found.</returns>
        /// <exception cref="FileNotFoundException">File was not found.</exception>
        public static IFileEntry FindFileEntry(this IArchive archive, string fileName) {
            foreach (IFileEntry candidate in archive) {
                if (candidate.CompareFileName(fileName) == 0) {
                    return candidate;
                }
            }
            throw new FileNotFoundException("Unable to find '" + fileName + "'");
        }

        /// <summary>
        /// Finds an IFileEntry for the named file in the archive.
        /// </summary>
        /// <param name="archive">Archive object.</param>
        /// <param name="fileName">Name of file to search for.</param>
        /// <param name="fileNameSeparator">Filename separator character.</param>
        /// <returns>The entry found.</returns>
        /// <exception cref="FileNotFoundException">File was not found.</exception>
        public static IFileEntry FindFileEntry(this IArchive archive, string fileName,
                char fileNameSeparator) {
            foreach (IFileEntry candidate in archive) {
                if (candidate.CompareFileName(fileName, fileNameSeparator) == 0) {
                    return candidate;
                }
            }
            throw new FileNotFoundException("Unable to find '" + fileName + "'");
        }

        /// <summary>
        /// Tries to find an IFileEntry for the named file in the archive.
        /// </summary>
        /// <param name="archive">Archive object.</param>
        /// <param name="fileName">Name of file to search for.</param>
        /// <param name="entry">Entry found, or NO_ENTRY if nothing matched.</param>
        /// <returns>True if the entry was found.</returns>
        public static bool TryFindFileEntry(this IArchive archive, string fileName,
                out IFileEntry entry) {
            foreach (IFileEntry candidate in archive) {
                if (candidate.CompareFileName(fileName) == 0) {
                    entry = candidate;
                    return true;
                }
            }
            entry = IFileEntry.NO_ENTRY;
            return false;
        }

        /// <summary>
        /// Returns the first entry in the archive.  If the archive has no entries,
        /// IFileEntry.NO_ENTRY is returned.
        /// </summary>
        public static IFileEntry GetFirstEntry(this IArchive archive) {
            IEnumerator<IFileEntry> numer = archive.GetEnumerator();
            if (!numer.MoveNext()) {
                return IFileEntry.NO_ENTRY;
            }
            return numer.Current;
        }

        /// <summary>
        /// Adjusts a filename to be compatible with the archive.  This must always replace
        /// instances of the filename separator character, so that a filename isn't confused with
        /// a partial path.  Some formats have additional restrictions.
        /// </summary>
        /// <param name="fileName">Filename to adjust.</param>
        /// <returns>Adjusted filename.</returns>
        public static string AdjustFileName(this IArchive archive, string fileName) {
            if (archive is Binary2) {
                return Binary2.AdjustFileName(fileName);
            } else if (archive is NuFX) {
                return NuFX.AdjustFileName(fileName);
            } else if (archive is Zip) {
                return Zip.AdjustFileName(fileName);
            } else {
                // Not currently needed for AppleSingle / GZip.
                throw new NotImplementedException("Not handled: " + archive.GetType().Name);
            }
        }

        /// <summary>
        /// Performs a final check on the storage name.  Currently just tests the length.
        /// </summary>
        /// <param name="storageName">Full storage name.</param>
        /// <returns>True if it looks good, false if not.</returns>
        public static bool CheckStorageName(this IArchive archive, string storageName) {
            if (archive is Binary2) {
                return (storageName.Length <= Binary2.MAX_FILE_NAME_LEN);
            } else {
                // AppleSingle, GZip, NuFX, and ZIP can be very long.
                // No need to offer to rename to make it fit.
                return true;
            }
        }

        #endregion IArchive

        #region IDiskImage

        /// <summary>
        /// Formats an IDiskImage.  This should only be used on a newly-created IDiskImage,
        /// when the ChunkAccess property is non-null and the FileSystem property is null.
        /// Query the FileSystem out of the IDiskImage when this returns.
        /// </summary>
        /// <remarks>
        /// <para>The chunks will be zeroed out before the new filesystem is written.</para>
        /// <para>This does not work with custom sector codecs.</para>
        /// </remarks>
        /// <param name="diskImage">Disk image object.</param>
        /// <param name="fsType">Filesystem to format the disk with.</param>
        /// <param name="volumeName">IFileSystem.Format() argument.</param>
        /// <param name="volumeNum">IFileSystem.Format() argument.</param>
        /// <param name="makeBootable">IFileSystem.Format() argument.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <exception cref="InvalidOperationException">ChunkAccess is null, or FileSystem is
        ///   not null.</exception>
        /// <exception cref="NotSupportedException">Filesystem not supported by this
        ///   function.</exception>
        public static void FormatDisk(this IDiskImage diskImage, Defs.FileSystemType fsType,
                string volumeName, int volumeNum, bool makeBootable, AppHook appHook) {
            if (diskImage.ChunkAccess == null || diskImage.Contents != null) {
                throw new InvalidOperationException(
                    "Disk image must have non-null ChunkAccess and no Contents");
            }

            // Map a filesystem object onto the IChunkAccess.
            IFileSystem fs;
            switch (fsType) {
                case Defs.FileSystemType.ProDOS:
                    fs = new ProDOS(diskImage.ChunkAccess, appHook);
                    break;
                case Defs.FileSystemType.DOS32:
                case Defs.FileSystemType.DOS33:
                    fs = new DOS(diskImage.ChunkAccess, appHook);
                    break;
                case Defs.FileSystemType.HFS:
                    fs = new HFS(diskImage.ChunkAccess, appHook);
                    break;
                default:
                    throw new NotSupportedException("FormatDisk doesn't handle: " + fsType);
            }

            // Zero out the chunks.
            diskImage.ChunkAccess.Initialize();

            // Format the filesystem.
            fs.Format(volumeName, volumeNum, makeBootable);

            // Dispose of the IFileSystem object to ensure everything has been written.
            fs.Dispose();

            // Re-analyze the disk image.  This should find the filesystem.
            // TODO: make this work with custom codecs
            diskImage.AnalyzeDisk();
            Debug.Assert(diskImage.ChunkAccess != null);
            Debug.Assert(diskImage.Contents is IFileSystem);
        }

        /// <summary>
        /// Invokes <see cref="IDiskImage.AnalyzeDisk"/> with default arguments.
        /// </summary>
        /// <remarks>
        /// Don't use this for 5.25" disk images when a SectorOrder hint is available.
        /// </remarks>
        public static void AnalyzeDisk(this IDiskImage diskImage) {
            diskImage.AnalyzeDisk(null, Defs.SectorOrder.Unknown,
                IDiskImage.AnalysisDepth.Full);
        }

        #endregion IDiskImage

        #region IChunkAccess

        /// <summary>
        /// Loads a range of contiguous 512-byte blocks into a newly-allocated buffer.
        /// </summary>
        /// <param name="source">Chunk data source.</param>
        /// <param name="startBlock">Block number of first block to read.</param>
        /// <param name="count">Number of blocks to read.</param>
        /// <param name="data">Buffer that will hold data.</param>
        /// <param name="offset">Start offset within the data buffer.</param>
        /// <returns>Error code.</returns>
        /// <exception cref="ArgumentException">Invalid request.</exception>
        /// <exception cref="IOException">Disk access failure.</exception>
        public static void ReadBlocks(this IChunkAccess source, uint startBlock, int count,
                byte[] data, int offset) {
            if (count <= 0) {
                throw new ArgumentException("Invalid count");
            }
            // Check range; be mindful of integer overflow.
            if (((long)startBlock + count) * Defs.BLOCK_SIZE >= source.FormattedLength) {
                throw new ArgumentException("Bad startBlock or count");
            }

            for (int i = 0; i < count; i++) {
                source.ReadBlock((uint)(startBlock + i), data, offset + i * Defs.BLOCK_SIZE);
            }
        }

        /// <summary>
        /// Scans for unreadable chunks.
        /// </summary>
        /// <param name="chunkAccess">Chunk data source.</param>
        /// <returns>Number of bad blocks or sectors found.</returns>
        public static int CountUnreadableChunks(this IChunkAccess chunkAccess) {
            int failCount = 0;
            int checkCount = 0;
            DateTime startWhen = DateTime.Now;

            if (chunkAccess.HasBlocks) {
                uint numBlocks = (uint)(chunkAccess.FormattedLength / Defs.BLOCK_SIZE);
                for (uint blk = 0; blk < numBlocks; blk++) {
                    checkCount++;
                    if (!chunkAccess.TestBlock(blk, out bool unused)) {
                        failCount++;
                    }
                }
            } else {
                Debug.Assert(chunkAccess.HasSectors);
                uint numTrks = chunkAccess.NumTracks;
                for (uint trk = 0; trk < numTrks; trk++) {
                    for (uint sct = 0; sct < chunkAccess.NumSectorsPerTrack; sct++) {
                        checkCount++;
                        if (!chunkAccess.TestSector(trk, sct, out bool unused)) {
                            failCount++;
                        }
                    }
                }
            }
            Debug.WriteLine("Bad chunk scan complete in " +
                ((DateTime.Now - startWhen).TotalSeconds) + " sec: " + failCount + " of " +
                checkCount + " failed");
            return failCount;
        }

        /// <summary>
        /// Dumps the full contents to a file.  Intended for debugging.
        /// </summary>
        /// <param name="chunkAccess">Chunk data source.</param>
        /// <param name="fileName">Output filename.</param>
        /// <param name="asBlocks">If true, write blocks; if false, write sectors.</param>
        /// <exception cref="IOException">Disk access failure.</exception>
        public static void DumpToFile(this IChunkAccess chunkAccess, string pathName,
                bool asBlocks) {
            using (FileStream fs = new FileStream(pathName, FileMode.Create)) {
                if (asBlocks) {
                    Debug.Assert(chunkAccess.HasBlocks);
                    byte[] dataBuf = new byte[Defs.BLOCK_SIZE];
                    uint numBlocks = (uint)(chunkAccess.FormattedLength / Defs.BLOCK_SIZE);
                    for (uint blk = 0; blk < numBlocks; blk++) {
                        chunkAccess.ReadBlock(blk, dataBuf, 0);
                        fs.Write(dataBuf, 0, Defs.BLOCK_SIZE);
                    }
                } else {
                    Debug.Assert(chunkAccess.HasSectors);
                    byte[] dataBuf = new byte[Defs.SECTOR_SIZE];
                    uint numTrks = chunkAccess.NumTracks;
                    for (uint trk = 0; trk < numTrks; trk++) {
                        for (uint sct = 0; sct < chunkAccess.NumSectorsPerTrack; sct++) {
                            chunkAccess.ReadSector(trk, sct, dataBuf, 0);
                            fs.Write(dataBuf, 0, Defs.SECTOR_SIZE);
                        }
                    }
                }
            }
        }

        #endregion IChunkAccess

        #region IFileSystem

        /// <summary>
        /// Returns the FileSystemType value for a filesystem.
        /// </summary>
        /// <returns>FileSystemType enumerated value, or Unknown if not known.</returns>
        public static Defs.FileSystemType GetFileSystemType(this IFileSystem fs) {
            if (fs is DOS) {
                return Defs.FileSystemType.DOS33;   // we don't really use the DOS32 enum value
            } else if (fs is ProDOS) {
                return Defs.FileSystemType.ProDOS;
            } else if (fs is HFS) {
                return Defs.FileSystemType.HFS;
            } else {
                Debug.Assert(false, "Unhandled fs type: " + fs.GetType().Name);
                return Defs.FileSystemType.Unknown;
            }
        }

        /// <summary>
        /// Determines whether a string is a valid filename.
        /// </summary>
        /// <param name="fileName">Filename to test.</param>
        /// <returns>True if the filename is valid.</returns>
        public static bool IsValidFileName(this IFileSystem fs, string fileName) {
            if (fs is DOS) {
                return DOS_FileEntry.IsFileNameValid(fileName);
            } else if (fs is HFS) {
                return HFS_FileEntry.IsFileNameValid(fileName, false);
            } else if (fs is ProDOS) {
                return ProDOS_FileEntry.IsFileNameValid(fileName);
            } else {
                throw new NotImplementedException("Not handled IVFN: " + fs.GetType().Name);
            }
        }

        /// <summary>
        /// Determines whether a string is a valid volume name.
        /// </summary>
        /// <param name="volName">Volume name to test.</param>
        /// <returns>True if the volume name is valid.</returns>
        public static bool IsValidVolumeName(this IFileSystem fs, string volName) {
            if (fs is DOS) {
                return DOS_FileEntry.IsVolumeNameValid(volName);
            } else if (fs is HFS) {
                return HFS_FileEntry.IsFileNameValid(volName, true);
            } else if (fs is ProDOS) {
                return ProDOS_FileEntry.IsFileNameValid(volName);
            } else {
                throw new NotImplementedException("Not handled IVVN: " + fs.GetType().Name);
            }
        }

        /// <summary>
        /// Adjusts a filename to be compatible with the filesystem.
        /// </summary>
        /// <param name="fileName">Filename to adjust.</param>
        /// <returns>Adjusted filename.</returns>
        public static string AdjustFileName(this IFileSystem fs, string fileName) {
            // These are static methods implemented in the IFileEntry objects, alongside the
            // other filename functions.  Sometimes it's convenient to pretend that it's an
            // IFileSystem instance method.
            if (fs is DOS) {
                return DOS_FileEntry.AdjustFileName(fileName);
            } else if (fs is HFS) {
                return HFS_FileEntry.AdjustFileName(fileName);
            } else if (fs is ProDOS) {
                return ProDOS_FileEntry.AdjustFileName(fileName);
            } else {
                throw new NotImplementedException("Not handled AFN: " + fs.GetType().Name);
            }
        }

        /// <summary>
        /// Adjusts a volume name to be compatible with the filesystem.
        /// </summary>
        /// <param name="fileName">Filename to adjust.</param>
        /// <returns>Adjusted filename, or null if this filesystem doesn't support volume
        ///   names.</returns>
        public static string? AdjustVolumeName(this IFileSystem fs, string fileName) {
            if (fs is DOS) {
                return DOS_FileEntry.AdjustVolumeName(fileName);
            } else if (fs is HFS) {
                return HFS_FileEntry.AdjustVolumeName(fileName);
            } else if (fs is ProDOS) {
                return ProDOS_FileEntry.AdjustVolumeName(fileName);
            } else {
                throw new NotImplementedException("Not handled AVN: " + fs.GetType().Name);
            }
        }

        /// <summary>
        /// Finds an IFileEntry for the named file in the specified directory.
        /// </summary>
        /// <remarks>
        /// The name is matched against the "normalized" filename, i.e. the string version,
        /// not the raw version.
        /// </remarks>
        /// <param name="dirEntry">File entry for directory in which to search.</param>
        /// <param name="fileName">Name of file to search for.  May be empty.</param>
        /// <returns>The entry found.</returns>
        /// <exception cref="FileNotFoundException">File was not found.</exception>
        /// <exception cref="ArgumentException">Directory entry argument does not reference a
        ///   directory.</exception>
        public static IFileEntry FindFileEntry(this IFileSystem fs, IFileEntry dirEntry,
                string fileName) {
            if (!TryFindFileEntry(fs, dirEntry, fileName, out IFileEntry entry)) {
                throw new FileNotFoundException("Unable to find '" + fileName +
                    "' in directory '" + dirEntry.FileName + "'");
            }
            return entry;
        }

        /// <summary>
        /// Tries to find an IFileEntry for the named file in the specified directory.
        /// </summary>
        /// <param name="dirEntry">File entry for directory in which to search.</param>
        /// <param name="fileName">Name of file to search for.  May be empty.</param>
        /// <param name="entry">Result: entry found, or NO_ENTRY if no match found.</param>
        /// <returns>True if the entry was found.</returns>
        /// <exception cref="ArgumentException">Directory entry argument does not reference a
        ///   directory.</exception>
        public static bool TryFindFileEntry(this IFileSystem fs, IFileEntry dirEntry,
                string fileName, out IFileEntry entry) {
            if (!dirEntry.IsDirectory) {
                throw new ArgumentException("Argument is not a directory");
            }
            foreach (IFileEntry candidate in dirEntry) {
                if (candidate.CompareFileName(fileName) == 0) {
                    entry = candidate;
                    return true;
                }
            }
            entry = IFileEntry.NO_ENTRY;
            return false;
        }

        /// <summary>
        /// Finds a file entry that matches the specified pathname.  The pathname should be
        /// a correctly-formed partial pathname that starts in the root directory.
        /// </summary>
        /// <param name="fs">Filesystem object.</param>
        /// <param name="path">Partial path from root directory.  An empty string returns
        ///   the root directory instead.</param>
        /// <param name="fssep">Filename separator character.</param>
        /// <returns>Entry for file.</returns>
        /// <exception cref="FileNotFoundException">File was not found.</exception>
        public static IFileEntry EntryFromPath(this IFileSystem fs, string path, char fssep) {
            IFileEntry dirEntry = fs.GetVolDirEntry();
            if (path == string.Empty) {
                return dirEntry;
            }

            // Walk through the path components, descending into the tree.
            string[] names = path.Split(fssep);
            IFileEntry match = IFileEntry.NO_ENTRY;
            foreach (string name in names) {
                if (!dirEntry.IsDirectory) {
                    throw new FileNotFoundException("Path ended in non-directory '" +
                        dirEntry.FileName + "'");
                }
                match = IFileEntry.NO_ENTRY;
                foreach (IFileEntry candidate in dirEntry) {
                    if (candidate.CompareFileName(name) == 0) {
                        match = candidate;
                        break;
                    }
                }
                if (match == IFileEntry.NO_ENTRY) {
                    throw new FileNotFoundException("No match for " + name);
                }
                dirEntry = match;
            }

            Debug.Assert(match != IFileEntry.NO_ENTRY);
            return match;
        }

        /// <summary>
        /// Creates a new file, also setting the file type.
        /// </summary>
        /// <remarks>
        /// <para>This is primarily useful for DOS I/A/B files, which have a small header at
        /// the start of the file.  For those, this will immediately do a zero-byte write to
        /// cause the header to be created.</para>
        /// </remarks>
        /// <param name="dirEntry">Directory in which to create the file.</param>
        /// <param name="fileName">Name of file to create.</param>
        /// <param name="mode">Regular file, extended file, or directory.</param>
        /// <param name="fileType">ProDOS file type.</param>
        /// <returns>Entry for new file.</returns>
        /// <exception cref="DiskFullException">Disk full, or volume directory full.</exception>
        public static IFileEntry CreateFile(this IFileSystem fs, IFileEntry dirEntry,
                string fileName, IFileSystem.CreateMode mode, byte fileType) {
            IFileEntry entry = fs.CreateFile(dirEntry, fileName, mode);
            entry.FileType = fileType;
            if (fs is DOS && (fileType == FileAttribs.FILE_TYPE_INT ||
                    fileType == FileAttribs.FILE_TYPE_BAS ||
                    fileType == FileAttribs.FILE_TYPE_BIN)) {
                // This is unnecessary if we're about to write data to the file anyway, but
                // essential for an empty file because the I/O code avoids zero-byte writes.
                using (Stream stream = fs.OpenFile(entry, IFileSystem.FileAccessMode.ReadWrite,
                        Defs.FilePart.DataFork)) {
                    stream.Write(CommonUtil.RawData.EMPTY_BYTE_ARRAY, 0, 0);
                }
            }
            entry.SaveChanges();
            return entry;
        }

        //public static DiskFileStream OpenDataForkRO(this IFileSystem fs, IFileEntry entry) {
        //    return fs.OpenFile(entry, IFileSystem.FileAccessMode.ReadOnly, Defs.FilePart.DataFork);
        //}

        /// <summary>
        /// Dumps the filesystem to a file.  Intended for debugging.  The filesystem will be
        /// switched to raw-access mode.
        /// </summary>
        /// <remarks>
        /// DOS filesystems will be written to a DOS-ordered (.do) file, everything else will
        /// be written to a ProDOS-ordered (.po) file.
        /// </remarks>
        /// <param name="pathName">Pathname of output file.</param>
        public static void DumpToFile(this IFileSystem fs, string pathName) {
            fs.PrepareRawAccess();
            bool asBlocks = true;
            if (fs is FS.DOS) {
                asBlocks = false;
            }
            fs.RawAccess.DumpToFile(pathName, asBlocks);
        }

        #endregion IFileSystem

        #region IFileEntry

        /// <summary>
        /// Obtains the filesystem the file entry is a part of.
        /// </summary>
        /// <remarks>
        /// <para>File entry objects are associated with both filesystems and file archives, so
        /// we need to break this down by class.</para>
        /// <para>Invalidated file entry objects will null their FileSystem reference, so this
        /// should only return non-null for valid entries.</para>
        /// </remarks>
        /// <param name="entry">File entry.</param>
        /// <returns>Filesystem reference, or null if entry is not part of a filesystem.</returns>
        public static IFileSystem? GetFileSystem(this IFileEntry entry) {
            if (entry is DOS_FileEntry) {
                return ((DOS_FileEntry)entry).FileSystem;
            } else if (entry is HFS_FileEntry) {
                return ((HFS_FileEntry)entry).FileSystem;
            } else if (entry is ProDOS_FileEntry) {
                return ((ProDOS_FileEntry)entry).FileSystem;
            } else {
                return null;
            }
        }

        /// <summary>
        /// Obtains the archive the file entry is a part of.
        /// </summary>
        /// <remarks>
        /// <para>File entry objects are associated with both filesystems and file archives, so
        /// we need to break this down by class.</para>
        /// <para>Invalidated file entry objects will null their Archive reference, so this
        /// should only return non-null for valid entries.</para>
        /// </remarks>
        /// <param name="entry">File entry.</param>
        /// <returns>Filesystem reference, or null if entry is not part of a filesystem.</returns>
        public static IArchive? GetArchive(this IFileEntry entry) {
            if (entry is AppleSingle_FileEntry) {
                return ((AppleSingle_FileEntry)entry).Archive;
            } else if (entry is Binary2_FileEntry) {
                return ((Binary2_FileEntry)entry).Archive;
            } else if (entry is GZip_FileEntry) {
                return ((GZip_FileEntry)entry).Archive;
            } else if (entry is NuFX_FileEntry) {
                return ((NuFX_FileEntry)entry).Archive;
            } else if (entry is Zip_FileEntry) {
                return ((Zip_FileEntry)entry).Archive;
            } else {
                return null;
            }
        }

        /// <summary>
        /// Determines whether the entry is a MacZip "header" entry.  These are ZIP archive
        /// entries with filenames that start with "__MACOSX/".
        /// </summary>
        /// <returns>True if the entry is a MacZip header file.</returns>
        public static bool IsMacZipHeader(this IFileEntry entry) {
            // Directory entries *can* have MacZip headers.  I'm not sure what that's useful
            // for, but I don't want to filter them out here.  Note that directory headers are
            // not directories, and so don't end with '/'.
            if (entry is Zip_FileEntry &&
                    entry.FullPathName.StartsWith(Zip.MAC_ZIP_RSRC_PREFIX_DIR)) {
                // Find the filename and verify that it starts with "._" (AppleDouble).  This
                // probably isn't necessary, as there shouldn't be anything else in __MACOSX,
                // but this is commonly used to filter out files, and we don't want to hide
                // anything we shouldn't.
                int fnIndex = entry.FileName.LastIndexOf(Zip.SCharacteristics.DefaultDirSep);
                if (fnIndex < 0) {
                    Debug.Assert(false);    // should be impossible; name has "__MACOSX/" prefix
                    fnIndex = 0;
                } else {
                    fnIndex++;
                }
                if (entry.FileName.Length > fnIndex + 1 &&
                        entry.FileName[fnIndex] == AppleSingle.ADF_PREFIX[0] &&
                        entry.FileName[fnIndex + 1] == AppleSingle.ADF_PREFIX[1]) {
                    return true;
                }
                return false;
            }
            return false;
        }

        #endregion
    }
}
