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

namespace DiskArc.FS {
    /// <summary>
    /// Fixed characteristics of a filesystem or file archive.
    /// </summary>
    /// <remarks>
    /// Immutable.
    /// </remarks>
    public class FSCharacteristics {
        /// <summary>
        /// Short, human-readable name.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Do we support creation of and writing to this class of archive?
        /// </summary>
        public bool CanWrite { get; }

        /// <summary>
        /// Does this filesystem support subdirectories?
        /// </summary>
        public bool IsHierarchical { get; }

        /// <summary>
        /// Pathname directory separator character.  Use IFileEntry.NO_DIR_SEP if the format
        /// is not hierarchical.
        /// </summary>
        public char DirSep { get; }

        /// <summary>
        /// Can resource forks be stored in the archive?
        /// </summary>
        public bool HasResourceForks { get; }

        /// <summary>
        /// Human-readable filename syntax rules.
        /// </summary>
        public string FileNameSyntaxRules { get; }

        /// <summary>
        /// Human-readable volume name syntax rules.
        /// </summary>
        public string VolumeNameSyntaxRules { get; }

        /// <summary>
        /// Earliest file timestamp that can be represented on this filesystem.
        /// </summary>
        public DateTime TimeStampStart { get; }

        /// <summary>
        /// Latest file timestamp that can be represented on this filesystem.
        /// </summary>
        public DateTime TimeStampEnd { get; }


        public FSCharacteristics(string name, bool canWrite, bool isHierarchical,
                char dirSep, bool hasResourceForks, string fnSyntax, string vnSyntax,
                DateTime tsStart, DateTime tsEnd) {
            Name = name;
            CanWrite = canWrite;
            IsHierarchical = isHierarchical;
            DirSep = dirSep;
            HasResourceForks = hasResourceForks;
            FileNameSyntaxRules = fnSyntax;
            VolumeNameSyntaxRules = vnSyntax;
            TimeStampStart = tsStart;
            TimeStampEnd = tsEnd;
        }
    }
}
