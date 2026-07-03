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
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace cp2_avalonia.Services;

public class FilePickerService(IDialogHost host) : IFilePickerService
{
    private IStorageProvider GetProvider() =>
        TopLevel.GetTopLevel(host.GetOwnerWindow())!.StorageProvider;

    private async Task<IStorageFolder?> ResolveStartDir(string? initialDir)
    {
        if (string.IsNullOrEmpty(initialDir)) return null;
        try { return await GetProvider().TryGetFolderFromPathAsync(initialDir); }
        catch (Exception ex) {
            AppLog.W("File picker: couldn't resolve initial directory '" + initialDir + "'", ex);
            return null;
        }
    }

    public async Task<string?> OpenFileAsync(string title,
            IReadOnlyList<FilePickerFileType>? fileTypes = null,
            string? initialDir = null)
    {
        var results = await GetProvider().OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = fileTypes,
            SuggestedStartLocation = await ResolveStartDir(initialDir)
        });
        return results.Count > 0 ? results[0].Path.LocalPath : null;
    }

    public async Task<IReadOnlyList<string>> OpenFilesAsync(string title,
            IReadOnlyList<FilePickerFileType>? fileTypes = null,
            string? initialDir = null)
    {
        var results = await GetProvider().OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = true,
            FileTypeFilter = fileTypes,
            SuggestedStartLocation = await ResolveStartDir(initialDir)
        });
        var paths = new List<string>(results.Count);
        foreach (var r in results) paths.Add(r.Path.LocalPath);
        return paths;
    }

    public async Task<string?> SaveFileAsync(string title,
            string? suggestedName = null,
            IReadOnlyList<FilePickerFileType>? fileTypes = null,
            string? initialDir = null)
    {
        var result = await GetProvider().SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            FileTypeChoices = fileTypes,
            SuggestedStartLocation = await ResolveStartDir(initialDir)
        });
        return result?.Path.LocalPath;
    }

    public async Task<string?> OpenFolderAsync(string title, string? initialDir = null)
    {
        var results = await GetProvider().OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = await ResolveStartDir(initialDir)
        });
        return results.Count > 0 ? results[0].Path.LocalPath : null;
    }
}