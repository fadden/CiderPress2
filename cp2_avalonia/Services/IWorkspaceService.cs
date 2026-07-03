/*
 * Copyright 2023 faddenSoft
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

using System.Collections.Generic;
using System.Threading.Tasks;
using AppCommon;
using CommonUtil;
using cp2_avalonia.Models;

namespace cp2_avalonia.Services;

public interface IWorkspaceService
{
    /// <summary>The open WorkTree, or null if no file is open.</summary>
    WorkTree? WorkTree { get; }

    /// <summary>True if a work file is currently open.</summary>
    bool IsFileOpen { get; }

    /// <summary>Full path of the currently open file, or empty string.</summary>
    string WorkPathName { get; }

    /// <summary>Application hook for logging and library configuration.</summary>
    AppHook AppHook { get; }

    /// <summary>Formatter for display strings (file sizes, dates, etc.).</summary>
    Formatter Formatter { get; }

    /// <summary>Debug message log.</summary>
    DebugMessageLog DebugLog { get; }

    /// <summary>Recent file paths, most-recent first.</summary>
    IReadOnlyList<string> RecentFilePaths { get; }

    /// <summary>
    /// Loads the recent file list from settings.
    /// </summary>
    void UnpackRecentFileList();

    /// <summary>
    /// Opens a work file, closing any currently open file first.
    /// Returns the opened WorkTree on success, or throws on failure.
    /// </summary>
    Task<WorkTree> OpenAsync(string path, bool readOnly, AutoOpenDepth depth);

    /// <summary>
    /// Closes the current work file.
    /// Returns false if the operation is canceled (e.g. user declines save prompt).
    /// </summary>
    bool Close();
}