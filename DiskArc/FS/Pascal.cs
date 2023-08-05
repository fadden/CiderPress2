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

namespace DiskArc.FS {
    /// <summary>
    /// Filesystem used for the UCSD Pascal system on the Apple II.
    /// </summary>
    public class Pascal : IFileSystem {
        private const string FILENAME_RULES =
            "1-15 characters, must not include '$=?,[#:', spaces, or control characters.";
        private const string VOLNAME_RULES =
            "1-7 characters, must not include '$=?,[#:', spaces, or control characters.";
        private static FSCharacteristics sCharacteristics = new FSCharacteristics(
            name: "Apple Pascal",
            canWrite: true,
            isHierarchical: false,
            dirSep: IFileEntry.NO_DIR_SEP,
            hasResourceForks: false,
            fnSyntax: FILENAME_RULES,
            vnSyntax: VOLNAME_RULES,
            tsStart: DateTime.MinValue,
            tsEnd: DateTime.MinValue
        );

        //
        // IFileSystem interfaces.
        //

        public FSCharacteristics Characteristics => throw new NotImplementedException();
        public static FSCharacteristics SCharacteristics => sCharacteristics;

        public Notes Notes { get; } = new Notes();

        public bool IsReadOnly => throw new NotImplementedException();

        public bool IsDubious => throw new NotImplementedException();

        public long FreeSpace => throw new NotImplementedException();

        public GatedChunkAccess RawAccess => throw new NotImplementedException();

        //
        // Implementation-specific.
        //

        internal AppHook AppHook { get; private set; }

        // Delegate: test image to see if it's ours.
        public static TestResult TestImage(IChunkAccess chunkSource, AppHook appHook) {
            throw new NotImplementedException();
        }

        // Delegate: returns true if the size (in bytes) is valid for this filesystem.
        public static bool IsSizeAllowed(long size) {
            throw new NotImplementedException();
        }

        public Pascal(IChunkAccess chunks, AppHook appHook) {
            AppHook = appHook;
            throw new NotImplementedException();
        }

        public override string ToString() {
            return "TODO";
        }

        // IDisposable generic finalizer.
        ~Pascal() {
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
                AppHook.LogW("Attempting to dispose of Pascal object twice");
                return;
            }
            if (!disposing) {
                // This is a GC finalization.  We can't know if the objects we have references
                // to have already been finalized, so all we can do is complain.
                return;
            }

            RawAccess.AccessLevel = GatedChunkAccess.AccessLvl.Closed;
            mDisposed = true;
        }

        // IFileSystem
        public void Flush() {
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
