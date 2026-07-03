/*
 * Copyright 2023 faddenSoft
 * Copyright 2026 Lydian Scale Software
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
using System.Runtime.InteropServices;

using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace cp2_avalonia.Common {
    /// <summary>
    /// Bitmap conversion utilities for Avalonia.
    /// </summary>
    public static class BitmapUtil {
        /// <summary>
        /// Converts a FileConv IBitmap to an Avalonia WriteableBitmap.
        /// </summary>
        /// <param name="src">Source bitmap (indexed-8 or 32-bit direct color).</param>
        /// <returns>Newly-allocated WriteableBitmap in Bgra8888 format.</returns>
        public static WriteableBitmap ConvertToBitmap(FileConv.IBitmap src) {
            int width = src.Width;
            int height = src.Height;
            byte[] srcPixels = src.GetPixels();

            var wb = new WriteableBitmap(
                new PixelSize(width, height),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Unpremul);

            using var fb = wb.Lock();
            int rowBytes = fb.RowBytes;
            IntPtr dest = fb.Address;

            if (src.IsIndexed8) {
                // Expand palette: palette entries are ARGB (A in high byte).
                int[] palette = src.GetColors()!;
                byte[] bgra = ExpandIndexed(srcPixels, palette, width, height);
                CopyRowByRow(bgra, width, height, dest, rowBytes);
            } else {
                // Direct color: GetPixels() returns bytes in BGRA order (little-endian ARGB).
                CopyRowByRow(srcPixels, width, height, dest, rowBytes);
            }

            return wb;
        }

        /// <summary>
        /// Expands an indexed-8 pixel array to 32-bit BGRA using the supplied palette.
        /// </summary>
        private static byte[] ExpandIndexed(byte[] indices, int[] palette, int width, int height) {
            var result = new byte[width * height * 4];
            var dst = 0;
            foreach (var t in indices)
            {
                var argb = palette[t];
                var a = (byte)(argb >> 24);
                var r = (byte)(argb >> 16);
                var g = (byte)(argb >> 8);
                var b = (byte)argb;
                // BGRA order for PixelFormat.Bgra8888
                result[dst++] = b;
                result[dst++] = g;
                result[dst++] = r;
                result[dst++] = a == 0 ? (byte)255 : a;   // 0 alpha means fully opaque here
            }
            return result;
        }

        /// <summary>
        /// Copies 32-bit BGRA pixel data row by row into an ILockedFramebuffer, respecting
        /// the framebuffer's RowBytes stride.
        /// </summary>
        private static void CopyRowByRow(byte[] src, int width, int height,
                IntPtr dest, int rowBytes) {
            int srcStride = width * 4;
            for (int row = 0; row < height; row++) {
                int srcOffset = row * srcStride;
                IntPtr destRow = dest + row * rowBytes;
                Marshal.Copy(src, srcOffset, destRow, srcStride);
            }
        }
    }
}
