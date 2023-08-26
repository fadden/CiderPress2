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

using CommonUtil;
using static DiskArc.Defs;

namespace DiskArc.FS {
    public class Gutenberg_FileEntry : IFileEntryExt, IDisposable {
        internal const int CAT_HEADER_LEN = 16;
        internal const int CAT_ENTRY_LEN = 16;

        //
        // IFileEntry interfaces.
        //

        public bool IsValid { get { return FileSystem != null; } }

        public bool IsDubious => mHasConflict || HasBadLink;
        public bool IsDamaged { get; internal set; }

        public bool IsDirectory { get; private set; }
        public bool HasDataFork => true;
        public bool HasRsrcFork => false;
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
                string pathName;
                pathName = mFileName;
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

        public bool HasProDOSTypes => true;

        public byte FileType {
            get {
                if (IsDirectory) {
                    return FileAttribs.FILE_TYPE_DIR;
                } else {
                    return TypeToProDOS(mFileType);
                }
            }
            set => throw new IOException();
        }
        public ushort AuxType { get { return 0; } set { } }

        public bool HasHFSTypes { get { return false; } }
        public uint HFSFileType { get { return 0; } set { } }
        public uint HFSCreator { get { return 0; } set { } }

        public byte Access {
            get {
                return (mFileType == TYPE_L ?
                    FileAttribs.FILE_ACCESS_LOCKED : FileAttribs.FILE_ACCESS_UNLOCKED);
            }
            set => throw new IOException();
        }
        public DateTime CreateWhen { get => TimeStamp.NO_DATE; set { } }
        public DateTime ModWhen { get => TimeStamp.NO_DATE; set { } }

        public long StorageSize => mNumSectorsUsed * SECTOR_SIZE;

        public long DataLength => mNumSectorsUsed * (SECTOR_SIZE - Gutenberg.SECTOR_HEADER_LEN);

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

        //
        // Implementation-specific.
        //

        private byte[] mRawFileName = new byte[Gutenberg.MAX_FILENAME_LEN];
        private byte mFirstTrack;
        private byte mFirstSector;
        private byte mFileType;

        // Cooked filename.
        private string mFileName = string.Empty;

        // Number of sectors used by the file.
        private int mNumSectorsUsed;

        /// <summary>
        /// Reference to filesystem object.
        /// </summary>
        public Gutenberg FileSystem { get; private set; }

        internal byte FirstTrack => mFirstTrack;
        internal byte FirstSector => mFirstSector;

        /// <summary>
        /// List of files contained in this entry.  Only the volume dir has children.
        /// </summary>
        internal List<IFileEntry> ChildList { get; } = new List<IFileEntry>();

        /// <summary>
        /// True if the file shares storage with another file or the system.
        /// </summary>
        private bool mHasConflict;

        /// <summary>
        /// True if the entry has a bad link in one of its sectors.
        /// </summary>
        internal bool HasBadLink { get; set; }

        /// <summary>
        /// Converts an Gutenberg type to its ProDOS equivalent.
        /// </summary>
        public static byte TypeToProDOS(byte fileType) {
            switch (fileType) {
                case TYPE_L:
                case TYPE_SP:
                    return FileAttribs.FILE_TYPE_TXT;
                case TYPE_M:
                    //return FileAttribs.FILE_TYPE_F2;
                case TYPE_P:
                    return FileAttribs.FILE_TYPE_BIN;
                default:
                    return FileAttribs.FILE_TYPE_NON;
            }
        }
        private const byte TYPE_L = 'L' | 0x80;
        private const byte TYPE_M = 'M' | 0x80;
        private const byte TYPE_P = 'P' | 0x80;
        private const byte TYPE_SP = ' ' | 0x80;


        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="fileSystem">Filesystem this file entry is a part of.</param>
        internal Gutenberg_FileEntry(Gutenberg fileSystem) {
            ContainingDir = IFileEntry.NO_ENTRY;
            FileSystem = fileSystem;
        }

        // IDisposable generic Dispose() implementation.
        public void Dispose() {
            //Dispose(true);
            GC.SuppressFinalize(this);
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
        internal static IFileEntry ScanDirectory(Gutenberg fs) {
            VolumeUsage vu = fs.VolUsage!;
            Notes notes = fs.Notes;
            byte[] sctBuf = new byte[SECTOR_SIZE];

            // Read the first sector.
            uint catTrk = Gutenberg.FIRST_CAT_TRACK;
            uint catSct = Gutenberg.FIRST_CAT_SECTOR;
            fs.ChunkAccess.ReadSector(catTrk, catSct, sctBuf, 0);

            // Use the volume name to create the volume directory entry.
            Gutenberg_FileEntry volDir =
                CreateFakeVolDirEntry(fs, sctBuf, Gutenberg.SECTOR_HEADER_LEN);

            int iterations = 0;
            while (iterations++ < Gutenberg.MAX_CAT_SECTORS) {
                byte curTrk = sctBuf[0x02];
                byte curSct = sctBuf[0x03];
                if (curTrk != catTrk || (curSct & 0x7f) != catSct) {
                    notes.AddE("Bad catalog self-reference: T" + curTrk + " &S" + (curSct & 0x7f));
                    fs.IsDubious = true;
                    // continue anyway
                }

                for (int offset = CAT_HEADER_LEN; offset < SECTOR_SIZE; offset += CAT_ENTRY_LEN) {
                    if (sctBuf[offset] != 0xa0) {
                        byte firstTrack = sctBuf[offset + 0x0c];
                        byte firstSector = sctBuf[offset + 0x0d];
                        byte fileType = sctBuf[offset + 0x0e];
                        if (firstTrack == 0x40) {
                            firstTrack = 0;
                        }
                        if (firstSector == 0x40) {
                            firstSector = 0;
                        }
                        if (firstTrack >= fs.ChunkAccess.NumTracks ||
                                firstSector >= fs.ChunkAccess.NumSectorsPerTrack ||
                                sctBuf[offset + 0x0f] != 0x8d) {
                            notes.AddE("Bad data in catalog sector T" + catTrk + " S" + catSct);
                            fs.IsDubious = true;
                            break;
                        }
                        if (fileType != TYPE_SP && fileType != TYPE_L && fileType != TYPE_M &&
                                fileType != TYPE_P) {
                            // It's possible there are file types we don't know about, so just
                            // make this a warning.
                            notes.AddW("Unexpected file type $" + fileType.ToString("x2"));
                        }

                        Gutenberg_FileEntry newEntry = CreateEntry(fs, volDir, sctBuf, offset,
                            Gutenberg.MAX_FILENAME_LEN, firstTrack, firstSector, fileType);
                        volDir.ChildList.Add(newEntry);

                        try {
                            using (Gutenberg_FileDesc fd = Gutenberg_FileDesc.CreateFD(newEntry,
                                    IFileSystem.FileAccessMode.ReadOnly, FilePart.DataFork, true)) {
                                fd.SetVolumeUsage();
                                newEntry.mNumSectorsUsed = fd.GetSectorsUsed();
                            }
                        } catch {
                            newEntry.IsDamaged = true;
                        }
                    }
                }

                catTrk = sctBuf[0x04];
                catSct = sctBuf[0x05];
                if ((catTrk & 0x80) != 0) {
                    // Reached end of list.
                    break;
                }
                if (catTrk >= fs.ChunkAccess.NumTracks ||
                        catSct >= fs.ChunkAccess.NumSectorsPerTrack) {
                    // Stop here, keep what we scanned so far.
                    notes.AddE("Bad catalog T/S link: T" + catTrk + " S" + catSct);
                    fs.IsDubious = true;
                    break;
                }
                fs.ChunkAccess.ReadSector(catTrk, catSct, sctBuf, 0);
            }
            if (iterations == Gutenberg.MAX_CAT_SECTORS) {
                volDir.Dispose();
                throw new DAException("infinite loop in catalog");
            }

            return volDir;
        }

        private static Gutenberg_FileEntry CreateEntry(Gutenberg fs, IFileEntry parentDir,
                byte[] rawFileName, int rawFileNameOffset, int rawFileNameLen,
                byte firstTrack, byte firstSector, byte fileType) {
            Gutenberg_FileEntry newEntry = new Gutenberg_FileEntry(fs);
            newEntry.ContainingDir = parentDir;
            newEntry.mRawFileName = new byte[Gutenberg.MAX_FILENAME_LEN];
            Array.Copy(rawFileName, rawFileNameOffset, newEntry.mRawFileName, 0,
                Gutenberg.MAX_FILENAME_LEN);
            newEntry.mFirstTrack = firstTrack;
            newEntry.mFirstSector = firstSector;
            newEntry.mFileType = fileType;

            newEntry.mFileName = CookName(rawFileName, rawFileNameOffset, rawFileNameLen);
            return newEntry;
        }

        private static Gutenberg_FileEntry CreateFakeVolDirEntry(Gutenberg fs,
                byte[] rawFileName, int rawFileNameOffset) {
            Gutenberg_FileEntry newEntry = CreateEntry(fs, IFileEntry.NO_ENTRY, rawFileName,
                rawFileNameOffset, Gutenberg.MAX_VOLNAME_LEN, 'D' | 0x80,
                Gutenberg.FIRST_CAT_TRACK, Gutenberg.FIRST_CAT_SECTOR);
            newEntry.IsDirectory = true;
            return newEntry;
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
            return string.Compare(mFileName, fileName, StringComparison.OrdinalIgnoreCase);
        }

        // IFileEntry
        public int CompareFileName(string fileName, char fileNameSeparator) {
            return string.Compare(mFileName, fileName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Converts a raw filename or volume name to string form.
        /// </summary>
        private static string CookName(byte[] rawFileName, int offset, int length) {
            Debug.Assert(rawFileName.Length - offset >= length);
            StringBuilder sb = new StringBuilder(length);
            for (int i = 0; i < length; i++) {
                sb.Append((char)(rawFileName[offset + i] & 0x7f));
            }
            return sb.ToString().TrimEnd();     // trim trailing spaces
        }

        #endregion Filenames

        public override string ToString() {
            return "[Gutenberg file entry: '" + mFileName + "']";
        }
    }
}
