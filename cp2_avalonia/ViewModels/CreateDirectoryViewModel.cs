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
/// ViewModel for the Create Directory dialog.
/// </summary>
public class CreateDirectoryViewModel : ObservableObject
{
    private readonly IFileSystem mFileSystem;
    private readonly IFileEntry mContainingDir;
    private readonly Func<string, bool> mIsValidFunc;

    public event Action<bool>? CloseRequested;

    public IRelayCommand OkCommand { get; }
    public IRelayCommand CancelCommand { get; }

    // ── Output property ───────────────────────────────────────────────────────
    private string mNewFileName = "NEW.DIR";
    public string NewFileName {
        get => mNewFileName;
        set { SetProperty(ref mNewFileName, value); UpdateControls(); }
    }

    // ── Validity ──────────────────────────────────────────────────────────────
    private bool mIsValid;
    public bool IsValid {
        get => mIsValid;
        private set { if (SetProperty(ref mIsValid, value)) OkCommand?.NotifyCanExecuteChanged(); }
    }

    // ── Display text ──────────────────────────────────────────────────────────
    public string SyntaxRulesText { get; }

    // ── Brush-replacement booleans ────────────────────────────────────────────
    /// <summary>True when the name passes the syntax check.</summary>
    private bool mIsNameValid;
    public bool IsNameValid {
        get => mIsNameValid;
        private set => SetProperty(ref mIsNameValid, value);
    }

    /// <summary>True when the name is unique in the containing directory.</summary>
    private bool mIsUniqueNameValid = true;
    public bool IsUniqueNameValid {
        get => mIsUniqueNameValid;
        private set => SetProperty(ref mIsUniqueNameValid, value);
    }

    private bool CanOk() => IsValid;

    public CreateDirectoryViewModel(
        IFileSystem fileSystem,
        IFileEntry parentDir,
        Func<string, bool> isValidName,
        string syntaxRules)
    {
        mFileSystem = fileSystem;
        mContainingDir = parentDir;
        mIsValidFunc = isValidName;
        SyntaxRulesText = syntaxRules;

        OkCommand = new AsyncRelayCommand(async () =>
            CloseRequested?.Invoke(true), CanOk);
        CancelCommand = new AsyncRelayCommand(async () =>
            CloseRequested?.Invoke(false));

        UpdateControls();
    }

    private void UpdateControls()
    {
        bool nameOk = mIsValidFunc(mNewFileName);
        IsNameValid = nameOk;

        bool exists = mFileSystem.TryFindFileEntry(mContainingDir, mNewFileName,
            out IFileEntry _);
        IsUniqueNameValid = !exists;

        IsValid = nameOk && !exists;
    }
}
