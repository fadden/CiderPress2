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

using static FileConv.FancyText;

namespace FileConv.Doc {
    /// <summary>
    /// Common definitions for Apple IIgs documents.
    /// </summary>
    public static class GSDocCommon {
        /// <summary>
        /// Known Apple IIgs font families.
        /// </summary>
        /// <remarks>
        /// The Macintosh used the same set of IDs for 0-24.  See page I-219 in _Inside
        /// Macintosh, Volume I_.
        /// </remarks>
        public enum FontFamilyNum {
            // Font family numbers with the high bit set are designed for the 5:12 aspect
            // ratio of the Apple IIgs.  Family numbers with the high bit clear are designed
            // for the 1:1 aspect ratio of the Macintosh.  Font numbers 1000-1200 ($3e8-4b0)
            // do not adhere to this convention.
            System = 0x0000,        // system font
            ApplFont = 0x0001,      // application font (Mac only?)

            // Font families defined by Apple IIgs Toolbox Reference (page 8-4) and IIgs
            // tech note #41.
            NewYork = 0x0002,
            Geneva = 0x0003,
            Monaco = 0x0004,
            Venice = 0x0005,
            London = 0x0006,
            Athens = 0x0007,
            SanFran = 0x0008,
            Toronto = 0x0009,
            Cairo = 0x000b,
            LosAngeles = 0x000c,
            // ZapfDingbats = 0x000d,
            // Bookman = 0x000e,
            // Helvetica Narrow = 0x000f,
            // Palatino = 0x0010,
            // ZapfChancery = 0x0012,
            Times = 0x0014,
            Helvetica = 0x0015,
            Courier = 0x0016,
            Symbol = 0x0017,
            Taliesin = 0x0018,
            // AvantGarde = 0x0021,
            // Chicago = 0xfffd,
            Shaston = 0xfffe,

            // Some additional families found in the wild (e.g. from Pointless or an app).
            Starfleet = 0x078d,
            Western = 0x088e,
            Genoa = 0x0bcb,
            Classical = 0x2baa,
            ChicagoAlt = 0x3fff,
            Genesys = 0x7530,
            PCMonospace = 0x7f58,
            AppleM = 0x7fdc,
            Unknown1 = 0x9c50,      // found in French AWGS doc "CONVSEC"
            Unknown2 = 0x9c54,      // found in French AWGS doc "CONVSEC"
        }

        private class FontDesc {
            public FontFamilyNum FamilyNum { get; private set; }
            public FontFamily Family { get; private set; }

            public FontDesc(FontFamilyNum familyNum, FontFamily family) {
                FamilyNum = familyNum;
                Family = family;
            }
        }

        /// <summary>
        /// List of fonts used in Apple IIgs documents.  Various attributes are noted that can
        /// be used to find the best match.
        /// </summary>
        /// <remarks>
        /// <para>More font families were defined than were included with GS/OS, and Pointless
        /// (TrueType font add-on for the IIgs) opened the door for many more.</para>
        /// </remarks>
        private static readonly FontDesc[] sKnownFonts = new FontDesc[] {
            new FontDesc(FontFamilyNum.NewYork,                     // serif
                new FontFamily("New York", isMono: false, isSerif: true)),
            new FontDesc(FontFamilyNum.Geneva,                      // sans-serif
                new FontFamily("Geneva", isMono: false, isSerif: false)),
            new FontDesc(FontFamilyNum.Monaco,                      // monospace + serif
                new FontFamily("Monaco", isMono: true, isSerif: true)),
            new FontDesc(FontFamilyNum.Venice,                      // fancy script, serif
                new FontFamily("Venice", isMono: false, isSerif: true)),
            new FontDesc(FontFamilyNum.London,
                new FontFamily("London", isMono: false, isSerif: true)),
            new FontDesc(FontFamilyNum.Athens,
                new FontFamily("Athens", isMono: false, isSerif: false)),
            new FontDesc(FontFamilyNum.SanFran,
                new FontFamily("San Francisco", isMono: false, isSerif: false)),
            new FontDesc(FontFamilyNum.Toronto,
                new FontFamily("Toronto", isMono: false, isSerif: false)),
            new FontDesc(FontFamilyNum.Cairo,
                new FontFamily("Cairo", isMono: false, isSerif: false)),
            new FontDesc(FontFamilyNum.LosAngeles,                  // script?
                new FontFamily("Los Angeles", isMono: false, isSerif: false)),
            new FontDesc(FontFamilyNum.Times,                       // serif
                new FontFamily("Times", isMono: false, isSerif: true)),
            new FontDesc(FontFamilyNum.Helvetica,                   // sans-serif
                new FontFamily("Helvetica", isMono: false, isSerif: false)),
            new FontDesc(FontFamilyNum.Courier,                     // mono + serif
                new FontFamily("Courier", isMono: true, isSerif: true)),
            new FontDesc(FontFamilyNum.Symbol,
                new FontFamily("Symbol", isMono: false, isSerif: false)),
            new FontDesc(FontFamilyNum.Taliesin,                    // (artsy)
                new FontFamily("Taliesin", isMono: false, isSerif: false)),
            new FontDesc(FontFamilyNum.Shaston,                     // ROM font; sans-serif
                new FontFamily("Shaston", isMono: false, isSerif: false)),

            new FontDesc(FontFamilyNum.Starfleet,
                new FontFamily("Starfleet", isMono: false, isSerif: false)),
            new FontDesc(FontFamilyNum.Western,
                new FontFamily("Western", isMono: false, isSerif: false)),
            new FontDesc(FontFamilyNum.Genoa,
                new FontFamily("Genoa", isMono: false, isSerif: false)),
            new FontDesc(FontFamilyNum.Classical,
                new FontFamily("Classical", isMono: false, isSerif: false)),
            new FontDesc(FontFamilyNum.ChicagoAlt,
                new FontFamily("Chicago", isMono: false, isSerif: false)),
            new FontDesc(FontFamilyNum.Genesys,                     // mono, sans-serif
                new FontFamily("Genesys", isMono: true, isSerif: false)),
            new FontDesc(FontFamilyNum.PCMonospace,
                new FontFamily("PC Monospace", isMono: true, isSerif: false)),
            new FontDesc(FontFamilyNum.AppleM,                      // mono, sans-serif
                new FontFamily("Apple M", isMono: true, isSerif: false)),
        };

        private static readonly Dictionary<int, FontFamily> sFamilyMap = GenerateFamilyMap();

        private static Dictionary<int, FontFamily> GenerateFamilyMap() {
            Dictionary<int, FontFamily> map = new Dictionary<int, FontFamily>();
            foreach (FontDesc desc in sKnownFonts) {
                map.Add((int)desc.FamilyNum, desc.Family);
            }
            return map;
        }

        /// <summary>
        /// Finds a font family by IIgs font family number.
        /// </summary>
        /// <param name="familyNum">Family number.</param>
        /// <returns>Font descriptor, or null if not found.</returns>
        public static FontFamily? FindFont(ushort familyNum) {
            if (sFamilyMap.TryGetValue(familyNum, out FontFamily? fontDesc)) {
                return fontDesc;
            }
            return null;
        }

        /// <summary>
        /// Gets a size multiplier for the font.
        /// </summary>
        /// <remarks>
        /// <para>The on-screen size of the various fonts varied significantly.  For example,
        /// the distance from the top of a 'T' to the bottom of a 'j' was 39 pixels in 24-point
        /// Courier, but 59 pixels in 24-point Venice.  The differences are also felt in the
        /// widths, which affect line length / word wrap.</para>
        /// <para>Reducing the point size excessively is unwise.  An 8-point font is fairly
        /// readable on the IIgs screen, but gets a bit tiny in RTF.  (We may want to apply
        /// a broad up-scaling.)</para>
        /// </remarks>
        /// <param name="familyNum">Font family number.</param>
        /// <returns>Size multiplier.  Will be 1.0 for unknown fonts.</returns>
        public static float GetFontMult(ushort familyNum) {
            FontFamilyNum eNum = (FontFamilyNum)familyNum;
            switch (eNum) {
                case FontFamilyNum.Classical:
                case FontFamilyNum.Genoa:
                case FontFamilyNum.Monaco:
                case FontFamilyNum.Western:
                    return 0.85f;           // 0.8 makes some things too small
                case FontFamilyNum.Times:
                    return 0.9f;
                case FontFamilyNum.ChicagoAlt:
                case FontFamilyNum.Courier:
                case FontFamilyNum.Geneva:
                case FontFamilyNum.Helvetica:
                case FontFamilyNum.Symbol:
                case FontFamilyNum.Starfleet:
                case FontFamilyNum.Unknown1:
                case FontFamilyNum.Unknown2:
                case FontFamilyNum.Venice:
                    return 1.0f;
                case FontFamilyNum.NewYork:
                    return 1.1f;
                case FontFamilyNum.AppleM:
                case FontFamilyNum.Athens:
                case FontFamilyNum.Cairo:
                case FontFamilyNum.Genesys:
                case FontFamilyNum.London:
                case FontFamilyNum.LosAngeles:
                case FontFamilyNum.SanFran:
                case FontFamilyNum.Shaston:
                case FontFamilyNum.System:
                case FontFamilyNum.Toronto:
                case FontFamilyNum.PCMonospace:
                case FontFamilyNum.Taliesin:
                    return 1.5f;
                default:
                    return 1.0f;
            }
        }

        /// <summary>
        /// Font style flags.
        /// </summary>
        [Flags]
        public enum FontStyleBits : byte {
            Plain = 0,
            Bold = 0x01,
            Italic = 0x02,
            Underline = 0x04,
            Outline = 0x08,
            Shadow = 0x10,
            // reserved 0x20
            Superscript = 0x40,     // not in QDII - AWGS only
            Subscript = 0x80,       // not in QDII - AWGS only
        }

        /// <summary>
        /// Standard color palette in 320 mode.
        /// </summary>
        /// <remarks>
        /// Apple IIgs Toolbox Reference, volume 2, page 16-35.
        /// </remarks>
        private static readonly int[] sStdColor320 = new int[] {
            ConvUtil.MakeRGB(0x00, 0x00, 0x00),     // 0000 Black
            ConvUtil.MakeRGB(0x77, 0x77, 0x77),     // 0001 Dark gray
            ConvUtil.MakeRGB(0x88, 0x44, 0x11),     // 0010 Brown
            ConvUtil.MakeRGB(0x77, 0x22, 0xcc),     // 0011 Purple
            ConvUtil.MakeRGB(0x00, 0x00, 0xff),     // 0100 Blue
            ConvUtil.MakeRGB(0x00, 0x88, 0x00),     // 0101 Dark green
            ConvUtil.MakeRGB(0xff, 0x77, 0x00),     // 0110 Orange
            ConvUtil.MakeRGB(0xdd, 0x00, 0x00),     // 0111 Red
            ConvUtil.MakeRGB(0xff, 0xaa, 0x99),     // 1000 Beige
            ConvUtil.MakeRGB(0xff, 0xff, 0x00),     // 1001 Yellow
            ConvUtil.MakeRGB(0x00, 0xee, 0x00),     // 1010 Green
            ConvUtil.MakeRGB(0x44, 0xdd, 0xff),     // 1011 Light blue
            ConvUtil.MakeRGB(0xdd, 0xaa, 0xff),     // 1100 Lilac
            ConvUtil.MakeRGB(0x77, 0x88, 0xff),     // 1101 Periwinkle blue
            ConvUtil.MakeRGB(0xcc, 0xcc, 0xcc),     // 1110 Light gray
            ConvUtil.MakeRGB(0xff, 0xff, 0xff),     // 1111 White
        };

        /// <summary>
        /// Standard color palette in 640 mode.  This is a combination of solid and dithered
        /// colors.  We don't want to draw the dither patterns, so we just return a solid
        /// blended color instead.
        /// </summary>
        /// <remarks>
        /// Apple IIgs Toolbox Reference, volume 2, page 16-36.
        /// </remarks>
        private static readonly int[] sStdColor640 = new int[] {
            ConvUtil.MakeRGB(0x00, 0x00, 0x00),     // 0000 Black+Black
            ConvUtil.MakeRGB(0x00, 0x00, 0xc0),     // 0001 Black+Blue
            ConvUtil.MakeRGB(0xc0, 0xc0, 0x00),     // 0010 Black+Yellow
            ConvUtil.MakeRGB(0xc0, 0xc0, 0xc0),     // 0011 Black+White
            ConvUtil.MakeRGB(0xc0, 0x00, 0x00),     // 0100 Red+Black
            ConvUtil.MakeRGB(0xc0, 0x00, 0xc0),     // 0101 Red+Blue
            ConvUtil.MakeRGB(0xff, 0xc0, 0x00),     // 0110 Red+Yellow
            ConvUtil.MakeRGB(0xff, 0xc0, 0xc0),     // 0111 Red+White
            ConvUtil.MakeRGB(0x00, 0xc0, 0x00),     // 1000 Green+Black
            ConvUtil.MakeRGB(0x00, 0xc0, 0xc0),     // 1001 Green+Blue
            ConvUtil.MakeRGB(0xc0, 0xff, 0x00),     // 1010 Green+Yellow
            ConvUtil.MakeRGB(0xc0, 0xff, 0xc0),     // 1011 Green+White
            ConvUtil.MakeRGB(0xc0, 0xc0, 0xc0),     // 1100 White+Black
            ConvUtil.MakeRGB(0xc0, 0xc0, 0xff),     // 1101 White+Blue
            ConvUtil.MakeRGB(0xff, 0xff, 0xc0),     // 1110 White+Yellow
            ConvUtil.MakeRGB(0xff, 0xff, 0xff),     // 1111 White+White
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
