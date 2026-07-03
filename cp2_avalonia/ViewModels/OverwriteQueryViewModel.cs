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
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using AppCommon;
using CommonUtil;

namespace cp2_avalonia.ViewModels;

/// <summary>
/// ViewModel for the Overwrite Query dialog.
/// </summary>
public class OverwriteQueryViewModel : ObservableObject
{
    public event Action<bool>? CloseRequested;

    public IRelayCommand OverwriteCommand { get; }
    public IRelayCommand SkipCommand { get; }
    public IRelayCommand CancelCommand { get; }

    // ── Output properties (read by caller after OK) ───────────────────────────
    /// <summary>User's choice: Overwrite or Skip.</summary>
    public CallbackFacts.Results Result { get; private set; }

    private bool mUseForAll;
    public bool UseForAll {
        get => mUseForAll;
        set => SetProperty(ref mUseForAll, value);
    }

    // ── Display properties ────────────────────────────────────────────────────
    public string NewFileName { get; }
    public string NewDirName { get; }
    public string NewModWhen { get; }
    public string ExistFileName { get; }
    public string ExistDirName { get; }
    public string ExistModWhen { get; }

    public OverwriteQueryViewModel(CallbackFacts facts)
    {
        NewFileName = PathName.GetFileName(facts.NewPathName, facts.NewDirSep);
        string ndn = PathName.GetDirectoryName(facts.NewPathName, facts.NewDirSep);
        NewDirName = string.IsNullOrEmpty(ndn) ? string.Empty : "Directory: " + ndn;
        NewModWhen = TimeStamp.IsValidDate(facts.NewModWhen)
            ? "Modified: " + facts.NewModWhen.ToString(CultureInfo.CurrentCulture)
            : "Modified: (unknown)";

        ExistFileName = PathName.GetFileName(facts.OrigPathName, facts.OrigDirSep);
        string edn = PathName.GetDirectoryName(facts.OrigPathName, facts.OrigDirSep);
        ExistDirName = string.IsNullOrEmpty(edn) ? string.Empty : "Directory: " + edn;
        ExistModWhen = TimeStamp.IsValidDate(facts.OrigModWhen)
            ? "Modified: " + facts.OrigModWhen.ToString(CultureInfo.CurrentCulture)
            : "Modified: (unknown)";

        OverwriteCommand = new AsyncRelayCommand(async () => {
            Result = CallbackFacts.Results.Overwrite;
            CloseRequested?.Invoke(true);
        });
        SkipCommand = new AsyncRelayCommand(async () => {
            Result = CallbackFacts.Results.Skip;
            CloseRequested?.Invoke(true);
        });
        CancelCommand = new AsyncRelayCommand(async () =>
            CloseRequested?.Invoke(false));
    }
}
