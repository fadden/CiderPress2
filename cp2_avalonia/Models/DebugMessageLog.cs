/*
 * Copyright 2023 faddenSoft
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
using System.Diagnostics;
using Avalonia.Threading;

using CommonUtil;

namespace cp2_avalonia.Models {
    /// <summary>
    /// Debug message log with UI-thread notification.  Replaces cp2_wpf/DebugMessageLog.cs.
    /// </summary>
    public sealed class DebugMessageLog : MessageLog {
        public class LogEventArgs(LogEntry entry) : EventArgs
        {
            public LogEntry Entry { get; private set; } = entry;
        }

        /// <summary>
        /// Event raised when a log entry is added.
        /// </summary>
        public event EventHandler<LogEventArgs>? RaiseLogEvent;

        /// <summary>
        /// One entry in the log.
        /// </summary>
        public class LogEntry(int index, DateTime when, Priority prio, string msg)
        {
            public int Index { get; private set; } = index;
            public DateTime When { get; private set; } = when;
            public Priority Priority { get; private set; } = prio;
            public string Message { get; private set; } = msg;
        }

        private readonly List<LogEntry> mEntries = new List<LogEntry>();
        private int mTopEntry;
        private int mLastIndex;
        private const int MAX_LINES = 100000;

        // MessageLog abstract override
        public override void Clear() {
            Debug.WriteLine("CLEAR message log");
            lock (mEntries) {
                mEntries.Clear();
            }
        }

        // MessageLog abstract override
        public override void Log(Priority prio, string message) {
            if (prio < mMinPriority) {
                return;
            }
            LogEntry ent;
            lock (mEntries) {
                ent = new LogEntry(++mLastIndex, DateTime.Now, prio, message);
                if (mEntries.Count < MAX_LINES) {
                    mEntries.Add(ent);
                } else {
                    mEntries[mTopEntry++] = ent;
                    if (mTopEntry == MAX_LINES) {
                        mTopEntry = 0;
                    }
                }
            }

            OnRaiseLogEvent(new LogEventArgs(ent));
        }

        private void OnRaiseLogEvent(LogEventArgs e) {
            EventHandler<LogEventArgs>? raiseEvent = RaiseLogEvent;
            if (raiseEvent != null) {
                if (Dispatcher.UIThread.CheckAccess()) {
                    raiseEvent(this, e);
                } else {
                    // Use Post (fire-and-forget) to avoid deadlocks when the UI thread
                    // is blocked waiting on a worker via Monitor.Wait.
                    Dispatcher.UIThread.Post(() => raiseEvent(this, e));
                }
            }
        }

        /// <summary>
        /// Returns all current entries in order (oldest first).
        /// </summary>
        public List<LogEntry> GetLogs() {
            lock (mEntries) {
                List<LogEntry> list = new List<LogEntry>(mEntries.Count);
                for (int i = mTopEntry; i < mEntries.Count; i++) {
                    list.Add(mEntries[i]);
                }
                for (int i = 0; i < mTopEntry; i++) {
                    list.Add(mEntries[i]);
                }
                return list;
            }
        }

        public int GetEntryCount() {
            lock (mEntries) {
                return mEntries.Count;
            }
        }
    }
}
