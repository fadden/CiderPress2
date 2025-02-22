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

using CommonUtil;
using static DiskArc.Defs;
using static DiskArc.FS.HFS_Struct;

namespace DiskArc.FS {
    /// <summary>
    /// Manage an HFS Master Directory Block.
    /// </summary>
    /// <remarks>
    /// The data lives in block 2, with a copy occasionally written to (last_block - 1).
    /// </remarks>
    internal class HFS_MDB {
        //
        // Properties that map to fields.  IM:F 2-60
        //

        public const int LENGTH = 162;

        [Flags]
        public enum AttribFlags : ushort {
            LockedByHardware = 0x0080,
            WasUnmounted = 0x0100,
            HasBeenSpared = 0x0200,
            LockedBySoftware = 0x8000
        }

        /// <summary>drSigWord: volume signature, $4244 for HFS.  Was $D2D7 for MFS.</summary>
        public ushort Signature {
            get { return drSigWord; }
            set { drSigWord = value; IsDirty = true; }
        }
        private ushort drSigWord;

        /// <summary>drCrDate: date and time of volume creation.</summary>
        public DateTime CreateDate {
            get { return TimeStamp.ConvertDateTime_HFS(drCrDate); }
            set { drCrDate = TimeStamp.ConvertDateTime_HFS(value); IsDirty = true; }
        }
        private uint drCrDate;

        /// <summary>drLsMod: date and time of last modification.</summary>
        public DateTime ModifyDate {
            get { return TimeStamp.ConvertDateTime_HFS(drLsMod); }
            set { drLsMod = TimeStamp.ConvertDateTime_HFS(value); IsDirty = true; }
        }
        private uint drLsMod;

        /// <summary>
        /// drAtrb: volume attribute flags:
        /// <list type="bullet">
        ///   <item>bit  7 (0x0080): volume locked by hardware</item>
        ///   <item>bit  8 (0x0100): volume was successfully unmounted</item>
        ///   <item>bit  9 (0x0200): volume has had its bad blocks spared</item>
        ///   <item>bit 15 (0x8000): volume locked by software</item>
        /// </list>
        /// </summary>
        public ushort Attributes {
            get { return drAtrb; }
            set { drAtrb = value; IsDirty = true; }
        }
        private ushort drAtrb;
        public const ushort ATTR_UNMOUNTED = 0x0100;

        /// <summary>drNmFls: number of files in root directory.</summary>
        public ushort RootFileCount {
            get { return drNmFls; }
            set { drNmFls = value; IsDirty = true; }
        }
        private ushort drNmFls;

        /// <summary>drVBMSt: logical block number of start of volume bitmap.
        /// Always 3.</summary>
        public ushort VolBitmapStart {
            get { return drVBMSt; }
            set { drVBMSt = value; IsDirty = true; }
        }
        private ushort drVBMSt;

        /// <summary>drAllocPtr: allocation block at which next allocation search should
        /// begin.</summary>
        public ushort NextAllocation {
            get { return drAllocPtr; }
            set { drAllocPtr = value; IsDirty = true; }
        }
        private ushort drAllocPtr;

        /// <summary>drNmAlBlks: number of allocation blocks in the volume
        /// (max 65535).</summary>
        public ushort TotalBlocks {
            get { return drNmAlBlks; }
            set { drNmAlBlks = value; IsDirty = true; }
        }
        private ushort drNmAlBlks;

        /// <summary>drAlBlkSiz: allocation block size, in bytes.  Must be a multiple of
        /// 512.</summary>
        public uint BlockSize {
            get { return drAlBlkSiz; }
            set { drAlBlkSiz = value; IsDirty = true; }
        }
        private uint drAlBlkSiz;

        /// <summary>drClpSiz: default clump size, in bytes.</summary>
        public uint ClumpSize {
            get { return drClpSiz; }
            set { drClpSiz = value; IsDirty = true; }
        }
        private uint drClpSiz;

        /// <summary>drAlBlSt: logical block number of first allocation block.</summary>
        public ushort AllocBlockStart {
            get { return drAlBlSt; }
            set { drAlBlSt = value; IsDirty = true; }
        }
        private ushort drAlBlSt;

        /// <summary>drNxtCNID: next unused Catalog Node ID (directory ID or file ID).</summary>
        public uint NextCatalogID {
            get { return drNxtCNID; }
            set { drNxtCNID = value; IsDirty = true; }
        }
        private uint drNxtCNID;

        /// <summary>drFreeBks: number of unused allocation blocks.</summary>
        public ushort FreeBlocks {
            get { return drFreeBks; }
            set { drFreeBks = value; IsDirty = true; }
        }
        private ushort drFreeBks;

        /// <summary>drVN: volume name; length byte followed by up to 27 characters.</summary>
        public string VolumeName {
            get { return mVolumeName; }
            set { mVolumeName = value; IsDirty = true; }
        }
        private byte[] drVN = new byte[HFS.MAX_VOL_NAME_LEN + 1];
        private string mVolumeName = string.Empty;

        /// <summary>drVolBkUp: date and time of last volume backup.</summary>
        public DateTime BackupDate {
            get { return TimeStamp.ConvertDateTime_HFS(drVolBkUp); }
            set { drVolBkUp = TimeStamp.ConvertDateTime_HFS(value); IsDirty = true; }
        }
        private uint drVolBkUp;

        /// <summary>drVSeqNum: volume backup sequence number.</summary>
        public ushort BackupSeqNum {
            get { return drVSeqNum; }
            set { drVSeqNum = value; IsDirty = true; }
        }
        private ushort drVSeqNum;

        /// <summary>drWrCnt: volume write count (appears to be mount/unmount count?)</summary>
        public uint WriteCount {
            get { return drWrCnt; }
            set { drWrCnt = value; IsDirty = true; }
        }
        private uint drWrCnt;

        /// <summary>drXTClpSiz: clump size for extents overflow file, in bytes.</summary>
        public uint ExtentsClumpSize {
            get { return drXTClpSiz; }
            set { drXTClpSiz = value; IsDirty = true; }
        }
        private uint drXTClpSiz;

        /// <summary>drCTClpSiz: clump size for catalog file, in bytes.</summary>
        public uint CatalogClumpSize {
            get { return drCTClpSiz; }
            set { drCTClpSiz = value; IsDirty = true; }
        }
        private uint drCTClpSiz;

        /// <summary>drNmRtDirs: number of directories in the root directory.</summary>
        public ushort RootDirCount {
            get { return drNmRtDirs; }
            set { drNmRtDirs = value; IsDirty = true; }
        }
        private ushort drNmRtDirs;

        /// <summary>drFilCnt: number of files on the volume.</summary>
        public uint FileCount {
            get { return drFilCnt; }
            set { drFilCnt = value; IsDirty = true; }
        }
        private uint drFilCnt;

        /// <summary>drDirCnt: number of directories on the volume.</summary>
        public uint DirCount {
            get { return drDirCnt; }
            set { drDirCnt = value; IsDirty = true; }
        }
        private uint drDirCnt;

        /// <summary>drFndrInfo: information used by the Macintosh Finder.</summary>
        private uint[] drFndrInfo = new uint[8];
        public uint[] FndrInfo {
            get {
                uint[] copy = new uint[8];
                Array.Copy(drFndrInfo, copy, drFndrInfo.Length);
                return copy;
            }
        }

        /// <summary>drVCSize / drEmbedSigWord: originally, size (in allocation blocks) of the
        /// volume cache.  Changed to embedded volume signature.</summary>
        private ushort drVCSize;

        /// <summary>drVBMCSize / drEmbedExtent: originally, size (in allocation blocks) of the
        /// volume bitmap cache.  Changed to embedded volume extent start block.</summary>
        private ushort drVBMCSize;

        /// <summary>drCtlCSize / drEmbedExtent: originally, size (in allocation blocks) of the
        /// common volume cache.  Changed to embedded volume block count.</summary>
        private ushort drCtlCSize;

        /// <summary>drXTFlSize: size (in bytes) of the extents overflow file.</summary>
        public uint ExtentsFileSize {
            get { return drXTFlSize; }
            set { drXTFlSize = value; IsDirty = IsAltDirty = true; }
        }
        private uint drXTFlSize;

        /// <summary>drXTExtRec: first extent record for the extents overflow file.
        /// Note: get{}/set{} copy the object contents.</summary>
        public ExtDataRec ExtentsFileRec {
            get { return new ExtDataRec(drXTExtRec); }
            set { drXTExtRec.CopyFrom(value); IsDirty = true; }
        }
        private ExtDataRec drXTExtRec = new ExtDataRec();

        /// <summary>drCTFlSize: size (in bytes) of the catalog file.</summary>
        public uint CatalogFileSize {
            get { return drCTFlSize; }
            set { drCTFlSize = value; IsDirty = IsAltDirty = true; }
        }
        private uint drCTFlSize;

        /// <summary>drCTExtRec: first extent record for the catalog file.
        /// Note: get{}/set{} copy the object contents.</summary>
        public ExtDataRec CatalogFileRec {
            get { return new ExtDataRec(drCTExtRec); }
            set { drCTExtRec.CopyFrom(value); IsDirty = true; }
        }
        private ExtDataRec drCTExtRec = new ExtDataRec();

        private void Load(byte[] buf, int offset) {
            int startOffset = offset;
            drSigWord = RawData.ReadU16BE(buf, ref offset);
            drCrDate = RawData.ReadU32BE(buf, ref offset);
            drLsMod = RawData.ReadU32BE(buf, ref offset);
            drAtrb = RawData.ReadU16BE(buf, ref offset);
            drNmFls = RawData.ReadU16BE(buf, ref offset);
            drVBMSt = RawData.ReadU16BE(buf, ref offset);
            drAllocPtr = RawData.ReadU16BE(buf, ref offset);
            drNmAlBlks = RawData.ReadU16BE(buf, ref offset);
            drAlBlkSiz = RawData.ReadU32BE(buf, ref offset);
            drClpSiz = RawData.ReadU32BE(buf, ref offset);
            drAlBlSt = RawData.ReadU16BE(buf, ref offset);
            drNxtCNID = RawData.ReadU32BE(buf, ref offset);
            drFreeBks = RawData.ReadU16BE(buf, ref offset);
            Array.Copy(buf, offset, drVN, 0, drVN.Length);
            offset += drVN.Length;
            drVolBkUp = RawData.ReadU32BE(buf, ref offset);
            drVSeqNum = RawData.ReadU16BE(buf, ref offset);
            drWrCnt = RawData.ReadU32BE(buf, ref offset);
            drXTClpSiz = RawData.ReadU32BE(buf, ref offset);
            drCTClpSiz = RawData.ReadU32BE(buf, ref offset);
            drNmRtDirs = RawData.ReadU16BE(buf, ref offset);
            drFilCnt = RawData.ReadU32BE(buf, ref offset);
            drDirCnt = RawData.ReadU32BE(buf, ref offset);
            for (int i = 0; i < drFndrInfo.Length; i++) {
                drFndrInfo[i] = RawData.ReadU32BE(buf, ref offset);
            }
            drVCSize = RawData.ReadU16BE(buf, ref offset);
            drVBMCSize = RawData.ReadU16BE(buf, ref offset);
            drCtlCSize = RawData.ReadU16BE(buf, ref offset);
            drXTFlSize = RawData.ReadU32BE(buf, ref offset);
            drXTExtRec.Load(buf, ref offset);
            drCTFlSize = RawData.ReadU32BE(buf, ref offset);
            drCTExtRec.Load(buf, ref offset);
            Debug.Assert(offset == startOffset + LENGTH);

            // This will be the empty string if the volume name is invalid.
            mVolumeName = HFS_FileEntry.GenerateCookedName(drVN, true);
        }

        private void Store(byte[] buf, int offset) {
            // Generate raw filename; assume it's valid.
            drVN[0] = (byte)VolumeName.Length;
            MacChar.UnicodeToMac(VolumeName, drVN, 1, MacChar.Encoding.RomanShowCtrl);

            int startOffset = offset;
            RawData.WriteU16BE(buf, ref offset, drSigWord);
            RawData.WriteU32BE(buf, ref offset, drCrDate);
            RawData.WriteU32BE(buf, ref offset, drLsMod);
            RawData.WriteU16BE(buf, ref offset, drAtrb);
            RawData.WriteU16BE(buf, ref offset, drNmFls);
            RawData.WriteU16BE(buf, ref offset, drVBMSt);
            RawData.WriteU16BE(buf, ref offset, drAllocPtr);
            RawData.WriteU16BE(buf, ref offset, drNmAlBlks);
            RawData.WriteU32BE(buf, ref offset, drAlBlkSiz);
            RawData.WriteU32BE(buf, ref offset, drClpSiz);
            RawData.WriteU16BE(buf, ref offset, drAlBlSt);
            RawData.WriteU32BE(buf, ref offset, drNxtCNID);
            RawData.WriteU16BE(buf, ref offset, drFreeBks);
            Array.Copy(drVN, 0, buf, offset, drVN.Length);
            offset += drVN.Length;
            RawData.WriteU32BE(buf, ref offset, drVolBkUp);
            RawData.WriteU16BE(buf, ref offset, drVSeqNum);
            RawData.WriteU32BE(buf, ref offset, drWrCnt);
            RawData.WriteU32BE(buf, ref offset, drXTClpSiz);
            RawData.WriteU32BE(buf, ref offset, drCTClpSiz);
            RawData.WriteU16BE(buf, ref offset, drNmRtDirs);
            RawData.WriteU32BE(buf, ref offset, drFilCnt);
            RawData.WriteU32BE(buf, ref offset, drDirCnt);
            for (int i = 0; i < drFndrInfo.Length; i++) {
                RawData.WriteU32BE(buf, ref offset, drFndrInfo[i]);
            }
            RawData.WriteU16BE(buf, ref offset, drVCSize);
            RawData.WriteU16BE(buf, ref offset, drVBMCSize);
            RawData.WriteU16BE(buf, ref offset, drCtlCSize);
            RawData.WriteU32BE(buf, ref offset, drXTFlSize);
            drXTExtRec.Store(buf, ref offset);
            RawData.WriteU32BE(buf, ref offset, drCTFlSize);
            drCTExtRec.Store(buf, ref offset);
            Debug.Assert(offset == startOffset + LENGTH);
        }

        public override string ToString() {
            return "[HFS MDB '" + VolumeName + "' size=" +
                ((long)TotalBlocks * BlockSize / 1024) + "KB]";
        }


        //
        // Additional properties.
        //

        /// <summary>
        /// Number of logical blocks per allocation block.
        /// </summary>
        public uint LogicalPerAllocBlock {
            get { return BlockSize / BLOCK_SIZE; }
        }

        public bool IsDirty { get; private set; }
        public bool IsAltDirty { get; private set; }

        public HFS_BTree? ExtentsFile { get; private set; }
        public HFS_BTree? CatalogFile { get; private set; }

        /// <summary>
        /// Block read from disk, or created internally.
        /// </summary>
        /// <remarks>
        /// We want to retain this in case some data was stored in reserved areas.
        /// </remarks>
        private byte[] mMDBBlock = new byte[BLOCK_SIZE];

        public uint AltMDBBlock {
            get {
                return (uint)((mChunkAccess.FormattedLength / BLOCK_SIZE) - 2);
            }
        }

        private IChunkAccess mChunkAccess;

        /// <summary>
        /// Constructor.
        /// </summary>
        public HFS_MDB(IChunkAccess chunkAccess) {
            mChunkAccess = chunkAccess;
        }

#if DEBUG
        ~HFS_MDB() { Debug.Assert(!IsDirty); }
#endif

        /// <summary>
        /// Reads the MDB from the primary location on the disk image, and populates the
        /// various fields.
        /// </summary>
        /// <exception cref="IOException">Disk access failure.</exception>
        /// <param name="chunkAccess">Chunk access object.</param>
        public void Read() {
            mChunkAccess.ReadBlock(HFS.MDB_BLOCK_NUM, mMDBBlock, 0);
            Load(mMDBBlock, 0);
        }

        /// <summary>
        /// Writes the MDB to the primary or alternate location.
        /// </summary>
        /// <remarks>
        /// This should only be called by external code when formatting a volume.  Prefer
        /// <see cref="Flush"/>.
        /// </remarks>
        /// <param name="chunkAccess">Chunk access object.</param>
        /// <param name="logiBlock">Logical block number to write.</param>
        /// <exception cref="IOException">Disk access failure.</exception>
        public void Write(uint logiBlock) {
            Store(mMDBBlock, 0);
            mChunkAccess.WriteBlock(logiBlock, mMDBBlock, 0);
            IsDirty = false;
        }

        /// <summary>
        /// Flushes any unwritten changes.
        /// </summary>
        public void Flush() {
            if (ExtentsFile != null) {
                ExtentsFile.Flush();
            }
            if (CatalogFile != null) {
                CatalogFile.Flush();
            }
            if (IsDirty) {
                Write(HFS.MDB_BLOCK_NUM);
            }
            if (IsAltDirty) {
                // We need to write the alternate MDB if the extents overflow or catalog tree
                // expands.  If we don't, fsck.hfs gets very confused, because it appears to
                // use that version to determine how big the tree is expected to be, and can
                // fatally corrupt a volume when attempting to fix it.  (Observed when checking
                // the disk image from the test that creates 100 empty files on a blank
                // 800KB image.)
                Write(AltMDBBlock);
                IsAltDirty = false;
            }
        }

        /// <summary>
        /// Prepares the extents and catalog trees for use.
        /// </summary>
        /// <param name="isFormat">True if the trees are being created for the first time as
        ///   part of formatting a disk.</param>
        /// <exception cref="IOException">Disk access failure or damaged tree structure.</exception>
        public void PrepareTrees(HFS fs, bool isFormat) {
            ExtentsFile = new HFS_BTree(fs, HFS.EXTENTS_CNID, ExtentsFileSize, ExtentsFileRec,
                isFormat);
            CatalogFile = new HFS_BTree(fs, HFS.CATALOG_CNID, CatalogFileSize, CatalogFileRec,
                isFormat);

            if (ExtentsFile.Header.MaxKeyLen != HFS.EXT_INDEX_KEY_LEN) {
                throw new IOException("Unexpected extents tree max key len " +
                    ExtentsFile.Header.MaxKeyLen);
            }
            if (CatalogFile.Header.MaxKeyLen != HFS.CAT_INDEX_KEY_LEN) {
                throw new IOException("Unexpected catalog tree max key len " +
                    CatalogFile.Header.MaxKeyLen);
            }
        }

        /// <summary>
        /// Allocates a Catalog Node ID.
        /// </summary>
        /// <exception cref="DiskFullException">Ran out of CNIDs.</exception>
        public uint AllocCNID() {
            // It's unclear whether this field should be interpreted as signed or unsigned.
            // If it's only incremented and used for equality comparisons in keys then
            // rolling over 2^31 doesn't matter.
            if (NextCatalogID == uint.MaxValue) {
                throw new DiskFullException("Ran out of CNIDs");
            }
            return NextCatalogID++;
        }
    }
}
