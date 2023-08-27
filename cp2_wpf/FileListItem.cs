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
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

using AppCommon;
using CommonUtil;
using DiskArc.FS;
using DiskArc;
using static DiskArc.Defs;
using cp2_wpf.WPFCommon;

//
// TODO (maybe): we might be able to improve latency when switching between large file lists by
// caching FileListItem objects, perhaps in a Dictionary<IFileEntry, FileListItem>.  The cache
// would be attached to the ArchiveTreeItem.
//
// Data virtualization would be better, but that's hard to do in WPF.
//

namespace cp2_wpf {
    /// <summary>
    /// Data object for the file list.  This is used for both file archives and disk images.
    /// Some of the fields are only relevant for one.
    /// </summary>
    public class FileListItem {
        /// <summary>
        /// String to show when no type information is available.  We want this to be something
        /// that isn't available in the Mac OS Roman character set, and that looks nicer than
        /// four NUL control picture glyphs.
        /// </summary>
        private const string NO_TYPE = "\u23af\u23af\u23af\u23af";

        private static readonly ControlTemplate sInvalidIcon =
            (ControlTemplate)Application.Current.FindResource("icon_StatusInvalid");
        private static readonly ControlTemplate sErrorIcon =
            (ControlTemplate)Application.Current.FindResource("icon_StatusError");
        private static readonly ControlTemplate sCommentIcon =
            (ControlTemplate)Application.Current.FindResource("icon_Comment");

        public IFileEntry FileEntry { get; private set; }

        public ControlTemplate? StatusIcon { get; private set; }
        public string FileName { get; private set; }
        public string PathName { get; private set; }
        public string Type { get; private set; }
        public string AuxType { get; private set; }
        //public string HFSType { get; private set; }
        //public string HFSCreator { get; private set; }
        public string CreateDate { get; private set; }
        public string ModDate { get; private set; }
        public string Access { get; private set; }
        public string DataLength { get; private set; }
        public string DataSize { get; private set; }
        public string DataFormat { get; private set; }
        public string RawDataLength { get; private set; }
        public string RsrcLength { get; private set; }
        public string RsrcSize { get; private set; }
        public string RsrcFormat { get; private set; }
        public string TotalSize { get; private set; }


        /// <summary>
        /// Constructor.  Fills out properties from file entry object.
        /// </summary>
        /// <param name="entry">File entry object.</param>
        /// <param name="fmt">Formatter.</param>
        public FileListItem(IFileEntry entry, Formatter fmt)
            : this(entry, IFileEntry.NO_ENTRY, null, fmt) { }

        /// <summary>
        /// Constructor for archive entries that might be a MacZip pair.
        /// </summary>
        /// <param name="entry">File entry object for main entry.</param>
        /// <param name="adfEntry">File entry object for MacZip header, or NO_ENTRY.</param>
        /// <param name="attrs">File attributes, from MacZip header.  Will be null if the file
        ///   doesn't have a MacZip header.</param>
        /// <param name="fmt">Formatter.</param>
        public FileListItem(IFileEntry entry, IFileEntry adfEntry, FileAttribs? adfAttrs,
                Formatter fmt) {
            FileEntry = entry;

            // Use the entry comment, unless it's MacZip, in which case use the ADF comment.
            string comment = entry.Comment;
            if (adfAttrs != null) {
                comment = adfAttrs.Comment;
            }

            if (entry.IsDubious) {
                StatusIcon = sInvalidIcon;
            } else if (entry.IsDamaged) {
                StatusIcon = sErrorIcon;
            } else if (comment.Length > 0) {
                StatusIcon = sCommentIcon;
            } else {
                StatusIcon = null;
            }
            FileName = entry.FileName;
            PathName = entry.FullPathName;
            CreateDate = fmt.FormatDateTime(entry.CreateWhen);
            ModDate = fmt.FormatDateTime(entry.ModWhen);
            if (adfAttrs != null) {
                Access = fmt.FormatAccessFlags(adfAttrs.Access);
            } else {
                Access = fmt.FormatAccessFlags(entry.Access);
            }

            if (entry.IsDiskImage) {
                Type = "Disk";
                AuxType = string.Format("{0}KB", entry.DataLength / 1024);
            } else if (entry.IsDirectory) {
                Type = "DIR";
                AuxType = string.Empty;
            } else if (entry is DOS_FileEntry) {
                Type = " " + FileTypes.GetDOSTypeAbbrev(entry.FileType);
                AuxType = string.Format("${0:X4}", entry.AuxType);
            } else if (entry.HasHFSTypes) {
                // See if ProDOS types are buried in the HFS types.
                if (FileAttribs.ProDOSFromHFS(entry.HFSFileType, entry.HFSCreator,
                        out byte proType, out ushort proAux)) {
                    Type = FileTypes.GetFileTypeAbbrev(proType) +
                        (entry.RsrcLength > 0 ? '+' : ' ');
                    AuxType = string.Format("${0:X4}", proAux);
                } else if (entry.HFSCreator != 0 || entry.HFSFileType != 0) {
                    // Stringify the HFS types.  No need to show as hex.
                    // All HFS files have a resource fork, so only show a '+' if it has data in it.
                    // (Or maybe just don't bother?)
                    Type = MacChar.StringifyMacConstant(entry.HFSFileType) /*+
                        (entry.RsrcLength > 0 ? '+' : ' ')*/;
                    AuxType = ' ' + MacChar.StringifyMacConstant(entry.HFSCreator);
                } else if (entry.HasProDOSTypes) {
                    // Use the ProDOS types instead.  GSHK does this for ProDOS files.
                    Type = FileTypes.GetFileTypeAbbrev(entry.FileType) +
                        (entry.HasRsrcFork ? '+' : ' ');
                    AuxType = string.Format("${0:X4}", entry.AuxType);
                } else {
                    // HFS types are zero, ProDOS types are zero or not available; give up.
                    Type = AuxType = NO_TYPE;
                }
            } else if (entry.HasProDOSTypes) {
                // Show a '+' if a resource fork is present, whether or not it has data.
                Type = FileTypes.GetFileTypeAbbrev(entry.FileType) +
                    (entry.HasRsrcFork ? '+' : ' ');
                AuxType = string.Format("${0:X4}", entry.AuxType);
            } else if (adfAttrs != null) {
                // Use the contents of the MacZip header file.
                if (FileAttribs.ProDOSFromHFS(adfAttrs.HFSFileType, adfAttrs.HFSCreator,
                        out byte proType, out ushort proAux)) {
                    Type = FileTypes.GetFileTypeAbbrev(proType) +
                        (adfAttrs.RsrcLength > 0 ? '+' : ' ');
                    AuxType = string.Format("${0:X4}", proAux);
                } else if (adfAttrs.HFSCreator != 0 || adfAttrs.HFSFileType != 0) {
                    Type = MacChar.StringifyMacConstant(adfAttrs.HFSFileType) +
                        (adfAttrs.RsrcLength > 0 ? '+' : ' ');
                    AuxType = ' ' + MacChar.StringifyMacConstant(adfAttrs.HFSCreator);
                } else {
                    // Use the ProDOS types instead.  GSHK does this for ProDOS files.
                    Type = FileTypes.GetFileTypeAbbrev(adfAttrs.FileType) +
                        (adfAttrs.RsrcLength > 0 ? '+' : ' ');
                    AuxType = string.Format("${0:X4}", adfAttrs.AuxType);
                }
            } else {
                // No type information available (e.g. ZIP without MacZip).
                Type = AuxType = NO_TYPE;
            }

            RawDataLength = string.Empty;

            long length, storageSize, totalSize = 0;
            CompressionFormat format;
            if (entry.GetPartInfo(FilePart.DataFork, out length, out storageSize, out format)) {
                if (entry.GetPartInfo(FilePart.RawData, out long rawLength, out long un1,
                        out CompressionFormat un2)) {
                    RawDataLength = rawLength.ToString();
                }
                DataLength = length.ToString();
                DataSize = storageSize.ToString();
                DataFormat = ThingString.CompressionFormat(format);
                totalSize += storageSize;
            } else if (entry.GetPartInfo(FilePart.DiskImage, out length, out storageSize,
                        out format)) {
                DataLength = length.ToString();
                DataSize = storageSize.ToString();
                DataFormat = ThingString.CompressionFormat(format);
                totalSize += storageSize;
            } else {
                DataLength = DataSize = DataFormat = string.Empty;
            }

            bool hasRsrc = true;
            long rsrcLen, rsrcSize;
            CompressionFormat rsrcFmt;
            if (adfAttrs != null) {
                // Use the length/format of the ADF header file in the ZIP archive as
                // the compressed length/format, since that's the part that's Deflated.
                if (!adfEntry.GetPartInfo(FilePart.DataFork, out long unused,
                        out rsrcSize, out rsrcFmt)) {
                    // not expected
                    hasRsrc = false;
                }
                rsrcLen = adfAttrs.RsrcLength;      // ADF is not compressed
            } else if (!entry.GetPartInfo(FilePart.RsrcFork, out rsrcLen, out rsrcSize, 
                    out rsrcFmt)) {
                hasRsrc = false;
            }
            if (hasRsrc) {
                RsrcLength = rsrcLen.ToString();
                RsrcSize = rsrcSize.ToString();
                RsrcFormat = ThingString.CompressionFormat(rsrcFmt);
            } else {
                RsrcLength = RsrcSize = RsrcFormat = string.Empty;
            }
            totalSize += rsrcSize;

            if (entry is DOS_FileEntry) {
                TotalSize = fmt.FormatSizeOnDisk(totalSize, SECTOR_SIZE);
            } else if (entry is ProDOS_FileEntry || entry is HFS_FileEntry) {
                TotalSize = fmt.FormatSizeOnDisk(totalSize, BLOCK_SIZE);
            } else {
                TotalSize = totalSize.ToString();
            }
        }

        /// <summary>
        /// Finds an item in the file list, searching by IFileEntry.
        /// </summary>
        public static FileListItem? FindItemByEntry(ObservableCollection<FileListItem> tvRoot,
                IFileEntry entry) {
            foreach (FileListItem item in tvRoot) {
                if (item.FileEntry == entry) {
                    return item;
                }
            }
            return null;
        }

        /// <summary>
        /// Sets the current file list selection to the item with a matching IFileEntry.  If
        /// no item matches, the selection is set to the first item in the list.
        /// </summary>
        /// <remarks>
        /// <para>Somehow the focus shift this does ends up being particularly forceful, and
        /// can cause some really strange behavior in WPF.</para>
        /// </remarks>
        public static void SetSelectionFocusByEntry(ObservableCollection<FileListItem> tvRoot,
                DataGrid dataGrid, IFileEntry selEntry) {
            FileListItem? reselItem = null;
            if (selEntry != IFileEntry.NO_ENTRY) {
                reselItem = FindItemByEntry(tvRoot, selEntry);
            }
            if (reselItem != null) {
                dataGrid.SelectedItem = reselItem;                  // item -> index
                dataGrid.SelectRowByIndex(dataGrid.SelectedIndex);  // set focus as well
            } else {
                // Not found (e.g. we changed directories and the selected entry is no longer
                // part of the file list), or selEntry is NO_ENTRY.
                if (dataGrid.Items.Count > 0) {
                    //Debug.WriteLine("Selecting row 0");
                    dataGrid.SelectRowByIndex(0);
                }
            }
        }

        public override string ToString() {
            return "[Item: " + FileName + "]";
        }
    }
}
