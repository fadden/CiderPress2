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

using CommonUtil;

namespace cp2.Tests {
    /// <summary>
    /// Tests the "print" command.
    /// </summary>
    internal static class TestPrint {
        public static void RunTest(ParamsBag parms) {
            Controller.Stdout.WriteLine("  Print...");
            Controller.PrepareTestTmp(parms);

            // TODO: test more stuff
            // - ASCII vs. High ASCII (DOS) vs. Mac OS Roman (HFS)
            //   - ctrl char handling, especially high ASCII
            // - CR vs. LF vs. CRLF
            // - extract from both disks and archives
            // - confirm MacZip behavior

            TestTurducken(parms);

            Controller.RemoveTestTmp(parms);
        }

        private static void TestTurducken(ParamsBag parms) {
            // Make a copy of a turducken file.
            string inputFile = Path.Join(Controller.TEST_DATA, "turducken", "MultiPart.hdv");
            string tdnFile = Path.Join(Controller.TEST_TMP, "tdn-test.hdv");
            FileUtil.CopyFile(inputFile, tdnFile);
            parms.SkipSimple = true;

            // Print a file from the gzip-compressed disk volume.
            string extArc = tdnFile + ExtArchive.SPLIT_CHAR + "2" + ExtArchive.SPLIT_CHAR +
                "small.woz.gz";

            MemoryStream stdout = Controller.CaptureConsoleOut();
            if (!Print.HandlePrint("print", new string[] { extArc, "hello.txt" }, parms)) {
                throw new Exception("print " + extArc + " hello.txt failed");
            }
            string[] expected = new string[] {
                "Hello, world!",
            };
            Controller.CompareLines(expected, stdout);

            // Repeat the procedure with a ZIP archive.
            extArc = tdnFile + ExtArchive.SPLIT_CHAR + "2" + ExtArchive.SPLIT_CHAR +
                "Some.Files.zip";
            stdout = Controller.CaptureConsoleOut();
            if (!Print.HandlePrint("print", new string[] { extArc, "file1.txt" }, parms)) {
                throw new Exception("print " + extArc + " file1.txt failed");
            }
            expected = new string[] {
                "This is file #1.  Nothing much to see here."
            };
            Controller.CompareLines(expected, stdout);
        }
    }
}
