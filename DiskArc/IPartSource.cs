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
    /// <para>Deferred-open data source, used to provide a stream of data to an IArchive.  An
    /// instance of this class is created by the application and passed into the library.</para>
    /// </summary>
    /// <remarks>
    /// <para>When a file (or fork of a file) is added to an archive, the operation is queued
    /// up with one of these objects.  The actual data source does not need to be opened until
    /// the changes are committed.  This allows the addition of hundreds of files without
    /// needing to open all of them ahead of time, while keeping the act of opening the files
    /// outside of the library.</para>
    /// <para>Archive formats that allow compression will usually store a file in uncompressed
    /// form if the compression fails.  This means it may be necessary to read the input twice.
    /// The <see cref="Rewind"/> method will be called if compression fails.  If the
    /// transaction failed partway through and was restarted, it may be closed and reopened.</para>
    /// <para>Sources are Disposed after a transaction is successfully committed, or is
    /// cancelled.  Do not use the same source for multiple actions.</para>
    /// </remarks>
    //
    // This could be an abstract subclass of Stream, but the whole point of the class is to
    // provide a "delayed open", so the interface should reflect that.  It might make sense to
    // have Open() return a Stream, but that could get weird if Open() is called multiple times,
    // and Rewind() doesn't fit well if the stream can't otherwise be positioned.  Wrapping a
    // stream seems to make the most sense, since it gives the greatest freedom for Open/Rewind,
    // and Read is just pass-through.
    public interface IPartSource : IDisposable {
        /// <summary>
        /// Object to use for a nonexistent entry.
        /// </summary>
        static IPartSource NO_SOURCE = new NoPartSource();

        /// <summary>
        /// Prepares the stream for reading.  The stream will be positioned at the start.
        /// </summary>
        /// <exception cref="InvalidOperationException">Stream is already open.</exception>
        void Open();

        /// <summary>
        /// Reads data from the stream.
        /// </summary>
        /// <param name="buf">Buffer to read data into.</param>
        /// <param name="offset">Offset at which data will be read.</param>
        /// <param name="count">Maximum number of bytes to read.</param>
        /// <returns>Number of bytes read.</returns>
        int Read(byte[] buf, int offset, int count);

        /// <summary>
        /// Rewinds the stream to the beginning.  This will only be called if compression was
        /// unsuccessfully attempted, or the transaction failed and was restarted.
        /// </summary>
        void Rewind();

        /// <summary>
        /// Closes the stream to release resources.  The stream may be reopened if the transaction
        /// fails and is restarted, so this must not discard anything required for a subsequent
        /// Open().
        /// </summary>
        /// <remarks>
        /// <para>Closing a closed source has no effect.</para>
        /// </remarks>
        void Close();
    }

    /// <summary>
    /// Class for NO_SOURCE instance.
    /// </summary>
    internal class NoPartSource : IPartSource {
        public void Open() {
            throw new NotImplementedException();
        }
        public int Read(byte[] buf, int offset, int count) {
            throw new NotImplementedException();
        }
        public void Rewind() {
            throw new NotImplementedException();
        }
        public void Close() {
            throw new NotImplementedException();
        }
        public void Dispose() { }
    }
}
