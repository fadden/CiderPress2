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
using System.Text;

namespace CommonUtil {
    /// <summary>
    /// Configurable text formatter.
    /// </summary>
    public class Formatter {
        public const int HEX_DUMP_WIDTH = 73;       // addr + digits + chars

        public delegate char CharConvFunc(byte val);

        /// <summary>
        /// Format configuration.  Fill one of these out and pass it to the Formatter constructor.
        /// </summary>
        public class FormatConfig {
            /// <summary>
            /// Display hexadecimal digits in upper case?
            /// </summary>
            public bool UpperHexDigits { get; set; } = false;

            /// <summary>
            /// Function to convert bytes to printable characters for a hex dump.
            /// </summary>
            public CharConvFunc HexDumpConvFunc { get; set; } = CharConv_HighASCII;

            /// <summary>
            /// If true, put a space between the number and units, e.g. "800 KB" vs. "800KB".
            /// </summary>
            public bool SpaceBeforeUnits { get; set; } = true;

            /// <summary>
            /// Constructor.  Sets options to defaults.
            /// </summary>
            public FormatConfig() { }

            /// <summary>
            /// Copy constructor.
            /// </summary>
            /// <param name="src">Source object.</param>
            public FormatConfig(FormatConfig src) {
                UpperHexDigits = src.UpperHexDigits;
                HexDumpConvFunc = src.HexDumpConvFunc;
                SpaceBeforeUnits = src.SpaceBeforeUnits;
            }
        }

        /// <summary>
        /// Internal copy of format configuration.
        /// </summary>
        private FormatConfig mConfig;

        /// <summary>
        /// Copy of configuration.
        /// </summary>
        public FormatConfig Config { get { return new FormatConfig(mConfig); } }

        // Format character to use when formatting hex digits ('x' or 'X').
        private char mHexFmtChar;

        // Generated format strings for hex values ("X2", "X4", "X6", "X8").
        private string[] mHexValueFormats = new string[4];

        // Buffer to use when generating hex dump lines.
        private char[] mHexDumpBuffer;


        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="config">Format configuration.</param>
        public Formatter(FormatConfig config) {
            mConfig = new FormatConfig(config);     // make a copy

            if (config.UpperHexDigits) {
                mHexFmtChar = 'X';
            } else {
                mHexFmtChar = 'x';
            }
            for (int index = 0; index < 4; index++) {
                int width = (index + 1) * 2;
                mHexValueFormats[index] = mHexFmtChar + width.ToString();
            }

            // Prep the static parts of the hex dump buffer.
            mHexDumpBuffer = new char[HEX_DUMP_WIDTH];
            for (int i = 0; i < mHexDumpBuffer.Length; i++) {
                mHexDumpBuffer[i] = ' ';
            }
            mHexDumpBuffer[6] = ':';
        }


        private const string NO_DATE_STR = "[No Date]";
        private const string INVALID_DATE_STR = "<invalid>";

        private static readonly char[] sHexCharsLower = {
            '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', 'a', 'b', 'c', 'd', 'e', 'f'
        };
        private static readonly char[] sHexCharsUpper = {
            '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', 'A', 'B', 'C', 'D', 'E', 'F'
        };

        /// <summary>
        /// Formats a value in hexadecimal.  The width is padded with zeroes to make the
        /// length even (so it'll be $00, $0100, $010000, etc.)  If minDigits is nonzero,
        /// additional zeroes may be added.
        /// </summary>
        /// <param name="value">Value to format, up to 32 bits.</param>
        /// <param name="minDigits">Minimum width, in printed digits (e.g. 4 is "0000").</param>
        /// <returns>Formatted string.</returns>
        public string FormatHexValue(int value, int minDigits) {
            int width = minDigits > 2 ? minDigits : 2;
            if (width < 8 && value > 0xffffff) {
                width = 8;
            } else if (width < 6 && value > 0xffff) {
                width = 6;
            } else if (width < 4 && value > 0xff) {
                width = 4;
            }
            int index = (width / 2) - 1;
            return '$' + value.ToString(mHexValueFormats[index]);
        }

        /// <summary>
        /// Generates a 15-char ProDOS-style date/time string, or a string that identifies the
        /// date as missing or invalid.  The style uses two digits for the year, and omits seconds.
        /// </summary>
        /// <param name="when">Date/time object.</param>
        /// <returns>Formatted string.</returns>
        public string FormatDateTime(DateTime when) {
            if (when == TimeStamp.NO_DATE) {
                return NO_DATE_STR;
            } else if (when == TimeStamp.INVALID_DATE) {
                return INVALID_DATE_STR;
            } else {
                return when.ToString("dd-MMM-yy HH:mm");
            }
        }

        private const string ACCESS_LETTERS = "dnb??iwr";

        /// <summary>
        /// Formats ProDOS-style access flags.
        /// </summary>
        /// <param name="access">Access flag byte.</param>
        /// <returns>Formatted string.</returns>
        public string FormatAccessFlags(byte access) {
            StringBuilder sb = new StringBuilder(8);
            for (int i = 0; i < 8; i++) {
                if ((access & 0x80) != 0) {
                    sb.Append(ACCESS_LETTERS[i]);
                }
                access <<= 1;
            }
            return string.Intern(sb.ToString());    // most entries will be similar
        }

        /// <summary>
        /// Formats a value that represents the size of something stored on a disk, such as a
        /// file or a partition.
        /// </summary>
        /// <param name="length">Value to format.</param>
        /// <param name="baseUnit">Base units: sectors, blocks, or KiB.</param>
        /// <returns>Formatted string.</returns>
        public string FormatSizeOnDisk(long length, int baseUnit) {
            const int SECTOR_SIZE = 256;
            const int BLOCK_SIZE = 512;
            const int KBLOCK_SIZE = 1024;
            if (baseUnit == KBLOCK_SIZE || length >= 10 * 1024 * 1024) {
                if (length >= 1024 * 1024 * 1024) {
                    return string.Format("{0:F1}{1}GB", length / (1024.0 * 1024.0 * 1024.0),
                        mConfig.SpaceBeforeUnits ? " " : "");
                } else if (length >= 10 * 1024 * 1024) {
                    return string.Format("{0:F1}{1}MB", length / (1024.0 * 1024.0),
                        mConfig.SpaceBeforeUnits ? " " : "");
                } else {
                    return string.Format("{0:F0}{1}KB", length / 1024.0,
                        mConfig.SpaceBeforeUnits ? " " : "");
                }
            } else {
                long num = length / baseUnit;
                string unitStr;
                switch (baseUnit) {
                    case SECTOR_SIZE:
                        unitStr = "sector";
                        break;
                    case BLOCK_SIZE:
                        unitStr = "block";
                        break;
                    default:
                        unitStr = "unit";
                        break;
                }
                // Always put a space between the number and the units here.
                return string.Format("{0:D0} {1}{2}", num, unitStr, (num == 1) ? "" : "s");
            }
        }

        /// <summary>
        /// Formats a buffer of data as a hex dump.
        /// </summary>
        /// <param name="data">Reference to data.</param>
        /// <returns>StringBuilder with output.</returns>
        public StringBuilder FormatHexDump(byte[] data) {
            StringBuilder sb = new StringBuilder();
            for (int offset = 0; offset < data.Length; offset += 16) {
                int lineLen = Math.Min(16, data.Length - offset);
                FormatHexDumpLine(data, offset, lineLen, offset, sb);
                sb.AppendLine();
            }
            return sb;
        }

        /// <summary>
        /// Formats a buffer of data as a hex dump.
        /// </summary>
        /// <param name="data">Reference to data.</param>
        /// <param name="offset">Start offset.</param>
        /// <param name="length">Length of data.</param>
        /// <param name="sb">StringBuilder that receives output.</param>
        /// <returns>StringBuilder with output.</returns>
        public void FormatHexDump(byte[] data, int offset, int length, StringBuilder sb) {
            for (int i = 0; i < length; i += 16) {
                int lineLen = Math.Min(16, length - i);
                FormatHexDumpCommon(data, offset + i, lineLen, i);
                sb.Append(mHexDumpBuffer);
                sb.AppendLine();
            }
        }

        /// <summary>
        /// Formats up to 16 bytes of data into a single line hex dump.  The output is
        /// appended to the StringBuilder.
        /// </summary>
        /// <param name="data">Reference to data.</param>
        /// <param name="offset">Start offset.</param>
        /// <param name="sb">StringBuilder that receives output.</param>
        public void FormatHexDumpLine(byte[] data, int offset, int length, int addr,
                StringBuilder sb) {
            FormatHexDumpCommon(data, offset, length, addr);
            sb.Append(mHexDumpBuffer);
        }

        /// <summary>
        /// Formats up to 16 bytes of data into mHexDumpBuffer.
        /// </summary>
        /// <remarks>
        /// With a six-digit address field, the output is 6+1+1+(16*3)+1+16 = 73 chars wide.
        /// </remarks>
        private void FormatHexDumpCommon(byte[] data, int offset, int length, int addr) {
            Debug.Assert(offset >= 0 && offset < data.Length);
            Debug.Assert(data.Length < (1 << 24));
            const int DATA_COL = 8;
            const int ASCII_COL = 57;

            char[] hexChars = mConfig.UpperHexDigits ? sHexCharsUpper : sHexCharsLower;
            char[] outBuf = mHexDumpBuffer;

            int skip = addr & 0x0f;     // we skip this many entries...
            offset -= skip;             // ...so adjust offset to balance it
            addr &= ~0x0f;

            // address field
            for (int i = 5; i >= 0; i--) {
                outBuf[i] = hexChars[addr & 0x0f];
                addr >>= 4;
            }

            // If addr doesn't start at xxx0, pad it.
            int index;
            for (index = 0; index < skip; index++) {
                outBuf[DATA_COL + index * 3] = outBuf[DATA_COL + index * 3 + 1] =
                    outBuf[ASCII_COL + index] = ' ';
            }

            // hex digits and characters
            for (int i = 0; i < length; i++) {
                byte val = data[offset + index];
                outBuf[DATA_COL + index * 3] = hexChars[val >> 4];
                outBuf[DATA_COL + index * 3 + 1] = hexChars[val & 0x0f];
                outBuf[ASCII_COL + index] = mConfig.HexDumpConvFunc(val);
                index++;
            }

            // for partial line, clear out previous contents
            for (; index < 16; index++) {
                outBuf[DATA_COL + index * 3] =
                    outBuf[DATA_COL + index * 3 + 1] =
                    outBuf[ASCII_COL + index] = ' ';
            }
        }

        private const char MIDDLE_DOT = '\u00b7';

        /// <summary>
        /// Converts a byte into printable form.  The high bit is stripped.
        /// </summary>
        /// <param name="val">Value to convert.</param>
        /// <returns>Printable character.</returns>
        public static char CharConv_HighASCII(byte val) {
            val &= 0x7f;
            if (val < 0x20 || val == 0x7f) {        // [0x00,0x1f] + 0x7f + [0x80,0x9f] + 0xff
                // The Control Pictures group is a nice thought, but they're unreadably small,
                // and they're a hair wider than the monospace font glyphs.  Traditionally
                // hex dumps use a '.' for unreadable characters.  We use a middle-dot to
                // differentiate them from actual periods.
                //return (char)(val + ASCIIUtil.CTRL_PIC_START);
                //return (char)ASCIIUtil.CTRL_PIC_DEL;
                return MIDDLE_DOT;
            } else {
                return (char)val;
            }
        }

        /// <summary>
        /// Converts a byte into printable form.  The value is treated as ISO 8859-1.
        /// </summary>
        /// <param name="val">Value to convert.</param>
        /// <returns>Printable character.</returns>
        public static char CharConv_Latin(byte val) {
            // Replace C0 and C1 control codes.
            if (val < 0x20 || (val >= 0x7f && val <= 0x9f)) {
                return MIDDLE_DOT;
            } else {
                return (char)val;   // maps directly to Unicode BMP
            }
        }

        /// <summary>
        /// Converts a byte into printable form.  The value is treated as Mac OS Roman.
        /// </summary>
        /// <param name="val">Value to convert.</param>
        /// <returns>Printable character.</returns>
        public static char CharConv_MOR(byte val) {
            if (val < 0x20 || val == 0x7f) {
                return MIDDLE_DOT;
            } else {
                return MacChar.MacToUnicode(val, MacChar.Encoding.Roman);
            }
        }
    }
}
