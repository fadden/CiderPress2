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
using System.Windows.Documents;
using System.Windows.Media;

using cp2_wpf.WPFCommon;

namespace cp2_wpf.LibTest {
    /// <summary>
    /// Executes the DiskArc/FileConv library tests.  The test code is loaded dynamically.
    /// </summary>
    public partial class TestManager : Window, INotifyPropertyChanged {
        // Full set of results returned by previous test run.
        private List<TestRunner.TestResult> mLastResults;

        private BackgroundWorker mWorker;
        private string mTestLibName;
        private string mTestIfaceName;

        private FlowDocument mFlowDoc = new FlowDocument();
        private Color mDefaultColor;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string propertyName = "") {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// True when we're not running.  Used to enable the "run test" button.
        /// </summary>
        public bool IsNotRunning {
            get { return mIsNotRunning; }
            set {
                mIsNotRunning = value;
                OnPropertyChanged();
            }
        }
        private bool mIsNotRunning;

        public bool IsOutputRetained {
            get { return mIsOutputRetained; }
            set {
                mIsOutputRetained = value;
                OnPropertyChanged();
            }
        }
        private bool mIsOutputRetained;

        public string RunButtonLabel {
            get { return mRunButtonLabel; }
            set {
                mRunButtonLabel = value;
                OnPropertyChanged();
            }
        }
        private string mRunButtonLabel = string.Empty;


        public TestManager(Window owner, string testLibName, string testIfaceName) {
            InitializeComponent();
            Owner = owner;
            DataContext = this;

            mTestLibName = testLibName;
            mTestIfaceName = testIfaceName;

            mLastResults = new List<TestRunner.TestResult>(0);
            mDefaultColor = ((SolidColorBrush)progressRichTextBox.Foreground).Color;

            // Create and configure the BackgroundWorker.
            mWorker = new BackgroundWorker();
            mWorker.WorkerReportsProgress = true;
            mWorker.WorkerSupportsCancellation = true;
            mWorker.DoWork += BackgroundWorker_DoWork;
            mWorker.ProgressChanged += BackgroundWorker_ProgressChanged;
            mWorker.RunWorkerCompleted += BackgroundWorker_RunWorkerCompleted;

            IsNotRunning = true;
            RunButtonLabel = (string)FindResource("str_RunTest");
            progressRichTextBox.Document = mFlowDoc;
        }

        /// <summary>
        /// Handles a click on the "run test" button, which becomes a "cancel test" button once
        /// the test has started.
        /// </summary>
        private void RunCancelButton_Click(object sender, RoutedEventArgs e) {
            if (mWorker.IsBusy) {
                IsNotRunning = false;
                mWorker.CancelAsync();
            } else {
                ResetDialog();
                RunButtonLabel = (string)FindResource("str_CancelTest");
                mWorker.RunWorkerAsync(mIsOutputRetained);
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

        private void ResetDialog() {
            mFlowDoc.Blocks.Clear();
            mLastResults.Clear();
        }

        // NOTE: executes on work thread.  DO NOT do any UI work here.  Pass the test
        // results through e.Result.
        private void BackgroundWorker_DoWork(object? sender, DoWorkEventArgs e) {
            BackgroundWorker? worker = sender as BackgroundWorker;
            if (worker == null) {
                throw new Exception("BackgroundWorker WTF?");
            }

            TestRunner test = new TestRunner();
            if (e.Argument != null) {
                test.RetainOutput = (bool)e.Argument;
            }
            List<TestRunner.TestResult> results = test.Run(worker, mTestLibName, mTestIfaceName);

            // Tests have finished, or were cancelled.  Set results appropriately.
            if (worker.CancellationPending) {
                e.Cancel = true;
            } else {
                e.Result = results;
            }
        }

        // Callback that fires when a progress update is made.
        private void BackgroundWorker_ProgressChanged(object? sender, ProgressChangedEventArgs e) {
            ProgressMessage? msg = e.UserState as ProgressMessage;
            if (msg == null) {
                // try it as a plain string
                string? str = e.UserState as string;
                if (!string.IsNullOrEmpty(str)) {
                    Debug.WriteLine("Sub-progress: " + e.UserState);
                }
            } else if (msg.HasColor) {
                progressRichTextBox.AppendText(msg.Text, msg.Color);
            } else {
                // plain foreground text color
                progressRichTextBox.AppendText(msg.Text, mDefaultColor);
            }
            progressRichTextBox.ScrollToEnd();
        }

        // Callback that fires when execution completes.
        private void BackgroundWorker_RunWorkerCompleted(object? sender,
                RunWorkerCompletedEventArgs e) {
            if (e.Cancelled) {
                Debug.WriteLine("Test halted -- user cancellation");
            } else if (e.Error != null) {
                Debug.WriteLine("Test harness failed: " + e.Error.ToString());
                progressRichTextBox.AppendText("\r\n");
                progressRichTextBox.AppendText(e.Error.ToString(), mDefaultColor);
                progressRichTextBox.ScrollToEnd();
            } else {
                Debug.WriteLine("Tests complete");
                List<TestRunner.TestResult>? results = e.Result as List<TestRunner.TestResult>;
                if (results != null) {
                    mLastResults = results;
                    PopulateOutputSelect();
                }
            }

            RunButtonLabel = (string)FindResource("str_RunTest");
            IsNotRunning = true;
        }

        private void PopulateOutputSelect() {
            outputSelectComboBox.Items.Clear();
            if (mLastResults.Count == 0) {
                return;
            }

            foreach (TestRunner.TestResult results in mLastResults) {
                outputSelectComboBox.Items.Add(results);
            }

            // Trigger update.
            outputSelectComboBox.SelectedIndex = 0;
        }

        private void OutputSelectComboBox_SelectedIndexChanged(object sender,
                SelectionChangedEventArgs e) {
            int sel = outputSelectComboBox.SelectedIndex;
            if (sel < 0) {
                // selection has been cleared
                outputTextBox.Text = string.Empty;
                return;
            }
            if (mLastResults == null || mLastResults.Count <= sel) {
                Debug.WriteLine("SelIndexChanged to " + sel + ", not available");
                return;
            }

            TestRunner.TestResult results = mLastResults[sel];

            StringBuilder sb = new StringBuilder();
            sb.AppendLine(results.Name);
            Exception? ex = results.Exc;
            bool first = true;
            while (ex != null) {
                sb.AppendLine();
                if (first) {
                    first = false;
                } else {
                    sb.Append("Caused by: ");
                }
                sb.AppendLine(ex.Message);
                sb.AppendLine(ex.GetType().Name + ":");
                sb.AppendLine(ex.StackTrace);
                ex = ex.InnerException;
            }

            outputTextBox.Text = sb.ToString();
        }
    }
}
