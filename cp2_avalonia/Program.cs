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
using System.Threading.Tasks;
using Avalonia;

using cp2_avalonia.Services;

namespace cp2_avalonia {
    internal class Program {
        // Avalonia configuration; don't remove; also used by visual designer.
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace();

        [STAThread]
        public static void Main(string[] args) {
            InstallGlobalExceptionHandlers();
            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
        }

        /// <summary>
        /// Routes otherwise-unhandled exceptions to the application log so they appear
        /// in the debug log dialog (DEBUG &gt; Show Debug Log) instead of only going to
        /// stderr / the debugger.
        /// </summary>
        private static void InstallGlobalExceptionHandlers() {
            AppDomain.CurrentDomain.UnhandledException += (sender, e) => {
                if (e.ExceptionObject is Exception ex) {
                    AppLog.E("Unhandled exception" + (e.IsTerminating ? " (terminating)" : ""), ex);
                } else {
                    AppLog.E("Unhandled non-exception error: " + e.ExceptionObject);
                }
            };
            TaskScheduler.UnobservedTaskException += (sender, e) => {
                AppLog.E("Unobserved task exception", e.Exception);
                e.SetObserved();
            };
        }
    }
}
