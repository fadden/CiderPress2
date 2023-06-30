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
using System.Windows;
using System.Windows.Media;

using DiskArc;

namespace cp2_wpf {
    /// <summary>
    /// Create directory UI.
    /// </summary>
    public partial class CreateDirectory : Window, INotifyPropertyChanged {
        // INotifyPropertyChanged
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string propertyName = "") {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

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
        private string mNewFileName;

        /// <summary>
        /// Set to true when input is valid.  Controls whether the OK button is enabled.
        /// </summary>
        public bool IsValid {
            get { return mIsValid; }
            set { mIsValid = value; OnPropertyChanged(); }
        }
        private bool mIsValid;

        public string SyntaxRulesText {
            get { return mSyntaxRulesText; }
            set { mSyntaxRulesText = value; OnPropertyChanged(); }
        }
        private string mSyntaxRulesText;

        private Brush mDefaultLabelColor = SystemColors.WindowTextBrush;
        private Brush mErrorLabelColor = Brushes.Red;

        public Brush SyntaxRulesForeground {
            get { return mSyntaxRulesForeground; }
            set { mSyntaxRulesForeground = value; OnPropertyChanged(); }
        }
        private Brush mSyntaxRulesForeground = SystemColors.WindowTextBrush;

        public Brush UniqueNameForeground {
            get { return mUniqueNameForeground; }
            set { mUniqueNameForeground = value; OnPropertyChanged(); }
        }
        private Brush mUniqueNameForeground = SystemColors.WindowTextBrush;


        public delegate bool IsValidDirNameFunc(string name);

        private IFileSystem mFileSystem;
        private IFileEntry mContainingDir;
        private IsValidDirNameFunc mIsValidFunc;


        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="parent">Parent window.</param>
        /// <param name="fs">Filesystem object.</param>
        /// <param name="containingDir">Directory that will contain the new file entry.</param>
        /// <param name="func">Function for evaluating filename correctness.</param>
        /// <param name="syntaxRules">Human-readable syntax rules to display.</param>
        public CreateDirectory(Window parent, IFileSystem fs, IFileEntry containingDir,
                IsValidDirNameFunc func, string syntaxRules) {
            InitializeComponent();
            Owner = parent;
            DataContext = this;

            mFileSystem = fs;
            mContainingDir = containingDir;
            mIsValidFunc = func;
            mSyntaxRulesText = syntaxRules;

            mNewFileName = "NEW.DIR";
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

        /// <summary>
        /// Updates the controls when the text changes.
        /// </summary>
        private void UpdateControls() {
            bool nameOkay = mIsValidFunc(mNewFileName);
            SyntaxRulesForeground = nameOkay ? mDefaultLabelColor : mErrorLabelColor;

            bool exists = mFileSystem.TryFindFileEntry(mContainingDir, mNewFileName,
                    out IFileEntry existing);
            UniqueNameForeground = exists ? mErrorLabelColor : mDefaultLabelColor;

            IsValid = nameOkay && !exists;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e) {
            DialogResult = true;
        }
    }
}
