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
using System.Windows.Controls.Primitives;
using System.Windows.Data;

using CommonUtil;
using FileConv;
using static FileConv.Converter;

namespace cp2_wpf {
    /// <summary>
    /// This implements the controls that underlie the converter configuration in the file
    /// viewer and file import/export.  The client code creates a mapping of the controls in
    /// the GUI, and then we create a mapping between the converter option definitions and
    /// the available controls.
    /// </summary>
    internal class ConfigOptCtrl {
        /// <summary>
        /// Base class for mappable control items.
        /// </summary>
        internal abstract class ControlMapItem : INotifyPropertyChanged {
            // INotifyPropertyChanged
            public event PropertyChangedEventHandler? PropertyChanged;
            protected void OnPropertyChanged([CallerMemberName] string propertyName = "") {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }

            /// <summary>
            /// Option tag, for config file option handling.
            /// </summary>
            public string OptTag { get; protected set; } = string.Empty;

            /// <summary>
            /// Outermost element, for visibility control.
            /// </summary>
            public FrameworkElement VisElem { get; }

            /// <summary>
            /// Function to call when the user updates the state of the control.
            /// </summary>
            protected UpdateOption Updater { get; }
            public delegate void UpdateOption(string tag, string newValue);

            /// <summary>
            /// Configures visibility.
            /// </summary>
            public Visibility ItemVis {
                get { return mItemVis; }
                set {
                    if (mItemVis != value) {
                        mItemVis = value;
                        OnPropertyChanged();
                        //Debug.WriteLine("VIS " + this + " -> " + mItemVis);
                    }
                }
            }
            private Visibility mItemVis;

            /// <summary>
            /// True if this item is available for assignment.
            /// </summary>
            public bool IsAvailable { get { return ItemVis != Visibility.Visible; } }

            /// <summary>
            /// Base class constructor.
            /// </summary>
            /// <param name="updater">Update function.</param>
            /// <param name="visElem">Outermost display element, for visibility.</param>
            public ControlMapItem(UpdateOption updater, FrameworkElement visElem) {
                Updater = updater;
                VisElem = visElem;

                Binding binding = new Binding("ItemVis") { Source = this };
                visElem.SetBinding(FrameworkElement.VisibilityProperty, binding);
            }

            /// <summary>
            /// Configures the control and makes it active.
            /// </summary>
            /// <param name="tag"></param>
            /// <param name="uiString"></param>
            /// <param name="defVal"></param>
            /// <param name="radioVal"></param>
            public abstract void AssignControl(string tag, string uiString, string defVal,
                string? radioVal = null);

            /// <summary>
            /// Hides the control.
            /// </summary>
            public void HideControl() {
                ItemVis = Visibility.Collapsed;
            }

            public override string ToString() {
                return "[CMI: Tag=" + OptTag + "]";
            }
        }

        /// <summary>
        /// ToggleButton item (CheckBox, RadioButton).  Single control.
        /// </summary>
        internal class ToggleButtonMapItem : ControlMapItem {
            public ToggleButton Ctrl { get; }

            public string? RadioVal { get; private set; }

            /// <summary>
            /// Change toggle state.
            /// </summary>
            public bool BoolValue {
                get { return mBoolValue; }
                set {
                    if (mBoolValue != value) {
                        mBoolValue = value;
                        OnPropertyChanged();
                        //Debug.WriteLine("VAL: " + this + " --> " + value);
                        if (RadioVal != null) {
                            // Radio buttons only send an update when they become true, and
                            // need to send their tag instead of a boolean.
                            if (value == true) {
                                Updater(OptTag, RadioVal);
                            }
                        } else {
                            // Checkboxes always update.
                            Updater(OptTag, value.ToString());
                        }
                    }
                }
            }
            private bool mBoolValue;

            /// <summary>
            /// Label string in the UI.
            /// </summary>
            public string UIString {
                get { return mUIString; }
                set {
                    if (mUIString != value) {
                        mUIString = value;
                        OnPropertyChanged();
                    }
                }
            }
            private string mUIString = string.Empty;

            public ToggleButtonMapItem(UpdateOption updater, ToggleButton ctrl)
                    : base(updater, ctrl) {
                Ctrl = ctrl;

                Binding binding = new Binding("BoolValue") { Source = this };
                ctrl.SetBinding(ToggleButton.IsCheckedProperty, binding);
                binding = new Binding("UIString") { Source = this };
                ctrl.SetBinding(ToggleButton.ContentProperty, binding);
            }

            public override void AssignControl(string tag, string uiString, string defVal,
                    string? radioVal = null) {
                OptTag = tag;
                UIString = uiString;
                RadioVal = radioVal;

                ItemVis = Visibility.Visible;
                if (bool.TryParse(defVal, out bool value)) {
                    BoolValue = value;
                }
            }

            public override string ToString() {
                return "[TBMI name=" + Ctrl.Name + " tag=" + OptTag + "]";
            }
        }

        /// <summary>
        /// String input item.  These have three parts: a StackPanel wrapped around a TextBlock
        /// for the label and a TextBox for the input.
        /// </summary>
        internal class TextBoxMapItem : ControlMapItem {
            public StackPanel Panel { get; private set; }
            public TextBlock Label { get; private set; }
            public TextBox Box { get; private set; }

            /// <summary>
            /// Input field state.
            /// </summary>
            public string StringValue {
                get { return mStringValue; }
                set {
                    if (mStringValue != value) {
                        mStringValue = value;
                        OnPropertyChanged();
                        //Debug.WriteLine("VAL: " + this + " --> '" + value + "'");
                        Updater(OptTag, value);
                    }
                }
            }
            private string mStringValue = string.Empty;

            /// <summary>
            /// Label string, to the left of the input box.
            /// </summary>
            public string UIString {
                get { return mUIString; }
                set {
                    // Add a colon to make it look nicer.
                    string modValue = value + ':';
                    if (mUIString != modValue) {
                        mUIString = modValue;
                        OnPropertyChanged();
                    }
                }
            }
            private string mUIString = string.Empty;

            public TextBoxMapItem(UpdateOption updater, StackPanel panel, TextBlock label,
                    TextBox box) : base(updater, panel) {
                Panel = panel;
                Label = label;
                Box = box;

                Binding binding = new Binding("StringValue") {
                    Source = this,
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
                };
                box.SetBinding(TextBox.TextProperty, binding);
                binding = new Binding("UIString") { Source = this };
                label.SetBinding(TextBlock.TextProperty, binding);
            }

            public override void AssignControl(string tag, string uiString, string defVal,
                    string? unused = null) {
                OptTag = tag;
                UIString = uiString;
                ItemVis = Visibility.Visible;
                StringValue = defVal;
            }

            public override string ToString() {
                return "[TBMI name=" + Box.Name + " tag=" + OptTag + "]";
            }
        }

        /// <summary>
        /// Radio button group.  Contains multiple toggle button items.
        /// </summary>
        internal class RadioButtonGroupItem : ControlMapItem {
            public GroupBox Group { get; private set; }

            public ToggleButtonMapItem[] ButtonItems { get; }

            /// <summary>
            /// Input field state.
            /// </summary>
            public string StringValue {
                get { return mStringValue; }
                set {
                    if (mStringValue != value) {
                        mStringValue = value;
                        OnPropertyChanged();
                        //Debug.WriteLine("VAL: " + this + " --> '" + value + "'");
                        Updater(OptTag, value);
                    }
                }
            }
            private string mStringValue = string.Empty;

            /// <summary>
            /// Label for the group box.
            /// </summary>
            public string UIString {
                get { return mUIString; }
                set {
                    if (mUIString != value) {
                        mUIString = value;
                        OnPropertyChanged();
                    }
                }
            }
            private string mUIString = string.Empty;

            public RadioButtonGroupItem(UpdateOption updater, GroupBox groupBox,
                    RadioButton[] buttons) : base(updater, groupBox) {
                Group = groupBox;

                Binding binding = new Binding("UIString") { Source = this };
                groupBox.SetBinding(GroupBox.HeaderProperty, binding);

                ButtonItems = new ToggleButtonMapItem[buttons.Length];
                for (int i = 0; i < buttons.Length; i++) {
                    RadioButton button = buttons[i];
                    ButtonItems[i] = new ToggleButtonMapItem(updater, button);
                }
            }

            public override void AssignControl(string tag, string uiString, string defVal,
                    string? unused = null) {
                OptTag = tag;
                UIString = uiString;
                ItemVis = Visibility.Visible;
            }

            public override string ToString() {
                return "[RGBI name=" + Group.Name + " tag=" + OptTag + "]";
            }
        }

        /// <summary>
        /// Loads the option dictionary with default values and values from the app settings.
        /// </summary>
        /// <param name="optDefs">Option definitions, from import/export converter.</param>
        /// <param name="configPrefix">Prefix string for config file options.</param>
        /// <param name="convTag">Converter tag.</param>
        /// <param name="dict">Dictionary into which the converter option strings will be
        ///   placed.</param>
        internal static Dictionary<string, string> LoadExportOptions(List<OptionDefinition> optDefs,
                string configPrefix, string convTag) {
            Dictionary<string, string> dict = new Dictionary<string, string>();

            // Init all options to default values.
            foreach (OptionDefinition optDef in optDefs) {
                string optTag = optDef.OptTag;
                dict[optTag] = optDef.DefaultVal;
            }

            // Get the option config string from the settings file, and parse it.
            string optStr = AppSettings.Global.GetString(configPrefix + convTag, string.Empty);
            if (!string.IsNullOrEmpty(optStr)) {
                if (!ConvConfig.ParseOptString(optStr, dict)) {
                    Debug.Assert(false, "failed to parse option string for " + convTag + ": '" +
                        optStr + "'");
                    // keep going, with defaults
                }
            }
            return dict;
        }

        /// <summary>
        /// Finds the first available control of the specified type.  If none are available, crash.
        /// </summary>
        /// <param name="ctrls">List of controls.</param>
        /// <param name="ctrlType">Type of control to find.</param>
        internal static ControlMapItem FindFirstAvailable(List<ControlMapItem> ctrls, 
                Type ctrlType) {
            foreach (ControlMapItem item in ctrls) {
                if (item.GetType() == ctrlType && item.IsAvailable) {
                    return item;
                }
            }
            throw new NotImplementedException("Not enough instances of " + ctrlType);
        }

        internal static void ConfigureControls(List<ControlMapItem> controls,
                List<OptionDefinition> optionDefs, Dictionary<string, string> convOptions) {
            foreach (OptionDefinition optDef in optionDefs) {
                string defaultVal = convOptions[optDef.OptTag];

                ControlMapItem item;
                switch (optDef.Type) {
                    case OptionDefinition.OptType.Boolean:
                        item = FindFirstAvailable(controls, typeof(ToggleButtonMapItem));
                        item.AssignControl(optDef.OptTag, optDef.OptLabel, defaultVal);
                        break;
                    case OptionDefinition.OptType.IntValue:
                        item = FindFirstAvailable(controls, typeof(TextBoxMapItem));
                        item.AssignControl(optDef.OptTag, optDef.OptLabel, defaultVal);
                        break;
                    case OptionDefinition.OptType.Multi:
                        RadioButtonGroupItem rbg =
                            (RadioButtonGroupItem)FindFirstAvailable(controls,
                                typeof(RadioButtonGroupItem));
                        rbg.AssignControl(optDef.OptTag, optDef.OptLabel, defaultVal);
                        // If we run out of buttons we'll crash.
                        for (int i = 0; i < optDef.MultiTags!.Length; i++) {
                            string rbTag = optDef.MultiTags[i];
                            string rbLabel = optDef.MultiDescrs![i];
                            // Always set the first button to ensure we have something set.
                            string isSet = (i == 0 || rbTag == defaultVal).ToString();
                            rbg.ButtonItems[i].AssignControl(optDef.OptTag, rbLabel, isSet, rbTag);
                        }
                        break;
                    default:
                        throw new NotImplementedException("Unknown optdef type " + optDef.Type);
                }
            }
        }

        /// <summary>
        /// Hides all of the custom controls.
        /// </summary>
        internal static void HideConvControls(List<ControlMapItem> controls) {
            foreach (ControlMapItem item in controls) {
                item.HideControl();

                if (item is RadioButtonGroupItem) {
                    ToggleButtonMapItem[] items = ((RadioButtonGroupItem)item).ButtonItems;
                    foreach (ToggleButtonMapItem tbItem in items) {
                        tbItem.HideControl();
                    }
                }
            }
        }
    }
}
