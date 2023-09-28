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

namespace CommonUtil {
    /// <summary>
    /// MouseText (<see href="https://en.wikipedia.org/wiki/MouseText"/>) character set
    /// conversion utilities.
    /// </summary>
    public static class MouseTextChar {
        /// <summary>
        /// Converts a MouseText character to Unicode.
        /// </summary>
        /// <param name="mtChar">MouseText character index (0-31).</param>
        /// <returns>Unicode equivalent.  UTF-16 string encoding may require a surrogate
        ///   pair.</returns>
        public static string MouseTextToUnicode(byte mtChar) {
            if (mtChar < sMouseToUnicode.Length) {
                return sMouseToUnicode[mtChar];
            } else {
                Debug.Assert(false);
                return "\u00bf";        // INVERTED QUESTION MARK
            }
        }

        /// <summary>
        /// Converts a MouseText character to ASCII.
        /// </summary>
        /// <param name="mtChar">MouseText character index (0-31).</param>
        /// <returns>ASCII equivalent.</returns>
        public static char MouseTextToASCII(byte mtChar) {
            if (mtChar < sMouseToAlt.Length) {
                return sMouseToAlt[mtChar];
            } else {
                Debug.Assert(false);
                return '\u00bf';        // INVERTED QUESTION MARK
            }
        }

        /// <summary>
        /// Unicode conversion.  For some characters, the best match is outside the BMP, so we
        /// need strings with surrogate pairs.
        /// </summary>
        /// <remarks>
        /// <para>Originally from http://hoop-la.ca/apple2/docs/mousetext/unicode.html
        /// (link dead, not archived).</para>
        /// </remarks>
        private static readonly string[] sMouseToUnicode = new string[32] {
            /*0x00*/ "\U0001F34E",      // RED APPLE
            /*0x01*/ "\U0001F34F",      // GREEN APPLE (or U+2316 POSITION INDICATOR?)
            /*0x02*/ "\u25c4",          // BLACK LEFT-POINTING POINTER
            /*0x03*/ "\u23f3",          // HOURGLASS WITH FLOWING SAND
            /*0x04*/ "\u2713",          // CHECK MARK
            /*0x05*/ "\u2705",          // WHITE HEAVY CHECK MARK
            /*0x06*/ "\u23ce",          // RETURN SYMBOL
            /*0x07*/ "\u2630",          // TRIGRAM FOR HEAVEN (3 lines, want 4)
            /*0x08*/ "\u2190",          // LEFTWARDS ARROW
            /*0x09*/ "\u2026",          // HORIZONTAL ELLIPSIS
            /*0x0a*/ "\u2193",          // DOWNWARDS ARROW
            /*0x0b*/ "\u2191",          // UPWARDS ARROW
            /*0x0c*/ "\u2594",          // UPPER ONE EIGHTH BLOCK
            /*0x0d*/ "\u21b5",          // DOWNWARDS ARROW WITH CORNER LEFTWARDS
            /*0x0e*/ "\u2589",          // LEFT SEVEN EIGHTHS BLOCK
            /*0x0f*/ "\u21e4",          // LEFTWARDS ARROW TO BAR
            /*0x10*/ "\u21e5",          // RIGHTWARDS ARROW TO BAR
            /*0x11*/ "\u2913",          // DOWNWARDS ARROW TO BAR
            /*0x12*/ "\u2912",          // UPWARDS ARROW TO BAR
            /*0x13*/ "\u2500",          // BOX DRAWINGS LIGHT HORIZONTAL
            /*0x14*/ "\u231e",          // BOTTOM LEFT CORNER
            /*0x15*/ "\u2192",          // UPWARDS ARROW TO BAR
            /*0x16*/ "\u2591",          // LIGHT SHADE (or U+1f67e CHECKER BOARD)
            /*0x17*/ "\u2592",          // MEDIUM SHADE (or U+1f67f REVERSE CHECKER BOARD)
            /*0x18*/ "\U0001F4C1",      // FILE FOLDER (might be better as a box)
            /*0x19*/ "\U0001F4C2",      // OPEN FILE FOLDER
            /*0x1a*/ "\u2595",          // RIGHT ONE EIGHTH BLOCK
            /*0x1b*/ "\u2666",          // BLACK DIAMOND SUIT
            /*0x1c*/ "\u203e",          // OVERLINE (wrong, want top+bottom)
            /*0x1d*/ "\u256c",          // BOX DRAWINGS DOUBLE VERTICAL AND HORIZONTAL
            /*0x1e*/ "\u22a1",          // SQUARED DOT OPERATOR (or U+1f4be FLOPPYDISK?); seems better than 25a3
            /*0x1f*/ "\u258f",          // LEFT ONE EIGHTH BLOCK
        };

        /// <summary>
        /// Plain ASCII equivalents displayed by AppleWorks when MouseText is not available.
        /// Extracted from SEG.WP by Hugh Hood.
        /// </summary>
        private static readonly char[] sMouseToAlt = new char[32] {
            /*0x00*/ '@',               // closed apple
            /*0x01*/ '@',               // open apple
            /*0x02*/ '^',               // mouse pointer
            /*0x03*/ '&',               // hourglass
            /*0x04*/ '\'',              // checkmark
            /*0x05*/ '\'',              // inverse checkmark
            /*0x06*/ '/',               // running man left -or- inverse return icon
            /*0x07*/ ':',               // running man right -or- four horizontal lines
            /*0x08*/ '<',               // left arrow
            /*0x09*/ '_',               // ellipsis
            /*0x0a*/ 'v',               // down arrow
            /*0x0b*/ '^',               // up arrow
            /*0x0c*/ '-',               // horizontal line, top
            /*0x0d*/ '/',               // return icon
            /*0x0e*/ '$',               // solid block
            /*0x0f*/ '{',               // left scroll arrow
            /*0x10*/ '}',               // right scroll arrow
            /*0x11*/ 'v',               // downward scroll arrow
            /*0x12*/ '^',               // upward scroll arrow
            /*0x13*/ '-',               // horizontal line, middle
            /*0x14*/ 'L',               // bottom-left corner ('L' shape)
            /*0x15*/ '>',               // right arrow
            /*0x16*/ '*',               // checkerboard 1
            /*0x17*/ '*',               // checkerboard 2
            /*0x18*/ '[',               // file folder left
            /*0x19*/ ']',               // file folder right
            /*0x1a*/ '|',               // vertical line, right
            /*0x1b*/ '#',               // diamond
            /*0x1c*/ '=',               // horizontal line, top and bottom
            /*0x1d*/ '#',               // plus sign (-ish)
            /*0x1e*/ 'O',               // square with dot and no left edge
            /*0x1f*/ '|',               // vertical line, left
        };
    }
}
