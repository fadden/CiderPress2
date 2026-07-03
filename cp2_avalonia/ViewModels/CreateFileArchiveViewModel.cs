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
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using static DiskArc.Defs;
using cp2_avalonia.Services;

namespace cp2_avalonia.ViewModels;

/// <summary>
/// ViewModel for the Create File Archive dialog.
/// </summary>
public class CreateFileArchiveViewModel : ObservableObject
{
    public event Action<bool>? CloseRequested;

    public IRelayCommand OkCommand { get; }
    public IRelayCommand CancelCommand { get; }

    // ── Output property ───────────────────────────────────────────────────────
    /// <summary>Selected archive format.  Read by caller after OK.</summary>
    private FileKind mKind;
    public FileKind Kind {
        get => mKind;
        private set => SetProperty(ref mKind, value);
    }

    // ── Radio button bindings ─────────────────────────────────────────────────
    public bool IsChecked_Binary2 {
        get => Kind == FileKind.Binary2;
        set { if (value) { Kind = FileKind.Binary2; } }
    }
    public bool IsChecked_NuFX {
        get => Kind == FileKind.NuFX;
        set { if (value) { Kind = FileKind.NuFX; } }
    }
    public bool IsChecked_Zip {
        get => Kind == FileKind.Zip;
        set { if (value) { Kind = FileKind.Zip; } }
    }

    public CreateFileArchiveViewModel(ISettingsService settingsService)
    {
        var settingsService1 = settingsService;

        // Restore last selection.
        FileKind kind = settingsService1.GetEnum(AppSettings.NEW_ARC_MODE, FileKind.NuFX);
        mKind = kind switch {
            FileKind.Binary2 or FileKind.Zip or FileKind.NuFX => kind,
            _ => FileKind.NuFX
        };

        OkCommand = new AsyncRelayCommand(async () => {
            settingsService1.SetEnum(AppSettings.NEW_ARC_MODE, Kind);
            CloseRequested?.Invoke(true);
        });
        CancelCommand = new AsyncRelayCommand(async () =>
            CloseRequested?.Invoke(false));
    }
}
