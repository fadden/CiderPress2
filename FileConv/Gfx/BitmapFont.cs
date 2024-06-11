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
    /// <remarks>
    /// <para>In theory this works for Mac fonts, though those are stored as a collection of
    /// resources.</para>
    /// TODO: figure out if Mac fonts were commonly distributed individually in single files.  If
    /// there's only one font in the resource fork, we can display it.  The default converter
    /// should still be "resources", but this could be allowed as a "maybe" conversion when
    /// we detect FONT/NFNT resources.
    /// </remarks>
    public class BitmapFont : Converter {
        public const string TAG = "qdfont";
        public const string LABEL = "QuickDraw II Font";
        public const string DESCRIPTION =
            "Converts a QuickDraw II bitmap font file to a bitmap, either as a grid with all " +
            "possible glyphs, or a sample string drawn with the font.";
        public const string DISCRIMINATOR = "ProDOS FON with auxtype $0000.";
        public override string Tag => TAG;
        public override string Label => LABEL;
        public override string Description => DESCRIPTION;
        public override string Discriminator => DISCRIMINATOR;

        public const string OPT_MODE = "mode";

        public const string MODE_GRID = "grid";
        public const string MODE_SAMPLE = "sample";
        public static readonly string[] ModeTags = new string[] {
            MODE_SAMPLE, MODE_GRID
        };
        public static readonly string[] ModeDescrs = new string[] {
            "Sample string", "Grid of glyphs"
        };
        internal enum ConvMode {
            Unknown = 0, Sample, Grid
        }

        public override List<OptionDefinition> OptionDefs { get; protected set; } =
            new List<OptionDefinition>() {
                new OptionDefinition(OPT_MODE, "Conversion Mode",
                    OptionDefinition.OptType.Multi, MODE_SAMPLE, ModeTags, ModeDescrs),
        };

        /// <summary>
        /// Font rendering palette.  Black-on-white characters, various others for the grid
        /// labels and framing.
        /// </summary>
        private static readonly Palette8 FONT_PALETTE = new Palette8("Font",
            new int[] {
                ConvUtil.MakeRGB(0xe0, 0xe0, 0xe0),     // 0=light gray (cell background)
                ConvUtil.MakeRGB(0x80, 0x80, 0x80),     // 1=dark gray (frame around cells)
                ConvUtil.MakeRGB(0xff, 0xff, 0xff),     // 2=white (glyph background)
                ConvUtil.MakeRGB(0x00, 0x00, 0x00),     // 3=black (glyph foreground)
                ConvUtil.MakeRGB(0x30, 0x30, 0xe0),     // 4=blue (hex labels)
                ConvUtil.MakeRGB(0xb0, 0x30, 0x30),     // 5=red (char not in font)
            });
        private const int COLOR_CELL_BG = 0;
        private const int COLOR_FRAME = 1;
        private const int COLOR_GLYPH_BG = 2;
        private const int COLOR_GLYPH_FG = 3;
        private const int COLOR_LABEL = 4;
        private const int COLOR_NO_CHAR = 5;

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

            public bool Load(byte[] buf, ref int offset, Notes notes) {
                byte strLen = buf[offset++];
                if (offset + strLen > buf.Length) {
                    notes.AddE("Error: family name string is longer than font file");
                    return false;
                }
                // The tech note doesn't say whether it's ASCII or MOR.  Handle MOR to be on
                // the safe side.
                FontFamilyName = MacChar.MacToUnicode(buf, offset, strLen, MacChar.Encoding.Roman);
                offset += strLen;

                if (offset + BASE_LENGTH > buf.Length) {
                    notes.AddE("Error: ran out of file while reading GS header");
                    return false;
                }

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
                    notes.AddW("Warning: invalid OffsetToMF " + OffsetToMF);
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
            public char FirstChar { get; private set; }
            public char LastChar { get; private set; }
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

            public bool Load(byte[] buf, ref int offset, Notes notes) {
                if (offset + BASE_LENGTH > buf.Length) {
                    notes.AddE("Error: ran out of file while reading Mac header");
                    return false;
                }

                int startOffset = offset;
                FontType = (short)RawData.ReadU16LE(buf, ref offset);
                FirstChar = (char)RawData.ReadU16LE(buf, ref offset);
                LastChar = (char)RawData.ReadU16LE(buf, ref offset);
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
                    notes.AddE("Error: bad first/last char " + FirstChar + " / " + LastChar);
                    return false;
                }
                if (FRectWidth <= 0 || FRectHeight <= 0 || RowWords <= 0) {
                    notes.AddE("Error: bad strike width/height/words " +
                        FRectWidth + " / " + FRectHeight + " / " + RowWords);
                    return false;
                }
                if (WidMax <= 0) {
                    notes.AddW("Warning: WidMax=" + WidMax);
                    // keep going
                }
                if (FRectHeight != Ascent + Descent) {
                    notes.AddW("Warning: ascent " + Ascent + " + descent " + Descent +
                        " != fRectHeight " + FRectHeight);
                    // keep going?
                }

                int bitImageWordLen = RowWords * FRectHeight;
                int charCount = LastChar - FirstChar + 3;
                int expectedLen = (bitImageWordLen + charCount * 2) * 2;

                if (offset + expectedLen > buf.Length) {
                    notes.AddE("Error: file must be " + (offset + expectedLen) +
                        " bytes long, but is only " + buf.Length);
                    return false;
                } else if (offset + expectedLen != buf.Length) {
                    notes.AddW("Note: excess bytes at end of file: " +
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

                // Validate table contents.
                int strikeWidth = RowWords * 16;
                for (int ch = 0; ch < charCount; ch++) {
                    if (!HasChar(ch)) {
                        if (ch == charCount) {
                            // The last entry holds the glyph for the missing character, and is
                            // required to exist.
                            notes.AddE("Error: the 'missing character' glyph is undefined");
                            return false;
                        }
                        continue;
                    }
                    int imageWidth = GetImageWidth(ch);
                    if (imageWidth == 0) {
                        // Nothing to draw, don't try to validate pixel data location.
                        continue;
                    }

                    // Confirm that the glyph image is actually in the table.  Instead of
                    // rejecting the entire font, we can just mark it as missing, unless
                    // it's at the very end.
                    int loc = GetLocation(ch);
                    if (loc >= strikeWidth) {
                        notes.AddW("Warning: location of index " + ch + " is " + loc +
                            " (strike width=" + strikeWidth + ")");
                        return false;
                    } else if (loc + imageWidth > strikeWidth) {
                        notes.AddW("Warning: index " + ch + " extends off end: loc=" + loc +
                            " imageWidth=" + imageWidth + " strikeWidth=" + strikeWidth);
                        return false;
                    }
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
            public int GetCharWidth(int ch) {
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

            /// <summary>
            /// Returns the image width of the specified character.  This is how many pixels wide
            /// the glyph is in the font strike.
            /// </summary>
            public int GetImageWidth(int ch) {
                if (ch < FirstChar || ch > LastChar + 1) {
                    return -1;
                }
                return LocTable[ch - FirstChar + 1] - LocTable[ch - FirstChar];
            }
        }


        private BitmapFont() { }

        public BitmapFont(FileAttribs attrs, Stream? dataStream, Stream? rsrcStream,
                ResourceMgr? resMgr, ConvFlags convFlags, AppHook appHook)
                : base(attrs, dataStream, rsrcStream, resMgr, convFlags, appHook) {
            Applic = TestApplicability();
        }

        protected override Applicability TestApplicability() {
            if (DataStream == null) {
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

            Notes tmpNotes = new Notes();
            int offset = 0;
            GSFontHeader gsHeader = new GSFontHeader();
            if (!gsHeader.Load(fullBuf, ref offset, tmpNotes)) {
                return new ErrorText("Invalid GS font header", tmpNotes);
            }
            MacFontHeader macHeader = new MacFontHeader();
            if (!macHeader.Load(fullBuf, ref offset, tmpNotes)) {
                return new ErrorText("Invalid Mac font header", tmpNotes);
            }

            AddFontNotes(gsHeader, macHeader, tmpNotes);
            int cellWidth = CalculateFontMetrics(gsHeader, macHeader, tmpNotes);

            //Bitmap8 output = new Bitmap8(macHeader.RowWords * 16, macHeader.FRectHeight);
            //output.SetPalette(FONT_PALETTE);
            //output.Notes.MergeFrom(tmpNotes);
            //DrawFontStrike(output, macHeader);
            //return output;

            string modeStr = GetStringOption(options, OPT_MODE, MODE_SAMPLE);
            switch (modeStr) {
                case MODE_GRID:
                    return RenderFontGrid(gsHeader, macHeader, cellWidth, tmpNotes);
                case MODE_SAMPLE:
                default:
                    return RenderFontSample(gsHeader, macHeader, tmpNotes);
            }
        }

        /// <summary>
        /// Calculates the Font Bounds Rectangle and other metrics.  The computed values are
        /// compared to the stored values, and warnings added to the Notes as needed.
        /// </summary>
        /// <param name="gsHeader">GS font header.</param>
        /// <param name="macHeader">Mac font header.</param>
        /// <param name="notes">Notes holder.</param>
        /// <returns>Maximum character cell width.</returns>
        private static int CalculateFontMetrics(GSFontHeader gsHeader, MacFontHeader macHeader,
                Notes notes) {
            int charWidMax = 0, fbrLeftExtent = 0, fbrRightExtent = 0;
            int fontLeft = 0, fontRight = 0;
            int imageWidthMax = 0/*, offsetMax = 0*/;
            int cellLeft = 0, cellRight = 0;

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

                // This function computes the "character cell width", which uses the maximum
                // values for the image width and character width to define a cell width that
                // will contain any glyph's foreground and background pixels, including kerning.

                int charWidth = macHeader.GetCharWidth(ch);
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

                // I strongly suspect stuff here will break if kernMax is positive.  I've never
                // actually found such a font though.
                int origin = macHeader.KernMax + charOffset;

                // Update left/right edges for fRectWidth.  This is based on the image width,
                // so don't do this when imageWidth==0 (it'll probably have a zero origin,
                // which will give us the wrong idea about the left edge).
                if (imageWidth != 0) {
                    if (origin < fontLeft) {
                        fontLeft = origin;
                    }
                    if (origin + imageWidth > fontRight) {
                        fontRight = origin + imageWidth;
                    }
                }

                // Update left/right edges for fbrExtent.
                if (origin < fbrLeftExtent) {
                    fbrLeftExtent = origin;
                }
                if (charWidth > fbrRightExtent) {
                    fbrRightExtent = charWidth;
                    // should this be Max(charWidth, imageWidth) ?
                }

                // Update our internal cell width calculation.
                int minLeft = Math.Min(fontLeft, fbrLeftExtent);
                if (minLeft < cellLeft) {
                    cellLeft = minLeft;
                }
                int maxRight = Math.Max(fontRight, fbrRightExtent);
                if (maxRight > cellRight) {
                    cellRight = maxRight;
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

            // The width of a cell that can hold a character is equal to the maximum kern plus
            // the rightward extent.  It doesn't matter whether any glyph actually kerns that
            // far to the left.  We need to size the cell for the font metrics.
            //int cellWidth = -cellLeft + cellRight;
            int cellWidth = -macHeader.KernMax + cellRight;

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

            notes.AddI("Calculated cellWidth l=" + cellLeft + " r=" + cellRight +
                " -> " + cellWidth);
            if (calcRectWidth != macHeader.FRectWidth) {
                notes.AddW("Value for fRectWidth (" + macHeader.FRectWidth +
                    ") does not match calculated value (" + calcRectWidth +
                    "); l=" + fontLeft + " r=" + fontRight +
                    ", max image width is " + imageWidthMax);
            }
            if (charWidMax != macHeader.WidMax) {
                notes.AddW("Value for widMax (" + macHeader.WidMax +
                    ") does not match calculated value (" + charWidMax + ")");
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

            return cellWidth;
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
                " \"" + gsHeader.FontFamilyName + "\", " + gsHeader.Size +
                " pts, styles: " + styleStr);
            notes.AddI("File format v" + (gsHeader.Version >> 8) + "." +
                (gsHeader.Version & 0xff) + ", fbrExtent=" + gsHeader.FbrExtent +
                ", offsetToMF=" + gsHeader.OffsetToMF);
            notes.AddI("Font type=$" + macHeader.FontType.ToString("x4") +
                ", firstChar=$" + ((ushort)macHeader.FirstChar).ToString("x2") +
                ", lastChar=$" + ((ushort)macHeader.LastChar).ToString("x2"));
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
                            output.SetPixelIndex(col16 * 16 + bit, row, COLOR_GLYPH_FG);
                        } else {
                            output.SetPixelIndex(col16 * 16 + bit, row, COLOR_GLYPH_BG);
                        }
                        mask >>= 1;
                    }
                }
            }
        }

        // Pixel draw delegate, so we can render glyphs on Bitmaps and ImageGrids with one func.
        private delegate void SetPixelFunc(int xc, int yc, byte color);

        /// <summary>
        /// Generates a font rendering in grid form.
        /// </summary>
        /// <param name="gsHeader">IIgs font header.</param>
        /// <param name="macHeader">Macintosh font header.</param>
        /// <param name="cellWidth">Calculated maximum cell width.</param>
        /// <param name="tmpNotes">Notes generated during calculations.</param>
        /// <returns>Bitmap with grid.</returns>
        private static IConvOutput RenderFontGrid(GSFontHeader gsHeader, MacFontHeader macHeader,
                int cellWidth, Notes tmpNotes) {
            ImageGrid.Builder bob = new ImageGrid.Builder();
            bob.SetGeometry(cellWidth, macHeader.FRectHeight, 16);
            bob.SetRange(macHeader.FirstChar, macHeader.LastChar - macHeader.FirstChar + 2);
            bob.SetColors(FONT_PALETTE, COLOR_LABEL, COLOR_CELL_BG, COLOR_FRAME, COLOR_FRAME);
            bob.SetLabels(hasLeftLabels: true, hasTopLabels: true, leftLabelIsRow: false);
            ImageGrid grid;
            try {
                grid = bob.Create();    // this will throw if bitmap would be too big
            } catch (BadImageFormatException ex) {
                return new ErrorText("Unable to generate grid: " + ex.Message);
            }
            grid.Bitmap.Notes.MergeFrom(tmpNotes);

            char ch = (char)0;      // referenced from closure
            SetPixelFunc setPixel = delegate (int xc, int yc, byte color) {
                grid.DrawPixel(ch, xc, yc, color);
            };

            for (ch = macHeader.FirstChar; ch <= macHeader.LastChar + 1; ch++) {
                if (!macHeader.HasChar(ch)) {
                    grid.DrawRect(ch, 0, 0, cellWidth, macHeader.FRectHeight, COLOR_NO_CHAR);
                    continue;
                }
                int charWidth = macHeader.GetCharWidth(ch);

                // Draw the character background rectangle.  This starts at the origin and
                // has width equal to the character width.  Pixels in the leftward kern will
                // appear to the left of the rectangle.  Dead characters (those with zero width)
                // won't have a background.
                grid.DrawRect(ch, -macHeader.KernMax, 0, charWidth, macHeader.FRectHeight,
                    COLOR_GLYPH_BG);

                // Draw the glyph, if it has an image.
                int imageWidth = macHeader.GetImageWidth(ch);
                if (imageWidth != 0) {
                    DrawGlyph(macHeader, ch, macHeader.GetOffset(ch), 0, setPixel);
                }
            }

            return grid.Bitmap;
        }

        /// <summary>
        /// Generates a rendering of a sample string in the current font.
        /// </summary>
        /// <param name="gsHeader">IIgs font header.</param>
        /// <param name="macHeader">Macintosh font header.</param>
        /// <param name="tmpNotes">Notes generated during calculations.</param>
        /// <returns>Bitmap with rendering.</returns>
        private static IConvOutput RenderFontSample(GSFontHeader gsHeader, MacFontHeader macHeader,
                Notes tmpNotes) {
            string testStr = "The quick brown fox jumps over the lazy dog.";

            int width = DrawString(macHeader, testStr, null);

            Bitmap8 bitmap = new Bitmap8(width, macHeader.FRectHeight);
            bitmap.SetPalette(FONT_PALETTE);
            bitmap.Notes.MergeFrom(tmpNotes);

            // Color 0 is the grid background color.  Set to white.
            bitmap.SetAllPixels(COLOR_GLYPH_BG);
            DrawString(macHeader, testStr, bitmap);

            return bitmap;
        }

        /// <summary>
        /// Draws a string with the current font.  If "bitmap" is null, the string's length
        /// is computed.
        /// </summary>
        private static int DrawString(MacFontHeader macHeader, string str, Bitmap8? bitmap) {
            SetPixelFunc setPixel = delegate (int xc, int yc, byte color) {
                bitmap!.SetPixelIndex(xc, yc, COLOR_GLYPH_FG);
            };

            int penOffset = 0;
            int lineWidth = 0;
            for (int i = 0; i < str.Length; i++) {
                char ch = str[i];
                if (!macHeader.HasChar(ch)) {
                    // Character is not part of font.  Use the "missing character" entry instead,
                    // which is required to exist.
                    ch = (char)(macHeader.LastChar + 1);
                }
                int charOffset = macHeader.GetOffset(ch);
                int origin = charOffset + macHeader.KernMax;

                if (penOffset + origin < 0) {
                    // This is probably the first character, and it has leftward kerning.
                    // Scoot over.  (Could also be the second character with a big kern after
                    // an initial very narrow non-kerned character; that'll look a little funny.)
                    Debug.Assert(origin < 0);
                    //Debug.WriteLine("Adjusting pen offset from " + penOffset + " to " +
                    //    (penOffset - origin));
                    penOffset += -origin;
                }

                int charWidth = macHeader.GetCharWidth(ch);
                int imageWidth = macHeader.GetImageWidth(ch);

                // The pen advances by the character width (which might be zero).
                int nextPenOffset = penOffset + charWidth;

                // The total width of the line rendering bitmap increases by the area covered
                // by drawn pixels.  If the glyph is fully in the leftward kern, it might not
                // expand at all.
                int renderLeft = penOffset + origin;
                int rightBound = renderLeft + imageWidth;
                lineWidth = Math.Max(lineWidth, rightBound);

                if (bitmap != null) {
                    Debug.Assert(renderLeft + imageWidth <= bitmap.Width);
                    DrawGlyph(macHeader, ch, renderLeft, 0, setPixel);
                } else {
                    //Debug.WriteLine("'" + ch + "': off=" + charOffset + " pen=" + penOffset +
                    //    " rl=" + renderLeft + " nxt=" + nextPenOffset);
                }
                Debug.Assert(nextPenOffset >= penOffset);
                penOffset = nextPenOffset;
            }

            return lineWidth;
        }

        /// <summary>
        /// Draws a single character glyph at the specified position.
        /// </summary>
        /// <param name="macHeader">Macintosh font header.</param>
        /// <param name="ch">Character to draw.</param>
        /// <param name="xpos">Horizontal pixel offset of top-left corner.</param>
        /// <param name="ypos">Vertical pixel offset of top-left corner.</param>
        /// <param name="setPixel">Delegate that sets a pixel to a particular color index.</param>
        private static void DrawGlyph(MacFontHeader macHeader, char ch, int xpos, int ypos,
                SetPixelFunc setPixel) {
            int loc = macHeader.GetLocation(ch);
            int imageWidth = macHeader.GetImageWidth(ch);
            for (int yc = 0; yc < macHeader.FRectHeight; yc++) {
                int xc = 0;
                for (int strikeOffset = loc; strikeOffset < loc + imageWidth; strikeOffset++) {
                    int wordOffset = strikeOffset >> 4;
                    int bitOffset = strikeOffset & 0x0f;
                    ushort bits = macHeader.BitImage[yc * macHeader.RowWords + wordOffset];
                    if ((bits & (0x8000 >> bitOffset)) != 0) {
                        setPixel(xc + xpos, yc + ypos, COLOR_GLYPH_FG);
                    }
                    xc++;
                }
            }
        }
    }
}
