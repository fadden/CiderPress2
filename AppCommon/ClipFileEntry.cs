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
using DiskArc;
using DiskArc.Arc;
using DiskArc.FS;
using FileConv;
using static DiskArc.Defs;
using static DiskArc.IFileSystem;

namespace AppCommon {
    /// <summary>
    /// <para>File entry information for one fork of one file.  This holds serializable data
    /// that can be placed on the system clipboard.</para>
    /// <para>This includes file attributes and a reference to the file contents, but only the
    /// attributes can be serialized.  The file contents must be transmitted through some other
    /// means.</para>
    /// </summary>
    [Serializable]
    public sealed class ClipFileEntry {
        /// <summary>
        /// Object that "generates" a stream of data for one part of a file.  It's actually
        /// opening the archive or filesystem entry and just copying data out.
        /// </summary>
        public class StreamGenerator {
            private object mArchiveOrFileSystem;
            private IFileEntry mEntry;
            private FilePart mPart;

            /// <summary>
            /// Constructor.
            /// </summary>
            /// <param name="archiveOrFileSystem">IArchive or IFileSystem instance.</param>
            /// <param name="entry">Entry to access.</param>
            /// <param name="part">File part.  This can also specify whether the fork should
            ///   be opened in "raw" mode.</param>
            public StreamGenerator(object archiveOrFileSystem, IFileEntry entry, FilePart part) {
                mArchiveOrFileSystem = archiveOrFileSystem;
                mEntry = entry;
                mPart = part;
            }

            /// <summary>
            /// Copies the file entry contents to a stream.
            /// </summary>
            /// <param name="outStream">Output stream.</param>
            /// <exception cref="IOException">Error while reading data.</exception>
            /// <exception cref="InvalidDataException">Corrupted data found.</exception>
            public void OutputToStream(Stream outStream) {
                if (mArchiveOrFileSystem is IArchive) {
                    IArchive arc = (IArchive)mArchiveOrFileSystem;
                    using (Stream inStream = arc.OpenPart(mEntry, mPart)) {
                        inStream.CopyTo(outStream);
                    }
                } else if (mArchiveOrFileSystem is IFileSystem) {
                    IFileSystem fs = (IFileSystem)mArchiveOrFileSystem;
                    using (Stream inStream = fs.OpenFile(mEntry, FileAccessMode.ReadOnly, mPart)) {
                        inStream.CopyTo(outStream);
                    }
                } else {
                    throw new NotImplementedException("Unexpected: " + mArchiveOrFileSystem);
                }
            }
        }

        /// <summary>
        /// Stream generator.
        /// </summary>
        /// <remarks>
        /// The NonSerialized attribute can only be applied to fields, not properties.
        /// </remarks>
        [NonSerialized]
        public StreamGenerator? mStreamGen = null;

        ///// <summary>
        ///// Partial path to use when dragging the file to a system-specific file area, such as
        ///// Windows Explorer.  The pathname must be compatible with the host system.
        ///// </summary>
        //public string OutputPath { get; set; } = string.Empty;

        /// <summary>
        /// Which fork of the file this is.  Also indicates "raw" file access.
        /// </summary>
        public FilePart Part { get; set; } = FilePart.Unknown;

        /// <summary>
        /// File attributes.
        /// </summary>
        public FileAttribs Attribs { get; set; } = new FileAttribs();

        /// <summary>
        /// Nullary constructor, for the deserializer.
        /// </summary>
        public ClipFileEntry() { }

        /// <summary>
        /// Standard constructor.
        /// </summary>
        public ClipFileEntry(object archiveOrFileSystem, IFileEntry entry, FilePart part) {
            mStreamGen = new StreamGenerator(archiveOrFileSystem, entry, part);
            Part = part;

            Attribs = new FileAttribs(entry);
        }

        public override string ToString() {
            return "[ClipFileEntry " + Attribs.FullPathName + "]";
        }
    }
}
