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
                new OptionDefinition(OPT_FOLLOW, "Follow SEP/REP",
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
            EvaluateApplicability(out CpuDef.CpuType recommendedCpu, out int startAddr);
            // If filetype indicates 65816, but preference is 8-bit, override preference.
            if (recommendedCpu == CpuDef.CpuType.Cpu65816 && cpuType != CpuDef.CpuType.Cpu65816) {
                cpuType = recommendedCpu;
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
                //   [$B1,$BD] = OBJ, LIB, S16, RTL, EXE, PIF, TIF, NDA, CDA, TOL, DVR, LDF, FST
                // These are OMF files, so we want the OMF formatter to take precedence.
                // Because OMF code is relocatable, a simple disassembly isn't very useful, but
                // we still want it to be on the list.  (The OMF converter may want to generate a
                // disassembly for portions known to hold executable code.)
                applic = Applicability.Probably;
                cpuType = CpuDef.CpuType.Cpu65816;
                startAddr = 0x020000;
            } else if (fileType == FileAttribs.FILE_TYPE_OS) {
                // These may have raw 65816 code, OMF, or arbitrary data.
                // "Start.GS.OS" is code that starts in bank 0.
                applic = Applicability.Probably;
                cpuType = CpuDef.CpuType.Cpu65816;
                startAddr = 0x000000;
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
        private bool m16Bit;
        private bool mDoFollow;
        private StatusFlags mStatusFlags;
        private StringBuilder mLineBuf = new StringBuilder(80);

        /// <summary>
        /// Constructor for 8-bit code.
        /// </summary>
        public Convert65(byte[] buf, int startAddr, CpuDef.CpuType cpuType,
                bool doInclUndoc, bool doTwoBrk) {
            mCpuDef = CpuDef.GetBestMatch(cpuType, doInclUndoc, doTwoBrk);
            mBuf = buf;
            mAddr = startAddr;

            m16Bit = false;
            mStatusFlags = StatusFlags.DefaultValue;
            mStatusFlags.E = 1;
        }

        /// <summary>
        /// Constructor for 16-bit code.
        /// </summary>
        public Convert65(byte[] buf, int startAddr, CpuDef.CpuType cpuType,
                bool doFollow, bool doStartLong, bool doTwoBrk) {
            mCpuDef = CpuDef.GetBestMatch(cpuType, false, doTwoBrk);
            mBuf = buf;
            mAddr = startAddr;
            m16Bit = true;
            mDoFollow = doFollow;
            mStatusFlags = StatusFlags.DefaultValue;
            mStatusFlags.E = 0;
            mStatusFlags.M = doStartLong ? 0 : 1;
            mStatusFlags.X = doStartLong ? 0 : 1;
        }

        public IConvOutput Convert() {
            SimpleText output = new SimpleText();
            if (m16Bit) {
                OutputMonitor16(0, mBuf.Length, output);
            } else {
                OutputMonitor8(0, mBuf.Length, output);
            }
            return output;
        }

        internal void OutputMonitor8(int offset, int length, SimpleText output) {
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
                    PrintMonitor8Line(op, mStatusFlags, tmpBuf, 0, mAddr + offset, "(truncated)",
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
        internal static void PrintMonitor8Line(OpDef op, StatusFlags flags, byte[] buf, int offset,
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
                case OpDef.AddressMode.StackPCRelLong:
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

        internal void OutputMonitor16(int offset, int length, SimpleText output) {
            int endOffset = offset + length;        // offset of first byte past end
            while (offset < endOffset) {
                OpDef op = mCpuDef.GetOpDef(mBuf[offset]);
                int opLen = op.GetLength(mStatusFlags);
                Debug.Assert(opLen > 0);
                int addr = mAddr + offset;
                int bank = addr >> 16;
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
                    PrintMonitor16Line(op, mStatusFlags, tmpBuf, 0, mAddr + offset, "(truncated)",
                        mLineBuf);
                } else if (bank == 0 && IsProDOS8Call(mBuf, offset, endOffset - offset)) {
                    // Print and skip P8 inline call stuff.
                    string callName = NiftyList.LookupP8MLI(mBuf[offset + 3]);
                    if (string.IsNullOrEmpty(callName)) {
                        callName = "(Unknown P8 MLI)";
                    }
                    PrintMonitor16Line(op, mStatusFlags, mBuf, offset, mAddr + offset, callName,
                        mLineBuf);
                    output.AppendLine(mLineBuf);
                    mLineBuf.Clear();
                    byte callNum = mBuf[offset + 3];
                    byte addrLo = mBuf[offset + 4];
                    byte addrHi = mBuf[offset + 5];
                    output.AppendLine(string.Format("00/{0:X4}: {1:X2}                {1:X2}",
                        mAddr + offset + 3, callNum));
                    mLineBuf.AppendFormat("00/{0:X4}: {1:X2} {2:X2}             {2:X2}{1:X2}",
                        mAddr + offset + 4, addrLo, addrHi);
                    opLen += 3;
                } else if (IsToolboxCall(mBuf, offset, endOffset - offset)) {
                    ushort req = (ushort)(mBuf[offset - 2] | (mBuf[offset - 1] << 8));
                    string callName = NiftyList.LookupToolbox(req);
                    PrintMonitor16Line(op, mStatusFlags, mBuf, offset, mAddr + offset, callName,
                        mLineBuf);
                } else if (IsInlineGSOS(mBuf, offset, endOffset - offset)) {
                    ushort req = (ushort)(mBuf[offset + 4] | (mBuf[offset + 5] << 8));
                    string callName = NiftyList.LookupGSOS(req);
                    PrintMonitor16Line(op, mStatusFlags, mBuf, offset, mAddr + offset, callName,
                        mLineBuf);
                    output.AppendLine(mLineBuf);
                    mLineBuf.Clear();
                    output.AppendLine(
                        string.Format("{0:X2}/{1:X4}: {2:X2} {3:X2}             {3:X2}{2:X2}",
                            bank, (mAddr + offset + 4) & 0xffff,
                            mBuf[offset + 4], mBuf[offset + 5]));
                    mLineBuf.AppendFormat(
                        "{0:X2}/{1:X4}: {2:X2} {3:X2} {4:X2} {5:X2}       {5:X2}{4:X2}{3:X2}{2:X2}",
                        bank, (mAddr + offset + 6) & 0xffff,
                        mBuf[offset + 6], mBuf[offset + 7], mBuf[offset + 8], mBuf[offset + 9]);
                    opLen += 6;
                } else if (IsStackGSOS(mBuf, offset, endOffset - offset)) {
                    ushort req = (ushort)(mBuf[offset - 2] | (mBuf[offset - 1] << 8));
                    string callName = NiftyList.LookupGSOS(req);
                    PrintMonitor16Line(op, mStatusFlags, mBuf, offset, mAddr + offset, callName,
                        mLineBuf);
                } else {
                    PrintMonitor16Line(op, mStatusFlags, mBuf, offset, mAddr + offset, string.Empty,
                        mLineBuf);
                }

                // Track SEP/REP, which can be used to change the register width.  Tracking XCE
                // is less likely to helpful.
                if (mDoFollow && offset + 1 < endOffset &&
                        (op == OpDef.OpSEP_Imm || op == OpDef.OpREP_Imm)) {
                    op.ComputeFlagChanges(mStatusFlags, mBuf, offset, out StatusFlags newFlags,
                        out StatusFlags unusedFlags);
                    mStatusFlags = newFlags;

                    // Show what changed with this operation.
                    byte operand = mBuf[offset + 1];
                    mLineBuf.Append("     (");
                    if ((operand & 0x20) != 0) {
                        mLineBuf.Append(mStatusFlags.M == 0 ? "longm" : "shortm");
                    }
                    if ((operand & 0x10) != 0) {
                        if ((operand & 0x20) != 0) {
                            mLineBuf.Append(' ');
                        }
                        mLineBuf.Append(mStatusFlags.X == 0 ? "longx" : "shortx");
                    }
                    //mLineBuf.Append("M=" + mStatusFlags.M + " X=" + mStatusFlags.X);
                    mLineBuf.Append(')');
                }

                output.AppendLine(mLineBuf);
                mLineBuf.Clear();

                offset += opLen;
            }
        }

        internal static void PrintMonitor16Line(OpDef op, StatusFlags flags, byte[] buf, int offset,
                int longAddr, string comment, StringBuilder sb) {
            byte byte0, byte1, byte2, byte3;
            int opLen = op.GetLength(flags);
            int bank = longAddr >> 16;
            int addr = longAddr & 0xffff;
            switch (opLen) {
                case 1:
                    byte0 = buf[offset];
                    byte1 = byte2 = byte3 = 0xcc;
                    sb.AppendFormat("{0:X2}/{1:X4}: {2:X2}            {3,-3}",
                        bank, addr, byte0, op.Mnemonic.ToUpper());
                    break;
                case 2:
                    byte0 = buf[offset];
                    byte1 = buf[offset + 1];
                    byte2 = byte3 = 0xcc;
                    sb.AppendFormat("{0:X2}/{1:X4}: {2:X2} {3:X2}         {4,-3}",
                        bank, addr, byte0, byte1, op.Mnemonic.ToUpper());
                    break;
                case 3:
                    byte0 = buf[offset];
                    byte1 = buf[offset + 1];
                    byte2 = buf[offset + 2];
                    byte3 = 0xcc;
                    sb.AppendFormat("{0:X2}/{1:X4}: {2:X2} {3:X2} {4:X2}      {5,-3}",
                        bank, addr, byte0, byte1, byte2, op.Mnemonic.ToUpper());
                    break;
                case 4:
                    byte0 = buf[offset];
                    byte1 = buf[offset + 1];
                    byte2 = buf[offset + 2];
                    byte3 = buf[offset + 3];
                    sb.AppendFormat("{0:X2}/{1:X4}: {2:X2} {3:X2} {4:X2} {5:X2}   {6,-3}",
                        bank, addr, byte0, byte1, byte2, byte3, op.Mnemonic.ToUpper());
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
                case OpDef.AddressMode.StackRTL:
                case OpDef.AddressMode.Acc:
                    break;
                case OpDef.AddressMode.DP:
                    sb.AppendFormat(" {0:X2}", byte1);
                    break;
                case OpDef.AddressMode.DPIndexX:
                    sb.AppendFormat(" {0:X2},X", byte1);
                    break;
                case OpDef.AddressMode.DPIndexY:
                    sb.AppendFormat(" {0:X2},Y", byte1);
                    break;
                case OpDef.AddressMode.DPIndexXInd:
                    sb.AppendFormat(" ({0:X2},X)", byte1);
                    break;
                case OpDef.AddressMode.DPInd:
                    sb.AppendFormat(" ({0:X2})", byte1);
                    break;
                case OpDef.AddressMode.DPIndIndexY:
                    sb.AppendFormat(" ({0:X2}),Y", byte1);
                    break;
                case OpDef.AddressMode.Imm:
                case OpDef.AddressMode.ImmLongA:
                case OpDef.AddressMode.ImmLongXY:
                    if (opLen == 2) {
                        sb.AppendFormat(" #{0:X2}", byte1);
                    } else {
                        sb.AppendFormat(" #{0:X2}{1:X2}", byte2, byte1);
                    }
                    break;
                case OpDef.AddressMode.PCRel:
                    int pcOffset = (sbyte)byte1;
                    if (pcOffset < 0) {
                        sb.AppendFormat(" {0:X4} {{-{1:X2}}}", RelOffset(addr, byte1), -pcOffset);
                    } else {
                        sb.AppendFormat(" {0:X4} {{+{1:X2}}}", RelOffset(addr, byte1), pcOffset);
                    }
                    break;
                case OpDef.AddressMode.Abs:
                    sb.AppendFormat(" {0:X2}{1:X2}", byte2, byte1);
                    if (bank == 0 && string.IsNullOrEmpty(comment)) {
                        comment = NiftyList.Lookup00Addr((ushort)(byte1 | (byte2 << 8)));
                    }
                    break;
                case OpDef.AddressMode.AbsIndexX:
                    sb.AppendFormat(" {0:X2}{1:X2},X", byte2, byte1);
                    break;
                case OpDef.AddressMode.AbsIndexY:
                    sb.AppendFormat(" {0:X2}{1:X2},Y", byte2, byte1);
                    break;
                case OpDef.AddressMode.AbsIndexXInd:
                    sb.AppendFormat(" ({0:X2}{1:X2},X)", byte2, byte1);
                    break;
                case OpDef.AddressMode.AbsInd:
                    sb.AppendFormat(" ({0:X2}{1:X2})", byte2, byte1);
                    break;
                case OpDef.AddressMode.WDM:
                case OpDef.AddressMode.StackInt:
                    if (opLen != 1) {       // BRK/COP can be 1 or 2 bytes
                        sb.AppendFormat(" {0:X2}", byte1);
                    }
                    break;

                case OpDef.AddressMode.AbsIndLong:      // JML
                    sb.AppendFormat(" ({0:X2}{1:X2})", byte2, byte1);
                    break;
                case OpDef.AddressMode.AbsLong:
                    sb.AppendFormat(" {0:X2}{1:X2}{2:X2}", byte3, byte2, byte1);
                    if (string.IsNullOrEmpty(comment)) {
                        ushort absAddr = (ushort)(byte1 | (byte2 << 8));
                        if (byte3 == 0x00) {
                            comment = NiftyList.Lookup00Addr(absAddr);
                        } else if (byte3 == 0x01) {
                            comment = NiftyList.Lookup01Vector(absAddr);
                        } else if (byte3 == 0xe0) {
                            comment = NiftyList.LookupE0Vector(absAddr);
                        } else if (byte3 == 0xe1) {
                            comment = NiftyList.LookupE1Vector(absAddr);
                        }
                    }
                    break;
                case OpDef.AddressMode.AbsIndexXLong:
                    sb.AppendFormat(" {0:X2}{1:X2}{2:X2},X", byte3, byte2, byte1);
                    break;
                case OpDef.AddressMode.BlockMove:
                    sb.AppendFormat(" {0:X2}{1:X2}", byte2, byte1);
                    break;
                case OpDef.AddressMode.DPIndLong:
                    sb.AppendFormat(" [{0:X2}]", byte1);
                    break;
                case OpDef.AddressMode.DPIndIndexYLong:
                    sb.AppendFormat(" [{0:X2}],Y", byte1);
                    break;
                case OpDef.AddressMode.StackPCRelLong:      // PER
                case OpDef.AddressMode.PCRelLong:           // BRL
                    short lpcOffset = (short)(byte1 | (byte2 << 8));
                    if (lpcOffset < 0) {
                        sb.AppendFormat(" {0:X4} {{-{1:X4}}}",
                            RelLongOffset((ushort)addr, (ushort)lpcOffset), -lpcOffset);
                    } else {
                        sb.AppendFormat(" {0:X4} {{+{1:X4}}}",
                            RelLongOffset((ushort)addr, (ushort)lpcOffset), lpcOffset);
                    }
                    break;
                case OpDef.AddressMode.StackAbs:            // PEA
                    sb.AppendFormat(" {0:X2}{1:X2}", byte2, byte1);
                    break;
                case OpDef.AddressMode.StackDPInd:
                    sb.AppendFormat(" {0:X2}", byte1);
                    break;
                case OpDef.AddressMode.StackRel:
                    sb.AppendFormat(" {0:X2},S", byte1);
                    break;
                case OpDef.AddressMode.StackRelIndIndexY:
                    sb.AppendFormat(" ({0:X2},S),Y", byte1);
                    break;
                case OpDef.AddressMode.Unknown:
                    // Shouldn't see these in 65816 mode.
                    Debug.Assert(opLen == 1);
                    Debug.Assert(false, "unhandled opcode " + op);
                    break;
                default:
                    Debug.Assert(false, "Unhandled address mode: " + op.AddrMode);
                    break;
            }

            if (!string.IsNullOrEmpty(comment)) {
                if (opLen < 4) {
                    sb.Append("    ");
                } else {
                    sb.Append("  ");
                }
                sb.Append(comment);
            }
        }

        /// <summary>
        /// Computes a relative offset, for branch instructions.
        /// </summary>
        private static ushort RelOffset(int addr, byte offset) {
            return (ushort)(addr + 2 + (sbyte)offset);
        }

        /// <summary>
        /// Computes a long relative offset, for branch instructions.
        /// </summary>
        private static ushort RelLongOffset(int addr, ushort offset) {
            return (ushort)(addr + 3 + (short)offset);
        }

        /// <summary>
        /// Returns true if the contents of the buffer look like a ProDOS 8 MLI call.
        /// </summary>
        private static bool IsProDOS8Call(byte[] buf, int offset, int length) {
            // Look for "JSR $BF00".
            return length >= 6 && buf[offset + 0] == 0x20 && buf[offset + 1] == 0x00 &&
                    buf[offset + 2] == 0xbf;
        }

        /// <summary>
        /// Returns true if the contents of the buffer look like a IIgs toolbox call.
        /// </summary>
        private static bool IsToolboxCall(byte[] buf, int offset, int length) {
            // Look for "JSL $E10000" immediately after a long LDX.  We could check the status
            // flags to confirm long registers but that's probably not necessary.
            return length >= 4 && offset >= 3 &&
                buf[offset + 0] == 0x22 && buf[offset + 1] == 0x00 && buf[offset + 2] == 0x00 &&
                buf[offset + 3] == 0xe1 && buf[offset - 3] == 0xa2;     // LDX #xxxx
        }

        /// <summary>
        /// Returns true if the contents of the buffer look like an inline GS/OS call.
        /// </summary>
        private static bool IsInlineGSOS(byte[] buf, int offset, int length) {
            // Look for "JSL $E100A8".
            return length >= 10 && buf[offset + 0] == 0x22 && buf[offset + 1] == 0xa8 &&
                buf[offset + 2] == 0x00 && buf[offset + 3] == 0xe1;
        }

        /// <summary>
        /// Returns true if the contents of the buffer look like a stack-based GS/OS call.
        /// </summary>
        private static bool IsStackGSOS(byte[] buf, int offset, int length) {
            // Look for "JSL $E100B0" immediately after a PEA.
            return length >= 4 && offset >= 3 &&
                buf[offset + 0] == 0x22 && buf[offset + 1] == 0xb0 && buf[offset + 2] == 0x00 &&
                buf[offset + 3] == 0xe1 && buf[offset - 3] == 0xf4;     // PEA xxxx
        }
    }
}
