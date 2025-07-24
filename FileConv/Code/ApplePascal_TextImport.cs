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
using System.Diagnostics;
using System.Text;

using CommonUtil;
using DiskArc;
using static FileConv.Converter;

// Testing with AppleWin: AppleWin.exe -l -s5 hdc -s5h1 boot.hdv -s6d1 testdisk.do
// (see https://github.com/AppleWin/AppleWin/issues/104#issuecomment-1668015171)

namespace FileConv.Code {
    /// <summary>
    /// Imports a text file, converting the end-of-line markers and character set.  Lines are
    /// grouped into 1024-byte blocks, and leading whitespace is compressed.
    /// </summary>
    public class ApplePascal_TextImport : Importer {
        public const string TAG = "pastext";
        public const string LABEL = "Pascal Text";
        public const string DESCRIPTION =
            "Converts a text file for use on a UCSD Pascal filesystem.";
        public const string DISCRIMINATOR = "file ending in \".TXT\".";
        public override string Tag => TAG;
        public override string Label => LABEL;
        public override string Description => DESCRIPTION;
        public override string Discriminator => DISCRIMINATOR;

        private const string TXT_EXT = ".txt";
        private const int CHUNK_SIZE = 1024;
        private const int LEADING_OFFSET = 32;
        private const int MAX_LEADING = 0x6d - LEADING_OFFSET;      // match Editor behavior (77)

        private const byte ASCII_CR = 0x0d;         // Ctrl+M
        private const byte ASCII_DLE = 0x10;        // Ctrl+P


        private ApplePascal_TextImport() { }

        public ApplePascal_TextImport(AppHook appHook) : base(appHook) {
            HasDataFork = true;
            HasRsrcFork = false;
        }

        public override Applicability TestApplicability(string fileName) {
            // Look for the ".txt".
            string ext = Path.GetExtension(fileName).ToLowerInvariant();
            if (ext == TXT_EXT) {
                return Applicability.Yes;
            } else {
                return Applicability.Maybe;
            }
        }

        public override string StripExtension(string fullPath) {
            // Text files on Pascal disks are expected to have a ".TEXT" suffix, and would be
            // exported as "FOO.TEXT.txt".  Our job is to remove the ".txt"; if the file is
            // just called "FOO.TEXT", leave it be.
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
            // There's no meaningful HFS type, so we use the encoded ProDOS form.
            proType = FileAttribs.FILE_TYPE_PTX;
            proAux = 0x0000;
            FileAttribs.ProDOSToHFS(proType, proAux, out hfsFileType, out hfsCreator);
        }

        public override void ConvertFile(Stream inStream, Dictionary<string, string> options,
                Stream? dataOutStream, Stream? rsrcOutStream) {
            if (dataOutStream == null) {
                return;
            }
            byte[] outBuf = new byte[CHUNK_SIZE];       // data accumulation buffer
            byte[] zeroBuf = new byte[CHUNK_SIZE];      // buffer full of zeroes

            // Pascal Textfiles start with a 1KB chunk that is reserved for use by the editor.
            // Fill it with zeroes.
            dataOutStream.Write(outBuf, 0, CHUNK_SIZE);

            // All characters should be plain ASCII.  Treat the input as UTF-8 just in case there
            // are non-ASCII characters that need to be parsed and discarded.
            using (StreamReader reader = new StreamReader(inStream, Encoding.GetEncoding("utf-8"),
                    false, -1, true)) {
                string? line;
                int outOffset = 0;
                bool isEmpty = true;
                while ((line = reader.ReadLine()) != null) {
                    Debug.WriteLine("LINE: " + line);

                    // Count up and remove leading spaces.
                    int leading = 0;
                    while (leading < line.Length && line[leading] == ' ' && leading < MAX_LEADING) {
                        leading++;
                    }
                    line = line.Substring(leading);

                    int lineLen = (leading > 0 ? 2 : 0) + line.Length + 1;
                    if (lineLen > CHUNK_SIZE) {
                        // Can't store this line in a single chunk.
                        throw new ConversionException("Line is longer than 1024 bytes");
                    }
                    if (outOffset + lineLen > CHUNK_SIZE) {
                        // Can't fit in current chunk.  Flush what we have, filling in the
                        // empty space at the end with zeroes.
                        Debug.Assert(outOffset > 0);
                        dataOutStream.Write(outBuf, 0, outOffset);
                        if (outOffset < CHUNK_SIZE) {
                            dataOutStream.Write(zeroBuf, 0, CHUNK_SIZE - outOffset);
                        }
                        outOffset = 0;
                        isEmpty = false;
                    }

                    // Generate ASCII byte form.
                    if (leading > 0) {
                        outBuf[outOffset++] = ASCII_DLE;
                        outBuf[outOffset++] = (byte)(leading + LEADING_OFFSET);
                    }
                    foreach (char ch in line) {
                        char newCh = ASCIIUtil.ReduceToASCII(ch, '?');
                        if (newCh < 0x20) {
                            newCh = '?';       // strip control chars
                        }
                        outBuf[outOffset++] = (byte)ch;
                    }
                    // Add a carriage return.
                    outBuf[outOffset++] = ASCII_CR;
                    Debug.Assert(outOffset <= CHUNK_SIZE);
                }

                // Flush anything left over.
                if (isEmpty || outOffset > 0) {
                    if (outOffset > 0) {
                        dataOutStream.Write(outBuf, 0, outOffset);
                    }
                    if (outOffset < CHUNK_SIZE) {
                        dataOutStream.Write(zeroBuf, 0, CHUNK_SIZE - outOffset);
                    }
                }
            }
        }
    }
}
