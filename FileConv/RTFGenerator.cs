﻿/*
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
using System.Text;

using static FileConv.FancyText;

namespace FileConv {
    /// <summary>
    /// Generates an RTF document (<see href="https://en.wikipedia.org/wiki/Rich_Text_Format"/>)
    /// from a FancyText object.
    /// </summary>
    /// <remarks>
    /// <para>The last update to RTF was v1.9.1, for Word 2007, in 2008.  Everything we use
    /// here was part of RTF v1.5 (<see href="https://www.biblioscape.com/rtf15_spec.htm"/>),
    /// published April 1997 for Word 97.</para>
    /// </remarks>
    public static class RTFGenerator {
        public const string FILE_EXT = ".rtf";

        /// <summary>
        /// RTF uses "twips", which are 1/20th of a point.  There are 72 points per inch.
        /// </summary>
        private const int TWIPS_PER_INCH = 1440;

        /// <summary>
        /// RTF isn't much concerned with line breaks.  It's a Microsoft format, so we might as
        /// well use CRLF when we emit them.
        /// </summary>
        private const string CRLF = "\r\n";

        /// <summary>
        /// First part of file header.  Configures character set and fonts.
        /// </summary>
        private const string HDR_START =
            // version 1, ANSI charset, code page 1252, def. font #0, def. language 1033 (en-US)
            @"{\rtf1\ansi\ansicpg1252\deff0\deflang1033\deflangfe1033{\fonttbl" +
            // font #0: Consolas; family=modern, pitch=fixed, charset=ANSI
            @"{\f0\fmodern\fprq1\fcharset0 Consolas;}" +
            // font #1: Courier New; family=modern, pitch=fixed, charset=ANSI
            @"{\f1\fmodern\fprq1\fcharset0 Courier New;}" +
            // font #2: Times New Roman; family=roman, pitch=variable, charset=ANSI
            @"{\f2\froman\fprq2\fcharset0 Times New Roman;}" +
            // font #3: Arial; family=swiss, pitch=variable, charset=ANSI
            @"{\f3\fswiss\fprq2\fcharset0 Arial;}" +
            // font #4: Symbol; family=roman, pitch=variable, charset=Symbol
            @"{\f4\froman\fprq2\fcharset2 Symbol;}" +
            @"}" + CRLF;
        private enum FontIndex { Consolas = 0, CourierNew, TimesNewRoman, Arial, Symbol };

        /// <summary>
        /// Last part of file header.
        /// </summary>
        private const string HDR_END =
            // normal view, 1-byte Unicode, reset default paragraph props, font #0, 10-point font
            @"\viewkind4\uc1\pard\f0\fs20 ";

        /// <summary>
        /// Final bit to write at the end of the file.
        /// </summary>
        private const string FILE_END =
            "}\r\n\0";

        /// <summary>
        /// Text colors available to documents.
        /// </summary>
        /// <remarks>
        /// <para>RTF documents use an indexed color palette.  These values match the color table
        /// generated by old versions of MS Word.</para>
        /// <para>The names are based on <see cref="System.Windows.Media.Colors"/> members,
        /// which are apparently based on X11 named color values, which are different from the
        /// W3C colors (<see href="https://en.wikipedia.org/wiki/X11_color_names"/>).</para>
        /// <para>Color #0 is "auto", so output indices are off by one.</para>
        /// </remarks>
        private static readonly int[] sColors = {
            // full colors (RGB component 0 or 255)
            0x000000,       // 1=Black
            0x0000ff,       // 2=Blue
            0x00ffff,       // 3=Cyan
            0x00ff00,       // 4=Lime
            0xff00ff,       // 5=Magenta
            0xff0000,       // 6=Red
            0xffff00,       // 7=Yellow
            0xffffff,       // 8=White
            // mixed colors
            0x000080,       // 9=DarkBlue (Colors.DarkBlue is #00008b)
            0x008080,       // 10=Teal
            0x008000,       // 11=Green
            0x800080,       // 12=DarkMagenta (Colors.DarkMagenta is #8b008b)
            0x800000,       // 13=DarkRed (Colors.DarkRed is #8b0000)
            0x808000,       // 14=Olive
            0x808080,       // 15=Gray
            0xc0c0c0,       // 16=LightGray (Colors.LightGray is #d3d3d3)
            0x404040,       // 17=DarkGray (Colors.DarkGray is #a9a9a9 -- much lighter)
            0xff9900,       // 18=Orange (Colors.Orange is #ffa500)
        };
        private const int COLOR_INDEX_BLACK = 1;
        private const int COLOR_INDEX_WHITE = 8;

        /// <summary>
        /// Optional color table.
        /// </summary>
        //private const string HDR_COLOR_TAB =
        //    @"{\colortbl;" +
        //    @"\red0\green0\blue0;\red0\green0\blue255;\red0\green255\blue255;\red0\green255\blue0;" +
        //    @"\red255\green0\blue255;\red255\green0\blue0;\red255\green255\blue0;\red255\green255\blue255;" +
        //    @"\red0\green0\blue128;\red0\green128\blue128;\red0\green128\blue0;" + CRLF +
        //    @"\red128\green0\blue128;\red128\green0\blue0;\red128\green128\blue0;\red128\green128\blue128;" +
        //    @"\red192\green192\blue192;\red64\green64\blue64;\red255\green153\blue0;"+
        //    @"}" + CRLF;
        private static readonly string HDR_COLOR_TAB = GenerateColorTable();

        private static string GenerateColorTable() {
            StringBuilder sb = new StringBuilder();
            sb.Append(@"{\colortbl;");      // leave space for color #0 = auto
            for (int i = 0; i < sColors.Length; i++) {
                int color = sColors[i];
                sb.AppendFormat(@"\red{0}\green{1}\blue{2};",
                    (color >> 16) & 0xff, (color >> 8) & 0xff, color & 0xff);
                if (i == 10) {
                    sb.Append(CRLF);    // match original CiderPress output
                }
            }
            sb.Append("}" + CRLF);
            return sb.ToString();
        }

        /// <summary>
        /// Generates an RTF document from the fancy text object.
        /// </summary>
        /// <param name="src">Document source.</param>
        /// <param name="outStream">Output stream.  The stream will be left open.</param>
        public static void Generate(FancyText src, Stream outStream) {
            // Use Encoding.Latin1, which is similar to CP1252.
            using (StreamWriter txtOut = new StreamWriter(outStream, Encoding.Latin1, -1, true)) {
                txtOut.Write(HDR_START);
                txtOut.Write(HDR_COLOR_TAB);    // TODO: omit if there are no color changes
                txtOut.Write(HDR_END);

                // See if our default font matches the FancyText default font.  Fix it if not.
                int fontIndex = ClosestFontFamily(FancyText.DEFAULT_FONT);
                if (fontIndex != 0) {
                    Debug.Assert(false, "default font mismatch");
                    txtOut.Write(@"\f" + fontIndex + " ");
                }

                // Walk through the text and annotations in parallel.
                StringBuilder sb = src.Text;
                IEnumerator<Annotation> iter = src.GetEnumerator();
                bool hasAnno = iter.MoveNext();
                int idx = 0;
                while (idx < sb.Length) {
                    if (hasAnno && iter.Current.Offset == idx) {
                        OutputAnnotation(iter.Current, txtOut);
                        idx += iter.Current.ExtraLen;
                        hasAnno = iter.MoveNext();
                        continue;
                    }

                    PrintChar(sb[idx], txtOut);
                    idx++;
                }

                txtOut.Write(FILE_END);
            }
        }

        /// <summary>
        /// Prints a character to the RTF output stream, escaping it if necessary.
        /// </summary>
        /// <param name="ch">Character to print.</param>
        /// <param name="txtOut">Output stream.</param>
        private static void PrintChar(char ch, StreamWriter txtOut) {
            if (ch == '\\') {
                txtOut.Write(@"\\");
            } else if (ch == '{') {
                txtOut.Write(@"\{");
            } else if (ch == '}') {
                txtOut.Write(@"\}");
            } else if (ch >= 0x20 && ch < 0x80) {
                // Printable char.
                txtOut.Write(ch);
            } else {
                // Non-ASCII value or control character.  Output as UTF-16.  This is expected to
                // be output as a signed value, though it looks like most parsers work either way.
                txtOut.Write(@"\u" + (short)ch + "?");
            }
        }

        private static void OutputAnnotation(Annotation anno, StreamWriter txtOut) {
            switch (anno.Type) {
                case AnnoType.NewParagraph:
                    txtOut.Write(@"\par" + CRLF);
                    break;
                case AnnoType.NewPage:
                    // Doesn't seem to be supported by RichTextBox, but is handled by MS Word.
                    txtOut.Write(@"\page ");
                    break;
                case AnnoType.Tab:
                    txtOut.Write(@"\tab ");
                    break;
                case AnnoType.Justification:
                    Justification justMode = (Justification)anno.Data!;
                    switch (justMode) {
                        case Justification.Left:
                            txtOut.Write(@"\ql ");
                            break;
                        case Justification.Right:
                            txtOut.Write(@"\qr ");
                            break;
                        case Justification.Center:
                            txtOut.Write(@"\qc ");
                            break;
                        case Justification.Full:
                            txtOut.Write(@"\qj ");
                            break;
                        default:
                            Debug.Assert(false, "Unhandled justification mode: " + justMode);
                            break;
                    }
                    break;
                case AnnoType.LeftMargin:
                    float leftMargin = (float)anno.Data!;
                    txtOut.Write(@"\li" + (int)Math.Round(TWIPS_PER_INCH * leftMargin) + " ");
                    break;
                case AnnoType.RightMargin:
                    float rightMargin = (float)anno.Data!;
                    txtOut.Write(@"\ri" + (int)Math.Round(TWIPS_PER_INCH * rightMargin) + " ");
                    break;
                case AnnoType.FontFamily:
                    int familyIndex = ClosestFontFamily((FontFamily)anno.Data!);
                    txtOut.Write(@"\f" + familyIndex + " ");
                    break;
                case AnnoType.FontSize:
                    // RTF takes the font size in half-points.
                    txtOut.Write(@"\fs" + ((int)anno.Data! * 2) + " ");
                    break;
                case AnnoType.Bold:
                    if ((bool)anno.Data!) {
                        txtOut.Write(@"\b ");
                    } else {
                        txtOut.Write(@"\b0 ");
                    }
                    break;
                case AnnoType.Italic:
                    if ((bool)anno.Data!) {
                        txtOut.Write(@"\i ");
                    } else {
                        txtOut.Write(@"\i0 ");
                    }
                    break;
                case AnnoType.Underline:
                    if ((bool)anno.Data!) {
                        txtOut.Write(@"\ul ");
                    } else {
                        // Can do dotted, dash, or double underlines as well.
                        txtOut.Write(@"\ul0 ");     // or \ulnone?
                    }
                    break;
                case AnnoType.Outline:
                    if ((bool)anno.Data!) {
                        txtOut.Write(@"\outl ");
                    } else {
                        txtOut.Write(@"\outl0 ");
                    }
                    break;
                case AnnoType.Shadow:
                    if ((bool)anno.Data!) {
                        txtOut.Write(@"\shad ");
                    } else {
                        txtOut.Write(@"\shad0 ");
                    }
                    break;
                case AnnoType.Superscript:
                    if ((bool)anno.Data!) {
                        txtOut.Write(@"\super ");
                    } else {
                        txtOut.Write(@"\nosupersub ");
                        // TODO: if subscript is enabled, re-enable it
                    }
                    break;
                case AnnoType.Subscript:
                    if ((bool)anno.Data!) {
                        txtOut.Write(@"\sub ");
                    } else {
                        txtOut.Write(@"\nosupersub ");
                        // TODO: if superscript is enabled, re-enable it
                    }
                    break;

                case AnnoType.ForeColor:
                    // Set foreground color, with color index.
                    int foreIndex = ClosestColorIndex((int)anno.Data!) + 1;
                    if (foreIndex == COLOR_INDEX_BLACK) {
                        // Black foreground; just reset to default.
                        txtOut.Write(@"\cf0 ");
                    } else {
                        txtOut.Write(@"\cf" + foreIndex + " ");
                    }
                    break;
                case AnnoType.BackColor:
                    // 2007 RTF spec says "Windows versions of Word have never supported" \cbN
                    // for background color.  Word uses \chcbpatN.  The only option for RichTextBox
                    // seems to be \highlightN, which appears to use a value from a fixed color
                    // table in the v1.5 spec, but we're using the same colors so it's fine
                    // whether it's fixed or based on our index.
                    int backIndex = ClosestColorIndex((int)anno.Data!) + 1;
                    //txtOut.Write(@"\chcbpat" + backIndex + " ");
                    if (backIndex == COLOR_INDEX_WHITE) {
                        // White background.  Special-case this to disable highlighting.
                        txtOut.Write(@"\highlight0 ");
                    } else {
                        txtOut.Write(@"\highlight" + backIndex + " ");
                    }
                    break;
                default:
                    Debug.Assert(false, "Unhandled annotation type: " + anno.Type);
                    break;
            }
        }

        /// <summary>
        /// Returns the index of the font that most closely matches the requested family.
        /// </summary>
        /// <param name="family">Font family.</param>
        /// <returns>Document font index.</returns>
        private static int ClosestFontFamily(FontFamily family) {
            FontIndex index;
            if (family.Name.ToLowerInvariant() == "symbol") {
                index = FontIndex.Symbol;
            } else if (family.IsMono) {
                if (family.IsSerif) {
                    index = FontIndex.CourierNew;
                } else {
                    index = FontIndex.Consolas;
                }
            } else {
                if (family.IsSerif) {
                    index = FontIndex.TimesNewRoman;
                } else {
                    index = FontIndex.Arial;
                }
            }
            Debug.WriteLine("RTF font " + family + " --> " + index);
            return (int)index;
        }

        /// <summary>
        /// Finds the color in the table that most closely matches the RGB color value.
        /// </summary>
        /// <param name="rgbColor">RGB color, as 0xFFrrggbb.</param>
        /// <returns>Color table index.</returns>
        private static int ClosestColorIndex(int rgbColor) {
            // Use a simple "RGB color distance" approach.  A more sophisticated solution would
            // use HSL/HSV to emphasize hue.  cf. http://en.wikipedia.org/wiki/Color_difference
            int srcRed = (rgbColor >> 16) & 0xff;
            int srcGreen = (rgbColor >> 8) & 0xff;
            int srcBlue = rgbColor & 0xff;

            int bestDiff = int.MaxValue;
            int bestIndex = -1;
            for (int i = 0; i < sColors.Length; i++) {
                int dstColor = sColors[i];
                int redDiff = srcRed - ((dstColor >> 16) & 0xff);
                int greenDiff = srcGreen - ((dstColor >> 8) & 0xff);
                int blueDiff = srcBlue - (dstColor & 0xff);
                int colorDiff = redDiff * redDiff + greenDiff * greenDiff + blueDiff * blueDiff;
                if (colorDiff == 0) {
                    // perfect match
                    return i;
                }
                if (colorDiff < bestDiff) {
                    bestDiff = colorDiff;
                    bestIndex = i;
                }
            }
            Debug.Assert(bestIndex >= 0);
            return bestIndex;
        }
    }
}
