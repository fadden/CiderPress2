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
    /// Filesystem used for the UCSD Pascal system on the Apple II.
    /// </summary>
    public class Pascal : IFileSystem {
        public const int VOL_DIR_START_BLOCK = 2;
        public const int MIN_VOL_SIZE = 6 * BLOCK_SIZE;     // 2 boot blocks, 4 dir blocks
        public const int MAX_VOL_SIZE = BLOCK_SIZE * 65535; // one block shy of 32MB
        public const int MAX_VOL_NAME_LEN = 7;
        public const int MAX_FILE_NAME_LEN = 15;
        public const long MAX_FILE_LEN = 0x01000000;        // 16MB (theoretically)

        private const string FILENAME_RULES =
            "1-15 characters, must not include '$=?,[#:', spaces, or control characters.";
        private const string VOLNAME_RULES =
            "1-7 characters, must not include '$=?,[#:', spaces, or control characters.";
        private static FSCharacteristics sCharacteristics = new FSCharacteristics(
            name: "Apple Pascal",
            canWrite: true,
            isHierarchical: false,
            dirSep: IFileEntry.NO_DIR_SEP,
            hasResourceForks: false,
            fnSyntax: FILENAME_RULES,
            vnSyntax: VOLNAME_RULES,
            tsStart: TimeStamp.PASCAL_MIN_TIMESTAMP,
            tsEnd: TimeStamp.PASCAL_MAX_TIMESTAMP
        );

        //
        // IFileSystem interfaces.
        //

        public FSCharacteristics Characteristics => sCharacteristics;
        public static FSCharacteristics SCharacteristics => sCharacteristics;

        public Notes Notes { get; } = new Notes();

        public bool IsReadOnly { get { return ChunkAccess.IsReadOnly || IsDubious; } }

        public bool IsDubious { get; internal set; }

        public long FreeSpace { get { return CalcFreeBlocks() * BLOCK_SIZE; } }

        public GatedChunkAccess RawAccess { get; private set; }

        //
        // Implementation-specific.
        //

        /// <summary>
        /// Data source.  Contents may be shared in various ways.
        /// </summary>
        internal IChunkAccess ChunkAccess { get; private set; }

        /// <summary>
        /// True if a change has been made that requires writing out the volume directory.  This
        /// is set for any change to file attributes.
        /// </summary>
        internal bool IsVolDirDirty { get; set; }

        /// <summary>
        /// Volume usage map.  Only valid in file-access mode.
        /// </summary>
        internal VolumeUsage? VolUsage { get; private set; }

        /// <summary>
        /// Application-specified options and message logging.
        /// </summary>
        internal AppHook AppHook { get; private set; }

        /// <summary>
        /// Volume directory header, as read from disk.
        /// </summary>
        private VolDirHeader mVolDirHeader;

        /// <summary>
        /// "Fake" volume directory entry, used to hold catalog entries.
        /// </summary>
        private IFileEntry mVolDirEntry;

        /// <summary>
        /// Buffer that holds directory data while we're flushing it.  This only exists to
        /// reduce allocations.
        /// </summary>
        private byte[] mDirOutBuffer = RawData.EMPTY_BYTE_ARRAY;

        /// <summary>
        /// List of open files.
        /// </summary>
        private OpenFileTracker mOpenFiles = new OpenFileTracker();

        /// <summary>
        /// Total blocks present in the filesystem, as determined by the value in the volume
        /// header.  This is meaningless in block-edit mode, where the bounds are determined
        /// by the ChunkAccess.
        /// </summary>
        public int TotalBlocks { get { return mVolDirHeader.mVolBlockCount; } }

        /// <summary>
        /// True if we're in file-access mode, false if raw-access mode.
        /// </summary>
        private bool IsPreppedForFileAccess { get { return mVolDirEntry != IFileEntry.NO_ENTRY; } }


        /// <summary>
        /// Volume directory header contents, from block 2.
        /// </summary>
        internal class VolDirHeader {
            public const int LENGTH = Pascal_FileEntry.DIR_ENTRY_LEN;

            public ushort mFirstBlock;
            public ushort mNextBlock;
            public ushort mFileType;
            public byte[] mVolumeName = new byte[MAX_VOL_NAME_LEN + 1];
            public ushort mVolBlockCount;
            public ushort mFileCount;
            public ushort mLastAccess;
            public ushort mLastDateSet;
            public uint mReserved;

            public void Load(byte[] buf, int offset) {
                int startOffset = offset;
                mFirstBlock = RawData.ReadU16LE(buf, ref offset);
                mNextBlock = RawData.ReadU16LE(buf, ref offset);
                mFileType = RawData.ReadU16LE(buf, ref offset);
                for (int i = 0; i < mVolumeName.Length; i++) {
                    mVolumeName[i] = buf[offset++];
                }
                mVolBlockCount = RawData.ReadU16LE(buf, ref offset);
                mFileCount = RawData.ReadU16LE(buf, ref offset);
                mLastAccess = RawData.ReadU16LE(buf, ref offset);
                mLastDateSet = RawData.ReadU16LE(buf, ref offset);
                mReserved = RawData.ReadU32LE(buf, ref offset);
                Debug.Assert(offset - startOffset == LENGTH);
            }
            public void Store(byte[] buf, int offset) {
                int startOffset = offset;
                RawData.WriteU16LE(buf, ref offset, mFirstBlock);
                RawData.WriteU16LE(buf, ref offset, mNextBlock);
                RawData.WriteU16LE(buf, ref offset, mFileType);
                for (int i = 0; i < mVolumeName.Length; i++) {
                    buf[offset++] = mVolumeName[i];
                }
                RawData.WriteU16LE(buf, ref offset, mVolBlockCount);
                RawData.WriteU16LE(buf, ref offset, mFileCount);
                RawData.WriteU16LE(buf, ref offset, mLastAccess);
                RawData.WriteU16LE(buf, ref offset, mLastDateSet);
                RawData.WriteU32LE(buf, ref offset, mReserved);
                Debug.Assert(offset - startOffset == LENGTH);
            }
        }

        // Delegate: test image to see if it's ours.
        public static TestResult TestImage(IChunkAccess chunks, AppHook appHook) {
            if (!chunks.HasBlocks) {
                return TestResult.No;
            }
            if (!IsSizeAllowed(chunks.FormattedLength)) {
                return TestResult.No;
            }

            byte[] dataBuf = new byte[BLOCK_SIZE];
            chunks.ReadBlock(VOL_DIR_START_BLOCK, dataBuf, 0);
            VolDirHeader hdr = new VolDirHeader();
            hdr.Load(dataBuf, 0);

            if (ValidateVolDirHeader(hdr, chunks.FormattedLength / BLOCK_SIZE)) {
                return TestResult.Yes;
            } else {
                return TestResult.No;
            }
        }

        /// <summary>
        /// Validates the contents of the volume directory header.
        /// </summary>
        /// <returns>True if all is well.</returns>
        private static bool ValidateVolDirHeader(VolDirHeader hdr, long chunkBlocks) {
            const int EXPECTED_FIRST_DATA = 6;
            const int MAX_FILE_COUNT = 77;      // floor(2048/26) - 1

            // Validate values.  We could allow the possibility of a volume with a nonstandard
            // directory length, but as far as I know such a thing has never been created.
            if (hdr.mFirstBlock != 0 || hdr.mNextBlock != EXPECTED_FIRST_DATA ||
                    hdr.mFileType != 0 ||
                    hdr.mVolumeName[0] == 0 || hdr.mVolumeName[0] > MAX_VOL_NAME_LEN ||
                    hdr.mVolBlockCount < EXPECTED_FIRST_DATA || hdr.mVolBlockCount > chunkBlocks ||
                    hdr.mFileCount > MAX_FILE_COUNT) {
                return false;
            }

            // Do a quick scan on the volume name.
            for (int i = 0; i < hdr.mVolumeName[0]; i++) {
                byte bch = hdr.mVolumeName[i + 1];
                if (bch <= 0x20 || bch >= 0x7f) {
                    return false;   // fail if we see spaces or control chars
                }
            }

            return true;
        }

        // Delegate: returns true if the size (in bytes) is valid for this filesystem.
        public static bool IsSizeAllowed(long size) {
            if (size % BLOCK_SIZE != 0) {
                return false;       // must be blocks
            }
            if (size == MAX_VOL_SIZE + BLOCK_SIZE) {
                // Not really expecting a 32MB Pascal volume, but somebody might try it.
                // Allow.
            } else if (size < MIN_VOL_SIZE || size > MAX_VOL_SIZE) {
                return false;
            }
            return true;
        }

        public Pascal(IChunkAccess chunks, AppHook appHook) {
            Debug.Assert(chunks.HasBlocks);
            ChunkAccess = chunks;
            AppHook = appHook;

            RawAccess = new GatedChunkAccess(chunks);
            mVolDirHeader = new VolDirHeader();
            mVolDirEntry = IFileEntry.NO_ENTRY;
        }

        public override string ToString() {
            string id = mVolDirEntry == IFileEntry.NO_ENTRY ? "(raw)" : mVolDirEntry.FileName;
            return "[Pascal vol '" + id + "']";
        }

        // IDisposable generic finalizer.
        ~Pascal() {
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
                AppHook.LogW("Attempting to dispose of Pascal object twice");
                return;
            }
            if (!disposing) {
                // This is a GC finalization.  We can't know if the objects we have references
                // to have already been finalized, so all we can do is complain.
                AppHook.LogW("GC disposing of filesystem object " + this);
                if (mOpenFiles.Count != 0) {
                    AppHook.LogW("Pascal FS finalized while " + mOpenFiles.Count + " files open");
                }
                return;
            }

            string id = mVolDirEntry == IFileEntry.NO_ENTRY ? "(raw)" : mVolDirEntry.FileName;
            AppHook.LogD("Pascal.Dispose(" + disposing + "): " + id);

            // This can happen easily if we have the filesystem in a "using" block and
            // something throws with a file open.  Post a warning and close all files.
            if (mOpenFiles.Count != 0) {
                AppHook.LogI("Pascal FS disposed with " + mOpenFiles.Count +" files open; closing");
                CloseAll();
            }

            try {
                FlushVolumeDir();
            } catch {
                AppHook.LogE("Failed while attempting to flush volume directory");
            }

            if (mVolDirEntry != IFileEntry.NO_ENTRY) {
                // Invalidate all associated file entry objects.
                InvalidateFileEntries();
            }

            RawAccess.AccessLevel = GatedChunkAccess.AccessLvl.Closed;
            mDisposed = true;
        }

        // IFileSystem
        public void Flush() {
            mOpenFiles.FlushAll();
            FlushVolumeDir();
        }

        /// <summary>
        /// Flushes the contents of the volume directory, if they have been changed.
        /// </summary>
        internal void FlushVolumeDir() {
            if (!IsVolDirDirty) {
                return;
            }
            Debug.Assert(IsPreppedForFileAccess);

            // We don't try to preserve the initial state of the directory blocks.  This means
            // we will purge the entries of previously-deleted files.  If we decided to be less
            // heavy-handed, we will need to make sure that the filename length of the entries
            // past the end are zeroed.
            byte[] buf = mDirOutBuffer;
            Array.Clear(buf);

            // Output the volume header first.  Copy any changes out of the volume dir object.
            mVolDirHeader.mLastDateSet = TimeStamp.ConvertDateTime_Pascal(mVolDirEntry.ModWhen);
            byte[] rawName = mVolDirEntry.RawFileName;
            mVolDirHeader.mVolumeName[0] = (byte)rawName.Length;
            for (int i = 0; i < rawName.Length; i++) {
                mVolDirHeader.mVolumeName[i + 1] = rawName[i];
            }
            int offset = 0;
            mVolDirHeader.Store(buf, offset);
            offset += VolDirHeader.LENGTH;

            // Output the file entries.
            Pascal_FileEntry volDir = (Pascal_FileEntry)mVolDirEntry;
            foreach (Pascal_FileEntry entry in volDir.ChildList) {
                entry.StoreEntry(buf, ref offset);
            }

            // Write the data to disk.
            offset = 0;
            for (uint block = VOL_DIR_START_BLOCK; block < mVolDirHeader.mNextBlock; block++) {
                ChunkAccess.WriteBlock(block, buf, offset);
                offset += BLOCK_SIZE;
            }

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
                ScanVolume();
                int numDirBlocks = mVolDirHeader.mNextBlock - VOL_DIR_START_BLOCK;
                mDirOutBuffer = new byte[numDirBlocks * BLOCK_SIZE];
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

            FlushVolumeDir();
            if (mVolDirEntry != IFileEntry.NO_ENTRY) {
                // Invalidate the FileEntry tree.  If we don't do this the application could
                // try to use a retained object after it was switched back to file access.
                InvalidateFileEntries();
            }

            mVolDirEntry = IFileEntry.NO_ENTRY;
            mDirOutBuffer = RawData.EMPTY_BYTE_ARRAY;
            VolUsage = null;
            IsDubious = false;
            RawAccess.AccessLevel = GatedChunkAccess.AccessLvl.Open;
        }

        /// <summary>
        /// Marks all file entry objects as invalid.
        /// </summary>
        private void InvalidateFileEntries() {
            Debug.Assert(mVolDirEntry != IFileEntry.NO_ENTRY);
            Pascal_FileEntry volDir = (Pascal_FileEntry)mVolDirEntry;
            if (!volDir.IsValid) {
                // Already done?  Shouldn't happen.
                return;
            }
            foreach (IFileEntry child in volDir) {
                Pascal_FileEntry entry = (Pascal_FileEntry)child;
                entry.Invalidate();
            }
            volDir.Invalidate();
        }

        /// <summary>
        /// Scans the contents of the volume directory.
        /// </summary>
        /// <exception cref="IOException">Disk access failure.</exception>
        /// <exception cref="DAException">Invalid filesystem.</exception>
        private void ScanVolume() {
            byte[] blockBuf = new byte[BLOCK_SIZE];
            ChunkAccess.ReadBlock(VOL_DIR_START_BLOCK, blockBuf, 0);
            mVolDirHeader.Load(blockBuf, 0);
            if (!ValidateVolDirHeader(mVolDirHeader, ChunkAccess.FormattedLength / BLOCK_SIZE)) {
                throw new DAException("Invalid volume directory header");
            }

            // Create volume usage map.  Assign "system" usage to the boot and directory blocks.
            VolUsage = new VolumeUsage(mVolDirHeader.mVolBlockCount);
            for (uint block = 0; block < mVolDirHeader.mNextBlock; block++) {
                VolUsage.MarkInUse(block);
                VolUsage.SetUsage(block, IFileEntry.NO_ENTRY);
            }

            // Scan the full catalog.
            mVolDirEntry = Pascal_FileEntry.ScanDirectory(this, mVolDirHeader);

            // Check the results of the volume usage scan for problems.
            VolUsage.Analyze(out int markedUsed, out int unusedMarked,
                    out int notMarkedUsed, out int conflicts);

            AppHook.LogI("Usage counts: " + markedUsed + " in use, " +
                unusedMarked + " unused but marked, " +
                notMarkedUsed + " used but not marked, " +
                conflicts + " conflicts");

            // There's no volume bitmap, so certain things aren't possible.
            Debug.Assert(unusedMarked == 0);
            Debug.Assert(notMarkedUsed == 0);

            if (conflicts != 0) {
                Notes.AddW("Found " + conflicts + " blocks in use by more than one file");
            }

            //Debug.WriteLine(VolUsage.DebugDump());
        }

        /// <summary>
        /// Calculates the total number of free blocks.
        /// </summary>
        private int CalcFreeBlocks() {
            if (!IsPreppedForFileAccess) {
                return -1;
            }
            int freeBlocks = 0;
            int nextStart = mVolDirHeader.mNextBlock;
            foreach (IFileEntry ientry in mVolDirEntry) {
                Pascal_FileEntry entry = (Pascal_FileEntry)ientry;
                freeBlocks += entry.StartBlock - nextStart;
                nextStart = entry.NextBlock;
            }
            freeBlocks += mVolDirHeader.mVolBlockCount - nextStart;
            return freeBlocks;
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
            if (ChunkAccess.FormattedLength < MIN_VOL_SIZE ||
                    ChunkAccess.FormattedLength > MAX_VOL_SIZE + BLOCK_SIZE) {  // allow 1 over
                throw new ArgumentOutOfRangeException("total length");
            }
            long formatBlockCount = ChunkAccess.FormattedLength / BLOCK_SIZE;
            if (formatBlockCount == 65536) {
                formatBlockCount = 65535;
            }

            // Validate volume name.  We don't care about volumeNum.
            if (!Pascal_FileEntry.IsVolumeNameValid(volumeName)) {
                throw new ArgumentException("Invalid volume name");
            }
            volumeName = volumeName.ToUpperInvariant();

            byte[] blockBuf = new byte[BLOCK_SIZE];

            // Write the standard boot block data to block 0/1 (we ignore the "make bootable"
            // flag).  If the chunks are from a source with room for 35 tracks * 16 sectors, use
            // the 5.25" boot blocks; otherwise we use the 3.5" boot block.
            if (ChunkAccess.FormattedLength == 140 * 1024) {
                ChunkAccess.WriteBlock(0, sBoot525Block0, 0);
                ChunkAccess.WriteBlock(1, sBoot525Block1, 0);
            } else {
                ChunkAccess.WriteBlock(0, sBoot35Block0, 0);
                ChunkAccess.WriteBlock(1, blockBuf, 0);         // zeroes
            }

            // Create the directory by filling out the volume header.
            VolDirHeader hdr = new VolDirHeader();
            hdr.mFirstBlock = 0;
            hdr.mNextBlock = 6;
            hdr.mFileType = 0;
            ASCIIUtil.StringToFixedPascalBytes(volumeName, hdr.mVolumeName);
            hdr.mVolBlockCount = (ushort)formatBlockCount;
            hdr.mFileCount = 0;
            hdr.mLastAccess = 0;
            hdr.mLastDateSet = TimeStamp.ConvertDateTime_Pascal(DateTime.Now);
            hdr.mReserved = 0;
            hdr.Store(blockBuf, 0);
            ChunkAccess.WriteBlock(VOL_DIR_START_BLOCK, blockBuf, 0);

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
            Pascal_FileEntry? entry = ientry as Pascal_FileEntry;
            if (entry == null || entry.FileSystem != this) {
                if (entry != null && entry.FileSystem == null) {
                    // Invalid entry; could be a deleted file, or from before a raw-mode switch.
                    throw new IOException("File entry is invalid");
                } else {
                    throw new FileNotFoundException("File entry is not part of this filesystem");
                }
            }
            if (part == FilePart.RsrcFork) {
                throw new IOException("File does not have a resource fork");
            }
            if (!mOpenFiles.CheckOpenConflict(entry, wantWrite, FilePart.Unknown)) {
                throw new IOException("File is already open; cannot " + op);
            }
        }

        // IFileSystem
        public IFileEntry GetVolDirEntry() {
            if (!IsPreppedForFileAccess) {
                throw new IOException("Filesystem object not prepared for file access");
            }
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
            if (part != FilePart.DataFork) {
                throw new ArgumentException("Requested file part not found");
            }

            Pascal_FileEntry entry = (Pascal_FileEntry)ientry;
            Pascal_FileDesc pfd = Pascal_FileDesc.CreateFD(entry, mode, part, false);
            mOpenFiles.Add(this, entry, pfd);
            return pfd;
        }

        /// <summary>
        /// Closes a file, removing it from our list.  Do not call this directly -- this is
        /// called from the file descriptor Dispose() call.
        /// </summary>
        /// <param name="ifd">Descriptor to close.</param>
        /// <exception cref="IOException">File descriptor was already closed, or was opened
        ///   by a different filesystem.</exception>
        internal void CloseFile(DiskFileStream ifd) {
            Pascal_FileDesc fd = (Pascal_FileDesc)ifd;
            if (fd.FileSystem != this) {
                // Should be impossible, though it could be null if previous close invalidated it.
                if (fd.FileSystem == null) {
                    throw new IOException("Invalid file descriptor");
                } else {
                    throw new IOException("File descriptor was opened by a different filesystem");
                }
            }

            // Find the file record, searching by descriptor.
            if (!mOpenFiles.RemoveDescriptor(ifd)) {
                throw new IOException("Open file record not found: " + fd);
            }

            // Take the opportunity to flush the volume directory, in case the file was modified.
            FlushVolumeDir();
        }

        // IFileSystem
        public void CloseAll() {
            mOpenFiles.CloseAll();
        }

        // IFileSystem
        public IFileEntry CreateFile(IFileEntry idirEntry, string fileName, CreateMode mode) {
            CheckFileAccess("create", idirEntry, true, FilePart.Unknown);
            Debug.Assert(idirEntry == mVolDirEntry);
            if (mode != CreateMode.File) {
                throw new ArgumentException("Invalid Pascal creation mode: " + mode);
            }
            Pascal_FileEntry dirEntry = (Pascal_FileEntry)idirEntry;
            if (fileName == null || !Pascal_FileEntry.IsFileNameValid(fileName)) {
                throw new ArgumentException("Invalid filename '" + fileName + "'");
            }

            // Check for an entry with a duplicate filename in the list of children.
            foreach (IFileEntry entry in dirEntry) {
                if (entry.CompareFileName(fileName) == 0) {
                    throw new IOException("A file with that name already exists");
                }
            }

            // Make sure there's room.
            int numDirBlocks = mVolDirHeader.mNextBlock - VOL_DIR_START_BLOCK;
            int maxFiles = (numDirBlocks * BLOCK_SIZE) / Pascal_FileEntry.DIR_ENTRY_LEN - 1;
            if (dirEntry.Count >= maxFiles) {
                throw new DiskFullException("Volume directory is full");
            }

            // We don't know how big this file will become.  Our best option is to create it
            // in the largest open space.
            if (!FindLargestFreeArea(out int gapIndex, out ushort gapStart,
                    out ushort gapBlockCount)) {
                throw new DiskFullException("Disk full");
            }
            Debug.Assert(gapStart < ushort.MaxValue);

            // Create a new file entry.  We allocate one block, with the bytes-used zeroed.
            // Note the Pascal system will delete such files automatically.  (Filer-created
            // files will have 1 block, with all 512 bytes used.)
            Pascal_FileEntry newEntry = Pascal_FileEntry.CreateEntry(this, mVolDirEntry, gapStart,
                (ushort)(gapStart + 1), Pascal_FileEntry.FileKind.DataFile, fileName, DateTime.Now);
            VolUsage!.AllocChunk(gapStart, newEntry);

            // Insert it in the list of children, and update the file count.
            dirEntry.ChildList.Insert(gapIndex, newEntry);
            mVolDirHeader.mFileCount++;

            // Save the updated directory.
            IsVolDirDirty = true;
            FlushVolumeDir();
            return newEntry;
        }

        /// <summary>
        /// Finds the largest contiguous collection of free blocks on the disk.
        /// </summary>
        /// <param name="gapIndex">Result: index of entry that comes after the gap.  This is
        ///   an index into the list of children, so the first file entry is 0.</param>
        /// <param name="gapStart">Result: block number of the start of the gap.</param>
        /// <param name="gapBlockCount">Result: number of blocks in the gap.</param>
        /// <returns>True if a gap was found, false if the disk is full.</returns>
        private bool FindLargestFreeArea(out int gapIndex, out ushort gapStart,
                out ushort gapBlockCount) {
            gapIndex = -1;
            gapStart = ushort.MaxValue;
            gapBlockCount = 0;
            ushort prevNext = mVolDirHeader.mNextBlock;

            Pascal_FileEntry volDir = (Pascal_FileEntry)mVolDirEntry;
            for (int i = 0; i < mVolDirEntry.Count; i++) {
                Pascal_FileEntry entry = (Pascal_FileEntry)volDir.ChildList[i];
                Debug.Assert(entry.StartBlock >= prevNext);     // should not be writable otherwise
                uint gapSize = (uint)entry.StartBlock - prevNext;
                if (gapSize > gapBlockCount) {
                    gapIndex = i;
                    gapStart = prevNext;
                    gapBlockCount = (ushort)gapSize;
                }
                prevNext = entry.NextBlock;
            }

            uint endSize = (uint)mVolDirHeader.mVolBlockCount - prevNext;
            if (endSize > gapBlockCount) {
                gapIndex = mVolDirEntry.Count;
                gapStart = prevNext;
                gapBlockCount = (ushort)endSize;
            }
            return (gapBlockCount != 0);
        }

        /// <summary>
        /// Determines the maximum allowed value for the "next block" field for a file entry.
        /// </summary>
        /// <param name="entry">File entry to examine.</param>
        /// <returns>Maximum next value (which will be equal to the current next value if the
        ///   file has run out of room).</returns>
        internal ushort GetMaxNext(Pascal_FileEntry entry) {
            Pascal_FileEntry volDir = (Pascal_FileEntry)mVolDirEntry;
            // This is a linear search.  We could do a faster binary search by StartBlock.
            int index = volDir.ChildList.IndexOf(entry);
            if (index < 0) {
                throw new DAException("Unable to find entry for open file");
            }
            ushort nextNext;
            if (index == volDir.ChildList.Count - 1) {
                // We're the last entry, so we can continue to the end of the volume.
                nextNext = mVolDirHeader.mVolBlockCount;
            } else {
                // We can only go until the start block of the next entry.
                nextNext = ((Pascal_FileEntry)volDir.ChildList[index + 1]).StartBlock;
            }
            return nextNext;
        }

        // IFileSystem
        public void AddRsrcFork(IFileEntry entry) {
            throw new IOException("Filesystem does not support resource forks");
        }

        // IFileSystem
        public void MoveFile(IFileEntry ientry, IFileEntry destDir, string newFileName) {
            CheckFileAccess("move", ientry, true, FilePart.Unknown);
            if (destDir != mVolDirEntry) {
                throw new IOException("Destination directory is invalid");
            }

            // Just a rename.
            ientry.FileName = newFileName;
            ientry.SaveChanges();
        }

        // IFileSystem
        public void DeleteFile(IFileEntry ientry) {
            CheckFileAccess("delete", ientry, true, FilePart.Unknown);
            if (ientry == mVolDirEntry) {
                throw new IOException("Can't delete volume directory");
            }
            Pascal_FileEntry entry = (Pascal_FileEntry)ientry;

            // Clear storage from volume usage tracker.
            for (uint block = entry.StartBlock; block < entry.NextBlock; block++) {
                VolUsage!.FreeChunk(block);
            }

            ((Pascal_FileEntry)mVolDirEntry).ChildList.Remove(entry);
            mVolDirHeader.mFileCount--;

            // This entry may no longer be used.
            entry.Invalidate();

            // Save the updated directory.
            IsVolDirDirty = true;
            FlushVolumeDir();
        }

        /// <summary>
        /// Defragments ("krunches") the filesystem.
        /// </summary>
        /// <remarks>
        /// <para>The filesystem must be in raw-access mode.</para>
        /// <para>The process will not be started if the filesystem has any errors or dubious
        /// files, or any .BAD files (by type, not extension).</para>
        /// </remarks>
        /// <returns>True if the drive was defragmented, false if something (such as bad blocks)
        ///   prevented the operation from starting.</returns>
        /// <exception cref="DAException">Unable to check filesystem for errors.</exception>
        public bool Defragment() {
            if (ChunkAccess.IsReadOnly) {
                throw new IOException("Can't format read-only data");
            }
            if (IsPreppedForFileAccess) {
                throw new IOException("Must be in raw access mode");
            }

            // Check the filesystem.
            PrepareFileAccess(true);
            if (IsDubious) {
                Debug.WriteLine("Filesystem is dubious");
                return false;
            }
            foreach (IFileEntry entry in mVolDirEntry) {
                if (entry.IsDubious || entry.IsDamaged) {
                    Debug.WriteLine("Found dubious/damaged file");
                    return false;
                }
                if (entry.FileType == FileAttribs.FILE_TYPE_BAD) {
                    Debug.WriteLine("Found bad-blocks file");
                    return false;
                }
            }
            if (Notes.WarningCount != 0 || Notes.ErrorCount != 0) {
                Debug.WriteLine("Found notes, being paranoid");
                return false;
            }

            // Grab the directory block and file counts while we have it open.
            int numDirBlocks = mVolDirHeader.mNextBlock - VOL_DIR_START_BLOCK;
            int numFiles = mVolDirHeader.mFileCount;
            ushort volBlockCount = mVolDirHeader.mVolBlockCount;
            ushort prevNextBlock = mVolDirHeader.mFirstBlock;
            Debug.Assert(numDirBlocks == 4);       // remove this
            byte[] dirBuf = new byte[numDirBlocks * BLOCK_SIZE];

            PrepareRawAccess();
            if (numFiles == 0) {
                Debug.WriteLine("Empty disk, nothing to do");
                return true;
            }

            // Do a bad block scan.  This will throw if an I/O error is encountered.
            for (uint blk = 0; blk < volBlockCount; blk++) {
                ChunkAccess.ReadBlock(blk, dirBuf, 0);
            }
            Debug.WriteLine("Looks clean, starting defrag");

            // Read the directory into memory.
            for (int i = 0; i < numDirBlocks; i++) {
                ChunkAccess.ReadBlock(VOL_DIR_START_BLOCK + (uint)i, dirBuf, BLOCK_SIZE * i);
            }

            // We start with the vol dir entry, so scan numFiles+1.
            int dirOffset = 0;
            for (int i = 0; i <= numFiles; i++) {
                string debugName = ASCIIUtil.PascalBytesToString(dirBuf, dirOffset + 0x06);
                ushort startBlock = RawData.GetU16LE(dirBuf, dirOffset);
                ushort nextBlock = RawData.GetU16LE(dirBuf, dirOffset + 2);

                if (startBlock != prevNextBlock) {
                    int shift = startBlock - prevNextBlock;
                    Debug.Assert(shift > 0);
                    Debug.WriteLine("'" + debugName + "': shift: " + startBlock + "-" + nextBlock +
                        " -> " + prevNextBlock);

                    CopyBlocks(ChunkAccess, startBlock, prevNextBlock, nextBlock - startBlock);
                    RawData.SetU16LE(dirBuf, dirOffset, (ushort)(startBlock - shift));
                    RawData.SetU16LE(dirBuf, dirOffset + 2, (ushort)(nextBlock - shift));

                    prevNextBlock = (ushort)(nextBlock - shift);
                } else {
                    Debug.WriteLine("'" + debugName + "': no shift");
                    prevNextBlock = nextBlock;
                }

                dirOffset += Pascal_FileEntry.DIR_ENTRY_LEN;
            }

            // Write the directory back to disk.
            //
            // NOTE: this would be safer but slower if we saved the directory after every
            // shift.  However, "safer" just means that we'd only trash one file instead of
            // multiple files, so we haven't really solved the problem.  Another risk-reducer
            // would be to clone the volume during the bad-block check, do the defragment run
            // on the in-memory copy, and then copy the entire thing out at the end.  I think
            // the filesystem structure is simple enough, and has been scanned carefully enough,
            // that these steps are unnecessary.
            for (int i = 0; i < numDirBlocks; i++) {
                ChunkAccess.WriteBlock(VOL_DIR_START_BLOCK + (uint)i, dirBuf, BLOCK_SIZE * i);
            }
            return true;
        }

        /// <summary>
        /// Copies a range of blocks from one part of the disk to another.  The source and
        /// destination ranges may be overlapping.
        /// </summary>
        /// <param name="chunkAccess">Chunk data object.</param>
        /// <param name="srcBlock">Starting block number for the source range.</param>
        /// <param name="dstBlock">Starting block number for the destination range.</param>
        /// <param name="count">Number of blocks to copy.</param>
        private static void CopyBlocks(IChunkAccess chunkAccess, uint srcBlock, uint dstBlock,
                int count) {
            Debug.Assert(count > 0);
            byte[] blockBuf = new byte[BLOCK_SIZE];
            if (srcBlock > dstBlock) {
                // Moving blocks toward start of volume.  Start from the start.
                for (uint i = 0; i < count; i++) {
                    chunkAccess.ReadBlock(srcBlock + i, blockBuf, 0);
                    chunkAccess.WriteBlock(dstBlock + i, blockBuf, 0);
                }
            } else if (srcBlock < dstBlock) {
                // Moving blocks toward end of volume.  Start from the end.
                throw new NotImplementedException();
            } else {
                Debug.Assert(false, "no-op");
            }
        }

        #region Miscellaneous

        /// <summary>
        /// Blocks 0 and 1 of a 5.25" bootable Pascal disk, formatted by APPLE3:FORMATTER
        /// from Pascal v1.3.
        /// </summary>
        private static readonly byte[] sBoot525Block0 = {
            0x01, 0xe0, 0x70, 0xb0, 0x04, 0xe0, 0x40, 0xb0, 0x39, 0xbd, 0x88, 0xc0,
            0x20, 0x20, 0x08, 0xa2, 0x00, 0xbd, 0x25, 0x08, 0x09, 0x80, 0x20, 0xfd,
            0xfb, 0xe8, 0xe0, 0x1d, 0xd0, 0xf3, 0xf0, 0xfe, 0xa9, 0x0a, 0x4c, 0x24,
            0xfc, 0x4d, 0x55, 0x53, 0x54, 0x20, 0x42, 0x4f, 0x4f, 0x54, 0x20, 0x46,
            0x52, 0x4f, 0x4d, 0x20, 0x53, 0x4c, 0x4f, 0x54, 0x20, 0x34, 0x2c, 0x20,
            0x35, 0x20, 0x4f, 0x52, 0x20, 0x36, 0x8a, 0x85, 0x43, 0x4a, 0x4a, 0x4a,
            0x4a, 0x09, 0xc0, 0x85, 0x0d, 0xa9, 0x5c, 0x85, 0x0c, 0xad, 0x00, 0x08,
            0xc9, 0x06, 0xb0, 0x0a, 0x69, 0x02, 0x8d, 0x00, 0x08, 0xe6, 0x3d, 0x6c,
            0x0c, 0x00, 0xa9, 0x00, 0x8d, 0x78, 0x04, 0xa9, 0x0a, 0x85, 0x0e, 0xa9,
            0x80, 0x85, 0x3f, 0x85, 0x11, 0xa9, 0x00, 0x85, 0x10, 0xa9, 0x08, 0x85,
            0x02, 0xa9, 0x02, 0x85, 0x0f, 0xa9, 0x00, 0x20, 0x4c, 0x09, 0xa2, 0x4e,
            0xa0, 0x06, 0xb1, 0x10, 0xd9, 0x39, 0x09, 0xf0, 0x2b, 0x18, 0xa5, 0x10,
            0x69, 0x1a, 0x85, 0x10, 0x90, 0x02, 0xe6, 0x11, 0xca, 0xd0, 0xe9, 0xc6,
            0x0e, 0xd0, 0xcc, 0x20, 0x20, 0x08, 0xa6, 0x43, 0xbd, 0x88, 0xc0, 0xa2,
            0x00, 0xbd, 0x2a, 0x09, 0x09, 0x80, 0x20, 0xfd, 0xfb, 0xe8, 0xe0, 0x15,
            0xd0, 0xf3, 0xf0, 0xfe, 0xc8, 0xc0, 0x13, 0xd0, 0xc9, 0xad, 0x81, 0xc0,
            0xad, 0x81, 0xc0, 0xa9, 0xd0, 0x85, 0x3f, 0xa9, 0x30, 0x85, 0x02, 0xa0,
            0x00, 0xb1, 0x10, 0x85, 0x0f, 0xc8, 0xb1, 0x10, 0x20, 0x4c, 0x09, 0xad,
            0x89, 0xc0, 0xa9, 0xd0, 0x85, 0x3f, 0xa9, 0x10, 0x85, 0x02, 0xa0, 0x00,
            0xb1, 0x10, 0x18, 0x69, 0x18, 0x85, 0x0f, 0xc8, 0xb1, 0x10, 0x69, 0x00,
            0x20, 0x4c, 0x09, 0xa5, 0x43, 0xc9, 0x50, 0xf0, 0x08, 0x90, 0x1a, 0xad,
            0x80, 0xc0, 0x6c, 0xf8, 0xff, 0xa2, 0x00, 0x8e, 0xc4, 0xfe, 0xe8, 0x8e,
            0xc6, 0xfe, 0xe8, 0x8e, 0xb6, 0xfe, 0xe8, 0x8e, 0xb8, 0xfe, 0x4c, 0xfb,
            0x08, 0xa2, 0x00, 0x8e, 0xc0, 0xfe, 0xe8, 0x8e, 0xc2, 0xfe, 0xa2, 0x04,
            0x8e, 0xb6, 0xfe, 0xe8, 0x8e, 0xb8, 0xfe, 0x4c, 0xfb, 0x08, 0x4e, 0x4f,
            0x20, 0x46, 0x49, 0x4c, 0x45, 0x20, 0x53, 0x59, 0x53, 0x54, 0x45, 0x4d,
            0x2e, 0x41, 0x50, 0x50, 0x4c, 0x45, 0x20, 0x0c, 0x53, 0x59, 0x53, 0x54,
            0x45, 0x4d, 0x2e, 0x41, 0x50, 0x50, 0x4c, 0x45, 0x4a, 0x08, 0xa5, 0x0f,
            0x29, 0x07, 0x0a, 0x85, 0x00, 0xa5, 0x0f, 0x28, 0x6a, 0x4a, 0x4a, 0x85,
            0xf0, 0xa9, 0x00, 0x85, 0x3e, 0x4c, 0x78, 0x09, 0xa6, 0x02, 0xf0, 0x22,
            0xc6, 0x02, 0xe6, 0x3f, 0xe6, 0x00, 0xa5, 0x00, 0x49, 0x10, 0xd0, 0x04,
            0x85, 0x00, 0xe6, 0xf0, 0xa4, 0x00, 0xb9, 0x8b, 0x09, 0x85, 0xf1, 0xa2,
            0x00, 0xe4, 0x02, 0xf0, 0x05, 0x20, 0x9b, 0x09, 0x90, 0xda, 0x60, 0x00,
            0x02, 0x04, 0x06, 0x08, 0x0a, 0x0c, 0x0e, 0x01, 0x03, 0x05, 0x07, 0x09,
            0x0b, 0x0d, 0x0f, 0xa6, 0x43, 0xa5, 0xf0, 0x0a, 0x0e, 0x78, 0x04, 0x20,
            0xa3, 0x0a, 0x4e, 0x78, 0x04, 0x20, 0x47, 0x0a, 0xb0, 0xfb, 0xa4, 0x2e,
            0x8c, 0x78, 0x04, 0xc4, 0xf0, 0xd0, 0xe6, 0xa5, 0x2d, 0xc5, 0xf1, 0xd0,
            0xec, 0x20, 0xdf, 0x09, 0xb0, 0xe7, 0x20, 0xc7, 0x09, 0x18, 0x60, 0xa0,
            0x00, 0xa2, 0x56, 0xca, 0x30, 0xfb, 0xb9, 0x00, 0x02, 0x5e, 0x00, 0x03,
            0x2a, 0x5e, 0x00, 0x03, 0x2a, 0x91, 0x3e, 0xc8, 0xd0, 0xed, 0x60, 0xa0,
            0x20, 0x88, 0xf0, 0x61, 0xbd, 0x8c, 0xc0, 0x10, 0xfb, 0x49, 0xd5, 0xd0,
            0xf4, 0xea, 0xbd, 0x8c, 0xc0, 0x10, 0xfb, 0xc9, 0xaa, 0xd0, 0xf2, 0xa0,
            0x56, 0xbd, 0x8c, 0xc0, 0x10, 0xfb, 0xc9, 0xad
        };
        private static readonly byte[] sBoot525Block1 = {
            0xd0, 0xe7, 0xa9, 0x00, 0x88, 0x84, 0x26, 0xbc, 0x8c, 0xc0, 0x10, 0xfb,
            0x59, 0xd6, 0x02, 0xa4, 0x26, 0x99, 0x00, 0x03, 0xd0, 0xee, 0x84, 0x26,
            0xbc, 0x8c, 0xc0, 0x10, 0xfb, 0x59, 0xd6, 0x02, 0xa4, 0x26, 0x99, 0x00,
            0x02, 0xc8, 0xd0, 0xee, 0xbc, 0x8c, 0xc0, 0x10, 0xfb, 0xd9, 0xd6, 0x02,
            0xd0, 0x13, 0xbd, 0x8c, 0xc0, 0x10, 0xfb, 0xc9, 0xde, 0xd0, 0x0a, 0xea,
            0xbd, 0x8c, 0xc0, 0x10, 0xfb, 0xc9, 0xaa, 0xf0, 0x5c, 0x38, 0x60, 0xa0,
            0xfc, 0x84, 0x26, 0xc8, 0xd0, 0x04, 0xe6, 0x26, 0xf0, 0xf3, 0xbd, 0x8c,
            0xc0, 0x10, 0xfb, 0xc9, 0xd5, 0xd0, 0xf0, 0xea, 0xbd, 0x8c, 0xc0, 0x10,
            0xfb, 0xc9, 0xaa, 0xd0, 0xf2, 0xa0, 0x03, 0xbd, 0x8c, 0xc0, 0x10, 0xfb,
            0xc9, 0x96, 0xd0, 0xe7, 0xa9, 0x00, 0x85, 0x27, 0xbd, 0x8c, 0xc0, 0x10,
            0xfb, 0x2a, 0x85, 0x26, 0xbd, 0x8c, 0xc0, 0x10, 0xfb, 0x25, 0x26, 0x99,
            0x2c, 0x00, 0x45, 0x27, 0x88, 0x10, 0xe7, 0xa8, 0xd0, 0xb7, 0xbd, 0x8c,
            0xc0, 0x10, 0xfb, 0xc9, 0xde, 0xd0, 0xae, 0xea, 0xbd, 0x8c, 0xc0, 0x10,
            0xfb, 0xc9, 0xaa, 0xd0, 0xa4, 0x18, 0x60, 0x86, 0x2b, 0x85, 0x2a, 0xcd,
            0x78, 0x04, 0xf0, 0x48, 0xa9, 0x00, 0x85, 0x26, 0xad, 0x78, 0x04, 0x85,
            0x27, 0x38, 0xe5, 0x2a, 0xf0, 0x37, 0xb0, 0x07, 0x49, 0xff, 0xee, 0x78,
            0x04, 0x90, 0x05, 0x69, 0xfe, 0xce, 0x78, 0x04, 0xc5, 0x26, 0x90, 0x02,
            0xa5, 0x26, 0xc9, 0x0c, 0xb0, 0x01, 0xa8, 0x20, 0xf4, 0x0a, 0xb9, 0x15,
            0x0b, 0x20, 0x04, 0x0b, 0xa5, 0x27, 0x29, 0x03, 0x0a, 0x05, 0x2b, 0xaa,
            0xbd, 0x80, 0xc0, 0xb9, 0x21, 0x0b, 0x20, 0x04, 0x0b, 0xe6, 0x26, 0xd0,
            0xbf, 0x20, 0x04, 0x0b, 0xad, 0x78, 0x04, 0x29, 0x03, 0x0a, 0x05, 0x2b,
            0xaa, 0xbd, 0x81, 0xc0, 0xa6, 0x2b, 0x60, 0xea, 0xa2, 0x11, 0xca, 0xd0,
            0xfd, 0xe6, 0x46, 0xd0, 0x02, 0xe6, 0x47, 0x38, 0xe9, 0x01, 0xd0, 0xf0,
            0x60, 0x01, 0x30, 0x28, 0x24, 0x20, 0x1e, 0x1d, 0x1c, 0x1c, 0x1c, 0x1c,
            0x1c, 0x70, 0x2c, 0x26, 0x22, 0x1f, 0x1e, 0x1d, 0x1c, 0x1c, 0x1c, 0x1c,
            0x1c, 0x20, 0x43, 0x4f, 0x50, 0x59, 0x52, 0x49, 0x47, 0x48, 0x54, 0x20,
            0x41, 0x50, 0x50, 0x4c, 0x45, 0x20, 0x43, 0x4f, 0x4d, 0x50, 0x55, 0x54,
            0x45, 0x52, 0x2c, 0x20, 0x49, 0x4e, 0x43, 0x2e, 0x2c, 0x20, 0x31, 0x39,
            0x38, 0x34, 0x2c, 0x20, 0x31, 0x39, 0x38, 0x35, 0x20, 0x43, 0x2e, 0x4c,
            0x45, 0x55, 0x4e, 0x47, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x68, 0x03, 0x00, 0x00, 0x02, 0x00, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xbb
        };

        /// <summary>
        /// Block 0 of a 3.5" bootable Pascal disk, formatted by APPLE3:FORMATTER from
        /// Pascal v1.3.  Block 1 is zeroed out.
        /// </summary>
        private static readonly byte[] sBoot35Block0 = {
            0x01, 0xe0, 0x70, 0xb0, 0x04, 0xe0, 0x40, 0xb0, 0x39, 0xbd, 0x88, 0xc0,
            0x20, 0x20, 0x08, 0xa2, 0x00, 0xbd, 0x25, 0x08, 0x09, 0x80, 0x20, 0xfd,
            0xfb, 0xe8, 0xe0, 0x1d, 0xd0, 0xf3, 0xf0, 0xfe, 0xa9, 0x0a, 0x4c, 0x24,
            0xfc, 0x4d, 0x55, 0x53, 0x54, 0x20, 0x42, 0x4f, 0x4f, 0x54, 0x20, 0x46,
            0x52, 0x4f, 0x4d, 0x20, 0x53, 0x4c, 0x4f, 0x54, 0x20, 0x34, 0x2c, 0x20,
            0x35, 0x20, 0x4f, 0x52, 0x20, 0x36, 0x8a, 0x85, 0x43, 0x4a, 0x4a, 0x4a,
            0x4a, 0x09, 0xc0, 0x85, 0x15, 0x8d, 0x5d, 0x09, 0xa9, 0x00, 0x8d, 0x78,
            0x04, 0x85, 0x14, 0xa9, 0x0a, 0x85, 0x0e, 0xa9, 0x80, 0x85, 0x13, 0x85,
            0x11, 0xa9, 0x00, 0x85, 0x10, 0x85, 0x0b, 0xa9, 0x02, 0x85, 0x0a, 0xa9,
            0x04, 0x85, 0x02, 0x20, 0x40, 0x09, 0xa2, 0x4e, 0xa0, 0x06, 0xb1, 0x10,
            0xd9, 0x2d, 0x09, 0xf0, 0x2b, 0x18, 0xa5, 0x10, 0x69, 0x1a, 0x85, 0x10,
            0x90, 0x02, 0xe6, 0x11, 0xca, 0xd0, 0xe9, 0xc6, 0x0e, 0xd0, 0xcc, 0x20,
            0x20, 0x08, 0xa6, 0x43, 0xbd, 0x88, 0xc0, 0xa2, 0x00, 0xbd, 0x1e, 0x09,
            0x09, 0x80, 0x20, 0xfd, 0xfb, 0xe8, 0xe0, 0x15, 0xd0, 0xf3, 0xf0, 0xfe,
            0xc8, 0xc0, 0x13, 0xd0, 0xc9, 0xad, 0x83, 0xc0, 0xad, 0x83, 0xc0, 0xa9,
            0xd0, 0x85, 0x13, 0xa0, 0x00, 0xb1, 0x10, 0x85, 0x0a, 0xc8, 0xb1, 0x10,
            0x85, 0x0b, 0xa9, 0x18, 0x85, 0x02, 0x20, 0x40, 0x09, 0xad, 0x8b, 0xc0,
            0xa9, 0xd0, 0x85, 0x13, 0xa0, 0x00, 0xb1, 0x10, 0x18, 0x69, 0x18, 0x85,
            0x0a, 0xc8, 0xb1, 0x10, 0x69, 0x00, 0x85, 0x0b, 0xa9, 0x08, 0x85, 0x02,
            0x20, 0x40, 0x09, 0xa5, 0x43, 0xc9, 0x50, 0xf0, 0x08, 0x90, 0x1a, 0xad,
            0x80, 0xc0, 0x6c, 0xf8, 0xff, 0xa2, 0x00, 0x8e, 0xc4, 0xfe, 0xe8, 0x8e,
            0xc6, 0xfe, 0xe8, 0x8e, 0xb6, 0xfe, 0xe8, 0x8e, 0xb8, 0xfe, 0x4c, 0xef,
            0x08, 0xa2, 0x00, 0x8e, 0xc0, 0xfe, 0xe8, 0x8e, 0xc2, 0xfe, 0xa2, 0x04,
            0x8e, 0xb6, 0xfe, 0xe8, 0x8e, 0xb8, 0xfe, 0x4c, 0xef, 0x08, 0x4e, 0x4f,
            0x20, 0x46, 0x49, 0x4c, 0x45, 0x20, 0x53, 0x59, 0x53, 0x54, 0x45, 0x4d,
            0x2e, 0x41, 0x50, 0x50, 0x4c, 0x45, 0x20, 0x0c, 0x53, 0x59, 0x53, 0x54,
            0x45, 0x4d, 0x2e, 0x41, 0x50, 0x50, 0x4c, 0x45, 0xa9, 0x01, 0x85, 0x42,
            0xa0, 0xff, 0xb1, 0x14, 0x8d, 0x5c, 0x09, 0xa9, 0x00, 0x85, 0x44, 0xa5,
            0x13, 0x85, 0x45, 0xa5, 0x0a, 0x85, 0x46, 0xa5, 0x0b, 0x85, 0x47, 0x20,
            0x00, 0x00, 0x90, 0x03, 0x4c, 0x5b, 0x08, 0xc6, 0x02, 0xf0, 0x0c, 0xe6,
            0x13, 0xe6, 0x13, 0xe6, 0x0a, 0xd0, 0xdc, 0xe6, 0x0b, 0xd0, 0xd8, 0x60,
            0x20, 0x43, 0x4f, 0x50, 0x59, 0x52, 0x49, 0x47, 0x48, 0x54, 0x20, 0x41,
            0x50, 0x50, 0x4c, 0x45, 0x20, 0x43, 0x4f, 0x4d, 0x50, 0x55, 0x54, 0x45,
            0x52, 0x2c, 0x20, 0x49, 0x4e, 0x43, 0x2e, 0x2c, 0x20, 0x31, 0x39, 0x38,
            0x34, 0x2c, 0x20, 0x31, 0x39, 0x38, 0x35, 0x20, 0x43, 0x2e, 0x4c, 0x45,
            0x55, 0x4e, 0x47, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0xb0, 0x01, 0x00, 0x00, 0x02, 0x00, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
        };

        #endregion Miscellaneous
    }
}
