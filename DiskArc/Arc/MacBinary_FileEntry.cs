/*
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
using System.Text;

using CommonUtil;
using static DiskArc.Defs;

namespace DiskArc.Arc {
    /// <summary>
    /// One record in a MacBinary file.
    /// </summary>
    public class MacBinary_FileEntry : IFileEntry {
        //
        // IFileEntry interfaces.
        //

        public bool IsValid { get { return Archive != null; } }

        public bool IsDubious { get; private set; }

        public bool IsDamaged { get; private set; }

        public bool IsDirectory => false;

        public bool HasDataFork => throw new NotImplementedException();
        public bool HasRsrcFork => throw new NotImplementedException();

        public bool IsDiskImage => false;

        public IFileEntry ContainingDir => IFileEntry.NO_ENTRY;
        public int Count => 0;

        public string FileName {
            get => throw new NotImplementedException();
            set => throw new NotImplementedException();
        }
        public char DirectorySeparatorChar {
            get => throw new NotImplementedException();
            set => throw new NotImplementedException();
        }
        public byte[] RawFileName {
            get => throw new NotImplementedException();
            set => throw new NotImplementedException();
        }

        public string FullPathName => throw new NotImplementedException();

        public bool HasProDOSTypes => throw new NotImplementedException();

        public byte FileType {
            get => throw new NotImplementedException();
            set => throw new NotImplementedException();
        }
        public ushort AuxType {
            get => throw new NotImplementedException();
            set => throw new NotImplementedException();
        }

        public bool HasHFSTypes => throw new NotImplementedException();

        public uint HFSFileType {
            get => throw new NotImplementedException();
            set => throw new NotImplementedException();
        }
        public uint HFSCreator {
            get => throw new NotImplementedException();
            set => throw new NotImplementedException();
        }
        public byte Access {
            get => throw new NotImplementedException();
            set => throw new NotImplementedException();
        }
        public DateTime CreateWhen {
            get => throw new NotImplementedException();
            set => throw new NotImplementedException();
        }
        public DateTime ModWhen {
            get => throw new NotImplementedException();
            set => throw new NotImplementedException();
        }

        public long StorageSize => throw new NotImplementedException();

        public long DataLength => throw new NotImplementedException();

        public long RsrcLength => throw new NotImplementedException();

        public string Comment {
            get => throw new NotImplementedException();
            set => throw new NotImplementedException();
        }

        private static readonly List<IFileEntry> sEmptyList = new List<IFileEntry>();
        public IEnumerator<IFileEntry> GetEnumerator() {
            return sEmptyList.GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() {
            return sEmptyList.GetEnumerator();
        }

        //
        // MacBinary-specific fields.
        //

        internal MacBinary Archive { get; private set; }

        private string mFileName = string.Empty;


        internal MacBinary_FileEntry(MacBinary archive) {
            Archive = archive;
        }

        // IFileEntry
        public bool GetPartInfo(FilePart part, out long length, out long storageSize,
                out CompressionFormat format) {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Scans the contents of the archive.
        /// </summary>
        /// <returns>True if parsing was successful, false if errors were found.</returns>
        internal bool Scan() {
            Stream stream = Archive.DataStream!;
            throw new NotImplementedException();
        }

        // IFileEntry
        public void SaveChanges() { /* not used for archives */ }

        // IFileEntry
        public int CompareFileName(string fileName) {
            return MacChar.CompareHFSFileNames(mFileName, fileName);
        }

        // IFileEntry
        public int CompareFileName(string fileName, char fileNameSeparator) {
            return CompareFileName(fileName);
        }
    }
}
