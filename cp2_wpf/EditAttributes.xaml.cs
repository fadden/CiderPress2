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
using CommonUtil;
using DiskArc;
using DiskArc.Arc;
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

        /// <summary>
        /// File attributes after edits.  The filename will always be in FullPathName, even
        /// for disk images.
        /// </summary>
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

            NewAttribs = new FileAttribs(mOldAttribs);
            if (entry is DOS_FileEntry && entry.IsDirectory) {
                // The DOS volume name is formatted as "DOS-nnn", but we just want the number.
                NewAttribs.FullPathName = ((DOS)archiveOrFileSystem).VolumeNum.ToString("D3");
            } else if (archiveOrFileSystem is IArchive) {
                NewAttribs.FullPathName = attribs.FullPathName;
            } else {
                NewAttribs.FullPathName = attribs.FileNameOnly;
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

            PrepareHFSTypes();

            PrepareTimestamps();

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

            bool nameOkay = mIsValidFunc(NewAttribs.FullPathName);
            SyntaxRulesForeground = nameOkay ? mDefaultLabelColor : mErrorLabelColor;

            bool notUnique = false;
            if (mArchiveOrFileSystem is IArchive) {
                IArchive arc = (IArchive)mArchiveOrFileSystem;
                // Check name for uniqueness.
                if (arc.TryFindFileEntry(NewAttribs.FullPathName, out IFileEntry entry) &&
                        entry != mFileEntry) {
                    notUnique = true;
                }
            } else {
                IFileSystem fs = (IFileSystem)mArchiveOrFileSystem;
                if (mFileEntry.ContainingDir != IFileEntry.NO_ENTRY) {
                    // Not editing the volume dir attributes.  Check name for uniqueness.
                    if (fs.TryFindFileEntry(mFileEntry.ContainingDir, NewAttribs.FullPathName,
                            out IFileEntry entry) && entry != mFileEntry) {
                        notUnique = true;
                    }
                }
            }

            UniqueNameForeground = notUnique ? mErrorLabelColor : mDefaultLabelColor;

            IsValid = nameOkay && !notUnique;

            // ProDOS file and aux type.
            // We're currently always picking the file type from a list, so it's always valid.
            ProAuxForeground = mProAuxValid ? mDefaultLabelColor : mErrorLabelColor;
            IsValid &= mProAuxValid;

            // HFS file type and creator.
            HFSTypeForeground = mHFSTypeValid ? mDefaultLabelColor : mErrorLabelColor;
            HFSCreatorForeground = mHFSCreatorValid ? mDefaultLabelColor : mErrorLabelColor;
            IsValid &= mHFSTypeValid & mHFSCreatorValid;
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
            get { return NewAttribs.FullPathName; }
            set { NewAttribs.FullPathName = value; OnPropertyChanged(); UpdateControls(); }
        }

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

        /// <summary>
        /// List of suitable types from the ProDOS type list.  This is the ItemsSource for the
        /// combo box.
        /// </summary>
        public List<ProTypeListItem> ProTypeList { get; } = new List<ProTypeListItem>();

        /// <summary>
        /// True if the ProDOS type list is enabled.  It will be visible but disabled in
        /// certain circumstances, such as when editing attributes for a directory entry.
        /// </summary>
        public bool IsProTypeListEnabled { get; private set; } = true;

        public string ProTypeDescString {
            get { return mProTypeDescString; }
            set { mProTypeDescString = value; OnPropertyChanged(); }
        }
        public string mProTypeDescString = string.Empty;

        /// <summary>
        /// Aux type input field (0-4 hex chars).  Must be a valid hex value or empty string.
        /// </summary>
        public string ProAuxString {
            get { return mProAuxString; }
            set {
                mProAuxString = value;
                mProAuxValid = true;
                OnPropertyChanged();
                if (string.IsNullOrEmpty(value)) {
                    NewAttribs.AuxType = 0;
                } else {
                    try {
                        NewAttribs.AuxType = Convert.ToUInt16(value, 16);
                    } catch (Exception) {       // ArgumentException or FormatException
                        mProAuxValid = false;
                    }
                }
                UpdateControls();
            }
        }
        private string mProAuxString = string.Empty;
        private bool mProAuxValid;

        // Aux type label color, set to red on error.
        public Brush ProAuxForeground {
            get { return mProAuxForeground; }
            set { mProAuxForeground = value; OnPropertyChanged(); }
        }
        private Brush mProAuxForeground = SystemColors.WindowTextBrush;

        public string HFSTypeCharsString {
            get { return mHFSTypeCharsString; }
            set {
                mHFSTypeCharsString = value;
                OnPropertyChanged();
                mHFSTypeHexString = SetHexFromChars(value, out uint newNum, out mHFSTypeValid);
                OnPropertyChanged(nameof(HFSTypeHexString));
                NewAttribs.HFSFileType = newNum;
                UpdateControls();
            }
        }
        private string mHFSTypeCharsString = string.Empty;

        public string HFSTypeHexString {
            get { return mHFSTypeHexString; }
            set {
                mHFSTypeHexString = value;
                OnPropertyChanged();
                mHFSTypeCharsString = SetCharsFromHex(value, out uint newNum, out mHFSTypeValid);
                OnPropertyChanged(nameof(HFSTypeCharsString));
                NewAttribs.HFSFileType = newNum;
                UpdateControls();
            }
        }
        private string mHFSTypeHexString = string.Empty;
        private bool mHFSTypeValid;

        public Brush HFSTypeForeground {
            get { return mHFSTypeForeground; }
            set { mHFSTypeForeground = value; OnPropertyChanged(); }
        }
        private Brush mHFSTypeForeground = SystemColors.WindowTextBrush;

        public string HFSCreatorCharsString {
            get { return mHFSCreatorCharsString; }
            set {
                mHFSCreatorCharsString = value;
                OnPropertyChanged();
                mHFSCreatorHexString =
                    SetHexFromChars(value, out uint newNum, out mHFSCreatorValid);
                OnPropertyChanged(nameof(HFSCreatorHexString));
                NewAttribs.HFSCreator = newNum;
                UpdateControls();
            }
        }
        private string mHFSCreatorCharsString = string.Empty;

        public string HFSCreatorHexString {
            get { return mHFSCreatorHexString; }
            set {
                mHFSCreatorHexString = value;
                OnPropertyChanged();
                mHFSCreatorCharsString =
                    SetCharsFromHex(value, out uint newNum, out mHFSCreatorValid);
                OnPropertyChanged(nameof(HFSCreatorCharsString));
                NewAttribs.HFSCreator = newNum;
                UpdateControls();
            }
        }
        private string mHFSCreatorHexString = string.Empty;
        private bool mHFSCreatorValid;

        public Brush HFSCreatorForeground {
            get { return mHFSCreatorForeground; }
            set { mHFSCreatorForeground = value; OnPropertyChanged(); }
        }
        private Brush mHFSCreatorForeground = SystemColors.WindowTextBrush;

        /// <summary>
        /// Computes the hexadecimal field when the 4-char field changes.
        /// </summary>
        /// <param name="charValue">New character constant.</param>
        /// <param name="newNum">Result: numeric value.</param>
        /// <param name="isValid">Result: true if string is valid.</param>
        /// <returns>Hexadecimal string.</returns>
        private static string SetHexFromChars(string charValue, out uint newNum, out bool isValid) {
            string newHexStr;
            isValid = true;
            if (string.IsNullOrEmpty(charValue)) {
                newNum = 0;
                newHexStr = string.Empty;
            } else if (charValue.Length == 4) {
                // set hex value
                newNum = MacChar.IntifyMacConstantString(charValue);
                newHexStr = newNum.ToString("X8");
            } else {
                // incomplete string, erase hex value
                newNum = 0;
                newHexStr = string.Empty;
                isValid = false;
            }
            return newHexStr;
        }

        /// <summary>
        /// Computes the 4-char field value when the hexadecimal field changes.
        /// </summary>
        /// <param name="hexStr">New hex string.</param>
        /// <param name="newNum">Result: numeric value.</param>
        /// <param name="isValid">Result: true if string is valid.</param>
        /// <returns>Character string.</returns>
        private static string SetCharsFromHex(string hexStr, out uint newNum, out bool isValid) {
            string newCharStr;
            isValid = true;
            if (string.IsNullOrEmpty(hexStr)) {
                newCharStr = string.Empty;
                newNum = 0;
            } else {
                try {
                    newNum = Convert.ToUInt32(hexStr, 16);
                    newCharStr = MacChar.StringifyMacConstant(newNum);
                } catch (Exception) {       // ArgumentException or FormatException
                    isValid = false;
                    newNum = 0;
                    newCharStr = string.Empty;
                }
            }
            return newCharStr;
        }

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
                ProTypeVisibility = Visibility.Collapsed;
            }
        }

        /// <summary>
        /// Initializes the fields during construction.
        /// </summary>
        private void PrepareHFSTypes() {
            if (mADFEntry == IFileEntry.NO_ENTRY && !mFileEntry.HasHFSTypes) {
                // Not MacZip AppleDouble, doesn't have HFS types.
                HFSTypeVisibility = Visibility.Collapsed;
            }

            // Set the hex string; automatically sets the 4-char string and "valid" flag.
            if (NewAttribs.HFSFileType == 0) {
                HFSTypeHexString = string.Empty;
            } else {
                HFSTypeHexString = NewAttribs.HFSFileType.ToString("X8");
            }
            if (NewAttribs.HFSCreator == 0) {
                HFSCreatorHexString = string.Empty;
            } else {
                HFSCreatorHexString = NewAttribs.HFSCreator.ToString("X8");
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

        public Visibility TimestampVisibility { get; private set; } = Visibility.Visible;

        public DateTime TimestampStart { get; set; }
        public DateTime TimestampEnd { get; set; }

        public DateTime? CreateDate {
            get { return mCreateDate; }
            set {
                mCreateDate = value;
                OnPropertyChanged();
                // TODO: check validity, merge with time and set property
            }
        }
        private DateTime? mCreateDate;

        // TODO: time value


        /// <summary>
        /// Prepares properties during construction.
        /// </summary>
        private void PrepareTimestamps() {
            if (mArchiveOrFileSystem is IArchive) {
                if (mADFEntry == IFileEntry.NO_ENTRY) {
                    IArchive arc = (IArchive)mArchiveOrFileSystem;
                    TimestampStart = arc.Characteristics.TimeStampStart;
                    TimestampEnd = arc.Characteristics.TimeStampEnd;
                } else {
                    // MacZip AppleDouble
                    TimestampStart = AppleSingle.SCharacteristics.TimeStampStart;
                    TimestampEnd = AppleSingle.SCharacteristics.TimeStampEnd;
                }
            } else {
                IFileSystem fs = (IFileSystem)mArchiveOrFileSystem;
                TimestampStart = fs.Characteristics.TimeStampStart;
                TimestampEnd = fs.Characteristics.TimeStampEnd;
            }
            Debug.WriteLine("Timestamp date range: " + TimestampStart + " - " + TimestampEnd);

            if (TimeStamp.IsValidDate(NewAttribs.CreateWhen)) {
                mCreateDate = NewAttribs.CreateWhen;
            }
        }


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
