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
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

using AppCommon;
using CommonUtil;
using cp2_wpf.WPFCommon;
using DiskArc;
using DiskArc.Multi;
using FileConv;

namespace cp2_wpf {
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window, INotifyPropertyChanged {
        private MainController mMainCtrl;

        // INotifyPropertyChanged
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// Call this when a notification-worthy property changes value.
        /// The CallerMemberName attribute puts the calling property's name in the first arg.
        /// </summary>
        /// <param name="propertyName">Name of property that changed.</param>
        private void OnPropertyChanged([CallerMemberName] string propertyName = "") {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// Version string, for display.
        /// </summary>
        public string ProgramVersionString {
            get { return GlobalAppVersion.AppVersion.ToString(); }
        }

        /// <summary>
        /// Which panel are we showing, launchPanel or mainPanel?
        /// </summary>
        public bool ShowLaunchPanel {
            get { return mShowLaunchPanel; }
            set {
                mShowLaunchPanel = value;
                OnPropertyChanged("LaunchPanelVisibility");
                OnPropertyChanged("MainPanelVisibility");
            }
        }
        private bool mShowLaunchPanel = true;

        /// <summary>
        /// Returns the visibility status of the launch panel.
        /// (Intended for use from XAML.)
        /// </summary>
        public Visibility LaunchPanelVisibility {
            get { return mShowLaunchPanel ? Visibility.Visible : Visibility.Collapsed; }
        }

        /// <summary>
        /// Returns the visibility status of the main panel.
        /// (Intended for use from XAML.)
        /// </summary>
        public Visibility MainPanelVisibility {
            get { return mShowLaunchPanel ? Visibility.Collapsed : Visibility.Visible; }
        }

        public double LeftPanelWidth {
            get { return mainTriptychPanel.ColumnDefinitions[0].ActualWidth; }
            set { mainTriptychPanel.ColumnDefinitions[0].Width = new GridLength(value); }
        }
        public double WorkTreePanelHeightRatio {
            get {
                // Recording the actual pixel height is very hard, because of the grid
                // measurement system and because the total panel height changes after
                // certain events.  Instead, we record the ratio between the heights.
                double ratio = leftPanel.RowDefinitions[0].ActualHeight /
                    leftPanel.RowDefinitions[2].ActualHeight;
                return ratio * 1000;
            }
            set {
                // If you set the height to a pixel value, you lose the auto-sizing behavior,
                // and the splitter will happily shove the bottom panel off the bottom of the
                // main window.  The trick is to use "star" units.
                // Thanks: https://stackoverflow.com/q/35000893/294248
                double ratio = value / 1000.0;
                leftPanel.RowDefinitions[0].Height = new GridLength(ratio, GridUnitType.Star);
                leftPanel.RowDefinitions[2].Height = new GridLength(1.0, GridUnitType.Star);
            }
        }

        //
        // Recent-used file tracking.
        //

        public bool ShowRecentFile1 {
            get { return !string.IsNullOrEmpty(mRecentFileName1); }
        }
        public string RecentFileName1 {
            get { return mRecentFileName1; }
            set { mRecentFileName1 = value; OnPropertyChanged();
                OnPropertyChanged("ShowRecentFile1"); }
        }
        public string RecentFilePath1 {
            get { return mRecentFilePath1; }
            set { mRecentFilePath1 = value; OnPropertyChanged(); }
        }
        private string mRecentFileName1 = string.Empty;
        private string mRecentFilePath1 = string.Empty;

        public bool ShowRecentFile2 {
            get { return !string.IsNullOrEmpty(mRecentFileName2); }
        }
        public string RecentFileName2 {
            get { return mRecentFileName2; }
            set {
                mRecentFileName2 = value;
                OnPropertyChanged();
                OnPropertyChanged("ShowRecentFile2");
            }
        }
        public string RecentFilePath2 {
            get { return mRecentFilePath2; }
            set { mRecentFilePath2 = value; OnPropertyChanged(); }
        }
        private string mRecentFileName2 = string.Empty;
        private string mRecentFilePath2 = string.Empty;

        public void UpdateRecentLinks() {
            List<string> pathList = mMainCtrl.RecentFilePaths;

            if (pathList.Count >= 1) {
                RecentFilePath1 = pathList[0];
                RecentFileName1 = Path.GetFileName(pathList[0]);
            }
            if (pathList.Count >= 2) {
                RecentFilePath2 = pathList[1];
                RecentFileName2 = Path.GetFileName(pathList[1]);
            }
        }

        /// <summary>
        /// Generates the list of recently-opened files for the "open recent" menu.
        /// </summary>
        private void RecentFilesMenu_SubmenuOpened(object sender, RoutedEventArgs e) {
            MenuItem recents = (MenuItem)sender;
            recents.Items.Clear();

            if (mMainCtrl.RecentFilePaths.Count == 0) {
                MenuItem mi = new MenuItem();
                mi.Header = "(none)";
                recents.Items.Add(mi);
            } else {
                for (int i = 0; i < mMainCtrl.RecentFilePaths.Count; i++) {
                    MenuItem mi = new MenuItem();
                    mi.Header = EscapeMenuString(string.Format("{0}: {1}", i + 1,
                        mMainCtrl.RecentFilePaths[i]));
                    mi.Command = recentFileCmd.Command;
                    mi.CommandParameter = i;
                    recents.Items.Add(mi);
                }
            }
        }

        /// <summary>
        /// Escapes a string for use as a WPF menu item.
        /// </summary>
        private string EscapeMenuString(string instr) {
            return instr.Replace("_", "__");
        }

        /// <summary>
        /// Set to true if the DEBUG menu should be visible on the main menu strip.
        /// </summary>
        public bool ShowDebugMenu {
            get { return mShowDebugMenu; }
            set { mShowDebugMenu = value; OnPropertyChanged(); }
        }
        private bool mShowDebugMenu = false;


        /// <summary>
        /// Window constructor.
        /// </summary>
        public MainWindow() {
            Debug.WriteLine("START at " + DateTime.Now.ToLocalTime());

#if !DEBUG
            Crash.AppTag = "CiderPress2";
            Crash.AppIdent = "CiderPress II v" + GlobalAppVersion.AppVersion.ToString();
            AppDomain.CurrentDomain.UnhandledException +=
                new UnhandledExceptionEventHandler(Crash.CrashReporter);
#endif

            InitializeComponent();
            DataContext = this;

            mMainCtrl = new MainController(this);

            // Get an event when the splitters move.  Because of the way things are set up, it's
            // actually best to get an event when the grid row/column sizes change.
            // https://stackoverflow.com/a/22495586/294248
            DependencyPropertyDescriptor widthDesc = DependencyPropertyDescriptor.FromProperty(
                ColumnDefinition.WidthProperty, typeof(ItemsControl));
            DependencyPropertyDescriptor heightDesc = DependencyPropertyDescriptor.FromProperty(
                RowDefinition.HeightProperty, typeof(ItemsControl));
            // main window, left/right panels
            widthDesc.AddValueChanged(mainTriptychPanel.ColumnDefinitions[0], GridSizeChanged);
            widthDesc.AddValueChanged(mainTriptychPanel.ColumnDefinitions[4], GridSizeChanged);
            // vertical resize within side panels
            heightDesc.AddValueChanged(leftPanel.RowDefinitions[0], GridSizeChanged);

            // Add events that fire when column headers change size.  Set this up for
            // the FileList DataGrids.
            PropertyDescriptor pd = DependencyPropertyDescriptor.FromProperty(
                DataGridColumn.ActualWidthProperty, typeof(DataGridColumn));
            AddColumnWidthChangeCallback(pd, fileListDataGrid);

            // Hack to put the focus on the selected item in the file list DataGrid.
            fileListDataGrid.ItemContainerGenerator.StatusChanged +=
                ItemContainerGenerator_StatusChanged;

            // Populate the import/export mode selection controls
            InitImportExportConfig();
        }

        private void AddColumnWidthChangeCallback(PropertyDescriptor pd, DataGrid dg) {
            foreach (DataGridColumn col in dg.Columns) {
                pd.AddValueChanged(col, ColumnWidthChanged);
            }
        }

        /// <summary>
        /// Handles source-initialized event.  This happens before Loaded, before the window
        /// is visible, which makes it a good time to set the size and position.
        /// </summary>
        private void Window_SourceInitialized(object sender, EventArgs e) {
            mMainCtrl.WindowSourceInitialized();
        }

        /// <summary>
        /// Handles window-loaded event.  Window is ready to go, so we can start doing things
        /// that involve user interaction.
        /// </summary>
        private void Window_Loaded(object sender, RoutedEventArgs e) {
            mMainCtrl.WindowLoaded();
        }

        /// <summary>
        /// Handles window-close event.  The user has an opportunity to cancel.
        /// </summary>
        private void Window_Closing(object sender, CancelEventArgs e) {
            Debug.WriteLine("Main app window close requested");
            if (mMainCtrl == null) {
                // early failure?
                return;
            }
#if DEBUG
            // Give the library a chance to test assertions in finalizers.
            Debug.WriteLine("doing GC stuff");
            GC.Collect();
            GC.WaitForPendingFinalizers();
#endif
            if (!mMainCtrl.WindowClosing()) {
                e.Cancel = true;
            }
        }

        //
        // We record the location and size of the window, the sizes of the panels, and the
        // widths of the various columns.  These events may fire rapidly while the user is
        // resizing them, so we just want to set a flag noting that a change has been made.
        //
        private void Window_LocationChanged(object? sender, EventArgs e) {
            //Debug.WriteLine("Main window location changed");
            AppSettings.Global.IsDirty = true;
        }
        private void Window_SizeChanged(object? sender, SizeChangedEventArgs e) {
            //Debug.WriteLine("Main window size changed");
            AppSettings.Global.IsDirty = true;
        }
        private void GridSizeChanged(object? sender, EventArgs e) {
            //Debug.WriteLine("Grid size change: " + sender);
            AppSettings.Global.IsDirty = true;
        }
        private void ColumnWidthChanged(object? sender, EventArgs e) {
            DataGridTextColumn? col = sender as DataGridTextColumn;
            if (col != null) {
                //Debug.WriteLine("Column " + col.Header + " width now " + col.ActualWidth);
            }
            AppSettings.Global.IsDirty = true;
        }

        /// <summary>
        /// Grabs the widths of the columns and saves them in the global AppSettings.
        /// </summary>
        public void CaptureColumnWidths() {
            string widthStr;

            widthStr = CaptureColumnWidths(fileListDataGrid);
            AppSettings.Global.SetString(AppSettings.MAIN_FILE_COL_WIDTHS, widthStr);
        }
        private string CaptureColumnWidths(GridView gv) {
            int[] widths = new int[gv.Columns.Count];
            for (int i = 0; i < gv.Columns.Count; i++) {
                widths[i] = (int)Math.Round(gv.Columns[i].ActualWidth);
            }
            return StringToValue.SerializeIntArray(widths);
        }
        private string CaptureColumnWidths(DataGrid dg) {
            int[] widths = new int[dg.Columns.Count];
            for (int i = 0; i < dg.Columns.Count; i++) {
                widths[i] = (int)Math.Round(dg.Columns[i].ActualWidth);
            }
            return StringToValue.SerializeIntArray(widths);
        }

        /// <summary>
        /// Applies column widths from the global AppSettings.
        /// </summary>
        public void RestoreColumnWidths() {
            RestoreColumnWidths(fileListDataGrid,
                AppSettings.Global.GetString(AppSettings.MAIN_FILE_COL_WIDTHS, string.Empty));
        }
        private void RestoreColumnWidths(GridView gv, string str) {
            int[] widths;
            try {
                widths = StringToValue.DeserializeIntArray(str);
            } catch (Exception ex) {
                Debug.WriteLine("Unable to deserialize widths for GridView: " + ex.Message);
                return;
            }
            if (widths.Length != gv.Columns.Count) {
                Debug.WriteLine("Incorrect column count for GridView");
                return;
            }

            for (int i = 0; i < widths.Length; i++) {
                gv.Columns[i].Width = widths[i];
            }
        }
        private void RestoreColumnWidths(DataGrid dg, string str) {
            int[] widths;
            try {
                widths = StringToValue.DeserializeIntArray(str);
            } catch (Exception ex) {
                Debug.WriteLine("Unable to deserialize widths for " + dg.Name + ": " + ex.Message);
                return;
            }
            if (widths.Length != dg.Columns.Count) {
                Debug.WriteLine("Incorrect column count for " + dg.Name);
                return;
            }

            for (int i = 0; i < widths.Length; i++) {
                dg.Columns[i].Width = widths[i];
            }
        }

        #region Can-execute handlers

        private void IsFileOpen(object sender, CanExecuteRoutedEventArgs e) {
            e.CanExecute = (mMainCtrl != null && mMainCtrl.IsFileOpen);
        }

        private void IsWritableSingleEntrySelected(object sender, CanExecuteRoutedEventArgs e) {
            e.CanExecute = (mMainCtrl != null && mMainCtrl.IsFileOpen && mMainCtrl.CanWrite &&
                mMainCtrl.IsSingleEntrySelected);
        }
        private void AreFileEntriesSelected(object sender, CanExecuteRoutedEventArgs e) {
            e.CanExecute = (mMainCtrl != null && mMainCtrl.IsFileOpen && ShowCenterFileList &&
                mMainCtrl.AreFileEntriesSelected);
        }
        private void IsSubTreeSelected(object sender, CanExecuteRoutedEventArgs e) {
            e.CanExecute = (mMainCtrl != null && mMainCtrl.IsFileOpen &&
                mMainCtrl.IsClosableTreeSelected);
        }
        private void CanCreateDirectory(object sender, CanExecuteRoutedEventArgs e) {
            e.CanExecute = (mMainCtrl != null && mMainCtrl.IsFileOpen && mMainCtrl.CanWrite &&
                mMainCtrl.IsHierarchicalFileSystemSelected);
        }
        private void CanNavToParent(object sender, CanExecuteRoutedEventArgs e) {
            e.CanExecute = (mMainCtrl != null && mMainCtrl.IsFileOpen &&
                ((mMainCtrl.IsHierarchicalFileSystemSelected && !mMainCtrl.IsSelectedDirRoot) ||
                !mMainCtrl.IsSelectedArchiveRoot));
        }
        private void CanNavToParentDir(object sender, CanExecuteRoutedEventArgs e) {
            e.CanExecute = (mMainCtrl != null && mMainCtrl.IsFileOpen &&
                mMainCtrl.IsHierarchicalFileSystemSelected && !mMainCtrl.IsSelectedDirRoot);
        }

        private void CanAddFiles(object sender, CanExecuteRoutedEventArgs e) {
            e.CanExecute = (mMainCtrl != null && mMainCtrl.IsFileOpen && mMainCtrl.CanWrite &&
                mMainCtrl.IsMultiFileItemSelected && ShowCenterFileList);
        }
        private void CanDefragment(object sender, CanExecuteRoutedEventArgs e) {
            // Technically we don't need the filesystem to be writable; rather, we need the disk
            // image or partition that holds it to be writable.  But a filesystem should only be
            // read-only if the continer is read-only or there are errors that render it dubious,
            // either of which will cause the defragmenter to refuse to run.
            e.CanExecute = (mMainCtrl != null && mMainCtrl.IsFileOpen && mMainCtrl.CanWrite &&
                mMainCtrl.IsDefragmentableSelected);
        }
        private void CanDeleteFiles(object sender, CanExecuteRoutedEventArgs e) {
            e.CanExecute = (mMainCtrl != null && mMainCtrl.IsFileOpen && mMainCtrl.CanWrite &&
                mMainCtrl.IsMultiFileItemSelected && ShowCenterFileList &&
                mMainCtrl.AreFileEntriesSelected);
        }
        private void CanEditBlocks(object sender, CanExecuteRoutedEventArgs e) {
            e.CanExecute = (mMainCtrl != null && mMainCtrl.IsFileOpen &&
                mMainCtrl.CanEditBlocks);
        }
        private void CanEditSectors(object sender, CanExecuteRoutedEventArgs e) {
            e.CanExecute = (mMainCtrl != null && mMainCtrl.IsFileOpen &&
                mMainCtrl.CanEditSectors);
        }
        private void IsDiskImageSelected(object sender, CanExecuteRoutedEventArgs e) {
            e.CanExecute = (mMainCtrl != null && mMainCtrl.IsFileOpen &&
                mMainCtrl.IsDiskImageSelected);
        }
        private void IsChunkyDiskOrPartitionSelected(object sender, CanExecuteRoutedEventArgs e) {
            e.CanExecute = (mMainCtrl != null && mMainCtrl.IsFileOpen &&
                mMainCtrl.IsDiskOrPartitionSelected && mMainCtrl.HasChunks);
        }
        private void IsWritablePartitionSelected(object sender, CanExecuteRoutedEventArgs e) {
            e.CanExecute = (mMainCtrl != null && mMainCtrl.IsFileOpen &&
                mMainCtrl.CanWrite && mMainCtrl.IsPartitionSelected);
        }
        private void IsNibbleImageSelected(object sender, CanExecuteRoutedEventArgs e) {
            e.CanExecute = (mMainCtrl != null && mMainCtrl.IsFileOpen &&
                mMainCtrl.IsNibbleImageSelected);
        }
        private void IsFileSystemSelected(object sender, CanExecuteRoutedEventArgs e) {
            e.CanExecute = (mMainCtrl != null && mMainCtrl.IsFileOpen &&
                mMainCtrl.IsFileSystemSelected);
        }
        private void IsEditableFileSystemSelected(object sender, CanExecuteRoutedEventArgs e) {
            e.CanExecute = (mMainCtrl != null && mMainCtrl.IsFileOpen &&
                mMainCtrl.IsFileSystemSelected && mMainCtrl.CanWrite);
        }

        #endregion Can-execute handlers

        #region Command handlers

        private void AboutCmd_Executed(object sender, ExecutedRoutedEventArgs e) {
            mMainCtrl.HelpAbout();
        }
        private void AddFilesCmd_Executed(object sender, ExecutedRoutedEventArgs e) {
            mMainCtrl.AddFiles();
        }
        private void CloseCmd_Executed(object sender, ExecutedRoutedEventArgs e) {
            if (!mMainCtrl.CloseWorkFile()) {
                Debug.WriteLine("Close cancelled");
            }
        }
        private void CloseSubTreeCmd_Executed(object sender, ExecutedRoutedEventArgs e) {
            mMainCtrl.CloseSubTree();
        }
        private void CopyCmd_Executed(object sender, ExecutedRoutedEventArgs e) {
            Debug.WriteLine("Copy!");
        }
        private void CreateDirectoryCmd_Executed(object sender, ExecutedRoutedEventArgs e) {
            mMainCtrl.CreateDirectory();
        }
        private void CutCmd_Executed(object sender, ExecutedRoutedEventArgs e) {
            Debug.WriteLine("Cut!");
        }
        private void DefragmentCmd_Executed(object sender, ExecutedRoutedEventArgs e) {
            mMainCtrl.Defragment();
        }
        private void DeleteFilesCmd_Executed(object sender, ExecutedRoutedEventArgs e) {
            mMainCtrl.DeleteFiles();
        }
        private void EditAppSettingsCmd_Executed(object sender, ExecutedRoutedEventArgs e) {
            mMainCtrl.EditAppSettings();
        }
        private void EditAttributesCmd_Executed(object sender, ExecutedRoutedEventArgs e) {
            mMainCtrl.EditAttributes();
        }
        private void EditBlocksCmd_Executed(object sender, ExecutedRoutedEventArgs e) {
            mMainCtrl.EditBlocksSectors(false);
        }
        private void EditDirAttributesCmd_Executed(object sender, ExecutedRoutedEventArgs e) {
            mMainCtrl.EditDirAttributes();
        }
        private void EditSectorsCmd_Executed(object sender, ExecutedRoutedEventArgs e) {
            mMainCtrl.EditBlocksSectors(true);
        }
        private void ExitCmd_Executed(object sender, ExecutedRoutedEventArgs e) {
            // Close the main window.  This operation can be cancelled by the user.
            Close();
        }
        private void ExportFilesCmd_Executed(object sender, ExecutedRoutedEventArgs e) {
            mMainCtrl.ExportFiles();
        }
        private void ExtractFilesCmd_Executed(object sender, ExecutedRoutedEventArgs e) {
            mMainCtrl.ExtractFiles();
        }
        private void HelpCmd_Executed(object sender, ExecutedRoutedEventArgs e) {
            mMainCtrl.HelpHelp();
        }
        private void ImportFilesCmd_Executed(object sender, ExecutedRoutedEventArgs e) {
            mMainCtrl.ImportFiles();
        }
        private void NavToParentCmd_Executed(object sender, ExecutedRoutedEventArgs e) {
            mMainCtrl.NavToParent(false);
        }
        private void NavToParentDirCmd_Executed(object sender, ExecutedRoutedEventArgs e) {
            mMainCtrl.NavToParent(true);
        }
        private void NewDiskImageCmd_Executed(object sender, ExecutedRoutedEventArgs e) {
            mMainCtrl.NewDiskImage();
        }
        private void NewFileArchiveCmd_Executed(object sender, ExecutedRoutedEventArgs e) {
            mMainCtrl.NewFileArchive();
        }
        private void OpenCmd_Executed(object sender, ExecutedRoutedEventArgs e) {
            mMainCtrl.OpenWorkFile();
        }
        private void OpenPhysicalDriveCmd_Executed(object sender, ExecutedRoutedEventArgs e) {
            mMainCtrl.OpenPhysicalDrive();
        }
        private void ReplacePartitionCmd_Executed(object sender, ExecutedRoutedEventArgs e) {
            mMainCtrl.ReplacePartition();
        }
        private void PasteCmd_Executed(object sender, ExecutedRoutedEventArgs e) {
            Debug.WriteLine("Paste!");
        }
        private void RecentFileCmd_Executed(object sender, ExecutedRoutedEventArgs e) {
            int recentIndex;
            if (e.Parameter is int) {
                recentIndex = (int)e.Parameter;
            } else if (e.Parameter is string) {
                recentIndex = int.Parse((string)e.Parameter);
            } else {
                throw new Exception("Bad parameter: " + e.Parameter);
            }
            if (recentIndex < 0 || recentIndex >= MainController.MAX_RECENT_FILES) {
                throw new Exception("Bad parameter: " + e.Parameter);
            }

            Debug.WriteLine("Recent project #" + recentIndex);
            mMainCtrl.OpenRecentFile(recentIndex);
        }
        private void ResetSortCmd_Executed(object sender, ExecutedRoutedEventArgs e) {
            fileListDataGrid.ResetSort();
        }
        private void SaveAsDiskImageCmd_Executed(Object sender, ExecutedRoutedEventArgs e) {
            mMainCtrl.SaveAsDiskImage();
        }
        private void ScanForBadBlocksCmd_Executed(object sender, ExecutedRoutedEventArgs e) {
            mMainCtrl.ScanForBadBlocks();
        }
        private void ScanForSubVolCmd_Executed(object sender, ExecutedRoutedEventArgs e) {
            mMainCtrl.ScanForSubVol();
        }
        private void ShowDirListCmd_Executed(object sender, ExecutedRoutedEventArgs e) {
            PreferSingleDirList = true;
            if (!ShowSingleDirFileList) {
                ShowSingleDirFileList = true;
                // NOTE: passing "true" to set the focus in the file list causes a weird bug
                // where all commands are disabled until something else is clicked.
                // Repro steps: (1) open ProDOS or HFS disk, (2) click toolbar buttons
                // { info, dir list, full list } in sequence twice.  Should lock out all
                // actions on second time through.  No idea why this is happening, but we
                // don't need to "hard shift" the focus here.
                mMainCtrl.PopulateFileList(IFileEntry.NO_ENTRY, false);
                // TODO: reset sort order, in case filename/pathname selected?
            }
            SetShowCenterInfo(CenterPanelChange.Files);
        }
        private void ShowFullListCmd_Executed(object sender, ExecutedRoutedEventArgs e) {
            PreferSingleDirList = false;
            if (ShowSingleDirFileList) {
                ShowSingleDirFileList = false;
                mMainCtrl.PopulateFileList(IFileEntry.NO_ENTRY, false);
                // TODO: reset sort order, in case filename/pathname selected?
            }
            SetShowCenterInfo(CenterPanelChange.Files);
        }
        private void ShowInfoCmd_Executed(object sender, ExecutedRoutedEventArgs e) {
            SetShowCenterInfo(CenterPanelChange.Info);
        }
        private void TestFilesCmd_Executed(object sender, ExecutedRoutedEventArgs e) {
            mMainCtrl.TestFiles();
        }
        private void ToggleInfoCmd_Executed(object sender, ExecutedRoutedEventArgs e) {
            SetShowCenterInfo(CenterPanelChange.Toggle);
        }
        private void ViewFilesCmd_Executed(object sender, ExecutedRoutedEventArgs e) {
            mMainCtrl.ViewFiles();
        }

        private void Debug_BulkCompressTestCmd_Executed(object sender, ExecutedRoutedEventArgs e) {
            mMainCtrl.Debug_BulkCompressTest();
        }
        private void Debug_DiskArcLibTestCmd_Executed(object sender, ExecutedRoutedEventArgs e) {
            mMainCtrl.Debug_DiskArcLibTests();
        }
        private void Debug_FileConvLibTestCmd_Executed(object sender, ExecutedRoutedEventArgs e) {
            mMainCtrl.Debug_FileConvLibTests();
        }
        private void Debug_ShowDebugLogCmd_Executed(object sender, ExecutedRoutedEventArgs e) {
            mMainCtrl.Debug_ShowDebugLog();
        }

        #endregion Command handlers

        #region Archive Tree

        /// <summary>
        /// Data for archive tree TreeView control.  This is a list of items, not a single item.
        /// </summary>
        public ObservableCollection<ArchiveTreeItem> ArchiveTreeRoot { get; private set; } =
            new ObservableCollection<ArchiveTreeItem>();

        /// <summary>
        /// Currently-selected item in the archive (work) TreeView, or null if nothing selected.
        /// </summary>
        public ArchiveTreeItem? SelectedArchiveTreeItem {
            get { return (ArchiveTreeItem?)archiveTree.SelectedItem; }
        }

        /// <summary>
        /// Handles selection change in archive tree view.
        /// </summary>
        private void ArchiveTree_SelectedItemChanged(object sender,
                RoutedPropertyChangedEventArgs<object> e) {
            ArchiveTreeItem? item = e.NewValue as ArchiveTreeItem;
            mMainCtrl.ArchiveTree_SelectionChanged(item);
        }

        #endregion Archive Tree

        #region Directory Tree

        /// <summary>
        /// Data for directory tree TreeView control.
        /// </summary>
        public ObservableCollection<DirectoryTreeItem> DirectoryTreeRoot { get; private set; } =
            new ObservableCollection<DirectoryTreeItem>();

        /// <summary>
        /// Currently-selected item in the directory TreeView, or null if nothing selected.
        /// </summary>
        public DirectoryTreeItem? SelectedDirectoryTreeItem {
            get { return (DirectoryTreeItem?)directoryTree.SelectedItem; }
        }

        /// <summary>
        /// Handles selection change in directory tree view.
        /// </summary>
        private void DirectoryTree_SelectedItemChanged(object sender,
                RoutedPropertyChangedEventArgs<object> e) {
            DirectoryTreeItem? item = e.NewValue as DirectoryTreeItem;
            mMainCtrl.DirectoryTree_SelectionChanged(item);
        }

        #endregion Directory Tree

        #region Center Panel

        // This determines whether we're showing a file list or the info panel.  It does not
        // affect the contents of the file list (full or dir).
        public enum CenterPanelChange { Unknown = 0, Files, Info, Toggle }
        public bool ShowCenterFileList { get { return !mShowCenterInfo; } }
        public bool ShowCenterInfoPanel { get { return mShowCenterInfo; } }
        private void SetShowCenterInfo(CenterPanelChange req) {
            if (HasInfoOnly && req != CenterPanelChange.Info) {
                Debug.WriteLine("Ignoring attempt to switch to file list");
                return;
            }
            switch (req) {
                case CenterPanelChange.Info:
                    mShowCenterInfo = true;
                    break;
                case CenterPanelChange.Files:
                    mShowCenterInfo = false;
                    break;
                case CenterPanelChange.Toggle:
                    mShowCenterInfo = !mShowCenterInfo;
                    break;
            }
            OnPropertyChanged("ShowCenterFileList");
            OnPropertyChanged("ShowCenterInfoPanel");
        }
        private bool mShowCenterInfo;

        // Enable or disable the toolbar buttons.
        public bool IsFullListEnabled {
            get { return mIsFullListEnabled; }
            set { mIsFullListEnabled = value; OnPropertyChanged(); }
        }
        private bool mIsFullListEnabled;
        public bool IsDirListEnabled {
            get { return mIsDirListEnabled; }
            set { mIsDirListEnabled = value; OnPropertyChanged(); }
        }
        private bool mIsDirListEnabled;

        /// <summary>
        /// <para>This determines whether we populate the file list with the full set of files
        /// or just those in the current directory.</para>
        /// </summary>
        public bool ShowSingleDirFileList {
            get { return mShowSingleDirFileList; }
            set { mShowSingleDirFileList = ShowCol_FileName = value; ShowCol_PathName = !value; }
        }
        private bool mShowSingleDirFileList;

        /// <summary>
        /// Remember if we prefer single-dir or full-file view for hierarchical filesystems.
        /// </summary>
        private bool PreferSingleDirList {
            get {
                return AppSettings.Global.GetBool(AppSettings.FILE_LIST_PREFER_SINGLE, true);
            }
            set {
                AppSettings.Global.SetBool(AppSettings.FILE_LIST_PREFER_SINGLE, value);
            }
        }

        /// <summary>
        /// True if the current display doesn't have a file list.
        /// </summary>
        private bool HasInfoOnly { get; set; }

        /// <summary>
        /// Configures column visibility based on what kind of data we're showing.  Call this
        /// after <see cref="SetCenterPanelContents"/>.
        /// </summary>
        /// <param name="isInfoOnly">Are there no files to display?</param>
        /// <param name="isArchive">Is this a file archive?</param>
        /// <param name="isHierarchic">Is the file list hierarchical?</param>
        /// <param name="hasRsrc">Does this have resource forks?</param>
        /// <param name="hasRaw">Does this have "raw" files (DOS 3.x)?</param>
        public void ConfigureCenterPanel(bool isInfoOnly, bool isArchive, bool isHierarchic,
                bool hasRsrc, bool hasRaw) {
            // We show the PathName column for file archives, and for hierarchical filesystems
            // if the user has requested a full-file list.  Show FileName for non-hierarchical
            // filesystems and for the directory list.
            ShowSingleDirFileList = !(isArchive || (isHierarchic && !PreferSingleDirList));
            HasInfoOnly = isInfoOnly;
            if (HasInfoOnly) {
                SetShowCenterInfo(CenterPanelChange.Info);
            } else {
                SetShowCenterInfo(CenterPanelChange.Files);
            }

            if (isInfoOnly) {
                IsFullListEnabled = IsDirListEnabled = false;
            } else if (isArchive) {
                IsFullListEnabled = true;
                IsDirListEnabled = false;
                Debug.Assert(!ShowSingleDirFileList);
            } else if (isHierarchic) {
                IsFullListEnabled = IsDirListEnabled = true;
                Debug.Assert(ShowSingleDirFileList == PreferSingleDirList);
            } else {
                IsFullListEnabled = false;
                IsDirListEnabled = true;
                Debug.Assert(ShowSingleDirFileList);
            }

            ShowCol_Format = isArchive;
            ShowCol_RawLen = hasRaw;
            ShowCol_RsrcLen = hasRsrc;
            ShowCol_TotalSize = !isArchive;
        }
        public void ReconfigureCenterPanel(bool hasRsrc) {
            ShowCol_RsrcLen = hasRsrc;
        }
        public bool ShowCol_FileName {
            get { return mShowCol_FileName; }
            set { mShowCol_FileName = value; OnPropertyChanged(); }
        }
        private bool mShowCol_FileName;
        public bool ShowCol_Format {
            get { return mShowCol_Format; }
            set { mShowCol_Format = value; OnPropertyChanged(); }
        }
        private bool mShowCol_Format;
        public bool ShowCol_PathName {
            get { return mShowCol_PathName; }
            set { mShowCol_PathName = value; OnPropertyChanged(); }
        }
        private bool mShowCol_PathName;
        public bool ShowCol_RawLen {
            get { return mShowCol_RawLen; }
            set { mShowCol_RawLen = value; OnPropertyChanged(); }
        }
        private bool mShowCol_RawLen;
        public bool ShowCol_RsrcLen {
            get { return mShowCol_RsrcLen; }
            set { mShowCol_RsrcLen = value; OnPropertyChanged(); }
        }
        private bool mShowCol_RsrcLen;
        public bool ShowCol_TotalSize {
            get { return mShowCol_TotalSize; }
            set { mShowCol_TotalSize = value; OnPropertyChanged(); }
        }
        private bool mShowCol_TotalSize;

        public ObservableCollection<FileListItem> FileList {
            get { return mFileList; }
            internal set { mFileList = value; OnPropertyChanged(); }
        }
        private ObservableCollection<FileListItem> mFileList =
            new ObservableCollection<FileListItem>();

        public FileListItem? SelectedFileListItem {
            get {
                return (FileListItem?)fileListDataGrid.SelectedItem;
            }
            set {
                fileListDataGrid.SelectedItem = value;
            }
        }

        public string CenterInfoText1 {
            get { return mCenterInfoText1; }
            set { mCenterInfoText1 = value; OnPropertyChanged(); }
        }
        private string mCenterInfoText1 = string.Empty;
        public string CenterInfoText2 {
            get { return mCenterInfoText2; }
            set { mCenterInfoText2 = value; OnPropertyChanged(); }
        }
        private string mCenterInfoText2 = string.Empty;

        /// <summary>
        /// Clears the partition, notes, and metadata lists displayed on the center panel.
        /// </summary>
        public void ClearCenterInfo() {
            ShowDiskUtilityButtons = false;

            PartitionList.Clear();
            ShowPartitionLayout = false;
            NotesList.Clear();
            ShowNotes = false;

            MetadataList.Clear();
            ShowMetadata = false;
        }

        public bool ShowDiskUtilityButtons {
            get { return mShowDiskUtilityButtons; }
            set { mShowDiskUtilityButtons = value; OnPropertyChanged(); }
        }
        private bool mShowDiskUtilityButtons;

        public bool ShowPartitionLayout {
            get { return mShowPartitionLayout; }
            set { mShowPartitionLayout = value; OnPropertyChanged(); }
        }
        private bool mShowPartitionLayout;
        public class PartitionListItem {
            public int Index { get; private set; }
            public long StartBlock { get; private set; }
            public long BlockCount { get; private set; }
            public string PartName { get; private set; }
            public string PartType { get; private set; }

            public Partition PartRef { get; private set; }

            public PartitionListItem(int index, Partition part) {
                PartRef = part;

                string name = string.Empty;
                string type = string.Empty;
                if (part is APM_Partition) {
                    name = ((APM_Partition)part).PartitionName;
                    type = ((APM_Partition)part).PartitionType;
                } else if (part is FocusDrive_Partition) {
                    name = ((FocusDrive_Partition)part).PartitionName;
                }

                Index = index;
                StartBlock = part.StartOffset / Defs.BLOCK_SIZE;
                BlockCount = part.Length / Defs.BLOCK_SIZE;
                PartName = name;
                PartType = type;
            }
            public override string ToString() {
                return "[Part: start=" + StartBlock + " count=" + BlockCount + " name=" +
                    PartName + "]";
            }
        }
        public ObservableCollection<PartitionListItem> PartitionList { get; } =
            new ObservableCollection<PartitionListItem>();

        public bool ShowMetadata {
            get { return mShowMetadata; }
            set { mShowMetadata = value; OnPropertyChanged(); }
        }
        private bool mShowMetadata;
        public bool CanAddMetadataEntry {
            get { return mCanAddMetadataEntry; }
            set { mCanAddMetadataEntry = value; OnPropertyChanged(); }
        }
        private bool mCanAddMetadataEntry;

        public class MetadataItem : INotifyPropertyChanged {
            public string Key { get; private set; }
            public string Value { get; private set; }
            public string? Description { get; private set; }
            public string? ValueSyntax { get; private set; }
            public bool CanEdit { get; private set; }

            public MetadataItem(string key, string value, string description,
                    string valueSyntax, bool canEdit) {
                Key = key;
                Value = value;
                Description = string.IsNullOrEmpty(description) ? null : description;
                ValueSyntax = string.IsNullOrEmpty(valueSyntax) ? null : valueSyntax;
                CanEdit = canEdit;
            }

            public event PropertyChangedEventHandler? PropertyChanged;
            public void SetValue(string value) {
                Value = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
            }
        }
        public ObservableCollection<MetadataItem> MetadataList { get; } =
            new ObservableCollection<MetadataItem>();

        /// <summary>
        /// Configures the metadata list, displayed on the info panel.
        /// </summary>
        public void SetMetadataList(IMetadata metaObj) {
            MetadataList.Clear();
            List<IMetadata.MetaEntry> entries = metaObj.GetMetaEntries();
            foreach (IMetadata.MetaEntry met in entries) {
                string? value = metaObj.GetMetaValue(met.Key, true);
                if (value == null) {
                    // Shouldn't be possible.
                    value = "!NOT FOUND!";
                }
                MetadataList.Add(new MetadataItem(met.Key, value, met.Description,
                    met.ValueSyntax, met.CanEdit));
            }
            ShowMetadata = true;
            CanAddMetadataEntry = metaObj.CanAddNewEntries;
        }

        public void UpdateMetadata(string key, string value) {
            foreach (MetadataItem item in MetadataList) {
                if (item.Key == key) {
                    item.SetValue(value);
                    break;
                }
            }
        }

        public void AddMetadata(IMetadata.MetaEntry met, string value) {
            MetadataList.Add(new MetadataItem(met.Key, value, met.Description,
                met.ValueSyntax, met.CanEdit));
        }

        public void RemoveMetadata(string key) {
            bool found = false;
            for (int i = 0; i < MetadataList.Count; i++) {
                MetadataItem item = MetadataList[i];
                if (item.Key == key) {
                    MetadataList.RemoveAt(i);
                    found = true;
                    break;
                }
            }
            Debug.Assert(found);
        }

        /// <summary>
        /// Handles a double-click on the metadata list.
        /// </summary>
        private void MetadataList_MouseDoubleClick(object sender, MouseButtonEventArgs e) {
            DataGrid grid = (DataGrid)sender;
            if (!grid.GetClickRowColItem(e, out int row, out int col, out object? item)) {
                // Header or empty area; ignore.
                return;
            }
            MetadataItem mdi = (MetadataItem)item;
            ArchiveTreeItem? arcTreeSel = archiveTree.SelectedItem as ArchiveTreeItem;
            if (arcTreeSel == null) {
                Debug.Assert(false, "archive tree is missing selection");
                return;
            }

            mMainCtrl.HandleMetadataDoubleClick(mdi, row, col);
        }

        private void Metadata_AddEntryButtonClick(object sender, RoutedEventArgs e) {
            mMainCtrl.HandleMetadataAddEntry();
        }

        /// <summary>
        /// Configures the partition list, displayed on the info panel.
        /// </summary>
        /// <param name="parts">Partition container.</param>
        public void SetPartitionList(IMultiPart parts) {
            PartitionList.Clear();
            for (int index = 0; index < parts.Count; index++) {
                Partition part = parts[index];
                PartitionListItem item =
                    new PartitionListItem(index + (WorkTree.ONE_BASED_INDEX ? 1 : 0), part);
                PartitionList.Add(item);
            }
            ShowPartitionLayout = (PartitionList.Count > 0);
        }

        public bool ShowNotes {
            get { return mShowNotes; }
            set { mShowNotes = value; OnPropertyChanged(); }
        }
        private bool mShowNotes;
        public ObservableCollection<Notes.Note> NotesList { get; } =
            new ObservableCollection<Notes.Note>();

        /// <summary>
        /// Sets the list of Notes to display on the center panel.
        /// </summary>
        public void SetNotesList(Notes notes) {
            NotesList.Clear();
            foreach (Notes.Note note in notes.GetNotes()) {
                NotesList.Add(note);
            }
            ShowNotes = (notes.Count > 0);
        }

        private void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e) {
            DataGrid grid = (DataGrid)sender;
            if (!grid.GetClickRowColItem(e, out int row, out int col, out object? item)) {
                // Header or empty area; ignore.
                return;
            }
            FileListItem fli = (FileListItem)item;

            ArchiveTreeItem? arcTreeSel = archiveTree.SelectedItem as ArchiveTreeItem;
            DirectoryTreeItem? dirTreeSel = directoryTree.SelectedItem as DirectoryTreeItem;
            if (arcTreeSel == null || dirTreeSel == null) {
                Debug.Assert(false, "tree is missing selection");
                return;
            }
            mMainCtrl.HandleFileListDoubleClick(fli, row, col, arcTreeSel, dirTreeSel);
        }

        // This is necessary because DataGrid eats the Delete key.  Usually.
        private void FileListDataGrid_PreviewKeyDown(object sender, KeyEventArgs e) {
            if (e.Key == Key.Delete) {
                Debug.WriteLine("Caught Delete in the center datagrid");
                // This event comes from the center DataGrid, so we can safely assume that a file
                // is open and the file list is visible.
                if (mMainCtrl.AreFileEntriesSelected) {
                    mMainCtrl.DeleteFiles();
                }
                e.Handled = true;       // consume
            }
        }

        private void FileListDataGrid_Drop(object sender, DragEventArgs e) {
            IFileEntry dropTarget = IFileEntry.NO_ENTRY;
            DataGrid grid = (DataGrid)sender;
            if (grid.GetDropRowColItem(e, out int row, out int col, out object? item)) {
                // Dropped on a specific item.  Do we want to do something with that fact?
                FileListItem fli = (FileListItem)item;
                dropTarget = fli.FileEntry;
            }
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                mMainCtrl.AddFileDrop(dropTarget, files);
            }
        }

        private void PartitionLayout_MouseDoubleClick(object sender, MouseButtonEventArgs e) {
            DataGrid grid = (DataGrid)sender;
            if (!grid.GetClickRowColItem(e, out int row, out int col, out object? item)) {
                // Header or empty area; ignore.
                return;
            }
            PartitionListItem pli = (PartitionListItem)item;

            ArchiveTreeItem? arcTreeSel = archiveTree.SelectedItem as ArchiveTreeItem;
            if (arcTreeSel == null) {
                Debug.Assert(false, "archive tree is missing selection");
                return;
            }
            mMainCtrl.HandlePartitionLayoutDoubleClick(pli, row, col, arcTreeSel);
        }

        /// <summary>
        /// Posts a notification that fades out after a few seconds.
        /// </summary>
        /// <param name="msg">Message to show.</param>
        public void PostNotification(string msg, bool success) {
            const int DURATION_SEC = 3;

            toastTextBlock.Text = msg;
            if (success) {
                toastBorder.BorderBrush = Brushes.Green;
            } else {
                toastBorder.BorderBrush = Brushes.Red;
            }

            // We could do this in XAML, but something this simple is easier to do here.
            //
            // Toggling the element's visibility isn't strictly necessary.  It matters for
            // mouse clicks, but we don't really want to receive those even when it's visible.
            // Setting IsHitTestVisible=False prevents clicks from registering.
            //
            // Updating the Visibility requires more than just collapsing at the end: if you do
            // two quick operations, the message gets hidden when the first animation completes.
            //toastMessage.Visibility = Visibility.Visible;
            DoubleAnimation doubAnim =
                new DoubleAnimation(1.0, 0.0, new Duration(TimeSpan.FromSeconds(DURATION_SEC)));
            //doubAnim.Completed += (sender, e) => {
            //    toastMessage.Visibility = Visibility.Collapsed;
            //};
            toastMessage.BeginAnimation(UIElement.OpacityProperty, doubAnim);

            mMainCtrl.AppHook.LogI("Show notification: '" + msg + "' success=" + success);
        }

        /// <summary>
        /// Sets the file list DataGrid's focus to the selected item.
        /// </summary>
        public void FileList_SetSelectionFocus() {
            mFileListRefocusNeeded = true;
        }

        private bool mFileListRefocusNeeded;

        /// <summary>
        /// Handles a status change in the file list DataGrid item container generator.  This
        /// is a hack to put the focus on the selected item.
        /// </summary>
        private void ItemContainerGenerator_StatusChanged(object? sender, EventArgs e) {
            if (!mFileListRefocusNeeded) {
                return;
            }
            //Debug.WriteLine("+ Status changed, refocus needed");
            if (fileListDataGrid.ItemContainerGenerator.Status ==
                    GeneratorStatus.ContainersGenerated) {
                int index = fileListDataGrid.SelectedIndex;
                if (index >= 0 && fileListDataGrid.SelectRowColAndFocus(index, 0)) {
                    // Sometimes this appears to work but actually doesn't.  The difference
                    // appears to be whether we were called as the result of ScrollIntoView()
                    // or as part of the framework layout code.  Only the latter works.  We
                    // should stop after 10 frames to avoid false-positives.
                    StackFrame[] frames = new StackTrace().GetFrames();
                    for (int i = 0; i < 10 && i < frames.Length; i++) {
                        System.Reflection.MethodBase? method = frames[i].GetMethod();
                        if (method != null && method.Name == "Measure" &&
                                method.DeclaringType == typeof(UIElement)) {
                            mFileListRefocusNeeded = false;
                            break;
                        }
                    }
                    if (mFileListRefocusNeeded) {
                        Debug.WriteLine("+ refocus successful, but wrong call trace");
                    } else {
                        Debug.WriteLine("+ refocus successful, index=" + index);
                    }
                }
            }
        }

        /// <summary>
        /// Handles the event that fires when the file list's context menu is opened.  We use
        /// this to detect the situation where focus has been lost entirely.
        /// </summary>
        /// <remarks>
        /// To test: create a disk image with one entry, then delete the entry.  The hack we
        /// use elsewhere won't work because there are no items, and hence no item container
        /// status changes.  The app focus ends up set to null, and the first keyboard event
        /// gets sent to the topmost element in the main window, which happens to be Menu.
        /// </remarks>
        private void FileListContextMenu_ContextMenuOpening(object sender, ContextMenuEventArgs e) {
            IInputElement? curFocus = FocusManager.GetFocusedElement(this);
            if (curFocus == null) {
                Debug.WriteLine("+ no focus found, shifting to file list (refocusNeeded=" +
                    mFileListRefocusNeeded + ")");
                fileListDataGrid.Focus();
            }
        }

        #endregion Center Panel

        #region Options Panel

        /// <summary>
        /// True if the options panel on the right side of the window should be fully visible.
        /// </summary>
        public bool ShowOptionsPanel {
            get { return mShowOptionsPanel; }
            set {
                mShowOptionsPanel = value;
                OnPropertyChanged();
                ShowHideRotation = value ? 0 : 90;
                OnPropertyChanged(nameof(ShowHideRotation));
            }
        }
        private bool mShowOptionsPanel = true;

        public double ShowHideRotation { get; private set; }

        private void ShowHideOptionsButton_Click(object sender, RoutedEventArgs e) {
            ShowOptionsPanel = !ShowOptionsPanel;
        }

        public bool IsChecked_AddCompress {
            get { return AppSettings.Global.GetBool(AppSettings.ADD_COMPRESS_ENABLED, true); }
            set {
                AppSettings.Global.SetBool(AppSettings.ADD_COMPRESS_ENABLED, value);
                OnPropertyChanged();
            }
        }
        public bool IsChecked_AddRaw {
            get { return AppSettings.Global.GetBool(AppSettings.ADD_RAW_ENABLED, false); }
            set {
                AppSettings.Global.SetBool(AppSettings.ADD_RAW_ENABLED, value);
                OnPropertyChanged();
            }
        }
        public bool IsChecked_AddRecurse {
            get { return AppSettings.Global.GetBool(AppSettings.ADD_RECURSE_ENABLED, true); }
            set {
                AppSettings.Global.SetBool(AppSettings.ADD_RECURSE_ENABLED, value);
                OnPropertyChanged();
            }
        }
        public bool IsChecked_AddStripExt {
            get { return AppSettings.Global.GetBool(AppSettings.ADD_STRIP_EXT_ENABLED, true); }
            set {
                AppSettings.Global.SetBool(AppSettings.ADD_STRIP_EXT_ENABLED, value);
                OnPropertyChanged();
            }
        }
        public bool IsChecked_AddStripPaths {
            get { return AppSettings.Global.GetBool(AppSettings.ADD_STRIP_PATHS_ENABLED, false); }
            set {
                AppSettings.Global.SetBool(AppSettings.ADD_STRIP_PATHS_ENABLED, value);
                OnPropertyChanged();
            }
        }

        public bool IsChecked_AddPreserveADF {
            get { return AppSettings.Global.GetBool(AppSettings.ADD_PRESERVE_ADF, true); }
            set {
                AppSettings.Global.SetBool(AppSettings.ADD_PRESERVE_ADF, value);
                OnPropertyChanged();
            }
        }
        public bool IsChecked_AddPreserveAS {
            get { return AppSettings.Global.GetBool(AppSettings.ADD_PRESERVE_AS, true); }
            set {
                AppSettings.Global.SetBool(AppSettings.ADD_PRESERVE_AS, value);
                OnPropertyChanged();
            }
        }
        public bool IsChecked_AddPreserveNAPS {
            get { return AppSettings.Global.GetBool(AppSettings.ADD_PRESERVE_NAPS, true); }
            set {
                AppSettings.Global.SetBool(AppSettings.ADD_PRESERVE_NAPS, value);
                OnPropertyChanged();
            }
        }

        public bool IsChecked_ExtRaw {
            get { return AppSettings.Global.GetBool(AppSettings.EXT_RAW_ENABLED, false); }
            set {
                AppSettings.Global.SetBool(AppSettings.EXT_RAW_ENABLED, value);
                OnPropertyChanged();
            }
        }
        public bool IsChecked_ExtStripPaths {
            get { return AppSettings.Global.GetBool(AppSettings.EXT_STRIP_PATHS_ENABLED, false); }
            set {
                AppSettings.Global.SetBool(AppSettings.EXT_STRIP_PATHS_ENABLED, value);
                OnPropertyChanged();
            }
        }
        public bool IsChecked_ExtPreserveADF {
            get {
                return (AppSettings.Global.GetEnum(AppSettings.EXT_PRESERVE_MODE,
                    ExtractFileWorker.PreserveMode.None) == ExtractFileWorker.PreserveMode.ADF);
            }
            set {
                if (value == true) {
                    AppSettings.Global.SetEnum(AppSettings.EXT_PRESERVE_MODE,
                        ExtractFileWorker.PreserveMode.ADF);
                }
                OnPropertyChanged();
            }
        }
        public bool IsChecked_ExtPreserveAS {
            get {
                return (AppSettings.Global.GetEnum(AppSettings.EXT_PRESERVE_MODE,
                    ExtractFileWorker.PreserveMode.None) == ExtractFileWorker.PreserveMode.AS);
            }
            set {
                if (value == true) {
                    AppSettings.Global.SetEnum(AppSettings.EXT_PRESERVE_MODE,
                        ExtractFileWorker.PreserveMode.AS);
                }
                OnPropertyChanged();
            }
        }
        public bool IsChecked_ExtPreserveNAPS {
            get {
                return (AppSettings.Global.GetEnum(AppSettings.EXT_PRESERVE_MODE,
                    ExtractFileWorker.PreserveMode.None) == ExtractFileWorker.PreserveMode.NAPS);
            }
            set {
                if (value == true) {
                    AppSettings.Global.SetEnum(AppSettings.EXT_PRESERVE_MODE,
                        ExtractFileWorker.PreserveMode.NAPS);
                }
                OnPropertyChanged();
            }
        }
        public bool IsChecked_ExtPreserveNone {
            get {
                return (AppSettings.Global.GetEnum(AppSettings.EXT_PRESERVE_MODE,
                    ExtractFileWorker.PreserveMode.None) == ExtractFileWorker.PreserveMode.None);
            }
            set {
                if (value == true) {
                    AppSettings.Global.SetEnum(AppSettings.EXT_PRESERVE_MODE,
                        ExtractFileWorker.PreserveMode.None);
                }
                OnPropertyChanged();
            }
        }

        public class ConvItem {
            public string Tag { get; private set; }
            public string Label { get; private set; }

            public ConvItem(string tag, string label) {
                Tag = tag;
                Label = label;
            }
        }

        public List<ConvItem> ImportConverters { get; } = new List<ConvItem>();
        public string ImportConvTag {
            get {
                return ((ConvItem)importCfgCombo.SelectedItem).Tag;
            }
            set {
                foreach (ConvItem item in ImportConverters) {
                    if (item.Tag == value) {
                        importCfgCombo.SelectedItem = item;
                        break;
                    }
                }
            }
        }
        public List<ConvItem> ExportConverters { get; } = new List<ConvItem>();
        public string ExportConvTag {
            get {
                return ((ConvItem)exportCfgCombo.SelectedItem).Tag;
            }
            set {
                foreach (ConvItem item in ExportConverters) {
                    if (item.Tag == value) {
                        exportCfgCombo.SelectedItem = item;
                        break;
                    }
                }
            }
        }
        public bool IsExportBestChecked { get; set; }
        public bool IsExportComboChecked { get; set; }

        /// <summary>
        /// Initializes the import/export configuration selection controls.
        /// </summary>
        private void InitImportExportConfig() {
            for (int i = 0; i < ImportFoundry.GetCount(); i++) {
                ImportFoundry.GetConverterInfo(i, out string tag, out string label,
                    out string description, out List<Converter.OptionDefinition> optionDefs);
                ConvItem item = new ConvItem(tag, label);
                ImportConverters.Add(item);
            }
            ImportConverters.Sort(delegate (ConvItem item1, ConvItem item2) {
                return string.Compare(item1.Label, item2.Label);
            });

            for (int i = 0; i < ExportFoundry.GetCount(); i++) {
                ExportFoundry.GetConverterInfo(i, out string tag, out string label,
                    out string description, out List<Converter.OptionDefinition> optionDefs);
                ConvItem item = new ConvItem(tag, label);
                ExportConverters.Add(item);
            }
            ExportConverters.Sort(delegate (ConvItem item1, ConvItem item2) {
                return string.Compare(item1.Label, item2.Label);
            });

            // Set initial configuration.  This should be updated when settings are read.
            IsExportBestChecked = true;
            importCfgCombo.SelectedIndex = 0;
            exportCfgCombo.SelectedIndex = 0;
        }

        private void ConfigureImportSettings_Click(object sender, RoutedEventArgs e) {
            SettingsHolder settings = new SettingsHolder(AppSettings.Global);
            EditConvertOpts dialog = new EditConvertOpts(this, false, settings);
            if (dialog.ShowDialog() == true) {
                AppSettings.Global.MergeSettings(settings);
            }
        }

        private void ConfigureExportSettings_Click(object sender, RoutedEventArgs e) {
            SettingsHolder settings = new SettingsHolder(AppSettings.Global);
            EditConvertOpts dialog = new EditConvertOpts(this, true, settings);
            if (dialog.ShowDialog() == true) {
                AppSettings.Global.MergeSettings(settings);
            }
        }

        /// <summary>
        /// Triggers OnPropertyChanged for all of the side-panel options.  Call this after the
        /// settings object is loaded or updated.
        /// </summary>
        public void PublishSideOptions() {
            IsChecked_AddCompress = IsChecked_AddCompress;
            IsChecked_AddRaw = IsChecked_AddRaw;
            IsChecked_AddRecurse = IsChecked_AddRecurse;
            IsChecked_AddStripExt = IsChecked_AddStripExt;
            IsChecked_AddStripPaths = IsChecked_AddStripPaths;
            IsChecked_AddPreserveADF = IsChecked_AddPreserveADF;
            IsChecked_AddPreserveAS = IsChecked_AddPreserveAS;
            IsChecked_AddPreserveNAPS = IsChecked_AddPreserveNAPS;
            IsChecked_ExtRaw = IsChecked_ExtRaw;
            IsChecked_ExtStripPaths = IsChecked_ExtStripPaths;

            SettingsHolder settings = AppSettings.Global;

            ExtractFileWorker.PreserveMode preserve =
                settings.GetEnum(AppSettings.EXT_PRESERVE_MODE,
                    ExtractFileWorker.PreserveMode.None);
            switch (preserve) {
                case ExtractFileWorker.PreserveMode.ADF:
                    IsChecked_ExtPreserveADF = true;
                    break;
                case ExtractFileWorker.PreserveMode.AS:
                    IsChecked_ExtPreserveAS = true;
                    break;
                case ExtractFileWorker.PreserveMode.NAPS:
                    IsChecked_ExtPreserveNAPS = true;
                    break;
                case ExtractFileWorker.PreserveMode.Host:
                case ExtractFileWorker.PreserveMode.None:
                default:
                    IsChecked_ExtPreserveNone = true;
                    break;
            }

            ImportConvTag = settings.GetString(AppSettings.CONV_IMPORT_TAG, string.Empty);
            ExportConvTag = settings.GetString(AppSettings.CONV_EXPORT_TAG, string.Empty);
            if (settings.GetBool(AppSettings.CONV_EXPORT_BEST, true)) {
                IsExportBestChecked = true;
            } else {
                IsExportComboChecked = true;
            }
            OnPropertyChanged(nameof(IsExportBestChecked));
            OnPropertyChanged(nameof(IsExportComboChecked));
        }

        #endregion Options Panel

        #region Status Bar

        /// <summary>
        /// String to display at the center of the status bar (bottom of window).
        /// </summary>
        public string CenterStatusText {
            get { return mCenterStatusText; }
            set { mCenterStatusText = value; OnPropertyChanged(); }
        }
        private string mCenterStatusText = string.Empty;

        #endregion Status Bar

        #region Misc

        private void DebugMenu_SubmenuOpened(object sender, RoutedEventArgs e) {
            debugShowDebugLogMenuItem.IsChecked = mMainCtrl.IsDebugLogOpen;
        }

        #endregion Misc
    }
}
