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

namespace FileConv.Code {
    /// <summary>
    /// Formats an Apple Pascal codefile file as text.
    /// </summary>
    public class ApplePascal_Code : Converter {
        public const string TAG = "pcd";
        public const string LABEL = "Apple Pascal Code";
        public const string DESCRIPTION =
            "Formats Apple Pascal code file as text, listing the various segments.";
        public const string DISCRIMINATOR = "Pascal Codefile, ProDOS PCD.";
        public override string Tag => TAG;
        public override string Label => LABEL;
        public override string Description => DESCRIPTION;
        public override string Discriminator => DISCRIMINATOR;


        private const int SEG_DICT_SIZE = 1024;
        private const int MIN_LEN = SEG_DICT_SIZE;          // must have room for header
        private const int MAX_LEN = 16 * 1024 * 1024 - 1;   // maybe?
        private const int SEG_NAME_LEN = 8;
        private const int NUM_SEGMENTS = 16;

        private enum SegmentKind : byte {
            Linked = 0,             // fully executable code segment; all externs resolved
            HostSeg,                // outer block of Pascal program
            SegProc,                // Pascal segment procedure (not used)
            UnitSeg,                // compiled regular UNIT
            SeprtSeg,               // separately-compiled procedure or function
            UnlinkedIntrins,        // INTRINSIC UNIT with unresolved calls
            LinkedIntrins,          // INTRINSIC UNIT in its final, ready-to-run state
            DataSeg,                // specification for data segment associated with INTRINSIC UNIT
        }

        private enum MachineType : byte {
            Unidentified = 0,
            PCode_MSB = 1,
            PCode_LSB = 2,
            Asm3 = 3,
            Asm4 = 4,
            Asm5 = 5,
            Asm6 = 6,
            Asm6502 = 7,
            Asm8 = 8,
            Asm9 = 9,
        }

        /// <summary>
        /// Segment information, extracted from the Segment Dictionary in block 0.
        /// </summary>
        private class Segment {
            public ushort mCodeAddr;
            public ushort mCodeLeng;
            public byte[] mRawName = new byte[SEG_NAME_LEN];
            public SegmentKind mSegmentKind;
            public ushort mTextAddr;
            public byte mSegNum;
            public MachineType mMachType;
            public byte mReserved;
            public byte mVersion;

            public string mName = string.Empty;

            public void Load(byte[] buf, int offset, int segNum) {
                // +$00: CODELENG, CODEADDR
                mCodeAddr = RawData.GetU16LE(buf, offset + 0x00 + segNum * 4 + 0x00);
                mCodeLeng = RawData.GetU16LE(buf, offset + 0x00 + segNum * 4 + 0x02);
                // +$40: SEGNAME
                Array.Copy(buf, offset + 0x40 + segNum * SEG_NAME_LEN, mRawName, 0, SEG_NAME_LEN);
                // +$c0: SEGKIND
                mSegmentKind = (SegmentKind)RawData.GetU16LE(buf, offset + 0xc0 + segNum * 2);
                // +$e0: TEXTADDR
                mTextAddr = RawData.GetU16LE(buf, offset + 0xe0 + segNum * 2);
                // +$100: SEGINFO
                ushort segInfo = RawData.GetU16LE(buf, offset + 0x100 + segNum * 2);
                mSegNum = (byte)(segInfo & 0xff);
                mMachType = (MachineType)((segInfo >> 8) & 0x0f);
                mReserved = (byte)((segInfo >> 12) & 0x01);
                mVersion = (byte)((segInfo >> 13) & 0x07);

                mName = Encoding.ASCII.GetString(mRawName, 0, SEG_NAME_LEN);
            }
        }


        private ApplePascal_Code() { }

        public ApplePascal_Code(FileAttribs attrs, Stream? dataStream, Stream? rsrcStream,
                ResourceMgr? resMgr, ConvFlags convFlags, AppHook appHook)
                : base(attrs, dataStream, rsrcStream, resMgr, convFlags, appHook) {
            Applic = TestApplicability();
        }

        protected override Applicability TestApplicability() {
            // Simple file type check, with a test for minimum length.
            if (DataStream == null) {
                return Applicability.Not;
            }
            if (FileAttrs.FileType == FileAttribs.FILE_TYPE_PCD &&
                    DataStream.Length >= MIN_LEN && DataStream.Length <= MAX_LEN) {
                return Applicability.Yes;
            }
            return Applicability.Not;
        }

        public override Type GetExpectedType(Dictionary<string, string> options) {
            return typeof(SimpleText);
        }

        public override IConvOutput ConvertFile(Dictionary<string, string> options) {
            if (Applic <= Applicability.Not) {
                Debug.Assert(false);
                return new ErrorText("ERROR");
            }
            Debug.Assert(DataStream != null);

            // Load entire file.
            DataStream.Position = 0;
            int length = (int)DataStream.Length;
            byte[] dataBuf = new byte[length];
            DataStream.ReadExactly(dataBuf, 0, length);

            SimpleText output = new SimpleText();

            Segment[] segments = LoadSegmentData(dataBuf);
            int segCount = 0;
            for (int i = 0; i < NUM_SEGMENTS; i++) {
                if (segments[i].mCodeAddr != 0 || segments[i].mCodeLeng != 0) {
                    segCount++;
                }
            }
            if (segCount == 0) {
                output.Append("No segments found.");
                return output;
            }

            output.Append("Pascal code file has ");
            output.Append(segCount);
            output.Append((segCount == 1) ? " segment" : " segments");
            output.AppendLine();

            uint intrinsSegs = RawData.GetU32LE(dataBuf, 0x120);
            output.Append("Intrinsic units required:");
            if (intrinsSegs == 0) {
                output.Append(" none");
            } else {
                for (int i = 0; i < 32; i++) {
                    if ((intrinsSegs & 0x01) != 0) {
                        output.Append(' ');
                        output.Append(i);
                    }
                    intrinsSegs >>= 1;
                }
            }
            output.AppendLine();

            Formatter.FormatConfig fmtConfig = new Formatter.FormatConfig();
            fmtConfig.HexDumpConvFunc = Formatter.CharConv_HighASCII;
            Formatter fmt = new Formatter(fmtConfig);

            for (int i = 0; i < segments.Length; i++) {
                PrintSegment(segments[i], i, dataBuf, fmt, output);
            }
            return output;
        }

        /// <summary>
        /// Loads the data from the segment dictionary.
        /// </summary>
        /// <param name="buf">Buffer with file data.</param>
        /// <returns>Array with data for all 16 slots.</returns>
        private static Segment[] LoadSegmentData(byte[] buf) {
            Segment[] segments = new Segment[NUM_SEGMENTS];
            for (int i = 0; i < NUM_SEGMENTS; i++) {
                segments[i] = new Segment();
                segments[i].Load(buf, 0, i);
            }
            return segments;
        }

        /// <summary>
        /// Prints a description of one segment.
        /// </summary>
        /// <param name="seg">Segment to print.</param>
        /// <param name="segIndex">Segment index (0-15).</param>
        /// <param name="buf">File data buffer.</param>
        /// <param name="output">Text output object.</param>
        private static void PrintSegment(Segment seg, int segIndex, byte[] buf, Formatter fmt,
                SimpleText output) {
            if (seg.mCodeAddr == 0 && seg.mCodeLeng == 0) {
                return;

            }
            string segKindStr;
            switch (seg.mSegmentKind) {
                case SegmentKind.Linked:
                    segKindStr = "LINKED";
                    break;
                case SegmentKind.HostSeg:
                    segKindStr = "HOSTSEG";
                    break;
                case SegmentKind.SegProc:
                    segKindStr = "SEGPROC";
                    break;
                case SegmentKind.UnitSeg:
                    segKindStr = "UNITSEG";
                    break;
                case SegmentKind.SeprtSeg:
                    segKindStr = "SEPRTSEG";
                    break;
                case SegmentKind.UnlinkedIntrins:
                    segKindStr = "UNLINKED_INTRINS";
                    break;
                case SegmentKind.LinkedIntrins:
                    segKindStr = "LINKED_INTRINS";
                    break;
                case SegmentKind.DataSeg:
                    segKindStr = "DATASEG";
                    break;
                default:
                    segKindStr = "UNKNOWN-" + (int)seg.mSegmentKind;
                    break;
            }

            string machTypeStr;
            switch (seg.mMachType) {
                case MachineType.Unidentified:
                    machTypeStr = "unidentified";
                    break;
                case MachineType.PCode_MSB:
                    machTypeStr = "P-Code (MSB first)";
                    break;
                case MachineType.PCode_LSB:
                    machTypeStr = "P-Code (LSB first)";
                    break;
                case MachineType.Asm6502:
                    machTypeStr = "Apple II 6502 machine code";
                    break;
                case MachineType.Asm3:
                case MachineType.Asm4:
                case MachineType.Asm5:
                case MachineType.Asm6:
                case MachineType.Asm8:
                case MachineType.Asm9:
                    machTypeStr = "Machine code (type " + (int)seg.mMachType + ")";
                    break;
                default:
                    machTypeStr = "UNKNOWN-" + (int)seg.mMachType;
                    break;
            }

            output.AppendLine();
            output.AppendLine(string.Format("Segment {0}: '{1}' ({2})",
                segIndex, seg.mName, segKindStr));
            output.AppendLine("  Segment start block: " + seg.mCodeAddr);
            output.AppendLine("  Segment length: " + seg.mCodeLeng);
            output.AppendLine("  Text address: " + seg.mTextAddr);
            output.AppendLine(string.Format("  Segment info: segNum={0} version={1} machType={2}",
                seg.mSegNum, seg.mVersion, machTypeStr));
            output.AppendLine();

            if (seg.mCodeAddr == 0) {
                if (seg.mSegmentKind == SegmentKind.DataSeg) {
                    // This just defines space, like BSS.
                    output.AppendLine("(no data for DATASEG segments)");
                } else {
                    output.AppendLine("Whoops: segment start block == 0 not expected");
                }
            } else {
                if (seg.mCodeAddr * 512 + seg.mCodeLeng > buf.Length) {
                    output.AppendLine("Whoops: code addr+length runs off end of file");
                } else {
                    StringBuilder sb = new StringBuilder(seg.mCodeLeng * 4);
                    fmt.FormatHexDump(buf, seg.mCodeAddr * 512, seg.mCodeLeng, sb);
                    output.Append(sb);
                }
            }
        }
    }
}
