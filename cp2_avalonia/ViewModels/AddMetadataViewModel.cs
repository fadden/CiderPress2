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

using DiskArc;

namespace cp2_avalonia.ViewModels;

/// <summary>
/// ViewModel for the Add Metadata Entry dialog.
/// </summary>
public class AddMetadataViewModel : ObservableObject
{
    private readonly IMetadata mMetaObj;

    public event Action<bool>? CloseRequested;

    public IRelayCommand OkCommand { get; }
    public IRelayCommand CancelCommand { get; }

    // ── Validity ──────────────────────────────────────────────────────────────
    private bool mIsValid;
    public bool IsValid {
        get => mIsValid;
        private set { if (SetProperty(ref mIsValid, value)) OkCommand?.NotifyCanExecuteChanged(); }
    }

    // ── Key ───────────────────────────────────────────────────────────────────
    private string mKeyText = "meta:new_key";
    public string KeyText {
        get => mKeyText;
        set { SetProperty(ref mKeyText, value); UpdateControls(); }
    }

    // ── Value ─────────────────────────────────────────────────────────────────
    private string mValueText = string.Empty;
    public string ValueText {
        get => mValueText;
        set { SetProperty(ref mValueText, value); UpdateControls(); }
    }

    // ── Syntax hint text ──────────────────────────────────────────────────────
    public string KeySyntaxText { get; }
    public string ValueSyntaxText { get; }

    // ── Brush-replacement booleans (use AXAML style trigger to color red) ───
    private bool mIsKeyValid;
    public bool IsKeyValid {
        get => mIsKeyValid;
        private set => SetProperty(ref mIsKeyValid, value);
    }

    private bool mIsValueValid;
    public bool IsValueValid {
        get => mIsValueValid;
        private set => SetProperty(ref mIsValueValid, value);
    }

    private bool mIsNonUniqueVisible;
    public bool IsNonUniqueVisible {
        get => mIsNonUniqueVisible;
        private set => SetProperty(ref mIsNonUniqueVisible, value);
    }

    private bool CanOk() => IsValid;

    public AddMetadataViewModel(IMetadata metaObj)
    {
        mMetaObj = metaObj;

        KeySyntaxText =
            "Keys are comprised of ASCII letters, numbers, and the underscore ('_')." +
            Environment.NewLine +
            "WOZ metadata keys are prefixed with \"meta:\".";
        ValueSyntaxText = "WOZ values may have any characters except linefeed and tab.";

        OkCommand = new AsyncRelayCommand(async () =>
            CloseRequested?.Invoke(true), CanOk);
        CancelCommand = new AsyncRelayCommand(async () =>
            CloseRequested?.Invoke(false));

        UpdateControls();
    }

    private void UpdateControls()
    {
        // Key must be ASCII letters, digits, or underscore; WOZ keys start with "meta:".
        bool keyOk = !string.IsNullOrEmpty(mKeyText) &&
            System.Text.RegularExpressions.Regex.IsMatch(mKeyText, @"^[A-Za-z0-9_:]+$");
        IsKeyValid = keyOk;

        bool valueOk = keyOk && mMetaObj.TestMetaValue(mKeyText, mValueText);
        IsValueValid = valueOk;

        // Check for duplicate key.
        bool exists = mMetaObj.GetMetaEntry(mKeyText) != null;
        IsNonUniqueVisible = exists;

        IsValid = keyOk && valueOk && !exists;
    }
}
