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

using AppCommon;

using CommonUtil;

using DiskArc;
using static DiskArc.Defs;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using cp2_avalonia.Models;
using cp2_avalonia.Services;
using cp2_avalonia.Views;

namespace cp2_avalonia.ViewModels;

/// <summary>
/// ViewModel for the file list (center panel, main area).
/// Owns the file entry collection, selection, column visibility, and
/// entry population/verification logic.
/// </summary>
public class FileListViewModel : ObservableObject {
    private readonly IViewActions mViewActions;
    private readonly ISettingsService _settingsService;
    private readonly AppHook mAppHook;

    public FileListViewModel(IViewActions viewActions, ISettingsService settingsService,
            AppHook appHook) {
        mViewActions = viewActions;
        _settingsService = settingsService;
        mAppHook = appHook;

        ResetSortCommand = new RelayCommand(DoResetSort);
    }

    // ---- File list collection ----

    public ObservableCollection<FileListItem> Items { get; } = new();

    private FileListItem? mSelectedItem;
    public FileListItem? SelectedItem {
        get => mSelectedItem;
        set => SetProperty(ref mSelectedItem, value);
    }

    /// <summary>
    /// Multi-select tracking. DataGrid multi-select is not directly bindable.
    /// Updated via SetSelection() called from view code-behind.
    /// </summary>
    internal IList<FileListItem> Selection { get; private set; } = new List<FileListItem>();

    /// <summary>
    /// Updates the multi-select collection and the primary SelectedItem.
    /// Called from view code-behind when DataGrid selection changes,
    /// via MainViewModel.UpdateFileListSelection().
    /// </summary>
    public void SetSelection(IList<FileListItem> items) {
        Selection = items;
        SelectedItem = items.Count > 0 ? items[0] : null;
    }

    // ---- Sort state ----

    private bool mIsResetSortEnabled;
    public bool IsResetSortEnabled {
        get => mIsResetSortEnabled;
        set => SetProperty(ref mIsResetSortEnabled, value);
    }

    /// <summary>
    /// Resets sort and signals MainViewModel to repopulate. The subscribe
    /// hook in WireChildViewModels fires after this executes.
    /// </summary>
    public IRelayCommand ResetSortCommand { get; }

    /// <summary>Fired when sort is reset; MainViewModel wires this to PopulateFileList.</summary>
    public event Action? SortReset;

    private void DoResetSort() {
        mViewActions.ResetFileListSort();
        IsResetSortEnabled = false;
        SortReset?.Invoke();
    }

    // ---- Column visibility ----

    private bool mShowCol_FileName = true;
    public bool ShowCol_FileName {
        get => mShowCol_FileName;
        set => SetProperty(ref mShowCol_FileName, value);
    }

    private bool mShowCol_PathName;
    public bool ShowCol_PathName {
        get => mShowCol_PathName;
        set => SetProperty(ref mShowCol_PathName, value);
    }

    private bool mShowCol_Format;
    public bool ShowCol_Format {
        get => mShowCol_Format;
        set => SetProperty(ref mShowCol_Format, value);
    }

    private bool mShowCol_RawLen;
    public bool ShowCol_RawLen {
        get => mShowCol_RawLen;
        set => SetProperty(ref mShowCol_RawLen, value);
    }

    private bool mShowCol_RsrcLen;
    public bool ShowCol_RsrcLen {
        get => mShowCol_RsrcLen;
        set => SetProperty(ref mShowCol_RsrcLen, value);
    }

    private bool mShowCol_TotalSize;
    public bool ShowCol_TotalSize {
        get => mShowCol_TotalSize;
        set => SetProperty(ref mShowCol_TotalSize, value);
    }

    // ---- Entry counts (observed by MainViewModel to update StatusBar) ----

    private int mLastDirCount;
    public int LastDirCount {
        get => mLastDirCount;
        set => SetProperty(ref mLastDirCount, value);
    }

    private int mLastFileCount;
    public int LastFileCount {
        get => mLastFileCount;
        set => SetProperty(ref mLastFileCount, value);
    }

    // ---- Focus request flag ----

    private bool mSwitchFocusToFileList;

    /// <summary>
    /// Requests that keyboard focus move to the file list after the next
    /// PopulateFileList() call. Cleared internally after use.
    /// </summary>
    public void RequestFocusAfterPopulate() {
        mSwitchFocusToFileList = true;
    }

    /// <summary>
    /// Returns true if a focus request is pending.
    /// </summary>
    internal bool HasFocusRequest => mSwitchFocusToFileList;

    /// <summary>
    /// Consumes (clears) the pending focus request.
    /// </summary>
    internal void ConsumeFocusRequest() { mSwitchFocusToFileList = false; }

    // =========================================================================
    // Population
    // =========================================================================

    /// <summary>
    /// Populates the file list based on the current work object and directory selection.
    /// </summary>
    /// <param name="currentWorkObject">DiskArc object currently selected in archive tree.</param>
    /// <param name="dirTreeSel">Currently selected directory tree item (may be null).</param>
    /// <param name="selEntry">Entry to pre-select after population.</param>
    /// <param name="focusOnFileList">If true, move keyboard focus to the list after population.</param>
    /// <param name="showSingleDir">If true, show a single directory; otherwise show full disk.</param>
    /// <param name="formatter">Formatter instance for size/date strings.</param>
    public void PopulateFileList(object currentWorkObject, DirectoryTreeItem? dirTreeSel,
            IFileEntry selEntry, bool focusOnFileList, bool showSingleDir,
            Formatter formatter) {
        if (selEntry != IFileEntry.NO_ENTRY) {
            Debug.WriteLine("FileList: Populate current item=" + selEntry.FileName +
                " (focus=" + focusOnFileList + " mSwitch=" + mSwitchFocusToFileList + ")");
        } else {
            Debug.WriteLine("FileList: Populate no selected item" +
                " (focus=" + focusOnFileList + " mSwitch=" + mSwitchFocusToFileList + ")");
        }

        DateTime clearWhen = DateTime.Now;
        Items.Clear();
        DateTime startWhen = DateTime.Now;

        int dirCount = 0;
        int fileCount = 0;
        if (currentWorkObject is IArchive archive) {
            PopulateEntriesFromArchive(archive,
                ref dirCount, ref fileCount, formatter);
        } else if (currentWorkObject is IFileSystem system) {
            if (showSingleDir) {
                if (dirTreeSel != null) {
                    PopulateEntriesFromSingleDir(dirTreeSel.FileEntry,
                        ref dirCount, ref fileCount, formatter);
                } else {
                    Debug.WriteLine("FileList: Can't populate, no dir tree sel");
                }
            } else {
                PopulateEntriesFromFullDisk(
                    system.GetVolDirEntry(),
                    ref dirCount, ref fileCount, formatter);
            }
        } else {
            Debug.Assert(false, "FileList: work object is " + currentWorkObject);
        }

        DateTime endWhen = DateTime.Now;
        Debug.WriteLine("FileList: refresh done in " +
            (endWhen - startWhen).TotalMilliseconds + " ms (clear took " +
            (startWhen - clearWhen).TotalMilliseconds + " ms)");

        if (Items.Count != 0) {
            if (focusOnFileList || mSwitchFocusToFileList) {
                mViewActions.SelectFileListItemByEntry(selEntry);
            }
        }

        if (mSwitchFocusToFileList) {
            Debug.WriteLine("FileList: focus requested");
            mViewActions.FocusFileList();
            mSwitchFocusToFileList = false;
        }

        // Update observable counts so MainViewModel can relay to StatusBar.
        LastDirCount = dirCount;
        LastFileCount = fileCount;

        mViewActions.ReapplyFileListSort();
    }

    private void PopulateEntriesFromArchive(IArchive arc, ref int dirCount,
            ref int fileCount, Formatter formatter) {
        bool macZipMode = _settingsService.GetBool(AppSettings.MAC_ZIP_ENABLED, true);
        foreach (IFileEntry entry in arc) {
            IFileEntry adfEntry = IFileEntry.NO_ENTRY;
            FileAttribs? adfAttrs = null;
            if (macZipMode && arc is DiskArc.Arc.Zip) {
                if (entry.IsMacZipHeader()) {
                    continue;
                }
                if (DiskArc.Arc.Zip.HasMacZipHeader(arc, entry, out adfEntry)) {
                    try {
                        using System.IO.Stream adfStream =
                            ArcTemp.ExtractToTemp(arc, adfEntry, FilePart.DataFork);
                        adfAttrs = new FileAttribs(entry);
                        adfAttrs.GetFromAppleSingle(adfStream, mAppHook);
                    } catch (Exception ex) {
                        Debug.WriteLine("FileList: Unable to get ADF attrs for '" +
                            entry.FullPathName + "': " + ex.Message);
                        adfAttrs = null;
                    }
                }
            }
            if (entry.IsDirectory) {
                dirCount++;
            } else {
                fileCount++;
            }
            Items.Add(new FileListItem(entry, adfEntry, adfAttrs, formatter));
        }
    }

    private void PopulateEntriesFromSingleDir(IFileEntry dirEntry, ref int dirCount,
            ref int fileCount, Formatter formatter) {
        foreach (IFileEntry entry in dirEntry) {
            if (entry.IsDirectory) {
                dirCount++;
            } else {
                fileCount++;
            }
            Items.Add(new FileListItem(entry, formatter));
        }
    }

    private void PopulateEntriesFromFullDisk(IFileEntry curDirEntry, ref int dirCount,
            ref int fileCount, Formatter formatter) {
        foreach (IFileEntry entry in curDirEntry) {
            Items.Add(new FileListItem(entry, formatter));
            if (entry.IsDirectory) {
                dirCount++;
                PopulateEntriesFromFullDisk(entry, ref dirCount, ref fileCount, formatter);
            } else {
                fileCount++;
            }
        }
    }

    // =========================================================================
    // Verification
    // =========================================================================

    /// <summary>
    /// Dispatches to the appropriate VerifyFileList overload based on the work object type.
    /// Returns false if the file list needs to be repopulated.
    /// </summary>
    public bool VerifyForObject(object? workObject, DirectoryTreeItem? dirSel,
            bool showSingleDir) {
        if (workObject is IArchive archive) {
            return VerifyFileList(archive);
        } else if (workObject is IFileSystem system) {
            if (showSingleDir) {
                if (dirSel == null) {
                    Debug.WriteLine("FileList: Can't verify, no dir tree sel");
                    return false;
                }
                return VerifyFileList(dirSel.FileEntry);
            } else {
                return VerifyFileList(system);
            }
        } else if (workObject is IMultiPart ||
                workObject is DiskArc.Multi.Partition ||
                workObject is IDiskImage) {
            Debug.WriteLine("FileList: Skipping re-check for non-FS object");
            return true;
        } else {
            Debug.Assert(false, "FileList: can't verify " + workObject);
            return false;
        }
    }

    private bool VerifyFileList(IArchive arc) {
        if (Items.Count != arc.Count) {
            return false;
        }
        int index = 0;
        foreach (IFileEntry entry in arc) {
            if (Items[index].FileEntry != entry) {
                return false;
            }
            index++;
        }
        return true;
    }

    private bool VerifyFileList(IFileEntry dirEntry) {
        int index = 0;
        bool ok = VerifyFileListR(ref index, dirEntry, false);
        return (ok && index == Items.Count);
    }

    private bool VerifyFileList(IFileSystem fs) {
        int index = 0;
        bool ok = VerifyFileListR(ref index, fs.GetVolDirEntry(), true);
        return (ok && index == Items.Count);
    }

    private bool VerifyFileListR(ref int index, IFileEntry dirEntry, bool doRecurse) {
        if (index >= Items.Count) {
            return false;
        }
        foreach (IFileEntry entry in dirEntry) {
            if (index >= Items.Count || Items[index].FileEntry != entry) {
                return false;
            }
            index++;
            if (doRecurse && entry.IsDirectory) {
                if (!VerifyFileListR(ref index, entry, doRecurse)) {
                    return false;
                }
            }
        }
        return true;
    }
}
