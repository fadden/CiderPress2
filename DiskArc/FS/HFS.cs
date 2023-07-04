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
using DiskArc.Multi;
using static DiskArc.Defs;
using static DiskArc.FileAnalyzer.DiskLayoutEntry;
using static DiskArc.FS.HFS_Struct;
using static DiskArc.IFileSystem;

namespace DiskArc.FS {
    /// <summary>
    /// Apple's Hierarchical File System.
    /// </summary>
    public class HFS : IFileSystem {
        public const int MAX_VOL_NAME_LEN = 27;         // max length of volume name
        public const int MAX_FILE_NAME_LEN = 31;        // max length of file name
        public const uint MDB_BLOCK_NUM = 2;            // logical block that holds the MDB
        public const ushort MDB_SIGNATURE = 0x4244;     // HFS volume signature
        public const int MAX_VALENCE = 32767;           // max number of files in a directory
        public const int MAX_BTREE_HEIGHT = 8;          // maximum height of a B*-tree
        public const byte EXT_INDEX_KEY_LEN = 0x07;     // length of key in extents tree indices
        public const byte CAT_INDEX_KEY_LEN = 0x25;     // length of key in catalog tree indices

        // Pre-defined catalog node IDs.
        public const uint ROOT_PAR_CNID = 1;
        public const uint ROOT_CNID = 2;
        public const uint EXTENTS_CNID = 3;
        public const uint CATALOG_CNID = 4;
        public const uint BADALLOC_CNID = 5;
        public const uint FIRST_CNID = 16;

        public const long MAX_FILE_LEN = 0x7fffffff;    // int.MaxValue; one byte shy of 2GB
        public const int MAX_ALLOC_BLOCKS = 65535;      // limited by 16-bit fields

        // Max vol size under GS/OS and Mac system 6/7 is 2GB (32,768 bytes per allocation block).
        // System 7.5 increased to 4GB, 7.5.2 increased to 2TB on some computers.  The
        // allocation block size is stored in a 32-bit integer, so the theoretical maximum
        // size (with 4GB allocation blocks) is 281TB.  4GB seems like a nice practical limit.
        public const long MAX_VOL_SIZE = 4L * 1024 * 1024 * 1024;       // 4GB

        // Need 6 system blocks, extents file, catalog file, some data.  Below a certain
        // point the sizing algorithms in the disk formatter have round-off issues, so set
        // an arbitrary floor so we don't have to worry about edge cases.
        internal const long MIN_VOL_SIZE = 256 * BLOCK_SIZE;            // 128KB

        internal const char SEP_CHAR = ':';             // path filename separator

        internal const int MAX_MAP_NODES = 1024;        // arbitrary, but very large

        public static readonly long FILE_MAGIC = 6838401457417744033;

        private const string FILENAME_RULES =
            "1-31 characters.  Must not include ':'.";
        private const string VOLNAME_RULES =
            "1-27 characters.  Must not include ':'.";
        private static FSCharacteristics sCharacteristics = new FSCharacteristics(
            name: "HFS",
            canWrite: true,
            isHierarchical: true,
            dirSep: SEP_CHAR,
            hasResourceForks: true,
            fnSyntax: FILENAME_RULES,
            vnSyntax: VOLNAME_RULES,
            tsStart: TimeStamp.HFS_MIN_TIMESTAMP,
            tsEnd: TimeStamp.HFS_MAX_TIMESTAMP
        );

        //
        // IFileSystem interfaces.
        //

        public FSCharacteristics Characteristics => sCharacteristics;
        public static FSCharacteristics SCharacteristics => sCharacteristics;

        public bool IsReadOnly { get { return ChunkAccess.IsReadOnly || IsDubious; } }

        public bool IsDubious { get; internal set; }

        public Notes Notes { get; } = new Notes();

        public GatedChunkAccess RawAccess { get; private set; }

        public long FreeSpace {
            get {
                if (!IsPreppedForFileAccess) {
                    return -1;
                }
                Debug.Assert(VolMDB != null);
                return VolMDB.FreeBlocks * VolMDB.BlockSize;
            }
        }


        //
        // Implementation-specific.
        //

        /// <summary>
        /// Data source.  Contents may be shared in various ways.
        /// </summary>
        internal IChunkAccess ChunkAccess { get; private set; }

        /// <summary>
        /// Application-specified options and message logging.
        /// </summary>
        internal AppHook AppHook { get; set; }

        /// <summary>
        /// Master directory block.
        /// </summary>
        internal HFS_MDB? VolMDB { get; set; }

        /// <summary>
        /// Volume allocation bitmap.
        /// </summary>
        internal HFS_VolBitmap? VolBitmap { get; private set; }

        /// <summary>
        /// True if we're in file-access mode.
        /// </summary>
        private bool IsPreppedForFileAccess { get { return VolMDB != null; } }

        /// <summary>
        /// File entry for the catalog root.
        /// </summary>
        private IFileEntry mRootDirEntry;


        /// <summary>
        /// Tracks an open file.
        /// </summary>
        private class OpenFileRec {
            public HFS_FileEntry Entry { get; private set; }
            public HFS_FileDesc FileDesc { get; private set; }

            public OpenFileRec(HFS_FileEntry entry, HFS_FileDesc desc) {
                Debug.Assert(desc.FileEntry == entry);  // check consistency and !Invalid
                Entry = entry;
                FileDesc = desc;
            }

            public override string ToString() {
                return "[HFS OpenFile: '" + Entry.FullPathName + "' part=" +
                    FileDesc.Part + " rw=" + FileDesc.CanWrite + "]";
            }
        }

        /// <summary>
        /// List of open files.
        /// </summary>
        private List<OpenFileRec> mOpenFiles = new List<OpenFileRec>();

        // Delegate: test image to see if it's ours.
        public static TestResult TestImage(IChunkAccess chunkSource, AppHook appHook) {
            return TestImage(chunkSource, appHook, out uint unused);
        }
        public static TestResult TestImage(IChunkAccess chunkSource, AppHook appHook,
                out uint totalBlocks) {
            totalBlocks = 0;
            if (!chunkSource.HasBlocks) {
                return TestResult.No;
            }
            if (!IsSizeAllowed(chunkSource.FormattedLength)) {
                return TestResult.No;
            }

            // Read the volume master directory header block.
            byte[] blkBuf = new byte[BLOCK_SIZE];
            try {
                chunkSource.ReadBlock(MDB_BLOCK_NUM, blkBuf, 0);
            } catch (BadBlockException) {
                return TestResult.No;
            }

            HFS_MDB mdb = new HFS_MDB(chunkSource);
            mdb.Read();
            if (mdb.Signature != MDB_SIGNATURE) {
                return TestResult.No;
            }
            if (mdb.BlockSize == 0 || (mdb.BlockSize & 0x1ff) != 0) {
                // Allocation block size must be a nonzero multiple of 512.
                return TestResult.No;
            }
            if (string.IsNullOrEmpty(mdb.VolumeName)) {
                // Bad size.
                return TestResult.No;
            }

            // Count up the number of logical blocks spanned by the declared allocation block
            // count, and see if it fits inside the data source.
            // (I've seen this be wrong on some CD-ROMs produced by Apple.)
            uint minBlocks =
                mdb.TotalBlocks * (mdb.BlockSize / BLOCK_SIZE) + mdb.AllocBlockStart + 2;
            if (minBlocks > chunkSource.FormattedLength / BLOCK_SIZE) {
                Debug.WriteLine("Volume spans " + minBlocks + " blocks, but chunk len is " +
                    (chunkSource.FormattedLength / BLOCK_SIZE) + " blocks");
                //return TestResult.No;
            }

            totalBlocks = minBlocks;
            return TestResult.Yes;
        }

        // Delegate: returns true if the size (in bytes) is valid for this filesystem.
        public static bool IsSizeAllowed(long size) {
            if (size % BLOCK_SIZE != 0) {
                return false;       // must be blocks
            }
            if (size < MIN_VOL_SIZE || size > MAX_VOL_SIZE) {
                return false;
            }
            return true;
        }

        /// <summary>
        /// Constructor.
        /// </summary>
        public HFS(IChunkAccess dataSource, AppHook appHook) {
            Debug.Assert(dataSource.HasBlocks);
            ChunkAccess = dataSource;
            AppHook = appHook;

            RawAccess = new GatedChunkAccess(dataSource);
            mRootDirEntry = IFileEntry.NO_ENTRY;
        }

        public override string ToString() {
            string id = (VolMDB == null) ? "(raw)" : VolMDB.VolumeName;
            return "[HFS vol: " + id + "]";
        }

        // IDisposable generic finalizer.
        ~HFS() {
            Dispose(false);
        }
        // IDisposable generic Dispose() implementation.
        public void Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        private bool mDisposed;
        protected virtual void Dispose(bool disposing) {
            if (mDisposed) {
                AppHook.LogW("Attempting to dispose of HFS object twice");
                return;
            }
            if (!disposing) {
                // This is a GC finalization.  We can't know if the objects we have references
                // to have already been finalized.
                AppHook.LogW("GC disposing of filesystem object " + this);
                if (mOpenFiles.Count != 0) {
                    foreach (OpenFileRec rec in mOpenFiles) {
                        AppHook.LogW("HFS FS finalized while file open: '" +
                            rec.Entry.FullPathName + "'");
                    }
                }
            }

            AppHook.LogD("HFS.Dispose(" + disposing + ")");

            // This can happen easily if we have the filesystem in a "using" block and
            // something throws with a file open.  Post a warning and close all files.
            if (mOpenFiles.Count != 0) {
                AppHook.LogI("HFS FS disposed with " + mOpenFiles.Count + " files open; closing");
                CloseAll();
            }

            VolMDB?.Flush();
            VolBitmap?.Flush();

            if (IsPreppedForFileAccess) {
                // Invalidate all associated file entry objects.
                InvalidateFileEntries(mRootDirEntry);
            }

            RawAccess.AccessLevel = GatedChunkAccess.AccessLvl.Closed;
            mDisposed = true;
        }

        // IFileSystem
        public void Flush() {
            if (mDisposed) {
                throw new IOException("Filesystem disposed, cannot flush");
            }
            foreach (OpenFileRec rec in mOpenFiles) {
                rec.FileDesc.Flush();
                rec.Entry.SaveChanges();
            }
            VolMDB?.Flush();
            VolBitmap?.Flush();

            // TODO: should we do SaveChanges() across all entries?
        }

        // IFileSystem
        public void PrepareFileAccess(bool doScan) {
            if (IsPreppedForFileAccess) {
                Debug.WriteLine("Volume already prepared for file access");
                return;
            }

            try {
                IsDubious = false;
                Notes.Clear();
                ScanVolume(doScan);
                RawAccess.AccessLevel = GatedChunkAccess.AccessLvl.ReadOnly;
            } catch (Exception ex) {
                // Failed; reset for raw.
                AppHook.LogE("Unable to prepare file access: " + ex.Message);
                PrepareRawAccess();
                throw new DAException("Unable to prepare file access", ex);
            }
        }

        // IFileSystem
        public void PrepareRawAccess() {
            if (mOpenFiles.Count != 0) {
                throw new DAException("Cannot switch to raw access mode with files open");
            }

            // This may be called by PrepareFileAccess() when something fails part-way through,
            // so expect that things may be partially initialized.

            if (VolMDB != null && VolMDB.IsDirty) {
                AppHook.LogI("PrepareRawAccess flushing MDB");
                VolMDB.Flush();
            }
            if (VolBitmap != null && VolBitmap.HasUnflushedChanges) {
                AppHook.LogI("PrepareRawAccess flushing volume bitmap");
                VolBitmap.Flush();
            }
            if (mRootDirEntry != IFileEntry.NO_ENTRY) {
                // Invalidate the FileEntry tree.  If we don't do this the application could
                // try to use a retained object after it was switched back to file access.
                InvalidateFileEntries(mRootDirEntry);
            }

            VolMDB = null;
            VolBitmap = null;
            mRootDirEntry = IFileEntry.NO_ENTRY;
            IsDubious = false;
            RawAccess.AccessLevel = GatedChunkAccess.AccessLvl.Open;
        }

        /// <summary>
        /// Recursively marks all file entry objects in the tree as invalid.
        /// </summary>
        /// <param name="dirEntry">File entry for directory to process.</param>
        private void InvalidateFileEntries(IFileEntry dirEntry) {
            if (dirEntry == IFileEntry.NO_ENTRY) {
                // This can happen if we failed during formatting.
                return;
            }
            Debug.Assert(dirEntry.IsDirectory);
            if (!((HFS_FileEntry)dirEntry).IsScanned) {
                // Don't force a scan during invalidation.
                return;
            }
            foreach (IFileEntry child in dirEntry) {
                if (child.IsDirectory) {
                    InvalidateFileEntries(child);
                } else {
                    HFS_FileEntry entry = (HFS_FileEntry)child;
                    if (entry.IsDirty) {
                        AppHook.LogW("Found un-flushed changes in file entry " + entry);
                        Debug.Assert(false);    // TODO: remove
                        entry.SaveChanges();
                    }
                    entry.Invalidate();
                }
            }
            ((HFS_FileEntry)dirEntry).Invalidate();
        }

        /// <summary>
        /// Creates various data structures for a disk volume, optionally performing a deep scan.
        /// </summary>
        /// <param name="doScan">If true, always do a full scan.</param>
        /// <exception cref="IOException">Disk access error.</exception>
        private void ScanVolume(bool doScan) {
            VolMDB = new HFS_MDB(ChunkAccess);
            VolMDB.Read();

            // We allowed this in TestImage, check it again here and set the "dubious" flag.
            uint minBlocks =
                VolMDB.TotalBlocks * (VolMDB.BlockSize / BLOCK_SIZE) + VolMDB.AllocBlockStart + 2;
            if (minBlocks > ChunkAccess.FormattedLength / BLOCK_SIZE) {
                Notes.AddE("HFS volume spans " + minBlocks.ToString("N0") +
                    " blocks, but disk/partition length is only " +
                    (ChunkAccess.FormattedLength / BLOCK_SIZE).ToString("N0") + " blocks");
                IsDubious = true;
            }

            if ((VolMDB.Attributes & HFS_MDB.ATTR_UNMOUNTED) == 0) {
                Notes.AddI("Volume was not unmounted cleanly after last use");
            }
            VolMDB.PrepareTrees(this, false);
            Debug.Assert(VolMDB.ExtentsFile != null && VolMDB.CatalogFile != null);
            if (doScan) {
                VolMDB.ExtentsFile.CheckTree();
                VolMDB.CatalogFile.CheckTree();
            }
            VolBitmap = new HFS_VolBitmap(VolMDB, ChunkAccess, doScan);
            VolBitmap.LoadBitmap(Notes);
            if (VolBitmap.CalcFreeBlocks() != VolMDB.FreeBlocks) {
                Notes.AddW("MDB free blocks (" + VolMDB.FreeBlocks + ") differs from bitmap (" +
                    VolBitmap.CalcFreeBlocks() + ")");
                // We could mark it as dubious, but it's better to let the full scan do that
                // if the problem is actually dangerous.
                doScan = true;
            }

            //
            // Find the root directory.
            //

            // Find a directory thread with CNID=2 in the key.
            HFS_Record.CatKey threadKey = new HFS_Record.CatKey(ROOT_CNID);
            bool found = VolMDB.CatalogFile.FindRecord(threadKey, out HFS_Node? node,
                out int recordIndex);
            if (!found) {
                throw new IOException("Unable to find root directory thread");
            }
            Debug.Assert(node != null);
            CatDataThreadRec thdRec = node.GetRecord(recordIndex).ParseCatDataThreadRec();

            // Find the directory record from the thread.
            HFS_Record.CatKey dirKey = new HFS_Record.CatKey(thdRec.thdParID, thdRec.thdCName, 0);
            found = VolMDB.CatalogFile.FindRecord(dirKey, out node, out recordIndex);
            if (!found) {
                throw new IOException("Unable to find root directory");
            }
            Debug.Assert(node != null);
            CatDataDirRec dirRec = node.GetRecord(recordIndex).ParseCatDataDirRec();

            // Create a file entry for the directory.
            mRootDirEntry = HFS_FileEntry.CreateDirEntry(this, dirKey, dirRec);

            if (mRootDirEntry.FileName != VolMDB.VolumeName) {
                Notes.AddW("Root dir name '" + mRootDirEntry.FileName +
                    "' does not match volume name '" + VolMDB.VolumeName + "'");
            }

            if (!doScan) {
                return;
            }

            FullScan(node);
        }

        private void FullScan(HFS_Node rootDirNode) {
            Debug.Assert(VolMDB != null);
            Debug.Assert(VolMDB.CatalogFile != null);

            // Create map of CNIDs to file entries.
            Dictionary<uint, HFS_FileEntry> dirCnidMap = new Dictionary<uint, HFS_FileEntry>();
            // Create a list of directories and files.
            List<HFS_FileEntry> entries = new List<HFS_FileEntry>();

            // Add the root dir to the directory CNID map.
            HFS_FileEntry hfsRoot = (HFS_FileEntry)mRootDirEntry;
            dirCnidMap.Add(hfsRoot.EntryCNID, hfsRoot);
            hfsRoot.IsScanned = true;

            uint biggestCNID = 0;

            // Walk through the catalog tree nodes, extracting directory and file records.
            HFS_Node node = rootDirNode;
            while (true) {
                foreach (HFS_Record rec in node) {
                    HFS_Record.CatDataType recType = rec.GetCatDataType();
                    HFS_FileEntry newEntry;
                    if (recType == HFS_Record.CatDataType.Directory) {
                        HFS_Record.CatKey catKey = rec.UnpackCatKey();
                        CatDataDirRec dirRec = rec.ParseCatDataDirRec();
                        if (dirRec.dirDirID == hfsRoot.EntryCNID) {
                            // Don't create root dir a second time.
                            continue;
                        }
                        newEntry = HFS_FileEntry.CreateDirEntry(this, catKey, dirRec);
                        dirCnidMap.Add(newEntry.EntryCNID, newEntry);
                        newEntry.IsScanned = true;
                    } else if (recType == HFS_Record.CatDataType.File) {
                        HFS_Record.CatKey catKey = rec.UnpackCatKey();
                        CatDataFilRec fileRec = rec.ParseCatDataFilRec();
                        newEntry = HFS_FileEntry.CreateFileEntry(this, catKey, fileRec);
                    } else {
                        // Ignore threads.
                        continue;
                    }
                    entries.Add(newEntry);
                    if (newEntry.EntryCNID > biggestCNID) {
                        biggestCNID = newEntry.EntryCNID;
                    }
                }

                // Follow the forward link.
                uint nextNode = node.FLink;
                if (nextNode == 0) {
                    break;
                }
                node = VolMDB.CatalogFile.GetNode(nextNode);
            }

            if (VolMDB.NextCatalogID <= biggestCNID) {
                Notes.AddE("Biggest CNID (" + biggestCNID + ") is larger than MDB 'next' value (" +
                    VolMDB.NextCatalogID + ")");
                IsDubious = true;
            }

            // Create the directory hierarchy.
            foreach (HFS_FileEntry ent in entries) {
                if (!dirCnidMap.TryGetValue(ent.ParentCNID, out HFS_FileEntry? parent)) {
                    // Not fatal, but stuff will be missing.
                    Notes.AddE("Unable to find parent for " + ent);
                    IsDubious = true;
                    continue;
                }
                parent.ChildList.Add(ent.FileName, ent);
                ent.ContainingDir = parent;
            }

            //
            // Generate volume usage data.
            //

            // Scan extents for extents overflow and catalog trees.
            //
            // We currently mark the trees as "system" usage.  It would be more informative to
            // create fake file entries for extents/catalog and use those instead, but if the
            // trees grew we'd need to keep the fake files up to date.  (Might work without
            // additional effort if the fake entry pulled the data out of the MDB when queried.)
            HFS_FileEntry.SetForkVolumeUsage(this, IFileEntry.NO_ENTRY, EXTENTS_CNID,
                    HFS_Record.ForkType.Data, VolMDB.ExtentsFileRec, VolMDB.ExtentsFileSize);
            HFS_FileEntry.SetForkVolumeUsage(this, IFileEntry.NO_ENTRY, CATALOG_CNID,
                    HFS_Record.ForkType.Data, VolMDB.CatalogFileRec, VolMDB.CatalogFileSize);

            // Scan extents for all files.
            //
            // It would be more efficient to do the first extent data record from each catalog
            // entry, and then just run through the extents file, finding the file entry by
            // CNID from the keys.  This is much easier though.
            foreach (HFS_FileEntry ent in entries) {
                if (ent.IsDirectory) {
                    continue;
                }
                ent.SetVolumeUsage();
            }

            //
            // Check the results of the volume usage scan for problems.
            //

            Debug.Assert(VolBitmap != null);
            VolBitmap.VolUsage.Analyze(out int markedUsed, out int unusedMarked,
                    out int notMarkedUsed, out int conflicts);

            AppHook.LogI("Usage counts: " + markedUsed + " in use, " +
                unusedMarked + " unused but marked, " +
                notMarkedUsed + " used but not marked, " +
                conflicts + " conflicts");

            if (notMarkedUsed != 0) {
                // Danger!
                Notes.AddE("Found " + notMarkedUsed + " used blocks that are marked free");
                IsDubious = true;
            }
            if (conflicts != 0) {
                // Individual files are marked "dubious", and can't be modified.
                Notes.AddW("Found " + conflicts + " blocks in use by more than one file");
                //IsDamaged = true;
            }

            bool doWarnUnused = AppHook.GetOptionBool(DAAppHook.WARN_MARKED_BUT_UNUSED, false);
            if (doWarnUnused && unusedMarked != 0) {
                Notes.AddW("Found " + unusedMarked + " unused blocks that are marked used");
            }
        }

        // IFileSystem
        public IMultiPart? FindEmbeddedVolumes() {
            return null;
        }

        // IFileSystem
        public void Format(string volumeName, int volumeNum, bool makeBootable) {
            // We only reject the call if the underlying storage is read-only.  If the filesystem
            // is read-only because of file damage, reformatting it is fine.
            if (ChunkAccess.IsReadOnly) {
                throw new IOException("Can't format read-only data");
            }
            if (IsPreppedForFileAccess) {
                throw new IOException("Must be in raw access mode");
            }
            if (ChunkAccess.FormattedLength < MIN_VOL_SIZE ||
                    ChunkAccess.FormattedLength > MAX_VOL_SIZE) {
                throw new ArgumentOutOfRangeException("Invalid length for HFS volume");
            }

            if (!HFS_FileEntry.IsFileNameValid(volumeName, true)) {
                throw new ArgumentException("Invalid volume name");
            }

            // Zero out the boot blocks.
            ChunkAccess.WriteBlock(0, HFS_FileDesc.sZeroBlock, 0);
            ChunkAccess.WriteBlock(1, HFS_FileDesc.sZeroBlock, 0);

            // Compute geometry.
            //
            // The size of an allocation block increases by one for every 65536 logical blocks
            // in the volume.  We know we can ignore 6 logical blocks: 2 boot, 1 mdb, 1 alt mdb,
            // 1 at the very end, and at least one for the volume bitmap.  (We can't ignore them
            // though; see code below.)
            //const int RESERVED = 6;
            const int RESERVED = 0;
            int numLogicalBlocks = (int)(ChunkAccess.FormattedLength / BLOCK_SIZE);
            int logicalPerAlloc = 1 + (numLogicalBlocks - RESERVED) / 65536;
            int allocBlockSize = logicalPerAlloc * BLOCK_SIZE;

            // This test appears in SVerify1.c in fsck.hfs.  If numLogicalBlocks is 524288
            // (exactly 256MB), the "minimum size" for an allocation block will be computed
            // to be 9 blocks (4608 bytes).  In practice this constraint is too tight because
            // it ignores the fact that at least 6 logical blocks can't be used for allocations.
            // If we want to create images that check cleanly, however, we need to follow
            // their logic.
            if (true) {
                int testNum = numLogicalBlocks;
                int testMin = BLOCK_SIZE;
                for (int i = 2; testNum > MAX_ALLOC_BLOCKS; i++) {
                    testMin = i * BLOCK_SIZE;
                    testNum = numLogicalBlocks / i;
                }
                Debug.Assert(allocBlockSize == testMin,
                    "fsck min for " + numLogicalBlocks + " is " + testMin);
            }

            // We can't know precisely how many allocation blocks there will be without knowing
            // how many logical blocks are used by the volume bitmap, and we can't compute the
            // size of the volume bitmap without knowing how many allocation blocks there are.
            // We resolve this by basing the size of the volume bitmap on the "worst case"
            // allocation block count.
            const int BITS_PER_BLOCK = BLOCK_SIZE * 8;
            int maxAllocBlocks =
                ((numLogicalBlocks - RESERVED) + logicalPerAlloc - 1) / logicalPerAlloc;
            int numBitmapBlocks = (maxAllocBlocks + BITS_PER_BLOCK - 1) / BITS_PER_BLOCK;

            // The first allocation block starts right after the bitmap.
            const int BITMAP_START_BLOCK = 3;
            int firstAllocBlock = BITMAP_START_BLOCK + numBitmapBlocks;

            // The number of logical blocks available for allocations is equal to the total
            // available, minus the stuff at the start and the two at the end.
            int availForAlloc = numLogicalBlocks - firstAllocBlock - 2;
            int numAllocBlocks = availForAlloc / logicalPerAlloc;

            // The file clump size is 4 allocation blocks.
            int clumpSize = 4 * allocBlockSize;

            // The tree clump size is 1/128th of the volume.  (This is the value libhfs uses;
            // a 40MB Mac-formatted volume was one block larger.  Close enough.)
            int treeAllocBlocks = numAllocBlocks / 128;
            if (treeAllocBlocks == 0) {
                Debug.Assert(false);        // we don't currently support volumes this small
                treeAllocBlocks = 1;
            }

            // Fill out the MDB.  All fields are initialized to zero by default.
            HFS_MDB mdb = new HFS_MDB(ChunkAccess);
            mdb.Signature = MDB_SIGNATURE;
            mdb.CreateDate = mdb.ModifyDate = DateTime.Now;
            mdb.Attributes = (ushort)HFS_MDB.AttribFlags.WasUnmounted;
            mdb.VolBitmapStart = BITMAP_START_BLOCK;
            mdb.TotalBlocks = (ushort)numAllocBlocks;
            mdb.BlockSize = (uint)allocBlockSize;
            mdb.ClumpSize = (uint)clumpSize;
            mdb.AllocBlockStart = (ushort)firstAllocBlock;
            mdb.NextCatalogID = FIRST_CNID;
            mdb.FreeBlocks = (ushort)numAllocBlocks;
            mdb.VolumeName = volumeName;
            mdb.ExtentsClumpSize = mdb.ExtentsFileSize = (uint)(treeAllocBlocks * allocBlockSize);
            mdb.CatalogClumpSize = mdb.CatalogFileSize = (uint)(treeAllocBlocks * allocBlockSize);

            // Create a new volume bitmap.
            VolBitmap = new HFS_VolBitmap(mdb, ChunkAccess, false);
            VolBitmap.InitNewBitmap();

            // We could do bad block sparing here, but doing it correctly requires understanding
            // the geometry of the drive (e.g. you want to exclude the region near the failure,
            // because adjacent blocks will likely start to fail).  We don't really expect to be
            // formatting a lot of hard drives, and floppies with bad areas should be discarded,
            // so there's little value in doing this at all.

            // Create extents for the tree files, and mark the storage as in-use in the volume
            // bitmap.
            ExtDataRec extRec = new ExtDataRec();
            extRec.Descs[0].xdrStABN = 0;
            extRec.Descs[0].xdrNumAblks = (ushort)treeAllocBlocks;
            mdb.ExtentsFileRec = extRec;
            VolBitmap.MarkExtentUsed(extRec.Descs[0], IFileEntry.NO_ENTRY);
            extRec.Descs[0].xdrStABN = (ushort)treeAllocBlocks;
            extRec.Descs[0].xdrNumAblks = (ushort)treeAllocBlocks;
            mdb.CatalogFileRec = extRec;
            VolBitmap.MarkExtentUsed(extRec.Descs[0], IFileEntry.NO_ENTRY);
            // Adjust the free block count.
            mdb.FreeBlocks = (ushort)(mdb.FreeBlocks - treeAllocBlocks * 2);

            // Create an empty B*-tree header node for each tree, and write them to disk.
            uint logiBlock = (uint)(firstAllocBlock + 0 * logicalPerAlloc);
            HFS_BTree.HeaderNode.CreateHeaderNode(ChunkAccess, logiBlock, EXT_INDEX_KEY_LEN,
                (uint)(treeAllocBlocks * logicalPerAlloc));

            logiBlock = (uint)(firstAllocBlock + treeAllocBlocks * logicalPerAlloc);
            HFS_BTree.HeaderNode.CreateHeaderNode(ChunkAccess, logiBlock, CAT_INDEX_KEY_LEN,
                (uint)(treeAllocBlocks * logicalPerAlloc));

            // Write the MDB to disk, in the primary and alternate locations.
            mdb.Write(MDB_BLOCK_NUM);
            mdb.Write((uint)numLogicalBlocks - 2);
            VolMDB = mdb;

            // Prepare trees for use.
            VolMDB.PrepareTrees(this, true);

            // Create root directory.  This is not included in the directory counts in the MDB.
            CreateDirectory(IFileEntry.NO_ENTRY, ROOT_PAR_CNID, ROOT_CNID, volumeName);

            // Write everything to disk and reset state.
            VolMDB.Flush();
            VolBitmap.Flush();
            PrepareRawAccess();
        }

        #region File Access

        /// <summary>
        /// Performs general checks on file-access calls, throwing exceptions when something
        /// is amiss.  An exception here generally indicates an error in the program calling
        /// into the library.
        /// </summary>
        /// <param name="op">Short string describing the operation.</param>
        /// <param name="ientry">File being accessed.</param>
        /// <param name="wantWrite">True if this operation might modify the file.</param>
        /// <param name="part">Which part of the file we want access to.  Pass "Unknown" to
        ///   match on any part.</param>
        /// <exception cref="IOException">Various.</exception>
        /// <exception cref="ArgumentException">Various.</exception>
        private void CheckFileAccess(string op, IFileEntry ientry, bool wantWrite, FilePart part) {
            if (mDisposed) {
                throw new ObjectDisposedException("Object was disposed");
            }
            if (!IsPreppedForFileAccess) {
                throw new IOException("Filesystem object not prepared for file access");
            }
            if (wantWrite && IsReadOnly) {
                throw new IOException("Filesystem is read-only");
            }
            if (ientry == IFileEntry.NO_ENTRY) {
                throw new ArgumentException("Cannot operate on NO_ENTRY");
            }
            if (ientry.IsDamaged) {
                throw new IOException("File '" + ientry.FileName +
                    "' is too damaged to access");
            }
            if (ientry.IsDubious && wantWrite) {
                throw new IOException("File '" + ientry.FileName +
                    "' is too damaged to modify");
            }
            HFS_FileEntry? entry = ientry as HFS_FileEntry;
            if (entry == null || entry.FileSystem != this) {
                if (entry != null && entry.FileSystem == null) {
                    // Invalid entry; could be a deleted file, or from before a raw-mode switch.
                    throw new IOException("File entry is invalid");
                } else {
                    throw new FileNotFoundException("File entry is not part of this filesystem");
                }
            }
            if (!CheckOpenConflict(entry, wantWrite, part)) {
                throw new IOException("File is already open; cannot " + op);
            }
        }

        // IFileSystem
        public IFileEntry GetVolDirEntry() {
            return mRootDirEntry;
        }

        // IFileSystem
        public void AddRsrcFork(IFileEntry ientry) {
            CheckFileAccess("extend", ientry, true, FilePart.Unknown);
            if (ientry.IsDirectory) {
                throw new IOException("Directories can not have resource forks");
            }
            // All files have resource forks.  Nothing to do.
        }

        // IFileSystem
        public DiskFileStream OpenFile(IFileEntry ientry, FileAccessMode mode, FilePart part) {
            if (part == FilePart.RawData) {
                part = FilePart.DataFork;   // do this before is-file-open check
            }
            CheckFileAccess("open", ientry, mode != FileAccessMode.ReadOnly, part);
            if (mode != FileAccessMode.ReadOnly && mode != FileAccessMode.ReadWrite) {
                throw new ArgumentException("Unknown file access mode " + mode);
            }
            if (ientry.IsDirectory) {
                throw new IOException("Cannot open directories");   // nothing there to see
            }

            HFS_FileEntry entry = (HFS_FileEntry)ientry;
            switch (part) {
                case FilePart.DataFork:
                case FilePart.RawData:
                case FilePart.RsrcFork:
                    break;
                default:
                    throw new ArgumentException("Unknown file part " + part);
            }

            HFS_FileDesc pfd = HFS_FileDesc.CreateFD(entry, mode, part, false);
            mOpenFiles.Add(new OpenFileRec(entry, pfd));
            return pfd;
        }

        /// <summary>
        /// Determines whether the specified file/part is currently open.
        /// </summary>
        /// <param name="entry">File to check.</param>
        /// <param name="wantWrite">True if we're going to modify the file.</param>
        /// <param name="part">File part.  Pass "Unknown" to match on any part.</param>
        /// <returns>True if the file is open.</returns>
        private bool CheckOpenConflict(HFS_FileEntry entry, bool wantWrite, FilePart part) {
            foreach (OpenFileRec rec in mOpenFiles) {
                if (rec.Entry != entry) {
                    continue;
                }
                if (part != FilePart.Unknown && rec.FileDesc.Part != part) {
                    // This file is open, but we're only interested in a specific part, and
                    // the part that's open isn't this one.
                    continue;
                }
                if (wantWrite) {
                    // We need exclusive access to this part.
                    return false;
                } else {
                    // We're okay if the existing open is read-only.
                    if (rec.FileDesc.CanWrite) {
                        return false;
                    }
                    // There may be additional open instances, but they must be read-only.
                    return true;
                }
            }
            return true;        // file is not open at all
        }

        /// <summary>
        /// Closes a file, removing it from our list.  Do not call this directly -- this is
        /// called from the HFS_FileDesc Dispose() call.
        /// </summary>
        /// <param name="ifd">Descriptor to close.</param>
        /// <exception cref="IOException">File descriptor was already closed, or was opened
        ///   by a different filesystem.</exception>
        internal void CloseFile(DiskFileStream ifd) {
            HFS_FileDesc fd = (HFS_FileDesc)ifd;
            if (fd.FileSystem != this) {
                // Should be impossible, though it could be null if previous close invalidated it.
                if (fd.FileSystem == null) {
                    throw new IOException("Invalid file descriptor");
                } else {
                    throw new IOException("File descriptor was opened by a different filesystem");
                }
            }
            Debug.Assert(!mDisposed, "closing file in filesystem after dispose");

            // Find the file record, searching by descriptor.
            bool found = false;
            foreach (OpenFileRec rec in mOpenFiles) {
                if (rec.FileDesc == fd) {
                    mOpenFiles.Remove(rec);
                    found = true;
                    break;
                }
            }
            if (!found) {
                throw new IOException("Open file record not found: " + fd);
            }

            // Flush any pending changes.  We do this here, rather than after each Write(),
            // for efficiency.  The VolBitmap or MDB may be null if the descriptor close is
            // happening as part of finalization.
            VolBitmap?.Flush();
            VolMDB?.Flush();
        }

        // IFileSystem
        public void CloseAll() {
            // Walk through from end to start so we don't trip when entries are removed.
            for (int i = mOpenFiles.Count - 1; i >= 0; --i) {
                try {
                    mOpenFiles[i].FileDesc.Close();
                } catch (IOException ex) {
                    // This could happen if Close flushed a change that wrote to a bad block,
                    // e.g. a new index block in a tree file.
                    Debug.WriteLine("Caught IOException during CloseAll: " + ex.Message);
                } catch (Exception ex) {
                    // Unexpected.  Discard it so cleanup can continue.
                    Debug.WriteLine("Unexpected exception in CloseAll: " + ex.Message);
                    Debug.Assert(false);
                }
            }
            Debug.Assert(mOpenFiles.Count == 0);

            // This is probably being called as part of closing the filesystem, so make sure
            // pending changes have been flushed.  In normal operation this shouldn't be needed,
            // because changes are flushed when files are closed.
            if (VolMDB != null && VolMDB.IsDirty) {
                AppHook.LogI("CloseAll flushing MDB");
                VolMDB.Flush();
            }
            if (VolBitmap != null && VolBitmap.HasUnflushedChanges) {
                AppHook.LogI("CloseAll flushing volume bitmap");
                VolBitmap.Flush();
            }

        }

        // IFileSystem
        public IFileEntry CreateFile(IFileEntry idirEntry, string fileName, CreateMode mode) {
            CheckFileAccess("create", idirEntry, true, FilePart.Unknown);
            HFS_FileEntry dirEntry = (HFS_FileEntry)idirEntry;
            if (!dirEntry.IsDirectory) {
                throw new ArgumentException("Directory entry argument must be a directory");
            }
            if (fileName == null || !HFS_FileEntry.IsFileNameValid(fileName, false)) {
                throw new ArgumentException("Invalid filename '" + fileName + "'");
            }
            if (mode != CreateMode.File &&
                    mode != CreateMode.Directory &
                    mode != CreateMode.Extended) {
                throw new ArgumentException("Invalid CreateMode " + mode);
            }

            Debug.Assert(VolMDB != null);
            HFS_BTree catTree = VolMDB.CatalogFile!;
            //HFS_Record.CatKey fileKey = new HFS_Record.CatKey(dirEntry.EntryCNID, fileName);
            //if (catTree.FindRecord(fileKey, out HFS_Node? unused1, out int unused2)) {
            //    throw new IOException("File '" + fileName + "' already exists");
            //}
            if (dirEntry.ChildList.ContainsKey(fileName)) {
                throw new IOException("File '" + fileName + "' already exists");
            }

            IFileEntry newEntry;
            if (mode == CreateMode.Directory) {
                newEntry = CreateDirectory(dirEntry, dirEntry.EntryCNID, 0, fileName);
            } else {
                newEntry = CreateFile(dirEntry, dirEntry.EntryCNID, 0, fileName);
            }

            // Add to list of children.
            dirEntry.ChildList.Add(newEntry.FileName, newEntry);
            return newEntry;
        }

        /// <summary>
        /// Creates a directory "file".
        /// </summary>
        private HFS_FileEntry CreateDirectory(IFileEntry parentDir, uint parentCNID,
                uint entryCNID, string fileName) {
            Debug.Assert(VolMDB != null);
            HFS_BTree catTree = VolMDB.CatalogFile!;

            // Expand tree's node pool if necessary.
            catTree.EnsureSpace(2);

            if (entryCNID == 0) {
                entryCNID = VolMDB.AllocCNID();
            }

            // Create directory thread record and key.
            HFS_Record.CatKey threadKey = new HFS_Record.CatKey(entryCNID);
            CatDataThreadRec threadData = new CatDataThreadRec();
            threadData.cdrType = (byte)HFS_Record.CatDataType.DirectoryThread;
            threadData.cdrResrv2 = 0;
            threadData.thdResrv[0] = threadData.thdResrv[1] = 0;
            threadData.thdParID = parentCNID;
            threadData.thdCName[0] = (byte)fileName.Length;
            MacChar.UnicodeToMac(fileName, threadData.thdCName, 1, MacChar.Encoding.RomanShowCtrl);

            HFS_Record threadRec = HFS_Record.GenerateCatDataThreadRec(threadKey, threadData);

            // Create directory record and key.
            HFS_Record.CatKey dirKey = new HFS_Record.CatKey(parentCNID, fileName);
            CatDataDirRec dirData = new CatDataDirRec();
            dirData.cdrType = (byte)HFS_Record.CatDataType.Directory;
            dirData.dirDirID = entryCNID;
            dirData.dirCrDat = dirData.dirMdDat = TimeStamp.ConvertDateTime_HFS(DateTime.Now);
            dirData.dirUsrInfo = DInfo.ZERO;
            dirData.dirFndrInfo = DXInfo.ZERO;
            for (int i = 0; i < 4; i++) {
                dirData.dirResrv[i] = 0;
            }

            HFS_Record dirRec = HFS_Record.GenerateCatDataDirRec(dirKey, dirData);
            catTree.Insert(threadKey, threadRec);
            catTree.Insert(dirKey, dirRec);

            UpdateValence(parentCNID, true, 1);

            HFS_FileEntry newEntry = HFS_FileEntry.CreateDirEntry(this, dirKey, dirData);
            newEntry.ContainingDir = parentDir;
            newEntry.IsScanned = true;
            return newEntry;
        }

        /// <summary>
        /// Creates a plain file.
        /// </summary>
        private HFS_FileEntry CreateFile(IFileEntry parentDir, uint parentCNID,
                uint entryCNID, string fileName) {
            Debug.Assert(VolMDB != null);
            HFS_BTree catTree = VolMDB.CatalogFile!;

            // Expand tree's node pool if necessary.
            catTree.EnsureSpace(1);

            if (entryCNID == 0) {
                entryCNID = VolMDB.AllocCNID();
            }

            // Create file record and key.
            HFS_Record.CatKey filKey = new HFS_Record.CatKey(parentCNID, fileName);
            CatDataFilRec filData = new CatDataFilRec();
            filData.cdrType = (byte)HFS_Record.CatDataType.File;
            filData.filUsrWds = FInfo.ZERO;
            filData.filFlNum = entryCNID;
            filData.filCrDat = filData.filMdDat = TimeStamp.ConvertDateTime_HFS(DateTime.Now);
            filData.filFndrInfo = FXInfo.ZERO;

            HFS_Record filRec = HFS_Record.GenerateCatDataFilRec(filKey, filData);
            catTree.Insert(filKey, filRec);

            UpdateValence(parentCNID, false, 1);

            Debug.Assert(parentDir != IFileEntry.NO_ENTRY);
            HFS_FileEntry newEntry = HFS_FileEntry.CreateFileEntry(this, filKey, filData);
            newEntry.ContainingDir = parentDir;
            return newEntry;
        }

        /// <summary>
        /// Updates the various file counts in the MDB and parent directory.
        /// </summary>
        /// <param name="parentCNID">CNID of directory where directory or file was created
        ///   or removed.</param>
        /// <param name="isDirectory">True if this is a directory.</param>
        /// <param name="adj">+1 for creation, -1 for deletion.</param>
        internal void UpdateValence(uint parentCNID, bool isDirectory, int adj) {
            Debug.Assert(adj == -1 || adj == 1);
            Debug.Assert(VolMDB != null);

            // Update file counts.
            if (parentCNID == ROOT_PAR_CNID) {
                // We just created the root directory.  This isn't included in the MDB's count,
                // so there's nothing to update.
                Debug.Assert(isDirectory && VolMDB.RootDirCount == 0);
                return;
            }

            if (isDirectory) {
                VolMDB.DirCount = (uint)(VolMDB.DirCount + adj);
            } else {
                VolMDB.FileCount = (uint)(VolMDB.FileCount + adj);
            }

            if (parentCNID == ROOT_CNID) {
                // Created in root dir, so there's an extra set of valence numbers.
                if (isDirectory) {
                    VolMDB.RootDirCount = (ushort)(VolMDB.RootDirCount + adj);
                } else {
                    VolMDB.RootFileCount = (ushort)(VolMDB.RootFileCount + adj);
                }
            }

            // Find directory record for the parent.
            GetDirectoryRecord(parentCNID, out HFS_Node dirNode, out int recordIndex);
            HFS_Record rec = dirNode.GetRecord(recordIndex);

            // Update directory valence field in the record.
            CatDataDirRec catData = rec.ParseCatDataDirRec();
            catData.dirVal = (ushort)(catData.dirVal + adj);
            catData.Store(rec.DataBuf, rec.DataOffset);

            // Save the updated node.
            dirNode.Write();
        }

        private void GetDirectoryRecord(uint dirCNID, out HFS_Node dirNode, out int recordIndex) {
            Debug.Assert(VolMDB != null);

            HFS_Record.CatKey threadKey = new HFS_Record.CatKey(dirCNID);
            bool found = VolMDB.CatalogFile!.FindRecord(threadKey, out HFS_Node? node,
                out int trecordIndex);
            if (!found) {
                throw new IOException("Unable to find directory thread for #" +
                    dirCNID.ToString("x4"));
            }
            Debug.Assert(node != null);
            CatDataThreadRec thdRec = node.GetRecord(trecordIndex).ParseCatDataThreadRec();

            // Find the directory record via the thread.
            HFS_Record.CatKey dirKey = new HFS_Record.CatKey(thdRec.thdParID, thdRec.thdCName, 0);
            found = VolMDB.CatalogFile.FindRecord(dirKey, out node, out recordIndex);
            if (!found) {
                throw new IOException("Unable to find directory #" + dirCNID.ToString("x4"));
            }
            Debug.Assert(node != null);
            dirNode = node;
        }

        // IFileSystem
        public void MoveFile(IFileEntry ientry, IFileEntry idestDir, string newFileName) {
            CheckFileAccess("move", ientry, true, FilePart.Unknown);
            if (ientry == mRootDirEntry) {
                throw new ArgumentException("Can't move volume directory");
            }
            if (idestDir == IFileEntry.NO_ENTRY || !((HFS_FileEntry)idestDir).IsValid) {
                throw new ArgumentException("Invalid destination file entry");
            }
            if (!idestDir.IsDirectory) {
                throw new ArgumentException("Destination file entry must be a directory");
            }
            if (newFileName == null || !HFS_FileEntry.IsFileNameValid(newFileName, false)) {
                throw new ArgumentException("Invalid filename '" + newFileName + "'");
            }
            HFS_FileEntry destDir = (HFS_FileEntry)idestDir;
            if (destDir == null || destDir.FileSystem != this) {
                if (destDir != null && destDir.FileSystem == null) {
                    // Invalid entry; could be a deleted file, or from before a raw-mode switch.
                    throw new IOException("Destination directory is invalid");
                } else {
                    throw new FileNotFoundException(
                        "Destination directory is not part of this filesystem");
                }
            }

            ((HFS_FileEntry)ientry).DoMoveFile(destDir, newFileName);
        }

        // IFileSystem
        public void DeleteFile(IFileEntry ientry) {
            CheckFileAccess("delete", ientry, true, FilePart.Unknown);
            if (ientry == mRootDirEntry) {
                throw new IOException("Can't delete volume directory");
            }
            HFS_FileEntry entry = (HFS_FileEntry)ientry;
            if (entry.IsDirectory && entry.ChildList.Count != 0) {
                throw new IOException("Directory is not empty (" +
                    entry.ChildList.Count + " files)");
            }

            Debug.Assert(entry.EntryCNID >= FIRST_CNID);
            HFS_FileEntry parentDir = (HFS_FileEntry)ientry.ContainingDir;

            if (!entry.IsDirectory) {
                // Release storage.
                using (HFS_FileDesc dataFd = HFS_FileDesc.CreateFD(entry, FileAccessMode.ReadWrite,
                        FilePart.DataFork, true)) {
                    dataFd.SetLength(0);
                }
                using (HFS_FileDesc rsrcFd = HFS_FileDesc.CreateFD(entry, FileAccessMode.ReadWrite,
                        FilePart.RsrcFork, true)) {
                    rsrcFd.SetLength(0);
                }
            }

            // Remove the catalog entry for the directory or file, and for the directory or
            // file thread.  File threads are optional.
            HFS_BTree catTree = VolMDB!.CatalogFile!;
            HFS_Record.CatKey catKey =
                new HFS_Record.CatKey(entry.ParentCNID, entry.RawFileNameStr, 0);
            catTree.Delete(catKey, false);
            HFS_Record.CatKey threadKey = new HFS_Record.CatKey(entry.EntryCNID);
            catTree.Delete(threadKey, !entry.IsDirectory);

            // Update counts in MDB and parent directory record.
            UpdateValence(parentDir.EntryCNID, entry.IsDirectory, -1);

            // Remove the entry from the parent's directory list.
            parentDir.ChildList.Remove(entry.FileName);

            // This entry may no longer be used.
            entry.Invalidate();
        }

        #endregion File Access

        #region Miscellaneous

        internal uint ExtentToLogiBlocks(ExtDescriptor desc, out uint numLogiBlocks) {
            Debug.Assert(VolMDB != null);
            uint logiStart = AllocBlockToLogiBlock(desc.xdrStABN);
            numLogiBlocks = desc.xdrNumAblks * VolMDB.BlockSize;
            return logiStart;
        }

        internal uint AllocBlockToLogiBlock(uint allocBlockNum) {
            Debug.Assert(VolMDB != null);
            if (allocBlockNum >= VolMDB.TotalBlocks) {
                throw new IOException("Invalid allocation block " + allocBlockNum);
            }
            return VolMDB.AllocBlockStart + allocBlockNum * VolMDB.LogicalPerAllocBlock;
        }

        #endregion Miscellaneous
    }
}
