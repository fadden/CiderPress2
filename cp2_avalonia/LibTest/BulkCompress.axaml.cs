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
using Avalonia.Controls;
using Avalonia.Interactivity;

using static DiskArc.Defs;

using cp2_avalonia.ViewModels;

namespace cp2_avalonia.LibTest {
    public partial class BulkCompress : Window {
        private BulkCompressViewModel? VM => DataContext as BulkCompressViewModel;

        public BulkCompress() {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
        }

        private void OnDataContextChanged(object? sender, EventArgs e) {
            if (DataContext is BulkCompressViewModel vm) {
                vm.CloseRequested += _ => Close();
                // Keep logTextBox in sync with ViewModel.LogText, auto-scrolling.
                vm.PropertyChanged += (s, args) => {
                    if (args.PropertyName == nameof(BulkCompressViewModel.LogText)) {
                        string text = vm.LogText;
                        logTextBox.Text = text;
                        logTextBox.CaretIndex = text.Length;
                    }
                };
            }
        }

        private void Window_Closing(object? sender, WindowClosingEventArgs e) {
            VM?.CancelIfBusy();
        }

        // Forward radio button selection to ViewModel.
        private void CompGroup_CheckedChanged(object? sender, RoutedEventArgs e) {
            if (VM == null) return;
            CompressionFormat fmt = CompressionFormat.NuLZW2;
            if (radioCompressSqueeze.IsChecked == true)      fmt = CompressionFormat.Squeeze;
            else if (radioCompressNuLZW1.IsChecked == true)  fmt = CompressionFormat.NuLZW1;
            else if (radioCompressNuLZW2.IsChecked == true)  fmt = CompressionFormat.NuLZW2;
            else if (radioCompressDeflate.IsChecked == true) fmt = CompressionFormat.Deflate;
            else if (radioCompressLZC12.IsChecked == true)   fmt = CompressionFormat.LZC12;
            else if (radioCompressLZC16.IsChecked == true)   fmt = CompressionFormat.LZC16;
            else if (radioCompressLZHuf.IsChecked == true)   fmt = CompressionFormat.LZHuf;
            else if (radioCompressZX0.IsChecked == true)     fmt = CompressionFormat.ZX0;
            VM.SelectedCompressionFormat = fmt;
        }
    }
}
