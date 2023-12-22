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
    /// Converts Apple MacPaint graphics documents.
    /// </summary>
    public class MacPaint : Converter {
        public const string TAG = "macpaint";
        public const string LABEL = "MacPaint Document";
        public const string DESCRIPTION =
            "Converts a MacPaint document to a 576x720 monochrome bitmap.";
        public const string DISCRIMINATOR =
            "HFS type 'PNTG', extensions '.pntg' and '.mac'.";
        public override string Tag => TAG;
        public override string Label => LABEL;
        public override string Description => DESCRIPTION;
        public override string Discriminator => DISCRIMINATOR;

        private const int MAX_VERSION = 3;      // seen in the wild
        private const int BITMAP_WIDTH = 576;
        private const int BITMAP_HEIGHT = 720;
        private const int ROW_STRIDE = BITMAP_WIDTH / 8;
        private const int BITMAP_SIZE = ROW_STRIDE * BITMAP_HEIGHT;

        private const int HEADER_LEN = 512;
        private const int MIN_LEN = HEADER_LEN + BITMAP_HEIGHT * 2;
        // "The worst case would be when _PackBits adds one byte to the row of bytes when packing."
        private const int MAX_LEN = DiskArc.Arc.MacBinary.HEADER_LEN + HEADER_LEN +
            BITMAP_HEIGHT * (ROW_STRIDE + 1);

        private static readonly uint HFS_TYPE = MacChar.IntifyMacConstantString("PNTG");


        private MacPaint() { }

        public MacPaint(FileAttribs attrs, Stream? dataStream, Stream? rsrcStream,
                ResourceMgr? resMgr, ConvFlags convFlags, AppHook appHook)
                : base(attrs, dataStream, rsrcStream, resMgr, convFlags, appHook) {
            Applic = TestApplicability();
        }

        protected override Applicability TestApplicability() {
            if (DataStream == null) {
                return Applicability.Not;
            }
            if (DataStream.Length < MIN_LEN || DataStream.Length > MAX_LEN) {
                return Applicability.Not;
            }

            bool ok;

            // Best indicator is the HFS file type.
            if (FileAttrs.HFSFileType == HFS_TYPE) {
                // Check the version number just to be sure.
                DataStream.Position = 0;
                uint vers = RawData.ReadU32BE(DataStream, out ok);
                if (vers > MAX_VERSION) {
                    // Weird version value.  Allow it, because the file type is a strong signal,
                    // but at reduced priority.
                    return Applicability.Maybe;
                } else {
                    return Applicability.Yes;
                }
            }

            // Next best is a MacBinary wrapper.
            DataStream.Position = DiskArc.Arc.MacBinary.TYPE_OFFSET;
            uint macBinType = RawData.ReadU32BE(DataStream, out ok);
            if (macBinType == HFS_TYPE) {
                // Looks like MacBinary PNTG.  Confirm the version number is good.
                DataStream.Position = DiskArc.Arc.MacBinary.HEADER_LEN;
                uint vers = RawData.ReadU32BE(DataStream, out ok);
                if (vers > MAX_VERSION) {
                    // Weird version.  Again, allow it because of the file type.
                    return Applicability.Maybe;
                } else {
                    // Looks like MacPaint in MacBinary.
                    return Applicability.Yes;
                }
            }

            // There's no ProDOS type for it, so if it's not BIN or NON, give up.
            // TODO: we can get a positive indication by attempting to decompress 720 lines.  If
            //   it works out exactly, we have a match.  Relatively expensive but not terribly
            //   so on modern hardware.
            if (FileAttrs.FileType != FileAttribs.FILE_TYPE_NON &&
                    FileAttrs.FileType != FileAttribs.FILE_TYPE_BIN) {
                return Applicability.Not;
            }

            string ext = Path.GetExtension(FileAttrs.FileNameOnly).ToLowerInvariant();
            bool goodExt = (ext == ".mac" || ext == ".pntg");

            // See if the first 4 bytes hold a valid MacPaint version number.  Since 0 is
            // valid, this is a very weak test.
            DataStream.Position = 0;
            uint version = RawData.ReadU32BE(DataStream, out ok);
            if (version > MAX_VERSION) {
                return Applicability.Not;
            }

            if (goodExt) {
                return Applicability.Maybe;
            } else {
                return Applicability.ProbablyNot;
            }
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

            byte[] readBuf = new byte[DataStream.Length];
            DataStream.Position = 0;
            DataStream.ReadExactly(readBuf, 0, (int)DataStream.Length);

            int dataOffset = HEADER_LEN;
            uint version = RawData.GetU32BE(readBuf, 0);
            bool badVersion = false;
            if (version > MAX_VERSION) {
                // MacBinary or garbled-but-maybe.
                if (RawData.GetU32BE(readBuf,
                             DiskArc.Arc.MacBinary.TYPE_OFFSET) == HFS_TYPE) {
                    // Looks like a MacBinary header, skip past it.
                    dataOffset += DiskArc.Arc.MacBinary.HEADER_LEN;
                } else {
                    badVersion = true;
                }
            }

            IConvOutput output = ConvertMacPaint(readBuf, dataOffset);
            if (badVersion) {
                output.Notes.AddW("Found bad version number: 0x" + version.ToString("x8"));
            }
            return output;
        }

        private static Bitmap8 ConvertMacPaint(byte[] buf, int startOffset) {
            Palette8 palette = Palette8.Palette_MonoBW;
            Bitmap8 output = new Bitmap8(BITMAP_WIDTH, BITMAP_HEIGHT);
            output.SetPalette(palette);

            byte[] uncRow = new byte[ROW_STRIDE];
            int srcOffset = startOffset;
            for (int row = 0; row < BITMAP_HEIGHT; row++) {
                bool ok = ApplePack.UnpackBits(buf, ref srcOffset, buf.Length - srcOffset,
                    uncRow, 0, uncRow.Length);
                if (!ok) {
                    output.Notes.AddW("UnpackBits had trouble on row " + row);
                }
                for (int col = 0; col < ROW_STRIDE; col++) {
                    byte pixelBits = uncRow[col];
                    for (int bit = 0; bit < 8; bit++) {
                        output.SetPixelIndex(col * 8 + bit, row, (byte)(1 - (pixelBits >> 7)));
                        pixelBits <<= 1;
                    }
                }
            }

            return output;
        }
    }
}
