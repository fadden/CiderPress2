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
    /// <para>This is not meant for opening individual documents with a specific type; the
    /// standard dialog works well for that.  We could support file extension filters but we don't
    /// currently need them.  This is also not meant for saving individual files, so we don't
    /// provide a way to enter a new filename.</para>
    /// </remarks>
    public partial class FileSelector : Window, INotifyPropertyChanged {
        //
        // Options.
        //

        public enum SelMode {
            Unknown = 0,
            SingleFile,             // select one file
            SingleFolder,           // select one folder (for output location)
            FilesAndFolders,        // select a collection of files and folders
        }

        /// <summary>
        /// What we allow the user to do.
        /// </summary>
        public SelMode SelectionMode { get; private set; } = SelMode.FilesAndFolders;

        /// <summary>
        /// Initial path.  If empty, the current working directory is used.
        /// </summary>
        public string InitialPath { get; private set; } = string.Empty;

        //
        // Results.
        //

        /// <summary>
        /// Base path in which the selected files live.  For Folder mode, this is the selected
        /// output directory.
        /// </summary>
        public string BasePath { get; private set; } = string.Empty;

        /// <summary>
        /// List of selected files, filenames only.
        /// </summary>
        public string[] SelectedFiles { get; private set; } = new string[0];

        /// <summary>
        /// List of selected files, full paths.
        /// </summary>
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

        public string AcceptButtonText { get; private set; }

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
            /// True if this entry is hidden.  Hidden entries may be inaccessible (e.g. My Music
            /// appears in the Documents folder but can't be used that way).
            /// </summary>
            public bool IsHidden { get; private set; }

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

            public ListItem(string pathName, string displayName, bool isFolder, bool isHidden,
                    ImageSource? icon, DateTime modWhen, long fileLength) {
                PathName = pathName;
                DisplayName = displayName;
                IsFolder = isFolder;
                IsHidden = isHidden;
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

            public override string ToString() {
                return "[ListItem: PathName='" + PathName + "']";
            }
        }

        public ObservableCollection<ListItem> SpecialPathList { get; private set; } =
            new ObservableCollection<ListItem>();

        public ObservableCollection<ListItem> FileList { get; private set; } =
            new ObservableCollection<ListItem>();


        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="owner">Parent window.</param>
        /// <param name="mode">File/folder selection mode.</param>
        /// <param name="initialPath">Initial path; may be empty to use a default.</param>
        public FileSelector(Window owner, SelMode mode, string initialPath) {
            InitializeComponent();
            Owner = owner;
            DataContext = this;

            SelectionMode = mode;
            if (string.IsNullOrEmpty(initialPath)) {
                InitialPath = Environment.CurrentDirectory;     // prefer My Documents?
            } else {
                InitialPath = initialPath;
            }

            switch (SelectionMode) {
                case SelMode.SingleFile:
                    Title = "Select File...";
                    AcceptButtonText = "Select";
                    break;
                case SelMode.SingleFolder:
                    Title = "Select Folder...";
                    AcceptButtonText = "Select Here";
                    break;
                case SelMode.FilesAndFolders:
                    Title = "Select Files and Folders...";
                    AcceptButtonText = "Select";
                    break;
                default:
                    throw new NotImplementedException();
            }

            fileListDataGrid.SelectionMode = (SelectionMode == SelMode.FilesAndFolders) ?
                DataGridSelectionMode.Extended : DataGridSelectionMode.Single;
            if (string.IsNullOrEmpty(InitialPath) || !Directory.Exists(InitialPath)) {
                mPathNameText = Environment.CurrentDirectory;
            } else {
                mPathNameText = InitialPath;
            }
        }

        private void Window_ContentRendered(object sender, EventArgs e) {
            LoadSpecialPaths();
            mConfiguringPanel = true;
            PathChanged();
            mConfiguringPanel = false;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e) {
            AcceptSelection();
        }

        private void NewDirectory_Click(object sender, RoutedEventArgs e) {
            // TODO
        }

        /// <summary>
        /// Pushes the TextBox text change to the property when the user hits Enter.
        /// </summary>
        /// <remarks>From <see href="https://stackoverflow.com/a/13289118/294248"/>.  Set this
        /// as the TextBox's KeyUp event handler.</remarks>
        private void PathTextBox_KeyUp(object sender, KeyEventArgs e) {
            if (e.Key == Key.Enter) {
                TextBox tBox = (TextBox)sender;
                DependencyProperty prop = TextBox.TextProperty;

                BindingExpression? binding = BindingOperations.GetBindingExpression(tBox, prop);
                if (binding != null) {
                    binding.UpdateSource();
                }
            }
        }

        /// <summary>
        /// Navigates up to the parent directory.
        /// </summary>
        private void NavUp_Click(object sender, RoutedEventArgs e) {
            try {
                // Use GetParent() rather than simply shaving off a component so that we don't
                // move past the root, which could be a fixed disk like "C:\" or a network
                // location like "\\webby\fadden".
                DirectoryInfo? parentInfo = Directory.GetParent(PathNameText);
                if (parentInfo == null) {
                    return;
                }
                PathNameText = parentInfo.FullName;
            } catch (Exception ex) {
                Debug.WriteLine("GetParent threw: " + ex.Message);
            }
        }

        /// <summary>
        /// Refreshes the special list and the file list.  The former is only useful for
        /// removable media.
        /// </summary>
        private void Refresh_Click(object sender, RoutedEventArgs e) {
            LoadSpecialPaths();
            PathChanged();
        }

        /// <summary>
        /// Handles a change to the special path list selection.
        /// </summary>
        private void SpecialPathList_SelectionChanged(object sender, SelectionChangedEventArgs e) {
            if (mConfiguringPanel) {
                // If we're here because the path changed and we're programmatically updating
                // the selection, don't do further processing.
                return;
            }

            // Switch to the new path, if it exists (which it should).
            ListItem? item = (ListItem?)((DataGrid)sender).SelectedItem;
            if (item == null) {
                return;
            }
            string newPath = item.PathName;
            if (Directory.Exists(newPath)) {
                PathNameText = newPath;
            } else {
                Debug.WriteLine("Unable to find path '" + newPath + "'");
            }
        }

        /// <summary>
        /// Handles a change to the file list selection.
        /// </summary>
        private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e) {
            if (SelectionMode == SelMode.SingleFolder) {
                // We're going to use the current folder, which could be empty.  The choice
                // of selected item doesn't matter.  We only disable the OK button if there
                // was an error, e.g. the path doesn't exist.
                IsValid = true;
                return;
            }

            DataGrid grid = (DataGrid)sender;
            if (grid.SelectedItems.Count == 0) {
                // Nothing is selected, disable OK button.
                IsValid = false;
                return;
            }
            // TODO? consider doing a permission check on file creation if we're in
            // folder-selection mode... https://stackoverflow.com/q/1281620/294248

            if (SelectionMode == SelMode.SingleFile) {
                // Make sure at least one non-directory item is selected.
                bool foundFile = false;
                foreach (object oitem in grid.SelectedItems) {
                    ListItem item = (ListItem)oitem;
                    if (!item.IsFolder) {
                        foundFile = true;
                        break;
                    }
                }
                if (!foundFile) {
                    // Only directories are selected, disable OK button.
                    IsValid = false;
                    return;
                }
            }

            IsValid = true;
        }

        /// <summary>
        /// Handles a double-click on the file list.
        /// </summary>
        private void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e) {
            DataGrid grid = (DataGrid)sender;
            if (!grid.GetClickRowColItem(e, out int row, out int col, out object? citem)) {
                // Header or empty area; ignore.
                return;
            }
            ListItem item = (ListItem)citem;

            Debug.WriteLine("Double-click on '" + item.PathName + "'");
            if (fileListDataGrid.SelectedItems.Count == 0) {
                // Nothing selected, nothing to do.
            } else if (fileListDataGrid.SelectedItems.Count == 1) {
                if (item.IsFolder) {
                    // Single folder double-clicked, move into it.
                    PathNameText = item.PathName;
                } else {
                    // Single file double-clicked, accept.
                    AcceptSelection();
                }
            } else {
                // Accept all.
                AcceptSelection();
            }
        }

        /// <summary>
        /// Attempts to accept the current selection as the result.  We can get here via a
        /// click of the OK button or a double-click.
        /// </summary>
        /// <remarks>
        /// The OK button should be disabled for invalid situations, but double-clicking stuff
        /// can still land here.
        /// </remarks>
        private void AcceptSelection() {
            BasePath = Path.GetFullPath(PathNameText);      // normalize
            if (SelectionMode == SelMode.SingleFolder) {
                DialogResult = true;
                return;
            }

            IList gridList = fileListDataGrid.SelectedItems;
            if (gridList.Count == 0) {
                Debug.WriteLine("Cannot accept: nothing selected");
                return;
            }

            if (SelectionMode == SelMode.SingleFile) {
                Debug.Assert(gridList.Count == 1);
                ListItem item = (ListItem)gridList[0]!;
                SelectedFiles = new string[] { Path.GetFileName(item.PathName) };
                SelectedPaths = new string[] { item.PathName };
                DialogResult = true;
                return;
            }

            SelectedFiles = new string[gridList.Count];
            SelectedPaths = new string[gridList.Count];
            for (int i = 0; i < gridList.Count; i++) {
                ListItem item = (ListItem)gridList[i]!;
                SelectedFiles[i] = Path.GetFileName(item.PathName);
                SelectedPaths[i] = item.PathName;
            }
            Debug.WriteLine("Accepted: base='" + BasePath + "', path count=" +
                SelectedPaths.Length);
            DialogResult = true;
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
            SpecialPathList.Clear();
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
                string displayPath = path;
                if (displayPath.EndsWith('\\')) {
                    displayPath = displayPath.Substring(0, path.Length - 1);
                }
                string displayName = di.VolumeLabel + " (" + displayPath + ")";
                SpecialPathList.Add(GenerateItem(path, displayName));
            }

            // TODO? add favorites / frequently-used folders
        }

        /// <summary>
        /// Generates a list item.
        /// </summary>
        /// <returns>The new item.</returns>
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
                bool isHidden = (attr & FileAttributes.Hidden) != 0;
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
                item =
                    new ListItem(path, displayName, isDirectory, isHidden, icon, modWhen, length);
            } catch (Exception ex) {
                Debug.WriteLine("Error processing '" + path + "': " + ex);
                item = new ListItem(path, displayName, false, false, null, DateTime.Now, -1);
            }
            return item;
        }

        private bool mConfiguringPanel = false;

        private void PathChanged() {
            Debug.WriteLine("Path is now '" + PathNameText + "'");
            foreach (ListItem specItem in SpecialPathList) {
                // Check to see if the current path equal to or a prefix of a special path.  This
                // is a little dodgy but it's not crucial that it work.  We just want to
                // change the selection in the special file pane when the user navigates into
                // a special directory.
                string prefixPath = specItem.PathName;
                if (!prefixPath.EndsWith(Path.DirectorySeparatorChar)) {
                    prefixPath += Path.DirectorySeparatorChar;
                }
                if (PathNameText.Equals(specItem.PathName,
                            StringComparison.InvariantCultureIgnoreCase) ||
                        PathNameText.StartsWith(prefixPath,
                            StringComparison.InvariantCultureIgnoreCase)) {
                    if (specialPathDataGrid.SelectedItem != specItem) {
                        mConfiguringPanel = true;
                        try {
                            Debug.WriteLine("Setting left column to " + specItem);
                            specialPathDataGrid.SelectedItem = specItem;
                        } finally {
                            mConfiguringPanel = false;
                        }
                    }
                    break;
                }
            }

            FileList.Clear();
            ErrorVisibility = Visibility.Collapsed;

            string[] entries;
            try {
                Mouse.OverrideCursor = Cursors.Wait;
                try {
                    switch (SelectionMode) {
                        case SelMode.SingleFolder:
                            //entries = Directory.GetDirectories(PathNameText);
                            //break;
                        case SelMode.SingleFile:
                        case SelMode.FilesAndFolders:
                            entries = Directory.GetFileSystemEntries(PathNameText);
                            break;
                        default:
                        throw new NotImplementedException();
                    }
                } catch (Exception ex) {
                    ShowError("Error: " + ex.Message);
                    return;
                }

                // Generate the items into a temporary list, and do the initial "default" sort.
                // (Can we convince the DataGrid to do this for us?)
                List<ListItem> tmpList = new List<ListItem>(entries.Length);
                foreach (string entry in entries) {
                    ListItem newItem = GenerateItem(entry);
                    if (!newItem.IsHidden) {
                        tmpList.Add(newItem);
                    }
                }
                tmpList.Sort(new SymbolsListComparer((int)SymbolsListComparer.SortField.Name, true));

                foreach (ListItem item in tmpList) {
                    FileList.Add(item);
                }
            } finally {
                Mouse.OverrideCursor = null;
            }

            if (fileListDataGrid.Items.Count > 0) {
                fileListDataGrid.SelectedIndex = 0;
                IsValid = true;
            } else {
                IsValid = (SelectionMode == SelMode.SingleFolder);
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
