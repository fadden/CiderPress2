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
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

// TODO:
// - use a file watcher to auto-refresh on change?  Or just have a refresh button?
// - recognize carriage return in path box
//
// - permission checks? https://stackoverflow.com/q/1281620/294248

namespace cp2_wpf.WPFCommon {
    /// <summary>
    /// File/folder selection dialog.  The style is similar to the standard file dialogs and
    /// Windows explorer in Win10.
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

        public enum SelMode {
            Unknown = 0,
            Files,                  // select files
            FilesAndFolders,        // select both files and folders
            Folder,                 // select single folder (e.g. for output location)
        }

        /// <summary>
        /// What we allow the user to do.
        /// </summary>
        public SelMode SelectionMode { get; set; } = SelMode.FilesAndFolders;

        /// <summary>
        /// True if multiple files can be selected.
        /// </summary>
        /// <remarks>
        /// Not compatible with "Folder" mode.
        /// </remarks>
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

        public string ErrorMessage {
            get { return mErrorMessage; }
            set { mErrorMessage = value; OnPropertyChanged(); }
        }
        private string mErrorMessage = string.Empty;

        public Visibility ErrorVisibility {
            get { return mErrorVisibility; }
            set { mErrorVisibility = value; OnPropertyChanged(); }
        }
        private Visibility mErrorVisibility = Visibility.Collapsed;

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
            /// True if this is a folder.
            /// </summary>
            public bool IsFolder { get; private set; }

            /// <summary>
            /// File icon.
            /// </summary>
            public ImageSource? Icon { get; private set; }

            /// <summary>
            /// Date when file or folder was last modified.
            /// </summary>
            public DateTime ModWhen { get; private set; }

            /// <summary>
            /// Date when file or folder was last modified (formatted).
            /// </summary>
            public string LastModDate { get; private set; }

            /// <summary>
            /// Size of file, in bytes.
            /// </summary>
            public long FileLength { get; private set; }

            /// <summary>
            /// Size of file, in KiB (formatted).
            /// </summary>
            public string Size { get; private set; }

            public ListItem(string pathName, string displayName, bool isFolder,
                    ImageSource? icon, DateTime modWhen, long fileLength) {
                PathName = pathName;
                DisplayName = displayName;
                IsFolder = isFolder;
                Icon = icon;
                ModWhen = modWhen;
                LastModDate = modWhen.ToString("g");
                FileLength = fileLength;
                if (fileLength < 0) {
                    Size = string.Empty;
                } else {
                    long kbLength = (fileLength + 1023) / 1024;
                    Size = kbLength.ToString("N0") + " KB";
                }
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

            switch (SelectionMode) {
                case SelMode.Files:
                    if (MultiSelect) {
                        Title = "Select Files...";
                    } else {
                        Title = "Select File...";
                    }
                    break;
                case SelMode.FilesAndFolders:
                    if (MultiSelect) {
                        Title = "Select Files and Folders...";
                    } else {
                        Title = "Select File...";
                    }
                    break;
                case SelMode.Folder:
                    Title = "Select Folder...";
                    MultiSelect = false;
                    break;
                default:
                    throw new NotImplementedException();
            }

            fileListDataGrid.SelectionMode =
                MultiSelect ? DataGridSelectionMode.Extended : DataGridSelectionMode.Single;
            if (string.IsNullOrEmpty(InitialPath) || !Directory.Exists(InitialPath)) {
                mPathNameText = Environment.CurrentDirectory;
            } else {
                mPathNameText = InitialPath;
            }

            LoadSpecialPaths();
            PathChanged();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e) {
            DialogResult = true;
            // TODO
        }

        private void NewDirectory_Click(object sender, RoutedEventArgs e) {
            // TODO
        }

        private void NavUp_Click(object sender, RoutedEventArgs e) {
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
            DataGrid grid = (DataGrid)sender;
            if (!grid.GetClickRowColItem(e, out int row, out int col, out object? citem)) {
                // Header or empty area; ignore.
                return;
            }
            ListItem item = (ListItem)citem;

            Debug.WriteLine("Double-click on '" + item.PathName + "'");
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

            ListItem item;
            try {
                FileAttributes attr = File.GetAttributes(path);
                bool isDirectory = (attr & FileAttributes.Directory) != 0;
                DateTime modWhen;
                long length;
                if (isDirectory) {
                    length = -1;
                    modWhen = Directory.GetLastWriteTime(path);
                } else {
                    modWhen = File.GetLastWriteTime(path);
                    length = new FileInfo(path).Length;
                }
                ImageSource icon = WinMagic.GetIcon(path, WinMagic.IconQuery.Specific, true);
                item = new ListItem(path, displayName, isDirectory, icon, modWhen, length);
            } catch (Exception ex) {
                Debug.WriteLine("Error processing '" + path + "': " + ex);
                item = new ListItem(path, displayName, false, null, DateTime.Now, -1);
            }
            return item;
        }

        private void PathChanged() {
            Debug.WriteLine("Path is now '" + PathNameText + "'");
            // TODO
            //
            // When folder changes, see if we want to update selection in SpecialPathList
            // to match.  Use best available prefix.

            FileList.Clear();
            ErrorVisibility = Visibility.Collapsed;

            string[] entries;
            try {
                Mouse.OverrideCursor = Cursors.Wait;
                switch (SelectionMode) {
                    case SelMode.Files:
                    case SelMode.FilesAndFolders:
                        entries = Directory.GetFileSystemEntries(PathNameText);
                        break;
                    case SelMode.Folder:
                        entries = Directory.GetDirectories(PathNameText);
                        break;
                    default:
                    throw new NotImplementedException();
                }
            } catch (Exception ex) {
                ShowError("Error: " + ex.Message);
                return;
            } finally {
                Mouse.OverrideCursor = null;
            }

            // Generate the items into a temporary list, and do the initial "default" sort.
            // (Can we convince the DataGrid to do this for us?)
            List<ListItem> tmpList = new List<ListItem>(entries.Length);
            foreach (string entry in entries) {
                tmpList.Add(GenerateItem(entry));
            }
            tmpList.Sort(new SymbolsListComparer((int)SymbolsListComparer.SortField.Name, true));

            foreach (ListItem item in tmpList) {
                FileList.Add(item);
            }
        }

        /// <summary>
        /// Handles a Sorting event.  We want folders to sort together, regardless of how
        /// everything else is sorted.
        /// </summary>
        private void FileList_Sorting(object sender, DataGridSortingEventArgs e) {
            DataGridColumn col = e.Column;

            // Set the SortDirection to a specific value.  If we don't do this, SortDirection
            // remains un-set, and the column header doesn't show up/down arrows or change
            // direction when clicked twice.
            ListSortDirection direction = (col.SortDirection != ListSortDirection.Ascending) ?
                ListSortDirection.Ascending : ListSortDirection.Descending;
            col.SortDirection = direction;

            bool isAscending = direction != ListSortDirection.Descending;

            IComparer comparer = new SymbolsListComparer(col.DisplayIndex, isAscending);
            ListCollectionView lcv = (ListCollectionView)
                CollectionViewSource.GetDefaultView(fileListDataGrid.ItemsSource);
            lcv.CustomSort = comparer;
            e.Handled = true;
        }

        private class SymbolsListComparer : IComparer, IComparer<ListItem> {
            // Must match order of items in DataGrid.  DataGrid must not allow columns to be
            // reordered.  We could check col.Header instead, but then we have to assume that
            // the Header is a string that doesn't get renamed.
            private int NUM_COLS = 4;
            internal enum SortField {
                Icon = 0, Name = 1, ModWhen = 2, Size = 3
            }
            private SortField mSortField;
            private bool mIsAscending;

            public SymbolsListComparer(int displayIndex, bool isAscending) {
                Debug.Assert(displayIndex >= 0 && displayIndex < NUM_COLS);
                mIsAscending = isAscending;
                mSortField = (SortField)displayIndex;
            }

            // IComparer interface
            public int Compare(object? x, object? y) {
                return Compare((ListItem?)x, (ListItem?)y);
            }

            // IComparer<ListItem> interface
            public int Compare(ListItem? item1, ListItem? item2) {
                Debug.Assert(item1 != null && item2 != null);

                int cmp;
                if (item1.IsFolder && !item2.IsFolder) {
                    cmp = -1;
                } else if (!item1.IsFolder && item2.IsFolder) {
                    cmp = 1;
                } else {
                    switch (mSortField) {
                        case SortField.Icon:
                            // Shouldn't be possible; fall through to name.
                        case SortField.Name:
                            cmp = string.Compare(item1.DisplayName, item2.DisplayName);
                            break;
                        case SortField.ModWhen:
                            if (item1.ModWhen < item2.ModWhen) {
                                cmp = -1;
                            } else if (item1.ModWhen > item2.ModWhen) {
                                cmp = 1;
                            } else {
                                cmp = 0;
                            }
                            break;
                        case SortField.Size:
                            if (item1.FileLength < item2.FileLength) {
                                cmp = -1;
                            } else if (item1.FileLength > item2.FileLength) {
                                cmp = 1;
                            } else {
                                cmp = 0;
                            }
                            break;
                        default:
                            Debug.Assert(false);
                            return 0;
                    }
                }

                if (cmp == 0) {
                    // Primary sort is equal, resolve by display name.
                    cmp = string.Compare(item1.DisplayName, item2.DisplayName);
                }
                if (!mIsAscending) {
                    cmp = -cmp;
                }
                return cmp;
            }
        }

        private void ShowError(string msg) {
            Debug.WriteLine("ERROR: " + msg);
            ErrorMessage = msg;
            ErrorVisibility = Visibility.Visible;
        }
    }
}
