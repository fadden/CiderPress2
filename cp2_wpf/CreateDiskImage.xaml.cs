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
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

using AppCommon;
using CommonUtil;
using DiskArc;
using DiskArc.Arc;
using DiskArc.Disk;
using DiskArc.FS;
using static DiskArc.Defs;

namespace cp2_wpf {
    /// <summary>
    /// Gather parameters for disk image creation.
    /// </summary>
    public partial class CreateDiskImage : Window, INotifyPropertyChanged {
        // INotifyPropertyChanged implementation
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string propertyName = "") {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private Brush mDefaultLabelColor = SystemColors.WindowTextBrush;
        private Brush mErrorLabelColor = Brushes.Red;

        /// <summary>
        /// True if the configuration is valid.  This will be false if the free-entry fields
        /// (custom size, volume name, volume number) are invalid.
        /// </summary>
        public bool IsValid {
            get { return mIsValid; }
            set { mIsValid = value; OnPropertyChanged(); }
        }
        private bool mIsValid;

        /// <summary>
        /// Pathname of created file.
        /// </summary>
        public string PathName { get; private set; } = string.Empty;

        private AppHook mAppHook;


        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="owner">Parent window.</param>
        /// <param name="appHook">Application hook reference.</param>
        public CreateDiskImage(Window owner, AppHook appHook) {
            InitializeComponent();
            Owner = owner;
            DataContext = this;

            mIsValid = false;
            mAppHook = appHook;

            SettingsHolder settings = AppSettings.Global;
            mDiskSize = settings.GetEnum(AppSettings.NEW_DISK_SIZE, DiskSizeValue.Flop525_140);
            mFilesystem = settings.GetEnum(AppSettings.NEW_DISK_FILESYSTEM, FileSystemType.ProDOS);
            mFileType = settings.GetEnum(AppSettings.NEW_DISK_FILE_TYPE, FileTypeValue.ProDOSBlock);

            mVolumeNameText = settings.GetString(AppSettings.NEW_DISK_VOLUME_NAME, "NEWDISK");
            mVolumeNumText = settings.GetInt(AppSettings.NEW_DISK_VOLUME_NUM,
                Defs.DEFAULT_525_VOLUME_NUM).ToString();
            mCustomSizeText = settings.GetString(AppSettings.NEW_DISK_CUSTOM_SIZE, "65535");
            mIsChecked_MakeBootable = settings.GetBool(AppSettings.NEW_DISK_MAKE_BOOTABLE, true);

            UpdateControls();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e) {
            if (!CreateImage()) {
                return;
            }

            DialogResult = true;

            // Save current UI state to the settings.
            GetVolNum(out int volNum);

            SettingsHolder settings = AppSettings.Global;
            settings.SetEnum(AppSettings.NEW_DISK_SIZE, mDiskSize);
            settings.SetEnum(AppSettings.NEW_DISK_FILESYSTEM, mFilesystem);
            settings.SetEnum(AppSettings.NEW_DISK_FILE_TYPE, mFileType);

            settings.SetString(AppSettings.NEW_DISK_VOLUME_NAME, mVolumeNameText);
            settings.SetInt(AppSettings.NEW_DISK_VOLUME_NUM, volNum);
            settings.SetString(AppSettings.NEW_DISK_CUSTOM_SIZE, mCustomSizeText);
            settings.SetBool(AppSettings.NEW_DISK_MAKE_BOOTABLE, mIsChecked_MakeBootable);
        }

        /// <summary>
        /// Creates the disk image file as directed.
        /// </summary>
        /// <returns>True on success.</returns>
        private bool CreateImage() {
            uint blocks, tracks, sectors;

            bool is13Sector = GetNumTracksSectors(out tracks, out sectors) && sectors == 13;
            string pathName = SelectOutputFile(mFileType, is13Sector);
            if (string.IsNullOrEmpty(pathName)) {
                return false;
            }

            FileStream? stream;
            try {
                stream = new FileStream(pathName, FileMode.Create);
            } catch (Exception ex) {
                MessageBox.Show(this, "Unable to create file: " + ex.Message, "Failed",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            MemoryStream? tmpStream = null;

            try {
                Mouse.OverrideCursor = Cursors.Wait;

                IDiskImage diskImage;
                SectorCodec codec;
                MediaKind mediaKind;

                GetVolNum(out int volNum);

                // Create a disk image in the output stream.
                switch (mFileType) {
                    case FileTypeValue.DOSSector:
                        if (!GetNumTracksSectors(out tracks, out sectors)) {
                            throw new Exception("internal error");
                        }
                        diskImage = UnadornedSector.CreateSectorImage(stream, tracks, sectors,
                            SectorOrder.DOS_Sector, mAppHook);
                        break;
                    case FileTypeValue.ProDOSBlock:
                        // Try to create as sectors, since this could be a DOS filesystem.
                        if (GetNumTracksSectors(out tracks, out sectors)) {
                            diskImage = UnadornedSector.CreateSectorImage(stream, tracks, sectors,
                                SectorOrder.ProDOS_Block, mAppHook);
                        } else if (GetNumBlocks(out blocks)) {
                            diskImage = UnadornedSector.CreateBlockImage(stream, blocks, mAppHook);
                        } else {
                            throw new Exception("internal error");
                        }
                        break;
                    case FileTypeValue.TwoIMG:
                        if (GetNumTracksSectors(out tracks, out sectors)) {
                            diskImage = TwoIMG.CreateDOSSectorImage(stream, tracks, mAppHook);
                        } else if (GetNumBlocks(out blocks)) {
                            diskImage = TwoIMG.CreateProDOSBlockImage(stream, blocks, mAppHook);
                        } else {
                            throw new Exception("internal error");
                        }
                        if (volNum != Defs.DEFAULT_525_VOLUME_NUM) {
                            ((TwoIMG)diskImage).VolumeNumber = volNum;
                        }
                        break;
                    case FileTypeValue.NuFX:
                        // Create a temporary in-memory image.
                        if (!GetNumBlocks(out blocks)) {
                            throw new Exception("internal error");
                        }
                        tmpStream = new MemoryStream();
                        if (blocks == 280) {
                            diskImage = UnadornedSector.CreateSectorImage(tmpStream, 35, 16,
                                SectorOrder.ProDOS_Block, mAppHook);
                        } else if (blocks == 1600) {
                            diskImage = UnadornedSector.CreateBlockImage(tmpStream, 1600, mAppHook);
                        } else {
                            throw new Exception("internal error");
                        }
                        break;
                    case FileTypeValue.DiskCopy42:
                        if (!GetMediaKind(out mediaKind)) {
                            throw new Exception("internal error");
                        }
                        diskImage = DiskCopy.CreateDisk(stream, mediaKind, mAppHook);
                        break;

                    case FileTypeValue.Woz:
                        if (IsFlop525) {
                            if (!GetNumTracksSectors(out tracks, out sectors)) {
                                throw new Exception("internal error");
                            }
                            codec = (sectors == 13) ?
                                StdSectorCodec.GetCodec(StdSectorCodec.CodecIndex525.Std_525_13) :
                                StdSectorCodec.GetCodec(StdSectorCodec.CodecIndex525.Std_525_16);
                            diskImage = Woz.CreateDisk525(stream, tracks, codec, (byte)volNum,
                                mAppHook);
                        } else {
                            if (!GetMediaKind(out mediaKind)) {
                                throw new Exception("internal error");
                            }
                            codec = StdSectorCodec.GetCodec(StdSectorCodec.CodecIndex35.Std_35);
                            diskImage = Woz.CreateDisk35(stream, mediaKind, WOZ_IL_35, codec,
                                mAppHook);
                        }
                        // Let's just add a default META chunk to all disks.
                        ((Woz)diskImage).AddMETA();
                        break;
                    case FileTypeValue.Nib:
                        if (!GetNumTracksSectors(out tracks, out sectors)) {
                            throw new Exception("internal error");
                        }
                        codec = (sectors == 13) ?
                            StdSectorCodec.GetCodec(StdSectorCodec.CodecIndex525.Std_525_13) :
                            StdSectorCodec.GetCodec(StdSectorCodec.CodecIndex525.Std_525_16);
                        diskImage = UnadornedNibble525.CreateDisk(stream, codec, (byte)volNum,
                            mAppHook);
                        break;
                    case FileTypeValue.Trackstar:
                        if (!GetNumTracksSectors(out tracks, out sectors)) {
                            throw new Exception("internal error");
                        }
                        codec = (sectors == 13) ?
                            StdSectorCodec.GetCodec(StdSectorCodec.CodecIndex525.Std_525_13) :
                            StdSectorCodec.GetCodec(StdSectorCodec.CodecIndex525.Std_525_16);
                        diskImage = Trackstar.CreateDisk(stream, codec, (byte)volNum, tracks,
                            mAppHook);
                        break;
                    default:
                        throw new NotImplementedException("Not implemented: " + mFileType);
                }

                // Format the filesystem, if one was chosen.
                using (diskImage) {
                    if (mFilesystem != FileSystemType.Unknown) {
                        // Map a filesystem instance on top of the disk image chunks.
                        FileAnalyzer.CreateInstance(mFilesystem, diskImage.ChunkAccess!, mAppHook,
                            out IDiskContents? contents);
                        if (contents is not IFileSystem) {
                            throw new DAException("Unable to create filesystem");
                        }
                        IFileSystem fs = (IFileSystem)contents;

                        // New stream, no need to initialize chunks.  Format the filesystem.
                        fs.Format(VolumeNameText, volNum, IsChecked_MakeBootable);

                        // Dispose of the object to ensure everything has been flushed.
                        fs.Dispose();
                    }
                }

                // Handle NuFX ".SDK" disk image creation.
                if (mFileType == FileTypeValue.NuFX) {
                    Debug.Assert(tmpStream != null);

                    NuFX archive = NuFX.CreateArchive(mAppHook);
                    archive.StartTransaction();
                    IFileEntry entry = archive.CreateRecord();
                    // Using the volume name is risky, because we don't syntax-check it for
                    // all filesystems.
                    if (mFilesystem == FileSystemType.ProDOS) {
                        entry.FileName = VolumeNameText;
                    } else {
                        entry.FileName = "DISK";
                    }
                    SimplePartSource source = new SimplePartSource(tmpStream);
                    archive.AddPart(entry, FilePart.DiskImage, source, CompressionFormat.Default);
                    archive.CommitTransaction(stream);
                }

                // Copy the pathname to a property where the caller can see it.
                PathName = pathName;
            } catch (Exception ex) {
                MessageBox.Show(this, "Error creating disk: " + ex.Message, "Failed",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                // Need to close file before deleting it.
                stream.Close();
                stream = null;
                Debug.WriteLine("Cleanup: removing '" + pathName + "'");
                File.Delete(pathName);
                return false;
            } finally {
                if (stream != null) {
                    stream.Close();
                }
                Mouse.OverrideCursor = null;
            }

            return true;
        }

        /// <summary>
        /// Selects the output file, using the standard file dialog.  The filename extension is
        /// chosen based on the file type.
        /// </summary>
        /// <param name="fileType">Type of file to create.</param>
        /// <param name="is13Sector">True if this is a 13-sector disk image.</param>
        /// <returns>Fullly-qualified pathname.</returns>
        internal static string SelectOutputFile(FileTypeValue fileType, bool is13Sector) {
            string filter, ext;
            switch (fileType) {
                case FileTypeValue.DOSSector:
                    if (is13Sector) {
                        filter = WinUtil.FILE_FILTER_D13;
                        ext = ".d13";
                    } else {
                        filter = WinUtil.FILE_FILTER_DO;
                        ext = ".do";
                    }
                    break;
                case FileTypeValue.ProDOSBlock:
                    filter = WinUtil.FILE_FILTER_PO;
                    ext = ".po";
                    break;
                case FileTypeValue.TwoIMG:
                    filter = WinUtil.FILE_FILTER_2MG;
                    ext = ".2mg";
                    break;
                case FileTypeValue.NuFX:
                    filter = WinUtil.FILE_FILTER_SDK;
                    ext = ".sdk";
                    break;
                case FileTypeValue.DiskCopy42:
                    filter = WinUtil.FILE_FILTER_DC42;
                    ext = ".image";
                    break;
                case FileTypeValue.Woz:
                    filter = WinUtil.FILE_FILTER_WOZ;
                    ext = ".woz";
                    break;
                case FileTypeValue.Nib:
                    filter = WinUtil.FILE_FILTER_NIB;
                    ext = ".nib";
                    break;
                case FileTypeValue.Trackstar:
                    filter = WinUtil.FILE_FILTER_APP;
                    ext = ".app";
                    break;
                default:
                    throw new NotImplementedException("Not implemented: " + fileType);
            }

            string fileName = "NewDisk" + ext;

            // AddExtension, ValidateNames, CheckPathExists, OverwritePrompt are enabled by default
            SaveFileDialog fileDlg = new SaveFileDialog() {
                Title = "Create File...",
                Filter = filter + "|" + WinUtil.FILE_FILTER_ALL,
                FilterIndex = 1,
                FileName = fileName
            };
            if (fileDlg.ShowDialog() != true) {
                return string.Empty;
            }
            string pathName = Path.GetFullPath(fileDlg.FileName);
            if (!pathName.ToLowerInvariant().EndsWith(ext)) {
                pathName += ext;
            }

            return pathName;
        }

        private static readonly string[] sPropList = {
            nameof(IsChecked_Flop525_113),
            nameof(IsChecked_Flop525_140),
            nameof(IsChecked_Flop525_160),
            nameof(IsChecked_Flop35_400),
            nameof(IsChecked_Flop35_800),
            nameof(IsChecked_Flop35_1440),
            nameof(IsChecked_Other_32MB),
            nameof(IsChecked_Other_Custom),

            nameof(IsEnabled_FS_DOS),
            nameof(IsChecked_FS_DOS),
            nameof(IsEnabled_FS_ProDOS),
            nameof(IsChecked_FS_ProDOS),
            nameof(IsEnabled_FS_HFS),
            nameof(IsChecked_FS_HFS),
            nameof(IsEnabled_FS_Pascal),
            nameof(IsChecked_FS_Pascal),
            nameof(IsEnabled_FS_CPM),
            nameof(IsChecked_FS_CPM),
            nameof(IsEnabled_FS_None),
            nameof(IsChecked_FS_None),

            nameof(IsEnabled_FT_DOSSector),
            nameof(IsChecked_FT_DOSSector),
            nameof(IsEnabled_FT_ProDOSBlock),
            nameof(IsChecked_FT_ProDOSBlock),
            nameof(IsEnabled_FT_TwoIMG),
            nameof(IsChecked_FT_TwoIMG),
            nameof(IsEnabled_FT_NuFX),
            nameof(IsChecked_FT_NuFX),
            nameof(IsEnabled_FT_DiskCopy42),
            nameof(IsChecked_FT_DiskCopy42),
            nameof(IsEnabled_FT_Woz),
            nameof(IsChecked_FT_Woz),
            nameof(IsEnabled_FT_Nib),
            nameof(IsChecked_FT_Nib),
            nameof(IsEnabled_FT_Trackstar),
            nameof(IsChecked_FT_Trackstar),
        };

        private const long FOUR_GB = 4L * 1024 * 1024 * 1024;

        /// <summary>
        /// Updates the controls, adjusting radio button selections as needed.  The data-entry
        /// fields are evaluated for validity.
        /// </summary>
        private void UpdateControls() {
            bool customSizeSyntaxOk = true;
            bool customSizeLimitOk = true;
            if (mDiskSize == DiskSizeValue.Other_Custom) {
                // Check syntax.
                long customSize = GetVolSize();
                if (customSize < 0) {
                    customSizeSyntaxOk = false;
                } else if (customSize == 0 || customSize > FOUR_GB) {
                    customSizeLimitOk = false;
                }
            }
            SizeDescForeground = customSizeSyntaxOk ? mDefaultLabelColor : mErrorLabelColor;
            SizeLimitForeground = customSizeLimitOk ? mDefaultLabelColor : mErrorLabelColor;

            IsValid = customSizeSyntaxOk && customSizeLimitOk;

            // Adjust the radio selections for filesystem and file type to fit the current
            // disk size specification.  If the custom size value is invalid, don't do
            // the adjustments.
            if (IsValid) {
                // Check filesystem.
                bool needNewFs = false;
                switch (mFilesystem) {
                    case FileSystemType.DOS33:
                        needNewFs = !IsEnabled_FS_DOS;
                        break;
                    case FileSystemType.ProDOS:
                        needNewFs = !IsEnabled_FS_ProDOS;
                        break;
                    case FileSystemType.HFS:
                        needNewFs = !IsEnabled_FS_HFS;
                        break;
                    case FileSystemType.Pascal:
                        needNewFs = !IsEnabled_FS_Pascal;
                        break;
                    case FileSystemType.CPM:
                        needNewFs = !IsEnabled_FS_CPM;
                        break;
                    case FileSystemType.Unknown:
                        break;
                    default:
                        Debug.Assert(false);
                        break;
                }
                if (needNewFs) {
                    if (IsEnabled_FS_DOS) {
                        mFilesystem = FileSystemType.DOS33;
                    } else if (IsEnabled_FS_ProDOS) {
                        mFilesystem = FileSystemType.ProDOS;
                    } else if (IsEnabled_FS_HFS) {
                        mFilesystem = FileSystemType.HFS;
                    } else if (IsEnabled_FS_Pascal) {
                        mFilesystem = FileSystemType.Pascal;
                    } else if (IsEnabled_FS_CPM) {
                        mFilesystem = FileSystemType.CPM;
                    } else {
                        mFilesystem = FileSystemType.Unknown;
                    }
                }

                // Check file type.
                bool needNewType = false;
                switch (mFileType) {
                    case FileTypeValue.DOSSector:
                        needNewType = !IsEnabled_FT_DOSSector;
                        break;
                    case FileTypeValue.ProDOSBlock:
                        needNewType = !IsEnabled_FT_ProDOSBlock;
                        break;
                    case FileTypeValue.TwoIMG:
                        needNewType = !IsEnabled_FT_TwoIMG;
                        break;
                    case FileTypeValue.NuFX:
                        needNewType = !IsEnabled_FT_NuFX;
                        break;
                    case FileTypeValue.DiskCopy42:
                        needNewType = !IsEnabled_FT_DiskCopy42;
                        break;
                    case FileTypeValue.Woz:
                        needNewType = !IsEnabled_FT_Woz;
                        break;
                    case FileTypeValue.Nib:
                        needNewType = !IsEnabled_FT_Nib;
                        break;
                    case FileTypeValue.Trackstar:
                        needNewType = !IsEnabled_FT_Trackstar;
                        break;
                    default:
                        Debug.Assert(false);
                        break;
                }
                if (needNewType) {
                    if (IsEnabled_FT_ProDOSBlock) {
                        mFileType = FileTypeValue.ProDOSBlock;
                    } else if (IsEnabled_FT_DOSSector) {
                        mFileType = FileTypeValue.DOSSector;
                    } else if (IsEnabled_FT_Woz) {
                        mFileType = FileTypeValue.Woz;
                    } else if (IsEnabled_FT_TwoIMG) {
                        mFileType = FileTypeValue.TwoIMG;
                    } else if (IsEnabled_FT_NuFX) {             // shouldn't get this far
                        mFileType = FileTypeValue.NuFX;
                    } else if (IsEnabled_FT_DiskCopy42) {
                        mFileType = FileTypeValue.DiskCopy42;
                    } else if (IsEnabled_FT_Nib) {
                        mFileType = FileTypeValue.Nib;
                    } else if (IsEnabled_FT_Trackstar) {
                        mFileType = FileTypeValue.Trackstar;
                    } else {
                        // Custom size that has no compatible disk formats.  Leave the selection
                        // alone while things get sorted.  (If the minimum granularity is blocks,
                        // we shouldn't hit this unless they enter "0".)
                    }
                }
            }

            // Check the volume name if an appropriate filesystem is selected.
            bool volNameSyntaxOk = true;
            switch (mFilesystem) {
                case FileSystemType.ProDOS:
                    volNameSyntaxOk = ProDOS_FileEntry.IsVolumeNameValid(VolumeNameText);
                    break;
                case FileSystemType.HFS:
                    volNameSyntaxOk = HFS_FileEntry.IsVolumeNameValid(VolumeNameText);
                    break;
                case FileSystemType.Pascal:
                    // TODO
                    break;
                case FileSystemType.DOS33:
                case FileSystemType.CPM:
                case FileSystemType.Unknown:
                    break;
                default:
                    throw new NotImplementedException("Didn't handle " + mFilesystem);
            }
            VolNameSyntaxForeground = volNameSyntaxOk ? mDefaultLabelColor : mErrorLabelColor;
            IsValid &= volNameSyntaxOk;

            // Check the volume number.  It's used for the DOS VTOC and for 5.25" floppy sector
            // formatting.  No real reason not to check it for everything.
            bool volNumSyntaxOk = GetVolNum(out int volNum);
            VolNumSyntaxForeground = volNumSyntaxOk ? mDefaultLabelColor : mErrorLabelColor;
            IsValid &= volNumSyntaxOk;

            // Broadcast updates for all controls.
            foreach (string propName in sPropList) {
                OnPropertyChanged(propName);
            }
        }

        #region Disk Size

        public enum DiskSizeValue {
            Unknown = 0,
            Flop525_114, Flop525_140, Flop525_160,
            Flop35_400, Flop35_800, Flop35_1440,
            Other_32MB, Other_Custom
        }
        public DiskSizeValue mDiskSize;

        /// <summary>
        /// Returns the size, in bytes, of the selected disk size item.  For custom entries
        /// this will return -1 if the syntax is invalid.
        /// </summary>
        private long GetVolSize() {
            switch (mDiskSize) {
                case DiskSizeValue.Flop525_114:
                    return 35 * 13 * SECTOR_SIZE;
                case DiskSizeValue.Flop525_140:
                    return 35 * 16 * SECTOR_SIZE;
                case DiskSizeValue.Flop525_160:
                    return 40 * 16 * SECTOR_SIZE;
                case DiskSizeValue.Flop35_400:
                    return 400 * 1024;
                case DiskSizeValue.Flop35_800:
                    return 800 * 1024;
                case DiskSizeValue.Flop35_1440:
                    return 1440 * 1024;
                case DiskSizeValue.Other_32MB:
                    return 32 * 1024 * 1024;
                case DiskSizeValue.Other_Custom:
                    return StringToValue.SizeToBytes(mCustomSizeText, true);
                default:
                    throw new NotImplementedException("Didn't handle size=" + mDiskSize);
            }
        }

        /// <summary>
        /// Calculates the number of blocks for the selected disk size item.
        /// </summary>
        /// <param name="blocks">Result: block count.</param>
        /// <returns>True on success, false if the item doesn't have blocks or is an invalid
        /// custom specifier.</returns>
        private bool GetNumBlocks(out uint blocks) {
            long volSize = GetVolSize();
            if (volSize < 0 || volSize % BLOCK_SIZE != 0) {
                blocks = 0;
                return false;
            }
            blocks = (uint)(volSize / BLOCK_SIZE);
            if (blocks * (long)BLOCK_SIZE != volSize) {
                return false;       // overflow
            }
            return true;
        }

        /// <summary>
        /// Calculates the number of tracks/sectors for the selected disk size item.
        /// </summary>
        /// <param name="tracks">Result: track count.</param>
        /// <param name="sectors">Result: sector count.</param>
        /// <returns>True on success, false if the item doesn't have sectors or is an invalid
        /// custom specifier.</returns>
        private bool GetNumTracksSectors(out uint tracks, out uint sectors) {
            switch (mDiskSize) {
                case DiskSizeValue.Flop525_114:
                    tracks = 35;
                    sectors = 13;
                    return true;
                case DiskSizeValue.Flop525_140:
                    tracks = 35;
                    sectors = 16;
                    return true;
                case DiskSizeValue.Flop525_160:
                    tracks = 40;
                    sectors = 16;
                    return true;
                case DiskSizeValue.Flop35_400:
                case DiskSizeValue.Flop35_800:
                case DiskSizeValue.Flop35_1440:
                case DiskSizeValue.Other_32MB:
                case DiskSizeValue.Other_Custom:
                    long volSize = GetVolSize();
                    if (volSize == 400 * 1024) {
                        // Special case for 400KB embedded volume (e.g. UniDOS).  This should
                        // perhaps be a standard case, but it's rare that somebody would want
                        // to create one stand-alone since nothing supports it.
                        tracks = 50;
                        sectors = 32;
                        return true;
                    } else {
                        tracks = sectors = 0;
                        return false;
                    }
                default:
                    throw new NotImplementedException("Didn't handle size=" + mDiskSize);
            }
        }

        private bool GetMediaKind(out MediaKind kind) {
            switch (mDiskSize) {
                case DiskSizeValue.Flop525_114:
                case DiskSizeValue.Flop525_140:
                case DiskSizeValue.Flop525_160:
                    kind = MediaKind.GCR_525;
                    return true;
                case DiskSizeValue.Flop35_400:
                    kind = MediaKind.GCR_SSDD35;
                    return true;
                case DiskSizeValue.Flop35_800:
                    kind = MediaKind.GCR_DSDD35;
                    return true;
                case DiskSizeValue.Flop35_1440:
                    kind = MediaKind.MFM_DSHD35;
                    return true;
                case DiskSizeValue.Other_32MB:
                case DiskSizeValue.Other_Custom:
                    kind = MediaKind.Unknown;
                    return false;
                default:
                    throw new NotImplementedException("Didn't handle size=" + mDiskSize);
            }
        }

        private bool IsFlop525 {
            get {
                return mDiskSize == DiskSizeValue.Flop525_114 ||
                    mDiskSize == DiskSizeValue.Flop525_140 ||
                    mDiskSize == DiskSizeValue.Flop525_160;
            }
        }
        private bool IsFlop35 {
            get {
                return mDiskSize == DiskSizeValue.Flop35_400 ||
                    mDiskSize == DiskSizeValue.Flop35_800 ||
                    mDiskSize == DiskSizeValue.Flop35_1440;
            }
        }

        public bool IsChecked_Flop525_113 {
            get { return mDiskSize == DiskSizeValue.Flop525_114; }
            set {
                if (value) { mDiskSize = DiskSizeValue.Flop525_114; }
                UpdateControls();
            }
        }
        public bool IsChecked_Flop525_140 {
            get { return mDiskSize == DiskSizeValue.Flop525_140; }
            set {
                if (value) { mDiskSize = DiskSizeValue.Flop525_140; }
                UpdateControls();
            }
        }
        public bool IsChecked_Flop525_160 {
            get { return mDiskSize == DiskSizeValue.Flop525_160; }
            set {
                if (value) { mDiskSize = DiskSizeValue.Flop525_160; }
                UpdateControls();
            }
        }
        public bool IsChecked_Flop35_400 {
            get { return mDiskSize == DiskSizeValue.Flop35_400; }
            set {
                if (value) { mDiskSize = DiskSizeValue.Flop35_400; }
                UpdateControls();
            }
        }
        public bool IsChecked_Flop35_800 {
            get { return mDiskSize == DiskSizeValue.Flop35_800; }
            set {
                if (value) { mDiskSize = DiskSizeValue.Flop35_800; }
                UpdateControls();
            }
        }
        public bool IsChecked_Flop35_1440 {
            get { return mDiskSize == DiskSizeValue.Flop35_1440; }
            set {
                if (value) { mDiskSize = DiskSizeValue.Flop35_1440; }
                UpdateControls();
            }
        }
        public bool IsChecked_Other_32MB {
            get { return mDiskSize == DiskSizeValue.Other_32MB; }
            set {
                if (value) { mDiskSize = DiskSizeValue.Other_32MB; }
                UpdateControls();
            }
        }
        public bool IsChecked_Other_Custom {
            get { return mDiskSize == DiskSizeValue.Other_Custom; }
            set {
                if (value) { mDiskSize = DiskSizeValue.Other_Custom; }
                UpdateControls();
            }
        }

        public Brush SizeDescForeground {
            get { return mSizeDescForeground; }
            set { mSizeDescForeground = value; OnPropertyChanged(); }
        }
        private Brush mSizeDescForeground = SystemColors.WindowTextBrush;

        public Brush SizeLimitForeground {
            get { return mSizeLimitForeground; }
            set { mSizeLimitForeground = value; OnPropertyChanged(); }
        }
        private Brush mSizeLimitForeground = SystemColors.WindowTextBrush;

        public string CustomSizeText {
            get { return mCustomSizeText; }
            set { mCustomSizeText = value; OnPropertyChanged();
                mDiskSize = DiskSizeValue.Other_Custom; UpdateControls(); }
        }
        private string mCustomSizeText;

        #endregion Disk Size

        #region Filesystem

        private FileSystemType mFilesystem;

        /// <summary>
        /// Converts the disk volume number to an integer.
        /// </summary>
        /// <param name="volNum">Result: volume number; will be 0 if conversion fails.</param>
        /// <returns>True on success.</returns>
        private bool GetVolNum(out int volNum) {
            if (!int.TryParse(VolumeNumText, out volNum)) {
                return false;
            }
            return volNum >= 0 && volNum <= 254;
        }

        public bool IsChecked_FS_None {
            get { return mFilesystem == FileSystemType.Unknown; }
            set {
                if (value) { mFilesystem = FileSystemType.Unknown; }
                UpdateControls();
            }
        }
        public bool IsEnabled_FS_None {
            get { return true; }
        }

        public bool IsChecked_FS_DOS {
            get { return mFilesystem == FileSystemType.DOS33; }
            set {
                if (value) { mFilesystem = FileSystemType.DOS33; }
                UpdateControls();
            }
        }
        public bool IsEnabled_FS_DOS {
            get { return DOS.IsSizeAllowed(GetVolSize()) && !IsFlop35; }
        }

        public bool IsChecked_FS_ProDOS {
            get { return mFilesystem == FileSystemType.ProDOS; }
            set {
                if (value) { mFilesystem = FileSystemType.ProDOS; }
                UpdateControls();
            }
        }
        public bool IsEnabled_FS_ProDOS {
            get { return ProDOS.IsSizeAllowed(GetVolSize()); }
        }

        public bool IsChecked_FS_HFS {
            get { return mFilesystem == FileSystemType.HFS; }
            set {
                if (value) { mFilesystem = FileSystemType.HFS; }
                UpdateControls();
            }
        }
        public bool IsEnabled_FS_HFS {
            get { return HFS.IsSizeAllowed(GetVolSize()); }
        }

        public bool IsChecked_FS_Pascal {
            get { return mFilesystem == FileSystemType.Pascal; }
            set {
                if (value) { mFilesystem = FileSystemType.Pascal; }
                UpdateControls();
            }
        }
        public bool IsEnabled_FS_Pascal {
            get { return Pascal.IsSizeAllowed(GetVolSize()); }
        }

        public bool IsChecked_FS_CPM {
            get { return mFilesystem == FileSystemType.CPM; }
            set {
                if (value) { mFilesystem = FileSystemType.CPM; }
                UpdateControls();
            }
        }
        public bool IsEnabled_FS_CPM {
            get {   // TODO
                long volSize = GetVolSize();
                return volSize == 140 * 1024 || volSize == 800 * 1024;
            }
        }

        public Brush VolNameSyntaxForeground {
            get { return mVolNameSyntaxForeground; }
            set { mVolNameSyntaxForeground = value; OnPropertyChanged(); }
        }
        private Brush mVolNameSyntaxForeground = SystemColors.WindowTextBrush;

        public string VolumeNameText {
            get { return mVolumeNameText; }
            set { mVolumeNameText = value; OnPropertyChanged(); UpdateControls(); }
        }
        private string mVolumeNameText;

        public Brush VolNumSyntaxForeground {
            get { return mVolNumSyntaxForeground; }
            set { mVolNumSyntaxForeground = value; OnPropertyChanged(); }
        }
        private Brush mVolNumSyntaxForeground = SystemColors.WindowTextBrush;

        public string VolumeNumText {
            get { return mVolumeNumText; }
            set { mVolumeNumText = value; OnPropertyChanged(); UpdateControls(); }
        }
        private string mVolumeNumText;

        public bool IsChecked_MakeBootable {
            get { return mIsChecked_MakeBootable; }
            set { mIsChecked_MakeBootable = value; OnPropertyChanged(); }
        }
        private bool mIsChecked_MakeBootable;

        #endregion Filesystem

        #region File Type

        // Interleave for WOZ 3.5" floppy disks.  Assume 4:1 since WOZ is an Apple II format.
        private const int WOZ_IL_35 = 4;

        public enum FileTypeValue {
            Unknown = 0,
            DOSSector,
            ProDOSBlock,
            TwoIMG,
            DiskCopy42,
            NuFX,
            Woz,
            Nib,
            Trackstar
        }
        private FileTypeValue mFileType;

        public bool IsChecked_FT_DOSSector {
            get { return mFileType == FileTypeValue.DOSSector; }
            set {
                if (value) { mFileType = FileTypeValue.DOSSector; }
                UpdateControls();
            }
        }
        public bool IsEnabled_FT_DOSSector {
            get {
                if (!GetNumTracksSectors(out uint tracks, out uint sectors)) {
                    return false;
                }
                if (!UnadornedSector.CanCreateSectorImage(tracks, sectors, SectorOrder.DOS_Sector,
                        out string errMsg)) {
                    return false;
                }
                return true;
            }
        }

        public bool IsChecked_FT_ProDOSBlock {
            get { return mFileType == FileTypeValue.ProDOSBlock; }
            set {
                if (value) { mFileType = FileTypeValue.ProDOSBlock; }
                UpdateControls();
            }
        }
        public bool IsEnabled_FT_ProDOSBlock {
            get {
                if (!GetNumBlocks(out uint blocks)) {
                    return false;
                }
                if (!UnadornedSector.CanCreateBlockImage(blocks, out string errMsg)) {
                    return false;
                }
                return true;
            }
        }

        public bool IsChecked_FT_TwoIMG {
            get { return mFileType == FileTypeValue.TwoIMG; }
            set {
                if (value) { mFileType = FileTypeValue.TwoIMG; }
                UpdateControls();
            }
        }
        public bool IsEnabled_FT_TwoIMG {
            get {
                if (!GetNumBlocks(out uint blocks)) {
                    return false;
                }
                if (!TwoIMG.CanCreateProDOSBlockImage(blocks, out string errMsg)) {
                    return false;
                }
                return true;
            }
        }

        public bool IsChecked_FT_NuFX {
            get { return mFileType == FileTypeValue.NuFX; }
            set {
                if (value) { mFileType = FileTypeValue.NuFX; }
                UpdateControls();
            }
        }
        public bool IsEnabled_FT_NuFX {
            get {
                if (!GetNumBlocks(out uint blocks)) {
                    return false;
                }
                // Limit ourselves to 140KB and 800KB floppies, since that's what utilities
                // on the Apple II will be expecting.
                return blocks == 280 || blocks == 1600;
            }
        }

        public bool IsChecked_FT_DiskCopy42 {
            get { return mFileType == FileTypeValue.DiskCopy42; }
            set {
                if (value) { mFileType = FileTypeValue.DiskCopy42; }
                UpdateControls();
            }
        }
        public bool IsEnabled_FT_DiskCopy42 {
            get {
                if (!GetNumBlocks(out uint blocks)) {
                    return false;
                }
                if (!GetMediaKind(out MediaKind kind)) {
                    return false;
                }
                return DiskCopy.CanCreateDisk(kind, out string errMsg);
            }
        }

        public bool IsChecked_FT_Woz {
            get { return mFileType == FileTypeValue.Woz; }
            set {
                if (value) { mFileType = FileTypeValue.Woz; }
                UpdateControls();
            }
        }
        public bool IsEnabled_FT_Woz {
            get {
                if (IsFlop525) {
                    if (!GetNumTracksSectors(out uint tracks, out uint sectors)) {
                        return false;
                    }
                    return Woz.CanCreateDisk525(tracks, out string errMsg);
                } else {
                    if (!GetMediaKind(out MediaKind kind)) {
                        return false;
                    }
                    return Woz.CanCreateDisk35(kind, WOZ_IL_35, out string errMsg);
                }
            }
        }

        public bool IsChecked_FT_Nib {
            get { return mFileType == FileTypeValue.Nib; }
            set {
                if (value) { mFileType = FileTypeValue.Nib; }
                UpdateControls();
            }
        }
        public bool IsEnabled_FT_Nib {
            get {
                if (!GetNumTracksSectors(out uint tracks, out uint sectors)) {
                    return false;
                }
                return tracks == 35;
            }
        }

        public bool IsChecked_FT_Trackstar {
            get { return mFileType == FileTypeValue.Trackstar; }
            set {
                if (value) { mFileType = FileTypeValue.Trackstar; }
                UpdateControls();
            }
        }
        public bool IsEnabled_FT_Trackstar {
            get {
                if (!GetNumTracksSectors(out uint tracks, out uint sectors)) {
                    return false;
                }
                return Trackstar.CanCreateDisk(tracks, sectors, out string errMsg);
            }
        }

        #endregion File Type
    }
}
