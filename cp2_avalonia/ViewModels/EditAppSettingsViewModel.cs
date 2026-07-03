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
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using CommonUtil;
using cp2_avalonia.Models;
using cp2_avalonia.Services;
using static cp2_avalonia.App;

namespace cp2_avalonia.ViewModels;

/// <summary>
/// ViewModel for the Application Settings dialog.
/// </summary>
public class EditAppSettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly IDialogService _dialogService;

    /// <summary>
    /// Local copy of settings that is modified by the dialog. Pushed to global on Apply/OK.
    /// </summary>
    private readonly SettingsHolder mSettings;

    public event Action<bool>? CloseRequested;

    public IRelayCommand OkCommand { get; }
    public IRelayCommand ApplyCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand ConfigureImportCommand { get; }
    public IRelayCommand ConfigureExportCommand { get; }
    public IRelayCommand ResetDialogMessagesCommand { get; }

    // ── General — Auto-open depth ─────────────────────────────────────────────
    private AutoOpenDepth mAutoOpenDepth;
    public bool AutoOpenDepth_Shallow {
        get => mAutoOpenDepth == AutoOpenDepth.Shallow;
        set { if (value) { mAutoOpenDepth = AutoOpenDepth.Shallow; SetAutoOpenDepth(); } }
    }
    public bool AutoOpenDepth_SubVol {
        get => mAutoOpenDepth == AutoOpenDepth.SubVol;
        set { if (value) { mAutoOpenDepth = AutoOpenDepth.SubVol; SetAutoOpenDepth(); } }
    }
    public bool AutoOpenDepth_Max {
        get => mAutoOpenDepth == AutoOpenDepth.Max;
        set { if (value) { mAutoOpenDepth = AutoOpenDepth.Max; SetAutoOpenDepth(); } }
    }
    private void SetAutoOpenDepth()
    {
        mSettings.SetEnum(AppSettings.AUTO_OPEN_DEPTH, mAutoOpenDepth);
        OnPropertyChanged(nameof(AutoOpenDepth_Shallow));
        OnPropertyChanged(nameof(AutoOpenDepth_SubVol));
        OnPropertyChanged(nameof(AutoOpenDepth_Max));
    }

    // ── General — Audio algorithm ─────────────────────────────────────────────
    private CassetteDecoder.Algorithm mAudioAlg;
    public bool Audio_Zero {
        get => mAudioAlg == CassetteDecoder.Algorithm.ZeroCross;
        set { if (value) { mAudioAlg = CassetteDecoder.Algorithm.ZeroCross; SetAudioAlg(); } }
    }
    public bool Audio_PTP_Sharp {
        get => mAudioAlg == CassetteDecoder.Algorithm.SharpPeak;
        set { if (value) { mAudioAlg = CassetteDecoder.Algorithm.SharpPeak; SetAudioAlg(); } }
    }
    public bool Audio_PTP_Round {
        get => mAudioAlg == CassetteDecoder.Algorithm.RoundPeak;
        set { if (value) { mAudioAlg = CassetteDecoder.Algorithm.RoundPeak; SetAudioAlg(); } }
    }
    public bool Audio_PTP_Shallow {
        get => mAudioAlg == CassetteDecoder.Algorithm.ShallowPeak;
        set { if (value) { mAudioAlg = CassetteDecoder.Algorithm.ShallowPeak; SetAudioAlg(); } }
    }
    private void SetAudioAlg()
    {
        mSettings.SetEnum(AppSettings.AUDIO_DECODE_ALG, mAudioAlg);
        OnPropertyChanged(nameof(Audio_Zero));
        OnPropertyChanged(nameof(Audio_PTP_Sharp));
        OnPropertyChanged(nameof(Audio_PTP_Round));
        OnPropertyChanged(nameof(Audio_PTP_Shallow));
    }

    // ── General — Theme ───────────────────────────────────────────────────────
    public int ThemeModeIndex {
        get => (int)mSettings.GetEnum(AppSettings.THEME_MODE, ThemeMode.Light);
        set {
            mSettings.SetEnum(AppSettings.THEME_MODE, (ThemeMode)value);
            OnPropertyChanged();
        }
    }

    // ── Dialog Messages ───────────────────────────────────────────────────────
    private int mSuppressedCount;
    public int SuppressedDialogCount => mSuppressedCount;
    public string SuppressedDialogLabel =>
        mSuppressedCount == 1 ? "1 Item suppressed" : $"{mSuppressedCount} Items suppressed";

    private int CountSuppressedDialogs()
    {
        int count = 0;
        if (mSettings.GetBool(AppSettings.VIEWER_SUPPRESS_UNAVAILABLE_CLOSE, false)) count++;
        if (mSettings.GetBool(AppSettings.VIEWER_SUPPRESS_UNAVAILABLE_REVERT, false)) count++;
        if (mSettings.GetBool(AppSettings.VIEWER_SUPPRESS_SECTOR_EDIT_WARNING, false)) count++;
        return count;
    }

    private void RefreshSuppressedCount()
    {
        mSuppressedCount = CountSuppressedDialogs();
        OnPropertyChanged(nameof(SuppressedDialogCount));
        OnPropertyChanged(nameof(SuppressedDialogLabel));
        ResetDialogMessagesCommand.NotifyCanExecuteChanged();
    }

    private bool CanResetDialogMessages()
    {
        return mSuppressedCount > 0;
    }

    // ── General — Checkboxes ──────────────────────────────────────────────────
    public bool EnableMacZip {        get => mSettings.GetBool(AppSettings.MAC_ZIP_ENABLED, true);
        set { mSettings.SetBool(AppSettings.MAC_ZIP_ENABLED, value); OnPropertyChanged(); }
    }
    public bool EnableDOSTextConv {
        get => mSettings.GetBool(AppSettings.DOS_TEXT_CONV_ENABLED, false);
        set { mSettings.SetBool(AppSettings.DOS_TEXT_CONV_ENABLED, value); OnPropertyChanged(); }
    }

    public EditAppSettingsViewModel(ISettingsService settingsService, IDialogService dialogService)
    {
        _settingsService = settingsService;
        _dialogService = dialogService;

        // Work on a local copy so that Cancel discards changes.
        mSettings = new SettingsHolder(_settingsService.Snapshot());

        // Load enum-backed properties from the local settings copy.
        mAutoOpenDepth = mSettings.GetEnum(AppSettings.AUTO_OPEN_DEPTH, AutoOpenDepth.SubVol);
        mAudioAlg = mSettings.GetEnum(AppSettings.AUDIO_DECODE_ALG,
            CassetteDecoder.Algorithm.ZeroCross);
        mSuppressedCount = CountSuppressedDialogs();

        OkCommand = new AsyncRelayCommand(async () => {
            ApplySettingsToGlobal();
            CloseRequested?.Invoke(true);
        });
        ApplyCommand = new RelayCommand(ApplySettingsToGlobal);
        CancelCommand = new AsyncRelayCommand(async () =>
            CloseRequested?.Invoke(false));

        ConfigureImportCommand = new AsyncRelayCommand(() =>
            OpenConvertOpts(isExport: false));
        ConfigureExportCommand = new AsyncRelayCommand(() =>
            OpenConvertOpts(isExport: true));
        ResetDialogMessagesCommand = new RelayCommand(
            ResetDialogMessages, CanResetDialogMessages);
    }

    private void ResetDialogMessages()
    {
        mSettings.Remove(AppSettings.VIEWER_SUPPRESS_UNAVAILABLE_CLOSE);
        mSettings.Remove(AppSettings.VIEWER_SUPPRESS_UNAVAILABLE_REVERT);
        mSettings.Remove(AppSettings.VIEWER_SUPPRESS_SECTOR_EDIT_WARNING);
        RefreshSuppressedCount();
        // Write to global settings immediately so Cancel doesn't undo the reset.
        _settingsService.SetBool(AppSettings.VIEWER_SUPPRESS_UNAVAILABLE_CLOSE, false);
        _settingsService.SetBool(AppSettings.VIEWER_SUPPRESS_UNAVAILABLE_REVERT, false);
        _settingsService.SetBool(AppSettings.VIEWER_SUPPRESS_SECTOR_EDIT_WARNING, false);
    }

    private void ApplySettingsToGlobal()
    {
        _settingsService.ReplaceSettings(mSettings);
        SettingsApplied?.Invoke();
    }

    private async Task OpenConvertOpts(bool isExport)
    {
        var vm = new EditConvertOptsViewModel(isExport, _settingsService);
        bool ok = await _dialogService.ShowDialogAsync(vm) == true;
        if (ok) {
            // Changes were merged into settings by the dialog; notify listeners.
            SettingsApplied?.Invoke();
        }
    }

    /// <summary>
    /// Raised when settings are pushed to global (Apply or OK).
    /// MainViewModel subscribes to call its own ApplySettings().
    /// </summary>
    public event Action? SettingsApplied;
}
