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
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DiskArc;
using static DiskArc.IMetadata;

namespace cp2_avalonia.ViewModels;

/// <summary>
/// ViewModel for the Edit Metadata dialog.
/// </summary>
public class EditMetadataViewModel : ObservableObject
{
    private readonly IMetadata mMetaObj;
    private readonly MetaEntry mMetaEntry;

    public event Action<bool>? CloseRequested;

    public IRelayCommand OkCommand { get; }
    public IRelayCommand DeleteCommand { get; }
    public IRelayCommand CancelCommand { get; }

    // ── Output properties (read by caller after OK) ───────────────────────────
    public bool DoDelete { get; private set; }
    public string KeyText { get; }

    // ── Validity ──────────────────────────────────────────────────────────────
    private bool mIsValid;
    public bool IsValid {
        get => mIsValid;
        private set => SetProperty(ref mIsValid, value);
    }

    // ── Edit permission ───────────────────────────────────────────────────────
    /// <summary>
    /// Whether the value can be edited.  View binds IsReadOnly="!CanEdit" to valueTextBox.
    /// </summary>
    public bool CanEdit { get; }
    public bool CanDelete { get; }

    // ── Value ─────────────────────────────────────────────────────────────────
    private string mValueText;
    public string ValueText {
        get => mValueText;
        set { SetProperty(ref mValueText, value); UpdateControls(); }
    }

    // ── Display text ──────────────────────────────────────────────────────────
    public string DescriptionText { get; }
    public string ValueSyntaxText { get; }

    // ── Brush-replacement boolean ─────────────────────────────────────────────
    private bool mIsValueValid;
    public bool IsValueValid {
        get => mIsValueValid;
        private set => SetProperty(ref mIsValueValid, value);
    }

    public EditMetadataViewModel(IMetadata metadata, string key)
    {
        mMetaObj = metadata;

        MetaEntry? entry = metadata.GetMetaEntry(key);
        if (entry == null) {
            Debug.Assert(false);
            throw new ArgumentException("couldn't find MetaEntry for key: " + key);
        }
        mMetaEntry = entry;

        KeyText = key;
        mValueText = metadata.GetMetaValue(key, false)!;
        DescriptionText = string.IsNullOrEmpty(entry.Description)
            ? "User-defined entry." : entry.Description;
        CanEdit = entry.CanEdit;
        CanDelete = entry.CanDelete;

        ValueSyntaxText = entry.CanEdit ? entry.ValueSyntax : "This entry can't be edited.";

        OkCommand = new AsyncRelayCommand(async () =>
            CloseRequested?.Invoke(true));
        DeleteCommand = new AsyncRelayCommand(async () => {
            DoDelete = true;
            CloseRequested?.Invoke(true);
        });
        CancelCommand = new AsyncRelayCommand(async () =>
            CloseRequested?.Invoke(false));

        UpdateControls();
    }

    private void UpdateControls()
    {
        bool ok = mMetaObj.TestMetaValue(mMetaEntry.Key, mValueText);
        IsValueValid = ok;
        IsValid = ok;
    }
}
