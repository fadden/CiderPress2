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
using System.Diagnostics;

using CommonUtil;
using DiskArc;

namespace FileConv.Gfx {
    /// <summary>
    /// Converts a QuickDraw bitmap font to a bitmap, as a character grid or sample text.
    /// </summary>
    public class BitmapFont : Converter {
        public const string TAG = "qdfont";
        public const string LABEL = "QuickDraw II Font";
        public const string DESCRIPTION = "Converts a QuickDraw II font file to a bitmap.";
        public const string DISCRIMINATOR = "ProDOS FON with auxtype $0000.";
        public override string Tag => TAG;
        public override string Label => LABEL;
        public override string Description => DESCRIPTION;
        public override string Discriminator => DISCRIMINATOR;

        /// <summary>
        /// Font rendering palette.  Light gray background that provides a frame, dark gray
        /// background for character cells, black-on-white characters.
        /// </summary>
        private static readonly Palette8 FONT_PALETTE = new Palette8("Font",
            new int[] {
                ConvUtil.MakeRGB(0x80, 0x80, 0x80),     // 0=dark gray (frame around cells)
                ConvUtil.MakeRGB(0xe0, 0xe0, 0xe0),     // 1=light gray (cell background)
                ConvUtil.MakeRGB(0xff, 0xff, 0xff),     // 2=white (glyph background)
                ConvUtil.MakeRGB(0x00, 0x00, 0x00),     // 3=black (glyph foreground)
            });
        private const int COLOR_FRAME = 0;
        private const int COLOR_CELL_BG = 1;
        private const int COLOR_CHAR_BG = 2;
        private const int COLOR_CHAR_FG = 3;

        private class GSFontHeader {
            // This is from the file header, and is not actually part of the font header.
            public string FontFamilyName { get; private set; } = string.Empty;

            // Font header fields.
            public short OffsetToMF { get; private set; }
            public short Family { get; private set; }
            public short Style { get; private set; }
            public short Size { get; private set; }
            public short Version { get; private set; }
            public short FbrExtent { get; private set; }
            public byte[] ExtraData { get; private set; } = RawData.EMPTY_BYTE_ARRAY;

            private const int BASE_LENGTH = 6 * 2;

            [Flags]
            public enum Styles {
                None = 0x00,
                Bold = 0x01, Italic = 0x02, Underline = 0x04, Outline = 0x08, Shadow = 0x10
            }

            public bool Load(byte[] buf, ref int offset) {
                byte strLen = buf[offset++];
                if (offset + strLen > buf.Length) {
                    return false;
                }
                // The tech note doesn't say whether it's ASCII or MOR.  Handle MOR to be on
                // the safe side.
                FontFamilyName = MacChar.MacToUnicode(buf, offset, strLen, MacChar.Encoding.Roman);
                offset += strLen;

                int startOffset = offset;
                OffsetToMF = (short)RawData.ReadU16LE(buf, ref offset);
                Family = (short)RawData.ReadU16LE(buf, ref offset);
                Style = (short)RawData.ReadU16LE(buf, ref offset);
                Size = (short)RawData.ReadU16LE(buf, ref offset);
                Version = (short)RawData.ReadU16LE(buf, ref offset);
                FbrExtent = (short)RawData.ReadU16LE(buf, ref offset);
                Debug.Assert(offset - startOffset == BASE_LENGTH);
                int extraLen = OffsetToMF * 2 - BASE_LENGTH;
                if (extraLen < 0) {
                    Debug.WriteLine("Warning: invalid OffsetToMF " + OffsetToMF);
                    // keep going
                } else if (OffsetToMF > BASE_LENGTH) {
                    ExtraData = new byte[extraLen];
                    Array.Copy(buf, offset, ExtraData, 0, extraLen);
                }
                return true;
            }
        }

        private class MacFontHeader {
            // Macintosh font header fields.  On the Mac these are stored big-endian, on the
            // Apple IIgs they're little-endian.
            public short FontType { get; private set; }
            public short FirstChar { get; private set; }
            public short LastChar { get; private set; }
            public short WidMax { get; private set; }
            public short KernMax { get; private set; }
            public short NDescent { get; private set; }
            public short FRectWidth { get; private set; }
            public short FRectHeight { get; private set; }
            public short OWTLoc { get; private set; }
            public short Ascent { get; private set; }
            public short Descent { get; private set; }
            public short Leading { get; private set; }
            public short RowWords { get; private set; }

            public ushort[] BitImage { get; private set; } = RawData.EMPTY_USHORT_ARRAY;
            public ushort[] LocTable { get; private set; } = RawData.EMPTY_USHORT_ARRAY;
            public ushort[] OWTable { get; private set; } = RawData.EMPTY_USHORT_ARRAY;

            private const int BASE_LENGTH = 13 * 2;

            public bool Load(byte[] buf, ref int offset) {
                int startOffset = offset;
                FontType = (short)RawData.ReadU16LE(buf, ref offset);
                FirstChar = (short)RawData.ReadU16LE(buf, ref offset);
                LastChar = (short)RawData.ReadU16LE(buf, ref offset);
                WidMax = (short)RawData.ReadU16LE(buf, ref offset);
                KernMax = (short)RawData.ReadU16LE(buf, ref offset);
                NDescent = (short)RawData.ReadU16LE(buf, ref offset);
                FRectWidth = (short)RawData.ReadU16LE(buf, ref offset);
                FRectHeight = (short)RawData.ReadU16LE(buf, ref offset);
                OWTLoc = (short)RawData.ReadU16LE(buf, ref offset);
                Ascent = (short)RawData.ReadU16LE(buf, ref offset);
                Descent = (short)RawData.ReadU16LE(buf, ref offset);
                Leading = (short)RawData.ReadU16LE(buf, ref offset);
                RowWords = (short)RawData.ReadU16LE(buf, ref offset);
                Debug.Assert(offset - startOffset == BASE_LENGTH);

                if (LastChar < 0 || LastChar > 255 || FirstChar > LastChar) {
                    Debug.WriteLine("Error: bad first/last char " + FirstChar + " / " + LastChar);
                    return false;
                }
                if (FRectWidth <= 0 || FRectHeight <= 0 || RowWords <= 0) {
                    Debug.WriteLine("Error: bad strike width/height/words " +
                        FRectWidth + " / " + FRectHeight + " / " + RowWords);
                    return false;
                }
                if (WidMax <= 0) {
                    Debug.WriteLine("Warning: WidMax=" + WidMax);
                    // keep going
                }
                if (FRectHeight != Ascent + Descent) {
                    Debug.WriteLine("Warning: ascent " + Ascent + " + descent " + Descent +
                        " != fRectHeight " + FRectHeight);
                    // keep going?
                }

                int bitImageWordLen = RowWords * FRectHeight;
                int charCount = LastChar - FirstChar + 3;
                int expectedLen = (bitImageWordLen + charCount * 2) * 2;

                if (offset + expectedLen > buf.Length) {
                    Debug.WriteLine("Error: expectedLen is " + expectedLen +
                        ", offset=" + offset + ", bufLen=" + buf.Length);
                    return false;
                } else if (offset + expectedLen != buf.Length) {
                    Debug.WriteLine("Note: excess bytes at end of file: " +
                        (buf.Length - offset - expectedLen));
                    // keep going
                }

                // Parameters look okay, load the arrays.
                BitImage = new ushort[bitImageWordLen];
                for (int i = 0; i < bitImageWordLen; i++) {
                    // NOTE: big-endian order
                    BitImage[i] = RawData.ReadU16BE(buf, ref offset);
                }
                LocTable = new ushort[charCount];
                for (int i = 0; i < charCount; i++) {
                    LocTable[i] = RawData.ReadU16LE(buf, ref offset);
                }
                OWTable = new ushort[charCount];
                for (int i = 0; i < charCount; i++) {
                    OWTable[i] = RawData.ReadU16LE(buf, ref offset);
                }

                Debug.Assert(offset - startOffset <= BASE_LENGTH + expectedLen);    // overrun?
                return true;
            }

            /// <summary>
            /// Returns true if the specified character is represented in the font.
            /// </summary>
            public bool HasChar(int ch) {
                if (ch < FirstChar || ch > LastChar + 1) {
                    return false;
                }
                return (OWTable[ch - FirstChar] != 0xffff);
            }

            /// <summary>
            /// Returns the character offset of the specified character.  Add this to KernMax to
            /// get the starting pen position.  This can be larger than KernMax if we want to
            /// leave blank space on the left side.
            /// </summary>
            public int GetOffset(int ch) {
                if (ch < FirstChar || ch > LastChar + 1) {
                    return -1;
                }
                return OWTable[ch - FirstChar] >> 8;
            }

            /// <summary>
            /// Returns the character width of the specified character.  This is how far the
            /// pen position should be advanced after drawing the glyph.
            /// </summary>
            public int GetWidth(int ch) {
                if (ch < FirstChar || ch > LastChar + 1) {
                    return -1;
                }
                return OWTable[ch - FirstChar] & 0xff;
            }

            /// <summary>
            /// Returns the horizontal offset, in pixels, of the start of the glyph in the
            /// font strike.
            /// </summary>
            /// <remarks>
            /// The width of the glyph can be determined by getting the horizontal offset of
            /// the following character.  Missing characters are required to hold the location
            /// of the next valid character.  The missing character glyph is stored at the end,
            /// so there will always be a next valid character.
            /// </remarks>
            public int GetLocation(int ch) {
                if (ch < FirstChar || ch > LastChar + 2) {
                    return -1;
                }
                return LocTable[ch - FirstChar];
            }
        }


        private BitmapFont() { }

        public BitmapFont(FileAttribs attrs, Stream? dataStream, Stream? rsrcStream,
                ResourceMgr? resMgr, ConvFlags convFlags, AppHook appHook)
                : base(attrs, dataStream, rsrcStream, resMgr, convFlags, appHook) {
            Applic = TestApplicability();
        }

        protected override Applicability TestApplicability() {
            if (DataStream == null || IsRawDOS) {
                return Applicability.Not;
            }
            if (FileAttrs.FileType == FileAttribs.FILE_TYPE_FON) {
                if (FileAttrs.AuxType == 0x0000) {
                    return Applicability.Yes;
                } else if (FileAttrs.AuxType == 0x0001) {
                    return Applicability.Not;       // TrueType
                } else {
                    // FON files with auxtypes $0006, $0016, $0026 have been seen.  They
                    // appear to be in the standard format.  Unsure about others.
                    return Applicability.Maybe;
                }
            }
            return Applicability.Not;
        }

        public override Type GetExpectedType(Dictionary<string, string> options) {
            return typeof(IBitmap);
        }

        public override IConvOutput ConvertFile(Dictionary<string, string> options) {
            if (Applic <= Applicability.Not) {
                Debug.Assert(false);
                return new ErrorText("ERROR");
            }
            Debug.Assert(DataStream != null);

            byte[] fullBuf = new byte[DataStream.Length];
            DataStream.Position = 0;
            DataStream.ReadExactly(fullBuf, 0, (int)DataStream.Length);

            int offset = 0;
            GSFontHeader gsHeader = new GSFontHeader();
            if (!gsHeader.Load(fullBuf, ref offset)) {
                return new ErrorText("Invalid GS font header");
            }
            MacFontHeader macHeader = new MacFontHeader();
            if (!macHeader.Load(fullBuf, ref offset)) {
                return new ErrorText("Invalid Mac font header");
            }

            Bitmap8 output = new Bitmap8(macHeader.RowWords * 16, macHeader.FRectHeight);
            output.SetPalette(FONT_PALETTE);
            AddFontNotes(gsHeader, macHeader, output.Notes);
            CheckFontMetrics(gsHeader, macHeader, output.Notes);
            DrawFontStrike(output, macHeader);

            //output.DrawChar('Z', 1, 1, COLOR_CHAR_FG, COLOR_CHAR_BG);
            return output;
        }

        /// <summary>
        /// Calculates the Font Bounds Rectangle and other metrics.  The computed values are
        /// compared to the stored values.
        /// </summary>
        /// <param name="gsHeader">GS font header.</param>
        /// <param name="macHeader">Mac font header.</param>
        /// <param name="notes">Notes holder.</param>
        /// <returns>Calculated width.</returns>
        private static void CheckFontMetrics(GSFontHeader gsHeader, MacFontHeader macHeader,
                Notes notes) {
            int charWidMax = 0, fbrLeftExtent = 0, fbrRightExtent = 0;
            int fontLeft = 0, fontRight = 0;
            int imageWidthMax = 0/*, offsetMax = 0*/;

            // Iterate over all characters, including the missing character glyph.
            for (int ch = macHeader.FirstChar; ch <= macHeader.LastChar + 1; ch++) {
                if (!macHeader.HasChar(ch)) {
                    continue;
                }

                // Character width: distance to advance the pen.  Does not include pixels
                //   drawn to the left of the origin (leftward kern).  Does include blank
                //   pixels to right right of the image.  This will be nonzero for spaces (which
                //   have zero image width) and zero for accent characters like umlauts (which
                //   are intended to overlap other characters; sometimes called "dead characters").
                //   The maximum character width value is stored in widMax.
                // Image width: width of glyph.  Does not include blank space or care about
                //   kerning.  Will be zero for space characters.
                // While the widths are variable, the height is constant across the entire font.

                // Font rectangle: a rectangle that can contain all possible glyphs when drawn
                //   from a common origin.  Based on image width, but must take kerning into
                //   account to set the origin (which can make the cumulative rect width wider
                //   than the maximum image width).  Stored in fRectWidth / fRectHeight.  Does not
                //   use the character width.
                // Character rectangle: bounds of a single glyph, based on image width.
                // Character bounds rectangle: like character rectangle, but uses the character
                //   width instead of the image width.
                // Font bounds rectangle: like the font rectangle, but uses the character width.
                //   The union of all character bounds rectangles.  Stored in fbrExtent.

                int charWidth = macHeader.GetWidth(ch);
                if (charWidth > charWidMax) {
                    charWidMax = charWidth;
                }

                int location = macHeader.GetLocation(ch);
                int nextLoc = macHeader.GetLocation(ch + 1);
                int imageWidth = nextLoc - location;
                if (imageWidth > imageWidthMax) {
                    imageWidthMax = imageWidth;
                }

                int charOffset = macHeader.GetOffset(ch);
                //if (charOffset > offsetMax) {
                //    offsetMax = charOffset;
                //}
                int origin = macHeader.KernMax + charOffset;

                //notes.AddI("C=$" + ch.ToString("x2") +
                //    " off=" + charOffset +
                //    " iw=" + imageWidth +
                //    " -> " + (charOffset + imageWidth) +
                //    " cw=" + charWidth
                //    );

                // Update left/right edges for fRectWidth.
                if (origin < fontLeft) {
                    fontLeft = origin;
                }
                if (origin + imageWidth > fontRight) {
                    fontRight = origin + imageWidth;
                }

                // Update left/right edges for fbrExtent.
                if (origin < fbrLeftExtent) {
                    fbrLeftExtent = origin;
                }
                if (charWidth > fbrRightExtent) {
                    fbrRightExtent = charWidth;
                    // should this be Max(charWidth, imageWidth) ?
                }
            }
            // "It would seem that fbrLeftExtent == -kernMax. [...] If kernMax is positive (that
            // is, if every character image starts at least 1 pixel to the right of the character
            // origin) then fbrLeftExtent is 0."
            if (fbrLeftExtent > 0) {
                fbrLeftExtent = 0;
            } else {
                fbrLeftExtent = -fbrLeftExtent;
            }

            // Something is wrong with the way I'm calculating this.  Or lots of fonts are wrong.
            int calcRectWidth = -fontLeft + fontRight;

            // "The fbrExtent value is the farthest possible horizontal distance from the pen
            // location to the far edge of any pixel that can be altered by drawing any character
            // in the font."  This includes foreground and background pixels.
            //
            // "Finally, we define fbrExtent to be the maximum of fbrLeftExtent and
            // fbrRightExtent."  I'm not sure why fbrLeftExtent is involved, though it might
            // have something to do with right-to-left fonts.
            //
            // fbrExtent is usually the same as widMax, but if the font kerns right it can be
            // larger.
            int fbrExtent = Math.Max(fbrLeftExtent, fbrRightExtent);

            if (charWidMax != macHeader.WidMax) {
                notes.AddW("Value for widMax (" + macHeader.WidMax +
                    ") does not match calculated value (" + charWidMax + ")");
            }
            if (calcRectWidth != macHeader.FRectWidth) {
                notes.AddW("Value for fRectWidth (" + macHeader.FRectWidth +
                    ") does not match calculated value (" + calcRectWidth +
                    "); l=" + fontLeft + " r=" + fontRight +
                    ", max image width is " + imageWidthMax);
            }
            if (fbrExtent != gsHeader.FbrExtent) {
                notes.AddW("Value for fbrExtent (" + gsHeader.FbrExtent +
                    ") does not match calculated value (" + fbrExtent + ")");
            }
            if (macHeader.FRectHeight != macHeader.Ascent + macHeader.Descent) {
                notes.AddW("Font rect height (" + macHeader.FRectHeight +
                    ") does not equal ascent (" + macHeader.Ascent + ") + descent (" +
                    macHeader.Descent + ")");
            }
        }

        /// <summary>
        /// Generates some notes from the font header values.
        /// </summary>
        private static void AddFontNotes(GSFontHeader gsHeader, MacFontHeader macHeader,
                Notes notes) {
            string styleStr = string.Empty;
            GSFontHeader.Styles fontStyle = (GSFontHeader.Styles)gsHeader.Style;
            if (fontStyle == GSFontHeader.Styles.None) {
                styleStr = "none";
            } else {
                foreach (GSFontHeader.Styles style in Enum.GetValues<GSFontHeader.Styles>()) {
                    if ((fontStyle & style) != 0) {
                        if (styleStr.Length != 0) {
                            styleStr += ' ';
                        }
                        styleStr += style.ToString();
                        fontStyle &= style;
                    }
                }
                if (fontStyle != 0) {
                    styleStr += "+ " + (int)fontStyle;
                }
            }
            notes.AddI("Family #" +
                (gsHeader.Family < 0 ?
                    "$" + ((ushort)gsHeader.Family).ToString("x4") : gsHeader.Family) +
                " \"" + gsHeader.FontFamilyName + "\", " + " " + gsHeader.Size +
                " pts, styles: " + styleStr);
            notes.AddI("File format v" + (gsHeader.Version >> 8) + "." +
                (gsHeader.Version & 0xff) + ", fbrExtent=" + gsHeader.FbrExtent +
                ", offsetToMF=" + gsHeader.OffsetToMF);
            notes.AddI("Font type=$" + macHeader.FontType.ToString("x4") + ", firstChar=" +
                macHeader.FirstChar + ", lastChar=" + macHeader.LastChar);
            notes.AddI("widMax=" + macHeader.WidMax + ", kernMax=" + macHeader.KernMax +
                ", ascent=" + macHeader.Ascent + ", descent=" + macHeader.Descent +
                ", nDescent=" + macHeader.NDescent + ", leading=" + macHeader.Leading);
            notes.AddI("fRectWidth=" + macHeader.FRectWidth + ", fRectHeight=" +
                macHeader.FRectHeight + ", rowWords=" + macHeader.RowWords + ", owTLoc=" +
                macHeader.OWTLoc);
        }

        /// <summary>
        /// Renders the font strike into a bitmap.
        /// </summary>
        private static void DrawFontStrike(Bitmap8 output, MacFontHeader macHeader) {
            for (int row = 0; row < macHeader.FRectHeight; row++) {
                for (int col16 = 0; col16 < macHeader.RowWords; col16++) {
                    ushort word = macHeader.BitImage[row * macHeader.RowWords + col16];
                    ushort mask = 0x8000;
                    for (int bit = 0; bit < 16; bit++) {
                        if ((word & mask) != 0) {
                            output.SetPixelIndex(col16 * 16 + bit, row, COLOR_CHAR_FG);
                        } else {
                            output.SetPixelIndex(col16 * 16 + bit, row, COLOR_CHAR_BG);
                        }
                        mask >>= 1;
                    }
                }
            }
        }
    }
}
