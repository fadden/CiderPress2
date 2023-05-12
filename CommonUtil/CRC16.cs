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
    /// Compute a CRC-16 (polynomial 0x1021).
    /// </summary>
    /// <remarks>
    /// <para>This is way more complicated than you might expect it to be.</para>
    /// <para>According to <see href="https://reveng.sourceforge.io/crc-catalogue/16.htm"/>,
    /// the algorithm commonly used on the Apple II is CRC-16/XMODEM, which is MSB-first
    /// and seeded with zero.  Seeding it with 0xffff computes CRC-16/IBM-3740, also known as
    /// CRC-16/CCITT-FALSE because it's commonly mistaken for CRC-CCITT.</para>
    /// <para>CRC-CCITT, or CRC-16/KERMIT, is LSB-first and seeded with zero.</para>
    /// </remarks>
    public static class CRC16 {
        private static readonly ushort[] sXMODEM_Tab = ComputeTableMSB(0x1021);
        private static readonly ushort[] sCCITT_Tab = ComputeTableLSB(0x8408);  // reversed poly

        /// <summary>
        /// Generates 256-entry LSB-first CRC table.
        /// </summary>
        private static ushort[] ComputeTableLSB(ushort poly) {
            ushort[] table = new ushort[256];

            for (int i = 0; i < 256; i++) {
                ushort val = (ushort)i;
                for (int j = 0; j < 8; j++) {
                    if ((val & 1) != 0) {
                        val = (ushort)((val >> 1) ^ poly);
                    } else {
                        val = (ushort)(val >> 1);
                    }
                }
                table[i] = val;
            }
            return table;
        }

        /// <summary>
        /// Generates 256-entry MSB-first CRC table.
        /// </summary>
        private static ushort[] ComputeTableMSB(ushort poly) {
            ushort[] table = new ushort[256];

            for (int i = 0; i < 256; i++) {
                ushort val = (ushort)(i << 8);
                for (int bit = 0; bit < 8; bit++) {
                    if ((val & 0x8000) != 0) {
                        val = (ushort)((val << 1) ^ poly);
                    } else {
                        val <<= 1;
                    }
                }
                table[i] = val;
            }
            return table;
        }

        /// <summary>
        /// Computes a CRC-16/CCITT (CRC-16/KERMIT) on part of a buffer of data.
        /// </summary>
        /// <param name="crc">Previously computed CRC value. Use 0x0000 as initial value.</param>
        /// <param name="buffer">Data to compute CRC on.</param>
        /// <param name="offset">Start offset within buffer.</param>
        /// <param name="count">Number of bytes to process.</param>
        /// <returns>Computed CRC value.</returns>
        public static ushort CCITT_OnBuffer(ushort crc, byte[] buffer, int offset, int count) {
            if (count <= 0) {
                return crc;
            }
            do {
                byte data = (byte)(buffer[offset++] ^ crc);
                crc = (ushort)(sCCITT_Tab[data] ^ (crc >> 8));
            } while (--count != 0);

            return crc;
        }

        /// <summary>
        /// Computes a CRC-16/XMODEM on part of a buffer of data.
        /// </summary>
        /// <param name="crc">Previously computed CRC value. Use 0x0000 as initial value.</param>
        /// <param name="buffer">Data to compute CRC on.</param>
        /// <param name="offset">Start offset within buffer.</param>
        /// <param name="count">Number of bytes to process.</param>
        /// <returns>Computed CRC value.</returns>
        public static ushort XMODEM_OnBuffer(ushort crc, byte[] buffer, int offset, int count) {
            if (count <= 0) {
                return crc;
            }
            do {
                byte data = (byte)(buffer[offset++] ^ (crc >> 8));
                crc = (ushort)(sXMODEM_Tab[data] ^ (crc << 8));
            } while (--count != 0);

            return crc;
        }

        /// <summary>
        /// Debug: evaluates the CRC functions.
        /// </summary>
        /// <remarks>
        /// <para>See the "CRC RevEng" catalog of 16-bit algorithms at
        /// <see href="https://reveng.sourceforge.io/crc-catalogue/"/> for a complete list.</para>
        ///
        /// <para>See <see href="https://stackoverflow.com/q/51960305/294248"/> for notes
        /// about LSB/MSB.</para>
        ///
        /// <para>The mathematical strength of a CRC computation is not affected by the initial
        /// seed value.  However, if the seed is zero, a buffer full of zeroes will yield a CRC of
        /// zero.  It's not uncommon for data to start with a bunch of leading zeroes, and we'd
        /// like the CRC to detect if the number of them is wrong.</para>
        /// </remarks>
        public static void DebugEvalFuncs() {
            // "0123456789"
            byte[] pattern = new byte[] { 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39 };

            // CRC-16/XMODEM
            // init=0x0000 refin=false refout=false xorout=0x0000 check=0x31c3
            ushort crc_xmodem = XMODEM_OnBuffer(0x0000, pattern, 0, pattern.Length);
            Debug.Assert(crc_xmodem == 0x31c3);

            // CRC-16/IBM-3740, a/k/a CRC-16/CCITT-FALSE
            // init=0xffff refin=false refout=false xorout=0x0000 check=0x29b1
            ushort crc_3740 = XMODEM_OnBuffer(0xffff, pattern, 0, pattern.Length);
            Debug.Assert(crc_3740 == 0x29b1);

            // CRC-16/KERMIT, a/k/a CRC-16/CCITT
            // init=0x0000 refin=true refout=true xorout=0x0000 check=0x2189
            ushort crc_kermit = CCITT_OnBuffer(0x0000, pattern, 0, pattern.Length);
            Debug.Assert(crc_kermit == 0x2189);
        }
    }
}
