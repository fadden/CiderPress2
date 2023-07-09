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

        public enum TxtConvMode { HighASCII, MOR, Latin }
        private delegate char TextConverter(byte b);
        private static TextConverter ModeToConverter(TxtConvMode mode) {
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
            private TextConverter mConverter;

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

        public delegate bool EnableWriteFunc();
        private EnableWriteFunc? mEnableWriteFunc;

        /// <summary>
        /// True if the "enable writes" button is enabled.
        /// </summary>
        //public bool IsEnableWritesEnabled {
        //    get { return mIsEnableWritesEnabled; }
        //    set { mIsEnableWritesEnabled = value; OnPropertyChanged(); }
        //}
        //private bool mIsEnableWritesEnabled;

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
        /// True if writes are enabled.
        /// </summary>
        private bool mIsWritingEnabled;

        /// <summary>
        /// True if buffer has been modified.
        /// </summary>
        public bool IsDirty {
            get { return mIsDirty; }
            set { mIsDirty = value; OnPropertyChanged(nameof(IsWriteButtonEnabled)); }
        }
        private bool mIsDirty;

        private bool mIsEntryValid;

        /// <summary>
        /// True if the "Write" button should be enabled in the GUI.  Depends on whether writing
        /// is possible for this volume, whether the buffer has been modified, and the contents
        /// of the entry fields.
        /// </summary>
        public bool IsWriteButtonEnabled {
            get { return mIsWritingEnabled && mIsDirty && mIsEntryValid; }
        }

        /// <summary>
        /// True if the "Read" button should be enabled in the GUI.  Depends on the contents
        /// of the entry fields.
        /// </summary>
        public bool IsReadButtonEnabled {
            get { return mIsEntryValid; }
        }

        private IChunkAccess mChunkAccess;
        private bool mAsSectors;

        private uint mCurBlockOrTrack;
        private uint mCurSector;
        private int mEnteredBlockOrTrack;
        private int mEnteredSector;

        private byte[] mBuffer;
        private int mCurCol;
        private int mCurRow;
        private int mCurDigit;
        private readonly int mNumRows;

        private bool InTextArea { get { return mCurCol == NUM_COLS; } }


        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="owner">Parent window.</param>
        /// <param name="chunks">Chunk data source.</param>
        /// <param name="asSectors">If true, try to operate on sectors rather than blocks.  This
        ///   will be ignored if the chunk source can't operate on sectors.</param>
        /// <param name="enableWriteFunc">Function to call to enable writes.  Will be null if
        ///   we don't allow writes.</param>
        /// <param name="formatter">Text formatter.</param>
        public EditSector(Window owner, IChunkAccess chunks, bool asSectors,
                EnableWriteFunc? enableWriteFunc, Formatter unused) {
            InitializeComponent();
            Owner = owner;
            DataContext = this;

            mChunkAccess = chunks;
            mAsSectors = asSectors;
            mEnableWriteFunc = enableWriteFunc;

            //if (enableWriteFunc != null) {
            //    IsEnableWritesEnabled = true;
            //}

            SettingsHolder settings = AppSettings.Global;
            mTxtConvMode =
                settings.GetEnum(AppSettings.SCTED_TEXT_CONV_MODE, TxtConvMode.HighASCII);

            if (mAsSectors && !chunks.HasSectors) {
                mAsSectors = false;
            }
            mBuffer = new byte[mAsSectors ? SECTOR_SIZE : BLOCK_SIZE];
            mNumRows = mBuffer.Length / NUM_COLS;

            for (int i = 0; i < mBuffer.Length; i += NUM_COLS) {
                SectorRow row = new SectorRow(mBuffer, i, mTxtConvMode);
                SectorData.Add(row);
            }
            mCurBlockOrTrack = 0;
            mCurSector = 0;
            ReadData();

            if (mAsSectors) {
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
        }

        private void Window_ContentRendered(object sender, EventArgs e) {
            // Put the focus on the data grid.
            SetPosition(mCurCol, mCurRow);
        }

        private void Window_Closing(object sender, CancelEventArgs e) {
            if (mIsDirty) {
                MessageBoxResult res = MessageBox.Show(this, "Discard changes?", "Confirm",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question);
                if (res == MessageBoxResult.Cancel) {
                    e.Cancel = true;
                    return;
                }
            }
        }

        /// <summary>
        /// Reads a block or sector of data from the current position.
        /// </summary>
        private void ReadData() {
            // TODO: confirm overwrite of modified data
            try {
                if (mAsSectors) {
                    mChunkAccess.ReadSector(mCurBlockOrTrack, mCurSector, mBuffer, 0);
                } else {
                    mChunkAccess.ReadBlock(mCurBlockOrTrack, mBuffer, 0);
                }
            } catch (BadBlockException) {
                // TODO
                Debug.Assert(false, "need to handle this");
                RawData.MemSet(mBuffer, 0, mBuffer.Length, 0xcc);
            }
            foreach (SectorRow row in SectorData) {
                row.Refresh();
            }
            SetSectorDataLabel();
        }

        /// <summary>
        /// Sets the label that identifies the source of the buffer contents.
        /// </summary>
        private void SetSectorDataLabel() {
            if (mAsSectors) {
                SectorDataLabel = string.Format("Track {0} (${0:X}), Sector {1} (${1:X})",
                    mCurBlockOrTrack, mCurSector);
            } else {
                SectorDataLabel = string.Format("Block {0} (${0:X})", mCurBlockOrTrack);
            }
        }

        private void CopyNumToTextFields() {
            mTrackBlockNumString = mCurBlockOrTrack.ToString();
            mSectorNumString = mCurSector.ToString();
            TrackSectorUpdated();
        }

        private void UpdateTxtConv() {
            // Update the setting.
            AppSettings.Global.SetEnum(AppSettings.SCTED_TEXT_CONV_MODE, mTxtConvMode);

            // Update all rows.
            foreach (SectorRow row in SectorData) {
                row.ConvMode = mTxtConvMode;
            }
        }

        /// <summary>
        /// Performs housekeeping when the track/block or sector text entry field is updated.
        /// </summary>
        private void TrackSectorUpdated() {
            int val, intBase;

            if (StringToValue.TryParseInt(TrackBlockNumString, out val, out intBase)) {
                mEnteredBlockOrTrack = val;
            } else {
                mEnteredBlockOrTrack = -1;
            }
            if (StringToValue.TryParseInt(SectorNumString, out val, out intBase)) {
                mEnteredSector = val;
            } else {
                mEnteredSector = -1;
            }

            bool trackBlockValid, sectorValid;

            if (mAsSectors) {
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
        private void TryEnableWrites() {
            if (mEnableWriteFunc == null || mIsWritingEnabled) {
                return;
            }
            if (!mEnableWriteFunc()) {
                MessageBox.Show(this, "Failed to enable write access", "Whoops",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            mIsWritingEnabled = true;
            Debug.WriteLine("Writes enabled");
        }

        private void ReadButton_Click(object sender, RoutedEventArgs e) {
            mCurBlockOrTrack = (uint)mEnteredBlockOrTrack;
            mCurSector = (uint)mEnteredSector;
            ReadData();
        }

        private void WriteButton_Click(object sender, RoutedEventArgs e) {
            Debug.WriteLine("Write!");
            Debug.Assert(!mChunkAccess.IsReadOnly);
            IsDirty = false;
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
                        TryEnableWrites();
                        if (mIsWritingEnabled) {
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
                        TryEnableWrites();
                        if (mIsWritingEnabled) {
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
    }
}
