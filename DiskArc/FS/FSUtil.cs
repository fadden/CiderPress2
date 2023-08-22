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

namespace DiskArc.FS {
    /// <summary>
    /// Helper functions for the filesystem code.
    /// </summary>
    internal static class FSUtil {
        // Buffer full of zeroes.
        private static byte[] sZeroBuf = RawData.EMPTY_BYTE_ARRAY;

        // Buffer full of 0xe5 (for CP/M).
        private static byte[] sE5Buf = RawData.EMPTY_BYTE_ARRAY;

        private static byte[] MakeBuffer(byte value, int count) {
            byte[] buf = new byte[count];
            RawData.MemSet(buf, 0, count, value);
            return buf;
        }

        /// <summary>
        /// Extends a file, filling it with zeroes (or, for CP/M, 0xe5).  The extension is
        /// performed by appending data to the end of the file.  On failure, an exception will
        /// be thrown, with the partial progress intact.
        /// </summary>
        /// <remarks>
        /// <para>This is preferred to simply increasing the storage limit because it overwrites
        /// the previous disk contents.</para>
        /// </remarks>
        /// <param name="stream">Stream to extend.</param>
        /// <param name="newLength">New length.  Must be greater than the current length.</param>
        /// <param name="chunkSize">Allocation unit size.  Each write will be this large to
        ///   minimize the number of I/O calls.</param>
        /// <param name="withE5">If true, the new storage is filled with 0xe5 instead
        ///   of 0x00.</param>
        /// <exception cref="IOException">Disk full or bad block.</exception>
        public static void ExtendFile(Stream stream, long newLength, int chunkSize,
                bool withE5 = false) {
            Debug.Assert(stream.CanWrite);
            if (newLength <= stream.Length) {
                throw new ArgumentOutOfRangeException(nameof(newLength), newLength,
                    "new length must be larger than the old (" + stream.Length + ")");
            }
            if (chunkSize % Defs.BLOCK_SIZE != 0) {
                throw new ArgumentOutOfRangeException(nameof(chunkSize), chunkSize,
                    "should be multiple of BLOCK_SIZE");
            }

            // Pick a buffer of bytes to write.
            byte[] dataSource;
            if (withE5) {
                if (sE5Buf.Length < chunkSize) {
                    sE5Buf = MakeBuffer(0xe5, chunkSize);
                }
                dataSource = sE5Buf;
            } else {
                if (sZeroBuf.Length < chunkSize) {
                    sZeroBuf = MakeBuffer(0x00, chunkSize);
                }
                dataSource = sZeroBuf;
            }

            long origPosn = stream.Position;
            long needed = newLength - stream.Length;
            Debug.Assert(needed > 0);

            try {
                stream.Position = stream.Length;

                // Do a potentially smaller write on the first one so we're aligned with the disk
                // block size.  This avoids partial block writes in the Write() call on
                // subsequent iterations.
                int partialCount = (int)(stream.Position % Defs.BLOCK_SIZE);
                while (needed > 0) {
                    int thisCount = chunkSize - partialCount;
                    if (thisCount > needed) {
                        thisCount = (int)needed;
                    }
                    stream.Write(dataSource, 0, thisCount);

                    needed -= thisCount;
                    partialCount = 0;
                }
            } finally {
                // Probably a DiskFullException, but could also be an I/O error from a bad part
                // of the disk.  The caller can decide if they want to retain the partial progress
                // or roll it back.
                stream.Position = origPosn;
            }
        }
    }
}
