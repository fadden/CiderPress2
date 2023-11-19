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
            public string FileName { get; private set; }
            public bool CurrentArchiveOnly { get; private set; }
            public bool Forward { get; private set; }

            public FindFileReq(string fileName, bool currentArchiveOnly, bool forward) {
                FileName = fileName;
                CurrentArchiveOnly = currentArchiveOnly;
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
            set { mFileName = value; OnPropertyChanged(); UpdateControls(); }
        }
        private string mFileName = sLastSearch;

        public bool IsCurrentArchiveOnlyChecked {
            get { return mIsCurrentArchiveOnlyChecked; }
            set { mIsCurrentArchiveOnlyChecked = value; OnPropertyChanged(); }
        }
        private bool mIsCurrentArchiveOnlyChecked = sCurrentArchiveOnly;

        public bool IsEnabled_PrevNext {
            get { return mIsEnabled_PrevNext; }
            set { mIsEnabled_PrevNext = value; OnPropertyChanged(); }
        }
        private bool mIsEnabled_PrevNext;

        // Keep track of the last configuration.
        private static string sLastSearch = string.Empty;
        private static bool sCurrentArchiveOnly = false;


        public FindFile(MainWindow owner) {
            InitializeComponent();
            Owner = owner;
            DataContext = this;

            UpdateControls();
        }

        /// <summary>
        /// When window finishes rendering, put the focus on the filename text box, with all
        /// of the text selected.
        /// </summary>
        private void Window_ContentRendered(object sender, EventArgs e) {
            fileNameTextBox.SelectAll();
            fileNameTextBox.Focus();
        }

        /// <summary>
        /// Saves current configuration in static variables, for use in the current session.
        /// </summary>
        private void SaveConfig() {
            sLastSearch = mFileName;
            sCurrentArchiveOnly = mIsCurrentArchiveOnlyChecked;
        }

        /// <summary>
        /// Updates controls after user input.
        /// </summary>
        private void UpdateControls() {
            IsEnabled_PrevNext = !string.IsNullOrEmpty(mFileName);
        }

        private void FindPrev_Click(object sender, RoutedEventArgs e) {
            FindFileReq req = new FindFileReq(mFileName, mIsCurrentArchiveOnlyChecked, false);
            FindRequested!(req);

            SaveConfig();
        }

        private void FindNext_Click(object sender, RoutedEventArgs e) {
            FindFileReq req = new FindFileReq(mFileName, mIsCurrentArchiveOnlyChecked, true);
            FindRequested!(req);

            SaveConfig();
        }
    }
}
