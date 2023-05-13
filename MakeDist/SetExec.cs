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

namespace MakeDist {
    /// <summary>
    /// <para>If we're creating our ZIP packages on Windows, the executables for Linux and Mac OS
    /// won't have their execute bits set, so the person doing the download will have to do it
    /// manually.  We can improve things by marking the entries as UNIX-origin and setting the
    /// permission bits.</para>
    /// <para>The .NET <see cref="System.IO.Compression.ZipArchiveEntry"/> class is *almost* what
    /// we want.  It provides access to the "external file attributes field", but doesn't let us
    /// change the "version made by" (the field is private and read-only).</para>
    /// <para>It might be convenient to do this in our own ZIP handler, but there's a bit of a
    /// chicken-and-egg issue with that.  We just need to edit values in the Central Directory,
    /// though, so it's pretty straightforward.</para>
    /// </summary>
    internal static class SetExec {
        private const int EOCD_LENGTH = 22;
        private const int MAX_COMMENT_LENGTH = 65535;
        private static readonly byte[] EOCD_SIGNATURE = new byte[] { 0x50, 0x4b, 0x05, 0x06 };

        private const int CD_LENGTH = 46;
        private const int OS_UNIX = 3;
        private const int S_IFREG = 0x8000;     // 0100000 (S_IFREG)
        private const int EXEC_BITS = 0x1ed;    // 0755 (S_IRWXU|S_IRGRP|S_IXGRP|S_IROTH|S_IXOTH)

        /// <summary>
        /// Handles the "set-exec" command.
        /// </summary>
        /// <param name="args">Argument list, starting with the ZIP file name.</param>
        /// <returns>True on success.</returns>
        public static bool HandleSetExec(string[] args, bool doVerbose) {
            Debug.Assert(args.Length > 1);
            List<string> entryList = new List<string>(args.Length - 1);
            for (int i = 1; i < args.Length; i++) {
                entryList.Add(args[i]);
            }

            using FileStream zipStream =
                new FileStream(args[0], FileMode.Open, FileAccess.ReadWrite);

            // Locate the central directory.
            if (!FindCentralDir(zipStream, out uint cdOffset, out uint cdLength,
                    out ushort cdNumRecs)) {
                return false;
            }

            //Console.WriteLine("+++ CD at +0x" + cdOffset.ToString("x8") + " len=" + cdLength +
            //    " numRecs=" + cdNumRecs);

            // Read the central directory into memory.
            byte[] cdBuf = new byte[cdLength];
            try {
                zipStream.Position = cdOffset;
                zipStream.ReadExactly(cdBuf, 0, (int)cdLength);
            } catch (IOException ex) {
                Console.Error.WriteLine("Error: " + ex.Message);
                return false;
            }

            // Parse the entries, looking for a match.
            int offset = 0;
            while (offset < cdBuf.Length) {
                if (!ParseCDEntry(cdBuf, offset, out int entryLen, out string fileName)) {
                    return false;
                }
                int index = entryList.IndexOf(fileName);
                if (index >= 0) {
                    // Found a match.
                    ushort versMadeBy = RawData.GetU16LE(cdBuf, offset + 0x04);
                    uint extFileAttr = RawData.GetU32LE(cdBuf, offset + 0x26);
                    if (doVerbose) {
                        Console.WriteLine("Found '" + fileName + "' (vers=0x" +
                            versMadeBy.ToString("x4") + ", extAttr=0x" +
                            extFileAttr.ToString("x8") + "), setting exec");
                    }

                    versMadeBy = (ushort)((versMadeBy & 0x00ff) | (OS_UNIX << 8));

                    extFileAttr = (extFileAttr & 0x0000ffff) | ((uint)(S_IFREG | EXEC_BITS) << 16);
                    RawData.SetU16LE(cdBuf, offset + 0x04, versMadeBy);
                    RawData.SetU32LE(cdBuf, offset + 0x26, extFileAttr);

                    // Remove it from the list.
                    entryList.RemoveAt(index);
                }

                offset += entryLen;
            }

            // Did we find everything?
            if (entryList.Count > 0) {
                foreach (string str in entryList) {
                    Console.Error.WriteLine("No match for '" + str + "'");
                }
                return false;
            }

            // All is well, write the central directory back to disk.
            try {
                zipStream.Position = cdOffset;
                zipStream.Write(cdBuf, 0, (int)cdLength);
            } catch (IOException ex) {
                Console.Error.WriteLine("Error during write: " + ex.Message);
                return false;
            }
            return true;
        }

        private static bool FindCentralDir(Stream zipStream, out uint cdOffset, out uint cdLength,
                out ushort cdNumRecs) {
            cdOffset = cdLength = cdNumRecs = 0;

            int readLen = (int)Math.Min(zipStream.Length, EOCD_LENGTH + MAX_COMMENT_LENGTH);
            byte[] eocdBuf = new byte[readLen];
            zipStream.Position = zipStream.Length - readLen;
            try {
                zipStream.ReadExactly(eocdBuf, 0, readLen);
            } catch (IOException ex) {
                Console.Error.WriteLine("Error: " + ex.Message);
                return false;
            }

            // Search for the signature.
            int offset = readLen - EOCD_LENGTH;
            while (offset >= 0) {
                int i;
                for (i = 0; i < EOCD_SIGNATURE.Length; i++) {
                    if (eocdBuf[offset + i] != EOCD_SIGNATURE[i]) {
                        break;
                    }
                }
                if (i == EOCD_SIGNATURE.Length) {
                    break;
                }
                offset--;
            }
            if (offset < 0) {
                Console.Error.WriteLine("Error: not a ZIP file");
                return false;
            }

            cdNumRecs = RawData.GetU16LE(eocdBuf, offset + 0x0a);
            cdLength = RawData.GetU32LE(eocdBuf, offset + 0x0c);
            cdOffset = RawData.GetU32LE(eocdBuf, offset + 0x10);
            return true;
        }

        private static bool ParseCDEntry(byte[] buf, int offset, out int entryLen,
                out string fileName) {
            entryLen = -1;
            fileName = string.Empty;
            if (buf.Length - offset < CD_LENGTH) {
                Console.Error.WriteLine("Error: ran off end of central dir");
                return false;
            }
            ushort fileNameLen = RawData.GetU16LE(buf, offset + 0x1c);
            ushort extraFieldLen = RawData.GetU16LE(buf, offset + 0x1e);
            ushort commentLen = RawData.GetU16LE(buf, offset + 0x20);
            entryLen = CD_LENGTH + fileNameLen + extraFieldLen + commentLen;
            if (buf.Length - offset < entryLen) {
                Console.Error.WriteLine("Error: overran end of central dir");
                return false;
            }

            fileName = Encoding.ASCII.GetString(buf, offset + 0x2e, fileNameLen);
            return true;
        }
    }
}
