/*
 * Copyright 2026 faddenSoft
 * Copyright 2026 Lydian Scale Software
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
using System.Diagnostics;
using System.Text.RegularExpressions;
using AppCommon;
using CommonUtil;
using DiskArc;
using DiskArc.Arc;
using DiskArc.FS;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace cp2_avalonia.ViewModels;

/// <summary>
/// ViewModel for the EditAttributes dialog.
/// </summary>
public class EditAttributesViewModel : ObservableObject
{
    // -----------------------------------------------------------------------------------------
    // Inner type

    public class ProTypeListItem(string label, byte value)
    {
        public string Label { get; private set; } = label;
        public byte Value { get; private set; } = value;
    }

    // -----------------------------------------------------------------------------------------
    // Close interaction

    public event Action<bool>? CloseRequested;

    // -----------------------------------------------------------------------------------------
    // Domain objects

    private readonly object mArchiveOrFileSystem;
    private readonly IFileEntry mFileEntry;
    private readonly IFileEntry mADFEntry;
    private readonly FileAttribs mOldAttribs;

    /// <summary>
    /// Updated attributes — callers read this after dialog returns true.
    /// </summary>
    public FileAttribs NewAttribs { get; private set; } = new FileAttribs();

    // -----------------------------------------------------------------------------------------
    // Commands

    public IRelayCommand OkCommand { get; }
    public IRelayCommand CancelCommand { get; }

    // -----------------------------------------------------------------------------------------
    // Global state

    private bool mIsAllReadOnly;
    public bool IsAllReadOnly {
        get => mIsAllReadOnly;
        private init => SetProperty(ref mIsAllReadOnly, value);
    }

    private bool mIsValid;
    public bool IsValid {
        get => mIsValid;
        private set { if (SetProperty(ref mIsValid, value)) OkCommand?.NotifyCanExecuteChanged(); }
    }

    // -----------------------------------------------------------------------------------------
    // Filename section

    private string mSyntaxRulesText = string.Empty;
    public string SyntaxRulesText {
        get => mSyntaxRulesText;
        set => SetProperty(ref mSyntaxRulesText, value);
    }

    // Validation booleans (replace IBrush properties)
    private bool mIsSyntaxValid = true;
    public bool IsSyntaxValid {
        get => mIsSyntaxValid;
        private set => SetProperty(ref mIsSyntaxValid, value);
    }

    private bool mIsUniqueNameValid = true;
    public bool IsUniqueNameValid {
        get => mIsUniqueNameValid;
        private set => SetProperty(ref mIsUniqueNameValid, value);
    }

    public bool IsUniqueTextVisible { get; private set; } = true;

    private string mDirSepText = string.Empty;
    public string DirSepText {
        get => mDirSepText;
        private set => SetProperty(ref mDirSepText, value);
    }

    private bool mIsDirSepTextVisible;
    public bool IsDirSepTextVisible {
        get => mIsDirSepTextVisible;
        private set => SetProperty(ref mIsDirSepTextVisible, value);
    }

    private const string DIR_SEP_CHAR_FMT = "\u2022 Directory separator character is '{0}'.";

    private readonly Func<string, bool> mIsValidFunc;
    private bool mIsFileNameValid;
    private bool mIsFileNameUnique;

    public string FileName {
        get => NewAttribs.FullPathName;
        set {
            NewAttribs.FullPathName = value;
            OnPropertyChanged();
            CheckFileNameValidity(out mIsFileNameValid, out mIsFileNameUnique);
            UpdateControls();
        }
    }

    private void CheckFileNameValidity(out bool isValid, out bool isUnique) {
        if (IsAllReadOnly) { isValid = isUnique = true; return; }
        isValid = mIsValidFunc(NewAttribs.FullPathName);
        isUnique = true;
        if (mArchiveOrFileSystem is IArchive arc) {
            if (arc.TryFindFileEntry(NewAttribs.FullPathName, out IFileEntry foundEntry) &&
                    foundEntry != mFileEntry) {
                isUnique = false;
            }
        } else {
            IFileSystem fs = (IFileSystem)mArchiveOrFileSystem;
            if (mFileEntry.ContainingDir != IFileEntry.NO_ENTRY) {
                if (fs.TryFindFileEntry(mFileEntry.ContainingDir, NewAttribs.FullPathName,
                        out IFileEntry foundEntry) && foundEntry != mFileEntry) {
                    isUnique = false;
                }
            }
        }
    }

    private void PrepareFileName() {
        if (mFileEntry is DOS_FileEntry && mFileEntry.IsDirectory) {
            NewAttribs.FullPathName = ((DOS)mArchiveOrFileSystem).VolumeNum.ToString("D3");
        } else if (mArchiveOrFileSystem is IArchive) {
            NewAttribs.FullPathName = mOldAttribs.FullPathName;
        } else {
            NewAttribs.FullPathName = mOldAttribs.FileNameOnly;
        }
        CheckFileNameValidity(out mIsFileNameValid, out mIsFileNameUnique);
    }

    // -----------------------------------------------------------------------------------------
    // File Type section

    public bool IsProTypeVisible { get; private set; } = true;
    public bool IsHFSTypeVisible { get; private set; } = true;

    public List<ProTypeListItem> ProTypeList { get; } = new List<ProTypeListItem>();

    public bool IsProTypeListEnabled { get; private set; } = true;
    public bool IsProAuxEnabled { get; private set; } = true;

    private string mProTypeDescString = string.Empty;
    public string ProTypeDescString {
        get => mProTypeDescString;
        set => SetProperty(ref mProTypeDescString, value);
    }

    /// <summary>Selected item in ProType combo. Setting it updates NewAttribs.FileType.</summary>
    private ProTypeListItem? mSelectedProTypeItem;
    public ProTypeListItem? SelectedProTypeItem {
        get => mSelectedProTypeItem;
        set {
            SetProperty(ref mSelectedProTypeItem, value);
            if (value != null) {
                NewAttribs.FileType = value.Value;
                Debug.WriteLine("ProDOS file type: $" + NewAttribs.FileType.ToString("x2"));
            }
            UpdateControls();
        }
    }

    private string mProAuxString = string.Empty;
    public string ProAuxString {
        get => mProAuxString;
        set {
            SetProperty(ref mProAuxString, value);
            mProAuxValid = true;
            if (string.IsNullOrEmpty(value)) {
                NewAttribs.AuxType = 0;
            } else {
                try {
                    NewAttribs.AuxType = Convert.ToUInt16(value, 16);
                } catch {
                    mProAuxValid = false;
                }
            }
            UpdateControls();
        }
    }
    private bool mProAuxValid = true;

    private bool mIsProAuxValid = true;
    public bool IsProAuxValid {
        get => mIsProAuxValid;
        private set => SetProperty(ref mIsProAuxValid, value);
    }

    // HFS types
    private string mHFSTypeCharsString = string.Empty;
    public string HFSTypeCharsString {
        get => mHFSTypeCharsString;
        set {
            SetProperty(ref mHFSTypeCharsString, value);
            mHFSTypeHexString = SetHexFromChars(value, out uint newNum, out mHFSTypeValid);
            OnPropertyChanged(nameof(HFSTypeHexString));
            NewAttribs.HFSFileType = newNum;
            UpdateControls();
        }
    }

    private string mHFSTypeHexString = string.Empty;
    public string HFSTypeHexString {
        get => mHFSTypeHexString;
        set {
            SetProperty(ref mHFSTypeHexString, value);
            mHFSTypeCharsString = SetCharsFromHex(value, out uint newNum, out mHFSTypeValid);
            OnPropertyChanged(nameof(HFSTypeCharsString));
            NewAttribs.HFSFileType = newNum;
            UpdateControls();
        }
    }
    private bool mHFSTypeValid = true;

    private bool mIsHFSTypeValid = true;
    public bool IsHFSTypeValid {
        get => mIsHFSTypeValid;
        private set => SetProperty(ref mIsHFSTypeValid, value);
    }

    private string mHFSCreatorCharsString = string.Empty;
    public string HFSCreatorCharsString {
        get => mHFSCreatorCharsString;
        set {
            SetProperty(ref mHFSCreatorCharsString, value);
            mHFSCreatorHexString = SetHexFromChars(value, out uint newNum, out mHFSCreatorValid);
            OnPropertyChanged(nameof(HFSCreatorHexString));
            NewAttribs.HFSCreator = newNum;
            UpdateControls();
        }
    }

    private string mHFSCreatorHexString = string.Empty;
    public string HFSCreatorHexString {
        get => mHFSCreatorHexString;
        set {
            SetProperty(ref mHFSCreatorHexString, value);
            mHFSCreatorCharsString = SetCharsFromHex(value, out uint newNum, out mHFSCreatorValid);
            OnPropertyChanged(nameof(HFSCreatorCharsString));
            NewAttribs.HFSCreator = newNum;
            UpdateControls();
        }
    }
    private bool mHFSCreatorValid = true;

    private bool mIsHFSCreatorValid = true;
    public bool IsHFSCreatorValid {
        get => mIsHFSCreatorValid;
        private set => SetProperty(ref mIsHFSCreatorValid, value);
    }

    private static string SetHexFromChars(string charValue, out uint newNum, out bool isValid) {
        isValid = true;
        if (string.IsNullOrEmpty(charValue)) { newNum = 0; return string.Empty; }
        if (charValue.Length == 4) {
            newNum = MacChar.IntifyMacConstantString(charValue);
            return newNum.ToString("X8");
        }
        newNum = 0;
        isValid = false;
        return string.Empty;
    }

    private static string SetCharsFromHex(string hexStr, out uint newNum, out bool isValid) {
        isValid = true;
        if (string.IsNullOrEmpty(hexStr)) { newNum = 0; return string.Empty; }
        try {
            newNum = Convert.ToUInt32(hexStr, 16);
            return MacChar.StringifyMacConstant(newNum);
        } catch {
            isValid = false;
            newNum = 0;
            return string.Empty;
        }
    }

    private static readonly byte[] DOS_TYPES = {
        FileAttribs.FILE_TYPE_TXT, FileAttribs.FILE_TYPE_INT, FileAttribs.FILE_TYPE_BAS,
        FileAttribs.FILE_TYPE_BIN, FileAttribs.FILE_TYPE_F2,  FileAttribs.FILE_TYPE_REL,
        FileAttribs.FILE_TYPE_F3,  FileAttribs.FILE_TYPE_F4
    };
    private static readonly byte[] PASCAL_TYPES = {
        FileAttribs.FILE_TYPE_NON, FileAttribs.FILE_TYPE_BAD, FileAttribs.FILE_TYPE_PCD,
        FileAttribs.FILE_TYPE_PTX, FileAttribs.FILE_TYPE_F3,  FileAttribs.FILE_TYPE_PDA,
        FileAttribs.FILE_TYPE_F4,  FileAttribs.FILE_TYPE_FOT, FileAttribs.FILE_TYPE_F5
    };

    private void PrepareProTypeList() {
        if (mFileEntry is DOS_FileEntry) {
            if (mFileEntry.IsDirectory) {
                IsProTypeListEnabled = IsProAuxEnabled = false;
                IsProTypeVisible = false;
            } else {
                foreach (byte type in DOS_TYPES) {
                    ProTypeList.Add(new ProTypeListItem(FileTypes.GetDOSTypeAbbrev(type), type));
                }
            }
        } else if (mFileEntry is Pascal_FileEntry) {
            IsProAuxEnabled = false;
            if (mFileEntry.IsDirectory) {
                IsProTypeListEnabled = false;
                IsProTypeVisible = false;
            } else {
                foreach (byte type in PASCAL_TYPES) {
                    ProTypeList.Add(new ProTypeListItem(FileTypes.GetPascalTypeName(type), type));
                }
            }
        } else if (mFileEntry.HasProDOSTypes || mADFEntry != IFileEntry.NO_ENTRY) {
            for (int type = 0; type < 256; type++) {
                string abbrev = FileTypes.GetFileTypeAbbrev(type);
                if (abbrev[0] == '$') abbrev = "???";
                ProTypeList.Add(new ProTypeListItem(abbrev + " $" + type.ToString("X2"), (byte)type));
            }
            IsProTypeListEnabled = !mFileEntry.IsDirectory;
            if (mArchiveOrFileSystem is IFileSystem) {
                IsProAuxEnabled = mFileEntry.ContainingDir != IFileEntry.NO_ENTRY;
            }
        } else {
            IsProTypeListEnabled = IsProAuxEnabled = false;
            IsProTypeVisible = false;
        }

        if (mFileEntry.IsDirectory && mFileEntry.ContainingDir == IFileEntry.NO_ENTRY) {
            IsUniqueTextVisible = false;
        }
        if (IsAllReadOnly) IsProTypeListEnabled = false;

        // Set SelectedProTypeItem based on current FileType
        mSelectedProTypeItem = null;
        for (int i = 0; i < ProTypeList.Count; i++) {
            if (ProTypeList[i].Value == NewAttribs.FileType) {
                mSelectedProTypeItem = ProTypeList[i];
                break;
            }
        }
        if (mSelectedProTypeItem == null && ProTypeList.Count > 0) {
            Debug.Assert(mFileEntry is DOS_FileEntry, "no ProDOS type matched");
            mSelectedProTypeItem = ProTypeList[0];
        }
    }

    private void PrepareHFSTypes() {
        if (mADFEntry == IFileEntry.NO_ENTRY && !mFileEntry.HasHFSTypes) {
            IsHFSTypeVisible = false;
        }
        mHFSTypeHexString = NewAttribs.HFSFileType == 0
            ? string.Empty : NewAttribs.HFSFileType.ToString("X8");
        mHFSCreatorHexString = NewAttribs.HFSCreator == 0
            ? string.Empty : NewAttribs.HFSCreator.ToString("X8");
    }

    // -----------------------------------------------------------------------------------------
    // Timestamps section

    public bool IsTimestampVisible { get; private set; } = true;
    public DateTimeOffset TimestampStart { get; private set; }
    public DateTimeOffset TimestampEnd { get; private set; }
    public string TimestampStartStr { get; private set; } = string.Empty;
    public string TimestampEndStr { get; private set; } = string.Empty;

    private DateTimeOffset? mCreateDate;
    public DateTimeOffset? CreateDate {
        get => mCreateDate;
        set {
            SetProperty(ref mCreateDate, value);
            NewAttribs.CreateWhen = DateTimeUpdated(mCreateDate, mCreateTimeString,
                out mCreateWhenValid);
            UpdateControls();
        }
    }

    private string mCreateTimeString = string.Empty;
    public string CreateTimeString {
        get => mCreateTimeString;
        set {
            SetProperty(ref mCreateTimeString, value);
            NewAttribs.CreateWhen = DateTimeUpdated(mCreateDate, mCreateTimeString,
                out mCreateWhenValid);
            UpdateControls();
        }
    }
    private bool mCreateWhenValid = true;

    private bool mIsCreateWhenValid = true;
    public bool IsCreateWhenValid {
        get => mIsCreateWhenValid;
        private set => SetProperty(ref mIsCreateWhenValid, value);
    }

    public bool CreateWhenEnabled { get; private set; } = true;

    private DateTimeOffset? mModDate;
    public DateTimeOffset? ModDate {
        get => mModDate;
        set {
            SetProperty(ref mModDate, value);
            NewAttribs.ModWhen = DateTimeUpdated(mModDate, mModTimeString, out mModWhenValid);
            UpdateControls();
        }
    }

    private string mModTimeString = string.Empty;
    public string ModTimeString {
        get => mModTimeString;
        set {
            SetProperty(ref mModTimeString, value);
            NewAttribs.ModWhen = DateTimeUpdated(mModDate, mModTimeString, out mModWhenValid);
            UpdateControls();
        }
    }
    private bool mModWhenValid = true;

    private bool mIsModWhenValid = true;
    public bool IsModWhenValid {
        get => mIsModWhenValid;
        private set => SetProperty(ref mIsModWhenValid, value);
    }

    public bool ModWhenEnabled { get; private set; } = true;

    private const string TIME_PATTERN = @"^(\d{1,2}):(\d\d)(?>:(\d\d))?$";
    private static readonly Regex sTimeRegex = new Regex(TIME_PATTERN);

    private DateTime DateTimeUpdated(DateTimeOffset? ndt, string timeStr, out bool isValid) {
        isValid = true;
        if (ndt == null) return TimeStamp.NO_DATE;
        DateTime dt = ndt.Value.DateTime;
        DateTime newWhen;
        if (!string.IsNullOrEmpty(timeStr)) {
            MatchCollection matches = sTimeRegex.Matches(timeStr);
            if (matches.Count != 1) { isValid = false; return TimeStamp.NO_DATE; }
            int hours = int.Parse(matches[0].Groups[1].Value);
            int minutes = int.Parse(matches[0].Groups[2].Value);
            int seconds = 0;
            if (!string.IsNullOrEmpty(matches[0].Groups[3].Value)) {
                seconds = int.Parse(matches[0].Groups[3].Value);
            }
            if (hours >= 24 || minutes >= 60 || seconds >= 60) { isValid = false; return TimeStamp.NO_DATE; }
            newWhen = new DateTime(dt.Year, dt.Month, dt.Day, hours, minutes, seconds, DateTimeKind.Local);
        } else {
            newWhen = DateTime.SpecifyKind(new DateTime(dt.Year, dt.Month, dt.Day), DateTimeKind.Local);
        }
        isValid = newWhen >= TimestampStart.DateTime && newWhen <= TimestampEnd.DateTime;
        return newWhen;
    }

    private void PrepareTimestamps() {
        DateTime tsStart, tsEnd;
        if (mArchiveOrFileSystem is IArchive) {
            if (mADFEntry == IFileEntry.NO_ENTRY) {
                IArchive arc = (IArchive)mArchiveOrFileSystem;
                tsStart = arc.Characteristics.TimeStampStart;
                tsEnd = arc.Characteristics.TimeStampEnd;
            } else {
                tsStart = AppleSingle.SCharacteristics.TimeStampStart;
                tsEnd = AppleSingle.SCharacteristics.TimeStampEnd;
            }
        } else {
            IFileSystem fs = (IFileSystem)mArchiveOrFileSystem;
            tsStart = fs.Characteristics.TimeStampStart;
            tsEnd = fs.Characteristics.TimeStampEnd;
        }
        TimestampStart = new DateTimeOffset(tsStart);
        TimestampEnd = new DateTimeOffset(tsEnd);
        TimestampStartStr = tsStart.ToShortDateString();
        TimestampEndStr = tsEnd.ToShortDateString();

        if (tsStart == tsEnd) IsTimestampVisible = false;

        if ((mArchiveOrFileSystem is Zip && mADFEntry == IFileEntry.NO_ENTRY) ||
                mArchiveOrFileSystem is GZip || mArchiveOrFileSystem is Pascal) {
            CreateWhenEnabled = false;
        }

        if (TimeStamp.IsValidDate(NewAttribs.CreateWhen)) {
            mCreateDate = new DateTimeOffset(NewAttribs.CreateWhen);
            mCreateTimeString = NewAttribs.CreateWhen.ToString("HH:mm:ss");
        }
        if (TimeStamp.IsValidDate(NewAttribs.ModWhen)) {
            mModDate = new DateTimeOffset(NewAttribs.ModWhen);
            mModTimeString = NewAttribs.ModWhen.ToString("HH:mm:ss");
        }
        mCreateWhenValid = mModWhenValid = true;

        if (IsAllReadOnly) { CreateWhenEnabled = false; ModWhenEnabled = false; }
    }

    // -----------------------------------------------------------------------------------------
    // Access Flags section

    public bool IsAccessVisible { get; private set; } = true;
    public bool IsLockedOnlyVisible { get; private set; } = false;
    public bool IsAllFlagsVisible { get; private set; } = false;

    private const byte FILE_ACCESS_TOGGLE = (byte)
        (FileAttribs.AccessFlags.Write | FileAttribs.AccessFlags.Rename |
         FileAttribs.AccessFlags.Delete);

    private bool mAccessLocked;
    public bool AccessLocked {
        get => mAccessLocked;
        set {
            SetProperty(ref mAccessLocked, value);
            if (value) {
                NewAttribs.Access = (byte)(NewAttribs.Access & ~FILE_ACCESS_TOGGLE);
            } else {
                NewAttribs.Access |= FILE_ACCESS_TOGGLE;
                NewAttribs.Access |= (byte)FileAttribs.AccessFlags.Read;
            }
        }
    }
    public bool AccessLockedEnabled { get; private set; } = true;

    private bool MakeAccessProp(ref bool field, string name, FileAttribs.AccessFlags flag,
            bool newValue) {
        field = newValue;
        OnPropertyChanged(name);
        if (newValue) { NewAttribs.Access |= (byte)flag; }
        else { NewAttribs.Access = (byte)(NewAttribs.Access & ~(byte)flag); }
        return newValue;
    }

    private bool mAccessRead;
    public bool AccessRead { get => mAccessRead; set => MakeAccessProp(ref mAccessRead, nameof(AccessRead), FileAttribs.AccessFlags.Read, value); }
    public bool AccessReadEnabled { get; private set; } = true;

    private bool mAccessWrite;
    public bool AccessWrite { get => mAccessWrite; set => MakeAccessProp(ref mAccessWrite, nameof(AccessWrite), FileAttribs.AccessFlags.Write, value); }
    public bool AccessWriteEnabled { get; private set; } = true;

    private bool mAccessRename;
    public bool AccessRename { get => mAccessRename; set => MakeAccessProp(ref mAccessRename, nameof(AccessRename), FileAttribs.AccessFlags.Rename, value); }
    public bool AccessRenameEnabled { get; private set; } = true;

    private bool mAccessDelete;
    public bool AccessDelete { get => mAccessDelete; set => MakeAccessProp(ref mAccessDelete, nameof(AccessDelete), FileAttribs.AccessFlags.Delete, value); }
    public bool AccessDeleteEnabled { get; private set; } = true;

    private bool mAccessBackup;
    public bool AccessBackup { get => mAccessBackup; set => MakeAccessProp(ref mAccessBackup, nameof(AccessBackup), FileAttribs.AccessFlags.Backup, value); }
    public bool AccessBackupEnabled { get; private set; } = true;

    private bool mAccessInvisible;
    public bool AccessInvisible { get => mAccessInvisible; set => MakeAccessProp(ref mAccessInvisible, nameof(AccessInvisible), FileAttribs.AccessFlags.Invisible, value); }
    public bool AccessInvisibleEnabled { get; private set; } = true;

    private void PrepareAccess() {
        if ((mArchiveOrFileSystem is Zip && mADFEntry == IFileEntry.NO_ENTRY) ||
                mArchiveOrFileSystem is GZip || mArchiveOrFileSystem is Pascal ||
                (mFileEntry.IsDirectory && mFileEntry.ContainingDir == IFileEntry.NO_ENTRY)) {
            IsAccessVisible = false;
            return;
        }
        if (mArchiveOrFileSystem is ProDOS || mArchiveOrFileSystem is NuFX ||
                mArchiveOrFileSystem is CPM) {
            IsAllFlagsVisible = true;
            if (mArchiveOrFileSystem is CPM) {
                AccessReadEnabled = AccessRenameEnabled = AccessDeleteEnabled =
                    AccessInvisibleEnabled = false;
            }
            mAccessRead     = (NewAttribs.Access & (byte)FileAttribs.AccessFlags.Read) != 0;
            mAccessWrite    = (NewAttribs.Access & (byte)FileAttribs.AccessFlags.Write) != 0;
            mAccessRename   = (NewAttribs.Access & (byte)FileAttribs.AccessFlags.Rename) != 0;
            mAccessBackup   = (NewAttribs.Access & (byte)FileAttribs.AccessFlags.Backup) != 0;
            mAccessDelete   = (NewAttribs.Access & (byte)FileAttribs.AccessFlags.Delete) != 0;
            mAccessInvisible= (NewAttribs.Access & (byte)FileAttribs.AccessFlags.Invisible) != 0;
        } else {
            IsLockedOnlyVisible = true;
            mAccessLocked = (NewAttribs.Access & (byte)FileAttribs.AccessFlags.Write) == 0;
            AccessInvisibleEnabled =
                mArchiveOrFileSystem is AppleSingle || mArchiveOrFileSystem is Binary2 ||
                mADFEntry != IFileEntry.NO_ENTRY;
            mAccessInvisible = (NewAttribs.Access & (byte)FileAttribs.AccessFlags.Invisible) != 0;
        }
        if (IsAllReadOnly) {
            AccessLockedEnabled = AccessInvisibleEnabled = AccessReadEnabled =
                AccessWriteEnabled = AccessRenameEnabled = AccessBackupEnabled =
                AccessDeleteEnabled = false;
        }
    }

    // -----------------------------------------------------------------------------------------
    // Comment section

    public bool IsCommentVisible { get; private set; } = true;

    private string mCommentText = string.Empty;
    public string CommentText {
        get => mCommentText;
        set {
            SetProperty(ref mCommentText, value);
            NewAttribs.Comment = value;
        }
    }

    private void PrepareComment() {
        if ((mArchiveOrFileSystem is not Zip || mADFEntry != IFileEntry.NO_ENTRY) &&
                mArchiveOrFileSystem is not NuFX) {
            IsCommentVisible = false;
            return;
        }
        mCommentText = NewAttribs.Comment;
    }

    // -----------------------------------------------------------------------------------------
    // Validation

    private void UpdateControls() {
        IsSyntaxValid = mIsFileNameValid;
        IsUniqueNameValid = mIsFileNameUnique;
        IsProAuxValid = mProAuxValid;
        IsHFSTypeValid = mHFSTypeValid;
        IsHFSCreatorValid = mHFSCreatorValid;
        IsCreateWhenValid = mCreateWhenValid;
        IsModWhenValid = mModWhenValid;

        if (mIsProAuxValid) {
            ProTypeDescString = FileTypes.GetDescription(NewAttribs.FileType, NewAttribs.AuxType);
        } else {
            ProTypeDescString = string.Empty;
        }

        IsValid = !IsAllReadOnly &&
            mIsFileNameValid && mIsFileNameUnique && mProAuxValid &&
            mHFSTypeValid && mHFSCreatorValid && mCreateWhenValid && mModWhenValid;
    }

    // -----------------------------------------------------------------------------------------
    // Constructor

    private bool CanOk() => IsValid;

    public EditAttributesViewModel(
        object archiveOrFileSystem,
        IFileEntry entry,
        IFileEntry adfEntry,
        FileAttribs initialAttribs,
        bool isReadOnly)
    {
        mArchiveOrFileSystem = archiveOrFileSystem;
        mFileEntry = entry;
        mADFEntry = adfEntry;
        mOldAttribs = initialAttribs;
        IsAllReadOnly = isReadOnly;

        if (archiveOrFileSystem is IArchive arc) {
            mIsValidFunc = arc.IsValidFileName;
            SyntaxRulesText = "\u2022 " + arc.Characteristics.FileNameSyntaxRules;
            if (arc.Characteristics.DefaultDirSep != IFileEntry.NO_DIR_SEP) {
                DirSepText = string.Format(DIR_SEP_CHAR_FMT, arc.Characteristics.DefaultDirSep);
                IsDirSepTextVisible = true;
            }
        } else if (archiveOrFileSystem is IFileSystem fs) {
            if (entry.IsDirectory && entry.ContainingDir == IFileEntry.NO_ENTRY) {
                mIsValidFunc = fs.IsValidVolumeName;
                SyntaxRulesText = "\u2022 " + fs.Characteristics.VolumeNameSyntaxRules;
            } else {
                mIsValidFunc = fs.IsValidFileName;
                SyntaxRulesText = "\u2022 " + fs.Characteristics.FileNameSyntaxRules;
            }
        } else {
            throw new NotImplementedException("Can't edit " + archiveOrFileSystem);
        }

        NewAttribs = new FileAttribs(initialAttribs);

        PrepareFileName();
        PrepareProTypeList();
        ProTypeDescString = FileTypes.GetDescription(initialAttribs.FileType, initialAttribs.AuxType);
        mProAuxString = initialAttribs.AuxType.ToString("X4");
        PrepareHFSTypes();
        PrepareTimestamps();
        PrepareAccess();
        PrepareComment();

        UpdateControls();

        OkCommand = new AsyncRelayCommand(
            async () => CloseRequested?.Invoke(true), CanOk);
        CancelCommand = new AsyncRelayCommand(
            async () => CloseRequested?.Invoke(false));
    }
}
