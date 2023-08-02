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

using static DiskArc.Defs;

namespace DiskArc.Multi {
    /// <summary>
    /// An IChunkAccess that provides access to a subset of another IChunkAccess.
    /// </summary>
    public class ChunkSubset : IChunkAccess {
        //
        // IChunkAccess interfaces.
        //

        public bool IsReadOnly => mBase.IsReadOnly;

        public bool IsModified {
            // Just pass through to base object.
            get { return mBase.IsModified; }
            set { mBase.IsModified = value; }
        }
        public long ReadCount => mBase.ReadCount;
        public long WriteCount => mBase.WriteCount;

        public long FormattedLength { get; private set; }

        public uint NumTracks { get; private set; }

        public uint NumSectorsPerTrack { get; private set; }

        public bool HasSectors { get; private set; }

        public bool HasBlocks { get; private set; }

        public SectorOrder FileOrder { get; set; }

        public SectorCodec? NibbleCodec => null;

        //
        // Innards.
        //

        private IChunkAccess mBase;
        public uint StartBlock { get; private set; }
        private bool mIsOzSpecial;
        private bool mIsOzFirstHalf;

        private byte[] mTmpBuf = new byte[BLOCK_SIZE];


        /// <summary>
        /// Private constructor.
        /// </summary>
        private ChunkSubset(IChunkAccess baseAcc) {
            mBase = baseAcc;
        }

        /// <summary>
        /// Creates a block-based chunk access object representing a subset of blocks.  Useful
        /// for multi-partition images.
        /// </summary>
        /// <param name="baseAccess">Chunk access object we are subsetting.</param>
        /// <param name="startBlock">Start block in base.</param>
        /// <param name="blockCount">Length of subset, in blocks.</param>
        /// <returns>New chunk access object.</returns>
        public static ChunkSubset CreateBlockSubset(IChunkAccess baseAccess, uint startBlock,
                uint blockCount) {
            Debug.Assert(baseAccess is not GatedChunkAccess);
            if (!baseAccess.HasBlocks) {
                throw new ArgumentException("Base chunk access doesn't have blocks");
            }
            long baseBlockCount = baseAccess.FormattedLength / BLOCK_SIZE;
            if (startBlock >= baseBlockCount || startBlock > baseBlockCount - blockCount) {
                throw new ArgumentException("Invalid block range: start=" + startBlock +
                    " count=" + blockCount + " baseBlockCount=" + baseBlockCount);
            }

            ChunkSubset newChunk = new ChunkSubset(baseAccess);
            newChunk.StartBlock = startBlock;
            //newChunk.mBlockCount = blockCount;
            newChunk.HasSectors = false;
            newChunk.HasBlocks = true;
            newChunk.NumSectorsPerTrack = 0;
            newChunk.NumTracks = 0;
            newChunk.FormattedLength = (long)blockCount * BLOCK_SIZE;
            newChunk.FileOrder = SectorOrder.ProDOS_Block;

            return newChunk;
        }

        /// <summary>
        /// Creates a block-based chunk access object inside a track/sector-based chunk.  Useful
        /// for DOS+ProDOS/Pascal/CPM hybrids.
        /// </summary>
        /// <param name="baseAccess">Chunk access object we are subsetting.</param>
        /// <param name="numSectorsPerTrack">Number of sectors per track (16 or 32).</param>
        /// <param name="numTracks">Number of tracks (usually 35, 40, or 50).</param>
        /// <param name="order">File order; should match request type.</param>
        /// <returns>New chunk access object.</returns>
        public static ChunkSubset CreateBlocksOnTracks(IChunkAccess baseAccess,
                uint numSectorsPerTrack, uint numTracks, SectorOrder order) {
            if (!baseAccess.HasSectors) {
                throw new ArgumentException("Base chunk access doesn't have sectors");
            }
            if (numSectorsPerTrack != 16 && numSectorsPerTrack != 32) {
                throw new ArgumentException("We don't handle numSectors=" + numSectorsPerTrack);
            }
            if (order != SectorOrder.CPM_KBlock && order != SectorOrder.ProDOS_Block) {
                throw new ArgumentException("Unexpected sector order: " + order);
            }

            ChunkSubset newChunk = new ChunkSubset(baseAccess);
            newChunk.StartBlock = 0;
            newChunk.HasSectors = true;
            newChunk.HasBlocks = true;
            newChunk.NumSectorsPerTrack = numSectorsPerTrack;
            newChunk.NumTracks = numTracks;
            newChunk.FormattedLength = numSectorsPerTrack * numTracks * SECTOR_SIZE;
            newChunk.FileOrder = order;

            return newChunk;
        }

        /// <summary>
        /// Creates a track/sector-based chunk access object inside a block-based chunk object.
        /// Useful for DOS in ProDOS.
        /// </summary>
        /// <remarks>
        /// <para>Sectors are assumed to be in DOS order, so the first block holds T0S0, the second
        /// block holds T0S1, etc.  This means we're declaring a subset of a ProDOS-block-ordered
        /// file to be DOS-ordered.  This works, but we can't set HasBlocks to true because our
        /// local ReadBlock function doesn't know how to act like it's reading ProDOS blocks from
        /// a DOS disk.</para>
        /// </remarks>
        /// <param name="baseAccess">Chunk access object we are subsetting.</param>
        /// <param name="startBlock">Start block in base.</param>
        /// <param name="numSectorsPerTrack">Number of sectors per track (16 or 32).</param>
        /// <param name="numTracks">Number of tracks (usually 35, 40, or 50).</param>
        /// <returns>New chunk access object.</returns>
        public static ChunkSubset CreateTracksOnBlocks(IChunkAccess baseAccess, uint startBlock,
                uint numSectorsPerTrack, uint numTracks) {
            if (!baseAccess.HasBlocks) {
                throw new ArgumentException("Base chunk access doesn't have blocks");
            }
            if (numSectorsPerTrack != 16 && numSectorsPerTrack != 32) {
                throw new ArgumentException("We don't handle numSectors=" + numSectorsPerTrack);
            }
            long baseBlockCount = baseAccess.FormattedLength / BLOCK_SIZE;
            uint blockCount = numSectorsPerTrack * numTracks / 2;
            if (startBlock >= baseBlockCount || startBlock > baseBlockCount - blockCount) {
                throw new ArgumentException("Invalid block range: start=" + startBlock +
                    " count=" + blockCount + " baseBlockCount=" + baseBlockCount);
            }

            ChunkSubset newChunk = new ChunkSubset(baseAccess);
            newChunk.StartBlock = startBlock;
            newChunk.HasSectors = true;
            newChunk.HasBlocks = false;
            newChunk.NumSectorsPerTrack = numSectorsPerTrack;
            newChunk.NumTracks = numTracks;
            newChunk.FormattedLength = numSectorsPerTrack * numTracks * SECTOR_SIZE;
            newChunk.FileOrder = SectorOrder.DOS_Sector;

            return newChunk;
        }

        /// <summary>
        /// Creates a track/sector-based chunk access object inside a block-based chunk object.
        /// Each block holds one sector, in either the top half or bottom half.  Only used for
        /// OzDOS 800KB volumes.
        /// </summary>
        /// <param name="baseAccess">Chunk access object we are subsetting.</param>
        /// <param name="firstHalf">True if this volume uses the first half of each block, false
        ///   if it uses the second half.</param>
        /// <returns>New chunk access object.</returns>
        public static ChunkSubset CreateTracksOnSplitBlocks(IChunkAccess baseAccess,
                bool firstHalf) {
            if (!baseAccess.HasBlocks) {
                throw new ArgumentException("Base chunk access doesn't have blocks");
            }
            if (baseAccess.FormattedLength != 800 * 1024) {
                throw new ArgumentException("Can't put this on that");
            }

            ChunkSubset newChunk = new ChunkSubset(baseAccess);
            newChunk.StartBlock = 0;
            newChunk.HasSectors = true;
            newChunk.HasBlocks = false;
            newChunk.NumSectorsPerTrack = 32;
            newChunk.NumTracks = 50;
            newChunk.FormattedLength = 32 * 50 * SECTOR_SIZE;   // 400KB
            newChunk.FileOrder = SectorOrder.DOS_Sector;
            newChunk.mIsOzSpecial = true;
            newChunk.mIsOzFirstHalf = firstHalf;

            return newChunk;
        }

        private void CheckSectorArgs(uint trk, uint sct, bool isWrite) {
            if (!HasSectors) {
                throw new InvalidOperationException("No sectors");
            }
            if (trk > NumTracks) {
                throw new ArgumentOutOfRangeException("Track out of range: " + trk +
                    " (max " + NumTracks + ")");
            }
            if (sct > NumSectorsPerTrack) {
                throw new ArgumentOutOfRangeException("Sector out of range: " + sct +
                    " (max " + NumSectorsPerTrack + ")");
            }
            if (isWrite && IsReadOnly) {
                throw new InvalidOperationException("Chunk access is read-only");
            }
        }

        private void CheckBlockArgs(uint block, bool isWrite) {
            if (!HasBlocks) {
                throw new InvalidOperationException("No blocks");
            }
            if (block >= FormattedLength / BLOCK_SIZE) {
                throw new ArgumentOutOfRangeException("Block out of range: " + block);
            }
            if (isWrite && IsReadOnly) {
                throw new InvalidOperationException("Chunk access is read-only");
            }
        }

        // IChunkAccess
        public void ReadSector(uint trk, uint sct, byte[] data, int offset) {
            CheckSectorArgs(trk, sct, false);
            if (mBase.HasSectors) {
                mBase.ReadSector(trk, sct, data, offset);
            } else if (mBase.HasBlocks && mIsOzSpecial) {
                uint blockIndex = trk * NumSectorsPerTrack + sct;
                mBase.ReadBlock(StartBlock + blockIndex, mTmpBuf, 0);
                if (mIsOzFirstHalf) {
                    Array.Copy(mTmpBuf, 0, data, offset, SECTOR_SIZE);              // 1st half
                } else {
                    Array.Copy(mTmpBuf, SECTOR_SIZE, data, offset, SECTOR_SIZE);    // 2nd half
                }
            } else if (mBase.HasBlocks) {
                // Probably DOS embedded in ProDOS.
                if (FileOrder != SectorOrder.DOS_Sector) {
                    // This should only happen if somebody calls AnalyzePartition() on us, which
                    // isn't necessary, and it failed to recognize the OS and started probing
                    // through the file orders.  We could handle this, but shouldn't need to.
                    Debug.Assert(false);
                    throw new BadBlockException("Shouldn't be using this disk order");
                }
                uint index = trk * NumSectorsPerTrack + sct;
                mBase.ReadBlock(StartBlock + index / 2, mTmpBuf, 0);
                if ((index & 0x01) == 0) {
                    Array.Copy(mTmpBuf, 0, data, offset, SECTOR_SIZE);              // 1st half
                } else {
                    Array.Copy(mTmpBuf, SECTOR_SIZE, data, offset, SECTOR_SIZE);    // 2nd half
                }
            } else {
                throw new InvalidOperationException("No blocks or sectors");
            }
        }

        // IChunkAccess
        public void ReadBlock(uint block, byte[] data, int offset) {
            CheckBlockArgs(block, false);
            if (mBase.HasBlocks) {
                // ChunkSubsets are fundamentally ProDOS-order, but we may store consecutive
                // DOS sectors on them and call it DOS order to prevent skew translation.
                // If we want to read blocks in this case, we'll need to undo the skew.
                Debug.Assert(FileOrder == SectorOrder.ProDOS_Block);
                mBase.ReadBlock(StartBlock + block, data, offset);
            } else {
                throw new InvalidOperationException("No blocks");
            }
        }

        // IChunkAccess
        public void WriteSector(uint trk, uint sct, byte[] data, int offset) {
            CheckSectorArgs(trk, sct, true);
            if (mBase.HasSectors) {
                mBase.WriteSector(trk, sct, data, offset);
            } else if (mBase.HasBlocks && mIsOzSpecial) {
                uint blockIndex = trk * NumSectorsPerTrack + sct;
                mBase.ReadBlock(StartBlock + blockIndex, mTmpBuf, 0);
                if (mIsOzFirstHalf) {
                    Array.Copy(data, offset, mTmpBuf, 0, SECTOR_SIZE);              // 1st half
                } else {
                    Array.Copy(data, offset, mTmpBuf, SECTOR_SIZE, SECTOR_SIZE);    // 2nd half
                }
                mBase.WriteBlock(StartBlock + blockIndex, mTmpBuf, 0);
            } else if (mBase.HasBlocks) {
                // Probably DOS embedded in ProDOS.
                if (FileOrder != SectorOrder.DOS_Sector) {
                    throw new BadBlockException("Shouldn't be using this disk order");
                }
                uint index = trk * NumSectorsPerTrack + sct;
                // Read the block, overwrite half with the provided data, then write it back.
                mBase.ReadBlock(StartBlock + index / 2, mTmpBuf, 0);
                if ((index & 0x01) == 0) {
                    Array.Copy(data, offset, mTmpBuf, 0, SECTOR_SIZE);              // 1st half
                } else {
                    Array.Copy(data, offset, mTmpBuf, SECTOR_SIZE, SECTOR_SIZE);    // 2nd half
                }
                mBase.WriteBlock(StartBlock + index / 2, mTmpBuf, 0);
            } else {
                throw new InvalidOperationException("No blocks or sectors");
            }
            IsModified = true;
        }

        // IChunkAccess
        public void WriteBlock(uint block, byte[] data, int offset) {
            CheckBlockArgs(block, true);
            if (mBase.HasBlocks) {
                mBase.WriteBlock(StartBlock + block, data, offset);
            } else {
                throw new InvalidOperationException("No blocks");
            }
            IsModified = true;
        }

        // IChunkAccess
        public bool TestSector(uint trk, uint sct, out bool writable) {
            CheckSectorArgs(trk, sct, false);
            if (mBase.HasSectors) {
                return mBase.TestSector(trk, sct, out writable);
            } else {
                // Assume it's nicely formatted.
                writable = true;
                return true;
            }
        }

        // IChunkAccess
        public bool TestBlock(uint block, out bool writable) {
            CheckBlockArgs(block, false);
            if (mBase.HasBlocks) {
                return mBase.TestBlock(StartBlock + block, out writable);
            } else {
                throw new InvalidOperationException("No blocks");
            }
        }

        // IChunkAccess
        public void Initialize() {
            if (IsReadOnly) {
                throw new InvalidOperationException("Chunk access is read-only");
            }
            IsModified = true;
            if (HasSectors) {
                for (uint trk = 0; trk < NumTracks; trk++) {
                    for (uint sct = 0; sct < NumSectorsPerTrack; sct++) {
                        WriteSector(trk, sct, GeneralChunkAccess.ZEROES, 0);
                    }
                }
            } else {
                for (uint blk = 0; blk < FormattedLength / BLOCK_SIZE; blk++) {
                    WriteBlock(blk, GeneralChunkAccess.ZEROES, 0);
                }
            }
        }
    }
}
