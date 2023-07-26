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
using static DiskArc.Defs;

namespace FileConv.Generic {
    public class HexDump : Converter {
        public const string TAG = "hex";
        public const string LABEL = "Hex Dump";
        public const string DESCRIPTION =
            "Generates a hex dump for file data.  The text conversion is configurable.";
        public override string Tag => TAG;
        public override string Label => LABEL;
        public override string Description => DESCRIPTION;

        public const string OPT_CHAR = "char";

        public override List<OptionDefinition> OptionDefs { get; protected set; } =
            new List<OptionDefinition>() {
                new OptionDefinition(OPT_CHAR, "Char Encoding",
                    OptionDefinition.OptType.Multi, ConvUtil.CHAR_MODE_HIGH_ASCII,
                    ConvUtil.ExportCharSrcTags, ConvUtil.ExportCharSrcDescrs),
            };

        /// <summary>
        /// Converting a 16MB file to a 75MB hex dump took 184ms.  Displaying it in a TextBox
        /// took nearly 10 seconds.  This part isn't the real bottleneck, but HFS files can
        /// get very large, so we want to have some reasonable limit to avoid stalling.
        /// </summary>
        private const int MAX_ALLOWED_LEN = 1024 * 1024 * 16;   // 16MB

        private byte[] mDataBuf = new byte[32768];


        public HexDump(FileAttribs attrs, Stream? dataStream, Stream? rsrcStream,
                ResourceMgr? resMgr, ConvFlags convFlags, AppHook appHook)
                : base(attrs, dataStream, rsrcStream, resMgr, convFlags, appHook) {
            mRsrcAsHex = true;
            Applic = TestApplicability();
        }

        protected override Applicability TestApplicability() {
            return Applicability.Always_Hex;
        }

        public override IConvOutput ConvertFile(Dictionary<string, string> options) {
            SimpleText output = new SimpleText();

            string charConvStr = GetStringOption(options, OPT_CHAR, ConvUtil.CHAR_MODE_HIGH_ASCII);
            if (!ConvUtil.TryParseExportCharSrc(charConvStr, out ConvUtil.ExportCharSrc charMode)) {
                // Unknown value, just use high ASCII.
                charMode = ConvUtil.ExportCharSrc.HighASCII;
                output.Notes.AddW("Unknown character set '" + charConvStr + "'");
            }

            if (DataStream != null) {
                ConvertStream(DataStream, charMode, output.Text);
            } else {
                output.Notes.AddI("File has no data fork");
            }
            return output;
        }

        /// <summary>
        /// Converts a single stream (data or resource fork).
        /// </summary>
        private void ConvertStream(Stream stream, ConvUtil.ExportCharSrc charMode,
                StringBuilder sb) {
            long nonSparseLen;
            if (stream is DiskFileStream) {
                nonSparseLen = ((DiskFileStream)stream).ComputeNonSparseLength();
            } else {
                nonSparseLen = stream.Length;
            }
            if (nonSparseLen > MAX_ALLOWED_LEN) {
                // It takes several seconds to compute and display the hex dump from a 16MB file
                // (which becomes a 75MB text document).  Let's not do that.
                sb.Append("[ non-sparse length (");
                sb.Append(nonSparseLen);
                sb.Append(") is too long for hex dump ]");
                return;
            }

            Formatter.FormatConfig fmtConfig = new Formatter.FormatConfig();
            switch (charMode) {
                case ConvUtil.ExportCharSrc.HighASCII:
                    fmtConfig.HexDumpConvFunc = Formatter.CharConv_HighASCII;
                    break;
                case ConvUtil.ExportCharSrc.Latin:
                    fmtConfig.HexDumpConvFunc = Formatter.CharConv_Latin;
                    break;
                case ConvUtil.ExportCharSrc.MacOSRoman:
                    fmtConfig.HexDumpConvFunc = Formatter.CharConv_MOR;
                    break;
                default:
                    Debug.Assert(false);
                    break;
            }
            Formatter fmt = new Formatter(fmtConfig);

            // Pre-set the length to cover the full file.
            int expectedLength = (int)((stream.Length + 15) / 16) * (Formatter.HEX_DUMP_WIDTH + 2);
            sb.EnsureCapacity(expectedLength);

            if (stream is DiskFileStream) {
                long holeStart = 0;
                long dataStart = stream.Seek(0, SEEK_ORIGIN_DATA);
                while (dataStart < stream.Length) {
                    if (holeStart != dataStart) {
                        sb.Append(Environment.NewLine);
                        sb.Append("[ sparse from +$");
                        sb.Append(holeStart.ToString("x6"));
                        sb.Append(" to +$");
                        sb.Append((dataStart - 1).ToString("x6"));
                        sb.Append(", length=");
                        sb.Append(dataStart - holeStart);
                        sb.Append(" ]");
                        sb.Append(Environment.NewLine);
                        sb.Append(Environment.NewLine);
                    }
                    // Find the end of the data part.
                    long dataEnd = stream.Seek(dataStart, SEEK_ORIGIN_HOLE);
                    Debug.Assert(dataEnd > dataStart);
                    // Output this section.
                    stream.Position = dataStart;
                    ConvertSection(stream, dataEnd - dataStart, (int)dataStart, fmt, sb);
                    // Advance to the next data section.
                    holeStart = dataEnd;
                    dataStart = stream.Seek(holeStart, SEEK_ORIGIN_DATA);
                }
                if (holeStart != dataStart) {
                    sb.Append(Environment.NewLine);
                    sb.Append("[ sparse to end of file, length=");
                    sb.Append(dataStart - holeStart);
                    sb.Append(" ]");
                    sb.Append(Environment.NewLine);
                }
            } else {
                // File from archive, convert entire thing.
                stream.Position = 0;
                ConvertSection(stream, stream.Length, 0, fmt, sb);
            }
        }

        /// <summary>
        /// Converts a chunk of the file.
        /// </summary>
        private void ConvertSection(Stream stream, long count, int addr, Formatter fmt,
                StringBuilder sb) {
            while (count != 0) {
                // Read a large chunk.
                int readCount = (int)Math.Min(count, mDataBuf.Length);
                int actual = stream.Read(mDataBuf, 0, readCount);
                if (actual == 0) {
                    break;      // EOF reached
                }
                int offset = 0;
                while (actual != 0) {
                    // Format 16 bytes at a time.  Sparse regions should begin and end at block
                    // boundaries, so we should be 16-byte aligned.
                    int fmtCount = Math.Min(actual, 16);
                    fmt.FormatHexDumpLine(mDataBuf, offset, fmtCount, addr, sb);
                    sb.Append(Environment.NewLine);
                    offset += fmtCount;
                    addr += fmtCount;
                    actual -= fmtCount;
                }

                count -= readCount;
            }
        }
    }
}
