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

namespace DiskArc {
    /// <summary>
    /// Stream for performing I/O on a file stored in an emulated filesystem.  Most of the
    /// interface is defined by the Stream base class; see
    /// <see href="https://docs.microsoft.com/en-us/dotnet/api/system.io.stream"/>
    /// for full documentation.
    /// </summary>
    /// <remarks>
    /// <para>These are created through calls on the filesystem object.</para>
    /// <para>Calls that modify files do not cause the file modification date to be updated
    /// automatically.</para>
    /// <para>Modifications to the file may not be reflected in the file entry object
    /// until <see cref="Flush"/> or <see cref="Close"/> is called.</para>
    ///
    /// <para>Notes on differences vs. standard Stream behavior:</para>
    /// <list type="bullet">
    ///   <item>Write and SetLength calls may cause a <see cref="DiskFullException"/> to
    ///     be thrown.  In such a case, partially-completed writes are possible.</item>
    ///   <item>Extending a file's length with <see cref="SetLength"/> may or may not cause
    ///     storage to be allocated, depending on how the filesystem handles sparse files.
    ///     The additional space will be zeroed out.
    ///     Truncation of a fork may not cause storage to be released until the file
    ///     is closed.</item>
    ///   <item>The <see cref="Seek"/> call supports two additional origin values, for
    ///     working with sparse files: <see cref="Defs.SEEK_ORIGIN_DATA"/> and
    ///     <see cref="Defs.SEEK_ORIGIN_HOLE"/>.</item>
    /// </list>
    /// <para>Read calls should return the full amount of data requested, unless EOF is reached.
    /// This not guaranteed, but is the expected behavior of disk and memory streams.</para>
    /// </remarks>
    public abstract class DiskFileStream : Stream {
        // Single-byte buffer for ReadByte/WriteByte, allocated on first use.
        private byte[]? mSingleBuf;

        /// <summary>
        /// Returns the part of the file that is open (e.g. data or resource fork).
        /// </summary>
        public abstract Defs.FilePart Part { get; }

        /// <summary>
        /// Confirms that the file descriptor is valid, and is for the specified file entry,
        /// which is part of the specified filesystem.
        /// </summary>
        /// <param name="fs">Expected filesystem.</param>
        /// <param name="entry">Expected file entry.</param>
        /// <returns>True if all is well.</returns>
        public abstract bool DebugValidate(IFileSystem fs, IFileEntry entry);

        // Stream
        public override int ReadByte() {
            if (mSingleBuf == null) {
                mSingleBuf = new byte[1];
            }
            if (Read(mSingleBuf, 0, 1) == 0) {
                return -1;      // EOF reached
            }
            return mSingleBuf[0];
        }

        // Stream
        public override void WriteByte(byte value) {
            if (mSingleBuf == null) {
                mSingleBuf = new byte[1];
            }
            mSingleBuf[0] = value;
            Write(mSingleBuf, 0, 1);
        }

        // Note:
        //  - Stream.Dispose() calls Close()
        //  - Stream.Close() calls Dispose(true) and GC.SuppressFinalize(this)
        // Subclasses should implement Dispose(bool) and (maybe) a finalizer.

        /// <summary>
        /// Computes the combined length of the non-sparse sections of the file.
        /// </summary>
        /// <returns>Length, in bytes.</returns>
        public long ComputeNonSparseLength() {
            long origPosition = Position;
            long totalLength = 0;
            long dataStart = Seek(0, Defs.SEEK_ORIGIN_DATA);
            while (dataStart < Length) {
                long dataEnd = Seek(dataStart, Defs.SEEK_ORIGIN_HOLE);
                totalLength += dataEnd - dataStart;
                dataStart = Seek(dataEnd, Defs.SEEK_ORIGIN_DATA);
            }
            Position = origPosition;
            return totalLength;
        }
    }
}
