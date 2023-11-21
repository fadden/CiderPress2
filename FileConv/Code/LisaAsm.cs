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
using DiskArc.FS;

namespace FileConv.Code {
    /// <summary>
    /// Handles all three major versions of Lazer's Interactive Symbolic Assembler.
    /// </summary>
    public class LisaAsm : Converter {
        public const string TAG = "lisasm";
        public const string LABEL = "LISA assembler";
        public const string DESCRIPTION = "Converts LISA v2-v5 source code to plain text.";
        public const string DISCRIMINATOR = "DOS BB, or ProDOS INT with specific auxtypes.";
        public override string Tag => TAG;
        public override string Label => LABEL;
        public override string Description => DESCRIPTION;
        public override string Discriminator => DISCRIMINATOR;

        private const int MIN_LEN_V2 = 4 + 3;       // header + file end marker
        private const int MIN_LEN_V3 = 4 + 2;       // header + trivial source
        private const int MAX_LEN = 64 * 1024;      // arbitrary cap, should apply to all versions

        // LISA v2 used the "alternate B" type.  Get the ProDOS type mapping for it.
        private static byte FILE_TYPE_BB = DOS_FileEntry.TypeToProDOS(DOS_FileEntry.TYPE_BB);


        private LisaAsm() { }

        public LisaAsm(FileAttribs attrs, Stream? dataStream, Stream? rsrcStream,
                ResourceMgr? resMgr, ConvFlags convFlags, AppHook appHook)
                : base(attrs, dataStream, rsrcStream, resMgr, convFlags, appHook) {
            Applic = TestApplicability();
        }

        protected override Applicability TestApplicability() {
            if (DataStream == null) {
                return Applicability.Not;
            }
            if (FileAttrs.FileType == FILE_TYPE_BB) {
                if (DataStream.Length < MIN_LEN_V2 || DataStream.Length > MAX_LEN) {
                    return Applicability.Not;
                }
                DataStream.Position = 0;
                ushort version = RawData.ReadU16LE(DataStream, out bool ok);
                ushort length = RawData.ReadU16LE(DataStream, out ok);
                if (!ok) {
                    return Applicability.Not;       // unexpected
                }
                if (length < MIN_LEN_V2 || length > DataStream.Length) {
                    return Applicability.Not;
                }
                // We could refine this further, confirming that the length is in the last 256
                // bytes of the file and that it points at a $ff byte, but that seems unnecessary.
                return Applicability.Probably;
            } else if (FileAttrs.FileType == FileAttribs.FILE_TYPE_INT) {
                if (LooksLikeLisa3(DataStream, FileAttrs.AuxType)) {
                    return Applicability.Yes;
                } else if (LooksLikeLisa4(DataStream, FileAttrs.AuxType)) {
                    return Applicability.Yes;
                } else {
                    return Applicability.Not;
                }
            } else {
                return Applicability.Not;
            }
        }

        public static bool LooksLikeLisa3(Stream stream, int auxType) {
            // Check aux type.
            if (auxType < 0x1000 || auxType >= 0x4000) {
                return false;
            }
            if (stream.Length < MIN_LEN_V3 || stream.Length > MAX_LEN) {
                return false;
            }
            stream.Position = 0;
            ushort codeLen = RawData.ReadU16LE(stream, out bool ok);
            ushort symLen = RawData.ReadU16LE(stream, out ok);
            if (!ok) {
                return false;       // unexpected
            }

            // Symbol table is 0-512 entries, 8 bytes each.
            if (symLen > stream.Length || (symLen & 0x03) != 0 ||
                    symLen > Lisa3.MAX_SYMBOLS * Lisa3.SYMBOL_TABLE_ENTRY_LEN) {
                return false;
            }

            // Check length of header + symbols + code.
            if (4 + codeLen + symLen > stream.Length) {
                return false;
            }
            return true;
        }

        public static bool LooksLikeLisa4(Stream stream, int auxType) {
            if (auxType < 0x4000 || auxType >= 0x6000) {
                return false;
            }
            return false;       // TODO
        }

        public override Type GetExpectedType(Dictionary<string, string> options) {
            return typeof(SimpleText);
        }

        public override IConvOutput ConvertFile(Dictionary<string, string> options) {
            if (Applic <= Applicability.Not) {
                Debug.Assert(false);
                return new ErrorText("ERROR");
            }
            // Load the entire file into memory.  DOS BB files are always "raw", so the stream
            // will be a multiple of 256 bytes.
            Debug.Assert(DataStream != null);
            byte[] fileBuf = new byte[DataStream.Length];
            DataStream.Position = 0;
            DataStream.ReadExactly(fileBuf, 0, (int)DataStream.Length);

            SimpleText output = new SimpleText();
            if (FileAttrs.FileType == FILE_TYPE_BB) {
                Lisa2 converter = new Lisa2(fileBuf, output);
                converter.Convert();
            } else if (FileAttrs.AuxType < 0x4000) {
                Lisa3 converter = new Lisa3(fileBuf, output);
                converter.Convert();
            } else {
                Debug.Assert(false);
            }
            return output;
        }
    }

    #region LISA v2

    /// <summary>
    /// LISA v2 converter.
    /// </summary>
    class Lisa2 {
        private static readonly int[] sTabStops = new int[] { 0, 9, 13, 29 };
        private const int COL_LABEL = 0;
        private const int COL_OPCODE = 1;
        private const int COL_OPERAND = 2;
        private const int COL_COMMENT = 3;

        private const byte EOL_MARKER = 0x0d;       // carriage return

        // Opcode mnemonic table, extracted from the assembler binary.
        private const string sOpcodes =
            "BGEBLTBMIBCCBCSBPLBNEBEQ" +    // 80-87
            "BVSBVCBSBBNMBM1BNZBIZBIM" +    // 88-8f
            "BIPBICBNCBRABTRBFLBRKBKS" +    // 90-97
            "CLVCLCCLDCLIDEXDEYINXINY" +    // 98-9f
            "NOPPHAPLAPHPPLPRTSRTIRSB" +    // a0-a7
            "RTNSECSEISEDTAXTAYTSXTXA" +    // a8-af
            "TXSTYAADDCPRDCRINRSUBLDD" +    // b0-b7
            "POPPPDSTDSTPLDRSTOSET___" +    // b8-bf
            "ADCANDORABITCMPCPXCPYDEC" +    // c0-c7
            "EORINCJMPJSR___LDALDXLDY" +    // c8-cf
            "STASTXSTYXORLSRRORROLASL" +    // d0-d7
            "ADREQUORGOBJEPZSTRDCMASC" +    // d8-df
            "ICLENDLSTNLSHEXBYTHBYPAU" +    // e0-e7
            "DFSDCI...PAGINVBLKDBYTTL" +    // e8-ef
            "SBC___LET.IF.EL.FI=  PHS" +    // f0-f7
            "DPH.DAGENNOGUSR_________";     // f8-ff

        private byte[] mBuf;
        private SimpleText mOutput;
        private TabbedLine mLineBuf = new TabbedLine(sTabStops);


        public Lisa2(byte[] buf, SimpleText output) {
            mBuf = buf;
            mOutput = output;
        }

        /// <summary>
        /// Performs the conversion.
        /// </summary>
        public void Convert() {
            int offset = 0;
            ushort version = RawData.ReadU16LE(mBuf, ref offset);
            ushort fileLen = RawData.ReadU16LE(mBuf, ref offset);
            Debug.Assert(fileLen <= mBuf.Length);

            while (offset < fileLen) {
                byte lineLen = mBuf[offset++];
                if (lineLen == 0xff) {
                    // End-of-file indicator.
                    if (fileLen - offset > 2) {
                        mOutput.Notes.AddW("File ended early, at +$" + offset.ToString("x4"));
                        break;
                    }
                } else if (lineLen == 0) {
                    // Shouldn't be possible.
                    mOutput.Notes.AddW("Found zero-length line");
                    break;
                }

                ConvertLine(offset, lineLen);
                mLineBuf.MoveLineTo(mOutput);
                offset += lineLen;
            }
        }

        /// <summary>
        /// Converts a single line.
        /// </summary>
        private void ConvertLine(int offset, int lineLen) {
            Debug.Assert(offset + lineLen <= mBuf.Length);
            if (mBuf[offset + lineLen - 1] != EOL_MARKER) {
                mOutput.Notes.AddW("Found line without EOL marker at +$" + offset.ToString("x4"));
                return;
            }
            int startOffset = offset;
            mLineBuf.Clear();

            if (mBuf[offset] == '*' || mBuf[offset] == ';') {
                // Entire line is a comment, just spit the whole thing out.
                while (offset - startOffset < lineLen - 1) {
                    mOutput.AppendPrintable((char)mBuf[offset++]);
                }
                if (mBuf[offset] != 0x0d) {
                    mOutput.Notes.AddW("Didn't find expected EOL token");
                    // keep going
                }
                return;
            }

            // See if the line starts with a label.
            char ch = (char)mBuf[offset];
            if ((ch >= 'A' && ch <= 'Z') || ch == '^') {
                // Line starts with a label.  These seem to be 8 bytes for standard labels, 7
                // bytes for local labels.  We want to pick up the trailing colon, so we can just
                // go until we hit a value >= $80 or == $0d (opcode or EOL).  Don't print spaces.
                while (ch < 0x80 && ch != EOL_MARKER) {
                    if (ch != ' ') {
                        mLineBuf.AppendPrintable(ch);
                    }
                    ch = (char)mBuf[++offset];
                }
            } else if (ch < 0x80) {
                // Unexpected.
                mOutput.Notes.AddW("Line started with unexpected character: $" +
                    mBuf[offset].ToString("x2"));
            } else {
                // First byte is the opcode mnemonic.
            }
            if (ch == EOL_MARKER) {
                // Label was on a line by itself.
                return;
            }

            mLineBuf.Tab(COL_OPCODE);

            // Get mnemonic index.
            byte mindex = mBuf[offset++];
            if (mindex < 0x80) {
                mOutput.Notes.AddW("Found bad mnemonic index at offset +$" +
                    offset.ToString("x4") + ": $" + mindex.ToString());
            } else {
                string mnemonic = sOpcodes.Substring((mindex - 0x80) * 3, 3);
                mLineBuf.Append(mnemonic);
                // The LISA 2.5 WRITE command adds a space after every mnemonic.  If we want
                // the output to diff cleanly, we need to do so as well.
                mLineBuf.Append(' ');
            }

            byte addrMode = mBuf[offset++];
            if (addrMode == 0x00) {
                // No operand (e.g. "NOP").  Continue on to see if a comment exists.
            } else if (addrMode <= 0x20) {
                // Operand here, copy until we hit EOL or high-ASCII semicolon.
                //mLineBuf.Tab(COL_OPERAND);    // should already be in right place
                ch = (char)mBuf[offset];
                while (ch != EOL_MARKER && ch < 0x80) {
                    mLineBuf.AppendPrintable(ch);
                    ch = (char)mBuf[++offset];
                }
            }

            byte commentStart = mBuf[offset++];
            if (commentStart == EOL_MARKER) {
                // No comment here.
                return;
            } else if (commentStart == (';' | 0x80)) {
                // Handle comment, copying characters to the end of the line.
                mLineBuf.Tab(COL_COMMENT);
                mLineBuf.Append(';');
                while (offset - startOffset < lineLen - 1) {
                    mLineBuf.AppendPrintable((char)mBuf[offset++]);
                }
            } else {
                mOutput.Notes.AddW("Unexpected comment marker at +$" + offset.ToString("x4"));
            }
        }
    }

    #endregion LISA v2

    #region LISA v3

    /// <summary>
    /// LISA v3 converter.
    /// </summary>
    /// <remarks>
    /// This is a direct port from the original CiderPress implementation, which was a conversion
    /// of the assembly routines in the LISA v3.1 source file "LISA3.9".
    /// </remarks>
    class Lisa3 {
        public const int MAX_SYMBOLS = 512;
        public const int SYMBOL_TABLE_ENTRY_LEN = 8;

        private static readonly int[] sTabStops = new int[] { 0, 9, 18, 40 };
        private const int COL_LABEL = 0;
        private const int COL_OPCODE = 1;
        private const int COL_OPERAND = 2;
        private const int COL_COMMENT = 3;

        //
        // Token constants, from LISA.MNEMONICS.
        //

        private const char MACROCHR = '/';

        private const byte LCLTKN = 0xf0;
        private const byte LBLTKN = 0xfa;
        // 0xfb used to indicate "second half" of symbol table
        private const byte MACTKN = 0xfc;
        // 0xfd used to indicate "second half" of symbol table
        private const byte CMNTTKN = 0xfe;
        private const byte SN = 0x00;
        private const byte SS = 0xb0;

        private const byte GROUP1 = 0x00;
        private const byte GROUP2 = 0x0b;
        private const byte GROUP3 = 0x11;
        private const byte GROUP4 = 0x24;
        private const byte GROUP5 = 0x29;
        private const byte GROUP6 = 0x34;
        private const byte GROUP7 = 0x39;
        private const byte GROUP8 = 0x3f;
        private const byte GROUP9 = 0x4a;

        // Opcode mnemonics.  String should be exactly 256 * 3 characters.
        private const string sMnemonics =
            // 0x00 (SN, M65.2) - Group 1 instructions
            "addadcandcmpeorldaorasbc" +
            "stasubxor" +
                     // 0x0b - Group 2 instructions
                     "asldecinclsrrol" +
            "ror" +
               // 0x11 - Group 3 instructions
               ".ifwhlbrabccbcsbeqbfl" +
            "bgebltbmibnebplbtrbvcbvs" +
            "jsrobjorgphs" +
                        // 0x24 - Group 4 instructions
                        ".mdfzrinplcl" +
            "rls" +
               // 0x29 - Group 5 instructions
               "bitcpxcpyjmpldxldystx" +
            "stytrbtsbstz" +
                        // 0x34 - Group 6 instructions
                        "=  conepzequ" +
            "set" +
               // 0x39 - Group 7 instructions
               ".daadrbytcspdbyhby" +
                                 // 0x3f - Group 8 instructions
                                 "anx" +
            "sbtttlchnblkdciinvrvsmsg" +
            "strzro" +
                  // 0x4a - Group 9 instructions
                  "dfshexusrsav" +
                              //M65LEN2  equ      * - M65.2
                              "??????" +    // 0x4e-0x4f
            "????????????????????????" +    // 0x50-0x57
            "????????????????????????" +    // 0x58-0x5f
            "????????????????????????" +    // 0x60-0x67
            "????????????????????????" +    // 0x68-0x6f
            "????????????????????????" +    // 0x70-0x77
            "????????????????????????" +    // 0x78-0x7f
            "????????????????????????" +    // 0x80-0x87
            "????????????????????????" +    // 0x88-0x8f
            "????????????????????????" +    // 0x90-0x97
            "????????????????????????" +    // 0x98-0x9f
            "????????????????????????" +    // 0xa0-0xa7
            "????????????????????????" +    // 0xa8-0xaf

            // 0xb0 (SS M65.1) - assembler directives
            ".el.fi.me.wedphif1if2end" +
            "expgenlstnlsnognoxpagpau" +
            "nlccnd   " +
                     // 0xc2 - Single-byte instructions
                     "asllsrrolrordec" +
            "incbrkclccldcliclvdexdey" +
            "inxinynopphaphpplaplprti" +
            "rtssecsedseitaxtaytsxtxa" +
            "txstyaphxphyplxply" +
                              //M65LEN1  equ      * - M65.1

                              "??????" +    // 0xe6-0xe7
            "????????????????????????" +    // 0xe8-0xef
            "????????????????????????" +    // 0xf0-0xff
            "????????????????????????";    // 0xf8-0xff

        private int mSymOffset;
        private int mSymCount;
        private string[] mSymbols = new string[0];
        private char[] mBinBuf = new char[8];

        private byte[] mBuf;
        private SimpleText mOutput;
        private TabbedLine mLineBuf = new TabbedLine(sTabStops);


        public Lisa3(byte[] buf, SimpleText output) {
            Debug.Assert(sMnemonics.Length == 256 * 3);
            mBuf = buf;
            mOutput = output;
        }

        public void Convert() {
            // Read header.
            const int headerLen = 4;
            ushort codeLen = RawData.GetU16LE(mBuf, 0);
            ushort symLen = RawData.GetU16LE(mBuf, 2);

            // Do some quick sanity checks.
            Debug.Assert((symLen & 0x03) == 0 && symLen <= MAX_SYMBOLS * SYMBOL_TABLE_ENTRY_LEN);
            int expectedLen = headerLen + symLen + codeLen;
            if (expectedLen > mBuf.Length) {
                mOutput.Notes.AddE("File is too short");
                Debug.Assert(false);        // shouldn't be here
                return;
            } else if (expectedLen < mBuf.Length) {
                mOutput.Notes.AddI("File is longer than expected by " +
                    (mBuf.Length - expectedLen) + " bytes");
            }

            mSymOffset = headerLen;
            mSymCount = symLen / 8;
            mSymbols = UnpackSymbolTable(mBuf, mSymOffset, mSymCount);

            int offset = headerLen + symLen;
            int endOffset = offset + codeLen;
            int lineNum = 0;

            while (offset < endOffset) {
                lineNum++;
                //Debug.WriteLine("START offset=$" + offset.ToString("x4") + " line=" + lineNum);
                byte flagByte = mBuf[offset++];
                int lineLen;
                if (flagByte < 0x80) {
                    // BIGONE - explicit length, complex line
                    lineLen = flagByte;
                    if (flagByte == 0) {
                        // End-of-file indication.
                        break;
                    } else {
                        // Subtract one, because length includes flagByte.
                        ProcessLine(offset, flagByte - 1);
                    }
                } else {
                    if (flagByte >= LCLTKN) {
                        // SPCLCASE - locals, labels, comments
                        lineLen = 1;
                        if (flagByte == CMNTTKN + 1) {
                            mLineBuf.Append(';');
                        } else if (flagByte == CMNTTKN) {
                            mLineBuf.Append('*');
                        } else if (flagByte < LBLTKN) {
                            // CNVRTLCL: 0xf0 - 0xf9 - local numeric labels
                            mLineBuf.Append('^');
                            mLineBuf.Append((char)('0' + flagByte - 0xf0));
                            mLineBuf.Append(':');
                        } else if (flagByte < MACTKN) {
                            // Normal label; 0xfb means add 256.
                            int idx = mBuf[offset] | ((flagByte & 0x01) << 8);
                            PrintSymLabel(idx);
                            mLineBuf.Append(':');
                            lineLen = 2;
                        } else {
                            // Macro (only object on line).
                            Debug.Assert(flagByte == MACTKN || flagByte == MACTKN + 1);
                            mLineBuf.Tab(COL_OPCODE);
                            int idx = mBuf[offset] | ((flagByte & 0x01) << 8);
                            mLineBuf.Append(MACROCHR);
                            PrintSymLabel(idx);
                            lineLen = 2;
                        }
                    } else {
                        // SHRTMNM2 - simple, standard mnemonic.
                        lineLen = 1;
                        mLineBuf.Tab(COL_OPCODE);
                        PrintMnemonic(flagByte);
                    }
                }

                mLineBuf.MoveLineTo(mOutput);

                // Advance the offset.  We already moved past the initial flag byte.
                Debug.Assert(lineLen > 0);
                offset += lineLen - 1;
            }
        }

        /// <summary>
        /// Processes a "big" line.
        /// </summary>
        private void ProcessLine(int offset, int lineLen) {
            // BIGONE
            byte mnemonic;
            byte firstByte = mBuf[offset];

            if (firstByte == CMNTTKN + 1 || firstByte == CMNTTKN) {
                // Full-line comment.
                if (firstByte == CMNTTKN + 1) {
                    mLineBuf.Append(';');
                } else {
                    mLineBuf.Append('*');
                }
                // CNVCMNT
                while (--lineLen != 0) {
                    mLineBuf.AppendPrintable((char)(mBuf[++offset] & 0x7f));
                }
                return;
            } else if (firstByte == MACTKN || firstByte == MACTKN + 1) {
                // CHKMACRO - handle macro.
                mnemonic = firstByte;
                int idx = (firstByte & 0x01) << 8;
                idx |= mBuf[++offset];
                mLineBuf.Tab(COL_OPCODE);
                mLineBuf.Append(MACROCHR);
                PrintSymLabel(idx);
                offset++;
                lineLen -= 2;
                goto CNVOPRND;
            } else if (firstByte == LBLTKN || firstByte == LBLTKN + 1) {
                // CHKCLBL - handle label at start of line.
                int idx = (firstByte & 0x01) << 8;
                idx |= mBuf[++offset];
                PrintSymLabel(idx);
                offset++;
                lineLen -= 2;
                // goto CNVTMNEM
            } else if (firstByte >= LCLTKN) {
                // CHKLLBL - handle local label (^).
                mLineBuf.Append('^');
                mLineBuf.Append((char)(firstByte - 0xc0));
                offset++;
                lineLen--;
                // goto CNVTMNEM
            } else {
                // No label; current value is the mnemonic.  Continue w/o advancing offset.
                // goto CNVTMNEM
            }

            // CNVTMNEM
            mnemonic = mBuf[offset++];
            lineLen--;
            if (mnemonic >= MACTKN) {
                // CNVRTMAC
                Debug.Assert(mnemonic == MACTKN || mnemonic == MACTKN + 1);
                mLineBuf.Tab(COL_OPCODE);
                int idx = (mnemonic & 0x01) << 8;
                idx |= mBuf[offset++];
                mLineBuf.Append(MACROCHR);
                PrintSymLabel(idx);
                lineLen--;
            } else {
                mLineBuf.Tab(COL_OPCODE);
                PrintMnemonic(mnemonic);
            }

        CNVOPRND:
            ConvertOperand(mnemonic, ref offset, ref lineLen);
        }

        private const string OPRTRST1 = "+-*/&|^=<>%<><";
        private const string OPRTRST2 = "\0\0\0\0\0\0\0\0\0\0\0==>";
        private enum OperandResult {
            Unknown = 0, Failed, GotoOUTOPRTR, GotoOUTOPRND
        }

        /// <summary>
        /// Converts the operand to text.
        /// </summary>
        private void ConvertOperand(byte mnemonic, ref int offset, ref int len) {
            // CNVOPRND
            if (mnemonic >= CMNTTKN) {
                // OUTCMNT2
                PrintComment(0, offset, len);
                return;
            }

            int adrsMode = 0;
            if (mnemonic < SS) {
                if (mnemonic < GROUP3 || (mnemonic >= GROUP5 && mnemonic < GROUP6)) {
                    // Address mode is explicit.
                    adrsMode = mBuf[offset++];
                    len--;
                }
            }
            mLineBuf.Tab(COL_OPERAND);
            if (adrsMode >= 0x10 && adrsMode < 0x80) {
                mLineBuf.Append('(');
            }

            // OUTOPRND
            while (len > 0) {
                byte val = mBuf[offset++];
                len--;

                if (val == 0x0e) {
                    mLineBuf.Append('~');
                    continue;       // goto OUTOPRND
                } else if (val == 0x0f) {
                    mLineBuf.Append('-');
                    continue;       // goto OUTOPRND
                } else if (val == 0x3a) {
                    mLineBuf.Append('#');
                    continue;       // goto OUTOPRND
                } else if (val == 0x3b) {
                    mLineBuf.Append('/');
                    continue;       // goto OUTOPRND
                } else if (val == 0x3d) {
                    mLineBuf.Append('@');
                    continue;       // goto OUTOPRND
                } else {
                    OperandResult result = PrintNum(adrsMode, val, ref offset, ref len);
                    if (result == OperandResult.Failed) {
                        return;
                    } else if (result == OperandResult.GotoOUTOPRND) {
                        continue;   // goto OUTOPRND
                    } else {
                        // goto OUTOPRTR
                    }
                }

            OUTOPRTR:
                if (len == 0) {
                    break;
                }
                byte opr = mBuf[offset++];
                len--;

                if (opr < 0x0e) {
                    mLineBuf.Append(' ');
                    mLineBuf.Append(OPRTRST1[opr]);
                    if (OPRTRST2[opr] != '\0') {
                        mLineBuf.Append(OPRTRST2[opr]);
                    }
                    mLineBuf.Append(' ');
                    // goto OUTOPRND
                } else if (opr < 0x20 || opr >= 0x30) {
                    // NOOPRTR
                    if (opr == CMNTTKN + 1) {
                        PrintComment(adrsMode, offset, len);
                        offset += len;
                        len = 0;
                        return;
                    }
                    mLineBuf.Append(',');
                    offset--;       // back up
                    len++;
                    // goto OUTOPRND
                } else {
                    mLineBuf.Append('+');
                    OperandResult result =
                        PrintNum(adrsMode, (byte)(opr - 0x10), ref offset, ref len);
                    if (result == OperandResult.Failed) {
                        return;
                    } else if (result == OperandResult.GotoOUTOPRND) {
                        continue;       // goto OUTOPRND
                    } else {
                        goto OUTOPRTR;
                    }
                }
            }
            PrintComment(adrsMode, offset, len);
        }

        /// <summary>
        /// Prints a numeric or symbolic operand value.  The return value tells the caller
        /// whether it should continue processing operand pieces or operators.
        /// </summary>
        private OperandResult PrintNum(int adrsMode, byte val, ref int offset, ref int len) {
            // OUTNUM - these all jump to OUTOPRTR unless otherwise specified
            OperandResult result = OperandResult.GotoOUTOPRTR;

            if (val < 0x1a) {
                mLineBuf.Append((char)('0' | val));
            } else if (val == 0x1a) {
                // 1-byte decimal.
                mLineBuf.Append(mBuf[offset++].ToString("D"));
                len--;
            } else if (val == 0x1b) {
                // 2-byte decimal.
                int val2 = mBuf[offset++];
                val2 |= mBuf[offset++] << 8;
                mLineBuf.Append(val2.ToString("D"));
                len -= 2;
            } else if (val == 0x1c) {
                // 1-byte hex.
                mLineBuf.Append('$');
                mLineBuf.Append(mBuf[offset++].ToString("X2"));
                len--;
            } else if (val == 0x1d) {
                // 2-byte hex.
                mLineBuf.Append('$');
                int val2 = mBuf[offset++];
                val2 |= mBuf[offset++] << 8;
                mLineBuf.Append(val2.ToString("X4"));
                len -= 2;
            } else if (val == 0x1e) {
                // 1-byte binary.
                mLineBuf.Append('%');
                mLineBuf.Append(ToBinary(mBuf[offset++]));
                len--;
            } else if (val == 0x1f) {
                // 2-byte binary.
                mLineBuf.Append('%');
                mLineBuf.Append(ToBinary(mBuf[offset++]));
                mLineBuf.Append(ToBinary(mBuf[offset++]));
                len -= 2;
            } else if (val >= 0x36 && val <= 0x39) {
                // OUTIMD
                if (val == 0x36 || val == 0x37) {
                    mLineBuf.Append('#');
                } else {
                    mLineBuf.Append('/');
                }
                int idx = (val & 0x01) << 8;
                idx |= mBuf[offset++];
                PrintSymLabel(idx);
                len--;
            } else if (val == 0x3c) {
                mLineBuf.Append('*');   // loc cntr token
            } else if (val < 0x4a) {
                // <0..<9 tokens
                mLineBuf.Append('<');
                mLineBuf.Append((char)(val - 0x10));
            } else if (val < 0x50) {
                // ?0..?5 tokens (+0x66)
                mLineBuf.Append('?');
                mLineBuf.Append((char)(val - 0x1a));
            } else if (val < 0x5a) {
                mLineBuf.Append('>');
                mLineBuf.Append((char)(val - 0x20));
            } else if (val < 0x60) {
                // ?6..?9 tokens (+0x5c)
                int newVal = val - 0x24;
                mLineBuf.Append('?');
                if (newVal == ';') {
                    mLineBuf.Append('#');
                } else {
                    mLineBuf.Append((char)newVal);
                }
                if (newVal == ':') {
                    result = OperandResult.GotoOUTOPRND;
                }
            } else if (val < 0x80) {
                // String tokens.
                int strLen = val & 0x1f;
                if (strLen == 0) {
                    // Explicit length byte.
                    strLen = mBuf[offset++];
                    len--;
                }
                if (strLen > len) {
                    mLineBuf.Append("!BAD STR!");
                    mOutput.Notes.AddW("Found bad string len at +$" + offset);
                    result = OperandResult.Failed;
                }
                char delim = (mBuf[offset] >= 0x80) ? '"' : '\'';
                mLineBuf.Append(delim);
                while (strLen-- != 0) {
                    char ch = (char)(mBuf[offset] & 0x7f);
                    if (ch == delim) {
                        mLineBuf.Append(delim);     // quote delimiters by doubling them up
                    }
                    mLineBuf.Append(ch);
                    offset++;
                    len--;
                }
                mLineBuf.Append(delim);
            } else if (val == LBLTKN || val == LBLTKN + 1) {
                // Label.
                int idx = (val & 0x01) << 8;
                idx |= mBuf[offset++];
                PrintSymLabel(idx);
                len--;
            } else if (val == CMNTTKN + 1) {
                // OUTCMNT2
                PrintComment(adrsMode, offset, len);
                offset += len;
                len = 0;
            } else {
                // Just go to OUTOPRTR.
                Debug.Assert(result == OperandResult.GotoOUTOPRTR);
            }

            return result;
        }

        private void PrintMnemonic(byte val) {
            string mnem = sMnemonics.Substring(val * 3, 3);
            mLineBuf.Append(mnem);
        }

        private void PrintSymLabel(int index) {
            if (index < 0 || index >= mSymbols.Length) {
                mLineBuf.Append("!BADSYM!");
                mOutput.Notes.AddW("Bad symbol index " + index);
                return;
            }
            mLineBuf.Append(mSymbols[index]);
        }

        /// <summary>
        /// Prints a line-end comment.  Also finishes off the operand, if necessary.
        /// </summary>
        private void PrintComment(int adrsMode, int offset, int len) {
            // OUTCMNT2
            Debug.Assert(len >= 0);
            if (adrsMode == 0x04) {
                mLineBuf.Append(",X");
            } else if (adrsMode == 0x08) {
                mLineBuf.Append(",Y");
            } else if (adrsMode == 0x10) {
                mLineBuf.Append(')');
            } else if (adrsMode == 0x20) {
                mLineBuf.Append(",X)");
            } else if (adrsMode == 0x40) {
                mLineBuf.Append("),Y");
            }

            if (len > 0) {
                mLineBuf.Tab(COL_COMMENT);
                mLineBuf.Append(';');
                while (len-- != 0) {
                    mLineBuf.AppendPrintable((char)(mBuf[offset++] & 0x7f));
                }
            }
        }

        /// <summary>
        /// Converts a byte to a binary string.
        /// </summary>
        private string ToBinary(byte val) {
            for (int bit = 0; bit < 8; bit++) {
                mBinBuf[bit] = (char)('0' + ((val >> (7 - bit)) & 0x01));
            }
            return new string(mBinBuf);
        }

        /// <summary>
        /// Unpacks the symbol table into an array of strings.
        /// </summary>
        private static string[] UnpackSymbolTable(byte[] buf, int symOffset, int symCount) {
            int[] unpackBuf = new int[8];
            StringBuilder sb = new StringBuilder(8);
            string[] symbols = new string[symCount];
            int pkOff = symOffset;
            for (int i = 0; i < symCount; i++) {
                unpackBuf[0] = buf[pkOff + 0] >> 2;
                unpackBuf[1] = ((buf[pkOff + 0] << 4) & 0x3c) | (buf[pkOff + 1]) >> 4;
                unpackBuf[2] = ((buf[pkOff + 1] << 2) & 0x3c) | (buf[pkOff + 2]) >> 6;
                unpackBuf[3] = buf[pkOff + 2] & 0x3f;

                unpackBuf[4] = buf[pkOff + 3] >> 2;
                unpackBuf[5] = ((buf[pkOff + 3] << 4) & 0x3c) | (buf[pkOff + 4]) >> 4;
                unpackBuf[6] = ((buf[pkOff + 4] << 2) & 0x3c) | (buf[pkOff + 5]) >> 6;
                unpackBuf[7] = buf[pkOff + 5] & 0x3f;

                sb.Clear();
                for (int ci = 0; ci < 8; ci++) {
                    if (unpackBuf[ci] == ' ') {
                        break;
                    } else if (unpackBuf[ci] > ' ') {
                        sb.Append((char)unpackBuf[ci]);
                    } else {
                        sb.Append((char)(unpackBuf[ci] | 0x40));
                    }
                }

                ushort val = RawData.GetU16LE(buf, pkOff + 6);

                symbols[i] = sb.ToString();
                //Debug.WriteLine("SYM '" + symbols[i] + "': $" + val.ToString("x4"));
                pkOff += SYMBOL_TABLE_ENTRY_LEN;
            }
            return symbols;
        }
    }

    #endregion LISA v3
}