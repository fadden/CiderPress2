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
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using CommonUtil;
using cp2_wpf.WPFCommon;

namespace cp2_wpf {
    /// <summary>
    /// Present a list of physical disks and allow the user to choose one.
    /// </summary>
    public partial class SelectPhysicalDrive : Window, INotifyPropertyChanged {
        // INotifyPropertyChanged
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string propertyName = "") {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// Set to true when input is valid.  Controls whether the OK button is enabled.
        /// </summary>
        public bool IsValid {
            get { return mIsValid; }
            set { mIsValid = value; OnPropertyChanged(); }
        }
        private bool mIsValid;

        /// <summary>
        /// Read-only setting.
        /// </summary>
        public bool OpenReadOnly {
            get { return mOpenReadOnly; }
            set { mOpenReadOnly = value; OnPropertyChanged(); }
        }
        private bool mOpenReadOnly;

        /// <summary>
        /// Result: selected disk item.
        /// </summary>
        public PhysicalDriveAccess.DiskInfo? SelectedDisk { get; private set; }

        public class DiskItem {
            public PhysicalDriveAccess.DiskInfo Info { get; private set; }

            public string Label { get; private set; }
            public string FileName { get; private set; }
            public string MediaType { get; private set; }
            public string Size { get; private set; }
            public bool CanOpen { get; private set; }

            public DiskItem(PhysicalDriveAccess.DiskInfo info) {
                Info = info;

                Label = "Physical disk #" + info.Number;
                FileName = info.Name;
                MediaType = info.MediaType.ToString();
                Size = (info.Size / (1024 * 1024)).ToString("N0") + " MB";
                // Disallow access to fixed disks, on the assumption that those have host
                // data and we shouldn't be messing with them.  At the very least we want
                // to disallow access to device 0 (boot disk).
                CanOpen = (info.MediaType != PhysicalDriveAccess.DiskInfo.MediaTypes.Fixed);
            }
        }
        public ObservableCollection<DiskItem> DiskItems { get; set; } =
            new ObservableCollection<DiskItem>();


        public SelectPhysicalDrive(Window owner) {
            InitializeComponent();
            Owner = owner;
            DataContext = this;

            List<PhysicalDriveAccess.DiskInfo>? diskList = PhysicalDriveAccess.GetDiskList();
            if (diskList != null && diskList.Count > 0) {
                int best = 0;
                for (int i = 0; i < diskList.Count; i++) {
                    PhysicalDriveAccess.DiskInfo di = diskList[i];
                    DiskItems.Add(new DiskItem(di));
                    if (best == 0 &&
                            di.MediaType == PhysicalDriveAccess.DiskInfo.MediaTypes.Removable) {
                        best = i;
                    }
                }
                diskItemDataGrid.SelectedIndex = best;
            }

            mOpenReadOnly = AppSettings.Global.GetBool(AppSettings.PHYS_OPEN_READ_ONLY, true);
        }

        private void Window_ContentRendered(object sender, EventArgs e) {
            diskItemDataGrid.Focus();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e) {
            Finish();
        }
        private void DiskItems_MouseDoubleClick(object sender, MouseButtonEventArgs e) {
            DataGrid grid = (DataGrid)sender;
            if (!grid.GetClickRowColItem(e, out int row, out int col, out object? oitem)) {
                // Header or empty area; ignore.
                return;
            }
            DiskItem item = (DiskItem)oitem;
            if (item.CanOpen) {
                Debug.Assert(IsValid);
                Finish();
            }
        }
        private void Finish() {
            AppSettings.Global.SetBool(AppSettings.PHYS_OPEN_READ_ONLY, mOpenReadOnly);
            DiskItem item = (DiskItem)diskItemDataGrid.SelectedItem;
            SelectedDisk = item.Info;
            DialogResult = true;
        }

        private void DiskItems_SelectionChanged(object sender, SelectionChangedEventArgs e) {
            DataGrid grid = (DataGrid)sender;
            DiskItem item = (DiskItem)grid.SelectedItem;
            IsValid = item.CanOpen;
        }
    }
}
