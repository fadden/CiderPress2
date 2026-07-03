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

using System.Collections.Generic;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;
using cp2_avalonia.Tools;
using static cp2_avalonia.Tools.ConfigOptCtrl;
using static FileConv.Converter;
using cp2_avalonia.ViewModels;

namespace cp2_avalonia.Views {
    /// <summary>
    /// Edit the options passed to the import/export converters.
    /// </summary>
    public partial class EditConvertOpts : Window {
        private readonly List<ControlMapItem> mCustomCtrls = new List<ControlMapItem>();
        private Dictionary<string, string> mConvOptions = new Dictionary<string, string>();
        private bool mIsConfiguring;

        private EditConvertOptsViewModel? VM => DataContext as EditConvertOptsViewModel;

        public EditConvertOpts() {
            InitializeComponent();
            if (Design.IsDesignMode) return;
            DataContextChanged += (_, __) => {
                if (DataContext is EditConvertOptsViewModel vm) {
                    vm.CloseRequested += result => Close(result);
                    // Build control map and trigger initial selection once ViewModel is live.
                    CreateControlMap();
                    converterCombo.SelectedIndex = 0;
                }
            };
        }

        private void ConverterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) {
            EditConvertOptsViewModel.ConverterListItem? selItem =
                ((ComboBox)sender).SelectedItem as EditConvertOptsViewModel.ConverterListItem;
            if (selItem == null) {
                Debug.WriteLine("No selection?");
                return;
            }
            VM!.SetDescription(selItem.Description);
            if (mCustomCtrls.Count > 0) {
                ConfigureForConverter(selItem.Tag, selItem.OptionDefs);
            }
        }

        private void CreateControlMap() {
            mCustomCtrls.Add(new ToggleButtonMapItem(UpdateOption, checkBox1));
            mCustomCtrls.Add(new ToggleButtonMapItem(UpdateOption, checkBox2));
            mCustomCtrls.Add(new ToggleButtonMapItem(UpdateOption, checkBox3));
            mCustomCtrls.Add(new TextBoxMapItem(UpdateOption, stringInput1, stringInput1_Label,
                stringInput1_Box));
            mCustomCtrls.Add(new RadioButtonGroupItem(UpdateOption, multiGroup1,
            [
                radioButton1_1, radioButton1_2, radioButton1_3,
                    radioButton1_4
            ]));
            mCustomCtrls.Add(new RadioButtonGroupItem(UpdateOption, multiGroup2,
            [
                radioButton2_1, radioButton2_2, radioButton2_3,
                    radioButton2_4
            ]));
        }

        private void ConfigureForConverter(string convTag, List<OptionDefinition> optDefs) {
            Debug.WriteLine("Configure controls for " + convTag);
            mIsConfiguring = true;
            mConvOptions = LoadExportOptions(optDefs, VM!.SettingPrefix, convTag, VM!.SettingsService);
            HideConvControls(mCustomCtrls);
            noOptions.IsVisible = (optDefs.Count == 0);
            ConfigureControls(mCustomCtrls, optDefs, mConvOptions);
            Dispatcher.UIThread.Post(() => mIsConfiguring = false);
        }

        private void UpdateOption(string tag, string newValue) {
            if (mIsConfiguring) {
                Debug.WriteLine("Ignoring initial set '" + tag + "' = '" + newValue + "'");
                return;
            }
            EditConvertOptsViewModel.ConverterListItem? selItem =
                converterCombo.SelectedItem as EditConvertOptsViewModel.ConverterListItem;
            Debug.Assert(selItem != null);
            VM!.UpdateOption(selItem.Tag, tag, newValue, mConvOptions);
        }
    }
}
