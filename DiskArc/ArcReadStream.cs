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
using System.Diagnostics;


namespace DiskArc {
    /// <summary>
    /// Stream of data from an archive entry.  Used for data and resource file forks as well
    /// as archived disk images.  The source data may be compressed, so the stream may be read
    /// but not seeked or written, and does not provide a length.
    /// </summary>
    /// <remarks>
    /// <para>All IArchive implementations use this stream; there is no specialization.</para>
    ///
    /// <para>While the application is presumably capable of computing a checksum on the
    /// uncompressed data, it has no way of knowing which algorithm and seed value to use, and
    /// may not have access to the computed checksum value stored in the archive.  We handle this
    /// by having the archive-specific library code pass in a CRC-checking object, which has the
    /// appropriate function, starting value, and expected value stored within it.</para>
    ///
    /// <para>Applications can reasonably expect to be able to extract a file with a simple
    /// call like <see cref="Stream.CopyTo"/>, so we must throw an exception if the archive
    /// data is corrupted rather than rely on non-intrusive error reporting.  The integrity test
    /// only happens when EOF is reached, which won't happen "naturally" if the caller allocates
    /// a full-length buffer and reads the file in one call.  We need to take the uncompressed
    /// length as an argument so we know when we've seen all of it.  In some cases the uncompressed
    /// length isn't known, e.g. "squeezed" files in Binary II, but in those cases there is no
    /// external checksum to verify.</para>
    /// </remarks>
    public class ArcReadStream : Stream {
        public enum CheckResult {
            Unknown = 0,
            Failure,
            Success
        }

        /// <summary>
        /// True if a checksum has failed.  This will not be set until a Read() call
        /// determines that end-of-stream has been reached.
        /// </summary>
        /// <remarks>
        /// <para>This is only set for checksums stored in the archive.  Checksums embedded in
        /// the compressed data are handled by the compression codecs, which will throw an
        /// exception when bad data is discovered.  If no checker object is provided, this will
        /// remain set to "unknown".</para>
        /// <para>Examining this is not normally required, as a failed check results in an
        /// exception.  This can be useful in regression tests, to verify that this class is
        /// actually testing the checksum.</para>
        /// </remarks>
        public CheckResult ChkResult { get; private set; } = CheckResult.Unknown;

        // Stream interfaces.  Readable, but not writable or seekable.
        public override bool CanRead => !mDisposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        /// <summary>
        /// Archive we're reading from.  Needed for stream-close notifications.
        /// </summary>
        private IArchiveExt mArchive;

        /// <summary>
        /// (Optional) compressed data source.
        /// </summary>
        private Stream? mCompDataStream;

        /// <summary>
        /// Our current position within the archive.  We can't rely on the archive's file position
        /// across calls because the application might be working with multiple file entries
        /// simultaneously, so the archive stream could get seeked between Read calls.
        /// </summary>
        private long mArcPosition;

        /// <summary>
        /// Optional checksum verification object.
        /// </summary>
        private Checker? mChecker;

        /// <summary>
        /// Length of uncompressed data, if known; else -1.  Must be set when reading uncompressed
        /// data.
        /// </summary>
        private long mLength;

        /// <summary>
        /// Total number of bytes written so far.
        /// </summary>
        private long mBytesWritten;

        /// <summary>
        /// Set when the object has been disposed.
        /// </summary>
        private bool mDisposed = false;


        /// <summary>
        /// Constructor for uncompressed data, read directly from the archive stream.  The
        /// archive stream must be positioned at the start of the data.
        /// </summary>
        /// <param name="archiveExt">Archive reference.</param>
        /// <param name="length">Length of file data.</param>
        /// <param name="checker">Optional checksum verifier.</param>
        internal ArcReadStream(IArchiveExt archiveExt, long length, Checker? checker) {
            if (length < 0) {
                throw new ArgumentOutOfRangeException("Bad length (" + length + ")");
            }

            mArchive = archiveExt;
            mLength = length;
            mChecker = checker;

            Debug.Assert(archiveExt.DataStream != null);
            Stream archiveStream = archiveExt.DataStream;
            Debug.Assert(archiveStream.CanRead && archiveStream.CanSeek);
            mArcPosition = archiveStream.Position;
            mBytesWritten = 0;
        }

        /// <summary>
        /// Constructor for data that must be decompressed.  The archive stream must be
        /// positioned at the start of the compressed data.
        /// </summary>
        /// <param name="archiveExt">Archive reference.</param>
        /// <param name="expandedLength">Length of uncompressed data, or -1 if not known.</param>
        /// <param name="checker">Optional checksum verifier.  If not null, a non-negative value
        ///   must be provided for "expandedLength".</param>
        /// <param name="compDataStream">Stream that expands compressed data.</param>
        internal ArcReadStream(IArchiveExt archiveExt, long expandedLength, Checker? checker,
                Stream compDataStream) :
                this(archiveExt, 0, checker) {      // don't pass length, might be -1
            if (expandedLength < 0 && checker != null) {
                throw new ArgumentOutOfRangeException("Checksum object requires length");
            }
            mLength = expandedLength;
            mCompDataStream = compDataStream;
        }

        // Note:
        //  - Stream.Dispose() calls Close()
        //  - Stream.Close() calls Dispose(true) and GC.SuppressFinalize(this)

        ~ArcReadStream() {
            Dispose(false);
        }
        // IDisposable
        protected override void Dispose(bool disposing) {
            Debug.Assert(!mDisposed, "ArcReadStream double dispose");

            // If not being GCed, notify the archive.  (If we are being GCed, the archive
            // probably isn't there anymore.)
            if (disposing) {
                mArchive.StreamClosing(this);
            } else {
                Debug.Assert(false, "ArcReadStream disposed by GC");
            }
            mDisposed = true;
        }

        // Stream
        public override int Read(byte[] buffer, int offset, int count) {
            Debug.Assert(mArchive.DataStream != null);

            // Set the archive file position in case it gets seeked between calls here.  This is
            // needed for both uncompressed and compressed reads, because the archive stream
            // underlies the compressed data stream.  (And if it doesn't, no harm done.)
            mArchive.DataStream.Position = mArcPosition;

            int actual;
            if (mCompDataStream == null) {
                // If we're not reading through a compression codec, we need to provide a "fake"
                // end of file.
                if (mBytesWritten + count > mLength) {
                    count = (int)(mLength - mBytesWritten);
                }
                actual = mArchive.DataStream.Read(buffer, offset, count);
            } else {
                actual = mCompDataStream.Read(buffer, offset, count);
            }
            mBytesWritten += actual;

            // Remember the position where we ended up in the archive stream.
            mArcPosition = mArchive.DataStream.Position;

            if (mChecker != null) {
                mChecker.Update(buffer, offset, actual);
                if (actual == 0 || mBytesWritten == mLength) {
                    // Reached end of stream.  Test the CRC.
                    if (mChecker.IsMatch) {
                        ChkResult = CheckResult.Success;
                    } else {
                        ChkResult = CheckResult.Failure;
                        throw new InvalidDataException("Corrupted data detected");
                    }
                }
            }
            return actual;
        }

        // Single-byte buffer for ReadByte/WriteByte, allocated on first use.
        private byte[]? mSingleBuf;

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
        public override void Flush() {
            throw new NotSupportedException();
        }

        // Stream
        public override long Seek(long offset, SeekOrigin origin) {
            throw new NotSupportedException();
        }

        // Stream
        public override void SetLength(long value) {
            throw new NotSupportedException();
        }

        // Stream
        public override void Write(byte[] buffer, int offset, int count) {
            throw new NotSupportedException();
        }
    }
}
