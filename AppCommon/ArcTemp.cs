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

using DiskArc;
using static DiskArc.Defs;

using CommonUtil;

namespace AppCommon {
    public static class ArcTemp {
        /// <summary>
        /// Extracts data from an archive stream and saves it in a temporary stream.  For
        /// smaller entries we keep it in memory, for larger entries we create a temp file
        /// on disk.
        /// </summary>
        /// <remarks>
        /// Caller must properly dispose the stream.
        /// </remarks>
        /// <param name="archive">File archive</param>
        /// <param name="entry">Archive file entry.</param>
        /// <param name="part">Part identifier.</param>
        /// <returns>File stream with archive entry contents.</returns>
        /// <exception cref="IOException">I/O error reading stream.</exception>
        /// <exception cref="InvalidDataException">Data compression error.</exception>
        public static Stream ExtractToTemp(IArchive archive, IFileEntry entry, FilePart part) {
            using ArcReadStream entryStream = archive.OpenPart(entry, part);

            // Get the length of the part we're extracting.  For gzip we need to do the part
            // query, as the DataLength field isn't currently set.  The part query may understate
            // the size of the output and should not be used to size a buffer.
            entry.GetPartInfo(part, out long partLength, out long unused1,
                out CompressionFormat unused2);

            return TempFile.CopyToTemp(entryStream, partLength);
        }
    }
}
