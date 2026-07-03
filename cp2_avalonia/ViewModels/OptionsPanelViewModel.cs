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
using System.Collections.ObjectModel;
using FileConv;

using CommunityToolkit.Mvvm.ComponentModel;

using cp2_avalonia.Models;
using cp2_avalonia.Services;

namespace cp2_avalonia.ViewModels;

/// <summary>
/// ViewModel for the options panel (right panel).
/// Owns all add/extract option checkboxes, DDCP mode, and converter selection.
/// </summary>
public class OptionsPanelViewModel : ObservableObject {
    private readonly ISettingsService _settingsService;
    private readonly IClipboardService _clipboardService;

    public OptionsPanelViewModel(ISettingsService settingsService,
            IClipboardService clipboardService) {
        _settingsService = settingsService;
        _clipboardService = clipboardService;

        // Initialize backing fields from settings so property getters are consistent.
        mDdcpAddExtract = _settingsService.GetBool(AppSettings.DDCP_ADD_EXTRACT, true);
        mExtPreserveMode = _settingsService.GetEnum(AppSettings.EXT_PRESERVE_MODE,
            AppCommon.ExtractFileWorker.PreserveMode.None);
        mShowOptionsPanel = _settingsService.GetBool(AppSettings.MAIN_RIGHT_PANEL_VISIBLE, true);
        mShowHideRotation = mShowOptionsPanel ? 0 : 90;
    }

    // ---- Panel visibility ----

    private bool mShowOptionsPanel;
    public bool ShowOptionsPanel {
        get => mShowOptionsPanel;
        set {
            SetProperty(ref mShowOptionsPanel, value);
            ShowHideRotation = value ? 0 : 90;
            _settingsService.SetBool(AppSettings.MAIN_RIGHT_PANEL_VISIBLE, value);
        }
    }

    private double mShowHideRotation;
    public double ShowHideRotation {
        get => mShowHideRotation;
        set => SetProperty(ref mShowHideRotation, value);
    }

    // ---- Add options ----

    public bool AddCompress {
        get => _settingsService.GetBool(AppSettings.ADD_COMPRESS_ENABLED, true);
        set {
            _settingsService.SetBool(AppSettings.ADD_COMPRESS_ENABLED, value);
            OnPropertyChanged();
        }
    }

    public bool AddRaw {
        get => _settingsService.GetBool(AppSettings.ADD_RAW_ENABLED, false);
        set {
            _settingsService.SetBool(AppSettings.ADD_RAW_ENABLED, value);
            OnPropertyChanged();
        }
    }

    public bool AddRecurse {
        get => _settingsService.GetBool(AppSettings.ADD_RECURSE_ENABLED, true);
        set {
            _settingsService.SetBool(AppSettings.ADD_RECURSE_ENABLED, value);
            OnPropertyChanged();
        }
    }

    public bool AddStripExt {
        get => _settingsService.GetBool(AppSettings.ADD_STRIP_EXT_ENABLED, true);
        set {
            _settingsService.SetBool(AppSettings.ADD_STRIP_EXT_ENABLED, value);
            OnPropertyChanged();
        }
    }

    public bool AddStripPaths {
        get => _settingsService.GetBool(AppSettings.ADD_STRIP_PATHS_ENABLED, false);
        set {
            _settingsService.SetBool(AppSettings.ADD_STRIP_PATHS_ENABLED, value);
            OnPropertyChanged();
        }
    }

    public bool AddPreserveADF {
        get => _settingsService.GetBool(AppSettings.ADD_PRESERVE_ADF, true);
        set {
            _settingsService.SetBool(AppSettings.ADD_PRESERVE_ADF, value);
            OnPropertyChanged();
        }
    }

    public bool AddPreserveAS {
        get => _settingsService.GetBool(AppSettings.ADD_PRESERVE_AS, true);
        set {
            _settingsService.SetBool(AppSettings.ADD_PRESERVE_AS, value);
            OnPropertyChanged();
        }
    }

    public bool AddPreserveNAPS {
        get => _settingsService.GetBool(AppSettings.ADD_PRESERVE_NAPS, true);
        set {
            _settingsService.SetBool(AppSettings.ADD_PRESERVE_NAPS, value);
            OnPropertyChanged();
        }
    }

    // ---- Extract/export options ----

    public bool ExtAddExportExt {
        get => _settingsService.GetBool(AppSettings.EXT_ADD_EXPORT_EXT, true);
        set {
            _settingsService.SetBool(AppSettings.EXT_ADD_EXPORT_EXT, value);
            OnPropertyChanged();
        }
    }

    public bool ExtRaw {
        get => _settingsService.GetBool(AppSettings.EXT_RAW_ENABLED, false);
        set {
            _settingsService.SetBool(AppSettings.EXT_RAW_ENABLED, value);
            OnPropertyChanged();
        }
    }

    public bool ExtStripPaths {
        get => _settingsService.GetBool(AppSettings.EXT_STRIP_PATHS_ENABLED, false);
        set {
            _settingsService.SetBool(AppSettings.EXT_STRIP_PATHS_ENABLED, value);
            OnPropertyChanged();
        }
    }

    // ---- DDCP mode (AddExtract/ImportExport/DDCPModeIndex share one backing field) ----

    private bool mDdcpAddExtract;

    /// <summary>
    /// True when drag-drop/clipboard mode is "Add/Extract" (default).
    /// Setting to false switches to "Import/Export" mode.
    /// </summary>
    public bool AddExtract {
        get => mDdcpAddExtract;
        set {
            mDdcpAddExtract = value;
            _settingsService.SetBool(AppSettings.DDCP_ADD_EXTRACT, value);
            _ = _clipboardService.ClearIfPendingAsync();
            OnPropertyChanged();
            OnPropertyChanged(nameof(ImportExport));
            OnPropertyChanged(nameof(DDCPModeIndex));
        }
    }

    public bool ImportExport {
        get => !mDdcpAddExtract;
        set => AddExtract = !value;
    }

    public int DDCPModeIndex {
        get => mDdcpAddExtract ? 0 : 1;
        set {
            if (value < 0) { return; }  // ignore -1 sent by ComboBox during initialization
            AddExtract = (value == 0);
        }
    }

    // ---- Export best/combo (two radio buttons sharing one backing field) ----

    public bool IsExportBestChecked {
        get => _settingsService.GetBool(AppSettings.CONV_EXPORT_BEST, true);
        set {
            if (value) {
                _settingsService.SetBool(AppSettings.CONV_EXPORT_BEST, true);
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsExportComboChecked));
        }
    }

    public bool IsExportComboChecked {
        get => !_settingsService.GetBool(AppSettings.CONV_EXPORT_BEST, true);
        set {
            if (value) {
                _settingsService.SetBool(AppSettings.CONV_EXPORT_BEST, false);
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsExportBestChecked));
        }
    }

    // ---- Extract preserve mode (four radio buttons sharing one backing field) ----

    private AppCommon.ExtractFileWorker.PreserveMode mExtPreserveMode;

    public bool ExtPreserveNone {
        get => mExtPreserveMode == AppCommon.ExtractFileWorker.PreserveMode.None;
        set {
            if (value) {
                mExtPreserveMode = AppCommon.ExtractFileWorker.PreserveMode.None;
                _settingsService.SetEnum(AppSettings.EXT_PRESERVE_MODE, mExtPreserveMode);
                RaiseAllExtPreserve();
            }
        }
    }

    public bool ExtPreserveAS {
        get => mExtPreserveMode == AppCommon.ExtractFileWorker.PreserveMode.AS;
        set {
            if (value) {
                mExtPreserveMode = AppCommon.ExtractFileWorker.PreserveMode.AS;
                _settingsService.SetEnum(AppSettings.EXT_PRESERVE_MODE, mExtPreserveMode);
                RaiseAllExtPreserve();
            }
        }
    }

    public bool ExtPreserveADF {
        get => mExtPreserveMode == AppCommon.ExtractFileWorker.PreserveMode.ADF;
        set {
            if (value) {
                mExtPreserveMode = AppCommon.ExtractFileWorker.PreserveMode.ADF;
                _settingsService.SetEnum(AppSettings.EXT_PRESERVE_MODE, mExtPreserveMode);
                RaiseAllExtPreserve();
            }
        }
    }

    public bool ExtPreserveNAPS {
        get => mExtPreserveMode == AppCommon.ExtractFileWorker.PreserveMode.NAPS;
        set {
            if (value) {
                mExtPreserveMode = AppCommon.ExtractFileWorker.PreserveMode.NAPS;
                _settingsService.SetEnum(AppSettings.EXT_PRESERVE_MODE, mExtPreserveMode);
                RaiseAllExtPreserve();
            }
        }
    }

    private void RaiseAllExtPreserve() {
        OnPropertyChanged(nameof(ExtPreserveNone));
        OnPropertyChanged(nameof(ExtPreserveAS));
        OnPropertyChanged(nameof(ExtPreserveADF));
        OnPropertyChanged(nameof(ExtPreserveNAPS));
    }

    // ---- Converter lists and selection ----

    public ObservableCollection<ConvItem> ImportConverters { get; } = new();
    public ObservableCollection<ConvItem> ExportConverters { get; } = new();

    private ConvItem? mSelectedImportConverter;
    public ConvItem? SelectedImportConverter {
        get => mSelectedImportConverter;
        set {
            mSelectedImportConverter = value;
            if (value != null) {
                _settingsService.SetString(AppSettings.CONV_IMPORT_TAG, value.Tag);
            }
            OnPropertyChanged();
        }
    }

    private ConvItem? mSelectedExportConverter;
    public ConvItem? SelectedExportConverter {
        get => mSelectedExportConverter;
        set {
            mSelectedExportConverter = value;
            if (value != null && !IsExportBestChecked) {
                _settingsService.SetString(AppSettings.CONV_EXPORT_TAG, value.Tag);
            }
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Populates ImportConverters and ExportConverters from ImportFoundry/ExportFoundry.
    /// Call once at construction time (after DI is configured).
    /// </summary>
    public void InitConverters() {
        ImportConverters.Clear();
        for (int i = 0; i < ImportFoundry.GetCount(); i++) {
            ImportFoundry.GetConverterInfo(i, out string tag, out string label,
                out string _, out _);
            ImportConverters.Add(new ConvItem(tag, label));
        }
        // Sort alphabetically by label.
        var sortedImport = new System.Collections.Generic.List<ConvItem>(ImportConverters);
        sortedImport.Sort((a, b) => String.CompareOrdinal(a.Label, b.Label));
        ImportConverters.Clear();
        foreach (ConvItem item in sortedImport) {
            ImportConverters.Add(item);
        }

        ExportConverters.Clear();
        for (int i = 0; i < ExportFoundry.GetCount(); i++) {
            ExportFoundry.GetConverterInfo(i, out string tag, out string label,
                out string _, out _);
            ExportConverters.Add(new ConvItem(tag, label));
        }
        var sortedExport = new System.Collections.Generic.List<ConvItem>(ExportConverters);
        sortedExport.Sort((a, b) => String.CompareOrdinal(a.Label, b.Label));
        ExportConverters.Clear();
        foreach (ConvItem item in sortedExport) {
            ExportConverters.Add(item);
        }

        // Set initial selection via backing field so we don't overwrite any saved setting.
        // RefreshFromSettings (called immediately after) will restore the saved choice.
        if (ImportConverters.Count > 0) {
            mSelectedImportConverter = ImportConverters[0];
            OnPropertyChanged(nameof(SelectedImportConverter));
        }
        if (ExportConverters.Count > 0) {
            mSelectedExportConverter = ExportConverters[0];
            OnPropertyChanged(nameof(SelectedExportConverter));
        }
    }

    /// <summary>
    /// Re-reads all settings values and updates properties. Called by MainViewModel
    /// after ISettingsService.SettingChanged fires or after the EditAppSettings dialog
    /// applies changes. Replaces the former PublishSideOptions() method.
    /// </summary>
    public void RefreshFromSettings() {
        OnPropertyChanged(nameof(AddCompress));
        OnPropertyChanged(nameof(AddRaw));
        OnPropertyChanged(nameof(AddRecurse));
        OnPropertyChanged(nameof(AddStripExt));
        OnPropertyChanged(nameof(AddStripPaths));
        OnPropertyChanged(nameof(AddPreserveADF));
        OnPropertyChanged(nameof(AddPreserveAS));
        OnPropertyChanged(nameof(AddPreserveNAPS));
        OnPropertyChanged(nameof(ExtAddExportExt));
        OnPropertyChanged(nameof(ExtRaw));
        OnPropertyChanged(nameof(ExtStripPaths));
        OnPropertyChanged(nameof(IsExportBestChecked));
        OnPropertyChanged(nameof(IsExportComboChecked));

        // Re-read DDCP backing field from settings.
        mDdcpAddExtract = _settingsService.GetBool(AppSettings.DDCP_ADD_EXTRACT, true);
        OnPropertyChanged(nameof(AddExtract));
        OnPropertyChanged(nameof(ImportExport));
        OnPropertyChanged(nameof(DDCPModeIndex));

        // Re-read preserve mode backing field.
        mExtPreserveMode = _settingsService.GetEnum(AppSettings.EXT_PRESERVE_MODE,
            AppCommon.ExtractFileWorker.PreserveMode.None);
        RaiseAllExtPreserve();

        // Restore selected import converter from settings; fall back to first item.
        string importTag = _settingsService.GetString(AppSettings.CONV_IMPORT_TAG,
            string.Empty);
        ConvItem? restoredImport = null;
        if (!string.IsNullOrEmpty(importTag)) {
            foreach (ConvItem item in ImportConverters) {
                if (item.Tag == importTag) { restoredImport = item; break; }
            }
        }
        mSelectedImportConverter = restoredImport ?? (ImportConverters.Count > 0 ? ImportConverters[0] : null);
        OnPropertyChanged(nameof(SelectedImportConverter));

        // Restore selected export converter from settings; fall back to first item.
        string exportTag = _settingsService.GetString(AppSettings.CONV_EXPORT_TAG,
            string.Empty);
        ConvItem? restoredExport = null;
        if (!string.IsNullOrEmpty(exportTag)) {
            foreach (ConvItem item in ExportConverters) {
                if (item.Tag == exportTag) { restoredExport = item; break; }
            }
        }
        mSelectedExportConverter = restoredExport ?? (ExportConverters.Count > 0 ? ExportConverters[0] : null);
        OnPropertyChanged(nameof(SelectedExportConverter));
    }
}
