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

using CommonUtil;
using DiskArc;
using static DiskArc.Defs;

namespace DiskArc.Multi {
    /// <summary>
    /// Definition of a single partition.
    /// </summary>
    /// <remarks>
    /// <para>Any additional details, such as partition names and type information, can be
    /// stored in a sub-class.  Bare-bones formats can just use this.</para>
    /// <para>Partitions must not overlap with other partitions, but the definition of
    /// "overlap" is somewhat flexible.  For example, on 800KB OzDOS disks, one 400KB DOS volume
    /// uses the top half of every 512-byte block, while the other volume uses the bottom half.  So
    /// the range of blocks overlaps, but they don't actually interact with the same data.  This
    /// means that a partition cannot be uniquely identified by start+length.</para>
    /// <para>Partitions are expected to hold filesystems, not disk images or multi-partition
    /// formats.</para>
    /// </remarks>
    public class Partition : IDisposable {
        /// <summary>
        /// Byte offset of start of partition.  Must be a multiple of the block size.
        /// </summary>
        public long StartOffset { get; protected set; }

        /// <summary>
        /// Length of partition, in bytes.  Must be a multiple of the block size.
        /// </summary>
        public long Length { get; protected set; }

        /// <summary>
        /// Chunk access object for the partition.  This allows access to all blocks and sectors
        /// within the partition, and none of the data outside it.
        /// </summary>
        /// <remarks>
        /// <para>This will be switched to read-only if <see cref="AnalayzePartition"/> is able
        /// to identify the filesystem.</para>
        /// <para>The object will be a "sub-chunk" accessor, pulling data from another chunk access
        /// object rather than the disk stream itself.</para>
        /// </remarks>
        public GatedChunkAccess ChunkAccess { get; protected set; }

        /// <summary>
        /// Filesystem found in the partition, set by <see cref="AnalyzePartition"/>.
        /// </summary>
        public IFileSystem? FileSystem { get; protected set; }

        /// <summary>
        /// Is re-analysis okay?
        /// </summary>
        /// <remarks>
        /// <para>We want to block this for embedded filesystems, especially DOS hybrids where
        /// ProDOS/Pascal have total overlap, because when we re-scan we might find DOS and just
        /// stop looking.</para>
        /// </remarks>
        private bool mIsReanalysisOkay;

        private ChunkSubset mPartChunks;
        private AppHook mAppHook;


        /// <summary>
        /// Constructor for a partition that is defined by a chunk subset.
        /// </summary>
        /// <remarks>
        /// <para>Re-analysis may yield different results, because the routines that call here
        /// are used for embedded/hybrid volumes.  If this is being used to define the "inner"
        /// volume of a hybrid disk, re-analysis might find the "outer" one.  For DOS Master
        /// embeds we might be giving the "is it DOS" test more leniency.  The only reliable
        /// way to reanalyze these is to reconsider the entire embedded volume set, because
        /// that guarantees that the results will match what happens the next time we open
        /// the file.</para>
        /// <para>We might consider recording the filesystem type, and only test for the same
        /// type during reanalysis, but that ignores the possibility that the contents have
        /// actually changed.</para>
        /// </remarks>
        /// <param name="partChunks">Chunk access object for partition contents.</param>
        /// <param name="fileSystem">Filesystem found on partition.</param>
        /// <param name="appHook">Application hook reference.</param>
        internal Partition(ChunkSubset partChunks, IFileSystem fileSystem, AppHook appHook) {
            mPartChunks = partChunks;
            ChunkAccess = new GatedChunkAccess(partChunks);
            FileSystem = fileSystem;
            mAppHook = appHook;

            // Set these so that we have something to display.  They're not actually useful.
            StartOffset = partChunks.StartBlock * BLOCK_SIZE;
            Length = partChunks.FormattedLength;
            mIsReanalysisOkay = false;
        }

        /// <summary>
        /// Constructor for a new partition that is a subset of blocks in the base chunk accessor.
        /// </summary>
        /// <param name="baseAccess">Base chunk access object (e.g. the full hard drive).</param>
        /// <param name="startOffset">Start offset, in bytes, of partition.</param>
        /// <param name="length">Length, in bytes, of partition.</param>
        /// <param name="appHook">Application hook reference.</param>
        internal Partition(IChunkAccess baseAccess, long startOffset, long length,
                AppHook appHook) {
            if (startOffset % BLOCK_SIZE != 0 || length % BLOCK_SIZE != 0) {
                throw new ArgumentException("Offset or length not multiple of block size");
            }

            // No need for additional analysis, just set this up now.
            ChunkSubset partChunks = ChunkSubset.CreateBlockSubset(baseAccess,
                (uint)(startOffset / BLOCK_SIZE), (uint)(length / BLOCK_SIZE));
            mPartChunks = partChunks;
            ChunkAccess = new GatedChunkAccess(partChunks);
            StartOffset = startOffset;
            Length = length;
            mAppHook = appHook;
            mIsReanalysisOkay = true;
        }

        ~Partition() {
            Dispose(false);     // cleanup check
        }
        public void Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        protected virtual void Dispose(bool disposing) {
            ChunkAccess.AccessLevel = GatedChunkAccess.AccessLvl.Closed;
            if (disposing) {
                FileSystem?.Dispose();
                FileSystem = null;
            } else {
                mAppHook.LogW("GC disposing of partition; has " +
                    (FileSystem == null ? "" : "non-") + "null FS");
                Debug.Assert(false, "GC disposing Partition, created:\r\n" + mCreationStackTrace);
            }
        }
#if DEBUG
        private string mCreationStackTrace = Environment.StackTrace + Environment.NewLine + "---";
#else
        private string mCreationStackTrace = "  (stack trace not enabled)";
#endif

        /// <summary>
        /// Analyzes the filesystem in the partition.  Configures FileSystem and ChunkAccess.
        /// </summary>
        public virtual void AnalyzePartition() {
            if (!mIsReanalysisOkay) {
                Debug.WriteLine("Reanalysis not allowed");
                return;
            }
            CloseContents();
            // leave the ChunkAccess alone

            // (Re-)analyze the filesystem.  Use the non-gated version of the chunk source.
            // No need to probe for the file order for an embedded volume.
            FileAnalyzer.AnalyzeFileSystem(mPartChunks, false, mAppHook,
                    out IDiskContents? contents);
            if (contents == null) {
                ChunkAccess.AccessLevel = GatedChunkAccess.AccessLvl.Open;
            } else if (contents is IFileSystem) {
                // Allow read-only access.  The chunk access object is shared with the filesystem.
                ChunkAccess.AccessLevel = GatedChunkAccess.AccessLvl.ReadOnly;
                FileSystem = (IFileSystem)contents;
            } else {
                // Currently ignoring the possibility of partitions-within-partitions, unless
                // they're embedded in a filesystem.
                throw new NotImplementedException("Found " + contents);
            }
        }

        /// <summary>
        /// Closes the IFileSystem object referenced by <see cref="FileSystem"/>.
        /// The property will be set to null.
        /// </summary>
        /// <remarks>
        /// <para>This has no effect if Contents is already null.</para>
        /// </remarks>
        public virtual void CloseContents() {
            if (FileSystem != null) {
                FileSystem.Dispose();
                FileSystem = null;
            }
            ChunkAccess.AccessLevel = GatedChunkAccess.AccessLvl.Open;
        }
    }
}
