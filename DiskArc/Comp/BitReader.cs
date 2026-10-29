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

namespace DiskArc.Comp {
    /// <summary>
    /// Reads bits from a data stream.
    /// </summary>
    /// <remarks>
    /// We don't seek or test the length of the input stream, in case it's not file-backed.
    /// </remarks>
    public class BitReader {
        private Stream mStream;
        private int mStreamLength;
        private int mBitCount;
        private const int BUF_BITS = 24;
        private uint mBits;

        /// <summary>
        /// Returns the amount of data left in the stream.
        /// </summary>
        public int Remaining => mStreamLength;

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="input">Data stream.  Need not be seekable.</param>
        /// <param name="length">Length of data stream.  May be shorter than the actual length
        ///   of the stream.</param>
        public BitReader(Stream input, int length) {
            mStream = input;
            mStreamLength = length;
            mBitCount = 0;
        }

        /// <summary>
        /// Reads and returns a single bit (as integer 0 or 1).
        /// </summary>
        public int ReadBitLE() {
            return (int)ReadBitsLE(1);
        }

        /// <summary>
        /// Reads and returns multiple bits, pulling from the least significant bit first.
        /// </summary>
        /// <remarks>
        /// You can get the same result by pulling the lsb repeatedly, and placing it in the
        /// appropriate position, e.g.
        /// <code>result |= newBit &lt;&lt; bitsSoFar++</code>
        /// </remarks>
        /// <param name="count">Number of bits to read (max 24).</param>
        public uint ReadBitsLE(int count) {
            Debug.Assert(count <= BUF_BITS);
            if (mBitCount < BUF_BITS) {
                LoadBitsLE();
            }
            if (count >= mBitCount) {
                throw new IOException("ran out of bits");
            }
            // Grab from the little end.
            uint result = mBits & ((1U << count) - 1);
            mBits >>= count;
            mBitCount -= count;
            return result;
        }

        /// <summary>
        /// Loads the bit buffer for little-endian access.
        /// </summary>
        private void LoadBitsLE() {
            while (mBitCount < BUF_BITS) {
                if (mStreamLength == 0) {
                    // Can't refill buffer, but it's not an error until the buffer is empty.
                    return;
                }
                int newBits = mStream.ReadByte();
                if (newBits < 0) {
                    // Actual stream length was shorter than expected.
                    throw new IOException("passed end of stream unexpectedly");
                }
                mStreamLength--;
                // Add to the big end of the buffer.
                mBits |= (uint)newBits << mBitCount;
                mBitCount += 8;
            }
        }
    }
}
