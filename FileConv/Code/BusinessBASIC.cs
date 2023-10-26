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
    /// Convert a tokenized Apple /// Business BASIC program to text.  The output can be plain
    /// text, or augmented with syntax highlighting.
    /// </summary>
    /// <remarks>
    /// This is based on David Schmidt's implementation for the original CiderPress.
    /// </remarks>
    public class BusinessBASIC : Converter {
        public const string TAG = "ba3";
        public const string LABEL = "Business BASIC";
        public const string DESCRIPTION =
            "Converts Apple /// Business BASIC programs to a text listing, with optional syntax " +
            "highlighting.  Control characters can be converted to printable form.";
        public const string DISCRIMINATOR = "ProDOS BA3.";
        public override string Tag => TAG;
        public override string Label => LABEL;
        public override string Description => DESCRIPTION;
        public override string Discriminator => DISCRIMINATOR;

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
        /// Longest possible length of a file.
        /// TODO: find out if this is correct
        /// </summary>
        private const int MAX_LEN = 48 * 1024;

        private const string InvalTok = "*error*";  // use for invalid tokens

        internal static readonly string[] sBusinessTokens = new string[] {
        /*$80*/ "END",      "FOR",      "NEXT",     "INPUT",
                "OUTPUT",   "DIM",      "READ",     "WRITE",
                "OPEN",     "CLOSE",    InvalTok,   "TEXT",
                InvalTok,   "BYE",      InvalTok,   InvalTok,
        /*$90*/ InvalTok,   InvalTok,   InvalTok,   "WINDOW",
                "INVOKE",   "PERFORM",  InvalTok,   InvalTok,
                "FRE",      "HPOS",     "VPOS",     "ERRLIN",
                "ERR",      "KBD",      "EOF",      "TIME$",
        /*$a0*/ "DATE$",    "PREFIX$",  "EXFN.",    "EXFN%.",
                "OUTREC",   "INDENT",   InvalTok,   InvalTok,
                InvalTok,   InvalTok,   InvalTok,   InvalTok,
                InvalTok,   "POP",      "HOME",     InvalTok,
        /*$b0*/ "SUB$(",    "OFF",      "TRACE",    "NOTRACE",
                "NORMAL",   "INVERSE",  "SCALE(",   "RESUME",
                InvalTok,   "LET",      "GOTO",     "IF",
                "RESTORE",  "SWAP",     "GOSUB",    "RETURN",
        /*$c0*/ "REM",      "STOP",     "ON",       InvalTok,
                "LOAD",     "SAVE",     "DELETE",   "RUN",
                "RENAME",   "LOCK",     "UNLOCK",   "CREATE",
                "EXEC",     "CHAIN",    InvalTok,   InvalTok,
        /*$d0*/ InvalTok,   "CATALOG",  InvalTok,   InvalTok,
                "DATA",     "IMAGE",    "CAT",      "DEF",
                InvalTok,   "PRINT",    "DEL",      "ELSE",
                "CONT",     "LIST",     "CLEAR",    "GET",
        /*$e0*/ "NEW",      "TAB",      "TO",       "SPC(",
                "USING",    "THEN",     InvalTok,   "MOD",
                "STEP",     "AND",      "OR",       "EXTENSION",
                "DIV",      InvalTok,   "FN",       "NOT",
        /*$f0*/ InvalTok,   InvalTok,   InvalTok,   InvalTok,
                InvalTok,   InvalTok,   InvalTok,   InvalTok,
                InvalTok,   InvalTok,   InvalTok,   InvalTok,
                InvalTok,   InvalTok,   InvalTok,   "*extend*",
        };
        private const byte TOK_FOR = 0x81;
        private const byte TOK_NEXT = 0x82;
        private const byte TOK_REM = 0xc0;
        private const byte TOK_EXTEND = 0xff;

        internal static readonly string[] sExtendedBusinessTokens = new string[] {
        /*$80*/ "TAB(",     "TO",       "SPC(",     "USING",
                "THEN",     InvalTok,   "MOD",      "STEP",
                "AND",      "OR",       "EXTENSION","DIV",
                InvalTok,   "FN",       "NOT",      InvalTok,
        /*$90*/ InvalTok,   InvalTok,   InvalTok,   InvalTok,
                InvalTok,   InvalTok,   InvalTok,   InvalTok,
                InvalTok,   InvalTok,   InvalTok,   InvalTok,
                "AS",       "SGN(",     "INT(",     "ABS(",
        /*$a0*/ InvalTok,   "TYP(",     "REC(",     InvalTok,
                InvalTok,   InvalTok,   InvalTok,   InvalTok,
                InvalTok,   InvalTok,   InvalTok,   InvalTok,
                InvalTok,   "PDL(",     "BUTTON(",  "SQR(",
        /*$b0*/ "RND(",     "LOG(",     "EXP(",     "COS(",
                "SIN(",     "TAN(",     "ATN(",     InvalTok,
                InvalTok,   InvalTok,   InvalTok,   InvalTok,
                InvalTok,   InvalTok,   InvalTok,   InvalTok,
        /*$c0*/ InvalTok,   InvalTok,   InvalTok,   "STR$(",
                "HEX$(",    "CHR$(",    "LEN(",     "VAL(",
                "ASC(",     "TEN(",     InvalTok,   InvalTok,
                "CONV(",    "CONV&(",   "CONV$(",   "CONV%(",
        /*$d0*/ "LEFT$(",   "RIGHT$(",  "MID$(",    "INSTR(",
                InvalTok,   InvalTok,   InvalTok,   InvalTok,
                InvalTok,   InvalTok,   InvalTok,   InvalTok,
                InvalTok,   InvalTok,   InvalTok,   InvalTok,
        /*$e0*/ InvalTok,   InvalTok,   InvalTok,   InvalTok,
                InvalTok,   InvalTok,   InvalTok,   InvalTok,
                InvalTok,   InvalTok,   InvalTok,   InvalTok,
                InvalTok,   InvalTok,   InvalTok,   InvalTok,
        /*$f0*/ InvalTok,   InvalTok,   InvalTok,   InvalTok,
                InvalTok,   InvalTok,   InvalTok,   InvalTok,
                InvalTok,   InvalTok,   InvalTok,   InvalTok,
                InvalTok,   InvalTok,   InvalTok,   InvalTok,
        };

        // Color selections for syntax highlighting.  These may be altered to match the
        // closest available color by the fancy text generator.
        private static readonly int COLOR_DEFAULT = Applesoft.COLOR_DEFAULT;
        private static readonly int COLOR_LINE_NUM = Applesoft.COLOR_LINE_NUM;
        private static readonly int COLOR_KEYWORD = Applesoft.COLOR_KEYWORD;
        private static readonly int COLOR_COMMENT = Applesoft.COLOR_COMMENT;
        private static readonly int COLOR_STRING = Applesoft.COLOR_STRING;
        private static readonly int COLOR_COLON = Applesoft.COLOR_COLON;


        private BusinessBASIC() { }

        public BusinessBASIC(FileAttribs attrs, Stream? dataStream, Stream? rsrcStream,
                ResourceMgr? resMgr, ConvFlags convFlags, AppHook appHook)
                : base(attrs, dataStream, rsrcStream, resMgr, convFlags, appHook) {
            Applic = TestApplicability();
        }

        protected override Applicability TestApplicability() {
            if (DataStream == null) {
                return Applicability.Not;
            }
            long length = DataStream.Length - (IsRawDOS ? 2 : 0);
            if (FileAttrs.FileType == FileAttribs.FILE_TYPE_BA3 &&
                    length >= 4 && length <= MAX_LEN) {
                return Applicability.Yes;
            }
            return Applicability.Not;
        }

        public override Type GetExpectedType(Dictionary<string, string> options) {
            if (GetBoolOption(options, OPT_HI, false)) {
                return typeof(FancyText);
            } else {
                return typeof(SimpleText);
            }
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

            int offset = 0;
            ushort fileLength = RawData.ReadU16LE(dataBuf, ref offset);
            if (fileLength != length - 2) {
                // Sometimes the embedded length is zero.
                output.Notes.AddI("Embedded file length is " + fileLength +
                    ", but actual length is " + length);
            }

            int textLine = 0;
            int nestLevels = 0;
            while (offset < length) {
                bool inQuote = false;
                bool inRem = false;
                bool firstData = true;
                bool literalYet = false;

                textLine++;

                // Process start of line.
                if (length - offset < 1) {
                    output.Notes.AddW("File may be truncated (at line len byte)");
                    break;
                }
                int startOffset = offset;

                byte lineLen = dataBuf[offset++];
                if (lineLen == 0) {
                    // Should be two bytes from the end.  Sometimes the EOF marker is dropped
                    // and we'll be zero bytes from the end.
                    if (length - offset > 2) {
                        output.Notes.AddW("Stopping at zero-length line; offset=" + offset +
                            ", length=" + length);
                    }
                    break;
                }
                if (length - offset < 3) {
                    output.Notes.AddW("File may be truncated (at line number)");
                    break;
                }
                ushort lineNum = RawData.ReadU16LE(dataBuf, ref offset);

                output.SetForeColor(COLOR_LINE_NUM);
                output.Append(' ');
                output.Append(lineNum);
                output.Append("   ");
                output.SetForeColor(COLOR_DEFAULT);

                if (nestLevels > 0) {
                    for (int i = 0; i < nestLevels; i++) {
                        output.Append("  ");
                    }
                }

                // Process a line.
                while (true) {
                    if (length - offset == 0) {
                        output.Notes.AddW("File may be truncated (mid line " + lineNum + ")");
                        break;
                    }
                    byte bval = dataBuf[offset];
                    if (bval == 0x00) {
                        break;      // end of line reached
                    } else if ((bval & 0x80) != 0) {
                        // Found a token.
                        literalYet = false;
                        if (bval == TOK_FOR) {
                            // Increase the indent level.
                            nestLevels++;
                        } else if (bval == TOK_NEXT) {
                            // Decrease the indent level.
                            nestLevels--;
                        }
                        if (!firstData) {
                            output.Append(' ');
                        }

                        output.SetForeColor(COLOR_KEYWORD);
                        if (bval == TOK_EXTEND) {
                            // This is an extended (two-byte) token.
                            offset++;
                            if (length - offset == 0) {
                                output.Notes.AddW("File ended early (mid token " + lineNum + ")");
                                break;
                            }
                            byte extTok = dataBuf[offset];
                            string tokStr = sExtendedBusinessTokens[extTok & 0x7f];
                            output.Append(tokStr);
                            // Supppress trailing space for tokens that end with '('.
                            firstData = (tokStr[tokStr.Length - 1] == '(');
                            output.SetForeColor(COLOR_DEFAULT);
                        } else {
                            // Common (one-byte) token.
                            output.Append(sBusinessTokens[bval & 0x7f]);
                            firstData = (
                                    bval == 0x99 ||     // HPOS
                                    bval == 0x9a ||     // VPOS
                                    bval == 0x9f ||     // TIME$
                                    bval == 0xa0 ||     // DATE$
                                    bval == 0xa1 ||     // PREFIX$
                                    bval == 0xa2 ||     // EXFN.
                                    bval == 0xa3 ||     // EXFN%.
                                    bval == 0xb0 ||     // SUB$(
                                    bval == 0xb6 ||     // SCALE(
                                    bval == 0xc0 ||     // REM
                                    bval == 0xe3);      // SPC(

                            // Check for REM token.  Do the rest of the line in the comment color.
                            if (bval == TOK_REM) {
                                output.SetForeColor(COLOR_COMMENT);
                                inRem = true;
                            } else {
                                output.SetForeColor(COLOR_DEFAULT);
                            }
                        }
                    } else {
                        // Literal character.
                        if (bval == (byte)':') {
                            firstData = true;
                        }
                        if (!firstData) {
                            if (!literalYet) {
                                output.Append(' ');
                                literalYet = true;
                            }
                        }

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

                if (inQuote || inRem) {
                    if (inQuote) {
                        output.Notes.AddI("Line " + textLine + " (#" + lineNum +
                            "): missing closing quote");
                    }
                    output.SetForeColor(COLOR_DEFAULT);
                }

                offset++;       // eat the EOL marker
                output.NewParagraph();

                // Should still be an EOF marker coming.
                if (length - offset == 0) {
                    output.Notes.AddW("File truncated mid-line " + lineNum);
                    break;
                }
                if (offset - startOffset != lineLen) {
                    output.Notes.AddW("Line " + lineNum + ": stored length " + lineLen +
                        " did not match actual " + (offset - startOffset));
                }
            }

            return output;
        }
    }
}
