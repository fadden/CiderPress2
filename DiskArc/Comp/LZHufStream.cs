/*
 * Copyright 2025 faddenSoft
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
    /// <para>Streaming implementation of LZHUF compression algorithm.  This is a direct port of
    /// LZHUF.C, with the original names mostly intact, modified to work with streaming I/O.  The
    /// output generated in the default configuration is identical to the original.</para>
    /// <para>By default, compressed output starts with the uncompressed file length, which can't
    /// be known until end-of-stream is reached.  As a result, all compression output must be
    /// cached in memory unless this is disabled.</para>
    /// </summary>
    /// <remarks>
    /// <para>The original LZHUF.C source file does not work on modern systems because parts of
    /// it quietly assume 16-bit integers.  Notably, some of the Huffman tree management will
    /// break in strange ways, and there are two memmove() calls that should use sizeof() instead
    /// of a fixed width value.</para>
    /// </remarks>
    public class LZHufStream : Stream {
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

        /// <summary>
        /// Include/expect 32-bit length at start of compressed data?
        /// </summary>
        private bool mIncludeLen;

        /// <summary>
        /// Length of data we're expecting to output when expanding.
        /// </summary>
        private long mExpOutputLen;

        /// <summary>
        /// Window initialization value.
        /// </summary>
        private byte mInitVal;

        //
        // Common constants and storage.
        //
        private const int N = 4096;                 // window buffer size
        private const int F = 60;                   // lookahead buffer size / max match
        private const int THRESHOLD = 2;            // min length to be a valid match
        private const int NIL = N;                  // leaf of tree

        private const int N_CHAR = 256 - THRESHOLD + F; // literals plus match lengths
        private const int T = N_CHAR * 2 - 1;       // size of table
        private const int R = T - 1;                // position of root
        private const int MAX_FREQ = 0x8000;        // update tree when root freq equals this

        private byte[] mTextBuf = new byte[N + F - 1];  // buffer + doubled-up wrap area

        // Adaptive Huffman tables.
        private ushort[] mFreq = new ushort[T + 1]; // frequency table
        private ushort[] mPrnt = new ushort[T + N_CHAR]; // pointers to parent nodes, except for the
                                            // elements [T..T + N_CHAR - 1] which are used to get
                                            // the positions of leaves corresponding to the codes.
        private ushort[] mSon = new ushort[T];      // pointers to child nodes (son[], son[] + 1)


        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="compDataStream">Stream that holds compressed data.  Must be positioned
        ///   at the start.  May be part of a larger stream (i.e. file archive).</param>
        /// <param name="mode">Flag that indicates whether we will be compressing or
        ///   expanding data.</param>
        /// <param name="leaveOpen">Flag that determines whether we close the compressed
        ///   data stream when we are disposed.</param>
        /// <param name="includeLen">Flag that indicates whether the uncompressed data length
        ///   is stored in the first 4 bytes of the compressed data stream.</param>
        /// <param name="initVal">Value to use when initializing window, normally 0x20.</param>
        /// <param name="origDataLen">Decompression: length of uncompressed data when includeLen
        ///   is true; -1 otherwise.</param>
        public LZHufStream(Stream compDataStream, CompressionMode mode, bool leaveOpen,
                bool includeLen, byte initVal = 0x20, long origDataLen = -1) {
            mCompDataStream = compDataStream;
            mCompressionMode = mode;
            mLeaveOpen = leaveOpen;
            mIncludeLen = includeLen;
            mInitVal = initVal;
            mExpOutputLen = origDataLen;

            if (mode == CompressionMode.Compress) {
                Debug.Assert(compDataStream.CanWrite);
                InitCompress();
            } else if (mode == CompressionMode.Decompress) {
                Debug.Assert(compDataStream.CanRead);
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

        /// <summary>
        /// Initialization of tree.
        /// </summary>
        private void StartHuff() {
            ushort i;
            for (i = 0; i < N_CHAR; i++) {
                mFreq[i] = 1;
                mSon[i] = (ushort)(i + T);
                mPrnt[i + T] = i;
            }
            i = 0;
            ushort j = N_CHAR;
            while (j <= R) {
                mFreq[j] = (ushort)(mFreq[i] + mFreq[i + 1]);
                mSon[j] = i;
                mPrnt[i] = mPrnt[i + 1] = j;
                i += 2;
                j++;
            }
            mFreq[T] = 0xffff;
            mPrnt[R] = 0;
        }

        /// <summary>
        /// Reconstruction of tree.
        /// </summary>
        /// <remarks>
        /// This is called when the 16-bit frequency counter overflows.
        /// </remarks>
        private void reconst() {
            int i, j;

            // Collect leaf nodes in the first half of the table
            // and replace the freq by (freq + 1) / 2.
            j = 0;
            for (i = 0; i < T; i++) {
                if (mSon[i] >= T) {
                    mFreq[j] = (ushort)((mFreq[i] + 1) / 2);
                    mSon[j] = mSon[i];
                    j++;
                }
            }
            // Begin constructing tree by connecting sons.
            for (i = 0, j = N_CHAR; j < T; i += 2, j++) {
                int k = i + 1;
                ushort f = mFreq[j] = (ushort)(mFreq[i] + mFreq[k]);
                for (k = j - 1; f < mFreq[k]; k--)
                    ;
                k++;
                int l = j - k;
                Array.Copy(mFreq, k, mFreq, k + 1, l);
                mFreq[k] = f;
                Array.Copy(mSon, k, mSon, k + 1, l);
                mSon[k] = (ushort)i;
            }
            // Connect prnt.
            for (i = 0; i < T; i++) {
                int k;
                if ((k = mSon[i]) >= T) {
                    mPrnt[k] = (ushort)i;
                } else {
                    mPrnt[k] = mPrnt[k + 1] = (ushort)i;
                }
            }
        }

        /// <summary>
        /// Increment frequency of given code by one, and update tree.
        /// </summary>
        private void update(ushort c) {
            if (mFreq[R] == MAX_FREQ) {
                reconst();
            }
            c = mPrnt[c + T];
            do {
                ushort k = ++mFreq[c];

                // If the order is disturbed, exchange nodes.
                ushort l = (ushort)(c + 1);
                if (k > mFreq[l]) {
                    while (k > mFreq[++l])
                        ;
                    l--;
                    mFreq[c] = mFreq[l];
                    mFreq[l] = k;

                    ushort i = mSon[c];
                    mPrnt[i] = l;
                    if (i < T) {
                        mPrnt[i + 1] = l;
                    }
                    ushort j = mSon[l];
                    mSon[l] = i;

                    mPrnt[j] = c;
                    if (j < T) {
                        mPrnt[j + 1] = c;
                    }
                    mSon[c] = j;

                    c = l;
                }
            } while ((c = mPrnt[c]) != 0);      // repeat up to root
        }

        #region Compress

        // Binary tree of input strings.  We want to have an entry for every possible string,
        // so there's one node per byte in the window.  The tree has 256 heads, one per possible
        // byte value, which are stored at the end of the right-son array.
        private int[] mLson = new int[N + 1];
        private int[] mRson = new int[N + 257];
        private int[] mDad = new int[N + 1];

        private enum CompState { Unknown = 0, Initializing, CheckMatch, LoadBytes }
        private CompState mCompState;

        private ushort mCompPutbuf;
        private int mCompPutlen;
        private int mCompMatchPosition;
        private int mCompMatchLength;

        // Variables pulled out of the top-level Encode function.
        private int mCompS;
        private int mCompR;
        private int mCompLen;
        private int mCompLastMatchLength;

        private int mCompBytesLoaded;

        private MemoryStream? mCompTmpStream;
        private Stream? mCompOutStream;
        private long mCompInputLength;


        /// <summary>
        /// Prepares object for compression.
        /// </summary>
        private void InitCompress() {
            InitTree();
            StartHuff();

            mCompPutbuf = 0;
            mCompPutlen = 0;
            mCompS = 0;
            mCompR = N - F;
            mCompLen = 0;
            mCompLastMatchLength = 0;

            for (int i = mCompS; i < mCompR; i++) {
                mTextBuf[i] = mInitVal;     // usually ' '
            }

            mCompState = CompState.Initializing;
            mCompInputLength = 0;
            if (mIncludeLen) {
                mCompTmpStream = new MemoryStream();
                mCompOutStream = mCompTmpStream;
            } else {
                mCompOutStream = mCompDataStream;
            }
        }

        /// <summary>
        /// Receives uncompressed data to be compressed.  (Stream call.)
        /// </summary>
        public override void Write(byte[] buffer, int offset, int count) {
            if (count == 0) {
                return;                     // nothing to do
            }

            mCompInputLength += count;
            DoEncode(buffer, offset, count, false);
        }

        /// <summary>
        /// Finishes compression operation.  This method is called when this object is closed,
        /// indicating that all data has been provided.
        /// </summary>
        private void FinishCompression() {
            DoEncode(RawData.EMPTY_BYTE_ARRAY, 0, 0, true);
            EncodeEnd();

            if (mIncludeLen) {
                if (mCompInputLength != (int)mCompInputLength) {
                    throw new InvalidDataException("too long for LZHuf");
                }
                mCompDataStream.WriteByte((byte)mCompInputLength);
                mCompDataStream.WriteByte((byte)(mCompInputLength >> 8));
                mCompDataStream.WriteByte((byte)(mCompInputLength >> 16));
                mCompDataStream.WriteByte((byte)(mCompInputLength >> 24));
                //Debug.WriteLine("LZHuf copying " + mCompInputLength + " bytes to output stream");
                mCompOutStream!.Position = 0;
                mCompOutStream.CopyTo(mCompDataStream);
            }
        }

        /// <summary>
        /// Main compression function.
        /// </summary>
        private void DoEncode(byte[] buffer, int offset, int count, bool finish) {
            // Special case for empty file.
            if (finish && mCompInputLength == 0) {
                return;
            }

            if (mCompState == CompState.Initializing) {
                // Start by reading F bytes into the window, at the end of the buffer.
                //   r = N - F;
                //   for (len = 0; len < F && (c = getc(infile)) != EOF; len++)
                //       text_buf[r + len] = c;
                // At the end, "len" holds the number of bytes we've read.

                while (mCompLen < F && count > 0) {
                    mTextBuf[mCompR + mCompLen++] = buffer[offset++];
                    count--;
                }
                if (mCompLen < F && !finish) {
                    // Wait for more input before producing output.
                    return;
                }

                // Add all of the strings that start with those bytes into the tree.  Do this
                // even if we have fewer than F bytes in the file, as it's okay to match against
                // the buffer fill.  This is useful because LZSS-type algorithms can match against
                // overlapping runs immediately.
                for (int i = 1; i <= F; i++) {
                    InsertNode(mCompR - i);
                }
                // Insert the string for the first byte in the file.  Note this sets the match
                // length and position values.
                InsertNode(mCompR);

                mCompState = CompState.CheckMatch;
            }

            // The original implementation read data as needed in a nested loop.  We need to turn
            // it inside-out, feeding bytes as they arrive.
            //
            // mCompLen holds the number of bytes we've read but haven't yet processed.  It will
            // usually be F (60), so that we can test for a maximum-length match, but will shrink
            // as we approach the end of the file.

            while (count > 0 || finish) {
                if (mCompState == CompState.CheckMatch) {
                    if (mCompMatchLength > mCompLen) {
                        mCompMatchLength = mCompLen;      // should only happen after EOF is seen
                    }
                    if (mCompMatchLength <= THRESHOLD) {
                        mCompMatchLength = 1;
                        EncodeChar(mTextBuf[mCompR]);
                    } else {
                        EncodeChar((ushort)(255 - THRESHOLD + mCompMatchLength));
                        EncodePosition((ushort)mCompMatchPosition);
                    }

                    mCompLastMatchLength = mCompMatchLength;
                    mCompBytesLoaded = 0;       // takes place of "i" in original
                    mCompState = CompState.LoadBytes;
                }
                Debug.Assert(mCompState == CompState.LoadBytes);

                // The last match (or literal) advanced one or more bytes.  We need to load that
                // many bytes into the buffer and insert the strings into the tree.
                if (!finish && mCompBytesLoaded < mCompLastMatchLength) {
                    Debug.Assert(count > 0);
                    byte c = buffer[offset++];
                    count--;
                    mCompBytesLoaded++;         // i++
                    DeleteNode(mCompS);
                    mTextBuf[mCompS] = c;
                    if (mCompS < F - 1) {
                        // Duplicate the start of the buffer at the end, so we can do string
                        // comparisons without having to mod the ring buffer index every time.
                        mTextBuf[mCompS + N] = c;
                    }
                    mCompS = (mCompS + 1) & (N - 1);
                    mCompR = (mCompR + 1) & (N - 1);
                    InsertNode(mCompR);
                }
                if (mCompBytesLoaded == mCompLastMatchLength) {
                    // We loaded everything we need.  Process the next match.
                    mCompState = CompState.CheckMatch;
                } else if (finish) {
                    // We don't have more data to add to the tree, but we still need to remove
                    // the entries that have fallen out of the window, and we need to process
                    // the last few bytes of data.  We also need to shrink the maximum
                    // match len.
                    //Debug.Assert(mCompLastMatchLength - mCompBytesLoaded >= mCompLen);
                    while (mCompBytesLoaded++ < mCompLastMatchLength) {
                        Debug.Assert(mCompLen > 0);
                        DeleteNode(mCompS);
                        mCompS = (mCompS + 1) & (N - 1);
                        mCompR = (mCompR + 1) & (N - 1);
                        if (--mCompLen > 0) {
                            InsertNode(mCompR);
                        }
                    }
                    if (mCompLen <= 0) {
                        // All done with file.
                        break;
                    }
                    mCompState = CompState.CheckMatch;
                }

                if (count == 0 && !finish) {
                    // All data in buffer has been processed.  Return to caller.
                    break;
                }
            }
        }

        /// <summary>
        /// Initialize trees.
        /// </summary>
        private void InitTree() {
            for (int i = N + 1; i <= N + 256; i++) {
                mRson[i] = NIL;     // root
            }
            for (int i = 0; i < N; i++) {
                mDad[i] = NIL;      // node
            }
        }

        /// <summary>
        /// Insert to tree.
        /// </summary>
        /// <remarks>
        /// <para>This adds or replaces a node in the binary tree of strings.  The string in
        /// question starts at window position "r".  While adding the string we need to compare
        /// the values in the tree nodes, so as part of doing that we remember the longest match
        /// we found.</para>
        /// <para>This sets mMatchLength and mMatchPosition to the best match found.  The match
        /// length may be zero.</para>
        /// </remarks>
        private void InsertNode(int r) {
            int cmp = 1;                        // start with right side
            int key = r;
            int p = N + 1 + mTextBuf[key];      // starting position, based on first byte
            mRson[r] = mLson[r] = NIL;          // reset pointers in this entry
            mCompMatchLength = 0;
            while (true) {
                if (cmp >= 0) {
                    if (mRson[p] != NIL) {
                        p = mRson[p];           // move here
                    } else {
                        mRson[p] = r;           // found empty space, add us as right child
                        mDad[r] = p;
                        return;
                    }
                } else {
                    if (mLson[p] != NIL) {
                        p = mLson[p];           // move here
                    } else {
                        mLson[p] = r;           // found empty space, add us as left child
                        mDad[r] = p;
                        return;
                    }
                }
                int i;
                for (i = 1; i < F; i++) {
                    // We don't need to worry about wrap-around because of the redundant end.
                    cmp = mTextBuf[key + i] - mTextBuf[p + i];
                    if (cmp != 0) {
                        break;
                    }
                }
                if (i > THRESHOLD) {
                    // Match is long enough to be useful.
                    if (i > mCompMatchLength) {
                        // This is the longest match.
                        mCompMatchPosition = ((r - p) & (N - 1)) - 1;
                        mCompMatchLength = i;
                        if (mCompMatchLength >= F) {
                            // Max-length match.  The new node is equivalent to the existing node,
                            // so there's no point in wasting time chasing farther down into the
                            // binary tree.
                            // Break out of the while loop.
                            break;
                        }
                    }
                    if (i == mCompMatchLength) {
                        // This match has the same length.  Update the match position if it's more
                        // favorable (smaller offsets are stored in fewer bits).
                        int c = ((r - p) & (N - 1)) - 1;
                        if (c < mCompMatchPosition) {
                            mCompMatchPosition = c;
                        }
                    }
                }
            }

            // Insert the node, replacing existing entry 'p' with our node 'r'.  We only do
            // this when 'p' is a maximum-length match for 'r'.
            mDad[r] = mDad[p];
            mLson[r] = mLson[p];
            mRson[r] = mRson[p];
            mDad[mLson[p]] = r;
            mDad[mRson[p]] = r;
            if (mRson[mDad[p]] == p) {
                mRson[mDad[p]] = r;
            } else {
                mLson[mDad[p]] = r;
            }
            mDad[p] = NIL;      // remove p
        }

        /// <summary>
        /// Remove from tree.
        /// </summary>
        private void DeleteNode(int p) {
            if (mDad[p] == NIL) {
                return;         // not registered
            }
            int q;
            if (mRson[p] == NIL) {
                q = mLson[p];
            } else if (mLson[p] == NIL) {
                q = mRson[p];
            } else {
                q = mLson[p];
                if (mRson[q] != NIL) {
                    do {
                        q = mRson[q];
                    } while (mRson[q] != NIL);
                    mRson[mDad[q]] = mLson[q];
                    mDad[mLson[q]] = mDad[q];
                    mLson[q] = mLson[p];
                    mDad[mLson[p]] = q;
                }
                mRson[q] = mRson[p];
                mDad[mRson[p]] = q;
            }
            mDad[q] = mDad[p];
            if (mRson[mDad[p]] == p) {
                mRson[mDad[p]] = q;
            } else {
                mLson[mDad[p]] = q;
            }
            mDad[p] = NIL;
        }

        // Tables for encoding the upper 6 bits of position.
        private static readonly byte[] P_LEN = new byte[64] {
            0x03, 0x04, 0x04, 0x04, 0x05, 0x05, 0x05, 0x05,
            0x05, 0x05, 0x05, 0x05, 0x06, 0x06, 0x06, 0x06,
            0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06,
            0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07,
            0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07,
            0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07,
            0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08,
            0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08
        };

        private static readonly byte[] P_CODE = new byte[64] {
            0x00, 0x20, 0x30, 0x40, 0x50, 0x58, 0x60, 0x68,
            0x70, 0x78, 0x80, 0x88, 0x90, 0x94, 0x98, 0x9C,
            0xA0, 0xA4, 0xA8, 0xAC, 0xB0, 0xB4, 0xB8, 0xBC,
            0xC0, 0xC2, 0xC4, 0xC6, 0xC8, 0xCA, 0xCC, 0xCE,
            0xD0, 0xD2, 0xD4, 0xD6, 0xD8, 0xDA, 0xDC, 0xDE,
            0xE0, 0xE2, 0xE4, 0xE6, 0xE8, 0xEA, 0xEC, 0xEE,
            0xF0, 0xF1, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7,
            0xF8, 0xF9, 0xFA, 0xFB, 0xFC, 0xFD, 0xFE, 0xFF
        };

        /// <summary>
        /// Encodes a character literal or a match length.
        /// </summary>
        private void EncodeChar(ushort c) {
            ushort i = 0;
            int j = 0;
            int k = mPrnt[c + T];

            // Travel from leaf to root.
            do {
                i >>= 1;

                // If node's address is odd-numbered, choose bigger brother node.
                if ((k & 1) != 0) {
                    i += 0x8000;
                }
                j++;
            } while ((k = mPrnt[k]) != R);
            Putcode(j, i);
            update(c);
        }

        /// <summary>
        /// Encodes a match position.
        /// </summary>
        private void EncodePosition(ushort c) {
            // Output upper 6 bits by table lookup.
            ushort i = (ushort)(c >> 6);
            Putcode(P_LEN[i], (ushort)(P_CODE[i] << 8));

            // Output lower 6 bits verbatim.
            Putcode(6, (ushort)((c & 0x3f) << 10));
        }

        /// <summary>
        /// Flush pending bits.
        /// </summary>
        private void EncodeEnd() {
            if (mCompPutlen != 0) {
                mCompOutStream!.WriteByte((byte)(mCompPutbuf >> 8));
            }
        }

        /// <summary>
        /// Output c bits of code.
        /// </summary>
        private void Putcode(int l, ushort c) {
            mCompPutbuf |= (ushort)(c >> mCompPutlen);
            if ((mCompPutlen += l) >= 8) {
                mCompOutStream!.WriteByte((byte)(mCompPutbuf >> 8));
                if ((mCompPutlen -= 8) >= 8) {
                    mCompOutStream.WriteByte((byte)mCompPutbuf);
                    mCompPutlen -= 8;
                    mCompPutbuf = (ushort)(c << (l - mCompPutlen));
                } else {
                    mCompPutbuf <<= 8;
                }
            }
        }

        #endregion Compress

        #region Expand

        private enum ExpState { Unknown = 0, GetNext, InMatch }
        private ExpState mExpState;

        private int mExpGetlen;
        private ushort mExpGetbuf;
        private int mExpMatchPosition;
        private int mExpMatchLength;

        // Variables pulled out of the top-level Decode function.
        private int mExpR;

        private long mExpDataOut;


        /// <summary>
        /// Prepares object for decompression.
        /// </summary>
        private void InitExpand() {
            StartHuff();

            for (int i = 0; i < N - F; i++) {
                mTextBuf[i] = mInitVal;     // usually ' '
            }
            mExpR = N - F;

            mExpGetlen = 0;
            mExpGetbuf = 0;

            mExpState = ExpState.GetNext;

            if (mIncludeLen) {
                mExpOutputLen = RawData.ReadU32LE(mCompDataStream, out bool ok);
                if (!ok) {
                    throw new InvalidDataException("failed reading length");
                }
            }
            if (mExpOutputLen < 0) {
                throw new InvalidDataException("invalid length for uncompressed data: " +
                    mExpOutputLen);
            }
            mExpDataOut = 0;
        }

        /// <summary>
        /// Reads uncompressed data.  (Stream call.)
        /// </summary>
        public override int Read(byte[] buffer, int offset, int count) {
            int startOffset = offset;

            // Continue processing compressed data until the user buffer is full or we have
            // written the entire file out.
            while (count > 0 && mExpDataOut < mExpOutputLen) {
                switch (mExpState) {
                    case ExpState.GetNext:
                        ushort c = DecodeChar();
                        if (c < 256) {
                            // Found a character literal.
                            buffer[offset++] = (byte)c;
                            count--;

                            mTextBuf[mExpR++] = (byte)c;
                            mExpR &= (N - 1);
                            mExpDataOut++;
                        } else {
                            // Found a match length.
                            mExpMatchPosition = (mExpR - DecodePosition() - 1) & (N - 1);
                            mExpMatchLength = c - 255 + THRESHOLD;
                            mExpState = ExpState.InMatch;
                        }
                        break;
                    case ExpState.InMatch:
                        while (count > 0 && mExpMatchLength > 0) {
                            byte ch = mTextBuf[mExpMatchPosition & (N - 1)];
                            mExpMatchPosition++;
                            mExpMatchLength--;
                            buffer[offset++] = ch;
                            count--;

                            mTextBuf[mExpR++] = ch;
                            mExpR &= (N - 1);
                            mExpDataOut++;
                        }
                        if (mExpMatchLength == 0) {
                            mExpState = ExpState.GetNext;
                        }
                        break;
                    default:
                        throw new InvalidDataException("? " + mExpState);
                }
            }
            return offset - startOffset;
        }

        private static readonly byte[] D_CODE = new byte[256] {
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01,
            0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01,
            0x02, 0x02, 0x02, 0x02, 0x02, 0x02, 0x02, 0x02,
            0x02, 0x02, 0x02, 0x02, 0x02, 0x02, 0x02, 0x02,
            0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03,
            0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03,
            0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04,
            0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05,
            0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06,
            0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07,
            0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08,
            0x09, 0x09, 0x09, 0x09, 0x09, 0x09, 0x09, 0x09,
            0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A,
            0x0B, 0x0B, 0x0B, 0x0B, 0x0B, 0x0B, 0x0B, 0x0B,
            0x0C, 0x0C, 0x0C, 0x0C, 0x0D, 0x0D, 0x0D, 0x0D,
            0x0E, 0x0E, 0x0E, 0x0E, 0x0F, 0x0F, 0x0F, 0x0F,
            0x10, 0x10, 0x10, 0x10, 0x11, 0x11, 0x11, 0x11,
            0x12, 0x12, 0x12, 0x12, 0x13, 0x13, 0x13, 0x13,
            0x14, 0x14, 0x14, 0x14, 0x15, 0x15, 0x15, 0x15,
            0x16, 0x16, 0x16, 0x16, 0x17, 0x17, 0x17, 0x17,
            0x18, 0x18, 0x19, 0x19, 0x1A, 0x1A, 0x1B, 0x1B,
            0x1C, 0x1C, 0x1D, 0x1D, 0x1E, 0x1E, 0x1F, 0x1F,
            0x20, 0x20, 0x21, 0x21, 0x22, 0x22, 0x23, 0x23,
            0x24, 0x24, 0x25, 0x25, 0x26, 0x26, 0x27, 0x27,
            0x28, 0x28, 0x29, 0x29, 0x2A, 0x2A, 0x2B, 0x2B,
            0x2C, 0x2C, 0x2D, 0x2D, 0x2E, 0x2E, 0x2F, 0x2F,
            0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37,
            0x38, 0x39, 0x3A, 0x3B, 0x3C, 0x3D, 0x3E, 0x3F,
        };

        private static readonly byte[] D_LEN = new byte[256] {
            0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03,
            0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03,
            0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03,
            0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03,
            0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04,
            0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04,
            0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04,
            0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04,
            0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04,
            0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04,
            0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05,
            0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05,
            0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05,
            0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05,
            0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05,
            0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05,
            0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05,
            0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05,
            0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06,
            0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06,
            0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06,
            0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06,
            0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06,
            0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06,
            0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07,
            0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07,
            0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07,
            0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07,
            0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07,
            0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07,
            0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08,
            0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08,
        };

        private ushort DecodeChar() {
            ushort c = mSon[R];

            // Travel from root to leaf,
            // choosing the smaller child node (son[]) if the read bit is 0,
            // the bigger (son[]+1] if 1.
            while (c < T) {
                c += (ushort)GetBit();
                c = mSon[c];
            }
            c -= T;
            update(c);
            return c;
        }

        private int DecodePosition() {
            // Recover upper 6 bits from table.
            ushort i = GetByte();
            ushort c = (ushort)(D_CODE[i] << 6);
            ushort j = D_LEN[i];

            // Read lower 6 bits verbatim.
            j -= 2;
            while (j-- != 0) {
                i = (ushort)((i << 1) + GetBit());
            }
            return c | (i & 0x3f);
        }

        /// <summary>
        /// Get one bit.
        /// </summary>
        private int GetBit() {
            while (mExpGetlen <= 8) {
                int i = mCompDataStream.ReadByte();
                if (i < 0) {
                    i = 0;
                }
                mExpGetbuf |= (ushort)(i << (8 - mExpGetlen));
                mExpGetlen += 8;
            }
            ushort i1 = mExpGetbuf;
            mExpGetbuf <<= 1;
            mExpGetlen--;
            return i1 >> 15;
        }

        /// <summary>
        /// Get one byte.
        /// </summary>
        /// <returns></returns>
        private ushort GetByte() {
            while (mExpGetlen <= 8) {
                int i = mCompDataStream.ReadByte();
                if (i < 0) {
                    i = 0;
                }
                mExpGetbuf |= (ushort)(i << (8 - mExpGetlen));
                mExpGetlen += 8;
            }
            ushort i1 = mExpGetbuf;
            mExpGetbuf <<= 8;
            mExpGetlen -= 8;
            return (ushort)(i1 >> 8);
        }

        #endregion Expand
    }
}
