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
using System.Text;

namespace CommonUtil {
    /// <summary>
    /// Utility functions for manipulating ASCII.
    /// </summary>
    public static class ASCIIUtil {
        public const int CTRL_PIC_START = 0x2400;
        public const int CTRL_PIC_END = 0x241f;
        // Control picture for DEL (0x7f).
        public const char CTRL_PIC_DEL = '\u2421';
        // Control picture to use for C1 control codes (0x80-9f in ISO 8859-1).  Use the
        // backwards '?', a/k/a SYMBOL FOR SUBSTITUTE FORM TWO.
        public const char CTRL_PIC_C1 = '\u2426';


        /// <summary>
        /// Makes a character printable, using the control pictures group for C0 control chars.
        /// Printable characters are returned unmodified.
        /// </summary>
        /// <param name="ch">Character to convert.</param>
        /// <returns>Printable character.</returns>
        public static char MakePrintable(char ch) {
            if (ch < 0x20) {
                return (char)(ch + CTRL_PIC_START);
            } else if (ch == 0x7f) {
                return CTRL_PIC_DEL;
            } else {
                return ch;
            }
        }

        /// <summary>
        /// Converts a control picture back to a raw control code.  Other characters are
        /// returned unmodified.
        /// </summary>
        /// <param name="ch">Character to convert.</param>
        /// <returns>Original character or control code.</returns>
        public static char MakeUnprintable(char ch) {
            if (ch >= CTRL_PIC_START && ch <= CTRL_PIC_END) {
                return (char)(ch - CTRL_PIC_START);
            } else if (ch == CTRL_PIC_DEL) {
                return '\x7f';
            } else {
                return ch;
            }
        }

        /// <summary>
        /// Converts an ASCII "Pascal string", which starts with a length byte, to a string.
        /// </summary>
        /// <param name="data">ASCII byte data.</param>
        /// <returns>Converted string.</returns>
        public static string PascalBytesToString(byte[] data) {
            int length = data[0];
            return Encoding.ASCII.GetString(data, 1, length);
        }

        /// <summary>
        /// Converts a string to an ASCII "Pascal string", which starts with a length byte, and
        /// stores it in a fixed-length buffer.  The unused area of the buffer will be zeroed.
        /// </summary>
        /// <param name="str">String to convert.</param>
        /// <param name="buf">Buffer to write data into.</param>
        public static void StringToFixedPascalBytes(string str, byte[] buf) {
            byte[] data = Encoding.ASCII.GetBytes(str);
            if (data.Length > 255) {
                throw new ArgumentException("string is longer than 255 bytes");
            }
            if (data.Length + 1 > buf.Length) {
                throw new ArgumentException("string is longer than the buffer can hold");
            }
            buf[0] = (byte)data.Length;
            for (int i = 0; i < data.Length; i++) {
                buf[i + 1] = data[i];
            }
            for (int i = data.Length + 1; i < buf.Length; i++) {
                buf[i] = 0x00;
            }
        }

        /// <summary>
        /// Reduces a string to ASCII values.
        /// </summary>
        /// <param name="chars">Array of characters to modify</param>
        /// <param name="replacement">Replacement character value.</param>
        public static void ReduceToASCII(char[] chars, char replacement) {
            for (int i = 0; i < chars.Length; i++) {
                char ch = ReduceToASCII(chars[i], replacement);
                chars[i] = ch;
            }
        }

        /// <summary>
        /// Converts a character to ASCII, stripping diacritical marks,
        /// e.g. 'á' and 'â' become 'a'.
        /// Anything that cannot be represented as ASCII is replaced with the supplied
        /// replacement character value.  C0 control codes are allowed.
        /// </summary>
        /// <remarks>
        /// Does NOT replace Control Pictures with control characters.
        /// </remarks>
        /// <param name="ch">Character to reduce.</param>
        /// <param name="replacement">Replacement character to use if all fails.</param>
        /// <returns>Reduced character.</returns>
        public static char ReduceToASCII(char ch, char replacement) {
            // Strip diacritical marks from letters.  This only handles the characters
            // included in Mac OS Roman, for the benefit of HFS -> ProDOS conversions.  Not
            // currently handling values that would become multiple characters (AE, FL, etc).
            //
            // For more thorough approaches, see:
            // https://stackoverflow.com/q/249087/294248
            // https://github.com/apache/lucenenet/blob/master/src/Lucene.Net.Analysis.Common/Analysis/Miscellaneous/ASCIIFoldingFilter.cs
            switch (ch) {
                case '\u00C0':  // LATIN CAPITAL LETTER A WITH GRAVE
                case '\u00C1':  // LATIN CAPITAL LETTER A WITH ACUTE
                case '\u00C2':  // LATIN CAPITAL LETTER A WITH CIRCUMFLEX
                case '\u00C3':  // LATIN CAPITAL LETTER A WITH TILDE
                case '\u00C4':  // LATIN CAPITAL LETTER A WITH DIAERESIS
                case '\u00C5':  // LATIN CAPITAL LETTER A WITH RING ABOVE
                    ch = 'A';
                    break;
                case '\u00E1':  // LATIN SMALL LETTER A WITH ACUTE
                case '\u00E0':  // LATIN SMALL LETTER A WITH GRAVE
                case '\u00E2':  // LATIN SMALL LETTER A WITH CIRCUMFLEX
                case '\u00E4':  // LATIN SMALL LETTER A WITH DIAERESIS
                case '\u00E3':  // LATIN SMALL LETTER A WITH TILDE
                case '\u00E5':  // LATIN SMALL LETTER A WITH RING ABOVE
                    ch = 'a';
                    break;
                case '\u00C7':  // LATIN CAPITAL LETTER C WITH CEDILLA
                    ch = 'C';
                    break;
                case '\u00E7':  // LATIN SMALL LETTER C WITH CEDILLA
                    ch = 'c';
                    break;
                case '\u00C8':  // LATIN CAPITAL LETTER E WITH GRAVE
                case '\u00C9':  // LATIN CAPITAL LETTER E WITH ACUTE
                case '\u00CA':  // LATIN CAPITAL LETTER E WITH CIRCUMFLEX
                case '\u00CB':  // LATIN CAPITAL LETTER E WITH DIAERESIS
                    ch = 'E';
                    break;
                case '\u00E8':  // LATIN SMALL LETTER E WITH GRAVE
                case '\u00E9':  // LATIN SMALL LETTER E WITH ACUTE
                case '\u00EA':  // LATIN SMALL LETTER E WITH CIRCUMFLEX
                case '\u00EB':  // LATIN SMALL LETTER E WITH DIAERESIS
                    ch = 'e';
                    break;
                case '\u0192':  // LATIN SMALL LETTER F WITH HOOK
                    ch = 'f';
                    break;
                case '\u00CC':  // LATIN CAPITAL LETTER I WITH GRAVE
                case '\u00CD':  // LATIN CAPITAL LETTER I WITH ACUTE
                case '\u00CE':  // LATIN CAPITAL LETTER I WITH CIRCUMFLEX
                case '\u00CF':  // LATIN CAPITAL LETTER I WITH DIAERESIS
                    ch = 'I';
                    break;
                case '\u00EC':  // LATIN SMALL LETTER I WITH GRAVE
                case '\u00ED':  // LATIN SMALL LETTER I WITH ACUTE
                case '\u00EE':  // LATIN SMALL LETTER I WITH CIRCUMFLEX
                case '\u00EF':  // LATIN SMALL LETTER I WITH DIAERESIS
                case '\u0131':  // LATIN SMALL LETTER DOTLESS I
                    ch = 'i';
                    break;
                case '\u00D1':  // LATIN CAPITAL LETTER N WITH TILDE
                    ch = 'N';
                    break;
                case '\u00F1':  // LATIN SMALL LETTER N WITH TILDE
                    ch = 'n';
                    break;
                case '\u00D2':  // LATIN CAPITAL LETTER O WITH GRAVE
                case '\u00D3':  // LATIN CAPITAL LETTER O WITH ACUTE
                case '\u00D4':  // LATIN CAPITAL LETTER O WITH CIRCUMFLEX
                case '\u00D5':  // LATIN CAPITAL LETTER O WITH TILDE
                case '\u00D6':  // LATIN CAPITAL LETTER O WITH DIAERESIS
                case '\u00D8':  // LATIN CAPITAL LETTER O WITH STROKE
                    ch = 'O';
                    break;
                case '\u00F2':  // LATIN SMALL LETTER O WITH GRAVE
                case '\u00F3':  // LATIN SMALL LETTER O WITH ACUTE
                case '\u00F4':  // LATIN SMALL LETTER O WITH CIRCUMFLEX
                case '\u00F5':  // LATIN SMALL LETTER O WITH TILDE
                case '\u00F6':  // LATIN SMALL LETTER O WITH DIAERESIS
                case '\u00F8':  // LATIN SMALL LETTER O WITH STROKE
                    ch = 'o';
                    break;
                case '\u00DF':  // LATIN SMALL LETTER SHARP S
                    ch = 's';
                    break;
                case '\u00D9':  // LATIN CAPITAL LETTER U WITH GRAVE
                case '\u00DA':  // LATIN CAPITAL LETTER U WITH ACUTE
                case '\u00DB':  // LATIN CAPITAL LETTER U WITH CIRCUMFLEX
                case '\u00DC':  // LATIN CAPITAL LETTER U WITH DIAERESIS
                    ch = 'U';
                    break;
                case '\u00F9':  // LATIN SMALL LETTER U WITH GRAVE
                case '\u00FA':  // LATIN SMALL LETTER U WITH ACUTE
                case '\u00FB':  // LATIN SMALL LETTER U WITH CIRCUMFLEX
                case '\u00FC':  // LATIN SMALL LETTER U WITH DIAERESIS
                    ch = 'u';
                    break;
                case '\u0178':  // LATIN CAPITAL LETTER Y WITH DIAERESIS
                    ch = 'Y';
                    break;
                case '\u00FF':  // LATIN SMALL LETTER Y WITH DIAERESIS
                    ch = 'y';
                    break;
                case '\u02DC':  // SMALL TILDE
                    ch = '~';
                    break;
            }

            // [DON'T] Convert control pictures back to control characters.
            if (ch >= CTRL_PIC_START && ch <= CTRL_PIC_END) {
                //ch = (char)(ch - CTRL_PIC_START);
            } else if (ch == CTRL_PIC_DEL) {
                //ch = (char)0x7f;
            } else if (ch > 0x7f) {
                // We're outside the ASCII range, replace the character.
                ch = replacement;
            }

            return ch;
        }
    }
}
