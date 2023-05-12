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
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

using CommonUtil;

namespace cp2_wpf.Tools {
    /// <summary>
    /// Log viewer.
    /// </summary>
    public partial class LogViewer : Window {
        /// <summary>
        /// Log entry collection.
        /// </summary>
        public ObservableCollection<LogEntry> LogEntries { get; set; }

        /// <summary>
        /// True if we're in auto-scroll mode.
        /// </summary>
        private bool mAutoScroll = true;

        /// <summary>
        /// Logger object.
        /// </summary>
        private DebugMessageLog mLog;

        public LogViewer(Window? owner, DebugMessageLog log) {
            InitializeComponent();
            Owner = owner;
            DataContext = this;

            if (owner == null) {
                // Modeless dialogs can get lost, so show them in the task bar.  There's no
                // "is modal" property in WPF, though we can fake it with a hack:
                // https://stackoverflow.com/a/1266900/294248
                ShowInTaskbar = true;
            }

            LogEntries = new ObservableCollection<LogEntry>();

            mLog = log;
            mLog.RaiseLogEvent += HandleLogEvent;

            // Pull all stored logs in.
            List<DebugMessageLog.LogEntry> logs = mLog.GetLogs();
            foreach (DebugMessageLog.LogEntry entry in logs) {
                LogEntries.Add(new LogEntry(entry));
            }
        }

        /// <summary>
        /// Handles the window close event.  Unsubscribes from the logger.
        /// </summary>
        private void Window_Closed(object sender, EventArgs e) {
            mLog.RaiseLogEvent -= HandleLogEvent;
        }

        /// <summary>
        /// Handles the arrival of a new log message.
        /// </summary>
        private void HandleLogEvent(object? sender, DebugMessageLog.LogEventArgs e) {
            DebugMessageLog.LogEntry newEntry = e.Entry;
            LogEntries.Add(new LogEntry(newEntry));
        }

        /// <summary>
        /// Handles a scroll event in the window.  We want to engage or disengage auto-scroll
        /// depending on where the scroll bar was positioned, so that we don't auto-scroll if
        /// the user is trying to look at something.
        /// </summary>
        /// <remarks>
        /// <para>Thanks: https://stackoverflow.com/a/34123691/294248</para>
        /// </remarks>
        private void ScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e) {
            ScrollViewer? sv = e.Source as ScrollViewer;
            if (sv == null) {
                Debug.WriteLine("ScrollViewer source was " + e.Source);
                return;
            }

            if (e.ExtentHeightChange == 0) {
                // Content didn't change; this is a user-scroll event.
                if (sv.VerticalOffset == sv.ScrollableHeight) {
                    // Scrolled all the way to bottom.  (Note these are doubles, not ints, but
                    // it seems to work correctly with "==".)
                    if (!mAutoScroll) {
                        //Debug.WriteLine("Engage auto-scroll");
                    }
                    mAutoScroll = true;
                } else {
                    if (mAutoScroll) {
                        //Debug.WriteLine("Disengage auto-scroll");
                    }
                    mAutoScroll = false;
                }
            } else {
                // Content changed.  Post a scroll request to move to the bottom.
                if (mAutoScroll) {
                    sv.ScrollToVerticalOffset(sv.ExtentHeight);
                    //Debug.WriteLine("Auto-scrolling");
                }
            }
        }
    }

    /// <summary>
    /// Wrapper for DebugMessageLog.LogEntry that is visible to XAML.
    /// </summary>
    public class LogEntry {
        private static readonly string[] sSingleLetter = { "V", "D", "I", "W", "E", "S" };

        public int Index { get; private set; }
        public DateTime When { get; private set; }
        public string Priority { get; private set; }
        public string Message { get; private set; }

        public LogEntry(DebugMessageLog.LogEntry entry) {
            Index = entry.Index;
            When = entry.When;
            Priority = sSingleLetter[(int)entry.Priority];
            Message = entry.Message;
        }
    }
}
