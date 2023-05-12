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
using System.Collections;
using System.Diagnostics;

using CommonUtil;
using DiskArc.Multi;
using static DiskArc.Defs;

namespace DiskArc.FS {
    /// <summary>
    /// Code for finding and enumerating filesystem partitions embedded in a ProDOS filesystem,
    /// notably DOS MASTER volumes.
    /// </summary>
    /// <remarks>
    /// <para>Re-analyzing the filesystem is okay, since the DOS partitions don't overlap with
    /// ProDOS the way they do for DOS_Hybrid, but it can be a little weird if the default
    /// sector order doesn't pass and it starts scanning for the correct order.  It's best if
    /// we don't re-analyze.</para>
    /// </remarks>
    public class ProDOS_Embedded : IMultiPart {
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

        private IChunkAccess mBaseChunks;
        private AppHook mAppHook;

        private List<Partition> mPartitions = new List<Partition>();


        /// <summary>
        /// Private constructor.
        /// </summary>
        /// <param name="chunkAccess">Chunk access object for ProDOS filesystem.</param>
        /// <param name="appHook">Application hook reference.</param>
        private ProDOS_Embedded(IChunkAccess chunkAccess, AppHook appHook) {
            Debug.Assert(chunkAccess is not GatedChunkAccess);
            Debug.Assert(chunkAccess.HasBlocks);

            mBaseChunks = chunkAccess;
            mAppHook = appHook;
            RawAccess = new GatedChunkAccess(chunkAccess);
        }

        /// <summary>
        /// Looks for filesystems embedded in a ProDOS filesystem.
        /// </summary>
        /// <param name="chunkAccess">Chunk access object for ProDOS filesystem.</param>
        /// <param name="vu">Volume usage for ProDOS filesystem.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <returns>List of embedded volumes as an IMultiPart instance, or null if no embedded
        ///   volumes were found.</returns>
        internal static IMultiPart? FindEmbeddedVolumes(IChunkAccess chunkAccess, VolumeUsage vu,
                AppHook appHook) {
            ProDOS_Embedded embeds = new ProDOS_Embedded(chunkAccess, appHook);
            if (!embeds.FindVolumes(vu)) {
                return null;
            } else {
                return embeds;
            }
        }

        public void Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        protected virtual void Dispose(bool disposing) {
            RawAccess.AccessLevel = GatedChunkAccess.AccessLvl.Closed;
            if (disposing) {
                foreach (Partition part in mPartitions) {
                    part.Dispose();
                }
            }
        }

        // Expected DOS 3.3 embedded volume sizes, in blocks.
        private static readonly uint[] sBlockCounts = new uint[] { 280, 320, 400, 800 };

        /// <summary>
        /// Searches for DOS volumes.
        /// </summary>
        private bool FindVolumes(VolumeUsage vu) {
            // Quick check on VolumeUsage.  Block 0 should be marked as in-use by the system.
            if (vu.GetUsage(0) != IFileEntry.NO_ENTRY) {
                Debug.Assert(false);        // shouldn't be here without full VU data
                return false;
            }

            // Count up the number of consecutive blocks that are marked in-use but don't have
            // an owner.  Grab the first chunk we find, starting from the end of the filesystem.
            uint startBlock = 0;
            uint endBlock = 0;
            uint totalBlocks = (uint)(mBaseChunks.FormattedLength / BLOCK_SIZE);
            for (uint index = totalBlocks - 1; index > 0; index--) {
                if (index < vu.Count && vu.GetInUse(index) && vu.GetUsage(index) == null) {
                    // Block is in use, no owner.
                    if (endBlock == 0) {
                        // Start (well, end) of block.
                        endBlock = index;
                    }
                } else {
                    // Block not in use, or has an owner.  If we were in a block, halt.
                    if (endBlock != 0) {
                        startBlock = index + 1;
                        break;
                    }
                }
            }

            if (startBlock == 0) {
                return false;
            }
            uint regionCount = endBlock - startBlock + 1;

            Debug.WriteLine("Found run of " + regionCount + " blocks");

            // DOS MASTER can fill an entire 800KB disk with DOS volumes, but when doing so it
            // leaves either the first 7 blocks (basic ProDOS volume structure) or 7+56 blocks
            // (volume structure + 28KB of storage) so that the disk is recognizable as ProDOS
            // and, in the latter case, can be booted into the modified DOS 3.3.
            //
            // If we see this, we treat the region as spanning the full 1600 blocks, but note
            // that the first few tracks of the first DOS volume must be marked as inaccessible.
            uint firstReserved = 0;
            if (totalBlocks == 1600) {
                if (regionCount == 1600 - 7) {
                    firstReserved = 1;
                    startBlock = 0;
                    regionCount = 1600;
                } else if (regionCount == 1600 - (7 + 56)) {
                    firstReserved = 8;
                    startBlock = 0;
                    regionCount = 1600;
                }
            }

            // DOS MASTER allows volumes of size 140KB, 160KB, 200KB, and 400KB.  The only
            // limitation is that they must all be the same size.
            foreach (uint testCount in sBlockCounts) {
                if (CheckVolumes(startBlock, regionCount, testCount)) {
                    // We have a winner.
                    if (firstReserved != 0) {
                        // TODO: check firstReserved against first DOS volume VTOC.  If not
                        //   correct, flag ourselves as Dubious.
                    }
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Tests whether there are a series of DOS volumes with a specific size, starting at a
        /// specific location.
        /// </summary>
        /// <param name="startBlock">ProDOS block number of volume start.</param>
        /// <param name="regionBlockCount">Total size of region, in blocks.</param>
        /// <param name="testBlockCount">Size, in blocks, of an individual DOS volume.</param>
        /// <returns>True if all "slots" hold a valid DOS filesystem.</returns>
        private bool CheckVolumes(uint startBlock, uint regionBlockCount, uint testBlockCount) {
            // Confirm that the size is a multiple.
            if (regionBlockCount < testBlockCount || regionBlockCount % testBlockCount != 0) {
                return false;
            }

            List<ChunkSubset> goodChunks = new List<ChunkSubset>();

            // Create a chunk access for each volume, and verify that DOS is present.
            // TODO: we could weaken the test and only require that N of M be identifiable, but
            //   we'd have to be really careful about false positives.
            uint numVolumes = regionBlockCount / testBlockCount;
            uint foundCount = 0;
            for (uint i = 0; i < numVolumes; i++) {
                uint subStart = startBlock + testBlockCount * i;
                uint numSectors;
                if (testBlockCount <= 400) {
                    numSectors = 16;
                } else {
                    numSectors = 32;
                }
                uint numTracks = testBlockCount / (numSectors / 2);
                ChunkSubset dosChunks = ChunkSubset.CreateTracksOnBlocks(mBaseChunks,
                    subStart, numSectors, numTracks);
                // Allow "yes" or "maybe".
                if (DOS.TestImage(dosChunks, mAppHook) ==
                        FileAnalyzer.DiskLayoutEntry.TestResult.No) {
                    Debug.WriteLine("No DOS found at " + i + " (start=" + subStart +
                        ") for size=" + testBlockCount);
                    // Comment this "return" statement out to show failure count in log.
                    return false;
                } else {
                    foundCount++;
                    goodChunks.Add(dosChunks);
                }
            }
            if (foundCount != numVolumes) {
                if (foundCount != 0) {
                    mAppHook.LogI("Rejecting multi-DOS volume: " + (numVolumes - foundCount) +
                        " of " + numVolumes + " were invalid (size=" + testBlockCount + " blocks)");
                }
                return false;
            }

            // Create the partition list.
            Debug.Assert(mPartitions.Count == 0);
            foreach (ChunkSubset subChunk in goodChunks) {
                Partition part = new Partition(subChunk, new DOS(subChunk, mAppHook), mAppHook);
                mPartitions.Add(part);
            }

            mAppHook.LogI("Found " + goodChunks.Count + " DOS volumes of size " + testBlockCount +
                " blocks");
            return true;
        }
    }
}
