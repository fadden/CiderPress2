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

using CommonUtil;
using static DiskArc.Defs;

namespace DiskArc.Arc {
    /// <summary>
    /// AppleSingle archive handling.
    /// </summary>
    public class AppleSingle : IArchiveExt {
        /// <summary>
        /// Preferred filename extension for AppleSingle files.
        /// </summary>
        public const string AS_EXT = ".as";

        /// <summary>
        /// Preferred prefix for AppleDouble header files.
        /// </summary>
        public const string ADF_PREFIX = "._";

        internal const uint SIGNATURE = 0x00051600;
        internal const uint ADF_SIGNATURE = 0x00051607;
        internal const uint VERSION_1 = 0x00010000;
        internal const uint VERSION_2 = 0x00020000;
        internal const int HEADER_LEN = 26;                 // file header length; 4+4+16+2

        private const string FILENAME_RULES = "Filename only; may be empty.";
        private static readonly ArcCharacteristics sCharacteristics = new ArcCharacteristics(
            name: "AppleSingle",
            canWrite: true,
            hasSingleEntry: true,
            hasResourceForks: true,
            hasDiskImages: false,
            hasArchiveComment: false,
            hasRecordComments: false,               // TODO: "standard Macintosh comment" entries
            defaultDirSep: IFileEntry.NO_DIR_SEP,   // partial paths are not supported
            fnSyntax: FILENAME_RULES,
            // AppleSingle v1 can have a system-specific timestamp, so this isn't quite right.
            tsStart: AppleSingle_FileEntry.ConvertDateTime_AS2K(int.MinValue + 1),
            tsEnd: AppleSingle_FileEntry.ConvertDateTime_AS2K(int.MaxValue)
        );

        //
        // IArchive interfaces.
        //

        public ArcCharacteristics Characteristics => sCharacteristics;
        public static ArcCharacteristics SCharacteristics => sCharacteristics;

        public Stream? DataStream { get; internal set; }

        public bool IsReadOnly => IsDubious;
        public bool IsDubious { get; internal set; }

        public Notes Notes { get; } = new Notes();

        public bool IsReconstructionNeeded => true;

        public string Comment { get => string.Empty; set { } }

        public IEnumerator<IFileEntry> GetEnumerator() {
            return RecordList.GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() {
            return RecordList.GetEnumerator();
        }
        public int Count => RecordList.Count;

        //
        // Internal interfaces.
        //

        /// <summary>
        /// Application hook reference.
        /// </summary>
        internal AppHook AppHook { get; private set; }

        /// <summary>
        /// True if this is an AppleDouble "header" file.
        /// </summary>
        public bool IsAppleDouble { get; internal set; }

        /// <summary>
        /// List of records.
        /// </summary>
        internal List<AppleSingle_FileEntry> RecordList { get; private set; } =
            new List<AppleSingle_FileEntry>(1);

        /// <summary>
        /// True if a transaction is open.
        /// </summary>
        private bool mIsTransactionOpen;

        // Temporary buffer, used for stream copying and compression, allocated on first use.
        internal byte[]? TmpBuf { get; private set; }

        /// <summary>
        /// Open-file tracking.
        /// </summary>
        private class OpenFileRec {
            public ArcReadStream ReadStream { get; private set; }

            public OpenFileRec(ArcReadStream stream) {
                ReadStream = stream;
            }

            public override string ToString() {
                return "[AppleSingle open]";
            }
        }

        /// <summary>
        /// List of open files.  We only have one record, but it has two openable parts, and
        /// each can be opened more than once.
        /// </summary>
        private List<OpenFileRec> mOpenFiles = new List<OpenFileRec>();


        /// <summary>
        /// Tests a stream to see if it is an AppleSingle file.
        /// </summary>
        /// <param name="stream">Stream to test.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <returns>True if this looks like AppleSingle.</returns>
        public static bool TestKind(Stream stream, AppHook appHook) {
            if (stream.Length < HEADER_LEN) {
                return false;
            }
            stream.Position = 0;
            // Test the signature and version number.
            byte[] buf = new byte[8];
            stream.ReadExactly(buf, 0, buf.Length);
            if (RawData.GetU32BE(buf, 0) == SIGNATURE) {
                // spec-compliant variety
                uint version = RawData.GetU32BE(buf, 4);
                return (version == VERSION_1 || version == VERSION_2);
            } else if (RawData.GetU32LE(buf, 0) == SIGNATURE) {
                // rare bad-Mac variety
                uint version = RawData.GetU32LE(buf, 4);
                return (version == VERSION_1 || version == VERSION_2);
            } else {
                return false;
            }
        }

        /// <summary>
        /// Tests a stream to see if it is an AppleDouble file.
        /// </summary>
        /// <param name="stream">Stream to test.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <returns>True if this looks like AppleDouble.</returns>
        public static bool TestDouble(Stream stream, AppHook appHook) {
            if (stream.Length < HEADER_LEN) {
                return false;
            }
            stream.Position = 0;
            // Test the signature and version number.
            byte[] buf = new byte[8];
            stream.ReadExactly(buf, 0, buf.Length);
            if (RawData.GetU32BE(buf, 0) == ADF_SIGNATURE) {
                // AppleDouble "header" file
                uint version = RawData.GetU32BE(buf, 4);
                return (version == VERSION_1 || version == VERSION_2);
            } else {
                return false;
            }
        }

    /// <summary>
    /// Private constructor.
    /// </summary>
    /// <param name="stream">Data stream if existing file, null if new archive.</param>
    /// <param name="appHook">Application hook reference.</param>
    private AppleSingle(Stream? stream, AppHook appHook) {
            DataStream = stream;
            AppHook = appHook;
        }

        /// <summary>
        /// Creates an empty archive object.
        /// </summary>
        /// <param name="version">Version number (1 or 2).</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <returns>Archive object.</returns>
        public static AppleSingle CreateArchive(int version, AppHook appHook) {
            if (version != 1 && version != 2) {
                throw new ArgumentException("Unsupported version " + version);
            }
            AppleSingle newArchive = new AppleSingle(null, appHook);
            AppleSingle_FileEntry newEntry = new AppleSingle_FileEntry(newArchive);
            newArchive.RecordList.Add(newEntry);
            newEntry.VersionNumber = version;
            if (version == 1) {
                newEntry.HomeFS = FileSystemType.ProDOS;
            } else {
                newEntry.HomeFS = FileSystemType.Unknown;
            }
            return newArchive;
        }

        /// <summary>
        /// Creates an empty AppleDouble "header" file.
        /// </summary>
        /// <param name="version">Version number (1 or 2).</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <returns>Archive object.</returns>
        public static AppleSingle CreateDouble(int version, AppHook appHook) {
            AppleSingle newArchive = CreateArchive(version, appHook);
            newArchive.IsAppleDouble = true;
            return newArchive;
        }

        /// <summary>
        /// Opens an existing archive.  May be used for AppleSingle and AppleDouble.
        /// </summary>
        /// <param name="stream">Archive data stream.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <returns>Archive instance.</returns>
        /// <exception cref="NotSupportedException">Data stream is not AppleSingle or AppleDouble
        ///   format.</exception>
        public static AppleSingle OpenArchive(Stream stream, AppHook appHook) {
            stream.Position = 0;
            AppleSingle archive = new AppleSingle(stream, appHook);
            AppleSingle_FileEntry newEntry = new AppleSingle_FileEntry(archive);
            archive.RecordList.Add(newEntry);
            if (!newEntry.Scan()) {
                throw new NotSupportedException("Incompatible data stream");
            }
            return archive;
        }

        // IArchive
        public Stream? ReopenStream(Stream newStream) {
            Debug.Assert(DataStream != null);
            Stream oldStream = DataStream;
            DataStream = newStream;
            return oldStream;
        }

        // IDisposable generic Dispose() implementation.
        public void Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        protected virtual void Dispose(bool disposing) {
            if (disposing) {
                // We're being disposed explicitly (not by the GC).  Dispose of all open entries,
                // so that attempts to continue to use them will start failing immediately.
                if (mOpenFiles.Count != 0) {
                    AppHook.LogW("AppleSingle disposed while " + mOpenFiles.Count +
                        " files are open");
                    // Walk through from end to start so we don't trip when entries are removed.
                    for (int i = mOpenFiles.Count - 1; i >= 0; --i) {
                        // This will call back into our StreamClosing function, which will
                        // remove it from the list.
                        mOpenFiles[i].ReadStream.Close();
                    }
                }
            }
            if (mIsTransactionOpen) {
                AppHook.LogW("Disposing of AppleSingle while transaction open");
                CancelTransaction();
            }
        }

        // IArchive
        public ArcReadStream OpenPart(IFileEntry ientry, FilePart part) {
            if (mIsTransactionOpen) {
                throw new InvalidOperationException("Cannot open parts while transaction is open");
            }
            AppleSingle_FileEntry entry = (AppleSingle_FileEntry)ientry;
            if (entry.Archive != this) {
                throw new ArgumentException("Entry is not part of this archive");
            }
            ArcReadStream newStream = entry.CreateReadStream(part);
            mOpenFiles.Add(new OpenFileRec(newStream));
            return newStream;
        }

        // IArchiveExt
        public void StreamClosing(ArcReadStream stream) {
            for (int i = mOpenFiles.Count - 1; i >= 0; --i) {
                if (mOpenFiles[i].ReadStream == stream) {
                    mOpenFiles.RemoveAt(i);
                    return;
                }
            }
            Debug.Assert(false, "Got StreamClosing for unknown stream");
        }

        // IArchive
        public void StartTransaction() {
            if (IsDubious) {
                throw new InvalidOperationException("Cannot modify a dubious archive");
            }
            if (mOpenFiles.Count != 0) {
                throw new InvalidOperationException("One or more entries are open");
            }
            if (mIsTransactionOpen) {
                throw new InvalidOperationException("Transaction is already open");
            }
            AppleSingle_FileEntry entry = RecordList[0];
            entry.ChangeObject = AppleSingle_FileEntry.CreateChangeObject(entry);
            entry.ChangeObject.OrigObject = entry;
            mIsTransactionOpen = true;
        }

        // IArchive
        public void CommitTransaction(Stream? outputStream) {
            if (!mIsTransactionOpen) {
                throw new InvalidOperationException("Must start transaction first");
            }

            if (outputStream != null) {
                if (!outputStream.CanWrite || !outputStream.CanSeek) {
                    throw new ArgumentException("Invalid output stream");
                }
                if (outputStream == DataStream) {
                    throw new InvalidOperationException("Cannot commit to source stream");
                }
            } else {
                throw new ArgumentException("Output stream cannot be null");
            }
            Debug.Assert(DataStream == null || DataStream.CanRead);

            Notes.Clear();

            // Allocate temporary buffer for stream data if we haven't yet.
            if (TmpBuf == null) {
                const int size = 32768;
                TmpBuf = new byte[size];
            }

            outputStream.Position = 0;
            outputStream.SetLength(0);
            AppleSingle_FileEntry entry = RecordList[0];
            AppleSingle_FileEntry? changeEntry = entry.ChangeObject;
            Debug.Assert(changeEntry != null);
            try {
                // Write the record.  It's okay if it has neither data nor rsrc fork data.
                changeEntry.WriteRecord(outputStream);
            } catch {
                // Failed; truncate output stream so we don't have a partial archive sitting around.
                outputStream.Position = 0;
                outputStream.SetLength(0);
                throw;
            }

            // Success!  Switch to the new archive stream and updated object.
            DataStream = outputStream;
            DataStream.Flush();
            entry.DisposeSources();
            entry.CommitChange();

            mIsTransactionOpen = false;
        }

        // IArchive
        public void CancelTransaction() {
            if (!mIsTransactionOpen) {
                return;
            }
            AppleSingle_FileEntry entry = RecordList[0];
            entry.DisposeSources();
            entry.ChangeObject = null;
            mIsTransactionOpen = false;
        }

        // IArchive
        public IFileEntry CreateRecord() {
            throw new NotSupportedException("Additional records cannot be created");
        }

        // IArchive
        public void DeleteRecord(IFileEntry entry) {
            throw new NotSupportedException("The record cannot be deleted");
        }

        // IArchive
        public void AddPart(IFileEntry ientry, FilePart part, IPartSource partSource,
                CompressionFormat compressFmt) {
            CheckModAccess(ientry);
            if (part != FilePart.DataFork && part != FilePart.RsrcFork) {
                throw new NotSupportedException("Invalid part: " + part);
            }
            if (partSource == IPartSource.NO_SOURCE) {
                throw new ArgumentException("Invalid part source");
            }
            if (compressFmt != CompressionFormat.Uncompressed &&
                    compressFmt != CompressionFormat.Default) {
                throw new ArgumentException("Compression is not supported");
            }
            ((AppleSingle_FileEntry)ientry).AddPart(part, partSource);
        }

        // IArchive
        public void DeletePart(IFileEntry ientry, FilePart part) {
            CheckModAccess(ientry);
            ((AppleSingle_FileEntry)ientry).DeletePart(part);
        }

        /// <summary>
        /// Confirms that we are allowed to modify this entry.
        /// </summary>
        /// <param name="ientry">Entry that will be modified.</param>
        private void CheckModAccess(IFileEntry ientry) {
            if (!mIsTransactionOpen) {
                throw new InvalidOperationException("Must start transaction first");
            }
            if (ientry == IFileEntry.NO_ENTRY) {
                throw new ArgumentException("Cannot operate on NO_ENTRY");
            }
            if (ientry.IsDamaged) {
                throw new ArgumentException("Record '" + ientry.FileName +
                    "' is too damaged to access");
            }
            AppleSingle_FileEntry? entry = ientry as AppleSingle_FileEntry;
            if (entry == null || entry.Archive != this) {
                if (entry != null && entry.Archive == null) {
                    throw new ArgumentException("Entry is invalid");
                } else {
                    throw new FileNotFoundException("Entry is not part of this archive");
                }
            } else {
                Debug.Assert(entry.ChangeObject != null);
            }
        }
    }
}
