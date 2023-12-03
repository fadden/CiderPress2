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
using System.Text;

using CommonUtil;
using DiskArc;

namespace FileConv.Generic {
    /// <summary>
    /// Plain text converter.  Unifies line endings (CR/LF/CRLF -> local) and converts non-ASCII
    /// characters according to the current options.
    /// </summary>
    public class PlainText : Converter {
        public const string TAG = "text";
        public const string LABEL = "Plain Text";
        public const string DESCRIPTION =
            "Plain text converter.  Converts end-of-line markers to the local convention, " +
            "and converts non-ASCII characters according to the current configuration options. " +
            "Control characters may be converted to printable form or output raw.";
        public const string DISCRIMINATOR = "any file; preferred for ProDOS TXT and DOS T.";
        public override string Tag => TAG;
        public override string Label => LABEL;
        public override string Description => DESCRIPTION;
        public override string Discriminator => DISCRIMINATOR;

        public const string OPT_CHAR = "char";
        public const string OPT_PRINT = "print";

        public override List<OptionDefinition> OptionDefs { get; protected set; } =
            new List<OptionDefinition>() {
                new OptionDefinition(OPT_PRINT, "Show ctrl chars",
                    OptionDefinition.OptType.Boolean, "true"),
                new OptionDefinition(OPT_CHAR, "Char Encoding",
                    OptionDefinition.OptType.Multi, ConvUtil.CHAR_MODE_HIGH_ASCII,
                    ConvUtil.ExportCharSrcTags, ConvUtil.ExportCharSrcDescrs),
            };


        private PlainText() { }

        public PlainText(FileAttribs attrs, Stream? dataStream, Stream? rsrcStream,
                ResourceMgr? resMgr, ConvFlags convFlags, AppHook appHook)
                : base(attrs, dataStream, rsrcStream, resMgr, convFlags, appHook) {
            Applic = TestApplicability();
        }

        protected override Applicability TestApplicability() {
            return Applicability.Always_Text;
        }

        public override Type GetExpectedType(Dictionary<string, string> options) {
            return typeof(SimpleText);
        }

        public override IConvOutput ConvertFile(Dictionary<string, string> options) {
            SimpleText output = new SimpleText();

            string charConvStr = GetStringOption(options, OPT_CHAR, ConvUtil.CHAR_MODE_HIGH_ASCII);
            if (!ConvUtil.TryParseExportCharSrc(charConvStr, out ConvUtil.ExportCharSrc charMode)) {
                // Unknown value, just use high ASCII.
                charMode = ConvUtil.ExportCharSrc.HighASCII;
            }

            bool printableOnly = GetBoolOption(options, OPT_PRINT, true);

            if (DataStream != null) {
                ConvertStream(DataStream, charMode, printableOnly, -1, output.Text);
            } else {
                output.Notes.AddI("File has no data fork");
            }
            return output;
        }

        /// <summary>
        /// Converts a single stream (data or resource fork).
        /// </summary>
        /// <param name="stream">Stream to convert.</param>
        /// <param name="charMode">How to convert bytes to characters.</param>
        /// <param name="printableOnly">If true, convert characters to printable form; if false,
        ///   output raw control characters.</param>
        /// <param name="stopAtByte">Byte to stop at (e.g. Ctrl+Z), or -1 if not needed.</param>
        /// <param name="sb">StringBuilder that receives the output.</param>
        internal static void ConvertStream(Stream stream, ConvUtil.ExportCharSrc charMode,
                bool printableOnly, int stopAtByte, StringBuilder sb) {
            string newline = Environment.NewLine;
            byte[] dataBuf = new byte[32768];

            stream.Position = 0;
            bool lastWasCR = false;
            while (true) {
                int actual = stream.Read(dataBuf, 0, dataBuf.Length);
                if (actual == 0) {
                    break;      // EOF reached
                }

                for (int i = 0; i < actual; i++) {
                    byte val = dataBuf[i];
                    if (val == stopAtByte) {
                        return;
                    }
                    char ch;
                    switch (charMode) {
                        case ConvUtil.ExportCharSrc.HighASCII:
                            ch = (char)(val & 0x7f);
                            break;
                        case ConvUtil.ExportCharSrc.Latin:
                            ch = (char)val;     // this appears to be sufficient
                            if (ch >= 0x80 && ch <= 0x9f) {
                                // Merge into single character.  Do this even if !printableOnly.
                                ch = ASCIIUtil.CTRL_PIC_C1;
                            }
                            break;
                        case ConvUtil.ExportCharSrc.MacOSRoman:
                            ch = MacChar.MacToUnicode(val, MacChar.Encoding.Roman);
                            break;
                        default:
                            Debug.Assert(false);
                            ch = '!';       // should not be here
                            break;
                    }
                    if (ch < 0x20 || ch == 0x7f) {
                        // Non-printable C0 control code.
                        if (ch == '\r') {
                            sb.Append(newline);
                        } else if (ch == '\n') {
                            if (!lastWasCR) {
                                sb.Append(newline);
                            }
                        } else if (ch == '\t') {
                            // Always output tabs as tab characters.
                            sb.Append(ch);
                        } else if (!printableOnly) {
                            sb.Append(ch);      // output raw control code
                        } else {
                            sb.Append(ASCIIUtil.MakePrintable(ch));
                        }
                    } else {
                        // Non-control char.
                        sb.Append(ch);
                    }
                    lastWasCR = (ch == '\r');
                }
            }
        }
    }
}
