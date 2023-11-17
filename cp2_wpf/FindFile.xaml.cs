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
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;

namespace cp2_wpf {
    /// <summary>
    /// "Find file" dialog.
    /// </summary>
    public partial class FindFile : Window, INotifyPropertyChanged {
        // INotifyPropertyChanged
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string propertyName = "") {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// Find file request details.
        /// </summary>
        public class FindFileReq {
            public bool Forward { get; private set; }

            public FindFileReq(bool forward) {
                Forward = forward;
            }

            public override string ToString() {
                return "[FindReq: fwd=" + Forward + "]";
            }
        }
        public event FindFileRequested? FindRequested;
        public delegate void FindFileRequested(FindFileReq req);

        /// <summary>
        /// Filename to search for.
        /// </summary>
        public string FileName {
            get { return mFileName; }
            set { mFileName = value; OnPropertyChanged(); }
        }
        private string mFileName = string.Empty;


        public FindFile(MainWindow owner) {
            InitializeComponent();
            Owner = owner;
            DataContext = this;
        }

        private void FindPrev_Click(object sender, RoutedEventArgs e) {
            FindFileReq req = new FindFileReq(false);
            FindRequested!(req);
        }

        private void FindNext_Click(object sender, RoutedEventArgs e) {
            FindFileReq req = new FindFileReq(true);
            FindRequested!(req);
        }
    }
}
