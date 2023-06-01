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
using System.Windows;
using System.Windows.Controls;

using AppCommon;
using CommonUtil;
using DiskArc.FS;
using DiskArc;
using static DiskArc.Defs;

namespace cp2_wpf {
    /// <summary>
    /// Data object for the file list.  This is used for both file archives and disk images.
    /// Some of the fields are only relevant for one.
    /// </summary>
    public class FileListItem {
        public IFileEntry FileEntry { get; set; }

        public ControlTemplate? StatusIcon { get; set; }
        public string FileName { get; set; }
        public string PathName { get; set; }
        public string Type { get; set; }
        public string AuxType { get; set; }
        //public string HFSType { get; set; }
        //public string HFSCreator { get; set; }
        public string CreateDate { get; set; }
        public string ModDate { get; set; }
        public string Access { get; set; }
        public string DataLength { get; set; }
        public string DataSize { get; set; }
        public string DataFormat { get; set; }
        public string RawDataLength { get; set; }
        public string RsrcLength { get; set; }
        public string RsrcSize { get; set; }
        public string RsrcFormat { get; set; }
        public string TotalSize { get; set; }

        public FileListItem(IFileEntry entry, Formatter fmt) {
            FileEntry = entry;

            if (entry.IsDubious) {
                StatusIcon =
                    (ControlTemplate)Application.Current.FindResource("icon_StatusInvalid");
            } else if (entry.IsDamaged) {
                StatusIcon =
                    (ControlTemplate)Application.Current.FindResource("icon_StatusError");
            } else {
                StatusIcon = null;
            }
            FileName = entry.FileName;
            PathName = entry.FullPathName;
            CreateDate = fmt.FormatDateTime(entry.CreateWhen);
            ModDate = fmt.FormatDateTime(entry.ModWhen);
            Access = fmt.FormatAccessFlags(entry.Access);

            if (entry.IsDiskImage) {
                Type = "Disk";
                AuxType = string.Format("{0}KB", entry.DataLength / 1024);
            } else if (entry.IsDirectory) {
                Type = "DIR";
                AuxType = string.Empty;
            } else if (entry is DOS_FileEntry) {
                switch (entry.FileType) {
                    case FileAttribs.FILE_TYPE_TXT:
                        Type = " T";
                        break;
                    case FileAttribs.FILE_TYPE_INT:
                        Type = " I";
                        break;
                    case FileAttribs.FILE_TYPE_BAS:
                        Type = " A";
                        break;
                    case FileAttribs.FILE_TYPE_BIN:
                        Type = " B";
                        break;
                    case FileAttribs.FILE_TYPE_F2:
                        Type = " S";
                        break;
                    case FileAttribs.FILE_TYPE_REL:
                        Type = " R";
                        break;
                    case FileAttribs.FILE_TYPE_F3:
                        Type = " AA";
                        break;
                    case FileAttribs.FILE_TYPE_F4:
                        Type = " BB";
                        break;
                    default:
                        Type = " ??";
                        break;
                }
                AuxType = string.Format("${0:X4}", entry.AuxType);
            } else if (entry.HasHFSTypes) {
                // See if ProDOS types are buried in the HFS types.
                if (FileAttribs.ProDOSFromHFS(entry.HFSFileType, entry.HFSCreator,
                        out byte proType, out ushort proAux)) {
                    Type = FileTypes.GetFileTypeAbbrev(proType) +
                        (entry.RsrcLength > 0 ? '+' : ' ');
                    AuxType = string.Format("${0:X4}", proAux);
                } else if (entry.HFSCreator == 0 && entry.HFSFileType == 0) {
                    if (entry.HasProDOSTypes) {
                        // Use the ProDOS types instead.  GSHK does this for ProDOS files.
                        Type = FileTypes.GetFileTypeAbbrev(entry.FileType) +
                            (entry.HasRsrcFork ? '+' : ' ');
                        AuxType = string.Format("${0:X4}", entry.AuxType);
                    } else {
                        Type = FileTypes.GetFileTypeAbbrev(0x00) +
                            (entry.RsrcLength > 0 ? '+' : ' ');
                        AuxType = "$0000";
                    }
                } else {
                    // Stringify the HFS types.  No need to show as hex.
                    // All HFS files have a resource fork, so only show a '+' if it has data in it.
                    Type = MacChar.StringifyMacConstant(entry.HFSFileType) +
                        (entry.RsrcLength > 0 ? '+' : ' ');
                    AuxType = ' ' + MacChar.StringifyMacConstant(entry.HFSCreator);
                }
            } else if (entry.HasProDOSTypes) {
                // Show a '+' if a resource fork is present, whether or not it has data.
                Type = FileTypes.GetFileTypeAbbrev(entry.FileType) +
                    (entry.HasRsrcFork ? '+' : ' ');
                AuxType = string.Format("${0:X4}", entry.AuxType);
            } else {
                Type = AuxType = "----";
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
            if (entry.GetPartInfo(FilePart.RsrcFork, out length, out storageSize, out format)) {
                RsrcLength = length.ToString();
                RsrcSize = storageSize.ToString();
                RsrcFormat = ThingString.CompressionFormat(format);
                totalSize += storageSize;
            } else {
                RsrcLength = RsrcSize = RsrcFormat = string.Empty;
            }

            TotalSize = totalSize.ToString();
        }

        public override string ToString() {
            return "[Item: " + FileName + "]";
        }
    }
}
