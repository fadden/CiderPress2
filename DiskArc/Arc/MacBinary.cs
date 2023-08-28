﻿/*
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

using CommonUtil;
using static DiskArc.Defs;

namespace DiskArc.Arc {
    /// <summary>
    /// MacBinary (I, II, III) archive handling.
    /// </summary>
    public class MacBinary : IArchiveExt {
        private const string FILENAME_RULES = "1-31 characters.  Must not include ':'.";

        private static readonly ArcCharacteristics sCharacteristics = new ArcCharacteristics(
            name: "MacBinary",
            canWrite: false,
            hasSingleEntry: true,
            hasResourceForks: true,
            hasDiskImages: false,
            hasArchiveComment: false,
            hasRecordComments: false,               // TODO: optional "info" chunk
            defaultDirSep: IFileEntry.NO_DIR_SEP,   // partial paths are not supported
            fnSyntax: FILENAME_RULES,
            tsStart: TimeStamp.HFS_MIN_TIMESTAMP,
            tsEnd: TimeStamp.HFS_MAX_TIMESTAMP
        );

        //
        // IArchive interfaces.
        //

        public ArcCharacteristics Characteristics => throw new NotImplementedException();
        public static ArcCharacteristics SCharacteristics => sCharacteristics;

        public Stream? DataStream { get; internal set; }

        public bool IsReadOnly => true;
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
        internal List<MacBinary_FileEntry> RecordList { get; private set; } =
            new List<MacBinary_FileEntry>(1);

        /// <summary>
        /// Open-file tracking.
        /// </summary>
        private class OpenFileRec {
            public ArcReadStream ReadStream { get; private set; }

            public OpenFileRec(ArcReadStream stream) {
                ReadStream = stream;
            }

            public override string ToString() {
                return "[MacBinary open]";
            }
        }

        /// <summary>
        /// List of open files.  We only have one record, but it has two openable parts, and
        /// each can be opened more than once.
        /// </summary>
        private List<OpenFileRec> mOpenFiles = new List<OpenFileRec>();


        /// <summary>
        /// Tests a stream to see if it is a MacBinary file.
        /// </summary>
        /// <param name="stream">Stream to test.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <returns>True if this looks like MacBinary.</returns>
        public static bool TestKind(Stream stream, AppHook appHook) {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Private constructor.
        /// </summary>
        private MacBinary(Stream? stream, AppHook appHook) {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Opens an existing archive.
        /// </summary>
        /// <param name="stream">Archive data stream.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <returns>Archive instance.</returns>
        /// <exception cref="NotSupportedException">Data stream is not in the expected
        ///   format.</exception>
        public static MacBinary OpenArchive(Stream stream, AppHook appHook) {
            stream.Position = 0;
            MacBinary archive = new MacBinary(stream, appHook);
            MacBinary_FileEntry newEntry = new MacBinary_FileEntry(archive);
            archive.RecordList.Add(newEntry);
            if (!newEntry.Scan()) {
                throw new NotSupportedException("Incompatible data stream");
            }
            return archive;
        }

        // IArchive
        public Stream? ReopenStream(Stream newStream) {
            throw new NotImplementedException();
        }

        // IDisposable generic Dispose() implementation.
        public void Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        protected virtual void Dispose(bool disposing) {
            if (disposing) {
                if (mOpenFiles.Count != 0) {
                    AppHook.LogW("MacBinary disposed while " + mOpenFiles.Count +
                        " files are open");
                    // Walk through from end to start so we don't trip when entries are removed.
                    for (int i = mOpenFiles.Count - 1; i >= 0; --i) {
                        // This will call back into our StreamClosing function, which will
                        // remove it from the list.
                        mOpenFiles[i].ReadStream.Close();
                    }
                }
            }
        }

        // IArchive
        public ArcReadStream OpenPart(IFileEntry entry, FilePart part) {
            throw new NotImplementedException();
        }

        // IArchiveExt
        public void StreamClosing(ArcReadStream stream) {
            throw new NotImplementedException();
        }

        // IArchive
        public void StartTransaction() {
            throw new InvalidOperationException("This archive format is read-only");
        }

        // IArchive
        public void CommitTransaction(Stream? outputStream) {
            throw new InvalidOperationException("Must start transaction first");
        }

        // IArchive
        public void CancelTransaction() { }

        // IArchive
        public IFileEntry CreateRecord() {
            throw new NotSupportedException("Additional records cannot be created");
        }

        // IArchive
        public void DeleteRecord(IFileEntry entry) {
            throw new NotSupportedException("The record cannot be deleted");
        }

        // IArchive
        public void AddPart(IFileEntry entry, FilePart part, IPartSource partSource,
                CompressionFormat compressFmt) {
            throw new InvalidOperationException("This archive format is read-only");
        }

        // IArchive
        public void DeletePart(IFileEntry entry, FilePart part) {
            throw new InvalidOperationException("This archive format is read-only");
        }
    }
}