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
    public class SCAsm : Converter {
        public const string TAG = "scasm";
        public const string LABEL = "S-C Assembler";
        public const string DESCRIPTION = "Converts S-C Assembler source code to plain text.";
        public const string DISCRIMINATOR = "DOS I, with specific contents.";
        public override string Tag => TAG;
        public override string Label => LABEL;
        public override string Description => DESCRIPTION;
        public override string Discriminator => DISCRIMINATOR;

        private const int MIN_LEN = 4;              // len+linenum+eol
        private const int MAX_LEN = 64 * 1024;      // arbitrary cap


        private SCAsm() { }

        public SCAsm(FileAttribs attrs, Stream? dataStream, Stream? rsrcStream,
                ResourceMgr? resMgr, ConvFlags convFlags, AppHook appHook)
                : base(attrs, dataStream, rsrcStream, resMgr, convFlags, appHook) {
            Applic = TestApplicability();
        }

        protected override Applicability TestApplicability() {
            if (DataStream == null || IsRawDOS) {
                return Applicability.Not;
            }
            if (DataStream.Length < MIN_LEN || DataStream.Length > MAX_LEN) {
                return Applicability.Not;
            }
            if (FileAttrs.FileType != FileAttribs.FILE_TYPE_INT) {
                return Applicability.Not;
            }
            if (IntegerBASIC.DetectContentType(DataStream) != IntegerBASIC.ContentType.SCAsm) {
                return Applicability.Not;
            }
            return Applicability.Probably;
        }

        public override Type GetExpectedType(Dictionary<string, string> options) {
            return typeof(SimpleText);
        }

        public override IConvOutput ConvertFile(Dictionary<string, string> options) {
            if (Applic <= Applicability.Not) {
                Debug.Assert(false);
                return new ErrorText("ERROR");
            }
            // Load the entire file into memory.
            Debug.Assert(DataStream != null);
            byte[] fileBuf = new byte[DataStream.Length];
            DataStream.Position = 0;
            DataStream.ReadExactly(fileBuf, 0, (int)DataStream.Length);

            SimpleText output = new SimpleText();
            DoConvert(fileBuf, output);
            return output;
        }

        public static void DoConvert(byte[] fileBuf, SimpleText output) {
            int offset = 0;

            while (offset < fileBuf.Length - 2) {
                int lineStartOffset = offset;

                byte lineLen = fileBuf[offset];
                ushort lineNum = RawData.GetU16LE(fileBuf, offset + 1);
                if (lineLen == 0) {
                    output.Notes.AddE("Found zero-length line");
                    break;
                } else if (offset + lineLen > fileBuf.Length) {
                    output.Notes.AddE("Line ran off end of file");
                    break;
                } else if (fileBuf[offset + lineLen - 1] != 0x00) {
                    output.Notes.AddE("Didn't find end-of-line marker");
                    break;
                }
                offset += 3;

                output.AppendFormat("{0:D4} ", lineNum);
                while (fileBuf[offset] != 0x00) {
                    byte bval = fileBuf[offset++];
                    if (bval < 0x20 || bval > 0xc0) {
                        // Invalid.
                        output.Notes.AddW("Found invalid byte value " + bval.ToString("x2"));
                        output.Append("?");
                    } else if (bval < 0x80) {
                        // Literal.
                        output.Append((char)bval);
                    } else if (bval < 0xc0) {
                        // Encoded run of spaces.
                        int count = bval - 0x80;
                        while (count-- != 0) {
                            output.Append(' ');
                        }
                    } else {
                        Debug.Assert(bval == 0xc0);
                        if (offset - lineStartOffset < 2) {
                            output.Notes.AddW("Line ended mid-RLE");
                            output.Append('!');
                        } else {
                            int count = fileBuf[offset++];
                            char repChar = (char)fileBuf[offset++];
                            while (count-- != 0) {
                                output.Append(repChar);
                            }
                        }
                    }
                }
                offset++;

                output.AppendLine();
            }
        }
    }
}
