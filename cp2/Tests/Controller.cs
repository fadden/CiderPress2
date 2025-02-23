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
using System.Text;

using AppCommon;
using CommonUtil;

namespace cp2.Tests {
    /// <summary>
    /// Execute tests for the cp2 command-line utility.
    /// </summary>
    /// <remarks>
    /// Commands are issued internally, passing strings to the command-line entry point, rather
    /// than by launching the command through the shell.
    /// </remarks>
    internal class Controller {
        public const string TEST_DATA = "TestData";
        public const string TEST_TMP = "TestTmp";

        // Preserve original stdout/stderr.
        public static TextWriter Stdout { get; private set; } = Console.Out;
        public static TextWriter Stderr { get; private set; } = Console.Error;

        public static bool HandleRunTests(string cmdName, string[] args, ParamsBag parms) {
            if (args.Length != 0) {
                CP2Main.ShowUsage(cmdName);
                return false;
            }

            if (!Directory.Exists(TEST_DATA)) {
                Console.Error.WriteLine("Error: unable to find " + TEST_DATA + " directory");
                return false;
            }

            // We need to write something to stdout before redirecting it if we want to have
            // a usable Console, because it appears some things are initialized on first use.
            Stdout.WriteLine("Running tests...");

            // Suppress stdout/stderr.
            CaptureConsoleOut();
            CaptureConsoleError();
            parms.Interactive = false;

            try {
                TestNode.RunTest(parms);
                TestCreateDiskImage.RunTest(parms);
                TestList.RunTest(parms);
                TestAdd.RunTest(parms);
                TestExtract.RunTest(parms);
                TestImport.RunTest(parms);
                TestExport.RunTest(parms);
                TestCopy.RunTest(parms);
                TestMove.RunTest(parms);
                TestMkdir.RunTest(parms);
                TestDelete.RunTest(parms);
                TestFileAttr.RunTest(parms);
                TestSectorEdit.RunTest(parms);
                TestCopySectors.RunTest(parms);
                TestCopyBlocks.RunTest(parms);
                TestCopyPartition.RunTest(parms);
                TestBless.RunTest(parms);
                TestDefrag.RunTest(parms);
                TestMetadata.RunTest(parms);
                TestPrint.RunTest(parms);
                TestTest.RunTest(parms);
                TestClip.RunTest(parms);
                TestMisc.RunTest(parms);
            } catch (Exception ex) {
                Stderr.WriteLine("Test failed: " + ex.Message);
                Stderr.WriteLine(ex.StackTrace!.ToString());
                return false;
            } finally {
                ResetConsole();
            }

            // Give any finalizer-based checks a chance to run.
            GC.Collect();
            GC.WaitForPendingFinalizers();

            Console.WriteLine("Success");
#if !DEBUG
            Console.WriteLine("...but it's best to test with a DEBUG build");
#endif
            return true;
        }

        #region Helper functions

        /// <summary>
        /// Redirects Console.Out to a memory stream.
        /// </summary>
        public static MemoryStream CaptureConsoleOut() {
            MemoryStream stream = new MemoryStream();
            StreamWriter sw = new StreamWriter(stream);
            sw.AutoFlush = true;
            Console.SetOut(sw);
            return stream;
        }

        /// <summary>
        /// Redirects Console.Error to a memory stream.
        /// </summary>
        public static MemoryStream CaptureConsoleError() {
            MemoryStream stream = new MemoryStream();
            StreamWriter sw = new StreamWriter(stream);
            sw.AutoFlush = true;
            Console.SetError(sw);
            return stream;
        }

        /// <summary>
        /// Resets stdout/stderr to the original streams.
        /// </summary>
        public static void ResetConsole() {
            Console.SetOut(Stdout);
            Console.SetError(Stderr);
        }

        /// <summary>
        /// Counts the number of lines of text in the stream.
        /// </summary>
        public static int CountLines(MemoryStream stream) {
            int count = 0;
            stream.Position = 0;
            using (StreamReader sr = new StreamReader(stream, Encoding.Default, true, -1, true)) {
                while (sr.ReadLine() != null) {
                    count++;
                }
            }
            return count;
        }

        /// <summary>
        /// Compares stream output to expected output.  Throws on mismatch.
        /// </summary>
        public static void CompareLines(string[] expected, MemoryStream stream) {
            stream.Position = 0;
            using (StreamReader sr = new StreamReader(stream, Encoding.Default, true, -1, true)) {
                int index = 0;
                string? readStr;
                while ((readStr = sr.ReadLine()) != null) {
                    if (index >= expected.Length) {
                        // Do nothing; will fail length check at the end.
                    } else if (readStr != expected[index]) {
                        throw new Exception("String mismatch: expected '" + expected[index] +
                            "', got '" + readStr + "'");
                    }
                    index++;
                }
                if (index != expected.Length) {
                    throw new Exception("Bad string count: expected " + expected.Length +
                        ", got " + index);
                }
            }
        }

        /// <summary>
        /// Determines whether the specified string appears in the memory stream.  The stream is
        /// processed as a series of text lines.
        /// </summary>
        public static bool HasText(string str, MemoryStream stream) {
            stream.Position = 0;
            using (StreamReader sr = new StreamReader(stream, Encoding.Default, true, -1, true)) {
                string? readStr;
                while ((readStr = sr.ReadLine()) != null) {
                    if (readStr.Contains(str)) {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Prepares the TestTmp directory for use.
        /// </summary>
        public static void PrepareTestTmp(ParamsBag parms) {
            if (Directory.Exists(TEST_TMP)) {
                // We're doing a recursive delete, good to have a safety check.  This should
                // only matter if the previous test run failed.
                string[] entries = Directory.GetFileSystemEntries(TEST_TMP);
                if (!parms.Overwrite) {
                    throw new Exception("Temp directory " + TEST_TMP +
                        " already exists (add --overwrite?)");
                }

                // Scrub the directory.
                RemoveTestTmp(parms);
            }

            Directory.CreateDirectory(TEST_TMP);
        }

        /// <summary>
        /// Removes the TestTmp directory and everything inside it.
        /// </summary>
        /// <remarks>
        /// The System.IO.Directory.Delete(path, bool) does the recursive delete, but fails if
        /// a read-only file is present.
        /// </remarks>
        public static void RemoveTestTmp(ParamsBag parms) {
            string fullPath = Path.GetFullPath(TEST_TMP);
            if (parms.Debug) {
                Stdout.WriteLine("+++ removing " + fullPath);
            }
            FileUtil.DeleteDirContents(fullPath);
            Directory.Delete(fullPath);
        }

        /// <summary>
        /// Creates a file in the TestTmp directory, and populates it with data.  The file will
        /// be closed before returning.
        /// </summary>
        /// <param name="fileName">Filename or partial path.</param>
        /// <param name="data">Data to write to file.</param>
        public static void CreateTestFile(string fileName, byte[] data) {
            string pathName = Path.Combine(TEST_TMP, fileName);
            using (FileStream file = new FileStream(pathName, FileMode.CreateNew)) {
                file.Write(data, 0, data.Length);
            }
        }

        /// <summary>
        /// Creates a file in the current directory, and populates it with data.  The file will
        /// be closed before returning.
        /// </summary>
        /// <param name="fileName">Filename or partial path.</param>
        /// <param name="count">Length of file.</param>
        public static void CreateSampleFile(string fileName, byte value, int count) {
            using (FileStream file = new FileStream(fileName, FileMode.CreateNew)) {
                for (int i = 0; i < count; i++) {
                    file.WriteByte(value);
                }
            }
        }

        /// <summary>
        /// Writes multiple copies of a byte value to a stream.
        /// </summary>
        /// <param name="stream">Stream to write to.</param>
        /// <param name="count">Number of bytes to write.</param>
        /// <param name="value">Value to write.</param>
        public static void WriteByteValue(Stream stream, int count, byte value) {
            for (int i = 0; i < count; i++) {
                stream.WriteByte(value);
            }
        }

        /// <summary>
        /// Creates a simple single-valued test source.
        /// </summary>
        /// <param name="length">Length of stream.  May be zero.</param>
        /// <param name="value">Value to store in stream.</param>
        /// <returns>New memory stream.</returns>
        public static SimplePartSource CreateSimpleSource(int length, byte value) {
            byte[] data = new byte[length];
            RawData.MemSet(data, 0, length, value);
            return new SimplePartSource(new MemoryStream(data));
        }

        /// <summary>
        /// Creates a simple test source filled with random values.
        /// </summary>
        /// <param name="length">Length of stream.</param>
        /// <param name="seed">Random number seed.</param>
        /// <returns>New memory stream.</returns>
        public static SimplePartSource CreateRandomSource(int length, int seed) {
            Random rand = new Random(seed);
            byte[] data = new byte[length];
            for (int i = 0; i < length; i++) {
                data[i] = (byte)rand.Next(256);
            }
            return new SimplePartSource(new MemoryStream(data));
        }

        #endregion Helper functions
    }
}
