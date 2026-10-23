/*
 * Copyright 2026 faddenSoft
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
using System.IO.Compression;

using CommonUtil;

namespace DiskArc.Comp {
    /// <summary>
    /// Streaming implementation of RLE 90 compression, as found in StuffIt and Arc.
    /// </summary>
    public class RLE90Stream : Stream {
        // Stream characteristics.
        public override bool CanRead {
            get { return mCompressionMode == CompressionMode.Decompress; }
        }
        public override bool CanWrite {
            get { return mCompressionMode == CompressionMode.Compress; }
        }
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        /// <summary>
        /// Compressed data is read from or written to this stream.
        /// </summary>
        private Stream mCompDataStream;

        /// <summary>
        /// Leave the compressed data stream open when we are disposed?
        /// </summary>
        private bool mLeaveOpen;

        /// <summary>
        /// To compress or not to compress?
        /// </summary>
        private CompressionMode mCompressionMode;

        //
        // Shared constants.
        //
        private const byte DELIM = 0x90;
        private const int MIN_RUN_LEN = 3;
        private const int MAX_RUN_LEN = 255;

        private long mExpInputRemaining;            // compressed data length, when expanding


        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="compDataStream">Stream that holds compressed data.  Must be positioned
        ///   at the start.  May be part of a larger stream (i.e. file archive).</param>
        /// <param name="mode">Flag that indicates whether we will be compressing or
        ///   expanding data.</param>
        /// <param name="leaveOpen">Flag that determines whether we close the compressed
        ///   data stream when we are disposed.</param>
        /// <param name="compDataStreamLen">Decompression: length of compressed data.</param>
        public RLE90Stream(Stream compDataStream, CompressionMode mode, bool leaveOpen,
                long compDataStreamLen) {
            mCompDataStream = compDataStream;
            mCompressionMode = mode;
            mLeaveOpen = leaveOpen;
            mExpInputRemaining = compDataStreamLen;

            if (mode == CompressionMode.Compress) {
                Debug.Assert(compDataStream.CanWrite);
                InitCompress();
            } else if (mode == CompressionMode.Decompress) {
                Debug.Assert(compDataStream.CanRead);
                if (compDataStreamLen < 0) {
                    throw new ArgumentException("Invalid value for " + nameof(compDataStreamLen));
                }
                InitExpand();
            } else {
                throw new ArgumentException("Invalid mode: " + mode);
            }
        }

        // IDisposable, from Stream
        protected override void Dispose(bool disposing) {
            if (disposing && mCompDataStream != null) {
                if (mCompressionMode == CompressionMode.Compress) {
                    FinishCompression();
                }

                if (!mLeaveOpen) {
                    mCompDataStream.Close();
                }

#pragma warning disable CS8625
                mCompDataStream = null;
#pragma warning restore CS8625
            }
            base.Dispose(disposing);
        }

        // Single-byte buffer for ReadByte/WriteByte.
        private byte[] mSingleBuf = new byte[1];

        // Stream
        public override int ReadByte() {
            if (Read(mSingleBuf, 0, 1) == 0) {
                return -1;      // EOF reached
            }
            return mSingleBuf[0];
        }

        // Stream
        public override void WriteByte(byte value) {
            mSingleBuf[0] = value;
            Write(mSingleBuf, 0, 1);
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

        #region Compress

        private int mCompRunCount;
        private byte mCompRunValue;

        /// <summary>
        /// Prepares object for compression.
        /// </summary>
        private void InitCompress() {
            mCompRunCount = 0;
            mCompRunValue = 0;
        }

        /// <summary>
        /// Receives uncompressed data to be compressed.  (Stream call.)
        /// </summary>
        public override void Write(byte[] buffer, int offset, int count) {
            if (count == 0) { return; }     // nothing to do

            while (count-- > 0) {
                byte val = buffer[offset++];
                if (mCompRunCount == 0) {
                    // Start of file, or immediately after outputting a run.
                    if (val == DELIM) {
                        mCompDataStream.WriteByte(DELIM);
                        mCompDataStream.WriteByte(0);
                    } else {
                        mCompRunValue = val;
                        mCompRunCount = 1;
                    }
                } else {
                    // We've seen a non-delimiter byte, and are looking for a run.
                    if (val == DELIM || val != mCompRunValue || mCompRunCount == MAX_RUN_LEN) {
                        // Output values we had accumulated.
                        if (mCompRunCount >= MIN_RUN_LEN) {
                            mCompDataStream.WriteByte(mCompRunValue);
                            mCompDataStream.WriteByte(DELIM);
                            mCompDataStream.WriteByte((byte)mCompRunCount);
                        } else {
                            // Just output a byte or two.
                            while (mCompRunCount-- != 0) {
                                mCompDataStream.WriteByte(mCompRunValue);
                            }
                        }
                        // Handle new value.
                        if (val == DELIM) {
                            mCompDataStream.WriteByte(DELIM);
                            mCompDataStream.WriteByte(0);
                            mCompRunCount = 0;
                        } else {
                            mCompRunValue = val;
                            mCompRunCount = 1;
                        }
                    } else {
                        // Building a run.
                        mCompRunCount++;
                    }
                }
            }
        }

        /// <summary>
        /// Finishes compression operation.  This method is called when this object is closed,
        /// indicating that all data has been provided.
        /// </summary>
        private void FinishCompression() {
            if (mCompRunCount >= MIN_RUN_LEN) {
                mCompDataStream.WriteByte(mCompRunValue);
                mCompDataStream.WriteByte(DELIM);
                mCompDataStream.WriteByte((byte)mCompRunCount);
            } else {
                // Just output a byte or two.
                while (mCompRunCount-- != 0) {
                    mCompDataStream.WriteByte(mCompRunValue);
                }
            }
        }

        #endregion Compress

        #region Expand

        private int mExpRunCount;
        private byte mExpRunValue;

        /// <summary>
        /// Prepares object for decompression.
        /// </summary>
        private void InitExpand() {
            mExpRunCount = 0;
            mExpRunValue = 0;
        }

        /// <summary>
        /// Reads uncompressed data.  (Stream call.)
        /// </summary>
        public override int Read(byte[] buffer, int offset, int count) {
            int startOffset = offset;
            int endOffset = offset + count;

            while (offset < endOffset) {
                if (mExpRunCount != 0) {
                    mExpRunCount--;
                    buffer[offset++] = mExpRunValue;
                } else {
                    int val = GetNext();
                    if (val < 0) {
                        break;
                    } else if (val != DELIM) {
                        mExpRunValue = buffer[offset++] = (byte)val;
                    } else {
                        int runCount = GetNext();
                        if (runCount == 0) {
                            // Special case to copy delimiter to output.
                            buffer[offset++] = DELIM;
                        } else if (runCount == 1) {
                            throw new InvalidDataException("Found run of length zero");
                        } else if (runCount < 0) {
                            throw new InvalidDataException("Hit EOF in the middle of a run");
                        } else {
                            mExpRunCount = runCount - 1;
                        }
                    }
                }
            }

            // Return number of bytes output to buffer
            return offset - startOffset;
        }

        /// <summary>
        /// Gets the next byte of input, returning -1 if we hit EOF.
        /// </summary>
        private int GetNext() {
            if (mExpInputRemaining == 0) {
                return -1;
            }
            mExpInputRemaining--;
            return mCompDataStream.ReadByte();
        }

        #endregion Expand
    }
}
