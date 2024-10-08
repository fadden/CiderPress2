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
using System.Text;

using CommonUtil;
using static DiskArc.Defs;

namespace DiskArc.Arc {
    /// <summary>
    /// One "file entry" in an audio recording.  This may wrap two blobs of data, e.g.
    /// Applesoft programs are preceded by a 3-byte chunk with the length of the next part.
    /// </summary>
    internal class AudioRecording_FileEntry : IFileEntry {
        //
        // IFileEntry interfaces.
        //

        public bool IsValid { get { return Archive != null; } }
        public bool IsDubious { get; private set; }
        public bool IsDamaged { get; private set; }

        public bool IsDirectory => false;

        public bool HasDataFork => true;

        public bool HasRsrcFork => false;

        public bool IsDiskImage => false;

        public IFileEntry ContainingDir => IFileEntry.NO_ENTRY;
        public int Count => 0;

        public string FileName {
            get => mFileName;
            set => throw new InvalidOperationException("Cannot modify object without transaction");
        }
        public char DirectorySeparatorChar {
            get => IFileEntry.NO_DIR_SEP;
            set { }
        }
        public byte[] RawFileName {
            get => Encoding.ASCII.GetBytes(mFileName);
            set => throw new InvalidOperationException("Cannot modify object without transaction");
        }

        public string FullPathName { get { return FileName; } }

        public bool HasProDOSTypes => true;
        public byte FileType {
            get => 0x00;    // TODO
            set => throw new InvalidOperationException("Cannot modify object without transaction");
        }
        public ushort AuxType {
            get => 0x1234;  // TODO
            set => throw new InvalidOperationException("Cannot modify object without transaction");
        }

        public bool HasHFSTypes => false;
        public uint HFSFileType { get => 0; set { } }
        public uint HFSCreator { get => 0; set { } }

        public byte Access {
            get => FileAttribs.FILE_ACCESS_UNLOCKED;
            set => throw new InvalidOperationException("Cannot modify object without transaction");
        }
        public DateTime CreateWhen { get => TimeStamp.NO_DATE; set { } }
        public DateTime ModWhen { get => TimeStamp.NO_DATE; set { } }

        public long StorageSize => 1234;    // TODO

        public long DataLength => 1234;     // TODO

        public long RsrcLength => 0;

        public string Comment { get => string.Empty; set { } }

        // Entries do not have children.
        private static readonly List<IFileEntry> sEmptyList = new List<IFileEntry>();
        public IEnumerator<IFileEntry> GetEnumerator() {
            return sEmptyList.GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() {
            return sEmptyList.GetEnumerator();
        }

        //
        // Implementation-specific fields.
        //

        private string mFileName = "TODO";      // TODO

        /// <summary>
        /// Reference to archive object.
        /// </summary>
        internal AudioRecording Archive { get; private set; }


        /// <summary>
        /// Private constructor.
        /// </summary>
        private AudioRecording_FileEntry(AudioRecording archive) {
            Archive = archive;
        }

        // IFileEntry
        public bool GetPartInfo(FilePart part, out long length, out long storageSize,
                out CompressionFormat format) {
            // TODO
            throw new NotImplementedException();
        }

        /// <summary>
        /// Creates an object for reading the contents of the entry out of the archive.
        /// </summary>
        internal ArcReadStream CreateReadStream(FilePart part) {
            Debug.Assert(Archive.DataStream != null);
            // TODO
            throw new NotImplementedException();
        }

        // IFileEntry
        public void SaveChanges() { }

        /// <summary>
        /// Invalidates the file entry object.  Called on the original set of entries after a
        /// successful commit.
        /// </summary>
        internal void Invalidate() {
            Debug.Assert(Archive != null); // harmless, but we shouldn't be invalidating twice
#pragma warning disable CS8625
            Archive = null;
#pragma warning restore CS8625
        }


        // IFileEntry
        public int CompareFileName(string fileName) {
            return string.Compare(mFileName, fileName, StringComparison.OrdinalIgnoreCase);
        }

        // IFileEntry
        public int CompareFileName(string fileName, char fileNameSeparator) {
            return PathName.ComparePathNames(mFileName, DirectorySeparatorChar, fileName,
                fileNameSeparator, PathName.CompareAlgorithm.OrdinalIgnoreCase);
        }
    }
}
