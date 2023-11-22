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

using Asm65;
using CommonUtil;
using DiskArc;

namespace FileConv.Code {
    /// <summary>
    /// Converts 6502, 65C02, and 65816 code to a disassembly listing.
    /// </summary>
    public class Disasm65 : Converter {
        public const string TAG = "disasm";
        public const string LABEL = "65xx disassembler";
        public const string DESCRIPTION =
            "Disassembles 6502, 65C02, and 65816 code to a text listing.  The output " +
            "resembles Apple II monitor output.";
        public const string DISCRIMINATOR = "DOS B, and various ProDOS types.";
        public override string Tag => TAG;
        public override string Label => LABEL;
        public override string Description => DESCRIPTION;
        public override string Discriminator => DISCRIMINATOR;

        public const string OPT_FOLLOW = "follow";
        public const string OPT_START_LONG = "long";
        public const string OPT_TWO_BRK = "twobrk";

        public const string OPT_CPU = "cpu";
        public const string CONV_CPU_6502 = "6502";
        public const string CONV_CPU_6502_UNDOC = "6502u";
        public const string CONV_CPU_65C02 = "65c02";
        public const string CONV_CPU_65816 = "65816";

        public static readonly string[] ConvCpuTags = new string[] {
            CONV_CPU_6502, CONV_CPU_6502_UNDOC, CONV_CPU_65C02, CONV_CPU_65816
        };
        public static readonly string[] ConvModeDescrs = new string[] {
            "6502", "6502 + Undocumented", "65C02", "65816"
        };

        public override List<OptionDefinition> OptionDefs { get; protected set; } =
            new List<OptionDefinition>() {
                new OptionDefinition(OPT_CPU, "CPU Mode",
                    OptionDefinition.OptType.Multi, CONV_CPU_65C02,
                    ConvCpuTags, ConvModeDescrs),
                new OptionDefinition(OPT_FOLLOW, "Follow SEP/REP/XCE",
                    OptionDefinition.OptType.Boolean, "true"),
                new OptionDefinition(OPT_START_LONG, "Start with long registers",
                    OptionDefinition.OptType.Boolean, "true"),
                new OptionDefinition(OPT_TWO_BRK, "BRK is two bytes",
                    OptionDefinition.OptType.Boolean, "false"),

            };

        private const int MAX_LEN = 1 * 1024 * 1024;        // arbitrary 1MB limit


        private Disasm65() { }

        public Disasm65(FileAttribs attrs, Stream? dataStream, Stream? rsrcStream,
                ResourceMgr? resMgr, ConvFlags convFlags, AppHook appHook)
                : base(attrs, dataStream, rsrcStream, resMgr, convFlags, appHook) {
            Applic = TestApplicability();
        }

        protected override Applicability TestApplicability() {
            if (DataStream == null) {
                return Applicability.Not;
            }
            if (DataStream.Length == 0 || DataStream.Length > MAX_LEN) {
                return Applicability.Not;
            }
            return EvaluateApplicability(out CpuDef.CpuType unused1, out int unused2);
        }

        public override Type GetExpectedType(Dictionary<string, string> options) {
            return typeof(SimpleText);
        }

        public override IConvOutput ConvertFile(Dictionary<string, string> options) {
            if (Applic <= Applicability.Not) {
                Debug.Assert(false);
                return new ErrorText("ERROR");
            }
            // Load the entire file into memory.  Even for a large IIgs program this shouldn't
            // be a strain.
            Debug.Assert(DataStream != null);
            byte[] fileBuf = new byte[DataStream.Length];
            DataStream.Position = 0;
            DataStream.ReadExactly(fileBuf, 0, (int)DataStream.Length);

            // Get preferred CPU type from preferences.
            CpuDef.CpuType cpuType =
                OptToEnum(GetStringOption(options, OPT_CPU, CONV_CPU_65C02), out bool doInclUndoc);
            // Get recommended CPU type based on filetype.
            EvaluateApplicability(out CpuDef.CpuType bestCpu, out int startAddr);
            // If filetype indicates 65816, but prefence is 8-bit, override preference.
            if (cpuType == CpuDef.CpuType.Cpu65816 && bestCpu != CpuDef.CpuType.Cpu65816) {
                cpuType = bestCpu;
            }

            bool doFollow = GetBoolOption(options, OPT_FOLLOW, true);
            bool doStartLong = GetBoolOption(options, OPT_START_LONG, true);
            bool doTwoBrk = GetBoolOption(options, OPT_TWO_BRK, false);

            Convert65 conv;
            if (cpuType == CpuDef.CpuType.Cpu65816) {
                conv = new Convert65(fileBuf, startAddr, cpuType,
                    doFollow: doFollow, doStartLong: doStartLong, doTwoBrk: doTwoBrk);
            } else {
                conv = new Convert65(fileBuf, startAddr, cpuType,
                    doInclUndoc: doInclUndoc, doTwoBrk: doTwoBrk);
            }
            return conv.Convert();
        }

        /// <summary>
        /// Evaluates the applicability of the file.
        /// </summary>
        /// <param name="cpuType">Result: recommended CPU type (65C02 for 8-bit,
        ///   65816 for 16-bit).</param>
        /// <param name="startAddr">Result: recommended start address.</param>
        /// <returns>Applicability level.</returns>
        private Applicability EvaluateApplicability(out CpuDef.CpuType cpuType, out int startAddr) {
            cpuType = CpuDef.CpuType.Cpu65C02;      // assume 8-bit

            Applicability applic;
            byte fileType = FileAttrs.FileType;
            if (fileType == FileAttribs.FILE_TYPE_BIN) {
                // Could be code or data.  Use a medium level of confidence so that, if it looks
                // like it might be a hi-res screen, that takes precedence.
                startAddr = FileAttrs.AuxType;
                applic = Applicability.Maybe;
            } else if (fileType == FileAttribs.FILE_TYPE_SYS ||
                       fileType == FileAttribs.FILE_TYPE_CMD ||
                       fileType == FileAttribs.FILE_TYPE_8OB ||
                       fileType == FileAttribs.FILE_TYPE_P8C) {
                // These are explicitly 8-bit code.
                applic = Applicability.Yes;
                startAddr = 0x2000;
            } else if (fileType >= 0xb1 && fileType <= 0xbd) {
                // GS/OS ref table F-1 identifies these types:
                // [B1,BD] = OBJ, LIB, S16, RTL, EXE, PIF, TIF, NDA, CDA, TOL, DVR, LDF, FST
                // These are OMF files, so we want the OMF formatter to take precedence.
                applic = Applicability.Probably;
                cpuType = CpuDef.CpuType.Cpu65816;
                startAddr = 0;
            } else if (fileType == FileAttribs.FILE_TYPE_OS) {
                // GS/OS startup code is not in OMF format.
                applic = Applicability.Yes;
                cpuType = CpuDef.CpuType.Cpu65816;
                startAddr = 0;
            } else if (fileType >= 0xf1 && fileType <= 0xf8) {
                // The user types are pretty much always used for data.
                applic = Applicability.ProbablyNot;
                startAddr = 0x1000;
            } else if (fileType == FileAttribs.FILE_TYPE_NON) {
                // Usually data, sometimes code.
                applic = Applicability.ProbablyNot;
                startAddr = 0x1000;
            } else {
                applic = Applicability.Not;
                cpuType = CpuDef.CpuType.CpuUnknown;
                startAddr = 0;
            }

            return applic;
        }

        /// <summary>
        /// Converts a "cpu=xyz" option value to an enumerated value.
        /// </summary>
        private static CpuDef.CpuType OptToEnum(string opt, out bool inclUndoc) {
            inclUndoc = false;
            switch (opt) {
                case CONV_CPU_6502:
                    return CpuDef.CpuType.Cpu6502;
                case CONV_CPU_6502_UNDOC:
                    inclUndoc = true;
                    return CpuDef.CpuType.Cpu6502;
                case CONV_CPU_65C02:
                    return CpuDef.CpuType.Cpu65C02;
                case CONV_CPU_65816:
                    return CpuDef.CpuType.Cpu65816;
                default:
                    return CpuDef.CpuType.Cpu65C02;
            }
        }
    }

    class Convert65 {
        private CpuDef mCpuDef;
        private byte[] mBuf;
        private int mAddr;
        private bool mEmulation;
        private bool mLongA;
        private bool mLongXY;
        private StatusFlags mStatusFlags = StatusFlags.DefaultValue;
        private StringBuilder mLineBuf = new StringBuilder(80);

        /// <summary>
        /// Constructor for 8-bit code.
        /// </summary>
        public Convert65(byte[] buf, int startAddr, CpuDef.CpuType cpuType,
                bool doInclUndoc, bool doTwoBrk) {
            mCpuDef = CpuDef.GetBestMatch(cpuType, doInclUndoc, doTwoBrk);
            mBuf = buf;
            mAddr = startAddr;

            mEmulation = mLongA = mLongXY = false;
        }

        /// <summary>
        /// Constructor for 16-bit code.
        /// </summary>
        public Convert65(byte[] buf, int startAddr, CpuDef.CpuType cpuType,
                bool doFollow, bool doStartLong, bool doTwoBrk) {
            mCpuDef = CpuDef.GetBestMatch(cpuType, false, doTwoBrk);
            mBuf = buf;
            mAddr = startAddr;
            mEmulation = true;
            mLongA = doStartLong;
            mLongXY = doStartLong;
        }

        public IConvOutput Convert() {
            SimpleText output = new SimpleText();
            OutputMonitor8(0, mBuf.Length, output);
            return output;
        }

        public void OutputMonitor8(int offset, int length, SimpleText output) {
            int endOffset = offset + length;        // offset of first byte past end
            while (offset < endOffset) {
                OpDef op = mCpuDef.GetOpDef(mBuf[offset]);
                int opLen = op.GetLength(mStatusFlags);
                Debug.Assert(opLen > 0);
                if (offset + opLen > endOffset) {
                    // Not enough bytes left.
                    byte[] tmpBuf = new byte[opLen];
                    for (int i = 0; i < opLen; i++) {
                        if (offset + i < endOffset) {
                            tmpBuf[i] = mBuf[offset + i];
                        } else {
                            tmpBuf[i] = 0;
                        }
                    }
                    PrintMonitor8Line(op, mStatusFlags, tmpBuf, 0, mAddr + offset, string.Empty,
                        mLineBuf);
                } else if (IsProDOS8Call(mBuf, offset, endOffset - offset)) {
                    // Print and skip P8 inline call stuff.
                    string callName = NiftyList.LookupP8MLI(mBuf[offset + 3]);
                    if (string.IsNullOrEmpty(callName)) {
                        callName = "(Unknown P8 MLI)";
                    }
                    PrintMonitor8Line(op, mStatusFlags, mBuf, offset, mAddr + offset, callName,
                        mLineBuf);
                    output.AppendLine(mLineBuf);
                    mLineBuf.Clear();
                    byte callNum = mBuf[offset + 3];
                    byte addrLo = mBuf[offset + 4];
                    byte addrHi = mBuf[offset + 5];
                    output.AppendLine(string.Format("{0:X4}-   {1:X2}                ${1:X2}",
                        mAddr + offset + 3, callNum));
                    mLineBuf.AppendFormat("{0:X4}-   {1:X2} {2:X2}             ${2:X2}{1:X2}",
                        mAddr + offset + 4, addrLo, addrHi);
                    opLen += 3;
                } else {
                    PrintMonitor8Line(op, mStatusFlags, mBuf, offset, mAddr + offset, string.Empty,
                        mLineBuf);
                }
                output.AppendLine(mLineBuf);
                mLineBuf.Clear();

                offset += opLen;
            }
        }

        /// <summary>
        /// Prints one line of 8-bit code, in //e monitor style.
        /// </summary>
        /// <param name="op">Opcode reference.</param>
        /// <param name="flags">Current status flags.</param>
        /// <param name="buf">Data buffer.</param>
        /// <param name="offset">Offset of opcode in data buffer.</param>
        /// <param name="addr">Address of opcode in memory.</param>
        /// <param name="comment">Additional comment; may be empty.</param>
        /// <param name="sb">StringBuilder that receives output.</param>
        public static void PrintMonitor8Line(OpDef op, StatusFlags flags, byte[] buf, int offset,
                int addr, string comment, StringBuilder sb) {
            byte byte0, byte1, byte2;
            int opLen = op.GetLength(flags);
            switch (opLen) {
                case 1:
                    byte0 = buf[offset];
                    byte1 = byte2 = 0xcc;
                    sb.AppendFormat("{0:X4}-   {1:X2}          {2,-3}",
                        addr, byte0, op.Mnemonic.ToUpper());
                    break;
                case 2:
                    byte0 = buf[offset];
                    byte1 = buf[offset + 1];
                    byte2 = 0xcc;
                    sb.AppendFormat("{0:X4}-   {1:X2} {2:X2}       {3,-3}",
                        addr, byte0, byte1, op.Mnemonic.ToUpper());
                    break;
                case 3:
                    byte0 = buf[offset];
                    byte1 = buf[offset + 1];
                    byte2 = buf[offset + 2];
                    sb.AppendFormat("{0:X4}-   {1:X2} {2:X2} {3:X2}    {4,-3}",
                        addr, byte0, byte1, byte2, op.Mnemonic.ToUpper());
                    break;
                default:
                    Debug.Assert(false);
                    return;
            }

            switch (op.AddrMode) {
                case OpDef.AddressMode.Implied:
                case OpDef.AddressMode.StackPull:
                case OpDef.AddressMode.StackPush:
                case OpDef.AddressMode.StackRTI:
                case OpDef.AddressMode.StackRTS:
                case OpDef.AddressMode.Acc:
                    break;
                case OpDef.AddressMode.DP:
                    sb.AppendFormat("   ${0:X2}", byte1);
                    break;
                case OpDef.AddressMode.DPIndexX:
                    sb.AppendFormat("   ${0:X2},X", byte1);
                    break;
                case OpDef.AddressMode.DPIndexY:
                    sb.AppendFormat("   ${0:X2},Y", byte1);
                    break;
                case OpDef.AddressMode.DPIndexXInd:
                    sb.AppendFormat("   (${0:X2},X)", byte1);
                    break;
                case OpDef.AddressMode.DPInd:
                    sb.AppendFormat("   (${0:X2})", byte1);
                    break;
                case OpDef.AddressMode.DPIndIndexY:
                    sb.AppendFormat("   (${0:X2}),Y", byte1);
                    break;
                case OpDef.AddressMode.Imm:
                    sb.AppendFormat("   #${0:X2}", byte1);
                    break;
                case OpDef.AddressMode.PCRel:
                    sb.AppendFormat("   ${0:X4}", RelOffset(addr, byte1));
                    break;
                case OpDef.AddressMode.Abs:
                    sb.AppendFormat("   ${0:X2}{1:X2}", byte2, byte1);
                    if (string.IsNullOrEmpty(comment)) {
                        comment = NiftyList.Lookup00Addr((ushort)(byte1 | (byte2 << 8)));
                    }
                    break;
                case OpDef.AddressMode.AbsIndexX:
                    sb.AppendFormat("   ${0:X2}{1:X2},X", byte2, byte1);
                    break;
                case OpDef.AddressMode.AbsIndexY:
                    sb.AppendFormat("   ${0:X2}{1:X2},Y", byte2, byte1);
                    break;
                case OpDef.AddressMode.AbsIndexXInd:
                    sb.AppendFormat("   (${0:X2}{1:X2},X)", byte2, byte1);
                    break;
                case OpDef.AddressMode.AbsInd:
                    sb.AppendFormat("   (${0:X2}{1:X2})", byte2, byte1);
                    break;
                case OpDef.AddressMode.StackInt:
                    if (opLen != 1) {       // BRK/COP can be 1 or 2 bytes
                        sb.AppendFormat("   ${0:X2}", byte1);
                    }
                    break;

                case OpDef.AddressMode.AbsIndLong:
                case OpDef.AddressMode.AbsLong:
                case OpDef.AddressMode.AbsIndexXLong:
                case OpDef.AddressMode.BlockMove:
                case OpDef.AddressMode.DPIndLong:
                case OpDef.AddressMode.DPIndIndexYLong:
                case OpDef.AddressMode.PCRelLong:
                case OpDef.AddressMode.StackAbs:
                case OpDef.AddressMode.StackDPInd:
                case OpDef.AddressMode.StackRTL:
                case OpDef.AddressMode.StackRel:
                case OpDef.AddressMode.StackRelIndIndexY:
                case OpDef.AddressMode.WDM:
                    // Shouldn't see these in 8-bit mode.
                    Debug.Assert(false, "Found 16-bit addr mode on 8-bit CPU");
                    break;
                case OpDef.AddressMode.Unknown:
                    // Used for 6502/65C02 opcodes when "include undoc" isn't set.
                    Debug.Assert(opLen == 1);
                    break;
                default:
                    Debug.Assert(false, "Unhandled address mode: " + op.AddrMode);
                    break;
            }

            if (!string.IsNullOrEmpty(comment)) {
                sb.Append("    ");
                sb.Append(comment);
            }
        }

        /// <summary>
        /// Computes a relative offset, for branch instructions.
        /// </summary>
        private static int RelOffset(int addr, byte offset) {
            return addr + 2 + (sbyte)offset;
        }

        /// <summary>
        /// Computes a long relative offset, for branch instructions.
        /// </summary>
        private static int LongRelOffset(int addr, ushort offset) {
            return addr + 3 + (short)offset;
        }

        /// <summary>
        /// Returns true if the contents of the buffer look like a ProDOS 8 MLI call.
        /// </summary>
        private static bool IsProDOS8Call(byte[] buf, int offset, int length) {
            return (length >= 6 && buf[offset + 0] == 0x20 && buf[offset + 1] == 0x00 &&
                    buf[offset + 2] == 0xbf);
        }
    }
}
