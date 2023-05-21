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
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using AppCommon;
using CommonUtil;
using cp2_wpf.WPFCommon;
using DiskArc;

namespace cp2_wpf {
    /// <summary>
    /// Main GUI controller.
    /// </summary>
    public partial class MainController {
        private const string FILE_ERR_CAPTION = "File error";

        private MainWindow mMainWin;

        private DebugMessageLog mDebugLog;
        private AppHook mAppHook;

        private string mWorkPathName = string.Empty;
        private WorkTree? mWorkTree = null;
        public Formatter mFormatter;

        /// <summary>
        /// True when a work file is open.
        /// </summary>
        public bool IsFileOpen { get { return mWorkTree != null; } }

        /// <summary>
        /// True when the file is open, and the file list is being shown.  False if we're showing
        /// something else, like Notes or a partition map.
        /// </summary>
        /// <remarks>
        /// The purpose is to determine whether commands like "add files" and "extract files"
        /// should be enabled.
        /// </remarks>
        //public bool IsFileAreaSelected { get { return IsFileOpen; } }   // TODO

        /// <summary>
        /// List of recently-opened files.
        /// </summary>
        public List<string> RecentFilePaths = new List<string>(MAX_RECENT_FILES);
        public const int MAX_RECENT_FILES = 6;

        /// <summary>
        /// Auto-open behavior.
        /// </summary>
        public enum AutoOpenDepth { Unknown = 0, Shallow, SubVol, Max }


        /// <summary>
        /// Constructor.
        /// </summary>
        public MainController(MainWindow mainWin) {
            mMainWin = mainWin;

            Formatter.FormatConfig cfg = new Formatter.FormatConfig();
            mFormatter = new Formatter(cfg);

            mDebugLog = new DebugMessageLog();
            mAppHook = new AppHook(mDebugLog);
        }

        /// <summary>
        /// Early initialization, before the window is visible.  Notably, we want to get the
        /// window placement data, so we can position and size the window before it's first
        /// drawn (avoids a blink).
        /// </summary>
        public void WindowSourceInitialized() {
            // Load the settings from the file.  If this fails we have no way to tell the user,
            // so just keep going.
            LoadAppSettings();

            SetAppWindowLocation();

            // The SetPlacement call causes the Loaded event to fire, so we get here after
            // WindowLoaded has run.  This has probably caused spurious setting of the app
            // settings dirty flag when components got sized.  Clear it here.
            AppSettings.Global.IsDirty = false;
        }

        /// <summary>
        /// Sets the app window's location and size.  This should be called before the window has
        /// finished initialization.
        /// </summary>
        private void SetAppWindowLocation() {
            // NOTE: this will cause the window Loaded event to fire immediately.
            string placement = AppSettings.Global.GetString(AppSettings.MAIN_WINDOW_PLACEMENT, "");
            if (!string.IsNullOrEmpty(placement)) {
                mMainWin.SetPlacement(placement);
            }

            SettingsHolder settings = AppSettings.Global;
            // This *must* be set or the left panel resize doesn't work the way we want.
            mMainWin.LeftPanelWidth =
                settings.GetInt(AppSettings.MAIN_LEFT_PANEL_WIDTH, 300);
            mMainWin.ShowOptionsPanel =
                settings.GetBool(AppSettings.MAIN_RIGHT_PANEL_VISIBLE, true);
            mMainWin.WorkTreePanelHeight =
                settings.GetInt(AppSettings.MAIN_WORK_TREE_HEIGHT, 200);
        }

        /// <summary>
        /// Perform one-time initialization after the Window has finished loading.  We defer
        /// to this point so we can report fatal errors directly to the user.
        /// </summary>
        public void WindowLoaded() {
            Debug.WriteLine("--- running unit tests ---");
            Debug.Assert(CommonUtil.RangeSet.Test());
            Debug.Assert(CommonUtil.Version.Test());
            Debug.Assert(CircularBitBuffer.DebugTest());
            Debug.Assert(Glob.DebugTest());
            Debug.Assert(PathName.DebugTest());
            Debug.Assert(TimeStamp.DebugTestDates());
            Debug.Assert(DiskArc.Disk.TrackInit.DebugCheckInterleave());
            Debug.Assert(DiskArc.Disk.Woz_Meta.DebugTest());
            Debug.WriteLine("--- unit tests complete ---");

            ApplyAppSettings();

            UpdateTitle();
            mMainWin.UpdateRecentLinks();

            ProcessCommandLine();
        }

        /// <summary>
        /// Processes any command-line arguments.
        /// </summary>
        private void ProcessCommandLine() {
            string[] args = Environment.GetCommandLineArgs();
            // Currently we just look for the name of a file to open.
            if (args.Length == 2) {
                DoOpenWorkFile(Path.GetFullPath(args[1]), false);
            }
        }

        /// <summary>
        /// Handles main window closing.
        /// </summary>
        /// <returns>True if it's okay for the window to close, false to cancel it.</returns>
        public bool WindowClosing() {
            SaveAppSettings();
            if (!CloseWorkFile()) {
                return false;
            }

            // WPF won't exit until all windows are closed, so any unowned windows need
            // to be cleaned up here.
            mDebugLogViewer?.Close();

            return true;
        }

        private void LoadAppSettings() {
            // Configure defaults.
            AppSettings.Global.SetBool(AppSettings.MAC_ZIP_ENABLED, true);
            AppSettings.Global.SetEnum(AppSettings.AUTO_OPEN_DEPTH, typeof(AutoOpenDepth),
                (int)AutoOpenDepth.SubVol);

            // Load settings from file and merge them in.
            string settingsPath =
                Path.Combine(WinUtil.GetRuntimeDataDir(), AppSettings.SETTINGS_FILE_NAME);
            try {
                string text = File.ReadAllText(settingsPath);
                SettingsHolder? fileSettings = SettingsHolder.Deserialize(text);
                if (fileSettings != null) {
                    AppSettings.Global.MergeSettings(fileSettings);
                }
                Debug.WriteLine("Settings file loaded and merged");
            } catch (Exception ex) {
                Debug.WriteLine("Unable to read settings file: " + ex.Message);
            }
        }

        private void SaveAppSettings() {
            SettingsHolder settings = AppSettings.Global;

            if (!settings.IsDirty) {
                Debug.WriteLine("Settings not dirty, not saving");
                return;
            }

            // Main window position and size.
            settings.SetString(AppSettings.MAIN_WINDOW_PLACEMENT, mMainWin.GetPlacement());

            // Horizontal splitters.
            settings.SetInt(AppSettings.MAIN_LEFT_PANEL_WIDTH, (int)mMainWin.LeftPanelWidth);
            settings.SetBool(AppSettings.MAIN_RIGHT_PANEL_VISIBLE, mMainWin.ShowOptionsPanel);

            // Vertical splitters.
            settings.SetInt(AppSettings.MAIN_WORK_TREE_HEIGHT, (int)mMainWin.WorkTreePanelHeight);

            //mMainWin.CaptureColumnWidths();

            string settingsPath =
                Path.Combine(WinUtil.GetRuntimeDataDir(), AppSettings.SETTINGS_FILE_NAME);
            try {
                string cereal = settings.Serialize();
                File.WriteAllText(settingsPath, cereal);
                settings.IsDirty = false;
                Debug.WriteLine("Saved settings to '" + settingsPath + "'");
            } catch (Exception ex) {
                Debug.WriteLine("Failed to save settings: " + ex.Message);
            }
        }

        private void ApplyAppSettings() {
            Debug.WriteLine("Applying app settings...");

            // Enable the DEBUG menu if configured.
            mMainWin.ShowDebugMenu =
                AppSettings.Global.GetBool(AppSettings.DEBUG_MENU_ENABLED, false);

            UnpackRecentFileList();
        }

        public void NewDiskImage() {
            Debug.WriteLine("new disk image!");
        }

        public void NewFileArchive() {
            Debug.WriteLine("new file archive!");
        }

        /// <summary>
        /// Handles File : Open.
        /// </summary>
        public void OpenWorkFile() {
            if (!CloseWorkFile()) {
                return;
            }

            string pathName = WinUtil.AskFileToOpen();
            if (string.IsNullOrEmpty(pathName)) {
                return;
            }
            DoOpenWorkFile(pathName, false);
        }

        private void DoOpenWorkFile(string pathName, bool asReadOnly) {
            Debug.Assert(mWorkTree == null);
            if (!File.Exists(pathName)) {
                // Should only happen for projects in "recents" list.
                string msg = "File not found: '" + pathName + "'";
                MessageBox.Show(msg, FILE_ERR_CAPTION, MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            AutoOpenDepth depth = (AutoOpenDepth)AppSettings.Global.GetEnum(
                AppSettings.AUTO_OPEN_DEPTH, typeof(AutoOpenDepth), (int)AutoOpenDepth.SubVol);
            WorkTree.DepthLimiter limiter =
                delegate (WorkTree.DepthParentKind parentKind, WorkTree.DepthChildKind childKind) {
                    return DepthLimit(parentKind, childKind, depth);
                };

            try {
                Mouse.OverrideCursor = Cursors.Wait;
                // TODO: do this on a background thread, in case we're opening something with
                //   lots of bits or from slow media
                mWorkTree = new WorkTree(pathName, limiter, asReadOnly, mAppHook);

                // File is fully parsed.  Generate the archive tree.
                PopulateArchiveTree();
            } catch (Exception ex) {
                // Expecting IOException and InvalidDataException.
                ShowFileError("Unable to open file: " + ex.Message);
                return;
            } finally {
                Mouse.OverrideCursor = null;
            }

            mWorkPathName = pathName;
            UpdateTitle();
            UpdateRecentFilesList(pathName);
            mMainWin.ShowLaunchPanel = false;
        }

        private static bool DepthLimit(WorkTree.DepthParentKind parentKind,
                WorkTree.DepthChildKind childKind, AutoOpenDepth depth) {
            if (depth == AutoOpenDepth.Max) {
                // Always descend.
                return true;
            } else if (depth == AutoOpenDepth.Shallow) {
                // We always descend into gzip, because gzip isn't very interesting for us,
                // and we want to treat .SDK generally like a disk image.  Otherwise, never
                // descend.
                if (childKind == WorkTree.DepthChildKind.AnyFile) {
                    return (parentKind == WorkTree.DepthParentKind.GZip ||
                            parentKind == WorkTree.DepthParentKind.NuFX);
                }
                if (parentKind == WorkTree.DepthParentKind.GZip ||
                        (parentKind == WorkTree.DepthParentKind.NuFX &&
                         childKind == WorkTree.DepthChildKind.DiskPart)) {
                    return true;
                }
            } else {
                // Depth is "SubVol".  Explore ZIP, multi-part, .SDK, and embeds.  Don't examine
                // any files in a filesystem.
                if (childKind == WorkTree.DepthChildKind.AnyFile) {
                    return (parentKind != WorkTree.DepthParentKind.FileSystem);
                }
                switch (parentKind) {
                    case WorkTree.DepthParentKind.GZip:
                        return true;
                    case WorkTree.DepthParentKind.Zip:
                        // Descend into disk images, but don't open archives.
                        if (childKind == WorkTree.DepthChildKind.DiskImage) {
                            return true;
                        }
                        break;
                    case WorkTree.DepthParentKind.NuFX:
                        // Descend into .SDK, but don't otherwise open the contents.
                        if (parentKind == WorkTree.DepthParentKind.NuFX &&
                                childKind == WorkTree.DepthChildKind.DiskPart) {
                            return true;
                        }
                        break;
                    case WorkTree.DepthParentKind.FileSystem:
                        // Descend into embedded volumes, otherwise stop.
                        if (childKind == WorkTree.DepthChildKind.Embed) {
                            return true;
                        }
                        break;
                    case WorkTree.DepthParentKind.MultiPart:
                        return true;
                    default:
                        Debug.Assert(false, "Unhandled case: " + parentKind);
                        break;
                }
            }
            return false;
        }

        /// <summary>
        /// Opens the Nth recently-opened project.
        /// </summary>
        /// <param name="recentIndex"></param>
        public void OpenRecentFile(int recentIndex) {
            if (!CloseWorkFile()) {
                return;
            }
            DoOpenWorkFile(RecentFilePaths[recentIndex], false);
        }

        /// <summary>
        /// Ensures that the named project is at the top of the list.  If it's elsewhere
        /// in the list, move it to the top.  Excess items are removed.
        /// </summary>
        /// <param name="projectPath"></param>
        private void UpdateRecentFilesList(string projectPath) {
            if (string.IsNullOrEmpty(projectPath)) {
                // This can happen if you create a new project, then close the window
                // without having saved it.
                return;
            }
            int index = RecentFilePaths.IndexOf(projectPath);
            if (index == 0) {
                // Already in the list, nothing changes.  No need to update anything else.
                return;
            }
            if (index > 0) {
                RecentFilePaths.RemoveAt(index);
            }
            RecentFilePaths.Insert(0, projectPath);

            // Trim the list to the max allowed.
            while (RecentFilePaths.Count > MAX_RECENT_FILES) {
                RecentFilePaths.RemoveAt(MAX_RECENT_FILES);
            }

            // Store updated list in app settings.  JSON-in-JSON is ugly and inefficient,
            // but it'll do for now.
            string cereal = JsonSerializer.Serialize(RecentFilePaths, typeof(List<string>));
            AppSettings.Global.SetString(AppSettings.RECENT_FILES_LIST, cereal);

            mMainWin.UpdateRecentLinks();
        }

        private void UnpackRecentFileList() {
            RecentFilePaths.Clear();

            string cereal = AppSettings.Global.GetString(AppSettings.RECENT_FILES_LIST, "");
            if (string.IsNullOrEmpty(cereal)) {
                return;
            }

            try {
                object? parsed = JsonSerializer.Deserialize<List<string>>(cereal);
                if (parsed != null) {
                    RecentFilePaths = (List<string>)parsed;
                }
            } catch (Exception ex) {
                Debug.WriteLine("Failed deserializing recent projects: " + ex.Message);
                return;
            }
        }

        /// <summary>
        /// Handles File : Close.
        /// </summary>
        /// <returns>True if the file was closed, false if the operation was cancelled.</returns>
        public bool CloseWorkFile() {
            if (mWorkTree == null) {
                return true;
            }
            Debug.WriteLine("Closing " + mWorkPathName);

            // Flush and close all disk images and file archives.
            ClearArchiveTree();
            // Close the host file.
            mWorkTree.Dispose();
            mWorkTree = null;

            UpdateTitle();
            ClearEntryCounts();
            mMainWin.ShowLaunchPanel = true;

            // Not necessary, but it lets us check the memory monitor to see if we got
            // rid of everything.
            GC.Collect();
            return true;
        }

        /// <summary>
        /// Update main window title to show file name.
        /// </summary>
        private void UpdateTitle() {
            StringBuilder sb = new StringBuilder();
            if (mWorkTree != null) {
                sb.Append(Path.GetFileName(mWorkPathName));
                if (!mWorkTree.CanWrite) {
                    sb.Append(" *READONLY*");
                }
                sb.Append(" - ");
            }
            sb.Append("CiderPress II");

            mMainWin.Title = sb.ToString();
        }

        /// <summary>
        /// Handles Edit : Application Settings.
        /// </summary>
        public void EditAppSettings() {
            EditAppSettings dlg = new EditAppSettings(mMainWin);
            dlg.SettingsApplied += ApplyAppSettings;
            dlg.ShowDialog();
            // Settings are applied via event.
        }

        /// <summary>
        /// Obtains the currently-selected entries from the archive tree and directory tree.
        /// </summary>
        /// <param name="archiveOrFileSystem">Result: IArchive or IFileSystem object selected
        ///   in the archive tree, or null if no such object is selected.</param>
        /// <param name="daNode">Result: DiskArcNode from this node, or nearest parent with a
        ///   non-null node.</param>
        /// <param name="selectionDir">Result: directory file entry selected in the directory
        ///   tree, or NO_ENTRY if no directory is selected (e.g. IArchive).</param>
        /// <returns>True if an IArchive or IFileSystem was selected.</returns>
        public bool GetSelectedArcDir([NotNullWhen(true)] out object? archiveOrFileSystem,
                [NotNullWhen(true)] out DiskArcNode? daNode, out IFileEntry selectionDir) {
            archiveOrFileSystem = null;
            daNode = null;
            selectionDir = IFileEntry.NO_ENTRY;

            ArchiveTreeItem? arcTreeSel = mMainWin.archiveTree.SelectedItem as ArchiveTreeItem;
            DirectoryTreeItem? dirTreeSel= mMainWin.directoryTree.SelectedItem as DirectoryTreeItem;
            if (arcTreeSel == null || dirTreeSel == null) {
                Debug.WriteLine("Current selection is not archive or filesystem");
                return false;
            }

            object? arcObj = arcTreeSel.WorkTreeNode.DAObject;
            if (arcObj == null) {
                Debug.WriteLine("DAObject was null");
                return false;
            } else if (arcObj is not IArchive && arcObj is not IFileSystem) {
                Debug.WriteLine("Unexpected DAObject type: " + arcObj.GetType());
                return false;
            }
            archiveOrFileSystem = arcObj;
            selectionDir = dirTreeSel.FileEntry;

            WorkTree.Node node = arcTreeSel.WorkTreeNode;
            while (node.DANode == null) {
                node = node.Parent!;
            }
            daNode = node.DANode;

            return true;
        }

        /// <summary>
        /// Obtains the current file list selection.
        /// </summary>
        /// <param name="skipDir">True if directories should be skipped.</param>
        /// <param name="skipOpenArc">True if open archives should be skipped.</param>
        /// <param name="closeOpenArc">True if open archives should be closed.</param>
        /// <param name="archiveOrFileSystem">Result: IArchive or IFileSystem object selected
        ///   in the archive tree, or null if no such object is selected.</param>
        /// <param name="selectionDir">Result: directory file entry selected in the directory
        ///   tree, or NO_ENTRY if no directory is selected (e.g. IArchive).</param>
        /// <param name="selected">Result: list of selected entries.</param>
        /// <returns>True if a non-empty list of selected items was found.</returns>
        public bool GetFileSelection(bool skipDir, bool skipOpenArc, bool closeOpenArc,
                [NotNullWhen(true)] out object? archiveOrFileSystem,
                out IFileEntry selectionDir,
                [NotNullWhen(true)] out List<IFileEntry>? selected) {
            selected = null;
            if (!GetSelectedArcDir(out archiveOrFileSystem, out DiskArcNode? unused,
                    out selectionDir)) {
                return false;
            }

            DataGrid dg = mMainWin.fileListDataGrid;
            IList treeSel = dg.SelectedItems;
            if (treeSel.Count == 0) {
                return false;
            }

            // Generate a selection set for the viewer.
            selected = new List<IFileEntry>(treeSel.Count);
            foreach (FileListItem listItem in treeSel) {
                IFileEntry entry = listItem.FileEntry;
                if (skipDir && entry.IsDirectory) {
                    Debug.WriteLine("Selection skip dir: " + entry);
                    continue;
                }
                ArchiveTreeItem? treeItem =
                    ArchiveTreeItem.FindItemByEntry(mMainWin.ArchiveTreeRoot, entry);
                if (treeItem != null) {
                    if (skipOpenArc) {
                        Debug.WriteLine("Selection skip open arc: " + entry);
                        continue;
                    } else if (closeOpenArc) {
                        // TODO - close open archive and remove from arc tree
                        continue;
                    }
                }
                selected.Add(listItem.FileEntry);
            }

            if (selected.Count == 0) {
                Debug.WriteLine("Nothing viewable selected");
                return false;
            }

            Debug.WriteLine("Current selection:");
            foreach (IFileEntry entry in selected) {
                Debug.WriteLine("  " + entry);
            }
            return true;
        }

        /// <summary>
        /// Opens the file viewer.
        /// </summary>
        public void ViewFiles() {
            // TODO: if only one file is selected, select all files in the same directory, to
            //   enable the forward/backward buttons in the viewer.
            if (!GetFileSelection(true, true, false, out object? archiveOrFileSystem,
                    out IFileEntry selectionDir, out List<IFileEntry>? selected)) {
                return;
            }

            FileViewer dialog = new FileViewer(mMainWin, archiveOrFileSystem, selected, mAppHook);
            dialog.ShowDialog();
        }

        /// <summary>
        /// Handles Actions : Extract Files
        /// </summary>
        public void ExtractFiles() {
            if (!GetFileSelection(false, false, true, out object? archiveOrFileSystem,
                    out IFileEntry selectionDir, out List<IFileEntry>? selected)) {
                return;
            }

            // TODO: there's no "pick a folder" dialog in WPF.  We can either use the Forms
            // version or pull in a 3rd-party library, e.g.
            // https://github.com/ookii-dialogs/ookii-dialogs-wpf
            // Unfortunately, drag & drop *out* of the application is fairly involved.
            // Hard-wire it for now.

            string outputDir = @"C:\src\test\_cp2";
            if (!Directory.Exists(outputDir)) {
                MessageBox.Show(mMainWin, "Files can only be extracted to '" + outputDir + "'",
                    "I said it's not ready");
                return;
            }
            ExtractProgress prog = new ExtractProgress(archiveOrFileSystem, selectionDir,
                selected, outputDir, mAppHook);
            Debug.WriteLine("Extract: selectionDir='" + selectionDir + "'");

            // Do the extraction on a background thread so we can show progress.
            WorkProgress workDialog = new WorkProgress(mMainWin, prog, false);
            workDialog.ShowDialog();
        }

        /// <summary>
        /// Handles Actions : Add Files
        /// </summary>
        public void AddFiles() {
            Debug.WriteLine("add!");

            // TODO: need a custom dialog to select files/folders.  All of the nonsense called out
            // in https://github.com/fadden/ciderpress/blob/master/util/SelectFilesDialog.h still
            // exists.  https://stackoverflow.com/q/31059/294248 has some stuff but it's
            // mostly Windows Forms or customized system dialogs.  Drag & drop from Explorer is
            // easier than this.
        }

        public void AddFileDrop(IFileEntry dropTarget, string[] pathNames) {
            Debug.Assert(pathNames.Length > 0);
            Debug.WriteLine("External file drop (target=" + dropTarget + "):");
            foreach (string path in pathNames) {
                Debug.WriteLine("  " + path);
            }

            if (!GetSelectedArcDir(out object? archiveOrFileSystem, out DiskArcNode? daNode,
                    out IFileEntry targetDir)) {
                // We don't have an archive and (optionally) directory target selected.
                return;
            }

            // Since we're dragging files out of an explorer window, all files should have the
            // same directory name.
            string basePath = Path.GetDirectoryName(pathNames[0])!;

            // TODO: configure add opts from settings
            AddFileSet.AddOpts addOpts = new AddFileSet.AddOpts();
            AddFileSet fileSet;
            try {
                fileSet = new AddFileSet(basePath, pathNames, addOpts, null, mAppHook);
            } catch (IOException ex) {
                ShowFileError(ex.Message);
                return;
            }
            if (fileSet.Count == 0) {
                Debug.WriteLine("File set was empty");
                return;
            }

            AddProgress prog =
                new AddProgress(archiveOrFileSystem, daNode, fileSet, targetDir, mAppHook);

            // Do the extraction on a background thread so we can show progress.
            WorkProgress workDialog = new WorkProgress(mMainWin, prog, false);
            workDialog.ShowDialog();

            // Refresh the contents of the file list.
            // TODO: if we added directories we also need to refresh the directory list
            if (archiveOrFileSystem is IArchive) {
                AddArchiveItems((IArchive)archiveOrFileSystem);
            } else {
                AddDiskItems(targetDir);
            }
        }

        /// <summary>
        /// Handles Help : About.
        /// </summary>
        public void About() {
            AboutBox dialog = new AboutBox(mMainWin);
            dialog.ShowDialog();
        }

        private static void ShowFileError(string msg) {
            MessageBox.Show(msg, FILE_ERR_CAPTION, MessageBoxButton.OK, MessageBoxImage.Error);
        }

        #region Debug

        public void Debug_DiskArcLibTests() {
            LibTest.TestManager dialog = new LibTest.TestManager(mMainWin, "DiskArcTests.dll",
                "DiskArcTests.ITest");
            dialog.ShowDialog();
        }

        public void Debug_FileConvLibTests() {
            LibTest.TestManager dialog = new LibTest.TestManager(mMainWin, "FileConvTests.dll",
                "FileConvTests.ITest");
            dialog.ShowDialog();
        }

        public void Debug_BulkCompressTest() {
            LibTest.BulkCompress dialog = new LibTest.BulkCompress(mMainWin, mAppHook);
            dialog.ShowDialog();
        }

        private Tools.LogViewer? mDebugLogViewer;
        public bool IsDebugLogOpen { get { return mDebugLogViewer != null; } }

        public void Debug_ShowDebugLog() {
            if (mDebugLogViewer == null) {
                Tools.LogViewer dlg = new Tools.LogViewer(null, mDebugLog);
                dlg.Closing += (sender, e) => {
                    Debug.WriteLine("Debug log viewer closed");
                    mDebugLogViewer = null;
                };
                dlg.Show();
                mDebugLogViewer = dlg;
            } else {
                // Ask the dialog to close.  Do the cleanup in the event.
                mDebugLogViewer.Close();
            }
        }

        #endregion Debug
    }
}
