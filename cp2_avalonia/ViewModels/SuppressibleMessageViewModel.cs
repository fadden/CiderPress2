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
/// ViewModel for a message box that includes a "Don't show this again" checkbox.
/// </summary>
public class SuppressibleMessageViewModel : ObservableObject
{
    public string Message { get; }
    public string Caption { get; }
    public string SuppressLabel { get; }

    public MBIcon IconKind { get; }
    public bool ShowIcon       => IconKind != MBIcon.None;
    public bool IsInfoIcon     => IconKind == MBIcon.Information;
    public bool IsWarningIcon  => IconKind == MBIcon.Warning;
    public bool IsErrorIcon    => IconKind == MBIcon.Error;
    public bool IsQuestionIcon => IconKind == MBIcon.Question;

    public bool ShowOkButton     { get; }
    public bool ShowCancelButton { get; }
    public bool ShowYesButton    { get; }
    public bool ShowNoButton     { get; }

    private bool mSuppressDontShow;
    public bool SuppressDontShow {
        get => mSuppressDontShow;
        set => SetProperty(ref mSuppressDontShow, value);
    }

    public MBResult Result { get; private set; } = MBResult.Cancel;

    public event Action<bool>? CloseRequested;

    public IRelayCommand OkCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand YesCommand { get; }
    public IRelayCommand NoCommand { get; }

    public SuppressibleMessageViewModel(string message, string caption, string suppressLabel,
            MBButton buttons, MBIcon icon)
    {
        Message = message;
        Caption = caption;
        SuppressLabel = suppressLabel;
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
