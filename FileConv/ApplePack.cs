/*
 * Copyright 2023 faddenSoft
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

using CommonUtil;

namespace FileConv {
    /// <summary>
    /// Functions for working with Apple's PackBytes and PackBits compression algorithms.
    /// </summary>
    public static class ApplePack {
        #region PackBytes

        private const byte FLAG_NORUN = 0x00;
        private const byte FLAG_RUN8 = 0x40;
        private const byte FLAG_RUN32 = 0x80;
        private const byte FLAG_RUN8_4 = 0xc0;

        /// <summary>
        /// Unpacks bytes in Apple PackBytes format.  This runs until all input has been consumed.
        /// </summary>
        /// <remarks>
        /// <para>The general format for PackBytes is a flag+length byte, followed by an amount of
        /// data specified by that byte.  This is effectively run-length encoding, though the
        /// use of 32-bit patterns can improve the compression ratio vs. a single-byte
        /// approach.</para>
        /// <code>Flag values:
        ///   00xxxxxx: 1 to 64 bytes, all different (data length == N)
        ///   01xxxxxx: 1 to 64 repeats of an 8-bit value (data length == 1)
        ///   10xxxxxx: 1 to 64 repeats of a 32-bit value (data length == 4)
        ///   11xxxxxx: 1 to 64 repeats of an 8-bit value repeated 4x (data length == 1)
        /// </code>
        /// <para>See also IIgs Tech Note #94, "Packing It In (and Out)".</para>
        /// </remarks>
        /// <param name="src">Source buffer.</param>
        /// <param name="srcOffset">Offset in buffer of first byte of data.</param>
        /// <param name="srcLen">Length of source data.</param>
        /// <param name="dst">Destination buffer.</param>
        /// <param name="dstOffset">Offset in buffer where output should start.</param>
        /// <returns>Number of bytes unpacked, -1 if the input underflowed, or -2 if the
        ///   output overflowed.</returns>
        public static int UnpackBytes(byte[] src, int srcOffset, int srcLen,
                byte[] dst, int dstOffset) {
            Debug.Assert(srcOffset >= 0 && srcLen >= 0);
            Debug.Assert(src.Length >= srcOffset + srcLen);
            Debug.Assert(dstOffset >= 0);
            Debug.Assert(dst.Length > dstOffset);

            if (srcLen == 0) {
                return 0;
            }

            int origDstOffset = dstOffset;

            while (srcLen > 0) {
                byte flag = src[srcOffset++];
                int flagCount = (flag & 0x3f) + 1;      // low 6 bits

                // Figure out how many bytes of input and output we need, and make sure we
                // have room.
                int inCount, outCount;
                switch (flag & 0xc0) {
                    case FLAG_NORUN:
                        inCount = flagCount;
                        outCount = flagCount;
                        break;
                    case FLAG_RUN8:
                        inCount = 1;
                        outCount = flagCount;
                        break;
                    case FLAG_RUN32:
                        inCount = 4;
                        outCount = flagCount * 4;
                        break;
                    case FLAG_RUN8_4:
                        inCount = 1;
                        outCount = flagCount * 4;
                        break;
                    default:
                        Debug.Assert(false);
                        return -1024;
                }

                if (srcOffset + inCount > src.Length) {
                    Debug.WriteLine("UnpackBytes underrun (flag=$" + flag.ToString("x2") + ")");
                    return -1;
                }
                if (dstOffset + outCount > dst.Length) {
                    Debug.WriteLine("UnpackBytes overrun (flag=$" + flag.ToString("x2") + ")");
                    return -2;
                }

                switch (flag & 0xc0) {
                    case FLAG_NORUN:
                        for (int i = 0; i < flagCount; i++) {
                            dst[dstOffset++] = src[srcOffset++];
                        }
                        break;
                    case FLAG_RUN8:
                        byte val8 = src[srcOffset++];
                        for (int i = 0; i < flagCount; i++) {
                            dst[dstOffset++] = val8;
                        }
                        break;
                    case FLAG_RUN32:
                        for (int i = 0; i < flagCount; i++) {
                            dst[dstOffset++] = src[srcOffset];
                            dst[dstOffset++] = src[srcOffset + 1];
                            dst[dstOffset++] = src[srcOffset + 2];
                            dst[dstOffset++] = src[srcOffset + 3];
                        }
                        srcOffset += 4;
                        break;
                    case FLAG_RUN8_4:
                        byte val8_4 = src[srcOffset++];
                        for (int i = 0; i < flagCount * 4; i++) {
                            dst[dstOffset++] = val8_4;
                        }
                        break;
                }

                srcLen -= inCount + 1;
            }

            return dstOffset - origDstOffset;
        }

        #endregion PackBytes

        #region PackBits

        /// <summary>
        /// Unpacks a buffer of data compressed with Apple's PackBits run-length encoding format.
        /// </summary>
        /// <remarks>
        /// <para>The data is regarded as a series of signed bytes, probably due to the
        /// characteristics of the Pascal programming language used by Apple.  The data stream
        /// is a code byte followed by zero or more data bytes:</para>
        /// <list type="bullet">
        ///   <item>N = -128: reserved, ignore (could be an end-of-stream indicator)</item>
        ///   <item>N &lt; 0: repeat the next byte (1-N) times</item>
        ///   <item>N &gt;= 0: copy the next (1+N) bytes</item>
        /// </list>
        /// <para>Example:</para>
        /// <code>FE AA 02 80 00 2A FD AA 03 80 00 2A 22 F7 AA  unpacks to
        /// AA AA AA 80 00 2A AA AA AA AA 80 00 2A 22 AA AA AA AA AA AA AA AA AA AA</code>
        /// <para>The caller must specify either the source length or destination length.
        /// We accept both here, and will stop when either one is reached.</para>
        /// <para><see href="http://web.archive.org/web/20080705155158/http://developer.apple.com/technotes/tn/tn1023.html"/></para>
        /// </remarks>
        /// <param name="src">Compressed data buffer.</param>
        /// <param name="srcOffset">Initial offset in data buffer.  Updated to point past the
        ///   last byte consumed.</param>
        /// <param name="srcLen">Maximum amount of data that may be consumed.</param>
        /// <param name="dst">Buffer for uncompressed data.</param>
        /// <param name="dstOffset">Initial offset in output buffer.</param>
        /// <param name="dstLen">Maximum length of output.</param>
        public static bool UnpackBits(byte[] src, ref int srcOffset, int srcLen,
                byte[] dst, int dstOffset, int dstLen) {
            if (srcLen == 0) {
                // Something failed earlier... file truncated?
                RawData.MemSet(dst, dstOffset, dstLen, 0xaa);
                return false;
            }

            int srcEnd = srcOffset + srcLen;
            int dstEnd = dstOffset + dstLen;

            while (srcOffset < srcEnd && dstOffset < dstEnd) {
                sbyte flagCount = (sbyte)src[srcOffset++];
                if (flagCount == -128) {
                    // Ignore.
                } else if (flagCount < 0) {
                    // Run of single byte value.
                    if (srcOffset == srcEnd) {
                        Debug.WriteLine("UnpackBits: source ended mid-run");
                        return false;
                    }
                    byte bval = src[srcOffset++];
                    int count = 1 - flagCount;
                    while (count-- != 0 && dstOffset != dstEnd) {
                        dst[dstOffset++] = bval;
                    }
                } else {
                    // Run of literals.
                    int count = flagCount + 1;
                    while (count-- != 0 && srcOffset != srcEnd && dstOffset != dstEnd) {
                        dst[dstOffset++] = src[srcOffset++];
                    }
                }
            }

            if (dstOffset < dstEnd) {
                Debug.WriteLine("UnpackBits: ran out of source material");
                while (dstOffset < dstEnd) {
                    dst[dstOffset++] = 0x55;
                }
                return false;
            }
            return true;
        }

        #endregion PackBits
    }
}
