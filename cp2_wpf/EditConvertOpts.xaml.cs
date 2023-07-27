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
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using AppCommon;
using CommonUtil;
using FileConv;
using static cp2_wpf.ConfigOptCtrl;
using static FileConv.Converter;

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
            public List<Converter.OptionDefinition> OptionDefs { get; protected set; }

            public ConverterListItem(string tag, string label, string description,
                    List<Converter.OptionDefinition> optionDefs) {
                Tag = tag;
                Label = label;
                Description = description;
                OptionDefs = optionDefs;
            }
        }

        public List<ConverterListItem> ConverterList { get; } = new List<ConverterListItem>();

        private SettingsHolder mSettings;
        private string mSettingPrefix;


        public EditConvertOpts(Window owner, bool isExport, SettingsHolder settings) {
            InitializeComponent();
            Owner = owner;
            DataContext = this;

            mSettings = settings;

            if (isExport) {
                Title = "Edit Export Conversion Options";
                mSettingPrefix = AppSettings.EXPORT_SETTING_PREFIX;
                for (int i = 0; i < ExportFoundry.GetCount(); i++) {
                    ExportFoundry.GetConverterInfo(i, out string tag, out string label,
                        out string description, out List<OptionDefinition> optionDefs);
                    ConverterListItem item =
                        new ConverterListItem(tag, label, description, optionDefs);
                    ConverterList.Add(item);
                }
            } else {
                Title = "Edit Import Conversion Options";
                mSettingPrefix = AppSettings.IMPORT_SETTING_PREFIX;
                for (int i = 0; i < ImportFoundry.GetCount(); i++) {
                    ImportFoundry.GetConverterInfo(i, out string tag, out string label,
                        out string description, out List<OptionDefinition> optionDefs);
                    ConverterListItem item =
                        new ConverterListItem(tag, label, description, optionDefs);
                    ConverterList.Add(item);
                }
            }

            ConverterList.Sort(delegate (ConverterListItem item1, ConverterListItem item2) {
                return string.Compare(item1.Label, item2.Label);
            });
            converterCombo.SelectedIndex = 0;

            CreateControlMap();
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

            ConfigureControls(selItem.Tag, selItem.OptionDefs);
        }


        private List<ControlMapItem> mCustomCtrls = new List<ControlMapItem>();
        private Dictionary<string, string> mConvOptions = new Dictionary<string, string>();

        /// <summary>
        /// Creates a map of the configurable controls.  The controls are defined in the
        /// "options" section of the XAML.
        /// </summary>
        private void CreateControlMap() {
            mCustomCtrls.Add(new ToggleButtonMapItem(UpdateOption, checkBox1));
            mCustomCtrls.Add(new ToggleButtonMapItem(UpdateOption, checkBox2));
            mCustomCtrls.Add(new ToggleButtonMapItem(UpdateOption, checkBox3));
            mCustomCtrls.Add(new TextBoxMapItem(UpdateOption, stringInput1, stringInput1_Label,
                stringInput1_Box));
            mCustomCtrls.Add(new RadioButtonGroupItem(UpdateOption, multiGroup1,
                new RadioButton[] { radioButton1_1, radioButton1_2, radioButton1_3,
                    radioButton1_4 }));
            mCustomCtrls.Add(new RadioButtonGroupItem(UpdateOption, multiGroup2,
                new RadioButton[] { radioButton2_1, radioButton2_2, radioButton2_3,
                    radioButton2_4 }));
        }

        /// <summary>
        /// Configures the controls for a specific converter.
        /// </summary>
        /// <remarks>
        /// The initial default value is determined by the option's default value and the
        /// local settings.
        /// </remarks>
        /// <param name="conv">Converter to configure for.</param>
        private void ConfigureControls(string convTag, List<OptionDefinition> optDefs) {
            Debug.WriteLine("Configure controls for " + convTag);
            mIsConfiguring = true;

            // Configure options for every control.  If not present in the config file, set
            // the control's default.
            mConvOptions = ConfigOptCtrl.LoadExportOptions(optDefs, mSettingPrefix, convTag);

            ConfigOptCtrl.HideConvControls(mCustomCtrls);

            // Show or hide the "no options" message.
            if (optDefs.Count == 0) {
                noOptions.Visibility = Visibility.Visible;
            } else {
                noOptions.Visibility = Visibility.Collapsed;
            }

            ConfigOptCtrl.ConfigureControls(mCustomCtrls, optDefs, mConvOptions);

            mIsConfiguring = false;
        }

        private bool mIsConfiguring;

        /// <summary>
        /// Updates an option as the result of UI interaction.
        /// </summary>
        /// <param name="tag">Tag of option to update.</param>
        /// <param name="newValue">New value.</param>
        private void UpdateOption(string tag, string newValue) {
            if (mIsConfiguring) {
                Debug.WriteLine("Ignoring initial set '" + tag + "' = '" + newValue + "'");
                return;
            }

            // Get converter tag, so we can form the settings file key.
            ConverterListItem? selItem = converterCombo.SelectedItem as ConverterListItem;
            Debug.Assert(selItem != null);
            string settingKey = mSettingPrefix + selItem.Tag;

            // Update the setting and generate the new config string.
            if (string.IsNullOrEmpty(newValue)) {
                mConvOptions.Remove(tag);
            } else {
                mConvOptions[tag] = newValue;
            }
            string optStr = ConvConfig.GenerateOptString(mConvOptions);

            // Save it to our local copy of the settings.
            // We can't remove it when empty because our changes are getting merged back into a
            // larger pool of settings.
            mSettings.SetString(settingKey, optStr);
        }
    }
}
