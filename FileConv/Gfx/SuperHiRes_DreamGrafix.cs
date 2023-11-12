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

namespace FileConv.Gfx {
    /// <summary>
    /// Converts a DreamGrafix 256-color or 3200-color graphics file to a bitmap.
    /// </summary>
    /// <remarks>
    /// Based on code provided by Jason Andersen for use in the original CiderPress.
    /// </remarks>
    public class SuperHiRes_DreamGrafix : Converter {
        public const string TAG = "shrdg";
        public const string LABEL = "DreamGrafix graphics document";
        public const string DESCRIPTION =
            "Converts a DreamGrafix image file to a 640x400 index-color or direct-color bitmap.";
        public const string DISCRIMINATOR = "ProDOS PNT/$8005.";
        public override string Tag => TAG;
        public override string Label => LABEL;
        public override string Description => DESCRIPTION;
        public override string Discriminator => DISCRIMINATOR;

        private const int FOOTER_LEN = 17;
        private static readonly byte[] SIGNATURE = Encoding.ASCII.GetBytes("DreamWorld");
        private const int IMAGE_WIDTH = 320;
        private const int IMAGE_HEIGHT = 200;
        private const int SIZE_256_COLOR = 32000 + 256 + 512 + 512;
        private const int SIZE_3200_COLOR = 32000 + 6400 + 512;
        private const int COLOR_MODE_256 = 0;
        private const int COLOR_MODE_3200 = 1;


        private SuperHiRes_DreamGrafix() { }

        public SuperHiRes_DreamGrafix(FileAttribs attrs, Stream? dataStream, Stream? rsrcStream,
                ResourceMgr? resMgr, ConvFlags convFlags, AppHook appHook)
                : base(attrs, dataStream, rsrcStream, resMgr, convFlags, appHook) {
            Applic = TestApplicability();
        }

        protected override Applicability TestApplicability() {
            if (DataStream == null) {
                return Applicability.Not;
            }
            // Look for PNT/$8005.  In theory we should also handle PIC/$8003 (uncompressed form),
            // but nobody seems to use that.
            if (FileAttrs.FileType != FileAttribs.FILE_TYPE_PNT || FileAttrs.AuxType != 0x8005) {
                return Applicability.Not;
            }
            if (!ReadFooter(DataStream, out int colorMode, out int width, out int height)) {
                return Applicability.Not;
            }
            if (width != IMAGE_WIDTH || height != IMAGE_HEIGHT) {
                // Unexpected, not sure what to do with this.
                return Applicability.Not;
            }
            return Applicability.Yes;
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
            ReadFooter(DataStream, out int colorMode, out int width, out int height);
            Debug.Assert(width == IMAGE_WIDTH && height == IMAGE_HEIGHT);

            byte[] fileBuf = new byte[DataStream.Length];
            DataStream.Position = 0;
            DataStream.ReadExactly(fileBuf, 0, (int)DataStream.Length);

            int expectedSize = (colorMode == COLOR_MODE_256) ? SIZE_256_COLOR : SIZE_3200_COLOR;
            byte[] uncompBuf = new byte[expectedSize];
            try {
                int actual = UnpackLZW(fileBuf, uncompBuf);
                if (actual != expectedSize) {
                    return new ErrorText("LZW unpacked to " + actual + " bytes, expected " +
                        expectedSize);
                }
            } catch (IndexOutOfRangeException ex) {
                return new ErrorText("Failed to unpack LZW: " + ex);
            }

            if (colorMode == COLOR_MODE_256) {
                // 256-color.  Just pass the buffer.
                return SuperHiRes.ConvertBuffer(uncompBuf);
            } else {
                // 3200-color.  Extract the color tables first.
                int[,] colorTables =
                    SuperHiRes_Brooks.ExtractColorTables(uncompBuf, 32000);
                return SuperHiRes_Brooks.Convert3200(uncompBuf, 0, colorTables);
            }
        }

        /// <summary>
        /// Reads and parses the file footer.
        /// </summary>
        /// <returns>True if we parsed it successfully, false if not.</returns>
        private static bool ReadFooter(Stream stream, out int colorMode, out int width,
                out int height) {
            colorMode = width = height = -1;
            if (stream.Length <= FOOTER_LEN) {
                return false;
            }

            byte[] buf = new byte[FOOTER_LEN];
            stream.Position = stream.Length - FOOTER_LEN;
            stream.ReadExactly(buf, 0, FOOTER_LEN);
            int offset = 0;
            colorMode = RawData.ReadU16LE(buf, ref offset);
            height = RawData.ReadU16LE(buf, ref offset);
            width = RawData.ReadU16LE(buf, ref offset);
            if (buf[offset] != SIGNATURE.Length ||
                    !RawData.CompareBytes(buf, offset + 1, SIGNATURE, 0, SIGNATURE.Length)) {
                return false;
            }

            return true;
        }

        private const int LZW_CLEAR_CODE = 256;
        private const int LZW_EOF_CODE = 257;
        private const int LZW_FIRST_FREE_CODE = 258;
        private static readonly ushort[] bitMasks = new ushort[13] {
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0x1ff, 0x3ff, 0x7ff, 0xfff
        };

        private ushort[] mHashNext = new ushort[4096];
        private ushort[] mHashChar = new ushort[4096];
        private ushort[] mStack = new ushort[32768];

        /// <summary>
        /// Unpacks DreamGrafix LZW.
        /// </summary>
        /// <remarks>
        /// <para>This is a direct port of the C++ code, which looks like a translation of the
        /// 65816 code.  TODO(someday): rewrite this to be cleaner.</para>
        /// <para>This doesn't do any bounds checking.  The caller is expected to catch the
        /// exceptions when bad data is encountered.</para>
        /// </remarks>
        /// <param name="inBuf">Input buffer.</param>
        /// <param name="outBuf">Output buffer.</param>
        /// <returns>Number of bytes unpacked.</returns>
        private int UnpackLZW(byte[] inBuf, byte[] outBuf) {
            int dstOffset = 0;

            ushort finChar, oldCode, inCode, freeCode, maxCode, k;
            ushort nBitMod1, nBitMask;
            int bitOffset;

            int iCode;
            int stackIdx = 0;

            // Initialize table and code reader.
            InitTable(out nBitMod1, out nBitMask, out maxCode, out freeCode);
            bitOffset = 0;
            finChar = oldCode = 0;

            while (true) {
                iCode = ReadCode(inBuf, ref bitOffset, nBitMod1, nBitMask);
                if (iCode == LZW_EOF_CODE) {
                    break;
                }
                if (iCode == LZW_CLEAR_CODE) {
                    // Got Clear
                    InitTable(out nBitMod1, out nBitMask, out maxCode, out freeCode);
                    iCode = ReadCode(inBuf, ref bitOffset, nBitMod1, nBitMask);
                    oldCode = (ushort)iCode;
                    //k = (ushort)iCode;
                    finChar = (ushort)iCode;
                    outBuf[dstOffset++] = (byte)iCode;
                    continue;
                }

                ushort A = inCode = (ushort)iCode;
                if (iCode < freeCode) {
                    goto inTable;
                }

                mStack[stackIdx] = (byte)finChar;
                stackIdx++;
                A = oldCode;

            inTable:
                if (A < 256) {
                    goto gotChar;
                }
                while (A >= 256) {
                    Debug.Assert(stackIdx >= 0 && stackIdx < mStack.Length);
                    ushort Y1 = A;
                    A = mHashChar[Y1];
                    mStack[stackIdx] = A;
                    stackIdx++;
                    A = mHashNext[Y1];
                }

            gotChar:
                A &= 0xff;
                finChar = A;
                k = A;

                outBuf[dstOffset] = (byte)A;
                ushort Y = 1;
                while (stackIdx != 0) {
                    stackIdx--;
                    A = mStack[stackIdx];
                    outBuf[dstOffset + Y] = (byte)A;
                    Y++;
                }
                dstOffset += Y;

                AddCode(ref freeCode, oldCode, k);
                oldCode = inCode;

                if (freeCode < maxCode) {
                    continue;
                }
                if (nBitMod1 == 12) {
                    continue;       // table is full (should be a table clear next)
                }

                nBitMod1++;
                nBitMask = bitMasks[nBitMod1];
                maxCode <<= 1;
            }

            return dstOffset;
        }

        private static void InitTable(out ushort nBitMod1, out ushort nBitMask, out ushort maxCode,
                out ushort freeCode) {
            nBitMod1 = 9;
            nBitMask = bitMasks[nBitMod1];
            maxCode = (ushort)(1 << nBitMod1);
            freeCode = LZW_FIRST_FREE_CODE;
        }

        private void AddCode(ref ushort freeCode, ushort oldCode, ushort k) {
            mHashChar[freeCode] = k;
            mHashNext[freeCode] = oldCode;
            freeCode++;
        }

        private static int ReadCode(byte[] srcBuf, ref int bitOffset, ushort nBitMod1,
                ushort nBitMask) {
            int bitIdx = bitOffset & 0x07;
            int byteIdx = bitOffset >> 3;
            int iCode = srcBuf[byteIdx];
            iCode |= srcBuf[byteIdx + 1] << 8;
            iCode |= srcBuf[byteIdx + 2] << 16;
            iCode >>= bitIdx;
            iCode &= nBitMask;
            bitOffset += nBitMod1;
            return iCode;
        }
    }
}
