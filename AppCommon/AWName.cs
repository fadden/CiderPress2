/*
 * Copyright 2022 faddenSoft
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

namespace AppCommon {
    /// <summary>
    /// AppleWorks filename functions.
    /// </summary>
    public static class AWName {
        /// <summary>
        /// Adjusts the case of letters in filenames of AppleWorks files (AWP, ADB, ASP)
        /// according to the bits in the auxtype field.  For this conversion, lower-case
        /// periods ('.') are converted to spaces (' '), which means the new name may not
        /// compare successfully to the original with general string-matching algorithms.
        /// </summary>
        /// <remarks>
        /// <para>If this is not an AppleWorks file, the original filename is returned.</para>
        /// </remarks>
        /// <param name="fileName">Original ProDOS filename.  This may already be mixed-case,
        ///   based on the GS/OS ProDOS FST lower-case feature.  Must be valid ProDOS.</param>
        /// <param name="fileType">ProDOS file type.</param>
        /// <param name="auxType">ProDOS auxiliary type.</param>
        /// <returns>Adjusted filename.</returns>
        public static string ChangeAppleWorksCase(string fileName, byte fileType, ushort auxType) {
            if (fileType < 0x19 || fileType > 0x1b) {
                return fileName;        // not an AppleWorks type
            }

            ushort lcFlags = (ushort)((auxType << 8) | (auxType >> 8));     // swap bytes
            char[] newName = new char[fileName.Length];
            for (int i = 0; i < fileName.Length; i++) {
                if ((lcFlags & 0x8000) != 0) {
                    newName[i] = NameCharToLower(fileName[i]);
                } else {
                    newName[i] = NameCharToUpper(fileName[i]);
                }
                lcFlags <<= 1;
            }
            return new string(newName);
        }

        private static char NameCharToLower(char origChar) {
            if (origChar == '.') {
                return ' ';
            } else if (origChar >= 'A' && origChar <= 'Z') {
                return (char)(origChar - 'A' + 'a');
            } else {
                return origChar;
            }
        }

        private static char NameCharToUpper(char origChar) {
            if (origChar == ' ') {
                return '.';
            } else if (origChar >= 'a' && origChar <= 'z') {
                return (char)(origChar - 'a' + 'A');
            } else {
                return origChar;
            }
        }


        // This was originally part of the ProDOS_FileEntry class.
#if false
        // Regex patterns for filename validation.
        //
        // Both filenames and volume names are 1-15 characters, starting with a letter,
        // and may contain numbers and '.'.  To support AppleWorks lower-case naming conventions,
        // filenames may additionally include ' ', which is treated as a lower-case '.'.
        //
        // The AppleWorks pattern is a superset of the ProDOS pattern, and should be used
        // when the file type isn't known.
        private const string FILE_NAME_PATTERN = @"^[A-Za-z]([A-Za-z0-9\.]{0,14})$";
        private const string AW_FILE_NAME_PATTERN = @"^[A-Za-z]([A-Za-z0-9\. ]{0,14})$";
        private static Regex sFileNameRegex = new Regex(FILE_NAME_PATTERN);
        private static Regex sAWFileNameRegex = new Regex(AW_FILE_NAME_PATTERN);

        const int MAX_FILE_NAME_LEN = 15;

        /// <summary>
        /// Generates a mixed-case file or volume name given a string and flags.
        /// </summary>
        /// <remarks>
        /// ProDOS 8 v1.8 and later support lower-case filenames by repurposing the
        /// version and min_version fields.  AppleWorks has a similar feature that uses
        /// bits in the aux_type field.
        ///
        /// The AppleWorks flags allow filenames to have spaces in them, by treating ' ' as
        /// a lower-case '.', but the ProDOS flags do not.  In practice, GS/OS will ignore
        /// the flag, so there's no harm setting it.
        /// </remarks>
        /// <param name="rawName"></param>
        /// <param name="length"></param>
        /// <param name="lcFlags"></param>
        /// <param name="fromAppleWorks"></param>
        /// <param name="isValid"></param>
        /// <returns></returns>
        public static string GenerateMixedCaseName(byte[] rawName, int length, ushort lcFlags,
                bool fromAppleWorks, out bool isValid) {
            Debug.Assert(rawName.Length == MAX_FILE_NAME_LEN);
            Debug.Assert(length > 0 && length <= MAX_FILE_NAME_LEN);

            char[] outName = new char[length];

            if (fromAppleWorks && lcFlags != 0) {
                // Use lower-case flags supplied in auxtype, converting to ProDOS format
                // by swapping bytes, shifting right once, and setting the high bit.
                ushort awFlags = (ushort)((lcFlags << 8) | (lcFlags >> 8));
                lcFlags = (ushort)((awFlags >> 1) | 0x8000);
            }
            if ((lcFlags & 0x8000) != 0) {
                // Convert according to GS/OS tech note #8.
                for (int idx = 0; idx < length; idx++) {
                    lcFlags <<= 1;
                    if ((lcFlags & 0x8000) != 0) {
                        outName[idx] = NameCharToLower(rawName[idx], fromAppleWorks);
                    } else {
                        outName[idx] = (char)rawName[idx];
                    }
                }
            } else {
                // just copy it
                for (int idx = 0; idx < length; idx++) {
                    outName[idx] = (char)rawName[idx];
                }
            }

            string result = new string(outName);

            // Validate the converted name string.
            MatchCollection matches;
            if (fromAppleWorks) {
                matches = sAWFileNameRegex.Matches(result);
            } else {
                matches = sFileNameRegex.Matches(result);
            }
            if (matches.Count == 0) {
                Debug.WriteLine("Found invalid chars in filename '" + result + "'");
                isValid = false;
            } else {
                isValid = true;
            }
            return result;
        }

        /// <summary>
        /// Generates the "raw" upper-case-only ProDOS filename and a set of lower-case flags
        /// from a mixed-case filename.  The filename is not otherwise transformed.
        /// </summary>
        /// <remarks>
        /// The GS/OS FST supports lower-case filenames, and ProDOS v1.8+ handles them
        /// correctly.  AppleWorks files, regardless of storage medium, use the aux type
        /// field to hold a similar set of flags.  We generate both sets of flags here.
        /// </remarks>
        /// <param name="fileName">ProDOS-compatible filename (may have spaces).</param>
        /// <param name="olcFlags">Flags for ProDOS directory entries.</param>
        /// <param name="oawFlags">Flags for AppleWorks aux type field.</param>
        /// <returns>Raw filename, which has the same length as the original.</returns>
        public static byte[] GenerateRawName(string fileName, out int olcFlags, out int oawFlags) {
            Debug.Assert(fileName.Length > 1 && fileName.Length <= MAX_FILE_NAME_LEN);
            byte[] rawName = new byte[MAX_FILE_NAME_LEN];
            ushort lcFlags = 0;
            ushort awFlags = 0;
            ushort bit = 0x8000;

            for (int i = 0; i < fileName.Length; i++) {
                char ch = fileName[i];
                if (ch >= 'a' && ch <= 'z') {
                    // Lower case, set bit.
                    lcFlags |= bit;
                    awFlags |= bit;
                    ch = (char)(ch - 0x20);     // convert to upper case
                } else if (ch == ' ') {
                    // Lower case for AppleWorks, upper case for ProDOS.
                    awFlags |= bit;
                    ch = '.';
                }
                bit >>= 1;
                rawName[i] = (byte)ch;
            }

            if (lcFlags == 0) {
                // Entirely upper case.  Output zero, rather than $8000, for the benefit of
                // pre-V1.8 ProDOS.
                olcFlags = 0;
            } else {
                // Shift right and set the high bit.
                olcFlags = (lcFlags >> 1) | 0x8000;
            }

            // AppleWorks aux type flags are byte-swapped.
            oawFlags = (awFlags << 8) | (awFlags >> 8);

            return rawName;
        }
#endif
    }
}
