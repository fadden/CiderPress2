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
using System.Text.RegularExpressions;

using CommonUtil;
using static DiskArc.Defs;

namespace DiskArc.FS {
    /// <summary>
    /// Apple Pascal filesystem entry.
    /// </summary>
    /// <remarks>
    /// <para>The Pascal filesystem is unusual in that the directory is treated as a single
    /// file, and there are no gaps between entries.  That means that a given file's attributes
    /// are not fixed in place, even when the file is open.  If an earlier file is deleted, the
    /// directory position could shift.  Because of this, changes to file attributes and
    /// storage must be managed through the filesystem object rather than by directly updating
    /// the directory blocks.</para>
    /// </remarks>
    public class Pascal_FileEntry : IFileEntryExt, IDisposable {
        public const int DIR_ENTRY_LEN = 26;
        public const ushort FILE_TYPE_MASK = 0x000f;

        // Pascal filetype values.
        internal enum FileKind: byte {
            UntypedFile = 0,
            XdskFile = 1,
            CodeFile = 2,
            TextFile = 3,
            InfoFile = 4,
            DataFile = 5,
            GrafFile = 6,
            FotoFile = 7,
            SecureDir = 8,
        }

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

        public int Count { get { return ChildList.Count; } }       // fake vol dir only

        public string FileName {
            get { return mFileName; }
            set {
                CheckChangeAllowed();
                if (IsVolumeDirectory) {
                    if (!IsVolumeNameValid(value)) {
                        throw new ArgumentException("Invalid volume name");
                    }
                } else {
                    if (!IsFileNameValid(value)) {
                        throw new ArgumentException("Invalid filename");
                    }
                }
                mFileName = value.ToUpperInvariant();
                ASCIIUtil.StringToFixedPascalBytes(mFileName, mRawFileName);
                MarkDirty();
            }
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
                byte[] result = new byte[mRawFileName[0]];
                Array.Copy(mRawFileName, 1, result, 0, result.Length);
                return result;
            }
            set {
                CheckChangeAllowed();
                // Minimal syntax checking.
                int maxLen = IsVolumeDirectory ? Pascal.MAX_VOL_NAME_LEN : Pascal.MAX_FILE_NAME_LEN;
                if (value.Length == 0 || value.Length > maxLen) {
                    throw new ArgumentException("Invalid name length (" + value.Length + ")");
                }
                mRawFileName[0] = (byte)value.Length;
                for (int i = 0; i < value.Length; i++) {
                    mRawFileName[i + 1] = value[i];
                }
                mFileName = ASCIIUtil.PascalBytesToString(mRawFileName);
                MarkDirty();
            }
        }

        public bool HasProDOSTypes => true;
        public byte FileType {
            get {
                if (IsVolumeDirectory) {
                    return FileAttribs.FILE_TYPE_DIR;
                } else {
                    return TypeToProDOS(mTypeAndFlags);
                }
            }
            set {
                CheckChangeAllowed();
                ushort newType = TypeFromProDOS(value);
                mTypeAndFlags = (byte)(newType | (mTypeAndFlags & ~FILE_TYPE_MASK));
                MarkDirty();
            }
        }
        public ushort AuxType { get => 0; set { } }

        public bool HasHFSTypes => false;
        public uint HFSFileType { get => 0; set { } }
        public uint HFSCreator { get => 0; set { } }

        public byte Access { get => (byte)ACCESS_UNLOCKED; set { } }
        public DateTime CreateWhen { get => TimeStamp.NO_DATE; set { } }
        public DateTime ModWhen {
            get { return TimeStamp.ConvertDateTime_Pascal(mModWhen); }
            set {
                CheckChangeAllowed();
                mModWhen = TimeStamp.ConvertDateTime_Pascal(value);
                MarkDirty();
            }
        }

        public long StorageSize {
            get { return (mNextBlock - mStartBlock) * BLOCK_SIZE; }
        }
        public long DataLength {
            get { return (mNextBlock - mStartBlock - 1) * BLOCK_SIZE + mByteCount; }
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

        /// <summary>
        /// Marks the file entry as dirty.
        /// </summary>
        /// <remarks>
        /// We don't actually maintain a local "dirty" flag, because any change requires writing
        /// the full directory out.  Instead, notify the filesystem object that changes are
        /// pending.
        /// </remarks>
        internal void MarkDirty() {
            FileSystem.IsVolDirDirty = true;
        }

        //
        // Implementation-specific.
        //

        // Raw data from directory entry.
        private string mFileName = string.Empty;
        private ushort mStartBlock;
        private ushort mNextBlock;
        private ushort mTypeAndFlags;
        private byte[] mRawFileName = new byte[Pascal.MAX_FILE_NAME_LEN + 1];
        private ushort mByteCount;
        private ushort mModWhen;

        /// <summary>
        /// Reference to filesystem object.
        /// </summary>
        public Pascal FileSystem { get; private set; }

        /// <summary>
        /// True if this is the "fake" volume directory object.
        /// </summary>
        private bool IsVolumeDirectory { get; set; }

        /// <summary>
        /// List of files contained in this entry.  Only the volume dir has children.
        /// </summary>
        internal List<IFileEntry> ChildList { get; } = new List<IFileEntry>();

        /// <summary>
        /// True if the file shares storage with another file or the system.
        /// </summary>
        private bool mHasConflict;

        public ushort StartBlock => mStartBlock;
        public ushort NextBlock => mNextBlock;
        public ushort ByteCount {
            get => mByteCount;
            set {
                Debug.Assert(value <= BLOCK_SIZE);
                if (mByteCount != value) {
                    mByteCount = value;
                    MarkDirty();
                }
            }
        }

        /// <summary>
        /// Converts a Pascal file type to its ProDOS equivalent.
        /// </summary>
        public static byte TypeToProDOS(ushort typeAndFlags) {
            switch ((FileKind)(typeAndFlags & FILE_TYPE_MASK)) {
                case FileKind.UntypedFile:
                    return FileAttribs.FILE_TYPE_NON;
                case FileKind.XdskFile:
                    return FileAttribs.FILE_TYPE_BAD;
                case FileKind.CodeFile:
                    return FileAttribs.FILE_TYPE_PCD;
                case FileKind.TextFile:
                    return FileAttribs.FILE_TYPE_PTX;
                case FileKind.InfoFile:
                    return FileAttribs.FILE_TYPE_F3;
                case FileKind.DataFile:
                    return FileAttribs.FILE_TYPE_PDA;
                case FileKind.GrafFile:
                    return FileAttribs.FILE_TYPE_F4;
                case FileKind.FotoFile:
                    return FileAttribs.FILE_TYPE_FOT;
                case FileKind.SecureDir:
                    return FileAttribs.FILE_TYPE_F5;
                default:
                    return FileAttribs.FILE_TYPE_NON;
            }
        }

        /// <summary>
        /// Converts a ProDOS file type to its Pascal equivalent.
        /// </summary>
        public static byte TypeFromProDOS(byte fileType) {
            switch (fileType) {
                case FileAttribs.FILE_TYPE_NON:
                    return (byte)FileKind.UntypedFile;
                case FileAttribs.FILE_TYPE_BAD:
                    return (byte)FileKind.XdskFile;
                case FileAttribs.FILE_TYPE_PCD:
                    return (byte)FileKind.CodeFile;
                case FileAttribs.FILE_TYPE_PTX:
                    return (byte)FileKind.TextFile;
                case FileAttribs.FILE_TYPE_F3:
                    return (byte)FileKind.InfoFile;
                case FileAttribs.FILE_TYPE_PDA:
                    return (byte)FileKind.DataFile;
                case FileAttribs.FILE_TYPE_F4:
                    return (byte)FileKind.GrafFile;
                case FileAttribs.FILE_TYPE_FOT:
                    return (byte)FileKind.FotoFile;
                case FileAttribs.FILE_TYPE_F5:
                    return (byte)FileKind.SecureDir;
                default:
                    return (byte)FileKind.UntypedFile;
            }
        }

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="fileSystem">Filesystem this file entry is a part of.</param>
        internal Pascal_FileEntry(Pascal fileSystem) {
            ContainingDir = IFileEntry.NO_ENTRY;
            FileSystem = fileSystem;
        }

        // IDisposable generic finalizer.
        ~Pascal_FileEntry() {
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
        /// Scans the volume directory.
        /// </summary>
        /// <remarks>
        /// <para>We don't keep the raw volume directory in memory, and when we flush changes
        /// we write the entire thing, so it's essential that all values in the directory entries
        /// be captured.</para>
        /// </remarks>
        /// <param name="fileSystem">Filesystem object.</param>
        /// <param name="hdr">Parsed volume directory header.</param>
        /// <returns>Volume directory file entry.</returns>
        /// <exception cref="IOException">Disk access failure.</exception>
        internal static IFileEntry ScanDirectory(Pascal fileSystem, Pascal.VolDirHeader hdr) {
            Pascal_FileEntry volDir = CreateFakeVolDirEntry(fileSystem, hdr);
            VolumeUsage vu = fileSystem.VolUsage!;
            Notes notes = fileSystem.Notes;

            int numBlocks = hdr.mNextBlock - Pascal.VOL_DIR_START_BLOCK;
            Debug.Assert(numBlocks > 0);
            byte[] dirBuf = new byte[numBlocks * BLOCK_SIZE];
            IChunkAccess chunks = fileSystem.ChunkAccess;
            uint prevStart = 0;
            uint block = 0;
            try {
                for (int i = 0; i < numBlocks; i++) {
                    block = Pascal.VOL_DIR_START_BLOCK + (uint)i;
                    chunks.ReadBlock(block, dirBuf, i * BLOCK_SIZE);
                }
            } catch (BadBlockException) {
                notes.AddE("Unable to read directory block " + block);
                fileSystem.IsDubious = true;
                // continue on, with a zeroed-out buffer
            }

            int maxEntCount = (numBlocks * BLOCK_SIZE) / DIR_ENTRY_LEN - 1;
            for (int i = 0; i < maxEntCount; i++) {
                int offset = DIR_ENTRY_LEN * (i + 1);
                Pascal_FileEntry? newEntry = ExtractEntry(fileSystem, dirBuf, offset);
                if (newEntry == null) {
                    // Ran off end of list of entries.  Did we expect to be done?
                    if (i < hdr.mFileCount) {
                        notes.AddW("File count (" + hdr.mFileCount + ") exceeds number of files");
                        fileSystem.IsDubious = true;
                        break;
                    }
                    break;
                } else if (i == hdr.mFileCount) {
                    // All entries should have their filename length field zeroed.
                    if (newEntry.mRawFileName[0] != 0) {
                        notes.AddW("Found file entries past the recorded file count (" +
                            hdr.mFileCount + ")");
                        // We could correct it, or just figure something is wrong and not touch it.
                        fileSystem.IsDubious = true;
                    }
                }

                if (newEntry.StartBlock < prevStart) {
                    notes.AddE("File entries are out of order");
                    fileSystem.IsDubious = true;
                }
                prevStart = newEntry.StartBlock;

                if (!newEntry.ValidateEntry(hdr)) {
                    newEntry.IsDamaged = true;
                    fileSystem.IsDubious = true;
                } else {
                    // Update file usage map.
                    for (uint ublk = newEntry.mStartBlock; ublk < newEntry.mNextBlock; ublk++) {
                        vu.MarkInUse(ublk);
                        vu.SetUsage(ublk, newEntry);
                    }
                }
                volDir.ChildList.Add(newEntry);
            }

            return volDir;
        }

        /// <summary>
        /// Creates a new directory entry from the raw data.
        /// </summary>
        private static Pascal_FileEntry? ExtractEntry(Pascal fileSystem, byte[] buffer,
                int offset) {
            Pascal_FileEntry newEntry = new Pascal_FileEntry(fileSystem);
            newEntry.mStartBlock = RawData.ReadU16LE(buffer, ref offset);
            newEntry.mNextBlock = RawData.ReadU16LE(buffer, ref offset);
            newEntry.mTypeAndFlags = RawData.ReadU16LE(buffer, ref offset);
            for (int i = 0; i < Pascal.MAX_FILE_NAME_LEN + 1; i++) {
                newEntry.mRawFileName[i] = buffer[offset++];
            }
            newEntry.mByteCount = RawData.ReadU16LE(buffer, ref offset);
            newEntry.mModWhen = RawData.ReadU16LE(buffer, ref offset);
            if (newEntry.mStartBlock == 0 || newEntry.mRawFileName[0] == 0) {
                // Unused or deleted entry.
                newEntry.Dispose();
                return null;
            }
            return newEntry;
        }

        /// <summary>
        /// Serializes a file entry into the directory file buffer.
        /// </summary>
        internal void StoreEntry(byte[] buffer, ref int offset) {
            int startOffset = offset;
            Debug.Assert(mRawFileName[0] != 0);     // don't store a deleted entry
            RawData.WriteU16LE(buffer, ref offset, mStartBlock);
            RawData.WriteU16LE(buffer, ref offset, mNextBlock);
            RawData.WriteU16LE(buffer, ref offset, mTypeAndFlags);
            for (int i = 0; i < Pascal.MAX_FILE_NAME_LEN + 1; i++) {
                buffer[offset++] = mRawFileName[i];
            }
            RawData.WriteU16LE(buffer, ref offset, mByteCount);
            RawData.WriteU16LE(buffer, ref offset, mModWhen);
            Debug.Assert(offset - startOffset == DIR_ENTRY_LEN);
        }

        /// <summary>
        /// Creates a new file entry.
        /// </summary>
        internal static Pascal_FileEntry CreateEntry(Pascal fs, IFileEntry volDir,
                ushort startBlock, ushort nextBlock, FileKind kind, string fileName,
                DateTime modWhen) {
            Pascal_FileEntry newEntry = new Pascal_FileEntry(fs);
            newEntry.ContainingDir = volDir;
            newEntry.mStartBlock = startBlock;
            newEntry.mNextBlock = nextBlock;
            newEntry.mTypeAndFlags = (ushort)kind;
            newEntry.mFileName = fileName;
            ASCIIUtil.StringToFixedPascalBytes(fileName, newEntry.mRawFileName);
            newEntry.mByteCount = 0;
            newEntry.mModWhen = TimeStamp.ConvertDateTime_Pascal(modWhen);
            return newEntry;
        }

        /// <summary>
        /// Creates a "fake" file entry for the volume directory.
        /// </summary>
        private static Pascal_FileEntry CreateFakeVolDirEntry(Pascal fileSystem,
                Pascal.VolDirHeader hdr) {
            string volName = ASCIIUtil.PascalBytesToString(hdr.mVolumeName);
            Pascal_FileEntry newEntry = CreateEntry(fileSystem, IFileEntry.NO_ENTRY,
                hdr.mFirstBlock, hdr.mNextBlock, FileKind.UntypedFile, volName,
                TimeStamp.ConvertDateTime_Pascal(hdr.mLastDateSet));
            newEntry.IsVolumeDirectory = true;
            return newEntry;
        }

        /// <summary>
        /// Validates the fields in a directory entry.  Initializes "cooked" filename.
        /// </summary>
        private bool ValidateEntry(Pascal.VolDirHeader hdr) {
            Notes notes = FileSystem.Notes;
            mFileName = "<DAMAGED>";
            if (mRawFileName[0] == 0 || mRawFileName[0] > Pascal.MAX_FILE_NAME_LEN) {
                notes.AddW("Invalid filename length");
                mRawFileName[0] = 0;    // so things don't blow up later
                return false;
            }
            mFileName = ASCIIUtil.PascalBytesToString(mRawFileName);
            // Range checks.
            if (mStartBlock < hdr.mNextBlock || mStartBlock >= hdr.mVolBlockCount) {
                notes.AddW("Invalid start block " + mStartBlock + " for '" + FileName + "'");
                return false;
            }
            if (mNextBlock <= mStartBlock || mNextBlock > hdr.mVolBlockCount) {
                notes.AddW("Invalid next block " + mNextBlock + " for '" + FileName +
                    "' (start=" + mStartBlock + ")");
                return false;
            }
            if (mByteCount > BLOCK_SIZE) {
                notes.AddW("Invalid byte count " + mByteCount + " for '" + FileName + "'");
                return false;
            }
            return true;
        }

        // IFileEntry
        public void SaveChanges() {
            // Our changes are effectively stored alongside all others in the directory file.
            // Save the whole thing if changes have been made.
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

        /// <summary>
        /// Grows the file if the block index doesn't fit within the file bounds.  If the file
        /// can't be expanded to fit the entire allocation, an exception is thrown.
        /// </summary>
        /// <param name="blockIndex">Block index within the file.</param>
        /// <exception cref="DiskFullException">Unable to extend file.</exception>
        internal void ExpandIfNeeded(int blockIndex) {
            if (blockIndex < NextBlock - StartBlock) {
                return;     // it fits
            }

            ushort maxNext = FileSystem.GetMaxNext(this);
            Debug.Assert(maxNext >= NextBlock);
            if (blockIndex < maxNext - StartBlock) {
                mNextBlock = (ushort)(StartBlock + blockIndex + 1);
                MarkDirty();
            } else {
                throw new DiskFullException("Disk full, unable to extend file");
            }
        }

        /// <summary>
        /// Truncates the file to the specified number of blocks.
        /// </summary>
        /// <param name="blockIndex">Block index within the file.</param>
        internal void TruncateTo(int blockIndex) {
            if (blockIndex >= NextBlock - StartBlock) {
                throw new DAException("internal error: truncation would expand");
            }

            mNextBlock = (ushort)(StartBlock + blockIndex + 1);
            MarkDirty();
        }

        #region Filenames

        // Regex pattern for filename validation.
        //
        // Filenames are 1-15 characters, and may include printable ASCII characters
        // other than '$=?, [#:'.  Volume names follow the same rules but are shorter.
        // (Regex trick is "negative lookahead": https://stackoverflow.com/a/35427132/294248.)
        private const string FILE_NAME_PATTERN = @"^((?![\$=?, \[#:])[\x20-\x7e])+$";
        private static Regex sFileNameRegex = new Regex(FILE_NAME_PATTERN);

        private const string INVALID_CHARS = @"$=?, #:]";

        // IFileEntry
        public int CompareFileName(string fileName) {
            return CompareFileNames(mFileName, fileName);
        }

        // IFileEntry
        public int CompareFileName(string fileName, char fileNameSeparator) {
            return CompareFileNames(mFileName, fileName);
        }

        /// <summary>
        /// Compares two filenames, case insensitive.
        /// </summary>
        public static int CompareFileNames(string fileName1, string fileName2) {
            return string.Compare(fileName1, fileName2,
                    StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsFileNameValid(string fileName) {
            MatchCollection matches = sFileNameRegex.Matches(fileName);
            return (matches.Count == 1);
        }

        public static bool IsVolumeNameValid(string volName) {
            return volName.Length <= Pascal.MAX_VOL_NAME_LEN && IsFileNameValid(volName);
        }

        /// <summary>
        /// Adjusts a filename to be compatible with this filesystem, removing invalid characters
        /// and shortening the name.
        /// </summary>
        /// <param name="fileName">Filename to adjust.</param>
        /// <returns>Adjusted filename.</returns>
        public static string AdjustFileName(string fileName) {
            return DoAdjustName(fileName, Pascal.MAX_FILE_NAME_LEN);
        }

        /// <summary>
        /// Adjusts a volume name to be compatible with this filesystem, removing invalid
        /// characters and shortening the name.
        /// </summary>
        /// <param name="volName">Volume to adjust.</param>
        /// <returns>Adjusted volume name.</returns>
        public static string AdjustVolumeName(string volName) {
            return DoAdjustName(volName, Pascal.MAX_VOL_NAME_LEN);
        }

        private static string DoAdjustName(string name, int maxLen) {
            if (string.IsNullOrEmpty(name)) {
                return "Q";
            }

            // Convert the string to ASCII values, stripping diacritical marks.
            char[] chars = name.ToCharArray();
            ASCIIUtil.ReduceToASCII(chars, '_');

            // Replace invalid characters.
            for (int i = 0; i < chars.Length; i++) {
                char ch = chars[i];
                if (ch < 0x20 || ch == 0x7f || INVALID_CHARS.Contains(ch)) {
                    chars[i] = '_';
                }
            }
            string cleaned = new string(chars);

            // Clamp to max length by removing characters from the middle.  We won't lose the
            // standard extensions (".TEXT", etc).
            if (cleaned.Length > maxLen) {
                int firstLen = maxLen / 2;              // 7 or 3
                int lastLen = maxLen - (firstLen + 2);  // 6 or 2
                cleaned = cleaned.Substring(0, firstLen) + ".." +
                    cleaned.Substring(name.Length - lastLen, lastLen);
                Debug.Assert(cleaned.Length == maxLen);
            }

            // Convert to upper case.
            return cleaned.ToUpperInvariant();
        }

        #endregion Filenames

        public override string ToString() {
            return "[Pascal file entry: '" + mFileName + "']";
        }
    }
}
