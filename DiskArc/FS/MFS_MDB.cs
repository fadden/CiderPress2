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
    /// Manage an MFS Master Directory Block (blocks 2 and 3).
    /// </summary>
    internal class MFS_MDB {
        public const ushort SIGNATURE = 0xd2d7;     // 'RW' in high ASCII
        public const int INFO_LENGTH = 64;          // Volume Info area length
        public const int VABM_LENGTH = BLOCK_SIZE * 2 - INFO_LENGTH;    // 960 bytes
        public const int MAX_ALLOC_BLOCKS = (VABM_LENGTH * 2) / 3;      // 640 entries
        public const uint MDB_BLOCK_NUM = 2;        // first block number
        public const int RESERVED_BLOCKS = 2;       // alloc blocks 0 and 1 are reserved

        //
        // Properties that map to fields.
        //

        [Flags]
        public enum AttribFlags : ushort {
            LockedByHardware = 0x0080,
            LockedBySoftware = 0x8000,
        }

        /// <summary>drSigWord: volume signature, $D2D7 for MFS.</summary>
        public ushort Signature => drSigWord;
        private ushort drSigWord;

        /// <summary>drCrDate: date and time of volume creation.</summary>
        public DateTime CreateDate => TimeStamp.ConvertDateTime_HFS(drCrDate);
        private uint drCrDate;

        public DateTime BackupDate => TimeStamp.ConvertDateTime_HFS(drLsBkUp);
        private uint drLsBkUp;

        /// <summary>
        /// drAtrb: volume attribute flags:
        /// <list type="bullet">
        ///   <item>bit  7 (0x0080): volume locked by hardware</item>
        ///   <item>bit 15 (0x8000): volume locked by software</item>
        /// </list>
        /// </summary>
        public ushort Attributes => drAtrb;
        private ushort drAtrb;

        /// <summary>drNmFls: number of files in directory.</summary>
        public ushort NumFiles { get => drNmFls; internal set { drNmFls = value; IsDirty = true; } }
        private ushort drNmFls;

        /// <summary>drDrSt: first block of directory.</summary>
        public ushort DirectoryStart => drDrSt;
        private ushort drDrSt;

        /// <summary>drBlLen: length of directory, in blocks.</summary>
        public ushort DirectoryLength => drBlLen;
        private ushort drBlLen;

        /// <summary>drNmAlBlks: number of allocation blocks.</summary>
        public ushort NumAllocBlocks => drNmAlBlks;
        private ushort drNmAlBlks;

        /// <summary>drAlBlkSiz: size of an allocation block, in bytes.</summary>
        public uint AllocBlockSize => drAlBlkSiz;
        private uint drAlBlkSiz;

        /// <summary>drClpSiz: number of bytes to allocate at a time.</summary>
        public uint ClumpSize => drClpSiz;
        private uint drClpSiz;

        /// <summary>drAlBlSt: block number where file allocation blocks start.</summary>
        public ushort AllocBlockStart => drAlBlSt;
        private ushort drAlBlSt;

        /// <summary>drNxtFNum: next unused file number.</summary>
        public uint NextFileNum { get => drNxtFNum; internal set { drNxtFNum = value; IsDirty = true; } }
        private uint drNxtFNum;

        /// <summary>drFreeBks: number of unused allocation blocks.</summary>
        public ushort FreeAllocBlocks { get => drFreeBks; internal set { drFreeBks = value; IsDirty = true; } }
        private ushort drFreeBks;

        /// <summary>drVN: volume name; length byte followed by up to 27 characters.</summary>
        public string VolumeName => mVolumeName;
        private byte[] drVN = new byte[MFS.MAX_VOL_NAME_LEN + 1];
        private string mVolumeName = string.Empty;

        private void LoadVolumeInfo(byte[] buf, int offset) {
            int startOffset = offset;
            drSigWord = RawData.ReadU16BE(buf, ref offset);
            drCrDate = RawData.ReadU32BE(buf, ref offset);
            drLsBkUp = RawData.ReadU32BE(buf, ref offset);
            drAtrb = RawData.ReadU16BE(buf, ref offset);
            drNmFls = RawData.ReadU16BE(buf, ref offset);
            drDrSt = RawData.ReadU16BE(buf, ref offset);
            drBlLen = RawData.ReadU16BE(buf, ref offset);
            drNmAlBlks = RawData.ReadU16BE(buf, ref offset);
            drAlBlkSiz = RawData.ReadU32BE(buf, ref offset);
            drClpSiz = RawData.ReadU32BE(buf, ref offset);
            drAlBlSt = RawData.ReadU16BE(buf, ref offset);
            drNxtFNum = RawData.ReadU32BE(buf, ref offset);
            drFreeBks = RawData.ReadU16BE(buf, ref offset);
            Array.Copy(buf, offset, drVN, 0, drVN.Length);
            offset += drVN.Length;
            Debug.Assert(offset == startOffset + INFO_LENGTH);

            // This will be the empty string if the volume name is invalid.
            mVolumeName = MFS_FileEntry.GenerateCookedName(drVN, true);
        }

        /// <summary>
        /// Number of logical (512-byte) blocks per allocation block.
        /// </summary>
        public uint LogicalPerAllocBlock { get { return AllocBlockSize / BLOCK_SIZE; } }

        //
        // Volume Allocation Block Map
        //

        /// <summary>
        /// Expanded form of allocation block map.  This array spans the full master directory
        /// block, and may be over-sized for a given volume.
        /// </summary>
        private ushort[] mAllocBlockMap = new ushort[MAX_ALLOC_BLOCKS];

        /// <summary>
        /// Loads the allocation map into an array.
        /// </summary>
        private void LoadAllocMap(byte[] buf, int offset) {
            int startOffset = offset;

            // The map fills the area of the 1KB MDB "block" that isn't occupied by the
            // Volume Info chunk.
            for (int i = 0; i < MAX_ALLOC_BLOCKS; i += 2) {
                // Grab the next 3 bytes, and convert it into two 12-bit values.
                byte val0 = buf[offset++];
                byte val1 = buf[offset++];
                byte val2 = buf[offset++];
                mAllocBlockMap[i] = (ushort)((val0 << 4) | (val1 >> 4));
                mAllocBlockMap[i + 1] = (ushort)(((val1 & 0x0f) << 8) | val2);
            }
            Debug.Assert(offset - startOffset == VABM_LENGTH);
        }

        //
        // Innards.
        //

        /// <summary>
        /// Buffer for raw data.
        /// </summary>
        private byte[] mDiskBuf = new byte[BLOCK_SIZE * 2];

        /// <summary>
        /// Chunk access object.
        /// </summary>
        private IChunkAccess mChunkAccess;

        /// <summary>
        /// True if the in-memory copy of the MDB (volume info and/or allocation map) has been
        /// modified and needs to be written back to disk.
        /// </summary>
        public bool IsDirty { get; internal set; }

        /// <summary>
        /// Optional volume usage tracking.
        /// </summary>
        public VolumeUsage? VolUsage { get; private set; }


        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="chunkAccess">Chunk access object.</param>
        public MFS_MDB(IChunkAccess chunkAccess) {
            mChunkAccess = chunkAccess;
        }

        /// <summary>
        /// Reads the contents of the MDB into memory, and parses them.
        /// </summary>
        public void Read() {
            mChunkAccess.ReadBlock(MDB_BLOCK_NUM, mDiskBuf, 0);
            mChunkAccess.ReadBlock(MDB_BLOCK_NUM + 1, mDiskBuf, BLOCK_SIZE);
            LoadVolumeInfo(mDiskBuf, 0);
            LoadAllocMap(mDiskBuf, INFO_LENGTH);
        }

        /// <summary>
        /// Serializes the 64-byte Volume Information chunk (inverse of LoadVolumeInfo).
        /// </summary>
        private void StoreVolumeInfo(byte[] buf, int offset) {
            int startOffset = offset;
            RawData.WriteU16BE(buf, ref offset, drSigWord);
            RawData.WriteU32BE(buf, ref offset, drCrDate);
            RawData.WriteU32BE(buf, ref offset, drLsBkUp);
            RawData.WriteU16BE(buf, ref offset, drAtrb);
            RawData.WriteU16BE(buf, ref offset, drNmFls);
            RawData.WriteU16BE(buf, ref offset, drDrSt);
            RawData.WriteU16BE(buf, ref offset, drBlLen);
            RawData.WriteU16BE(buf, ref offset, drNmAlBlks);
            RawData.WriteU32BE(buf, ref offset, drAlBlkSiz);
            RawData.WriteU32BE(buf, ref offset, drClpSiz);
            RawData.WriteU16BE(buf, ref offset, drAlBlSt);
            RawData.WriteU32BE(buf, ref offset, drNxtFNum);
            RawData.WriteU16BE(buf, ref offset, drFreeBks);
            Array.Copy(drVN, 0, buf, offset, drVN.Length);
            offset += drVN.Length;
            Debug.Assert(offset == startOffset + INFO_LENGTH);
        }

        /// <summary>
        /// Serializes the Volume Allocation Block Map into packed 12-bit form (inverse of
        /// LoadAllocMap).
        /// </summary>
        private void StoreAllocMap(byte[] buf, int offset) {
            int startOffset = offset;
            for (int i = 0; i < MAX_ALLOC_BLOCKS; i += 2) {
                ushort v0 = mAllocBlockMap[i];
                ushort v1 = mAllocBlockMap[i + 1];
                buf[offset++] = (byte)((v0 >> 4) & 0xff);
                buf[offset++] = (byte)(((v0 & 0x0f) << 4) | ((v1 >> 8) & 0x0f));
                buf[offset++] = (byte)(v1 & 0xff);
            }
            Debug.Assert(offset - startOffset == VABM_LENGTH);
        }

        /// <summary>
        /// Writes the MDB (volume info + allocation map) back to blocks 2 and 3, if it has been
        /// modified.
        /// </summary>
        /// <remarks>
        /// <para>MFS keeps a backup copy of the MDB at the last two logical blocks of the disk,
        /// but it is a point-in-time snapshot (written at format/backup time), not a live mirror
        /// of the primary.  On real disks it commonly still reflects the freshly-initialized
        /// state (zero files, all blocks free) even after files have been added.  We deliberately
        /// do not update it: CiderPress II only ever reads the primary copy, so keeping the
        /// backup in sync would gain nothing and would actually make our disks diverge from how
        /// real MFS leaves the backup, while adding a risk of clobbering file storage if the
        /// backup location were miscalculated.</para>
        /// </remarks>
        public void Write() {
            if (!IsDirty) {
                return;
            }
            StoreVolumeInfo(mDiskBuf, 0);
            StoreAllocMap(mDiskBuf, INFO_LENGTH);
            mChunkAccess.WriteBlock(MDB_BLOCK_NUM, mDiskBuf, 0);
            mChunkAccess.WriteBlock(MDB_BLOCK_NUM + 1, mDiskBuf, BLOCK_SIZE);
            IsDirty = false;
        }

        /// <summary>
        /// Initializes the volume information and allocation map for a freshly-formatted volume,
        /// and writes both the primary copy (blocks 2-3) and the backup copy (the last two
        /// logical blocks of the disk).
        /// </summary>
        /// <remarks>
        /// The backup copy is written here as a point-in-time snapshot, matching how real MFS
        /// initialization behaves; it is deliberately not kept in sync afterward (see the remarks
        /// on <see cref="Write"/>).
        /// </remarks>
        /// <param name="volumeName">Volume name (already validated).</param>
        /// <param name="createWhen">Volume creation date/time.</param>
        /// <param name="dirStart">First logical block of the file directory.</param>
        /// <param name="dirLength">Length of the file directory, in logical blocks.</param>
        /// <param name="numAllocBlocks">Number of allocation blocks in the volume.</param>
        /// <param name="allocBlockSize">Size of an allocation block, in bytes.</param>
        /// <param name="clumpSize">Clump size hint, in bytes.</param>
        /// <param name="allocBlockStart">First logical block of the allocation-block area.</param>
        /// <param name="diskBlockCount">Total number of logical blocks on the disk.</param>
        public void Format(string volumeName, DateTime createWhen, ushort dirStart,
                ushort dirLength, ushort numAllocBlocks, uint allocBlockSize, uint clumpSize,
                ushort allocBlockStart, int diskBlockCount) {
            uint hfsTime = TimeStamp.ConvertDateTime_HFS(createWhen);
            drSigWord = SIGNATURE;
            drCrDate = hfsTime;
            drLsBkUp = hfsTime;
            drAtrb = 0;
            drNmFls = 0;
            drDrSt = dirStart;
            drBlLen = dirLength;
            drNmAlBlks = numAllocBlocks;
            drAlBlkSiz = allocBlockSize;
            drClpSiz = clumpSize;
            drAlBlSt = allocBlockStart;
            drNxtFNum = 1;                          // file numbers start at 1
            drFreeBks = numAllocBlocks;             // all blocks free

            // Set the volume name.  The caller is expected to have validated it already.
            byte[]? rawName = MFS_FileEntry.GenerateRawVolumeName(volumeName);
            if (rawName == null) {
                throw new ArgumentException("Invalid MFS volume name '" + volumeName + "'");
            }
            Array.Clear(drVN, 0, drVN.Length);
            Array.Copy(rawName, drVN, drVN.Length);
            mVolumeName = MFS_FileEntry.GenerateCookedName(drVN, true);

            // All allocation blocks start out free (map entry 0).
            Array.Clear(mAllocBlockMap, 0, mAllocBlockMap.Length);

            // Serialize and write the primary copy (blocks 2-3) and an identical backup copy at
            // the end of the disk.
            StoreVolumeInfo(mDiskBuf, 0);
            StoreAllocMap(mDiskBuf, INFO_LENGTH);
            mChunkAccess.WriteBlock(MDB_BLOCK_NUM, mDiskBuf, 0);
            mChunkAccess.WriteBlock(MDB_BLOCK_NUM + 1, mDiskBuf, BLOCK_SIZE);
            mChunkAccess.WriteBlock((uint)(diskBlockCount - 2), mDiskBuf, 0);
            mChunkAccess.WriteBlock((uint)(diskBlockCount - 1), mDiskBuf, BLOCK_SIZE);
            IsDirty = false;
        }

        /// <summary>
        /// Initializes the volume usage map, setting the "in use" flags.  This must not be
        /// called until the MDB has been loaded.
        /// </summary>
        /// <remarks>
        /// This is optional.  Only call this if you're interested in the VolumeUsage data.
        /// </remarks>
        public void InitVolumeUsage() {
            Debug.Assert(VolUsage == null);
            Debug.Assert(NumAllocBlocks != 0);
            VolUsage = new VolumeUsage(NumAllocBlocks);

            for (uint chunk = 0; chunk < NumAllocBlocks; chunk++) {
                if (mAllocBlockMap[chunk] != 0) {
                    VolUsage.MarkInUse(chunk);
                }
            }
        }

        /// <summary>
        /// Gets the 12-bit allocation map entry for allocation block N.  Allocation blocks
        /// start at 2.
        /// </summary>
        /// <param name="index"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
        public ushort GetAllocMapEntry(int index) {
            if (index < RESERVED_BLOCKS || index >= NumAllocBlocks + RESERVED_BLOCKS) {
                throw new ArgumentOutOfRangeException(nameof(index), index,
                    "range is [2," + (NumAllocBlocks + RESERVED_BLOCKS - 1) + "]");
            }
            return mAllocBlockMap[index - RESERVED_BLOCKS];
        }

        /// <summary>
        /// Sets the 12-bit allocation map entry for allocation block N.  Allocation blocks
        /// start at 2.  The value is another allocation block number (chain link), 1 (end of
        /// chain), or 0 (free).
        /// </summary>
        public void SetAllocMapEntry(int index, ushort value) {
            if (index < RESERVED_BLOCKS || index >= NumAllocBlocks + RESERVED_BLOCKS) {
                throw new ArgumentOutOfRangeException(nameof(index), index,
                    "range is [2," + (NumAllocBlocks + RESERVED_BLOCKS - 1) + "]");
            }
            mAllocBlockMap[index - RESERVED_BLOCKS] = value;
            IsDirty = true;
        }

        /// <summary>
        /// Allocates one free allocation block.  The block is marked as end-of-chain (value 1)
        /// in the allocation map, and the free-block count is decremented.  The caller is
        /// responsible for linking it into a file's block chain.
        /// </summary>
        /// <param name="entry">File entry to associate with the block in the volume usage
        ///   map (used only if usage tracking is active).</param>
        /// <returns>Absolute allocation block number (>= RESERVED_BLOCKS).</returns>
        /// <exception cref="DiskFullException">No free allocation blocks remain.</exception>
        public ushort AllocBlock(IFileEntry entry) {
            for (int i = 0; i < NumAllocBlocks; i++) {
                if (mAllocBlockMap[i] == 0) {
                    mAllocBlockMap[i] = 1;      // in use, end of chain
                    drFreeBks--;
                    IsDirty = true;
                    VolUsage?.AllocChunk((uint)i, entry);
                    return (ushort)(i + RESERVED_BLOCKS);
                }
            }
            throw new DiskFullException("No free allocation blocks");
        }

        /// <summary>
        /// Frees a single allocation block, marking it unused in the map and incrementing the
        /// free-block count.  The caller is responsible for fixing up any chain links.
        /// </summary>
        /// <param name="allocBlockNum">Absolute allocation block number (>= RESERVED_BLOCKS).</param>
        public void FreeBlock(ushort allocBlockNum) {
            int idx = allocBlockNum - RESERVED_BLOCKS;
            if (idx < 0 || idx >= NumAllocBlocks) {
                throw new ArgumentOutOfRangeException(nameof(allocBlockNum), allocBlockNum,
                    "invalid allocation block");
            }
            if (mAllocBlockMap[idx] != 0) {
                mAllocBlockMap[idx] = 0;
                drFreeBks++;
                IsDirty = true;
                VolUsage?.FreeChunk((uint)idx);
            }
        }

        /// <summary>
        /// Frees an entire fork block chain, starting from the given allocation block.  Each
        /// block in the chain is marked unused and the free-block count is incremented.
        /// </summary>
        /// <remarks>
        /// The walk is bounded and tolerant of a corrupt link: if a link points outside the
        /// valid range it stops rather than throwing, so a delete can't be derailed midway by a
        /// slightly damaged chain (which matters when the volume was opened with --fast, i.e.
        /// without a full block-chain scan).
        /// </remarks>
        /// <param name="startAblk">First allocation block of the fork, or 0 for an empty
        ///   fork.</param>
        public void FreeChain(ushort startAblk) {
            if (startAblk == 0) {
                return;             // empty fork, nothing to free
            }
            ushort ablk = startAblk;
            int guard = 0;
            while (guard++ <= NumAllocBlocks) {
                int idx = ablk - RESERVED_BLOCKS;
                if (idx < 0 || idx >= NumAllocBlocks) {
                    break;          // corrupt link; stop rather than throw
                }
                ushort next = mAllocBlockMap[idx];
                FreeBlock(ablk);
                if (next == 1 || next == 0) {
                    break;          // end of chain (1) or already-free (0, corrupt)
                }
                ablk = next;
            }
        }

        /// <summary>
        /// Counts up the number of free blocks in the allocation map.
        /// </summary>
        public uint CalcFreeBlocks() {
            uint freeCount = 0;
            for (int i = 0; i < NumAllocBlocks; i++) {
                if (mAllocBlockMap[i] == 0) {
                    freeCount++;
                }
            }
            return freeCount;
        }

        public override string ToString() {
            return "[MFS_MDB '" + mVolumeName + "']";
        }
    }
}
