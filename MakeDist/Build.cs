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
using System.IO.Compression;

using CommonUtil;

namespace MakeDist {
    internal static class Build {
        private static string[] sTargets = new string[] {
            "cp2",
            "Examples/AddFile",
            "Examples/ListContents",
        };

        private static string[] sDistFiles = new string[] {
            "README.md",
            "LegalStuff.txt",
            "cp2/Manual-cp2.md"
        };

        private const string DIST_DIR = "DIST";

        /// <summary>
        /// Executes the various build commands.
        /// </summary>
        /// <param name="isDebug">If set, perform a debug build.</param>
        /// <returns>True on success.</returns>
        public static bool ExecBuild(string versionTag, List<string> rids, bool isDebug) {
            if (!File.Exists("CiderPress2.sln")) {
                Console.WriteLine("This can only be run from the root of the source tree.");
                return false;
            }
            if (Directory.Exists(DIST_DIR)) {
                Console.Write("The " + DIST_DIR + " directory exists; remove it (y/N)? ");
                string? response = Console.ReadLine();
                if (!string.IsNullOrEmpty(response) && char.ToLower(response[0]) == 'y') {
                    // recursively remove directory
                    Directory.Delete(DIST_DIR, true);
                } else {
                    // Can't continue.
                    Console.WriteLine("Cancelled");
                    return false;
                }
            }

            DateTime startWhen = DateTime.Now;

            string distPath = DIST_DIR;
            Directory.CreateDirectory(distPath);

            foreach (string rid in rids) {
                Console.WriteLine();
                Console.WriteLine("### Building for " + rid + "...");
                if (!BuildRID(rid, isDebug, false, distPath)) {
                    return false;
                }
                if (!BuildRID(rid, isDebug, true, distPath)) {
                    return false;
                }
            }

            Console.WriteLine("### Build completed, elapsed time " +
                (DateTime.Now - startWhen).TotalSeconds.ToString("N1") + " sec");

            Console.WriteLine();
            startWhen = DateTime.Now;
            if (!MakePackages(versionTag, distPath)) {
                return false;
            }
            Console.WriteLine("### Packaging completed, elapsed time " +
                (DateTime.Now - startWhen).TotalSeconds.ToString("N1") + " sec");
            return true;
        }

        private static bool BuildRID(string rid, bool isDebug, bool isContained, string distPath) {
            string scStr = isContained ? "_sc" : "_fd";
            string debugStr = isDebug ? "_debug" : "";
            string outputDir = Path.Combine(distPath, rid + scStr + debugStr);
            Directory.CreateDirectory(outputDir);

            foreach (string target in sTargets) {
                if (!BuildTarget(rid, isDebug, isContained, outputDir, target)) {
                    return false;
                }
            }
            // Throw in the WPF app for Windows builds, but not for the self-contained version.
            if (rid.StartsWith("win") && !isContained) {
                if (!BuildTarget(rid, isDebug, isContained, outputDir, "cp2_wpf")) {
                    return false;
                }
            }
            return true;
        }

        private static bool BuildTarget(string rid, bool isDebug, bool isContained,
                string outputDir, string target) {
            string cmdStr = "dotnet build " + target + " --runtime " + rid +
                (isContained ? " --self-contained" : " --no-self-contained") +
                " --configuration " + (isDebug ? "Debug" : "Release") +
                (isDebug ? "" : " /p:DebugType=None") +     // suppress PDB generation
                " --output " + outputDir;
            Console.WriteLine("--- Invoking: " + cmdStr);
            ShellCommand cmd = new ShellCommand("dotnet", cmdStr, string.Empty,
                new Dictionary<string, string?>());
            cmd.TimeoutMs = 45 * 1000;
            cmd.Execute();
            Console.WriteLine("Exit code: " + cmd.ExitCode);
            Console.WriteLine("stdout:");
            Console.Write(cmd.Stdout.ToString());
            Console.WriteLine("stderr:");
            Console.Write(cmd.Stderr.ToString());

            return (cmd.ExitCode == 0);
        }

        /// <summary>
        /// Takes the contents of the various directories, combines them with documentation and
        /// support files, and packages them into a ZIP archive.  The current directory should
        /// be the root of the source tree.
        /// </summary>
        /// <param name="distPath">Path to DIST directory.</param>
        /// <returns>True on success.</returns>
        private static bool MakePackages(string versionTag, string distPath) {
            string rootPath = Environment.CurrentDirectory;
            try {
                Environment.CurrentDirectory = distPath;
                foreach (string dir in Directory.EnumerateDirectories(".")) {
                    if (!Package(versionTag, rootPath, dir)) {
                        return false;
                    }
                }
            } finally {
                Environment.CurrentDirectory = rootPath;
            }
            return true;
        }

        private static bool Package(string versionTag, string rootPath, string dir) {
            Console.WriteLine("Packaging " + Path.GetFileName(dir) + "...");

            // Copy the support files into each distribution.
            foreach (string fileName in sDistFiles) {
                string srcPath = Path.Combine(rootPath, fileName);
                string dstPath = Path.Combine(dir, Path.GetFileName(fileName));
                FileUtil.CopyFile(srcPath, dstPath);
            }

            // Create the ZIP archive.
            //
            // When packing four sets of RIDs, release build:
            //   CompressionLevel.Optimal      : 15.8 sec, output 128592 KB
            //   CompressionLevel.SmallestSize : 26.4 sec, output 128020 KB
            // Improvement of 0.5%.
            ZipFile.CreateFromDirectory(dir,
                "cp2_" + versionTag + "_" + Path.GetFileName(dir) + ".zip",
                CompressionLevel.Optimal, false);
            return true;
        }
    }
}
