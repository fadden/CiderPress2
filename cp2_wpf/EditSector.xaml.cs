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

        public class SectorRow : INotifyPropertyChanged {
            public event PropertyChangedEventHandler? PropertyChanged;
            private void OnPropertyChanged([CallerMemberName] string propertyName = "") {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }

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

            private char[] mTextHolder = new char[16];
            public string AsText {
                get {
                    for (int i = 0; i < NUM_COLS; i++) {
                        mTextHolder[i] = Formatter.CharConv_HighASCII(mBuffer[mRowOffset + i]);
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

            private byte[] mBuffer;
            private int mRowOffset;
            public int RowIndex { get; private set; }

            public SectorRow(byte[] buf, int offset) {
                mBuffer = buf;
                mRowOffset = offset;

                RowIndex = mRowOffset / NUM_COLS;
                if (mBuffer.Length == SECTOR_SIZE) {
                    RowLabel = (mRowOffset & 0xf0).ToString("X2");
                } else {
                    RowLabel = (mRowOffset & 0x1f0).ToString("X3");
                }
            }
        }

        public List<SectorRow> SectorData { get; set;  } = new List<SectorRow>();

        public delegate bool EnableWriteFunc();
        private EnableWriteFunc? mEnableWriteFunc;

        /// <summary>
        /// True if the "enable writes" button is enabled.
        /// </summary>
        public bool IsEnableWritesEnabled {
            get { return mIsEnableWritesEnabled; }
            set { mIsEnableWritesEnabled = value; OnPropertyChanged(); }
        }
        private bool mIsEnableWritesEnabled;


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

        /// <summary>
        /// True if the "Write" button should be enabled in the GUI.
        /// </summary>
        public bool IsWriteButtonEnabled { get { return mIsWritingEnabled && mIsDirty; } }

        private IChunkAccess mChunkAccess;
        private bool mAsSectors;

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

            if (enableWriteFunc != null) {
                IsEnableWritesEnabled = true;
            }

            if (mAsSectors && !chunks.HasSectors) {
                mAsSectors = false;
            }
            mBuffer = new byte[mAsSectors ? SECTOR_SIZE : BLOCK_SIZE];
            if (mAsSectors) {
                chunks.ReadSector(0, 0, mBuffer, 0);
            } else {
                chunks.ReadBlock(0, mBuffer, 0);
            }
            for (int i = 0; i < mBuffer.Length; i += NUM_COLS) {
                SectorRow row = new SectorRow(mBuffer, i);
                SectorData.Add(row);
            }

            mCurRow = mCurCol = 0;
            mCurDigit = 0;
            mNumRows = mBuffer.Length / NUM_COLS;
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

        private void EnableWrites_Click(object sender, RoutedEventArgs e) {
            Debug.Assert(mEnableWriteFunc != null);
            TryEnableWrites();
        }

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
            Debug.WriteLine("Read!");
            sectorDataGrid.SelectRowColAndFocus(2, 2);
        }

        private void WriteButton_Click(object sender, RoutedEventArgs e) {
            Debug.WriteLine("Write!");
            Debug.Assert(!mChunkAccess.IsReadOnly);
            IsDirty = false;
        }

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
                    // TODO: something with these?
                    e.Handled = true;
                    break;
                case Key.Tab:       // default: move right one column
                    trackNumBox.Focus();
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
