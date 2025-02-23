/*
 * Copyright 2025 faddenSoft
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
    /// Tests the "bless" command.
    /// </summary>
    internal static class TestBless {
        public static void RunTest(ParamsBag parms) {
            Controller.Stdout.WriteLine("  Bless...");
            Controller.PrepareTestTmp(parms);

            //
            // Just need a simple test to exercise the options.
            //
            DoTests(parms);

            Controller.RemoveTestTmp(parms);
        }

        private static void DoTests(ParamsBag parms) {
            // Make a copy of a turducken file.
            string inputFile = Path.Join(Controller.TEST_DATA, "turducken", "MultiPart.hdv");
            string tdnFile = Path.Join(Controller.TEST_TMP, "tdn-test.hdv");
            FileUtil.CopyFile(inputFile, tdnFile);

            // Bless partition #2, using the default boot block.  The turducken image's boot
            // blocks are zeroed, so this should succeed without the --overwrite flag.
            parms.Overwrite = false;
            string extArc = tdnFile + ExtArchive.SPLIT_CHAR + "2";
            if (!DiskUtil.HandleBless("bless", new string[] { extArc, "Dir1", "-" }, parms)) {
                throw new Exception("bless " + extArc + " default failed");
            }
            // Confirm failure with nonexistent dir.
            if (DiskUtil.HandleBless("bless", new string[] { extArc, "DirZZZ", "-" }, parms)) {
                throw new Exception("bless " + extArc + " bad dir succeeded");
            }

            // Create a 1KB boot image file.
            string bootFile = Path.Join(Controller.TEST_TMP, "bootimage.bin");
            using (Stream stream = new FileStream(bootFile, FileMode.Create)) {
                for (int i = 0; i < 1024; i++) {
                    stream.WriteByte(0xcc);
                }
            }

            // Confirm failure with no overwrite flag.
            parms.Overwrite = false;
            if (DiskUtil.HandleBless("bless", new string[] { extArc, "Dir1", bootFile }, parms)) {
                throw new Exception("bless " + extArc + " boot replace succeeded w/o force");
            }
            // Retry with overwrite.
            parms.Overwrite = true;
            if (!DiskUtil.HandleBless("bless", new string[] { extArc, "Dir1", bootFile }, parms)) {
                throw new Exception("bless " + extArc + " boot replace failed w/ force");
            }

            parms.Overwrite = false;
        }
    }
}
