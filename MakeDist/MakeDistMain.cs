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

using AppCommon;
using CommonUtil;

namespace MakeDist {
    /// <summary>
    /// Command-line entry point.
    /// </summary>
    static class MakeDistMain {
        private const string APP_NAME = "MakeDist";

        /// <summary>
        /// Runtime IDs we want to build for.
        /// </summary>
        private static List<string> sStdRIDs = new List<string>() {
            "win-x86",          // 32-bit, non-version-specific Windows
            "win-x64",          // 64-bit, non-version-specific Windows
            "linux-x64",        // 64-bit, most Linux desktop distributions
            "osx-x64",          // 64-bit, non-version-specific Mac OS (min version 10.12 Sierra)
        };

        /// <summary>
        /// OS entry point.
        /// </summary>
        internal static void Main(string[] args) {
            Environment.ExitCode = 2;       // use code 2 for usage problems

            if (args.Length == 0) {
                Usage();
                return;
            }
            string cmd = args[0];

            if (cmd == "build") {
                bool isDebugBuild = false;
                if (args.Length < 1 || args.Length > 2) {
                    Usage();
                    return;
                }
                string versionTag = GlobalAppVersion.AppVersion.GetBuildTag();

                if (args.Length == 2) {
                    if (args[1] == "--debug") {
                        isDebugBuild = true;
                    } else if (args[1] == "--release") {
                        isDebugBuild = false;
                    } else {
                        Usage();
                        return;
                    }
                }

                // TODO: take RIDs as command-line argument list, with "std" doing default set

                bool result = Build.ExecBuild(versionTag, sStdRIDs, isDebugBuild);
                Environment.ExitCode = result ? 0 : 1;
                if (!result) {
                    Console.Error.WriteLine("FAILED");
                }
            } else if (cmd == "clobber") {
                Clobber();
                Environment.ExitCode = 0;
            } else {
                Usage();
            }
        }

        /// <summary>
        /// Prints general usage summary.
        /// </summary>
        private static void Usage() {
            Console.WriteLine("Usage: " + APP_NAME + " build version-tag [--debug|--release]");
            Console.WriteLine("       " + APP_NAME + " clobber");
        }

        #region Clobber

        private const string PROJ_EXT = ".csproj";
        private const string OBJ_DIR = "obj";
        private const string BIN_DIR = "bin";

        /// <summary>
        /// Performs the "clobber" operation.
        /// </summary>
        private static void Clobber() {
            string curDir = Environment.CurrentDirectory;
            Console.WriteLine("Clobbering in '" + curDir + "'...");
            List<string> scrubPaths = new List<string>();

            ScanClobberables(curDir, scrubPaths);

            Console.WriteLine("Paths to scrub:");
            foreach (string path in scrubPaths) {
                Console.WriteLine("  " + path);
            }
            Console.Write("Proceed (y/N)? ");
            string? response = Console.ReadLine();
            if (!string.IsNullOrEmpty(response) && char.ToLower(response[0]) == 'y') {
                Console.WriteLine("Scrubbing...");
                DeletePaths(scrubPaths);
            } else {
                Console.WriteLine("Cancelled");
            }
        }

        /// <summary>
        /// Recursively scans for things to clobber.
        /// </summary>
        /// <param name="directory">Directory to scan.</param>
        /// <param name="scrubPaths">Accumulated list of scrub targets.</param>
        private static void ScanClobberables(string directory, List<string> scrubPaths) {
            foreach (string path in Directory.EnumerateFileSystemEntries(directory)) {
                // Descend into subdirectories (but not "bin" or "obj").
                string fileName = Path.GetFileName(path);
                if (fileName != OBJ_DIR && fileName != BIN_DIR && Directory.Exists(path)) {
                    ScanClobberables(path, scrubPaths);
                }

                if (path.ToLowerInvariant().EndsWith(PROJ_EXT)) {
                    // Found a project file, scrub "obj" and "bin" here.
                    string objPath = Path.Combine(directory, OBJ_DIR);
                    if (Directory.Exists(objPath)) {
                        scrubPaths.Add(objPath);
                    }
                    string binPath = Path.Combine(directory, BIN_DIR);
                    // Attempting to delete MakeDist/bin will likely fail because it's running.
                    if (Directory.Exists(binPath) && Path.GetFileName(directory) != APP_NAME) {
                        scrubPaths.Add(binPath);
                    }
                }
            }
        }

        /// <summary>
        /// Recursively deletes a list of directories.
        /// </summary>
        /// <param name="paths">List of paths to remove.</param>
        private static void DeletePaths(List<string> paths) {
            foreach (string path in paths) {
                FileUtil.DeleteDirContents(path);
                Directory.Delete(path);
            }
        }

        #endregion Clobber
    }
}
