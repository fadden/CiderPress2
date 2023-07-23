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

using AppCommon;
using CommonUtil;
using DiskArc;
using DiskArc.Arc;
using DiskArc.Disk;
using static DiskArc.Defs;
using FileTypeValue = cp2_wpf.CreateDiskImage.FileTypeValue;

namespace cp2_wpf {
    /// <summary>
    /// Save a disk image or partition as a disk image.
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

        private IChunkAccess mChunks;
        private AppHook mAppHook;


        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="owner">Parent window.</param>
        /// <param name="diskOrPartition">IDiskImage or Partition object.</param>
        /// <param name="chunks">Chunk access object.</param>
        /// <param name="formatter">Text formatter.</param>
        /// <param name="appHook">Application hook reference.</param>
        public SaveAsDisk(Window owner, object diskOrPartition, IChunkAccess chunks,
                Formatter formatter, AppHook appHook) {
            InitializeComponent();
            Owner = owner;
            DataContext = this;

            mChunks = chunks;
            mAppHook = appHook;

            SetDefaultFileType();

            string type;
            if (diskOrPartition is IDiskImage) {
                type = "disk image";
            } else {
                type = "partition";
            }
            SourceSizeText = "Source " + type + " size is " +
                formatter.FormatSizeOnDisk(chunks.FormattedLength, 1024);
        }

        /// <summary>
        /// Gets the default value from settings, then fixes it if it's not allowed.
        /// </summary>
        private void SetDefaultFileType() {
            mFileType = AppSettings.Global.GetEnum(AppSettings.SAVE_DISK_FILE_TYPE,
                FileTypeValue.ProDOSBlock);

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
            if (!needNewType) {
                return;
            }

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
        }

        private void OkButton_Click(object sender, RoutedEventArgs e) {
            if (!CreateImage()) {
                return;
            }

            DialogResult = true;

            AppSettings.Global.SetEnum(AppSettings.SAVE_DISK_FILE_TYPE, mFileType);
        }

        /// <summary>
        /// Creates a new disk image file and copies the data into it.
        /// </summary>
        /// <returns>True on success.</returns>
        private bool CreateImage() {
            uint blocks, tracks, sectors;

            bool is13Sector = GetNumTracksSectors(out tracks, out sectors) && sectors == 13;
            string pathName = CreateDiskImage.SelectOutputFile(mFileType, is13Sector);
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

            IDiskImage? diskImage = null;
            MemoryStream? tmpStream = null;
            int errorCount;

            // Create a disk image in the output stream.
            try {
                Mouse.OverrideCursor = Cursors.Wait;

                SectorCodec codec;

                int volNum = Defs.DEFAULT_525_VOLUME_NUM;

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
                        tmpStream = new MemoryStream();
                        if (GetNumTracksSectors(out tracks, out sectors)) {
                            Debug.Assert(tracks == 35 && sectors == 16);
                            diskImage = UnadornedSector.CreateSectorImage(tmpStream, tracks,
                                sectors, SectorOrder.ProDOS_Block, mAppHook);
                        } else if (GetNumBlocks(out blocks)) {
                            Debug.Assert(blocks == 1600);
                            diskImage = UnadornedSector.CreateBlockImage(tmpStream, blocks,
                                mAppHook);
                        } else {
                            throw new Exception("internal error");
                        }
                        break;
                    case FileTypeValue.Woz:
                        if (GetNumTracksSectors(out tracks, out sectors)) {
                            codec = (sectors == 13) ?
                                StdSectorCodec.GetCodec(StdSectorCodec.CodecIndex525.Std_525_13) :
                                StdSectorCodec.GetCodec(StdSectorCodec.CodecIndex525.Std_525_16);
                            diskImage = Woz.CreateDisk525(stream, tracks, codec, (byte)volNum,
                                mAppHook);
                        } else {
                            if (!GetMediaKind(out MediaKind kind)) {
                                throw new Exception("internal error");
                            }
                            codec = StdSectorCodec.GetCodec(StdSectorCodec.CodecIndex35.Std_35);
                            diskImage = Woz.CreateDisk35(stream, kind, WOZ_IL_35, codec,
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
                    case FileTypeValue.DiskCopy42:
                    case FileTypeValue.Trackstar:
                        throw new Exception("not yet");
                    default:
                        throw new NotImplementedException("Not implemented: " + mFileType);
                }

                // Do the disk copy.  In theory this could be a few GB from a physical device,
                // but it will generally be pretty small.  Doing it on the GUI thread should
                // be fine for now.
                CopyDisk(mChunks, diskImage.ChunkAccess!, out errorCount);

                // Handle NuFX ".SDK" disk image creation.
                if (mFileType == FileTypeValue.NuFX) {
                    Debug.Assert(tmpStream != null);

                    NuFX archive = NuFX.CreateArchive(mAppHook);
                    archive.StartTransaction();
                    IFileEntry entry = archive.CreateRecord();
                    entry.FileName = "DISK";
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
                if (diskImage != null) {
                    diskImage.Dispose();
                }
                if (stream != null) {
                    stream.Close();
                }
                Mouse.OverrideCursor = null;
            }

            if (errorCount != 0) {
                string msg = "Some data could not be read. Total errors: " + errorCount + ".";
                MessageBox.Show(msg, "Partial Copy", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            return true;
        }

        /// <summary>
        /// Copies all tracks/sectors or all blocks from the source to the destination.  It's
        /// okay if the source is smaller.  Copying by sectors is preferred.  Unreadable sectors
        /// are copied as zeroes.
        /// </summary>
        /// <param name="srcChunks">Source chunk access object.</param>
        /// <param name="dstChunks">Destination chunk access object.</param>
        /// <param name="errorCount">Result: number of errors encountered.</param>
        internal static void CopyDisk(IChunkAccess srcChunks, IChunkAccess dstChunks,
                out int errorCount) {
            Debug.Assert(!dstChunks.IsReadOnly);
            if (srcChunks.FormattedLength > dstChunks.FormattedLength) {
                throw new Exception("Internal error: src size exceeds dst");
            }

            errorCount = 0;
            if (srcChunks.HasSectors && dstChunks.HasSectors) {
                Debug.Assert(srcChunks.NumTracks == dstChunks.NumTracks);
                Debug.Assert(srcChunks.NumSectorsPerTrack == dstChunks.NumSectorsPerTrack);

                byte[] copyBuf = new byte[SECTOR_SIZE];

                for (uint trk = 0; trk < srcChunks.NumTracks; trk++) {
                    for (uint sct = 0; sct < srcChunks.NumSectorsPerTrack; sct++) {
                        try {
                            srcChunks.ReadSector(trk, sct, copyBuf, 0);
                        } catch (IOException) {
                            Debug.WriteLine("Failed reading T" + trk + " S" + sct);
                            // Continue, but write a zeroed-out buffer.
                            RawData.MemSet(copyBuf, 0, SECTOR_SIZE, 0x00);
                            errorCount++;
                        }

                        // We just created this disk, so writes shouldn't fail.
                        dstChunks.WriteSector(trk, sct, copyBuf, 0);
                    }
                }
            } else if (srcChunks.HasBlocks && dstChunks.HasBlocks) {
                byte[] copyBuf = new byte[BLOCK_SIZE];
                uint numBlocks = (uint)(srcChunks.FormattedLength / BLOCK_SIZE);
                for (uint block = 0; block < numBlocks; block++) {
                    try {
                        srcChunks.ReadBlock(block, copyBuf, 0);
                    } catch (IOException) {
                        Debug.WriteLine("Failed reading block " + block);
                        // Continue, but write a zeroed-out buffer.
                        RawData.MemSet(copyBuf, 0, BLOCK_SIZE, 0x00);
                        errorCount++;
                    }

                    dstChunks.WriteBlock(block, copyBuf, 0);
                }
            } else {
                throw new NotImplementedException();
            }
        }

        private bool GetNumBlocks(out uint blocks) {
            if (mChunks.HasBlocks) {
                blocks = (uint)(mChunks.FormattedLength / BLOCK_SIZE);
                return true;
            } else {
                blocks = 0;
                return false;
            }
        }

        private bool GetNumTracksSectors(out uint tracks, out uint sectors) {
            if (mChunks.HasSectors) {
                tracks = mChunks.NumTracks;
                sectors = mChunks.NumSectorsPerTrack;
                return true;
            } else {
                tracks = sectors = 0;
                return false;
            }
        }

        private bool GetMediaKind(out MediaKind kind) {
            kind = MediaKind.Unknown;
            if (!GetNumBlocks(out uint blocks)) {
                return false;
            }
            if (blocks == 800) {
                kind = MediaKind.GCR_SSDD35;
                return true;
            } else if (blocks == 1600) {
                kind = MediaKind.GCR_DSDD35;
                return true;
            }
            return false;
        }

        // Interleave for WOZ 3.5" floppy disks.  Assume 4:1 since WOZ is an Apple II format.
        private const int WOZ_IL_35 = 4;

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
                    return TwoIMG.CanCreateDOSSectorImage(tracks, sectors, out string errMsg);
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
