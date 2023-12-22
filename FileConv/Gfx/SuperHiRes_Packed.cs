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
    public class SuperHiRes_Packed : Converter {
        public const string TAG = "shrpk";
        public const string LABEL = "Super Hi-Res Packed Screen Image";
        public const string DESCRIPTION =
            "Converts a packed Apple IIgs super hi-res image to a bitmap.";
        public const string DISCRIMINATOR = "ProDOS PNT/$0001.";
        public override string Tag => TAG;
        public override string Label => LABEL;
        public override string Description => DESCRIPTION;
        public override string Discriminator => DISCRIMINATOR;

        // Set the max length to the length of the original plus the maximum PackBytes expansion
        // of 1 byte for every 64.
        private const int MAX_LEN = SuperHiRes.EXPECTED_LEN + (SuperHiRes.EXPECTED_LEN / 64);

        private SuperHiRes_Packed() { }

        public SuperHiRes_Packed(FileAttribs attrs, Stream? dataStream, Stream? rsrcStream,
                ResourceMgr? resMgr, ConvFlags convFlags, AppHook appHook)
                : base(attrs, dataStream, rsrcStream, resMgr, convFlags, appHook) {
            Applic = TestApplicability();
        }

        protected override Applicability TestApplicability() {
            if (DataStream == null) {
                return Applicability.Not;
            }
            if (DataStream.Length > MAX_LEN) {
                return Applicability.Not;       // even if it's the right type, don't touch it
            }
            if (FileAttrs.FileType == FileAttribs.FILE_TYPE_PNT && FileAttrs.AuxType == 0x0001) {
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

            byte[] readBuf = new byte[DataStream.Length];
            DataStream.Position = 0;
            DataStream.ReadExactly(readBuf, 0, (int)DataStream.Length);

            // Not much to this.  Unpack the file as a single PackBytes buffer, then render it
            // using the super hi-res screen code.
            byte[] shrBuf = new byte[SuperHiRes.EXPECTED_LEN];
            int unpackLen = ApplePack.UnpackBytes(readBuf, 0, readBuf.Length, shrBuf, 0,
                out bool unpackErr);
            if (unpackErr || unpackLen != SuperHiRes.EXPECTED_LEN) {
                return new ErrorText("Failed to unpack compressed image data");
            }

            return SuperHiRes.ConvertBuffer(shrBuf);
        }
    }
}
