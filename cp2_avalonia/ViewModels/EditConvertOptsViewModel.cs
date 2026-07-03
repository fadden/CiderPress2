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
using System.Collections.Generic;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using CommonUtil;
using FileConv;
using static FileConv.Converter;
using cp2_avalonia.Services;

namespace cp2_avalonia.ViewModels;

/// <summary>
/// ViewModel for the Edit Import/Export Conversion Options dialog.
/// </summary>
public class EditConvertOptsViewModel : ObservableObject
{
    /// <summary>
    /// Represents one converter in the list.
    /// </summary>
    public class ConverterListItem(
        string tag,
        string label,
        string description,
        List<OptionDefinition> optionDefs)
    {
        public string Tag { get; } = tag;
        public string Label { get; } = label;
        public string Description { get; } = description;
        public List<OptionDefinition> OptionDefs { get; } = optionDefs;
    }

    public event Action<bool>? CloseRequested;

    public string DialogTitle { get; }

    public IRelayCommand OkCommand { get; }
    public IRelayCommand CancelCommand { get; }

    // ── Settings accumulator (empty-initialized — only changed keys are stored) ─
    private readonly SettingsHolder mSettings = new SettingsHolder();
    private readonly string mSettingPrefix;
    private readonly ISettingsService _settingsService;

    // ── Bindable properties ────────────────────────────────────────────────────
    private string mDescriptionText = string.Empty;
    public string DescriptionText {
        get => mDescriptionText;
        set => SetProperty(ref mDescriptionText, value);
    }

    public List<ConverterListItem> ConverterList { get; } = new List<ConverterListItem>();

    public EditConvertOptsViewModel(bool isExport, ISettingsService settingsService)
    {
        _settingsService = settingsService;
        DialogTitle = isExport ? "Edit Export Conversion Options" : "Edit Import Conversion Options";
        mSettingPrefix = isExport
            ? AppSettings.EXPORT_SETTING_PREFIX
            : AppSettings.IMPORT_SETTING_PREFIX;

        if (isExport) {
            for (int i = 0; i < ExportFoundry.GetCount(); i++) {
                ExportFoundry.GetConverterInfo(i, out string tag, out string label,
                    out string description, out List<OptionDefinition> optionDefs);
                ConverterList.Add(new ConverterListItem(tag, label, description, optionDefs));
            }
        } else {
            for (int i = 0; i < ImportFoundry.GetCount(); i++) {
                ImportFoundry.GetConverterInfo(i, out string tag, out string label,
                    out string description, out List<OptionDefinition> optionDefs);
                ConverterList.Add(new ConverterListItem(tag, label, description, optionDefs));
            }
        }
        ConverterList.Sort((a, b) => string.CompareOrdinal(a.Label, b.Label));

        OkCommand = new AsyncRelayCommand(async () => {
            // Merge only the keys the user explicitly changed into the global settings.
            _settingsService.MergeSettings(mSettings);
            CloseRequested?.Invoke(true);
        });
        CancelCommand = new AsyncRelayCommand(async () =>
            CloseRequested?.Invoke(false));
    }

    /// <summary>
    /// Called by the code-behind when the converter selection changes, to update
    /// DescriptionText.
    /// </summary>
    public void SetDescription(string description)
    {
        DescriptionText = description.Replace("\n", Environment.NewLine);
    }

    /// <summary>
    /// Called by code-behind's ConfigOptCtrl when the user changes a converter option.
    /// </summary>
    public void UpdateOption(string convTag, string optionTag, string newValue,
            Dictionary<string, string> convOptions)
    {
        ConverterListItem? convItem = ConverterList.Find(item => item.Tag == convTag);
        OptionDefinition? optDef = convItem?.OptionDefs.Find(def => def.OptTag == optionTag);
        if (optDef == null)
        {
            return;
        }
        if (!string.IsNullOrEmpty(newValue) && !IsValidOptionValue(optDef, newValue))
        {
            return;
        }

        string settingKey = mSettingPrefix + convTag;
        if (string.IsNullOrEmpty(newValue)) {
            convOptions.Remove(optionTag);
        } else {
            convOptions[optionTag] = newValue;
        }
        string optStr = ConvConfig.GenerateOptString(convOptions);
        mSettings.SetString(settingKey, optStr);
    }

    private static bool IsValidOptionValue(OptionDefinition optDef, string value)
    {
        switch (optDef.Type)
        {
            case OptionDefinition.OptType.Boolean:
                return bool.TryParse(value, out _);
            case OptionDefinition.OptType.Multi:
                return optDef.MultiTags != null &&
                    Array.IndexOf(optDef.MultiTags, value) >= 0;
            case OptionDefinition.OptType.IntValue:
                return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture,
                    out int intValue) && intValue > 0;
            default:
                return false;
        }
    }

    /// <summary>
    /// Returns the setting prefix (for code-behind to load existing options).
    /// </summary>
    public string SettingPrefix => mSettingPrefix;
    internal ISettingsService SettingsService => _settingsService;
}
