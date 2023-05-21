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
using System.Runtime;
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
            mAutoOpenDepth =
                (MainController.AutoOpenDepth)mSettings.GetEnum(AppSettings.AUTO_OPEN_DEPTH,
                    typeof(MainController.AutoOpenDepth), (int)MainController.AutoOpenDepth.SubVol);
            SetAutoOpenDepth();
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
            mSettings.SetEnum(AppSettings.AUTO_OPEN_DEPTH,
                typeof(MainController.AutoOpenDepth), (int)mAutoOpenDepth);
            OnPropertyChanged("AutoOpenDepth_Shallow");
            OnPropertyChanged("AutoOpenDepth_SubVol");
            OnPropertyChanged("AutoOpenDepth_Max");
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
