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
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

using AppCommon;
using CommonUtil;
using DiskArc;

namespace cp2_wpf {
    /// <summary>
    /// One entry in the archive tree hierarchy.
    /// </summary>
    public class ArchiveTreeItem : INotifyPropertyChanged {
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
                //Debug.WriteLine("Tree: selected '" + Name + "'=" + value);
                if (value != mIsSelected) {
                    mIsSelected = value;
                    OnPropertyChanged();
                }
            }
        }
        private bool mIsSelected;


        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="workNode">Reference to corresponding work tree node.</param>
        public ArchiveTreeItem(WorkTree.Node workNode) {
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
            if (workNode.IsReadOnly) {
                ReadOnlyIcon =
                    (ControlTemplate)Application.Current.FindResource("icon_StatusNoNoColor");
            }

            Items = new ObservableCollection<ArchiveTreeItem>();
        }

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

        public override string ToString() {
            return "[ArchiveTreeItem: name='" + Name + "' node=" + WorkTreeNode + "]";
        }


        /// <summary>
        /// Constructs a section of the archive tree.  This is called recursively.
        /// </summary>
        /// <param name="workNode">Work tree node.</param>
        /// <param name="tvRoot">Root of sub-tree.</param>
        public static void ConstructTree(WorkTree.Node workNode,
                ObservableCollection<ArchiveTreeItem> tvRoot) {
            ArchiveTreeItem newItem = new ArchiveTreeItem(workNode);
            tvRoot.Add(newItem);
            WorkTree.Node[] children = workNode.GetChildren();
            foreach (WorkTree.Node child in children) {
                ConstructTree(child, newItem.Items);
            }
        }
    }
}
