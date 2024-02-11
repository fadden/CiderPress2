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
using DiskArc.FS;

namespace FileConv.Gfx {
    /// <summary>
    /// Converts Fontrix fonts.
    /// </summary>
    public class FontrixFont : Converter {
        public const string TAG = "fontrixfont";
        public const string LABEL = "Fontrix Font";
        public const string DESCRIPTION =
            "Converts a Fontrix font file to a bitmap.  ";

        // TODO
        public const string DISCRIMINATOR = "DOS B with aux type $6400";
        public override string Tag => TAG;
        public override string Label => LABEL;
        public override string Description => DESCRIPTION;
        public override string Discriminator => DISCRIMINATOR;

        public const string OPT_MULTIROW = "multirow";
        public const string OPT_SPACING = "spacing";

        public override List<OptionDefinition> OptionDefs { get; protected set; } =
            new List<OptionDefinition>() {
                new (OPT_MULTIROW, "Display multiple rows",
                    OptionDefinition.OptType.Boolean, "true"),
                new (OPT_SPACING, "Show space between characters",
                    OptionDefinition.OptType.Boolean, "true"),
            };



        private const int CHAR_COUNT_LOCATION = 0x10;
        private const int FIRST_ASCII_CHAR_LOCATION = 0x11;
        private const int IS_PROPORTIONAL_LOCATION = 0x12;

        private const int ID_BYTE_1 = 0x18;
        private const int ID_BYTE_2 = 0x19;
        private const int ID_BYTE_3 = 0x1a;


        private const byte ID_BYTE_1_VAL = 0x90;
        private const byte ID_BYTE_2_VAL = 0xF7;
        private const byte ID_BYTE_3_VAL = 0xB2;


        private const byte MAX_NUM_CHARACTERS = 94;


        private const int NUM_GRID_ROWS = 6;


        const int INTER_CHARACTER_SPACING_PX = 2;
        const int LEFT_PAD = 2;
        const int RIGHT_PAD = 2;
        const int TOP_PADDING = 2;
        const int BOTTOM_PADDING = 2;



        // The file data.  Give it a minimum size up front so it shouldn't be null later on.
        private byte[] DataBuf = new byte[1];

        private FontrixFont() { }

        public FontrixFont(FileAttribs attrs, Stream? dataStream, Stream? rsrcStream,
                ResourceMgr? resMgr, ConvFlags convFlags, AppHook appHook)
                : base(attrs, dataStream, rsrcStream, resMgr, convFlags, appHook) {
            Applic = TestApplicability();
        }

        protected override Applicability TestApplicability() {
            if (DataStream == null) {
                return Applicability.Not;
            }

            if (DataStream.Length < ID_BYTE_3) {
                return Applicability.Not;
            }

            if (FileAttrs.FileType == FileAttribs.FILE_TYPE_BIN) {
                if (FileAttrs.AuxType == 0x6400) {

                    // Only need to read up to ID_BYTE_3 to validate. +1 to get size from offset.
                    var buffer = new byte[ID_BYTE_3 + 1];

                    DataStream.Position = 0;
                    DataStream.ReadExactly(buffer, 0, ID_BYTE_3 + 1);

                    bool valid = buffer[ID_BYTE_1] == ID_BYTE_1_VAL &&
                                 buffer[ID_BYTE_2] == ID_BYTE_2_VAL &&
                                 buffer[ID_BYTE_3] == ID_BYTE_3_VAL &&
                                 buffer[CHAR_COUNT_LOCATION] <= MAX_NUM_CHARACTERS;

                    if (valid) {
                        return Applicability.Yes;
                    } else {
                        Debug.WriteLine("Valid is not set for this font.");
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

            DataBuf = new byte[DataStream.Length];
            DataStream.Position = 0;
            DataStream.ReadExactly(DataBuf, 0, DataBuf.Length);

            bool doMultirow = GetBoolOption(options, OPT_MULTIROW, false);
            bool doSpacing = GetBoolOption(options, OPT_SPACING, false);

            if (doMultirow) {
                return ConvertIntoGrid(DataBuf, Palette8.Palette_MonoBW, doSpacing);
            } else {
                return Convert(DataBuf, Palette8.Palette_MonoBW, doSpacing);
            }
        }

        /// <summary>
        /// Converts a Fontrix font file to a B&amp;W bitmap.
        /// </summary>
        public Bitmap8 Convert(byte[] buf, Palette8 palette, bool doSpacing) {
            Bitmap8 output;

            int totalWidth = 0;
            int maxHeight = 0;

            int firstCharOffset = GetFirstCharacterOffset();
            var charCount = GetCharacterCount();

            for (int idx = firstCharOffset; idx < firstCharOffset + charCount; idx++) {

                if (idx != firstCharOffset && doSpacing) {
                    totalWidth += INTER_CHARACTER_SPACING_PX;
                }

                totalWidth += GetWidthForCharacter(idx);
                int height = GetFontHeight();
                if (height > maxHeight) { maxHeight = height; }

            }

            output = new Bitmap8(LEFT_PAD + totalWidth + RIGHT_PAD,
                                 TOP_PADDING + maxHeight + BOTTOM_PADDING);

            output.SetPalette(palette);

            for (int idx = 0; idx < output.Width; idx++) {
                for (int jdx = 0; jdx < output.Height; jdx++) {
                    output.SetPixelIndex(idx, jdx, 1);
                }
            }

            int xOffset = LEFT_PAD;
            for (int idx = firstCharOffset; idx < firstCharOffset + charCount; idx++) {

                byte charWidth = GetWidthForCharacter(idx);
                byte charHeight = GetFontHeight();
                byte[] charData = GetDataForCharacter(idx);

                int offset = 0;

                for (int row = 0; row < charHeight; row++) {
                    for (int col = 0; col < WidthToBytes(charWidth); col++) {
                        byte bval = charData[offset++];
                        for (int bit = 7; bit >= 0; bit--) {
                            // Bits are zero for background, one for foreground; leftmost pixel in LSB.
                            byte color = (byte)(1 - (bval >> 7));
                            var hpos = col * 8 + bit + xOffset;
                            if (hpos < totalWidth + LEFT_PAD + RIGHT_PAD) {
                                output.SetPixelIndex(hpos, row + TOP_PADDING, color);
                            }
                            bval <<= 1;
                        }
                    }
                }
                xOffset += charWidth;
                if (doSpacing) { xOffset += INTER_CHARACTER_SPACING_PX; }
            }
            return output;
        }

        /// <summary>
        /// Returns the number of characters in the current font.
        /// </summary>
        private byte GetCharacterCount() {
            Debug.Assert(DataBuf.Length > CHAR_COUNT_LOCATION);

            return DataBuf[CHAR_COUNT_LOCATION];
        }


        /// <summary>
        /// Returns the true if the current font is proportional, false otherwise.
        /// </summary>
        private bool IsFontProportional() {
            Debug.Assert(DataBuf.Length > IS_PROPORTIONAL_LOCATION);

            return DataBuf[IS_PROPORTIONAL_LOCATION] > 0;
        }


        /// <summary>
        /// Returns the ASCII code of the first character defined in the font.
        /// </summary>
        private byte GetFirstCharAsciiCode() {
            Debug.Assert(DataBuf.Length > FIRST_ASCII_CHAR_LOCATION);

            return DataBuf[FIRST_ASCII_CHAR_LOCATION];
        }


        /// <summary>
        /// Returns the offset in the internal pointer table for the first character in the font.
        /// It is calculated by subtracting 32 from the ASCII value of the first character.
        /// ASCII 32 is space, and there is a pointer assigned for space, but it is generally
        /// undefined or points to null data.  Per the documentation of Fontrix, the space 
        /// character is not used in practice.
        /// </summary>
        private byte GetFirstCharacterOffset() {
            const byte SPACE_VAL_OFFSET = 32;
            return (byte)(GetFirstCharAsciiCode() - SPACE_VAL_OFFSET);
        }



        /// <summary>
        /// This is a utility function for drawing the font bitmap. It returns the total number 
        /// of pixels a series of encoded characters in the font require to display, taking into
        /// consideration whether the font is proportional and whether the user wants to display
        /// inter-character spacing. 
        /// </summary>
        private int GetWidthForRun(byte start, byte length, bool doSpacing) {

            int totalWidth = 0;

            for (int idx = start; idx < start + length; idx++) {

                if (idx != start && doSpacing) totalWidth += INTER_CHARACTER_SPACING_PX;

                totalWidth += GetWidthForCharacter(idx);
            }
            return totalWidth;
        }


        /// <summary>
        /// Converts a Fontrix font file to a multi-row grid on a B&amp;W bitmap.
        /// </summary>
        public Bitmap8 ConvertIntoGrid(byte[] buf, Palette8 palette, bool doSpacing) {
            Bitmap8 output;

            var charCount = GetCharacterCount();

            // If there aren't at least NUM_GRID_ROW items, default to the linear display.
            if (charCount < NUM_GRID_ROWS) {
                return Convert(buf, palette, doSpacing);
            }

            byte startingChar = GetFirstCharacterOffset();

            int fullWidth = GetWidthForRun(startingChar, charCount, doSpacing);

            int idealRowWidth = fullWidth / NUM_GRID_ROWS;

            int spacingVal = doSpacing ? INTER_CHARACTER_SPACING_PX : 0;

            var charCounts = new byte[NUM_GRID_ROWS];
            var rowWidths = new int[NUM_GRID_ROWS];
            int currRow = 0;

            for (int idx = startingChar; idx < startingChar + charCount; idx++) {

                int thisCharWidth = GetWidthForCharacter(idx);
                charCounts[currRow]++;
                rowWidths[currRow] += thisCharWidth + spacingVal;

                if (rowWidths[currRow] >= idealRowWidth && currRow != NUM_GRID_ROWS - 1) {
                    currRow++;
                }
            }

            int totalWidth = 0;
            foreach (var w in rowWidths) {
                totalWidth = Math.Max(totalWidth, w);
            }

            int maxHeight = GetFontHeight() * NUM_GRID_ROWS +
                            INTER_CHARACTER_SPACING_PX * (NUM_GRID_ROWS - 1);

            output = new Bitmap8(LEFT_PAD + totalWidth + RIGHT_PAD,
                                 TOP_PADDING + maxHeight + BOTTOM_PADDING);

            output.SetPalette(palette);

            // Clear the bitmap
            for (int idx = 0; idx < output.Width; idx++) {
                for (int jdx = 0; jdx < output.Height; jdx++) {
                    output.SetPixelIndex(idx, jdx, 1);
                }
            }

            int currCharNum = startingChar;

            int yOffset = TOP_PADDING;

            for (var rowNum = 0; rowNum < NUM_GRID_ROWS; rowNum++) {

                int xOffset = LEFT_PAD;

                for (int idx = 0; idx < charCounts[rowNum]; idx++) {
                    byte charWidth = GetWidthForCharacter(currCharNum);
                    byte charHeight = GetFontHeight();
                    byte[] charData = GetDataForCharacter(currCharNum);

                    int offset = 0;

                    for (int row = 0; row < charHeight; row++) {
                        for (int col = 0; col < WidthToBytes(charWidth); col++) {
                            byte bval = charData[offset++];
                            for (int bit = 7; bit >= 0; bit--) {
                                // Bits are zero for background, one for foreground; leftmost pixel in LSB.
                                byte color = (byte)(1 - (bval >> 7));
                                var hpos = col * 8 + bit + xOffset;
                                if (hpos < totalWidth + LEFT_PAD + RIGHT_PAD) {
                                    output.SetPixelIndex(hpos, row + yOffset, color);
                                }
                                bval <<= 1;
                            }
                        }
                    }
                    currCharNum++;
                    xOffset += charWidth;
                    if (doSpacing) { xOffset += INTER_CHARACTER_SPACING_PX; }
                }
                yOffset += INTER_CHARACTER_SPACING_PX + GetFontHeight();
            }
            return output;
        }

        /// <summary>
        /// Get the height in pixels for all characters in the font.
        /// </summary>
        /// <returns>The pixel height of all characters.</returns>
        private byte GetFontHeight() {
            Debug.Assert(DataBuf != null);
            return DataBuf[0x14];
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
        /// between the 0th position and the hightest bit position in the byte.
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
        /// non=proportional fonts
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
            var byteWidth = (byte)(8 * (a2 - a1) / GetFontHeight()); ;

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
            byte loVal = DataBuf[0x20 + (2 * charOffset)];
            byte hiVal = DataBuf[0x21 + (2 * charOffset)];
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
                retval[idx] = DataBuf[dataOffset + idx];
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


