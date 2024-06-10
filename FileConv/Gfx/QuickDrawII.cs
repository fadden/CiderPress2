/*
 * Copyright 2024 faddenSoft
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

namespace FileConv.Gfx {
    public static class QuickDrawII {
        /// <summary>
        /// Standard color palette in 320 mode.
        /// </summary>
        /// <remarks>
        /// Apple IIgs Toolbox Reference, volume 2, page 16-35.  Every pixel is 4 bits, and
        /// does a direct lookup into the table.
        /// </remarks>
        internal static readonly int[] sStdColor320 = new int[] {
            ConvUtil.MakeRGB(0x00, 0x00, 0x00),     // 0 000 Black
            ConvUtil.MakeRGB(0x77, 0x77, 0x77),     // 1 777 Dark gray
            ConvUtil.MakeRGB(0x88, 0x44, 0x11),     // 2 841 Brown
            ConvUtil.MakeRGB(0x77, 0x22, 0xcc),     // 3 72C Purple
            ConvUtil.MakeRGB(0x00, 0x00, 0xff),     // 4 00F Blue
            ConvUtil.MakeRGB(0x00, 0x88, 0x00),     // 5 080 Dark green
            ConvUtil.MakeRGB(0xff, 0x77, 0x00),     // 6 F70 Orange
            ConvUtil.MakeRGB(0xdd, 0x00, 0x00),     // 7 D00 Red
            ConvUtil.MakeRGB(0xff, 0xaa, 0x99),     // 8 FA9 Beige
            ConvUtil.MakeRGB(0xff, 0xff, 0x00),     // 9 FF0 Yellow
            ConvUtil.MakeRGB(0x00, 0xee, 0x00),     // A 0E0 Green
            ConvUtil.MakeRGB(0x44, 0xdd, 0xff),     // B 4DF Light blue
            ConvUtil.MakeRGB(0xdd, 0xaa, 0xff),     // C DAF Lilac
            ConvUtil.MakeRGB(0x77, 0x88, 0xff),     // D 78F Periwinkle blue
            ConvUtil.MakeRGB(0xcc, 0xcc, 0xcc),     // E CCC Light gray
            ConvUtil.MakeRGB(0xff, 0xff, 0xff),     // F FFF White
        };

        /// <summary>
        /// Standard color palette in 640 mode.  This is a combination of solid and dithered
        /// colors.  We don't want to draw the dither patterns, so we just return a solid
        /// blended color instead.
        /// </summary>
        /// <remarks>
        /// <para>Apple IIgs Toolbox Reference, volume 2, page 16-159.  According to the error
        /// corrections in TBRef volume 3 (page 43-2), the table on page 16-36 is incorrect.</para>
        /// <para>In 320 mode, the 4-bit pixel value is used as an index into a 16-entry table.
        /// In 640 mode, the 2-bit pixel value is an index into one of four 4-entry "mini
        /// palettes".  Each of the four pixels in a byte uses a different mini-palette.</para>
        /// <para>To ensure black &amp; white look good, the standard palette puts black in entry 0
        /// and white in entry 3 of every mini-palette.  Entry 1 is blue or red, entry 2 is
        /// yellow or green, based on whether the pixel is even or odd.  For example, the GS/OS
        /// finder uses the standard palette for most of the screen, with the color $D for the
        /// background.  This is alternating white/blue, which forms the lavender color.</para>
        /// <para>For this table, we take two adjacent pixels and blend the colors together.
        /// (Some emulators appear to do this, except that white+black is displayed as independent
        /// pixels so fine detail isn't lost.)  This allows the table to be used the same way
        /// as it would be in 320 mode, and for TextEdit "color words".</para>
        /// </remarks>
        internal static readonly int[] sStdColor640 = new int[] {
            ConvUtil.MakeRGB(0x00, 0x00, 0x00),     // 0=0000 Black+Black
            ConvUtil.MakeRGB(0x00, 0x00, 0x7f),     // 1=0001 Black+Blue
            ConvUtil.MakeRGB(0x7f, 0x7f, 0x00),     // 2=0010 Black+Yellow
            ConvUtil.MakeRGB(0x6f, 0x6f, 0x6f),     // 3=0011 Black+White (should be alternating)
            ConvUtil.MakeRGB(0x7f, 0x00, 0x00),     // 4=0100 Red+Black
            ConvUtil.MakeRGB(0x7f, 0x00, 0x7f),     // 5=0101 Red+Blue
            ConvUtil.MakeRGB(0xff, 0x7f, 0x00),     // 6=0110 Red+Yellow
            ConvUtil.MakeRGB(0xff, 0x7f, 0x7f),     // 7=0111 Red+White
            ConvUtil.MakeRGB(0x00, 0x7f, 0x00),     // 8=1000 Green+Black
            ConvUtil.MakeRGB(0x00, 0x7f, 0x7f),     // 9=1001 Green+Blue
            ConvUtil.MakeRGB(0x7f, 0xff, 0x00),     // A=1010 Green+Yellow
            ConvUtil.MakeRGB(0x7f, 0xff, 0x7f),     // B=1011 Green+White
            ConvUtil.MakeRGB(0x8f, 0x8f, 0x8f),     // C=1100 White+Black (should be alternating)
            ConvUtil.MakeRGB(0x7f, 0x7f, 0xff),     // D=1101 White+Blue
            ConvUtil.MakeRGB(0xff, 0xff, 0x7f),     // E=1110 White+Yellow
            ConvUtil.MakeRGB(0xff, 0xff, 0xff),     // F=1111 White+White
        };

        /// <summary>
        /// Gets a color from the standard QDII color palette.
        /// </summary>
        /// <param name="index">16-bit color pattern.  Only the low 4 bits are used.</param>
        /// <param name="is640">True if we want the 640-mode color.</param>
        /// <returns>ARGB color value.</returns>
        public static int GetStdColor(int index, bool is640 = false) {
            if (is640) {
                return sStdColor640[index & 0x0f];
            } else {
                return sStdColor320[index & 0x0f];
            }
        }
    }
}
