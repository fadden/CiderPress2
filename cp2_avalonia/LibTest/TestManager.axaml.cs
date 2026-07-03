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

using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Threading;

using cp2_avalonia.ViewModels;

namespace cp2_avalonia.LibTest {
    public partial class TestManager : Window {
        private TestManagerViewModel? VM => DataContext as TestManagerViewModel;

        public TestManager() {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
        }

        private void OnDataContextChanged(object? sender, EventArgs e) {
            if (DataContext is TestManagerViewModel vm) {
                // Wire close event.
                vm.CloseRequested += _ => Close();

                // Wire colored progress output.
                vm.ProgressAppended += pair => AppendColoredText(pair.text, pair.color);

                // Wire reset request (clear text + color runs).
                vm.ResetRequested += OnResetRequested;
            }
        }

        private void OnResetRequested() {
            progressTextBlock.Inlines!.Clear();
            progressTextBlock.Text = null;
        }

        private void Window_Closing(object? sender, WindowClosingEventArgs e) {
            VM?.CancelIfBusy();
        }

        private void OutputSelectComboBox_SelectedIndexChanged(object? sender,
                SelectionChangedEventArgs e) {
            VM?.OnOutputSelectChanged(outputSelectComboBox.SelectedIndex);
        }

        private void AppendColoredText(string text, Color color) {
            // Split on newlines to emit Run + LineBreak inlines.
            string[] parts = text.Split('\n');
            for (int i = 0; i < parts.Length; i++) {
                if (parts[i].Length > 0) {
                    progressTextBlock.Inlines!.Add(new Run(parts[i]) {
                        Foreground = new SolidColorBrush(color),
                    });
                }
                if (i < parts.Length - 1) {
                    progressTextBlock.Inlines!.Add(new LineBreak());
                }
            }
            // Scroll to end after layout.
            Dispatcher.UIThread.Post(() => {
                progressScroll.Offset = new Avalonia.Vector(
                    progressScroll.Offset.X, double.MaxValue);
            }, DispatcherPriority.Background);
        }
    }
}
