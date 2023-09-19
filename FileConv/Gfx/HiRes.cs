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
    /// Converts an uncompressed Apple II hi-res screen image.
    /// </summary>
    public class HiRes : Converter {
        public const string TAG = "hgr";
        public const string LABEL = "Hi-Res Graphics";
        public const string DESCRIPTION =
            "Converts a standard hi-res screen to a bitmap.";
        public const string DISCRIMINATOR =
            "ProDOS FOT with auxtype < $4000, BIN, or DOS B. " +
            "Length between $1ff8 and $2001, inclusive.";
        public override string Tag => TAG;
        public override string Label => LABEL;
        public override string Description => DESCRIPTION;
        public override string Discriminator => DISCRIMINATOR;

        public const string OPT_BW = "bw";

        public override List<OptionDefinition> OptionDefs { get; protected set; } =
            new List<OptionDefinition>() {
                new OptionDefinition(OPT_BW, "Black & white",
                    OptionDefinition.OptType.Boolean, "false"),
            };

        private const int EXPECTED_LEN = 8192;
        private const int MIN_LEN = EXPECTED_LEN - 8;
        private const int MAX_LEN = EXPECTED_LEN + 1;

        private HiRes() { }

        public HiRes(FileAttribs attrs, Stream? dataStream, Stream? rsrcStream,
                ResourceMgr? resMgr, ConvFlags convFlags, AppHook appHook)
                : base(attrs, dataStream, rsrcStream, resMgr, convFlags, appHook) {
            Applic = TestApplicability();
        }

        protected override Applicability TestApplicability() {
            if (DataStream == null || IsRawDOS) {
                return Applicability.Not;
            }
            if (DataStream.Length < MIN_LEN || DataStream.Length > MAX_LEN) {
                return Applicability.Not;
            }
            if (FileAttrs.FileType == FileAttribs.FILE_TYPE_BIN) {
                return Applicability.Probably;
            } else if (FileAttrs.FileType == FileAttribs.FILE_TYPE_FOT) {
                if (FileAttrs.AuxType < 0x4000) {      // 0x0000-3fff is uncompressed hgr/dhgr
                    return Applicability.Probably;
                }
            }
            return Applicability.Not;
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

            bool doMonochrome = GetBoolOption(options, OPT_BW, false);
            return ConvertBuffer(fullBuf, Palette8.Palette_HiRes, doMonochrome);
        }

        #region Common

        private const int BLACK0 = 0;
        private const int GREEN = 1;
        private const int PURPLE = 2;
        private const int WHITE0 = 3;
        private const int BLACK1 = 4;
        private const int ORANGE = 5;
        private const int BLUE = 6;
        private const int WHITE1 = 7;

        // Output is pixel-doubled so we can render half-pixel shifts.
        public const int OUTPUT_WIDTH = 560;
        public const int OUTPUT_HEIGHT = 384;

        private const int NUM_BYTES_PER_ROW = 40;
        private const int NUM_COLS = NUM_BYTES_PER_ROW * 7;     // 280
        private const int NUM_ROWS = 192;

        // Storage for unpacked hi-res lines.  We pad a little on the start and end so we
        // can skip range checks when looking backward or drawing off the end.
        private const int OVER = 2;     // padding

        /// <summary>
        /// Converts an 8KB hi-res screen capture to an 8-color 560x384 bitmap.  We double
        /// every pixel so that we can emulate the half-pixel shift effect.
        /// </summary>
        /// <param name="buf">Buffer of hi-res data.</param>
        /// <param name="palette">Color palette.</param>
        /// <param name="doMonochrome">True if we want a B&W conversion, false for color.</param>
        /// <returns>Rendered bitmap.</returns>
        internal static Bitmap8 ConvertBuffer(byte[] buf, Palette8 palette, bool doMonochrome) {
            Bitmap8 output = new Bitmap8(OUTPUT_WIDTH, OUTPUT_HEIGHT);
            output.IsDoubled = true;
            output.SetPalette(palette);

            bool[] lineBits = new bool[OVER + NUM_COLS + OVER];
            bool[] hiFlags = new bool[OVER + NUM_COLS + OVER];
            int[] colorBuf = new int[(OVER + NUM_COLS + OVER) * 2];

            for (int row = 0; row < NUM_ROWS; row++) {
                // Unravel the bits into an array of boolean values.  This makes the color
                // conversion logic simpler, because we don't have to dodge the high bits.
                int rowOffset = RowToOffset(row);
                int idx = OVER;
                for (int col = 0; col < NUM_BYTES_PER_ROW; col++) {
                    byte val = buf[rowOffset + col];
                    bool isHiBitSet = (val & 0x80) != 0;

                    for (int bit = 0; bit < 7; bit++) {
                        hiFlags[idx] = isHiBitSet;     // set same way for all 7 bits
                        lineBits[idx] = (val & 0x01) != 0;
                        idx++;
                        val >>= 1;
                    }
                }

                Array.Clear(colorBuf);     // clear to zero (BLACK0)
                //for (int i = 0; i < mColorBuf.Length; i++) {
                //    mColorBuf[i] = ORANGE;
                //}

                // Walk through 280 columns, setting 560 color values.
                for (int col = 0; col < NUM_COLS; col++) {
                    int inIdx = OVER + col;
                    // Color is +4 if the high bit was set for the byte this column is in.
                    int colorShift = hiFlags[inIdx] ? 4 : 0;
                    // Apply half-pixel shift to output.  This can cause some pixels to be
                    // unwritten (leaving them set to BLACK0), and write one pixel past the end.
                    int outIdx = (OVER + col) * 2 + (hiFlags[inIdx] ? 1 : 0);

                    if (doMonochrome) {
                        // We keep the half-pixel shift here. (To simulate IIgs RGB, undo it.)
                        if (!lineBits[inIdx]) {
                            colorBuf[outIdx] = colorBuf[outIdx + 1] = BLACK0 + colorShift;
                        } else {
                            colorBuf[outIdx] = colorBuf[outIdx + 1] = WHITE0 + colorShift;
                        }
                    } else {
                        if (!lineBits[inIdx]) {
                            // Bit not set, draw black.
                            colorBuf[outIdx] = colorBuf[outIdx + 1] = BLACK0 + colorShift;
                        } else {
                            if (colorBuf[outIdx - 2] != BLACK0 &&
                                    colorBuf[outIdx - 2] != BLACK1) {
                                // Previous bit was set, draw white.
                                colorBuf[outIdx] = colorBuf[outIdx + 1] = WHITE0 + colorShift;

                                // Previous pixel is part of a run of white.
                                colorBuf[outIdx - 2] = colorBuf[outIdx - 1] = WHITE0 + colorShift;
                            } else {
                                // Previous bit was clear, draw a color.  The color depends on
                                // whether this is an odd or even byte.
                                if ((inIdx & 0x01) != 0) {
                                    colorBuf[outIdx] = colorBuf[outIdx + 1] = GREEN + colorShift;
                                } else {
                                    colorBuf[outIdx] = colorBuf[outIdx + 1] = PURPLE + colorShift;
                                }
                            }

                            // A run of (say) green pixels will appear to be alternating green
                            // and black lines.  We want to look back at the pixel before last
                            // and see if it's the same color.  If so, set the previous pixel
                            // to match our color.
                            //
                            // We also want to do this if the pixel-before-last was white.  This
                            // is unnecessary (but harmless) if we're in a run of white.
                            if (colorBuf[outIdx - 4] == colorBuf[outIdx] ||
                                    colorBuf[outIdx - 4] == WHITE0 ||
                                    colorBuf[outIdx - 4] == WHITE1) {
                                // Back-fill previous gap with color.
                                colorBuf[outIdx - 2] = colorBuf[outIdx - 1] = colorBuf[outIdx];
                            }
                        }
                    }
                }

                // Copy color buffer contents to the bitmap.  We're doubling pixels, so set
                // values on two adjacent rows.
                for (int col = 0; col < NUM_COLS * 2; col++) {
                    byte colorIdx = (byte)colorBuf[OVER * 2 + col];
                    output.SetPixelIndex(col, row * 2, colorIdx);
                    output.SetPixelIndex(col, row * 2 + 1, colorIdx);
                }
            }

            return output;
        }

        /// <summary>
        /// Computes the offset of the Nth row of a hi-res image.
        /// </summary>
        /// <param name="row">Row number (0-191).</param>
        /// <returns>Buffer offset (0-8191).</returns>
        public static int RowToOffset(int row) {
            if (row < 0 || row > NUM_ROWS) {
                throw new ArgumentOutOfRangeException(nameof(row));
            }
            // If row is ABCDEFGH, we want pppFGHCD EABAB000 (where p would be $20/$40).
            int low = ((row & 0xc0) >> 1) | ((row & 0xc0) >> 3) | ((row & 0x08) << 4);
            int high = ((row & 0x07) << 2) | ((row & 0x30) >> 4);
            return (high << 8) | low;
        }

        #endregion Common
    }
}
