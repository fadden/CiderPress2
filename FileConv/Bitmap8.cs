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
    /// 8-bit indexed color bitmap.
    /// </summary>
    /// <remarks>
    /// <para>New bitmaps are filled with color index 0.</para>
    /// </remarks>
    public class Bitmap8 : IBitmap {
        public const int MAX_DIMENSION = 4096;

        //
        // IBitmap properties.
        //
        public Notes Notes { get; } = new Notes();
        public int Width { get; private set; }
        public int Height { get; private set; }
        public bool IsIndexed8 => true;
        public bool IsDoubled { get; set; }

        private byte[] mPixelData;
        private Palette8 mPalette;

        /// <summary>
        /// Constructor.  Zero-dimensional bitmaps not currently allowed.
        /// </summary>
        /// <param name="width">Bitmap width.</param>
        /// <param name="height">Bitmap height.</param>
        public Bitmap8(int width, int height) {
            if (width <= 0 || width > MAX_DIMENSION || height <= 0 || height > MAX_DIMENSION) {
                throw new ArgumentException("Bad bitmap width/height " + width + "," + height);
            }
            Width = width;
            Height = height;

            // Using stride == width.
            mPixelData = new byte[width * height];
            mPalette = new Palette8("Default");
        }

        // IBitmap
        public int[]? GetColors() {
            int[] palette = mPalette.GetPalette();
            int[] colors = new int[mPalette.Count];
            for (int i = 0; i < colors.Length; i++) {
                colors[i] = palette[i];
            }
            return colors;
        }

        public int NumColors { get { return mPalette.Count; } }

        /// <summary>
        /// Sets the indexed color palette.
        /// </summary>
        /// <param name="palette">Source palette; this will make a copy.</param>
        public void SetPalette(Palette8 palette) {
            mPalette = new Palette8(palette);
        }

        // IBitmap
        public byte[] GetPixels() {
            return mPixelData;
        }

        /// <summary>
        /// Gets the color index for a single pixel.
        /// </summary>
        /// <param name="x">X coordinate.</param>
        /// <param name="y">Y coordinate.</param>
        /// <returns>Color index.</returns>
        public byte GetPixelIndex(int x, int y) {
            return mPixelData[x + y * Width];
        }

        /// <summary>
        /// Sets the color for a single pixel.
        /// </summary>
        /// <param name="x">X coordinate.</param>
        /// <param name="y">Y coordinate.</param>
        /// <param name="colorIndex">Color index.</param>
        public void SetPixelIndex(int x, int y, byte colorIndex) {
            if (x < 0 || x >= Width) {
                throw new ArgumentOutOfRangeException(nameof(x), "x=" + x + " width=" + Width);
            }
            if (y < 0 || y >= Height) {
                throw new ArgumentOutOfRangeException(nameof(y), "y=" + y + " height=" + Height);
            }
            if (colorIndex < 0 || colorIndex >= mPalette.Count) {
                throw new ArgumentOutOfRangeException(nameof(colorIndex),
                    "index=" + colorIndex + " max=" + mPalette.Count);
            }
            mPixelData[x + y * Width] = colorIndex;
        }

        /// <summary>
        /// Fills a rectangular area with a color.
        /// </summary>
        /// <param name="left">Left edge of rect.</param>
        /// <param name="top">Top edge of rect.</param>
        /// <param name="width">Width of rect.</param>
        /// <param name="height">Height of rect.</param>
        /// <param name="colorIndex">Color index.</param>
        public void FillRect(int left, int top, int width, int height, byte colorIndex) {
            for (int row = top; row < top + height; row++) {
                for (int col = left; col < left + width; col++) {
                    mPixelData[col + row * Width] = colorIndex;
                }
            }
        }

        /// <summary>
        /// Fills the entire bitmap with a solid color.
        /// </summary>
        /// <remarks>
        /// <para>Normally you'd skip this step by defining your desired background color as
        /// color entry zero.</para>
        /// </remarks>
        /// <param name="colorIndex">Color index.</param>
        public void SetAllPixels(byte colorIndex) {
            for (int i = 0; i < mPixelData.Length; i++) {
                mPixelData[i] = colorIndex;
            }
        }

        /// <summary>
        /// Draws an 8x8 character cell on the bitmap.
        /// </summary>
        /// <param name="ch">Character to draw.</param>
        /// <param name="xc">X coord of upper-left pixel.</param>
        /// <param name="yc">Y coord of upper-left pixel.</param>
        /// <param name="foreColor">Foreground color index.</param>
        /// <param name="backColor">Background color index.</param>
        public void DrawChar(char ch, int xc, int yc, byte foreColor, byte backColor) {
            int origXc = xc;
            int[] charBits = Font8x8.GetBitData(ch);
            for (int row = 0; row < 8; row++) {
                int rowBits = charBits[row];
                for (int col = 7; col >= 0; col--) {
                    if ((rowBits & (1 << col)) != 0) {
                        SetPixelIndex(xc, yc, foreColor);
                    } else {
                        SetPixelIndex(xc, yc, backColor);
                    }
                    xc++;
                }

                xc = origXc;
                yc++;
            }
        }

        public override string ToString() {
            return "[Bitmap8: " + Width + "x" + Height + ", " + mPalette.Count + " colors]";
        }
    }
}
