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
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Media;

using AppCommon;
using CommonUtil;
using DiskArc;

namespace cp2_wpf.LibTest {
    /// <summary>
    /// <para>Finds and executes all tests.</para>
    ///
    /// <para>This should be instantiated and run exclusively on a background worker thread.</para>
    /// </summary>
    public class TestRunner {
        private static bool SHOW_TIMES = true;
        private const string TEST_DATA_FILENAME = "TestData";

        public class TestResult {
            public string Name { get; private set; }
            public bool Success { get; private set; }
            public Exception? Exc { get; private set; }

            public TestResult(string name, Exception? exc) {
                Name = name;
                Exc = exc;
                Success = (exc == null);
            }

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
        /// Base directory for temp files.
        /// </summary>
        //private string mTestTmpDir = string.Empty;

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
        private List<TestResult> mResults = new List<TestResult>();


        /// <summary>
        /// </summary>
        /// <param name="worker">Background worker object from dialog box.</param>
        public List<TestResult> Run(BackgroundWorker worker, string testLibName,
                string testIfaceName) {
            Debug.Assert(mWorker == null);  // don't re-use this object
            mWorker = worker;
            Debug.WriteLine("Test runner starting, retain=" + RetainOutput);
            PrintProgress("\r\n");  // weird: text box is ignoring first CRLF

            // Find directory with test library and data.
            string? testRoot = GetTestRoot();
            if (testRoot == null) {
                PrintErrMsg("Can't find TestData directory");
                PrintFailure();
                return mResults;
            }
            mTestDataDir = Path.Combine(testRoot, TEST_DATA_FILENAME);
            if (!Directory.Exists(mTestDataDir)) {
                PrintErrMsg("Test data directory not found: " + mTestDataDir);
                PrintFailure();
                return mResults;
            }

            string? libDir = GetDLLLocation();
            if (libDir == null) {
                PrintErrMsg("Can't find lib directory");
                PrintFailure();
                return mResults;
            }
            string libPath = Path.Combine(libDir, testLibName);
            if (!File.Exists(libPath)) {
                PrintErrMsg("DLL '" + libPath + "' does not exist");
                PrintFailure();
                return mResults;
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
                return mResults;
            }
            IEnumerable<Type>? typesFound = Mirror.FindImplementingTypes(itestType);
            if (typesFound == null) {
                PrintErrMsg("Unable to find test classes");
                PrintFailure();
                return mResults;
            }

            DateTime startWhen = DateTime.Now;
            int successCount = 0;
            int failureCount = 0;

            foreach (Type type in typesFound) {
                //DAUtil.TotalReads = DAUtil.TotalWrites = 0;
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
                    //if (type.Name != "TestHFS_FileIO" || meth.Name != "TestGrind") {
                    //    continue;
                    //}
                    ParameterInfo[] parms = meth.GetParameters();
                    if (parms.Length != 1 || parms[0].ParameterType != typeof(AppHook)) {
                        PrintProgress("Skipping test with wrong args: " + meth.Name + "\r\n",
                            Colors.Blue);
                        continue;
                    }
                    PrintProgress("  " + meth.Name + " ...");
                    try {
                        DateTime testStartWhen = DateTime.Now;
                        meth.Invoke(null, new object[] { appHook });
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
                        mResults.Add(new TestResult(type.Name + "." + meth.Name,
                            ex.InnerException));
                    } catch (Exception ex) {
                        // Something bad happened.
                        PrintFailure();
                        failureCount++;
                        mResults.Add(new TestResult("Unexpected test harness failure", ex));
                    }
                }
                //PrintProgress("  Total reads " + DAUtil.TotalReads +
                //    ", writes " + DAUtil.TotalWrites + "\r\n");
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
        /// <param name="msg"></param>
        public void ReportTestFailure(string msg) {
            Debug.WriteLine("Test failure: " + msg);
            PrintProgress(" " + msg + "\r\n", Colors.Blue);
        }

        private static string? GetTestRoot() {
            string exeName = typeof(TestRunner).Assembly.Location;
            string? baseDir = Path.GetDirectoryName(exeName);
            if (string.IsNullOrEmpty(baseDir)) {
                throw new IOException("Unable to find dir for: '" + exeName + "'");
            }

            string tryPath;

            tryPath = Path.Combine(baseDir, TEST_DATA_FILENAME);
            if (Directory.Exists(tryPath)) {
                return baseDir;
            }

            // Hack during development: remove bin/Debug/net6.0-windows, then move up one
            // more so we're in the top-level directory rather than the TestDiskArc dir.
            string? upDev = baseDir;
            for (int i = 0; i < 4; i++) {
                upDev = Path.GetDirectoryName(upDev);
            }
            if (upDev == null) {
                // Something is really wrong.
                return null;
            }
            tryPath = Path.Combine(upDev, TEST_DATA_FILENAME);
            if (Directory.Exists(tryPath)) {
                return upDev;
            }

            Debug.WriteLine("Unable to find RuntimeData dir near " + exeName);
            return null;
        }

        private static string? GetDLLLocation() {
            string exeName = typeof(TestRunner).Assembly.Location;
            return Path.GetDirectoryName(exeName);
        }
    }
}
