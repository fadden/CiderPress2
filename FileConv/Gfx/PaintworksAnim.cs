/*
 * Copyright 2025 faddenSoft
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
    /// Converts a Paintworks Animation file.
    /// </summary>
    /// <remarks>
    /// We're not currently set up to output video files, and the WPF image viewer just shows
    /// the first frame of animated GIFs, so we just convert the first frame to a bitmap as a
    /// quick preview.
    /// </remarks>
    public class PaintworksAnim : Converter {
        public const string TAG = "shrani";
        public const string LABEL = "Super Hi-Res Animation";
        public const string DESCRIPTION =
            "Converts the first frame of an Apple IIgs Paintworks animation to a bitmap.";
        public const string DISCRIMINATOR = "ProDOS ANI/$0000.";
        public override string Tag => TAG;
        public override string Label => LABEL;
        public override string Description => DESCRIPTION;
        public override string Discriminator => DISCRIMINATOR;

        public const int SHR_IMAGE_LEN = 32768;
        public const int MIN_LEN = SHR_IMAGE_LEN + 8;
        public const int MAX_LEN = 16 * 1024 * 1024;        // arbitrary 16MB cap

        private PaintworksAnim() { }

        public PaintworksAnim(FileAttribs attrs, Stream? dataStream, Stream? rsrcStream,
                ResourceMgr? resMgr, ConvFlags convFlags, AppHook appHook)
                : base(attrs, dataStream, rsrcStream, resMgr, convFlags, appHook) {
            Applic = TestApplicability();
        }

        protected override Applicability TestApplicability() {
            if (DataStream == null) {
                return Applicability.Not;
            }
            // Official definition is 32KB PIC/$0000.
            if (FileAttrs.FileType == FileAttribs.FILE_TYPE_ANI && FileAttrs.AuxType == 0x0000 &&
                    DataStream.Length >= MIN_LEN && DataStream.Length <= MAX_LEN) {
                return Applicability.Yes;
            } else {
                return Applicability.Not;
            }
        }

        public override Type GetExpectedType(Dictionary<string, string> options) {
            return typeof(IBitmap);     // TODO
        }

        public override IConvOutput ConvertFile(Dictionary<string, string> options) {
            if (Applic <= Applicability.Not) {
                Debug.Assert(false);
                return new ErrorText("ERROR");
            }
            Debug.Assert(DataStream != null);

            // Read the whole thing in.  (We currently only need the first 32KB, but we want to
            // scan the file contents for curiosity's sake.)
            byte[] fullBuf = new byte[DataStream.Length];
            DataStream.Position = 0;
            DataStream.ReadExactly(fullBuf, 0, (int)DataStream.Length);
            return ConvertBuffer(fullBuf);
        }

        /// <summary>
        /// Converts the first frame of an ANI animation to a 640x400 bitmap.
        /// </summary>
        internal static Bitmap8 ConvertBuffer(byte[] buf) {
            Bitmap8 result = SuperHiRes.ConvertBuffer(buf);

            // Let's poke at the file a bit.
            int offset = SHR_IMAGE_LEN;
            uint dataLen = RawData.ReadU32LE(buf, ref offset);
            ushort frameDelay = RawData.ReadU16LE(buf, ref offset);
            ushort flagsMaybe = RawData.ReadU16LE(buf, ref offset);
            result.Notes.AddI("frameDelay=" + frameDelay + " VBLs");
            result.Notes.AddI("flags?=$" + flagsMaybe.ToString("x4"));
            if (buf.Length - SHR_IMAGE_LEN - 8 != dataLen) {
                result.Notes.AddW("dataLen=" + dataLen + ", but file len is " + buf.Length);
            }
            if ((dataLen & 0x03) != 0) {
                result.Notes.AddW("Warning: dataLen is not a multiple of 4");
            }

            uint animLen = RawData.ReadU32LE(buf, ref offset);
            if (animLen != dataLen) {
                result.Notes.AddW("total len (" + dataLen + ") != anim len (" + animLen + ")");
            }
            if ((animLen & 0x03) != 0) {
                result.Notes.AddW("Warning: animLen is not a multiple of 4");
            }
            int animEnd = offset + (int)animLen - 4;
            if (animLen <= 4) {
                // Work around missing anim len by using the full data len.
                animEnd = offset + (int)dataLen - 4;
            }
            int frameCount = 0;
            bool lastWasZero = false;
            while (offset < animEnd) {
                ushort off = RawData.ReadU16LE(buf, ref offset);
                ushort val = RawData.ReadU16LE(buf, ref offset);
                if (off == 0) {
                    frameCount++;
                    lastWasZero = true;
                } else {
                    lastWasZero = false;
                }
            }
            result.Notes.AddI("counted " + frameCount + " frames");
            if (!lastWasZero) {
                result.Notes.AddW("Warning: animation data did not end on a frame boundary");
            }

            return result;
        }
    }
}
