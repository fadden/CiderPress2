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

namespace cp2_avalonia.Services;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;
using AppCommon;
using CommonUtil;
using DiskArc;
using Actions;
using Models;

public class WorkspaceService : IWorkspaceService {
    private const int MAX_RECENT_FILES = 6;

    private readonly ISettingsService _settings;

    public WorkTree? WorkTree { get; private set; }
    public bool IsFileOpen => WorkTree != null;
    public string WorkPathName { get; private set; } = string.Empty;

    public Formatter Formatter { get; private set; }
    public AppHook AppHook { get; }
    public ObservableCollection<string> RecentFilePaths { get; } = new();
    public DebugMessageLog DebugLog { get; }

    // Explicit IReadOnlyList implementation for interface
    IReadOnlyList<string> IWorkspaceService.RecentFilePaths => RecentFilePaths;

    public WorkspaceService(ISettingsService settings) {
        _settings = settings;
        DebugLog = new DebugMessageLog();
        // Retain everything (including Verbose) at the storage layer; the debug log
        // dialog applies its own display-priority filter on top of this.
        DebugLog.SetMinPriority(MessageLog.Priority.Verbose);
        AppHook = new AppHook(DebugLog);
        // Make the shared log available to the application-wide logging facade, so
        // errors and exceptions detected anywhere can be surfaced in the debug log
        // dialog (DEBUG > Show Debug Log).
        AppLog.Initialize(DebugLog);
        Formatter = new Formatter(new Formatter.FormatConfig());
    }

    public async Task<WorkTree> OpenAsync(string path, bool readOnly, AutoOpenDepth depth) {
        Debug.Assert(WorkTree == null);

        bool Limiter(WorkTree.DepthParentKind parentKind, WorkTree.DepthChildKind childKind) => DepthLimit(parentKind, childKind, depth);

        AppHook.LogI("Opening work file '" + path + "' readOnly=" + readOnly);

        OpenProgress prog = new OpenProgress(path, Limiter, readOnly, AppHook);

        bool success = await Task.Run(() => {
            var bw = new BackgroundWorker() { WorkerReportsProgress = true };
            object result = prog.DoWork(bw);
            return prog.RunWorkerCompleted(result);
        });

        if (!success) {
            if (prog.Results.mException != null) {
                throw prog.Results.mException;
            }
            return null!;
        }

        Debug.Assert(prog.Results.mWorkTree != null);
        WorkTree = prog.Results.mWorkTree;
        WorkPathName = path;
        UpdateRecentFilesList(path);
        AppHook.LogI("Load of '" + path + "' completed");
        return WorkTree;
    }

    public bool Close() {
        if (WorkTree == null) {
            return true;
        }
        Debug.WriteLine("WorkspaceService closing " + WorkPathName);
        FlushAllFileSystems(WorkTree.RootNode);
        WorkTree.Dispose();
        WorkTree = null;
        WorkPathName = string.Empty;
        return true;
    }

    /// <summary>
    /// Recursively walks the WorkTree node hierarchy and calls Flush() on every IFileSystem
    /// node. This ensures pending file entry changes are saved before Dispose() runs, avoiding
    /// a Debug.Assert in ProDOS.InvalidateFileEntries when IsChangePending is true.
    /// </summary>
    private static void FlushAllFileSystems(WorkTree.Node node) {
        if (node.DAObject is IFileSystem fs) {
            try {
                fs.Flush();
            } catch (Exception ex) {
                AppLog.E("Flush failed for '" + node.Label + "'", ex);
            }
        }
        foreach (WorkTree.Node child in node) {
            FlushAllFileSystems(child);
        }
    }

    public void UnpackRecentFileList() {
        RecentFilePaths.Clear();
        string cereal = _settings.GetString(AppSettings.RECENT_FILES_LIST, "");
        if (string.IsNullOrEmpty(cereal)) {
            return;
        }
        try {
            object? parsed = JsonSerializer.Deserialize<List<string>>(cereal);
            if (parsed != null) {
                foreach (string s in (List<string>)parsed) {
                    RecentFilePaths.Add(s);
                }
            }
        } catch (Exception ex) {
            AppLog.W("Failed deserializing recent files list", ex);
        }
    }

    private void UpdateRecentFilesList(string pathName) {
        if (string.IsNullOrEmpty(pathName)) {
            return;
        }
        int index = RecentFilePaths.IndexOf(pathName);
        if (index == 0) {
            return;
        }
        if (index > 0) {
            RecentFilePaths.RemoveAt(index);
        }
        RecentFilePaths.Insert(0, pathName);
        while (RecentFilePaths.Count > MAX_RECENT_FILES) {
            RecentFilePaths.RemoveAt(MAX_RECENT_FILES);
        }
        string cereal = JsonSerializer.Serialize<IEnumerable<string>>(RecentFilePaths);
        _settings.SetString(AppSettings.RECENT_FILES_LIST, cereal);
    }

    private static bool DepthLimit(WorkTree.DepthParentKind _,
            WorkTree.DepthChildKind childKind, AutoOpenDepth depth) {
        if (depth == AutoOpenDepth.Max) {
            return true;
        }
        if (depth == AutoOpenDepth.Shallow) {
            return false;
        }
        Debug.Assert(depth == AutoOpenDepth.SubVol);
        return childKind != WorkTree.DepthChildKind.FileArchive;
    }
}
