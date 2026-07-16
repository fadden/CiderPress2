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

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

using FileConv;
using cp2_avalonia.Controls.FancyTextViewer;
using cp2_avalonia.Tools;
using static cp2_avalonia.Tools.ConfigOptCtrl;
using cp2_avalonia.Services;
using cp2_avalonia.ViewModels;

namespace cp2_avalonia.Views;

/// <summary>
/// File viewer dialog -- thin Window code-behind.
/// Logic lives in FileViewerViewModel.
/// </summary>
public partial class FileViewer : Window
{
    private FileViewerViewModel? VM => DataContext as FileViewerViewModel;

    private readonly List<ControlMapItem> mCustomCtrls = new List<ControlMapItem>();
    private bool mUpdatingTab;
    private readonly Cursor mHandCursor = new Cursor(StandardCursorType.Hand); 
    private bool mIsBitmapPanning;
    private Avalonia.Point mBitmapPanStartPoint;
    private Avalonia.Vector mBitmapPanStartOffset;
    private bool mShiftHeld;

    // Wheel handler delegates held as fields so they can be removed on Closing.
    private EventHandler<PointerWheelEventArgs>? mDataWheelTunnel;
    private EventHandler<PointerWheelEventArgs>? mRsrcWheelTunnel;
    private EventHandler<PointerWheelEventArgs>? mBitmapWheelTunnel;

    public FileViewer() {
        InitializeComponent();
        if (Design.IsDesignMode) return;

        Activated += (_, _) =>
            Dispatcher.UIThread.Post(UpdateBitmapPanCursor, DispatcherPriority.Input);
        Deactivated += (_, _) => {
            mShiftHeld = false;  // clear on focus loss so we don't get stuck
            if (!mIsBitmapPanning) {
                Cursor = null;
            }
        };

        // Track shift key so the nav button Click handlers can read it.
        // Button.Click fires with plain RoutedEventArgs, not PointerReleasedEventArgs,
        // so we cannot read KeyModifiers from the Click args directly.
        AddHandler(KeyDownEvent, (_, e) => {
            if (e.Key == Key.LeftShift || e.Key == Key.RightShift) mShiftHeld = true;
        }, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, (_, e) => {
            if (e.Key == Key.LeftShift || e.Key == Key.RightShift) mShiftHeld = false;
        }, RoutingStrategies.Tunnel);

        // Back/Forward history context menus — populated on demand.
        backContextMenu.Opening += (_, e) => {
            if (VM == null) { e.Cancel = true; return; }
            PopulateHistoryMenu(backContextMenu, VM.GetBackMenuItems());
            if (backContextMenu.Items.Count == 0) e.Cancel = true;
        };
        forwardContextMenu.Opening += (_, e) => {
            if (VM == null) { e.Cancel = true; return; }
            PopulateHistoryMenu(forwardContextMenu, VM.GetForwardMenuItems());
            if (forwardContextMenu.Items.Count == 0) e.Cancel = true;
        };

        // Drag/drop: accept a single file entry dragged from the main window file list.
        AddHandler(DragDrop.DragOverEvent, FileViewer_DragOver);
        AddHandler(DragDrop.DropEvent, FileViewer_Drop);

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e) {
        var vm = VM;
        if (vm == null) return;

        // Wire close event.
        vm.CloseRequested += _ => Close();

        // Subscribe to NoteText: set note text block
        vm.PropertyChanged += (s, args) => {
            switch (args.PropertyName) {
                case nameof(FileViewerViewModel.NoteText):
                    noteTextBlock.Inlines!.Clear();
                    noteTextBlock.Text = vm.NoteText;
                    noteScroll.Offset = new Avalonia.Vector(0, 0);
                    break;
                case nameof(FileViewerViewModel.SelectedForkTab):
                    mUpdatingTab = true;
                    tabControl.SelectedItem = vm.SelectedForkTab switch {
                        FileViewerViewModel.Tab.Rsrc => rsrcTabItem,
                        FileViewerViewModel.Tab.Note => noteTabItem,
                        _ => dataTabItem
                    };
                    mUpdatingTab = false;
                    break;
            }
        };

        // Subscribe to FindResultFound event.
        vm.FindResultFound += ApplyFindResult;

        // Build control map and start the viewer.
        CreateControlMap();

        // Wire wheel zoom handlers and keep references so they can be removed on close.
        mDataWheelTunnel = (_, ev) => ForwardWheelZoom(dataForkTextViewer, ev);
        dataForkScroll.AddHandler(InputElement.PointerWheelChangedEvent,
            mDataWheelTunnel, RoutingStrategies.Tunnel);

        mRsrcWheelTunnel = (_, ev) => ForwardWheelZoom(rsrcForkTextViewer, ev);
        rsrcForkScroll.AddHandler(InputElement.PointerWheelChangedEvent,
            mRsrcWheelTunnel, RoutingStrategies.Tunnel);

        mBitmapWheelTunnel = BitmapScroll_PointerWheelChanged;
        dataBitmapScroll.AddHandler(InputElement.PointerWheelChangedEvent,
            mBitmapWheelTunnel, RoutingStrategies.Tunnel);

        vm.Init();
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e) {
        // Remove wheel handlers to avoid dangling references.
        if (mDataWheelTunnel != null)
            dataForkScroll.RemoveHandler(InputElement.PointerWheelChangedEvent, mDataWheelTunnel);
        if (mRsrcWheelTunnel != null)
            rsrcForkScroll.RemoveHandler(InputElement.PointerWheelChangedEvent, mRsrcWheelTunnel);
        if (mBitmapWheelTunnel != null)
            dataBitmapScroll.RemoveHandler(InputElement.PointerWheelChangedEvent, mBitmapWheelTunnel);
    }

    // -----------------------------------------------------------------------------------------
    // Find result application

    private void ApplyFindResult((int offset, int length) result) {
        switch (VM!.SelectedForkTab) {
            case FileViewerViewModel.Tab.Rsrc:
                rsrcForkTextViewer.SelectRange(result.offset, result.length);
                break;
            case FileViewerViewModel.Tab.Note:
                noteTextBlock.SelectionStart = result.offset;
                noteTextBlock.SelectionEnd = result.offset + result.length;
                ScrollSelectionIntoView(noteTextBlock, result.offset);
                break;
            default:
                dataForkTextViewer.SelectRange(result.offset, result.length);
                break;
        }
    }

    private static void ScrollSelectionIntoView(SelectableTextBlock target, int offset) {
        // Defer to next layout pass so TextLayout reflects the new selection.
        Dispatcher.UIThread.Post(() => {
            var layout = target.TextLayout;
            Avalonia.Rect rect = layout.HitTestTextPosition(offset);
            // BringIntoView takes a rect in the control's local coordinates and asks
            // the containing ScrollViewer to scroll it into view.
            target.BringIntoView(rect);
        }, DispatcherPriority.Background);
    }

    // -----------------------------------------------------------------------------------------
    // FancyTextViewer input forwarding from surrounding scroll viewer space

    private static bool IsRightClickRelease(PointerReleasedEventArgs e) =>
        e.InitialPressMouseButton == MouseButton.Right;

    private static void ForwardWheelZoom(FancyTextViewer target, PointerWheelEventArgs e) {
        if (e.Handled || !target.IsVisible) {
            return;
        }
        if (target.TryAdjustScaleFromPointerWheel(e.KeyModifiers, e.Delta.Y)) {
            e.Handled = true;
        }
    }

    private static void ForwardContextMenu(FancyTextViewer target, PointerReleasedEventArgs e) {
        if (e.Handled || !target.IsVisible || !IsRightClickRelease(e)) {
            return;
        }
        if (target.TryOpenContextMenu()) {
            e.Handled = true;
        }
    }

    private void BitmapScroll_PointerWheelChanged(object? sender, PointerWheelEventArgs e) {
        if (e.Handled || VM == null || !VM.IsBitmapVisible) {
            return;
        }
        if ((e.KeyModifiers & KeyModifiers.Control) == 0 || e.Delta.Y == 0) {
            return;
        }
        if (e.Delta.Y > 0) {
            VM.IncrementMagTickCommand.Execute(null);
        } else {
            VM.DecrementMagTickCommand.Execute(null);
        }
        UpdateBitmapPanCursor();
        e.Handled = true;
    }

    private bool CanPanBitmap() {
        if (VM == null || !VM.IsBitmapVisible || !previewImage.IsVisible) {
            return false;
        }
        if (dataBitmapScroll.Viewport.Width <= 0 || dataBitmapScroll.Viewport.Height <= 0) {
            return false;
        }
        return dataBitmapScroll.Extent.Width > dataBitmapScroll.Viewport.Width + 0.5 ||
            dataBitmapScroll.Extent.Height > dataBitmapScroll.Viewport.Height + 0.5;
    }

    private void UpdateBitmapPanCursor() {
        bool showPanCursor = CanPanBitmap();
        previewImage.Cursor = showPanCursor ? mHandCursor : null;
        if (previewImage.IsPointerOver) {
            Cursor = showPanCursor ? mHandCursor : null;
        } else if (!mIsBitmapPanning) {
            Cursor = null;
        }
    }

    private void PreviewImage_PointerPressed(object? sender, PointerPressedEventArgs e) {
        UpdateBitmapPanCursor();
        if (e.Handled || !CanPanBitmap() || !e.GetCurrentPoint(previewImage).Properties.IsLeftButtonPressed) {
            return;
        }
        mIsBitmapPanning = true;
        mBitmapPanStartPoint = e.GetPosition(dataBitmapScroll);
        mBitmapPanStartOffset = dataBitmapScroll.Offset;
        e.Pointer.Capture(previewImage);
        e.Handled = true;
    }

    private void PreviewImage_PointerEntered(object? sender, PointerEventArgs e) {
        UpdateBitmapPanCursor();
    }

    private void PreviewImage_PointerMoved(object? sender, PointerEventArgs e) {
        UpdateBitmapPanCursor();
        if (!mIsBitmapPanning) {
            return;
        }
        Avalonia.Point pt = e.GetPosition(dataBitmapScroll);
        double maxX = Math.Max(0, dataBitmapScroll.Extent.Width - dataBitmapScroll.Viewport.Width);
        double maxY = Math.Max(0, dataBitmapScroll.Extent.Height - dataBitmapScroll.Viewport.Height);
        double nextX = Math.Clamp(mBitmapPanStartOffset.X - (pt.X - mBitmapPanStartPoint.X), 0, maxX);
        double nextY = Math.Clamp(mBitmapPanStartOffset.Y - (pt.Y - mBitmapPanStartPoint.Y), 0, maxY);
        dataBitmapScroll.Offset = new Avalonia.Vector(nextX, nextY);
        e.Handled = true;
    }

    private void EndBitmapPan(PointerEventArgs e) {
        if (!mIsBitmapPanning) {
            return;
        }
        mIsBitmapPanning = false;
        if (ReferenceEquals(e.Pointer.Captured, previewImage)) {
            e.Pointer.Capture(null);
        }
    }

    private void PreviewImage_PointerReleased(object? sender, PointerReleasedEventArgs e) {
        EndBitmapPan(e);
        UpdateBitmapPanCursor();
    }

    private void PreviewImage_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) {
        mIsBitmapPanning = false;
        UpdateBitmapPanCursor();
    }

    private void PreviewImage_PointerExited(object? sender, PointerEventArgs e) {
        if (!mIsBitmapPanning) {
            previewImage.Cursor = null;
            Cursor = null;
        }
    }

    private void DataForkScroll_PointerReleased(object? sender, PointerReleasedEventArgs e) {
        ForwardContextMenu(dataForkTextViewer, e);
    }

    private void RsrcForkScroll_PointerReleased(object? sender, PointerReleasedEventArgs e) {
        ForwardContextMenu(rsrcForkTextViewer, e);
    }

    // -----------------------------------------------------------------------------------------
    // List navigation buttons — detect Shift modifier to decide history coalescing behavior

    private void PrevInList_Click(object? sender, RoutedEventArgs e) {
        VM?.DoPrevInList(mShiftHeld);
    }

    private void NextInList_Click(object? sender, RoutedEventArgs e) {
        VM?.DoNextInList(mShiftHeld);
    }

    // -----------------------------------------------------------------------------------------
    // Tab control

    private void TabControl_SelectionChanged(object? sender, SelectionChangedEventArgs e) {
        if (tabControl == null || dataTabItem == null) return;
        if (mUpdatingTab) return;
        FileViewerViewModel.Tab tab = ReferenceEquals(tabControl.SelectedItem, rsrcTabItem)
            ? FileViewerViewModel.Tab.Rsrc
            : ReferenceEquals(tabControl.SelectedItem, noteTabItem)
                ? FileViewerViewModel.Tab.Note
                : FileViewerViewModel.Tab.Data;
        if (VM != null) {
            VM.SelectedForkTab = tab;
        }
    }


    // -----------------------------------------------------------------------------------------
    // Converter combo box

    private void ConvComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e) {
        if (VM == null) return;
        FileViewerViewModel.ConverterComboItem? item =
            convComboBox.SelectedItem as FileViewerViewModel.ConverterComboItem;
        if (item == null) {
            // No converter selected (list cleared mid-load): hide the whole Options box
            // rather than showing an empty shell or a misleading "no options" message.
            HideConvControls(mCustomCtrls);
            noOptions.IsVisible = false;
            optionsGroupBox.IsVisible = false;
            return;
        }
        ConfigureControls(item.Converter);
        VM.OnConverterSelected();
    }

    // -----------------------------------------------------------------------------------------
    // Text / HexDump / Best buttons

    private void SelectPlainText_Click(object? sender, RoutedEventArgs e) =>
        SelectConversionByType(typeof(FileConv.Generic.PlainText));

    private void SelectHexDump_Click(object? sender, RoutedEventArgs e) =>
        SelectConversionByType(typeof(FileConv.Generic.HexDump));

    private void SelectBest_Click(object? sender, RoutedEventArgs e) {
        if (VM != null && VM.ConverterList.Count > 0) {
            VM.SelectedConverterIndex = 0;
        }
    }

    private void SelectConversionByType(Type convType) {
        if (VM == null) return;
        for (int i = 0; i < VM.ConverterList.Count; i++) {
            if (VM.ConverterList[i].Converter.GetType() == convType) {
                VM.SelectedConverterIndex = i;
                return;
            }
        }
    }

    // -----------------------------------------------------------------------------------------
    // Search

    private void SearchStringTextBox_KeyDown(object? sender, KeyEventArgs e) {
        if (e.Key == Key.Return || e.Key == Key.Enter) {
            VM?.FindNextCommand.Execute(null);
            e.Handled = true;
        }
    }

    // -----------------------------------------------------------------------------------------
    // ConfigOptCtrl -- remains in code-behind (takes named control references)

    private void CreateControlMap() {
        mCustomCtrls.Clear();
        mCustomCtrls.Add(new ToggleButtonMapItem(UpdateOption, checkBox1));
        mCustomCtrls.Add(new ToggleButtonMapItem(UpdateOption, checkBox2));
        mCustomCtrls.Add(new ToggleButtonMapItem(UpdateOption, checkBox3));
        mCustomCtrls.Add(new TextBoxMapItem(UpdateOption, stringInput1,
            stringInput1_Label, stringInput1_Box));
        mCustomCtrls.Add(new RadioButtonGroupItem(UpdateOption, multiGroup1,
        [
            radioButton1_1, radioButton1_2, radioButton1_3, radioButton1_4
        ]));
        mCustomCtrls.Add(new RadioButtonGroupItem(UpdateOption, multiGroup2,
        [
            radioButton2_1, radioButton2_2, radioButton2_3, radioButton2_4
        ]));
    }

    private void ConfigureControls(Converter conv) {
        VM!.BeginConfiguring();
        var newOpts = VM.TryConsumePendingConverterOptions(conv.Tag, out Dictionary<string, string> pendingOpts)
            ? pendingOpts
            : LoadExportOptions(conv.OptionDefs,
                AppSettings.VIEW_SETTING_PREFIX, conv.Tag, VM!.SettingsService);
        VM.SetConvOptions(newOpts);
        HideConvControls(mCustomCtrls);
        noOptions.IsVisible = (conv.OptionDefs.Count == 0);
        ConfigOptCtrl.ConfigureControls(mCustomCtrls, conv.OptionDefs, VM.GetConvOptions());
        // A converter is configured: show the Options box (with either its option
        // controls or the "no options" message).
        optionsGroupBox.IsVisible = true;
        Dispatcher.UIThread.Post(() => VM?.EndConfiguring());
    }

    private void UpdateOption(string tag, string newValue) {
        VM?.SetConvOption(tag, newValue);
    }

    // -----------------------------------------------------------------------------------------
    // History context menus

    private void PopulateHistoryMenu(ContextMenu menu,
            IEnumerable<FileViewerViewModel.HistoryMenuEntry> entries)
    {
        menu.Items.Clear();
        foreach (var entry in entries) {
            int capturedIndex = entry.HistoryIndex;
            var item = new MenuItem { Header = entry.Label };
            ToolTip.SetTip(item, entry.Tooltip);
            item.Click += (_, _) => VM?.NavigateToHistory(capturedIndex);
            menu.Items.Add(item);
        }
    }

    // -----------------------------------------------------------------------------------------
    // Window lifecycle

    protected override void OnClosed(EventArgs e) {
        base.OnClosed(e);
        VM?.Dispose();
    }

    // -----------------------------------------------------------------------------------------
    // Drag/drop — accept a single file entry from the main window file list

    private const string DRAG_FORMAT = "cp2_avalonia/FileListDrag";
    private const string DRAG_SOURCE_FORMAT = "cp2_avalonia/FileListDragSource";
    private const string DRAG_WORKPATH_FORMAT = "cp2_avalonia/FileListDragWorkPath";

    private void FileViewer_DragOver(object? sender, DragEventArgs e)
    {
        if (e.Data.Contains(DRAG_FORMAT) && e.Data.Contains(DRAG_SOURCE_FORMAT)) {
            e.DragEffects = DragDropEffects.Copy;
        } else {
            e.DragEffects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void FileViewer_Drop(object? sender, DragEventArgs e)
    {
        if (VM == null) return;
        if (!e.Data.Contains(DRAG_FORMAT) || !e.Data.Contains(DRAG_SOURCE_FORMAT)) return;

        var entries = e.Data.Get(DRAG_FORMAT) as List<DiskArc.IFileEntry>;
        var source = e.Data.Get(DRAG_SOURCE_FORMAT);
        var workPath = e.Data.Get(DRAG_WORKPATH_FORMAT) as string ?? string.Empty;

        if (entries == null || entries.Count == 0 || source == null) return;

        VM.LoadDroppedEntry(source, entries[0], workPath);
        e.Handled = true;
    }
}
