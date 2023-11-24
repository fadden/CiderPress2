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
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Media;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

using CommonUtil;
using cp2_wpf.WPFCommon;
using DiskArc;
using static DiskArc.Defs;

namespace cp2_wpf {
    /// <summary>
    /// Sector edit dialog.
    /// </summary>
    /// <remarks>
    /// <para>Currently using a DataGrid to hold the block/sector data.  It works better than I
    /// anticipated.</para>
    /// </remarks>
    public partial class EditSector : Window, INotifyPropertyChanged {
        private const int NUM_COLS = 16;

        public enum SectorEditMode {
            Unknown = 0, Sectors, Blocks, CPMBlocks
        }

        public enum TxtConvMode { HighASCII, MOR, Latin }
        private static Formatter.CharConvFunc ModeToConverter(TxtConvMode mode) {
            switch (mode) {
                case TxtConvMode.HighASCII:
                default:
                    return Formatter.CharConv_HighASCII;
                case TxtConvMode.MOR:
                    return Formatter.CharConv_MOR;
                case TxtConvMode.Latin:
                    return Formatter.CharConv_Latin;
            }
        }

        public class SectorRow : INotifyPropertyChanged {
            public event PropertyChangedEventHandler? PropertyChanged;
            private void OnPropertyChanged([CallerMemberName] string propertyName = "") {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }

            private byte[] mBuffer;
            private int mRowOffset;

            /// <summary>
            /// Constructor.
            /// </summary>
            /// <param name="buf"></param>
            /// <param name="offset"></param>
            /// <param name="txtConvMode"></param>
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

            public string C0 { get { return Get(0x00); }
                set { Set(0x00, value); OnPropertyChanged(); } }
            public string C1 { get { return Get(0x01); }
                set { Set(0x01, value); OnPropertyChanged(); } }
            public string C2 { get { return Get(0x02); }
                set { Set(0x02, value); OnPropertyChanged(); } }
            public string C3 { get { return Get(0x03); }
                set { Set(0x03, value); OnPropertyChanged(); } }
            public string C4 { get { return Get(0x04); }
                set { Set(0x04, value); OnPropertyChanged(); } }
            public string C5 { get { return Get(0x05); }
                set { Set(0x05, value); OnPropertyChanged(); } }
            public string C6 { get { return Get(0x06); }
                set { Set(0x06, value); OnPropertyChanged(); } }
            public string C7 { get { return Get(0x07); }
                set { Set(0x07, value); OnPropertyChanged(); } }
            public string C8 { get { return Get(0x08); }
                set { Set(0x08, value); OnPropertyChanged(); } }
            public string C9 { get { return Get(0x09); }
                set { Set(0x09, value); OnPropertyChanged(); } }
            public string Ca { get { return Get(0x0a); }
                set { Set(0x0a, value); OnPropertyChanged(); } }
            public string Cb { get { return Get(0x0b); }
                set { Set(0x0b, value); OnPropertyChanged(); } }
            public string Cc { get { return Get(0x0c); }
                set { Set(0x0c, value); OnPropertyChanged(); } }
            public string Cd { get { return Get(0x0d); }
                set { Set(0x0d, value); OnPropertyChanged(); } }
            public string Ce { get { return Get(0x0e); }
                set { Set(0x0e, value); OnPropertyChanged(); } }
            public string Cf { get { return Get(0x0f); }
                set { Set(0x0f, value); OnPropertyChanged(); } }

            /// <summary>
            /// Text conversion mode.
            /// </summary>
            public TxtConvMode ConvMode {
                get { return mConvMode; }
                set {
                    mConvMode = value;
                    mConverter = ModeToConverter(value);
                    OnPropertyChanged("AsText");
                }
            }
            private TxtConvMode mConvMode;
            private Formatter.CharConvFunc mConverter;

            private char[] mTextHolder = new char[16];
            public string AsText {
                get {
                    for (int i = 0; i < NUM_COLS; i++) {
                        mTextHolder[i] = mConverter(mBuffer[mRowOffset + i]);
                    }
                    return new string(mTextHolder);
                }
            }

            private string Get(int col) {
                return mBuffer[mRowOffset + col].ToString("X2");
            }
            private void Set(int col, string value) {
                try {
                    byte conv = Convert.ToByte(value, 16);
                    mBuffer[mRowOffset + col] = conv;
                    OnPropertyChanged("AsText");        // update text representation too
                } catch {
                    Debug.Assert(false, "invalid byte value '" + value + "'");
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

        private Brush mDefaultLabelColor = SystemColors.WindowTextBrush;
        private Brush mErrorLabelColor = Brushes.Red;

        /// <summary>
        /// Collection of rows that make up the block/sector data.
        /// </summary>
        public List<SectorRow> SectorData { get; set; } = new List<SectorRow>();

        public Visibility SectorDataGridVisibility {
            get { return mSectorDataGridVisibility; }
            set { mSectorDataGridVisibility = value; OnPropertyChanged(); }
        }
        private Visibility mSectorDataGridVisibility = Visibility.Visible;

        public string SectorDataLabel {
            get { return mSectorDataLabel; }
            set { mSectorDataLabel = value; OnPropertyChanged(); }
        }
        private string mSectorDataLabel = "LABEL HERE";

        public Visibility SectorVisibility { get; private set; }
        public string TrackBlockLabel { get; private set; }
        public Brush TrackBlockLabelForeground {
            get { return mTrackBlockLabelForeground; }
            set { mTrackBlockLabelForeground = value; OnPropertyChanged(); }
        }
        private Brush mTrackBlockLabelForeground = SystemColors.WindowTextBrush;
        public Brush SectorLabelForeground {
            get { return mSectorLabelForeground; }
            set { mSectorLabelForeground = value; OnPropertyChanged(); }
        }
        private Brush mSectorLabelForeground = SystemColors.WindowTextBrush;

        public string TrackBlockInfoLabel { get; private set; }
        public string SectorInfoLabel { get; private set; }

        public string TrackBlockNumString {
            get { return mTrackBlockNumString; }
            set { mTrackBlockNumString = value; OnPropertyChanged(); TrackSectorUpdated(); }
        }
        private string mTrackBlockNumString = string.Empty;

        public string SectorNumString {
            get { return mSectorNumString; }
            set { mSectorNumString = value; OnPropertyChanged(); TrackSectorUpdated(); }
        }
        private string mSectorNumString = string.Empty;

        public string IOErrorMsg {
            get { return mIOErrorMsg; }
            set { mIOErrorMsg = value; OnPropertyChanged(); TrackSectorUpdated(); }
        }
        private string mIOErrorMsg = "I/O Error";

        public Visibility IOErrorMsgVisibility {
            get { return mIOErrorMsgVisibility; }
            set {  mIOErrorMsgVisibility = value; OnPropertyChanged(); }
        }
        private Visibility mIOErrorMsgVisibility = Visibility.Collapsed;

        public delegate bool EnableWriteFunc();
        private EnableWriteFunc? mEnableWriteFunc;

        /// <summary>
        /// Text conversion mode for hex dump.  Set by radio button.
        /// </summary>
        private TxtConvMode mTxtConvMode;

        public bool IsChecked_ConvHighASCII {
            get { return mTxtConvMode == TxtConvMode.HighASCII; }
            set {
                if (value) {
                    mTxtConvMode = TxtConvMode.HighASCII;
                    UpdateTxtConv();
                }
                OnPropertyChanged();
            }
        }
        public bool IsChecked_ConvMOR {
            get { return mTxtConvMode == TxtConvMode.MOR; }
            set {
                if (value) {
                    mTxtConvMode = TxtConvMode.MOR;
                    UpdateTxtConv();
                }
                OnPropertyChanged();
            }
        }
        public bool IsChecked_ConvLatin {
            get { return mTxtConvMode == TxtConvMode.Latin; }
            set {
                if (value) {
                    mTxtConvMode = TxtConvMode.Latin;
                    UpdateTxtConv();
                }
                OnPropertyChanged();
            }
        }

        // INotifyPropertyChanged implementation
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string propertyName = "") {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// True if writes have been enabled in the chunk.
        /// </summary>
        private bool mIsWritingEnabled;

        /// <summary>
        /// True if buffer has been modified.
        /// </summary>
        public bool IsDirty {
            get { return mIsDirty; }
            set { mIsDirty = value; }
        }
        private bool mIsDirty;

        /// <summary>
        /// True if the text entered into the track/block/sector fields is valid.
        /// </summary>
        private bool mIsEntryValid;

        /// <summary>
        /// True if the "Write" button should be enabled in the GUI.  Depends on whether writing
        /// is possible for this volume, and the contents of the entry fields.
        /// </summary>
        public bool IsWriteButtonEnabled {
            get { return mEnableWriteFunc != null && mIsEntryValid; }
        }

        /// <summary>
        /// True if the "Read" button should be enabled in the GUI.  Depends on the contents
        /// of the entry fields.
        /// </summary>
        public bool IsReadButtonEnabled {
            get { return mIsEntryValid; }
        }

        public bool IsPrevEnabled {
            get { return mIsPrevEnabled; }
            set { mIsPrevEnabled = value; OnPropertyChanged(); }
        }
        private bool mIsPrevEnabled;
        public bool IsNextEnabled {
            get { return mIsNextEnabled; }
            set { mIsNextEnabled = value; OnPropertyChanged(); }
        }
        private bool mIsNextEnabled;

        private IChunkAccess mChunkAccess;
        private SectorEditMode mEditMode;
        private Formatter mFormatter;

        private uint mCurBlockOrTrack;
        private uint mCurSector;
        private int mEnteredBlockOrTrack;
        private int mEnteredSector;

        private byte[] mBuffer;
        private int mCurCol;
        private int mCurRow;
        private int mCurDigit;
        private readonly int mNumRows;

        private SectorOrder mSectorOrder;

        private bool InTextArea { get { return mCurCol == NUM_COLS; } }

        // Keep track of the integer base used for track, sector, and block numbers.  Store it in
        // a static so it persists for the session.
        private static int sTrackNumBase = 10;
        private static int sSectorNumBase = 10;
        private static int sBlockNumBase = 10;


        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="owner">Parent window.</param>
        /// <param name="chunks">Chunk data source.</param>
        /// <param name="mode">Edit mode.  This will be ignored if the mode is Sectors but the
        ///   chunk source can't operate on sectors.</param>
        /// <param name="enableWriteFunc">Function to call to enable writes.  Will be null if
        ///   we don't allow writes (read-only volume).</param>
        /// <param name="formatter">Text formatter.</param>
        public EditSector(Window owner, IChunkAccess chunks, SectorEditMode editMode,
                EnableWriteFunc? enableWriteFunc, Formatter formatter) {
            InitializeComponent();
            Owner = owner;
            DataContext = this;

            mChunkAccess = chunks;
            mEditMode = editMode;
            mEnableWriteFunc = enableWriteFunc;
            mFormatter = formatter;

            Debug.Assert(mEnableWriteFunc == null || !mChunkAccess.IsReadOnly);

            SettingsHolder settings = AppSettings.Global;
            mTxtConvMode =
                settings.GetEnum(AppSettings.SCTED_TEXT_CONV_MODE, TxtConvMode.HighASCII);

            if (editMode == SectorEditMode.Sectors && !chunks.HasSectors) {
                editMode = SectorEditMode.Blocks;
            }
            bool asSectors = (editMode == SectorEditMode.Sectors);
            switch (editMode) {
                case SectorEditMode.Sectors:
                    Title = "Edit Sectors";
                    mSectorOrder = SectorOrder.DOS_Sector;
                    break;
                case SectorEditMode.Blocks:
                    Title = "Edit Blocks";
                    mSectorOrder = SectorOrder.ProDOS_Block;
                    break;
                case SectorEditMode.CPMBlocks:
                    Title = "Edit Blocks (CP/M)";
                    mSectorOrder = SectorOrder.CPM_KBlock;
                    break;
                default:
                    Debug.Assert(false);
                    mSectorOrder = SectorOrder.Physical;
                    break;
            }

            mBuffer = new byte[asSectors ? SECTOR_SIZE : BLOCK_SIZE];
            mNumRows = mBuffer.Length / NUM_COLS;

            for (int i = 0; i < mBuffer.Length; i += NUM_COLS) {
                SectorRow row = new SectorRow(mBuffer, i, mTxtConvMode);
                SectorData.Add(row);
            }
            mCurBlockOrTrack = 0;
            mCurSector = 0;
            SetPrevNextEnabled();
            UpdateTxtConv();
            ReadFromDisk();

            if (asSectors) {
                SectorVisibility = Visibility.Visible;
                TrackBlockLabel = "Track:";
                TrackBlockInfoLabel = string.Format("\u2022 Track is 0-{0} (${1:X})",
                    mChunkAccess.NumTracks - 1, mChunkAccess.NumTracks - 1);
                SectorInfoLabel = string.Format("\u2022 Sector is 0-{0} (${1:X})",
                    mChunkAccess.NumSectorsPerTrack - 1, mChunkAccess.NumSectorsPerTrack - 1);
            } else {
                SectorVisibility = Visibility.Collapsed;
                TrackBlockLabel = "Block:";
                int numBlocks = (int)(mChunkAccess.FormattedLength / BLOCK_SIZE);
                TrackBlockInfoLabel = string.Format("\u2022 Block is 0-{0} (${1:X})",
                    numBlocks - 1, numBlocks - 1);
                SectorInfoLabel = string.Empty;
            }
            CopyNumToTextFields();

            mCurRow = mCurCol = 0;
            mCurDigit = 0;

            PrepareSectorOrder();
            PrepareSectorCodec();
        }

        private void Window_ContentRendered(object sender, EventArgs e) {
            // Put the focus on the data grid.
            //SetPosition(mCurCol, mCurRow);

            // Put the focus on the track/block input field, since their first move will likely
            // be to read something other than the boot block.
            trackBlockNumBox.Focus();
            trackBlockNumBox.SelectAll();
        }

        // Handles PreviewKeyDown event.
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e) {
            // Handle Ctrl+C (but not Ctrl+Shift+C or other variants).
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.C) {
                e.Handled = true;
                CopyToClipboard();
            }
        }

        private void Window_Closing(object sender, CancelEventArgs e) {
            if (!ConfirmDiscardChanges()) {
                e.Cancel = true;
            }
        }

        /// <summary>
        /// If there are unwritten modifications to the buffer, ask the user to confirm.
        /// </summary>
        /// <returns>True if it's okay to continue.</returns>
        private bool ConfirmDiscardChanges() {
            if (!mIsDirty) {
                return true;
            }
            MessageBoxResult res = MessageBox.Show(this, "Discard unwritten modifications?",
                "Confirm", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (res == MessageBoxResult.Cancel) {
                return false;
            }
            return true;
        }

        /// <summary>
        /// Reads a block or sector of data from the current position.
        /// </summary>
        private void ReadFromDisk() {
            if (!ConfirmDiscardChanges()) {
                return;
            }
            try {
                switch (mEditMode) {
                    case SectorEditMode.Sectors:
                        mChunkAccess.ReadSector(mCurBlockOrTrack, mCurSector, mBuffer, 0,
                            mSectorOrder);
                        break;
                    case SectorEditMode.Blocks:
                    case SectorEditMode.CPMBlocks:
                        mChunkAccess.ReadBlock(mCurBlockOrTrack, mBuffer, 0, mSectorOrder);
                        break;
                    default:
                        Debug.Assert(false);
                        return;
                }
                SectorDataGridVisibility = Visibility.Visible;
                IOErrorMsgVisibility = Visibility.Collapsed;
            } catch (BadBlockException) {
                RawData.MemSet(mBuffer, 0, mBuffer.Length, 0xcc);
                SectorDataGridVisibility = Visibility.Hidden;   // not Collapsed
                IOErrorMsgVisibility = Visibility.Visible;
            }
            foreach (SectorRow row in SectorData) {
                row.Refresh();
            }
            IsDirty = false;
            SetSectorDataLabel();
        }

        private void WriteToDisk() {
            if (!TryEnableWrites()) {
                // This shouldn't happen; Write button should have been disabled.
                MessageBox.Show(this, "Unable to write to this disk", "Not Possible",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            try {
                switch (mEditMode) {
                    case SectorEditMode.Sectors:
                        mChunkAccess.WriteSector(mCurBlockOrTrack, mCurSector, mBuffer, 0,
                            mSectorOrder);
                        break;
                    case SectorEditMode.Blocks:
                    case SectorEditMode.CPMBlocks:
                        mChunkAccess.WriteBlock(mCurBlockOrTrack, mBuffer, 0, mSectorOrder);
                        break;
                    default:
                        Debug.Assert(false);
                        return;
                }
                IsDirty = false;
            } catch (Exception ex) {
                MessageBox.Show(this, "Write failed: " + ex.Message, "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            SetSectorDataLabel();   // clear mod flag, update T/S if we wrote elsewhere
        }

        /// <summary>
        /// Sets the label that identifies the source of the buffer contents.
        /// </summary>
        private void SetSectorDataLabel() {
            string dirtyStr = IsDirty ? " [*]" : string.Empty;
            if (mEditMode == SectorEditMode.Sectors) {
                SectorDataLabel = string.Format("Track {0} (${0:X2}), Sector {1} (${1:X}) {2}",
                    mCurBlockOrTrack, mCurSector, dirtyStr);
            } else if (mEditMode == SectorEditMode.CPMBlocks) {
                uint allocBlock = DiskArc.FS.CPM.DiskBlockToAllocBlock(mCurBlockOrTrack,
                        mChunkAccess.FormattedLength, out uint offset);
                if (allocBlock != uint.MaxValue) {
                    SectorDataLabel =
                        string.Format("Block {0} (${0:X}) / Alloc {1}.{2} (${1:X2}) {3}",
                            mCurBlockOrTrack, allocBlock, offset / BLOCK_SIZE, dirtyStr);
                } else {
                    SectorDataLabel = string.Format("Block {0} (${0:X2}) / Alloc --.- ($--) {1}",
                        mCurBlockOrTrack, dirtyStr);
                }
            } else {
                SectorDataLabel = string.Format("Block {0} (${0:X2}) {1}",
                    mCurBlockOrTrack, dirtyStr);
            }
        }

        /// <summary>
        /// Enables or disables the Prev/Next buttons, based on the current location.  Call
        /// this whenever the location changes.
        /// </summary>
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
            string fmtStr = "{0:D}";
            if (mEditMode == SectorEditMode.Sectors) {
                if (sTrackNumBase == 16) {
                    fmtStr = "${0:X2}";
                }
            } else {
                if (sBlockNumBase == 16) {
                    fmtStr = "${0:X4}";
                }
            }
            TrackBlockNumString = string.Format(fmtStr, mCurBlockOrTrack);

            fmtStr = "{0:D}";
            if (sSectorNumBase == 16) {
                fmtStr = "${0:X2}";
            }
            SectorNumString = string.Format(fmtStr, mCurSector);
        }

        private void UpdateTxtConv() {
            // Update the setting.
            AppSettings.Global.SetEnum(AppSettings.SCTED_TEXT_CONV_MODE, mTxtConvMode);

            // Update all rows.
            foreach (SectorRow row in SectorData) {
                row.ConvMode = mTxtConvMode;
            }
            Formatter.FormatConfig config = mFormatter.Config;
            config.HexDumpConvFunc = ModeToConverter(mTxtConvMode);
            mFormatter = new Formatter(config);
        }

        /// <summary>
        /// Performs housekeeping when the track/block or sector text entry field is updated.
        /// </summary>
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

            TrackBlockLabelForeground = trackBlockValid ? mDefaultLabelColor : mErrorLabelColor;
            SectorLabelForeground = sectorValid ? mDefaultLabelColor : mErrorLabelColor;

            // Update the state of the Read/Write buttons.
            mIsEntryValid = trackBlockValid && sectorValid;
            OnPropertyChanged(nameof(IsReadButtonEnabled));
            OnPropertyChanged(nameof(IsWriteButtonEnabled));
        }

        //private void EnableWrites_Click(object sender, RoutedEventArgs e) {
        //    Debug.Assert(mEnableWriteFunc != null);
        //    TryEnableWrites();
        //}

        /// <summary>
        /// Attempts to enable modifications.
        /// </summary>
        private bool TryEnableWrites() {
            if (mEnableWriteFunc == null) {
                return false;
            }
            if (mIsWritingEnabled) {
                return true;
            }
            if (!mEnableWriteFunc()) {
                MessageBox.Show(this, "Failed to enable write access", "Whoops",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            mIsWritingEnabled = true;
            Debug.WriteLine("Writes enabled");
            return true;
        }

        private void ReadButton_Click(object sender, RoutedEventArgs e) {
            mCurBlockOrTrack = (uint)mEnteredBlockOrTrack;
            mCurSector = (uint)mEnteredSector;
            SetPrevNextEnabled();
            ReadFromDisk();
        }

        private void PrevButton_Click(object sender, RoutedEventArgs e) {
            // Assume "prev" button is disabled if this would set an invalid value.
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
            ReadFromDisk();
        }

        private void NextButton_Click(object sender, RoutedEventArgs e) {
            // Assume "next" button is disabled if this would set an invalid value.
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
            ReadFromDisk();
        }

        private void WriteButton_Click(object sender, RoutedEventArgs e) {
            Debug.Assert(!mChunkAccess.IsReadOnly);
            if (mCurBlockOrTrack != mEnteredBlockOrTrack || mCurSector != mEnteredSector) {
                MessageBoxResult res = MessageBox.Show(this,
                    "This will write to a different sector than was read from. Proceed?",
                    "Confirm", MessageBoxButton.OKCancel, MessageBoxImage.Question);
                if (res == MessageBoxResult.Cancel) {
                    return;
                }
            }

            mCurBlockOrTrack = (uint)mEnteredBlockOrTrack;
            mCurSector = (uint)mEnteredSector;
            SetPrevNextEnabled();
            WriteToDisk();
        }

        private void CopyButton_Click(object sender, RoutedEventArgs e) {
            CopyToClipboard();
        }

        /// <summary>
        /// Event that fires when the set of selected cells changes.  This happens whenever
        /// a hex entry is clicked on or the cursor is moved.
        /// </summary>
        private void SectorDataGrid_SelectedCellsChanged(object sender,
                SelectedCellsChangedEventArgs e) {
            IList<DataGridCellInfo> selSet = e.AddedCells;
            if (selSet.Count != 1) {
                Debug.Assert(false, "more than one selected? count=" + selSet.Count);
                return;
            }
            DataGridCellInfo info = selSet[0];
            SectorRow row = (SectorRow)info.Item;
            int col = info.Column.DisplayIndex;     // text area is allowed
            mCurRow = row.RowIndex;
            mCurCol = col;
            mCurDigit = 0;      // reset digits after movement

            int offset = mCurRow * NUM_COLS + mCurCol;
            Debug.WriteLine("Select: posn=$" + offset.ToString("x3"));
        }

        private void SectorDataGrid_PreviewKeyDown(object sender, KeyEventArgs e) {
            bool posnChanged = false;
            bool digitPushed = false;

            // Don't handle Ctrl/Shift stuff here.
            // TODO: handle shift-Tab?
            if (Keyboard.Modifiers != ModifierKeys.None) {
                return;
            }

            switch (e.Key) {
                case Key.Left:
                    posnChanged = MoveLeft();
                    e.Handled = true;
                    break;
                case Key.Right:
                    posnChanged = MoveRight();
                    e.Handled = true;
                    break;
                case Key.Up:
                    posnChanged = MoveUp();
                    e.Handled = true;
                    break;
                case Key.Down:
                    posnChanged = MoveDown();
                    e.Handled = true;
                    break;
                case Key.Enter:     // default: move down one row
                    // TODO: something with this?
                    e.Handled = true;
                    break;
                case Key.Tab:               // default: move right one column
                    trackBlockNumBox.Focus();    // move to track or block number
                    e.Handled = true;
                    break;
                case Key.D0:
                case Key.D1:
                case Key.D2:
                case Key.D3:
                case Key.D4:
                case Key.D5:
                case Key.D6:
                case Key.D7:
                case Key.D8:
                case Key.D9:
                    if (!InTextArea) {
                        if (TryEnableWrites()) {
                            SectorData[mCurRow].PushDigit(mCurCol, (byte)((int)e.Key - Key.D0));
                            digitPushed = true;
                        }
                        e.Handled = true;
                    }
                    break;
                case Key.A:
                case Key.B:
                case Key.C:
                case Key.D:
                case Key.E:
                case Key.F:
                    if (!InTextArea) {
                        if (TryEnableWrites()) {
                            SectorData[mCurRow].PushDigit(mCurCol, (byte)((int)e.Key - Key.A + 10));
                            digitPushed = true;
                        }
                        e.Handled = true;
                    }
                    break;

                default:
                    break;
            }

            if (digitPushed) {
                mCurDigit++;
                if (mCurDigit > 1) {
                    mCurDigit = 0;
                    posnChanged = MoveRight();
                }
                IsDirty = true;
                SetSectorDataLabel();
            }
            if (posnChanged) {
                SetPosition(mCurCol, mCurRow);
            }
        }

        private void SetPosition(int col, int row) {
            sectorDataGrid.SelectRowColAndFocus(row, col);
        }

        private bool MoveLeft() {
            mCurCol--;
            if (mCurCol < 0) {
                mCurCol = NUM_COLS - 1;         // wrap around to last byte, not text area
                mCurRow--;
                if (mCurRow < 0) {
                    mCurRow = mNumRows - 1;
                }
            }
            return true;
        }

        private bool MoveRight() {
            mCurCol++;
            if (mCurCol >= NUM_COLS) {          // don't move into or past text area
                mCurCol = 0;
                mCurRow++;
                if (mCurRow == mNumRows) {
                    mCurRow = 0;
                }
            }
            return true;
        }

        private bool MoveUp() {
            mCurRow--;
            if (mCurRow < 0) {
                mCurRow = mNumRows - 1;
            }
            return true;
        }

        private bool MoveDown() {
            mCurRow++;
            if (mCurRow == mNumRows) {
                mCurRow = 0;
            }
            return true;
        }

        private void CopyToClipboard() {
            if (IOErrorMsgVisibility == Visibility.Visible) {
                // Showing an error, nothing to copy.
                SystemSounds.Exclamation.Play();
                return;
            }

            string dumpText = mFormatter.FormatHexDump(mBuffer).ToString();

            DataObject clipObj = new DataObject();
            clipObj.SetText(dumpText);
            Clipboard.SetDataObject(clipObj, true);
        }

        #region Sector Order

        private bool mInitializingAdv;

        public bool IsSectorOrderEnabled { get; private set; }

        public class SectorOrderItem {
            public string Label { get; }
            public SectorOrder Order { get; }

            public SectorOrderItem(string label, SectorOrder order) {
                Label = label;
                Order = order;
            }
        }

        public List<SectorOrderItem> SectorOrderList { get; } = new List<SectorOrderItem>() {
            new SectorOrderItem("DOS 3.3", SectorOrder.DOS_Sector),
            new SectorOrderItem("ProDOS", SectorOrder.ProDOS_Block),
            new SectorOrderItem("CP/M", SectorOrder.CPM_KBlock),
            new SectorOrderItem("Physical", SectorOrder.Physical)
        };

        /// <summary>
        /// Prepares the sector-order selection combo box.  Does not alter the sector order.
        /// </summary>
        private void PrepareSectorOrder() {
            // Disable the control if this isn't a 16-sector disk.
            if (!mChunkAccess.HasSectors || mChunkAccess.NumSectorsPerTrack != 16) {
                IsSectorOrderEnabled = false;
                sectorOrderCombo.SelectedIndex = -1;
                return;
            }
            IsSectorOrderEnabled = true;

            // Set selection to default value, making sure we select something.
            int match;
            for (match = 0; match < SectorOrderList.Count; match++) {
                if (SectorOrderList[match].Order == mSectorOrder) {
                    break;
                }
            }
            if (match == SectorOrderList.Count) {
                match = 0;
            }
            mInitializingAdv = true;
            sectorOrderCombo.SelectedIndex = match;
            mInitializingAdv = false;
        }

        private void SectorOrderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) {
            if (mInitializingAdv) {
                return;
            }
            SectorOrderItem? item = ((ComboBox)sender).SelectedItem as SectorOrderItem;
            if (item != null) {
                mSectorOrder = item.Order;
                ReadFromDisk();
            }
        }

        #endregion Sector Order

        #region Sector Codec

        public string SectorCodecName { get; private set; } = string.Empty;

        /// <summary>
        /// Prepares the sector codec display.
        /// </summary>
        private void PrepareSectorCodec() {
            if (mChunkAccess.NibbleCodec == null) {
                SectorCodecName = "N/A";
            } else {
                SectorCodecName = mChunkAccess.NibbleCodec.Name;
            }
        }

#if false
        // For this to work we need a way to change the SectorCodec on the fly in
        // NibbleChunkAccess.  We need to reset some state, e.g. the TrackEntry objects
        // hold the result of NibbleCodec.FindSectors().  Shouldn't be too difficult but
        // it's a lower-priority feature.

        public bool IsSectorCodecEnabled { get; private set; }

        public class SectorCodecItem {
            public string Label { get; }
            public SectorCodec Codec { get; }
            public SectorCodecItem(string label, SectorCodec codec) {
                Label = label;
                Codec = codec;
            }
        }

        public List<SectorCodecItem> SectorCodecList { get; } = new List<SectorCodecItem>();

        private void PrepareSectorCodec() {
            if (mChunkAccess.NibbleCodec == null) {
                IsSectorCodecEnabled = false;
                return;
            }
            IsSectorCodecEnabled = true;

            SectorCodec curCodec = mChunkAccess.NibbleCodec;
            bool is525 = curCodec.DecodedSectorSize == 256;
            if (is525) {
                foreach (StdSectorCodec.CodecIndex525 index in
                        Enum.GetValues(typeof(StdSectorCodec.CodecIndex525))) {
                    SectorCodec codec = StdSectorCodec.GetCodec(index);
                    SectorCodecList.Add(new SectorCodecItem(codec.Name, codec));
                }
            } else {
                foreach (StdSectorCodec.CodecIndex35 index in
                        Enum.GetValues(typeof(StdSectorCodec.CodecIndex35))) {
                    SectorCodec codec = StdSectorCodec.GetCodec(index);
                    SectorCodecList.Add(new SectorCodecItem(codec.Name, codec));
                }
            }

            // Look for a match.  We have to search by label.
            int match;
            for (match = 0; match < SectorOrderList.Count; match++) {
                if (SectorOrderList[match].Order == mSectorOrder) {
                    break;
                }
            }
            if (match == SectorOrderList.Count) {
                match = 0;
            }
            mInitializingAdv = true;
            sectorOrderCombo.SelectedIndex = match;
            mInitializingAdv = false;
        }

        private void SectorCodecCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) {
            if (mInitializingAdv) {
                return;
            }
            // TODO
        }
#endif

        #endregion Sector Codec
    }
}
