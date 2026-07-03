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
using System.IO;
using CommonUtil;
using Common;  // PlatformUtil

public class SettingsService : ISettingsService {
    private readonly SettingsHolder mHolder = new SettingsHolder();
    private bool mLoaded;

    public event Action<string>? SettingChanged;
    // NOTE: SettingChanged fires the key name on every Set* call (and string.Empty for bulk
    // ReplaceSettings/MergeSettings).  No production code currently subscribes to it —
    // settings changes propagate through EditAppSettingsViewModel.SettingsApplied →
    // MainViewModel.ApplySettings() instead.  The event hook is intentionally retained
    // for future bindings (e.g. live theme switching without an Apply button).

    public bool GetBool(string key, bool defaultValue) {
        bool value = mHolder.GetBool(key, defaultValue);
        AppLog.D("Settings: read " + key + " = " + value);
        return value;
    }
    public void SetBool(string key, bool value) {
        AppLog.D("Settings: set " + key + " = " + value);
        mHolder.SetBool(key, value);
        SettingChanged?.Invoke(key);
    }

    public int GetInt(string key, int defaultValue) {
        int value = mHolder.GetInt(key, defaultValue);
        AppLog.D("Settings: read " + key + " = " + value);
        return value;
    }
    public void SetInt(string key, int value) {
        AppLog.D("Settings: set " + key + " = " + value);
        mHolder.SetInt(key, value);
        SettingChanged?.Invoke(key);
    }

    public string GetString(string key, string defaultValue) {
        string value = mHolder.GetString(key, defaultValue);
        AppLog.D("Settings: read " + key + " = " + value);
        return value;
    }
    public void SetString(string key, string? value) {
        AppLog.D("Settings: set " + key + " = " + value);
        mHolder.SetString(key, value);
        SettingChanged?.Invoke(key);
    }

    public T GetEnum<T>(string key, T defaultValue) where T : struct {
        T value = mHolder.GetEnum(key, defaultValue);
        AppLog.D("Settings: read " + key + " = " + value);
        return value;
    }
    public void SetEnum<T>(string key, T value) where T : struct {
        AppLog.D("Settings: set " + key + " = " + value);
        mHolder.SetEnum(key, value);
        SettingChanged?.Invoke(key);
    }

    public void Load() {
        if (mLoaded) return;
        mLoaded = true;
        string settingsPath = Path.Combine(
            PlatformUtil.GetRuntimeDataDir(), AppSettings.SETTINGS_FILE_NAME);
        if (!File.Exists(settingsPath)) {
            AppLog.D("Settings: no settings file at '" + settingsPath + "'; using defaults");
            return;
        }
        try {
            string text = File.ReadAllText(settingsPath);
            SettingsHolder? fileSettings = SettingsHolder.Deserialize(text);
            if (fileSettings != null) {
                mHolder.MergeSettings(fileSettings);
            }
            AppLog.I("Settings: read from '" + settingsPath + "'");
        } catch (Exception ex) {
            AppLog.W("Unable to read settings file", ex);
        }
    }

    public void Save() {
        string settingsPath = Path.Combine(
            PlatformUtil.GetRuntimeDataDir(), AppSettings.SETTINGS_FILE_NAME);
        if (!mHolder.IsDirty) {
            AppLog.D("Settings: unchanged; not saving to '" + settingsPath + "'");
            return;
        }
        try {
            string cereal = mHolder.Serialize();
            File.WriteAllText(settingsPath, cereal);
            mHolder.IsDirty = false;
            AppLog.I("Settings: saved to '" + settingsPath + "'");
        } catch (Exception ex) {
            AppLog.E("Failed to save settings file", ex);
        }
    }

    public SettingsHolder Snapshot() => new SettingsHolder(mHolder);

    public void ReplaceSettings(SettingsHolder source) {
        mHolder.ReplaceSettings(source);
        SettingChanged?.Invoke(string.Empty);
    }

    public void MergeSettings(SettingsHolder delta) {
        mHolder.MergeSettings(delta);
        SettingChanged?.Invoke(string.Empty);
    }
}
