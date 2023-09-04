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

using static DiskArc.Defs;

namespace DiskArc.Arc {
    /// <summary>
    /// Common class for keeping track of open files in an IArchive instance.
    /// </summary>
    internal class OpenStreamTracker {
        private class OpenStreamRec {
            // File entry associated with the open stream.
            public IFileEntry FileEntry { get; private set; }

            // Open stream (read-only, non-seekable).
            public ArcReadStream ReadStream { get; private set; }

            public OpenStreamRec(IFileEntry entry, ArcReadStream stream) {
                FileEntry = entry;
                ReadStream = stream;
            }

            public override string ToString() {
                return "[Open stream: '" + FileEntry.FullPathName + "']";
            }
        }

        private List<OpenStreamRec> mOpenStreams = new List<OpenStreamRec>();

        public int Count => mOpenStreams.Count;


        public OpenStreamTracker() { }

        /// <summary>
        /// Adds a new entry to the list of open streams.  Does not check for conflicts.
        /// </summary>
        /// <param name="entry">Entry to add.</param>
        /// <param name="stream">Open stream.</param>
        public void Add(IFileEntry entry, ArcReadStream stream) {
            mOpenStreams.Add(new OpenStreamRec(entry, stream));
        }

        /// <summary>
        /// Removes an entry from the list of open streams.  Called when a stream is closed.
        /// </summary>
        /// <param name="stream">Stream that is being closed.</param>
        /// <returns>True if the stream was found, false if not.</returns>
        public bool RemoveDescriptor(ArcReadStream stream) {
            // Start looking at the end, because if we're doing a Dispose-time mass close
            // we'll be doing it in that order.
            for (int i = mOpenStreams.Count - 1; i >= 0; --i) {
                if (mOpenStreams[i].ReadStream == stream) {
                    mOpenStreams.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Closes all open streams.
        /// </summary>
        public void CloseAll() {
            // Walk through from end to start so we don't trip when entries are removed.
            for (int i = mOpenStreams.Count - 1; i >= 0; --i) {
                // This will call back into our StreamClosing function, which will
                // remove it from the list.
                mOpenStreams[i].ReadStream.Close();
            }
            Debug.Assert(mOpenStreams.Count == 0);
        }
    }
}
