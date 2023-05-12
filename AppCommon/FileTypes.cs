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

using CommonUtil;
using DiskArc;


namespace AppCommon {
    /// <summary>
    /// File type utility functions.
    /// </summary>
    public static class FileTypes {
        /// <summary>
        /// Gets the 3-character ProDOS filetype abbreviation string.
        /// </summary>
        /// <param name="fileType">ProDOS file type, 0-255.</param>
        /// <returns>The abbreviation string, or an empty string if the file type value is out
        ///   of range.</returns>
        public static string GetFileTypeAbbrev(int fileType) {
            if (fileType >= 0 && fileType <= 255) {
                return sFileTypeNames[fileType];
            } else {
                return string.Empty;
            }
        }

        /// <summary>
        /// Finds a ProDOS file type by 3-letter abbreviation.  Two-letter strings will have a
        /// space appended (for "OS " and "WP ").
        /// </summary>
        /// <param name="abbrev">Two- or three-character file type abbreviation.</param>
        /// <returns>File type (0-255), or -1 on failure.</returns>
        public static int GetFileTypeByAbbrev(string abbrev) {
            if (abbrev.Length < 2 || abbrev.Length > 3) {
                return -1;
            }
            string modAbbrev = abbrev.ToUpper();
            if (modAbbrev.Length == 2) {
                modAbbrev += " ";
            }
            for (int i = 0; i < sFileTypeNames.Length; i++) {
                if (sFileTypeNames[i] == modAbbrev) {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>
        /// ProDOS file type names.  Must be 3 chars and upper case.
        /// </summary>
        private static readonly string[] sFileTypeNames = new string[] {
            "NON", "BAD", "PCD", "PTX", "TXT", "PDA", "BIN", "FNT",
            "FOT", "BA3", "DA3", "WPF", "SOS", "$0D", "$0E", "DIR",
            "RPD", "RPI", "AFD", "AFM", "AFR", "SCL", "PFS", "$17",
            "$18", "ADB", "AWP", "ASP", "$1C", "$1D", "$1E", "$1F",
            "TDM", "$21", "$22", "$23", "$24", "$25", "$26", "$27",
            "$28", "$29", "8SC", "8OB", "8IC", "8LD", "P8C", "$2F",
            "$30", "$31", "$32", "$33", "$34", "$35", "$36", "$37",
            "$38", "$39", "$3A", "$3B", "$3C", "$3D", "$3E", "$3F",
            "DIC", "OCR", "FTD", "$43", "$44", "$45", "$46", "$47",
            "$48", "$49", "$4A", "$4B", "$4C", "$4D", "$4E", "$4F",
            "GWP", "GSS", "GDB", "DRW", "GDP", "HMD", "EDU", "STN",
            "HLP", "COM", "CFG", "ANM", "MUM", "ENT", "DVU", "FIN",
            "$60", "$61", "$62", "$63", "$64", "$65", "$66", "$67",
            "$68", "$69", "$6A", "BIO", "$6C", "TDR", "PRE", "HDV",
            "$70", "$71", "$72", "$73", "$74", "$75", "$76", "$77",
            "$78", "$79", "$7A", "$7B", "$7C", "$7D", "$7E", "$7F",
            "$80", "$81", "$82", "$83", "$84", "$85", "$86", "$87",
            "$88", "$89", "$8A", "$8B", "$8C", "$8D", "$8E", "$8F",
            "$90", "$91", "$92", "$93", "$94", "$95", "$96", "$97",
            "$98", "$99", "$9A", "$9B", "$9C", "$9D", "$9E", "$9F",
            "WP ", "$A1", "$A2", "$A3", "$A4", "$A5", "$A6", "$A7",
            "$A8", "$A9", "$AA", "GSB", "TDF", "BDF", "$AE", "$AF",
            "SRC", "OBJ", "LIB", "S16", "RTL", "EXE", "PIF", "TIF",
            "NDA", "CDA", "TOL", "DVR", "LDF", "FST", "$BE", "DOC",
            "PNT", "PIC", "ANI", "PAL", "$C4", "OOG", "SCR", "CDV",
            "FON", "FND", "ICN", "$CB", "$CC", "$CD", "$CE", "$CF",
            "$D0", "$D1", "$D2", "$D3", "$D4", "MUS", "INS", "MDI",
            "SND", "$D9", "$DA", "DBM", "$DC", "DDD", "$DE", "$DF",
            "LBR", "$E1", "ATK", "$E3", "$E4", "$E5", "$E6", "$E7",
            "$E8", "$E9", "$EA", "$EB", "$EC", "$ED", "R16", "PAS",
            "CMD", "$F1", "$F2", "$F3", "$F4", "$F5", "$F6", "$F7",
            "$F8", "OS ", "INT", "IVR", "BAS", "VAR", "REL", "SYS"
        };
    }
}
