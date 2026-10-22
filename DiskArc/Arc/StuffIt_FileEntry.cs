/*
 * Copyright 2026 faddenSoft
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
using System.Text;

using CommonUtil;
using DiskArc.Comp;
using static DiskArc.Defs;

namespace DiskArc.Arc {
    /// <summary>
    /// One record in a StuffIt archive.
    /// </summary>
    public class StuffIt_FileEntry : IFileEntry {
        internal const char SEP_CHAR = ':';
        internal const int MAX_FILENAME_LEN = 63;
        internal const int FILE_HEADER_LEN = 112;
        internal const int START_FOLDER = 32;
        internal const int END_FOLDER = 33;


        //
        // IFileEntry interfaces.
        //

        public bool IsValid { get { return Archive != null; } }
        public bool IsDubious { get; private set; }
        public bool IsDamaged { get; private set; }

        public bool IsDirectory => mCompDMethod == START_FOLDER;

        public bool HasDataFork => true;
        public bool HasRsrcFork => RsrcLength > 0;
        public bool IsDiskImage => false;

        public IFileEntry ContainingDir => IFileEntry.NO_ENTRY;
        public int Count => 0;

        public string FileName {
            get {
                if (!IsValid) {
                    throw new InvalidOperationException("Invalid object");
                }
                return FolderPrefix + mFileName;
            }
            set {
                // The application is expected to prevent duplicate names.  We don't make any
                // attempt to detect or prevent them.
                CheckChangeAllowed();
                ChangeObject!.SetFileName(value);
            }
        }
        private string mFileName = string.Empty;

        public char DirectorySeparatorChar {
            get => SEP_CHAR;
            set { }
        }

        public byte[] RawFileName {
            get {
                byte[] result = new byte[mRawFileName[0]];
                Array.Copy(mRawFileName, result, result.Length);
                return result;
            }
            set {
                CheckChangeAllowed();
                ChangeObject!.SetFileName(value);
            }
        }

        public string FullPathName { get { return FileName; } }     // TODO? handle HMF

        public bool HasProDOSTypes => false;

        public byte FileType {
            get { return 0; }
            set { }
        }
        public ushort AuxType {
            get { return 0; }
            set { }
        }

        public bool HasHFSTypes => true;

        public uint HFSFileType {
            get {
                return mFileType;
            }
            set {
                CheckChangeAllowed();
                ChangeObject!.mFileType = value;
            }
        }
        public uint HFSCreator {
            get {
                return mCreator;
            }
            set {
                CheckChangeAllowed();
                ChangeObject!.mCreator = value;
            }
        }

        public byte Access {
            get { return FileAttribs.FILE_ACCESS_UNLOCKED; }    // TODO: check flags?
            set { }
        }
        public DateTime CreateWhen {
            get { return TimeStamp.ConvertDateTime_HFS(mCreationDate); }
            set {
                CheckChangeAllowed();
                ChangeObject!.mCreationDate = TimeStamp.ConvertDateTime_HFS(value);
            }
        }
        public DateTime ModWhen {
            get { return TimeStamp.ConvertDateTime_HFS(mModDate); }
            set {
                CheckChangeAllowed();
                ChangeObject!.mModDate = TimeStamp.ConvertDateTime_HFS(value);
            }
        }

        public long StorageSize { get { return mCompRLength + mCompDLength; } }

        public long DataLength => mDataLength;

        public long RsrcLength => mRsrcLength;

        public string Comment {
            get { return string.Empty; }
            set { }
        }

        // Entries do not have children.  (Even with HMF.)
        private static readonly List<IFileEntry> sEmptyList = new List<IFileEntry>();
        public IEnumerator<IFileEntry> GetEnumerator() {
            return sEmptyList.GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() {
            return sEmptyList.GetEnumerator();
        }

        //
        // StuffIt-specific fields.
        //

        /// <summary>
        /// Reference to archive object.
        /// </summary>
        internal StuffIt Archive { get; private set; }

        /// <summary>
        /// Object that receives changes made to the file entry.  Set non-null when a
        /// transaction is opened.  Newly-created records point to themselves.
        /// </summary>
        /// <remarks>
        /// <para>This field is never exposed to the application.  The application does all
        /// operations on the original object.  The only time the application has a direct
        /// reference to a change object is when the entry is newly created.</para>
        /// </remarks>
        internal StuffIt_FileEntry? ChangeObject { get; set; }

        /// <summary>
        /// Link back to original object, from change object.
        /// </summary>
        internal StuffIt_FileEntry? OrigObject { get; set; }

        /// <summary>
        /// True if a change has been made that requires writing the record.
        /// </summary>
        /// <remarks>
        /// If not set, we can just copy the old record to the new archive stream.
        /// </remarks>
        internal bool IsDirty { get; private set; }

        /// <summary>
        /// True if this is an end-folder marker.
        /// </summary>
        internal bool IsEndFolder => mCompDMethod == END_FOLDER;

        /// <summary>
        /// Blank string, or directory partial path ending with ':'.
        /// </summary>
        internal string FolderPrefix { get; set; } = string.Empty;


        //
        // Fields from the file header.  Names come from StuffIt.h excerpt in the 1.5 User's Guide.
        //
        private byte mCompRMethod;
        private byte mCompDMethod;
        private byte[] mRawFileName = new byte[MAX_FILENAME_LEN + 1];
        private uint mFileType;
        private uint mCreator;
        private ushort mFndrFlags;      // TODO: preserve these when interacting with HFS/MFS
        private uint mCreationDate;
        private uint mModDate;
        private uint mRsrcLength;
        private uint mDataLength;
        private uint mCompRLength;
        private uint mCompDLength;
        private ushort mRsrcCRC;
        private ushort mDataCRC;
        private byte mRPad, mDPad;
        private uint mReserved;
        private ushort mHdrCRC;

        /// <summary>
        /// Offset of file header.
        /// </summary>
        private long mHeaderOffset;


        /// <summary>
        /// Converts a StuffIt compression method value to a CompressionFormat enum value.
        /// </summary>
        private static CompressionFormat GetCompressionFormat(byte methodByte) {
            switch (methodByte) {
                case 0: return CompressionFormat.Uncompressed;
                case 1: return CompressionFormat.StuffRLE;
                case 2: return CompressionFormat.StuffLZC;
                case 3: return CompressionFormat.StuffHuff;
                case 5: return CompressionFormat.StuffLZAH;
                case 6: return CompressionFormat.StuffFixedHuff;
                case 8: return CompressionFormat.StuffMW;
                case 13: return CompressionFormat.StuffLZHuffman;
                case 15: return CompressionFormat.StuffArsenic;
                case START_FOLDER: return CompressionFormat.Uncompressed;
                case END_FOLDER: return CompressionFormat.Uncompressed;
                default: return CompressionFormat.Unknown;
            }
        }


        /// <summary>
        /// Private constructor.
        /// </summary>
        private StuffIt_FileEntry(StuffIt archive) {
            Archive = archive;
        }

        /// <summary>
        /// Creates a new, empty record.
        /// </summary>
        internal static StuffIt_FileEntry CreateNew(StuffIt archive) {
            StuffIt_FileEntry newEntry = new StuffIt_FileEntry(archive);
            newEntry.IsDirty = true;
            return newEntry;
        }

        /// <summary>
        /// Clones a file entry for use as a change object.
        /// </summary>
        internal static StuffIt_FileEntry CreateChangeObject(StuffIt_FileEntry src) {
            if (src.IsDamaged) {
                // Dubious is okay, damaged is not.
                throw new InvalidOperationException("Cannot clone a damaged entry");
            }
            Debug.Assert(src.ChangeObject == null);

            StuffIt_FileEntry newEntry = new StuffIt_FileEntry(src.Archive);
            CopyFields(newEntry, src);
            return newEntry;
        }

        /// <summary>
        /// Copies all fields from one object to another.
        /// </summary>
        internal static void CopyFields(StuffIt_FileEntry newEntry, StuffIt_FileEntry src) {
            throw new Exception("TODO");
        }

        /// <summary>
        /// Reads the next record from the StuffIt archive.
        /// </summary>
        internal static StuffIt_FileEntry? ReadNextRecord(StuffIt archive) {
            StuffIt_FileEntry entry = new StuffIt_FileEntry(archive);
            if (!entry.ReadRecord()) {
                return null;
            }
            return entry;
        }

        /// <summary>
        /// Reads a record from the current position in the archive data stream.
        /// On return, the stream will be positioned at the start of the following record, or EOF.
        /// </summary>
        /// <returns>True on success.</returns>
        private bool ReadRecord() {
            Stream stream = Archive.DataStream!;
            mHeaderOffset = stream.Position;

            byte[] hdrBuf = new byte[FILE_HEADER_LEN];
            try {
                stream.ReadExactly(hdrBuf, 0, FILE_HEADER_LEN);
            } catch (EndOfStreamException) {
                Archive.Notes.AddE("Ran off end of archive while reading file header");
                return false;
            }

            int offset = 0;
            mCompRMethod = RawData.ReadU8(hdrBuf, ref offset);
            mCompDMethod = RawData.ReadU8(hdrBuf, ref offset);
            Array.Copy(hdrBuf, offset, mRawFileName, 0, mRawFileName.Length);
            offset += mRawFileName.Length;
            mFileType = RawData.ReadU32BE(hdrBuf, ref offset);
            mCreator = RawData.ReadU32BE(hdrBuf, ref offset);
            mFndrFlags = RawData.ReadU16BE(hdrBuf, ref offset);
            mCreationDate = RawData.ReadU32BE(hdrBuf, ref offset);
            mModDate = RawData.ReadU32BE(hdrBuf, ref offset);
            mRsrcLength = RawData.ReadU32BE(hdrBuf, ref offset);
            mDataLength = RawData.ReadU32BE(hdrBuf, ref offset);
            mCompRLength = RawData.ReadU32BE(hdrBuf, ref offset);
            mCompDLength = RawData.ReadU32BE(hdrBuf, ref offset);
            mRsrcCRC = RawData.ReadU16BE(hdrBuf, ref offset);
            mDataCRC = RawData.ReadU16BE(hdrBuf, ref offset);
            mRPad = RawData.ReadU8(hdrBuf, ref offset);
            mDPad = RawData.ReadU8(hdrBuf, ref offset);
            mReserved = RawData.ReadU32BE(hdrBuf, ref offset);
            mHdrCRC = RawData.ReadU16BE(hdrBuf, ref offset);
            Debug.Assert(offset == FILE_HEADER_LEN);

            // Convert filename to cooked form.
            byte nameLen = mRawFileName[0];
            if (nameLen < 1 || nameLen > MAX_FILENAME_LEN) {
                return false;
            }
            mFileName = MacChar.MacToUnicode(mRawFileName, 1, nameLen,
                MacChar.Encoding.RomanShowCtrl);

            // Validate values.
            ushort calcHdrCrc = CRC16.ARC_OnBuffer(0x0000, hdrBuf, 0, FILE_HEADER_LEN - 2);
            if (calcHdrCrc != mHdrCRC) {
                Archive.Notes.AddW("Corrupted header: expected CRC=$" + mHdrCRC.ToString("X4") +
                    ", actual=$" + calcHdrCrc.ToString("X4"));
                //return false;   // try anyway?
            }
            if (mCompRMethod == 0 && mRsrcLength != mCompRLength) {
                Archive.Notes.AddW("Length mismatch on uncompresed rsrc fork");
                return false;
            }
            if (mCompDMethod == 0 && mDataLength != mCompDLength) {
                Archive.Notes.AddW("Length mismatch on uncompresed data fork");
                return false;
            }
            if (mCompRLength > stream.Length || mCompDLength > stream.Length) {
                Archive.Notes.AddW("Entry has impossibly long fork");
                return false;
            }

            // If this is not a directory entry, seek past the compressed data.
            if (mCompDMethod != START_FOLDER && mCompDMethod != END_FOLDER) {
                long skipLen = mCompRLength + mCompDLength;
                if (stream.Length - stream.Position < skipLen) {
                    Archive.Notes.AddE("Compressed data truncated");
                    return false;
                }
                stream.Seek(skipLen, SeekOrigin.Current);
            }

            return true;
        }

        /// <summary>
        /// Creates an object for reading the contents of the entry out of the archive.
        /// </summary>
        /// <remarks>
        /// <para>We allow access to file forks and disk images.  We don't allow direct access
        /// to filename or comment threads, or arbitrary threads we don't recognize.</para>
        /// </remarks>
        /// <exception cref="FileNotFoundException">Part does not exist.</exception>
        /// <exception cref="NotSupportedException">Compression format not supported.</exception>
        internal ArcReadStream CreateReadStream(FilePart part) {
            long fileOffset;
            uint uncompLen;
            uint compLen;
            ushort crc;
            CompressionFormat format;
            if (part == FilePart.RsrcFork) {
                fileOffset = mHeaderOffset + FILE_HEADER_LEN;
                uncompLen = mRsrcLength;
                compLen = mCompRLength;
                crc = mRsrcCRC;
                format = GetCompressionFormat(mCompRMethod);
            } else if (part == FilePart.DataFork) {
                fileOffset = mHeaderOffset + FILE_HEADER_LEN + mCompRLength;
                uncompLen = mDataLength;
                compLen = mCompDLength;
                crc = mDataCRC;
                format = GetCompressionFormat(mCompDMethod);
            } else {
                throw new FileNotFoundException("Record does not have part: " + part);
            }

            Checker checker = new CheckARC(0x0000, crc);

            // Seek to start of compressed or uncompressed data.
            Debug.Assert(Archive.DataStream != null);
            Archive.DataStream.Position = fileOffset;

            if (format == CompressionFormat.Uncompressed) {
                return new ArcReadStream(Archive, compLen, checker);
            }

            Stream expander;
            switch (format) {
                case CompressionFormat.StuffLZC:
                    expander = new LZCStream(Archive.DataStream, CompressionMode.Decompress,
                        true, true, compLen, 14, true);
                    break;
                default:
                    throw new NotSupportedException("Compression format not supported");
            }
            return new ArcReadStream(Archive, uncompLen, checker, expander);
        }

        // IFileEntry
        public bool GetPartInfo(FilePart part, out long length, out long storageSize,
                out CompressionFormat format) {
            if (part == FilePart.DataFork) {
                length = mDataLength;
                storageSize = mCompDLength;
                format = GetCompressionFormat(mCompDMethod);
                return true;
            } else if (part == FilePart.RsrcFork) {
                length = mRsrcLength;
                storageSize = mCompRLength;
                format = GetCompressionFormat(mCompRMethod);
                return true;
            } else {
                length = -1;
                storageSize = 0;
                format = CompressionFormat.Uncompressed;
                return false;
            }
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
        /// Saves the part source for a future commit.
        /// </summary>
        /// <param name="part">Part to update.</param>
        /// <param name="partSource">Part source object.</param>
        /// <param name="compressFmt">Type of compression to use.</param>
        internal void AddPart(FilePart part, IPartSource partSource,
                CompressionFormat compressFmt) {
            Debug.Assert(ChangeObject != null);

            switch (compressFmt) {
                case CompressionFormat.Default:
                case CompressionFormat.Uncompressed:
                case CompressionFormat.StuffRLE:
                case CompressionFormat.StuffLZC:
                case CompressionFormat.StuffHuff:
                    break;
                default:
                    throw new ArgumentException("Compression format " + compressFmt +
                        " not supported for this archive");
            }
            throw new Exception("TODO");
        }

        /// <summary>
        /// Deletes a part of the record.
        /// </summary>
        /// <param name="part">Part to delete.</param>
        internal void DeletePart(FilePart part) {
            Debug.Assert(ChangeObject != null);
            throw new Exception("TODO");
        }

        /// <summary>
        /// Writes the record header and data to the output stream.
        /// </summary>
        /// <remarks>
        /// Because all I/O is deferred, we can't know the storage size of most data items until
        /// after they have been written to the archive stream.
        /// </remarks>
        internal void WriteRecord(Stream? srcStream, Stream outputStream) {
            throw new Exception("TODO");
        }


        /// <summary>
        /// Resets change state.
        /// </summary>
        /// <remarks>
        /// <para>This is called on the new entry objects after a transaction has completed
        /// successfully.</para>
        /// </remarks>
        internal void CommitChange() {
            Debug.Assert(ChangeObject != null);
            throw new Exception("TODO");
        }

        internal void DisposeSources() {
            Debug.Assert(ChangeObject != null);
            throw new Exception("TODO");
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

        #region Filenames

        // IFileEntry
        public int CompareFileName(string fileName) {
            return string.Compare(mFileName, fileName, StringComparison.OrdinalIgnoreCase);
        }

        // IFileEntry
        public int CompareFileName(string fileName, char fileNameSeparator) {
            return PathName.ComparePathNames(mFileName, SEP_CHAR, fileName,
                fileNameSeparator, PathName.CompareAlgorithm.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Sets the "raw" and "cooked" filenames, from a string.
        /// </summary>
        /// <remarks>
        /// <para>All characters are treated as Mac OS Roman.  Characters outside the character 
        /// set will be rejected.</para>
        /// </remarks>
        /// <param name="fileName">New filename, which may be a partial path.</param>
        /// <exception cref="ArgumentException">Invalid filename.</exception>
        private void SetFileName(string fileName) {
            if (!IsFileNameValid(fileName)) {
                throw new ArgumentException("Invalid filename: '" + fileName + "'");
            }
            mFileName = fileName;
            mRawFileName = new byte[fileName.Length];
            MacChar.UnicodeToMac(fileName, mRawFileName, 0, MacChar.Encoding.RomanShowCtrl);

            // TODO: deal with filename prefix for HMF
        }

        /// <summary>
        /// Determines whether the string is a valid NuFX filename.
        /// </summary>
        /// <param name="fileName">Filename to check.</param>
        /// <returns>True if all is well.</returns>
        public static bool IsFileNameValid(string fileName) {
            if (string.IsNullOrEmpty(fileName)) {
                return false;       // must be at least one char
            }
            if (fileName.Length > MAX_FILENAME_LEN) {
                return false;       // too long
            }
            if (!MacChar.IsStringValid(fileName, MacChar.Encoding.RomanShowCtrl)) {
                return false;       // invalid chars found
            }
            return true;
        }

        /// <summary>
        /// Sets the "raw" and "cooked" filenames, from a byte array.
        /// </summary>
        /// <param name="rawFileName">New filename, which may be a partial path.</param>
        /// <exception cref="ArgumentException">Invalid filename.</exception>
        private void SetFileName(byte[] rawFileName) {
            if (rawFileName.Length == 0) {
                throw new ArgumentException("Filename must be at least one character");
            }
            if (rawFileName.Length > MAX_FILENAME_LEN) {
                throw new ArgumentException("Filename is excessively long (" +
                    rawFileName.Length + ")");
            }
            mRawFileName = new byte[rawFileName.Length];
            Array.Copy(rawFileName, mRawFileName, rawFileName.Length);
            mFileName = MacChar.MacToUnicode(rawFileName, 0, rawFileName.Length,
                MacChar.Encoding.RomanShowCtrl);
        }

        #endregion Filenames

        public override string ToString() {
            StringBuilder sb = new StringBuilder();
            sb.Append("[StuffIt-entry '");
            sb.Append(FileName);
            sb.Append("' ");
            if (IsDirectory) {
                sb.Append(" STF");
            }
            if (IsEndFolder) {
                sb.Append(" ENF");
            }
            sb.Append("]");
            return sb.ToString();
        }
    }
}