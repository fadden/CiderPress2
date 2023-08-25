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
    public class RDOS_FileEntry : IFileEntryExt, IDisposable {
        //
        // IFileEntry interfaces.
        //

        public bool IsValid { get { return FileSystem != null; } }

        public bool IsDubious { get; private set; }
        public bool IsDamaged { get; private set; }

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
        public ushort AuxType {
            get {
                // Don't return the load address for text files, as it's usually $B100, which
                // confuses the file viewer into thinking it's a random-access text file.  The
                // value is similarly useless for Applesoft files, but it's harmless there.
                if (mFileType == TYPE_T) {
                    return 0;
                } else {
                    return mLoadAddr;
                }
            }
            set => throw new IOException();
        }

        public bool HasHFSTypes { get { return false; } }
        public uint HFSFileType { get { return 0; } set { } }
        public uint HFSCreator { get { return 0; } set { } }

        public byte Access {
            get => FileAttribs.FILE_ACCESS_UNLOCKED;
            set => throw new IOException();
        }
        public DateTime CreateWhen { get => TimeStamp.NO_DATE; set { } }
        public DateTime ModWhen { get => TimeStamp.NO_DATE; set { } }

        public long StorageSize => mSectorCount * SECTOR_SIZE;

        public long DataLength => mFileLength;

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

        // Raw values from disk.
        private byte[] mRawFileName = new byte[RDOS.MAX_FILENAME_LEN];
        private byte mFileType;
        private byte mSectorCount;
        private ushort mLoadAddr;
        private ushort mFileLength;
        private ushort mStartIndex;

        // Cooked filename.
        private string mFileName = string.Empty;

        public ushort StartIndex => mStartIndex;
        public byte SectorCount => mSectorCount;

        /// <summary>
        /// Reference to filesystem object.
        /// </summary>
        public RDOS FileSystem { get; private set; }

        /// <summary>
        /// List of files contained in this entry.  Only the volume dir has children.
        /// </summary>
        internal List<IFileEntry> ChildList { get; } = new List<IFileEntry>();

        /// <summary>
        /// True if the file shares storage with another file or the system.
        /// </summary>
        private bool mHasConflict;

        /// <summary>
        /// Converts an RDOS type to its ProDOS equivalent.
        /// </summary>
        public static byte TypeToProDOS(byte fileType) {
            switch (fileType) {
                case TYPE_A:
                    return FileAttribs.FILE_TYPE_BAS;
                case TYPE_B:
                    return FileAttribs.FILE_TYPE_BIN;
                case TYPE_T:
                    return FileAttribs.FILE_TYPE_TXT;
                case TYPE_S:
                default:
                    return FileAttribs.FILE_TYPE_NON;
            }
        }
        private const byte TYPE_A = 'A' | 0x80;
        private const byte TYPE_B = 'B' | 0x80;
        private const byte TYPE_S = 'S' | 0x80;
        private const byte TYPE_T = 'T' | 0x80;


        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="fileSystem">Filesystem this file entry is a part of.</param>
        internal RDOS_FileEntry(RDOS fileSystem) {
            ContainingDir = IFileEntry.NO_ENTRY;
            FileSystem = fileSystem;
        }

        // IDisposable generic finalizer.
        ~RDOS_FileEntry() {
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
        internal static IFileEntry ScanDirectory(RDOS fs) {
            RDOS_FileEntry volDir = CreateFakeVolDirEntry(fs);
            VolumeUsage vu = fs.VolUsage!;

            byte[] sctBuf = new byte[SECTOR_SIZE];

            uint prevStart = 0;
            for (uint sct = 0; sct < fs.NumCatSectors; sct++) {
                fs.ChunkAccess.ReadSector(RDOS.CAT_TRACK, sct, sctBuf, 0, fs.FSOrder);
                bool done = false;
                for (int offset = 0; offset < SECTOR_SIZE; offset += RDOS.CAT_ENTRY_LEN) {
                    if (sctBuf[offset] == 0x00) {
                        done = true;
                        break;          // end of catalog
                    } else if (sctBuf[offset] == 0x80) {
                        continue;       // deleted entry
                    }

                    RDOS_FileEntry newEntry = CreateEntry(fs, volDir, sctBuf, offset,
                        sctBuf[offset + 0x18], sctBuf[offset + 0x19],
                        RawData.GetU16LE(sctBuf, offset + 0x1a),
                        RawData.GetU16LE(sctBuf, offset + 0x1c),
                        RawData.GetU16LE(sctBuf, offset + 0x1e));
                    newEntry.ContainingDir = volDir;

                    if (!newEntry.Validate(fs.Notes)) {
                        fs.IsDubious = true;
                    } else {
                        for (uint sctIdx = newEntry.mStartIndex;
                                sctIdx < newEntry.mStartIndex + newEntry.mSectorCount; sctIdx++) {
                            vu.MarkInUse(sctIdx);
                            vu.SetUsage(sctIdx, newEntry);
                        }
                    }
                    if (newEntry.mStartIndex < prevStart) {
                        // Not sure if this matters, but it would affect the algorithm that
                        // finds an open space to create a file.
                        fs.Notes.AddW("File entries are out of order");
                    }

                    volDir.ChildList.Add(newEntry);

                    prevStart = newEntry.mStartIndex;
                }
                if (done) {
                    break;      // stop at first $00 entry
                }
            }

            return volDir;
        }

        private static RDOS_FileEntry CreateEntry(RDOS fs, IFileEntry parentDir, byte[] rawFileName,
                int rawFileNameOffset, byte fileType, byte sectorCount, ushort loadAddr,
                ushort fileLength, ushort startIndex) {
            RDOS_FileEntry newEntry = new RDOS_FileEntry(fs);
            newEntry.ContainingDir = parentDir;
            newEntry.mRawFileName = new byte[RDOS.MAX_FILENAME_LEN];
            Array.Copy(rawFileName, rawFileNameOffset, newEntry.mRawFileName, 0,
                RDOS.MAX_FILENAME_LEN);
            newEntry.mFileType = fileType;
            newEntry.mSectorCount = sectorCount;
            newEntry.mLoadAddr = loadAddr;
            newEntry.mFileLength = fileLength;
            newEntry.mStartIndex = startIndex;

            newEntry.mFileName = CookFileName(rawFileName, rawFileNameOffset);
            return newEntry;
        }

        /// <summary>
        /// Validates a directory entry.
        /// </summary>
        /// <param name="notes">Notes object.</param>
        /// <returns>True if all is well.</returns>
        private bool Validate(Notes notes) {
            for (int i = 0; i < mRawFileName.Length; i++) {
                if (mRawFileName[i] < 0xa0 || mRawFileName[i] >= 0xff) {
                    notes.AddW("Invalid bytes in filename '" + mFileName + "'");
                    // not fatal
                    break;
                }
            }
            if (mFileType != TYPE_A && mFileType != TYPE_B && mFileType != TYPE_S &&
                    mFileType != TYPE_T) {
                notes.AddW("Invalid file type 0x" + mFileType.ToString("x2") + ": " +
                    mFileName);
                // not fatal
            }
            if (mSectorCount == 0) {
                notes.AddW("Zero-length file: " + mFileName);
                IsDamaged = true;
                return false;
            }
            if (mFileLength > mSectorCount * SECTOR_SIZE) {
                notes.AddW("Invalid file length " + mFileLength + " (count=" + mSectorCount +
                    "): " + mFileName);
                IsDubious = true;
                return false;
            }
            if (mStartIndex >= FileSystem.TotalSectors) {
                notes.AddE("Invalid start index " + mStartIndex + ": " + mFileName);
                IsDamaged = true;
                return false;
            }

            if (mStartIndex + mSectorCount > FileSystem.TotalSectors) {
                notes.AddE("Invalid start + count: " + mFileName);
                IsDamaged = true;
                return false;
            }
            return true;
        }

        private static readonly byte[] RAW33_NAME = MakeRawName("RDOS 3.3");
        private static readonly byte[] RAW32_NAME = MakeRawName("RDOS 3.2");
        private static readonly byte[] RAW3_NAME = MakeRawName("RDOS 3");

        private static RDOS_FileEntry CreateFakeVolDirEntry(RDOS fs) {
            byte[] rawName;
            switch (fs.Flavor) {
                case RDOS.RDOSFlavor.RDOS33: rawName = RAW33_NAME; break;
                case RDOS.RDOSFlavor.RDOS32: rawName = RAW32_NAME; break;
                case RDOS.RDOSFlavor.RDOS3: rawName = RAW3_NAME; break;
                default: throw new NotImplementedException();
            }
            RDOS_FileEntry newEntry =
                CreateEntry(fs, IFileEntry.NO_ENTRY, rawName, 0, (byte)'D', 0, 0, 0, 0);
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
        /// Converts a raw filename to string form.
        /// </summary>
        private static string CookFileName(byte[] rawFileName, int offset) {
            Debug.Assert(rawFileName.Length - offset >= RDOS.MAX_FILENAME_LEN);
            StringBuilder sb = new StringBuilder(RDOS.MAX_FILENAME_LEN);
            for (int i = 0; i < RDOS.MAX_FILENAME_LEN; i++) {
                sb.Append((char)(rawFileName[offset + i] & 0x7f));
            }
            return sb.ToString().TrimEnd();     // trim trailing spaces
        }

        /// <summary>
        /// Converts a filename in string form to raw bytes.
        /// </summary>
        private static byte[] MakeRawName(string fileName) {
            Debug.Assert(fileName.Length <= RDOS.MAX_FILENAME_LEN);
            byte[] rawBuf = new byte[RDOS.MAX_FILENAME_LEN];
            int i;
            for (i = 0; i < fileName.Length; i++) {
                rawBuf[i] = (byte)(fileName[i] | 0x80);
            }
            for (; i < RDOS.MAX_FILENAME_LEN; i++) {
                rawBuf[i] = ' ' | 0x80;
            }
            return rawBuf;
        }

        #endregion Filenames

        public override string ToString() {
            return "[RDOS file entry: '" + mFileName + "']";
        }
    }
}
