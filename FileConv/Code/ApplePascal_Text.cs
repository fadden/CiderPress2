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

using CommonUtil;
using DiskArc;

namespace FileConv.Code {
    /// <summary>
    /// Convert an Apple Pascal textfile to plain text.  These files have a 1KB header, don't
    /// allow lines to cross 1KB page boundaries, and run-length encode leading spaces.
    /// </summary>
    public class ApplePascal_Text : Converter {
        public const string TAG = "ptx";
        public const string LABEL = "Apple Pascal Text";
        public const string DESCRIPTION =
            "Converts Apple Pascal text file to plain text.";
        public const string DISCRIMINATOR = "Pascal Textfile, ProDOS PTX.";
        public override string Tag => TAG;
        public override string Label => LABEL;
        public override string Description => DESCRIPTION;
        public override string Discriminator => DISCRIMINATOR;


        private const int TEXT_BLOCK_SIZE = 1024;
        private const int MIN_LEN = TEXT_BLOCK_SIZE;        // must have room for header
        private const int MAX_LEN = 16 * 1024 * 1024 - 1;   // theoretically possible
        private const byte RLE_DELIM = 0x10;
        private const int RLE_COUNT_ADJ = 32;


        private ApplePascal_Text() { }

        public ApplePascal_Text(FileAttribs attrs, Stream? dataStream, Stream? rsrcStream,
                ResourceMgr? resMgr, ConvFlags convFlags, AppHook appHook)
                : base(attrs, dataStream, rsrcStream, resMgr, convFlags, appHook) {
            Applic = TestApplicability();
        }

        protected override Applicability TestApplicability() {
            // Simple file type check, with a test for minimum length.
            if (DataStream == null) {
                return Applicability.Not;
            }
            if (FileAttrs.FileType == FileAttribs.FILE_TYPE_PTX &&
                    DataStream.Length >= MIN_LEN && DataStream.Length <= MAX_LEN) {
                return Applicability.Yes;
            }
            return Applicability.Not;
        }

        public override Type GetExpectedType(Dictionary<string, string> options) {
            return typeof(SimpleText);
        }

        public override IConvOutput ConvertFile(Dictionary<string, string> options) {
            if (Applic <= Applicability.Not) {
                Debug.Assert(false);
                return new ErrorText("ERROR");
            }
            Debug.Assert(DataStream != null);

            // Load entire file.
            DataStream.Position = 0;
            int length = (int)DataStream.Length;
            byte[] dataBuf = new byte[length];
            DataStream.ReadExactly(dataBuf, 0, length);

            SimpleText output = new SimpleText();

            // skip first chunk
            int offset = TEXT_BLOCK_SIZE;
            while (offset < dataBuf.Length) {
                ProcessChunk(dataBuf, offset, output);
                offset += TEXT_BLOCK_SIZE;
            }
            return output;
        }

        /// <summary>
        /// Processes a 1KB chunk.  Files generally appear to be stored as multiples of 1KB,
        /// but we should probably watch out for partial blocks anyway.
        /// </summary>
        private static void ProcessChunk(byte[] buf, int offset, SimpleText output) {
            int startOffset = offset;

            try {
                bool startOfLine = true;
                while (offset - startOffset < TEXT_BLOCK_SIZE) {
                    byte bval = buf[offset++];
                    if (bval == 0x00) {
                        // End of data for this chunk.
                        if (!startOfLine) {
                            output.Notes.AddW("Found NUL in middle of line");
                        }
                        break;
                    } else if (bval == RLE_DELIM) {
                        if (!startOfLine) {
                            output.Notes.AddW("Found DLE in middle of line");
                        } else {
                            // Output spaces.
                            byte spcCount = buf[offset++];
                            if (spcCount >= RLE_COUNT_ADJ) {
                                for (int i = 0; i < spcCount - RLE_COUNT_ADJ; i++) {
                                    output.Append(' ');
                                }
                            } else {
                                output.Notes.AddW("Found invalid indentation count " + spcCount);
                            }
                        }
                        startOfLine = false;
                    } else if (bval == '\r') {
                        output.AppendLine();
                        startOfLine = true;
                    } else {
                        output.AppendPrintable((char)bval);
                        startOfLine = false;
                    }
                }
            } catch (IndexOutOfRangeException) {
                // Unexpected, so handling it as an exception is reasonable.
                output.Notes.AddI("Last block wasn't a full 1KB");
            }
        }
    }
}
