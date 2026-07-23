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
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

using Avalonia.Media;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using cp2_avalonia.LibTest;
using cp2_avalonia.Services;

namespace cp2_avalonia.ViewModels;

/// <summary>
/// ViewModel for the Library Test Runner dialog.
/// </summary>
public class TestManagerViewModel : ObservableObject
{
    // -----------------------------------------------------------------------------------------
    // Close interaction

    public event Action<bool>? CloseRequested;

    // -----------------------------------------------------------------------------------------
    // Commands

    public IRelayCommand RunCancelCommand { get; }
    public IRelayCommand CloseCommand { get; }

    // -----------------------------------------------------------------------------------------
    // Progress event — pushes (text, color) pairs to code-behind

    public event Action<(string text, Color color)>? ProgressAppended;

    // -----------------------------------------------------------------------------------------
    // Reset event — tells code-behind to clear progressTextEditor + transformer

    public event Action? ResetRequested;

    // -----------------------------------------------------------------------------------------
    // Properties

    private bool mIsNotRunning;
    public bool IsNotRunning {
        get => mIsNotRunning;
        set => SetProperty(ref mIsNotRunning, value);
    }

    private string mRunButtonLabel = string.Empty;
    public string RunButtonLabel {
        get => mRunButtonLabel;
        set => SetProperty(ref mRunButtonLabel, value);
    }

    public ObservableCollection<TestRunner.TestResult> OutputItems { get; } =
        new ObservableCollection<TestRunner.TestResult>();

    private bool mIsOutputSelectEnabled;
    public bool IsOutputSelectEnabled {
        get => mIsOutputSelectEnabled;
        set => SetProperty(ref mIsOutputSelectEnabled, value);
    }

    private bool mIsOutputRetained;
    public bool IsOutputRetained {
        get => mIsOutputRetained;
        set => SetProperty(ref mIsOutputRetained, value);
    }

    private string mSelectedOutputText = string.Empty;
    public string SelectedOutputText {
        get => mSelectedOutputText;
        set => SetProperty(ref mSelectedOutputText, value);
    }

    // -----------------------------------------------------------------------------------------
    // Private state

    private readonly BackgroundWorker mWorker;
    private readonly string mTestIfaceName;
    private List<TestRunner.TestResult> mLastResults = new List<TestRunner.TestResult>();

    private static readonly Color sDefaultColor = Colors.Black;

    private const string STR_RUN_TEST = "Run Test";
    private const string STR_CANCEL_TEST = "Cancel";

    private const string STR_NO_RESULTS =
        "(No test results yet. Run the tests first.\r\n" +
        "If all tests pass, no results will appear here.\r\n" +
        "Failures will be listed in the drop-down above.)";

    private const string STR_ALL_PASSED =
        "All tests passed. No failures to report.";

    private const string STR_RUN_FAILED =
        "The test could not be run, so there is no data to report.\r\n" +
        "See the output above (and the Debug Log) for details.";

    // -----------------------------------------------------------------------------------------
    // Public interface for code-behind

    /// <summary>
    /// Cancels the background worker if it is currently running.  Called from
    /// code-behind Window_Closing handler.
    /// </summary>
    public void CancelIfBusy() {
        if (mWorker.IsBusy) {
            mWorker.CancelAsync();
        }
    }

    /// <summary>
    /// Updates SelectedOutputText based on the ComboBox selection index.
    /// Called by code-behind SelectionChanged handler.
    /// </summary>
    public void OnOutputSelectChanged(int index) {
        if (index < 0) {
            SelectedOutputText = STR_NO_RESULTS;
            return;
        }
        if (mLastResults.Count <= index) {
            Debug.WriteLine("OnOutputSelectChanged to " + index + ", not available");
            return;
        }

        TestRunner.TestResult result = mLastResults[index];
        StringBuilder sb = new StringBuilder();
        sb.AppendLine(result.Name);
        Exception? ex = result.Exc;
        bool first = true;
        while (ex != null) {
            sb.AppendLine();
            if (first) { first = false; } else { sb.Append("Caused by: "); }
            sb.AppendLine(ex.Message);
            sb.AppendLine(ex.GetType().Name + ":");
            sb.AppendLine(ex.StackTrace);
            ex = ex.InnerException;
        }
        SelectedOutputText = sb.ToString();
    }

    // -----------------------------------------------------------------------------------------
    // Worker implementation

    private void ResetDialog() {
        mLastResults.Clear();
        OutputItems.Clear();
        IsOutputSelectEnabled = false;
        SelectedOutputText = STR_NO_RESULTS;
        ResetRequested?.Invoke();
    }

    // NOTE: executes on work thread — no UI access.
    private void BackgroundWorker_DoWork(object? sender, DoWorkEventArgs e) {
        BackgroundWorker? worker = sender as BackgroundWorker;
        if (worker == null) throw new Exception("BackgroundWorker WTF?");

        TestRunner test = new TestRunner();
        if (e.Argument != null) {
            test.RetainOutput = (bool)e.Argument;
        }
        List<TestRunner.TestResult>? results = test.Run(worker, mTestIfaceName);

        if (worker.CancellationPending) {
            e.Cancel = true;
        } else {
            e.Result = results;
        }
    }

    // Fires on the UI thread.
    private void BackgroundWorker_ProgressChanged(object? sender, ProgressChangedEventArgs e) {
        ProgressMessage? msg = e.UserState as ProgressMessage;
        if (msg == null) {
            string? str = e.UserState as string;
            if (!string.IsNullOrEmpty(str)) {
                Debug.WriteLine("Sub-progress: " + e.UserState);
            }
        } else {
            Color color = msg.HasColor ? msg.Color : sDefaultColor;
            ProgressAppended?.Invoke((msg.Text, color));
        }
    }

    // Fires on the UI thread.
    private void BackgroundWorker_RunWorkerCompleted(object? sender,
            RunWorkerCompletedEventArgs e) {
        if (e.Cancelled) {
            Debug.WriteLine("Test halted -- user cancellation");
        } else if (e.Error != null) {
            AppLog.E("Library test harness failed", e.Error);
            ProgressAppended?.Invoke(("\r\n", sDefaultColor));
            ProgressAppended?.Invoke((e.Error.ToString(), sDefaultColor));
        } else {
            Debug.WriteLine("Tests complete");
            List<TestRunner.TestResult>? results = e.Result as List<TestRunner.TestResult>;
            if (results != null) {
                mLastResults = results;
                PopulateOutputSelect();
            } else {
                // Run aborted before any tests executed (e.g. test data/library not found).
                // Distinguish this from "ran, zero failures" so we don't claim success.
                mLastResults.Clear();
                OutputItems.Clear();
                IsOutputSelectEnabled = false;
                SelectedOutputText = STR_RUN_FAILED;
            }
        }

        RunButtonLabel = STR_RUN_TEST;
        IsNotRunning = true;
    }

    private void PopulateOutputSelect() {
        OutputItems.Clear();
        if (mLastResults.Count == 0) {
            IsOutputSelectEnabled = false;
            SelectedOutputText = STR_ALL_PASSED;
            return;
        }
        foreach (TestRunner.TestResult result in mLastResults) {
            OutputItems.Add(result);
        }
        IsOutputSelectEnabled = true;
        // Pre-populate text for index 0 (ComboBox will default there).
        OnOutputSelectChanged(0);
    }

    // -----------------------------------------------------------------------------------------
    // Constructor

    public TestManagerViewModel(string testIfaceName) {
        mTestIfaceName = testIfaceName;

        mWorker = new BackgroundWorker();
        mWorker.WorkerReportsProgress = true;
        mWorker.WorkerSupportsCancellation = true;
        mWorker.DoWork += BackgroundWorker_DoWork;
        mWorker.ProgressChanged += BackgroundWorker_ProgressChanged;
        mWorker.RunWorkerCompleted += BackgroundWorker_RunWorkerCompleted;

        IsNotRunning = true;
        RunButtonLabel = STR_RUN_TEST;
        IsOutputSelectEnabled = false;
        SelectedOutputText = STR_NO_RESULTS;

        RunCancelCommand = new RelayCommand(() => {
            if (mWorker.IsBusy) {
                mWorker.CancelAsync();
            } else {
                ResetDialog();
                RunButtonLabel = STR_CANCEL_TEST;
                mWorker.RunWorkerAsync(mIsOutputRetained);
            }
        });

        CloseCommand = new AsyncRelayCommand(
            async () => CloseRequested?.Invoke(false));
    }
}
