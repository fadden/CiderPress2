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
    /// Apple IIgs super hi-res 3200-color screen image.  Named after the designer of the format,
    /// John Brooks.
    /// </summary>
    public class SuperHiRes_Brooks : Converter {
        public const string TAG = "shr3200";
        public const string LABEL = "Super Hi-Res 3200-Color Screen Image";
        public const string DESCRIPTION =
            "Converts an Apple IIgs Brooks-format 3200-color super hi-res image to a bitmap.";
        public const string DISCRIMINATOR = "ProDOS PIC/$0002, 37.5KB. " +
            "May be BIN with extension \".3200\".";
        public override string Tag => TAG;
        public override string Label => LABEL;
        public override string Description => DESCRIPTION;
        public override string Discriminator => DISCRIMINATOR;

        public const int EXPECTED_LEN = 38400;


        private SuperHiRes_Brooks() { }

        public SuperHiRes_Brooks(FileAttribs attrs, Stream? dataStream, Stream? rsrcStream,
                ResourceMgr? resMgr, ConvFlags convFlags, AppHook appHook)
                : base(attrs, dataStream, rsrcStream, resMgr, convFlags, appHook) {
            Applic = TestApplicability();
        }

        protected override Applicability TestApplicability() {
            if (DataStream == null) {
                return Applicability.Not;
            }
            if (DataStream.Length != EXPECTED_LEN) {
                return Applicability.Not;
            }
            // Official definition is 37.5KB PIC/$0002.
            if (FileAttrs.FileType == FileAttribs.FILE_TYPE_PIC && FileAttrs.AuxType == 0x0002) {
                return Applicability.Yes;
            }
            // Maybe they lost the aux type.
            if (FileAttrs.FileType == FileAttribs.FILE_TYPE_PIC) {
                return Applicability.Probably;
            }
            // A few used BIN with a file extension.
            string ext = Path.GetExtension(FileAttrs.FileNameOnly).ToLowerInvariant();
            if (FileAttrs.FileType == FileAttribs.FILE_TYPE_BIN) {
                if (ext == ".3200") {
                    return Applicability.Probably;
                } else {
                    // This is probably just some data file that happens to be the right size.
                    // Put the converter in the set, but below the generic options.
                    return Applicability.ProbablyNot;
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
            DataStream.ReadExactly(fullBuf, 0, Math.Min(EXPECTED_LEN, (int)DataStream.Length));

            // Copy color tables out.  The palettes are stored in reverse order (color 15 first),
            // so we want to reverse them as we extract.
            int offset = NUM_ROWS * BYTE_WIDTH;     // 32000
            int[,] colorTables = new int[NUM_ROWS, COLOR_TABLE_LEN];
            for (int row = 0; row < NUM_ROWS; row++) {
                for (int i = 0; i < COLOR_TABLE_LEN; i++) {
                    colorTables[row, COLOR_TABLE_LEN - i - 1] =
                        SuperHiRes.ARGBFromSHR(fullBuf[offset], fullBuf[offset + 1]);
                    offset += 2;
                }
            }
            Debug.Assert(offset == EXPECTED_LEN);

            return Convert3200(fullBuf, 0, colorTables);
        }

        #region Common

        // Double the pixels for consistency with other SHR converters.  Not strictly necessary.
        private const int OUTPUT_WIDTH = 640;
        private const int OUTPUT_HEIGHT = 400;

        private const int BYTE_WIDTH = 160;             // number of bytes per row
        private const int NUM_COLS = BYTE_WIDTH * 2;    // 320 mode only
        private const int NUM_ROWS = 200;               // source rows; will be doubled for output
        private const int COLOR_TABLE_LEN = 16;

        internal static Bitmap32 Convert3200(byte[] buf, int offset, int[,] colorTables) {
            Bitmap32 output = new Bitmap32(OUTPUT_WIDTH, OUTPUT_HEIGHT);
            output.IsDoubled = true;

            byte[] colorBuf = new byte[NUM_COLS];
            for (int row = 0; row < NUM_ROWS; row++) {
                SuperHiRes.ConvertLine(buf, offset, BYTE_WIDTH, false, false, colorBuf, 0);

                for (int col = 0; col < NUM_COLS; col++) {
                    int colorIndex = colorBuf[col];
                    int colorValue = colorTables[row, colorIndex];
                    output.SetPixel(col * 2, row * 2, colorValue);
                    output.SetPixel(col * 2 + 1, row * 2, colorValue);
                    output.SetPixel(col * 2, row * 2 + 1, colorValue);
                    output.SetPixel(col * 2 + 1, row * 2 + 1, colorValue);
                }
                offset += BYTE_WIDTH;
            }

            return output;
        }

        #endregion Common
    }
}
