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

        public bool IsDirectory { get { return IsVolumeDirectory; } }

        public bool HasDataFork => true;
        public bool HasRsrcFork => false;
        public bool IsDiskImage => false;

        public IFileEntry ContainingDir { get; private set; }

        public int Count => ChildList.Count;        // fake vol dir only

        public string FileName {
            get {
                if (IsVolumeDirectory) {
                    return mVolDirName;
                }
                return mExtentList[0].FileName;
            }
            set {
                CheckChangeAllowed();
                throw new NotImplementedException();
            }
        }
        public char DirectorySeparatorChar { get => IFileEntry.NO_DIR_SEP; set { } }
        public string FullPathName {
            get {
                if (IsVolumeDirectory) {
                    return mVolDirName;
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
                if (IsVolumeDirectory) {
                    return new byte[] { (byte)'?' };
                }
                byte[] result = new byte[Extent.FILENAME_FIELD_LEN];
                Array.Copy(mExtentList[0].RawFileName, 1, result, 0, result.Length);
                return result;
            }
            set {
                CheckChangeAllowed();
                throw new NotImplementedException();
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
                if (IsVolumeDirectory) {
                    return FileAttribs.FILE_ACCESS_UNLOCKED;
                }
                // Pull the attribute flags out of the lowest-numbered extent (usually #0).
                return mExtentList[0].AccessFlags;
            }
            set {
                CheckChangeAllowed();
                throw new NotImplementedException();
            }
        }

        // Timestamps may be available on some disks.  Not currently handling them.
        public DateTime CreateWhen { get => TimeStamp.NO_DATE; set { } }
        public DateTime ModWhen { get => TimeStamp.NO_DATE; set { } }

        public long StorageSize {
            get {
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

            public byte Status => mStatus;
            public byte[] RawFileName {
                get { return mRawFileName; }        // do not modify return value
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
            }
            public int ExtentNumber { get { return mExtentHigh * 32 + mExtentLow; } }
            public int RecordCount => mRecordCount;
            public byte ByteCount {
                get {
                    if (mByteCount == 0 || mByteCount > CPM.FILE_REC_LEN) {
                        return CPM.FILE_REC_LEN;
                    } else {
                        return mByteCount;
                    }
                }
            }
            public ushort this[int i] {
                get {
                    return mBlockPtrs[i];
                }
            }

            public int DirIndex { get; }
            public int PtrsPerExtent { get; }
            public string FileName { get; private set; } = string.Empty;

            private GroupBool mDirtyFlag;

            public Extent(int dirIndex, int ptrsPerExtent, GroupBool dirtyFlag) {
                Debug.Assert(dirIndex >= 0);
                Debug.Assert(ptrsPerExtent == 8 || ptrsPerExtent == 16);
                DirIndex = dirIndex;
                PtrsPerExtent = ptrsPerExtent;
                mDirtyFlag = dirtyFlag;
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
                    FileName = CookFileName(mRawFileName);
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
        /// Reference to filesystem object.
        /// </summary>
        public CPM FileSystem { get; private set; }

        /// <summary>
        /// List of extents associated with this file.  There will be at least one entry here.
        /// The list is kept sorted by extent number.  There will be gaps for sparse files.
        /// </summary>
        private List<Extent> mExtentList = new List<Extent>();
        internal Extent GetExtent(int extNum) {
            // TODO: handle sparse files
            return mExtentList[extNum];
        }

        /// <summary>
        /// True if this is the "fake" volume directory object.
        /// </summary>
        private bool IsVolumeDirectory { get; set; }

        private string mVolDirName;

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
            if (fileSystem.AllocUnitSize == 1024) {
                mVolDirName = "5.25\" disk";
            } else {
                mVolDirName = "3.5\" disk";
            }
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
            VolumeUsage vu = fileSystem.VolUsage!;
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
            CPM_FileEntry? newEntry = null;
            foreach (Extent ext in sortex.Keys) {
                if (ext.Status > CPM.MAX_USER_NUM) {
                    // Don't create a file entry for these.
                    // TODO(someday): extract v3 date info and disc label
                    continue;
                }

                if (ext.Status == prevStatus && ext.FileName == prevFileName) {
                    // Add this extent to the entry in progress.  It should not be possible to
                    // find a duplicate here, since we didn't add those to the sorted list.
                    Debug.Assert(newEntry != null);
                    Debug.Assert(!newEntry.mExtentList.Contains(ext));
                    newEntry.mExtentList.Add(ext);
                } else {
                    // Create a new file entry.
                    newEntry = new CPM_FileEntry(fileSystem);
                    newEntry.mExtentList.Add(ext);
                    volDir.ChildList.Add(newEntry);

                    prevStatus = ext.Status;
                    prevFileName = ext.FileName;
                }
            }

            // The outcome of the above is a fully-sorted list, but we want the child list to be
            // arranged according to the first appearance of each file in the directory to match
            // the output of "DIR".  Some CP/M utilities created sorted lists, so this step may
            // not be necessary or desirable.
            volDir.ChildList.Sort(delegate (IFileEntry ientry1, IFileEntry ientry2) {
                CPM_FileEntry entry1 = (CPM_FileEntry)ientry1;
                CPM_FileEntry entry2 = (CPM_FileEntry)ientry2;
                int index1 = int.MaxValue, index2 = int.MaxValue;
                //foreach (Extent ext in entry1.mExtentList) {
                //    if (index1 > ext.DirIndex) {
                //        index1 = ext.DirIndex;
                //    }
                //}
                //foreach (Extent ext in entry2.mExtentList) {
                //    if (index2 > ext.DirIndex) {
                //        index2 = ext.DirIndex;
                //    }
                //}
                index1 = entry1.mExtentList[0].DirIndex;
                index2 = entry2.mExtentList[0].DirIndex;
                return (index1 - index2);
            });

            return volDir;
        }

        /// <summary>
        /// Creates a "fake" file entry for the volume directory.
        /// </summary>
        private static CPM_FileEntry CreateFakeVolDirEntry(CPM fs) {
            CPM_FileEntry newEntry = new CPM_FileEntry(fs);
            newEntry.IsVolumeDirectory = true;
            //newEntry.mExtentList.Add(new Extent(0, 16, new GroupBool()));
            return newEntry;
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
            @"((?:\.)((?![<>\.,;:=\?\*\[\] ])[\x20-\x7e]){1,3})?$";
        private static Regex sFileNameRegex = new Regex(FILE_NAME_PATTERN);

        // IFileEntry
        public int CompareFileName(string fileName) {
            return string.Compare(FileName, fileName, StringComparison.OrdinalIgnoreCase);
        }

        // IFileEntry
        public int CompareFileName(string fileName, char fileNameSeparator) {
            return string.Compare(FileName, fileName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Returns true if the string is a valid ProDOS filename.
        /// </summary>
        public static bool IsFileNameValid(string fileName) {
            MatchCollection matches = sFileNameRegex.Matches(fileName);
            return (matches.Count == 1);
        }

        public static string AdjustFileName(string fileName) {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Converts an 11-byte raw filename to a "cooked" 8.3 filename.  Padding spaces are
        /// excluded.  If the extension is "   ", the '.' and extension will be omitted.
        /// </summary>
        /// <param name="rawFileName">Raw filename bytes.</param>
        /// <returns>Cooked filename string.</returns>
        internal static string CookFileName(byte[] rawFileName) {
            Debug.Assert(rawFileName.Length == Extent.FILENAME_FIELD_LEN);
            StringBuilder sb = new StringBuilder(Extent.FILENAME_FIELD_LEN + 1);
            for (int i = 0; i < 8; i++) {
                int ch = rawFileName[i] & 0x7f;
                if (ch == ' ') {
                    break;
                }
                sb.Append((char)ch);
            }
            if (rawFileName[8] != ' ') {
                sb.Append('.');
                for (int i = 0; i < 3; i++) {
                    int ch = rawFileName[8 + i] & 0x7f;
                    if (ch == ' ') {
                        break;
                    }
                    sb.Append((char)ch);
                }
            }
            return sb.ToString();
        }

        #endregion Filenames

        public override string ToString() {
            string fileName;
            if (IsVolumeDirectory) {
                fileName = mVolDirName;
            } else if (mExtentList.Count > 0) {
                fileName = mExtentList[0].FileName;
            } else {
                fileName = "!BAD!";
            }
            return "[CP/M file entry: '" + fileName + "']";
        }
    }
}
