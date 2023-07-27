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
    /// Utility functions for use by file converters.
    /// </summary>
    public static class ConvUtil {
        /// <summary>
        /// Converts three 8-bit color values to a single 32-bit ARGB color value.
        /// </summary>
        /// <param name="r">Red.</param>
        /// <param name="g">Green.</param>
        /// <param name="b">Blue.</param>
        /// <returns>Combined value (0xFFrrggbb).</returns>
        public static int MakeRGB(int r, int g, int b) {
            CheckByte(r, nameof(r));
            CheckByte(g, nameof(g));
            CheckByte(b, nameof(b));
            return (0xff << 24) | (r << 16) | (g << 8) | b;
        }

        /// <summary>
        /// Converts four 8-bit color values to a single 32-bit ARGB color value.  Values should
        /// not be pre-multiplied.
        /// </summary>
        /// <remarks>
        /// <para>The API matches <see cref="System.Drawing.Color.FromArgb(int, int, int, int)"/>,
        /// but returns an integer instead of a Color.  Returning an unsigned value might make
        /// more sense, but the .NET Core API is pretty consistent with its love of Int32.</para>
        /// </remarks>
        /// <param name="a">Alpha channel.</param>
        /// <param name="r">Red.</param>
        /// <param name="g">Green.</param>
        /// <param name="b">Blue.</param>
        /// <returns>Combined value (0xaarrggbb).</returns>
        public static int MakeARGB(int a, int r, int g, int b) {
            CheckByte(a, nameof(a));
            CheckByte(r, nameof(r));
            CheckByte(g, nameof(g));
            CheckByte(b, nameof(b));
            return (a << 24) | (r << 16) | (g << 8) | b;
        }

        /// <summary>
        /// Converts four 8-bit color values to a single 32-bit ARGB value.  Values should
        /// not be pre-multiplied.
        /// </summary>
        /// <param name="a">Alpha channel.</param>
        /// <param name="r">Red.</param>
        /// <param name="g">Green.</param>
        /// <param name="b">Blue.</param>
        /// <returns>Combined value (0xaarrggbb).</returns>
        public static int MakeARGB(byte a, byte r, byte g, byte b) {
            return (a << 24) | (r << 16) | (g << 8) | b;
        }

        /// <summary>
        /// Checks that an integer argument can be safely cast to a byte.
        /// </summary>
        /// <param name="val">Value to check.</param>
        /// <param name="name">Argument name.</param>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
        internal static void CheckByte(int val, string name) {
            if (val < 0 || val > 255) {
                throw new ArgumentOutOfRangeException(name, "value " + val + " not in [0,255]");
            }
        }


        // Strings used in import/export specs.
        public const string CHAR_MODE_ASCII = "ascii";
        public const string CHAR_MODE_CP1252 = "1252";
        public const string CHAR_MODE_HIGH_ASCII = "hiascii";
        public const string CHAR_MODE_LATIN = "latin";
        public const string CHAR_MODE_MOR = "mor";
        public const string CHAR_MODE_UTF8 = "utf8";

        /// <summary>
        /// Character conversion mode.  This specifies the character encoding in the archive.
        /// </summary>
        public enum ExportCharSrc {
            Unknown = 0,
            HighASCII,      // ASCII chars, may or may not have high bits set (DOS, ProDOS, ...)
            MacOSRoman,     // Mac OS Roman 8-bit data (Mac, GS/OS).
            Latin,          // ISO-8859-1 8-bit data (unusual but not impossible)
        }

        public static readonly string[] ExportCharSrcTags = new string[] {
            CHAR_MODE_HIGH_ASCII, CHAR_MODE_MOR, CHAR_MODE_LATIN
        };
        public static readonly string[] ExportCharSrcDescrs = new string[] {
            "High/low ASCII", "Mac OS Roman", "ISO Latin-1"
        };

        /// <summary>
        /// Parses an ExportCharSrc value string.
        /// </summary>
        public static bool TryParseExportCharSrc(string modeStr, out ExportCharSrc mode) {
            mode = ExportCharSrc.Unknown;
            if (modeStr == CHAR_MODE_HIGH_ASCII) {
                mode = ExportCharSrc.HighASCII;
            } else if (modeStr == CHAR_MODE_MOR) {
                mode = ExportCharSrc.MacOSRoman;
            } else if (modeStr == CHAR_MODE_LATIN) {
                mode = ExportCharSrc.Latin;
            }
            return (mode != ExportCharSrc.Unknown);
        }

        /// <summary>
        /// Character conversion mode.  This specifies the character encoding in the host file.
        /// </summary>
        public enum ImportCharSrc {
            Unknown = 0,
            UTF8,           // UTF-8; also use this for plain ASCII files
            CP1252,         // Windows character encoding
            Latin,          // ISO-8859-1
        }

        public static readonly string[] ImportCharSrcTags = new string[] {
            CHAR_MODE_UTF8, CHAR_MODE_LATIN, CHAR_MODE_CP1252
        };
        public static readonly string[] ImportCharSrcDescrs = new string[] {
            "UTF-8", "ISO Latin-1", "Win CP1252"
        };

        /// <summary>
        /// Parses an ImportCharSrc value string.
        /// </summary>
        public static bool TryParseImportCharSrc(string modeStr, out ImportCharSrc mode) {
            mode = ImportCharSrc.Unknown;
            if (modeStr == CHAR_MODE_UTF8) {
                mode = ImportCharSrc.UTF8;
            } else if (modeStr == CHAR_MODE_LATIN) {
                mode = ImportCharSrc.Latin;
            } else if (modeStr == CHAR_MODE_CP1252) {
                mode = ImportCharSrc.CP1252;
            }
            return (mode != ImportCharSrc.Unknown);
        }

        /// <summary>
        /// Character conversion mode.  This specifies the character encoding that will be used
        /// when writing to the archive.
        /// </summary>
        public enum ImportCharDst {
            Unknown = 0,
            ASCII,
            HighASCII,
            MacOSRoman,
        }

        public static readonly string[] ImportCharDstTags = new string[] {
            CHAR_MODE_ASCII, CHAR_MODE_HIGH_ASCII, CHAR_MODE_MOR
        };
        public static readonly string[] ImportCharDstDescrs = new string[] {
            "ASCII", "High ASCII", "Mac OS Roman"
        };

        /// <summary>
        /// Parses an ImportCharDst value string.
        /// </summary>
        public static bool TryParseImportCharDst(string modeStr, out ImportCharDst mode) {
            mode = ImportCharDst.Unknown;
            if (modeStr == CHAR_MODE_ASCII) {
                mode = ImportCharDst.ASCII;
            } else if (modeStr == CHAR_MODE_HIGH_ASCII) {
                mode = ImportCharDst.HighASCII;
            } else if (modeStr == CHAR_MODE_MOR) {
                mode = ImportCharDst.MacOSRoman;
            }
            return (mode != ImportCharDst.Unknown);
        }

#if false
        /// <summary>
        /// Determines the best value for the ProDOS file type from the available data.  If no
        /// useful type information is present, returns $00/0000.
        /// </summary>
        /// <param name="entry">File entry.</param>
        /// <param name="fileType">Result: ProDOS file type.</param>
        /// <param name="auxType">Result: ProDOS aux type.</param>
        public static void GetBestProDOSTypes(IFileEntry entry, out byte fileType,
                out ushort auxType) {
            if (entry.HasProDOSTypes && (entry.FileType != 0 || entry.AuxType != 0)) {
                // Use the ProDOS types.
                fileType = entry.FileType;
                auxType = entry.AuxType;
                return;
            }
            if (entry.HasHFSTypes && FileTypes.ProDOSFromHFS(entry.HFSFileType, entry.HFSCreator,
                    out fileType, out auxType)) {
                // Use the ProDOS types stored in the HFS types.
                return;
            }

            // Nothing useful found.
            auxType = fileType = 0;
        }
#endif
    }
}
