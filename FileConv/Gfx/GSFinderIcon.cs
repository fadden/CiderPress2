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
    /// Render the icons from an Apple IIgs Finder icon file in a grid.  Each entry has four
    /// images: full-size, full-size mask, small, and small mask.  We can generate a grid with
    /// one icon per line, four columns across.
    /// </summary>
    public class GSFinderIcon : Converter {
        public const string TAG = "gsicon";
        public const string LABEL = "Apple IIgs Finder Icons File";
        public const string DESCRIPTION =
            "Converts an Apple IIgs Finder Icons File to a bitmap grid.";
        public const string DISCRIMINATOR = "ProDOS ICN ($CA).";
        public override string Tag => TAG;
        public override string Label => LABEL;
        public override string Description => DESCRIPTION;
        public override string Discriminator => DISCRIMINATOR;

        private static readonly Palette8 ICON_PALETTE = CreatePalette();
        private static int COLOR_FRAME = QuickDrawII.sStdColor640.Length;
        private static int COLOR_BKGND = QuickDrawII.sStdColor640.Length + 1;

        private static Palette8 CreatePalette() {
            // Use the 640-mode palette, since icons are destined to appear in the Finder.
            // We want to add a couple of entries for the grid frame and background.
            int[] colors = new int[QuickDrawII.sStdColor640.Length + 2];
            Array.Copy(QuickDrawII.sStdColor640, colors, QuickDrawII.sStdColor640.Length);
            colors[COLOR_FRAME] = ConvUtil.MakeRGB(0xb6, 0x30, 0x44);   // muted blue
            colors[COLOR_BKGND] = ConvUtil.MakeRGB(0x40, 0x40, 0x40);   // dark gray
            return new Palette8("std640+", colors);
        }

        private GSFinderIcon() { }

        public GSFinderIcon(FileAttribs attrs, Stream? dataStream, Stream? rsrcStream,
                ResourceMgr? resMgr, ConvFlags convFlags, AppHook appHook)
                : base(attrs, dataStream, rsrcStream, resMgr, convFlags, appHook) {
            Applic = TestApplicability();
        }

        protected override Applicability TestApplicability() {
            if (DataStream == null) {
                return Applicability.Not;
            }
            if (FileAttrs.FileType == FileAttribs.FILE_TYPE_ICN) {
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

            return new ErrorText("TODO!");
        }
    }
}
