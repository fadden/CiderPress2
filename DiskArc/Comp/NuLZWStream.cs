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
using System.IO.Compression;

using CommonUtil;

namespace DiskArc.Comp {
    /// <summary>
    /// NuFX RLE + LZW compression.
    /// </summary>
    /// <remarks>
    /// <para>The compression is performed on 4KB chunks.  If the file isn't a multiple of 4096,
    /// the last chunk is padded with zeroes.  There is no indication of the original end of
    /// file in the compressed data, so the uncompressed length must be provided when
    /// decompressing.</para>
    /// </remarks>
    public class NuLZWStream : Stream {
        private const int CHUNK_SIZE = 4096;
        private const int DEFAULT_VOL_NUM = 254;
        private const int DEFAULT_RLE_DELIM = 0xdb;
        private const int RLE_OUTPUT_BUF_LEN = CHUNK_SIZE + 8;      // allow a little overflow
        private const int LZW_OUTPUT_BUF_LEN = CHUNK_SIZE + 8;      // allow a little overflow
        private const int CHUNK_INPUT_BUF_LEN = CHUNK_SIZE + 4 + 2; // chunk + header + overflow
        private const int CHUNK_OUTPUT_BUF_LEN = CHUNK_SIZE;
        private const int LZW_EXP_TABLE_SIZE = 4096;

        private const int LZW_START_BITS = 9;
        private const int LZW_MAX_BITS = 12;
        private const int LZW_TABLE_CLEAR = 0x0100;
        private const int LZW_FIRST_CODE = 0x0101;
        private const int LZW_LAST_CODE = 0x0ffd;           // clear the table when LZW/2 gets here

        // Hash table size for 12-bit codes.  Prime.  Yields (4095-256)/5199 = 74% occupancy.
        private const int LZW_HASH_TABLE_SIZE = 0x13ff;


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
        /// 5.25" volume number.  When compressing, the caller should set it immediately after
        /// creating the object.  When decompressing, this will be valid after end-of-file
        /// is returned.
        /// </summary>
        public byte VolumeNum { get; set; }

        /// <summary>
        /// NuFX LZW/1 or LZW/2?
        /// </summary>
        private bool mIsType2;

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
        ///   at the start.</param>
        /// <param name="mode">Flag that indicates whether we will be compressing or
        ///   expanding data.</param>
        /// <param name="leaveOpen">Flag that determines whether we close the compressed
        ///   data stream when we are disposed.</param>
        /// <param name="isType2">False for LZW/1, true for LZW/2.</param>
        /// <param name="expandedLength">Length of uncompressed data, when expanding.  When
        ///   compressing, pass -1.</param>
        public NuLZWStream(Stream compDataStream, CompressionMode mode, bool leaveOpen,
                bool isType2, long expandedLength) {
            mCompDataStream = compDataStream;
            mCompressionMode = mode;
            mLeaveOpen = leaveOpen;
            mIsType2 = isType2;

            if (mode == CompressionMode.Compress) {
                Debug.Assert(compDataStream.CanWrite);
                InitCompress();
            } else if (mode == CompressionMode.Decompress) {
                Debug.Assert(compDataStream.CanRead);
                InitExpand(expandedLength);
            } else {
                throw new ArgumentException("Invalid mode: " + mode);
            }
        }

        // Note:
        //  - Stream.Dispose() calls Close()
        //  - Stream.Close() calls Dispose(true) and GC.SuppressFinalize(this)

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

        // Pre-computed hash function.
        private static int[] sHashFunc = GenerateHashCodes();

        // Buffer used to hold entire output file for LZW/1.
        private MemoryStream? mCompLZW1Out;

        // Buffer used to accumulate a 4KB chunk of input data.
        private byte[]? mCompInBuf;
        private int mCompInLength;

        // Work buffers for compression code.
        private byte[]? mCompRLEOutBuf;
        private byte[]? mCompLZWOutBuf;

        private bool mCompHeaderWritten;
        private ushort mCompCrc;

        // LZW state.
        private struct LZWHash {
            public int mEntryCode;      // table entry index, 0x0101 - 0x0fff
            public int mPrefixCode;     // index of prefix string
            public byte mSuffix;        // suffix character
        }
        private LZWHash[]? mCompHashTable;

        // Compression state, preserved between blocks for LZW/2.
        private int mCompLZWNextCode;
        private int mCompLZWBitLen;
        private bool mCompLZWNeedInitialClear;


        /// <summary>
        /// Prepares object for compression.
        /// </summary>
        private void InitCompress() {
            // Buffer used while accumulating 4KB of input.
            mCompInBuf = new byte[CHUNK_SIZE];
            mCompInLength = 0;

            if (!mIsType2) {
                // Buffer for LZW/1 output.
                mCompLZW1Out = new MemoryStream();
            }
            mCompCrc = 0x0000;

            // Work buffers for RLE and LZW compression output.
            mCompRLEOutBuf = new byte[RLE_OUTPUT_BUF_LEN];
            mCompLZWOutBuf = new byte[LZW_OUTPUT_BUF_LEN];

            VolumeNum = DEFAULT_VOL_NUM;

            mCompHashTable = new LZWHash[LZW_HASH_TABLE_SIZE];
            ClearLZWTable();
        }

        /// <summary>
        /// Receives uncompressed data to be compressed.  (Stream call.)
        /// </summary>
        public override void Write(byte[] data, int offset, int length) {
            Debug.Assert(mCompInBuf != null);

            if (!mCompHeaderWritten) {
                WriteHeader();
                mCompHeaderWritten = true;
            }

            while (length != 0) {
                // Assemble a 4KB chunk, and process it.  The partial block at the end of the
                // file is handled when this object is closed.
                int copyLen = Math.Min(length, CHUNK_SIZE - mCompInLength);
                Array.Copy(data, offset, mCompInBuf, mCompInLength, copyLen);
                offset += copyLen;
                length -= copyLen;
                mCompInLength += copyLen;

                if (mCompInLength == CHUNK_SIZE) {
                    CompressChunk();
                    mCompInLength = 0;
                }
            }
        }

        /// <summary>
        /// Generates the two- or four-byte file header.
        /// </summary>
        private void WriteHeader() {
            if (mIsType2) {
                mCompDataStream.WriteByte(VolumeNum);
                mCompDataStream.WriteByte(DEFAULT_RLE_DELIM);
            } else {
                Debug.Assert(mCompLZW1Out != null);
                RawData.WriteU16LE(mCompLZW1Out, 0xcccd);    // leave space for CRC-16
                mCompLZW1Out.WriteByte(VolumeNum);
                mCompLZW1Out.WriteByte(DEFAULT_RLE_DELIM);
            }
        }

        /// <summary>
        /// Finishes compression operation.  This method is called when this object is closed,
        /// indicating that all data has been provided.
        /// </summary>
        private void FinishCompression() {
            Debug.Assert(mCompInBuf != null);

            // This should only happen for zero-length input.
            if (!mCompHeaderWritten) {
                WriteHeader();
                mCompHeaderWritten = true;
            }

            if (mCompInLength != 0) {
                // Handle final block.  Zero-fill the unused space.
                for (int i = mCompInLength; i < CHUNK_SIZE; i++) {
                    mCompInBuf[i] = 0x00;
                }

                CompressChunk();
            }

            if (!mIsType2) {
                // Set the CRC at the start of the file, then copy the whole thing out.
                RawData.SetU16LE(mCompLZW1Out!.GetBuffer(), 0, mCompCrc);
                mCompLZW1Out.Position = 0;
                mCompLZW1Out.CopyTo(mCompDataStream);
                mCompDataStream.Flush();
            }
        }

        /// <summary>
        /// Compresses a 4KB chunk of data.
        /// </summary>
        private void CompressChunk() {
            Debug.Assert(mCompInBuf != null);
            if (!mIsType2) {
                // Compute a CRC on the entire chunk, including the zero-padding.
                mCompCrc = CRC16.XMODEM_OnBuffer(mCompCrc, mCompInBuf, 0, CHUNK_SIZE);
            }

            Debug.Assert(mCompRLEOutBuf != null);

            byte[] lzwSrcBuf;
            int lzwSrcLen;

            int rleOutLen = CompressRLE(mCompInBuf, 0);
            if (rleOutLen >= CHUNK_SIZE) {
                // Compress LZW from the uncompressed data.
                lzwSrcBuf = mCompInBuf;
                lzwSrcLen = CHUNK_SIZE;
            } else {
                // Compress LZW from the RLE output.
                lzwSrcBuf = mCompRLEOutBuf;
                lzwSrcLen = rleOutLen;
            }

            int lzwOutLen = CompressLZW(lzwSrcBuf, 0, lzwSrcLen);
            int afterRle = (rleOutLen < CHUNK_SIZE) ? rleOutLen : CHUNK_SIZE;   // clamp to 4KB
            byte[] compOutputBuf;
            int compOutputLen;

            if (mIsType2) {
                if (lzwOutLen + 2 < rleOutLen) {     // LZW/2 adds 2 bytes to LZW output
                    // Use LZW output.
                    RawData.WriteU16LE(mCompDataStream, (ushort)(afterRle | 0x8000));
                    RawData.WriteU16LE(mCompDataStream, (ushort)(lzwOutLen + 4));
                    compOutputBuf = mCompLZWOutBuf!;
                    compOutputLen = lzwOutLen;
                } else if (rleOutLen < CHUNK_SIZE) {
                    // Use RLE output.
                    RawData.WriteU16LE(mCompDataStream, (ushort)afterRle);
                    compOutputBuf = mCompRLEOutBuf;
                    compOutputLen = rleOutLen;
                    ClearLZWTable();
                } else {
                    // Use original data.
                    RawData.WriteU16LE(mCompDataStream, (ushort)afterRle);
                    compOutputBuf = mCompInBuf;
                    compOutputLen = CHUNK_SIZE;
                    ClearLZWTable();
                }
                mCompDataStream.Write(compOutputBuf, 0, compOutputLen);
            } else {
                Debug.Assert(mCompLZW1Out != null);
                RawData.WriteU16LE(mCompLZW1Out, (ushort)afterRle);

                if (lzwOutLen < rleOutLen) {
                    // Use LZW output.
                    mCompLZW1Out.WriteByte(1);       // set "LZW used" flag
                    compOutputBuf = mCompLZWOutBuf!;
                    compOutputLen = lzwOutLen;
                } else if (rleOutLen < CHUNK_SIZE) {
                    // Use RLE output.
                    mCompLZW1Out.WriteByte(0);
                    compOutputBuf = mCompRLEOutBuf;
                    compOutputLen = rleOutLen;
                } else {
                    // Use original data.
                    mCompLZW1Out.WriteByte(0);
                    compOutputBuf = mCompInBuf;
                    compOutputLen = CHUNK_SIZE;
                }
                mCompLZW1Out.Write(compOutputBuf, 0, compOutputLen);

                // Reset this every time.
                ClearLZWTable();
            }
        }

        /// <summary>
        /// Compresses data with RLE.  Returns the length of the compressed data.  If compression
        /// fails, we may return early, with a value >= CHUNK_SIZE.
        /// </summary>
        private int CompressRLE(byte[] srcBuf, int srcOffset) {
            Debug.Assert(mCompRLEOutBuf != null);
            int endOffset = srcOffset + CHUNK_SIZE;
            int outOffset = 0;

            byte matchVal;
            int matchCount;

            while (srcOffset < endOffset) {
                matchVal = srcBuf[srcOffset++];
                matchCount = 1;
                while (srcOffset < endOffset && srcBuf[srcOffset] == matchVal && matchCount < 256) {
                    matchCount++;
                    srcOffset++;
                }
                if (matchCount > 3) {
                    mCompRLEOutBuf[outOffset++] = DEFAULT_RLE_DELIM;
                    mCompRLEOutBuf[outOffset++] = matchVal;
                    mCompRLEOutBuf[outOffset++] = (byte)(matchCount - 1);
                } else {
                    if (matchVal == DEFAULT_RLE_DELIM) {
                        // Encode 1-3 0xDBs.
                        mCompRLEOutBuf[outOffset++] = DEFAULT_RLE_DELIM;
                        mCompRLEOutBuf[outOffset++] = matchVal;
                        mCompRLEOutBuf[outOffset++] = (byte)(matchCount - 1);
                    } else {
                        while (matchCount-- != 0) {
                            mCompRLEOutBuf[outOffset++] = matchVal;
                        }
                    }
                }

                if (outOffset >= CHUNK_SIZE) {
                    // Compression has failed, give up now.
                    return CHUNK_SIZE;
                }
            }

            return outOffset;
        }

        /// <summary>
        /// Clears the LZW hash table and resets the output codes.
        /// </summary>
        private void ClearLZWTable() {
            Debug.Assert(mCompHashTable != null);
            for (int i = 0; i < mCompHashTable.Length; i++) {
                mCompHashTable[i].mEntryCode = 0;
            }
            mCompLZWNextCode = LZW_FIRST_CODE;
            mCompLZWBitLen = LZW_START_BITS;
            mCompLZWNeedInitialClear = false;
        }

        /// <summary>
        /// Compresses data with LZW.  Returns the length of the compressed data.  If compression
        /// fails, we may return early, with a value >= CHUNK_SIZE.
        /// </summary>
        private int CompressLZW(byte[] srcBuf, int srcOffset, int srcLen) {
            //
            // The basic idea is to gather strings into a tree (technically a "prefix tree" or
            // "trie") in which each node has up to 256 children, one per possible byte value.
            // While reading input, we start at the top of the tree, and traverse to the
            // appropriate child.  We repeat this for subsequent bytes of input until we reach
            // a point where there is no child.  We then output the index of the current node,
            // and create a new child node.  As we see the same sequences over and over, the
            // tree gets taller and taller.
            //
            // Doing it with a full 256-way tree structure would cost a fair bit of memory, so
            // instead we use a hash table.  The node index for the part of the sequence we
            // recognized is combined with the new byte value to get the hash table index.  If
            // the values in the hash table match, we have the node; if they don't, we have a
            // collision, and need to probe for the next entry.
            //
            // We never really output literals, only node indices.  The first 256 node indices
            // just happen to represent the values a byte can hold.
            //

            Debug.Assert(mCompHashTable != null);
            const int adjbits = LZW_MAX_BITS - 10;
            int srcEndOffset = srcOffset + srcLen;
            int dstOffset = 0;
            int atBit = 0;
            bool needSpecialClear = false;

            if (mCompLZWNeedInitialClear) {
                // Output a table clear code, then clear the table.
                PutLZWCode(LZW_TABLE_CLEAR, mCompLZWBitLen, ref dstOffset, ref atBit);
                ClearLZWTable();
            }

        start:      // jump back here after a table clear
            int nextCode = mCompLZWNextCode;
            int bitLen = mCompLZWBitLen;
            int highCode = (1 << bitLen) - 1;

            // The first prefix tree index we output is simply the first byte.
            int prefixCode = srcBuf[srcOffset++];
            while (srcOffset < srcEndOffset) {
                // Get the next byte of input.
                byte ic = srcBuf[srcOffset++];

                // Combine the prefix code and suffix character into a single hash index.
                int hashIndex = prefixCode ^ sHashFunc[ic];         // [0,4095]
                int code = mCompHashTable[hashIndex].mEntryCode;
                if (code != 0) {
                    // We found an in-use entry; could be the matching entry, or a hash table
                    // collision.  If the latter, we need to probe until we find a match or
                    // an empty space.
                    while (mCompHashTable[hashIndex].mPrefixCode != prefixCode ||
                            mCompHashTable[hashIndex].mSuffix != ic) {
                        int hashDelta = (0x120 - ic) << adjbits;
                        if (hashIndex >= hashDelta) {
                            hashIndex -= hashDelta;
                        } else {
                            hashIndex += LZW_HASH_TABLE_SIZE - hashDelta;
                        }
                        code = mCompHashTable[hashIndex].mEntryCode;
                        if (code == 0) {
                            // Found an unused entry.
                            break;
                        }
                    }

                    if (code != 0) {
                        // Found a matching string.  Continue our descent.
                        prefixCode = code;
                        continue;
                    }
                }

                // Found an unused entry.  Write the prefix to the output file, then add the
                // prefix/suffix combo to the hash table.
                if (!PutLZWCode(prefixCode, bitLen, ref dstOffset, ref atBit)) {
                    return CHUNK_SIZE;
                }
                mCompHashTable[hashIndex].mEntryCode = nextCode;
                mCompHashTable[hashIndex].mPrefixCode = prefixCode;
                mCompHashTable[hashIndex].mSuffix = ic;

                // Time to increase the output code width?  (This is actually one iteration too
                // early.  This is apparently common across enough LZW implementations to have
                // been semi-officially named "early change".)
                if (nextCode == highCode) {
                    highCode += nextCode + 1;
                    bitLen++;
                }
                nextCode++;

                // Start the next string search with the last character we read.
                prefixCode = ic;

                // If the table is full, clear it.  We're leaving the last entry unused, but
                // experiments show it doesn't much matter, and I'd like to mimic GS/ShrinkIt.
                //
                // This only applies to LZW/2, as you can't fill the table with 4KB of input
                // without first overflowing the output buffer.
                if (nextCode > LZW_LAST_CODE) {
                    // Output the pending prefix code.
                    if (!PutLZWCode(prefixCode, bitLen, ref dstOffset, ref atBit)) {
                        return CHUNK_SIZE;
                    }

                    if (srcOffset < srcEndOffset) {
                        // More input pending.  Output the clear, reset state, and continue.
                        PutLZWCode(LZW_TABLE_CLEAR, bitLen, ref dstOffset, ref atBit);
                        ClearLZWTable();
                        // If you don't like "goto", just copy the assignments here.
                        goto start;
                    } else {
                        // Input is empty.  We can't output a clear code now, because the
                        // expander stops as soon as it has produced enough *output*, not when
                        // it consumes all the *input*.  We need to hold this for the next chunk.
                        Debug.WriteLine("Hit rare block-end clear #1");
                        needSpecialClear = true;
                        PutLZWCode(0, 0, ref dstOffset, ref atBit);     // flush bits

                        // Input is done, so we'll fall out of "while" loop.
                    }
                }
            }

            // Input done.
            if (!needSpecialClear) {
                // Output the last code.  We don't need to add a new entry to the table, because
                // we stopped before we found the end of the prefix string.  We do, however, want
                // to leave a space in the table to make things simpler on the expansion side.
                PutLZWCode(prefixCode, bitLen, ref dstOffset, ref atBit);

                PutLZWCode(0, 0, ref dstOffset, ref atBit);             // flush bits

                // Advance to the next entry.
                if (nextCode == highCode) {
                    //highCode += nextCode + 1;
                    bitLen++;
                }
                nextCode++;

                // Whoops, ran out of space in the table.  Set the flag to clear the table on
                // the next call.
                if (nextCode > LZW_LAST_CODE) {
                    Debug.WriteLine("Hit rare block-end clear #2");
                    needSpecialClear = true;
                }
            }

            mCompLZWNextCode = nextCode;
            mCompLZWBitLen = bitLen;
            mCompLZWNeedInitialClear = needSpecialClear;

            return dstOffset;
        }

        /// <summary>
        /// Outputs a 9-12 bit LZW code.  Pass a zero value for "bitLen" to signal end-of-input.
        /// </summary>
        /// <remarks>
        /// The LZW output buffer is slightly oversized, so we can write multiple bytes here
        /// and only range-check at the end.
        /// </remarks>
        /// <param name="code">Code to output, in the low bits.</param>
        /// <param name="bitLen">Number of valid bits in "code".</param>
        /// <param name="dstOffset">Output byte offset.</param>
        /// <param name="atBit">Output bit offset.</param>
        /// <returns>True if all is well, false if we've overrun the output buffer.</returns>
        private bool PutLZWCode(int code, int bitLen, ref int dstOffset, ref int atBit) {
            Debug.Assert(mCompLZWOutBuf != null);

            //Debug.WriteLine("code=0x" + code.ToString("x4") + " bits=" + bitLen + " at=" + atBit);

            if (bitLen == 0) {
                // Flush bits and return.
                if (atBit != 0) {
                    dstOffset++;
                    atBit = 0;
                }
                return dstOffset < CHUNK_SIZE;
            }

            if (atBit != 0) {
                // Combine with current buffer contents.
                code <<= atBit;
                code |= mCompLZWOutBuf[dstOffset];
            }
            mCompLZWOutBuf[dstOffset++] = (byte)code;

            // All codes are at least 9 bits, so we know we need at least one more.
            mCompLZWOutBuf[dstOffset] = (byte)(code >> 8);

            atBit += bitLen;
            // atBit was [0,7], bitLen is [9,12], so the new range for atBit is [9,19].  We've
            // written one full byte and part or all of a second byte.  The next step, based
            // on the value of atBit, is:
            // [9,15]: done
            // [16]: advance output pointer
            // [17,19]: advance output pointer, write partial byte
            //
            // We want to leave with dstOffset pointing at the last partial byte, or the first
            // empty byte.  Never at a full byte.

            if (atBit >= 16) {
                mCompLZWOutBuf[++dstOffset] = (byte)(code >> 16);
            }
            atBit &= 0x07;

            return dstOffset < CHUNK_SIZE;
        }

        /// <summary>
        /// Generates a 256-entry table used for the hash function.
        /// </summary>
        /// <remarks>
        /// <para>The table has 256 entries, one per byte value.</para>
        /// </remarks>
        private static int[] GenerateHashCodes() {
            // Comment from the UNIX compress v4.3 sources:
            //  Algorithm:  use open addressing double hashing (no chaining) on the
            //  prefix code / next character combination.  We do a variant of Knuth's
            //  algorithm D (vol. 3, sec. 6.4) along with G. Knott's relatively-prime
            //  secondary probe.  Here, the modular division first probe is gives way
            //  to a faster exclusive-or manipulation.
            //
            // The computation is simple enough that, on a modern system, it might be faster
            // just to compute it.  It would likely be even faster to replace the scheme
            // entirely, now that integer division isn't fatally slow.

            int[] table = new int[256];
            int adjbits = LZW_MAX_BITS - 10;
            for (int i = 0; i < 256; i++) {
                table[i] = (((i & 0x07) << 7) ^ i) << adjbits;
            }
            return table;
        }

        #endregion Compress

        #region Expand

        private int mExpRemaining;
        private bool mExpInputEnded;
        private bool mExpHeaderRead;

        private ushort mStoredCRC;
        private ushort mCalcCRC;
        private byte mRLEDelim;

        private byte[]? mExpChunkBuf;
        private int mExpChunkCount;

        private byte[]? mExpWorkBuf;

        private byte[]? mExpOutputBuf;
        private int mExpOutputPosn;

        private struct LZWEntry {
            public int mDepth;          // 0-4096
            public byte mFinalVal;      // byte added in this node
            public int mParentCode;     // 9-12 bits, [0x0000,0x0fff] or so
        }
        private LZWEntry[]? mExpTable;
        private int mExpLZWEntry;
        private int mExpLZWBitLen;


        /// <summary>
        /// Prepares object for decompression.
        /// </summary>
        private void InitExpand(long expandedLength) {
            if (expandedLength != (int)expandedLength) {
                throw new ArgumentException("Invalid expanded length: " + expandedLength);
            }
            mExpRemaining = (int)expandedLength;

            mExpChunkBuf = new byte[CHUNK_INPUT_BUF_LEN];
            mExpChunkCount = 0;
            mExpWorkBuf = new byte[CHUNK_OUTPUT_BUF_LEN];

            mExpOutputBuf = new byte[CHUNK_SIZE];
            mExpOutputPosn = CHUNK_SIZE;        // no output available

            mExpInputEnded = false;
            mCalcCRC = 0x0000;

            // Allocate table, and initialize first 257 entries.  The values in those entries
            // are constant, so we could handle them conditionally in the code, but this
            // avoids having to do so.
            mExpTable = new LZWEntry[LZW_EXP_TABLE_SIZE];
            for (int i = 0; i < LZW_FIRST_CODE; i++) {
                mExpTable[i].mFinalVal = (byte)i;
                mExpTable[i].mDepth = 0;
                mExpTable[i].mParentCode = 0;
            }
            mExpLZWEntry = LZW_FIRST_CODE - 1;
            mExpLZWBitLen = LZW_START_BITS;
        }

        /// <summary>
        /// Reads uncompressed data.  (Stream call.)
        /// </summary>
        public override int Read(byte[] buffer, int offset, int count) {
            Debug.Assert(mExpChunkBuf != null);
            Debug.Assert(mExpOutputBuf != null);

            if (!mExpHeaderRead) {
                ReadHeader();
                mExpHeaderRead = true;
            }

            int copyCount = 0;
            while (count != 0) {
                // Copy out any pending data.
                if (mExpOutputPosn != CHUNK_SIZE) {
                    int copied = OutputChunk(buffer, offset, count);
                    offset += copied;
                    count -= copied;
                    copyCount += copied;
                }
                if (count == 0 || mExpRemaining == 0) {
                    // Request fulfilled or out of data.
                    break;
                }

                // Fill our input buffer with compressed data.  The buffer is large enough to
                // ensure that we have a compressed chunk (need 4KB + the header bytes).
                while (!mExpInputEnded && mExpChunkCount < CHUNK_INPUT_BUF_LEN) {
                    int bytesNeeded = CHUNK_INPUT_BUF_LEN - mExpChunkCount;
                    int actual = mCompDataStream.Read(mExpChunkBuf, mExpChunkCount, bytesNeeded);
                    mExpInputEnded = (actual == 0);
                    mExpChunkCount += actual;
                }

                // Expand one 4KB chunk.
                ExpandChunk();
                if (!mIsType2) {
                    // Update the CRC, which includes the padding.
                    mCalcCRC = CRC16.XMODEM_OnBuffer(mCalcCRC, mExpOutputBuf, 0, CHUNK_SIZE);
                }
            }

            if (!mIsType2 && mExpRemaining == 0) {
                if (mCalcCRC != mStoredCRC) {
                    throw new InvalidDataException("LZW/1 CRC mismatch");
                }
            }
            return copyCount;
        }

        /// <summary>
        /// Reads the file header.
        /// </summary>
        private void ReadHeader() {
            if (!mIsType2) {
                mStoredCRC = RawData.ReadU16LE(mCompDataStream, out bool ok);
                if (!ok) {
                    throw new InvalidDataException("Unable to read header");
                }
            }
            int volByte = mCompDataStream.ReadByte();
            int delimByte = mCompDataStream.ReadByte();
            VolumeNum = (byte)volByte;
            mRLEDelim = (byte)delimByte;
        }

        /// <summary>
        /// Expands a chunk, from mExpChunkBuf to mExpOutputBuf.  The output is always 4KB.
        /// </summary>
        private void ExpandChunk() {
            Debug.Assert(mExpChunkBuf != null);
            Debug.Assert(mExpOutputBuf != null);

            // Each chunk represents 4096 bytes of compressed data.  The minimum possible
            // size for a full chunk is a 4-byte LZW/2 header followed by a 12-bit code.
            const int MIN_CHUNK = 6;
            if (mExpChunkCount < MIN_CHUNK && mExpInputEnded) {
                if (mExpChunkCount != 0) {
                    // ShrinkIt likes to add an extra byte to the end.
                    //Debug.WriteLine("NuLZW expand found " + mExpChunkCount + " extra bytes");
                }
                throw new InvalidDataException("NuLZW buffer underrun (header)");
            }

            int headerLen = mIsType2 ? 2 : 3;

            bool lzwUsed;
            ushort postRleLen = RawData.GetU16LE(mExpChunkBuf, 0);
            int expectedLzwLen = -1;
            if (mIsType2) {
                // Extract LZW flag from RLE length.
                lzwUsed = (postRleLen & 0x8000) != 0;
                postRleLen &= 0x7fff;
                if (lzwUsed) {
                    // Length of compressed data chunk.  Unreliable in "bad Mac" archives.
                    expectedLzwLen = RawData.GetU16LE(mExpChunkBuf, 2);
                    headerLen = 4;
                }
            } else {
                lzwUsed = mExpChunkBuf[2] != 0;
            }
            if (postRleLen > CHUNK_SIZE) {
                // File truncated?
                throw new InvalidDataException("NuLZW buffer underrun (RLE len)");
            }

            int chunkUsed;
            if (lzwUsed) {
                // Chunk starts just past the header.  We don't know how long it is, but we
                // know how much output it should produce.  If the data is damaged, the code
                // might start reading from the next chunk.
                Debug.Assert(mExpWorkBuf != null);
                if (!mIsType2) {
                    // Reset every time.
                    mExpLZWEntry = LZW_FIRST_CODE - 1;
                    mExpLZWBitLen = LZW_START_BITS;
                }
                int lzwLen = ExpandLZW(mExpChunkBuf, headerLen, mExpWorkBuf, 0, postRleLen);
                if (lzwLen < 0) {
                    throw new InvalidDataException("NuLZW data error (LZW decoding)");
                }
                if (expectedLzwLen > 0 && expectedLzwLen != lzwLen + headerLen) {
                    Debug.WriteLine("Warning: LZW output length mismatch (expected " +
                        expectedLzwLen + ", used " + lzwLen + " + " + headerLen + ")");
                }
                chunkUsed = lzwLen;

                // Undo the RLE compression.
                if (!ExpandRLE(mExpWorkBuf, 0, postRleLen, mExpOutputBuf, 0)) {
                    throw new InvalidDataException("NuLZW data error (RLE decoding)");
                }
            } else {
                if (mIsType2) {
                    // Clear table when LZW fails.
                    mExpLZWEntry = LZW_FIRST_CODE - 1;
                    mExpLZWBitLen = LZW_START_BITS;
                }
                if (!ExpandRLE(mExpChunkBuf, headerLen, postRleLen, mExpOutputBuf, 0)) {
                    throw new InvalidDataException("NuLZW data error (RLE decoding)");
                }
                chunkUsed = postRleLen;
            }

            mExpOutputPosn = 0;                         // init the output counter

            // Shift any remaining data to start of chunk input buffer.
            mExpChunkCount -= headerLen + chunkUsed;   // subtract consumed data from chunk
            Debug.Assert(mExpChunkCount >= 0);
            if (mExpChunkCount != 0) {
                Array.Copy(mExpChunkBuf, headerLen + chunkUsed, mExpChunkBuf, 0,
                    mExpChunkCount);
            }
        }

        /// <summary>
        /// Outputs data from an expanded chunk.  We need to avoid copying the padding that
        /// was added to the end of the file.
        /// </summary>
        private int OutputChunk(byte[] buffer, int offset, int count) {
            Debug.Assert(mExpOutputBuf != null);

            // Compute amount of data left in the 4KB chunk buffer.
            int dataRemaining = CHUNK_SIZE - mExpOutputPosn;

            // Compute amount of file data left in the chunk buffer.  We will output at most
            // this much.
            int fileRemaining = Math.Min(dataRemaining, mExpRemaining);

            // Only copy the part that's within the file bounds.
            int copyLen = Math.Min(fileRemaining, count);
            if (copyLen != 0) {
                Debug.Assert(copyLen > 0);
                Array.Copy(mExpOutputBuf, mExpOutputPosn, buffer, offset, copyLen);
                mExpRemaining -= copyLen;
            }

            // Consume what we copied plus perhaps some padding at the end.
            int consumeLen = Math.Min(dataRemaining, count);
            mExpOutputPosn += consumeLen;

            return copyLen;
        }

        /// <summary>
        /// Expands data compressed with RLE into a 4KB chunk.  Returns true on success, false
        /// if the data appears to be damaged.
        /// </summary>
        private bool ExpandRLE(byte[] srcBuf, int srcOffset, int srcLength,
                byte[] dstBuf, int dstOffset) {
            if (srcLength == CHUNK_SIZE) {
                // Data was stored without compression.  Copy the whole thing to the output
                // buffer.  (It would be more efficient to skip the copy and just write the
                // output from the chunk buffer, but the added complexity isn't worthwhile.)
                Array.Copy(srcBuf, srcOffset, dstBuf, 0, CHUNK_SIZE);
                return true;
            }

            int srcEndOffset = srcOffset + srcLength;
            int dstEndOffset = dstOffset + CHUNK_SIZE;
            byte rleDelim = mRLEDelim;

            // Do the whole thing in a try/catch loop, so we can let the runtime take care
            // of the bounds checking.  We won't throw an exception unless the input is bad.
            try {
                int count;
                byte val;
                while (srcOffset < srcEndOffset) {
                    val = srcBuf[srcOffset++];
                    if (val == rleDelim) {
                        val = srcBuf[srcOffset++];
                        count = srcBuf[srcOffset++];
                        while (count-- >= 0) {
                            dstBuf[dstOffset++] = val;
                        }
                    } else {
                        dstBuf[dstOffset++] = val;
                    }
                }
            } catch (IndexOutOfRangeException) {
                // Whoops.
                //Debug.Assert(false, "Bad RLE data (caught)");
                return false;
            }

            if (srcOffset != srcEndOffset || dstOffset != dstEndOffset) {
                Debug.Assert(false, "Bad RLE data");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Expands data compressed with LZW.
        /// </summary>
        /// <remarks>
        /// <para>The length of input data is not known, but will be less than 4KB.  The length
        /// of the output is known, and is used to determine when to stop.  This may read two
        /// bytes past the end of the input data, so srcBuf should be slightly oversize.</para>
        /// <para>Set entry=0x0100 and bits=9 before the first call, before each LZW/1 chunk,
        /// and whenever LZW/2 fails to compress a chunk.</para>
        /// </remarks>
        /// <returns>Number of bytes consumed, or -1 if bad data was encountered.</returns>
        private int ExpandLZW(byte[] srcBuf, int srcOffset,
                byte[] dstBuf, int dstOffset, int dstCount) {
            // Attribution note: the original NuFX LZW code was sent to me by Kent Dickey in 1989
            // or thereabouts.  In 2020 he forwarded a streamlined version he wrote for the
            // updated version of KEGS.  This implementation is based on his newer code.

            Debug.Assert(mExpTable != null);
            Debug.Assert(mExpLZWBitLen ==
                32 - System.Numerics.BitOperations.LeadingZeroCount((uint)(mExpLZWEntry + 1)));

            int origSrcOffset = srcOffset;
            int entry = mExpLZWEntry;
            int bitLen = mExpLZWBitLen;
            int dstEndOffset = dstOffset + dstCount;
            int mask = (1 << bitLen) - 1;
            int bitPosn = 0;

            while (dstOffset < dstEndOffset) {
                // Get the next 24 bits from the stream.  We don't need all of these, but it's
                // much simpler to just grab them all than it is to try to keep track of how
                // many we need and how many we already have.
                //
                // This will read past the end of the 4KB buffer if the input is >= 4094 bytes
                // long, so the input buffer must be oversized by 2.
                int newCode = (srcBuf[srcOffset + 2] << 16) | (srcBuf[srcOffset + 1] << 8) |
                    srcBuf[srcOffset];
                newCode = (newCode >> bitPosn) & mask;

                // Update the bit position, then advance the source offset by the whole bytes.
                bitPosn += bitLen;
                srcOffset += bitPosn >> 3;      // 9-19 bits, so 1-3 bytes
                bitPosn &= 0x07;

                // If the next code won't fit in our current bit length, increase the bit length.
                // We're advancing it one iteration too early, but we need to match the behavior
                // of ShrinkIt here.
                if (entry + 1 == mask) {
                    bitLen++;
                    mask = (mask << 1) + 1;
                }

                //Debug.WriteLine("entry=" + entry.ToString("x4") +
                //    " code=" + newCode.ToString("x4"));

                // If this is a table clear code, reset everything and loop.
                //
                // We set the initial code to 0x0100 instead of 0x0101 to avoid having to
                // special-case the first byte of input.  Since code 0x0100 is reserved for table
                // clears, this doesn't cause problems.
                //
                // This cleanly handles an awkward case, where a table clear is the second-to-last
                // code in a chunk.  If we don't store the value in the table, we have to handle
                // it as a special case.
                if (newCode == LZW_TABLE_CLEAR) {
                    entry = LZW_FIRST_CODE - 1;
                    bitLen = LZW_START_BITS;
                    mask = (1 << LZW_START_BITS) - 1;
                    continue;
                }
                if (newCode > entry) {
                    // Code references a table entry we haven't populated yet.  Bad data.
                    Debug.WriteLine("LZW bad code: 0x" + newCode.ToString("x4") + " vs. entry " +
                        entry.ToString("x4"));
                    return -1;
                }

                // The value in newCode can be 0x00-ff, representing a single byte, or
                // 0x101+, which references a table entry.  Each table entry holds a byte
                // value and a pointer to the next entry up the tree.
                //
                // The tree is traversed from bottom to top, which is the reverse order of the
                // string we want to output.  Some implementations use a stack, gathering bytes
                // and then spitting them out.  Since we have a bit more memory to throw around
                // than an Apple II, we just keep track of the depth at every point in the tree,
                // so we can simply place the bytes as we walk up the tree.
                int depth = mExpTable[newCode].mDepth;
                int outOffset = dstOffset + depth;
                if (outOffset >= dstEndOffset) {
                    Debug.WriteLine("LZW buffer overrun");
                    return -1;
                }
                int finalCode = newCode;
                byte finalVal = 0;
                while (outOffset >= dstOffset) {
                    finalVal = mExpTable[finalCode].mFinalVal;
                    dstBuf[outOffset--] = finalVal;
                    finalCode = mExpTable[finalCode].mParentCode;
                }

                // Update the table entry for the previous string, adding the first character
                // of this string to the end.  (LZW decoding has a funky off-by-one mechanic.)
                mExpTable[entry].mFinalVal = finalVal;

                // Advance output buffer.  The depth is stored as (value - 1) in the tree, so
                // increment it first.
                depth++;
                dstOffset += depth;

                // Move to the next table entry.
                entry++;
                if (entry == LZW_EXP_TABLE_SIZE) {
                    Debug.WriteLine("LZW code exceeded table size");
                    return -1;
                }

                // Nifty trick: pre-emptively set values in the next entry.  LZW's off-by-one
                // system blows up on the "KwKwK" case, because the entry needs the final
                // character from itself.  This can only happen when the first and last
                // character in the string are the same.  Most implementations handle this as
                // a special case when reading the input (newCode == entry).
                //
                // Storing this value every time may or may not be more efficient than
                // explicitly checking for the special case, but it feels cleaner.
                mExpTable[entry].mDepth = depth;
                mExpTable[entry].mFinalVal = finalVal;
                mExpTable[entry].mParentCode = newCode;
            }

            // Done with chunk.  If we used part of a byte, round up.
            if (bitPosn != 0) {
                srcOffset++;
            }

            if (dstOffset != dstEndOffset) {
                Debug.WriteLine("LZW buffer overrun (at end)");
                return -1;
            }

            // Preserve these for next chunk (only needed for LZW/2).
            mExpLZWEntry = entry;
            mExpLZWBitLen = bitLen;

            return srcOffset - origSrcOffset;
        }

        #endregion Expand
    }
}
