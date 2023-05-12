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
using DiskArc;
using FileConv;
using FileConv.Gfx;

namespace FileConvTests {
    /// <summary>
    /// Tests the various graphics exporters.  Not really looking to evaluate the output beyond
    /// ensuring that it's the expected size and has the expected range of colors.
    /// </summary>
    public class TestGraphics : ITest {
        // Test an Apple II hi-res screen capture.
        public static void TestHiRes(AppHook appHook) {
            // Use a file that has all 8 colors.
            using Stream origBasStream = Helper.GetCopyOfTestFile("Graphics:BARS.HR", appHook,
                out FileAttribs attribs);

            HiRes ext = new HiRes(attribs, origBasStream, null, null, Converter.ConvFlags.None,
                appHook);
            if (ext.Applic <= Converter.Applicability.Not) {
                throw new Exception("not applicable");
            }

            Dictionary<string, string> options = new Dictionary<string, string>();
            IConvOutput expOut = ext.ConvertFile(options);
            Bitmap8 b8 = (Bitmap8)expOut;
            Helper.CheckColors(b8, 8);

            // Now repeat, but in B&W mode.  We expect to find the two blacks and the two whites.
            options.Add(HiRes.OPT_BW, true.ToString());
            expOut = ext.ConvertFile(options);
            b8 = (Bitmap8)expOut;
            Helper.CheckColors(b8, 4);
        }
    }
}
