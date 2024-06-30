/*
 * Copyright 2023 faddenSoft
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
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Controls;

using AppCommon;
using CommonUtil;
using cp2_wpf.WPFCommon;
using DiskArc;
using static DiskArc.Defs;

namespace cp2_wpf {
    /// <summary>
    /// One entry in the directory tree hierarchy.
    /// </summary>
    public class DirectoryTreeItem : INotifyPropertyChanged {
        /// <summary>
        /// Reference to parent node.  Will be null at root of tree.
        /// </summary>
        public DirectoryTreeItem? Parent { get; private set; }

        /// <summary>
        /// Name to show in the GUI.
        /// </summary>
        public string Name {
            get { return mName; }
            set { mName = value; OnPropertyChanged(); }
        }
        private string mName = string.Empty;

        /// <summary>
        /// Directory file entry for this item.
        /// </summary>
        public IFileEntry FileEntry { get; set; }

        /// <summary>
        /// List of child items.
        /// </summary>
        public ObservableCollection<DirectoryTreeItem> Items { get; set; }

        // INotifyPropertyChanged
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string propertyName = "") {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// Tied to TreeViewItem.IsExpanded.
        /// </summary>
        public bool IsExpanded {
            get { return mIsExpanded; }
            set {
                if (value != mIsExpanded) {
                    Debug.WriteLine("Tree: expanded '" + Name + "'=" + value);
                    mIsExpanded = value;
                    OnPropertyChanged();
                    // If we track the parent, we can propagate the expansion upward.
                }
            }
        }
        private bool mIsExpanded = true;

        /// <summary>
        /// Tied to TreeViewItem.IsSelected.
        /// </summary>
        public bool IsSelected {
            get { return mIsSelected; }
            set {
                //Debug.WriteLine("DirTree: " + (value ? "" : "UN") + "selected '" + Name + "'");
                if (value != mIsSelected) {
                    mIsSelected = value;
                    OnPropertyChanged();
                }
                if (value) {
                    // Setting IsSelected to true on one node is supposed to clear it from other
                    // nodes, but virtualization can interfere with this.
                    PurgeSelectionsExcept(this);
                }
            }
        }
        private bool mIsSelected;


        public DirectoryTreeItem(DirectoryTreeItem? parent, string name, IFileEntry fileEntry) {
            Parent = parent;
            Name = name;
            FileEntry = fileEntry;

            Items = new ObservableCollection<DirectoryTreeItem>();
        }

        public override string ToString() {
            return "[DirectoryTreeItem: name='" + Name + "' entry=" + FileEntry + "]";
        }


        /// <summary>
        /// Recursively finds an item in the directory tree.  Expands the nodes leading to
        /// the found item.
        /// </summary>
        /// <param name="tvRoot">Start point of search.</param>
        /// <param name="dirEntry">Directory file entry to search for.</param>
        /// <returns>Item found, or null.</returns>
        public static DirectoryTreeItem? FindItemByEntry(
                ObservableCollection<DirectoryTreeItem> tvRoot, IFileEntry dirEntry) {
            Debug.Assert(dirEntry.IsDirectory);
            // Currently no way to select the root dir from the list, so no need to test root.
            foreach (DirectoryTreeItem treeItem in tvRoot) {
                if (treeItem.FileEntry == dirEntry) {
                    return treeItem;
                }
                DirectoryTreeItem? found = FindItemByEntry(treeItem.Items, dirEntry);
                if (found != null) {
                    // Was found in sub-tree, so expand this part.
                    treeItem.IsExpanded = true;
                    return found;
                }
            }
            return null;
        }

        /// <summary>
        /// Finds an item in the directory tree, and marks it as selected.
        /// </summary>
        /// <param name="mainWin">Main window.</param>
        /// <param name="dirEntry">Directory file entry to search for.</param>
        /// <returns>True if found.</returns>
        public static bool SelectItemByEntry(MainWindow mainWin, IFileEntry dirEntry) {
            ObservableCollection<DirectoryTreeItem> tvRoot = mainWin.DirectoryTreeRoot;
            DirectoryTreeItem? item = FindItemByEntry(tvRoot, dirEntry);
            if (item != null) {
                // We need to bring the item into view *before* we select it, in case it's
                // virtual and the TreeViewItem doesn't really exist yet.
                BringItemIntoView(mainWin.directoryTree, item);
                item.IsSelected = true;
                return true;
            } else {
                return false;
            }
        }

        /// <summary>
        /// Scrolls the TreeView so the specified item is visible.  This works even if the tree
        /// is virtualized and the item in question hasn't been prepared yet.  Call this right
        /// before setting IsSelected.
        /// </summary>
        /// <remarks>
        /// <para>This simple function took a few hours to figure out.  Big thanks to
        /// <see href="https://stackoverflow.com/a/17883600/294248"/> and the other
        /// solutions on that page.</para>
        /// <para><see href="https://stackoverflow.com/a/9494484/294248"/> seems like it would
        /// be enough, but TreeViewItem.BringIntoView() doesn't seem to work.</para>
        /// </remarks>
        /// <param name="treeView">TreeView that holds the item.</param>
        /// <param name="item">Item to view.</param>
        internal static void BringItemIntoView(TreeView treeView, DirectoryTreeItem item) {
            //Debug.WriteLine("Bring into view: " + item);

            // We're passing in a leaf node, but we need to work from the root out.
            Stack<DirectoryTreeItem> stack = new Stack<DirectoryTreeItem>();
            stack.Push(item);
            while (item.Parent != null) {
                stack.Push(item.Parent);
                item = item.Parent;
            }
            ItemsControl? containerControl = treeView;
            while (stack.Count > 0) {
                Debug.Assert(containerControl != null);
                DirectoryTreeItem dtItem = stack.Pop();
                var virtPanel =
                    VisualHelper.GetVisualChild<VirtualizingStackPanel>(containerControl);
                if (virtPanel != null) {
                    // If we have a parent, find our place in its list of children.  If not,
                    // we're at the root, so find the index in the TreeView's items list.
                    int index = dtItem.Parent != null ? dtItem.Parent.Items.IndexOf(dtItem) :
                        treeView.Items.IndexOf(dtItem);
                    if (index < 0) {
                        // Unexpected.
                        Debug.WriteLine("Item not found!");
                    } else {
                        virtPanel.BringIndexIntoView_Public(index);
                    }
                    treeView.Focus();
                }
                var tvItem = containerControl.ItemContainerGenerator.ContainerFromItem(dtItem);
                containerControl = (ItemsControl)tvItem;
                if (containerControl == null && stack.Count != 0) {
                    // This happens in weird circumstances, notably when the "find" feature
                    // is blowing up.
                    Debug.WriteLine("GLITCH while bringing TreeView item into view");
                    break;
                }
            }
        }

        /// <summary>
        /// Clears the IsSelected flag from all but one item.
        /// </summary>
        /// <param name="keep">Item to keep.</param>
        private static void PurgeSelectionsExcept(DirectoryTreeItem keep) {
            DirectoryTreeItem top = keep;
            while (top.Parent != null) {
                top = top.Parent;
            }
            PurgeSelections(top.Items, keep);
        }
        private static void PurgeSelections(ObservableCollection<DirectoryTreeItem> items,
                DirectoryTreeItem keep) {
            foreach (DirectoryTreeItem item in items) {
                Debug.Assert(item != keep || item.IsSelected);
                if (item.IsSelected && item != keep) {
                    // This should only happen if the TreeViewItem got recycled, so setting
                    // the selected value to false should have no effect on the UI.
                    //Debug.WriteLine("Purging selection: " + item);
                    item.IsSelected = false;
                }
                PurgeSelections(item.Items, keep);
            }
        }
    }
}
