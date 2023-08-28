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

using CommonUtil;
using static DiskArc.Defs;
using static DiskArc.IFileSystem;

namespace DiskArc.FS {
    public class MFS_FileEntry : IFileEntryExt, IDisposable {
        internal const int ENTRY_BASE_LEN = 50;         // 50 bytes plus the filename
        internal const int FINDER_INFO_LEN = 16;        // FInfo struct

        //
        // IFileEntry interfaces.
        //

        public bool IsValid { get { return FileSystem != null; } }

        public bool IsDubious => mHasConflict || HasBadLink;
        public bool IsDamaged { get; internal set; }

        public bool IsDirectory { get; private set; }
        public bool HasDataFork => true;            // both forks are always present, but they
        public bool HasRsrcFork => true;            //  might have zero length
        public bool IsDiskImage => false;

        public IFileEntry ContainingDir { get; private set; }

        public int Count => ChildList.Count;        // fake vol dir only

        public string FileName {
            get => mFileName;
            set => throw new IOException();
        }
        public char DirectorySeparatorChar { get => IFileEntry.NO_DIR_SEP; set { } }
        public string FullPathName {
            get {
                string pathName = mFileName;
                if (FileSystem == null) {
                    pathName = "!INVALID! was:" + pathName;
                }
                return pathName;
            }
        }
        public byte[] RawFileName {
            get {
                byte[] result = new byte[mFileName.Length];
                Array.Copy(mRawFileName, 1, result, 0, result.Length);
                return result;
            }
            set => throw new IOException();
        }

        public bool HasProDOSTypes => false;

        public byte FileType { get => 0; set { } }
        public ushort AuxType { get => 0; set { } }

        public bool HasHFSTypes => true;
        public uint HFSFileType {
            get { return RawData.GetU32BE(mFinderInfo, 0); }    // fdType
            set => throw new IOException();
        }
        public uint HFSCreator {
            get { return RawData.GetU32BE(mFinderInfo, 4); }    // fdCreator
            set => throw new IOException();
        }

        public byte Access {
            get {
                return (mFlags & (byte)FileFlags.Locked) != 0 ?
                    FileAttribs.FILE_ACCESS_LOCKED : FileAttribs.FILE_ACCESS_UNLOCKED;
            }
            set => throw new IOException();
        }
        public DateTime CreateWhen {
            get { return TimeStamp.ConvertDateTime_HFS(mCreateWhen); }
            set => throw new IOException();
        }
        public DateTime ModWhen {
            get { return TimeStamp.ConvertDateTime_HFS(mModWhen); }
            set => throw new IOException();
        }

        public long StorageSize => mDataPhysicalLen + mRsrcPhysicalLen;

        public long DataLength => mDataLogicalLen;

        public long RsrcLength => mRsrcLogicalLen;

        public string Comment { get => string.Empty; set { } }

        public bool GetPartInfo(FilePart part, out long length, out long storageSize,
                out CompressionFormat format) {
            format = CompressionFormat.Uncompressed;
            if (part == FilePart.DataFork || part == FilePart.RawData) {
                length = mDataLogicalLen;
                storageSize = mDataPhysicalLen;
                return true;
            } else if (part == FilePart.RsrcFork) {
                length = mRsrcLogicalLen;
                storageSize = mRsrcPhysicalLen;
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

        //
        // MFS file entry values
        //

        [Flags]
        public enum FileFlags : byte {
            Locked = 0x01,
            EntryUsed = 0x80,
        }

        private byte mFlags;                    // flFlags
        private byte mVersion;                  // flTyp
        private byte[] mFinderInfo = new byte[FINDER_INFO_LEN]; // flUsrWds
        private uint mFileNumber;               // flFlNum
        private ushort mDataStartBlock;         // flStBlk
        private uint mDataLogicalLen;           // flLgLen
        private uint mDataPhysicalLen;          // flPyLen
        private ushort mRsrcStartBlock;         // flRstBlk
        private uint mRsrcLogicalLen;           // flRLgLen
        private uint mRsrcPhysicalLen;          // flRPyLen
        private uint mCreateWhen;               // flCrDat
        private uint mModWhen;                  // flMdDat
        private byte[] mRawFileName = new byte[MFS.MAX_FILE_NAME_LEN + 1];  // flNam

        internal ushort DataStartBlock => mDataStartBlock;
        internal uint DataLogicalLen => mDataLogicalLen;
        internal ushort RsrcStartBlock => mRsrcStartBlock;
        internal uint RsrcLogicalLen => mRsrcLogicalLen;

        //
        // Implementation-specific.
        //

        // Cooked filename.
        private string mFileName = string.Empty;

        /// <summary>
        /// Reference to filesystem object.
        /// </summary>
        public MFS FileSystem { get; private set; }

        /// <summary>
        /// List of files contained in this entry.  Only the volume dir has children.
        /// </summary>
        internal List<IFileEntry> ChildList { get; } = new List<IFileEntry>();

        /// <summary>
        /// True if we've detected (and Noted) a conflict with another file.
        /// </summary>
        private bool mHasConflict;

        /// <summary>
        /// True if the entry has a bad block link in the alloc map.
        /// </summary>
        internal bool HasBadLink { get; set; }


        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="fileSystem">Filesystem this file entry is a part of.</param>
        internal MFS_FileEntry(MFS fileSystem) {
            ContainingDir = IFileEntry.NO_ENTRY;
            FileSystem = fileSystem;
        }

        // IDisposable generic finalizer.
        ~MFS_FileEntry() {
            Dispose(false);
        }
        // IDisposable generic Dispose() implementation.
        public void Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        protected virtual void Dispose(bool disposing) {
            // nothing to do
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
        /// Scans the volume directory.
        /// </summary>
        /// <param name="fs">Filesystem object.</param>
        /// <returns>Volume directory file entry.</returns>
        /// <exception cref="IOException">Disk access failure.</exception>
        internal static IFileEntry ScanDirectory(MFS fs, bool doFullScan) {
            MFS_MDB mdb = fs.VolMDB!;

            MFS_FileEntry volDir = CreateFakeVolDirEntry(fs);

            byte[] blkBuf = new byte[BLOCK_SIZE];
            // Walk through all the directory blocks.
            for (uint blk = mdb.DirectoryStart; blk < mdb.DirectoryStart + mdb.DirectoryLength;
                    blk++) {
                fs.ChunkAccess.ReadBlock(blk, blkBuf, 0);

                // Walk through the entries in this block.
                int offset = 0;
                while (offset < BLOCK_SIZE) {
                    MFS_FileEntry? newEntry = ExtractFileEntry(fs, blk, blkBuf, ref offset);
                    if (newEntry == null) {
                        break;          // no more in this block
                    }
                    if ((offset & 0x01) != 0) {
                        offset++;       // align to 16-bit word
                    }
                    Debug.Assert(offset <= BLOCK_SIZE);

                    newEntry.ContainingDir = volDir;
                    volDir.ChildList.Add(newEntry);

                    if (newEntry.mDataStartBlock == 1 ||
                            newEntry.mDataStartBlock >= mdb.NumAllocBlocks) {
                        fs.Notes.AddW("Bad data start block " + newEntry.mDataStartBlock + ": '" +
                            newEntry.FileName + "'");
                        newEntry.IsDamaged = true;
                    }
                    if (newEntry.mRsrcStartBlock == 1 ||
                            newEntry.mRsrcStartBlock >= mdb.NumAllocBlocks) {
                        fs.Notes.AddW("Bad rsrc start block " + newEntry.mRsrcStartBlock + ": '" +
                            newEntry.FileName + "'");
                        newEntry.IsDamaged = true;
                    }

                    if (doFullScan && !newEntry.IsDamaged) {
                        Debug.Assert(mdb.VolUsage != null);
                        if (newEntry.mDataStartBlock != 0) {
                            using (MFS_FileDesc fd = MFS_FileDesc.CreateFD(newEntry,
                                    FileAccessMode.ReadOnly, FilePart.DataFork, true)) {
                                fd.SetVolumeUsage();
                                if (!newEntry.IsDubious && !newEntry.IsDamaged) {
                                    if (fd.GetBlocksUsed() * mdb.AllocBlockSize !=
                                            newEntry.mDataPhysicalLen) {
                                        fs.Notes.AddW("Data fork physical length is incorrect " +
                                            " for '" + newEntry.mFileName + "': len=" +
                                            newEntry.mDataPhysicalLen + " blocks=" +
                                            fd.GetBlocksUsed());
                                    }
                                }
                            }
                        }
                        if (newEntry.mRsrcStartBlock != 0) {
                            using (MFS_FileDesc fd = MFS_FileDesc.CreateFD(newEntry,
                                    FileAccessMode.ReadOnly, FilePart.RsrcFork, true)) {
                                fd.SetVolumeUsage();
                                if (!newEntry.IsDubious && !newEntry.IsDamaged) {
                                    if (fd.GetBlocksUsed() * mdb.AllocBlockSize !=
                                            newEntry.mRsrcPhysicalLen) {
                                        fs.Notes.AddW("Rsrc fork physical length is incorrect " +
                                            " for '" + newEntry.mFileName + "': len=" +
                                            newEntry.mRsrcPhysicalLen + " blocks=" +
                                            fd.GetBlocksUsed());
                                    }
                                }
                            }
                        }
                    }
                }
            }
            return volDir;
        }

        private static MFS_FileEntry? ExtractFileEntry(MFS fs, uint blk,
                byte[] blkBuf, ref int offset) {
            if (blkBuf.Length - offset < FINDER_INFO_LEN + 1) {
                return null;        // not enough room left
            }
            if ((blkBuf[offset] & (byte)FileFlags.EntryUsed) == 0) {
                return null;        // no more entries in this block
            }
            int startOffset = offset;

            MFS_FileEntry newEntry = new MFS_FileEntry(fs);
            newEntry.mFlags = blkBuf[offset++];
            newEntry.mVersion = blkBuf[offset++];
            Array.Copy(blkBuf, offset, newEntry.mFinderInfo, 0, FINDER_INFO_LEN);
            offset += FINDER_INFO_LEN;
            newEntry.mFileNumber = RawData.ReadU32BE(blkBuf, ref offset);
            newEntry.mDataStartBlock = RawData.ReadU16BE(blkBuf, ref offset);
            newEntry.mDataLogicalLen = RawData.ReadU32BE(blkBuf, ref offset);
            newEntry.mDataPhysicalLen = RawData.ReadU32BE(blkBuf, ref offset);
            newEntry.mRsrcStartBlock = RawData.ReadU16BE(blkBuf, ref offset);
            newEntry.mRsrcLogicalLen = RawData.ReadU32BE(blkBuf, ref offset);
            newEntry.mRsrcPhysicalLen = RawData.ReadU32BE(blkBuf, ref offset);
            newEntry.mCreateWhen = RawData.ReadU32BE(blkBuf, ref offset);
            newEntry.mModWhen = RawData.ReadU32BE(blkBuf, ref offset);
            Debug.Assert(offset - startOffset == ENTRY_BASE_LEN);

            byte nameLen = blkBuf[offset++];
            if (offset + nameLen > blkBuf.Length) {
                fs.Notes.AddW("Directory entry ran off end of block " + blk);
                return null;
            }
            newEntry.mRawFileName[0] = nameLen;
            Array.Copy(blkBuf, offset, newEntry.mRawFileName, 1, nameLen);
            offset += nameLen;

            newEntry.mFileName = GenerateCookedName(newEntry.mRawFileName, false);
            return newEntry;
        }

        private static MFS_FileEntry CreateFakeVolDirEntry(MFS fs) {
            Debug.Assert(fs.VolMDB != null);

            MFS_FileEntry volDir = new MFS_FileEntry(fs);
            volDir.IsDirectory = true;
            volDir.mFileName = fs.VolMDB.VolumeName;
            return volDir;
        }

        // IFileEntry
        public void SaveChanges() { }

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

        // IFileEntry
        public int CompareFileName(string fileName) {
            return MacChar.CompareHFSFileNames(mFileName, fileName);
        }

        // IFileEntry
        public int CompareFileName(string fileName, char fileNameSeparator) {
            return MacChar.CompareHFSFileNames(mFileName, fileName);
        }

        /// <summary>
        /// Generates the "cooked" form of the file or volume name.
        /// </summary>
        /// <remarks>
        /// <para>MFS allows all characters, with a minimum length of 1 character and a
        /// maximum length of 255 characters.  Volume names follow the same rules, but are
        /// limited to 27 characters.</para>
        /// <para>We convert control characters to printable glyphs for display.</para>
        /// </remarks>
        /// <param name="rawFileName">Raw filename data (variable length).</param>
        /// <returns>Filename string, or the empty string if name has an invalid length.  We
        ///   don't test for invalid characters here.</returns>
        public static string GenerateCookedName(byte[] rawFileName, bool isVolumeName) {
            int maxNameLen = isVolumeName ? MFS.MAX_VOL_NAME_LEN : MFS.MAX_FILE_NAME_LEN;

            byte nameLen = rawFileName[0];
            if (nameLen < 1 || nameLen > maxNameLen || nameLen >= rawFileName.Length) {
                Debug.WriteLine("Invalid raw MFS filename");
                return string.Empty;
            }

            return MacChar.MacToUnicode(rawFileName, 1, nameLen, MacChar.Encoding.RomanShowCtrl);
        }

        #endregion Filenames

        public override string ToString() {
            return "[MFS file entry: '" + mFileName + "']";
        }
    }
}
