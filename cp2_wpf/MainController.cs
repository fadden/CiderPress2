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
            Debug.WriteLine("--- running unit tests ---");
            Debug.Assert(RangeSet.Test());
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

        private void DoOpenWorkFile(string pathName, bool asReadOnly) {
            Debug.Assert(mWorkTree == null);
            if (!File.Exists(pathName)) {
                // Should only happen for projects in "recents" list.
                string msg = "File not found: '" + pathName + "'";
                MessageBox.Show(msg, FILE_ERR_CAPTION, MessageBoxButton.OK, MessageBoxImage.Error);
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

            // Flush and close all disk images and file archives.
            ClearArchiveTree();
            // Close the host file.
            mWorkTree.Dispose();
            mWorkTree = null;

            UpdateTitle();
            ClearEntryCounts();
            mMainWin.ShowLaunchPanel = true;

            // This seems like a good time to save our current settings, notably the Recents list.
            SaveAppSettings();

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
        ///   non-null node.</param>
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
            IList treeSel = dg.SelectedItems;
            if (treeSel.Count == 0) {
                return false;
            }

            // If we have a single selection, we want to track that selection so we show it
            // first in the file viewer.  This won't work if we select a single directory and then
            // view files from the Actions menu.
            object? selItem = null;
            if (oneMeansAll && dg.SelectedItems.Count == 1) {
                treeSel = dg.Items;
                selItem = dg.SelectedItem;
            }

            // Generate a selection set.  We want to collect the full set here so we can
            // present the user with dialogs like, "are you sure you want to delete 57 files?",
            // which will be wrong if we don't descend into subdirs.  The various workers can
            // do the recursion themselves if enabled, but there are advantages to doing it here,
            // and very little performance cost.
            selected = new List<IFileEntry>(treeSel.Count);
            if (archiveOrFileSystem is IArchive) {
                // For file archives, it's valid to delete "directory entries" without deleting
                // their contents, and the order in which we remove things doesn't matter.  So
                // we just copy everything in, skipping directories if this is e.g. a "view" op.
                foreach (FileListItem listItem in treeSel) {
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
                // before their contents, which requires an explicit sort.

                // Start by creating a dictionary of selected entries, so we can exclude them
                // during the subdir descent with O(1) lookups.
                Dictionary<IFileEntry, IFileEntry> knownItems =
                    new Dictionary<IFileEntry, IFileEntry>();
                foreach (FileListItem listItem in treeSel) {
                    knownItems.Add(listItem.FileEntry, listItem.FileEntry);
                }

                // Add items to the "selected" list.  Descend into subdirectories.
                foreach (FileListItem listItem in treeSel) {
                    IFileEntry entry = listItem.FileEntry;
                    if (entry.IsDirectory) {
                        if (!omitDir) {
                            selected.Add(listItem.FileEntry);
                            AddDirEntries(listItem.FileEntry, knownItems, selected);
                        }
                    } else {
                        selected.Add(listItem.FileEntry);
                    }
                }

                // Sort the list of selected items by filename.  The exact sort order doesn't
                // matter; we just need directories to appear before their contents, which they
                // should do with any ascending sort (by virtue of the names being shorter).
                //
                // We want to suppress this when viewing files.  This only really matters for
                // file deletion, so we can use the "omit dir" flag.
                if (!omitDir) {
                    selected.Sort(delegate (IFileEntry entry1, IFileEntry entry2) {
                        return string.Compare(entry1.FullPathName, entry2.FullPathName);
                    });
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
        /// Handles Actions : Add Files
        /// </summary>
        public void AddFiles() {
            HandleAddImport(null);
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
            AddPaths(fileDlg.SelectedPaths, spec);
        }

        /// <summary>
        /// Handles file drop.
        /// </summary>
        /// <param name="dropTarget">File entry onto which the files were dropped.</param>
        /// <param name="pathNames">List of pathnames to add.</param>
        public void AddFileDrop(IFileEntry dropTarget, string[] pathNames) {
            Debug.Assert(pathNames.Length > 0);
            Debug.WriteLine("External file drop (target=" + dropTarget + "):");
            ConvConfig.FileConvSpec? spec = null;       // TODO: import if configured
            AddPaths(pathNames, spec);
        }

        private void AddPaths(string[] pathNames, ConvConfig.FileConvSpec? importSpec) {
            Debug.WriteLine("Add paths (importSpec=" + importSpec + "):");
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
                    oneMeansAll:false, out archiveOrFileSystem, out IFileEntry selectionDir,
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
                    // Set the selection.  This causes a refresh.
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
        /// Handles Actions : Edit Blocks / Sectors
        /// </summary>
        public void EditBlocksSectors(bool asSectors) {
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
            EditSector dialog = new EditSector(mMainWin, chunks, asSectors, func, mFormatter);
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
            string convTag =
                mMainWin.IsExportBestChecked ? ConvConfig.BEST : mMainWin.ExportConvTag;
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
            Debug.WriteLine("Export spec: " + spec);
            HandleExtractExport(spec);
        }

        /// <summary>
        /// Handles Actions : Extract Files
        /// </summary>
        public void ExtractFiles() {
            HandleExtractExport(null);
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
        /// Handles Actions : Export Files
        /// </summary>
        public void ImportFiles() {
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
            Debug.WriteLine("Export spec: " + spec);
            HandleAddImport(spec);
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
            string pathName = Path.Combine(WinUtil.GetRuntimeDataDir(), "CiderPress2-notes.txt");
            string url = "file://" + pathName;
            CommonUtil.ShellCommand.OpenUrl(url);
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
            LibTest.BulkCompress dialog = new LibTest.BulkCompress(mMainWin, AppHook);
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
