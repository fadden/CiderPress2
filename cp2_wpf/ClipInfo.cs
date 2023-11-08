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
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;

using AppCommon;

namespace cp2_wpf {
    /// <summary>
    /// Extended attribute information added to the clipboard for the benefit of drag/copy
    /// operations that originate and terminate in this application.
    /// </summary>
    [Serializable]
    internal class ClipInfo {
        public const string XFER_METADATA_NAME = "faddenSoft:CiderPressII:md-v1";
        public static short XFER_METADATA = RegisterFormat(XFER_METADATA_NAME);
        public const string XFER_STREAMS_NAME = "faddenSoft:CiderPressII:st-v1";
        public static short XFER_STREAMS = RegisterFormat(XFER_STREAMS_NAME);

        /// <summary>
        /// List of ClipFileEntry objects.  We receive one of these for every fork of every
        /// file on the clipboard.
        /// </summary>
        public List<ClipFileEntry>? ClipEntries { get; set; } = null;

        // Fields split out of a CommonUtil.Version object.
        public int AppVersionMajor { get; set; }
        public int AppVersionMinor { get; set; }
        public int AppVersionPatch { get; set; }

        public int ProcessId { get; set; }


        /// <summary>
        /// Nullary constructor, for the deserializer.
        /// </summary>
        public ClipInfo() { }

        /// <summary>
        /// Constructor.  Most of the goodies are in the ClipFileSet, but we want to add some
        /// application-level stuff like the version number.
        /// </summary>
        public ClipInfo(List<ClipFileEntry> clipEntries, CommonUtil.Version appVersion) {
            ClipEntries = clipEntries;
            AppVersionMajor = appVersion.Major;
            AppVersionMinor = appVersion.Minor;
            AppVersionPatch = appVersion.Patch;
            ProcessId = Process.GetCurrentProcess().Id;
        }

        /// <summary>
        /// Returns true if we find our metadata and streams on the clipboard or in a drop.
        /// </summary>
        /// <param name="dropObject">Data object from drop, or null to check clipboard.</param>
        public static bool IsDataFromCP2(IDataObject? dropObject) {
            IDataObject dataObj = (dropObject == null) ? Clipboard.GetDataObject() : dropObject;
            object? metaData = dataObj.GetData(XFER_METADATA_NAME);
            if (!(metaData is MemoryStream)) {
                return false;
            }
            //object? streamData = dataObj.GetData(XFER_STREAMS_NAME);
            //if (streamData is null) {
            //    return false;
            //}
            return true;
        }

        /// <summary>
        /// Extracts and deserializes the ClipInfo contents from the data object.
        /// </summary>
        /// <param name="dropObject">Data object from drop, or null to check clipboard.</param>
        /// <returns>ClipInfo object, or null if it couldn't be found.</returns>
        public static ClipInfo? GetClipInfo(IDataObject? dropObject) {
            IDataObject dataObj = (dropObject == null) ? Clipboard.GetDataObject() : dropObject;
            object? metaData = dataObj.GetData(XFER_METADATA_NAME);
            if (metaData is null) {
                Debug.WriteLine("Didn't find " + XFER_METADATA_NAME);
                return null;
            }
            if (metaData is not MemoryStream) {
                Debug.WriteLine("Found " + XFER_METADATA_NAME + " w/o MemoryStream");
                return null;
            }
            ClipInfo clipInfo;
            try {
                object? parsed =
                    JsonSerializer.Deserialize((MemoryStream)metaData, typeof(ClipInfo));
                if (parsed == null) {
                    return null;
                }
                clipInfo = (ClipInfo)parsed;
                if (clipInfo.ClipEntries == null) {
                    Debug.WriteLine("ClipInfo arrived without ClipEntries");
                    return null;
                }
            } catch (JsonException ex) {
                Debug.WriteLine("Clipboard deserialization failed: " + ex.Message);
                return null;
            }
            return clipInfo;
        }

        /// <summary>
        /// Registers a clipboard format with the system.
        /// </summary>
        /// <returns>Clipboard format index (0xc000-ffff).</returns>
        private static short RegisterFormat(string name) {
            // Return value should be 0xc000-0xffff.
            uint fmtNum = RegisterClipboardFormat(name);
            if (fmtNum == 0) {
                // TODO: if this is likely to occur, we should handle it better
                throw new Exception("Unable to register clipboard format '" +
                    name + "', error=" + Marshal.GetLastWin32Error());
            }
            Debug.Assert(fmtNum == (ushort)fmtNum);
            Debug.WriteLine("Registered clipboard format 0x" + fmtNum.ToString("x4"));
            return (short)fmtNum;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint RegisterClipboardFormat(string lpszFormat);
    }
}
