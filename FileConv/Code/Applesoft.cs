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

namespace FileConv.Code {
    /// <summary>
    /// Convert a tokenized Applesoft BASIC program to text.  The output can be plain text, or
    /// text augmented with syntax highlighting.  The goal is match the output of the Applesoft
    /// LIST command (with POKE 33,73 to suppress line breaks).
    /// </summary>
    /// <remarks>
    /// <para>Applesoft programs are very small, so we always generate the syntax-highlighted
    /// form and let the caller choose which version they want.</para>
    /// </remarks>
    public class Applesoft : Converter {
        public const string TAG = "bas";
        public override string Tag => TAG;
        public override string Label => "Applesoft BASIC";

        private const string OPT_HI = "hi";
        private const string OPT_PRINT = "print";

        public override List<OptionDefinition> OptionDefs { get; protected set; } =
            new List<OptionDefinition>() {
                new OptionDefinition(OPT_HI, "Syntax highlighting",
                    OptionDefinition.OptType.Boolean, "false"),
                new OptionDefinition(OPT_PRINT, "Show ctrl chars",
                    OptionDefinition.OptType.Boolean, "true"),
            };

        /// <summary>
        /// Maximum possible length, assuming the program must fit in RAM.
        /// </summary>
        private const int MAX_LEN = 48 * 1024;

        /// <summary>
        /// Applesoft tokens, for values >= 128.  Only values 128-234 are actually used; 235+
        /// show up as error messages.
        /// </summary>
        /// <remarks>
        /// Entries must be upper case, for the benefit of the import parser.
        /// </remarks>
        internal static readonly string[] sApplesoftTokens = new string[] {
        /*$80*/ "END",    "FOR",    "NEXT",   "DATA",   "INPUT",  "DEL",    "DIM",    "READ",
                "GR",     "TEXT",   "PR#",    "IN#",    "CALL",   "PLOT",   "HLIN",   "VLIN",
        /*$90*/ "HGR2",   "HGR",    "HCOLOR=","HPLOT",  "DRAW",   "XDRAW",  "HTAB",   "HOME",
                "ROT=",   "SCALE=", "SHLOAD", "TRACE",  "NOTRACE","NORMAL", "INVERSE","FLASH",
        /*$a0*/ "COLOR=", "POP",    "VTAB",   "HIMEM:", "LOMEM:", "ONERR",  "RESUME", "RECALL",
                "STORE",  "SPEED=", "LET",    "GOTO",   "RUN",    "IF",     "RESTORE","&",
        /*$b0*/ "GOSUB",  "RETURN", "REM",    "STOP",   "ON",     "WAIT",   "LOAD",   "SAVE",
                "DEF",    "POKE",   "PRINT",  "CONT",   "LIST",   "CLEAR",  "GET",    "NEW",
        /*$c0*/ "TAB(",   "TO",     "FN",     "SPC(",   "THEN",   "AT",     "NOT",    "STEP",
                "+",      "-",      "*",      "/",      "^",      "AND",    "OR",     ">",
        /*$d0*/ "=",      "<",      "SGN",    "INT",    "ABS",    "USR",    "FRE",    "SCRN(",
                "PDL",    "POS",    "SQR",    "RND",    "LOG",    "EXP",    "COS",    "SIN",
        /*$e0*/ "TAN",    "ATN",    "PEEK",   "LEN",    "STR$",   "VAL",    "ASC",    "CHR$",
                "LEFT$",  "RIGHT$", "MID$"
        };

        // Color selections for syntax highlighting.  These may be altered to match the
        // closest available color by the fancy text generator.
        private static readonly int COLOR_DEFAULT = ConvUtil.MakeRGB(0x40, 0x40, 0x40);
        private static readonly int COLOR_LINE_NUM = ConvUtil.MakeRGB(0x40, 0x40, 0x40);
        private static readonly int COLOR_KEYWORD = ConvUtil.MakeRGB(0x00, 0x00, 0x00);
        private static readonly int COLOR_COMMENT = ConvUtil.MakeRGB(0x00, 0x80, 0x00);
        private static readonly int COLOR_STRING = ConvUtil.MakeRGB(0x00, 0x00, 0x80);
        private static readonly int COLOR_COLON = ConvUtil.MakeRGB(0xff, 0x00, 0x00);


        public Applesoft(FileAttribs attrs, Stream? dataStream, Stream? rsrcStream,
                ResourceMgr? resMgr, ConvFlags convFlags, AppHook appHook)
                : base(attrs, dataStream, rsrcStream, resMgr, convFlags, appHook) {
            Applic = TestApplicability();
        }

        protected override Applicability TestApplicability() {
            // Simple file type check, with a test for minimum length.
            if (DataStream == null || IsRawDOS) {
                return Applicability.Not;
            }
            long length = DataStream.Length - (IsRawDOS ? 2 : 0);
            if (FileAttrs.FileType == FileAttribs.FILE_TYPE_BAS &&
                    length >= 2 && length <= MAX_LEN) {
                return Applicability.Probably;
            }
            return Applicability.Not;
        }

        public override IConvOutput ConvertFile(Dictionary<string, string> options) {
            if (Applic <= Applicability.Not) {
                Debug.Assert(false);
                return new ErrorText("ERROR");
            }
            Debug.Assert(DataStream != null);

            FancyText output = new FancyText();
            output.PreferSimple = !GetBoolOption(options, OPT_HI, false);
            bool makePrintable = GetBoolOption(options, OPT_PRINT, true);

            // Slurp the full thing into memory.
            DataStream.Position = 0;
            int length = (int)DataStream.Length;
            byte[] dataBuf = new byte[length];
            DataStream.ReadExactly(dataBuf, 0, length);

            int textLine = 0;
            int offset = IsRawDOS ? 2 : 0;          // skip length word if present
            while (offset < length) {
                textLine++;

                // Process start of line.
                if (length - offset < 2) {
                    output.Notes.AddW("File may be truncated (at addr)");
                    break;
                }
                ushort nextAddr = RawData.ReadU16LE(dataBuf, ref offset);
                if (nextAddr == 0) {
                    if (length - offset > 1) {
                        // Allow one extra byte; seems to appear on some files.
                        output.Notes.AddW("File ended early; excess bytes: " + (length - offset));
                    }
                    break;
                }

                if (length - offset < 2) {
                    output.Notes.AddW("File may be truncated (at line num)");
                    break;
                }
                ushort lineNum = RawData.ReadU16LE(dataBuf, ref offset);

                output.SetForeColor(COLOR_LINE_NUM);
                output.Append(' ');
                output.Append(lineNum);
                output.Append(' ');
                output.SetForeColor(COLOR_DEFAULT);

                bool inRem = false;
                bool inQuote = false;
                while (true) {
                    if (length == 0) {
                        output.Notes.AddW("File may be truncated (mid line " + lineNum + ")");
                        break;
                    }
                    byte bval = dataBuf[offset];
                    if (bval == 0x00) {
                        break;      // end of line
                    } else if ((bval & 0x80) != 0) {
                        // Token.
                        output.SetForeColor(COLOR_KEYWORD);
                        output.Append(' ');
                        int tokenIndex = bval & 0x7f;
                        if (tokenIndex < sApplesoftTokens.Length) {
                            output.Append(sApplesoftTokens[tokenIndex]);
                        } else {
                            output.Append("ERROR");
                            output.Notes.AddW("Line " + lineNum +
                                ": found invalid token 0x" + bval.ToString("x2"));
                        }
                        output.Append(' ');

                        if (bval == 0xb2) {
                            // REM statement.  Do rest of line in green.
                            output.SetForeColor(COLOR_COMMENT);
                            inRem = true;
                        } else {
                            output.SetForeColor(COLOR_DEFAULT);
                        }
                    } else {
                        // Literal.
                        if (bval == '"' && !inRem) {
                            if (!inQuote) {
                                output.SetForeColor(COLOR_STRING);
                                output.Append('"');
                            } else {
                                output.Append('"');
                                output.SetForeColor(COLOR_DEFAULT);
                            }
                            inQuote = !inQuote;
                        } else if (bval == ':' && !inRem && !inQuote) {
                            output.SetForeColor(COLOR_COLON);
                            output.Append(':');
                            output.SetForeColor(COLOR_DEFAULT);
                        } else if (inRem && bval == '\r') {
                            // Carriage return in a comment.  Output as a new paragraph if we're
                            // in "make printable".  In non-printable we output the CR without
                            // a paragraph break, so exported files get the raw value.
                            if (makePrintable) {
                                output.NewParagraph();
                                textLine++;
                            } else {
                                output.Append((char)bval);
                            }
                        } else {
                            if (makePrintable) {
                                output.AppendPrintable((char)bval);
                            } else {
                                output.Append((char)bval);
                            }
                        }
                    }

                    offset++;
                }

                // Lines shouldn't end in the middle of a quote, but Applesoft accepts them.
                if (inQuote || inRem) {
                    if (inQuote) {
                        output.Notes.AddI("Line " + textLine + " (#" + lineNum +
                            "): missing closing quote");
                    }
                    output.SetForeColor(COLOR_DEFAULT);
                }

                offset++;       // eat the EOL marker

                output.NewParagraph();
            }

            return output;
        }
    }
}
