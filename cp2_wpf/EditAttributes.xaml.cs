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
using DiskArc;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace cp2_wpf {
    /// <summary>
    /// File entry attribute editor.
    /// </summary>
    public partial class EditAttributes : Window, INotifyPropertyChanged {
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

        private delegate bool IsValidDirNameFunc(string name);
        private IsValidDirNameFunc mIsValidFunc;

        /// <summary>
        /// Filename string.
        /// </summary>
        public string FileName {
            get { return mFileName; }
            set { mFileName = value; OnPropertyChanged(); UpdateControls(); }
        }
        private string mFileName;

        // [] filename
        //    - list rules, flag non-unique
        //    - show dir name separator char
        // [] dates (disabled for DOS)
        //    - create date (DatePicker class)
        //    - mod date
        //    - show valid date ranges, explain limitations
        // [] ProDOS file type:
        //    - file type popup with strings; can enter string or $xx
        //      - instructions under
        //    - aux type as $xxxx
        //    - show type description string (type+aux -> str)
        //    - available on HFS: modifies HFS type; need to show as single group?
        // [] HFS file type:
        //    - checkbox for optional situations (NuFX, ProDOS ext)
        //    - input as 4-char or $xxxxxxxx
        // [] access
        //    - locked/unlocked for DOS/HFS, disabled for Pascal
        //    - six checkboxes for ProDOS
        // [] comment
        //    - only for NuFX, Zip
        //
        // SPECIAL:
        // - ProDOS dir type change not allowed
        // - HFS dir type change not possible
        //
        // Characteristics needs:
        // - date range limitation string; do we want min/max date values?  Maybe define in
        //   TimeStamp, copy into Characteristics
        // - filename limitations for IArchive

        public FileAttribs NewAttribs { get; } = new FileAttribs();

        private object mArchiveOrFileSystem;
        private IFileEntry mFileEntry;


        public EditAttributes(Window parent, object archiveOrFileSystem, IFileEntry entry,
                FileAttribs attribs) {
            InitializeComponent();
            Owner = parent;
            DataContext = this;

            mArchiveOrFileSystem = archiveOrFileSystem;
            mFileEntry = entry;

            mFileName = entry.FileName;

            // TODO: target-specific setup
            mIsValidFunc = delegate (string arg) { return false; };

            UpdateControls();
        }

        /// <summary>
        /// When window finishes rendering, put the focus on the filename text box.
        /// </summary>
        private void Window_ContentRendered(object sender, EventArgs e) {
            fileNameTextBox.SelectAll();
            fileNameTextBox.Focus();
        }

        /// <summary>
        /// Updates the controls when a change has been made.
        /// </summary>
        private void UpdateControls() {
            bool nameOkay = mIsValidFunc(mFileName);
            SyntaxRulesForeground = nameOkay ? mDefaultLabelColor : mErrorLabelColor;

            IsValid = nameOkay;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e) {
            // TODO: fill out FileAttribs object

            DialogResult = true;
        }
    }
}
