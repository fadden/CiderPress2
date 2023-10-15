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
    /// attributes can be serialized.  The file contents must be handled through some other
    /// means available to the caller.</para>
    /// </summary>
    [Serializable]
    public sealed class ClipFileEntry {
        public class StreamGenerator {
            private object mArchiveOrFileSystem;
            private IFileEntry mEntry;

            public StreamGenerator(object archiveOrFileSystem, IFileEntry entry) {
                this.mArchiveOrFileSystem = archiveOrFileSystem;
                this.mEntry = entry;
            }

            public void OutputToStream(Stream stream) {
                stream.Write(Encoding.ASCII.GetBytes("Hello, clipped world!\r\n"));
            }
        }

        [NonSerialized]
        public StreamGenerator mStreamGen;

        public string PathName { get; set; } = string.Empty;

        public FileAttribs Attribs { get; set; }

        public ClipFileEntry(object archiveOrFileSystem, IFileEntry entry) {
            mStreamGen = new StreamGenerator(archiveOrFileSystem, entry);

            Attribs = new FileAttribs(entry);
        }
    }
}
