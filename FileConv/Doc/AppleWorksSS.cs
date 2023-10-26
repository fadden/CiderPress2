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
    public class AppleWorksSS : Converter {
        public const string TAG = "asp";
        public const string LABEL = "AppleWorks SS";
        public const string DESCRIPTION =
            "Converts an AppleWorks Spreadsheet document to CSV.";
        public const string DISCRIMINATOR = "ProDOS ASP.";
        public override string Tag => TAG;
        public override string Label => LABEL;
        public override string Description => DESCRIPTION;
        public override string Discriminator => DISCRIMINATOR;

        private const int HEADER_LEN = 300;
        private const int DOUBLE_LEN = 8;
        private const int PROP_LABEL_COUNT = 8;

        private const int MIN_LEN = HEADER_LEN + 2;
        private const int MAX_LEN = 256 * 1024;     // arbitrary


        private AppleWorksSS() { }

        public AppleWorksSS(FileAttribs attrs, Stream? dataStream, Stream? rsrcStream,
                ResourceMgr? resMgr, ConvFlags convFlags, AppHook appHook)
                : base(attrs, dataStream, rsrcStream, resMgr, convFlags, appHook) {
            Applic = TestApplicability();
        }

        protected override Applicability TestApplicability() {
            if (DataStream == null || DataStream.Length < MIN_LEN || DataStream.Length > MAX_LEN) {
                return Applicability.Not;
            }
            if (FileAttrs.FileType == FileAttribs.FILE_TYPE_ASP) {
                return Applicability.Yes;
            }
            return Applicability.Not;
        }

        public override Type GetExpectedType(Dictionary<string, string> options) {
            return typeof(CellGrid);
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

            // The first record starts right after the header.  AppleWorks 3.0 inserted an extra
            // two bytes that "are invalid and should be skipped".
            byte ssMinVers = fileBuf[242];      // zero for pre-v3.0, 30 for v3.0
            int recOffset = HEADER_LEN;
            if (ssMinVers != 0) {
                recOffset += 2;
            }

            CellGrid output = new CellGrid();

            int rowIndex = 0;
            while (recOffset < fileBuf.Length) {
                if (fileBuf.Length - recOffset < 2) {
                    output.Notes.AddE("File truncated");
                    return output;
                }
                ushort rowRemain = RawData.ReadU16LE(fileBuf, ref recOffset);
                if (rowRemain == 0xffff) {
                    // Found the end-of-file marker.
                    break;
                }
                if (recOffset + rowRemain > fileBuf.Length) {
                    output.Notes.AddE("File truncated in row index " + rowIndex);
                    return output;
                }
                if (fileBuf[recOffset + rowRemain - 1] != 0xff) {
                    output.Notes.AddW("Row index " + rowIndex + " does not have $ff at end");
                }

                // Process the row record.
                ProcessRowRecord(fileBuf, recOffset, rowRemain, output);

                recOffset += rowRemain;
                rowIndex++;
            }

            AppleWorksWP.DumpTags(fileBuf, recOffset, output);

            return output;
        }

        private static void ProcessRowRecord(byte[] rowBuf, int offset, ushort recordLen,
                CellGrid output) {
            int startOffset = offset;

            ushort rowNum = RawData.ReadU16LE(rowBuf, ref offset);
            if (rowNum == 0) {
                output.Notes.AddW("Found row zero (expected to start at 1)");
            }
            try {
                int colNum = 0;
                while (offset - startOffset < recordLen) {
                    byte ctrl = rowBuf[offset++];
                    if (ctrl == 0xff) {
                        // End of row record.
                        break;
                    } else if (ctrl >= 0x01 && ctrl <= 0x7f) {
                        ProcessCell(rowBuf, offset, ctrl, colNum, rowNum, output);
                        offset += ctrl;
                    } else if (ctrl >= 0x81 && ctrl <= 0xfe) {
                        // Ctrl value minus $80 is the number of columns to skip (1-126).
                        colNum += ctrl - 0x80 - 1;
                    } else {
                        // Invalid value.
                        output.Notes.AddW("Found invalid ctrl value $" + ctrl.ToString("x2") +
                            " in row " + rowNum);
                        return;
                    }

                    colNum++;
                }
            } catch (IndexOutOfRangeException) {
                output.Notes.AddE("Overran record for row " + rowNum);
            }
        }

        /// <summary>
        /// Processes a single cell.  This does minimal range checking, relying on the parent's
        /// exception handler to catch failures.
        /// </summary>
        private static void ProcessCell(byte[] rowBuf, int offset, byte cellLen, int colNum,
                int rowNum, CellGrid output) {
            int startOffset = offset;

            // Some bits are used to determine the nature of the cell contents.  Others affect
            // recalculation and formatting, which don't concern us.
            byte flag0 = rowBuf[offset++];
            if ((flag0 & 0x80) != 0) {
                // Bit 7 set, this is a value.
                byte flag1 = rowBuf[offset++];

                if ((flag0 & 0x20) != 0) {
                    // Bit 5 set, this is a "value constant".  The field holds a 64-bit IEEE 754
                    // floating-point value, which happily is the same format C# uses.
                    double val = GetSANEDouble(rowBuf, offset);
                    output.SetCellValue(colNum, rowNum - 1, val.ToString());
                } else {
                    if ((flag1 & 0x08) != 0) {
                        // Bit 3 set, this is a "value label" (v3.0+)
                        // The data starts with a display string.  This is a cached result of
                        // formatting the tokens.  We want to ignore it.
                        byte strLen = rowBuf[offset++];
                        offset += strLen;
                    } else {
                        // This is a "value formula".
                        // The data starts with a cached computation result.  Ignore it.
                        offset += DOUBLE_LEN;
                    }
                    // Process list of tokens.
                    StringBuilder sb = new StringBuilder();
                    while (offset - startOffset < cellLen) {
                        ProcessToken(rowBuf, ref offset, colNum, rowNum, sb);
                    }
                    output.SetCellValue(colNum, rowNum - 1, sb.ToString());
                }
            } else {
                // Bit 7 clear, this is a label.
                if ((flag0 & 0x20) != 0) {
                    // Bit 5 set, this is a "propagated label" (e.g. a row of hyphens).  The
                    // specified character is repeated 8x.
                    char ch = AppleWorksWP.GetChar(rowBuf, offset++);
                    StringBuilder sb = new StringBuilder(8);
                    for (int i = 0; i < PROP_LABEL_COUNT; i++) {
                        sb.Append(ch);
                    }
                    output.SetCellValue(colNum, rowNum - 1, sb.ToString());
                } else {
                    // This is a "regular label".
                    // The cell holds a string.  The length is determined by the cell length,
                    // rather than a length byte.
                    int labelLen = cellLen - 1;
                    output.SetCellValue(colNum, rowNum - 1,
                        AppleWorksWP.GetString(rowBuf, offset, labelLen));
                }
            }
        }

        private const int TOKEN_START = 0xc0;

        // Token strings.
        //
        // $fc/ellipsis is "..." in AppleWorks, but programs like Excel expect ".." for ranges.
        // For better conversions we could remap the functions to modern equivalents, e.g.
        // change "@avg" to "@average", and preface entries like "(A3)" with "+" or "=" to
        // signal that it's meant to be interpreted as a formula rather than a label.
        private static readonly string[] sTokenTable = new string[0x100 - TOKEN_START] {
            /*$c0*/ "@Deg",     "@Rad",     "@Pi",      "@True",
            /*$c4*/ "@False",   "@Not",     "@IsBlank", "@IsNA",
            /*$c8*/ "@IsError", "@Exp",     "@Ln",      "@Log",
            /*$cc*/ "@Cos",     "@Sin",     "@Tan",     "@ACos",
            /*$d0*/ "@ASin",    "@ATan2",   "@ATan",    "@Mod",
            /*$d4*/ "@FV",      "@PV",      "@PMT",     "@Term",
            /*$d8*/ "@Rate",    "@Round",   "@Or",      "@And",
            /*$dc*/ "@Sum",     "@Avg",     "@Choose",  "@Count",
            /*$e0*/ "@Error",   "@IRR",     "@If",      "@Int",
            /*$e4*/ "@Lookup",  "@Max",     "@Min",     "@NA",
            /*$e8*/ "@NPV",     "@Sqrt",    "@Abs",     "",
            /*$ec*/ "<>",       ">=",       "<=",       "=",
            /*$f0*/ ">",        "<",        ",",        "^",
            /*$f4*/ ")",        "-",        "+",        "/",
            /*$f8*/ "*",        "(",        "-" /*unary*/, "+" /*unary*/,
            /*$fc*/ "..",       "",         "",         ""
        };
        private const byte TOK_ERROR = 0xe0;
        private const byte TOK_NA = 0xe7;
        private const byte TOK_DOUBLE = 0xfd;
        private const byte TOK_RCREF = 0xfe;
        private const byte TOK_STRING = 0xff;

        private static void ProcessToken(byte[] rowBuf, ref int offset, int colNum, int rowNum,
                StringBuilder sb) {
            byte token = rowBuf[offset++];
            if (token < TOKEN_START) {
                //output.Notes.AddE("Unexpected token value $" + token.ToString("x2"));
                return;
            }
            sb.Append(sTokenTable[token - TOKEN_START]);
            switch (token) {
                case TOK_ERROR:
                case TOK_NA:
                    // @Error and @NA followed by three bytes of zero.
                    offset += 3;
                    break;
                case TOK_DOUBLE:
                    // SANE 64-bit float value.
                    double val = GetSANEDouble(rowBuf, offset);
                    sb.Append(val);
                    offset += DOUBLE_LEN;
                    break;
                case TOK_RCREF:
                    // Row/column reference.  Values are signed.
                    sbyte colDelta = (sbyte)rowBuf[offset++];
                    short rowDelta = (short)RawData.ReadU16LE(rowBuf, ref offset);
                    AppendLettersForCol(colNum + colDelta, sb);
                    sb.Append((rowNum + rowDelta).ToString());
                    break;
                case TOK_STRING:
                    // ASCII string (no inverse/MouseText allowed here).  Excel wants this in
                    // double quotes.  It should be in double quotes in AppleWorks, so I don't
                    // think '"' can appear in the string itself.
                    sb.Append('"');
                    byte strLen = rowBuf[offset++];
                    string str = Encoding.ASCII.GetString(rowBuf, offset, strLen);
                    sb.Append(str);
                    sb.Append('"');
                    offset += strLen;
                    break;
                default:
                    // Nothing further to do.
                    break;
            }
        }

        /// <summary>
        /// Extracts a 64-bit little-endian SANE double.
        /// </summary>
        private static double GetSANEDouble(byte[] buf, int offset) {
            long raw = 0;
            for (int i = DOUBLE_LEN - 1; i >= 0; i--) {
                raw = (raw << 8) | buf[offset + i];
            }
            return BitConverter.Int64BitsToDouble(raw);
        }

        private static void AppendLettersForCol(int colNum, StringBuilder sb) {
            if (colNum < 0 || colNum > 127) {
                sb.Append("#ERR#");
            } else if (colNum < 26) {
                sb.Append((char)(colNum + 'A'));
            } else {
                sb.Append((char)(colNum / 26 + 'A' - 1));
                sb.Append((char)(colNum % 26 + 'A'));
            }
        }
    }
}
