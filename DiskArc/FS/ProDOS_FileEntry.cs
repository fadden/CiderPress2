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
using System.Text.RegularExpressions;

using CommonUtil;
using static DiskArc.Defs;

namespace DiskArc.FS {
    /// <summary>
    /// This represents the contents of a directory entry, with additional details from the
    /// extended info block (for forked files) or directory header (for directory files).
    /// Changes to the file metadata are stored here and flushed to disk on command.  A single
    /// object is associated with each file that has been scanned.
    /// </summary>
    public class ProDOS_FileEntry : IFileEntryExt, IDisposable {
        #region IFileEntry / dirent

        //
        // IFileEntry interfaces.
        //

        public bool IsValid { get { return FileSystem != null; } }
        public bool IsDubious { get { return mHasConflict || HasJunk; } }
        public bool IsDamaged { get; internal set; }

        public bool IsDirectory {
            get { return (mStorageType == (int)ProDOS.StorageType.Directory); }
        }
        public bool HasDataFork => true;
        public bool HasRsrcFork {
            get { return (mStorageType == (int)ProDOS.StorageType.Extended); }
        }
        public bool IsDiskImage => false;

        public IFileEntry ContainingDir { get; private set; }

        public int Count => mChildList.Count;

        public string FullPathName {
            get {
                // The value is generated on each use, so that changes to parent directory
                // names are handled correctly.
                string pathName;
                if (FileSystem == null) {
                    pathName = "!INVALID! was: '" + mFileName + "'";
                } else if (string.IsNullOrEmpty(mFileName)) {
                    pathName = "!FileNameEmpty!";
                } else if (IsVolumeDirectory) {
                    pathName = ProDOS.SEP_CHAR + mFileName;
                } else {
                    Debug.Assert(ContainingDir != IFileEntry.NO_ENTRY);
                    if (((ProDOS_FileEntry)ContainingDir).IsVolumeDirectory) {
                        pathName = mFileName;
                    } else {
                        pathName = ContainingDir.FullPathName + ProDOS.SEP_CHAR + mFileName;
                    }
                }
                return pathName;
            }
        }
        public string FileName {
            get { return mFileName; }
            set {
                CheckChangeAllowed();
                SetFileName(value);
                IsDirentDirty = true;
            }
        }
        public char DirectorySeparatorChar { get => ProDOS.SEP_CHAR; set { } }
        public byte[] RawFileName {
            get {
                byte[] result = new byte[mFileName.Length];
                Array.Copy(mRawFileName, result, result.Length);
                return result;
            }
            set {
                CheckChangeAllowed();
                // Convert the value to a string and use the filename setter.  This allows
                // the "raw" interface to accept lower-case letters.  This might violate the
                // spirit of "raw", but there's no other way to set the LC flags, and ProDOS
                // isn't really an interesting case for raw filenames anyway.
                SetFileName(System.Text.Encoding.ASCII.GetString(value));
                IsDirentDirty = true;
            }
        }

        public bool HasProDOSTypes { get { return true; } }
        public byte FileType {
            get { return mFileType; }
            set {
                CheckChangeAllowed();
                if (IsDirectory) {
                    return;
                } else {
                    // Disallow putting the DIR file type on non-directory files.
                    if (value == FileAttribs.FILE_TYPE_DIR) {
                        return;
                    }
                }
                mFileType = value;
                IsDirentDirty = true;
            }
        }
        public ushort AuxType {
            get { return mAuxType; }
            set {
                CheckChangeAllowed();
                if (IsDirectory) { return; }
                mAuxType = value;
                IsDirentDirty = true;
            }
        }

        public bool HasHFSTypes {
            get { return HasRsrcFork; }
        }
        public uint HFSFileType {
            get { return ExtInfo == null ? 0 : ExtInfo.HFSFileType; }
            set {
                CheckChangeAllowed();
                if (ExtInfo == null) { return; }
                ExtInfo.HFSFileType = value;
            }
        }
        public uint HFSCreator {
            get { return ExtInfo == null ? 0 : ExtInfo.HFSCreator; }
            set {
                CheckChangeAllowed();
                if (ExtInfo == null) { return; }
                ExtInfo.HFSCreator = value;
            }
        }

        public byte Access {
            get { return mAccess; }
            set {
                CheckChangeAllowed();
                mAccess = value;
                IsDirentDirty = true;
            }
        }
        public DateTime CreateWhen {
            get { return TimeStamp.ConvertDateTime_ProDOS(mCreateWhen); }
            set {
                CheckChangeAllowed();
                mCreateWhen = TimeStamp.ConvertDateTime_ProDOS(value);
                IsDirentDirty = true;
            }
        }
        public DateTime ModWhen {
            get { return TimeStamp.ConvertDateTime_ProDOS(mModWhen); }
            set {
                CheckChangeAllowed();
                mModWhen = TimeStamp.ConvertDateTime_ProDOS(value);
                IsDirentDirty = true;
            }
        }

        public long StorageSize { get { return BlocksUsed * BLOCK_SIZE; } }
        public long DataLength {
            get {
                if (mStorageType == (int)ProDOS.StorageType.Extended) {
                    Debug.Assert(ExtInfo != null);
                    return ExtInfo.DataEof;
                } else {
                    return mEof;
                }
            }
        }
        public long RsrcLength {
            get {
                if (mStorageType == (int)ProDOS.StorageType.Extended) {
                    Debug.Assert(ExtInfo != null);
                    return ExtInfo.RsrcEof;
                } else {
                    return 0;
                }
            }
        }

        public string Comment { get => string.Empty; set { } }

        public bool GetPartInfo(FilePart part, out long length, out long storageSize,
                out CompressionFormat format) {
            format = CompressionFormat.Uncompressed;
            if (part == FilePart.DataFork || part == FilePart.RawData) {
                if (mStorageType == (int)ProDOS.StorageType.Extended) {
                    Debug.Assert(ExtInfo != null);
                    length = ExtInfo.DataEof;
                    storageSize = ExtInfo.DataBlocksUsed * BLOCK_SIZE;
                } else {
                    length = DataLength;
                    storageSize = StorageSize;
                }
                return true;
            } else if (part == FilePart.RsrcFork) {
                if (mStorageType == (int)ProDOS.StorageType.Extended) {
                    Debug.Assert(ExtInfo != null);
                    length = ExtInfo.RsrcEof;
                    // We could include the 512-byte extended info block here, but it may be
                    // useful to have the actual resource fork block count available.
                    storageSize = ExtInfo.RsrcBlocksUsed * BLOCK_SIZE;
                    return true;
                } else {
                    length = -1;
                    storageSize = 0;
                    return false;
                }
            } else {
                length = -1;
                storageSize = 0;
                return false;
            }
        }

        public IEnumerator<IFileEntry> GetEnumerator() {
            if (!IsValid) {
                throw new DAException("Invalid file entry object");
            }
            return ChildList.GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() {
            if (!IsValid) {
                throw new DAException("Invalid file entry object");
            }
            return ChildList.GetEnumerator();
        }

        /// <summary>
        /// Throws an exception if attribute changes are not allowed.
        /// </summary>
        private void CheckChangeAllowed() {
            if (FileSystem == null || FileSystem.IsReadOnly || IsDubious || IsDamaged) {
                throw new IOException("Changes not allowed");
            }
        }

        //
        // ProDOS-specific properties.
        //

        // Data parsed from directory file entry.
        private byte mStorageType;
        private byte mNameLength;
        private readonly byte[] mRawFileName;
        private byte mFileType;
        private ushort mKeyPointer;
        private ushort mBlocksUsed;
        private uint mEof;
        private uint mCreateWhen;
        private ushort mLcFlags;
        private byte mAccess;
        private ushort mAuxType;
        private uint mModWhen;
        private ushort mHeaderPointer;

        // "Cooked" filename.
        private string mFileName;

        /// <summary>
        /// ProDOS storage type.  Determines the file structure.
        /// </summary>
        public ProDOS.StorageType StorageType {
            get { return (ProDOS.StorageType)mStorageType; }
            internal set {
                if (mStorageType != (byte)value) {
                    mStorageType = (byte)value;
                    IsDirentDirty = true;
                }
            }
        }

        /// <summary>
        /// Key block number.
        /// </summary>
        internal ushort KeyBlock {
            get { return mKeyPointer; }
            set {
                if (mKeyPointer != value) {
                    mKeyPointer = value;
                    IsDirentDirty = true;
                }
            }
        }

        /// <summary>
        /// Total number of blocks used by this file.
        /// </summary>
        /// <remarks>
        /// For an extended file, this is the sum of the blocks used for each fork, plus one.
        /// ProDOS stores the full value in the directory entry so that utilities don't have to
        /// go fishing for it.  This value is set and retrieved independently of the values
        /// in the extended info block, so when updating a forked file both entries must be
        /// updated.
        /// </remarks>
        internal ushort BlocksUsed {
            get {
                return mBlocksUsed;
            }
            set {
                Debug.Assert(value > 0 && value < FileSystem.TotalBlocks);
                if (mBlocksUsed != value) {
                    mBlocksUsed = value;
                    IsDirentDirty = true;
                }
            }
        }

        /// <summary>
        /// Fixes the directory entry "blocks used" field without setting the dirty flag.  If
        /// the directory entry is written for some other reason, the fixed value will be saved,
        /// but we don't push the change.
        /// </summary>
        internal void FixBlocksUsed(ushort value) {
            mBlocksUsed = value;
            // don't set dirty flag
        }

        /// <summary>
        /// EOF value stored in the directory entry.  For an extended file, this will be 512,
        /// representing only the length of the extended info block.
        /// </summary>
        internal uint EndOfFile {
            get { return mEof; }
            set {
                if (StorageType == ProDOS.StorageType.Extended) {
                    Debug.Assert(value == BLOCK_SIZE);
                } else {
                    Debug.Assert(value <= ProDOS.MAX_FILE_LEN);
                }
                if (mEof != value) {
                    mEof = value;
                    IsDirentDirty = true;
                }
            }
        }

        /// <summary>
        /// Pointer to directory entry block in containing dir, for easy update when file
        /// attributes change.  Can be altered by MoveFile().
        /// </summary>
        internal uint DirentBlockPtr { get; set; } = uint.MaxValue;

        /// <summary>
        /// Index to dirent in containing dir block (1-N, where N is 13 in normal volumes).
        /// </summary>
        internal int DirentIndex { get; set; } = -1;

        /// <summary>
        /// Length of dirent entries in the directory that contains this entry.  Combine with
        /// <see cref="DirentIndex"/> to get a byte offset within a block.
        /// </summary>
        internal int DirentEntryLength { get; set; }

        /// <summary>
        /// Updates fields when a file entry is moved to a new directory.
        /// </summary>
        internal void MoveEntry(ProDOS_FileEntry newContainingDir, uint direntBlockPtr,
                int direntIndex) {
            Debug.Assert(newContainingDir != ContainingDir);
            ContainingDir = newContainingDir;
            DirentBlockPtr = direntBlockPtr;
            DirentIndex = direntIndex;
            mHeaderPointer = newContainingDir.KeyBlock;
            IsDirentDirty = true;
        }

        #endregion IFileEntry / dirent


        #region Extended Files

        /// <summary>
        /// Optional extended info block, for forked files.
        /// </summary>
        /// <remarks>
        /// This is only used internally, so we don't need "check access" guards.
        /// </remarks>
        internal class ExtendedInfo {
            private const int FINDER_INFO_LEN = 18;     // size + type + 16 data bytes

            private byte mDataStorageType;
            private ushort mDataKeyBlock;
            private ushort mDataBlocksUsed;
            private uint mDataEof;

            private byte[] mFinderInfo = RawData.EMPTY_BYTE_ARRAY;

            private byte mRsrcStorageType;
            private ushort mRsrcKeyBlock;
            private ushort mRsrcBlocksUsed;
            private uint mRsrcEof;

            public ExtendedInfo() { }

            /// <summary>
            /// Parses the contents of the extended info block.
            /// </summary>
            /// <returns>True on success, false if the contents appear damaged.</returns>
            public bool ParseBlock(byte[] extBlk, int extOffset, ProDOS fs) {
                int offset = extOffset;
                mDataStorageType = RawData.ReadU8(extBlk, ref offset);
                mDataKeyBlock = RawData.ReadU16LE(extBlk, ref offset);
                mDataBlocksUsed = RawData.ReadU16LE(extBlk, ref offset);
                mDataEof = RawData.ReadU24LE(extBlk, ref offset);

                offset = extOffset + 256;
                mRsrcStorageType = RawData.ReadU8(extBlk, ref offset);
                mRsrcKeyBlock = RawData.ReadU16LE(extBlk, ref offset);
                mRsrcBlocksUsed = RawData.ReadU16LE(extBlk, ref offset);
                mRsrcEof = RawData.ReadU24LE(extBlk, ref offset);

                // Do a quick check on the storage type and key block.  This should be sufficient
                // to detect garbage blocks and prevent anything weird from happening.
                if (mDataStorageType != (byte)ProDOS.StorageType.Seedling &&
                        mDataStorageType != (byte)ProDOS.StorageType.Sapling &&
                        mDataStorageType != (byte)ProDOS.StorageType.Tree) {
                    fs.Notes.AddE("Invalid storage type on data fork ($" +
                        mDataStorageType.ToString("x2") + ")");
                    return false;
                }
                if (mRsrcStorageType != (byte)ProDOS.StorageType.Seedling &&
                        mRsrcStorageType != (byte)ProDOS.StorageType.Sapling &&
                        mRsrcStorageType != (byte)ProDOS.StorageType.Tree) {
                    fs.Notes.AddE("Invalid storage type on rsrc fork ($" +
                        mRsrcStorageType.ToString("x2") + ")");
                    return false;
                }
                if (mDataKeyBlock <= ProDOS.VOL_DIR_START_BLK || mDataKeyBlock >= fs.TotalBlocks) {
                    fs.Notes.AddE("Invalid key block on data fork (" + mDataKeyBlock + ")");
                }
                if (mRsrcKeyBlock <= ProDOS.VOL_DIR_START_BLK || mRsrcKeyBlock >= fs.TotalBlocks) {
                    fs.Notes.AddE("Invalid key block on data fork (" + mRsrcKeyBlock + ")");
                }

                // ProDOS TN #25 says there may be 1 or 2 18-byte fields here.  (For the file I/O
                // calls, the GS/OS option list drops the size/type bytes and concatenates
                // the data.)  If we find appropriate-looking data we copy the whole thing into
                // a byte buffer; otherwise we just ignore it all.
                if (extBlk[extOffset + 8] == FINDER_INFO_LEN) {
                    int len;
                    if (extBlk[extOffset + 8 + FINDER_INFO_LEN] == FINDER_INFO_LEN) {
                        len = FINDER_INFO_LEN * 2;      // Found both sets.
                    } else {
                        len = FINDER_INFO_LEN;          // Only one set.
                    }
                    mFinderInfo = new byte[len];
                    Array.Copy(extBlk, extOffset + 8, mFinderInfo, 0, len);
                }

                return true;
            }


            public bool IsDirty { get; set; }

            public byte[] FinderInfo { get { return mFinderInfo; } }

            public bool HasTypeInfo {
                get {
                    if (mFinderInfo.Length < FINDER_INFO_LEN) {
                        return false;
                    }
                    if (mFinderInfo[0] != FINDER_INFO_LEN || mFinderInfo[1] != 1) {
                        // Strictly speaking, if the total size is 36, we should check to see
                        // if the second entry holds FInfo (maybe they put FXInfo first).
                        // I don't think this actually happens.
                        return false;
                    }
                    return true;
                }
            }
            public uint HFSFileType {
                get {
                    if (HasTypeInfo) {
                        return RawData.GetU32BE(mFinderInfo, 2);
                    } else {
                        return 0;
                    }
                }
                set {
                    if (!HasTypeInfo) {
                        CreateTypeInfo();
                    }
                    RawData.SetU32BE(mFinderInfo, 2, value);
                    IsDirty = true;
                }
            }
            public uint HFSCreator {
                get {
                    if (HasTypeInfo) {
                        return RawData.GetU32BE(mFinderInfo, 6);
                    } else {
                        return 0;
                    }
                }
                set {
                    if (!HasTypeInfo) {
                        CreateTypeInfo();
                    }
                    RawData.SetU32BE(mFinderInfo, 6, value);
                    IsDirty = true;
                }
            }

            public byte DataStorageType {
                get { return mDataStorageType; }
                set {
                    mDataStorageType = value;
                    IsDirty = true;
                }
            }
            public byte RsrcStorageType {
                get { return mRsrcStorageType; }
                set {
                    mRsrcStorageType = value;
                    IsDirty = true;
                }
            }
            public ushort DataKeyBlock {
                get { return mDataKeyBlock; }
                set {
                    mDataKeyBlock = value;
                    IsDirty = true;
                }
            }
            public ushort RsrcKeyBlock {
                get { return mRsrcKeyBlock; }
                set {
                    mRsrcKeyBlock = value;
                    IsDirty = true;
                }
            }
            public ushort DataBlocksUsed {
                get { return mDataBlocksUsed; }
                set {
                    mDataBlocksUsed = value;
                    IsDirty = true;
                }
            }
            internal void FixDataBlocksUsed(ushort value) {
                mDataBlocksUsed = value;
                // don't set dirty flag
            }
            public ushort RsrcBlocksUsed {
                get { return mRsrcBlocksUsed; }
                set {
                    mRsrcBlocksUsed = value;
                    IsDirty = true;
                }
            }
            internal void FixRsrcBlocksUsed(ushort value) {
                mRsrcBlocksUsed = value;
                // don't set dirty flag
            }
            public uint DataEof {
                get { return mDataEof; }
                set {
                    mDataEof = value;
                    IsDirty = true;
                }
            }
            public uint RsrcEof {
                get { return mRsrcEof; }
                set {
                    mRsrcEof = value;
                    IsDirty = true;
                }
            }

            /// <summary>
            /// Replaces the current option list with one that matches what the HFS FST creates.
            /// </summary>
            /// <remarks>
            /// See ProDOS technical note #25.
            /// </remarks>
            private void CreateTypeInfo() {
                Debug.WriteLine("Adding HFS option list to extended info block");
                mFinderInfo = new byte[FINDER_INFO_LEN * 2];
                mFinderInfo[0] = mFinderInfo[FINDER_INFO_LEN] = FINDER_INFO_LEN;
                mFinderInfo[1] = 1;                     // FInfo
                mFinderInfo[FINDER_INFO_LEN + 1] = 2;   // FXInfo
            }
        }

        /// <summary>
        /// Extended info block.  Will be null if the file isn't forked.
        /// </summary>
        internal ExtendedInfo? ExtInfo { get; private set; } = null;

        #endregion Extended Files


        #region Directory Files

        /// <summary>
        /// For directories: length, in bytes, of each entry.
        /// </summary>
        /// <remarks>
        /// Extracted from header, necessary for walking through the contents of directory blocks.
        /// Usually $27.
        /// </remarks>
        public byte DirEntryLength {
            get { return mDirEntryLength; }
            internal set { mDirEntryLength = value; /*don't set dirty flag*/ }
        }
        private byte mDirEntryLength = 0;

        /// <summary>
        /// For directories: number of entries in each block.
        /// </summary>
        /// <remarks>
        /// Extracted from header.  Should be floor((512 - 4) / DirEntryLength), but it doesn't
        /// have to be.  Usually $0d.
        /// </remarks>
        public byte DirEntriesPerBlock {
            get { return mDirEntriesPerBlock; }
            internal set { mDirEntriesPerBlock = value; /*don't set dirty flag*/ }
        }
        private byte mDirEntriesPerBlock = 0;

        /// <summary>
        /// For directories: number of active files.  This initially comes from the dir header
        /// file_count field, and is corrected while scanning.
        /// </summary>
        public int DirFileCount {
            get { return mDirFileCount; }
            internal set { mDirFileCount = value; /*don't set dirty flag*/ }
        }
        private int mDirFileCount = -1;

        /// <summary>
        /// For directories: list of files in the directory, in order of appearance.
        /// Accessing this causes a directory scan on first use.
        /// </summary>
        /// <remarks>
        /// Eliminating this in favor of a simple enumerator that walks the raw directory contents
        /// is a little awkward because we want to ensure that there is exactly one IFileEntry
        /// object per file entry.  If we break that rule, we could have file attribute
        /// modifications pending in more than one place.
        /// </remarks>
        /// <exception cref="IOException">Disk access failure.</exception>
        internal List<IFileEntry> ChildList {
            get {
                if (IsDirectory && !IsScanned) {
                    ScanDir(false);
                }
                return mChildList;
            }
        }

        private List<IFileEntry> mChildList = new List<IFileEntry>();

        /// <summary>
        /// Has this directory been scanned?  Set to true for newly-created (and therefore empty)
        /// directories.
        /// </summary>
        internal bool IsScanned { get; set; }

        #endregion Directory Files

        /// <summary>
        /// Reference to filesystem object.
        /// </summary>
        public ProDOS FileSystem { get; private set; }

        /// <summary>
        /// True if this is the "fake" volume directory entry.
        /// </summary>
        public bool IsVolumeDirectory {
            get { return IsDirectory && ContainingDir == IFileEntry.NO_ENTRY; }
        }

        /// <summary>
        /// True if this is a regular file.  False for extended files, directories, and
        /// Pascal volumes.
        /// </summary>
        public bool IsRegularFile {
            get {
                return StorageType == ProDOS.StorageType.Seedling ||
                       StorageType == ProDOS.StorageType.Sapling ||
                       StorageType == ProDOS.StorageType.Tree;
            }
        }

        /// <summary>
        /// True if there are outstanding changes to be written.
        /// </summary>
        internal bool IsChangePending {
            get { return IsDirentDirty || (ExtInfo != null && ExtInfo.IsDirty); }
        }

        /// <summary>
        /// True if junk was found in an index block.  Extending the EOF without clearing the
        /// blocks could cause problems.
        /// </summary>
        internal bool HasJunk { get; set; }

        /// <summary>
        /// True if changes have been made that altered the directory entry.
        /// </summary>
        private bool IsDirentDirty { get; set; }
        //private bool IsDirentDirty {
        //    get { return mIsDirentDirty; }
        //    set { mIsDirentDirty = value; if (value == true) {
        //            Debug.WriteLine("ProDOS entry dirty"); } }
        //}
        //private bool mIsDirentDirty;

        /// <summary>
        /// True if we've detected (and Noted) a conflict with another file.
        /// </summary>
        private bool mHasConflict;

        // General-purpose temporary buffer, to reduce allocations.  Don't call other methods
        // while holding data here.
        private byte[] mTmpBlockBuf = new byte[BLOCK_SIZE];


        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="container">Filesystem object.</param>
        /// <param name="parent">Entry for directory that contains this file, or NO_ENTRY
        ///   for volume directory.</param>
        internal ProDOS_FileEntry(ProDOS container, IFileEntry parent) {
            FileSystem = container;
            ContainingDir = parent;

            mRawFileName = new byte[ProDOS.MAX_FILE_NAME_LEN];
            mFileName = string.Empty;
        }

        // IDisposable generic finalizer.
        ~ProDOS_FileEntry() {
            Dispose(false);
        }
        // IDisposable generic Dispose() implementation.
        public void Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        protected virtual void Dispose(bool disposing) {
            // The only reason for doing this is to ensure that we're flushing changes.  This
            // can happen if the application chooses not to call SaveChanges before closing
            // the filesystem or switching to raw mode.
            //
            // (IFileEntry is not IDisposable, so this is normally only called by the GC.)
            if (IsDirentDirty || (ExtInfo != null && ExtInfo.IsDirty)) {
                if (disposing) {
                    Debug.WriteLine("Disposing of entry with unsaved changes: " + this);
                    Debug.Assert(false);    // TODO: remove
                    SaveChanges();
                } else {
                    // Calling SaveChanges() would be risky.
                    Debug.Assert(false, "GC disposing of entry with unsaved changes: " + this);
                }
            }
        }

        /// <summary>
        /// Invalidates the file entry object.  This is called when the filesystem is switched
        /// to "raw" access mode, so that any objects retained by the application stop working.
        /// </summary>
        internal void Invalidate() {
            Debug.Assert(FileSystem != null); // harmless, but we shouldn't be invalidating twice
#pragma warning disable CS8625
            FileSystem = null;  // ensure that "is this part of our filesystem" check fails
#pragma warning restore CS8625
            ContainingDir = IFileEntry.NO_ENTRY;
            mChildList.Clear();
        }

        /// <summary>
        /// Creates a "fake" file entry for the volume directory.  Does not set the blocks_used
        /// or EOF fields, since those values cannot be obtained from the vol dir header.
        /// </summary>
        /// <param name="container">Filesystem object.</param>
        /// <param name="header">Volume directory header.</param>
        /// <returns>New ProDOS file entry.</returns>
        internal static ProDOS_FileEntry CreateVolDirEntry(ProDOS container,
                ProDOS.VolDirHeader header) {
            ProDOS_FileEntry newEntry = new ProDOS_FileEntry(container, IFileEntry.NO_ENTRY);
            newEntry.mStorageType = (byte)ProDOS.StorageType.Directory;
            newEntry.mNameLength = header.mNameLength;
            Array.Copy(header.mRawVolumeName, newEntry.mRawFileName, newEntry.mRawFileName.Length);
            newEntry.mFileType = FileAttribs.FILE_TYPE_DIR;
            newEntry.mKeyPointer = ProDOS.VOL_DIR_START_BLK;
            newEntry.mBlocksUsed = 0;   // value shouldn't matter
            newEntry.mEof = 0;
            newEntry.mCreateWhen = header.mCreateWhen;
            newEntry.mLcFlags = header.mLcFlags;
            newEntry.mAccess = header.mAccess;
            newEntry.mAuxType = 0;
            newEntry.mModWhen = header.mModWhen;
            newEntry.mHeaderPointer = 0;

            newEntry.mFileName = header.VolumeName;

            // Save directory header parameters.
            newEntry.DirEntryLength = header.mEntryLength;
            newEntry.DirEntriesPerBlock = header.mEntriesPerBlock;
            newEntry.DirFileCount = header.mFileCount;

            return newEntry;
        }

        /// <summary>
        /// Scans a directory.  If damage is found, such as a bad block or a bad pointer,
        /// the IsDamaged flag is raised and the scan terminates.
        /// </summary>
        /// <exception cref="IOException">Disk access failure.</exception>
        internal void ScanDir(bool doFullScan) {
            if (!doFullScan) {
                //Debug.WriteLine("Scanning dir " + FullPathName);
            }

            // Always set this.  Whether we succeed or fail, we only try once.
            Debug.Assert(IsDirectory && !IsScanned);
            Debug.Assert(mChildList.Count == 0);
            IsScanned = true;

            Notes notes = FileSystem.Notes;
            Debug.Assert(FileSystem.VolBitmap != null);
            VolumeUsage vu = FileSystem.VolBitmap.VolUsage;

            int entriesPerBlock = -1;       // read from dir header
            int entryLength = -1;           // read from dir header
            int activeEntries = 0;          // running total
            int numBlocks = 0;

            uint blockPtr = KeyBlock;
            uint prevBlock = 0;
            int iter = 0;

            while (blockPtr != 0) {
                if (!FileSystem.IsBlockValid(blockPtr)) {
                    FileSystem.Notes.AddE("Invalid block pointer in directory (" + blockPtr +
                        "): " + FullPathName);
                    // Can abort the whole thing or return what we have, but can't go further.
                    IsDamaged = true;
                    return;
                }

                // Read block.  This may throw if the disk has an error.
                byte[] dirData = new byte[BLOCK_SIZE];
                try {
                    FileSystem.ChunkAccess.ReadBlock(blockPtr, dirData, 0);
                } catch (BadBlockException) {
                    notes.AddE("Failed reading block " + blockPtr + " in " + FullPathName);
                    IsDamaged = true;
                    FileSystem.IsDubious = true;
                    vu.SetUsage(blockPtr, IFileEntry.NO_ENTRY);
                    return;
                }
                numBlocks++;

                ushort prev = RawData.GetU16LE(dirData, 0);
                ushort next = RawData.GetU16LE(dirData, 2);
                if (prev != prevBlock) {
                    FileSystem.Notes.AddW("Incorrect prev pointer in directory (expected " +
                        prevBlock + ", found " + ")");
                    // keep going
                }

                // Update volume usage map.
                if (doFullScan) {
                    vu.SetUsage(blockPtr, this);
                }

                int entryIndex = 1;         // BAP says start with 0, fixed in errata
                // Skip prev/next pointers.
                int offset = 4;

                //
                // The first entry in the first block is the directory header.  We need to
                // extract the entries-per-block and entry length values.
                //
                int blockEntryCount = entriesPerBlock;
                if (blockEntryCount == -1) {
                    int beforeOffset = offset;
                    if (IsVolumeDirectory) {
                        // We're walking the volume directory, so this is the volume dir header,
                        // which we've already parsed.  Just skip over the header bytes.
                        entriesPerBlock = DirEntriesPerBlock;
                        entryLength = DirEntryLength;
                    } else {
                        // Regular directory, parse and analyze the header.
                        DirHeader dirHeader = ExtractDirHeader(dirData, ref offset,
                            this, DirentBlockPtr, DirentIndex, DirentEntryLength,
                            out bool isDamaged, notes);
                        if (isDamaged) {
                            // If the header is severely damaged, we can't reliably parse
                            // the remaining fields, and there's a fair chance this isn't
                            // actually a directory.
                            IsDamaged = true;
                            return;
                        }

                        entriesPerBlock = dirHeader.mEntriesPerBlock;
                        entryLength = dirHeader.mEntryLength;
                    }
                    // Skip past header, and deduct it from the count for this block.
                    offset = beforeOffset + entryLength;
                    blockEntryCount = entriesPerBlock - 1;
                    entryIndex++;
                }

                //
                // Scan the actual entries out of the directory.
                //
                while (blockEntryCount > 0) {
                    int beforeOffset = offset;
                    if ((dirData[offset + 0x00] & 0xf0) == 0) {
                        // Storage type is zero, so this is a deleted or unused slot.  Ignore it.
                    } else {
                        ProDOS_FileEntry newEntry = new ProDOS_FileEntry(FileSystem, this);
                        if (!newEntry.ExtractFileEntry(blockPtr, entryIndex, entryLength, dirData,
                                ref offset)) {
                            throw new DAException("Internal failure");
                        }
                        activeEntries++;

                        if (newEntry.IsDamaged) {
                            // We have an entry that extracted badly.  It's possible the
                            // directory itself is damaged, but since the dir header parsed
                            // correctly maybe we just assume this single entry is corrupt.
                            //IsDamaged = true;
                        } else if (doFullScan && !newEntry.IsDirectory) {
                            // Do a "deep scan", opening files and examining their structure.
                            // The volume usage code will call back into us to report conflicts.
                            int dataBlocksUsed = 0, rsrcBlocksUsed = 0;

                            try {
                                using (ProDOS_FileDesc dataFd = ProDOS_FileDesc.CreateFD(newEntry,
                                        IFileSystem.FileAccessMode.ReadOnly,
                                        FilePart.DataFork, true)) {
                                    dataFd.SetVolumeUsage(out bool isDamaged);
                                    if (!isDamaged) {
                                        dataBlocksUsed = dataFd.CalcBlocksUsed();
                                    }
                                    // Notes should have been filed already.
                                    newEntry.IsDamaged |= isDamaged;
                                }
                            } catch {
                                newEntry.IsDamaged = true;
                            }

                            if (!newEntry.IsDamaged && newEntry.HasRsrcFork) {
                                try {
                                    using (ProDOS_FileDesc rsrcFd = ProDOS_FileDesc.CreateFD(newEntry,
                                            IFileSystem.FileAccessMode.ReadOnly,
                                            FilePart.RsrcFork, true)) {
                                        rsrcFd.SetVolumeUsage(out bool isDamaged);
                                        if (!isDamaged) {
                                            rsrcBlocksUsed = rsrcFd.CalcBlocksUsed();
                                        }
                                        // Notes should have been filed already.
                                        newEntry.IsDamaged |= isDamaged;
                                    }
                                } catch {
                                    newEntry.IsDamaged = true;
                                }
                            }

                            // Fix the various block_usage fields if they don't match our findings.
                            if (!newEntry.IsDamaged) {
                                FixBlockUsage(newEntry, dataBlocksUsed, rsrcBlocksUsed, notes);
                            }
                        }

                        // Record the exact location of the directory entry, so that we can
                        // update it later without having to scan around.
                        newEntry.DirentBlockPtr = blockPtr;
                        newEntry.DirentIndex = entryIndex;
                        newEntry.DirentEntryLength = entryLength;

                        mChildList.Add(newEntry);
                    }

                    // Adjust the offset by the full declared size, not just the known fields.
                    offset = beforeOffset + entryLength;
                    blockEntryCount--;
                    entryIndex++;
                }

                // Follow pointer to next directory block.
                prevBlock = blockPtr;
                blockPtr = next;

                // Make sure we're not looping forever.
                if (++iter > ProDOS.MAX_DIR_BLOCKS) {
                    FileSystem.Notes.AddE("Found circular directory link in " + FullPathName);
                    IsDamaged = true;
                    mChildList.Clear();     // don't allow traversal of damaged directory
                    return;
                }
            }

            // Verify some of the housekeeping.
            if (DirFileCount != activeEntries) {
                notes.AddW("Incorrect active entry count (" + DirFileCount +
                    " vs " + activeEntries + "): " + FullPathName);
                DirFileCount = activeEntries;
            }
            if (BlocksUsed != numBlocks) {
                // The volume dir value is always wrong because there's no parent dir entry,
                // so don't complain about it.
                if (!IsVolumeDirectory) {
                    notes.AddI("Incorrect storage size on directory (should be " +
                        numBlocks + ", found " + BlocksUsed + "): " +
                        FullPathName);
                }
                // Fix the blocks-used value.
                FixBlocksUsed((ushort)numBlocks);
            }

            // Check directory EOF.  The volume directory doesn't have a parent dir entry,
            // so there's nothing to compare against for that.  We fixed blocks_used
            // earlier, so a disagreement here would indicate a problem with the EOF.
            if (StorageSize != DataLength) {
                if (IsVolumeDirectory) {
                    Debug.Assert(!IsDirentDirty);
                    EndOfFile = (uint)numBlocks * BLOCK_SIZE;
                    IsDirentDirty = false;
                } else {
                    notes.AddW("Directory EOF does not match blocks used: " + FullPathName);
                }
            }
        }

        /// <summary>
        /// Fixes the blocks_used fields in the directory entry and extended info block, based
        /// on the values computed by opening the files.
        /// </summary>
        private void FixBlockUsage(ProDOS_FileEntry entry, int dataBlocksUsed,
                int rsrcBlocksUsed, Notes notes) {
            int totalBlocksUsed = dataBlocksUsed + rsrcBlocksUsed;
            if (entry.HasRsrcFork) {
                totalBlocksUsed++;      // add one for extended info block
            }

            // Validate the block usage count from the directory entry.  Update
            // the value in the file entry if it's wrong.
            if (entry.BlocksUsed != totalBlocksUsed) {
                notes.AddI("Incorrect blocks used on file (" + entry.BlocksUsed +
                    " vs actual " + totalBlocksUsed + "): " + entry.FullPathName);
                entry.FixBlocksUsed((ushort)totalBlocksUsed);
            }

            // Validate the block usage counts in the extended info block.
            if (entry.HasRsrcFork) {
                Debug.Assert(entry.ExtInfo != null);
                if (entry.ExtInfo.DataBlocksUsed != dataBlocksUsed) {
                    notes.AddI("Incorrect blocks used on data fork (" +
                        entry.ExtInfo.DataBlocksUsed + " vs actual " + dataBlocksUsed + "): " +
                        entry.FullPathName);
                    entry.ExtInfo.FixDataBlocksUsed((ushort)dataBlocksUsed);
                }
                if (entry.ExtInfo.RsrcBlocksUsed != rsrcBlocksUsed) {
                    notes.AddI("Incorrect blocks used on rsrc fork (" +
                        entry.ExtInfo.RsrcBlocksUsed + " vs actual " + rsrcBlocksUsed + "): " +
                        entry.FullPathName);
                    entry.ExtInfo.FixRsrcBlocksUsed((ushort)rsrcBlocksUsed);
                }
            }
        }

        /// <summary>
        /// Holder for data parsed from a directory header.  We already have a file entry for
        /// the directory file; this is a place to store the (mostly redundant) information
        /// from the header entry.
        /// </summary>
        internal class DirHeader {
            // Parsed data.
            public int mStorageType;
            public int mNameLength;
            public byte[] mRawFileName = new byte[ProDOS.MAX_FILE_NAME_LEN];
            public byte mReserved1;
            public ushort mReserved2;
            public ushort mReserved3;
            public uint mReserved4;
            public uint mCreateWhen;
            public byte mVersion;
            public byte mMinVersion;
            public byte mAccess;
            public byte mEntryLength;
            public byte mEntriesPerBlock;
            public ushort mFileCount;
            public ushort mParentPointer;
            public byte mParentEntry;
            public byte mParentEntryLength;

            public DirHeader() { }
        }

        /// <summary>
        /// Loads the contents of a directory header.  The contents are compared to those
        /// in the parent directory entry.
        /// </summary>
        /// <param name="data">Data buffer.</param>
        /// <param name="offset">Offset into data buffer where dir header starts.</param>
        /// <param name="dirEntry">File entry for this directory in parent directory.</param>
        /// <param name="dirEntryBlock">Block that holds file entry for this dir.</param>
        /// <param name="dirEntryIndex">Index into dir entry block.</param>
        /// <param name="isDamaged">Result: true if the header is too damaged to use.</param>
        internal static DirHeader ExtractDirHeader(byte[] data, ref int offset,
                ProDOS_FileEntry dirEntry, uint dirEntryBlock, int dirEntryIndex,
                int parentEntryLength, out bool isDamaged, Notes notes) {
            isDamaged = false;
            DirHeader dirHeader = new DirHeader();

            byte typeAndLen = RawData.ReadU8(data, ref offset);
            dirHeader.mStorageType = typeAndLen >> 4;
            dirHeader.mNameLength = typeAndLen & 0x0f;
            Array.Copy(data, offset, dirHeader.mRawFileName, 0, ProDOS.MAX_FILE_NAME_LEN);
            offset += ProDOS.MAX_FILE_NAME_LEN;
            dirHeader.mReserved1 = RawData.ReadU8(data, ref offset);
            dirHeader.mReserved2 = RawData.ReadU16LE(data, ref offset);
            dirHeader.mReserved3 = RawData.ReadU16LE(data, ref offset);
            dirHeader.mReserved4 = RawData.ReadU24LE(data, ref offset);
            dirHeader.mCreateWhen = RawData.ReadU32LE(data, ref offset);
            dirHeader.mVersion = RawData.ReadU8(data, ref offset);
            dirHeader.mMinVersion = RawData.ReadU8(data, ref offset);
            dirHeader.mAccess = RawData.ReadU8(data, ref offset);
            dirHeader.mEntryLength = RawData.ReadU8(data, ref offset);
            dirHeader.mEntriesPerBlock = RawData.ReadU8(data, ref offset);
            dirHeader.mFileCount = RawData.ReadU16LE(data, ref offset);
            dirHeader.mParentPointer = RawData.ReadU16LE(data, ref offset);
            dirHeader.mParentEntry = RawData.ReadU8(data, ref offset);
            dirHeader.mParentEntryLength = RawData.ReadU8(data, ref offset);

            // Check for irregularities and damage.  Nothing is considered fatal.
            if (dirHeader.mStorageType != (int)ProDOS.StorageType.SubdirHeader) {
                notes.AddE("Unexpected storage type in directory header");
                isDamaged = true;
            }
            if (dirHeader.mEntryLength < ProDOS.EXPECT_DIR_ENTRY_LENGTH) {
                // We won't find all of the fields we expect.
                notes.AddE("Dir entry len is too short (" + dirHeader.mEntryLength + ")");
                isDamaged = true;
            }
            if (dirHeader.mNameLength == 0) {
                notes.AddE("File name length must not be zero");
                isDamaged = true;
            }
            byte first = dirHeader.mRawFileName[0];
            if (first < 'A' || first > 'Z') {
                notes.AddE("First letter of file name must be a letter");
            }

            if (dirHeader.mReserved1 != 0x75 && dirHeader.mReserved1 != 0x76) {
                // BAP says "must contain $75", apparently GS/OS incremented it.
                notes.AddI("Directory header reserved field does not contain $75/76: " +
                    dirEntry.FullPathName);
            }

            // Experiments show that min_version is always zero, but the version field varies:
            // - ProDOS 1.1.1 uses $00
            // - ProDOS 2.0.3 uses $23
            // - GS/OS FST uses $05
            // This does not hold filename LC flags.
            if (dirHeader.mMinVersion != 0) {
                notes.AddI("Directory header has nonzero MIN_VERSION (" + dirHeader.mMinVersion +
                    "): " + dirEntry.FullPathName);
            }

            //
            // See if header entries match parent directory entry.  These are generally
            // non-fatal.  While some programs might be confused, we don't rely on the
            // values, so at worst we will trample something deliberately nonstandard.
            //

            // ProDOS keeps the filename string in sync, but doesn't use the version
            // field to hold LC flags here.
            if (dirEntry.mNameLength != dirHeader.mNameLength ||
                !RawData.CompareBytes(dirEntry.mRawFileName, 0, dirHeader.mRawFileName, 0,
                    dirEntry.mNameLength)) {
                notes.AddW("Directory header filename does not match entry: " +
                    dirEntry.FullPathName);
            }

            // ProDOS doesn't appear to update the access or create_date fields
            // in the directory header on SET_FILE_INFO calls.  I think they're
            // both initially correct, but the header always has access=$c3.
            // In any event, it doesn't appear to matter if they're wrong.  No
            // complaints from Mr. Fixit.
            if (false && dirHeader.mAccess != dirEntry.Access) {
                // It looks like ProDOS sets the header to $c3 and doesn't update it
                // when the directory is locked / unlocked.  Since the "backup needed"
                // bit isn't set, the fields will almost always differ.
                notes.AddI("Directory header access flags don't match ($" +
                    dirHeader.mAccess.ToString("x2") + " vs $" +
                    dirEntry.Access.ToString("x2") + "): " +
                    dirEntry.FullPathName);
            }
            if (false &&
                  TimeStamp.ConvertDateTime_ProDOS(dirHeader.mCreateWhen) != dirEntry.CreateWhen) {
                notes.AddI("Directory header creation date doesn't match (" +
                    TimeStamp.ConvertDateTime_ProDOS(dirHeader.mCreateWhen) + " vs " +
                    dirEntry.CreateWhen + "): " +
                    dirEntry.FullPathName);
            }

            if (dirHeader.mParentEntry != dirEntryIndex) {
                notes.AddI("PARENT_ENTRY in dir entry header is incorrect (" +
                    dirHeader.mParentEntry + " vs. " + dirEntryIndex + ": " + dirEntry);
            }

            if (dirHeader.mEntriesPerBlock < 1 ||
                    dirHeader.mEntriesPerBlock > (BLOCK_SIZE - 4) / dirHeader.mEntryLength) {
                // Bad math.
                notes.AddE("Entries per block is bad (" + dirHeader.mEntriesPerBlock + ")");
                isDamaged = true;
            }

            // Confirm that the parent pointer actually points to our parent, and has the
            // correct entry length. Not sure if these should be warnings or errors.
            if (dirHeader.mParentPointer != dirEntryBlock) {
                notes.AddW("Incorrect parent pointer in directory header (" +
                    dirHeader.mParentPointer + " vs. " + dirEntryBlock + "): " +
                    dirEntry.FullPathName);
            }
            if (dirHeader.mParentEntry != dirEntryIndex) {
                notes.AddW("Incorrect parent dir index in directory header (" +
                    dirHeader.mParentEntry + " vs. " + dirEntryIndex + "): " +
                    dirEntry.FullPathName);
            }
            if (dirHeader.mParentEntryLength != parentEntryLength) {
                notes.AddW("Incorrect parent entry length in directory header (" +
                    dirHeader.mParentEntryLength + " vs. " + parentEntryLength + "): " +
                    dirEntry.FullPathName);
            }

            // Set the file count value from the header.  We can double-check this while
            // scanning the directory contents.
            dirEntry.DirFileCount = dirHeader.mFileCount;

            // Save the dir entry parameters, for code that wants to parse the directory contents.
            dirEntry.DirEntryLength = dirHeader.mEntryLength;
            dirEntry.DirEntriesPerBlock = dirHeader.mEntriesPerBlock;

            return dirHeader;
        }

        /// <summary>
        /// Extracts a file entry from a directory block.
        /// </summary>
        /// <remarks>
        /// For efficiency, this does NOT open directory files to scan their headers.
        /// The caller must set the DirEntryLength, DirEntriesPerBlock, and DirFileCount
        /// properties for directory files.
        /// </remarks>
        /// <param name="dirBlockNum">Directory block number, so we can find
        ///   it quickly when we need to update the directory entry.</param>
        /// <param name="dirEntryIndex">Offset of entry in directory block, so we can find
        ///   it quickly when we need to update the directory entry.</param>
        /// <param name="dirEntryLength">Length of a directory entry.</param>
        /// <param name="data">Byte buffer with directory data.</param>
        /// <param name="offset">Offset to start of directory entry.</param>
        /// <returns>True on success, false if the slot appears to be empty or deleted.</returns>
        internal bool ExtractFileEntry(uint dirBlockNum, int dirEntryIndex, int dirEntryLength,
                byte[] data, ref int offset) {
            // Parse contents.
            byte typeAndLen = RawData.ReadU8(data, ref offset);
            mStorageType = (byte)(typeAndLen >> 4);
            mNameLength = (byte)(typeAndLen & 0x0f);
            Array.Copy(data, offset, mRawFileName, 0, ProDOS.MAX_FILE_NAME_LEN);
            offset += ProDOS.MAX_FILE_NAME_LEN;
            mFileType = RawData.ReadU8(data, ref offset);
            mKeyPointer = RawData.ReadU16LE(data, ref offset);
            mBlocksUsed = RawData.ReadU16LE(data, ref offset);
            mEof = RawData.ReadU24LE(data, ref offset);
            mCreateWhen = RawData.ReadU32LE(data, ref offset);
            mLcFlags = RawData.ReadU16LE(data, ref offset);
            mAccess = RawData.ReadU8(data, ref offset);
            mAuxType = RawData.ReadU16LE(data, ref offset);
            mModWhen = RawData.ReadU32LE(data, ref offset);
            mHeaderPointer = RawData.ReadU16LE(data, ref offset);

            // Check for irregularities and damage.
            switch ((ProDOS.StorageType)mStorageType) {
                case ProDOS.StorageType.Deleted:
                    // We could try to recover these, but stepping around probably-bad data
                    // is too much effort.  An "undelete" function should be implemented as
                    // a completely separate function.
                    return false;
                case ProDOS.StorageType.Seedling:
                case ProDOS.StorageType.Sapling:
                case ProDOS.StorageType.Tree:
                case ProDOS.StorageType.PascalVolume:
                case ProDOS.StorageType.Extended:
                case ProDOS.StorageType.Directory:
                    // all good
                    break;
                case ProDOS.StorageType.SubdirHeader:
                case ProDOS.StorageType.VolDirHeader:
                default:
                    // Should not see these on file entries.
                    FileSystem.Notes.AddE("Invalid storage type found");
                    IsDamaged = true;
                    break;
            }

            // Validate and convert filename.
            if (mNameLength == 0) {
                FileSystem.Notes.AddE("File name length must not be zero");
                IsDamaged = true;
                // leave FileName empty
            } else {
                bool isNameValid;
                mFileName = GenerateMixedCaseName(mRawFileName,
                    mNameLength, mLcFlags, out isNameValid);
                if (!isNameValid) {
                    FileSystem.Notes.AddW("Filename uses illegal chars: '" + mFileName + "'");
                    // Don't really need to set dubious/damaged here, since it doesn't affect
                    // the structure of the file or disk image.  Some disks put spaces and/or
                    // lower-case letters in ProDOS directories.
                }
                //Debug.WriteLine("Filename is " + newEntry.FileName);
            }

            // Check key block.
            if (!FileSystem.IsBlockValid(mKeyPointer)) {
                FileSystem.Notes.AddE("Invalid key block (" + mKeyPointer + "): " +
                    FullPathName);
                IsDamaged = true;
            }

            // Verify header pointer.
            if (mHeaderPointer != ((ProDOS_FileEntry)ContainingDir).KeyBlock) {
                FileSystem.Notes.AddW("File entry header pointer mismatch");
                // not fatal
            }

            if (IsDirectory && mFileType != FileAttribs.FILE_TYPE_DIR) {
                FileSystem.Notes.AddW("Directory has non-DIR file type");
                // not fatal; any value in checking the auxtype?
            }

            // Handle forked files.  The directory entry EOF will be 512, representing the
            // extended key block.
            if (StorageType == ProDOS.StorageType.Extended) {
                try {
                    FileSystem.ChunkAccess.ReadBlock(KeyBlock, mTmpBlockBuf, 0);
                    ExtInfo = new ExtendedInfo();
                    if (!ExtInfo.ParseBlock(mTmpBlockBuf, 0, FileSystem)) {
                        FileSystem.Notes.AddE("Extended info appears damaged: " + FullPathName);
                        IsDamaged = true;
                    }
                } catch (IOException) {
                    FileSystem.Notes.AddE("Unable to read extended info block: " + FullPathName);
                    IsDamaged = true;
                }
            }

            DirentBlockPtr = dirBlockNum;
            DirentIndex = dirEntryIndex;
            DirentEntryLength = dirEntryLength;

            // Clear any dirty flags set by properties.
            IsDirentDirty = false;
            if (ExtInfo != null) {
                ExtInfo.IsDirty = false;
            }
            return true;
        }

        // IFileEntryExt
        public void AddConflict(uint chunk, IFileEntry entry) {
            if (!mHasConflict) {
                // Only report when the dubious flag isn't set, so that we don't flood the Notes.
                // This means we won't dump a full report if there are multiple files overlapping
                // in multiple places, but we will mention each problematic file at least once.
                string name = (entry == IFileEntry.NO_ENTRY) ?
                    VolumeUsage.SYSTEM_STR : entry.FullPathName;
                FileSystem.Notes.AddW(FullPathName + " overlaps with " + name);
            }
            mHasConflict = true;
        }

        // Work buffers for SaveChanges(), allocated on first use.
        private byte[]? mBeforeBuf;
        private byte[]? mEditBuf;

        // IFileEntry
        public void SaveChanges() {
            if (!IsValid) {
                throw new DAException("Cannot save changes on invalid file entry object");
            }
            if (!IsDirentDirty && !(HasRsrcFork && ExtInfo != null && ExtInfo.IsDirty)) {
                return;
            }
            Debug.Assert(!FileSystem.IsReadOnly && !IsDubious && !IsDamaged);

            if (mBeforeBuf == null) {
                mBeforeBuf = new byte[BLOCK_SIZE];
                mEditBuf = new byte[BLOCK_SIZE];
            } else {
                Debug.Assert(mEditBuf != null);
            }

            if (IsDirentDirty && IsVolumeDirectory) {
                // Update volume directory.
                // Only a subset of values may be changed: filename (with lower-case flags),
                // access flags, and create date.  It looks like GS/OS stores the modification
                // date as well, in the reserved area.  All values are stored in the directory
                // header in block 2.
                byte[] data = mBeforeBuf;
                FileSystem.ChunkAccess.ReadBlock(ProDOS.VOL_DIR_START_BLK, data, 0);

                data[0x04] = (byte)(((int)ProDOS.StorageType.VolDirHeader << 4) | mNameLength);
                Array.Copy(mRawFileName, 0, data, 0x05, ProDOS.MAX_FILE_NAME_LEN);
                RawData.SetU32LE(data, 0x16, mModWhen);
                RawData.SetU16LE(data, 0x1a, mLcFlags);
                RawData.SetU32LE(data, 0x1c, mCreateWhen);
                data[0x22] = mAccess;

                FileSystem.ChunkAccess.WriteBlock(ProDOS.VOL_DIR_START_BLK, data, 0);
                IsDirentDirty = false;
                return;
            }

            if (IsDirentDirty) {
                // Read the directory block, and make a copy we can compare against.
                FileSystem.ChunkAccess.ReadBlock(DirentBlockPtr, mBeforeBuf, 0);
                Array.Copy(mBeforeBuf, mEditBuf, BLOCK_SIZE);

                int direntOffset = 4 + (DirentIndex - 1) * DirentEntryLength;
                Debug.Assert(direntOffset > 0 && direntOffset <= BLOCK_SIZE - DirentEntryLength);
                int offset = direntOffset;
                RawData.WriteU8(mEditBuf, ref offset, (byte)(mStorageType << 4 | mNameLength));
                Array.Copy(mRawFileName, 0, mEditBuf, offset, ProDOS.MAX_FILE_NAME_LEN);
                offset += ProDOS.MAX_FILE_NAME_LEN;
                RawData.WriteU8(mEditBuf, ref offset, mFileType);
                RawData.WriteU16LE(mEditBuf, ref offset, mKeyPointer);
                RawData.WriteU16LE(mEditBuf, ref offset, mBlocksUsed);
                RawData.WriteU24LE(mEditBuf, ref offset, mEof);
                RawData.WriteU32LE(mEditBuf, ref offset, mCreateWhen);
                RawData.WriteU16LE(mEditBuf, ref offset, mLcFlags);
                RawData.WriteU8(mEditBuf, ref offset, mAccess);
                RawData.WriteU16LE(mEditBuf, ref offset, mAuxType);
                RawData.WriteU32LE(mEditBuf, ref offset, mModWhen);
                RawData.WriteU16LE(mEditBuf, ref offset, mHeaderPointer);
                Debug.Assert(offset == direntOffset + ProDOS.EXPECT_DIR_ENTRY_LENGTH);

                // See if we changed anything.  If not, skip the write.  (This is a little
                // slower but much simpler than having "if new value is different" tests in
                // every setter, and correctly handles the case where a value is changed
                // then restored to its original value.)
                if (!RawData.CompareBytes(mBeforeBuf, mEditBuf, mEditBuf.Length)) {
                    FileSystem.ChunkAccess.WriteBlock(DirentBlockPtr, mEditBuf, 0);
                    //Debug.WriteLine("Wrote dirent");
                }
                if (IsDirectory) {
                    // If the filename of a directory changed, we also need to update it in
                    // the dir header.  Compare the storage type / name length byte as well
                    // (the storage type of a directory will never change).
                    if (!RawData.CompareBytes(mBeforeBuf, direntOffset,
                            mEditBuf, direntOffset, ProDOS.MAX_FILE_NAME_LEN + 1)) {

                        // Get the first block in the directory file.
                        byte[] modData = new byte[BLOCK_SIZE];
                        FileSystem.ChunkAccess.ReadBlock(KeyBlock, modData, 0);

                        int storageType = modData[4] & 0xf0;

                        // Copy the new name and length byte over, fix the header storage
                        // type, and write the block back.
                        Array.Copy(mEditBuf, direntOffset, modData, 4,
                            ProDOS.MAX_FILE_NAME_LEN + 1);
                        modData[4] = (byte)((modData[4] & 0x0f) | storageType);

                        FileSystem.ChunkAccess.WriteBlock(KeyBlock, modData, 0);
                    }
                }
                IsDirentDirty = false;
            }

            // If something in the extended info block changed, such as the data fork EOF
            // or HFS file type, update that.  This is used when adding a resource fork to
            // an existing file, so we want to write all fields.
            if (HasRsrcFork && ExtInfo != null && ExtInfo.IsDirty) {
                // Read the extended info block, and make a copy we can modify.
                FileSystem.ChunkAccess.ReadBlock(mKeyPointer, mBeforeBuf, 0);
                Array.Copy(mBeforeBuf, mEditBuf, BLOCK_SIZE);

                int offset = 0;
                RawData.WriteU8(mEditBuf, ref offset, ExtInfo.DataStorageType);
                RawData.WriteU16LE(mEditBuf, ref offset, ExtInfo.DataKeyBlock);
                RawData.WriteU16LE(mEditBuf, ref offset, ExtInfo.DataBlocksUsed);
                RawData.WriteU24LE(mEditBuf, ref offset, ExtInfo.DataEof);

                // If we have Finder info, copy that over.
                if (ExtInfo.FinderInfo.Length > 0) {
                    Array.Copy(ExtInfo.FinderInfo, 0, mEditBuf, 8,
                        ExtInfo.FinderInfo.Length);
                }

                offset = 0x0100;
                RawData.WriteU8(mEditBuf, ref offset, ExtInfo.RsrcStorageType);
                RawData.WriteU16LE(mEditBuf, ref offset, ExtInfo.RsrcKeyBlock);
                RawData.WriteU16LE(mEditBuf, ref offset, ExtInfo.RsrcBlocksUsed);
                RawData.WriteU24LE(mEditBuf, ref offset, ExtInfo.RsrcEof);

                // Write the new block if anything changed.
                if (!RawData.CompareBytes(mBeforeBuf, mEditBuf, mEditBuf.Length)) {
                    FileSystem.ChunkAccess.WriteBlock(mKeyPointer, mEditBuf, 0);
                    //Debug.WriteLine("Wrote ext block");
                }
                ExtInfo.IsDirty = false;
            }
        }

        /// <summary>
        /// Updates the file_count member of a volume directory or subdirectory header.
        /// </summary>
        /// <remarks>
        /// <para>The directory header, found in the first block of a directory, holds the number
        /// of "active" file entries in the subdirectory file.</para>
        /// <para>If we allow direct writes to directory files, we will need to ensure that this is
        /// only called while the file is not open.</para>
        /// </remarks>
        /// <param name="newVal">Adjustment value (usually -1/+1).</param>
        /// <exception cref="IOException">I/O failure.</exception>
        internal void UpdateDirFileCount(int adjust) {
            //Debug.Assert(!FileSystem.IsFileOpen(this));
            Debug.Assert(DirFileCount >= 0);

            if (DirFileCount + adjust != ((ushort)(DirFileCount + adjust))) {
                // Over/underflow should be impossible, since we corrected the count
                // with the actual value when scanning the volume.
                throw new DAException("Invalid active file count adjustment: active=" +
                    DirFileCount + " adj=" + adjust);
            }

            // Get the first directory block.
            byte[] modData = mTmpBlockBuf;
            FileSystem.ChunkAccess.ReadBlock(KeyBlock, modData, 0);

            // Set the new value and write it back.  We want to use our copy, not the value
            // from the header, because the header value might have been wrong.
            ushort curCount = RawData.GetU16LE(modData, 0x25);
            if (curCount != DirFileCount) {
                Debug.WriteLine("Fixing incorrect dir header file_count value (was " +
                    curCount + ", should have been " + DirFileCount + ")");
            }
            ushort activeFiles = (ushort)(DirFileCount + adjust);
            //Debug.WriteLine("Active file count now " + activeFiles);
            RawData.SetU16LE(modData, 0x25, activeFiles);
            FileSystem.ChunkAccess.WriteBlock(KeyBlock, modData, 0);

            DirFileCount = activeFiles;
        }

        /// <summary>
        /// Adds a resource fork to the file.
        /// </summary>
        /// <exception cref="DiskFullException">Disk full.</exception>
        /// <exception cref="IOException">Disk access failure.</exception>
        internal void AddRsrcFork() {
            Debug.Assert(StorageType != ProDOS.StorageType.Extended);
            Debug.Assert(StorageType != ProDOS.StorageType.Directory);

            ProDOS_VolBitmap? vb = FileSystem.VolBitmap;
            Debug.Assert(vb != null);
            vb.BeginUpdate();
            try {
                // Do these first, to cause an early "disk full".
                uint extPtr = vb.AllocBlock(this);          // extended info block
                uint rsrcKeyPtr = vb.AllocBlock(this);      // seedling key block

                // Paranoia: write zeroed-out data to the extended info block.  If the block
                // is bad, better that it should fail now before we've updated the file attribs.
                FileSystem.ChunkAccess.WriteBlock(extPtr, ProDOS_FileDesc.sZeroBlock, 0);

                // Set storage type to "unlock" extended info fields.
                ProDOS.StorageType dataForkStorage = StorageType;
                StorageType = ProDOS.StorageType.Extended;

                ExtendedInfo newInfo = new ExtendedInfo();

                // Copy current type/key/used/eof to extended info block.
                newInfo.DataStorageType = (byte)dataForkStorage;
                newInfo.DataKeyBlock = KeyBlock;
                newInfo.DataBlocksUsed = BlocksUsed;
                newInfo.DataEof = EndOfFile;

                // Init resource fork.
                newInfo.RsrcStorageType = (byte)ProDOS.StorageType.Seedling;
                newInfo.RsrcKeyBlock = (ushort)rsrcKeyPtr;
                newInfo.RsrcBlocksUsed = 1;
                newInfo.RsrcEof = 0;

                // Convert main attributes.
                KeyBlock = (ushort)extPtr;
                EndOfFile = BLOCK_SIZE;     // now covers ext info block only
                BlocksUsed = (ushort)(newInfo.DataBlocksUsed + newInfo.RsrcBlocksUsed + 1);

                ExtInfo = newInfo;

                // Save changes.  We've read and/or written every block involved already, so
                // unless the media is actively degrading we should never get here.
                Debug.Assert(IsDirentDirty && ExtInfo.IsDirty);
                try {
                    SaveChanges();
                } catch (Exception) {
                    // If the dirty flag is still set, then we apparently failed without writing
                    // the new directory entry, so we should undo those changes.
                    if (IsDirentDirty) {
                        StorageType = (ProDOS.StorageType)newInfo.DataStorageType;
                        KeyBlock = newInfo.DataKeyBlock;
                        EndOfFile = newInfo.DataEof;
                        BlocksUsed = newInfo.DataBlocksUsed;
                        ExtInfo = null;
                    }
                    throw;
                }

                vb.EndUpdate();
            } catch (Exception) {
                vb.AbortUpdate();
                throw;
            }
            vb.Flush();
        }

        #region Filenames

        // Regex pattern for filename validation.
        //
        // Both filenames and volume names are 1-15 characters, starting with a letter,
        // and may contain numbers and '.'.  We allow lower case (GS/OS extension).
        private const string FILE_NAME_PATTERN = @"^[A-Za-z]([A-Za-z0-9\.]{0,14})$";
        private static Regex sFileNameRegex = new Regex(FILE_NAME_PATTERN);

        /// <summary>
        /// Sets the various fields associated with the filename.  The actual rename, including
        /// updates to the redundant copy in the directory header, happens in SaveChanges().
        /// </summary>
        private void SetFileName(string fileName) {
            byte[]? rawName = GenerateRawName(fileName, out ushort lcFlags);
            if (rawName == null) {
                throw new ArgumentException("Invalid filename '" + fileName + "'");
            }
            if (ContainingDir != IFileEntry.NO_ENTRY) {
                // Not the volume dir.  Make sure we're not about to cause a duplicate.
                foreach (IFileEntry entry in ContainingDir) {
                    if (entry != this && entry.CompareFileName(fileName) == 0) {
                        throw new IOException("A file with that name already exists");
                    }
                }
            }

            Array.Copy(rawName, mRawFileName, mRawFileName.Length);
            mNameLength = (byte)fileName.Length;
            mLcFlags = lcFlags;
            mFileName = fileName;
        }

        // IFileEntry
        public int CompareFileName(string fileName) {
            return CompareFileNames(mFileName, fileName);
        }

        // IFileEntry
        public int CompareFileName(string fileName, char fileNameSeparator) {
            return CompareFileNames(mFileName, fileName);
            //return PathName.ComparePathNames(mFileName, ProDOS.SEP_CHAR, fileName,
            //    fileNameSeparator, PathName.CompareAlgorithm.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Compares two filenames, case insensitive.
        /// </summary>
        public static int CompareFileNames(string fileName1, string fileName2) {
            return string.Compare(fileName1, fileName2,
                    StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Generates a mixed-case file or volume name given a string and flags.
        /// </summary>
        /// <remarks>
        /// <para>ProDOS 8 v1.8 and later support lower-case filenames by repurposing the
        /// version and min_version fields.  AppleWorks has a similar feature that uses
        /// bits in the aux_type field, and additionally allows ' ' to be represented as
        /// a lower-case '.', but we don't handle that here.</para>
        /// <para>Some disks, like Beagle Bros' "Extra K", have a filename with high
        /// ASCII values.  We strip those here.</para>
        /// </remarks>
        /// <param name="rawName">Raw filename data (full field).</param>
        /// <param name="length">Length of filename, in bytes.</param>
        /// <param name="lcFlags">Lower case flags.</param>
        /// <param name="isValid">Result: true if the filename is valid.</param>
        /// <returns>Converted name.</returns>
        public static string GenerateMixedCaseName(byte[] rawName, int length, ushort lcFlags,
                out bool isValid) {
            Debug.Assert(rawName.Length == ProDOS.MAX_FILE_NAME_LEN);
            Debug.Assert(length > 0 && length <= ProDOS.MAX_FILE_NAME_LEN);

            char[] outName = new char[length];

            if ((lcFlags & 0x8000) != 0) {
                // Convert according to GS/OS tech note #8.
                for (int idx = 0; idx < length; idx++) {
                    lcFlags <<= 1;
                    byte rawVal = (byte)(rawName[idx] & 0x7f);
                    if (rawVal < 0x20 || rawVal == 0x7f) {
                        outName[idx] = '?';
                    } else if ((lcFlags & 0x8000) != 0) {
                        outName[idx] = NameCharToLower(rawVal);
                    } else {
                        outName[idx] = (char)rawVal;
                    }
                }
            } else {
                // just copy it
                for (int idx = 0; idx < length; idx++) {
                    byte rawVal = (byte)(rawName[idx] & 0x7f);
                    if (rawVal < 0x20 || rawVal == 0x7f) {
                        outName[idx] = '?';
                    } else {
                        outName[idx] = (char)(rawName[idx] & 0x7f);
                    }
                }
            }

            string result = new string(outName);

            // Validate the converted name string.
            isValid = IsFileNameValid(result);
            if (!isValid) {
                Debug.WriteLine("Found invalid chars in filename '" + result + "'");
            }
            return result;
        }

        private static char NameCharToLower(byte rawChar) {
            if (rawChar >= 'A' && rawChar <= 'Z') {
                return (char)(rawChar - 'A' + 'a');
            } else {
                if (!((rawChar >= '0' && rawChar <= '9') || rawChar == '.')) {
                    //Debug.WriteLine("Found invalid char 0x" + rawChar.ToString("x2") +
                    //    " in ProDOS filename");
                }
                return (char)rawChar;
            }
        }

        /// <summary>
        /// Returns true if the string is a valid ProDOS filename.
        /// </summary>
        public static bool IsFileNameValid(string fileName) {
            MatchCollection matches = sFileNameRegex.Matches(fileName);
            return (matches.Count == 1);
        }

        /// <summary>
        /// Returns true if the string is a valid ProDOS volume name.
        /// </summary>
        public static bool IsVolumeNameValid(string volName) {
            return IsFileNameValid(volName);
        }

        /// <summary>
        /// Generates the "raw" upper-case-only ProDOS filename and a set of lower-case flags
        /// from a mixed-case filename.  The filename is not otherwise transformed.
        /// </summary>
        /// <param name="fileName">ProDOS-compatible filename.</param>
        /// <param name="olcFlags">Result: GS/OS lower-case flags.</param>
        /// <returns>Raw filename, in a maximum-length byte buffer, or null if the conversion
        ///   failed.</returns>
        public static byte[]? GenerateRawName(string fileName, out ushort olcFlags) {
            if (!IsFileNameValid(fileName)) {
                olcFlags = 0;
                return null;
            }
            byte[] rawName = new byte[ProDOS.MAX_FILE_NAME_LEN];
            ushort lcFlags = 0;
            ushort bit = 0x8000;

            for (int i = 0; i < fileName.Length; i++) {
                char ch = fileName[i];
                if (ch >= 'a' && ch <= 'z') {
                    // Lower case, set bit.
                    lcFlags |= bit;
                    ch = (char)(ch - 0x20);     // convert to upper case
                } else if (ch == ' ') {
                    // AppleWorks format allows this, the GS/OS extensions to ProDOS don't.
                    // We can either silently convert space to '.', or reject it.
                    //ch = '.';
                    Debug.Assert(false);    // should have been rejected by regex
                } else if ((ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == '.') {
                    // Upper case, essentially.
                } else {
                    // Invalid character; should have been caught before now?
                    Debug.Assert(false);
                }
                bit >>= 1;
                rawName[i] = (byte)ch;
            }

            if (lcFlags == 0) {
                // Entirely upper case.  Output zero, rather than $8000, for the benefit of
                // pre-V1.8 ProDOS.
                olcFlags = 0;
            } else {
                // Shift right and set the high bit.
                olcFlags = (ushort)((lcFlags >> 1) | 0x8000);
            }

            return rawName;
        }

        /// <summary>
        /// Adjusts a filename to be compatible with this filesystem, removing invalid characters
        /// and shortening the name.
        /// </summary>
        /// <param name="fileName">Filename to adjust.</param>
        /// <returns>Adjusted filename.</returns>
        public static string AdjustFileName(string fileName) {
            if (string.IsNullOrEmpty(fileName)) {
                return "Q";
            }

            char[] chars = fileName.ToCharArray();
            // Convert the string to ASCII values, stripping diacritical marks, then replace
            // anything that isn't valid for ProDOS.
            ASCIIUtil.ReduceToASCII(chars, '.');
            for (int i = 0; i < chars.Length; i++) {
                char ch = chars[i];
                if ((ch < 'A' || ch > 'Z') && (ch < 'a' || ch > 'z') &&
                        (ch < '0' || ch > '9') && (ch != '.')) {
                    chars[i] = '.';
                }
            }

            // If it doesn't start with a letter, add one.
            string cleaned;
            if ((chars[0] < 'A' || chars[0] > 'Z') && (chars[0] < 'a' || chars[0] > 'z')) {
                cleaned = 'X' + new string(chars);
            } else {
                cleaned = new string(chars);
            }

            // Clamp to max length by removing characters from the middle.
            if (cleaned.Length > ProDOS.MAX_FILE_NAME_LEN) {
                int firstLen = ProDOS.MAX_FILE_NAME_LEN / 2;        // 7
                int lastLen = ProDOS.MAX_FILE_NAME_LEN - (firstLen + 2);
                cleaned = cleaned.Substring(0, firstLen) + ".." +
                    cleaned.Substring(fileName.Length - lastLen, lastLen);
                Debug.Assert(cleaned.Length == ProDOS.MAX_FILE_NAME_LEN);
            }
            return cleaned;
        }

        /// <summary>
        /// Adjusts a volume name to be compatible with this filesystem.
        /// </summary>
        /// <param name="volName">Volume name to adjust.</param>
        /// <returns>Adjusted volume name, or null if this filesystem doesn't support
        ///   volume names.</returns>
        public static string? AdjustVolumeName(string volName) {
            return AdjustFileName(volName);
        }

        #endregion Filenames

        public override string ToString() {
            return "[ProDOS file entry: '" + mFileName + "' isDir=" + IsDirectory +
                (IsVolumeDirectory ? " (vol)" : "") + "]";
        }
    }
}
