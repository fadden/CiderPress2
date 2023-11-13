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
    public class SuperHiRes_3201 : Converter {
        public const string TAG = "shr3201";
        public const string LABEL = "Compressed 3200-Color Image";
        public const string DESCRIPTION =
            "Converts a \".3201\" compressed 3200-color image to a 640x400 direct-color bitmap.";
        public const string DISCRIMINATOR = "ProDOS BIN with extension \".3201\".";
        public override string Tag => TAG;
        public override string Label => LABEL;
        public override string Description => DESCRIPTION;
        public override string Discriminator => DISCRIMINATOR;

        private static readonly byte[] SIGNATURE = new byte[] {
            'A' | 0x80, 'P' | 0x80, 'P' | 0x80, 0x00
        };
        private const int COLOR_TABLE_LEN = 200 * 16 * 2;
        private const int MIN_LEN = 4 + COLOR_TABLE_LEN + 200;
        private const int NUM_ROWS = 200;
        private const int BYTE_WIDTH = 160;     // 320-mode, 2 pixels/byte


        private SuperHiRes_3201() { }

        public SuperHiRes_3201(FileAttribs attrs, Stream? dataStream, Stream? rsrcStream,
                ResourceMgr? resMgr, ConvFlags convFlags, AppHook appHook)
                : base(attrs, dataStream, rsrcStream, resMgr, convFlags, appHook) {
            Applic = TestApplicability();
        }

        protected override Applicability TestApplicability() {
            if (DataStream == null) {
                return Applicability.Not;
            }
            if (FileAttrs.FileType != FileAttribs.FILE_TYPE_BIN) {
                return Applicability.Not;
            }
            if (DataStream.Length < MIN_LEN) {
                return Applicability.Not;
            }
            string ext = Path.GetExtension(FileAttrs.FileNameOnly).ToLowerInvariant();
            if (ext != ".3201") {
                return Applicability.Not;
            }
            // Check the file signature.
            DataStream.Position = 0;
            for (int i = 0; i < SIGNATURE.Length; i++) {
                if (DataStream.ReadByte() != SIGNATURE[i]) {
                    return Applicability.Not;
                }
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

            // Copy color tables out.
            int offset = SIGNATURE.Length;
            int[,] colorTables = SuperHiRes_Brooks.ExtractColorTables(fileBuf, offset);
            offset += COLOR_TABLE_LEN;

            // Unpack the pixel data.
            byte[] unpackBuf = new byte[NUM_ROWS * BYTE_WIDTH];     // 32000
            int actual = ApplePack.UnpackBytes(fileBuf, offset, (int)(DataStream.Length - offset),
                unpackBuf, 0);
            if (actual != unpackBuf.Length) {
                return new ErrorText("Failed to unpack pixel data.");
            }

            return SuperHiRes_Brooks.Convert3200(unpackBuf, 0, colorTables);
        }
    }
}
