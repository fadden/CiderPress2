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
using System.Diagnostics;

using CommonUtil;
using static DiskArc.Defs;
using static DiskArc.FileAnalyzer.DiskLayoutEntry;
using static DiskArc.IFileSystem;

// TODO:
// - create AllocMap class in commonutil to track allocation bitmap
//   - simple bit vector based on 32-bit ints
//   - need FreeCount, FindFirstFree, MarkFree/MarkUsed
//   - makes VolumeAlloc calls

namespace DiskArc.FS {
    /// <summary>
    /// CP/M filesystem implementation.  Focus is on the Apple II implementation, which was
    /// based on CP/M v2.2.
    /// </summary>
    public class CPM : IFileSystem {
        private const string FILENAME_RULES =
            "8.3 format, using printable ASCII characters except for spaces and " +
                "\u201c<>.,;:=?*[]\u201d.";
        private static FSCharacteristics sCharacteristics = new FSCharacteristics(
            name: "CP/M",
            canWrite: true,
            isHierarchical: false,
            dirSep: IFileEntry.NO_DIR_SEP,
            hasResourceForks: false,
            fnSyntax: FILENAME_RULES,
            vnSyntax: string.Empty,
            tsStart: DateTime.MinValue,
            tsEnd: DateTime.MinValue
        );

        //
        // IFileSystem interfaces.
        //

        public FSCharacteristics Characteristics => throw new NotImplementedException();
        public static FSCharacteristics SCharacteristics => sCharacteristics;

        public Notes Notes { get; } = new Notes();

        public bool IsReadOnly { get { return ChunkAccess.IsReadOnly || IsDubious; } }

        public bool IsDubious { get; internal set; }

        public long FreeSpace => throw new NotImplementedException();

        public GatedChunkAccess RawAccess => throw new NotImplementedException();

        //
        // Implementation-specific.
        //

        /// <summary>
        /// Data source.  Contents may be shared in various ways.
        /// </summary>
        internal IChunkAccess ChunkAccess { get; private set; }

        internal AppHook AppHook { get; private set; }

        /// <summary>
        /// List of open files.
        /// </summary>
        private OpenFileTracker mOpenFiles = new OpenFileTracker();


        // Delegate: test image to see if it's ours.
        public static TestResult TestImage(IChunkAccess chunkSource, AppHook appHook) {
            throw new NotImplementedException();
        }

        // Delegate: returns true if the size (in bytes) is valid for this filesystem.
        public static bool IsSizeAllowed(long size) {
            throw new NotImplementedException();
        }

        public CPM(IChunkAccess chunks, AppHook appHook) {
            AppHook = appHook;
            throw new NotImplementedException();
        }

        public override string ToString() {
            return "TODO";
        }

        // IDisposable generic finalizer.
        ~CPM() {
            Dispose(false);
        }
        // IDisposable generic Dispose() implementation.
        public void Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        private bool mDisposed;
        protected virtual void Dispose(bool disposing) {
            if (mDisposed) {
                AppHook.LogW("Attempting to dispose of CPM object twice");
                return;
            }
            if (!disposing) {
                // This is a GC finalization.  We can't know if the objects we have references
                // to have already been finalized, so all we can do is complain.
                AppHook.LogW("GC disposing of filesystem object " + this);
                if (mOpenFiles.Count != 0) {
                    AppHook.LogW("CPM FS finalized while " + mOpenFiles.Count + " files open");
                }
                return;
            }

            // TODO

            RawAccess.AccessLevel = GatedChunkAccess.AccessLvl.Closed;
            mDisposed = true;
        }

        // IFileSystem
        public void Flush() {
            mOpenFiles.FlushAll();
            throw new NotImplementedException();
        }

        // IFileSystem
        public void PrepareFileAccess(bool doScan) {
            throw new NotImplementedException();
        }

        // IFileSystem
        public void PrepareRawAccess() {
            throw new NotImplementedException();
        }

        // IFileSystem
        public IMultiPart? FindEmbeddedVolumes() {
            return null;
        }

        // IFileSystem
        public void Format(string volumeName, int volumeNum, bool makeBootable) {
            throw new NotImplementedException();
        }

        private void CheckFileAccess(string op, IFileEntry ientry, bool wantWrite,
                FilePart part) {
            throw new NotImplementedException();
        }

        // IFileSystem
        public IFileEntry GetVolDirEntry() {
            throw new NotImplementedException();
        }

        // IFileSystem
        public DiskFileStream OpenFile(IFileEntry entry, FileAccessMode mode, FilePart part) {
            throw new NotImplementedException();
        }

        internal void CloseFile(DiskFileStream ifd) {
            throw new NotImplementedException();
        }

        // IFileSystem
        public void CloseAll() {
            throw new NotImplementedException();
        }

        // IFileSystem
        public IFileEntry CreateFile(IFileEntry dirEntry, string fileName, CreateMode mode) {
            throw new NotImplementedException();
        }

        // IFileSystem
        public void AddRsrcFork(IFileEntry entry) {
            throw new IOException("Filesystem does not support resource forks");
        }

        // IFileSystem
        public void MoveFile(IFileEntry entry, IFileEntry destDir, string newFileName) {
            throw new NotImplementedException();
        }

        // IFileSystem
        public void DeleteFile(IFileEntry entry) {
            throw new NotImplementedException();
        }
    }
}
