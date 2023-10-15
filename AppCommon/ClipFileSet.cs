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
    /// <para>This holds a set of file entry information that can be serialized and placed on the
    /// system clipboard.</para>
    /// </summary>
    public class ClipFileSet {
        public int Count => Entries.Count;

        public ClipFileEntry this[int index] {
            get { return Entries[index]; }
        }

        /// <summary>
        /// List of entries for the clipboard.  This can be serialized.
        /// </summary>
        public List<ClipFileEntry> Entries { get; private set; } = new List<ClipFileEntry>();

        /// <summary>
        /// Constructs a set of ClipFileEntries that contains the entries provided.  Additional
        /// entries may be added for directories.
        /// </summary>
        /// <param name="archiveOrFileSystem">IArchive or IFileSystem instance.</param>
        /// <param name="entries">List of entries to add.</param>
        /// <param name="baseDir">For disk images, the effective root of the filesystem.</param>
        /// <param name="preserveMode">Preservation mode to use for extracted files.</param>
        /// <param name="useRawData">If true, use "raw" mode on appropriate disks.</param>
        /// <param name="exportSpec">Export specification.  If non-null, this will do an export
        ///   operation rather than extract.</param>
        /// <param name="appHook">Application hook reference.</param>
        public ClipFileSet(object archiveOrFileSystem, List<IFileEntry> entries,
                IFileEntry baseDir, ExtractFileWorker.PreserveMode preserveMode,
                bool useRawData, ConvConfig.FileConvSpec? exportSpec, AppHook appHook) {

            if (archiveOrFileSystem is IArchive) {
                Debug.Assert(baseDir == IFileEntry.NO_ENTRY);
                GenerateFromArchive((IArchive)archiveOrFileSystem, entries, preserveMode,
                    exportSpec, appHook);
            } else if (archiveOrFileSystem is IFileSystem) {
                Debug.Assert(baseDir != IFileEntry.NO_ENTRY);
                GenerateFromDisk((IFileSystem)archiveOrFileSystem, entries, baseDir, preserveMode,
                    useRawData, exportSpec, appHook);
            } else {
                throw new ArgumentException("Invalid archive/fs object");
            }
        }

        private void GenerateFromArchive(IArchive arc, List<IFileEntry> entries,
                ExtractFileWorker.PreserveMode preserveMode,
                ConvConfig.FileConvSpec? exportSpec, AppHook appHook) {
            Debug.Assert(Entries.Count == 0);

            // TODO:
            // - synthesize directory entries as needed; maintain dict with dirs
        }

        private void GenerateFromDisk(IFileSystem fs, List<IFileEntry> entries,
                IFileEntry baseDir, ExtractFileWorker.PreserveMode preserveMode,
                bool useRawData, ConvConfig.FileConvSpec? exportSpec, AppHook appHook) {
            Debug.Assert(Entries.Count == 0);
            fs.PrepareFileAccess(true);

            // TODO
            // - recursively ensure ContainingDir is in set ahead of entry
            // - if entry is directory, see if it's already in set so we don't add twice

            foreach (IFileEntry entry in entries) {
                Entries.Add(new ClipFileEntry(fs, entry));
            }
        }
    }
}
