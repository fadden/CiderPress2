/*
 * Copyright 2019 faddenSoft
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
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using CommonUtil;
using FileConv;

namespace cp2_avalonia.Common {
    /// <summary>
    /// Creates an animated GIF from a collection of Bitmap8 frames.  This is a pure C#
    /// port of the WPF AnimatedGifEncoder; it uses an internal GIF LZW encoder in place of
    /// the WPF-only GifBitmapEncoder.
    /// </summary>
    public class AnimatedGifEncoder {
        // GIF signature + version.
        private static readonly byte[] GIF89A_SIGNATURE = "GIF89a"u8.ToArray();

        private static readonly byte[] NetscapeExtStart =
        [
            UnpackedGif.EXTENSION_INTRODUCER,
            UnpackedGif.APP_EXTENSION_LABEL,
            0x0b,           // Block Size
            (byte)'N',      // Application Identifier (8 bytes)
            (byte)'E',
            (byte)'T',
            (byte)'S',
            (byte)'C',
            (byte)'A',
            (byte)'P',
            (byte)'E',
            (byte)'2',      // Appl. Authentication Code (3 bytes)
            (byte)'.',
            (byte)'0',
            0x03 // size of block
            // followed by loop flag, 2-byte repetition count, and $00 to terminate
        ];

        private static readonly byte[] GraphicControlStart =
        [
            UnpackedGif.EXTENSION_INTRODUCER,
            UnpackedGif.GRAPHIC_CONTROL_LABEL,
            0x04 // Block Size
            // followed by flags, 2-byte delay, transparency color index, and $00 to terminate
        ];

        /// <summary>
        /// Per-frame data.
        /// </summary>
        private class FrameEntry(Bitmap8 bitmap, int delayMsec)
        {
            public Bitmap8 Bitmap { get; } = bitmap;
            public int DelayMsec { get; } = delayMsec;
        }

        private readonly List<FrameEntry> mFrames = new List<FrameEntry>();

        public int Count => mFrames.Count;

        public void AddFrame(Bitmap8 bitmap, int delayMsec) {
            mFrames.Add(new FrameEntry(bitmap, delayMsec));
        }

        /// <summary>
        /// Converts the list of frames into an animated GIF, and writes it to the stream.
        /// </summary>
        /// <param name="stream">Output stream.</param>
        /// <param name="maxWidth">Width of largest frame, set on return.</param>
        /// <param name="maxHeight">Height of largest frame, set on return.</param>
        public void Save(Stream stream, out int maxWidth, out int maxHeight) {
            maxWidth = maxHeight = -1;

            if (mFrames.Count == 0) {
                Debug.Assert(false);
                return;
            }

            // Determine largest dimensions.
            foreach (FrameEntry fe in mFrames) {
                if (fe.Bitmap.Width > maxWidth)
                {
                    maxWidth = fe.Bitmap.Width;
                }

                if (fe.Bitmap.Height > maxHeight)
                {
                    maxHeight = fe.Bitmap.Height;
                }
            }

            // GIF89a header + logical screen descriptor.
            stream.Write(GIF89A_SIGNATURE, 0, GIF89A_SIGNATURE.Length);
            WriteLittleUshort(stream, (ushort)maxWidth);
            WriteLittleUshort(stream, (ushort)maxHeight);
            stream.WriteByte(0x70);         // no GCT; max color resolution
            stream.WriteByte(0);            // background color index
            stream.WriteByte(0);            // no pixel aspect ratio adjustment

            // Netscape looping extension.
            stream.Write(NetscapeExtStart, 0, NetscapeExtStart.Length);
            stream.WriteByte(1);            // sub-block ID: loop count follows
            WriteLittleUshort(stream, 0);   // loop forever
            stream.WriteByte(0);            // end of block

            byte disposalMethod =
                (byte)UnpackedGif.GraphicControlExtension.DisposalMethods.RestoreBackground;

            foreach (FrameEntry fe in mFrames) {
                Bitmap8 bmp = fe.Bitmap;
                int[] colors = bmp.GetColors()!;
                int numColors = bmp.NumColors;

                // Determine the GIF color table size field (N where actual count = 2^(N+1)).
                int colorTableSizeField = ComputeColorTableSizeField(numColors);
                int numTableEntries = 1 << (colorTableSizeField + 1);

                // Build RGB color table bytes.
                byte[] colorTable = new byte[numTableEntries * 3];
                for (int i = 0; i < numColors; i++) {
                    int argb = colors[i];
                    colorTable[i * 3]     = (byte)((argb >> 16) & 0xff); // R
                    colorTable[i * 3 + 1] = (byte)((argb >> 8) & 0xff);  // G
                    colorTable[i * 3 + 2] = (byte)(argb & 0xff);          // B
                }
                // Unused entries stay black (0,0,0).

                // LZW minimum code size.
                int lzwMinCodeSize = colorTableSizeField + 1;
                if (lzwMinCodeSize < 2)
                {
                    lzwMinCodeSize = 2;
                }

                // Graphic Control Extension.
                stream.Write(GraphicControlStart, 0, GraphicControlStart.Length);
                stream.WriteByte((byte)(disposalMethod << 2));  // no transparency, no user input
                WriteLittleUshort(stream, (ushort)Math.Round(fe.DelayMsec / 10.0));
                stream.WriteByte(0);            // transparent color index (unused)
                stream.WriteByte(0);            // end of GCE

                // Image descriptor.
                stream.WriteByte(UnpackedGif.IMAGE_SEPARATOR);
                WriteLittleUshort(stream, 0);                       // left
                WriteLittleUshort(stream, 0);                       // top
                WriteLittleUshort(stream, (ushort)bmp.Width);
                WriteLittleUshort(stream, (ushort)bmp.Height);
                // Local color table flag + size field; no sort / no interlace.
                stream.WriteByte((byte)(0x80 | colorTableSizeField));

                // Local color table.
                stream.Write(colorTable, 0, colorTable.Length);

                // LZW-compressed image data.
                EncodeLzwImageData(stream, bmp.GetPixels(), lzwMinCodeSize);
            }

            stream.WriteByte(UnpackedGif.GIF_TRAILER);
        }

        /// <summary>
        /// Computes the GIF color table size field value N such that 2^(N+1) >= numColors.
        /// N is in the range [0, 7].
        /// </summary>
        private static int ComputeColorTableSizeField(int numColors) {
            if (numColors <= 0)
            {
                numColors = 1;
            }

            int n = 0;
            while ((1 << (n + 1)) < numColors) {
                n++;
                if (n == 7)
                {
                    break;
                }
            }
            return n;
        }

        /// <summary>
        /// Encodes indexed pixel data using GIF LZW and writes it as GIF image data
        /// sub-blocks (including the LZW minimum code size byte and the terminating $00).
        /// </summary>
        private static void EncodeLzwImageData(Stream stream, byte[] pixels, int lzwMinCodeSize) {
            int clearCode = 1 << lzwMinCodeSize;
            int eoiCode = clearCode + 1;

            // Write the LZW minimum code size byte.
            stream.WriteByte((byte)lzwMinCodeSize);

            // Sub-block accumulation: each sub-block is at most 255 bytes.
            byte[] subBlock = new byte[255];
            int subBlockLen = 0;
            ulong bitBuffer = 0;
            int bitsInBuffer = 0;

            void WriteSubBlockIfFull() {
                if (subBlockLen == 255) {
                    stream.WriteByte(255);
                    stream.Write(subBlock, 0, 255);
                    subBlockLen = 0;
                }
            }

            void EmitCode(int code, int codeSize) {
                bitBuffer |= (ulong)code << bitsInBuffer;
                bitsInBuffer += codeSize;
                while (bitsInBuffer >= 8) {
                    subBlock[subBlockLen++] = (byte)(bitBuffer & 0xFF);
                    bitBuffer >>= 8;
                    bitsInBuffer -= 8;
                    WriteSubBlockIfFull();
                }
            }

            void FlushBitsAndTerminate() {
                // Flush any remaining bits.
                while (bitsInBuffer > 0) {
                    subBlock[subBlockLen++] = (byte)(bitBuffer & 0xFF);
                    bitBuffer >>= 8;
                    bitsInBuffer -= 8;
                    WriteSubBlockIfFull();
                }
                // Flush the last sub-block (might be partial).
                if (subBlockLen > 0) {
                    stream.WriteByte((byte)subBlockLen);
                    stream.Write(subBlock, 0, subBlockLen);
                    subBlockLen = 0;
                }
                // Block terminator.
                stream.WriteByte(0);
            }

            // GIF LZW encoding.
            // We use a dictionary keyed by (prefix_code << 8 | pixel) → code.
            // prefix_code is at most 4095 (12 bits), pixel is 0-255, so key fits in int.
            var codeTable = new Dictionary<int, int>(5000);

            int codeSize = lzwMinCodeSize + 1;
            int nextCode = eoiCode + 1;

            void ResetTable() {
                codeTable.Clear();
                codeSize = lzwMinCodeSize + 1;
                nextCode = eoiCode + 1;
            }

            // Emit initial clear code.
            EmitCode(clearCode, codeSize);

            if (pixels.Length == 0) {
                EmitCode(eoiCode, codeSize);
                FlushBitsAndTerminate();
                return;
            }

            int prefix = pixels[0];

            for (int i = 1; i < pixels.Length; i++) {
                int pixel = pixels[i];
                int key = (prefix << 8) | pixel;
                if (codeTable.TryGetValue(key, out int existingCode)) {
                    prefix = existingCode;
                } else {
                    EmitCode(prefix, codeSize);
                    if (nextCode < 4096) {
                        codeTable[key] = nextCode++;
                        // Increase code size when we've exceeded the current range.
                        if (nextCode > (1 << codeSize) && codeSize < 12) {
                            codeSize++;
                        }
                    } else {
                        // Table full: emit clear code and reset.
                        EmitCode(clearCode, codeSize);
                        ResetTable();
                    }
                    prefix = pixel;
                }
            }

            // Emit the last prefix code and end-of-information code.
            EmitCode(prefix, codeSize);
            EmitCode(eoiCode, codeSize);
            FlushBitsAndTerminate();
        }

        private static void WriteLittleUshort(Stream stream, ushort val) {
            stream.WriteByte((byte)val);
            stream.WriteByte((byte)(val >> 8));
        }
    }
}
