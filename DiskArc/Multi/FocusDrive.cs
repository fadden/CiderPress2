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
using System.Text;

using CommonUtil;
using static DiskArc.Defs;
using static DiskArc.FileAnalyzer.DiskLayoutEntry;

namespace DiskArc.Multi {
    /// <summary>
    /// FocusDrive partition format.
    /// </summary>
    public class FocusDrive : IMultiPart {
        private const int PART_MAP_BLOCK_COUNT = 3;
        private const int MAX_PARTITIONS = 30;

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
            public const int MAP_LENGTH = 512;
            public static readonly byte[] SIGNATURE = Encoding.ASCII.GetBytes("Parsons Engin.");
            private const int NAME_LEN = 32;

            // Partition map block.
            public byte[] mSignature = new byte[SIGNATURE.Length];
            public byte mUnknown1;
            public byte mPartCount;
            public byte[] mUnknown2 = new byte[16];
            public Entry[] mEntries = new Entry[MAX_PARTITIONS];
            public class Entry {
                public uint mStartBlock;
                public uint mBlockCount;
                public uint mUnknown1e;
                public uint mUnknown2e;
                public byte[] mName = new byte[NAME_LEN];

                public string Name {
                    get {
                        int len;
                        for (len = 0; len < NAME_LEN; len++) {
                            if (mName[len] == '\0') {
                                break;
                            }
                        }
                        return Encoding.ASCII.GetString(mName, 0, len);
                    }
                }
            }

            // Partition name block header.
            public uint mLeftoverBlockCount;

            public void Load(byte[] buf, int offset) {
                int startOffset = offset;
                for (int i = 0; i < mSignature.Length; i++) {
                    mSignature[i] = buf[offset++];
                }
                mUnknown1 = buf[offset++];
                mPartCount = buf[offset++];
                for (int i = 0; i < mUnknown2.Length; i++) {
                    mUnknown2[i] = buf[offset++];
                }
                for (int part = 0; part < MAX_PARTITIONS; part++) {
                    mEntries[part] = new Entry();
                    mEntries[part].mStartBlock = RawData.ReadU32LE(buf, ref offset);
                    mEntries[part].mBlockCount = RawData.ReadU32LE(buf, ref offset);
                    mEntries[part].mUnknown1e = RawData.ReadU32LE(buf, ref offset);
                    mEntries[part].mUnknown2e = RawData.ReadU32LE(buf, ref offset);
                }
                Debug.Assert(offset - startOffset == MAP_LENGTH);

                // Extract the left-over blocks count, which is stored in what would be
                // expected to hold the first partition name.
                offset += 4;
                mLeftoverBlockCount = RawData.ReadU32LE(buf, ref offset);
                offset += 24;

                // Extract the 30 partition names.
                for (int part = 0; part < MAX_PARTITIONS; part++) {
                    for (int i = 0; i < NAME_LEN; i++) {
                        mEntries[part].mName[i] = buf[offset++];
                    }
                }

                // Last name slot is empty.
                offset += 32;

                Debug.Assert(offset - startOffset == BLOCK_SIZE * 3);
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

        public FocusDrive(IChunkAccess chunkAccess, AppHook appHook) {
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
            if (chunkAccess.FormattedLength / BLOCK_SIZE < PART_MAP_BLOCK_COUNT) {
                return null;        // need the 3-block partition map
            }
            List<Partition> partitions = new List<Partition>();
            byte[] blockBuf = new byte[BLOCK_SIZE * PART_MAP_BLOCK_COUNT];

            for (int i = 0; i < PART_MAP_BLOCK_COUNT; i++) {
                chunkAccess.ReadBlock((uint)i, blockBuf, BLOCK_SIZE * i);
            }
            PartitionMap pm = new PartitionMap();
            pm.Load(blockBuf, 0);

            // See if it looks like one of ours.
            if (RawData.MemCmp(pm.mSignature, PartitionMap.SIGNATURE,
                    PartitionMap.SIGNATURE.Length) != 0) {
                return null;
            }
            if (pm.mPartCount > MAX_PARTITIONS) {
                return null;
            }

            if (!isTestOnly) {
                for (int i = 0; i < pm.mPartCount; i++) {
                    Partition? newPart = NewPartition(chunkAccess, pm.mEntries[i].mStartBlock,
                        pm.mEntries[i].mBlockCount, pm.mEntries[i].Name, notes, appHook);
                    if (newPart != null) {
                        partitions.Add(newPart);
                    }
                }
            }

            return partitions;
        }

        private static Partition? NewPartition(IChunkAccess chunkAccess, uint startBlock,
                uint blockCount, string name, Notes? notes, AppHook appHook) {
            long totalBlocks = chunkAccess.FormattedLength / BLOCK_SIZE;
            if (startBlock >= totalBlocks) {
                notes?.AddE("Start of partition at block=" + startBlock +
                    " is past end of file (" + totalBlocks + " blocks)");
                return null;
            } else if (startBlock + blockCount > totalBlocks) {
                notes?.AddE("Partition at " + startBlock + " runs off end of file");
                return null;
            }
            Partition newPart = new FocusDrive_Partition(chunkAccess,
                (long)startBlock * BLOCK_SIZE, (long)blockCount * BLOCK_SIZE, name, appHook);
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
            long lastEnd = PART_MAP_BLOCK_COUNT * BLOCK_SIZE;
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
