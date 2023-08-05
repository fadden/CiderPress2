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
using static DiskArc.IFileSystem;

namespace DiskArc.FS {
    public class Pascal_FileDesc : DiskFileStream {
        // Stream
        public override bool CanRead => throw new NotImplementedException();

        public override bool CanSeek => throw new NotImplementedException();

        public override bool CanWrite => throw new NotImplementedException();

        public override long Length => throw new NotImplementedException();

        public override long Position { get => throw new NotImplementedException();
            set => throw new NotImplementedException(); }

        // DiskFileStream
        public override FilePart Part => throw new NotImplementedException();

        internal static Pascal_FileDesc CreateFD(Pascal_FileEntry entry, FileAccessMode mode,
                FilePart part, bool internalOpen) {
            throw new NotImplementedException();
        }

        // Stream
        public override int Read(byte[] buffer, int offset, int count) {
            throw new NotImplementedException();
        }

        // Stream
        public override void Write(byte[] buffer, int offset, int count) {
            throw new NotImplementedException();
        }

        // Stream
        public override long Seek(long offset, SeekOrigin origin) {
            throw new NotImplementedException();
        }

        // Stream
        public override void SetLength(long value) {
            throw new NotImplementedException();
        }

        // Stream
        public override void Flush() {
            throw new NotImplementedException();
        }

        // IDisposable generic finalizer.
        ~Pascal_FileDesc() {
            Dispose(false);
        }
        protected override void Dispose(bool disposing) {
            bool tryToSave = false;

            if (disposing) {
                // Explicit close, via Close() call or "using" statement.
                // TODO
            } else {
                // Finalizer dispose.
                // TODO
            }

            if (tryToSave) {
                // TODO
            }

            // Mark the fd invalid so future calls will crash or do nothing.
            Invalidate();
        }

        /// <summary>
        /// Marks the file descriptor invalid, clearing various fields to ensure something bad
        /// will happen if we try to use it.
        /// </summary>
        private void Invalidate() {
#pragma warning disable CS8625
            throw new NotImplementedException();
#pragma warning restore CS8625
        }

        public override string ToString() {
            return "TODO";
        }
    }
}
