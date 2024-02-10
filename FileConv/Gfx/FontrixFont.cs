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


        private const int ID_BYTE_1 = 0x18;
        private const byte ID_BYTE_1_VAL = 0x90;
        private const int ID_BYTE_2 = 0x19;
        private const byte ID_BYTE_2_VAL = 0xF7;
        private const int ID_BYTE_3 = 0x1a;
        private const byte ID_BYTE_3_VAL = 0xB2;


        const int CHAR_COUNT_LOCATION = 0x10;
        const int FIRST_ASCII_CHAR_LOCATION = 0x11;
        const int IS_PROPORTIONAL_LOCATION = 0x12;

        const int INTER_CHARACTER_SPACING_PX = 3;


        const int LEFT_PADDING = 2;
        const int RIGHT_PADDING = 2;
        const int TOP_PADDING = 2;
        const int BOTTOM_PADDING = 2;


        // We have to unpack the data in order to validate it, so might as well keep a copy of it for later.
        private byte[]? DataBuf;

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

            if (FileAttrs.FileType == FileAttribs.FILE_TYPE_BIN) {
                if (FileAttrs.AuxType == 0x6400) {
                    UnpackData();


                    bool valid = DataBuf?[ID_BYTE_1] == ID_BYTE_1_VAL &&
                                 DataBuf[ID_BYTE_2] == ID_BYTE_2_VAL &&
                                 DataBuf[ID_BYTE_3] == ID_BYTE_3_VAL;

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

            Debug.Assert(DataBuf != null);
            Debug.Assert(DataBuf.Length >= 0);
            Debug.Assert(DataStream != null);

            bool doMultirow = GetBoolOption(options, OPT_MULTIROW, false);

            bool doSpacing = GetBoolOption(options, OPT_SPACING, false);

            if (doMultirow) {
                return ConvertMonoIntoGrid(DataBuf, Palette8.Palette_MonoBW, doSpacing);
            } else {
                return ConvertMono(DataBuf, Palette8.Palette_MonoBW, doSpacing);
            }
        }

        /// <summary>
        /// Converts a Fontrix font file to a B&amp;W bitmap.
        /// </summary>
        public Bitmap8 ConvertMono(byte[] buf, Palette8 palette, bool doSpacing) {
            Bitmap8 output;

            int totalWidth = 0;
            int maxHeight = 0;

            bool isProportional = buf[IS_PROPORTIONAL_LOCATION] > 0;

            int firstCharOffset = buf[FIRST_ASCII_CHAR_LOCATION] - 32;
            var charCount = buf[CHAR_COUNT_LOCATION];

            for (int idx = firstCharOffset; idx < firstCharOffset + charCount; idx++) {


                if (idx != firstCharOffset && doSpacing) totalWidth += INTER_CHARACTER_SPACING_PX;
                if (isProportional) {
                    totalWidth += GetMeasuredWidthForCharacter(idx);
                } else {
                    totalWidth += GetWidthForCharacter(idx);
                }
                int height = GetFontHeight();
                if (height > maxHeight) { maxHeight = height; }

            }


            output = new Bitmap8(LEFT_PADDING + totalWidth + RIGHT_PADDING,
                                 TOP_PADDING + maxHeight + BOTTOM_PADDING);


            output.SetPalette(palette);

            for (int idx = 0; idx < output.Width; idx++) {
                for (int jdx = 0; jdx < output.Height; jdx++) {
                    output.SetPixelIndex(idx, jdx, 1);
                }
            }


            int xOffset = LEFT_PADDING;
            for (int idx = firstCharOffset; idx < firstCharOffset + charCount; idx++) {


                byte charWidth =
                    isProportional ? GetMeasuredWidthForCharacter(idx) : GetWidthForCharacter(idx);
                byte charHeight = GetFontHeight();
                byte[] charData = GetDataForCharacter(idx);

                int offset = 0;

                for (int row = 0; row < charHeight; row++) {
                    for (int col = 0; col < WidthToBytes(charWidth); col++) {
                        byte bval = charData[offset++];
                        for (int bit = 7; bit >= 0; bit--) {
                            // Bits are zero for background, one for foreground; leftmost pixel in LSB.
                            byte color = (byte)(1 - (bval >> 7));

                            if (col * 8 + bit + xOffset < totalWidth + LEFT_PADDING + RIGHT_PADDING) {


                                output.SetPixelIndex(col * 8 + bit + xOffset, row + TOP_PADDING, color);

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
        /// Converts a Fontrix font file to a B&amp;W bitmap.
        /// </summary>
        public Bitmap8 ConvertMonoIntoGrid(byte[] buf, Palette8 palette, bool doSpacing) {
            Bitmap8 output;

            int totalWidth = 0;
            int maxHeight = 0;

            var charCount = buf[CHAR_COUNT_LOCATION];

            // IF there aren't 3 items, there's no need to split them into three rows.
            if (charCount <= 3) {
                return ConvertMono(buf, palette, doSpacing);
            }

            bool isProportional = buf[IS_PROPORTIONAL_LOCATION] > 0;

            int firstCharOffset = buf[FIRST_ASCII_CHAR_LOCATION] - 32;

            for (int idx = firstCharOffset; idx < firstCharOffset + charCount; idx++) {


                if (idx != firstCharOffset && doSpacing) totalWidth += INTER_CHARACTER_SPACING_PX;
                if (isProportional) {
                    totalWidth += GetMeasuredWidthForCharacter(idx);
                } else {
                    totalWidth += GetWidthForCharacter(idx);
                }
                int height = GetFontHeight();
                if (height > maxHeight) { maxHeight = height; }

            }


            output = new Bitmap8(LEFT_PADDING + totalWidth + RIGHT_PADDING,
                                 TOP_PADDING + maxHeight + BOTTOM_PADDING);



            output.SetPalette(palette);

            for (int idx = 0; idx < output.Width; idx++) {
                for (int jdx = 0; jdx < output.Height; jdx++) {
                    output.SetPixelIndex(idx, jdx, 1);
                }
            }


            int xOffset = LEFT_PADDING;
            for (int idx = firstCharOffset; idx < firstCharOffset + charCount; idx++) {


                byte charWidth =
                    isProportional ? GetMeasuredWidthForCharacter(idx) : GetWidthForCharacter(idx);
                byte charHeight = GetFontHeight();
                byte[] charData = GetDataForCharacter(idx);

                int offset = 0;

                for (int row = 0; row < charHeight; row++) {
                    for (int col = 0; col < WidthToBytes(charWidth); col++) {
                        byte bval = charData[offset++];
                        for (int bit = 7; bit >= 0; bit--) {
                            // Bits are zero for background, one for foreground; leftmost pixel in LSB.
                            byte color = (byte)(1 - (bval >> 7));

                            if (col * 8 + bit + xOffset < totalWidth + LEFT_PADDING + RIGHT_PADDING) {


                                output.SetPixelIndex(col * 8 + bit + xOffset, row + TOP_PADDING, color);

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
        /// Get the height in pixels for all characters in the font.
        /// </summary>
        /// <returns>The pixel height of all characters.</returns>
        private byte GetFontHeight() {
            Debug.Assert(DataBuf != null);
            return DataBuf[0x14];
        }



        private static byte MeasureByteWidth(byte val) {
            if (val >= 128) { return 8; }
            if (val >= 64) { return 7; }
            if (val >= 32) { return 6; }
            if (val >= 16) { return 5; }
            if (val >= 8) { return 4; }
            if (val >= 4) { return 3; }
            if (val >= 2) { return 2; }
            if (val >= 1) { return 1; }
            return 0;
        }

        /// <summary>
        /// Get the width in pixels for the character at the  given offset.
        /// </summary>
        /// <param name="offset">The character width to fetch</param>
        /// <returns>The pixel width of the character within the file.</returns>
        private byte GetWidthForCharacter(int offset) {
            Debug.Assert(DataBuf != null);

            var a1 = GetDataOffsetForCharacter(offset);
            var a2 = GetDataOffsetForCharacter(offset + 1);
            var byteWidth = (byte)(8 * (a2 - a1) / GetFontHeight()); ;

            return byteWidth;
        }


        private byte GetMeasuredWidthForCharacter(int offset) {
            Debug.Assert(DataBuf != null);

            var height = GetFontHeight();
            var byteWidth = GetWidthForCharacter(offset) / 8;
            var data = GetDataForCharacter(offset);

            byte width = 0;

            for (var h = 0; h < height; h++) {
                for (var w = 0; w < byteWidth; w++) {
                    var b = data[h * byteWidth + w];
                    byte thisbyte = (byte)(MeasureByteWidth(b) + (w * 8));
                    if (thisbyte > width) { width = thisbyte; };
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
            Debug.Assert(DataBuf != null);

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
            Debug.Assert(DataBuf != null);

            byte width = GetWidthForCharacter(offset);

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

        /// <summary>
        ///  Reads the data from the datastream into a byte array.  
        /// </summary>
        private void UnpackData() {
            Debug.Assert(DataStream != null);

            DataBuf = new byte[DataStream.Length];
            DataStream.Position = 0;
            DataStream.ReadExactly(DataBuf, 0, DataBuf.Length);
        }

    }

}


