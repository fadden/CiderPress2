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
using System.Text.RegularExpressions;

using CommonUtil;
using static DiskArc.Defs;

namespace DiskArc.FS {
    public class RDOS_FileEntry : IFileEntryExt, IDisposable {
        //
        // IFileEntry interfaces.
        //

        public bool IsValid { get { return FileSystem != null; } }

        public bool IsDubious => throw new NotImplementedException();

        public bool IsDamaged => throw new NotImplementedException();

        public bool IsDirectory => throw new NotImplementedException();

        public bool HasDataFork => throw new NotImplementedException();
        public bool HasRsrcFork => throw new NotImplementedException();
        public bool IsDiskImage => throw new NotImplementedException();

        public IFileEntry ContainingDir { get; private set; }

        public int Count => throw new NotImplementedException();

        public string FileName { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public char DirectorySeparatorChar { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public string FullPathName => throw new NotImplementedException();
        public byte[] RawFileName { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

        public bool HasProDOSTypes => throw new NotImplementedException();

        public byte FileType { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public ushort AuxType { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

        public bool HasHFSTypes { get { return false; } }
        public uint HFSFileType { get { return 0; } set { } }
        public uint HFSCreator { get { return 0; } set { } }

        public byte Access { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public DateTime CreateWhen { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public DateTime ModWhen { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

        public long StorageSize => throw new NotImplementedException();

        public long DataLength => throw new NotImplementedException();

        public long RsrcLength => throw new NotImplementedException();

        public string Comment { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

        public bool GetPartInfo(FilePart part, out long length, out long storageSize, out CompressionFormat format) {
            throw new NotImplementedException();
        }

        public IEnumerator<IFileEntry> GetEnumerator() {
            throw new NotImplementedException();
        }
        IEnumerator IEnumerable.GetEnumerator() {
            throw new NotImplementedException();
        }

        private void CheckChangeAllowed() {
            throw new NotImplementedException();
        }

        //
        // Implementation-specific.
        //

        /// <summary>
        /// Reference to filesystem object.
        /// </summary>
        public RDOS FileSystem { get; private set; }

        /// <summary>
        /// True if this is the "fake" volume directory object.
        /// </summary>
        private bool IsVolumeDirectory { get; set; }

        /// <summary>
        /// List of files contained in this entry.  Only the volume dir has children.
        /// </summary>
        internal List<IFileEntry> ChildList { get; } = new List<IFileEntry>();

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="fileSystem">Filesystem this file entry is a part of.</param>
        internal RDOS_FileEntry(RDOS fileSystem) {
            ContainingDir = IFileEntry.NO_ENTRY;
            FileSystem = fileSystem;
        }

        // IDisposable generic finalizer.
        ~RDOS_FileEntry() {
            Dispose(false);
        }
        // IDisposable generic Dispose() implementation.
        public void Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        protected virtual void Dispose(bool disposing) {
            // nothing to do
        }

        /// <summary>
        /// Invalidates the file entry object.  This is called when the filesystem is switched
        /// to "raw" access mode, so that any objects retained by the application stop working.
        /// </summary>
        internal void Invalidate() {
            Debug.Assert(FileSystem != null); // harmless, but we shouldn't be invalidating twice
#pragma warning disable CS8625
            FileSystem = null;  // ensure that "is this part of our filesystem" check fails
#pragma warning restore CS8625
            ContainingDir = IFileEntry.NO_ENTRY;
            ChildList.Clear();
        }

        // IFileEntry
        public void SaveChanges() {
            throw new NotImplementedException();
        }

        // IFileEntry
        public void AddConflict(uint chunk, IFileEntry entry) {
            throw new NotImplementedException();
        }

        #region Filenames

        // IFileEntry
        public int CompareFileName(string fileName) {
            throw new NotImplementedException();
        }

        // IFileEntry
        public int CompareFileName(string fileName, char fileNameSeparator) {
            throw new NotImplementedException();
        }

        public static string AdjustFileName(string fileName) {
            throw new NotImplementedException();
        }

        #endregion Filenames

        public override string ToString() {
            return "TODO";
        }
    }
}
