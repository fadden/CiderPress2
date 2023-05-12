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

namespace DiskArc.Arc {
    /// <summary>
    /// Archive-related utility routines.
    /// </summary>
    internal static class ArcUtil {
        /// <summary>
        /// Copies data from one stream to another, optionally compressing it.  If the compression
        /// codec fails, or does not make the output smaller than the input, the original data
        /// is copied to the output without compression (if requested).
        /// </summary>
        /// <remarks>
        /// <para>The output is written to the current output stream position.  On retry the
        /// output stream is rewound and truncated.</para>
        /// </remarks>
        /// <param name="source">Data source.</param>
        /// <param name="archiveStream">Stream that receives the compressed or uncompressed
        ///   data.</param>
        /// <param name="compOutStream">Compression codec stream.  If no compression is desired,
        ///   pass null.</param>
        /// <param name="checker">Optional CRC/checksum calculator.  Applied to the
        ///   uncompressed data.</param>
        /// <param name="isUncompOkay">Set to true if uncompressed data is okay.</param>
        /// <param name="tmpBuf">Temporary buffer, for compression.</param>
        /// <param name="inputLength">Result: length of input stream.</param>
        /// <param name="outputLength">Result: length of compressed or stored data.</param>
        /// <returns>True if compression was used, false if stored uncompressed.</returns>
        public static bool CopyPartSource(IPartSource source, Stream archiveStream,
                Stream? compOutStream, Checker? checker, bool isUncompOkay, byte[] tmpBuf,
                out int inputLength, out int outputLength) {
            long startPosn = archiveStream.Position;
            source.Open();

            try {
                inputLength = outputLength = 0;     // appease compiler

                // If we have a compressor, try that.
                if (compOutStream != null) {
                    while (true) {
                        int actual = source.Read(tmpBuf, 0, tmpBuf.Length);
                        if (actual == 0) {
                            break;
                        }
                        inputLength += actual;
                        if (checker != null) {
                            checker.Update(tmpBuf, 0, actual);
                        }
                        compOutStream.Write(tmpBuf, 0, actual);
                    }
                    compOutStream.Close();

                    outputLength = (int)(archiveStream.Position - startPosn);
                    if (isUncompOkay && outputLength >= inputLength) {
                        // Compression failed.  Clear the compression stream reference, rewind the
                        // data source, truncate the output, and fall through.
                        compOutStream = null;
                        archiveStream.Position = startPosn;
                        archiveStream.SetLength(startPosn);
                        source.Rewind();
                        checker?.Reset();
                    }
                }

                // No compressor or compression failed.  Copy directly to archive.
                if (compOutStream == null) {
                    inputLength = 0;
                    while (true) {
                        int actual = source.Read(tmpBuf, 0, tmpBuf.Length);
                        if (actual == 0) {
                            break;
                        }
                        inputLength += actual;
                        if (checker != null) {
                            checker.Update(tmpBuf, 0, actual);
                        }
                        archiveStream.Write(tmpBuf, 0, actual);
                    }
                    outputLength = inputLength;
                }

                return (compOutStream != null);
            } finally {
                source.Close();     // don't Dispose, we may not be done with it
            }
        }
    }
}
