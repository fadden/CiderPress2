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
    public class MerlinAsm : Converter {
        public const string TAG = "merlin";
        public const string LABEL = "Merlin Assembler";
        public const string DESCRIPTION = "Converts Merlin assembler source code to plain text.";
        public const string DISCRIMINATOR = "ProDOS TXT or DOS T, usually with extension \".S\".";
        public override string Tag => TAG;
        public override string Label => LABEL;
        public override string Description => DESCRIPTION;
        public override string Discriminator => DISCRIMINATOR;

        private const int MAX_LEN = 64 * 1024;      // arbitrary cap


        private MerlinAsm() { }

        public MerlinAsm(FileAttribs attrs, Stream? dataStream, Stream? rsrcStream,
                ResourceMgr? resMgr, ConvFlags convFlags, AppHook appHook)
                : base(attrs, dataStream, rsrcStream, resMgr, convFlags, appHook) {
            Applic = TestApplicability();
        }

        protected override Applicability TestApplicability() {
            if (DataStream == null || IsRawDOS) {
                return Applicability.Not;
            }
            if (DataStream.Length > MAX_LEN) {
                return Applicability.Not;
            }
            if (FileAttrs.FileType != FileAttribs.FILE_TYPE_TXT) {
                return Applicability.Not;
            }
            // Check extension and file contents.
            string ext = Path.GetExtension(FileAttrs.FileNameOnly).ToLowerInvariant();
            bool hasExt = (ext == ".s");
            bool looksGood = LooksLikeMerlin(DataStream);

            if (hasExt && looksGood) {
                return Applicability.Yes;
            } else if (looksGood) {
                // Possibly Merlin, probably *some* sort of assembler.
                return Applicability.Probably;
            } else if (hasExt) {
                // Unlikely, but offer as non-default option.
                return Applicability.ProbablyNot;
            } else {
                return Applicability.Not;
            }
        }

        /// <summary>
        /// Returns true if the contents of the file look like a Merlin source file.  This will
        /// also return true for DOS ED/ASM files.
        /// </summary>
        /// <remarks>
        /// The file must be exclusively high ASCII and 0x20.  Typical source files start with
        /// a space on 40-60% of lines, and '*' or ';' on most of the rest, but "equates" files
        /// break the rule.
        /// </remarks>
        private static bool LooksLikeMerlin(Stream stream) {
            int lineCount, spaceLineCount, commentLineCount;

            lineCount = spaceLineCount = commentLineCount = 0;
            bool isLineStart = true;

            // Load the entire thing into memory.  We confirmed that it's small.
            byte[] buf = new byte[stream.Length];
            stream.Position = 0;
            stream.ReadExactly(buf, 0, buf.Length);

            int offset = 0;
            while (offset < buf.Length) {
                byte bval = buf[offset++];
                if ((bval & 0x80) == 0 && bval != ' ') {
                    // Low ASCII byte that isn't a space character.  Not our file.
                    return false;
                }
                if (isLineStart) {
                    lineCount++;
                    if ((bval & 0x7f) == ' ' && offset != buf.Length &&
                            (buf[offset] & 0x7f) != ' ') {
                        // Found a space followed by a non-space.
                        spaceLineCount++;
                    }
                    if (bval == ('*' | 0x80)) {
                        commentLineCount++;
                    }
                    isLineStart = false;
                }
                if (bval == ('\r' | 0x80)) {
                    isLineStart = true;
                }
            }

            // Apply heuristics.
            if (lineCount == 0) {
                return false;       // don't divide by zero
            }
            if ((spaceLineCount * 100) / lineCount > 40) {
                return true;
            }
            if (((spaceLineCount + commentLineCount) * 100) / lineCount > 50) {
                return true;
            }
            return false;
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

        private static readonly int[] sTabStops = new int[] { 0, 9, 15, 26 };
        private const int COL_LABEL = 0;
        private const int COL_OPCODE = 1;
        private const int COL_OPERAND = 2;
        private const int COL_COMMENT = 3;

        /// <summary>
        /// Converts a Merlin assembler source file to plain text.
        /// </summary>
        /// <remarks>
        /// <para>We also want to handle DOS Toolkit ED/ASM sources, which are similar but don't
        /// strip the high bit from spaces that are in comments and quoted text.  So we need to
        /// keep track of those things.</para>
        /// </remarks>
        public static void DoConvert(byte[] fileBuf, SimpleText output) {
            TabbedLine lineBuf = new TabbedLine(sTabStops);
            int curCol = -1;
            char quoteChar = '\0';
            int lineNum = 0;

            bool isLineStart = true;
            for (int offset = 0; offset < fileBuf.Length; offset++) {
                byte rawVal = fileBuf[offset];
                char ch = (char)(rawVal & 0x7f);

                bool wasLineStart = isLineStart;
                if (isLineStart) {
                    lineNum++;
                    isLineStart = false;
                    curCol = COL_LABEL;
                    if (rawVal == ('*' | 0x80)) {
                        // Leading '*' makes entire line a comment.  Advance to the column
                        // without generating any spaces.
                        curCol = COL_COMMENT;
                    }
                }

                if (rawVal == ('\r' | 0x80)) {
                    // End of line found.  Copy line buffer to output.
                    lineBuf.MoveLineTo(output);
                    isLineStart = true;
                    if (quoteChar != '\0') {
                        // Unterminated quote.
                        output.Notes.AddW("Unterminated quote on line " + lineNum);
                        quoteChar = '\0';
                    }
                    continue;
                }

                if (curCol >= COL_COMMENT) {
                    lineBuf.Append(ch);
                } else if (quoteChar != '\0') {
                    // In quoted text.  See if this is the close quote.
                    if (ch == quoteChar) {
                        quoteChar = '\0';
                    }
                    lineBuf.Append(ch);
                } else if (curCol == COL_OPERAND &&
                        (rawVal == ('\'' | 0x80)) || rawVal == ('"' | 0x80)) {
                    // Start of quoted text.
                    quoteChar = ch;
                    lineBuf.Append(ch);
                } else if (rawVal == (' ' | 0x80)) {
                    // High-ASCII space, this is a tab.
                    curCol++;
                    Debug.Assert(curCol <= COL_COMMENT);
                    lineBuf.Tab(curCol);
                } else if (rawVal == (';' | 0x80) &&
                        (wasLineStart || fileBuf[offset - 1] == (' ' | 0x80))) {
                    // Found a high-ASCII semicolon at the start of the line, or right after
                    // a high-ASCII space.  Semicolons can appear in the middle of macros, so
                    // we need the extra test to avoid introducing a column break.
                    //
                    // This is a line with just a comment, or a comment on an opcode that
                    // doesn't have an operand.
                    curCol = COL_COMMENT;
                    lineBuf.Tab(curCol);
                    lineBuf.Append(ch);
                } else {
                    lineBuf.Append(ch);
                }
            }

            if (lineBuf.Length != 0) {
                lineBuf.MoveLineTo(output);
            }
        }
    }
}
