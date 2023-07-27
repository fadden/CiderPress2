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
using System.Windows.Documents;

using CommonUtil;
using DiskArc;
using DiskArc.FS;
using FileConv;
using Microsoft.Win32;
using static cp2_wpf.ConfigOptCtrl;

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


        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="owner">Parent window.</param>
        /// <param name="archiveOrFileSystem">IArchive or IFileSystem object reference.</param>
        /// <param name="selected">List of selected file entries.</param>
        /// <param name="firstSel">Index of first selection.  Useful when a single selection is
        ///   converted into a full dir selection.</param>
        /// <param name="appHook">Application hook reference.</param>
        public FileViewer(Window owner, object archiveOrFileSystem, List<IFileEntry> selected,
                int firstSel, AppHook appHook) {
            InitializeComponent();
            Owner = owner;
            DataContext = this;

            Debug.Assert(selected.Count > 0);
            mArchiveOrFileSystem = archiveOrFileSystem;
            mSelected = selected;
            mCurIndex = firstSel;
            mAppHook = appHook;

            mConvOptions = new Dictionary<string, string>();

            CreateControlMap();
        }

        /// <summary>
        /// When the window finishes setting up, before it's made visible, configure the dialog
        /// for the first item in the selection.
        /// </summary>
        private void Window_SourceInitialized(object sender, EventArgs e) {
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
                bool macZipEnabled = AppSettings.Global.GetBool(AppSettings.MAC_ZIP_ENABLED, true);
                applics = ExportFoundry.GetApplicableConverters(mArchiveOrFileSystem,
                    entry, attrs, IsDOSRaw, macZipEnabled, out mDataFork, out mRsrcFork, mAppHook);
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
            } else {
                HideConvControls(mCustomCtrls);
                noOptions.Visibility = Visibility.Visible;
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

        private const int MAX_FANCY_TEXT = 1024 * 1024 * 2;     // 2MB
        private const int MAX_SIMPLE_TEXT = 1024 * 1024 * 2;    // 2MB

        /// <summary>
        /// Reformats the file when the conversion combo box selection changes.
        /// </summary>
        private void ConvComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) {
            ConverterComboItem? item = (ConverterComboItem)convComboBox.SelectedItem;
            if (item == null) {
                // This happens when the combo box is cleared for a new file.
                return;
            }

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

        /// <summary>
        /// Selects the converter combo box item for the specified conversion type.  Used for
        /// the text/hex/best buttons.
        /// </summary>
        private void SelectConversion(Type convType) {
            foreach (ConverterComboItem item in convComboBox.Items) {
                if (item.Converter.GetType() == convType) {
                    convComboBox.SelectedItem = item;
                    break;
                }
            }
        }

        /// <summary>
        /// Formats the current file with the selected converter.
        /// </summary>
        private void FormatFile() {
            ConverterComboItem? item = (ConverterComboItem)convComboBox.SelectedItem;
            Debug.Assert(item != null);

            // Do the conversion.
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
            IConvOutput? convOut;
            string prefix;
            if (mCurRsrcOutput != null && tabControl.SelectedItem == rsrcTabItem) {
                convOut = mCurRsrcOutput;
                prefix = ".r";
            } else {
                convOut = mCurDataOutput;
                prefix = "";
            }

            string filter, ext;
            if (convOut is FancyText && !((FancyText)convOut).PreferSimple) {
                filter = WinUtil.FILE_FILTER_RTF;
                ext = prefix + ".rtf";
            } else if (convOut is SimpleText) {
                filter = WinUtil.FILE_FILTER_TEXT;
                ext = prefix + ".txt";
            } else if (convOut is CellGrid) {
                filter = WinUtil.FILE_FILTER_CSV;
                ext = prefix + ".csv";
            } else if (convOut is IBitmap) {
                filter = WinUtil.FILE_FILTER_PNG;
                ext = prefix + ".png";
            } else {
                Debug.Assert(false, "not handling " + convOut.GetType().Name);
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
                    if (convOut is FancyText && !((FancyText)convOut).PreferSimple) {
                        StringBuilder sb = ((FancyText)convOut).Text;
                        RTFGenerator.Generate((FancyText)convOut, outStream);
                    } else if (convOut is SimpleText) {
                        StringBuilder sb = ((SimpleText)convOut).Text;
                        using (StreamWriter sw = new StreamWriter(outStream)) {
                            sw.Write(sb);
                        }
                    } else if (convOut is CellGrid) {
                        StringBuilder sb = new StringBuilder();
                        CSVGenerator.GenerateString((CellGrid)convOut, false, sb);
                        using (StreamWriter sw = new StreamWriter(outStream)) {
                            sw.Write(sb);
                        }
                    } else if (convOut is IBitmap) {
                        PNGGenerator.Generate((IBitmap)convOut, outStream);
                    } else {
                        Debug.Assert(false, "not handling " + convOut.GetType().Name);
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
            mCustomCtrls.Add(new RadioButtonGroupItem(UpdateOption, multiGroup2,
                new RadioButton[] { radioButton2_1, radioButton2_2, radioButton2_3,
                    radioButton2_4 }));
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
            Debug.WriteLine("Configure controls for " + conv);
            mIsConfiguring = true;

            // Configure options for every control.  If not present in the config file, set
            // the control's default.
            mConvOptions = ConfigOptCtrl.LoadExportOptions(conv.OptionDefs,
                AppSettings.VIEW_SETTING_PREFIX, conv.Tag);

            ConfigOptCtrl.HideConvControls(mCustomCtrls);

            // Show or hide the "no options" message.
            if (conv.OptionDefs.Count == 0) {
                noOptions.Visibility = Visibility.Visible;
            } else {
                noOptions.Visibility = Visibility.Collapsed;
            }

            ConfigOptCtrl.ConfigureControls(mCustomCtrls, conv.OptionDefs, mConvOptions);

            mIsConfiguring = false;
        }

        private bool mIsConfiguring;

        /// <summary>
        /// Updates an option as the result of UI interaction.
        /// </summary>
        /// <param name="tag">Tag of option to update.</param>
        /// <param name="newValue">New value.</param>
        private void UpdateOption(string tag, string newValue) {
            if (mIsConfiguring) {
                Debug.WriteLine("Ignoring initial set '" + tag + "' = '" + newValue + "'");
                return;
            }

            // Get converter tag, so we can form the settings file key.
            ConverterComboItem? item = (ConverterComboItem)convComboBox.SelectedItem;
            Debug.Assert(item != null);
            string settingKey = AppSettings.VIEW_SETTING_PREFIX + item.Converter.Tag;

            // Update setting, and generate the new setting string.
            if (string.IsNullOrEmpty(newValue)) {
                mConvOptions.Remove(tag);
            } else {
                mConvOptions[tag] = newValue;
            }
            string optStr = ConvConfig.GenerateOptString(mConvOptions);

            // Enable the button if the config string doesn't match what's in the app settings.
            // We don't actually update the settings here.
            IsSaveDefaultsEnabled =
                (AppSettings.Global.GetString(settingKey, string.Empty) != optStr);
            //Debug.WriteLine("CMP '" + AppSettings.Global.GetString(settingKey, string.Empty) +
            //    "' vs '" + optStr + "'");

            // Update the formatted file output.
            FormatFile();
        }

        public bool IsSaveDefaultsEnabled {
            get { return mIsSaveDefaultsEnabled; }
            set { mIsSaveDefaultsEnabled = value; OnPropertyChanged(); }
        }
        private bool mIsSaveDefaultsEnabled;

        /// <summary>
        /// Handles a click on the "save as defaults" button.
        /// </summary>
        private void SaveDefaultsButton_Click(object sender, RoutedEventArgs e) {
            ConverterComboItem? item = (ConverterComboItem)convComboBox.SelectedItem;
            Debug.Assert(item != null);

            string optStr = ConvConfig.GenerateOptString(mConvOptions);
            string settingKey = AppSettings.VIEW_SETTING_PREFIX + item.Converter.Tag;
            AppSettings.Global.SetString(settingKey, optStr);
            IsSaveDefaultsEnabled = false;
        }

        #endregion Configure
    }
}
