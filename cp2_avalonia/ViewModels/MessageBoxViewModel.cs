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

using cp2_avalonia.Services;

namespace cp2_avalonia.ViewModels;

/// <summary>
/// ViewModel for the message box dialog.
/// </summary>
public class MessageBoxViewModel : ObservableObject
{
    // ── Bound properties ──────────────────────────────────────────────────────
    public string Message { get; }
    public string Caption { get; }

    // ── Icon visibility ───────────────────────────────────────────────────────
    public MBIcon IconKind { get; }
    public bool ShowIcon      => IconKind != MBIcon.None;
    public bool IsInfoIcon    => IconKind == MBIcon.Information;
    public bool IsWarningIcon => IconKind == MBIcon.Warning;
    public bool IsErrorIcon   => IconKind == MBIcon.Error;
    public bool IsQuestionIcon => IconKind == MBIcon.Question;

    // ── Button visibility ─────────────────────────────────────────────────────
    public bool ShowOkButton     { get; }
    public bool ShowCancelButton { get; }
    public bool ShowYesButton    { get; }
    public bool ShowNoButton     { get; }

    // ── Output ────────────────────────────────────────────────────────────────
    /// <summary>Which button was clicked. Defaults to Cancel (window-close behavior).</summary>
    public MBResult Result { get; private set; } = MBResult.Cancel;

    // ── Interactions ──────────────────────────────────────────────────────────
    public event Action<bool>? CloseRequested;

    // ── Commands ──────────────────────────────────────────────────────────────
    public IRelayCommand OkCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand YesCommand { get; }
    public IRelayCommand NoCommand { get; }

    public MessageBoxViewModel(string message, string caption, MBButton buttons, MBIcon icon)
    {
        Message = message;
        Caption = caption;
        IconKind = icon;

        ShowOkButton     = buttons == MBButton.OK || buttons == MBButton.OKCancel;
        ShowCancelButton = buttons == MBButton.OKCancel || buttons == MBButton.YesNoCancel;
        ShowYesButton    = buttons == MBButton.YesNo || buttons == MBButton.YesNoCancel;
        ShowNoButton     = buttons == MBButton.YesNo || buttons == MBButton.YesNoCancel;

        OkCommand = new AsyncRelayCommand(async () => {
            Result = MBResult.OK;
            CloseRequested?.Invoke(true);
        });
        CancelCommand = new AsyncRelayCommand(async () => {
            Result = MBResult.Cancel;
            CloseRequested?.Invoke(false);
        });
        YesCommand = new AsyncRelayCommand(async () => {
            Result = MBResult.Yes;
            CloseRequested?.Invoke(true);
        });
        NoCommand = new AsyncRelayCommand(async () => {
            Result = MBResult.No;
            CloseRequested?.Invoke(false);
        });

    }
}
