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

using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;


namespace cp2_avalonia.Services;

public interface IFilePickerService
{
    /// <summary>Opens a file picker for a single file. Returns the path, or null if canceled.</summary>
    Task<string?> OpenFileAsync(string title,
        IReadOnlyList<FilePickerFileType>? fileTypes = null,
        string? initialDir = null);

    /// <summary>Opens a file picker for multiple files. Returns paths, or empty list if canceled.</summary>
    Task<IReadOnlyList<string>> OpenFilesAsync(string title,
        IReadOnlyList<FilePickerFileType>? fileTypes = null,
        string? initialDir = null);

    /// <summary>Opens a save-file dialog. Returns the chosen path, or null if canceled.</summary>
    Task<string?> SaveFileAsync(string title,
        string? suggestedName = null,
        IReadOnlyList<FilePickerFileType>? fileTypes = null,
        string? initialDir = null);

    /// <summary>Opens a folder picker. Returns the chosen path, or null if canceled.</summary>
    Task<string?> OpenFolderAsync(string title, string? initialDir = null);
}