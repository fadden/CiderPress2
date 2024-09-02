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

namespace FileConv.Gfx
{
    /// <summary>
    /// Converts Print Shop fonts.
    /// </summary>
    public class PrintShopFont : Converter
    {
        public const string TAG = "psfont";
        public const string LABEL = "Print Shop Font";
        public const string DESCRIPTION =
            "Converts a Print Shop font file to a bitmap, either as a grid with all " +
            "possible glyphs, or a sample string drawn with the font.  " +
            "These can optionally be pixel-multiplied x2/x3, as they would when printed.";
        public const string DISCRIMINATOR = "DOS B with load address $5FF4 or $6000.";
        public override string Tag => TAG;
        public override string Label => LABEL;
        public override string Description => DESCRIPTION;
        public override string Discriminator => DISCRIMINATOR;

        public const string OPT_MULT = "mult";
        public const string OPT_MODE = "mode";

        public override List<OptionDefinition> OptionDefs { get; protected set; } =
            new List<OptionDefinition>() {
                new OptionDefinition(OPT_MULT, "Multiply width/height",
                    OptionDefinition.OptType.Boolean, "true"),
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

        private const int MINIMUM_HEADER_SIZE = 576;
        private const int PSC_FONT_HEADERS_SIZE = 12;
        private const int MAX_CHARACTER_DATA_SIZE = 152;
        private const int CLASSIC_MONO_LEN = 576;

        // There are 59 characters in the font, but characters 0 and 32 are not standard.
        // This is taken into conderation when calculating MAX_FONT_FILE_SIZE.
        private const int CHARACTER_COUNT = 59;
        private const int MAX_FONT_FILE_SIZE = PSC_FONT_HEADERS_SIZE +
                                               MINIMUM_HEADER_SIZE +
                                               ((CHARACTER_COUNT - 2) * MAX_CHARACTER_DATA_SIZE) +
                                               CLASSIC_MONO_LEN;

        private const ushort FONT_WIDTH_TABLE = 0x6000;
        private const ushort FONT_HEIGHT_TABLE = 0x603B;
        private const ushort FONT_ADD_LO_TABLE = 0x6076;
        private const ushort FONT_ADD_HI_TABLE = 0x60B1;
        private const byte MAX_CHARACTER_HEIGHT = 38;
        private const byte MAX_CHARACTER_WIDTH = 48;
        private const ushort IMAGE_CHARACTER = 32;
        private const int MISSING_GLYPH_WIDTH = 8;
        //const int INTER_CHARACTER_SPACING_PX = 3;
        //const int LEFT_PADDING = 2;
        //const int RIGHT_PADDING = 2;
        //const int TOP_PADDING = 2;
        //const int BOTTOM_PADDING = 2;
        private const int XMULT = 2;
        private const int YMULT = 3;


        // We have to unpack the data in order to validate it, so might as well keep a copy of it for later.
        private byte[]? mDataBuf;

        private int mMaxGlyphWidth, mMaxGlyphHeight;


        private PrintShopFont() { }

        public PrintShopFont(FileAttribs attrs, Stream? dataStream, Stream? rsrcStream,
                ResourceMgr? resMgr, ConvFlags convFlags, AppHook appHook)
                : base(attrs, dataStream, rsrcStream, resMgr, convFlags, appHook)
        {
            Applic = TestApplicability();
        }

        protected override Applicability TestApplicability()
        {
            if (DataStream == null)
            {
                return Applicability.Not;
            }

            if (FileAttrs.FileType == FileAttribs.FILE_TYPE_BIN)
            {
                if (FileAttrs.DataLength <= MAX_FONT_FILE_SIZE &&
                   ((FileAttrs.AuxType == 0x6000 && FileAttrs.DataLength >= MINIMUM_HEADER_SIZE) ||
                    (FileAttrs.AuxType == 0x5FF4 && FileAttrs.DataLength >= MINIMUM_HEADER_SIZE + PSC_FONT_HEADERS_SIZE)))
                {
                    UnpackData();

                    if (DataPointersAreValid())
                    {
                        if (CountOversizedCharacters() == 0)
                        {
                            return Applicability.Yes;
                        }
                        else
                        {
                            return Applicability.Probably;
                        }
                    }
                }
            }
            return Applicability.Not;
        }

        public override Type GetExpectedType(Dictionary<string, string> options)
        {
            return typeof(IBitmap);
        }

        public override IConvOutput ConvertFile(Dictionary<string, string> options)
        {
            if (Applic <= Applicability.Not)
            {
                Debug.Assert(false);
                return new ErrorText("ERROR");
            }

            Debug.Assert(mDataBuf != null);
            Debug.Assert(mDataBuf.Length >= 0);
            Debug.Assert(DataStream != null);

            CalculateFontMetrics();

            bool doMult = GetBoolOption(options, OPT_MULT, false);
            string modeStr = GetStringOption(options, OPT_MODE, ConvUtil.FONT_MODE_SAMPLE);
            switch (modeStr) {
                case ConvUtil.FONT_MODE_GRID:
                    return RenderFontGrid(doMult);
                case ConvUtil.FONT_MODE_SAMPLE:
                default:
                    return RenderFontSample(doMult);
            }
        }

        /// <summary>
        /// Calculates font metrics.  Sets mMaxGlyphWidth and mMaxGlyphHeight.
        /// </summary>
        private void CalculateFontMetrics()
        {
            int maxWidth = 0;
            int maxHeight = 0;
            for (int idx = 0; idx < CHARACTER_COUNT; idx++)
            {
                if (idx != IMAGE_CHARACTER)
                {
                    int width = GetWidthForCharacter(idx);
                    int height = GetHeightForCharacter(idx);
                    if (width > maxWidth) { maxWidth = width; }
                    if (height > maxHeight) { maxHeight = height; }
                }
            }
            mMaxGlyphWidth = maxWidth;
            mMaxGlyphHeight = maxHeight;
        }

        /// <summary>
        /// Converts a Print Shop font file to a glyph image grid.
        /// </summary>
        private IConvOutput RenderFontGrid(bool doMult)
        {
            int xmult = doMult ? XMULT : 1;
            int ymult = doMult ? YMULT : 1;

            ImageGrid.Builder bob = new ImageGrid.Builder();
            bob.SetGeometry(mMaxGlyphWidth * xmult, mMaxGlyphHeight * ymult, 8);
            bob.SetRange(0, CHARACTER_COUNT);
            bob.SetColors(FONT_PALETTE, COLOR_LABEL, COLOR_CELL_BG, COLOR_FRAME, COLOR_FRAME);
            bob.SetLabels(hasLeftLabels: true, hasTopLabels: true, leftLabelIsRow: false, 16);
            ImageGrid grid;
            try
            {
                grid = bob.Create();    // this will throw if bitmap would be too big
            }
            catch (BadImageFormatException ex)
            {
                return new ErrorText("Unable to generate grid: " + ex.Message);
            }

            // Configure pixel-set delegate.  This will take care of pixel size multiplication.
            SetPixelFunc setPixel;
            if (doMult) {
                setPixel = delegate (int glyphIndex, int col, int row, byte color) {
                    for (int y = 0; y < YMULT; y++)
                    {
                        for (int x = 0; x < XMULT; x++)
                        {
                            grid.DrawPixel(glyphIndex, col * XMULT + x, row * YMULT + y, color);
                        }
                    }
                };
            } else {
                setPixel = delegate (int glyphIndex, int col, int row, byte color)
                {
                    grid.DrawPixel(glyphIndex, col, row, color);
                };
            }

            // Draw each glyph in its own cell.
            for (int idx = 0; idx < CHARACTER_COUNT; idx++)
            {
                if (idx == 0)
                {
                    // Show the space character as a 1-pixel-high line.  It actually has a
                    // height of zero, but the width (which is auto-generated by the editor)
                    // is valid.  We could do this in a different color to show that there
                    // isn't actually anything to draw, but that's probably not needed.
                    for (int i = 0; i < GetWidthForCharacter(idx); i++) {
                        setPixel(0, i, 0, COLOR_GLYPH_BG);
                    }
                }
                else if (idx == IMAGE_CHARACTER)
                {
                    // Fill the cell with the missing-glyph color.
                    grid.DrawRect(idx, 0, 0, mMaxGlyphWidth * xmult, mMaxGlyphHeight * ymult,
                        COLOR_NO_CHAR);
                }
                else
                {
                    // Draw a background rectangle the full size of the glyph.
                    byte charWidth = GetWidthForCharacter(idx);
                    byte charHeight = GetHeightForCharacter(idx);
                    grid.DrawRect(idx, 0, 0, charWidth * xmult, charHeight * ymult,
                        COLOR_GLYPH_BG);

                    // Draw the glyph.
                    DrawGlyph(idx, 0, 0, setPixel);
                }
            }

            return grid.Bitmap;
        }

        /// <summary>
        /// Renders a sample string with a Print Shop font file.
        /// </summary>
        private IConvOutput RenderFontSample(bool doMult) {
            string testStr = "The quick brown fox jumps!";
            int xmult = doMult ? XMULT : 1;
            int ymult = doMult ? YMULT : 1;

            int width = MeasureString(testStr);

            Bitmap8 bitmap = new Bitmap8(width * xmult, mMaxGlyphHeight * ymult);
            bitmap.SetPalette(FONT_PALETTE);

            // Color 0 is the grid background color, which we don't want.  Set to white.
            bitmap.SetAllPixels(COLOR_GLYPH_BG);
            DrawString(testStr, doMult, bitmap);

            return bitmap;
        }

        /// <summary>
        /// Measures the rendered width of a string, in pixels.
        /// </summary>
        private int MeasureString(string str)
        {
            int width = 0;
            bool first = true;
            foreach (char ch in str)
            {
                int idx = MapChar(ch);
                byte charWidth;
                if (idx < 0)
                {
                    charWidth = MISSING_GLYPH_WIDTH;      // invalid char, leave some space
                }
                else
                {
                    charWidth = GetWidthForCharacter(idx);
                }
                width += charWidth + 1;     // glyph width, plus one pixel before it
                if (first)
                {
                    first = false;
                    width--;                // undo 1-pixel gap before first glyph
                }
            }
            return width;
        }

        /// <summary>
        /// Draws a string on a bitmap.
        /// </summary>
        private void DrawString(string str, bool doMult, Bitmap8 bitmap)
        {
            // Configure pixel-set delegate.  This will take care of pixel size multiplication.
            SetPixelFunc setPixel;
            if (doMult) {
                setPixel = delegate (int unused, int col, int row, byte color)
                {
                    for (int y = 0; y < YMULT; y++)
                    {
                        for (int x = 0; x < XMULT; x++)
                        {
                            bitmap.SetPixelIndex(col * XMULT + x, row * YMULT + y, color);
                        }
                    }
                };
            } else {
                setPixel = delegate (int unused, int col, int row, byte color)
                {
                    bitmap.SetPixelIndex(col, row, color);
                };
            }

            int xoff = 0;
            for (int i = 0; i < str.Length; i++)
            {
                int glyphIndex = MapChar(str[i]);
                if (glyphIndex < 0)
                {
                    // Invalid character, leave a small blank space.
                    xoff += MISSING_GLYPH_WIDTH;
                }
                else
                {
                    DrawGlyph(glyphIndex, xoff, 0, setPixel);
                    xoff += GetWidthForCharacter(glyphIndex);
                }
                xoff++;     // add one space between glyphs
            }
        }

        /// <summary>
        /// Maps a character to a glyph index.  Letters are converted to upper case.
        /// Returns -1 for '@' and invalid characters.
        /// </summary>
        private static int MapChar(char ch)
        {
            ch = char.ToUpper(ch);
            if (ch < ' ' || ch == '@' || ch > 'Z')
            {
                return -1;
            }
            else
            {
                return ch - 0x20;
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
            byte charHeight = GetHeightForCharacter(glyphIndex);
            byte[] charData = GetDataForCharacter(glyphIndex);
            int offset = 0;
            for (int row = 0; row < charHeight; row++)
            {
                for (int col = 0; col < WidthToBytes(charWidth); col++)
                {
                    byte bval = charData[offset++];
                    for (int bit = 0; bit < 8; bit++)
                    {
                        if ((bval >> 7) != 0)
                        {
                            setPixel(glyphIndex, col * 8 + bit + xpos, row + ypos, COLOR_GLYPH_FG);
                        }
                        bval <<= 1;
                    }
                }
            }
        }

        /// <summary>
        /// Get the height in pixels for the character at the  given offset.
        /// </summary>
        /// <param name="offset">The character height to fetch</param>
        /// <returns>The pixel height of the character within the file.</returns>
        private byte GetHeightForCharacter(int offset)
        {
            Debug.Assert(mDataBuf != null);
            Debug.Assert(offset >= 0 && offset < CHARACTER_COUNT);

            ushort heights = (ushort)(FONT_HEIGHT_TABLE - FileAttrs.AuxType);
            byte height = mDataBuf[offset + heights];
            return height;
        }

        /// <summary>
        /// Get the width in pixels for the character at the  given offset.
        /// </summary>
        /// <param name="offset">The character width to fetch</param>
        /// <returns>The pixel width of the character within the file.</returns>
        private byte GetWidthForCharacter(int offset)
        {
            Debug.Assert(mDataBuf != null);
            Debug.Assert(offset >= 0 && offset < CHARACTER_COUNT);

            ushort widths = (ushort)(FONT_WIDTH_TABLE - FileAttrs.AuxType);
            byte width = (byte)(mDataBuf[offset + widths] & 0x7f);
            return width;
        }

        /// <summary>
        /// Get the data block pointer for the character at the  given offset.
        /// </summary>
        /// <param name="offset">The character offset to fetch</param>
        /// <returns>The pointer to the data within the file.</returns>
        private ushort GetAddressForCharacter(int offset)
        {
            Debug.Assert(mDataBuf != null);
            Debug.Assert(offset >= 0 && offset < CHARACTER_COUNT);

            ushort loVals = (ushort)(FONT_ADD_LO_TABLE - FileAttrs.AuxType);
            ushort hiVals = (ushort)(FONT_ADD_HI_TABLE - FileAttrs.AuxType);
            ushort addr = (ushort)(mDataBuf[offset + loVals] + 256 * mDataBuf[offset + hiVals]);
            return addr;
        }

        /// <summary>
        /// Get the data block from the file at the pointer for the character at the given offset.
        /// </summary>
        /// <param name="offset">The character offset to fetch.</param>
        /// <returns>A byte array containing the character data.</returns>
        private byte[] GetDataForCharacter(int offset)
        {
            Debug.Assert(mDataBuf != null);
            Debug.Assert(offset >= 0 && offset < CHARACTER_COUNT);
            byte width = GetWidthForCharacter(offset);

            int dataLength = GetHeightForCharacter(offset) * WidthToBytes(width);

            ushort dataOffset = (ushort)(GetAddressForCharacter(offset) - FileAttrs.AuxType);

            byte[] retval = new byte[dataLength];
            for (int idx = 0; idx < dataLength; idx++)
            {
                retval[idx] = mDataBuf[dataOffset + idx];
            }
            return retval;
        }

        /// <summary>
        /// Calculates the number of bytes it takes to store a given run of pixels.
        /// </summary>
        /// <param name="width">The number of pixels to use.</param>
        /// <returns>The number of bytes it takes to pack the given pixel count.</returns>
        private static byte WidthToBytes(byte width)
        {
            if (width == 0) return 0;
            return (byte)(((width - 1) / 8) + 1);
        }

        /// <summary>
        /// Reads the data from the datastream into a byte array.
        /// </summary>
        private void UnpackData()
        {
            Debug.Assert(DataStream != null);

            mDataBuf = new byte[DataStream.Length];
            DataStream.Position = 0;
            DataStream.ReadExactly(mDataBuf, 0, mDataBuf.Length);
        }

        /// <summary>
        /// Checks to see if the data offsets of each character in the file point to an internal
        /// data block within the file.
        /// </summary>
        /// <returns>True, if the offsets are contained within the font file, False otherwise.</returns>
        private bool DataPointersAreValid()
        {
            Debug.Assert(mDataBuf != null);
            Debug.Assert(mDataBuf.Length > MINIMUM_HEADER_SIZE + PSC_FONT_HEADERS_SIZE);

            bool valid = true;

            for (byte idx = 1; idx < CHARACTER_COUNT && valid; idx++)
            {
                if (idx != IMAGE_CHARACTER)
                {  // We don't want to check the "Image" character @ offset 32.
                    ushort addr = GetAddressForCharacter(idx);

                    if (!(addr >= 0x6000 && addr < FileAttrs.AuxType + mDataBuf.Length))
                    {
                        Debug.WriteLine("PSF: address out of range for character " + idx);
                    }

                    valid &= addr >= 0x6000 && addr < FileAttrs.AuxType + mDataBuf.Length;
                }
            }

            if (!valid)
            {
                Debug.WriteLine("PSF: valid is not set for this font");
            }

            return valid;
        }


        /// <summary>
        /// Checks to see if the widths and heights of each character in the file are
        /// consistent with those of a Print Shop font file.
        /// </summary>
        /// <returns>Count of the total characters that are larger than those that can be edited
        /// in the Print Shop Companion font editor.</returns>
        private int CountOversizedCharacters()
        {
            Debug.Assert(mDataBuf != null);
            Debug.Assert(mDataBuf.Length > MINIMUM_HEADER_SIZE + PSC_FONT_HEADERS_SIZE);

            int oversized = 0;

            for (ushort idx = 1; idx < CHARACTER_COUNT; idx++)
            {
                if (idx != IMAGE_CHARACTER)
                {  // We don't want to check the "Image" character @ offset 32.
                    byte width = GetWidthForCharacter(idx);
                    byte height = GetHeightForCharacter(idx);

                    if (!(height <= MAX_CHARACTER_HEIGHT))
                    {
                        Debug.WriteLine("Height out of range for Character " + idx);
                    }

                    if (!(width <= MAX_CHARACTER_WIDTH))
                    {
                        Debug.WriteLine("Width out of range for Character " + idx);
                    }

                    if (height <= MAX_CHARACTER_HEIGHT && width <= MAX_CHARACTER_WIDTH)
                    {
                        oversized++;
                    }
                }
            }

            if (oversized > 0)
            {
                Debug.WriteLine(oversized + " character(s) are oversized.");
            }

            return oversized;
        }
    }
}
