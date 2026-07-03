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
using System.IO;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using cp2_avalonia.ViewModels;

namespace cp2_avalonia.Tools {
    /// <summary>
    /// Debug log viewer.  Always modeless.
    /// </summary>
    public partial class LogViewer : Window {
        private LogViewerViewModel? VM => DataContext as LogViewerViewModel;

        public LogViewer() {
            InitializeComponent();
            DataContextChanged += (_, __) => {
                if (DataContext is LogViewerViewModel vm) {
                    vm.CloseRequested += () => Close();
                    vm.SaveRequested += async () => await DoSave();
                    vm.CopyRequested += async () => await DoCopy();
                    vm.ScrollToEndRequested += ScrollToEnd;
                    // Start at the bottom so the latest entries are visible on open.
                    ScrollToEnd(force: true);
                }
            };
        }

        private void Window_Closed(object? sender, EventArgs e) {
            VM?.OnWindowClosed();
        }

        // Treat the view as "at the bottom" if it's within this many pixels of the end,
        // to allow for fractional offsets.
        private const double BottomTolerance = 2.0;

        /// <summary>
        /// Scrolls the log list to the bottom so the most recent entries stay visible.
        /// </summary>
        /// <param name="force">
        /// If true, always scroll (e.g. the filter changed).  If false, only scroll when
        /// the view is already at the bottom, so scrolling up to read history is not
        /// disturbed by new entries.
        /// </param>
        private void ScrollToEnd(bool force) {
            // Decide before posting: the at-bottom check must use the pre-layout extent,
            // i.e. the scroll state from before any just-added item grows the content.
            if (!force && !IsScrolledToBottom()) {
                return;
            }
            // Posted at Background priority so it runs after the list has laid out any
            // newly added items (or after a filter change rebuilt the list).
            Dispatcher.UIThread.Post(() => {
                int count = logListBox.ItemCount;
                if (count > 0) {
                    logListBox.ScrollIntoView(count - 1);
                }
            }, DispatcherPriority.Background);
        }

        /// <summary>
        /// Returns true if the log list is scrolled to (or very near) the bottom.  Returns
        /// true when the content fits without scrolling or the scroll viewer isn't realized
        /// yet, so the view follows new entries by default.
        /// </summary>
        private bool IsScrolledToBottom() {
            ScrollViewer? sv = logListBox.FindDescendantOfType<ScrollViewer>();
            if (sv == null) {
                return true;
            }
            return sv.Offset.Y >= sv.Extent.Height - sv.Viewport.Height - BottomTolerance;
        }

        private async System.Threading.Tasks.Task DoSave() {
            var topLevel = GetTopLevel(this);
            var file = await topLevel!.StorageProvider.SaveFilePickerAsync(
                new FilePickerSaveOptions {
                    Title = "Save Debug Log...",
                    SuggestedFileName = "cp2-log.txt",
                    FileTypeChoices =
                    [
                        new FilePickerFileType("Text Files") { Patterns = ["*.txt"] },
                        new FilePickerFileType("All Files") { Patterns = ["*"] }
                    ]
                });
            if (file == null) { return; }
            await using var stream = await file.OpenWriteAsync();
            using var writer = new StreamWriter(stream);
            writer.Write(VM!.BuildLogText());
        }

        private async System.Threading.Tasks.Task DoCopy() {
            string text = VM!.BuildLogText();
            var clipboard = GetTopLevel(this)?.Clipboard;
            if (clipboard != null) {
                await clipboard.SetTextAsync(text);
            }
        }
    }
}
