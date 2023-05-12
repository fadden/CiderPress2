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

namespace CommonUtil {
    public static class TempFile {
        /// <summary>
        /// Streams this size or smaller are extracted to a MemoryStream().  Increase this to
        /// improve performance when working with larger files at the expense of memory footprint.
        /// </summary>
        private const int MAX_MEM_STREAM = 32 * 1024 * 1024;

        /// <summary>
        /// Copies data from an input stream and saves it in a temporary stream.  If we can
        /// determine that the input is small we will keep it in memory, otherwise we create
        /// a temporary file on disk.
        /// </summary>
        /// <remarks>
        /// <para>The temporary file will be marked "delete on close", so that the file is
        /// discarded when the stream is closed.</para>
        /// </remarks>
        /// <param name="stream">Input stream.  Need not be seekable.</param>
        /// <returns>Temporary stream with a copy of the contents.</returns>
        /// <exception cref="IOException">I/O error reading stream.</exception>
        public static Stream CopyToTemp(Stream stream) {
            long length = -1;
            if (stream.CanSeek) {
                length = stream.Length;
            }

            return CopyToTemp(stream, length);
        }

        /// <summary>
        /// Copies data from an input stream and saves it in a temporary stream.  If the stream
        /// is not small enough to fit in memory, a delete-on-close temporary file is created.
        /// </summary>
        /// <param name="stream">Input stream.  Need not be seekable.</param>
        /// <param name="expectedLength">Expected length of the stream, used to decide if
        ///   we want to keep it in memory.</param>
        /// <returns>Temporary stream with a copy of the contents.</returns>
        /// <exception cref="IOException">I/O error reading stream.</exception>
        public static Stream CopyToTemp(Stream stream, long expectedLength) {
            Stream tmpStream;
            if (expectedLength >= 0 && expectedLength <= MAX_MEM_STREAM) {
                tmpStream = new MemoryStream();
            } else {
                tmpStream = CreateTempFile();
            }

            try {
                stream.CopyTo(tmpStream);
            } catch {
                tmpStream.Dispose();
                throw;
            }
            tmpStream.Position = 0;
            return tmpStream;
        }

        /// <summary>
        /// Creates an "anonymous" temporary file, wherever it is that temp files are created.
        /// The file will be marked as delete-on-close.
        /// </summary>
        /// <remarks>
        /// Bear in mind that delete-on-close is not necessarily delete-on-halt.  These files will
        /// be deleted if the program crashes with an uncaught exception, but possibly not if it's
        /// killed with a signal or the debugger.
        /// </remarks>
        /// <returns>Stream to open temp file.</returns>
        public static Stream CreateTempFile() {
            string tmpPath = Path.GetTempFileName();
            FileAttributes attribs = File.GetAttributes(tmpPath);
            File.SetAttributes(tmpPath, attribs | FileAttributes.Temporary);
            Stream tmpStream = new FileStream(tmpPath, FileMode.Open, FileAccess.ReadWrite,
                FileShare.None, 8192, FileOptions.DeleteOnClose);
            return tmpStream;
        }
    }
}
