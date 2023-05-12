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
using DiskArc.Comp;
using DiskArc.FS;
using static DiskArc.Defs;

namespace DiskArc.Arc {
    /// <summary>
    /// One record in a NuFX archive.
    /// </summary>
    public class NuFX_FileEntry : IFileEntry {
        internal const char DEFAULT_FSSEP = ':';        // default value for new records
        internal const int BIG_THREAD_COUNT = 16;       // maximum reasonable thread count
        private const int REC_FIXED_LEN = 56;           // length of fixed-size portion of v0 hdr
        private const int GSHK_MIN_NAME_LEN = 32;       // min length of pre-sized filename thread
        private const int MAX_REC_VERSION = 3;          // don't understand anything beyond this
        private const int BIG_ATTRIB_COUNT = 256;       // maximum reasonable size of attributes
        private const int BIG_PATHNAME_LEN = 1024;      // maximum reasonable pathname length
        private const int BIG_COMMENT_LEN = 65535;      // maximum reasonable comment length
        private const int MIN_USEFUL_OPTION_LIST = 16;  // min needed to hold HFS type/creator
        private const int NEW_RECORD_VERSION = 3;       // use this for all new records
        private const ushort DEFAULT_FILESYSTEM = (ushort)FileSystemType.ProDOS;

        // Record header signature: "NuFX" in alternating low/high ASCII.
        private static readonly byte[] RECORD_SIG = new byte[] { 0x4e, 0xf5, 0x46, 0xd8 };

        //
        // IFileEntry interfaces.
        //

        public bool IsDubious { get; private set; }

        public bool IsDamaged { get; private set; }

        // NuFX does support directory "control threads", but nothing used them.
        public bool IsDirectory => false;

        public bool HasDataFork {
            get {
                if (IsDiskImage) {
                    return false;
                }
                return GetThreadEof(ThreadHeader.ClassKind.DataFork) >= 0;
            }
        }
        public bool HasRsrcFork {
            get {
                if (IsDiskImage) {
                    return false;
                }
                return GetThreadEof(ThreadHeader.ClassKind.RsrcFork) >= 0;
            }
        }
        public bool IsDiskImage {
            get { return GetThreadEof(ThreadHeader.ClassKind.DiskImage) != -1; }
        }

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
                // The application is expected to prevent duplicate names.  We don't make any
                // attempt to detect or prevent them.
                CheckChangeAllowed();
                ChangeObject!.SetFileName(value);
            }
        }
        private string mFileName = string.Empty;

        public char DirectorySeparatorChar {
            get { return (char)mFssep; }
            set {
                CheckChangeAllowed();
                if (value != (byte)value) {
                    throw new ArgumentException("Invalid filename separator char '" + value + "'");
                }
                ChangeObject!.mFssep = (byte)value;
            }
        }

        public byte[] RawFileName {
            get {
                byte[] result = new byte[mRawFileName.Length];
                Array.Copy(mRawFileName, result, result.Length);
                return result;
            }
            set {
                CheckChangeAllowed();
                ChangeObject!.SetFileName(value);
            }
        }

        public string FullPathName { get { return FileName; } }

        public bool HasProDOSTypes => true;

        public byte FileType {
            get { return (byte)mFileType; }
            set {
                CheckChangeAllowed();
                ChangeObject!.mFileType = value;
            }
        }
        public ushort AuxType {
            get {
                if (IsDiskImage) {
                    // For disk images, the full 32 bits are used to hold the block count, so the
                    // value we report here would be incomplete.  Return 0 rather than a partial
                    // result.
                    return 0;
                } else {
                    return (ushort)mExtraType;
                }
            }
            set {
                CheckChangeAllowed();
                if (IsDiskImage) {
                    // This field defines the uncompressed length of the disk image thread.
                    // Changing this would be a very bad idea.
                    Debug.WriteLine("Attempted to change extra_type of disk image");
                    return;
                }
                ChangeObject!.mExtraType = value;
            }
        }

        public bool HasHFSTypes {
            get {
                if (mFileSysID != (ushort)FileSystemType.ProDOS &&
                        mFileSysID != (ushort)FileSystemType.HFS) {
                    return false;
                }
                if (mOptionList.Length > 0 && mOptionList.Length < MIN_USEFUL_OPTION_LIST) {
                    // Too short to be valid, but it exists, so we can't stomp on it.
                    return false;
                }
                return true;
            }
        }

        public uint HFSFileType {
            get {
                if (!HasHFSTypes || mOptionList.Length < MIN_USEFUL_OPTION_LIST) {
                    return 0;
                }
                return RawData.GetU32BE(mOptionList, 0);
            }
            set {
                CheckChangeAllowed();
                Debug.Assert(ChangeObject != null);
                if (!ChangeObject.HasHFSTypes) {
                    throw new ArgumentException("HFS types not supported for fileSysID " +
                        ChangeObject.mFileSysID);
                }
                if (ChangeObject.mOptionList.Length > 0 &&
                        ChangeObject.mOptionList.Length < MIN_USEFUL_OPTION_LIST) {
                    throw new ArgumentException("Incompatible option list");
                }
                if (ChangeObject.mOptionList.Length == 0) {
                    ChangeObject.CreateOptionList();
                }
                RawData.SetU32BE(ChangeObject.mOptionList, 0, value);
            }
        }
        public uint HFSCreator {
            get {
                if (!HasHFSTypes || mOptionList.Length < MIN_USEFUL_OPTION_LIST) {
                    return 0;
                }
                return RawData.GetU32BE(mOptionList, 4);
            }
            set {
                CheckChangeAllowed();
                Debug.Assert(ChangeObject != null);
                if (!ChangeObject.HasHFSTypes) {
                    throw new ArgumentException("HFS types not supported for fileSysID " +
                        ChangeObject.mFileSysID);
                }
                if (ChangeObject.mOptionList.Length > 0 &&
                        ChangeObject.mOptionList.Length < MIN_USEFUL_OPTION_LIST) {
                    throw new ArgumentException("Incompatible option list");
                }
                if (ChangeObject.mOptionList.Length == 0) {
                    ChangeObject.CreateOptionList();
                }
                RawData.SetU32BE(ChangeObject.mOptionList, 4, value);
            }
        }
        private void CreateOptionList() {
            // Based on results from experiments with the GS/OS FSTs.
            if (mFileSysID == (ushort)FileSystemType.ProDOS) {
                mOptionList = new byte[32];     // all values zero
            } else {
                mOptionList = new byte[36];     // all values zero...
                mOptionList[32] = 0x02;         // ...except this one
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
            get { return TimeStamp.ConvertDateTime_GS(mCreateWhen); }
            set {
                CheckChangeAllowed();
                ChangeObject!.mCreateWhen = TimeStamp.ConvertDateTime_GS(value);
            }
        }
        public DateTime ModWhen {
            get { return TimeStamp.ConvertDateTime_GS(mModWhen); }
            set {
                CheckChangeAllowed();
                ChangeObject!.mModWhen = TimeStamp.ConvertDateTime_GS(value);
            }
        }

        public long StorageSize { get { return CalcStorageSize(); } }

        public long DataLength {
            get {
                // If this is a disk image record, return the disk image thread length.
                if (IsDiskImage) {
                    // The thread EOF field tends to hold zero or something random.  Sometimes
                    // the storage_type field isn't set correctly.  Ignore everything but the
                    // extra_type.
                    return mExtraType * BLOCK_SIZE;
                }
                long eof = GetThreadEof(ThreadHeader.ClassKind.DataFork);
                if (eof < 0) {
                    eof = 0;    // return zero if thread doesn't exist
                }
                return eof;
            }
        }

        public long RsrcLength {
            get {
                // If this is a disk image record, return 0.
                if (IsDiskImage) {
                    return 0;
                }
                long eof = GetThreadEof(ThreadHeader.ClassKind.RsrcFork);
                if (eof < 0 /*&& mStorageType == (int)FS.ProDOS.StorageType.Extended*/) {
                    eof = 0;    // return 0 if thread doesn't exist
                }
                return eof;
            }
        }

        public string Comment {
            get { return mComment; }
            set {
                // Limit character set to Mac OS Roman.  Technically we should limit it to ASCII.
                // We will do an LF/CRLF -> CR conversion just before writing it out.
                CheckChangeAllowed();
                Debug.Assert(ChangeObject != null);
                if (value == string.Empty) {
                    // Delete the comment entirely.  The application can override this by
                    // resetting the comment field length.  This feels more natural than
                    // requiring that applications explicitly clear the length for NuFX archives.
                    ChangeObject.mComment = value;
                    ChangeObject.mCommentFieldLength = 0;
                    return;
                }
                if (!MacChar.IsStringValid(value, MacChar.Encoding.Roman)) {
                    throw new ArgumentException("Comment contains invalid characters");
                }
                ChangeObject.mComment = value;
                // Don't set the field length; let the commit process do that.
            }
        }
        private string mComment = string.Empty;

        public bool GetPartInfo(FilePart part, out long length, out long storageSize,
                out CompressionFormat format) {
            // Find first matching entry in thread list.
            ThreadHeader? thread = FindThread(part);
            if (thread != null) {
                if (part == FilePart.DiskImage) {
                    // Thread EOF is expected to be zero for disk images.  Return a useful value.
                    length = mExtraType * BLOCK_SIZE;
                } else {
                    length = thread.mThreadEof;
                }
                storageSize = thread.mCompThreadEof;
                format = (CompressionFormat)thread.mThreadFormat;
                return true;
            } else {
                length = storageSize = -1;
                format = CompressionFormat.Unknown;
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
        // NuFX-specific fields.
        //

        /// <summary>
        /// Filesystem ID.
        /// </summary>
        /// <para>This must be set to "ProDOS" or "HFS" for HFS file types to be preserved in an
        /// option list.  Changing the filesystem ID invalidates the option list, so this must
        /// be set before the HFS types are set.</para>
        /// </remarks>
        public FileSystemType FileSysID {
            get { return (FileSystemType)mFileSysID; }
            set {
                // No point in range-checking the value.  It's ProDOS, HFS, or "other".
                CheckChangeAllowed();
                Debug.Assert(ChangeObject != null);
                if (ChangeObject.mFileSysID != (ushort)value) {
                    ChangeObject.mFileSysID = (ushort)value;
                    ChangeObject.mOptionList = RawData.EMPTY_BYTE_ARRAY;
                }
            }
        }

        /// <summary>
        /// Full value of extra_type field.
        /// </summary>
        public uint ExtraType {
            get { return mExtraType;  }
        }

        /// <summary>
        /// <para>Length of comment buffer.  NuFX allows the comment buffer to be over-sized to
        /// make in-place editing easier.  Set this value after setting the comment.</para>
        /// <para>If this value is too small to hold the comment, the value will be increased to
        /// match the comment length when the record is written.</para>
        /// </summary>
        public uint CommentFieldLength {
            get { return mCommentFieldLength; }
            set {
                CheckChangeAllowed();
                Debug.Assert(ChangeObject != null);
                ChangeObject.mCommentFieldLength = value;
            }
        }
        private uint mCommentFieldLength;


        /// <summary>
        /// Thread header.
        /// </summary>
        private class ThreadHeader {
            public const int LENGTH = 16;
            public const int MESSAGE_CLASS = 0;
            public const int DATA_CLASS = 2;
            public const int FILENAME_CLASS = 3;
            public const int DATA_FORK = 0;
            public const int DISK_IMAGE = 1;
            public const int RSRC_FORK = 2;
            public const int COMMENT = 1;

            /// <summary>Classification of the thread: 0=message, 1=control, 2=data,
            /// 3=filename.</summary>
            public ushort mThreadClass;

            /// <summary>Compression format: 0=none, 1=squeeze, 2=LZW/1, 3=LZW/2, 4=LZC-12,
            /// 5=LZC-16.</summary>
            public ushort mThreadFormat;

            /// <summary>Thread kind; only meaningful when combined with class.</summary>
            public ushort mThreadKind;

            /// <summary>CRC, meaning depends on record version: 0/1=unused, 2=compressed data,
            /// 3=uncompressed data.</summary>
            public ushort mThreadCrc;

            /// <summary>Length of data when thread is uncompressed.  For comments and
            /// filenames, the length of the comment or filename.</summary>
            public uint mThreadEof;

            /// <summary>Length of data in the archive file.</summary>
            public uint mCompThreadEof;


            /// <summary>
            /// Absolute offset within archive stream of start of thread data.
            /// </summary>
            public long mFileOffset = -1;

            /// <summary>
            /// ThreadHeader in a change object: offset of data in the original archive.
            /// </summary>
            public long mOrigFileOffset = -1;

            /// <summary>
            /// Thread class + kind combinations that we actually care about.  This does not
            /// include the deprecated comment format, icons, or directory control threads.
            /// </summary>
            public enum ClassKind {
                Unknown = 0,
                Comment,
                DataFork,
                DiskImage,
                RsrcFork,
                FileName
            }
            public ClassKind ThreadKind {
                get {
                    switch (mThreadClass) {
                        case 0x0000:    // message_thread class
                            if (mThreadKind == COMMENT) {
                                return ClassKind.Comment;
                            } else {
                                return ClassKind.Unknown;
                            }
                        case 0x0001:    // control_thread class
                            return ClassKind.Unknown;
                        case 0x0002:    // data_thread class
                            if (mThreadKind == DATA_FORK) {
                                return ClassKind.DataFork;
                            } else if (mThreadKind == DISK_IMAGE) {
                                return ClassKind.DiskImage;
                            } else if (mThreadKind == RSRC_FORK) {
                                return ClassKind.RsrcFork;
                            } else {
                                return ClassKind.Unknown;
                            }
                        case 0x0003:    // filename_thread class
                            if (mThreadKind == 0) {
                                return ClassKind.FileName;
                            } else {
                                return ClassKind.Unknown;
                            }
                        default:
                            return ClassKind.Unknown;
                    }
                }
                set {
                    switch (value) {
                        case ClassKind.Comment:
                            mThreadClass = MESSAGE_CLASS;
                            mThreadKind = 1;
                            break;
                        case ClassKind.DataFork:
                            mThreadClass = DATA_CLASS;
                            mThreadKind = DATA_FORK;
                            break;
                        case ClassKind.DiskImage:
                            mThreadClass = DATA_CLASS;
                            mThreadKind = DISK_IMAGE;
                            break;
                        case ClassKind.RsrcFork:
                            mThreadClass = DATA_CLASS;
                            mThreadKind = RSRC_FORK;
                            break;
                        case ClassKind.FileName:
                            mThreadClass = FILENAME_CLASS;
                            mThreadKind = 0;
                            break;
                        default:
                            Debug.Assert(false);
                            break;
                    }
                }
            }

            public void Load(byte[] buf, int offset) {
                mThreadClass = RawData.GetU16LE(buf, offset + 0x00);
                mThreadFormat = RawData.GetU16LE(buf, offset + 0x02);
                mThreadKind = RawData.GetU16LE(buf, offset + 0x04);
                mThreadCrc = RawData.GetU16LE(buf, offset + 0x06);
                mThreadEof = RawData.GetU32LE(buf, offset + 0x08);
                mCompThreadEof = RawData.GetU32LE(buf, offset + 0x0c);
            }
            public void Store(byte[] buf, int offset) {
                RawData.SetU16LE(buf, offset + 0x00, mThreadClass);
                RawData.SetU16LE(buf, offset + 0x02, mThreadFormat);
                RawData.SetU16LE(buf, offset + 0x04, mThreadKind);
                RawData.SetU16LE(buf, offset + 0x06, mThreadCrc);
                RawData.SetU32LE(buf, offset + 0x08, mThreadEof);
                RawData.SetU32LE(buf, offset + 0x0c, mCompThreadEof);
            }

            /// <summary>
            /// Clones the object for use in a change object.
            /// </summary>
            public ThreadHeader Clone() {
                ThreadHeader newObj = new ThreadHeader();
                newObj.mThreadClass = mThreadClass;
                newObj.mThreadFormat = mThreadFormat;
                newObj.mThreadKind = mThreadKind;
                newObj.mThreadCrc = mThreadCrc;
                newObj.mThreadEof = mThreadEof;
                newObj.mCompThreadEof = mCompThreadEof;

                newObj.mOrigFileOffset = mFileOffset;
                newObj.mFileOffset = -1;
                return newObj;
            }

            /// <summary>
            /// Creates a zero-length stub part for situations where a thread was required but not
            /// provided.
            /// </summary>
            /// <param name="rsrcFork">True if we should add a resource fork.</param>
            public static ThreadHeader CreateMiranda(bool rsrcFork) {
                ThreadHeader fakeThread = new ThreadHeader();
                fakeThread.mThreadClass = DATA_CLASS;
                fakeThread.mThreadFormat = (ushort)CompressionFormat.Uncompressed;
                if (!rsrcFork) {
                    fakeThread.mThreadKind = DATA_FORK;
                } else {
                    fakeThread.mThreadKind = RSRC_FORK;
                }
                fakeThread.mThreadCrc = 0xffff;     // for v3 records
                fakeThread.mThreadEof = 0;
                fakeThread.mCompThreadEof = 0;
                fakeThread.mFileOffset = 0;
                return fakeThread;
            }

            public override string ToString() {
                return "[Thread type=" + ThreadKind + " eof=" + mThreadEof + " comp_eof=" +
                    mCompThreadEof + "]";
            }
        }

        /// <summary>
        /// Object that represents an existing thread or a data stream to be added.
        /// </summary>
        private class EditPart {
            /// <summary>
            /// What kind of part this is.
            /// </summary>
            /// <remarks>
            /// For non-file parts, like control threads and file icons, this is set to "unknown".
            /// </remarks>
            public FilePart PartKind { get; private set; }

            /// <summary>
            /// Thread header, from existing thread.  Set for new parts after being written to
            /// archive.
            /// </summary>
            public ThreadHeader? Thread { get; set; }

            /// <summary>
            /// Data source object.  Will be NO_SOURCE for threads being copied from the archive.
            /// </summary>
            public IPartSource Source { get; private set; } = IPartSource.NO_SOURCE;

            /// <summary>
            /// Compression format to use when adding data.
            /// </summary>
            public CompressionFormat Format { get; private set; }

            /// <summary>
            /// Constructor for object representing an existing thread.
            /// </summary>
            /// <param name="thread">Thread header.</param>
            public EditPart(ThreadHeader thread) {
                // We don't reject duplicate threads or try to catch other issues.  The issues
                // already exist; our responsibility is just to make sure they don't get worse.

                // Copy the thread contents.
                Thread = thread.Clone();

                // Identify the type of thread, so we don't create conflicts.
                switch (thread.ThreadKind) {
                    case ThreadHeader.ClassKind.DataFork:
                        PartKind = FilePart.DataFork;
                        break;
                    case ThreadHeader.ClassKind.RsrcFork:
                        PartKind = FilePart.RsrcFork;
                        break;
                    case ThreadHeader.ClassKind.DiskImage:
                        PartKind = FilePart.DiskImage;
                        break;
                    default:
                        Debug.Assert(false);
                        PartKind = FilePart.Unknown;
                        break;
                }

                // No need to set Source/Format since the data already exists.
            }

            /// <summary>
            /// Constructor for object representing new data.
            /// </summary>
            /// <param name="partKind">Which part of the file this is.</param>
            /// <param name="source">Data source object.</param>
            /// <param name="format">Preferred compression format.</param>
            public EditPart(FilePart partKind, IPartSource source, CompressionFormat format) {
                PartKind = partKind;
                Source = source;
                Format = format;
            }

            public override string ToString() {
                return "[Part kind=" + PartKind + " fmt=" + Format + "]";
            }
        }

        /// <summary>
        /// Reference to archive object.
        /// </summary>
        internal NuFX Archive { get; private set; }

        /// <summary>
        /// Object that receives changes made to the file entry.  Set non-null when a
        /// transaction is opened.  Newly-created records point to themselves.
        /// </summary>
        /// <remarks>
        /// <para>This field is never exposed to the application.  The application does all
        /// operations on the original object.  The only time the application has a direct
        /// reference to a change object is when the entry is newly created.</para>
        /// </remarks>
        internal NuFX_FileEntry? ChangeObject { get; set; }

        /// <summary>
        /// Link back to original object, from change object.
        /// </summary>
        internal NuFX_FileEntry? OrigObject { get; set; }

        /// <summary>
        /// File offset of the start of the record.
        /// </summary>
        //internal long RecHdrFileOffset { get; set; }

        /// <summary>
        /// Full length of the record, including headers and data.
        /// </summary>
        //internal long RecordLength { get; private set; }

        /// <summary>
        /// Length of record header, including thread arrays.  The first chunk of thread data
        /// is this many bytes past the start of the record.
        /// </summary>
        //private long mPreThreadLength;

        internal int PartCount { get { return mParts.Count; } }

        //
        // Fields from the record header.
        //
        private ushort mHeaderVersion;
        private ushort mFileSysID;
        private byte mFssep;
        private byte mAccess;
        private uint mFileType;
        private uint mExtraType;
        private ushort mStorageType;
        private ulong mCreateWhen;
        private ulong mModWhen;
        private ulong mArchiveWhen;
        private byte[] mOptionList = RawData.EMPTY_BYTE_ARRAY;
        private byte[] mRawFileName = RawData.EMPTY_BYTE_ARRAY;

        /// <summary>
        /// List of threads found in the archive.
        /// </summary>
        /// <remarks>
        /// We do not create "Miranda threads" for missing data and resource forks (GSHK bug).
        /// We synthesize empty threads when requested.
        /// </remarks>
        private List<ThreadHeader> mThreads = new List<ThreadHeader>();

        /// <summary>
        /// List of parts to create in a new archive.  This can be a mix of old and new.  The
        /// list will be empty unless this is a change object, in which case this will be
        /// populated and "mThreads" will be empty.
        /// </summary>
        private List<EditPart> mParts = new List<EditPart>();

        /// <summary>
        /// Internal buffer, used for reading and writing the record and thread headers.
        /// </summary>
        private byte[] mHeaderBuf =
            new byte[Math.Max(REC_FIXED_LEN, BIG_ATTRIB_COUNT - REC_FIXED_LEN) +
                BIG_THREAD_COUNT * ThreadHeader.LENGTH];


        /// <summary>
        /// Private constructor.
        /// </summary>
        private NuFX_FileEntry(NuFX archive) {
            Archive = archive;
        }

        /// <summary>
        /// Creates a new, empty record.
        /// </summary>
        internal static NuFX_FileEntry CreateNew(NuFX archive) {
            // Most fields can remain set to zero.  The filename will be empty, so this is not
            // yet a valid record.
            NuFX_FileEntry newEntry = new NuFX_FileEntry(archive);
            newEntry.mHeaderVersion = NEW_RECORD_VERSION;
            newEntry.mFssep = (byte)DEFAULT_FSSEP;
            newEntry.mAccess = FileAttribs.FILE_ACCESS_UNLOCKED;
            newEntry.mFileSysID = DEFAULT_FILESYSTEM;
            newEntry.mArchiveWhen = TimeStamp.ConvertDateTime_GS(DateTime.Now);
            return newEntry;
        }

        /// <summary>
        /// Makes a clone of the file entry object, for use as a change object.  Converts the
        /// list of threads to a list of parts.
        /// </summary>
        internal static NuFX_FileEntry CreateChangeObject(NuFX_FileEntry src) {
            if (src.IsDamaged) {
                // Dubious is okay, damaged is not.
                throw new InvalidOperationException("Cannot clone a damaged entry");
            }
            Debug.Assert(src.ChangeObject == null);

            NuFX_FileEntry newEntry = new NuFX_FileEntry(src.Archive);
            CopyFields(newEntry, src);

            // Convert list of ThreadHeaders into list of Parts.  We leave mThreads empty.
            foreach (ThreadHeader thread in src.mThreads) {
                switch (thread.ThreadKind) {
                    case ThreadHeader.ClassKind.FileName:
                    case ThreadHeader.ClassKind.Comment:
                        // Don't make a part for the filename or comment.  Those are handled
                        // separately.
                        continue;
                    case ThreadHeader.ClassKind.DataFork:
                    case ThreadHeader.ClassKind.RsrcFork:
                    case ThreadHeader.ClassKind.DiskImage:
                        break;
                    default:
                        // Nothing special to do for miscellaneous threads; just copy them.
                        break;
                }
                newEntry.mParts.Add(new EditPart(thread));
            }

            return newEntry;
        }

        /// <summary>
        /// Copies all fields from one object to another.
        /// </summary>
        internal static void CopyFields(NuFX_FileEntry dst, NuFX_FileEntry src) {
            // Copy record header fields.
            dst.mHeaderVersion = src.mHeaderVersion;
            dst.mFileSysID = src.mFileSysID;
            dst.mFssep = src.mFssep;
            dst.mAccess = src.mAccess;
            dst.mFileType = src.mFileType;
            dst.mExtraType = src.mExtraType;
            dst.mStorageType = src.mStorageType;
            dst.mCreateWhen = src.mCreateWhen;
            dst.mModWhen = src.mModWhen;
            dst.mArchiveWhen = src.mArchiveWhen;
            dst.mOptionList = new byte[src.mOptionList.Length];
            Array.Copy(src.mOptionList, dst.mOptionList, src.mOptionList.Length);
            dst.mRawFileName = new byte[src.mRawFileName.Length];
            Array.Copy(src.mRawFileName, dst.mRawFileName, src.mRawFileName.Length);

            // Copy processed data fields.
            dst.mFileName = src.mFileName;     // do we need this? should be using raw
            dst.mComment = src.mComment;

            // don't touch the Threads or Parts lists
        }

        /// <summary>
        /// Reads the next record from the NuFX archive.
        /// </summary>
        /// <param name="archive">Archive object.</param>
        /// <returns>File entry for the record, or null if the record was truncated or otherwise
        ///   damaged beyond use.</returns>
        internal static NuFX_FileEntry? ReadNextRecord(NuFX archive) {
            NuFX_FileEntry entry = new NuFX_FileEntry(archive);
            if (!entry.ReadRecord()) {
                return null;
            }
            return entry;
        }

        /// <summary>
        /// Reads a record from the current position in the archive data stream.
        /// </summary>
        /// <returns>True on success.</returns>
        private bool ReadRecord() {
            Stream stream = Archive.DataStream!;
            long recHdrFileOffset = stream.Position;

            // Start by reading the fixed-size portion of the header.
            byte[] hdrBuf = mHeaderBuf;

            try {
                stream.ReadExactly(hdrBuf, 0, REC_FIXED_LEN);
            } catch (EndOfStreamException) {
                Archive.Notes.AddE("Archive truncated in record header");
                return false;
            }

            // Test the signature bytes.
            for (int i = 0; i < RECORD_SIG.Length; i++) {
                if (hdrBuf[i] != RECORD_SIG[i]) {
                    Archive.Notes.AddE("Did not find record header signature");
                    return false;
                }
            }

            // Get the header CRC calculation started.
            ushort calcHdrCrc = CRC16.XMODEM_OnBuffer(0x0000, hdrBuf, 6, REC_FIXED_LEN - 6);

            // Read the fields into members or locals.  We can't interpret some of them yet.
            ushort storedCrc = RawData.GetU16LE(hdrBuf, 0x04);
            ushort attribCount = RawData.GetU16LE(hdrBuf, 0x06);
            mHeaderVersion = RawData.GetU16LE(hdrBuf, 0x08);
            uint threadCount = RawData.GetU32LE(hdrBuf, 0x0a);
            mFileSysID = RawData.GetU16LE(hdrBuf, 0x0e);
            mFssep = hdrBuf[0x10];      // spec makes this a 16-bit quantity with upper byte unused
            mAccess = hdrBuf[0x12];     // 32-bit value, but upper 3 bytes are effectively unused
            mFileType = RawData.GetU32LE(hdrBuf, 0x16);
            mExtraType = RawData.GetU32LE(hdrBuf, 0x1a);
            mStorageType = RawData.GetU16LE(hdrBuf, 0x1e);
            mCreateWhen = RawData.GetU64LE(hdrBuf, 0x20);
            mModWhen = RawData.GetU64LE(hdrBuf, 0x28);
            mArchiveWhen = RawData.GetU64LE(hdrBuf, 0x30);

            // Check a few things.
            if (mHeaderVersion > MAX_REC_VERSION) {
                Archive.Notes.AddE("Unknown record header version: " + mHeaderVersion);
                return false;
            }
            if (attribCount < REC_FIXED_LEN + 2 || attribCount > BIG_ATTRIB_COUNT) {
                Archive.Notes.AddE("Record header attribute count looks wrong: " + attribCount);
                return false;
            }
            if (threadCount > BIG_THREAD_COUNT) {
                Archive.Notes.AddE("Thread count looks wrong: " + threadCount);
                return false;
            }

            // P8 ShrinkIt generated v0/v1 records, GSHK generated v3.  v2, which stored a
            // checksum on the *compressed* data in the record header, was only generated by
            // pre-release GSHK (IIRC), so it is unlikely that there are any instances in
            // wide circulation.  We can reasonably treat v2 as a v1 archive.
            if (mHeaderVersion == 2) {
                Archive.Notes.AddI("Wow, found a v2 header");
                mHeaderVersion = 1;
            }

            // Read the variable-length portion in.  We make an assumption about the maximum
            // size so we can just pull the whole thing in all at once.
            int extraBytes = attribCount - REC_FIXED_LEN;
            try {
                stream.ReadExactly(hdrBuf, 0, extraBytes);
            } catch (EndOfStreamException) {
                Archive.Notes.AddE("Archive truncated in record header part 2");
                return false;
            }
            calcHdrCrc = CRC16.XMODEM_OnBuffer(calcHdrCrc, hdrBuf, 0, extraBytes);

            // Extract the filename length from the end of the chunk.
            ushort fileNameLen = RawData.GetU16LE(hdrBuf, extraBytes - 2);
            extraBytes -= 2;

            // If the version is >= 1, and the attrib_count indicates there's space, we need
            // to check for an option list.
            if (mHeaderVersion >= 1 && extraBytes >= 2) {
                ushort optionSize = RawData.GetU16LE(hdrBuf, 0);
                if (optionSize > extraBytes) {
                    // Apparently, GSHK sometimes creates a bad option list, claiming there are
                    // 36 bytes when there's only room for 18.  Clamp the value to what's
                    // actually available.
                    Archive.Notes.AddW("Found option size " + optionSize +
                        ", but header only holds" + extraBytes);
                    optionSize = (ushort)extraBytes;
                }
                if (optionSize > 0) {
                    mOptionList = new byte[optionSize];
                    Array.Copy(hdrBuf, 2, mOptionList, 0, optionSize);
                }
            }

            // There may be additional data between the option list and the filename length.
            // We're currently ignoring it.

            // If the filename is stored in the record header, read that.
            if (fileNameLen != 0) {
                if (fileNameLen > BIG_PATHNAME_LEN) {
                    Archive.Notes.AddE("Found huge pathname in record header, len=" + fileNameLen);
                    return false;
                }
                mRawFileName = new byte[fileNameLen];
                try {
                    stream.ReadExactly(mRawFileName, 0, fileNameLen);
                } catch (EndOfStreamException) {
                    Archive.Notes.AddE("Archive truncated in filename");
                    return false;
                }
                calcHdrCrc = CRC16.XMODEM_OnBuffer(calcHdrCrc, mRawFileName, 0, fileNameLen);
            }

            // Now read the thread array.
            long totalDataLen = 0;
            long threadDataStart = stream.Position + threadCount * ThreadHeader.LENGTH;
            for (int i = 0; i < threadCount; i++) {
                try {
                    stream.ReadExactly(hdrBuf, 0, ThreadHeader.LENGTH);
                } catch (EndOfStreamException) {
                    Archive.Notes.AddE("Archive truncated in thread header");
                    return false;
                }
                calcHdrCrc = CRC16.XMODEM_OnBuffer(calcHdrCrc, hdrBuf, 0, ThreadHeader.LENGTH);

                ThreadHeader newThread = new ThreadHeader();
                newThread.Load(hdrBuf, 0);
                newThread.mFileOffset = threadDataStart + totalDataLen;

                // Not much to validate in a thread header.  We gloss over threads we don't
                // recognize, so the only thing that can really go wrong is for the
                // comp_thread_eof to be something crazy.  We'll check that later.
                mThreads.Add(newThread);

                totalDataLen += newThread.mCompThreadEof;
            }

            // We should now be positioned at the start of the data.  Confirm that the archive
            // holds all of it.
            if (stream.Length - stream.Position < totalDataLen) {
                Archive.Notes.AddE("Archive truncated in record data");
                IsDamaged = true;
                return false;
            }

            // Record header is finally done.  Remember how big it is.
            //mPreThreadLength = stream.Position - RecHdrFileOffset;

            // Validate the thread types, and dig out the filename if it's in there.
            if (!ProcessThreads(stream)) {
                // We have a bad collection of otherwise valid threads.  Mark the record as
                // damaged and unreadable, but keep going so we process records that follow.
                IsDamaged = true;
            }

            // Generate the Mac OS Roman form of the filename.
            if (mRawFileName.Length == 0) {
                Archive.Notes.AddW("Found record without filename");
                // keep going?
            }
            ProcessFileName();

            if (calcHdrCrc != storedCrc) {
                // Not fatal, but not great.
                Archive.Notes.AddW("Stored CRC does not match record header contents");
                IsDubious = true;
            }

            // The IsDiskImage property should be working now.
            if (IsDiskImage) {
                // Normally this will be 512.  Some old / broken images had this set to 2.
                // We don't use it, and we'll fix it if the record gets written, so there's
                // no need to fix it here.
                if (mStorageType < 16) {
                    Archive.Notes.AddW("Disk image storage type is bad (found " + mStorageType +
                        ", should be 512)");
                }
            } else {
                if (mStorageType == 4 || mStorageType > 5) {
                    // Finding directory or subdir header stuff here would be weird.
                    Archive.Notes.AddW("Unexpected storage type: " + mStorageType);
                }
            }

            if (mFileSysID == (int)FileSystemType.HFS && mFssep == (byte)'?') {
                // A wayward Macintosh application created records with a bad filesystem
                // separator character and big-endian LZW/2 lengths.
                Archive.Notes.AddW("Found a 'bad Mac' record");
                mFssep = (byte)DEFAULT_FSSEP;
            }

            // Success!  Remember the total length so we can make full-record copies.
            stream.Seek(totalDataLen, SeekOrigin.Current);
            //RecordLength = stream.Position - recHdrFileOffset;

            return true;
        }

        /// <summary>
        /// Processes the thread set, extracting the filename and looking for invalid
        /// combinations.
        /// </summary>
        /// <remarks>
        /// The storage_type field must be set before calling here.
        /// </remarks>
        /// <returns>True if all is well, false if the record should not be used.</returns>
        private bool ProcessThreads(Stream stream) {
            bool hasDiskThread = false;
            bool hasFileThread = false;
            bool hasFileNameThread = false;
            bool hasCommentThread = false;

            foreach (ThreadHeader thread in mThreads) {
                switch (thread.ThreadKind) {
                    case ThreadHeader.ClassKind.FileName:
                        if (hasFileNameThread) {
                            Archive.Notes.AddW("Found two filename threads");
                            return false;
                        }
                        if (thread.mThreadEof > 0 && thread.mThreadEof <= BIG_PATHNAME_LEN) {
                            mRawFileName = new byte[thread.mThreadEof];
                            // We confirmed earlier that all record data is present in file, so
                            // this should succeed.
                            long oldPos = stream.Position;
                            stream.Position = thread.mFileOffset;
                            stream.ReadExactly(mRawFileName, 0, (int)thread.mThreadEof);
                            stream.Position = oldPos;
                        } else {
                            Archive.Notes.AddW("Found invalid filename thread with length: " +
                                thread.mThreadEof);
                        }
                        hasFileNameThread = true;
                        break;
                    case ThreadHeader.ClassKind.DataFork:
                    case ThreadHeader.ClassKind.RsrcFork:
                        // Not checking for 2x data or 2x rsrc; easy to just use first.
                        if (hasDiskThread) {
                            Archive.Notes.AddW("Found both file threads and disk image");
                            return false;
                        }
                        hasFileThread = true;
                        break;
                    case ThreadHeader.ClassKind.DiskImage:
                        if (hasFileThread) {
                            Archive.Notes.AddW("Found both file threads and disk image");
                            return false;
                        }
                        hasDiskThread = true;
                        break;
                    case ThreadHeader.ClassKind.Comment:
                        if (hasCommentThread) {
                            // Not fatal, just weird.
                            Archive.Notes.AddW("Found two comment threads");
                            break;
                        }
                        if (thread.mThreadEof == 0) {
                            mComment = string.Empty;
                        } else if (thread.mThreadEof <= BIG_COMMENT_LEN) {
                            byte[] cmtBuf = new byte[thread.mThreadEof];
                            long oldPos = stream.Position;
                            stream.Position = thread.mFileOffset;
                            stream.ReadExactly(cmtBuf, 0, (int)thread.mThreadEof);
                            stream.Position = oldPos;

                            mComment = MacChar.MacToUnicode(cmtBuf, 0, cmtBuf.Length,
                                MacChar.Encoding.Roman);
                        } else {
                            Archive.Notes.AddW("Found invalid comment thread with length: " +
                                thread.mThreadEof);
                        }
                        mCommentFieldLength = thread.mCompThreadEof;
                        hasCommentThread = true;
                        break;
                    default:
                        // Ignore mystery thread.  It will be discarded if the archive is
                        // rewritten.  Flag the record as dubious so the caller gets an indication.
                        IsDubious = true;
                        Archive.Notes.AddW("Found unknown thread type: class=$" +
                            thread.mThreadClass.ToString("x4") + " kind=$" +
                            thread.mThreadKind.ToString("x4"));
                        break;
                }
            }

            if (!hasDiskThread && !hasFileThread) {
                // This can happen for a zero-length file, because GS/ShrinkIt doesn't create
                // a data thread for an empty fork.  We don't need to do anything about it here,
                // but will eventually need to use the storage_type field to see which (empty)
                // forks to create.
                Archive.Notes.AddI("Found empty record (fixed)");
                if (mStorageType == (ushort)ProDOS.StorageType.Extended) {
                    mThreads.Add(ThreadHeader.CreateMiranda(true));
                } else {
                    mThreads.Add(ThreadHeader.CreateMiranda(false));
                }
            }

            // We discard the filename thread when editing an archive (it doesn't become an
            // edit part).  To ensure consistency, throw it away here.
            if (hasFileNameThread) {
                for (int i = 0; i < mThreads.Count; i++) {
                    if (mThreads[i].ThreadKind == ThreadHeader.ClassKind.FileName) {
                        mThreads.RemoveAt(i);
                        break;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Returns the first thread with a matching type, or null if no thread matches.
        /// </summary>
        private ThreadHeader? FindThread(ThreadHeader.ClassKind ck) {
            foreach (ThreadHeader thread in mThreads) {
                if (thread.ThreadKind == ck) {
                    return thread;
                }
            }
            return null;
        }

        /// <summary>
        /// Returns the first thread with a matching type, or null if no thread matches.
        /// </summary>
        private ThreadHeader? FindThread(FilePart part) {
            switch (part) {
                case FilePart.DataFork:
                    return FindThread(ThreadHeader.ClassKind.DataFork);
                case FilePart.DiskImage:
                    return FindThread(ThreadHeader.ClassKind.DiskImage);
                case FilePart.RsrcFork:
                    return FindThread(ThreadHeader.ClassKind.RsrcFork);
                default:
                    return null;
            }
        }

        /// <summary>
        /// Returns the thread_eof fields from the first Thread with a matching type.  If no
        /// thread matches, returns -1.
        /// </summary>
        private long GetThreadEof(ThreadHeader.ClassKind ck) {
            ThreadHeader? thread = FindThread(ck);
            if (thread == null) {
                return -1;
            } else {
                return thread.mThreadEof;
            }
        }

        /// <summary>
        /// Returns true if the specified part is present in the record.  This may only be used
        /// with change objects.
        /// </summary>
        internal bool HasEditPart(FilePart partKind) {
            if (ChangeObject != null && ChangeObject != this) {
                throw new InvalidOperationException("Only valid on change objects");
            }
            foreach (EditPart partObj in mParts) {
                if (partObj.PartKind == partKind) {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Computes the sum of the comp_thread_eof fields of all data-class threads.
        /// </summary>
        private long CalcStorageSize() {
            long size = 0;
            foreach (ThreadHeader thread in mThreads) {
                if (thread.mThreadClass == ThreadHeader.DATA_CLASS) {
                    size += thread.mCompThreadEof;
                }
            }
            return size;
        }

        /// <summary>
        /// Creates an object for reading the contents of the entry out of the archive.
        /// </summary>
        /// <remarks>
        /// <para>We allow access to file forks and disk images.  We don't allow direct access
        /// to filename or comment threads, or arbitrary threads we don't recognize.</para>
        /// </remarks>
        /// <exception cref="FileNotFoundException">Part does not exist.</exception>
        /// <exception cref="NotImplementedException">Compression format not supported.</exception>
        internal ArcReadStream CreateReadStream(FilePart part) {
            ThreadHeader? thread;

            if (part == FilePart.DiskImage) {
                if (!IsDiskImage) {
                    throw new FileNotFoundException("Not part of disk image: " + part);
                }
                thread = FindThread(ThreadHeader.ClassKind.DiskImage);
                if (thread == null) {
                    // Should be impossible.
                    throw new FileNotFoundException("No disk image thread found");
                }
            } else if (part == FilePart.DataFork) {
                if (IsDiskImage) {
                    // TODO: should we allow this to work anyway, just so code that
                    //   doesn't care doesn't have to special-case it?
                    throw new FileNotFoundException("No data fork in disk image");
                }
                thread = FindThread(ThreadHeader.ClassKind.DataFork);
            } else if (part == FilePart.RsrcFork) {
                if (IsDiskImage) {
                    throw new FileNotFoundException("No rsrc fork in disk image");
                }
                thread = FindThread(ThreadHeader.ClassKind.RsrcFork);
            } else {
                throw new FileNotFoundException("Record does not have part: " + part);
            }

            if (thread == null) {
                // This can happen if GSHK failed to add a data_thread for the fork because
                // it was empty.  Check the request against the storage type.
                if (part == FilePart.RsrcFork &&
                        mStorageType != (int)FS.ProDOS.StorageType.Extended) {
                    throw new FileNotFoundException("Record does not include a resource fork");
                }

                // Give them a zero-byte stream from the archive file.
                return new ArcReadStream(Archive, 0, null);
            }
            Checker? checker = null;
            if (mHeaderVersion == 3) {
                checker = new CheckXMODEM16(0xffff, thread.mThreadCrc);
            }

            // Seek to start of compressed or uncompressed data.
            Debug.Assert(Archive.DataStream != null);
            Archive.DataStream.Position = thread.mFileOffset;

            if (thread.mThreadFormat == (int)CompressionFormat.Uncompressed) {
                return new ArcReadStream(Archive, thread.mCompThreadEof, checker);
            }

            // Disk images don't store the uncompressed thread length in the thread header.
            uint threadEof;
            if (part == FilePart.DiskImage) {
                threadEof = mExtraType * BLOCK_SIZE;
            } else {
                threadEof = thread.mThreadEof;
            }

            Stream expander;
            switch ((CompressionFormat)thread.mThreadFormat) {
                case CompressionFormat.Squeeze:
                    expander = new SqueezeStream(Archive.DataStream, CompressionMode.Decompress,
                        true, false, string.Empty);
                    break;
                case CompressionFormat.NuLZW1:
                    expander = new NuLZWStream(Archive.DataStream, CompressionMode.Decompress,
                        true, false, threadEof);
                    break;
                case CompressionFormat.NuLZW2:
                    expander = new NuLZWStream(Archive.DataStream, CompressionMode.Decompress,
                        true, true, threadEof);
                    break;
                default:
                    throw new NotImplementedException("Compression format not supported");
            }
            return new ArcReadStream(Archive, threadEof, checker, expander);
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
                case CompressionFormat.Squeeze:
                case CompressionFormat.NuLZW1:
                case CompressionFormat.NuLZW2:
                    break;
                default:
                    throw new ArgumentException("Compression format " + compressFmt +
                        " not supported for this archive");
            }

            // Take inventory.
            bool hasData = false, hasRsrc = false, hasDisk = false;
            foreach (EditPart curPart in ChangeObject.mParts) {
                switch (curPart.PartKind) {
                    case FilePart.DataFork:
                        hasData = true;
                        break;
                    case FilePart.RsrcFork:
                        hasRsrc = true;
                        break;
                    case FilePart.DiskImage:
                        hasDisk = true;
                        break;
                    default:
                        // Don't care.
                        break;
                }
            }

            // Validate part.
            switch (part) {
                case FilePart.DataFork:
                    if (hasData || hasDisk) {
                        throw new ArgumentException("Record already has a data or disk thread");
                    }
                    break;
                case FilePart.RsrcFork:
                    if (hasRsrc || hasDisk) {
                        throw new ArgumentException("Record already has a resource or disk thread");
                    }
                    break;
                case FilePart.DiskImage:
                    if (hasData || hasRsrc || hasDisk) {
                        throw new ArgumentException(
                            "Record already has a data, resource, or disk thread");
                    }
                    break;
                default:
                    throw new NotSupportedException("Invalid part");
            }

            // Looks good, add it to our list.
            EditPart newPart = new EditPart(part, partSource, compressFmt);
            ChangeObject.mParts.Add(newPart);
        }

        /// <summary>
        /// Deletes a part of the record.
        /// </summary>
        /// <param name="part">Part to delete.</param>
        internal void DeletePart(FilePart part) {
            Debug.Assert(ChangeObject != null);
            int index;
            for (index = 0; index < ChangeObject.mParts.Count; index++) {
                if (ChangeObject.mParts[index].PartKind == part) {
                    break;
                }
            }
            if (index == ChangeObject.mParts.Count) {
                // Already deleted or never existed.  We don't create a part for filenames, so
                // we don't need to handle that specially.
                throw new FileNotFoundException("Record does not have part with type " + part);
            }
            ChangeObject.mParts[index].Source.Dispose();
            ChangeObject.mParts.RemoveAt(index);
        }

        /// <summary>
        /// Writes the record header and data to the output stream.
        /// </summary>
        /// <remarks>
        /// Because all I/O is deferred, we can't know the storage size of most data items until
        /// after they have been written to the archive stream.
        /// </remarks>
        internal void WriteRecord(Stream? srcStream, Stream outputStream) {
            Debug.Assert(mThreads.Count == 0);

            if (mHeaderVersion < 3) {
                mHeaderVersion = 1;
            }

            // Fix the storage type.
            if (HasEditPart(FilePart.RsrcFork)) {
                mStorageType = (ushort)FS.ProDOS.StorageType.Extended;
            } else if (HasEditPart(FilePart.DiskImage)) {
                mStorageType = BLOCK_SIZE;      // always 512
            } else if (mStorageType < 1 || mStorageType > 3) {
                // Not extended, not a disk.  If it already has the storage type of an ordinary
                // file, leave it alone, otherwise set it (maybe they deleted a resource fork).
                mStorageType = 1;
            }

            // Delete the option list if it's not providing value.
            if (mOptionList.Length > 0 && HFSCreator == 0 && HFSFileType == 0) {
                Debug.WriteLine("Discarding option list");
                mOptionList = RawData.EMPTY_BYTE_ARRAY;
            }

            // We need one thread per part, plus a filename thread.
            int numThreads = mParts.Count + 1;
            if (!string.IsNullOrEmpty(mComment) || mCommentFieldLength != 0) {
                numThreads++;
            }

            // The standard header buf has enough room for a large option list and 16 thread
            // headers.  The various tests should have prevented anything larger from getting
            // through to this point.
            //
            // We don't try to preserve arbitrary junk in the record header.  If something
            // actually creates useful data there we will need to revisit this.
            int attribCount = REC_FIXED_LEN + 4;    // base header + option_size + filename_length
            attribCount += mOptionList.Length;      // add in the option list
            int hdrOffset = 0;

            byte[] hdrBuf = mHeaderBuf;
            for (int i = 0; i < RECORD_SIG.Length; i++) {
                hdrBuf[i] = RECORD_SIG[i];
            }
            hdrOffset += RECORD_SIG.Length + 2;                     // leave space for CRC-16
            RawData.WriteU16LE(hdrBuf, ref hdrOffset, (ushort)attribCount);
            RawData.WriteU16LE(hdrBuf, ref hdrOffset, mHeaderVersion);
            RawData.WriteU32LE(hdrBuf, ref hdrOffset, (uint)numThreads);
            RawData.WriteU16LE(hdrBuf, ref hdrOffset, mFileSysID);
            RawData.WriteU16LE(hdrBuf, ref hdrOffset, mFssep);      // high byte will be zero
            RawData.WriteU32LE(hdrBuf, ref hdrOffset, mAccess);     // high 3 bytes will be zero
            RawData.WriteU32LE(hdrBuf, ref hdrOffset, mFileType);
            RawData.WriteU32LE(hdrBuf, ref hdrOffset, mExtraType);
            RawData.WriteU16LE(hdrBuf, ref hdrOffset, mStorageType);
            RawData.WriteU64LE(hdrBuf, ref hdrOffset, mCreateWhen);
            RawData.WriteU64LE(hdrBuf, ref hdrOffset, mModWhen);
            RawData.WriteU64LE(hdrBuf, ref hdrOffset, mArchiveWhen);
            RawData.WriteU16LE(hdrBuf, ref hdrOffset, (ushort)mOptionList.Length);
            Array.Copy(mOptionList, 0, hdrBuf, hdrOffset, mOptionList.Length);
            hdrOffset += mOptionList.Length;
            RawData.WriteU16LE(hdrBuf, ref hdrOffset, 0);           // in-header filename length
            Debug.Assert(hdrOffset == attribCount);

            // Remember where the record starts, so we can go back and write the header CRC.
            long recStartOffset = outputStream.Position;
            // Compute where the thread headers start, so we can write those later.
            long threadOffset = outputStream.Position + hdrOffset;
            // Compute where the thread data starts, so we can write that as we go.
            long dataOffset = threadOffset + numThreads * ThreadHeader.LENGTH;

            //
            // Generate the filename thread, which always comes first.  We didn't create an
            // edit part for this, because we want to have the same behavior whether the filename
            // was stored in the record header or a thread.
            //
            // We can mimic GSHK behavior by using a buffer that is at least 32 bytes long,
            // or we can just make it the size it needs to be.
            //
            int fileNameFieldLen = Math.Max(mRawFileName.Length, GSHK_MIN_NAME_LEN);
            ThreadHeader fileNameThread = new ThreadHeader();
            fileNameThread.mThreadClass = ThreadHeader.FILENAME_CLASS;
            fileNameThread.mThreadFormat = (ushort)CompressionFormat.Uncompressed;
            fileNameThread.mThreadKind = 0;
            fileNameThread.mThreadCrc = 0;
            fileNameThread.mThreadEof = (uint)mRawFileName.Length;
            fileNameThread.mCompThreadEof = (uint)fileNameFieldLen;
            fileNameThread.Store(hdrBuf, hdrOffset);
            hdrOffset += ThreadHeader.LENGTH;

            outputStream.Position = dataOffset;
            outputStream.Write(mRawFileName, 0, mRawFileName.Length);
            if (mRawFileName.Length < fileNameFieldLen) {
                // Apply padding.
                outputStream.Position += fileNameFieldLen - mRawFileName.Length;
                outputStream.SetLength(outputStream.Position);
            }
            dataOffset = outputStream.Position;

            //
            // If we have a comment thread, generate that next.  Preserve the existing thread
            // pre-size value if it exists.
            //
            if (!string.IsNullOrEmpty(mComment) || mCommentFieldLength != 0) {
                // Generate the raw data.
                byte[] cmtBuf = GenerateCommentData(mComment, out int cmtLength);

                if (cmtLength > mCommentFieldLength) {
                    mCommentFieldLength = (uint)cmtLength;
                }

                // Copy it back to pick up CR/LF changes and Mac OS Roman omissions.  If we're
                // editing the record this won't matter, but if it's a new record the object
                // will become part of the new set.
                mComment = MacChar.MacToUnicode(cmtBuf, 0, cmtLength, MacChar.Encoding.Roman);
                uint commentFieldLen = Math.Max((uint)cmtLength, mCommentFieldLength);

                ThreadHeader cmtThread = new ThreadHeader();
                cmtThread.mThreadClass = ThreadHeader.MESSAGE_CLASS;
                cmtThread.mThreadFormat = (ushort)CompressionFormat.Uncompressed;
                cmtThread.mThreadKind = ThreadHeader.COMMENT;
                cmtThread.mThreadCrc = 0;
                cmtThread.mThreadEof = (uint)cmtLength;
                cmtThread.mCompThreadEof = commentFieldLen;
                cmtThread.Store(hdrBuf, hdrOffset);
                hdrOffset += ThreadHeader.LENGTH;

                outputStream.Position = dataOffset;
                outputStream.Write(cmtBuf, 0, cmtLength);
                if (cmtLength < commentFieldLen) {
                    // Apply padding.
                    outputStream.Position += commentFieldLen - cmtLength;
                    outputStream.SetLength(outputStream.Position);
                }
                dataOffset = outputStream.Position;
            }

            //
            // Generate the parts.  For unmodified parts we just copy the existing thread
            // record and archive data.  For new parts we need to read the data and possibly
            // apply compression.
            //
            // We only generate the parts we recognize.
            //
            // If the fork is empty, this won't actually write anything.
            //
            foreach (EditPart editPart in mParts) {
                if (editPart.Source == IPartSource.NO_SOURCE) {
                    // Data is coming from the original archive, unmodified.  Might be compressed,
                    // might not.  Clone the thread header and copy the raw data.
                    Debug.Assert(editPart.Thread != null);
                    outputStream.Position = dataOffset;
                    editPart.Thread.mFileOffset = dataOffset;   // save new offset in thread record
                    if (editPart.Thread.mCompThreadEof != 0) {
                        // A zero EOF could be a Miranda thread, which won't have a source stream.
                        Debug.Assert(srcStream != null);
                        srcStream.Position = editPart.Thread.mOrigFileOffset;
                        FileUtil.CopyStream(srcStream, outputStream,
                            (int)editPart.Thread.mCompThreadEof, Archive.TmpBuf1!);
                    }
                    dataOffset = outputStream.Position;
                } else {
                    // Read the data from the part source, and write it to the archive.
                    outputStream.Position = dataOffset;
                    ThreadHeader thread = WritePart(outputStream, editPart);
                    Debug.Assert(outputStream.Position - dataOffset == thread.mCompThreadEof);
                    editPart.Thread = thread;
                    dataOffset = outputStream.Position;

                    // Now that we know how large the data is, do special handling for
                    // disk images.
                    if (editPart.Thread.ThreadKind == ThreadHeader.ClassKind.DiskImage) {
                        if (editPart.Thread.mThreadEof % BLOCK_SIZE != 0) {
                            throw new InvalidDataException(
                                "Disk image is not a multiple of 512 bytes");
                        }
                        mFileType = 0x00;
                        mExtraType = editPart.Thread.mThreadEof / BLOCK_SIZE;
                        RawData.SetU32LE(hdrBuf, 0x16, mFileType);
                        RawData.SetU32LE(hdrBuf, 0x1a, mExtraType);
                        Debug.Assert(mStorageType == BLOCK_SIZE);   // set earlier

                        // For consistency with ShrinkIt, zero out the thread EOF.
                        editPart.Thread.mThreadEof = 0;
                    }
                }

                editPart.Thread.Store(hdrBuf, hdrOffset);   // copy to header buf
                hdrOffset += ThreadHeader.LENGTH;
            }
            long endPosition = outputStream.Position;

            // Calculate the CRC on the record and thread headers.
            ushort crc = CRC16.XMODEM_OnBuffer(0x0000, hdrBuf, 6, hdrOffset - 6);
            RawData.SetU16LE(hdrBuf, 4, crc);

            // Go back and write the record header and thread headers.
            outputStream.Position = recStartOffset;
            outputStream.Write(hdrBuf, 0, hdrOffset);

            // Leave the file positioned at the end of the record data.
            outputStream.Position = endPosition;
        }

        /// <summary>
        /// Writes a part to the archive output stream.  The details are stored in a new
        /// ThreadHeader.
        /// </summary>
        private ThreadHeader WritePart(Stream outputStream, EditPart editPart) {
            CompressionFormat format = editPart.Format;
            Stream? compStream;
            switch (format) {
                case CompressionFormat.Uncompressed:
                    compStream = null;
                    break;
                case CompressionFormat.Default:
                    if (mHeaderVersion < 3) {
                        format = CompressionFormat.NuLZW1;      // old record, use LZW/1
                        compStream = new NuLZWStream(outputStream, CompressionMode.Compress,
                            true, false, -1);
                    } else {
                        format = CompressionFormat.NuLZW2;      // new record, use LZW/2
                        compStream = new NuLZWStream(outputStream, CompressionMode.Compress,
                            true, true, -1);
                    }
                    break;
                case CompressionFormat.Squeeze:
                    compStream = new SqueezeStream(outputStream, CompressionMode.Compress,
                        true, false, string.Empty);
                    break;
                case CompressionFormat.NuLZW1:
                    compStream = new NuLZWStream(outputStream, CompressionMode.Compress,
                        true, false, -1);
                    break;
                case CompressionFormat.NuLZW2:
                    compStream = new NuLZWStream(outputStream, CompressionMode.Compress,
                        true, true, -1);
                    break;
                default:
                    throw new ArgumentException("Invalid compression format " + format +
                        ": " + FileName);
            }

            // The smallest possible LZW/2 output is 24 bytes (including all headers), for a
            // block full of zeroes.  However, we don't know how much data we're about to be
            // sent, so we can't short-circuit the process for tiny files.

            ThreadHeader thd = new ThreadHeader();
            bool needCheck = true;
            switch (editPart.PartKind) {
                case FilePart.DataFork:
                    thd.ThreadKind = ThreadHeader.ClassKind.DataFork;
                    break;
                case FilePart.DiskImage:
                    thd.ThreadKind = ThreadHeader.ClassKind.DiskImage;
                    break;
                case FilePart.RsrcFork:
                    thd.ThreadKind = ThreadHeader.ClassKind.RsrcFork;
                    break;
                default:
                    // Shouldn't be able to add a part we don't recognize.
                    throw new NotImplementedException("Can't handle part kind " +
                        editPart.PartKind);
            }

            Checker? checker = null;
            if (needCheck && mHeaderVersion >= 3) {
                checker = new CheckXMODEM16(0xffff, 0);
            }

            long startPosn = outputStream.Position;
            if (ArcUtil.CopyPartSource(editPart.Source, outputStream, compStream, checker,
                    true, Archive.TmpBuf1!, out int inputLength, out int outputLength)) {
                thd.mThreadFormat = (ushort)format;
            } else {
                thd.mThreadFormat = (ushort)CompressionFormat.Uncompressed;
            }
            if (checker != null) {
                thd.mThreadCrc = (ushort)checker.Value;
            }

            thd.mThreadEof = (uint)inputLength;
            thd.mCompThreadEof = (uint)outputLength;
            thd.mFileOffset = startPosn;
            return thd;
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
                Debug.Assert(mParts.Count == 0);
                CopyFields(this, ChangeObject);
            } else {
                Debug.Assert(OrigObject != null);
                OrigObject = null;
            }

            // Copy the list of threads from the "edit parts" list to the "threads" list.
            Debug.Assert(ChangeObject.mThreads.Count == 0);
            mThreads.Clear();
            foreach (EditPart part in ChangeObject.mParts) {
                Debug.Assert(part.Thread != null);
                part.Thread.mOrigFileOffset = -1;
                mThreads.Add(part.Thread);
            }
            Debug.Assert(mThreads.Count == ChangeObject.mParts.Count);
            mParts.Clear();
            ChangeObject = null;
        }

        internal void DisposeSources() {
            Debug.Assert(ChangeObject != null);
            foreach (EditPart editPart in ChangeObject.mParts) {
                if (editPart.Source != IPartSource.NO_SOURCE) {
                    editPart.Source.Dispose();
                }
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

        /// <summary>
        /// Converts the raw filename bytes to a Unicode string.
        /// </summary>
        /// <remarks>
        /// <para>In theory, the contents of the record filename field could be from a variety of
        /// operating systems.  In practice, it's ProDOS or HFS.  Even if it isn't, the Mac OS
        /// Roman conversion is fully reversible.</para>
        /// <para>The only non-reversible thing we do is high-bit stripping.
        /// ShrinkIt v3.0.0 had a bug where the filename thread would get created
        /// with the high bits set.  We want to undo that without stomping on
        /// filenames that just happen to have a non-ASCII character in them.  If all
        /// of the high bits are set, assume it's a "defective" name and clear them all.</para>
        /// <para>This high-bit-ism was also done for disk archives by most older versions
        /// of ShrinkIt, so we can't necessarily limit the clear to records identified as
        /// ProDOS.  Should be safe to exclude HFS however.</para>
        /// </remarks>
        private void ProcessFileName() {
            if (mFileSysID != (ushort)FileSystemType.HFS) {
                StripHiIfAllSet(mRawFileName);
            }
            mFileName = MacChar.MacToUnicode(mRawFileName, 0, mRawFileName.Length,
                MacChar.Encoding.RomanShowCtrl);
        }

        /// <summary>
        /// Strips the high bit from raw filename bytes if they are set on all bytes.  Modifies
        /// the data in place.
        /// </summary>
        private static void StripHiIfAllSet(byte[] buf) {
            foreach (byte b in buf) {
                if ((b & 0x80) == 0) {
                    return;
                }
            }
            for (int i = 0; i < buf.Length; i++) {
                buf[i] &= 0x7f;
            }
        }

        // IFileEntry
        public int CompareFileName(string fileName) {
            return string.Compare(mFileName, fileName, StringComparison.OrdinalIgnoreCase);
        }

        // IFileEntry
        public int CompareFileName(string fileName, char fileNameSeparator) {
            return PathName.ComparePathNames(mFileName, (char)mFssep, fileName,
                fileNameSeparator, PathName.CompareAlgorithm.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Sets the "raw" and "cooked" filenames, from a string.
        /// </summary>
        /// <remarks>
        /// <para>All characters are treated as Mac OS Roman.  This works for ProDOS and
        /// HFS.  Characters outside the character set will be rejected.</para>
        /// </remarks>
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
            if (!MacChar.IsStringValid(fileName, MacChar.Encoding.RomanShowCtrl)) {
                throw new ArgumentException("Invalid characters found: " + fileName);
            }

            mFileName = fileName;
            mRawFileName = new byte[fileName.Length];
            MacChar.UnicodeToMac(fileName, mRawFileName, 0, MacChar.Encoding.RomanShowCtrl);
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
            if (rawFileName.Length > BIG_PATHNAME_LEN) {
                throw new ArgumentException("Filename is excessively long (" +
                    rawFileName.Length + ")");
            }
            mRawFileName = new byte[rawFileName.Length];
            Array.Copy(rawFileName, mRawFileName, rawFileName.Length);
            mFileName = MacChar.MacToUnicode(rawFileName, 0, rawFileName.Length,
                MacChar.Encoding.RomanShowCtrl);
        }

        /// <summary>
        /// Generates the raw data for the comment field.
        /// </summary>
        /// <remarks>
        /// We want to convert Unicode to ASCII or (if we're feeling generous) Mac OS Roman.  We
        /// should also convert LF and CRLF to CR.
        /// </remarks>
        /// <param name="length">Result: length of data in buffer.</param>
        private static byte[] GenerateCommentData(string comment, out int length) {
            Debug.Assert(comment != null);
            // The transformed data will be the same length or shorter.
            byte[] outBuf = new byte[comment.Length];
            bool lastWasCr = false;
            int offset = 0;
            for (int i = 0; i < comment.Length; i++) {
                byte val = MacChar.UnicodeToMac(comment[i], (byte)'?', MacChar.Encoding.Roman);
                if (val == '\r') {
                    lastWasCr = true;
                    outBuf[offset++] = val;
                } else if (val == '\n') {
                    if (lastWasCr) {
                        // CRLF, do nothing
                    } else {
                        // standalone LF
                        outBuf[offset++] = (byte)'\r';
                    }
                    lastWasCr = false;
                } else {
                    outBuf[offset++] = val;
                    lastWasCr = false;
                }
            }
            length = offset;
            return outBuf;
        }

        #endregion Filenames

        public override string ToString() {
            StringBuilder sb = new StringBuilder();
            sb.Append("[NuFX-entry '");
            sb.Append(FileName);
            sb.Append("' ");
            if (mThreads.Count != 0) {
                sb.Append(" threads={");
                foreach (ThreadHeader thd in mThreads) {
                    switch (thd.ThreadKind) {
                        case ThreadHeader.ClassKind.DataFork:
                            sb.Append("data,");
                            break;
                        case ThreadHeader.ClassKind.RsrcFork:
                            sb.Append("rsrc,");
                            break;
                        case ThreadHeader.ClassKind.DiskImage:
                            sb.Append("disk,");
                            break;
                        case ThreadHeader.ClassKind.Comment:
                            sb.Append("cmmt,");
                            break;
                        case ThreadHeader.ClassKind.FileName:
                            sb.Append("name,");
                            break;
                        default:
                            sb.Append("????,");
                            break;
                    }
                }
                sb.Append("}");
            }
            if (mParts.Count != 0) {
                sb.Append(" parts={");
                foreach (EditPart part in mParts) {
                    switch (part.PartKind) {
                        case FilePart.DataFork:
                            sb.Append("data,");
                            break;
                        case FilePart.RsrcFork:
                            sb.Append("rsrc,");
                            break;
                        case FilePart.DiskImage:
                            sb.Append("disk,");
                            break;
                        default:
                            sb.Append("????,");
                            break;

                    }
                }
                sb.Append("}");
            }
            sb.Append("]");
            return sb.ToString();
        }
    }
}
