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
    /// GZip archive handling.
    /// </summary>
    public class GZip : IArchiveExt {
        private static readonly ArcCharacteristics sCharacteristics = new ArcCharacteristics(
            name: "GZip",
            canWrite: true,
            hasSingleEntry: true,
            hasResourceForks: false,
            hasDiskImages: false,
            hasArchiveComment: false,
            hasRecordComments: false,       // currently ignoring gzip comment field
            defaultDirSep: '/');

        //
        // IArchive interfaces.
        //

        public ArcCharacteristics Characteristics => sCharacteristics;

        public Stream? DataStream { get; internal set; }

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
        /// List of records.
        /// </summary>
        internal List<GZip_FileEntry> RecordList { get; private set; } =
            new List<GZip_FileEntry>(1);

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
                return "[GZip open]";
            }
        }

        /// <summary>
        /// List of open files.  We only have one record, but it has two openable parts, and
        /// each can be opened more than once.
        /// </summary>
        private List<OpenFileRec> mOpenFiles = new List<OpenFileRec>();

        /// <summary>
        /// Tests a stream to see if it contains a GZip archive.
        /// </summary>
        /// <param name="stream">Stream to test.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <returns>True if this looks like GZip.</returns>
        public static bool TestKind(Stream stream, AppHook appHook) {
            // The ".gz" file extension is a pretty solid indication of what's in the file, so
            // we don't need to do a particularly deep check here.
            stream.Position = 0;
            if (stream.Length < GZip_FileEntry.MIN_HEADER_LEN + GZip_FileEntry.FOOTER_LEN) {
                // Too short to be valid.
                return false;
            }
            byte[] hdrBuf = new byte[GZip_FileEntry.MIN_HEADER_LEN];
            stream.ReadExactly(hdrBuf, 0, hdrBuf.Length);       // shouldn't fail
            if (hdrBuf[0] != GZip_FileEntry.ID1 || hdrBuf[1] != GZip_FileEntry.ID2 ||
                    hdrBuf[2] != GZip_FileEntry.CM_DEFLATE ||
                    (hdrBuf[3] & ~GZip_FileEntry.FLAG_MASK) != 0) {
                Debug.WriteLine("Not a GZip header");
                return false;
            }
            return true;
        }

        /// <summary>
        /// Private constructor.
        /// </summary>
        /// <param name="stream">Data stream if existing file, null if new archive.</param>
        /// <param name="appHook">Application hook reference.</param>
        private GZip(Stream? stream, AppHook appHook) {
            DataStream = stream;
            AppHook = appHook;
        }

        /// <summary>
        /// Creates an empty archive object.
        /// </summary>
        /// <param name="appHook">Application hook reference.</param>
        /// <returns>Archive object.</returns>
        public static GZip CreateArchive(AppHook appHook) {
            GZip archive = new GZip(null, appHook);
            GZip_FileEntry newEntry = new GZip_FileEntry(archive);
            archive.RecordList.Add(newEntry);
            return archive;
        }

        /// <summary>
        /// Opens an existing archive.
        /// </summary>
        /// <param name="stream">Archive data stream.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <returns>Archive instance.</returns>
        /// <exception cref="NotSupportedException">Data stream is not GZip.</exception>
        public static GZip OpenArchive(Stream stream, AppHook appHook) {
            stream.Position = 0;
            GZip archive = new GZip(stream, appHook);
            GZip_FileEntry newEntry = new GZip_FileEntry(archive);
            archive.RecordList.Add(newEntry);
            if (!newEntry.Scan()) {
                throw new NotSupportedException("Incompatible data stream");
            }
            return archive;
        }

        // IArchive
        public Stream? ReopenStream(Stream newStream) {
            Debug.Assert(DataStream != null);
            Stream? oldStream = DataStream;
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
                    AppHook.LogW("GZip disposed while " + mOpenFiles.Count + " files are open");
                    // Walk through from end to start so we don't trip when entries are removed.
                    for (int i = mOpenFiles.Count - 1; i >= 0; --i) {
                        // This will call back into our StreamClosing function, which will
                        // remove it from the list.
                        mOpenFiles[i].ReadStream.Close();
                    }
                }
            }
            if (mIsTransactionOpen) {
                AppHook.LogW("Disposing of GZip while transaction open");
                CancelTransaction();
            }
        }

        // IArchive
        public ArcReadStream OpenPart(IFileEntry ientry, FilePart part) {
            if (mIsTransactionOpen) {
                throw new InvalidOperationException("Cannot open parts while transaction is open");
            }
            GZip_FileEntry entry = (GZip_FileEntry)ientry;
            if (entry.Archive != this) {
                throw new ArgumentException("Entry is not part of this archive");
            }
            ArcReadStream newStream = entry.CreateReadStream();
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
            GZip_FileEntry entry = RecordList[0];
            entry.ChangeObject = GZip_FileEntry.CreateChangeObject(entry);
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
            GZip_FileEntry entry = RecordList[0];
            GZip_FileEntry? changeEntry = entry.ChangeObject;
            Debug.Assert(changeEntry != null);
            try {
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
            GZip_FileEntry entry = RecordList[0];
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
            throw new NotSupportedException("Additional records cannot be created");
        }

        // IArchive
        public void AddPart(IFileEntry ientry, FilePart part, IPartSource partSource,
                CompressionFormat compressFmt) {
            CheckModAccess(ientry);
            if (part != FilePart.DataFork) {
                throw new NotSupportedException("Cannot add part: " + part);
            }
            if (partSource == IPartSource.NO_SOURCE) {
                throw new ArgumentException("Invalid part source");
            }
            if (compressFmt != CompressionFormat.Deflate &&
                    compressFmt != CompressionFormat.Default) {
                // Should we simply ignore requests for "uncompressed", rather than failing?
                throw new ArgumentException("Compression type is not supported");
            }
            ((GZip_FileEntry)ientry).AddPart(partSource);
        }

        // IArchive
        public void DeletePart(IFileEntry ientry, FilePart part) {
            CheckModAccess(ientry);
            if (part != FilePart.DataFork) {
                throw new NotSupportedException("Cannot delete part: " + part);
            }
            ((GZip_FileEntry)ientry).DeletePart();
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
            GZip_FileEntry? entry = ientry as GZip_FileEntry;
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
