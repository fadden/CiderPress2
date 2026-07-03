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
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using AppCommon;
using cp2_avalonia.Services;

namespace cp2_avalonia.ViewModels;

/// <summary>
/// ViewModel for the cancellable WorkProgress dialog.
/// </summary>
public class WorkProgressViewModel : ObservableObject
{
    // ── Nested types (moved from WorkProgress code-behind) ────────────────────

    /// <summary>Task-specific callbacks.</summary>
    public interface IWorker
    {
        /// <summary>Does the work; runs on a background thread.</summary>
        object DoWork(BackgroundWorker worker);
        /// <summary>Called on the UI thread after successful completion.</summary>
        bool RunWorkerCompleted(object? results);
    }

    /// <summary>
    /// Message box query sent via ReportProgress from a worker thread.
    /// Uses Monitor.Wait/Pulse for cross-thread synchronisation.
    /// </summary>
    public class MessageBoxQuery(string text, string caption, MBButton button, MBIcon image)
    {
        private const MBResult RESULT_UNSET = (MBResult)(-1000);

        public string Text { get; } = text;
        public string Caption { get; } = caption;
        public MBButton Button { get; } = button;
        public MBIcon Image { get; } = image;

        private MBResult mResult = RESULT_UNSET;
        private readonly object mLockObj = new object();

        public MBResult WaitForResult()
        {
            lock (mLockObj) {
                while (mResult == RESULT_UNSET) { Monitor.Wait(mLockObj); }
                return mResult;
            }
        }

        public void SetResult(MBResult value)
        {
            lock (mLockObj) {
                mResult = value;
                Monitor.Pulse(mLockObj);
            }
        }
    }

    /// <summary>
    /// File overwrite query sent via ReportProgress from a worker thread.
    /// </summary>
    public class OverwriteQuery(CallbackFacts what)
    {
        private CallbackFacts.Results mResult = CallbackFacts.Results.Unknown;
        private bool mUseForAll;
        private readonly object mLockObj = new object();

        public CallbackFacts Facts { get; } = what;

        public CallbackFacts.Results WaitForResult(out bool useForAll)
        {
            lock (mLockObj) {
                while (mResult == CallbackFacts.Results.Unknown) { Monitor.Wait(mLockObj); }
                useForAll = mUseForAll;
                return mResult;
            }
        }

        public void SetResult(CallbackFacts.Results value, bool useForAll)
        {
            lock (mLockObj) {
                mResult = value;
                mUseForAll = useForAll;
                Monitor.Pulse(mLockObj);
            }
        }
    }

    // ── Events for sub-dialogs (wired by code-behind) ─────────────────────────
    /// <summary>Fires when the worker needs an overwrite decision from the user.</summary>
    public event Action<OverwriteQuery>? OverwriteQueryRequested;
    /// <summary>Fires when the worker needs to show an informational message box.</summary>
    public event Action<MessageBoxQuery>? MessageBoxRequested;

    // ── BackgroundWorker and callback ─────────────────────────────────────────
    private readonly IWorker mCallbacks;
    private BackgroundWorker? mWorker;
    private readonly bool mIsIndeterminate;

    public event Action<bool>? CloseRequested;

    public IRelayCommand CancelCommand { get; }

    // ── Bindable properties ───────────────────────────────────────────────────
    private string mMessageText = string.Empty;
    public string MessageText {
        get => mMessageText;
        set => SetProperty(ref mMessageText, value);
    }

    private double mProgressValue;
    public double ProgressValue {
        get => mProgressValue;
        set => SetProperty(ref mProgressValue, value);
    }

    public bool IsIndeterminate => mIsIndeterminate;

    private bool mIsCancelEnabled = true;
    public bool IsCancelEnabled {
        get => mIsCancelEnabled;
        set => SetProperty(ref mIsCancelEnabled, value);
    }

    /// <summary>Result read by caller after ShowDialogAsync returns.</summary>
    public bool DialogResult { get; private set; }

    /// <summary>True while the background worker is running.</summary>
    public bool IsBusy => mWorker?.IsBusy ?? false;

    public WorkProgressViewModel(IWorker callbacks, bool isIndeterminate)
    {
        mCallbacks = callbacks;
        mIsIndeterminate = isIndeterminate;

        CancelCommand = new RelayCommand(() => {
            mWorker?.CancelAsync();
            IsCancelEnabled = false;
        });
    }

    /// <summary>
    /// Called from code-behind's Window_Loaded handler once the window is ready.
    /// Starts the background worker.
    /// </summary>
    public void StartWorker()
    {
        mWorker = new BackgroundWorker {
            WorkerReportsProgress = true,
            WorkerSupportsCancellation = true
        };
        mWorker.DoWork += DoWork;
        mWorker.ProgressChanged += ProgressChanged;
        mWorker.RunWorkerCompleted += RunWorkerCompleted;
        mWorker.RunWorkerAsync();
    }

    /// <summary>
    /// Cancels the operation. Called from the View's OnClosing override when the
    /// user tries to close the window while work is still in progress.
    /// </summary>
    public void RequestCancel()
    {
        mWorker?.CancelAsync();
    }

    // ── BackgroundWorker handlers ─────────────────────────────────────────────

    // Executes on the work thread.  Do NOT access GUI objects here.
    private void DoWork(object? sender, DoWorkEventArgs e)
    {
        Debug.Assert(sender == mWorker);
        object results = mCallbacks.DoWork(mWorker!);
        if (mWorker!.CancellationPending) {
            e.Cancel = true;
        } else {
            e.Result = results;
        }
    }

    // Executes on the UI thread each time the worker calls ReportProgress().
    private void ProgressChanged(object? sender, ProgressChangedEventArgs e)
    {
        if (e.UserState is OverwriteQuery oq) {
            OverwriteQueryRequested?.Invoke(oq);
            // oq.SetResult is called by the code-behind event handler
            return;
        }

        if (e.UserState is MessageBoxQuery qq) {
            MessageBoxRequested?.Invoke(qq);
            // qq.SetResult is called by the code-behind event handler
            return;
        }

        if (e.UserState is string msg && !string.IsNullOrEmpty(msg)) {
            MessageText = msg;
        }
        int percent = e.ProgressPercentage;
        if (percent >= 0 && percent <= 100) {
            ProgressValue = percent;
        }
    }

    // Executes on the UI thread after the worker finishes.
    private void RunWorkerCompleted(object? sender, RunWorkerCompletedEventArgs e)
    {
        if (e.Cancelled || e.Error != null) {
            if (e.Error != null) {
                AppLog.E("Background operation failed", e.Error);
            }
            DialogResult = false;
        } else {
            DialogResult = mCallbacks.RunWorkerCompleted(e.Result);
        }
        CloseRequested?.Invoke(DialogResult);
    }
}
