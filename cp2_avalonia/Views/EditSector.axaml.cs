/*
 * Copyright 2023 faddenSoft
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
using System.Windows.Input;


using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using cp2_avalonia.ViewModels;

namespace cp2_avalonia.Views {
    /// <summary>
    /// Sector edit dialog — thin code-behind.  All business logic lives in
    /// EditSectorViewModel.
    /// </summary>
    public partial class EditSector : Window {
        private const int NUM_COLS = 16;

        // Async close guard — stays in code-behind.
        private bool mUserConfirmedClose;

        // DataGrid cursor tracking — purely UI state.
        private int mCurRow;
        private int mCurCol;
        private int mCurDigit;

        private EditSectorViewModel? VM => DataContext as EditSectorViewModel;

        public EditSector() {
            InitializeComponent();
            if (Design.IsDesignMode) return;

            DataContextChanged += (_, __) => {
                if (DataContext is EditSectorViewModel vm) {
                    vm.CloseRequested += result => Close(result);
                }
            };
            Loaded += (_, __) => {
                OnFontsChanged();
                App.FontsChanged += OnFontsChanged;
            };
            Closing += (_, __) => {
                App.FontsChanged -= OnFontsChanged;
            };
        }

        private void OnFontsChanged() {
            if (Application.Current?.TryFindResource("GeneralMonoFont", out var f) == true && f is FontFamily ff) {
                Resources["GeneralMonoFont"] = ff;
                sectorDataGrid.FontFamily = ff;
            }
            if (Application.Current?.TryFindResource("ViewerDefaultFontSize", out var s) == true && s is double sz) {
                Resources["ViewerDefaultFontSize"] = sz;
                sectorDataGrid.FontSize = sz;
            }
            // Avalonia DataGrid tracks a per-column high-water-mark for Auto
            // widths that never decreases. Force each column to 1 px so the
            // tracker resets, then restore Auto after layout has processed the
            // collapsed state so columns re-measure from scratch.
            foreach (var col in sectorDataGrid.Columns)
                col.Width = new DataGridLength(1, DataGridLengthUnitType.Pixel);
            Dispatcher.UIThread.Post(() => {
                foreach (var col in sectorDataGrid.Columns)
                    col.Width = DataGridLength.Auto;
                // Re-trigger SizeToContent so the window can shrink too.
                SizeToContent = SizeToContent.Manual;
                SizeToContent = SizeToContent.Width;
            }, DispatcherPriority.Background);
        }

        private void Window_Opened(object? sender, EventArgs e) {
            if (Design.IsDesignMode) return;
            trackBlockNumBox.Focus();
            trackBlockNumBox.SelectAll();
        }

        // Intercept Ctrl+C to copy buffer to clipboard.
        private void Window_KeyDown(object? sender, KeyEventArgs e) {
            if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.C) {
                e.Handled = true;
                ((ICommand)VM!.CopyToClipboardCommand).Execute(null);
            }
        }

        private async void Window_Closing(object? sender, WindowClosingEventArgs e) {
            if (Design.IsDesignMode) return;
            if (mUserConfirmedClose) return;
            if (VM!.IsDirty) {
                e.Cancel = true;
                bool discard = await VM!.ConfirmDiscardChangesAsync();
                if (discard) {
                    mUserConfirmedClose = true;
                    Close();
                }
            }
        }

        // -----------------------------------------------------------------------------------------
        // DataGrid interaction — purely visual/UI

        private void SectorDataGrid_CurrentCellChanged(object? sender, EventArgs e) {
            var col = sectorDataGrid.CurrentColumn;
            if (col == null) return;
            int displayIndex = col.DisplayIndex;
            if (displayIndex == 0) return; // RowLabel — ignore

            if (sectorDataGrid.SelectedItem is not EditSectorViewModel.SectorRow row) return;
            mCurRow = row.RowIndex;
            mCurCol = displayIndex - 1; // 0..15 = hex cols, 16 = text
            mCurDigit = 0;
        }

        private void SectorDataGrid_KeyDown(object? sender, KeyEventArgs e) {
            if (VM == null) return;
            bool posnChanged = false;
            bool digitPushed = false;

            if (e.KeyModifiers != KeyModifiers.None) return;

            switch (e.Key) {
                case Key.Left:  posnChanged = MoveLeft();  e.Handled = true; break;
                case Key.Right: posnChanged = MoveRight(); e.Handled = true; break;
                case Key.Up:    posnChanged = MoveUp();    e.Handled = true; break;
                case Key.Down:  posnChanged = MoveDown();  e.Handled = true; break;
                case Key.Enter: e.Handled = true; break;
                case Key.Tab:
                    trackBlockNumBox.Focus();
                    e.Handled = true;
                    break;
                case Key.D0: case Key.D1: case Key.D2: case Key.D3:
                case Key.D4: case Key.D5: case Key.D6: case Key.D7:
                case Key.D8: case Key.D9:
                    if (!InTextArea) {
                        if (VM!.IsEditable) {
                            VM!.SectorData[mCurRow].PushDigit(mCurCol,
                                (byte)((int)e.Key - (int)Key.D0));
                            digitPushed = true;
                        }
                        e.Handled = true;
                    }
                    break;
                case Key.A: case Key.B: case Key.C: case Key.D:
                case Key.E: case Key.F:
                    if (!InTextArea) {
                        if (VM!.IsEditable) {
                            VM!.SectorData[mCurRow].PushDigit(mCurCol,
                                (byte)((int)e.Key - (int)Key.A + 10));
                            digitPushed = true;
                        }
                        e.Handled = true;
                    }
                    break;
            }

            if (digitPushed) {
                mCurDigit++;
                if (mCurDigit > 1) {
                    mCurDigit = 0;
                    posnChanged = MoveRight();
                }
                VM!.MarkDirtyAndUpdateLabel();
            }
            if (posnChanged) {
                SetPosition(mCurCol, mCurRow);
            }
        }

        private bool InTextArea => mCurCol == NUM_COLS;

        private void SetPosition(int col, int row) {
            int gridCol = col + 1;
            if (VM == null) return;
            if (row < 0 || row >= VM!.SectorData.Count) return;
            object item = VM!.SectorData[row];
            DataGridColumn? dgCol = sectorDataGrid.Columns.Count > gridCol
                ? sectorDataGrid.Columns[gridCol] : null;
            if (dgCol == null) return;
            sectorDataGrid.SelectedItem = item;
            sectorDataGrid.CurrentColumn = dgCol;
            sectorDataGrid.ScrollIntoView(item, dgCol);
            sectorDataGrid.Focus();
        }

        private bool MoveLeft() {
            mCurCol--;
            if (mCurCol < 0) {
                mCurCol = NUM_COLS - 1;
                mCurRow--;
                if (mCurRow < 0) mCurRow = VM!.NumRows - 1;
            }
            return true;
        }

        private bool MoveRight() {
            mCurCol++;
            if (mCurCol >= NUM_COLS) {
                mCurCol = 0;
                mCurRow++;
                if (mCurRow == VM!.NumRows) mCurRow = 0;
            }
            return true;
        }

        private bool MoveUp() {
            mCurRow--;
            if (mCurRow < 0) mCurRow = VM!.NumRows - 1;
            return true;
        }

        private bool MoveDown() {
            mCurRow++;
            if (mCurRow == VM!.NumRows) mCurRow = 0;
            return true;
        }
    }
}
