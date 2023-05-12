/*
 * Copyright 2022 faddenSoft
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
using System.IO;

namespace CommonUtil {
    /// <summary>
    /// File I/O utilities.
    /// </summary>
    public static class FileUtil {
        /// <summary>
        /// Copies a specific amount of data from one stream to another.  The streams are
        /// not seeked.
        /// </summary>
        /// <param name="src">Source stream.</param>
        /// <param name="dst">Destination stream.</param>
        /// <param name="count">Number of bytes to copy.</param>
        /// <param name="tmpBuf">Temporary buffer to use.</param>
        /// <exception cref="EndOfStreamException">Source stream had fewer than "count" bytes
        ///   of data available.</exception>
        public static void CopyStream(Stream src, Stream dst, long count, byte[] tmpBuf) {
            if (count < 0) {
                throw new ArgumentException("Invalid count (" + count + ")");
            }
            while (count != 0) {
                int copyCount = (int)Math.Min(count, tmpBuf.Length);
                int actual = src.Read(tmpBuf, 0, copyCount);
                if (actual == 0) {
                    throw new EndOfStreamException();
                }
                dst.Write(tmpBuf, 0, actual);

                count -= actual;
            }
        }

        /// <summary>
        /// Reads the requested amount of data from the stream.  If less data is available,
        /// throws EndOfStreamException.
        /// </summary>
        /// <remarks>
        /// <para>This will be part of the Stream class in .NET 7.  We're not building against
        /// that yet, so use an extension method with an identical signature and behavior.  (If
        /// Stream defines an identical method, this will be ignored.)</para>
        /// <para>This call is needed because the Stream definition says, "An implementation is
        /// free to return fewer bytes than requested even if the end of the stream has not been
        /// reached."  It will always return at least 1 byte per call until EOF.</para>
        /// </remarks>
        /// <param name="stream">Stream to read from.</param>
        /// <param name="buffer">Buffer to read into.</param>
        /// <param name="offset">Initial offset to read data at.</param>
        /// <param name="count">Number of bytes to read.</param>
        /// <exception cref="EndOfStreamException">End of stream reached.</exception>
        public static void ReadExactly(this Stream stream, byte[] buffer, int offset, int count) {
            if (buffer == null) {
                throw new ArgumentNullException("buffer is null");
            }
            if (count < 0 || offset < 0 || offset > buffer.Length - count) {
                throw new ArgumentException("invalid offset/count: offset=" + offset +
                    " count=" + count + " length=" + buffer.Length);
            }
            while (count != 0) {
                int actual = stream.Read(buffer, offset, count);
                if (actual == 0) {
                    throw new EndOfStreamException();
                }
                offset += actual;
                count -= actual;
            }
        }

        /// <summary>
        /// Makes a copy of a file.  The destination file must not already exist.
        /// </summary>
        /// <param name="srcPathName">Source pathname.</param>
        /// <param name="dstPathName">Destination pathname.</param>
        public static void CopyFile(string srcPathName, string dstPathName) {
            using (FileStream inFile = new FileStream(srcPathName, FileMode.Open)) {
                using (FileStream outFile = new FileStream(dstPathName, FileMode.CreateNew)) {
                    inFile.CopyTo(outFile);
                }
            }
        }

        /// <summary>
        /// Performs a binary comparison on two disk files.
        /// </summary>
        /// <param name="fileName1">First file.</param>
        /// <param name="fileName2">Second file.</param>
        /// <returns>Less than, equal to, or greater than zero depending on whether the contents
        ///   of file 1 are less than, equal to, or greater than the contents of file 2.</returns>
        public static int CompareFiles(string fileName1, string fileName2) {
            using FileStream stream1 = new FileStream(fileName1, FileMode.Open, FileAccess.Read);
            using FileStream stream2 = new FileStream(fileName2, FileMode.Open, FileAccess.Read);
            return CompareStreams(stream1, stream2);
        }

        /// <summary>
        /// Performs a binary comparison on two streams.  The streams do not need to be seekable,
        /// but must be positioned at the start of the range to compare.  The streams will be
        /// read until at least one reaches EOF.
        /// </summary>
        /// <param name="stream1">First stream.</param>
        /// <param name="stream2">Second stream.</param>
        /// <returns>Less than, equal to, or greater than zero depending on whether the contents
        ///   of file 1 are less than, equal to, or greater than the contents of file 2.</returns>
        public static int CompareStreams(Stream stream1, Stream stream2) {
            byte[] readBuf1 = new byte[8192];
            byte[] readBuf2 = new byte[8192];
            while (true) {
                // Try to read a full buffer from each.
                int rem1 = (int)(stream1.Length - stream1.Position);
                stream1.ReadExactly(readBuf1, 0, Math.Min(rem1, readBuf1.Length));
                int rem2 = (int)(stream2.Length - stream2.Position);
                stream2.ReadExactly(readBuf2, 0, Math.Min(rem2, readBuf2.Length));

                if (rem1 != rem2) {
                    // Got different amounts of data from each.  Compare the common portion; if
                    // it's identical, the file that ended first is "smaller".
                    int count = Math.Min(rem1, rem2);
                    int diff = RawData.MemCmp(readBuf1, readBuf2, count);
                    if (diff != 0) {
                        return diff;
                    }
                    if (rem1 < rem2) {
                        return -1;
                    } else {
                        return 1;
                    }
                }
                if (rem1 == 0) {
                    return 0;   // EOF reached on both streams
                }
                int cmp = RawData.MemCmp(readBuf1, readBuf2, rem1);
                if (cmp != 0) {
                    return cmp;
                }
            }
        }

        /// <summary>
        /// Recursively deletes all of the files and directories in a directory.  Clears the
        /// "read-only" flag as needed.
        /// </summary>
        /// <remarks>
        /// <see cref="System.IO.Directory.Delete(string, bool)"/> can do a recursive delete,
        /// but stops when it hits a read-only file.
        /// </remarks>
        public static void DeleteDirContents(string dirPath) {
            foreach (string str in Directory.EnumerateFileSystemEntries(dirPath)) {
                FileAttributes attr = File.GetAttributes(str);
                if ((attr & FileAttributes.Directory) != 0) {
                    DeleteDirContents(str);
                    Directory.Delete(str);
                } else {
                    if ((attr & FileAttributes.ReadOnly) != 0) {
                        attr &= ~FileAttributes.ReadOnly;
                        File.SetAttributes(str, attr);
                    }
                    File.Delete(str);
                }
            }
        }
    }
}
