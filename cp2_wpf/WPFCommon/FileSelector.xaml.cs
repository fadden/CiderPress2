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
using System.Windows.Input;
using System.Windows.Media;

// TODO:
// - generate file list from current dir
// - need an "up one directory" button ("upload" arrow icon)
// - use a file watcher to auto-refresh on change?  Or just have a refresh button?
// - need a "create new directory" button
// - "ok" button label should reflect what we're trying to do, especially for folder selection
//
// - permission checks? https://stackoverflow.com/q/1281620/294248

namespace cp2_wpf.WPFCommon {
    /// <summary>
    /// File/folder selection dialog.
    /// </summary>
    /// <remarks>
    /// <para>OpenFileDialog doesn't allow selection of both files and folders (without some
    /// native hackery), and SHBrowseForFolder is an abomination.  There's a Vista+ folder
    /// selection dialog enabled with FOS_PICKFOLDERS, but getting to that from managed code is
    /// complicated.</para>
    /// <para>This is not meant for opening documents with a specific type, for which the
    /// standard dialog works well.  We could support file extension filters but we don't
    /// currently need them.  It's also not meant for saving files, so we don't provide
    /// a way to enter a new filename.</para>
    /// </remarks>
    public partial class FileSelector : Window, INotifyPropertyChanged {
        //
        // Options.
        //

        /// <summary>
        /// True if files can be selected.  False if the goal is to select a folder.
        /// </summary>
        public bool CanSelectFiles { get; set; } = true;

        /// <summary>
        /// True if folders can be selected.  False if we're only interested in files.  Folders
        /// will still be shown either way; it's just a question of whether the OK button is
        /// enabled when only folders are chosen.
        /// </summary>
        public bool CanSelectFolders { get; set; } = false;

        /// <summary>
        /// True if multiple files can be selected.
        /// </summary>
        public bool MultiSelect { get; set; } = false;

        /// <summary>
        /// Initial path.  If empty, the current working directory is used.
        /// </summary>
        public string InitialPath { get; set; } = string.Empty;

        //
        // Results.
        //

        public string BasePath { get; private set; } = string.Empty;
        public string[] SelectedFiles { get; private set; } = new string[0];
        public string[] SelectedPaths { get; private set; } = new string[0];

        //
        // Innards.
        //

        // INotifyPropertyChanged
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string propertyName = "") {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// Set to true when input is valid.  Controls whether the OK button is enabled.
        /// </summary>
        public bool IsValid {
            get { return mIsValid; }
            set { mIsValid = value; OnPropertyChanged(); }
        }
        private bool mIsValid;

        public string PathNameText {
            get { return mPathNameText; }
            set { mPathNameText = value; OnPropertyChanged(); PathChanged(); }
        }
        private string mPathNameText;

        /// <summary>
        /// One item in the file list or special path list.
        /// </summary>
        public class ListItem {
            /// <summary>
            /// Full pathname of item.
            /// </summary>
            public string PathName { get; private set; }

            /// <summary>
            /// Name to display on screen.  Usually the filename portion of PathName.
            /// </summary>
            public string DisplayName { get; private set; }

            /// <summary>
            /// File icon.
            /// </summary>
            public ImageSource? Icon { get; private set; }

            // TODO: modification date

            // TODO: size

            public ListItem(string pathName, string displayName, ImageSource? icon) {
                PathName = pathName;
                DisplayName = displayName;
                Icon = icon;
            }
        }

        public ObservableCollection<ListItem> SpecialPathList { get; private set; } =
            new ObservableCollection<ListItem>();

        public ObservableCollection<ListItem> FileList { get; private set; } =
            new ObservableCollection<ListItem>();


        public FileSelector(Window owner) {
            InitializeComponent();
            Owner = owner;
            DataContext = this;

            fileListDataGrid.SelectionMode =
                MultiSelect ? DataGridSelectionMode.Extended : DataGridSelectionMode.Single;
            if (string.IsNullOrEmpty(InitialPath) || !Directory.Exists(InitialPath)) {
                mPathNameText = Environment.CurrentDirectory;
            } else {
                mPathNameText = InitialPath;
            }

            LoadSpecialPaths();

            FileList.Add(new ListItem("x", "sample file 1", null));
            FileList.Add(new ListItem("x", "sample file 2", null));

            PathChanged();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e) {
            DialogResult = true;
            // TODO
        }

        private void NewDirectory_Click(object sender, RoutedEventArgs e) {
            // TODO
        }

        private void SpecialPathList_SelectionChanged(object sender, SelectionChangedEventArgs e) {
            // Switch to the new path, if it exists (which it should).
            ListItem item = (ListItem)((DataGrid)sender).SelectedItem;
            string newPath = item.PathName;
            if (Directory.Exists(newPath)) {
                PathNameText = newPath;
            } else {
                Debug.WriteLine("Unable to find path '" + newPath + "'");
            }
        }

        private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e) {
            // TODO
            // File mode: set IsValid if at least one entry is selected
            // Folder mode: set IsValid if files can be created in the current dir
        }

        private void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e) {
            // TODO
            // In single-select mode: if folder, open; if file, select and close dialog.
            // In multi-select mode: if folder, open; if file, do nothing.
            //
            // When folder changes, see if we want to update selection in SpecialPathList
            // to match.  Use best available prefix.
        }

        private static readonly Environment.SpecialFolder[] sSpecialFolders = {
            //Environment.SpecialFolder.MyComputer,
            Environment.SpecialFolder.Desktop,
            Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.MyPictures,
            Environment.SpecialFolder.NetworkShortcuts,
        };

        /// <summary>
        /// Generates the list of "special" items in the left-hand window.
        /// </summary>
        /// <remarks>
        /// This is currently "special" and "known" folders, plus all logical drives that are
        /// reported as "ready".
        /// </remarks>
        private void LoadSpecialPaths() {
            foreach (Environment.SpecialFolder spec in sSpecialFolders) {
                string path = Environment.GetFolderPath(spec);
                if (string.IsNullOrEmpty(path)) {
                    Debug.WriteLine("No path for spec=" + spec);
                    continue;
                }

                SpecialPathList.Add(GenerateItem(path));
            }
            string dlPath = WinMagic.GetKnownFolderPath(WinMagic.KnownFolder.Downloads);
            if (!string.IsNullOrEmpty(dlPath)) {
                SpecialPathList.Add(GenerateItem(dlPath));
            }

            foreach (DriveInfo di in DriveInfo.GetDrives()) {
                if (!di.IsReady) {
                    continue;       // e.g. floppy drive with no floppy
                }

                string path = di.Name;
                string displayName = di.VolumeLabel + " (" + path + ")";
                SpecialPathList.Add(GenerateItem(path, displayName));
            }

            // TODO: add favorites / frequently-used folders?
        }

        /// <summary>
        /// Generates a list item.
        /// </summary>
        private ListItem GenerateItem(string path, string displayName = "") {
            if (string.IsNullOrEmpty(displayName)) {
                displayName = Path.GetFileName(path);
                if (string.IsNullOrEmpty(displayName)) {
                    displayName = path;
                }
            }
            ImageSource icon = WinMagic.GetIcon(path, WinMagic.IconQuery.Specific);
            ListItem item = new ListItem(path, displayName, icon);
            return item;
        }

        private void PathChanged() {
            Debug.WriteLine("Path is now '" + PathNameText + "'");
            // TODO
        }
    }
}
