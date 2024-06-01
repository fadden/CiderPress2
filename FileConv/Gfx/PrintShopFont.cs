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
            "Converts a Print Shop font file to a bitmap.  " +
            "These can optionally be pixel-multiplied x2/x3.";

        // TODO
        public const string DISCRIMINATOR = "DOS B with aux type $5FF4 or $6000";
        public override string Tag => TAG;
        public override string Label => LABEL;
        public override string Description => DESCRIPTION;
        public override string Discriminator => DISCRIMINATOR;

        public const string OPT_MULT = "mult";

        public override List<OptionDefinition> OptionDefs { get; protected set; } =
            new List<OptionDefinition>() {
                new OptionDefinition(OPT_MULT, "Multiply width/height",
                    OptionDefinition.OptType.Boolean, "true"),
            };

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
        const int INTER_CHARACTER_SPACING_PX = 3;
        const int LEFT_PADDING = 2;
        const int RIGHT_PADDING = 2;
        const int TOP_PADDING = 2;
        const int BOTTOM_PADDING = 2;
        private const int XMULT = 2;
        private const int YMULT = 3;


        // We have to unpack the data in order to validate it, so might as well keep a copy of it for later.
        private byte[]? DataBuf;

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

            Debug.Assert(DataBuf != null);
            Debug.Assert(DataBuf.Length >= 0);
            Debug.Assert(DataStream != null);


            bool doMult = GetBoolOption(options, OPT_MULT, false);

            return ConvertMono(DataBuf, Palette8.Palette_MonoBW, doMult);
        }

        /// <summary>
        /// Converts a Print Shop font file to a B&amp;W bitmap.
        /// </summary>
        public Bitmap8 ConvertMono(byte[] buf, Palette8 palette, bool doMult)
        {
            Bitmap8 output;

            int totalWidth = 0;
            int maxHeight = 0;

            for (int idx = 1; idx < CHARACTER_COUNT; idx++)
            {
                if (idx != IMAGE_CHARACTER)
                {
                    if (idx != 1) totalWidth += INTER_CHARACTER_SPACING_PX;
                    totalWidth += GetWidthForCharacter(idx);
                    int height = GetHeightForCharacter(idx);
                    if (height > maxHeight) { maxHeight = height; }
                }
            }

            Debug.WriteLine("TotalWidth: " + totalWidth);

            if (doMult)
            {
                output = new Bitmap8((LEFT_PADDING + totalWidth + RIGHT_PADDING) * XMULT,
                                     (TOP_PADDING + maxHeight + BOTTOM_PADDING) * YMULT);
            }
            else
            {
                output = new Bitmap8(LEFT_PADDING + totalWidth + RIGHT_PADDING,
                                     TOP_PADDING+ maxHeight + BOTTOM_PADDING);
            }
            output.SetPalette(palette);

            for (int idx = 0; idx < output.Width; idx++) {
                for (int jdx = 0; jdx < output.Height; jdx++) {
                    output.SetPixelIndex(idx, jdx, 1);
                }
            }

            int xOffset = LEFT_PADDING;
            for (int idx = 1; idx < CHARACTER_COUNT; idx++) // Skip Offset 0 ("Space")
            {
                if (idx != IMAGE_CHARACTER)
                {
                    byte charWidth = GetWidthForCharacter(idx);
                    byte charHeight = GetHeightForCharacter(idx);
                    byte[] charData = GetDataForCharacter(idx);

                    int offset = 0;
                    for (int row = 0; row < charHeight; row++)
                    {
                        for (int col = 0; col < WidthToBytes(charWidth); col++)
                        {
                            byte bval = charData[offset++];
                            for (int bit = 0; bit < 8; bit++)
                            {
                                // Bits are zero for background, one for foreground; leftmost pixel in MSB.
                                byte color = (byte)(1-(bval >> 7));

                                if (col * 8 + bit + xOffset < totalWidth + LEFT_PADDING + RIGHT_PADDING)
                                {
                                    if (doMult)
                                    {
                                        SetMultPixels(output, col * 8 + bit + xOffset, row + TOP_PADDING, color);
                                    }
                                    else
                                    {
                                        output.SetPixelIndex(col * 8 + bit + xOffset, row + TOP_PADDING, color);
                                    }
                                }
                                bval <<= 1;
                            }
                        }
                    }
                    xOffset += charWidth + INTER_CHARACTER_SPACING_PX;

                }
            }
            return output;
        }

        /// <summary>
        /// Sets a rectangular block of pixels to the same value, as part of pixel-multiplying
        /// an image.
        /// </summary>
        /// <param name="output">Bitmap to draw to.</param>
        /// <param name="col">Column number (pre-multiplied value).</param>
        /// <param name="row">Row number (pre-multiplied value).</param>
        /// <param name="colorIndex">Color index.</param>
        private static void SetMultPixels(Bitmap8 output, int col, int row, byte colorIndex)
        {
            for (int y = 0; y < YMULT; y++)
            {
                for (int x = 0; x < XMULT; x++)
                {
                    output.SetPixelIndex(col * XMULT + x, row * YMULT + y, colorIndex);
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
            Debug.Assert(DataBuf != null);
            Debug.Assert(offset >= 0 && offset < CHARACTER_COUNT);

            ushort heights = (ushort)(FONT_HEIGHT_TABLE - FileAttrs.AuxType);
            byte height = DataBuf[offset + heights];
            return height;
        }

        /// <summary>
        /// Get the width in pixels for the character at the  given offset.
        /// </summary>
        /// <param name="offset">The character width to fetch</param>
        /// <returns>The pixel width of the character within the file.</returns>
        private byte GetWidthForCharacter(int offset)
        {
            Debug.Assert(DataBuf != null);
            Debug.Assert(offset >= 0 && offset < CHARACTER_COUNT);

            ushort widths = (ushort)(FONT_WIDTH_TABLE - FileAttrs.AuxType);
            byte width = (byte)(DataBuf[offset + widths] & 0x7f);
            return width;
        }

        /// <summary>
        /// Get the data block pointer for the character at the  given offset.
        /// </summary>
        /// <param name="offset">The character offset to fetch</param>
        /// <returns>The pointer to the data within the file.</returns>
        private ushort GetAddressForCharacter(int offset)
        {
            Debug.Assert(DataBuf != null);
            Debug.Assert(offset >= 0 && offset < CHARACTER_COUNT);

            ushort loVals = (ushort)(FONT_ADD_LO_TABLE - FileAttrs.AuxType);
            ushort hiVals = (ushort)(FONT_ADD_HI_TABLE - FileAttrs.AuxType);
            ushort addr = (ushort)(DataBuf[offset + loVals] + 256 * DataBuf[offset + hiVals]);
            return addr;
        }

        /// <summary>
        /// Get the data block from the file at the pointer for the character at the given offset.
        /// </summary>
        /// <param name="offset">The character offset to fetch.</param>
        /// <returns>A byte array containing the character data.</returns>
        private byte[] GetDataForCharacter(int offset)
        {
            Debug.Assert(DataBuf != null);
            Debug.Assert(offset >= 0 && offset < CHARACTER_COUNT);
            byte width = GetWidthForCharacter(offset);

            int dataLength = GetHeightForCharacter(offset) * WidthToBytes(width);

            ushort dataOffset = (ushort)(GetAddressForCharacter(offset) - FileAttrs.AuxType);

            byte[] retval = new byte[dataLength];
            for (int idx = 0; idx < dataLength; idx++)
            {
                retval[idx] = DataBuf[dataOffset + idx];
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

            DataBuf = new byte[DataStream.Length];
            DataStream.Position = 0;
            DataStream.ReadExactly(DataBuf, 0, DataBuf.Length);
        }

        /// <summary>
        /// Checks to see if the data offsets of each character in the file point to an internal
        /// data block within the file.
        /// </summary>
        /// <returns>True, if the offsets are contained within the font file, False otherwise.</returns>
        private bool DataPointersAreValid()
        {
            Debug.Assert(DataBuf != null);
            Debug.Assert(DataBuf.Length > MINIMUM_HEADER_SIZE + PSC_FONT_HEADERS_SIZE);

            bool valid = true;

            for (byte idx = 1; idx < CHARACTER_COUNT && valid; idx++)
            {
                if (idx != IMAGE_CHARACTER)
                {  // We don't want to check the "Image" character @ offset 32.
                    ushort addr = GetAddressForCharacter(idx);

                    if (!(addr >= 0x6000 && addr < FileAttrs.AuxType + DataBuf.Length))
                    {
                        Debug.WriteLine("Address out of range for Character " + idx);
                    }

                    valid &= addr >= 0x6000 && addr < FileAttrs.AuxType + DataBuf.Length;
                }
            }

            if (!valid)
            {
                Debug.WriteLine("Valid is not set for this font.");
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
            Debug.Assert(DataBuf != null);
            Debug.Assert(DataBuf.Length > MINIMUM_HEADER_SIZE + PSC_FONT_HEADERS_SIZE);

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
                Debug.WriteLine(oversized + " character(s) are oversized}.");
            }

            return oversized;
        }
    }

}


