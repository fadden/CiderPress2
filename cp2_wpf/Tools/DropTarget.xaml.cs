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
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using CommonUtil;
using cp2_wpf.WPFCommon;

namespace cp2_wpf.Tools {
    /// <summary>
    /// Drop / paste target window, for testing.
    /// </summary>
    public partial class DropTarget : Window, INotifyPropertyChanged {
        // INotifyPropertyChanged implementation
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string propertyName = "") {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public string TextArea {
            get { return mTextArea; }
            set { mTextArea = value; OnPropertyChanged(); }
        }
        private string mTextArea = "Drag or paste stuff here.";

        private Formatter mFormatter;


        public DropTarget() {
            InitializeComponent();
            Owner = null;
            DataContext = this;

            mFormatter = new Formatter(new Formatter.FormatConfig());
        }

        // This is a modeless dialog window, so we need to Close() rather than set DialogResult.
        private void CancelButton_Click(object sender, RoutedEventArgs e) {
            Close();
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e) {
            // Handle Ctrl+V (but not Ctrl+Shift+V or other variants).
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.V) {
                e.Handled = true;
                DoPaste();
            }
        }

        private void PasteButton_Click(object sender, RoutedEventArgs e) {
            DoPaste();
        }

        private void DoPaste() {
            IDataObject clipData = Clipboard.GetDataObject();
            ShowDataObject(clipData);
        }

        private void TextArea_Drop(object sender, DragEventArgs e) {
            IDataObject dropData = e.Data;
            ShowDataObject(dropData);
        }

        // Need to do this because the TextBox only accepts text by default, and it doesn't even
        // do that when IsReadOnly is set.
        private void TextBox_PreviewDragOver(object sender, DragEventArgs e) {
            e.Handled = true;
        }

        /// <summary>
        /// Dumps the contents of a data object.
        /// </summary>
        private void ShowDataObject(IDataObject dataObj) {
            StringBuilder sb = new StringBuilder();

            string[] formats = dataObj.GetFormats(false);

            sb.Append("Found " + formats.Length + " formats:\r\n");
            foreach (string format in formats) {
                sb.Append("\u2022 ");
                sb.Append(format);
                if (format == ClipHelper.DESC_ARRAY_FORMAT) {
                    sb.Append(":\r\n");
                    DumpDescriptors(dataObj, sb);
                    continue;
                } else if (format == ClipHelper.FILE_CONTENTS_FORMAT) {
                    // Should have been accompanied by descriptor array, and dumped with that.
                    // The data cannot be obtained from GetData(), since it's an array of streams.
                    sb.Append(": (shown in descriptor section)\r\n");
                    continue;
                } else if (format == ClipInfo.CLIPBOARD_FORMAT_NAME) {
                    sb.Append(":\r\n");
                    // Dump the full text of the JSON stream for debugging.
                    MemoryStream cerealStream = (MemoryStream)dataObj.GetData(format);
                    using (StreamReader sr = new StreamReader(cerealStream, Encoding.UTF8)) {
                        string cereal = sr.ReadToEnd();
                        sb.AppendLine(cereal);
                    }
                    continue;
                }

                try {
                    object? data = dataObj.GetData(format);
                    if (data != null) {
                        sb.Append(" (");
                        sb.Append(data.GetType().Name);
                        sb.Append(")");
                    } else {
                        sb.Append(" NULL?!");
                    }

                    if (data is MemoryStream) {
                        MemoryStream stream = (MemoryStream)data;
                        sb.Append(" len=");
                        sb.Append(stream.Length);
                        sb.Append("\r\n    ");
                        int showLen = Math.Min((int)stream.Length, 16);
                        byte[] buffer = new byte[showLen];  // not sure if GetBuffer always works
                        stream.Position = 0;
                        stream.Read(buffer, 0, showLen);
                        mFormatter.FormatHexDumpLine(buffer, 0, showLen, 0, sb);
                    } else if (data is string[]) {
                        string[] strArray = (string[])data;
                        for (int i = 0; i < strArray.Length; i++) {
                            sb.Append("\r\n    ");
                            sb.Append(i);
                            sb.Append(": ");
                            sb.Append(strArray[i]);
                        }
                    } else if (data is string) {
                        string str = (string)data;
                        str = str.Replace("\t", "\u2409");      // replace with Control Pictures
                        str = str.Replace("\n", "\u240a");
                        str = str.Replace("\r", "\u240d");
                        sb.Append("\r\n    \u201c");
                        AppendLimitedString(sb, str);
                        sb.Append('\u201d');
                    }
                } catch (Exception ex) {
                    sb.Append(" - GetData failed: ");
                    sb.Append(ex.Message);
                }
                sb.Append("\r\n");
            }

            TextArea = sb.ToString();
        }

        private void DumpDescriptors(IDataObject dataObj, StringBuilder sb) {
            MemoryStream descStream = (MemoryStream)dataObj.GetData(ClipHelper.DESC_ARRAY_FORMAT);
            IEnumerable<ClipHelper.FileDescriptor> descriptors =
                ClipHelper.FileDescriptorReader.Read(descStream);
            int fileIndex = 0;
            foreach (ClipHelper.FileDescriptor desc in descriptors) {
                string fileName = desc.FileName;
                sb.AppendFormat("    {0}: '{1}': ", fileIndex, fileName);
                if ((desc.FileAttributes & FileAttributes.Directory) != 0) {
                    // Handle directory.
                    sb.Append("is directory");
                } else {
                    Debug.WriteLine("+ stream get contents " + fileIndex);
                    using (Stream? contents = ClipHelper.GetFileContents(dataObj, fileIndex)) {
                        if (contents == null) {
                            sb.Append("contents are null");
                        } else {
                            contents.Position = 0;
                            long fileLen = 0;
                            Debug.WriteLine("+ starting read");
                            while (contents.ReadByte() >= 0) {
                                fileLen++;
                            }
                            sb.Append("read " + fileLen + " bytes");
                        }
                    }
                    Debug.WriteLine("+ stream closed");
                }
                fileIndex++;
                sb.Append("\r\n");
            }
        }

        private const int MAX_STRING = 80;
        private static void AppendLimitedString(StringBuilder sb, string str) {
            if (str.Length <= MAX_STRING) {
                sb.Append(str);
                return;
            }
            sb.Append(str.Substring(0, MAX_STRING / 2));
            sb.Append(" [\u2026] ");
            sb.Append(str.Substring(str.Length - MAX_STRING / 2));
        }
    }
}
