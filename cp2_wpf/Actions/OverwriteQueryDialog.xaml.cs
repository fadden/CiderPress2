/*
 * Copyright 2024 faddenSoft
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
using System.Windows;

using AppCommon;
using CommonUtil;

namespace cp2_wpf.Actions {
    /// <summary>
    /// File overwrite confirmation dialog implementation.
    /// </summary>
    public partial class OverwriteQueryDialog : Window {
        /// <summary>
        /// User selection.  Will be Overwrite or Skip.  The caller is responsible for
        /// setting Cancel if the dialog is cancelled.
        /// </summary>
        public CallbackFacts.Results Result { get; private set; }

        /// <summary>
        /// Current value of "use for all conflicts" checkbox.  (By definition this is false
        /// if this dialog is being used.)
        /// </summary>
        public bool UseForAll { get; set; }

        //
        // Dialog pieces.
        //

        public string NewFileName { get; set; }
        public string NewDirName { get; set; }
        public string NewModWhen { get; set; }
        public string ExistFileName { get; set; }
        public string ExistDirName { get; set; }
        public string ExistModWhen { get; set; }


        public OverwriteQueryDialog(Window parent, CallbackFacts facts) {
            InitializeComponent();
            Owner = parent;
            DataContext = this;

            NewFileName = PathName.GetFileName(facts.NewPathName, facts.NewDirSep);
            string ndn = PathName.GetDirectoryName(facts.NewPathName, facts.NewDirSep);
            if (!string.IsNullOrEmpty(ndn)) {
                NewDirName = "Directory: " + ndn;
            } else {
                NewDirName = string.Empty;
            }
            if (TimeStamp.IsValidDate(facts.NewModWhen)) {
                NewModWhen = "Modified: " + facts.NewModWhen.ToString();
            } else {
                NewModWhen = "Modified: (unknown)";
            }

            ExistFileName = PathName.GetFileName(facts.OrigPathName, facts.OrigDirSep);
            string edn = PathName.GetDirectoryName(facts.OrigPathName, facts.OrigDirSep);
            if (!string.IsNullOrEmpty(edn)) {
                ExistDirName = "Directory: " + edn;
            } else {
                ExistDirName = string.Empty;
            }
            if (TimeStamp.IsValidDate(facts.OrigModWhen)) {
                ExistModWhen = "Modified: " + facts.OrigModWhen.ToString();
            } else {
                ExistModWhen = "Modified: (unknown)";
            }
        }

        private void Replace_Click(object sender, RoutedEventArgs e) {
            Result = CallbackFacts.Results.Overwrite;
            DialogResult = true;
        }

        private void Skip_Click(object sender, RoutedEventArgs e) {
            Result = CallbackFacts.Results.Skip;
            DialogResult = true;
        }
    }
}
