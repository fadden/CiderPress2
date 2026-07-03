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
using System.ComponentModel;
using System.Diagnostics;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using CommonUtil;
using static DiskArc.Defs;

using cp2_avalonia.LibTest;
using cp2_avalonia.Services;

namespace cp2_avalonia.ViewModels;

/// <summary>
/// ViewModel for the Bulk Compress test dialog.
/// </summary>
public class BulkCompressViewModel : ObservableObject
{
    // -----------------------------------------------------------------------------------------
    // Close interaction

    public event Action<bool>? CloseRequested;

    // -----------------------------------------------------------------------------------------
    // Commands

    public IRelayCommand ChooseFileCommand { get; }
    public IRelayCommand RunCancelCommand { get; }
    public IRelayCommand CloseCommand { get; }

    // -----------------------------------------------------------------------------------------
    // Properties

    private bool mCanStartRunning;
    public bool CanStartRunning {
        get => mCanStartRunning;
        private set => SetProperty(ref mCanStartRunning, value);
    }

    // True whenever a path is selected, regardless of whether a run is in progress.
    // Keeps the Run/Cancel button enabled during a run so the user can cancel.
    private bool mIsRunCancelEnabled;
    public bool IsRunCancelEnabled {
        get => mIsRunCancelEnabled;
        private set => SetProperty(ref mIsRunCancelEnabled, value);
    }

    private string mPathName = string.Empty;
    public string PathName {
        get => mPathName;
        set {
            SetProperty(ref mPathName, value);
            bool hasPath = !string.IsNullOrEmpty(value);
            CanStartRunning = hasPath && !mWorker.IsBusy;
            IsRunCancelEnabled = hasPath;
        }
    }

    private string mRunButtonLabel = string.Empty;
    public string RunButtonLabel {
        get => mRunButtonLabel;
        set => SetProperty(ref mRunButtonLabel, value);
    }

    private string mProgressMsg = string.Empty;
    public string ProgressMsg {
        get => mProgressMsg;
        set => SetProperty(ref mProgressMsg, value);
    }

    private string mLogText = string.Empty;
    public string LogText {
        get => mLogText;
        private set => SetProperty(ref mLogText, value);
    }

    private bool mCanChooseFile = true;
    public bool CanChooseFile {
        get => mCanChooseFile;
        private set => SetProperty(ref mCanChooseFile, value);
    }

    // Set by code-behind CompGroup_CheckedChanged forwarding.
    public CompressionFormat SelectedCompressionFormat { get; set; } =
        CompressionFormat.NuLZW2;

    // -----------------------------------------------------------------------------------------
    // Private state

    private readonly BackgroundWorker mWorker;
    private readonly AppHook mAppHook;

    private const string STR_RUN_TEST = "Run Test";
    private const string STR_CANCEL_TEST = "Cancel";

    // -----------------------------------------------------------------------------------------
    // Public interface for code-behind

    public void CancelIfBusy() {
        if (mWorker.IsBusy) {
            mWorker.CancelAsync();
        }
    }

    // -----------------------------------------------------------------------------------------
    // Worker implementation

    private void ResetLog() {
        LogText = string.Empty;
        ProgressMsg = "Ready";
    }

    // NOTE: executes on work thread — no UI access.
    private void BackgroundWorker_DoWork(object? sender, DoWorkEventArgs e) {
        BackgroundWorker? worker = sender as BackgroundWorker;
        if (worker == null) throw new Exception("BackgroundWorker WTF?");

        if (e.Argument == null) {
            worker.ReportProgress(0, new ProgressMessage("Pathname was null"));
            return;
        }

        string pathName = (string)e.Argument;
        BulkCompressTest.RunTest(worker, pathName, SelectedCompressionFormat, mAppHook);

        if (worker.CancellationPending) {
            e.Cancel = true;
        }
    }

    // Fires on the UI thread.
    private void BackgroundWorker_ProgressChanged(object? sender, ProgressChangedEventArgs e) {
        ProgressMessage? msg = e.UserState as ProgressMessage;
        if (msg == null) {
            if (e.UserState is string str) {
                ProgressMsg = str;
            }
        } else {
            LogText += msg.Text;
        }
    }

    // Fires on the UI thread.
    private void BackgroundWorker_RunWorkerCompleted(object? sender,
            RunWorkerCompletedEventArgs e) {
        if (e.Cancelled) {
            ProgressMsg = "Halted (user cancellation)";
        } else if (e.Error != null) {
            ProgressMsg = "Failed";
            AppLog.E("Bulk compression test failed", e.Error);
            LogText += "\r\n" + e.Error;
        } else {
            ProgressMsg = "Test complete";
        }

        RunButtonLabel = STR_RUN_TEST;
        CanChooseFile = true;
        bool hasPath = !string.IsNullOrEmpty(mPathName);
        CanStartRunning = hasPath;
        IsRunCancelEnabled = hasPath;
    }

    // -----------------------------------------------------------------------------------------
    // Constructor

    public BulkCompressViewModel(AppHook appHook, IFilePickerService filePickerService) {
        mAppHook = appHook;

        mWorker = new BackgroundWorker();
        mWorker.WorkerReportsProgress = true;
        mWorker.WorkerSupportsCancellation = true;
        mWorker.DoWork += BackgroundWorker_DoWork;
        mWorker.ProgressChanged += BackgroundWorker_ProgressChanged;
        mWorker.RunWorkerCompleted += BackgroundWorker_RunWorkerCompleted;

        CanStartRunning = false;
        RunButtonLabel = STR_RUN_TEST;
        ProgressMsg = "Ready";

        ChooseFileCommand = new AsyncRelayCommand(async () => {
            string? path = await filePickerService.OpenFileAsync("Select File");
            if (!string.IsNullOrEmpty(path)) {
                PathName = path;
            }
        });

        RunCancelCommand = new RelayCommand(() => {
            if (mWorker.IsBusy) {
                mWorker.CancelAsync();
            } else {
                ResetLog();
                CanChooseFile = false;
                CanStartRunning = false;
                // IsRunCancelEnabled stays true so Cancel remains clickable.
                RunButtonLabel = STR_CANCEL_TEST;
                mWorker.RunWorkerAsync(mPathName);
            }
        });

        CloseCommand = new AsyncRelayCommand(
            async () => CloseRequested?.Invoke(false));
    }
}
