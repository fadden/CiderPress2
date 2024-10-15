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

using CommonUtil;

namespace cp2_wpf {
    /// <summary>
    /// Application settings dialog.
    /// </summary>
    public partial class EditAppSettings : Window, INotifyPropertyChanged {
        /// <summary>
        /// Event that the controller can subscribe to if it wants to be notified when the
        /// "Apply" or "OK" button is hit.
        /// </summary>
        public event SettingsAppliedHandler? SettingsApplied;
        public delegate void SettingsAppliedHandler();

        /// <summary>
        /// Copy of settings that we make changes to.  On "Apply" or "OK", this is pushed
        /// into the global settings object.
        /// </summary>
        private SettingsHolder mSettings;

        /// <summary>
        /// Dirty flag, set when anything in mSettings changes.  Determines whether or not
        /// the Apply button is enabled.
        /// </summary>
        public bool IsDirty {
            get { return mIsDirty; }
            set {
                mIsDirty = value;
                OnPropertyChanged();
            }
        }
        private bool mIsDirty;

        // INotifyPropertyChanged implementation
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string propertyName = "") {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }


        public EditAppSettings(Window owner) {
            InitializeComponent();
            Owner = owner;
            DataContext = this;

            // Make a local copy of the settings.
            mSettings = new SettingsHolder(AppSettings.Global);
        }

        private void Window_Loaded(object sender, RoutedEventArgs e) {
            Loaded_General();
            IsDirty = false;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e) {
            ApplySettings();
            DialogResult = true;
        }

        private void ApplyButton_Click(object sender, RoutedEventArgs e) {
            ApplySettings();
        }

        /// <summary>
        /// Applies our local settings to the global settings object.
        /// </summary>
        private void ApplySettings() {
            AppSettings.Global.ReplaceSettings(mSettings);
            OnSettingsApplied();
            IsDirty = false;
        }

        /// <summary>
        /// Raises the "settings applied" event.
        /// </summary>
        private void OnSettingsApplied() {
            SettingsAppliedHandler? handler = SettingsApplied;
            if (handler != null) {
                handler();
            }
        }

        #region General

        private void Loaded_General() {
            mAutoOpenDepth = mSettings.GetEnum(AppSettings.AUTO_OPEN_DEPTH,
                MainController.AutoOpenDepth.SubVol);
            SetAutoOpenDepth();
            mAudioAlg = mSettings.GetEnum(AppSettings.AUDIO_DECODE_ALG,
                CassetteDecoder.Algorithm.ZeroCross);
            SetAudioAlg();
        }

        private MainController.AutoOpenDepth mAutoOpenDepth;
        public bool AutoOpenDepth_Shallow {
            get { return mAutoOpenDepth == MainController.AutoOpenDepth.Shallow; }
            set {
                mAutoOpenDepth = MainController.AutoOpenDepth.Shallow;
                SetAutoOpenDepth();
            }
        }
        public bool AutoOpenDepth_SubVol {
            get { return mAutoOpenDepth == MainController.AutoOpenDepth.SubVol; }
            set {
                mAutoOpenDepth = MainController.AutoOpenDepth.SubVol;
                SetAutoOpenDepth();
            }
        }
        public bool AutoOpenDepth_Max {
            get { return mAutoOpenDepth == MainController.AutoOpenDepth.Max; }
            set {
                mAutoOpenDepth = MainController.AutoOpenDepth.Max;
                SetAutoOpenDepth();
            }
        }
        private void SetAutoOpenDepth() {
            mSettings.SetEnum(AppSettings.AUTO_OPEN_DEPTH, mAutoOpenDepth);
            OnPropertyChanged(nameof(AutoOpenDepth_Shallow));
            OnPropertyChanged(nameof(AutoOpenDepth_SubVol));
            OnPropertyChanged(nameof(AutoOpenDepth_Max));
            IsDirty = true;
        }

        private CassetteDecoder.Algorithm mAudioAlg;
        public bool Audio_Zero {
            get { return mAudioAlg == CassetteDecoder.Algorithm.ZeroCross; }
            set {
                mAudioAlg = CassetteDecoder.Algorithm.ZeroCross;
                SetAudioAlg();
            }
        }
        public bool Audio_PTP_Sharp {
            get { return mAudioAlg == CassetteDecoder.Algorithm.SharpPeak; }
            set {
                mAudioAlg = CassetteDecoder.Algorithm.SharpPeak;
                SetAudioAlg();
            }
        }
        public bool Audio_PTP_Round {
            get { return mAudioAlg == CassetteDecoder.Algorithm.RoundPeak; }
            set {
                mAudioAlg = CassetteDecoder.Algorithm.RoundPeak;
                SetAudioAlg();
            }
        }
        public bool Audio_PTP_Shallow {
            get { return mAudioAlg == CassetteDecoder.Algorithm.ShallowPeak; }
            set {
                mAudioAlg = CassetteDecoder.Algorithm.ShallowPeak;
                SetAudioAlg();
            }
        }
        private void SetAudioAlg() {
            mSettings.SetEnum(AppSettings.AUDIO_DECODE_ALG, mAudioAlg);
            OnPropertyChanged(nameof(Audio_Zero));
            OnPropertyChanged(nameof(Audio_PTP_Sharp));
            OnPropertyChanged(nameof(Audio_PTP_Round));
            OnPropertyChanged(nameof(Audio_PTP_Shallow));
            IsDirty = true;
        }

        public bool EnableMacZip {
            get { return mSettings.GetBool(AppSettings.MAC_ZIP_ENABLED, true); }
            set {
                mSettings.SetBool(AppSettings.MAC_ZIP_ENABLED, value);
                OnPropertyChanged();
                IsDirty = true;
            }
        }

        public bool EnableDOSTextConv {
            get { return mSettings.GetBool(AppSettings.DOS_TEXT_CONV_ENABLED, false); }
            set {
                mSettings.SetBool(AppSettings.DOS_TEXT_CONV_ENABLED, value);
                OnPropertyChanged();
                IsDirty = true;
            }
        }

        private void ConfigureImportOptions_Click(object sender, RoutedEventArgs e) {
            SettingsHolder settings = new SettingsHolder(mSettings);
            EditConvertOpts dialog = new EditConvertOpts(this, false, settings);
            if (dialog.ShowDialog() == true) {
                mSettings.MergeSettings(settings);
                IsDirty = mSettings.IsDirty;
            }
        }

        private void ConfigureExportOptions_Click(object sender, RoutedEventArgs e) {
            SettingsHolder settings = new SettingsHolder(mSettings);
            EditConvertOpts dialog = new EditConvertOpts(this, true, settings);
            if (dialog.ShowDialog() == true) {
                mSettings.MergeSettings(settings);
                IsDirty = mSettings.IsDirty;
            }
        }

        public bool EnableDebugMenu {
            get { return mSettings.GetBool(AppSettings.DEBUG_MENU_ENABLED, false); }
            set {
                mSettings.SetBool(AppSettings.DEBUG_MENU_ENABLED, value);
                OnPropertyChanged();
                IsDirty = true;
            }
        }

        #endregion General
    }
}
