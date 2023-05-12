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
    /// Code Page 437 character set conversion utilities.
    /// </summary>
    /// <remarks>
    /// <para><see href="https://en.wikipedia.org/wiki/Code_page_437">CP437</see> is the classic
    /// IBM PC character set, featuring ASCII with some Latin and Greek characters, as well
    /// as the "line art" glyphs used for user interfaces.</para>
    /// <para>Unfortunately, values in the control character range have an alternate meaning.
    /// In some situations they can be "screen codes", displayed as arrows and playing card
    /// symbols.  This makes it
    /// <see href="https://stackoverflow.com/q/57976504/294248">difficult</see> to define
    /// a bidirectional conversion without context.</para>
    /// <para>For our purposes -- filenames and comments -- we want to interpret them as control
    /// characters.</para>
    /// </remarks>
    public static class CP437 {
        /// <summary>
        /// Converts a buffer of CP437-encoded bytes into a Unicode string.
        /// </summary>
        /// <param name="encStr">CP437-encoded data buffer.</param>
        /// <param name="offset">Start offset.</param>
        /// <param name="length">String length.</param>
        /// <returns>Converted string.</returns>
        public static string ToUnicode(byte[] encStr, int offset, int length) {
            if (length == 0) {
                return string.Empty;
            }

            char[] uniStr = new char[length];
            for (int i = 0; i < length; i++) {
                uniStr[i] = sCP437ToUnicode[encStr[offset + i]];
            }
            return new string(uniStr);
        }

        /// <summary>
        /// Converts a Unicode string to a CP437-encoded byte buffer.  Unsupported
        /// characters are converted to '?'.
        /// </summary>
        /// <param name="str">String to encode.</param>
        /// <returns>CP437-encoded string.</returns>
        public static byte[] FromUnicode(string str) {
            byte[] outBuf = new byte[str.Length];
            for (int i = 0; i < str.Length; i++) {
                short encVal = sUnicodeToCP437[str[i]];
                if (encVal < 0) {
                    Debug.WriteLine("String has invalid value 0x" +
                        ((int)str[i]).ToString("x4") + "('" + str[i] + "')");
                    encVal = (short)'?';
                }
                outBuf[i] = (byte)encVal;
            }
            return outBuf;
        }

        /// <summary>
        /// Determines whether a string is comprised entirely of characters in CP437.
        /// </summary>
        /// <param name="str">String to validate.</param>
        /// <returns>True if the string is valid.</returns>
        public static bool IsStringValid(string str) {
            for (int i = 0; i < str.Length; i++) {
                char ch = str[i];
                if (ch >= sCP437ToUnicode.Length || sCP437ToUnicode[ch] < 0) {
                    return false;
                }
            }
            return true;
        }

        #region CP437

        /// <summary>
        /// <para>Maps Code Page 437 byte values to Unicode.  Mapping comes from
        /// https://www.unicode.org/Public/MAPPINGS/VENDORS/MICSFT/PC/CP437.TXT</para>
        ///
        /// <para>The <see href="https://en.wikipedia.org/wiki/Code_page_437">wikipedia</see>
        /// page shows the "screen code" character values for 0x00-0x1f and 0x7f.</para>
        ///
        /// <para>(I think this is currently identical to what you'd get by using .NET features,
        /// e.g. "Encoding.GetEncoding(437)", since we're not currently mapping control characters
        /// to the control picture glyphs.)</para>
        /// </summary>
        private static readonly char[] sCP437ToUnicode = new char[256] {
            /*0x00*/ '\u0000',  // [control] NULL
            /*0x01*/ '\u0001',  // [control] START OF HEADING
            /*0x02*/ '\u0002',  // [control] START OF TEXT
            /*0x03*/ '\u0003',  // [control] END OF TEXT
            /*0x04*/ '\u0004',  // [control] END OF TRANSMISSION
            /*0x05*/ '\u0005',  // [control] ENQUIRY
            /*0x06*/ '\u0006',  // [control] ACKNOWLEDGE
            /*0x07*/ '\u0007',  // [control] BELL
            /*0x08*/ '\u0008',  // [control] BACKSPACE
            /*0x09*/ '\u0009',  // [control] HORIZONTAL TABULATION
            /*0x0a*/ '\u000a',  // [control] LINE FEED
            /*0x0b*/ '\u000b',  // [control] VERTICAL TABULATION
            /*0x0c*/ '\u000c',  // [control] FORM FEED
            /*0x0d*/ '\u000d',  // [control] CARRIAGE RETURN
            /*0x0e*/ '\u000e',  // [control] SHIFT OUT
            /*0x0f*/ '\u000f',  // [control] SHIFT IN
            /*0x10*/ '\u0010',  // [control] DATA LINK ESCAPE
            /*0x11*/ '\u0011',  // [control] DEVICE CONTROL ONE
            /*0x12*/ '\u0012',  // [control] DEVICE CONTROL TWO
            /*0x13*/ '\u0013',  // [control] DEVICE CONTROL THREE
            /*0x14*/ '\u0014',  // [control] DEVICE CONTROL FOUR
            /*0x15*/ '\u0015',  // [control] NEGATIVE ACKNOWLEDGE
            /*0x16*/ '\u0016',  // [control] SYNCHRONOUS IDLE
            /*0x17*/ '\u0017',  // [control] END OF TRANSMISSION BLOCK
            /*0x18*/ '\u0018',  // [control] CANCEL
            /*0x19*/ '\u0019',  // [control] END OF MEDIUM
            /*0x1a*/ '\u001a',  // [control] SUBSTITUTE
            /*0x1b*/ '\u001b',  // [control] ESCAPE
            /*0x1c*/ '\u001c',  // [control] FILE SEPARATOR
            /*0x1d*/ '\u001d',  // [control] GROUP SEPARATOR
            /*0x1e*/ '\u001e',  // [control] RECORD SEPARATOR
            /*0x1f*/ '\u001f',  // [control] UNIT SEPARATOR
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
            /*0x2a*/ '\u002a',  // ASTERISK
            /*0x2b*/ '\u002b',  // PLUS SIGN
            /*0x2c*/ '\u002c',  // COMMA
            /*0x2d*/ '\u002d',  // HYPHEN-MINUS
            /*0x2e*/ '\u002e',  // FULL STOP
            /*0x2f*/ '\u002f',  // SOLIDUS
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
            /*0x3a*/ '\u003a',  // COLON
            /*0x3b*/ '\u003b',  // SEMICOLON
            /*0x3c*/ '\u003c',  // LESS-THAN SIGN
            /*0x3d*/ '\u003d',  // EQUALS SIGN
            /*0x3e*/ '\u003e',  // GREATER-THAN SIGN
            /*0x3f*/ '\u003f',  // QUESTION MARK
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
            /*0x4a*/ '\u004a',  // LATIN CAPITAL LETTER J
            /*0x4b*/ '\u004b',  // LATIN CAPITAL LETTER K
            /*0x4c*/ '\u004c',  // LATIN CAPITAL LETTER L
            /*0x4d*/ '\u004d',  // LATIN CAPITAL LETTER M
            /*0x4e*/ '\u004e',  // LATIN CAPITAL LETTER N
            /*0x4f*/ '\u004f',  // LATIN CAPITAL LETTER O
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
            /*0x5a*/ '\u005a',  // LATIN CAPITAL LETTER Z
            /*0x5b*/ '\u005b',  // LEFT SQUARE BRACKET
            /*0x5c*/ '\u005c',  // REVERSE SOLIDUS
            /*0x5d*/ '\u005d',  // RIGHT SQUARE BRACKET
            /*0x5e*/ '\u005e',  // CIRCUMFLEX ACCENT
            /*0x5f*/ '\u005f',  // LOW LINE
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
            /*0x6a*/ '\u006a',  // LATIN SMALL LETTER J
            /*0x6b*/ '\u006b',  // LATIN SMALL LETTER K
            /*0x6c*/ '\u006c',  // LATIN SMALL LETTER L
            /*0x6d*/ '\u006d',  // LATIN SMALL LETTER M
            /*0x6e*/ '\u006e',  // LATIN SMALL LETTER N
            /*0x6f*/ '\u006f',  // LATIN SMALL LETTER O
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
            /*0x7a*/ '\u007a',  // LATIN SMALL LETTER Z
            /*0x7b*/ '\u007b',  // LEFT CURLY BRACKET
            /*0x7c*/ '\u007c',  // VERTICAL LINE
            /*0x7d*/ '\u007d',  // RIGHT CURLY BRACKET
            /*0x7e*/ '\u007e',  // TILDE
            /*0x7f*/ '\u007f',  // [control] DELETE
            /*0x80*/ '\u00c7',  // LATIN CAPITAL LETTER C WITH CEDILLA
            /*0x81*/ '\u00fc',  // LATIN SMALL LETTER U WITH DIAERESIS
            /*0x82*/ '\u00e9',  // LATIN SMALL LETTER E WITH ACUTE
            /*0x83*/ '\u00e2',  // LATIN SMALL LETTER A WITH CIRCUMFLEX
            /*0x84*/ '\u00e4',  // LATIN SMALL LETTER A WITH DIAERESIS
            /*0x85*/ '\u00e0',  // LATIN SMALL LETTER A WITH GRAVE
            /*0x86*/ '\u00e5',  // LATIN SMALL LETTER A WITH RING ABOVE
            /*0x87*/ '\u00e7',  // LATIN SMALL LETTER C WITH CEDILLA
            /*0x88*/ '\u00ea',  // LATIN SMALL LETTER E WITH CIRCUMFLEX
            /*0x89*/ '\u00eb',  // LATIN SMALL LETTER E WITH DIAERESIS
            /*0x8a*/ '\u00e8',  // LATIN SMALL LETTER E WITH GRAVE
            /*0x8b*/ '\u00ef',  // LATIN SMALL LETTER I WITH DIAERESIS
            /*0x8c*/ '\u00ee',  // LATIN SMALL LETTER I WITH CIRCUMFLEX
            /*0x8d*/ '\u00ec',  // LATIN SMALL LETTER I WITH GRAVE
            /*0x8e*/ '\u00c4',  // LATIN CAPITAL LETTER A WITH DIAERESIS
            /*0x8f*/ '\u00c5',  // LATIN CAPITAL LETTER A WITH RING ABOVE
            /*0x90*/ '\u00c9',  // LATIN CAPITAL LETTER E WITH ACUTE
            /*0x91*/ '\u00e6',  // LATIN SMALL LIGATURE AE
            /*0x92*/ '\u00c6',  // LATIN CAPITAL LIGATURE AE
            /*0x93*/ '\u00f4',  // LATIN SMALL LETTER O WITH CIRCUMFLEX
            /*0x94*/ '\u00f6',  // LATIN SMALL LETTER O WITH DIAERESIS
            /*0x95*/ '\u00f2',  // LATIN SMALL LETTER O WITH GRAVE
            /*0x96*/ '\u00fb',  // LATIN SMALL LETTER U WITH CIRCUMFLEX
            /*0x97*/ '\u00f9',  // LATIN SMALL LETTER U WITH GRAVE
            /*0x98*/ '\u00ff',  // LATIN SMALL LETTER Y WITH DIAERESIS
            /*0x99*/ '\u00d6',  // LATIN CAPITAL LETTER O WITH DIAERESIS
            /*0x9a*/ '\u00dc',  // LATIN CAPITAL LETTER U WITH DIAERESIS
            /*0x9b*/ '\u00a2',  // CENT SIGN
            /*0x9c*/ '\u00a3',  // POUND SIGN
            /*0x9d*/ '\u00a5',  // YEN SIGN
            /*0x9e*/ '\u20a7',  // PESETA SIGN
            /*0x9f*/ '\u0192',  // LATIN SMALL LETTER F WITH HOOK
            /*0xa0*/ '\u00e1',  // LATIN SMALL LETTER A WITH ACUTE
            /*0xa1*/ '\u00ed',  // LATIN SMALL LETTER I WITH ACUTE
            /*0xa2*/ '\u00f3',  // LATIN SMALL LETTER O WITH ACUTE
            /*0xa3*/ '\u00fa',  // LATIN SMALL LETTER U WITH ACUTE
            /*0xa4*/ '\u00f1',  // LATIN SMALL LETTER N WITH TILDE
            /*0xa5*/ '\u00d1',  // LATIN CAPITAL LETTER N WITH TILDE
            /*0xa6*/ '\u00aa',  // FEMININE ORDINAL INDICATOR
            /*0xa7*/ '\u00ba',  // MASCULINE ORDINAL INDICATOR
            /*0xa8*/ '\u00bf',  // INVERTED QUESTION MARK
            /*0xa9*/ '\u2310',  // REVERSED NOT SIGN
            /*0xaa*/ '\u00ac',  // NOT SIGN
            /*0xab*/ '\u00bd',  // VULGAR FRACTION ONE HALF
            /*0xac*/ '\u00bc',  // VULGAR FRACTION ONE QUARTER
            /*0xad*/ '\u00a1',  // INVERTED EXCLAMATION MARK
            /*0xae*/ '\u00ab',  // LEFT-POINTING DOUBLE ANGLE QUOTATION MARK
            /*0xaf*/ '\u00bb',  // RIGHT-POINTING DOUBLE ANGLE QUOTATION MARK
            /*0xb0*/ '\u2591',  // LIGHT SHADE
            /*0xb1*/ '\u2592',  // MEDIUM SHADE
            /*0xb2*/ '\u2593',  // DARK SHADE
            /*0xb3*/ '\u2502',  // BOX DRAWINGS LIGHT VERTICAL
            /*0xb4*/ '\u2524',  // BOX DRAWINGS LIGHT VERTICAL AND LEFT
            /*0xb5*/ '\u2561',  // BOX DRAWINGS VERTICAL SINGLE AND LEFT DOUBLE
            /*0xb6*/ '\u2562',  // BOX DRAWINGS VERTICAL DOUBLE AND LEFT SINGLE
            /*0xb7*/ '\u2556',  // BOX DRAWINGS DOWN DOUBLE AND LEFT SINGLE
            /*0xb8*/ '\u2555',  // BOX DRAWINGS DOWN SINGLE AND LEFT DOUBLE
            /*0xb9*/ '\u2563',  // BOX DRAWINGS DOUBLE VERTICAL AND LEFT
            /*0xba*/ '\u2551',  // BOX DRAWINGS DOUBLE VERTICAL
            /*0xbb*/ '\u2557',  // BOX DRAWINGS DOUBLE DOWN AND LEFT
            /*0xbc*/ '\u255d',  // BOX DRAWINGS DOUBLE UP AND LEFT
            /*0xbd*/ '\u255c',  // BOX DRAWINGS UP DOUBLE AND LEFT SINGLE
            /*0xbe*/ '\u255b',  // BOX DRAWINGS UP SINGLE AND LEFT DOUBLE
            /*0xbf*/ '\u2510',  // BOX DRAWINGS LIGHT DOWN AND LEFT
            /*0xc0*/ '\u2514',  // BOX DRAWINGS LIGHT UP AND RIGHT
            /*0xc1*/ '\u2534',  // BOX DRAWINGS LIGHT UP AND HORIZONTAL
            /*0xc2*/ '\u252c',  // BOX DRAWINGS LIGHT DOWN AND HORIZONTAL
            /*0xc3*/ '\u251c',  // BOX DRAWINGS LIGHT VERTICAL AND RIGHT
            /*0xc4*/ '\u2500',  // BOX DRAWINGS LIGHT HORIZONTAL
            /*0xc5*/ '\u253c',  // BOX DRAWINGS LIGHT VERTICAL AND HORIZONTAL
            /*0xc6*/ '\u255e',  // BOX DRAWINGS VERTICAL SINGLE AND RIGHT DOUBLE
            /*0xc7*/ '\u255f',  // BOX DRAWINGS VERTICAL DOUBLE AND RIGHT SINGLE
            /*0xc8*/ '\u255a',  // BOX DRAWINGS DOUBLE UP AND RIGHT
            /*0xc9*/ '\u2554',  // BOX DRAWINGS DOUBLE DOWN AND RIGHT
            /*0xca*/ '\u2569',  // BOX DRAWINGS DOUBLE UP AND HORIZONTAL
            /*0xcb*/ '\u2566',  // BOX DRAWINGS DOUBLE DOWN AND HORIZONTAL
            /*0xcc*/ '\u2560',  // BOX DRAWINGS DOUBLE VERTICAL AND RIGHT
            /*0xcd*/ '\u2550',  // BOX DRAWINGS DOUBLE HORIZONTAL
            /*0xce*/ '\u256c',  // BOX DRAWINGS DOUBLE VERTICAL AND HORIZONTAL
            /*0xcf*/ '\u2567',  // BOX DRAWINGS UP SINGLE AND HORIZONTAL DOUBLE
            /*0xd0*/ '\u2568',  // BOX DRAWINGS UP DOUBLE AND HORIZONTAL SINGLE
            /*0xd1*/ '\u2564',  // BOX DRAWINGS DOWN SINGLE AND HORIZONTAL DOUBLE
            /*0xd2*/ '\u2565',  // BOX DRAWINGS DOWN DOUBLE AND HORIZONTAL SINGLE
            /*0xd3*/ '\u2559',  // BOX DRAWINGS UP DOUBLE AND RIGHT SINGLE
            /*0xd4*/ '\u2558',  // BOX DRAWINGS UP SINGLE AND RIGHT DOUBLE
            /*0xd5*/ '\u2552',  // BOX DRAWINGS DOWN SINGLE AND RIGHT DOUBLE
            /*0xd6*/ '\u2553',  // BOX DRAWINGS DOWN DOUBLE AND RIGHT SINGLE
            /*0xd7*/ '\u256b',  // BOX DRAWINGS VERTICAL DOUBLE AND HORIZONTAL SINGLE
            /*0xd8*/ '\u256a',  // BOX DRAWINGS VERTICAL SINGLE AND HORIZONTAL DOUBLE
            /*0xd9*/ '\u2518',  // BOX DRAWINGS LIGHT UP AND LEFT
            /*0xda*/ '\u250c',  // BOX DRAWINGS LIGHT DOWN AND RIGHT
            /*0xdb*/ '\u2588',  // FULL BLOCK
            /*0xdc*/ '\u2584',  // LOWER HALF BLOCK
            /*0xdd*/ '\u258c',  // LEFT HALF BLOCK
            /*0xde*/ '\u2590',  // RIGHT HALF BLOCK
            /*0xdf*/ '\u2580',  // UPPER HALF BLOCK
            /*0xe0*/ '\u03b1',  // GREEK SMALL LETTER ALPHA
            /*0xe1*/ '\u00df',  // LATIN SMALL LETTER SHARP S
            /*0xe2*/ '\u0393',  // GREEK CAPITAL LETTER GAMMA
            /*0xe3*/ '\u03c0',  // GREEK SMALL LETTER PI
            /*0xe4*/ '\u03a3',  // GREEK CAPITAL LETTER SIGMA
            /*0xe5*/ '\u03c3',  // GREEK SMALL LETTER SIGMA
            /*0xe6*/ '\u00b5',  // MICRO SIGN
            /*0xe7*/ '\u03c4',  // GREEK SMALL LETTER TAU
            /*0xe8*/ '\u03a6',  // GREEK CAPITAL LETTER PHI
            /*0xe9*/ '\u0398',  // GREEK CAPITAL LETTER THETA
            /*0xea*/ '\u03a9',  // GREEK CAPITAL LETTER OMEGA
            /*0xeb*/ '\u03b4',  // GREEK SMALL LETTER DELTA
            /*0xec*/ '\u221e',  // INFINITY
            /*0xed*/ '\u03c6',  // GREEK SMALL LETTER PHI
            /*0xee*/ '\u03b5',  // GREEK SMALL LETTER EPSILON
            /*0xef*/ '\u2229',  // INTERSECTION
            /*0xf0*/ '\u2261',  // IDENTICAL TO
            /*0xf1*/ '\u00b1',  // PLUS-MINUS SIGN
            /*0xf2*/ '\u2265',  // GREATER-THAN OR EQUAL TO
            /*0xf3*/ '\u2264',  // LESS-THAN OR EQUAL TO
            /*0xf4*/ '\u2320',  // TOP HALF INTEGRAL
            /*0xf5*/ '\u2321',  // BOTTOM HALF INTEGRAL
            /*0xf6*/ '\u00f7',  // DIVISION SIGN
            /*0xf7*/ '\u2248',  // ALMOST EQUAL TO
            /*0xf8*/ '\u00b0',  // DEGREE SIGN
            /*0xf9*/ '\u2219',  // BULLET OPERATOR
            /*0xfa*/ '\u00b7',  // MIDDLE DOT
            /*0xfb*/ '\u221a',  // SQUARE ROOT
            /*0xfc*/ '\u207f',  // SUPERSCRIPT LATIN SMALL LETTER N
            /*0xfd*/ '\u00b2',  // SUPERSCRIPT TWO
            /*0xfe*/ '\u25a0',  // BLACK SQUARE
            /*0xff*/ '\u00a0',  // NO-BREAK SPACE
        };

        /// <summary>
        /// Maps a BMP (Basic Multilingual Plane) code point to a CP437 byte value (0-255).
        /// Invalid values are mapped to -1.
        /// </summary>
        private static short[] sUnicodeToCP437 = GenerateReverseMap(sCP437ToUnicode);

        #endregion CP437

        /// <summary>
        /// Generates a reverse character mapping for BMP code points.  Throws an exception if
        /// the reverse mapping is ambiguous because two values map to the same thing.
        /// </summary>
        private static short[] GenerateReverseMap(char[] map) {
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
            return rev;
        }
    }
}
