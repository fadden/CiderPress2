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
        public ushort NumFiles => drNmFls;
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
        public uint NextFileNum => drNxtFNum;
        private uint drNxtFNum;

        /// <summary>drFreeBks: number of unused allocation blocks.</summary>
        public ushort FreeAllocBlocks => drFreeBks;
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

        //
        // Volume Allocation Block Map
        //

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
        /// Optional volume usage tracking.
        /// </summary>
        private VolumeUsage? mVolumeUsage;


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
        /// Initializes the volume usage map, setting the "in use" flags.  This must not be
        /// called until the MDB has been loaded.
        /// </summary>
        /// <remarks>
        /// This is optional.  Only call this if you're interested in the VolumeUsage data.
        /// </remarks>
        public void InitVolumeUsage() {
            Debug.Assert(mVolumeUsage == null);
            Debug.Assert(NumAllocBlocks != 0);
            mVolumeUsage = new VolumeUsage(NumAllocBlocks);

            for (uint chunk = 0; chunk < NumAllocBlocks; chunk++) {
                if (mAllocBlockMap[chunk] != 0) {
                    mVolumeUsage.MarkInUse(chunk);
                }
            }
        }

        public override string ToString() {
            return "[MFS_MDB '" + mVolumeName + "']";
        }
    }
}
