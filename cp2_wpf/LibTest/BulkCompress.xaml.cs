/*
 * Copyright 2022 faddenSoft
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

using CommonUtil;
using static DiskArc.Defs;

namespace cp2_wpf.LibTest {
    /// <summary>
    /// Bulk compression test runner.
    /// </summary>
    public partial class BulkCompress : Window, INotifyPropertyChanged {
        private BackgroundWorker mWorker;

        private CompressionFormat mFormat;
        private AppHook mAppHook;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string propertyName = "") {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// True when we're not running.  Used to enable the "run test" button.
        /// </summary>
        public bool CanStartRunning {
            get { return mCanStartRunning; }
            set { mCanStartRunning = value; OnPropertyChanged(); }
        }
        private bool mCanStartRunning;

        /// <summary>
        /// Pathname of disk image or file archive to test.
        /// </summary>
        public string PathName {
            get { return mPathName; }
            set { mPathName = value; OnPropertyChanged(); PathNameChanged();  }
        }
        private string mPathName;

        /// <summary>
        /// Current label for Run/Cancel button.
        /// </summary>
        public string RunButtonLabel {
            get { return mRunButtonLabel; }
            set { mRunButtonLabel = value; OnPropertyChanged(); }
        }
        private string mRunButtonLabel = string.Empty;

        /// <summary>
        /// Progress message text..
        /// </summary>
        public string ProgressMsg {
            get { return mProgressMsg; }
            set { mProgressMsg = value; OnPropertyChanged(); }
        }
        private string mProgressMsg = string.Empty;


        public BulkCompress(Window owner, AppHook appHook) {
            InitializeComponent();
            Owner = owner;
            DataContext = this;

            mPathName = string.Empty;
            mAppHook = appHook;

            // Create and configure the BackgroundWorker.
            mWorker = new BackgroundWorker();
            mWorker.WorkerReportsProgress = true;
            mWorker.WorkerSupportsCancellation = true;
            mWorker.DoWork += BackgroundWorker_DoWork;
            mWorker.ProgressChanged += BackgroundWorker_ProgressChanged;
            mWorker.RunWorkerCompleted += BackgroundWorker_RunWorkerCompleted;

            CanStartRunning = false;    // need pathname first
            RunButtonLabel = (string)FindResource("str_RunTest");
            radioCompressNuLZW2.IsChecked = true;

            ResetLog();
        }

        /// <summary>
        /// Handles a click on the "choose file" button.  Populates the pathname property.
        /// </summary>
        private void ChooseFileButton_Click(object sender, RoutedEventArgs e) {
            string pathName = WinUtil.AskFileToOpen();
            if (string.IsNullOrEmpty(pathName)) {
                return;
            }
            PathName = pathName;
        }

        /// <summary>
        /// Handles a click on the "run test" button, which becomes a "cancel test" button once
        /// the test has started.
        /// </summary>
        private void RunCancelButton_Click(object sender, RoutedEventArgs e) {
            if (mWorker.IsBusy) {
                //CanStartRunning = false;
                mWorker.CancelAsync();
            } else {
                ResetLog();
                chooseFileButton.IsEnabled = false;
                RunButtonLabel = (string)FindResource("str_CancelTest");
                mWorker.RunWorkerAsync(mPathName);
            }
        }

        /// <summary>
        /// Cancels the test if the user closes the window.
        /// </summary>
        private void Window_Closing(object sender, CancelEventArgs e) {
            if (mWorker.IsBusy) {
                mWorker.CancelAsync();
            }
        }

        private void PathNameChanged() {
            // Shouldn't be possible to change the pathname while the test is running, so we
            // can enable the test based on the presence of a pathname.
            CanStartRunning = !string.IsNullOrEmpty(PathName);
        }

        private void ResetLog() {
            logTextBox.Clear();
            //logTextBox.AppendText("Data source: " + mPathName + "\r\n");
            ProgressMsg = "Ready";
        }

        private void CompGroup_CheckedChanged(object sender, RoutedEventArgs e) {
            if (radioCompressSqueeze.IsChecked == true) {
                mFormat = CompressionFormat.Squeeze;
            } else if (radioCompressNuLZW1.IsChecked == true) {
                mFormat = CompressionFormat.NuLZW1;
            } else if (radioCompressNuLZW2.IsChecked == true) {
                mFormat = CompressionFormat.NuLZW2;
            } else if (radioCompressDeflate.IsChecked == true) {
                mFormat = CompressionFormat.Deflate;
            } else if (radioCompressLZC12.IsChecked == true) {
                mFormat = CompressionFormat.LZC12;
            } else if (radioCompressLZC16.IsChecked == true) {
                mFormat = CompressionFormat.LZC16;
            } else {
                // Shouldn't happen unless nothing is checked.
                mFormat = CompressionFormat.Uncompressed;
            }
        }

        // NOTE: executes on work thread.  DO NOT do any UI work here.  Pass the test
        // results through e.Result.
        private void BackgroundWorker_DoWork(object? sender, DoWorkEventArgs e) {
            BackgroundWorker? worker = sender as BackgroundWorker;
            if (worker == null) {
                throw new Exception("BackgroundWorker WTF?");
            }

            if (e.Argument == null) {
                worker.ReportProgress(0, new ProgressMessage("Pathname was null"));
                return;
            }

            string pathName = (string)e.Argument;
            BulkCompressTest.RunTest(worker, pathName, mFormat, mAppHook);

            // Tests have finished, or were cancelled.  Set results appropriately.
            if (worker.CancellationPending) {
                e.Cancel = true;
            }
        }

        // Callback that fires when a progress update is made.
        private void BackgroundWorker_ProgressChanged(object? sender, ProgressChangedEventArgs e) {
            ProgressMessage? msg = e.UserState as ProgressMessage;
            if (msg == null) {
                // try it as a plain string
                string? str = e.UserState as string;
                if (str != null) {
                    ProgressMsg = str;
                }
            } else {
                logTextBox.AppendText(msg.Text);
            }
            logTextBox.ScrollToEnd();
        }

        // Callback that fires when execution completes.
        private void BackgroundWorker_RunWorkerCompleted(object? sender,
                RunWorkerCompletedEventArgs e) {
            if (e.Cancelled) {
                ProgressMsg = "Halted (user cancellation)";
            } else if (e.Error != null) {
                ProgressMsg = "Failed";
                Debug.WriteLine("Test harness failed: " + e.Error.ToString());
                logTextBox.AppendText("\r\n");
                logTextBox.AppendText(e.Error.ToString());
                logTextBox.ScrollToEnd();
            } else {
                ProgressMsg = "Test complete";
                //List<TestRunner.TestResult>? results = e.Result as List<TestRunner.TestResult>;
                //if (results != null) {
                //    // do stuff
                //}
            }

            RunButtonLabel = (string)FindResource("str_RunTest");
            chooseFileButton.IsEnabled = true;
            CanStartRunning = true;
        }
    }
}
