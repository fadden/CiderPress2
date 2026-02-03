/*
 * Copyright 2026 faddenSoft
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
using System.IO;
using System.Text;

using CommonUtil;
using DiskArc.Misc;

namespace DiskArcTests {
    /// <summary>
    /// Test the BinSCII base-64 codec.
    /// </summary>
    public class TestBinSCII : ITest {
        /// <summary>
        /// Tests some arbitrary patterns.
        /// </summary>
        public static void TestCodec(AppHook appHook) {
            byte[] single = { 0xcc };
            DoTestCodec(single, appHook);
            DoTestCodec(Patterns.sRunPattern, appHook);
        }

        /// <summary>
        /// Tests decoding a multi-part Usenet posting.
        /// </summary>
        public static void TestPosting(AppHook appHook) {
            using (Stream bscFile = Helper.OpenTestFile("base64/shrinkit.bsc", true, appHook)) {
                using (StreamReader reader = new StreamReader(bscFile)) {
                    byte[] decBuf = BinSCII.DecodeToBuffer(reader, out string decFileName,
                        out BinSCII.FileInfo decFileInfo, out bool decCrcOk);
                    if (!decCrcOk) {
                        throw new Exception("failed to unpack shrinkit.bsc");
                    }
                }
            }
        }

        /// <summary>
        /// Tests detection of bad CRCs.
        /// </summary>
        public static void TestFail(AppHook appHook) {
            const string FILENAME = "BadCRC";
            byte[] data = Patterns.sRunPattern;
            MemoryStream textBuf = new MemoryStream();
            BinSCII.FileInfo fileInfo = new BinSCII.FileInfo(
                (uint)data.Length, 0xe3, 0xff, 0x1111, 0x42, 0x2222,
                TimeStamp.NO_DATE, TimeStamp.NO_DATE);

            Encoding enc = new ASCIIEncoding();
            using (StreamWriter stream = new StreamWriter(textBuf, enc, -1, true)) {
                BinSCII.EncodeFromBuffer(data, 0, data.Length, FILENAME, fileInfo, stream);
            }

            // Corrupt data at an arbitrary position in the last chunk of data.
            textBuf.Position = textBuf.Length - 40;
            byte val = (byte)textBuf.ReadByte();
            textBuf.Position--;
            if (val == 'A') {
                textBuf.WriteByte((byte)'B');
            } else {
                textBuf.WriteByte((byte)'A');
            }

            textBuf.Position = 0;
            using (StreamReader stream = new StreamReader(textBuf, enc, false, -1, true)) {
                byte[] decBuf = BinSCII.DecodeToBuffer(stream, out string decFileName,
                    out BinSCII.FileInfo decFileInfo, out bool decCrcOk);
                if (decCrcOk) {
                    throw new Exception("failed to notice bad data");
                }
                if (fileInfo != decFileInfo) {
                    throw new Exception("file info mismatch");
                }
            }
        }

        private static void DoTestCodec(byte[] data, AppHook appHook) {
            const string FILENAME = "TestFile";
            MemoryStream textBuf = new MemoryStream();

            BinSCII.FileInfo fileInfo = new BinSCII.FileInfo(
                (uint)data.Length, 0xc3, 0x06, 0xff01, 0x01, 0x1234,
                new DateTime(1977, 6, 1, 1, 2, 0), new DateTime(1986, 9, 15, 4, 5, 0));

            // Encode to memory stream.
            Encoding enc = new ASCIIEncoding();
            using (StreamWriter stream = new StreamWriter(textBuf, enc, -1, true)) {
                BinSCII.EncodeFromBuffer(data, 0, data.Length, FILENAME, fileInfo, stream);
            }

            // Decode from memory stream.  Compare contents and attributes.
            textBuf.Position = 0;
            using (StreamReader stream = new StreamReader(textBuf, enc, false, -1, true)) {
                byte[] decBuf = BinSCII.DecodeToBuffer(stream, out string decFileName,
                    out BinSCII.FileInfo decFileInfo, out bool decCrcOk);
                if (!RawData.CompareBytes(data, decBuf, data.Length)) {
                    throw new Exception("data mismatch; srcLen=" + data.Length +
                        " decLen=" + decBuf.Length);
                }
                if (!decCrcOk) {
                    throw new Exception("contents matched but CRC did not");
                }
                if (decFileName != FILENAME) {
                    throw new Exception("filename mismatch");
                }
                if (fileInfo != decFileInfo) {
                    throw new Exception("file info mismatch");
                }
            }
        }
    }
}
