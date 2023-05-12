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

using CommonUtil;
using FileConv;
using FileConv.Generic;

// TODO: verify EOL conversion.  Try a file with a changing pattern, e.g. CR / CRLF / LF,
//   and both single- and double-spacing.

namespace FileConvTests {
    /// <summary>
    /// Tests import/export of plain text in various encodings.
    /// </summary>
    public class TestPlainText : ITest {
        internal static readonly char[] sASCIIChars = GenerateChars(Encoding.ASCII);
        internal static readonly char[] sLatinChars = GenerateChars(Encoding.Latin1);
        internal static readonly char[] sCP1252Chars = GenerateChars(Encoding.GetEncoding(1252));

        /// <summary>
        /// Generates the full set of characters defined by the specified encoding.
        /// </summary>
        private static char[] GenerateChars(Encoding enc) {
            if (enc == Encoding.ASCII) {
                char[] chars = new char[128];
                for (int i = 0; i < 128; i++) {
                    chars[i] = (char)i;
                }
                return chars;
            } else {
                byte[] src = new byte[256];
                for (int i = 0; i < 256; i++) {
                    src[i] = (byte)i;
                }
                return enc.GetChars(src);
            }
        }

        public static void _TestImportCharsets(AppHook appHook) {
            // TODO:
            // - Generate input streams with full range of ASCII, iso-8859-1, and cp1252 values.
            //   Import to ASCII and MOR.  Note ASCII accent-stripping behavior.  Need to generate
            //   expected-value tables, which will be using the same routines, so mostly we're
            //   just verifying that the conversions are being applied successfully.
        }

        public static void TestImportMOR(AppHook appHook) {
            // Generate a UTF-8 stream that has all 256 Mac OS Roman characters.
            MemoryStream utfStream = new MemoryStream();
            using (StreamWriter writer =
                    new StreamWriter(utfStream, new UTF8Encoding(false), -1, true)) {
                for (int i = 0; i < 256; i++) {
                    writer.Write(MacChar.MacToUnicode((byte)i, MacChar.Encoding.Roman));
                }
            }

            // Import the UTF-8 stream back to Mac OS Roman.
            PlainTextImport imp = new PlainTextImport(appHook);
            Dictionary<string, string> options = new Dictionary<string, string>() {
                { PlainTextImport.OPT_INCHAR, ConvUtil.CHAR_MODE_UTF8 },
                { PlainTextImport.OPT_OUTCHAR, ConvUtil.CHAR_MODE_MOR },
            };
            utfStream.Position = 0;
            MemoryStream morStream = new MemoryStream();
            imp.ConvertFile(utfStream, options, morStream, null);

            // Confirm that (almost) nothing got altered.
            if (morStream.Length != 256) {
                throw new Exception("Unexpected length " + morStream.Length);
            }
            morStream.Position = 0;
            for (int i = 0; i < 256; i++) {
                int ic;
                if ((ic = morStream.ReadByte()) != i) {
                    // The conversion alters the EOL markers, so LF becomes CR.
                    if (i != '\n') {
                        throw new Exception("Found wrong byte at " + i + ": actual=" + ic);
                    }
                }
            }
        }

        public static void TestExportCharsets(AppHook appHook) {
            MemoryStream morStream = new MemoryStream();
            for (int i = 0; i < 256; i++) {
                morStream.WriteByte((byte)i);
            }

            PlainText exp = new PlainText(new DiskArc.FileAttribs(), morStream, null, null,
                Converter.ConvFlags.None, appHook);
            Dictionary<string, string> options = new Dictionary<string, string>() {
                { PlainText.OPT_PRINT, "false" },
                { PlainText.OPT_CHAR, ConvUtil.CHAR_MODE_MOR },
            };

            // Convert MOR to UTF-8.  LF and CR will be replaced with CRLF, but otherwise the
            // output should match our expected conversion.
            IConvOutput txtOut = exp.ConvertFile(options);
            SimpleText stxt = (SimpleText)txtOut;
            int adjust = 0;
            for (int i = 0; i < 256; i++) {
                char ch = MacChar.MacToUnicode((byte)i, MacChar.Encoding.Roman);
                char xformChar = stxt.Text[i + adjust];
                if (i == '\r' || i == '\n') {
                    ch = Environment.NewLine[0];                // expect first system newline char
                    adjust += Environment.NewLine.Length - 1;   // adjust for CRLF
                }
                if (ch != xformChar) {
                    throw new Exception("char " + i + ": expected \\u" +
                        ((int)ch).ToString("x4") + ", found \\u" +
                        ((int)stxt.Text[i]).ToString("x4"));
                }
            }

            // TODO: test ASCII, ISO Latin-1
        }
    }
}
