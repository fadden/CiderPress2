/*
 * Copyright 2024 faddenSoft
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
    /// Streaming implementation of LZW compression.  This produces output identical to the
    /// UNIX "compress" command, v4.0.
    /// </summary>
    /// <remarks>
    /// <para>Compatibility is more important than performance or readability, so this follows the
    /// original source code fairly closely, retaining naming conventions and code structure.
    /// I have discarded the various memory-saving techniques, like hard-limiting the maximum
    /// number of bits to less than 16.</para>
    /// <para>To function as a streaming class, rather than a file filter, the structure of the
    /// main loops had to be altered significantly.</para>
    /// </remarks>
    public class LZCStream : Stream {
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
        private const int BITS = 16;                // full size
        private const int HSIZE = 69001;            // 95% occupancy
        private const int INIT_BITS = 9;            // initial number of bits/code
        private const int FIRST = 257;              // first free entry (assuming block mode)
        private const int CLEAR = 256;              // table clear output code (in block mode)
        private static int MAXCODE(int bits) { return (1 << bits) - 1; }

        // Largest amount of data that can be represented with a single code.  When configured
        // for 16 bits, compressing ~2.1 gigabytes of zeroes leads to code $ffff representing
        // 65280 bytes.  With block mode disabled, it could be one larger.
        private const int MAX_STACK = (1 << BITS) - 256 + 1;

        private const byte HEADER0 = 0x1f;
        private const byte HEADER1 = 0x9d;
        private const int BIT_MASK = 0x1f;
        private const int BLOCK_MASK = 0x80;

        private int mCompBitLimit;                  // caller-requested bit limit
        private bool mComp_block_compress;          // if true, issue CLEAR codes after table fills
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
        /// <param name="maxBits">Compression: maximum code width, must be in the range
        ///   [9,16].</param>
        /// <param name="blockMode">Compression: use block mode (recommended)?</param>
        public LZCStream(Stream compDataStream, CompressionMode mode, bool leaveOpen,
                long compDataStreamLen, int maxBits, bool blockMode) {
            if (maxBits < INIT_BITS || maxBits > BITS) {
                throw new ArgumentOutOfRangeException(nameof(maxBits), maxBits, "must be [9,16]");
            }
            mCompDataStream = compDataStream;
            mCompressionMode = mode;
            mLeaveOpen = leaveOpen;
            mExpInputRemaining = compDataStreamLen;
            mCompBitLimit = maxBits;
            mComp_block_compress = blockMode;

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
        public override void WriteByte(byte value) {
            if (mSingleBuf == null) {
                mSingleBuf = new byte[1];
            }
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

        private int mComp_offset;
        private int mComp_in_count;                // length of input (rolls over after 2GB)
        private int mComp_bytes_out;               // length of compressed output
        private int mComp_out_count;                // # of codes output (for debugging)

        private bool mComp_clear_flg;
        private int mComp_ratio;
        private const int CHECK_GAP = 10000;        // ratio check interval
        private int mComp_checkpoint;

        private int mComp_n_bits;                   // number of bits/code
        private int mComp_maxbits;                  // user settable max # bits/code
        private int mComp_maxcode;                  // maximum code, given n_bits
        private int mComp_maxmaxcode;
        private int mComp_free_ent;                 // first unused entry
        private int mComp_ent;

        // Tables used for compression.
        private int[]? mComp_htab;
        private ushort[]? mComp_codetab;
        private byte[]? mComp_buf;

        private int mComp_hsize;                    // for dynamic table sizing
        private int mComp_hshift;

        private bool mCompHeaderWritten;
        private bool mFirstByteRead;


        /// <summary>
        /// Prepares object for compression.
        /// </summary>
        private void InitCompress() {
            mComp_buf = new byte[BITS];
            mComp_htab = new int[HSIZE];
            mComp_codetab = new ushort[HSIZE];
            mComp_hsize = HSIZE;

            mComp_maxbits = mCompBitLimit;
            mComp_maxmaxcode = 1 << mCompBitLimit;

            int hshift = 0;
            for (int fcode = mComp_hsize; fcode < 65536; fcode *= 2) {
                hshift++;
            }
            mComp_hshift = 8 - hshift;              // set hash code range bound
            ClearHash(mComp_hsize);                 // clear hash table

            mComp_ratio = 0;

            mComp_offset = 0;
            mComp_out_count = 0;
            mComp_clear_flg = false;
            mComp_ratio = 0;
            mComp_in_count = 0;
            mComp_checkpoint = CHECK_GAP;
            mComp_n_bits = INIT_BITS;
            mComp_maxcode = MAXCODE(mComp_n_bits);
            mComp_free_ent = mComp_block_compress ? FIRST : 256;

            mCompHeaderWritten = false;
            mFirstByteRead = false;
        }

        /// <summary>
        /// Receives uncompressed data to be compressed.  (Stream call.)
        /// </summary>
        public override void Write(byte[] buffer, int offset, int count) {
            Debug.Assert(mComp_htab != null && mComp_codetab != null);

            if (!mCompHeaderWritten) {
                WriteHeader();
                mCompHeaderWritten = true;
            }

            if (count == 0) {
                return;                     // nothing to do
            }

            if (!mFirstByteRead) {
                // First byte is handled specially.
                mComp_ent = buffer[offset++];
                count--;
                mComp_in_count++;
                mFirstByteRead = true;
            }

            // Iterate through all data in the write buffer.
            while (count > 0) {
                int c = buffer[offset++];
                count--;
                mComp_in_count++;

                int fcode = (c << mComp_maxbits) + mComp_ent;
                int i = ((c << mComp_hshift) ^ mComp_ent);      // xor hashing

                if (mComp_htab[i] == fcode) {
                    mComp_ent = mComp_codetab[i];               // matched existing entry
                    continue;
                } else if (mComp_htab[i] < 0) {
                    goto nomatch;                               // empty slot
                }
                int disp = mComp_hsize - i;                     // secondary hash (after G. Knott)
                if (i == 0) {
                    disp = 1;
                }
            probe:
                i -= disp;
                if (i < 0) {
                    i += mComp_hsize;
                }
                if (mComp_htab[i] == fcode) {
                    mComp_ent = mComp_codetab[i];
                    continue;
                }
                if (mComp_htab[i] > 0) {
                    goto probe;
                }
            nomatch:
                Output(mComp_ent);
                mComp_out_count++;
                mComp_ent = c;

                if (mComp_free_ent < mComp_maxmaxcode) {
                    // Table isn't full, so add the new entry.
                    mComp_codetab[i] = (ushort)mComp_free_ent++;        // code -> hashtable
                    mComp_htab[i] = fcode;
                } else if (mComp_in_count >= mComp_checkpoint && mComp_block_compress) {
                    ClearBlock();
                }
            }
        }

        /// <summary>
        /// Writes the LZC header.
        /// </summary>
        private void WriteHeader() {
            byte flags = (byte)(mCompBitLimit | (mComp_block_compress ? BLOCK_MASK : 0));
            mCompDataStream.WriteByte(HEADER0);
            mCompDataStream.WriteByte(HEADER1);
            mCompDataStream.WriteByte(flags);

            mComp_bytes_out = 3;        // includes 3-byte header mojo
        }

        /// <summary>
        /// Outputs the given code.
        /// </summary>
        /// <remarks>
        /// This retains a buffer with room for 8 codes.  When the buffer fills up, the entire
        /// thing is written at once.  This can cause alignment gaps in the output, especially
        /// when outputting clear codes, because all codes in the buffer must be the same width.
        /// </remarks>
        /// <param name="code">Code to output, or -1 if input reached EOF.</param>
        private void Output(int code) {
            Debug.Assert(mComp_buf != null);

            int r_off = mComp_offset;
            int bits = mComp_n_bits;

            if (code >= 0) {
                // In the original code, a single VAX insv instruction does most of this next part.

                // Get to the first byte.
                int bp = (r_off >> 3);
                r_off &= 7;

                // Since code is always >= 8 bits, only need to mask the first hunk on the left.
                byte rmask = (byte)(0xff >> (8 - r_off));   // 0x00, 0x01, 0x03, 0x07, ... 0xff
                byte lmask = (byte)(0xff << r_off);         // 0xff, 0xfe, 0xfc, 0xf8, ... 0x00
                mComp_buf[bp] = (byte)((mComp_buf[bp] & rmask) | (code << r_off) & lmask);
                bp++;
                bits -= (8 - r_off);
                code >>= 8 - r_off;
                // Get any 8 bit parts in the middle (<=1 for up to 16 bits).
                if (bits >= 8) {
                    mComp_buf[bp++] = (byte)code;
                    code >>= 8;
                    bits -= 8;
                }
                // Last bits.
                if (bits != 0) {
                    mComp_buf[bp] = (byte)code;
                }

                mComp_offset += mComp_n_bits;
                if (mComp_offset == (mComp_n_bits << 3)) {
                    // Buffer is full.  For some reason, the original does this one byte at a time;
                    // apparently this was a performance improvement added in v2.1.
                    mCompDataStream.Write(mComp_buf, 0, mComp_n_bits);
                    mComp_bytes_out += mComp_n_bits;
                    mComp_offset = 0;
                }

                // If the next entry is going to be too big for the code size, then increase
                // it, if possible.
                if (mComp_free_ent > mComp_maxcode || mComp_clear_flg) {
                    // Write the whole buffer, because the input side won't discover the size
                    // increase until after it has read it.
                    // Note the number of bytes required to store 8 codes is equal to the
                    // number of bits per code.
                    if (mComp_offset > 0) {
                        mCompDataStream.Write(mComp_buf, 0, mComp_n_bits);
                        mComp_bytes_out += mComp_n_bits;
                    }
                    mComp_offset = 0;

                    if (mComp_clear_flg) {
                        mComp_n_bits = INIT_BITS;
                        mComp_maxcode = MAXCODE(mComp_n_bits);
                        mComp_clear_flg = false;
                    } else {
                        mComp_n_bits++;
                        if (mComp_n_bits == mComp_maxbits) {
                            mComp_maxcode = mComp_maxmaxcode;
                        } else {
                            mComp_maxcode = MAXCODE(mComp_n_bits);
                        }
                    }
                }
            } else {
                // At EOF, write the rest of the buffer.
                if (mComp_offset > 0) {
                    mCompDataStream.Write(mComp_buf, 0, (mComp_offset + 7) / 8);
                    mComp_bytes_out += (mComp_offset + 7) / 8;
                    mComp_offset = 0;
                }
            }
        }

        /// <summary>
        /// Clears the table for block compression mode, if the compression ratio appears to
        /// be declining.
        /// </summary>
        /// <remarks>
        /// The math is done with signed 32-bit integers, which will roll over after 2GB of
        /// input.  We could fix this, but our primary goal is compatibility.
        /// </remarks>
        private void ClearBlock() {
            mComp_checkpoint = mComp_in_count + CHECK_GAP;
            int rat;
            if (mComp_in_count > 0x007fffff) {
                // Shift will overflow.
                rat = mComp_bytes_out >> 8;
                if (rat == 0) {
                    rat = 0x7fffffff;       // don't divide by zero
                } else {
                    rat = mComp_in_count / rat;
                }
            } else {
                rat = (mComp_in_count << 8) / mComp_bytes_out;      // 8 fractional bits
            }
            if (rat > mComp_ratio) {
                mComp_ratio = rat;
            } else {
                mComp_ratio = 0;
                ClearHash(mComp_hsize);
                mComp_free_ent = FIRST;
                mComp_clear_flg = true;
                Output(CLEAR);
            }
        }

        /// <summary>
        /// Clears the compression hash table.
        /// </summary>
        /// <param name="hsize"></param>
        private void ClearHash(int hsize) {
            Debug.Assert(mComp_htab != null);
            for (int i = 0; i < hsize; i++) {
                mComp_htab[i] = -1;
            }
        }

        /// <summary>
        /// Finishes compression operation.  This method is called when this object is closed,
        /// indicating that all data has been provided.
        /// </summary>
        private void FinishCompression() {
            if (!mCompHeaderWritten) {
                // This should only happen for zero-length input.  The file should be nothing
                // but the header.
                WriteHeader();
                return;
            }

            // Put out the final code.
            Output(mComp_ent);
            mComp_out_count++;
            Output(-1);
        }

        #endregion Compress

        #region Expand

        private int mExp_n_bits;                    // number of bits/code
        private int mExp_maxbits;                   // max # bits/code; configurable
        private int mExp_maxcode;                   // maximum code, given n_bits
        private int mExp_maxmaxcode;                // should NEVER generate this code
        private bool mExp_block_compress;           // handle code 256 as CLEAR?
        private int mExp_free_ent;                  // first unused entry

        private int mExp_offset;                    // offset, in bits, into read buffer
        private int mExp_size;                      // size, in bits, of read buffer
        private bool mExp_clear_flg;

        private byte mExp_finchar;
        private int mExp_oldcode;

        // Tables used for expansion.  In the original, these were overlaid on the compression
        // tables.  (For some reason they didn't want to use malloc.)
        private ushort[]? mExp_tab_prefix;          // code (9-16 bits)
        private byte[]? mExp_tab_suffix;            // byte suffix
        private byte[]? mExp_de_stack;              // decompression stack
        private byte[]? mExp_buf;                   // buffer that holds up to 8 codes

        private byte[]? mExpTempBuf;
        private int mExpTempBufOffset;
        private int mExpTempBufLength;

        private bool mExpHeaderRead;
        private bool mExpFirstCode;


        /// <summary>
        /// Prepares object for decompression.
        /// </summary>
        private void InitExpand() {
            mExp_tab_prefix = new ushort[HSIZE];
            mExp_tab_suffix = new byte[HSIZE];
            mExp_de_stack = new byte[MAX_STACK];
            mExp_buf = new byte[BITS + 1];      // original code would read past end of buf
            mExpTempBuf = new byte[MAX_STACK];

            mExp_maxbits = mCompBitLimit;
            mExp_n_bits = INIT_BITS;
            mExp_maxcode = MAXCODE(mExp_n_bits);
            mExp_maxmaxcode = 1 << BITS;
            mExp_offset = mExp_size = 0;
            mExp_clear_flg = false;

            mExpHeaderRead = false;
            mExpFirstCode = true;
            mExpTempBufOffset = mExpTempBufLength = 0;

            // Init first 256 entries in table.
            for (int code = 255; code >= 0; code--) {
                mExp_tab_prefix[code] = 0;
                mExp_tab_suffix[code] = (byte)code;
            }
        }

        /// <summary>
        /// Reads uncompressed data.  (Stream call.)
        /// </summary>
        public override int Read(byte[] buffer, int offset, int count) {
            // The original code drove the input and output streams, but we're being driven by
            // "read" calls instead.  The idea here is to decode a single code into a buffer,
            // and copy as much as will fit into the read buffer.  If we have more room, decode
            // the next code; if we have more than will fit,  finish it up on the next call.  Most
            // codes will expand to a dozen or so bytes at most, but in theory a single code could
            // generate up to 2^16-2^8=65280 bytes.

            Debug.Assert(mExpTempBuf != null);
            Debug.Assert(offset <= buffer.Length - count);
            int readCount = 0;      // total number of bytes we've placed in the read buffer

            if (!mExpHeaderRead) {
                ReadHeader();
                mExp_free_ent = mExp_block_compress ? FIRST : 256;
            }

            // Start by copying any data left over from previous call.
            if (mExpTempBufLength - mExpTempBufOffset != 0) {
                int copyLen = Math.Min(count, mExpTempBufLength - mExpTempBufOffset);
                Debug.Assert(copyLen > 0);
                Array.Copy(mExpTempBuf, mExpTempBufOffset, buffer, offset, copyLen);
                offset += copyLen;
                count -= copyLen;
                mExpTempBufOffset += copyLen;
                readCount += copyLen;
            }

            // If we have more room in the read buffer, decode until the buffer is full or EOF.
            while (count > 0) {
                int decodeLen = DecodeNext();
                if (decodeLen < 0) {
                    break;      // EOF, return anything we've managed to decode
                }
                mExpTempBufLength = decodeLen;
                int copyLen = Math.Min(count, mExpTempBufLength);
                Array.Copy(mExpTempBuf, 0, buffer, offset, copyLen);
                offset += copyLen;
                count -= copyLen;
                mExpTempBufOffset = copyLen;
                readCount += copyLen;
            }
            //Debug.WriteLine("STRING: " + Encoding.ASCII.GetString(buffer, 0, readCount));
            return readCount;
        }

        /// <summary>
        /// Reads values from the LZC header.  Sets globals.  EOF will cause an exception.
        /// </summary>
        private void ReadHeader() {
            int hdr0 = mCompDataStream.ReadByte();
            int hdr1 = mCompDataStream.ReadByte();
            if (hdr0 != HEADER0 || hdr1 != HEADER1) {
                throw new InvalidDataException("This is not an LZC stream");
            }
            int flags = mCompDataStream.ReadByte();
            mExp_maxbits = flags & BIT_MASK;
            mExp_block_compress = (flags & BLOCK_MASK) != 0;
            if (mExp_maxbits < INIT_BITS || mExp_maxbits > BITS) {
                throw new InvalidDataException("Invalid maxbits in LZC header (" +
                    mExp_maxbits + ")");
            }
            mExp_maxmaxcode = 1 << mExp_maxbits;
            mExpHeaderRead = true;
            mExpInputRemaining -= 3;
        }

        /// <summary>
        /// Decodes the next LZW code into the temporary buffer.
        /// </summary>
        /// <returns>Number of bytes decoded, or -1 if EOF was reached.</returns>
        private int DecodeNext() {
            Debug.Assert(mExpTempBuf != null && mExp_tab_prefix != null &&
                mExp_tab_suffix != null && mExp_de_stack != null);

            if (mExpFirstCode) {
                // First code is handled specially.
                mExpFirstCode = false;

                mExp_oldcode = GetCode();
                mExp_finchar = (byte)mExp_oldcode;
                if (mExp_oldcode == -1) {
                    return -1;      // EOF already? Get out of here.
                }

                // First code must be a literal.
                mExpTempBuf[0] = mExp_finchar;
                return 1;
            }

            // Get the next LZW code.
            int code = GetCode();
            if (code < 0) {
                return -1;
            } else if (code == CLEAR && mExp_block_compress) {
                // Clear the table and reset.
                for (int i = 255; i >= 0; i--) {
                    mExp_tab_prefix[i] = 0;
                }
                mExp_clear_flg = true;
                mExp_free_ent = FIRST - 1;
                code = GetCode();
                if (code < 0) {
                    // CLEAR code was the last thing in the file.
                    return -1;      // O, untimely death!
                }
            }

            int incode = code;
            int stackp = 0;
            int outCount = 0;

            // Special case for KwKwK string.
            if (code >= mExp_free_ent) {
                mExp_de_stack[stackp++] = mExp_finchar;
                code = mExp_oldcode;
            }

            // Generate output characters in reverse order.
            while (code >= 256) {
                mExp_de_stack[stackp++] = mExp_tab_suffix[code];
                code = mExp_tab_prefix[code];
            }
            mExp_finchar = mExp_tab_suffix[code];
            mExp_de_stack[stackp++] = mExp_finchar;

            // And put them out in forward order.
            // (We could save a step by just returning the data in a reverse-order buffer, and
            // let the caller copy it backward.  For now I'm going to mimic the original sources
            // as closely as possible.)
            do {
                mExpTempBuf[outCount++] = mExp_de_stack[--stackp];
            } while (stackp > 0);

            // Generate the new entry.  (Unless the table is full.)
            code = mExp_free_ent;
            if (code < mExp_maxmaxcode) {
                mExp_tab_prefix[code] = (ushort)mExp_oldcode;
                mExp_tab_suffix[code] = mExp_finchar;
                mExp_free_ent = code + 1;
            }

            // Remember previous code.
            mExp_oldcode = incode;

            return outCount;
        }

        /// <summary>
        /// Gets the next LZW code from the input stream.
        /// </summary>
        /// <remarks>
        /// <para>The LZC algorithm requires reading codes in chunks of 8, so e.g. if we're
        /// working with 10-bit codes we'll read 10 bytes at a time.</para>
        /// </remarks>
        /// <returns>Next code, or -1 if EOF was hit.</returns>
        private int GetCode() {
            Debug.Assert(mExp_buf != null);

            if (mExp_clear_flg || mExp_offset >= mExp_size || mExp_free_ent > mExp_maxcode) {
                // If the next entry will be too big for the current code size, then we must
                // increase the size.  This implies reading a new buffer full, too.
                if (mExp_free_ent > mExp_maxcode) {
                    mExp_n_bits++;
                    if (mExp_n_bits == mExp_maxbits) {
                        mExp_maxcode = mExp_maxmaxcode;     // won't get any bigger now
                    } else {
                        mExp_maxcode = MAXCODE(mExp_n_bits);
                    }
                }
                if (mExp_clear_flg) {
                    mExp_n_bits = INIT_BITS;
                    mExp_maxcode = MAXCODE(mExp_n_bits);
                    mExp_clear_flg = false;
                }
                // We want 8 codes at N bits each, which means we want to read N bytes.
                mExp_size = TryToRead(mExp_n_bits);
                if (mExp_size == 0) {
                    return -1;      // end of file
                }
                mExp_offset = 0;
                // Round size down to integral number of codes.
                // This also changes it from a byte count to a bit count.  In normal operation
                // this value is one bit past the 7th code, so the "offset >= size" condition works
                // correctly, but at EOF we need to know how many full codes we have in the buffer.
                // The idea is to subtract the maximum number of bits that could be totally
                // extraneous.  For example, if we read 3 bytes while processing 10-bit codes,
                // this computes (3 * 8) - (10 - 1) = 15.  Once we're >= bit 15 we know we're done.
                mExp_size = (mExp_size << 3) - (mExp_n_bits - 1);
            }

            // This next part is implemented with a single VAX "extzv" instruction in the
            // original code ("returns a longword zero-extended field that has been extracted
            // from the specified variable bit field").
            int r_off = mExp_offset;        // offset to first bit in code
            int bits = mExp_n_bits;         // number of bits in code
            int bp = 0;

            // Get to the first byte.
            bp += (r_off >> 3);
            r_off &= 7;
            // Get first part (low order bits).
            int code = mExp_buf[bp++] >> r_off;
            bits -= (8 - r_off);
            r_off = 8 - r_off;      // now, offset into code word
            // Get any 8 bit parts in the middle (<=1 for up to 16 bits).
            if (bits >= 8) {
                code |= mExp_buf[bp++] << r_off;
                r_off += 8;
                bits -= 8;
            }
            // High order bits.  (I'm generating this rather than using a table.)  Note this
            // can go past the end of the buffer, but only when bits==0.
            byte rmask = (byte)(0xff >> (8 - bits));    // 0x00, 0x01, 0x03, 0x07, ... 0xff
            Debug.Assert(bp < mExp_buf.Length);
            code |= (mExp_buf[bp] & rmask) << r_off;

            mExp_offset += mExp_n_bits;
            return code;
        }

        /// <summary>
        /// Tries to read N bytes from the compressed data stream into the expansion I/O buffer.
        /// This will retry until all bytes have been read or EOF is reached.
        /// </summary>
        /// <param name="wanted">Number of bytes wanted.</param>
        /// <returns>Number of bytes read, or zero on EOF.</returns>
        private int TryToRead(int wanted) {
            Debug.Assert(mExp_buf != null);
            if (wanted > mExpInputRemaining) {
                wanted = (int)mExpInputRemaining;
            }
            int got = 0;
            while (got < wanted) {
                int actual = mCompDataStream.Read(mExp_buf, got, wanted);
                if (actual == 0) {
                    break;      // reached EOF, return whatever we got
                }
                got += actual;
                wanted -= actual;
            }
            mExpInputRemaining -= got;
            return got;
        }

        #endregion Expand
    }
}
