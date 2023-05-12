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
    /// Classic Macintosh character set conversion utilities.
    /// </summary>
    /// <remarks>
    /// <para>There are a number of regional encodings.  Only Mac OS Roman is currently
    /// supported.</para>
    /// </remarks>
    public static class MacChar {
        /// <summary>
        /// Character encoding identifier.
        /// </summary>
        public enum Encoding {
            Unknown = 0,

            /// <summary>
            /// Standard conversion.  Use for comments and documents.
            /// </summary>
            Roman,

            /// <summary>
            /// Conversion with control characters converted to printable form.  Use for
            /// filenames and other places where control characters (notably CR/LF) are not
            /// normally expected to appear.
            /// </summary>
            RomanShowCtrl
        }

        /// <summary>
        /// Converts a buffer of Mac OS-encoded bytes into a Unicode string.
        /// </summary>
        /// <param name="macStr">Mac-encoded data buffer.</param>
        /// <param name="offset">Start offset.</param>
        /// <param name="length">String length.</param>
        /// <param name="enc">Encoding type.</param>
        /// <returns>Converted string.</returns>
        public static string MacToUnicode(byte[] macStr, int offset, int length, Encoding enc) {
            char[] map = GetMacToUnicodeMap(enc);
            if (length == 0) {
                return string.Empty;
            }

            char[] uniStr = new char[length];
            for (int i = 0; i < length; i++) {
                uniStr[i] = map[macStr[offset + i]];
            }
            return new string(uniStr);
        }

        /// <summary>
        /// Converts a single Mac OS-encoded byte to a Unicode character.
        /// </summary>
        /// <param name="val">Value to convert.</param>
        /// <param name="enc">Encoding type.</param>
        /// <returns>Converted character.</returns>
        public static char MacToUnicode(byte val, Encoding enc) {
            char[] map = GetMacToUnicodeMap(enc);
            return map[val];
        }

        private static char[] GetMacToUnicodeMap(Encoding enc) {
            switch (enc) {
                case Encoding.Roman:
                    return sRomanToUnicode;
                case Encoding.RomanShowCtrl:
                    return sRomanShowCtrlToUnicode;
                default:
                    throw new NotImplementedException("Unknown encoding");
            }
        }

        /// <summary>
        /// Converts a Mac OS string with a leading length byte (e.g. Str31 or Str255) to a
        /// Unicode string.
        /// </summary>
        /// <param name="macStr">Mac-encoded string data buffer.</param>
        /// <param name="offset">Start offset.</param>
        /// <param name="enc">Encoding type.</param>
        /// <returns>Converted string.</returns>
        public static string MacStrToUnicode(byte[] macStr, int offset, Encoding enc) {
            byte length = macStr[offset];
            return MacToUnicode(macStr, offset + 1, length, enc);
        }

        private static short[] GetUnicodeToMacMap(Encoding enc) {
            switch (enc) {
                case Encoding.Roman:
                    return sUnicodeToRoman;
                case Encoding.RomanShowCtrl:
                    return sUnicodeToRomanShowCtrl;
                default:
                    throw new NotImplementedException("Unknown encoding");
            }
        }

        /// <summary>
        /// Converts a Unicode string into a buffer of Mac OS-encoded bytes.  Unsupported
        /// characters are converted to '?'.
        /// </summary>
        /// <remarks>
        /// The output byte buffer should have the same length as the input string.
        /// </remarks>
        /// <param name="str">String to convert.</param>
        /// <param name="buf">Output buffer.</param>
        /// <param name="offset">Start offset in output buffer.</param>
        /// <param name="enc">Encoding type.</param>
        public static void UnicodeToMac(string str, byte[] buf, int offset, Encoding enc) {
            short[] map = GetUnicodeToMacMap(enc);
            for (int i = 0; i < str.Length; i++) {
                short macVal = map[str[i]];
                if (macVal < 0) {
                    Debug.WriteLine("String has invalid value 0x" +
                        ((int)str[i]).ToString("x4") + "('" + str[i] + "')");
                    macVal = (short)'?';
                }
                buf[offset + i] = (byte)macVal;
            }
        }

        /// <summary>
        /// Converts a Unicode string into a buffer of Mac OS-encoded bytes.  Unsupported
        /// characters are converted to '?'.
        /// </summary>
        /// <param name="str">String to convert.</param>
        /// <param name="enc">Encoding type.</param>
        /// <returns>Byte array with Mac OS chars.</returns>
        public static byte[] UnicodeToMac(string str, Encoding enc) {
            byte[] result = new byte[str.Length];
            UnicodeToMac(str, result, 0, enc);
            return result;
        }

        /// <summary>
        /// Converts a Unicode character into a Mac OS-encoded byte value.  If the character
        /// is not supported, a replacement value is returned.
        /// </summary>
        /// <param name="ch">Character to convert.</param>
        /// <param name="unk">Replacement value to use for unknown characters.</param>
        /// <param name="enc">Encoding type.</param>
        /// <returns>Mac OS character byte value.</returns>
        public static byte UnicodeToMac(char ch, byte unk, Encoding enc) {
            short[] map = GetUnicodeToMacMap(enc);
            short macVal = map[ch];
            if (macVal < 0) {
                macVal = unk;
            }
            return (byte)macVal;
        }

        /// <summary>
        /// Converts a Unicode string to a length-delimited Mac OS string, in a new buffer.
        /// </summary>
        /// <param name="str">String to encode.</param>
        /// <param name="enc">Encoding type.</param>
        /// <returns>Length-delimited Mac OS string.</returns>
        /// <exception cref="ArgumentException">String is more than 255 characters long.</exception>
        public static byte[] UnicodeToMacStr(string str, Encoding enc) {
            if (str.Length > 255) {
                throw new ArgumentException("String too long");
            }
            byte[] outBuf = new byte[str.Length + 1];
            outBuf[0] = (byte)str.Length;
            UnicodeToMac(str, outBuf, 1, enc);
            return outBuf;
        }

        /// <summary>
        /// Determines whether a character is included in the specified encoding.
        /// </summary>
        /// <param name="ch">Character to validate.</param>
        /// <param name="enc">Encoding type.</param>
        /// <returns>True if the string is valid.</returns>
        public static bool IsCharValid(char ch, Encoding enc) {
            short[] map = GetUnicodeToMacMap(enc);
            return !(ch >= map.Length || map[ch] < 0);
        }

        /// <summary>
        /// Determines whether a string is comprised entirely of characters in the specified
        /// encoding.
        /// </summary>
        /// <param name="str">String to validate.</param>
        /// <param name="enc">Encoding type.</param>
        /// <returns>True if the string is valid.</returns>
        public static bool IsStringValid(string str, Encoding enc) {
            short[] map = GetUnicodeToMacMap(enc);
            for (int i = 0; i < str.Length; i++) {
                char ch = str[i];
                if (ch >= map.Length || map[ch] < 0) {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Compares two length-delimited Mac OS Roman strings, using the HFS filename
        /// comparison rules.
        /// </summary>
        /// <param name="str1">Buffer with first string.</param>
        /// <param name="offset1">Offset of first string within buffer.</param>
        /// <param name="str2">Buffer with second string.</param>
        /// <param name="offset2">Offset of second string within buffer.</param>
        /// <returns>A value less than, equal to, or greater than zero depending on whether the
        ///   first string is less than, equal to, or greater than the second string.</returns>
        public static int CompareHFSFileNames(byte[] str1, int offset1,
                byte[] str2, int offset2) {
            byte len1 = str1[offset1];
            byte len2 = str2[offset2];
            while (len1 != 0 && len2 != 0) {
                byte ch1 = str1[++offset1];
                byte ch2 = str2[++offset2];
                if (ch1 != ch2) {
                    // Bytes aren't identical, do the table lookup and check again.  This can
                    // change the order, as well as nullify case differences.
                    int val1 = sRomanCompare[ch1];
                    int val2 = sRomanCompare[ch2];
                    if (val1 != val2) {
                        return val1 - val2;
                    }
                }
                --len1;
                --len2;
            }
            if (len1 == 0 && len2 == 0) {
                // Exact match.
                return 0;
            } else if (len1 == 0) {
                // String1 is shorter, so it comes first.
                return -1;
            } else {
                // String2 is shorter.
                return 1;
            }
        }

        /// <summary>
        /// Compares two Unicode strings, using the HFS filename comparison rules.  Assumes
        /// the filenames were converted with the "show ctrl" table.
        /// </summary>
        /// <param name="fileName1">First filename.</param>
        /// <param name="fileName2">Second filename.</param>
        /// <returns>A value less than, equal to, or greater than zero depending on whether the
        ///   first string is less than, equal to, or greater than the second string.</returns>
        public static int CompareHFSFileNames(string fileName1, string fileName2) {
            int len1 = fileName1.Length;
            int len2 = fileName2.Length;
            int offset1 = 0;
            int offset2 = 0;
            while (len1 != 0 && len2 != 0) {
                short mval1 = sUnicodeToRomanShowCtrl[fileName1[offset1++]];
                short mval2 = sUnicodeToRomanShowCtrl[fileName2[offset2++]];
                if (mval1 < 0 || mval2 < 0) {
                    throw new ArgumentException("Non-Mac Roman character found");
                }
                if (mval1 != mval2) {
                    // Bytes aren't identical, do the table lookup and check again.  This can
                    // change the order, as well as nullify case differences.
                    int val1 = sRomanCompare[mval1];
                    int val2 = sRomanCompare[mval2];
                    if (val1 != val2) {
                        return val1 - val2;
                    }
                }
                --len1;
                --len2;
            }
            if (len1 == 0 && len2 == 0) {
                // Exact match.
                return 0;
            } else if (len1 == 0) {
                // String1 is shorter, so it comes first.
                return -1;
            } else {
                // String2 is shorter.
                return 1;
            }
        }

        /// <summary>
        /// Filename comparison class, for use with .NET Sort functions.
        /// </summary>
        public class HFSFileNameComparer : Comparer<string> {
            public override int Compare(string? fileName1, string? fileName2) {
                Debug.Assert(fileName1 != null && fileName2 != null);
                return CompareHFSFileNames(fileName1, fileName2);
            }
        }

        /// <summary>
        /// Converts a 32-bit big-endian value to a four-character strings.  Useful for HFS
        /// creator and file types, and Mac resource types.  Control characters are converted
        /// to the printable range.
        /// </summary>
        /// <param name="val">Value to convert.</param>
        /// <returns>Filtered 4-character string.</returns>
        public static string StringifyMacConstant(uint val) {
            char[] arr = new char[4];
            for (int i = 0; i < 4; i++) {
                byte bval = ((byte)(val >> (3 - i) * 8));
                arr[i] = MacToUnicode(bval, Encoding.RomanShowCtrl);
            }
            return new string(arr);
        }

        /// <summary>
        /// Converts a 4-character Mac OS Roman string to a 4-byte integer.  Control characters
        /// are expected to be in the printable range.
        /// </summary>
        /// <param name="typeStr">String to convert.  All characters must be valid
        ///   Mac OS Roman.</param>
        /// <returns>Integer equivalent.</returns>
        /// <exception cref="ArgumentException">Invalid string.</exception>
        public static uint IntifyMacConstantString(string typeStr) {
            if (typeStr.Length != 4) {
                throw new ArgumentException("Type string has incorrect length: '" + typeStr + "'");
            }
            uint result = 0;
            for (int i = 0; i < 4; i++) {
                byte bch = UnicodeToMac(typeStr[i], 0x00, Encoding.RomanShowCtrl);
                result = (result << 8) | bch;
            }
            return result;
        }

        #region Mac OS Roman

        /// <summary>
        /// <para>Maps Mac OS Roman byte values to Unicode.  Mapping comes from
        /// <see href="https://www.unicode.org/Public/MAPPINGS/VENDORS/APPLE/ROMAN.TXT"/>.</para>
        ///
        /// <para>An older set of glyphs had 4 printable entries in the control character range
        /// (0x11-14), and no glyphs for 0xc9-ff.  The glyphs were only defined in the system
        /// font (Chicago).  See _Inside Macintosh, Volume I_ page I-221 for a chart.</para>
        ///
        /// <para>In 1998, for Mac OS 8.5, entry 0xdb was changed from CURRENCY SIGN
        /// ('\u00a4') to EURO SIGN ('\u20ac').  Because this is designed for use with
        /// older systems, CURRENCY SIGN is still used here.</para>
        /// </summary>
        private static readonly char[] sRomanToUnicode = new char[256] {
            /*0x00*/ '\u0000',  // NULL
            /*0x01*/ '\u0001',  // START OF HEADING
            /*0x02*/ '\u0002',  // START OF TEXT
            /*0x03*/ '\u0003',  // END OF TEXT
            /*0x04*/ '\u0004',  // END OF TRANSMISSION
            /*0x05*/ '\u0005',  // ENQUIRY
            /*0x06*/ '\u0006',  // ACKNOWLEDGE
            /*0x07*/ '\u0007',  // BELL
            /*0x08*/ '\u0008',  // BACKSPACE
            /*0x09*/ '\u0009',  // HORIZONTAL TABULATION
            /*0x0a*/ '\u000a',  // LINE FEED
            /*0x0b*/ '\u000b',  // VERTICAL TABULATION
            /*0x0c*/ '\u000c',  // FORM FEED
            /*0x0d*/ '\u000d',  // CARRIAGE RETURN
            /*0x0e*/ '\u000e',  // SHIFT OUT
            /*0x0f*/ '\u000f',  // SHIFT IN
            /*0x10*/ '\u0010',  // DATA LINK ESCAPE
            /*0x11*/ '\u0011',  // DEVICE CONTROL ONE
            /*0x12*/ '\u0012',  // DEVICE CONTROL TWO
            /*0x13*/ '\u0013',  // DEVICE CONTROL THREE
            /*0x14*/ '\u0014',  // DEVICE CONTROL FOUR
            /*0x15*/ '\u0015',  // NEGATIVE ACKNOWLEDGE
            /*0x16*/ '\u0016',  // SYNCHRONOUS IDLE
            /*0x17*/ '\u0017',  // END OF TRANSMISSION BLOCK
            /*0x18*/ '\u0018',  // CANCEL
            /*0x19*/ '\u0019',  // END OF MEDIUM
            /*0x1a*/ '\u001a',  // SUBSTITUTE
            /*0x1b*/ '\u001b',  // ESCAPE
            /*0x1c*/ '\u001c',  // FILE SEPARATOR
            /*0x1d*/ '\u001d',  // GROUP SEPARATOR
            /*0x1e*/ '\u001e',  // RECORD SEPARATOR
            /*0x1f*/ '\u001f',  // UNIT SEPARATOR
            /*0x20*/ '\u0020',  // SPACE
            /*0x21*/ '\u0021',  // EXCLAMATION MARK
            /*0x22*/ '\u0022',  // QUOTATION MARK
            /*0x23*/ '\u0023',  // NUMBER SIGN
            /*0x24*/ '\u0024',  // DOLLAR SIGN
            /*0x25*/ '\u0025',  // PERCENT SIGN
            /*0x26*/ '\u0026',  // AMPERSAND
            /*0x27*/ '\u0027',  // APOSTROPHE
            /*0x28*/ '\u0028',  // LEFT PARENTHESIS
            /*0x29*/ '\u0029',  // RIGHT PARENTHESIS
            /*0x2A*/ '\u002A',  // ASTERISK
            /*0x2B*/ '\u002B',  // PLUS SIGN
            /*0x2C*/ '\u002C',  // COMMA
            /*0x2D*/ '\u002D',  // HYPHEN-MINUS
            /*0x2E*/ '\u002E',  // FULL STOP
            /*0x2F*/ '\u002F',  // SOLIDUS
            /*0x30*/ '\u0030',  // DIGIT ZERO
            /*0x31*/ '\u0031',  // DIGIT ONE
            /*0x32*/ '\u0032',  // DIGIT TWO
            /*0x33*/ '\u0033',  // DIGIT THREE
            /*0x34*/ '\u0034',  // DIGIT FOUR
            /*0x35*/ '\u0035',  // DIGIT FIVE
            /*0x36*/ '\u0036',  // DIGIT SIX
            /*0x37*/ '\u0037',  // DIGIT SEVEN
            /*0x38*/ '\u0038',  // DIGIT EIGHT
            /*0x39*/ '\u0039',  // DIGIT NINE
            /*0x3A*/ '\u003A',  // COLON
            /*0x3B*/ '\u003B',  // SEMICOLON
            /*0x3C*/ '\u003C',  // LESS-THAN SIGN
            /*0x3D*/ '\u003D',  // EQUALS SIGN
            /*0x3E*/ '\u003E',  // GREATER-THAN SIGN
            /*0x3F*/ '\u003F',  // QUESTION MARK
            /*0x40*/ '\u0040',  // COMMERCIAL AT
            /*0x41*/ '\u0041',  // LATIN CAPITAL LETTER A
            /*0x42*/ '\u0042',  // LATIN CAPITAL LETTER B
            /*0x43*/ '\u0043',  // LATIN CAPITAL LETTER C
            /*0x44*/ '\u0044',  // LATIN CAPITAL LETTER D
            /*0x45*/ '\u0045',  // LATIN CAPITAL LETTER E
            /*0x46*/ '\u0046',  // LATIN CAPITAL LETTER F
            /*0x47*/ '\u0047',  // LATIN CAPITAL LETTER G
            /*0x48*/ '\u0048',  // LATIN CAPITAL LETTER H
            /*0x49*/ '\u0049',  // LATIN CAPITAL LETTER I
            /*0x4A*/ '\u004A',  // LATIN CAPITAL LETTER J
            /*0x4B*/ '\u004B',  // LATIN CAPITAL LETTER K
            /*0x4C*/ '\u004C',  // LATIN CAPITAL LETTER L
            /*0x4D*/ '\u004D',  // LATIN CAPITAL LETTER M
            /*0x4E*/ '\u004E',  // LATIN CAPITAL LETTER N
            /*0x4F*/ '\u004F',  // LATIN CAPITAL LETTER O
            /*0x50*/ '\u0050',  // LATIN CAPITAL LETTER P
            /*0x51*/ '\u0051',  // LATIN CAPITAL LETTER Q
            /*0x52*/ '\u0052',  // LATIN CAPITAL LETTER R
            /*0x53*/ '\u0053',  // LATIN CAPITAL LETTER S
            /*0x54*/ '\u0054',  // LATIN CAPITAL LETTER T
            /*0x55*/ '\u0055',  // LATIN CAPITAL LETTER U
            /*0x56*/ '\u0056',  // LATIN CAPITAL LETTER V
            /*0x57*/ '\u0057',  // LATIN CAPITAL LETTER W
            /*0x58*/ '\u0058',  // LATIN CAPITAL LETTER X
            /*0x59*/ '\u0059',  // LATIN CAPITAL LETTER Y
            /*0x5A*/ '\u005A',  // LATIN CAPITAL LETTER Z
            /*0x5B*/ '\u005B',  // LEFT SQUARE BRACKET
            /*0x5C*/ '\u005C',  // REVERSE SOLIDUS
            /*0x5D*/ '\u005D',  // RIGHT SQUARE BRACKET
            /*0x5E*/ '\u005E',  // CIRCUMFLEX ACCENT
            /*0x5F*/ '\u005F',  // LOW LINE
            /*0x60*/ '\u0060',  // GRAVE ACCENT
            /*0x61*/ '\u0061',  // LATIN SMALL LETTER A
            /*0x62*/ '\u0062',  // LATIN SMALL LETTER B
            /*0x63*/ '\u0063',  // LATIN SMALL LETTER C
            /*0x64*/ '\u0064',  // LATIN SMALL LETTER D
            /*0x65*/ '\u0065',  // LATIN SMALL LETTER E
            /*0x66*/ '\u0066',  // LATIN SMALL LETTER F
            /*0x67*/ '\u0067',  // LATIN SMALL LETTER G
            /*0x68*/ '\u0068',  // LATIN SMALL LETTER H
            /*0x69*/ '\u0069',  // LATIN SMALL LETTER I
            /*0x6A*/ '\u006A',  // LATIN SMALL LETTER J
            /*0x6B*/ '\u006B',  // LATIN SMALL LETTER K
            /*0x6C*/ '\u006C',  // LATIN SMALL LETTER L
            /*0x6D*/ '\u006D',  // LATIN SMALL LETTER M
            /*0x6E*/ '\u006E',  // LATIN SMALL LETTER N
            /*0x6F*/ '\u006F',  // LATIN SMALL LETTER O
            /*0x70*/ '\u0070',  // LATIN SMALL LETTER P
            /*0x71*/ '\u0071',  // LATIN SMALL LETTER Q
            /*0x72*/ '\u0072',  // LATIN SMALL LETTER R
            /*0x73*/ '\u0073',  // LATIN SMALL LETTER S
            /*0x74*/ '\u0074',  // LATIN SMALL LETTER T
            /*0x75*/ '\u0075',  // LATIN SMALL LETTER U
            /*0x76*/ '\u0076',  // LATIN SMALL LETTER V
            /*0x77*/ '\u0077',  // LATIN SMALL LETTER W
            /*0x78*/ '\u0078',  // LATIN SMALL LETTER X
            /*0x79*/ '\u0079',  // LATIN SMALL LETTER Y
            /*0x7A*/ '\u007A',  // LATIN SMALL LETTER Z
            /*0x7B*/ '\u007B',  // LEFT CURLY BRACKET
            /*0x7C*/ '\u007C',  // VERTICAL LINE
            /*0x7D*/ '\u007D',  // RIGHT CURLY BRACKET
            /*0x7E*/ '\u007E',  // TILDE
            /*0x7f*/ '\u007f',  // DELETE
            /*0x80*/ '\u00C4',  // LATIN CAPITAL LETTER A WITH DIAERESIS
            /*0x81*/ '\u00C5',  // LATIN CAPITAL LETTER A WITH RING ABOVE
            /*0x82*/ '\u00C7',  // LATIN CAPITAL LETTER C WITH CEDILLA
            /*0x83*/ '\u00C9',  // LATIN CAPITAL LETTER E WITH ACUTE
            /*0x84*/ '\u00D1',  // LATIN CAPITAL LETTER N WITH TILDE
            /*0x85*/ '\u00D6',  // LATIN CAPITAL LETTER O WITH DIAERESIS
            /*0x86*/ '\u00DC',  // LATIN CAPITAL LETTER U WITH DIAERESIS
            /*0x87*/ '\u00E1',  // LATIN SMALL LETTER A WITH ACUTE
            /*0x88*/ '\u00E0',  // LATIN SMALL LETTER A WITH GRAVE
            /*0x89*/ '\u00E2',  // LATIN SMALL LETTER A WITH CIRCUMFLEX
            /*0x8A*/ '\u00E4',  // LATIN SMALL LETTER A WITH DIAERESIS
            /*0x8B*/ '\u00E3',  // LATIN SMALL LETTER A WITH TILDE
            /*0x8C*/ '\u00E5',  // LATIN SMALL LETTER A WITH RING ABOVE
            /*0x8D*/ '\u00E7',  // LATIN SMALL LETTER C WITH CEDILLA
            /*0x8E*/ '\u00E9',  // LATIN SMALL LETTER E WITH ACUTE
            /*0x8F*/ '\u00E8',  // LATIN SMALL LETTER E WITH GRAVE
            /*0x90*/ '\u00EA',  // LATIN SMALL LETTER E WITH CIRCUMFLEX
            /*0x91*/ '\u00EB',  // LATIN SMALL LETTER E WITH DIAERESIS
            /*0x92*/ '\u00ED',  // LATIN SMALL LETTER I WITH ACUTE
            /*0x93*/ '\u00EC',  // LATIN SMALL LETTER I WITH GRAVE
            /*0x94*/ '\u00EE',  // LATIN SMALL LETTER I WITH CIRCUMFLEX
            /*0x95*/ '\u00EF',  // LATIN SMALL LETTER I WITH DIAERESIS
            /*0x96*/ '\u00F1',  // LATIN SMALL LETTER N WITH TILDE
            /*0x97*/ '\u00F3',  // LATIN SMALL LETTER O WITH ACUTE
            /*0x98*/ '\u00F2',  // LATIN SMALL LETTER O WITH GRAVE
            /*0x99*/ '\u00F4',  // LATIN SMALL LETTER O WITH CIRCUMFLEX
            /*0x9A*/ '\u00F6',  // LATIN SMALL LETTER O WITH DIAERESIS
            /*0x9B*/ '\u00F5',  // LATIN SMALL LETTER O WITH TILDE
            /*0x9C*/ '\u00FA',  // LATIN SMALL LETTER U WITH ACUTE
            /*0x9D*/ '\u00F9',  // LATIN SMALL LETTER U WITH GRAVE
            /*0x9E*/ '\u00FB',  // LATIN SMALL LETTER U WITH CIRCUMFLEX
            /*0x9F*/ '\u00FC',  // LATIN SMALL LETTER U WITH DIAERESIS
            /*0xA0*/ '\u2020',  // DAGGER
            /*0xA1*/ '\u00B0',  // DEGREE SIGN
            /*0xA2*/ '\u00A2',  // CENT SIGN
            /*0xA3*/ '\u00A3',  // POUND SIGN
            /*0xA4*/ '\u00A7',  // SECTION SIGN
            /*0xA5*/ '\u2022',  // BULLET
            /*0xA6*/ '\u00B6',  // PILCROW SIGN
            /*0xA7*/ '\u00DF',  // LATIN SMALL LETTER SHARP S
            /*0xA8*/ '\u00AE',  // REGISTERED SIGN
            /*0xA9*/ '\u00A9',  // COPYRIGHT SIGN
            /*0xAA*/ '\u2122',  // TRADE MARK SIGN
            /*0xAB*/ '\u00B4',  // ACUTE ACCENT
            /*0xAC*/ '\u00A8',  // DIAERESIS
            /*0xAD*/ '\u2260',  // NOT EQUAL TO
            /*0xAE*/ '\u00C6',  // LATIN CAPITAL LETTER AE
            /*0xAF*/ '\u00D8',  // LATIN CAPITAL LETTER O WITH STROKE
            /*0xB0*/ '\u221E',  // INFINITY
            /*0xB1*/ '\u00B1',  // PLUS-MINUS SIGN
            /*0xB2*/ '\u2264',  // LESS-THAN OR EQUAL TO
            /*0xB3*/ '\u2265',  // GREATER-THAN OR EQUAL TO
            /*0xB4*/ '\u00A5',  // YEN SIGN
            /*0xB5*/ '\u00B5',  // MICRO SIGN
            /*0xB6*/ '\u2202',  // PARTIAL DIFFERENTIAL
            /*0xB7*/ '\u2211',  // N-ARY SUMMATION
            /*0xB8*/ '\u220F',  // N-ARY PRODUCT
            /*0xB9*/ '\u03C0',  // GREEK SMALL LETTER PI
            /*0xBA*/ '\u222B',  // INTEGRAL
            /*0xBB*/ '\u00AA',  // FEMININE ORDINAL INDICATOR
            /*0xBC*/ '\u00BA',  // MASCULINE ORDINAL INDICATOR
            /*0xBD*/ '\u03A9',  // GREEK CAPITAL LETTER OMEGA
            /*0xBE*/ '\u00E6',  // LATIN SMALL LETTER AE
            /*0xBF*/ '\u00F8',  // LATIN SMALL LETTER O WITH STROKE
            /*0xC0*/ '\u00BF',  // INVERTED QUESTION MARK
            /*0xC1*/ '\u00A1',  // INVERTED EXCLAMATION MARK
            /*0xC2*/ '\u00AC',  // NOT SIGN
            /*0xC3*/ '\u221A',  // SQUARE ROOT
            /*0xC4*/ '\u0192',  // LATIN SMALL LETTER F WITH HOOK
            /*0xC5*/ '\u2248',  // ALMOST EQUAL TO
            /*0xC6*/ '\u2206',  // INCREMENT
            /*0xC7*/ '\u00AB',  // LEFT-POINTING DOUBLE ANGLE QUOTATION MARK
            /*0xC8*/ '\u00BB',  // RIGHT-POINTING DOUBLE ANGLE QUOTATION MARK
            /*0xC9*/ '\u2026',  // HORIZONTAL ELLIPSIS
            /*0xCA*/ '\u00A0',  // NO-BREAK SPACE
            /*0xCB*/ '\u00C0',  // LATIN CAPITAL LETTER A WITH GRAVE
            /*0xCC*/ '\u00C3',  // LATIN CAPITAL LETTER A WITH TILDE
            /*0xCD*/ '\u00D5',  // LATIN CAPITAL LETTER O WITH TILDE
            /*0xCE*/ '\u0152',  // LATIN CAPITAL LIGATURE OE
            /*0xCF*/ '\u0153',  // LATIN SMALL LIGATURE OE
            /*0xD0*/ '\u2013',  // EN DASH
            /*0xD1*/ '\u2014',  // EM DASH
            /*0xD2*/ '\u201C',  // LEFT DOUBLE QUOTATION MARK
            /*0xD3*/ '\u201D',  // RIGHT DOUBLE QUOTATION MARK
            /*0xD4*/ '\u2018',  // LEFT SINGLE QUOTATION MARK
            /*0xD5*/ '\u2019',  // RIGHT SINGLE QUOTATION MARK
            /*0xD6*/ '\u00F7',  // DIVISION SIGN
            /*0xD7*/ '\u25CA',  // LOZENGE
            /*0xD8*/ '\u00FF',  // LATIN SMALL LETTER Y WITH DIAERESIS
            /*0xD9*/ '\u0178',  // LATIN CAPITAL LETTER Y WITH DIAERESIS
            /*0xDA*/ '\u2044',  // FRACTION SLASH
            /*0xDB*/ '\u00A4',  // CURRENCY SIGN (became EURO SIGN later)
            /*0xDC*/ '\u2039',  // SINGLE LEFT-POINTING ANGLE QUOTATION MARK
            /*0xDD*/ '\u203A',  // SINGLE RIGHT-POINTING ANGLE QUOTATION MARK
            /*0xDE*/ '\uFB01',  // LATIN SMALL LIGATURE FI
            /*0xDF*/ '\uFB02',  // LATIN SMALL LIGATURE FL
            /*0xE0*/ '\u2021',  // DOUBLE DAGGER
            /*0xE1*/ '\u00B7',  // MIDDLE DOT
            /*0xE2*/ '\u201A',  // SINGLE LOW-9 QUOTATION MARK
            /*0xE3*/ '\u201E',  // DOUBLE LOW-9 QUOTATION MARK
            /*0xE4*/ '\u2030',  // PER MILLE SIGN
            /*0xE5*/ '\u00C2',  // LATIN CAPITAL LETTER A WITH CIRCUMFLEX
            /*0xE6*/ '\u00CA',  // LATIN CAPITAL LETTER E WITH CIRCUMFLEX
            /*0xE7*/ '\u00C1',  // LATIN CAPITAL LETTER A WITH ACUTE
            /*0xE8*/ '\u00CB',  // LATIN CAPITAL LETTER E WITH DIAERESIS
            /*0xE9*/ '\u00C8',  // LATIN CAPITAL LETTER E WITH GRAVE
            /*0xEA*/ '\u00CD',  // LATIN CAPITAL LETTER I WITH ACUTE
            /*0xEB*/ '\u00CE',  // LATIN CAPITAL LETTER I WITH CIRCUMFLEX
            /*0xEC*/ '\u00CF',  // LATIN CAPITAL LETTER I WITH DIAERESIS
            /*0xED*/ '\u00CC',  // LATIN CAPITAL LETTER I WITH GRAVE
            /*0xEE*/ '\u00D3',  // LATIN CAPITAL LETTER O WITH ACUTE
            /*0xEF*/ '\u00D4',  // LATIN CAPITAL LETTER O WITH CIRCUMFLEX
            /*0xF0*/ '\uF8FF',  // Apple logo (solid)
            /*0xF1*/ '\u00D2',  // LATIN CAPITAL LETTER O WITH GRAVE
            /*0xF2*/ '\u00DA',  // LATIN CAPITAL LETTER U WITH ACUTE
            /*0xF3*/ '\u00DB',  // LATIN CAPITAL LETTER U WITH CIRCUMFLEX
            /*0xF4*/ '\u00D9',  // LATIN CAPITAL LETTER U WITH GRAVE
            /*0xF5*/ '\u0131',  // LATIN SMALL LETTER DOTLESS I
            /*0xF6*/ '\u02C6',  // MODIFIER LETTER CIRCUMFLEX ACCENT
            /*0xF7*/ '\u02DC',  // SMALL TILDE
            /*0xF8*/ '\u00AF',  // MACRON
            /*0xF9*/ '\u02D8',  // BREVE
            /*0xFA*/ '\u02D9',  // DOT ABOVE
            /*0xFB*/ '\u02DA',  // RING ABOVE
            /*0xFC*/ '\u00B8',  // CEDILLA
            /*0xFD*/ '\u02DD',  // DOUBLE ACUTE ACCENT
            /*0xFE*/ '\u02DB',  // OGONEK
            /*0xFF*/ '\u02C7'   // CARON
        };


        /// <summary>
        /// Maps a BMP (Basic Multilingual Plane) code point to a Mac OS Roman byte value (0-255).
        /// Invalid values are mapped to -1.
        /// </summary>
        private static readonly short[] sUnicodeToRoman = GenerateReverseMap(sRomanToUnicode, true);

        /// <summary>
        /// Table with control characters replaced.
        /// </summary>
        private static readonly char[] sRomanShowCtrlToUnicode = GenerateShowCtrl(sRomanToUnicode);

        /// <summary>
        /// Reverse table, with control characters replaced.
        /// </summary>
        private static readonly short[] sUnicodeToRomanShowCtrl =
            GenerateReverseMap(sRomanShowCtrlToUnicode, true);

        /// <summary>
        /// Case-insensitive sort order for Mac OS Roman character set.  Upper/lower characters are
        /// paired, with the second character flagged.  All 256 possible byte values are included.
        /// </summary>
        /// <remarks>
        /// This doesn't entirely make sense, e.g. vowels with diacritical marks are largely
        /// grouped together, except for the upper-case versions that appear near the end.
        /// However, the same order appears in multiple places, including Apple's HFS FST for
        /// GS/OS, so this appears to be the correct order.
        /// </remarks>
        private static ushort[] sRomanSortOrder = new ushort[256] {
            0x00,        // ␀
            0x01,        // ␁
            0x02,        // ␂
            0x03,        // ␃
            0x04,        // ␄
            0x05,        // ␅
            0x06,        // ␆
            0x07,        // ␇
            0x08,        // ␈
            0x09,        // ␉
            0x0a,        // ␊
            0x0b,        // ␋
            0x0c,        // ␌
            0x0d,        // ␍
            0x0e,        // ␎
            0x0f,        // ␏
            0x10,        // ␐
            0x11,        // ␑
            0x12,        // ␒
            0x13,        // ␓
            0x14,        // ␔
            0x15,        // ␕
            0x16,        // ␖
            0x17,        // ␗
            0x18,        // ␘
            0x19,        // ␙
            0x1a,        // ␚
            0x1b,        // ␛
            0x1c,        // ␜
            0x1d,        // ␝
            0x1e,        // ␞
            0x1f,        // ␟
            0x20,0x80ca, //  / 
            0x21,        // !
            0x22,        // "
            0xd2,        // “
            0xd3,        // ”
            0xc7,        // «
            0xc8,        // »
            0x23,        // #
            0x24,        // $
            0x25,        // %
            0x26,        // &
            0x27,        // '
            0xd4,        // ‘
            0xd5,        // ’
            0x28,        // (
            0x29,        // )
            0x2a,        // *
            0x2b,        // +
            0x2c,        // ,
            0x2d,        // -
            0x2e,        // .
            0x2f,        // /
            0x30,        // 0
            0x31,        // 1
            0x32,        // 2
            0x33,        // 3
            0x34,        // 4
            0x35,        // 5
            0x36,        // 6
            0x37,        // 7
            0x38,        // 8
            0x39,        // 9
            0x3a,        // :
            0x3b,        // ;
            0x3c,        // <
            0x3d,        // =
            0x3e,        // >
            0x3f,        // ?
            0x40,        // @
            0x41,0x8061, // A/a
            0x88,0x80cb, // à/À
            0x80,0x808a, // Ä/ä
            0x8b,0x80cc, // ã/Ã
            0x81,0x808c, // Å/å
            0xae,0x80be, // Æ/æ
            0x60,        // `
            0x87,        // á
            0x89,        // â
            0xbb,        // ª
            0x42,0x8062, // B/b
            0x43,0x8063, // C/c
            0x82,0x808d, // Ç/ç
            0x44,0x8064, // D/d
            0x45,0x8065, // E/e
            0x83,0x808e, // É/é
            0x8f,        // è
            0x90,        // ê
            0x91,        // ë
            0x46,0x8066, // F/f
            0x47,0x8067, // G/g
            0x48,0x8068, // H/h
            0x49,0x8069, // I/i
            0x92,        // í
            0x93,        // ì
            0x94,        // î
            0x95,        // ï
            0x4a,0x806a, // J/j
            0x4b,0x806b, // K/k
            0x4c,0x806c, // L/l
            0x4d,0x806d, // M/m
            0x4e,0x806e, // N/n
            0x84,0x8096, // Ñ/ñ
            0x4f,0x806f, // O/o
            0x85,0x809a, // Ö/ö
            0x9b,0x80cd, // õ/Õ
            0xaf,0x80bf, // Ø/ø
            0xce,0x80cf, // Œ/œ
            0x97,        // ó
            0x98,        // ò
            0x99,        // ô
            0xbc,        // º
            0x50,0x8070, // P/p
            0x51,0x8071, // Q/q
            0x52,0x8072, // R/r
            0x53,0x8073, // S/s
            0xa7,        // ß
            0x54,0x8074, // T/t
            0x55,0x8075, // U/u
            0x86,0x809f, // Ü/ü
            0x9c,        // ú
            0x9d,        // ù
            0x9e,        // û
            0x56,0x8076, // V/v
            0x57,0x8077, // W/w
            0x58,0x8078, // X/x
            0x59,0x8079, // Y/y
            0xd8,        // ÿ
            0x5a,0x807a, // Z/z
            0x5b,        // [
            0x5c,        // \
            0x5d,        // ]
            0x5e,        // ^
            0x5f,        // _
            0x7b,        // {
            0x7c,        // |
            0x7d,        // }
            0x7e,        // ~
            0x7f,        // ␡
            0xa0,        // †
            0xa1,        // °
            0xa2,        // ¢
            0xa3,        // £
            0xa4,        // §
            0xa5,        // •
            0xa6,        // ¶
            0xa8,        // ®
            0xa9,        // ©
            0xaa,        // ™
            0xab,        // ´
            0xac,        // ¨
            0xad,        // ≠
            0xb0,        // ∞
            0xb1,        // ±
            0xb2,        // ≤
            0xb3,        // ≥
            0xb4,        // ¥
            0xb5,        // µ
            0xb6,        // ∂
            0xb7,        // ∑
            0xb8,        // ∏
            0xb9,        // π
            0xba,        // ∫
            0xbd,        // Ω
            0xc0,        // ¿
            0xc1,        // ¡
            0xc2,        // ¬
            0xc3,        // √
            0xc4,        // ƒ
            0xc5,        // ≈
            0xc6,        // ∆
            0xc9,        // …
            0xd0,        // –
            0xd1,        // —
            0xd6,        // ÷
            0xd7,        // ◊
            0xd9,        // Ÿ
            0xda,        // ⁄
            0xdb,        // ¤
            0xdc,        // ‹
            0xdd,        // ›
            0xde,        // ﬁ
            0xdf,        // ﬂ
            0xe0,        // ‡
            0xe1,        // ·
            0xe2,        // ‚
            0xe3,        // „
            0xe4,        // ‰
            0xe5,        // Â
            0xe6,        // Ê
            0xe7,        // Á
            0xe8,        // Ë
            0xe9,        // È
            0xea,        // Í
            0xeb,        // Î
            0xec,        // Ï
            0xed,        // Ì
            0xee,        // Ó
            0xef,        // Ô
            0xf0,        // 
            0xf1,        // Ò
            0xf2,        // Ú
            0xf3,        // Û
            0xf4,        // Ù
            0xf5,        // ı
            0xf6,        // ˆ
            0xf7,        // ˜
            0xf8,        // ¯
            0xf9,        // ˘
            0xfa,        // ˙
            0xfb,        // ˚
            0xfc,        // ¸
            0xfd,        // ˝
            0xfe,        // ˛
            0xff,        // ˇ
        };

        /// <summary>
        /// Case-insensitive sort table, e.g. for comparing HFS filenames.
        /// For each byte, test (table[str1[i]] - table[str2[i]]).
        /// </summary>
        private static byte[] sRomanCompare = GenerateSortTable(sRomanSortOrder);

        #endregion Mac OS Roman

        /// <summary>
        /// Generates a character table that is a copy of the original, but with 0x00-1f and 0x7f
        /// replaced with characters from the "control pictures" block.  This works better when
        /// displaying filenames with control characters in them.
        /// </summary>
        private static char[] GenerateShowCtrl(char[] srcSet) {
            char[] newSet = new char[srcSet.Length];
            for (int i = 0; i < srcSet.Length; i++) {
                if (i < 0x20) {
                    newSet[i] = (char)(i + 0x2400);
                } else if (i == 0x7f) {
                    newSet[i] = '\u2421';
                } else {
                    newSet[i] = srcSet[i];
                }
            }
            return newSet;
        }

        /// <summary>
        /// Generates a reverse character mapping for BMP code points.  Throws an exception if
        /// the reverse mapping is ambiguous because two values map to the same thing.
        /// </summary>
        private static short[] GenerateReverseMap(char[] map, bool roman1998) {
            short[] rev = new short[65536];
            for (int i = 0; i < rev.Length; i++) {
                rev[i] = -1;
            }
            for (int i = 0; i < 256; i++) {
                char codePoint = map[i];
                if (rev[codePoint] != -1) {
                    throw new Exception("Doubled entry " + i);
                }
                rev[codePoint] = (short)i;
            }

            // Handle the 1998 change to 0xdb (from \u00a4 CURRENCY SIGN to \u20ac EURO SIGN)
            // by adding an entry for the newer mapping.
            // This could be handled more generally by passing in an "additional conversions"
            // tuple list, but for now we just pass a flag.
            if (roman1998) {
                rev[0x20ac] = 0xdb;
            }
            return rev;
        }

        /// <summary>
        /// Generates a translation table that can be used to perform string comparisons.
        /// </summary>
        /// <param name="orderTable">List of Mac OS 8-bit characters, in order, with lower-case
        ///   equivalents flagged.</param>
        /// <returns>256-byte lookup table.</returns>
        private static byte[] GenerateSortTable(ushort[] orderTable) {
#if DEBUG
            Debug.Assert(orderTable.Length == 256);
            bool[] verify = new bool[256];
#endif
            byte[] sortTable = new byte[256];
            int outIndex = -1;
            for (int i = 0; i < orderTable.Length; i++) {
                ushort val = orderTable[i];
                if (val < 0x100) {
                    // Not a lower-case version of the previous char.  Advance the output index.
                    outIndex++;
                }
                sortTable[val & 0xff] = (byte)outIndex;
#if DEBUG
                // Make sure each entry is set exactly once.
                Debug.Assert(!verify[val & 0xff]);
                verify[val & 0xff] = true;
#endif
            }
            return sortTable;
        }



#if false
        public static string GenOrderTable() {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            int outCount = 0;
            bool lineStart = true;
            for (int i = 0; i < 256; i++) {
                bool firstInSet = true;
                bool found = false;
                for (int j = 0; j < 256; j++) {
                    if (sGSOSOrder[j] == i) {
                        found = true;
                        if (!firstInSet) {
                            sb.Append('/');
                        } else if (!lineStart) {
                            sb.Append(", ");
                        }
                        sb.Append(sRomanToUnicode[j]);

                        if (firstInSet) {
                            outCount++;
                        }
                        firstInSet = false;
                        lineStart = false;
                    }
                }
                if (found && outCount % 16 == 0) {
                    sb.AppendLine();
                    lineStart = true;
                }
            }
            return sb.ToString();
        }

        public static string GenOrderTable2() {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("private static readonly int[] sEquivChars = new int[] {");

            System.Text.StringBuilder vals = new System.Text.StringBuilder();
            System.Text.StringBuilder cmt = new System.Text.StringBuilder();

            for (int i = 0; i < 256; i++) {
                vals.Clear();
                cmt.Clear();

                cmt.Append("// ");

                bool found = false;
                for (int j = 0; j < 256; j++) {
                    if (sGSOSOrder[j] == i) {
                        if (found) {
                            // second item
                            vals.Append("0x80" + j.ToString("x2"));
                        } else {
                            vals.Append("0x" + j.ToString("x2"));
                        }
                        vals.Append(',');
                        if (found) {
                            cmt.Append('/');
                        }
                        cmt.Append(sRomanToUnicode[j]);

                        found = true;
                    }
                }
                if (found) {
                    sb.AppendFormat("{0,-12} {1}", vals, cmt);
                    sb.AppendLine();
                }
            }

            sb.AppendLine("};");
            return sb.ToString();

        }

        //
        // Three tables from three different sources.  The tables are all different, but the
        // relationships between characters work out exactly the same.
        //

        // table from GS/OS HFS FST
        private static readonly int[] sGSOSOrder = new int[256] {
            0x00,0x01,0x02,0x03,0x04,0x05,0x06,0x07,0x08,0x09,0x0a,0x0b,0x0c,0x0d,0x0e,0x0f,
            0x10,0x11,0x12,0x13,0x14,0x15,0x16,0x17,0x18,0x19,0x1a,0x1b,0x1c,0x1d,0x1e,0x1f,
            0x20,0x22,0x23,0x28,0x29,0x2a,0x2b,0x2c,0x2f,0x30,0x31,0x32,0x33,0x34,0x35,0x36,
            0x37,0x38,0x39,0x3a,0x3b,0x3c,0x3d,0x3e,0x3f,0x40,0x41,0x42,0x43,0x44,0x45,0x46,
            0x47,0x49,0x58,0x5b,0x5e,0x61,0x68,0x69,0x6b,0x6d,0x74,0x76,0x78,0x7a,0x7b,0x7f,
            0x8e,0x8f,0x92,0x94,0x96,0x99,0x9f,0xa1,0xa4,0xa6,0xa9,0xaa,0xab,0xac,0xad,0xae,
            0x54,0x49,0x58,0x5b,0x5e,0x61,0x68,0x69,0x6b,0x6d,0x74,0x76,0x78,0x7a,0x7b,0x7f,
            0x8e,0x8f,0x92,0x94,0x96,0x99,0x9f,0xa1,0xa4,0xa6,0xa9,0xaf,0xb0,0xb1,0xb2,0xb3,
            0x4c,0x50,0x5c,0x62,0x7d,0x81,0x9a,0x55,0x4a,0x56,0x4c,0x4e,0x50,0x5c,0x62,0x64,
            0x65,0x66,0x6f,0x70,0x71,0x72,0x7d,0x89,0x8a,0x8b,0x81,0x83,0x9c,0x9d,0x9e,0x9a,
            0xb4,0xb5,0xb6,0xb7,0xb8,0xb9,0xba,0x95,0xbb,0xbc,0xbd,0xbe,0xbf,0xc0,0x52,0x85,
            0xc1,0xc2,0xc3,0xc4,0xc5,0xc6,0xc7,0xc8,0xc9,0xca,0xcb,0x57,0x8c,0xcc,0x52,0x85,
            0xcd,0xce,0xcf,0xd0,0xd1,0xd2,0xd3,0x26,0x27,0xd4,0x20,0x4a,0x4e,0x83,0x87,0x87,
            0xd5,0xd6,0x24,0x25,0x2d,0x2e,0xd7,0xd8,0xa7,0xd9,0xda,0xdb,0xdc,0xdd,0xde,0xdf,
            0xe0,0xe1,0xe2,0xe3,0xe4,0xe5,0xe6,0xe7,0xe8,0xe9,0xea,0xeb,0xec,0xed,0xee,0xef,
            0xf0,0xf1,0xf2,0xf3,0xf4,0xf5,0xf6,0xf7,0xf8,0xf9,0xfa,0xfb,0xfc,0xfd,0xfe,0xff
        };

        // table from libhfs; differs at 0x41
        private static readonly int[] sLibHfsOrder = new int[256] {
            0x00,0x01,0x02,0x03,0x04,0x05,0x06,0x07,0x08,0x09,0x0a,0x0b,0x0c,0x0d,0x0e,0x0f,
            0x10,0x11,0x12,0x13,0x14,0x15,0x16,0x17,0x18,0x19,0x1a,0x1b,0x1c,0x1d,0x1e,0x1f,
            0x20,0x22,0x23,0x28,0x29,0x2a,0x2b,0x2c,0x2f,0x30,0x31,0x32,0x33,0x34,0x35,0x36,
            0x37,0x38,0x39,0x3a,0x3b,0x3c,0x3d,0x3e,0x3f,0x40,0x41,0x42,0x43,0x44,0x45,0x46,
            0x47,0x48,0x58,0x5a,0x5e,0x60,0x67,0x69,0x6b,0x6d,0x73,0x75,0x77,0x79,0x7b,0x7f,
            0x8d,0x8f,0x91,0x93,0x96,0x98,0x9f,0xa1,0xa3,0xa5,0xa8,0xaa,0xab,0xac,0xad,0xae,
            0x54,0x48,0x58,0x5a,0x5e,0x60,0x67,0x69,0x6b,0x6d,0x73,0x75,0x77,0x79,0x7b,0x7f,
            0x8d,0x8f,0x91,0x93,0x96,0x98,0x9f,0xa1,0xa3,0xa5,0xa8,0xaf,0xb0,0xb1,0xb2,0xb3,
            0x4c,0x50,0x5c,0x62,0x7d,0x81,0x9a,0x55,0x4a,0x56,0x4c,0x4e,0x50,0x5c,0x62,0x64,
            0x65,0x66,0x6f,0x70,0x71,0x72,0x7d,0x89,0x8a,0x8b,0x81,0x83,0x9c,0x9d,0x9e,0x9a,
            0xb4,0xb5,0xb6,0xb7,0xb8,0xb9,0xba,0x95,0xbb,0xbc,0xbd,0xbe,0xbf,0xc0,0x52,0x85,
            0xc1,0xc2,0xc3,0xc4,0xc5,0xc6,0xc7,0xc8,0xc9,0xca,0xcb,0x57,0x8c,0xcc,0x52,0x85,
            0xcd,0xce,0xcf,0xd0,0xd1,0xd2,0xd3,0x26,0x27,0xd4,0x20,0x4a,0x4e,0x83,0x87,0x87,
            0xd5,0xd6,0x24,0x25,0x2d,0x2e,0xd7,0xd8,0xa7,0xd9,0xda,0xdb,0xdc,0xdd,0xde,0xdf,
            0xe0,0xe1,0xe2,0xe3,0xe4,0xe5,0xe6,0xe7,0xe8,0xe9,0xea,0xeb,0xec,0xed,0xee,0xef,
            0xf0,0xf1,0xf2,0xf3,0xf4,0xf5,0xf6,0xf7,0xf8,0xf9,0xfa,0xfb,0xfc,0xfd,0xfe,0xff
        };

        // table from Linux; different from previous, but same end result
        private static readonly int[] sLinuxOrder = new int[256] {
            0x00,0x01,0x02,0x03,0x04,0x05,0x06,0x07,0x08,0x09,0x0A,0x0B,0x0C,0x0D,0x0E,0x0F,
            0x10,0x11,0x12,0x13,0x14,0x15,0x16,0x17,0x18,0x19,0x1A,0x1B,0x1C,0x1D,0x1E,0x1F,
            0x20,0x22,0x23,0x28,0x29,0x2A,0x2B,0x2C,0x2F,0x30,0x31,0x32,0x33,0x34,0x35,0x36,
            0x37,0x38,0x39,0x3A,0x3B,0x3C,0x3D,0x3E,0x3F,0x40,0x41,0x42,0x43,0x44,0x45,0x46,
            0x47,0x48,0x57,0x59,0x5D,0x5F,0x66,0x68,0x6A,0x6C,0x72,0x74,0x76,0x78,0x7A,0x7E,
            0x8C,0x8E,0x90,0x92,0x95,0x97,0x9E,0xA0,0xA2,0xA4,0xA7,0xA9,0xAA,0xAB,0xAC,0xAD,
            0x4E,0x48,0x57,0x59,0x5D,0x5F,0x66,0x68,0x6A,0x6C,0x72,0x74,0x76,0x78,0x7A,0x7E,
            0x8C,0x8E,0x90,0x92,0x95,0x97,0x9E,0xA0,0xA2,0xA4,0xA7,0xAF,0xB0,0xB1,0xB2,0xB3,
            0x4A,0x4C,0x5A,0x60,0x7B,0x7F,0x98,0x4F,0x49,0x51,0x4A,0x4B,0x4C,0x5A,0x60,0x63,
            0x64,0x65,0x6E,0x6F,0x70,0x71,0x7B,0x84,0x85,0x86,0x7F,0x80,0x9A,0x9B,0x9C,0x98,
            0xB4,0xB5,0xB6,0xB7,0xB8,0xB9,0xBA,0x94,0xBB,0xBC,0xBD,0xBE,0xBF,0xC0,0x4D,0x81,
            0xC1,0xC2,0xC3,0xC4,0xC5,0xC6,0xC7,0xC8,0xC9,0xCA,0xCB,0x55,0x8A,0xCC,0x4D,0x81,
            0xCD,0xCE,0xCF,0xD0,0xD1,0xD2,0xD3,0x26,0x27,0xD4,0x20,0x49,0x4B,0x80,0x82,0x82,
            0xD5,0xD6,0x24,0x25,0x2D,0x2E,0xD7,0xD8,0xA6,0xD9,0xDA,0xDB,0xDC,0xDD,0xDE,0xDF,
            0xE0,0xE1,0xE2,0xE3,0xE4,0xE5,0xE6,0xE7,0xE8,0xE9,0xEA,0xEB,0xEC,0xED,0xEE,0xEF,
            0xF0,0xF1,0xF2,0xF3,0xF4,0xF5,0xF6,0xF7,0xF8,0xF9,0xFA,0xFB,0xFC,0xFD,0xFE,0xFF
        };

        public static string CompareOrderings() {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < 256; i++) {
                sb.Append(i.ToString("x2"));
                sb.Append(":");
                for (int j = 0; j < 256; j++) {
                    if (sLibHfsOrder[j] == i) {
                        sb.Append(' ');
                        sb.Append(j.ToString("x2"));
                    }
                }
                sb.Append(" |");
                for (int j = 0; j < 256; j++) {
                    if (sLinuxOrder[j] == i) {
                        sb.Append(' ');
                        sb.Append(j.ToString("x2"));
                    }
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }
#endif
    }
}
