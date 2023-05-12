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
using System.Diagnostics;
using System.Threading;
using System.Windows;

using CommonUtil;

namespace cp2_wpf {
    /// <summary>
    /// Class for managing the debug log.  Logs may be generated from any thread, so this class
    /// needs to be thread-safe.
    /// </summary>
    public class DebugMessageLog : MessageLog {
        public class LogEventArgs : EventArgs {
            public LogEntry Entry { get; private set; }

            public LogEventArgs(LogEntry entry) {
                Entry = entry;
            }
        }

        /// <summary>
        /// Subscribe to this event to be notified when something is logged.
        /// </summary>
        public event EventHandler<LogEventArgs>? RaiseLogEvent;

        /// <summary>
        /// Holds a single log entry.
        /// </summary>
        public class LogEntry {
            public int Index { get; private set; }
            public DateTime When { get; private set; }
            public Priority Priority { get; private set; }
            public string Message { get; private set; }

            public LogEntry(int index, DateTime when, Priority prio, string msg) {
                Index = index;
                When = when;
                Priority = prio;
                Message = msg;
            }
        }

        /// <summary>
        /// Log collection.
        /// </summary>
        private List<LogEntry> mEntries = new List<LogEntry>();
        private int mTopEntry = 0;
        private int mLastIndex;

        /// <summary>
        /// Maximum number of lines we'll hold in memory.  This is a simple measure
        /// to keep the process from expanding without bound.
        /// </summary>
        private int mMaxLines = 100000;

        // MessageLog
        public override void Clear() {
            Debug.WriteLine("CLEAR message log");
            lock (mEntries) {
                mEntries.Clear();
            }
        }

        // MessageLog
        public override void Log(Priority prio, string message) {
            if (prio < mMinPriority) {      // unsynchronized; this is fine
                return;
            }
            LogEntry ent;
            lock (mEntries) {
                // Not handling "relative time" mode.
                ent = new LogEntry(++mLastIndex, DateTime.Now, prio, message);
                if (mEntries.Count < mMaxLines) {
                    // Still growing.
                    mEntries.Add(ent);
                } else {
                    // Circular replacement.  Adding to the end then removing [0] has
                    // significant performance issues.
                    mEntries[mTopEntry++] = ent;
                    if (mTopEntry == mMaxLines) {
                        mTopEntry = 0;
                    }
                }
            }

            OnRaiseLogEvent(new LogEventArgs(ent));
        }

        /// <summary>
        /// Raises an event when a new log is received.
        /// </summary>
        protected virtual void OnRaiseLogEvent(LogEventArgs e) {
            EventHandler<LogEventArgs>? raiseEvent = RaiseLogEvent;
            if (raiseEvent != null) {
                // Use Invoke(), rather than just posting the event directly, if we're not
                // running on the GUI thread.
                // Thanks: https://stackoverflow.com/a/11625264/294248
                // and https://stackoverflow.com/a/14770717/294248
                if (Thread.CurrentThread == Application.Current.Dispatcher.Thread) {
                    // Running on UI thread, call directly.
                    //Debug.WriteLine("local");
                    raiseEvent(this, e);
                } else {
                    // Not on UI thread, dispatch it.
                    //Debug.WriteLine("remote");
                    Application.Current.Dispatcher.Invoke(new Action(() => {
                        raiseEvent(this, e);
                    }));
                }
            } else {
                //Debug.WriteLine("nobody listening for logs");
            }
        }

        /// <summary>
        /// Gets a list of all stored logs.
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
    }
}
