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

namespace DiskArc.FS {
    /// <summary>
    /// Track allocation block usage.
    /// </summary>
    /// <remarks>
    /// <para>CP/M filesystems don't have a volume bitmap.  Usage information is stored entirely
    /// in the directory extent records.  We use that information to generate a bitmap for
    /// efficiency when allocating blocks and calculating free space.</para>
    /// <para>Allocation blocks span two or more disk blocks.  Allocation block 0 holds the start
    /// of the disk directory.  On 5.25" Apple II disks, allocation blocks wrap around to the
    /// start of the disk.</para>
    /// </remarks>
    internal class CPM_AllocMap {
        /// <summary>
        /// Detailed volume usage map.
        /// </summary>
        public VolumeUsage VolUsage { get; }

        /// <summary>
        /// Number of alloc blocks on the volume.
        /// </summary>
        public int TotalAllocBlocks { get; private set; }

        /// <summary>
        /// Space available, in bytes.
        /// </summary>
        public long FreeSpace { get { return CalcFreeBlocks() * (long)mAllocUnitSize; } }

        /// <summary>
        /// Allocation unit size, in bytes.
        /// </summary>
        private uint mAllocUnitSize;

        /// <summary>
        /// In-use bitmap (0=free, 1=used).
        /// </summary>
        private byte[] mVolBitmap;


        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="volumeSize">Size of the filesystem, in bytes.</param>
        public CPM_AllocMap(long volumeSize) {
            if (!CPM.GetDiskParameters(volumeSize, out uint allocUnit, out uint dirStartBlock,
                    out uint dirBlockCount, out bool doBlocksWrap)) {
                throw new DAException("should not be here");
            }
            mAllocUnitSize = allocUnit;

            if (doBlocksWrap) {
                TotalAllocBlocks = (int)(volumeSize / allocUnit);
            } else {
                TotalAllocBlocks = (int)((volumeSize - dirStartBlock * BLOCK_SIZE) / allocUnit);
            }

            mVolBitmap = new byte[(TotalAllocBlocks + 7) / 8];
            if (mVolBitmap.Length * 8 != TotalAllocBlocks) {
                // This means we have some extra bits in the last byte, because the number
                // of alloc blocks wasn't a multiple of 8.  5.25" disks have 140 1KB alloc blocks,
                // so we want to mark the last 4 bits as in-use so we don't try to allocate them.
                byte mask = (byte)(0xff >> (TotalAllocBlocks % 8));
                mVolBitmap[mVolBitmap.Length - 1] = mask;
            }
            Debug.Assert(CalcFreeBlocks() == TotalAllocBlocks);
            VolUsage = new VolumeUsage(TotalAllocBlocks);
        }

        /// <summary>
        /// Counts up the number of unallocated blocks.
        /// </summary>
        private int CalcFreeBlocks() {
            uint allocNum = 0;
            int freeCount = 0;
            while (allocNum < TotalAllocBlocks) {
                if (allocNum + 32 <= TotalAllocBlocks) {
                    // At least 32 blocks left to check.  Grab four bytes and count the unset bits.
                    uint bi = allocNum / 8;
                    int fourBytes = mVolBitmap[bi] | mVolBitmap[bi + 1] << 8 |
                        mVolBitmap[bi + 2] << 16 | mVolBitmap[bi + 3] << 24;
                    freeCount += BitTwiddle.CountZeroBits(fourBytes);
                    allocNum += 32;
                } else {
                    if (!IsAllocBlockInUse(allocNum)) {
                        freeCount++;
                    }
                    allocNum++;
                }
            }
            return freeCount;
        }

        /// <summary>
        /// Determines whether the volume bitmap indicates that a block is in use.
        /// </summary>
        /// <param name="allocNum">Allocation block number.</param>
        /// <returns>True if the block is marked as being in use.</returns>
        public bool IsAllocBlockInUse(uint allocNum) {
            Debug.Assert(allocNum < TotalAllocBlocks);

            // One bit per block, 0=free, 1=used.
            uint offset = allocNum / 8;
            int mask = 0x80 >> ((int)allocNum & 0x07);
            if ((mVolBitmap[offset] & mask) != 0) {
                return true;
            } else {
                return false;
            }
        }

        public void MarkAllocBlockUsed(uint allocNum, IFileEntry entry) {
            SetBlockState(allocNum, true);
            VolUsage.AllocChunk(allocNum, entry);
        }
        public void MarkAllocBlockUnused(uint allocNum) {
            Debug.Assert(allocNum > 0 && allocNum < TotalAllocBlocks, "Mark unused " + allocNum);
            SetBlockState(allocNum, false);
            VolUsage.FreeChunk(allocNum);
        }

        /// <summary>
        /// Marks a block as in-use during the initial file scan.  A clash here means the disk
        /// has conflicting entries.  A clash elsewhere indicates an internal block allocation
        /// error.
        /// </summary>
        /// <param name="allocNum">Allocation block to mark.</param>
        /// <param name="entry">Associated file entry.</param>
        public void MarkUsedByScan(uint allocNum, IFileEntry entry) {
            SetBlockState(allocNum, true);
            VolUsage.MarkInUse(allocNum);
            VolUsage.SetUsage(allocNum, entry);
        }

        /// <summary>
        /// Marks the specified allocation block as used or unused in the bitmap.
        /// </summary>
        private void SetBlockState(uint allocNum, bool inUse) {
            Debug.Assert(allocNum >= 0 && allocNum < TotalAllocBlocks);

            uint offset = allocNum / 8;
            int mask = 0x80 >> ((int)allocNum & 0x07);
            if (inUse) {
                mVolBitmap[offset] |= (byte)mask;
            } else {
                mVolBitmap[offset] &= (byte)~mask;
            }
        }

        /// <summary>
        /// Allocates a new block, if one is available.
        /// </summary>
        /// <param name="entry">File entry that the block is assigned to, or NO_ENTRY for
        ///   system storage.</param>
        /// <returns>Block number.</returns>
        /// <exception cref="DiskFullException">No free blocks are available.</exception>
        public uint AllocateAllocBlock(IFileEntry entry) {
            // Search by bytes, then bits.
            // TODO: keep track of last block allocated so we can avoid searching through start
            //   of list; reset pointer when a block is freed
            int maxOffset = (TotalAllocBlocks + 7) / 8;
            for (int offset = 0; offset < maxOffset; offset++) {
                byte mapVal = mVolBitmap[offset];
                if (mapVal != 0) {
                    // At least one block is free.
                    int subBlock = 0;
                    while ((mapVal & 0x80) != 0) {
                        subBlock++;
                        mapVal <<= 1;
                    }

                    uint allocNum = (uint)(offset * 8 + subBlock);
                    Debug.Assert(allocNum < TotalAllocBlocks);
                    Debug.Assert(!IsAllocBlockInUse(allocNum));
                    MarkAllocBlockUsed(allocNum, entry);
                    return allocNum;
                }
            }
            Debug.WriteLine("Disk full");
            throw new DiskFullException("Disk full");
        }
    }
}
