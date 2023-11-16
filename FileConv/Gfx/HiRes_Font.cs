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
    /// <para>Converts an Apple II hi-res font into a bitmap grid for easy viewing.  The Apple ///
    /// used the same format, but the high bit had a special meaning.</para>
    /// <para>We currently output at 1:1, so half-pixel shifts are not modeled.</para>
    /// </summary>
    public class HiRes_Font : Converter {
        public const string TAG = "hgrfont";
        public const string LABEL = "Hi-Res Screen Font";
        public const string DESCRIPTION =
            "Converts an Apple II or /// hi-res screen font to a bitmap grid.";
        public const string DISCRIMINATOR =
            "ProDOS FNT, BIN or PDA, or DOS B, with a length of 768 or 1024 bytes.";
        public override string Tag => TAG;
        public override string Label => LABEL;
        public override string Description => DESCRIPTION;
        public override string Discriminator => DISCRIMINATOR;

        public const string OPT_FLHI = "flhi";

        public override List<OptionDefinition> OptionDefs { get; protected set; } =
            new List<OptionDefinition>() {
                new OptionDefinition(OPT_FLHI, "Add color if hi bit set",
                    OptionDefinition.OptType.Boolean, "false"),
            };

        internal static readonly Palette8 sFontPalette = new Palette8("Hi-Res Font", new int[] {
            ConvUtil.MakeRGB(0x80, 0x80, 0x80),     // 0=gray border
            ConvUtil.MakeRGB(0x00, 0x00, 0x00),     // 1=black background
            ConvUtil.MakeRGB(0xff, 0xff, 0xff),     // 2=white foreground
            ConvUtil.MakeRGB(0xff, 0x00, 0x00),     // 3=red "flash"
        });
        internal const int BORD_INDEX = 0;
        internal const int BG_INDEX = 1;
        internal const int FG_INDEX = 2;
        internal const int FLASH_INDEX = 3;

        private const int SHORT_FONT_LEN = 768;
        private const int FULL_FONT_LEN = 1024;


        private HiRes_Font() { }

        public HiRes_Font(FileAttribs attrs, Stream? dataStream, Stream? rsrcStream,
                ResourceMgr? resMgr, ConvFlags convFlags, AppHook appHook)
                : base(attrs, dataStream, rsrcStream, resMgr, convFlags, appHook) {
            Applic = TestApplicability();
        }

        protected override Applicability TestApplicability() {
            if (DataStream == null || IsRawDOS) {
                return Applicability.Not;
            }
            if (DataStream.Length != SHORT_FONT_LEN && DataStream.Length != FULL_FONT_LEN) {
                return Applicability.Not;
            }
            if (FileAttrs.FileType == FileAttribs.FILE_TYPE_FNT) {
                return Applicability.Yes;
            } else if (FileAttrs.FileType == FileAttribs.FILE_TYPE_BIN ||
                    FileAttrs.FileType == FileAttribs.FILE_TYPE_PDA) {
                // Could be a font, could be some other type of file that just happens to
                // be this long.  We want this to be higher than plain text or hex dump,
                // but lower than anything that has a reasonable chance of identifying the
                // file contents.
                //
                // We could try to analyze the contents, e.g. most fonts will have a blank
                // space for 0x20 (possibly inverted), but that's dubious at best.
                return Applicability.Maybe;
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

            byte[] fileBuf = new byte[DataStream.Length];
            DataStream.Position = 0;
            DataStream.ReadExactly(fileBuf, 0, (int)DataStream.Length);

            bool doFlashHi = GetBoolOption(options, OPT_FLHI, false);
            return ConvertBuffer(fileBuf, sFontPalette, doFlashHi);
        }

        private const int CELL_WIDTH = 7;
        private const int CELL_HEIGHT = 8;
        private const int HORIZ_CELLS = 16;
        private const int VERT_CELLS_SHORT = 96 / 16;
        private const int VERT_CELLS_LONG = 128 / 16;

        private static Bitmap8 ConvertBuffer(byte[] buf, Palette8 palette, bool doFlashHi) {
            bool isShort = (buf.Length == SHORT_FONT_LEN);
            int vertCells = isShort ? VERT_CELLS_SHORT : VERT_CELLS_LONG;
            int width = 1 + HORIZ_CELLS * (CELL_WIDTH + 1);
            int height = 1 + vertCells * (CELL_HEIGHT + 1);
            Bitmap8 output = new Bitmap8(width, height);
            output.SetPalette(palette);

            // All pixels initially have index 0, our grey background, so we don't need to
            // render that part.

            int cellX = 1;
            int cellY = 1;
            for (int chidx = 0; chidx < buf.Length / 8; chidx++) {
                DrawCell(buf, chidx * 8, cellX, cellY, doFlashHi, output);
                cellX += CELL_WIDTH + 1;
                if (cellX == output.Width) {
                    cellX = 1;
                    cellY += CELL_HEIGHT + 1;
                }
            }
            return output;
        }

        /// <summary>
        /// Renders a single hi-res character cell.
        /// </summary>
        private static void DrawCell(byte[] buf, int offset, int cellX, int cellY, bool doFlashHi,
                Bitmap8 output) {
            for (int row = 0; row < CELL_HEIGHT; row++) {
                byte bval = buf[offset++];
                byte fgIndex = FG_INDEX;
                if (doFlashHi && ((bval & 0x80) != 0)) {
                    fgIndex = FLASH_INDEX;
                }
                for (int col = 0; col < CELL_WIDTH; col++) {
                    if ((bval & 0x01) == 0) {
                        output.SetPixelIndex(cellX + col, cellY + row, BG_INDEX);
                    } else {
                        output.SetPixelIndex(cellX + col, cellY + row, fgIndex);
                    }
                    bval >>= 1;
                }
            }
        }
    }
}
