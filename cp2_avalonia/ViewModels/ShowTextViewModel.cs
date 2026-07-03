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

namespace cp2_avalonia.ViewModels;

/// <summary>
/// ViewModel for the ShowText dialog.  Can be shown modal or modeless.
/// </summary>
public class ShowTextViewModel : ObservableObject
{
    /// <summary>
    /// Text displayed in the window.
    /// </summary>
    public string DisplayText { get; }

    /// <summary>
    /// Title shown in the window's title bar.
    /// </summary>
    public string WindowTitle { get; }

    /// <summary>
    /// Signals the View to close.  Provides the dialog result (always true for Close).
    /// </summary>
    public event Action<bool>? CloseRequested;

    // ── Modeless close ────────────────────────────────────────────────────────
    /// <summary>
    /// Used by external callers (e.g. MainViewModel) to programmatically close the window.
    /// The View registers a handler that calls Close() when this fires.
    /// </summary>
    public event Action? CloseRequested_Modeless;

    public IRelayCommand CloseCommand { get; }

    public void RequestClose() => CloseRequested_Modeless?.Invoke();

    public ShowTextViewModel(string displayText, string title = "CiderPress II")
    {
        DisplayText = displayText;
        WindowTitle = title;

        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke(true));
    }
}
