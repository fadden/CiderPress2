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
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;

using CommonUtil;
using DiskArc;
using DiskArc.FS;
using FileConv;
using Microsoft.Win32;
using static FileConv.Converter;

namespace cp2_wpf {
    /// <summary>
    /// File viewer.
    /// </summary>
    public partial class FileViewer : Window, INotifyPropertyChanged {
        public string DataPlainText {
            get { return mDataPlainText; }
            set { mDataPlainText = value; OnPropertyChanged(); }
        }
        private string mDataPlainText = string.Empty;

        public bool IsDataTabEnabled {
            get { return mIsDataTabEnabled; }
            set { mIsDataTabEnabled = value; OnPropertyChanged(); }
        }
        private bool mIsDataTabEnabled;

        public string RsrcPlainText {
            get { return mRsrcPlainText; }
            set { mRsrcPlainText = value; OnPropertyChanged(); }
        }
        private string mRsrcPlainText = string.Empty;

        public bool IsRsrcTabEnabled {
            get { return mIsRsrcTabEnabled; }
            set { mIsRsrcTabEnabled = value; OnPropertyChanged(); }
        }
        private bool mIsRsrcTabEnabled;

        public string NotePlainText {
            get { return mNotePlainText; }
            set { mNotePlainText = value; OnPropertyChanged(); }
        }
        private string mNotePlainText = string.Empty;

        public bool IsNoteTabEnabled {
            get { return mIsNoteTabEnabled; }
            set { mIsNoteTabEnabled = value; OnPropertyChanged(); }
        }
        private bool mIsNoteTabEnabled;

        private enum Tab {
            Unknown = 0, Data, Rsrc, Note
        }
        private void ShowTab(Tab tab) {
            switch (tab) {
                case Tab.Data: tabControl.SelectedItem = dataTabItem; break;
                case Tab.Rsrc: tabControl.SelectedItem = rsrcTabItem; break;
                case Tab.Note: tabControl.SelectedItem = noteTabItem; break;
                default: Debug.Assert(false); break;
            }
        }

        public bool IsOptionsBoxEnabled {
            get { return mIsOptionsBoxEnabled; }
            set { mIsOptionsBoxEnabled = value; OnPropertyChanged(); }
        }
        private bool mIsOptionsBoxEnabled;

        public Visibility SimpleTextVisibility { get; set; }
        public Visibility FancyTextVisibility { get; set; }
        public Visibility BitmapVisibility { get; set; }
        private enum DisplayItemType {
            Unknown = 0, SimpleText, FancyText, Bitmap
        }
        private void SetDisplayType(DisplayItemType item) {
            switch (item) {
                case DisplayItemType.SimpleText:
                    SimpleTextVisibility = Visibility.Visible;
                    FancyTextVisibility = Visibility.Collapsed;
                    BitmapVisibility = Visibility.Collapsed;
                    break;
                case DisplayItemType.FancyText:
                    SimpleTextVisibility = Visibility.Collapsed;
                    FancyTextVisibility = Visibility.Visible;
                    BitmapVisibility = Visibility.Collapsed;
                    break;
                case DisplayItemType.Bitmap:
                    SimpleTextVisibility = Visibility.Collapsed;
                    FancyTextVisibility = Visibility.Collapsed;
                    BitmapVisibility = Visibility.Visible;
                    break;
                default:
                    Debug.Assert(false);
                    break;
            }
            OnPropertyChanged("SimpleTextVisibility");
            OnPropertyChanged("FancyTextVisibility");
            OnPropertyChanged("BitmapVisibility");
        }

        public string GraphicsZoomStr {
            get { return mGraphicsZoomStr; }
            set { mGraphicsZoomStr = value; OnPropertyChanged(); }
        }
        private string mGraphicsZoomStr = string.Empty;

        public bool IsDOSRaw {
            get { return mIsDOSRaw; }
            set { mIsDOSRaw = value; OnPropertyChanged(); ShowFile(false); }
        }
        private bool mIsDOSRaw;

        public bool IsDOSRawEnabled {
            get { return mIsDOSRawEnabled; }
            set { mIsDOSRawEnabled = value; OnPropertyChanged(); }
        }
        private bool mIsDOSRawEnabled;

        public bool IsExportEnabled {
            get { return mIsExportEnabled; }
            set { mIsExportEnabled = value; OnPropertyChanged(); }
        }
        private bool mIsExportEnabled;

        public bool HasPrevFile {
            get { return mHasPrevFile; }
            set { mHasPrevFile = value; OnPropertyChanged(); }
        }
        private bool mHasPrevFile;
        public string PrevFileTip {
            get { return mPrevFileTip; }
            set { mPrevFileTip = value; OnPropertyChanged(); }
        }
        public string mPrevFileTip = string.Empty;

        public bool HasNextFile {
            get { return mHasNextFile; }
            set { mHasNextFile = value; OnPropertyChanged(); }
        }
        private bool mHasNextFile;
        public string NextFileTip {
            get { return mNextFileTip; }
            set { mNextFileTip = value; OnPropertyChanged(); }
        }
        public string mNextFileTip = string.Empty;

        // INotifyPropertyChanged
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string propertyName = "") {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private object mArchiveOrFileSystem;
        private List<IFileEntry> mSelected;
        private int mCurIndex;      // index into mSelected
        private Dictionary<string, string> mConvOptions;
        private Stream? mDataFork;
        private Stream? mRsrcFork;
        private IConvOutput? mCurDataOutput;
        private IConvOutput? mCurRsrcOutput;
        private AppHook mAppHook;

        private SettingsHolder mLocalSettings = new SettingsHolder();

        /// <summary>
        /// Holds an item for the conversion selection combo box.
        /// </summary>
        private class ConverterComboItem {
            public string Name { get; private set; }
            public Converter Converter { get; private set; }

            public ConverterComboItem(string name, Converter converter) {
                Name = name;
                Converter = converter;
            }
            // This determines what the combo box shows.
            public override string ToString() {
                return Name;
            }
        }

        public FileViewer(Window owner, object archiveOrFileSystem, List<IFileEntry> selected,
                AppHook appHook) {
            InitializeComponent();
            Owner = owner;
            DataContext = this;

            Debug.Assert(selected.Count > 0);
            mArchiveOrFileSystem = archiveOrFileSystem;
            mSelected = selected;
            mAppHook = appHook;

            mConvOptions = new Dictionary<string, string>();
            // TODO: init mLocalSettings from global app settings

            CreateControlMap();
        }

        /// <summary>
        /// When the window finishes setting up, before it's made visible, configure the dialog
        /// for the first item in the selection.
        /// </summary>
        private void Window_SourceInitialized(object sender, EventArgs e) {
            mCurIndex = 0;
            magnificationSlider.Value = 1;      // TODO: get from settings
            UpdatePrevNextControls();
            ShowFile(true);
        }

        /// <summary>
        /// Catches the window-closed event to ensure we're closing our streams.
        /// </summary>
        private void Window_Closed(object sender, EventArgs e) {
            CloseStreams();
        }

        private void UpdatePrevNextControls() {
            if (mCurIndex > 0) {
                HasPrevFile = true;
                PrevFileTip = mSelected[mCurIndex - 1].FullPathName;
            } else {
                HasPrevFile = false;
                PrevFileTip = string.Empty;
            }
            if (mCurIndex < mSelected.Count - 1) {
                HasNextFile = true;
                NextFileTip = mSelected[mCurIndex + 1].FullPathName;
            } else {
                HasNextFile = false;
                NextFileTip = string.Empty;
            }
        }
        private void PrevFile_Click(object sender, RoutedEventArgs e) {
            Debug.Assert(mCurIndex > 0);
            mCurIndex--;
            UpdatePrevNextControls();
            ShowFile(true);
        }
        private void NextFile_Click(object sender, RoutedEventArgs e) {
            Debug.Assert(mCurIndex < mSelected.Count - 1);
            mCurIndex++;
            UpdatePrevNextControls();
            ShowFile(true);
        }

        /// <summary>
        /// Watches the tab control so we can disable the format options when the data tab
        /// isn't selected.
        /// </summary>
        private void TabControl_SelectionChanged(object sender, SelectionChangedEventArgs e) {
            // TODO: don't disable the controls when in "hex dump" mode?  Not sure how often
            //   people will be switching charsets.  Resource forks are generally Mac OS Roman.
            IsOptionsBoxEnabled = dataTabItem.IsSelected;
        }

        /// <summary>
        /// Configures the dialog to display a new file.
        /// </summary>
        /// <param name="fileChanged">True if we switched to a new file.  False if we're
        ///   just changing the "raw DOS" setting.</param>
        private void ShowFile(bool fileChanged) {
            IsDOSRawEnabled = (mArchiveOrFileSystem is DOS);

            CloseStreams();

            IFileEntry entry = mSelected[mCurIndex];
            FileAttribs attrs = new FileAttribs(entry);
            List<Converter>? applics;
            try {
                applics = ExportFoundry.GetApplicableConverters(mArchiveOrFileSystem,
                    entry, attrs, IsDOSRaw, out mDataFork, out mRsrcFork, mAppHook);
                // The "generic" converters always apply.
                Debug.Assert(applics.Count > 0);
            } catch (Exception ex) {
                Debug.WriteLine("conv failed: " + ex);
                ShowErrorMessage(ex.Message);
                applics = null;
            }

            string oldName = string.Empty;
            if (convComboBox.SelectedItem != null) {
                oldName = ((ConverterComboItem)convComboBox.SelectedItem).Name;
            }

            convComboBox.Items.Clear();
            if (applics != null) {
                int newIndex = 0;
                for (int i = 0; i < applics.Count; i++) {
                    Converter conv = applics[i];
                    ConverterComboItem item = new ConverterComboItem(conv.Label, conv);
                    convComboBox.Items.Add(item);
                    if (!fileChanged && conv.Label == oldName) {
                        newIndex = i;
                    }
                }
                convComboBox.SelectedIndex = newIndex;
            }

            Title = entry.FileName + " - File Viewer";
        }

        /// <summary>
        /// Closes the data/rsrc fork streams if they are open.
        /// </summary>
        private void CloseStreams() {
            if (mDataFork != null) {
                mDataFork.Close();
                mDataFork = null;
            }
            if (mRsrcFork != null) {
                mRsrcFork.Close();
                mRsrcFork = null;
            }
        }

        /// <summary>
        /// Displays an error message in the data tab.
        /// </summary>
        private void ShowErrorMessage(string msg) {
            DataPlainText = "Viewer error: " + msg;
            SetDisplayType(DisplayItemType.SimpleText);
            ShowTab(Tab.Data);
        }

        private const int MAX_FANCY_TEXT = 1024 * 1024 * 16;     // 16MB
        private const int MAX_SIMPLE_TEXT = 1024 * 1024 * 16;    // 16MB

        /// <summary>
        /// Reformats the file when the conversion combo box selection changes.
        /// </summary>
        private void ConvComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) {
            ConverterComboItem? item = (ConverterComboItem)convComboBox.SelectedItem;
            if (item == null) {
                // This happens when the combo box is cleared for a new file.
                return;
            }

            PrepareOptions(item.Converter);
            ConfigureControls(item.Converter);
            IsExportEnabled = false;

            FormatFile();
        }

        private void SelectPlainText_Click(object sender, RoutedEventArgs e) {
            SelectConversion(typeof(FileConv.Generic.PlainText));
        }
        private void SelectHexDump_Click(object sender, RoutedEventArgs e) {
            SelectConversion(typeof(FileConv.Generic.HexDump));
        }
        private void SelectBest_Click(object sender, RoutedEventArgs e) {
            convComboBox.SelectedIndex = 0;
        }
        private void SelectConversion(Type convType) {
            foreach (ConverterComboItem item in convComboBox.Items) {
                if (item.Converter.GetType() == convType) {
                    convComboBox.SelectedItem = item;
                    break;
                }
            }
        }

        private void FormatFile() {
            ConverterComboItem? item = (ConverterComboItem)convComboBox.SelectedItem;
            Debug.Assert(item != null);

            DateTime startWhen = DateTime.Now;
            mCurDataOutput = item.Converter.ConvertFile(mConvOptions);
            DateTime dataDoneWhen = DateTime.Now;
            // TODO: make the resource fork formatting on-demand: only do it if the resource
            //   tab is enabled, or when we switch to the resource fork tab
            mCurRsrcOutput = item.Converter.FormatResources(mConvOptions);
            DateTime rsrcDoneWhen = DateTime.Now;
            mAppHook.LogD(item.Converter.Label + " conv took " +
                (dataDoneWhen - startWhen).TotalMilliseconds + " ms (data fork) / " +
                (rsrcDoneWhen - dataDoneWhen).TotalMilliseconds + " ms (rsrc fork)");

            dataForkTextBox.HorizontalContentAlignment = HorizontalAlignment.Left;
            dataForkTextBox.VerticalContentAlignment = VerticalAlignment.Top;

            if (mCurDataOutput is ErrorText) {
                StringBuilder sb = ((SimpleText)mCurDataOutput).Text;
                DataPlainText = ((SimpleText)mCurDataOutput).Text.ToString();
                dataForkTextBox.HorizontalContentAlignment = HorizontalAlignment.Center;
                dataForkTextBox.VerticalContentAlignment = VerticalAlignment.Center;
                SetDisplayType(DisplayItemType.SimpleText);
            } else if (mCurDataOutput is FancyText && !((FancyText)mCurDataOutput).PreferSimple) {
                StringBuilder sb = ((FancyText)mCurDataOutput).Text;
                if (sb.Length > MAX_FANCY_TEXT) {
                    int oldLen = sb.Length;
                    sb.Clear();
                    sb.Append("[ output too long for viewer (length=");
                    sb.Append(oldLen);
                    sb.Append(") ]");
                }
                MemoryStream rtfStream = new MemoryStream();
                RTFGenerator.Generate((FancyText)mCurDataOutput, rtfStream);
                rtfStream.Position = 0;
                TextRange range = new TextRange(dataRichTextBox.Document.ContentStart,
                    dataRichTextBox.Document.ContentEnd);
                range.Load(rtfStream, DataFormats.Rtf);
                SetDisplayType(DisplayItemType.FancyText);
                IsExportEnabled = true;

                //using (FileStream stream =
                //        new FileStream(@"C:\src\ciderpress2\test.rtf", FileMode.Create)) {
                //    rtfStream.Position = 0;
                //    rtfStream.CopyTo(stream);
                //}
            } else if (mCurDataOutput is SimpleText) {
                StringBuilder sb = ((SimpleText)mCurDataOutput).Text;
                if (sb.Length > MAX_SIMPLE_TEXT) {
                    int oldLen = sb.Length;
                    sb.Clear();
                    sb.Append("[ output too long for viewer (length=");
                    sb.Append(oldLen);
                    sb.Append(") ]");
                }
                DataPlainText = ((SimpleText)mCurDataOutput).Text.ToString();
                SetDisplayType(DisplayItemType.SimpleText);
                IsExportEnabled = true;
            } else if (mCurDataOutput is CellGrid) {
                StringBuilder sb = new StringBuilder();
                CSVGenerator.GenerateString((CellGrid)mCurDataOutput, false, sb);
                if (sb.Length > MAX_SIMPLE_TEXT) {
                    int oldLen = sb.Length;
                    sb.Clear();
                    sb.Append("[ output too long for viewer (length=");
                    sb.Append(oldLen);
                    sb.Append(") ]");
                }
                DataPlainText = sb.ToString();
                SetDisplayType(DisplayItemType.SimpleText);
                IsExportEnabled = true;
            } else if (mCurDataOutput is IBitmap) {
                IBitmap bitmap = (IBitmap)mCurDataOutput;
                previewImage.Source = WinUtil.ConvertToBitmapSource(bitmap);
                ConfigureMagnification();
                SetDisplayType(DisplayItemType.Bitmap);

                //using (FileStream tmpStream =
                //        new FileStream(@"C:\src\ciderpress2\TEST.png", FileMode.Create)) {
                //    PNGGenerator.Generate(bitmap, tmpStream);
                //}
                IsExportEnabled = true;
            } else if (mCurDataOutput is HostConv) {
                DataPlainText = "TODO: host-convert " + ((HostConv)mCurDataOutput).Kind;
                SetDisplayType(DisplayItemType.SimpleText);
            } else {
                Debug.Assert(false, "unknown IConvOutput impl " + mCurDataOutput);
            }
            IsDataTabEnabled = (mDataFork != null);

            if (mCurRsrcOutput == null) {
                IsRsrcTabEnabled = false;
                RsrcPlainText = string.Empty;
            } else {
                IsRsrcTabEnabled = true;
                RsrcPlainText = ((SimpleText)mCurRsrcOutput).Text.ToString();
            }

            Notes comboNotes = new Notes();
            if (mCurDataOutput.Notes.Count > 0) {
                comboNotes.MergeFrom(mCurDataOutput.Notes);
            }
            if (mCurRsrcOutput != null && mCurRsrcOutput.Notes.Count > 0) {
                comboNotes.MergeFrom(mCurRsrcOutput.Notes);
            }
            if (comboNotes.Count > 0) {
                NotePlainText = comboNotes.ToString();
                IsNoteTabEnabled = true;
            } else {
                NotePlainText = string.Empty;
                IsNoteTabEnabled = false;
            }

            SelectEnabledTab();
        }

        /// <summary>
        /// If the currently-selected tab is not enabled, switch to one that is.
        /// </summary>
        private void SelectEnabledTab() {
            if ((tabControl.SelectedItem == dataTabItem && IsDataTabEnabled) ||
                    (tabControl.SelectedItem == rsrcTabItem && IsRsrcTabEnabled) ||
                    (tabControl.SelectedItem == noteTabItem && IsNoteTabEnabled)) {
                // Keep current selection.
                return;
            }
            if (IsDataTabEnabled) {
                ShowTab(Tab.Data);
            } else if (IsRsrcTabEnabled) {
                ShowTab(Tab.Rsrc);
            } else {
                ShowTab(Tab.Note);
            }
        }

        /// <summary>
        /// Handles the Export button.
        /// </summary>
        private void ExportButton_Click(object sender, RoutedEventArgs e) {
            string filter, ext;
            if (mCurDataOutput is FancyText && !((FancyText)mCurDataOutput).PreferSimple) {
                filter = WinUtil.FILE_FILTER_RTF;
                ext = ".rtf";
            } else if (mCurDataOutput is SimpleText) {
                filter = WinUtil.FILE_FILTER_TXT;
                ext = ".txt";
            } else if (mCurDataOutput is CellGrid) {
                filter = WinUtil.FILE_FILTER_CSV;
                ext = ".csv";
            } else if (mCurDataOutput is IBitmap) {
                filter = WinUtil.FILE_FILTER_PNG;
                ext = ".png";
            } else {
                Debug.Assert(false, "not handling " + mCurDataOutput.GetType().Name);
                return;
            }

            // We'd like to use the FileDialog "AddExtension" property to automatically add the
            // extension if it's not present, but this doesn't work correctly if the file appears
            // to have some other extension.  For example, it won't add ".png" to a filename that
            // ends with ".pic".
            string fileName = mSelected[mCurIndex].FileName;
            if (!fileName.ToLowerInvariant().EndsWith(ext)) {
                fileName += ext;
            }

            // AddExtension, ValidateNames, CheckPathExists, OverwritePrompt are enabled by default
            SaveFileDialog fileDlg = new SaveFileDialog() {
                Title = "Export File...",
                Filter = filter + "|" + WinUtil.FILE_FILTER_ALL,
                FilterIndex = 1,
                FileName = fileName
            };
            if (fileDlg.ShowDialog() != true) {
                return;
            }
            string pathName = Path.GetFullPath(fileDlg.FileName);
            if (!pathName.ToLowerInvariant().EndsWith(ext)) {
                pathName += ext;
            }

            try {
                using (Stream outStream = new FileStream(pathName, FileMode.Create)) {
                    if (mCurDataOutput is FancyText && !((FancyText)mCurDataOutput).PreferSimple) {
                        StringBuilder sb = ((FancyText)mCurDataOutput).Text;
                        RTFGenerator.Generate((FancyText)mCurDataOutput, outStream);
                    } else if (mCurDataOutput is SimpleText) {
                        StringBuilder sb = ((SimpleText)mCurDataOutput).Text;
                        using (StreamWriter sw = new StreamWriter(outStream)) {
                            sw.Write(sb);
                        }
                    } else if (mCurDataOutput is CellGrid) {
                        StringBuilder sb = new StringBuilder();
                        CSVGenerator.GenerateString((CellGrid)mCurDataOutput, false, sb);
                        using (StreamWriter sw = new StreamWriter(outStream)) {
                            sw.Write(sb);
                        }
                    } else if (mCurDataOutput is IBitmap) {
                        PNGGenerator.Generate((IBitmap)mCurDataOutput, outStream);
                    } else {
                        Debug.Assert(false, "not handling " + mCurDataOutput.GetType().Name);
                        return;
                    }
                }
            } catch (Exception ex) {
                MessageBox.Show(this, "Export failed: " + ex.Message, "Export Failed",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Handles movement on the graphics zoom slider.
        /// </summary>
        private void MagnificationSlider_ValueChanged(object sender,
                RoutedPropertyChangedEventArgs<double> e) {
            ConfigureMagnification();
        }

        /// <summary>
        /// Configures the magnification level of the bitmap display.
        /// </summary>
        private void ConfigureMagnification() {
            int tick = (int)magnificationSlider.Value;
            double mult;
            if (tick == 0) {
                mult = 0.5;
            } else {
                mult = tick;
            }
            GraphicsZoomStr = mult.ToString() + "X";

            IBitmap? bitmap = mCurDataOutput as IBitmap;
            if (bitmap == null) {
                return;
            }

            previewImage.Width = Math.Floor(bitmap.Width * mult);
            previewImage.Height = Math.Floor(bitmap.Height * mult);
            Debug.WriteLine("Gfx zoom " + mult + " --> " +
                previewImage.Width + "x" + previewImage.Height);
        }

        #region Configure

        /// <summary>
        /// Base class for mappable control items.
        /// </summary>
        private abstract class ControlMapItem : INotifyPropertyChanged {
            public string OptTag { get; protected set; } = string.Empty;

            public FrameworkElement VisElem { get; }

            public delegate void UpdateOption(string tag, string newValue);
            protected UpdateOption Updater { get; }

            // INotifyPropertyChanged
            public event PropertyChangedEventHandler? PropertyChanged;
            protected void OnPropertyChanged([CallerMemberName] string propertyName = "") {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }

            /// <summary>
            /// Configures visibility.
            /// </summary>
            public Visibility Visibility {
                get { return mVisibility; }
                set {
                    if (mVisibility != value) {
                        mVisibility = value;
                        OnPropertyChanged();
                        //Debug.WriteLine("VIS " + this + " -> " + mVisibility);
                    }
                }
            }
            private Visibility mVisibility;

            /// <summary>
            /// True if this item is available for assignment.
            /// </summary>
            public bool IsAvailable { get { return Visibility != Visibility.Visible; } }

            public ControlMapItem(UpdateOption updater, FrameworkElement visElem) {
                Updater = updater;
                VisElem = visElem;

                Binding binding = new Binding("Visibility") { Source = this };
                visElem.SetBinding(FrameworkElement.VisibilityProperty, binding);
            }

            public abstract void AssignControl(string tag, string uiString, string defVal,
                string? radioVal = null);

            public void HideControl() {
                Visibility = Visibility.Collapsed;
            }

            public override string ToString() {
                return "[CMI: Tag=" + OptTag + "]";
            }
        }

        /// <summary>
        /// ToggleButton item (CheckBox, RadioButton).  Single control.
        /// </summary>
        private class ToggleButtonMapItem : ControlMapItem {
            public ToggleButton Ctrl { get; }

            public string? RadioVal { get; private set; }

            /// <summary>
            /// Change toggle state.
            /// </summary>
            public bool BoolValue {
                get { return mBoolValue; }
                set {
                    if (mBoolValue != value) {
                        mBoolValue = value;
                        OnPropertyChanged();
                        //Debug.WriteLine("VAL: " + this + " --> " + value);
                        if (RadioVal != null) {
                            // Radio buttons only send an update when they become true, and
                            // need to send their tag instead of a boolean.
                            if (value == true) {
                                Updater(OptTag, RadioVal);
                            }
                        } else {
                            // Checkboxes always update.
                            Updater(OptTag, value.ToString());
                        }
                    }
                }
            }
            private bool mBoolValue;

            /// <summary>
            /// Label string in the UI.
            /// </summary>
            public string UIString {
                get { return mUIString; }
                set {
                    if (mUIString != value) {
                        mUIString = value;
                        OnPropertyChanged();
                    }
                }
            }
            private string mUIString = string.Empty;

            public ToggleButtonMapItem(UpdateOption updater, ToggleButton ctrl)
                    : base(updater, ctrl) {
                Ctrl = ctrl;

                Binding binding = new Binding("BoolValue") { Source = this };
                ctrl.SetBinding(ToggleButton.IsCheckedProperty, binding);
                binding = new Binding("UIString") { Source = this };
                ctrl.SetBinding(ToggleButton.ContentProperty, binding);
            }

            public override void AssignControl(string tag, string uiString, string defVal,
                    string? radioVal = null) {
                OptTag = tag;
                UIString = uiString;
                RadioVal = radioVal;

                Visibility = Visibility.Visible;
                if (bool.TryParse(defVal, out bool value)) {
                    BoolValue = value;
                }
            }

            public override string ToString() {
                return "[TBMI name=" + Ctrl.Name + " tag=" + OptTag + "]";
            }
        }

        /// <summary>
        /// String input item.  These have three parts: a StackPanel wrapped around a TextBlock
        /// for the label and a TextBox for the input.
        /// </summary>
        private class TextBoxMapItem : ControlMapItem {
            public StackPanel Panel { get; private set; }
            public TextBlock Label { get; private set; }
            public TextBox Box { get; private set; }

            /// <summary>
            /// Input field state.
            /// </summary>
            public string StringValue {
                get { return mStringValue; }
                set {
                    if (mStringValue != value) {
                        mStringValue = value;
                        OnPropertyChanged();
                        //Debug.WriteLine("VAL: " + this + " --> '" + value + "'");
                        Updater(OptTag, value);
                    }
                }
            }
            private string mStringValue = string.Empty;

            /// <summary>
            /// Label string, to the left of the input box.
            /// </summary>
            public string UIString {
                get { return mUIString; }
                set {
                    // Add a colon to make it look nicer.
                    string modValue = value + ':';
                    if (mUIString != modValue) {
                        mUIString = modValue;
                        OnPropertyChanged();
                    }
                }
            }
            private string mUIString = string.Empty;

            public TextBoxMapItem(UpdateOption updater, StackPanel panel, TextBlock label,
                    TextBox box) : base(updater,panel) {
                Panel = panel;
                Label = label;
                Box = box;

                Binding binding = new Binding("StringValue") {
                    Source = this,
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
                };
                box.SetBinding(TextBox.TextProperty, binding);
                binding = new Binding("UIString") { Source = this };
                label.SetBinding(TextBlock.TextProperty, binding);
            }

            public override void AssignControl(string tag, string uiString, string defVal,
                    string? unused = null) {
                OptTag = tag;
                UIString = uiString;
                Visibility = Visibility.Visible;
                StringValue = defVal;
            }

            public override string ToString() {
                return "[TBMI name=" + Box.Name + " tag=" + OptTag + "]";
            }
        }

        /// <summary>
        /// Radio button group.  Contains multiple toggle button items.
        /// </summary>
        private class RadioButtonGroupItem : ControlMapItem {
            public GroupBox Group { get; private set; }

            public ToggleButtonMapItem[] ButtonItems { get; }

            /// <summary>
            /// Input field state.
            /// </summary>
            public string StringValue {
                get { return mStringValue; }
                set {
                    if (mStringValue != value) {
                        mStringValue = value;
                        OnPropertyChanged();
                        //Debug.WriteLine("VAL: " + this + " --> '" + value + "'");
                        Updater(OptTag, value);
                    }
                }
            }
            private string mStringValue = string.Empty;

            /// <summary>
            /// Label for the group box.
            /// </summary>
            public string UIString {
                get { return mUIString; }
                set {
                    if (mUIString != value) {
                        mUIString = value;
                        OnPropertyChanged();
                    }
                }
            }
            private string mUIString = string.Empty;

            public RadioButtonGroupItem(UpdateOption updater, GroupBox groupBox,
                    RadioButton[] buttons) : base(updater, groupBox) {
                Group = groupBox;

                Binding binding = new Binding("UIString") { Source = this };
                groupBox.SetBinding(GroupBox.HeaderProperty, binding);

                ButtonItems = new ToggleButtonMapItem[buttons.Length];
                for (int i = 0; i < buttons.Length; i++) {
                    RadioButton button = buttons[i];
                    ButtonItems[i] = new ToggleButtonMapItem(updater, button);
                }
            }

            public override void AssignControl(string tag, string uiString, string defVal,
                    string? unused = null) {
                OptTag = tag;
                UIString = uiString;
                Visibility = Visibility.Visible;
            }

            public override string ToString() {
                return "[RGBI name=" + Group.Name + " tag=" + OptTag + "]";
            }
        }

        private List<ControlMapItem> mCustomCtrls = new List<ControlMapItem>();

        /// <summary>
        /// Creates a map of the configurable controls.  The controls are defined in the
        /// "options" section of the XAML.
        /// </summary>
        private void CreateControlMap() {
            mCustomCtrls.Add(new ToggleButtonMapItem(UpdateOption, checkBox1));
            mCustomCtrls.Add(new ToggleButtonMapItem(UpdateOption, checkBox2));
            mCustomCtrls.Add(new ToggleButtonMapItem(UpdateOption, checkBox3));
            mCustomCtrls.Add(new TextBoxMapItem(UpdateOption, stringInput1, stringInput1_Label,
                stringInput1_Box));
            mCustomCtrls.Add(new RadioButtonGroupItem(UpdateOption, multiGroup1,
                new RadioButton[] { radioButton1_1, radioButton1_2, radioButton1_3,
                    radioButton1_4 }));
        }

        /// <summary>
        /// Configures the converter option dictionary.
        /// </summary>
        /// <param name="conv">Converter to configure for.</param>
        private void PrepareOptions(Converter conv) {
            mConvOptions.Clear();
            foreach (OptionDefinition optDef in conv.OptionDefs) {
                string optTag = optDef.OptTag;
                string cfgKey = MakeConfigKey(conv.Tag, optTag);
                if (mLocalSettings.Exists(cfgKey)) {
                    mConvOptions[optTag] = mLocalSettings.GetString(cfgKey, string.Empty);
                } else {
                    mConvOptions[optTag] = optDef.DefaultVal;
                }
            }
        }

        /// <summary>
        /// Configures the controls for a specific converter.
        /// </summary>
        /// <remarks>
        /// The initial default value is determined by the option's default value and the
        /// local settings.
        /// </remarks>
        /// <param name="conv">Converter to configure for.</param>
        private void ConfigureControls(Converter conv) {
            mIsConfiguring = true;
            Debug.WriteLine("Configure controls for " + conv);
            foreach (ControlMapItem item in mCustomCtrls) {
                item.HideControl();

                if (item is RadioButtonGroupItem) {
                    ToggleButtonMapItem[] items = ((RadioButtonGroupItem)item).ButtonItems;
                    foreach (ToggleButtonMapItem tbItem in items) {
                        tbItem.HideControl();
                    }
                }
            }

            // Show or hide the "no options" message.
            if (conv.OptionDefs.Count == 0) {
                noOptions.Visibility = Visibility.Visible;
            } else {
                noOptions.Visibility = Visibility.Collapsed;
            }

            foreach (OptionDefinition optDef in conv.OptionDefs) {
                string cfgKey = MakeConfigKey(conv.Tag, optDef.OptTag);
                string defaultVal;
                if (mLocalSettings.Exists(cfgKey)) {
                    defaultVal = mLocalSettings.GetString(cfgKey, string.Empty);
                } else {
                    defaultVal = optDef.DefaultVal;
                }

                ControlMapItem item;
                switch (optDef.Type) {
                    case OptionDefinition.OptType.Boolean:
                        item = FindFirstAvailable(typeof(ToggleButtonMapItem));
                        item.AssignControl(optDef.OptTag, optDef.OptLabel, defaultVal);
                        break;
                    case OptionDefinition.OptType.IntValue:
                        item = FindFirstAvailable(typeof(TextBoxMapItem));
                        item.AssignControl(optDef.OptTag, optDef.OptLabel, defaultVal);
                        break;
                    case OptionDefinition.OptType.Multi:
                        RadioButtonGroupItem rbg =
                            (RadioButtonGroupItem)FindFirstAvailable(typeof(RadioButtonGroupItem));
                        rbg.AssignControl(optDef.OptTag, optDef.OptLabel, defaultVal);
                        // If we run out of buttons we'll crash.
                        for (int i = 0; i < optDef.MultiTags!.Length; i++) {
                            string rbTag = optDef.MultiTags[i];
                            string rbLabel = optDef.MultiDescrs![i];
                            // Always set the first button to ensure we have something set.
                            string isSet = (i == 0 || rbTag == defaultVal).ToString();
                            rbg.ButtonItems[i].AssignControl(optDef.OptTag, rbLabel, isSet, rbTag);
                        }
                        break;
                    default:
                        throw new NotImplementedException("Unknown optdef type " + optDef.Type);
                }
            }

            mIsConfiguring = false;
        }

        /// <summary>
        /// Finds the first available control of the specified type.  If none are available, crash.
        /// </summary>
        private ControlMapItem FindFirstAvailable(Type ctrlType) {
            foreach (ControlMapItem item in mCustomCtrls) {
                if (item.GetType() == ctrlType && item.IsAvailable) {
                    return item;
                }
            }
            throw new NotImplementedException("Not enough instances of " + ctrlType);
        }

        /// <summary>
        /// Makes a key into the settings dictionary.
        /// </summary>
        /// <param name="convTag">Converter tag.</param>
        /// <param name="optTag">Option tag.</param>
        /// <returns>Key.</returns>
        private static string MakeConfigKey(string convTag, string optTag) {
            return "FileExport-" + convTag + ":" + optTag;
        }

        private bool mIsConfiguring;

        /// <summary>
        /// Updates an option as the result of UI interaction.
        /// </summary>
        /// <param name="tag">Tag of option to update.</param>
        /// <param name="newValue">New value.</param>
        private void UpdateOption(string tag, string newValue) {
            if (mIsConfiguring) {
                Debug.WriteLine("IGNORING set '" + tag + "' = '" + newValue + "'");
                return;
            }

            mConvOptions[tag] = newValue;

            ConverterComboItem? item = (ConverterComboItem)convComboBox.SelectedItem;
            Debug.Assert(item != null);
            string cfgKey = MakeConfigKey(item.Converter.Tag, tag);
            Debug.WriteLine("Set option '" + cfgKey + "' = '" + newValue + "'");
            mLocalSettings.SetString(cfgKey, newValue);

            FormatFile();
        }

        #endregion Configure
    }
}
