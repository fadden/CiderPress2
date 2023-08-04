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
    /// Macintosh Apple Partition Map (APM) format.
    /// </summary>
    /// <remarks>
    /// <para>The partition list includes the partition map itself, so all partition maps
    /// must have at least one entry.  Partition maps cannot be expanded easily, so it's
    /// common to over-allocate the storage (32 or 64 entries).</para>
    /// </remarks>
    public class APM : IMultiPart {
        public const int DDR_SIGNATURE = 0x4552;        // big-endian 'ER'
        public const int PART_SIGNATURE = 0x504d;       // big-endian 'PM'

        private const int MAX_PARTITIONS = 256;         // 2^31 allowed but absurd

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

        /// <summary>
        /// The Driver Descriptor Record lives in block 0.  In theory, this tells us the block
        /// size and number of blocks on the disk.  In practice, some third-party products
        /// generated bad values, so mostly we're just interested in the signature.
        /// </summary>
        internal class DriverDescriptorRecord {
            public const int LENGTH = 512;
            private const int FIXED_LENGTH = 26;

            /// <summary>device signature</summary>
            public ushort sbSig;

            /// <summary>block size of the device</summary>
            public ushort sbBlkSize;

            /// <summary>number of blocks on the device</summary>
            public uint sbBlkCount;

            /// <summary>reserved</summary>
            public ushort sbDevType;

            /// <summary>reserved</summary>
            public ushort sbDevId;

            /// <summary>reserved</summary>
            public uint sbData;

            /// <summary>number of driver descriptor entries</summary>
            public ushort sbDrvrCount;

            /// <summary>first driver's starting block</summary>
            public uint ddBlock;

            /// <summary>size of the driver, in 512-byte blocks</summary>
            public ushort ddSize;

            /// <summary>operating system type (MacOS = 1)</summary>
            public ushort ddType;

            /// <summary>additional drivers, if any</summary>
            public ushort[] ddPad = new ushort[243];


            public void Load(byte[] buf, int offset) {
                int startOffset = offset;
                sbSig = RawData.ReadU16BE(buf, ref offset);
                sbBlkSize = RawData.ReadU16BE(buf, ref offset);
                sbBlkCount = RawData.ReadU32BE(buf, ref offset);
                sbDevType = RawData.ReadU16BE(buf, ref offset);
                sbDevId = RawData.ReadU16BE(buf, ref offset);
                sbData = RawData.ReadU32BE(buf, ref offset);
                sbDrvrCount = RawData.ReadU16BE(buf, ref offset);
                ddBlock = RawData.ReadU32BE(buf, ref offset);
                ddSize = RawData.ReadU16BE(buf, ref offset);
                ddType = RawData.ReadU16BE(buf, ref offset);
                Debug.Assert(offset - startOffset == FIXED_LENGTH);
                for (int i = 0; i < ddPad.Length; i++) {
                    RawData.ReadU16BE(buf, ref offset);
                }
                Debug.Assert(offset - startOffset == LENGTH);
            }
        }

        /// <summary>
        /// The partition map has one map entry per block, starting in block 1.  All entries
        /// have the same values in the first 3 fields, so the size of the partition map can
        /// be determined by reading any entry.
        /// </summary>
        private class MapEntry {
            public const int LENGTH = 512;
            private const int FIXED_LENGTH = 136;

            /// <summary>partition signature</summary>
            public ushort pmSig;

            /// <summary>reserved</summary>
            public ushort pmSigPad;

            /// <summary>number of blocks in partition map</summary>
            public uint pmMapBlkCnt;

            /// <summary>first physical block of partition</summary>
            public uint pmPyPartStart;

            /// <summary>number of blocks in partition</summary>
            public uint pmPartBlkCnt;

            /// <summary>partition name</summary>
            public byte[] pmPartName = new byte[32];

            /// <summary>partition type</summary>
            public byte[] pmParType = new byte[32];

            /// <summary>first logical block of data area</summary>
            public uint pmLgDataStart;

            /// <summary>number of blocks in data area</summary>
            public uint pmDataCnt;

            /// <summary>partition status information</summary>
            public uint pmPartStatus;

            /// <summary>first logical block of boot code</summary>
            public uint pmLgBootStart;

            /// <summary>size of boot code, in bytes</summary>
            public uint pmBootSize;

            /// <summary>boot code load address</summary>
            public uint pmBootAddr;

            /// <summary>reserved</summary>
            public uint pmBootAddr2;

            /// <summary>boot code entry point</summary>
            public uint pmBootEntry;

            /// <summary>reserved</summary>
            public uint pmBootEntry2;

            /// <summary>boot code checksum</summary>
            public uint pmBootCksum;

            /// <summary>processor type</summary>
            public byte[] pmProcessor = new byte[16];

            /// <summary>reserved</summary>
            public ushort[] pmPad = new ushort[188];

            //
            // Computed fields
            //

            public string PartitionName { get; private set; } = string.Empty;
            public string PartitionType { get; private set; } = string.Empty;
            public string Processor { get; private set; } = string.Empty;

            public void Load(byte[] buf, int offset) {
                int startOffset = offset;
                pmSig = RawData.ReadU16BE(buf, ref offset);
                pmSigPad = RawData.ReadU16BE(buf, ref offset);
                pmMapBlkCnt = RawData.ReadU32BE(buf, ref offset);
                pmPyPartStart = RawData.ReadU32BE(buf, ref offset);
                pmPartBlkCnt = RawData.ReadU32BE(buf, ref offset);
                Array.Copy(buf, offset, pmPartName, 0, pmPartName.Length);
                offset += pmPartName.Length;
                Array.Copy(buf, offset, pmParType, 0, pmParType.Length);
                offset += pmParType.Length;
                pmLgDataStart = RawData.ReadU32BE(buf, ref offset);
                pmDataCnt = RawData.ReadU32BE(buf, ref offset);
                pmPartStatus = RawData.ReadU32BE(buf, ref offset);
                pmLgBootStart = RawData.ReadU32BE(buf, ref offset);
                pmBootSize = RawData.ReadU32BE(buf, ref offset);
                pmBootAddr = RawData.ReadU32BE(buf, ref offset);
                pmBootAddr2 = RawData.ReadU32BE(buf, ref offset);
                pmBootEntry = RawData.ReadU32BE(buf, ref offset);
                pmBootEntry2 = RawData.ReadU32BE(buf, ref offset);
                pmBootCksum = RawData.ReadU32BE(buf, ref offset);
                Array.Copy(buf, offset, pmProcessor, 0, pmProcessor.Length);
                offset += pmProcessor.Length;
                Debug.Assert(offset - startOffset == FIXED_LENGTH);
                for (int i = 0; i < pmPad.Length; i++) {
                    RawData.ReadU16BE(buf, ref offset);
                }
                Debug.Assert(offset - startOffset == LENGTH);

                // Assume Mac OS Roman for strings.
                PartitionName = ExtractString(buf, 0x10, 32);
                PartitionType = ExtractString(buf, 0x30, 32);
                Processor = ExtractString(buf, 0x78, 16);
            }

            private string ExtractString(byte[] buf, int offset, int maxLen) {
                int len;
                for (len = 0; len < maxLen; len++) {
                    if (buf[offset + len] == 0x00) {
                        break;
                    }
                }
                return MacChar.MacToUnicode(buf, offset, len, MacChar.Encoding.Roman);
            }

            public override string ToString() {
                return "[PM: name='" + PartitionName + "' type='" + PartitionType + "' proc='" +
                    Processor + "' start=" + pmPyPartStart + " count=" + pmPartBlkCnt +
                    " (" + (pmPartBlkCnt / (1024 * 1024.0 / BLOCK_SIZE)).ToString("F2") + " MiB)]";
            }
        }

        private List<Partition> mPartitions;
        private AppHook mAppHook;


        // Delegate: test image to see if it's ours.
        public static TestResult TestImage(IChunkAccess chunkAccess, AppHook appHook) {
            if (!chunkAccess.HasBlocks) {
                return TestResult.No;
            }
            if (chunkAccess is ChunkSubset) {
                // Don't look for APM inside other partitions.
                return TestResult.No;
            }

            // Read the DDR out of block 0.
            byte[] blockBuf = new byte[BLOCK_SIZE];
            chunkAccess.ReadBlock(0, blockBuf, 0);
            DriverDescriptorRecord ddr = new DriverDescriptorRecord();
            ddr.Load(blockBuf, 0);
            if (ddr.sbSig != DDR_SIGNATURE || ddr.sbBlkSize != BLOCK_SIZE) {
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

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="chunkAccess">Chunk access object for full disk.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <exception cref="ArgumentException">Chunk access does not provide blocks.</exception>
        /// <exception cref="InvalidDataException">This is not an APM image.</exception>
        public APM(IChunkAccess chunkAccess, AppHook appHook) {
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
            // We need this to be read-write for utilities that write to partitions.  However,
            // for safety this should close down if we have an open filesystem on any of the
            // partitions.  I'm not sure that's worth the complexity (the caller could always
            // just write directly to the underlying stream).
            RawAccess.AccessLevel = GatedChunkAccess.AccessLvl.Open;

            // Check for overlap.
            if (!ValidatePartitionMap(chunkAccess, Notes)) {
                Notes.AddE("Partition map validation failed");
                IsDubious = true;
            }
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
        /// Loads the list of partitions from the chunk provider.
        /// </summary>
        /// <param name="chunkAccess">Block access object.</param>
        /// <param name="isTestOnly">If true, no partition objects will be created.</param>
        /// <param name="notes">Partition note tracker.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <returns>List of partitions, or null if the format was incorrect.  If
        ///   <paramref name="isTestOnly"/> is true, the list will have zero entries.</returns>
        private static List<Partition>? LoadPartitions(IChunkAccess chunkAccess, bool isTestOnly,
                Notes? notes, AppHook appHook) {
            long totalBlocks = chunkAccess.FormattedLength / BLOCK_SIZE;
            if (totalBlocks < 2) {
                return null;        // need at least the DDR and the PM
            }
            List<Partition> partitions = new List<Partition>();
            byte[] blockBuf = new byte[BLOCK_SIZE];

            // Partition map starts in block 1.  The map length is stored in each entry.
            uint blockNum = 1;
            uint partCount = 0;
            do {
                chunkAccess.ReadBlock(blockNum, blockBuf, 0);
                MapEntry mapEnt = new MapEntry();
                mapEnt.Load(blockBuf, 0);
                if (mapEnt.pmSig != PART_SIGNATURE) {
                    return null;
                }
                uint thisCount = mapEnt.pmMapBlkCnt;
                if (partCount != 0 && thisCount != partCount) {
                    Debug.WriteLine("APM: different partition map counts");
                    notes?.AddW("The partition map size is not the same in all entries");
                    // keep going? in theory only the first one matters
                }
                if (thisCount > MAX_PARTITIONS || thisCount > totalBlocks - 1) {
                    Debug.WriteLine("APM: too many partitions (" + thisCount + ")");
                    return null;
                }
                partCount = thisCount;

                if (!isTestOnly) {
                    appHook.LogI("  " + mapEnt);
                }

                bool skipCreate = false;
                long blockCount = mapEnt.pmPartBlkCnt;
                if (mapEnt.pmPyPartStart > totalBlocks) {
                    //Debug.WriteLine("Partition '" + mapEnt.PartitionName + "' starts past EOF");
                    notes?.AddE("Start of partition ('" + mapEnt.PartitionName +
                        "') at block=" + mapEnt.pmPyPartStart + " is past end of file (" +
                        totalBlocks + " blocks)");
                    skipCreate = true;
                } else if ((long)mapEnt.pmPyPartStart + mapEnt.pmPartBlkCnt > totalBlocks) {
                    //Debug.WriteLine("Partition '" + mapEnt.PartitionName + "' extends past EOF");
                    blockCount = totalBlocks - mapEnt.pmPyPartStart;
                    notes?.AddW("Length of partition ('" + mapEnt.PartitionName +
                        "') reduced from " + mapEnt.pmPartBlkCnt + " blocks to " +
                        blockCount + " blocks");
                }

                if (!isTestOnly && !skipCreate) {
                    APM_Partition part = new APM_Partition(chunkAccess,
                        (long)mapEnt.pmPyPartStart * BLOCK_SIZE, (long)blockCount * BLOCK_SIZE,
                        mapEnt.PartitionName, mapEnt.PartitionType, appHook);
                    partitions.Add(part);
                }

                blockNum++;
            } while (blockNum <= partCount);

            //notes?.AddI("Found " + partitions.Count + " APM partitions");

            return partitions;
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

            // All blocks in disk must be represented, except the DDR in block 0.
            bool result = true;
            long lastEnd = 1 * BLOCK_SIZE;
            for (int i = 0; i < sorted.Length; i++) {
                if (sorted[i].StartOffset < lastEnd) {
                    notes.AddE("Partitions " + (i - 1) + " and " + i + " overlap");
                    result = false;     // danger!
                } else if (sorted[i].StartOffset > lastEnd) {
                    notes.AddI("Unexpected gap before partition " + i + " (" +
                        ((sorted[i].StartOffset - lastEnd) / BLOCK_SIZE) + " blocks)");
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
