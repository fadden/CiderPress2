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
using System.Collections;
using System.Diagnostics;

using CommonUtil;
using DiskArc.FS;
using static DiskArc.Defs;
using static DiskArc.FileAnalyzer.DiskLayoutEntry;

namespace DiskArc.Multi {
    /// <summary>
    /// MicroDrive partition format.
    /// </summary>
    public class MicroDrive : IMultiPart {
        private const int FIRST_PART_START = 256;

        //
        // IMultiPart interfaces.
        //

        public bool IsDubious { get; private set; }

        public Notes Notes { get; } = new Notes();

        public GatedChunkAccess RawAccess { get; }

        public int Count { get { return mPartitions.Count; } }
        public Partition this[int key] { get { return mPartitions[key]; } }

        public IEnumerator<Partition> GetEnumerator() {
            return mPartitions.GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() {
            return mPartitions.GetEnumerator();
        }

        //
        // Innards.
        //

        private AppHook mAppHook;
        private List<Partition> mPartitions;

        private class PartitionMap {
            public const int LENGTH = 192;
            public const int NUM_PARTS = 8;
            public const ushort MAGIC = 0xccca;

            public ushort mMagic;
            public ushort mCylinders;
            public ushort mReserved1;
            public ushort mHeads;
            public ushort mSectors;
            public ushort mReserved2;
            public byte mNumPart1;
            public byte mNumPart2;
            public byte[] mReserved3 = new byte[10];
            public ushort mROMVersion;
            public byte[] mReserved4 = new byte[6];
            public uint[] mPartitionStart1 = new uint[NUM_PARTS];       // starts at 0x20
            public uint[] mPartitionSize1 = new uint[NUM_PARTS];
            public byte[] mReserved5 = new byte[32];
            public uint[] mPartitionStart2 = new uint[NUM_PARTS];       // starts at 0x80
            public uint[] mPartitionSize2 = new uint[NUM_PARTS];
            // 320 bytes reserved

            public void Load(byte[] buf, int offset) {
                int startOffset = offset;
                mMagic = RawData.ReadU16LE(buf, ref offset);
                mCylinders = RawData.ReadU16LE(buf, ref offset);
                mReserved1 = RawData.ReadU16LE(buf, ref offset);
                mHeads = RawData.ReadU16LE(buf, ref offset);
                mSectors = RawData.ReadU16LE(buf, ref offset);
                mReserved2 = RawData.ReadU16LE(buf, ref offset);
                mNumPart1 = buf[offset++];
                mNumPart2 = buf[offset++];
                for (int i = 0; i < mReserved3.Length; i++) {
                    mReserved3[i] = buf[offset++];
                }
                mROMVersion = RawData.ReadU16LE(buf, ref offset);
                for (int i = 0; i < mReserved4.Length; i++) {
                    mReserved4[i] = buf[offset++];
                }
                for (int i = 0; i < NUM_PARTS; i++) {
                    mPartitionStart1[i] = RawData.ReadU32LE(buf, ref offset);
                }
                for (int i = 0; i < NUM_PARTS; i++) {
                    mPartitionSize1[i] = RawData.ReadU32LE(buf, ref offset);
                }
                for (int i = 0; i < mReserved5.Length; i++) {
                    mReserved5[i] = buf[offset++];
                }
                for (int i = 0; i < NUM_PARTS; i++) {
                    mPartitionStart2[i] = RawData.ReadU32LE(buf, ref offset);
                }
                for (int i = 0; i < NUM_PARTS; i++) {
                    mPartitionSize2[i] = RawData.ReadU32LE(buf, ref offset);
                }
                Debug.Assert(offset - startOffset == LENGTH);
            }
        }

        // Delegate: test image to see if it's ours.
        public static TestResult TestImage(IChunkAccess chunkAccess, AppHook appHook) {
            if (!chunkAccess.HasBlocks) {
                return TestResult.No;
            }
            if (chunkAccess is ChunkSubset) {
                // Don't look for APM inside other partitions.
                return TestResult.No;
            }

            List<Partition>? partitions = LoadPartitions(chunkAccess, true, null, appHook);
            if (partitions == null) {
                return TestResult.No;
            }

            return TestResult.Yes;
        }

        // Delegate: returns true if the size (in bytes) is valid for this filesystem.
        public static bool IsSizeAllowed(long size) {
            return false;
        }

        public MicroDrive(IChunkAccess chunkAccess, AppHook appHook) {
            Debug.Assert(chunkAccess is not GatedChunkAccess);
            if (!chunkAccess.HasBlocks) {
                throw new ArgumentException("Must have blocks");
            }
            List<Partition>? partitions = LoadPartitions(chunkAccess, false, Notes, appHook);
            if (partitions == null) {
                throw new InvalidDataException("Incompatible data stream");
            }

            mAppHook = appHook;
            mPartitions = partitions;
            RawAccess = new GatedChunkAccess(chunkAccess);
            RawAccess.AccessLevel = GatedChunkAccess.AccessLvl.Open;

            // Check for overlap.
            if (!ValidatePartitionMap(chunkAccess, Notes)) {
                Notes.AddE("Partition map validation failed");
                IsDubious = true;
            }
        }

        public void Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        protected virtual void Dispose(bool disposing) {
            if (disposing) {
                foreach (Partition part in mPartitions) {
                    part.Dispose();
                }
            }
            if (RawAccess != null) {
                RawAccess.AccessLevel = GatedChunkAccess.AccessLvl.Closed;
            }
        }

        private static List<Partition>? LoadPartitions(IChunkAccess chunkAccess, bool isTestOnly,
                Notes? notes, AppHook appHook) {
            List<Partition> partitions = new List<Partition>();
            byte[] blockBuf = new byte[BLOCK_SIZE];

            chunkAccess.ReadBlock(0, blockBuf, 0);
            PartitionMap pm = new PartitionMap();
            pm.Load(blockBuf, 0);

            // Check a couple of things.
            if (pm.mMagic != PartitionMap.MAGIC) {
                return null;
            }
            if (pm.mNumPart1 == 0 || pm.mNumPart1 > PartitionMap.NUM_PARTS ||
                    pm.mNumPart2 > PartitionMap.NUM_PARTS) {
                return null;        // no partitions, or too many defined
            }
            if (pm.mPartitionStart1[0] != FIRST_PART_START) {
                return null;        // first partition is in the wrong place
            }

            // Load up all the partitions we can find.
            if (!isTestOnly) {
                for (int i = 0; i < pm.mNumPart1; i++) {
                    Partition? newPart = NewPartition(chunkAccess, pm.mPartitionStart1[i],
                        pm.mPartitionSize1[i] & 0x00ffffff, notes, appHook);
                    if (newPart != null) {
                        partitions.Add(newPart);
                    }
                }
                for (int i = 0; i < pm.mNumPart2; i++) {
                    Partition? newPart = NewPartition(chunkAccess, pm.mPartitionStart2[i],
                        pm.mPartitionSize2[i] & 0x00ffffff, notes, appHook);
                    if (newPart != null) {
                        partitions.Add(newPart);
                    }
                }
            }

            return partitions;
        }

        private static Partition? NewPartition(IChunkAccess chunkAccess, uint startBlock,
                uint blockCount, Notes? notes, AppHook appHook) {
            long totalBlocks = chunkAccess.FormattedLength / BLOCK_SIZE;
            if (startBlock >= totalBlocks) {
                notes?.AddE("Start of partition at block=" + startBlock +
                    " is past end of file (" + totalBlocks + " blocks)");
                return null;
            } else if (startBlock + blockCount > totalBlocks) {
                notes?.AddE("Partition at " + startBlock + " runs off end of file");
                return null;
            }
            Partition newPart = new Partition(chunkAccess,
                (long)startBlock * BLOCK_SIZE, (long)blockCount * BLOCK_SIZE, appHook);
            return newPart;
        }

        /// <summary>
        /// Validates the partition map, checking for gaps and overlapping sections.
        /// </summary>
        private bool ValidatePartitionMap(IChunkAccess fullChunks, Notes notes) {
            // Make a shallow copy, and sort it by starting block.
            Partition[] sorted = new Partition[Count];
            for (int i = 0; i < Count; i++) {
                sorted[i] = this[i];
            }
            Array.Sort(sorted, delegate (Partition p1, Partition p2) {
                return p1.StartOffset.CompareTo(p2.StartOffset);
            });

            bool result = true;
            long lastEnd = FIRST_PART_START * BLOCK_SIZE;
            for (int i = 0; i < sorted.Length; i++) {
                if (sorted[i].StartOffset < lastEnd) {
                    notes.AddE("Partitions " + (i - 1) + " and " + i + " overlap");
                    result = false;     // danger!
                } else if (sorted[i].StartOffset > lastEnd) {
                    notes.AddI("Unexpected gap before partition at " + sorted[i].StartOffset +
                        " (" + ((sorted[i].StartOffset - lastEnd) / BLOCK_SIZE) + " blocks)");
                }
                lastEnd = sorted[i].StartOffset + sorted[i].Length;
            }

            // Check the end zone.
            if (lastEnd < fullChunks.FormattedLength) {
                Notes.AddW("Found unused data past end of partitioned area (" +
                    (fullChunks.FormattedLength - lastEnd) / BLOCK_SIZE + " blocks)");
            } else if (lastEnd > fullChunks.FormattedLength) {
                // Should have been caught earlier.
                Notes.AddE("Last partition extends off end of disk image");
            }

            return result;
        }
    }
}
