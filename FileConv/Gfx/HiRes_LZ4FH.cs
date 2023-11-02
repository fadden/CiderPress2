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

namespace FileConv.Gfx {
    /// <summary>
    /// Converts an LZ4FH-compressed Apple II hi-res screen image.
    /// </summary>
    public class HiRes_LZ4FH : Converter {
        public const string TAG = "lz4fh";
        public const string LABEL = "LZ4FH Compressed Graphics";
        public const string DESCRIPTION = "Converts an LZ4FH-compressed hi-res screen to a bitmap.";
        public const string DISCRIMINATOR = "ProDOS FOT with auxtype $8066.";
        public override string Tag => TAG;
        public override string Label => LABEL;
        public override string Description => DESCRIPTION;
        public override string Discriminator => DISCRIMINATOR;

        public const string OPT_BW = "bw";

        public override List<OptionDefinition> OptionDefs { get; protected set; } =
            new List<OptionDefinition>() {
                new OptionDefinition(OPT_BW, "Black & white",
                    OptionDefinition.OptType.Boolean, "false"),
            };

        private const int EXPECTED_LEN = HiRes.EXPECTED_LEN;
        private const int MAX_EXPANSION = 100;              // int(ceil(8192/255)) * 33 + 1
        private const int MIN_LEN = 128;                    // actual seems to be 136; be generous
        private const int MAX_LEN = EXPECTED_LEN + MAX_EXPANSION;
        private const byte LZ4FH_MAGIC = 0x66;              // ASCII 'f'


        private HiRes_LZ4FH() { }

        public HiRes_LZ4FH(FileAttribs attrs, Stream? dataStream, Stream? rsrcStream,
                ResourceMgr? resMgr, ConvFlags convFlags, AppHook appHook)
                : base(attrs, dataStream, rsrcStream, resMgr, convFlags, appHook) {
            Applic = TestApplicability();
        }

        protected override Applicability TestApplicability() {
            if (DataStream == null) {
                return Applicability.Not;
            }
            if (DataStream.Length < MIN_LEN || DataStream.Length > MAX_LEN) {
                return Applicability.Not;
            }
            if (FileAttrs.FileType == FileAttribs.FILE_TYPE_FOT &&
                    FileAttrs.AuxType == 0x8066) {
                return Applicability.Yes;
            }
            return Applicability.Not;
        }

        public override Type GetExpectedType(Dictionary<string, string> options) {
            return typeof(IBitmap);
        }

        public override IConvOutput ConvertFile(Dictionary<string, string> options) {
            if (Applic <= Applicability.Not) {
                Debug.Assert(false);
                return new ErrorText("ERROR");
            }
            Debug.Assert(DataStream != null);

            byte[] compBuf = new byte[DataStream.Length];
            DataStream.Position = 0;
            DataStream.ReadExactly(compBuf, 0, (int)DataStream.Length);
            if (compBuf[0] != LZ4FH_MAGIC) {
                return new ErrorText("Invalid magic number in LZ4FH");
            }

            bool doMonochrome = GetBoolOption(options, OPT_BW, false);
            byte[] hgrBuf = new byte[EXPECTED_LEN];
            if (!UncompressLZ4FH(compBuf, hgrBuf)) {
                return new ErrorText("Unable to decompress LZ4FH data");
            }
            return HiRes.ConvertBuffer(hgrBuf, Palette8.Palette_HiRes, doMonochrome);
        }

        private const int MIN_MATCH_LEN = 4;
        private const int INITIAL_LEN = 15;
        private const int EMPTY_MATCH_TOKEN = 253;
        private const int EOD_MATCH_TOKEN = 254;

        /// <summary>
        /// Decompresses a buffer of LZ4FH data.
        /// </summary>
        /// <returns>True on success, false if the input was corrupted.</returns>
        private static bool UncompressLZ4FH(byte[] inBuf, byte[] outBuf) {
            int inOffset = 1;   // skip magic
            int outOffset = 0;
            while (true) {
                byte mixedLen = inBuf[inOffset++];

                int literalLen = mixedLen >> 4;
                if (literalLen != 0) {
                    if (literalLen == INITIAL_LEN) {
                        literalLen += inBuf[inOffset++];
                    }
                    if (inOffset + literalLen > inBuf.Length ||
                            outOffset + literalLen > outBuf.Length) {
                        Debug.WriteLine("Buffer overrun (literal)");
                        return false;
                    }
                    Array.Copy(inBuf, inOffset, outBuf, outOffset, literalLen);
                    inOffset += literalLen;
                    outOffset += literalLen;
                }

                int matchLen = mixedLen & 0x0f;
                if (matchLen == INITIAL_LEN) {
                    byte addon = inBuf[inOffset++];
                    if (addon == EMPTY_MATCH_TOKEN) {
                        matchLen = -MIN_MATCH_LEN;      // balance min add below
                    } else if (addon == EOD_MATCH_TOKEN) {
                        break;      // out of while loop
                    } else {
                        matchLen += addon;
                    }
                }

                matchLen += MIN_MATCH_LEN;
                if (matchLen != 0) {
                    int matchOffset = inBuf[inOffset++];
                    matchOffset |= inBuf[inOffset++] << 8;
                    // We're copying a potentially overlapping region within the output buffer.
                    // We can't use Array.Copy because we want the self-copy behavior.
                    if (matchOffset + matchLen > outBuf.Length ||
                            outOffset + matchLen > outBuf.Length) {
                        Debug.WriteLine("Buffer overrun (match)");
                        return false;
                    }
                    while (matchLen-- != 0) {
                        outBuf[outOffset++] = outBuf[matchOffset++];
                    }
                }
            }
            return true;
        }
    }
}
