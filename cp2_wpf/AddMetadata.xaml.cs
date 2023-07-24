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
using System.Windows.Media;

using DiskArc;
using static DiskArc.IMetadata;

namespace cp2_wpf {
    /// <summary>
    /// Add a new metadata entry.
    /// </summary>
    public partial class AddMetadata : Window, INotifyPropertyChanged {
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
        /// Current value of the key text.
        /// </summary>
        public string KeyText {
            get { return mKeyText; }
            set { mKeyText = value; OnPropertyChanged(); UpdateControls(); }
        }
        private string mKeyText;

        /// <summary>
        /// Current value of the value text.
        /// </summary>
        public string ValueText {
            get { return mValueText; }
            set { mValueText = value; OnPropertyChanged(); UpdateControls(); }
        }
        private string mValueText = string.Empty;

        public string KeySyntaxText { get; private set; }
        public string ValueSyntaxText { get; private set; }


        private Brush mDefaultLabelColor = SystemColors.WindowTextBrush;
        private Brush mErrorLabelColor = Brushes.Red;

        public Brush KeySyntaxForeground {
            get { return mKeySyntaxForeground; }
            set { mKeySyntaxForeground = value; OnPropertyChanged(); }
        }
        private Brush mKeySyntaxForeground = SystemColors.WindowTextBrush;

        public Brush ValueSyntaxForeground {
            get { return mValueSyntaxForeground; }
            set { mValueSyntaxForeground = value; OnPropertyChanged(); }
        }
        private Brush mValueSyntaxForeground = SystemColors.WindowTextBrush;

        public Visibility NonUniqueVisibility {
            get { return mNonUniqueVisibility; }
            set { mNonUniqueVisibility = value; OnPropertyChanged(); }
        }
        private Visibility mNonUniqueVisibility;

        /// <summary>
        /// Metadata holder.
        /// </summary>
        private IMetadata mMetaObj;


        public AddMetadata(Window owner, IMetadata metaObj) {
            InitializeComponent();
            Owner = owner;
            DataContext = this;

            mMetaObj = metaObj;
            mKeyText = "meta:new_key";
            // If we support something other than WOZ, we'll need to fix this.
            KeySyntaxText =
                "Keys are comprised of ASCII letters, numbers, and the underscore ('_')." +
                Environment.NewLine +
                "WOZ metadata keys are prefixed with \"meta:\".";

            ValueSyntaxText = "WOZ values may have any characters except linefeed and tab.";

            UpdateControls();
        }

        private void Window_ContentRendered(object sender, EventArgs e) {
            keyTextBox.Select(5, mKeyText.Length - 5);
            keyTextBox.Focus();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e) {
            DialogResult = true;
        }

        private void UpdateControls() {
            bool isOkay = mMetaObj.TestMetaValue(KeyText, ValueText);
            bool isUnique = mMetaObj.GetMetaValue(KeyText, false) == null;
            IsValid = isOkay && isUnique;
            KeySyntaxForeground = isOkay ? mDefaultLabelColor : mErrorLabelColor;
            // We can't differentiate between bad keys and bad values, but it's pretty hard
            // to make a bad value.
            NonUniqueVisibility = isUnique ? Visibility.Collapsed : Visibility.Visible;

            //Debug.WriteLine("key=" + KeyText + " value=" + ValueText + " valid=" + IsValid);
        }
    }
}
