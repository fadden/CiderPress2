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
using System.Diagnostics;
using System.Text;

using CommonUtil;

namespace DiskArc {
    /// <summary>
    /// Track usage of disk volume allocation units.
    /// </summary>
    /// <remarks>
    /// <para>The goal is to provide a simple linear map of sectors, blocks, or whatever
    /// allocation unit the filesystem uses.  How those map to physical track/sector is up to
    /// the caller.</para>
    ///
    /// <para>This tracks the used/free state and the usage (i.e. which file is using the chunk)
    /// as parallel entities.</para>
    ///
    /// <para>This object may be provided to the application, so all of the methods that
    /// modify data structures should be internal-only.</para>
    /// </remarks>
    public class VolumeUsage {
        internal const string SYSTEM_STR = "<system>";

        /// <summary>
        /// Chunk usage.
        /// </summary>
        /// <remarks>
        /// We have one entry per chunk, but none of our filesystems allow more than 65536
        /// allocation units, so minimizing size is useful but not crucial.
        /// </remarks>
        [Flags]
        public enum UsageFlags : byte {
            MarkedInUse = 0x01,             // chunk marked in-use by filesystem
            Unreadable = 0x02,              // nibble images only: cannot read entire chunk
            Conflict = 0x04,                // more than one file has claimed this block

            // TODO(maybe): could have a file structure vs. file data flag; only useful if
            //   we're trying to make fancy visual displays
        };

        /// <summary>
        /// Usage of a single chunk.
        /// </summary>
        /// <remarks>
        /// This is a struct instead of a class to make the array more compact.
        /// </remarks>
        private struct ChunkUsage {
            /// <summary>
            /// Flags indicating the chunk's state.  Unused free blocks will be zero.
            /// </summary>
            public UsageFlags mFlags;

            /// <summary>
            /// Reference to the file that this block is assigned to, NO_ENTRY for system
            /// blocks, or null if unassigned.
            /// </summary>
            public IFileEntry? mFileEntry;

            public bool IsMarkedUsed { get { return (mFlags & UsageFlags.MarkedInUse) != 0; } }
            public bool HasConflict { get { return (mFlags & UsageFlags.Conflict) != 0; } }

            public bool HasUsage { get { return mFileEntry != null; } }
            public bool IsUsedBySystem { get { return mFileEntry == IFileEntry.NO_ENTRY; } }
            public bool IsUsedByDirectory {
                get {
                    return mFileEntry != null && mFileEntry != IFileEntry.NO_ENTRY &&
                        mFileEntry.IsDirectory;
                }
            }

            public override string ToString() {
                string fileUsage;
                if (mFileEntry == null) {
                    fileUsage = "<none>";
                } else if (mFileEntry == IFileEntry.NO_ENTRY) {
                    fileUsage = SYSTEM_STR;
                } else {
                    fileUsage = mFileEntry.FileName;
                }
                return "[ChunkUsage: inUse=" + IsMarkedUsed + " hasCfl=" + HasConflict +
                    " file=" + fileUsage + "]";
            }
        }

        /// <summary>
        /// Array of chunks.
        /// </summary>
        private ChunkUsage[] mChunks;


        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="numChunks">Number of discrete allocation chunks.</param>
        public VolumeUsage(int numChunks) {
            mChunks = new ChunkUsage[numChunks];
        }

        /// <summary>
        /// Returns the number of chunks in the usage map.
        /// </summary>
        public int Count { get { return mChunks.Length; } }

        /// <summary>
        /// Sets the usage for a chunk during a volume scan.  If the chunk is already
        /// marked as other than "unused", the Conflict flag will be raised.  Does not alter
        /// the in-use flag.
        /// </summary>
        /// <param name="chunk">Chunk number.</param>
        /// <param name="entry">File entry for chunk user, or null if the block is unused.</param>
        /// <param name="hasConflict">Value set to true if a conflict is found.</param>
        internal void SetUsage(uint chunk, IFileEntry? entry) {
            IFileEntry? cflct = mChunks[chunk].mFileEntry;
            if (entry != null && cflct != null) {
                // Chunk isn't empty, and we're not clearing it; this is bad.
                mChunks[chunk].mFlags |= UsageFlags.Conflict;

                IFileEntryExt? ext1 = entry as IFileEntryExt;
                IFileEntryExt? ext2 = cflct as IFileEntryExt;
                if (ext1 != null && ext1 == ext2) {
                    // This can happen on damaged disks, e.g. ProDOS directory loops.
                    //Debug.WriteLine("file conflicts with itself: " + ext1);
                }
                if (ext1 != null) {
                    ext1.AddConflict(chunk, cflct);
                }
                if (ext2 != null) {
                    ext2.AddConflict(chunk, entry);
                }
                if (entry == IFileEntry.NO_ENTRY && cflct == IFileEntry.NO_ENTRY) {
                    // This could be a bug in the filesystem code, or a circular loop in a volume
                    // directory traversal.
                    Debug.WriteLine("System conflicting with self: chunk=" + chunk);
                } else if (ext1 == null && ext2 == null) {
                    // Nobody to report problems to.  This is fine.
                    Debug.WriteLine("Nobody to report conflict to; chunk=" + chunk);
                }
            } else {
                mChunks[chunk].mFileEntry = entry;
            }
        }

        /// <summary>
        /// Gets the file usage for a chunk.
        /// </summary>
        /// <param name="chunk">Chunk number.</param>
        /// <returns>File that "owns" the block, NO_ENTRY if the system owns it, or null
        ///   if the chunk is not claimed.</returns>
        public IFileEntry? GetUsage(uint chunk) {
            return mChunks[chunk].mFileEntry;
        }

        /// <summary>
        /// Gets the in-use status of a chunk.
        /// </summary>
        /// <param name="chunk">Chunk number.</param>
        /// <returns>True if chunk is marked as being in use.</returns>
        public bool GetInUse(uint chunk) {
            return mChunks[chunk].IsMarkedUsed;
        }

        /// <summary>
        /// Marks a chunk as "in use" during a volume scan.  Does not alter the file entry.
        /// </summary>
        /// <param name="chunk"></param>
        internal void MarkInUse(uint chunk) {
            mChunks[chunk].mFlags |= UsageFlags.MarkedInUse;
        }

        /// <summary>
        /// Marks a chunk as in-use and associated with a particular file.
        /// </summary>
        /// <param name="chunk">Chunk number.</param>
        /// <param name="entry">File entry.</param>
        internal void AllocChunk(uint chunk, IFileEntry entry) {
            Debug.Assert(mChunks[chunk].mFileEntry == null);

            // Allocating an allocated chunk indicates problems upstream.
            if ((mChunks[chunk].mFlags & UsageFlags.MarkedInUse) != 0) {
                Debug.WriteLine("Chunk allocated twice");
            }

            mChunks[chunk].mFlags |= UsageFlags.MarkedInUse;
            mChunks[chunk].mFileEntry = entry;
        }

        /// <summary>
        /// Marks a chunk as no longer in use.  Clears the file usage.
        /// </summary>
        /// <param name="chunk">Chunk number.</param>
        internal void FreeChunk(uint chunk) {
            //Debug.Assert(mChunks[chunk].mFileEntry != null);

            mChunks[chunk].mFlags &= ~UsageFlags.MarkedInUse;
            mChunks[chunk].mFileEntry = null;
        }


        /// <summary>
        /// Generates chunk usage statistics.
        /// </summary>
        /// <param name="markedUsed">Number of blocks that are marked "in use".</param>
        /// <param name="unusedMarked">Number of blocks that are marked "in use", but have
        ///   no usage information.</param>
        /// <param name="notMarkedUsed">Number of blocks that have usage information, but are
        ///   not marked "in use" (dangerous).</param>
        /// <param name="conflicts">Number of blocks with conflicting usage information
        ///   (dangerous).</param>
        public void Analyze(out int markedUsed, out int unusedMarked, out int notMarkedUsed,
                out int conflicts) {
            markedUsed = 0;
            unusedMarked = 0;
            notMarkedUsed = 0;
            conflicts = 0;
            foreach (ChunkUsage usage in mChunks) {
                if (usage.IsMarkedUsed) {
                    // marked used...
                    markedUsed++;
                    if (!usage.HasUsage) {
                        // ...but no usage found
                        unusedMarked++;
                    }
                } else {
                    // not marked used...
                    if (usage.HasUsage) {
                        // ...but usage found
                        notMarkedUsed++;
                    }
                }
                if (usage.HasConflict) {
                    conflicts++;
                }
            }
        }

        /// <summary>
        /// Generates a set of ranges in which blocks are marked as used, but nothing appears
        /// to be using them.
        /// </summary>
        /// <returns></returns>
        public RangeSet GenerateNoUsageSet() {
            // It might be more efficient to define an iterator on this class and hand
            // that to the RangeSet constructor.  However, our data sets are small, and
            // there should be few regions.
            RangeSet set = new RangeSet();
            for (int i = 0; i < mChunks.Length; i++) {
                if (!mChunks[i].HasUsage && mChunks[i].IsMarkedUsed) {
                    set.Add(i);
                }
            }
            return set;
        }

        /// <summary>
        /// Convert a chunk state into a single, hopefully meaningful, character.  Used for
        /// the debug dump.
        /// </summary>
        private static char UsageToChar(ChunkUsage usage) {
            if (!usage.HasUsage) {
                if (usage.IsMarkedUsed) {
                    return 'X';         // marked in use, but not used... subvolume?
                } else {
                    return '.';         // unused
                }
            } else {
                if (!usage.IsMarkedUsed) {
                    return '!';         // block in use, but not marked as such; danger!
                }
                if (usage.HasConflict) {
                    return '#';         // multiple uses; danger!
                }
                if (usage.IsUsedBySystem) {
                    return 'S';         // boot blocks, allocation bitmap
                }
                if (usage.IsUsedByDirectory) {
                    return 'D';         // volume directory, subdirectory
                }

                // Assume file data.
                return 'F';
            }
        }

        internal string DebugDump() {
            const int LINE_WIDTH = 64;

            StringBuilder sb = new StringBuilder(mChunks.Length);
            int lineCount = LINE_WIDTH;
            foreach (ChunkUsage usage in mChunks) {
                sb.Append(UsageToChar(usage));
                if (--lineCount == 0) {
                    sb.AppendLine();
                    lineCount = LINE_WIDTH;
                }
            }

            return sb.ToString();
        }
    }
}
