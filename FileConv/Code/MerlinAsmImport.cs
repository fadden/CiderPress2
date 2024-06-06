/*
 * Copyright 2024 faddenSoft
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
using System.Text;

using CommonUtil;
using DiskArc;
using static FileConv.Converter;

/*
Line configurations we need to test:

blank:
full-line comment:     ;.....
full-line comment:     *.....
label-only:            blah
label+operand:         blah  pha
label+operand+opcode:  blah  lda  #$30
label+operand+string:  blah  asc  "with spaces"
all four:              blah  asc  'with spaces'  ;comment words
blank+comment:                                   ;comment words
label+comment:         blah                      ;comment words
label+operand+comment: blah  pha                 ;comment words

These could have trailing spaces, e.g. label-only could be "blah ".  This should be trimmed
out of non-comment fields, even if there is no subsequent field.  (FWIW, Merlin-encoded source
files can have trailing spaces after operands.)

Normally each field is separated by a high-ASCII space.  If operand or opcode+operand is
missing but there's an end-of-line comment, a single separator character is sufficient.  However,
if you enter more in the editor, they will be stored in the file.
*/

namespace FileConv.Code {
    /// <summary>
    /// Imports a text file, converting the end-of-line markers and character set.
    /// </summary>
    public class MerlinAsmImport : Importer {
        public const string TAG = "merlin";
        public const string LABEL = "Merlin Assembler";
        public const string DESCRIPTION =
            "Converts Merlin source code from plain text to Merlin's ProDOS format.  " +
            "This does not check syntax or modify content, so the input must be a valid " +
            "Merlin assembly source file.";
        public const string DISCRIMINATOR = "file ending in \".S.TXT\".";
        public override string Tag => TAG;
        public override string Label => LABEL;
        public override string Description => DESCRIPTION;
        public override string Discriminator => DISCRIMINATOR;

        private const string TXT_EXT = ".txt";


        private MerlinAsmImport() { }

        public MerlinAsmImport(AppHook appHook) : base(appHook) {
            HasDataFork = true;
            HasRsrcFork = false;
        }

        public override Applicability TestApplicability(string fileName) {
            // Just check for the ".txt", don't worry about the ".S".
            string ext = Path.GetExtension(fileName).ToLowerInvariant();
            if (ext == ".txt") {
                return Applicability.Yes;
            } else {
                return Applicability.Maybe;
            }
        }

        public override string StripExtension(string fullPath) {
            string ext = Path.GetExtension(fullPath).ToLowerInvariant();
            if (string.IsNullOrEmpty(ext) || fullPath.Length == ext.Length) {
                return fullPath;        // no extension, or nothing but extension
            }
            if (ext != TXT_EXT) {
                return fullPath;
            }
            return fullPath.Substring(0, fullPath.Length - ext.Length);
        }

        public override void GetFileTypes(out byte proType, out ushort proAux,
                out uint hfsFileType, out uint hfsCreator) {
            // HFS type won't matter since this is expected to end up on a ProDOS disk and won't
            // have a resource fork, but we can set it anyway.
            hfsFileType = FileAttribs.TYPE_TEXT;
            hfsCreator = FileAttribs.CREATOR_CPII;
            proType = FileAttribs.FILE_TYPE_TXT;
            proAux = 0x0000;
        }

        private enum Col { Unknown = 0, Label, Opcode, Operand, Comment };

        public override void ConvertFile(Stream inStream, Dictionary<string, string> options,
                Stream? dataOutStream, Stream? rsrcOutStream) {
            if (dataOutStream == null) {
                return;
            }

            byte[] outBuf = new byte[256];

            // Input should be plain ASCII.  Treat it as UTF-8 just in case there are non-ASCII
            // characters that need to be parsed and discarded.
            using (StreamReader reader = new StreamReader(inStream, Encoding.GetEncoding("utf-8"),
                    false, -1, true)) {

                string? line;
                while ((line = reader.ReadLine()) != null) {
                    Debug.WriteLine("LINE: " + line);
                    int outPos = 0;
                    if (line.Length >= outBuf.Length - 1) {
                        dataOutStream.Write(Encoding.ASCII.GetBytes("!LINE TOO LONG!"));
                        continue;
                    }

                    if (line.Length == 0) {
                        // Blank line.
                    } else if (line[0] == ';' || line[0] == '*') {
                        // Full-line comment.
                        for (outPos = 0; outPos < line.Length; outPos++) {
                            char ch = line[outPos];
                            if (ch == ' ') {
                                outBuf[outPos] = (byte)' ';
                            } else {
                                outBuf[outPos] = (byte)(ch | 0x80);
                            }
                        }
                    } else {
                        // Standard line.
                        int inPos = 0;
                        CopyLabel(line, ref inPos, outBuf, ref outPos);
                        CopyOpcode(line, ref inPos, outBuf, ref outPos);
                        CopyOperand(line, ref inPos, outBuf, ref outPos);
                        CopyComment(line, ref inPos, outBuf, ref outPos);
                    }
                    // Add a high-ASCII carriage return, and write the output.
                    outBuf[outPos++] = '\r' | 0x80;
                    dataOutStream.Write(outBuf, 0, outPos);
                }
            }
        }

        private static void CopyLabel(string line, ref int inPos, byte[] outBuf, ref int outPos) {
            // Copy label characters.  If the line starts with a space, there's no label.
            while (inPos < line.Length && line[inPos] != ' ') {
                outBuf[outPos++] = (byte)(line[inPos++] | 0x80);
            }
            // Consume trailing spaces.
            while (inPos < line.Length && line[inPos] == ' ')
                inPos++;
        }

        private static void CopyOpcode(string line, ref int inPos, byte[] outBuf, ref int outPos) {
            if (inPos == line.Length) {
                return;
            }
            Debug.Assert(line[inPos] != ' ');

            if (line[inPos] == ';') {
                return;                         // no opcode, just a comment
            }
            outBuf[outPos++] = ' ' | 0x80;      // output field separator

            // Copy opcode characters.
            while (inPos < line.Length && line[inPos] != ' ') {
                outBuf[outPos++] = (byte)(line[inPos++] | 0x80);
            }
            // Consume trailing spaces.
            while (inPos < line.Length && line[inPos] == ' ')
                inPos++;
        }

        private static void CopyOperand(string line, ref int inPos, byte[] outBuf, ref int outPos) {
            if (inPos == line.Length) {
                return;
            }
            Debug.Assert(line[inPos] != ' ');

            if (line[inPos] == ';') {
                return;                         // no operand, just a comment
            }
            outBuf[outPos++] = ' ' | 0x80;      // output field separator

            // Copy operand characters, stopping on the first non-quoted space or EOL.
            const char NO_QUOTE = '\0';         // assume '\0' won't appear in input
            char quoteCh = NO_QUOTE;
            while (inPos < line.Length) {
                char ch = line[inPos++];
                if (ch == ' ') {
                    if (quoteCh == NO_QUOTE) {
                        break;
                    } else {
                        outBuf[outPos++] = (byte)' ';
                    }
                } else {
                    outBuf[outPos++] = (byte)(ch | 0x80);
                }
                if (quoteCh == NO_QUOTE && (ch == '"' || ch == '\'')) {
                    quoteCh = ch;
                } else if (ch == quoteCh) {
                    // Found matching quote.
                    quoteCh = NO_QUOTE;
                }
            }
            if (quoteCh != NO_QUOTE) {
                Debug.WriteLine("Found unterminated quote in operand");
            }
            // Consume trailing spaces.
            while (inPos < line.Length && line[inPos] == ' ')
                inPos++;
        }

        private static void CopyComment(string line, ref int inPos, byte[] outBuf, ref int outPos) {
            if (inPos == line.Length) {
                return;
            }
            Debug.Assert(line[inPos] != ' ');
            outBuf[outPos++] = ' ' | 0x80;      // output field separator

            while (inPos < line.Length) {
                char ch = line[inPos++];
                if (ch == ' ') {
                    outBuf[outPos++] = (byte)' ';
                } else {
                    outBuf[outPos++] = (byte)(ch | 0x80);
                }
            }
        }
    }
}
