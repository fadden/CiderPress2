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
using System.Text;

using CommonUtil;
using DiskArc.FS;

namespace DiskArc.Misc {
    /// <summary>
    /// BinSCII encoding and decoding functions.
    /// </summary>
    public static class BinSCII {
        // Maximum length of a single chunk.
        public const int CHUNK_SIZE = 12 * 1024;

        // Chunk start signature string.
        private const string SIGNATURE = "FiLeStArTfIlEsTaRt";

        // 64-entry encoding symbol dictionary.  All characters must be ASCII.
        private const string SYMBOLS =
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789()";

        private const int BYTES_PER_LINE = 48;
        private const int CHARS_PER_LINE = 64;


        /// <summary>
        /// File information stored in the BinSCII header.  This must be the same for every
        /// chunk, or decoders may get confused.
        /// </summary>
        public class FileInfo {
            public const int SER_ATTR_LEN = 27;

            public uint FileLength { get; private set; }
            public byte Access { get; private set; }
            public byte FileType { get; private set; }
            public ushort AuxType { get; private set; }
            public byte StorageType { get; private set; }
            public ushort BlockCount { get; private set; }
            public DateTime CreateWhen { get; private set; }
            public DateTime ModWhen { get; private set; }

            /// <summary>
            /// Construct a file info object.
            /// </summary>
            /// <param name="fileLength">Total length of file.</param>
            /// <param name="access">ProDOS access byte.</param>
            /// <param name="fileType">ProDOS file type.</param>
            /// <param name="auxType">ProDOS aux type.</param>
            /// <param name="storageType">ProDOS storage type.</param>
            /// <param name="blockCount">ProDOS block count.</param>
            /// <param name="createWhen">Create date, or TimeStamp.NO_DATE.</param>
            /// <param name="modWhen">Modification date, or TimeStamp.NO_DATE.</param>
            public FileInfo(uint fileLength, byte access, byte fileType,
                    ushort auxType, byte storageType, ushort blockCount,
                    DateTime createWhen, DateTime modWhen) {
                FileLength = fileLength;
                Access = access;
                FileType = fileType;
                AuxType = auxType;
                StorageType = storageType;
                BlockCount = blockCount;
                CreateWhen = createWhen;
                ModWhen = modWhen;
            }

            private FileInfo() { }

            /// <summary>
            /// Object that can be used to indicate no attributes were defined.
            /// </summary>
            public static readonly FileInfo NO_ATTRS =
                new FileInfo(0, 0, 0, 0, 0, 0, DateTime.MinValue, DateTime.MinValue);


            /// <summary>
            /// Serializes the file info, combining it with the per-chunk offset and length.
            /// </summary>
            /// <param name="fileOffset">Offset of start of chunk within file.</param>
            /// <param name="chunkLen">Length of chunk.</param>
            public byte[] Serialize(int fileOffset, int chunkLen) {
                byte[] attrBuf = new byte[SER_ATTR_LEN];
                int offset = 0;
                RawData.WriteU24LE(attrBuf, ref offset, FileLength);
                RawData.WriteU24LE(attrBuf, ref offset, (uint)fileOffset);
                RawData.WriteU8(attrBuf, ref offset, Access);
                RawData.WriteU8(attrBuf, ref offset, FileType);
                RawData.WriteU16LE(attrBuf, ref offset, AuxType);
                RawData.WriteU8(attrBuf, ref offset, StorageType);
                RawData.WriteU16LE(attrBuf, ref offset, BlockCount);
                uint createStamp = TimeStamp.ConvertDateTime_ProDOS(CreateWhen);
                RawData.WriteU32LE(attrBuf, ref offset, createStamp);
                uint modStamp = TimeStamp.ConvertDateTime_ProDOS(ModWhen);
                RawData.WriteU32LE(attrBuf, ref offset, modStamp);
                RawData.WriteU24LE(attrBuf, ref offset, (uint)chunkLen);

                ushort headerCrc = CRC16.XMODEM_OnBuffer(0, attrBuf, 0, offset);
                RawData.WriteU16LE(attrBuf, ref offset, headerCrc);
                RawData.WriteU8(attrBuf, ref offset, 0);
                Debug.Assert(offset == SER_ATTR_LEN);

                return attrBuf;
            }

            /// <summary>
            /// Deserializes the file info and the per-chunk offset and length.  Verifies the
            /// CRC but does not attempt to validate the contents of the fields.
            /// </summary>
            /// <param name="attrBuf">Serialized attributes.</param>
            /// <param name="fileOffset">Result: offset of start of chunk within file.</param>
            /// <param name="chunkLen">Result: length of chunk.</param>
            /// <returns>New FileInfo object.</returns>
            public static FileInfo Deserialize(byte[] attrBuf, out int fileOffset,
                    out int chunkLen, out bool crcOk) {
                int offset = 0;
                FileInfo info = new FileInfo();
                info.FileLength = RawData.ReadU24LE(attrBuf, ref offset);
                fileOffset = (int)RawData.ReadU24LE(attrBuf, ref offset);
                info.Access = RawData.ReadU8(attrBuf, ref offset);
                info.FileType = RawData.ReadU8(attrBuf, ref offset);
                info.AuxType = RawData.ReadU16LE(attrBuf, ref offset);
                info.StorageType = RawData.ReadU8(attrBuf, ref offset);
                info.BlockCount = RawData.ReadU16LE(attrBuf, ref offset);
                uint createStamp = RawData.ReadU32LE(attrBuf, ref offset);
                info.CreateWhen = TimeStamp.ConvertDateTime_ProDOS(createStamp);
                uint modStamp = RawData.ReadU32LE(attrBuf, ref offset);
                info.ModWhen = TimeStamp.ConvertDateTime_ProDOS(modStamp);
                chunkLen = (int)RawData.ReadU24LE(attrBuf, ref offset);
                ushort storedCrc = RawData.ReadU16LE(attrBuf, ref offset);
                offset++;       // ignore last byte
                Debug.Assert(offset == SER_ATTR_LEN);

                ushort calcCrc = CRC16.XMODEM_OnBuffer(0, attrBuf, 0, SER_ATTR_LEN - 3);
                crcOk = (storedCrc == calcCrc);
                return info;
            }

            // Define comparison operator for the benefit of unit tests.
            public static bool operator ==(FileInfo a, FileInfo b) {
                return a.FileLength == b.FileLength &&
                    a.Access == b.Access &&
                    a.FileType == b.FileType &&
                    a.AuxType == b.AuxType &&
                    a.StorageType == b.StorageType &&
                    a.BlockCount == b.BlockCount &&
                    a.CreateWhen == b.CreateWhen &&
                    a.ModWhen == b.ModWhen;
            }
            public static bool operator !=(FileInfo a, FileInfo b) {
                return !(a == b);
            }
            public override bool Equals(object? obj) {
                return obj is FileInfo && this == (FileInfo)obj;
            }
            public override int GetHashCode() {
                return (Access << 24) ^ (FileType << 16) ^ AuxType ^
                    CreateWhen.GetHashCode() ^ ModWhen.GetHashCode();
            }
        }

        /// <summary>
        /// Encodes a buffer into BinSCII, possibly generating multiple chunks.
        /// </summary>
        /// <param name="buf">Data buffer.</param>
        /// <param name="offset">Offset of start of data in buffer.</param>
        /// <param name="length">Length of data.</param>
        /// <param name="fileName">ProDOS filename to store in chunk header.</param>
        /// <param name="attrs">File attributes.</param>
        /// <param name="outStream">Stream to write data to.</param>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
        public static void EncodeFromBuffer(byte[] buf, int offset, int length, string fileName,
                FileInfo attrs, StreamWriter outStream) {
            if (offset < 0) {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }
            if (length <= 0) {
                throw new ArgumentOutOfRangeException(nameof(length));
            }

            int fileOffset = 0;
            while (length > 0) {
                int chunkLen = (length > CHUNK_SIZE) ? CHUNK_SIZE : length;
                EncodeChunk(buf, offset + fileOffset, chunkLen, fileOffset, fileName,
                    attrs, outStream);
                length -= chunkLen;
                fileOffset += chunkLen;
            }
        }

        /// <summary>
        /// Generate the BinSCII encoding of a chunk of data.
        /// </summary>
        /// <param name="buf">Data buffer.</param>
        /// <param name="offset">Start offset within buffer.</param>
        /// <param name="length">Length of chunk, must be in the range [1,12288].</param>
        /// <param name="fileOffset">Start offset of data within source file.  (If buf holds the
        ///   entire file, this will be the same as offset.)</param>
        /// <param name="attrs">File attributes (must be the same for all chunks of a file).</param>
        /// <param name="outStream">Stream to write data to.</param>
        public static void EncodeChunk(byte[] buf, int offset, int length, int fileOffset,
                string fileName, FileInfo attrs, StreamWriter outStream) {
            // Approximate length of output, with CRLF line endings.
            int outLines = (length + BYTES_PER_LINE - 1) / BYTES_PER_LINE + 3;
            int outLen = outLines * (CHARS_PER_LINE + 2);

            StringBuilder sb = new StringBuilder(outLen);
            sb.AppendLine(SIGNATURE);
            sb.AppendLine(SYMBOLS);

            string adjName = ProDOS_FileEntry.AdjustFileName(fileName);
            sb.Append((char)(adjName.Length + 'A' - 1));
            sb.Append(adjName);
            for (int i = 15; i > adjName.Length; i--) {
                sb.Append(' ');
            }
            byte[] encAttrs = attrs.Serialize(fileOffset, length);
            EncodeBytes(encAttrs, 0, encAttrs.Length, sb);
            sb.AppendLine(string.Empty);

            ushort crc16 = 0;
            while (length > 0) {
                if (length < BYTES_PER_LINE) {
                    // Special handling for the end of the file.
                    byte[] tmpBuf = new byte[BYTES_PER_LINE];
                    for (int i = 0; i < length; i++) {
                        tmpBuf[i] = buf[offset + i];
                    }
                    buf = tmpBuf;
                    offset = 0;

                }
                crc16 = CRC16.XMODEM_OnBuffer(crc16, buf, offset, BYTES_PER_LINE);
                EncodeBytes(buf, offset, BYTES_PER_LINE, sb);
                sb.AppendLine(string.Empty);

                offset += BYTES_PER_LINE;
                length -= BYTES_PER_LINE;
            }
            byte[] crcBuf = new byte[] { (byte)(crc16 & 0xff), (byte)(crc16 >> 8), 0 };
            EncodeBytes(crcBuf, 0, crcBuf.Length, sb);
            sb.AppendLine(string.Empty);

            Debug.WriteLine("Guessed len=" + outLen + ", actual=" + sb.Length);
            outStream.Write(sb);
        }

        /// <summary>
        /// Encodes 8-bit bytes to characters.
        /// </summary>
        /// <param name="buf">Data buffer.</param>
        /// <param name="offset">Offset of start of data.</param>
        /// <param name="length">Length of data, must be a multiple of 3.</param>
        /// <param name="sb">Output buffer.</param>
        private static void EncodeBytes(byte[] buf, int offset, int length, StringBuilder sb) {
            Debug.Assert(offset >= 0);
            Debug.Assert(length > 0 && length % 3 == 0);

            // Collect bytes three at a time, converting them into four characters.
            //  abcdefgh ijklmnop qrstuvwx -> 00stuvwx 00mnopqr 00ghijkl 00abcdef
            int end = offset + length;
            while (offset < end) {
                sb.Append(SYMBOLS[buf[offset + 2] & 0x3f]);
                sb.Append(SYMBOLS[((buf[offset + 2] >> 6) | (buf[offset + 1] << 2)) & 0x3f]);
                sb.Append(SYMBOLS[((buf[offset + 1] >> 4) | (buf[offset] << 4)) & 0x3f]);
                sb.Append(SYMBOLS[buf[offset] >> 2]);
                offset += 3;
            }
        }

        public static byte[] DecodeToBuffer(StreamReader inStream, out string fileName,
                out FileInfo attrs, out bool crcOk) {
            int fileOffset, chunkLen;
            bool chunkCrcOk;

            // Decode the first chunk.
            crcOk = true;
            byte[] chunkBuf = new byte[CHUNK_SIZE];
            if (!DecodeNextChunk(inStream, chunkBuf, out fileName, out attrs, out fileOffset,
                    out chunkLen, out chunkCrcOk)) {
                Debug.WriteLine("No data found");
                return RawData.EMPTY_BYTE_ARRAY;
            }
            crcOk &= chunkCrcOk;

            // Pull the total file length out of the chunk.
            int fileLength = (int)attrs.FileLength;
            if (fileLength == 0 || fileLength > ProDOS.MAX_FILE_LEN) {
                throw new InvalidDataException("Invalid file length " + fileLength);
            }

            // Allocate a buffer to hold the full contents.
            byte[] fileBuf = new byte[attrs.FileLength];
            Array.Copy(chunkBuf, fileBuf, chunkLen);
            int offset = CHUNK_SIZE;
            while (offset < fileLength) {
                // We double-buffer with the chunk buf because the output is always a multiple
                // of 48 bytes, but we want our buffer to match the actual size.
                if (!DecodeNextChunk(inStream, chunkBuf, out fileName, out attrs, out fileOffset,
                        out chunkLen, out chunkCrcOk)) {
                    break;
                }
                crcOk &= chunkCrcOk;
                Array.Copy(chunkBuf, 0, fileBuf, offset, chunkLen);
                offset += CHUNK_SIZE;
            }
            return fileBuf;
        }

        /// <summary>
        /// Decodes the next BinSCII chunk found in the stream.
        /// </summary>
        /// <param name="inStream">Text input stream.</param>
        /// <param name="outBuf">Output buffer, must hold CHUNK_SIZE bytes.</param>
        /// <param name="fileName">Result: filename.</param>
        /// <param name="attrs">Result: decoded file attributes.</param>
        /// <param name="fileOffset">Result: offset of start of chunk in output file.</param>
        /// <param name="chunkLen">Result: length of the decoded chunk.</param>
        /// <returns>Decoded chunk, or an empty array if no chunk was found.  The buffer may
        ///   be oversized slightly.</returns>
        /// <exception cref="InvalidDataException">Bad header CRC or bad input data
        ///   encountered.  Not thrown for data CRC mismatch.</exception>
        public static bool DecodeNextChunk(StreamReader inStream, byte[] outBuf,
                out string fileName, out FileInfo attrs, out int fileOffset, out int chunkLen,
                out bool crcOk) {
            while (true) {
                // StreamReader.ReadLine() recognizes CR, LF, and CRLF, and removes them.
                string? line = inStream.ReadLine();
                if (line == null) {
                    fileName = string.Empty;
                    attrs = FileInfo.NO_ATTRS;
                    fileOffset = chunkLen = -1;
                    crcOk = false;
                    return false;
                }
                // In theory, lines should not have leading or trailing whitespace.  In practice,
                // there are files that do.
                line = line.Trim();
                if (line == SIGNATURE) {
                    DecodeChunk(inStream, outBuf, out fileName, out attrs, out fileOffset,
                        out chunkLen, out crcOk);
                    return true;
                }
            }
        }

        /// <summary>
        /// Decodes a BinSCII chunk.  The input stream should be positioned immediately after
        /// the signature line.
        /// </summary>
        /// <param name="inStream">Text input stream.</param>
        /// <param name="outBuf">Output buffer, must hold CHUNK_SIZE bytes.</param>
        /// <param name="fileName">Result: filename.</param>
        /// <param name="attrs">Result: decoded file attributes.</param>
        /// <param name="fileOffset">Result: offset of start of chunk in output file.</param>
        /// <param name="chunkLen">Result: length of the decoded chunk.</param>
        /// <exception cref="InvalidDataException">Bad header CRC or bad input data
        ///   encountered.  Not thrown for data CRC mismatch.</exception>
        private static void DecodeChunk(StreamReader inStream, byte[] outBuf, out string fileName,
                out FileInfo attrs, out int fileOffset, out int chunkLen, out bool crcOk) {
            const int EXP_ATTR_LEN = 1 + 15 + 36;

            // Process encoding dictionary.  We want to convert it to a lookup table that converts
            // an ASCII character value to a 6-bit index.
            string? dict = inStream.ReadLine();
            if (dict == null) {
                throw new InvalidDataException("Dictionary line missing");
            }
            dict = dict.Trim();
            if (dict.Length != SYMBOLS.Length) {
                throw new InvalidDataException("Bad dict length");
            }
            int[] lookup = new int[256];
            for (int i = 0; i < lookup.Length; i++) {
                lookup[i] = -1;     // init nonzero so we can detect repeated symbols
            }
            for (int i = 0; i < dict.Length; i++) {
                byte cval = (byte)dict[i];
                if (lookup[cval] >= 0) {
                    throw new InvalidDataException("Dictionary entry appeared twice: " + dict[i]);
                }
                lookup[cval] = i;
            }

            // Process filename and encoded attributes.
            string? attrStr = inStream.ReadLine();
            if (attrStr == null) {
                throw new InvalidDataException("Attributes line missing");
            }
            attrStr = attrStr.Trim();
            if (attrStr.Length != EXP_ATTR_LEN) {
                throw new InvalidDataException("Bad attr line length: " + attrStr.Length);
            }
            int fileNameLen = attrStr[0] - 'A' + 1;
            if (fileNameLen <= 0 || fileNameLen > ProDOS.MAX_FILE_NAME_LEN) {
                throw new InvalidDataException("Bad filename length: " + fileNameLen);
            }
            fileName = attrStr.Substring(1, fileNameLen);
            byte[] attrBuf = new byte[FileInfo.SER_ATTR_LEN];
            DecodeString(attrStr.Substring(16), attrBuf, 0, lookup);
            attrs = FileInfo.Deserialize(attrBuf, out fileOffset, out chunkLen,
                    out bool headerCrcOk);
            if (!headerCrcOk) {
                throw new InvalidDataException("Bad header CRC");
            }
            if (fileOffset % CHUNK_SIZE != 0) {
                throw new InvalidDataException("Unexpected file offset: " + fileOffset);
            }
            if (chunkLen <= 0 || chunkLen > CHUNK_SIZE) {
                throw new InvalidDataException("Unexpected chunk length: " + chunkLen);
            }

            // Decode the data, which will always be a multiple of 48 bytes.
            int lineCount = (chunkLen + BYTES_PER_LINE - 1) / BYTES_PER_LINE;
            int outOffset = 0;
            ushort calcCrc = 0;
            while (lineCount-- > 0) {
                string? line = inStream.ReadLine();
                if (line == null) {
                    throw new InvalidDataException("File ended early");
                }
                // In theory, lines should not have leading or trailing whitespace.  In practice,
                // there are files that do.
                line = line.Trim();
                DecodeString(line, outBuf, outOffset, lookup);
                calcCrc = CRC16.XMODEM_OnBuffer(calcCrc, outBuf, outOffset, BYTES_PER_LINE);
                outOffset += BYTES_PER_LINE;
            }

            // Read and verify the CRC.
            string? crcLine = inStream.ReadLine();
            if (crcLine == null) {
                throw new InvalidDataException("File ended before CRC");
            }
            crcLine = crcLine.Trim();
            if (crcLine.Length != 4) {
                throw new InvalidDataException("Bad CRC line: " + crcLine);
            }
            byte[] crcBuf = new byte[3];
            DecodeString(crcLine, crcBuf, 0, lookup);
            ushort storedCrc = RawData.GetU16LE(crcBuf, 0);
            crcOk = (storedCrc == calcCrc);
            if (!crcOk) {
                Debug.WriteLine("BinSCII: bad data CRC");   // allow caller to decide what to do
            }
        }

        /// <summary>
        /// Decodes a single BinSCII string.
        /// </summary>
        /// <param name="str">Input string.  Length must be a multiple of 4.</param>
        /// <param name="buf">Output buffer.  Must hold (str.Length/4)*3 bytes.</param>
        /// <param name="offset">Start offset in output buffer.</param>
        /// <returns></returns>
        private static void DecodeString(string str, byte[] buf, int offset, int[] lookup) {
            Debug.Assert(str.Length % 4 == 0);

            // Convert ASCII values to indices with the lookup table, then shift the values
            // to recover the original bytes.
            //  00stuvwx 00mnopqr 00ghijkl 00abcdef -> abcdefgh ijklmnop qrstuvwx
            for (int i = 0; i < str.Length; i += 4) {
                buf[offset++] = (byte)((lookup[str[i + 3]] << 2) | (lookup[str[i + 2]] >> 4));
                buf[offset++] = (byte)((lookup[str[i + 2]] << 4) | (lookup[str[i + 1]] >> 2));
                buf[offset++] = (byte)((lookup[str[i + 1]] << 6) | (lookup[str[i]]));
            }
        }
    }
}
