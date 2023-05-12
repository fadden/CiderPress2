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

using CommonUtil;

namespace FileConv {
    /// <summary>
    /// 32-bit direct color bitmap.
    /// </summary>
    /// <remarks>
    /// <para>New bitmaps are filled with color 0 (transparent black).</para>
    /// </remarks>
    public class Bitmap32 : IBitmap {
        public const int MAX_DIMENSION = 4096;

        //
        // IBitmap properties.
        //
        public Notes Notes { get; } = new Notes();
        public int Width { get; private set; }
        public int Height { get; private set; }
        public bool IsIndexed8 => false;
        public bool IsDoubled { get; set; }

        // Pixel data, in BGRA order.
        private byte[] mPixelData;
        private const int PIXEL_FORMAT_WIDTH = 4;

        /// <summary>
        /// Constructor.  Zero-dimensional bitmaps not currently allowed.
        /// </summary>
        /// <param name="width">Bitmap width.</param>
        /// <param name="height">Bitmap height.</param>
        public Bitmap32(int width, int height) {
            if (width <= 0 || width > MAX_DIMENSION || height <= 0 || height > MAX_DIMENSION) {
                throw new ArgumentException("Bad bitmap width/height " + width + "," + height);
            }
            Width = width;
            Height = height;

            mPixelData = new byte[width * PIXEL_FORMAT_WIDTH * height];
        }

        // IBitmap
        public int[]? GetColors() {
            return null;
        }

        // IBitmap
        public byte[] GetPixels() {
            return mPixelData;
        }

        /// <summary>
        /// Gets the color for a single pixel.
        /// </summary>
        /// <param name="x">X coordinate.</param>
        /// <param name="y">Y coordinate.</param>
        /// <returns>ARGB color value.</returns>
        public int GetPixel(int x, int y) {
            if (x < 0 || x >= Width) {
                throw new ArgumentOutOfRangeException(nameof(x), "x=" + x + " width=" + Width);
            }
            if (y < 0 || y >= Height) {
                throw new ArgumentOutOfRangeException(nameof(y), "y=" + y + " height=" + Height);
            }

            int offset = (x + y * Width) * PIXEL_FORMAT_WIDTH;
            int value = mPixelData[offset] | (mPixelData[offset + 1] << 8) |
                (mPixelData[offset + 2] << 16) | (mPixelData[offset + 3] << 24);
            return value;
        }

        /// <summary>
        /// Sets the color for a single pixel.
        /// </summary>
        /// <param name="x">X coordinate.</param>
        /// <param name="y">Y coordinate.</param>
        /// <param name="argb">Color value.</param>
        public void SetPixel(int x, int y, int argb) {
            if (x < 0 || x >= Width) {
                throw new ArgumentOutOfRangeException(nameof(x), "x=" + x + " width=" + Width);
            }
            if (y < 0 || y >= Height) {
                throw new ArgumentOutOfRangeException(nameof(y), "y=" + y + " height=" + Height);
            }

            int offset = (x + y * Width) * PIXEL_FORMAT_WIDTH;
            mPixelData[offset] = (byte)argb;                // blue
            mPixelData[offset + 1] = (byte)(argb >> 8);     // green
            mPixelData[offset + 2] = (byte)(argb >> 16);    // red
            mPixelData[offset + 3] = (byte)(argb >> 24);    // alpha

            //mPixelData[offset + 3] = 0x7f;      // DEBUG alpha test
        }

        public override string ToString() {
            return "[Bitmap32: " + Width + "x" + Height + "]";
        }
    }
}
