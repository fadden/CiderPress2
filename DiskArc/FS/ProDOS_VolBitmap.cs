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

namespace DiskArc.FS {
    /// <summary>
    /// Internal class to manage the ProDOS block allocation bitmap.
    /// </summary>
    internal class ProDOS_VolBitmap {
        /// <summary>
        /// Volume bitmap's first block.
        /// </summary>
        public uint StartBlock { get; private set; }

        /// <summary>
        /// Total number of blocks in the volume (max 65535).
        /// </summary>
        public int TotalBlocks { get; private set; }

        /// <summary>
        /// Number of blocks in the volume bitmap, determined by the volume size.
        /// </summary>
        public int NumBitmapBlocks { get; private set; }

        /// <summary>
        /// Volume usage map.
        /// </summary>
        public VolumeUsage VolUsage { get; private set; }

        /// <summary>
        /// True if a transaction is open.
        /// </summary>
        public bool IsTransactionOpen { get; private set; }

        /// <summary>
        /// Filesystem data source.
        /// </summary>
        private IChunkAccess mChunkSource;

        /// <summary>
        /// Bitmap data, as it appears on disk.
        /// </summary>
        private byte[] mVolBitmap;

        /// <summary>
        /// True if we have unsaved changes in volume bitmap block N.
        /// </summary>
        private bool[] mDirtyFlags;

        /// <summary>
        /// True if there are any un-flushed changes.
        /// </summary>
        public bool HasUnflushedChanges {
            get {
                foreach (bool dirty in mDirtyFlags) {
                    if (dirty) {
                        return true;
                    }
                }
                return false;
            }
        }

        // Record of events to play back to "undo" a transaction.
        // TODO(maybe): we don't try to manage the block dirty flags, which might be set
        //   before the transaction begins.  We could just save/restore a copy of the bool[].
        private class UndoRecord {
            public uint BlockNum { get; private set; }
            public bool IsAlloc { get; private set; }
            public IFileEntry? FileEntry { get; private set; }

            public UndoRecord(uint blockNum, bool isAlloc, IFileEntry? fileEntry) {
                BlockNum = blockNum;
                IsAlloc = isAlloc;
                FileEntry = fileEntry;
            }
        }
        private List<UndoRecord> mUndoLog = new List<UndoRecord>();

        /// <summary>
        /// True if volume usage tracking is enabled.
        /// </summary>
        public bool DoTrackUsage { get; private set; }


        /// <summary>
        /// Constructor.  This must be followed by a call to LoadBitmap or InitNewBitmap.
        /// </summary>
        /// <param name="totalBlocks">Total number of blocks in the filesystem.</param>
        /// <param name="startBlock">First block used to store volume bitmap data.</param>
        /// <param name="chunkAccess">Chunk data source.</param>
        /// <param name="doTrackUsage">If set, track usage.</param>
        public ProDOS_VolBitmap(int totalBlocks, uint startBlock, IChunkAccess chunkAccess,
                bool doTrackUsage) {
            Debug.Assert(totalBlocks <= ProDOS.MAX_VOL_SIZE / BLOCK_SIZE);
            Debug.Assert(startBlock > ProDOS.VOL_DIR_START_BLK && startBlock < totalBlocks);
            Debug.Assert(chunkAccess.FormattedLength >= totalBlocks * BLOCK_SIZE);

            StartBlock = startBlock;
            TotalBlocks = totalBlocks;
            mChunkSource = chunkAccess;
            DoTrackUsage = doTrackUsage;

            const int BITS_PER_BLOCK = BLOCK_SIZE * 8;
            NumBitmapBlocks = (TotalBlocks + BITS_PER_BLOCK - 1) / BITS_PER_BLOCK;

            // Create an empty usage map initially.  This is replaced with a full map when we
            // load the volume bitmap, but only if we're tracking usage.
            VolUsage = new VolumeUsage(0);

            mVolBitmap = RawData.EMPTY_BYTE_ARRAY;      // allocated during load from disk
            mDirtyFlags = new bool[NumBitmapBlocks];
        }

#if DEBUG
        ~ProDOS_VolBitmap() { Debug.Assert(!HasUnflushedChanges); }
#endif

        /// <summary>
        /// Reads the volume bitmap from disk.
        /// </summary>
        /// <remarks>
        /// If any of the blocks fails to read, we ignore the whole thing.  There's no value
        /// in processing only part of the bitmap.  We leave the bitmap array reference
        /// pointing to an empty byte array.
        /// </remarks>
        /// <exception cref="BadBlockException">Failure to read block from chunk source.</exception>
        /// <exception cref="IOException">Disk access failure.</exception>
        public void LoadBitmap(Notes notes) {
            Debug.Assert(StartBlock > 0 && NumBitmapBlocks > 0);

            try {
                byte[] bits = new byte[NumBitmapBlocks * BLOCK_SIZE];
                mChunkSource.ReadBlocks(StartBlock, NumBitmapBlocks, bits, 0);
                mVolBitmap = bits;
            } catch (IOException ex) {
                notes.AddE("Unable to read volume bitmap: " + ex.Message);
                throw;
            }
            //Debug.WriteLine("Loaded " + NumBitmapBlocks + " blocks from vol bitmap at " +
            //    StartBlock);

            // Clear all the block dirty flags.
            for (int i = 0; i < mDirtyFlags.Length; i++) {
                mDirtyFlags[i] = false;
            }

            // Check for bytes with set bits past the end.  This doesn't check the bits
            // in the last partial byte (if any).
            for (int boff = (TotalBlocks + 7) / 8; boff < mVolBitmap.Length; boff++) {
                if (mVolBitmap[boff] != 0) {
                    // Not dangerous, but could indicate something is doing updates badly.
                    notes.AddW("Found junk at end of volume bitmap");
                    break;
                }
            }

            if (DoTrackUsage) {
                VolUsage = new VolumeUsage(TotalBlocks);
                for (uint i = 0; i < TotalBlocks; i++) {
                    if (IsBlockInUse(i)) {
                        VolUsage.MarkInUse(i);
                    }
                }
            }
        }

        /// <summary>
        /// Flushes pending volume bitmap changes to disk.
        /// </summary>
        /// <exception cref="BadBlockException">Failure to write block from chunk source
        ///   (should be impossible).</exception>
        /// <exception cref="IOException">General chunk source I/O failure.</exception>
        public void Flush() {
            for (int i = 0; i < NumBitmapBlocks; i++) {
                if (!mDirtyFlags[i]) {
                    continue;
                }
                mChunkSource.WriteBlock((uint)(StartBlock + i), mVolBitmap, i * BLOCK_SIZE);
                mDirtyFlags[i] = false;
            }
        }

        /// <summary>
        /// Initializes a new volume bitmap, marking all blocks as unused.  Used when
        /// formatting a volume.
        /// </summary>
        public void InitNewBitmap() {
            Debug.Assert(!mChunkSource.IsReadOnly);

            // Allocate bitmap.  All values are initially zero, indicating block is in use.
            mVolBitmap = new byte[NumBitmapBlocks * BLOCK_SIZE];
            if (DoTrackUsage) {
                VolUsage = new VolumeUsage(TotalBlocks);
            }

            // Mark all blocks as free (bit=1).  This is really just a memset plus some fiddling
            // around in the last byte.
            uint blockNum = 0;
            while (blockNum < TotalBlocks) {
                if (blockNum + 8 <= TotalBlocks) {
                    // Set 8 blocks at once.
                    mVolBitmap[blockNum >> 3] = 0xff;
                    blockNum += 8;
                } else {
                    // Set the last few.
                    MarkBlockUnused(blockNum);
                    blockNum++;
                }
            }

            // Mark all bitmap storage blocks as dirty.
            for (int i = 0; i < mDirtyFlags.Length; i++) {
                mDirtyFlags[i] = true;
            }
        }

        /// <summary>
        /// Counts up the number of unallocated blocks.
        /// </summary>
        public int CalcFreeBlocks() {
            uint blockNum = 0;
            int freeCount = 0;
            while (blockNum < TotalBlocks) {
                if (blockNum + 32 <= TotalBlocks) {
                    // At least 32 blocks left to check.  Grab four bytes and count the set bits.
                    uint bi = blockNum / 8;
                    int fourBytes = mVolBitmap[bi] | mVolBitmap[bi + 1] << 8 |
                        mVolBitmap[bi + 2] << 16 | mVolBitmap[bi + 3] << 24;
                    freeCount += BitTwiddle.CountOneBits(fourBytes);
                    blockNum += 32;
                } else {
                    if (!IsBlockInUse(blockNum)) {
                        freeCount++;
                    }
                    blockNum++;
                }
            }
            return freeCount;
        }

        /// <summary>
        /// Determines whether the volume bitmap indicates that a block is in use.
        /// </summary>
        /// <param name="blockNum">Block number.</param>
        /// <returns>True if the block is marked as being in use.</returns>
        public bool IsBlockInUse(uint blockNum) {
            Debug.Assert(blockNum < TotalBlocks);

            // One bit per block, 0=used, 1=free.
            uint offset = blockNum / 8;
            int mask = 0x80 >> ((int)blockNum & 0x07);
            if ((mVolBitmap[offset] & mask) == 0) {
                return true;
            } else {
                return false;
            }
        }

        public void MarkBlockUsed(uint blockNum, IFileEntry entry) {
            if (IsTransactionOpen) {
                // Record previous state.
                IFileEntry? usage = null;
                if (DoTrackUsage) {
                    VolUsage.GetUsage(blockNum);
                }
                mUndoLog.Add(new UndoRecord(blockNum, IsBlockInUse(blockNum), usage));
            }
            SetBlockState(blockNum, true);
            if (DoTrackUsage) {
                VolUsage.AllocChunk(blockNum, entry);
            }
        }
        public void MarkBlockUnused(uint blockNum) {
            if (IsTransactionOpen) {
                // Record previous state.
                IFileEntry? usage = null;
                if (DoTrackUsage) {
                    usage = VolUsage.GetUsage(blockNum);
                }
                mUndoLog.Add(new UndoRecord(blockNum, IsBlockInUse(blockNum), usage));
            }
            SetBlockState(blockNum, false);
            if (DoTrackUsage) {
                VolUsage.FreeChunk(blockNum);
            }
        }

        /// <summary>
        /// Marks the specified block as used or unused in the volume bitmap.
        /// </summary>
        private void SetBlockState(uint blockNum, bool inUse) {
            Debug.Assert(blockNum >= 0 && blockNum < TotalBlocks);

            uint offset = blockNum / 8;
            int mask = 0x80 >> ((int)blockNum & 0x07);
            if (inUse) {
                mVolBitmap[offset] &= (byte)~mask;
            } else {
                mVolBitmap[offset] |= (byte)mask;
            }

            // Mark the bitmap block as dirty.
            mDirtyFlags[offset / BLOCK_SIZE] = true;
        }

        /// <summary>
        /// Allocates a new block.  This adds the block to the pending transaction list,
        /// and updates the volume bitmap, but does not write anything to the ChunkSource.
        /// </summary>
        /// <param name="entry">File entry that the block is assigned to, or NO_ENTRY for
        ///   system storage.</param>
        /// <returns>Block number.</returns>
        /// <exception cref="DiskFullException">Unable to allocate block.</exception>
        /// <exception cref="DAException">Internal error: next available block is
        ///   0, 1, or 2.</exception>
        public uint AllocBlock(IFileEntry entry) {
            // Search by bytes, then bits.
            //
            // TODO(maybe): this shows up in the performance profiler when running the test
            //   suite, which likes to fill 32MB volumes with large files.  There may be a
            //   benefit to keeping track of the lowest-numbered free block, so we don't have to
            //   walk the full set every time.
            int maxOffset = (TotalBlocks + 7) / 8;
            for (int offset = 0; offset < maxOffset; offset++) {
                byte mapVal = mVolBitmap[offset];
                if (mapVal != 0) {
                    // At least one block is free.
                    int subBlock = 0;
                    while ((mapVal & 0x80) == 0) {
                        subBlock++;
                        mapVal <<= 1;
                    }

                    uint blockNum = (uint)(offset * 8 + subBlock);
                    Debug.Assert(!IsBlockInUse(blockNum));
                    if (blockNum <= ProDOS.VOL_DIR_START_BLK) {
                        // This should never happen unless we allowed writes to a disk with
                        // block 0/1/2 marked as free.  More likely we have a bug somewhere
                        // that changed the allocation status of block 0.
                        throw new DAException("Attempt to allocate block " + blockNum);
                    } else if (blockNum >= TotalBlocks) {
                        // This could happen if there were some junk bits set in the last byte.
                        throw new DiskFullException("Disk full (+junk)");
                    }
                    MarkBlockUsed(blockNum, entry);
                    Debug.Assert(blockNum <= 65535);
                    //Debug.WriteLine("ALLOC: " + blockNum);
                    return blockNum;
                }
            }
            Debug.WriteLine("Disk full");
            throw new DiskFullException("Disk full");
        }

        /// <summary>
        /// Begins a block allocation transaction.  Transactions must exist only within a
        /// single API call.  Otherwise, aborting a transaction in one call could undo
        /// allocations made in an earlier, successful call.
        /// </summary>
        /// <exception cref="DAException">An update is already open.</exception>
        public void BeginUpdate() {
            if (IsTransactionOpen) {
                throw new DAException("Volume bitmap is in the middle of a commit");
            }

            Debug.Assert(mUndoLog.Count == 0);
            IsTransactionOpen = true;
        }

        /// <summary>
        /// Finishes a block allocation transaction, committing the changes.  Does not flush
        /// the changes to disk.
        /// </summary>
        /// <exception cref="DAException">An update has not been started.</exception>
        public void EndUpdate() {
            if (!IsTransactionOpen) {
                throw new DAException("Attempt to end update while none in progress");
            }

            mUndoLog.Clear();
            IsTransactionOpen = false;
        }

        /// <summary>
        /// Aborts a block allocation transaction, discarding the changes by copying the
        /// backup copy over the original.
        /// </summary>
        public void AbortUpdate() {
            if (!IsTransactionOpen) {
                // Don't throw here -- we might have ended the update and then thrown while
                // flushing the bitmap to disk.
                Debug.WriteLine("VolBitmap asked to abort but no change in progress");
                return;
            }

            IsTransactionOpen = false;

            // Play the undo log backward, for correct behavior when a single block is modified
            // more than once.  If we allocated a block then freed it, the undo log should
            // undo the free then undo the alloc.
            for (int i = mUndoLog.Count - 1; i >= 0; i--) {
                UndoRecord rec = mUndoLog[i];
                if (rec.IsAlloc) {
                    Debug.Assert(rec.FileEntry != null);
                    //Debug.WriteLine(" UNDO: restore alloc " + rec.BlockNum + ": " + rec.FileEntry);
                    MarkBlockUsed(rec.BlockNum, rec.FileEntry);
                } else {
                    //Debug.WriteLine(" UNDO: restore free " + rec.BlockNum);
                    MarkBlockUnused(rec.BlockNum);
                }
            }
            mUndoLog.Clear();
        }
    }
}
