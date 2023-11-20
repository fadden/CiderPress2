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

        private const int MIN_LEN_V2 = 4+3;         // header + file end marker
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
                if (FileAttrs.AuxType >= 0x1000 && FileAttrs.AuxType < 0x3fff) {
                    // Could be v3.
                    return Applicability.Not;       // TODO
                } else if (FileAttrs.AuxType >= 0x4000 && FileAttrs.AuxType <= 0x7fff) {
                    // Could be v4/v5.
                    return Applicability.Not;       // TODO
                } else {
                    return Applicability.Not;
                }
            } else {
                return Applicability.Not;
            }
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
            }
            return output;
        }
    }

    class Lisa2 {
        private static readonly int[] sTabStops = new int[] { 0, 9, 13, 29 };
        private const int COL_LABEL = 0;
        private const int COL_OPCODE = 1;
        private const int COL_OPERAND = 2;
        private const int COL_COMMENT = 3;

        private const byte EOL_MARKER = 0x0d;       // carriage return

        // Opcode table, extracted from the assembler binary.
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
            "DPH.DAGENNOGUSR_________" ;    // f8-ff

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
}
