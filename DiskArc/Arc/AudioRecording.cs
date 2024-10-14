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
            if (wav == null) {
                return false;
            }
            // Scan for the first good entry.  No need to process the entire file.
            CassetteDecoder.Algorithm alg = appHook.GetOptionEnum(DAAppHook.AUDIO_REC_ALG,
                CassetteDecoder.Algorithm.Zero);
            List<CassetteDecoder.Chunk> chunks = CassetteDecoder.DecodeFile(wav, alg, true);
            return chunks.Count != 0;
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
                throw new NotSupportedException("not a supported WAV file format");
            }
            AudioRecording archive = new AudioRecording(stream, wav, appHook);

            // Allow the default algorithm to be overridden.
            CassetteDecoder.Algorithm alg = appHook.GetOptionEnum(DAAppHook.AUDIO_REC_ALG,
                CassetteDecoder.Algorithm.Zero);
            List<CassetteDecoder.Chunk> chunks = CassetteDecoder.DecodeFile(wav, alg, false);
            if (chunks.Count == 0) {
                // Doesn't appear to be an Apple II recording.
                throw new NotSupportedException("no Apple II recordings found");
            }
            archive.GenerateRecords(chunks);
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

        /// <summary>
        /// Generates file records from the decoded audio data chunks.  Files will be treated
        /// as binary blobs unless we identify what looks like a paired entry.
        /// </summary>
        private void GenerateRecords(List<CassetteDecoder.Chunk> chunks) {
            // Generate info for all chunks, for debugging.
            for (int i = 0; i < chunks.Count; i++) {
                CassetteDecoder.Chunk chunk = chunks[i];
                Notes.AddI("#" + i + ": " + chunk.ToString());
            }

            for (int i = 0; i < chunks.Count; i++) {
                CassetteDecoder.Chunk chunk = chunks[i];
                byte fileType;
                if (i == chunks.Count - 1) {
                    // Last chunk.
                    fileType = FileAttribs.FILE_TYPE_NON;
                } else if (chunk.Data.Length == 2) {
                    // Could be followed by Integer BASIC program or Applesoft shape table.
                    ushort len = RawData.GetU16LE(chunk.Data, 0);
                    if (CheckINT(len, chunks[i + 1])) {
                        fileType = FileAttribs.FILE_TYPE_INT;
                    } else if (CheckShape(len, chunks[i + 1])) {
                        fileType = FileAttribs.FILE_TYPE_BIN;
                    } else {
                        fileType = FileAttribs.FILE_TYPE_NON;
                    }
                } else if (chunk.Data.Length == 3) {
                    // Could be followed by Applesoft BASIC program or stored array.
                    ushort len = RawData.GetU16LE(chunk.Data, 0);
                    byte xtra = chunk.Data[2];
                    if (CheckBAS(len, xtra, chunks[i + 1])) {
                        fileType = FileAttribs.FILE_TYPE_BAS;
                    } else {
                        // TODO: find or create examples of VAR tapes, and figure out how to
                        //   determine validity.
                        fileType = FileAttribs.FILE_TYPE_NON;
                    }
                } else {
                    fileType = FileAttribs.FILE_TYPE_NON;
                }
                ushort auxType;
                switch (fileType) {
                    case FileAttribs.FILE_TYPE_BIN:
                        auxType = 0x1000;
                        i++;
                        break;
                    case FileAttribs.FILE_TYPE_INT:
                        auxType = 0x0000;
                        i++;
                        break;
                    case FileAttribs.FILE_TYPE_BAS:
                        auxType = 0x0801;
                        i++;
                        break;
                    case FileAttribs.FILE_TYPE_VAR:
                        auxType = 0x0000;
                        i++;
                        break;
                    case FileAttribs.FILE_TYPE_NON:
                    default:
                        fileType = FileAttribs.FILE_TYPE_BIN;
                        auxType = 0x1000;
                        break;
                }
                AudioRecording_FileEntry newEntry = new AudioRecording_FileEntry(this,
                    "File" + RecordList.Count.ToString("D2"), fileType, auxType, chunks[i].Data);
                if (chunk.CalcChecksum != 0x00 || chunk.BadEnd) {
                    newEntry.IsDubious = true;
                }
                RecordList.Add(newEntry);
            }
        }

        /// <summary>
        /// Detects whether a chunk holds an Integer BASIC program.
        /// </summary>
        private static bool CheckINT(ushort len, CassetteDecoder.Chunk chunk) {
            Debug.WriteLine("CheckINT: decLen=" + len + " chunkLen=" + chunk.Data.Length);
            if (chunk.Data.Length < 4) {
                // Too short to be BASIC.
                return false;
            }

            // Check to see if the start looks like the first line of a BASIC program.
            byte lineLen = chunk.Data[0];
            int lineNum = RawData.GetU16LE(chunk.Data, 1);
            if (lineNum > 32767 || lineLen > chunk.Data.Length || chunk.Data[lineLen - 1] != 0x01) {
                // Doesn't look like INT.
                return false;
            }
            return true;
        }

        /// <summary>
        /// Detects whether a chunk holds an Applesoft shape table.
        /// This test is fairly weak, and should only be made after the INT test fails.
        /// </summary>
        private static bool CheckShape(ushort len, CassetteDecoder.Chunk chunk) {
            // Shape tables are difficult to identify.  Do a few simple tests.
            if (chunk.Data.Length < 2) {
                return false;
            }
            int shapeCount = chunk.Data[0];
            if (shapeCount == 0 || shapeCount * 2 >= chunk.Data.Length) {
                // Empty table, or offset table exceeds length of file.
                return false;
            }

            // It's not totally wrong, it has a two-byte header, it's not INT, and we're
            // going to call it BIN either way.  Declare success.
            return true;
        }

        /// <summary>
        /// Detects whether a chunk holds an Applesoft BASIC program.
        /// </summary>
        private static bool CheckBAS(ushort len, byte xtra, CassetteDecoder.Chunk chunk) {
            // Declared length in header chunk should be one shorter than actual length.
            Debug.WriteLine("CheckBAS: decLen=" + len + " chunkLen=" + chunk.Data.Length +
                 " xtra=$" + xtra.ToString("x2"));

            if (chunk.Data.Length < 4) {
                // Too short to be valid or useful.
                return false;
            }

            //
            // We do a quick test where we look at the first two lines of the program, and see
            // if they look coherent.
            //
            // Start with trivial checks on the next-address pointer and line number.
            //
            ushort nextAddr1 = RawData.GetU16LE(chunk.Data, 0);
            ushort lineNum1 = RawData.GetU16LE(chunk.Data, 2);
            if (nextAddr1 == 0 || lineNum1 > 63999) {
                // Empty or invalid.  Assume not BAS.
                return false;
            }
            // Find the end of the line.
            int idx;
            for (idx = 4; idx < chunk.Data.Length; idx++) {
                if (chunk.Data[idx] == 0x00) {
                    break;
                }
            }
            idx++;
            if (idx + 2 == chunk.Data.Length) {
                // At end of the file... is this a one-liner?
                return false;       // TODO - make a test case
            } else if (idx + 4 < chunk.Data.Length) {
                ushort nextAddr2 = RawData.GetU16LE(chunk.Data, idx);
                ushort lineNum2 = RawData.GetU16LE(chunk.Data, idx + 2);
                if (lineNum2 <= lineNum1 || lineNum2 > 63999) {
                    // Invalid line structure.
                    return false;
                }
                if (nextAddr2 <= nextAddr1) {
                    // Pointers moving backward.
                    return false;
                }
            }
            return true;
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
