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
using System.Diagnostics;

using CommonUtil;
using DiskArc;

namespace FileConv.Gfx {
    /// <summary>
    /// Apple IIgs super hi-res screen image.
    /// </summary>
    /// <remarks>
    /// <para>This handles the basic uncompressed screen dump file format, and provides some
    /// general routines for use by other converters.</para>
    /// </remarks>
    public class SuperHiRes : Converter {
        public const string TAG = "shr";
        public const string LABEL = "Super Hi-Res Screen Image";
        public const string DESCRIPTION =
            "Converts a standard Apple IIgs super hi-res image to a bitmap.";
        public const string DISCRIMINATOR =
            "ProDOS PIC/$0000, 32KB. May be BIN with extension \".PIC\" or \".SHR.";
        public override string Tag => TAG;
        public override string Label => LABEL;
        public override string Description => DESCRIPTION;
        public override string Discriminator => DISCRIMINATOR;

        public const int EXPECTED_LEN = 32768;


        private SuperHiRes() { }

        public SuperHiRes(FileAttribs attrs, Stream? dataStream, Stream? rsrcStream,
                ResourceMgr? resMgr, ConvFlags convFlags, AppHook appHook)
                : base(attrs, dataStream, rsrcStream, resMgr, convFlags, appHook) {
            Applic = TestApplicability();
        }

        protected override Applicability TestApplicability() {
            if (DataStream == null) {
                return Applicability.Not;
            }
            // Must be 32KB or we won't know what to do with it.
            if (DataStream.Length != EXPECTED_LEN) {
                return Applicability.Not;
            }
            // Official definition is 32KB PIC/$0000.
            if (FileAttrs.FileType == FileAttribs.FILE_TYPE_PIC && FileAttrs.AuxType == 0x0000) {
                return Applicability.Yes;
            }
            // Sometimes the aux type is set wrong.
            if (FileAttrs.FileType == FileAttribs.FILE_TYPE_PIC) {
                return Applicability.Probably;
            }
            // Sometimes the file type is totally wrong, but they try to make up for it with a
            // filename extension.
            string ext = Path.GetExtension(FileAttrs.FileNameOnly).ToLowerInvariant();
            if (FileAttrs.FileType == FileAttribs.FILE_TYPE_BIN &&
                    (ext == ".pic" || ext == ".shr")) {
                return Applicability.Probably;
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

            byte[] fullBuf = new byte[EXPECTED_LEN];
            DataStream.Position = 0;
            DataStream.ReadExactly(fullBuf, 0, Math.Min(EXPECTED_LEN, (int)DataStream.Length));
            return ConvertBuffer(fullBuf);
        }

        #region Common

        // Output is pixel-doubled so we can handle 320 mode and 640 mode.
        private const int OUTPUT_WIDTH = 640;
        private const int OUTPUT_HEIGHT = 400;
        private const int PALETTE_LENGTH = 256;

        private const int BYTE_WIDTH = 160;             // number of bytes per row
        private const int NUM_COLS = BYTE_WIDTH * 4;    // so we can handle 320 mode and 640 mode
        private const int NUM_ROWS = 200;               // source rows; will be doubled for output


        /// <summary>
        /// Converts a 32K screen capture to a bitmap.  This works for 256-color images, but
        /// not for 3200-color images.
        /// </summary>
        internal static Bitmap8 ConvertBuffer(byte[] buf) {
            Bitmap8 output = new Bitmap8(OUTPUT_WIDTH, OUTPUT_HEIGHT);
            output.IsDoubled = true;

            // Extract the color palette.
            // TODO: we could reduce the size of the output by omitting palettes that aren't
            //   actually used.  A simple optimization would be to scan the SCBs to see if the
            //   entire image uses a single palette, which should be fairly common.
            int offset = 0x7e00;
            int[] colors = new int[PALETTE_LENGTH];
            for (int i = 0; i < PALETTE_LENGTH; i++) {
                colors[i] = ARGBFromSHR(buf[offset + i * 2], buf[offset + i * 2 + 1]);
            }
            output.SetPalette(new Palette8("SHR Screen", colors));

            // Extract the SCBs.
            offset = 0x7d00;
            byte[] scbs = new byte[NUM_ROWS];
            for (int i = 0; i < NUM_ROWS; i++) {
                scbs[i] = buf[offset + i];
            }

            // Convert the pixel data.
            offset = 0;
            byte[] colorBuf = new byte[NUM_COLS];
            for (int row = 0; row < NUM_ROWS; row++) {
                UnpackSCB(scbs[row], out int paletteNum, out bool isFill, out bool is640);
                ConvertLine(buf, offset, BYTE_WIDTH, isFill, is640, colorBuf, 0);

                // Copy 640 pixels to bitmap.  In 320 mode we need to double up.
                int colorOffset = paletteNum << 4;
                for (int col = 0; col < NUM_COLS; col++) {
                    byte colorIndex;
                    if (is640) {
                        colorIndex = colorBuf[col];
                    } else {
                        colorIndex = colorBuf[col / 2];
                    }
                    // Factor in the palette index, and set the pixels.
                    colorIndex = (byte)(colorIndex | colorOffset);
                    output.SetPixelIndex(col, row * 2, colorIndex);
                    output.SetPixelIndex(col, row * 2 + 1, colorIndex);
                }
                offset += BYTE_WIDTH;
            }

            return output;
        }

        /// <summary>
        /// Converts a single row of SHR pixel data.  The output buffer must be able to hold
        /// (byteCount * 2) or (byteCount * 4) color indices, depending on the
        /// <paramref name="is640"/> setting.
        /// </summary>
        /// <remarks>
        /// <para>This always takes a whole number of bytes, even if the source material has
        /// an odd width.  The output is a color table index, not a color value.</para>
        /// </remarks>
        /// <param name="pixelData">Pixel data buffer.</param>
        /// <param name="pdOffset">Offset to start of current row in pixel data buffer.</param>
        /// <param name="byteCount">Number of bytes in this row (usually 160).</param>
        /// <param name="isFill">True if fill mode is enabled.</param>
        /// <param name="is640">True if 640 mode is enabled.</param>
        /// <param name="colorBuf">Buffer that receives color index values (0-15).</param>
        /// <param name="cbOffset">Offset of first entry in color index output buffer.</param>
        public static void ConvertLine(byte[] pixelData, int pdOffset, int byteCount,
                bool isFill, bool is640, byte[] colorBuf, int cbOffset) {
            int prevColor = 0;
            for (int i = 0; i < byteCount; i++) {
                byte val = pixelData[pdOffset + i];
                if (is640) {
                    colorBuf[cbOffset + i * 4] = (byte)(((val >> 6) & 0x03) + 8);
                    colorBuf[cbOffset + i * 4 + 1] = (byte)(((val >> 4) & 0x03) + 12);
                    colorBuf[cbOffset + i * 4 + 2] = (byte)((val >> 2) & 0x03);
                    colorBuf[cbOffset + i * 4 + 3] = (byte)((val & 0x03) + 4);
                } else {
                    int color0 = (val >> 4) & 0x0f;
                    int color1 = val & 0x0f;
                    if (isFill) {
                        //Debug.Assert(false, "found an image with fill mode!");
                        if (color0 == 0) {
                            color0 = prevColor;
                        } else {
                            prevColor = color0;
                        }
                        if (color1 == 0) {
                            color1 = prevColor;
                        } else {
                            prevColor = color1;
                        }
                    }
                    colorBuf[cbOffset + i * 2] = (byte)color0;
                    colorBuf[cbOffset + i * 2 + 1] = (byte)color1;
                }
            }
        }

        /// <summary>
        /// Unpacks an SCB byte.
        /// </summary>
        /// <param name="scb">SCB value.</param>
        /// <param name="paletteNum">Result: palette number (0-15).</param>
        /// <param name="isFill">Result: true if this line is using fill mode.</param>
        /// <param name="is640">Result: true if this line is using 640 mode.</param>
        public static void UnpackSCB(byte scb, out int paletteNum, out bool isFill,
                out bool is640) {
            paletteNum = scb & 0x0f;
            // 0x10 is reserved
            isFill = (scb & 0x20) != 0;
            // 0x40 is interrupt enable
            is640 = (scb & 0x80) != 0;
        }

        /// <summary>
        /// Converts a two-byte SHR color palette entry to a 32-bit ARGB value.
        /// </summary>
        /// <param name="lo">Low (even) byte from palette.</param>
        /// <param name="hi">High (odd) byte from palette.</param>
        /// <returns>ARGB value.</returns>
        public static int ARGBFromSHR(byte lo, byte hi) {
            return ARGBFromSHR((ushort)(lo | (hi << 8)));
        }

        /// <summary>
        /// Converts a 16-bit SHR color palette entry to a 32-bit ARGB value.
        /// </summary>
        /// <remarks>
        /// <para>Each color is a 4-bit value, which we want to convert to an 8-bit value.
        /// For example, $7 becomes $77.  We can do this by masking/shifting/ORing, or by
        /// multiplying the value by 17.</para>
        /// <para>The alpha channel will be set to opaque (0xff).</para>
        /// </remarks>
        /// <param name="gsc">16-bit value from palette.</param>
        /// <returns>ARGB value.</returns>
        public static int ARGBFromSHR(ushort gsc) {
            // desired result is AARRGGBB; form 0A0R0G0B from 0RGB, shift, merge
            int val = 0x0f000000 | ((gsc & 0x0f00) << 8) | ((gsc & 0xf0) << 4) | (gsc & 0x0f);
            val |= val << 4;
            return val;
        }

        #endregion Common
    }
}
