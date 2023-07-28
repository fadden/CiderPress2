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
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Windows;

namespace cp2_wpf.WPFCommon {
    /// <summary>
    /// Some magical Windows-only functions.
    /// </summary>
    public static class WinMagic {
        #region Known Folders
        public enum KnownFolder {
            Contacts,
            Downloads,
            Favorites,
            Links,
            SavedGames,
            SavedSearches
        }

        /// <summary>
        /// Obtains the path to a "known" folder.  While some of these are accessible through
        /// the Environment "special" folders interface, others (like Downloads) can be
        /// moved by the user, so we have to query the system instead of simply pulling them
        /// out of the environment.
        /// </summary>
        /// <remarks>
        /// <para>From <see href="https://stackoverflow.com/a/21953690/294248"/>.</para>
        /// </remarks>
        /// <param name="knownFolder"></param>
        /// <returns></returns>
        public static string GetKnownFolderPath(KnownFolder knownFolder) {
            return SHGetKnownFolderPath(_guids[knownFolder], 0);
        }

        // Magic values from Win API KNOWNFOLDERID constants.  This system replaces the older
        // CSIDL system.  See https://learn.microsoft.com/en-us/windows/win32/shell/knownfolderid
        // and https://learn.microsoft.com/en-us/windows/win32/shell/known-folders .
        private static readonly Dictionary<KnownFolder, Guid> _guids = new() {
            [KnownFolder.Contacts] = new("56784854-C6CB-462B-8169-88E350ACB882"),
            [KnownFolder.Downloads] = new("374DE290-123F-4565-9164-39C4925E467B"),
            [KnownFolder.Favorites] = new("1777F761-68AD-4D8A-87BD-30B759FA33DD"),
            [KnownFolder.Links] = new("BFB9D5E0-C6A9-404C-B2B2-AE6DB6AF4968"),
            [KnownFolder.SavedGames] = new("4C5C32FF-BB9D-43B0-B5B4-2D72E54EAAA4"),
            [KnownFolder.SavedSearches] = new("7D1D3A04-DEBB-4115-95CF-2F29DA2920DA")
        };

        [DllImport("shell32", CharSet = CharSet.Auto, ExactSpelling = true, PreserveSig = false)]
        private static extern string SHGetKnownFolderPath(
            [MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, nint hToken = 0);

        #endregion Known Folders

        #region File Icon

        public enum IconQuery {
            Unknown = 0, Specific, GenericFile, GenericFolder
        }

        /// <summary>
        /// Obtains the icon for a file.  Obtaining specific icons for file can be slow, so
        /// we provide the option of obtaining a generic file/folder icon.
        /// </summary>
        /// <remarks>
        /// <para>From <see href="https://stackoverflow.com/a/29819585/294248"/>.</para>
        /// </remarks>
        /// <param name="strPath">Path to file.</param>
        /// <param name="query">Icon query type.</param>
        /// <returns>Icon graphic.</returns>
        public static ImageSource GetIcon(string strPath, IconQuery query) {
            SHFILEINFO info = new SHFILEINFO(true);
            int cbFileInfo = Marshal.SizeOf(info);
            bool wantSmall = false;
            SHGFI flags = SHGFI.Icon;
            flags |= wantSmall ? SHGFI.SmallIcon : SHGFI.LargeIcon;
            int fileAttribs = 0;
            switch (query) {
                case IconQuery.Specific:
                    break;
                case IconQuery.GenericFile:
                    flags |= SHGFI.UseFileAttributes;
                    fileAttribs = FILE_ATTRIBUTE_NORMAL;
                    break;
                case IconQuery.GenericFolder:
                    flags |= SHGFI.UseFileAttributes;
                    fileAttribs = FILE_ATTRIBUTE_DIRECTORY;
                    break;
                default:
                    throw new NotImplementedException();
            }

            SHGetFileInfo(strPath, fileAttribs, out info, (uint)cbFileInfo, flags);

            IntPtr iconHandle = info.hIcon;
            //if (IntPtr.Zero == iconHandle) // not needed, always return icon (blank)
            //  return DefaultImgSrc;
            ImageSource img = Imaging.CreateBitmapSourceFromHIcon(
                        iconHandle,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
            DestroyIcon(iconHandle);
            return img;
        }

        /// <summary>Maximal Length of unmanaged Windows-Path-strings</summary>
        private const int MAX_PATH = 260;
        /// <summary>Maximal Length of unmanaged Typename</summary>
        private const int MAX_TYPE = 80;

        private const int FILE_ATTRIBUTE_DIRECTORY = 0x0010;
        private const int FILE_ATTRIBUTE_NORMAL = 0x0080;

        // Info on constants:
        // https://learn.microsoft.com/en-us/windows/win32/fileio/file-attribute-constants
        [Flags]
        public enum SHGFI : int {
            /// <summary>get icon</summary>
            Icon = 0x000000100,
            /// <summary>get display name</summary>
            DisplayName = 0x000000200,
            /// <summary>get type name</summary>
            TypeName = 0x000000400,
            /// <summary>get attributes</summary>
            Attributes = 0x000000800,
            /// <summary>get icon location</summary>
            IconLocation = 0x000001000,
            /// <summary>return exe type</summary>
            ExeType = 0x000002000,
            /// <summary>get system icon index</summary>
            SysIconIndex = 0x000004000,
            /// <summary>put a link overlay on icon</summary>
            LinkOverlay = 0x000008000,
            /// <summary>show icon in selected state</summary>
            Selected = 0x000010000,
            /// <summary>get only specified attributes</summary>
            Attr_Specified = 0x000020000,
            /// <summary>get large icon</summary>
            LargeIcon = 0x000000000,
            /// <summary>get small icon</summary>
            SmallIcon = 0x000000001,
            /// <summary>get open icon</summary>
            OpenIcon = 0x000000002,
            /// <summary>get shell size icon</summary>
            ShellIconSize = 0x000000004,
            /// <summary>pszPath is a pidl</summary>
            PIDL = 0x000000008,
            /// <summary>use passed dwFileAttribute</summary>
            UseFileAttributes = 0x000000010,
            /// <summary>apply the appropriate overlays</summary>
            AddOverlays = 0x000000020,
            /// <summary>Get the index of the overlay in the upper 8 bits of the iIcon</summary>
            OverlayIndex = 0x000000040,
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct SHFILEINFO {
            public SHFILEINFO(bool b) {
                hIcon = IntPtr.Zero;
                iIcon = 0;
                dwAttributes = 0;
                szDisplayName = "";
                szTypeName = "";
            }
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_PATH)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_TYPE)]
            public string szTypeName;
        };

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        public static extern int SHGetFileInfo(string pszPath, int dwFileAttributes,
            out SHFILEINFO psfi, uint cbfileInfo, SHGFI uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyIcon(IntPtr hIcon);

        #endregion File Icon
    }
}
