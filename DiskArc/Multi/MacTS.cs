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
using static DiskArc.Defs;
using static DiskArc.FileAnalyzer.DiskLayoutEntry;

namespace DiskArc.Multi {
    /// <summary>
    /// Early Macintosh "TS" partition format.  Pre-dates APM.
    /// </summary>
    public class MacTS : IMultiPart {
        public const int PART_SIGNATURE = 0x5453;       // big-endian 'TS'

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

        private List<Partition> mPartitions = new List<Partition>(1);
        private AppHook mAppHook;


        // Delegate: test image to see if it's ours.
        public static TestResult TestImage(IChunkAccess chunkAccess, AppHook appHook) {
            if (!chunkAccess.HasBlocks) {
                return TestResult.No;
            }
            if (chunkAccess is ChunkSubset) {
                // Don't look for TS inside other partitions.
                return TestResult.No;
            }

            // Read the DDR out of block 0.
            byte[] blockBuf = new byte[BLOCK_SIZE];
            chunkAccess.ReadBlock(0, blockBuf, 0);
            APM.DriverDescriptorRecord ddr = new APM.DriverDescriptorRecord();
            ddr.Load(blockBuf, 0);
            if (ddr.sbSig != APM.DDR_SIGNATURE || ddr.sbBlkSize != BLOCK_SIZE) {
                return TestResult.No;
            }
            if (ddr.sbBlkCount > chunkAccess.FormattedLength / BLOCK_SIZE) {
                // Should be correct value or zero.  Some internal Apple CD-ROMs are totally wrong,
                // so let's just ignore this.
                Debug.WriteLine("DDR block count should be " +
                    (chunkAccess.FormattedLength / BLOCK_SIZE) + " blocks, but is " +
                    ddr.sbBlkCount);
                //return TestResult.No;
            }

            List<Partition>? partitions = LoadPartitions(chunkAccess, null, null);
            if (partitions == null) {
                return TestResult.No;
            }

            return TestResult.Yes;
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
        /// <exception cref="InvalidDataException">This is not a MacTS image.</exception>
        public MacTS(IChunkAccess chunkAccess, AppHook appHook) {
            Debug.Assert(chunkAccess is not GatedChunkAccess);
            if (!chunkAccess.HasBlocks) {
                throw new ArgumentException("Must have blocks");
            }
            List<Partition>? partitions = LoadPartitions(chunkAccess, Notes, appHook);
            if (partitions == null) {
                throw new InvalidDataException("Incompatible data stream");
            }

            mAppHook = appHook;
            mPartitions = partitions;
            RawAccess = new GatedChunkAccess(chunkAccess);
            RawAccess.AccessLevel = GatedChunkAccess.AccessLvl.ReadOnly;

            if (IsDubious) {
                foreach (Partition part in partitions) {
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
            if (RawAccess != null) {
                RawAccess.AccessLevel = GatedChunkAccess.AccessLvl.Closed;
            }
        }

        /// <summary>
        /// Processes an old-style 'TS' partition map.
        /// </summary>
        private static List<Partition>? LoadPartitions(IChunkAccess chunkAccess, Notes? notes,
                AppHook? appHook) {
            long totalBlocks = chunkAccess.FormattedLength / BLOCK_SIZE;
            List<Partition> partitions = new List<Partition>();
            byte[] blockBuf = new byte[BLOCK_SIZE];

            chunkAccess.ReadBlock(1, blockBuf, 0);
            if (RawData.GetU16BE(blockBuf, 0) != PART_SIGNATURE) {
                return null;
            }
            int offset = 2;
            do {
                uint startBlock = RawData.ReadU32BE(blockBuf, ref offset);
                uint blockCount = RawData.ReadU32BE(blockBuf, ref offset);
                uint fsid = RawData.ReadU32BE(blockBuf, ref offset);

                if (startBlock == 0) {
                    break;      // end of list reached
                }

                if (startBlock >= totalBlocks || startBlock > totalBlocks - blockCount) {
                    Debug.WriteLine("Bad TS partition: start=" + startBlock + " count=" +
                        blockCount + " totalBlocks=" + totalBlocks);
                    return null;
                }
                if (appHook != null) {
                    Partition part = new Partition(chunkAccess,
                        (long)startBlock * BLOCK_SIZE, (long)blockCount * BLOCK_SIZE, appHook);
                    partitions.Add(part);

                    Debug.WriteLine("[TS: start=" + startBlock + " count=" + blockCount +
                        " fsid='" + RawData.StringifyU32BE(fsid) + "']");
                }
            } while (offset < BLOCK_SIZE - 12);

            notes?.AddI("Found " + partitions.Count + " TS partitions");

            return partitions;
        }
    }
}
