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
using System.Collections;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

using CommonUtil;
using static DiskArc.Defs;

namespace DiskArc.FS {
    public class CPM_FileEntry : IFileEntryExt, IDisposable {
        //
        // IFileEntry interfaces.
        //

        public bool IsValid { get { return FileSystem != null; } }

        public bool IsDubious { get { return mHasConflict; } }

        public bool IsDamaged { get; private set; }

        public bool IsDirectory { get; private set; }

        public bool HasDataFork => true;
        public bool HasRsrcFork => false;
        public bool IsDiskImage => false;

        public IFileEntry ContainingDir { get; private set; }

        public int Count => ChildList.Count;        // fake vol dir only

        public string FileName {
            get {
                if (IsDirectory) {
                    return mFakeDirName;
                }
                return mExtentList[0].FileName;
            }
            set {
                CheckChangeAllowed();
                if (IsDirectory) {
                    return;
                }
                if (!IsFileNameValid(value)) {
                    throw new ArgumentException("Invalid filename");
                }
                // Update all copies of the filename.
                foreach (Extent ext in mExtentList) {
                    ext.FileName = value;
                }
            }
        }
        public char DirectorySeparatorChar { get => IFileEntry.NO_DIR_SEP; set { } }
        public string FullPathName {
            get {
                if (IsDirectory) {
                    return mFakeDirName;
                }
                string pathName = mExtentList[0].FileName;
                if (FileSystem == null) {
                    pathName = "!INVALID! was:" + pathName;
                }
                return pathName;
            }
        }
        public byte[] RawFileName {
            get {
                if (IsDirectory) {
                    return new byte[] { (byte)'?' };
                }
                // Return it in 8+3 form.
                byte[] result = new byte[Extent.FILENAME_FIELD_LEN];
                Array.Copy(mExtentList[0].RawFileName, result, result.Length);
                return result;
            }
            set {
                CheckChangeAllowed();
                if (IsDirectory) {
                    return;
                }
                // Minimal checks.
                if (value.Length != Extent.FILENAME_FIELD_LEN) {
                    throw new ArgumentException("Must be an 11-byte 8+3 name");
                }
                // Update all copies of the raw filename.  The access flags are stored in the
                // filename bytes, so we want to save and restore them.
                byte savedFlags = Access;
                foreach (Extent ext in mExtentList) {
                    ext.RawFileName = value;
                }
                Access = savedFlags;
            }
        }

        public bool HasProDOSTypes => false;

        public byte FileType { get => 0; set { } }
        public ushort AuxType { get => 0; set { } }

        public bool HasHFSTypes => false;
        public uint HFSFileType { get => 0; set { } }
        public uint HFSCreator { get => 0; set { } }

        public byte Access {
            get {
                if (IsDirectory) {
                    return FileAttribs.FILE_ACCESS_UNLOCKED;
                }
                // Pull the attribute flags out of the lowest-numbered extent (usually #0).
                return mExtentList[0].AccessFlags;
            }
            set {
                CheckChangeAllowed();
                if (IsDirectory) {
                    return;
                }
                foreach (Extent ext in mExtentList) {
                    ext.AccessFlags = value;
                }
            }
        }

        // Timestamps may be available on some disks.  Not currently handling them.
        public DateTime CreateWhen { get => TimeStamp.NO_DATE; set { } }
        public DateTime ModWhen { get => TimeStamp.NO_DATE; set { } }

        public long StorageSize {
            get {
                if (IsDirectory) {
                    return 0;
                }
                long total = 0;
                foreach (Extent ext in mExtentList) {
                    for (int i = 0; i < ext.PtrsPerExtent; i++) {
                        if (ext[i] != 0) {
                            total += FileSystem.AllocUnitSize;
                        }
                    }
                }
                return total;
            }
        }

        public long DataLength {
            get {
                if (IsDirectory) {
                    return 0;
                }
                // Get the extent number of the last extent in the list.
                Extent lastExt = mExtentList[mExtentList.Count - 1];
                int fullExtents = lastExt.ExtentNumber;
                // Get the number of records used in the last extent.
                int recordCount = lastExt.RecordCount;
                return (fullExtents * CPM.RECS_PER_EXTENT + recordCount) * CPM.FILE_REC_LEN
                    - CPM.FILE_REC_LEN + lastExt.ByteCount;
            }
        }

        public long RsrcLength => 0;

        public string Comment { get => string.Empty; set { } }

        public bool GetPartInfo(FilePart part, out long length, out long storageSize,
                out CompressionFormat format) {
            format = CompressionFormat.Uncompressed;
            if (part == FilePart.DataFork || part == FilePart.RawData) {
                length = DataLength;
                storageSize = StorageSize;
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
        // Implementation-specific.
        //

        /// <summary>
        /// Directory extent record.  We have one of these for every slot in the volume directory.
        /// Changing a value sets the "is dirty" flag for the associated disk block.
        /// </summary>
        internal class Extent : IComparable<Extent> {
            public const int LENGTH = 32;
            public const int MAX_BLOCK_PTRS = 16;
            public const int FILENAME_FIELD_LEN = 11;     // 8.3 without the dot

            private byte mStatus;
            private byte[] mRawFileName = new byte[FILENAME_FIELD_LEN];
            private byte mExtentLow;
            private byte mByteCount;
            private byte mExtentHigh;
            private byte mRecordCount;
            private ushort[] mBlockPtrs = new ushort[MAX_BLOCK_PTRS];

            public byte Status {
                get { return mStatus; }
                set {
                    mStatus = value;
                    mFileName = CookFileName(mRawFileName, mStatus);
                    mDirtyFlag.IsSet = true;
                }
            }
            public byte[] RawFileName {
                get { return mRawFileName; }        // caller must not modify array contents
                set {
                    // This tramples the access flags.  The caller must preserve them.
                    Debug.Assert(value.Length == FILENAME_FIELD_LEN);
                    Array.Copy(value, mRawFileName, FILENAME_FIELD_LEN);
                    mFileName = CookFileName(mRawFileName, mStatus);
                    mDirtyFlag.IsSet = true;
                }
            }
            public byte AccessFlags {
                get {
                    FileAttribs.AccessFlags flags = FileAttribs.AccessFlags.Read |
                        FileAttribs.AccessFlags.Rename | FileAttribs.AccessFlags.Delete;
                    if ((mRawFileName[8] & 0x80) == 0) {
                        flags |= FileAttribs.AccessFlags.Write;
                    }
                    if ((mRawFileName[9] & 0x80) != 0) {
                        flags |= FileAttribs.AccessFlags.Invisible;
                    }
                    if ((mRawFileName[10] & 0x80) != 0) {
                        flags |= FileAttribs.AccessFlags.Backup;
                    }
                    return (byte)flags;
                }
                set {
                    if ((value & (byte)FileAttribs.AccessFlags.Write) == 0) {
                        mRawFileName[8] |= 0x80;
                    } else {
                        mRawFileName[8] &= 0x7f;
                    }
                    if ((value & (byte)FileAttribs.AccessFlags.Invisible) != 0) {
                        mRawFileName[9] |= 0x80;
                    } else {
                        mRawFileName[9] &= 0x7f;
                    }
                    if ((value & (byte)FileAttribs.AccessFlags.Backup) != 0) {
                        mRawFileName[10] |= 0x80;
                    } else {
                        mRawFileName[10] &= 0x7f;
                    }
                    mDirtyFlag.IsSet = true;
                }
            }
            public int ExtentNumber {
                get { return mExtentHigh * 32 + mExtentLow; }
                set {
                    Debug.Assert(value <= CPM.MAX_EXTENT_NUM);
                    mExtentLow = (byte)(value % 32);
                    mExtentHigh = (byte)(value / 32);
                    mDirtyFlag.IsSet = true;
                }
            }
            public byte RecordCount {
                get { return mRecordCount; }
                set {
                    Debug.Assert(value <= 128);
                    if (mRecordCount != value) {
                        mRecordCount = value;
                        mDirtyFlag.IsSet = true;
                    }
                }
            }
            public byte ByteCount {
                get {
                    if (mByteCount == 0 || mByteCount > CPM.FILE_REC_LEN) {
                        return CPM.FILE_REC_LEN;
                    } else {
                        return mByteCount;
                    }
                }
                set {
                    Debug.Assert(value <= 128);
                    if (mByteCount != value) {
                        mByteCount = value;
                    }
                }
            }
            public ushort this[int i] {
                get {
                    return mBlockPtrs[i];
                }
                set {
                    mBlockPtrs[i] = value;
                    mDirtyFlag.IsSet = true;
                }
            }

            public int DirIndex { get; }
            public int PtrsPerExtent { get; }
            public string FileName {
                get { return mFileName; }
                set {
                    Debug.Assert(mStatus != CPM.NO_DATA);
                    RawifyFileName(value, mRawFileName, out byte userNum);
                    mFileName = value;
                    mStatus = userNum;
                    mDirtyFlag.IsSet = true;
                }
            }
            private string mFileName;

            private GroupBool mDirtyFlag;

            public Extent(int dirIndex, int ptrsPerExtent, GroupBool dirtyFlag) {
                Debug.Assert(dirIndex >= 0);
                Debug.Assert(ptrsPerExtent == 8 || ptrsPerExtent == 16);
                DirIndex = dirIndex;
                PtrsPerExtent = ptrsPerExtent;
                mDirtyFlag = dirtyFlag;
                mFileName = string.Empty;
            }

            public void SetForNew(byte userNumber, string fileName) {
                Status = userNumber;
                FileName = fileName;
                ExtentNumber = 0;
                RecordCount = 0;
                ByteCount = 0;
                for (int i = 0; i < PtrsPerExtent; i++) {
                    this[i] = 0;
                }
            }

            public void Load(byte[] buf) {
                int offset = DirIndex * LENGTH;

                int startOffset = offset;
                mStatus = buf[offset++];
                Array.Copy(buf, offset, mRawFileName, 0, FILENAME_FIELD_LEN);
                offset += FILENAME_FIELD_LEN;
                mExtentLow = buf[offset++];
                mByteCount = buf[offset++];
                mExtentHigh = buf[offset++];
                mRecordCount = buf[offset++];
                if (PtrsPerExtent == 16) {
                    for (int i = 0; i < 16; i++) {
                        mBlockPtrs[i] = buf[offset++];
                    }
                } else {
                    for (int i = 0; i < 8; i++) {
                        mBlockPtrs[i] = RawData.ReadU16LE(buf, ref offset);
                    }
                }
                Debug.Assert(offset - startOffset == LENGTH);

                if (mStatus != CPM.NO_DATA) {
                    mFileName = CookFileName(mRawFileName, mStatus);
                }
            }
            public void Store(byte[] buf) {
                int offset = DirIndex * LENGTH;

                int startOffset = offset;
                buf[offset++] = mStatus;
                Array.Copy(mRawFileName, 0, buf, offset, FILENAME_FIELD_LEN);
                offset += FILENAME_FIELD_LEN;
                buf[offset++] = mExtentLow;
                buf[offset++] = mByteCount;
                buf[offset++] = mExtentHigh;
                buf[offset++] = mRecordCount;
                if (PtrsPerExtent == 16) {
                    for (int i = 0; i < 16; i++) {
                        buf[offset++] = (byte)mBlockPtrs[i];
                    }
                } else {
                    for (int i = 0; i < 8; i++) {
                        RawData.WriteU16LE(buf, ref offset, mBlockPtrs[i]);
                    }
                }
                Debug.Assert(offset - startOffset == LENGTH);
            }

            // IComparable
            public int CompareTo(Extent? other) {
                // Ascending sort by user number, then filename, then extent number.
                if (other == null) {
                    return 1;
                }
                if (mStatus < other.mStatus) {
                    return -1;
                } else if (mStatus > other.mStatus) {
                    return 1;
                }
                int nameCmp = FileName.CompareTo(other.FileName);
                if (nameCmp != 0) {
                    return nameCmp;
                }
                if (ExtentNumber < other.ExtentNumber) {
                    return -1;
                } else if (ExtentNumber > other.ExtentNumber) {
                    return 1;
                }
                // This should not be possible for file extents.  It may be possible for
                // special records, and is definitely possible for unused entries.
                return 0;
            }

            public override string ToString() {
                return "[Extent: " + mStatus + ":'" + FileName + "' ext#" + ExtentNumber + "]";
            }
        }

        /// <summary>
        /// Special extent use as a placeholder in sparse files, and as a return value when a
        /// requested extent doesn't exist.
        /// </summary>
        internal static readonly Extent NO_EXTENT = new Extent(65535, 8, new GroupBool());

        /// <summary>
        /// List of extents associated with this file.  There will be at least one entry here.
        /// The list is kept sorted by extent number.  There may be gaps for sparse files.
        /// </summary>
        private List<Extent> mExtentList = new List<Extent>();
        internal Extent GetExtent(int extNum) {
            // Try the common case, where extent N is in slot N.
            if (extNum < mExtentList.Count) {
                Extent extent = mExtentList[extNum];
                if (extent.ExtentNumber == extNum) {
                    return extent;
                }
            }
            // There are sparse gaps, need to walk the list.  We could avoid this by inserting
            // dummy entries, but then we couldn't use entry[0] for file attributes.  This should
            // be extremely rare.
            foreach (Extent ext in mExtentList) {
                if (ext.ExtentNumber == extNum) {
                    return ext;
                }
            }
            return NO_EXTENT;
        }

        /// <summary>
        /// Adds a new extent to the list of extents.  The extent number must not already exist.
        /// </summary>
        /// <param name="ext">Extent to add.</param>
        internal void AddExtent(Extent ext) {
            Debug.Assert(GetExtent(ext.ExtentNumber) == NO_EXTENT);
            Debug.Assert(ext.Status == mExtentList[0].Status);
            Debug.Assert(ext.FileName == mExtentList[0].FileName);
            if (ext.ExtentNumber == mExtentList.Count) {
                // At end of list.
                mExtentList.Add(ext);
            } else {
                // Find correct spot.
                bool wasInserted = false;
                for (int i = 0; i < mExtentList.Count; i++) {
                    if (ext.ExtentNumber < mExtentList[i].ExtentNumber) {
                        mExtentList.Insert(i, ext);
                        wasInserted = true;
                        break;
                    }
                }
                if (!wasInserted) {
                    mExtentList.Add(ext);
                }
            }
        }

        /// <summary>
        /// Truncates a file to the specified length.
        /// </summary>
        /// <param name="length">New length.</param>
        /// <exception cref="DiskFullException">Rare sparse file edge case.</exception>
        internal void TruncateTo(long length) {
            CPM_AllocMap allocMap = FileSystem!.AllocMap!;

            // Compute the number of extents needed.
            int extNeeded, bytesInLast;
            if (length == 0) {
                // We always need at least one extent, to hold the filename.  This is the only
                // one that's allowed to have zero assigned alloc blocks.
                extNeeded = 1;
                bytesInLast = 0;
            } else {
                extNeeded = (int)((length + CPM.BYTES_PER_EXTENT - 1) / CPM.BYTES_PER_EXTENT);
                bytesInLast = (int)(length - (extNeeded - 1) * CPM.BYTES_PER_EXTENT);
                Debug.Assert(bytesInLast > 0 && bytesInLast <= CPM.BYTES_PER_EXTENT);
            }

            // Compute the number of alloc blocks needed in the last extent.
            uint allocSize = FileSystem.AllocUnitSize;
            int allocInLast = (int)((bytesInLast + allocSize - 1) / allocSize);
            int maxPerExt = (FileSystem.TotalAllocBlocks <= 256) ? 16 : 8;
            Debug.Assert(allocInLast >= 0 && allocInLast <= maxPerExt);
            Debug.Assert(allocInLast > 0 || length == 0);

            // Find the extent in which things are expected to end.  If it doesn't exist,
            // because we're truncating to a sparse hole, we have to create a new one.  In theory
            // we could run out of disk space (volume directory full) while doing this.  We
            // could potentially avoid that by removing the later extents first, but if we fail
            // here we can just bail out because we haven't changed anything yet.
            int lastExtNum = extNeeded - 1;
            Extent lastExt = GetExtent(lastExtNum);
            if (lastExt == NO_EXTENT) {
                lastExt = FileSystem.FindFreeExtent();      // could throw
                lastExt.SetForNew(UserNumber, FileName);
                lastExt.ExtentNumber = lastExtNum;
                AddExtent(lastExt);
            }

            // Remove any extents beyond the last one we need.
            for (int i = mExtentList.Count - 1; i >= 0; i--) {
                Extent ext = mExtentList[i];
                if (ext.ExtentNumber > lastExtNum) {
                    ReleaseExtent(ext, allocMap);
                    mExtentList.RemoveAt(i);
                }
            }

            // Release un-needed allocation blocks in the last extent.
            for (int i = allocInLast; i < maxPerExt; i++) {
                uint allocNum = lastExt[i];
                if (allocNum != 0) {
                    allocMap.MarkAllocBlockUnused(lastExt[i]);
                    lastExt[i] = 0;
                }
            }

            // Update the record and byte counts.
            UpdateLength(length);
        }

        /// <summary>
        /// Updates the length fields in the extents to match recent activity.
        /// </summary>
        /// <param name="length">Length of file.</param>
        internal void UpdateLength(long length) {
            // Update all extents.  The Extent code will refrain from setting the dirty flag
            // if we don't actually change anything.
            for (int i = 0; i < mExtentList.Count; i++) {
                Extent ext = mExtentList[i];
                if (i == mExtentList.Count - 1) {
                    // Last extent, set RC and BC based on the amount of data held in it.
                    int extentLen = (int)(length - (ext.ExtentNumber * CPM.BYTES_PER_EXTENT));
                    ext.RecordCount = (byte)((extentLen + 127) / CPM.FILE_REC_LEN);
                    ext.ByteCount = (byte)(extentLen % CPM.FILE_REC_LEN);
                } else {
                    // Not the last extent, so we set to show that the full 16KB is used.
                    ext.RecordCount = 128;
                    ext.ByteCount = 0;
                }
            }
        }

        /// <summary>
        /// Reference to filesystem object.
        /// </summary>
        public CPM FileSystem { get; private set; }

        /// <summary>
        /// User number.
        /// </summary>
        /// <remarks>
        /// </remarks>
        public byte UserNumber {
            get { return mExtentList[0].Status; }
            set {
                CheckChangeAllowed();
                if (IsDirectory) {
                    return;
                }
                if (value > CPM.MAX_USER_NUM) {
                    throw new ArgumentException("Invalid user number");
                }
                // Update all copies of the user number.
                foreach (Extent ext in mExtentList) {
                    ext.Status = value;
                }
            }
        }

        /// <summary>
        /// True if this is the "fake" volume directory object.
        /// </summary>
        private bool IsVolumeDirectory { get; set; }

        /// <summary>
        /// For volume directory objects, the "fake" volume name.
        /// </summary>
        private string mFakeDirName = string.Empty;

        /// <summary>
        /// List of files contained in this entry.  Only the volume dir has children.
        /// </summary>
        internal List<IFileEntry> ChildList { get; } = new List<IFileEntry>();

        /// <summary>
        /// True if the file shares storage with another file or the system.
        /// </summary>
        private bool mHasConflict;

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="fileSystem">Filesystem this file entry is a part of.</param>
        internal CPM_FileEntry(CPM fileSystem) {
            ContainingDir = IFileEntry.NO_ENTRY;
            FileSystem = fileSystem;
        }

        // IDisposable generic finalizer.
        ~CPM_FileEntry() {
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
            if (disposing) {
                SaveChanges();
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
            ChildList.Clear();
        }

        /// <summary>
        /// Scans the list of extents, generating a list of files.
        /// </summary>
        /// <param name="fileSystem">Filesystem to scan.</param>
        /// <returns>Fake file entry object for the volume directory.</returns>
        internal static IFileEntry ScanDirectory(CPM fileSystem) {
            CPM_FileEntry volDir = CreateFakeVolDirEntry(fileSystem);
            Notes notes = fileSystem.Notes;

            // Generate a sorted list of extents, so all parts of a given file will be
            // next to each other and in extent order.
            SortedList<Extent, Extent> sortex = new SortedList<Extent, Extent>();
            foreach (Extent ext in fileSystem.Extents) {
                if (ext.Status != CPM.NO_DATA) {
                    if (sortex.ContainsKey(ext)) {
                        // An identical entry is already present.
                        notes.AddE("Found two identical entries; user=" + ext.Status +
                            " name='" + ext.FileName + "' ext#=" + ext.ExtentNumber);
                        fileSystem.IsDubious = true;
                    } else {
                        sortex.Add(ext, ext);
                    }
                }
            }

            // Generate file entries, validating the extents as we go.
            string prevFileName = string.Empty;
            int prevStatus = -1;
            CPM_FileEntry? curEntry = null;
            foreach (Extent ext in sortex.Keys) {
                Debug.Assert(ext.Status != CPM.NO_DATA);    // these were omitted
                if (ext.Status > CPM.MAX_USER_NUM) {
                    // Don't create a file entry for these.
                    // TODO(someday): extract v3 date info and disc label
                    if (ext.Status == CPM.RESERVED_SPACE) {
                        MarkExtentBlocks(ext, fileSystem, IFileEntry.NO_ENTRY);
                    } else {
                        // Assume this is a special entry that doesn't have any associated disk
                        // allocation.
                        notes.AddI("Found extent with unexpected status 0x" +
                            ext.Status.ToString("x2"));
                    }
                    continue;
                }

                if (ext.Status == prevStatus && ext.FileName == prevFileName) {
                    // Add this extent to the entry in progress.  It should not be possible to
                    // find a duplicate here, since we didn't add those to the sorted list.
                    Debug.Assert(curEntry != null);
                    Debug.Assert(!curEntry.mExtentList.Contains(ext));
                    curEntry.mExtentList.Add(ext);
                } else {
                    // Create a new file entry.
                    curEntry = new CPM_FileEntry(fileSystem);
                    curEntry.ContainingDir = volDir;
                    curEntry.mExtentList.Add(ext);
                    volDir.ChildList.Add(curEntry);

                    if (!IsFileNameValid(curEntry.FileName)) {
                        notes.AddW("Invalid filename '" + curEntry.FileName + "'");
                        // No need to mark as dubious.
                    }

                    prevStatus = ext.Status;
                    prevFileName = ext.FileName;
                }
                MarkExtentBlocks(ext, fileSystem, curEntry);
            }

            // The outcome of the above is a fully-sorted list, but we want the child list to be
            // arranged according to the first appearance of each file in the directory to match
            // the output of "DIR".  Some CP/M utilities created sorted lists, so this step may
            // not be necessary or desirable.
            volDir.ChildList.Sort(delegate (IFileEntry ientry1, IFileEntry ientry2) {
                CPM_FileEntry entry1 = (CPM_FileEntry)ientry1;
                CPM_FileEntry entry2 = (CPM_FileEntry)ientry2;
                if (entry1.IsDirectory && !entry2.IsDirectory) {
                    return 1;
                } else if (!entry1.IsDirectory && entry2.IsDirectory) {
                    return -1;
                } else if (entry1.IsDirectory && entry2.IsDirectory) {
                    return entry1.mFakeDirName.CompareTo(entry2.mFakeDirName);
                } else {
                    int index1 = entry1.mExtentList[0].DirIndex;
                    int index2 = entry2.mExtentList[0].DirIndex;
                    return (index1 - index2);
                }
            });

            return volDir;
        }

        /// <summary>
        /// Marks the allocation blocks listed in the extent as being in use.  Conflicts will
        /// be reported through <see cref="AddConflict"/>.
        /// </summary>
        /// <param name="ext">Extent to process.</param>
        /// <param name="ientry">File entry to associate the blocks with, or NO_ENTRY if this
        ///   is a system area.</param>
        private static void MarkExtentBlocks(Extent ext, CPM fs, IFileEntry ientry) {
            CPM_AllocMap map = fs.AllocMap!;
            for (int i = 0; i < ext.PtrsPerExtent; i++) {
                ushort allocNum = ext[i];
                if (allocNum == 0) {
                    // sparse; ignore
                } else if (allocNum >= fs.TotalAllocBlocks) {
                    fs.Notes.AddE("Bad alloc block in '" + ientry.FileName + "': 0x" +
                        allocNum.ToString("x2"));
                    CPM_FileEntry? entry = ientry as CPM_FileEntry;
                    if (entry != null) {
                        entry.IsDamaged = true;
                    }
                } else {
                    map.MarkUsedByScan(ext[i], ientry);
                }
            }
        }

        /// <summary>
        /// Releases the storage associated with this file.
        /// </summary>
        internal void ReleaseStorage() {
            CPM_AllocMap allocMap = FileSystem!.AllocMap!;
            foreach (Extent ext in mExtentList) {
                ReleaseExtent(ext, allocMap);
            }
        }

        /// <summary>
        /// Frees the blocks associated with an extent, and marks the extent as unused.
        /// </summary>
        /// <param name="ext"></param>
        /// <param name="allocMap"></param>
        private static void ReleaseExtent(Extent ext, CPM_AllocMap allocMap) {
            for (int i = 0; i < ext.PtrsPerExtent; i++) {
                uint allocNum = ext[i];
                if (allocNum != 0) {
                    allocMap.MarkAllocBlockUnused(ext[i]);
                }
            }
            ext.Status = CPM.NO_DATA;
        }

        /// <summary>
        /// Creates a new directory entry.
        /// </summary>
        /// <param name="fs">Filesystem object.</param>
        /// <param name="parentDir">Parent directory.</param>
        /// <param name="ext">Initial extent.  Contents will be overwritten.</param>
        /// <param name="fileName">Filename.</param>
        /// <returns>New directory entry object.</returns>
        internal static CPM_FileEntry CreateEntry(CPM fs, IFileEntry parentDir, Extent ext,
                string fileName) {
            CPM_FileEntry newEntry = new CPM_FileEntry(fs);
            newEntry.ContainingDir = parentDir;
            newEntry.mExtentList.Add(ext);
            ext.SetForNew(0, fileName);
            return newEntry;
        }

        /// <summary>
        /// Creates a "fake" file entry for the volume directory.
        /// </summary>
        private static CPM_FileEntry CreateFakeVolDirEntry(CPM fs) {
            CPM_FileEntry volDirEntry = new CPM_FileEntry(fs);
            volDirEntry.IsVolumeDirectory = volDirEntry.IsDirectory = true;
            if (fs.AllocUnitSize == 1024) {
                volDirEntry.mFakeDirName = "5.25\" disk";
            } else {
                volDirEntry.mFakeDirName = "3.5\" disk";
            }
            //for (int i = 1; i <= CPM.MAX_USER_NUM; i++) {
            //    CPM_FileEntry fakeDir = new CPM_FileEntry(fs);
            //    fakeDir.IsDirectory = true;
            //    fakeDir.mFakeDirName = i.ToString();
            //    fakeDir.ContainingDir = volDirEntry;
            //    volDirEntry.ChildList.Add(fakeDir);
            //}
            return volDirEntry;
        }

        // IFileEntry
        public void SaveChanges() {
            // If we have pending changes, one or more directory blocks will be dirty.  Ask the
            // filesystem to do a flush.
            FileSystem.FlushVolumeDir();
        }

        // IFileEntry
        public void AddConflict(uint chunk, IFileEntry entry) {
            if (!mHasConflict) {
                string name = (entry == IFileEntry.NO_ENTRY) ?
                    VolumeUsage.SYSTEM_STR : entry.FullPathName;
                if (entry == IFileEntry.NO_ENTRY) {
                    FileSystem.Notes.AddE(FullPathName + " overlaps with " + name);
                    FileSystem.IsDubious = true;
                } else {
                    FileSystem.Notes.AddW(FullPathName + " overlaps with " + name);
                }
            }
            mHasConflict = true;
        }

        #region Filenames

        // Regex pattern for filename validation.
        //
        // Filenames are 1-8 characters with a 0-3 character extension.  They may include
        // printable ASCII characters other than spaces and '<>.,;:=?*[]'.  This regex
        // allows "NAME" and "NAME.EXT" but not "NAME.".
        // (Exclusion uses "negative lookahead": https://stackoverflow.com/a/35427132/294248.)
        private const string FILE_NAME_PATTERN = @"^((?![<>\.,;:=\?\*\[\] ])[\x20-\x7e]){1,8}" +
            @"((?:\.)((?![<>\.,;:=\?\*\[\] ])[\x20-\x7e]){1,3})?" +
            @"((?:,)(\d{1,2}))?$";
        private static Regex sFileNameRegex = new Regex(FILE_NAME_PATTERN);

        private const string INVALID_CHARS = @"<>.,;:=?*[] ";

        // IFileEntry
        public int CompareFileName(string fileName) {
            return string.Compare(FileName, fileName, StringComparison.OrdinalIgnoreCase);
        }

        // IFileEntry
        public int CompareFileName(string fileName, char fileNameSeparator) {
            return string.Compare(FileName, fileName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Returns true if the string is a valid CP/M filename.
        /// </summary>
        public static bool IsFileNameValid(string fileName) {
            MatchCollection matches = sFileNameRegex.Matches(fileName);
            if (matches.Count != 1) {
                return false;
            }
            if (matches[0].Groups.Count != 6) {
                Debug.Assert(false, "filename regex failed");
                return false;
            }
            // Check for a user number string.  We don't append the user number when user==0, so
            // we don't accept it here either.  If we did, a single file could effectively have
            // two different filenames, which could cause confusion elsewhere.
            string userNumStr = matches[0].Groups[5].Value;
            if (!string.IsNullOrEmpty(userNumStr)) {
                if (!byte.TryParse(userNumStr, out byte num)) {
                    return false;
                }
                if (num == 0 || num > CPM.MAX_USER_NUM) {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Adjusts a filename to be compatible with this filesystem, removing invalid characters
        /// and shortening the name.
        /// </summary>
        /// <param name="fileName">Filename to adjust.</param>
        /// <returns>Adjusted filename.</returns>
        public static string AdjustFileName(string fileName) {
            // Check for a user number suffix.  If we find one, strip it off the end of the string
            // and preserve it for later.
            int userNumber = -1;
            int lastComma = fileName.LastIndexOf(',');
            if (lastComma != -1 && lastComma >= fileName.Length - 3) {
                if (byte.TryParse(fileName.Substring(lastComma + 1), out byte parsedNum)) {
                    if (parsedNum > 0 && parsedNum < CPM.MAX_USER_NUM) {
                        // Strip it off the filename before continuing.
                        fileName = fileName.Substring(0, lastComma);
                        userNumber = parsedNum;
                    }
                }
            }

            // Split in half at the last '.'.
            string namePart, extPart;
            int lastDot = fileName.LastIndexOf('.');
            if (lastDot == -1) {
                namePart = fileName;
                extPart = string.Empty;
            } else {
                namePart = fileName.Substring(0, lastDot);
                extPart = fileName.Substring(lastDot + 1, fileName.Length - (lastDot + 1));
            }

            namePart = CleanPart(namePart);
            if (string.IsNullOrEmpty(namePart)) {
                namePart = "Q";
            }
            extPart = CleanPart(extPart);

            // Clamp name part to max length by removing characters from the middle.
            if (namePart.Length > 8) {
                namePart = namePart.Substring(0, 4) + "_" +
                    namePart.Substring(namePart.Length - 3, 3);
                Debug.Assert(namePart.Length == 8);
            }
            // Clamp extension by keeping the first 3 chars.
            if (extPart.Length > 3) {
                extPart = extPart.Substring(0, 3);
            }

            // Combine and convert to upper case.
            string combined = namePart;
            if (extPart != string.Empty) {
                combined += '.' + extPart;
            }
            if (userNumber != -1) {
                combined += ',' + userNumber.ToString();
            }
            return combined.ToUpperInvariant();
        }

        private static string CleanPart(string part) {
            // Convert the string to ASCII values, stripping diacritical marks.
            char[] chars = part.ToCharArray();
            ASCIIUtil.ReduceToASCII(chars, '_');

            // Replace invalid characters.
            for (int i = 0; i < chars.Length; i++) {
                char ch = chars[i];
                if (ch < 0x20 || ch == 0x7f || INVALID_CHARS.Contains(ch)) {
                    chars[i] = '_';
                }
            }
            return new string(chars);
        }

        /// <summary>
        /// Converts an 11-byte raw filename to a "cooked" 8.3 filename.  Padding spaces are
        /// excluded.  If the extension is "   ", the '.' and extension will be omitted.
        /// </summary>
        /// <param name="rawFileName">Raw filename bytes.</param>
        /// <returns>Cooked filename string.</returns>
        internal static string CookFileName(byte[] rawFileName, byte userNum) {
            Debug.Assert(rawFileName.Length == Extent.FILENAME_FIELD_LEN);
            StringBuilder sb = new StringBuilder(Extent.FILENAME_FIELD_LEN + 1);
            for (int i = 0; i < 8; i++) {
                int ch = rawFileName[i] & 0x7f;
                if (ch == ' ') {
                    break;
                }
                sb.Append((char)ch);
            }
            if ((rawFileName[8] & 0x7f) != ' ') {
                sb.Append('.');
                for (int i = 0; i < 3; i++) {
                    int ch = rawFileName[8 + i] & 0x7f;
                    if (ch == ' ') {
                        break;
                    }
                    sb.Append((char)ch);
                }
            }
            if (userNum != 0 && userNum <= CPM.MAX_USER_NUM) {
                sb.Append(',');
                sb.Append(userNum.ToString());
            }
            return sb.ToString();
        }

        /// <summary>
        /// Converts an 8.3 filename string to a "raw" 11-byte buffer.
        /// </summary>
        /// <param name="fileName">Filename to rawify.</param>
        /// <param name="outBuf">Buffer that receives filename bytes.</param>
        internal static void RawifyFileName(string fileName, byte[] outBuf, out byte userNum) {
            Debug.Assert(IsFileNameValid(fileName));
            for (int i = 0; i < Extent.FILENAME_FIELD_LEN; i++) {
                outBuf[i] = (byte)' ';
            }
            int commaIndex = fileName.IndexOf(',');
            if (commaIndex != -1) {
                if (byte.TryParse(fileName.Substring(commaIndex + 1), out userNum)) {
                    if (userNum > CPM.MAX_USER_NUM) {
                        Debug.Assert(false, "Bad user number in " + fileName);
                        userNum = 0;
                    }
                } else {
                    Debug.Assert(false, "failed to parse user number");
                    userNum = 0;
                }
                fileName = fileName.Substring(0, commaIndex);
            } else {
                userNum = 0;
            }
            int index = 0;
            foreach (char ch in fileName) {
                if (ch == '.') {
                    index = 8;
                } else {
                    outBuf[index] = (byte)ch;
                    index++;
                }
            }
        }

        #endregion Filenames

        public override string ToString() {
            string fileName;
            if (IsDirectory) {
                fileName = mFakeDirName;
            } else if (mExtentList.Count > 0) {
                fileName = mExtentList[0].FileName;
            } else {
                fileName = "!BAD!";
            }
            return "[CP/M file entry: '" + fileName + "']";
        }
    }
}
