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
using static DiskArc.Defs;
using static DiskArc.IFileSystem;

namespace DiskArc.FS {
    /// <summary>
    /// Implementation of a ProDOS file descriptor.
    /// </summary>
    /// <remarks>
    /// <para>This object doesn't tie up OS resources, and does not cache writes, so explicit
    /// destruction is not required.  However, the filesystem object keeps a list of open
    /// files, so we need to ensure that it's notified.  It's also handy to confirm that
    /// files with modifications were closed, since we may defer directory updates.</para>
    ///
    /// <para>Directory operations (rename, set access flags) are allowed to happen while a file
    /// is being accessed, and it's possible for both forks to be open simultaneously, so to
    /// avoid clashes this object is not allowed to modify the blocks that hold the directory
    /// contents or the extended info (which may contain the HFS file type).  Changes to storage
    /// type, key block, EOF, and blocks used are made through the file entry object, which is
    /// shared by both forks.</para>
    ///
    /// <para>The filesystem must not delete an open file.  There's no immediate problem, but
    /// since new files may be created afterward, it would be possible for the open file's
    /// storage to be re-used.</para>
    ///
    /// <para>Because this is intended for use in file archive applications, modifications to
    /// files do not alter the modification date.</para>
    /// </remarks>
    public class ProDOS_FileDesc : DiskFileStream {
        private const int PTRS_PER_IBLOCK = 256;        // max 256 pointers in index blocks
        private const int PTRS_PER_MIBLOCK = 128;       // max 128 pointers in master index

        // Stream
        public override bool CanRead { get { return FileEntry != null; } }
        public override bool CanSeek { get { return FileEntry != null; } }
        public override bool CanWrite { get { return FileEntry != null && !mIsReadOnly; } }
        public override long Length => mEndOfFile;
        public override long Position {
            get { return mMark; }
            set { Seek(value, SeekOrigin.Begin); }
        }

        // DiskFileStream
        public override FilePart Part { get; }

        private bool IsRsrcFork { get { return Part == FilePart.RsrcFork; } }

        internal ProDOS FileSystem { get; private set; }
        internal ProDOS_FileEntry FileEntry { get; private set; }

        private bool mIsReadOnly;                       // is writing disallowed?
        private bool mInternalOpen;                     // skip informing filesystem of close?


        /// <summary>
        /// Storage type of open fork.
        /// The value may change if the file is modified.
        /// </summary>
        internal ProDOS.StorageType StorageType { get; private set; }

        /// <summary>
        /// File's key block, the meaning of which is determined by the storage type.
        /// The value may change if the file is modified.
        /// </summary>
        internal uint KeyBlock { get; private set; }

        /// <summary>
        /// Current file length.
        /// The value may change if the file is modified.
        /// </summary>
        private int mEndOfFile;

        /// <summary>
        /// Blocks used by the open fork of the file, according to the directory entry or
        /// extended info block.
        /// The value may change if the file is modified.
        /// </summary>
        internal int BlocksUsed { get; private set; }

        // General-purpose temporary disk buffer, to reduce allocations.  Don't call other methods
        // while holding data here.
        private byte[] mTmpBlockBuf = new byte[BLOCK_SIZE];
        internal static readonly byte[] sZeroBlock = new byte[BLOCK_SIZE];

        /// <summary>
        /// A copy of the contents of a disk block, tagged with the block number and a "dirty"
        /// flag.
        /// </summary>
        internal class DiskBlock {
            public uint BlockNum { get; private set; }
            public byte[] Data { get; private set; }
            public bool IsDirty { get; private set; }

            /// <summary>
            /// Constructor, for an empty block.
            /// </summary>
            public DiskBlock(uint blockNum) {
                BlockNum = blockNum;
                Data = new byte[BLOCK_SIZE];
            }

            /// <summary>
            /// Constructor, for a block loaded from disk.
            /// </summary>
            /// <param name="blockNum">Disk block number.</param>
            /// <param name="chunkSource">Block I/O object.</param>
            public DiskBlock(uint blockNum, IChunkAccess chunkSource) : this(blockNum) {
                LoadBlock(blockNum, chunkSource);
            }

            /// <summary>
            /// Loads a block from disk.
            /// </summary>
            /// <remarks>
            /// This will throw an exception if the current contents are dirty.  The caller
            /// should either flush or invalidate the previous contents before calling here.
            /// </remarks>
            /// <param name="blockNum">Disk block number.</param>
            /// <param name="chunkSource">Block I/O object.</param>
            /// <exception cref="IOException">Disk access failure.</exception>
            public void LoadBlock(uint blockNum, IChunkAccess chunkSource) {
                if (IsDirty) {
                    throw new DAException("Overwriting dirty block");
                }

                chunkSource.ReadBlock(blockNum, Data, 0);

                // Read successful, update block number.
                BlockNum = blockNum;
            }

            /// <summary>
            /// Zeroes out the contents, representing a newly-allocated block.
            /// </summary>
            /// <param name="blockNum">Disk block number.</param>
            public void NewBlock(uint blockNum) {
                if (IsDirty) {
                    throw new DAException("Overwriting dirty block");
                }
                Array.Clear(Data);
                BlockNum = blockNum;
                IsDirty = true;
            }

            /// <summary>
            /// If the block is dirty, write the changes to disk.
            /// </summary>
            /// <param name="chunkSource">Block I/O object.</param>
            /// <exception cref="IOException">Disk access failure.</exception>
            public void SaveChanges(IChunkAccess chunkSource) {
                if (!IsDirty) {
                    return;
                }
                chunkSource.WriteBlock(BlockNum, Data, 0);
                IsDirty = false;
            }

            /// <summary>
            /// Utility routine for index blocks: get the Nth block number.
            /// </summary>
            public uint GetBlockPtr(int index) {
                Debug.Assert(index >= 0 && index < PTRS_PER_IBLOCK);
                return (uint)(Data[index] | (Data[index + BLOCK_SIZE / 2] << 8));
            }

            /// <summary>
            /// Utility routine for index blocks: set the Nth block number.
            /// </summary>
            public void SetBlockPtr(int index, uint blockNum) {
                Debug.Assert(index >= 0 && index < PTRS_PER_IBLOCK);
                Data[index] = (byte)blockNum;
                Data[index + BLOCK_SIZE / 2] = (byte)(blockNum >> 8);
                IsDirty = true;
            }

            public override string ToString() {
                return "[DiskBlock num=" + BlockNum + " dirty=" + IsDirty + "]";
            }
        }

        // Block # with extended file info (or 0 if none).
        private uint mExtBlockPtr = 0;

        // For sapling: copy of contents of index block.
        private DiskBlock? mIndexBlock = null;

        // For tree: copy of contents of master index block.
        private DiskBlock? mMasterIndexBlock = null;

        // For tree: copy of contents of index block we're currently working with.
        private DiskBlock? mCachedIndexBlock = null;
        private int mCachedIndexIndex = -1;

        // For directory: full set of blocks in file.
        private List<DiskBlock>? mDirBlocks = null;

        /// <summary>
        /// For directories: number of blocks used.
        /// </summary>
        public int NumDirBlocks {
            get {
                Debug.Assert(StorageType == ProDOS.StorageType.Directory);
                if (mDirBlocks == null) {
                    return -1;
                } else {
                    return mDirBlocks.Count;
                }
            }
        }

        /// <summary>
        /// Pathname to file, for debugging only.
        /// </summary>
        public string DebugPathName { get; private set; }

        /// <summary>
        /// Current file position (unsigned 24-bit value, 0 - 16,777,215).  May extend past EOF.
        /// </summary>
        private int mMark;


        /// <summary>
        /// Private constructor.
        /// </summary>
        private ProDOS_FileDesc(ProDOS_FileEntry entry, FileAccessMode mode,
                FilePart part, bool internalOpen) {
            FileSystem = entry.FileSystem;
            FileEntry = entry;
            Part = part;
            mInternalOpen = internalOpen;

            DebugPathName = entry.FullPathName;
            mIsReadOnly = (mode == FileAccessMode.ReadOnly);
        }

        /// <summary>
        /// Creates a file descriptor for the specified file entry.
        /// </summary>
        /// <remarks>
        /// <para>This is an internal method.  The caller is expected to validate the mode/part
        /// arguments, and ensure that this part of the file is not already open.</para>
        /// </remarks>
        /// <param name="entry">File entry to open.</param>
        /// <param name="mode">Access mode.</param>
        /// <param name="part">File part.</param>
        /// <param name="internalOpen">True if this is an "internal" open, which means it's
        ///   not being tracked by the filesystem object.</param>
        /// <exception cref="IOException">Disk access failure, or corrupted file
        ///   structure.</exception>
        /// <returns>File descriptor object.</returns>
        internal static ProDOS_FileDesc CreateFD(ProDOS_FileEntry entry,
                FileAccessMode mode, FilePart part, bool internalOpen) {
            Debug.Assert(!entry.IsDamaged);
            Debug.Assert(part == FilePart.DataFork || part == FilePart.RsrcFork);

            switch (entry.StorageType) {
                case ProDOS.StorageType.Seedling:
                case ProDOS.StorageType.Sapling:
                case ProDOS.StorageType.Tree:
                case ProDOS.StorageType.Extended:
                    break;
                case ProDOS.StorageType.Directory:
                    if (mode != FileAccessMode.ReadOnly) {
                        // Read-write could cause conflicts with cached file info.
                        throw new ArgumentException("Directories may not be opened for writing");
                    }
                    break;
                case ProDOS.StorageType.SubdirHeader:
                case ProDOS.StorageType.VolDirHeader:
                    // The header entries aren't files.  Something is broken?
                    throw new DAException("Header entries aren't files, don't try to open them");
                case ProDOS.StorageType.PascalVolume:
                default:
                    throw new DAException("Unable to open storage type " + entry.StorageType);
            }
            if (part != FilePart.DataFork &&
                    entry.StorageType != ProDOS.StorageType.Extended) {
                throw new IOException("File doesn't have a resource fork");
            }

            // Load primary index block into memory.  If we encounter any problems we abandon
            // the procedure.
            ProDOS_FileDesc newFd = new ProDOS_FileDesc(entry, mode, part, internalOpen);
            try {
                switch (entry.StorageType) {
                    case ProDOS.StorageType.Seedling:
                    case ProDOS.StorageType.Sapling:
                    case ProDOS.StorageType.Tree:
                        newFd.LoadIndexBlock(entry.StorageType, entry.KeyBlock, entry.DataLength,
                            entry.BlocksUsed);
                        break;
                    case ProDOS.StorageType.Extended: {
                            newFd.mExtBlockPtr = entry.KeyBlock;
                            Debug.Assert(entry.ExtInfo != null);
                            if (part == FilePart.DataFork) {
                                newFd.LoadIndexBlock(
                                    (ProDOS.StorageType)entry.ExtInfo.DataStorageType,
                                        entry.ExtInfo.DataKeyBlock, entry.ExtInfo.DataEof,
                                        entry.ExtInfo.DataBlocksUsed);
                            } else {
                                newFd.LoadIndexBlock(
                                    (ProDOS.StorageType)entry.ExtInfo.RsrcStorageType,
                                        entry.ExtInfo.RsrcKeyBlock, entry.ExtInfo.RsrcEof,
                                        entry.ExtInfo.RsrcBlocksUsed);
                            }
                        }
                        break;
                    case ProDOS.StorageType.Directory:
                        newFd.LoadDirBlockList(entry.KeyBlock, (int)entry.DataLength,
                            entry.BlocksUsed);
                        break;
                    default:
                        Debug.Assert(false);
                        throw new DAException("huh");
                }
            } catch {
                // We want GC cleanup of file descriptors to be a big red flag, so dispose
                // this explicitly.
                newFd.Dispose();
                throw;
            }

            newFd.mMark = 0;
            return newFd;
        }

        /// <summary>
        /// Loads the primary index block for the current file part.
        /// </summary>
        /// <para>The contents are not scrutinized for validity (that's up to the
        /// volume usage scan).</para>
        ///
        /// <para>For tree files, we're only loading the master index block.  The sub-index blocks
        /// are loaded as needed.</para>
        ///
        /// <para>Copies the arguments to the appropriate properties, and initializes
        /// file-structure-specific data structures.</para>
        /// <param name="storageType">Storage type of this file part.</param>
        /// <param name="keyBlock">Key block for this file part.</param>
        /// <param name="length">Length of this file part.</param>
        /// <param name="blocksUsed">Blocks used by this file part.</param>
        /// <returns>True on success.</returns>
        /// <exception cref="IOException">Disk read failure.</exception>
        private void LoadIndexBlock(ProDOS.StorageType storageType, uint keyBlock, long length,
                int blocksUsed) {
            if (!FileSystem.IsBlockValid(keyBlock)) {
                FileSystem.Notes.AddE("Invalid key block " + keyBlock);
                throw new IOException("Invalid key block " + keyBlock);
            }
            StorageType = storageType;
            KeyBlock = keyBlock;
            mEndOfFile = (int)length;
            BlocksUsed = blocksUsed;

            switch (storageType) {
                case ProDOS.StorageType.Seedling:
                    // Nothing to do here.
                    break;
                case ProDOS.StorageType.Sapling:
                    try {
                        mIndexBlock = new DiskBlock(keyBlock, FileSystem.ChunkAccess);
                    } catch (IOException) {
                        FileSystem.Notes.AddE("Failed reading index block " + keyBlock);
                        throw;
                    }
                    break;
                case ProDOS.StorageType.Tree:
                    try {
                        mMasterIndexBlock = new DiskBlock(keyBlock, FileSystem.ChunkAccess);
                        mCachedIndexBlock = new DiskBlock(uint.MaxValue);
                    } catch (IOException) {
                        FileSystem.Notes.AddE("Failed reading master index block " + keyBlock);
                        throw;
                    }
                    break;
                default:
                    // should not be here
                    Debug.Assert(false);
                    throw new DAException("huh");
            }
        }

        /// <summary>
        /// Counts up the number of nonzero block pointers in an index block.
        /// </summary>
        /// <remarks>
        /// Assumes there's no extra junk in the index block.  We screen for that during
        /// the scan, so it should be a reasonable assumption.
        /// </remarks>
        private static int CountNonSparse(DiskBlock block) {
            int count = 0;
            for (int i = 0; i < PTRS_PER_IBLOCK; i++) {
                if (block.GetBlockPtr(i) != 0) {
                    count++;
                }
            }
            return count;
        }

        /// <summary>
        /// Calculates the number of blocks currently required to store the open fork.
        /// </summary>
        /// <remarks>
        /// <para>Unlike fields like EOF and storage type, the values stored in the directory entry
        /// and extended info block are not definitive.  The value computed here is the
        /// correct value, and may not match what's in the BlocksUsed property.</para>
        ///
        /// <para>For an extended file, to get the total block count you must sum the number
        /// of blocks used for each fork, then add one for the extended key block.</para>
        /// </remarks>
        /// <returns>Blocks used.</returns>
        internal int CalcBlocksUsed() {
            switch (StorageType) {
                case ProDOS.StorageType.Seedling:
                    // No index blocks, 1 data block.
                    return 1;
                case ProDOS.StorageType.Sapling:
                    Debug.Assert(mIndexBlock != null);
                    return 1 + CountNonSparse(mIndexBlock);
                case ProDOS.StorageType.Tree:
                    Debug.Assert(mMasterIndexBlock != null);
                    Debug.Assert(mCachedIndexBlock != null);
                    int count = 1;      // master index block
                    for (int i = 0; i < PTRS_PER_MIBLOCK; i++) {
                        uint indexPtr = CacheTreeIndex(i);
                        if (indexPtr != 0) {
                            // Non-sparse index block.  Count 1 for the index block, then
                            // count the non-sparse entries in it.
                            count++;
                            count += CountNonSparse(mCachedIndexBlock);
                        }
                    }
                    return count;
                case ProDOS.StorageType.Directory:
                    Debug.Assert(mDirBlocks != null);
                    return mDirBlocks.Count;
                default:
                    throw new Exception();
            }
        }


        /// <summary>
        /// Loads the blocks used for a directory.
        ///
        /// Initializes various properties and data structures.
        /// </summary>
        private bool LoadDirBlockList(uint keyBlock, int length, int blocksUsed) {
            StorageType = ProDOS.StorageType.Directory;
            KeyBlock = keyBlock;
            mEndOfFile = length;
            BlocksUsed = blocksUsed;

            mDirBlocks = new List<DiskBlock>();
            uint curBlock = keyBlock;
            uint prevBlock = 0;
            int iter = 0;

            while (curBlock != 0) {
                if (!FileSystem.IsBlockValid(curBlock)) {
                    FileSystem.Notes.AddE("Invalid block pointer in directory (" + curBlock + ")");
                    // Can abort the whole thing or return what we have, but can't go further.
                    return false;
                }

                try {
                    DiskBlock db = new DiskBlock(curBlock, FileSystem.ChunkAccess);

                    ushort prev = RawData.GetU16LE(db.Data, 0);
                    ushort next = RawData.GetU16LE(db.Data, 2);
                    if (prev != prevBlock) {
                        FileSystem.Notes.AddW("Incorrect prev pointer in directory (should be " +
                            prevBlock + ", found " + prev + ")");
                        // keep going
                    }
                    // We don't try to validate any fields other than the pointers.

                    mDirBlocks.Add(db);

                    prevBlock = curBlock;
                    curBlock = next;

                    // Make sure we're not looping forever.
                    if (++iter > ProDOS.MAX_DIR_BLOCKS) {
                        FileSystem.Notes.AddE("Found circular directory links");
                        return false;
                    }
                } catch (IOException) {
                    return false;
                }
            }

            return true;
        }

#if false
        /// <summary>
        /// Gets a reference to data for the Nth directory block.  The contents of the
        /// data reference returned must not be modified.
        /// </summary>
        /// <remarks>
        /// This is an internal routine, used to provide reduced-overhead I/O on a
        /// directory object.  The data is already in memory, so no actual I/O occurs.
        /// </remarks>
        /// <param name="index">Directory block index (0-N).</param>
        /// <param name="blockNum">Block number.</param>
        /// <param name="data">Data buffer.</param>
        /// <param name="offset">Byte offset.</param>
        /// <returns>True on success.</returns>
        internal bool GetDirBlock(int index, out uint blockNum, out byte[] data,
                out int offset) {
            data = RawData.EMPTY_BYTE_ARRAY;
            blockNum = 0;
            offset = -1;

            if (mDirBlocks == null) {
                return false;
            }
            if (index < 0 || index >= mDirBlocks.Count) {
                return false;
            }

            blockNum = mDirBlocks[index].BlockNum;
            data = mDirBlocks[index].Data;
            offset = 0;
            return true;
        }
#endif

        /// <summary>
        /// <para>Validates the set of blocks held in an index block.  Just checks the block
        /// numbers; does not attempt to read the data blocks.</para>
        /// <para>All 256 pointers are checked, but any pointers to blocks that would be past
        /// the EOF are required to be zero.</para>
        /// </summary>
        /// <param name="vu">Volume usage object.</param>
        /// <param name="db">Block with indices.</param>
        /// <param name="nonLegit">First non-legitimate entry.  Any nonzero entries past this
        ///   point will cause the file to be marked "dubious".</param>
        /// <param name="isDamaged">Result: is file too damaged to access?</param>
        private void ValidateBlocks(VolumeUsage vu, DiskBlock db, int nonLegit,
                out bool isDamaged) {
            isDamaged = false;
            Debug.Assert(nonLegit >= 0);
            vu.SetUsage(db.BlockNum, FileEntry);
            for (int i = 0; i < PTRS_PER_IBLOCK; i++) {
                uint ptr = db.GetBlockPtr(i);
                if (i < nonLegit) {
                    if (ptr == 0) {
                        // sparse, do nothing
                    } else if (FileSystem.IsBlockValid(ptr)) {
                        // add to usage map
                        vu.SetUsage(ptr, FileEntry);
                    } else {
                        FileSystem.Notes.AddE("Invalid block ptr in " +
                            (IsRsrcFork ? "rsrc" : "data") + " block #" + i +
                            "(" + db.BlockNum + "), entry $" + i.ToString("x2") +
                            " (" + ptr + "): " + FileEntry.FullPathName);
                        isDamaged = true;
                        break;
                    }
                } else {
                    if (ptr != 0) {
                        // Junk could cause problems later, but it doesn't count as file damage.
                        FileEntry.HasJunk = true;
                        FileSystem.Notes.AddW("Invalid data at end of index block " + db.BlockNum +
                            " (file '" + FileEntry.FullPathName + "')");
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Sets the volume usage entries for the index and data blocks.  Checks for invalid
        /// block references while doing so.
        /// </summary>
        /// <remarks>
        /// If this is an extended file, the volume usage for the extended info block is only
        /// set when the resource fork is being scanned.  This avoids incorrectly reporting
        /// a conflict.
        /// </remarks>
        /// <param name="isDamaged">Result: true if bad index block found.</param>
        /// <param name="isDubious">Result: true if conflict found.</param>
        internal void SetVolumeUsage(out bool isDamaged) {
            Debug.Assert(FileSystem.VolBitmap != null);
            VolumeUsage vu = FileSystem.VolBitmap.VolUsage;

            isDamaged = false;

            switch (StorageType) {
                case ProDOS.StorageType.Seedling:
                    // Key block was checked when fd was opened.
                    vu.SetUsage(KeyBlock, FileEntry);
                    break;
                case ProDOS.StorageType.Sapling: {
                        // Validate contents of index block.
                        Debug.Assert(mIndexBlock != null);
                        int firstNonLegit = (mEndOfFile + BLOCK_SIZE - 1) / BLOCK_SIZE;
                        ValidateBlocks(vu, mIndexBlock, firstNonLegit, out isDamaged);
                        if (!isDamaged && mIndexBlock.GetBlockPtr(0) == 0) {
                            // We only check if not damaged, to reduce overall noise.
                            FileSystem.Notes.AddW("First block of sapling file is sparse: " +
                                FileEntry.FullPathName);
                        }
                    }
                    break;
                case ProDOS.StorageType.Tree: {
                        Debug.Assert(mMasterIndexBlock != null && mCachedIndexBlock != null);
                        // Add master index block.
                        vu.SetUsage(mMasterIndexBlock.BlockNum, FileEntry);

                        // Add the contents of each index block.
                        int eofRemaining = mEndOfFile;
                        for (int i = 0; i < PTRS_PER_MIBLOCK; i++) {
                            uint indexBlockNum = mMasterIndexBlock.GetBlockPtr(i);
                            if (indexBlockNum != 0) {
                                // Non-sparse block, we expect to find data here.
                                if (!FileSystem.IsBlockValid(indexBlockNum)) {
                                    FileSystem.Notes.AddE("Invalid index block pointer " +
                                        indexBlockNum +
                                        " (file '" + FileEntry.FullPathName + "')");
                                    throw new IOException("Invalid index block pointer");
                                }

                                CacheTreeIndex(i);
                                int firstNonLegit = (eofRemaining + BLOCK_SIZE - 1) / BLOCK_SIZE;
                                if (firstNonLegit > PTRS_PER_IBLOCK) {
                                    firstNonLegit = PTRS_PER_IBLOCK;
                                }
                                ValidateBlocks(vu, mCachedIndexBlock, firstNonLegit, out isDamaged);

                                if (i == 0 && !isDamaged) {
                                    if (mCachedIndexBlock.GetBlockPtr(0) == 0) {
                                        // Not a problem for us, but can confuse GS/OS.
                                        // e.g. https://github.com/fadden/ciderpress/issues/49
                                        FileSystem.Notes.AddW(
                                            "First block of tree file is sparse: " +
                                            FileEntry.FullPathName);
                                    }
                                }
                            }
                            eofRemaining -= BLOCK_SIZE * PTRS_PER_IBLOCK;    // 128KB per index
                            if (eofRemaining < 0) {
                                eofRemaining = 0;
                            }
                        }
                    }
                    break;
                case ProDOS.StorageType.Directory: {
                        Debug.Assert(mDirBlocks != null);
                        for (int i = 0; i < mDirBlocks.Count; i++) {
                            DiskBlock db = mDirBlocks[i];
                            vu.SetUsage(db.BlockNum, FileEntry);
                        }
                    }
                    break;
                case ProDOS.StorageType.Extended:
                // Only one fork is open, so this makes no sense.
                case ProDOS.StorageType.PascalVolume:
                // Can't be opened as a file.
                case ProDOS.StorageType.SubdirHeader:
                case ProDOS.StorageType.VolDirHeader:
                // Should not be associated with an open file.
                default:
                    throw new Exception("Unexpected storage type on open file");
            }

            // If this is the resource fork, mark the extended info block.
            if (IsRsrcFork) {
                Debug.Assert(FileSystem.IsBlockValid(mExtBlockPtr));
                vu.SetUsage(mExtBlockPtr, FileEntry);
            }
        }

        /// <summary>
        /// Marks all of the blocks associated with the open fork as free, including the
        /// index blocks.  Call this when deleting a file (or a fork of a file).
        /// </summary>
        /// <remarks>
        /// This does not release the extended info block, since that's not really a
        /// part of either fork.
        ///
        /// The various data structures holding file pieces are not modified.  This just
        /// sets bits in the volume bitmap.
        /// </remarks>
        internal void ReleaseStorage() {
            ProDOS_VolBitmap? vb = FileSystem.VolBitmap;
            Debug.Assert(vb != null);

            switch (StorageType) {
                case ProDOS.StorageType.Seedling:
                    vb.MarkBlockUnused(KeyBlock);
                    break;
                case ProDOS.StorageType.Sapling: {
                        Debug.Assert(mIndexBlock != null);
                        FreeBlocks(vb, mIndexBlock);
                        vb.MarkBlockUnused(mIndexBlock.BlockNum);
                    }
                    break;
                case ProDOS.StorageType.Tree: {
                        Debug.Assert(mMasterIndexBlock != null && mCachedIndexBlock != null);

                        // Release the contents of each index block, then the index itself.
                        // This doesn't modify the file structure, just the volume block bitmap.
                        for (int i = 0; i < PTRS_PER_MIBLOCK; i++) {
                            uint indexPtr = CacheTreeIndex(i);
                            if (indexPtr != 0) {
                                Debug.Assert(FileSystem.IsBlockValid(indexPtr));
                                FreeBlocks(vb, mCachedIndexBlock);
                                vb.MarkBlockUnused(indexPtr);
                            }
                        }

                        // Free the master index block.
                        vb.MarkBlockUnused(mMasterIndexBlock.BlockNum);
                    }
                    break;
                case ProDOS.StorageType.Directory: {
                        Debug.Assert(mDirBlocks != null);
                        foreach (DiskBlock db in mDirBlocks) {
                            vb.MarkBlockUnused(db.BlockNum);
                        }
                    }
                    break;
                default:
                    throw new Exception("Unexpected storage type " + StorageType);
            }
        }

        /// <summary>
        /// Marks all of the blocks listed in an index block as free.
        /// </summary>
        private static void FreeBlocks(ProDOS_VolBitmap vb, DiskBlock indexBlock) {
            for (int i = 0; i < PTRS_PER_IBLOCK; i++) {
                uint blockNum = indexBlock.GetBlockPtr(i);
                if (blockNum != 0) {
                    vb.MarkBlockUnused(blockNum);
                }
            }
        }

        /// <summary>
        /// Loads the Nth index from a tree file into mCachedIndexBlock, flushing the
        /// previous contents to disk if dirty.
        /// </summary>
        /// <remarks>
        /// May modify the master index block and volume bitmap.
        /// </remarks>
        /// <param name="indexIndex">Index into master index (0-127).</param>
        /// <param name="isNewBlock">True if this is a newly-allocated index block, false
        ///   if this is an existing block that should be read from disk.</param>
        /// <returns>Index block pointer, or 0 if it's sparse.</returns>
        private uint CacheTreeIndex(int indexIndex, bool isNewBlock = false) {
            Debug.Assert(StorageType == ProDOS.StorageType.Tree);
            Debug.Assert(mMasterIndexBlock != null);
            Debug.Assert(mCachedIndexBlock != null);
            Debug.Assert(indexIndex >= 0 && indexIndex <= PTRS_PER_MIBLOCK);

            uint indexPtr = mMasterIndexBlock.GetBlockPtr(indexIndex);
            if (indexPtr == 0) {
                // Sparse entry.
                return 0;
            }
            Debug.Assert(FileSystem.IsBlockValid(indexPtr));   // should have been screened earlier
            if (isNewBlock) {
                FlushCachedIndex();
                mCachedIndexBlock.NewBlock(indexPtr);
                mCachedIndexIndex = indexIndex;
            } else if (mCachedIndexBlock.BlockNum != indexPtr) {
                // Different block in buffer, load new block from disk.
                FlushCachedIndex();
                try {
                    mCachedIndexBlock.LoadBlock(indexPtr, FileSystem.ChunkAccess);
                } catch (IOException) {
                    FileSystem.AppHook.LogE("I/O error reading block " + indexPtr);
                    throw;
                }
                mCachedIndexIndex = indexIndex;
            } else {
                // Block is already loaded.
                Debug.Assert(mCachedIndexIndex == indexIndex);
            }
            return indexPtr;
        }

        /// <summary>
        /// Flushes the cached index block to disk.  Does nothing if the fork's file structure
        /// isn't a tree.
        ///
        /// May modify the master index block and volume bitmap, but only to remove a block
        /// (this won't cause a disk full error).
        /// </summary>
        /// <remarks>
        /// If we're flushing an index block that's entirely zero-filled, we remove it from
        /// the master index block and update the volume bitmap.  It's more efficient to check
        /// it here than on every time we update an entry.
        /// </remarks>
        private void FlushCachedIndex() {
            if (StorageType != ProDOS.StorageType.Tree) {
                return;
            }
            Debug.Assert(mMasterIndexBlock != null);
            Debug.Assert(mCachedIndexBlock != null);

            if (mCachedIndexBlock.IsDirty) {
                // Write the index block to disk.
                mCachedIndexBlock.SaveChanges(FileSystem.ChunkAccess);

                // If it's entirely filled with zeroes, remove it from the master index.  (We
                // could also have skipped the block write.)
                if (RawData.IsAllZeroes(mCachedIndexBlock.Data, 0, BLOCK_SIZE)) {
                    // Set the master index entry to zero.
                    mMasterIndexBlock.SetBlockPtr(mCachedIndexIndex, 0);
                    ProDOS_VolBitmap? vb = FileSystem.VolBitmap;
                    Debug.Assert(vb != null);

                    // Update the block usage map.
                    vb.MarkBlockUnused(mCachedIndexBlock.BlockNum);
                    BlocksUsed--;
                }
            }
        }

        /// <summary>
        /// Returns the block number of the Nth block in the file, or 0 if the block is sparse.
        /// For tree files, this may need to load the appropriate index block from disk.
        /// </summary>
        /// <exception cref="IOException">Disk access failure.</exception>
        private uint GetBlockNum(int blockIndex) {
            Debug.Assert(blockIndex >= 0);
            Debug.Assert(blockIndex < PTRS_PER_IBLOCK * PTRS_PER_MIBLOCK);  // 32768

            switch (StorageType) {
                case ProDOS.StorageType.Seedling:
                    if (blockIndex == 0) {
                        // Data is in our one and only block.
                        Debug.Assert(FileSystem.IsBlockValid(KeyBlock));
                        return KeyBlock;
                    } else {
                        return 0;       // trailing sparse
                    }
                case ProDOS.StorageType.Sapling:
                    if (blockIndex < PTRS_PER_IBLOCK) {
                        // Data is in the area covered by the sapling (256 blocks).
                        Debug.Assert(mIndexBlock != null);
                        return mIndexBlock.GetBlockPtr(blockIndex);
                    } else {
                        return 0;       // trailing sparse
                    }
                case ProDOS.StorageType.Tree:
                    Debug.Assert(mMasterIndexBlock != null && mCachedIndexBlock != null);

                    // Calculate index into master index block (256 blocks per index).
                    int indexIndex = blockIndex / PTRS_PER_IBLOCK;
                    // Load the appropriate index block.
                    uint indexPtr;
                    try {
                        indexPtr = CacheTreeIndex(indexIndex);
                    } catch (IOException) {
                        // We disallow reading damaged files, so this must be happening during
                        // the initial scan.
                        FileSystem.Notes.AddE("Failed reading index block #" + indexIndex);
                        throw;
                    }
                    if (indexPtr == 0) {
                        // Sparse index, so block is sparse too.
                        return 0;
                    }
                    return mCachedIndexBlock.GetBlockPtr(blockIndex & 0xff);
                case ProDOS.StorageType.Directory:
                    Debug.Assert(mDirBlocks != null);
                    return mDirBlocks[blockIndex].BlockNum;
                default:
                    throw new Exception();
            }
        }

        /// <summary>
        /// Sets a block number in an index block.  This is only for use with sapling and tree
        /// files.
        /// </summary>
        /// <remarks>
        /// The file structure must accommodate the block index.  Expand the file if necessary
        /// before calling here.  This may allocate or release index blocks for tree files.
        ///
        /// The very first block of the file cannot be made sparse.
        /// </remarks>
        /// <param name="blockIndex">Index of block to set (file posn / 512).</param>
        /// <param name="blockNum">New value (0 or 2-65535).</param>
        /// <exception cref="DiskFullException">No space to allocate new index block.</exception>
        /// <exception cref="IOException">Disk access failure.</exception>
        private void SetIndexBlockNum(int blockIndex, uint blockNum) {
            Debug.Assert(blockIndex >= 0);
            Debug.Assert(blockNum <= ProDOS.MAX_VOL_SIZE / BLOCK_SIZE);

            if (blockIndex == 0 && blockNum == 0) {
                // ProDOS affectation.
                Debug.Assert(false, "First block of file cannot be made sparse");
                return;
            }

            switch (StorageType) {
                case ProDOS.StorageType.Sapling:
                    Debug.Assert(blockIndex < PTRS_PER_IBLOCK);
                    // Data is in the area covered by the sapling (256 blocks).
                    Debug.Assert(mIndexBlock != null);
                    mIndexBlock.SetBlockPtr(blockIndex, blockNum);
                    break;
                case ProDOS.StorageType.Tree:
                    Debug.Assert(blockIndex < PTRS_PER_IBLOCK * PTRS_PER_MIBLOCK);  // 32768
                    Debug.Assert(mMasterIndexBlock != null && mCachedIndexBlock != null);

                    // Calculate index into master index block (256 blocks per index).
                    int indexIndex = blockIndex / PTRS_PER_IBLOCK;
                    uint indexPtr = CacheTreeIndex(indexIndex);
                    if (indexPtr == 0) {
                        // Index block is sparse.  Allocate index block if necessary.
                        if (blockNum == 0) {
                            // Writing a sparse block in a sparse index block, nothing to do.
                            return;
                        }

                        // We need to reference a block from an index block that is currently
                        // sparse.  Allocate a new, zeroed out block to hold indices.
                        ProDOS_VolBitmap? vb = FileSystem.VolBitmap;
                        Debug.Assert(vb != null);
                        indexPtr = vb.AllocBlock(FileEntry);
                        mMasterIndexBlock.SetBlockPtr(indexIndex, indexPtr);
                        CacheTreeIndex(indexIndex, true);
                        BlocksUsed++;
                    }

                    // Now we have the index block in the cache.  Set the pointer.
                    mCachedIndexBlock.SetBlockPtr(blockIndex & 0xff, blockNum);
                    break;
                case ProDOS.StorageType.Seedling:
                case ProDOS.StorageType.Directory:
                default:
                    throw new NotImplementedException("Unsupported storage type " + StorageType);
            }
        }

        // Stream
        public override int Read(byte[] readBuf, int readOffset, int count) {
            CheckValid();
            if (readOffset < 0) {
                throw new ArgumentOutOfRangeException(nameof(readOffset), readOffset, "bad offset");
            }
            if (count < 0) {
                throw new ArgumentOutOfRangeException(nameof(count), count, "bad count");
            }
            if (readBuf == null) {
                throw new ArgumentNullException(nameof(readBuf));
            }
            if (count == 0 || mMark >= mEndOfFile) {
                return 0;
            }
            if (readOffset >= readBuf.Length || count > readBuf.Length - readOffset) {
                throw new ArgumentException("Buffer overrun");
            }

            // Trim request to fit file bounds.
            if ((long)mMark + count > mEndOfFile) {
                count = mEndOfFile - mMark;
            }
            if ((long)readOffset + count > readBuf.Length) {
                throw new ArgumentOutOfRangeException("Buffer overflow");
            }
            int actual = count;    // we read it all, or fail

            // Calculate block index from file position.
            int blockIndex = mMark / BLOCK_SIZE;
            int blockOffset = mMark % BLOCK_SIZE;
            while (count > 0) {
                // Read the block's contents into a temporary buffer.
                uint blockNum = GetBlockNum(blockIndex);

                byte[] data;
                if (blockNum == 0) {
                    // Sparse block, just copy zeroes.
                    data = sZeroBlock;
                } else {
                    // Read data from source.
                    data = mTmpBlockBuf;
                    FileSystem.ChunkAccess.ReadBlock(blockNum, data, 0);
                }

                // Copy everything we need out of this block.
                int bufLen = BLOCK_SIZE - blockOffset;
                if (bufLen > count) {
                    bufLen = count;
                }
                Array.Copy(data, blockOffset, readBuf, readOffset, bufLen);

                readOffset += bufLen;
                count -= bufLen;
                mMark += bufLen;

                blockIndex++;
                blockOffset = 0;
            }

            return actual;
        }

        // Stream
        public override void Write(byte[] buf, int offset, int count) {
            CheckValid();
            if (mIsReadOnly) {
                throw new NotSupportedException("File was opened read-only");
            }
            if (offset < 0 || count < 0) {
                throw new ArgumentOutOfRangeException("Bad offset / count");
            }
            if (buf == null) {
                throw new ArgumentNullException("Buffer is null");
            }
            if (count == 0) {
                return;
            }
            if (offset >= buf.Length || count > buf.Length - offset) {
                throw new ArgumentException("Buffer overrun");
            }
            if (offset + count > buf.Length) {
                throw new ArgumentOutOfRangeException("Buffer underflow");
            }

            Debug.Assert(StorageType == ProDOS.StorageType.Seedling ||
                StorageType == ProDOS.StorageType.Sapling ||
                StorageType == ProDOS.StorageType.Tree);
            Debug.Assert(mMark >= 0 && mMark <= ProDOS.MAX_FILE_LEN);
            if (mMark + count > ProDOS.MAX_FILE_LEN) {
                // We don't do partial writes, so we just throw if we're off the end.
                // The last byte in the last block is not accessible.
                throw new IOException("Write would exceed max file size (mark=" +
                    mMark + " len=" + count + ")");
            }

            ProDOS_VolBitmap? vb = FileSystem.VolBitmap;
            Debug.Assert(vb != null);

            //
            // Write one block at a time.  We update the various bits and pieces after each
            // change, so that encountering a bad block or disk full error (which will throw
            // completely out of this method) leaves us with as much data written as possible,
            // and state internally consistent.
            //

            while (count > 0) {
                // Set block index according to file position.
                int blockIndex = mMark / BLOCK_SIZE;
                int blockOffset = mMark % BLOCK_SIZE;

                byte[] writeSource;
                int writeSourceOff;
                int writeLen;

                uint blockNum = GetBlockNum(blockIndex);
                if (blockOffset != 0 || count < BLOCK_SIZE) {
                    // Partial write to the start or end of a block.  If there's an existing
                    // non-sparse block here, we need to read it and merge the contents.
                    if (blockNum != 0) {
                        FileSystem.ChunkAccess.ReadBlock(blockNum, mTmpBlockBuf, 0);
                    } else {
                        Array.Clear(mTmpBlockBuf);
                    }
                    // Try to fill out the rest of the block, but clamp to write length.
                    writeLen = BLOCK_SIZE - blockOffset;
                    if (writeLen > count) {
                        writeLen = count;
                    }
                    Array.Copy(buf, offset, mTmpBlockBuf, blockOffset, writeLen);
                    writeSource = mTmpBlockBuf;
                    writeSourceOff = 0;
                } else {
                    // Writing a full block, so just use the source data.
                    writeSource = buf;
                    writeSourceOff = offset;
                    writeLen = BLOCK_SIZE;
                }

                // If the block is filled with zero, we don't need to write it.  If the
                // block is allocated, deallocate it.  We could downsize the file structure
                // if the tail end of the file is completely zero, but that's probably not
                // worth handling.
                //
                // The exception to the rule is that the first block of the file is always
                // allocated.  The GS/OS FST and certain P8 file utilities will behave
                // strangely if this is not the case.
                //
                // (We will never call SetIndexBlockNum on a seedling.  The only way it could
                // happen is if the seedling's key block was zero, but that would have
                // resulted in the file being marked "damage" and hence not writable.)
                if (blockIndex != 0 &&
                        RawData.IsAllZeroes(writeSource, writeSourceOff, BLOCK_SIZE)) {
                    // Not first block, and nothing but zero.  Store as sparse "hole".
                    if (blockNum == 0) {
                        // Already sparse, nothing to do here.
                    } else {
                        // Was allocated, deallocate the block.
                        SetIndexBlockNum(blockIndex, 0);
                        vb.MarkBlockUnused(blockNum);

                        // If the blocks_used value in the directory entry is too small, and
                        // it wasn't fixed by the scanner, this could underflow.  Clamp to zero.
                        if (BlocksUsed > 0) {
                            BlocksUsed--;
                        } else {
                            Debug.Assert(false);
                        }
                    }
                } else {
                    ExpandIfNeeded(blockIndex, vb);
                    if (blockNum == 0) {
                        // Was sparse, allocate a block to hold the data, and add it to the
                        // file structure.
                        blockNum = vb.AllocBlock(FileEntry);
                        SetIndexBlockNum(blockIndex, blockNum);
                        BlocksUsed++;
                    }

                    // Finally, write the block to the disk.
                    FileSystem.ChunkAccess.WriteBlock(blockNum, writeSource, writeSourceOff);
                }

                // Advance file position.
                mMark += writeLen;
                offset += writeLen;
                count -= writeLen;
                if (mMark > mEndOfFile) {
                    mEndOfFile = mMark;
                }
            }

            // We could flush the index blocks, volume bitmap, and dirent values at this point
            // to make us more reliable if the entire application crashes between Write calls,
            // but there's so many other layers where things could go wrong that I don't think
            // it's useful.  Paranoid callers can call Flush() after Write().
            // TODO(maybe): have a flush-after-write setting?

            // NOTE: pushing the dirent changes to the FileDescr here doesn't work unless you
            // also flush the index blocks, because we might have sparsed an index block out
            // of existence, which changes blocks_used.
        }

        /// <summary>
        /// Expands the file if the block index doesn't fit within the bounds of the current
        /// storage type.  This will update the StorageType, KeyBlock, and BlocksUsed fields.
        /// </summary>
        /// <exception cref="DiskFullException">Disk is full.</exception>
        private void ExpandIfNeeded(int blockIndex, ProDOS_VolBitmap vb) {
            Debug.Assert(blockIndex >= 0 && blockIndex * BLOCK_SIZE < ProDOS.MAX_FILE_LEN);

            if (blockIndex > 0 && StorageType == ProDOS.StorageType.Seedling) {
                // Expand seedling to sapling.  Allocate an index block, copy the key block
                // pointer into the 0th entry, and change the key block to point at the index.
                uint blockNum = vb.AllocBlock(FileEntry);
                BlocksUsed++;
                DiskBlock db = new DiskBlock(blockNum);
                db.SetBlockPtr(0, KeyBlock);
                KeyBlock = blockNum;

                Debug.Assert(mIndexBlock == null);
                StorageType = ProDOS.StorageType.Sapling;
                mIndexBlock = db;
            }

            if (blockIndex >= PTRS_PER_IBLOCK && StorageType == ProDOS.StorageType.Sapling) {
                // Expand sapling to tree.  Allocate the master index block, copy the key block
                // pointer into the 0th entry, and change the key block to point to the master.
                Debug.Assert(mIndexBlock != null);
                Debug.Assert(mIndexBlock.BlockNum == KeyBlock);
                uint blockNum = vb.AllocBlock(FileEntry);
                BlocksUsed++;
                DiskBlock db = new DiskBlock(blockNum);
                db.SetBlockPtr(0, KeyBlock);
                KeyBlock = blockNum;

                Debug.Assert(mMasterIndexBlock == null && mCachedIndexBlock == null);
                StorageType = ProDOS.StorageType.Tree;
                mCachedIndexBlock = mIndexBlock;
                mCachedIndexIndex = 0;
                mMasterIndexBlock = db;
                mIndexBlock = null;
            }
        }

        /// <summary>
        /// Flushes the sapling index block or the master index + cached index block.
        /// </summary>
        private void FlushIndexBlocks() {
            if (mIndexBlock != null) {
                mIndexBlock.SaveChanges(FileSystem.ChunkAccess);
            }
            FlushCachedIndex();     // may modify master index, so do this before
            if (mMasterIndexBlock != null) {
                mMasterIndexBlock.SaveChanges(FileSystem.ChunkAccess);
            }
        }

        // Stream
        public override void Flush() {
            CheckValid();
            if (mIsReadOnly) {
                return;
            }

            // Flush index blocks.  This may affect the dirent values and the volume bitmap
            // if we end up writing a zeroed-out index block, so do this first.
            FlushIndexBlocks();

            // Submit changes to values stored in the directory entry.  We aren't allowed to
            // modify these blocks directly.
            if (FileEntry.StorageType == ProDOS.StorageType.Extended) {
                Debug.Assert(FileEntry.ExtInfo != null);
                if (IsRsrcFork) {
                    FileEntry.ExtInfo.RsrcStorageType = (byte)StorageType;
                    FileEntry.ExtInfo.RsrcKeyBlock = (ushort)KeyBlock;
                    FileEntry.ExtInfo.RsrcEof = (uint)mEndOfFile;
                    FileEntry.ExtInfo.RsrcBlocksUsed = (ushort)BlocksUsed;
                } else {
                    FileEntry.ExtInfo.DataStorageType = (byte)StorageType;
                    FileEntry.ExtInfo.DataKeyBlock = (ushort)KeyBlock;
                    FileEntry.ExtInfo.DataEof = (uint)mEndOfFile;
                    FileEntry.ExtInfo.DataBlocksUsed = (ushort)BlocksUsed;
                }
                // Main dir entry blocks_used field tracks changes to both forks.  If both forks
                // are open for writing, both writers will need to keep it up to date.
                FileEntry.BlocksUsed = (ushort)(FileEntry.ExtInfo.DataBlocksUsed +
                    FileEntry.ExtInfo.RsrcBlocksUsed + 1);
            } else {
                FileEntry.StorageType = StorageType;
                FileEntry.KeyBlock = (ushort)KeyBlock;
                FileEntry.EndOfFile = (uint)mEndOfFile;
                FileEntry.BlocksUsed = (ushort)BlocksUsed;
            }

            // Flush dirent and extended info block changes.
            FileEntry.SaveChanges();

            // Flush this too, just for good measure.
            FileSystem.VolBitmap?.Flush();
        }

        // Stream
        public override void SetLength(long newEof) {
            CheckValid();
            if (mIsReadOnly) {
                throw new NotSupportedException("File was opened read-only");
            }
            if (newEof < 0 || newEof > ProDOS.MAX_FILE_LEN) {
                throw new ArgumentOutOfRangeException(nameof(newEof), newEof, "Invalid length");
            }
            if (newEof == mEndOfFile) {
                return;
            }

            if (newEof > mEndOfFile) {
                // Creating a sparse hole at the end, just set the value and be done.
                mEndOfFile = (int)newEof;
                Flush();
                return;
            }

            // If the end of file is 0, 511, or 512, we need one block; at 513 we need 2.  So
            // the number of blocks required is ((EOF - 1) / 512) + 1, with a special case
            // for zero because we still want to allocate a block even if we don't have data.
            // We want the index of the last block, which is (length - 1).

            int oldLastBlockIndex = (mEndOfFile - 1) / BLOCK_SIZE;
            //if (oldLastBlockIndex < 0) {      // -1/512 == 0
            //    oldLastBlockIndex = 0;
            //}
            int newLastBlockIndex = ((int)newEof - 1) / BLOCK_SIZE;
            //if (newLastBlockIndex < 0) {
            //    newLastBlockIndex = 0;
            //}
            if (oldLastBlockIndex == newLastBlockIndex) {
                // No change to blocks_used.  If the EOF is different, update it.
                if (mEndOfFile != (int)newEof) {
                    mEndOfFile = (int)newEof;
                }
                Flush();
                return;
            }

            ProDOS_VolBitmap? vb = FileSystem.VolBitmap;
            Debug.Assert(vb != null);

            // Set all the block numbers in the truncated area to zero.  We free the data
            // blocks here.  If all the blocks in a tree index block are zeroed, the master
            // block handler will also release the index block.
            //
            // We could be more efficient here, removing the tail end of a large tree
            // file with calls to the FreeBlocks() routine used by the file deletion code,
            // but I think the only thing we might want to optimize is full truncation
            // (i.e. newEof == 0).
            for (int idx = newLastBlockIndex + 1; idx <= oldLastBlockIndex; idx++) {
                uint blockPtr = GetBlockNum(idx);
                if (blockPtr != 0) {
                    // Non-sparse; release the block.
                    vb.MarkBlockUnused(blockPtr);
                    BlocksUsed--;
                    SetIndexBlockNum(idx, 0);
                }
            }

            mEndOfFile = (int)newEof;

            // If appropriate, down-size the file.
            if (newLastBlockIndex < PTRS_PER_IBLOCK && StorageType == ProDOS.StorageType.Tree) {
                // Downsize tree to sapling.  Set the key pointer to the first index block, and
                // discard the master index block.
                Debug.Assert(mMasterIndexBlock != null && mCachedIndexBlock != null);
                KeyBlock = CacheTreeIndex(0);       // get the first index block into the cache
                Debug.Assert(KeyBlock != 0);        // (first block can't be sparse)
                mIndexBlock = mCachedIndexBlock;
                mCachedIndexBlock = null;
                mCachedIndexIndex = -1;

                vb.MarkBlockUnused(mMasterIndexBlock.BlockNum);
                BlocksUsed--;
                mMasterIndexBlock = null;
                StorageType = ProDOS.StorageType.Sapling;
            }

            if (newLastBlockIndex == 0 && StorageType == ProDOS.StorageType.Sapling) {
                // Downsize sapling to seedling.  Set the key pointer to the first block, and
                // discard the index block.
                Debug.Assert(mIndexBlock != null);
                KeyBlock = mIndexBlock.GetBlockPtr(0);
                Debug.Assert(KeyBlock != 0);        // (first block can't be sparse)

                vb.MarkBlockUnused(mIndexBlock.BlockNum);
                BlocksUsed--;
                mIndexBlock = null;
                StorageType = ProDOS.StorageType.Seedling;
            }

            // Flushing changes seems reasonable, given the potential magnitude of the changes.
            Flush();
        }

        // Stream
        public override long Seek(long seekOff, SeekOrigin origin) {
            CheckValid();
            if (seekOff < -ProDOS.MAX_FILE_LEN || seekOff > ProDOS.MAX_FILE_LEN) {
                throw new ArgumentOutOfRangeException(nameof(seekOff), seekOff, "invalid offset");
            }

            long newPos;
            switch (origin) {
                case SeekOrigin.Begin:
                    newPos = seekOff;
                    break;
                case SeekOrigin.Current:
                    newPos = mMark + seekOff;
                    break;
                case SeekOrigin.End:
                    newPos = mEndOfFile + seekOff;
                    break;
                case SEEK_ORIGIN_DATA:
                    newPos = SeekDataOrHole((int)seekOff, true);
                    break;
                case SEEK_ORIGIN_HOLE:
                    newPos = SeekDataOrHole((int)seekOff, false);
                    break;
                default:
                    throw new ArgumentException("Invalid seek mode");
            }

            // It's okay to be positioned one byte past the end of the file.  The largest file is
            // 0x00ffffff, so the last byte is at mark=0x00fffffe, and we want to allow 0x00ffffff.
            if (newPos < 0 || newPos > ProDOS.MAX_FILE_LEN) {
                throw new IOException("Invalid seek offset " + newPos);
            }
            mMark = (int)newPos;
            return newPos;
        }

        /// <summary>
        /// Finds the next data area or hole, starting from the specified absolute file offset.
        /// </summary>
        /// <param name="offset">Initial offset.</param>
        /// <param name="findData">If true, look for data; if false, for a hole.</param>
        /// <returns>Offset to data or hole.  If it's in the current block, the current offset
        ///   will be returned.  Otherwise, the offset of the start of the disk block (or
        ///   block-sized hole) will be returned.</returns>
        private int SeekDataOrHole(int offset, bool findData) {
            while (offset < mEndOfFile) {
                int blockIndex = offset / BLOCK_SIZE;
                uint blockNum = GetBlockNum(blockIndex);

                if (blockNum == 0) {
                    if (!findData) {
                        // in a hole
                        return offset;
                    }
                } else {
                    if (findData) {
                        // in a data region
                        return offset;
                    }
                }

                // Advance to next block, aligning the position with the block boundary.
                offset = (offset + BLOCK_SIZE) & ~(BLOCK_SIZE - 1);
            }
            if (offset >= mEndOfFile) {
                // We're positioned in the "hole" at the end of the file.  There's no more
                // data to be found, so return EOF either way.
                return mEndOfFile;
            }
            return offset;
        }

        // IDisposable generic finalizer.
        ~ProDOS_FileDesc() {
            Dispose(false);
        }
        protected override void Dispose(bool disposing) {
            bool tryToSave = false;

            if (disposing) {
                // Explicit close, via Close() call or "using" statement.
                if (FileEntry != null) {
                    // First close/dispose.
                    tryToSave = true;
                } else {
                    // We've already been invalidated, nothing to do.  We probably got called
                    // twice because the developer called Close() within a "using".  I don't
                    // think there's a need to throw things if this happens.
                }
            } else {
                // Finalizer dispose.
                if (FileEntry == null) {
                    // We're already disposed... why are we here?
                    Debug.Assert(false, "Finalization didn't get suppressed?");
                } else {
                    // Developer failed to close the file, and somehow the filesystem either
                    // lost its reference to us (or never had one, because we failed mid-open),
                    // or is getting finalized as well.  We could try a last desperate attempt,
                    // but if the filesystem doesn't know about us then it'll reject the call,
                    // and if the filesystem is getting finalized then something is likely to
                    // fail (the order in which objects are finalized is indeterminate).
                    Debug.WriteLine("NOTE: GC disposing of file desc object " + this);
                    Debug.Assert(false, "Finalizing unclosed fd: " + this);
                }
            }

            if (tryToSave) {
                Debug.Assert(FileEntry != null);
                if (FileEntry.IsChangePending && mIsReadOnly) {
                    // This should only happen if the code changed an attribute,
                    // like the file type, while the file was open.  It shouldn't have been
                    // caused by our read-only accesses.  Put up a warning message just in
                    // case the pending changes aren't expected.
                    Debug.WriteLine("NOTE: dirent changes pending for read-only file");
                    Debug.Assert(false);        // TODO: remove
                }

                // Flush all pending changes.  This will fail if the file entry object has
                // been invalidated.
                try {
                    Flush();
                } catch (Exception) {
                    Debug.Assert(false, "FileDesc dispose-time flush failed: " + DebugPathName);
                }

                if (!mInternalOpen) {
                    try {
                        // Tell the OS to forget about us.
                        FileSystem.CloseFile(this);
                    } catch (Exception ex) {
                        // Oh well.
                        Debug.WriteLine("FileSystem.CloseFile from fd close failed: " + ex.Message);
                    }
                }
            }

            // Mark the fd invalid so future calls will crash or do nothing.
            Invalidate();
        }

        /// <summary>
        /// Marks the file descriptor invalid, clearing various fields to ensure something bad
        /// will happen if we try to use it.
        /// </summary>
        private void Invalidate() {
#pragma warning disable CS8625
            FileSystem = null;
            FileEntry = null;
#pragma warning restore CS8625
            KeyBlock = uint.MaxValue;
            mIndexBlock = null;
            mMasterIndexBlock = null;
            mCachedIndexBlock = null;
            mDirBlocks = null;
        }

        /// <summary>
        /// Throws an exception if the file descriptor has been invalidated.
        /// </summary>
        private void CheckValid() {
            if (FileEntry == null || !FileEntry.IsValid) {
                throw new ObjectDisposedException("File descriptor has been closed (" +
                    DebugPathName + ")");
            }
        }

        // DiskFileStream
        public override bool DebugValidate(IFileSystem fs, IFileEntry entry) {
            Debug.Assert(entry != null && entry != IFileEntry.NO_ENTRY);
            if (FileSystem == null || FileEntry == null) {
                return false;       // we're invalid
            }
            return (fs == FileSystem && entry == FileEntry);
        }

        public override string ToString() {
            string stStr;
            switch (StorageType) {
                case ProDOS.StorageType.Seedling: stStr = "seed"; break;
                case ProDOS.StorageType.Sapling: stStr = "sapl"; break;
                case ProDOS.StorageType.Tree: stStr = "tree"; break;
                default: stStr = "????"; break;
            }
            return "[ProDOS_FileDesc '" + DebugPathName + "' isRsrc=" + IsRsrcFork +
                (FileEntry == null ? " *INVALID*" : "") +
                ", storage=" + stStr +
                ", iblk=" + (mIndexBlock == null ?
                    "NA" : mIndexBlock.IsDirty ? "dirty" : "clean") +
                ", mblk=" + (mMasterIndexBlock == null ?
                    "NA" : mMasterIndexBlock.IsDirty ? "dirty" : "clean") +
                ", cblk=" + (mCachedIndexBlock == null ?
                    "NA" : mCachedIndexBlock.IsDirty ? "dirty" : "clean") +
                "]";
        }
    }
}
