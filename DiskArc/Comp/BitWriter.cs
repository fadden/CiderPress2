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
    /// Writes bits to a data stream.
    /// </summary>
    public class BitWriter {
        private Stream mStream;
        private int mBitCount;
        private uint mBits;

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="input">Data stream.  Need not be seekable.</param>
        public BitWriter(Stream output) {
            Debug.Assert(output.CanWrite);
            mStream = output;
            mBitCount = 0;
        }

        /// <summary>
        /// Writes multiple bits, starting with the least significant bit.
        /// </summary>
        /// <param name="bits">Data bits.</param>
        /// <param name="count">Number of bits to write (max 24).</param>
        public void WriteBitsLE(uint bits, int count) {
            Debug.Assert(count >= 0 && count <= 24);    // limit ensures fit within 32-bit uint
            Debug.Assert(mBitCount >= 0);   // don't write after Finish()
            mBits |= bits << mBitCount;
            mBitCount += count;
            while (mBitCount >= 8) {
                mStream.WriteByte((byte)mBits);
                mBits >>= 8;
                mBitCount -= 8;
            }
        }

        /// <summary>
        /// Writes one byte to the output.
        /// </summary>
        public void WriteByteLE(byte by) {
            WriteBitsLE(by, 8);
        }

        /// <summary>
        /// Writes any pending bits to the stream.  The remainder of the byte will be zeroed.
        /// After this is called, no more data may be written to the stream.
        /// </summary>
        public void Finish() {
            if (mBitCount != 0) {
                mStream.WriteByte((byte)mBits);
            }
            mBitCount = -1024;
        }
    }
}
