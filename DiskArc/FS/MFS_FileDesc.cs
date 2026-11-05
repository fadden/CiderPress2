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

using CommonUtil;
using static DiskArc.Defs;
using static DiskArc.IFileSystem;

namespace DiskArc.FS {
    public class MFS_FileDesc : DiskFileStream {
        // Stream
        public override bool CanRead { get { return FileEntry != null; } }
        public override bool CanSeek { get { return FileEntry != null; } }
        public override bool CanWrite { get { return FileEntry != null && !mIsReadOnly; } }

        public override long Length => mEOF;

        public override long Position {
            get { return mMark; }
            set { Seek(value, SeekOrigin.Begin); }
        }

        // DiskFileStream
        public override FilePart Part { get; }

        internal MFS FileSystem { get; private set; }
        internal MFS_FileEntry FileEntry { get; private set; }

        private string mDebugPathName { get; set; }
        private bool mIsReadOnly;                       // is writing disallowed?
        private bool mInternalOpen;                     // skip informing filesystem of close?
        private ushort mStartBlock;

        /// <summary>
        /// Current file length.
        /// The value may change if the file is modified.
        /// </summary>
        private int mEOF;

        /// <summary>
        /// Current file position.  May extend past EOF.
        /// </summary>
        private int mMark;

        // General-purpose temporary disk buffer, to reduce allocations.
        private byte[] mTmpBlockBuf = new byte[BLOCK_SIZE];

        /// <summary>
        /// List of allocation blocks associated with this file.
        /// </summary>
        private List<ushort> mBlockList = new List<ushort>();


        /// <summary>
        /// Private constructor.
        /// </summary>
        private MFS_FileDesc(MFS_FileEntry entry, FileAccessMode mode, FilePart part,
                bool internalOpen) {
            FileSystem = entry.FileSystem;
            FileEntry = entry;
            Part = part;
            mInternalOpen = internalOpen;
            mIsReadOnly = (mode == FileAccessMode.ReadOnly);

            mDebugPathName = entry.FullPathName;        // latch name when file opened
        }

        /// <summary>
        /// Creates a file descriptor for the specified file entry.
        /// </summary>
        /// <remarks>
        /// <para>This is an internal method.  The caller is expected to validate the mode/part
        /// arguments, and ensure that this part of the file is not already open.</para>
        /// </remarks>
        /// <param name="entry">File entry to open.</param>
        /// <param name="mode">Access mode.</param>
        /// <param name="part">File part.</param>
        /// <param name="internalOpen">True if this is an "internal" open, which means it's
        ///   not being tracked by the filesystem object.</param>
        /// <returns>File descriptor object.</returns>
        /// <exception cref="IOException">Disk access failure, or corrupted file
        ///   structure.</exception>
        internal static MFS_FileDesc CreateFD(MFS_FileEntry entry, FileAccessMode mode,
                FilePart part, bool internalOpen) {
            Debug.Assert(!entry.IsDamaged);

            MFS_FileDesc newFd = new MFS_FileDesc(entry, mode, part, internalOpen);
            if (part == FilePart.DataFork || part == FilePart.RawData) {
                newFd.mEOF = (int)entry.DataLogicalLen;
                newFd.mStartBlock = entry.DataStartBlock;
            } else if (part == FilePart.RsrcFork) {
                newFd.mEOF = (int)entry.RsrcLogicalLen;
                newFd.mStartBlock = entry.RsrcStartBlock;
            } else {
                throw new ArgumentException("bad part " + part);
            }

            // Walk through the list of sectors to gather the full list.  This will throw an
            // exception if something looks wrong.
            newFd.GenerateBlockList();

            return newFd;
        }

        /// <summary>
        /// Generates the list of allocation blocks that make up this fork of the file.
        /// </summary>
        internal void GenerateBlockList() {
            Debug.Assert(mBlockList.Count == 0);
            MFS_MDB mdb = FileSystem.VolMDB!;
            ushort ablk = mStartBlock;
            if (ablk == 0) {
                // Empty fork.
                return;
            }

            int iterations = 0;
            while (iterations++ < MFS_MDB.MAX_ALLOC_BLOCKS) {
                mBlockList.Add(ablk);
                ushort nextAblk = mdb.GetAllocMapEntry(ablk);
                if (nextAblk == 0 || nextAblk >= mdb.NumAllocBlocks + MFS_MDB.RESERVED_BLOCKS) {
                    FileSystem.Notes.AddW("Bad block link at " + ablk + " to " + nextAblk + ": '" +
                        mDebugPathName + "'");
                    FileEntry.HasBadLink = true;
                    break;
                }
                if (nextAblk == 1) {
                    break;      // end of file
                }
                ablk = nextAblk;
            }
            if (iterations == MFS_MDB.MAX_ALLOC_BLOCKS) {
                FileSystem.Notes.AddW("Circular block chain in '" + mDebugPathName + "'");
                FileEntry.IsDamaged = true;
            }
        }

        /// <summary>
        /// Sets the volume usage for the blocks associated with this file.  Call this during
        /// the initial disk scan.
        /// </summary>
        internal void SetVolumeUsage() {
            VolumeUsage vu = FileSystem.VolMDB!.VolUsage!;
            foreach (uint ablk in mBlockList) {
                vu.SetUsage(ablk - MFS_MDB.RESERVED_BLOCKS, FileEntry);
            }
        }

        /// <summary>
        /// Gets the number of blocks used by the file.
        /// </summary>
        internal int GetBlocksUsed() {
            return mBlockList.Count;
        }

        private uint GetLogicalBlockNum(uint logicalIndex) {
            MFS_MDB mdb = FileSystem.VolMDB!;
            uint abIndex = logicalIndex / mdb.LogicalPerAllocBlock;
            uint abOffset = logicalIndex % mdb.LogicalPerAllocBlock;

            // Convert index into an absolute allocation block number.
            Debug.Assert(abIndex >= 0 && abIndex < mBlockList.Count);
            uint ablk = mBlockList[(int)abIndex];

            // Convert ABN to a logical block number.
            uint logicalBlock = FileSystem.AllocBlockToLogiBlock(ablk);
            return logicalBlock + abOffset;
        }

        // Stream
        public override int Read(byte[] readBuf, int readOffset, int count) {
            CheckValid();
            if (readOffset < 0) {
                throw new ArgumentOutOfRangeException(nameof(readOffset), readOffset, "bad offset");
            }
            if (count < 0) {
                throw new ArgumentOutOfRangeException(nameof(count), count, "bad count");
            }
            if (readBuf == null) {
                throw new ArgumentNullException(nameof(readBuf));
            }
            if (count == 0 || mMark >= mEOF) {
                return 0;
            }
            if (readOffset >= readBuf.Length || count > readBuf.Length - readOffset) {
                throw new ArgumentException("Buffer overrun");
            }

            // Trim request to fit file bounds.
            if ((long)mMark + count > mEOF) {
                count = mEOF - mMark;
            }
            if ((long)readOffset + count > readBuf.Length) {
                throw new ArgumentOutOfRangeException("Buffer overflow");
            }
            int actual = count;    // we read it all, or fail

            // Calculate block index from file position.
            int blockIndex = mMark / BLOCK_SIZE;
            int blockOffset = mMark % BLOCK_SIZE;
            while (count > 0) {
                uint blockNum = GetLogicalBlockNum((uint)blockIndex);
                FileSystem.ChunkAccess.ReadBlock(blockNum, mTmpBlockBuf, 0);

                // Copy everything we need out of this block.
                int bufLen = BLOCK_SIZE - blockOffset;
                if (bufLen > count) {
                    bufLen = count;
                }
                Array.Copy(mTmpBlockBuf, blockOffset, readBuf, readOffset, bufLen);

                readOffset += bufLen;
                count -= bufLen;
                mMark += bufLen;

                blockIndex++;
                blockOffset = 0;
            }

            return actual;
        }

        // Stream
        public override void Write(byte[] buffer, int offset, int count) {
            CheckValid();
            if (mIsReadOnly) {
                throw new NotSupportedException("File was opened read-only");
            }
            if (offset < 0) {
                throw new ArgumentOutOfRangeException(nameof(offset), offset, "bad offset");
            }
            if (count < 0) {
                throw new ArgumentOutOfRangeException(nameof(count), count, "bad count");
            }
            if (buffer == null) {
                throw new ArgumentNullException(nameof(buffer));
            }
            if (count == 0) {
                return;
            }
            if (offset >= buffer.Length || count > buffer.Length - offset) {
                throw new ArgumentException("Buffer overrun");
            }
            if ((long)mMark + count > MFS.MAX_FILE_LEN) {
                throw new IOException("Write would exceed max file size (mark=" +
                    mMark + " len=" + count + ")");
            }

            MFS_MDB mdb = FileSystem.VolMDB!;
            uint logPerAlloc = mdb.LogicalPerAllocBlock;

            while (count > 0) {
                int logicalIndex = mMark / BLOCK_SIZE;      // 512-byte block index within fork
                int blockOffset = mMark % BLOCK_SIZE;
                int abIndex = (int)(logicalIndex / logPerAlloc);

                // Make sure the fork has enough allocation blocks to hold this position.
                EnsureAllocated(abIndex);

                uint blockNum = GetLogicalBlockNum((uint)logicalIndex);

                byte[] writeSource;
                int writeSourceOff;
                int writeLen;
                if (blockOffset != 0 || count < BLOCK_SIZE) {
                    // Partial write: read-modify-write the logical block.
                    FileSystem.ChunkAccess.ReadBlock(blockNum, mTmpBlockBuf, 0);
                    writeLen = BLOCK_SIZE - blockOffset;
                    if (writeLen > count) {
                        writeLen = count;
                    }
                    Array.Copy(buffer, offset, mTmpBlockBuf, blockOffset, writeLen);
                    writeSource = mTmpBlockBuf;
                    writeSourceOff = 0;
                } else {
                    // Full-block write straight from the caller's buffer.
                    writeSource = buffer;
                    writeSourceOff = offset;
                    writeLen = BLOCK_SIZE;
                }

                FileSystem.ChunkAccess.WriteBlock(blockNum, writeSource, writeSourceOff);

                mMark += writeLen;
                offset += writeLen;
                count -= writeLen;
                if (mMark > mEOF) {
                    mEOF = mMark;
                }
            }
        }

        /// <summary>
        /// Ensures the fork's block chain contains at least (abIndex + 1) allocation blocks,
        /// allocating and linking new blocks as needed.
        /// </summary>
        /// <exception cref="DiskFullException">No free allocation blocks remain.</exception>
        private void EnsureAllocated(int abIndex) {
            MFS_MDB mdb = FileSystem.VolMDB!;
            while (mBlockList.Count <= abIndex) {
                ushort newAblk = mdb.AllocBlock(FileEntry);     // marked as end-of-chain
                if (mBlockList.Count == 0) {
                    mStartBlock = newAblk;      // first block of this fork
                } else {
                    // Link the previous last block to the new one.
                    mdb.SetAllocMapEntry(mBlockList[mBlockList.Count - 1], newAblk);
                }
                mBlockList.Add(newAblk);
            }
        }

        // Stream
        public override long Seek(long seekOff, SeekOrigin origin) {
            CheckValid();
            if (seekOff < -MFS.MAX_FILE_LEN || seekOff > MFS.MAX_FILE_LEN) {
                throw new ArgumentOutOfRangeException(nameof(seekOff), seekOff, "invalid offset");
            }

            long newPos;
            switch (origin) {
                case SeekOrigin.Begin:
                    newPos = seekOff;
                    break;
                case SeekOrigin.Current:
                    newPos = mMark + seekOff;
                    break;
                case SeekOrigin.End:
                    newPos = mEOF + seekOff;
                    break;
                case SEEK_ORIGIN_DATA:
                    newPos = seekOff;       // no holes
                    break;
                case SEEK_ORIGIN_HOLE:
                    newPos = mEOF;          // no holes
                    break;
                default:
                    throw new ArgumentException("Invalid seek mode");
            }

            if (newPos < 0 || newPos > MFS.MAX_FILE_LEN) {
                throw new IOException("Invalid seek offset " + newPos);
            }
            mMark = (int)newPos;
            return newPos;
        }

        // Stream
        public override void SetLength(long newEof) {
            CheckValid();
            if (mIsReadOnly) {
                throw new NotSupportedException("File was opened read-only");
            }
            if (newEof < 0 || newEof > MFS.MAX_FILE_LEN) {
                throw new ArgumentOutOfRangeException(nameof(newEof), newEof, "Invalid length");
            }
            if (newEof == mEOF) {
                return;
            }

            if (newEof > mEOF) {
                // Grow, zero-filling the new storage via the normal Write path.
                int origEOF = mEOF;
                try {
                    FSUtil.ExtendFile(this, newEof, (int)FileSystem.VolMDB!.AllocBlockSize);
                } catch {
                    // Probably a DiskFullException.  Roll back the growth and re-throw.
                    mEOF = origEOF;
                    TruncateStorage();
                    throw;
                }
            } else {
                // Shrink: drop the EOF, then release any now-unused allocation blocks.
                mEOF = (int)newEof;
                TruncateStorage();
            }

            mEOF = (int)newEof;
            Flush();
        }

        /// <summary>
        /// Releases allocation blocks that are no longer needed to hold the current EOF, and
        /// fixes up the chain terminator (or start block, if the fork is now empty).
        /// </summary>
        private void TruncateStorage() {
            MFS_MDB mdb = FileSystem.VolMDB!;
            uint allocBlockSize = mdb.AllocBlockSize;
            int needed = (mEOF == 0) ? 0 :
                (int)(((long)mEOF + allocBlockSize - 1) / allocBlockSize);
            while (mBlockList.Count > needed) {
                ushort ablk = mBlockList[mBlockList.Count - 1];
                mdb.FreeBlock(ablk);
                mBlockList.RemoveAt(mBlockList.Count - 1);
            }
            if (mBlockList.Count == 0) {
                mStartBlock = 0;
            } else {
                // The new last block terminates the chain.
                mdb.SetAllocMapEntry(mBlockList[mBlockList.Count - 1], 1);
            }
        }

        // Stream
        public override void Flush() {
            CheckValid();
            if (mIsReadOnly) {
                return;
            }
            // Storage (block chain) is updated eagerly during writes; reconcile the entry's
            // fork geometry here and persist the directory.
            MFS_MDB mdb = FileSystem.VolMDB!;
            uint physicalLen = (uint)mBlockList.Count * mdb.AllocBlockSize;
            ushort startBlock = (mBlockList.Count == 0) ? (ushort)0 : mBlockList[0];
            FileEntry.UpdateFork(Part, startBlock, (uint)mEOF, physicalLen);
            FileEntry.SaveChanges();
        }

        // IDisposable generic finalizer.
        ~MFS_FileDesc() {
            Dispose(false);
        }
        protected override void Dispose(bool disposing) {
            if (disposing) {
                if (!mInternalOpen) {
                    try {
                        // Flush pending changes to the file entry and directory.
                        if (!mIsReadOnly) {
                            Flush();
                        }
                    } catch (Exception ex) {
                        Debug.WriteLine("FileDesc dispose-time flush failed: " + ex.Message);
                    }
                    try {
                        // Tell the OS to forget about us.
                        FileSystem.CloseFile(this);
                    } catch (Exception ex) {
                        Debug.WriteLine("FileSystem.CloseFile from fd close failed: " + ex.Message);
                    }
                }
            } else {
                // Finalizer dispose.
                if (FileEntry == null) {
                    Debug.Assert(false, "Finalization didn't get suppressed?");
                } else {
                    Debug.WriteLine("NOTE: GC disposing of file desc object " + this);
                }
            }

            // Mark the fd invalid so future calls will crash or do nothing.
            Invalidate();
            base.Dispose(disposing);
        }

        /// <summary>
        /// Marks the file descriptor invalid, clearing various fields to ensure something bad
        /// will happen if we try to use it.
        /// </summary>
        private void Invalidate() {
#pragma warning disable CS8625
            FileSystem = null;
            FileEntry = null;
#pragma warning restore CS8625
        }

        /// <summary>
        /// Throws an exception if the file descriptor has been invalidated.
        /// </summary>
        private void CheckValid() {
            if (FileEntry == null || !FileEntry.IsValid) {
                throw new ObjectDisposedException("File descriptor has been closed (" +
                    mDebugPathName + ")");
            }
        }

        // DiskFileStream
        public override bool DebugValidate(IFileSystem fs, IFileEntry entry) {
            Debug.Assert(entry != null && entry != IFileEntry.NO_ENTRY);
            if (FileSystem == null || FileEntry == null) {
                return false;       // we're invalid
            }
            return (fs == FileSystem && entry == FileEntry);
        }

        public override string ToString() {
            return "[MFS_FileDesc '" + mDebugPathName +
                (FileEntry == null ? " *INVALID*" : "") + "']";
        }
    }
}
