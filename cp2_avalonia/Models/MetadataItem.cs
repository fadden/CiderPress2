/*
 * Copyright 2023 faddenSoft
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
using System.ComponentModel;

using Avalonia.Media;

namespace cp2_avalonia.Models {
    /// <summary>
    /// Item displayed in the metadata list in the center info panel.
    /// </summary>
    public class MetadataItem(
        string key,
        string value,
        string description,
        string valueSyntax,
        bool canEdit)
        : INotifyPropertyChanged
    {
        public string Key { get; private set; } = key;
        public string Value { get; private set; } = value;
        public string? Description { get; private set; } = string.IsNullOrEmpty(description) ? null : description;
        public string? ValueSyntax { get; private set; } = string.IsNullOrEmpty(valueSyntax) ? null : valueSyntax;
        public bool CanEdit { get; private set; } = canEdit;
        public IBrush TextForeground => CanEdit ? Brushes.Black : Brushes.Gray;

        public event PropertyChangedEventHandler? PropertyChanged;
        public void SetValue(string value) {
            Value = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
        }
    }
}