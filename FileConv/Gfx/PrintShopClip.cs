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
    /// Converts Print Shop clip art of various types.
    /// </summary>
    public class PrintShopClip : Converter {
        public const string TAG = "psclip";
        public const string LABEL = "Print Shop Clip Art";
        public const string DESCRIPTION =
            "Converts a Print Shop or Print Shop GS clip art graphic file to an 88x52 bitmap.  " +
            "These can optionally be pixel-multiplied x2/x3 to 176x156.  Monochrome and color " +
            "formats are supported.";
        public const string DISCRIMINATOR =
            "ProDOS $f8 / $c313 and $c323, DOS B / $x800, length 572/576 or 1716 " +
            " (length may vary slightly).";
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

        private const byte PSGS_TYPE = 0xf8;
        private const ushort PSGS_AUX_MONO_CLIP = 0xc313;
        private const ushort PSGS_AUX_COLOR_CLIP = 0xc323;

        private const int CLASSIC_MONO_LEN = 576;
        private const int GS_MONO_LEN = 572;
        private const int GS_COLOR_LEN = 1716;
        private const int CLIP_WIDTH = 88;
        private const int CLIP_HEIGHT = 52;
        private const int CLIP_PLANE_LEN = (CLIP_WIDTH / 8) * CLIP_HEIGHT;  // 572
        private const int XMULT = 2;
        private const int YMULT = 3;

        /// <summary>
        /// Print Shop GS palette.  The bit scheme looks like CMYK, but in practice it's closer
        /// to RGB primaries and the classic Apple II colors.  The values in the table were
        /// obtained from a KEGS screen grab.
        /// </summary>
        public static readonly Palette8 Palette_PSGS = new Palette8("Print Shop GS",
            new int[] {
                ConvUtil.MakeRGB(0xff, 0xff, 0xff),     // 000 white
                ConvUtil.MakeRGB(0x00, 0x00, 0xff),     // 001 blue (cyan?)
                ConvUtil.MakeRGB(0xff, 0x00, 0x00),     // 010 red (magenta?)
                ConvUtil.MakeRGB(0xcc, 0x00, 0xcc),     // 011 purple
                ConvUtil.MakeRGB(0xff, 0xff, 0x00),     // 100 yellow
                ConvUtil.MakeRGB(0x00, 0xff, 0x00),     // 101 green
                ConvUtil.MakeRGB(0xff, 0x66, 0x00),     // 110 orange
                ConvUtil.MakeRGB(0x00, 0x00, 0x00),     // 111 black
            });


        private PrintShopClip() { }

        public PrintShopClip(FileAttribs attrs, Stream? dataStream, Stream? rsrcStream,
                ResourceMgr? resMgr, ConvFlags convFlags, AppHook appHook)
                : base(attrs, dataStream, rsrcStream, resMgr, convFlags, appHook) {
            Applic = TestApplicability();
        }

        protected override Applicability TestApplicability() {
            if (DataStream == null) {
                return Applicability.Not;
            }

            if (FileAttrs.FileType == FileAttribs.FILE_TYPE_BIN &&
                    DataStream.Length == CLASSIC_MONO_LEN) {
                // Classic PrintShop monochrome.  Check the aux type for additional verification.
                if (FileAttrs.AuxType == 0x4800 || FileAttrs.AuxType == 0x5800 ||
                        FileAttrs.AuxType == 0x6800 || FileAttrs.AuxType == 0x7800) {
                    return Applicability.Yes;
                } else {
                    // Probably just a 'B' file of the right size.  It would help if we knew how
                    // many other nearby files had the same size, to detect a collection, but that
                    // information isn't available to us.
                    return Applicability.ProbablyNot;
                }
            } else if (FileAttrs.FileType == PSGS_TYPE &&
                    FileAttrs.AuxType == PSGS_AUX_MONO_CLIP) {
                if (DataStream.Length == GS_MONO_LEN || DataStream.Length == GS_MONO_LEN + 4) {
                    return Applicability.Yes;
                } else {
                    return Applicability.Maybe;
                }
            } else if (FileAttrs.FileType == PSGS_TYPE &&
                    FileAttrs.AuxType == PSGS_AUX_COLOR_CLIP) {
                if (DataStream.Length == GS_COLOR_LEN || DataStream.Length == GS_COLOR_LEN + 4) {
                    return Applicability.Yes;
                } else {
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
            DataStream.ReadExactly(fullBuf, 0, fullBuf.Length);

            bool doMult = GetBoolOption(options, OPT_MULT, false);

            if (DataStream.Length < GS_COLOR_LEN) {
                return ConvertMono(fullBuf, Palette8.Palette_MonoBW, doMult);
            } else {
                return ConvertColor(fullBuf, Palette_PSGS, doMult);
            }
        }

        /// <summary>
        /// Converts a monochrome Print Shop clip art file to a B&amp;W bitmap.
        /// </summary>
        public static Bitmap8 ConvertMono(byte[] buf, Palette8 palette, bool doMult) {
            Bitmap8 output;
            if (doMult) {
                output = new Bitmap8(CLIP_WIDTH * XMULT, CLIP_HEIGHT * YMULT);
            } else {
                output = new Bitmap8(CLIP_WIDTH, CLIP_HEIGHT);
            }
            output.SetPalette(palette);

            int offset = 0;
            for (int row = 0; row < CLIP_HEIGHT; row++) {
                for (int col = 0; col < CLIP_WIDTH / 8; col++) {
                    byte bval = buf[offset++];
                    for (int bit = 0; bit < 8; bit++) {
                        // Bits are zero for white, one for black; leftmost pixel in MSB.
                        byte color = (byte)(1 - (bval >> 7));
                        // First row of bits is top row of pixels.
                        if (doMult) {
                            SetMultPixels(output, col * 8 + bit, row, color);
                        } else {
                            output.SetPixelIndex(col * 8 + bit, row, color);
                        }
                        bval <<= 1;
                    }
                }
            }
            return output;
        }

        /// <summary>
        /// Converts a color Print Shop GS clip art file to an 8-color bitmap.
        /// </summary>
        public static Bitmap8 ConvertColor(byte[] buf, Palette8 palette, bool doMult) {
            Bitmap8 output;
            if (doMult) {
                output = new Bitmap8(CLIP_WIDTH * XMULT, CLIP_HEIGHT * YMULT);
            } else {
                output = new Bitmap8(CLIP_WIDTH, CLIP_HEIGHT);
            }
            output.SetPalette(palette);

            // Three consecutive bitmaps, one for each color plane.  Y then M then C.
            int offset = 0;
            for (int row = 0; row < CLIP_HEIGHT; row++) {
                for (int col = 0; col < CLIP_WIDTH / 8; col++) {
                    byte yval = buf[offset];
                    byte mval = buf[offset + CLIP_PLANE_LEN];
                    byte cval = buf[offset + CLIP_PLANE_LEN * 2];
                    offset++;
                    for (int bit = 0; bit < 8; bit++) {
                        // As with mono, leftmost pixel is in high bit.
                        int index =
                            ((yval & 0x80) >> 5) | ((mval & 0x80) >> 6) | ((cval & 0x80) >> 7);
                        if (doMult) {
                            SetMultPixels(output, col * 8 + bit, row, (byte)index);
                        } else {
                            output.SetPixelIndex(col * 8 + bit, row, (byte)index);
                        }
                        yval <<= 1;
                        mval <<= 1;
                        cval <<= 1;
                    }
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
        private static void SetMultPixels(Bitmap8 output, int col, int row, byte colorIndex) {
            for (int y = 0; y < YMULT; y++) {
                for (int x = 0; x < XMULT; x++) {
                    output.SetPixelIndex(col * XMULT + x, row * YMULT + y, colorIndex);
                }
            }
        }
    }
}
