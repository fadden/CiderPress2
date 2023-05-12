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
using FileConv.Code;

namespace FileConvTests {
    /// <summary>
    /// Tests import/export of Applesoft BASIC programs.
    /// </summary>
    public class TestApplesoft : ITest {
        // Run a BAS program through a few round trips.
        public static void TestSimple(AppHook appHook) {
            Dictionary<string, string> options = new Dictionary<string, string>();
            ApplesoftImport imp = new ApplesoftImport(appHook);

            // Get the original copy of the BAS file, stored in an SDK archive.
            using Stream origBasStream = Helper.GetCopyOfTestFile("Code:ALL.TOKENS", appHook,
                out FileAttribs attribs);

            // Open the modified text listing.
            using Stream listingTxtStream =
                Helper.OpenTestFile("fileconv/ALL.TOKENS.mod.txt", true, appHook);

            // Convert the text to BAS.
            using Stream firstBasStream = new MemoryStream();
            imp.ConvertFile(listingTxtStream, options, firstBasStream, null);

            // Does it match the original?
            firstBasStream.Position = origBasStream.Position = 0;
            if (FileUtil.CompareStreams(origBasStream, firstBasStream) != 0) {
                throw new Exception("mod conv != original BAS");
            }

            // Convert the BAS program back to text.
            Applesoft exp = new Applesoft(attribs, firstBasStream, null, null,
                Converter.ConvFlags.None, appHook);
            if (exp.Applic <= Converter.Applicability.Not) {
                throw new Exception("not applicable");
            }
            IConvOutput expOut = exp.ConvertFile(options);
            if (expOut is not SimpleText) {
                throw new Exception("output not SimpleText: " + expOut);
            }
            MemoryStream secondTxtStream = new MemoryStream();
            TXTGenerator.Generate((SimpleText)expOut, secondTxtStream);

            // Now convert that back to BAS.
            using Stream secondBasStream = new MemoryStream();
            secondTxtStream.Position = 0;
            imp.ConvertFile(secondTxtStream, options, secondBasStream, null);

            // Does it match the original?
            secondBasStream.Position = origBasStream.Position = 0;
            if (FileUtil.CompareStreams(origBasStream, secondBasStream) != 0) {
                throw new Exception("re-conv != original BAS");
            }
        }
    }
}
