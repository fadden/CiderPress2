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
using System.Runtime.CompilerServices;
using System.Windows;

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

        /// <summary>
        /// True if the configuration is valid.  This will be false if the free-entry fields
        /// (custom size, volume name, volume number) are invalid.
        /// </summary>
        public bool IsValid {
            get { return mIsValid; }
            set { mIsValid = value; OnPropertyChanged(); }
        }
        private bool mIsValid;



        public CreateDiskImage(Window owner) {
            InitializeComponent();
            Owner = owner;
            DataContext = this;

            mIsValid = false;

            mDiskSize = DiskSizeValue.Flop525_140;      // TODO: from settings
            mFilesystem = FilesystemValue.ProDOS;
            mFileType = FileTypeValue.ProDOSBlock;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e) {
            DialogResult = true;
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
            nameof(IsEnabled_FT_Woz),
            nameof(IsChecked_FT_Woz),
            nameof(IsEnabled_FT_Nib),
            nameof(IsChecked_FT_Nib),
            nameof(IsEnabled_FT_Trackstar),
            nameof(IsChecked_FT_Trackstar),
        };

        private void UpdateControls() {
            // Check filesystem.
            bool needNewFs = false;
            switch (mFilesystem) {
                case FilesystemValue.DOS:
                    needNewFs = !IsEnabled_FS_DOS;
                    break;
                case FilesystemValue.ProDOS:
                    needNewFs = !IsEnabled_FS_ProDOS;
                    break;
                case FilesystemValue.HFS:
                    needNewFs = !IsEnabled_FS_HFS;
                    break;
                case FilesystemValue.Pascal:
                    needNewFs = !IsEnabled_FS_Pascal;
                    break;
                case FilesystemValue.CPM:
                    needNewFs = !IsEnabled_FS_CPM;
                    break;
                case FilesystemValue.None:
                    break;
                default:
                    Debug.Assert(false);
                    break;
            }
            if (needNewFs) {
                if (IsEnabled_FS_DOS) {
                    mFilesystem = FilesystemValue.DOS;
                } else if (IsEnabled_FS_ProDOS) {
                    mFilesystem = FilesystemValue.ProDOS;
                } else if (IsEnabled_FS_HFS) {
                    mFilesystem = FilesystemValue.HFS;
                } else if (IsEnabled_FS_Pascal) {
                    mFilesystem = FilesystemValue.Pascal;
                } else if (IsEnabled_FS_CPM) {
                    mFilesystem = FilesystemValue.CPM;
                } else {
                    mFilesystem = FilesystemValue.None;
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
                } else if (IsEnabled_FT_Nib) {
                    mFileType = FileTypeValue.Nib;
                } else if (IsEnabled_FT_Trackstar) {
                    mFileType = FileTypeValue.Trackstar;
                } else {
                    Debug.Assert(false);
                }
            }

            // Broadcast updates for all controls.
            foreach (string propName in sPropList) {
                OnPropertyChanged(propName);
            }
        }

        #region Disk Size

        public enum DiskSizeValue {
            Unknown = 0,
            Flop525_113, Flop525_140, Flop525_160,
            Flop35_400, Flop35_800, Flop35_1440,
            Other_32MB, Other_Custom
        }
        public DiskSizeValue mDiskSize;

        private long GetVolSize() {
            switch (mDiskSize) {
                case DiskSizeValue.Flop525_113:
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
                    Debug.Assert(false, "TODO");
                    return 0;
                default:
                    throw new NotImplementedException("Didn't handle size=" + mDiskSize);
            }
        }

        private bool GetBlocks(out uint blocks) {
            long volSize = GetVolSize();
            if (volSize % BLOCK_SIZE != 0) {
                blocks = 0;
                return false;
            }
            blocks = (uint)volSize / BLOCK_SIZE;
            if (blocks * BLOCK_SIZE != volSize) {
                return false;       // overflow
            }
            return true;
        }

        private bool GetTracksSectors(out uint tracks, out uint sectors) {
            switch (mDiskSize) {
                case DiskSizeValue.Flop525_113:
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
                    tracks = sectors = 0;
                    return false;
                default:
                    throw new NotImplementedException("Didn't handle size=" + mDiskSize);
            }
        }

        private bool GetMediaKind(out MediaKind kind) {
            switch (mDiskSize) {
                case DiskSizeValue.Flop525_113:
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
                return mDiskSize == DiskSizeValue.Flop525_113 ||
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
            get { return mDiskSize == DiskSizeValue.Flop525_113; }
            set {
                if (value) { mDiskSize = DiskSizeValue.Flop525_113; }
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

        #endregion Disk Size

        #region Filesystem

        public enum FilesystemValue {
            Unknown = 0,
            None, DOS, ProDOS, HFS, Pascal, CPM
        }

        private FilesystemValue mFilesystem;

        public bool IsChecked_FS_None {
            get { return mFilesystem == FilesystemValue.None; }
            set {
                if (value) { mFilesystem = FilesystemValue.None; }
                UpdateControls();
            }
        }
        public bool IsEnabled_FS_None {
            get { return true; }
        }

        public bool IsChecked_FS_DOS {
            get { return mFilesystem == FilesystemValue.DOS; }
            set {
                if (value) { mFilesystem = FilesystemValue.DOS; }
                UpdateControls();
            }
        }
        public bool IsEnabled_FS_DOS {
            get { return DOS.IsSizeAllowed(GetVolSize()) && !IsFlop35; }
        }

        public bool IsChecked_FS_ProDOS {
            get { return mFilesystem == FilesystemValue.ProDOS; }
            set {
                if (value) { mFilesystem = FilesystemValue.ProDOS; }
                UpdateControls();
            }
        }
        public bool IsEnabled_FS_ProDOS {
            get { return ProDOS.IsSizeAllowed(GetVolSize()); }
        }

        public bool IsChecked_FS_HFS {
            get { return mFilesystem == FilesystemValue.HFS; }
            set {
                if (value) { mFilesystem = FilesystemValue.HFS; }
                UpdateControls();
            }
        }
        public bool IsEnabled_FS_HFS {
            get { return HFS.IsSizeAllowed(GetVolSize()); }
        }

        public bool IsChecked_FS_Pascal {
            get { return mFilesystem == FilesystemValue.Pascal; }
            set {
                if (value) { mFilesystem = FilesystemValue.Pascal; }
                UpdateControls();
            }
        }
        public bool IsEnabled_FS_Pascal {
            get {   // TODO
                long volSize = GetVolSize();
                return volSize == 140 * 1024 || volSize == 800 * 1024;
            }
        }

        public bool IsChecked_FS_CPM {
            get { return mFilesystem == FilesystemValue.CPM; }
            set {
                if (value) { mFilesystem = FilesystemValue.CPM; }
                UpdateControls();
            }
        }
        public bool IsEnabled_FS_CPM {
            get {   // TODO
                long volSize = GetVolSize();
                return volSize == 140 * 1024 || volSize == 800 * 1024;
            }
        }

        #endregion Filesystem

        #region File Type

        public enum FileTypeValue {
            Unknown = 0,
            DOSSector,
            ProDOSBlock,
            TwoIMG,
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
                if (!GetTracksSectors(out uint tracks, out uint sectors)) {
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
                if (!GetBlocks(out uint blocks)) {
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
                if (!GetBlocks(out uint blocks)) {
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
                if (!GetBlocks(out uint blocks)) {
                    return false;
                }
                // Limit ourselves to 140KB and 800KB floppies, since that's what utilities
                // on the Apple II will be expecting.
                return blocks == 280 || blocks == 1600;
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
                    if (!GetTracksSectors(out uint tracks, out uint sectors)) {
                        return false;
                    }
                    return Woz.CanCreateDisk525(tracks, out string errMsg);
                } else {
                    if (!GetMediaKind(out MediaKind kind)) {
                        return false;
                    }
                    // Assume 4:1 interleave, since WOZ is an Apple II format.
                    return Woz.CanCreateDisk35(kind, 4, out string errMsg);
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
                if (!GetTracksSectors(out uint tracks, out uint sectors)) {
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
                if (!GetTracksSectors(out uint tracks, out uint sectors)) {
                    return false;
                }
                return tracks == 35 || tracks == 40;
            }
        }

        #endregion File Type
    }
}
