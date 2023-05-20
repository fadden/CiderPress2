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
using static DiskArc.IFileSystem;

namespace DiskArc.FS {
    /// <summary>
    /// ProDOS filesystem implementation.
    /// </summary>
    public class ProDOS : IFileSystem {
        //
        // Constants.
        //
        public const int VOL_DIR_START_BLK = 2;                 // volume dir header block
        public const int MAX_FILE_NAME_LEN = 15;                // max length of filename
        public const int MAX_VOL_SIZE = BLOCK_SIZE * 65535;     // one block shy of 32MB
        public const int MAX_FILE_LEN = 256 * 256 * 256 - 1;    // 24-bit EOF, ~16MB

        internal const int MIN_VOL_SIZE = BLOCK_SIZE * 5;       // smallest useful volume
        internal const int MAX_DIR_BLOCKS = 65532;              // max possible dir length
        internal const char SEP_CHAR = '/';                     // for debug pathname formatting

        // I don't know if there's an official limit to directory depth, but we want to
        // pick something reasonable so that we can use recursion without fear.
        internal const int MAX_DIR_DEPTH = 256;

        // The length of a directory entry can be different in every directory.  We want to
        // enforce a minimum to ensure that we have access to our fields, but don't want to
        // use a fixed length when walking the directory.
        //
        // It's unclear whether the directory header is supposed to be padded to match the size
        // of the actual file entries, but it's reasonable to assume that it is.
        internal const int EXPECT_DIR_ENTRY_LENGTH = 0x27;
        internal const int EXPECT_DIR_PER_BLOCK = 0x0d;

        private static FSCharacteristics sCharacteristics = new FSCharacteristics(
            name: "ProDOS/SOS",
            canWrite: true,
            isHierarchical: true,
            dirSep: SEP_CHAR,
            hasResourceForks: true
        );

        //
        // IFileSystem interfaces.
        //

        public FSCharacteristics Characteristics => sCharacteristics;
        public static FSCharacteristics SCharacteristics => sCharacteristics;

        public Notes Notes { get; } = new Notes();

        public bool IsReadOnly { get { return ChunkAccess.IsReadOnly || IsDubious; } }

        public bool IsDubious { get; internal set; }

        public long FreeSpace {
            get {
                if (VolBitmap == null) {
                    return -1;
                }
                return VolBitmap.CalcFreeBlocks() * BLOCK_SIZE;
            }
        }

        public GatedChunkAccess RawAccess { get; private set; }


        //
        // Implementation-specific.
        //

        /// <summary>
        /// ProDOS storage type.
        /// </summary>
        public enum StorageType : byte {
            Deleted = 0x00,
            Seedling = 0x01,
            Sapling = 0x02,
            Tree = 0x03,
            PascalVolume = 0x04,
            Extended = 0x05,
            Directory = 0x0d,
            SubdirHeader = 0x0e,
            VolDirHeader = 0x0f
        }

        /// <summary>
        /// Data source.  Contents may be shared in various ways.
        /// </summary>
        internal IChunkAccess ChunkAccess { get; private set; }

        /// <summary>
        /// List of embedded volumes.
        /// </summary>
        private IMultiPart? mEmbeds;

        /// <summary>
        /// Volume directory header data, as read from disk.
        /// </summary>
        private VolDirHeader mVolDirHeader;

        /// <summary>
        /// "Fake" volume directory entry, used as root node in file hierarchy.
        /// Will be set to NO_ENTRY when the filesystem is in raw-access mode.
        /// </summary>
        private IFileEntry mVolDirEntry;

        /// <summary>
        /// Volume block allocation bitmap.
        /// Will be null when the filesystem is in raw-access mode.
        /// </summary>
        internal ProDOS_VolBitmap? VolBitmap { get; private set; }

        /// <summary>
        /// Record of an open file.
        /// </summary>
        private class OpenFileRec {
            public ProDOS_FileEntry Entry { get; private set; }
            public ProDOS_FileDesc FileDesc { get; private set; }

            public OpenFileRec(ProDOS_FileEntry entry, ProDOS_FileDesc desc) {
                Debug.Assert(desc.FileEntry == entry);  // check consistency and !Invalid
                Entry = entry;
                FileDesc = desc;
            }

            public override string ToString() {
                return "[ProDOS OpenFile: '" + Entry.FullPathName + "' part=" +
                    FileDesc.Part + " rw=" + FileDesc.CanWrite + "]";
            }
        }

        /// <summary>
        /// List of open files.
        /// </summary>
        private List<OpenFileRec> mOpenFiles = new List<OpenFileRec>();

        /// <summary>
        /// Total blocks present in the filesystem, as determined by the value in the volume
        /// header.  This is meaningless in block-edit mode, where the bounds are determined
        /// by the ChunkAccess.
        /// </summary>
        public int TotalBlocks { get { return mVolDirHeader.TotalBlocks; } }

        /// <summary>
        /// True if we're in file-access mode, false if raw-access mode.
        /// </summary>
        private bool IsPreppedForFileAccess { get { return mVolDirEntry != IFileEntry.NO_ENTRY; } }

        /// <summary>
        /// Application-specified options and message logging.
        /// </summary>
        internal AppHook AppHook { get; set; }


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
            // Minimum useful: 2 boot blocks, 1 master dir, 1 volume bitmap, 1 file key block.
            if (!IsSizeAllowed(chunkSource.FormattedLength)) {
                return TestResult.No;
            }

            // Read the volume master directory header block.
            byte[] blkBuf = new byte[BLOCK_SIZE];
            try {
                chunkSource.ReadBlock(VOL_DIR_START_BLK, blkBuf, 0);
            } catch (BadBlockException) {
                return TestResult.No;
            }

            // Test some entries to see if they're consistent with ProDOS.  Ideally this would
            // check values in the other half of the block, for better verification of
            // sector ordering, but there won't be anything there on a new volume, and in
            // practice this works fine because we're looking at block 2.
            byte volDirEntryLength = blkBuf[0x23];
            byte volDirEntriesPerBlock = blkBuf[0x24];

            if (!(blkBuf[0x00] == 0 && blkBuf[0x01] == 0) ||
                !((blkBuf[0x04] & 0xf0) == 0xf0) ||
                !((blkBuf[0x04] & 0x0f) != 0) ||
                !(volDirEntryLength * volDirEntriesPerBlock <= BLOCK_SIZE - 4) ||
                !(blkBuf[0x05] >= 'A' && blkBuf[0x05] <= 'Z'))
            {
                return TestResult.No;
            }

            ushort volBitPtr = RawData.GetU16LE(blkBuf, 4 + 0x23);
            totalBlocks = RawData.GetU16LE(blkBuf, 4 + 0x25);
            if (volBitPtr <= VOL_DIR_START_BLK || volBitPtr >= totalBlocks) {
                // Badly-formed image.  Offer it up as a maybe so we have the opportunity
                // to recover files from it read-only.
                return TestResult.Maybe;
            }

            return TestResult.Yes;
        }

        // Delegate: returns true if the size (in bytes) is valid for this filesystem.
        public static bool IsSizeAllowed(long size) {
            if (size % BLOCK_SIZE != 0) {
                return false;       // must be blocks
            }
            if (size == MAX_VOL_SIZE + BLOCK_SIZE) {
                // ProDOS volumes frequently have 65535 blocks in a 65536-block container.
                // Explicitly allow it.
            } else if (size < MIN_VOL_SIZE || size > MAX_VOL_SIZE) {
                return false;
            }
            return true;
        }

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="source">Data source, which we now own.</param>
        /// <param name="log">Debug message log.</param>
        public ProDOS(IChunkAccess source, AppHook appHook) {
            Debug.Assert(source.HasBlocks);
            ChunkAccess = source;
            AppHook = appHook;

            RawAccess = new GatedChunkAccess(source);

            mVolDirHeader = new VolDirHeader();
            mVolDirEntry = IFileEntry.NO_ENTRY;
            VolBitmap = null;
        }

        public override string ToString() {
            string id = mVolDirEntry == IFileEntry.NO_ENTRY ? "(raw)" : mVolDirHeader.VolumeName;
            return "[ProDOS vol '" + id + "']";
        }

        // IDisposable generic finalizer.
        ~ProDOS() {
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
                AppHook.LogW("Attempting to dispose of ProDOS object twice");
                return;
            }
            if (!disposing) {
                // This is a GC finalization.  We can't know if the objects we have references
                // to have already been finalized, so all we can do is complain.
                AppHook.LogW("GC disposing of filesystem object " + this);
                if (mOpenFiles.Count != 0) {
                    foreach (OpenFileRec rec in mOpenFiles) {
                        AppHook.LogW("ProDOS FS finalized while file open: '" +
                            rec.Entry.FullPathName + "'");
                    }
                }
                return;
            }

            AppHook.LogD("ProDOS.Dispose(" + disposing + "): " + mVolDirHeader.VolumeName);

            // This can happen easily if we have the filesystem in a "using" block and
            // something throws with a file open.  Post a warning and close all files.
            if (mOpenFiles.Count != 0) {
                AppHook.LogI("ProDOS FS disposed with " + mOpenFiles.Count+ " files open; closing");
                CloseAll();
            }

            try {
                VolBitmap?.Flush();
            } catch (IOException) {
                AppHook.LogE("Failed while attempting to flush volume bitmap");
            }

            if (IsPreppedForFileAccess) {
                // Invalidate all associated file entry objects.
                InvalidateFileEntries(mVolDirEntry);
                mEmbeds?.Dispose();
                mEmbeds = null;
            } else {
                Debug.Assert(mEmbeds == null);
            }

            RawAccess.AccessLevel = GatedChunkAccess.AccessLvl.Closed;
            mDisposed = true;
        }

        // IFileSystem
        public void Flush() {
            foreach (OpenFileRec rec in mOpenFiles) {
                rec.FileDesc.Flush();
                rec.Entry.SaveChanges();
            }
            VolBitmap?.Flush();

            if (mEmbeds != null) {
                foreach (Partition part in mEmbeds) {
                    part.FileSystem?.Flush();
                }
            }

            // TODO: should we do SaveChanges() across all entries?
        }

        /// <summary>
        /// Validates the block number.  In addition to capping it to the size of the filesystem,
        /// we disallow blocks 0/1, which should never be referenced by anything.
        /// </summary>
        internal bool IsBlockValid(uint blockNum) {
            return blockNum >= VOL_DIR_START_BLK && blockNum < TotalBlocks;
        }

        // IFileSystem
        public void PrepareFileAccess(bool doScan) {
            if (IsPreppedForFileAccess) {
                Debug.WriteLine("Volume already prepared for file access");
                return;
            }

            try {
                // Scan the volume from scratch.
                IsDubious = false;
                Notes.Clear();
                ScanVolume(doScan);
                RawAccess.AccessLevel = GatedChunkAccess.AccessLvl.ReadOnly;
                // TODO(maybe): do a bad-block scan to identify files with damage and flag
                //   unusable blocks in volume bitmap.  Need a "scan" function in IChunkAccess.
            } catch (Exception ex) {
                // If the volume scan failed, return us to raw mode.  We shouldn't end up
                // here unless something is very wrong, like host system I/O errors; any
                // issues with the filesystem should be handled through the usual damage
                // tracking mechanisms.
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
            if (mEmbeds != null) {
                foreach (Partition part in mEmbeds) {
                    if (part.FileSystem != null) {
                        part.FileSystem.PrepareRawAccess();     // will fail if files are open
                    }
                }
            }

            // This may be called by PrepareFileAccess() when something fails part-way through,
            // so expect that things may be partially initialized.

            if (VolBitmap != null && VolBitmap.HasUnflushedChanges) {
                AppHook.LogI("PrepareRawAccess flushing volume bitmap");
                VolBitmap.Flush();
            }
            if (mVolDirEntry != IFileEntry.NO_ENTRY) {
                // Invalidate the FileEntry tree.  If we don't do this the application could
                // try to use a retained object after it was switched back to file access.
                InvalidateFileEntries(mVolDirEntry);
            }

            mEmbeds?.Dispose();
            mEmbeds = null;

            // Reset some fields that get initialized by the constructor.
            mVolDirHeader = new VolDirHeader();
            mVolDirEntry = IFileEntry.NO_ENTRY;
            VolBitmap = null;
            IsDubious = false;

            RawAccess.AccessLevel = GatedChunkAccess.AccessLvl.Open;
        }

        /// <summary>
        /// Recursively marks all file entry objects in the tree as invalid.
        /// </summary>
        /// <param name="dirEntry">File entry for directory to process.</param>
        private void InvalidateFileEntries(IFileEntry idirEntry) {
            ProDOS_FileEntry dirEntry = (ProDOS_FileEntry)idirEntry;
            Debug.Assert(dirEntry.IsDirectory);
            if (!dirEntry.IsValid) {
                // Whoops, already invalidated.
                return;
            }
            if (!dirEntry.IsScanned) {
                // Don't force a scan during invalidation.
                return;
            }
            foreach (IFileEntry child in dirEntry) {
                if (child.IsDirectory) {
                    InvalidateFileEntries(child);
                } else {
                    ProDOS_FileEntry childEntry = (ProDOS_FileEntry)child;
                    if (childEntry.IsChangePending) {
                        AppHook.LogW("Found un-flushed changes in file entry " + childEntry);
                        Debug.Assert(false);    // TODO: remove
                        childEntry.SaveChanges();
                    }
                    childEntry.Invalidate();
                }
            }
            dirEntry.Invalidate();
        }

        #region Preparation

        /// <summary>
        /// Recursively scans all files on the entire volume, and loads the volume bitmap.
        /// Scans file usage and looks for errors.
        /// </summary>
        /// <remarks>
        /// Walks through every file's index blocks to see which blocks are actually used
        /// by files. This tells us if parts of a file are marked as free in the in-use
        /// bitmap (dangerous), or if blocks are marked in-use that aren't part of a file.  The
        /// latter strategy is used by some programs that embed DOS 3.3 sub-volumes.
        /// </remarks>
        /// <param name="fullScan">If set, do a deep recursive scan.</param>
        /// <exception cref="IOException">Failure to read from storage.</exception>
        private void ScanVolume(bool fullScan) {
            // Read the volume directory header from block 2.  It determines the size of
            // and layout of the volume.
            if (!ExtractVolDirHeader()) {
                // Shouldn't have gotten this far if the volume dir header is fully broken.
                IsDubious = true;
                return;
            }

            // Load volume bitmap into mVolBitmap.  If the load fails, the bitmap will be an
            // array of length zero.
            VolBitmap = new ProDOS_VolBitmap(mVolDirHeader.TotalBlocks,
                    mVolDirHeader.mBitMapPointer, ChunkAccess, fullScan);
            try {
                VolBitmap.LoadBitmap(Notes);
            } catch (IOException) {
                IsDubious = true;
            }

            mVolDirEntry = ProDOS_FileEntry.CreateVolDirEntry(this, mVolDirHeader);

            if (!fullScan) {
                return;
            }

            //
            // Set the "in use" flags in the volume tracker from the volume bitmap,
            // and mark the system blocks.
            //

            // Mark volume bitmap.
            for (int i = 0; i < VolBitmap.NumBitmapBlocks; i++) {
                VolBitmap.VolUsage.SetUsage((uint)(mVolDirHeader.mBitMapPointer + i),
                    IFileEntry.NO_ENTRY);
            }

            // Mark boot blocks.
            VolBitmap.VolUsage.SetUsage(0, IFileEntry.NO_ENTRY);
            VolBitmap.VolUsage.SetUsage(1, IFileEntry.NO_ENTRY);

            //
            // Recursively scan the volume, starting from the volume directory.  Update the
            // volume usage tracker as we go.
            //

            DateTime startWhen = DateTime.Now;
            //AppHook.LogI("Scanning files and directories");
            ScanDirRecursive(mVolDirEntry, 0);

            DateTime endWhen = DateTime.Now;
            //Notes.AddI("Deep scan took " + (endWhen - startWhen).TotalMilliseconds +
            //    " ms for volume with " + TotalBlocks + " blocks");

            // Check the results of the volume usage scan for problems.
            VolBitmap.VolUsage.Analyze(out int markedUsed, out int unusedMarked,
                    out int notMarkedUsed, out int conflicts);

            AppHook.LogI("Usage counts: " + markedUsed + " in use, " +
                unusedMarked + " unused but marked, " +
                notMarkedUsed + " used but not marked, " +
                conflicts + " conflicts");

            if (notMarkedUsed != 0) {
                Notes.AddE("Found " + notMarkedUsed + " used blocks that are marked free");
                IsDubious = true;
            }
            if (conflicts != 0) {
                // If we treat the conflicting files as read-only (to the extent of not
                // allowing them to be deleted), then there's no harm in allowing modifications
                // to the rest of the volume.
                Notes.AddW("Found " + conflicts + " blocks in use by more than one file");
                //IsDamaged = true;
            }

            bool doWarnUnused = AppHook.GetOptionBool(DAAppHook.WARN_MARKED_BUT_UNUSED, false);

            // See if we have a large range of blocks that are marked used but have no usage.
            // If so, we may have an embedded DOS 3.3 volume.
            if (unusedMarked != 0) {
                RangeSet noUsage = VolBitmap.VolUsage.GenerateNoUsageSet();
                IEnumerator<RangeSet.Range> iter = noUsage.RangeListIterator;
                while (iter.MoveNext()) {
                    RangeSet.Range range = iter.Current;
                    string msg = ("Found " + (range.High - range.Low + 1) +
                        " unused blocks marked used, starting at $" + range.Low.ToString("x4"));
                    if (doWarnUnused) {
                        Notes.AddW(msg);
                    } else {
                        Notes.AddI(msg);
                    }

                    // TODO: something useful with embedded volumes
                    //       see DiskFSProDOS::ScanForSubVolumes
                }
            }

            //Debug.WriteLine(VolBitmap.VolUsage.DebugDump());
        }

        private void ScanDirRecursive(IFileEntry idirEntry, int depth) {
            ProDOS_FileEntry dirEntry = (ProDOS_FileEntry)idirEntry;

            if (depth > MAX_DIR_DEPTH) {
                Notes.AddE("Max directory depth exceeded (" + MAX_DIR_DEPTH + "); circular?");
                dirEntry.IsDamaged = true;
                // We need to clear the list of children, or we'll stack overflow when trying to
                // invalidate the file entry objects if this is circular.
                dirEntry.ChildList.Clear();
                return;
            }

            dirEntry.ScanDir(true);
            if (!dirEntry.IsDamaged) {
                foreach (IFileEntry entry in dirEntry) {
                    if (entry.IsDirectory) {
                        ScanDirRecursive(entry, depth + 1);
                    }
                }
            }
        }

        /// <summary>
        /// Holder for data parsed from volume directory header.  This isn't an actual file,
        /// but it's useful to give it a "fake" entry, especially since certain fields (like
        /// creation date and access flags) can't otherwise be referenced without defining
        /// volume-specific APIs.
        /// </summary>
        internal class VolDirHeader {
            // Parsed data.
            public byte mStorageType;
            public byte mNameLength;
            public readonly byte[] mRawVolumeName = new byte[MAX_FILE_NAME_LEN];
            public ushort mReserved;
            public uint mModWhen;
            public ushort mLcFlags;
            public uint mCreateWhen;
            public byte mVersion;
            public byte mMinVersion;
            public byte mAccess;
            public byte mEntryLength;
            public byte mEntriesPerBlock;
            public ushort mFileCount;
            public ushort mBitMapPointer;
            public ushort mRawTotalBlocks;

            // Processed data.
            public string VolumeName { get; set; } = "(name unset)";
            public ushort TotalBlocks { get; set; }
        }

        /// <summary>
        /// Extracts the contents of the volume directory header from the disk image.
        /// </summary>
        /// <returns>False if this doesn't look like a valid ProDOS volume dir header.</returns>
        /// <exception cref="IOException">Failure to read blocks.</exception>
        private bool ExtractVolDirHeader() {
            byte[] blkBuf = new byte[BLOCK_SIZE];
            ChunkAccess.ReadBlock(VOL_DIR_START_BLK, blkBuf, 0);

            int offset = 0;
            ushort prevDirBlock = RawData.ReadU16LE(blkBuf, ref offset);
            ushort nextDirBlock = RawData.ReadU16LE(blkBuf, ref offset);

            byte typeAndLen = RawData.ReadU8(blkBuf, ref offset);
            mVolDirHeader.mStorageType = (byte)(typeAndLen >> 4);
            mVolDirHeader.mNameLength = (byte)(typeAndLen & 0x0f);
            Array.Copy(blkBuf, offset, mVolDirHeader.mRawVolumeName, 0, MAX_FILE_NAME_LEN);
            offset += MAX_FILE_NAME_LEN;
            mVolDirHeader.mReserved = RawData.ReadU16LE(blkBuf, ref offset);
            mVolDirHeader.mModWhen = RawData.ReadU32LE(blkBuf, ref offset);
            mVolDirHeader.mLcFlags = RawData.ReadU16LE(blkBuf, ref offset);
            mVolDirHeader.mCreateWhen = RawData.ReadU32LE(blkBuf, ref offset);
            mVolDirHeader.mVersion = RawData.ReadU8(blkBuf, ref offset);
            mVolDirHeader.mMinVersion = RawData.ReadU8(blkBuf, ref offset);
            mVolDirHeader.mAccess = RawData.ReadU8(blkBuf, ref offset);
            mVolDirHeader.mEntryLength = RawData.ReadU8(blkBuf, ref offset);
            mVolDirHeader.mEntriesPerBlock = RawData.ReadU8(blkBuf, ref offset);
            mVolDirHeader.mFileCount = RawData.ReadU16LE(blkBuf, ref offset);
            mVolDirHeader.mBitMapPointer = RawData.ReadU16LE(blkBuf, ref offset);
            mVolDirHeader.mRawTotalBlocks = RawData.ReadU16LE(blkBuf, ref offset);

            // Check for irregularities and damage.  It's okay to be fault-intolerant here,
            // because if something looks wrong this probably isn't actually ProDOS (though
            // we should have figured that out earlier).
            if (mVolDirHeader.mStorageType != (int)StorageType.VolDirHeader) {
                Notes.AddE("Unexpected storage type in volume header");
                return false;
            }
            if (mVolDirHeader.mNameLength == 0) {
                Notes.AddE("Volume name length must not be zero");
                return false;
            }
            if (prevDirBlock != 0) {
                Notes.AddE("First vol dir block has nonzero prev pointer");
                return false;
            }
            if (nextDirBlock == 1 || nextDirBlock == 2) {
                Notes.AddE("Invalid value (" + nextDirBlock + ") for vol dir next pointer");
                return false;
            }
            byte first = mVolDirHeader.mRawVolumeName[0];
            if (first < 'A' || first > 'Z') {
                Notes.AddE("First letter of volume name must be a letter");
                return false;
            }
            if (mVolDirHeader.mEntryLength < EXPECT_DIR_ENTRY_LENGTH) {
                // Too short, we won't find all of the fields we require.
                Notes.AddE("Dir entry len is too short (" + mVolDirHeader.mEntryLength + ")");
                return false;
            }
            if (mVolDirHeader.mEntriesPerBlock < 1 ||
                    mVolDirHeader.mEntriesPerBlock > Defs.BLOCK_SIZE / mVolDirHeader.mEntryLength) {
                // Bad math.
                Notes.AddE("Entries per block is bad (" + mVolDirHeader.mEntriesPerBlock + ")");
                return false;
            }
            if (mVolDirHeader.mBitMapPointer <= VOL_DIR_START_BLK ||
                    mVolDirHeader.mBitMapPointer >= mVolDirHeader.mRawTotalBlocks) {
                Notes.AddE("Invalid volume bitmap pointer: " + mVolDirHeader.mBitMapPointer);
                // Mark the filesystem as dubious, so we don't try to write to it, then use a
                // fake bitmap pointer so we don't trip assertions elsewhere.
                mVolDirHeader.mBitMapPointer = 6;
                IsDubious = true;
            }

            mVolDirHeader.VolumeName = ProDOS_FileEntry.GenerateMixedCaseName(
                mVolDirHeader.mRawVolumeName, mVolDirHeader.mNameLength,
                mVolDirHeader.mLcFlags, out bool isNameValid);
            if (!isNameValid) {
                Notes.AddW("Volume name uses illegal chars");
            }

            if (mVolDirHeader.mMinVersion != 0) {
                Notes.AddI("Volume header has nonzero MIN_VERSION, may confuse GS/OS");
            }

            // Compare the number of blocks in the volume to the number of blocks provided
            // by the chunk data source.  They should match, except for the special case
            // of a 32MB image, which might have an extra block at the end if the creator
            // decided to make it exactly 32MB.
            mVolDirHeader.TotalBlocks = mVolDirHeader.mRawTotalBlocks;
            long blocksInFile = ChunkAccess.FormattedLength / BLOCK_SIZE;
            if (mVolDirHeader.mRawTotalBlocks < MIN_VOL_SIZE / BLOCK_SIZE) {
                // Too small to continue parsing.
                Notes.AddE("Volume too small for ProDOS (" + mVolDirHeader.mRawTotalBlocks + ")");
                return false;
            } else if (mVolDirHeader.mRawTotalBlocks < blocksInFile) {
                // Extra data at end, e.g. a 140K volume on an 800K floppy.
                // The excess data will be used if the volume is reformatted.
                if (mVolDirHeader.mRawTotalBlocks == 65535 && blocksInFile == 65536) {
                    // Special case of a "32MB" 65535-block volume stored in a 65536-block area.
                    // This is sufficiently normal that we can skip the note.
                } else {
                    Notes.AddI("Under-sized volume spans " + mVolDirHeader.mRawTotalBlocks +
                        " blocks, container holds " + blocksInFile);
                }
            } else if (mVolDirHeader.mRawTotalBlocks > blocksInFile) {
                // Volume is larger than image file.  This is possible for expanding
                // formats like .HDV, but we don't handle expansion.  Cut down the declared
                // volume size to match the current size (but be careful not to write it).
                Notes.AddW("Reducing volume total blocks (" + mVolDirHeader.mRawTotalBlocks +
                    ") to match image size (" + blocksInFile + ")");
                // We know this value fits into ushort because vol dir entry's value is larger.
                mVolDirHeader.TotalBlocks = (ushort)blocksInFile;
            } else {
                // Exact match.
            }

            return true;
        }

        // IFileSystem
        public IMultiPart? FindEmbeddedVolumes() {
            if (mEmbeds != null) {
                return mEmbeds;
            }
            if (!IsPreppedForFileAccess) {
                throw new IOException("Must be in file access mode");
            }
            if (VolBitmap == null || !VolBitmap.DoTrackUsage) {
                // Complain, but not really worth throwing an exception.
                AppHook.LogE("FindEmbeddedVolumes: did not perform full scan in PrepareFileAccess");
                return null;
            }
            mEmbeds = ProDOS_Embedded.FindEmbeddedVolumes(ChunkAccess, VolBitmap.VolUsage, AppHook);
            Debug.Assert(mEmbeds == null || mEmbeds.Count > 0);
            return mEmbeds;
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
                    ChunkAccess.FormattedLength > MAX_VOL_SIZE + BLOCK_SIZE) {  // allow 1 over
                throw new ArgumentOutOfRangeException("total length");
            }
            long formatBlockCount = ChunkAccess.FormattedLength / BLOCK_SIZE;
            if (formatBlockCount == 65536) {
                // This is common enough that we should just handle it here.
                formatBlockCount = 65535;
            }

            // Validate volume name.  We don't care about volumeNum.
            if (!ProDOS_FileEntry.IsFileNameValid(volumeName)) {
                throw new ArgumentException("Invalid volume name");
            }

            // Write the standard boot block data to block 0 (we ignore the "make bootable" flag).
            ChunkAccess.WriteBlock(0, sBootBlock0, 0);

            byte[]? rawName = ProDOS_FileEntry.GenerateRawName(volumeName, out ushort lcFlags);
            Debug.Assert(rawName != null);      // format was validated earlier

            // Write four empty directory blocks, setting the prev/next block pointers.
            byte[] buffer = new byte[BLOCK_SIZE];
            uint bitmapPtr = 6;
            for (uint block = bitmapPtr - 1; block > VOL_DIR_START_BLK; block--) {
                RawData.SetU16LE(buffer, 0, (ushort)(block - 1));
                if (block != bitmapPtr - 1) {
                    RawData.SetU16LE(buffer, 2, (ushort)(block + 1));
                }
                ChunkAccess.WriteBlock(block, buffer, 0);
            }

            uint createWhen = TimeStamp.ConvertDateTime_ProDOS(DateTime.Now);

            // Fill out all of the fields in our volume dir storage.  The object will be discarded
            // and re-read when we switch to file mode, but it's convenient to fill the fields
            // out this way.
            mVolDirHeader.mStorageType = (byte)StorageType.VolDirHeader;
            mVolDirHeader.mNameLength = (byte)volumeName.Length;
            Array.Copy(rawName, mVolDirHeader.mRawVolumeName, MAX_FILE_NAME_LEN);
            mVolDirHeader.mReserved = 0;
            mVolDirHeader.mModWhen = createWhen;    // GS/OS writes this
            mVolDirHeader.mLcFlags = lcFlags;
            mVolDirHeader.mCreateWhen = createWhen;
            mVolDirHeader.mVersion = 5;             // GS/OS uses 5
            mVolDirHeader.mMinVersion = 0;
            mVolDirHeader.mAccess = 0xc3;           // read/write/rename/delete
            mVolDirHeader.mEntryLength = 0x27;
            mVolDirHeader.mEntriesPerBlock = 0x0d;
            mVolDirHeader.mFileCount = 0;
            mVolDirHeader.mBitMapPointer = (ushort)bitmapPtr;
            mVolDirHeader.mRawTotalBlocks = (ushort)formatBlockCount;

            mVolDirHeader.TotalBlocks = mVolDirHeader.mRawTotalBlocks;
            mVolDirHeader.VolumeName = volumeName;

            // Write the first directory block, which has the volume directory header.
            int hdrOff = 0;
            RawData.WriteU16LE(buffer, ref hdrOff, 0);
            RawData.WriteU16LE(buffer, ref hdrOff, VOL_DIR_START_BLK + 1);
            StoreVolDirHeader(buffer, hdrOff);
            ChunkAccess.WriteBlock(VOL_DIR_START_BLK, buffer, 0);

            // Create the volume bitmap, and mark boot / volume dir / bitmap blocks as in-use.
            // We're not in file mode, so we're not tracking volume usage, and don't need to set
            // the file entry arg for each block.
            VolBitmap = new ProDOS_VolBitmap(mVolDirHeader.TotalBlocks, bitmapPtr, ChunkAccess,
                doTrackUsage: false);
            VolBitmap.InitNewBitmap();
            for (uint i = 0; i < bitmapPtr + VolBitmap.NumBitmapBlocks; i++) {
                VolBitmap.MarkBlockUsed(i, IFileEntry.NO_ENTRY);
            }

            // Write the volume bitmap to disk, then reset state.
            VolBitmap.Flush();
            PrepareRawAccess();
        }

        /// <summary>
        /// Copies the contents of the VolDirHeader structure into the byte buffer.  Used
        /// when formatting a disk image.
        /// </summary>
        private void StoreVolDirHeader(byte[] buffer, int offset) {
            byte typeAndLen = (byte)(mVolDirHeader.mStorageType << 4 | mVolDirHeader.mNameLength);
            RawData.WriteU8(buffer, ref offset, typeAndLen);
            Array.Copy(mVolDirHeader.mRawVolumeName, 0, buffer, offset, MAX_FILE_NAME_LEN);
            offset += MAX_FILE_NAME_LEN;
            RawData.WriteU16LE(buffer, ref offset, mVolDirHeader.mReserved);
            RawData.WriteU32LE(buffer, ref offset, mVolDirHeader.mModWhen);
            RawData.WriteU16LE(buffer, ref offset, mVolDirHeader.mLcFlags);
            RawData.WriteU32LE(buffer, ref offset, mVolDirHeader.mCreateWhen);
            RawData.WriteU8(buffer, ref offset, mVolDirHeader.mVersion);
            RawData.WriteU8(buffer, ref offset, mVolDirHeader.mMinVersion);
            RawData.WriteU8(buffer, ref offset, mVolDirHeader.mAccess);
            RawData.WriteU8(buffer, ref offset, mVolDirHeader.mEntryLength);
            RawData.WriteU8(buffer, ref offset, mVolDirHeader.mEntriesPerBlock);
            RawData.WriteU16LE(buffer, ref offset, mVolDirHeader.mFileCount);
            RawData.WriteU16LE(buffer, ref offset, mVolDirHeader.mBitMapPointer);
            RawData.WriteU16LE(buffer, ref offset, mVolDirHeader.mRawTotalBlocks);
        }

        #endregion Preparation

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
        private void CheckFileAccess(string op, IFileEntry ientry, bool wantWrite,
                FilePart part) {
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
            ProDOS_FileEntry? entry = ientry as ProDOS_FileEntry;
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
            if (!IsPreppedForFileAccess) {
                throw new IOException("Filesystem object not prepared for file access");
            }
            return mVolDirEntry;
        }

        // IFileSystem
        public DiskFileStream OpenFile(IFileEntry ientry, FileAccessMode mode, FilePart part) {
            if (part == FilePart.RawData) {
                part = FilePart.DataFork;   // do this before is-file-open check
            }
            CheckFileAccess("open", ientry, mode != FileAccessMode.ReadOnly, part);
            if (mode != FileAccessMode.ReadOnly &&
                    mode != FileAccessMode.ReadWrite) {
                throw new ArgumentException("Unknown file access mode " + mode);
            }

            ProDOS_FileEntry entry = (ProDOS_FileEntry)ientry;
            switch (part) {
                case FilePart.DataFork:
                case FilePart.RawData:
                    break;
                case FilePart.RsrcFork:
                    if (!entry.HasRsrcFork) {
                        throw new IOException("File does not have a resource fork");
                    }
                    break;
                default:
                    throw new ArgumentException("Unknown file part " + part);
            }

            ProDOS_FileDesc pfd = ProDOS_FileDesc.CreateFD(entry, mode, part, false);
            mOpenFiles.Add(new OpenFileRec(entry, pfd));
            return pfd;
        }

        /// <summary>
        /// Determines whether we can open this file/part.  If the caller is requesting write
        /// access, we can't allow it if the file is already opened read-write.
        /// </summary>
        /// <remarks>
        /// This is also testing whether we're allowed to delete the file.  That is processed
        /// as a write operation on an ambiguous part.  If any part of the file is open, the
        /// call will be rejected.
        /// </remarks>
        /// <param name="entry">File to check.</param>
        /// <param name="wantWrite">True if we're going to modify the file.</param>
        /// <param name="part">File part.  Pass "Unknown" to match on any part.</param>
        /// <returns>True if all is well (no conflict).</returns>
        private bool CheckOpenConflict(ProDOS_FileEntry entry, bool wantWrite, FilePart part) {
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
        /// called from the ProDOS_FileDesc Dispose() call.
        /// </summary>
        /// <param name="ifd">Descriptor to close.</param>
        /// <exception cref="IOException">File descriptor was already closed, or was opened
        ///   by a different filesystem.</exception>
        internal void CloseFile(DiskFileStream ifd) {
            ProDOS_FileDesc fd = (ProDOS_FileDesc)ifd;
            if (fd.FileSystem != this) {
                // Should be impossible, though it could be null if previous close invalidated it.
                if (fd.FileSystem == null) {
                    throw new IOException("Invalid file descriptor");
                } else {
                    throw new IOException("File descriptor was opened by a different filesystem");
                }
            }

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

            if (VolBitmap != null) {
                // Flush any pending changes.  We do this here, rather than after each Write(),
                // for efficiency.  The VolBitmap may be null if the descriptor close is happening
                // as part of finalization.
                VolBitmap.Flush();
            }
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
            // (Example of when this happens: suppose file creation fails after one of the file
            // blocks is allocated.  The change is reverted, but the revert code doesn't restore
            // the dirty flags to their previous state.  We don't actually have anything to
            // write, but the flags are set so we do it anyway.)
            if (VolBitmap != null && VolBitmap.HasUnflushedChanges) {
                AppHook.LogI("CloseAll flushing volume bitmap changes");
                VolBitmap.Flush();
            }
        }

        // IFileSystem
        public IFileEntry CreateFile(IFileEntry idirEntry, string fileName, CreateMode mode) {
            CheckFileAccess("create", idirEntry, true, FilePart.Unknown);
            ProDOS_FileEntry dirEntry = (ProDOS_FileEntry)idirEntry;
            if (!dirEntry.IsDirectory) {
                throw new ArgumentException("Directory entry argument must be a directory");
            }
            if (fileName == null || !ProDOS_FileEntry.IsFileNameValid(fileName)) {
                throw new ArgumentException("Invalid filename '" + fileName + "'");
            }
            if (mode != CreateMode.File &&
                    mode != CreateMode.Directory &
                    mode != CreateMode.Extended) {
                throw new ArgumentException("Invalid CreateMode " + mode);
            }

            Debug.Assert(VolBitmap != null);

            // Check for an entry with a duplicate filename in the list of children.
            foreach (IFileEntry entry in dirEntry) {
                if (entry.CompareFileName(fileName) == 0) {
                    throw new IOException("A file with that name already exists");
                }
            }

            // Arguments appear to be valid.  Find an empty slot in the directory, allocating
            // a new directory block if necessary.  If we extend the directory, the new block
            // will not be un-done if we encounter an error later.
            FindDirSlot(dirEntry, out uint dirBlockNum, out byte blockSlot,
                out int fullSlot);
            int dirOffset = 4 + (blockSlot - 1) * dirEntry.DirEntryLength;

            // Read the directory block.  Do this before anything else in case it fails.
            byte[] modData = new byte[BLOCK_SIZE];
            ChunkAccess.ReadBlock(dirBlockNum, modData, 0);

            // Generate the raw filename and lower-case flags.
            byte[]? rawFileName = ProDOS_FileEntry.GenerateRawName(fileName, out ushort lcFlags);
            Debug.Assert(rawFileName != null);      // filename was validated earlier
            uint createWhen = TimeStamp.ConvertDateTime_ProDOS(DateTime.Now);
            byte fileType = 0x00;
            byte newStorageType;
            ushort blocksUsed;

            ProDOS_FileEntry newEntry = new ProDOS_FileEntry(this, dirEntry);

            VolBitmap.BeginUpdate();
            try {
                uint keyBlock;
                if (mode == CreateMode.File) {
                    // Just allocate a block to use as the key block.
                    newStorageType = (byte)StorageType.Seedling;
                    blocksUsed = 1;
                    keyBlock = VolBitmap.AllocBlock(newEntry);
                } else if (mode == CreateMode.Extended) {
                    // Allocate one block for each fork plus the key block.
                    newStorageType = (byte)StorageType.Extended;
                    blocksUsed = 3;
                    keyBlock = VolBitmap.AllocBlock(newEntry);
                    uint dataKeyBlock = VolBitmap.AllocBlock(newEntry);
                    uint rsrcKeyBlock = VolBitmap.AllocBlock(newEntry);

                    // Fill out the extended info block.
                    byte[] extData = new byte[BLOCK_SIZE];
                    extData[0x00] = (int)StorageType.Seedling;
                    RawData.SetU16LE(extData, 0x01, (ushort)dataKeyBlock);
                    RawData.SetU16LE(extData, 0x03, 1);
                    extData[0x0100] = (int)StorageType.Seedling;
                    RawData.SetU16LE(extData, 0x0101, (ushort)rsrcKeyBlock);
                    RawData.SetU16LE(extData, 0x0103, 1);

                    // Write it to disk.
                    ChunkAccess.WriteBlock(keyBlock, extData, 0);
                } else /*mode == IFileSystem.CreateMode.Directory*/ {
                    // Allocate the first block of the directory.
                    newStorageType = (byte)StorageType.Directory;
                    blocksUsed = 1;
                    keyBlock = VolBitmap.AllocBlock(newEntry);
                    fileType = FileAttribs.FILE_TYPE_DIR;

                    // Fill out the directory header.
                    byte[] dirHdr = new byte[BLOCK_SIZE];
                    int hdrOffset = 4;     // skip over prev/next pointers (both zero)
                    byte typeLen = (byte)((int)StorageType.SubdirHeader << 4 | fileName.Length);

                    RawData.WriteU8(dirHdr, ref hdrOffset, typeLen);
                    Array.Copy(rawFileName, 0, dirHdr, hdrOffset, MAX_FILE_NAME_LEN);
                    hdrOffset += MAX_FILE_NAME_LEN;
                    RawData.WriteU8(dirHdr, ref hdrOffset, 0x76);       // magic reserved value
                    hdrOffset += 7;                                 // reserved, leave set to zero
                    RawData.WriteU32LE(dirHdr, ref hdrOffset, createWhen);
                    RawData.WriteU8(dirHdr, ref hdrOffset, 0x05);       // version (GS/OS value)
                    RawData.WriteU8(dirHdr, ref hdrOffset, 0x00);       // min_version
                    RawData.WriteU8(dirHdr, ref hdrOffset, 0xc3);       // access flags (unlocked)
                    RawData.WriteU8(dirHdr, ref hdrOffset, EXPECT_DIR_ENTRY_LENGTH);
                    RawData.WriteU8(dirHdr, ref hdrOffset, EXPECT_DIR_PER_BLOCK);
                    RawData.WriteU16LE(dirHdr, ref hdrOffset, 0x0000);  // active entries is zero
                    RawData.WriteU16LE(dirHdr, ref hdrOffset, (ushort)dirBlockNum);
                    RawData.WriteU8(dirHdr, ref hdrOffset, blockSlot);
                    RawData.WriteU8(dirHdr, ref hdrOffset, dirEntry.DirEntryLength);
                    Debug.Assert(hdrOffset == 4 + EXPECT_DIR_ENTRY_LENGTH);

                    // Write it to disk.
                    ChunkAccess.WriteBlock(keyBlock, dirHdr, 0);
                }

                Debug.Assert((modData[dirOffset] & 0xf0) == (int)StorageType.Deleted);

                // Fill out the directory entry.  We're doing this in the existing block, not
                // a new block, so we can't skip over zeroed-out fields.
                int offset = dirOffset;
                RawData.WriteU8(modData, ref offset, (byte)(newStorageType << 4 | fileName.Length));
                Array.Copy(rawFileName, 0, modData, offset, MAX_FILE_NAME_LEN);
                offset += MAX_FILE_NAME_LEN;
                RawData.WriteU8(modData, ref offset, fileType);         // file type = $00
                RawData.WriteU16LE(modData, ref offset, (ushort)keyBlock);
                RawData.WriteU16LE(modData, ref offset, blocksUsed);
                if (mode == CreateMode.File) {
                    RawData.WriteU24LE(modData, ref offset, 0);         // EOF = 0
                } else {
                    RawData.WriteU24LE(modData, ref offset, 512);       // EOF = 512 for dir/ext
                }
                RawData.WriteU32LE(modData, ref offset, createWhen);    // create date
                RawData.WriteU16LE(modData, ref offset, lcFlags);
                RawData.WriteU8(modData, ref offset, 0xe3);             // unlocked / backup
                RawData.WriteU16LE(modData, ref offset, 0x0000);        // aux type = $00
                RawData.WriteU32LE(modData, ref offset, createWhen);    // mod date
                RawData.WriteU16LE(modData, ref offset, (ushort)dirEntry.KeyBlock);
                Debug.Assert(offset - dirOffset == EXPECT_DIR_ENTRY_LENGTH);

                // Write the block back to disk.  Failure at this point is harmless since
                // nothing refers to the blocks we've previously written, so long as we abort
                // the volume bitmap changes.
                ChunkAccess.WriteBlock(dirBlockNum, modData, 0);

                VolBitmap.EndUpdate();
            } catch (Exception) {
                VolBitmap.AbortUpdate();
                throw;
            }
            VolBitmap.Flush();

            // Increment the directory's file_count field.
            dirEntry.UpdateDirFileCount(+1);

            // Populate the file entry object for the new file.
            if (!newEntry.ExtractFileEntry(dirBlockNum, blockSlot, dirEntry.DirEntryLength,
                    modData, ref dirOffset)) {
                throw new DAException("FileEntry rejected our new entry");
            }
            if (mode == CreateMode.Directory) {
                // Supply a few fields that come from the directory file's header, rather
                // than the directory entry.  Normally these are set during the directory scan.
                newEntry.DirEntryLength = EXPECT_DIR_ENTRY_LENGTH;
                newEntry.DirEntriesPerBlock = EXPECT_DIR_PER_BLOCK;
                newEntry.DirFileCount = 0;
                newEntry.IsScanned = true;
            }
            Debug.Assert(!newEntry.IsDamaged && !newEntry.IsDubious);

            //Debug.WriteLine("ADD: " + fileName + " fullSlot=" + fullSlot +
            //    " direntBlk=" + newEntry.DirentBlockNum + " direntOff=" + newEntry.DirentOffset);

            // Insert it into the list of children.
            dirEntry.ChildList.Insert(fullSlot, newEntry);

            return newEntry;
        }

        /// <summary>
        /// Finds the first empty slot in the specified directory.  If no slot is available,
        /// the directory will be extended if possible.
        /// </summary>
        /// <param name="dirEntry">Directory in which we are creating the new file.</param>
        /// <param name="dirBlockNum">Result: number of block with empty slot.</param>
        /// <param name="direntSlot">Result: slot number (1-N) within this block.</param>
        /// <param name="fullSlot">Result: slot number (0-N) within the List.</param>
        /// <exception cref="DiskFullException">Disk full, or volume directory full.</exception>
        /// <exception cref="DAException">Something bad happened.</exception>
        private void FindDirSlot(ProDOS_FileEntry dirEntry, out uint dirBlockNum,
                out byte direntSlot, out int fullSlot) {
            Debug.Assert(dirEntry.DirEntriesPerBlock > 0);
            Debug.Assert(dirEntry.DirEntryLength > 0);

            // This represents the file's index in the List<> of children.
            fullSlot = 0;

            uint blockNum = dirEntry.KeyBlock;
            Debug.Assert(blockNum != 0);

            bool first = true;
            byte[] blkData = new byte[BLOCK_SIZE];
            while (true) {
                ChunkAccess.ReadBlock(blockNum, blkData, 0);

                // Skip the prev/next pointers, and the directory header in the first block.
                int offset = 4;
                int remaining = dirEntry.DirEntriesPerBlock;
                if (first) {
                    first = false;
                    offset += dirEntry.DirEntryLength;
                    remaining--;
                }

                // Search this block.
                while (remaining-- != 0) {
                    byte typeAndLen = blkData[offset];
                    if ((typeAndLen & 0xf0) == (int)StorageType.Deleted) {
                        // Found a deleted or never-used slot.
                        //Debug.WriteLine("Found empty dir slot in block " + blockNum +
                        //    " off=" + offset);
                        dirBlockNum = blockNum;
                        direntSlot = (byte)(dirEntry.DirEntriesPerBlock - remaining);
                        return;
                    }
                    offset += dirEntry.DirEntryLength;
                    fullSlot++;
                }

                ushort nextBlockNum = RawData.GetU16LE(blkData, 2);
                if (nextBlockNum == 0) {
                    // Break out of loop with last block number still in blockNum.
                    break;
                }

                blockNum = nextBlockNum;
            }

            //
            // We failed to find an empty slot.  Expand the directory if possible.
            //

            //Debug.WriteLine("No empty slot found");

            if (dirEntry.DirFileCount != dirEntry.BlocksUsed * dirEntry.DirEntriesPerBlock - 1) {
                // This isn't necessarily incorrect, as there's no requirement to fill the
                // first empty slot, but it hints at problems elsewhere.
                Debug.Assert(false, "Should have found an empty slot");
            }
            if (dirEntry.IsVolumeDirectory) {
                // Can't expand the volume dir.
                throw new DiskFullException("Volume directory is full");
            }
            if (dirEntry.DirFileCount >= 65535) {
                // This should be impossible, since each file in the directory needs 1 block,
                // but could happen if we didn't reliably use the first available entry when
                // creating new files.  Check it so we don't overflow file_count.
                throw new DiskFullException("Directory full");
            }
            Debug.Assert(VolBitmap != null);    // appease nullability check

            VolBitmap.BeginUpdate();
            try {
                uint newBlockNum = VolBitmap.AllocBlock(dirEntry);

                // Create the new directory block, which just needs the prev/next pointers.
                // Write it to disk.  If this fails, all we need to do is revert the allocation.
                byte[] newBlockBuf = new byte[BLOCK_SIZE];
                RawData.SetU16LE(newBlockBuf, 0, (ushort)blockNum);     // set prev
                ChunkAccess.WriteBlock(newBlockNum, newBlockBuf, 0);

                // Update the prev pointer in the last dir block.
                Debug.Assert(RawData.GetU16LE(blkData, 2) == 0);    // old "next" ptr
                RawData.SetU16LE(blkData, 2, (ushort)newBlockNum);  // set next
                ChunkAccess.WriteBlock(blockNum, blkData, 0);

                // Set the result parameter.
                dirBlockNum = newBlockNum;

                VolBitmap.EndUpdate();
                // don't flush bitmap change; caller will be doing that shortly
            } catch (Exception) {
                VolBitmap.AbortUpdate();
                throw new IOException("Directory extension failed");
            }

            // We need to update the storage size in the file's directory entry.  If this
            // fails, the filesystem will be in a mildly inconsistent state... but if we can't
            // access the directory block, having an incorrect storage size is probably
            // the least of our worries.
            dirEntry.BlocksUsed++;
            dirEntry.EndOfFile += BLOCK_SIZE;
            dirEntry.SaveChanges();

            // Use the first entry.
            direntSlot = 1;
        }

        // IFileSystem
        public void AddRsrcFork(IFileEntry ientry) {
            CheckFileAccess("extend", ientry, true, FilePart.Unknown);
            ProDOS_FileEntry entry = (ProDOS_FileEntry)ientry;
            if (entry.HasRsrcFork) {
                return;
            }
            if (entry.StorageType != StorageType.Seedling &&
                    entry.StorageType != StorageType.Sapling &&
                    entry.StorageType != StorageType.Tree) {
                throw new IOException("File can not be extended");
            }

            entry.AddRsrcFork();
        }

        // IFileSystem
        public void MoveFile(IFileEntry ientry, IFileEntry idestDir, string newFileName) {
            CheckFileAccess("move", ientry, true, FilePart.Unknown);
            if (idestDir == IFileEntry.NO_ENTRY || !((ProDOS_FileEntry)idestDir).IsValid) {
                throw new ArgumentException("Invalid destination file entry");
            }
            if (!idestDir.IsDirectory) {
                throw new ArgumentException("Destination file entry must be a directory");
            }
            if (newFileName == null || !HFS_FileEntry.IsFileNameValid(newFileName, false)) {
                throw new ArgumentException("Invalid filename '" + newFileName + "'");
            }
            ProDOS_FileEntry destDir = (ProDOS_FileEntry)idestDir;
            if (destDir == null || destDir.FileSystem != this) {
                if (destDir != null && destDir.FileSystem == null) {
                    // Invalid entry; could be a deleted file, or from before a raw-mode switch.
                    throw new IOException("Destination directory is invalid");
                } else {
                    throw new FileNotFoundException(
                        "Destination directory is not part of this filesystem");
                }
            }
            ProDOS_FileEntry entry = (ProDOS_FileEntry)ientry;
            if (entry.ContainingDir == idestDir) {
                // Moving to the same directory.  This is just a rename.
                entry.FileName = newFileName;
                entry.SaveChanges();
                return;
            }
            if (entry.IsVolumeDirectory) {
                throw new ArgumentException("Can't move volume directory");
            }
            if (entry.DirentEntryLength != destDir.DirEntryLength) {
                throw new ArgumentException("Destination dirent size is different from source");
            }

            // Unless we're renaming the volume, we need to make sure the name isn't already in
            // use.  We can just check the destination directory's child list.
            Debug.Assert(!entry.IsVolumeDirectory);
            foreach (IFileEntry checkEntry in destDir) {
                if (checkEntry.CompareFileName(newFileName) == 0) {
                    throw new IOException("A file with that name already exists");
                }
            }

            // If we're moving a directory, make sure we're not trying to move it into a child.
            if (entry.IsDirectory) {
                IFileEntry checkEnt = idestDir;
                while (checkEnt != IFileEntry.NO_ENTRY) {
                    if (checkEnt == entry) {
                        throw new IOException("Cannot move directory to a subdirectory of itself");
                    }
                    checkEnt = checkEnt.ContainingDir;
                }
            }
            ProDOS_FileEntry srcDir = (ProDOS_FileEntry)entry.ContainingDir;

            // Read the directory block that has the file entry.
            byte[] srcDirBuf = new byte[BLOCK_SIZE];
            uint srcDirBlock = entry.DirentBlockPtr;
            ChunkAccess.ReadBlock(srcDirBlock, srcDirBuf, 0);
            int srcDirOffset = 4 + (entry.DirentIndex - 1) * entry.DirentEntryLength;

            // Find the first available slot in the destination directory.  This will extend
            // the subdirectory file by one block if nothing is available.  This won't be undone
            // if we fail further on.
            FindDirSlot(destDir, out uint destDirBlock, out byte destBlockSlot,
                out int destFullSlot);
            int destDirOffset = 4 + (destBlockSlot - 1) * destDir.DirEntryLength;

            VolBitmap!.Flush();     // no further volume bitmap changes

            // Read the destination directory block.
            byte[] destDirBuf = new byte[BLOCK_SIZE];
            ChunkAccess.ReadBlock(destDirBlock, destDirBuf, 0);

            // Copy contents.
            Array.Copy(srcDirBuf, srcDirOffset, destDirBuf, destDirOffset, destDir.DirEntryLength);

            // Mark old entry as deleted by setting the storage type to zero.
            srcDirBuf[srcDirOffset] &= 0x0f;

            // Update file entry with new name and directory values.  These make changes
            // internally and mark the entry as dirty, deferring the actual update until
            // SaveChanges().  Mostly we want the new raw filename.  We need to change the
            // filename after changing the containing dir so that the test for duplicates
            // doesn't trip.
            entry.MoveEntry(destDir, destDirBlock, destBlockSlot);
            entry.FileName = newFileName;

            byte[] newRawName = entry.RawFileName;
            byte[] newRawBuf = new byte[MAX_FILE_NAME_LEN + 1];
            Array.Copy(newRawName, 0, newRawBuf, 1, newRawName.Length);
            newRawBuf[0] = (byte)(((byte)entry.StorageType << 4) | (byte)newRawName.Length);
            Array.Copy(newRawBuf, 0, destDirBuf, destDirOffset, MAX_FILE_NAME_LEN + 1);

            RawData.SetU16LE(destDirBuf, destDirOffset + 0x25, destDir.KeyBlock);

            // If this is a directory, we need to update a few things in the header stored in
            // the first (key) block.
            if (entry.IsDirectory) {
                byte[] firstDirBuf = new byte[BLOCK_SIZE];
                ChunkAccess.ReadBlock(entry.KeyBlock, firstDirBuf, 0);

                byte storageType = (byte)(firstDirBuf[4] & 0xf0);

                // Copy the new name and length byte over, and fix the header storage type.
                Array.Copy(newRawBuf, 0, firstDirBuf, 4, MAX_FILE_NAME_LEN + 1);
                firstDirBuf[4] = (byte)((firstDirBuf[4] & 0x0f) | storageType);

                // Fix the parent references.
                RawData.SetU16LE(firstDirBuf, 4 + 0x23, (ushort)destDirBlock);
                firstDirBuf[4 + 0x25] = destBlockSlot;

                // Write updated dir header.
                ChunkAccess.WriteBlock(entry.KeyBlock, firstDirBuf, 0);
            }

            // Write updated directory blocks.
            ChunkAccess.WriteBlock(srcDirBlock, srcDirBuf, 0);
            ChunkAccess.WriteBlock(destDirBlock, destDirBuf, 0);

            // Tell the entry to flush changes.  This shouldn't actually find anything to do.
            entry.SaveChanges();

            // Update valences.
            srcDir.UpdateDirFileCount(-1);
            destDir.UpdateDirFileCount(+1);

            // Update our children lists.
            srcDir.ChildList.Remove(entry);
            destDir.ChildList.Insert(destFullSlot, entry);
        }

        // IFileSystem
        public void DeleteFile(IFileEntry ientry) {
            CheckFileAccess("delete", ientry, true, FilePart.Unknown);
            if (ientry == mVolDirEntry) {
                throw new IOException("Can't delete volume directory");
            }
            ProDOS_FileEntry entry = (ProDOS_FileEntry)ientry;
            if (entry.IsDirectory && entry.ChildList.Count != 0) {
                throw new IOException("Directory is not empty (" +
                    entry.ChildList.Count + " files)");
            }

            ProDOS_FileEntry dirEntry = (ProDOS_FileEntry)ientry.ContainingDir;
            Debug.Assert(dirEntry.IsDirectory);

            Debug.Assert(VolBitmap != null);
            VolBitmap.BeginUpdate();
            try {
                // We examined the file during the directory scan, so any bad files should
                // have been marked as damaged/dubious and thus undeletable.  We're not expecting
                // this to fail.
                //
                // The access mode doesn't matter here.  We use mode=read-only so the call
                // doesn't get upset about directories.
                using (ProDOS_FileDesc dataFd = ProDOS_FileDesc.CreateFD(entry,
                        FileAccessMode.ReadOnly, FilePart.DataFork, true)) {
                    dataFd.ReleaseStorage();
                }

                if (entry.HasRsrcFork) {
                    using (ProDOS_FileDesc rsrcFd = ProDOS_FileDesc.CreateFD(entry,
                            FileAccessMode.ReadOnly, FilePart.RsrcFork, true)) {
                        rsrcFd.ReleaseStorage();
                    }
                    // Release the extended info block.
                    VolBitmap.MarkBlockUnused(entry.KeyBlock);
                }

                // Update the file's storage type in the directory entry.
                entry.StorageType = StorageType.Deleted;
                // Update the directory entry before the bitmap.  If the directory entry
                // change fails, the filesystem state will still be consistent.
                entry.SaveChanges();

                VolBitmap.EndUpdate();
            } catch (Exception) {
                VolBitmap.AbortUpdate();
                throw;
            }

            // Decrement the directory's file_count field.
            dirEntry.UpdateDirFileCount(-1);

            // If this fails, we'll have unused blocks that are marked used.  Not ideal, but
            // not dangerous.
            VolBitmap.Flush();

            // Update internal data structures.  This should not fail unless the library is
            // broken.
            ProDOS_FileEntry parent = (ProDOS_FileEntry)entry.ContainingDir;
            parent.ChildList.Remove(entry);

            // This entry may no longer be used.
            entry.Invalidate();
        }

        #endregion File Access

        #region Miscellaneous

        /// <summary>
        /// ProDOS boot block.  Blocks 0 and 1 are reserved, but only block 0 is used.
        /// Some hard drive images have an alternating pattern in block 1, but on most disks
        /// it's just zeroed out.
        ///
        /// This boot block was generated by the ProDOS FST in GS/OS 6.0.1 for an 800KB
        /// floppy disk.  Variations exist, but this should work for everything.
        ///
        /// ProDOS is copyright Apple Computer, Inc.
        /// </summary>
        private static readonly byte[] sBootBlock0 = new byte[512] {
            0x01,0x38,0xb0,0x03,0x4c,0x1c,0x09,0x78,0x86,0x43,0xc9,0x03,0x08,0x8a,0x29,0x70,
            0x4a,0x4a,0x4a,0x4a,0x09,0xc0,0x85,0x49,0xa0,0xff,0x84,0x48,0x28,0xc8,0xb1,0x48,
            0xd0,0x3a,0xb0,0x0e,0xa9,0x03,0x8d,0x00,0x08,0xe6,0x3d,0xa5,0x49,0x48,0xa9,0x5b,
            0x48,0x60,0x85,0x40,0x85,0x48,0xa0,0x5e,0xb1,0x48,0x99,0x94,0x09,0xc8,0xc0,0xeb,
            0xd0,0xf6,0xa2,0x06,0xbc,0x32,0x09,0xbd,0x39,0x09,0x99,0xf2,0x09,0xbd,0x40,0x09,
            0x9d,0x7f,0x0a,0xca,0x10,0xee,0xa9,0x09,0x85,0x49,0xa9,0x86,0xa0,0x00,0xc9,0xf9,
            0xb0,0x2f,0x85,0x48,0x84,0x60,0x84,0x4a,0x84,0x4c,0x84,0x4e,0x84,0x47,0xc8,0x84,
            0x42,0xc8,0x84,0x46,0xa9,0x0c,0x85,0x61,0x85,0x4b,0x20,0x27,0x09,0xb0,0x66,0xe6,
            0x61,0xe6,0x61,0xe6,0x46,0xa5,0x46,0xc9,0x06,0x90,0xef,0xad,0x00,0x0c,0x0d,0x01,
            0x0c,0xd0,0x52,0xa9,0x04,0xd0,0x02,0xa5,0x4a,0x18,0x6d,0x23,0x0c,0xa8,0x90,0x0d,
            0xe6,0x4b,0xa5,0x4b,0x4a,0xb0,0x06,0xc9,0x0a,0xf0,0x71,0xa0,0x04,0x84,0x4a,0xad,
            0x20,0x09,0x29,0x0f,0xa8,0xb1,0x4a,0xd9,0x20,0x09,0xd0,0xdb,0x88,0x10,0xf6,0xa0,
            0x16,0xb1,0x4a,0x4a,0x6d,0x1f,0x09,0x8d,0x1f,0x09,0xa0,0x11,0xb1,0x4a,0x85,0x46,
            0xc8,0xb1,0x4a,0x85,0x47,0xa9,0x00,0x85,0x4a,0xa0,0x1e,0x84,0x4b,0x84,0x61,0xc8,
            0x84,0x4d,0x20,0x27,0x09,0xb0,0x35,0xe6,0x61,0xe6,0x61,0xa4,0x4e,0xe6,0x4e,0xb1,
            0x4a,0x85,0x46,0xb1,0x4c,0x85,0x47,0x11,0x4a,0xd0,0x18,0xa2,0x01,0xa9,0x00,0xa8,
            0x91,0x60,0xc8,0xd0,0xfb,0xe6,0x61,0xea,0xea,0xca,0x10,0xf4,0xce,0x1f,0x09,0xf0,
            0x07,0xd0,0xd8,0xce,0x1f,0x09,0xd0,0xca,0x58,0x4c,0x00,0x20,0x4c,0x47,0x09,0x02,
            0x26,0x50,0x52,0x4f,0x44,0x4f,0x53,0xa5,0x60,0x85,0x44,0xa5,0x61,0x85,0x45,0x6c,
            0x48,0x00,0x08,0x1e,0x24,0x3f,0x45,0x47,0x76,0xf4,0xd7,0xd1,0xb6,0x4b,0xb4,0xac,
            0xa6,0x2b,0x18,0x60,0x4c,0xbc,0x09,0x20,0x58,0xfc,0xa0,0x14,0xb9,0x58,0x09,0x99,
            0xb1,0x05,0x88,0x10,0xf7,0x4c,0x55,0x09,0xd5,0xce,0xc1,0xc2,0xcc,0xc5,0xa0,0xd4,
            0xcf,0xa0,0xcc,0xcf,0xc1,0xc4,0xa0,0xd0,0xd2,0xcf,0xc4,0xcf,0xd3,0xa5,0x53,0x29,
            0x03,0x2a,0x05,0x2b,0xaa,0xbd,0x80,0xc0,0xa9,0x2c,0xa2,0x11,0xca,0xd0,0xfd,0xe9,
            0x01,0xd0,0xf7,0xa6,0x2b,0x60,0xa5,0x46,0x29,0x07,0xc9,0x04,0x29,0x03,0x08,0x0a,
            0x28,0x2a,0x85,0x3d,0xa5,0x47,0x4a,0xa5,0x46,0x6a,0x4a,0x4a,0x85,0x41,0x0a,0x85,
            0x51,0xa5,0x45,0x85,0x27,0xa6,0x2b,0xbd,0x89,0xc0,0x20,0xbc,0x09,0xe6,0x27,0xe6,
            0x3d,0xe6,0x3d,0xb0,0x03,0x20,0xbc,0x09,0xbc,0x88,0xc0,0x60,0xa5,0x40,0x0a,0x85,
            0x53,0xa9,0x00,0x85,0x54,0xa5,0x53,0x85,0x50,0x38,0xe5,0x51,0xf0,0x14,0xb0,0x04,
            0xe6,0x53,0x90,0x02,0xc6,0x53,0x38,0x20,0x6d,0x09,0xa5,0x50,0x18,0x20,0x6f,0x09,
            0xd0,0xe3,0xa0,0x7f,0x84,0x52,0x08,0x28,0x38,0xc6,0x52,0xf0,0xce,0x18,0x08,0x88,
            0xf0,0xf5,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        };

        #endregion Miscellaneous
    }
}
