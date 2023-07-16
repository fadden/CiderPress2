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
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;

using static DiskArc.Defs;

namespace cp2_wpf {
    /// <summary>
    /// Query file archive creation parameters.
    /// </summary>
    public partial class CreateFileArchive : Window, INotifyPropertyChanged {
        // INotifyPropertyChanged implementation
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string propertyName = "") {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public FileKind Kind { get; private set; }

        public bool IsChecked_Binary2 {
            get { return (Kind == FileKind.Binary2); }
            set {
                if (value) {
                    Kind = FileKind.Binary2;
                }
                OnPropertyChanged();
            }
        }

        public bool IsChecked_NuFX {
            get { return (Kind == FileKind.NuFX); }
            set {
                if (value) {
                    Kind = FileKind.NuFX;
                }
                OnPropertyChanged();
            }
        }

        public bool IsChecked_Zip {
            get { return (Kind == FileKind.Zip); }
            set {
                if (value) {
                    Kind = FileKind.Zip;
                }
                OnPropertyChanged();
            }
        }

        public CreateFileArchive(Window owner) {
            InitializeComponent();
            Owner = owner;
            DataContext = this;

            // Set initial value.
            FileKind kind = AppSettings.Global.GetEnum(AppSettings.NEW_ARC_MODE, FileKind.NuFX);
            switch (kind) {
                case FileKind.Binary2:
                case FileKind.Zip:
                case FileKind.NuFX:
                    Kind = kind;
                    break;
                default:
                    Debug.Assert(false);
                    Kind = FileKind.NuFX;
                    break;
            }
        }

        private void OkButton_Click(object sender, RoutedEventArgs e) {
            // Save default for next time.
            AppSettings.Global.SetEnum(AppSettings.NEW_ARC_MODE, Kind);
            DialogResult = true;
        }
    }
}
