/*
 * Copyright 2018 faddenSoft
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
    /// Compute a standard CRC-32 (polynomial 0x04c11db7, or
    /// x^32+x^26+x^23+x^22+x^16+x^12+x^11+x^10+x^8+x^7+x^5+x^4+x^2+x+1).
    /// </summary>
    /// <remarks>
    /// <para>This algorithm is used by ZIP, gzip, PNG, and many others.</para>
    /// </remarks>
    public static class CRC32 {
        private static readonly uint[] sTable = ComputeTableLSB(0xedb88320);   // reversed poly

        private const uint INVERT = 0xffffffff;

        /// <summary>
        /// Generates 256-entry LSB-first CRC table.
        /// </summary>
        private static uint[] ComputeTableLSB(uint poly) {
            uint[] table = new uint[256];

            for (int i = 0; i < 256; i++) {
                uint val = (uint) i;
                for (int j = 0; j < 8; j++) {
                    if ((val & 1) != 0) {
                        val = (val >> 1) ^ poly;
                    } else {
                        val = val >> 1;
                    }
                }
                table[i] = val;
            }
            return table;
        }

        /// <summary>
        /// Computes a CRC-32 on part of a buffer of data.
        /// </summary>
        /// <param name="crc">Previously computed CRC value. Use zero as initial value.</param>
        /// <param name="buffer">Data to compute CRC on.</param>
        /// <param name="offset">Start offset within buffer.</param>
        /// <param name="count">Number of bytes to process.</param>
        /// <returns>Computed CRC value.</returns>
        public static uint OnBuffer(uint crc, byte[] buffer, int offset, int count) {
            if (count <= 0) {
                return crc;
            }
            crc = crc ^ INVERT;
            do {
                byte data = (byte)(buffer[offset++] ^ crc);
                crc = sTable[data] ^ (crc >> 8);
            } while (--count != 0);
            return crc ^ INVERT;
        }

        /// <summary>
        /// Computes a CRC-32 on a single byte.
        /// </summary>
        /// <param name="crc">Previously computed CRC value. Use zero as initial value.</param>
        /// <param name="val">Value to compute CRC on.</param>
        /// <returns>Computed CRC value.</returns>
        public static uint OnByte(uint crc, byte val) {
            crc = crc ^ INVERT;
            crc = sTable[(byte)(val ^ crc)] ^ (crc >> 8);
            return crc ^ INVERT;
        }

        /// <summary>
        /// Computes a CRC-32 on a buffer of data.
        /// </summary>
        /// <param name="crc">Previously computed CRC value. Use zero as initial value.</param>
        /// <param name="buffer">Data to compute CRC on.</param>
        /// <returns>Computed CRC value.</returns>
        public static uint OnWholeBuffer(uint crc, byte[] buffer) {
            return OnBuffer(crc, buffer, 0, buffer.Length);
        }

        /// <summary>
        /// Computes a CRC-32 on an entire file.  An exception will be thrown on file errors.
        /// </summary>
        /// <param name="pathName">Full path to file to open.</param>
        /// <returns>Computed CRC value.</returns>
        public static uint OnWholeFile(string pathName) {
            using (FileStream fs = File.Open(pathName, FileMode.Open, FileAccess.Read)) {
                return OnStream(fs);
            }
        }

        /// <summary>
        /// Computes a CRC-32 on a Stream.  The Stream will be read until EOF is reached.
        /// </summary>
        /// <param name="stream">Stream to read from.</param>
        /// <returns>Computed CRC value.</returns>
        public static uint OnStream(Stream stream) {
            byte[] buffer = new byte[8192];
            uint crc = 0;
            while (true) {
                int actual = stream.Read(buffer, 0, buffer.Length);
                if (actual == 0) {
                    break;
                }
                crc = OnBuffer(crc, buffer, 0, actual);
            }
            return crc;
        }

        /// <summary>
        /// Debug: evaluates the CRC function.
        /// </summary>
        /// <remarks>
        /// <para>See the "CRC RevEng" catalog at
        /// <see href="https://reveng.sourceforge.io/crc-catalogue/"/> for alternatives.</para>
        /// </remarks>
        public static void DebugEvalFuncs() {
            // "0123456789"
            byte[] pattern = new byte[] { 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39 };

            // CRC-32/ISO-HDLC, a/k/a CRC-32
            // init=0xffffffff refin=true refout=true xorout=0xffffffff check=0xcbf43926
            uint crc32 = OnBuffer(0, pattern, 0, pattern.Length);
            Debug.Assert(crc32 == 0xcbf43926);
        }
    }
}
