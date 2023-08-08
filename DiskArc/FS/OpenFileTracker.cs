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

namespace DiskArc.FS {
    /// <summary>
    /// Common class for keeping track of open files.
    /// </summary>
    internal class OpenFileTracker {
        /// <summary>
        /// One of these per open file.
        /// </summary>
        private class OpenFileRec {
            // File entry associated with the open descriptor.
            public IFileEntry FileEntry { get; private set; }

            // File descriptor.
            public DiskFileStream FileDesc { get; private set; }

            public OpenFileRec(IFileSystem fs, IFileEntry entry, DiskFileStream desc) {
                if (!desc.DebugValidate(fs, entry)) {
                    throw new DAException("OpenFileRec descriptor validation failed");
                }
                FileEntry = entry;
                FileDesc = desc;
            }

            public override string ToString() {
                return "[Open file: '" + FileEntry.FullPathName + "' part=" +
                    FileDesc.Part + " rw=" + FileDesc.CanWrite + "]";
            }
        }

        /// <summary>
        /// List of open files.
        /// </summary>
        private List<OpenFileRec> mOpenFiles = new List<OpenFileRec>();

        /// <summary>
        /// Returns a count of the number of open files.
        /// </summary>
        public int Count => mOpenFiles.Count;

        public OpenFileTracker() { }

        /// <summary>
        /// Determines whether we can open this file/part.  If the caller is requesting write
        /// access, we can't allow it if the file is already opened read-write.
        /// </summary>
        /// <remarks>
        /// This is also testing whether we're allowed to delete the file.  That is processed
        /// as a write operation on an ambiguous part.  If any part of the file is open, the
        /// call will be rejected.
        /// </remarks>
        /// <param name="entry">File to check.</param>
        /// <param name="wantWrite">True if we're going to modify the file.</param>
        /// <param name="part">File part.  Pass "Unknown" to match on any part.</param>
        /// <returns>True if all is well (no conflict).</returns>
        public bool CheckOpenConflict(IFileEntry entry, bool wantWrite, FilePart part) {
            foreach (OpenFileRec rec in mOpenFiles) {
                if (rec.FileEntry != entry) {
                    continue;
                }
                if (part != FilePart.Unknown && rec.FileDesc.Part != part) {
                    // This file is open, but we're only interested in a specific part, and
                    // the part that's open isn't this one.
                    continue;
                }
                if (wantWrite) {
                    // We need exclusive access to this part.
                    return false;
                } else {
                    // We're okay if the existing open is read-only.
                    if (rec.FileDesc.CanWrite) {
                        return false;
                    }
                    // There may be even more open instances, but they're all read-only.
                    return true;
                }
            }
            return true;        // file is not open at all
        }

        /// <summary>
        /// Adds a new entry to the list of open files.  Does not check for conflicts.
        /// </summary>
        public void Add(IFileSystem fs, IFileEntry entry, DiskFileStream fd) {
            mOpenFiles.Add(new OpenFileRec(fs, entry, fd));
        }

        /// <summary>
        /// Flushes the contents and attributes of all open files.
        /// </summary>
        public void FlushAll() {
            foreach (OpenFileRec rec in mOpenFiles) {
                rec.FileDesc.Flush();
                rec.FileEntry.SaveChanges();
            }
        }

        /// <summary>
        /// Removes an entry from the list of open files.  Called when a descriptor is closed.
        /// </summary>
        /// <param name="ifd">File descriptor that is being closed.</param>
        /// <returns>True if the descriptor was found, false if not.</returns>
        public bool RemoveDescriptor(DiskFileStream ifd) {
            foreach (OpenFileRec rec in mOpenFiles) {
                if (rec.FileDesc == ifd) {
                    mOpenFiles.Remove(rec);
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Closes all open files.
        /// </summary>
        public void CloseAll() {
            // Walk through from end to start so we don't trip when entries are removed.
            for (int i = mOpenFiles.Count - 1; i >= 0; --i) {
                try {
                    mOpenFiles[i].FileDesc.Close();
                } catch (IOException ex) {
                    // This could happen if Close flushed a change that wrote to a bad block,
                    // e.g. a new index block in a tree file.
                    Debug.WriteLine("Caught IOException during CloseAll: " + ex.Message);
                } catch (Exception ex) {
                    // Unexpected.  Discard exception so cleanup can continue.
                    Debug.WriteLine("Unexpected exception in CloseAll: " + ex.Message);
                    Debug.Assert(false);
                }
            }
            Debug.Assert(mOpenFiles.Count == 0);
        }
    }
}
