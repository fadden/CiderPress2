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
using System.Text;

using CommonUtil;
using DiskArc;

namespace FileConv.Code {
    /// <summary>
    /// Imports an Applesoft BASIC program from a text file.
    /// </summary>
    /// <remarks>
    /// This does not currently work with the syntax-highlighted RTF output.
    /// </remarks>
    public class ApplesoftImport : Importer {
        public const string TAG = "bas";
        public const string LABEL = "Applesoft BASIC";
        public const string DESCRIPTION =
            "Converts an Applesoft BASIC program listing to tokenized form, ready for execution " +
            "on an Apple II.  Accepts control characters in printable or non-printable form.";
        public const string DISCRIMINATOR =
            "extensions \".BAS\" and \".BAS.TXT\".";
        public override string Tag => TAG;
        public override string Label => LABEL;
        public override string Description => DESCRIPTION;
        public override string Discriminator => DISCRIMINATOR;

        private const string TXT_EXT = ".txt";
        private const string BAS_EXT = ".bas";
        private const string BAS_TXT_EXT = ".bas.txt";

        private const int MAX_LINE_NUM = 63999;
        private const int MAX_TOKEN_LEN = 7;
        private const ushort PTR_PLACEHOLDER = 0xfeed;
        private const ushort BAS_LOAD_ADDR = 0x0801;

        // The longest line you can input from the keyboard is ~256 bytes because of the
        // limitations of the input buffer.  Tokenized lines don't have a limitation beyond
        // the 64K next-line pointer.  Oversizing the buffer here is fine.
        private const int MAX_TOKENIZED_LINE_LEN = 32768;


        private ApplesoftImport() { }

        public ApplesoftImport(AppHook appHook) : base (appHook) {
            appHook.LogD("Importing Applesoft");
            Debug.Assert(DebugTestTokenLookup());
        }

        public override Converter.Applicability TestApplicability(string fileName) {
            string ext = Path.GetExtension(fileName).ToLowerInvariant();
            if (ext == TXT_EXT) {
                return Converter.Applicability.Maybe;
            } else if (ext == BAS_EXT) {
                return Converter.Applicability.Maybe;
            } else if (fileName.Length > BAS_TXT_EXT.Length &&
                    fileName.ToLowerInvariant().EndsWith(BAS_TXT_EXT)) {
                return Converter.Applicability.Yes;
            } else {
                return Converter.Applicability.Not;
            }
        }

        public override string StripExtension(string fullPath) {
            string ext = Path.GetExtension(fullPath).ToLowerInvariant();
            if (string.IsNullOrEmpty(ext) || fullPath.Length == ext.Length) {
                return fullPath;        // no extension, or nothing but extension
            }
            if (ext == BAS_EXT) {
                // Remove ".bas".
                fullPath = fullPath.Substring(0, fullPath.Length - ext.Length);
            } else if (ext == TXT_EXT) {
                // Remove ".txt".
                fullPath = fullPath.Substring(0, fullPath.Length - ext.Length);
                ext = Path.GetExtension(fullPath).ToLowerInvariant();
                if (ext == BAS_EXT) {
                    // Remove ".bas.txt".
                    fullPath = fullPath.Substring(0, fullPath.Length - ext.Length);
                }
            }
            return fullPath;
        }

        public override void GetFileTypes(out byte proType, out ushort proAux,
                out uint hfsFileType, out uint hfsCreator) {
            proType = FileAttribs.FILE_TYPE_BAS;
            proAux = 0x0801;
            FileAttribs.ProDOSToHFS(proType, proAux, out hfsFileType, out hfsCreator);
        }

        public override void ConvertFile(Stream inStream, Dictionary<string, string> options,
                Stream? dataOutStream, Stream? rsrcOutStream) {
            if (dataOutStream == null) {
                return;
            }
            using (StreamReader sr = new StreamReader(inStream, Encoding.UTF8, true, -1, true)) {
                string? error = ParseBAS(sr, dataOutStream);

                if (error != null) {
                    throw new ConversionException(error);
                }
            }
        }

        /// <summary>
        /// Tokenizes an Applesoft BASIC program from a text file.
        /// </summary>
        /// <param name="reader">Input stream reader with text listing.</param>
        /// <param name="outStream">Output stream with BAS program.</param>
        /// <returns>Error message, or null on success.</returns>
        public static string? ParseBAS(StreamReader reader, Stream outStream) {
            const int TOKEN_INDEX_DATA = 0x83 - 128;
            const int TOKEN_INDEX_REM = 0xb2 - 128;
            const int TOKEN_INDEX_PRINT = 0xba - 128;
            const int TOKEN_INDEX_AT = 0xc5 - 128;
            const int TOKEN_INDEX_ATN = 0xe1 - 128;

            byte[] outBuf = new byte[MAX_TOKENIZED_LINE_LEN];
            int outOffset = 0;

            char[] tokenBuf = new char[MAX_TOKEN_LEN];

            int fileLineNum = 0;
            int lastLineNum = -1;
            bool lastWasREM = false;
            string? line;
            while ((line = reader.ReadLine()) != null) {
                fileLineNum++;
                int charPosn = 0;

                // Look for a line number.
                string? err = ReadLineNumber(line, fileLineNum, ref charPosn, out int lineNum);
                if (err != null) {
                    return err;
                }
                if (lineNum == -1) {
                    string trimmed = line.Trim();
                    if (trimmed.Length == 0) {
                        // Totally blank line.  Ignore it.
                        continue;
                    } else if (trimmed[0] == '#' || trimmed.ToUpper().StartsWith("REM")) {
                        // Source-only comment.
                        continue;
                    } else if (lastWasREM && lastLineNum != -1) {
                        // Special case of REM with an embedded carriage return.  Append to
                        // previous line contents.  We need to back up a byte to overwrite
                        // the end-of-line marker.
                        outBuf[outOffset - 1] = (byte)'\r';
                        while (charPosn < line.Length) {
                            outBuf[outOffset++] =
                                (byte)ASCIIUtil.MakeUnprintable(line[charPosn++]);
                        }
                        outBuf[outOffset++] = 0x00;
                        continue;
                    } else {
                        return FailMsg(fileLineNum, "line did not start with number");
                    }
                }
                if (lineNum == lastLineNum) {
                    return FailMsg(fileLineNum, "duplicate line number: " + lineNum);
                }
                if (lineNum < lastLineNum) {
                    return FailMsg(fileLineNum, "line number out of order");
                }
                lastLineNum = lineNum;

                RawData.WriteU16LE(outBuf, ref outOffset, PTR_PLACEHOLDER);
                RawData.WriteU16LE(outBuf, ref outOffset, (ushort)lineNum);

                //
                // Scan tokens.  We need to identify the longest matching token, so we will
                // use "ONERR" instead of stopping with "ON".  For the most part we ignore
                // whitespace, e.g. "HOME" and "H O M E" are equivalent, but it needs to be
                // considered for a couple of special cases.
                //
                // The relevant code in Applesoft starts at $d56c:
                // https://6502disassembly.com/a2-rom/Applesoft.html#SymPARSE
                //

                while (charPosn < line.Length) {
                    // Gather up the next few non-whitespace characters.
                    int tokenBufCnt;
                    int tokenStartPosn = charPosn;
                    bool foundQuote = false;
                    for (tokenBufCnt = 0; tokenBufCnt < MAX_TOKEN_LEN && charPosn < line.Length;
                            tokenBufCnt++) {
                        if (!GetNextNWC(line, ref charPosn, out char ch)) {
                            break;      // end of line
                        }
                        if (ch == '"') {
                            foundQuote = true;
                            break;      // start of quoted text
                        }
                        tokenBuf[tokenBufCnt] = ch;
                    }

                    if (tokenBufCnt == 0) {
                        // Didn't find a token.
                        if (foundQuote) {
                            // Copy the quoted text to the output.  It's possible for strings
                            // to be unterminated at the end of the line.
                            outBuf[outOffset++] = (byte)'"';
                            while (charPosn < line.Length) {
                                char ch = line[charPosn++];
                                outBuf[outOffset++] = (byte)ch;
                                if (ch == '"') {
                                    break;
                                }
                            }
                        } else {
                            // End of line reached.
                            break;
                        }
                    } else {
                        // Found some characters.  Look for the longest matching token.
                        int tokenIndex = FindLongestMatch(tokenBuf, tokenBufCnt, out int foundLen);
                        if (tokenIndex >= 0) {
                            // Found a matching token.
                            if (tokenIndex == TOKEN_INDEX_AT || tokenIndex == TOKEN_INDEX_ATN) {
                                // Special case.  We have to go back and re-scan the original,
                                // taking whitespace into account.  They both start with 'A',
                                // so start by finding the 'T'.
                                int checkPosn = tokenStartPosn + 1;
                                while (char.ToUpper(line[checkPosn++]) != 'T') { }

                                if (checkPosn < line.Length) {
                                    // Is the next one an 'N' (forming "ATN")?
                                    char upch = char.ToUpper(line[checkPosn]);
                                    if (upch == 'N') {
                                        // Yes, keep this token.
                                        Debug.Assert(tokenIndex == TOKEN_INDEX_ATN);
                                    } else if (upch == 'O') {
                                        // Found "ATO", we want to treat it as "A TO".  Consume
                                        // the "A" so we get the "TO" the next time around.
                                        charPosn = tokenStartPosn;
                                        GetNextNWC(line, ref charPosn, out char ch);
                                        outBuf[outOffset++] = (byte)ch;
                                        continue;
                                    } else {
                                        // Did not find "ATN".
                                        if (tokenIndex == TOKEN_INDEX_ATN) {
                                            // There was a space, so we want to reduce to "AT".
                                            tokenIndex = TOKEN_INDEX_AT;
                                            foundLen--;
                                        }
                                    }
                                }
                            }
                            outBuf[outOffset++] = (byte)(tokenIndex + 128);

                            // Consume the characters used by the token, including whitespace.
                            charPosn = tokenStartPosn;
                            for (int i = 0; i < foundLen; i++) {
                                GetNextNWC(line, ref charPosn, out char unusedch);
                            }

                            // Special handling for REM or DATA statements.
                            if (charPosn == line.Length) {
                                // Nothing further on this line.
                            } else if (tokenIndex == TOKEN_INDEX_REM) {
                                lastWasREM = true;
                                if (line[charPosn] == ' ') {
                                    // Eat one leading space, if present.
                                    charPosn++;
                                }
                                // Copy verbatim to end of line.
                                while (charPosn < line.Length) {
                                    outBuf[outOffset++] =
                                        (byte)ASCIIUtil.MakeUnprintable(line[charPosn++]);
                                }
                            } else if (tokenIndex == TOKEN_INDEX_DATA) {
                                if (line[charPosn] == ' ') {
                                    // Eat one leading space, if present.
                                    charPosn++;
                                }
                                // Copy until ':' found, or EOL.
                                bool inQuote = false;
                                while (charPosn < line.Length) {
                                    char ch = line[charPosn];
                                    if (ch == '"') {
                                        inQuote = !inQuote;     // must ignore ':' in quoted strings
                                    }
                                    if (!inQuote && ch == ':') {
                                        break;
                                    }
                                    outBuf[outOffset++] =
                                        (byte)ASCIIUtil.MakeUnprintable(line[charPosn++]);
                                }
                            }
                        } else {
                            charPosn = tokenStartPosn;
                            GetNextNWC(line, ref charPosn, out char ch);
                            if (ch == '?') {
                                // Accept as abbreviation for PRINT.
                                outBuf[outOffset++] = TOKEN_INDEX_PRINT + 128;
                            } else {
                                // REM, DATA, and quoted text were handled earlier.  Everything
                                // else must be converted to upper case.
                                outBuf[outOffset++] = (byte)char.ToUpper(ch);
                            }
                        }
                    }
                }

                outBuf[outOffset++] = 0x00;     // end-of-line marker
            }

            // Output EOF marker.
            outBuf[outOffset++] = 0x00;
            outBuf[outOffset++] = 0x00;

            // Fix up line pointers.
            if (!SetLinePointers(outBuf, outOffset, BAS_LOAD_ADDR)) {
                return FailMsg(fileLineNum, "failed to set line pointers");
            }

            // Write all data to output stream.
            outStream.Write(outBuf, 0, outOffset);

            return null;        // success
        }

        /// <summary>
        /// Returns the next non-whitespace character.
        /// </summary>
        /// <param name="line">Line from input.</param>
        /// <param name="posn">Current position in line.</param>
        /// <param name="ch">Result: character found.</param>
        /// <returns>True on success, false if end-of-line was reached.</returns>
        private static bool GetNextNWC(string line, ref int posn, out char ch) {
            while (posn < line.Length) {
                ch = line[posn++];
                if (ch != ' ' && ch != '\t' && ch != '\r' && ch != '\n') {
                    // Found non-whitespace.  Undo control pictures (which would not have been
                    // used for CRLF).
                    ch = ASCIIUtil.MakeUnprintable(ch);
                    return true;
                }
            }
            ch = '\ufffe';
            return false;
        }

        /// <summary>
        /// Reads the line number from the start of the line.  The line number will be
        /// reported as -1 if the line doesn't start with a number.
        /// </summary>
        private static string? ReadLineNumber(string line, int fileLineNum, ref int charPosn,
                out int lineNum) {
            lineNum = -1;
            int lineNumLen = 0;
            while (charPosn < line.Length) {
                if (!GetNextNWC(line, ref charPosn, out char ch)) {
                    // Reached EOL.  Did we find anything at all?
                    if (lineNumLen == 0) {
                        break;      // completely blank line
                    } else {
                        return FailMsg(fileLineNum, "found nothing but line number");
                    }
                }

                if (ch < '0' || ch > '9') {
                    charPosn--;     // back up
                    break;          // found start of tokens, bail
                }
                if (lineNumLen == 5) {
                    // Max allowed is 63999 ($f9ff).
                    return FailMsg(fileLineNum, "line number has too many digits");
                }
                if (lineNum == -1) {
                    lineNum = 0;
                }
                lineNum = lineNum * 10 + (ch - '0');
                lineNumLen++;
            }
            if (lineNum > MAX_LINE_NUM) {
                return FailMsg(fileLineNum, "line number is > " + MAX_LINE_NUM);
            }
            return null;
        }

        private static bool SetLinePointers(byte[] buf, int length, ushort loadAddr) {
            int offset = 0;

            while (length - offset > 4) {
                if (RawData.GetU16LE(buf, offset) != PTR_PLACEHOLDER) {
                    Debug.Assert(false, "bad ptr placeholder at off=" + offset);
                    return false;
                }
                int ptrOffset = offset;

                // Skip ptr and line number, and search for the next $00 byte.
                offset += 4;
                while (offset < length && buf[offset] != 0x00) {
                    offset++;
                }
                offset++;       // ptr points to first byte past the $00
                if (offset >= length) {
                    Debug.Assert(false, "ran off end");
                    return false;
                }

                // Set the pointer.
                RawData.SetU16LE(buf, ptrOffset, (ushort)(loadAddr + offset));
            }

            return true;
        }

        /// <summary>
        /// Forms an error message.
        /// </summary>
        private static string FailMsg(int fileLineNum, string msg) {
            return "line " + fileLineNum + ": " + msg;
        }

        #region Token Parser

        private static readonly TokenNode sSearchRoot = GenerateTokenLookup();

        private class TokenNode {
            public int mTokenIndex;
            public Dictionary<char, TokenNode> mDict;

            public TokenNode(int tokenIndex) {
                mTokenIndex = tokenIndex;
                mDict = new Dictionary<char, TokenNode>();
            }
        }

        /// <summary>
        /// Generates the token parsing tree.
        /// </summary>
        private static TokenNode GenerateTokenLookup() {
            TokenNode root = new TokenNode(-1);

            for (int i = 0; i < Applesoft.sApplesoftTokens.Length; i++) {
                string token = Applesoft.sApplesoftTokens[i];
                AddToken(i, root, token, 0);
            }

            return root;
        }

        private static void AddToken(int tokenIndex, TokenNode node, string token, int charIndex) {
            Debug.Assert(charIndex < token.Length);
            char ch = token[charIndex];
            if (!node.mDict.TryGetValue(ch, out TokenNode? nextNode)) {
                // Haven't seen this char at this position before.
                if (charIndex == token.Length - 1) {
                    nextNode = new TokenNode(tokenIndex);       // new leaf
                } else {
                    nextNode = new TokenNode(-1);               // new middle-tree node
                }
                node.mDict.Add(ch, nextNode);
            } else {
                // We've seen this before (e.g. ONERR comes before ON).
                if (charIndex == token.Length - 1) {
                    Debug.Assert(nextNode.mTokenIndex == -1);
                    nextNode.mTokenIndex = tokenIndex;          // token on middle-tree node
                }
            }
            if (charIndex < token.Length - 1) {
                AddToken(tokenIndex, nextNode, token, charIndex + 1);
            }
        }

        /// <summary>
        /// Finds the longest match for a token.  Characters are converted to upper case.
        /// </summary>
        /// <param name="tokenBuf">Buffer with 1-N token characters (no whitespace).</param>
        /// <param name="numChars">Number of characters in buffer.</param>
        /// <param name="bestLen">Length of token match, or -1 if no match found.</param>
        /// <returns>Token index of best match, or -1 if no match found.</returns>
        private static int FindLongestMatch(char[] tokenBuf, int numChars, out int bestLen) {
            // Example: tokenBuf = "ONERRGO".  Descend the tree until we find "ON", record that
            // as best index.  Continue until we find "ONERR", record that.  Continue until we
            // run out of tree.
            int bestIndex = -1;
            bestLen = -1;
            TokenNode node = sSearchRoot;
            for (int i = 0; i < numChars; i++) {
                char ch = char.ToUpper(tokenBuf[i]);
                if (!node.mDict.TryGetValue(ch, out TokenNode? nextNode)) {
                    return bestIndex;
                }
                if (nextNode.mTokenIndex >= 0) {
                    bestIndex = nextNode.mTokenIndex;
                    bestLen = i + 1;
                }
                node = nextNode;
            }
            return bestIndex;
        }

        public static bool DebugTestTokenLookup() {
            int bestLen, tokenIndex;

            tokenIndex = FindLongestMatch("END".ToCharArray(), "END".Length, out bestLen);
            Debug.Assert(tokenIndex == 0);
            tokenIndex = FindLongestMatch("For".ToCharArray(), "For".Length, out bestLen);
            Debug.Assert(tokenIndex == 1);
            tokenIndex = FindLongestMatch("ONERR".ToCharArray(), "ONERR".Length, out bestLen);
            Debug.Assert(tokenIndex == 37);
            tokenIndex = FindLongestMatch("ON".ToCharArray(), "ON".Length, out bestLen);
            Debug.Assert(tokenIndex == 52);

            return true;
        }

        #endregion Token Parser
    }
}
