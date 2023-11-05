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
using System.Reflection;

using CommonUtil;
using DiskArc;

namespace cp2.Tests {
    /// <summary>
    /// Execute library tests.
    /// </summary>
    /// <remarks>
    /// <para>The library test assemblies are loaded dynamically.</para>
    /// </remarks>
    internal static class LibTestRunner {
        /// <summary>
        /// Executes the DiskArc library tests.
        /// </summary>
        public static bool HandleRunDATests(string cmdName, string[] args, ParamsBag parms) {
            return RunLibTests(cmdName, args, "DiskArcTests.dll", "DiskArcTests.ITest", parms);
        }

        /// <summary>
        /// Executes the FileConv library tests.
        /// </summary>
        public static bool HandleRunFCTests(string cmdName, string[] args, ParamsBag parms) {
            return RunLibTests(cmdName, args, "FileConvTests.dll", "FileConvTests.ITest", parms);
        }

        /// <summary>
        /// Common library test entry point.
        /// </summary>
        private static bool RunLibTests(string cmdName, string[] args, string libName,
                string ifaceTypeName, ParamsBag parms) {
            if (args.Length > 2) {
                CP2Main.ShowUsage(cmdName);
                return false;
            }
            string testClass = (args.Length > 0) ? args[0] : string.Empty;
            string testMethod = (args.Length > 1) ? args[1] : string.Empty;

            // The DiskArc and FileConv tests also require access to the archives in the TestData
            // directory.  The test should be run in the directory where TestData lives.
            if (!Directory.Exists(Controller.TEST_DATA)) {
                Console.Error.WriteLine("Error: unable to find " + Controller.TEST_DATA +
                    " directory");
                return false;
            }

            // Library tests require these to be set.
            parms.AppHook.SetOptionBool(DAAppHook.WARN_MARKED_BUT_UNUSED, true);
            parms.AppHook.SetOption(DAAppHook.LIB_TEST_ROOT, Environment.CurrentDirectory);

            // Load the DLL from the directory we're being executed from.
            string exeName = typeof(LibTestRunner).Assembly.Location;
            string? exeDir = Path.GetDirectoryName(exeName);
            if (string.IsNullOrEmpty(exeDir)) {
                Console.Error.WriteLine("Error: unable to find dir for: '" + exeName + "'");
                return false;
            }

            string libPath = Path.Combine(exeDir, libName);
            if (!File.Exists(libPath)) {
                Console.Error.WriteLine("Error: '" + libPath + "' does not exist");
                return false;
            }

            Console.WriteLine("Loading DiskArc library tests from '" + libPath + "'");
            Assembly dll = Assembly.LoadFile(libPath);
            Type? itestType = dll.GetType(ifaceTypeName);
            if (itestType == null) {
                Console.Error.WriteLine("Error: unable to find ITest interface in test library");
                return false;
            }
            IEnumerable<Type>? typesFound = Mirror.FindImplementingTypes(itestType);
            if (typesFound == null) {
                Console.Error.WriteLine("Error: didn't find any test classes");
                return false;
            }

            return RunTests(typesFound, testClass, testMethod, parms.AppHook);
        }


        /// <summary>
        /// Executes all listed tests.
        /// </summary>
        private static bool RunTests(IEnumerable<Type> typesFound, string testClass,
                string testMethod, AppHook appHook) {
            DateTime startWhen = DateTime.Now;
            int successCount = 0;
            int failureCount = 0;

            foreach (Type type in typesFound) {
                if (!string.IsNullOrEmpty(testClass) && type.Name != testClass) {
                    continue;
                }
                Console.WriteLine("Test set: " + type.Name + " ...");
                MethodInfo[] methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static);
                foreach (MethodInfo meth in methods) {
                    if (!string.IsNullOrEmpty(testMethod) && meth.Name != testMethod) {
                        continue;
                    }
                    if (!meth.Name.StartsWith("Test")) {
                        // skip this one
                        continue;
                    }
                    ParameterInfo[] parms = meth.GetParameters();
                    if (parms.Length != 1 || parms[0].ParameterType != typeof(AppHook)) {
                        Console.WriteLine("Skipping test with wrong args: " + meth.Name);
                        continue;
                    }
                    Console.Write("  " + meth.Name + " ...");
                    try {
                        DateTime testStartWhen = DateTime.Now;
                        meth.Invoke(null, new object[] { appHook });
                        // Test completed successfully.
                        int totalMs = (int)(DateTime.Now - testStartWhen).TotalMilliseconds;
                        Console.WriteLine(" (" + totalMs + " ms)");
                        successCount++;
                    } catch (TargetInvocationException ex) {
                        // Test case threw an exception to indicate failure.
                        Console.WriteLine("failed");
                        failureCount++;
                        Console.WriteLine(ex.InnerException);
                    } catch (Exception ex) {
                        // Something bad happened.
                        Console.WriteLine("failed unexpectedly");
                        failureCount++;
                        Console.WriteLine(ex.InnerException);
                    }
                }
            }

            DateTime endWhen = DateTime.Now;

            int numTests = successCount + failureCount;
            if (successCount == 0) {
                Console.WriteLine("No tests found");
            } else if (failureCount == 0) {
                Console.WriteLine(string.Format("All " + numTests +
                    " tests passed in {0:N3} sec",
                    (endWhen - startWhen).TotalSeconds));
            } else {
                Console.WriteLine(successCount + " of " + numTests + " tests passed");
            }
#if !DEBUG
            Console.WriteLine("NOTE: it's best to test with a DEBUG build");
#endif

            return (failureCount == 0);
        }
    }
}
