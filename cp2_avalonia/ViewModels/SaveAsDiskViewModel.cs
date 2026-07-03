/*
 * Copyright 2026 faddenSoft
 * Copyright 2026 Lydian Scale Software
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
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

using Avalonia.Platform.Storage;

using AppCommon;
using CommonUtil;
using DiskArc;
using DiskArc.Arc;
using DiskArc.Disk;
using static DiskArc.Defs;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using cp2_avalonia.Models;
using cp2_avalonia.Services;

namespace cp2_avalonia.ViewModels;

/// <summary>
/// ViewModel for Save As Disk Image dialog.
/// </summary>
public class SaveAsDiskViewModel : ObservableObject
{
    // -----------------------------------------------------------------------------------------
    // Close interaction

    public event Action<bool>? CloseRequested;

    // -----------------------------------------------------------------------------------------
    // Commands

    public IRelayCommand OkCommand { get; }
    public IRelayCommand CancelCommand { get; }

    // -----------------------------------------------------------------------------------------
    // Output

    /// <summary>Pathname of created file (set on success). Read by caller after dialog closes.</summary>
    public string PathName { get; private set; } = string.Empty;

    // -----------------------------------------------------------------------------------------
    // Domain objects

    private readonly IChunkAccess mChunks;
    private readonly AppHook mAppHook;
    private readonly IFilePickerService _filePickerService;
    private readonly ISettingsService _settingsService;
    private readonly IDialogService _dialogService;

    private DiskImageTypes.FileTypeValue mFileType;

    // Interleave constants
    private const int WOZ_IL_35 = 4;
    private const int MOOF_IL_35 = 2;

    // -----------------------------------------------------------------------------------------
    // Properties

    private string mSourceSizeText = string.Empty;
    public string SourceSizeText {
        get => mSourceSizeText;
        private set => SetProperty(ref mSourceSizeText, value);
    }

    // File type radio button properties
    public bool IsChecked_FT_DOSSector {
        get => mFileType == DiskImageTypes.FileTypeValue.DOSSector;
        set { if (value) { mFileType = DiskImageTypes.FileTypeValue.DOSSector; NotifyAllFTProps(); } }
    }
    public bool IsEnabled_FT_DOSSector {
        get {
            if (GetNumTracksSectors(out uint tracks, out uint sectors)) {
                return UnadornedSector.CanCreateSectorImage(tracks, sectors, SectorOrder.DOS_Sector, out string _);
            }
            return false;
        }
    }

    public bool IsChecked_FT_ProDOSBlock {
        get => mFileType == DiskImageTypes.FileTypeValue.ProDOSBlock;
        set { if (value) { mFileType = DiskImageTypes.FileTypeValue.ProDOSBlock; NotifyAllFTProps(); } }
    }
    public bool IsEnabled_FT_ProDOSBlock {
        get {
            if (GetNumTracksSectors(out uint tracks, out uint sectors)) {
                return UnadornedSector.CanCreateSectorImage(tracks, sectors, SectorOrder.ProDOS_Block, out string _);
            } else if (GetNumBlocks(out uint blocks)) {
                return UnadornedSector.CanCreateBlockImage(blocks, out string _);
            }
            return false;
        }
    }

    public bool IsChecked_FT_SimpleBlock {
        get => mFileType == DiskImageTypes.FileTypeValue.SimpleBlock;
        set { if (value) { mFileType = DiskImageTypes.FileTypeValue.SimpleBlock; NotifyAllFTProps(); } }
    }
    public bool IsEnabled_FT_SimpleBlock => IsEnabled_FT_ProDOSBlock;

    public bool IsChecked_FT_TwoIMG {
        get => mFileType == DiskImageTypes.FileTypeValue.TwoIMG;
        set { if (value) { mFileType = DiskImageTypes.FileTypeValue.TwoIMG; NotifyAllFTProps(); } }
    }
    public bool IsEnabled_FT_TwoIMG {
        get {
            if (GetNumTracksSectors(out uint tracks, out uint sectors)) {
                return TwoIMG.CanCreateDOSSectorImage(tracks, sectors, out string _);
            } else if (GetNumBlocks(out uint blocks)) {
                return TwoIMG.CanCreateProDOSBlockImage(blocks, out string _);
            }
            return false;
        }
    }

    public bool IsChecked_FT_NuFX {
        get => mFileType == DiskImageTypes.FileTypeValue.NuFX;
        set { if (value) { mFileType = DiskImageTypes.FileTypeValue.NuFX; NotifyAllFTProps(); } }
    }
    public bool IsEnabled_FT_NuFX {
        get {
            if (GetNumTracksSectors(out uint tracks, out uint sectors)) {
                return tracks == 35 && sectors == 16;
            } else if (GetNumBlocks(out uint blocks)) {
                return blocks == 1600;
            }
            return false;
        }
    }

    public bool IsChecked_FT_DiskCopy42 {
        get => mFileType == DiskImageTypes.FileTypeValue.DiskCopy42;
        set { if (value) { mFileType = DiskImageTypes.FileTypeValue.DiskCopy42; NotifyAllFTProps(); } }
    }
    public bool IsEnabled_FT_DiskCopy42 {
        get {
            if (!GetMediaKind(out MediaKind kind)) { return false; }
            return DiskCopy.CanCreateDisk(kind, out string _);
        }
    }

    public bool IsChecked_FT_Woz {
        get => mFileType == DiskImageTypes.FileTypeValue.Woz;
        set { if (value) { mFileType = DiskImageTypes.FileTypeValue.Woz; NotifyAllFTProps(); } }
    }
    public bool IsEnabled_FT_Woz {
        get {
            if (GetNumTracksSectors(out uint tracks, out uint sectors)) {
                if (sectors != 13 && sectors != 16) return false;
                return Woz.CanCreateDisk525(tracks, out string _);
            } else if (GetNumBlocks(out uint blocks)) {
                if (blocks == 800)  return Woz.CanCreateDisk35(MediaKind.GCR_SSDD35, WOZ_IL_35, out string _);
                if (blocks == 1600) return Woz.CanCreateDisk35(MediaKind.GCR_DSDD35, WOZ_IL_35, out string _);
            }
            return false;
        }
    }

    public bool IsChecked_FT_Moof {
        get => mFileType == DiskImageTypes.FileTypeValue.Moof;
        set { if (value) { mFileType = DiskImageTypes.FileTypeValue.Moof; NotifyAllFTProps(); } }
    }
    public bool IsEnabled_FT_Moof {
        get {
            if (GetNumBlocks(out uint blocks)) {
                if (blocks == 800)  return Moof.CanCreateDisk35(MediaKind.GCR_SSDD35, MOOF_IL_35, out string _);
                if (blocks == 1600) return Moof.CanCreateDisk35(MediaKind.GCR_DSDD35, MOOF_IL_35, out string _);
            }
            return false;
        }
    }

    public bool IsChecked_FT_Nib {
        get => mFileType == DiskImageTypes.FileTypeValue.Nib;
        set { if (value) { mFileType = DiskImageTypes.FileTypeValue.Nib; NotifyAllFTProps(); } }
    }
    public bool IsEnabled_FT_Nib {
        get {
            if (!GetNumTracksSectors(out uint tracks, out uint sectors)) return false;
            return tracks == 35 && (sectors == 13 || sectors == 16);
        }
    }

    public bool IsChecked_FT_Trackstar {
        get => mFileType == DiskImageTypes.FileTypeValue.Trackstar;
        set { if (value) { mFileType = DiskImageTypes.FileTypeValue.Trackstar; NotifyAllFTProps(); } }
    }
    public bool IsEnabled_FT_Trackstar {
        get {
            if (!GetNumTracksSectors(out uint tracks, out uint sectors)) return false;
            return Trackstar.CanCreateDisk(tracks, sectors, out string _);
        }
    }

    private static readonly string[] sFTPropList =
    [
        nameof(IsChecked_FT_DOSSector),    nameof(IsEnabled_FT_DOSSector),
        nameof(IsChecked_FT_ProDOSBlock),  nameof(IsEnabled_FT_ProDOSBlock),
        nameof(IsChecked_FT_SimpleBlock),  nameof(IsEnabled_FT_SimpleBlock),
        nameof(IsChecked_FT_TwoIMG),       nameof(IsEnabled_FT_TwoIMG),
        nameof(IsChecked_FT_NuFX),         nameof(IsEnabled_FT_NuFX),
        nameof(IsChecked_FT_DiskCopy42),   nameof(IsEnabled_FT_DiskCopy42),
        nameof(IsChecked_FT_Woz),          nameof(IsEnabled_FT_Woz),
        nameof(IsChecked_FT_Moof),         nameof(IsEnabled_FT_Moof),
        nameof(IsChecked_FT_Nib),          nameof(IsEnabled_FT_Nib),
        nameof(IsChecked_FT_Trackstar),    nameof(IsEnabled_FT_Trackstar)
    ];

    private void NotifyAllFTProps() {
        foreach (string name in sFTPropList) OnPropertyChanged(name);
    }

    // -----------------------------------------------------------------------------------------
    // Chunk access helpers

    private bool GetNumBlocks(out uint blocks) {
        if (mChunks.HasBlocks) {
            blocks = (uint)(mChunks.FormattedLength / BLOCK_SIZE);
            return true;
        }
        blocks = 0;
        return false;
    }

    private bool GetNumTracksSectors(out uint tracks, out uint sectors) {
        if (mChunks.HasSectors) {
            tracks = mChunks.NumTracks;
            sectors = mChunks.NumSectorsPerTrack;
            return true;
        }
        tracks = sectors = 0;
        return false;
    }

    private bool GetMediaKind(out MediaKind kind) {
        kind = MediaKind.Unknown;
        if (!GetNumBlocks(out uint blocks)) return false;
        if (blocks == 800)       { kind = MediaKind.GCR_SSDD35; return true; }
        if (blocks == 1600)      { kind = MediaKind.GCR_DSDD35; return true; }
        if (blocks == 1440)      { kind = MediaKind.MFM_DSDD35; return true; }
        if (blocks == 2880)      { kind = MediaKind.MFM_DSHD35; return true; }
        return false;
    }

    // -----------------------------------------------------------------------------------------
    // Default file type selection

    private void SetDefaultFileType() {
        mFileType = _settingsService.GetEnum(AppSettings.SAVE_DISK_FILE_TYPE,
            DiskImageTypes.FileTypeValue.ProDOSBlock);

        bool needNewType = false;
        switch (mFileType) {
            case DiskImageTypes.FileTypeValue.DOSSector:   needNewType = !IsEnabled_FT_DOSSector; break;
            case DiskImageTypes.FileTypeValue.ProDOSBlock: needNewType = !IsEnabled_FT_ProDOSBlock; break;
            case DiskImageTypes.FileTypeValue.SimpleBlock: needNewType = !IsEnabled_FT_SimpleBlock; break;
            case DiskImageTypes.FileTypeValue.TwoIMG:      needNewType = !IsEnabled_FT_TwoIMG; break;
            case DiskImageTypes.FileTypeValue.NuFX:        needNewType = !IsEnabled_FT_NuFX; break;
            case DiskImageTypes.FileTypeValue.DiskCopy42:  needNewType = !IsEnabled_FT_DiskCopy42; break;
            case DiskImageTypes.FileTypeValue.Woz:         needNewType = !IsEnabled_FT_Woz; break;
            case DiskImageTypes.FileTypeValue.Moof:        needNewType = !IsEnabled_FT_Moof; break;
            case DiskImageTypes.FileTypeValue.Nib:         needNewType = !IsEnabled_FT_Nib; break;
            case DiskImageTypes.FileTypeValue.Trackstar:   needNewType = !IsEnabled_FT_Trackstar; break;
            default: Debug.Assert(false); break;
        }
        if (!needNewType) return;

        if (IsEnabled_FT_ProDOSBlock)       mFileType = DiskImageTypes.FileTypeValue.ProDOSBlock;
        else if (IsEnabled_FT_SimpleBlock)  mFileType = DiskImageTypes.FileTypeValue.SimpleBlock;
        else if (IsEnabled_FT_DOSSector)    mFileType = DiskImageTypes.FileTypeValue.DOSSector;
        else if (IsEnabled_FT_Woz)          mFileType = DiskImageTypes.FileTypeValue.Woz;
        else if (IsEnabled_FT_Moof)         mFileType = DiskImageTypes.FileTypeValue.Moof;
        else if (IsEnabled_FT_TwoIMG)       mFileType = DiskImageTypes.FileTypeValue.TwoIMG;
        else if (IsEnabled_FT_NuFX)         mFileType = DiskImageTypes.FileTypeValue.NuFX;
        else if (IsEnabled_FT_DiskCopy42)   mFileType = DiskImageTypes.FileTypeValue.DiskCopy42;
        else if (IsEnabled_FT_Nib)          mFileType = DiskImageTypes.FileTypeValue.Nib;
        else if (IsEnabled_FT_Trackstar)    mFileType = DiskImageTypes.FileTypeValue.Trackstar;
        else { Debug.Assert(false); mFileType = DiskImageTypes.FileTypeValue.ProDOSBlock; }
    }

    // -----------------------------------------------------------------------------------------
    // Disk creation

    private async Task<bool> CreateImageAsync() {
        bool is13Sector = GetNumTracksSectors(out uint _, out uint sectors) && sectors == 13;

        // Build file picker types
        FilePickerFileType fpft;
        string[] exts;
        switch (mFileType) {
            case DiskImageTypes.FileTypeValue.DOSSector:
                if (is13Sector) {
                    fpft = new FilePickerFileType("DOS 13-Sector") { Patterns = ["*.d13"] };
                    exts = [".d13"];
                } else {
                    fpft = new FilePickerFileType("DOS-Order Disk") { Patterns = ["*.do"] };
                    exts = [".do"];
                }
                break;
            case DiskImageTypes.FileTypeValue.ProDOSBlock:
                fpft = new FilePickerFileType("ProDOS-Order Disk") { Patterns = ["*.po"] };
                exts = [".po"];
                break;
            case DiskImageTypes.FileTypeValue.SimpleBlock:
                fpft = new FilePickerFileType("Disk Image") { Patterns = ["*.iso", "*.hdv"] };
                exts = [".iso", ".hdv"];
                break;
            case DiskImageTypes.FileTypeValue.TwoIMG:
                fpft = new FilePickerFileType("2IMG") { Patterns = ["*.2mg"] };
                exts = [".2mg"];
                break;
            case DiskImageTypes.FileTypeValue.NuFX:
                fpft = new FilePickerFileType("ShrinkIt Disk") { Patterns = ["*.sdk"] };
                exts = [".sdk"];
                break;
            case DiskImageTypes.FileTypeValue.DiskCopy42:
                fpft = new FilePickerFileType("DiskCopy 4.2") { Patterns = ["*.image"] };
                exts = [".image"];
                break;
            case DiskImageTypes.FileTypeValue.Woz:
                fpft = new FilePickerFileType("WOZ") { Patterns = ["*.woz"] };
                exts = [".woz"];
                break;
            case DiskImageTypes.FileTypeValue.Moof:
                fpft = new FilePickerFileType("MOOF") { Patterns = ["*.moof"] };
                exts = [".moof"];
                break;
            case DiskImageTypes.FileTypeValue.Nib:
                fpft = new FilePickerFileType("Nibble") { Patterns = ["*.nib"] };
                exts = [".nib"];
                break;
            case DiskImageTypes.FileTypeValue.Trackstar:
                fpft = new FilePickerFileType("Trackstar") { Patterns = ["*.app"] };
                exts = [".app"];
                break;
            default:
                throw new NotImplementedException("Not implemented: " + mFileType);
        }

        string suggestedName = "SavedDisk" + exts[0];
        string? pathName = await _filePickerService.SaveFileAsync(
            "Save Disk Image As...",
            suggestedName,
            [fpft, new FilePickerFileType("All Files") { Patterns = ["*"] }]);

        if (string.IsNullOrEmpty(pathName)) return false;

        // Ensure correct extension
        bool isExtValid = false;
        foreach (string ext in exts) {
            if (pathName.ToLowerInvariant().EndsWith(ext)) { isExtValid = true; break; }
        }
        if (!isExtValid) pathName += exts[0];

        FileStream? stream;
        try {
            stream = new FileStream(pathName, FileMode.Create);
        } catch (Exception ex) {
            AppLog.E("Save as disk: unable to create file '" + pathName + "'", ex);
            await _dialogService.ShowMessageAsync("Unable to create file: " + ex.Message, "Failed");
            return false;
        }

        IDiskImage? diskImage = null;
        MemoryStream? tmpStream = null;
        int errorCount;

        try {
            SectorCodec codec;
            MediaKind mediaKind;
            var volNum = DEFAULT_525_VOLUME_NUM;

            uint blocks;
            uint sectors2;
            uint tracks;
            switch (mFileType) {
                case DiskImageTypes.FileTypeValue.DOSSector:
                    if (!GetNumTracksSectors(out tracks, out sectors2)) throw new Exception("internal error");
                    diskImage = UnadornedSector.CreateSectorImage(stream, tracks, sectors2, SectorOrder.DOS_Sector, mAppHook);
                    break;
                case DiskImageTypes.FileTypeValue.ProDOSBlock:
                case DiskImageTypes.FileTypeValue.SimpleBlock:
                    if (GetNumTracksSectors(out tracks, out sectors2)) {
                        diskImage = UnadornedSector.CreateSectorImage(stream, tracks, sectors2, SectorOrder.ProDOS_Block, mAppHook);
                    } else if (GetNumBlocks(out blocks)) {
                        diskImage = UnadornedSector.CreateBlockImage(stream, blocks, mAppHook);
                    } else throw new Exception("internal error");
                    break;
                case DiskImageTypes.FileTypeValue.TwoIMG:
                    if (GetNumTracksSectors(out tracks, out sectors2)) {
                        diskImage = TwoIMG.CreateDOSSectorImage(stream, tracks, mAppHook);
                    } else if (GetNumBlocks(out blocks)) {
                        diskImage = TwoIMG.CreateProDOSBlockImage(stream, blocks, mAppHook);
                    } else throw new Exception("internal error");
                    break;
                case DiskImageTypes.FileTypeValue.NuFX:
                    tmpStream = new MemoryStream();
                    if (GetNumTracksSectors(out tracks, out sectors2)) {
                        diskImage = UnadornedSector.CreateSectorImage(tmpStream, tracks, sectors2, SectorOrder.ProDOS_Block, mAppHook);
                    } else if (GetNumBlocks(out blocks)) {
                        diskImage = UnadornedSector.CreateBlockImage(tmpStream, blocks, mAppHook);
                    } else throw new Exception("internal error");
                    break;
                case DiskImageTypes.FileTypeValue.Woz:
                    if (GetNumTracksSectors(out tracks, out sectors2)) {
                        codec = (sectors2 == 13) ?
                            StdSectorCodec.GetCodec(StdSectorCodec.CodecIndex525.Std_525_13) :
                            StdSectorCodec.GetCodec(StdSectorCodec.CodecIndex525.Std_525_16);
                        diskImage = Woz.CreateDisk525(stream, tracks, codec, (byte)volNum, mAppHook);
                    } else {
                        if (!GetMediaKind(out mediaKind)) throw new Exception("internal error");
                        codec = StdSectorCodec.GetCodec(StdSectorCodec.CodecIndex35.Std_35);
                        diskImage = Woz.CreateDisk35(stream, mediaKind, WOZ_IL_35, codec, mAppHook);
                    }
                    ((Woz)diskImage).AddMETA();
                    ((Woz)diskImage).SetCreator("CiderPress II v" + GlobalAppVersion.AppVersion);
                    break;
                case DiskImageTypes.FileTypeValue.Moof:
                    if (!GetMediaKind(out mediaKind)) throw new Exception("internal error");
                    codec = StdSectorCodec.GetCodec(StdSectorCodec.CodecIndex35.Std_35);
                    diskImage = Moof.CreateDisk35(stream, mediaKind, MOOF_IL_35, codec, mAppHook);
                    ((Moof)diskImage).AddMETA();
                    ((Moof)diskImage).SetCreator("CiderPress II v" + GlobalAppVersion.AppVersion);
                    break;
                case DiskImageTypes.FileTypeValue.Nib:
                    if (!GetNumTracksSectors(out tracks, out sectors2)) throw new Exception("internal error");
                    codec = (sectors2 == 13) ?
                        StdSectorCodec.GetCodec(StdSectorCodec.CodecIndex525.Std_525_13) :
                        StdSectorCodec.GetCodec(StdSectorCodec.CodecIndex525.Std_525_16);
                    diskImage = UnadornedNibble525.CreateDisk(stream, codec, (byte)volNum, mAppHook);
                    break;
                case DiskImageTypes.FileTypeValue.DiskCopy42:
                    if (!GetMediaKind(out mediaKind)) throw new Exception("internal error");
                    diskImage = DiskCopy.CreateDisk(stream, mediaKind, mAppHook);
                    break;
                case DiskImageTypes.FileTypeValue.Trackstar:
                    if (!GetNumTracksSectors(out tracks, out sectors2)) throw new Exception("internal error");
                    codec = (sectors2 == 13) ?
                        StdSectorCodec.GetCodec(StdSectorCodec.CodecIndex525.Std_525_13) :
                        StdSectorCodec.GetCodec(StdSectorCodec.CodecIndex525.Std_525_16);
                    diskImage = Trackstar.CreateDisk(stream, codec, (byte)volNum, tracks, mAppHook);
                    break;
                default:
                    throw new NotImplementedException("Not implemented: " + mFileType);
            }

            CopyDisk(mChunks, diskImage.ChunkAccess!, out errorCount);

            if (mFileType == DiskImageTypes.FileTypeValue.NuFX) {
                Debug.Assert(tmpStream != null);
                NuFX archive = NuFX.CreateArchive(mAppHook);
                archive.StartTransaction();
                IFileEntry entry = archive.CreateRecord();
                entry.FileName = "DISK";
                SimplePartSource source = new SimplePartSource(tmpStream);
                archive.AddPart(entry, FilePart.DiskImage, source, CompressionFormat.Default);
                archive.CommitTransaction(stream);
            }

            PathName = pathName;
        } catch (Exception ex) {
            AppLog.E("Save as disk: error creating disk", ex);
            await _dialogService.ShowMessageAsync("Error creating disk: " + ex.Message, "Failed");
            stream.Close();
            stream = null;
            Debug.WriteLine("Cleanup: removing '" + pathName + "'");
            File.Delete(pathName);
            return false;
        } finally {
            diskImage?.Dispose();
            stream?.Close();
        }

        if (errorCount != 0) {
            await _dialogService.ShowMessageAsync(
                "Some data could not be read. Total errors: " + errorCount + ".", "Partial Copy");
        }

        return true;
    }

    /// <summary>
    /// Copies all tracks/sectors or all blocks from source to destination.
    /// Unreadable sectors are copied as zeroes.
    /// </summary>
    internal static void CopyDisk(IChunkAccess srcChunks, IChunkAccess dstChunks, out int errorCount) {
        Debug.Assert(!dstChunks.IsReadOnly);
        if (srcChunks.FormattedLength > dstChunks.FormattedLength) {
            throw new Exception("Internal error: src size exceeds dst");
        }

        errorCount = 0;
        if (srcChunks.HasSectors && dstChunks.HasSectors) {
            byte[] copyBuf = new byte[SECTOR_SIZE];
            for (uint trk = 0; trk < srcChunks.NumTracks; trk++) {
                for (uint sct = 0; sct < srcChunks.NumSectorsPerTrack; sct++) {
                    try {
                        srcChunks.ReadSector(trk, sct, copyBuf, 0);
                    } catch (IOException) {
                        AppLog.W("Save as disk: read error at track " + trk + ", sector " + sct);
                        RawData.MemSet(copyBuf, 0, SECTOR_SIZE, 0x00);
                        errorCount++;
                    }
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
                    AppLog.W("Save as disk: read error at block " + block);
                    RawData.MemSet(copyBuf, 0, BLOCK_SIZE, 0x00);
                    errorCount++;
                }
                dstChunks.WriteBlock(block, copyBuf, 0);
            }
        } else {
            throw new NotImplementedException();
        }
    }

    // -----------------------------------------------------------------------------------------
    // Constructor

    public SaveAsDiskViewModel(
        object diskOrPartition,
        IChunkAccess chunks,
        Formatter formatter,
        AppHook appHook,
        IFilePickerService filePickerService,
        ISettingsService settingsService,
        IDialogService dialogService)
    {
        mChunks = chunks;
        mAppHook = appHook;
        _filePickerService = filePickerService;
        _settingsService = settingsService;
        _dialogService = dialogService;

        SetDefaultFileType();

        string type = (diskOrPartition is IDiskImage) ? "disk image" : "partition";
        SourceSizeText = "Source " + type + " size is " +
            formatter.FormatSizeOnDisk(chunks.FormattedLength, 1024);

        OkCommand = new AsyncRelayCommand(async () => {
            if (!await CreateImageAsync()) return;
            _settingsService.SetEnum(AppSettings.SAVE_DISK_FILE_TYPE, mFileType);
            CloseRequested?.Invoke(true);
        });
        CancelCommand = new AsyncRelayCommand(
            async () => CloseRequested?.Invoke(false));
    }
}
