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

namespace CommonUtil {
    /// <summary>
    /// Class for managing a circular buffer of bits.  Data may be accessed a bit at a time or a
    /// byte at a time, and will wrap around when the end of the buffer is reached.
    /// </summary>
    /// <remarks>
    /// <para>This class just provides a wrapper around a section of a byte buffer.  It does
    /// not own the buffer or cache any values.  Having multiple overlapping instances is
    /// allowed.</para>
    /// <para>The data may start or end partway into the first or last byte.</para>
    /// <para>This was designed for use with GCR-encoded disk nibbles.</para>
    /// </remarks>
    public class CircularBitBuffer {
        /// <summary>
        /// Smallest allowed data.
        /// </summary>
        public const int MIN_BITS = 8;

        /// <summary>
        /// Current position, in the range [0,bitCount].
        /// </summary>
        public int BitPosition {
            get { return mBitOffset - mStartBitOffset; }
            set {
                if (value < 0 || value >= mEndBitOffset - mStartBitOffset) {
                    throw new ArgumentOutOfRangeException("value",
                        "Bit position out of range: " + value);
                }
                mBitOffset = value + mStartBitOffset;
            }
        }

        /// <summary>
        /// Total size of data spanned, in bits.
        /// </summary>
        public int BitCount { get { return mEndBitOffset - mStartBitOffset; } }

        /// <summary>
        /// Set to true whenever a write operation is performed.
        /// </summary>
        /// <remarks>
        /// This may be shared between multiple instances, so that the application can tell at
        /// a glance if any track has been modified.
        /// </remarks>
        private GroupBool mModifiedFlag;

        /// <summary>
        /// If true, modifications to the buffer are not allowed.
        /// </summary>
        public bool IsReadOnly { get; private set; }

        /// <summary>
        /// Absolute current position within the buffer.  This factors the base byte/bit offset in.
        /// </summary>
        /// <remarks>
        /// We write to the high bit first, so bit position zero is the high bit of the first byte
        /// in the buffer.
        /// </remarks>
        private int mBitOffset;

        /// <summary>
        /// Starting absolute bit offset within the buffer.
        /// </summary>
        private int mStartBitOffset;

        /// <summary>
        /// Ending absolute bit offset within the buffer (exclusive).
        /// </summary>
        private int mEndBitOffset;

        /// <summary>
        /// Data buffer.
        /// </summary>
        private byte[] mBuffer;

        /// <summary>
        /// This will be set if we detect that the buffer has nothing but zeroes in it.
        /// </summary>
        private bool mZeroedBuffer;


        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="buffer">Buffer of bits.</param>
        /// <param name="byteOffset">Offset of first byte of data.</param>
        /// <param name="bitOffset">Offset of first bit of data (0-7).</param>
        /// <param name="bitCount">Number of bits in buffer.</param>
        public CircularBitBuffer(byte[] buffer, int byteOffset, int bitOffset, int bitCount)
                : this(buffer, byteOffset, bitOffset, bitCount, new GroupBool(), false) {
        }

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="buffer">Buffer of bits.</param>
        /// <param name="byteOffset">Offset of first byte of data.</param>
        /// <param name="bitOffset">Offset of first bit of data (0-7).</param>
        /// <param name="bitCount">Number of bits in buffer.</param>
        /// <param name="groupModFlag">Object to update when a change is made.</param>
        /// <param name="isReadOnly">True if buffer should be treated as read-only.</param>
        public CircularBitBuffer(byte[] buffer, int byteOffset, int bitOffset, int bitCount,
                GroupBool groupModFlag, bool isReadOnly) {
            if (buffer == null) {
                throw new ArgumentNullException("buffer is null");
            }
            if (byteOffset < 0 || byteOffset >= buffer.Length) {
                throw new ArgumentOutOfRangeException(nameof(byteOffset));
            }
            if (bitOffset < 0 || bitOffset > 7) {
                throw new ArgumentOutOfRangeException(nameof(bitOffset));
            }
            // We want to be able to reference the absolute position within the buffer as a
            // simple integer for simplicity, so make sure it fits.  This limits us to 512MB.
            if (bitCount >= int.MaxValue / 8) {
                throw new ArgumentOutOfRangeException(nameof(bitCount), "too much data");
            }
            // Can't latch the next byte if we don't have at least a byte's worth of data.
            if (bitCount < MIN_BITS) {
                throw new ArgumentOutOfRangeException(nameof(bitCount), "not enough data");
            }
            if (byteOffset + ((bitCount + bitOffset + 7) / 8) > buffer.Length) {
                throw new ArgumentOutOfRangeException(nameof(bitCount),
                    "bitCount exceeds buffer length");
            }

            mBuffer = buffer;
            mStartBitOffset = byteOffset * 8 + bitOffset;
            mEndBitOffset = mStartBitOffset + bitCount;
            mModifiedFlag = groupModFlag;
            IsReadOnly = isReadOnly;

            mBitOffset = mStartBitOffset;
        }

        /// <summary>
        /// Copy constructor.
        /// </summary>
        public CircularBitBuffer(CircularBitBuffer src) {
            mBuffer = src.mBuffer;
            mStartBitOffset = src.mStartBitOffset;
            mEndBitOffset = src.mEndBitOffset;
            mModifiedFlag = src.mModifiedFlag;
            IsReadOnly = src.IsReadOnly;
            mZeroedBuffer = src.mZeroedBuffer;

            // Don't clone the current bit position.  Start at the start.
            mBitOffset = mStartBitOffset;
        }

        /// <summary>
        /// Adjusts the value of BitPosition.  This should be used instead of simply adding or
        /// subtracting an offset because it needs to wrap around.
        /// </summary>
        /// <param name="adjBits">Adjustment (+/-).  May not exceed size of buffer.</param>
        /// <returns>New position.</returns>
        public int AdjustBitPosition(int adjBits) {
            int bitCount = BitCount;
            if (adjBits > bitCount || adjBits < -bitCount) {
                throw new ArgumentOutOfRangeException(nameof(adjBits),
                    "Adjustment exceeds size of buffer");
            }
            // Update value, then add/subtract to bring it back in line.
            int relPosn = BitPosition + adjBits;
            while (relPosn < 0) {
                relPosn += bitCount;
            }
            while (relPosn > BitCount) {
                relPosn -= bitCount;
            }
            mBitOffset = mStartBitOffset + relPosn;
            return BitPosition;
        }

        /// <summary>
        /// Reads the next bit from the buffer, advancing the position.
        /// </summary>
        /// <returns>0 or 1.</returns>
        public byte ReadNextBit() {
            byte byteVal = mBuffer[mBitOffset >> 3];
            int bitIndex = mBitOffset & 0x07;

            mBitOffset++;
            if (mBitOffset == mEndBitOffset) {
                mBitOffset = mStartBitOffset;
            }

            return (byte)((byteVal >> (7 - bitIndex)) & 0x01);  // index 0 is MSB
        }

        /// <summary>
        /// Writes a bit to the bitstream.
        /// </summary>
        /// <param name="value">Value to write (0 or 1).</param>
        public void WriteBit(int value) {
            Debug.Assert(value == 0 || value == 1);
            if (IsReadOnly) {
                throw new NotSupportedException("Buffer is read-only");
            }
            mZeroedBuffer = false;
            mModifiedFlag.IsSet = true;

            int byteIndex = mBitOffset >> 3;
            int bitIndex = mBitOffset & 0x07;
            Debug.Assert(byteIndex < mBuffer.Length);
            if (value == 0) {
                mBuffer[byteIndex] = (byte)(mBuffer[byteIndex] & ~(0x80 >> bitIndex));
            } else {
                mBuffer[byteIndex] = (byte)(mBuffer[byteIndex] | (0x80 >> bitIndex));
            }
            mBitOffset++;
            if (mBitOffset == mEndBitOffset) {
                mBitOffset = mStartBitOffset;
            }
        }

        /// <summary>
        /// Reads the next 8 bits from the buffer.
        /// </summary>
        /// <returns>Value read.</returns>
        public byte ReadOctet() {
            int startPosn = mBitOffset;
            byte result;
            if (mBitOffset + 8 <= mEndBitOffset) {
                // Not wrapping mid-byte, grab next 8 bits.
                if ((startPosn & 0x07) == 0) {
                    // Byte-aligned.  Easy.
                    result = mBuffer[mBitOffset >> 3];
                } else {
                    // Not byte-aligned.  Grab bits from two adjacent bytes.
                    int byteIndex = startPosn >> 3;
                    int bitIndex = startPosn & 0x07;
                    result = (byte)((mBuffer[byteIndex] << bitIndex) |
                                    (mBuffer[byteIndex + 1] >> (8 - bitIndex)));
                }
                mBitOffset += 8;
                if (mBitOffset == mEndBitOffset) {
                    // Exact end of buffer reached.
                    mBitOffset = mStartBitOffset;
                }
            } else {
                // Buffer wraps mid-byte.  This is rare, so just walk forward one bit at a time.
                result = 0;
                for (int i = 0; i < 8; i++) {
                    result = (byte)((result << 1) | ReadNextBit());
                }
            }
            return result;
        }

        /// <summary>
        /// Reads at least 8 bits from the buffer, not stopping until the high bit is set or
        /// the cursor returns to the initial position.
        /// </summary>
        /// <remarks>
        /// There is no bound on the number of zero bits that will be consumed.
        /// </remarks>
        /// <returns>Next value, in the range [0x80,0xff], or zero if there are no 1 bits.</returns>
        public byte LatchNextByte() {
            if (mZeroedBuffer) {
                mBitOffset += 8;
                if (mBitOffset >= mEndBitOffset) {
                    mBitOffset = mStartBitOffset;       // sloppy
                }
                return 0;
            }

            int startPosn = mBitOffset;
            byte result = ReadOctet();

            while ((result & 0x80) == 0 && mBitOffset != startPosn) {
                // High bit wasn't set, keep shifting bits in until it is.
                result <<= 1;
                result |= ReadNextBit();
            }
            if ((result & 0x80) == 0) {
                // The buffer is filled with zeroes, which makes this hilariously expensive.
                Debug.WriteLine("Unable to find nonzero value in buffer");
                mZeroedBuffer = true;
                result = 0;
            }
            return result;
        }

        /// <summary>
        /// Writes a byte to the bitstream, merging with existing data.
        /// </summary>
        /// <param name="value">Value to write.</param>
        /// <param name="width">Width of the value (8-10).  If the width is 9 or 10, zero bits
        ///   are added after the byte.</param>
        public void WriteByte(byte value, int width) {
            if (width < 8 || width > 10) {
                throw new ArgumentOutOfRangeException(nameof(width), "bad width: " + width);
            }
            if (IsReadOnly) {
                throw new NotSupportedException("Buffer is read-only");
            }
            mZeroedBuffer = false;
            mModifiedFlag.IsSet = true;

            if (mBitOffset + width <= mEndBitOffset) {
                // Not wrapping around buffer mid-write.
                if ((mBitOffset & 0x07) == 0) {
                    // Byte-aligned.
                    mBuffer[mBitOffset >> 3] = value;
                } else {
                    // Not byte-aligned.
                    int byteIndex = mBitOffset >> 3;
                    int bitIndex = mBitOffset & 0x07;
                    byte mask = (byte)(0xff >> bitIndex);
                    // Preserve 1-7 high bits, copy high part of value into low bits.
                    mBuffer[byteIndex] = (byte)((mBuffer[byteIndex] & ~mask) | value >> bitIndex);
                    // Preserve 1-7 low bits, copy low part of value into high bits.
                    byteIndex++;
                    mBuffer[byteIndex] =
                        (byte)((mBuffer[byteIndex] & mask) | value << (8 - bitIndex));
                }
                mBitOffset += 8;
                if (width > 8) {
                    // 9- or 10-bit byte, output a zero bit
                    int byteIndex = mBitOffset >> 3;
                    int bitIndex = mBitOffset & 0x07;
                    mBuffer[byteIndex] = (byte)(mBuffer[byteIndex] & ~(0x80 >> bitIndex));
                    mBitOffset++;
                }
                if (width > 9) {
                    // 10-bit byte, output a zero bit
                    int byteIndex = mBitOffset >> 3;
                    int bitIndex = mBitOffset & 0x07;
                    mBuffer[byteIndex] = (byte)(mBuffer[byteIndex] & ~(0x80 >> bitIndex));
                    mBitOffset++;
                }
                if (mBitOffset == mEndBitOffset) {
                    mBitOffset = mStartBitOffset;
                }
            } else {
                // Buffer wraps mid-byte.  This is rare, so just write one bit at a time.
                for (int i = 7; i >= 0; i--) {
                    WriteBit((value >> i) & 0x01);
                }
                if (width > 8) {
                    WriteBit(0);
                }
                if (width > 9) {
                    WriteBit(0);
                }
            }
        }

        /// <summary>
        /// Writes an 8-bit byte into the buffer.
        /// </summary>
        /// <param name="val">Value to write.</param>
        public void WriteOctet(byte val) {
            WriteByte(val, 8);
        }

        /// <summary>
        /// Writes one or more 8-bit bytes into the buffer.
        /// </summary>
        /// <param name="buffer">Bytes to write.</param>
        public void WriteOctets(byte[] buffer) {
            WriteOctets(buffer, 0, buffer.Length);
        }

        /// <summary>
        /// Writes one or more 8-bit bytes into the buffer.
        /// </summary>
        /// <param name="buffer">Bytes to write.</param>
        /// <param name="offset">Offset of first byte in buffer.</param>
        /// <param name="count">Number of bytes to write.</param>
        public void WriteOctets(byte[] buffer, int offset, int count) {
            for (int i = 0; i < count; i++) {
                WriteByte(buffer[offset + i], 8);
            }
        }

        /// <summary>
        /// Checks to see if the latched bytes at the current position match a sequence.
        /// </summary>
        /// <remarks>
        /// <para>On success, the buffer will be positioned immediately after the last matching
        /// byte.  On failure, it will be positioned after the first non-matching byte.</para>
        /// <para>If the sequence length is 0, this always returns true.</para>
        /// </remarks>
        /// <param name="expected">Byte sequence to search for.</param>
        /// <param name="expLen">Sequence byte count.</param>
        /// <returns>True if the sequence was found, or "expLen" is zero.</returns>
        public bool ExpectLatchSequence(byte[] expected, int expLen) {
            if (expLen == 0) {
                return true;
            }
            if (expected == null) {
                throw new ArgumentNullException("Null sequence");
            }
            if (expLen < 0 || expLen > expected.Length) {
                throw new ArgumentOutOfRangeException("expLen", "bad sequence length");
            }
            for (int i = 0; i < expLen; i++) {
                byte val = LatchNextByte();
                if (val != expected[i]) {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Searches for the next occurrence of a byte sequence, starting from the current
        /// position.  Data will be read with a byte latch.
        /// </summary>
        /// <remarks>
        /// <para>The sequence is treated as a series of whole bytes, not a bit stream, so
        /// this cannot be used to find an arbitrary bit pattern.</para>
        /// <para>If the sequence was found, the current bit position will be left pointing to the
        /// first bit past the end of the sequence, NOT the start of the sequence.</para>
        /// </remarks>
        /// <param name="sequence">Byte pattern to search for.</param>
        /// <param name="seqByteLen">Sequence length, in bytes.</param>
        /// <param name="maxBitDistance">Maximum search distance, in bits.  The search stops
        ///   when the query point has reached or passed this many bits from the start point when
        ///   latching a new byte at the start of a pattern.  Pass -1 to search the entire
        ///   buffer.</param>
        /// <returns>The bit position of the start of the sequence, or -1 if the sequence
        ///   was not found.</returns>
        public int FindNextLatchSequence(byte[] sequence, int seqByteLen, int maxBitDistance) {
            if (sequence.Length == 0) {
                throw new ArgumentException("Empty sequence");
            }
            if (seqByteLen <= 0 || seqByteLen > sequence.Length) {
                throw new ArgumentOutOfRangeException(nameof(seqByteLen), "invalid sequence length");
            }
            if (seqByteLen * 8 > BitCount) {
                throw new ArgumentException("Sequence length exceeds bit buffer length");
            }
            if (maxBitDistance >= BitCount) {
                // If the max bit distance exceeds the size of the buffer, trim it.  Might be
                // worth throwing an exception here.
                Debug.WriteLine("FindNextLatchSequence trimming max bit distance");
                maxBitDistance = -1;
            }
            if (maxBitDistance != -1 && maxBitDistance < sequence.Length * 8) {
                // Must be positive and at least as long as the sequence we're searching for.
                throw new ArgumentOutOfRangeException(nameof(maxBitDistance),
                    "invalid max bit distance");
            }

            // Identify the end offset, and set a flag to indicate whether we end before or
            // after we wrap.
            bool endAfterWrap;
            int searchEndOffset;
            if (maxBitDistance == -1) {
                // We want to end when we come back to the place we started.  It's possible
                // the pattern started a byte back, so it's important that we don't bail in
                // the middle of a partial match.
                searchEndOffset = mBitOffset;
                endAfterWrap = true;
            } else {
                searchEndOffset = mBitOffset + maxBitDistance;
                if (searchEndOffset > mEndBitOffset) {
                    endAfterWrap = true;
                    searchEndOffset -= (mEndBitOffset - mStartBitOffset);
                } else {
                    endAfterWrap = false;
                }
            }

            int seqStartPosn = -1;
            int seqResetPosn = -1;
            int seqIndex = 0;
            bool wrapped = false;
            bool resetWrapped = false;
            while (true) {
                // If we've reached the end, and aren't in the middle of a match, give up.
                if (seqIndex == 0 && (!endAfterWrap || wrapped) && mBitOffset >= searchEndOffset) {
                    return -1;
                }
                // If we didn't expect to wrap, but we have, then we probably overshot the end by
                // a couple of bits.
                if (!endAfterWrap && wrapped) {
                    return -1;
                }

                // Remember the start offset of this byte, in case it's the start of the
                // sequence that we're looking for (this will be the return value).
                int beforePosn = mBitOffset;
                if (seqIndex == 0) {
                    seqStartPosn = beforePosn;
                }

                // Get the next byte.
                byte val = LatchNextByte();

                // If the new position is less than the old, we've wrapped around.
                if (mBitOffset < beforePosn) {
                    if (wrapped) {
                        // Shouldn't be possible, unless we've got a bad boundary case on the
                        // search end offset.
                        Debug.Assert(false, "wrapped twice");
                        return -1;
                    }
                    wrapped = true;
                }

                if (val == sequence[seqIndex]) {
                    if (seqIndex == 0) {
                        // This is the position of the byte after the one we just latched,
                        // which is where we want to return to if this sequence doesn't pan out.
                        // Remember the "wrapped" status as well.
                        seqResetPosn = mBitOffset;
                        resetWrapped = wrapped;
                    }
                    seqIndex++;
                    if (seqIndex == sequence.Length) {
                        // Success!
                        return seqStartPosn - mStartBitOffset;
                    }
                } else if (seqIndex != 0) {
                    // One or more bytes matched, but not the full sequence.  Move back to where
                    // we were after reading the first matching byte.  (If we're guaranteed that
                    // the sequence has no repeated values, we could be a little more efficient
                    // here.)
                    //
                    // Since we're backing up, we may need to reset the "wrapped" value as well.
                    mBitOffset = seqResetPosn;
                    wrapped = resetWrapped;
                    seqIndex = 0;
                }
            }
        }

        /// <summary>
        /// Fills the entire buffer with a single byte value.
        /// </summary>
        /// <remarks>
        /// <para>If the buffer length is not a multiple of the value width, the last byte
        /// written will be truncated.</para>
        /// <para>The buffer's position will be reset to the start of the buffer.</para>
        /// </remarks>
        /// <param name="value">Value to use.</param>
        /// <param name="width">Width of the value (8-10).  If the width is 9 or 10, zero bits
        ///   are added after the byte.</param>
        public void Fill(byte value, int width) {
            if (width < 8 || width > 10) {
                throw new ArgumentOutOfRangeException(nameof(width), "invalid width: " + width);
            }
            if (IsReadOnly) {
                throw new NotSupportedException("Buffer is read-only");
            }
            mModifiedFlag.IsSet = true;

            int posn = mStartBitOffset;
            int bitsRemaining = mEndBitOffset - mStartBitOffset;
            if (width == 8 && (posn & 7) == 0 && (bitsRemaining & 7) == 0) {
                // Everything is byte-aligned, do it the easy way.
                RawData.MemSet(mBuffer, posn / 8, bitsRemaining / 8, value);
            } else {
                // One byte at a time.
                while (bitsRemaining > width) {
                    WriteByte(value, width);
                    bitsRemaining -= width;
                }
                // One bit at a time.
                while (bitsRemaining-- > 0) {
                    WriteBit((value >> 7) & 0x01);
                    // Move to the next bit, shifting zeroes in.
                    value <<= 1;
                }
            }
            mBitOffset = mStartBitOffset;
        }

        public override string ToString() {
            return "[CBB startByte=" + (mStartBitOffset / 8) + ", endByte=" + (mEndBitOffset / 8) +
                ", posnByte=" + (mBitOffset / 8) + ", ro=" + IsReadOnly +
                ", grpMod=" + mModifiedFlag.IsSet + "]";
        }

        #region Unit tests

        public static bool DebugTest() {
            return DebugReadTest() && DebugWriteTest() && DebugSequenceTest() && DebugMiscTest();
        }

        private static bool DebugReadTest() {
            // Simple test with some 10-bit sync bytes in the middle.
            byte[] in1 = new byte[] { 0xd5, 0xff, 0x3f, 0xcf, 0xf3, 0xfc, 0xff, 0xaa };
            byte[] out1 = new byte[] { 0xd5, 0xff, 0xff, 0xff, 0xff, 0xff, 0xaa };

            CircularBitBuffer circ = new CircularBitBuffer(in1, 0, 0, in1.Length * 8);
            for (int i = 0; i < out1.Length; i++) {
                int next;
                if ((next = circ.LatchNextByte()) != out1[i]) {
                    Debug.Assert(false, "mismatch " + i + ": exp=$" + out1[i].ToString("x2") +
                        " actual=" + next.ToString("x2"));
                    return false;
                }
            }

            // Simple bit test, with initial position partway into a byte.
            int[] out2 = new int[] { 0, 1, 1, 1, 1, 1, 1, /**/ 1, 1, 0, 0, 1, 1 };
            circ.BitPosition = 17;
            for (int i = 0; i < out2.Length; i++) {
                if (circ.ReadNextBit() != out2[i]) {
                    Debug.Assert(false, "bit mismatch " + i);
                    return false;
                }
            }

            // Configure the buffer to start midway in the first byte, then declare the length
            // to be the same number of bits short.  Our search for 0xa5 will fail the first few
            // times through, but succeed eventually as the wrap-around realigns the bit stream.
            // Read sequence: ff 97 ff fd bf ff e9 ff ff a5
            byte[] in3 = new byte[] { 0xff, 0xa5, 0xff };
            circ = new CircularBitBuffer(in3, 0, 1, in3.Length * 8 - 1);
            int count;
            for (count = 0; count < 10; count++) {
                byte next = circ.LatchNextByte();
                if (next == 0xa5) {
                    //Debug.WriteLine("Found at iteration " + count);
                    break;
                }
            }
            if (count != 9) {
                Debug.Assert(false, "Didn't find 0xa5");
                return false;
            }

            // Same idea, but with a byte-aligned start and a non-aligned end.
            circ = new CircularBitBuffer(in3, 0, 0, in3.Length * 8 - 1);
            for (count = 0; count < 5; count++) {
                byte next = circ.LatchNextByte();
                if (next == 0x97) {
                    //Debug.WriteLine("Found at iteration " + count);
                    break;
                }
            }
            if (count != 4) {
                Debug.Assert(false, "Didn't find 0x97");
                return false;
            }

            // Edge case: zeroed-out buffer.
            byte[] in4 = new byte[] { 0x00 };
            circ = new CircularBitBuffer(in4, 0, 0, in4.Length * 8);
            byte result = circ.LatchNextByte();
            if (result != 0) {
                Debug.Assert(false, "Latch failure not reported");
                return false;
            }

            return true;
        }

        private static bool DebugWriteTest() {
            byte[] testBuf = new byte[6];
            // Start 1 byte into the buffer to confirm correct use of offset.  Stop at end of
            // buffer so overflow causes an exception.
            CircularBitBuffer circ =
                new CircularBitBuffer(testBuf, 1, 0, (testBuf.Length - 1) * 8);

            byte[] check0 = new byte[] { 0xd5, 0xfe, 0x22, 0x7f, 0x00, 0x00 };
            testBuf[0] = 0xd5;
            testBuf[1] = 0xff;
            testBuf[2] = 0xff;
            testBuf[3] = 0xff;
            circ.BitPosition = 7;
            circ.WriteByte(0x11, 10);
            if (!RawData.CompareBytes(testBuf, check0, testBuf.Length)) {
                Debug.Assert(false, "check0 fail");
                return false;
            }

            testBuf[0] = 0;

            // Fill with everything byte-aligned.
            byte[] check1 = new byte[] { 0x00, 0xaa, 0xaa, 0xaa, 0xaa, 0xaa };
            circ.Fill(0xaa, 8);
            Debug.Assert(circ.BitPosition == 0);
            if (!RawData.CompareBytes(testBuf, check1, testBuf.Length)) {
                Debug.Assert(false, "check1 fail");
                return false;
            }

            // Use Fill() to exercise WriteByte() and WriteBit().
            byte[] check2 = new byte[] { 0x00, 0xff, 0x7f, 0xbf, 0xdf, 0xef };
            Array.Clear(testBuf);                               // test with array initially zero
            circ.BitPosition = 0;
            circ.Fill(0xff, 9);
            if (!RawData.CompareBytes(testBuf, check2, testBuf.Length)) {
                Debug.Assert(false, "check2/0 fail");
                return false;
            }
            RawData.MemSet(testBuf, 1, testBuf.Length - 1, 0xff); // test with array initially ones
            circ.BitPosition = 0;
            circ.Fill(0xff, 9);
            if (!RawData.CompareBytes(testBuf, check2, testBuf.Length)) {
                Debug.Assert(false, "check2/1 fail");
                return false;
            }

            byte[] check3 = new byte[] { 0x00, 0xff, 0x3f, 0xcf, 0xf3, 0xfc };
            Array.Clear(testBuf);
            circ.BitPosition = 0;
            circ.Fill(0xff, 10);
            if (!RawData.CompareBytes(testBuf, check3, testBuf.Length)) {
                Debug.Assert(false, "check3/0 fail");
                return false;
            }
            RawData.MemSet(testBuf, 1, testBuf.Length - 1, 0xff); // test with array initially ones
            circ.BitPosition = 0;
            circ.Fill(0xff, 10);
            if (!RawData.CompareBytes(testBuf, check3, testBuf.Length)) {
                Debug.Assert(false, "check3/1 fail");
                return false;
            }

            // Test with non-byte boundaries.
            Array.Clear(testBuf);
            circ = new CircularBitBuffer(testBuf, 0, 3, (testBuf.Length * 8) - 6);
            byte[] checkOdd1 = new byte[] { 0x1f, 0xff, 0xff, 0xff, 0xff, 0xf8 };
            circ.Fill(0xff, 8);
            if (!RawData.CompareBytes(testBuf, checkOdd1, testBuf.Length)) {
                Debug.Assert(false, "checkOdd1 fail");
                return false;
            }

            byte[] checkOdd2 = new byte[] { 0x1f, 0xe7, 0xf9, 0xfe, 0x7f, 0x98 };
            Array.Clear(testBuf);
            circ.BitPosition = 0;
            circ.Fill(0xff, 10);
            if (!RawData.CompareBytes(testBuf, checkOdd2, testBuf.Length)) {
                Debug.Assert(false, "checkOdd2 fail");
                return false;
            }

            // Test read-only buffer.
            CircularBitBuffer roCirc =
                new CircularBitBuffer(testBuf, 0, 0, testBuf.Length * 8, new GroupBool(), true);
            try {
                roCirc.WriteBit(1);
                Debug.Assert(false, "R/O fail");
                return false;
            } catch (NotSupportedException) { /*expected*/ }

            // Test mod flag.
            GroupBool modFlag = new GroupBool();
            CircularBitBuffer modCirc =
                new CircularBitBuffer(testBuf, 0, 0, testBuf.Length * 8, modFlag, false);
            if (modFlag.IsSet) {
                Debug.Assert(false, "mod set early");
                return false;
            }
            modCirc.WriteBit(1);
            if (!modFlag.IsSet) {
                Debug.Assert(false, "mod not set");
                return false;
            }

            return true;
        }

        private static bool DebugSequenceTest() {
            byte[] testData = new byte[] {
                0xff, 0x3f, 0xcf, 0xf3, 0xfc, 0xff, 0x3f, 0xcf,
                0xf3, 0xff, 0x56, 0xaa, 0x5a, 0x6a, 0xb3, 0x5b,
                0xff, 0x3f, 0xcf, 0xf3, 0xfc, 0xff, 0xd5, 0xaa,
                0xad, 0x96, 0x96, 0x96, 0x96, 0x96, 0x9d, 0xdf
            };
            byte[] seq1 = new byte[] { 0xd5, 0xaa, 0x96 };
            byte[] seq2 = new byte[] { 0xd5, 0xaa, 0xad };
            byte[] seq3 = new byte[] { 0xd5, 0xaa, 0xb5 };

            // Basic search.  Each sequence appears only once, so there's no need to
            // reset the position.
            CircularBitBuffer circ = new CircularBitBuffer(testData, 0, 0, testData.Length * 8);
            int seq1Offset = circ.FindNextLatchSequence(seq1, seq1.Length, -1);
            if (seq1Offset != 9 * 8 + 6) {
                Debug.Assert(false, "Incorrect seq1 offset");
                return false;
            }
            int seq2Offset = circ.FindNextLatchSequence(seq2, seq2.Length, -1);
            if (seq2Offset != 22 * 8) {
                Debug.Assert(false, "Incorrect seq2 offset");
                return false;
            }
            int seq3Offset = circ.FindNextLatchSequence(seq3, seq3.Length, -1);
            if (seq3Offset != -1) {
                Debug.Assert(false, "Incorrect seq3 offset");
                return false;
            }

            // Confirm reported position takes buffer offset into account.
            CircularBitBuffer subCirc = new CircularBitBuffer(testData, 16, 1,
                (testData.Length - 16) * 8 - 1);
            int subCircOffset = subCirc.FindNextLatchSequence(seq2, seq2.Length, -1);
            if (subCircOffset != 6 * 8 - 1) {
                Debug.Assert(false, "Incorrect subCirc offset");
                return false;
            }

            // Match string spans the wrap-around point.
            byte[] seq4 = new byte[] { 0x9d, 0xdf, 0xff };
            int seq4Offset = circ.FindNextLatchSequence(seq4, seq4.Length, -1);
            if (seq4Offset != 30 * 8) {
                Debug.Assert(false, "Incorrect seq4 offset");
                return false;
            }

            // Same as above, but start offset 1 bit.
            // 9d df ff 3f, start 1 bit late to get 3b bf fe 7e+, latch behavior reads it as
            // ee ff f9 f8+.
            byte[] seq5 = new byte[] { 0xee, 0xff, 0xf9 };
            circ.BitPosition = 30 * 8 + 1;
            int seq5Offset = circ.FindNextLatchSequence(seq5, seq5.Length, -1);
            if (seq5Offset != 30 * 8 + 1) {
                Debug.Assert(false, "Incorrect seq5 offset");
                return false;
            }

            // Test range limit.
            circ.BitPosition = 19 * 8;      // next bytes: f3 fc ff / d5 aa ad
            int seq2lim1 = circ.FindNextLatchSequence(seq2, seq2.Length, 3 * 8);
            if (seq2lim1 != -1) {
                Debug.Assert(false, "Incorrect seq2lim1");
                return false;
            }
            circ.BitPosition = 19 * 8;      // next bytes: f3 fc ff d5 / aa ad
            int seq2lim2 = circ.FindNextLatchSequence(seq2, seq2.Length, 4 * 8);
            if (seq2lim2 != 22 * 8) {
                Debug.Assert(false, "Incorrect seq2lim2");
                return false;
            }

            // Start near end of buffer, find sequence near start of buffer, with a limit.
            circ.BitPosition = (testData.Length - 3) * 8;
            byte[] seq5a = new byte[] { 0xff, 0xd5, 0xaa };     // starts at +9
            int seq5alim1 = circ.FindNextLatchSequence(seq5a, seq5a.Length, 20 * 8);
            if (seq5alim1 != 8 * 8 + 4) {
                Debug.Assert(false, "Incorrect seq5alim1");
                return false;
            }

            // Start in the middle of the sequence we're trying to find.  We should wrap around
            // and catch it.
            circ.BitPosition = 23 * 8;
            int midSeq1 = circ.FindNextLatchSequence(seq2, seq2.Length, -1);
            if (midSeq1 != 22 * 8) {
                Debug.Assert(false, "Incorrect midSeq1");
                return false;
            }

            // Test match with repeated values.
            byte[] seq6 = new byte[] { 0x96, 0x96, 0x9d };
            circ.BitPosition = 0;
            int seq6Offset = circ.FindNextLatchSequence(seq6, seq6.Length, -1);
            if (seq6Offset != 28 * 8) {
                Debug.Assert(false, "Incorrect seq6 offset");
                return false;
            }

            // Confirm that multiple successive queries yield non-overlapping results.
            byte[] seq7 = new byte[] { 0x96, 0x96 };
            circ.BitPosition = 0;
            int seq7query1 = circ.FindNextLatchSequence(seq7, seq7.Length, -1);
            int seq7query2 = circ.FindNextLatchSequence(seq7, seq7.Length, -1);
            int seq7query3 = circ.FindNextLatchSequence(seq7, seq7.Length, -1);
            if (seq7query1 != 25 * 8) {
                Debug.Assert(false, "Incorrect seq7query1");
                return false;
            }
            if (seq7query2 != 27 * 8) {
                Debug.Assert(false, "Incorrect seq7query2");
                return false;
            }
            if (seq7query3 != seq7query1) {
                Debug.Assert(false, "Incorrect seq7query3");
                return false;
            }


            // Test a case where the search zone ends exactly at the end of the buffer, but the
            // end of the buffer isn't bit-aligned and the sequence isn't found.  This causes us
            // to wrap around by a couple of bits unexpectedly.
            CircularBitBuffer shortCirc = new CircularBitBuffer(testData, 0, 0, 7 * 8);
            shortCirc.BitPosition = 3 * 8;
            int shortQuery1 = shortCirc.FindNextLatchSequence(seq1, 3, 4 * 8);
            Debug.Assert(shortQuery1 == -1);

            return true;
        }

        private static bool DebugMiscTest() {
            byte[] testBuf = new byte[8];
            CircularBitBuffer circ = new CircularBitBuffer(testBuf, 0, 0, testBuf.Length * 8);
            if (circ.BitPosition != 0) {
                return false;
            }
            circ.AdjustBitPosition(2 * 8);
            circ.AdjustBitPosition(8 * 8);
            if (circ.BitPosition != 2 * 8) {
                Debug.Assert(false, "Incorrect forward wrap");
                return false;
            }
            circ.AdjustBitPosition(-4 * 8);
            if (circ.BitPosition != 6 * 8) {
                Debug.Assert(false, "Incorrect backward wrap");
                return false;
            }

            return true;
        }

        #endregion Unit tests
    }
}
