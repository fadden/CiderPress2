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
using System.Windows.Input;
using System.Windows.Media;

using DiskArc;
using static DiskArc.IMetadata;

namespace cp2_wpf {
    /// <summary>
    /// Interaction logic for EditMetadata.xaml
    /// </summary>
    public partial class EditMetadata : Window, INotifyPropertyChanged {
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

        public string KeyText { get; set; }

        public string ValueText {
            get { return mValueText; }
            set { mValueText = value; OnPropertyChanged(); UpdateControls(); }
        }
        private string mValueText;

        public string DescriptionText { get; private set; }
        public string ValueSyntaxText { get; private set; }

        private Brush mDefaultLabelColor = SystemColors.WindowTextBrush;
        private Brush mErrorLabelColor = Brushes.Red;

        public Brush ValueSyntaxForeground {
            get { return mValueSyntaxForeground; }
            set { mValueSyntaxForeground = value; OnPropertyChanged(); }
        }
        private Brush mValueSyntaxForeground = SystemColors.WindowTextBrush;

        private IMetadata mMetaObj;
        private MetaEntry mMetaEntry;


        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="owner">Parent window.</param>
        /// <param name="metaObj">Metadata holder.</param>
        /// <param name="key">Key of entry to edit.</param>
        public EditMetadata(Window owner, IMetadata metaObj, string key) {
            InitializeComponent();
            Owner = owner;
            DataContext = this;

            mMetaObj = metaObj;
            MetaEntry? entry = metaObj.GetMetaEntry(key);
            if (entry == null) {
                Debug.Assert(false);
                throw new ArgumentException("couldn't find MetaEntry");
            }
            mMetaEntry = entry;

            KeyText = key;
            mValueText = metaObj.GetMetaValue(key, false)!;
            DescriptionText = entry.Description;

            if (!entry.CanEdit) {
                ValueSyntaxText = "This entry can't be edited.";
                valueTextBox.IsReadOnly = true;
            } else {
                ValueSyntaxText = entry.ValueSyntax;
            }
            UpdateControls();
        }

        /// <summary>
        /// When window finishes rendering, put the focus on the value text box, with all
        /// of the text selected.
        /// </summary>
        private void Window_ContentRendered(object sender, EventArgs e) {
            if (mMetaEntry.CanEdit) {
                valueTextBox.SelectAll();
            }
            valueTextBox.Focus();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e) {
            DialogResult = true;
        }

        private void UpdateControls() {
            IsValid = mMetaObj.TestMetaValue(mMetaEntry.Key, ValueText);
            ValueSyntaxForeground = IsValid ? mDefaultLabelColor : mErrorLabelColor;
        }
    }
}
