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
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

using AppCommon;
using CommonUtil;

namespace cp2_wpf {
    /// <summary>
    /// Extended attribute information added to the clipboard for the benefit of drop/paste
    /// operations that terminate in a second copy of CiderPress II.
    /// </summary>
    [Serializable]
    internal class ClipInfo {
        public const string CLIPBOARD_FORMAT_NAME = "faddenSoft:CiderPressII:v1";
        public static short CLIPBOARD_FORMAT = RegisterFormat();

        public ClipFileSet? ClipSet { get; set; } = null;

        public int AppVersionMajor { get; set; }
        public int AppVersionMinor { get; set; }
        public int AppVersionPatch { get; set; }

        public ClipInfo() { }
        public ClipInfo(ClipFileSet clipSet, CommonUtil.Version appVersion) {
            ClipSet = clipSet;
            AppVersionMajor = appVersion.Major;
            AppVersionMinor = appVersion.Minor;
            AppVersionPatch = appVersion.Patch;
        }

        /// <summary>
        /// Registers our clipboard format with the system.
        /// </summary>
        /// <returns>Clipboard format index.</returns>
        private static short RegisterFormat() {
            // Return value should be 0xc000-0xffff.
            uint fmtNum = RegisterClipboardFormat(CLIPBOARD_FORMAT_NAME);
            if (fmtNum == 0) {
                // TODO: if this is likely to occur, we should handle it better
                throw new Exception("Unable to register clipboard format '" +
                    CLIPBOARD_FORMAT_NAME + "', error=" + Marshal.GetLastWin32Error());
            }
            Debug.Assert(fmtNum == (ushort)fmtNum);
            Debug.WriteLine("Registered clipboard format 0x" + fmtNum.ToString("x4"));
            return (short)fmtNum;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint RegisterClipboardFormat(string lpszFormat);
    }
}
