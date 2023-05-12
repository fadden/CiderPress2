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

namespace FileConv {
    /// <summary>
    /// Color palette, for 8-bit indexed color images.
    /// </summary>
    public class Palette8 {
        private const int PALETTE_SIZE = 256;

        /// <summary>
        /// Human-readable identification label.
        /// </summary>
        public string Label { get; private set; }

        private int[] mPalette;
        private int mMaxColorIndex;

        /// <summary>
        /// Constructor.  Creates an empty palette.
        /// </summary>
        /// <param name="label">Identification label.</param>
        public Palette8(string label) {
            Label = label;
            mPalette = new int[PALETTE_SIZE];
            mMaxColorIndex = 0;
        }

        public Palette8(string label, int[] values) : this(label) {
            if (values.Length > PALETTE_SIZE) {
                throw new ArgumentException(nameof(values), "too many values");
            }
            for (int i = 0; i < values.Length; i++) {
                mPalette[i] = values[i];
            }
            mMaxColorIndex = values.Length;
        }

        /// <summary>
        /// Copy constructor.
        /// </summary>
        /// <param name="src">Source object.</param>
        public Palette8(Palette8 src) {
            Label = src.Label;
            mPalette = new int[PALETTE_SIZE];
            for (int i = 0; i < src.mMaxColorIndex; i++) {
                mPalette[i] = src.mPalette[i];
            }
            mMaxColorIndex = src.mMaxColorIndex;
        }

        /// <summary>
        /// Returns the index of the highest-numbered in-use entry.
        /// </summary>
        public int Count { get { return mMaxColorIndex; } }

        /// <summary>
        /// Gets the Nth entry in the color palette.
        /// </summary>
        /// <param name="index">Palette entry index.</param>
        /// <returns>32-bit ARGB color value.</returns>
        public int GetColor(int index) {
            if (index < 0 || index > 255) {
                throw new ArgumentOutOfRangeException(nameof(index),"0-255, max=" + mMaxColorIndex);
            }
            return mPalette[index];
        }

        /// <summary>
        /// Sets the Nth entry in the color palette.
        /// </summary>
        /// <remarks>
        /// The size of the color palette will expand to hold the largest index.  For best
        /// results, start with index 0 and count up, avoiding duplicates.
        /// </remarks>
        /// <param name="index">Palette index, 0-255.</param>
        /// <param name="a">Alpha value.</param>
        /// <param name="r">Red value.</param>
        /// <param name="g">Green value.</param>
        /// <param name="b">Blue value.</param>
        public void SetColor(int index, byte a, byte r, byte g, byte b) {
            if (index < 0 || index > 255) {
                throw new ArgumentOutOfRangeException(nameof(index), "0-255");
            }
            mPalette[index] = ConvUtil.MakeARGB(a, r, g, b);
            if (index >= mMaxColorIndex) {
                mMaxColorIndex = index + 1;
            }
        }

        public int[] GetPalette() {
            return mPalette;
        }

        #region Apple II Colors

        // The basic 16 colors, in lo-res order.
        public enum Apple2Colors {
            Black = 0, DeepRed, DarkBlue, Purple,
            DarkGreen, DarkGray, MediumBlue, LightBlue,
            Brown, Orange, LightGray, Pink,
            LightGreen, Yellow, Aquamarine, White
        }

        // Colors displayed on the IIgs RGB color monitor, for lo-res, hi-res, and double hi-res.
        private static readonly int[] Apple2RGB = {
            ConvUtil.MakeRGB(0x00, 0x00, 0x00),     // $0 black
            ConvUtil.MakeRGB(0xdd, 0x00, 0x33),     // $1 deep red / magenta
            ConvUtil.MakeRGB(0x00, 0x00, 0x99),     // $2 dark blue
            ConvUtil.MakeRGB(0xdd, 0x22, 0xdd),     // $3 purple / violet
            ConvUtil.MakeRGB(0x00, 0x77, 0x22),     // $4 dark green
            ConvUtil.MakeRGB(0x55, 0x55, 0x55),     // $5 gray 1 / dark gray
            ConvUtil.MakeRGB(0x22, 0x22, 0xff),     // $6 medium blue
            ConvUtil.MakeRGB(0x66, 0xaa, 0xff),     // $7 light blue
            ConvUtil.MakeRGB(0x88, 0x55, 0x00),     // $8 brown
            ConvUtil.MakeRGB(0xff, 0x66, 0x00),     // $9 orange
            ConvUtil.MakeRGB(0xaa, 0xaa, 0xaa),     // $a gray 2 / light gray
            ConvUtil.MakeRGB(0xff, 0x99, 0x88),     // $b pink
            ConvUtil.MakeRGB(0x11, 0xdd, 0x00),     // $c light green
            ConvUtil.MakeRGB(0xff, 0xff, 0x00),     // $d yellow
            ConvUtil.MakeRGB(0x44, 0xff, 0x99),     // $e aquamarine
            ConvUtil.MakeRGB(0xff, 0xff, 0xff),     // $f white
        };

        /// <summary>
        /// Color palette for the 8 colors shown on the hi-res screen.
        /// </summary>
        public static Palette8 Palette_HiRes = new Palette8("Hi-Res (RGB)",
            new int[] {
                Apple2RGB[(int)Apple2Colors.Black],
                Apple2RGB[(int)Apple2Colors.LightGreen],
                Apple2RGB[(int)Apple2Colors.Purple],
                Apple2RGB[(int)Apple2Colors.White],
                Apple2RGB[(int)Apple2Colors.Black],
                Apple2RGB[(int)Apple2Colors.Orange],
                Apple2RGB[(int)Apple2Colors.MediumBlue],
                Apple2RGB[(int)Apple2Colors.White],
            });

        /// <summary>
        /// Color palette for the 16 colors shown on the double hi-res screen.
        /// </summary>
        public static Palette8 Palette_DoubleHiRes = new Palette8("Double Hi-Res (RGB)",
            new int[] {
                Apple2RGB[(int)Apple2Colors.Black],
                Apple2RGB[(int)Apple2Colors.DeepRed],
                Apple2RGB[(int)Apple2Colors.Brown],
                Apple2RGB[(int)Apple2Colors.Orange],
                Apple2RGB[(int)Apple2Colors.DarkGreen],
                Apple2RGB[(int)Apple2Colors.DarkGray],
                Apple2RGB[(int)Apple2Colors.LightGreen],
                Apple2RGB[(int)Apple2Colors.Yellow],
                Apple2RGB[(int)Apple2Colors.DarkBlue],
                Apple2RGB[(int)Apple2Colors.Purple],
                Apple2RGB[(int)Apple2Colors.LightGray],
                Apple2RGB[(int)Apple2Colors.Pink],
                Apple2RGB[(int)Apple2Colors.MediumBlue],
                Apple2RGB[(int)Apple2Colors.LightBlue],
                Apple2RGB[(int)Apple2Colors.Aquamarine],
                Apple2RGB[(int)Apple2Colors.White],
            });

        #endregion Apple II Colors
    }
}
