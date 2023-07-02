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
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using AppCommon;
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
        private IFileEntry mADFEntry;
        private FileAttribs mOldAttribs;

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="parent">Parent window.</param>
        /// <param name="archiveOrFileSystem">IArchive or IFileSystem object.</param>
        /// <param name="entry">File entry object.</param>
        /// <param name="adfEntry">For MacZip, the ADF header entry; otherwise NO_ENTRY.</param>
        /// <param name="attribs">Current file attributes, from <paramref name="entry"/> or
        ///   MacZip header contents.</param>
        public EditAttributes(Window parent, object archiveOrFileSystem, IFileEntry entry,
                IFileEntry adfEntry, FileAttribs attribs) {
            InitializeComponent();
            Owner = parent;
            DataContext = this;

            mArchiveOrFileSystem = archiveOrFileSystem;
            mFileEntry = entry;
            mADFEntry = adfEntry;
            mOldAttribs = attribs;

            if (entry is DOS_FileEntry && entry.IsDirectory) {
                // The DOS volume name is formatted as "DOS-nnn", but we just want the number.
                mFileName = ((DOS)archiveOrFileSystem).VolumeNum.ToString("D3");
            } else {
                mFileName = attribs.FullPathName;
            }

            NewAttribs = new FileAttribs(mOldAttribs);
            NewAttribs.FullPathName = mFileName;

            if (archiveOrFileSystem is IArchive) {
                IArchive arc = (IArchive)archiveOrFileSystem;
                mIsValidFunc = arc.IsValidFileName;
                mSyntaxRulesText = "\u2022 " + arc.Characteristics.FileNameSyntaxRules;
                if (arc.Characteristics.DefaultDirSep != IFileEntry.NO_DIR_SEP) {
                    DirSepText = string.Format(DIR_SEP_CHAR_FMT, arc.Characteristics.DefaultDirSep);
                    DirSepTextVisibility = Visibility.Visible;
                } else {
                    DirSepText = string.Empty;
                    DirSepTextVisibility = Visibility.Collapsed;
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
                DirSepTextVisibility = Visibility.Collapsed;
            } else {
                throw new NotImplementedException("Can't edit " + archiveOrFileSystem);
            }

            PrepareProTypeList();
            ProTypeDescString = FileTypes.GetDescription(attribs.FileType, attribs.AuxType);
            ProAuxString = attribs.AuxType.ToString("X4");

            UpdateControls();
        }


        private void Window_Loaded(object sender, RoutedEventArgs e) {
            Loaded_FileType();
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
            //
            // Filename.
            //

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

            //
            // ProDOS file and aux type.
            // We're currently always picking the file type from a list, so it's always valid.
            //
            ProAuxForeground = mProAuxValid ? mDefaultLabelColor : mErrorLabelColor;
            IsValid &= mProAuxValid;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e) {
            DialogResult = true;
        }


        #region Filename

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

        #endregion Filename

        #region File Type

        //
        // File types.
        //

        public Visibility ProTypeVisibility { get; private set; } = Visibility.Visible;
        public Visibility HFSTypeVisibility { get; private set; } = Visibility.Visible;

        public class ProTypeListItem {
            public string Label { get; private set; }
            public byte Value { get; private set; }

            public ProTypeListItem(string label, byte value) {
                Label = label;
                Value = value;
            }
        }

        public List<ProTypeListItem> ProTypeList { get; } = new List<ProTypeListItem>();

        public bool IsProTypeListEnabled { get; private set; } = true;

        public string ProTypeDescString {
            get { return mProTypeDescString; }
            set { mProTypeDescString = value; OnPropertyChanged(); }
        }
        public string mProTypeDescString = string.Empty;

        // Aux type input field (0-4 hex chars).
        public string ProAuxString {
            get { return mProAuxString; }
            set {
                mProAuxValid = true;
                mProAuxString = value;
                OnPropertyChanged();
                if (string.IsNullOrEmpty(ProAuxString)) {
                    NewAttribs.AuxType = 0;
                } else {
                    try {
                        NewAttribs.AuxType = Convert.ToUInt16(ProAuxString, 16);
                    } catch (Exception) {       // ArgumentException or FormatException
                        mProAuxValid = false;
                    }
                }
                UpdateControls();
            }
        }
        private string mProAuxString;
        private bool mProAuxValid;

        // Aux type label color, set to red on error.
        public Brush ProAuxForeground {
            get { return mProAuxForeground; }
            set { mProAuxForeground = value; OnPropertyChanged(); }
        }
        private Brush mProAuxForeground = SystemColors.WindowTextBrush;

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
        // THOUGHT:
        // - HFS types of zero map to an empty input field, rather than NULs
        // - modifying one field potentially changes the other; if chars are edited then
        //   blank out hex, if hex is edited blank out chars, until a valid entry is made;
        //   then fill out the other with equivalent data

        private static readonly byte[] DOS_TYPES = {
            FileAttribs.FILE_TYPE_TXT,      // T
            FileAttribs.FILE_TYPE_INT,      // I
            FileAttribs.FILE_TYPE_BAS,      // A
            FileAttribs.FILE_TYPE_BIN,      // B
            FileAttribs.FILE_TYPE_F2,       // S
            FileAttribs.FILE_TYPE_REL,      // R
            FileAttribs.FILE_TYPE_F3,       // AA
            FileAttribs.FILE_TYPE_F4        // BB
        };

        /// <summary>
        /// Prepares the DOS/ProDOS type pop-up.
        /// </summary>
        private void PrepareProTypeList() {
            if (mFileEntry is DOS_FileEntry) {
                foreach (byte type in DOS_TYPES) {
                    string abbrev = FileTypes.GetDOSTypeAbbrev(type);
                    ProTypeList.Add(new ProTypeListItem(abbrev, type));
                }
            } else if (mFileEntry.HasProDOSTypes || mADFEntry != IFileEntry.NO_ENTRY) {
                for (int type = 0; type < 256; type++) {
                    string abbrev = FileTypes.GetFileTypeAbbrev(type);
                    if (abbrev[0] == '$') {
                        abbrev = "???";
                    }
                    string label = abbrev + " $" + type.ToString("X2");
                    ProTypeList.Add(new ProTypeListItem(label, (byte)type));
                }

                IsProTypeListEnabled = (!mFileEntry.IsDirectory);
            } else {
                IsProTypeListEnabled = false;
            }
        }

        private void Loaded_FileType() {
            // Set the selected entry in the DOS/ProDOS type pop-up.  If ProDOS types aren't
            // relevant, the list will be empty and this won't do anything.
            for (int i = 0; i < ProTypeList.Count; i++) {
                if (ProTypeList[i].Value == NewAttribs.FileType) {
                    proTypeCombo.SelectedIndex = i;
                    break;
                }
            }
            if (ProTypeList.Count != 0 && proTypeCombo.SelectedIndex < 0) {
                Debug.Assert(false, "no ProDOS type matched");
                proTypeCombo.SelectedIndex = 0;
            }
        }

        private void ProTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) {
            int selIndex = proTypeCombo.SelectedIndex;
            if (selIndex >= 0) {
                NewAttribs.FileType = ProTypeList[selIndex].Value;
                Debug.WriteLine("ProDOS file type: $" + NewAttribs.FileType.ToString("x2"));
            }
        }

        #endregion File Type


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

    }
}
