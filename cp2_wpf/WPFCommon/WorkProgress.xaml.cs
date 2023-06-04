/*
 * Copyright 2019 faddenSoft
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
using System.Threading;
using System.Windows;

namespace cp2_wpf.WPFCommon {
    /// <summary>
    /// Cancellable progress dialog.
    /// </summary>
    /// <remarks>
    /// The dialog will return True if the operation ran to completion, False if it was cancelled
    /// or halted early with an error.
    /// </remarks>
    public partial class WorkProgress : Window {
        /// <summary>
        /// Task-specific stuff.
        /// </summary>
        public interface IWorker {
            /// <summary>
            /// Does the work, executing on a work thread.
            /// </summary>
            /// <param name="worker">BackgroundWorker object reference.</param>
            /// <returns>Results of work.</returns>
            object DoWork(BackgroundWorker worker);

            /// <summary>
            /// Called on successful completion of the work.  Executes on main thread.
            /// </summary>
            /// <param name="results">Results of work.</param>
            /// <returns>Value to return from dialog (usually true on success).</returns>
            bool RunWorkerCompleted(object? results);
        }

        /// <summary>
        /// Message box query, sent from a non-GUI thread via the progress update mechanism.
        /// </summary>
        public class MessageBoxQuery {
            private const MessageBoxResult RESULT_UNSET = (MessageBoxResult)(-1000);

            public string Text { get; private set; }
            public string Caption { get; private set; }
            public MessageBoxButton Button { get; private set; }
            public MessageBoxImage Image { get; private set; }

            private MessageBoxResult mResult = RESULT_UNSET;
            private object mLockObj = new object();

            /// <summary>
            /// Wait for a result to arrive on the worker side.  Call this after ReportProgress().
            /// </summary>
            /// <returns>Result from <see cref="MessageBox.Show"/>.</returns>
            public MessageBoxResult WaitForResult() {
                lock (mLockObj) {
                    while (mResult == RESULT_UNSET) {
                        Monitor.Wait(mLockObj);
                    }
                    return mResult;
                }
            }

            /// <summary>
            /// Sets the result.  Call this from ReportProgress() on the GUI side.
            /// </summary>
            /// <param name="value">Result from <see cref="MessageBox.Show"/>.</param>
            public void SetResult(MessageBoxResult value) {
                lock (mLockObj) {
                    mResult = value;
                    Monitor.Pulse(mLockObj);
                }
            }

            /// <summary>
            /// Constructor.
            /// </summary>
            /// <param name="text">String to show in the message box.</param>
            /// <param name="caption">String to show in the caption.</param>
            /// <param name="button">Enumerated value for buttons to show.</param>
            /// <param name="image">Enumerated value for icons to show.</param>
            public MessageBoxQuery(string text, string caption, MessageBoxButton button,
                    MessageBoxImage image) {
                Text = text;
                Caption = caption;
                Button = button;
                Image = image;
            }
        }

        private IWorker mCallbacks;
        private BackgroundWorker mWorker;


        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="owner">Window parent.</param>
        /// <param name="callbacks">Object with start/finish code.</param>
        /// <param name="isIndeterminate">True if the progress bar should display as an
        ///   animated solid rather than fill in.</param>
        public WorkProgress(Window owner, IWorker callbacks, bool isIndeterminate) {
            InitializeComponent();
            Owner = owner;

            progressBar.IsIndeterminate = isIndeterminate;

            mCallbacks = callbacks;

            // Create and configure the BackgroundWorker.
            mWorker = new BackgroundWorker();
            mWorker.WorkerReportsProgress = true;
            mWorker.WorkerSupportsCancellation = true;
            mWorker.DoWork += DoWork;
            mWorker.ProgressChanged += ProgressChanged;
            mWorker.RunWorkerCompleted += RunWorkerCompleted;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e) {
            mWorker.RunWorkerAsync();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) {
            mWorker.CancelAsync();
            cancelButton.IsEnabled = false;
        }

        private void Window_Closing(object sender, CancelEventArgs e) {
            // Either we're closing naturally, or the user clicked the 'X' in the window frame.
            // If we were running, treat it as a cancel request.
            if (mWorker.IsBusy) {
                Debug.WriteLine("Close requested, issuing cancel");
                mWorker.CancelAsync();
                e.Cancel = true;
            }
        }

        // NOTE: executes on work thread.  DO NOT do any UI work here.
        private void DoWork(object? sender, DoWorkEventArgs e) {
            Debug.Assert(sender == mWorker);

            object results = mCallbacks.DoWork(mWorker);
            if (mWorker.CancellationPending) {
                e.Cancel = true;
            } else {
                e.Result = results;
            }
        }

        /// <summary>
        /// Executes when a progress update is sent from the work thread.  The update can be
        /// a MessageBox request, or a simple string update with a completion percentage.
        /// </summary>
        /// <remarks>
        /// <para>Message box queries must wait for the result.  Otherwise the progress dialog
        /// can close, cancelling the MessageBox and confusing the window manager.</para>
        /// </remarks>
        private void ProgressChanged(object? sender, ProgressChangedEventArgs e) {
            MessageBoxQuery? qq = e.UserState as MessageBoxQuery;
            if (qq != null) {
                MessageBoxResult mbr =
                    MessageBox.Show(this, qq.Text, qq.Caption, qq.Button, qq.Image);
                qq.SetResult(mbr);
                return;
            }

            string? msg = e.UserState as string;
            if (!string.IsNullOrEmpty(msg)) {
                messageText.Text = msg;
            }

            int percent = e.ProgressPercentage;
            if (percent >= 0 && percent <= 100) {
                progressBar.Value = percent;
            }
        }

        // Callback that fires when execution completes.
        private void RunWorkerCompleted(object? sender, RunWorkerCompletedEventArgs e) {
            if (e.Cancelled) {
                Debug.WriteLine("CANCELLED " + DialogResult);
                // If the window was closed, the DialogResult will already be set, and WPF
                // throws a misleading exception ("only after Window is created and shown")
                // if you try to set the result twice.
                if (DialogResult == null) {
                    DialogResult = false;
                }
            } else if (e.Error != null) {
                // Unexpected; success/failure should be passed through e.Result.
                string failMsg = (string)FindResource("str_OperationFailedCaption");
                MessageBox.Show(e.Error.ToString(), failMsg,
                    MessageBoxButton.OK, MessageBoxImage.Error);
                DialogResult = false;
            } else {
                // On success, return "true" from the dialog.
                DialogResult = mCallbacks.RunWorkerCompleted(e.Result);
            }
        }
    }
}
