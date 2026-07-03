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
using DiskArc.FS;
using static DiskArc.Defs;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using cp2_avalonia.Models;
using cp2_avalonia.Services;

namespace cp2_avalonia.ViewModels;

public class CreateDiskImageViewModel : ObservableObject
{
    private readonly AppHook _appHook;
    private readonly IFilePickerService _filePickerService;
    private readonly ISettingsService _settingsService;
    private readonly IDialogService _dialogService;

    /// <summary>
    /// Pathname of the created file (set on success).
    /// </summary>
    public string CreatedDiskPath { get; private set; } = string.Empty;

    public event Action<bool>? CloseRequested;

    public IRelayCommand OkCommand { get; }
    public IRelayCommand CancelCommand { get; }

    // --- Validity ---

    public bool IsValid {
        get => _isValid;
        private set => SetProperty(ref _isValid, value);
    }
    private bool _isValid;

    public bool IsSizeDescValid {
        get => _isSizeDescValid;
        private set => SetProperty(ref _isSizeDescValid, value);
    }
    private bool _isSizeDescValid = true;

    public bool IsSizeLimitValid {
        get => _isSizeLimitValid;
        private set => SetProperty(ref _isSizeLimitValid, value);
    }
    private bool _isSizeLimitValid = true;

    public bool IsVolNameSyntaxValid {
        get => _isVolNameSyntaxValid;
        private set => SetProperty(ref _isVolNameSyntaxValid, value);
    }
    private bool _isVolNameSyntaxValid = true;

    public bool IsVolNumSyntaxValid {
        get => _isVolNumSyntaxValid;
        private set => SetProperty(ref _isVolNumSyntaxValid, value);
    }
    private bool _isVolNumSyntaxValid = true;

    // --- Disk Size ---

    public DiskImageTypes.DiskSizeValue mDiskSize;

    public bool IsChecked_Flop525_113 {
        get => mDiskSize == DiskImageTypes.DiskSizeValue.Flop525_114;
        set { if (value) { mDiskSize = DiskImageTypes.DiskSizeValue.Flop525_114; } UpdateControls(); }
    }
    public bool IsChecked_Flop525_140 {
        get => mDiskSize == DiskImageTypes.DiskSizeValue.Flop525_140;
        set { if (value) { mDiskSize = DiskImageTypes.DiskSizeValue.Flop525_140; } UpdateControls(); }
    }
    public bool IsChecked_Flop525_160 {
        get => mDiskSize == DiskImageTypes.DiskSizeValue.Flop525_160;
        set { if (value) { mDiskSize = DiskImageTypes.DiskSizeValue.Flop525_160; } UpdateControls(); }
    }
    public bool IsChecked_Flop35_400 {
        get => mDiskSize == DiskImageTypes.DiskSizeValue.Flop35_400;
        set { if (value) { mDiskSize = DiskImageTypes.DiskSizeValue.Flop35_400; } UpdateControls(); }
    }
    public bool IsChecked_Flop35_800 {
        get => mDiskSize == DiskImageTypes.DiskSizeValue.Flop35_800;
        set { if (value) { mDiskSize = DiskImageTypes.DiskSizeValue.Flop35_800; } UpdateControls(); }
    }
    public bool IsChecked_Flop35_1440 {
        get => mDiskSize == DiskImageTypes.DiskSizeValue.Flop35_1440;
        set { if (value) { mDiskSize = DiskImageTypes.DiskSizeValue.Flop35_1440; } UpdateControls(); }
    }
    public bool IsChecked_Other_32MB {
        get => mDiskSize == DiskImageTypes.DiskSizeValue.Other_32MB;
        set { if (value) { mDiskSize = DiskImageTypes.DiskSizeValue.Other_32MB; } UpdateControls(); }
    }
    public bool IsChecked_Other_Custom {
        get => mDiskSize == DiskImageTypes.DiskSizeValue.Other_Custom;
        set { if (value) { mDiskSize = DiskImageTypes.DiskSizeValue.Other_Custom; } UpdateControls(); }
    }

    public string CustomSizeText {
        get => _customSizeText;
        set {
            _customSizeText = value;
            OnPropertyChanged();
            mDiskSize = DiskImageTypes.DiskSizeValue.Other_Custom;
            UpdateControls();
        }
    }
    private string _customSizeText;

    // --- Filesystem ---

    private FileSystemType mFilesystem;

    public bool IsChecked_FS_None {
        get => mFilesystem == FileSystemType.Unknown;
        set { if (value) { mFilesystem = FileSystemType.Unknown; } UpdateControls(); }
    }
    public bool IsEnabled_FS_None => true;

    public bool IsChecked_FS_DOS {
        get => mFilesystem == FileSystemType.DOS33;
        set { if (value) { mFilesystem = FileSystemType.DOS33; } UpdateControls(); }
    }
    public bool IsEnabled_FS_DOS => DOS.IsSizeAllowed(GetVolSize()) && !IsFlop35;

    public bool IsChecked_FS_ProDOS {
        get => mFilesystem == FileSystemType.ProDOS;
        set { if (value) { mFilesystem = FileSystemType.ProDOS; } UpdateControls(); }
    }
    public bool IsEnabled_FS_ProDOS => ProDOS.IsSizeAllowed(GetVolSize());

    public bool IsChecked_FS_HFS {
        get => mFilesystem == FileSystemType.HFS;
        set { if (value) { mFilesystem = FileSystemType.HFS; } UpdateControls(); }
    }
    public bool IsEnabled_FS_HFS => HFS.IsSizeAllowed(GetVolSize());

    public bool IsChecked_FS_Pascal {
        get => mFilesystem == FileSystemType.Pascal;
        set { if (value) { mFilesystem = FileSystemType.Pascal; } UpdateControls(); }
    }
    public bool IsEnabled_FS_Pascal => Pascal.IsSizeAllowed(GetVolSize());

    public bool IsChecked_FS_CPM {
        get => mFilesystem == FileSystemType.CPM;
        set { if (value) { mFilesystem = FileSystemType.CPM; } UpdateControls(); }
    }
    public bool IsEnabled_FS_CPM => CPM.IsSizeAllowed(GetVolSize());

    public string VolumeNameText {
        get => _volumeNameText;
        set { _volumeNameText = value; OnPropertyChanged(); UpdateControls(); }
    }
    private string _volumeNameText;

    public string VolumeNumText {
        get => _volumeNumText;
        set { _volumeNumText = value; OnPropertyChanged(); UpdateControls(); }
    }
    private string _volumeNumText;

    public bool IsChecked_ReserveBoot {
        get => _isCheckedReserveBoot;
        set => SetProperty(ref _isCheckedReserveBoot, value);
    }
    private bool _isCheckedReserveBoot;

    // --- File Type ---

    private const int WOZ_IL_35 = 4;
    private const int MOOF_IL_35 = 2;

    private DiskImageTypes.FileTypeValue mFileType;

    public bool IsChecked_FT_DOSSector {
        get => mFileType == DiskImageTypes.FileTypeValue.DOSSector;
        set { if (value) { mFileType = DiskImageTypes.FileTypeValue.DOSSector; } UpdateControls(); }
    }
    public bool IsEnabled_FT_DOSSector {
        get {
            if (!GetNumTracksSectors(out uint tracks, out uint sectors)) return false;
            return UnadornedSector.CanCreateSectorImage(tracks, sectors,
                SectorOrder.DOS_Sector, out string _);
        }
    }

    public bool IsChecked_FT_ProDOSBlock {
        get => mFileType == DiskImageTypes.FileTypeValue.ProDOSBlock;
        set { if (value) { mFileType = DiskImageTypes.FileTypeValue.ProDOSBlock; } UpdateControls(); }
    }
    public bool IsEnabled_FT_ProDOSBlock {
        get {
            if (!GetNumBlocks(out uint blocks)) return false;
            return UnadornedSector.CanCreateBlockImage(blocks, out string _);
        }
    }

    public bool IsChecked_FT_SimpleBlock {
        get => mFileType == DiskImageTypes.FileTypeValue.SimpleBlock;
        set { if (value) { mFileType = DiskImageTypes.FileTypeValue.SimpleBlock; } UpdateControls(); }
    }
    public bool IsEnabled_FT_SimpleBlock => IsEnabled_FT_ProDOSBlock;

    public bool IsChecked_FT_TwoIMG {
        get => mFileType == DiskImageTypes.FileTypeValue.TwoIMG;
        set { if (value) { mFileType = DiskImageTypes.FileTypeValue.TwoIMG; } UpdateControls(); }
    }
    public bool IsEnabled_FT_TwoIMG {
        get {
            if (!GetNumBlocks(out uint blocks)) return false;
            return TwoIMG.CanCreateProDOSBlockImage(blocks, out string _);
        }
    }

    public bool IsChecked_FT_NuFX {
        get => mFileType == DiskImageTypes.FileTypeValue.NuFX;
        set { if (value) { mFileType = DiskImageTypes.FileTypeValue.NuFX; } UpdateControls(); }
    }
    public bool IsEnabled_FT_NuFX {
        get {
            if (!GetNumBlocks(out uint blocks)) return false;
            return blocks == 280 || blocks == 1600;
        }
    }

    public bool IsChecked_FT_DiskCopy42 {
        get => mFileType == DiskImageTypes.FileTypeValue.DiskCopy42;
        set { if (value) { mFileType = DiskImageTypes.FileTypeValue.DiskCopy42; } UpdateControls(); }
    }
    public bool IsEnabled_FT_DiskCopy42 {
        get {
            if (!GetNumBlocks(out uint _)) return false;
            if (!GetMediaKind(out MediaKind kind)) return false;
            return DiskCopy.CanCreateDisk(kind, out string _);
        }
    }

    public bool IsChecked_FT_Woz {
        get => mFileType == DiskImageTypes.FileTypeValue.Woz;
        set { if (value) { mFileType = DiskImageTypes.FileTypeValue.Woz; } UpdateControls(); }
    }
    public bool IsEnabled_FT_Woz {
        get {
            if (IsFlop525) {
                if (!GetNumTracksSectors(out uint tracks, out uint _)) return false;
                return Woz.CanCreateDisk525(tracks, out string _);
            } else {
                if (!GetMediaKind(out MediaKind kind)) return false;
                return Woz.CanCreateDisk35(kind, WOZ_IL_35, out string _);
            }
        }
    }

    public bool IsChecked_FT_Moof {
        get => mFileType == DiskImageTypes.FileTypeValue.Moof;
        set { if (value) { mFileType = DiskImageTypes.FileTypeValue.Moof; } UpdateControls(); }
    }
    public bool IsEnabled_FT_Moof {
        get {
            if (IsFlop525) return false;
            if (!GetMediaKind(out MediaKind kind)) return false;
            return Moof.CanCreateDisk35(kind, MOOF_IL_35, out string _);
        }
    }

    public bool IsChecked_FT_Nib {
        get => mFileType == DiskImageTypes.FileTypeValue.Nib;
        set { if (value) { mFileType = DiskImageTypes.FileTypeValue.Nib; } UpdateControls(); }
    }
    public bool IsEnabled_FT_Nib {
        get {
            if (!GetNumTracksSectors(out uint tracks, out uint _)) return false;
            return tracks == 35;
        }
    }

    public bool IsChecked_FT_Trackstar {
        get => mFileType == DiskImageTypes.FileTypeValue.Trackstar;
        set { if (value) { mFileType = DiskImageTypes.FileTypeValue.Trackstar; } UpdateControls(); }
    }
    public bool IsEnabled_FT_Trackstar {
        get {
            if (!GetNumTracksSectors(out uint tracks, out uint sectors)) return false;
            return Trackstar.CanCreateDisk(tracks, sectors, out string _);
        }
    }

    // --- Constructor ---

    public CreateDiskImageViewModel(AppHook appHook, IFilePickerService filePickerService,
            ISettingsService settingsService, IDialogService dialogService) {
        _appHook = appHook;
        _filePickerService = filePickerService;
        _settingsService = settingsService;
        _dialogService = dialogService;

        mDiskSize = _settingsService.GetEnum(AppSettings.NEW_DISK_SIZE,
            DiskImageTypes.DiskSizeValue.Flop525_140);
        mFilesystem = _settingsService.GetEnum(AppSettings.NEW_DISK_FILESYSTEM,
            FileSystemType.ProDOS);
        mFileType = _settingsService.GetEnum(AppSettings.NEW_DISK_FILE_TYPE,
            DiskImageTypes.FileTypeValue.ProDOSBlock);

        _volumeNameText = _settingsService.GetString(AppSettings.NEW_DISK_VOLUME_NAME, "NEWDISK");
        _volumeNumText = _settingsService.GetInt(AppSettings.NEW_DISK_VOLUME_NUM,
            DEFAULT_525_VOLUME_NUM).ToString();
        _customSizeText = _settingsService.GetString(AppSettings.NEW_DISK_CUSTOM_SIZE, "65535");
        _isCheckedReserveBoot = _settingsService.GetBool(AppSettings.NEW_DISK_RESERVE_BOOT, true);

        OkCommand = new AsyncRelayCommand(ExecuteOkAsync);
        CancelCommand = new AsyncRelayCommand(ExecuteCancelAsync);

        UpdateControls();
    }

    // --- Command handlers ---

    private async Task ExecuteOkAsync() {
        if (!await CreateImageAsync()) {
            return;
        }

        GetVolNum(out int volNum);
        _settingsService.SetEnum(AppSettings.NEW_DISK_SIZE, mDiskSize);
        _settingsService.SetEnum(AppSettings.NEW_DISK_FILESYSTEM, mFilesystem);
        _settingsService.SetEnum(AppSettings.NEW_DISK_FILE_TYPE, mFileType);
        _settingsService.SetString(AppSettings.NEW_DISK_VOLUME_NAME, _volumeNameText);
        _settingsService.SetInt(AppSettings.NEW_DISK_VOLUME_NUM, volNum);
        _settingsService.SetString(AppSettings.NEW_DISK_CUSTOM_SIZE, _customSizeText);
        _settingsService.SetBool(AppSettings.NEW_DISK_RESERVE_BOOT, _isCheckedReserveBoot);

        CloseRequested?.Invoke(true);
    }

    private async Task ExecuteCancelAsync() {
        CloseRequested?.Invoke(false);
    }

    // --- Disk creation ---

    private async Task<bool> CreateImageAsync() {
        bool is13Sector = GetNumTracksSectors(out uint chkTracks, out uint chkSectors)
            && chkSectors == 13;

        // Build file picker type based on mFileType.
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

        string suggestedName = "NewDisk" + exts[0];
        string? pathName = await _filePickerService.SaveFileAsync(
            "Create File...",
            suggestedName,
            [fpft, new FilePickerFileType("All Files") { Patterns = ["*"] }]);

        if (string.IsNullOrEmpty(pathName)) {
            return false;
        }

        // Fix extension if needed.
        bool isExtValid = false;
        foreach (string ext in exts) {
            if (pathName.ToLowerInvariant().EndsWith(ext)) {
                isExtValid = true;
                break;
            }
        }
        if (!isExtValid) {
            pathName += exts[0];
        }

        FileStream? stream;
        try {
            stream = new FileStream(pathName, FileMode.Create);
        } catch (Exception ex) {
            AppLog.E("Create disk image: unable to create file '" + pathName + "'", ex);
            await _dialogService.ShowMessageAsync("Unable to create file: " + ex.Message, "Error");
            return false;
        }

        MemoryStream? tmpStream = null;
        try {
            IDiskImage diskImage;
            SectorCodec codec;
            MediaKind mediaKind;
            uint blocks, tracks, sectors;

            GetVolNum(out int volNum);

            switch (mFileType) {
                case DiskImageTypes.FileTypeValue.DOSSector:
                    if (!GetNumTracksSectors(out tracks, out sectors)) {
                        throw new Exception("internal error");
                    }
                    diskImage = UnadornedSector.CreateSectorImage(stream, tracks, sectors,
                        SectorOrder.DOS_Sector, _appHook);
                    break;
                case DiskImageTypes.FileTypeValue.ProDOSBlock:
                case DiskImageTypes.FileTypeValue.SimpleBlock:
                    if (GetNumTracksSectors(out tracks, out sectors)) {
                        diskImage = UnadornedSector.CreateSectorImage(stream, tracks, sectors,
                            SectorOrder.ProDOS_Block, _appHook);
                    } else if (GetNumBlocks(out blocks)) {
                        diskImage = UnadornedSector.CreateBlockImage(stream, blocks, _appHook);
                    } else {
                        throw new Exception("internal error");
                    }
                    break;
                case DiskImageTypes.FileTypeValue.TwoIMG:
                    if (GetNumTracksSectors(out tracks, out sectors)) {
                        diskImage = TwoIMG.CreateDOSSectorImage(stream, tracks, _appHook);
                    } else if (GetNumBlocks(out blocks)) {
                        diskImage = TwoIMG.CreateProDOSBlockImage(stream, blocks, _appHook);
                    } else {
                        throw new Exception("internal error");
                    }
                    if (volNum != DEFAULT_525_VOLUME_NUM) {
                        ((TwoIMG)diskImage).VolumeNumber = volNum;
                    }
                    break;
                case DiskImageTypes.FileTypeValue.NuFX:
                    if (!GetNumBlocks(out blocks)) {
                        throw new Exception("internal error");
                    }
                    tmpStream = new MemoryStream();
                    if (blocks == 280) {
                        diskImage = UnadornedSector.CreateSectorImage(tmpStream, 35, 16,
                            SectorOrder.ProDOS_Block, _appHook);
                    } else if (blocks == 1600) {
                        diskImage = UnadornedSector.CreateBlockImage(tmpStream, 1600, _appHook);
                    } else {
                        throw new Exception("internal error");
                    }
                    break;
                case DiskImageTypes.FileTypeValue.DiskCopy42:
                    if (!GetMediaKind(out mediaKind)) {
                        throw new Exception("internal error");
                    }
                    diskImage = DiskCopy.CreateDisk(stream, mediaKind, _appHook);
                    break;
                case DiskImageTypes.FileTypeValue.Woz:
                    if (IsFlop525) {
                        if (!GetNumTracksSectors(out tracks, out sectors)) {
                            throw new Exception("internal error");
                        }
                        codec = (sectors == 13) ?
                            StdSectorCodec.GetCodec(StdSectorCodec.CodecIndex525.Std_525_13) :
                            StdSectorCodec.GetCodec(StdSectorCodec.CodecIndex525.Std_525_16);
                        diskImage = Woz.CreateDisk525(stream, tracks, codec, (byte)volNum,
                            _appHook);
                    } else {
                        if (!GetMediaKind(out mediaKind)) {
                            throw new Exception("internal error");
                        }
                        codec = StdSectorCodec.GetCodec(StdSectorCodec.CodecIndex35.Std_35);
                        diskImage = Woz.CreateDisk35(stream, mediaKind, WOZ_IL_35, codec,
                            _appHook);
                    }
                    Woz woz = (Woz)diskImage;
                    woz.AddMETA();
                    woz.SetCreator("CiderPress II v" + GlobalAppVersion.AppVersion);
                    break;
                case DiskImageTypes.FileTypeValue.Moof:
                    if (IsFlop525) {
                        throw new Exception("internal error");
                    } else {
                        if (!GetMediaKind(out mediaKind)) {
                            throw new Exception("internal error");
                        }
                        codec = StdSectorCodec.GetCodec(StdSectorCodec.CodecIndex35.Std_35);
                        diskImage = Moof.CreateDisk35(stream, mediaKind, MOOF_IL_35, codec,
                            _appHook);
                    }
                    Moof moof = (Moof)diskImage;
                    moof.AddMETA();
                    moof.SetCreator("CiderPress II v" + GlobalAppVersion.AppVersion);
                    break;
                case DiskImageTypes.FileTypeValue.Nib:
                    if (!GetNumTracksSectors(out tracks, out sectors)) {
                        throw new Exception("internal error");
                    }
                    codec = (sectors == 13) ?
                        StdSectorCodec.GetCodec(StdSectorCodec.CodecIndex525.Std_525_13) :
                        StdSectorCodec.GetCodec(StdSectorCodec.CodecIndex525.Std_525_16);
                    diskImage = UnadornedNibble525.CreateDisk(stream, codec, (byte)volNum,
                        _appHook);
                    break;
                case DiskImageTypes.FileTypeValue.Trackstar:
                    if (!GetNumTracksSectors(out tracks, out sectors)) {
                        throw new Exception("internal error");
                    }
                    codec = (sectors == 13) ?
                        StdSectorCodec.GetCodec(StdSectorCodec.CodecIndex525.Std_525_13) :
                        StdSectorCodec.GetCodec(StdSectorCodec.CodecIndex525.Std_525_16);
                    diskImage = Trackstar.CreateDisk(stream, codec, (byte)volNum, tracks,
                        _appHook);
                    break;
                default:
                    throw new NotImplementedException("Not implemented: " + mFileType);
            }

            // Format the filesystem, if one was chosen.
            using (diskImage) {
                if (mFilesystem != FileSystemType.Unknown) {
                    FileAnalyzer.CreateInstance(mFilesystem, diskImage.ChunkAccess!, _appHook,
                        out IDiskContents? contents);
                    if (contents is not IFileSystem fs) {
                        throw new DAException("Unable to create filesystem");
                    }

                    fs.Format(VolumeNameText, volNum, _isCheckedReserveBoot);
                    fs.Dispose();
                }
            }

            // Handle NuFX ".SDK" disk image creation.
            if (mFileType == DiskImageTypes.FileTypeValue.NuFX) {
                Debug.Assert(tmpStream != null);
                NuFX archive = NuFX.CreateArchive(_appHook);
                archive.StartTransaction();
                IFileEntry entry = archive.CreateRecord();
                entry.FileName = mFilesystem == FileSystemType.ProDOS ? VolumeNameText : "DISK";
                SimplePartSource source = new SimplePartSource(tmpStream);
                archive.AddPart(entry, FilePart.DiskImage, source, CompressionFormat.Default);
                archive.CommitTransaction(stream);
            }

            CreatedDiskPath = pathName;
        } catch (Exception ex) {
            AppLog.E("Create disk image: error creating disk", ex);
            await _dialogService.ShowMessageAsync("Error creating disk: " + ex.Message, "Error");
            stream.Close();
            stream = null;
            Debug.WriteLine("Cleanup: removing '" + pathName + "'");
            File.Delete(pathName);
            return false;
        } finally {
            stream?.Close();
        }

        return true;
    }

    // --- Property notification list for computed properties ---

    private static readonly string[] sPropList =
    [
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
        nameof(IsEnabled_FT_SimpleBlock),
        nameof(IsChecked_FT_SimpleBlock),
        nameof(IsEnabled_FT_TwoIMG),
        nameof(IsChecked_FT_TwoIMG),
        nameof(IsEnabled_FT_NuFX),
        nameof(IsChecked_FT_NuFX),
        nameof(IsEnabled_FT_DiskCopy42),
        nameof(IsChecked_FT_DiskCopy42),
        nameof(IsEnabled_FT_Woz),
        nameof(IsChecked_FT_Woz),
        nameof(IsEnabled_FT_Moof),
        nameof(IsChecked_FT_Moof),
        nameof(IsEnabled_FT_Nib),
        nameof(IsChecked_FT_Nib),
        nameof(IsEnabled_FT_Trackstar),
        nameof(IsChecked_FT_Trackstar)
    ];

    private const long FOUR_GB = 4L * 1024 * 1024 * 1024;

    /// <summary>
    /// Updates all controls, adjusting radio button selections if needed and
    /// validating free-text fields.
    /// </summary>
    private void UpdateControls() {
        bool customSizeSyntaxOk = true;
        bool customSizeLimitOk = true;
        if (mDiskSize == DiskImageTypes.DiskSizeValue.Other_Custom) {
            long customSize = GetVolSize();
            if (customSize < 0) {
                customSizeSyntaxOk = false;
            } else if (customSize == 0 || customSize > FOUR_GB) {
                customSizeLimitOk = false;
            }
        }
        IsSizeDescValid = customSizeSyntaxOk;
        IsSizeLimitValid = customSizeLimitOk;

        IsValid = customSizeSyntaxOk && customSizeLimitOk;

        if (IsValid) {
            // Re-select filesystem if current one is now disabled.
            bool needNewFs = false;
            switch (mFilesystem) {
                case FileSystemType.DOS33:   needNewFs = !IsEnabled_FS_DOS; break;
                case FileSystemType.ProDOS:  needNewFs = !IsEnabled_FS_ProDOS; break;
                case FileSystemType.HFS:     needNewFs = !IsEnabled_FS_HFS; break;
                case FileSystemType.Pascal:  needNewFs = !IsEnabled_FS_Pascal; break;
                case FileSystemType.CPM:     needNewFs = !IsEnabled_FS_CPM; break;
                case FileSystemType.Unknown: break;
                default: Debug.Assert(false); break;
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

            // Re-select file type if current one is now disabled.
            bool needNewType = false;
            switch (mFileType) {
                case DiskImageTypes.FileTypeValue.DOSSector:   needNewType = !IsEnabled_FT_DOSSector; break;
                case DiskImageTypes.FileTypeValue.ProDOSBlock:  needNewType = !IsEnabled_FT_ProDOSBlock; break;
                case DiskImageTypes.FileTypeValue.SimpleBlock:  needNewType = !IsEnabled_FT_SimpleBlock; break;
                case DiskImageTypes.FileTypeValue.TwoIMG:       needNewType = !IsEnabled_FT_TwoIMG; break;
                case DiskImageTypes.FileTypeValue.NuFX:         needNewType = !IsEnabled_FT_NuFX; break;
                case DiskImageTypes.FileTypeValue.DiskCopy42:   needNewType = !IsEnabled_FT_DiskCopy42; break;
                case DiskImageTypes.FileTypeValue.Woz:          needNewType = !IsEnabled_FT_Woz; break;
                case DiskImageTypes.FileTypeValue.Moof:         needNewType = !IsEnabled_FT_Moof; break;
                case DiskImageTypes.FileTypeValue.Nib:          needNewType = !IsEnabled_FT_Nib; break;
                case DiskImageTypes.FileTypeValue.Trackstar:    needNewType = !IsEnabled_FT_Trackstar; break;
                default: Debug.Assert(false); break;
            }
            if (needNewType) {
                if (IsEnabled_FT_ProDOSBlock) {
                    mFileType = DiskImageTypes.FileTypeValue.ProDOSBlock;
                } else if (IsEnabled_FT_SimpleBlock) {
                    mFileType = DiskImageTypes.FileTypeValue.SimpleBlock;
                } else if (IsEnabled_FT_DOSSector) {
                    mFileType = DiskImageTypes.FileTypeValue.DOSSector;
                } else if (IsEnabled_FT_Woz) {
                    mFileType = DiskImageTypes.FileTypeValue.Woz;
                } else if (IsEnabled_FT_Moof) {
                    mFileType = DiskImageTypes.FileTypeValue.Moof;
                } else if (IsEnabled_FT_TwoIMG) {
                    mFileType = DiskImageTypes.FileTypeValue.TwoIMG;
                } else if (IsEnabled_FT_NuFX) {
                    mFileType = DiskImageTypes.FileTypeValue.NuFX;
                } else if (IsEnabled_FT_DiskCopy42) {
                    mFileType = DiskImageTypes.FileTypeValue.DiskCopy42;
                } else if (IsEnabled_FT_Nib) {
                    mFileType = DiskImageTypes.FileTypeValue.Nib;
                } else if (IsEnabled_FT_Trackstar) {
                    mFileType = DiskImageTypes.FileTypeValue.Trackstar;
                }
                // else: custom size with no compatible type — leave selection alone
            }
        }

        // Validate volume name for filesystems that use it.
        bool volNameSyntaxOk = true;
        switch (mFilesystem) {
            case FileSystemType.ProDOS:
                volNameSyntaxOk = ProDOS_FileEntry.IsVolumeNameValid(VolumeNameText);
                break;
            case FileSystemType.HFS:
                volNameSyntaxOk = HFS_FileEntry.IsVolumeNameValid(VolumeNameText);
                break;
            case FileSystemType.Pascal:
                volNameSyntaxOk = Pascal_FileEntry.IsVolumeNameValid(VolumeNameText);
                break;
            case FileSystemType.DOS33:
            case FileSystemType.CPM:
            case FileSystemType.Unknown:
                break;
            default:
                throw new NotImplementedException("Didn't handle " + mFilesystem);
        }
        IsVolNameSyntaxValid = volNameSyntaxOk;
        IsValid &= volNameSyntaxOk;

        // Validate volume number.
        bool volNumSyntaxOk = GetVolNum(out int _);
        IsVolNumSyntaxValid = volNumSyntaxOk;
        IsValid &= volNumSyntaxOk;

        // Broadcast all property changes for computed properties.
        foreach (string propName in sPropList) {
            OnPropertyChanged(propName);
        }
    }

    // --- Helper methods ---

    private long GetVolSize() {
        switch (mDiskSize) {
            case DiskImageTypes.DiskSizeValue.Flop525_114:  return 35 * 13 * SECTOR_SIZE;
            case DiskImageTypes.DiskSizeValue.Flop525_140:  return 35 * 16 * SECTOR_SIZE;
            case DiskImageTypes.DiskSizeValue.Flop525_160:  return 40 * 16 * SECTOR_SIZE;
            case DiskImageTypes.DiskSizeValue.Flop35_400:   return 400 * 1024;
            case DiskImageTypes.DiskSizeValue.Flop35_800:   return 800 * 1024;
            case DiskImageTypes.DiskSizeValue.Flop35_1440:  return 1440 * 1024;
            case DiskImageTypes.DiskSizeValue.Other_32MB:   return 32 * 1024 * 1024;
            case DiskImageTypes.DiskSizeValue.Other_Custom:
                return StringToValue.SizeToBytes(_customSizeText, true);
            default:
                throw new NotImplementedException("Didn't handle size=" + mDiskSize);
        }
    }

    private bool GetNumBlocks(out uint blocks) {
        long volSize = GetVolSize();
        if (volSize < 0 || volSize % BLOCK_SIZE != 0) {
            blocks = 0;
            return false;
        }
        blocks = (uint)(volSize / BLOCK_SIZE);
        return blocks * (long)BLOCK_SIZE == volSize;
    }

    private bool GetNumTracksSectors(out uint tracks, out uint sectors) {
        switch (mDiskSize) {
            case DiskImageTypes.DiskSizeValue.Flop525_114:
                tracks = 35; sectors = 13; return true;
            case DiskImageTypes.DiskSizeValue.Flop525_140:
                tracks = 35; sectors = 16; return true;
            case DiskImageTypes.DiskSizeValue.Flop525_160:
                tracks = 40; sectors = 16; return true;
            case DiskImageTypes.DiskSizeValue.Flop35_400:
            case DiskImageTypes.DiskSizeValue.Flop35_800:
            case DiskImageTypes.DiskSizeValue.Flop35_1440:
            case DiskImageTypes.DiskSizeValue.Other_32MB:
            case DiskImageTypes.DiskSizeValue.Other_Custom:
                long volSize = GetVolSize();
                if (volSize == 400 * 1024) {
                    tracks = 50; sectors = 32; return true;
                } else {
                    tracks = sectors = 0; return false;
                }
            default:
                throw new NotImplementedException("Didn't handle size=" + mDiskSize);
        }
    }

    private bool GetMediaKind(out MediaKind kind) {
        switch (mDiskSize) {
            case DiskImageTypes.DiskSizeValue.Flop525_114:
            case DiskImageTypes.DiskSizeValue.Flop525_140:
            case DiskImageTypes.DiskSizeValue.Flop525_160:
                kind = MediaKind.GCR_525; return true;
            case DiskImageTypes.DiskSizeValue.Flop35_400:
                kind = MediaKind.GCR_SSDD35; return true;
            case DiskImageTypes.DiskSizeValue.Flop35_800:
                kind = MediaKind.GCR_DSDD35; return true;
            case DiskImageTypes.DiskSizeValue.Flop35_1440:
                kind = MediaKind.MFM_DSHD35; return true;
            case DiskImageTypes.DiskSizeValue.Other_32MB:
            case DiskImageTypes.DiskSizeValue.Other_Custom:
                kind = MediaKind.Unknown; return false;
            default:
                throw new NotImplementedException("Didn't handle size=" + mDiskSize);
        }
    }

    private bool IsFlop525 =>
        mDiskSize == DiskImageTypes.DiskSizeValue.Flop525_114 ||
        mDiskSize == DiskImageTypes.DiskSizeValue.Flop525_140 ||
        mDiskSize == DiskImageTypes.DiskSizeValue.Flop525_160;

    private bool IsFlop35 =>
        mDiskSize == DiskImageTypes.DiskSizeValue.Flop35_400 ||
        mDiskSize == DiskImageTypes.DiskSizeValue.Flop35_800 ||
        mDiskSize == DiskImageTypes.DiskSizeValue.Flop35_1440;

    private bool GetVolNum(out int volNum) {
        if (!int.TryParse(_volumeNumText, out volNum)) {
            return false;
        }
        return volNum is >= 0 and <= 254;
    }
}
