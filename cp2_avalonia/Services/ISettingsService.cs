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

namespace cp2_avalonia.Services;

using System;
using CommonUtil;

public interface ISettingsService
{
    /// <summary>Fires the key name every time a setting is changed through this service.</summary>
    event Action<string>? SettingChanged;

    bool GetBool(string key, bool defaultValue);
    void SetBool(string key, bool value);

    int GetInt(string key, int defaultValue);
    void SetInt(string key, int value);

    string GetString(string key, string defaultValue);
    void SetString(string key, string? value);

    T GetEnum<T>(string key, T defaultValue) where T : struct;
    void SetEnum<T>(string key, T value) where T : struct;

    /// <summary>Loads settings from disk and merges into the current holder.</summary>
    void Load();

    /// <summary>Saves settings to disk if dirty.</summary>
    void Save();

    /// <summary>Returns a point-in-time copy of all current settings.</summary>
    SettingsHolder Snapshot();

    /// <summary>Replaces all settings with the values from <paramref name="source"/>.</summary>
    void ReplaceSettings(SettingsHolder source);

    /// <summary>Merges only the keys present in <paramref name="delta"/> into current settings.</summary>
    void MergeSettings(SettingsHolder delta);
}