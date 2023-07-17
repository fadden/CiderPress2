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
using System.Text;

namespace CommonUtil {
    /// <summary>
    /// String-to-value converters for some common and less-common situations.
    /// </summary>
    public static class StringToValue {
        /// <summary>
        /// <para>Parses an integer in a variety of formats (hex, decimal, binary).  We allow
        /// hex to be identified with a leading '$' as well as "0x".</para>
        /// <para>Trim whitespace before calling here.</para>
        /// </summary>
        /// <param name="str">String to parse.</param>
        /// <param name="val">Integer value of string.</param>
        /// <param name="intBase">What base the string was in (2, 10, or 16).</param>
        /// <returns>True if the parsing was successful.</returns>
        public static bool TryParseInt(string str, out int val, out int intBase) {
            if (string.IsNullOrEmpty(str)) {
                val = intBase = 0;
                return false;
            }

            if (str[0] == '$') {
                intBase = 16;
                str = str.Substring(1);     // convert functions don't like '$'
            } else if (str.Length > 2 && str[0] == '0' && (str[1] == 'x' || str[1] == 'X')) {
                intBase = 16;
            } else if (str[0] == '%') {
                intBase = 2;
                str = str.Substring(1);     // convert functions don't like '%'
            } else {
                intBase = 10;               // try it as decimal
            }

            try {
                val = Convert.ToInt32(str, intBase);
            } catch {
                val = 0;
                return false;
            }

            return true;
        }

        /// <summary>
        /// Converts a size descriptor, like "10KB", to a byte value, like "10240".
        /// </summary>
        /// <param name="sizeStr">String to convert.</param>
        /// <param name="baseIsBlocks">If true, lack of a suffix means blocks.</param>
        /// <returns>Value, in bytes, or -1 if the conversion failed.</returns>
        public static long SizeToBytes(string sizeStr, bool baseIsBlocks = false) {
            return SizeToBytes(sizeStr, 0, baseIsBlocks);
        }

        /// <summary>
        /// Converts a size descriptor, like "10KB", to a byte value, like "10240".
        /// </summary>
        /// <param name="sizeStr">String to convert.</param>
        /// <param name="trackSize">Value to use for units of "tracks".  If the value is zero,
        ///   track specifiers are rejected.</param>
        /// <param name="baseIsBlocks">If true, lack of a suffix means blocks.</param>
        /// <returns>Value, in bytes, or -1 if the conversion failed.</returns>
        public static long SizeToBytes(string sizeStr, int trackSize, bool baseIsBlocks = false) {
            const int INVALID_ARGS = -1;

            if (trackSize < 0) {
                throw new ArgumentOutOfRangeException(nameof(trackSize), "Invalid track size");
            }

            int nonIndex;
            for (nonIndex = 0; nonIndex < sizeStr.Length; nonIndex++) {
                if (sizeStr[nonIndex] < '0' || sizeStr[nonIndex] > '9') {
                    break;
                }
            }
            if (nonIndex == 0) {
                // Empty string, or no digits.
                return INVALID_ARGS;
            }
            string digits = sizeStr.Substring(0, nonIndex);
            string suffix = sizeStr.Substring(nonIndex).Trim();
            if (!int.TryParse(digits, out int num) || num < 0) {
                // Invalid number.
                return INVALID_ARGS;
            }

            // Supported conversions:
            // - bytes (no suffix)
            // - kibibytes (1024 bytes) -- "K", "KB", "KiB"
            // - mebibytes (1024*1024 bytes) -- "M", "MB", "MiB"
            // - gibibytes (1024*1024*1024 bytes) -- "G", "GB", "GiB"
            // - tebibytes (1024*1024*1024*1024 bytes) -- "T", "TB", "TiB"
            // - blocks (512 bytes) -- "BLK", "BLOCKS"
            // - tracks (trackSize bytes) - "TRK", "TRACKS"
            //
            // The value must be in decimal.  The suffix is case-insensitive.  Whitespace
            // before and after the unit multiplier is ignored.
            //
            // In theory, "K" and "KB" should be kilobytes (1000), and only "KiB" should be
            // kibibytes (1024).  Ditto for the other SI prefixes.
            //
            // We trim whitespace off the start to allow "800 KB" and "800KB".

            long mult = baseIsBlocks ? 512 : 1;
            switch (suffix.TrimStart().ToLowerInvariant()) {
                case "":            // bytes
                    break;
                case "k":
                case "kb":
                case "kib":
                    mult = 1024;
                    break;
                case "m":
                case "mb":
                case "mib":
                    mult = 1024 * 1024;
                    break;
                case "g":
                case "gb":
                case "gib":
                    mult = 1024 * 1024 * 1024;
                    break;
                case "t":
                case "tb":
                case "tib":
                    mult = 1024L * 1024 * 1024 * 1024;
                    break;
                case "blk":
                case "blocks":
                    mult = 512;
                    break;
                case "trk":
                case "tracks":
                    if (trackSize == 0) {
                        return INVALID_ARGS;
                    }
                    mult = trackSize;
                    break;
                default:
                    return INVALID_ARGS;
            }

            return num * mult;
        }

        /// <summary>
        /// Serializes an integer array into a string.
        /// </summary>
        /// <param name="values">Array to serialize.</param>
        /// <returns>Serialized data.</returns>
        public static string SerializeIntArray(int[] values) {
            StringBuilder sb = new StringBuilder(64);
            sb.Append("int[]");
            for (int i = 0; i < values.Length; i++) {
                sb.Append(',');
                sb.Append(values[i]);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Deserializes an integer array from a string.  Throws an exception if the format
        /// is incorrect.
        /// </summary>
        /// <param name="cereal">Serialized data.</param>
        /// <returns>Integer array with contents.</returns>
        public static int[] DeserializeIntArray(string cereal) {
            string[] splitted = cereal.Split(',');
            if (splitted.Length == 0) {
                throw new Exception("Bad serialized int[]");
            }
            if (splitted[0] != "int[]") {
                throw new Exception("Bad serialized int[], started with " + splitted[0]);
            }
            int[] arr = new int[splitted.Length - 1];
            try {
                for (int i = 1; i < splitted.Length; i++) {
                    arr[i - 1] = int.Parse(splitted[i]);
                }
            } catch (Exception ex) {
                throw new Exception("Bad serialized int[]: " + ex.Message);
            }
            return arr;
        }
    }
}
