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
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using CommonUtil;
using DiskArc;

namespace cp2_wpf.WPFCommon {
    /// <summary>
    /// Sector edit dialog.
    /// </summary>
    public partial class EditSector : Window, INotifyPropertyChanged {
        public delegate bool EnableWriteFunc();

        private EnableWriteFunc? mEnableWriteFunc;

        // temporary
        public string SectorData {
            get { return mSectorData; }
            set { mSectorData = value; OnPropertyChanged(); }
        }
        private string mSectorData;

        /// <summary>
        /// True if the "enable writes" button is enabled.
        /// </summary>
        public bool IsEnableWritesEnabled {
            get { return mIsEnableWritesEnabled; }
            set { mIsEnableWritesEnabled = value; OnPropertyChanged(); }
        }
        private bool mIsEnableWritesEnabled;

        /// <summary>
        /// True if writes are enabled.
        /// </summary>
        public bool IsWriteEnabled {
            get { return mIsWriteEnabled; }
            set { mIsWriteEnabled = value; OnPropertyChanged(); }
        }
        private bool mIsWriteEnabled;

        // INotifyPropertyChanged implementation
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string propertyName = "") {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public EditSector(Window owner, EnableWriteFunc? enableWriteFunc) {
            InitializeComponent();
            Owner = owner;
            DataContext = this;

            mEnableWriteFunc = enableWriteFunc;
            if (enableWriteFunc != null) {
                IsEnableWritesEnabled = true;
            }

            mSectorData = "TODO: put real data here";
        }

        private void EnableWrites_Click(object sender, RoutedEventArgs e) {
            Debug.WriteLine("click");
            if (mEnableWriteFunc == null) {
                return;     // shouldn't be here
            }
            if (!mEnableWriteFunc()) {
                MessageBox.Show(this, "Failed to enable write access", "Whoops",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            IsWriteEnabled = true;
            Debug.WriteLine("enabled");
        }

        private void ReadButton_Click(object sender, RoutedEventArgs e) {
            Debug.WriteLine("Read!");
        }

        private void WriteButton_Click(object sender, RoutedEventArgs e) {
            Debug.WriteLine("Write!");
        }
    }
}
