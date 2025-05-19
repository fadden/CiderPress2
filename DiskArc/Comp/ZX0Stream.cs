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
    /// Streaming implementation of Einar Saukas' ZX0 compression.  This is based on v2.2, as
    /// implemented in <see href="https://github.com/einar-saukas/ZX0"/>.
    /// </summary>
    /// <remarks>
    /// <para>This is a straight port of the original author's
    /// <see href="https://github.com/einar-saukas/ZX0-Java">Java implementation</see>.  The
    /// implementation is a block-oriented compressor that uses optimal parsing, and I haven't
    /// attempted a streaming conversion, so this currently holds the entire input and output in
    /// memory.</para>
    /// <para>The format has no header, and does not include feature flags or the uncompressed
    /// length with the compressed data.  The end of stream is indicated by a specific sequence
    /// in the compressed data.</para>
    /// </remarks>
    public class ZX0Stream : Stream {
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
        /// Constructor.
        /// </summary>
        /// <param name="compDataStream">Stream that holds compressed data.  Must be positioned
        ///   at the start.  May be part of a larger stream (i.e. file archive).</param>
        /// <param name="mode">Flag that indicates whether we will be compressing or
        ///   expanding data.</param>
        /// <param name="leaveOpen">Flag that determines whether we close the compressed
        ///   data stream when we are disposed.</param>
        /// <param name="compDataStreamLen">Decompression: length of compressed data.</param>
        public ZX0Stream(Stream compDataStream, CompressionMode mode, bool leaveOpen,
                long compDataStreamLen, bool quickMode) {
            mCompDataStream = compDataStream;
            mCompressionMode = mode;
            mLeaveOpen = leaveOpen;
            mExpInputLength = compDataStreamLen;
            mCompQuickMode = quickMode;

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

        /// <summary>
        /// Tracks linked blocks of data.
        /// </summary>
        private class Block {
            public int Bits { get; private set; }
            public int Index { get; private set; }
            public int Offset { get; private set; }
            public Block? Chain { get; set; }

            public Block(int bits, int index, int offset, Block? chain) {
                Bits = bits;
                Index = index;
                Offset = offset;
                Chain = chain;
            }
        }

        /// <summary>
        /// Optimal parser.
        /// </summary>
        private class Optimizer {
            public const int INITIAL_OFFSET = 1;
            public const int MAX_SCALE = 50;

            private Block?[]? mLastLiteral;
            private Block?[]? mLastMatch;
            private Block?[]? mOptimal;
            private int[]? mMatchLength;
            private int[]? mBestLength;

            private static int OffsetCeiling(int index, int offsetLimit) {
                return Math.Min(Math.Max(index, INITIAL_OFFSET), offsetLimit);
            }

            private static int EliasGammaBits(int value) {
                int bits = 1;
                while (value > 1) {
                    bits += 2;
                    value >>= 1;
                }
                return bits;
            }

            /// <summary>
            /// Analyze input file, identifying the arrangement of literals and matches that
            /// yields the optimal output size.
            /// </summary>
            public Block? Optimize(byte[] input, int skip, int offsetLimit) {
                int arraySize = OffsetCeiling(input.Length - 1, offsetLimit) + 1;
                //Debug.WriteLine("Optimize: input length=" + input.Length +
                //    ", arraySize=" + arraySize);
                mLastLiteral = new Block[arraySize];
                mLastMatch = new Block[arraySize];
                mOptimal = new Block[input.Length];
                mMatchLength = new int[arraySize];
                mBestLength = new int[input.Length];
                if (mBestLength.Length > 2) {
                    mBestLength[2] = 2;
                }

                // Start with fake block.
                mLastMatch[INITIAL_OFFSET] = new Block(-1, skip - 1, INITIAL_OFFSET, null);

                for (int index = skip; index < input.Length; index++) {
                    int maxOffset = OffsetCeiling(index, offsetLimit);
                    Debug.Assert(index < mOptimal.Length);
                    mOptimal[index] = ProcessTask(1, maxOffset, index, skip, input);
                }

                return mOptimal[input.Length - 1];
            }

            /// <summary>
            /// Processes a range of locations.  (In the original Java code, this is the
            /// entry point for pooled threads.)
            /// </summary>
            private Block? ProcessTask(int initialOffset, int finalOffset, int index,
                    int skip, byte[] input) {
                Debug.Assert(mLastLiteral != null && mLastMatch != null && mOptimal != null);
                Debug.Assert(mMatchLength != null && mBestLength != null);
                int bestLengthSize = 2;
                Block? optimalBlock = null;
                for (int offset = initialOffset; offset <= finalOffset; offset++) {
                    if (index != skip && index >= offset && input[index] == input[index - offset]) {
                        // Copy from last offset.
                        if (mLastLiteral[offset] != null) {
                            int length = index - mLastLiteral[offset]!.Index;
                            int bits = mLastLiteral[offset]!.Bits + 1 + EliasGammaBits(length);
                            mLastMatch[offset] = new Block(bits, index, offset, mLastLiteral[offset]);
                            if (optimalBlock == null || optimalBlock.Bits > bits) {
                                optimalBlock = mLastMatch[offset];
                            }
                        }
                        // Copy from new offset.
                        if (++mMatchLength[offset] > 1) {
                            if (bestLengthSize < mMatchLength[offset]) {
                                int bits1 = mOptimal[index - mBestLength[bestLengthSize]]!.Bits +
                                    EliasGammaBits(mBestLength[bestLengthSize] - 1);
                                do {
                                    bestLengthSize++;
                                    int bits2 = mOptimal[index - bestLengthSize]!.Bits +
                                        EliasGammaBits(bestLengthSize - 1);
                                    if (bits2 <= bits1) {
                                        mBestLength[bestLengthSize] = bestLengthSize;
                                        bits1 = bits2;
                                    } else {
                                        mBestLength[bestLengthSize] = mBestLength[bestLengthSize - 1];
                                    }
                                } while (bestLengthSize < mMatchLength[offset]);
                            }
                            int length = mBestLength[mMatchLength[offset]];
                            int bits = mOptimal[index - length]!.Bits + 8 +
                                EliasGammaBits((offset - 1) / 128 + 1) + EliasGammaBits(length - 1);
                            if (mLastMatch[offset] == null || mLastMatch[offset]!.Index != index ||
                                    mLastMatch[offset]!.Bits > bits) {
                                mLastMatch[offset] = new Block(bits, index, offset,
                                    mOptimal[index - length]);
                                if (optimalBlock == null || optimalBlock.Bits > bits) {
                                    optimalBlock = mLastMatch[offset];
                                }
                            }
                        }
                    } else {
                        // Copy literals.
                        mMatchLength[offset] = 0;
                        if (mLastMatch[offset] != null) {
                            int length = index - mLastMatch[offset]!.Index;
                            int bits = mLastMatch[offset]!.Bits + 1 + EliasGammaBits(length) +
                                length * 8;
                            mLastLiteral[offset] = new Block(bits, index, 0, mLastMatch[offset]);
                            if (optimalBlock == null || optimalBlock.Bits > bits) {
                                optimalBlock = mLastLiteral[offset];
                            }
                        }
                    }
                }
                return optimalBlock;
            }
        }

        private MemoryStream? mCompInputStream;
        private bool mCompQuickMode;

        private byte[]? mCompOutput;
        private int mCompOutputIndex;
        private int mCompInputIndex;
        private int mCompBitIndex;
        private byte mCompBitMask;
        private int mCompDiff;
        private bool mCompBacktrack;

        private const int MAX_OFFSET_ZX0 = 32640;
        private const int MAX_OFFSET_ZX7 = 2176;

        /// <summary>
        /// Prepares object for compression.
        /// </summary>
        private void InitCompress() {
            mCompInputStream = new MemoryStream();
        }

        /// <summary>
        /// Receives uncompressed data to be compressed.  (Stream call.)
        /// </summary>
        public override void Write(byte[] buffer, int offset, int count) {
            // Append to memory buffer.
            mCompInputStream!.Write(buffer, offset, count);
        }

        /// <summary>
        /// Finishes compression operation.  This method is called when this object is closed,
        /// indicating that all data has been provided.
        /// </summary>
        private void FinishCompression() {
            // The subroutines use input.Length; be lazy and just copy the data out into a
            // buffer that's the exact right size.
            byte[] input = mCompInputStream!.ToArray();
            if (input.Length == 0) {
                // Special handling for empty file.
                return;
            }
            // Analyze the file.
            Block? optimal = new Optimizer().Optimize(input, 0,
                mCompQuickMode ? MAX_OFFSET_ZX7 : MAX_OFFSET_ZX0);
            if (optimal == null) {
                throw new Exception("Internal error: failed to parse input for ZX0");
            }
            // Output the compressed data per the analysis.
            int delta = 0;
            byte[] output = Compress(optimal, input, 0, false, true /*!classic*/, ref delta);
            mCompDataStream.Write(output, 0, output.Length);
        }

        /// <summary>
        /// Performs the actual compression.
        /// </summary>
        private byte[] Compress(Block? optimal, byte[] input, int skip, bool backwardsMode,
                bool invertMode, ref int delta) {
            Debug.Assert(optimal != null);      // optimal must be nullable for stuff later
            int lastOffset = Optimizer.INITIAL_OFFSET;

            // Calculate and allocate output buffer.
            mCompOutput = new byte[(optimal.Bits + 25) / 8];

            // Un-reverse optimal sequence.
            Block? prev = null;
            while (optimal != null) {
                Block? next = optimal.Chain;
                optimal.Chain = prev;
                prev = optimal;
                optimal = next;
            }

            // Initialize data.
            mCompDiff = mCompOutput.Length - input.Length + skip;
            delta = 0;
            mCompInputIndex = skip;
            mCompOutputIndex = 0;
            mCompBitMask = 0;
            mCompBacktrack = true;

            // Generate output.
            for (optimal = prev!.Chain; optimal != null; prev = optimal, optimal = optimal.Chain) {
                int length = optimal.Index - prev.Index;
                if (optimal.Offset == 0) {
                    // Copy literals indicator.
                    PutBit(0);

                    // Copy literals length.
                    PutInterlacedEliasGamma(length, backwardsMode, false);

                    // Copy literals value.
                    for (int i = 0; i < length; i++) {
                        PutByte(input[mCompInputIndex]);
                        AdvanceBytes(1, ref delta);
                    }
                } else if (optimal.Offset == lastOffset) {
                    // Copy from last offset indicator.
                    PutBit(0);

                    // Copy from last offset length.
                    PutInterlacedEliasGamma(length, backwardsMode, false);
                    AdvanceBytes(length, ref delta);
                } else {
                    // Copy from new offset indicator.
                    PutBit(1);

                    // Copy from new offset MSB.
                    PutInterlacedEliasGamma((optimal.Offset - 1) / 128 + 1, backwardsMode,
                        invertMode);

                    // Copy from new offset LSB.
                    PutByte((byte)(backwardsMode ? ((optimal.Offset - 1) % 128) << 1 :
                        (127 - (optimal.Offset - 1) % 128) << 1));

                    // Copy from new offset length.
                    mCompBacktrack = true;
                    PutInterlacedEliasGamma(length - 1, backwardsMode, false);
                    AdvanceBytes(length, ref delta);

                    lastOffset = optimal.Offset;
                }
            }

            // End marker.
            PutBit(1);
            PutInterlacedEliasGamma(256, backwardsMode, invertMode);

            // Done!
            return mCompOutput;
        }

        private void PutInterlacedEliasGamma(int value, bool backwardsMode,
                bool invertMode) {
            int i = 2;
            while (i <= value) {
                i <<= 1;
            }
            i >>= 1;
            while ((i >>= 1) > 0) {
                PutBit((byte)(backwardsMode ? 1 : 0));
                PutBit((byte)(invertMode == ((value & i) == 0) ? 1 : 0));
            }
            PutBit((byte)(!backwardsMode ? 1 : 0));
        }

        private void PutByte(byte value) {
            mCompOutput![mCompOutputIndex++] = value;
            mCompDiff--;
        }

        private void PutBit(byte value) {
            Debug.Assert(mCompOutput != null);
            if (mCompBacktrack) {
                if (value > 0) {
                    mCompOutput[mCompOutputIndex - 1] |= 1;
                }
                mCompBacktrack = false;
            } else {
                if (mCompBitMask == 0) {
                    mCompBitMask = 128;
                    mCompBitIndex = mCompOutputIndex;
                    PutByte(0);
                }
                if (value > 0) {
                    mCompOutput[mCompBitIndex] |= mCompBitMask;
                }
                mCompBitMask >>= 1;
            }
        }

        private void AdvanceBytes(int n, ref int delta) {
            mCompInputIndex += n;
            mCompDiff += n;
            if (delta < mCompDiff) {
                delta = mCompDiff;
            }
        }

        #endregion Compress

        #region Expand

        private long mExpInputLength;

        private byte[]? mExpInput;              // fixed-size input buffer
        private MemoryStream? mExpOutStream;    // variable-size output buffer
        private int mExpReadOffset;
        private bool mExpReady;

        private int mExpLastOffset;
        private int mExpInputIndex;
        private byte mExpBitMask;
        private byte mExpBitValue;
        private bool mExpBackwards;
        private bool mExpInverted;
        private bool mExpBacktrack;
        private byte mLastByte;

        private enum ExpState {
            Unknown = 0, CopyLiterals, CopyFromLastOffset, CopyFromNewOffset, Done
        }

        /// <summary>
        /// Prepares object for decompression.
        /// </summary>
        private void InitExpand() {
            mExpInput = new byte[mExpInputLength];

            // Pre-size the output stream based on the length of the input stream, to reduce
            // allocations.
            mExpOutStream = new MemoryStream((int)mExpInputLength);
            mExpReadOffset = 0;
            mExpReady = false;

            mExpLastOffset = Optimizer.INITIAL_OFFSET;
            mExpInputIndex = 0;
            mExpBitMask = 0;
            mExpBackwards = false;
            mExpInverted = true;        // !classic
            mExpBacktrack = false;
        }

        /// <summary>
        /// Reads uncompressed data.  (Stream call.)
        /// </summary>
        public override int Read(byte[] buffer, int offset, int count) {
            Debug.Assert(mExpOutStream != null);
            if (!mExpReady) {
                // Do the full decompress on the first read call.
                DoDecompress();
                mExpOutStream.Position = 0;
                mExpReady = true;
            }

            // Copy as much as we can into the user's buffer.
            int actual = count;
            if (actual > mExpOutStream.Length - mExpReadOffset) {
                actual = (int)(mExpOutStream.Length - mExpReadOffset);
            }
            byte[] buf = mExpOutStream.GetBuffer();
            Array.Copy(buf, mExpReadOffset, buffer, offset, actual);
            mExpReadOffset += actual;
            return actual;
        }

        /// <summary>
        /// Performs the decompression operation.
        /// </summary>
        private void DoDecompress() {
            Debug.Assert(mExpOutStream != null);
            if (mExpInputLength == 0) {
                return;     // special handling for zero-length file
            }

            // Read all of the compressed data into memory.  The algorithm is amenable to
            // streaming, but this is simpler.
            //Debug.WriteLine("Reading compressed data: " + mExpInputLength + " bytes (posn=" +
            //    mCompDataStream.Position + ")");
            mCompDataStream.ReadExactly(mExpInput!, 0, (int)mExpInputLength);

            ExpState state = ExpState.CopyLiterals;
            while (state != ExpState.Done) {
                switch (state) {
                    case ExpState.CopyLiterals: {
                            int length = GetInterlacedEliasGamma(false);
                            //Debug.WriteLine("LIT " + length);
                            for (int i = 0; i < length; i++) {
                                mExpOutStream.WriteByte(GetByte());
                            }
                            state = (GetBit() == 0) ?
                                ExpState.CopyFromLastOffset : ExpState.CopyFromNewOffset;
                        }
                        break;
                    case ExpState.CopyFromLastOffset: {
                            int length = GetInterlacedEliasGamma(false);
                            //Debug.WriteLine("LAST " + length);
                            CopyBytes(length);
                            state = (GetBit() == 0) ?
                                ExpState.CopyLiterals : ExpState.CopyFromNewOffset;
                        }
                        break;
                    case ExpState.CopyFromNewOffset: {
                            int msb = GetInterlacedEliasGamma(true);    // false for "classic" mode
                            if (msb == 256) {
                                state = ExpState.Done;
                            } else {
                                int lsb = GetByte() >> 1;
                                mExpLastOffset = mExpBackwards ?
                                    msb * 128 + lsb - 127 : msb * 128 - lsb;
                                mExpBacktrack = true;
                                int length = GetInterlacedEliasGamma(false) + 1;
                                //Debug.WriteLine("NEW " + mExpLastOffset + " " + length);
                                CopyBytes(length);
                                state = (GetBit() == 0) ?
                                    ExpState.CopyLiterals : ExpState.CopyFromNewOffset;
                            }
                        }
                        break;
                    default:
                        throw new Exception("bad state");
                }
            }
        }

        private byte GetByte() {
            Debug.Assert(mExpInputIndex < mExpInput!.Length);
            mLastByte = mExpInput![mExpInputIndex++];
            return mLastByte;
        }

        private byte GetBit() {
            if (mExpBacktrack) {
                mExpBacktrack = false;
                return (byte)(mLastByte & 0x01);
            }
            mExpBitMask >>= 1;
            if (mExpBitMask == 0) {
                mExpBitMask = 128;
                mExpBitValue = GetByte();
            }
            return (byte)((mExpBitValue & mExpBitMask) != 0 ? 1 : 0);
        }

        private int GetInterlacedEliasGamma(bool msb) {
            int value = 1;
            while (GetBit() == (mExpBackwards ? 1 : 0)) {
                value = (value << 1) | GetBit() ^ (msb && mExpInverted ? 1 : 0);
            }
            return value;
        }

        private void CopyBytes(int length) {
            Debug.Assert(mExpOutStream != null);
            // We can't just use GetBuffer() once because the copy range might overlap with itself,
            // and the underlying byte[] could be reallocated.  We either need to get the buffer
            // every time, or use Position+ReadByte.  In theory we could just bump the Capacity
            // to ensure the new data fits, but that assumes the memory stream will only expand
            // when required.
            while (length-- > 0) {
                long posn = mExpOutStream.Length - mExpLastOffset;
                Debug.Assert(posn >= 0 && posn < mExpOutStream.Length);
                mExpOutStream.Position = posn;
                int val = mExpOutStream.ReadByte();
                mExpOutStream.Position = mExpOutStream.Length;
                mExpOutStream.WriteByte((byte)val);
            }
        }

        #endregion Expand
    }
}
