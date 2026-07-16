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
namespace cp2_avalonia.Views;

using System.Collections.Generic;
using Models;

/// <summary>
/// Interface for view-level operations that cannot be achieved through
/// data binding alone (scroll, focus, cursor, native menu, multi-select).
/// Implemented by MainWindow; passed to MainViewModel at construction.
/// </summary>
public interface IViewActions
{
    // --- Scroll/Focus (genuinely view-level; cannot be done via bindings) ---
    void ScrollFileListTo(object item);
    void ScrollFileListToTop();
    void ScrollDirectoryTreeToTop();
    void FocusFileList();
    void SetFileListSelectionFocus(int index);

    // --- Multi-select (DataGrid multi-select is not bindable in Avalonia) ---
    void SelectAllFileListItems();
    void SetFileListSelection(IList<FileListItem> items);

    // --- Toast/Notification (timer animation requires imperative control) ---
    void ShowToast(string message, bool success);

    // --- Cursor (direct Window.Cursor manipulation) ---
    void SetCursorBusy(bool busy);

    // --- Archive/Directory tree selection (needed by ArchiveTreeItem.SelectItem refactor) ---
    void SelectArchiveTreeItem(ArchiveTreeItem item);

    /// <summary>Selects the "best" descendant of <paramref name="root"/> in the archive tree.</summary>
    void SelectBestArchiveTreeItem(ArchiveTreeItem root);

    /// <summary>Sets the directory tree's selected item directly.</summary>
    void SelectDirectoryTreeItem(DirectoryTreeItem? item);

    /// <summary>
    /// Finds the directory-tree entry matching <paramref name="dirEntry"/> and selects it.
    /// Returns true if found.
    /// </summary>
    bool SelectDirectoryTreeItemByEntry(
        System.Collections.ObjectModel.ObservableCollection<DirectoryTreeItem> root,
        DiskArc.IFileEntry dirEntry);

    /// <summary>Scrolls <paramref name="item"/> into view in the file list DataGrid.</summary>
    void ScrollFileListItemIntoView(FileListItem item);

    /// <summary>
    /// Sets the file-list selection to the item whose FileEntry matches
    /// <paramref name="selEntry"/> and scrolls it into view.
    /// </summary>
    void SelectFileListItemByEntry(DiskArc.IFileEntry selEntry);

    /// <summary>Re-applies the last user-chosen column sort after file-list repopulation.</summary>
    void ReapplyFileListSort();

    /// <summary>
    /// Sizes the visible Filename/Pathname column to fit the widest value in the entire
    /// file-list model, so the column doesn't grow as rows scroll into view under
    /// virtualization.  Does nothing once the user has manually resized that column.
    /// </summary>
    void PreSizeNameColumn();

    // --- Recent files menu (native platform menu construction) ---
    void PopulateRecentFilesMenu();

    /// <summary>Resets file-list sort state (clears column sort tags and tracked sort column).</summary>
    void ResetFileListSort();

    /// <summary>Scrolls a directory tree item into view without changing the selection.</summary>
    void ScrollDirectoryTreeItemIntoView(DirectoryTreeItem item);

    /// <summary>Moves keyboard focus to the directory tree.</summary>
    void FocusDirectoryTree();

    /// <summary>Moves keyboard focus to the archive tree.</summary>
    void FocusArchiveTree();

    /// <summary>
    /// Moves keyboard focus to the directory tree after a short delay, so it wins over the
    /// focus the file-list DataGrid grabs asynchronously while repopulating.
    /// </summary>
    void FocusDirectoryTreeDeferred();

    /// <summary>Opens (or re-focuses) the ASCII Chart window.</summary>
    void ShowAsciiChart();
}
