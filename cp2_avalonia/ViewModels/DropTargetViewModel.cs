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
/// ViewModel for the drag/paste target test window (always modeless).
/// </summary>
public class DropTargetViewModel : ObservableObject
{
    // ── Modeless lifecycle ────────────────────────────────────────────────────
    public event Action? Closed;

    /// <summary>Fires when the user clicks Paste; code-behind handles clipboard access.</summary>
    public event Action? PasteRequested;

    public IRelayCommand CloseCommand { get; }
    public IRelayCommand PasteCommand { get; }

    // ── Bindable text area ────────────────────────────────────────────────────
    private string mTextArea = "Drag or paste stuff here.\n";
    public string TextArea {
        get => mTextArea;
        set => SetProperty(ref mTextArea, value);
    }

    public DropTargetViewModel()
    {
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke());
        PasteCommand = new RelayCommand(() => PasteRequested?.Invoke());
    }

    /// <summary>
    /// Called by code-behind when the window is closed.
    /// </summary>
    public event Action? CloseRequested;
    public void RequestClose() => CloseRequested?.Invoke();

    public void OnWindowClosed()
    {
        Closed?.Invoke();
    }
}
