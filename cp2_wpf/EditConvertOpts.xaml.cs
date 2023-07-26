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
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using AppCommon;
using CommonUtil;
using FileConv;

namespace cp2_wpf {
    /// <summary>
    /// Edit the options passed to the import/export converters.
    /// </summary>
    public partial class EditConvertOpts : Window, INotifyPropertyChanged {
        // INotifyPropertyChanged implementation
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string propertyName = "") {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public string DescriptionText {
            get { return mDescriptionText; }
            set { mDescriptionText = value; OnPropertyChanged(); }
        }
        private string mDescriptionText = string.Empty;

        /// <summary>
        /// List of import or export converters.
        /// </summary>
        public class ConverterListItem {
            public string Tag { get; private set; }
            public string Label { get; private set; }
            public string Description { get; private set; }

            public ConverterListItem(string tag, string label, string description) {
                Tag = tag;
                Label = label;
                Description = description;
            }
        }

        public List<ConverterListItem> ConverterList { get; } = new List<ConverterListItem>();


        public EditConvertOpts(Window owner, bool isExport) {
            InitializeComponent();
            Owner = owner;
            DataContext = this;

            if (isExport) {
                for (int i = 0; i < ExportFoundry.GetCount(); i++) {
                    ExportFoundry.GetConverterInfo(i, out string tag, out string label,
                        out string description);
                    ConverterListItem item = new ConverterListItem(tag, label, description);
                    ConverterList.Add(item);
                }
            } else {
                for (int i = 0; i < ImportFoundry.GetCount(); i++) {
                    ImportFoundry.GetConverterInfo(i, out string tag, out string label,
                        out string description);
                    ConverterListItem item = new ConverterListItem(tag, label, description);
                    ConverterList.Add(item);
                }
            }

            ConverterList.Sort(delegate (ConverterListItem item1, ConverterListItem item2) {
                return string.Compare(item1.Label, item2.Label);
            });
            converterCombo.SelectedIndex = 0;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e) {
            DialogResult = true;
        }

        private void ConverterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) {
            ConverterListItem? selItem = ((ComboBox)sender).SelectedItem as ConverterListItem;
            if (selItem == null) {
                Debug.WriteLine("No selection?");
                return;
            }

            // Set the description, replacing any newline chars with the system's preferred EOL.
            DescriptionText = selItem.Description.Replace("\n", Environment.NewLine);
        }
    }
}
