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
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Avalonia.Media;
using CommonUtil;
using DiskArc;

using cp2_avalonia.Services;

namespace cp2_avalonia.LibTest {
    /// <summary>
    /// <para>Finds and executes all tests.</para>
    ///
    /// <para>This should be instantiated and run exclusively on a background worker thread.</para>
    /// </summary>
    public class TestRunner {
        private static readonly bool SHOW_TIMES = true;
        private const string TEST_DATA_FILENAME = "TestData";

        public class TestResult(string name, Exception? exc)
        {
            public string Name { get; private set; } = name;
            private bool Success { get; set; } = (exc == null);
            public Exception? Exc { get; private set; } = exc;

            // Return a string for use in the UI combo box.
            public override string ToString() {
                return (Success ? "OK" : "FAIL") + " - " + Name;
            }
        }

        /// <summary>
        /// If true, don't scrub directories.
        /// </summary>
        public bool RetainOutput { get; set; }

        /// <summary>
        /// Place where test data files live.
        /// </summary>
        private string mTestDataDir = string.Empty;

        /// <summary>
        /// Reference to background worker object, for progress messages and cancellation checks.
        /// </summary>
        private BackgroundWorker? mWorker;

        /// <summary>
        /// Test results, one entry per test executed.
        /// </summary>
        private readonly List<TestResult> mResults = new List<TestResult>();


        /// <summary>
        /// </summary>
        /// <param name="worker">Background worker object from dialog box.</param>
        /// <param name="testLibName">Test library name</param>
        /// <param name="testIfaceName">Test interface name</param>
        /// <returns>
        /// The list of test results (empty if all tests passed), or <c>null</c> if the
        /// test run could not be started (e.g. the test data or library could not be found).
        /// </returns>
        public List<TestResult>? Run(BackgroundWorker worker, string testLibName,
                string testIfaceName) {
            Debug.Assert(mWorker == null);  // don't re-use this object
            mWorker = worker;
            Debug.WriteLine("Test runner starting, retain=" + RetainOutput);

            // Find directory with test library and data.
            string? testRoot = GetTestRoot();
            if (testRoot == null) {
                AppLog.W("Test runner: could not locate TestData directory");
                PrintErrMsg("Can't find TestData directory");
                PrintFailure();
                return null;
            }
            AppLog.D("Test runner: test root = '" + testRoot + "'");
            mTestDataDir = Path.Combine(testRoot, TEST_DATA_FILENAME);
            AppLog.D("Test runner: test data dir = '" + mTestDataDir + "'");
            if (!Directory.Exists(mTestDataDir)) {
                AppLog.W("Test runner: test data directory not found: '" + mTestDataDir + "'");
                PrintErrMsg("Test data directory not found: " + mTestDataDir);
                PrintFailure();
                return null;
            }

            string? libDir = GetDLLLocation();
            if (libDir == null) {
                AppLog.W("Test runner: could not locate library directory");
                PrintErrMsg("Can't find lib directory");
                PrintFailure();
                return null;
            }
            AppLog.D("Test runner: library dir = '" + libDir + "'");
            string libPath = Path.Combine(libDir, testLibName);
            AppLog.D("Test runner: library path = '" + libPath + "'");
            if (!File.Exists(libPath)) {
                AppLog.W("Test runner: library DLL does not exist: '" + libPath + "'");
                PrintErrMsg("DLL '" + libPath + "' does not exist");
                PrintFailure();
                return null;
            }
            PrintProgress("Loading library tests from " + libPath + "\r\n");

            // Use a new AppHook so we can set test-specific options without disrupting the
            // main application.  We may want to hook up the app debug log viewer.
            AppHook appHook = new AppHook(new SimpleMessageLog());
            appHook.SetOptionBool(DAAppHook.WARN_MARKED_BUT_UNUSED, true);
            appHook.SetOption(DAAppHook.LIB_TEST_ROOT, testRoot);

            // Find the classes with types.
            Assembly dll = Assembly.LoadFile(libPath);
            Type? itestType = dll.GetType(testIfaceName);
            if (itestType == null) {
                PrintErrMsg("Unable to find ITest interface in test library");
                PrintFailure();
                return null;
            }
            IEnumerable<Type>? typesFound = Mirror.FindImplementingTypes(itestType);
            if (typesFound == null) {
                PrintErrMsg("Unable to find test classes");
                PrintFailure();
                return null;
            }

            DateTime startWhen = DateTime.Now;
            int successCount = 0;
            int failureCount = 0;

            foreach (Type type in typesFound) {
                PrintProgress("Test set: " + type.Name + " ...\r\n");
                MethodInfo[] methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static);
                foreach (MethodInfo meth in methods) {
                    if (worker.CancellationPending) {
                        PrintProgress("\r\n** Cancelled **" + "\r\n");
                        return mResults;
                    }
                    if (!meth.Name.StartsWith("Test")) {
                        Debug.WriteLine("Test harness: skipping " + type.Name + "." + meth.Name);
                        continue;
                    }
                    ParameterInfo[] parms = meth.GetParameters();
                    if (parms.Length != 1 || parms[0].ParameterType != typeof(AppHook)) {
                        PrintProgress("Skipping test with wrong args: " + meth.Name + "\r\n",
                            Colors.Blue);
                        continue;
                    }
                    PrintProgress("  " + meth.Name + " ...");
                    try {
                        DateTime testStartWhen = DateTime.Now;
                        meth.Invoke(null, [appHook]);
                        // Test completed successfully.
                        int totalMs = (int)(DateTime.Now - testStartWhen).TotalMilliseconds;
                        if (SHOW_TIMES) {
                            PrintProgress(" (" + totalMs + " ms)");
                        }
                        PrintSuccess();
                        successCount++;
                    } catch (TargetInvocationException ex) {
                        // Test case threw an exception to indicate failure.
                        PrintFailure();
                        failureCount++;
                        Exception? inner = ex.InnerException;
                        mResults.Add(new TestResult(type.Name + "." + meth.Name, inner));
                        string testName = type.Name + "." + meth.Name;
                        if (inner != null) {
                            AppLog.W("Library test failed: " + testName, inner);
                        } else {
                            AppLog.W("Library test failed: " + testName);
                        }
                    } catch (Exception ex) {
                        // Something bad happened.
                        PrintFailure();
                        failureCount++;
                        mResults.Add(new TestResult("Unexpected test harness failure", ex));
                        AppLog.E("Unexpected test harness failure in " +
                            type.Name + "." + meth.Name, ex);
                    }
                }
            }

            DateTime endWhen = DateTime.Now;

            int numTests = successCount + failureCount;
            if (failureCount == 0) {
                PrintProgress(string.Format("All " + numTests +
                    " tests passed in {0:N3} sec\r\n",
                    (endWhen - startWhen).TotalSeconds), Colors.Green);
            } else {
                PrintProgress(successCount + " of " + numTests + " tests passed\r\n",
                    Colors.OrangeRed);
            }

            // Prod the GC so we can see if we're leaking.
            GC.Collect();

            return mResults;
        }

        private void PrintProgress(string msg) {
            mWorker?.ReportProgress(0, new ProgressMessage(msg));
        }

        private void PrintProgress(string msg, Color color) {
            mWorker?.ReportProgress(0, new ProgressMessage(msg, color));
        }

        private void PrintErrMsg(string msg) {
            PrintProgress(" [" + msg + "] ", Colors.Blue);
        }

        private void PrintSuccess() {
            PrintProgress(" success\r\n", Colors.Green);
        }

        private void PrintFailure() {
            PrintProgress(" failed\r\n", Colors.Red);
        }

        /// <summary>
        /// Reports a test failure message from a test case.
        /// </summary>
        public void ReportTestFailure(string msg) {
            Debug.WriteLine("Test failure: " + msg);
            PrintProgress(" " + msg + "\r\n", Colors.Blue);
        }

        private static string? GetTestRoot() {
            string exeName = typeof(TestRunner).Assembly.Location;
            AppLog.D("Test runner: assembly location = '" + exeName + "'");
            string? baseDir = Path.GetDirectoryName(exeName);
            if (string.IsNullOrEmpty(baseDir)) {
                throw new IOException("Unable to find dir for: '" + exeName + "'");
            }

            var tryPath = Path.Combine(baseDir, TEST_DATA_FILENAME);
            AppLog.D("Test runner: looking for TestData at '" + tryPath + "'");
            if (Directory.Exists(tryPath)) {
                return baseDir;
            }

            // Walk up the directory tree looking for TestData. The output path depth
            // varies (e.g. bin/Debug/net10.0/osx-x64 vs bin/Debug/net8.0), so search
            // rather than using a fixed level count.
            string? upDev = baseDir;
            for (int i = 0; i < 8; i++) {
                upDev = Path.GetDirectoryName(upDev);
                if (upDev == null) {
                    AppLog.W("Test runner: could not walk up from '" + baseDir + "' to find TestData");
                    return null;
                }
                tryPath = Path.Combine(upDev, TEST_DATA_FILENAME);
                AppLog.D("Test runner: looking for TestData at '" + tryPath + "'");
                if (Directory.Exists(tryPath)) {
                    return upDev;
                }
            }

            AppLog.W("Test runner: unable to find TestData directory near '" + exeName + "'");
            return null;
        }

        private static string? GetDLLLocation() {
            string exeName = typeof(TestRunner).Assembly.Location;
            return Path.GetDirectoryName(exeName);
        }
    }
}
