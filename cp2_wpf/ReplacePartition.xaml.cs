﻿/*
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

using AppCommon;
using CommonUtil;
using DiskArc;
using DiskArc.Arc;
using DiskArc.Disk;
using DiskArc.Multi;
using static DiskArc.Defs;


namespace cp2_wpf {
    /// <summary>
    /// Replace a partition with the contents of a disk image file.
    /// </summary>
    public partial class ReplacePartition : Window, INotifyPropertyChanged {
        // INotifyPropertyChanged implementation
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string propertyName = "") {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private Brush mDefaultLabelColor = SystemColors.WindowTextBrush;
        private Brush mErrorLabelColor = Brushes.Red;

        public bool IsValid {
            get { return mIsValid; }
            set { mIsValid = value; OnPropertyChanged(); }
        }
        private bool mIsValid;

        public string DstSizeText {
            get { return mDstSizeText; }
            set { mDstSizeText = value; OnPropertyChanged(); }
        }
        private string mDstSizeText;

        public string SrcSizeText {
            get { return mSrcSizeText; }
            set { mSrcSizeText = value; OnPropertyChanged(); }
        }
        private string mSrcSizeText;

        public string SizeDiffText {
            get { return mSizeDiffText; }
            set { mSizeDiffText = value; OnPropertyChanged(); }
        }
        private string mSizeDiffText;

        public Brush SizeDiffForeground {
            get { return mSizeDiffForeground; }
            set { mSizeDiffForeground = value; OnPropertyChanged(); }
        }
        private Brush mSizeDiffForeground = SystemColors.WindowTextBrush;

        public Visibility SizeDiffVisibility {
            get { return mSizeDiffVisibility; }
            set { mSizeDiffVisibility = value; OnPropertyChanged(); }
        }
        private Visibility mSizeDiffVisibility;

        public delegate bool EnableWriteFunc();
        private EnableWriteFunc mEnableWriteFunc;

        private Partition mDstPartition;
        private IChunkAccess mSrcChunks;
        private AppHook mAppHook;


        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="owner">Parent window.</param>
        /// <param name="dstPartition">Partition to be overwritten.</param>
        /// <param name="srcChunks">Chunk data source.</param>
        /// <param name="enableWriteFunc">Function to call to enable writes.</param>
        /// <param name="formatter">Text formatter.</param>
        /// <param name="appHook">Application hook reference.</param>
        public ReplacePartition(Window owner, Partition dstPartition, IChunkAccess srcChunks,
                EnableWriteFunc enableWriteFunc, Formatter formatter, AppHook appHook) {
            InitializeComponent();
            Owner = owner;
            DataContext = this;

            mDstPartition = dstPartition;
            mSrcChunks = srcChunks;
            mEnableWriteFunc = enableWriteFunc;
            mAppHook = appHook;

            IChunkAccess dstChunks = dstPartition.ChunkAccess;
            if (dstChunks.HasSectors) {
                mDstSizeText =
                    string.Format("Destination partition has {0} tracks of {1} sectors ({2}).",
                        dstChunks.NumTracks, dstChunks.NumSectorsPerTrack,
                        formatter.FormatSizeOnDisk(dstChunks.FormattedLength, KBLOCK_SIZE));
            } else if (dstChunks.HasBlocks) {
                mDstSizeText =
                    string.Format("Destination partition has {0} blocks ({1}).",
                        dstChunks.FormattedLength / BLOCK_SIZE,
                        formatter.FormatSizeOnDisk(dstChunks.FormattedLength, KBLOCK_SIZE));
            } else {
                throw new NotImplementedException();
            }

            if (srcChunks.HasSectors) {
                mSrcSizeText =
                    string.Format("Source image has {0} tracks of {1} sectors ({2}).",
                        srcChunks.NumTracks, srcChunks.NumSectorsPerTrack,
                        formatter.FormatSizeOnDisk(srcChunks.FormattedLength, KBLOCK_SIZE));
            } else {
                mSrcSizeText =
                    string.Format("Source image has {0} blocks ({1}).",
                        srcChunks.FormattedLength / BLOCK_SIZE,
                        formatter.FormatSizeOnDisk(srcChunks.FormattedLength, KBLOCK_SIZE));
            }

            mIsValid = false;
            if (!(srcChunks.HasSectors && dstChunks.HasSectors) &&
                    !(srcChunks.HasBlocks && dstChunks.HasBlocks)) {
                // This should only happen if we're trying to load a sector-only disk image,
                // such as a .d13, into a partition.
                // It currently also happens if we try to load a block-only image into a
                // space occupied by an embedded volume.  In theory we should allow it, but
                // the library code doesn't currently handle it.
                mSizeDiffText = "Source and destination are not compatible (blocks vs. sectors).";
            } else if (srcChunks.HasSectors && dstChunks.HasSectors &&
                    srcChunks.NumSectorsPerTrack != dstChunks.NumSectorsPerTrack) {
                // Trying to load a 13-sector disk into a 16-sector volume, or a 16-sector disk
                // into a 32-sector embedded volume.
                mSizeDiffText = "Source and destination have differing sectors per track.";
            } else {
                if (srcChunks.FormattedLength == dstChunks.FormattedLength) {
                    mSizeDiffText = string.Empty;
                    mSizeDiffVisibility = Visibility.Collapsed;
                    mIsValid = true;
                } else if (srcChunks.FormattedLength < dstChunks.FormattedLength) {
                    mSizeDiffText = "NOTE: the source image is smaller than the destination. " +
                        "The leftover space will likely be unusable.";
                    mIsValid = true;
                } else {
                    // Not allowed.
                    mSizeDiffText = "The source image is larger than the destination.";
                }
            }
            if (!mIsValid) {
                mSizeDiffForeground = mErrorLabelColor;
            }

            // TODO: warn about DOS hybrids?
        }

        private void CopyButton_Click(object sender, RoutedEventArgs e) {
            if (!mEnableWriteFunc()) {
                MessageBox.Show(this, "Unable to prepare partition for writing", "Failed",
                    MessageBoxButton.OK, MessageBoxImage.Hand);
                return;
            }

            SaveAsDisk.CopyDisk(mSrcChunks, mDstPartition.ChunkAccess, out int errorCount);
            if (errorCount != 0) {
                string msg = "Some data could not be read. Total errors: " + errorCount + ".";
                MessageBox.Show(msg, "Partial Copy", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            DialogResult = true;
        }
    }
}
