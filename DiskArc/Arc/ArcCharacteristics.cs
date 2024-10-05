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
    /// Characteristics and capabilities of a file archive format.  These apply to all instances
    /// of an archive class.
    /// </summary>
    /// <remarks>
    /// <para>Immutable.</para>
    /// </remarks>
    public class ArcCharacteristics {
        /// <summary>
        /// Human-readable name.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Do we support creation of and writing to this class of archive?
        /// </summary>
        public bool CanWrite { get; }

        /// <summary>
        /// Does this archive always have a single entry?
        /// </summary>
        public bool HasSingleEntry { get; }

        /// <summary>
        /// Can resource forks be stored in the archive?
        /// </summary>
        public bool HasResourceForks { get; }

        /// <summary>
        /// Can disk images be stored in the archive?
        /// </summary>
        public bool HasDiskImages { get; }

        /// <summary>
        /// Can an archive-scope comment be stored?
        /// </summary>
        public bool HasArchiveComment { get; }

        /// <summary>
        /// Can comments be placed on individual records?
        /// </summary>
        public bool HasRecordComments { get; }

        /// <summary>
        /// Pathname directory separator character to use for new records.  Use
        /// IFileEntry.NO_DIR_SEP if the format does not support partial paths
        /// (i.e. filename only).
        /// </summary>
        public char DefaultDirSep { get; }

        /// <summary>
        /// Human-readable filename syntax rules.
        /// </summary>
        public string FileNameSyntaxRules { get; }

        /// <summary>
        /// Earliest timestamp that can be represented in this archive.
        /// Use DateTime.MinValue if timestamps aren't supported.
        /// </summary>
        public DateTime TimeStampStart { get; }

        /// <summary>
        /// Latest timestamp that can be represented in this archive.
        /// Use DateTime.MinValue if timestamps aren't supported.
        /// </summary>
        public DateTime TimeStampEnd { get; }


        public ArcCharacteristics(string name, bool canWrite, bool hasSingleEntry,
                bool hasResourceForks, bool hasDiskImages, bool hasArchiveComment,
                bool hasRecordComments, char defaultDirSep, string fnSyntax,
                DateTime tsStart, DateTime tsEnd) {
            Name = name;
            CanWrite = canWrite;
            HasSingleEntry = hasSingleEntry;
            HasResourceForks = hasResourceForks;
            HasDiskImages = hasDiskImages;
            HasArchiveComment = hasArchiveComment;
            HasRecordComments = hasRecordComments;
            DefaultDirSep = defaultDirSep;
            FileNameSyntaxRules = fnSyntax;
            TimeStampStart = tsStart;
            TimeStampEnd = tsEnd;
        }
    }
}
