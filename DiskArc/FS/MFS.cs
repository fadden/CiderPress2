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

using CommonUtil;
using static DiskArc.Defs;
using static DiskArc.FileAnalyzer.DiskLayoutEntry;
using static DiskArc.IFileSystem;

namespace DiskArc.FS {
    /// <summary>
    /// Macintosh File System (MFS) implementation.
    /// </summary>
    public class MFS : IFileSystem {
        public const int MAX_VOL_NAME_LEN = 27;
        public const int MAX_FILE_NAME_LEN = 255;
        public const long MAX_FILE_LEN = 0x7fffffff;    // int.MaxValue; one byte shy of 2GB

        internal const long MIN_VOL_SIZE = 16 * 2 * BLOCK_SIZE;     // 16KB (arbitrary)
        internal const long MAX_VOL_SIZE = 1440 * 2 * BLOCK_SIZE;   // 1440KB (arbitrary)

        /// <summary>Minimum size of the file directory, in logical blocks (used by Format).</summary>
        private const int MIN_DIR_LENGTH = 4;
        /// <summary>Default clump size, in allocation blocks (used by Format).</summary>
        private const int DEFAULT_CLUMP_ABLKS = 8;

        private const string VOLNAME_RULES =
            "1-27 characters.";
        private const string FILENAME_RULES =
            "1-255 characters.";
        private static FSCharacteristics sCharacteristics = new FSCharacteristics(
            name: "MFS",
            canWrite: true,
            isHierarchical: false,
            dirSep: IFileEntry.NO_DIR_SEP,
            hasResourceForks: true,
            fnSyntax: FILENAME_RULES,
            vnSyntax: VOLNAME_RULES,
            tsStart: TimeStamp.HFS_MIN_TIMESTAMP,
            tsEnd: TimeStamp.HFS_MAX_TIMESTAMP
        );

        //
        // IFileSystem interfaces.
        //

        public FSCharacteristics Characteristics => sCharacteristics;
        public static FSCharacteristics SCharacteristics => sCharacteristics;

        public Notes Notes { get; } = new Notes();

        public bool IsReadOnly => ChunkAccess.IsReadOnly || IsDubious;

        public bool IsDubious { get; internal set; }

        /// <summary>
        /// True if the in-memory volume directory has pending changes that need to be written.
        /// </summary>
        internal bool IsVolDirDirty { get; set; }

        public long FreeSpace { get; private set; }

        public GatedChunkAccess RawAccess { get; }

        //
        // Implementation-specific.
        //

        /// <summary>
        /// Data source.  Contents may be shared in various ways.
        /// </summary>
        internal IChunkAccess ChunkAccess { get; private set; }

        /// <summary>
        /// Master directory block.
        /// </summary>
        internal MFS_MDB? VolMDB { get; private set; }

        /// <summary>
        /// Application-specified options and message logging.
        /// </summary>
        internal AppHook AppHook { get; private set; }

        /// <summary>
        /// List of open files.
        /// </summary>
        private OpenFileTracker mOpenFiles = new OpenFileTracker();

        /// <summary>
        /// "Fake" volume directory entry, used to hold directory entries.
        /// </summary>
        private IFileEntry mVolDirEntry;

        /// <summary>
        /// True if we're in file-access mode, false if raw-access mode.
        /// </summary>
        private bool IsPreppedForFileAccess { get { return mVolDirEntry != IFileEntry.NO_ENTRY; } }


        // Delegate: test image to see if it's ours.
        public static TestResult TestImage(IChunkAccess chunkSource, AppHook appHook) {
            if (!chunkSource.HasBlocks) {
                return TestResult.No;
            }
            if (!IsSizeAllowed(chunkSource.FormattedLength)) {
                return TestResult.No;
            }

            MFS_MDB mdb = new MFS_MDB(chunkSource);
            mdb.Read();
            if (mdb.Signature != MFS_MDB.SIGNATURE) {
                return TestResult.No;
            }
            if (mdb.AllocBlockSize == 0 || (mdb.AllocBlockSize & 0x1ff) != 0) {
                // Allocation block size must be a nonzero multiple of 512.
                return TestResult.No;
            }
            if (string.IsNullOrEmpty(mdb.VolumeName)) {
                return TestResult.No;
            }
            int diskBlockCount = (int)(chunkSource.FormattedLength / BLOCK_SIZE);
            if (mdb.DirectoryStart >= diskBlockCount || mdb.AllocBlockStart >= diskBlockCount) {
                return TestResult.No;
            }

            int blocksPerAlloc = (int)(mdb.AllocBlockSize / BLOCK_SIZE);
            int diskAllocCount = (diskBlockCount - mdb.AllocBlockStart) / blocksPerAlloc;
            if (mdb.NumAllocBlocks > diskAllocCount) {
                Debug.WriteLine("MFS claims to have " + mdb.NumAllocBlocks +
                    " alloc blocks, but max is " + diskAllocCount);
                return TestResult.No;
            }

            return TestResult.Yes;
        }

        // Delegate: returns true if the size (in bytes) is valid for this filesystem.
        public static bool IsSizeAllowed(long size) {
            if (size % BLOCK_SIZE != 0) {
                return false;       // must be blocks
            }
            if (size < MIN_VOL_SIZE || size > MAX_VOL_SIZE) {
                return false;
            }
            return true;
        }

        public MFS(IChunkAccess chunks, AppHook appHook) {
            Debug.Assert(chunks.HasBlocks);
            ChunkAccess = chunks;
            AppHook = appHook;

            RawAccess = new GatedChunkAccess(chunks);
            mVolDirEntry = IFileEntry.NO_ENTRY;
        }

        public override string ToString() {
            string id = mVolDirEntry == IFileEntry.NO_ENTRY ? "raw" : mVolDirEntry.FileName;
            return "[MFS (" + id + ")]";
        }

        // IFileSystem
        public string? DebugDump() { return null; }

        // IDisposable generic finalizer.
        ~MFS() {
            Dispose(false);
        }
        // IDisposable generic Dispose() implementation.
        public void Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        private bool mDisposed;
        protected virtual void Dispose(bool disposing) {
            if (mDisposed) {
                AppHook.LogW("Attempting to dispose of MFS object twice");
                return;
            }
            AppHook.LogD("MFS.Dispose");
            if (disposing) {
                if (mOpenFiles.Count != 0) {
                    CloseAll();
                }
                if (mVolDirEntry != IFileEntry.NO_ENTRY) {
                    // Invalidate all associated file entry objects.
                    InvalidateFileEntries();
                }
                RawAccess.AccessLevel = GatedChunkAccess.AccessLvl.Closed;
                mDisposed = true;
            }
        }

        // IFileSystem
        public void Flush() {
            mOpenFiles.FlushAll();
            FlushDirectory();
        }

        /// <summary>
        /// Writes the volume directory and MDB back to disk, if they have been modified.
        /// </summary>
        /// <exception cref="DiskFullException">The volume directory is full.</exception>
        internal void FlushDirectory() {
            if (!IsVolDirDirty) {
                return;
            }
            Debug.Assert(IsPreppedForFileAccess);
            MFS_MDB mdb = VolMDB!;

            int dirBlockCount = mdb.DirectoryLength;
            byte[] buf = new byte[dirBlockCount * BLOCK_SIZE];      // zero-filled

            // Pack the entries into the directory blocks.  Entries can't cross block boundaries,
            // and each is aligned to a 16-bit word (handled by StoreEntry).
            MFS_FileEntry volDir = (MFS_FileEntry)mVolDirEntry;
            int curBlock = 0;
            int blockOffset = 0;
            foreach (IFileEntry ientry in volDir.ChildList) {
                MFS_FileEntry entry = (MFS_FileEntry)ientry;
                int entryLen = entry.GetStoredLength();
                if (entryLen > BLOCK_SIZE) {
                    throw new IOException("Directory entry too large to store");
                }
                if (blockOffset + entryLen > BLOCK_SIZE) {
                    // Advance to the next directory block.
                    curBlock++;
                    blockOffset = 0;
                    if (curBlock >= dirBlockCount) {
                        throw new DiskFullException("Volume directory is full");
                    }
                }
                int bufOffset = curBlock * BLOCK_SIZE + blockOffset;
                entry.StoreEntry(buf, ref bufOffset);
                blockOffset = bufOffset - curBlock * BLOCK_SIZE;
            }

            // Write the directory blocks back to disk.
            for (int i = 0; i < dirBlockCount; i++) {
                ChunkAccess.WriteBlock(mdb.DirectoryStart + (uint)i, buf, i * BLOCK_SIZE);
            }

            // Persist MDB changes (free counts, file count, next file number, alloc map).
            mdb.Write();
            FreeSpace = (long)mdb.FreeAllocBlocks * mdb.AllocBlockSize;
            IsVolDirDirty = false;
        }

        // IFileSystem
        public void PrepareFileAccess(bool doScan) {
            if (IsPreppedForFileAccess) {
                Debug.WriteLine("Volume already prepared for file access");
                return;
            }

            try {
                // Reset all values and scan the volume.
                IsDubious = false;
                Notes.Clear();
                ScanVolume(doScan);
                RawAccess.AccessLevel = GatedChunkAccess.AccessLvl.ReadOnly;
            } catch (Exception ex) {
                // Failed; reset for raw.
                AppHook.LogE("Unable to prepare file access: " + ex.Message);
                PrepareRawAccess();
                throw new DAException("Unable to prepare file access", ex);
            }
        }

        // IFileSystem
        public void PrepareRawAccess() {
            if (mOpenFiles.Count != 0) {
                throw new DAException("Cannot switch to raw access mode with files open");
            }

            if (mVolDirEntry != IFileEntry.NO_ENTRY) {
                // Invalidate the FileEntry tree.  If we don't do this the application could
                // try to use a retained object after it was switched back to file access.
                InvalidateFileEntries();
            }
            mVolDirEntry = IFileEntry.NO_ENTRY;
            VolMDB = null;
            IsDubious = false;
            RawAccess.AccessLevel = GatedChunkAccess.AccessLvl.Open;
        }

        /// <summary>
        /// Marks all file entry objects as invalid.
        /// </summary>
        private void InvalidateFileEntries() {
            Debug.Assert(mVolDirEntry != IFileEntry.NO_ENTRY);
            MFS_FileEntry volDir = (MFS_FileEntry)mVolDirEntry;
            if (!volDir.IsValid) {
                // Already done?  Shouldn't happen.
                return;
            }
            foreach (IFileEntry child in volDir) {
                MFS_FileEntry entry = (MFS_FileEntry)child;
                entry.SaveChanges();
                entry.Invalidate();
            }
            volDir.Invalidate();
        }

        /// <summary>
        /// Scans the contents of the volume directory.
        /// </summary>
        /// <exception cref="IOException">Disk access failure.</exception>
        /// <exception cref="DAException">Invalid filesystem.</exception>
        private void ScanVolume(bool doScan) {
            VolMDB = new MFS_MDB(ChunkAccess);
            VolMDB.Read();
            if (doScan) {
                VolMDB.InitVolumeUsage();
            }

            mVolDirEntry = MFS_FileEntry.ScanDirectory(this, doScan);

            if (VolMDB.CalcFreeBlocks() != VolMDB.FreeAllocBlocks) {
                Notes.AddW("MDB free block count (" + VolMDB.FreeAllocBlocks +
                    ") differs from calculation (" + VolMDB.CalcFreeBlocks() + ")");
                IsDubious = true;
            }
            FreeSpace = (long)VolMDB.FreeAllocBlocks * VolMDB.AllocBlockSize;

            if (doScan) {
                VolumeUsage vu = VolMDB.VolUsage!;
                vu.Analyze(out int markedUsed, out int unusedMarked,
                        out int notMarkedUsed, out int conflicts);
                AppHook.LogI("Usage counts: " + markedUsed + " in use, " +
                    unusedMarked + " unused but marked, " +
                    notMarkedUsed + " used but not marked, " +
                    conflicts + " conflicts");

                if (notMarkedUsed != 0) {
                    // Danger!
                    Notes.AddE("Found " + notMarkedUsed + " used blocks that are marked free");
                    IsDubious = true;
                }
                if (conflicts != 0) {
                    // Individual files are marked "dubious", and can't be modified.
                    Notes.AddW("Found " + conflicts + " blocks in use by more than one file");
                }

                if (unusedMarked != 0) {
                    bool doWarnUnused =
                        AppHook.GetOptionBool(DAAppHook.WARN_MARKED_BUT_UNUSED, false);
                    if (doWarnUnused) {
                        Notes.AddW("Found " + unusedMarked +
                            " unused sectors that are marked used");
                    } else {
                        Notes.AddI("Found " + unusedMarked +
                            " unused sectors that are marked used");
                    }
                }

                Debug.WriteLine(vu.DebugDump());
            }
        }

        // IFileSystem
        public IMultiPart? FindEmbeddedVolumes() {
            return null;
        }

        // IFileSystem
        public void Format(string volumeName, int volumeNum, bool makeBootable) {
            // We only reject the call if the underlying storage is read-only.  If the filesystem
            // is read-only because of file damage, reformatting it is fine.
            if (ChunkAccess.IsReadOnly) {
                throw new IOException("Can't format read-only data");
            }
            if (IsPreppedForFileAccess) {
                throw new IOException("Must be in raw access mode");
            }
            if (!IsSizeAllowed(ChunkAccess.FormattedLength)) {
                throw new ArgumentException("size not valid");
            }
            if (!MFS_FileEntry.IsVolumeNameValid(volumeName)) {
                throw new ArgumentException("Invalid volume name");
            }

            int totalBlocks = (int)(ChunkAccess.FormattedLength / BLOCK_SIZE);

            // Zero the boot blocks.  MFS boot code lives here on startup disks, but we have no
            // Mac boot image to install, so the area is always left empty (matching how real MFS
            // formats non-bootable disks).
            byte[] zeroBlock = new byte[BLOCK_SIZE];
            ChunkAccess.WriteBlock(0, zeroBlock, 0);
            ChunkAccess.WriteBlock(1, zeroBlock, 0);

            // Compute the volume geometry.
            //
            // Layout: 2 boot blocks, the 2-block MDB, the fixed-size file directory, the
            // allocation-block area, and finally a 2-block backup copy of the MDB at the end.
            // The directory begins immediately after the MDB (block 4).
            const int DIR_START = (int)MFS_MDB.MDB_BLOCK_NUM + 2;       // block 4
            const int NON_DIR_OVERHEAD = 2 /*boot*/ + 2 /*MDB*/ + 2 /*backup MDB*/;

            // Directory length.  Real MFS uses 12 directory blocks on a 400KB disk and 26 on an
            // 800KB disk; this interpolates between (and extrapolates from) those reference
            // points, keeping the allocation-block count close to Apple's.
            int dirLength = (7 * totalBlocks - 600) / 400;
            if (dirLength < MIN_DIR_LENGTH) {
                dirLength = MIN_DIR_LENGTH;
            }

            int allocRegion = totalBlocks - NON_DIR_OVERHEAD - dirLength;
            if (allocRegion < 1) {
                throw new ArgumentException("size not valid");      // shouldn't happen
            }

            // Choose the allocation block size: the smallest power-of-two multiple of the logical
            // block size for which the allocation map (one 12-bit entry per block) still fits
            // within the two-block MDB.  This reproduces Apple's standard geometry: a 1KB
            // allocation block for 400KB disks, 2KB for 800KB.
            int logicalPerAlloc = 1;
            int numAllocBlocks = allocRegion / logicalPerAlloc;
            while (numAllocBlocks > MFS_MDB.MAX_ALLOC_BLOCKS) {
                logicalPerAlloc *= 2;
                numAllocBlocks = allocRegion / logicalPerAlloc;
            }
            int allocBlockSize = logicalPerAlloc * BLOCK_SIZE;
            int allocBlockStart = DIR_START + dirLength;

            // The clump size is a hint for how much to allocate at a time.  Our allocator works
            // one block at a time and ignores it, but we set a reasonable value (matching the
            // 8-allocation-block clump seen on real 800KB MFS disks).
            int clumpSize = allocBlockSize * DEFAULT_CLUMP_ABLKS;

            // Zero the file directory blocks so the scanner sees an empty directory.
            for (int i = 0; i < dirLength; i++) {
                ChunkAccess.WriteBlock((uint)(DIR_START + i), zeroBlock, 0);
            }

            // Fill out and write the MDB (primary + backup copies).
            MFS_MDB mdb = new MFS_MDB(ChunkAccess);
            mdb.Format(volumeName, DateTime.Now, DIR_START, (ushort)dirLength,
                (ushort)numAllocBlocks, (uint)allocBlockSize, (uint)clumpSize,
                (ushort)allocBlockStart, totalBlocks);

            // Reset state.
            PrepareRawAccess();
        }

        /// <summary>
        /// Performs general checks on file-access calls, throwing exceptions when something
        /// is amiss.  An exception here generally indicates an error in the program calling
        /// into the library.
        /// </summary>
        /// <param name="op">Short string describing the operation.</param>
        /// <param name="ientry">File being accessed.</param>
        /// <param name="wantWrite">True if this operation might modify the file.</param>
        /// <param name="part">Which part of the file we want access to.  Pass "Unknown" to
        ///   match on any part.</param>
        /// <exception cref="IOException">Various.</exception>
        /// <exception cref="ArgumentException">Various.</exception>
        private void CheckFileAccess(string op, IFileEntry ientry, bool wantWrite, FilePart part) {
            if (mDisposed) {
                throw new ObjectDisposedException("Object was disposed");
            }
            if (!IsPreppedForFileAccess) {
                throw new IOException("Filesystem object not prepared for file access");
            }
            if (wantWrite && IsReadOnly) {
                throw new IOException("Filesystem is read-only");
            }
            if (ientry == IFileEntry.NO_ENTRY) {
                throw new ArgumentException("Cannot operate on NO_ENTRY");
            }
            if (ientry.IsDamaged) {
                throw new IOException("File '" + ientry.FileName +
                    "' is too damaged to access");
            }
            if (ientry.IsDubious && wantWrite) {
                throw new IOException("File '" + ientry.FileName +
                    "' is too damaged to modify");
            }
            MFS_FileEntry? entry = ientry as MFS_FileEntry;
            if (entry == null || entry.FileSystem != this) {
                if (entry != null && entry.FileSystem == null) {
                    // Invalid entry; could be a deleted file, or from before a raw-mode switch.
                    throw new IOException("File entry is invalid");
                } else {
                    throw new FileNotFoundException("File entry is not part of this filesystem");
                }
            }
            if (!mOpenFiles.CheckOpenConflict(entry, wantWrite, part)) {
                throw new IOException("File is already open; cannot " + op);
            }
        }
        // IFileSystem
        public IFileEntry GetVolDirEntry() {
            return mVolDirEntry;
        }

        // IFileSystem
        public DiskFileStream OpenFile(IFileEntry ientry, FileAccessMode mode, FilePart part) {
            if (part == FilePart.RawData) {
                part = FilePart.DataFork;   // do this before is-file-open check
            }
            CheckFileAccess("open", ientry, mode != FileAccessMode.ReadOnly, part);
            if (mode != FileAccessMode.ReadOnly && mode != FileAccessMode.ReadWrite) {
                throw new ArgumentException("Unknown file access mode " + mode);
            }
            if (ientry.IsDirectory) {
                throw new IOException("Cannot open directories");   // nothing there to see
            }

            MFS_FileEntry entry = (MFS_FileEntry)ientry;
            switch (part) {
                case FilePart.DataFork:
                case FilePart.RawData:
                case FilePart.RsrcFork:
                    break;
                default:
                    throw new ArgumentException("Unknown file part " + part);
            }

            MFS_FileDesc pfd = MFS_FileDesc.CreateFD(entry, mode, part, false);
            mOpenFiles.Add(this, entry, pfd);
            return pfd;
        }

        internal void CloseFile(DiskFileStream ifd) {
            MFS_FileDesc fd = (MFS_FileDesc)ifd;
            if (fd.FileSystem != this) {
                // Should be impossible, though it could be null if previous close invalidated it.
                if (fd.FileSystem == null) {
                    throw new IOException("Invalid file descriptor");
                } else {
                    throw new IOException("File descriptor was opened by a different filesystem");
                }
            }
            Debug.Assert(!mDisposed, "closing file in filesystem after dispose");

            // Find the file record, searching by descriptor.
            if (!mOpenFiles.RemoveDescriptor(ifd)) {
                throw new IOException("Open file record not found: " + fd);
            }

            // Take the opportunity to flush the volume directory, in case the file was modified.
            FlushDirectory();
        }

        // IFileSystem
        public void CloseAll() {
            mOpenFiles.CloseAll();
        }

        // IFileSystem
        public IFileEntry CreateFile(IFileEntry idirEntry, string fileName, CreateMode mode) {
            CheckFileAccess("create", idirEntry, true, FilePart.Unknown);
            if (idirEntry != mVolDirEntry) {
                throw new ArgumentException("Invalid directory entry for MFS");
            }
            // MFS is a flat filesystem with no subdirectories; files always have both forks.
            if (mode != CreateMode.File && mode != CreateMode.Extended) {
                throw new ArgumentException("Invalid MFS creation mode: " + mode);
            }
            if (fileName == null || !MFS_FileEntry.IsFileNameValid(fileName)) {
                throw new ArgumentException("Invalid filename '" + fileName + "'");
            }

            MFS_MDB mdb = VolMDB!;
            MFS_FileEntry volDir = (MFS_FileEntry)mVolDirEntry;

            // Reject duplicate names.
            foreach (IFileEntry entry in volDir) {
                if (entry.CompareFileName(fileName) == 0) {
                    throw new IOException("A file with that name already exists");
                }
            }

            // Create a zero-length entry (both forks empty) with a fresh file number.
            uint fileNumber = mdb.NextFileNum;
            MFS_FileEntry newEntry = MFS_FileEntry.CreateEntry(this, mVolDirEntry, fileName,
                fileNumber, DateTime.Now);

            volDir.ChildList.Add(newEntry);
            mdb.NextFileNum = fileNumber + 1;
            mdb.NumFiles = (ushort)(mdb.NumFiles + 1);
            IsVolDirDirty = true;
            try {
                FlushDirectory();
            } catch {
                // Probably a full directory.  Roll back the in-memory changes.  Nothing was
                // written to disk (FlushDirectory throws before writing), so on-disk state is
                // unchanged.
                volDir.ChildList.Remove(newEntry);
                newEntry.Invalidate();
                mdb.NextFileNum = fileNumber;
                mdb.NumFiles = (ushort)(mdb.NumFiles - 1);
                throw;
            }
            return newEntry;
        }

        // IFileSystem
        public void AddRsrcFork(IFileEntry entry) {
            throw new IOException("Filesystem does not support resource forks");
        }

        // IFileSystem
        public void MoveFile(IFileEntry ientry, IFileEntry destDir, string newFileName) {
            CheckFileAccess("move", ientry, true, FilePart.Unknown);
            if (ientry == mVolDirEntry) {
                throw new IOException("Cannot rename the volume directory");
            }
            // MFS is a flat filesystem; the only valid destination is the volume directory, so
            // "move" degenerates to "rename".
            if (destDir != mVolDirEntry) {
                throw new IOException("MFS is a flat filesystem; cannot move files into a folder");
            }
            if (newFileName == null || !MFS_FileEntry.IsFileNameValid(newFileName)) {
                throw new ArgumentException("Invalid filename '" + newFileName + "'");
            }

            MFS_FileEntry entry = (MFS_FileEntry)ientry;
            MFS_FileEntry volDir = (MFS_FileEntry)mVolDirEntry;

            // Reject a name that collides with a different existing file (a case-only change to
            // the same entry is allowed).
            foreach (IFileEntry other in volDir) {
                if (other != entry && other.CompareFileName(newFileName) == 0) {
                    throw new IOException("A file with that name already exists");
                }
            }

            byte[] oldRawName = entry.RawFileName;      // saved for rollback
            entry.FileName = newFileName;               // validates, updates name, marks dirty
            try {
                FlushDirectory();
            } catch {
                // A longer name may have overflowed the fixed-size directory area.  Nothing was
                // written (FlushDirectory throws before writing), so restoring the in-memory name
                // leaves the volume consistent.
                entry.RawFileName = oldRawName;
                throw;
            }
        }

        // IFileSystem
        public void DeleteFile(IFileEntry ientry) {
            CheckFileAccess("delete", ientry, true, FilePart.Unknown);
            if (ientry == mVolDirEntry) {
                throw new IOException("Cannot delete the volume directory");
            }

            MFS_FileEntry entry = (MFS_FileEntry)ientry;
            MFS_MDB mdb = VolMDB!;
            MFS_FileEntry volDir = (MFS_FileEntry)mVolDirEntry;

            // Release both fork block chains (either may be empty).
            mdb.FreeChain(entry.DataStartBlock);
            mdb.FreeChain(entry.RsrcStartBlock);

            // Remove the entry from the directory and update the file count.
            volDir.ChildList.Remove(entry);
            mdb.NumFiles = (ushort)(mdb.NumFiles - 1);
            IsVolDirDirty = true;

            // Removing an entry can only shrink the directory, so this won't overflow.
            FlushDirectory();

            // Invalidate the object so stale application references fail fast.
            entry.Invalidate();
        }

        #region Miscellaneous

        internal uint AllocBlockToLogiBlock(uint allocBlockNum) {
            Debug.Assert(VolMDB != null);
            if (allocBlockNum < MFS_MDB.RESERVED_BLOCKS ||
                    allocBlockNum - MFS_MDB.RESERVED_BLOCKS >= VolMDB.NumAllocBlocks) {
                throw new IOException("Invalid allocation block " + allocBlockNum);
            }
            return VolMDB.AllocBlockStart +
                (allocBlockNum - MFS_MDB.RESERVED_BLOCKS) * VolMDB.LogicalPerAllocBlock;
        }

        #endregion
    }
}
