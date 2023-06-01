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
using System.Text;

using CommonUtil;
using static DiskArc.Defs;

namespace DiskArc.Arc {
    /// <summary>
    /// One record in a ZIP archive.
    /// </summary>
    /// <remarks>
    /// <para>We don't attempt to use ZIP fields to store Apple II-specific values.  It would
    /// only make sense to do so if archives were unpacked directly on an Apple II.</para>
    /// <para>We currently ignore the "extra" data fields.  Any such data in the central directory
    /// will be discarded when the archive is rewritten.  The LFH copy will be retained for
    /// records that aren't modified, but the LFH and CDFH extras can be different.  We don't want
    /// to blindly retain the extra data because it might conflict with other changes, e.g. if
    /// the extra data has a high-resolution version of the modification date, we would need to
    /// update it when the modification date changes.  (We could try to preserve the CDFH copy
    /// for unmodified records, but there's currently little value in doing so.)</para>
    /// </remarks>
    public class Zip_FileEntry : IFileEntry {
        internal const char SEP_CHAR = '/';                 // filename separator character
        private const int MAX_VAR_LEN = 65535;              // max value for a ushort
        private const int BIG_PATHNAME_LEN = 1024;          // be reasonable
        private const int VERSION_NEEDED = 0x0014;          // 2.0, minimum for Deflate
        private const int VERSION_MADE_BY = 0x003f;         // DOS / 6.3 (because of UTF-8)
        private const int EXTERNAL_ATTRIBS = 0x0020;        // MS-DOS "archive" flag
        private const ushort DATA_DESC_FLAG = 0x0008;       // bit 3, data descriptor flag
        private const ushort LANGUAGE_ENC_FLAG = 0x0800;    // bit 11, EFS / language encoding flag
        private const int COMP_UNCOMPRESSED = 0;            // comp method == uncompressed
        private const int COMP_DEFLATE = 8;                 // comp method == Deflate

        // Compression level for Deflate.  A test with the A2 Romulan CD-ROM showed that
        // SmallestSize took 45% more time and reduced the file size by an additional 0.1%.
        private const CompressionLevel COMP_LEVEL = CompressionLevel.Optimal;

        //
        // IFileEntry interfaces.
        //

        public bool IsDubious { get; private set; }

        public bool IsDamaged { get; private set; }

        public bool IsDirectory {
            get {
                // Directories are just zero-length files whose names end with '/'.
                return DataLength == 0 && FileName.EndsWith(SEP_CHAR);
            }
        }

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
                // The application is expected to prevent duplicate names.  We don't make any
                // attempt to detect or prevent them, because the ZIP file spec doesn't require it.
                // We decide CP437 vs. UTF-8 when writing the new entry out.
                CheckChangeAllowed();
                ChangeObject!.SetFileName(value);
                ChangeObject.IsDirty = true;
            }
        }
        public char DirectorySeparatorChar {
            get => SEP_CHAR;
            set { }
        }
        public byte[] RawFileName {
            get {
                if (mCDFH.HasUnicodeText) {
                    return Encoding.UTF8.GetBytes(mFileName);
                } else {
                    return CP437.FromUnicode(mFileName);
                }
            }
            set {
                // We don't store the raw form, so just convert it according to the current
                // flag setting and save the cooked version.
                CheckChangeAllowed();
                string newFileName;
                if (mCDFH.HasUnicodeText) {
                    newFileName = Encoding.UTF8.GetString(value);
                } else {
                    newFileName = CP437.ToUnicode(value, 0, value.Length);
                }
                ChangeObject!.SetFileName(newFileName);
                ChangeObject.IsDirty = true;
            }
        }

        public string FullPathName { get { return FileName; } }

        public bool HasProDOSTypes => false;
        public byte FileType {
            get { return 0; }
            set { }
        }
        public ushort AuxType {
            get { return 0; }
            set { }
        }

        public bool HasHFSTypes => false;
        public uint HFSFileType {
            get { return 0; }
            set { }
        }
        public uint HFSCreator {
            get { return 0; }
            set { }
        }

        // There are various ways to preserve file access permissions.  Not important for us.
        public byte Access {
            get { return FileAttribs.FILE_ACCESS_UNLOCKED; }
            set { }
        }
        public DateTime CreateWhen {
            get { return TimeStamp.NO_DATE; }
            set { }
        }
        public DateTime ModWhen {
            get { return TimeStamp.ConvertDateTime_MSDOS(mCDFH.LastModDate, mCDFH.LastModTime); }
            set {
                CheckChangeAllowed();
                TimeStamp.ConvertDateTime_MSDOS(value, out ushort date, out ushort time);
                ChangeObject!.mCDFH.LastModDate = date;
                ChangeObject.mCDFH.LastModTime = time;
                ChangeObject.IsDirty = true;
            }
        }

        public long StorageSize { get { return mCDFH.CompSize; } }

        public long DataLength { get { return mCDFH.UncompSize; } }

        public long RsrcLength => 0;

        public string Comment {
            get {
                return mComment;
            }
            set {
                CheckChangeAllowed();
                ChangeObject!.mComment = value;
                ChangeObject.IsDirty = true;
            }
        }

        private static readonly List<IFileEntry> sEmptyList = new List<IFileEntry>();
        public IEnumerator<IFileEntry> GetEnumerator() {
            return sEmptyList.GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() {
            return sEmptyList.GetEnumerator();
        }

        //
        // ZIP-specific fields.
        //

        /// <summary>
        /// Central Directory File Header.
        /// </summary>
        private class CentralDirFileHeader {
            public const int LENGTH = 46;
            public static readonly byte[] SIGNATURE = new byte[] { 0x50, 0x4b, 0x01, 0x02 };
            private const byte OS_UNIX = 0x03;      // for "version made by" field

            public ushort VersionMadeBy { get; set; }
            public ushort VersionNeeded { get; set; }
            public ushort Flags { get; set; }
            public ushort CompMethod { get; set; }
            public ushort LastModTime { get; set; }
            public ushort LastModDate { get; set; }
            public uint Crc32 { get; set; }
            public uint CompSize { get; set; }
            public uint UncompSize { get; set; }
            public ushort FileNameLength { get; set; }
            public ushort ExtraFieldLength { get; set; }
            public ushort CommentLength { get; set; }
            public ushort DiskNumberStart { get; set; }
            public ushort InternalAttribs { get; set; }
            public uint ExternalAttribs { get; set; }
            public uint LocalHeaderOffset { get; set; }

            /// <summary>
            /// Length of optional data descriptor.
            /// </summary>
            //public int DataDescriptorLength { get; set; }

            public long FileRecordLength { get; set; } = -1;

            /// <summary>
            /// File offset of start of data in the archive.
            /// </summary>
            public long FileDataOffset { get; set; } = -1;

            /// <summary>
            /// True if the filename and comment fields are encoded with UTF-8, rather than CP437.
            /// </summary>
            public bool HasUnicodeText {
                get {
                    if ((Flags & LANGUAGE_ENC_FLAG) != 0) {
                        // general flags bit 11
                        return true;
                    }
                    if (VersionMadeBy >> 8 == OS_UNIX) {
                        // Info-ZIP and 7-Zip use this field to determine the default encoding.
                        // Info-ZIP on Mac OS uses UTF-8 but doesn't set bit 11.
                        return true;
                    }
                    return false;
                }
            }

            public CompressionFormat Format {
                get {
                    if (CompMethod == COMP_DEFLATE) {
                        return CompressionFormat.Deflate;
                    } else if (CompMethod == COMP_UNCOMPRESSED) {
                        return CompressionFormat.Uncompressed;
                    } else {
                        return CompressionFormat.Unknown;
                    }
                }
            }

            public CentralDirFileHeader() { }

            /// <summary>
            /// Copy constructor.
            /// </summary>
            public CentralDirFileHeader(CentralDirFileHeader src) {
                // Clone the fixed-length part by doing a simple store/load in a temporary buffer.
                byte[] buf = new byte[LENGTH];
                src.Store(buf, 0);
                Load(buf, 0);
                FileRecordLength = src.FileRecordLength;
                FileDataOffset = src.FileDataOffset;
            }

            /// <summary>
            /// Loads the fixed-length portion of the file header.
            /// </summary>
            public void Load(byte[] buf, int offset) {
                int startOffset = offset;
                for (int i = 0; i < SIGNATURE.Length; i++) {
                    if (buf[offset++] != SIGNATURE[i]) {
                        throw new InvalidDataException("Signature does not match");
                    }
                }
                VersionMadeBy = RawData.ReadU16LE(buf, ref offset);
                VersionNeeded = RawData.ReadU16LE(buf, ref offset);
                Flags = RawData.ReadU16LE(buf, ref offset);
                CompMethod = RawData.ReadU16LE(buf, ref offset);
                LastModTime = RawData.ReadU16LE(buf, ref offset);
                LastModDate = RawData.ReadU16LE(buf, ref offset);
                Crc32 = RawData.ReadU32LE(buf, ref offset);
                CompSize = RawData.ReadU32LE(buf, ref offset);
                UncompSize = RawData.ReadU32LE(buf, ref offset);
                FileNameLength = RawData.ReadU16LE(buf, ref offset);
                ExtraFieldLength = RawData.ReadU16LE(buf, ref offset);
                CommentLength = RawData.ReadU16LE(buf, ref offset);
                DiskNumberStart = RawData.ReadU16LE(buf, ref offset);
                InternalAttribs = RawData.ReadU16LE(buf, ref offset);
                ExternalAttribs = RawData.ReadU32LE(buf, ref offset);
                LocalHeaderOffset = RawData.ReadU32LE(buf, ref offset);
                Debug.Assert(offset - startOffset == LENGTH);
            }

            /// <summary>
            /// Stores the fixed-length portion of the file header.
            /// </summary>
            public void Store(byte[] buf, int offset) {
                int startOffset = offset;
                Array.Copy(SIGNATURE, buf, SIGNATURE.Length);
                offset += SIGNATURE.Length;
                RawData.WriteU16LE(buf, ref offset, VersionMadeBy);
                RawData.WriteU16LE(buf, ref offset, VersionNeeded);
                RawData.WriteU16LE(buf, ref offset, Flags);
                RawData.WriteU16LE(buf, ref offset, CompMethod);
                RawData.WriteU16LE(buf, ref offset, LastModTime);
                RawData.WriteU16LE(buf, ref offset, LastModDate);
                RawData.WriteU32LE(buf, ref offset, Crc32);
                RawData.WriteU32LE(buf, ref offset, CompSize);
                RawData.WriteU32LE(buf, ref offset, UncompSize);
                RawData.WriteU16LE(buf, ref offset, FileNameLength);
                RawData.WriteU16LE(buf, ref offset, ExtraFieldLength);
                RawData.WriteU16LE(buf, ref offset, CommentLength);
                RawData.WriteU16LE(buf, ref offset, DiskNumberStart);
                RawData.WriteU16LE(buf, ref offset, InternalAttribs);
                RawData.WriteU32LE(buf, ref offset, ExternalAttribs);
                RawData.WriteU32LE(buf, ref offset, LocalHeaderOffset);
                Debug.Assert(offset - startOffset == LENGTH);
            }
        }

        /// <summary>
        /// Local File Header.
        /// </summary>
        private class LocalFileHeader {
            public const int LENGTH = 30;
            public static readonly byte[] SIGNATURE = new byte[] { 0x50, 0x4b, 0x03, 0x04 };

            public ushort VersionNeeded { get; set; }
            public ushort Flags { get; set; }
            public ushort CompMethod { get; set; }
            public ushort LastModTime { get; set; }
            public ushort LastModDate { get; set; }
            public uint Crc32 { get; set; }
            public uint CompSize { get; set; }
            public uint UncompSize { get; set; }
            public ushort FileNameLength { get; set; }
            public ushort ExtraFieldLength { get; set; }

            /// <summary>
            /// Single buffer with filename, extra field, and comment, in that order.
            /// </summary>
            //public byte[] VarData { get; set; } = RawData.EMPTY_BYTE_ARRAY;

            public LocalFileHeader() { }

            /// <summary>
            /// Constructs a new local file header object from the central directory file
            /// header object.
            /// </summary>
            public LocalFileHeader(CentralDirFileHeader cdfh) {
                VersionNeeded = cdfh.VersionNeeded;
                Flags = cdfh.Flags;
                CompMethod = cdfh.CompMethod;
                LastModTime = cdfh.LastModTime;
                LastModDate = cdfh.LastModDate;
                Crc32 = cdfh.Crc32;
                CompSize = cdfh.CompSize;
                UncompSize = cdfh.UncompSize;
                FileNameLength = cdfh.FileNameLength;
                ExtraFieldLength = cdfh.ExtraFieldLength;
            }

            /// <summary>
            /// Loads the fixed-length portion of the file header.
            /// </summary>
            public void Load(byte[] buf, int offset) {
                int startOffset = offset;
                for (int i = 0; i < SIGNATURE.Length; i++) {
                    if (buf[offset++] != SIGNATURE[i]) {
                        throw new InvalidDataException("Signature does not match");
                    }
                }
                VersionNeeded = RawData.ReadU16LE(buf, ref offset);
                Flags = RawData.ReadU16LE(buf, ref offset);
                CompMethod = RawData.ReadU16LE(buf, ref offset);
                LastModTime = RawData.ReadU16LE(buf, ref offset);
                LastModDate = RawData.ReadU16LE(buf, ref offset);
                Crc32 = RawData.ReadU32LE(buf, ref offset);
                CompSize = RawData.ReadU32LE(buf, ref offset);
                UncompSize = RawData.ReadU32LE(buf, ref offset);
                FileNameLength = RawData.ReadU16LE(buf, ref offset);
                ExtraFieldLength = RawData.ReadU16LE(buf, ref offset);
                Debug.Assert(offset - startOffset == LENGTH);
            }

            /// <summary>
            /// Stores the fixed-length portion of the file header.
            /// </summary>
            public void Store(byte[] buf, int offset) {
                int startOffset = offset;
                Array.Copy(SIGNATURE, buf, SIGNATURE.Length);
                offset += SIGNATURE.Length;
                RawData.WriteU16LE(buf, ref offset, VersionNeeded);
                RawData.WriteU16LE(buf, ref offset, Flags);
                RawData.WriteU16LE(buf, ref offset, CompMethod);
                RawData.WriteU16LE(buf, ref offset, LastModTime);
                RawData.WriteU16LE(buf, ref offset, LastModDate);
                RawData.WriteU32LE(buf, ref offset, Crc32);
                RawData.WriteU32LE(buf, ref offset, CompSize);
                RawData.WriteU32LE(buf, ref offset, UncompSize);
                RawData.WriteU16LE(buf, ref offset, FileNameLength);
                RawData.WriteU16LE(buf, ref offset, ExtraFieldLength);
                Debug.Assert(offset - startOffset == LENGTH);
            }
        }

        /// <summary>
        /// Data Descriptor.
        /// </summary>
        public class DataDescriptor {
            public const int LENGTH = 16;
            public static readonly byte[] SIGNATURE = new byte[] { 0x50, 0x4b, 0x07, 0x08 };

            public uint Crc32;
            public uint CompSize;
            public uint UncompSize;

            /// <summary>
            /// Loads the fixed-length portion of the file header.
            /// </summary>
            public void Load(byte[] buf, int offset) {
                int startOffset = offset;
                for (int i = 0; i < SIGNATURE.Length; i++) {
                    if (buf[offset++] != SIGNATURE[i]) {
                        throw new InvalidDataException("Signature does not match");
                    }
                }
                Crc32 = RawData.ReadU32LE(buf, ref offset);
                CompSize = RawData.ReadU32LE(buf, ref offset);
                UncompSize = RawData.ReadU32LE(buf, ref offset);
                Debug.Assert(offset - startOffset == LENGTH);
            }
        }

        /// <summary>
        /// Reference to archive object.
        /// </summary>
        internal Zip Archive { get; private set; }

        /// <summary>
        /// Object that receives changes made to the file entry.  Set non-null when a
        /// transaction is opened.  Newly-created records point to themselves.
        /// </summary>
        /// <remarks>
        /// <para>This field is never exposed to the application.  The application does all
        /// operations on the original object.  The only time the application has a direct
        /// reference to a change object is when the entry is newly created.</para>
        /// </remarks>
        internal Zip_FileEntry? ChangeObject { get; set; }

        /// <summary>
        /// Link back to original object, from change object.
        /// </summary>
        internal Zip_FileEntry? OrigObject { get; set; }

        /// <summary>
        /// True if a change has been made that requires writing the record.
        /// </summary>
        /// <remarks>
        /// If not set, we can just copy the old record to the new archive stream.
        /// </remarks>
        internal bool IsDirty { get; private set; }

        /// <summary>
        /// True if the filename and comment use UTF-8 encoding.
        /// </summary>
        /// <remarks>
        /// This exists so that the regression tests can confirm that the archive has the
        /// expected encoding.  Applications should not need to use this, as the encoding is
        /// factored into the filename/comment string conversions.
        /// </remarks>
        public bool HasUnicodeText { get { return mCDFH.HasUnicodeText; } }

        /// <summary>
        /// Central directory header entry for this record.
        /// </summary>
        private CentralDirFileHeader mCDFH = new CentralDirFileHeader();

        // For internal validation.
        internal long LocalHeaderOffsetCheck { get { return mCDFH.LocalHeaderOffset; } }
        internal long FileRecordLengthCheck { get { return mCDFH.FileRecordLength; } }

        /// <summary>
        /// Filename.
        /// </summary>
        private string mFileName = string.Empty;

        /// <summary>
        /// Comment.
        /// </summary>
        private string mComment = string.Empty;

        /// <summary>
        /// True if the change object has file data.  This will be false for newly-created
        /// entries, and entries where DeletePart was used.  Only meaningful in change objects.
        /// </summary>
        internal bool HasDataPart { get; private set; } = true;

        /// <summary>
        /// Pending data.  Only set in change objects.  Will be NO_SOURCE if we're not replacing
        /// the current record contents.
        /// </summary>
        private IPartSource mPendingDataPart = IPartSource.NO_SOURCE;

        /// <summary>
        /// Format to use for pending data.  Only set in change objects.
        /// </summary>
        private CompressionFormat mPendingFormat = CompressionFormat.Default;


        /// <summary>
        /// Private constructor.
        /// </summary>
        private Zip_FileEntry(Zip archive) {
            Archive = archive;
        }

        /// <summary>
        /// Creates a new, empty record.
        /// </summary>
        internal static Zip_FileEntry CreateNew(Zip archive) {
            Zip_FileEntry newEntry = new Zip_FileEntry(archive);
            newEntry.HasDataPart = false;
            newEntry.IsDirty = true;
            return newEntry;
        }

        /// <summary>
        /// Clones a file entry for use as a change object.
        /// </summary>
        internal static Zip_FileEntry CreateChangeObject(Zip_FileEntry src) {
            if (src.IsDamaged) {
                // Dubious is okay, damaged is not.
                throw new InvalidOperationException("Cannot clone a damaged entry");
            }
            Debug.Assert(src.ChangeObject == null);

            Zip_FileEntry newEntry = new Zip_FileEntry(src.Archive);
            CopyFields(newEntry, src);

            // Invalidate these values to ensure that we're setting them when writing the
            // record out.
            newEntry.mCDFH.LocalHeaderOffset = 0xcccccccc;
            newEntry.mCDFH.FileDataOffset = 0xcccccccd;
            newEntry.mCDFH.FileRecordLength = 0xccccccce;

            return newEntry;
        }

        /// <summary>
        /// Copies all fields from one object to another.
        /// </summary>
        internal static void CopyFields(Zip_FileEntry newEntry, Zip_FileEntry src) {
            // Copy the central directory values.
            newEntry.mCDFH = new CentralDirFileHeader(src.mCDFH);
            // Copy the processed filename and comment (which are immutable strings).
            newEntry.mFileName = src.mFileName;
            newEntry.mComment = src.mComment;
        }

        /// <summary>
        /// Reads the next record from the ZIP archive.
        /// </summary>
        internal static Zip_FileEntry? ReadNextRecord(Zip archive) {
            Zip_FileEntry entry = new Zip_FileEntry(archive);
            if (!entry.ReadRecord()) {
                return null;
            }
            return entry;
        }

        /// <summary>
        /// Reads a Central Directory record from the current position in the archive data stream.
        /// On exit, the stream will be positioned immediately after the CD record.
        /// </summary>
        /// <returns>True on success.</returns>
        private bool ReadRecord() {
            Stream stream = Archive.DataStream!;
            byte[] hdrBuf = new byte[CentralDirFileHeader.LENGTH];
            try {
                stream.ReadExactly(hdrBuf, 0, CentralDirFileHeader.LENGTH);
            } catch (EndOfStreamException) {
                Archive.Notes.AddE("Ran off end of archive while reading central dir");
                return false;
            }
            mCDFH.Load(hdrBuf, 0);
            int cdVarLen = mCDFH.FileNameLength + mCDFH.ExtraFieldLength + mCDFH.CommentLength;
            byte[] cdVarBuf = new byte[cdVarLen];
            try {
                stream.ReadExactly(cdVarBuf, 0, cdVarLen);
            } catch (EndOfStreamException) {
                Archive.Notes.AddE("Ran off end of archive while reading central dir: varLen="
                    + cdVarLen);
                return false;
            }

            long cdPosn = stream.Position;

            // Extract the filename and comment from the CD file header.
            if (mCDFH.HasUnicodeText) {
                mFileName = Encoding.UTF8.GetString(cdVarBuf, 0, mCDFH.FileNameLength);
                mComment = Encoding.UTF8.GetString(cdVarBuf,
                    mCDFH.FileNameLength + mCDFH.ExtraFieldLength, mCDFH.CommentLength);
            } else {
                mFileName = CP437.ToUnicode(cdVarBuf, 0, mCDFH.FileNameLength);
                mComment = CP437.ToUnicode(cdVarBuf,
                    mCDFH.FileNameLength + mCDFH.ExtraFieldLength, mCDFH.CommentLength);
            }

            // The lengths will be 0xffffffff for ZIP64 archives, which we don't handle.  I've
            // also seen these for normal ZIP archives (e.g. "zork1-cpm.zip"), but that's not
            // allowed by the spec.  These can only be read in a streaming mode, because you
            // have to uncompress the file to figure out where it ends.
            // TODO: support these; unzip and Windows Explorer handle them (though Explorer
            //   fails if you try to copy a file in or otherwise edit it)
            if (mCDFH.UncompSize == 0xffffffff || mCDFH.CompSize == 0xffffffff) {
                Archive.Notes.AddE("Found entry with no length stored in central dir");
                IsDamaged = true;
                return true;
            }

            //
            // Confirm the local file header is where we expect, and find the start of data.
            //

            byte[] lfhBuf = new byte[LocalFileHeader.LENGTH];
            stream.Position = mCDFH.LocalHeaderOffset;
            try {
                stream.ReadExactly(lfhBuf, 0, LocalFileHeader.LENGTH);
            } catch (EndOfStreamException) {
                // The local file headers should come *before* the central dir entries, so this
                // would be very weird even if the file were empty.
                Archive.Notes.AddE("Ran off end of archive while reading local file header");
                return false;
            }
            LocalFileHeader lfh = new LocalFileHeader();
            lfh.Load(lfhBuf, 0);
            // NOTE: the LFH extra field can be different from the CDFH extra field.
            int lfhVarLen = lfh.FileNameLength + lfh.ExtraFieldLength;

            mCDFH.FileDataOffset = stream.Position + lfhVarLen;

            // Position us just past the end of the compressed data.
            stream.Position = mCDFH.FileDataOffset + mCDFH.CompSize;
            if ((lfh.Flags & DATA_DESC_FLAG) != 0) {
                // Look for the data descriptor.  The standard says they can appear without a
                // signature, but in practice this doesn't happen.  In a Zip64 archive the
                // length fields will be 8 bytes long, but we don't support that.
                //
                // We don't *need* the data descriptor, but if we copy the full record we need
                // to bring it along with us.
                byte[] ddBuf = new byte[DataDescriptor.LENGTH];
                try {
                    stream.ReadExactly(ddBuf, 0, DataDescriptor.LENGTH);
                } catch (EndOfStreamException) {
                    Archive.Notes.AddE("Ran off end of archive while reading data descriptor");
                    return false;
                }
                bool ddOkay = true;
                DataDescriptor dd = new DataDescriptor();
                try {
                    dd.Load(ddBuf, 0);
                    if (dd.Crc32 != mCDFH.Crc32 || dd.CompSize != mCDFH.CompSize ||
                            dd.UncompSize != mCDFH.UncompSize) {
                        Archive.Notes.AddW("Found mismatched data descriptor");
                        IsDubious = true;
                        ddOkay = false;
                    }
                } catch (InvalidDataException) {
                    Archive.Notes.AddW("Found bad data descriptor");
                    IsDubious = true;
                    ddOkay = false;
                }
                //mCDFH.DataDescriptorLength = DataDescriptor.LENGTH;
                if (!ddOkay) {
                    // Bad data descriptor, let's ignore it.  Back up and clear DD flag.
                    stream.Position = mCDFH.FileDataOffset + mCDFH.CompSize;
                    mCDFH.Flags = (ushort)(mCDFH.Flags & ~DATA_DESC_FLAG);
                }
            }
            mCDFH.FileRecordLength = stream.Position - mCDFH.LocalHeaderOffset;

            stream.Position = cdPosn;

            return true;
        }

        /// <summary>
        /// Creates an object for reading the contents of the entry out of the archive.
        /// </summary>
        /// <exception cref="NotImplementedException">Compression format not supported.</exception>
        internal ArcReadStream CreateReadStream() {
            Debug.Assert(Archive.DataStream != null);
            Archive.DataStream.Position = mCDFH.FileDataOffset;

            Checker checker = new CheckCRC32(0, mCDFH.Crc32);
            if (mCDFH.Format == CompressionFormat.Uncompressed) {
                return new ArcReadStream(Archive, mCDFH.UncompSize, checker);
            } else if (mCDFH.Format == CompressionFormat.Deflate) {
                Stream expander = new DeflateStream(Archive.DataStream, CompressionMode.Decompress,
                    true);
                return new ArcReadStream(Archive, mCDFH.UncompSize, checker, expander);
            } else {
                throw new NotImplementedException("Compression format not supported");
            }
        }

        // IFileEntry
        public bool GetPartInfo(FilePart part, out long length, out long storageSize,
                out CompressionFormat format) {
            if (part == FilePart.DataFork) {
                length = mCDFH.UncompSize;
                storageSize = mCDFH.CompSize;
                format = mCDFH.Format;
                return true;
            } else {
                length = storageSize = -1;
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
        /// Adds a part source for file data.
        /// </summary>
        internal void AddPart(IPartSource partSource, CompressionFormat compressFmt) {
            Debug.Assert(ChangeObject != null);
            switch (compressFmt) {
                case CompressionFormat.Default:
                case CompressionFormat.Uncompressed:
                case CompressionFormat.Deflate:
                    break;
                default:
                    throw new ArgumentException("Compression format " + compressFmt +
                        " not supported for this archive");
            }
            if (ChangeObject.HasDataPart) {
                throw new ArgumentException("Record already has a data fork");
            }

            ChangeObject.HasDataPart = true;
            ChangeObject.mPendingDataPart = partSource;
            ChangeObject.mPendingFormat = compressFmt;
            ChangeObject.IsDirty = true;
        }

        /// <summary>
        /// Deletes file data.
        /// </summary>
        internal void DeletePart() {
            Debug.Assert(ChangeObject != null);
            if (!ChangeObject.HasDataPart) {
                throw new FileNotFoundException("Record does not have a data fork");
            }
            ChangeObject.HasDataPart = false;
            ChangeObject.mPendingDataPart.Dispose();
            ChangeObject.mPendingDataPart = IPartSource.NO_SOURCE;
            ChangeObject.IsDirty = true;
        }

        /// <summary>
        /// Copies a full record without modifications.  Updates the CDFH record with the new
        /// file offsets.
        /// </summary>
        internal void CopyRecord(Stream outputStream) {
            Debug.Assert(OrigObject != null);

            // Update the file offsets to point to the new file position.
            mCDFH.LocalHeaderOffset = (uint)outputStream.Position;
            mCDFH.FileDataOffset = mCDFH.LocalHeaderOffset +
                (OrigObject.mCDFH.FileDataOffset - OrigObject.mCDFH.LocalHeaderOffset);
            mCDFH.FileRecordLength = OrigObject.mCDFH.FileRecordLength;     // no change to size

            Stream srcStream = Archive.DataStream!;
            srcStream.Position = OrigObject.mCDFH.LocalHeaderOffset;
            Debug.Assert(srcStream.Position +
                OrigObject.mCDFH.FileRecordLength <= srcStream.Length);
            Debug.Assert(OrigObject.mCDFH.FileRecordLength >= 0);
            FileUtil.CopyStream(srcStream, outputStream, OrigObject.mCDFH.FileRecordLength,
                Archive.TmpBuf!);
        }

        /// <summary>
        /// Writes the LFH and file data to the output stream.  Updates the CDFH record with
        /// the file offsets and lengths.
        /// </summary>
        internal void WriteRecord(Stream outputStream) {
            Debug.Assert(OrigObject != null);
            Debug.Assert(Archive.TmpBuf != null);

            // Figure out how we should encode the filename and comment.  Generate the encodings
            // to get their length.
            bool needUnicode;
            needUnicode = !CP437.IsStringValid(mFileName);
            needUnicode |= !CP437.IsStringValid(mComment);
            byte[] rawFileName;
            byte[] rawComment;
            if (needUnicode) {
                rawFileName = Encoding.UTF8.GetBytes(mFileName);
                rawComment = Encoding.UTF8.GetBytes(mComment);
            } else {
                rawFileName = CP437.FromUnicode(mFileName);
                rawComment = CP437.FromUnicode(mComment);
            }
            mCDFH.Flags = needUnicode ? LANGUAGE_ENC_FLAG : (ushort)0;

            if (rawFileName.Length > MAX_VAR_LEN || rawComment.Length > MAX_VAR_LEN) {
                // Could happen because of UTF-8 expansion.  We either need to cut something
                // off, which requires trimming in a way that doesn't split a UTF-8 value, or
                // just give up.  I don't really expect filenames and comments to be this long.
                throw new InvalidDataException("Filename or comment is too long");
            }
            mCDFH.FileNameLength = (ushort)rawFileName.Length;
            mCDFH.ExtraFieldLength = 0;         // drop all "extra" fields
            mCDFH.CommentLength = (ushort)rawComment.Length;
            //mCDFH.DataDescriptorLength = 0;     // drop data descriptor

            // Set position to start of data.
            mCDFH.LocalHeaderOffset = (uint)outputStream.Position;
            int lfhSize = LocalFileHeader.LENGTH + rawFileName.Length;
            outputStream.Position += lfhSize;
            mCDFH.FileDataOffset = outputStream.Position;

            if (mPendingDataPart == IPartSource.NO_SOURCE) {
                // Only the meta-data was updated.  Copy existing record data.
                Stream srcStream = Archive.DataStream!;
                srcStream.Position = OrigObject.mCDFH.FileDataOffset;
                FileUtil.CopyStream(srcStream, outputStream, (int)mCDFH.CompSize, Archive.TmpBuf);
            } else {
                // Copy or compress the part.
                Stream? compStream;
                if (mPendingFormat == CompressionFormat.Default ||
                        mPendingFormat == CompressionFormat.Deflate) {
                    compStream = new DeflateStream(outputStream, COMP_LEVEL, true);
                } else {
                    compStream = null;
                }
                Checker checker = new CheckCRC32(0, 0);
                if (ArcUtil.CopyPartSource(mPendingDataPart, outputStream, compStream, checker,
                        true, Archive.TmpBuf, out int inputLength, out int outputLength)) {
                    mCDFH.CompMethod = COMP_DEFLATE;
                    // TODO: if COMP_LEVEL != optimal, we're supposed to set flag bits 1+2
                } else {
                    mCDFH.CompMethod = COMP_UNCOMPRESSED;
                }
                mCDFH.UncompSize = (uint)inputLength;
                mCDFH.CompSize = (uint)outputLength;
                mCDFH.Crc32 = checker.Value;
            }
            Debug.Assert(outputStream.Position == mCDFH.FileDataOffset + mCDFH.CompSize);
            mCDFH.FileRecordLength = outputStream.Position - mCDFH.LocalHeaderOffset;

            // Set a few more things, in case we're overwriting an existing record.
            mCDFH.VersionNeeded = VERSION_NEEDED;
            mCDFH.VersionMadeBy = VERSION_MADE_BY;
            mCDFH.InternalAttribs = 0;
            mCDFH.ExternalAttribs = EXTERNAL_ATTRIBS;
            Debug.Assert(mCDFH.DiskNumberStart == 0);

            // Generate the LFH from the CDFH.
            LocalFileHeader lfh = new LocalFileHeader(mCDFH);
            lfh.Store(Archive.TmpBuf, 0);

            long recEndOffset = outputStream.Position;
            outputStream.Position = mCDFH.LocalHeaderOffset;
            outputStream.Write(Archive.TmpBuf, 0, LocalFileHeader.LENGTH);
            outputStream.Write(rawFileName, 0, rawFileName.Length);
            // no extra fields, no data descriptor
            Debug.Assert(outputStream.Position == mCDFH.FileDataOffset);
            outputStream.Position = recEndOffset;
        }

        internal void WriteCentralDirEntry(Stream outputStream) {
            Debug.Assert(Archive.TmpBuf != null);

            // We didn't hang on to the raw filename or comment we generated when writing the LFH,
            // so we need to regenerate them here.
            byte[] rawFileName;
            byte[] rawComment;
            if (mCDFH.HasUnicodeText) {
                rawFileName = Encoding.UTF8.GetBytes(mFileName);
                rawComment = Encoding.UTF8.GetBytes(mComment);
            } else {
                rawFileName = CP437.FromUnicode(mFileName);
                rawComment = CP437.FromUnicode(mComment);
            }
            if (rawFileName.Length != mCDFH.FileNameLength ||
                    rawComment.Length != mCDFH.CommentLength) {
                // Should be impossible.
                throw new DAException("String conversion mismatch");
            }

            // We currently discard the "extra" data from the central header.  This field could
            // be nonzero if we're doing a record copy.
            mCDFH.ExtraFieldLength = 0;

            mCDFH.Store(Archive.TmpBuf, 0);
            outputStream.Write(Archive.TmpBuf, 0, CentralDirFileHeader.LENGTH);
            outputStream.Write(rawFileName, 0, rawFileName.Length);
            // no "extra" data
            outputStream.Write(rawComment, 0, rawComment.Length);
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
            if (ChangeObject != this) {
                // Not a newly-created record, copy fields back.
                CopyFields(this, ChangeObject);
            } else {
                mPendingFormat = CompressionFormat.Default;
                IsDirty = false;
                OrigObject = null;
            }
            ChangeObject = null;
            Debug.Assert(!IsDirty);
            Debug.Assert(mPendingDataPart == IPartSource.NO_SOURCE);
            Debug.Assert(mPendingFormat == CompressionFormat.Default);
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
        /// Sets the filename, checking syntax.
        /// </summary>
        /// <param name="fileName">New filename, which may be a partial path.</param>
        /// <exception cref="ArgumentException">Invalid filename.</exception>
        private void SetFileName(string fileName) {
            if (string.IsNullOrEmpty(fileName)) {
                throw new ArgumentException("Filename must be at least one character");
            }
            if (fileName.Length > BIG_PATHNAME_LEN) {
                throw new ArgumentException("Filename is excessively long (" +
                    fileName.Length + ")");
            }
            if (fileName[0] == SEP_CHAR) {
                throw new ArgumentException("Filename may not start with '/'");
            }
            // TODO: should also prevent logical MS-DOS names like "C:\".
            // TODO: shouldn't allow a trailing slash if the entry has data?

            mFileName = fileName;
        }

        #endregion Filenames

        public override string ToString() {
            StringBuilder sb = new StringBuilder();
            sb.Append("[Zip-entry '");
            sb.Append(FileName);
            sb.Append("']");
            return sb.ToString();
        }
    }
}
