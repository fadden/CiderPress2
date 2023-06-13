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
using System.Windows;
using System.Windows.Controls;

using AppCommon;
using CommonUtil;
using cp2_wpf.WPFCommon;
using DiskArc;
using DiskArc.Arc;

namespace cp2_wpf {
    /// <summary>
    /// One entry in the archive tree hierarchy.
    /// </summary>
    public class ArchiveTreeItem : INotifyPropertyChanged {
        /// <summary>
        /// Reference to parent node.  Will be null at root of tree.
        /// </summary>
        public ArchiveTreeItem? Parent { get; private set; }

        /// <summary>
        /// Type string to show in the GUI.
        /// </summary>
        public string TypeStr { get; set; }

        /// <summary>
        /// Name to show in the GUI.
        /// </summary>
        public string Name { get; set; }


        public enum Status { None = 0, OK, Dubious, Warning, Error };
        public ControlTemplate? StatusIcon { get; set; }
        public ControlTemplate? ReadOnlyIcon { get; set; }

        /// <summary>
        /// Reference to corresponding node in work tree.
        /// </summary>
        public WorkTree.Node WorkTreeNode { get; private set; }

        /// <summary>
        /// Children.
        /// </summary>
        public ObservableCollection<ArchiveTreeItem> Items { get; private set; }

        // INotifyPropertyChanged
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string propertyName = "") {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// True if this item can be closed.  This will be set for disk images and file archives
        /// that are children of IFileSystem or IArchive.
        /// </summary>
        public bool CanClose {
            get {
                if (Parent == null) {
                    return false;
                }
                WorkTree.Node workNode = WorkTreeNode;
                object daObject = workNode.DAObject;
                if (!(daObject is IDiskImage || daObject is IArchive)) {
                    return false;
                }
                WorkTree.Node parentNode = Parent.WorkTreeNode;
                object daParent = parentNode.DAObject;
                if (!(daParent is IFileSystem || daParent is IArchive)) {
                    return false;
                }
                return true;
            }
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
                //Debug.WriteLine("ArcTree: " + (value ? "" : "UN") + "selected '" + Name + "'");
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


        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="workNode">Reference to corresponding work tree node.</param>
        public ArchiveTreeItem(ArchiveTreeItem? parent, WorkTree.Node workNode) {
            Parent = parent;
            WorkTreeNode = workNode;

            TypeStr = workNode.TypeStr;
            Name = workNode.Label;

            // Should we cache resource refs for performance?  We shouldn't be using them often.
            switch (workNode.NodeStatus) {
                case WorkTree.Node.Status.OK:
                    StatusIcon = null;
                    break;
                case WorkTree.Node.Status.Dubious:
                    StatusIcon =
                        (ControlTemplate)Application.Current.FindResource("icon_StatusInvalid");
                    break;
                case WorkTree.Node.Status.Warning:
                    StatusIcon =
                        (ControlTemplate)Application.Current.FindResource("icon_StatusWarning");
                    break;
                case WorkTree.Node.Status.Error:
                    StatusIcon =
                        (ControlTemplate)Application.Current.FindResource("icon_StatusError");
                    break;
                default:
                    Debug.Assert(false);
                    break;
            }
            if (workNode.IsNodeReadOnly) {
                ReadOnlyIcon =
                    (ControlTemplate)Application.Current.FindResource("icon_StatusNoNoColor");
            }

            Items = new ObservableCollection<ArchiveTreeItem>();
        }

        /// <summary>
        /// Removes the specified child from the tree.
        /// </summary>
        public bool RemoveChild(ArchiveTreeItem child) {
            return Items.Remove(child);
        }

        /// <summary>
        /// Finds a tree item with a matching file entry object.
        /// </summary>
        /// <param name="tvRoot">Items list of root object.</param>
        /// <param name="fileEntry">File entry to search for.</param>
        /// <returns>Item found, or null if not found.</returns>
        public static ArchiveTreeItem? FindItemByEntry(ObservableCollection<ArchiveTreeItem> tvRoot,
                IFileEntry fileEntry) {
            foreach (ArchiveTreeItem treeItem in tvRoot) {
                DiskArcNode? daNode = treeItem.WorkTreeNode.DANode;
                if (daNode != null && daNode.EntryInParent == fileEntry) {
                    return treeItem;
                }
                ArchiveTreeItem? found = FindItemByEntry(treeItem.Items, fileEntry);
                if (found != null) {
                    // TODO: shouldn't do this here.  Add an "expand sub-tree" method.  Need
                    //   to add a parent reference?
                    treeItem.IsExpanded = true;
                    return found;
                }
            }
            return null;
        }

        /// <summary>
        /// Recursively finds a node with a specific DiskArc object (IFileSystem, Partition, etc.)
        /// in the tree.
        /// </summary>
        /// <param name="tvRoot">Root of tree to search.</param>
        /// <param name="matchObj">Object to search for.</param>
        /// <returns></returns>
        public static ArchiveTreeItem? FindItemByDAObject(
                ObservableCollection<ArchiveTreeItem> tvRoot, object matchObj) {
            foreach (ArchiveTreeItem treeItem in tvRoot) {
                if (matchObj == treeItem.WorkTreeNode.DAObject) {
                    return treeItem;
                }
                ArchiveTreeItem? found = FindItemByDAObject(treeItem.Items, matchObj);
                if (found != null) {
                    treeItem.IsExpanded = true;
                    return found;
                }
            }
            return null;
        }

        /// <summary>
        /// Constructs the archive tree, starting from the root.
        /// </summary>
        /// <param name="tvItems">Items list at root of tree.</param>
        /// <param name="workNode">Work tree node.</param>
        /// <returns>Item created.</returns>
        public static ArchiveTreeItem ConstructTree(ObservableCollection<ArchiveTreeItem> tvItems,
                WorkTree.Node workNode) {
            ArchiveTreeItem newItem = new ArchiveTreeItem(null, workNode);
            tvItems.Add(newItem);
            // Descend into workNode's children.
            foreach (WorkTree.Node child in workNode) {
                ConstructTree(newItem, child);
            }
            return newItem;
        }

        /// <summary>
        /// Constructs a section of the archive tree.  This is called recursively.
        /// </summary>
        /// <param name="parent">Parent item.</param>
        /// <param name="workNode">Work tree node.</param>
        /// <returns>Item created.</returns>
        public static ArchiveTreeItem ConstructTree(ArchiveTreeItem parent,
                WorkTree.Node workNode) {
            // Create a new tree item for workNode, and add it to the parent's child list.
            ArchiveTreeItem newItem = new ArchiveTreeItem(parent, workNode);
            parent.Items.Add(newItem);
            // Descend into workNode's children.
            foreach (WorkTree.Node child in workNode) {
                ConstructTree(newItem, child);
            }
            return newItem;
        }

        /// <summary>
        /// Selects the "best" thing in the specified tree.  For example, we prefer to select
        /// filesystems rather than the disk image that contains them.  If nothing interesting
        /// is found, the root will be selected.
        /// </summary>
        /// <param name="root">Starting point.</param>
        public static void SelectBestFrom(TreeView tv, ArchiveTreeItem root) {
            ArchiveTreeItem? best = SelectBestFromR(root);
            if (best == null) {
                BringItemIntoView(tv, root);
                root.IsSelected = true;
            } else {
                BringItemIntoView(tv, best);
                best.IsSelected = true;
            }
            //Debug.WriteLine("GOT: arc=" + CountSelections(root));
        }
        private static ArchiveTreeItem? SelectBestFromR(ArchiveTreeItem root) {
            ArchiveTreeItem? result;

            foreach (ArchiveTreeItem item in root.Items) {
                if (item.WorkTreeNode.DAObject is GZip) {
                    // Go deeper.
                    if ((result = SelectBestFromR(item)) != null) {
                        return result;
                    }
                } else if (item.WorkTreeNode.DAObject is NuFX) {
                    IFileEntry firstEntry = ((NuFX)item.WorkTreeNode.DAObject).GetFirstEntry();
                    if (!firstEntry.IsDiskImage) {
                        // Non-disk archive, select it.
                        return item;
                    }
                    // Disk archive, go deeper.
                    if ((result = SelectBestFromR(item)) != null) {
                        return result;
                    }
                } else if (item.WorkTreeNode.DAObject is IFileSystem ||
                           item.WorkTreeNode.DAObject is IArchive) {
                    // These are good.
                    return item;
                } else {
                    // IMultiPart, IDiskImage, or Partition; go deeper.
                    if ((result = SelectBestFromR(item)) != null) {
                        return result;
                    }
                }
            }
            return null;
        }

#if false
        private static int CountSelections(ArchiveTreeItem item) {
            while (item.Parent != null) {
                item = item.Parent;
            }
            int count = item.IsSelected ? 1 : 0;
            CountSelections(item.Items, ref count);
            return count;
        }
        private static void CountSelections(ObservableCollection<ArchiveTreeItem> items,
                ref int count) {
            foreach (ArchiveTreeItem item in items) {
                if (item.IsSelected) {
                    count++;
                }
                CountSelections(item.Items, ref count);
            }
        }
#endif

        public override string ToString() {
            return "[ArchiveTreeItem: name='" + Name + "' node=" + WorkTreeNode + "]";
        }

        // TODO: share this with DirectoryTreeItem.  We need Parent and Items.  Abstract base
        // class is awkward because a lot of things want the specific subclass from the
        // observable lists, but it's probably worth doing at this point.

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
        internal static void BringItemIntoView(TreeView treeView, ArchiveTreeItem item) {
            //Debug.WriteLine("Bring into view: " + item);

            // We're passing in a leaf node, but we need to work from the root out.
            Stack<ArchiveTreeItem> stack = new Stack<ArchiveTreeItem>();
            stack.Push(item);
            while (item.Parent != null) {
                stack.Push(item.Parent);
                item = item.Parent;
            }
            ItemsControl? containerControl = treeView;
            while (stack.Count > 0) {
                Debug.Assert(containerControl != null);
                ArchiveTreeItem atItem = stack.Pop();
                VirtualizingStackPanel? virtPanel =
                    VisualHelper.GetVisualChild<VirtualizingStackPanel>(containerControl);
                if (virtPanel != null) {
                    // If we have a parent, find our place in its list of children.  If not,
                    // we're at the root, so find the index in the TreeView's items list.
                    int index = atItem.Parent != null ? atItem.Parent.Items.IndexOf(atItem) :
                        treeView.Items.IndexOf(atItem);
                    if (index < 0) {
                        // Unexpected.
                        Debug.WriteLine("Item not found!");
                    } else {
                        virtPanel.BringIndexIntoView_Public(index);
                    }
                    treeView.Focus();
                }
                var tvItem = containerControl.ItemContainerGenerator.ContainerFromItem(atItem);
                containerControl = (ItemsControl)tvItem;
                Debug.Assert(containerControl != null || stack.Count == 0);
            }
        }

        /// <summary>
        /// Clears the IsSelected flag from all but one item.
        /// </summary>
        /// <param name="keep">Item to keep.</param>
        private static void PurgeSelectionsExcept(ArchiveTreeItem keep) {
            ArchiveTreeItem top = keep;
            while (top.Parent != null) {
                top = top.Parent;
            }
            PurgeSelections(top.Items, keep);
        }
        private static void PurgeSelections(ObservableCollection<ArchiveTreeItem> items,
                ArchiveTreeItem keep) {
            foreach (ArchiveTreeItem item in items) {
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
