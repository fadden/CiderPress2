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
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using CommonUtil;
using DiskArc;
using DiskArc.FS;
using static DiskArc.Defs;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using cp2_avalonia.Services;

namespace cp2_avalonia.ViewModels;

/// <summary>
/// ViewModel for the EditSector dialog.
/// </summary>
public class EditSectorViewModel : ObservableObject
{
    private const int NUM_COLS = 16;

    // -----------------------------------------------------------------------------------------
    // Enums / delegates / inner types migrated from EditSector

    public enum SectorEditMode { Unknown = 0, Sectors, Blocks /*, CPMBlocks */ }
    public enum TxtConvMode { HighASCII, MOR, Latin }
    public delegate bool EnableWriteFunc();

    public static Formatter.CharConvFunc ModeToConverter(TxtConvMode mode) {
        return mode switch {
            TxtConvMode.HighASCII => Formatter.CharConv_HighASCII,
            TxtConvMode.MOR       => Formatter.CharConv_MOR,
            TxtConvMode.Latin     => Formatter.CharConv_Latin,
            _                     => Formatter.CharConv_HighASCII,
        };
    }

    // -----------------------------------------------------------------------------------------
    // SectorRow — inner data class for the hex grid

    public class SectorRow : ObservableObject
    {
        private readonly byte[] mBuffer;
        private readonly int mRowOffset;
        private TxtConvMode mConvMode;
        private Formatter.CharConvFunc mConverter;
        private readonly char[] mTextHolder = new char[16];

        public SectorRow(byte[] buf, int offset, TxtConvMode txtConvMode) {
            mBuffer = buf;
            mRowOffset = offset;
            mConvMode = txtConvMode;
            mConverter = ModeToConverter(txtConvMode);
            RowIndex = mRowOffset / NUM_COLS;
            if (mBuffer.Length == SECTOR_SIZE) {
                RowLabel = (mRowOffset & 0xf0).ToString("X2");
            } else {
                RowLabel = (mRowOffset & 0x1f0).ToString("X3");
            }
        }

        public int RowIndex { get; private set; }
        public string RowLabel { get; private set; }

        public string C0 { get { return Get(0x00); } set { Set(0x00, value); OnPropertyChanged(nameof(C0)); } }
        public string C1 { get { return Get(0x01); } set { Set(0x01, value); OnPropertyChanged(nameof(C1)); } }
        public string C2 { get { return Get(0x02); } set { Set(0x02, value); OnPropertyChanged(nameof(C2)); } }
        public string C3 { get { return Get(0x03); } set { Set(0x03, value); OnPropertyChanged(nameof(C3)); } }
        public string C4 { get { return Get(0x04); } set { Set(0x04, value); OnPropertyChanged(nameof(C4)); } }
        public string C5 { get { return Get(0x05); } set { Set(0x05, value); OnPropertyChanged(nameof(C5)); } }
        public string C6 { get { return Get(0x06); } set { Set(0x06, value); OnPropertyChanged(nameof(C6)); } }
        public string C7 { get { return Get(0x07); } set { Set(0x07, value); OnPropertyChanged(nameof(C7)); } }
        public string C8 { get { return Get(0x08); } set { Set(0x08, value); OnPropertyChanged(nameof(C8)); } }
        public string C9 { get { return Get(0x09); } set { Set(0x09, value); OnPropertyChanged(nameof(C9)); } }
        public string Ca { get { return Get(0x0a); } set { Set(0x0a, value); OnPropertyChanged(nameof(Ca)); } }
        public string Cb { get { return Get(0x0b); } set { Set(0x0b, value); OnPropertyChanged(nameof(Cb)); } }
        public string Cc { get { return Get(0x0c); } set { Set(0x0c, value); OnPropertyChanged(nameof(Cc)); } }
        public string Cd { get { return Get(0x0d); } set { Set(0x0d, value); OnPropertyChanged(nameof(Cd)); } }
        public string Ce { get { return Get(0x0e); } set { Set(0x0e, value); OnPropertyChanged(nameof(Ce)); } }
        public string Cf { get { return Get(0x0f); } set { Set(0x0f, value); OnPropertyChanged(nameof(Cf)); } }

        public TxtConvMode ConvMode {
            get => mConvMode;
            set {
                mConvMode = value;
                mConverter = ModeToConverter(value);
                OnPropertyChanged(nameof(AsText));
            }
        }

        public string AsText {
            get {
                for (int i = 0; i < NUM_COLS; i++) {
                    mTextHolder[i] = mConverter(mBuffer[mRowOffset + i]);
                }
                return new string(mTextHolder);
            }
        }

        private string Get(int col) => mBuffer[mRowOffset + col].ToString("X2");

        private void Set(int col, string value) {
            if (byte.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                    out byte parsed)) {
                mBuffer[mRowOffset + col] = parsed;
                OnPropertyChanged(nameof(AsText));
            }
        }

        public void PushDigit(int col, byte value) {
            switch (col) {
                case 0x00: C0 = DoPushDigit(col, value); break;
                case 0x01: C1 = DoPushDigit(col, value); break;
                case 0x02: C2 = DoPushDigit(col, value); break;
                case 0x03: C3 = DoPushDigit(col, value); break;
                case 0x04: C4 = DoPushDigit(col, value); break;
                case 0x05: C5 = DoPushDigit(col, value); break;
                case 0x06: C6 = DoPushDigit(col, value); break;
                case 0x07: C7 = DoPushDigit(col, value); break;
                case 0x08: C8 = DoPushDigit(col, value); break;
                case 0x09: C9 = DoPushDigit(col, value); break;
                case 0x0a: Ca = DoPushDigit(col, value); break;
                case 0x0b: Cb = DoPushDigit(col, value); break;
                case 0x0c: Cc = DoPushDigit(col, value); break;
                case 0x0d: Cd = DoPushDigit(col, value); break;
                case 0x0e: Ce = DoPushDigit(col, value); break;
                case 0x0f: Cf = DoPushDigit(col, value); break;
                default:
                    Debug.Assert(false);
                    break;
            }
        }

        private string DoPushDigit(int col, byte value) {
            byte curVal = mBuffer[mRowOffset + col];
            mBuffer[mRowOffset + col] = (byte)((curVal << 4) | value);
            return Get(col);
        }

        public void Refresh() {
            OnPropertyChanged(nameof(C0));
            OnPropertyChanged(nameof(C1));
            OnPropertyChanged(nameof(C2));
            OnPropertyChanged(nameof(C3));
            OnPropertyChanged(nameof(C4));
            OnPropertyChanged(nameof(C5));
            OnPropertyChanged(nameof(C6));
            OnPropertyChanged(nameof(C7));
            OnPropertyChanged(nameof(C8));
            OnPropertyChanged(nameof(C9));
            OnPropertyChanged(nameof(Ca));
            OnPropertyChanged(nameof(Cb));
            OnPropertyChanged(nameof(Cc));
            OnPropertyChanged(nameof(Cd));
            OnPropertyChanged(nameof(Ce));
            OnPropertyChanged(nameof(Cf));
            OnPropertyChanged(nameof(AsText));
        }
    }

    // -----------------------------------------------------------------------------------------
    // BlockOrderItem / SectorOrderItem

    public class BlockOrderItem(string label, SectorOrder order)
    {
        public string Label { get; } = label;
        public SectorOrder Order { get; } = order;
        public override string ToString() => Label;
    }

    public class SectorOrderItem(string label, SectorOrder order)
    {
        public string Label { get; } = label;
        public SectorOrder Order { get; } = order;
        public override string ToString() => Label;
    }

    // -----------------------------------------------------------------------------------------
    // Services / dependencies

    private readonly IChunkAccess mChunkAccess;
    private readonly SectorEditMode mEditMode;
    private readonly EnableWriteFunc? mEnableWriteFunc;
    private Formatter mFormatter;
    private readonly IDialogService _dialogService;
    private readonly IClipboardService _clipboardService;
    private readonly ISettingsService _settingsService;

    // -----------------------------------------------------------------------------------------
    // Static display-base state (persists across invocations)

    private static int sTrackNumBase = 10;
    private static int sSectorNumBase = 10;
    private static int sBlockNumBase = 10;

    // -----------------------------------------------------------------------------------------
    // Internal state

    private uint mCurBlockOrTrack;
    private uint mCurSector;
    private int mEnteredBlockOrTrack;
    private int mEnteredSector;
    private readonly byte[] mBuffer;
    public readonly int NumRows;
    private SectorOrder mSectorOrder;
    private bool mInitializingAdv;
    private bool mIsEntryValid;

    // -----------------------------------------------------------------------------------------
    // Close interaction (MVVM pattern for dialog dismissal)

    public event Action<bool>? CloseRequested;

    // -----------------------------------------------------------------------------------------
    // Observable properties

    public List<SectorRow> SectorData { get; } = new List<SectorRow>();

    private bool mIsSectorDataGridVisible = true;
    public bool IsSectorDataGridVisible {
        get => mIsSectorDataGridVisible;
        set => SetProperty(ref mIsSectorDataGridVisible, value);
    }

    private string mSectorDataLabel = string.Empty;
    public string SectorDataLabel {
        get => mSectorDataLabel;
        set => SetProperty(ref mSectorDataLabel, value);
    }

    public bool IsSectorVisible { get; private set; }
    public string TrackBlockLabel { get; private set; } = string.Empty;
    public string DialogTitle { get; private set; } = string.Empty;

    // Validation-color booleans (replaces IBrush properties)
    private bool mIsTrackBlockValid = true;
    public bool IsTrackBlockValid {
        get => mIsTrackBlockValid;
        set => SetProperty(ref mIsTrackBlockValid, value);
    }

    private bool mIsSectorValid = true;
    public bool IsSectorValid {
        get => mIsSectorValid;
        set => SetProperty(ref mIsSectorValid, value);
    }

    public string TrackBlockInfoLabel { get; private set; } = string.Empty;
    public string SectorInfoLabel { get; private set; } = string.Empty;

    private string mTrackBlockNumString = string.Empty;
    public string TrackBlockNumString {
        get => mTrackBlockNumString;
        set { SetProperty(ref mTrackBlockNumString, value); TrackSectorUpdated(); }
    }

    private string mSectorNumString = string.Empty;
    public string SectorNumString {
        get => mSectorNumString;
        set { SetProperty(ref mSectorNumString, value); TrackSectorUpdated(); }
    }

    private string mIOErrorMsg = "I/O Error";
    public string IOErrorMsg {
        get => mIOErrorMsg;
        set => SetProperty(ref mIOErrorMsg, value);
    }

    private bool mIsIOErrorMsgVisible;
    public bool IsIOErrorMsgVisible {
        get => mIsIOErrorMsgVisible;
        set => SetProperty(ref mIsIOErrorMsgVisible, value);
    }

    private bool mIsDirty;
    public bool IsDirty {
        get => mIsDirty;
        set => SetProperty(ref mIsDirty, value);
    }

    /// <summary>True if writes have been enabled in the chunk.</summary>
    private bool mWritesEnabled;
    public bool WritesEnabled {
        get => mWritesEnabled;
        private set => SetProperty(ref mWritesEnabled, value);
    }

    private TxtConvMode mTxtConvMode;
    public bool IsChecked_ConvHighASCII {
        get => mTxtConvMode == TxtConvMode.HighASCII;
        set {
            if (value) { mTxtConvMode = TxtConvMode.HighASCII; UpdateTxtConv(); }
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsChecked_ConvMOR));
            OnPropertyChanged(nameof(IsChecked_ConvLatin));
        }
    }
    public bool IsChecked_ConvMOR {
        get => mTxtConvMode == TxtConvMode.MOR;
        set {
            if (value) { mTxtConvMode = TxtConvMode.MOR; UpdateTxtConv(); }
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsChecked_ConvHighASCII));
            OnPropertyChanged(nameof(IsChecked_ConvLatin));
        }
    }
    public bool IsChecked_ConvLatin {
        get => mTxtConvMode == TxtConvMode.Latin;
        set {
            if (value) { mTxtConvMode = TxtConvMode.Latin; UpdateTxtConv(); }
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsChecked_ConvHighASCII));
            OnPropertyChanged(nameof(IsChecked_ConvMOR));
        }
    }

    private bool mIsPrevEnabled;
    public bool IsPrevEnabled {
        get => mIsPrevEnabled;
        set { if (SetProperty(ref mIsPrevEnabled, value)) PrevCommand?.NotifyCanExecuteChanged(); }
    }

    private bool mIsNextEnabled;
    public bool IsNextEnabled {
        get => mIsNextEnabled;
        set { if (SetProperty(ref mIsNextEnabled, value)) NextCommand?.NotifyCanExecuteChanged(); }
    }

    public bool IsReadButtonEnabled => mIsEntryValid;
    public bool IsWriteButtonEnabled => mEnableWriteFunc != null && mIsEntryValid;
    public bool IsEditable => mEnableWriteFunc != null;

    // Block order
    public bool IsBlockOrderVisible { get; private set; }
    public bool IsBlockOrderEnabled { get; private set; }
    public List<BlockOrderItem> BlockOrderList { get; } = new List<BlockOrderItem>();

    private int mSelectedBlockOrderIndex = -1;
    public int SelectedBlockOrderIndex {
        get => mSelectedBlockOrderIndex;
        set {
            if (mInitializingAdv || value == mSelectedBlockOrderIndex) return;
            SetProperty(ref mSelectedBlockOrderIndex, value);
            _ = OnBlockOrderChangedAsync(value);
        }
    }

    // Sector order
    public bool IsSectorOrderEnabled { get; private set; }
    public List<SectorOrderItem> SectorOrderList { get; } = new List<SectorOrderItem>() {
        new SectorOrderItem("DOS 3.3", SectorOrder.DOS_Sector),
        new SectorOrderItem("ProDOS",  SectorOrder.ProDOS_Block),
        new SectorOrderItem("CP/M",    SectorOrder.CPM_KBlock),
        new SectorOrderItem("Physical",SectorOrder.Physical)
    };

    private int mSelectedSectorOrderIndex = -1;
    public int SelectedSectorOrderIndex {
        get => mSelectedSectorOrderIndex;
        set {
            if (mInitializingAdv || value == mSelectedSectorOrderIndex) return;
            SetProperty(ref mSelectedSectorOrderIndex, value);
            _ = OnSectorOrderChangedAsync(value);
        }
    }

    // Sector codec
    private string mSectorCodecName = string.Empty;
    public string SectorCodecName {
        get => mSectorCodecName;
        private set => SetProperty(ref mSectorCodecName, value);
    }

    // -----------------------------------------------------------------------------------------
    // Commands

    public IRelayCommand ReadCommand { get; }
    public IRelayCommand WriteCommand { get; }
    public IRelayCommand PrevCommand { get; }
    public IRelayCommand NextCommand { get; }
    public IRelayCommand CopyToClipboardCommand { get; }
    public IRelayCommand CloseCommand { get; }

    // -----------------------------------------------------------------------------------------
    // Constructor

    private bool CanRead() => IsReadButtonEnabled;
    private bool CanWrite() => IsWriteButtonEnabled;
    private bool CanPrev() => IsPrevEnabled;
    private bool CanNext() => IsNextEnabled;

    public EditSectorViewModel(
        IChunkAccess chunks,
        SectorEditMode editMode,
        EnableWriteFunc? enableWriteFunc,
        Formatter formatter,
        IDialogService dialogService,
        IClipboardService clipboardService,
        ISettingsService settingsService)
    {
        mChunkAccess = chunks;
        mEnableWriteFunc = enableWriteFunc;
        mFormatter = formatter;
        _dialogService = dialogService;
        _clipboardService = clipboardService;
        _settingsService = settingsService;

        Debug.Assert(mEnableWriteFunc == null || !mChunkAccess.IsReadOnly);

        mTxtConvMode = _settingsService.GetEnum(AppSettings.SCTED_TEXT_CONV_MODE, TxtConvMode.HighASCII);

        // Normalize editMode (CPMBlocks → Blocks internally)
        if (editMode == SectorEditMode.Sectors && !chunks.HasSectors) {
            editMode = SectorEditMode.Blocks;
        }
        bool asSectors = (editMode == SectorEditMode.Sectors);

        switch (editMode) {
            case SectorEditMode.Sectors:
                mSectorOrder = SectorOrder.DOS_Sector;
                DialogTitle = "Edit Sectors";
                break;
            case SectorEditMode.Blocks:
                mSectorOrder = SectorOrder.ProDOS_Block;
                DialogTitle = "Edit Blocks";
                break;
            // case SectorEditMode.CPMBlocks:  // TODO: This probably should be called as a CTOR anymore.
            //     editMode = SectorEditMode.Blocks;
            //     mSectorOrder = SectorOrder.CPM_KBlock;
            //     DialogTitle = "Edit Blocks (CP/M)";
            //     break;
            default:
                Debug.Assert(false);
                mSectorOrder = SectorOrder.Physical;
                DialogTitle = "Edit THING";
                break;
        }
        mEditMode = editMode;

        mBuffer = new byte[asSectors ? SECTOR_SIZE : BLOCK_SIZE];
        NumRows = mBuffer.Length / NUM_COLS;

        for (int i = 0; i < mBuffer.Length; i += NUM_COLS) {
            SectorData.Add(new SectorRow(mBuffer, i, mTxtConvMode));
        }

        mCurBlockOrTrack = 0;
        mCurSector = 0;
        SetPrevNextEnabled();
        UpdateTxtConv();
        ReadFromDisk();

        if (asSectors) {
            IsSectorVisible = true;
            TrackBlockLabel = "Track:";
            TrackBlockInfoLabel = string.Format("\u2022 Track is 0-{0} (${1:X})",
                mChunkAccess.NumTracks - 1, mChunkAccess.NumTracks - 1);
            SectorInfoLabel = string.Format("\u2022 Sector is 0-{0} (${1:X})",
                mChunkAccess.NumSectorsPerTrack - 1, mChunkAccess.NumSectorsPerTrack - 1);
        } else {
            IsSectorVisible = false;
            TrackBlockLabel = "Block:";
            int numBlocks = (int)(mChunkAccess.FormattedLength / BLOCK_SIZE);
            TrackBlockInfoLabel = string.Format("\u2022 Block is 0-{0} (${1:X})",
                numBlocks - 1, numBlocks - 1);
            SectorInfoLabel = string.Empty;
        }
        CopyNumToTextFields();

        PrepareBlockOrder();
        PrepareSectorOrder();
        PrepareSectorCodec();

        // Commands

        ReadCommand  = new AsyncRelayCommand(ReadButtonClickedAsync, CanRead);
        WriteCommand = new AsyncRelayCommand(WriteButtonClickedAsync, CanWrite);
        PrevCommand  = new AsyncRelayCommand(PrevButtonClickedAsync, CanPrev);
        NextCommand  = new AsyncRelayCommand(NextButtonClickedAsync, CanNext);

        CopyToClipboardCommand = new AsyncRelayCommand(CopyToClipboardAsync);
        CloseCommand = new AsyncRelayCommand(async () =>
            CloseRequested?.Invoke(false));
    }

    // -----------------------------------------------------------------------------------------
    // Read / Write

    private void ReadFromDisk() {
        try {
            switch (mEditMode) {
                case SectorEditMode.Sectors:
                    mChunkAccess.ReadSector(mCurBlockOrTrack, mCurSector, mBuffer, 0, mSectorOrder);
                    break;
                default: // Blocks
                    mChunkAccess.ReadBlock(mCurBlockOrTrack, mBuffer, 0, mSectorOrder);
                    break;
            }
            IsSectorDataGridVisible = true;
            IsIOErrorMsgVisible = false;
        } catch (BadBlockException ex) {
            AppLog.W("Sector editor: read error", ex);
            RawData.MemSet(mBuffer, 0, mBuffer.Length, 0xcc);
            IsSectorDataGridVisible = false;
            IsIOErrorMsgVisible = true;
        }
        foreach (SectorRow row in SectorData) {
            row.Refresh();
        }
        IsDirty = false;
        SetSectorDataLabel();
    }

    private async Task ReadFromDiskAsync() {
        if (!await ConfirmDiscardChangesAsync()) return;
        ReadFromDisk();
    }

    private async Task WriteToDiskAsync() {
        if (!TryEnableWrites()) {
            await _dialogService.ShowMessageAsync("Unable to write to this disk", "Not Possible");
            return;
        }
        try {
            switch (mEditMode) {
                case SectorEditMode.Sectors:
                    mChunkAccess.WriteSector(mCurBlockOrTrack, mCurSector, mBuffer, 0, mSectorOrder);
                    break;
                default: // Blocks
                    mChunkAccess.WriteBlock(mCurBlockOrTrack, mBuffer, 0, mSectorOrder);
                    break;
            }
            IsDirty = false;
        } catch (Exception ex) {
            AppLog.E("Sector editor: write failed", ex);
            await _dialogService.ShowMessageAsync("Write failed: " + ex.Message, "Error");
        }
        SetSectorDataLabel();
    }

    private void SetSectorDataLabel() {
        string dirtyStr = IsDirty ? " [*]" : string.Empty;
        if (mEditMode == SectorEditMode.Sectors) {
            SectorDataLabel = string.Format("Track {0} (${0:X2}), Sector {1} (${1:X}) {2}",
                mCurBlockOrTrack, mCurSector, dirtyStr);
        } else if (mSectorOrder == SectorOrder.CPM_KBlock) {
            uint allocBlock = CPM.DiskBlockToAllocBlock(mCurBlockOrTrack,
                    mChunkAccess.FormattedLength, out uint offset);
            if (allocBlock != uint.MaxValue) {
                SectorDataLabel = string.Format("Block {0} (${0:X}) / Alloc {1}.{2} (${1:X2}) {3}",
                    mCurBlockOrTrack, allocBlock, offset / BLOCK_SIZE, dirtyStr);
            } else {
                SectorDataLabel = string.Format("Block {0} (${0:X2}) / Alloc --.- ($--) {1}",
                    mCurBlockOrTrack, dirtyStr);
            }
        } else {
            SectorDataLabel = string.Format("Block {0} (${0:X2}) {1}", mCurBlockOrTrack, dirtyStr);
        }
    }

    private void SetPrevNextEnabled() {
        bool hasPrev, hasNext;
        if (mEditMode == SectorEditMode.Sectors) {
            hasPrev = (mCurBlockOrTrack != 0 || mCurSector != 0);
            hasNext = (mCurBlockOrTrack != mChunkAccess.NumTracks - 1 ||
                mCurSector != mChunkAccess.NumSectorsPerTrack - 1);
        } else {
            long numBlocks = mChunkAccess.FormattedLength / BLOCK_SIZE;
            hasPrev = (mCurBlockOrTrack != 0);
            hasNext = (mCurBlockOrTrack != numBlocks - 1);
        }
        IsPrevEnabled = hasPrev;
        IsNextEnabled = hasNext;
    }

    private void CopyNumToTextFields() {
        // Use SetField directly to avoid triggering TrackSectorUpdated mid-init.
        string fmtStr = "{0:D}";
        if (mEditMode == SectorEditMode.Sectors) {
            if (sTrackNumBase == 16) fmtStr = "${0:X2}";
        } else {
            if (sBlockNumBase == 16) fmtStr = "${0:X4}";
        }
        mTrackBlockNumString = string.Format(fmtStr, mCurBlockOrTrack);
        OnPropertyChanged(nameof(TrackBlockNumString));

        fmtStr = "{0:D}";
        if (sSectorNumBase == 16) fmtStr = "${0:X2}";
        mSectorNumString = string.Format(fmtStr, mCurSector);
        OnPropertyChanged(nameof(SectorNumString));

        // Sync entered values and button states after Prev/Next navigation,
        // since the property setters are bypassed above.
        TrackSectorUpdated();
    }

    private void UpdateTxtConv() {
        _settingsService.SetEnum(AppSettings.SCTED_TEXT_CONV_MODE, mTxtConvMode);
        foreach (SectorRow row in SectorData) {
            row.ConvMode = mTxtConvMode;
        }
        Formatter.FormatConfig config = mFormatter.Config;
        config.HexDumpConvFunc = ModeToConverter(mTxtConvMode);
        mFormatter = new Formatter(config);
    }

    private void TrackSectorUpdated() {
        int val, intBase;

        if (StringToValue.TryParseInt(TrackBlockNumString, out val, out intBase)) {
            mEnteredBlockOrTrack = val;
            if (mEditMode == SectorEditMode.Sectors) {
                sTrackNumBase = intBase;
            } else {
                sBlockNumBase = intBase;
            }
        } else {
            mEnteredBlockOrTrack = -1;
        }
        if (StringToValue.TryParseInt(SectorNumString, out val, out intBase)) {
            mEnteredSector = val;
            sSectorNumBase = intBase;
        } else {
            mEnteredSector = -1;
        }

        bool trackBlockValid, sectorValid;
        if (mEditMode == SectorEditMode.Sectors) {
            trackBlockValid = mEnteredBlockOrTrack >= 0 &&
                mEnteredBlockOrTrack < mChunkAccess.NumTracks;
            sectorValid = mEnteredSector >= 0 &&
                mEnteredSector < mChunkAccess.NumSectorsPerTrack;
        } else {
            long numBlocks = mChunkAccess.FormattedLength / BLOCK_SIZE;
            trackBlockValid = mEnteredBlockOrTrack >= 0 && mEnteredBlockOrTrack < numBlocks;
            sectorValid = true;
        }

        IsTrackBlockValid = trackBlockValid;
        IsSectorValid = sectorValid;
        mIsEntryValid = trackBlockValid && sectorValid;
        OnPropertyChanged(nameof(IsReadButtonEnabled));
        ReadCommand?.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsWriteButtonEnabled));
        WriteCommand?.NotifyCanExecuteChanged();
    }

    private bool TryEnableWrites() {
        if (mEnableWriteFunc == null) return false;
        if (WritesEnabled) return true;
        if (!mEnableWriteFunc()) {
            AppLog.W("Sector editor: failed to enable write access");
            _ = _dialogService.ShowMessageAsync("Failed to enable write access", "Whoops");
            return false;
        }
        WritesEnabled = true;
        Debug.WriteLine("Writes enabled");
        return true;
    }

    // -----------------------------------------------------------------------------------------
    // Button command implementations

    private async Task ReadButtonClickedAsync() {
        mCurBlockOrTrack = (uint)mEnteredBlockOrTrack;
        mCurSector = (uint)mEnteredSector;
        SetPrevNextEnabled();
        await ReadFromDiskAsync();
    }

    private async Task PrevButtonClickedAsync() {
        if (mEditMode == SectorEditMode.Sectors) {
            if (mCurSector != 0) {
                mCurSector--;
            } else {
                mCurSector = mChunkAccess.NumSectorsPerTrack - 1;
                mCurBlockOrTrack--;
            }
        } else {
            mCurBlockOrTrack--;
        }
        SetPrevNextEnabled();
        CopyNumToTextFields();
        await ReadFromDiskAsync();
    }

    private async Task NextButtonClickedAsync() {
        if (mEditMode == SectorEditMode.Sectors) {
            mCurSector++;
            if (mCurSector == mChunkAccess.NumSectorsPerTrack) {
                mCurSector = 0;
                mCurBlockOrTrack++;
            }
        } else {
            mCurBlockOrTrack++;
        }
        SetPrevNextEnabled();
        CopyNumToTextFields();
        await ReadFromDiskAsync();
    }

    private async Task WriteButtonClickedAsync() {
        Debug.Assert(!mChunkAccess.IsReadOnly);
        if (mCurBlockOrTrack != mEnteredBlockOrTrack || mCurSector != mEnteredSector) {
            bool ok = await _dialogService.ShowConfirmAsync(
                "This will write to a different sector than was read from. Proceed?",
                "Confirm");
            if (!ok) return;
        }
        mCurBlockOrTrack = (uint)mEnteredBlockOrTrack;
        mCurSector = (uint)mEnteredSector;
        SetPrevNextEnabled();
        await WriteToDiskAsync();
    }

    private async Task CopyToClipboardAsync() {
        if (IsIOErrorMsgVisible) return;
        string dumpText = mFormatter.FormatHexDump(mBuffer).ToString();
        await _clipboardService.SetTextAsync(dumpText);
    }

    /// <summary>
    /// Called by code-behind Window_Closing when the user tries to close with dirty data.
    /// Returns true if it's okay to close (discard changes).
    /// </summary>
    public async Task<bool> ConfirmDiscardChangesAsync() {
        if (!IsDirty) return true;
        return await _dialogService.ShowConfirmAsync("Discard unwritten modifications?", "Confirm");
    }

    // -----------------------------------------------------------------------------------------
    // Block order

    private void PrepareBlockOrder() {
        if (mEditMode == SectorEditMode.Sectors) {
            IsBlockOrderVisible = false;
            return;
        }
        IsBlockOrderVisible = true;
        BlockOrderList.Add(new BlockOrderItem("ProDOS", SectorOrder.ProDOS_Block));
        if (CPM.IsSizeAllowed(mChunkAccess.FormattedLength) &&
                mChunkAccess.NumSectorsPerTrack == 16) {
            BlockOrderList.Add(new BlockOrderItem("CP/M", SectorOrder.CPM_KBlock));
        }
        IsBlockOrderEnabled = BlockOrderList.Count > 1;

        mInitializingAdv = true;
        int match = 0;
        for (int i = 0; i < BlockOrderList.Count; i++) {
            if (BlockOrderList[i].Order == mSectorOrder) { match = i; break; }
        }
        mSelectedBlockOrderIndex = match;
        mInitializingAdv = false;
    }

    private async Task OnBlockOrderChangedAsync(int idx) {
        if (idx < 0 || idx >= BlockOrderList.Count) return;
        BlockOrderItem item = BlockOrderList[idx];
        if (item.Order == mSectorOrder) return;

        if (mIsDirty) {
            bool discard = await _dialogService.ShowConfirmAsync(
                "Changing the block order will abandon your pending changes. Continue?",
                "Pending Changes");
            if (!discard) {
                // Revert
                mInitializingAdv = true;
                for (int i = 0; i < BlockOrderList.Count; i++) {
                    if (BlockOrderList[i].Order == mSectorOrder) {
                        mSelectedBlockOrderIndex = i;
                        OnPropertyChanged(nameof(SelectedBlockOrderIndex));
                        break;
                    }
                }
                mInitializingAdv = false;
                return;
            }
        }
        mSectorOrder = item.Order;
        ReadFromDisk();
    }

    // -----------------------------------------------------------------------------------------
    // Sector order

    private void PrepareSectorOrder() {
        if (!mChunkAccess.HasSectors || mChunkAccess.NumSectorsPerTrack != 16 ||
                (mChunkAccess.HasBlocks && mChunkAccess.NibbleCodec == null)) {
            IsSectorOrderEnabled = false;
            return;
        }
        IsSectorOrderEnabled = true;

        mInitializingAdv = true;
        int match = 0;
        for (int i = 0; i < SectorOrderList.Count; i++) {
            if (SectorOrderList[i].Order == mSectorOrder) { match = i; break; }
        }
        mSelectedSectorOrderIndex = match;
        mInitializingAdv = false;
    }

    private async Task OnSectorOrderChangedAsync(int idx) {
        if (idx < 0 || idx >= SectorOrderList.Count) return;
        SectorOrderItem item = SectorOrderList[idx];
        if (item.Order == mSectorOrder) return;
        mSectorOrder = item.Order;
        await ReadFromDiskAsync();
    }

    // -----------------------------------------------------------------------------------------
    // Sector codec

    private void PrepareSectorCodec() {
        SectorCodecName = mChunkAccess.NibbleCodec == null ? "N/A" : mChunkAccess.NibbleCodec.Name;
    }

    // -----------------------------------------------------------------------------------------
    // Keyboard entry — called from code-behind

    public bool TryEnableWritesPublic() => TryEnableWrites();

    public void MarkDirtyAndUpdateLabel() {
        IsDirty = true;
        SetSectorDataLabel();
    }
}
