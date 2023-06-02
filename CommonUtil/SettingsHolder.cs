/*
 * Copyright 2023 faddenSoft
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
using System.Text.Json;

namespace CommonUtil {
    /// <summary>
    /// Settings registry.  Contents are stored as name/value pairs, where the value is
    /// serialized as a string.  Names are case-sensitive.  Serialization to/from JSON is
    /// included.
    /// </summary>
    public class SettingsHolder {
        /// <summary>
        /// Dirty flag, set to true by every "set" call that changes a value.  This should
        /// be cleared by the caller after the changes have been processed (e.g. save to a file).
        /// </summary>
        public bool IsDirty { get; set; }
        //public bool IsDirty {
        //    get { return mIsDirty; }
        //    set {
        //        if (value == true) {
        //            Debug.WriteLine("IsDirty=true");
        //        }
        //        mIsDirty = value;
        //    }
        //}
        //private bool mIsDirty;

        /// <summary>
        /// Settings storage.
        /// </summary>
        private Dictionary<string, string> mSettings = new Dictionary<string, string>();


        /// <summary>
        /// Constructor.  The list of settings will be empty.
        /// </summary>
        public SettingsHolder() { }

        /// <summary>
        /// Copy constructor.  Makes a deep copy of the original.
        /// </summary>
        public SettingsHolder(SettingsHolder src) {
            mSettings.EnsureCapacity(src.mSettings.Count);
            foreach (KeyValuePair<string, string> kvp in src.mSettings) {
                mSettings.Add(kvp.Key, kvp.Value);
            }
        }

        /// <summary>
        /// Replaces the existing list of settings with a new list.
        /// </summary>
        /// <remarks>
        /// This can be used to replace the contents of a singleton settings object without
        /// discarding the object itself.
        /// </remarks>
        /// <param name="newSettings">New settings object.</param>
        public void ReplaceSettings(SettingsHolder newSettings) {
            // Create a temporary copy of the object, and then just grab the settings dictionary
            // from it.
            mSettings = new SettingsHolder(newSettings).mSettings;
            IsDirty = true;
        }

        /// <summary>
        /// Merges settings from another settings object into this one.
        /// </summary>
        /// <param name="newSettings">New settings object.</param>
        public void MergeSettings(SettingsHolder newSettings) {
            foreach (KeyValuePair<string, string> kvp in newSettings.mSettings) {
                mSettings[kvp.Key] = kvp.Value;     // add or replace
            }
            IsDirty = true;
        }

        /// <summary>
        /// Determines whether a setting with the specified key exists.
        /// </summary>
        /// <param name="name">Setting name.</param>
        /// <returns>True if the setting was found.</returns>
        public bool Exists(string name) {
            return mSettings.ContainsKey(name);
        }

        /// <summary>
        /// Retrieves an integer setting.
        /// </summary>
        /// <param name="name">Setting name.</param>
        /// <param name="defaultValue">Setting default value.</param>
        /// <returns>The value found, or the default value if no setting with the specified
        ///   name exists, or the stored value is not an integer.</returns>
        public int GetInt(string name, int defaultValue) {
            if (!mSettings.TryGetValue(name, out string? valueStr)) {
                return defaultValue;
            }
            if (!int.TryParse(valueStr, out int value)) {
                Debug.WriteLine("Warning: int parse failed on " + name + "=" + valueStr);
                return defaultValue;
            }
            return value;
        }

        /// <summary>
        /// Sets an integer setting.
        /// </summary>
        /// <param name="name">Setting name.</param>
        /// <param name="value">Setting value.</param>
        public void SetInt(string name, int value) {
            string newVal = value.ToString();
            if (!mSettings.TryGetValue(name, out string? oldValue) || oldValue != newVal) {
                mSettings[name] = newVal;
                IsDirty = true;
            }
        }

        /// <summary>
        /// Retrieves a boolean setting.
        /// </summary>
        /// <param name="name">Setting name.</param>
        /// <param name="defaultValue">Setting default value.</param>
        /// <returns>The value found, or the default value if no setting with the specified
        ///   name exists, or the stored value is not a boolean.</returns>
        public bool GetBool(string name, bool defaultValue) {
            if (!mSettings.TryGetValue(name, out string? valueStr)) {
                return defaultValue;
            }
            if (!bool.TryParse(valueStr, out bool value)) {
                Debug.WriteLine("Warning: bool parse failed on " + name + "=" + valueStr);
                return defaultValue;
            }
            return value;
        }

        /// <summary>
        /// Sets a boolean setting.
        /// </summary>
        /// <param name="name">Setting name.</param>
        /// <param name="value">Setting value.</param>
        public void SetBool(string name, bool value) {
            string newVal = value.ToString();
            if (!mSettings.TryGetValue(name, out string? oldValue) || oldValue != newVal) {
                mSettings[name] = newVal;
                IsDirty = true;
            }
        }

        /// <summary>
        /// Retrieves an enumerated value setting.
        /// </summary>
        /// <param name="name">Setting name.</param>
        /// <param name="enumType">Enum type that the value is part of.</param>
        /// <param name="defaultValue">Setting default value.</param>
        /// <returns>The value found, or the default value if no setting with the specified
        ///   name exists, or the stored value is not a member of the specified enumerated
        ///   type.</returns>
        //public int GetEnum(string name, Type enumType, int defaultValue) {
        //    if (!mSettings.TryGetValue(name, out string? valueStr)) {
        //        return defaultValue;
        //    }
        //    try {
        //        object o = Enum.Parse(enumType, valueStr);
        //        return (int)o;
        //    } catch (ArgumentException ae) {
        //        Debug.WriteLine("Failed to parse " + valueStr + " (enum " + enumType + "): " +
        //            ae.Message);
        //        return defaultValue;
        //    }
        //}

        /// <summary>
        /// Retrieves an enumerated value setting.
        /// </summary>
        /// <typeparam name="T">Enumerated type.</typeparam>
        /// <param name="name">Setting name.</param>
        /// <param name="defaultValue">Setting default value.</param>
        /// <returns>The value found, or the default value if no setting with the specified
        ///   name exists, or the stored value is not a member of the specified enumerated
        ///   type.</returns>
        public T GetEnum<T>(string name, T defaultValue) {
            if (!mSettings.TryGetValue(name, out string? valueStr)) {
                return defaultValue;
            }
            try {
                object o = Enum.Parse(typeof(T), valueStr);
                return (T)o;
            } catch (ArgumentException ae) {
                Debug.WriteLine("Failed to parse " + valueStr + " (enum " + typeof(T) + "): " +
                    ae.Message);
                return defaultValue;
            }
        }

        /// <summary>
        /// Sets an enumerated setting.
        /// </summary>
        /// <remarks>
        /// The value is output to the settings file as a string, rather than an integer, allowing
        /// the correct handling even if the enumerated values are renumbered.
        /// </remarks>
        /// <param name="name">Setting name.</param>
        /// <param name="enumType">Enum type.</param>
        /// <param name="value">Setting value (integer enum index).</param>
        //public void SetEnum(string name, Type enumType, int value) {
        //    string? newVal = Enum.GetName(enumType, value);
        //    if (newVal == null) {
        //        Debug.WriteLine("Unable to get enum name type=" + enumType + " value=" + value);
        //        return;
        //    }
        //    if (!mSettings.TryGetValue(name, out string? oldValue) || oldValue != newVal) {
        //        mSettings[name] = newVal;
        //        IsDirty = true;
        //    }
        //}

        /// <summary>
        /// Sets an enumerated setting.
        /// </summary>
        /// <remarks>
        /// The value is output to the settings file as a string, rather than an integer, allowing
        /// the correct handling even if the enumerated values are renumbered.
        /// </remarks>
        /// <typeparam name="T">Enumerated type.</typeparam>
        /// <param name="name">Setting name.</param>
        /// <param name="value">Setting value (integer enum index).</param>
        public void SetEnum<T>(string name, T value) {
            if (value == null) {
                throw new NotImplementedException("Can't handle a null-valued enum type");
            }
            string? newVal = Enum.GetName(typeof(T), value);
            if (newVal == null) {
                Debug.WriteLine("Unable to get enum name type=" + typeof(T) + " value=" + value);
                return;
            }
            if (!mSettings.TryGetValue(name, out string? oldValue) || oldValue != newVal) {
                mSettings[name] = newVal;
                IsDirty = true;
            }
        }

        /// <summary>
        /// Retrieves a string setting.  The default value will be returned if the key
        /// is not found, or if the value is null.
        /// </summary>
        /// <param name="name">Setting name.</param>
        /// <param name="defaultValue">Setting default value.</param>
        /// <returns>The value found, or defaultValue if not value is found.</returns>
        public string GetString(string name, string defaultValue) {
            if (!mSettings.TryGetValue(name, out string? valueStr) || valueStr == null) {
                return defaultValue;
            }
            return valueStr;
        }

        /// <summary>
        /// Sets a string setting.
        /// </summary>
        /// <param name="name">Setting name.</param>
        /// <param name="value">Setting value.  If the value is null, the setting will be
        ///   removed.</param>
        public void SetString(string name, string? value) {
            if (value == null) {
                mSettings.Remove(name);
                IsDirty = true;
            } else {
                if (!mSettings.TryGetValue(name, out string? oldValue) || oldValue != value) {
                    mSettings[name] = value;
                    IsDirty = true;
                }
            }
        }

        /// <summary>
        /// Serializes settings dictionary into a string.  Useful for saving settings to a file.
        /// </summary>
        /// <remarks>
        /// <para>This just serializes the dictionary.  No structure is defined to store
        /// additional metadata.</para>
        /// </remarks>
        /// <returns>Serialized settings.</returns>
        public string Serialize() {
            // Use WriteIndented to improve readability for nosy humans.
            string cereal = JsonSerializer.Serialize(mSettings, typeof(Dictionary<string, string>),
                new JsonSerializerOptions() { WriteIndented = true });
            return cereal;
        }

        /// <summary>
        /// Deserializes settings from a string.  Useful for loading settings from a file.
        /// </summary>
        /// <param name="cereal">Serialized settings.</param>
        /// <returns>Deserialized settings, or null if deserialization failed.</returns>
        public static SettingsHolder? Deserialize(string cereal) {
            try {
                object? parsed = JsonSerializer.Deserialize(cereal,
                    typeof(Dictionary<string, string>));
                if (parsed == null) {
                    return null;
                }
                SettingsHolder settings = new SettingsHolder();
                settings.mSettings = (Dictionary<string, string>)parsed;
                return settings;
            } catch (JsonException ex) {
                Debug.WriteLine("Settings deserialization failed: " + ex.Message);
                return null;
            }
        }

        public override string ToString() {
            return "[SettingsHolder: " + mSettings.Count + " entries]";
        }
    }
}
