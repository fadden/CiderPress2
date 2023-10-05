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
using System.Text;

using CommonUtil;
using DiskArc.Multi;
using static DiskArc.Defs;

namespace DiskArc.FS {
    /// <summary>
    /// Code for finding and enumerating filesystem partitions embedded in a ProDOS filesystem,
    /// notably DOS MASTER and PPM volumes.
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

        private ProDOS mFileSystem;
        private IChunkAccess mBaseChunks;
        private AppHook mAppHook;

        private List<Partition> mPartitions = new List<Partition>();


        /// <summary>
        /// Private constructor.
        /// </summary>
        /// <param name="fs">ProDOS filesystem reference.</param>
        /// <param name="appHook">Application hook reference.</param>
        private ProDOS_Embedded(ProDOS fs, AppHook appHook) {
            IChunkAccess chunkAccess = fs.ChunkAccess;
            Debug.Assert(chunkAccess is not GatedChunkAccess);
            Debug.Assert(chunkAccess.HasBlocks);

            mFileSystem = fs;
            mBaseChunks = chunkAccess;
            mAppHook = appHook;
            RawAccess = new GatedChunkAccess(chunkAccess);
        }

        /// <summary>
        /// Looks for filesystems embedded in a ProDOS filesystem.
        /// </summary>
        /// <param name="fs">ProDOS filesystem reference.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <returns>List of embedded volumes as an IMultiPart instance, or null if no embedded
        ///   volumes were found.</returns>
        internal static IMultiPart? FindEmbeddedVolumes(ProDOS fs, AppHook appHook) {
            ProDOS_Embedded embeds = new ProDOS_Embedded(fs, appHook);

            bool foundDOS = embeds.FindDOSVolumes();
            Debug.Assert(foundDOS ^ embeds.mPartitions.Count == 0);
            embeds.FindPPMVolumes();
            if (embeds.mPartitions.Count == 0) {
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
        /// Searches for DOS volumes.  If a set is found, they're added to the partition list.
        /// </summary>
        private bool FindDOSVolumes() {
            // Quick check on VolumeUsage.  Block 0 should be marked as in-use by the system.
            VolumeUsage vu = mFileSystem.VolBitmap!.VolUsage;
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
                if (TryVolumes(startBlock, regionCount, testCount)) {
                    // We have a winner.
                    if (firstReserved != 0) {
                        // TODO: check firstReserved against first DOS volume VTOC.  If not
                        //   correct, flag ourselves as Dubious.
                    }
                    return true;
                }
            }
            Debug.Assert(mPartitions.Count == 0);
            return false;
        }

        /// <summary>
        /// Tests whether there are a series of DOS volumes with a specific size, starting at a
        /// specific location.  If all looks correct, this forms the partition list.
        /// </summary>
        /// <param name="startBlock">ProDOS block number of volume start.</param>
        /// <param name="regionBlockCount">Total size of region, in blocks.</param>
        /// <param name="testBlockCount">Size, in blocks, of an individual DOS volume.</param>
        /// <returns>True if all "slots" hold a valid DOS filesystem.</returns>
        private bool TryVolumes(uint startBlock, uint regionBlockCount, uint testBlockCount) {
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
                // This Partition constructor prevents re-analysis, which isn't strictly necessary
                // here because the partition is an independent entity.  However, if we want the
                // results to come up the same way when the file is reopened, it's best to force
                // a full re-scan every time.  Our image testing strongly favors finding DOS.
                Partition part = new Partition(subChunk, new DOS(subChunk, mAppHook), mAppHook);
                mPartitions.Add(part);
            }

            mAppHook.LogI("Found " + goodChunks.Count + " DOS volumes of size " + testBlockCount +
                " blocks");
            return true;
        }

        #region PPM

        //
        // Two basic approaches for PPM: treat it as a series of loosely-related embedded
        // volumes (like DOS master), or treat it as a disk image file that can be opened
        // to expose the multi-part layout within.
        //
        // We're using the embedded approach.  Treating it as a readable file:
        // - Requires adding file-handling code to ProDOS_FileDesc for storage type 4.  This is a
        //   little weirder than it sounds because the PASCAL.AREA file can exceed the 16MB
        //   maximum length of a ProDOS file, so exceptions to length checks have to be added.
        // - Requires updating the disk image code in the application to recognize the file's
        //   special characteristics.  Testing the filename may be enough, checking the ProDOS
        //   storage type is preferred.
        // - Allows the PPM area to be extracted, which is a problem: PPM uses absolute
        //   ProDOS filesystem offsets rather than partition-relative offsets, so you can't
        //   locate the partitions without knowing where the PPM area sat within the filesystem.
        //
        // It's simpler to handle it through the embedded partition mechanism.
        //

        private const string PPM_AREA_NAME = "PASCAL.AREA";
        private const int PPM_MAX_VOLUME = 31;
        private const int PPM_INFO_SIZE = 8;
        private const int PPM_DESC_SIZE = 16;
        private const int PPM_DESC_OFFSET = 0x100;
        private const int PPM_SIGNATURE = 0x4D505003;   // "PPM" with preceding length byte

        /// <summary>
        /// Looks for a Pascal ProFile Manager area.  If found, identifies the partitions.
        /// </summary>
        private void FindPPMVolumes() {
            // Look for a file with the appropriate characteristics in the root dir.
            IFileEntry rootDir = mFileSystem.GetVolDirEntry();
            if (!mFileSystem.TryFindFileEntry(rootDir, PPM_AREA_NAME, out IFileEntry ientry)) {
                return;
            }
            ProDOS_FileEntry pasEntry = (ProDOS_FileEntry)ientry;
            if (pasEntry.StorageType != ProDOS.StorageType.PascalVolume) {
                mAppHook.LogW("PPM: ignoring " + pasEntry.FileName + " with wrong storage type");
                return;
            }

            uint startBlock = pasEntry.KeyBlock;
            uint blockCount = pasEntry.BlocksUsed;
            if (startBlock == 0 || blockCount < 2 ||
                    startBlock + blockCount > mFileSystem.TotalBlocks) {
                mAppHook.LogW("PPM: invalid: start=" + startBlock + " count=" + blockCount +
                    " totalBlocks=" + mFileSystem.TotalBlocks);
                return;
            }

            // Read the PPM info blocks.
            byte[] ppmInfo = new byte[BLOCK_SIZE * 2];
            try {
                mBaseChunks.ReadBlock(startBlock, ppmInfo, 0);
                mBaseChunks.ReadBlock(startBlock + 1, ppmInfo, BLOCK_SIZE);
            } catch (Exception ex) {
                mAppHook.LogW("PPM: unable to read info blocks: " + ex.Message);
                return;
            }

            // Do a quick check on the info.
            int offset = 0;
            ushort mapBlockCount = RawData.ReadU16LE(ppmInfo, ref offset);
            ushort volCount = RawData.ReadU16LE(ppmInfo, ref offset);
            uint signature = RawData.ReadU32LE(ppmInfo, ref offset);
            if (signature != PPM_SIGNATURE) {
                mAppHook.LogW("PPM: did not find signature (got " + signature.ToString("x8") + ")");
                return;
            }
            if (mapBlockCount != blockCount) {
                mAppHook.LogW("PPM: stored block count is " + mapBlockCount + ", dir count is " +
                    blockCount);
                // keep going?
            }
            if (volCount == 0 || volCount > PPM_MAX_VOLUME) {
                mAppHook.LogW("PPM: invalid volume count: " + volCount);
                return;
            }

            // Generate partition list.  Partition slot #0 is essentially the PPM map itself,
            // so skip over that.
            int prevEnd = 0;
            for (int i = 1; i < volCount + 1; i++) {
                int infoOffset = i * PPM_INFO_SIZE;
                ushort partStart = RawData.GetU16LE(ppmInfo, infoOffset + 0);
                ushort partCount = RawData.GetU16LE(ppmInfo, infoOffset + 2);
                if (partStart + partCount > mFileSystem.TotalBlocks) {
                    mAppHook.LogW("PPM: invalid partition " + i + ": start=" + partStart +
                        ", count=" + partCount);
                    continue;
                }

                int descOffset = PPM_DESC_OFFSET + i * PPM_DESC_SIZE;
                byte strLen = ppmInfo[descOffset];
                if (strLen >= PPM_DESC_SIZE) {
                    mAppHook.LogW("PPM: invalid description string " + i + ": len=" + strLen);
                    continue;
                }
                string desc = Encoding.ASCII.GetString(ppmInfo, descOffset + 1, strLen);

                if (partStart < prevEnd) {
                    mAppHook.LogW("PPM: partition " + i + " overlaps with previous");
                    // We have no way to mark these as damaged/dubious, so just omit it entirely.
                    continue;
                }

                PPM_Partition newPart = new PPM_Partition(mBaseChunks,
                    (long)partStart * BLOCK_SIZE, (long)partCount * BLOCK_SIZE, desc, mAppHook);
                mPartitions.Add(newPart);

                prevEnd = partStart + partCount;
            }
        }

        #endregion PPM
    }
}
