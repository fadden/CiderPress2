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
using static DiskArc.FS.HFS_Struct;

namespace DiskArc.FS {
    /// <summary>
    /// Internal class to manage the HFS volume bitmap.
    /// </summary>
    /// <remarks>
    /// This is very similar to ProDOS, with two key differences: (1) the meaning of the bits
    /// is inverted; (2) the system areas are not spanned by allocation blocks, so allocating
    /// block 0 is valid (though in practice it will always be part of the catalog file, and
    /// allocated when the volume is created).
    /// </remarks>
    internal class HFS_VolBitmap {
        /// <summary>
        /// Logical block number of start of bitmap (expected to be 3).
        /// </summary>
        public ushort StartBlock { get { return mMDB.VolBitmapStart; } }

        /// <summary>
        /// Total number of allocation blocks in the volume (max 65535).
        /// </summary>
        public int NumAllocBlocks { get { return mMDB.TotalBlocks; } }

        /// <summary>
        /// Number of logical blocks in the volume bitmap, determined by the volume size.
        /// </summary>
        public int NumBitmapBlocks { get; private set; }

        /// <summary>
        /// Volume usage map.
        /// </summary>
        public VolumeUsage VolUsage { get; private set; }

        /// <summary>
        /// Reference to MDB object.
        /// </summary>
        private HFS_MDB mMDB;

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

        /// <summary>
        /// True if volume usage tracking is enabled.
        /// </summary>
        private bool mDoTrackUsage;


        /// <summary>
        /// Constructor.  This must be followed by a call to LoadBitmap or InitNewBitmap.
        /// </summary>
        /// <param name="mdb">MDB reference.</param>
        /// <param name="chunkSource">Data source.</param>
        /// <param name="doTrackUsage">If set, track usage.</param>
        public HFS_VolBitmap(HFS_MDB mdb, IChunkAccess chunkSource, bool doTrackUsage) {
            mMDB = mdb;
            mChunkSource = chunkSource;
            mDoTrackUsage = doTrackUsage;

            const int BITS_PER_BLOCK = BLOCK_SIZE * 8;
            NumBitmapBlocks = (NumAllocBlocks + BITS_PER_BLOCK - 1) / BITS_PER_BLOCK;

            // Create an empty usage map initially.  This is replaced with a full map when we
            // load the volume bitmap, but only if we're tracking usage.
            VolUsage = new VolumeUsage(0);

            mVolBitmap = RawData.EMPTY_BYTE_ARRAY;      // allocated during load from disk
            mDirtyFlags = new bool[NumBitmapBlocks];
        }

#if DEBUG
        ~HFS_VolBitmap() { Debug.Assert(!HasUnflushedChanges); }
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

            // Clear all the block dirty flags.
            for (int i = 0; i < mDirtyFlags.Length; i++) {
                mDirtyFlags[i] = false;
            }

            // Check for bytes with set bits past the end.  This doesn't check the bits
            // in the last partial byte (if any).
            for (int boff = (NumAllocBlocks + 7) / 8; boff < mVolBitmap.Length; boff++) {
                if (mVolBitmap[boff] != 0) {
                    // Not dangerous, but could indicate something is doing updates badly.
                    notes.AddW("Found junk at end of volume bitmap");
                    break;
                }
            }

            if (mDoTrackUsage) {
                VolUsage = new VolumeUsage(NumAllocBlocks);
                for (uint i = 0; i < NumAllocBlocks; i++) {
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

            // Allocate bitmap.  All values are initially zero, indicating blocks are free.
            mVolBitmap = new byte[NumBitmapBlocks * BLOCK_SIZE];
            if (mDoTrackUsage) {
                VolUsage = new VolumeUsage(NumAllocBlocks);
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
            while (blockNum < NumAllocBlocks) {
                if (blockNum + 32 <= NumAllocBlocks) {
                    // At least 32 blocks left to check.  Grab four bytes and count the unset bits.
                    uint bi = blockNum / 8;
                    int fourBytes = mVolBitmap[bi] | mVolBitmap[bi + 1] << 8 |
                        mVolBitmap[bi + 2] << 16 | mVolBitmap[bi + 3] << 24;
                    if (fourBytes == 0) {
                        freeCount += 32;
                    } else if ((uint)fourBytes == 0xffffffff) {
                        // all in use
                    } else {
                        freeCount += BitTwiddle.CountZeroBits(fourBytes);
                    }
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
            Debug.Assert(blockNum < NumAllocBlocks);

            // One bit per block, 1=used, 0=free.
            uint offset = blockNum / 8;
            int mask = 0x80 >> ((int)blockNum & 0x07);
            if ((mVolBitmap[offset] & mask) != 0) {
                return true;
            } else {
                return false;
            }
        }

        public void MarkBlockUsed(uint blockNum, IFileEntry entry) {
            SetBlockState(blockNum, true);
            if (mDoTrackUsage) {
                VolUsage.AllocChunk(blockNum, entry);
            }
        }
        public void MarkBlockUnused(uint blockNum) {
            SetBlockState(blockNum, false);
            if (mDoTrackUsage) {
                VolUsage.FreeChunk(blockNum);
            }
        }

        public void MarkExtentUsed(ExtDescriptor desc, IFileEntry entry) {
            for (uint i = 0; i < desc.xdrNumAblks; i++) {
                MarkBlockUsed(desc.xdrStABN + i, entry);
            }
        }
        private void MarkExtentUnused(ExtDescriptor desc) {
            for (uint i = 0; i < desc.xdrNumAblks; i++) {
                MarkBlockUnused(desc.xdrStABN + i);
            }
        }

        /// <summary>
        /// Marks the specified block as used or unused in the volume bitmap.
        /// </summary>
        private void SetBlockState(uint blockNum, bool inUse) {
            Debug.Assert(blockNum >= 0 && blockNum < NumAllocBlocks);

            uint offset = blockNum / 8;
            int mask = 0x80 >> ((int)blockNum & 0x07);
            if (inUse) {
                mVolBitmap[offset] |= (byte)mask;
            } else {
                mVolBitmap[offset] &= (byte)~mask;
            }

            // Mark the bitmap block as dirty.
            mDirtyFlags[offset / BLOCK_SIZE] = true;
        }

        /// <summary>
        /// Attempts to allocate a full clump.  If that's not possible, the largest available
        /// space is returned.
        /// </summary>
        /// <param name="clumpBlocks">Clump size, in allocation blocks.</param>
        /// <returns>Extent descriptor.</returns>
        /// <exception cref="DiskFullException">No space available to expand.</exception>
        public ExtDescriptor AllocBlocks(uint clumpBlocks, uint searchStart, IFileEntry entry) {
            Debug.Assert(searchStart < NumAllocBlocks);

            // If the block we were given as the search starting point is free, back up to see
            // if we trimmed something off the end of the previous alloc.
            if (!IsBlockInUse(searchStart)) {
                // Don't run off the top of the list, and don't back up into the used blocks.
                while (searchStart > 0 && !IsBlockInUse(searchStart - 1)) {
                    searchStart--;
                }
            }

            uint bestPos = 0;
            uint bestLen = 0;
            uint curPos = searchStart;
            uint endPos = (uint)NumAllocBlocks;
            bool wrapped = false;

            while (true) {
                // Skip past used blocks.
                while (curPos < endPos) {
                    if (!IsBlockInUse(curPos)) {
                        break;
                    }
                    curPos++;
                }

                // Count available blocks, stopping if we find enough.  (If we hit the end of
                // the bitmap above, this will do nothing.)
                uint startPos = curPos;
                while (curPos < endPos && curPos - startPos < clumpBlocks) {
                    if (IsBlockInUse(curPos)) {
                        break;
                    }
                    curPos++;
                }

                uint freeLen = curPos - startPos;
                if (freeLen > bestLen) {
                    // Record what we found, then bail out or keep looking.
                    bestPos = startPos;
                    bestLen = freeLen;
                    if (bestLen == clumpBlocks) {
                        // Success.
                        break;
                    }
                }

                if (!wrapped && curPos == endPos) {
                    // Reached the end for the first time.  Start over at the beginning.
                    curPos = 0;
                    wrapped = true;
                }
                if (wrapped && curPos >= searchStart) {
                    // Give up.
                    break;
                }
            }

            if (bestLen == 0) {
                Debug.Assert(CalcFreeBlocks() == 0);
                throw new DiskFullException("Disk full: no allocation blocks available");
            } else if (bestLen < clumpBlocks) {
                //Debug.WriteLine("short alloc");
            }

            ExtDescriptor desc = new ExtDescriptor();
            desc.xdrStABN = (ushort)bestPos;
            desc.xdrNumAblks = (ushort)bestLen;

            MarkExtentUsed(desc, entry);
            mMDB.FreeBlocks -= (ushort)bestLen;
            return desc;
        }

        /// <summary>
        /// Releases blocks from an extent.
        /// </summary>
        public void ReleaseBlocks(ExtDescriptor desc) {
            MarkExtentUnused(desc);
            mMDB.FreeBlocks += desc.xdrNumAblks;
        }
    }
}
