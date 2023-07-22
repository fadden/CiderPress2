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
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Media;

using FileConv;

namespace cp2_wpf {
    public static class WinUtil {
        public const string FILE_FILTER_ALL = "All files|*.*";
        public const string FILE_FILTER_KNOWN = "Known formats|" +
            "*.shk;*.sdk;*.sea;*.bny;*.bqy;*.bxy;*.bse;" +
            "*.dsk;*.po;*.do;*.d13;*.2mg;*.img;*.iso;*.hdv;*.dc;*.dc6;*.image;*.ddd;" +
            "*.nib;*.nb2;*.raw;*.app;*.woz;" +
            "*.gz;*.zip;*.as;*.acu";
        public const string FILE_FILTER_ALL_DISK = "Disk image formats|" +
            "*.sdk;*.dsk;*.po;*.do;*.d13;*.2mg;*.img;*.iso;*.hdv;*.dc;*.dc6;*.image;*.ddd;" +
            "*.nib;*.nb2;*.raw;*.app;*.woz;";
        public const string FILE_FILTER_2MG = "2IMG disk image (*.2mg)|*.2mg";
        public const string FILE_FILTER_APP = "Trackstar disk image (*.app)|*.app";
        public const string FILE_FILTER_BINARY2 = "Binary ][ (*.bny/bqy)|*.bny;*.bqy";
        public const string FILE_FILTER_CSV = "Comma-separated values (*.csv)|*.csv";
        public const string FILE_FILTER_D13 = "Unadorned 13-sector disk (*.d13)|*.d13";
        public const string FILE_FILTER_DO = "Unadorned DOS sectors (*.do)|*.do";
        public const string FILE_FILTER_DC42 = "DiskCopy 4.2 image (*.image)|*.image";
        public const string FILE_FILTER_NIB = "Nibble disk image (*.nib)|*.nib";
        public const string FILE_FILTER_NUFX = "ShrinkIt (NuFX) archive (*.shk)|*.shk";
        public const string FILE_FILTER_PNG = "PNG images (*.png)|*.png";
        public const string FILE_FILTER_PO = "Unadorned ProDOS blocks (*.po)|*.po";
        public const string FILE_FILTER_RTF = "RTF documents (*.rtf)|*.rtf";
        public const string FILE_FILTER_SDK = "ShrinkIt disk image (*.sdk)|*.sdk";
        public const string FILE_FILTER_TEXT = "Text documents (*.txt)|*.txt";
        public const string FILE_FILTER_WOZ = "WOZ disk image (*.woz)|*.woz";
        public const string FILE_FILTER_ZIP = "ZIP archive (*.zip)|*.zip";

        /// <summary>
        /// Asks the user to select a file to open.
        /// </summary>
        /// <remarks>
        /// <para>I'd like to show the "read only" checkbox here, but it's broken:
        /// <see href="https://github.com/dotnet/wpf/issues/6346">
        /// "OpenFileDialog.ShowReadOnly does not work"</see></para>
        /// </remarks>
        /// <returns>Full pathname of selected file, or the empty string if the operation
        ///   was cancelled.</returns>
        public static string AskFileToOpen() {
            OpenFileDialog fileDlg = new OpenFileDialog() {
                Filter = FILE_FILTER_KNOWN + "|" + FILE_FILTER_ALL,
                FilterIndex = 1
            };
            if (fileDlg.ShowDialog() != true) {
                return string.Empty;
            }
            return Path.GetFullPath(fileDlg.FileName);
        }

        /// <summary>
        /// Converts an IBitmap to a BitmapSource for display.  The bitmap will be
        /// the same size as the original content.
        /// </summary>
        public static BitmapSource ConvertToBitmapSource(IBitmap bitmap) {
            if (bitmap is Bitmap8) {
                // Create indexed color palette.
                int[] intPal = bitmap.GetColors()!;
                List<Color> colors = new List<Color>(intPal.Length);
                foreach (int argb in intPal) {
                    Color col = Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16),
                        (byte)(argb >> 8), (byte)argb);
                    colors.Add(col);
                }
                BitmapPalette palette = new BitmapPalette(colors);

                // indexed-color; see https://stackoverflow.com/a/15272528/294248 for direct color
                BitmapSource image = BitmapSource.Create(
                    bitmap.Width,
                    bitmap.Height,
                    96.0,
                    96.0,
                    PixelFormats.Indexed8,
                    palette,
                    bitmap.GetPixels(),
                    bitmap.Width);
                image.Freeze();

                return image;
            } else {
                // Direct color.
                PixelFormat pixelFormat = PixelFormats.Bgra32;
                int stride = (bitmap.Width * pixelFormat.BitsPerPixel + 7) / 8;
                BitmapSource image = BitmapSource.Create(
                    bitmap.Width,
                    bitmap.Height,
                    96.0,
                    96.0,
                    pixelFormat,
                    null,
                    bitmap.GetPixels(),
                    stride);
                image.Freeze();

                return image;
            }
        }

        /// <summary>
        /// Finds the directory where runtime data files, such as application settings, may be
        /// found.  Normally this will be in the same directory as the executable, but during
        /// development it's at the root of the source tree (below the project directory).
        /// </summary>
        /// <returns>Directory, or an empty string if unable to find.</returns>
        public static string GetRuntimeDataDir() {
            // Normal: "C:\Src\CiderPress2\CiderPress2.dll"
            // Debugger: "C:\Src\CiderPress2\cp2_wpf\bin\Debug\net6.0-windows\CiderPress2.dll"
            string? exeName = typeof(AboutBox).Assembly.Location;
            string? baseDir = Path.GetDirectoryName(exeName);
            if (string.IsNullOrEmpty(baseDir)) {
                return string.Empty;
            }
            if (baseDir.Contains(@"\bin\Debug\") || baseDir.Contains(@"\bin\Release\")) {
                // Remove "cp2_wpf\bin\Debug\net6.0-windows" (four levels).
                baseDir = Path.GetDirectoryName(baseDir);
                baseDir = Path.GetDirectoryName(baseDir);
                baseDir = Path.GetDirectoryName(baseDir);
                baseDir = Path.GetDirectoryName(baseDir);
            }
            if (baseDir == null) {
                return string.Empty;
            }
            return baseDir;
        }
    }
}
