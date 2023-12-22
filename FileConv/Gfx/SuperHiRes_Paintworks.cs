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
    public class SuperHiRes_Paintworks : Converter {
        public const string TAG = "shrpw";
        public const string LABEL = "Paintworks Graphics Image";
        public const string DESCRIPTION =
            "Converts a Paintworks super hi-res image file to a 640x400 or 640x792 " +
            "indexed-color bitmap.";
        public const string DISCRIMINATOR = "ProDOS PNT/$0000.";
        public override string Tag => TAG;
        public override string Label => LABEL;
        public override string Description => DESCRIPTION;
        public override string Discriminator => DISCRIMINATOR;


        private const int PIXEL_DATA_OFFSET = 32 + 2 + 512;     // 0x0222
        private const int MIN_LEN = PIXEL_DATA_OFFSET + 1;
        private const int SHORT_ROWS = 200;
        private const int TALL_ROWS = 396;
        private const int NUM_COLS = 320;
        private const int OUTPUT_WIDTH = NUM_COLS * 2;          // double pixels for consistency
        private const int BYTES_PER_ROW = NUM_COLS / 2;
        private const int MAX_EXPECTED_LEN = TALL_ROWS * BYTES_PER_ROW; // 63360


        private SuperHiRes_Paintworks() { }

        public SuperHiRes_Paintworks(FileAttribs attrs, Stream? dataStream, Stream? rsrcStream,
                ResourceMgr? resMgr, ConvFlags convFlags, AppHook appHook)
                : base(attrs, dataStream, rsrcStream, resMgr, convFlags, appHook) {
            Applic = TestApplicability();
        }

        protected override Applicability TestApplicability() {
            if (DataStream == null) {
                return Applicability.Not;
            }
            if (FileAttrs.FileType != FileAttribs.FILE_TYPE_PNT || FileAttrs.AuxType != 0) {
                return Applicability.Not;
            }
            if (DataStream.Length < MIN_LEN) {
                return Applicability.Not;
            }
            return Applicability.Yes;
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

            // Some files are slightly oversized.  Allow it.
            byte[] unpackBuf = new byte[MAX_EXPECTED_LEN + 1024];
            int unpackCount = ApplePack.UnpackBytes(fileBuf, PIXEL_DATA_OFFSET,
                fileBuf.Length - PIXEL_DATA_OFFSET, unpackBuf, 0, out bool unpackErr);
            bool isShort = false;
            // TODO: I've seen a few 44800-byte files (280 lines).  We may want to handle
            // anything that's a multiple of 160 bytes.
            if (unpackCount == SHORT_ROWS * BYTES_PER_ROW) {
                isShort = true;
            } else if (unpackCount < TALL_ROWS * BYTES_PER_ROW) {
                // Let's retry this as a PNT/$0001 file, which starts with the uncompressed
                // data at the start of the file.  The title "Revolution 76" does this.
                int retryCount = ApplePack.UnpackBytes(fileBuf, 0, fileBuf.Length, unpackBuf, 0,
                    out unpackErr);
                if (retryCount == SuperHiRes.EXPECTED_LEN) {
                    // Render it, but complain.
                    IConvOutput wrongOut = SuperHiRes.ConvertBuffer(unpackBuf);
                    wrongOut.Notes.AddW("File type is wrong, should be $C1 (PNT) / $0001");
                    return wrongOut;
                } else {
                    // No luck.  Report failure.
                    if (unpackErr) {
                        return new ErrorText("Failed to decompress data.");
                    } else {
                        return new ErrorText("Failed to decompress data (found " +
                            unpackCount + " bytes, expected " + SuperHiRes.EXPECTED_LEN + ").");
                    }
                }
            }

            // There's no SCB, so this is always 320 mode with palette 0.  Extract colors.
            int[] colors = new int[16];
            int offset = 0;
            for (int i = 0; i < colors.Length; i++) {
                colors[i] = SuperHiRes.ARGBFromSHR(fileBuf[offset + i * 2],
                    fileBuf[offset + i * 2 + 1]);
            }

            IConvOutput output = ConvertPaintworks(unpackBuf, colors, isShort);

            // Now that we have an output object, add any warnings encountered earlier.
            if (unpackErr) {
                output.Notes.AddW("Encountered an error while unpacking the file");
            }
            if (unpackCount > TALL_ROWS * BYTES_PER_ROW) {
                output.Notes.AddI("Found " + (unpackCount - TALL_ROWS * BYTES_PER_ROW) +
                    " extra bytes at end");
            }
            return output;
        }

        private static Bitmap8 ConvertPaintworks(byte[] buf, int[] colors, bool isShort) {
            int numRows = isShort ? SHORT_ROWS : TALL_ROWS;
            Bitmap8 output = new Bitmap8(OUTPUT_WIDTH, numRows * 2);
            output.SetPalette(new Palette8("SHR Screen", colors));

            // Convert the pixel data.
            int offset = 0;
            byte[] colorBuf = new byte[NUM_COLS];
            for (int row = 0; row < numRows; row++) {
                SuperHiRes.ConvertLine(buf, offset, BYTES_PER_ROW, false, false, colorBuf, 0);

                // Copy pixels to bitmap, doubling them.
                for (int col = 0; col < NUM_COLS; col++) {
                    byte colorIndex;
                    colorIndex = colorBuf[col];
                    output.SetPixelIndex(col * 2, row * 2, colorIndex);
                    output.SetPixelIndex(col * 2 + 1, row * 2, colorIndex);
                    output.SetPixelIndex(col * 2, row * 2 + 1, colorIndex);
                    output.SetPixelIndex(col * 2 + 1, row * 2 + 1, colorIndex);
                }
                offset += BYTES_PER_ROW;
            }
            return output;
        }
    }
}
