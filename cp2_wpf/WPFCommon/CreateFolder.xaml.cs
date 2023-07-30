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
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;

namespace cp2_wpf.WPFCommon {
    /// <summary>
    /// Ask the user for the name of a new folder, then create it.
    /// </summary>
    public partial class CreateFolder : Window, INotifyPropertyChanged {
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
        /// String with the new filename.
        /// </summary>
        public string NewFileName {
            get { return mNewFileName; }
            set {
                mNewFileName = value;
                OnPropertyChanged();
                UpdateControls();
            }
        }
        private string mNewFileName = "New folder";

        private string mDirectoryPath;


        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="owner">Parent window.</param>
        /// <param name="directoryPath">Directory where new folder will be created.</param>
        public CreateFolder(Window owner, string directoryPath) {
            InitializeComponent();
            Owner = owner;
            DataContext = this;

            Debug.Assert(Directory.Exists(directoryPath));
            mDirectoryPath = directoryPath;

            UpdateControls();
        }

        /// <summary>
        /// When window finishes rendering, put the focus on the directory name text box, with all
        /// of the text selected.
        /// </summary>
        private void Window_ContentRendered(object sender, EventArgs e) {
            newFileNameTextBox.SelectAll();
            newFileNameTextBox.Focus();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e) {
            try {
                string pathName = Path.Combine(mDirectoryPath, NewFileName);
                // This will succeed if the directory already exists.  This seems fine.
                Directory.CreateDirectory(pathName);
                DialogResult = true;
            } catch (Exception ex) {
                MessageBox.Show(this, "Error: " + ex.Message, "Unable to create folder",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void UpdateControls() {
            // We could do a syntax check here, but it's safer to just let Windows do it.  Only
            // disable the OK button if the field is completely empty.
            IsValid = !string.IsNullOrEmpty(NewFileName);
        }
    }
}
