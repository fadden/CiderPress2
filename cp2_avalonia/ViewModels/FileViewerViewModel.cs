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
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommonUtil;
using DiskArc;
using DiskArc.FS;
using FileConv;
using cp2_avalonia.Common;
using cp2_avalonia.Services;

namespace cp2_avalonia.ViewModels;

/// <summary>
/// ViewModel for the File Viewer dialog.
/// </summary>
public class FileViewerViewModel : ObservableObject, IDisposable
{
    // -----------------------------------------------------------------------------------------
    // Inner types

    public enum Tab { Unknown = 0, Data, Rsrc, Note }
    private enum DisplayItemType { Unknown = 0, SimpleText, FancyText, Bitmap }
    public enum ReconcileResult { NoChange = 0, Reverted, Close, CannotLoad }

    private class HistoryEntry
    {
        public required string CurrentPath { get; init; }
        public required List<string> SelectedPaths { get; init; }
        public required int CurrentIndex { get; init; }
        public required string SourceWorkPathName { get; init; }
        public required string ConverterTag { get; init; }
        public required Dictionary<string, string> ConverterOptions { get; init; }
        public required Tab SelectedTab { get; init; }
    }

    /// <summary>
    /// Item data for the Back/Forward history context menus.
    /// </summary>
    public record HistoryMenuEntry(string Label, string Tooltip, int HistoryIndex);

    public class ConverterComboItem(string name, Converter converter)
    {
        public string Name { get; private set; } = name;
        public Converter Converter { get; private set; } = converter;

        public override string ToString() => Name;
    }

    // -----------------------------------------------------------------------------------------
    // Constants

    private const int MAX_MAGNIFICATION_TICK = 8;
    private const int MAX_BITMAP_SCALE_DIMENSION = Bitmap8.MAX_DIMENSION;
    private const int MAX_FANCY_TEXT = 1024 * 1024 * 2;
    private const int MAX_SIMPLE_TEXT = 1024 * 1024 * 2;
    private const string TEMP_FILE_PREFIX = "cp2tmp_";

    // -----------------------------------------------------------------------------------------
    // Services

    internal ISettingsService SettingsService => _settingsService;

    private readonly ISettingsService _settingsService;
    private readonly IFilePickerService _filePickerService;
    private readonly IClipboardService _clipboardService;
    private readonly IViewerService _viewerService;

    // -----------------------------------------------------------------------------------------
    // State

    private object mArchiveOrFileSystem;
    private readonly List<IFileEntry> mSelected;
    private readonly List<string> mSelectedPaths = new();
    private readonly List<HistoryEntry> mHistory = new();
    private int mCurIndex;
    private Dictionary<string, string> mConvOptions = new();
    private Stream? mDataFork;
    private Stream? mRsrcFork;
    private IConvOutput? mCurDataOutput;
    private IConvOutput? mCurRsrcOutput;
    private string mNoteTextStr = string.Empty;
    private DisplayItemType mDataDisplayType = DisplayItemType.Unknown;
    private readonly AppHook mAppHook;
    private readonly List<string> mTmpFiles = new();
    private string mDataForkText = string.Empty;
    private string mRsrcForkText = string.Empty;
    private int mFindCaretOffset;
    private bool mIsConfiguring;
    private int mHistoryIndex = -1;
    private bool mIsRestoringHistory;
    private HistoryEntry? mPendingHistoryRestore;
    private bool mPrePopulateForwardOnInit;

    // -----------------------------------------------------------------------------------------
    // Find result event (pushes (offset, length) to code-behind)

    public event Action<(int offset, int length)>? FindResultFound;

    // -----------------------------------------------------------------------------------------
    // Close interaction

    public event Action<bool>? CloseRequested;
    public string SourceWorkPathName { get; private set; }

    // -----------------------------------------------------------------------------------------
    // Commands

    public IRelayCommand PrevInListCommand { get; }
    public IRelayCommand NextInListCommand { get; }
    public IRelayCommand BackCommand { get; }
    public IRelayCommand ForwardCommand { get; }
    public IRelayCommand ExportCommand { get; }
    public IRelayCommand FindNextCommand { get; }
    public IRelayCommand FindPrevCommand { get; }
    public IRelayCommand CopyTextCommand { get; }
    public IRelayCommand SaveDefaultsCommand { get; }
    public IRelayCommand CloseCommand { get; }
    public IRelayCommand DecrementMagTickCommand { get; }
    public IRelayCommand IncrementMagTickCommand { get; }

    // -----------------------------------------------------------------------------------------
    // Converter list (bound to convComboBox.ItemsSource)

    public ObservableCollection<ConverterComboItem> ConverterList { get; } = new();
    public FancyTextDisplayViewModel DataTextViewModel { get; } = new();
    public FancyTextDisplayViewModel RsrcTextViewModel { get; } = new();

    // -----------------------------------------------------------------------------------------

    private bool mIsDataTabEnabled;
    public bool IsDataTabEnabled
    {
        get => mIsDataTabEnabled;
        set => SetProperty(ref mIsDataTabEnabled, value);
    }

    private bool mIsRsrcTabEnabled;
    public bool IsRsrcTabEnabled
    {
        get => mIsRsrcTabEnabled;
        set => SetProperty(ref mIsRsrcTabEnabled, value);
    }

    private bool mIsNoteTabEnabled;
    public bool IsNoteTabEnabled
    {
        get => mIsNoteTabEnabled;
        set => SetProperty(ref mIsNoteTabEnabled, value);
    }

    private bool mIsTextVisible;
    public bool IsTextVisible
    {
        get => mIsTextVisible;
        set => SetProperty(ref mIsTextVisible, value);
    }

    private bool mIsBitmapVisible;
    public bool IsBitmapVisible
    {
        get => mIsBitmapVisible;
        set => SetProperty(ref mIsBitmapVisible, value);
    }

    private Bitmap? mPreviewBitmap;
    public Bitmap? PreviewBitmap
    {
        get => mPreviewBitmap;
        set => SetProperty(ref mPreviewBitmap, value);
    }

    private bool mIsOptionsBoxEnabled;
    public bool IsOptionsBoxEnabled
    {
        get => mIsOptionsBoxEnabled;
        set => SetProperty(ref mIsOptionsBoxEnabled, value);
    }

    // Starts true so the very first frame shows the loading indicator instead of the
    // uninitialized conversion panel; cleared when the first (deferred) load completes.
    private bool mIsLoading = true;
    /// <summary>
    /// True while a file conversion/display operation is pending or in progress. Drives
    /// the "Loading..." overlay and disables the conversion panel.
    /// </summary>
    public bool IsLoading
    {
        get => mIsLoading;
        set => SetProperty(ref mIsLoading, value);
    }

    private bool mIsSaveDefaultsEnabled;
    public bool IsSaveDefaultsEnabled
    {
        get => mIsSaveDefaultsEnabled;
                set { if (SetProperty(ref mIsSaveDefaultsEnabled, value)) SaveDefaultsCommand?.NotifyCanExecuteChanged(); }
    }

    public bool IsDOSRaw
    {
        get => _settingsService.GetBool(AppSettings.VIEW_RAW_ENABLED, false);
        set
        {
            _settingsService.SetBool(AppSettings.VIEW_RAW_ENABLED, value);
            OnPropertyChanged(nameof(IsDOSRaw));
            ShowFile(true);
        }
    }

    private bool mIsDOSRawEnabled;
    public bool IsDOSRawEnabled
    {
        get => mIsDOSRawEnabled;
        set => SetProperty(ref mIsDOSRawEnabled, value);
    }

    private bool mIsFindEnabled;
    public bool IsFindEnabled
    {
        get => mIsFindEnabled;
        set => SetProperty(ref mIsFindEnabled, value);
    }

    private bool mIsFindButtonsEnabled;
    public bool IsFindButtonsEnabled
    {
        get => mIsFindButtonsEnabled;
        set { if (SetProperty(ref mIsFindButtonsEnabled, value)) { FindNextCommand?.NotifyCanExecuteChanged(); FindPrevCommand?.NotifyCanExecuteChanged(); } }
    }

    private bool mIsExportEnabled;
    public bool IsExportEnabled
    {
        get => mIsExportEnabled;
        set { if (SetProperty(ref mIsExportEnabled, value)) { ExportCommand?.NotifyCanExecuteChanged(); CopyTextCommand?.NotifyCanExecuteChanged(); } }
    }

    private bool mHasPrevInList;
    public bool HasPrevInList
    {
        get => mHasPrevInList;
        set { if (SetProperty(ref mHasPrevInList, value)) PrevInListCommand?.NotifyCanExecuteChanged(); }
    }

    private string mPrevInListTip = "Previous in List";
    public string PrevInListTip
    {
        get => mPrevInListTip;
        set => SetProperty(ref mPrevInListTip, value);
    }

    private bool mHasNextInList;
    public bool HasNextInList
    {
        get => mHasNextInList;
        set { if (SetProperty(ref mHasNextInList, value)) NextInListCommand?.NotifyCanExecuteChanged(); }
    }

    private string mNextInListTip = "Next in List";
    public string NextInListTip
    {
        get => mNextInListTip;
        set => SetProperty(ref mNextInListTip, value);
    }

    private bool mHasBackHistory;
    public bool HasBackHistory
    {
        get => mHasBackHistory;
        set { if (SetProperty(ref mHasBackHistory, value)) BackCommand?.NotifyCanExecuteChanged(); }
    }

    private string mBackTip = "Back";
    public string BackTip
    {
        get => mBackTip;
        set => SetProperty(ref mBackTip, value);
    }

    private bool mHasForwardHistory;
    public bool HasForwardHistory
    {
        get => mHasForwardHistory;
        set { if (SetProperty(ref mHasForwardHistory, value)) ForwardCommand?.NotifyCanExecuteChanged(); }
    }

    private string mForwardTip = "Forward";
    public string ForwardTip
    {
        get => mForwardTip;
        set => SetProperty(ref mForwardTip, value);
    }

    private string mGraphicsZoomStr = "1×";
    public string GraphicsZoomStr
    {
        get => mGraphicsZoomStr;
        set => SetProperty(ref mGraphicsZoomStr, value);
    }

    private string mSearchString = string.Empty;
    public string SearchString
    {
        get => mSearchString;
        set
        {
            SetProperty(ref mSearchString, value);
            mFindCaretOffset = 0;
            UpdateFindControls();
        }
    }

    private bool mIsSourceModifiedWarningVisible;
    public bool IsSourceModifiedWarningVisible
    {
        get => mIsSourceModifiedWarningVisible;
        set => SetProperty(ref mIsSourceModifiedWarningVisible, value);
    }

    private int mSelectedConverterIndex = -1;
    public int SelectedConverterIndex
    {
        get => mSelectedConverterIndex;
        set => SetProperty(ref mSelectedConverterIndex, value);
    }

    // DataForkText — pre-extracted/truncated text used for find/copy/export on text output.
    public string DataForkText => mDataForkText;

    private string mNoteText = string.Empty;
    public string NoteText
    {
        get => mNoteText;
        private set => SetProperty(ref mNoteText, value);
    }

    private Tab mSelectedForkTab = Tab.Unknown;
    public Tab SelectedForkTab
    {
        get => mSelectedForkTab;
        set
        {
            SetProperty(ref mSelectedForkTab, value);
            IsOptionsBoxEnabled = (value == Tab.Data);
            mFindCaretOffset = 0;
            UpdateCurrentHistoryEntrySnapshot();
        }
    }

    private double mMagnificationTick = 1.0;
    public double MagnificationTick
    {
        get => mMagnificationTick;
        set
        {
            SetProperty(ref mMagnificationTick, value);
            if (mCurDataOutput != null)
            {
                ConfigureMagnification();
            }
        }
    }

    private double mPreviewImageWidth = double.NaN;
    public double PreviewImageWidth
    {
        get => mPreviewImageWidth;
        set => SetProperty(ref mPreviewImageWidth, value);
    }

    private double mPreviewImageHeight = double.NaN;
    public double PreviewImageHeight
    {
        get => mPreviewImageHeight;
        set => SetProperty(ref mPreviewImageHeight, value);
    }

    private string mWindowTitle = "File Viewer";
    public string WindowTitle
    {
        get => mWindowTitle;
        set => SetProperty(ref mWindowTitle, value);
    }

    // -----------------------------------------------------------------------------------------
    // Constructor

    private bool CanPrevInList() => HasPrevInList;
    private bool CanNextInList() => HasNextInList;
    private bool CanBack() => HasBackHistory;
    private bool CanForward() => HasForwardHistory;
    private bool CanExport() => IsExportEnabled;
    private bool CanFindButtons() => IsFindButtonsEnabled;
    private bool CanSaveDefaults() => IsSaveDefaultsEnabled;

    public FileViewerViewModel(
            object archiveOrFileSystem,
            List<IFileEntry> entries,
            int initialIndex,
            string sourceWorkPathName,
            AppHook appHook,
            ISettingsService settingsService,
            IFilePickerService filePickerService,
            IClipboardService clipboardService,
            IViewerService viewerService,
            bool prePopulateForward = false)
    {
        mArchiveOrFileSystem = archiveOrFileSystem;
        mSelected = new List<IFileEntry>(entries);
        mCurIndex = initialIndex;
        mPrePopulateForwardOnInit = prePopulateForward;
        SourceWorkPathName = sourceWorkPathName;
        mAppHook = appHook;
        _settingsService = settingsService;
        _filePickerService = filePickerService;
        _clipboardService = clipboardService;
        _viewerService = viewerService;
        CaptureSelectionPaths();

        // Initial property values
        IsDataTabEnabled = IsRsrcTabEnabled = IsNoteTabEnabled = false;
        IsTextVisible = true;
        IsBitmapVisible = false;
        IsOptionsBoxEnabled = false;
        IsSaveDefaultsEnabled = false;
        IsDOSRawEnabled = false;
        IsFindEnabled = IsFindButtonsEnabled = false;
        IsExportEnabled = false;
        HasPrevInList = HasNextInList = false;
        HasBackHistory = HasForwardHistory = false;

        // Commands
        PrevInListCommand = new RelayCommand(() => DoPrevInList(false), CanPrevInList);
        NextInListCommand = new RelayCommand(() => DoNextInList(false), CanNextInList);
        BackCommand = new RelayCommand(DoBack, CanBack);
        ForwardCommand = new RelayCommand(DoForward, CanForward);
        ExportCommand = new AsyncRelayCommand(DoExportAsync, CanExport);
        FindNextCommand = new RelayCommand(() => DoFind(true), CanFindButtons);
        FindPrevCommand = new RelayCommand(() => DoFind(false), CanFindButtons);
        CopyTextCommand = new AsyncRelayCommand(DoCopyAsync, CanExport);
        SaveDefaultsCommand = new RelayCommand(DoSaveDefaults, CanSaveDefaults);
        CloseCommand = new AsyncRelayCommand(
            async () => CloseRequested?.Invoke(true));
        DecrementMagTickCommand = new RelayCommand(() =>
        {
            MagnificationTick--;
            if (MagnificationTick < 0)
            {
                MagnificationTick = 0;
            }
        });
        IncrementMagTickCommand = new RelayCommand(() =>
        {
            MagnificationTick++;
            if (MagnificationTick > MAX_MAGNIFICATION_TICK)
            {
                MagnificationTick = MAX_MAGNIFICATION_TICK; 
            }
        });
    }

    // -----------------------------------------------------------------------------------------
    // Init — called by code-behind from the Loaded handler after subscriptions are ready

    public void Init()
    {
        UpdateNavigationControls();
        AppendCurrentHistoryEntry(clearForward: false);
        if (mPrePopulateForwardOnInit) PrePopulateForwardHistory();
        ShowFileDeferred(true);
    }

    public bool IsSourceObject(object archiveOrFileSystem) =>
        ReferenceEquals(mArchiveOrFileSystem, archiveOrFileSystem);

    /// <summary>
    /// Loads a single file entry dropped onto this viewer window.
    /// If the entry is a directory, loads all non-directory files within it instead.
    /// Behaves like a manual open: appends to history and clears forward stack.
    /// </summary>
    public void LoadDroppedEntry(object archiveOrFileSystem, IFileEntry entry,
            string sourceWorkPathName)
    {
        List<IFileEntry> toLoad;
        if (entry.IsDirectory) {
            toLoad = new List<IFileEntry>();
            foreach (IFileEntry child in entry) {
                if (!child.IsDirectory) {
                    toLoad.Add(child);
                }
            }
            if (toLoad.Count == 0) return;  // empty directory — nothing to show
        } else {
            toLoad = new List<IFileEntry> { entry };
        }
        LoadEntries(archiveOrFileSystem, toLoad, 0, sourceWorkPathName,
            prePopulateForward: false);
    }

    public void LoadEntries(object archiveOrFileSystem, List<IFileEntry> entries,
            int initialIndex, string sourceWorkPathName, bool prePopulateForward = false)
    {
        CloseStreams();
        bool sourceChanged = !ReferenceEquals(mArchiveOrFileSystem, archiveOrFileSystem);
        mArchiveOrFileSystem = archiveOrFileSystem;
        mSelected.Clear();
        mSelected.AddRange(entries);
        mCurIndex = Math.Clamp(initialIndex, 0, mSelected.Count - 1);
        SourceWorkPathName = sourceWorkPathName;
        mConvOptions = new();
        mPendingHistoryRestore = null;
        mIsRestoringHistory = false;
        if (sourceChanged) {
            mHistory.Clear();
            mHistoryIndex = -1;
        }
        CaptureSelectionPaths();
        UpdateNavigationControls();
        AppendCurrentHistoryEntry(clearForward: true);
        if (prePopulateForward) PrePopulateForwardHistory();
        ShowFileDeferred(true);
    }

    public void CaptureSelectionPaths()
    {
        mSelectedPaths.Clear();
        mSelectedPaths.AddRange(mSelected.Select(entry => entry.FullPathName));
    }

    /// <summary>
    /// Updates the navigation pool (the list used by Up/Down arrows) to reflect the
    /// current file-list selection in the main window. Called whenever the user changes
    /// the highlighted items while the viewer is open.
    /// The currently displayed file must be present in the new pool; if it is not found
    /// the pool is left unchanged to avoid displaying a file whose index is inconsistent.
    /// History for the current entry is updated in-place so Back/Forward tooltips stay
    /// accurate, but no history append or ShowFile call is made.
    /// </summary>
    public void UpdatePool(object archiveOrFileSystem, List<IFileEntry> newEntries)
    {
        if (!ReferenceEquals(mArchiveOrFileSystem, archiveOrFileSystem)) {
            return;
        }
        if (newEntries.Count == 0) {
            return;
        }

        // Determine where the currently displayed file lives in the new pool.
        string currentPath = (mCurIndex >= 0 && mCurIndex < mSelectedPaths.Count)
            ? mSelectedPaths[mCurIndex] : string.Empty;
        List<string> newPaths = newEntries.Select(e => e.FullPathName).ToList();
        int newIdx = string.IsNullOrEmpty(currentPath) ? -1 : newPaths.IndexOf(currentPath);

        if (newIdx < 0) {
            // Current file is not in the new pool — add it at the front so it remains
            // accessible and the index is consistent with what is displayed.
            newEntries.Insert(0, mSelected[mCurIndex]);
            newPaths.Insert(0, currentPath);
            newIdx = 0;
        }

        mSelected.Clear();
        mSelected.AddRange(newEntries);
        mCurIndex = newIdx;
        CaptureSelectionPaths();

        // Keep the current history entry consistent with the new pool.
        UpdateCurrentHistoryEntrySnapshot();
    }

    public void CaptureCurrentStatePaths()
    {
        CaptureSelectionPaths();
        UpdateCurrentHistoryEntrySnapshot();
    }

    /// <summary>
    /// Pre-populates the forward history stack with stub entries for the files after the
    /// current one in the selection. Called after AppendCurrentHistoryEntry so stubs sit
    /// beyond mHistoryIndex. The user can then step through them with Forward without
    /// returning to the main window. Each stub uses an empty converter tag so the viewer
    /// picks the best default converter for that file.
    /// </summary>
    private void PrePopulateForwardHistory()
    {
        List<string> sharedPaths = new List<string>(mSelectedPaths);
        for (int i = mCurIndex + 1; i < sharedPaths.Count; i++) {
            mHistory.Add(new HistoryEntry {
                CurrentPath = sharedPaths[i],
                SelectedPaths = sharedPaths,
                CurrentIndex = i,
                SourceWorkPathName = SourceWorkPathName,
                ConverterTag = string.Empty,
                ConverterOptions = new Dictionary<string, string>(),
                SelectedTab = Tab.Data
            });
        }
        UpdateNavigationControls();
    }

    public ReconcileResult ReconcileAfterRefresh(object archiveOrFileSystem)
    {
        if (!ReferenceEquals(mArchiveOrFileSystem, archiveOrFileSystem)) {
            return ReconcileResult.Close;
        }
        if (mHistory.Count == 0) {
            return ReconcileResult.Close;
        }

        int currentIndex = mHistoryIndex;
        for (int i = mHistory.Count - 1; i >= 0; i--) {
            if (!TryRebuildHistoryEntry(mHistory[i], out HistoryEntry rebuilt)) {
                mHistory.RemoveAt(i);
                if (i < currentIndex) {
                    currentIndex--;
                } else if (i == currentIndex) {
                    currentIndex = i - 1;
                }
            } else {
                mHistory[i] = rebuilt;
            }
        }

        if (mHistory.Count == 0) {
            mHistoryIndex = -1;
            UpdateNavigationControls();
            return ReconcileResult.Close;
        }
        if (currentIndex < 0 || currentIndex >= mHistory.Count) {
            mHistoryIndex = FindFallbackHistoryIndex(currentIndex);
            if (mHistoryIndex < 0 || !RestoreHistoryEntry(mHistoryIndex)) {
                return ReconcileResult.Close;
            }
            FileNavigated?.Invoke(mSelected[mCurIndex]);
            return ReconcileResult.Reverted;
        }

        mHistoryIndex = currentIndex;
        if (!RestoreHistoryEntry(mHistoryIndex)) {
            int fallbackIndex = FindFallbackHistoryIndex(currentIndex - 1);
            if (fallbackIndex < 0 || !RestoreHistoryEntry(fallbackIndex)) {
                return ReconcileResult.CannotLoad;
            }
            mHistoryIndex = fallbackIndex;
            FileNavigated?.Invoke(mSelected[mCurIndex]);
            return ReconcileResult.Reverted;
        }
        return ReconcileResult.NoChange;
    }

    public void RequestClose()
    {
        CloseRequested?.Invoke(true);
    }

    /// <summary>
    /// Called after a sector/block editor session to reload with a freshly-reparsed
    /// filesystem or archive.  Swaps in the new source, re-resolves all selected paths,
    /// and reloads the current file in whatever converter mode was active.
    /// Returns true if the current file was found in the new source and the viewer was
    /// successfully updated; false if it could not be found (caller should close the viewer).
    /// </summary>
    public bool TryReloadWithNewSource(object newArchiveOrFileSystem)
    {
        string currentPath = (mCurIndex >= 0 && mCurIndex < mSelectedPaths.Count)
            ? mSelectedPaths[mCurIndex] : string.Empty;
        if (string.IsNullOrEmpty(currentPath)) return false;

        object oldSource = mArchiveOrFileSystem;
        mArchiveOrFileSystem = newArchiveOrFileSystem;

        try
        {
            if (!TryResolveSelectedPaths(mSelectedPaths, currentPath,
                    out List<IFileEntry> entries, out List<string> paths, out int currentIndex))
            {
                mArchiveOrFileSystem = oldSource;
                return false;
            }

            CloseStreams();
            mSelected.Clear();
            mSelected.AddRange(entries);
            mSelectedPaths.Clear();
            mSelectedPaths.AddRange(paths);
            mCurIndex = currentIndex;
            UpdateNavigationControls();
            ShowFile(false);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.W("File viewer: failed to switch source", ex);
            mArchiveOrFileSystem = oldSource;
            return false;
        }
    }

    // -----------------------------------------------------------------------------------------
    // Dialog suppression

    /// <summary>
    /// Returns true if the "This file is unavailable. Viewer will close" dialog is suppressed.
    /// </summary>
    public bool IsUnavailableCloseDialogSuppressed() =>
        _settingsService.GetBool(AppSettings.VIEWER_SUPPRESS_UNAVAILABLE_CLOSE, false);

    /// <summary>
    /// Returns true if the "File is unavailable. Reverting to previous file" dialog is suppressed.
    /// </summary>
    public bool IsUnavailableRevertDialogSuppressed() =>
        _settingsService.GetBool(AppSettings.VIEWER_SUPPRESS_UNAVAILABLE_REVERT, false);

    /// <summary>
    /// Persists the user's suppression choice for the unavailable-close dialog.
    /// </summary>
    public void SetUnavailableCloseDialogSuppressed(bool suppress) =>
        _settingsService.SetBool(AppSettings.VIEWER_SUPPRESS_UNAVAILABLE_CLOSE, suppress);

    /// <summary>
    /// Persists the user's suppression choice for the unavailable-revert dialog.
    /// </summary>
    public void SetUnavailableRevertDialogSuppressed(bool suppress) =>
        _settingsService.SetBool(AppSettings.VIEWER_SUPPRESS_UNAVAILABLE_REVERT, suppress);

    // -----------------------------------------------------------------------------------------
    // Navigation

    /// <summary>
    /// Raised after the viewer navigates to a different file.  The argument is the new entry.
    /// </summary>
    public event Action<IFileEntry>? FileNavigated;

    /// <summary>
    /// Returns the file entry currently displayed, or null if the viewer has no valid selection.
    /// </summary>
    public IFileEntry? CurrentEntry =>
        (mCurIndex >= 0 && mCurIndex < mSelected.Count) ? mSelected[mCurIndex] : null;

    // Called from code-behind to handle the Shift modifier on list-navigation buttons.
    // Without shift: replace current history entry (coalesce, preserve forward history).
    // With shift: append new history entry and clear forward history (record every step).
    public void DoPrevInList(bool shift)
    {
        if (mCurIndex <= 0) return;
        mCurIndex--;
        if (shift) {
            AppendCurrentHistoryEntry(clearForward: true);
        } else {
            ReplaceCurrentHistoryEntry();
        }
        ShowFileDeferred(true);
        FileNavigated?.Invoke(mSelected[mCurIndex]);
    }

    public void DoNextInList(bool shift)
    {
        if (mCurIndex >= mSelected.Count - 1) return;
        mCurIndex++;
        if (shift) {
            AppendCurrentHistoryEntry(clearForward: true);
        } else {
            ReplaceCurrentHistoryEntry();
        }
        ShowFileDeferred(true);
        FileNavigated?.Invoke(mSelected[mCurIndex]);
    }

    private void DoBack()
    {
        Debug.Assert(mHistoryIndex > 0);
        NavigateToHistory(mHistoryIndex - 1);
    }

    private void DoForward()
    {
        Debug.Assert(mHistoryIndex >= 0 && mHistoryIndex < mHistory.Count - 1);
        NavigateToHistory(mHistoryIndex + 1);
    }

    /// <summary>
    /// Navigates to the history entry at the given index. Used by Back, Forward, and the
    /// history context menus so they all share the same FileNavigated side-effect.
    /// </summary>
    public void NavigateToHistory(int historyIndex)
    {
        if (RestoreHistoryEntry(historyIndex)) {
            FileNavigated?.Invoke(mSelected[mCurIndex]);
        }
    }

    /// <summary>
    /// Returns menu entries for the Back history context menu (newest-first).
    /// </summary>
    public IEnumerable<HistoryMenuEntry> GetBackMenuItems()
    {
        for (int i = mHistoryIndex - 1; i >= 0; i--) {
            yield return MakeMenuEntry(i);
        }
    }

    /// <summary>
    /// Returns menu entries for the Forward history context menu (oldest-first).
    /// </summary>
    public IEnumerable<HistoryMenuEntry> GetForwardMenuItems()
    {
        for (int i = mHistoryIndex + 1; i < mHistory.Count; i++) {
            yield return MakeMenuEntry(i);
        }
    }

    private HistoryMenuEntry MakeMenuEntry(int index)
    {
        string path = mHistory[index].CurrentPath;
        string filename = string.IsNullOrEmpty(path) ? string.Empty : Path.GetFileName(path);
        string label = string.IsNullOrEmpty(filename) ? path : filename;
        if (string.IsNullOrEmpty(label)) label = "(unknown)";
        return new HistoryMenuEntry(label, path, index);
    }

    private void UpdateNavigationControls()
    {
        if (mCurIndex > 0) {
            HasPrevInList = true;
            PrevInListTip = "Previous in List\n" + mSelected[mCurIndex - 1].FullPathName;
        } else {
            HasPrevInList = false;
            PrevInListTip = "Previous in List";
        }
        if (mCurIndex < mSelected.Count - 1) {
            HasNextInList = true;
            NextInListTip = "Next in List\n" + mSelected[mCurIndex + 1].FullPathName;
        } else {
            HasNextInList = false;
            NextInListTip = "Next in List";
        }
        if (mHistoryIndex > 0) {
            HasBackHistory = true;
            BackTip = "Back\n" + mHistory[mHistoryIndex - 1].CurrentPath;
        } else {
            HasBackHistory = false;
            BackTip = "Back";
        }
        if (mHistoryIndex >= 0 && mHistoryIndex < mHistory.Count - 1) {
            HasForwardHistory = true;
            ForwardTip = "Forward\n" + mHistory[mHistoryIndex + 1].CurrentPath;
        } else {
            HasForwardHistory = false;
            ForwardTip = "Forward";
        }
    }

    // -----------------------------------------------------------------------------------------
    // File display

    private bool mShowFilePending;
    private bool mPendingFileChanged;

    /// <summary>
    /// Shows the loading indicator and defers the (synchronous, potentially slow) file
    /// conversion until after the next render pass, so the user sees "Loading..." instead
    /// of a frozen or uninitialized viewer. Rapid navigation clicks coalesce into a
    /// single load of the most recent target, since navigation state is updated by the
    /// caller before this is invoked.
    /// </summary>
    /// <param name="fileChanged">Passed through to <see cref="ShowFile"/>.</param>
    private void ShowFileDeferred(bool fileChanged)
    {
        IsLoading = true;
        mPendingFileChanged |= fileChanged;
        if (mShowFilePending)
        {
            return;
        }
        mShowFilePending = true;
        Dispatcher.UIThread.Post(() =>
        {
            mShowFilePending = false;
            bool changed = mPendingFileChanged;
            mPendingFileChanged = false;
            try
            {
                ShowFile(changed);
            }
            finally
            {
                IsLoading = false;
            }
        }, DispatcherPriority.Background);
    }

    private void ShowFile(bool fileChanged)
    {
        IsDOSRawEnabled = (mArchiveOrFileSystem is DOS);
        CloseStreams();
        IsExportEnabled = IsFindEnabled = false;

        IFileEntry entry = mSelected[mCurIndex];
        FileAttribs attrs = new FileAttribs(entry);
        List<Converter>? applicableConverters;
        try
        {
            bool macZipEnabled = _settingsService.GetBool(AppSettings.MAC_ZIP_ENABLED, true);
            applicableConverters = ExportFoundry.GetApplicableConverters(mArchiveOrFileSystem,
                entry, attrs, IsDOSRaw, macZipEnabled, out mDataFork, out mRsrcFork, mAppHook);
            Debug.Assert(applicableConverters.Count > 0);
        }
        catch (Exception ex)
        {
            AppLog.E("File viewer: conversion failed", ex);
            applicableConverters = null;
            // Signal an error via the text display viewmodels directly.
            mCurDataOutput = new SimpleText("Viewer error: " + ex.Message);
            mCurRsrcOutput = null;
            mDataForkText = ((SimpleText)mCurDataOutput).Text.ToString();
            mRsrcForkText = string.Empty;
            mNoteTextStr = string.Empty;
            mDataDisplayType = DisplayItemType.SimpleText;
            IsTextVisible = true;
            IsBitmapVisible = false;
            IsDataTabEnabled = true;
            IsRsrcTabEnabled = false;
            IsNoteTabEnabled = false;
            DataTextViewModel.SetFromConvOutput(mCurDataOutput);
            RsrcTextViewModel.SetFromConvOutput(null);
            NoteText = string.Empty;
            SelectedForkTab = Tab.Data;
        }

        if (applicableConverters != null)
        {
            string oldTag = mPendingHistoryRestore?.ConverterTag ?? GetCurrentConverterTag();

            ConverterList.Clear();
            if (applicableConverters.Count > 0)
            {
                mAppHook.LogD("Best converter is " + applicableConverters[0].Label +
                    ", rating=" + applicableConverters[0].Applic);
            }
            int newIndex = 0;
            foreach (var conv in applicableConverters)
            {
                if (conv.Applic == Converter.Applicability.NotSelectable)
                {
                    continue;
                }
                ConverterComboItem item = new ConverterComboItem(conv.Label, conv);
                ConverterList.Add(item);
                if ((!fileChanged || mPendingHistoryRestore != null) &&
                        conv.Tag == oldTag)
                {
                    newIndex = ConverterList.Count - 1;
                }
            }
            // Setting SelectedConverterIndex triggers ConvComboBox_SelectionChanged in the
            // code-behind, which calls ConfigureControls() then OnConverterSelected().
            SelectedConverterIndex = newIndex;
        }

        WindowTitle = entry.FileName + " - File Viewer";
        UpdateFindControls();
    }

    private void CloseStreams()
    {
        mDataFork?.Close();
        mDataFork = null;
        mRsrcFork?.Close();
        mRsrcFork = null;
    }

    // -----------------------------------------------------------------------------------------
    // Converter selection (called by code-behind after ConfigureControls)

    public void OnConverterSelected()
    {
        FormatFile();
        if (mPendingHistoryRestore != null && IsTabEnabled(mPendingHistoryRestore.SelectedTab)) {
            SelectedForkTab = mPendingHistoryRestore.SelectedTab;
        }
        UpdateCurrentHistoryEntrySnapshot();
        mPendingHistoryRestore = null;
        mIsRestoringHistory = false;
    }

    private void FormatFile()
    {
        if (SelectedConverterIndex < 0 || SelectedConverterIndex >= ConverterList.Count)
        {
            return;
        }
        ConverterComboItem item = ConverterList[SelectedConverterIndex];
        DateTime startWhen = DateTime.Now;
        try
        {
            mCurDataOutput = item.Converter.ConvertFile(mConvOptions);
        }
        catch (ObjectDisposedException)
        {
            // File descriptor was invalidated (e.g. sector editor wrote to this file's blocks).
            // Reload on the next UI cycle to avoid re-entrant ShowFile→FormatFile calls.
            // The lambda is fully guarded: FileAttribs(entry) in ShowFile runs before that
            // method's own try-catch, so any exception there would otherwise crash the app.
            Dispatcher.UIThread.Post(() => {
                try {
                    // Replace the stale IFileEntry so FileAttribs() doesn't throw on it.
                    string path = (mCurIndex >= 0 && mCurIndex < mSelectedPaths.Count)
                        ? mSelectedPaths[mCurIndex] : string.Empty;
                    if (!string.IsNullOrEmpty(path)) {
                        try {
                            if (TryResolveEntry(mArchiveOrFileSystem, path, out IFileEntry fresh)) {
                                mSelected[mCurIndex] = fresh;
                            }
                        } catch (Exception ex) {
                            AppLog.W("File viewer: entry refresh failed", ex);
                        }
                    }
                    ShowFile(false);
                } catch (Exception ex) {
                    AppLog.E("File viewer: reload after stale descriptor failed", ex);
                    mCurDataOutput = new SimpleText("File data changed; reload failed: " + ex.Message);
                    DataTextViewModel.SetFromConvOutput(mCurDataOutput);
                    mDataDisplayType = DisplayItemType.SimpleText;
                    IsTextVisible = true;
                    IsBitmapVisible = false;
                }
            });
            return;
        }
        catch (Exception ex)
        {
            if (ex is BadBlockException) {
                AppLog.W("File viewer: bad disk block encountered", ex);
                mCurDataOutput = new SimpleText("Error: bad disk block encountered: " + ex.Message);
            } else {
                AppLog.E("File viewer: converter '" + item.Converter.GetType().Name + "' crashed", ex);
                mCurDataOutput = new SimpleText("Error: converter (" + item.Converter.GetType().Name +
                    ") crashed:\r\n" + ex);
            }
        }
        mCurRsrcOutput = item.Converter.FormatResources(mConvOptions);
        mAppHook.LogD(item.Converter.Label + " conv took " +
            (DateTime.Now - startWhen).TotalMilliseconds + " ms");

        // Preserve converter-generated notes before any display-only output substitutions.
        Notes comboNotes = new Notes();
        if (mCurDataOutput?.Notes.Count > 0)
        {
            comboNotes.MergeFrom(mCurDataOutput.Notes);
        }
        if (mCurRsrcOutput?.Notes.Count > 0)
        {
            comboNotes.MergeFrom(mCurRsrcOutput.Notes);
        }

        IsFindEnabled = IsExportEnabled = false;
        mDataDisplayType = DisplayItemType.Unknown;
        mDataForkText = string.Empty;
        mRsrcForkText = string.Empty;

        if (mCurDataOutput is ErrorText text)
        {
            mDataForkText = text.Text.ToString();
            mDataDisplayType = DisplayItemType.SimpleText;
        }
        else if (mCurDataOutput is FancyText { PreferSimple: false } fancy)
        {
            StringBuilder sb = fancy.Text;
            if (sb.Length > MAX_FANCY_TEXT)
            {
                int oldLen = sb.Length;
                sb.Clear();
                sb.Append("[ output too long for viewer (length=");
                sb.Append(oldLen);
                sb.Append(") ]");
            }
            mDataForkText = sb.ToString();
            mDataDisplayType = DisplayItemType.FancyText;
            IsFindEnabled = IsExportEnabled = true;
        }
        else if (mCurDataOutput is SimpleText simpleText)
        {
            StringBuilder sb = simpleText.Text;
            if (sb.Length > MAX_SIMPLE_TEXT)
            {
                int oldLen = sb.Length;
                sb.Clear();
                sb.Append("[ output too long for viewer (length=");
                sb.Append(oldLen);
                sb.Append(") ]");
            }
            mDataForkText = sb.ToString();
            mDataDisplayType = DisplayItemType.SimpleText;
            IsFindEnabled = IsExportEnabled = true;
        }
        else if (mCurDataOutput is CellGrid cellGrid)
        {
            StringBuilder sb = new StringBuilder();
            CSVGenerator.GenerateString(cellGrid, false, sb);
            if (sb.Length > MAX_SIMPLE_TEXT)
            {
                int oldLen = sb.Length;
                sb.Clear();
                sb.Append("[ output too long for viewer (length=");
                sb.Append(oldLen);
                sb.Append(") ]");
            }
            // Replace the output with SimpleText so code-behind uses DataForkText
            mDataForkText = sb.ToString();
            mCurDataOutput = new SimpleText(mDataForkText);
            mDataDisplayType = DisplayItemType.SimpleText;
            IsFindEnabled = IsExportEnabled = true;
        }
        else if (mCurDataOutput is IBitmap)
        {
            ConfigureMagnification();
            mDataDisplayType = DisplayItemType.Bitmap;
            IsExportEnabled = true;
        }
        else if (mCurDataOutput is HostConv hostConv)
        {
            HostConv.FileKind kind = hostConv.Kind;
            switch (kind)
            {
                case HostConv.FileKind.GIF:
                case HostConv.FileKind.JPEG:
                case HostConv.FileKind.PNG:
                    Bitmap? bmp = PrepareHostImage(mDataFork);
                    if (bmp == null)
                    {
                        string errMsg = "Unable to decode " + kind + " image.";
                        mDataForkText = errMsg;
                        mCurDataOutput = new SimpleText(errMsg);
                        mDataDisplayType = DisplayItemType.SimpleText;
                    }
                    else
                    {
                        PreviewBitmap = bmp;
                        ConfigureMagnification();
                        mDataDisplayType = DisplayItemType.Bitmap;
                    }
                    break;
                case HostConv.FileKind.PDF:
                case HostConv.FileKind.RTF:
                case HostConv.FileKind.Word:
                    string extMsg = LaunchExternalViewer(mDataFork, kind)
                        ? "(Displaying with external command)"
                        : "Failed to launch external viewer";
                    mDataForkText = extMsg;
                    mCurDataOutput = new SimpleText(extMsg);
                    mDataDisplayType = DisplayItemType.SimpleText;
                    break;
            }
        }
        else
        {
            Debug.Assert(false, "unknown IConvOutput impl " + mCurDataOutput);
        }

        IsTextVisible = (mDataDisplayType == DisplayItemType.SimpleText ||
            mDataDisplayType == DisplayItemType.FancyText);
        IsBitmapVisible = (mDataDisplayType == DisplayItemType.Bitmap);

        IsDataTabEnabled = (mDataFork != null);
        // Disable data tab if it has empty plain text and a resource fork is available
        if (IsDataTabEnabled && mCurRsrcOutput != null &&
                mDataDisplayType == DisplayItemType.SimpleText &&
                mDataForkText.Length == 0)
        {
            IsDataTabEnabled = false;
        }

        // Resource fork
        if (mCurRsrcOutput == null)
        {
            IsRsrcTabEnabled = false;
            mRsrcForkText = string.Empty;
        }
        else
        {
            IsRsrcTabEnabled = true;
            mRsrcForkText = ((SimpleText)mCurRsrcOutput).Text.ToString();
        }

        // Notes
        if (comboNotes.Count > 0)
        {
            mNoteTextStr = comboNotes.ToString();
            IsNoteTabEnabled = true;
        }
        else
        {
            mNoteTextStr = string.Empty;
            IsNoteTabEnabled = false;
        }

        DataTextViewModel.SetFromConvOutput(mCurDataOutput);
        RsrcTextViewModel.SetFromConvOutput(mCurRsrcOutput);
        NoteText = mNoteTextStr;

        SelectEnabledTab();
        mFindCaretOffset = 0;
        UpdateFindControls();
    }

    private void SelectEnabledTab()
    {
        Tab current = SelectedForkTab;
        if ((current == Tab.Data && IsDataTabEnabled) ||
                (current == Tab.Rsrc && IsRsrcTabEnabled) ||
                (current == Tab.Note && IsNoteTabEnabled))
        {
            IsOptionsBoxEnabled = (current == Tab.Data);
            return;
        }
        if (IsDataTabEnabled)
        {
            SelectedForkTab = Tab.Data;
        }
        else if (IsRsrcTabEnabled)
        {
            SelectedForkTab = Tab.Rsrc;
        }
        else
        {
            SelectedForkTab = Tab.Note;
        }
    }

    // -----------------------------------------------------------------------------------------
    // Magnification

    private void ConfigureMagnification()
    {
        int tick = (int)MagnificationTick;
        double mult = (tick == 0) ? 0.5 : tick;

        if (mCurDataOutput is HostConv)
        {
            GraphicsZoomStr = (tick == 0 ? "½" : tick.ToString()) + "×";
            Bitmap? bmp = PreviewBitmap;
            if (bmp == null) return;
            PreviewImageWidth = Math.Floor(bmp.PixelSize.Width * mult);
            PreviewImageHeight = Math.Floor(bmp.PixelSize.Height * mult);
            return;
        }
        if (mCurDataOutput is not IBitmap ibm) return;

        int maxScale = Math.Min(MAX_BITMAP_SCALE_DIMENSION / ibm.Width,
            MAX_BITMAP_SCALE_DIMENSION / ibm.Height);
        if (maxScale < 1)
        {
            maxScale = 1;
        }
        if (tick > maxScale)
        {
            tick = maxScale;
            mult = tick;
            // Clamp the UI state so subsequent zoom actions remain in a valid range.
            SetProperty(ref mMagnificationTick, tick);
        }
        GraphicsZoomStr = (tick == 0 ? "½" : tick.ToString()) + "×";

        switch (mult)
        {
            case < 1.0:
                PreviewBitmap = BitmapUtil.ConvertToBitmap(ibm);
                PreviewImageWidth = Math.Floor(ibm.Width * mult);
                PreviewImageHeight = Math.Floor(ibm.Height * mult);
                break;
            case 1.0:
                PreviewBitmap = BitmapUtil.ConvertToBitmap(ibm);
                PreviewImageWidth = ibm.Width;
                PreviewImageHeight = ibm.Height;
                break;
            default:
            {
                IBitmap scaled = ibm.ScaleUp((int)mult);
                PreviewBitmap = BitmapUtil.ConvertToBitmap(scaled);
                PreviewImageWidth = scaled.Width;
                PreviewImageHeight = scaled.Height;
                break;
            }
        }
    }

    private static Bitmap? PrepareHostImage(Stream? stream)
    {
        if (stream == null) return null;
        stream.Position = 0;
        MemoryStream tmpStream = new MemoryStream();
        stream.CopyTo(tmpStream);
        tmpStream.Position = 0;
        try
        {
            return new Bitmap(tmpStream);
        }
        catch (Exception ex)
        {
            AppLog.W("File viewer: bitmap decode failed", ex);
            return null;
        }
    }

    // -----------------------------------------------------------------------------------------
    // External viewer and temp files

    private bool LaunchExternalViewer(Stream? stream, HostConv.FileKind kind)
    {
        if (stream == null) return false;
        string ext = kind switch
        {
            HostConv.FileKind.GIF => ".gif",
            HostConv.FileKind.JPEG => ".jpg",
            HostConv.FileKind.PNG => ".png",
            HostConv.FileKind.PDF => ".pdf",
            HostConv.FileKind.RTF => ".rtf",
            HostConv.FileKind.Word => ".doc",
            _ => string.Empty
        };
        if (string.IsNullOrEmpty(ext))
        {
            Debug.Assert(false, "Unhandled kind: " + kind);
            return false;
        }
        string tmpFileName = Path.GetTempPath() + TEMP_FILE_PREFIX +
            Guid.NewGuid().ToString() + ext;
        try
        {
            using (Stream tmpFile = new FileStream(tmpFileName, FileMode.Create))
            {
                stream.Position = 0;
                stream.CopyTo(tmpFile);
            }
            mAppHook.LogI("Created temp file '" + tmpFileName + "'");
            mTmpFiles.Add(tmpFileName);
        }
        catch (IOException ex)
        {
            mAppHook.LogW("Failed to create temp file '" + tmpFileName + "': " + ex.Message);
            return false;
        }
        try
        {
            Process proc = new Process();
            proc.StartInfo = new ProcessStartInfo(tmpFileName)
            {
                UseShellExecute = true
            };
            return proc.Start();
        }
        catch (Exception ex)
        {
            mAppHook.LogW("Failed to launch external viewer: " + ex.Message);
            return false;
        }
    }

    private void DeleteTempFiles()
    {
        foreach (string path in mTmpFiles)
        {
            try
            {
                File.Delete(path);
                mAppHook.LogI("Removed temp '" + path + "'");
            }
            catch (Exception ex)
            {
                mAppHook.LogW("Unable to remove temp: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// Scans for stale temporary files left by previous viewer sessions.
    /// </summary>
    public static List<string> FindStaleTempFiles()
    {
        List<string> staleTemps = new List<string>();
        string pattern = TEMP_FILE_PREFIX + "*";
        string[] allFiles = Directory.GetFiles(Path.GetTempPath(), pattern);
        foreach (string path in allFiles)
        {
            try
            {
                using FileStream _ = new FileStream(path, FileMode.Open,
                    FileAccess.Read, FileShare.None);
                staleTemps.Add(path);
            }
            catch (Exception ex)
            {
                // Can't open = can't delete (likely in use by another process); skip.
                AppLog.D("File viewer: skipping in-use temp file '" + path + "': " + ex.Message);
            }
        }
        return staleTemps;
    }

    // -----------------------------------------------------------------------------------------
    // Export and Copy

    private async Task DoExportAsync()
    {
        IConvOutput? convOut;
        string prefix;
        if (mCurRsrcOutput != null && SelectedForkTab == Tab.Rsrc)
        {
            convOut = mCurRsrcOutput;
            prefix = ".r";
        }
        else
        {
            convOut = mCurDataOutput;
            prefix = "";
        }
        if (convOut == null) return;

        string ext;
        List<FilePickerFileType> fileTypes;
        switch (convOut)
        {
            case FancyText { PreferSimple: false }:
                ext = prefix + RTFGenerator.FILE_EXT;
                fileTypes =
                [
                    new FilePickerFileType("RTF Document") { Patterns = ["*.rtf"] },
                    FilePickerFileTypes.All
                ];
                break;
            case SimpleText:
                ext = prefix + TXTGenerator.FILE_EXT;
                fileTypes =
                [
                    new FilePickerFileType("Text File") { Patterns = ["*.txt"] },
                    FilePickerFileTypes.All
                ];
                break;
            case CellGrid:
                ext = prefix + CSVGenerator.FILE_EXT;
                fileTypes =
                [
                    new FilePickerFileType("CSV File") { Patterns = ["*.csv"] },
                    FilePickerFileTypes.All
                ];
                break;
            case IBitmap:
                ext = prefix + PNGGenerator.FILE_EXT;
                fileTypes =
                [
                    new FilePickerFileType("PNG Image") { Patterns = ["*.png"] },
                    FilePickerFileTypes.All
                ];
                break;
            default:
                Debug.Assert(false, "not handling " + convOut.GetType().Name);
                return;
        }

        string fileName = (mSelected.Count > 0) ? mSelected[mCurIndex].FileName : "export";
        if (!fileName.ToLowerInvariant().EndsWith(ext))
        {
            fileName += ext;
        }

        string? path = await _filePickerService.SaveFileAsync("Export File...", fileName,
            fileTypes);
        if (path == null) return;

        try
        {
            using (Stream outStream = new FileStream(path, FileMode.Create))
            {
                CopyViewToStream(convOut, outStream);
            }
        }
        catch (Exception ex)
        {
            AppLog.E("File viewer: export failed", ex);
        }
    }

    private void CopyViewToStream(IConvOutput convOut, Stream outStream)
    {
        switch (convOut)
        {
            case FancyText { PreferSimple: false } @out:
                RTFGenerator.Generate(@out, outStream);
                break;
            case SimpleText text:
            {
                using var sw = new StreamWriter(outStream, leaveOpen: true);
                sw.Write(text.Text);
                break;
            }
            case CellGrid @out:
            {
                var sb = new StringBuilder();
                CSVGenerator.GenerateString(@out, false, sb);
                using StreamWriter sw = new StreamWriter(outStream, leaveOpen: true);
                sw.Write(sb);
                break;
            }
            case IBitmap @out:
                PNGGenerator.Generate(@out, outStream);
                break;
            default:
                throw new NotImplementedException("Can't export " + convOut.GetType().Name);
        }
    }

    private async Task DoCopyAsync()
    {
        if (SelectedForkTab == Tab.Note)
        {
            await _clipboardService.SetTextAsync(mNoteTextStr);
            return;
        }
        IConvOutput? convOut = (mCurRsrcOutput != null && SelectedForkTab == Tab.Rsrc)
            ? mCurRsrcOutput : mCurDataOutput;
        if (convOut == null) return;

        try
        {
            switch (convOut)
            {
                case FancyText { PreferSimple: false } text:
                    await _clipboardService.SetTextAsync(text.Text.ToString());
                    break;
                case SimpleText text:
                    await _clipboardService.SetTextAsync(text.Text.ToString());
                    break;
                case CellGrid @out:
                {
                    var sb = new StringBuilder();
                    CSVGenerator.GenerateString(@out, false, sb);
                    await _clipboardService.SetTextAsync(sb.ToString());
                    break;
                }
                case IBitmap:
                    await _clipboardService.SetTextAsync("[PNG image - copy to file using Export]");
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLog.E("File viewer: copy failed", ex);
        }
    }

    // -----------------------------------------------------------------------------------------
    // Find

    private void UpdateFindControls()
    {
        IsFindButtonsEnabled = IsFindEnabled && !string.IsNullOrEmpty(mSearchString);
    }

    private void DoFind(bool forward)
    {
        if (string.IsNullOrEmpty(SearchString)) return;

        string text = SelectedForkTab switch
        {
            Tab.Rsrc => mRsrcForkText,
            Tab.Note => mNoteTextStr,
            _ => mDataDisplayType == DisplayItemType.Bitmap ? string.Empty : mDataForkText
        };
        if (string.IsNullOrEmpty(text)) return;

        int startPos = forward ? mFindCaretOffset : Math.Max(0, mFindCaretOffset - 1);

        int index;
        if (forward)
        {
            index = text.IndexOf(SearchString, startPos, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                index = text.IndexOf(SearchString, 0, StringComparison.OrdinalIgnoreCase);
            }
        }
        else
        {
            if (startPos < 0) startPos = text.Length - 1;
            index = text.LastIndexOf(SearchString, startPos, StringComparison.OrdinalIgnoreCase);
            if (index < 0 && text.Length > 0)
            {
                index = text.LastIndexOf(SearchString, text.Length - 1,
                    StringComparison.OrdinalIgnoreCase);
            }
        }

        if (index >= 0)
        {
            mFindCaretOffset = index + SearchString.Length;
            FindResultFound?.Invoke((index, SearchString.Length));
        }
    }

    // -----------------------------------------------------------------------------------------
    // Options — called by code-behind ConfigureControls/UpdateOption

    public void BeginConfiguring()
    {
        mIsConfiguring = true;
    }

    public void EndConfiguring()
    {
        mIsConfiguring = false;
    }

    public Dictionary<string, string> GetConvOptions() => mConvOptions;

    public void SetConvOptions(Dictionary<string, string> options)
    {
        mConvOptions = options;
        UpdateCurrentHistoryEntrySnapshot();
    }

    public void SetConvOption(string tag, string newValue)
    {
        if (mIsConfiguring) return;
        if (SelectedConverterIndex < 0 || SelectedConverterIndex >= ConverterList.Count) return;

        ConverterComboItem item = ConverterList[SelectedConverterIndex];
        Converter.OptionDefinition? optDef = item.Converter.OptionDefs.Find(def => def.OptTag == tag);
        if (optDef == null)
        {
            return;
        }
        if (!string.IsNullOrEmpty(newValue) && !IsValidOptionValue(optDef, newValue))
        {
            return;
        }

        string settingKey = AppSettings.VIEW_SETTING_PREFIX + item.Converter.Tag;

        if (string.IsNullOrEmpty(newValue))
        {
            mConvOptions.Remove(tag);
        }
        else
        {
            mConvOptions[tag] = newValue;
        }
        string optStr = ConvConfig.GenerateOptString(mConvOptions);
        IsSaveDefaultsEnabled = !_settingsService.GetString(settingKey, string.Empty)
            .Equals(optStr, StringComparison.InvariantCultureIgnoreCase);
        FormatFile();
        UpdateCurrentHistoryEntrySnapshot();
    }

    private static bool IsValidOptionValue(Converter.OptionDefinition optDef, string value)
    {
        switch (optDef.Type)
        {
            case Converter.OptionDefinition.OptType.Boolean:
                return bool.TryParse(value, out _);
            case Converter.OptionDefinition.OptType.Multi:
                return optDef.MultiTags != null &&
                    Array.IndexOf(optDef.MultiTags, value) >= 0;
            case Converter.OptionDefinition.OptType.IntValue:
                return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture,
                    out int intValue) && intValue > 0;
            default:
                return false;
        }
    }

    private void DoSaveDefaults()
    {
        if (SelectedConverterIndex < 0 || SelectedConverterIndex >= ConverterList.Count) return;
        ConverterComboItem item = ConverterList[SelectedConverterIndex];
        string optStr = ConvConfig.GenerateOptString(mConvOptions);
        string settingKey = AppSettings.VIEW_SETTING_PREFIX + item.Converter.Tag;
        _settingsService.SetString(settingKey, optStr);
        IsSaveDefaultsEnabled = false;
    }

    public bool TryConsumePendingConverterOptions(string converterTag,
            out Dictionary<string, string> options)
    {
        if (mPendingHistoryRestore != null && mPendingHistoryRestore.ConverterTag == converterTag) {
            options = CloneOptions(mPendingHistoryRestore.ConverterOptions);
            return true;
        }

        options = new Dictionary<string, string>();
        return false;
    }

    private void AppendCurrentHistoryEntry(bool clearForward)
    {
        HistoryEntry snapshot = CreateHistoryEntry();

        if (mHistoryIndex < 0 || mHistory.Count == 0) {
            mHistory.Clear();
            mHistory.Add(snapshot);
            mHistoryIndex = 0;
            UpdateNavigationControls();
            return;
        }

        if (clearForward && mHistoryIndex < mHistory.Count - 1) {
            mHistory.RemoveRange(mHistoryIndex + 1, mHistory.Count - (mHistoryIndex + 1));
        }
        if (HistoryEntriesEqual(mHistory[mHistoryIndex], snapshot)) {
            mHistory[mHistoryIndex] = snapshot;
            UpdateNavigationControls();
            return;
        }
        if (mHistoryIndex == mHistory.Count - 1 &&
                HistoryEntriesEqual(mHistory[^1], snapshot)) {
            UpdateNavigationControls();
            return;
        }

        mHistory.Add(snapshot);
        mHistoryIndex = mHistory.Count - 1;
        UpdateNavigationControls();
    }

    private void ReplaceCurrentHistoryEntry()
    {
        if (mHistoryIndex < 0 || mHistory.Count == 0) {
            AppendCurrentHistoryEntry(clearForward: false);
            return;
        }

        mHistory[mHistoryIndex] = CreateHistoryEntry();
        PruneAdjacentDuplicateHistory();
        UpdateNavigationControls();
    }

    private void UpdateCurrentHistoryEntrySnapshot()
    {
        if (mIsRestoringHistory || mHistoryIndex < 0 || mHistoryIndex >= mHistory.Count ||
                mSelectedPaths.Count == 0) {
            return;
        }
        mHistory[mHistoryIndex] = CreateHistoryEntry();
        UpdateNavigationControls();
    }

    private HistoryEntry CreateHistoryEntry()
    {
        string currentPath = (mCurIndex >= 0 && mCurIndex < mSelectedPaths.Count)
            ? mSelectedPaths[mCurIndex] : string.Empty;
        return new HistoryEntry {
            CurrentPath = currentPath,
            SelectedPaths = new List<string>(mSelectedPaths),
            CurrentIndex = mCurIndex,
            SourceWorkPathName = SourceWorkPathName,
            ConverterTag = GetCurrentConverterTag(),
            ConverterOptions = CloneOptions(mConvOptions),
            SelectedTab = SelectedForkTab
        };
    }

    private bool RestoreHistoryEntry(int historyIndex)
    {
        if (historyIndex < 0 || historyIndex >= mHistory.Count) {
            return false;
        }
        if (!TryResolveSelectedPaths(mHistory[historyIndex].SelectedPaths,
                mHistory[historyIndex].CurrentPath, out List<IFileEntry> entries,
                out List<string> paths, out int currentIndex)) {
            return false;
        }

        CloseStreams();
        mSelected.Clear();
        mSelected.AddRange(entries);
        mSelectedPaths.Clear();
        mSelectedPaths.AddRange(paths);
        mCurIndex = currentIndex;
        mHistoryIndex = historyIndex;
        mPendingHistoryRestore = mHistory[historyIndex];
        mIsRestoringHistory = true;
        UpdateNavigationControls();
        ShowFileDeferred(false);
        return true;
    }

    private bool TryRebuildHistoryEntry(HistoryEntry entry, out HistoryEntry rebuilt)
    {
        if (entry.SourceWorkPathName != SourceWorkPathName ||
                !TryResolveSelectedPaths(entry.SelectedPaths, entry.CurrentPath,
                    out List<IFileEntry> _, out List<string> paths, out int currentIndex)) {
            rebuilt = null!;
            return false;
        }

        rebuilt = new HistoryEntry {
            CurrentPath = paths[currentIndex],
            SelectedPaths = paths,
            CurrentIndex = currentIndex,
            SourceWorkPathName = entry.SourceWorkPathName,
            ConverterTag = entry.ConverterTag,
            ConverterOptions = CloneOptions(entry.ConverterOptions),
            SelectedTab = entry.SelectedTab
        };
        return true;
    }

    private bool TryResolveSelectedPaths(List<string> selectedPaths, string currentPath,
            out List<IFileEntry> entries, out List<string> resolvedPaths, out int currentIndex)
    {
        entries = new List<IFileEntry>();
        resolvedPaths = new List<string>();
        currentIndex = -1;

        for (int i = 0; i < selectedPaths.Count; i++) {
            if (!TryResolveEntry(mArchiveOrFileSystem, selectedPaths[i], out IFileEntry entry)) {
                continue;
            }
            entries.Add(entry);
            resolvedPaths.Add(entry.FullPathName);
            if (selectedPaths[i] == currentPath) {
                currentIndex = resolvedPaths.Count - 1;
            }
        }

        return currentIndex >= 0 && entries.Count > 0;
    }

    private int FindFallbackHistoryIndex(int preferredStart)
    {
        if (mHistory.Count == 0) return -1;
        return Math.Min(preferredStart, mHistory.Count - 1);
    }

    private void PruneAdjacentDuplicateHistory()
    {
        if (mHistoryIndex > 0 && HistoryEntriesEqual(mHistory[mHistoryIndex - 1], mHistory[mHistoryIndex])) {
            mHistory.RemoveAt(mHistoryIndex);
            mHistoryIndex--;
        }
        if (mHistoryIndex >= 0 && mHistoryIndex < mHistory.Count - 1 &&
                HistoryEntriesEqual(mHistory[mHistoryIndex], mHistory[mHistoryIndex + 1])) {
            mHistory.RemoveAt(mHistoryIndex + 1);
        }
    }

    private static bool HistoryEntriesEqual(HistoryEntry left, HistoryEntry right) =>
        left.CurrentPath == right.CurrentPath &&
        left.SourceWorkPathName == right.SourceWorkPathName &&
        left.CurrentIndex == right.CurrentIndex &&
        left.ConverterTag == right.ConverterTag &&
        left.SelectedTab == right.SelectedTab &&
        left.SelectedPaths.SequenceEqual(right.SelectedPaths) &&
        DictionariesEqual(left.ConverterOptions, right.ConverterOptions);

    private static bool DictionariesEqual(Dictionary<string, string> left,
            Dictionary<string, string> right) =>
        left.Count == right.Count &&
        left.OrderBy(kvp => kvp.Key).SequenceEqual(right.OrderBy(kvp => kvp.Key));

    private static Dictionary<string, string> CloneOptions(Dictionary<string, string> options) =>
        options.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

    private bool IsTabEnabled(Tab tab) => tab switch {
        Tab.Data => IsDataTabEnabled,
        Tab.Rsrc => IsRsrcTabEnabled,
        Tab.Note => IsNoteTabEnabled,
        _ => false
    };

    private string GetCurrentConverterTag() =>
        SelectedConverterIndex >= 0 && SelectedConverterIndex < ConverterList.Count
            ? ConverterList[SelectedConverterIndex].Converter.Tag : string.Empty;

    // -----------------------------------------------------------------------------------------
    // Dispose

    public void Dispose()
    {
        _viewerService.Unregister(this);
        CloseStreams();
        DeleteTempFiles();
    }

    private static bool TryResolveEntry(object archiveOrFileSystem, string fullPathName,
            out IFileEntry entry)
    {
        switch (archiveOrFileSystem)
        {
            case IFileSystem fileSystem:
                try
                {
                    entry = fileSystem.EntryFromPath(fullPathName, fileSystem.Characteristics.DirSep);
                    return true;
                }
                catch (FileNotFoundException)
                {
                    break;
                }
            case IArchive archive when archive.Characteristics.DefaultDirSep != IFileEntry.NO_DIR_SEP:
                try
                {
                    entry = archive.FindFileEntry(fullPathName, archive.Characteristics.DefaultDirSep);
                    return true;
                }
                catch (FileNotFoundException)
                {
                    break;
                }
            case IArchive archive:
                if (archive.TryFindFileEntry(fullPathName, out entry))
                {
                    return true;
                }
                break;
        }

        entry = IFileEntry.NO_ENTRY;
        return false;
    }
}