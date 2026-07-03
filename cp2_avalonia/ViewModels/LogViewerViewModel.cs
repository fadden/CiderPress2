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
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using CommonUtil;
using cp2_avalonia.Models;
using cp2_avalonia.Services;

namespace cp2_avalonia.ViewModels;

/// <summary>
/// ViewModel for the debug Log Viewer tool window (always modeless).
/// </summary>
public class LogViewerViewModel : ObservableObject
{
    private readonly DebugMessageLog mLog;
    private readonly ISettingsService mSettings;

    // Master list of every entry received, unfiltered.  The observable LogEntries
    // collection is the display-filtered subset.
    private readonly List<LogEntry> mAllEntries = new List<LogEntry>();

    // ── Modeless lifecycle ────────────────────────────────────────────────────
    public event Action? Closed;


    // ── Interactions exposed to code-behind ───────────────────────────────────
    /// <summary>Fires when Save is requested; code-behind handles file save.</summary>
    public event Action? SaveRequested;
    /// <summary>Fires when Copy is requested; code-behind handles clipboard.</summary>
    public event Action? CopyRequested;
    /// <summary>
    /// Fires when the list should scroll to the bottom so the latest items stay on screen.
    /// The bool argument is <c>true</c> to force the scroll (e.g. the filter changed and the
    /// list was rebuilt) or <c>false</c> to only scroll when the view is already at the
    /// bottom (e.g. a new entry arrived while the user is reading history).
    /// </summary>
    public event Action<bool>? ScrollToEndRequested;

    public IRelayCommand SaveCommand { get; }
    public IRelayCommand CopyCommand { get; }
    public IRelayCommand CloseCommand { get; }

    // ── Observable (display-filtered) log entries ─────────────────────────────
    public ObservableCollection<LogEntry> LogEntries { get; } =
        new ObservableCollection<LogEntry>();

    /// <summary>
    /// Labels for the minimum-priority combo box, in ascending severity.  The index
    /// maps directly onto <see cref="MessageLog.Priority"/> (Verbose=0 .. Error=4).
    /// </summary>
    public IReadOnlyList<string> PriorityLabels { get; } =
        new[] { "Verbose", "Debug", "Info", "Warning", "Error" };

    private int mMinPriorityIndex;
    /// <summary>
    /// Minimum priority to display/copy/save.  Selecting a value shows that level and
    /// anything of higher severity.  Persisted across runs.
    /// </summary>
    public int MinPriorityIndex
    {
        get => mMinPriorityIndex;
        set
        {
            if (value < 0 || value >= PriorityLabels.Count) {
                value = 0;
            }
            if (SetProperty(ref mMinPriorityIndex, value)) {
                mSettings.SetInt(AppSettings.DEBUG_LOG_MIN_PRIORITY, value);
                RebuildFilteredList();
            }
        }
    }

    private MessageLog.Priority MinPriority => (MessageLog.Priority)mMinPriorityIndex;

    public LogViewerViewModel(DebugMessageLog log, ISettingsService settings)
    {
        mLog = log;
        mSettings = settings;

        // Restore the saved minimum priority (default Verbose = show everything).
        mMinPriorityIndex = settings.GetInt(AppSettings.DEBUG_LOG_MIN_PRIORITY,
            (int)MessageLog.Priority.Verbose);
        if (mMinPriorityIndex < 0 || mMinPriorityIndex >= PriorityLabels.Count) {
            mMinPriorityIndex = 0;
        }

        // Populate from existing log entries.
        List<DebugMessageLog.LogEntry> existing = mLog.GetLogs();
        foreach (DebugMessageLog.LogEntry entry in existing) {
            mAllEntries.Add(new LogEntry(entry));
        }
        RebuildFilteredList();

        // Subscribe to new log events.
        mLog.RaiseLogEvent += HandleLogEvent;

        SaveCommand = new RelayCommand(() => SaveRequested?.Invoke());
        CopyCommand = new RelayCommand(() => CopyRequested?.Invoke());
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke());
    }

    /// <summary>
    /// Rebuilds the visible <see cref="LogEntries"/> collection from the master list,
    /// applying the current minimum-priority filter.
    /// </summary>
    private void RebuildFilteredList()
    {
        LogEntries.Clear();
        MessageLog.Priority min = MinPriority;
        foreach (LogEntry entry in mAllEntries) {
            if (entry.Level >= min) {
                LogEntries.Add(entry);
            }
        }
        // Filter changed and the list was rebuilt: always show the latest entries.
        ScrollToEndRequested?.Invoke(true);
    }

    /// <summary>
    /// Builds the plain-text representation of the currently-displayed (filtered) log
    /// entries.  Used by Save and Copy, so they honor the priority filter.
    /// </summary>
    public string BuildLogText()
    {
        var sb = new StringBuilder(LogEntries.Count * 80);
        foreach (LogEntry entry in LogEntries) {
            sb.Append(entry.When.ToString(@"hh\:mm\:ss\.fff"));
            sb.Append(' ');
            sb.Append(entry.Priority);
            sb.Append(' ');
            sb.AppendLine(entry.Message);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Called by code-behind when the window is closed, to unsubscribe from the log.
    /// </summary>
    public event Action? CloseRequested;
    public void RequestClose() => CloseRequested?.Invoke();

    public void OnWindowClosed()
    {
        mLog.RaiseLogEvent -= HandleLogEvent;
        Closed?.Invoke();
    }

    // NOTE: may be called on a background thread.
    private void HandleLogEvent(object? sender, DebugMessageLog.LogEventArgs e)
    {
        // ObservableCollection requires UI thread; Avalonia's dispatcher handles marshaling
        // from code-behind's ScrollChanged handler.  Just append from whatever thread we're on.
        LogEntry entry = new LogEntry(e.Entry);
        mAllEntries.Add(entry);
        if (entry.Level >= MinPriority) {
            LogEntries.Add(entry);
            // New entry: only follow if the user is already at the bottom.
            ScrollToEndRequested?.Invoke(false);
        }
    }
}
