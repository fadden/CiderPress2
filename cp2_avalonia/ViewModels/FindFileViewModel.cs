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

using cp2_avalonia.Models;

namespace cp2_avalonia.ViewModels;

/// <summary>
/// ViewModel for the inline Find File panel embedded in the main window's left column.
/// The panel is hidden until <see cref="Show"/> is called; it hides itself via
/// <see cref="HideCommand"/>.  <see cref="FindRequested"/> fires for every Find Next /
/// Find Prev press while the panel is visible.
/// </summary>
public class FindFileViewModel : ObservableObject
{
    // Persists across invocations of the panel.
    private static string sLastSearch = string.Empty;
    private static bool sCurrentArchiveOnly;

    // ── Event fired on each find request ─────────────────────────────────────
    /// <summary>Subscribe to receive search requests while the panel is visible.</summary>
    public event Action<FindFileReq>? FindRequested;

    // ── Commands ──────────────────────────────────────────────────────────────
    public IRelayCommand FindNextCommand { get; }
    public IRelayCommand FindPrevCommand { get; }
    public IRelayCommand HideCommand { get; }

    // ── Bindable properties ───────────────────────────────────────────────────
    private string mFileName;
    public string FileName {
        get => mFileName;
        set { SetProperty(ref mFileName, value); UpdateControls(); }
    }

    private bool mIsCurrentArchiveOnlyChecked;
    public bool IsCurrentArchiveOnlyChecked {
        get => mIsCurrentArchiveOnlyChecked;
        set => SetProperty(ref mIsCurrentArchiveOnlyChecked, value);
    }

    private bool mIsEnabled_PrevNext;
    public bool IsEnabled_PrevNext {
        get => mIsEnabled_PrevNext;
        private set { if (SetProperty(ref mIsEnabled_PrevNext, value)) { FindNextCommand?.NotifyCanExecuteChanged(); FindPrevCommand?.NotifyCanExecuteChanged(); } }
    }

    private bool mIsFindPanelVisible;
    public bool IsFindPanelVisible {
        get => mIsFindPanelVisible;
        private set => SetProperty(ref mIsFindPanelVisible, value);
    }

    private bool CanFindPrevNext() => IsEnabled_PrevNext;

    public FindFileViewModel()
    {
        mFileName = sLastSearch;
        mIsCurrentArchiveOnlyChecked = sCurrentArchiveOnly;

        FindNextCommand = new RelayCommand(() => {
            SaveConfig();
            FindRequested?.Invoke(new FindFileReq(mFileName, mIsCurrentArchiveOnlyChecked,
                forward: true));
        }, CanFindPrevNext);
        FindPrevCommand = new RelayCommand(() => {
            SaveConfig();
            FindRequested?.Invoke(new FindFileReq(mFileName, mIsCurrentArchiveOnlyChecked,
                forward: false));
        }, CanFindPrevNext);
        HideCommand = new RelayCommand(() => { IsFindPanelVisible = false; });

        UpdateControls();
    }

    /// <summary>
    /// Makes the find panel visible.  Call this from MainViewModel when the Find command fires.
    /// </summary>
    public void Show()
    {
        IsFindPanelVisible = true;
    }

    private void SaveConfig()
    {
        sLastSearch = mFileName;
        sCurrentArchiveOnly = mIsCurrentArchiveOnlyChecked;
    }

    private void UpdateControls()
    {
        IsEnabled_PrevNext = !string.IsNullOrEmpty(mFileName);
    }
}
