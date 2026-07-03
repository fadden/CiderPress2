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
using System.ComponentModel;
using DiskArc;
using cp2_avalonia.Models;

using CommunityToolkit.Mvvm.ComponentModel;

namespace cp2_avalonia.ViewModels;

/// <summary>
/// ViewModel for the directory tree (left panel, bottom).
/// Owns the root collection, selection tracking, and population/verification logic.
/// </summary>
public class DirectoryTreeViewModel : ObservableObject, IDisposable {
    /// <summary>
    /// Root collection for the directory tree control.
    /// </summary>
    public ObservableCollection<DirectoryTreeItem> TreeRoot { get; } = [];

    private DirectoryTreeItem? mSelectedItem;
    /// <summary>
    /// Currently selected directory tree item. Setting this also updates
    /// IsSelected on the item and clears the previous item's IsSelected.
    /// </summary>
    public DirectoryTreeItem? SelectedItem {
        get => mSelectedItem;
        set {
            if (mSelectedItem == value) {
                return;
            }
            if (mSelectedItem != null) {
                mSelectedItem.IsSelected = false;
            }
            SetProperty(ref mSelectedItem, value);
            if (mSelectedItem != null) {
                mSelectedItem.IsSelected = true;
                CachedSelectedItem = mSelectedItem;
            }
        }
    }

    /// <summary>
    /// Retains the last non-null SelectedItem. Used by cross-VM coordination
    /// methods (SyncDirectoryTreeToFileSelection, NavToParent, etc.).
    /// Reset to null on workspace close.
    /// </summary>
    public DirectoryTreeItem? CachedSelectedItem { get; private set; }

    /// <summary>
    /// Resets the cached selection. Call on workspace close.
    /// </summary>
    public void ResetCache() {
        CachedSelectedItem = null;
    }

    // Tracks (item, handler) pairs so they can be unwired on Clear/Dispose.
    private readonly List<(DirectoryTreeItem item, PropertyChangedEventHandler handler)>
        mItemHandlers = new();

    /// <summary>
    /// Subscribes to IsSelected changes on the given item and all its descendants.
    /// Call after populating TreeRoot or expanding a subtree.
    /// </summary>
    public void SubscribeToSelectionChanges(DirectoryTreeItem item) {
        DirectoryTreeItem captured = item;
        PropertyChangedEventHandler handler = (s, e) => {
            if (e.PropertyName == nameof(DirectoryTreeItem.IsSelected) && captured.IsSelected) {
                SelectedItem = captured;
            }
        };
        item.PropertyChanged += handler;
        mItemHandlers.Add((item, handler));

        foreach (DirectoryTreeItem child in item.Items) {
            SubscribeToSelectionChanges(child);
        }
    }

    /// <summary>
    /// Subscribes to all items in TreeRoot. Call after populating the directory tree.
    /// </summary>
    public void SubscribeAllSelectionChanges() {
        // Unwire previous handlers before rebuilding.
        foreach (var (item, handler) in mItemHandlers) {
            item.PropertyChanged -= handler;
        }
        mItemHandlers.Clear();
        foreach (DirectoryTreeItem root in TreeRoot) {
            SubscribeToSelectionChanges(root);
        }
    }

    /// <summary>
    /// Recursively populates the directory tree from an IFileSystem root entry.
    /// </summary>
    public static void PopulateTree(DirectoryTreeItem? parent,
            ObservableCollection<DirectoryTreeItem> tvRoot, IFileEntry dirEntry) {
        DirectoryTreeItem newItem = new DirectoryTreeItem(parent, dirEntry.FileName, dirEntry);
        tvRoot.Add(newItem);
        foreach (IFileEntry entry in dirEntry) {
            if (entry.IsDirectory) {
                PopulateTree(newItem, newItem.Items, entry);
            }
        }
    }

    /// <summary>
    /// Verifies that the directory tree matches the file system's actual structure.
    /// Returns false if the tree needs to be repopulated.
    /// </summary>
    public static bool VerifyTree(ObservableCollection<DirectoryTreeItem> tvRoot,
            IFileEntry dirEntry, int index) {
        if (index >= tvRoot.Count) {
            return false;
        }
        DirectoryTreeItem item = tvRoot[index];
        if (item.FileEntry != dirEntry) {
            return false;
        }
        int childIndex = 0;
        foreach (IFileEntry entry in dirEntry) {
            if (entry.IsDirectory) {
                if (!VerifyTree(item.Items, entry, childIndex++)) {
                    return false;
                }
            }
        }
        if (childIndex != item.Items.Count) {
            return false;
        }
        return true;
    }

    public void Dispose() {
        foreach (var (item, handler) in mItemHandlers) {
            item.PropertyChanged -= handler;
        }
        mItemHandlers.Clear();
    }
}
