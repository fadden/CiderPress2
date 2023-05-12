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
using System.Text;

using CommonUtil;
using static DiskArc.Defs;

namespace DiskArc.Arc {
    /// <summary>
    /// The one and only record in an AppleSingle archive.
    /// </summary>
    /// <remarks>
    /// <para>We want to keep everything here, rather than in the archive class, so that
    /// modifications are correctly handled through transactions.</para>
    /// <para>The goal is to gather all the various bits of information, and then when writing
    /// output the entries needed to represent all of the data.  This allows us to pass through
    /// things we don't care about (like MS-DOS file info) while changing stuff we do.</para>
    /// <para>We don't "upgrade" records from v1 to v2.  The code knows how to read and write
    /// both versions.  The "home file system" parameter, used for v1 archives, can be set by
    /// the application to change which values are written out.</para>
    /// </remarks>
    public class AppleSingle_FileEntry : IFileEntry {
        // Files found online always seem to have a data fork entry, even if it's zero-length.
        // It's probably worth replicating that behavior to avoid surprises.
        public const bool ALWAYS_DATA = true;

        private const int ENTRY_LENGTH = 12;
        private const int MAX_ENTRIES = 16;             // only 15 possible, so must be less
        private const int MAX_FILENAME_LENGTH = 1024;   // absurdly long filename
        private const uint NO_PART_OFFSET = 0xffffffff; // offset to use when part isn't present

        private const int FILE_DATES_LEN = 16;          // length of known part of ID #8
        private const int FINDER_INFO_LEN = 32;         // length of known part of ID #9
        private const int MAC_FILE_INFO_LEN = 4;        // length of known part of ID #10
        private const int PRODOS_FILE_INFO_LEN = 8;     // length of known part of ID #11
        private const int MSDOS_FILE_INFO_LEN = 2;      // length of known part of ID #12

        private const int HOME_FS_LEN = 16;
        private const string FS_PRODOS    = "ProDOS          ";
        private const string FS_MACINTOSH = "Macintosh       ";
        private const string FS_MSDOS     = "MS-DOS          ";
        private const string FS_UNIX      = "Unix            ";
        private const string FS_VAX_VMS   = "VAX VMS         ";
        private const string FS_MAC_OS_X  = "Mac OS X        ";

        //
        // IFileEntry interfaces.
        //

        public bool IsDubious { get; private set; }

        public bool IsDamaged { get; private set; }

        public bool IsDirectory => false;

        public bool HasDataFork { get { return mHasDataPart; } }
        public bool HasRsrcFork { get { return mHasRsrcPart; } }

        public bool IsDiskImage => false;

        public IFileEntry ContainingDir => IFileEntry.NO_ENTRY;
        public int ChildCount => 0;

        public string FileName {
            get {
                if (Archive == null) {
                    throw new InvalidOperationException("Invalid object");
                }
                return mFileName;
            }
            set {
                CheckChangeAllowed();
                ChangeObject!.SetFileName(value);
            }
        }
        public char DirectorySeparatorChar { get => IFileEntry.NO_DIR_SEP; set { } }

        public byte[] RawFileName {
            get {
                byte[] buf = new byte[mRawFileName.Length];
                Array.Copy(mRawFileName, buf, mRawFileName.Length);
                return buf;
            }
            set {
                // Cook the bytes and use the filename setter.
                CheckChangeAllowed();
                string cookedName = GenerateCookedName(value);
                ChangeObject!.SetFileName(cookedName);
            }
        }

        public string FullPathName { get { return FileName; } }

        public bool HasProDOSTypes => true;
        public byte FileType {
            get { return mFileType; }
            set {
                CheckChangeAllowed();
                ChangeObject!.mFileType = value;
            }
        }
        public ushort AuxType {
            get { return mAuxType; }
            set {
                CheckChangeAllowed();
                ChangeObject!.mAuxType = value;
            }
        }

        public bool HasHFSTypes => true;
        public uint HFSFileType {
            get { return mHFSFileType; }
            set {
                CheckChangeAllowed();
                ChangeObject!.mHFSFileType = value;
            }
        }
        public uint HFSCreator {
            get { return mHFSCreator; }
            set {
                CheckChangeAllowed();
                ChangeObject!.mHFSCreator = value;
            }
        }

        public byte Access {
            get { return mAccess; }
            set {
                CheckChangeAllowed();
                ChangeObject!.mAccess = value;
            }
        }

        public DateTime CreateWhen {
            get { return mCreateWhen; }
            set {
                CheckChangeAllowed();
                ChangeObject!.mCreateWhen = value;
            }
        }
        public DateTime ModWhen {
            get { return mModWhen; }
            set {
                CheckChangeAllowed();
                ChangeObject!.mModWhen = value;
            }
        }

        public long StorageSize { get { return mDataForkLength + mRsrcForkLength; } }

        public long DataLength { get { return mDataForkLength; } }

        public long RsrcLength { get { return mRsrcForkLength; } }

        // TODO(maybe): support entry type 4
        public string Comment { get => string.Empty; set { } }

        private static readonly List<IFileEntry> sEmptyList = new List<IFileEntry>();
        public IEnumerator<IFileEntry> GetEnumerator() {
            return sEmptyList.GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() {
            return sEmptyList.GetEnumerator();
        }

        //
        // AppleSingle-specific fields.
        //

        // Header fields.
        public bool IsLittleEndian { get; private set; }
        public int VersionNumber { get; internal set; }
        public FileSystemType HomeFS { get; set; }          // for v1
        private int NumEntries { get; set; }

        private string mFileName = string.Empty;
        private byte[] mRawFileName = RawData.EMPTY_BYTE_ARRAY;
        private uint mDataForkOffset = NO_PART_OFFSET;
        private uint mDataForkLength;
        private uint mRsrcForkOffset = NO_PART_OFFSET;
        private uint mRsrcForkLength;

        private uint mHFSFileType;
        private uint mHFSCreator;
        private byte mFileType;
        private ushort mAuxType;
        private byte mAccess = FileAttribs.FILE_ACCESS_UNLOCKED;
        private DateTime mCreateWhen = TimeStamp.NO_DATE;
        private DateTime mModWhen = TimeStamp.NO_DATE;
        private DateTime mBackupWhen = TimeStamp.NO_DATE;   // TODO: inaccessible; use pub prop?
        private DateTime mAccessWhen = TimeStamp.NO_DATE;   // TODO: inaccessible; use pub prop?

        private uint mMacAttributes;        // 32 bits of attributes, for Macintosh files
        private ushort mMSDosAttributes;    // 16 bits of attributes, for MS-DOS files

        private enum EntryID {
            Unknown = 0,
            DataFork = 1,
            RsrcFork = 2,
            RealName = 3,
            Comment = 4,
            IconBW = 5,
            IconColor = 6,
            FileInfo = 7,
            FileDates = 8,
            FinderInfo = 9,
            MacFileInfo = 10,
            ProDOSFileInfo = 11,
            MSDOSFileInfo = 12,
            AFPShortName = 13,
            AFPFileInfo = 14,
            AFPDirectoryID = 15,
            DataPathName = 100,             // AppleDouble v1
        }

        /// <summary>
        /// Reference to archive object.
        /// </summary>
        internal AppleSingle Archive { get; private set; }

        /// <summary>
        /// Object that receives changes when transaction is open.
        /// </summary>
        internal AppleSingle_FileEntry? ChangeObject { get; set; }

        /// <summary>
        /// Link back to original object, from change object.
        /// </summary>
        internal AppleSingle_FileEntry? OrigObject { get; set; }

        private bool mHasDataPart;
        private bool mHasRsrcPart;

        // Part source info for change object.
        private IPartSource mDataForkSource = IPartSource.NO_SOURCE;
        private IPartSource mRsrcForkSource = IPartSource.NO_SOURCE;

        // Temporary storage for file I/O, to reduce allocations.  Must be large enough to
        // hold the file header and the largest allowable filename.
        private byte[] mWorkBuf = new byte[Math.Max(AppleSingle.HEADER_LEN, MAX_FILENAME_LENGTH)];


        /// <summary>
        /// Private constructor.
        /// </summary>
        /// <param name="archive"></param>
        internal AppleSingle_FileEntry(AppleSingle archive) {
            Archive = archive;
        }

        /// <summary>
        /// Clones a file entry for use as a change object.
        /// </summary>
        internal static AppleSingle_FileEntry CreateChangeObject(AppleSingle_FileEntry src) {
            Debug.Assert(src.ChangeObject == null && src.OrigObject == null);
            if (src.IsDamaged) {
                throw new InvalidOperationException("Cannot clone a damaged entry");
            }

            AppleSingle_FileEntry newEntry = new AppleSingle_FileEntry(src.Archive);
            CopyFields(newEntry, src);
            // Invalidate these.
            newEntry.mDataForkOffset = newEntry.mRsrcForkOffset = NO_PART_OFFSET;
            newEntry.mDataForkLength = newEntry.mRsrcForkLength = 0;
            return newEntry;
        }

        /// <summary>
        /// Copies all fields from one object to another.
        /// </summary>
        internal static void CopyFields(AppleSingle_FileEntry dst, AppleSingle_FileEntry src) {
            dst.IsLittleEndian = src.IsLittleEndian;
            dst.VersionNumber = src.VersionNumber;
            dst.HomeFS = src.HomeFS;
            dst.NumEntries = src.NumEntries;

            dst.mFileName = src.mFileName;
            dst.mRawFileName = new byte[src.mRawFileName.Length];
            Array.Copy(src.mRawFileName, dst.mRawFileName, src.mRawFileName.Length);
            dst.mDataForkOffset = src.mDataForkOffset;
            dst.mDataForkLength = src.mDataForkLength;
            dst.mRsrcForkOffset = src.mRsrcForkOffset;
            dst.mRsrcForkLength = src.mRsrcForkLength;
            // Skip mDataForkSource, mRsrcForkSource.

            dst.mHFSFileType = src.mHFSFileType;
            dst.mHFSCreator = src.mHFSCreator;
            dst.mFileType = src.mFileType;
            dst.mAuxType = src.mAuxType;
            dst.mAccess = src.mAccess;
            dst.mCreateWhen = src.mCreateWhen;
            dst.mModWhen = src.mModWhen;
            dst.mBackupWhen = src.mBackupWhen;
            dst.mAccessWhen = src.mAccessWhen;

            dst.mMacAttributes = src.mMacAttributes;
            dst.mMSDosAttributes = src.mMSDosAttributes;

            dst.mHasDataPart = src.mHasDataPart;
            dst.mHasRsrcPart = src.mHasRsrcPart;
        }

        /// <summary>
        /// Scans the contents of the archive.
        /// </summary>
        /// <returns>True if parsing was successful, false if errors were found.</returns>
        internal bool Scan() {
            Stream stream = Archive.DataStream!;

            byte[] buf = mWorkBuf;
            try {
                stream.ReadExactly(buf, 0, AppleSingle.HEADER_LEN);
            } catch (EndOfStreamException) {
                return false;
            }
            uint version;
            if (RawData.GetU32BE(buf, 0) == AppleSingle.SIGNATURE) {
                // spec-compliant variety
                version = RawData.GetU32BE(buf, 4);
                IsLittleEndian = false;
            } else if (RawData.GetU32LE(buf, 0) == AppleSingle.SIGNATURE) {
                // rare bad-Mac variety
                version = RawData.GetU32LE(buf, 4);
                IsLittleEndian = true;
                Archive.Notes.AddW("File is in little-endian order");
            } else if (RawData.GetU32BE(buf, 0) == AppleSingle.ADF_SIGNATURE) {
                // AppleDouble header
                version = RawData.GetU32BE(buf, 4);
                IsLittleEndian = false;
                Archive.IsAppleDouble = true;
            } else {
                return false;
            }
            switch (version) {
                case AppleSingle.VERSION_1:
                    VersionNumber = 1;
                    break;
                case AppleSingle.VERSION_2:
                    VersionNumber = 2;
                    break;
                default:
                    return false;
            }

            if (VersionNumber == 2) {
                HomeFS = FileSystemType.Unknown;
            } else {
                // Grab 16 bytes from +$08 as a string.
                string homeName = Encoding.ASCII.GetString(buf, 8, 16);
                switch (homeName) {
                    case FS_PRODOS:
                        HomeFS = FileSystemType.ProDOS;
                        break;
                    case FS_MACINTOSH:
                        HomeFS = FileSystemType.HFS;
                        break;
                    case FS_MSDOS:
                        HomeFS = FileSystemType.MSDOS;
                        break;
                    case FS_UNIX:
                        HomeFS = FileSystemType.Unix;
                        break;
                    case FS_VAX_VMS:
                        // Not going near this one.
                    case FS_MAC_OS_X:
                        // Should be HFS+?  Leave set to "Unknown" so we use UTF-8 names.
                    default:
                        HomeFS = FileSystemType.Unknown;
                        break;
                }
            }
            Archive.AppHook.LogI("Apple" + (Archive.IsAppleDouble ? "Double" : "Single") +
                " version=" + VersionNumber + " homeFS=" + HomeFS +
                (IsLittleEndian ? " (little-endian)" : ""));

            NumEntries = (int)RawData.GetUWord(buf, 24, 2, !IsLittleEndian);
            if (NumEntries > MAX_ENTRIES) {
                Archive.AppHook.LogW("Bad entry count (" + NumEntries + ")");
                return false;
            }
            // Is the file long enough to hold all of these?
            if (stream.Position + NumEntries * ENTRY_LENGTH > stream.Length) {
                Archive.AppHook.LogW("Entry count exceeds length (" + NumEntries + ")");
                return false;
            }

            Debug.Assert(buf.Length > ENTRY_LENGTH);
            for (int i = 0; i < NumEntries; i++) {
                if (!ReadEntry()) {
                    // Could be one bad entry in an otherwise reasonable file.
                    Debug.Assert(IsDubious);
                }
            }

            // Process raw filename.  The name may include characters that are invalid for the
            // specified filesystem, e.g. GSHK specifies ProDOS as the "home" filesystem for
            // files from HFS disks.
            mFileName = GenerateCookedName(mRawFileName);

            return true;
        }

        /// <summary>
        /// Reads the next entry header from the stream.  If the entry holds metadata, the
        /// contents of the entry are unpacked.
        /// </summary>
        /// <returns>True if entry looks reasonable, false if not.</returns>
        private bool ReadEntry() {
            Stream stream = Archive.DataStream!;
            byte[] buf = mWorkBuf;

            stream.ReadExactly(buf, 0, ENTRY_LENGTH);
            uint entryID = RawData.GetUWord(buf, 0, 4, !IsLittleEndian);
            uint entryOffset = RawData.GetUWord(buf, 4, 4, !IsLittleEndian);
            uint entryLen = RawData.GetUWord(buf, 8, 4, !IsLittleEndian);

            // Make sure the body of the entry fits inside the file.  We don't check for
            // overlapping bodies, or bodies that overlap headers; this would be weird, but
            // not technically illegal.
            if (entryLen > stream.Length - entryOffset) {
                Archive.Notes.AddW("Bad entry (ID=" + entryID + "): body runs off end of file");
                IsDubious = true;
                return false;
            }

            long savedPos;
            switch ((EntryID)entryID) {
                case EntryID.DataFork:
                    mDataForkOffset = entryOffset;
                    mDataForkLength = entryLen;
                    mHasDataPart = true;
                    break;
                case EntryID.RsrcFork:
                    mRsrcForkOffset = entryOffset;
                    mRsrcForkLength = entryLen;
                    mHasRsrcPart = true;
                    break;
                case EntryID.RealName:
                    // Read raw filename into a new buffer.
                    if (entryLen > MAX_FILENAME_LENGTH) {
                        Archive.Notes.AddW("Found an extremely long filename (" + entryLen + ")");
                        IsDubious = true;
                        return false;
                    }
                    savedPos = stream.Position;
                    stream.Position = entryOffset;
                    mRawFileName = new byte[entryLen];
                    stream.ReadExactly(mRawFileName, 0, (int)entryLen);
                    stream.Position = savedPos;
                    break;
                case EntryID.Comment:
                    // I'm not sure what "standard Macintosh comments" are.  Plain Mac Roman text?
                    // Ignore it until we find some examples.
                    Archive.Notes.AddI("Found a comment (ignoring)");
                    break;
                case EntryID.IconBW:
                case EntryID.IconColor:
                    // As noted in the spec, icons were usually stored in the resource fork.  It's
                    // unclear whether this was ever actually useful.
                    Archive.Notes.AddI("Found icon type " + entryID + " (discarding)");
                    break;
                case EntryID.FileInfo:
                    ProcessEntry7(entryOffset, entryLen);
                    break;
                case EntryID.FileDates:
                    if (entryLen < FILE_DATES_LEN) {
                        Archive.Notes.AddW("Found short File Dates entry");
                        return false;
                    }
                    if (!GrabChunk(entryOffset, FILE_DATES_LEN)) {
                        return false;
                    }
                    // "Bad Mac" archives use 0x80700000 here, which is March 1932.  It's like
                    // they tried to output "no date" and missed.
                    int createWhen = (int)RawData.GetUWord(buf, 0, 4, !IsLittleEndian);
                    int modWhen =    (int)RawData.GetUWord(buf, 4, 4, !IsLittleEndian);
                    int backupWhen = (int)RawData.GetUWord(buf, 8, 4, !IsLittleEndian);
                    int accessWhen = (int)RawData.GetUWord(buf, 12, 4, !IsLittleEndian);
                    mCreateWhen = ConvertDateTime_AS2K(createWhen);
                    mModWhen = ConvertDateTime_AS2K(modWhen);
                    mBackupWhen = ConvertDateTime_AS2K(backupWhen);
                    mAccessWhen = ConvertDateTime_AS2K(accessWhen);
                    break;
                case EntryID.FinderInfo:
                    if (entryLen < FINDER_INFO_LEN) {
                        Archive.Notes.AddW("Found short Finder Info entry");
                        return false;
                    }
                    // TODO(someday): capture the extended attributes so we can propagate them
                    //   if the archive is modified.
                    if (!GrabChunk(entryOffset, FINDER_INFO_LEN)) {
                        return false;
                    }
                    // Grab the types, ignore the rest.  These appear to be stored in big-endian
                    // order, even in "bad mac" little-endian archives.
                    mHFSFileType = RawData.GetUWord(buf, 0, 4, true /*!IsLittleEndian*/);
                    mHFSCreator = RawData.GetUWord(buf, 4, 4, true /*!IsLittleEndian*/);
                    break;
                case EntryID.MacFileInfo:
                    if (entryLen < MAC_FILE_INFO_LEN) {
                        Archive.Notes.AddW("Found short Macintosh File Info entry");
                        return false;
                    }
                    if (!GrabChunk(entryOffset, MAC_FILE_INFO_LEN)) {
                        return false;
                    }
                    mMacAttributes = RawData.GetUWord(buf, 0, 4, !IsLittleEndian);
                    break;
                case EntryID.ProDOSFileInfo:
                    if (entryLen < PRODOS_FILE_INFO_LEN) {
                        Archive.Notes.AddW("Found short ProDOS File Info entry");
                        return false;
                    }
                    if (!GrabChunk(entryOffset, PRODOS_FILE_INFO_LEN)) {
                        return false;
                    }
                    ushort proAccess = (ushort)RawData.GetUWord(buf, 0, 2, !IsLittleEndian);
                    ushort proFileType = (ushort)RawData.GetUWord(buf, 2, 2, !IsLittleEndian);
                    uint proAuxType = RawData.GetUWord(buf, 4, 4, !IsLittleEndian);
                    mAccess = (byte)proAccess;
                    mFileType = (byte)proFileType;
                    mAuxType = (ushort)proAuxType;
                    break;
                case EntryID.MSDOSFileInfo:
                    if (entryLen < MSDOS_FILE_INFO_LEN) {
                        Archive.Notes.AddW("Found short MS-DOS File Info entry");
                        return false;
                    }
                    if (!GrabChunk(entryOffset, MSDOS_FILE_INFO_LEN)) {
                        return false;
                    }
                    mMSDosAttributes = (ushort)RawData.GetUWord(buf, 0, 2, !IsLittleEndian);
                    break;
                case EntryID.AFPShortName:
                case EntryID.AFPFileInfo:
                case EntryID.AFPDirectoryID:
                case EntryID.DataPathName:
                 // Don't really know what to do with these.
                default:
                    // Unknown entry, ignore it.
                    Archive.Notes.AddI("Ignoring entry with ID " + entryID);
                    break;
            }

            return true;
        }

        /// <summary>
        /// Handles entry ID 7, extracting the metadata found.
        /// </summary>
        private bool ProcessEntry7(uint fileOffset, uint length) {
            int expectedLen;
            switch (HomeFS) {
                case FileSystemType.ProDOS:
                    expectedLen = 16;
                    break;
                case FileSystemType.HFS:
                    expectedLen = 16;
                    break;
                case FileSystemType.MSDOS:
                    expectedLen = 6;
                    break;
                case FileSystemType.Unix:
                    expectedLen = 12;
                    break;
                default:
                    return false;
            }
            // Actual length may be longer, but can't be shorter.
            if (length < expectedLen) {
                Archive.Notes.AddW("Ignoring short entry #7 (len=" + length + ")");
                return false;
            }

            if (!GrabChunk(fileOffset, length)) {
                return false;
            }
            byte[] buf = mWorkBuf;
            switch (HomeFS) {
                case FileSystemType.ProDOS:
                    ushort proCreateDate = (ushort)RawData.GetUWord(buf, 0, 2, !IsLittleEndian);
                    ushort proCreateTime = (ushort)RawData.GetUWord(buf, 2, 2, !IsLittleEndian);
                    ushort proModDate = (ushort)RawData.GetUWord(buf, 4, 2, !IsLittleEndian);
                    ushort proModTime = (ushort)RawData.GetUWord(buf, 6, 2, !IsLittleEndian);
                    ushort proAccess = (ushort)RawData.GetUWord(buf, 8, 2, !IsLittleEndian);
                    ushort proFileType = (ushort)RawData.GetUWord(buf, 10, 2, !IsLittleEndian);
                    uint proAuxType = RawData.GetUWord(buf, 12, 4, !IsLittleEndian);
                    mCreateWhen = TimeStamp.ConvertDateTime_ProDOS(proCreateDate |
                        (uint)proCreateTime << 16);
                    mModWhen = TimeStamp.ConvertDateTime_ProDOS(proModDate |
                        (uint)proModTime << 16);
                    mAccess = (byte)proAccess;
                    mFileType = (byte)proFileType;
                    mAuxType = (ushort)proAuxType;
                    break;
                case FileSystemType.HFS:
                    // TODO: this seems straightforward, but we need an example to test against.
                    uint macCreateWhen = RawData.GetUWord(buf, 0, 4, !IsLittleEndian);
                    uint macModWhen = RawData.GetUWord(buf, 4, 4, !IsLittleEndian);
                    uint macBackupWhen = RawData.GetUWord(buf, 8, 4, !IsLittleEndian);
                    uint macAttributes = RawData.GetUWord(buf, 12, 4, !IsLittleEndian);
                    mCreateWhen = TimeStamp.ConvertDateTime_HFS(macCreateWhen);
                    mModWhen = TimeStamp.ConvertDateTime_HFS(macModWhen);
                    mBackupWhen = TimeStamp.ConvertDateTime_HFS(macBackupWhen);
                    mMacAttributes = macAttributes;
                    break;
                case FileSystemType.MSDOS:
                    // TODO: unsure about the date/time ordering, which is documented as a single
                    //  32-bit field, but is usually passed around as a pair of 16-bit values.
                    //  Need to find an example.
                    ushort dosModDate = (ushort)RawData.GetUWord(buf, 0, 2, !IsLittleEndian);
                    ushort dosModTime = (ushort)RawData.GetUWord(buf, 2, 2, !IsLittleEndian);
                    ushort dosAttributes = (ushort)RawData.GetUWord(buf, 4, 2, !IsLittleEndian);
                    mModWhen = TimeStamp.ConvertDateTime_MSDOS(dosModDate, dosModTime);
                    mMSDosAttributes = dosAttributes;
                    break;
                case FileSystemType.Unix:
                    // The documentation refers to "create date/time", but UNIX doesn't track
                    // that.  It's likely they're referring to the "change" timestamp.
                    // TODO: need an example to test against.
                    int unixCreateWhen = (int)RawData.GetUWord(buf, 0, 4, !IsLittleEndian);
                    int unixAccessWhen = (int)RawData.GetUWord(buf, 4, 4, !IsLittleEndian);
                    int unixModWhen = (int)RawData.GetUWord(buf, 8, 4, !IsLittleEndian);
                    // Preserving the "change" timestamp is pointless, and preserving "access"
                    // isn't terribly useful (it's effectively "archived when").  Not going to
                    // bother saving them off.
                    mModWhen = TimeStamp.ConvertDateTime_Unix32(unixModWhen);
                    break;
                default:
                    Debug.Assert(false);
                    break;
            }
            return true;
        }

        /// <summary>
        /// Grabs a chunk of data from the stream, reading it into the work buffer.  Saves and
        /// restores the file position.
        /// </summary>
        private bool GrabChunk(uint fileOffset, uint length) {
            if (length > mWorkBuf.Length) {
                return false;
            }
            Stream stream = Archive.DataStream!;
            if (length > stream.Length - fileOffset) {
                return false;
            }
            long savedPos = stream.Position;
            stream.Position = fileOffset;
            stream.ReadExactly(mWorkBuf, 0, (int)length);
            stream.Position = savedPos;
            return true;
        }


        // IFileEntry
        public bool GetPartInfo(FilePart part, out long length, out long storageSize,
                out CompressionFormat format) {
            format = CompressionFormat.Uncompressed;
            switch (part) {
                case FilePart.DataFork:
                    length = storageSize = mDataForkLength;
                    return HasDataFork;
                case FilePart.RsrcFork:
                    length = storageSize = mRsrcForkLength;
                    return HasRsrcFork;
                default:
                    length = storageSize = -1;
                    format = CompressionFormat.Unknown;
                    return false;
            }
        }

        /// <summary>
        /// Creates an object for reading the contents of the entry out of the archive.
        /// </summary>
        internal ArcReadStream CreateReadStream(FilePart part) {
            Debug.Assert(Archive.DataStream != null);

            long start, len;
            switch (part) {
                case FilePart.DataFork:
                    if (mDataForkOffset == 0) {
                        throw new FileNotFoundException("No data fork");
                    }
                    start = mDataForkOffset;
                    len = mDataForkLength;
                    break;
                case FilePart.RsrcFork:
                    if (mRsrcForkOffset == 0) {
                        throw new FileNotFoundException("No rsrc fork");
                    }
                    start = mRsrcForkOffset;
                    len = mRsrcForkLength;
                    break;
                default:
                    throw new FileNotFoundException("No part of type " + part);
            }

            Archive.DataStream.Position = start;
            return new ArcReadStream(Archive, len, null);
        }

        // IFileEntry
        public void SaveChanges() { /* not used for archives */ }

        /// <summary>
        /// Checks to see if making changes to this object is allowed.  This is called on the
        /// base object when a change is requested.
        /// </summary>
        /// <exception cref="InvalidOperationException">Nope.</exception>
        private void CheckChangeAllowed() {
            Debug.Assert(Archive != null);
            if (ChangeObject == null) {
                throw new InvalidOperationException("Cannot modify object without transaction");
            }
        }

        /// <summary>
        /// Adds a part source for file data.
        /// </summary>
        internal void AddPart(FilePart part, IPartSource partSource) {
            Debug.Assert(ChangeObject != null);
            switch (part) {
                case FilePart.DataFork:
                    if (ChangeObject.mHasDataPart) {
                        throw new ArgumentException("Record already has a data fork");
                    }
                    ChangeObject.mDataForkSource = partSource;
                    ChangeObject.mHasDataPart = true;
                    break;
                case FilePart.RsrcFork:
                    if (ChangeObject.mHasRsrcPart) {
                        throw new ArgumentException("Record already has a rsrc fork");
                    }
                    ChangeObject.mRsrcForkSource = partSource;
                    ChangeObject.mHasRsrcPart = true;
                    break;
                default:
                    throw new NotSupportedException("Cannot add part: " + part);
            }
        }

        /// <summary>
        /// Deletes file data.
        /// </summary>
        internal void DeletePart(FilePart part) {
            Debug.Assert(ChangeObject != null);
            switch (part) {
                case FilePart.DataFork:
                    if (!ChangeObject.mHasDataPart) {
                        throw new FileNotFoundException("Record does not have a data fork");
                    }
                    ChangeObject.mHasDataPart = false;
                    ChangeObject.mDataForkSource.Dispose();
                    ChangeObject.mDataForkSource = IPartSource.NO_SOURCE;
                    ChangeObject.mDataForkOffset = NO_PART_OFFSET;
                    ChangeObject.mDataForkLength = 0;
                    break;
                case FilePart.RsrcFork:
                    if (!ChangeObject.mHasRsrcPart) {
                        throw new FileNotFoundException("Record does not have a rsrc fork");
                    }
                    ChangeObject.mHasRsrcPart = false;
                    ChangeObject.mRsrcForkSource.Dispose();
                    ChangeObject.mRsrcForkSource = IPartSource.NO_SOURCE;
                    ChangeObject.mRsrcForkOffset = NO_PART_OFFSET;
                    ChangeObject.mRsrcForkLength = 0;
                    break;
                default:
                    throw new NotSupportedException("Cannot delete part: " + part);
            }
        }

        /// <summary>
        /// Writes the record to the output stream.  Invoke on the change object.
        /// </summary>
        internal void WriteRecord(Stream outputStream) {
            Debug.Assert(Archive.TmpBuf != null);
            Debug.Assert(OrigObject != null);

            // It's okay if mHasDataPart and mHasRsrcPart are both false.  It's a bit
            // meaningless, but we don't fail.

            // Compute the number of entries so we can size the entry list.  The decisions
            // here must exactly match the decisions below.
            NumEntries = 0;
            if (mRawFileName.Length > 0) {
                NumEntries++;           // ID=3
            }
            if (VersionNumber == 1) {
                NumEntries++;           // ID=7
            } else {
                NumEntries++;           // ID=8
                if (mHFSFileType != 0 || mHFSCreator != 0) {
                    NumEntries++;       // ID=9
                }
                if (mMacAttributes != 0) {
                    NumEntries++;       // ID=10
                }
                if (mFileType != 0 || mAuxType != 0 || mAccess != 0) {
                    NumEntries++;       // ID=11
                }
                if (mMSDosAttributes != 0) {
                    NumEntries++;       // ID=12
                }
            }
            if (ALWAYS_DATA || mHasDataPart) {
                NumEntries++;           // ID=1
            }
            if (mHasRsrcPart) {
                NumEntries++;           // ID=2
            }

            // Allocate a buffer for the file header and entry headers.
            byte[] hdrBuf = new byte[AppleSingle.HEADER_LEN + ENTRY_LENGTH * NumEntries];

            int offset = 0;
            if (Archive.IsAppleDouble) {
                RawData.WriteU32BE(hdrBuf, ref offset, AppleSingle.ADF_SIGNATURE);
            } else {
                RawData.WriteU32BE(hdrBuf, ref offset, AppleSingle.SIGNATURE);
            }
            if (VersionNumber == 1) {
                RawData.WriteU32BE(hdrBuf, ref offset, AppleSingle.VERSION_1);
                string fsConst;
                switch (HomeFS) {
                    case FileSystemType.ProDOS:
                        fsConst = FS_PRODOS;
                        break;
                    case FileSystemType.HFS:
                        fsConst = FS_MACINTOSH;
                        break;
                    case FileSystemType.MSDOS:
                        fsConst = FS_MSDOS;
                        break;
                    case FileSystemType.Unix:
                    default:
                        fsConst = FS_UNIX;
                        break;
                }
                for (int i = 0; i < HOME_FS_LEN; i++) {
                    hdrBuf[offset + i] = (byte)fsConst[i];
                }
            } else {
                RawData.WriteU32BE(hdrBuf, ref offset, AppleSingle.VERSION_2);
                RawData.MemSet(hdrBuf, offset, HOME_FS_LEN, 0x00);
            }
            offset += HOME_FS_LEN;
            RawData.WriteU16BE(hdrBuf, ref offset, (ushort)NumEntries);
            Debug.Assert(offset == AppleSingle.HEADER_LEN);

            outputStream.Position = offset + ENTRY_LENGTH * NumEntries;

            // Filename first (if any).
            if (mRawFileName.Length > 0) {
                RawData.WriteU32BE(hdrBuf, ref offset, (uint)EntryID.RealName);
                RawData.WriteU32BE(hdrBuf, ref offset, (uint)outputStream.Position);
                RawData.WriteU32BE(hdrBuf, ref offset, (uint)mRawFileName.Length);
                outputStream.Write(mRawFileName, 0, mRawFileName.Length);
            }

            // File attributes.
            if (VersionNumber == 1) {
                byte[] infoBuf;
                switch (HomeFS) {
                    case FileSystemType.ProDOS:
                        infoBuf = new byte[16];
                        uint proCreate = TimeStamp.ConvertDateTime_ProDOS(mCreateWhen);
                        RawData.SetU16BE(infoBuf, 0, (ushort)proCreate);
                        RawData.SetU16BE(infoBuf, 2, (ushort)(proCreate >> 16));
                        uint proMod = TimeStamp.ConvertDateTime_ProDOS(mModWhen);
                        RawData.SetU16BE(infoBuf, 4, (ushort)proMod);
                        RawData.SetU16BE(infoBuf, 6, (ushort)(proMod >> 16));
                        RawData.SetU16BE(infoBuf, 8, mAccess);
                        RawData.SetU16BE(infoBuf, 10, mFileType);
                        RawData.SetU32BE(infoBuf, 12, mAuxType);
                        break;
                    case FileSystemType.HFS:
                        infoBuf = new byte[16];
                        uint macCreate = TimeStamp.ConvertDateTime_HFS(mCreateWhen);
                        RawData.SetU32BE(infoBuf, 0, macCreate);
                        uint macMod = TimeStamp.ConvertDateTime_HFS(mModWhen);
                        RawData.SetU32BE(infoBuf, 4, macMod);
                        uint macBackup = TimeStamp.ConvertDateTime_HFS(mBackupWhen);
                        RawData.SetU32BE(infoBuf, 8, macBackup);
                        RawData.SetU32BE(infoBuf, 12, mMacAttributes);
                        break;
                    case FileSystemType.MSDOS:
                        infoBuf = new byte[6];
                        TimeStamp.ConvertDateTime_MSDOS(mModWhen, out ushort dosDate,
                            out ushort dosTime);
                        RawData.SetU16BE(infoBuf, 0, dosDate);
                        RawData.SetU16BE(infoBuf, 2, dosTime);
                        RawData.SetU16BE(infoBuf, 4, mMSDosAttributes);
                        break;
                    case FileSystemType.Unix:
                        infoBuf = new byte[12];
                        int unixMod = TimeStamp.ConvertDateTime_Unix32(mModWhen);
                        RawData.SetU32BE(infoBuf, 0, (uint)unixMod);
                        RawData.SetU32BE(infoBuf, 4, (uint)unixMod);
                        RawData.SetU32BE(infoBuf, 8, (uint)unixMod);
                        break;
                    default:
                        infoBuf = RawData.EMPTY_BYTE_ARRAY;
                        break;
                }
                RawData.WriteU32BE(hdrBuf, ref offset, (uint)EntryID.FileInfo);
                RawData.WriteU32BE(hdrBuf, ref offset, (uint)outputStream.Position);
                RawData.WriteU32BE(hdrBuf, ref offset, (uint)infoBuf.Length);
                outputStream.Write(infoBuf, 0, infoBuf.Length);
            } else {
                // Version 2.

                if (true) { // always output ID=8
                    byte[] dateBuf = new byte[16];
                    int create = ConvertDateTime_AS2K(mCreateWhen);
                    RawData.SetU32BE(dateBuf, 0, (uint)create);
                    int mod = ConvertDateTime_AS2K(mModWhen);
                    RawData.SetU32BE(dateBuf, 4, (uint)mod);
                    int backup = ConvertDateTime_AS2K(mBackupWhen);
                    RawData.SetU32BE(dateBuf, 8, (uint)backup);
                    int access = ConvertDateTime_AS2K(mAccessWhen);
                    RawData.SetU32BE(dateBuf, 12, (uint)access);

                    RawData.WriteU32BE(hdrBuf, ref offset, (uint)EntryID.FileDates);
                    RawData.WriteU32BE(hdrBuf, ref offset, (uint)outputStream.Position);
                    RawData.WriteU32BE(hdrBuf, ref offset, (uint)dateBuf.Length);
                    outputStream.Write(dateBuf, 0, dateBuf.Length);
                }

                if (mHFSFileType != 0 || mHFSCreator != 0) {
                    byte[] finderBuf = new byte[32];
                    RawData.SetU32BE(finderBuf, 0, mHFSFileType);
                    RawData.SetU32BE(finderBuf, 4, mHFSCreator);

                    RawData.WriteU32BE(hdrBuf, ref offset, (uint)EntryID.FinderInfo);
                    RawData.WriteU32BE(hdrBuf, ref offset, (uint)outputStream.Position);
                    RawData.WriteU32BE(hdrBuf, ref offset, (uint)finderBuf.Length);
                    outputStream.Write(finderBuf, 0, finderBuf.Length);
                }

                if (mMacAttributes != 0) {
                    byte[] macBuf = new byte[8];
                    RawData.SetU32BE(macBuf, 0, mMacAttributes);

                    RawData.WriteU32BE(hdrBuf, ref offset, (uint)EntryID.MacFileInfo);
                    RawData.WriteU32BE(hdrBuf, ref offset, (uint)outputStream.Position);
                    RawData.WriteU32BE(hdrBuf, ref offset, (uint)macBuf.Length);
                    outputStream.Write(macBuf, 0, macBuf.Length);
                }

                if (mFileType != 0 || mAuxType != 0 || mAccess != 0) {
                    byte[] proBuf = new byte[8];
                    RawData.SetU16BE(proBuf, 0, mAccess);
                    RawData.SetU16BE(proBuf, 2, mFileType);
                    RawData.SetU32BE(proBuf, 4, mAuxType);

                    RawData.WriteU32BE(hdrBuf, ref offset, (uint)EntryID.ProDOSFileInfo);
                    RawData.WriteU32BE(hdrBuf, ref offset, (uint)outputStream.Position);
                    RawData.WriteU32BE(hdrBuf, ref offset, (uint)proBuf.Length);
                    outputStream.Write(proBuf, 0, proBuf.Length);
                }

                if (mMSDosAttributes != 0) {
                    byte[] dosBuf = new byte[2];
                    RawData.SetU16BE(dosBuf, 0, mMSDosAttributes);

                    RawData.WriteU32BE(hdrBuf, ref offset, (uint)EntryID.MSDOSFileInfo);
                    RawData.WriteU32BE(hdrBuf, ref offset, (uint)outputStream.Position);
                    RawData.WriteU32BE(hdrBuf, ref offset, (uint)dosBuf.Length);
                    outputStream.Write(dosBuf, 0, dosBuf.Length);
                }
            }

            if (ALWAYS_DATA || mHasDataPart) {
                // Data fork.
                Debug.Assert(Archive.TmpBuf != null);
                mDataForkOffset = (uint)outputStream.Position;
                if (ALWAYS_DATA && !mHasDataPart) {
                    // Fake entry.
                    mHasDataPart = true;
                    mDataForkLength = 0;
                } else if (mDataForkSource == IPartSource.NO_SOURCE) {
                    // Retain existing entry; may be zero-length.
                    Stream srcStream = Archive.DataStream!;
                    srcStream.Position = OrigObject.mDataForkOffset;
                    FileUtil.CopyStream(srcStream, outputStream, OrigObject.mDataForkLength,
                        Archive.TmpBuf);
                    mDataForkLength = OrigObject.mDataForkLength;
                } else {
                    // Copy from part source.
                    ArcUtil.CopyPartSource(mDataForkSource, outputStream, null, null, true,
                        Archive.TmpBuf, out int inputLength, out int outputLength);
                    mDataForkLength = (uint)outputLength;
                }

                RawData.WriteU32BE(hdrBuf, ref offset, (uint)EntryID.DataFork);
                RawData.WriteU32BE(hdrBuf, ref offset, mDataForkOffset);
                RawData.WriteU32BE(hdrBuf, ref offset, mDataForkLength);
            }

            if (mHasRsrcPart) {
                // Resource fork.
                mRsrcForkOffset = (uint)outputStream.Position;
                if (mRsrcForkSource == IPartSource.NO_SOURCE) {
                    // Retain existing entry; may be zero-length.
                    Stream srcStream = Archive.DataStream!;
                    srcStream.Position = OrigObject.mRsrcForkOffset;
                    FileUtil.CopyStream(srcStream, outputStream, OrigObject.mRsrcForkLength,
                        Archive.TmpBuf);
                    mRsrcForkLength = OrigObject.mRsrcForkLength;
                } else {
                    // Copy from part source.
                    ArcUtil.CopyPartSource(mRsrcForkSource, outputStream, null, null, true,
                        Archive.TmpBuf, out int inputLength, out int outputLength);
                    mRsrcForkLength = (uint)outputLength;
                }

                RawData.WriteU32BE(hdrBuf, ref offset, (uint)EntryID.RsrcFork);
                RawData.WriteU32BE(hdrBuf, ref offset, mRsrcForkOffset);
                RawData.WriteU32BE(hdrBuf, ref offset, mRsrcForkLength);
            }

            if (offset != hdrBuf.Length) {
                // Whoops, the code is broken.
                Debug.Assert(false);
                throw new DAException("Entry count mismatch: NumEntries=" + NumEntries +
                    ", offset=" + offset + " hdrBuf.Length=" + hdrBuf.Length);
            }

            // Go back and write the header.
            outputStream.Position = 0;
            outputStream.Write(hdrBuf, 0, hdrBuf.Length);
        }

        /// <summary>
        /// Commits the change object into the object, and discards the change object.
        /// </summary>
        internal void CommitChange() {
            Debug.Assert(ChangeObject != this);
            CopyFields(this, ChangeObject!);    // copy fields back into primary object
            Debug.Assert(mDataForkSource == IPartSource.NO_SOURCE);
            Debug.Assert(mRsrcForkSource == IPartSource.NO_SOURCE);
            ChangeObject = null;
            OrigObject = null;
        }

        internal void DisposeSources() {
            Debug.Assert(ChangeObject != null);
            if (ChangeObject.mDataForkSource != IPartSource.NO_SOURCE) {
                ChangeObject.mDataForkSource.Dispose();
                ChangeObject.mDataForkSource = IPartSource.NO_SOURCE;
            }
            if (ChangeObject.mRsrcForkSource != IPartSource.NO_SOURCE) {
                ChangeObject.mRsrcForkSource.Dispose();
                ChangeObject.mRsrcForkSource = IPartSource.NO_SOURCE;
            }
        }

        /// <summary>
        /// Invalidates the file entry object.
        /// </summary>
        internal void Invalidate() {
            Debug.Assert(Archive != null); // harmless, but we shouldn't be invalidating twice
#pragma warning disable CS8625
            Archive = null;
#pragma warning restore CS8625
            ChangeObject = null;
        }

        #region Filenames

        // IFileEntry
        public int CompareFileName(string fileName) {
            return string.Compare(mFileName, fileName, StringComparison.OrdinalIgnoreCase);
        }

        // IFileEntry
        public int CompareFileName(string fileName, char fileNameSeparator) {
            return CompareFileName(fileName);
        }

        /// <summary>
        /// Generates the "cooked" filename from the raw filename bytes.  The conversion is
        /// chosen based on the archive properties.
        /// </summary>
        private string GenerateCookedName(byte[] rawFileName) {
            if (HomeFS == FileSystemType.ProDOS || HomeFS == FileSystemType.HFS) {
                // v1 archive, probably from an old Mac or Apple II
                return MacChar.MacToUnicode(rawFileName, 0, rawFileName.Length,
                    MacChar.Encoding.RomanShowCtrl);
            } else {
                return Encoding.UTF8.GetString(rawFileName, 0, rawFileName.Length);
            }
        }

        /// <summary>
        /// Sets the "raw" and "cooked" filenames.
        /// </summary>
        private void SetFileName(string fileName) {
            // No restrictions on the length of the name or its contents.  UNIX names aren't
            // allowed to have '/', '\0', or '%' without escaping, but there's nothing in the v2
            // archive that indicates that a filename comes from a UNIX system.
            if (fileName.Length > MAX_FILENAME_LENGTH) {
                throw new ArgumentException("Filename is excessively long (" +
                    fileName.Length + ")");
            }
            mFileName = fileName;

            // Generate the "raw" bytes.
            if (HomeFS == FileSystemType.ProDOS || HomeFS == FileSystemType.HFS) {
                Debug.Assert(VersionNumber == 1);
                mRawFileName = MacChar.UnicodeToMac(fileName, MacChar.Encoding.RomanShowCtrl);
            } else {
                mRawFileName = Encoding.UTF8.GetBytes(mFileName);
            }
        }

        #endregion Filenames

        #region Miscellaneous

        // Adjustment for AppleSingle ID 8 timestamps (seconds since Jan 1 2000, signed) to
        // UNIX time (seconds since Jan 1 1970, signed), both UTC.  30 years + 7 leap days.
        private static readonly long AS2K_TIME_OFFSET =
            (long)(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc) -
                   new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        private const int INVALID_AS2K_DATE = int.MinValue;     // 0x80000000
        private const uint INVALID_LE_AS2K_DATE = 0x80700000;

        /// <summary>
        /// Converts an AppleSingle ID 8 timestamp to a DateTime object.
        /// </summary>
        private static DateTime ConvertDateTime_AS2K(int when) {
            if (when == INVALID_AS2K_DATE || (uint)when == INVALID_LE_AS2K_DATE) {
                return TimeStamp.NO_DATE;
            }
            long unixSec = when - AS2K_TIME_OFFSET;
            DateTimeOffset dtOff = DateTimeOffset.FromUnixTimeSeconds(unixSec);
            // UNIX timestamps are UTC.  Convert to local time.
            DateTime dt = dtOff.LocalDateTime;
            Debug.Assert(ConvertDateTime_AS2K(dt) == when);     // confirm reversibility
            return dt;
        }

        /// <summary>
        /// Converts a DateTime object to an AppleSingle ID 8 timestamp.
        /// </summary>
        private static int ConvertDateTime_AS2K(DateTime when) {
            if (!TimeStamp.IsValidDate(when)) {
                return INVALID_AS2K_DATE;
            }
            long unixSec = new DateTimeOffset(when).ToUnixTimeSeconds();
            long as2kSec = unixSec + AS2K_TIME_OFFSET;
            if (as2kSec >= int.MinValue && unixSec <= int.MaxValue) {
                return (int)as2kSec;
            } else {
                return INVALID_AS2K_DATE;
            }
        }

        #endregion Miscellaneous
    }
}
