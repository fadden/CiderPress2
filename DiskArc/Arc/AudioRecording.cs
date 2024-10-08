/*
 * Copyright 2024 faddenSoft
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
    /// Apple II audio cassette recording.
    /// </summary>
    public class AudioRecording : IArchiveExt {
        private const string FILENAME_RULES = "Auto-generated.";
        private static readonly ArcCharacteristics sCharacteristics = new ArcCharacteristics(
            name: "Audio Recording",
            canWrite: false,
            hasSingleEntry: false,
            hasResourceForks: false,
            hasDiskImages: false,
            hasArchiveComment: false,
            hasRecordComments: false,
            defaultDirSep: IFileEntry.NO_DIR_SEP,
            fnSyntax: FILENAME_RULES,
            tsStart: DateTime.MinValue,
            tsEnd: DateTime.MinValue
        );

        //
        // IArchive interfaces.
        //
        public ArcCharacteristics Characteristics => sCharacteristics;
        public static ArcCharacteristics SCharacteristics => sCharacteristics;

        public Stream? DataStream { get; internal set; }

        public bool IsValid { get; private set; } = true;
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

        internal WAVFile AudioData { get; private set; }

        /// <summary>
        /// Application hook reference.
        /// </summary>
        internal AppHook AppHook { get; private set; }

        /// <summary>
        /// List of records.
        /// </summary>
        internal List<AudioRecording_FileEntry> RecordList { get; private set; } =
            new List<AudioRecording_FileEntry>();

        /// <summary>
        /// Open stream tracker.
        /// </summary>
        private OpenStreamTracker mStreamTracker = new OpenStreamTracker();

        /// <summary>
        /// Tests a stream to see if it contains an AudioCassette "archive".
        /// </summary>
        /// <param name="stream">Stream to test.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <returns>True if this looks like a match.</returns>
        public static bool TestKind(Stream stream, AppHook appHook) {
            stream.Position = 0;
            WAVFile? wav = WAVFile.Prepare(stream);
            return (wav != null);
        }

        /// <summary>
        /// Private constructor.
        /// </summary>
        /// <param name="stream">Data stream if existing file, null if new archive.</param>
        /// <param name="appHook">Application hook reference.</param>
        private AudioRecording(Stream? stream, WAVFile wavFile, AppHook appHook) {
            DataStream = stream;
            AudioData = wavFile;
            AppHook = appHook;
        }

        /// <summary>
        /// Opens an existing archive.
        /// </summary>
        /// <param name="stream">Archive data stream.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <returns>Archive instance.</returns>
        /// <exception cref="NotSupportedException">Data stream is not compatible.</exception>
        public static AudioRecording OpenArchive(Stream stream, AppHook appHook) {
            stream.Position = 0;
            WAVFile? wav = WAVFile.Prepare(stream);
            if (wav == null) {
                throw new NotSupportedException("Not a supported WAV file format");
            }
            AudioRecording archive = new AudioRecording(stream, wav, appHook);
            List<CassetteDecoder.Chunk> chunks = CassetteDecoder.DecodeFile(wav,
                CassetteDecoder.Algorithm.Zero);
            // TODO - analyze contents
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
                // We're being disposed explicitly (not by the GC).  Dispose of all open entries,
                // so that attempts to continue to use them will start failing immediately.
                if (mStreamTracker.Count != 0) {
                    AppHook.LogW("AudioRecording disposed while " + mStreamTracker.Count +
                        " streams are open");
                    mStreamTracker.CloseAll();
                }
            }
            IsValid = false;
        }

        // IArchive
        public ArcReadStream OpenPart(IFileEntry ientry, FilePart part) {
            if (!IsValid) {
                throw new InvalidOperationException("Archive object is invalid");
            }
            if (ientry.IsDamaged) {
                throw new IOException("Entry is too damaged to open");
            }
            AudioRecording_FileEntry entry = (AudioRecording_FileEntry)ientry;
            if (entry.Archive != this) {
                throw new ArgumentException("Entry is not part of this archive");
            }
            ArcReadStream newStream = entry.CreateReadStream(part);
            mStreamTracker.Add(ientry, newStream);
            return newStream;
        }

        // IArchiveExt
        public void StreamClosing(ArcReadStream stream) {
            if (!mStreamTracker.RemoveDescriptor(stream)) {
                Debug.Assert(false, "Got StreamClosing for unknown stream");
                // continue on
            }
        }

        // IArchive
        public void StartTransaction() {
            throw new InvalidOperationException("This archive format is read-only");
        }

        // IArchive
        public void CommitTransaction(Stream? outputStream) {
            throw new InvalidOperationException("This archive format is read-only");
        }

        // IArchive
        public void CancelTransaction() { }

        // IArchive
        public IFileEntry CreateRecord() {
            throw new InvalidOperationException("This archive format is read-only");
        }

        // IArchive
        public void DeleteRecord(IFileEntry entry) {
            throw new InvalidOperationException("This archive format is read-only");
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
