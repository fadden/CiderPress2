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
using cp2_avalonia.Models;

using CommunityToolkit.Mvvm.ComponentModel;

namespace cp2_avalonia.ViewModels;

/// <summary>
/// ViewModel for the archive tree (left panel, top).
/// Owns the root collection, selection tracking, and subscription lifecycle
/// for IsSelected-based selection sync.
/// </summary>
public class ArchiveTreeViewModel : ObservableObject, IDisposable {
    /// <summary>
    /// Root collection for the archive tree control.
    /// </summary>
    public ObservableCollection<ArchiveTreeItem> TreeRoot { get; } = [];

    private ArchiveTreeItem? mSelectedItem;
    /// <summary>
    /// Currently selected archive tree item. Setting this also updates
    /// IsSelected on the item and clears the previous item's IsSelected.
    /// </summary>
    public ArchiveTreeItem? SelectedItem {
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
    /// methods (NavToParent, etc.) that need the most recent meaningful
    /// selection even after the tree is cleared. Reset to null on workspace close.
    /// </summary>
    public ArchiveTreeItem? CachedSelectedItem { get; private set; }

    /// <summary>
    /// Resets the cached selection. Call on workspace close.
    /// </summary>
    public void ResetCache() {
        CachedSelectedItem = null;
    }

    // Tracks (item, handler) pairs so they can be unwired on Clear/Dispose.
    private readonly List<(ArchiveTreeItem item, PropertyChangedEventHandler handler)>
        mItemHandlers = new();

    /// <summary>
    /// Subscribes to IsSelected changes on the given item and all its descendants.
    /// Call after populating TreeRoot or expanding a subtree.
    /// </summary>
    public void SubscribeToSelectionChanges(ArchiveTreeItem item) {
        ArchiveTreeItem captured = item;
        PropertyChangedEventHandler handler = (s, e) => {
            if (e.PropertyName == nameof(ArchiveTreeItem.IsSelected) && captured.IsSelected) {
                SelectedItem = captured;
            }
        };
        item.PropertyChanged += handler;
        mItemHandlers.Add((item, handler));

        foreach (ArchiveTreeItem child in item.Items) {
            SubscribeToSelectionChanges(child);
        }
    }

    /// <summary>
    /// Subscribes to all items in TreeRoot. Call after PopulateArchiveTree().
    /// </summary>
    public void SubscribeAllSelectionChanges() {
        // Unwire previous handlers before rebuilding.
        foreach (var (item, handler) in mItemHandlers) {
            item.PropertyChanged -= handler;
        }
        mItemHandlers.Clear();
        foreach (ArchiveTreeItem root in TreeRoot) {
            SubscribeToSelectionChanges(root);
        }
    }

    public void Dispose() {
        foreach (var (item, handler) in mItemHandlers) {
            item.PropertyChanged -= handler;
        }
        mItemHandlers.Clear();
    }
}
