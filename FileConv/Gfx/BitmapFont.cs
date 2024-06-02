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
using static FileConv.Gfx.DoubleHiRes;

namespace FileConv.Gfx {
    /// <summary>
    /// Converts a QuickDraw bitmap font to a bitmap, as a character grid or sample text.
    /// </summary>
    public class BitmapFont : Converter {
        public const string TAG = "qdfont";
        public const string LABEL = "QuickDraw II Font";
        public const string DESCRIPTION = "Converts a QuickDraw II font file to a bitmap.";
        public const string DISCRIMINATOR = "ProDOS FON with auxtype $0000.";
        public override string Tag => TAG;
        public override string Label => LABEL;
        public override string Description => DESCRIPTION;
        public override string Discriminator => DISCRIMINATOR;

        /// <summary>
        /// Font rendering palette.  Light gray background that provides a frame, dark gray
        /// background for character cells, black-on-white characters.
        /// </summary>
        private static readonly Palette8 FONT_PALETTE = new Palette8("Font",
            new int[] {
                ConvUtil.MakeRGB(0xe0, 0xe0, 0xe0),     // 0=light gray
                ConvUtil.MakeRGB(0x80, 0x80, 0x80),     // 1=dark gray
                ConvUtil.MakeRGB(0x00, 0x00, 0x00),     // 2=black
                ConvUtil.MakeRGB(0xff, 0xff, 0xff),     // 3=white
            });
        private const int COLOR_FRAME = 0;
        private const int COLOR_CELL_BK = 1;
        private const int COLOR_CHAR_BK = 2;
        private const int COLOR_CHAR_FG = 3;


        private BitmapFont() { }

        public BitmapFont(FileAttribs attrs, Stream? dataStream, Stream? rsrcStream,
                ResourceMgr? resMgr, ConvFlags convFlags, AppHook appHook)
                : base(attrs, dataStream, rsrcStream, resMgr, convFlags, appHook) {
            Applic = TestApplicability();
        }

        protected override Applicability TestApplicability() {
            if (DataStream == null || IsRawDOS) {
                return Applicability.Not;
            }
            if (FileAttrs.FileType == FileAttribs.FILE_TYPE_FON && FileAttrs.AuxType == 0x0000) {
                return Applicability.Yes;
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
            DataStream.ReadExactly(fullBuf, 0, (int)DataStream.Length);

            // TODO
            Bitmap8 output = new Bitmap8(10, 10);
            output.SetPalette(FONT_PALETTE);
            output.DrawChar('Z', 1, 1, COLOR_CHAR_FG, COLOR_CHAR_BK);
            output.Notes.AddI("TODO!");
            return output;
        }
    }
}
