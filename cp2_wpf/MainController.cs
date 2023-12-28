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
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

using AppCommon;
using CommonUtil;
using cp2_wpf.Actions;
using cp2_wpf.Tools;
using cp2_wpf.WPFCommon;
using Delay;
using DiskArc;
using DiskArc.Arc;
using DiskArc.FS;
using DiskArc.Multi;
using static DiskArc.Defs;
using FileConv;

namespace cp2_wpf {
    /// <summary>
    /// Main GUI controller.
    /// </summary>
    public partial class MainController {
        private const string FILE_ERR_CAPTION = "File error";

        private MainWindow mMainWin;

        private DebugMessageLog mDebugLog;
        public AppHook AppHook { get; private set; }

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
            AppHook = new AppHook(mDebugLog);
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
            mMainWin.WorkTreePanelHeightRatio =
                settings.GetInt(AppSettings.MAIN_WORK_TREE_HEIGHT, 800);    // 0.7:1

            mMainWin.RestoreColumnWidths();
        }

        /// <summary>
        /// Perform one-time initialization after the Window has finished loading.  We defer
        /// to this point so we can report fatal errors directly to the user.
        /// </summary>
        public void WindowLoaded() {
            AppHook.LogI("--- running unit tests ---");
            Debug.Assert(RangeSet.Test());
            Debug.Assert(CommonUtil.Version.Test());
            Debug.Assert(CircularBitBuffer.DebugTest());
            Debug.Assert(Glob.DebugTest());
            Debug.Assert(PathName.DebugTest());
            Debug.Assert(TimeStamp.DebugTestDates());
            Debug.Assert(DiskArc.Disk.TrackInit.DebugCheckInterleave());
            Debug.Assert(DiskArc.Disk.Woz_Meta.DebugTest());
            AppHook.LogI("--- unit tests complete ---");

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

            bool readOnly = false;

            int curArg = 1;
            if (args.Length > curArg && args[curArg] == "-r") {
                readOnly = true;
                curArg++;
            }

            if (args.Length > curArg) {
                if (args[curArg].StartsWith(PhysicalDriveAccess.Win.PHYSICAL_DRIVE_PREFIX)) {
                    DoOpenPhysicalDrive(args[curArg], readOnly);
                } else {
                    DoOpenWorkFile(Path.GetFullPath(args[curArg]), readOnly);
                }
            }
        }

        /// <summary>
        /// Handles main window closing.
        /// </summary>
        /// <returns>True if it's okay for the window to close, false to cancel it.</returns>
        public bool WindowClosing() {
            // Force save on exit, because some things may not force an immediate settings change.
            AppSettings.Global.IsDirty = true;

            SaveAppSettings();
            if (!CloseWorkFile()) {
                return false;
            }

            // WPF won't exit until all windows are closed, so any unowned windows need
            // to be cleaned up here.
            mDebugLogViewer?.Close();
            mDebugDropTarget?.Close();

            return true;
        }

        /// <summary>
        /// Loads application settings from the config file.
        /// </summary>
        private void LoadAppSettings() {
            // Configure defaults.
            SettingsHolder settings = AppSettings.Global;
            settings.SetBool(AppSettings.MAC_ZIP_ENABLED, true);
            settings.SetEnum(AppSettings.AUTO_OPEN_DEPTH, AutoOpenDepth.SubVol);

            settings.SetBool(AppSettings.ADD_RECURSE_ENABLED, true);
            settings.SetBool(AppSettings.ADD_COMPRESS_ENABLED, true);
            settings.SetBool(AppSettings.ADD_STRIP_PATHS_ENABLED, false);
            settings.SetBool(AppSettings.ADD_STRIP_EXT_ENABLED, true);
            settings.SetBool(AppSettings.ADD_RAW_ENABLED, false);
            settings.SetBool(AppSettings.ADD_PRESERVE_ADF, true);
            settings.SetBool(AppSettings.ADD_PRESERVE_AS, true);
            settings.SetBool(AppSettings.ADD_PRESERVE_NAPS, true);
            settings.SetEnum(AppSettings.EXT_PRESERVE_MODE,
                ExtractFileWorker.PreserveMode.NAPS);
            settings.SetBool(AppSettings.EXT_RAW_ENABLED, false);
            settings.SetBool(AppSettings.EXT_STRIP_PATHS_ENABLED, false);

            settings.SetString(AppSettings.CONV_IMPORT_TAG, "text");

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

            // Trigger OnPropertyChanged for the new values.
            mMainWin.PublishSideOptions();
        }

        /// <summary>
        /// Saves application settings to the config file.
        /// </summary>
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
            settings.SetInt(AppSettings.MAIN_WORK_TREE_HEIGHT,
                (int)mMainWin.WorkTreePanelHeightRatio);

            mMainWin.CaptureColumnWidths();

            settings.SetString(AppSettings.CONV_IMPORT_TAG, mMainWin.ImportConvTag);
            settings.SetString(AppSettings.CONV_EXPORT_TAG, mMainWin.ExportConvTag);
            settings.SetBool(AppSettings.CONV_EXPORT_BEST, mMainWin.IsExportBestChecked);

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

        /// <summary>
        /// Applies application settings.  This is run on startup, and when the "Apply" button
        /// is hit in the Edit App Settings dialog.
        /// </summary>
        private void ApplyAppSettings() {
            Debug.WriteLine("Applying app settings...");
            SettingsHolder settings = AppSettings.Global;

            // Enable the DEBUG menu if configured.
            mMainWin.ShowDebugMenu = settings.GetBool(AppSettings.DEBUG_MENU_ENABLED, false);

            if (mWorkTree != null) {
                AutoOpenDepth depth = settings.GetEnum(AppSettings.AUTO_OPEN_DEPTH,
                    AutoOpenDepth.SubVol);
                WorkTree.DepthLimiter limiter =
                    delegate (WorkTree.DepthParentKind parentKind,
                            WorkTree.DepthChildKind childKind) {
                        return DepthLimit(parentKind, childKind, depth);
                    };
                mWorkTree.DepthLimitFunc = limiter;
            }

            UnpackRecentFileList();

            // If a setting like MacZip changed state, we need to recompute the file list.
            RefreshDirAndFileList();

            // Hack to make the resource length column toggle when MacZip updated.
            if (CurrentWorkObject is Zip) {
                mMainWin.ReconfigureCenterPanel(
                    settings.GetBool(AppSettings.MAC_ZIP_ENABLED, false));
            }
        }

        public void NavToParent(bool dirOnly) {
            DirectoryTreeItem? dirSel = mMainWin.SelectedDirectoryTreeItem;
            if (dirSel == null) {
                return;
            }
            if (dirSel.Parent != null) {
                dirSel.Parent.IsSelected = true;
                DirectoryTreeItem.BringItemIntoView(mMainWin.directoryTree, dirSel.Parent);
            } else if (!dirOnly) {
                ArchiveTreeItem? arcSel = mMainWin.SelectedArchiveTreeItem;
                if (arcSel == null) {
                    return;
                }
                if (arcSel.Parent != null) {
                    arcSel.Parent.IsSelected = true;
                    ArchiveTreeItem.BringItemIntoView(mMainWin.archiveTree, arcSel.Parent);
                }
            }
        }

        public void NewDiskImage() {
            // Close the current file now, in case we want to overwrite it.
            if (!CloseWorkFile()) {
                return;
            }

            CreateDiskImage dialog = new CreateDiskImage(mMainWin, AppHook);
            if (dialog.ShowDialog() != true) {
                return;
            }

            // Open the new archive.
            DoOpenWorkFile(dialog.PathName, false);
        }

        /// <summary>
        /// Handles File : New File Archive
        /// </summary>
        public void NewFileArchive() {
            CreateFileArchive dialog = new CreateFileArchive(mMainWin);
            if (dialog.ShowDialog() != true) {
                return;
            }

            // It feels less distracting to do this now, rather than earlier.
            if (!CloseWorkFile()) {
                return;
            }

            string ext, filter;
            switch (dialog.Kind) {
                case FileKind.Binary2:
                    ext = ".bny";
                    filter = WinUtil.FILE_FILTER_BINARY2;
                    break;
                case FileKind.NuFX:
                    ext = ".shk";
                    filter = WinUtil.FILE_FILTER_NUFX;
                    break;
                case FileKind.Zip:
                    ext = ".zip";
                    filter = WinUtil.FILE_FILTER_ZIP;
                    break;
                default:
                    Debug.Assert(false);
                    return;
            }
            string fileName = "NewArchive" + ext;

            SaveFileDialog fileDlg = new SaveFileDialog() {
                Title = "Create New Archive...",
                Filter = filter + "|" + WinUtil.FILE_FILTER_ALL,
                FilterIndex = 1,
                FileName = fileName
            };
            if (fileDlg.ShowDialog() != true) {
                return;
            }

            // Add extension if not present.  (AddExtension property doesn't work quite right.)
            string pathName = Path.GetFullPath(fileDlg.FileName);
            if (!pathName.ToLowerInvariant().EndsWith(ext)) {
                pathName += ext;
            }

            IArchive archive;
            switch (dialog.Kind) {
                case FileKind.Binary2:
                    archive = Binary2.CreateArchive(AppHook);
                    break;
                case FileKind.NuFX:
                    archive = NuFX.CreateArchive(AppHook);
                    break;
                case FileKind.Zip:
                    archive = Zip.CreateArchive(AppHook);
                    break;
                default:
                    Debug.Assert(false);
                    return;
            }
            try {
                using (FileStream imgStream = new FileStream(pathName, FileMode.CreateNew)) {
                    archive.StartTransaction();
                    archive.CommitTransaction(imgStream);
                }
                DoOpenWorkFile(pathName, false);
            } catch (IOException ex) {
                ShowFileError("Unable to create archive: " + ex.Message);
            }
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

        /// <summary>
        /// Handles file drop operation on launch panel.
        /// </summary>
        public void DropOpenWorkFile(string pathName) {
            if (!CloseWorkFile()) {     // shouldn't be necessary; just being paranoid
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
            AppHook.LogI("Opening work file '" + pathName + "' readOnly=" + asReadOnly);

            AutoOpenDepth depth =
                AppSettings.Global.GetEnum(AppSettings.AUTO_OPEN_DEPTH, AutoOpenDepth.SubVol);
            WorkTree.DepthLimiter limiter =
                delegate (WorkTree.DepthParentKind parentKind, WorkTree.DepthChildKind childKind) {
                    return DepthLimit(parentKind, childKind, depth);
                };

            try {
                Mouse.OverrideCursor = Cursors.Wait;

                // Do the load on a background thread so we can show progress.
                OpenProgress prog = new OpenProgress(pathName, limiter, asReadOnly, AppHook);
                WorkProgress workDialog = new WorkProgress(mMainWin, prog, true);
                if (workDialog.ShowDialog() != true) {
                    // cancelled or failed
                    if (prog.Results.mException != null) {
                        MessageBox.Show(mMainWin, "Error: " + prog.Results.mException.Message,
                            FILE_ERR_CAPTION, MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                    return;
                }
                Debug.Assert(prog.Results.mWorkTree != null);
                mWorkTree = prog.Results.mWorkTree;

                // File is fully parsed.  Generate the archive tree.
                PopulateArchiveTree();
            } catch (Exception ex) {
                // Expecting IOException and InvalidDataException.
                ShowFileError("Unable to open file: " + ex.Message);
                return;
            } finally {
                Mouse.OverrideCursor = null;
            }
            AppHook.LogI("Load of '" + pathName + "' completed");

            mWorkPathName = pathName;
            UpdateTitle();
            UpdateRecentFilesList(pathName);
            mMainWin.ShowLaunchPanel = false;

            // In theory this puts the focus on the file list.  In practice it does do this, but
            // the first time you hit a key, the menu steals it back.
            //mMainWin.fileListDataGrid.Focus();
            //IInputElement newFocus = Keyboard.Focus(mMainWin.fileListDataGrid);
            //Debug.Assert(newFocus == mMainWin.fileListDataGrid);

            // We can't really focus on the file list yet because it won't get populated until
            // the various selection-changed events percolate through.
            mSwitchFocusToFileList = true;
        }

        /// <summary>
        /// Handles File > Open Physical Drive
        /// </summary>
        public void OpenPhysicalDrive() {
            if (!CloseWorkFile()) {
                return;
            }

            SelectPhysicalDrive dialog = new SelectPhysicalDrive(mMainWin);
            if (dialog.ShowDialog() != true) {
                return;
            }

            PhysicalDriveAccess.DiskInfo disk = dialog.SelectedDisk!;

            if (!WinUtil.IsAdministrator()) {
                // TODO: add setting to do this automatically
                MessageBoxResult result = MessageBox.Show(mMainWin,
                    "CiderPress II is not running as Administrator. Restart?",
                    "Escalate privileges", MessageBoxButton.OKCancel, MessageBoxImage.Question);
                if (result == MessageBoxResult.OK) {
                    // Restart process with escalated privileges.
                    string exeName = Process.GetCurrentProcess().MainModule!.FileName!;
                    ProcessStartInfo startInfo = new ProcessStartInfo(exeName);
                    if (dialog.OpenReadOnly) {
                        startInfo.ArgumentList.Add("-r");
                    }
                    startInfo.ArgumentList.Add(disk.Name);
                    startInfo.Verb = "runas";
                    startInfo.UseShellExecute = true;
                    try {
                        Process.Start(startInfo);
                        Application.Current.Shutdown();
                    } catch (Win32Exception ex) {
                        Debug.WriteLine("Process.Start failed: " + ex.Message);
                    }
                    return;
                }
            }

            DoOpenPhysicalDrive(disk.Name, dialog.OpenReadOnly);
        }

        private void DoOpenPhysicalDrive(string deviceName, bool asReadOnly) {
            if (deviceName == PhysicalDriveAccess.Win.PHYSICAL_DRIVE_PREFIX + "0") {
                Debug.Assert(false, "disallowed for safety reasons");
                return;
            }
            SafeFileHandle handle = PhysicalDriveAccess.Win.OpenDisk(deviceName, false,
                out long deviceSize, out int errCode);
            if (handle.IsInvalid) {
                const int ERROR_ACCESS_DENIED = 5;
                string msg;
                if (errCode == ERROR_ACCESS_DENIED) {
                    msg = "Unable to open disk: access denied (need to run as Administrator?)";
                } else {
                    msg = "Unable to open disk '" + deviceName + "': error 0x" +
                        errCode.ToString("x8");
                }
                MessageBox.Show(mMainWin, msg, "Failed",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                handle.Dispose();
                return;
            }

            AutoOpenDepth depth =
                AppSettings.Global.GetEnum(AppSettings.AUTO_OPEN_DEPTH, AutoOpenDepth.SubVol);
            WorkTree.DepthLimiter limiter =
                delegate (WorkTree.DepthParentKind parentKind, WorkTree.DepthChildKind childKind) {
                    return DepthLimit(parentKind, childKind, depth);
                };

            try {
                Mouse.OverrideCursor = Cursors.Wait;

                // Do the load on a background thread so we can show progress.
                OpenProgress prog = new OpenProgress(handle, deviceName, deviceSize, limiter,
                    asReadOnly, AppHook);
                WorkProgress workDialog = new WorkProgress(mMainWin, prog, true);
                if (workDialog.ShowDialog() != true) {
                    // cancelled or failed
                    if (prog.Results.mException != null) {
                        MessageBox.Show(mMainWin, "Error: " + prog.Results.mException.Message,
                            FILE_ERR_CAPTION, MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                    return;
                }
                Debug.Assert(prog.Results.mWorkTree != null);
                mWorkTree = prog.Results.mWorkTree;

                // File is fully parsed.  Generate the archive tree.
                PopulateArchiveTree();
            } catch (Exception ex) {
                // Expecting IOException and InvalidDataException.
                ShowFileError("Unable to open file: " + ex.Message);
                return;
            } finally {
                Mouse.OverrideCursor = null;
            }

            AppHook.LogI("Opened device '" + deviceName + "'");
            mWorkPathName = deviceName;
            UpdateTitle();
            mMainWin.ShowLaunchPanel = false;
            mSwitchFocusToFileList = true;
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
                    case WorkTree.DepthParentKind.Archive:
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
        /// <param name="recentIndex">Index of entry in recents list.</param>
        public void OpenRecentFile(int recentIndex) {
            if (!CloseWorkFile()) {
                return;
            }
            if (recentIndex >= RecentFilePaths.Count) {
                Debug.Assert(false);
                return;
            }
            DoOpenWorkFile(RecentFilePaths[recentIndex], false);
        }

        /// <summary>
        /// Ensures that the named file is at the top of the list.  If it's elsewhere
        /// in the list, move it to the top.  Excess items are removed.
        /// </summary>
        /// <param name="pathName">Pathname of file to add to list.</param>
        private void UpdateRecentFilesList(string pathName) {
            if (string.IsNullOrEmpty(pathName)) {
                // This can happen if you create a new project, then close the window
                // without having saved it.
                return;
            }
            int index = RecentFilePaths.IndexOf(pathName);
            if (index == 0) {
                // Already in the list, nothing changes.  No need to update anything else.
                return;
            }
            if (index > 0) {
                RecentFilePaths.RemoveAt(index);
            }
            RecentFilePaths.Insert(0, pathName);

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

        /// <summary>
        /// Unpacks the list of recent files from the application settings entry.
        /// </summary>
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

            // Flush and close all disk images and file archives.  Purge GUI elements.
            mMainWin.ClearTreesAndLists();
            // Close the host file.
            mWorkTree.Dispose();
            mWorkTree = null;

            UpdateTitle();
            ClearEntryCounts();
            mMainWin.ShowLaunchPanel = true;

            // If the clipboard contents are ours, clear them.
            ClipInfo? clipInfo = ClipInfo.GetClipInfo(null);
            if (clipInfo != null && clipInfo.ProcessId == Process.GetCurrentProcess().Id) {
                AppHook.LogI("Clearing clipboard");
                Clipboard.Clear();
            }

            // This seems like a good time to save our current settings, notably the Recents list.
            SaveAppSettings();

            // Not necessary to collect garbage, but it lets us check the memory monitor to see
            // if we got rid of everything.
            GC.Collect();
            return true;
        }

        /// <summary>
        /// Update main window title to show file name.
        /// </summary>
        private void UpdateTitle() {
            StringBuilder sb = new StringBuilder();
            if (mWorkTree != null) {
                string fileName = Path.GetFileName(mWorkPathName);
                if (string.IsNullOrEmpty(fileName)) {
                    // Physical devices don't have filenames, so this comes up empty.
                    fileName = mWorkPathName;
                }
                sb.Append(fileName);
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
            // Settings are applied via raised event.
        }

        /// <summary>
        /// Handles (context) : Close Sub-Tree.
        /// </summary>
        public void CloseSubTree() {
            ArchiveTreeItem? arcTreeSel = mMainWin.SelectedArchiveTreeItem;
            if (arcTreeSel == null) {
                return;
            }
            CloseSubTree(arcTreeSel);
        }

        /// <summary>
        /// Closes a sub-tree of the archive tree.  May only be used for disk images and file
        /// archives that exist as files inside a filesystem or archive.
        /// </summary>
        /// <param name="item">Tree item to close.</param>
        private void CloseSubTree(ArchiveTreeItem item) {
            Debug.Assert(item.CanClose);
            if (item.Parent == null) {
                Debug.Assert(false, "cannot close root");
                return;
            }
            WorkTree.Node workNode = item.WorkTreeNode;
            ArchiveTreeItem parentItem = item.Parent;
            WorkTree.Node parentNode = item.Parent.WorkTreeNode;
            parentNode.CloseChild(workNode);
            bool ok = parentItem.RemoveChild(item);
            Debug.Assert(ok, "failed to remove child tree item");

            // Select the parent tree item.
            parentItem.IsSelected = true;
        }

        /// <summary>
        /// Obtains the currently-selected entries from the archive tree and directory tree.
        /// </summary>
        /// <param name="archiveOrFileSystem">Result: IArchive or IFileSystem object selected
        ///   in the archive tree, or null if no such object is selected.</param>
        /// <param name="daNode">Result: DiskArcNode from this node, or nearest parent with a
        ///   non-null node, or null if not IArchive or IFileSystem.</param>
        /// <param name="selectionDir">Result: directory file entry selected in the directory
        ///   tree, or NO_ENTRY if no directory is selected (e.g. IArchive).</param>
        /// <returns>True if an IArchive or IFileSystem was selected.</returns>
        public bool GetSelectedArcDir([NotNullWhen(true)] out object? archiveOrFileSystem,
                [NotNullWhen(true)] out DiskArcNode? daNode, out IFileEntry selectionDir) {
            archiveOrFileSystem = null;
            daNode = null;
            selectionDir = IFileEntry.NO_ENTRY;

            ArchiveTreeItem? arcTreeSel = mMainWin.SelectedArchiveTreeItem;
            DirectoryTreeItem? dirTreeSel= mMainWin.SelectedDirectoryTreeItem;
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

            daNode = arcTreeSel.WorkTreeNode.FindDANode();
            return true;
        }

        /// <summary>
        /// Obtains the current file list selection.  Depending on the arguments, the result
        /// may include the results of a recursive filesystem descent.
        /// </summary>
        /// <param name="omitDir">True if directories should be omitted from the list.  This also
        ///   prevents recursive descent.  Set when viewing entries.</param>
        /// <param name="omitOpenArc">True if open archives should be omitted.  Set when
        ///   viewing entries.</param>
        /// <param name="closeOpenArc">True if open archives should be closed.  Set when
        ///   extracting or deleting entries.</param>
        /// <param name="archiveOrFileSystem">Result: IArchive or IFileSystem object selected
        ///   in the archive tree, or null if no such object is selected.</param>
        /// <param name="selectionDir">Result: directory file entry selected in the directory
        ///   tree, or NO_ENTRY if no directory is selected (e.g. IArchive).</param>
        /// <param name="selected">Result: list of selected entries.</param>
        /// <param name="firstSel">Result: index of first selected item.  Will be zero unless
        ///   <paramref name="oneMeansAll"/> is set.  If the selected item was omitted (e.g.
        ///   because it's a directory), this will be -1.</param>
        /// <returns>True if a non-empty list of selected items was found.</returns>
        public bool GetFileSelection(bool omitDir, bool omitOpenArc, bool closeOpenArc,
                bool oneMeansAll,
                [NotNullWhen(true)] out object? archiveOrFileSystem,
                out IFileEntry selectionDir,
                [NotNullWhen(true)] out List<IFileEntry>? selected,
                out int firstSel) {
            selected = null;
            firstSel = 0;
            if (!GetSelectedArcDir(out archiveOrFileSystem, out DiskArcNode? unused,
                    out selectionDir)) {
                return false;
            }

            // We don't need to check for open archives if nothing is open below this level,
            // or if we don't feel the need to handle them specially.
            bool doCheckOpen = false;
            if (omitOpenArc || closeOpenArc) {
                ArchiveTreeItem? arcTreeSel = mMainWin.SelectedArchiveTreeItem;
                if (arcTreeSel != null) {
                    doCheckOpen = arcTreeSel.Items.Count != 0;
                }
            }

            // Get the list of selected items.  The behavior is a little strange: if you select
            // an item in the middle, and then use select-all, the item you selected will be at
            // the top (unless your selection was at the very bottom).  Everything else will be
            // in the expected order.
            DataGrid dg = mMainWin.fileListDataGrid;
            IList listSel = dg.SelectedItems;
            if (listSel.Count == 0) {
                return false;
            }

            // If we have a single selection, we want to track that selection so we show it
            // first in the file viewer.  This won't work if we select a single directory and then
            // view files from the Actions menu.
            object? selItem = null;
            if (oneMeansAll && dg.SelectedItems.Count == 1) {
                listSel = dg.Items;
                selItem = dg.SelectedItem;
            }

            // Generate a selection set.  We want to collect the full set here so we can
            // present the user with dialogs like, "are you sure you want to delete 57 files?",
            // which will be wrong if we don't descend into subdirs.  The various workers can
            // do the recursion themselves if enabled, but there are advantages to doing it here,
            // and very little performance cost.
            selected = new List<IFileEntry>(listSel.Count);
            if (archiveOrFileSystem is IArchive) {
                // For file archives, it's valid to delete "directory entries" without deleting
                // their contents, and the order in which we remove things doesn't matter.  So
                // we just copy everything in, skipping directories if this is e.g. a "view" op.
                foreach (FileListItem listItem in listSel) {
                    if (omitDir && listItem.FileEntry.IsDirectory) {
                        continue;
                    }
                    selected.Add(listItem.FileEntry);
                }
            } else {
                // For filesystems, we need to scan the subdirectories.  If the display is in
                // single-directory mode this is easy, but if it's in full-list mode we might
                // have a directory and a couple of things inside that directory.  We need to
                // avoid adding entries twice, and we need to ensure that directories appear
                // before their contents.

                // Start by creating a dictionary of selected entries, so we can exclude them
                // during the subdir descent with O(1) lookups.
                Dictionary<IFileEntry, IFileEntry> knownItems =
                    new Dictionary<IFileEntry, IFileEntry>();
                foreach (FileListItem listItem in listSel) {
                    knownItems.Add(listItem.FileEntry, listItem.FileEntry);
                }

                // Add unique items to the "selected" list.  Include and descend into
                // subdirectories if "omitDir" isn't set.
                foreach (FileListItem listItem in listSel) {
                    IFileEntry entry = listItem.FileEntry;
                    if (entry.IsDirectory) {
                        if (!omitDir) {
                            selected.Add(listItem.FileEntry);
                            AddDirEntries(listItem.FileEntry, knownItems, selected);
                        }
                    } else {
                        // Don't need to check knownItems, since it's all from the selection.
                        selected.Add(listItem.FileEntry);
                    }
                }

                // We need directories to appear before their contents, which they should do with
                // any ascending sort (by virtue of the names being shorter).  The problem is that
                // sometimes we want to preserve the original file order, so we want to minimize
                // the effects of the sorting.
                //
                // We always want to suppress this when viewing files.  Sorting is only of vital
                // importance for  recursive file deletion, so we can skip this if the "omit dir"
                // flag is set by the file viewer.  (If we're only shifting directories around,
                // it shouldn't actually matter whether we do it or not, but there's no point
                // in doing work we don't have to.)
                if (!omitDir) {
                    selected = ShiftDirectories(selected);
                    //selected.Sort(delegate (IFileEntry entry1, IFileEntry entry2) {
                    //    return string.Compare(entry1.FullPathName, entry2.FullPathName);
                    //});
                }
            }
            // Selection set may be empty at this point.

            // Handle open archives.  We may need to remove them from the set.
            if (doCheckOpen) {
                Debug.Assert(omitOpenArc || closeOpenArc);
                for (int i = 0; i < selected.Count; i++) {
                    IFileEntry entry = selected[i];
                    if (entry.IsDirectory) {
                        continue;
                    }
                    ArchiveTreeItem? treeItem =
                        ArchiveTreeItem.FindItemByEntry(mMainWin.ArchiveTreeRoot, entry);
                    if (treeItem != null) {
                        if (omitOpenArc) {
                            Debug.WriteLine("Selection skip open arc: " + entry);
                            // Remove from list.
                            selected.RemoveAt(i--);
                        } else if (closeOpenArc) {
                            Debug.WriteLine("Selection close open arc: " + entry);
                            // Leave it in the list.
                            CloseSubTree(treeItem);
                        }
                    }
                }
            }

            // If we're in "one means all" mode, and only one item was selecteed, we need to
            // identify which member of the selection set we're supposed to display.
            if (selItem != null) {
                Debug.Assert(selItem != null);
                for (int i = 0; i < selected.Count; i++) {
                    if (selected[i] == ((FileListItem)selItem).FileEntry) {
                        firstSel = i;
                        break;
                    }
                }
            }
            return true;
        }

        private void AddDirEntries(IFileEntry entry, Dictionary<IFileEntry, IFileEntry> excludes,
                List<IFileEntry> list) {
            foreach (IFileEntry child in entry) {
                if (!excludes.ContainsKey(child)) {
                    list.Add(child);
                }
                if (child.IsDirectory) {
                    AddDirEntries(child, excludes, list);
                }
            }
        }

        /// <summary>
        /// Moves directory entries so they appear before the first file in that directory.
        /// </summary>
        /// <remarks>
        /// Only useful for filesystems, as file archives don't support ContainingDir.
        /// </remarks>
        /// <param name="entries">List of entries to sort.</param>
        /// <returns></returns>
        private List<IFileEntry> ShiftDirectories(List<IFileEntry> entryList) {
            // Generate a dictionary of all included directories.
            Dictionary<IFileEntry, bool> dirs = new Dictionary<IFileEntry, bool>();
            foreach (IFileEntry entry in entryList) {
                if (entry.IsDirectory) {
                    dirs.Add(entry, false);
                }
            }
            if (dirs.Count == 0) {
                // Nothing to do, just return original list.
                return entryList;
            }

            List<IFileEntry> sorted = new List<IFileEntry>(entryList.Count);

            foreach (IFileEntry entry in entryList) {
                if (entry.IsDirectory) {
                    if (!dirs[entry]) {
                        // This one hasn't been encountered or referenced before.  We need to
                        // copy this entry to the output, but first we need to recursively copy
                        // any of the directory's parent entries that are in entryList, starting
                        // from the topmost.
                        ShiftDirAddParents(entry, sorted, dirs);
                        Debug.Assert(dirs[entry]);
                    }
                } else {
                    IFileEntry parent = entry.ContainingDir;
                    if (!dirs.TryGetValue(parent, out bool alreadySeen)) {
                        // Parent directory isn't part of our selection.  Nothing for us to do.
                    } else if (!alreadySeen) {
                        // Need to add the directory first.
                        ShiftDirAddParents(parent, sorted, dirs);
                        Debug.Assert(dirs[parent]);
                    }
                    sorted.Add(entry);
                }
            }

            Debug.Assert(sorted.Count == entryList.Count);
            return sorted;
        }

        /// <summary>
        /// Recursively adds the specified entry and any of its parent directories to
        /// the sorted list, if they're not there already.
        /// </summary>
        private void ShiftDirAddParents(IFileEntry entry, List<IFileEntry> sorted,
                Dictionary<IFileEntry, bool> dirs) {
            Debug.Assert(entry.IsDirectory);
            if (entry.ContainingDir != IFileEntry.NO_ENTRY) {
                ShiftDirAddParents(entry.ContainingDir, sorted, dirs);
            }
            if (!dirs.TryGetValue(entry, out bool alreadySeen)) {
                // Not part of selection.  Ignore it.
            } else if (!alreadySeen) {
                // Included in selection but not yet seen; add it now.
                //Debug.WriteLine("Moving dir parent earlier: " + entry);
                sorted.Add(entry);
                dirs[entry] = true;
            }
        }

        /// <summary>
        /// Handles Actions : Add Files
        /// </summary>
        public void AddFiles() {
            HandleAddImport(null);
        }

        /// <summary>
        /// Performs some basic checks before trying to add/paste/drop files.  Reports failures
        /// to the user.
        /// </summary>
        /// <returns>True if it's okay to paste or drop files here.</returns>
        internal bool CheckPasteDropOkay() {
            if (!CanWrite) {
                if (CurrentWorkObject is IFileSystem) {
                    MessageBox.Show("Can't add files to a read-only filesystem.", "Unable to add",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                } else {
                    MessageBox.Show("Can't add files to a read-only archive.", "Unable to add",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
                return false;
            }
            if (!IsMultiFileItemSelected) {
                MessageBox.Show("Can't add files to a single-file archive.", "Unable to add",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
            return true;
        }

        /// <summary>
        /// Handles paste and file-list drop operations.
        /// </summary>
        public void PasteOrDrop(IDataObject? dropObj, IFileEntry dropTarget) {
            if (!CheckPasteDropOkay()) {
                return;
            }

            IDataObject dataObj = (dropObj != null) ? dropObj : Clipboard.GetDataObject();
            ClipInfo? clipInfo = ClipInfo.GetClipInfo(dropObj);
            if (clipInfo == null || clipInfo.ClipEntries == null) {
                // TODO? handle paste from Windows Explorer ZIP folder
                return;
            }
            if (clipInfo.IsExport) {
                MessageBox.Show(mMainWin,
                    "The file copy was performed in \"export\" mode.  Please use \"extract\" " +
                    "mode when copying files between instances of CiderPress II.",
                    "Can't Paste Exports", MessageBoxButton.OK, MessageBoxImage.Stop);
                return;
            }

            if (clipInfo.AppVersionMajor != GlobalAppVersion.AppVersion.Major ||
                    clipInfo.AppVersionMinor != GlobalAppVersion.AppVersion.Minor ||
                    clipInfo.AppVersionPatch != GlobalAppVersion.AppVersion.Patch) {
                MessageBox.Show(mMainWin,
                    "Cannot copy & paste between different versions of the application.",
                    "Version Mismatch", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (clipInfo.ClipEntries.Count == 0) {
                Debug.WriteLine("Pasting empty file set");
                return;
            }
            AppHook.LogI("Paste from clipboard; found " + clipInfo.ClipEntries.Count +
                " files/forks");

            // Move us to the front of the application window stack.  This is useful when we're
            // receiving a drop: if we're overlapping with the source window and put up an error
            // dialog, it might be hidden.
            mMainWin.Activate();

            if (!GetSelectedArcDir(out object? archiveOrFileSystem, out DiskArcNode? daNode,
                    out IFileEntry targetDir)) {
                // We don't have an archive and (optionally) directory target selected.
                return;
            }
            if (dropTarget != IFileEntry.NO_ENTRY && dropTarget.IsDirectory) {
                targetDir = dropTarget;
            }
            if (clipInfo.ProcessId == Process.GetCurrentProcess().Id &&
                    archiveOrFileSystem is IArchive) {
                // This isn't going to work, unless we extract everything to temp files first.
                MessageBox.Show(mMainWin,
                    "Files cannot be copied and pasted from a file archive to itself.",
                    "Conflict", MessageBoxButton.OK, MessageBoxImage.Stop);
                return;
            }

            // Define a delegate that provides a read-only non-seekable stream for the contents
            // of a given entry.
            ClipPasteWorker.ClipStreamGenerator streamGen = delegate (ClipFileEntry clipEntry) {
                int index = clipInfo.ClipEntries.IndexOf(clipEntry);
                if (index < 0) {
                    Debug.WriteLine("Unable to find stream index " + index);
                    return null;
                }
                if (dropObj == null) {
                    // From clipboard.
                    return ClipHelper.GetClipboardContentsSTA(index, ClipInfo.XFER_STREAMS);
                } else {
                    // From drag & drop object.
                    return ClipHelper.GetFileContents(dropObj, index, ClipInfo.XFER_STREAMS);
                }
            };

            SettingsHolder settings = AppSettings.Global;
            ClipPasteProgress prog =
                new ClipPasteProgress(archiveOrFileSystem, daNode, targetDir, clipInfo, streamGen,
                        AppHook) {
                    DoCompress = settings.GetBool(AppSettings.ADD_COMPRESS_ENABLED, true),
                    EnableMacOSZip = settings.GetBool(AppSettings.MAC_ZIP_ENABLED, true),
                    ConvertDOSText = settings.GetBool(AppSettings.DOS_TEXT_CONV_ENABLED, false),
                    StripPaths = settings.GetBool(AppSettings.ADD_STRIP_PATHS_ENABLED, false),
                    RawMode = settings.GetBool(AppSettings.ADD_RAW_ENABLED, false),
                };

            // Do the extraction on a background thread so we can show progress.
            WorkProgress workDialog = new WorkProgress(mMainWin, prog, false);
            if (workDialog.ShowDialog() == true) {
                mMainWin.PostNotification("Files added", true);
            } else {
                mMainWin.PostNotification("Cancelled", false);
            }

            // Refresh the contents of the file list.  Do this even if the operation was
            // cancelled, because it might have completed partially.
            RefreshDirAndFileList();
        }

        private void HandleAddImport(ConvConfig.FileConvSpec? spec) {
            //OpenFileDialog fileDlg = new OpenFileDialog() {
            //    Filter = WinUtil.FILE_FILTER_ALL,
            //    FilterIndex = 1,
            //    Multiselect = true,
            //    Title = "Select Files to Add (No Directories)"
            //};

            string initialDir = AppSettings.Global.GetString(AppSettings.LAST_ADD_DIR,
                Environment.CurrentDirectory);

            FileSelector fileDlg = new FileSelector(mMainWin, FileSelector.SelMode.FilesAndFolders,
                    initialDir);
            if (fileDlg.ShowDialog() != true) {
                return;
            }
            AppSettings.Global.SetString(AppSettings.LAST_ADD_DIR, fileDlg.BasePath);
            AddPaths(fileDlg.SelectedPaths, IFileEntry.NO_ENTRY, spec);
        }

        /// <summary>
        /// Handles file drop.
        /// </summary>
        /// <param name="dropTarget">File entry onto which the files were dropped, or
        ///   NO_ENTRY for archives.</param>
        /// <param name="pathNames">List of pathnames to add.</param>
        public void AddFileDrop(IFileEntry dropTarget, string[] pathNames) {
            Debug.Assert(pathNames.Length > 0);
            Debug.WriteLine("External file drop (target=" + dropTarget + "):");
            if (!CheckPasteDropOkay()) {
                return;
            }
            AddPaths(pathNames, dropTarget,
                mMainWin.IsChecked_ImportExport ? GetImportSpec() : null);
        }

        private void AddPaths(string[] pathNames, IFileEntry dropTarget,
                ConvConfig.FileConvSpec? importSpec) {
            Debug.WriteLine("Add paths (importSpec=" + importSpec + "):");
            foreach (string path in pathNames) {
                Debug.WriteLine("  " + path);
            }

            if (!GetSelectedArcDir(out object? archiveOrFileSystem, out DiskArcNode? daNode,
                    out IFileEntry targetDir)) {
                // We don't have an archive and (optionally) directory target selected.
                return;
            }
            if (dropTarget != IFileEntry.NO_ENTRY && dropTarget.IsDirectory) {
                targetDir = dropTarget;
            }

            // Since we're dragging files out of an explorer window, all files should have the
            // same directory name.
            string basePath = Path.GetDirectoryName(pathNames[0])!;

            AddFileSet.AddOpts addOpts = ConfigureAddOpts(importSpec != null);
            AddFileSet fileSet;
            try {
                fileSet = new AddFileSet(basePath, pathNames, addOpts, importSpec, AppHook);
            } catch (IOException ex) {
                ShowFileError(ex.Message);
                return;
            }
            if (fileSet.Count == 0) {
                Debug.WriteLine("File set was empty");
                return;
            }

            SettingsHolder settings = AppSettings.Global;
            AddProgress prog =
                new AddProgress(archiveOrFileSystem, daNode, fileSet, targetDir, AppHook) {
                    DoCompress = settings.GetBool(AppSettings.ADD_COMPRESS_ENABLED, true),
                    EnableMacOSZip = settings.GetBool(AppSettings.MAC_ZIP_ENABLED, true),
                    StripPaths = settings.GetBool(AppSettings.ADD_STRIP_PATHS_ENABLED, false),
                    RawMode = settings.GetBool(AppSettings.ADD_RAW_ENABLED, false),
                };

            // Do the extraction on a background thread so we can show progress.
            WorkProgress workDialog = new WorkProgress(mMainWin, prog, false);
            if (workDialog.ShowDialog() == true) {
                if (importSpec == null) {
                    mMainWin.PostNotification("Files added", true);
                } else {
                    mMainWin.PostNotification("Files imported", true);
                }
            } else {
                mMainWin.PostNotification("Cancelled", false);
            }

            // Refresh the contents of the file list.  Do this even if the operation was
            // cancelled, because it might have completed partially.
            RefreshDirAndFileList();
        }

        private AddFileSet.AddOpts ConfigureAddOpts(bool isImport) {
            SettingsHolder settings = AppSettings.Global;
            AddFileSet.AddOpts addOpts = new AddFileSet.AddOpts();
            if (isImport) {
                // Anything stored with preserved attributes should be added, not imported.
                addOpts.ParseADF = addOpts.ParseAS = addOpts.ParseNAPS = addOpts.CheckNamed =
                    addOpts.CheckFinderInfo = false;
            } else {
                addOpts.ParseADF = settings.GetBool(AppSettings.ADD_PRESERVE_ADF, true);
                addOpts.ParseAS = settings.GetBool(AppSettings.ADD_PRESERVE_AS, true);
                addOpts.ParseNAPS = settings.GetBool(AppSettings.ADD_PRESERVE_NAPS, true);
                addOpts.CheckNamed = false;         // not for Windows
                addOpts.CheckFinderInfo = false;    // not for Windows
            }
            addOpts.Recurse = settings.GetBool(AppSettings.ADD_RECURSE_ENABLED, true);
            addOpts.StripExt = settings.GetBool(AppSettings.ADD_STRIP_EXT_ENABLED, true);
            return addOpts;
        }

        /// <summary>
        /// Handles Actions : Create Directory
        /// </summary>
        public void CreateDirectory() {
            if (!GetSelectedArcDir(out object? archiveOrFileSystem, out DiskArcNode? daNode,
                    out IFileEntry targetDir)) {
                return;
            }
            IFileSystem? fs = archiveOrFileSystem as IFileSystem;
            if (fs == null) {
                Debug.Assert(false);
                return;
            }

            string rules = "\u2022 " + fs.Characteristics.FileNameSyntaxRules;
            CreateDirectory dialog = new CreateDirectory(mMainWin, fs, targetDir,
                fs.IsValidFileName, rules);
            if (dialog.ShowDialog() != true) {
                return;
            }

            try {
                IFileEntry newEntry = fs.CreateFile(targetDir, dialog.NewFileName,
                    IFileSystem.CreateMode.Directory);
                // Refresh the directory and file lists.
                RefreshDirAndFileList();
                FileListItem.SetSelectionFocusByEntry(mMainWin.FileList, mMainWin.fileListDataGrid,
                    newEntry);
                mMainWin.PostNotification("Directory created", true);
            } catch (Exception ex) {
                MessageBox.Show(mMainWin, "Failed: " + ex.Message, "Failed",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Handles Actions : Defragment
        /// </summary>
        public void Defragment() {
            Pascal? fs = CurrentWorkObject as Pascal;
            if (fs == null) {
                Debug.Assert(false);
                return;
            }

            // We assume that defragmentation isn't going to render the filesystem unusable or
            // significantly different -- in theory the list of files should be identical -- so
            // we shouldn't need to close and re-scan the work tree unless the filesystem has
            // embedded volumes.  We currently only do this for Pascal, which doesn't have embeds.
            // We do need to re-scan the file list because the IFileEntry objects have all changed.
            bool ok = false;
            try {
                fs.PrepareRawAccess();
                ok = fs.Defragment();
            } catch (Exception ex) {
                // This is bad.  Hopefully it blew up before anything got shuffled.
                MessageBox.Show(mMainWin, "Error: " + ex.Message, "Failed",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            } finally {
                fs.PrepareFileAccess(true);
                RefreshDirAndFileList();
            }
            if (ok) {
                mMainWin.PostNotification("Defragmentation successful", true);
            } else {
                MessageBox.Show(mMainWin, "Filesystems with errors cannot be defragmented.",
                    "Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Handles Actions : Delete Files
        /// </summary>
        public void DeleteFiles() {
            if (!GetSelectedArcDir(out object? archiveOrFileSystem, out DiskArcNode? daNode,
                    out IFileEntry unused)) {
                // We don't have an archive and (optionally) directory target selected.
                return;
            }

            if (!GetFileSelection(omitDir:false, omitOpenArc:false, closeOpenArc:true,
                    oneMeansAll:false, out archiveOrFileSystem, out IFileEntry unusedDir,
                    out List<IFileEntry>? selected, out int unused1)) {
                return;
            }
            if (selected.Count == 0) {
                MessageBox.Show(mMainWin, "No files selected.", "Empty", MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            int firstSelIndex = mMainWin.fileListDataGrid.SelectedIndex;

            // We can't undo it, so get confirmation first.
            string msg = string.Format("Delete {0} file {1}?", selected.Count,
                selected.Count == 1 ? "entry" : "entries");
            MessageBoxResult res = MessageBox.Show(mMainWin, msg, "Confirm Delete",
                MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (res != MessageBoxResult.OK) {
                return;
            }
            SettingsHolder settings = AppSettings.Global;
            DeleteProgress prog = new DeleteProgress(archiveOrFileSystem, daNode, selected,
                    AppHook) {
                DoCompress = settings.GetBool(AppSettings.ADD_COMPRESS_ENABLED, true),
                EnableMacOSZip = settings.GetBool(AppSettings.MAC_ZIP_ENABLED, true),
            };

            // Do the deletion on a background thread so we can show progress.
            WorkProgress workDialog = new WorkProgress(mMainWin, prog, false);
            bool didCancel;
            if (workDialog.ShowDialog() == true) {
                mMainWin.PostNotification("Deletion successful", true);
                didCancel = false;
            } else {
                mMainWin.PostNotification("Cancelled", false);
                didCancel = true;
            }

            if (didCancel && archiveOrFileSystem is IArchive) {
                // Nothing will have changed, don't mess with selection.
            } else {
                // Put the selection on the item above the first one we deleted.
                if (firstSelIndex > 0) {
                    mMainWin.fileListDataGrid.SelectRowByIndex(firstSelIndex - 1);
                }
            }

            // Refresh the directory and file lists.
            RefreshDirAndFileList();
        }

        /// <summary>
        /// Handles Actions : Edit Attributes
        /// </summary>
        public void EditAttributes() {
            ArchiveTreeItem? arcTreeSel = mMainWin.SelectedArchiveTreeItem;
            FileListItem? fileItem = mMainWin.SelectedFileListItem;
            if (arcTreeSel == null || fileItem == null) {
                Debug.Assert(false);
                return;
            }

            // If this is a directory, find the matching item (no effect on IArchive).
            DirectoryTreeItem? dirTreeItem = null;
            if (fileItem.FileEntry.IsDirectory) {
                dirTreeItem = DirectoryTreeItem.FindItemByEntry(mMainWin.DirectoryTreeRoot,
                    fileItem.FileEntry);
            }
            Debug.WriteLine("Editing " + fileItem.FileEntry + " in " +
                arcTreeSel.WorkTreeNode.DAObject);

            EditAttributes(arcTreeSel.WorkTreeNode, fileItem.FileEntry, dirTreeItem, fileItem);
        }

        /// <summary>
        /// Handles Actions : Edit Directory Attributes
        /// </summary>
        public void EditDirAttributes() {
            ArchiveTreeItem? arcTreeSel = mMainWin.SelectedArchiveTreeItem;
            DirectoryTreeItem? dirTreeSel = mMainWin.SelectedDirectoryTreeItem;
            if (arcTreeSel == null || dirTreeSel == null) {
                Debug.Assert(false);
                return;
            }

            // The file list item will also be available if we're in full-list mode.  It will
            // not necessarily be the item that is selected.
            FileListItem? fileItem =
                FileListItem.FindItemByEntry(mMainWin.FileList, dirTreeSel.FileEntry);
            EditAttributes(arcTreeSel.WorkTreeNode, dirTreeSel.FileEntry, dirTreeSel, fileItem);
        }

        private void EditAttributes(WorkTree.Node workNode, IFileEntry entry,
                DirectoryTreeItem? dirItem, FileListItem? fileItem) {
            Debug.WriteLine("EditAttributes: " + entry);
            object archiveOrFileSystem = workNode.DAObject;

            bool isMacZip = false;
            bool isMacZipEnabled = AppSettings.Global.GetBool(AppSettings.MAC_ZIP_ENABLED, true);
            IFileEntry adfEntry = IFileEntry.NO_ENTRY;
            IFileEntry adfArchiveEntry = IFileEntry.NO_ENTRY;
            if (isMacZipEnabled && workNode.DAObject is Zip &&
                    Zip.HasMacZipHeader((Zip)workNode.DAObject, entry, out adfEntry)) {
                // Extract the archive and get the first entry.
                Zip arc = (Zip)workNode.DAObject;
                using (Stream adfStream = ArcTemp.ExtractToTemp(arc, adfEntry, FilePart.DataFork)) {
                    using (IArchive adfArchive = AppleSingle.OpenArchive(adfStream, AppHook)) {
                        adfArchiveEntry = adfArchive.GetFirstEntry();
                    }
                }
                isMacZip = true;
            }
            FileAttribs curAttribs = new FileAttribs(entry);
            if (isMacZip) {
                // Get the attributes from the contents of the AppleSingle entry, but use the
                // filename from the main entry.
                curAttribs.GetFromAppleSingle(adfArchiveEntry);
                curAttribs.FullPathName = entry.FullPathName;
            }

            EditAttributes dialog = new EditAttributes(mMainWin, archiveOrFileSystem, entry,
                adfArchiveEntry, curAttribs);
            if (dialog.ShowDialog() != true) {
                return;
            }
            // TODO: compare curAttribs to new attribs; if no change, do nothing.

            SettingsHolder settings = AppSettings.Global;
            EditAttributesProgress prog = new EditAttributesProgress(mMainWin, workNode.DAObject,
                    workNode.FindDANode(), entry, adfEntry, dialog.NewAttribs, AppHook) {
                DoCompress = settings.GetBool(AppSettings.ADD_COMPRESS_ENABLED, true),
                EnableMacOSZip = isMacZipEnabled,
            };

            // Currently doing updates on GUI thread.  Errors are reported to user.
            if (prog.DoUpdate(isMacZip)) {
                mMainWin.PostNotification("File attributes edited", true);

                if (fileItem != null) {
                    // We may need to update multiple columns, so best to just regenerate the
                    // entire item.
                    FileListItem newFli;
                    if (isMacZip) {
                        newFli = new FileListItem(entry, adfEntry, dialog.NewAttribs, mFormatter);
                    } else {
                        newFli = new FileListItem(entry, mFormatter);
                    }
                    int index = mMainWin.FileList.IndexOf(fileItem);
                    Debug.Assert(index >= 0);
                    mMainWin.FileList[index] = newFli;
                    // Set the selection.  This causes a refresh (via selection-changed event).
                    mMainWin.SelectedFileListItem = newFli;

                    ArchiveTreeItem? ati =
                        ArchiveTreeItem.FindItemByEntry(mMainWin.ArchiveTreeRoot, entry);
                    if (ati != null) {
                        ati.Name = entry.FileName;
                        // Should we update WorkTree.Node.Label?
                    }
                }

                if (dirItem != null) {
                    // Only the name appears in the list, so we just need to update that.
                    dirItem.Name = entry.FileName;

                    if (entry.IsDirectory && entry.ContainingDir == IFileEntry.NO_ENTRY) {
                        // Volume dir.  Find the archive entry.
                        ArchiveTreeItem? ati =
                            ArchiveTreeItem.FindItemByDAObject(mMainWin.ArchiveTreeRoot,
                            archiveOrFileSystem);
                        if (ati != null) {
                            ati.Name = entry.FileName;
                            // Should we update WorkTree.Node.Label?
                        } else {
                            Debug.Assert(false, "archive tree entry not found for vol dir");
                        }
                    }
                }
            }

            // Refresh the directory and file lists.  In some cases this is unnecessary because
            // the file selection update did the refresh for us.
            Debug.WriteLine("Edit attributes: final refresh");
            RefreshDirAndFileList();
        }

        /// <summary>
        /// Handles Actions : Edit Sectors / Blocks / Blocks (CPM)
        /// </summary>
        public void EditBlocksSectors(EditSector.SectorEditMode editMode) {
            Debug.Assert(mWorkTree != null);
            ArchiveTreeItem? arcTreeSel = mMainWin.SelectedArchiveTreeItem;
            if (arcTreeSel == null) {
                Debug.Assert(false);
                return;
            }
            WorkTree.Node workNode = arcTreeSel.WorkTreeNode;
            object daObject = workNode.DAObject;
            IChunkAccess? chunks;
            //IFileSystem? fs;
            if (daObject is IDiskImage) {
                chunks = ((IDiskImage)daObject).ChunkAccess;
                //fs = ((IDiskImage)daObject).Contents as IFileSystem;
            } else if (daObject is Partition) {
                chunks = ((Partition)daObject).ChunkAccess;
                //fs = ((Partition)daObject).FileSystem;
            } else {
                Debug.Assert(false, "unexpected sector edit target: " + daObject);
                return;
            }
            if (chunks == null) {
                MessageBox.Show(mMainWin, "Disk sector format not recognized", "Trouble",
                    MessageBoxButton.OK, MessageBoxImage.Stop);
                return;
            }

            bool writeEnabled = false;
            EditSector.EnableWriteFunc? func = null;
            if (!chunks.IsReadOnly) {
                func = delegate () {
                    // Close everything below the current node.
                    Debug.Assert(mWorkTree.CheckHealth());
                    workNode.CloseChildren();
                    // Close the IFileSystem or IMultiPart object inside the disk/partition.
                    if (daObject is IDiskImage) {
                        ((IDiskImage)daObject).CloseContents();
                    } else if (daObject is Partition) {
                        ((Partition)daObject).CloseContents();
                    }
                    Debug.Assert(mWorkTree.CheckHealth());
                    // Drop all child references.
                    arcTreeSel.Items.Clear();
                    writeEnabled = true;
                    return true;
                };
            }
            EditSector dialog = new EditSector(mMainWin, chunks, editMode, func, mFormatter);
            dialog.ShowDialog();
            Debug.WriteLine("After dialog, enabled=" + writeEnabled);

            // Flush any pending changes made to the disk image.
            if (daObject is IDiskImage) {
                ((IDiskImage)daObject).Flush();
            }

            if (writeEnabled) {
                try {
                    // TODO: should probably show a progress dialog here
                    Mouse.OverrideCursor = Cursors.Wait;

                    // Re-scan the disk image.
                    if (daObject is IDiskImage) {
                        mWorkTree.ReprocessDiskImage(workNode);
                    } else if (daObject is Partition) {
                        mWorkTree.ReprocessPartition(workNode);
                    }
                    // Repopulate the archive tree.
                    foreach (WorkTree.Node childNode in workNode) {
                        ArchiveTreeItem.ConstructTree(arcTreeSel, childNode);
                    }
                } finally {
                    Mouse.OverrideCursor = null;
                }
            }
        }

        /// <summary>
        /// Handles Actions : Export Files
        /// </summary>
        public void ExportFiles() {
            HandleExtractExport(GetExportSpec());
        }

        /// <summary>
        /// Returns the import specification for the current import mode.
        /// </summary>
        private ConvConfig.FileConvSpec GetImportSpec() {
            string convTag = mMainWin.ImportConvTag;
            string settingKey = AppSettings.IMPORT_SETTING_PREFIX + convTag;
            string convSettings = AppSettings.Global.GetString(settingKey, string.Empty);

            ConvConfig.FileConvSpec? spec;
            if (string.IsNullOrEmpty(convSettings)) {
                spec = ConvConfig.CreateSpec(convTag);
            } else {
                spec = ConvConfig.CreateSpec(convTag + "," + convSettings);
            }
            if (spec == null) {
                // String parsing failure.  Use default options.
                Debug.Assert(false);
                AppHook.LogW("Failed to parse converter settings for " + convTag + " '" +
                    convSettings + "'");
                spec = ConvConfig.CreateSpec(convTag);
                Debug.Assert(spec != null);
            }
            //Debug.WriteLine("Import spec: " + spec);
            return spec;
        }

        /// <summary>
        /// Returns the export specification for the current export mode.
        /// </summary>
        /// <param name="convTag">If non-null, the method returns the export specification for
        ///   the specified tag instead of the current mode.</param>
        private ConvConfig.FileConvSpec GetExportSpec(string? convTag = null) {
            if (convTag == null) {
                convTag = mMainWin.IsExportBestChecked ? ConvConfig.BEST : mMainWin.ExportConvTag;
            }
            string settingKey = AppSettings.EXPORT_SETTING_PREFIX + convTag;
            string convSettings = AppSettings.Global.GetString(settingKey, string.Empty);

            ConvConfig.FileConvSpec? spec;
            if (string.IsNullOrEmpty(convSettings)) {
                spec = ConvConfig.CreateSpec(convTag);
            } else {
                spec = ConvConfig.CreateSpec(convTag + "," + convSettings);
            }
            if (spec == null) {
                // String parsing failure.  Use default options.
                Debug.Assert(false);
                AppHook.LogW("Failed to parse converter settings for " + convTag + " '" +
                    convSettings + "'");
                spec = ConvConfig.CreateSpec(convTag);
                Debug.Assert(spec != null);
            }
            //Debug.WriteLine("Export spec: " + spec);
            return spec;
        }

        /// <summary>
        /// Returns a dictionary with default export specifications for all known tags.
        /// </summary>
        private Dictionary<string, ConvConfig.FileConvSpec>? GetDefaultExportSpecs() {
            Dictionary<string, ConvConfig.FileConvSpec> defaults =
                new Dictionary<string, ConvConfig.FileConvSpec>();
            List<string>? tags = ExportFoundry.GetConverterTags();
            foreach (string tag in tags) {
                defaults[tag] = GetExportSpec(tag);
            }
            return defaults;
        }

        /// <summary>
        /// Handles Actions : Extract Files
        /// </summary>
        public void ExtractFiles() {
            HandleExtractExport(null);
        }

        /// <summary>
        /// Handles Edit : Copy
        /// </summary>
        /// <remarks>
        /// Creates a clipboard data object with the contents of the selected files.
        /// </remarks>
        public void CopyToClipboard() {
            if (mMainWin.fileListDataGrid.SelectedItems.Count == 0) {
                Debug.Assert(false);
                return;
            }
            VirtualFileDataObject vfdo = GenerateVFDO();
            Clipboard.SetDataObject(vfdo);
            Debug.WriteLine("Copied to clipboard");
        }

        /// <summary>
        /// Generates a list of "virtual" files for drag+drop and the clipboard.  The list is
        /// from the current file list selection.
        /// </summary>
        internal VirtualFileDataObject GenerateVFDO() {
            DataGrid dataGrid = mMainWin.fileListDataGrid;
            if (dataGrid.SelectedItems.Count == 0) {
                // continue anyway to create empty obj
            }

            // Gather list of entries, descending into subdirectories.  We want to close any
            // open archives before trying to copy them.
            List<IFileEntry> entries;
            if (!GetFileSelection(omitDir: false, omitOpenArc: false, closeOpenArc: true,
                    oneMeansAll: false, out object? archiveOrFileSystem,
                    out IFileEntry selectionDir, out List<IFileEntry>? selected, out int unused)) {
                entries = new List<IFileEntry>();   // continue with an empty selection
            } else {
                entries = selected;
            }

            IFileEntry baseDir = IFileEntry.NO_ENTRY;       // equivalent to volume dir
            if (CurrentWorkObject is IFileSystem && mMainWin.ShowSingleDirFileList) {
                DirectoryTreeItem? dirItem = mMainWin.SelectedDirectoryTreeItem;
                if (dirItem != null) {
                    // We're in single-directory view mode, so make the output filenames relative
                    // to the currently selected directory.
                    baseDir = dirItem.FileEntry;
                }
            }

            // Collect relevant settings.
            SettingsHolder settings = AppSettings.Global;
            ExtractFileWorker.PreserveMode preserve =
                settings.GetEnum(AppSettings.EXT_PRESERVE_MODE,
                    ExtractFileWorker.PreserveMode.None);
            bool rawMode = settings.GetBool(AppSettings.EXT_RAW_ENABLED, false);
            bool doStrip = settings.GetBool(AppSettings.EXT_STRIP_PATHS_ENABLED, false);
            bool doMacZip = settings.GetBool(AppSettings.MAC_ZIP_ENABLED, true);
            ConvConfig.FileConvSpec? exportSpec = null;
            Dictionary<string, ConvConfig.FileConvSpec>? defaultSpecs = null;
            if (mMainWin.IsChecked_ImportExport) {
                exportSpec = GetExportSpec();
                defaultSpecs = GetDefaultExportSpecs();
            }

            // Prepare the file set according to the current options.
            ClipFileSet clipSet;
            try {
                // Drag/copy for extract only examines data structures, but drag/copy for export
                // may have to open file contents.
                Mouse.OverrideCursor = Cursors.Wait;
                clipSet = new ClipFileSet(CurrentWorkObject!, entries, baseDir,
                    preserve, useRawData: rawMode, stripPaths: doStrip, enableMacZip: doMacZip,
                    exportSpec, defaultSpecs, AppHook);
            } finally {
                Mouse.OverrideCursor = null;
            }

            // Configure the virtual file descriptors that transmit the file contents.
            // The start/end actions on the VFDO only work if the drop *target* supports
            // drop as an asynchronous operation.  So they'll fire if dragging into Windows
            // Explorer but not into another CP2 window, unless we figure that part out.
            List<ClipFileEntry> clipEntries = clipSet.ForeignEntries;
            VirtualFileDataObject vfdo = new VirtualFileDataObject();
            VirtualFileDataObject.FileDescriptor[] vfds =
                new VirtualFileDataObject.FileDescriptor[clipEntries.Count];

            for (int i = 0; i < clipEntries.Count; i++) {
                ClipFileEntry clipEntry = clipEntries[i];
                // Set the values that go into the Windows FILEDESCRIPTOR struct.  This won't be
                // used if the receiver is a second copy of us.
                vfds[i] = new VirtualFileDataObject.FileDescriptor();
                vfds[i].Name = clipEntry.ExtractPath;
                vfds[i].Length = clipEntry.OutputLength;
                if (TimeStamp.IsValidDate(clipEntry.Attribs.CreateWhen)) {
                    vfds[i].CreateTimeUtc = clipEntry.Attribs.CreateWhen;
                }
                if (TimeStamp.IsValidDate(clipEntry.Attribs.ModWhen)) {
                    vfds[i].ChangeTimeUtc = clipEntry.Attribs.ModWhen;
                }
                if (clipEntry.Attribs.IsDirectory) {
                    vfds[i].IsDirectory = true;
                } else {
                    vfds[i].StreamContents = stream => {
                        clipEntry.mStreamGen!.OutputToStream(stream);
                    };
               }
            }
            vfdo.SetData(vfds);

            // Serialize direct transfer data.  There won't be any for "export" mode.
            if (clipSet.XferEntries.Count > 0) {
                Debug.Assert(exportSpec == null);
                ClipInfo clipInfo = new ClipInfo(clipSet.XferEntries, GlobalAppVersion.AppVersion);
                string cereal = JsonSerializer.Serialize(clipInfo,
                    new JsonSerializerOptions() { WriteIndented = true });
                vfdo.SetData(ClipInfo.XFER_METADATA, Encoding.UTF8.GetBytes(cereal));

                for (int i = 0; i < clipSet.XferEntries.Count; i++) {
                    ClipFileEntry clipEntry = clipSet.XferEntries[i];
                    if (!clipEntry.Attribs.IsDirectory) {
                        vfdo.SetData(ClipInfo.XFER_STREAMS, i, stream => {
                            clipEntry.mStreamGen!.OutputToStream(stream);
                        });
                    }
                }
            } else if (exportSpec != null) {
                // Create an empty set.  This allows the remote side to see that it was created
                // by us, but there was nothing to do.
                ClipInfo clipInfo = new ClipInfo(GlobalAppVersion.AppVersion);
                clipInfo.IsExport = true;
                string cereal = JsonSerializer.Serialize(clipInfo,
                    new JsonSerializerOptions() { WriteIndented = true });
                vfdo.SetData(ClipInfo.XFER_METADATA, Encoding.UTF8.GetBytes(cereal));
            } else {
                // Not export, but nothing to copy.  Don't create a ClipInfo.
            }

            //vfdo.SetData(DataFormats.UnicodeText,
            //    "This is a test (" + dataGrid.SelectedItems.Count + ")");
            //vfdo.SetData(DataFormats.Text,
            //    "This is plain text (" + dataGrid.SelectedItems.Count + ")");

            AppHook.LogI("Generated VFDO with foreign=" + clipSet.ForeignEntries.Count +
                " xfer=" + clipSet.XferEntries.Count);

            return vfdo;
        }

        private void HandleExtractExport(ConvConfig.FileConvSpec? exportSpec) {
            if (!GetFileSelection(omitDir: false, omitOpenArc: false, closeOpenArc: true,
                    oneMeansAll: false, out object? archiveOrFileSystem,
                    out IFileEntry selectionDir, out List<IFileEntry>? selected, out int unused)) {
                return;
            }
            if (selected.Count == 0) {
                MessageBox.Show(mMainWin, "No files selected.", "Empty", MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            // If we're in full list mode, don't use the directory selection to trim the paths.
            if (archiveOrFileSystem is IFileSystem && !mMainWin.ShowSingleDirFileList) {
                selectionDir = ((IFileSystem)archiveOrFileSystem).GetVolDirEntry();
            }

            string initialDir = AppSettings.Global.GetString(AppSettings.LAST_EXTRACT_DIR,
                Environment.CurrentDirectory);

            //BrowseForFolder folderDialog = new BrowseForFolder();
            //string? outputDir = folderDialog.SelectFolder("Select destination for " +
            //    (exportSpec == null ? "extracted" : "exported") + " files:",
            //    initialDir, mMainWin);
            //if (outputDir == null) {
            //    return;
            //}
            FileSelector fileDialog = new FileSelector(mMainWin, FileSelector.SelMode.SingleFolder,
                initialDir);
            if (fileDialog.ShowDialog() != true) {
                return;
            }
            string outputDir = fileDialog.BasePath;

            SettingsHolder settings = AppSettings.Global;
            settings.SetString(AppSettings.LAST_EXTRACT_DIR, outputDir);

            ExtractProgress prog = new ExtractProgress(archiveOrFileSystem, selectionDir,
                    selected, outputDir, exportSpec, AppHook) {
                Preserve = settings.GetEnum(AppSettings.EXT_PRESERVE_MODE,
                    ExtractFileWorker.PreserveMode.None),
                EnableMacOSZip = settings.GetBool(AppSettings.MAC_ZIP_ENABLED, true),
                StripPaths = settings.GetBool(AppSettings.EXT_STRIP_PATHS_ENABLED, false),
                RawMode = settings.GetBool(AppSettings.EXT_RAW_ENABLED, false),
                DefaultSpecs = GetDefaultExportSpecs()
            };
            Debug.WriteLine("Extract: outputDir='" + outputDir +
                "', selectionDir='" + selectionDir + "'");

            // Do the extraction on a background thread so we can show progress.
            WorkProgress workDialog = new WorkProgress(mMainWin, prog, false);
            if (workDialog.ShowDialog() == true) {
                if (exportSpec != null) {
                    mMainWin.PostNotification("Export successful", true);
                } else {
                    mMainWin.PostNotification("Extraction successful", true);
                }
            } else {
                mMainWin.PostNotification("Cancelled", false);
            }
        }

        /// <summary>
        /// Handles Edit : Find Files
        /// </summary>
        public void FindFiles() {
            FindFile dialog = new FindFile(mMainWin);
            dialog.FindRequested += DoFindFiles;        // request arrives through this
            dialog.ShowDialog();
        }

        /// <summary>
        /// Handles Actions : Export Files
        /// </summary>
        public void ImportFiles() {
            HandleAddImport(GetImportSpec());
        }

        /// <summary>
        /// Handles moving files between directories (via drag and drop).  This is only
        /// useful for hierarchical filesystems.
        /// </summary>
        public void MoveFiles(List<IFileEntry> moveList, IFileEntry targetDir) {
            if (CurrentWorkObject is not IFileSystem) {
                Debug.WriteLine("Ignoring move request in " + CurrentWorkObject);
                return;
            }
            if (targetDir == IFileEntry.NO_ENTRY) {
                // Dragging files from an IArchive into the directory tree does this.
                Debug.Assert(false, "Drag target is NO_FILE");
                return;
            }
            if (!CanWrite) {
                MessageBox.Show(mMainWin, "Drop target is not writable", "Not Writable",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            if (targetDir.IsDubious || targetDir.IsDamaged) {
                MessageBox.Show(mMainWin, "Destination directory is not writable", "Not Writable",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            if (!targetDir.IsDirectory) {
                Debug.Assert(false, "bad move request");
                return;
            }

            // Screen out invalid and no-op requests.
            for (int i = moveList.Count - 1; i >= 0; i--) {
                IFileEntry entry = moveList[i];
                if (entry.ContainingDir == targetDir) {
                    Debug.WriteLine("- ignoring non-move " + entry + " -> " + targetDir);
                    moveList.RemoveAt(i);
                }
                if (entry.IsDirectory) {
                    // Make sure we're not trying to move a directory into itself or into a
                    // descendant.
                    IFileEntry checkEnt = targetDir;
                    while (checkEnt != IFileEntry.NO_ENTRY) {
                        if (checkEnt == entry) {
                            MessageBox.Show(mMainWin,
                                "Cannot move a directory into itself or a descendant", "Bad Move",
                                MessageBoxButton.OK, MessageBoxImage.Error);
                            return;
                        }
                        checkEnt = checkEnt.ContainingDir;
                    }
                }
            }
            if (moveList.Count == 0) {
                // Should we show a message to the user?
                Debug.WriteLine("Nothing to move");
                return;
            }

            Debug.WriteLine("Move files to " + targetDir + ":");
            foreach (IFileEntry entry in moveList) {
                Debug.WriteLine("  " + entry);
            }

            if (!GetSelectedArcDir(out object? archiveOrFileSystem, out DiskArcNode? daNode,
                    out IFileEntry unused)) {
                // shouldn't be possible
                Debug.Assert(false);
                return;
            }
            Debug.Assert(archiveOrFileSystem == CurrentWorkObject);

            SettingsHolder settings = AppSettings.Global;
            MoveProgress prog = new MoveProgress(CurrentWorkObject, daNode, moveList, targetDir,
                    AppHook) {
                DoCompress = settings.GetBool(AppSettings.ADD_COMPRESS_ENABLED, true),
            };

            // Do the move on a background thread so we can show progress.
            WorkProgress workDialog = new WorkProgress(mMainWin, prog, false);
            if (workDialog.ShowDialog() == true) {
                mMainWin.PostNotification("Move successful", true);
            } else {
                mMainWin.PostNotification("Cancelled", false);
            }

            // Clear selection, which won't be valid once we replace the file list items.
            mMainWin.fileListDataGrid.SelectedItems.Clear();

            // Generate new file list items for all moved entries.  This is necessary because
            // the pathname fields have changed.  (Shouldn't be necessary in single-dir mode,
            // because we don't currently show the pathname field in that configuration.)
            foreach (IFileEntry entry in moveList) {
                FileListItem? item = FileListItem.FindItemByEntry(mMainWin.FileList, entry,
                        out int itemIndex);
                if (item == null) {
                    Debug.Assert(false, "unable to find entry: " + entry);
                    continue;
                }
                FileListItem newItem = new FileListItem(entry, mFormatter);
                mMainWin.FileList.RemoveAt(itemIndex);
                mMainWin.FileList.Insert(itemIndex, newItem);

                // Add the new item to the selection set.
                // (this is currently being partially un-done by the refresh code)
                mMainWin.fileListDataGrid.SelectedItems.Add(newItem);
            }

            // Refresh the directory and file lists.
            RefreshDirAndFileList();
        }

        /// <summary>
        /// Handles Actions : Replace Partition
        /// </summary>
        public void ReplacePartition() {
            Debug.Assert(mWorkTree != null);
            ArchiveTreeItem? arcTreeSel = mMainWin.SelectedArchiveTreeItem;
            if (arcTreeSel == null) {
                Debug.Assert(false);
                return;
            }
            WorkTree.Node workNode = arcTreeSel.WorkTreeNode;
            Debug.Assert(workNode.DAObject == CurrentWorkObject);
            Partition? dstPartition = workNode.DAObject as Partition;
            if (dstPartition == null) {
                Debug.Assert(false);
                return;
            }

            // Get the name of the source disk image.
            OpenFileDialog fileDlg = new OpenFileDialog() {
                Filter = WinUtil.FILE_FILTER_KNOWN + "|" + WinUtil.FILE_FILTER_ALL_DISK,
                FilterIndex = 1
            };
            if (fileDlg.ShowDialog() != true) {
                return;
            }
            string pathName = Path.GetFullPath(fileDlg.FileName);
            string ext = Path.GetExtension(pathName);

            // Open the file.
            FileStream stream;
            try {
                stream = new FileStream(pathName, FileMode.Open, FileAccess.Read);
            } catch (Exception ex) {
                MessageBox.Show(mMainWin, "Unable to open file: " + ex.Message, "I/O Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Analyze the file contents.
            // TODO: this only accepts disk image files.  It will reject "file.po.gz" and
            // "file.sdk" because they are actually multi-file archives.  We can do better here,
            // handling the "simple" formats, but we probably don't want to get into disk archives
            // stored in ZIP files.
            using (stream) {
                FileAnalyzer.AnalysisResult result = FileAnalyzer.Analyze(stream, ext, AppHook,
                    out FileKind kind, out SectorOrder orderHint);
                string errMsg;
                switch (result) {
                    case FileAnalyzer.AnalysisResult.DubiousSuccess:
                    case FileAnalyzer.AnalysisResult.FileDamaged:
                        errMsg = "File is not usable due to possible damage.";
                        break;
                    case FileAnalyzer.AnalysisResult.UnknownExtension:
                        errMsg = "File format not recognized.";
                        break;
                    case FileAnalyzer.AnalysisResult.ExtensionMismatch:
                        errMsg = "File appears to have the wrong extension.";
                        break;
                    case FileAnalyzer.AnalysisResult.NotImplemented:
                        errMsg = "Support for this type of file has not been implemented.";
                        break;
                    case FileAnalyzer.AnalysisResult.Success:
                        if (!Defs.IsDiskImageFile(kind)) {
                            errMsg = "File is not a disk image.";
                        } else {
                            errMsg = string.Empty;
                        }
                        break;
                    default:
                        errMsg = "Internal error: unexpected result from analyzer: " + result;
                        break;
                }
                if (!string.IsNullOrEmpty(errMsg)) {
                    MessageBox.Show(mMainWin, errMsg, "Unable to use file",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Create disk image.
                using IDiskImage? diskImage = FileAnalyzer.PrepareDiskImage(stream, kind, AppHook);
                if (diskImage == null) {
                    errMsg =
                        "Unable to prepare disk image, type=" + ThingString.FileKind(kind) + ".";
                    MessageBox.Show(mMainWin, errMsg, "Unable to use file",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                // Attempt the disk analysis while we have the order hint handy.  We're only
                // interested in the chunks, not the filesystem.
                if (!diskImage.AnalyzeDisk(null, orderHint, IDiskImage.AnalysisDepth.ChunksOnly)) {
                    errMsg = "Unable to determine format of disk image contents.";
                    MessageBox.Show(mMainWin, errMsg, "Unable to use file",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                Debug.Assert(diskImage.ChunkAccess != null);

                // Define a function to close the contents of the partition we're replacing.
                bool wasClosed = false;
                ReplacePartition.EnableWriteFunc func = delegate () {
                    // Close everything below the current node.
                    Debug.Assert(mWorkTree.CheckHealth());
                    workNode.CloseChildren();
                    dstPartition.CloseContents();
                    Debug.Assert(mWorkTree.CheckHealth());
                    // Drop all child references.
                    arcTreeSel.Items.Clear();
                    wasClosed = true;
                    return true;
                };

                ReplacePartition dialog = new ReplacePartition(mMainWin, dstPartition,
                    diskImage.ChunkAccess, func, mFormatter, AppHook);
                if (dialog.ShowDialog() == true) {
                    mMainWin.PostNotification("Completed", true);
                }

                try {
                    // TODO: should probably show a progress dialog here
                    Mouse.OverrideCursor = Cursors.Wait;

                    if (wasClosed) {
                        // Re-scan the partition.
                        mWorkTree.ReprocessPartition(workNode);
                        // Repopulate the archive tree.
                        foreach (WorkTree.Node childNode in workNode) {
                            ArchiveTreeItem.ConstructTree(arcTreeSel, childNode);
                        }
                    }
                } finally {
                    Mouse.OverrideCursor = null;
                }
            }
        }

        /// <summary>
        /// Handles Actions : Save As Disk Image
        /// </summary>
        public void SaveAsDiskImage() {
            IChunkAccess? chunks = GetCurrentWorkChunks();
            if (chunks == null) {
                Debug.Assert(false);
                return;
            }

            SaveAsDisk dialog =
                new SaveAsDisk(mMainWin, CurrentWorkObject!, chunks, mFormatter, AppHook);
            if (dialog.ShowDialog() == true) {
                mMainWin.PostNotification("Saved", true);
            }
        }

        /// <summary>
        /// Handles Actions : Scan For Bad Blocks
        /// </summary>
        public void ScanForBadBlocks() {
            IDiskImage? diskImage = CurrentWorkObject as IDiskImage;
            if (diskImage == null) {
                Debug.Assert(false);
                return;
            }
            if (diskImage.ChunkAccess == null) {
                MessageBox.Show(mMainWin,
                    "Disk format was not recognized, so all blocks are \"bad\".", "Nope",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ScanBlocksProgress prog = new ScanBlocksProgress(diskImage, AppHook);
            WorkProgress workDialog = new WorkProgress(mMainWin, prog, true);
            if (workDialog.ShowDialog() == true) {
                List<ScanBlocksProgress.Failure>? results = prog.FailureResults!;
                if (results.Count != 0) {
                    StringBuilder sb = new StringBuilder();
                    sb.Append("Unreadable blocks/sectors: ");
                    sb.AppendLine(results.Count.ToString());
                    sb.AppendLine();
                    foreach (ScanBlocksProgress.Failure failure in results) {
                        if (failure.IsBlock) {
                            sb.Append("Block ");
                            sb.Append(failure.BlockOrTrack);
                        } else {
                            sb.Append("T");
                            sb.Append(failure.BlockOrTrack);
                            sb.Append(" S");
                            sb.Append(failure.Sector);
                        }
                        if (!failure.IsUnwritable) {
                            sb.Append(" (writable)");
                        }
                        sb.AppendLine();
                    }

                    ShowText reportDialog = new ShowText(mMainWin, sb.ToString());
                    reportDialog.Title = "Errors";
                    reportDialog.ShowDialog();
                } else {
                    mMainWin.PostNotification("Scan successful, no errors", true);
                }
            }
        }

        /// <summary>
        /// Handles Actions : Test Files
        /// </summary>
        public void TestFiles() {
            if (!GetFileSelection(omitDir: false, omitOpenArc: false, closeOpenArc: true,
                    oneMeansAll: false, out object? archiveOrFileSystem,
                    out IFileEntry unusedDir, out List<IFileEntry>? selected, out int unused)) {
                return;
            }
            if (selected.Count == 0) {
                MessageBox.Show(mMainWin, "No files selected.", "Empty", MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            // Note: this probably won't report a failure for a file that has been identified
            // as damaged by the file scanner, because the file scanner would have truncated
            // the file.  We can either redundantly identify damaged/dubious files, or just
            // limit the output to generating a set of facts that the user doesn't already have.
            // The CLI tool handles this by outputting the Notes as well as failure messages.
            // The GUI shows status icons for damage/dubious.
            //
            // The real purpose of this is for verifying the compression and checksum of data
            // stored in a file archive.  Files in filesystems should be fully handled by the
            // initial file scan.

            TestProgress prog = new TestProgress(archiveOrFileSystem, selected, AppHook) {
                EnableMacOSZip = AppSettings.Global.GetBool(AppSettings.MAC_ZIP_ENABLED, true),
            };
            WorkProgress workDialog = new WorkProgress(mMainWin, prog, false);
            if (workDialog.ShowDialog() == true) {
                List<TestProgress.Failure>? results = prog.FailureResults!;
                if (results.Count != 0) {
                    StringBuilder sb = new StringBuilder();
                    sb.Append("Failures: ");
                    sb.AppendLine(results.Count.ToString());
                    sb.AppendLine();
                    foreach (TestProgress.Failure failure in results) {
                        sb.Append(failure.Entry.FullPathName);
                        sb.Append(" (");
                        sb.Append(failure.Part);
                        sb.AppendLine(")");
                    }

                    ShowText reportDialog = new ShowText(mMainWin, sb.ToString());
                    reportDialog.Title = "Failures";
                    reportDialog.Show();
                } else {
                    mMainWin.PostNotification("Tests successful, no failures", true);
                }
            } else {
                mMainWin.PostNotification("Cancelled", false);
            }
        }

        /// <summary>
        /// Handles Actions : View Files.
        /// </summary>
        public void ViewFiles() {
            if (!GetFileSelection(omitDir:true, omitOpenArc:true, closeOpenArc:false,
                    oneMeansAll:true, out object? archiveOrFileSystem, out IFileEntry unusedDir,
                    out List<IFileEntry>? selected, out int firstSel)) {
                return;
            }
            if (selected.Count == 0 || firstSel < 0) {
                MessageBox.Show(mMainWin, "No viewable files selected (can't view directories " +
                    "or open disks/archives).", "Empty", MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            FileViewer dialog = new FileViewer(mMainWin, archiveOrFileSystem, selected,
                firstSel, AppHook);
            dialog.ShowDialog();
        }

        /// <summary>
        /// Handles Help : About.
        /// </summary>
        public void HelpAbout() {
            AboutBox dialog = new AboutBox(mMainWin);
            dialog.ShowDialog();
        }

        /// <summary>
        /// Handles Help : Help.
        /// </summary>
        public void HelpHelp() {
            //string pathName = Path.Combine(WinUtil.GetRuntimeDataDir(), "CiderPress2-notes.txt");
            //string url = "file://" + pathName;
            string url = "https://ciderpress2.com/gui-manual/";
            CommonUtil.ShellCommand.OpenUrl(url);
        }

        private static void ShowFileError(string msg) {
            MessageBox.Show(msg, FILE_ERR_CAPTION, MessageBoxButton.OK, MessageBoxImage.Error);
        }

        #region Find Files

        /// <summary>
        /// This keeps track of state while we're traversing the tree, trying to find matching
        /// entries.
        /// </summary>
        private class FindFileState {
            // Plan: walk through the entire tree, looking for entries that match and for the
            // currently-selected entry.  We need to save four things: the very first match, the
            // very last match, the match before the selected entry, and the match after the
            // selected entry.
            //
            // - If we're searching forward: select the match after the selected entry.  If we
            //   didn't find one, select the very first match in the tree.
            // - If we're searching backward: select the match before the selected entry.  If we
            //   didn't find one, select the very last match in the tree.
            // - Either way, if we come up empty, report failure.
            //
            // We search all of a given archive before descending into contained volumes.
            // Subdirectories are traversed inline; this is necessary to get the correct
            // behavior when the full file list is being displayed for a filesystem.

            public ArchiveTreeItem mCurrentArchive;
            public IFileEntry mCurrentEntry;

            public bool mFoundCurrent;

            public ArchiveTreeItem? mFirstArchive;
            public IFileEntry mFirstEntry = IFileEntry.NO_ENTRY;
            public ArchiveTreeItem? mPrevArchive;
            public IFileEntry mPrevEntry = IFileEntry.NO_ENTRY;
            public ArchiveTreeItem? mNextArchive;
            public IFileEntry mNextEntry = IFileEntry.NO_ENTRY;
            public ArchiveTreeItem? mLastArchive;
            public IFileEntry mLastEntry = IFileEntry.NO_ENTRY;

            public FindFileState(ArchiveTreeItem currentArchive, IFileEntry currentEntry) {
                mCurrentArchive = currentArchive;
                mCurrentEntry = currentEntry;
            }

            public override string ToString() {
                return "[FindRes: current=" + mCurrentEntry +
                    "\r\n  first=" + mFirstEntry +
                    "\r\n  prev=" + mPrevEntry +
                    "\r\n  next=" + mNextEntry +
                    "\r\n  last=" + mLastEntry +
                    "\r\n]";
            }
        }

        /// <summary>
        /// Does the actual work of finding matching files.
        /// </summary>
        /// <param name="req">Request, from FindFile dialog.</param>
        private void DoFindFiles(FindFile.FindFileReq req) {
            Debug.WriteLine("Find Files: " + req);

            // Determine which archive is selected.
            ArchiveTreeItem? arcTreeSel = mMainWin.SelectedArchiveTreeItem;
            if (arcTreeSel == null) {
                Debug.WriteLine("No archive entry selected");
                return;
            }

            // If it's not an IArchive or IFileSystem then we won't have a valid file list and
            // hence no starting point.  We currently fail.  It might be better to just scan for
            // the first one in the tree; currently we just disable Find when this is the case.
            object? arcObj = arcTreeSel.WorkTreeNode.DAObject;
            if (arcObj is not IArchive && arcObj is not IFileSystem) {
                Debug.WriteLine("Can't start with this archive object: " + arcObj);
                return;
            }

            // Determine which file entry is selected.  The Find command should be disabled if
            // no files are selected.
            IFileEntry selEntry;
            DataGrid dg = mMainWin.fileListDataGrid;
            IList listSel = dg.SelectedItems;
            if (listSel.Count == 0) {
                // Use the first item in the list.  If there are no items, we fail.  It would
                // be better to scan for the next viable archive.
                IList allItems = dg.Items;
                if (allItems.Count == 0) {
                    // TODO: this ought to be fixed
                    Debug.WriteLine("Empty IArchive / IFileSystem selected, can't search");
                    return;
                }
                selEntry = ((FileListItem)allItems[0]!).FileEntry;
            } else {
                // Use the first item in the selection.  This is the item that was selected
                // first, which is not necessarily the item that is at the top of the selection.
                selEntry = ((FileListItem)listSel[0]!).FileEntry;
            }

            Debug.WriteLine("FIND starting from " + arcTreeSel + " / " + selEntry);
            FindFileState results = new FindFileState(arcTreeSel, selEntry);
            DateTime startWhen = DateTime.Now;
            FindInTree(mMainWin.ArchiveTreeRoot, req, results);
            DateTime endWhen = DateTime.Now;

            Debug.WriteLine("FIND results (" + (endWhen - startWhen).TotalMilliseconds + " ms): " +
                results);
            if (results.mFirstArchive == null) {
                mMainWin.PostNotification("No matches found", false);
                return;
            }

            // Select the matching item.
            ArchiveTreeItem newTreeItem;
            IFileEntry newEntry;
            if (req.Forward) {
                if (results.mNextArchive != null) {
                    newTreeItem = results.mNextArchive;
                    newEntry = results.mNextEntry;
                } else {
                    Debug.Assert(results.mFirstArchive != null);
                    newTreeItem = results.mFirstArchive;
                    newEntry = results.mFirstEntry;
                }
            } else {
                if (results.mPrevArchive != null) {
                    newTreeItem = results.mPrevArchive;
                    newEntry = results.mPrevEntry;
                } else {
                    Debug.Assert(results.mLastArchive != null);
                    newTreeItem = results.mLastArchive;
                    newEntry = results.mLastEntry;
                }
            }

            // Move the selection in the three windows to the appropriate place.
            ArchiveTreeItem.SelectItem(mMainWin, newTreeItem);
            if (newEntry.ContainingDir != IFileEntry.NO_ENTRY) {
                DirectoryTreeItem.SelectItemByEntry(mMainWin, newEntry.ContainingDir);
            }
            FileListItem.SelectAndView(mMainWin, newEntry);
        }

        /// <summary>
        /// Recursively searches the archive tree.
        /// </summary>
        private static void FindInTree(ObservableCollection<ArchiveTreeItem> tvRoot,
                FindFile.FindFileReq req, FindFileState results) {
            foreach (ArchiveTreeItem treeItem in tvRoot) {
                object daObject = treeItem.WorkTreeNode.DAObject;
                // If the "current archive only" flag is set, only look for matches if this
                // is the current archive.  (This is less efficient than it could be, but it
                // makes the code simpler, and the data set isn't very large.)
                if (!req.CurrentArchiveOnly || treeItem == results.mCurrentArchive) {
                    if (daObject is IArchive) {
                        FindInArchive(treeItem, req, results);
                    } else if (daObject is IFileSystem) {
                        IFileSystem fs = (IFileSystem)daObject;
                        FindInFileSystem(treeItem, fs.GetVolDirEntry(), req, results);
                    }
                }
                FindInTree(treeItem.Items, req, results);
            }
        }

        private static void FindInArchive(ArchiveTreeItem treeItem, FindFile.FindFileReq req,
                FindFileState results) {
            // TODO? respect file list sort order
            bool macZipEnabled = AppSettings.Global.GetBool(AppSettings.MAC_ZIP_ENABLED, true);
            IArchive arc = (IArchive)treeItem.WorkTreeNode.DAObject;
            foreach (IFileEntry entry in arc) {
                if (macZipEnabled && entry.IsMacZipHeader()) {
                    // Don't include MacZip header entries in search.
                    continue;
                }
                if (entry == results.mCurrentEntry) {
                    results.mFoundCurrent = true;
                }
                if (EntryMatches(entry, req)) {
                    UpdateFindState(treeItem, entry, results);
                }
            }
        }

        private static void FindInFileSystem(ArchiveTreeItem treeItem, IFileEntry dir,
                FindFile.FindFileReq req, FindFileState results) {
            // TODO? respect file list sort order
            IFileSystem fs = (IFileSystem)treeItem.WorkTreeNode.DAObject;
            foreach (IFileEntry entry in dir) {
                if (entry == results.mCurrentEntry) {
                    results.mFoundCurrent = true;
                }
                if (EntryMatches(entry, req)) {
                    UpdateFindState(treeItem, entry, results);
                }
                if (entry.IsDirectory) {
                    FindInFileSystem(treeItem, entry, req, results);
                }
            }
        }

        /// <summary>
        /// Determines whether the specified entry matches the request parameters.
        /// </summary>
        private static bool EntryMatches(IFileEntry entry, FindFile.FindFileReq req) {
            return entry.FileName.Contains(req.FileName,
                    StringComparison.InvariantCultureIgnoreCase);
        }

        /// <summary>
        /// Updates the state object after a match is found.
        /// </summary>
        private static void UpdateFindState(ArchiveTreeItem treeItem, IFileEntry matchEntry,
                FindFileState results) {
            results.mLastArchive = treeItem;
            results.mLastEntry = matchEntry;
            if (results.mFirstArchive == null) {
                // First match found.
                results.mFirstArchive = treeItem;
                results.mFirstEntry = matchEntry;
            }
            // Only update the before/after stuff if this isn't the current entry.
            if (matchEntry != results.mCurrentEntry) {
                if (!results.mFoundCurrent) {
                    results.mPrevArchive = treeItem;
                    results.mPrevEntry = matchEntry;
                } else if (results.mNextArchive == null) {
                    results.mNextArchive = treeItem;
                    results.mNextEntry = matchEntry;
                }
            }
        }

        #endregion Find Files

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
            LibTest.BulkCompress dialog = new LibTest.BulkCompress(mMainWin, AppHook);
            dialog.ShowDialog();
        }

        private Tools.LogViewer? mDebugLogViewer;
        public bool IsDebugLogOpen => mDebugLogViewer != null;

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

        private DropTarget? mDebugDropTarget;
        public bool IsDropTargetOpen => mDebugDropTarget != null;

        public void Debug_ShowDropTarget() {
            if (mDebugDropTarget == null) {
                DropTarget dlg = new DropTarget();
                dlg.Closing += (sender, e) => {
                    Debug.WriteLine("Drop target test closed");
                    mDebugDropTarget = null;
                };
                dlg.Show();
                mDebugDropTarget = dlg;
            } else {
                // Ask the dialog to close.  Do the cleanup in the event.
                mDebugDropTarget.Close();
            }
        }

        #endregion Debug
    }
}
