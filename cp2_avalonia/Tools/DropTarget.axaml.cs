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
using System.Collections.Generic;
using System.Text;

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using DiskArc;
using cp2_avalonia.Models;
using cp2_avalonia.Platform;
using cp2_avalonia.ViewModels;

namespace cp2_avalonia.Tools {
    /// <summary>
    /// Drop / paste target window, for testing clipboard and drag-drop.
    /// This is a modeless dialog: no Owner, ShowInTaskbar=True, closes via Close().
    /// </summary>
    public partial class DropTarget : Window {
        private DropTargetViewModel? VM => DataContext as DropTargetViewModel;

        // Linux XDND receive handler (null on non-Linux or until Loaded fires).
        private LinuxDrag? mLinuxDrag;

        public DropTarget() {
            InitializeComponent();
            DataContextChanged += (_, __) => {
                if (DataContext is DropTargetViewModel vm) {
                    vm.CloseRequested += () => Close();
                    vm.PasteRequested += async () => await DoPasteAsync();
                }
            };

            // Register TextBox drop handler after InitializeComponent.
            var textBox = this.FindControl<TextBox>("textArea");
            if (textBox != null) {
                textBox.AddHandler(DragDrop.DropEvent, TextArea_Drop);
                textBox.AddHandler(DragDrop.DragOverEvent, TextArea_DragOver);
                // Use tunneling (Preview) so we intercept paste before the TextBox
                // handles Command+V / Ctrl+V internally and marks it as handled.
                textBox.AddHandler(KeyDownEvent, TextArea_PreviewKeyDown,
                    Avalonia.Interactivity.RoutingStrategies.Tunnel);
            }

            if (OperatingSystem.IsLinux()) {
                Loaded += OnLinuxLoaded;
#pragma warning disable CA1416  // guarded by OperatingSystem.IsLinux() above
                Closed += (_, _) => { mLinuxDrag?.Dispose(); mLinuxDrag = null; };
#pragma warning restore CA1416
            }
        }

        // ---- Linux XDND receive ----

        [System.Runtime.Versioning.SupportedOSPlatform("linux")]
        private void OnLinuxLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e) {
            nint xid = this.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (xid == IntPtr.Zero) return;
            mLinuxDrag = LinuxDrag.Install(xid,
                onImportDrop:    (paths, rx, ry) => ShowLinuxFileDrop(paths),
                onImportCp2Drop: (json,  rx, ry) => ShowLinuxCp2Drop(json));
        }

        [System.Runtime.Versioning.SupportedOSPlatform("linux")]
        private void ShowLinuxFileDrop(string[] paths) {
            var sb = new StringBuilder();
            sb.AppendLine("=== Linux XDND file drop ===");
            foreach (string p in paths)
                sb.AppendLine("  " + p);
            VM!.TextArea = sb.ToString();
        }

        [System.Runtime.Versioning.SupportedOSPlatform("linux")]
        private void ShowLinuxCp2Drop(string json) {
            var sb = new StringBuilder();
            sb.AppendLine("=== Linux XDND CP2 drop ===");
            DumpClipInfoText(json, sb);
            VM!.TextArea = sb.ToString();
        }

        /// <summary>
        /// Called from MainWindow.HandleLinuxInternalDrop when the drag lands here.
        /// Displays the dragged archive entries for diagnostic purposes.
        /// </summary>
        public void ShowLinuxInternalDrop(IReadOnlyList<IFileEntry> entries) {
            var sb = new StringBuilder();
            sb.AppendLine("=== Linux internal drag (file list) ===");
            sb.AppendLine("Entries: " + entries.Count);
            foreach (IFileEntry entry in entries)
                sb.AppendLine("  '" + entry.FullPathName + "'");
            VM!.TextArea = sb.ToString();
        }

        private void Window_Closed(object? sender, EventArgs e) {
            VM?.OnWindowClosed();
        }

        private void TextArea_PreviewKeyDown(object? sender, KeyEventArgs e) {
            bool isPaste = e.Key == Key.V && (OperatingSystem.IsMacOS()
                ? (e.KeyModifiers & KeyModifiers.Meta) != 0
                : (e.KeyModifiers & KeyModifiers.Control) != 0);
            if (isPaste) {
                e.Handled = true;
                _ = DoPasteAsync();
            }
        }

        private void Window_KeyDown(object? sender, KeyEventArgs e) {
            bool isPaste = e.Key == Key.V && (OperatingSystem.IsMacOS()
                ? (e.KeyModifiers & KeyModifiers.Meta) != 0
                : (e.KeyModifiers & KeyModifiers.Control) != 0);
            if (isPaste) {
                e.Handled = true;
                _ = DoPasteAsync();
            }
        }

        private async System.Threading.Tasks.Task DoPasteAsync() {
            var clipboard = GetTopLevel(this)?.Clipboard;
            if (clipboard == null) {
                VM!.TextArea = "(No clipboard available)\n";
                return;
            }
            var sb = new StringBuilder();
            sb.AppendLine("=== Clipboard paste ===");

            // On X11 Avalonia maps "text/uri-list" to DataFormat.File, so
            // IClipboard.GetDataAsync("text/uri-list") silently returns null.
            // DataFormats.Files is the correct key on all platforms.
            object? filesObj = await clipboard.GetDataAsync(DataFormats.Files);
            if (filesObj is IEnumerable<IStorageItem> storageItems) {
                sb.AppendLine("=== Files ===");
                foreach (IStorageItem item in storageItems)
                    sb.AppendLine("  " + (item.TryGetLocalPath() ?? item.Name));
                VM!.TextArea = sb.ToString();
                return;
            }

            string? text = await clipboard.GetTextAsync();
            if (text == null) {
                sb.AppendLine("(no text or files on clipboard)");
            } else {
                sb.AppendLine("Text length: " + text.Length + " chars");
                if (ClipInfo.IsClipTextFromCP2(text)) {
                    sb.AppendLine("Recognized as CiderPress II clipboard data:");
                    DumpClipInfoText(text, sb);
                } else {
                    string preview = text.Length > 200 ? text.Substring(0, 200) + "..." : text;
                    sb.AppendLine(preview);
                }
            }
            VM!.TextArea = sb.ToString();
        }

        private void TextArea_DragOver(object? sender, DragEventArgs e) {
            e.DragEffects = DragDropEffects.Copy;
            e.Handled = true;
        }

        private void TextArea_Drop(object? sender, DragEventArgs e) {
            ShowDataObject(e.Data);
        }

        private void ShowDataObject(IDataObject dataObj) {
            StringBuilder sb = new StringBuilder();

            IEnumerable<string> formats = dataObj.GetDataFormats();
            var formatList = new List<string>(formats);

            sb.AppendLine("Found " + formatList.Count + " format(s):");
            foreach (string format in formatList) {
                sb.Append("\u2022 ");
                sb.AppendLine(format);
            }

            if (dataObj.Contains(DataFormats.Text)) {
                string? text = dataObj.Get(DataFormats.Text) as string;
                if (ClipInfo.IsClipTextFromCP2(text)) {
                    sb.AppendLine();
                    sb.AppendLine("=== CiderPress II clipboard data ===");
                    DumpClipInfoText(text, sb);
                } else if (text != null) {
                    sb.AppendLine();
                    sb.AppendLine("Text content preview:");
                    string preview = text.Length > 200 ? text.Substring(0, 200) + "..." : text;
                    sb.AppendLine(preview);
                }
            }

            if (dataObj.Contains(DataFormats.Files)) {
                var files = dataObj.GetFiles();
                if (files != null) {
                    sb.AppendLine();
                    sb.AppendLine("=== Files ===");
                    foreach (var item in files) {
                        string? path = item.TryGetLocalPath();
                        sb.AppendLine("  " + (path ?? item.Name));
                    }
                }
            }

            VM!.TextArea = sb.ToString();
        }

        private static void DumpClipInfoText(string? clipText, StringBuilder sb) {
            ClipInfo? clipInfo = ClipInfo.FromClipString(clipText);
            if (clipInfo == null) {
                sb.AppendLine("  ERROR: failed to deserialize ClipInfo");
                return;
            }
            sb.AppendLine("  ProcessId=" + clipInfo.ProcessId + " IsExport=" + clipInfo.IsExport);
            sb.AppendLine("  Version=" + clipInfo.AppVersionMajor + "." +
                clipInfo.AppVersionMinor + "." + clipInfo.AppVersionPatch);
            if (clipInfo.ClipEntries == null) {
                sb.AppendLine("  (no ClipEntries)");
            } else {
                sb.AppendLine("  ClipEntries.Count=" + clipInfo.ClipEntries.Count);
                for (int i = 0; i < clipInfo.ClipEntries.Count; i++) {
                    AppCommon.ClipFileEntry entry = clipInfo.ClipEntries[i];
                    sb.AppendLine("  [" + i + "] '" + entry.Attribs.FullPathName +
                        "' part=" + entry.Part + " len=" + entry.OutputLength);
                }
            }
        }
    }
}
