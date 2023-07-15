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
    /// The one and only record in a GZip archive.
    /// </summary>
    /// <remarks>
    /// <para>The built-in <see cref="System.IO.Compression.GZipStream"/> class does most of what
    /// we want, but doesn't provide a way to access any of the header fields.  Fortunately,
    /// wrapping the output of DeflateStream with a header and footer is pretty easy.</para>
    /// <para>The GZipStream class does correctly handle multi-member archives, so we can use
    /// it as the ArcReadStream decompressor.</para>
    /// </remarks>
    public class GZip_FileEntry : IFileEntry {
        public const byte ID1 = 0x1f;
        public const byte ID2 = 0x8b;
        public const byte CM_DEFLATE = 8;
        public const byte OS_UNIX = 3;
        internal const int MIN_HEADER_LEN = 10;
        internal const int FOOTER_LEN = 8;
        internal const CompressionLevel COMP_LEVEL = CompressionLevel.Optimal;

        [Flags]
        internal enum HeaderFlags : byte {
            FTEXT = 1 << 0,
            FHCRC = 1 << 1,
            FEXTRA = 1 << 2,
            FNAME = 1 << 3,
            FCOMMENT = 1 << 4
        }
        internal const byte FLAG_MASK = 0x1f;

        //
        // IFileEntry interfaces.
        //

        public bool IsValid { get { return Archive != null; } }
        public bool IsDubious { get; private set; }
        public bool IsDamaged { get; private set; }

        public bool HasDataFork { get { return mHasDataPart; } }

        public bool IsDirectory => false;
        public bool HasRsrcFork => false;
        public bool IsDiskImage => false;

        public IFileEntry ContainingDir => IFileEntry.NO_ENTRY;
        public int Count => 0;

        public string FileName {
            get {
                if (!IsValid) {
                    throw new InvalidOperationException("Invalid object");
                }
                return mFileName;
            }
            set {
                // We're not too picky here, because we don't really use this filename.
                CheckChangeAllowed();
                Debug.Assert(ChangeObject != null);
                if (!IsFileNameValid(value)) {
                    throw new ArgumentException("Invalid filename: '" + value + "'");
                }
                ChangeObject.mFileName = value;
                ChangeObject.mRawFileName = Encoding.UTF8.GetBytes(value);
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
                CheckChangeAllowed();
                Debug.Assert(ChangeObject != null);
                foreach (byte bb in value) {
                    if (bb == 0x00) {
                        throw new ArgumentException("Filenames cannot contain NULs");
                    }
                }
                ChangeObject.mRawFileName = value;
                ChangeObject.mFileName = Encoding.UTF8.GetString(value);
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

        public byte Access {
            get { return FileAttribs.FILE_ACCESS_UNLOCKED; }
            set { }
        }
        public DateTime CreateWhen {
            get { return TimeStamp.NO_DATE; }
            set { }
        }
        public DateTime ModWhen {
            get { return TimeStamp.ConvertDateTime_Unix32(mLastModWhen); }
            set {
                CheckChangeAllowed();
                ChangeObject!.mLastModWhen = TimeStamp.ConvertDateTime_Unix32(value);
            }
        }

        public long StorageSize { get { return mCompLength; } }

        // The uncompressed length is inaccurate for multi-member files and very large files.
        // We don't want to return an incorrect value, so unless we want to scan the entire
        // file we need to return -1 here.  (Use GetPartInfo to get the length of the last part.)
        public long DataLength { get { return -1; } }

        public long RsrcLength => 0;

        public string Comment { get => string.Empty; set { } }

        private static readonly List<IFileEntry> sEmptyList = new List<IFileEntry>();
        public IEnumerator<IFileEntry> GetEnumerator() {
            return sEmptyList.GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() {
            return sEmptyList.GetEnumerator();
        }

        //
        // GZip-specific fields.
        //

        /// <summary>
        /// Optional filename stored in the header (cooked form).
        /// </summary>
        private string mFileName = string.Empty;

        /// <summary>
        /// Optional filename stored in the header (raw form).
        /// </summary>
        private byte[] mRawFileName = RawData.EMPTY_BYTE_ARRAY;

        /// <summary>
        /// Modification timestamp, from the header.
        /// </summary>
        private int mLastModWhen = TimeStamp.INVALID_UNIX_TIME;

        /// <summary>
        /// Length of the compressed data, including headers and footers.  Will be inaccurate if
        /// there are multiple members.
        /// </summary>
        private long mCompLength = -1;

        /// <summary>
        /// Uncompressed length of the last member in the gzip file.  This will not be an
        /// accurate value for the uncompressed length of the data if there are multiple
        /// members or the length exceeds 2^32.
        /// </summary>
        private uint mUncompLength = 0;

        /// <summary>
        /// Length of header of first member.
        /// </summary>
        private long mFirstHeaderLength;

        /// <summary>
        /// Reference to archive object.
        /// </summary>
        internal GZip Archive { get; private set; }

        /// <summary>
        /// Object that receives changes when transaction is open.
        /// </summary>
        internal GZip_FileEntry? ChangeObject { get; set; }

        /// <summary>
        /// Link back to original object, from change object.
        /// </summary>
        internal GZip_FileEntry? OrigObject { get; set; }

        /// <summary>
        /// True if the change object has file data.  This will be false for newly-created
        /// entries, and entries where DeletePart was used.  Only meaningful in change objects.
        /// </summary>
        private bool mHasDataPart;

        /// <summary>
        /// Pending data.  Only set in change objects.  Will be NO_SOURCE if we're not replacing
        /// the current record contents.
        /// </summary>
        private IPartSource mPendingDataPart = IPartSource.NO_SOURCE;


        /// <summary>
        /// Private constructor.
        /// </summary>
        /// <param name="archive"></param>
        internal GZip_FileEntry(GZip archive) {
            Archive = archive;
        }

        /// <summary>
        /// Clones a file entry for use as a change object.
        /// </summary>
        internal static GZip_FileEntry CreateChangeObject(GZip_FileEntry src) {
            Debug.Assert(src.ChangeObject == null && src.OrigObject == null);
            if (src.IsDamaged) {
                throw new InvalidOperationException("Cannot clone a damaged entry");
            }

            GZip_FileEntry newEntry = new GZip_FileEntry(src.Archive);
            CopyFields(newEntry, src);

            // Invalidate these to ensure we're rewriting them.
            newEntry.mCompLength = -1;
            newEntry.mUncompLength = uint.MaxValue;
            newEntry.mFirstHeaderLength = -1;
            return newEntry;
        }

        /// <summary>
        /// Copies all fields from one object to another.
        /// </summary>
        internal static void CopyFields(GZip_FileEntry dst, GZip_FileEntry src) {
            dst.mFileName = src.mFileName;
            dst.mRawFileName = src.mRawFileName;
            dst.mLastModWhen = src.mLastModWhen;

            dst.mCompLength = src.mCompLength;
            dst.mUncompLength = src.mUncompLength;
            dst.mFirstHeaderLength = src.mFirstHeaderLength;

            dst.mHasDataPart = src.mHasDataPart;
        }

        /// <summary>
        /// Scans the contents of the archive.
        /// </summary>
        /// <returns>True if parsing was successful, false if errors were found.</returns>
        internal bool Scan() {
            Debug.Assert(Archive.DataStream != null);
            Stream stream = Archive.DataStream;
            Debug.Assert(stream.Position == 0);
            if (!ReadHeader(stream, out byte[] rawFileName, out int lastModWhen)) {
                return false;
            }
            if (stream.Position > stream.Length - FOOTER_LEN) {
                Debug.WriteLine("Not enough room left for footer");
                return false;
            }

            mFirstHeaderLength = stream.Position;
            mRawFileName = rawFileName;
            mFileName = Encoding.UTF8.GetString(mRawFileName);      // assume UTF-8
            mLastModWhen = lastModWhen;

            // The "gzip" utility reports the full length of the file, with headers and footers,
            // as the compressed length.  This seems like the honest value for a single-file
            // compressor.  (It then lies and uses the no-header/no-footer value when computing
            // the compression ratio.)
            //mCompLength = stream.Length - stream.Position - FOOTER_LEN;
            mCompLength = stream.Length;

            stream.Position = stream.Length - FOOTER_LEN;
            bool ok;
            uint crc32 = RawData.ReadU32LE(stream, out ok);
            uint isize = RawData.ReadU32LE(stream, out ok);
            if (!ok) {
                Debug.WriteLine("Failed reading footer");       // not expected
                return false;
            }
            mUncompLength = isize;

            mHasDataPart = true;
            return true;
        }

        /// <summary>
        /// Reads and parses the gzip member header from the current stream position.  On return,
        /// the stream will be positioned at the start of the compressed data.
        /// </summary>
        /// <param name="stream">Stream to read from.</param>
        /// <param name="rawFileName">Result: raw filename, or empty array if none present.</param>
        /// <param name="lastModWhen">Result: last modification date.</param>
        /// <returns>True on success, false if end-of-file was reached.</returns>
        internal static bool ReadHeader(Stream stream, out byte[] rawFileName,
                out int lastModWhen) {
            rawFileName = RawData.EMPTY_BYTE_ARRAY;
            lastModWhen = TimeStamp.INVALID_UNIX_TIME;

            if (stream.Length - stream.Position < MIN_HEADER_LEN + FOOTER_LEN) {
                // Not enough data left to be a valid member.
                return false;
            }
            byte[] hdrBuf = new byte[MIN_HEADER_LEN];
            stream.ReadExactly(hdrBuf, 0, hdrBuf.Length);
            if (hdrBuf[0] != ID1 || hdrBuf[1] != ID2 || hdrBuf[2] != CM_DEFLATE) {
                // TODO: if some other compression format were popular in gzip files, we
                //   would want to put up a better error message
                return false;
            }

            byte flags = hdrBuf[3];
            int timeStamp = (int)RawData.GetU32LE(hdrBuf, 4);
            if (timeStamp != 0) {
                lastModWhen = timeStamp;
            }
            // ignore XFL (+8) and OS (+9)

            // If "extra field" is present, skip past it.
            if ((flags & (byte)HeaderFlags.FEXTRA) != 0) {
                int len1 = stream.ReadByte();
                int len2 = stream.ReadByte();
                if (len2 < 0) {
                    return false;   // early EOF
                }
                ushort xlen = (ushort)(len1 | (len2 << 8));
                stream.Position += xlen;
                if (stream.Position > stream.Length) {
                    return false;   // early EOF
                }
            }

            // If a null-terminated filename is present, read it.
            if ((flags & (byte)HeaderFlags.FNAME) != 0) {
                if (!GetNullTermString(stream, out rawFileName)) {
                    Debug.WriteLine("Hit EOF while reading filename");
                    return false;
                }
            }

            // If a null-terminated comment is present, read it... and ignore it.
            if ((flags & (byte)HeaderFlags.FCOMMENT) != 0) {
                if (!GetNullTermString(stream, out byte[] comment)) {
                    Debug.WriteLine("Hit EOF while reading comment");
                    return false;
                }
                Debug.WriteLine("Found comment: '" + comment + "'");
            }

            // If a 16-bit header CRC is present, read it... and ignore it.
            if ((flags & (byte)HeaderFlags.FHCRC) != 0) {
                int crc1 = stream.ReadByte();
                int crc2 = stream.ReadByte();
                if (crc2 < 0) {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Reads a null-terminated string from the stream.  On return, the stream will be
        /// positioned on the byte after the terminating null byte.
        /// </summary>
        /// <param name="stream">Stream to read from.</param>
        /// <param name="rawString">Result: raw string, without the terminating null byte.</param>
        /// <returns>True on success, false if EOF was reached before the end of the string
        ///   was found.</returns>
        private static bool GetNullTermString(Stream stream, out byte[] rawString) {
            rawString = RawData.EMPTY_BYTE_ARRAY;

            // Scan forward to determine the length.
            long startPos = stream.Position;
            while (true) {
                int ch = stream.ReadByte();
                if (ch < 0) {
                    return false;
                } else if (ch == 0x00) {
                    break;
                }
            }
            long endPos = stream.Position;

            // Allocate a buffer, seek back, and read the string.
            int nameLen = (int)(endPos - startPos);
            rawString = new byte[nameLen - 1];          // storage, minus trailing null byte
            stream.Position = startPos;
            stream.ReadExactly(rawString, 0, nameLen - 1);
            stream.Position = endPos;
            return true;
        }

        // IFileEntry
        public bool GetPartInfo(FilePart part, out long length, out long storageSize,
                out CompressionFormat format) {
            if (part == FilePart.DataFork) {
                // Return the maybe-not-accurate uncompressed length we get from the footer,
                // so that the application has a way to use it if it wants.
                length = mUncompLength;
                // Return the full size of the compressed file, including headers and footers.
                // This won't match what "gzip -l" reports, since it's only looking at the
                // compressed data.
                storageSize = mCompLength;
                format = CompressionFormat.Deflate;
                return mHasDataPart;
            } else {
                length = -1;
                storageSize = 0;
                format = CompressionFormat.Unknown;
                return false;
            }
        }

        /// <summary>
        /// Creates an object for reading the contents of the entry out of the archive.
        /// </summary>
        internal ArcReadStream CreateReadStream() {
            if (!mHasDataPart) {
                // Can only happen on a newly-created record, which can only happen in a
                // newly-created archive.
                throw new FileNotFoundException("No data");
            }
            Debug.Assert(Archive.DataStream != null);

            // Feed the whole archive in.
            Archive.DataStream.Position = 0;
            GZipStream gzStream = new GZipStream(Archive.DataStream, CompressionMode.Decompress);

            // CRC-32 checks are performed by GZipStream on each member.
            return new ArcReadStream(Archive, -1, null, gzStream);
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
        internal void AddPart(IPartSource partSource) {
            Debug.Assert(ChangeObject != null);
            if (ChangeObject.mHasDataPart) {
                throw new ArgumentException("Record already has a data fork");
            }
            ChangeObject.mPendingDataPart = partSource;
            ChangeObject.mHasDataPart = true;
        }

        /// <summary>
        /// Deletes file data.
        /// </summary>
        internal void DeletePart() {
            Debug.Assert(ChangeObject != null);
            if (!ChangeObject.mHasDataPart) {
                throw new ArgumentException("Record does not have a data fork");
            }
            ChangeObject.mPendingDataPart.Dispose();
            ChangeObject.mPendingDataPart = IPartSource.NO_SOURCE;
            ChangeObject.mHasDataPart = false;
        }

        /// <summary>
        /// Writes the record to the output stream.  Invoke this on the change object.
        /// </summary>
        internal void WriteRecord(Stream outputStream) {
            Debug.Assert(OrigObject != null);
            Debug.Assert(Archive.TmpBuf != null);

            if (!mHasDataPart) {
                throw new InvalidOperationException("Record has no data");
            }

            if (mPendingDataPart == IPartSource.NO_SOURCE) {
                // Only changes were to metadata, so we can just copy everything past the header.
                Debug.Assert(Archive.DataStream != null);
                WriteHeader(outputStream);
                mFirstHeaderLength = outputStream.Position;

                Archive.DataStream.Position = OrigObject.mFirstHeaderLength;
                long copyLen = Archive.DataStream.Length - Archive.DataStream.Position;
                FileUtil.CopyStream(Archive.DataStream, outputStream, copyLen, Archive.TmpBuf!);

                mUncompLength = OrigObject.mUncompLength;
                mCompLength = OrigObject.mCompLength;
            } else {
                // New data.  Write header.
                WriteHeader(outputStream);
                mFirstHeaderLength = outputStream.Position;

                // New data, need to deflate.
                Stream deflateStream = new DeflateStream(outputStream, COMP_LEVEL, true);
                Checker checker = new CheckCRC32(0, 0);
                ArcUtil.CopyPartSource(mPendingDataPart, outputStream, deflateStream, checker,
                    false, Archive.TmpBuf, out int inputLength, out int outputLength);
                mUncompLength = (uint)inputLength;
                mCompLength = outputLength;

                // Write footer.
                RawData.WriteU32LE(outputStream, checker.Value);
                RawData.WriteU32LE(outputStream, (uint)inputLength);
            }
        }

        internal void WriteHeader(Stream outputStream) {
            byte flags = 0;
            if (mRawFileName.Length > 0) {
                flags |= (byte)HeaderFlags.FNAME;
            }
            outputStream.WriteByte(ID1);
            outputStream.WriteByte(ID2);
            outputStream.WriteByte(CM_DEFLATE);
            outputStream.WriteByte(flags);
            RawData.WriteU32LE(outputStream, (uint)mLastModWhen);
            outputStream.WriteByte(0);          // XFL
            outputStream.WriteByte(OS_UNIX);    // match Linux gzip

            if (mRawFileName.Length > 0) {
                outputStream.Write(mRawFileName, 0, mRawFileName.Length);
                outputStream.WriteByte(0x00);
            }
        }

        internal void CommitChange() {
            Debug.Assert(ChangeObject != this);
            CopyFields(this, ChangeObject!);    // copy fields back into primary object
            Debug.Assert(mPendingDataPart == IPartSource.NO_SOURCE);
            ChangeObject = null;
            OrigObject = null;
        }

        internal void DisposeSources() {
            Debug.Assert(ChangeObject != null);
            if (ChangeObject.mPendingDataPart != IPartSource.NO_SOURCE) {
                ChangeObject.mPendingDataPart.Dispose();
                ChangeObject.mPendingDataPart = IPartSource.NO_SOURCE;
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

        // IFileEntry
        public int CompareFileName(string fileName) {
            // This should probably change behavior based on the OS type.  It's easier to just
            // do a case-insensitive match, and since there's exactly one file present it
            // doesn't really matter what we do.
            return string.Compare(mFileName, fileName, StringComparison.OrdinalIgnoreCase);
        }

        // IFileEntry
        public int CompareFileName(string fileName, char fileNameSeparator) {
            return CompareFileName(fileName);
        }

        /// <summary>
        /// Determines whether the string is a valid gzip filename.
        /// </summary>
        /// <param name="fileName">Filename to check.</param>
        /// <returns>True if all is well.</returns>
        public static bool IsFileNameValid(string fileName) {
            // The filename is stored as a null-terminated string, so there's no restriction
            // on the filename length.  It just can't contain a null byte.
            if (fileName.Contains('\0')) {
                return false;
            }
            return true;
        }
    }
}
