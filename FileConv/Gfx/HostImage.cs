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

using CommonUtil;
using DiskArc;

namespace FileConv.Gfx {
    /// <summary>
    /// <para>Detection of images that can be decoded directly by the host.  These are easily
    /// identified by filename extension and magic number.  GIF and JPEG are often found on
    /// vintage systems.</para>
    /// <para>This class doesn't actually convert the image to a bitmap, because .NET Core
    /// doesn't include the classes to do that.  GUI frameworks generally do, and CLI tools should
    /// be extracting these as-is rather than converting them, so it's up to the application to
    /// act appropriately when a HostImage is returned.</para>
    /// </summary>
    public class HostImage : Converter {
        public const string TAG = "host";
        public const string LABEL = "Host Image";
        public const string DESCRIPTION =
            "Converts a modern image format, such as GIF or JPEG, to a bitmap for viewing.";
        public const string DISCRIMINATOR = "filename extension \".GIF\", \".JPG\", or \".JPEG\". ";
        public override string Tag => TAG;
        public override string Label => LABEL;
        public override string Description => DESCRIPTION;
        public override string Discriminator => DISCRIMINATOR;

        private HostConv.FileKind mKind;


        private HostImage() { }

        public HostImage(FileAttribs attrs, Stream? dataStream, Stream? rsrcStream,
                ResourceMgr? resMgr, ConvFlags convFlags, AppHook appHook)
                : base(attrs, dataStream, rsrcStream, resMgr, convFlags, appHook) {
            Applic = TestApplicability();
        }

        private static byte[] GIF87A_SIG = new byte[] { 0x47, 0x49, 0x46, 0x38, 0x37, 0x61 };
        private static byte[] GIF89A_SIG = new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 };
        private static byte[] JPEG_SIG = new byte[] { 0xff, 0xd8, 0xff };   // SOI + SOFx
        private static byte[] PNG_SIG =
            new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a };

        private static uint PDF_TYPE = MacChar.IntifyMacConstantString("PDF ");
        private static byte[] PDF_SIG = new byte[] { 0x25, 0x50, 0x44, 0x46 };  // "%PDF" is enough

        private static uint RTF_TYPE = MacChar.IntifyMacConstantString("RTF ");
        private static byte[] RTF_SIG = new byte[] { 0x7b, 0x5c, 0x72, 0x74 };  // "{\rtf"

        private static uint WORD_TYPE = MacChar.IntifyMacConstantString("WDBN");
        private static uint WORD6_TYPE = MacChar.IntifyMacConstantString("W6BN");
        private static uint WORD8_TYPE = MacChar.IntifyMacConstantString("W8BN");

        protected override Applicability TestApplicability() {
            const int TEST_LEN = 8;
            if (DataStream == null || DataStream.Length < TEST_LEN) {
                return Applicability.Not;
            }
            string ext = Path.GetExtension(FileAttrs.FileNameOnly).ToLowerInvariant();
            byte[] buf = new byte[TEST_LEN];
            DataStream.Position = 0;
            DataStream.ReadExactly(buf, 0, buf.Length);

            if (ext == ".gif") {
                if (RawData.CompareBytes(buf, GIF87A_SIG, GIF87A_SIG.Length) ||
                        RawData.CompareBytes(buf, GIF89A_SIG, GIF89A_SIG.Length)) {
                    mKind = HostConv.FileKind.GIF;
                    return Applicability.Yes;
                }
            } else if (ext == ".jpg" || ext == ".jpeg") {
                if (RawData.CompareBytes(buf, JPEG_SIG, JPEG_SIG.Length)) {
                    mKind = HostConv.FileKind.JPEG;
                    return Applicability.Yes;
                }
            } else if (ext == ".png") {
                if (RawData.CompareBytes(buf, PNG_SIG, PNG_SIG.Length)) {
                    mKind = HostConv.FileKind.PNG;
                    return Applicability.Yes;
                }
            } else if (ext == ".pdf" || FileAttrs.HFSFileType == PDF_TYPE) {
                if (RawData.CompareBytes(buf, PDF_SIG, PDF_SIG.Length)) {
                    mKind = HostConv.FileKind.PDF;
                    return Applicability.Yes;
                }
            } else if (ext == ".rtf" || FileAttrs.HFSFileType == RTF_TYPE) {
                if (RawData.CompareBytes(buf, RTF_SIG, RTF_SIG.Length)) {
                    mKind = HostConv.FileKind.RTF;
                    return Applicability.Yes;
                }
            } else if (FileAttrs.HFSFileType == WORD_TYPE || FileAttrs.HFSFileType == WORD6_TYPE ||
                    FileAttrs.HFSFileType == WORD8_TYPE) {
                mKind = HostConv.FileKind.Word;
                return Applicability.Yes;
            }
            return Applicability.Not;
        }

        public override Type GetExpectedType(Dictionary<string, string> options) {
            return typeof(HostConv);
        }

        public override IConvOutput ConvertFile(Dictionary<string, string> options) {
            return new HostConv(mKind);
        }
    }
}
