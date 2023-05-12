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
using System.Diagnostics;

namespace CommonUtil {
    /// <summary>
    /// <para>This provides a way for the application to pass configuration options into the
    /// library, and for the library to pass debug messages to the application.  This is not
    /// intended to hold application settings or significant state.  It's mostly a way for
    /// test/debug code to pass values "out of band".</para>
    /// <para>For the most part this just gets passed around and ignored.</para>
    /// </summary>
    public class AppHook {
        /// <summary>
        /// Debug message log.
        /// </summary>
        public MessageLog Log { get; private set; }

        private Dictionary<string, string> mOptions = new Dictionary<string, string>();


        /// <summary>
        /// Constructor.  Pass in a reference to the application's debug log object.
        /// </summary>
        /// <param name="log">Debug log output.</param>
        public AppHook(MessageLog log) {
            Log = log;
        }

        public string GetOption(string key, string defaultValue) {
            if (!mOptions.TryGetValue(key, out string? valueStr)) {
                return defaultValue;
            }
            return valueStr;
        }

        public void SetOption(string key, string value) {
            if (value == null) {
                throw new ArgumentNullException("value");
            }
            mOptions[key] = value;
        }

        public bool GetOptionBool(string key, bool defaultValue) {
            if (!mOptions.TryGetValue(key, out string? valueStr)) {
                return defaultValue;
            }
            if (!bool.TryParse(valueStr, out bool value)) {
                Debug.WriteLine("Warning: bool parse failed on " + key + "=" + valueStr);
                return defaultValue;
            }
            return value;
        }

        public void SetOptionBool(string key, bool value) {
            string newVal = value.ToString();
            //if (!mOptions.TryGetValue(key, out string? oldValue) || oldValue != newVal) {
            mOptions[key] = newVal;
            //}
        }

        public int GetOptionInt(string key, int defaultValue) {
            if (!mOptions.TryGetValue(key, out string? valueStr)) {
                return defaultValue;
            }
            if (!int.TryParse(valueStr, out int value)) {
                Debug.WriteLine("Warning: int parse failed on " + key + "=" + valueStr);
                return defaultValue;
            }
            return value;
        }

        public void SetOptionInt(string key, int value) {
            string newVal = value.ToString();
            //if (!mOptions.TryGetValue(key, out string? oldValue) || oldValue != newVal) {
            mOptions[key] = newVal;
            //}
        }


        public void LogD(string msg) {
            Debug.WriteLine("MsgD: " + msg);
            Log.LogD(msg);
        }
        public void LogI(string msg) {
            Debug.WriteLine("MsgI: " + msg);
            Log.LogI(msg);
        }
        public void LogW(string msg) {
            Debug.WriteLine("MsgW: " + msg);
            Log.LogW(msg);
        }
        public void LogE(string msg) {
            Debug.WriteLine("MsgE: " + msg);
            Log.LogE(msg);
        }
    }
}
