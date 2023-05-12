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
    /// CFFA storage card image.
    /// </summary>
    public class CFFA : IMultiPart {
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

        // Delegate: test image to see if it's ours.
        public static TestResult TestImage(IChunkAccess chunkAccess, AppHook appHook) {
            if (!chunkAccess.HasBlocks) {
                return TestResult.No;
            }
            if (chunkAccess is ChunkSubset) {
                // Don't look for CFFA inside other partitions.
                return TestResult.No;
            }

            // Do the full partition scan.  We're just probing to see if it looks like a
            // filesystem is present, not doing the actual filesystem analysis, so it shouldn't
            // be too expensive.
            List<Partition>? partitions = FindPartitions(chunkAccess, null, appHook,
                out int goodCount);
            bool allGood = false;
            allGood = partitions != null && (goodCount == partitions.Count);
            DisposePartitions(partitions);
            if (partitions == null) {
                return TestResult.No;
            } else if (!allGood) {
                return TestResult.Maybe;
            } else {
                return TestResult.Yes;
            }
        }

        // Delegate: returns true if the size (in bytes) is valid for this filesystem.
        public static bool IsSizeAllowed(long size) {
            return false;
        }

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="chunkAccess">Chunk access object for full disk.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <exception cref="ArgumentException">Chunk access does not provide blocks.</exception>
        /// <exception cref="InvalidDataException">This is not a CFFA image.</exception>
        public CFFA(IChunkAccess chunkAccess, AppHook appHook) {
            Debug.Assert(chunkAccess is not GatedChunkAccess);
            if (!chunkAccess.HasBlocks) {
                throw new ArgumentException("Must have blocks");
            }
            List<Partition>? partitions = FindPartitions(chunkAccess, Notes, appHook,
                out int goodCount);
            if (partitions == null) {
                throw new InvalidDataException("Incompatible data stream");
            }
            Debug.Assert(goodCount > 0);

            mAppHook = appHook;
            mPartitions = partitions;
            RawAccess = new GatedChunkAccess(chunkAccess);
            // We need this to be read-write for utilities that write to partitions.  However,
            // for safety this should close down if we have an open filesystem on any of the
            // partitions.  I'm not sure that's worth the complexity (the caller could always
            // just write directly to the underlying stream).
            RawAccess.AccessLevel = GatedChunkAccess.AccessLvl.Open;

            if (IsDubious) {
                foreach (Partition part in mPartitions) {
                    // TODO: set partition ChunkAccess to read-only?  Can't set FileSystem
                    //   IsDubious because that gets cleared when its analyzed.
                }
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
            RawAccess.AccessLevel = GatedChunkAccess.AccessLvl.Closed;
        }

        /// <summary>
        /// Finds the list of partitions.
        /// </summary>
        /// <param name="chunkAccess">Block access object.</param>
        /// <param name="notes">Partition note tracker.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <param name="goodCount">Result: number of partitions with recognizable
        ///   filesystems.</param>
        /// <returns>List of partitions, or null if the format was incorrect.</returns>
        private static List<Partition>? FindPartitions(IChunkAccess chunkAccess, Notes? notes,
                AppHook appHook, out int goodCount) {
            const int STD_PART_COUNT = 65536;     // number of blocks in "standard" 32MB partition
            const int LARGE_PART_COUNT = 1024 * 1024 * (1024 / BLOCK_SIZE);     // 1GB, in blocks

            goodCount = 0;
            long totalBlocks = chunkAccess.FormattedLength / BLOCK_SIZE;
            if (totalBlocks <= STD_PART_COUNT) {
                // Too small to have multiple partitions.  This can be handled as an unadorned
                // ProDOS-order disk image.
                return null;
            }
            appHook.LogD("CFFA: scanning, totalBlocks=" + totalBlocks);

            // Look for the first four partitions.
            uint startBlock = 0;
            List<Partition> partitions = FindPartitionSet(chunkAccess, totalBlocks, ref startBlock,
                STD_PART_COUNT, 4, appHook, out goodCount);

            if (startBlock == totalBlocks) {
                // Ran out of disk.  If we found at least one good partition, return the list.
                // Otherwise, we can't say for certain if this is CFFA storage.
                appHook.LogI("CFFA <= 4: " + partitions.Count + " partitions, " +
                    goodCount + " good");
                if (goodCount > 0) {
                    return partitions;
                } else {
                    DisposePartitions(partitions);
                    return null;
                }
            }
            Debug.Assert(startBlock == STD_PART_COUNT * 4);

            // Some space left.  In 4-part mode this will be one or two 1GB partitions, in
            // 8-part mode this will be 1-4 additional 32MB partitions.  We have to try it
            // both ways and see which yields the better results.  A card that was partitioned
            // one way and then reformatted without being zeroed first will false-positive.
            Debug.WriteLine("- probe4");
            uint startBlock4 = startBlock;
            List<Partition> part4 = FindPartitionSet(chunkAccess, totalBlocks, ref startBlock4,
                LARGE_PART_COUNT, 2, appHook, out int goodCount4);
            Debug.WriteLine("- probe8");
            uint startBlock8 = startBlock;
            List<Partition> part8 = FindPartitionSet(chunkAccess, totalBlocks, ref startBlock8,
                STD_PART_COUNT, 4, appHook, out int goodCount8);

            // Choose the winner based on how many *good* partitions we found.  If we found the
            // same either way, tie goes to CFFA4... we probably found one good ProDOS partition
            // in the 5th region; since we found nothing good in 6+ there's no reason to report
            // them as blank partitions.
            Debug.WriteLine("CFFA probe found part4=" + part4.Count + "/" + goodCount4 +
                ", part8=" + part8.Count + "/" + goodCount8);
            List<Partition> keepParts;
            int keepGood;
            if (goodCount8 > goodCount4) {
                Debug.WriteLine(" Treating as CFFA-8");
                keepParts = part8;
                keepGood = goodCount8;
                DisposePartitions(part4);
            } else {
                Debug.WriteLine(" Treating as CFFA-4/6");
                keepParts = part4;
                keepGood = goodCount4;
                DisposePartitions(part8);
            }
            // Transfer the ones we're keeping.
            foreach (Partition part in keepParts) {
                partitions.Add(part);
            }
            goodCount += keepGood;

            appHook.LogI("CFFA > 4: " + partitions.Count + " partitions, " +
                goodCount + " good");
            if (partitions.Count > 1) {
                return partitions;
            } else {
                return null;
            }
        }

        /// <summary>
        /// Finds up to N partitions of a specific size.
        /// </summary>
        /// <param name="chunkAccess">Chunk access object for the entire disk image.</param>
        /// <param name="totalBlocks">Number of blocks in the disk image.</param>
        /// <param name="startBlock">Start block of the first partition.  Will be updated.</param>
        /// <param name="partSize">Number of blocks in the partition.</param>
        /// <param name="numParts">Number of partitions to look for.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <param name="goodCount">Number of "good" partitions found.</param>
        /// <returns>List of partitions.</returns>
        private static List<Partition> FindPartitionSet(IChunkAccess chunkAccess, long totalBlocks,
                ref uint startBlock, uint partSize, int numParts, AppHook appHook,
                out int goodCount) {
            List<Partition> partitions = new List<Partition>();
            goodCount = 0;

            for (int pi = 0; pi < numParts && startBlock < totalBlocks; pi++) {
                uint blocksRemaining = (uint)(totalBlocks - startBlock);
                uint checkBlockCount = Math.Min(partSize, blocksRemaining);
                ChunkSubset chunks = ChunkSubset.CreateBlockSubset(chunkAccess, startBlock,
                    checkBlockCount);
                if (!CheckFileSystem(chunks, appHook, out uint fsBlockCount)) {
                    // Didn't find anything.  Could be we're looking in the wrong place,
                    // could be we just don't recognize the filesystem.
                    Debug.WriteLine("Unable to find FS at block=" + startBlock);
                } else if (fsBlockCount > checkBlockCount) {
                    // The filesystem is larger than the space set aside for it.
                    Debug.WriteLine("Size of filesystem at block=" + startBlock +
                        " exceeds chunk bounds");
                } else {
                    Debug.WriteLine("Found good FS at block=" + startBlock);
                    goodCount++;
                }

                Partition newPart = new Partition(chunkAccess, startBlock * BLOCK_SIZE,
                    checkBlockCount * BLOCK_SIZE, appHook);
                partitions.Add(newPart);

                startBlock += checkBlockCount;
            }

            return partitions;
        }

        /// <summary>
        /// Check to see if there's a filesystem in the chunk.
        /// </summary>
        /// <param name="chunks">Chunk access object (usually a ChunkSubset).</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <param name="fsBlockCount">Result: reported filesystem length.</param>
        /// <returns>True if a filesystem was found.</returns>
        private static bool CheckFileSystem(IChunkAccess chunks, AppHook appHook,
                out uint fsBlockCount) {
            if (ProDOS.TestImage(chunks, appHook, out fsBlockCount) == TestResult.Yes) {
                return true;
            } else if (HFS.TestImage(chunks, appHook, out fsBlockCount) == TestResult.Yes) {
                return true;
            } else {
                fsBlockCount = 0;
                return false;
            }
        }

        /// <summary>
        /// Disposes of the contents of a list of partitions.
        /// </summary>
        private static void DisposePartitions(List<Partition>? parts) {
            if (parts != null) {
                foreach (Partition part in parts) {
                    part.Dispose();
                }
                parts.Clear();
            }
        }
    }
}
