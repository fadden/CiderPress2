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
using static DiskArc.FS.HFS_Struct;

namespace DiskArc.FS {
    /// <summary>
    /// HFS file entry.
    /// </summary>
    public class HFS_FileEntry : IFileEntryExt, IDisposable {
        #region IFileEntry

        //
        // IFileEntry interfaces.
        //

        public bool IsValid { get { return FileSystem != null; } }
        public bool IsDubious { get { return mHasConflict; } }
        public bool IsDamaged { get; private set; }

        public bool IsDirectory { get; private set; }

        public bool HasDataFork { get { return !IsDirectory; } }
        public bool HasRsrcFork { get { return !IsDirectory; } }
        public bool IsDiskImage => false;

        public IFileEntry ContainingDir { get; internal set; }

        public int Count { get { return mChildList.Count; } }

        public string FileName {
            get { return mFileName; }
            set {
                CheckChangeAllowed();
                // This requires inserting and deleting entries in the catalog tree, so we make
                // the change immediately rather than deferring to the next SaveChanges call.
                DoMoveFile(ContainingDir, value);
            }
        }
        public char DirectorySeparatorChar {
            get { return HFS.SEP_CHAR; }
            set { }
        }
        public byte[] RawFileName {
            get {
                int len = mRawFileNameStr[0];
                byte[] rawName = new byte[len];
                Array.Copy(mRawFileNameStr, 1, rawName, 0, len);
                return rawName;
            }
            set {
                CheckChangeAllowed();
                string fileName = MacChar.MacToUnicode(value, 0, value.Length,
                    MacChar.Encoding.RomanShowCtrl);
                DoMoveFile(ContainingDir, fileName);
            }
        }
        private string mFileName = NO_FILENAME;
        private byte[] mRawFileNameStr = NO_FILENAME_RAW;

        public string FullPathName {
            get {
                // The value is generated on each use, so that changes to parent directory
                // names and file moves are handled correctly.
                string pathName;
                if (string.IsNullOrEmpty(mFileName)) {
                    pathName = "!FileNameEmpty!";
                } else if (IsVolumeDirectory) {
                    pathName = HFS.SEP_CHAR + mFileName;
                } else {
                    Debug.Assert(ContainingDir != IFileEntry.NO_ENTRY);
                    if (((HFS_FileEntry)ContainingDir).IsVolumeDirectory) {
                        pathName = mFileName;
                    } else {
                        pathName = ContainingDir.FullPathName + HFS.SEP_CHAR + mFileName;
                    }
                }
                if (FileSystem == null) {
                    pathName = "!INVALID! was:" + pathName;
                }
                return pathName;
            }
        }

        public bool HasProDOSTypes { get { return false; } }
        public byte FileType { get { return 0; } set { } }
        public ushort AuxType { get { return 0; } set { } }

        public bool HasHFSTypes { get { return !IsDirectory; } }
        public uint HFSFileType {
            get { return mHFSFileType; }
            set {
                CheckChangeAllowed();
                if (!IsDirectory) {
                    mHFSFileType = value;
                    IsDirty = true;
                }
            }
        }
        public uint HFSCreator {
            get { return mHFSCreator; }
            set {
                CheckChangeAllowed();
                if (!IsDirectory) {
                    mHFSCreator = value;
                    IsDirty = true;
                }
            }
        }
        private uint mHFSFileType;
        private uint mHFSCreator;

        public byte Access {
            get { return mAccess; }
            set {
                CheckChangeAllowed();
                if (!IsDirectory) {
                    mAccess = ((value & (byte)AccessFlags.Write) != 0) ?
                        (byte)ACCESS_UNLOCKED : (byte)ACCESS_UNWRITABLE;
                    IsDirty = true;
                }
            }
        }
        private byte mAccess;

        public DateTime CreateWhen {
            get { return TimeStamp.ConvertDateTime_HFS(mCreateWhen); }
            set {
                CheckChangeAllowed();
                mCreateWhen = TimeStamp.ConvertDateTime_HFS(value);
                IsDirty = true;
            }
        }
        private uint mCreateWhen;

        public DateTime ModWhen {
            get { return TimeStamp.ConvertDateTime_HFS(mModWhen); }
            set {
                CheckChangeAllowed();
                mModWhen = TimeStamp.ConvertDateTime_HFS(value);
                IsDirty = true;
            }
        }
        private uint mModWhen;

        public long StorageSize {
            get {
                return DataPhyLength + RsrcPhyLength;
            }
        }

        public long DataLength { get; internal set; }

        public long RsrcLength { get; internal set; }

        public string Comment { get { return string.Empty; } set { } }

        public bool GetPartInfo(FilePart part, out long length, out long storageSize,
                out CompressionFormat format) {
            format = CompressionFormat.Uncompressed;
            if (part == FilePart.DataFork || part == FilePart.RawData) {
                length = DataLength;
                storageSize = DataPhyLength;
                return true;
            } else if (part == FilePart.RsrcFork) {
                length = RsrcLength;
                storageSize = RsrcPhyLength;
                return true;
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
            return ChildList.Values.GetEnumerator();    // Values is efficient in SortedList
        }
        IEnumerator IEnumerable.GetEnumerator() {
            if (!IsValid) {
                throw new DAException("Invalid file entry object");
            }
            return ChildList.Values.GetEnumerator();
        }

        /// <summary>
        /// Throws an exception if attribute changes are not allowed.
        /// </summary>
        private void CheckChangeAllowed() {
            if (FileSystem == null || FileSystem.IsReadOnly || IsDubious || IsDamaged) {
                throw new IOException("Changes not allowed");
            }
        }

        #endregion IFileEntry


        //
        // HFS-specific properties.
        //

        // Empty extent record; used for initialization.
        private static readonly ExtDataRec EMPTY_EXT_DATA_REC = new ExtDataRec();

        private const string NO_FILENAME = "!UNSET!";
        private static byte[] NO_FILENAME_RAW =
            MacChar.UnicodeToMacStr("!UNSET!", MacChar.Encoding.RomanShowCtrl);

        internal HFS FileSystem { get; private set; }

        public uint EntryCNID { get; private set; }         // from record
        internal uint ParentCNID { get; set; }              // from key; updated by MoveFile

        /// <summary>
        /// True if this is the volume (root) directory.
        /// </summary>
        internal bool IsVolumeDirectory { get { return EntryCNID == HFS.ROOT_CNID; } }

        /// <summary>
        /// Non-directory: physical length of data fork.
        /// </summary>
        internal uint DataPhyLength {
            get { return mDataPhyLength; }
            set {
                if (mDataPhyLength != value) {
                    mDataPhyLength = value;
                    IsDirty = true;
                }
            }
        }
        private uint mDataPhyLength;

        /// <summary>
        /// Non-directory: extent record for data fork.
        /// Note: get{}/set{} copy the object contents.
        /// </summary>
        internal ExtDataRec DataExtentRec {
            get { return new ExtDataRec(mDataExtentRec); }
            set {
                if (mDataExtentRec != value) {
                    mDataExtentRec.CopyFrom(value);
                    IsDirty = true;
                }
            }
        }
        private ExtDataRec mDataExtentRec = new ExtDataRec();

        /// <summary>
        /// Non-directory: physical length of resource fork.
        /// </summary>
        internal uint RsrcPhyLength {
            get { return mRsrcPhyLength; }
            set {
                if (mRsrcPhyLength != value) {
                    mRsrcPhyLength = value;
                    IsDirty = true;
                }
            }
        }
        private uint mRsrcPhyLength;

        /// <summary>
        /// Non-directory: extent record for resource fork.
        /// Note: get{}/set{} copy the object contents.
        /// </summary>
        internal ExtDataRec RsrcExtentRec {
            get { return new ExtDataRec(mRsrcExtentRec); }
            set {
                if (mRsrcExtentRec != value) {
                    mRsrcExtentRec.CopyFrom(value);
                    IsDirty = true;
                }
            }
        }
        private ExtDataRec mRsrcExtentRec = new ExtDataRec();

        internal byte[] RawFileNameStr { get { return mRawFileNameStr; } }

        /// <summary>
        /// Date/time of last backup.  Not accessible via IFileEntry.
        /// </summary>
        public DateTime BackupWhen {
            get { return TimeStamp.ConvertDateTime_HFS(mBackupWhen); }
            set {
                CheckChangeAllowed();
                mBackupWhen = TimeStamp.ConvertDateTime_HFS(value);
                IsDirty = true;
            }
        }
        private uint mBackupWhen;

        /// <summary>
        /// For directories: list of children.
        /// </summary>
        /// <remarks>
        /// <para>When reading the contents of a directory from disk, the contents are
        /// already sorted, because that's how they're stored in the B*-tree.  We use a
        /// sorted tree here to ensure that the list stays sorted when new files are added.</para>
        /// <para>SortedList is more efficient than SortedDictionary when the contents are
        /// added in sorted order.  Also, the Keys and Values properties are fast.</para>
        /// </remarks>
        internal SortedList<string, IFileEntry> ChildList {
            get {
                if (IsDirectory && !IsScanned) {
                    ScanDir();
                }
                return mChildList;
            }
        }
        private SortedList<string, IFileEntry> mChildList =
            new SortedList<string, IFileEntry>(new MacChar.HFSFileNameComparer());
        internal bool IsScanned { get; set; }

        /// <summary>
        /// True if we've detected (and Noted) a conflict with another file.
        /// </summary>
        private bool mHasConflict;

        /// <summary>
        /// True if there are pending changes to the catalog record.
        /// </summary>
        internal bool IsDirty { get; private set; }
        //internal bool IsDirty {
        //    get { return mIsDirty; }
        //    set { mIsDirty = value; if (value == true) {
        //            Debug.WriteLine("HFS entry dirty"); } }
        //}
        //private bool mIsDirty;


        /// <summary>
        /// Constructor.
        /// </summary>
        internal HFS_FileEntry(HFS container) {
            FileSystem = container;

            ContainingDir = IFileEntry.NO_ENTRY;
        }

        // IDisposable generic finalizer.
        ~HFS_FileEntry() {
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
            if (IsDirty) {
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
        /// Creates a new file entry object from a directory record.
        /// </summary>
        internal static HFS_FileEntry CreateDirEntry(HFS fileSystem, HFS_Record.CatKey dirKey,
                CatDataDirRec dirRec) {
            HFS_FileEntry newEntry = new HFS_FileEntry(fileSystem);

            newEntry.IsDirectory = true;
            newEntry.ParentCNID = dirKey.ParentID;
            newEntry.EntryCNID = dirRec.dirDirID;

            // Make a local copy of the filename, which starts with a length byte.
            int fileNameLen = dirKey.Data[dirKey.NameOffset];
            newEntry.mRawFileNameStr = new byte[fileNameLen + 1];
            Array.Copy(dirKey.Data, dirKey.NameOffset, newEntry.mRawFileNameStr, 0,
                fileNameLen + 1);

            newEntry.mFileName = MacChar.MacStrToUnicode(newEntry.mRawFileNameStr, 0,
                MacChar.Encoding.RomanShowCtrl);

            newEntry.mCreateWhen = dirRec.dirCrDat;
            newEntry.mModWhen = dirRec.dirMdDat;
            newEntry.mBackupWhen = dirRec.dirBkDat;
            newEntry.mAccess = (byte)ACCESS_UNLOCKED;

            newEntry.IsDirty = false;
            return newEntry;
        }

        /// <summary>
        /// Creates a new file entry object from a file record.
        /// </summary>
        /// <remarks>
        /// Does not set ContainingDir.
        /// </remarks>
        internal static HFS_FileEntry CreateFileEntry(HFS fileSystem, HFS_Record.CatKey fileKey,
                CatDataFilRec fileRec) {
            HFS_FileEntry newEntry = new HFS_FileEntry(fileSystem);

            newEntry.IsDirectory = false;
            newEntry.ParentCNID = fileKey.ParentID;
            newEntry.EntryCNID = fileRec.filFlNum;
            newEntry.DataLength = fileRec.filLgLen;
            newEntry.mDataPhyLength = fileRec.filPyLen;     // use member to bypass IsDirty
            newEntry.mDataExtentRec = fileRec.filExtRec;    // copied
            newEntry.RsrcLength = fileRec.filRLgLen;
            newEntry.mRsrcPhyLength = fileRec.filRPyLen;    // use member to bypass IsDirty
            newEntry.mRsrcExtentRec = fileRec.filRExtRec;   // copied
            newEntry.mHFSFileType = fileRec.filUsrWds.fdType;
            newEntry.mHFSCreator = fileRec.filUsrWds.fdCreator;
            newEntry.mCreateWhen = fileRec.filCrDat;
            newEntry.mModWhen = fileRec.filMdDat;
            newEntry.mBackupWhen = fileRec.filBkDat;
            if ((fileRec.filFlags & CatDataFilRec.FLAG_LOCKED) != 0) {
                // Keep all permissions except the "writable" flag.
                newEntry.mAccess = (byte)ACCESS_UNWRITABLE;
            } else {
                newEntry.mAccess = (byte)ACCESS_UNLOCKED;
            }

            // Make a local copy of the filename, which starts with a length byte.
            int fileNameLen = fileKey.Data[fileKey.NameOffset];
            newEntry.mRawFileNameStr = new byte[fileNameLen + 1];
            Array.Copy(fileKey.Data, fileKey.NameOffset, newEntry.mRawFileNameStr, 0,
                fileNameLen + 1);

            newEntry.mFileName = MacChar.MacStrToUnicode(newEntry.mRawFileNameStr, 0,
                MacChar.Encoding.RomanShowCtrl);

            Debug.Assert(!newEntry.IsDirty);
            return newEntry;
        }

        /// <summary>
        /// Scans the contents of a directory.  Only used when the full filesystem scan is
        /// disabled, so we're scanning directories on first reference.
        /// </summary>
        internal void ScanDir() {
            Debug.WriteLine("Scanning dir " + FullPathName);

            // Always set this.  Whether we succeed or fail, we only try once.
            Debug.Assert(IsDirectory && !IsScanned);
            Debug.Assert(mChildList.Count == 0);
            IsScanned = true;

            HFS_BTree catTree = FileSystem.VolMDB!.CatalogFile!;
            Notes notes = FileSystem.Notes;

            // Find the thread record for this directory.  Because the thread record has our
            // CNID and an empty filename, it comes immediately before all of the file records
            // for our children (which use our CNID as the first part of the key).
            HFS_Record.CatKey key = new HFS_Record.CatKey(EntryCNID);
            if (!catTree.FindRecord(key, out HFS_Node? node, out int recordNum)) {
                throw new IOException("Unable to find directory thread: " + FullPathName);
            }
            Debug.Assert(node != null);

            // Walk through the leaves.
            while (true) {
                recordNum++;
                if (recordNum == node.NumRecords) {
                    // Reached the end of the record.
                    if (node.FLink == 0) {
                        // Reached the end of the leaves.
                        break;
                    }
                    node = catTree.GetNode(node.FLink);
                    if (node.NumRecords == 0) {
                        // Throwing here, rather than flagging the issue, may be excessive.  But
                        // we're only here because they skipped the full filesystem scan, which
                        // is turning out to be a bad idea.
                        throw new IOException("Found an empty leaf node");
                    }
                    recordNum = 0;
                }

                // Get the record, and check the CNID in the key.
                HFS_Record rec = node.GetRecord(recordNum);
                HFS_Record.CatKey recKey = rec.UnpackCatKey();
                if (recKey.ParentID != EntryCNID) {
                    // This entry is not one of our children.
                    break;
                }

                HFS_Record.CatDataType recType = rec.GetCatDataType();
                HFS_FileEntry newEntry;
                if (recType == HFS_Record.CatDataType.Directory) {
                    HFS_Record.CatKey catKey = rec.UnpackCatKey();
                    CatDataDirRec dirRec = rec.ParseCatDataDirRec();
                    newEntry = CreateDirEntry(FileSystem, catKey, dirRec);
                } else if (recType == HFS_Record.CatDataType.File) {
                    HFS_Record.CatKey catKey = rec.UnpackCatKey();
                    CatDataFilRec fileRec = rec.ParseCatDataFilRec();
                    newEntry = CreateFileEntry(FileSystem, catKey, fileRec);
                } else {
                    // Ignore threads.
                    continue;
                }
                newEntry.ContainingDir = this;
                mChildList.Add(newEntry.FileName, newEntry);
            }
        }

        internal void SetVolumeUsage() {
            Debug.Assert(!IsDirectory);
            SetForkVolumeUsage(FileSystem, this, EntryCNID, HFS_Record.ForkType.Data,
                mDataExtentRec, DataPhyLength);
            SetForkVolumeUsage(FileSystem, this, EntryCNID, HFS_Record.ForkType.Rsrc,
                mRsrcExtentRec, RsrcPhyLength);
        }

        internal static void SetForkVolumeUsage(HFS fs, IFileEntry entry, uint cnid,
                HFS_Record.ForkType fork, ExtDataRec extRec, uint phyLength) {
            HFS_VolBitmap? vb = fs.VolBitmap;
            Debug.Assert(vb != null);
            VolumeUsage vu = vb.VolUsage;

            ushort ablkIndex = 0;
            SetExtRecUsage(vu, entry, extRec, ref ablkIndex);
            if (!CheckExtDataRec(fs, extRec)) {
                fs.Notes.AddE("Invalid extent: " + entry.FullPathName);
                ((HFS_FileEntry)entry).IsDamaged = true;
            }

            HFS_MDB? mdb = fs.VolMDB;
            Debug.Assert(mdb != null);
            Debug.Assert(mdb.ExtentsFile != null);
            while (ablkIndex * mdb.BlockSize < phyLength) {
                // Must be something in the extents overflow file.
                HFS_Record.ExtKey extKey = new HFS_Record.ExtKey(fork == HFS_Record.ForkType.Rsrc,
                    cnid, ablkIndex);
                bool found = mdb.ExtentsFile.FindRecord(extKey, out HFS_Node? node,
                    out int recordIndex);
                if (!found) {
                    Debug.Assert(false);
                    break;
                }
                Debug.Assert(node != null);
                ExtDataRec edr = node.GetRecord(recordIndex).ParseExtDataRec();
                if (!CheckExtDataRec(fs, edr)) {
                    fs.Notes.AddE("Invalid extent: " + entry.FullPathName);
                    ((HFS_FileEntry)entry).IsDamaged = true;
                }
                SetExtRecUsage(vu, entry, edr, ref ablkIndex);
            }
        }
        private static void SetExtRecUsage(VolumeUsage vu, IFileEntry entry,
                ExtDataRec extRec, ref ushort ablkIndex) {
            // Start with the record in the file.
            for (int i = 0; i < ExtDataRec.NUM_RECS; i++) {
                ushort start = extRec.Descs[i].xdrStABN;
                ushort count = extRec.Descs[i].xdrNumAblks;
                for (uint blk = start; blk < start + count; blk++) {
                    vu.SetUsage(blk, entry);
                }
                ablkIndex += count;
            }
        }

        /// <summary>
        /// Tests the validity of an extent record.
        /// </summary>
        private static bool CheckExtDataRec(HFS fs, ExtDataRec edr) {
            ushort maxAllocNum = fs.VolMDB!.TotalBlocks;
            bool foundEmpty = false;
            for (int i = 0; i < ExtDataRec.NUM_RECS; i++) {
                ExtDescriptor ed = edr.Descs[i];
                ushort start = ed.xdrStABN;
                ushort count = ed.xdrNumAblks;
                if (start >= maxAllocNum || count > maxAllocNum - start) {
                    // Note: test is wrong (>=) in fsck.hfs
                    Debug.WriteLine("Bad extent: start/count too large: " + edr);
                    return false;
                }
                if (start != 0 && count == 0) {
                    Debug.WriteLine("Bad extent: missing count: " + edr);
                    return false;
                }
                if (foundEmpty) {
                    if (count != 0) {
                        Debug.WriteLine("Bad extent: found data after empty desc: " + edr);
                        return false;
                    }
                }
                foundEmpty = (count == 0);
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

        // IFileEntry
        public void SaveChanges() {
            if (!IsValid) {
                //throw new DAException("Cannot save changes on invalid file entry object");
                Debug.Assert(false, "Cannot save changes in invalid file entry object");
                return;
            }
            if (!IsDirty) {
                return;
            }
            Debug.Assert(!FileSystem.IsReadOnly && !IsDubious && !IsDamaged);

            // Get the node with our catalog record.
            HFS_Record.CatKey key = new HFS_Record.CatKey(ParentCNID, mRawFileNameStr, 0);
            HFS_BTree catTree = FileSystem.VolMDB!.CatalogFile!;
            if (!catTree.FindRecord(key, out HFS_Node? node, out int recordIndex)) {
                // unexpected
                throw new IOException("Unable to find catalog record: " + FullPathName);
            }
            Debug.Assert(node != null);
            HFS_Record rec = node.GetRecord(recordIndex);

            if (IsDirectory) {
                // Get current data from disk, and copy our fields over.
                CatDataDirRec dirRec = rec.ParseCatDataDirRec();
                dirRec.dirCrDat = mCreateWhen;
                dirRec.dirMdDat = mModWhen;
                dirRec.dirBkDat = mBackupWhen;
                // Serialize it back into the record, and write it to disk.
                rec.UpdateCatDataDirRec(dirRec);
            } else {
                // Get current data from disk, and copy our fields over.
                CatDataFilRec filRec = rec.ParseCatDataFilRec();
                filRec.filLgLen = (uint)DataLength;
                filRec.filPyLen = DataPhyLength;
                filRec.filRLgLen = (uint)RsrcLength;
                filRec.filRPyLen = RsrcPhyLength;
                filRec.filUsrWds.fdType = mHFSFileType;
                filRec.filUsrWds.fdCreator = mHFSCreator;
                filRec.filCrDat = mCreateWhen;
                filRec.filMdDat = mModWhen;
                filRec.filBkDat = mBackupWhen;
                if ((mAccess & (byte)AccessFlags.Write) == 0) {
                    filRec.filFlags |= CatDataFilRec.FLAG_LOCKED;   // write disabled
                } else {
                    filRec.filFlags &= (~CatDataFilRec.FLAG_LOCKED) & 0xff;
                }
                filRec.filExtRec.CopyFrom(mDataExtentRec);
                filRec.filRExtRec.CopyFrom(mRsrcExtentRec);

                // Serialize it back into the record, and write it to disk.
                rec.UpdateCatDataFilRec(filRec);
            }
            node.Write();

            IsDirty = false;
        }

        /// <summary>
        /// Common code for moving and renaming files.
        /// </summary>
        /// <param name="idestDir">Destination directory.</param>
        /// <param name="newFileName">New filename.</param>
        internal void DoMoveFile(IFileEntry idestDir, string newFileName) {
            if (!IsNameValid(newFileName, IsVolumeDirectory)) {
                throw new ArgumentException("Invalid filename");
            }

            bool isMove = (ContainingDir != idestDir);
            bool isNewName = (mFileName != newFileName);
            if (!isMove && !isNewName) {
                //Debug.WriteLine("DoMoveFile: no-op");
                return;
            }

            // Unless we're renaming the volume, we need to make sure the name isn't already in
            // use.  We can just check the destination directory's child list.
            uint destCNID;
            if (IsVolumeDirectory) {
                Debug.Assert(!isMove);
                destCNID = ParentCNID;
                Debug.Assert(destCNID == HFS.ROOT_PAR_CNID);
            } else {
                HFS_FileEntry parent = (HFS_FileEntry)idestDir;
                if (parent.ChildList.ContainsKey(newFileName)) {
                    throw new IOException("A file with that name already exists");
                }
                destCNID = ((HFS_FileEntry)idestDir).EntryCNID;
            }

            // If we're moving a directory, make sure we're not trying to move it into a child
            // or itself.
            if (IsDirectory) {
                IFileEntry checkEnt = idestDir;
                while (checkEnt != IFileEntry.NO_ENTRY) {
                    if (checkEnt == this) {
                        throw new IOException("Cannot move directory to a subdirectory of itself");
                    }
                    checkEnt = checkEnt.ContainingDir;
                }
            }

            HFS_BTree catTree = FileSystem.VolMDB!.CatalogFile!;

            // Find the file's catalog entry record and the thread record (optional for
            // non-directory), using the current filename.
            HFS_Record.CatKey oldCatKey = new HFS_Record.CatKey(ParentCNID, mFileName);
            bool found = catTree.FindRecord(oldCatKey, out HFS_Node? entryNode,
                out int entryRecNum);
            if (!found) {
                throw new IOException("Unable to find catalog record");
            }
            Debug.Assert(entryNode != null);
            HFS_Record.CatKey? oldThreadKey = new HFS_Record.CatKey(EntryCNID);
            found = catTree.FindRecord(oldThreadKey, out HFS_Node? threadNode,
                out int threadRecNum);
            if (!found) {
                if (IsDirectory) {
                    throw new IOException("Unable to find directory thread record");
                } else {
                    oldThreadKey = null;
                }
            }

            // Form new file/dir record and (sometimes) thread record.
            HFS_Record.CatKey newCatKey;
            HFS_Record newRecord;
            if (IsDirectory) {
                HFS_Record rec = entryNode.GetRecord(entryRecNum);
                CatDataDirRec catRec = rec.ParseCatDataDirRec();
                newCatKey = new HFS_Record.CatKey(destCNID, newFileName);
                newRecord = HFS_Record.GenerateCatDataDirRec(newCatKey, catRec);
            } else {
                HFS_Record rec = entryNode.GetRecord(entryRecNum);
                CatDataFilRec catRec = rec.ParseCatDataFilRec();
                newCatKey = new HFS_Record.CatKey(destCNID, newFileName);
                newRecord = HFS_Record.GenerateCatDataFilRec(newCatKey, catRec);
            }

            HFS_Record.CatKey? newThreadKey = null;
            HFS_Record? newThreadRec = null;
            if (oldThreadKey != null) {
                // Update the thread record.
                CatDataThreadRec threadData = HFS_Record.CreateCatThread(destCNID,
                    newFileName, IsDirectory);
                newThreadKey = new HFS_Record.CatKey(EntryCNID);
                newThreadRec = HFS_Record.GenerateCatDataThreadRec(newThreadKey, threadData);
            }

            // We can insert the new catalog dir/file records before deleting the old ones,
            // because the keys are different, but the thread records keep the same keys.
            // Expand the free space before we mess with anything.  Because the catalog data
            // key can end up in a different node, deleting the old node is not a guarantee
            // that the tree won't expand when we insert the updated node.
            catTree.EnsureSpace(2);

            // Delete the old keys.
            catTree.Delete(oldCatKey, false);
            if (oldThreadKey != null) {
                catTree.Delete(oldThreadKey, false);
            }

            // Insert the new keys.
            catTree.Insert(newCatKey, newRecord);
            if (newThreadKey != null && newThreadRec != null) {
                catTree.Insert(newThreadKey, newThreadRec);
            }

            // If this was a move, update the directory valences and the entry's parent CNID.
            // Update our internal child lists.
            if (isMove) {
                HFS_FileEntry oldParent = (HFS_FileEntry)ContainingDir;
                HFS_FileEntry newParent = (HFS_FileEntry)idestDir;
                FileSystem.UpdateValence(ParentCNID, IsDirectory, -1);
                FileSystem.UpdateValence(destCNID, IsDirectory, +1);
                ParentCNID = destCNID;

                oldParent.ChildList.Remove(mFileName);
                newParent.ChildList.Add(newFileName, this);
                ContainingDir = newParent;
            } else {
                // The ChildList is a sorted list that uses the filename as the key.  Update it,
                // unless this is the volume directory.
                if (ContainingDir != IFileEntry.NO_ENTRY) {
                    ((HFS_FileEntry)ContainingDir).ChildList.Remove(mFileName);
                    ((HFS_FileEntry)ContainingDir).ChildList.Add(newFileName, this);
                }
            }

            // Update filename properties.
            byte[] rawFileName = new byte[newFileName.Length];
            MacChar.UnicodeToMac(newFileName, rawFileName, 0, MacChar.Encoding.RomanShowCtrl);
            mFileName = newFileName;
            byte len = (byte)rawFileName.Length;
            Debug.Assert(newFileName.Length == len);
            mRawFileNameStr = new byte[len + 1];
            mRawFileNameStr[0] = len;
            Array.Copy(rawFileName, 0, mRawFileNameStr, 1, len);

            // If this was the volume directory, update the MDB.
            if (IsVolumeDirectory) {
                FileSystem.VolMDB.VolumeName = newFileName;
            }
        }

        #region Filenames

        // IFileEntry
        public int CompareFileName(string fileName) {
            return MacChar.CompareHFSFileNames(mFileName, fileName);
        }

        // IFileEntry
        public int CompareFileName(string fileName, char fileNameSeparator) {
            return CompareFileName(fileName);
            //return PathName.ComparePathNames(mFileName, HFS.SEP_CHAR, fileName, fileNameSeparator,
            //    PathName.CompareAlgorithm.HFSFileName);
        }

        /// <summary>
        /// Compares two pathnames, using the Mac OS Roman comparison rules.
        /// </summary>
        /// <param name="fileName1">First filename.</param>
        /// <param name="fileName2">Second filename.</param>
        /// <returns>Less than zero if fileName1 precedes fileName2 in lexical sort order, zero
        ///   if it's in the same position, or greater than zero if fileName1 follows
        ///   fileName2.</returns>
        //public static int CompareFileNames(string fileName1, string fileName2) {
        //    return MacChar.CompareHFSFileNames(fileName1, fileName2);
        //}

        /// <summary>
        /// Returns true if the string is a valid HFS volume name.
        /// </summary>
        /// <remarks>
        /// Control characters are allowed if mapped to the control-pictures group.
        /// </remarks>
        public static bool IsVolumeNameValid(string name) {
            return IsNameValid(name, true);
        }

        /// <summary>
        /// Returns true if the string is a valid HFS file name.
        /// </summary>
        /// <remarks>
        /// Control characters are allowed if mapped to the control-pictures group.
        /// </remarks>
        public static bool IsFileNameValid(string name) {
            return IsNameValid(name, false);
        }

        private static bool IsNameValid(string name, bool isVolumeName) {
            // We need to check the unicode values of the Mac OS Roman character set, so we
            // can't use a simple regex for this one.
            int maxLen = isVolumeName ? HFS.MAX_VOL_NAME_LEN : HFS.MAX_FILE_NAME_LEN;
            if (name.Length < 1 || name.Length > maxLen) {
                return false;
            }
            if (name.Contains(':')) {
                return false;
            }
            return MacChar.IsStringValid(name, MacChar.Encoding.RomanShowCtrl);
        }

        /// <summary>
        /// Generates the "cooked" form of the filename.
        /// </summary>
        /// <remarks>
        /// <para>HFS allows all characters except ':', with a minimum length of 1 character and
        /// a maximum length of 31 characters.  Volume names follow the same rules, but are
        /// limited to 27 characters.</para>
        /// <para>We convert control characters to printable glyphs for display.</para>
        /// </remarks>
        /// <param name="rawFileName">Raw filename data (28 or 32 bytes).</param>
        /// <returns>Filename string, or the empty string if name has an invalid length.  We
        ///   don't test for invalid characters here.</returns>
        public static string GenerateCookedName(byte[] rawFileName, bool isVolumeName) {
            int maxNameLen = isVolumeName ? HFS.MAX_VOL_NAME_LEN : HFS.MAX_FILE_NAME_LEN;
            Debug.Assert(rawFileName.Length == maxNameLen + 1);

            byte nameLen = rawFileName[0];
            if (nameLen < 1 || nameLen > maxNameLen) {
                //Debug.WriteLine("Invalid raw HFS filename");
                return string.Empty;
            }

            return MacChar.MacToUnicode(rawFileName, 1, nameLen, MacChar.Encoding.RomanShowCtrl);
        }

        /// <summary>
        /// Generates the "raw" HFS filename or volume name.
        /// </summary>
        /// <param name="fileName">HFS-compatible filename.</param>
        /// <returns>Raw filename, in a maximum-length byte buffer, or null if the conversion
        ///   failed.</returns>
        public static byte[]? GenerateRawName(string fileName, bool isVolumeName) {
            int maxNameLen = isVolumeName ? HFS.MAX_VOL_NAME_LEN : HFS.MAX_FILE_NAME_LEN;
            if (fileName.Length < 1 || fileName.Length > maxNameLen) {
                return null;
            }
            if (!MacChar.IsStringValid(fileName, MacChar.Encoding.RomanShowCtrl)) {
                return null;
            }

            byte[] rawName = new byte[maxNameLen];
            rawName[0] = (byte)fileName.Length;
            MacChar.UnicodeToMac(fileName, rawName, 1, MacChar.Encoding.RomanShowCtrl);
            return rawName;
        }

        /// <summary>
        /// Adjusts a filename to be compatible with this filesystem, removing invalid characters
        /// and shortening the name.
        /// </summary>
        /// <param name="fileName">Filename to adjust.</param>
        /// <returns>Adjusted filename.</returns>
        public static string AdjustFileName(string fileName) {
            return DoAdjustFileName(fileName, HFS.MAX_FILE_NAME_LEN);
        }

        /// <summary>
        /// Adjusts a volume name to be compatible with this filesystem.
        /// </summary>
        /// <param name="volName">Volume name to adjust.</param>
        /// <returns>Adjusted volume name, or null if this filesystem doesn't support
        ///   volume names.</returns>
        public static string? AdjustVolumeName(string volName) {
            return DoAdjustFileName(volName, HFS.MAX_VOL_NAME_LEN);
        }

        private static string DoAdjustFileName(string fileName, int maxLen) {
            if (string.IsNullOrEmpty(fileName)) {
                return "Q";
            }

            // Replace colons and any non-MOR characters.
            char[] chars = fileName.ToCharArray();
            for (int i = 0; i < chars.Length; i++) {
                char ch = chars[i];
                if (!MacChar.IsCharValid(ch, MacChar.Encoding.RomanShowCtrl)) {
                    chars[i] = '?';
                } else if (ch == ':') {
                    chars[i] = '?';
                }
            }

            string cleaned = new string(chars);
            // Clamp to max length by removing characters from the middle.
            if (cleaned.Length > maxLen) {
                int firstLen = maxLen / 2;        // 15
                int lastLen = maxLen - (firstLen + 2);
                cleaned = cleaned.Substring(0, firstLen) + ".." +
                    cleaned.Substring(fileName.Length - lastLen, lastLen);
                Debug.Assert(cleaned.Length == maxLen);
            }
            return cleaned;
        }

        #endregion Filenames

        public override string ToString() {
            return "[HFS file entry: '" + mFileName + "' (ID=#0x" + EntryCNID.ToString("x4") + ")]";
        }
    }
}
