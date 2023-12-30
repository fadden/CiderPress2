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
    /// Converts an uncompressed Apple II double-hi-res screen image.
    /// </summary>
    public class DoubleHiRes : Converter {
        public const string TAG = "dhgr";
        public const string LABEL = "Double Hi-Res Graphics";
        public const string DESCRIPTION =
            "Converts a double-hi-res screen to a bitmap.";
        public const string DISCRIMINATOR =
            "ProDOS FOT with auxtype < $4000, BIN, or DOS B.  Length $4000.";
        public override string Tag => TAG;
        public override string Label => LABEL;
        public override string Description => DESCRIPTION;
        public override string Discriminator => DISCRIMINATOR;

        public const string OPT_CONV = "conv";

        public const string CONV_MODE_BW = "bw";
        public const string CONV_MODE_LATCHED = "latch";
        public const string CONV_MODE_WINDOW = "window";
        public const string CONV_MODE_SIMPLE = "simple";

        public static readonly string[] ConvModeTags = new string[] {
            CONV_MODE_BW, CONV_MODE_LATCHED, CONV_MODE_WINDOW, CONV_MODE_SIMPLE
        };
        public static readonly string[] ConvModeDescrs = new string[] {
            "Black & White", "Latched", "Sliding Window", "Simple 140"
        };
        internal enum ColorConvMode {
            Unknown = 0,
            BlackWhite,
            Latched,
            SlidingWindow,
            Simple,
        }

        public override List<OptionDefinition> OptionDefs { get; protected set; } =
            new List<OptionDefinition>() {
                new OptionDefinition(OPT_CONV, "Color Conversion",
                    OptionDefinition.OptType.Multi, CONV_MODE_LATCHED,
                    ConvModeTags, ConvModeDescrs),
            };

        private const int PAGE_SIZE = 8192;
        private const int EXPECTED_LEN = PAGE_SIZE * 2;
        private const int MIN_PB_LEN = ((EXPECTED_LEN + 255) / 256) * 2;
        private const int MAX_PB_LEN = EXPECTED_LEN;        // assume compression succeeded
        private const int NUM_ROWS = 192;
        private const int PIXELS_PER_ROW = 560;
        private const int NUM_COLORS = 16;
        private const int MAX_LOOK = 4;

        private const int OUTPUT_WIDTH = PIXELS_PER_ROW;
        private const int OUTPUT_HEIGHT = NUM_ROWS * 2;


        private DoubleHiRes() { }

        public DoubleHiRes(FileAttribs attrs, Stream? dataStream, Stream? rsrcStream,
                ResourceMgr? resMgr, ConvFlags convFlags, AppHook appHook)
                : base(attrs, dataStream, rsrcStream, resMgr, convFlags, appHook) {
            Applic = TestApplicability();
        }

        protected override Applicability TestApplicability() {
            if (DataStream == null || IsRawDOS) {
                return Applicability.Not;
            }
            // Check for PackBytes-compressed hi-res.
            if (FileAttrs.FileType == FileAttribs.FILE_TYPE_FOT && FileAttrs.AuxType == 0x4001 &&
                    DataStream.Length >= MIN_PB_LEN && DataStream.Length <= MAX_PB_LEN) {
                return Applicability.Probably;           // 0x4001 is PackBytes-compressed dhgr
            }

            // Primary indicator is file length.  Must be $2000.
            if (DataStream.Length != EXPECTED_LEN) {
                return Applicability.Not;
            }
            if (FileAttrs.FileType == FileAttribs.FILE_TYPE_BIN) {
                return Applicability.Probably;
            } else if (FileAttrs.FileType == FileAttribs.FILE_TYPE_FOT) {
                if (FileAttrs.AuxType < 0x4000) {      // 0x0000-3fff is uncompressed hgr/dhgr
                    return Applicability.Yes;
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

            byte[] fullBuf = new byte[EXPECTED_LEN];
            DataStream.Position = 0;
            if (FileAttrs.FileType == FileAttribs.FILE_TYPE_FOT && FileAttrs.AuxType == 0x4001) {
                byte[] tmpBuf = new byte[DataStream.Length];
                DataStream.ReadExactly(tmpBuf, 0, tmpBuf.Length);
                int outLen = ApplePack.UnpackBytes(tmpBuf, 0, tmpBuf.Length, fullBuf, 0,
                    out bool decompErr);
                if (decompErr || outLen != EXPECTED_LEN) {
                    return new ErrorText("Unable to decompress data.");
                }
            } else {
                DataStream.ReadExactly(fullBuf, 0, Math.Min(EXPECTED_LEN, (int)DataStream.Length));
            }

            ColorConvMode convMode =
                OptToEnum(GetStringOption(options, OPT_CONV, CONV_MODE_LATCHED));
            return ConvertBuffer(fullBuf, Palette8.Palette_DoubleHiRes, convMode);
        }

        // Converts a "conv=xyz" option value to an enumerated value.
        private static ColorConvMode OptToEnum(string opt) {
            switch (opt) {
                case CONV_MODE_BW:
                    return ColorConvMode.BlackWhite;
                case CONV_MODE_LATCHED:
                    return ColorConvMode.Latched;
                case CONV_MODE_WINDOW:
                    return ColorConvMode.SlidingWindow;
                case CONV_MODE_SIMPLE:
                    return ColorConvMode.Simple;
                default:
                    return ColorConvMode.BlackWhite;
            }
        }

        /// <summary>
        /// Converts a 16KB double-hi-res screen capture to a 16-color 560x384 bitmap.  We double
        /// every pixel vertically to retain the original proportions.
        /// </summary>
        /// <param name="buf">Buffer of double-hi-res data.</param>
        /// <param name="palette">Color palette.</param>
        /// <param name="convMode">Color conversion mode.</param>
        /// <returns>Rendered bitmap.</returns>
        internal static Bitmap8 ConvertBuffer(byte[] buf, Palette8 palette,
                ColorConvMode convMode) {
            Bitmap8 output = new Bitmap8(OUTPUT_WIDTH, OUTPUT_HEIGHT);
            output.SetPalette(palette);

            int[] rowBits = new int[PIXELS_PER_ROW + MAX_LOOK];
            int[] colorBuf = new int[OUTPUT_WIDTH];

            for (int row = 0; row < NUM_ROWS; row++) {
                int rowOffset = HiRes.RowToOffset(row);     // same memory layout as hi-res
                int bitOffset = 0;

                // Clear the look-ahead bits.
                for (int i = 0; i < MAX_LOOK; i++) {
                    rowBits[PIXELS_PER_ROW + i] = 0;
                }

                // Unravel the bits into an array of 0/1 values, alternating between main
                // and aux memory, and skipping the high bits.
                byte val;
                for (int col = 0; col < PIXELS_PER_ROW / 7; col++) {
                    if ((col & 0x01) != 0) {
                        // odd-numbered bytes come from main memory
                        val = buf[rowOffset + PAGE_SIZE + col / 2];
                    } else {
                        // even-numbered bytes come from aux memory
                        val = buf[rowOffset + col / 2];
                    }

                    for (int bit = 0; bit < 7; bit++) {
                        rowBits[bitOffset++] = val & 0x01;
                        val >>= 1;
                    }
                }

                switch (convMode) {
                    case ColorConvMode.BlackWhite:
                        ConvertRowBW(rowBits, colorBuf);
                        break;
                    case ColorConvMode.Latched:
                        ConvertRowLatched(rowBits, colorBuf);
                        break;
                    case ColorConvMode.SlidingWindow:
                        ConvertRowSlidingWindow(rowBits, colorBuf);
                        break;
                    case ColorConvMode.Simple:
                        ConvertRowSimple(rowBits, colorBuf);
                        break;
                    default:
                        Debug.Assert(false);
                        return output;
                }

                // Copy to bitmap.
                for (int col = 0; col < PIXELS_PER_ROW; col++) {
                    byte colorIdx = (byte)colorBuf[col];
                    output.SetPixelIndex(col, row * 2, colorIdx);
                    output.SetPixelIndex(col, row * 2 + 1, colorIdx);
                }
            }

            return output;
        }

        /// <summary>
        /// Map shifted pixel values to a color.
        /// </summary>
        /// <remarks>
        /// <para>This is used when mapping the bits in a sliding 4-bit window to a color value.
        /// The trick is that the color depends not only on the bit values but also on the
        /// relative position from the left edge of the screen.  So we need to combine the
        /// column position (mod 4) with the 4-bit window value to get a color.</para>
        /// </remarks>
        private static readonly int[,] sColorMap = GenerateSlidingColorMap();

        private static int[,] GenerateSlidingColorMap() {
            int[,] map = new int[4, 16];
            for (int posn = 0; posn < 4; posn++) {
                for (int pixelBits = 0; pixelBits < NUM_COLORS; pixelBits++) {
                    int color = pixelBits;
                    for (int shift = 0; shift < posn; shift++) {
                        // rotate the 4-bit value
                        if ((color & 0x01) != 0) {
                            color |= 0x010;
                        }
                        color = (color >> 1) & 0x0f;
                    }
                    map[posn, pixelBits] = color;
                }
            }
            return map;
        }

        /// <summary>
        /// Convert bits to 560 black or white pixels.
        /// </summary>
        private static void ConvertRowBW(int[] rowBits, int[] colorBuf) {
            const int BLACK = 0;
            const int WHITE = 15;

            for (int i = 0; i < PIXELS_PER_ROW; i++) {
                if (rowBits[i] != 0) {
                    colorBuf[i] = WHITE;
                } else {
                    colorBuf[i] = BLACK;
                }
            }
        }

        /// <summary>
        /// Convert bits to 140 color pixels.  Not an accurate reflection of the output, but
        /// easily reversible.
        /// </summary>
        private static void ConvertRowSimple(int[] rowBits, int[] colorBuf) {
            for (int i = 0; i < PIXELS_PER_ROW; i += 4) {
                int val = (rowBits[i] << 3) |
                          (rowBits[i + 1] << 2) |
                          (rowBits[i + 2] << 1) |
                          rowBits[i + 3];
                colorBuf[i] = colorBuf[i + 1] = colorBuf[i + 2] = colorBuf[i + 3] = val;
            }
        }

        /// <summary>
        /// Convert bits to 140 color pixels, using a sliding 4-bit window to determine the
        /// color of the current pixel.  This is the approach described in //e technote #3.
        /// Reasonably accurate but doesn't quite line up with the IIgs RGB output at the fringes.
        /// </summary>
        private static void ConvertRowSlidingWindow(int[] rowBits, int[] colorBuf) {
            int pixVal = 0;
            for (int i = 0; i < PIXELS_PER_ROW; i++) {
                // The pixel value is determined by the bits at N-3, N-2, N-1, and N.
                pixVal = ((pixVal << 1) | rowBits[i]) & 0x0f;
                colorBuf[i] = sColorMap[(i + 1) & 0x03, pixVal];
            }
        }

        /// <summary>
        /// Converts bits to 140 color pixels, using a sliding window plus additional
        /// heuristics to clean up the fringes.  This improves the crispness of the output,
        /// bringing it closer to the IIgs RGB output.
        /// </summary>
        /// <remarks>
        /// <para>The value of the color at pixel N is determined by looking at the bits at
        /// positions N-3, N-2, N-1, and N.  When we see a color transition, we also look at the
        /// *next* four bits to special-case white/black transitions.  This reduces the color
        /// fringes around sharply-defined objects.</para>
        /// <para>Once a color is "latched", we keep outputting that color until we find a new
        /// one that we like more.</para>
        /// </remarks>
        private static void ConvertRowLatched(int[] rowBits, int[] colorBuf) {
            const int BLACK = 0;
            const int WHITE = 15;

            // Initialize "whole", which holds previous, current, and next bits: PPPPCNNN.
            // Assume the pixels off the left edge of the screen are zero.
            int whole = 0;
            for (int i = 0; i < 4; i++) {
                whole = (whole << 1) | rowBits[i];
            }

            // Color of "previous" pixel, which is always zero.
            int oldColor = 0;

            for (int i = 0; i < PIXELS_PER_ROW; i++) {
                // Shift another bit in, to give 3 prev and 4 next (PPPCNNNN).
                // This is offset by 4 because we already loaded the first 4 bits.
                whole = ((whole << 1) | rowBits[i + 4]) & 0xff;

                // Compute the new color from the PPPC bits.
                int newColor = sColorMap[(i + 1) & 0x03, (whole >> 4) & 0x0f];
                if (newColor != oldColor) {
                    // Transition to new color.  Check for white/black blocks in *next* chunk
                    // of pixels.  The goal is to eliminate color fringes on white/black
                    // boundaries, which are the most easily visible.
                    int shift1, shift2, shift3;
                    shift1 = (whole >> 3) & 0x0f;   // PPCN
                    shift2 = (whole >> 2) & 0x0f;   // PCNN
                    shift3 = (whole >> 1) & 0x0f;   // CNNN
                    if (shift1 == 0x0f || shift2 == 0x0f || shift3 == 0x0f) {
                        newColor = WHITE;
                    } else if (shift1 == 0 || shift2 == 0 || shift3 == 0) {
                        newColor = BLACK;
                    }
                }

                colorBuf[i] = newColor;

                // Use the new color as the old color for the next iteration.  This is *not* the
                // same as getting the color from PPPP before shifting next round, because we
                // might have overridden white or black above.  In that case, the new color would
                // be compared against white or black in the transition check, instead of comparing
                // against the actual color of the PPPP pixels.
                oldColor = newColor;        // latch it in
            }
        }
    }
}
