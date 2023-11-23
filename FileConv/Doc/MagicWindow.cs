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

namespace FileConv.Doc {
    /// <summary>
    /// Converts a Magic Window document to text.
    /// </summary>
    public class MagicWindow : Converter {
        public const string TAG = "magwin";
        public const string LABEL = "Magic Window Document";
        public const string DESCRIPTION = "Converts a Magic Window document to text.";
        public const string DISCRIMINATOR = "DOS B with extension \".MW\".";
        public override string Tag => TAG;
        public override string Label => LABEL;
        public override string Description => DESCRIPTION;
        public override string Discriminator => DISCRIMINATOR;

        private const int HEADER_LEN = 256;
        private const int MIN_LEN = HEADER_LEN;
        private const int MAX_LEN = 48 * 1024;      // arbitrary but generous


        private MagicWindow() { }

        public MagicWindow(FileAttribs attrs, Stream? dataStream, Stream? rsrcStream,
                ResourceMgr? resMgr, ConvFlags convFlags, AppHook appHook)
                : base(attrs, dataStream, rsrcStream, resMgr, convFlags, appHook) {
            Applic = TestApplicability();
        }

        protected override Applicability TestApplicability() {
            if (DataStream == null || IsRawDOS) {
                return Applicability.Not;
            }
            // Should we insist on DataStream == DOS_FileDesc?  Don't think there's a ProDOS vers.
            if (FileAttrs.FileType != FileAttribs.FILE_TYPE_BIN) {
                return Applicability.Not;
            }
            if (DataStream.Length < MIN_LEN || DataStream.Length > MAX_LEN) {
                return Applicability.Not;
            }
            string ext = Path.GetExtension(FileAttrs.FileNameOnly).ToLowerInvariant();
            if (ext != ".mw") {
                return Applicability.Not;
            }

            // The original CiderPress converter jumped through a lot of hoops to analyze the
            // file contents, but it did so to allow files with names that don't end in ".MW".
            // Since we require the extension we can just do a quick test here.
            //
            // All MW files seem to have 0x8d in the first byte, so check for that here.
            DataStream.Position = 0;
            byte data = (byte)DataStream.ReadByte();
            if (data != 0x8d) {
                return Applicability.Not;
            }
            // The meaning of the next byte is unknown.  The following bytes seems to be the start
            // of a page header string, padded with spaces.  It's stored in high ASCII for the
            // original MW, low ASCII for MW2.
            DataStream.ReadByte();      // skip
            data = (byte)DataStream.ReadByte();
            if (data < 0x20) {
                return Applicability.Not;
            }

            return Applicability.Yes;
        }

        public override Type GetExpectedType(Dictionary<string, string> options) {
            return typeof(SimpleText);
        }

        public override IConvOutput ConvertFile(Dictionary<string, string> options) {
            if (Applic <= Applicability.Not) {
                Debug.Assert(false);
                return new ErrorText("ERROR");
            }
            // Load the entire file into memory.
            Debug.Assert(DataStream != null);
            byte[] fileBuf = new byte[DataStream.Length];
            DataStream.Position = 0;
            DataStream.ReadExactly(fileBuf, 0, (int)DataStream.Length);

            SimpleText output = new SimpleText();

            // Everything past the header should be mixed-case high ASCII.
            int offset = HEADER_LEN;
            while (offset < fileBuf.Length) {
                // TODO: interpret the printer codes to generate fancy text
                char ch = (char)(fileBuf[offset++] & 0x7f);
                if (ch == '\r') {
                    output.AppendLine();
                } else {
                    output.AppendPrintable(ch);
                }
            }

            return output;
        }
    }
}
