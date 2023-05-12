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
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;

using AppCommon;
using CommonUtil;
using DiskArc;
using static DiskArc.Defs;

namespace cp2_wpf {
    /// <summary>
    /// One entry in the directory tree hierarchy.
    /// </summary>
    public class DirectoryTreeItem : INotifyPropertyChanged {
        /// <summary>
        /// Name to show in the GUI.
        /// </summary>
        public string Name { get; set; }

        public IFileEntry FileEntry { get; set; }
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
                //Debug.WriteLine("Tree: selected '" + Name + "'=" + value);
                if (value != mIsSelected) {
                    mIsSelected = value;
                    OnPropertyChanged();
                }
            }
        }
        private bool mIsSelected;

        public DirectoryTreeItem(string name, IFileEntry fileEntry) {
            Name = name;
            FileEntry = fileEntry;

            Items = new ObservableCollection<DirectoryTreeItem>();
        }

        public override string ToString() {
            return "[DirectoryTreeItem: name='" + Name + "' entry=" + FileEntry + "]";
        }

        /// <summary>
        /// Recursively populates the directory tree view from an IFileSystem object.
        /// </summary>
        /// <param name="tvRoot">Level where new items should be added.</param>
        /// <param name="fs">Filesystem object.</param>
        /// <param name="dirEntry">Directory entry object.</param>
        /// <param name="appHook">Application hook reference.</param>
        public static void PopulateDirectoryTree(ObservableCollection<DirectoryTreeItem> tvRoot,
                IFileSystem fs, IFileEntry dirEntry, AppHook appHook) {
            DirectoryTreeItem newItem = new DirectoryTreeItem(dirEntry.FileName, dirEntry);
            tvRoot.Add(newItem);
            foreach (IFileEntry entry in dirEntry) {
                if (entry.IsDirectory) {
                    PopulateDirectoryTree(newItem.Items, fs, entry, appHook);
                }
            }
        }

        public static bool SelectItemByEntry(ObservableCollection<DirectoryTreeItem> tvRoot,
                IFileEntry dirEntry) {
            Debug.Assert(dirEntry.IsDirectory);
            // Currently no way to select the root dir from the list, so no need to test root.
            foreach (DirectoryTreeItem treeItem in tvRoot) {
                if (treeItem.FileEntry == dirEntry) {
                    treeItem.IsSelected = true;
                    return true;
                }
                if (SelectItemByEntry(treeItem.Items, dirEntry)) {
                    treeItem.IsExpanded = true;
                    return true;
                }
            }
            return false;
        }
    }
}
