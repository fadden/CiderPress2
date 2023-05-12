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
    /// Tests the "get-metadata" and "set-metadata" commands.
    /// </summary>
    internal static class TestMetadata {
        public static void RunTest(ParamsBag parms) {
            Controller.Stdout.WriteLine("  Metadata...");
            Controller.PrepareTestTmp(parms);

            //
            // Things to test:
            // - get single attribute, get all attributes
            // - add new attribute, delete new attribute
            //

            TestTurducken(parms);

            Controller.RemoveTestTmp(parms);
        }

        private static void TestTurducken(ParamsBag parms) {
            // Make a copy of a turducken file.
            string inputFile = Path.Join(Controller.TEST_DATA, "turducken", "MultiPart.hdv");
            string tdnFile = Path.Join(Controller.TEST_TMP, "tdn-test.hdv");
            FileUtil.CopyFile(inputFile, tdnFile);
            parms.SkipSimple = true;

            // Get an attribute from the WOZ.
            string extArc = tdnFile + ExtArchive.SPLIT_CHAR + "2" + ExtArchive.SPLIT_CHAR +
                "small.woz.gz";
            MemoryStream stdout = Controller.CaptureConsoleOut();
            if (!Metadata.HandleGetMetadata("gm", new string[] { extArc, "meta:language" }, parms)) {
                throw new Exception("gm " + extArc + " get language #1 failed");
            }
            Controller.CompareLines(new string[] { "English" }, stdout);

            // Set an attribute.
            if (!Metadata.HandleSetMetadata("sm",
                    new string[] { extArc, "meta:language", "Spanish" }, parms)) {
                throw new Exception("sm " + extArc + " set language failed");
            }
            stdout = Controller.CaptureConsoleOut();
            if (!Metadata.HandleGetMetadata("gm", new string[] { extArc, "meta:language" }, parms)) {
                throw new Exception("gm " + extArc + " get language #2 failed");
            }
            Controller.CompareLines(new string[] { "Spanish" }, stdout);

            // Add a new item.
            string metaKey = "meta:random_thing";
            string metaValue = "PLOVER / PLUGH \u2022";
            if (!Metadata.HandleSetMetadata("sm",
                    new string[] { extArc, metaKey, metaValue }, parms)) {
                throw new Exception("sm " + extArc + " set new failed");
            }
            // Do a "get all" and see if it appears.
            stdout = Controller.CaptureConsoleOut();
            if (!Metadata.HandleGetMetadata("gm", new string[] { extArc }, parms)) {
                throw new Exception("gm " + extArc + " get new failed");
            }
            if (!Controller.HasText(metaValue, stdout)) {
                throw new Exception("gm " + extArc + " new value not found");
            }
            // Delete the item.
            if (!Metadata.HandleSetMetadata("sm",
                    new string[] { extArc, metaKey }, parms)) {
                throw new Exception("sm " + extArc + " delete new failed");
            }
            // Confirm it's gone.
            if (Metadata.HandleGetMetadata("gm", new string[] { extArc, metaKey }, parms)) {
                throw new Exception("gm " + extArc + " key wasn't deleted");
            }
        }
    }
}
