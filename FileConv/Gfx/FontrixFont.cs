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
    /// Converts Fontrix fonts.
    /// </summary>
    public class FontrixFont : Converter {
        public const string TAG = "fontrixfont";
        public const string LABEL = "Fontrix Font";
        public const string DESCRIPTION =
            "Converts a Fontrix font file to a bitmap, either as a grid with all possible " +
            "glyphs, or a sample string drawn with the font.";
        public const string DISCRIMINATOR = "DOS B with aux type $6400.";
        public override string Tag => TAG;
        public override string Label => LABEL;
        public override string Description => DESCRIPTION;
        public override string Discriminator => DISCRIMINATOR;

        public const string OPT_MODE = "mode";

        public override List<OptionDefinition> OptionDefs { get; protected set; } =
            new List<OptionDefinition>() {
                new OptionDefinition(OPT_MODE, "Conversion Mode",
                    OptionDefinition.OptType.Multi, ConvUtil.FONT_MODE_SAMPLE,
                        ConvUtil.FontModeTags, ConvUtil.FontModeDescrs),
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
                ConvUtil.MakeRGB(0x30, 0x30, 0xe0),     // 4=blue (labels)
                ConvUtil.MakeRGB(0xb0, 0x30, 0x30),     // 5=red (char not in font)
            });
        private const int COLOR_CELL_BG = 0;
        private const int COLOR_FRAME = 1;
        private const int COLOR_GLYPH_BG = 2;
        private const int COLOR_GLYPH_FG = 3;
        private const int COLOR_LABEL = 4;
        private const int COLOR_NO_CHAR = 5;

        private const int CHAR_COUNT_LOCATION = 0x10;
        private const int FIRST_ASCII_CHAR_LOCATION = 0x11;
        private const int IS_PROPORTIONAL_LOCATION = 0x12;

        private const int ID_BYTE_1 = 0x18;
        private const int ID_BYTE_2 = 0x19;
        private const int ID_BYTE_3 = 0x1a;

        private static readonly byte[] SIG1 = new byte[] { 0x90, 0xf7, 0xb2 };
        private static readonly byte[] SIG2 = new byte[] { 0x6f, 0x08, 0x4d };

        private const byte SPACE_VAL_OFFSET = 32;
        private const int MISSING_GLYPH_WIDTH = 8;
        private const int SPACE_WIDTH = 8;

        private const int MAX_FILE_LEN = 32 * 1024;     // editor is DOS + Applesoft + hi-res

        private const byte MAX_NUM_CHARACTERS = 94;

        // The file data.  Give it a minimum size up front so it shouldn't be null later on.
        private byte[] mDataBuf = new byte[0];


        private FontrixFont() { }

        public FontrixFont(FileAttribs attrs, Stream? dataStream, Stream? rsrcStream,
                ResourceMgr? resMgr, ConvFlags convFlags, AppHook appHook)
                : base(attrs, dataStream, rsrcStream, resMgr, convFlags, appHook) {
            Applic = TestApplicability();
        }

        protected override Applicability TestApplicability() {
            if (DataStream == null || IsRawDOS) {
                return Applicability.Not;
            }
            if (DataStream.Length < ID_BYTE_3 || DataStream.Length > MAX_FILE_LEN) {
                return Applicability.Not;
            }

            if (FileAttrs.FileType == FileAttribs.FILE_TYPE_BIN) {
                if (FileAttrs.AuxType == 0x6400) {

                    // Only need to read up to ID_BYTE_3 to validate. +1 to get size from offset.
                    var buffer = new byte[ID_BYTE_3 + 1];

                    DataStream.Position = 0;
                    DataStream.ReadExactly(buffer, 0, ID_BYTE_3 + 1);

                    // We're expecting 90 f7 b2, but sometimes we find 6f 08 4d.
                    bool hasSig1 =
                        buffer[ID_BYTE_1] == SIG1[0] &&
                        buffer[ID_BYTE_2] == SIG1[1] &&
                        buffer[ID_BYTE_3] == SIG1[2];
                    bool hasSig2 =
                        buffer[ID_BYTE_1] == SIG2[0] &&
                        buffer[ID_BYTE_2] == SIG2[1] &&
                        buffer[ID_BYTE_3] == SIG2[2];
                    // Check some of the other values to confirm they're in the right range.
                    bool goodVals =
                        buffer[CHAR_COUNT_LOCATION] >= 1 &&
                        buffer[CHAR_COUNT_LOCATION] <= MAX_NUM_CHARACTERS &&
                        buffer[FIRST_ASCII_CHAR_LOCATION] > 32 &&
                        buffer[FIRST_ASCII_CHAR_LOCATION] <= 127 &&
                        buffer[IS_PROPORTIONAL_LOCATION] >= 0 &&
                        buffer[IS_PROPORTIONAL_LOCATION] <= 1;

                    if (hasSig1 && goodVals) {
                        return Applicability.Yes;
                    } else if (hasSig2 && goodVals) {
                        return Applicability.Probably;
                    } else if (goodVals) {
                        // No matching ID bytes; put it on the list, but near the bottom.
                        // (Since the auxtype matches, an argument could be made for Maybe,
                        // but we're really expecting the ID bytes to be there.)
                        return Applicability.ProbablyNot;
                    }
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

            mDataBuf = new byte[DataStream.Length];
            DataStream.Position = 0;
            DataStream.ReadExactly(mDataBuf, 0, mDataBuf.Length);

            string modeStr = GetStringOption(options, OPT_MODE, ConvUtil.FONT_MODE_SAMPLE);
            switch (modeStr) {
                case ConvUtil.FONT_MODE_GRID:
                    return RenderFontGrid();
                case ConvUtil.FONT_MODE_SAMPLE:
                default:
                    return RenderFontSample();
            }
        }

        /// <summary>
        /// Renders a Fontrix font file to a glyph image grid.
        /// </summary>
        private IConvOutput RenderFontGrid() {
            int maxGlyphWidth = CalcMaxGlyphWidth();
            int maxGlyphHeight = GetFontHeight();
            int start = GetFirstCharacterOffset();
            int numChars = GetCharacterCount();

            // Set up the grid, using the ASCII value rather than the glyph index as the
            // cell number.  This makes the labels match ASCII.
            ImageGrid.Builder bob = new ImageGrid.Builder();
            bob.SetGeometry(maxGlyphWidth, maxGlyphHeight, 16);
            bob.SetRange(start + SPACE_VAL_OFFSET, numChars);
            bob.SetColors(FONT_PALETTE, COLOR_LABEL, COLOR_CELL_BG, COLOR_FRAME, COLOR_FRAME);
            bob.SetLabels(hasLeftLabels: true, hasTopLabels: true, leftLabelIsRow: false, 16);
            ImageGrid grid;
            try {
                grid = bob.Create();    // this will throw if bitmap would be too big
            } catch (BadImageFormatException ex) {
                return new ErrorText("Unable to generate grid: " + ex.Message);
            }

            SetPixelFunc setPixel = delegate (int glyphIndex, int col, int row, byte color) {
                grid.DrawPixel(glyphIndex, col, row, color);
            };

            for (int i = start; i < start + numChars; i++) {
                // Draw background.
                grid.DrawRect(i + SPACE_VAL_OFFSET, 0, 0, GetWidthForCharacter(i),
                    maxGlyphHeight, COLOR_GLYPH_BG);

                // Draw glyph.
                DrawGlyph(i, 0, 0, setPixel);
            }

            return grid.Bitmap;
        }

        /// <summary>
        /// Renders a sample string with a Fontrix font.
        /// </summary>
        private IConvOutput RenderFontSample() {
            string testStr = "The quick brown fox jumps over the lazy dog.";

            int width = MeasureString(testStr);

            Bitmap8 bitmap = new Bitmap8(width, GetFontHeight());
            bitmap.SetPalette(FONT_PALETTE);

            // Color 0 is the grid background color, which we don't want.  Set to white.
            bitmap.SetAllPixels(COLOR_GLYPH_BG);
            DrawString(testStr, bitmap);

            return bitmap;
        }

        /// <summary>
        /// Measures the rendered width of a string, in pixels.
        /// </summary>
        private int MeasureString(string str) {
            int start = GetFirstCharAsciiCode();
            int numChars = GetCharacterCount();
            int width = 0;
            for (int i = 0; i < str.Length; i++) {
                int idx = MapChar(str[i]);
                int charWidth;
                if (idx >= 0) {
                    charWidth = GetWidthForCharacter(idx);
                } else {
                    charWidth = MISSING_GLYPH_WIDTH;
                }

                // Add width of glyph, plus one pixel spacing before it unless it's the first.
                width += charWidth + (i == 0 ? 0 : 1);
            }
            return width;
        }

        /// <summary>
        /// Draws a string on a bitmap.
        /// </summary>
        private void DrawString(string str, Bitmap8 bitmap) {
            // Configure pixel-set delegate.  This will take care of pixel size multiplication.
            SetPixelFunc setPixel = delegate (int unused, int col, int row, byte color) {
                bitmap.SetPixelIndex(col, row, color);
            };

            int xoff = 0;
            foreach (char ch in str) {
                int idx = MapChar(ch);
                if (idx >= 0) {
                    DrawGlyph(idx, xoff, 0, setPixel);
                    xoff += GetWidthForCharacter(idx);
                } else {
                    xoff += MISSING_GLYPH_WIDTH;

                }
                xoff++;     // add one space between glyphs
            }
        }

        /// <summary>
        /// Maps a character to a glyph index.  Returns -1 if the character is not represented
        /// in the font.
        /// </summary>
        private int MapChar(char ch) {
            int start = GetFirstCharAsciiCode();
            int numChars = GetCharacterCount();
            if (ch < start || ch >= start + numChars) {
                // Not present in font.  Try upper case.
                ch = char.ToUpper(ch);
            }
            if (ch >= start && ch < start + numChars) {
                return ch - SPACE_VAL_OFFSET;
            } else {
                return -1;
            }
        }

        // Pixel draw delegate, so we can render glyphs on Bitmaps and ImageGrids with one func.
        private delegate void SetPixelFunc(int glyphIndex, int xc, int yc, byte color);

        /// <summary>
        /// Draws a single glyph at the specified position.
        /// </summary>
        /// <param name="glyphIndex">Index of character to draw.</param>
        /// <param name="xpos">Horizontal pixel offset of top-left corner.</param>
        /// <param name="ypos">Vertical pixel offset of top-left corner.</param>
        /// <param name="setPixel">Delegate that sets a pixel to a particular color index.</param>
        private void DrawGlyph(int glyphIndex, int xpos, int ypos, SetPixelFunc setPixel) {
            byte charWidth = GetWidthForCharacter(glyphIndex);
            byte charHeight = GetFontHeight();
            byte[] charData = GetDataForCharacter(glyphIndex);

            int offset = 0;

            for (int row = 0; row < charHeight; row++) {
                for (int col = 0; col < WidthToBytes(charWidth); col++) {
                    byte bval = charData[offset++];
                    for (int bit = 7; bit >= 0; bit--) {
                        // Bits are zero for background, one for foreground; leftmost pixel in LSB.
                        if ((bval >> 7) != 0) {
                            setPixel(glyphIndex + SPACE_VAL_OFFSET,
                                col * 8 + bit + xpos, row + ypos, COLOR_GLYPH_FG);
                        }
                        bval <<= 1;
                    }
                }
            }
        }


        /// <summary>
        /// Returns the number of characters in the current font.
        /// </summary>
        private byte GetCharacterCount() {
            Debug.Assert(mDataBuf.Length > CHAR_COUNT_LOCATION);

            return mDataBuf[CHAR_COUNT_LOCATION];
        }


        /// <summary>
        /// Returns the true if the current font is proportional, false otherwise.
        /// </summary>
        private bool IsFontProportional() {
            Debug.Assert(mDataBuf.Length > IS_PROPORTIONAL_LOCATION);

            return mDataBuf[IS_PROPORTIONAL_LOCATION] > 0;
        }


        /// <summary>
        /// Returns the ASCII code of the first character defined in the font.
        /// </summary>
        private byte GetFirstCharAsciiCode() {
            Debug.Assert(mDataBuf.Length > FIRST_ASCII_CHAR_LOCATION);

            return mDataBuf[FIRST_ASCII_CHAR_LOCATION];
        }


        /// <summary>
        /// Returns the offset in the internal pointer table for the first character in the font.
        /// It is calculated by subtracting 32 from the ASCII value of the first character.
        /// ASCII 32 is space, and there is a pointer assigned for space, but it is generally
        /// undefined or points to null data.  Per the documentation of Fontrix, the space 
        /// character is not used in practice.
        /// </summary>
        private byte GetFirstCharacterOffset() {
            return (byte)(GetFirstCharAsciiCode() - SPACE_VAL_OFFSET);
        }


        /// <summary>
        /// Calculates the width of the widest glyph in the set.
        /// </summary>
        private int CalcMaxGlyphWidth() {
            int count = GetCharacterCount();
            int start = GetFirstCharacterOffset();
            int maxWidth = 0;
            for (int i = start; i < start + count; i++) {
                int width = GetWidthForCharacter(i);
                if (width > maxWidth) {
                    maxWidth = width;
                }
            }
            return maxWidth;
        }


        /// <summary>
        /// Get the height in pixels for all characters in the font.
        /// </summary>
        /// <returns>The pixel height of all characters.</returns>
        private byte GetFontHeight() {
            return mDataBuf[0x14];
        }

        /// <summary>
        /// Get the practical width in pixels for the character at the given offset. Depending on
        /// whether or not the font is proportional, it will return either the full byte-span width
        /// or a measured width.
        /// </summary>
        /// <param name="offset">The character width to fetch</param>
        /// <returns>
        /// The most relevant pixel width of the character within the file. 
        /// </returns>
        private byte GetWidthForCharacter(int offset) {
            if (IsFontProportional()) {
                return GetMeasuredWidthForCharacter(offset);
            } else {
                return GetFullWidthForCharacter(offset);
            }
        }


        /// <summary>
        /// This is a utility function for measuring the width of pixels in a character. Since the
        /// data is stored LSB leading, the span of pixels can be measured by finding the width
        /// between the 0th position and the highest bit position in the byte.
        /// </summary>
        /// <param name="val">The byte being measured</param>
        /// <returns>
        /// The number of pixels the high bit of the byte is away from the 0th position.
        /// </returns>
        private static byte MeasureByteWidth(byte val) {
            return val switch {
                >= 128 => 8,
                >= 64 => 7,
                >= 32 => 6,
                >= 16 => 5,
                >= 8 => 4,
                >= 4 => 3,
                >= 2 => 2,
                >= 1 => 1,
                _ => 0,
            };
        }


        /// <summary>
        /// Get the width in pixels for the character at the given offset.  Used for
        /// non-proportional fonts
        /// </summary>
        /// <param name="offset">The character width to fetch</param>
        /// <returns>
        /// The full-byte pixel width of the character within the file. (Char width in bytes * 8)
        /// </returns>
        private byte GetFullWidthForCharacter(int offset) {
            // This is measured from the size of the block of contiguous bytes between this char and
            //  the following character's data start, divided by the height of the character.
            var a1 = GetDataOffsetForCharacter(offset);
            var a2 = GetDataOffsetForCharacter(offset + 1);
            var byteWidth = (byte)(8 * (a2 - a1) / GetFontHeight());

            return byteWidth;
        }


        /// <summary>
        /// Get the exact maximum width in pixels for the character at the given offset. Used in
        /// proportional fonts.
        /// </summary>
        /// <param name="offset">The character width to fetch</param>
        /// <returns>
        /// The width in pixels of this character, as measured from leftmost- to rightmost-pixel 
        /// along each line.  This is generally smaller than the full width value.
        /// </returns>
        private byte GetMeasuredWidthForCharacter(int offset) {
            var height = GetFontHeight();
            var byteWidth = GetFullWidthForCharacter(offset) / 8;
            var data = GetDataForCharacter(offset);

            byte width = 0;

            for (var h = 0; h < height; h++) {
                for (var w = 0; w < byteWidth; w++) {
                    var b = data[h * byteWidth + w];
                    byte thisByte = (byte)(MeasureByteWidth(b) + (w * 8));
                    if (thisByte > width) { width = thisByte; };
                }
            }
            return width;
        }


        /// <summary>
        /// Get the data block pointer for the character at the  given offset.
        /// </summary>
        /// <param name="charOffset">The character offset to fetch</param>
        /// <returns>The pointer to the data within the file.</returns>
        private ushort GetDataOffsetForCharacter(int charOffset) {
            byte loVal = mDataBuf[0x20 + (2 * charOffset)];
            byte hiVal = mDataBuf[0x21 + (2 * charOffset)];
            ushort dataOffset = (ushort)(loVal + 256 * hiVal);
            return dataOffset;
        }

        /// <summary>
        /// Get the data block from the file at the pointer for the character at the given offset.
        /// </summary>
        /// <param name="offset">The character offset to fetch.</param>
        /// <returns>A byte array containing the character data.</returns>
        private byte[] GetDataForCharacter(int offset) {
            byte width = GetFullWidthForCharacter(offset);

            int dataLength = GetFontHeight() * WidthToBytes(width);

            ushort dataOffset = GetDataOffsetForCharacter(offset);

            byte[] retval = new byte[dataLength];
            for (int idx = 0; idx < dataLength; idx++) {
                retval[idx] = mDataBuf[dataOffset + idx];
            }
            return retval;
        }

        /// <summary>
        /// Calculates the number of bytes it takes to store a given run of pixels.
        /// </summary>
        /// <param name="width">The number of pixels to use.</param>
        /// <returns>The number of bytes it takes to pack the given pixel count.</returns>
        private static byte WidthToBytes(byte width) {
            if (width == 0) return 0;
            return (byte)(((width - 1) / 8) + 1);
        }
    }
}
