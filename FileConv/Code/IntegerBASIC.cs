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
    /// Convert a tokenized Integer BASIC program to text.  The output can be plain text, or
    /// text augmented with syntax highlighting.  The goal is to match the output of the BASIC
    /// LIST command.
    /// </summary>
    public class IntegerBASIC : Converter {
        public const string TAG = "int";
        public const string LABEL = "Integer BASIC";
        public const string DESCRIPTION =
            "Converts Integer BASIC programs to a text listing, with optional syntax " +
            "highlighting.  The output is identical to the LIST command.  Control characters " +
            "can be converted to printable form.";
        public const string DISCRIMINATOR = "ProDOS INT, DOS I.";
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
        /// Maximum conceivable length, assuming the program must fit in RAM.
        /// </summary>
        private const int MAX_LEN = 48 * 1024;

        /// <summary>
        /// Integer tokens, for values < 128.
        /// </summary>
        /// <remarks>
        /// <para>This table comes from FID.C, with some minor edits.  Not all values are allowed
        /// to appear as tokens in a valid program.</para>
        /// </remarks>
        internal static readonly string[] sIntegerTokens = new string[] {
            /*$00*/ "HIMEM:",   "<EOL>",    "_ ",       ":",
                    "LOAD ",    "SAVE ",    "CON ",     "RUN ",
                    "RUN ",     "DEL ",     ",",        "NEW ",
                    "CLR ",     "AUTO ",    ",",        "MAN ",
            /*$10*/ "HIMEM:",   "LOMEM:",   "+",        "-",
                    "*",        "/",        "=",        "#",
                    ">=",       ">",        "<=",       "<>",
                    "<",        "AND ",     "OR ",      "MOD ",
            /*$20*/ "^ ",       "+",        "(",        ",",
                    "THEN ",    "THEN ",    ",",        ",",
                    "\"",       "\"",       "(",        "!",
                    "!",        "(",        "PEEK ",    "RND ",
            /*$30*/ "SGN ",     "ABS ",     "PDL ",     "RNDX ",
                    "(",        "+",        "-",        "NOT ",
                    "(",        "=",        "#",        "LEN(",
                    "ASC(",     "SCRN(",    ",",        "(",
            /*$40*/ "$",        "$",        "(",        ",",
                    ",",        ";",        ";",        ";",
                    ",",        ",",        ",",        "TEXT ",
                    "GR ",      "CALL ",    "DIM ",     "DIM ",
            /*$50*/ "TAB ",     "END ",     "INPUT ",   "INPUT ",
                    "INPUT ",   "FOR ",     "=",        "TO ",
                    "STEP ",    "NEXT ",    ",",        "RETURN ",
                    "GOSUB ",   "REM ",     "LET ",     "GOTO ",
            /*$60*/ "IF ",      "PRINT ",   "PRINT ",   "PRINT ",
                    "POKE ",    ",",        "COLOR=",   "PLOT ",
                    ",",        "HLIN ",    ",",        "AT ",
                    "VLIN ",    ",",        "AT ",      "VTAB ",
            /*$70*/ "=",        "=",        ")",        ")",
                    "LIST ",    ",",        "LIST ",    "POP ",
                    "NODSP ",   "NODSP ",   "NOTRACE ", "DSP ",
                    "DSP ",     "TRACE ",   "PR#",      "IN#"
        };

        private const byte TOK_EOL = 0x01;
        private const byte TOK_COLON = 0x03;
        private const byte TOK_OPEN_QUOTE = 0x28;
        private const byte TOK_CLOSE_QUOTE = 0x29;
        private const byte TOK_REM = 0x5d;

        // Color selections for syntax highlighting.  These may be altered to match the
        // closest available color by the fancy text generator.
        private static readonly int COLOR_DEFAULT = Applesoft.COLOR_DEFAULT;
        private static readonly int COLOR_LINE_NUM = Applesoft.COLOR_LINE_NUM;
        private static readonly int COLOR_KEYWORD = Applesoft.COLOR_KEYWORD;
        private static readonly int COLOR_COMMENT = Applesoft.COLOR_COMMENT;
        private static readonly int COLOR_STRING = Applesoft.COLOR_STRING;
        private static readonly int COLOR_COLON = Applesoft.COLOR_COLON;


        private IntegerBASIC() { }

        public IntegerBASIC(FileAttribs attrs, Stream? dataStream, Stream? rsrcStream,
                ResourceMgr? resMgr, ConvFlags convFlags, AppHook appHook)
                : base(attrs, dataStream, rsrcStream, resMgr, convFlags, appHook) {
            Applic = TestApplicability();
        }

        protected override Applicability TestApplicability() {
            // This is a little tricky because the S-C and LISA assemblers used DOS file type 'I'
            // for their source code.  We need to rule those out.
            if (DataStream == null || IsRawDOS) {
                return Applicability.Not;
            }
            long length = DataStream.Length - (IsRawDOS ? 2 : 0);
            if (FileAttrs.FileType != FileAttribs.FILE_TYPE_INT ||
                    length < 2 || length > MAX_LEN) {
                return Applicability.Not;
            }

            // TODO: screen out S-C assembler and LISA

            return Applicability.Probably;
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

            int textLine = 0;
            int offset = IsRawDOS ? 2 : 0;          // skip length word if present
            while (offset < length) {
                textLine++;
                bool newTrailingSpace = false;

                if (length - offset < 3) {
                    output.Notes.AddW("File may be truncated (at length/num)");
                    break;
                }

                int startOffset = offset;

                // Get the line length byte and line number.
                byte lineLen = dataBuf[offset++];
                if (lineLen == 0) {
                    output.Notes.AddW("Found zero-length line at offset=$" + offset.ToString("x4"));
                    break;
                }
                ushort lineNum = RawData.ReadU16LE(dataBuf, ref offset);

                output.SetForeColor(COLOR_LINE_NUM);
                output.AppendFormat("{0,5} ", lineNum);
                output.SetForeColor(COLOR_DEFAULT);

                // Process the contents of the line.
                bool trailingSpace = true;
                while (dataBuf[offset] != TOK_EOL && offset < length) {
                    byte curByte = dataBuf[offset++];
                    if (curByte == TOK_OPEN_QUOTE) {
                        // Start of quoted text.  Open and close quote are separate tokens, and
                        // not equal to ASCII '"'.
                        output.SetForeColor(COLOR_STRING);
                        output.Append('"');
                        while (dataBuf[offset] != TOK_CLOSE_QUOTE && offset < length) {
                            if (makePrintable) {
                                output.AppendPrintable((char)(dataBuf[offset] & 0x7f));
                            } else {
                                output.Append((char)(dataBuf[offset] & 0x7f));
                            }
                            offset++;
                        }
                        if (dataBuf[offset] != TOK_CLOSE_QUOTE) {
                            output.Notes.AddE("File ended while in a string constant");
                            break;
                        }
                        output.Append('"');
                        output.SetForeColor(COLOR_DEFAULT);
                        offset++;
                    } else if (curByte == TOK_REM) {
                        // REM statement, consume everything to EOL.
                        output.SetForeColor(COLOR_COMMENT);
                        if (!trailingSpace) {
                            output.Append(' ');
                        }
                        output.Append(sIntegerTokens[curByte]);   // output "REM "
                        while (dataBuf[offset] != TOK_EOL && offset < length) {
                            if (makePrintable) {
                                output.AppendPrintable((char)(dataBuf[offset] & 0x7f));
                            } else {
                                output.Append((char)(dataBuf[offset] & 0x7f));
                            }
                            offset++;
                        }
                        if (dataBuf[offset] != TOK_EOL) {
                            output.Notes.AddE("File ended while in a comment");
                            break;
                        }
                        output.SetForeColor(COLOR_DEFAULT);
                    } else if (curByte >= ('0' | 0x80) && curByte <= ('9' | 0x80)) {
                        // Integer constant.  The token's value is not used.
                        if (length - offset < 2) {
                            output.Notes.AddE("File ended while in a numeric constant");
                            break;
                        }
                        ushort val = RawData.ReadU16LE(dataBuf, ref offset);
                        output.Append(val.ToString("D"));
                    } else if (curByte >= ('A' | 0x80) && curByte <= ('Z' | 0x80)) {
                        // Variable name.  "token" holds first letter.
                        output.Append((char)(curByte & 0x7f));
                        while (offset < length) {
                            byte bval = dataBuf[offset++];
                            if ((bval >= ('A' | 0x80) && bval <= ('Z' | 0x80)) ||
                                    (bval >= ('0' | 0x80) && bval <= ('9' | 0x80))) {
                                output.Append((char)(bval & 0x7f));
                            } else {
                                offset--;
                                break;  // this is a token
                            }
                        }
                    } else if (curByte < 0x80) {
                        // Found a token.
                        if (curByte == TOK_COLON) {
                            output.SetForeColor(COLOR_COLON);
                        } else {
                            output.SetForeColor(COLOR_KEYWORD);
                        }
                        string tokStr = sIntegerTokens[curByte];
                        if (tokStr[0] >= 0x21 && tokStr[0] <= 0x3f || curByte < 0x12) {
                            // does not need a leading space
                        } else {
                            // Add a leading space if we didn't just have a trailing space.
                            if (!trailingSpace) {
                                output.Append(' ');
                            }
                        }
                        output.Append(tokStr);
                        output.SetForeColor(COLOR_DEFAULT);

                        if (tokStr[tokStr.Length - 1] == ' ') {
                            newTrailingSpace = true;
                        }
                    } else {
                        // Bad value; either we're out of sync or this isn't a valid program.
                        output.Notes.AddW("Unexpected value $" + curByte.ToString("x2") +
                            " at offset +$" + offset.ToString("X4"));
                        // skip past it and keep trying; probably junk but maybe not?
                    }

                    trailingSpace = newTrailingSpace;
                    newTrailingSpace = false;
                }

                output.NewParagraph();

                // Verify that we ended on the EOL token.  If we didn't, we probably hit something
                // weird and halted early.
                if (dataBuf[offset] != TOK_EOL && length - offset > 0) {
                    output.Notes.AddE("Stopping at offset $" + offset.ToString("x4") +
                        " (length=$" + length.ToString("x4") + ")");
                    break;
                }
                offset++;       // eat the end-of-line token
                if (offset - startOffset != lineLen) {
                    output.Notes.AddW("Line " + lineNum + ": stored length " + lineLen +
                        " did not match actual " + (offset - startOffset));
                }
            }

            return output;
        }
    }
}
