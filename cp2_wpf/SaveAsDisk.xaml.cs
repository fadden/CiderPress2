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
using static cp2_wpf.CreateDiskImage;
using DiskArc.Multi;
using System.Windows.Controls.Primitives;

namespace cp2_wpf
{
    /// <summary>
    /// Interaction logic for SaveAsDisk.xaml
    /// </summary>
    public partial class SaveAsDisk : Window, INotifyPropertyChanged {
        // INotifyPropertyChanged implementation
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string propertyName = "") {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// Pathname of created file.
        /// </summary>
        public string PathName { get; private set; } = string.Empty;

        public string SourceSizeText { get; private set; } = string.Empty;

        private object mDiskOrPartition;
        private Formatter mFormatter;
        private AppHook mAppHook;


        public SaveAsDisk(Window owner, object diskOrPartition, Formatter formatter,
                AppHook appHook) {
            InitializeComponent();

            Owner = owner;
            DataContext = this;

            mDiskOrPartition = diskOrPartition;
            mFormatter = formatter;
            mAppHook = appHook;

            IChunkAccess? chunks = GetChunkAccess();
            if (chunks == null) {
                // TODO: test this; too early?
                MessageBox.Show(this, "Unable to identify disk sector format.", "Problem",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                DialogResult = false;
                return;
            }

            // TODO: get initial setting from settings

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
                Debug.Assert(false);
                mFileType = FileTypeValue.ProDOSBlock;
            }

            string type;
            if (diskOrPartition is IDiskImage) {
                type = "disk image";
            } else {
                type = "partition";
            }
            SourceSizeText = "Source " + type + " size is " +
                formatter.FormatSizeOnDisk(chunks.FormattedLength, 1024);
        }

        private void OkButton_Click(object sender, RoutedEventArgs e) {
            if (!CreateImage()) {
                return;
            }

            DialogResult = true;
        }

        private bool CreateImage() {
            Debug.WriteLine("CREATE!");
            // TODO:
            // - get name of file to save to
            // - create type-specific file
            // - copy sectors if available, blocks otherwise
            // - report failed reads
            //
            // - save radio button setting
            return true;
        }

        /// <summary>
        /// Returns the chunk access object from the disk image or partition.  The object is
        /// likely read-only due to an open filesystem.
        /// </summary>
        private IChunkAccess? GetChunkAccess() {
            if (mDiskOrPartition is IDiskImage) {
                return ((IDiskImage)mDiskOrPartition).ChunkAccess;
            } else {
                return ((Partition)mDiskOrPartition).ChunkAccess;
            }
        }

        private bool GetNumBlocks(out uint blocks) {
            IChunkAccess chunks = GetChunkAccess()!;

            if (chunks.HasBlocks) {
                blocks = (uint)(chunks.FormattedLength / BLOCK_SIZE);
                return true;
            } else {
                blocks = 0;
                return false;
            }
        }

        private bool GetNumTracksSectors(out uint tracks, out uint sectors) {
            IChunkAccess chunks = GetChunkAccess()!;

            if (chunks.HasSectors) {
                tracks = chunks.NumTracks;
                sectors = chunks.NumSectorsPerTrack;
                return true;
            } else {
                tracks = sectors = 0;
                return false;
            }
        }

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
            }
        }
        public bool IsEnabled_FT_DOSSector {
            get {
                if (GetNumTracksSectors(out uint tracks, out uint sectors)) {
                    return UnadornedSector.CanCreateSectorImage(tracks, sectors,
                        SectorOrder.DOS_Sector, out string errMsg);
                } else {
                    return false;
                }
            }
        }

        public bool IsChecked_FT_ProDOSBlock {
            get { return mFileType == FileTypeValue.ProDOSBlock; }
            set {
                if (value) { mFileType = FileTypeValue.ProDOSBlock; }
            }
        }
        public bool IsEnabled_FT_ProDOSBlock {
            get {
                if (GetNumTracksSectors(out uint tracks, out uint sectors)) {
                    return UnadornedSector.CanCreateSectorImage(tracks, sectors,
                        SectorOrder.ProDOS_Block, out string errMsg);
                } else if (GetNumBlocks(out uint blocks)) {
                    return UnadornedSector.CanCreateBlockImage(blocks, out string errMsg);
                } else {
                    return false;
                }
            }
        }

        public bool IsChecked_FT_TwoIMG {
            get { return mFileType == FileTypeValue.TwoIMG; }
            set {
                if (value) { mFileType = FileTypeValue.TwoIMG; }
            }
        }
        public bool IsEnabled_FT_TwoIMG {
            get {
                if (GetNumTracksSectors(out uint tracks, out uint sectors)) {
                    return TwoIMG.CanCreateDOSSectorImage(tracks, out string errMsg);
                } else if (GetNumBlocks(out uint blocks)) {
                    return TwoIMG.CanCreateProDOSBlockImage(blocks, out string errMsg);
                } else {
                    return false;
                }
            }
        }

        public bool IsChecked_FT_NuFX {
            get { return mFileType == FileTypeValue.NuFX; }
            set {
                if (value) { mFileType = FileTypeValue.NuFX; }
            }
        }
        public bool IsEnabled_FT_NuFX {
            get {
                // Limit ourselves to 140KB and 800KB floppies, since that's what utilities
                // on the Apple II will be expecting.
                if (GetNumTracksSectors(out uint tracks, out uint sectors)) {
                    return tracks == 35 && sectors == 16;
                } else if (GetNumBlocks(out uint blocks)) {
                    return blocks == 1600;
                } else {
                    return false;
                }
            }
        }

        public bool IsChecked_FT_DiskCopy42 {
            get { return mFileType == FileTypeValue.DiskCopy42; }
            set {
                if (value) { mFileType = FileTypeValue.DiskCopy42; }
            }
        }
        public bool IsEnabled_FT_DiskCopy42 {
            get {
                if (!GetNumBlocks(out uint blocks)) {
                    return false;
                }
                return blocks == 1600;
            }
        }

        public bool IsChecked_FT_Woz {
            get { return mFileType == FileTypeValue.Woz; }
            set {
                if (value) { mFileType = FileTypeValue.Woz; }
            }
        }
        public bool IsEnabled_FT_Woz {
            get {
                if (GetNumTracksSectors(out uint tracks, out uint sectors)) {
                    if (sectors != 13 && sectors != 16) {
                        return false;       // can't handle 32-sector disks
                    }
                    return Woz.CanCreateDisk525(tracks, out string errMsg);
                } else if (GetNumBlocks(out uint blocks)) {
                    if (blocks == 800) {
                        return Woz.CanCreateDisk35(MediaKind.GCR_SSDD35, WOZ_IL_35,
                            out string errMsg);
                    } else if (blocks == 1600) {
                        return Woz.CanCreateDisk35(MediaKind.GCR_DSDD35, WOZ_IL_35,
                            out string errMsg);
                    } else {
                        return false;
                    }
                } else {
                    return false;
                }
            }
        }

        public bool IsChecked_FT_Nib {
            get { return mFileType == FileTypeValue.Nib; }
            set {
                if (value) { mFileType = FileTypeValue.Nib; }
            }
        }
        public bool IsEnabled_FT_Nib {
            get {
                if (!GetNumTracksSectors(out uint tracks, out uint sectors)) {
                    return false;
                }
                return tracks == 35 && (sectors == 13 || sectors == 16);
            }
        }

        public bool IsChecked_FT_Trackstar {
            get { return mFileType == FileTypeValue.Trackstar; }
            set {
                if (value) { mFileType = FileTypeValue.Trackstar; }
            }
        }
        public bool IsEnabled_FT_Trackstar {
            get {
                if (!GetNumTracksSectors(out uint tracks, out uint sectors)) {
                    return false;
                }
                return (tracks == 35 || tracks == 40) && (sectors == 13 || sectors == 16);
            }
        }
    }
}
