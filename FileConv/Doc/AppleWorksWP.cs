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

namespace FileConv.Doc {
    public class AppleWorksWP : Converter {
        public const string TAG = "awp";
        public const string LABEL = "AppleWorks WP";
        public const string DESCRIPTION =
            "Converts an AppleWorks Word Processor document to formatted text.";
        public const string DISCRIMINATOR = "ProDOS AWP.";
        public override string Tag => TAG;
        public override string Label => LABEL;
        public override string Description => DESCRIPTION;
        public override string Discriminator => DISCRIMINATOR;

        public const string OPT_SHOW_EMBEDS = "embed";
        public const string OPT_MTEXT = "mtext";

        public override List<OptionDefinition> OptionDefs { get; protected set; } =
            new List<OptionDefinition>() {
                new OptionDefinition(OPT_SHOW_EMBEDS, "Show embedded formatting",
                    OptionDefinition.OptType.Boolean, "true"),
                new OptionDefinition(OPT_MTEXT, "Show MouseText",
                    OptionDefinition.OptType.Boolean, "true"),
            };

        private const int HEADER_LEN = 300;
        private const int MIN_LEN = HEADER_LEN + 2;
        private const int MAX_LEN = 256 * 1024;     // arbitrary

        private const int NUM_TAB_STOPS = 80;
        private const int NUM_TAB_RULERS = 6;
        private static readonly int DEFAULT_FG_COLOR = ConvUtil.MakeRGB(0x00, 0x00, 0x00);
        private static readonly int DEFAULT_BG_COLOR = ConvUtil.MakeRGB(0xff, 0xff, 0xff);
        private static readonly int EMBED_FG_COLOR = ConvUtil.MakeRGB(0x00, 0x00, 0xff);
        private static readonly int INVERSE_FG_COLOR = ConvUtil.MakeRGB(0xff, 0xff, 0xff);
        private static readonly int INVERSE_BG_COLOR = ConvUtil.MakeRGB(0x00, 0x00, 0x00);

#if false
        // Tab stop values for v1/v2.
        private byte TAB_NONE = (byte)'=';
        private byte TAB_BASIC = (byte)'|';
        // v3 replaced '|' with the following:
        private byte TAB_LEFT = (byte)'<';
        private byte TAB_CENTER = (byte)'^';
        private byte TAB_RIGHT = (byte)'>';
        private byte TAB_DECIMAL = (byte)'.';
#endif

        /// <summary>
        /// Fixed-size AWP header.
        /// </summary>
        private class Header {
            public byte[] mUnused1 = new byte[4];                   // 000 - 003
            public byte mSeventyNine;                               // 004
            public byte[] mTabStops = new byte[NUM_TAB_STOPS];      // 005 - 084
            public byte mZoomSwitch;                                // 085
            public byte[] mUnused2 = new byte[4];                   // 086 - 089
            public byte mPaginatedFlag;                             // 090
            public byte mMinLeftMargin;                             // 091
            public byte mMailMergeFlag;                             // 092
            public byte[] mUnused3 = new byte[83];                  // 093-175
            public byte mMultiRulerFlag;                            // 176
            public byte[] mTabRulers = new byte[NUM_TAB_RULERS];    // 177-182
            public byte mSFMinVers;                                 // 183
            public byte[] mUnused4 = new byte[66];                  // 184-249
            public byte[] mAvailable = new byte[50];                // 250-299

            public void Load(byte[] buf, int offset) {
                int startOffset = offset;

                CopyIn(buf, ref offset, mUnused1);
                mSeventyNine = buf[offset++];
                CopyIn(buf, ref offset, mTabStops);
                mZoomSwitch = buf[offset++];
                CopyIn(buf, ref offset, mUnused2);
                mPaginatedFlag = buf[offset++];
                mMinLeftMargin = buf[offset++];
                mMailMergeFlag = buf[offset++];
                CopyIn(buf, ref offset, mUnused3);
                mMultiRulerFlag = buf[offset++];
                CopyIn(buf, ref offset, mTabRulers);
                mSFMinVers = buf[offset++];
                CopyIn(buf, ref offset, mUnused4);
                CopyIn(buf, ref offset, mAvailable);
                Debug.Assert(offset - startOffset == HEADER_LEN);
            }

            private static void CopyIn(byte[] srcBuf, ref int srcOffset, byte[] dstBuf) {
                Array.Copy(srcBuf, srcOffset, dstBuf, 0, dstBuf.Length);
                srcOffset += dstBuf.Length;
            }
        }

        private bool mShowEmbeds;
        private bool mShowMouseText;
        private bool mDidParaBreak;
        private bool mIsInverse;


        private AppleWorksWP() { }

        public AppleWorksWP(FileAttribs attrs, Stream? dataStream, Stream? rsrcStream,
                ResourceMgr? resMgr, ConvFlags convFlags, AppHook appHook)
                : base(attrs, dataStream, rsrcStream, resMgr, convFlags, appHook) {
            Applic = TestApplicability();
        }

        protected override Applicability TestApplicability() {
            if (DataStream == null || DataStream.Length < MIN_LEN || DataStream.Length > MAX_LEN) {
                return Applicability.Not;
            }
            if (FileAttrs.FileType == FileAttribs.FILE_TYPE_AWP) {
                return Applicability.Yes;
            }
            return Applicability.Not;
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

            mShowEmbeds = GetBoolOption(options, OPT_SHOW_EMBEDS, true);
            mShowMouseText = GetBoolOption(options, OPT_MTEXT, true);

            Header hdr = new Header();
            hdr.Load(fileBuf, 0);
            if (hdr.mSeventyNine != 79) {
                return new ErrorText("Incorrect signature byte value");
            }

            FancyText output = new FancyText();
            if (hdr.mSFMinVers != 0 && hdr.mSFMinVers != 30) {
                output.Notes.AddW("Unexpected value for sfMinVers: " + hdr.mSFMinVers);
            }
            bool skipWord = (hdr.mSFMinVers >= 30);

            // Set left/right margins to 1 inch.  AppleWorks uses 10 CPI for screen and print,
            // which might be closer to 12 points than the default 10.
            output.SetLeftMargin(1.0f);
            output.SetRightMargin(1.0f);

            // No need to start a new paragraph if the first thing in the file is a line command.
            mDidParaBreak = true;

            // TODO(someday): process tab stops in ruler

            // Read the line records.
            int offset = HEADER_LEN;
            while (true) {
                try {
                    byte lineRecData = fileBuf[offset++];
                    byte lineRecCode = fileBuf[offset++];
                    if (skipWord) {
                        skipWord = false;
                        continue;
                    }
                    if (lineRecData == 0xff && lineRecCode == 0xff) {
                        // EOF marker.
                        break;
                    }

                    ProcessLineRecord(fileBuf, ref offset, lineRecData, lineRecCode, output);
                } catch (IndexOutOfRangeException) {
                    output.Notes.AddE("File was truncated");
                    return output;
                }
            }

            DumpTags(fileBuf, offset, output);

            return output;
        }

        private const byte LINE_CODE_TEXT = 0x00;
        private const int LINE_CODE_CR = 0xd0;
        private const byte LINE_CODE_CMD_MIN = 0xd1;

        private void ProcessLineRecord(byte[] buf, ref int offset,
                byte lineRecData, byte lineRecCode, FancyText output) {
            if (lineRecCode == LINE_CODE_CR) {
                // Ignoring horizontal offset.
                output.NewParagraph();
                mDidParaBreak = true;
            } else if (lineRecCode == LINE_CODE_TEXT) {
                ProcessTextRecord(buf, ref offset, lineRecData, output);
            } else if (lineRecCode >= LINE_CODE_CMD_MIN) {
                if (!mDidParaBreak) {
                    output.NewParagraph();
                    mDidParaBreak = true;
                }
                ProcessCommandLineRecord(lineRecData, lineRecCode, output);
            } else {
                output.Notes.AddW("Found bad line record command code $" +
                    lineRecCode.ToString("x2"));
            }
        }

        // Codes used in Command Line Records.
        private enum CmdCode : byte {
            ReservedD4 = 0xd4,                  // "used internally as ruler"
            PageHeaderEnd = 0xd5,
            PageFooterEnd = 0xd6,
            RightJustified = 0xd7,
            PlatenWidth = 0xd8,                 // data = 1/10th inch
            LeftMargin = 0xd9,                  // data = 1/10th inch
            RightMargin = 0xda,                 // data = 1/10th inch
            CharsPerInch = 0xdb,                // data = number of chars
            Proportional1 = 0xdc,
            Proportional2 = 0xdd,
            Indent = 0xde,                      // data = number of chars
            Justify = 0xdf,
            Unjustify = 0xe0,
            Center = 0xe1,
            PaperLength = 0xe2,                 // data = 1/10th inch
            TopMargin = 0xe3,                   // data = 1/10th inch
            BottomMargin = 0xe4,                // data = 1/10th inch
            LinesPerInch = 0xe5,                // data = number of lines
            SingleSpace = 0xe6,
            DoubleSpace = 0xe7,
            TripleSpace = 0xe8,
            NewPage = 0xe9,
            GroupBegin = 0xea,
            GroupEnd = 0xeb,
            PageHeader = 0xec,
            PageFooter = 0xed,
            SkipLines = 0xee,                   // data = number of lines
            PageNumber = 0xef,                  // data = page number
            PauseEachPage = 0xf0,
            PauseHere = 0xf1,
            SetMarker = 0xf2,                   // data = marker number
            PageNumber256 = 0xf3,               // data = (add 256)
            PageBreak = 0xf4,                   // data = page number
            PageBreak256 = 0xf5,                // data = (add 256)
            PageBreakPara = 0xf6,               // data = (break in middle of paragraph)
            PageBreakPara256 = 0xf7,            // data = (add 256 in middle of paragraph)
            // ...
            EndOfFile = 0xff
        }

        private void ProcessCommandLineRecord(byte lineRecData, byte lineRecCode,
                FancyText output) {
            CmdCode code = (CmdCode)lineRecCode;
            switch (code) {
                case CmdCode.Unjustify:
                    output.SetJustification(FancyText.Justification.Left);
                    break;
                case CmdCode.RightJustified:
                    output.SetJustification(FancyText.Justification.Right);
                    break;
                case CmdCode.Center:
                    output.SetJustification(FancyText.Justification.Center);
                    break;
                case CmdCode.Justify:
                    output.SetJustification(FancyText.Justification.Full);
                    break;
                case CmdCode.LeftMargin:
                    output.SetLeftMargin(lineRecData / 10.0f);
                    break;
                case CmdCode.RightMargin:
                    output.SetRightMargin(lineRecData / 10.0f);
                    break;

                case CmdCode.PageNumber:
                    ShowEmbed("<set-page-number " + lineRecData + ">", output);
                    break;
                case CmdCode.PageHeader:
                    ShowEmbed("<page-header>", output);
                    break;
                case CmdCode.PageHeaderEnd:
                    ShowEmbed("</page-header>", output);
                    break;
                case CmdCode.PageFooter:
                    ShowEmbed("<page-footer>", output);
                    break;
                case CmdCode.PageFooterEnd:
                    ShowEmbed("</page-footer>", output);
                    break;
                case CmdCode.PageBreak:
                    ShowEmbed("<page-break>", output);
                    output.NewPage();
                    break;

                // TODO(someday):
                // Might be able to handle Indent with RTF \fiN \liN.  \li is used to implement
                // the left margin, so we'd increase that while adjusting the first-line indent.
                //
                // Looks like single/double/triple spacing in RTF is \sl0\slmultN ?
                //
                // For Proportional1/2 we can switch to a non-monospace font.
                //
                // For CPI we can change the font size.  Lines per inch requires changing the
                // line spacing.
                //
                // SkipLines seems straightforward?

                default:
                    Debug.WriteLine("Ignoring AWP command $" + lineRecCode.ToString("x2") +
                        " data=$" + lineRecData.ToString("x2"));
                    break;
            }
        }

        private enum CharCode : byte {
            BoldBegin = 0x01,
            BoldEnd = 0x02,
            SuperscriptBegin = 0x03,
            SuperscriptEnd = 0x04,
            SubscriptBegin = 0x05,
            SubscriptEnd = 0x06,
            UnderlineBegin = 0x07,
            UnderlineEnd = 0x08,
            PrintPageNumber = 0x09,
            EnterKeyboard = 0x0a,
            StickySpace = 0x0b,
            BeginMailMerge = 0x0c,
            Reserved0D = 0x0d,
            PrintDate = 0x0e,
            PrintTime = 0x0f,
            SpecialCode1 = 0x10,
            SpecialCode2 = 0x11,
            SpecialCode3 = 0x12,
            SpecialCode4 = 0x13,
            SpecialCode5 = 0x14,
            SpecialCode6 = 0x15,
            Tab = 0x16,
            TabFill = 0x17,
            Reserved18 = 0x18,
        }

        private void ProcessTextRecord(byte[] buf, ref int offset, byte recordLen,
                FancyText output) {
            int startOffset = offset;
            byte crPosTabFlag = buf[offset++];
            byte remCountCrFlag = buf[offset++];
            if ((remCountCrFlag & 0x7f) != recordLen - 2) {
                // The text byte count seems redundant.  Make sure.
                output.Notes.AddW("Byte count mismatch: record len=" + recordLen +
                    ", text count=" + (remCountCrFlag & 0x7f));
            }

            if ((crPosTabFlag & 0x80) != 0) {
                // This line has "tab filler", or is a ruler.  Don't show rulers.
                if (crPosTabFlag == 0xff) {
                    //output.Notes.AddI("Found ruler in text record (offset=$" +
                    //    offset.ToString("x4") + ")");
                    offset = startOffset + recordLen;
                    return;
                }
            }

            while (offset < startOffset + recordLen) {
                byte ch = buf[offset++];
                if (ch < 0x20) {
                    // Control character range; has special meaning.
                    CharCode charCode = (CharCode)ch;
                    switch (charCode) {
                        case CharCode.BoldBegin:
                            output.SetBold(true);
                            break;
                        case CharCode.BoldEnd:
                            output.SetBold(false);
                            break;
                        case CharCode.SuperscriptBegin:
                            output.SetSuperscript(true);
                            break;
                        case CharCode.SuperscriptEnd:
                            output.SetSuperscript(false);
                            break;
                        case CharCode.SubscriptBegin:
                            output.SetSubscript(true);
                            break;
                        case CharCode.SubscriptEnd:
                            output.SetSubscript(false);
                            break;
                        case CharCode.UnderlineBegin:
                            output.SetUnderline(true);
                            break;
                        case CharCode.UnderlineEnd:
                            output.SetUnderline(false);
                            break;
                        case CharCode.EnterKeyboard:
                            ShowEmbed("<kbd-entry>", output);
                            break;
                        case CharCode.PrintPageNumber:
                            ShowEmbed("<page#>", output);
                            break;
                        case CharCode.StickySpace:
                            output.Append(FancyText.NO_BREAK_SPACE);
                            break;
                        case CharCode.BeginMailMerge:
                            ShowEmbed("<mail-merge>", output);
                            break;
                        case CharCode.PrintDate:
                            ShowEmbed("<date>", output);
                            break;
                        case CharCode.PrintTime:
                            ShowEmbed("<time>", output);
                            break;
                        case CharCode.Tab:
                            output.Tab();
                            break;
                        case CharCode.TabFill:
                            // Tab fill character.  Not visible in document?
                            output.Append(' ');
                            break;
                        default:
                            ShowEmbed("^", output);
                            output.Notes.AddI("Unhandled special text character $" +
                                ch.ToString("x2"));
                            break;
                    }
                } else {
                    // ASCII, inverse, or MouseText character.
                    uint charValue = ConvChar(ch, mShowMouseText, out bool charInverse);
                    if (charInverse && !mIsInverse) {
                        // Switch to inverse text mode.
                        output.SetForeColor(INVERSE_FG_COLOR);
                        output.SetBackColor(INVERSE_BG_COLOR);
                        mIsInverse = true;
                    } else if (!charInverse && mIsInverse) {
                        output.SetForeColor(DEFAULT_FG_COLOR);
                        output.SetBackColor(DEFAULT_BG_COLOR);
                        mIsInverse = false;
                    }

                    if (charValue == (char)charValue) {
                        // Simple character.
                        output.Append((char)charValue);
                    } else {
                        // Unpack the surrogate pair.
                        char hiPart = (char)(charValue >> 16);
                        char loPart = (char)charValue;
                        output.Append(hiPart);
                        output.Append(loPart);
                    }
                }
            }

            // Don't let inverse mode carry across text chunks.
            if (mIsInverse) {
                output.SetForeColor(DEFAULT_FG_COLOR);
                output.SetBackColor(DEFAULT_BG_COLOR);
                mIsInverse = false;
            }

            // Handle carriage return at end of line.  Ignore horizontal position.
            if ((remCountCrFlag & 0x80) != 0) {
                output.NewParagraph();
                mDidParaBreak = true;
            } else {
                mDidParaBreak = false;
            }
        }

        /// <summary>
        /// Displays a string in blue text if "show embeds" is enabled.
        /// </summary>
        /// <param name="text">String to display.</param>
        /// <param name="output">Output object.</param>
        private void ShowEmbed(string text, FancyText output) {
            if (mShowEmbeds) {
                output.SetForeColor(EMBED_FG_COLOR);
                output.Append(text);
                if (mIsInverse) {
                    output.SetForeColor(INVERSE_FG_COLOR);
                } else {
                    output.SetForeColor(DEFAULT_FG_COLOR);
                }
            }
        }

        #region Common

        /// <summary>
        /// Gets a single character from the buffer.  If MouseText is allowed, a suitable
        /// Unicode character will be returned; otherwise it will be reduced to ASCII.
        /// </summary>
        /// <param name="bch">Raw character to convert.</param>
        /// <param name="allowMouseText">If true, return MouseText-ish Unicode.</param>
        /// <param name="isInverse">Result: true if character is inverse (white on black).</param>
        /// <returns>32-bit value, with the character value in the low 16 bits.  If a surrogate
        /// pair is required (only for MouseText), the upper part of the pair will be in the
        /// upper part of the return value.</returns>
        public static uint ConvChar(byte bch, bool allowMouseText, out bool isInverse) {
            isInverse = false;
            if (bch < 0x80) {
                // Plain ASCII, though we might want to do something with DEL (0x7f).
                return (char)bch;
            } else if (bch <= 0x9f) {
                // 0x80-0x9f is inverse upper; map 100x xxxx --> 010x xxxx
                isInverse = true;
                return (char)(bch ^ 0xc0);
            } else if (bch >= 0xc0 && bch <= 0xdf) {
                // MouseText characters.
                if (allowMouseText) {
                    string chars = MouseTextChar.MouseTextToUnicode((byte)(bch & 0x1f));
                    if (chars.Length == 1) {
                        return chars[0];
                    } else if (chars.Length == 2) {
                        // A surrogate pair was needed.  Combine the two UTF-16 code points into
                        // a single 32-bit value.
                        return (uint)(chars[0] | (chars[1] << 16));
                    } else {
                        Debug.Assert(false);
                        return '!';
                    }
                } else {
                    return MouseTextChar.MouseTextToASCII((byte)(bch & 0x1f));
                }
            } else {
                // 0xa0-0xbf is inverse symbols; map 101x xxxx --> 001x xxxx
                // 0xe0-0xff is inverse lower; map 111x xxxx --> 011x xxxx
                isInverse = true;
                return (char)(bch & 0x7f);
            }
        }

        /// <summary>
        /// Gets a single character from the buffer, normalizing inverse and MouseText to
        /// plain ASCII.
        /// </summary>
        public static char GetChar(byte[] buf, int offset) {
            return (char)ConvChar(buf[offset], false, out bool unused);
        }

        /// <summary>
        /// Gets a string from the buffer, normalizing inverse and MouseText to plain ASCII.
        /// </summary>
        public static string GetString(byte[] buf, int offset, int strLen) {
            StringBuilder sb = new StringBuilder(strLen);
            for (int i = 0; i < strLen; i++) {
                sb.Append((char)ConvChar(buf[offset++], false, out bool unused));
            }
            return sb.ToString();
        }

        /// <summary>
        /// Dumps the optional tags appended to an AppleWorks document.
        /// </summary>
        /// <param name="fileBuf">Data buffer.</param>
        /// <param name="offset">Offset of first byte after the end-of-file marker ($ffff).</param>
        /// <param name="output">Converter output object.  Values will be added to Notes.</param>
        public static void DumpTags(byte[] fileBuf, int offset, IConvOutput output) {
            if (offset + 4 >= fileBuf.Length) {
                // No tags.
                return;
            }
            output.Notes.AddI("Found tags (" + (fileBuf.Length - offset) + " bytes)");
            // I have yet to find a file that actually has tags, so I'm not doing anything
            // with this yet.
        }

        #endregion Common
    }
}
