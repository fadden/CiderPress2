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
    /// Streaming implementation of StuffIt's LZ+Huffman compression (algorithm 13).
    /// </summary>
    /// <remarks>
    /// <para>The primary source for this algorithm is the open-source version of
    /// The Unarchiver and its
    /// <see href="https://github.com/ashang/unar/blob/master/XADMaster/XADStuffIt13Handle.m">method 13</see>
    /// decompressor.  The Sit13Decoder/Sit13Encoder implementations in
    /// <see href="https://github.com/benletchford/stuffit-rs">stuffit-rs</see> (v0.4.0) were
    /// very helpful.</para>
    /// <para>The file sizes used in StuffIt v1.5/v5 archives are 32 bits, so we don't currently
    /// try to handle files larger than 2GB.</para>
    /// </remarks>
    public class StuffLZHuffStream : Stream {
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
        /// <param name="index">Compression: encoding table index. Pass 0 for adaptive, 1-5
        ///   for fixed tables, or -1 to allow the algorithm to decide.</param>
        public StuffLZHuffStream(Stream compDataStream, CompressionMode mode, bool leaveOpen,
                long compDataStreamLen, int index) {
            mCompDataStream = compDataStream;
            mCompressionMode = mode;
            mLeaveOpen = leaveOpen;

            if (mode == CompressionMode.Compress) {
                Debug.Assert(compDataStream.CanWrite);
                InitCompress(index);
            } else if (mode == CompressionMode.Decompress) {
                Debug.Assert(compDataStream.CanRead);
                if (compDataStreamLen < 0) {
                    throw new ArgumentException("Invalid value for " + nameof(compDataStreamLen));
                }
                InitExpand(compDataStreamLen);
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

        //
        // Common members.
        //

        //
        // LZ77 matches against strings we've seen in the past, but can also do a match
        // where a string overlaps with itself (like filling RAM with an overlapping memory
        // move).  Given a maximum match offset of O, and a maximum match length of L, we
        // want to hold O bytes of data we've already processed (string dictionary), and L
        // bytes of data we haven't processed yet (repeating-string opportunity).
        //
        // The largest value for O is 16 bits (64KB), and the largest value for L is
        // 32767+65=32832, so we can accommodate this with a 128KB sliding window.  We can use
        // a smaller window if we want to maintain the string dictionary data in a separate
        // structure.
        //
        // The largest possible offset using one of the fixed encoding tables is 13 bits (8KB),
        // so can currently get away with a 64KB window (which is easier to mentally factor
        // when debugging).
        //
        private const int WINDOW_SIZE = 64*1024;            // must be power of 2
        private const int WINDOW_MASK = WINDOW_SIZE - 1;

        private const int STOP_SYMBOL = 320;


        #region Compress

        //
        // String match dictionary.
        //
        // Match lengths can be from 3 to 32832 bytes, and be at an offset from 1 to 65536 bytes
        // back from the current position.  Matches that overlap with the current position are
        // allowed.  While the maximum match length doesn't change, the maximum offset can vary.
        //
        // To narrow the search for match candidates, we hash the first three bytes, and use
        // that value to access a list of all strings with the same hash code.  Matches that
        // fall outside the maximum offset can be discarded.  The most recently-discovered
        // strings should be prioritized, because smaller offsets can take fewer bits to encode.
        //
        // A simple arrangement is to have a pool of match nodes, with a number of elements
        // equal to the maximum offset.  For every byte of input, we grab the node at
        // (file_position % max_offset), remove it from an existing chain, then insert it
        // at the head of the chain for the hash value.
        //

        /// <summary>
        /// Each node is a link in a chain.
        /// </summary>
        /// <remarks>
        /// NOTE: this is a struct, not a class, because we need a big array of them and don't
        /// need to pass them around individually.  These use the default initializer, so you
        /// can identify a new node by mPrev==mNext==0.
        /// </remarks>
        private struct MatchNode {
            /// <summary>
            /// Position in file where this string starts.
            /// </summary>
            public int mPosn;

            /// <summary>
            /// Array index of previous node in chain, or -1 if this is referenced by hash table.
            /// </summary>
            public int mPrev;

            /// <summary>
            /// Array index of next node in chain, or -1 if none.
            /// </summary>
            public int mNext;

            /// <summary>
            /// Hash value for this string.
            /// </summary>
            public uint mHash;

            public override string ToString() {
                return "[posn=" + mPosn + " prev=" + mPrev + " next=" + mNext +
                    " (hash=$" + mHash.ToString("x6") + ")";
            }
        }

        /// <summary>
        /// Maps a string hash value to the head of a node chain.
        /// </summary>
        private Dictionary<uint, int> mCompMatchDict = new Dictionary<uint, int>(0);

        /// <summary>
        /// Match string dictionary, one element for every possible match position.
        /// </summary>
        private MatchNode[] mCompMatchNodes = Array.Empty<MatchNode>();

        /// <summary>
        /// Simple wrapper to hold the two parts of the encoding data together.
        /// </summary>
        private class EncoderTable {
            public uint[] Codes { get; private set; }
            public int[] Lengths { get; private set; }
            public EncoderTable(uint[] codes, int[] lengths) {
                Debug.Assert(codes.Length == lengths.Length);
                Codes = codes;
                Lengths = lengths;
            }
        }
        private static readonly EncoderTable NO_ENCODER =
            new EncoderTable(Array.Empty<uint>(), Array.Empty<int>());

        private const int MIN_MATCH_LEN = 3;
        private const int MAX_MATCH_LEN = 32767 + 65;
        private int mMaxOffset;     // determined when encoder is set

        private EncoderTable mCompFirstEncoder = NO_ENCODER;
        private EncoderTable mCompSecondEncoder = NO_ENCODER;
        private EncoderTable mCompOffsetEncoder = NO_ENCODER;
        private EncoderTable mCompCurEncoder = NO_ENCODER;

        private int mCompTableIndex;
        private bool mCompHeaderWritten;
        private BitWriter? mCompBitWriter;
        private byte[] mCompWindow = Array.Empty<byte>();
        private int mCompReadPos;
        private int mCompWritePos;


        /// <summary>
        /// Prepares object for compression.
        /// </summary>
        private void InitCompress(int tableIndex) {
            if (tableIndex == -1) {
                tableIndex = 1;
            } else if (tableIndex == 0) {
                throw new NotImplementedException("mode 0 not supported");
            } else if (tableIndex > 5) {
                throw new ArgumentOutOfRangeException(nameof(tableIndex),
                    "index should be 0-5, or -1 for default");
            }
            mCompTableIndex = tableIndex;
            mCompHeaderWritten = false;
            mCompBitWriter = new BitWriter(mCompDataStream);
            mCompWindow = new byte[WINDOW_SIZE];

            mCompReadPos = mCompWritePos = 0;
        }

        /// <summary>
        /// Receives uncompressed data to be compressed.  (Stream call.)
        /// </summary>
        public override void Write(byte[] buffer, int offset, int count) {
            //
            // Copy incoming data into the buffer.  We need to stay 32832 bytes ahead of the
            // "read" pointer to foster overlapping matches, and we need to preserve the 65536
            // bytes behind the "read" pointer because that's our string dictionary.
            //
            while (count > 0) {
                // Copy the next byte in.
                mCompWindow[mCompWritePos++ & WINDOW_MASK] = buffer[offset++];
                count--;

                if (mCompWritePos < 0) {
                    // This can happen due to integer overflow if we exceed 2GB.  We can avoid
                    // this situation by changing all file positions from int to long.
                    throw new InvalidDataException("input may not exceed 2GB");
                }

                // If we're far enough ahead of the read pointer, process some data.
                if (mCompWritePos - mCompReadPos > MAX_MATCH_LEN) {
                    CompressData(false);
                }
            }
        }

        /// <summary>
        /// Finishes compression operation.  This method is called when this object is closed,
        /// indicating that all data has been provided.
        /// </summary>
        private void FinishCompression() {
            CompressData(true);
        }

        /// <summary>
        /// Compresses some or all of the pending data.
        /// </summary>
        /// <param name="finishing">True if called from FinishCompression().</param>
        private void CompressData(bool finishing) {
            Debug.Assert(mCompBitWriter != null);

            //
            // The current approach is to use a fixed table, specified by the caller, because
            // taking it as an argument makes it easier to exercise the different tables using
            // the API.  We don't currently handle index==0, which would require performing a
            // frequency analysis and generating a Huffman tree.
            //
            if (!mCompHeaderWritten) {
                // Write the header byte.  Just need the table index in the high nibble.
                mCompBitWriter.WriteByteLE((byte)(mCompTableIndex << 4));

                // Set up encoder tables.  (At some point we may want to select a table based
                // on the buffer contents.)
                mCompFirstEncoder = new EncoderTable(
                    GenerateCodesFromLengths(FirstCodeLengths[mCompTableIndex - 1]),
                    FirstCodeLengths[mCompTableIndex - 1]);
                mCompSecondEncoder = new EncoderTable(
                    GenerateCodesFromLengths(SecondCodeLengths[mCompTableIndex - 1]),
                    SecondCodeLengths[mCompTableIndex - 1]);
                mCompOffsetEncoder = new EncoderTable(
                    GenerateCodesFromLengths(OffsetCodeLengths[mCompTableIndex - 1]),
                    OffsetCodeLengths[mCompTableIndex - 1]);
                mCompCurEncoder = mCompFirstEncoder;

                // Maximum length of an offset is determined by the offset encoder, which is used
                // to encode the length in bits of the offset value (minus one, because offset==0
                // isn't valid).  The table has N symbols, starting from zero, so the longest
                // offset must fit in (N-1) bits.
                mMaxOffset = 1 << (mCompOffsetEncoder.Codes.Length - 1);
                // The max offset determines how many entries there are in our string-matching set.
                mCompMatchNodes = new MatchNode[mMaxOffset];
                mCompMatchDict = new Dictionary<uint, int>(mMaxOffset);

                mCompHeaderWritten = true;
            }

            //
            // We loop until we get low on data, unless we're finishing the file.
            //
            while ((finishing && mCompReadPos < mCompWritePos) ||
                    mCompWritePos - mCompReadPos > MAX_MATCH_LEN) {
                // Find the best match for the current file position.
                bool found = FindBestMatch(out int matchOffset, out int matchLength);

                // Add the string at the current offset to the hash table.
                UpdateStringDict(mCompReadPos);

                if (found) {
                    //Debug.WriteLine("MATCH @$" + mCompReadPos.ToString("x6") + " (" + mCompReadPos +
                    //    " ) off=" + matchOffset + " len=" + matchLength);
                    // Encode match.
                    EncodeMatch(matchOffset, matchLength);

                    mCompCurEncoder = mCompSecondEncoder;       // switch to second tree

                    // Add the strings we're skipping over to the hash table.
                    for (int i = 1; i < matchLength; i++) {
                        UpdateStringDict(mCompReadPos + i);
                    }

                    mCompReadPos += matchLength;
                } else {
                    // Encode literal.
                    byte value = mCompWindow[mCompReadPos & WINDOW_MASK];
                    EncodeSymbol(value, mCompCurEncoder);
                    mCompCurEncoder = mCompFirstEncoder;        // switch to first tree

                    mCompReadPos++;
                }
            }

            if (finishing) {
                Debug.Assert(mCompReadPos == mCompWritePos);
                EncodeSymbol(STOP_SYMBOL, mCompCurEncoder);
                mCompBitWriter.Finish();
            }
        }

        /// <summary>
        /// Updates the string dictionary, setting the entry for the specified position.
        /// </summary>
        /// <param name="fileOffset"></param>
        private void UpdateStringDict(int fileOffset) {
            int index = fileOffset & (mMaxOffset - 1);
            if (mCompMatchNodes[index].mPrev == 0 && mCompMatchNodes[index].mNext == 0) {
                // This is the first time we're using this node (fields initialized to zero).
            } else if (mCompMatchNodes[index].mPrev == -1) {
                // No "prev" link, hash table points directly to this node.
                if (mCompMatchNodes[index].mNext == -1) {
                    // No "next" link, we're solo.  Remove the hash table reference.
                    mCompMatchDict.Remove(mCompMatchNodes[index].mHash);
                } else {
                    // We're at the head of a list with multiple nodes.  This means we're removing
                    // an entry that isn't the oldest in the hash chain, which doesn't make sense.
                    throw new Exception("whoops");
                }
            } else {
                // Last element in a chain.  Remove the link to us from the previous entry.
                mCompMatchNodes[mCompMatchNodes[index].mPrev].mNext = -1;
            }

            mCompMatchNodes[index].mPrev = mCompMatchNodes[index].mNext = -1;

            // Calculate the hash code.  This will fail if we're too close to the end of input.
            if (!CalcHash(mCompReadPos, out uint hash)) {
                return;
            }

            mCompMatchNodes[index].mHash = hash;
            mCompMatchNodes[index].mPosn = fileOffset;

            // Look for an existing entry for this hash code.
            if (mCompMatchDict.TryGetValue(hash, out int nodeIndex)) {
                // Entry exists, insert us at the head of the chain.
                mCompMatchNodes[index].mNext = nodeIndex;
                mCompMatchNodes[nodeIndex].mPrev = index;
            }
            mCompMatchDict[hash] = index;
        }

        /// <summary>
        /// Calculates a hash code on the string starting at the specified file offset.
        /// </summary>
        private bool CalcHash(int fileOffset, out uint hash) {
            if (fileOffset + 2 >= mCompWritePos) {
                hash = 0;
                return false;
            }
            hash = (uint)(mCompWindow[fileOffset & WINDOW_MASK] |
                (mCompWindow[(fileOffset + 1) & WINDOW_MASK] << 8) |
                (mCompWindow[(fileOffset + 2) & WINDOW_MASK] << 16));
            return true;
        }

        /// <summary>
        /// Finds the best match for the string at the current file offset.
        /// </summary>
        /// <param name="matchOffset">Result: offset from current position.</param>
        /// <param name="matchLength">Result: length of match.</param>
        /// <returns>True if a match was found.</returns>
        private bool FindBestMatch(out int matchOffset, out int matchLength) {
            matchOffset = matchLength = -1;

            // Calculate a hash on the string at the current position.
            if (!CalcHash(mCompReadPos, out uint hash)) {
                return false;
            }
            // See if we have an entry in the table.
            if (!mCompMatchDict.TryGetValue(hash, out int checkIndex)) {
                return false;
            }

            int maxMatchLen = MAX_MATCH_LEN;
            if (maxMatchLen > mCompWritePos - mCompReadPos) {
                maxMatchLen = mCompWritePos - mCompReadPos;
            }

            // Walk the chain, stopping if we find a potential match that starts too far away.
            int bestLength = 0;
            int bestPosn = 0;
            while (checkIndex >= 0) {
                int testPosn = mCompMatchNodes[checkIndex].mPosn;
                int testOffset = mCompReadPos - testPosn;
                if (testOffset > mMaxOffset) {
                    break;
                }

                // Calculate the match length.  Depending on the hashing approach, we might be able
                // to skip testing the first few bytes.
                int testLen;
                for (testLen = 0; testLen < maxMatchLen; testLen++) {
                    if (mCompWindow[(mCompReadPos + testLen) & WINDOW_MASK] !=
                            mCompWindow[(testPosn + testLen) & WINDOW_MASK]) {
                        break;
                    }
                }
                if (testLen >= MIN_MATCH_LEN && testLen > bestLength) {
                    bestLength = testLen;
                    bestPosn = testPosn;
                }
                if (testLen == maxMatchLen) {
                    break;      // not going to get any better
                }

                // Chase links.
                int nextIndex = mCompMatchNodes[checkIndex].mNext;
                checkIndex = nextIndex;
            }

            if (bestLength < MIN_MATCH_LEN) {
                return false;
            }
            matchOffset = mCompReadPos - bestPosn;
            matchLength = bestLength;
            Debug.Assert(matchOffset > 0);
            return true;
        }

        /// <summary>
        /// Encodes a match.
        /// </summary>
        private void EncodeMatch(int offset, int length) {
            Debug.Assert(mCompBitWriter != null);
            Debug.Assert(length >= MIN_MATCH_LEN);
            Debug.Assert(offset > 0);

            // Encode the match length.
            if (length <= 64) {
                EncodeSymbol(256 + length - 3, mCompCurEncoder);
            } else if (length <= 65 + 1023) {
                EncodeSymbol(318, mCompCurEncoder);
                mCompBitWriter.WriteBitsLE((uint)(length - 65), 10);
            } else if (length <= 65 + 32767) {
                EncodeSymbol(319, mCompCurEncoder);
                mCompBitWriter.WriteBitsLE((uint)(length - 65), 15);
            } else {
                throw new Exception("internal error: invalid length");
            }

            // Encode the match offset.
            int bitLen;
            if (offset == 1) {
                bitLen = 0;
            } else if (offset == 2) {
                bitLen = 1;
            } else {
                bitLen = System.Numerics.BitOperations.Log2((uint)(offset - 1)) + 1;
            }
            Debug.Assert(bitLen < mCompOffsetEncoder.Codes.Length);

            EncodeSymbol(bitLen, mCompOffsetEncoder);

            if (bitLen > 1) {
                // Output the rest of the integer.  Remove the leading '1' bit, and subtract 1,
                // to get a value that fits in (bitLen - 1) bits.
                int encOffset = offset - (1 << (bitLen - 1)) - 1;
                mCompBitWriter.WriteBitsLE((uint)encOffset, bitLen - 1);
            }
        }

        /// <summary>
        /// Encodes a symbol using the specified table.
        /// </summary>
        private void EncodeSymbol(int symbol, EncoderTable encoder) {
            Debug.Assert(mCompBitWriter != null);
            if (encoder.Lengths[symbol] <= 0) {
                throw new Exception("internal error: invalid symbol " + symbol);
            }

            //Debug.WriteLine("Encode sym=" + symbol + ": code=$" +
            //    encoder.Codes[symbol].ToString("x") + " len=" + encoder.Lengths[symbol]);
            mCompBitWriter.WriteBitsLE(encoder.Codes[symbol], encoder.Lengths[symbol]);
        }

        #endregion Compress


        #region Expand

        private static readonly int[,] EMPTY_TREE = new int[0, 0];
        private int[,] mExpFirstCodeTree = EMPTY_TREE;
        private int[,] mExpSecondCodeTree = EMPTY_TREE;
        private int[,] mExpOffsetCodeTree = EMPTY_TREE;
        private int[,] mExpCurCodeTree = EMPTY_TREE;        // points to either first or second

        private bool mExpHeaderRead;
        private BitReader? mExpBitReader;
        private byte[] mExpWindow = Array.Empty<byte>();
        private int mExpOutCount;
        private int mExpMatchLen;
        private int mExpMatchOffset;
        private bool mExpDone;


        /// <summary>
        /// Prepares object for decompression.
        /// </summary>
        private void InitExpand(long compDataStreamLen) {
            mExpHeaderRead = false;

            mExpBitReader = new BitReader(mCompDataStream, (int)compDataStreamLen);
            mExpWindow = new byte[WINDOW_SIZE];
            mExpOutCount = 0;
            mExpMatchLen = 0;
            mExpDone = false;
        }

        /// <summary>
        /// Reads uncompressed data.  (Stream call.)
        /// </summary>
        public override int Read(byte[] buffer, int offset, int count) {
            if (!mExpHeaderRead) {
                ReadHeader();
                mExpHeaderRead = true;
            }
            if (mExpDone) {
                return 0;       // previous Read call hit end of file
            }

            int startOffset = offset;
            while (count-- != 0) {
                int val = GetNextByte();
                if (val < 0) {
                    mExpDone = true;
                    break;
                }
                buffer[offset++] = (byte)val;
            }
            if (true) {
                string foo = System.Text.Encoding.ASCII.GetString(buffer, 0, offset - startOffset);
            }
            return offset - startOffset;
        }

        /// <summary>
        /// Reads and decodes the file header, which could be a single byte, or could contain
        /// multiple encoding trees.
        /// </summary>
        private void ReadHeader() {
            int firstByte = mExpBitReader!.ReadByte();
            int index = firstByte >> 4;
            if (index < 0 || index > 5) {
                throw new InvalidDataException("Unexpected configuration byte: $" +
                    firstByte.ToString("x2"));
            }

            if (index == 0) {
                int[,] metaTree = GenerateTreeFromCodes(MetaCodes, MetaCodeLengths);
                mExpFirstCodeTree = ReadTreeFromStream(metaTree, LIT_LEN_SYM_COUNT);
                if ((firstByte & 0x08) == 0) {
                    mExpSecondCodeTree = ReadTreeFromStream(metaTree, LIT_LEN_SYM_COUNT);
                } else {
                    mExpSecondCodeTree = mExpFirstCodeTree;
                }
                mExpOffsetCodeTree = ReadTreeFromStream(metaTree, (firstByte & 0x07) + 10);
            } else {
                GenerateTrees(index,
                    out mExpFirstCodeTree, out mExpSecondCodeTree, out mExpOffsetCodeTree);
            }
            mExpCurCodeTree = mExpFirstCodeTree;
        }

        /// <summary>
        /// Reads a Huffman encoding tree from the input stream.  This is only used when parsing
        /// the header of certain files.
        /// </summary>
        private int[,] ReadTreeFromStream(int[,] codeTree, int symCount) {
            Debug.Assert(mExpBitReader != null);
            int[] lengths = new int[symCount];
            int length = 0;
            int symIndex = 0;
            while (symIndex < symCount) {
                // Decode the next symbol from the input, using the meta code tree.  This can
                // set the value of "length" or just repeat the previous value multiple times.
                int val = DecodeSymbol(codeTree);
                switch (val) {
                    case 31:
                        length = -1;
                        break;
                    case 32:
                        length++;
                        break;
                    case 33:
                        length--;
                        break;
                    case 34:
                        int bit = mExpBitReader.ReadBitLE();
                        if (bit != 0) {
                            lengths[symIndex++] = length;
                        }
                        break;
                    case 35:    // 2-9
                        int count3 = (int)mExpBitReader.ReadBitsLE(3) + 2;
                        while (count3-- > 0 && symIndex < symCount) {
                            lengths[symIndex++] = length;
                        }
                        break;
                    case 36:    // 10-73
                        int count6 = (int)mExpBitReader.ReadBitsLE(6) + 10;
                        while (count6-- > 0 && symIndex < symCount) {
                            lengths[symIndex++] = length;
                        }
                        break;
                    default:
                        Debug.Assert(val < 37);
                        length = val + 1;
                        break;
                }
                if (symIndex < symCount) {
                    lengths[symIndex++] = length;
                }
            }

            return GenerateTreeFromLengths(lengths);
        }

        /// <summary>
        /// Returns the next byte of output, or -1 if EOF has been reached.
        /// </summary>
        private int GetNextByte() {
            Debug.Assert(mExpBitReader != null);

            // If we're expanding in a match, continue with that.  Otherwise, decode the next
            // bit of input.

            if (mExpMatchLen == 0) {
                int val = DecodeSymbol(mExpCurCodeTree);
                //Debug.WriteLine("DECODE $" + val.ToString("x2"));
                if (val < 256) {
                    // Literal value.
                    mExpCurCodeTree = mExpFirstCodeTree;      // switch to first tree!
                    mExpWindow[mExpOutCount++ & WINDOW_MASK] = (byte)val;
                    return val;
                } else {
                    // Match.  Start by decoding the length.
                    mExpCurCodeTree = mExpSecondCodeTree;     // switch to second tree!
                    if (val < 318) {
                        // Length encoded directly (3-64).
                        mExpMatchLen = val - 256 + 3;
                    } else if (val == 318 /*$13e*/) {
                        // Length is 10 bits (65 - 1023+65=1088).
                        mExpMatchLen = (int)mExpBitReader.ReadBitsLE(10) + 65;
                    } else if (val == 319 /*$13f*/) {
                        // Length is 15 bits (65 - 32767+65=32832).
                        mExpMatchLen = (int)mExpBitReader.ReadBitsLE(15) + 65;
                    } else if (val == STOP_SYMBOL /*$140*/) {
                        //Debug.WriteLine("Found stop symbol, remaining="
                        //    + mExpBitReader.RemainingBits);
                        return -1;
                    } else {
                        throw new InvalidDataException("Unexpected symbol " + val);
                    }

                    // Decode the offset.  The length of the offset is encoded with Huffman,
                    // and is followed by the variable-length offset bits.
                    int bitLen = DecodeSymbol(mExpOffsetCodeTree);
                    if (bitLen == 0) {
                        mExpMatchOffset = 1;
                    } else if (bitLen == 1) {
                        mExpMatchOffset = 2;
                    } else {
                        mExpMatchOffset = (1 << (bitLen - 1)) +
                            (int)mExpBitReader.ReadBitsLE(bitLen - 1) + 1;
                    }
                    //Debug.WriteLine("  MATCH @" + mExpOutCount + " len=" + mExpMatchLen +
                    //    " off=" + mExpMatchOffset + " (@" + (mExpOutCount - mExpMatchOffset) + ")");
                }
            }

            // Grab the next byte from the match.
            int pos = mExpOutCount - mExpMatchOffset;
            byte copyVal = mExpWindow[pos & WINDOW_MASK];
            mExpWindow[mExpOutCount++ & WINDOW_MASK] = copyVal;
            mExpMatchLen--;
            return copyVal;
        }

        /// <summary>
        /// Decodes the next symbol from the input stream.
        /// </summary>
        /// <param name="codeTree">Huffman code tree to use.</param>
        /// <returns>Symbol (0-320).</returns>
        /// <exception cref="InvalidDataException">Unable to decode bit stream.</exception>
        private int DecodeSymbol(int[,] codeTree) {
            int nodeIndex = 0;
            int debugBitCount = 0;
            while (true) {
                // If we reached a leaf, return the value.
                if (codeTree[nodeIndex, 0] == codeTree[nodeIndex, 1]) {
                    //Debug.WriteLine("Decode sym=" + codeTree[nodeIndex, 0] +
                    //    " len=" + debugBitCount);
                    return codeTree[nodeIndex, 0];
                }

                int bit = mExpBitReader!.ReadBitLE();
                int nextIndex = codeTree[nodeIndex, bit];
                if (nextIndex == NULL_REF) {
                    throw new InvalidDataException("failed to decode symbol");
                }
                nodeIndex = nextIndex;
                debugBitCount++;
            }
        }

        #endregion Expand


        #region Code Tables

        //
        // (Tables come from XADStuffIt13Handle.m in The Unarchiver.)
        //
        // There are five sets of code tables, each of which includes three Huffman encoding
        // tables.  A given file is encoded with one specific set.
        //
        // The first two tables in each set are used to encode 8-bit literals (entries 0-255)
        // and 6-bit match lengths (entries 256-319).  Entries 318 and 319 are special, used for
        // longer matches.  The final value (entry 320) is output at the end of the compressed data
        // stream.  The codec switches between the two tables as it works.
        //
        // The third table in each set is used to encode the length of the match offset.
        //
        // Some compressed files store the Huffman tables in the compressed data stream (indicated
        // as "set 0").  These tables are themselves compressed with Huffman, using the
        // "meta code" tables.
        //
        // In all cases, the arrays are lists of code lengths for the symbols in the table.
        // The data is used to construct a binary tree that can be used to encode or decode a
        // bit stream.
        //

        private static readonly int[] FirstCodeLengths_1 = {
             4, 5, 7, 8, 8, 9, 9, 9, 9, 7, 9, 9, 9, 8, 9, 9,
             9, 9, 9, 9, 9, 9, 9,10, 9, 9,10,10, 9,10, 9, 9,
             5, 9, 9, 9, 9,10, 9, 9, 9, 9, 9, 9, 9, 9, 7, 9,
             9, 8, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9,
             9, 8, 9, 9, 8, 8, 9, 9, 9, 9, 9, 9, 9, 7, 8, 9,
             7, 9, 9, 7, 7, 9, 9, 9, 9,10, 9,10,10,10, 9, 9,
             9, 5, 9, 8, 7, 5, 9, 8, 8, 7, 9, 9, 8, 8, 5, 5,
             7,10, 5, 8, 5, 8, 9, 9, 9, 9, 9,10, 9, 9,10, 9,
             9,10,10,10,10,10,10,10, 9,10,10,10,10,10,10,10,
             9,10,10,10,10,10,10,10,10,10,10,10,10,10,10,10,
             9,10,10,10,10,10,10,10, 9, 9,10,10,10,10,10,10,
            10,10,10,10,10,10,10,10,10,10, 9,10,10,10,10,10,
             9,10,10,10,10,10,10,10,10,10,10,10,10,10,10,10,
            10,10,10,10,10,10,10,10,10,10,10,10,10,10,10,10,
             9,10,10,10,10,10,10,10,10,10,10,10, 9, 9,10,10,
             9,10,10,10,10,10,10,10, 9,10,10,10, 9,10, 9, 5,
             6, 5, 5, 8, 9, 9, 9, 9, 9, 9,10,10,10, 9,10,10,
            10,10,10,10,10,10,10,10,10,10,10,10,10,10,10,10,
            10,10,10, 9,10, 9, 9, 9,10, 9,10, 9,10, 9,10, 9,
            10,10,10, 9,10, 9,10,10, 9, 9, 9, 6, 9, 9,10, 9,
             5,
        };

        private static readonly int[] SecondCodeLengths_1 = {
             4, 5, 6, 6, 7, 7, 6, 7, 7, 7, 6, 8, 7, 8, 8, 8,
             8, 9, 6, 9, 8, 9, 8, 9, 9, 9, 8,10, 5, 9, 7, 9,
             6, 9, 8,10, 9,10, 8, 8, 9, 9, 7, 9, 8, 9, 8, 9,
             8, 8, 6, 9, 9, 8, 8, 9, 9,10, 8, 9, 9,10, 8,10,
             8, 8, 8, 8, 8, 9, 7,10, 6, 9, 9,11, 7, 8, 8, 9,
             8,10, 7, 8, 6, 9,10, 9, 9,10, 8,11, 9,11, 9,10,
             9, 8, 9, 8, 8, 8, 8,10, 9, 9,10,10, 8, 9, 8, 8,
             8,11, 9, 8, 8, 9, 9,10, 8,11,10,10, 8,10, 9,10,
             8, 9, 9,11, 9,11, 9,10,10,11,10,12, 9,12,10,11,
            10,11, 9,10,10,11,10,11,10,11,10,11,10,10,10, 9,
             9, 9, 8, 7, 6, 8,11,11, 9,12,10,12, 9,11,11,11,
            10,12,11,11,10,12,10,11,10,10,10,11,10,11,11,11,
             9,12,10,12,11,12,10,11,10,12,11,12,11,12,11,12,
            10,12,11,12,11,11,10,12,10,11,10,12,10,12,10,12,
            10,11,11,11,10,11,11,11,10,12,11,12,10,10,11,11,
             9,12,11,12,10,11,10,12,10,11,10,12,10,11,10, 7,
             5, 4, 6, 6, 7, 7, 7, 8, 8, 7, 7, 6, 8, 6, 7, 7,
             9, 8, 9, 9,10,11,11,11,12,11,10,11,12,11,12,11,
            12,12,12,12,11,12,12,11,12,11,12,11,13,11,12,10,
            13,10,14,14,13,14,15,14,16,15,15,18,18,18, 9,18,
             8,
        };

        private static readonly int[] OffsetCodeLengths_1 = {       // 11 entries
             5, 6, 3, 3, 3, 3, 3, 3, 3, 4, 6,
        };

        private static readonly int[] FirstCodeLengths_2 = {
             4, 7, 7, 8, 7, 8, 8, 8, 8, 7, 8, 7, 8, 7, 9, 8,
             8, 8, 9, 9, 9, 9,10,10, 9,10,10,10,10,10, 9, 9,
             5, 9, 8, 9, 9,11,10, 9, 8, 9, 9, 9, 8, 9, 7, 8,
             8, 8, 9, 9, 9, 9, 9,10, 9, 9, 9,10, 9, 9,10, 9,
             8, 8, 7, 7, 7, 8, 8, 9, 8, 8, 9, 9, 8, 8, 7, 8,
             7,10, 8, 7, 7, 9, 9, 9, 9,10,10,11,11,11,10, 9,
             8, 6, 8, 7, 7, 5, 7, 7, 7, 6, 9, 8, 6, 7, 6, 6,
             7, 9, 6, 6, 6, 7, 8, 8, 8, 8, 9,10, 9,10, 9, 9,
             8, 9,10,10, 9,10,10, 9, 9,10,10,10,10,10,10,10,
             9,10,10,11,10,10,10,10,10,10,10,11,10,11,10,10,
             9,11,10,10,10,10,10,10, 9, 9,10,11,10,11,10,11,
            10,12,10,11,10,12,11,12,10,12,10,11,10,11,11,11,
             9,10,11,11,11,12,12,10,10,10,11,11,10,11,10,10,
             9,11,10,11,10,11,11,11,10,11,11,12,11,11,10,10,
            10,11,10,10,11,11,12,10,10,11,11,12,11,11,10,11,
             9,12,10,11,11,11,10,11,10,11,10,11, 9,10, 9, 7,
             3, 5, 6, 6, 7, 7, 8, 8, 8, 9, 9, 9,11,10,10,10,
            12,13,11,12,12,11,13,12,12,11,12,12,13,12,14,13,
            14,13,15,13,14,15,15,14,13,15,15,14,15,14,15,15,
            14,15,13,13,14,15,15,14,14,16,16,15,15,15,12,15,
            10,
        };

        private static readonly int[] SecondCodeLengths_2 = {
             5, 6, 6, 6, 6, 7, 7, 7, 7, 7, 7, 8, 7, 8, 7, 7,
             7, 8, 8, 8, 8, 9, 8, 9, 8, 9, 9, 9, 7, 9, 8, 8,
             6, 9, 8, 9, 8, 9, 8, 9, 8, 9, 8, 9, 8, 9, 8, 8,
             8, 8, 8, 9, 8, 9, 8, 9, 9,10, 8,10, 8, 9, 9, 8,
             8, 8, 7, 8, 8, 9, 8, 9, 7, 9, 8,10, 8, 9, 8, 9,
             8, 9, 8, 8, 8, 9, 9, 9, 9,10, 9,11, 9,10, 9,10,
             8, 8, 8, 9, 8, 8, 8, 9, 9, 8, 9,10, 8, 9, 8, 8,
             8,11, 8, 7, 8, 9, 9, 9, 9,10, 9,10, 9,10, 9, 8,
             8, 9, 9,10, 9,10, 9,10, 8,10, 9,10, 9,11,10,11,
             9,11,10,10,10,11, 9,11, 9,10, 9,11, 9,11,10,10,
             9,10, 9, 9, 8,10, 9,11, 9, 9, 9,11,10,11, 9,11,
             9,11, 9,11,10,11,10,11,10,11, 9,10,10,11,10,10,
             8,10, 9,10,10,11, 9,11, 9,10,10,11, 9,10,10, 9,
             9,10, 9,10, 9,10, 9,10, 9,11, 9,11,10,10, 9,10,
             9,11, 9,11, 9,11, 9,10, 9,11, 9,11, 9,11, 9,10,
             8,11, 9,10, 9,10, 9,10, 8,10, 8, 9, 8, 9, 8, 7,
             4, 4, 5, 6, 6, 6, 7, 7, 7, 7, 8, 8, 8, 7, 8, 8,
             9, 9,10,10,10,10,10,10,11,11,10,10,12,11,11,12,
            12,11,12,12,11,12,12,12,12,12,12,11,12,11,13,12,
            13,12,13,14,14,14,15,13,14,13,14,18,18,17, 7,16,
             9,
        };

        private static readonly int[] OffsetCodeLengths_2 = {       // 13 entries
             5, 6, 4, 4, 3, 3, 3, 3, 3, 4, 4, 4, 6,
        };

        private static readonly int[] FirstCodeLengths_3 = {
             6, 6, 6, 6, 6, 9, 8, 8, 4, 9, 8, 9, 8, 9, 9, 9,
             8, 9, 9,10, 8,10,10,10, 9,10,10,10, 9,10,10, 9,
             9, 9, 8,10, 9,10, 9,10, 9,10, 9,10, 9, 9, 8, 9,
             8, 9, 9, 9,10,10,10,10, 9, 9, 9,10, 9,10, 9, 9,
             7, 8, 8, 9, 8, 9, 9, 9, 8, 9, 9,10, 9, 9, 8, 9,
             8, 9, 8, 8, 8, 9, 9, 9, 9, 9,10,10,10,10,10, 9,
             8, 8, 9, 8, 9, 7, 8, 8, 9, 8,10,10, 8, 9, 8, 8,
             8,10, 8, 8, 8, 8, 9, 9, 9, 9,10,10,10,10,10, 9,
             7, 9, 9,10,10,10,10,10, 9,10,10,10,10,10,10, 9,
             9,10,10,10,10,10,10,10,10, 9,10,10,10,10,10,10,
             9,10,10,10,10,10,10,10, 9, 9, 9,10,10,10,10,10,
            10,10,10,10,10,10,10,10,10,10, 9,10,10,10,10, 9,
             8, 9,10,10,10,10,10,10,10,10,10,10, 9,10,10,10,
             9,10,10,10,10,10,10,10,10,10,10,10,10,10,10, 9,
             9,10,10,10,10,10,10, 9,10,10,10,10,10,10, 9, 9,
             9,10,10,10,10,10,10, 9, 9,10, 9, 9, 8, 9, 8, 9,
             4, 6, 6, 6, 7, 8, 8, 9, 9,10,10,10, 9,10,10,10,
            10,10,10,10,10,10,10,10,10,10,10,10,10,10, 7,10,
            10,10, 7,10,10, 7, 7, 7, 7, 7, 6, 7,10, 7, 7,10,
             7, 7, 7, 6, 7, 6, 6, 7, 7, 6, 6, 9, 6, 9,10, 6,
            10,
        };

        private static readonly int[] SecondCodeLengths_3 = {
             5, 6, 6, 6, 6, 7, 7, 7, 6, 8, 7, 8, 7, 9, 8, 8,
             7, 7, 8, 9, 9, 9, 9,10, 8, 9, 9,10, 8,10, 9, 8,
             6,10, 8,10, 8,10, 9, 9, 9, 9, 9,10, 9, 9, 8, 9,
             8, 9, 8, 9, 9,10, 9,10, 9, 9, 8,10, 9,11,10, 8,
             8, 8, 8, 9, 7, 9, 9,10, 8, 9, 8,11, 9,10, 9,10,
             8, 9, 9, 9, 9, 8, 9, 9,10,10,10,12,10,11,10,10,
             8, 9, 9, 9, 8, 9, 8, 8,10, 9,10,11, 8,10, 9, 9,
             8,12, 8, 9, 9, 9, 9, 8, 9,10, 9,12,10,10,10, 8,
             7,11,10, 9,10,11, 9,11, 7,11,10,12,10,12,10,11,
             9,11, 9,12,10,12,10,12,10, 9,11,12,10,12,10,11,
             9,10, 9,10, 9,11,11,12, 9,10, 8,12,11,12, 9,12,
            10,12,10,13,10,12,10,12,10,12,10, 9,10,12,10, 9,
             8,11,10,12,10,12,10,12,10,11,10,12, 8,12,10,11,
            10,10,10,12, 9,11,10,12,10,12,11,12,10, 9,10,12,
             9,10,10,12,10,11,10,11,10,12, 8,12, 9,12, 8,12,
             8,11,10,11,10,11, 9,10, 8,10, 9, 9, 8, 9, 8, 7,
             4, 3, 5, 5, 6, 5, 6, 6, 7, 7, 8, 8, 8, 7, 7, 7,
             9, 8, 9, 9,11, 9,11, 9, 8, 9, 9,11,12,11,12,12,
            13,13,12,13,14,13,14,13,14,13,13,13,12,13,13,12,
            13,13,14,14,13,13,14,14,14,14,15,18,17,18, 8,16,
            10,
        };

        private static readonly int[] OffsetCodeLengths_3 = {       // 14 entries
             6, 7, 4, 4, 3, 3, 3, 3, 3, 4, 4, 4, 5, 7,
        };

        private static readonly int[] FirstCodeLengths_4 = {
             2, 6, 6, 7, 7, 8, 7, 8, 7, 8, 8, 9, 8, 9, 9, 9,
             8, 8, 9, 9, 9,10,10, 9, 8,10, 9,10, 9,10, 9, 9,
             6, 9, 8, 9, 9,10, 9, 9, 9,10, 9, 9, 9, 9, 8, 8,
             8, 8, 8, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9,10,10, 9,
             7, 7, 8, 8, 8, 8, 9, 9, 7, 8, 9,10, 8, 8, 7, 8,
             8,10, 8, 8, 8, 9, 8, 9, 9,10, 9,11,10,11, 9, 9,
             8, 7, 9, 8, 8, 6, 8, 8, 8, 7,10, 9, 7, 8, 7, 7,
             8,10, 7, 7, 7, 8, 9, 9, 9, 9,10,11, 9,11,10, 9,
             7, 9,10,10,10,11,11,10,10,11,10,10,10,11,11,10,
             9,10,10,11,10,11,10,11,10,10,10,11,10,11,10,10,
             9,10,10,11,10,10,10,10, 9,10,10,10,10,11,10,11,
            10,11,10,11,11,11,10,12,10,11,10,11,10,11,11,10,
             8,10,10,11,10,11,11,11,10,11,10,11,10,11,11,11,
             9,10,11,11,10,11,11,11,10,11,11,11,10,10,10,10,
            10,11,10,10,11,11,10,10, 9,11,10,10,11,11,10,10,
            10,11,10,10,10,10,10,10, 9,11,10,10, 8,10, 8, 6,
             5, 6, 6, 7, 7, 8, 8, 8, 9,10,11,10,10,11,11,12,
            12,10,11,12,12,12,12,13,13,13,13,13,12,13,13,15,
            14,12,14,15,16,12,12,13,15,14,16,15,17,18,15,17,
            16,15,15,15,15,13,13,10,14,12,13,17,17,18,10,17,
             4,
        };

        private static readonly int[] SecondCodeLengths_4 = {
             4, 5, 6, 6, 6, 6, 7, 7, 6, 7, 7, 9, 6, 8, 8, 7,
             7, 8, 8, 8, 6, 9, 8, 8, 7, 9, 8, 9, 8, 9, 8, 9,
             6, 9, 8, 9, 8,10, 9, 9, 8,10, 8,10, 8, 9, 8, 9,
             8, 8, 7, 9, 9, 9, 9, 9, 8,10, 9,10, 9,10, 9, 8,
             7, 8, 9, 9, 8, 9, 9, 9, 7,10, 9,10, 9, 9, 8, 9,
             8, 9, 8, 8, 8, 9, 9,10, 9, 9, 8,11, 9,11,10,10,
             8, 8,10, 8, 8, 9, 9, 9,10, 9,10,11, 9, 9, 9, 9,
             8, 9, 8, 8, 8,10,10, 9, 9, 8,10,11,10,11,11, 9,
             8, 9,10,11, 9,10,11,11, 9,12,10,10,10,12,11,11,
             9,11,11,12, 9,11, 9,10,10,10,10,12, 9,11,10,11,
             9,11,11,11,10,11,11,12, 9,10,10,12,11,11,10,11,
             9,11,10,11,10,11, 9,11,11, 9, 8,11,10,11,11,10,
             7,12,11,11,11,11,11,12,10,12,11,13,11,10,12,11,
            10,11,10,11,10,11,11,11,10,12,11,11,10,11,10,10,
            10,11,10,12,11,12,10,11, 9,11,10,11,10,11,10,12,
             9,11,11,11, 9,11,10,10, 9,11,10,10, 9,10, 9, 7,
             4, 5, 5, 5, 6, 6, 7, 6, 8, 7, 8, 9, 9, 7, 8, 8,
            10, 9,10,10,12,10,11,11,11,11,10,11,12,11,11,11,
            11,11,13,12,11,12,13,12,12,12,13,11, 9,12,13, 7,
            13,11,13,11,10,11,13,15,15,12,14,15,15,15, 6,15,
             5,
        };

        private static readonly int[] OffsetCodeLengths_4 = {       // 11 entries
             3, 6, 5, 4, 2, 3, 3, 3, 4, 4, 6,
        };

        private static readonly int[] FirstCodeLengths_5 = {
             7, 9, 9, 9, 9, 9, 9, 9, 9, 8, 9, 9, 9, 7, 9, 9,
             9, 9, 9, 9, 9, 9, 9,10, 9,10, 9,10, 9,10, 9, 9,
             5, 9, 7, 9, 9, 9, 9, 9, 7, 7, 7, 9, 7, 7, 8, 7,
             8, 8, 7, 7, 9, 9, 9, 9, 7, 7, 7, 9, 9, 9, 9, 9,
             9, 7, 9, 7, 7, 7, 7, 9, 9, 7, 9, 9, 7, 7, 7, 7,
             7, 9, 7, 8, 7, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9,
             9, 7, 8, 7, 7, 7, 8, 8, 6, 7, 9, 7, 7, 8, 7, 5,
             6, 9, 5, 7, 5, 6, 7, 7, 9, 8, 9, 9, 9, 9, 9, 9,
             9, 9,10, 9,10,10,10, 9, 9,10,10,10,10,10,10,10,
             9,10,10,10,10,10,10,10,10,10,10,10, 9,10,10,10,
             9,10,10,10, 9, 9,10, 9, 9, 9, 9,10,10,10,10,10,
            10,10,10,10,10,10, 9,10,10,10,10,10,10,10,10,10,
             9,10,10,10, 9,10,10,10, 9, 9, 9,10,10,10,10,10,
             9,10, 9,10,10, 9,10,10, 9,10,10,10,10,10,10,10,
             9,10,10,10,10,10,10,10,10,10,10,10,10,10,10,10,
             9,10,10,10,10,10,10,10, 9,10, 9,10, 9,10,10, 9,
             5, 6, 8, 8, 7, 7, 7, 9, 9, 9, 9, 9, 9, 9, 9, 9,
             9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9,
             9,10,10,10,10,10,10,10,10,10,10,10,10,10,10,10,
            10,10,10,10,10,10,10,10, 9,10,10, 5,10, 8, 9, 8,
             9,
        };

        private static readonly int[] SecondCodeLengths_5 = {
             8,10,11,11,11,12,11,11,12, 6,11,12,10, 5,12,12,
            12,12,12,12,12,13,13,14,13,13,12,13,12,13,12,15,
             4,10, 7, 9,11,11,10, 9, 6, 7, 8, 9, 6, 7, 6, 7,
             8, 7, 7, 8, 8, 8, 8, 8, 8, 9, 8, 7,10, 9,10,10,
            11, 7, 8, 6, 7, 8, 8, 9, 8, 7,10,10, 8, 7, 8, 8,
             7,10, 7, 6, 7, 9, 9, 8,11,11,11,10,11,11,11, 8,
            11, 6, 7, 6, 6, 6, 6, 8, 7, 6,10, 9, 6, 7, 6, 6,
             7,10, 6, 5, 6, 7, 7, 7,10, 8,11, 9,13, 7,14,16,
            12,14,14,15,15,16,16,14,15,15,15,15,15,15,15,15,
            14,15,13,14,14,16,15,17,14,17,15,17,12,14,13,16,
            12,17,13,17,14,13,13,14,14,12,13,15,15,14,15,17,
            14,17,15,14,15,16,12,16,15,14,15,16,15,16,17,17,
            15,15,17,17,13,14,15,15,13,12,16,16,17,14,15,16,
            15,15,13,13,15,13,16,17,15,17,17,17,16,17,14,17,
            14,16,15,17,15,15,14,17,15,17,15,16,15,15,16,16,
            14,17,17,15,15,16,15,17,15,14,16,16,16,16,16,12,
             4, 4, 5, 5, 6, 6, 6, 7, 7, 7, 8, 8, 8, 8, 9, 9,
             9, 9, 9,10,10,10,11,10,11,11,11,11,11,12,12,12,
            13,13,12,13,12,14,14,12,13,13,13,13,14,12,13,13,
            14,14,14,13,14,14,15,15,13,15,13,17,17,17, 9,17,
             7,
        };

        private static readonly int[] OffsetCodeLengths_5 = {       // 11 entries
             6, 7, 7, 6, 4, 3, 2, 2, 3, 3, 6,
        };

        private static readonly int[][] FirstCodeLengths = [
            FirstCodeLengths_1,
            FirstCodeLengths_2,
            FirstCodeLengths_3,
            FirstCodeLengths_4,
            FirstCodeLengths_5,
        ];
        private static readonly int[][] SecondCodeLengths = [
            SecondCodeLengths_1,
            SecondCodeLengths_2,
            SecondCodeLengths_3,
            SecondCodeLengths_4,
            SecondCodeLengths_5,
        ];
        private static readonly int[][] OffsetCodeLengths = [       // lengths vary
            OffsetCodeLengths_1,
            OffsetCodeLengths_2,
            OffsetCodeLengths_3,
            OffsetCodeLengths_4,
            OffsetCodeLengths_5,
        ];

        private static readonly int[] MetaCodes = {                 // 37 entries
            0x5d8,0x058,0x040,0x0c0,0x000,0x078,0x02b,0x014,
            0x00c,0x01c,0x01b,0x00b,0x010,0x020,0x038,0x018,
            0x0d8,0xbd8,0x180,0x680,0x380,0xf80,0x780,0x480,
            0x080,0x280,0x3d8,0xfd8,0x7d8,0x9d8,0x1d8,0x004,
            0x001,0x002,0x007,0x003,0x008
        };

        private static readonly int[] MetaCodeLengths = {           // 37 entries
            11,8,8,8,8,7,6,5,5,5,5,6,5,6,7,7,9,12,10,11,11,12,
            12,11,11,11,12,12,12,12,12,5,2,2,3,4,5
        };

        //
        // We represent binary trees in an Nx2 array.  The root is at node zero, and we fill
        // in the rest linearly by walking from top to bottom, left to right.  References to
        // other nodes are stored as an array index, with -1 used to indicate a null reference
        // (newly-allocated nodes hold [-1,-1]).  For a leaf index, we set both entries to the
        // value, so "left == right" indicates a leaf.
        //
        // The code length arrays have 321 elements, so we should need at most (N*2-1)=641
        // elements to hold a tree.  The longest code length in the fixed trees is 18 bits,
        // but encodings read from files could potentially be larger, so using a sparse array
        // representation could span a lot of memory.
        //

        /// <summary>
        /// Number of symbols in the literal/length table.
        /// </summary>
        private const int LIT_LEN_SYM_COUNT = 321;

        /// <summary>
        /// Maximum number of elements we expect to have in the tree.
        /// </summary>
        private const int MAX_TREE_COUNT = LIT_LEN_SYM_COUNT * 2 - 1;

        /// <summary>
        /// Maximum allows length of a Huffman code.
        /// </summary>
        private const int MAX_CODE_LENGTH = 32;     // should be 24, to work with bit rd/wr?

        /// <summary>
        /// A null reference in the tree.
        /// </summary>
        private const int NULL_REF = -1;

        /// <summary>
        /// Generates Huffman decoder trees for the fixed table specified by index.
        /// </summary>
        /// <param name="index">Table index (1-5).</param>
        /// <param name="firstCode">Result: first code tree.</param>
        /// <param name="secondCode">Result: second code tree.</param>
        /// <param name="offsetCode">Result: offset code tree.</param>
        private static void GenerateTrees(int index, out int[,] firstCode, out int[,] secondCode,
                out int[,] offsetCode) {
            if (index < 1 || index > 5) {
                throw new Exception("Unexpected index " + index);
            }
            firstCode = GenerateTreeFromLengths(FirstCodeLengths[index - 1]);
            secondCode = GenerateTreeFromLengths(SecondCodeLengths[index - 1]);
            offsetCode = GenerateTreeFromLengths(OffsetCodeLengths[index - 1]);
        }

        /// <summary>
        /// Generates a Huffman decoder tree for a set of symbols, given the lengths for each.
        /// </summary>
        /// <remarks>
        /// The standard description of this algorithm involves sorting the list of symbols
        /// by increasing length, but in our case the symbol is implied by the array index.
        /// Instead, we iterate through the array multiple times.  Should be fast enough.
        /// </remarks>
        /// <param name="lengths">Length of each symbol's encoding, in bits.  Max 32.</param>
        /// <returns>The decoder tree, as a two-dimensional array (left/right ref).</returns>
        private static int[,] GenerateTreeFromLengths(int[] lengths) {
            const int ROOT_NODE = 0;
            int symbolsLeft = lengths.Length;

            // This will hold the tree.
            int[,] tree = new int[MAX_TREE_COUNT,2];

            // Index of next free node.  Node 0 is the root.
            int nextIndex = 1;
            tree[ROOT_NODE, 0] = tree[ROOT_NODE, 1] = NULL_REF;

            // Huffman code for next symbol.
            int code = 0;

            // Find all symbols of a given length, starting with the shortest.
            for (int length = 1; length <= MAX_CODE_LENGTH; length++) {
                // Search through all entries in the array.
                for (int idx = 0; idx < lengths.Length; idx++) {
                    if (lengths[idx] != length) {
                        continue;
                    }
                    // Walk the tree, starting at the root.  The "code" variable is effectively
                    // MSBit-first, so we want to process bits from the left, not the LSBit.
                    // If we step out of the tree, create a new node.
                    int nodeIndex = ROOT_NODE;
                    for (int bitPos = length - 1; bitPos >= 0; bitPos--) {
                        int bit = (code >> bitPos) & 1;
                        if (tree[nodeIndex, bit] == NULL_REF) {
                            // Create a new child node, and set the reference to it.
                            tree[nextIndex, 0] = tree[nextIndex, 1] = NULL_REF;
                            tree[nodeIndex, bit] = nextIndex;
                            nextIndex++;
                        }
                        nodeIndex = tree[nodeIndex, bit];       // move down
                    }
                    // Reached the leaf.
                    if (tree[nodeIndex, 0] != NULL_REF || tree[nodeIndex, 1] != NULL_REF) {
                        throw new Exception("Leaf is not");
                    }
                    // References and values are both small positive integers.  We know it's a
                    // leaf because they're both set to the same thing.
                    tree[nodeIndex, 0] = tree[nodeIndex, 1] = idx;

                    code++;

                    // If we've processed all symbols, bail.
                    if (--symbolsLeft == 0) {
                        return tree;
                    }
                }
                code <<= 1;
            }

            // We won't get here for hard-coded trees, which contain all symbols, but trees from
            // file headers are likely incomplete.
            return tree;
        }


        /// <summary>
        /// Generates a Huffman decoder tree for a given set of codes.  Codes are described
        /// with an LSB-first bit pattern and a length value.
        /// </summary>
        private static int[,] GenerateTreeFromCodes(int[] codes, int[] lengths) {
            Debug.Assert(codes.Length == lengths.Length);

            const int ROOT_NODE = 0;

            // This will hold the tree.
            int[,] tree = new int[codes.Length * 2 - 1, 2];

            // Index of next free node.  Node 0 is the root.
            int nextIndex = 1;
            tree[ROOT_NODE, 0] = tree[ROOT_NODE, 1] = NULL_REF;

            for (int idx = 0; idx < codes.Length; idx++) {
                int length = lengths[idx];
                Debug.Assert(length > 0);
                int code = codes[idx];
                int nodeIndex = ROOT_NODE;
                for (int bitPos = 0; bitPos < length; bitPos++) {
                    int bit = (code >> bitPos) & 0x01;
                    if (tree[nodeIndex, bit] == NULL_REF) {
                        // Create a new child node, and set the reference to it.
                        tree[nextIndex, 0] = tree[nextIndex, 1] = NULL_REF;
                        tree[nodeIndex, bit] = nextIndex;
                        nextIndex++;
                    }
                    nodeIndex = tree[nodeIndex, bit];
                }
                // Reached the leaf.
                if (tree[nodeIndex, 0] != NULL_REF || tree[nodeIndex, 1] != NULL_REF) {
                    throw new Exception("Leaf is not");
                }
                tree[nodeIndex, 0] = tree[nodeIndex, 1] = idx;
            }

            return tree;
        }

        /// <summary>
        /// Generates Huffman codes for a set of symbols, given the lengths for each.  Useful
        /// when encoding.
        /// </summary>
        /// <param name="lengths">Length of each symbol's encoding, in bits.  Max 32.</param>
        /// <returns>An array of code patterns.</returns>
        private static uint[] GenerateCodesFromLengths(int[] lengths) {
            uint[] codes = new uint[lengths.Length];
            int symbolsLeft = lengths.Length;
            // Huffman code for next symbol.
            int code = 0;

            // Find all symbols of a given length, starting with the shortest.
            for (int length = 1; length <= MAX_CODE_LENGTH; length++) {
                // Search through all entries in the array.
                for (int idx = 0; idx < lengths.Length; idx++) {
                    if (lengths[idx] != length) {
                        continue;
                    }
                    // Generate the next available code with the current length.  Our variable
                    // is effectively MSBit-first, so we need to flip it around.
                    uint rev = BitTwiddle.ReverseBits((uint)code) >> (32 - length);
                    codes[idx] = (uint)rev;

                    code++;

                    // If we've processed all symbols, bail.
                    if (--symbolsLeft == 0) {
                        return codes;
                    }
                }
                code <<= 1;
            }

            Debug.Assert(false, "shouldn't be here for fixed tables");
            return codes;
        }

        #endregion Code Tables
    }
}
