/*
 * Copyright 2026 faddenSoft
 * Copyright 2026 Lydian Scale Software
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
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using AppCommon;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;

using CommonUtil;

using DiskArc;
using DiskArc.Arc;
using DiskArc.Disk;
using DiskArc.FS;
using DiskArc.Multi;
using static DiskArc.Defs;

using FileConv;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using cp2_avalonia.Common;
using cp2_avalonia.Models;

using cp2_avalonia.Actions;
using cp2_avalonia.Services;
using cp2_avalonia.Views;

namespace cp2_avalonia.ViewModels;

/// <summary>
/// Primary ViewModel for the main application window.
/// </summary>
public class MainViewModel : ObservableObject
{
    // Host-bound instances created from DI-managed factories.
    private readonly IDialogService mDialogService;
    private readonly IFilePickerService mFilePickerService;

    private readonly IDialogServiceFactory _dialogServiceFactory;
    private readonly IFilePickerServiceFactory _filePickerServiceFactory;

    // Container-managed singletons — use _camelCase per convention.
    private readonly ISettingsService _settingsService;
    private readonly IClipboardService _clipboardService;
    private readonly IViewerService _viewerService;
    private readonly IWorkspaceService _workspaceService;

    // View-level operations injected at construction.
    private readonly IViewActions _viewActions;

    // Retained for dialog host access.
    private readonly IDialogHost _dialogHost;

    /// <summary>
    /// Temporary reference to the controller. Removed in Phase 3B when
    /// Modeless debug viewer windows, owned by the VM after Group F migration.
    /// </summary>
    private LogViewerViewModel? mDebugLogViewer;
    private DropTargetViewModel? mDebugDropTarget;
    private FileViewer? mActiveViewerWindow;  // window targeted by regular Enter/double-click
    private readonly List<FileViewer> mAllViewerWindows = new();  // all open viewer windows
    private bool mViewerNavigating;  // true while FileNavigated is updating the file list selection

    // ---- Child ViewModels ----
    public ArchiveTreeViewModel ArchiveTree { get; private set; } = null!;
    public DirectoryTreeViewModel DirectoryTree { get; private set; } = null!;
    public FileListViewModel FileList { get; private set; } = null!;
    public CenterInfoViewModel CenterInfo { get; private set; } = null!;
    public OptionsPanelViewModel Options { get; private set; } = null!;
    public StatusBarViewModel StatusBar { get; private set; } = null!;
    public FindFileViewModel FindFileVM { get; } = new FindFileViewModel();

    // Constructor 
    public MainViewModel(
        IDialogHost dialogHost,
        IViewActions viewActions,
        IDialogServiceFactory dialogServiceFactory,
        IFilePickerServiceFactory filePickerServiceFactory,
        ISettingsService settingsService,
        IClipboardService clipboardService,
        IViewerService viewerService,
        IWorkspaceService workspaceService
    )
    {
        _dialogHost = dialogHost;
        _viewActions = viewActions;
        _dialogServiceFactory = dialogServiceFactory;
        _filePickerServiceFactory = filePickerServiceFactory;
        _viewerService = viewerService;
        _workspaceService = workspaceService;

        // Create host-bound services from factories.
        mDialogService = _dialogServiceFactory.Create(dialogHost);
        mFilePickerService = _filePickerServiceFactory.Create(dialogHost);

        // Container-managed singletons.
        _settingsService = settingsService;
        _clipboardService = clipboardService;

        // Create child ViewModels.
        ArchiveTree = new ArchiveTreeViewModel();
        DirectoryTree = new DirectoryTreeViewModel();
        CenterInfo = new CenterInfoViewModel(mDialogService);
        Options = new OptionsPanelViewModel(_settingsService, _clipboardService, mDialogService);
        StatusBar = new StatusBarViewModel();
        // FileList needs AppHook which lives on _workspaceService; created after it's assigned.
        FileList = new FileListViewModel(_viewActions, _settingsService,
            _workspaceService.AppHook);

        // Register ViewModel→View mappings on our DialogService instance.
        RegisterDialogMappings();



        // 1. NewDiskImageCommand — async, always enabled
        NewDiskImageCommand = new AsyncRelayCommand(NewDiskImage);

        // 2. NewFileArchiveCommand — async, always enabled
        NewFileArchiveCommand = new AsyncRelayCommand(NewFileArchive);

        // 3. OpenCommand — async, always enabled
        OpenCommand = new AsyncRelayCommand(
            () => OpenWorkFileAsync());

        // 4. OpenPhysicalDriveCommand — async, always enabled
        OpenPhysicalDriveCommand = new AsyncRelayCommand(async () => {
            await mDialogService.ShowMessageAsync(
                "Open Physical Drive is not yet implemented.", "Not Implemented");
        });

        // 5. CloseCommand — sync, enabled when file is open
        CloseCommand = new RelayCommand(
            () => { CloseWorkFile(); }, CanClose);

        // 6–11. RecentFile1–6Commands — async, always enabled
        RecentFile1Command = new AsyncRelayCommand(
            () => OpenRecentFile(0));
        RecentFile2Command = new AsyncRelayCommand(
            () => OpenRecentFile(1));
        RecentFile3Command = new AsyncRelayCommand(
            () => OpenRecentFile(2));
        RecentFile4Command = new AsyncRelayCommand(
            () => OpenRecentFile(3));
        RecentFile5Command = new AsyncRelayCommand(
            () => OpenRecentFile(4));
        RecentFile6Command = new AsyncRelayCommand(
            () => OpenRecentFile(5));

        // 12. ExitCommand — sync, always enabled
        ExitCommand = new RelayCommand(() => _dialogHost.GetOwnerWindow().Close());

        // 13. CopyCommand — async, needs file open + entries selected + file list visible
        CopyCommand = new AsyncRelayCommand(CopyToClipboard, CanCopy);

        // 13b. CutCommand — same prerequisites as CopyCommand; only for writable targets - CURRENTLY UNUSED
        CutCommand = new AsyncRelayCommand(CutToClipboard, CanCut);

        // 14. PasteCommand — async, needs file open + writable + multi-file item
        PasteCommand = new AsyncRelayCommand(
            () => PasteOrDrop(null, IFileEntry.NO_ENTRY), CanPaste);

        // 15. FindCommand — sync, needs file open; shows the inline find panel
        FindCommand = new RelayCommand(FindFiles, CanFind);

        // Wire find panel requests once — the VM persists for the lifetime of MainViewModel.
        FindFileVM.FindRequested += DoFindFiles;

        // 16. SelectAllCommand — sync, enabled when file is open
        SelectAllCommand = new RelayCommand(
            () => _viewActions.SelectAllFileListItems(), CanSelectAll);

        // 17. EditAppSettingsCommand — async, always enabled; re-activates if already open
        EditAppSettingsCommand = new AsyncRelayCommand(async () => {
            if (mDialogService.TryActivate<EditAppSettingsViewModel>()) return;
            var vm = new EditAppSettingsViewModel(_settingsService, mDialogService);
            vm.SettingsApplied += () => { ApplySettings(); _settingsService.Save(); };
            await mDialogService.ShowDialogAsync(vm);
        });


        // 18. ViewFilesCommand — needs file open + entries selected + file list visible.
        // Routes through HandleFileListDoubleClick (like WPF's ViewFilesCmd) so activating a
        // directory navigates into it instead of opening a (useless, slow) viewer on it; only
        // real files fall through to the viewer.
        ViewFilesCommand = new RelayCommand(
            () => HandleFileListDoubleClick(forceNew: false), CanViewFiles);
        ViewFilesInNewViewerCommand = new RelayCommand(
            () => HandleFileListDoubleClick(forceNew: true), CanViewFiles);

        // 19. AddFilesCommand — async, needs open + writable + multi-file + file list visible
        AddFilesCommand = new AsyncRelayCommand(
            () => AddFiles(), CanAddFiles);

        // 20. ImportFilesCommand — async, same canExecute as AddFilesCommand
        ImportFilesCommand = new AsyncRelayCommand(
            () => ImportFiles(), CanAddFiles);

        // 21. ExtractFilesCommand — async, same canExecute as CopyCommand
        ExtractFilesCommand = new AsyncRelayCommand(
            () => ExtractFiles(), CanExtractFiles);

        // 22. ExportFilesCommand — async, same canExecute as CopyCommand
        ExportFilesCommand = new AsyncRelayCommand(
            () => ExportFiles(), CanExportFiles);

        // 23. DeleteFilesCommand — async, needs open + writable + multi + selected + file list
        DeleteFilesCommand = new AsyncRelayCommand(
            () => DeleteFiles(), CanDeleteFiles);

        // 24. TestFilesCommand — async, same canExecute as CopyCommand
        TestFilesCommand = new AsyncRelayCommand(
            () => TestFiles(), CanTestFiles);

        // 25. EditAttributesCommand — async, needs file open + single entry selected
        EditAttributesCommand = new AsyncRelayCommand(
            () => EditAttributes(), CanEditAttributes);

        // 26. CreateDirectoryCommand — async, needs writable + hierarchical FS
        CreateDirectoryCommand = new AsyncRelayCommand(
            () => CreateDirectory(), CanCreateDirectory);

        // 27. EditDirAttributesCommand — async, needs file open + file system selected
        EditDirAttributesCommand = new AsyncRelayCommand(
            () => EditDirAttributes(), CanEditDirAttributes);

        // 28. EditSectorsCommand — async, needs file open + can edit sectors
        EditSectorsCommand = new AsyncRelayCommand(
            () => EditBlocksSectors(EditSectorViewModel.SectorEditMode.Sectors), CanEditSectorsCmd);

        // 29. EditBlocksCommand — async, needs file open + can edit blocks
        EditBlocksCommand = new AsyncRelayCommand(
            () => EditBlocksSectors(EditSectorViewModel.SectorEditMode.Blocks), CanEditBlocksCmd);

        // 30. SaveAsDiskImageCommand — async, needs open + disk/partition + has chunks
        SaveAsDiskImageCommand = new AsyncRelayCommand(
            () => SaveAsDiskImage(), CanSaveAsDiskImage);

        // 31. ReplacePartitionCommand — async, needs open + writable + partition
        ReplacePartitionCommand = new AsyncRelayCommand(
            () => ReplacePartition(), CanReplacePartition);

        // 32. ScanForBadBlocksCommand — async, needs open + nibble image selected
        ScanForBadBlocksCommand = new AsyncRelayCommand(
            () => ScanForBadBlocks(), CanScanForBadBlocks);

        // 33. ScanForSubVolCommand — sync, needs open + file system
        ScanForSubVolCommand = new RelayCommand(
            () => ScanForSubVol(), CanScanForSubVol);

        // 34. DefragmentCommand — async, needs open + defragmentation + writable
        DefragmentCommand = new AsyncRelayCommand(
            () => Defragment(), CanDefragment);

        // 35. CloseSubTreeCommand — sync, needs open + closable tree
        CloseSubTreeCommand = new RelayCommand(
            () => CloseSubTree(), CanCloseSubTree);

        // 36. ShowFullListCommand — sync, enabled when IsFullListEnabled
        ShowFullListCommand = new RelayCommand(() =>
        {
            PreferSingleDirList = false;
            if (ShowSingleDirFileList)
            {
                ShowSingleDirFileList = false;
                PopulateFileList(IFileEntry.NO_ENTRY, false);
            }
            SetShowCenterInfo(CenterPanelChange.Files);
        }, CanShowFullList);

        // 37. ShowDirListCommand — sync, enabled when IsDirListEnabled
        ShowDirListCommand = new RelayCommand(() =>
        {
            PreferSingleDirList = true;
            if (!ShowSingleDirFileList)
            {
                ShowSingleDirFileList = true;
                PopulateFileList(IFileEntry.NO_ENTRY, false);
            }
            SetShowCenterInfo(CenterPanelChange.Files);
        }, CanShowDirList);

        // 38. ShowInfoCommand — sync, enabled when file is open
        ShowInfoCommand = new RelayCommand(
            () => SetShowCenterInfo(CenterPanelChange.Info), CanShowInfo);

        // 39. ToggleInfoCommand — sync, enabled when file is open
        ToggleInfoCommand = new RelayCommand(
            () => SetShowCenterInfo(CenterPanelChange.Toggle), CanToggleInfo);

        // 40. NavToParentDirCommand — sync, needs open + hierarchical FS + not at root
        NavToParentDirCommand = new RelayCommand(
            () => NavToParent(true), CanNavToParentDir);

        // 41. NavToParentCommand — sync, needs open + (hier && !dirRoot) || !arcRoot
        NavToParentCommand = new RelayCommand(
            () => NavToParent(false), CanNavToParent);

        // 42. HelpCommand — sync, always enabled; no controller dependency
        HelpCommand = new RelayCommand(() =>
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://ciderpress2.com/gui-manual/",
                    UseShellExecute = true
                });
            }
            catch (Exception ex) { AppLog.W("Help: unable to launch browser", ex); }
        });

        // 43. AboutCommand — async, always enabled
        AboutCommand = new AsyncRelayCommand(async () => {
            var vm = new AboutBoxViewModel();
            await mDialogService.ShowDialogAsync(vm);
        });

        // ShowAsciiChartCommand — sync, always enabled; opens the ASCII Chart window (view-level).
        ShowAsciiChartCommand = new RelayCommand(() => _viewActions.ShowAsciiChart());

        // 44. Debug_DiskArcLibTestCommand — async, always enabled
        Debug_DiskArcLibTestCommand = new AsyncRelayCommand(async () => {
            var vm = new TestManagerViewModel("DiskArcTests.dll", "DiskArcTests.ITest");
            await mDialogService.ShowDialogAsync(vm);
        });

        // 45. Debug_FileConvLibTestCommand — async, always enabled
        Debug_FileConvLibTestCommand = new AsyncRelayCommand(async () => {
            var vm = new TestManagerViewModel("FileConvTests.dll", "FileConvTests.ITest");
            await mDialogService.ShowDialogAsync(vm);
        });

        // 46. Debug_BulkCompressTestCommand — async, always enabled
        Debug_BulkCompressTestCommand = new AsyncRelayCommand(async () => {
            var vm = new BulkCompressViewModel(_workspaceService.AppHook, mFilePickerService);
            await mDialogService.ShowDialogAsync(vm);
        });

        // 47. Debug_ShowSystemInfoCommand — async, always enabled
        Debug_ShowSystemInfoCommand = new AsyncRelayCommand(async () => {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("CiderPress II GUI v" + GlobalAppVersion.AppVersion
#if DEBUG
                + " DEBUG"
#endif
                );
            sb.AppendLine("+ DiskArc Library v" + LibVersion +
                (string.IsNullOrEmpty(BUILD_TYPE) ? "" : " (" + BUILD_TYPE + ")"));
            sb.AppendLine("+ Runtime: " +
                System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription +
                " / " +
                System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier);
            sb.AppendLine("  E_V=" + Environment.Version);
            sb.AppendLine("  E_OV=" + Environment.OSVersion);
            sb.AppendLine("  E_OV_P=" + Environment.OSVersion.Platform);
            sb.AppendLine("  E_64=" + Environment.Is64BitOperatingSystem + " / " +
                Environment.Is64BitProcess);
            sb.AppendLine("  E_MACH=\"" + Environment.MachineName + "\" cpus=" +
                Environment.ProcessorCount);
            sb.AppendLine("  E_CD=\"" + Environment.CurrentDirectory + "\"");
            sb.AppendLine("  RI_FD=" +
                System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
            sb.AppendLine("  RI_OSA=" +
                System.Runtime.InteropServices.RuntimeInformation.OSArchitecture);
            sb.AppendLine("  RI_OSD=" +
                System.Runtime.InteropServices.RuntimeInformation.OSDescription);
            sb.AppendLine("  RI_PA=" +
                System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture);
            sb.AppendLine("  RI_RI=" +
                System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier);
            sb.AppendLine(" " +
                " IsFreeBSD=" + System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                    System.Runtime.InteropServices.OSPlatform.FreeBSD) +
                " IsOSX=" + System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                    System.Runtime.InteropServices.OSPlatform.OSX) +
                " IsLinux=" + System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                    System.Runtime.InteropServices.OSPlatform.Linux) +
                " IsWindows=" + System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                    System.Runtime.InteropServices.OSPlatform.Windows));
            var vm = new ShowTextViewModel(sb.ToString(), "System Info");
            await mDialogService.ShowDialogAsync(vm);
        });

        // 48. Debug_ShowDebugLogCommand — sync, always enabled
        Debug_ShowDebugLogCommand = new RelayCommand(() => {
            if (mDebugLogViewer == null) {
                var vm = new LogViewerViewModel(_workspaceService.DebugLog, _settingsService);
                vm.Closed += () => { mDebugLogViewer = null; IsDebugLogVisible = false; };
                mDebugLogViewer = vm;
                IsDebugLogVisible = true;
                mDialogService.ShowModeless(vm);
            } else {
                mDebugLogViewer.RequestClose();
                mDebugLogViewer = null;
                IsDebugLogVisible = false;
            }
        });

        // 49. Debug_ShowDropTargetCommand — sync, always enabled
        Debug_ShowDropTargetCommand = new RelayCommand(() => {
            if (mDebugDropTarget == null) {
                var vm = new DropTargetViewModel();
                vm.Closed += () => { mDebugDropTarget = null; IsDropTargetVisible = false; };
                mDebugDropTarget = vm;
                IsDropTargetVisible = true;
                mDialogService.ShowModeless(vm);
            } else {
                mDebugDropTarget.RequestClose();
                mDebugDropTarget = null;
                IsDropTargetVisible = false;
            }
        });

        // 50. Debug_ConvertANICommand — async, enabled when ANI is selected
        Debug_ConvertANICommand = new AsyncRelayCommand(async () => {
            if (FileList.Selection.Count == 0) { return; }
            FileListItem item = FileList.Selection[0];
            IFileEntry entry = item.FileEntry;
            FileAttribs attrs = new FileAttribs(entry);
            object? archiveOrFileSystem = mCurrentWorkObject;
            if (archiveOrFileSystem == null) { return; }
            ExportFoundry.GetApplicableConverters(archiveOrFileSystem, entry, attrs,
                false, true, out Stream? dataStream, out Stream? rsrcStream,
                _workspaceService.AppHook);
            rsrcStream?.Close();
            if (dataStream == null) { return; }
            AnimatedGifEncoder? enc;
            using (dataStream) {
                enc = DoConvertANI(dataStream);
            }
            if (enc == null) {
                AppLog.W("Convert ANI: unable to parse file");
                await ShowMessageAsync("Unable to parse ANI file.", "Failed");
                return;
            }
            var fileTypes = new FilePickerFileType[] {
                new FilePickerFileType("Animated GIF") { Patterns = ["*.gif"] },
                new FilePickerFileType("All Files") { Patterns = ["*"] }
            };
            string? pathName = await mFilePickerService.SaveFileAsync(
                "Save Animated GIF...",
                suggestedName: Path.GetFileName(item.FileName) + ".gif",
                fileTypes: fileTypes);
            if (pathName == null) { return; }
            using (FileStream outStream = new FileStream(pathName, FileMode.Create)) {
                enc.Save(outStream, out int maxWidth, out int maxHeight);
            }
            Debug.WriteLine("Done (frames=" + enc.Count + " " + pathName + ")");
        }, CanConvertANI);


        // 51. ResetSortCommand — forwarded from FileList; subscribe to trigger repopulate.
        ResetSortCommand = FileList.ResetSortCommand;

        FileList.SortReset += () => PopulateFileList(IFileEntry.NO_ENTRY, false);


    }

    /// <summary>
    /// One-time registration of all ViewModel→View pairs used by
    /// IDialogService.ShowDialogAsync. Add entries as dialog ViewModels
    /// are created in Phases 4A/4B.
    /// </summary>
    private void RegisterDialogMappings()
    {
        mDialogService.Register<EditSectorViewModel, EditSector>();
        // Additional registrations added below as dialogs are converted (Phase 4A/4B):
        mDialogService.Register<EditAttributesViewModel, EditAttributes>();
        mDialogService.Register<CreateDiskImageViewModel, CreateDiskImage>();
        mDialogService.Register<SaveAsDiskViewModel, SaveAsDisk>();
        mDialogService.Register<TestManagerViewModel, LibTest.TestManager>();
        mDialogService.Register<BulkCompressViewModel, LibTest.BulkCompress>();
        mDialogService.Register<FileViewerViewModel, FileViewer>();
        // Phase 4B registrations:
        mDialogService.Register<ShowTextViewModel, Tools.ShowText>();
        mDialogService.Register<AboutBoxViewModel, AboutBox>();
        mDialogService.Register<AddMetadataViewModel, AddMetadata>();
        mDialogService.Register<EditMetadataViewModel, EditMetadata>();
        mDialogService.Register<CreateFileArchiveViewModel, CreateFileArchive>();
        mDialogService.Register<CreateDirectoryViewModel, CreateDirectory>();
        mDialogService.Register<OverwriteQueryViewModel, OverwriteQueryDialog>();
        mDialogService.Register<ReplacePartitionViewModel, ReplacePartition>();
        mDialogService.Register<EditConvertOptsViewModel, EditConvertOpts>();
        mDialogService.Register<EditAppSettingsViewModel, EditAppSettings>();
        mDialogService.Register<LogViewerViewModel, Tools.LogViewer>();
        mDialogService.Register<DropTargetViewModel, Tools.DropTarget>();
        mDialogService.Register<WorkProgressViewModel, WorkProgress>();
        mDialogService.Register<MessageBoxViewModel, MessageBoxView>();
        mDialogService.Register<SuppressibleMessageViewModel, SuppressibleMessageView>();
    }




    // -----------------------------------------------------------------------------------------
    // Step 4: Lifecycle

    /// <summary>
    /// Performs initialization after the main window has loaded.
    /// Called from MainWindow code-behind after window controls are ready.
    /// </summary>
    public void Initialize()
    {
        _workspaceService.AppHook.LogI("--- running unit tests ---");
        Debug.Assert(RangeSet.Test());
        Debug.Assert(CommonUtil.Version.Test());
        Debug.Assert(CircularBitBuffer.DebugTest());
        Debug.Assert(Glob.DebugTest());
        Debug.Assert(PathName.DebugTest());
        Debug.Assert(TimeStamp.DebugTestDates());
        Debug.Assert(TrackInit.DebugCheckInterleave());
        Debug.Assert(Wozoof_Meta.DebugTest());
        _workspaceService.AppHook.LogI("--- unit tests complete ---");

        _settingsService.Load();  // no-op: already loaded in App.Initialize()
        ApplySettings();

        UpdateTitle();
        UpdateRecentLinks();

    }

    /// <summary>
    /// Saves settings and performs cleanup when the window is closing.
    /// Called from MainWindow code-behind after window placement is saved.
    /// </summary>
    public void Shutdown()
    {
        mDebugLogViewer?.RequestClose();
        ArchiveTree.Dispose();
        DirectoryTree.Dispose();
        _settingsService.Save();
    }

    /// <summary>
    /// Applies application settings to the ViewModel and services.
    /// Called from Initialize() and when the settings dialog fires its Apply event.
    /// </summary>
    public void ApplySettings()
    {
        Debug.WriteLine("Applying app settings...");
#if DEBUG
        ShowDebugMenu = _settingsService.GetBool(AppSettings.DEBUG_MENU_ENABLED, true);
#else
        ShowDebugMenu = _settingsService.GetBool(AppSettings.DEBUG_MENU_ENABLED, false);
#endif
        // Window placement and panel width are restored in MainWindow.Window_Loaded
        // (view concerns; code-behind reads from _settingsService there).
        _workspaceService.UnpackRecentFileList();
        if (_workspaceService.IsFileOpen)
        {
            RefreshDirAndFileList(false);
        }
        _workspaceService.AppHook.SetOptionEnum(DAAppHook.AUDIO_DEC_ALG,
            _settingsService.GetEnum(AppSettings.AUDIO_DECODE_ALG,
                CassetteDecoder.Algorithm.ZeroCross));
        if (Application.Current is App app)
        {
            app.ApplyTheme();
            app.ApplyFonts();
        }
        // Notify all options-panel bindings that settings may have changed.
        Options.RefreshFromSettings();
    }

    /// <summary>
    /// Updates the window title to reflect the open file.
    /// </summary>
    private void UpdateTitle()
    {
        var sb = new StringBuilder();
        if (_workspaceService.WorkTree != null)
        {
            string fileName = Path.GetFileName(_workspaceService.WorkPathName);
            if (string.IsNullOrEmpty(fileName))
            {
                fileName = _workspaceService.WorkPathName;
            }
            sb.Append(fileName);
            if (!_workspaceService.WorkTree.CanWrite)
            {
                sb.Append(" *READONLY*");
            }
            sb.Append(" - ");
        }
        sb.Append("CiderPress II");
        WindowTitle = sb.ToString();
    }

    /// <summary>
    /// Updates recent file VM properties and rebuilds the native menu.
    /// </summary>
    internal void UpdateRecentLinks()
    {
        var paths = _workspaceService.RecentFilePaths;
        string path1 = paths.Count >= 1 ? paths[0] : string.Empty;
        string path2 = paths.Count >= 2 ? paths[1] : string.Empty;
        RecentFilePath1 = path1;
        RecentFileName1 = string.IsNullOrEmpty(path1) ? string.Empty :
            Path.GetFileName(path1);
        RecentFilePath2 = path2;
        RecentFileName2 = string.IsNullOrEmpty(path2) ? string.Empty :
            Path.GetFileName(path2);
        _viewActions.PopulateRecentFilesMenu();
    }

    /// <summary>
    /// Closes the current work file. Returns true if successfully closed.
    /// </summary>
    public bool CloseWorkFile()
    {
        if (_workspaceService.WorkTree == null)
        {
            return true;
        }
        Debug.WriteLine("Closing " + _workspaceService.WorkPathName);

        _viewerService.CloseViewersForSource(_workspaceService.WorkPathName);
        ClearTreesAndLists();
        _workspaceService.Close();
        CachedDirectoryTreeSelection = null;
        CachedArchiveTreeSelection = null;

        UpdateTitle();
        StatusBar.ClearEntryCounts();
        LaunchPanelVisible = true;
        MainPanelVisible = false;

        _ = _clipboardService.ClearIfPendingAsync();
        _settingsService.Save();
        RefreshAllCommandStates();
        GC.Collect();
        return true;
    }

    /// <summary>
    /// Core logic for opening a work file. Runs OpenProgress on a background
    /// thread via WorkspaceService; shows a busy cursor during load.
    /// </summary>
    public async Task OpenWorkFileAsync(string? path = null)
    {
        path ??= await mFilePickerService.OpenFileAsync("Open");
        if (path == null) { return; }
        if (!CloseWorkFile()) { return; }

        if (!File.Exists(path))
        {
            AppLog.W("Open: file not found: '" + path + "'");
            _ = mDialogService.ShowMessageAsync("File not found: '" + path + "'", "Error");
            return;
        }

        try
        {
            AutoOpenDepth depth = _settingsService.GetEnum(
                AppSettings.AUTO_OPEN_DEPTH, AutoOpenDepth.SubVol);
            await mDialogService.ShowLoadingAsync(
                "Loading ‘" + Path.GetFileName(path) + "’…",
                async () => await _workspaceService.OpenAsync(path, readOnly: false, depth));
            if (!_workspaceService.IsFileOpen) { return; }
            PopulateArchiveTree();
        }
        catch (Exception ex)
        {
            AppLog.E("Unable to open file '" + path + "'", ex);
            _ = mDialogService.ShowMessageAsync("Unable to open file: " + ex.Message, "Error");
            return;
        }

        _workspaceService.AppHook.LogI("Load of '" + path + "' completed");
        UpdateTitle();
        UpdateRecentLinks();
        LaunchPanelVisible = false;
        MainPanelVisible = true;
    }

    /// <summary>
    /// Opens a file from drag-and-drop on the launch panel.
    /// </summary>
    public async Task DropOpenWorkFile(string pathName)
    {
        if (!CloseWorkFile()) { return; }
        await OpenWorkFileAsync(pathName);
    }

    /// <summary>
    /// Opens a recent file by index (0 = most recent).
    /// </summary>
    public async Task OpenRecentFile(int recentIndex)
    {
        if (recentIndex >= _workspaceService.RecentFilePaths.Count) { return; }
        if (!CloseWorkFile()) { return; }
        await OpenWorkFileAsync(_workspaceService.RecentFilePaths[recentIndex]);
    }


    // -----------------------------------------------------------------------------------------
    // Step 5: Group D — view/navigation double-click and metadata handlers

    /// <summary>
    /// Handles a double click on the file list.  If the entry is a directory, selects it in
    /// the directory tree.  If it is an archive/disk that can be opened as a sub-volume, opens
    /// it; otherwise, views the file.
    /// </summary>
    public void HandleFileListDoubleClick(bool forceNew = false)
    {
        ArchiveTreeItem? arcTreeSel = CachedArchiveTreeSelection;
        DirectoryTreeItem? dirTreeSel = CachedDirectoryTreeSelection;
        if (arcTreeSel == null || dirTreeSel == null)
        {
            Debug.Assert(false, "tree is missing selection");
            return;
        }

        IArchive? arc = arcTreeSel.WorkTreeNode.DAObject as IArchive;
        IFileSystem? fs = arcTreeSel.WorkTreeNode.DAObject as IFileSystem;
        if (!(arc == null ^ fs == null))
        {
            Debug.Assert(false, "Unexpected: arc=" + arc + " fs=" + fs);
            return;
        }

        if (FileList.Selection.Count == 0)
        {
            Debug.WriteLine("Double-click but no selection");
            return;
        }

        if (FileList.Selection.Count == 1)
        {
            FileListItem fli = FileList.Selection[0];
            IFileEntry entry = fli.FileEntry;
            if (entry.IsDirectory)
            {
                if (fs != null)
                {
                    if (ShowSingleDirFileList)
                    {
                        // Single-directory mode: switch the file list to show the new
                        // directory's contents, keeping focus in the file list.
                        FileList.RequestFocusAfterPopulate();
                        if (!_viewActions.SelectDirectoryTreeItemByEntry(DirectoryTree.TreeRoot, entry))
                        {
                            Debug.WriteLine("Unable to find dir tree entry for " + entry);
                        }
                    }
                    else
                    {
                        // Full-list mode: point the directory tree at the new directory and
                        // move focus to it (WPF behavior).  The file list stays as the full
                        // listing; OnDirectoryTreeSelectionChanged highlights the new dir in it,
                        // which makes the DataGrid grab focus asynchronously -- so the focus shift
                        // must be deferred past that settle to win.
                        if (!_viewActions.SelectDirectoryTreeItemByEntry(DirectoryTree.TreeRoot, entry))
                        {
                            Debug.WriteLine("Unable to find dir tree entry for " + entry);
                        }
                        _viewActions.FocusDirectoryTreeDeferred();
                    }
                }
                // else: directory file in a file archive — nothing to do.
                return;
            }

            // Is it a disk image or file archive already open in the archive tree?
            ArchiveTreeItem? ati = ArchiveTreeItem.FindItemByEntry(ArchiveTree.TreeRoot, entry);
            if (ati != null)
            {
                _viewActions.SelectBestArchiveTreeItem(ati);
                FileList.RequestFocusAfterPopulate();
                return;
            }

            _viewActions.SetCursorBusy(true);
            try
            {
                WorkTree.Node? newNode =
                    _workspaceService.WorkTree!.TryCreateSub(arcTreeSel.WorkTreeNode, entry);
                if (newNode != null)
                {
                    ArchiveTreeItem newItem = ArchiveTreeItem.ConstructTree(arcTreeSel, newNode);
                    _viewActions.SelectBestArchiveTreeItem(newItem);
                    FileList.RequestFocusAfterPopulate();
                    return;
                }
            }
            finally
            {
                _viewActions.SetCursorBusy(false);
            }
        }

        // View the selection.
        _ = forceNew ? ViewFilesInNew() : ViewFiles();
    }

    /// <summary>
    /// Handles a double click on a partition layout entry.  If the partition is already
    /// open in the archive tree, selects it; otherwise tries to open it.
    /// </summary>
    public void HandlePartitionLayoutDoubleClick(PartitionListItem item,
            ArchiveTreeItem? arcTreeSel)
    {
        arcTreeSel ??= CachedArchiveTreeSelection;
        if (arcTreeSel == null)
        {
            Debug.Assert(false, "archive tree is missing selection");
            return;
        }

        ArchiveTreeItem? ati = ArchiveTreeItem.FindItemByDAObject(ArchiveTree.TreeRoot, item.PartRef);
        if (ati != null)
        {
            _viewActions.SelectBestArchiveTreeItem(ati);
            return;
        }

        _viewActions.SetCursorBusy(true);
        try
        {
            WorkTree.Node? newNode =
                _workspaceService.WorkTree!.TryCreatePartition(arcTreeSel.WorkTreeNode,
                    item.Index);
            if (newNode != null)
            {
                ArchiveTreeItem newItem = ArchiveTreeItem.ConstructTree(arcTreeSel, newNode);
                _viewActions.SelectBestArchiveTreeItem(newItem);
            }
        }
        finally
        {
            _viewActions.SetCursorBusy(false);
        }
    }

    /// <summary>
    /// Handles a double click on a metadata list entry.  Shows the edit-metadata dialog.
    /// </summary>
    public async Task HandleMetadataDoubleClick(MetadataItem item, int row, int col)
    {
        await CenterInfo.HandleMetadataDoubleClick(item, row, col, mCurrentWorkObject);
    }

    /// <summary>
    /// Handles a click on the "Add Metadata Entry" button.
    /// </summary>
    public async Task HandleMetadataAddEntry()
    {
        await CenterInfo.HandleMetadataAddEntry(mCurrentWorkObject);
    }

    /// <summary>
    /// Handles Actions : Edit Sectors / Blocks.
    /// </summary>
    public async Task EditBlocksSectors(EditSectorViewModel.SectorEditMode editMode)
    {
        Debug.Assert(_workspaceService.WorkTree != null);
        ArchiveTreeItem? arcTreeSel = CachedArchiveTreeSelection;
        if (arcTreeSel == null)
        {
            Debug.Assert(false);
            return;
        }
        WorkTree.Node workNode = arcTreeSel.WorkTreeNode;
        object daObject = workNode.DAObject;
        IChunkAccess? chunks;
        if (daObject is IDiskImage image)
        {
            chunks = image.ChunkAccess;
        }
        else if (daObject is Partition partition)
        {
            chunks = partition.ChunkAccess;
        }
        else
        {
            Debug.Assert(false, "unexpected sector edit target: " + daObject);
            return;
        }
        if (chunks == null)
        {
            _ = mDialogService.ShowMessageAsync("Disk sector format not recognized", "Trouble");
            return;
        }

        bool writeEnabled = false;
        EditSectorViewModel.EnableWriteFunc? func = null;
        if (!chunks.IsReadOnly)
        {
            func = delegate ()
            {
                // Close any open file viewers before invalidating the filesystem.
                foreach (var viewer in _viewerService.ActiveViewers.ToList())
                {
                    if (viewer.SourceWorkPathName == _workspaceService.WorkPathName)
                        viewer.RequestClose();
                }
                Debug.Assert(_workspaceService.WorkTree!.CheckHealth());
                workNode.CloseChildren();
                if (daObject is IDiskImage di)
                {
                    di.CloseContents();
                }
                else if (daObject is Partition part)
                {
                    part.CloseContents();
                }
                Debug.Assert(_workspaceService.WorkTree!.CheckHealth());
                arcTreeSel.Items.Clear();
                writeEnabled = true;
                return true;
            };
        }

        // Warn the user (once) that writing will close any open file viewers.
        bool hasViewers = _viewerService.ActiveViewers.Any(
            v => v.SourceWorkPathName == _workspaceService.WorkPathName);
        if (hasViewers && !chunks.IsReadOnly &&
                !_settingsService.GetBool(AppSettings.VIEWER_SUPPRESS_SECTOR_EDIT_WARNING, false))
        {
            (Services.MBResult _, bool suppress) = await mDialogService.ShowSuppressibleMessageAsync(
                "One or more file viewers are open for this disk. " +
                "If you write to the disk, all open file viewers will be closed.",
                "File Viewer Warning",
                "Don't show this message again",
                Services.MBButton.OK,
                Services.MBIcon.Warning);
            if (suppress)
            {
                _settingsService.SetBool(AppSettings.VIEWER_SUPPRESS_SECTOR_EDIT_WARNING, true);
            }
        }

        var vm = new EditSectorViewModel(chunks, editMode, func,
            _workspaceService.Formatter, mDialogService, _clipboardService, _settingsService);
        await mDialogService.ShowDialogAsync(vm);
        Debug.WriteLine("After EditSector dialog, enabled=" + vm.WritesEnabled);
        writeEnabled = vm.WritesEnabled;

        if (daObject is IDiskImage diskImg)
        {
            diskImg.Flush();
        }

        if (writeEnabled)
        {
            _viewActions.SetCursorBusy(true);
            try
            {
                if (daObject is IDiskImage)
                {
                    _workspaceService.WorkTree!.ReprocessDiskImage(workNode);
                }
                else if (daObject is Partition)
                {
                    _workspaceService.WorkTree!.ReprocessPartition(workNode);
                }
                foreach (WorkTree.Node childNode in workNode)
                {
                    ArchiveTreeItem.ConstructTree(arcTreeSel, childNode);
                }
                ReloadOrCloseViewersAfterSectorEdit(workNode);
            }
            finally
            {
                _viewActions.SetCursorBusy(false);
            }
        }
    }


    // -----------------------------------------------------------------------------------------
    // Step 3D: Group D — view and find commands

    /// <summary>
    /// Views selected files in the FileViewer dialog, reusing the active window if open.
    /// </summary>
    private async Task ViewFiles() {
        int rawSelCount = FileList.Selection.Count;
        if (!GetViewerSelection(rawSelCount, out object? aofs, out List<IFileEntry>? selected,
                out int firstSel, out bool prePopulateForward)) {
            return;
        }

        try {
            FileViewerViewModel? vm = mActiveViewerWindow?.DataContext as FileViewerViewModel;
            if (vm == null || mActiveViewerWindow == null || !mActiveViewerWindow.IsVisible) {
                OpenNewViewerWindow(aofs!, selected!, firstSel, prePopulateForward);
            } else {
                vm.LoadEntries(aofs!, selected!, firstSel,
                    _workspaceService.WorkPathName, prePopulateForward);
                ActivateFileViewerWindow();
            }
        } catch (Exception ex) {
            AppLog.E("File viewer threw an exception", ex);
            await mDialogService.ShowMessageAsync("FileViewer threw an exception:\n" + ex.Message +
                "\n\n" + ex.StackTrace, "ViewFiles Error");
        }
    }

    /// <summary>
    /// Always opens the selected files in a new FileViewer window.
    /// </summary>
    private async Task ViewFilesInNew() {
        int rawSelCount = FileList.Selection.Count;
        if (!GetViewerSelection(rawSelCount, out object? aofs, out List<IFileEntry>? selected,
                out int firstSel, out bool prePopulateForward)) {
            return;
        }

        try {
            OpenNewViewerWindow(aofs!, selected!, firstSel, prePopulateForward);
        } catch (Exception ex) {
            AppLog.E("File viewer threw an exception", ex);
            await mDialogService.ShowMessageAsync("FileViewer threw an exception:\n" + ex.Message +
                "\n\n" + ex.StackTrace, "ViewFiles Error");
        }
    }

    /// <summary>
    /// Resolves the file selection for viewer commands.  Returns false (and shows an error)
    /// if the selection cannot be determined or is empty.
    /// </summary>
    private bool GetViewerSelection(int rawSelCount,
            out object? archiveOrFileSystem, out List<IFileEntry>? selected,
            out int firstSel, out bool prePopulateForward)
    {
        prePopulateForward = false;
        if (!GetFileSelection(omitDir: true, omitOpenArc: true, closeOpenArc: false,
                oneMeansAll: true, out archiveOrFileSystem, out IFileEntry _,
                out selected, out firstSel)) {
            _ = mDialogService.ShowMessageAsync("GetFileSelection returned false — no selection could " +
                "be determined.", "ViewFiles Debug");
            return false;
        }
        if (selected!.Count == 0 || firstSel < 0) {
            _ = mDialogService.ShowMessageAsync("No viewable files selected (can't view directories " +
                "or open disks/archives).\nselected.Count=" + selected.Count +
                " firstSel=" + firstSel, "Empty");
            return false;
        }
        // Pre-populate Forward only when the user explicitly selected multiple files.
        prePopulateForward = rawSelCount > 1;
        return true;
    }

    /// <summary>
    /// Creates a new FileViewerViewModel + FileViewer window and shows it.
    /// Sets mActiveViewerWindow to the new window so regular Enter retargets it.
    /// </summary>
    private void OpenNewViewerWindow(object archiveOrFileSystem, List<IFileEntry> selected,
            int firstSel, bool prePopulateForward)
    {
        var vm = new FileViewerViewModel(archiveOrFileSystem, selected, firstSel,
            _workspaceService.WorkPathName, _workspaceService.AppHook,
            _settingsService, mFilePickerService, _clipboardService, _viewerService,
            prePopulateForward);
        vm.FileNavigated += entry => {
            mViewerNavigating = true;
            try {
                _viewActions.SelectFileListItemByEntry(entry);
            } finally {
                mViewerNavigating = false;
            }
        };
        _viewerService.Register(vm);

        var window = new FileViewer { DataContext = vm };
        mAllViewerWindows.Add(window);
        window.Activated += (_, _) => {
            mActiveViewerWindow = window;
            // Sync the file list highlight to whatever this viewer is showing.
            IFileEntry? current = vm.CurrentEntry;
            if (current != null) {
                mViewerNavigating = true;
                try {
                    _viewActions.SelectFileListItemByEntry(current);
                } finally {
                    mViewerNavigating = false;
                }
            }
        };
        window.Closed += (_, _) => {
            _viewerService.Unregister(vm);
            mAllViewerWindows.Remove(window);
            if (ReferenceEquals(mActiveViewerWindow, window)) {
                // Prefer the most recently activated still-open window.
                mActiveViewerWindow = mAllViewerWindows.LastOrDefault(w => w.IsVisible);
            }
        };
        mActiveViewerWindow = window;
        window.Show(_dialogHost.GetOwnerWindow());
    }

    /// <summary>
    /// Handles Edit : Find Files.  Shows the inline find panel.
    /// </summary>
    private void FindFiles() {
        FindFileVM.Show();
    }

    /// <summary>
    /// Does the actual work of finding matching files.
    /// </summary>
    private void DoFindFiles(FindFileReq req) {
        Debug.WriteLine("Find Files: " + req);

        ArchiveTreeItem? arcTreeSel = CachedArchiveTreeSelection;
        if (arcTreeSel == null) {
            Debug.WriteLine("No archive entry selected");
            return;
        }

        object? arcObj = arcTreeSel.WorkTreeNode.DAObject;
        if (arcObj is not IArchive && arcObj is not IFileSystem) {
            Debug.WriteLine("Can't start with this archive object: " + arcObj);
            return;
        }

        IFileEntry selEntry;
        var listSel = FileList.Selection;
        if (listSel.Count == 0) {
            var allItems = FileList.Items;
            if (allItems.Count == 0) {
                Debug.WriteLine("Empty archive/FS selected, can't search");
                return;
            }
            selEntry = allItems[0].FileEntry;
        } else {
            selEntry = listSel[0].FileEntry;
        }

        Debug.WriteLine("FIND starting from " + arcTreeSel + " / " + selEntry);
        FindFileState results = new FindFileState(arcTreeSel, selEntry);
        FindInTree(ArchiveTree.TreeRoot, req, results,
            _settingsService.GetBool(AppSettings.MAC_ZIP_ENABLED, true));
        Debug.WriteLine("FIND results: " + results);

        if (results.mFirstArchive == null) {
            _viewActions.ShowToast("No matches found", false);
            return;
        }

        ArchiveTreeItem newTreeItem;
        IFileEntry newEntry;
        if (req.Forward) {
            if (results.mNextArchive != null) {
                newTreeItem = results.mNextArchive;
                newEntry = results.mNextEntry;
            } else {
                newTreeItem = results.mFirstArchive;
                newEntry = results.mFirstEntry;
            }
        } else {
            if (results.mPrevArchive != null) {
                newTreeItem = results.mPrevArchive;
                newEntry = results.mPrevEntry;
            } else {
                newTreeItem = results.mLastArchive!;
                newEntry = results.mLastEntry;
            }
        }

        ArchiveTreeItem.SelectItem(_viewActions, newTreeItem);
        if (newEntry.ContainingDir != IFileEntry.NO_ENTRY) {
            _viewActions.SelectDirectoryTreeItemByEntry(DirectoryTree.TreeRoot,
                newEntry.ContainingDir);
        }
        _viewActions.SelectFileListItemByEntry(newEntry);
    }

    private static void FindInTree(ObservableCollection<ArchiveTreeItem> tvRoot,
            FindFileReq req, FindFileState results, bool macZipEnabled) {
        foreach (ArchiveTreeItem treeItem in tvRoot) {
            object daObject = treeItem.WorkTreeNode.DAObject;
            if (!req.CurrentArchiveOnly || treeItem == results.mCurrentArchive) {
                if (daObject is IArchive) {
                    FindInArchive(treeItem, req, results, macZipEnabled);
                } else if (daObject is IFileSystem system) {
                    FindInFileSystem(treeItem, system.GetVolDirEntry(),
                        req, results);
                }
            }
            FindInTree(treeItem.Items, req, results, macZipEnabled);
        }
    }

    private static void FindInArchive(ArchiveTreeItem treeItem, FindFileReq req,
            FindFileState results, bool macZipEnabled) {
        IArchive arc = (IArchive)treeItem.WorkTreeNode.DAObject;
        foreach (IFileEntry entry in arc) {
            if (macZipEnabled && entry.IsMacZipHeader()) {
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
            FindFileReq req, FindFileState results) {
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

    private static bool EntryMatches(IFileEntry entry, FindFileReq req) {
        return entry.FileName.Contains(req.FileName,
                StringComparison.InvariantCultureIgnoreCase);
    }

    private static void UpdateFindState(ArchiveTreeItem treeItem, IFileEntry matchEntry,
            FindFileState results) {
        results.mLastArchive = treeItem;
        results.mLastEntry = matchEntry;
        if (results.mFirstArchive == null) {
            results.mFirstArchive = treeItem;
            results.mFirstEntry = matchEntry;
        }
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

    private class FindFileState(ArchiveTreeItem currentArchive, IFileEntry currentEntry)
    {
        public readonly ArchiveTreeItem mCurrentArchive = currentArchive;
        public readonly IFileEntry mCurrentEntry = currentEntry;

        public bool mFoundCurrent;

        public ArchiveTreeItem? mFirstArchive;
        public IFileEntry mFirstEntry = IFileEntry.NO_ENTRY;
        public ArchiveTreeItem? mPrevArchive;
        public IFileEntry mPrevEntry = IFileEntry.NO_ENTRY;
        public ArchiveTreeItem? mNextArchive;
        public IFileEntry mNextEntry = IFileEntry.NO_ENTRY;
        public ArchiveTreeItem? mLastArchive;
        public IFileEntry mLastEntry = IFileEntry.NO_ENTRY;

        public override string ToString() {
            return "[FindRes: current=" + mCurrentEntry +
                "\r\n  first=" + mFirstEntry +
                "\r\n  prev=" + mPrevEntry +
                "\r\n  next=" + mNextEntry +
                "\r\n  last=" + mLastEntry +
                "\r\n]";
        }
    }

    // -----------------------------------------------------------------------------------------
    // Step 3A: Group A — file operations (new, add, import, extract, export, delete, test, move)

    /// <summary>
    /// Handles File → New Disk Image.  Prompts for format/size, then creates and opens
    /// the new image file.
    /// </summary>
    private async Task NewDiskImage() {
        if (!CloseWorkFile()) {
            return;
        }
        var vm = new CreateDiskImageViewModel(_workspaceService.AppHook, mFilePickerService, _settingsService, mDialogService);
        await mDialogService.ShowDialogAsync(vm);
        if (!string.IsNullOrEmpty(vm.CreatedDiskPath)) {
            await OpenWorkFileAsync(vm.CreatedDiskPath);
        }
    }

    /// <summary>
    /// Handles File → New File Archive.  Prompts for archive kind and save path, creates
    /// the archive, then opens it.
    /// </summary>
    private async Task NewFileArchive() {
        var vm = new CreateFileArchiveViewModel(_settingsService);
        if (await mDialogService.ShowDialogAsync(vm) != true) {
            return;
        }
        if (!CloseWorkFile()) {
            return;
        }

        string ext;
        FilePickerFileType fileType;
        switch (vm.Kind) {
            case FileKind.Binary2:
                ext = ".bny";
                fileType = new FilePickerFileType("Binary II") {
                    Patterns = ["*.bny", "*.bqy"]
                };
                break;
            case FileKind.NuFX:
                ext = ".shk";
                fileType = new FilePickerFileType("NuFX Archive") {
                    Patterns = ["*.shk", "*.sdk", "*.bxy"]
                };
                break;
            case FileKind.Zip:
                ext = ".zip";
                fileType = new FilePickerFileType("ZIP Archive") {
                    Patterns = ["*.zip"]
                };
                break;
            default:
                Debug.Assert(false);
                return;
        }

        var fileTypes = new FilePickerFileType[] {
            fileType,
            new FilePickerFileType("All Files") { Patterns = ["*"] }
        };
        string? pathName = await mFilePickerService.SaveFileAsync(
            "Create New Archive...",
            suggestedName: "NewArchive" + ext,
            fileTypes: fileTypes);
        if (pathName == null) {
            return;
        }
        if (!pathName.ToLowerInvariant().EndsWith(ext)) {
            pathName += ext;
        }

        IArchive archive;
        switch (vm.Kind) {
            case FileKind.Binary2:
                archive = Binary2.CreateArchive(_workspaceService.AppHook);
                break;
            case FileKind.NuFX:
                archive = NuFX.CreateArchive(_workspaceService.AppHook);
                break;
            case FileKind.Zip:
                archive = Zip.CreateArchive(_workspaceService.AppHook);
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
            await OpenWorkFileAsync(pathName);
        } catch (IOException ex) {
            ShowFileError("Unable to create archive: " + ex.Message);
        }
    }

    /// <summary>
    /// Handles Actions → Add Files.
    /// </summary>
    private async Task AddFiles() {
        await HandleAddImport(null);
    }

    /// <summary>
    /// Handles Actions → Import Files.
    /// </summary>
    private async Task ImportFiles() {
        await HandleAddImport(GetImportSpec());
    }

    private async Task HandleAddImport(ConvConfig.FileConvSpec? spec) {
        string initialDir = _settingsService.GetString(AppSettings.LAST_ADD_DIR,
            Environment.CurrentDirectory);

        IReadOnlyList<string> files = await mFilePickerService.OpenFilesAsync(
            spec == null ? "Select files to add" : "Select files to import",
            initialDir: initialDir);
        if (files.Count == 0) {
            return;
        }

        string[] pathNames = new string[files.Count];
        for (int i = 0; i < files.Count; i++) {
            pathNames[i] = files[i];
        }

        // Compute the longest common directory prefix of all selected files.
        string basePath = Path.GetDirectoryName(Path.GetFullPath(pathNames[0])) ??
            Path.GetFullPath(pathNames[0]);
        for (int i = 1; i < pathNames.Length; i++) {
            string dir = Path.GetDirectoryName(Path.GetFullPath(pathNames[i])) ??
                Path.GetFullPath(pathNames[i]);
            basePath = GetCommonPathPrefix(basePath, dir);
        }
        _settingsService.SetString(AppSettings.LAST_ADD_DIR, basePath);

        await AddPaths(pathNames, IFileEntry.NO_ENTRY, spec, basePath);
    }

    /// <summary>
    /// Handles Actions → Extract Files.
    /// </summary>
    private async Task ExtractFiles() {
        await HandleExtractExport(null);
    }

    /// <summary>
    /// Handles Actions → Export Files.
    /// </summary>
    private async Task ExportFiles() {
        await HandleExtractExport(GetExportSpec());
    }

    private async Task HandleExtractExport(ConvConfig.FileConvSpec? exportSpec) {
        if (!GetFileSelection(omitDir: false, omitOpenArc: false, closeOpenArc: true,
                oneMeansAll: false, out object? archiveOrFileSystem,
                out IFileEntry selectionDir, out List<IFileEntry>? selected, out int _)) {
            return;
        }
        if (selected.Count == 0) {
            await mDialogService.ShowMessageAsync("No files selected.", "Empty");
            return;
        }

        // In full-list mode, use the volume dir as the base for path trimming.
        if (archiveOrFileSystem is IFileSystem system && !ShowSingleDirFileList) {
            selectionDir = system.GetVolDirEntry();
        }

        string initialDir = _settingsService.GetString(AppSettings.LAST_EXTRACT_DIR,
            Environment.CurrentDirectory);

        string? outputDir = await mFilePickerService.OpenFolderAsync(
            "Select destination for " + (exportSpec == null ? "extracted" : "exported") + " files",
            initialDir: initialDir);
        if (outputDir == null) {
            return;
        }

        _settingsService.SetString(AppSettings.LAST_EXTRACT_DIR, outputDir);

        ExtractProgress prog = new ExtractProgress(archiveOrFileSystem, selectionDir,
                selected, outputDir, exportSpec, _workspaceService.AppHook) {
            Preserve = _settingsService.GetEnum(AppSettings.EXT_PRESERVE_MODE,
                ExtractFileWorker.PreserveMode.None),
            AddExportExt = _settingsService.GetBool(AppSettings.EXT_ADD_EXPORT_EXT, true),
            EnableMacOSZip = _settingsService.GetBool(AppSettings.MAC_ZIP_ENABLED, true),
            StripPaths = _settingsService.GetBool(AppSettings.EXT_STRIP_PATHS_ENABLED, false),
            RawMode = _settingsService.GetBool(AppSettings.EXT_RAW_ENABLED, false),
            DefaultSpecs = GetDefaultExportSpecs()
        };
        Debug.WriteLine("Extract: outputDir='" + outputDir +
            "', selectionDir='" + selectionDir + "'");

        var workVm = new WorkProgressViewModel(prog, false);
        bool success = await mDialogService.ShowDialogAsync(workVm) == true;
        if (success) {
            _viewActions.ShowToast(exportSpec != null ? "Export successful"
                : "Extraction successful", true);
        } else {
            _viewActions.ShowToast("Cancelled", false);
        }
    }

    /// <summary>
    /// Handles Actions → Delete Files.
    /// </summary>
    private async Task DeleteFiles() {
        if (!GetSelectedArcDir(out object? archiveOrFileSystem, out DiskArcNode? daNode,
                out IFileEntry unused)) {
            return;
        }

        if (!GetFileSelection(omitDir: false, omitOpenArc: false, closeOpenArc: true,
                oneMeansAll: false, out archiveOrFileSystem, out IFileEntry unusedDir,
                out List<IFileEntry>? selected, out int _)) {
            return;
        }
        if (selected.Count == 0) {
            await mDialogService.ShowMessageAsync("No files selected.", "Empty");
            return;
        }

        // Remember the topmost selected row so we can reselect the item above it after
        // deletion.  GetFileSelection only reports an index in oneMeansAll mode (WPF reads
        // the DataGrid's SelectedIndex here), so compute it from the current selection.
        int firstSelIndex = int.MaxValue;
        foreach (FileListItem selItem in FileList.Selection) {
            int idx = FileList.Items.IndexOf(selItem);
            if (idx >= 0 && idx < firstSelIndex) {
                firstSelIndex = idx;
            }
        }
        if (firstSelIndex == int.MaxValue) {
            firstSelIndex = 0;
        }

        string msg = string.Format("Delete {0} file {1}?", selected.Count,
            selected.Count == 1 ? "entry" : "entries");
        if (!await mDialogService.ShowConfirmAsync(msg, "Confirm Delete")) {
            return;
        }

        DeleteProgress prog = new DeleteProgress(archiveOrFileSystem, daNode, selected,
                _workspaceService.AppHook) {
            DoCompress = _settingsService.GetBool(AppSettings.ADD_COMPRESS_ENABLED, true),
            EnableMacOSZip = _settingsService.GetBool(AppSettings.MAC_ZIP_ENABLED, true),
        };

        var workVm = new WorkProgressViewModel(prog, false);
        bool didCancel = await mDialogService.ShowDialogAsync(workVm) != true;
        if (!didCancel) {
            _viewActions.ShowToast("Deletion successful", true);
        } else {
            _viewActions.ShowToast("Cancelled", false);
        }

        if (!(didCancel && archiveOrFileSystem is IArchive)) {
            // Put the selection on the item above the first one we deleted, matching WPF.
            // FileList.Items still holds the pre-delete rows, so the neighbor entry is valid;
            // setting the view-model selection lets RefreshDirAndFileList preserve it by entry
            // (and scroll it into view).  Without this the now-deleted selected entry is lost
            // and the selection resets to the top of the list.
            if (firstSelIndex > 0 && FileList.Items.Count > 0) {
                int selectIdx = Math.Min(firstSelIndex - 1, FileList.Items.Count - 1);
                FileList.SelectedItem = FileList.Items[selectIdx];
            }
        }

        RefreshDirAndFileList();
    }

    /// <summary>
    /// Handles Actions → Test Files.
    /// </summary>
    private async Task TestFiles() {
        if (!GetFileSelection(omitDir: false, omitOpenArc: false, closeOpenArc: true,
                oneMeansAll: false, out object? archiveOrFileSystem,
                out IFileEntry unusedDir, out List<IFileEntry>? selected, out int unused)) {
            return;
        }
        if (selected.Count == 0) {
            await mDialogService.ShowMessageAsync("No files selected.", "Empty");
            return;
        }

        TestProgress prog = new TestProgress(archiveOrFileSystem, selected,
                _workspaceService.AppHook) {
            EnableMacOSZip = _settingsService.GetBool(AppSettings.MAC_ZIP_ENABLED, true),
        };
        var workVm = new WorkProgressViewModel(prog, false);
        if (await mDialogService.ShowDialogAsync(workVm) == true) {
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
                var reportVm = new ShowTextViewModel(sb.ToString(), "Test Results");
                await mDialogService.ShowDialogAsync(reportVm);
            } else {
                _viewActions.ShowToast("Tests successful, no failures", true);
            }
        } else {
            _viewActions.ShowToast("Cancelled", false);
        }
    }

    /// <summary>
    /// Moves files to a new directory.  Called from drag-drop handlers.
    /// </summary>
    public async Task MoveFiles(List<IFileEntry> moveList, IFileEntry targetDir) {
        if (mCurrentWorkObject is not IFileSystem) {
            Debug.WriteLine("Ignoring move request in " + mCurrentWorkObject);
            return;
        }
        if (targetDir == IFileEntry.NO_ENTRY) {
            Debug.Assert(false, "Drag target is NO_FILE");
            return;
        }
        if (!CanWrite) {
            await mDialogService.ShowMessageAsync("Drop target is not writable.", "Not Writable");
            return;
        }
        if (targetDir.IsDubious || targetDir.IsDamaged) {
            await mDialogService.ShowMessageAsync(
                "Destination directory is not writable.", "Not Writable");
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
                continue;
            }
            if (entry.IsDirectory) {
                IFileEntry checkEnt = targetDir;
                while (checkEnt != IFileEntry.NO_ENTRY) {
                    if (checkEnt == entry) {
                        await mDialogService.ShowMessageAsync(
                            "Cannot move a directory into itself or a descendant.",
                            "Bad Move");
                        return;
                    }
                    checkEnt = checkEnt.ContainingDir;
                }
            }
        }
        if (moveList.Count == 0) {
            Debug.WriteLine("Nothing to move");
            return;
        }

        if (!GetSelectedArcDir(out object? archiveOrFileSystem, out DiskArcNode? daNode,
                out IFileEntry unused)) {
            Debug.Assert(false);
            return;
        }

        MoveProgress prog = new MoveProgress(mCurrentWorkObject, daNode, moveList, targetDir,
                _workspaceService.AppHook) {
            DoCompress = _settingsService.GetBool(AppSettings.ADD_COMPRESS_ENABLED, true),
        };

        var workVm = new WorkProgressViewModel(prog, false);
        bool success = await mDialogService.ShowDialogAsync(workVm) == true;
        if (success) {
            _viewActions.ShowToast("Move successful", true);
            CaptureActiveFileViewerState();
        } else {
            _viewActions.ShowToast("Cancelled", false);
        }

        // Check whether any moved entry is a directory.  If so, rebuild ALL FileListItems
        // because child entries cache PathName at construction and moving a directory changes
        // the FullPathName of every descendant.
        bool movedDirectory = System.Linq.Enumerable.Any(moveList, e => e.IsDirectory);
        List<FileListItem> newSelection = [];

        if (movedDirectory) {
            HashSet<IFileEntry> movedSet = new HashSet<IFileEntry>(moveList);
            for (int i = 0; i < FileList.Items.Count; i++) {
                FileListItem old = FileList.Items[i];
                FileListItem rebuilt =
                    new FileListItem(old.FileEntry, _workspaceService.Formatter);
                FileList.Items[i] = rebuilt;
                if (movedSet.Contains(old.FileEntry)) {
                    newSelection.Add(rebuilt);
                }
            }
        } else {
            foreach (IFileEntry entry in moveList) {
                FileListItem? item = FileListItem.FindItemByEntry(FileList.Items, entry,
                        out int itemIndex);
                if (item == null) {
                    Debug.Assert(false, "unable to find entry: " + entry);
                    continue;
                }
                FileListItem newItem =
                    new FileListItem(entry, _workspaceService.Formatter);
                FileList.Items.RemoveAt(itemIndex);
                FileList.Items.Insert(itemIndex, newItem);
                newSelection.Add(newItem);
            }
        }

        _viewActions.SetFileListSelection(newSelection);
        RefreshDirAndFileList();
    }

    /// <summary>
    /// Returns the longest common directory prefix of two absolute directory paths.
    /// </summary>
    private static string GetCommonPathPrefix(string path1, string path2) {
        char sep = Path.DirectorySeparatorChar;
        string[] parts1 = path1.Split(sep);
        string[] parts2 = path2.Split(sep);
        int commonLen = Math.Min(parts1.Length, parts2.Length);
        int lastMatch = 0;
        for (int i = 0; i < commonLen; i++) {
            if (!string.Equals(parts1[i], parts2[i], StringComparison.Ordinal)) {
                break;
            }
            lastMatch = i + 1;
        }
        if (lastMatch == 0) {
            return Path.GetPathRoot(path1) ?? path1;
        }
        string result = string.Join(sep, parts1, 0, lastMatch);
        if (result.Length == 0) {
            result = sep.ToString();
        }
        return result;
    }

    // -----------------------------------------------------------------------------------------
    // Step 3E: Group E — clipboard operations and add-file helpers

    /// <summary>
    /// Shows a file-operation error dialog, fire-and-forget.
    /// </summary>
    private void ShowFileError(string msg) {
        Debug.WriteLine("ShowFileError: " + msg);
        _workspaceService.AppHook.LogE("ShowFileError: " + msg);
        _ = mDialogService.ShowMessageAsync(msg, "Error");
    }

    /// <summary>
    /// Returns the currently-configured import conversion spec from settings.
    /// </summary>
    private ConvConfig.FileConvSpec GetImportSpec() {
        var convTag = _settingsService.GetString(AppSettings.CONV_IMPORT_TAG, "text");
        var settingKey = AppSettings.IMPORT_SETTING_PREFIX + convTag;
        var convSettings = _settingsService.GetString(settingKey, string.Empty);
        var spec = string.IsNullOrEmpty(convSettings)
            ? ConvConfig.CreateSpec(convTag)
            : ConvConfig.CreateSpec(convTag + "," + convSettings);
        if (spec == null) {
            Debug.Assert(false, "Failed to parse import spec for tag: " + convTag);
            spec = ConvConfig.CreateSpec(convTag);
        }
        return spec;
    }

    /// <summary>
    /// Returns the currently-configured export conversion spec from settings.
    /// </summary>
    private ConvConfig.FileConvSpec GetExportSpec(string? convTag = null) {
        if (convTag == null) {
            bool useBest = _settingsService.GetBool(AppSettings.CONV_EXPORT_BEST, true);
            convTag = useBest ? ConvConfig.BEST
                : _settingsService.GetString(AppSettings.CONV_EXPORT_TAG, ConvConfig.BEST);
        }
        string settingKey = AppSettings.EXPORT_SETTING_PREFIX + convTag;
        string convSettings = _settingsService.GetString(settingKey, string.Empty);
        ConvConfig.FileConvSpec? spec = string.IsNullOrEmpty(convSettings)
            ? ConvConfig.CreateSpec(convTag)
            : ConvConfig.CreateSpec(convTag + "," + convSettings);
        if (spec == null) {
            Debug.Assert(false, "Failed to parse export spec for tag: " + convTag);
            spec = ConvConfig.CreateSpec(ConvConfig.BEST)!;
        }
        return spec;
    }

    /// <summary>
    /// Returns a dictionary of per-converter export specs for all registered converters.
    /// </summary>
    private Dictionary<string, ConvConfig.FileConvSpec>? GetDefaultExportSpecs() {
        var defaults = new Dictionary<string, ConvConfig.FileConvSpec>();
        List<string> tags = ExportFoundry.GetConverterTags();
        foreach (string tag in tags) {
            defaults[tag] = GetExportSpec(tag);
        }
        return defaults;
    }

    /// <summary>
    /// Builds AddFileSet options from current application settings.
    /// </summary>
    private AddFileSet.AddOpts ConfigureAddOpts(bool isImport) {
        AddFileSet.AddOpts addOpts = new AddFileSet.AddOpts();
        if (isImport) {
            addOpts.ParseADF = addOpts.ParseAS = addOpts.ParseNAPS =
                addOpts.CheckNamed = addOpts.CheckFinderInfo = false;
        } else {
            addOpts.ParseADF = _settingsService.GetBool(AppSettings.ADD_PRESERVE_ADF, true);
            addOpts.ParseAS = _settingsService.GetBool(AppSettings.ADD_PRESERVE_AS, true);
            addOpts.ParseNAPS = _settingsService.GetBool(AppSettings.ADD_PRESERVE_NAPS, true);
            addOpts.CheckNamed = false;
            addOpts.CheckFinderInfo = false;
        }
        addOpts.Recurse = _settingsService.GetBool(AppSettings.ADD_RECURSE_ENABLED, true);
        addOpts.StripExt = _settingsService.GetBool(AppSettings.ADD_STRIP_EXT_ENABLED, true);
        return addOpts;
    }

    /// <summary>
    /// Handles external file drop (from file manager).  Validates the target and delegates
    /// to AddPaths.
    /// </summary>
    internal async Task AddFileDrop(IFileEntry dropTarget, string[] pathNames) {
        Debug.Assert(pathNames.Length > 0);
        Debug.WriteLine("External file drop (target=" + dropTarget + "):");
        if (!CheckPasteDropOkay()) {
            return;
        }
        await AddPaths(pathNames, dropTarget,
            IsChecked_ImportExport ? GetImportSpec() : null, null);
    }

    /// <summary>
    /// Adds an array of file-system paths to the current archive/filesystem with a
    /// WorkProgress dialog.
    /// </summary>
    private async Task AddPaths(string[] pathNames, IFileEntry dropTarget,
            ConvConfig.FileConvSpec? importSpec, string? explicitBasePath) {
        Debug.WriteLine("Add paths (importSpec=" + importSpec + "):");
        foreach (string path in pathNames) {
            Debug.WriteLine("  " + path);
        }

        if (!GetSelectedArcDir(out object? archiveOrFileSystem, out DiskArcNode? daNode,
                out IFileEntry targetDir)) {
            return;
        }
        if (dropTarget != IFileEntry.NO_ENTRY && dropTarget.IsDirectory) {
            targetDir = dropTarget;
        }

        string basePath;
        if (!string.IsNullOrEmpty(explicitBasePath)) {
            basePath = Path.GetFullPath(explicitBasePath);
        } else {
            basePath = Path.GetDirectoryName(Path.GetFullPath(pathNames[0])) ??
                Path.GetFullPath(pathNames[0]);
        }

        AddFileSet.AddOpts addOpts = ConfigureAddOpts(importSpec != null);
        AddFileSet fileSet;
        try {
            fileSet = new AddFileSet(basePath, pathNames, addOpts, importSpec,
                _workspaceService.AppHook);
        } catch (IOException ex) {
            ShowFileError(ex.Message);
            return;
        }
        if (fileSet.Count == 0) {
            Debug.WriteLine("File set was empty");
            return;
        }

        AddProgress prog = new AddProgress(archiveOrFileSystem, daNode, fileSet, targetDir,
                _workspaceService.AppHook) {
            DoCompress = _settingsService.GetBool(AppSettings.ADD_COMPRESS_ENABLED, true),
            EnableMacOSZip = _settingsService.GetBool(AppSettings.MAC_ZIP_ENABLED, true),
            StripPaths = _settingsService.GetBool(AppSettings.ADD_STRIP_PATHS_ENABLED, false),
            RawMode = _settingsService.GetBool(AppSettings.ADD_RAW_ENABLED, false),
        };

        var workVm = new WorkProgressViewModel(prog, false);
        bool success = await mDialogService.ShowDialogAsync(workVm) == true;
        if (success) {
            _viewActions.ShowToast(importSpec == null ? "Files added" : "Files imported", true);
        } else {
            _viewActions.ShowToast("Cancelled", false);
        }

        // Refresh even if canceled — partial progress may have occurred.
        RefreshDirAndFileList();
        TryOpenNewSubVolumes();
    }

    /// <summary>
    /// Handles Edit → Copy.  Serializes selected entries to the clipboard via
    /// <see cref="IClipboardService.SetClipAsync"/>.
    /// </summary>
    private async Task CopyToClipboard() {
        if (!GetFileSelection(omitDir: false, omitOpenArc: false, closeOpenArc: true,
                oneMeansAll: false, out object? archiveOrFileSystem,
                out IFileEntry selectionDir, out List<IFileEntry>? entries, out int unused)) {
            entries = [];
        }

        IFileEntry baseDir = IFileEntry.NO_ENTRY;
        if (mCurrentWorkObject is IFileSystem && ShowSingleDirFileList) {
            DirectoryTreeItem? dirItem = DirectoryTree.SelectedItem;
            if (dirItem != null) {
                baseDir = dirItem.FileEntry;
            }
        }

        ExtractFileWorker.PreserveMode preserve =
            _settingsService.GetEnum(AppSettings.EXT_PRESERVE_MODE,
                ExtractFileWorker.PreserveMode.None);
        bool addExportExt = _settingsService.GetBool(AppSettings.EXT_ADD_EXPORT_EXT, true);
        bool rawMode = _settingsService.GetBool(AppSettings.EXT_RAW_ENABLED, false);
        bool doStrip = _settingsService.GetBool(AppSettings.EXT_STRIP_PATHS_ENABLED, false);
        bool doMacZip = _settingsService.GetBool(AppSettings.MAC_ZIP_ENABLED, true);
        ConvConfig.FileConvSpec? exportSpec = null;
        Dictionary<string, ConvConfig.FileConvSpec>? defaultSpecs = null;
        if (IsChecked_ImportExport) {
            exportSpec = GetExportSpec();
            defaultSpecs = GetDefaultExportSpecs();
        }

        ClipFileSet clipSet;
        _viewActions.SetCursorBusy(true);
        try {
            clipSet = new ClipFileSet(mCurrentWorkObject!, entries, baseDir,
                preserve, addExportExt: addExportExt, useRawData: rawMode, stripPaths: doStrip,
                enableMacZip: doMacZip, exportSpec, defaultSpecs, _workspaceService.AppHook);
        } finally {
            _viewActions.SetCursorBusy(false);
        }

        // Only serialize direct-transfer (non-export) entries.
        List<ClipFileEntry> xferEntries = clipSet.XferEntries;

        // File content is extracted to temp files inside SetClipAsync so the clipboard
        // JSON contains only metadata.  Do not base64-encode here.

        ClipInfo clipInfo = new ClipInfo(xferEntries, GlobalAppVersion.AppVersion) {
            StripPaths = doStrip
        };
        if (exportSpec != null) {
            clipInfo.IsExport = true;
        }

        ClearCutState(); // New copy supersedes any pending cut.
        await _clipboardService.SetClipAsync(clipInfo, xferEntries, clipSet.ForeignEntries);
        _workspaceService.AppHook.LogI("Copied " + xferEntries.Count + " entries to clipboard");
        _viewActions.ShowToast("Copied " + xferEntries.Count + " item(s)", true);
    }

    /// <summary>
    /// Handles Edit → Cut.  Same as Copy but marks the clipboard data as a cut and records
    /// the source entries so they can be deleted after a successful same-process paste.
    /// Cross-process paste (another instance) behaves as a Copy — source is not deleted.
    /// </summary>
    private async Task CutToClipboard() {
        if (!GetFileSelection(omitDir: false, omitOpenArc: false, closeOpenArc: true,
                oneMeansAll: false, out object? archiveOrFileSystem,
                out IFileEntry selectionDir, out List<IFileEntry>? entries, out int unused)) {
            entries = [];
        }
        if (entries == null || entries.Count == 0) return;

        IFileEntry baseDir = IFileEntry.NO_ENTRY;
        if (mCurrentWorkObject is IFileSystem && ShowSingleDirFileList) {
            DirectoryTreeItem? dirItem = DirectoryTree.SelectedItem;
            if (dirItem != null) baseDir = dirItem.FileEntry;
        }

        ExtractFileWorker.PreserveMode preserve =
            _settingsService.GetEnum(AppSettings.EXT_PRESERVE_MODE,
                ExtractFileWorker.PreserveMode.None);
        bool addExportExt = _settingsService.GetBool(AppSettings.EXT_ADD_EXPORT_EXT, true);
        bool rawMode = _settingsService.GetBool(AppSettings.EXT_RAW_ENABLED, false);
        bool doStrip = _settingsService.GetBool(AppSettings.EXT_STRIP_PATHS_ENABLED, false);
        bool doMacZip = _settingsService.GetBool(AppSettings.MAC_ZIP_ENABLED, true);

        // Cut always uses extract mode (not export) because we must preserve file data
        // for reliable re-insertion after deleting the source.
        ClipFileSet clipSet;
        _viewActions.SetCursorBusy(true);
        try {
            clipSet = new ClipFileSet(mCurrentWorkObject!, entries, baseDir,
                preserve, addExportExt: addExportExt, useRawData: rawMode, stripPaths: doStrip,
                enableMacZip: doMacZip, null, null, _workspaceService.AppHook);
        } finally {
            _viewActions.SetCursorBusy(false);
        }

        List<ClipFileEntry> xferEntries = clipSet.XferEntries;

        // File content is extracted to temp files inside SetClipAsync (same as CopyToClipboard).

        ClipInfo clipInfo = new ClipInfo(xferEntries, GlobalAppVersion.AppVersion) {
            IsCut = true,
            StripPaths = doStrip
        };

        // Capture cut source state for post-paste deletion.
        ClearCutState();
        if (GetSelectedArcDir(out object? srcArcOrFS, out DiskArcNode? srcDaNode, out _)) {
            mCutSourceObj = srcArcOrFS;
            mCutDaNode = srcDaNode;
            mCutEntries = new List<IFileEntry>(entries);
        }

        await _clipboardService.SetClipAsync(clipInfo, xferEntries, clipSet.ForeignEntries);
        _workspaceService.AppHook.LogI("Cut " + xferEntries.Count + " entries to clipboard");
        _viewActions.ShowToast("Cut " + entries.Count + " item(s)", true);
    }

    /// <summary>
    /// Carries the data needed for a cross-process or cross-instance drag operation.
    /// </summary>
    internal record DragTransferData(
        string ClipJsonText,
        IReadOnlyList<string> ExtractedPaths,
        string? TempDir);

    /// <summary>
    /// Builds a <see cref="DragTransferData"/> for the current file selection.
    /// Called from the view's drag initiation code to populate cross-process drag formats.
    /// Returns null if there is nothing to drag.
    /// </summary>
    internal DragTransferData? BuildDragData() {
        if (!GetFileSelection(omitDir: false, omitOpenArc: false, closeOpenArc: false,
                oneMeansAll: false, out object? archiveOrFileSystem,
                out IFileEntry selectionDir, out List<IFileEntry>? entries, out int unused)) {
            return null;
        }
        if (entries == null || entries.Count == 0) return null;

        IFileEntry baseDir = IFileEntry.NO_ENTRY;
        if (mCurrentWorkObject is IFileSystem && ShowSingleDirFileList) {
            DirectoryTreeItem? dirItem = DirectoryTree.SelectedItem;
            if (dirItem != null) baseDir = dirItem.FileEntry;
        }

        ExtractFileWorker.PreserveMode preserve =
            _settingsService.GetEnum(AppSettings.EXT_PRESERVE_MODE,
                ExtractFileWorker.PreserveMode.None);
        bool addExportExt = _settingsService.GetBool(AppSettings.EXT_ADD_EXPORT_EXT, true);
        bool rawMode = _settingsService.GetBool(AppSettings.EXT_RAW_ENABLED, false);
        bool doStrip = _settingsService.GetBool(AppSettings.EXT_STRIP_PATHS_ENABLED, false);
        bool doMacZip = _settingsService.GetBool(AppSettings.MAC_ZIP_ENABLED, true);
        ConvConfig.FileConvSpec? exportSpec = IsChecked_ImportExport ? GetExportSpec() : null;
        Dictionary<string, ConvConfig.FileConvSpec>? defaultSpecs =
            IsChecked_ImportExport ? GetDefaultExportSpecs() : null;

        ClipFileSet clipSet;
        try {
            clipSet = new ClipFileSet(mCurrentWorkObject!, entries, baseDir,
                preserve, addExportExt: addExportExt, useRawData: rawMode, stripPaths: doStrip,
                enableMacZip: doMacZip, exportSpec, defaultSpecs, _workspaceService.AppHook);
        } catch (Exception ex) {
            AppLog.E("Drag/copy: building clip file set failed", ex);
            return null;
        }

        List<ClipFileEntry> xferEntries = clipSet.XferEntries;

        // Always create a temp dir: xfer entries go into "x/" for the CP2 JSON,
        // foreign entries go into the root for the OS file manager.
        string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "cp2drag_" + Environment.ProcessId + "_" +
            DateTime.UtcNow.Ticks.ToString("x"));
        System.IO.Directory.CreateDirectory(tempDir);

        // Extract xfer entries to temp files.  Paths are passed to the destination via
        // a "Cp2FileData" array injected into the JSON by Cp2ClipUtil — AppCommon's
        // ClipFileEntry stays metadata-only.
        string xferDir = System.IO.Path.Combine(tempDir, "x");
        System.IO.Directory.CreateDirectory(xferDir);
        var xferPaths = new string?[xferEntries.Count];
        for (int i = 0; i < xferEntries.Count; i++) {
            ClipFileEntry entry = xferEntries[i];
            if (entry.mStreamGen == null || entry.Attribs.IsDirectory) continue;
            string tempPath = System.IO.Path.Combine(xferDir, "e" + i.ToString("d4"));
            try {
                using var outStream = new System.IO.FileStream(tempPath, System.IO.FileMode.Create);
                entry.mStreamGen.OutputToStream(outStream);
                xferPaths[i] = tempPath;
            } catch (Exception ex) {
                AppLog.E("Drag/copy: transfer extraction failed", ex);
            }
        }

        ClipInfo clipInfo = new ClipInfo(xferEntries, GlobalAppVersion.AppVersion) {
            StripPaths = doStrip,   // carry source's EXT intent so PasteOrDrop can OR with ADD
        };
        if (exportSpec != null) {
            clipInfo.IsExport = true;
        }
        string clipJsonText = Services.Cp2ClipUtil.InjectFileData(clipInfo.ToClipString(), xferPaths);

        // Extract foreign entries to the temp dir root for OS file manager consumption.
        // Collect top-level paths explicitly to exclude the "x" xfer subdir.
        var extractedPaths = new List<string>();
        var seenTopLevel = new System.Collections.Generic.HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (ClipFileEntry entry in clipSet.ForeignEntries) {
            string extractPath = System.IO.Path.Combine(tempDir, entry.ExtractPath);
            string? dirName = System.IO.Path.GetDirectoryName(extractPath);
            if (dirName != null && !System.IO.Directory.Exists(dirName))
                System.IO.Directory.CreateDirectory(dirName);
            if (entry.Attribs.IsDirectory) {
                System.IO.Directory.CreateDirectory(extractPath);
            } else {
                if (entry.mStreamGen == null) continue;
                try {
                    using var outStream = new System.IO.FileStream(
                        extractPath, System.IO.FileMode.Create);
                    entry.mStreamGen.OutputToStream(outStream);
                } catch (Exception ex) {
                    AppLog.E("Drag/copy: foreign-format extraction failed", ex);
                }
            }
            // Track top-level item (first path component relative to tempDir).
            string rel = entry.ExtractPath;
            int sep = rel.IndexOfAny(new[] { System.IO.Path.DirectorySeparatorChar, '/' });
            string topName = sep >= 0 ? rel.Substring(0, sep) : rel;
            seenTopLevel.Add(System.IO.Path.Combine(tempDir, topName));
        }
        extractedPaths.AddRange(seenTopLevel);

        return new DragTransferData(clipJsonText, extractedPaths, tempDir);
    }

    /// <summary>
    /// Deletes the source entries after a successful same-process Cut+Paste.
    /// </summary>
    private async Task DeleteCutEntries() {
        object? srcObj = mCutSourceObj;
        DiskArcNode? srcNode = mCutDaNode;
        List<IFileEntry>? entries = mCutEntries;
        ClearCutState();

        if (srcObj == null || srcNode == null || entries == null || entries.Count == 0) {
            return;
        }

        DeleteProgress delProg = new DeleteProgress(srcObj, srcNode, entries,
                _workspaceService.AppHook) {
            DoCompress = _settingsService.GetBool(AppSettings.ADD_COMPRESS_ENABLED, true),
            EnableMacOSZip = _settingsService.GetBool(AppSettings.MAC_ZIP_ENABLED, true),
        };
        var workVm = new WorkProgressViewModel(delProg, false);
        bool success = await mDialogService.ShowDialogAsync(workVm) == true;
        if (success) {
            _workspaceService.AppHook.LogI("Cut source files deleted after paste");
        } else {
            _workspaceService.AppHook.LogW("Failed to delete cut source files");
        }
        RefreshDirAndFileList();
    }

    /// <summary>
    /// Handles Edit → Paste and file-manager drag-drop onto the file list.
    /// </summary>
    /// <param name="dropData">Data from a drag-drop operation, or null for clipboard paste.</param>
    /// <param name="dropTarget">File entry the data was dropped onto, or NO_ENTRY.</param>
    public async Task PasteOrDrop(Avalonia.Input.IDataObject? dropData, IFileEntry dropTarget) {
        if (!CheckPasteDropOkay()) {
            return;
        }

        // For cross-instance drag-drop, check the drag data object for CP2 JSON first.
        // Check both the private UTI and the plain-text fallback used by the macOS native
        // drag path (where DataFormats.Text carries the JSON when custom UTI lookup fails).
        // For keyboard Paste (Ctrl+V), dropData is null and we fall through to the clipboard.
        // Keep the raw text so we can extract the Cp2FileData array for the stream generator.
        ClipInfo? clipInfo = null;
        string? rawClipText = null;
        if (dropData != null) {
            string? clipText = dropData.Get(ClipInfo.CROSS_INSTANCE_FORMAT) as string;
            if (!ClipInfo.IsClipTextFromCP2(clipText))
                clipText = dropData.Get(Avalonia.Input.DataFormats.Text) as string;
            clipInfo = ClipInfo.FromClipString(clipText);
            rawClipText = clipText;
        }
        if (clipInfo == null) {
            clipInfo = await _clipboardService.GetClipAsync();
            // Cross-instance clipboard paste: read raw text for Cp2FileData extraction.
            // Same-process paste uses live mStreamGen so this call is skipped there.
            if (clipInfo != null && clipInfo.ProcessId != System.Environment.ProcessId)
                rawClipText = await _clipboardService.GetRawClipTextAsync();
        }

        if (clipInfo == null) {
            // Not CP2 data — check for external files from a file manager.
            string? uriList = await _clipboardService.GetUriListAsync();
            if (!string.IsNullOrEmpty(uriList)) {
                await PasteExternalFiles(uriList, dropTarget);
                return;
            }

            await mDialogService.ShowMessageAsync(
                "No CiderPress II file data found on the clipboard.\n\n" +
                "Use Edit \u2192 Copy to copy files within CiderPress II,\n" +
                "or copy files in your file manager to paste them here.",
                "Nothing to Paste");
            return;
        }

        if (clipInfo.IsExport) {
            await mDialogService.ShowMessageAsync(
                "The file copy was performed in \"export\" mode.  Please use \"extract\" " +
                "mode when copying files between CiderPress II windows.",
                "Can't Paste Exports");
            return;
        }

        if (clipInfo.AppVersionMajor != GlobalAppVersion.AppVersion.Major ||
                clipInfo.AppVersionMinor != GlobalAppVersion.AppVersion.Minor ||
                clipInfo.AppVersionPatch != GlobalAppVersion.AppVersion.Patch) {
            await mDialogService.ShowMessageAsync(
                "Cannot copy and paste between different versions of the application.",
                "Version Mismatch");
            return;
        }

        if (clipInfo.ClipEntries!.Count == 0) {
            Debug.WriteLine("Pasting empty file set");
            return;
        }
        _workspaceService.AppHook.LogI("Paste from clipboard; found " +
            clipInfo.ClipEntries.Count + " files/forks");

        // Bring this window to the front so confirmation dialogs appear on top.
        _dialogHost.GetOwnerWindow()?.Activate();

        if (!GetSelectedArcDir(out object? archiveOrFileSystem, out DiskArcNode? daNode,
                out IFileEntry targetDir)) {
            return;
        }
        if (dropTarget != IFileEntry.NO_ENTRY && dropTarget.IsDirectory) {
            targetDir = dropTarget;
        }
        if (clipInfo.ProcessId == Environment.ProcessId && archiveOrFileSystem is IArchive) {
            await mDialogService.ShowMessageAsync(
                "Files cannot be copied and pasted from a file archive to itself.",
                "Conflict");
            return;
        }

        // Build entry→temp-file map from the Cp2FileData array injected by the source.
        // Same-process paste always uses mStreamGen below; this map is only reached for
        // cross-instance paste where mStreamGen is null.
        var xferPathMap = new System.Collections.Generic.Dictionary<ClipFileEntry, string>();
        string?[]? xferData = Services.Cp2ClipUtil.ExtractFileData(rawClipText);
        if (xferData != null && clipInfo.ClipEntries != null) {
            for (int i = 0; i < System.Math.Min(xferData.Length, clipInfo.ClipEntries.Count); i++) {
                if (!string.IsNullOrEmpty(xferData[i]))
                    xferPathMap[clipInfo.ClipEntries[i]] = xferData[i]!;
            }
        }

        ClipPasteWorker.ClipStreamGenerator streamGen =
            delegate (ClipFileEntry clipEntry) {
                if (clipEntry.mStreamGen != null) {
                    MemoryStream ms = new MemoryStream();
                    clipEntry.mStreamGen.OutputToStream(ms);
                    ms.Position = 0;
                    return ms;
                }
                if (xferPathMap.TryGetValue(clipEntry, out string? path) &&
                        System.IO.File.Exists(path))
                    return System.IO.File.OpenRead(path);
                return null;
            };

        ClipPasteProgress prog =
            new ClipPasteProgress(archiveOrFileSystem, daNode, targetDir,
                    clipInfo, streamGen, _workspaceService.AppHook) {
                DoCompress = _settingsService.GetBool(AppSettings.ADD_COMPRESS_ENABLED, true),
                EnableMacOSZip = _settingsService.GetBool(AppSettings.MAC_ZIP_ENABLED, true),
                ConvertDOSText = _settingsService.GetBool(AppSettings.DOS_TEXT_CONV_ENABLED, false),
                // Strip if the SOURCE set EXT_STRIP (intent carried in clipInfo.StripPaths)
                // OR if the DESTINATION's import setting says to strip.  The two settings
                // are independent: EXT strips at copy/drag time, ADD strips at paste time.
                StripPaths = clipInfo.StripPaths ||
                             _settingsService.GetBool(AppSettings.ADD_STRIP_PATHS_ENABLED, false),
                RawMode = _settingsService.GetBool(AppSettings.ADD_RAW_ENABLED, false),
            };

        var workVm = new WorkProgressViewModel(prog, false);
        bool success = await mDialogService.ShowDialogAsync(workVm) == true;
        if (success) {
            _viewActions.ShowToast("Files added", true);
            // For same-process Cut+Paste, delete the source files now that paste succeeded.
            if (clipInfo.IsCut && clipInfo.ProcessId == Environment.ProcessId &&
                    mCutEntries != null) {
                await DeleteCutEntries();
            }
        } else {
            _viewActions.ShowToast("Cancelled", false);
        }

        RefreshDirAndFileList();
        TryOpenNewSubVolumes();
    }

    /// <summary>
    /// Handles paste of external files from the system clipboard.  Parses a text/uri-list
    /// string into local file paths and adds them via the standard add-files path.
    /// </summary>
    private async Task PasteExternalFiles(string uriList, IFileEntry dropTarget) {
        var paths = new List<string>();
        foreach (string line in uriList.Split('\n')) {
            string trimmed = line.Trim('\r', ' ');
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#')) {
                continue;   // skip comments and blank lines per RFC 2483
            }
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri) && uri.IsFile) {
                paths.Add(uri.LocalPath);
            }
        }
        if (paths.Count == 0) {
            await mDialogService.ShowMessageAsync(
                "The clipboard contains file references, but none are local files.",
                "Nothing to Paste");
            return;
        }
        _workspaceService.AppHook.LogI("Paste external files from clipboard: " +
            paths.Count + " file(s)");
        await AddFileDrop(dropTarget, paths.ToArray());
    }

    // -----------------------------------------------------------------------------------------
    // Step 3C: Group C — disk/partition operations

    /// <summary>
    /// Handles Actions → Save As Disk Image.
    /// </summary>
    private async Task SaveAsDiskImage() {
        IChunkAccess? chunks = GetCurrentWorkChunks();
        if (chunks == null) {
            Debug.Assert(false);
            return;
        }

        var vm = new SaveAsDiskViewModel(mCurrentWorkObject!, chunks,
            _workspaceService.Formatter, _workspaceService.AppHook,
            mFilePickerService, _settingsService, mDialogService);
        bool? result = await mDialogService.ShowDialogAsync(vm);
        if (result == true) {
            _viewActions.ShowToast("Saved", true);
        }
    }

    /// <summary>
    /// Handles Actions → Replace Partition.
    /// </summary>
    private async Task ReplacePartition() {
        Debug.Assert(_workspaceService.WorkTree != null);
        ArchiveTreeItem? arcTreeSel = CachedArchiveTreeSelection;
        if (arcTreeSel == null) {
            Debug.Assert(false);
            return;
        }
        WorkTree.Node workNode = arcTreeSel.WorkTreeNode;
        Partition? dstPartition = workNode.DAObject as Partition;
        if (dstPartition == null) {
            Debug.Assert(false);
            return;
        }

        // Ask the user to pick a source disk image.
        string? pathName = await mFilePickerService.OpenFileAsync("Open");
        if (string.IsNullOrEmpty(pathName)) {
            return;
        }
        string ext = Path.GetExtension(pathName);

        FileStream stream;
        try {
            stream = new FileStream(pathName, FileMode.Open, FileAccess.Read);
        } catch (Exception ex) {
            AppLog.E("Unable to open file '" + pathName + "'", ex);
            await mDialogService.ShowMessageAsync("Unable to open file: " + ex.Message, "I/O Error");
            return;
        }

        using (stream) {
            FileAnalyzer.AnalysisResult result = FileAnalyzer.Analyze(stream, ext,
                _workspaceService.AppHook, out FileKind kind, out SectorOrder orderHint);
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
                    errMsg = IsDiskImageFile(kind) ? string.Empty : "File is not a disk image.";
                    break;
                default:
                    errMsg = "Internal error: unexpected result from analyzer: " + result;
                    break;
            }
            if (!string.IsNullOrEmpty(errMsg)) {
                await mDialogService.ShowMessageAsync(errMsg, "Unable to use file");
                return;
            }

            using IDiskImage? diskImage = FileAnalyzer.PrepareDiskImage(stream, kind,
                    _workspaceService.AppHook);
            if (diskImage == null) {
                await mDialogService.ShowMessageAsync(
                    "Unable to prepare disk image, type=" + ThingString.FileKind(kind) + ".",
                    "Unable to use file");
                return;
            }
            if (!diskImage.AnalyzeDisk(null, orderHint, IDiskImage.AnalysisDepth.ChunksOnly)) {
                await mDialogService.ShowMessageAsync(
                    "Unable to determine format of disk image contents.", "Unable to use file");
                return;
            }
            Debug.Assert(diskImage.ChunkAccess != null);

            bool wasClosed = false;
            ReplacePartitionViewModel.EnableWriteFunc func = delegate () {
                Debug.Assert(_workspaceService.WorkTree.CheckHealth());
                workNode.CloseChildren();
                dstPartition.CloseContents();
                Debug.Assert(_workspaceService.WorkTree.CheckHealth());
                arcTreeSel.Items.Clear();
                wasClosed = true;
                return true;
            };

            var vm = new ReplacePartitionViewModel(dstPartition,
                diskImage.ChunkAccess, func, _workspaceService.Formatter,
                _workspaceService.AppHook, mDialogService);
            bool? dlgResult = await mDialogService.ShowDialogAsync(vm);
            if (dlgResult == true) {
                _viewActions.ShowToast("Completed", true);
            }

            _viewActions.SetCursorBusy(true);
            try {
                if (wasClosed) {
                    _workspaceService.WorkTree.ReprocessPartition(workNode);
                    foreach (WorkTree.Node childNode in workNode) {
                        ArchiveTreeItem.ConstructTree(arcTreeSel, childNode);
                    }
                    HandleWorkspaceViewerInvalidation();
                }
            } catch (Exception ex) {
                AppLog.E("Replace partition: post-processing failed", ex);
            } finally {
                _viewActions.SetCursorBusy(false);
            }
        }
    }

    /// <summary>
    /// Handles Actions → Scan For Bad Blocks.
    /// </summary>
    private async Task ScanForBadBlocks() {
        IDiskImage? diskImage = mCurrentWorkObject as IDiskImage;
        if (diskImage == null) {
            Debug.Assert(false);
            return;
        }
        if (diskImage.ChunkAccess == null) {
            await mDialogService.ShowMessageAsync(
                "Disk format was not recognized, so all blocks are \"bad\".", "Nope");
            return;
        }

        ScanBlocksProgress prog = new ScanBlocksProgress(diskImage, _workspaceService.AppHook);
        var workVm = new WorkProgressViewModel(prog, false);
        if (await mDialogService.ShowDialogAsync(workVm) == true) {
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

                var reportVm = new ShowTextViewModel(sb.ToString(), "Errors");
                await mDialogService.ShowDialogAsync(reportVm);
            } else {
                _viewActions.ShowToast("Scan successful, no errors", true);
            }
        } else {
            _viewActions.ShowToast("Cancelled", false);
        }
    }

    /// <summary>
    /// Handles Actions → Scan For Sub-Volumes.
    /// </summary>
    private void ScanForSubVol() {
        IFileSystem? fs = mCurrentWorkObject as IFileSystem;
        if (fs == null) {
            Debug.Assert(false);
            return;
        }
        ArchiveTreeItem? arcTreeSel = CachedArchiveTreeSelection;
        if (arcTreeSel == null) {
            Debug.Assert(false);
            return;
        }
        Debug.Assert(arcTreeSel.WorkTreeNode.DAObject == fs);

        IMultiPart? embeds = fs.FindEmbeddedVolumes();
        if (embeds == null) {
            Debug.WriteLine("No sub-volumes found");
            return;
        }

        // If already open, just select it.
        ArchiveTreeItem? existing =
            ArchiveTreeItem.FindItemByDAObject(ArchiveTree.TreeRoot, embeds);
        if (existing != null) {
            ArchiveTreeItem.SelectItem(_viewActions, existing);
            return;
        }

        // Add to the tree and select the new multipart node.
        WorkTree.Node? newNode =
            _workspaceService.WorkTree!.TryCreateMultiPart(arcTreeSel.WorkTreeNode, embeds);
        if (newNode != null) {
            ArchiveTreeItem newItem = ArchiveTreeItem.ConstructTree(arcTreeSel, newNode);
            ArchiveTreeItem.SelectItem(_viewActions, newItem);
        }
    }

    /// <summary>
    /// Handles Actions → Defragment.
    /// </summary>
    private async Task Defragment() {
        Pascal? fs = mCurrentWorkObject as Pascal;
        if (fs == null) {
            Debug.Assert(false);
            return;
        }
        bool ok = false;
        try {
            fs.PrepareRawAccess();
            ok = fs.Defragment();
        } catch (Exception ex) {
            AppLog.E("Defragment failed", ex);
            await mDialogService.ShowMessageAsync("Error: " + ex.Message, "Failed");
            return;
        } finally {
            fs.PrepareFileAccess(true);
            RefreshDirAndFileList(false);
        }
        if (ok) {
            _viewActions.ShowToast("Defragmentation successful", true);
        } else {
            await mDialogService.ShowMessageAsync(
                "Filesystems with errors cannot be defragmented.", "Failed");
        }
    }

    /// <summary>
    /// Handles Actions → Close Sub-Tree (archive tree command button).
    /// </summary>
    private void CloseSubTree() {
        ArchiveTreeItem? arcTreeSel = CachedArchiveTreeSelection;
        if (arcTreeSel == null) {
            return;
        }
        CloseSubTree(arcTreeSel);
    }

    // -----------------------------------------------------------------------------------------
    // Step 3B: Group B — edit attributes and create directory

    /// <summary>
    /// Handles Actions → Edit Attributes (file list selection).
    /// </summary>
    private async Task EditAttributes() {
        ArchiveTreeItem? arcTreeSel = CachedArchiveTreeSelection;
        FileListItem? fileItem = FileList.SelectedItem;
        if (arcTreeSel == null || fileItem == null) {
            Debug.Assert(false);
            return;
        }

        DirectoryTreeItem? dirTreeItem = null;
        if (fileItem.FileEntry.IsDirectory) {
            dirTreeItem = DirectoryTreeItem.FindItemByEntry(DirectoryTree.TreeRoot,
                fileItem.FileEntry);
        }

        await EditAttributesImpl(arcTreeSel.WorkTreeNode, fileItem.FileEntry,
            dirTreeItem, fileItem);
    }

    /// <summary>
    /// Handles Actions → Edit Directory Attributes (directory tree selection).
    /// </summary>
    private async Task EditDirAttributes() {
        ArchiveTreeItem? arcTreeSel = CachedArchiveTreeSelection;
        DirectoryTreeItem? dirTreeSel = CachedDirectoryTreeSelection;
        if (arcTreeSel == null || dirTreeSel == null) {
            Debug.Assert(false);
            return;
        }

        FileListItem? fileItem =
            FileListItem.FindItemByEntry(FileList.Items, dirTreeSel.FileEntry);
        await EditAttributesImpl(arcTreeSel.WorkTreeNode, dirTreeSel.FileEntry,
            dirTreeSel, fileItem);
    }

    private async Task EditAttributesImpl(WorkTree.Node workNode, IFileEntry entry,
            DirectoryTreeItem? dirItem, FileListItem? fileItem) {
        Debug.WriteLine("EditAttributes: " + entry);
        object archiveOrFileSystem = workNode.DAObject;

        bool isMacZip = false;
        bool isMacZipEnabled = _settingsService.GetBool(AppSettings.MAC_ZIP_ENABLED, true);
        IFileEntry adfEntry = IFileEntry.NO_ENTRY;
        FileAttribs curAttribs = new FileAttribs(entry);

        if (isMacZipEnabled && workNode.DAObject is Zip arc &&
                Zip.HasMacZipHeader(arc, entry, out adfEntry)) {
            using (Stream adfStream = ArcTemp.ExtractToTemp(arc, adfEntry,
                       FilePart.DataFork)) {
                using (IArchive adfArchive = AppleSingle.OpenArchive(adfStream,
                        _workspaceService.AppHook)) {
                    IFileEntry adfArchiveEntry = adfArchive.GetFirstEntry();
                    curAttribs.GetFromAppleSingle(adfArchiveEntry);
                    curAttribs.FullPathName = entry.FullPathName;
                    isMacZip = true;

                    bool isReadOnly;
                    if (archiveOrFileSystem is IFileSystem fs) {
                        isReadOnly = fs.IsReadOnly;
                    } else if (archiveOrFileSystem is IArchive ia) {
                        isReadOnly = ia.IsReadOnly;
                    } else {
                        isReadOnly = false;
                    }

                    var vm = new EditAttributesViewModel(
                        archiveOrFileSystem, entry, adfArchiveEntry, curAttribs, isReadOnly);
                    // Only apply changes if the user accepted (OK).  IsValid just tracks
                    // field validity and is true for Cancel too, so gate on the dialog result.
                    bool? accepted = await mDialogService.ShowDialogAsync(vm);
                    if (accepted != true) {
                        return;
                    }

                    EditAttributesProgress prog = new EditAttributesProgress(
                            _dialogHost.GetOwnerWindow(), arc,
                            workNode.FindDANode(), entry, adfEntry,
                            vm.NewAttribs, _workspaceService.AppHook) {
                        DoCompress = _settingsService.GetBool(AppSettings.ADD_COMPRESS_ENABLED, true),
                        EnableMacOSZip = isMacZipEnabled,
                    };

                    if (prog.DoUpdate(isMacZip)) {
                        _viewActions.ShowToast("File attributes edited", true);
                        FinishEditAttributes(entry, adfEntry, vm.NewAttribs,
                            isMacZip, fileItem, dirItem, archiveOrFileSystem);
                        CaptureActiveFileViewerState();
                    }
                }
            }
            // Refresh with focus on the file list (WPF default) so the repopulate reselects
            // the edited entry — otherwise a rename that moves the row (e.g. HFS re-sorts by
            // name) loses the selection entirely.
            RefreshDirAndFileList();
            return;
        }

        // Non-MacZip path.
        if (!isMacZip) {
            bool isReadOnly;
            if (archiveOrFileSystem is IFileSystem fs2) {
                isReadOnly = fs2.IsReadOnly;
            } else if (archiveOrFileSystem is IArchive ia2) {
                isReadOnly = ia2.IsReadOnly;
            } else {
                isReadOnly = false;
            }

            var vm2 = new EditAttributesViewModel(
                archiveOrFileSystem, entry, IFileEntry.NO_ENTRY, curAttribs, isReadOnly);
            // Only apply changes if the user accepted (OK).  IsValid just tracks
            // field validity and is true for Cancel too, so gate on the dialog result.
            bool? accepted = await mDialogService.ShowDialogAsync(vm2);
            if (accepted != true) {
                return;
            }

            EditAttributesProgress prog2 = new EditAttributesProgress(
                    _dialogHost.GetOwnerWindow(), workNode.DAObject, workNode.FindDANode(),
                    entry, IFileEntry.NO_ENTRY, vm2.NewAttribs, _workspaceService.AppHook) {
                DoCompress = _settingsService.GetBool(AppSettings.ADD_COMPRESS_ENABLED, true),
                EnableMacOSZip = isMacZipEnabled,
            };

            if (prog2.DoUpdate(false)) {
                _viewActions.ShowToast("File attributes edited", true);
                FinishEditAttributes(entry, IFileEntry.NO_ENTRY, vm2.NewAttribs,
                    false, fileItem, dirItem, archiveOrFileSystem);
                CaptureActiveFileViewerState();
            }
        }

        // Refresh with focus on the file list (WPF default) so the repopulate reselects
        // the edited entry — otherwise a rename that moves the row (e.g. HFS re-sorts by
        // name) loses the selection entirely.
        RefreshDirAndFileList();
    }

    private void FinishEditAttributes(IFileEntry entry, IFileEntry adfEntry,
            FileAttribs newAttribs, bool isMacZip, FileListItem? fileItem,
            DirectoryTreeItem? dirItem, object archiveOrFileSystem) {
        if (fileItem != null) {
            var newFli = (isMacZip
                ? new FileListItem(entry, adfEntry, newAttribs, _workspaceService.Formatter)
                : new FileListItem(entry, _workspaceService.Formatter));
            var index = FileList.Items.IndexOf(fileItem);
            if (index >= 0) {
                FileList.Items[index] = newFli;
                FileList.SelectedItem = newFli;
                // Replacing the selected item makes the DataGrid advance its visual
                // selection (it doesn't two-way bind SelectedItem the way WPF does);
                // re-select the edited entry so the grid highlight stays put and a
                // follow-up Alt-Enter edits the item the user is actually looking at.
                _viewActions.SelectFileListItemByEntry(entry);
            }

            ArchiveTreeItem? ati =
                ArchiveTreeItem.FindItemByEntry(ArchiveTree.TreeRoot, entry);
            if (ati != null) {
                ati.Name = entry.FileName;
            }
        }

        if (dirItem != null) {
            dirItem.Name = entry.FileName;

            if (entry.IsDirectory && entry.ContainingDir == IFileEntry.NO_ENTRY) {
                ArchiveTreeItem? ati =
                    ArchiveTreeItem.FindItemByDAObject(ArchiveTree.TreeRoot, archiveOrFileSystem);
                if (ati != null) {
                    ati.Name = entry.FileName;
                }
            }
        }

        // When a directory is renamed, child entries' cached PathName strings become stale.
        // Rebuild every file list item in-place to keep the current sort order.
        if (entry.IsDirectory) {
            FileListItem? newSelection = null;
            for (int i = 0; i < FileList.Items.Count; i++) {
                FileListItem old = FileList.Items[i];
                FileListItem rebuilt = new FileListItem(old.FileEntry, _workspaceService.Formatter);
                FileList.Items[i] = rebuilt;
                if (old.FileEntry == entry) {
                    newSelection = rebuilt;
                }
            }
            if (newSelection != null) {
                FileList.SelectedItem = newSelection;
                // Keep the DataGrid's visual selection in sync (see note above).
                _viewActions.SelectFileListItemByEntry(entry);
            }
        }
    }

    /// <summary>
    /// Handles Actions → Create Directory.
    /// </summary>
    private async Task CreateDirectory() {
        if (!GetSelectedArcDir(out object? archiveOrFileSystem, out DiskArcNode? _,
                out IFileEntry targetDir)) {
            return;
        }
        IFileSystem? fs = archiveOrFileSystem as IFileSystem;
        if (fs == null) {
            Debug.Assert(false);
            return;
        }

        string rules = "\u2022 " + fs.Characteristics.FileNameSyntaxRules;
        var vm = new CreateDirectoryViewModel(fs, targetDir, fs.IsValidFileName, rules);
        if (await mDialogService.ShowDialogAsync(vm) != true) {
            return;
        }

        try {
            IFileEntry newEntry = fs.CreateFile(targetDir, vm.NewFileName,
                IFileSystem.CreateMode.Directory);
            RefreshDirAndFileList(false);
            _viewActions.SelectFileListItemByEntry(newEntry);
        } catch (Exception ex) {
            _workspaceService.AppHook.LogE("CreateDirectory failed: " + ex.Message);
            _ = mDialogService.ShowMessageAsync("Failed to create directory: " + ex.Message,
                "Error");
        }
    }

    // -----------------------------------------------------------------------------------------
    // Step 3F: Group F — debug/misc commands

    /// <summary>
    /// Navigates to the parent node. Consults the directory tree first; if already at the
    /// volume root and dirOnly is false, navigates up in the archive tree instead.
    /// </summary>
    private void NavToParent(bool dirOnly)
    {
        // Step 1: try to go up in the directory tree.
        DirectoryTreeItem? dirSel = CachedDirectoryTreeSelection;
        if (dirSel is { Parent: not null })
        {
            _viewActions.ScrollDirectoryTreeItemIntoView(dirSel.Parent);
            _viewActions.SelectDirectoryTreeItem(dirSel.Parent);
            _viewActions.FocusDirectoryTree();
            return;
        }

        // Step 2: at directory root (or no directory tree). If dirOnly, stop here.
        if (dirOnly)
        {
            return;
        }

        // Step 3: try to go up in the archive tree.
        ArchiveTreeItem? arcSel = CachedArchiveTreeSelection;
        if (arcSel is { Parent: not null })
        {
            _viewActions.SelectArchiveTreeItem(arcSel.Parent);
            _viewActions.FocusArchiveTree();
        }
    }

    // Quick-and-dirty ANI file conversion helper. Returns null on failure.
    private static AnimatedGifEncoder? DoConvertANI(Stream dataStream)
    {
        const int PIC_LEN = 32768;
        if (dataStream.Length < PIC_LEN + 12 || dataStream.Length > 512 * 1024 * 1024)
        {
            Debug.WriteLine("unsupported file size: " + dataStream.Length);
            return null;
        }
        dataStream.Position = 0;
        byte[] buf = new byte[dataStream.Length];
        dataStream.ReadExactly(buf, 0, buf.Length);
        uint dataLen = RawData.GetU32LE(buf, PIC_LEN);
        ushort vblDelay = RawData.GetU16LE(buf, PIC_LEN + 4);
        uint animLen = RawData.GetU32LE(buf, PIC_LEN + 8);
        if (buf.Length < PIC_LEN + 8 + (int)animLen)
        {
            Debug.WriteLine("file was cut off");
            return null;
        }
        int delayMsec = (int)(vblDelay * (1000 / 60.0));
        if (delayMsec == 0)
        {
            delayMsec = 1;
        }
        AnimatedGifEncoder enc = new AnimatedGifEncoder();

        // ANI files have an initial frame, and a series of looping animation frames.  The
        // initial frame isn't really part of the animated sequence, so we don't want to
        // output it.
        int animOff = PIC_LEN + 12;
        int animEnd = PIC_LEN + 8 + (int)animLen;
        if (animEnd <= animOff)
        {
            // Work around missing anim len by using full data len.
            animEnd = PIC_LEN + 8 + (int)dataLen;
        }
        while (animOff < animEnd)
        {
            while (animOff < animEnd)
            {
                Debug.Assert(animOff >= 0 && animOff < buf.Length);
                ushort dataOff = RawData.ReadU16LE(buf, ref animOff);
                ushort value = RawData.ReadU16LE(buf, ref animOff);
                if (dataOff == 0)
                {
                    break;
                }
                if (dataOff > 0x7ffe)
                {
                    Debug.WriteLine("found invalid data offset $" + dataOff.ToString("x4"));
                }
                else
                {
                    RawData.SetU16LE(buf, dataOff, value);
                }
            }
            Bitmap8 rawImage = FileConv.Gfx.SuperHiRes.ConvertBuffer(buf);
            enc.AddFrame(rawImage, delayMsec);
            if (enc.Count > 10000)
            {
                Debug.WriteLine("runaway!");
                break;
            }
        }

        return enc;
    }


    // --- Properties ---

    private string mWindowTitle = "CiderPress II";
    public string WindowTitle
    {
        get => mWindowTitle;
        set => SetProperty(ref mWindowTitle, value);
    }

    // ---- Panel visibility ----
    private bool mLaunchPanelVisible = true;
    public bool LaunchPanelVisible
    {
        get => mLaunchPanelVisible;
        set => SetProperty(ref mLaunchPanelVisible, value);
    }

    private bool mMainPanelVisible;
    public bool MainPanelVisible
    {
        get => mMainPanelVisible;
        set => SetProperty(ref mMainPanelVisible, value);
    }

    // ShowOptionsPanel, ShowHideRotation → Options.ShowOptionsPanel, Options.ShowHideRotation

    // ---- Debug Visibility ----

    private bool mShowDebugMenu;
    public bool ShowDebugMenu
    {
        get => mShowDebugMenu;
        set => SetProperty(ref mShowDebugMenu, value);
    }

    private bool mIsDebugLogVisible;
    public bool IsDebugLogVisible
    {
        get => mIsDebugLogVisible;
        set => SetProperty(ref mIsDebugLogVisible, value);
    }

    private bool mIsDropTargetVisible;
    public bool IsDropTargetVisible
    {
        get => mIsDropTargetVisible;
        set => SetProperty(ref mIsDropTargetVisible, value);
    }

    // ---- Version String ----
    // CenterStatusText, RightStatusText → StatusBar.CenterText, StatusBar.RightText

    public string ProgramVersionString => GlobalAppVersion.AppVersion.ToString();

    // ---- Tree Collections (moved to ArchiveTree.TreeRoot / DirectoryTree.TreeRoot) ----
    // ArchiveTreeRoot → ArchiveTree.TreeRoot
    // DirectoryTreeRoot → DirectoryTree.TreeRoot

    // ---- File List (moved to FileList.Items / FileList.SelectedItem) ----
    // FileList collection → FileList.Items
    // SelectedFileListItem → FileList.SelectedItem

    // ---- Recent Files ----

    private string mRecentFileName1 = string.Empty;
    public string RecentFileName1
    {
        get => mRecentFileName1;
        set
        {
            SetProperty(ref mRecentFileName1, value);
            OnPropertyChanged(nameof(ShowRecentFile1));
        }
    }

    private string mRecentFilePath1 = string.Empty;
    public string RecentFilePath1
    {
        get => mRecentFilePath1;
        set => SetProperty(ref mRecentFilePath1, value);
    }

    public bool ShowRecentFile1 => !string.IsNullOrEmpty(mRecentFileName1);

    private string mRecentFileName2 = string.Empty;
    public string RecentFileName2
    {
        get => mRecentFileName2;
        set
        {
            SetProperty(ref mRecentFileName2, value);
            OnPropertyChanged(nameof(ShowRecentFile2));
        }
    }

    private string mRecentFilePath2 = string.Empty;
    public string RecentFilePath2
    {
        get => mRecentFilePath2;
        set => SetProperty(ref mRecentFilePath2, value);
    }

    public bool ShowRecentFile2 => !string.IsNullOrEmpty(mRecentFileName2);

    // ---- Options Panel (moved to OptionsPanelViewModel) ----
    // ImportConverters, ExportConverters, SelectedImportConverter, SelectedExportConverter
    // → Options.ImportConverters, Options.ExportConverters, etc.
    // IsChecked_Add*, IsChecked_Ext*, IsChecked_AddExtract/ImportExport → Options.*
    // IsExportBestChecked/IsExportComboChecked → Options.IsExportBestChecked/IsExportComboChecked
    // IsChecked_ExtPreserve* → Options.ExtPreserveNone/AS/ADF/NAPS
    // SelectedDDCPModeIndex → Options.DDCPModeIndex

    // Forwarding accessors kept for code that references IsChecked_ImportExport directly
    // (AddFileDrop, clipboard operations).
    private bool IsChecked_ImportExport => Options.ImportExport;

    // ---- Toolbar Brushes ----

    public static readonly IBrush ToolbarHighlightBrush = Brushes.Green;
    public static readonly IBrush ToolbarNohiBrush = Brushes.Transparent;

    private IBrush mFullListBorderBrush = Brushes.Transparent;
    public IBrush FullListBorderBrush
    {
        get => mFullListBorderBrush;
        set => SetProperty(ref mFullListBorderBrush, value);
    }

    private IBrush mDirListBorderBrush = Brushes.Transparent;
    public IBrush DirListBorderBrush
    {
        get => mDirListBorderBrush;
        set => SetProperty(ref mDirListBorderBrush, value);
    }

    private IBrush mInfoBorderBrush = Brushes.Transparent;
    public IBrush InfoBorderBrush
    {
        get => mInfoBorderBrush;
        set => SetProperty(ref mInfoBorderBrush, value);
    }

    // ---- Center Panel Display ----

    private bool mShowCenterInfo;
    public bool ShowCenterFileList => !mShowCenterInfo;
    public bool ShowCenterInfoPanel => mShowCenterInfo;

    /// <summary>
    /// Configures column visibility based on the type of content being shown.
    /// </summary>
    public void ConfigureCenterPanel(bool isInfoOnly, bool isArchive, bool isHierarchic,
            bool hasRsrc, bool hasRaw)
    {
        ShowSingleDirFileList = !(isArchive || (isHierarchic && !PreferSingleDirList));
        HasInfoOnly = isInfoOnly;
        SetShowCenterInfo(HasInfoOnly ? CenterPanelChange.Info : CenterPanelChange.Files);
        if (isInfoOnly)
        {
            IsFullListEnabled = IsDirListEnabled = false;
        }
        else if (isArchive)
        {
            IsFullListEnabled = true;
            IsDirListEnabled = false;
        }
        else if (isHierarchic)
        {
            IsFullListEnabled = IsDirListEnabled = true;
        }
        else
        {
            IsFullListEnabled = false;
            IsDirListEnabled = true;
        }
        FileList.ShowCol_Format = isArchive;
        FileList.ShowCol_RawLen = hasRaw;
        FileList.ShowCol_RsrcLen = hasRsrc;
        FileList.ShowCol_TotalSize = !isArchive;
    }

    /// <summary>
    /// Sets the center panel mode. Guards against switching away from
    /// info-only mode, updates toolbar brush highlights, and raises
    /// notifications for ShowCenterFileList and ShowCenterInfoPanel.
    /// </summary>
    internal void SetShowCenterInfo(CenterPanelChange req)
    {
        if (HasInfoOnly && req != CenterPanelChange.Info)
        {
            Debug.WriteLine("Ignoring attempt to switch to file list");
            return;
        }
        switch (req)
        {
            case CenterPanelChange.Info: mShowCenterInfo = true; break;
            case CenterPanelChange.Files: mShowCenterInfo = false; break;
            case CenterPanelChange.Toggle: mShowCenterInfo = !mShowCenterInfo; break;
        }
        OnPropertyChanged(nameof(ShowCenterFileList));
        OnPropertyChanged(nameof(ShowCenterInfoPanel));
        if (mShowCenterInfo)
        {
            InfoBorderBrush = ToolbarHighlightBrush;
            FullListBorderBrush = DirListBorderBrush = ToolbarNohiBrush;
        }
        else if (ShowSingleDirFileList)
        {
            DirListBorderBrush = ToolbarHighlightBrush;
            FullListBorderBrush = InfoBorderBrush = ToolbarNohiBrush;
        }
        else
        {
            FullListBorderBrush = ToolbarHighlightBrush;
            DirListBorderBrush = InfoBorderBrush = ToolbarNohiBrush;
        }
    }



    private bool mShowSingleDirFileList;
    public bool ShowSingleDirFileList
    {
        get => mShowSingleDirFileList;
        set
        {
            SetProperty(ref mShowSingleDirFileList, value);
            FileList.ShowCol_FileName = value;
            FileList.ShowCol_PathName = !value;
        }
    }

    private bool mHasInfoOnly;
    public bool HasInfoOnly
    {
        get => mHasInfoOnly;
        set => SetProperty(ref mHasInfoOnly, value);
    }

    // Temporary — promoted to ISettingsService in Phase 3A.
    internal bool PreferSingleDirList
    {
        get => _settingsService.GetBool(AppSettings.FILE_LIST_PREFER_SINGLE, true);
        set
        {
            _settingsService.SetBool(AppSettings.FILE_LIST_PREFER_SINGLE, value);
            OnPropertyChanged();
        }
    }

    private bool mIsFullListEnabled;
    public bool IsFullListEnabled
    {
        get => mIsFullListEnabled;
        set => SetProperty(ref mIsFullListEnabled, value);
    }

    private bool mIsDirListEnabled;
    public bool IsDirListEnabled
    {
        get => mIsDirListEnabled;
        set => SetProperty(ref mIsDirListEnabled, value);
    }

    // IsResetSortEnabled → FileList.IsResetSortEnabled
    // ShowCol_* → FileList.ShowCol_*
    // CenterInfoText1/2, CenterInfoList, ShowDiskUtilityButtons, etc. → CenterInfo.*
    // SelectedArchiveTreeItem, SelectedDirectoryTreeItem → ArchiveTree.SelectedItem, etc.

    // ---- Helper Methods ----

    internal void ClearTreesAndLists()
    {
        ArchiveTree.TreeRoot.Clear();
        DirectoryTree.TreeRoot.Clear();
        FileList.Items.Clear();
        IsFullListEnabled = false;
        IsDirListEnabled = false;
    }

    /// <summary>
    /// Populates ImportConverters and ExportConverters.
    /// Kept as a wrapper so MainWindow.axaml.cs can call it from Loaded.
    /// </summary>
    public void InitImportExportConfig()
    {
        Options.InitConverters();
        // RefreshFromSettings was called earlier (from ApplySettings) when the converter
        // lists were still empty, so saved selections couldn't be restored then.
        // Now that the lists are populated, re-apply to restore the user's saved choices.
        Options.RefreshFromSettings();
    }

    /// <summary>
    /// Re-publishes side options. Kept as a wrapper for backward compatibility.
    /// </summary>
    public void PublishSideOptions()
    {
        Options.RefreshFromSettings();
    }

    // ---- CanExecute State Properties ----

    private bool mIsFileOpen;
    public bool IsFileOpen
    {
        get => mIsFileOpen;
        set => SetProperty(ref mIsFileOpen, value);
    }

    private bool mCanWrite;
    public bool CanWrite
    {
        get => mCanWrite;
        set => SetProperty(ref mCanWrite, value);
    }

    private bool mAreFileEntriesSelected;
    public bool AreFileEntriesSelected
    {
        get => mAreFileEntriesSelected;
        set => SetProperty(ref mAreFileEntriesSelected, value);
    }

    private bool mIsSingleEntrySelected;
    public bool IsSingleEntrySelected
    {
        get => mIsSingleEntrySelected;
        set => SetProperty(ref mIsSingleEntrySelected, value);
    }

    private bool mCanEditBlocks;
    public bool CanEditBlocks
    {
        get => mCanEditBlocks;
        set => SetProperty(ref mCanEditBlocks, value);
    }

    private bool mCanEditSectors;
    public bool CanEditSectors
    {
        get => mCanEditSectors;
        set => SetProperty(ref mCanEditSectors, value);
    }

    private bool mHasChunks;
    public bool HasChunks
    {
        get => mHasChunks;
        set => SetProperty(ref mHasChunks, value);
    }

    private bool mIsANISelected;
    public bool IsANISelected
    {
        get => mIsANISelected;
        set => SetProperty(ref mIsANISelected, value);
    }

    private bool mIsDiskImageSelected;
    public bool IsDiskImageSelected
    {
        get => mIsDiskImageSelected;
        set => SetProperty(ref mIsDiskImageSelected, value);
    }

    private bool mIsPartitionSelected;
    public bool IsPartitionSelected
    {
        get => mIsPartitionSelected;
        set => SetProperty(ref mIsPartitionSelected, value);
    }

    private bool mIsDiskOrPartitionSelected;
    public bool IsDiskOrPartitionSelected
    {
        get => mIsDiskOrPartitionSelected;
        set => SetProperty(ref mIsDiskOrPartitionSelected, value);
    }

    private bool mIsNibbleImageSelected;
    public bool IsNibbleImageSelected
    {
        get => mIsNibbleImageSelected;
        set => SetProperty(ref mIsNibbleImageSelected, value);
    }


    private bool mIsFileSystemSelected;
    public bool IsFileSystemSelected
    {
        get => mIsFileSystemSelected;
        set => SetProperty(ref mIsFileSystemSelected, value);
    }

    private bool mIsMultiFileItemSelected;
    public bool IsMultiFileItemSelected
    {
        get => mIsMultiFileItemSelected;
        set => SetProperty(ref mIsMultiFileItemSelected, value);
    }

    private bool mIsDefragmentableSelected;
    public bool IsDefragmentableSelected
    {
        get => mIsDefragmentableSelected;
        set => SetProperty(ref mIsDefragmentableSelected, value);
    }

    private bool mIsHierarchicalFileSystemSelected;
    public bool IsHierarchicalFileSystemSelected
    {
        get => mIsHierarchicalFileSystemSelected;
        set => SetProperty(ref mIsHierarchicalFileSystemSelected, value);
    }

    private bool mIsSelectedDirRoot;
    public bool IsSelectedDirRoot
    {
        get => mIsSelectedDirRoot;
        set => SetProperty(ref mIsSelectedDirRoot, value);
    }

    private bool mIsSelectedArchiveRoot;
    public bool IsSelectedArchiveRoot
    {
        get => mIsSelectedArchiveRoot;
        set => SetProperty(ref mIsSelectedArchiveRoot, value);
    }

    private bool mIsClosableTreeSelected;
    public bool IsClosableTreeSelected
    {
        get => mIsClosableTreeSelected;
        set => SetProperty(ref mIsClosableTreeSelected, value);
    }


    // =========================================================================
    // CanExecute methods for commands
    // =========================================================================

    private bool CanClose() => IsFileOpen;
    private bool CanCopy() => IsFileOpen && AreFileEntriesSelected && ShowCenterFileList;
    private bool CanCut() => IsFileOpen && AreFileEntriesSelected && ShowCenterFileList && CanWrite;
    private bool CanPaste() => IsFileOpen && CanWrite && IsMultiFileItemSelected;
    private bool CanFind() => IsFileOpen;
    private bool CanSelectAll() => IsFileOpen;
    private bool CanViewFiles() => CanCopy();
    private bool CanAddFiles() => IsFileOpen && CanWrite && IsMultiFileItemSelected && ShowCenterFileList;
    private bool CanExtractFiles() => CanCopy();
    private bool CanExportFiles() => CanCopy();
    private bool CanDeleteFiles() => IsFileOpen && CanWrite && IsMultiFileItemSelected && AreFileEntriesSelected && ShowCenterFileList;
    private bool CanTestFiles() => CanCopy();
    private bool CanEditAttributes() => IsFileOpen && IsSingleEntrySelected;
    private bool CanCreateDirectory() => IsFileOpen && CanWrite && IsHierarchicalFileSystemSelected;
    private bool CanEditDirAttributes() => IsFileOpen && IsFileSystemSelected;
    private bool CanEditSectorsCmd() => IsFileOpen && CanEditSectors;
    private bool CanEditBlocksCmd() => IsFileOpen && CanEditBlocks;
    private bool CanSaveAsDiskImage() => IsFileOpen && IsDiskOrPartitionSelected && HasChunks;
    private bool CanReplacePartition() => IsFileOpen && CanWrite && IsPartitionSelected;
    private bool CanScanForBadBlocks() => IsFileOpen && IsNibbleImageSelected;
    private bool CanScanForSubVol() => IsFileOpen && IsFileSystemSelected;
    private bool CanDefragment() => IsFileOpen && IsDefragmentableSelected && CanWrite;
    private bool CanCloseSubTree() => IsFileOpen && IsClosableTreeSelected;
    private bool CanShowFullList() => IsFullListEnabled;
    private bool CanShowDirList() => IsDirListEnabled;
    private bool CanShowInfo() => IsFileOpen;
    private bool CanToggleInfo() => IsFileOpen;
    private bool CanNavToParentDir() =>
        IsFileOpen && IsHierarchicalFileSystemSelected && !IsSelectedDirRoot;
    private bool CanNavToParent() =>
        IsFileOpen && ((IsHierarchicalFileSystemSelected && !IsSelectedDirRoot) || !IsSelectedArchiveRoot);
    private bool CanConvertANI() => IsANISelected;

    /// <summary>
    /// Notifies all gated commands that their CanExecute state may have changed.
    /// Call at the end of RefreshAllCommandStates().
    /// </summary>
    private void NotifyAllCommandsCanExecuteChanged()
    {
        CloseCommand.NotifyCanExecuteChanged();
        SelectAllCommand.NotifyCanExecuteChanged();
        FindCommand.NotifyCanExecuteChanged();
        CutCommand.NotifyCanExecuteChanged();
        CopyCommand.NotifyCanExecuteChanged();
        PasteCommand.NotifyCanExecuteChanged();
        ViewFilesCommand.NotifyCanExecuteChanged();
        ViewFilesInNewViewerCommand.NotifyCanExecuteChanged();
        AddFilesCommand.NotifyCanExecuteChanged();
        ImportFilesCommand.NotifyCanExecuteChanged();
        ExtractFilesCommand.NotifyCanExecuteChanged();
        ExportFilesCommand.NotifyCanExecuteChanged();
        DeleteFilesCommand.NotifyCanExecuteChanged();
        TestFilesCommand.NotifyCanExecuteChanged();
        EditAttributesCommand.NotifyCanExecuteChanged();
        CreateDirectoryCommand.NotifyCanExecuteChanged();
        EditDirAttributesCommand.NotifyCanExecuteChanged();
        EditSectorsCommand.NotifyCanExecuteChanged();
        EditBlocksCommand.NotifyCanExecuteChanged();
        SaveAsDiskImageCommand.NotifyCanExecuteChanged();
        ReplacePartitionCommand.NotifyCanExecuteChanged();
        ScanForBadBlocksCommand.NotifyCanExecuteChanged();
        ScanForSubVolCommand.NotifyCanExecuteChanged();
        DefragmentCommand.NotifyCanExecuteChanged();
        CloseSubTreeCommand.NotifyCanExecuteChanged();
        ShowFullListCommand.NotifyCanExecuteChanged();
        ShowDirListCommand.NotifyCanExecuteChanged();
        ShowInfoCommand.NotifyCanExecuteChanged();
        ToggleInfoCommand.NotifyCanExecuteChanged();
        NavToParentDirCommand.NotifyCanExecuteChanged();
        NavToParentCommand.NotifyCanExecuteChanged();
        Debug_ConvertANICommand.NotifyCanExecuteChanged();
    }

    // File menu
    public IRelayCommand NewDiskImageCommand { get; }
    public IRelayCommand NewFileArchiveCommand { get; }
    public IRelayCommand OpenCommand { get; }
    public IRelayCommand OpenPhysicalDriveCommand { get; }
    public IRelayCommand CloseCommand { get; }
    public IRelayCommand ExitCommand { get; }


    // Recent files
    public IRelayCommand RecentFile1Command { get; }
    public IRelayCommand RecentFile2Command { get; }
    public IRelayCommand RecentFile3Command { get; }
    public IRelayCommand RecentFile4Command { get; }
    public IRelayCommand RecentFile5Command { get; }
    public IRelayCommand RecentFile6Command { get; }

    // Edit menu
    public IRelayCommand CutCommand { get; }
    public IRelayCommand CopyCommand { get; }
    public IRelayCommand PasteCommand { get; }
    public IRelayCommand FindCommand { get; }
    public IRelayCommand SelectAllCommand { get; }
    public IRelayCommand EditAppSettingsCommand { get; }

    // Actions menu
    public IRelayCommand ViewFilesCommand { get; }
    public IRelayCommand ViewFilesInNewViewerCommand { get; }
    public IRelayCommand AddFilesCommand { get; }
    public IRelayCommand ImportFilesCommand { get; }
    public IRelayCommand ExtractFilesCommand { get; }
    public IRelayCommand ExportFilesCommand { get; }
    public IRelayCommand DeleteFilesCommand { get; }
    public IRelayCommand TestFilesCommand { get; }
    public IRelayCommand EditAttributesCommand { get; }
    public IRelayCommand CreateDirectoryCommand { get; }
    public IRelayCommand EditDirAttributesCommand { get; }
    public IRelayCommand EditSectorsCommand { get; }
    public IRelayCommand EditBlocksCommand { get; }
    public IRelayCommand SaveAsDiskImageCommand { get; }
    public IRelayCommand ReplacePartitionCommand { get; }
    public IRelayCommand ScanForBadBlocksCommand { get; }
    public IRelayCommand ScanForSubVolCommand { get; }
    public IRelayCommand DefragmentCommand { get; }
    public IRelayCommand CloseSubTreeCommand { get; }

    // View menu
    public IRelayCommand ShowFullListCommand { get; }
    public IRelayCommand ShowDirListCommand { get; }
    public IRelayCommand ShowInfoCommand { get; }

    // Navigate
    public IRelayCommand NavToParentDirCommand { get; }
    public IRelayCommand NavToParentCommand { get; }

    // Tools
    public IRelayCommand ShowAsciiChartCommand { get; }

    // Help
    public IRelayCommand HelpCommand { get; }
    public IRelayCommand AboutCommand { get; }

    // Debug
    public IRelayCommand Debug_DiskArcLibTestCommand { get; }
    public IRelayCommand Debug_FileConvLibTestCommand { get; }
    public IRelayCommand Debug_BulkCompressTestCommand { get; }
    public IRelayCommand Debug_ShowSystemInfoCommand { get; }
    public IRelayCommand Debug_ShowDebugLogCommand { get; }
    public IRelayCommand Debug_ShowDropTargetCommand { get; }
    public IRelayCommand Debug_ConvertANICommand { get; }

    // Toolbar
    public IRelayCommand ResetSortCommand { get; private set; }
    public IRelayCommand ToggleInfoCommand { get; }

    /*
        // ---- Temporary Pass-through properties -- removed in iteration 2 -----

        public ICommand? OpenCommand { get; set; }
        public ICommand? NewDiskImageCommand { get; set; }
        public ICommand? NewFileArchiveCommand { get; set; }
        public ICommand? OpenPhysicalDriveCommand { get; set; }
        public ICommand? CloseCommand { get; set; }
        public ICommand? RecentFile1Command { get; set; }
        public ICommand? RecentFile2Command { get; set; }
        public ICommand? RecentFile3Command { get; set; }
        public ICommand? RecentFile4Command { get; set; }
        public ICommand? RecentFile5Command { get; set; }
        public ICommand? RecentFile6Command { get; set; }
        public ICommand? ExitCommand { get; set; }
        public ICommand? CopyCommand { get; set; }
        public ICommand? PasteCommand { get; set; }
        public ICommand? FindCommand { get; set; }
        public ICommand? SelectAllCommand { get; set; }
        public ICommand? EditAppSettingsCommand { get; set; }
        public ICommand? ViewFilesCommand { get; set; }
        public ICommand? AddFilesCommand { get; set; }
        public ICommand? ImportFilesCommand { get; set; }
        public ICommand? ExtractFilesCommand { get; set; }
        public ICommand? ExportFilesCommand { get; set; }
        public ICommand? DeleteFilesCommand { get; set; }
        public ICommand? TestFilesCommand { get; set; }
        public ICommand? EditAttributesCommand { get; set; }
        public ICommand? CreateDirectoryCommand { get; set; }
        public ICommand? EditDirAttributesCommand { get; set; }
        public ICommand? EditSectorsCommand { get; set; }
        public ICommand? EditBlocksCommand { get; set; }
        public ICommand? SaveAsDiskImageCommand { get; set; }
        public ICommand? ReplacePartitionCommand { get; set; }
        public ICommand? ScanForBadBlocksCommand { get; set; }
        public ICommand? ScanForSubVolCommand { get; set; }
        public ICommand? DefragmentCommand { get; set; }
        public ICommand? CloseSubTreeCommand { get; set; }
        public ICommand? ShowFullListCommand { get; set; }
        public ICommand? ShowDirListCommand { get; set; }
        public ICommand? ShowInfoCommand { get; set; }
        public ICommand? NavToParentDirCommand { get; set; }
        public ICommand? NavToParentCommand { get; set; }
        public ICommand? HelpCommand { get; set; }
        public ICommand? AboutCommand { get; set; }
        public ICommand? Debug_DiskArcLibTestCommand { get; set; }
        public ICommand? Debug_FileConvLibTestCommand { get; set; }
        public ICommand? Debug_BulkCompressTestCommand { get; set; }
        public ICommand? Debug_ShowSystemInfoCommand { get; set; }
        public ICommand? Debug_ShowDebugLogCommand { get; set; }
        public ICommand? Debug_ShowDropTargetCommand { get; set; }
        public ICommand? Debug_ConvertANICommand { get; set; }
        public ICommand? ResetSortCommand { get; set; }
        public ICommand? ToggleInfoCommand { get; set; }
    */

    // =========================================================================
    // Step 3H — State fields, selection caches, and helpers
    // =========================================================================

    // Reentrancy guard for file list ↔ directory tree selection sync.
    private bool mSyncingSelection;
    // mSwitchFocusToFileList → FileList.RequestFocusAfterPopulate()

    /// <summary>
    /// Currently-selected DiskArc library object in the archive tree (i.e.
    /// WorkTreeNode.DAObject).  May be IDiskImage, IArchive, IMultiPart, IFileSystem, or
    /// Partition.
    /// </summary>
    private object? mCurrentWorkObject;

    /// <summary>
    /// Exposes the currently open archive or filesystem for use by the drag/drop handler
    /// in the main window code-behind.
    /// </summary>
    internal object? CurrentWorkObject => mCurrentWorkObject;

    /// <summary>
    /// Exposes the workspace path name for use by the drag/drop handler.
    /// </summary>
    internal string WorkPathName => _workspaceService.WorkPathName;

    // ---- Cut state (same-process cut+paste) ----
    // Stores the source object and entries for a pending Cut.  After a successful
    // same-process paste the entries are deleted from the source.
    private object? mCutSourceObj;
    private DiskArcNode? mCutDaNode;
    private List<IFileEntry>? mCutEntries;

    private void ClearCutState() {
        mCutSourceObj = null;
        mCutDaNode = null;
        mCutEntries = null;
    }

    /// <summary>
    /// Cached archive tree selection. Updated in OnArchiveTreeSelectionChanged.
    /// </summary>
    internal ArchiveTreeItem? CachedArchiveTreeSelection { get; private set; }

    /// <summary>
    /// Cached directory tree selection. Updated in OnDirectoryTreeSelectionChanged and
    /// SyncDirectoryTreeToFileSelection.
    /// </summary>
    internal DirectoryTreeItem? CachedDirectoryTreeSelection { get; private set; }

    // FileListSelection → FileList.Selection (moved to FileListViewModel)

    /// <summary>
    /// Called by the View when the file-list DataGrid selection changes.
    /// </summary>
    public void UpdateFileListSelection(IList<FileListItem> items)
    {
        FileList.SetSelection(items);
        RefreshAllCommandStates();

        // Keep the open viewer's navigation pool in sync with whatever is currently
        // highlighted, so Up/Down always reflects the live selection.
        // Skip this when the selection change was triggered by the viewer's own navigation —
        // in that case the pool is already correct and we must not overwrite it.
        if (!mViewerNavigating) {
            FileViewerViewModel? viewer = GetActiveFileViewer();
            if (viewer != null && mActiveViewerWindow?.IsVisible == true) {
                if (GetFileSelection(omitDir: true, omitOpenArc: true, closeOpenArc: false,
                        oneMeansAll: true, out object? aofs, out IFileEntry _,
                        out List<IFileEntry>? sel, out int _) && sel.Count > 0) {
                    viewer.UpdatePool(aofs, sel);
                }
            }
        }
    }

    /// <summary>
    /// Shows a simple modal message box. Routes through the dialog service.
    /// </summary>
    private Task ShowMessageAsync(string message, string title)
    {
        return mDialogService.ShowMessageAsync(message, title);
    }

    /// <summary>
    /// Recomputes all state-dependent CanExecute properties from the VM's own state
    /// and raises change notifications so command CanExecute methods re-evaluate.
    /// </summary>
    internal void RefreshAllCommandStates()
    {
        IsFileOpen = _workspaceService.IsFileOpen;
        IsFileOpen = _workspaceService.IsFileOpen;
        var arcTreeSel = CachedArchiveTreeSelection;

        CanWrite = arcTreeSel is { WorkTreeNode.IsReadOnly: false };

        AreFileEntriesSelected = FileList.Selection.Count > 0;
        IsSingleEntrySelected = FileList.Selection.Count == 1;

        if (FileList.Selection.Count == 1) {
            FileListItem selItem = FileList.Selection[0];
            FileAttribs attrs = new FileAttribs(selItem.FileEntry);
            IsANISelected = attrs is { FileType: FileAttribs.FILE_TYPE_ANI, AuxType: 0x0000 };
        } else {
            IsANISelected = false;
        }

        IsDiskImageSelected = mCurrentWorkObject is IDiskImage;
        IsPartitionSelected = mCurrentWorkObject is Partition;
        IsDiskOrPartitionSelected =
            mCurrentWorkObject is IDiskImage || mCurrentWorkObject is Partition;
        IsNibbleImageSelected = mCurrentWorkObject is INibbleDataAccess;
        IsFileSystemSelected = mCurrentWorkObject is IFileSystem;

        if (mCurrentWorkObject is IFileSystem) {
            IsMultiFileItemSelected = true;
        } else if (mCurrentWorkObject is IArchive archive) {
            IsMultiFileItemSelected =
                !archive.Characteristics.HasSingleEntry;
        } else {
            IsMultiFileItemSelected = false;
        }

        IsDefragmentableSelected = mCurrentWorkObject is Pascal;

        IsHierarchicalFileSystemSelected =
            (mCurrentWorkObject is IFileSystem { Characteristics.IsHierarchical: true });

        DirectoryTreeItem? dirSel = CachedDirectoryTreeSelection;
        IsSelectedDirRoot = dirSel is { Parent: null };
        IsSelectedArchiveRoot = arcTreeSel is { Parent: null };
        IsClosableTreeSelected = arcTreeSel is { CanClose: true };

        IChunkAccess? chunks = GetCurrentWorkChunks();
        HasChunks = chunks != null;
        CanEditBlocks = chunks is { HasBlocks: true };
        CanEditSectors = chunks is { HasSectors: true };

        NotifyAllCommandsCanExecuteChanged();
    }

    private IChunkAccess? GetCurrentWorkChunks()
    {
        if (mCurrentWorkObject is IDiskImage image) {
            return image.ChunkAccess;
        } else if (mCurrentWorkObject is Partition partition) {
            return partition.ChunkAccess;
        } else {
            return null;
        }
    }

    /// <summary>
    /// Gets the currently selected archive or filesystem and directory entry.
    /// </summary>
    public bool GetSelectedArcDir(
            [NotNullWhen(true)] out object? archiveOrFileSystem,
            [NotNullWhen(true)] out DiskArcNode? daNode,
            out IFileEntry selectionDir)
    {
        archiveOrFileSystem = null;
        daNode = null;
        selectionDir = IFileEntry.NO_ENTRY;

        ArchiveTreeItem? arcTreeSel = CachedArchiveTreeSelection;
        DirectoryTreeItem? dirTreeSel = CachedDirectoryTreeSelection;
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
    /// Gets the current selection from the file list.
    /// </summary>
    public bool GetFileSelection(bool omitDir, bool omitOpenArc, bool closeOpenArc,
            bool oneMeansAll,
            [NotNullWhen(true)] out object? archiveOrFileSystem,
            out IFileEntry selectionDir,
            [NotNullWhen(true)] out List<IFileEntry>? selected,
            out int firstSel)
    {
        selected = null;
        firstSel = 0;
        if (!GetSelectedArcDir(out archiveOrFileSystem, out DiskArcNode? _,
                out selectionDir)) {
            return false;
        }

        var listSel = FileList.Selection;
        if (listSel == null || listSel.Count == 0) {
            return false;
        }

        IEnumerable<FileListItem> itemsToProcess;
        FileListItem? singleSelItem = null;
        if (oneMeansAll && listSel.Count == 1) {
            singleSelItem = listSel[0];
            itemsToProcess = FileList.Items;
        } else {
            itemsToProcess = listSel;
        }

        selected = [];
        if (archiveOrFileSystem is IArchive) {
            foreach (FileListItem listItem in itemsToProcess) {
                if (omitDir && listItem.FileEntry.IsDirectory) {
                    continue;
                }
                selected.Add(listItem.FileEntry);
            }
        } else {
            var knownItems = new Dictionary<IFileEntry, IFileEntry>();
            foreach (FileListItem listItem in itemsToProcess) {
                if (!knownItems.ContainsKey(listItem.FileEntry)) {
                    knownItems.Add(listItem.FileEntry, listItem.FileEntry);
                }
            }
            foreach (FileListItem listItem in itemsToProcess) {
                IFileEntry entry = listItem.FileEntry;
                if (entry.IsDirectory) {
                    if (!omitDir) {
                        selected.Add(entry);
                        AddDirEntries(entry, knownItems, selected);
                    }
                } else {
                    selected.Add(entry);
                }
            }
            if (!omitDir) {
                selected = ShiftDirectories(selected);
            }
        }

        if (omitOpenArc || closeOpenArc) {
            ArchiveTreeItem? arcTreeSel = CachedArchiveTreeSelection;
            bool hasOpenChildren = arcTreeSel != null && arcTreeSel.Items.Count != 0;
            if (hasOpenChildren) {
                for (int i = 0; i < selected.Count; i++) {
                    IFileEntry entry = selected[i];
                    if (entry.IsDirectory) {
                        continue;
                    }
                    ArchiveTreeItem? treeItem =
                        ArchiveTreeItem.FindItemByEntry(ArchiveTree.TreeRoot, entry);
                    if (treeItem != null) {
                        if (omitOpenArc) {
                            selected.RemoveAt(i--);
                        } else if (closeOpenArc) {
                            CloseSubTree(treeItem);
                        }
                    }
                }
            }
        }

        if (singleSelItem != null) {
            for (int i = 0; i < selected.Count; i++) {
                if (selected[i] == singleSelItem.FileEntry) {
                    firstSel = i;
                    break;
                }
            }
        }
        return true;
    }

    private void AddDirEntries(IFileEntry entry,
            Dictionary<IFileEntry, IFileEntry> excludes, List<IFileEntry> list)
    {
        foreach (IFileEntry child in entry) {
            if (!excludes.ContainsKey(child)) {
                list.Add(child);
            }
            if (child.IsDirectory) {
                AddDirEntries(child, excludes, list);
            }
        }
    }

    private List<IFileEntry> ShiftDirectories(List<IFileEntry> entries)
    {
        var dirs = new List<IFileEntry>();
        var files = new List<IFileEntry>();
        foreach (IFileEntry e in entries) {
            if (e.IsDirectory) dirs.Add(e); else files.Add(e);
        }
        dirs.Sort((a, b) =>
            string.Compare(a.FullPathName, b.FullPathName, StringComparison.Ordinal));
        var result = new List<IFileEntry>(dirs.Count + files.Count);
        result.AddRange(dirs);
        result.AddRange(files);
        return result;
    }

    /// <summary>
    /// Performs pre-flight checks before add/paste/drop operations.
    /// </summary>
    internal bool CheckPasteDropOkay()
    {
        if (!CanWrite) {
            string msg = (mCurrentWorkObject is IFileSystem)
                ? "Can't add files to a read-only filesystem."
                : "Can't add files to a read-only archive.";
            _ = ShowMessageAsync(msg, "Unable to add");
            return false;
        }
        if (!IsMultiFileItemSelected) {
            _ = ShowMessageAsync("Can't add files to a single-file archive.", "Unable to add");
            return false;
        }
        return true;
    }

    /// <summary>
    /// Closes a subtree node, removing it from the archive tree.
    /// </summary>
    private void CloseSubTree(ArchiveTreeItem item)
    {
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
        parentItem.IsSelected = true;
    }

    /// <summary>
    /// Handles selection change in archive tree view.
    /// </summary>
    internal void OnArchiveTreeSelectionChanged(ArchiveTreeItem? newSel)
    {
        Debug.WriteLine("Archive tree selection now: " + (newSel == null ? "none" : newSel));
        if (newSel != null) {
            CachedArchiveTreeSelection = newSel;
        }
        if (newSel == null) {
            DirectoryTree.TreeRoot.Clear();
            mCurrentWorkObject = null;
            return;
        }

        DirectoryTree.TreeRoot.Clear();
        CenterInfo.ClearCenterInfo();

        mCurrentWorkObject = newSel.WorkTreeNode.DAObject;

        WorkTree? workTree = _workspaceService.WorkTree;
        Debug.Assert(workTree != null);
        ConfigureCenterInfo(mCurrentWorkObject, newSel.Name);
        if (newSel.WorkTreeNode == workTree.RootNode) {
            if (workTree.ReadWriteOpenFailure == null) {
                CenterInfo.CenterInfoText2 = string.Empty;
            } else {
                if (workTree.ReadWriteOpenFailure is IOException &&
                        (workTree.ReadWriteOpenFailure.HResult & 0xffff) == 32) {
                    CenterInfo.CenterInfoText2 = "Unable to open the file for writing, because " +
                        "it's being used by another process.";
                } else if (workTree.ReadWriteOpenFailure is UnauthorizedAccessException) {
                    CenterInfo.CenterInfoText2 = "Unable to open the file for writing, because " +
                        "it's marked read-only.";
                } else {
                    CenterInfo.CenterInfoText2 = "Unable to open the file for writing.";
                }
            }
        } else {
            CenterInfo.CenterInfoText2 = string.Empty;
        }

        if (mCurrentWorkObject is IFileSystem fs) {
            Debug.Assert(fs.GetVolDirEntry() != IFileEntry.NO_ENTRY);
            DirectoryTreeViewModel.PopulateTree(null, DirectoryTree.TreeRoot, fs.GetVolDirEntry());
            DirectoryTree.SubscribeAllSelectionChanges();
            _viewActions.SelectDirectoryTreeItem(DirectoryTree.TreeRoot[0]);
            _viewActions.ScrollDirectoryTreeToTop();
            CenterInfo.SetNotesList(fs.Notes);
        } else {
            string title = "Information";
            if (mCurrentWorkObject is IArchive arc) {
                title = "File Archive Entry List";
                CenterInfo.SetNotesList(arc.Notes);
            } else if (mCurrentWorkObject is IDiskImage disk) {
                title = "Disk Image Information";
                CenterInfo.SetNotesList(disk.Notes);
                CenterInfo.ShowDiskUtilityButtons = true;
                if (disk is IMetadata metadata) {
                    CenterInfo.SetMetadataList(metadata);
                }
            } else if (mCurrentWorkObject is IMultiPart parts) {
                title = "Multi-Partition Information";
                CenterInfo.SetNotesList(parts.Notes);
                CenterInfo.SetPartitionList(parts);
            } else if (mCurrentWorkObject is Partition) {
                title = "Partition Information";
                CenterInfo.ShowDiskUtilityButtons = true;
            }
            DirectoryTreeItem newItem = new DirectoryTreeItem(null, title, IFileEntry.NO_ENTRY);
            DirectoryTree.TreeRoot.Add(newItem);
            _viewActions.SelectDirectoryTreeItem(newItem);
        }

        _viewActions.ScrollFileListToTop();
        RefreshAllCommandStates();
    }

    /// <summary>
    /// Handles selection change in directory tree view.
    /// </summary>
    internal void OnDirectoryTreeSelectionChanged(DirectoryTreeItem? newSel)
    {
        Debug.WriteLine("Directory tree selection now: " + (newSel == null ? "none" : newSel));
        if (newSel != null) {
            CachedDirectoryTreeSelection = newSel;
        }
        if (newSel == null) {
            FileList.Items.Clear();
            return;
        }
        if (mSyncingSelection) {
            return;
        }

        if (mCurrentWorkObject is IArchive archive) {
            bool hasRsrc = archive.Characteristics.HasResourceForks;
            if (archive is Zip) {
                hasRsrc = _settingsService.GetBool(AppSettings.MAC_ZIP_ENABLED, true);
            }
            ConfigureCenterPanel(isInfoOnly: false, isArchive: true,
                isHierarchic: true, hasRsrc: hasRsrc, hasRaw: false);
            RefreshDirAndFileList(false);
        } else if (mCurrentWorkObject is IFileSystem system) {
            bool hasRsrc = system.Characteristics.HasResourceForks;
            bool isHier = system.Characteristics.IsHierarchical;
            ConfigureCenterPanel(isInfoOnly: false, isArchive: false,
                isHierarchic: isHier, hasRsrc: hasRsrc, hasRaw: mCurrentWorkObject is DOS);
            if (ShowSingleDirFileList) {
                RefreshDirAndFileList(false);
            } else {
                RefreshDirAndFileList(false);
                // In full-file-list mode, select and scroll to the directory entry
                // that corresponds to the chosen directory tree node.
                DirectoryTreeItem? newSel2 = newSel;   // capture for lambda clarity
                if (newSel2?.FileEntry != null) {
                    _viewActions.SelectFileListItemByEntry(newSel2.FileEntry);
                    FileListItem? dirItem =
                        FileListItem.FindItemByEntry(FileList.Items, newSel2.FileEntry);
                    if (dirItem != null) {
                        _viewActions.ScrollFileListItemIntoView(dirItem);
                    }
                }
            }
        } else {
            ConfigureCenterPanel(isInfoOnly: true, isArchive: false,
                isHierarchic: false, hasRsrc: false, hasRaw: false);
            StatusBar.ClearEntryCounts();
        }

        RefreshAllCommandStates();
    }

    /// <summary>
    /// When the user selects a file in the full file list, sync the directory tree
    /// to show the containing directory of the selected file.
    /// </summary>
    internal void SyncDirectoryTreeToFileSelection()
    {
        if (mSyncingSelection || ShowSingleDirFileList ||
                mCurrentWorkObject is not IFileSystem) {
            return;
        }

        FileListItem? selItem = FileList.SelectedItem;
        if (selItem == null) {
            return;
        }

        IFileEntry entry = selItem.FileEntry;
        IFileEntry targetDir = entry.IsDirectory ? entry : entry.ContainingDir;
        if (targetDir == IFileEntry.NO_ENTRY) {
            return;
        }

        DirectoryTreeItem? curDirItem = CachedDirectoryTreeSelection;
        if (curDirItem != null && curDirItem.FileEntry == targetDir) {
            return;
        }

        mSyncingSelection = true;
        try {
            bool found = _viewActions.SelectDirectoryTreeItemByEntry(
                DirectoryTree.TreeRoot, targetDir);
            if (found) {
                // CachedDirectoryTreeSelection will be updated via OnDirectoryTreeSelectionChanged
            }
        } finally {
            mSyncingSelection = false;
        }

        RefreshAllCommandStates();
    }

    private void AddInfoItem(string name, string value)
    {
        CenterInfo.CenterInfoList.Add(new CenterInfoItem(name + ":", value));
    }

    private void ConfigureCenterInfo(object workObj, string selName)
    {
        CenterInfo.CenterInfoList.Clear();
        string infoText;
        if (workObj is IArchive arc) {
            infoText = "File archive - " + ThingString.IArchive(arc) + " - " + selName;
            AddInfoItem("Entries", arc.Count.ToString());
        } else if (workObj is IDiskImage disk) {
            infoText = "Disk image - " + ThingString.IDiskImage(disk) + " - " + selName;
            IChunkAccess? chunks = disk.ChunkAccess;
            if (chunks != null) {
                AddInfoItem("Total size",
                    _workspaceService.Formatter.FormatSizeOnDisk(
                        chunks.FormattedLength, KBLOCK_SIZE));
                if (chunks.HasSectors) {
                    AddInfoItem("Geometry",
                        chunks.NumTracks.ToString() + " tracks, " +
                        chunks.NumSectorsPerTrack.ToString() + " sectors");
                    if (chunks.NumSectorsPerTrack == 16) {
                        AddInfoItem("File order",
                            ThingString.SectorOrder(chunks.FileOrder));
                    }
                }
                if (chunks.NibbleCodec != null) {
                    AddInfoItem("Nibble codec", chunks.NibbleCodec.Name);
                }
            }
        } else if (workObj is IFileSystem fs) {
            infoText = "Filesystem - " + ThingString.IFileSystem(fs) + " - " + selName;
            IChunkAccess chunks = fs.RawAccess;
            AddInfoItem("Volume size",
                _workspaceService.Formatter.FormatSizeOnDisk(
                    chunks.FormattedLength, KBLOCK_SIZE));
        } else if (workObj is IMultiPart partitions) {
            infoText = "Multi-partition format - " + ThingString.IMultiPart(partitions) +
                       " - " + selName;
            AddInfoItem("Partition count", partitions.Count.ToString());
        } else if (workObj is Partition part) {
            infoText = "Disk partition - " + ThingString.Partition(part) + " - " + selName;
            AddInfoItem("Start block", (part.StartOffset / BLOCK_SIZE).ToString());
            AddInfoItem("Block count", (part.Length / BLOCK_SIZE).ToString() + " (" +
                (part.Length / (1024 * 1024.0)).ToString("N1") + " MB)");
        } else {
            infoText = "???";
        }
        CenterInfo.CenterInfoText1 = infoText;
    }

    // =========================================================================
    // Step 3G — UI Population Methods
    // =========================================================================

    /// <summary>
    /// Populates the archive tree from the workspace's WorkTree.
    /// </summary>
    internal bool PopulateArchiveTree()
    {
        WorkTree? workTree = _workspaceService.WorkTree;
        Debug.Assert(workTree != null);

        ArchiveTree.TreeRoot.Clear();
        _workspaceService.AppHook.LogI("Constructing archive trees...");
        DateTime startWhen = DateTime.Now;

        ArchiveTreeItem.ConstructTree(ArchiveTree.TreeRoot, workTree.RootNode);
        ArchiveTree.SubscribeAllSelectionChanges();

        _workspaceService.AppHook.LogI("Finished archive tree construction in " +
            (DateTime.Now - startWhen).TotalMilliseconds + " ms");

        _viewActions.SelectBestArchiveTreeItem(ArchiveTree.TreeRoot[0]);
        return true;
    }

    // PopulateDirectoryTree → DirectoryTreeViewModel.PopulateTree (moved)

    /// <summary>
    /// Refreshes the contents of the directory tree and file list.
    /// </summary>
    internal void RefreshDirAndFileList(bool focusOnFileList = true)
    {
        if (focusOnFileList) {
            FileList.RequestFocusAfterPopulate();
        }
        if (mCurrentWorkObject == null) {
            return;
        }

        FileListItem? selectedItem = FileList.SelectedItem;
        IFileEntry selFileEntry = IFileEntry.NO_ENTRY;
        if (selectedItem != null) {
            if (selectedItem.FileEntry.IsValid) {
                selFileEntry = selectedItem.FileEntry;
            } else {
                Debug.WriteLine("Refresh: selected file entry is no longer valid");
            }
        } else {
            Debug.WriteLine("Refresh: no file list sel");
        }

        if (mCurrentWorkObject is IFileSystem fs) {
            IFileEntry curSel = IFileEntry.NO_ENTRY;
            DirectoryTreeItem? dirTreeSel = CachedDirectoryTreeSelection;
            if (dirTreeSel != null) {
                if (dirTreeSel.FileEntry.IsValid) {
                    curSel = dirTreeSel.FileEntry;
                } else {
                    Debug.WriteLine("Refresh: selected dir entry is no longer valid");
                }
            } else {
                Debug.WriteLine("Refresh: no dir tree sel");
            }

            ObservableCollection<DirectoryTreeItem> rootList = DirectoryTree.TreeRoot;
            IFileEntry volDir = fs.GetVolDirEntry();
            if (!DirectoryTreeViewModel.VerifyTree(rootList, volDir, 0)) {
                Debug.WriteLine("Re-populate directory tree");
                rootList.Clear();
                DirectoryTreeViewModel.PopulateTree(null, rootList, volDir);
                DirectoryTree.SubscribeAllSelectionChanges();

                if (curSel == IFileEntry.NO_ENTRY ||
                        !_viewActions.SelectDirectoryTreeItemByEntry(
                            DirectoryTree.TreeRoot, curSel)) {
                    _viewActions.SelectDirectoryTreeItem(rootList[0]);
                }
            } else {
                Debug.WriteLine("Not repopulating directory tree");
            }
        }

        if (!FileList.VerifyForObject(mCurrentWorkObject, CachedDirectoryTreeSelection,
                ShowSingleDirFileList)) {
            PopulateFileList(selFileEntry, focusOnFileList);
        } else {
            FileListItem? item = FileListItem.FindItemByEntry(FileList.Items, selFileEntry);
            if (item != null) {
                FileList.SelectedItem = item;
            }
            Debug.WriteLine("Not repopulating file list");
        }
        if (FileList.HasFocusRequest) {
            Debug.WriteLine("++ focus to file list requested");
            _viewActions.FocusFileList();
            FileList.ConsumeFocusRequest();
        }

        RefreshActiveFileViewer();
    }

    private FileViewerViewModel? GetActiveFileViewer() =>
        mActiveViewerWindow?.DataContext as FileViewerViewModel
            ?? (_viewerService.ActiveViewers.Count > 0 ? _viewerService.ActiveViewers[0] : null);

    private void ActivateFileViewerWindow()
    {
        if (mActiveViewerWindow == null) {
            return;
        }
        if (mActiveViewerWindow.WindowState == WindowState.Minimized) {
            mActiveViewerWindow.WindowState = WindowState.Normal;
        }
        mActiveViewerWindow.Activate();
        mActiveViewerWindow.Focus();
    }

    private void CaptureActiveFileViewerState()
    {
        if (mCurrentWorkObject == null) return;
        foreach (var viewer in _viewerService.ActiveViewers.ToList()) {
            if (viewer.IsSourceObject(mCurrentWorkObject)) {
                viewer.CaptureCurrentStatePaths();
            }
        }
    }

    private void RefreshActiveFileViewer()
    {
        if (mCurrentWorkObject == null) return;
        foreach (var viewer in _viewerService.ActiveViewers.ToList()) {
            if (!viewer.IsSourceObject(mCurrentWorkObject)) continue;
            switch (viewer.ReconcileAfterRefresh(mCurrentWorkObject)) {
                case FileViewerViewModel.ReconcileResult.NoChange:
                    break;
                case FileViewerViewModel.ReconcileResult.Reverted:
                    _ = RevertUnavailableFileViewerAsync(viewer);
                    break;
                case FileViewerViewModel.ReconcileResult.CannotLoad:
                    _ = CannotLoadFileViewerAsync(viewer);
                    break;
                default:
                    _ = CloseUnavailableFileViewerAsync(viewer);
                    break;
            }
        }
    }

    private void HandleWorkspaceViewerInvalidation()
    {
        foreach (var viewer in _viewerService.ActiveViewers.ToList()) {
            if (viewer.SourceWorkPathName == _workspaceService.WorkPathName) {
                _ = CloseUnavailableFileViewerAsync(viewer);
            }
        }
    }

    /// <summary>
    /// After a sector/block editor session, try to reload any open file viewers using
    /// the freshly-reparsed filesystem objects from the disk's work tree node.
    /// Falls back to the existing close behavior for viewers whose files can't be found.
    /// </summary>
    private void ReloadOrCloseViewersAfterSectorEdit(WorkTree.Node diskNode)
    {
        // Collect the new filesystem/archive objects from the reprocessed disk.
        var newSources = new List<object>();
        foreach (WorkTree.Node childNode in diskNode)
        {
            if (childNode.DAObject is IFileSystem or IArchive)
                newSources.Add(childNode.DAObject);
        }

        foreach (var viewer in _viewerService.ActiveViewers.ToList())
        {
            if (viewer.SourceWorkPathName != _workspaceService.WorkPathName) continue;

            bool reloaded = newSources.Any(src => viewer.TryReloadWithNewSource(src));
            if (!reloaded)
            {
                _ = CloseUnavailableFileViewerAsync(viewer);
            }
        }
    }

    private async Task CloseUnavailableFileViewerAsync(FileViewerViewModel viewer)
    {
        if (viewer.IsUnavailableCloseDialogSuppressed()) {
            Debug.WriteLine("Suppressed: unavailable-close dialog");
        } else {
            (Services.MBResult _, bool suppress) = await mDialogService.ShowSuppressibleMessageAsync(
                "This file is unavailable. Viewer will close",
                "File Viewer",
                "Don't show this message again",
                Services.MBButton.OK,
                Services.MBIcon.Warning);
            if (suppress) {
                viewer.SetUnavailableCloseDialogSuppressed(true);
            }
        }

        if (_viewerService.ActiveViewers.Any(v => ReferenceEquals(v, viewer))) {
            viewer.RequestClose();
        }
    }

    private async Task CannotLoadFileViewerAsync(FileViewerViewModel viewer)
    {
        // Non-suppressible — an unexpected load failure after a fallback was found.
        await mDialogService.ShowMessageAsync(
            "Cannot load file. Viewer will close",
            "File Viewer",
            Services.MBButton.OK,
            Services.MBIcon.Error);

        if (_viewerService.ActiveViewers.Any(v => ReferenceEquals(v, viewer))) {
            viewer.RequestClose();
        }
    }

    private async Task RevertUnavailableFileViewerAsync(FileViewerViewModel viewer)
    {
        if (viewer.IsUnavailableRevertDialogSuppressed()) {
            Debug.WriteLine("Suppressed: unavailable-revert dialog");
        } else {
            (Services.MBResult _, bool suppress) = await mDialogService.ShowSuppressibleMessageAsync(
                "File is unavailable. Reverting to previous file",
                "File Viewer",
                "Don't show this message again",
                Services.MBButton.OK,
                Services.MBIcon.Warning);
            if (suppress) {
                viewer.SetUnavailableRevertDialogSuppressed(true);
            }
        }
    }

    /// <summary>
    /// Wrapper — delegates to FileList.PopulateFileList with current context.
    /// </summary>
    internal void PopulateFileList(IFileEntry selEntry, bool focusOnFileList)
    {
        if (mCurrentWorkObject == null) {
            return;
        }
        FileList.PopulateFileList(
            mCurrentWorkObject,
            CachedDirectoryTreeSelection,
            selEntry,
            focusOnFileList,
            ShowSingleDirFileList,
            _workspaceService.Formatter);

        StatusBar.SetEntryCounts(mCurrentWorkObject as IFileSystem,
            FileList.LastDirCount, FileList.LastFileCount, _workspaceService.Formatter);
        RefreshAllCommandStates();
    }

    // PopulateEntriesFromArchive/SingleDir/FullDisk → FileListViewModel (moved)
    // SetEntryCounts → StatusBarViewModel.SetEntryCounts (moved)
    // VerifyDirectoryTree → DirectoryTreeViewModel.VerifyTree (moved)
    // VerifyFileList overloads → FileListViewModel (moved)

    /// <summary>
    /// Scans the currently selected archive for entries that look like disk images or
    /// file archives but have not yet been opened as sub-volumes in the work tree.
    /// </summary>
    internal void TryOpenNewSubVolumes()
    {
        ArchiveTreeItem? arcTreeSel = CachedArchiveTreeSelection;
        WorkTree? workTree = _workspaceService.WorkTree;
        if (arcTreeSel == null || workTree == null) {
            return;
        }
        IArchive? arc = arcTreeSel.WorkTreeNode.DAObject as IArchive;
        if (arc == null) {
            return;
        }

        bool addedAny = false;
        foreach (IFileEntry entry in arc) {
            if (entry.IsDirectory) {
                continue;
            }
            if (ArchiveTreeItem.FindItemByEntry(ArchiveTree.TreeRoot, entry) != null) {
                continue;
            }
            try {
                WorkTree.Node? newNode =
                    workTree.TryCreateSub(arcTreeSel.WorkTreeNode, entry);
                if (newNode != null) {
                    ArchiveTreeItem.ConstructTree(arcTreeSel, newNode);
                    addedAny = true;
                }
            } catch (Exception ex) {
                AppLog.W("Open sub-volumes: failed on '" + entry.FullPathName + "'", ex);
            }
        }

        if (addedAny) {
            arcTreeSel.IsExpanded = true;
        }
    }

}
