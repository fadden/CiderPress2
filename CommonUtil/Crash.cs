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
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CommonUtil {
    public static class Crash {
        /// <summary>
        /// Application tag.  Short string set by application, included in the crash log filename.
        /// </summary>
        public static string AppTag { get; set; } = "APP";

        /// <summary>
        /// Application identifier.  This is an arbitrary string set by the application.  It
        /// will be included in the crash dump.
        /// </summary>
        public static string AppIdent { get; set; } = "(app ident unset)";

        /// <summary>
        /// Writes an unhandled exception trace to a crash file.
        /// </summary>
        /// <remarks>
        /// Usage:
        /// <code>
        ///   AppDomain.CurrentDomain.UnhandledException +=
        ///       new UnhandledExceptionEventHandler(CommonUtil.Crash.CrashReporter);
        /// </code>
        /// Thanks: https://stackoverflow.com/a/21308327/294248
        /// </remarks>
        public static void CrashReporter(object sender, UnhandledExceptionEventArgs e) {
            string crashFileName = "CrashLog-" + AppTag + ".txt";

            Exception? ex = (Exception)e.ExceptionObject;
            Debug.WriteLine("CRASHING (term=" + e.IsTerminating + "): " + ex);

            // Move to directory where executable lives.  Maybe not ideal, but it's better
            // than strewing crash reports in random locations.
            string? exeName = typeof(Crash).Assembly.Location;
            string? baseDir = Path.GetDirectoryName(exeName);
            string crashPath = crashFileName;
            if (!string.IsNullOrEmpty(baseDir)) {
                crashPath = Path.Combine(baseDir, crashFileName);
            }
            Debug.WriteLine("Writing crash log to " + crashPath);

            try {
                // Open the file in "append" mode.  Ideally we'd erase the file if it's older
                // than a certain limit.
                using (StreamWriter writer = new StreamWriter(crashPath, true)) {
                    writer.WriteLine("*** CRASH at " + DateTime.Now.ToLocalTime() + " ***");
                    writer.WriteLine("  App: " + AppIdent);
                    writer.WriteLine("  OS: " + RuntimeInformation.OSDescription);
                    writer.WriteLine("  Runtime: " + RuntimeInformation.FrameworkDescription +
                        " / " + RuntimeInformation.RuntimeIdentifier);
                    writer.WriteLine();
                    while (ex != null) {
                        writer.WriteLine(ex.GetType().FullName + ": " + ex.Message);
                        writer.WriteLine("Trace:");
                        writer.WriteLine(ex.StackTrace);
                        writer.WriteLine();

                        ex = ex.InnerException;
                    }

                    writer.WriteLine("***");
                    writer.WriteLine("");
                }
            } catch {
                // damn it
                Debug.WriteLine("Crashed while crashing");
            }
        }
    }
}
