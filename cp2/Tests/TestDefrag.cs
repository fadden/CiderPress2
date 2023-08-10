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
    /// Tests the "defrag" command.
    /// </summary>
    internal static class TestDefrag {
        public static void RunTest(ParamsBag parms) {
            Controller.Stdout.WriteLine("  Defrag...");
            Controller.PrepareTestTmp(parms);

            DoTests(parms);

            Controller.RemoveTestTmp(parms);
        }

        private static void DoTests(ParamsBag parms) {
            // Make a copy of a Pascal disk image.
            string inputFile = Path.Join(Controller.TEST_DATA, "pascal", "Apple Pascal 1.3.po.gz");
            string testFile = Path.Join(Controller.TEST_TMP, "pascal.po.gz");
            FileUtil.CopyFile(inputFile, testFile);
            parms.SkipSimple = true;

            // Run the defragmenter on it.  We're just exercising the command, not evaluating
            // the outcome, so if it reports success we'll believe it.
            if (!DiskUtil.HandleDefrag("defrag", new string[] { testFile }, parms)) {
                throw new Exception("defrag " + testFile + " failed");
            }
        }
    }
}
