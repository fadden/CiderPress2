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
        /// List of recently-opened projects.
        /// </summary>
        public List<string> RecentProjectPaths = new List<string>(MAX_RECENT_PROJECTS);
        public const int MAX_RECENT_PROJECTS = 6;


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
            //LoadAppSettings();

            // TODO: replace these with something like "--depth"?  It's less a question of which
            //   things to open than how far down to go.
            AppSettings.Global.SetBool(AppSettings.DEEP_SCAN_ARCHIVES, true);       // DEBUG DEBUG
            AppSettings.Global.SetBool(AppSettings.DEEP_SCAN_FILESYSTEMS, true);    // DEBUG DEBUG

            SetAppWindowLocation();
        }

        /// <summary>
        /// Sets the app window's location and size.  This should be called before the window has
        /// finished initialization.
        /// </summary>
        private void SetAppWindowLocation() {
            // These *must* be set or the panel resize doesn't work the way we want.
            mMainWin.LeftPanelWidth = 300;
            //mMainWin.RightPanelWidth = 180;
        }

        /// <summary>
        /// Perform one-time initialization after the Window has finished loading.  We defer
        /// to this point so we can report fatal errors directly to the user.
        /// </summary>
        public void WindowLoaded() {
            Debug.WriteLine("Unit tests...");
            Debug.Assert(CircularBitBuffer.DebugTest());
            Debug.Assert(Glob.DebugTest());
            Debug.Assert(PathName.DebugTest());
            Debug.Assert(TimeStamp.DebugTestDates());
            Debug.Assert(DiskArc.Disk.TrackInit.DebugCheckInterleave());
            Debug.Assert(DiskArc.Disk.Woz_Meta.DebugTest());
            Debug.WriteLine("Unit tests complete");

            ApplyAppSettings();

            UpdateTitle();

            ProcessCommandLine();
        }

        private void ProcessCommandLine() {
            string[] args = Environment.GetCommandLineArgs();
            if (args.Length == 2) {
                DoOpenWorkFile(Path.GetFullPath(args[1]), false);
            }
        }

        /// <summary>
        /// Handles main window closing.
        /// </summary>
        /// <returns>True if it's okay for the window to close, false to cancel it.</returns>
        public bool WindowClosing() {
            //SaveAppSettings();
            if (!CloseWorkFile()) {
                return false;
            }

            // WPF won't exit until all windows are closed, so any unowned windows need
            // to be cleaned up here.
            mDebugLogViewer?.Close();

            return true;
        }

        private void ApplyAppSettings() {
            // TODO
            UnpackRecentProjectList();
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

            WorkTree.DepthLimiter limiter =
                delegate (WorkTree.DepthParentKind parentKind, WorkTree.DepthChildKind childKind) {
                    return DepthLimit(parentKind, childKind /*TODO: pass settings*/);
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
            mMainWin.ShowLaunchPanel = false;
        }

        private static bool DepthLimit(WorkTree.DepthParentKind parentKind,
                WorkTree.DepthChildKind childKind) {
            // TODO
            return true;
        }

        /// <summary>
        /// Opens the Nth recently-opened project.
        /// </summary>
        /// <param name="projIndex"></param>
        public void OpenRecentProject(int projIndex) {
            if (!CloseWorkFile()) {
                return;
            }
            DoOpenWorkFile(RecentProjectPaths[projIndex], false);
        }

        private void UnpackRecentProjectList() {
            RecentProjectPaths.Clear();
            // TODO
            string sample1 = @"C:\Src\CiderPress2\TestData\fileconv\test-files.sdk";
            RecentProjectPaths.Add(sample1);
            mMainWin.RecentProjectName1 = Path.GetFileName(sample1);
            mMainWin.RecentProjectPath1 = sample1;
            string sample2 = @"C:\Src\test\foo.po";
            RecentProjectPaths.Add(sample2);
            mMainWin.RecentProjectName2 = Path.GetFileName(sample2);
            mMainWin.RecentProjectPath2 = sample2;
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
        /// Obtains the currently-selected entries from the archive tree and directory tree.
        /// </summary>
        /// <param name="archiveOrFileSystem">Result: IArchive or IFileSystem object selected
        ///   in the archive tree, or null if no such object is selected.</param>
        /// <param name="daNode">Result: selected DiskArcNode.</param>
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
            daNode = arcTreeSel.WorkTreeNode.DANode;
            Debug.Assert(daNode != null);

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
