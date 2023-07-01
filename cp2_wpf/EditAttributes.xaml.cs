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

using DiskArc;
using DiskArc.FS;

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

        public FileAttribs NewAttribs { get; private set; } = new FileAttribs();

        private object mArchiveOrFileSystem;
        private IFileEntry mFileEntry;
        private FileAttribs mOldAttribs;


        //
        // Filename.
        //

        public string SyntaxRulesText {
            get { return mSyntaxRulesText; }
            set { mSyntaxRulesText = value; OnPropertyChanged(); }
        }
        private string mSyntaxRulesText;

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

        public string DirSepText { get; private set; }
        public Visibility DirSepTextVisibility { get; private set; }
        private const string DIR_SEP_CHAR_FMT = "\u2022 Directory separator character is '{0}'.";

        private delegate bool IsValidFileNameFunc(string name);
        private IsValidFileNameFunc mIsValidFunc;

        /// <summary>
        /// Filename string.
        /// </summary>
        public string FileName {
            get { return mFileName; }
            set { mFileName = value; OnPropertyChanged(); UpdateControls(); }
        }
        private string mFileName;

        //
        // Dates.
        //

        // [] dates (disabled for DOS)
        //    - create date (DatePicker class)
        //    - mod date
        //    - show valid date ranges, explain limitations

        // Characteristics needs:
        // - date range limitation string; do we want min/max date values?  Maybe define in
        //   TimeStamp, copy into Characteristics


        //
        // File types.
        //

        // [] ProDOS file type:
        //    - file type popup with strings; can enter string or $xx
        //      - instructions under
        //    - aux type as $xxxx
        //    - show type description string (type+aux -> str)
        //    - available on HFS: modifies HFS type; need to show as single group?
        // [] HFS file type:
        //    - checkbox for optional situations (NuFX, ProDOS ext)
        //    - input as 4-char or $xxxxxxxx
        // SPECIAL:
        // - ProDOS dir type change not allowed
        // - HFS dir type change not possible
        //

        /// <summary>
        /// ProDOS file type string.
        /// </summary>
        public string ProTypeStr {
            get { return mProTypeStr; }
            set { mProTypeStr = value; OnPropertyChanged(); UpdateControls(); }
        }
        private string mProTypeStr = "???";

        //
        // Access flags.
        //

        // [] access
        //    - locked/unlocked for DOS/HFS, disabled for Pascal
        //    - six checkboxes for ProDOS

        //
        // Comment.
        //

        // [] comment
        //    - only for NuFX, Zip
        //


        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="parent">Parent window.</param>
        /// <param name="archiveOrFileSystem">IArchive or IFileSystem object.</param>
        /// <param name="entry">File entry object.</param>
        /// <param name="attribs">Current file attributes, from <paramref name="entry"/> or
        ///   MacZip header contents.</param>
        public EditAttributes(Window parent, object archiveOrFileSystem, IFileEntry entry,
                FileAttribs attribs) {
            InitializeComponent();
            Owner = parent;
            DataContext = this;

            mArchiveOrFileSystem = archiveOrFileSystem;
            mFileEntry = entry;
            mOldAttribs = attribs;

            if (entry is DOS_FileEntry && entry.IsDirectory) {
                // The filename is formatted as "DOS-nnn", but we just want the number.
                mFileName = ((DOS)archiveOrFileSystem).VolumeNum.ToString("D3");
            } else {
                mFileName = entry.FileName;
            }

            if (archiveOrFileSystem is IArchive) {
                IArchive arc = (IArchive)archiveOrFileSystem;
                mIsValidFunc = arc.IsValidFileName;
                mSyntaxRulesText = "\u2022 " + arc.Characteristics.FileNameSyntaxRules;
                if (arc.Characteristics.DefaultDirSep != IFileEntry.NO_DIR_SEP) {
                    DirSepText = string.Format(DIR_SEP_CHAR_FMT, arc.Characteristics.DefaultDirSep);
                    DirSepTextVisibility = Visibility.Visible;
                } else {
                    DirSepText = string.Empty;
                    DirSepTextVisibility = Visibility.Hidden;
                }
            } else if (archiveOrFileSystem is IFileSystem) {
                IFileSystem fs = (IFileSystem)archiveOrFileSystem;
                if (entry.IsDirectory && entry.ContainingDir == IFileEntry.NO_ENTRY) {
                    // Volume Directory.
                    mIsValidFunc = fs.IsValidVolumeName;
                    mSyntaxRulesText = "\u2022 " + fs.Characteristics.VolumeNameSyntaxRules;
                } else {
                    mIsValidFunc = fs.IsValidFileName;
                    mSyntaxRulesText = "\u2022 " + fs.Characteristics.FileNameSyntaxRules;
                }
                DirSepText = string.Empty;
                DirSepTextVisibility = Visibility.Hidden;
            } else {
                throw new NotImplementedException("Can't edit " + archiveOrFileSystem);
            }

            mProTypeStr = "$" + attribs.FileType.ToString("x2");

            UpdateControls();
        }

        /// <summary>
        /// When window finishes rendering, put the focus on the filename text box, with all of
        /// the text selected.
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

            bool notUnique = false;
            if (mArchiveOrFileSystem is IArchive) {
                IArchive arc = (IArchive)mArchiveOrFileSystem;
                // Check name for uniqueness.
                if (arc.TryFindFileEntry(mFileName, out IFileEntry entry) &&
                        entry != mFileEntry) {
                    notUnique = true;
                }
            } else {
                IFileSystem fs = (IFileSystem)mArchiveOrFileSystem;
                if (mFileEntry.ContainingDir != IFileEntry.NO_ENTRY) {
                    // Not editing the volume dir attributes.  Check name for uniqueness.
                    if (fs.TryFindFileEntry(mFileEntry.ContainingDir, mFileName,
                            out IFileEntry entry) && entry != mFileEntry) {
                        notUnique = true;
                    }
                }
            }

            UniqueNameForeground = notUnique ? mErrorLabelColor : mDefaultLabelColor;

            IsValid = nameOkay && !notUnique;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e) {
            NewAttribs = new FileAttribs(mOldAttribs);
            NewAttribs.FullPathName = mFileName;
            // TODO: fill out remaining fields

            DialogResult = true;
        }
    }
}
