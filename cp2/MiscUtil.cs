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
using System.Diagnostics;

using AppCommon;
using CommonUtil;
using DiskArc.Misc;

namespace cp2 {
    internal class MiscUtil {
        /// <summary>
        /// Handles the "un-binscii" command.
        /// </summary>
        public static bool HandleUnBinSCII(string cmdName, string[] args, ParamsBag parms) {
            if (args.Length == 0) {
                CP2Main.ShowUsage(cmdName);
                return false;
            }
            if (parms.Preserve != ExtractFileWorker.PreserveMode.None &&
                    parms.Preserve != ExtractFileWorker.PreserveMode.NAPS) {
                Console.Error.WriteLine("Error: only the 'none' and 'naps' preservation " +
                    " modes are allowed");
                return false;
            }

            // Ideally we'd allow specification of files within archives, but that would
            // require having ExtArchive detect BinSCII segments, or at least throwing back
            // something informative when a file's contents aren't identified as file archive
            // or disk image.  We could do this by creating an IArchive class for BinSCII,
            // though actually treating BinSCII segments as an archive is tricky.

            Dictionary<string, string> prevOpen = new Dictionary<string, string>();

            foreach (string pathName in args) {
                StreamReader? reader = null;
                try {
                    reader = new StreamReader(new FileStream(pathName, FileMode.Open));
                    if (!ExtractBinSCII(pathName, reader, prevOpen, parms)) {
                        return false;
                    }
                } catch (Exception ex) {
                    Console.Error.WriteLine("Error: unable to open input file: " + ex.Message);
                    return false;
                } finally {
                    reader?.Dispose();
                }
            }
            return true;
        }

        /// <summary>
        /// Extracts all BinSCII chunks to files in the current directory.
        /// </summary>
        /// <param name="pathName">Pathname of BinSCII file, for debug messages.</param>
        /// <param name="reader">BinSCII input stream.</param>
        /// <param name="parms">Parameter set.</param>
        /// <returns>True on success.</returns>
        private static bool ExtractBinSCII(string pathName, StreamReader reader,
                Dictionary<string, string> prevOpen, ParamsBag parms) {
            // Decode each chunk we find individually.  We might encounter chunks from more than
            // one output file, and they can appear in any order.

            if (parms.Verbose) {
                Console.WriteLine("Decoding '" + pathName + "'...");
            }

            bool allCrcOk = true;

            byte[] chunkBuf = new byte[BinSCII.CHUNK_SIZE];
            bool found = false;
            while (true) {
                string fileName;
                BinSCII.FileInfo attrs;
                int fileOffset, chunkLen;
                bool chunkCrcOk;
                if (!BinSCII.DecodeNextChunk(reader, chunkBuf, out fileName, out attrs,
                        out fileOffset, out chunkLen, out chunkCrcOk)) {
                    if (!found) {
                        Console.Error.WriteLine("No BinSCII chunks found");
                        return false;
                    } else {
                        break;
                    }
                }
                found = true;
                allCrcOk &= chunkCrcOk;

                int segNum = fileOffset / BinSCII.CHUNK_SIZE;
                if (!parms.Verbose) {
                    if (!chunkCrcOk) {
                        Console.Error.WriteLine("Warning: found bad CRC in '" +
                            pathName + "': '" + fileName + "' chunk " + segNum);
                        // keep going
                    }
                } else {
                    string crcMsg = chunkCrcOk ? "" : " - bad CRC";
                    Console.WriteLine(string.Format("{0,2} {1,-15} {2,-3} ${3,-4:x4}{4}",
                        segNum, fileName, FileTypes.GetFileTypeAbbrev(attrs.FileType),
                        attrs.AuxType, crcMsg));
                }

                string outName = fileName;
                if (parms.Preserve == ExtractFileWorker.PreserveMode.NAPS) {
                    outName += string.Format("#{0:x2}{1:x4}", attrs.FileType, attrs.AuxType);
                }

                if (!prevOpen.ContainsKey(outName) && !parms.Overwrite) {
                    // We're opening this output file for the first time.  Check to see if the
                    // file exists.
                    if (File.Exists(outName)) {
                        Console.Error.WriteLine("Output file '" + outName +
                            "' already exists (add --overwrite?)");
                        return false;
                    }
                }
                prevOpen[outName] = outName;

                using (Stream outStream = new FileStream(outName, FileMode.OpenOrCreate)) {
                    outStream.Position = fileOffset;
                    outStream.Write(chunkBuf, 0, chunkLen);
                }
            }

            return allCrcOk;
        }
    }
}
