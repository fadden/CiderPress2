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
using System.IO.Compression;
using System.Text.RegularExpressions;

using CommonUtil;
using DiskArc.Comp;
using DiskArc.FS;
using static DiskArc.Defs;

namespace DiskArc.Arc {
    /// <summary>
    /// One record in a Binary II file.
    /// </summary>
    /// <remarks>
    /// <para>Phantom files and GS/OS option lists are not currently exposed, but these don't
    /// really seem to exist in file archives found on the web.</para>
    /// </remarks>
    public class Binary2_FileEntry : IFileEntry {
        private const string SQUEEZE_SUFFIX = ".qq";
        private const char SEP_CHAR = '/';

        //
        // IFileEntry interfaces.
        //
        public bool IsDubious { get; private set; }

        public bool IsDamaged { get; private set; }

        public bool IsDirectory { get { return FileType == FileAttribs.FILE_TYPE_DIR; } }

        // We don't allow a record to be committed if it doesn't have a data fork, so HasDataFork
        // can only be false for newly-created records.
        public bool HasDataFork { get { return HasDataPart; } }
        public bool HasRsrcFork => false;
        public bool IsDiskImage => false;

        public IFileEntry ContainingDir => IFileEntry.NO_ENTRY;
        public int Count => 0;

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
                ChangeObject.HeaderDirty = true;
            }
        }
        public char DirectorySeparatorChar {
            get => SEP_CHAR;
            set { }
        }
        public byte[] RawFileName {
            get {
                byte fileNameLen = mHeader.mRawFileName[0];
                byte[] result = new byte[fileNameLen];
                Array.Copy(mHeader.mRawFileName, 1, result, 0, result.Length);
                return result;
            }
            set {
                CheckChangeAllowed();
                // Binary II requires simple ASCII, so just convert raw value to string.
                ChangeObject!.SetFileName(System.Text.Encoding.ASCII.GetString(value));
                ChangeObject.HeaderDirty = true;
            }
        }

        public string FullPathName { get { return FileName; } }

        public bool HasProDOSTypes => true;

        public byte FileType {
            get { return mHeader.mFileType; }
            set {
                CheckChangeAllowed();
                Debug.Assert(ChangeObject != null);
                if (value == FileAttribs.FILE_TYPE_DIR) {
                    if (ChangeObject.DataLength != 0) {
                        throw new InvalidOperationException(
                            "Can't change type to DIR on non-empty record");
                    }
                    if (ChangeObject.HasDataPart) {
                        throw new InvalidOperationException(
                            "Can't change type to DIR after adding data part");
                    }
                    ChangeObject.mHeader.mStorageType = (byte)ProDOS.StorageType.Directory;
                } else {
                    ChangeObject.mHeader.mStorageType = (byte)ProDOS.StorageType.Sapling;
                }
                ChangeObject.mHeader.mFileType = value;
                ChangeObject.HeaderDirty = true;
            }
        }
        public ushort AuxType {
            get { return mHeader.mAuxType; }
            set {
                CheckChangeAllowed();
                ChangeObject!.mHeader.mAuxType = value;
                ChangeObject.HeaderDirty = true;
            }
        }

        // TODO(maybe): set to true if the record has a GS/OS option list and OS type of HFS.
        //   Not sure such archives were ever actually created, so probably not worth the effort.
        public bool HasHFSTypes => false;
        public uint HFSFileType {
            get { return 0; }
            set { }
        }
        public uint HFSCreator {
            get { return 0; }
            set { }
        }

        public byte Access {
            get { return mHeader.mAccess; }
            set {
                CheckChangeAllowed();
                ChangeObject!.mHeader.mAccess = value;
                ChangeObject.HeaderDirty = true;
            }
        }

        public DateTime CreateWhen {
            get {
                uint prodosWhen = (uint)(mHeader.mCreateDate | (mHeader.mCreateTime << 16));
                return TimeStamp.ConvertDateTime_ProDOS(prodosWhen);
            }
            set {
                CheckChangeAllowed();
                uint prodosWhen = TimeStamp.ConvertDateTime_ProDOS(value);
                ChangeObject!.mHeader.mCreateDate = (ushort)prodosWhen;
                ChangeObject.mHeader.mCreateTime = (ushort)(prodosWhen >> 16);
                ChangeObject.HeaderDirty = true;
            }
        }
        public DateTime ModWhen {
            get {
                uint prodosWhen = (uint)(mHeader.mModDate | (mHeader.mModTime << 16));
                return TimeStamp.ConvertDateTime_ProDOS(prodosWhen);
            }
            set {
                CheckChangeAllowed();
                uint prodosWhen = TimeStamp.ConvertDateTime_ProDOS(value);
                ChangeObject!.mHeader.mModDate = (ushort)prodosWhen;
                ChangeObject.mHeader.mModTime = (ushort)(prodosWhen >> 16);
                ChangeObject.HeaderDirty = true;
            }
        }

        public long StorageSize {
            get {
                // Return the actual data length, up to nearest multiple of 128.  This reflects
                // the number of bytes required to store the data in the archive.  The ProDOS
                // block count stored in the header is interesting but inaccurate, and
                // inconsistent with what this field means across different archive types.
                return Binary2.RoundUpChunkLen((int)mHeader.mEOF);
            }
        }

        public long DataLength {
            get {
                if (mIsSqueezed) {
                    return -1;
                } else {
                    return mHeader.mEOF;
                }
            }
        }

        public long RsrcLength => 0;

        public string Comment { get => string.Empty; set { } }

        public bool GetPartInfo(FilePart part, out long length, out long storageSize,
                out CompressionFormat format) {
            format = mIsSqueezed ? CompressionFormat.Squeeze : CompressionFormat.Uncompressed;
            if (part == FilePart.DataFork) {
                length = DataLength;
                storageSize = StorageSize;
                return true;
            } else {
                length = -1;
                storageSize = 0;
                return false;
            }
        }

        // Entries do not have children.
        private static readonly List<IFileEntry> sEmptyList = new List<IFileEntry>();
        public IEnumerator<IFileEntry> GetEnumerator() {
            return sEmptyList.GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() {
            return sEmptyList.GetEnumerator();
        }


        //
        // Internal fields.
        //

        /// <summary>
        /// Reference to archive object.
        /// </summary>
        internal Binary2 Archive { get; private set; }

        /// <summary>
        /// Object that receives changes when transaction is open.
        /// </summary>
        internal Binary2_FileEntry? ChangeObject { get; set; }

        /// <summary>
        /// Link back to original object, from change object.
        /// </summary>
        internal Binary2_FileEntry? OrigObject { get; set; }

        /// <summary>
        /// True if one or more parts have changed.  Non-directory entries aren't allowed to
        /// not have a data fork part, so we can just check for a non-empty part source.
        /// </summary>
        /// <remarks>
        /// It only makes sense to examine this for an object in the "edit" list.  Note the
        /// ChangeObject will be non-null for a newly-created entry.
        /// </remarks>
        internal bool HasPartChanges { get { return mPendingDataPart != IPartSource.NO_SOURCE; } }

        /// <summary>
        /// True if this entry has a data fork, even if it's zero bytes long.  This will be false
        /// for newly-created entries and entries where DeletePart was used.
        /// </summary>
        internal bool HasDataPart { get; set; }

        /// <summary>
        /// Pending data.  Only set in change objects.  Will be NO_SOURCE if we're not replacing
        /// the current record contents.
        /// </summary>
        private IPartSource mPendingDataPart = IPartSource.NO_SOURCE;

        /// <summary>
        /// Compression format to use for pending data.
        /// </summary>
        private CompressionFormat mPendingFormat = CompressionFormat.Default;

        /// <summary>
        /// True if one or more header fields have been modified.
        /// </summary>
        //internal bool HeaderDirty { get; private set; }
        internal bool HeaderDirty {
            get { return mHeaderDirty; }
            set {
                Debug.Assert(OrigObject != null);
                mHeaderDirty = value;
            }
        }
        private bool mHeaderDirty = false;

        /// <summary>
        /// Number of files that follow this one in the archive.  The count includes "phantom"
        /// files.
        /// </summary>
        internal int FilesToFollow {
            get { return mHeader.mFilesToFollow; }
            set {
                Debug.Assert(ChangeObject != null);
                if (value >= Binary2.MAX_RECORDS) {
                    throw new ArgumentException("Can't have " + value + " files following");
                }
                if (ChangeObject.mHeader.mFilesToFollow != value) {
                    // We set this for every record when committing a change, so only raise the
                    // "dirty" flag if the count changes.
                    ChangeObject.mHeader.mFilesToFollow = (byte)value;
                    ChangeObject.HeaderDirty = true;
                }

            }
        }

        /// <summary>
        /// Parsed header.
        /// </summary>
        private Header mHeader;

        /// <summary>
        /// File offset of 128-byte header.
        /// </summary>
        internal long HeaderFileOffset { get; set; } = -1;

        /// <summary>
        /// True if the data is in "squeeze" format.
        /// </summary>
        private bool mIsSqueezed;

        /// <summary>
        /// Filename or partial pathname.
        /// </summary>
        public string mFileName = string.Empty;


        // Regex pattern for filename validation.  The filename field may hold a simple
        // filename or a partial pathname, so we allow the usual ProDOS syntax plus '/'
        // between elements.  The Binary II format has no provision for lower case, so we
        // do not allow it here.
        //
        // This pattern doesn't check the overall string length (max 64).
        private const string FILE_NAME_PATTERN =
            @"^[A-Z]([A-Z0-9\.]{0,14})(\/[A-Z]([A-Z0-9\.]{0,14}))*$";
        private static Regex sFileNameRegex = new Regex(FILE_NAME_PATTERN);


        /// <summary>
        /// Binary II record header.  Exactly fills one 128-byte chunk.
        /// </summary>
        private class Header {
            public const int RESV_LEN = 21;

            /// <summary>First ID byte.</summary>
            public byte mId0;

            /// <summary>Second ID byte.</summary>
            public byte mId1;

            /// <summary>Third ID byte.</summary>
            public byte mId2;

            /// <summary>ProDOS 8 access flags.</summary>
            public byte mAccess;

            /// <summary>ProDOS 8 file type.</summary>
            public byte mFileType;

            /// <summary>ProDOS 8 aux type.</summary>
            public ushort mAuxType;

            /// <summary>ProDOS 8 storage type.</summary>
            public byte mStorageType;

            /// <summary>Size of file on disk, in 512-byte blocks.</summary>
            public ushort mStorageSize;

            /// <summary>Modification date, in ProDOS 8 format.</summary>
            public ushort mModDate;

            /// <summary>Modification time, in ProDOS 8 format.</summary>
            public ushort mModTime;

            /// <summary>Creation date, in ProDOS 8 format.</summary>
            public ushort mCreateDate;

            /// <summary>Creation time, in ProDOS 8 format.</summary>
            public ushort mCreateTime;

            /// <summary>Fourth ID byte.</summary>
            public byte mId3;

            /// <summary>Reserved, must be zero.</summary>
            public byte mReserved1;

            /// <summary>Length of file, in bytes.</summary>
            public uint mEOF;

            /// <summary>File name, preceded by length byte.</summary>
            public byte[] mRawFileName = new byte[Binary2.MAX_FILE_NAME_LEN + 1];

            /// <summary>Reserved, must be zero.</summary>
            public byte[] mReserved2 = new byte[RESV_LEN];

            /// <summary>High part of GS/OS aux type.</summary>
            public ushort mGAuxType;

            /// <summary>High part of GS/OS access flags.</summary>
            public byte mGAccess;

            /// <summary>High part of GS/OS file type.</summary>
            public byte mGFileType;

            /// <summary>High part of GS/OS storage type.</summary>
            public byte mGStorageType;

            /// <summary>High part of GS/OS file storage size.</summary>
            public ushort mGStorageSize;

            /// <summary>High part of GS/OS file length.</summary>
            public byte mGEOF;

            /// <summary>Total disk space required for all entries in this archive, in
            /// 512-byte blocks.</summary>
            public uint mTotalDiskSpace;

            /// <summary>
            /// <para>Operating system type for this file.</para>
            /// <list type="bullet">
            ///   <item>$00 ProDOS / SOS</item>
            ///   <item>$01 DOS 3.3</item>
            ///   <item>$02 (reserved)</item>
            /// </list>
            /// <para>Remaining values match GS/OS definition.  Version 0 of the Binary II
            /// specification used slightly different definitions
            /// (2=Pascal, 3=CP/M, 4=MS-DOS).</para>
            /// </summary>
            public byte mOSType;

            /// <summary>Native file type, for non-ProDOS operating systems.</summary>
            public ushort mNativeFileType;

            /// <summary>Flag indicating entry is a "phantom" file.  Used for GS/OS option
            /// lists and metadata sent by telecommunications software.</summary>
            public byte mPhantomFileFlag;

            /// <summary>
            /// <para>Data flags indicating special handling.</para>
            /// <list type="bullet">
            ///   <item>$80 file is compressed (with "squeeze")</item>
            ///   <item>$40 file is encrypted (method undefined)</item>
            ///   <item>$01 file is sparse (meaning undefined)</item>
            /// </list>
            /// <para>Other values are reserved.</para>
            /// </summary>
            public byte mDataFlags;

            /// <summary>Binary II version number (0 or 1).</summary>
            public byte mVersion;

            /// <summary>Number of files that follow this entry.  Counts down in each entry,
            /// reaching 0 for the final file.  The count does include phantom files.</summary>
            public byte mFilesToFollow;


            [Flags]
            public enum DataFlags : byte {
                Sparse = 0x01,
                Encrypted = 0x40,
                Compressed = 0x80
            }

            public Header() { }

            /// <summary>
            /// Copy constructor.
            /// </summary>
            public Header(Header src) {
                // We don't keep the original data buffer, so alloc a temp buffer and store/load.
                byte[] buf = new byte[Binary2.CHUNK_LEN];
                src.Store(buf, 0);
                Load(buf, 0);
            }

            public void Load(byte[] buf, int offset) {
                int startOffset = offset;
                mId0 = RawData.ReadU8(buf, ref offset);
                mId1 = RawData.ReadU8(buf, ref offset);
                mId2 = RawData.ReadU8(buf, ref offset);
                mAccess = RawData.ReadU8(buf, ref offset);
                mFileType = RawData.ReadU8(buf, ref offset);
                mAuxType = RawData.ReadU16LE(buf, ref offset);
                mStorageType = RawData.ReadU8(buf, ref offset);
                mStorageSize = RawData.ReadU16LE(buf, ref offset);
                mModDate = RawData.ReadU16LE(buf, ref offset);
                mModTime = RawData.ReadU16LE(buf, ref offset);
                mCreateDate = RawData.ReadU16LE(buf, ref offset);
                mCreateTime = RawData.ReadU16LE(buf, ref offset);
                mId3 = RawData.ReadU8(buf, ref offset);
                mReserved1 = RawData.ReadU8(buf, ref offset);
                mEOF = RawData.ReadU24LE(buf, ref offset);
                Array.Copy(buf, offset, mRawFileName, 0, Binary2.MAX_FILE_NAME_LEN + 1);
                offset += Binary2.MAX_FILE_NAME_LEN + 1;
                Array.Copy(buf, offset, mReserved2, 0, RESV_LEN);
                offset += RESV_LEN;
                mGAuxType = RawData.ReadU16LE(buf, ref offset);
                mGAccess = RawData.ReadU8(buf, ref offset);
                mGFileType = RawData.ReadU8(buf, ref offset);
                mGStorageType = RawData.ReadU8(buf, ref offset);
                mGStorageSize = RawData.ReadU16LE(buf, ref offset);
                mGEOF = RawData.ReadU8(buf, ref offset);
                mTotalDiskSpace = RawData.ReadU32LE(buf, ref offset);
                mOSType = RawData.ReadU8(buf, ref offset);
                mNativeFileType = RawData.ReadU16LE(buf, ref offset);
                mPhantomFileFlag = RawData.ReadU8(buf, ref offset);
                mDataFlags = RawData.ReadU8(buf, ref offset);
                mVersion = RawData.ReadU8(buf, ref offset);
                mFilesToFollow = RawData.ReadU8(buf, ref offset);
                Debug.Assert(offset == startOffset + Binary2.CHUNK_LEN);
            }

            public void Store(byte[] buf, int offset) {
                int startOffset = offset;
                RawData.WriteU8(buf, ref offset, mId0);
                RawData.WriteU8(buf, ref offset, mId1);
                RawData.WriteU8(buf, ref offset, mId2);
                RawData.WriteU8(buf, ref offset, mAccess);
                RawData.WriteU8(buf, ref offset, mFileType);
                RawData.WriteU16LE(buf, ref offset, mAuxType);
                RawData.WriteU8(buf, ref offset, mStorageType);
                RawData.WriteU16LE(buf, ref offset, mStorageSize);
                RawData.WriteU16LE(buf, ref offset, mModDate);
                RawData.WriteU16LE(buf, ref offset, mModTime);
                RawData.WriteU16LE(buf, ref offset, mCreateDate);
                RawData.WriteU16LE(buf, ref offset, mCreateTime);
                RawData.WriteU8(buf, ref offset, mId3);
                RawData.WriteU8(buf, ref offset, mReserved1);
                RawData.WriteU24LE(buf, ref offset, mEOF);
                Array.Copy(mRawFileName, 0, buf, offset, Binary2.MAX_FILE_NAME_LEN + 1);
                offset += Binary2.MAX_FILE_NAME_LEN + 1;
                Array.Copy(mReserved2, 0, buf, offset, RESV_LEN);
                offset += RESV_LEN;
                RawData.WriteU16LE(buf, ref offset, mGAuxType);
                RawData.WriteU8(buf, ref offset, mGAccess);
                RawData.WriteU8(buf, ref offset, mGFileType);
                RawData.WriteU8(buf, ref offset, mGStorageType);
                RawData.WriteU16LE(buf, ref offset, mGStorageSize);
                RawData.WriteU8(buf, ref offset, mGEOF);
                RawData.WriteU32LE(buf, ref offset, mTotalDiskSpace);
                RawData.WriteU8(buf, ref offset, mOSType);
                RawData.WriteU16LE(buf, ref offset, mNativeFileType);
                RawData.WriteU8(buf, ref offset, mPhantomFileFlag);
                RawData.WriteU8(buf, ref offset, mDataFlags);
                RawData.WriteU8(buf, ref offset, mVersion);
                RawData.WriteU8(buf, ref offset, mFilesToFollow);
                Debug.Assert(offset == startOffset + Binary2.CHUNK_LEN);
            }
        }


        /// <summary>
        /// Private constructor.
        /// </summary>
        /// <param name="archive"></param>
        private Binary2_FileEntry(Binary2 archive, Header header) {
            Archive = archive;
            mHeader = header;
        }

        /// <summary>
        /// Creates a new, empty object.
        /// </summary>
        /// <param name="archive">Archive that this entry will be a part of.</param>
        /// <returns>New entry object.</returns>
        internal static Binary2_FileEntry CreateNew(Binary2 archive) {
            // Most fields can remain set to zero.  The filename will be empty, so this is not
            // yet a valid record.
            Header header = new Header();
            header.mId0 = Binary2.ID0;
            header.mId1 = Binary2.ID1;
            header.mId2 = Binary2.ID2;
            header.mId3 = Binary2.ID3;
            header.mVersion = 1;

            header.mStorageType = (byte)DiskArc.FS.ProDOS.StorageType.Sapling;
            header.mAccess = FileAttribs.FILE_ACCESS_UNLOCKED;

            Binary2_FileEntry newEntry = new Binary2_FileEntry(archive, header);
            newEntry.HasDataPart = false;
            return newEntry;
        }

        /// <summary>
        /// Clones a file entry for use as a change object.
        /// </summary>
        internal static Binary2_FileEntry CreateChangeObject(Binary2_FileEntry src) {
            if (src.IsDamaged) {
                // Dubious is okay, damaged is not.
                throw new InvalidOperationException("Cannot clone a damaged entry");
            }

            // Clone the header data.
            Header header = new Header(src.mHeader);
            Binary2_FileEntry newEntry = new Binary2_FileEntry(src.Archive, header);
            CopyFields(newEntry, src);
            newEntry.HeaderFileOffset = -1;
            return newEntry;
        }

        /// <summary>
        /// Copies all fields from one object to another.
        /// </summary>
        internal static void CopyFields(Binary2_FileEntry dst, Binary2_FileEntry src) {
            // Copy fields whose values aren't stored in the header.
            dst.mIsSqueezed = src.mIsSqueezed;
            dst.mFileName = src.mFileName;
            Debug.Assert(src.HasDataPart);     // should always be set in original entry
            dst.HasDataPart = src.HasDataPart;
            dst.HeaderFileOffset = src.HeaderFileOffset;

            // Don't copy IsDubious
            // Don't touch mHeader
        }

        /// <summary>
        /// Creates an object from the stream at the current position.  On completion,
        /// the stream will be positioned at the start of the following record, or EOF.
        /// </summary>
        /// <param name="archive">Archive that this entry will be a part of.</param>
        /// <returns>New entry object, or null if a Binary II record wasn't found.</returns>
        internal static Binary2_FileEntry? ReadNextRecord(Binary2 archive) {
            Debug.Assert(archive.DataStream != null);

            if (archive.DataStream.Position >= archive.DataStream.Length) {
                // At EOF, nothing to read.
                return null;
            }
            if (archive.DataStream.Position + Binary2.CHUNK_LEN > archive.DataStream.Length) {
                archive.Notes.AddI("Found garbage at end of file (" +
                    (archive.DataStream.Length - archive.DataStream.Position) + " bytes)");
                archive.IsDubious = true;
                return null;
            }

            byte[] headerBuf = new byte[Binary2.CHUNK_LEN];
            archive.DataStream.ReadExactly(headerBuf, 0, Binary2.CHUNK_LEN);

            // Parse the raw fields.
            Header header = new Header();
            header.Load(headerBuf, 0);

            // Create an object to hold state, then validate the header.
            Binary2_FileEntry newEntry = new Binary2_FileEntry(archive, header);
            if (!newEntry.ValidateHeader()) {
                archive.IsDubious = true;
                return null;
            }
            newEntry.HeaderFileOffset = archive.DataStream.Position - Binary2.CHUNK_LEN;

            // Seek past the data.
            long newPos = archive.DataStream.Seek(newEntry.StorageSize, SeekOrigin.Current);
            if (newPos > archive.DataStream.Length) {
                // Validation succeeded, so the data is all there; this is likely a failure
                // to pad the archive to a 128-byte boundary.
                archive.Notes.AddW("Last Binary II entry not padded to 128-byte boundary");
            }

            newEntry.CheckSqueezed();

            newEntry.HasDataPart = true;
            return newEntry;
        }

        /// <summary>
        /// Validates the contents of the header, and sets some local properties to
        /// computed values.
        /// </summary>
        /// <returns>False if the header is damaged.</returns>
        public bool ValidateHeader() {
            Debug.Assert(Archive.DataStream != null);
            if (mHeader.mId0 != Binary2.ID0 || mHeader.mId1 != Binary2.ID1 ||
                    mHeader.mId2 != Binary2.ID2 || mHeader.mId3 != Binary2.ID3) {
                Archive.Notes.AddW("Entry has incorrect ID bytes");
                return false;
            }

            if (Archive.DataStream.Position > Archive.DataStream.Length - (long)mHeader.mEOF) {
                // Archive was probably truncated.  Flag the last entry as damaged, but keep it.
                Archive.Notes.AddW("Last entry extends past end of archive file");
                IsDamaged = true;
            }

            // Binary II identifies directories by their file type, not their storage type,
            if (mHeader.mStorageType == (byte)ProDOS.StorageType.Directory) {
                if (mHeader.mFileType != FileAttribs.FILE_TYPE_DIR) {
                    Archive.Notes.AddW("Dir entry has wrong file type");
                    return false;
                }
                if (mHeader.mEOF != 0 || mHeader.mStorageSize != 0) {
                    // BLU v2.28 puts a nonzero value in the EOF and storage size fields,
                    // contrary to the spec, but does not generate any data in the archive.
                    // Accept the entry but fix the values.
                    Archive.Notes.AddI("Dir entry has nonzero file length");
                    mHeader.mEOF = 0;
                    mHeader.mStorageSize = 0;
                }
            } else {
                if (mHeader.mFileType == FileAttribs.FILE_TYPE_DIR) {
                    Archive.Notes.AddW("Found DIR type on non-dir entry");
                    return false;
                }
            }

            byte fileNameLen = mHeader.mRawFileName[0];
            if (fileNameLen < 1 || fileNameLen > Binary2.MAX_FILE_NAME_LEN) {
                Archive.Notes.AddW("Invalid filename length (" + fileNameLen + ")");
                return false;
            }
            char[] convChars = new char[fileNameLen];
            for (int i = 0; i < fileNameLen; i++) {
                convChars[i] = (char)mHeader.mRawFileName[i + 1];   // ASCII "conversion"
            }
            mFileName = new string(convChars);
            if (!IsFileNameValid(mFileName)) {
                Archive.Notes.AddW("Invalid filename: '" + mFileName + "'");
                // fix it later
            }

            // TODO(maybe): expose the "native name".  Not sure that was ever used.

            return true;
        }

        /// <summary>
        /// Sets the "is squeezed" flag, based on the entry's attributes and contents.
        /// </summary>
        private void CheckSqueezed() {
            Debug.Assert(Archive.DataStream != null);
            bool isSqueezed = false;

            // If we have enough data, do additional checks.
            if (DataLength >= SqueezeStream.MIN_FULL_HEADER_LEN) {
                Debug.Assert(!IsDirectory);
                // Check the data flag and the filename extension.
                isSqueezed = (mHeader.mDataFlags & (byte)Header.DataFlags.Compressed) != 0;
                if (!isSqueezed && mFileName.EndsWith(SQUEEZE_SUFFIX,
                        StringComparison.InvariantCultureIgnoreCase)) {
                    isSqueezed = true;
                }

                // Attributes say it's squeezed; do a quick check on the magic number.
                if (isSqueezed) {
                    long oldPosn = Archive.DataStream.Position;
                    Archive.DataStream.Position = HeaderFileOffset + Binary2.CHUNK_LEN;
                    byte[] checkBuf = new byte[2];
                    Archive.DataStream.ReadExactly(checkBuf, 0, 2);
                    if (checkBuf[0] != SqueezeStream.MAGIC0 ||
                            checkBuf[1] != SqueezeStream.MAGIC1) {
                        isSqueezed = false;
                    }
                    Archive.DataStream.Position = oldPosn;
                }
            }
            mIsSqueezed = isSqueezed;
        }

        /// <summary>
        /// Creates an object for reading the contents of the entry out of the archive.
        /// </summary>
        internal ArcReadStream CreateReadStream() {
            Debug.Assert(Archive.DataStream != null);
            Archive.DataStream.Position = HeaderFileOffset + Binary2.CHUNK_LEN;
            if (mIsSqueezed) {
                // Expanded length is not recorded in the archive.
                Stream expander = new SqueezeStream(Archive.DataStream, CompressionMode.Decompress,
                    true, true, string.Empty);
                return new ArcReadStream(Archive, -1, null, expander);
            } else {
                // This creates a zero-length stream for directories, which is fine.
                return new ArcReadStream(Archive, mHeader.mEOF, null);
            }
        }

        // IFileEntry
        public void SaveChanges() { /* not used for archives */ }

        /// <summary>
        /// Saves the part source for a future commit.
        /// </summary>
        /// <param name="partSource">Part source object.</param>
        /// <param name="compressFmt">Type of compression to use.</param>
        internal void AddPart(IPartSource partSource, CompressionFormat compressFmt) {
            Debug.Assert(ChangeObject != null);
            Debug.Assert(partSource != IPartSource.NO_SOURCE);
            if (ChangeObject.HasDataPart) {
                throw new ArgumentException("Record already has data fork");
            }
            Debug.Assert(mPendingDataPart == IPartSource.NO_SOURCE);
            Debug.Assert(ChangeObject.mPendingDataPart == IPartSource.NO_SOURCE);
            ChangeObject.mPendingDataPart = partSource;
            ChangeObject.mPendingFormat = compressFmt;
            ChangeObject.HasDataPart = true;
        }

        /// <summary>
        /// Deletes a part of the record.
        /// </summary>
        internal void DeletePart() {
            Debug.Assert(ChangeObject != null);
            if (!ChangeObject.HasDataPart) {
                // Already deleted or never existed.
                throw new FileNotFoundException("Record does not a data fork");
            }
            ChangeObject.HasDataPart = false;
            ChangeObject.mPendingDataPart.Dispose();
            ChangeObject.mPendingDataPart = IPartSource.NO_SOURCE;
            ChangeObject.mHeader.mEOF = 0;
            ChangeObject.mHeader.mStorageSize = 0;
            ChangeObject.mIsSqueezed = false;
            ChangeObject.HeaderDirty = true;    // record change to EOF
        }

        internal void SerializeHeader(byte[] buf) {
            mHeader.Store(buf, 0);
        }

        /// <summary>
        /// Writes the pending data part to the archive file.
        /// </summary>
        /// <param name="outputStream">Stream to write the compressed or uncompressed
        ///   data to.</param>
        /// <param name="tmpBuf1">Temporary buffer, for compression.</param>
        /// <param name="tmpBuf2">Temporary buffer, for compression.</param>
        internal void WriteParts(Stream outputStream, byte[] tmpBuf1, byte[] tmpBuf2) {
            Debug.Assert(ChangeObject == null || ChangeObject == this);
            IPartSource source = mPendingDataPart;
            if (source == IPartSource.NO_SOURCE) {
                // This entry has no parts.  We can throw an error, or just output
                // it as a zero-length file entry.  Try the latter.
                Debug.Assert(mHeader.mEOF == 0);
                Debug.Assert(mHeader.mStorageSize == 0);
                return;
            }
            if (IsDirectory) {
                throw new InvalidOperationException("Directories cannot have data");
            }

            Stream? compStream = null;
            if (mPendingFormat == CompressionFormat.Squeeze ||
                    mPendingFormat == CompressionFormat.Default) {
                compStream = new SqueezeStream(outputStream, CompressionMode.Compress,
                    true, true, "SQ");
            }

            long startPosn = outputStream.Position;
            if (ArcUtil.CopyPartSource(source, outputStream, compStream, null, true, tmpBuf1,
                    out int inputLength, out int outputLength)) {
                // Set the "compressed" flag.  Both BLU and ShrinkIt will recognize a file as
                // squeezed if this is set; it's not necessary to add the ".QQ" extension.
                mHeader.mDataFlags |= (byte)Header.DataFlags.Compressed;
                mIsSqueezed = true;
            } else {
                // Make sure the "compressed" Flag is clear.
                mHeader.mDataFlags &= (byte)~Header.DataFlags.Compressed;
                mIsSqueezed = false;
            }

            // Pad the output to the nearest multiple of 128.  (Might be able to seek to N-1 and
            // write one byte?)
            int storageSize = Binary2.RoundUpChunkLen(outputLength);
            while (outputStream.Position - startPosn < storageSize) {
                outputStream.WriteByte(0x00);
            }

            mHeader.mEOF = (uint)outputLength;
            mHeader.mStorageSize = 0;
            mHeader.mTotalDiskSpace = 0;
        }

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
        /// Resets change state.
        /// </summary>
        /// <remarks>
        /// <para>This is called on the new entry objects after a transaction has completed
        /// successfully.  After a failed transaction it's only necessary to null out the
        /// change object references in the original set of entries.</para>
        /// </remarks>
        internal void CommitChange() {
            if (ChangeObject != this) {
                Debug.Assert(ChangeObject != null);
                CopyFields(this, ChangeObject);
                mHeader = ChangeObject.mHeader;
            } else {
                // Newly-created record.
                HeaderDirty = false;
                mPendingDataPart = IPartSource.NO_SOURCE;
                OrigObject = null;
            }
            ChangeObject = null;

            Debug.Assert(!HeaderDirty);
            Debug.Assert(mPendingDataPart == IPartSource.NO_SOURCE);
        }

        internal void DisposeSources() {
            Debug.Assert(ChangeObject != null);
            if (ChangeObject.mPendingDataPart != IPartSource.NO_SOURCE) {
                ChangeObject.mPendingDataPart.Dispose();
                ChangeObject.mPendingDataPart = IPartSource.NO_SOURCE;
            }
        }

        /// <summary>
        /// Invalidates the file entry object.  Called on the original set of entries after a
        /// successful commit.
        /// </summary>
        internal void Invalidate() {
            Debug.Assert(Archive != null); // harmless, but we shouldn't be invalidating twice
#pragma warning disable CS8625
            Archive = null;
#pragma warning restore CS8625
            ChangeObject = null;
        }

        // IFileEntry
        public int CompareFileName(string fileName) {
            // Simple case-insensitive comparison.
            return string.Compare(mFileName, fileName, StringComparison.OrdinalIgnoreCase);
        }

        // IFileEntry
        public int CompareFileName(string fileName, char fileNameSeparator) {
            return PathName.ComparePathNames(mFileName, SEP_CHAR, fileName,
                fileNameSeparator, PathName.CompareAlgorithm.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Returns true if the string is a valid Binary II file name.
        /// </summary>
        public static bool IsFileNameValid(string fileName) {
            if (fileName.Length < 1 || fileName.Length > Binary2.MAX_FILE_NAME_LEN) {
                return false;
            }
            MatchCollection matches = sFileNameRegex.Matches(fileName);
            return (matches.Count == 1);
        }

        /// <summary>
        /// Sets the "raw" and "cooked" filenames.
        /// </summary>
        /// <param name="fileName">New filename, which may be a partial path.</param>
        /// <exception cref="ArgumentException">Invalid filename.</exception>
        private void SetFileName(string fileName) {
            if (!IsFileNameValid(fileName)) {
                throw new ArgumentException("Invalid filename: '" + fileName + "'");
            }
            mFileName = fileName;
            mHeader.mRawFileName[0] = (byte)fileName.Length;
            for (int i = 0; i < fileName.Length; i++) {
                mHeader.mRawFileName[i + 1] = (byte)fileName[i];
            }
            // Zero out the excess.
            for (int i = fileName.Length; i < Binary2.MAX_FILE_NAME_LEN; i++) {
                mHeader.mRawFileName[i + 1] = 0x00;
            }
        }

        public override string ToString() {
            return "[Binary2-entry '" + FileName + "' change=" +
                (ChangeObject == null ? "(null)" : "(YES)") + "]";
        }
    }
}
