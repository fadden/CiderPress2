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
    public class CPM_FileDesc : DiskFileStream {
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

        internal CPM FileSystem { get; private set; }
        internal CPM_FileEntry FileEntry { get; private set; }

        private string mDebugPathName { get; set; }
        private bool mIsReadOnly;                       // is writing disallowed?

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

        // Buffer full of 0xe5, for sparse reads.
        private static readonly byte[] sE5Block = GenerateE5();
        private static byte[] GenerateE5() {
            byte[] buf = new byte[BLOCK_SIZE];
            RawData.MemSet(buf, 0, BLOCK_SIZE, 0xe5);
            return buf;
        }


        /// <summary>
        /// Private constructor.
        /// </summary>
        private CPM_FileDesc(CPM_FileEntry entry, FileAccessMode mode, FilePart part) {
            FileSystem = entry.FileSystem;
            FileEntry = entry;
            Part = part;
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
        /// <exception cref="IOException">Disk access failure, or corrupted file
        ///   structure.</exception>
        /// <returns>File descriptor object.</returns>
        internal static CPM_FileDesc CreateFD(CPM_FileEntry entry, FileAccessMode mode,
                FilePart part) {
            Debug.Assert(!entry.IsDamaged);
            Debug.Assert(part == FilePart.DataFork);

            CPM_FileDesc newFd = new CPM_FileDesc(entry, mode, part);
            newFd.mEOF = (int)entry.DataLength;

            return newFd;
        }

        // Stream
        public override int Read(byte[] readBuf, int readOffset, int count) {
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
                uint blockNum = GetBlockNum(blockIndex, out int unused1, out int unused2,
                    out int unused3);
                byte[] data;
                if (blockNum == uint.MaxValue) {
                    // Sparse block, just copy 0xe5 (seems more appropriate than zeroes).
                    data = sE5Block;
                } else {
                    // Read data into temporary buffer.
                    data = mTmpBlockBuf;
                    FileSystem.ChunkAccess.ReadBlockCPM(blockNum, mTmpBlockBuf, 0);
                }

                // Copy everything we need out of this block.
                int bufLen = BLOCK_SIZE - blockOffset;
                if (bufLen > count) {
                    bufLen = count;
                }
                Array.Copy(data, blockOffset, readBuf, readOffset, bufLen);

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
                throw new ArgumentNullException("Buffer is null");
            }
            if (count == 0) {
                return;
            }
            if (offset >= buffer.Length || count > buffer.Length - offset) {
                throw new ArgumentException("Buffer overrun");
            }
            if (offset + count > buffer.Length) {
                throw new ArgumentOutOfRangeException("Buffer overflow");
            }
            Debug.Assert(mMark >= 0 && mMark <= CPM.MAX_FILE_LEN);
            if (mMark + count > CPM.MAX_FILE_LEN) {
                // We don't do partial writes, so we just throw if we're off the end.
                throw new IOException("Write would exceed max file size (mark=" +
                    mMark + " len=" + count + ")");
            }

            while (count > 0) {
                // Set block index according to file position.
                int blockIndex = mMark / BLOCK_SIZE;
                int blockOffset = mMark % BLOCK_SIZE;

                byte[] writeSource;
                int writeSourceOff;
                int writeLen;

                uint blockNum = GetBlockNum(blockIndex, out int extentNum, out int extentIndex,
                    out int subBlockIndex);
                if (blockNum == uint.MaxValue) {
                    // No allocation block has been assigned.  Allocate a new one and add it
                    // in the appropriate position in the appropriate extent.
                    blockNum = ExpandFile(extentNum, extentIndex, subBlockIndex);
                }
                if (blockOffset != 0 || count < BLOCK_SIZE) {
                    // Partial write to the start or end of a block.  Read the block and merge
                    // the contents.
                    FileSystem.ChunkAccess.ReadBlockCPM(blockNum, mTmpBlockBuf, 0);
                    writeLen = BLOCK_SIZE - blockOffset;
                    if (writeLen > count) {
                        writeLen = count;
                    }
                    Array.Copy(buffer, offset, mTmpBlockBuf, blockOffset, writeLen);
                    writeSource = mTmpBlockBuf;
                    writeSourceOff = 0;
                } else {
                    // Writing a full block, so just use the source data.
                    writeSource = buffer;
                    writeSourceOff = offset;
                    writeLen = BLOCK_SIZE;
                }

                // Finally, write the block to the disk.
                FileSystem.ChunkAccess.WriteBlockCPM(blockNum, writeSource, writeSourceOff);

                // Advance file position.
                mMark += writeLen;
                offset += writeLen;
                count -= writeLen;
                if (mMark > mEOF) {
                    mEOF = mMark;
                }
            }
        }

        /// <summary>
        /// Determines the disk block number of the Nth 512-byte block in the file.  Note that
        /// zero is a valid result for wrap-around volumes.
        /// </summary>
        /// <param name="blockIndex">Index into the file (512-byte blocks).</param>
        /// <param name="extentNum">Result: extent number that holds the block.</param>
        /// <param name="extentIndex">Result: index within extent (0-7 or 0-15).</param>
        /// <param name="subBlockIndex">Result: index of 512-byte block within alloc block
        ///   (0-1 or 0-3).</param>
        /// <returns>Disk block number, or uint.MaxValue if no block is allocated (sparse or off
        ///   the end of the file).</returns>
        /// <exception cref="IOException">Disk access failure.</exception>
        private uint GetBlockNum(int blockIndex, out int extentNum, out int extentIndex,
                out int subBlockIndex) {
            Debug.Assert(blockIndex >= 0);

            const int BLOCKS_PER_EXTENT = CPM.RECS_PER_EXTENT * CPM.FILE_REC_LEN / BLOCK_SIZE;
            Debug.Assert(BLOCKS_PER_EXTENT == 32);                      // expecting 16KB extents
            int blocksPerAlloc = (int)(FileSystem.AllocUnitSize / BLOCK_SIZE);
            Debug.Assert(blocksPerAlloc == 2 || blocksPerAlloc == 4);   // expecting 1KB or 2KB

            // Figure out which extent we're in.
            extentNum = blockIndex / BLOCKS_PER_EXTENT;
            // Figure out which allocation block entry we want.
            int blockRem = blockIndex % BLOCKS_PER_EXTENT;
            extentIndex = blockRem / blocksPerAlloc;
            // Identify the disk block within the allocation block.
            subBlockIndex = blockRem % blocksPerAlloc;         // 0-1 or 0-3

            // Find the appropriate extent.
            CPM_FileEntry.Extent ext = FileEntry.GetExtent(extentNum);
            if (ext == CPM_FileEntry.NO_EXTENT) {
                return uint.MaxValue;        // fully sparse extent, or off the end
            }
            // Get the alloc block number from the extent.
            ushort allocBlock = ext[extentIndex];
            if (allocBlock == 0) {
                return uint.MaxValue;        // sparse alloc block
            }

            // Convert the alloc block number and block remainder to a disk block.
            uint block = FileSystem.AllocBlockToDiskBlock(allocBlock) + (uint)subBlockIndex;
            Debug.Assert(subBlockIndex == block % blocksPerAlloc);
            return block;
        }

        /// <summary>
        /// Expands the file to include an allocation block for the specified block index.  This
        /// may expand at the end or in the middle (when filling in a sparse region).
        /// </summary>
        /// <param name="extentNum">Extent number that holds the block.</param>
        /// <param name="extentIndex">Index within extent (0-7 or 0-15).</param>
        /// <returns>Disk block number.</returns>
        /// <exception cref="DiskFullException">Disk is full.</exception>
        private uint ExpandFile(int extentNum, int extentIndex, int subBlockIndex) {
            // Get the extent.
            CPM_FileEntry.Extent ext = FileEntry.GetExtent(extentNum);
            if (ext == CPM_FileEntry.NO_EXTENT) {
                // Need to allocate a new extent.  This will throw if none are available.
                ext = FileSystem.FindFreeExtent();
                ext.SetForNew(FileEntry.UserNumber, FileEntry.FileName);
                ext.ExtentNumber = extentNum;
                FileEntry.AddExtent(ext);
            }

            CPM_AllocMap allocMap = FileSystem!.AllocMap!;
            ushort newAllocBlock = (ushort)allocMap.AllocateAllocBlock(FileEntry);
            ext[extentIndex] = newAllocBlock;

            // Now convert the block index.
            uint block = FileSystem.AllocBlockToDiskBlock(newAllocBlock) + (uint)subBlockIndex;
            Debug.WriteLine("Expanding " + FileEntry.FileName + ": ext=" + ext + " allocNum=" +
                newAllocBlock + " diskBlock=" + block);
            return block;
        }

        // Stream
        public override long Seek(long seekOff, SeekOrigin origin) {
            CheckValid();
            if (seekOff < -CPM.MAX_FILE_LEN || seekOff > CPM.MAX_FILE_LEN) {
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
                    newPos = SeekDataOrHole((int)seekOff, true);
                    break;
                case SEEK_ORIGIN_HOLE:
                    newPos = SeekDataOrHole((int)seekOff, false);
                    break;
                default:
                    throw new ArgumentException("Invalid seek mode");
            }

            // It's okay to be positioned one byte past the end of the file.  The largest file is
            // 0x01000000, so the last byte is at mark=0x00ffffff, and we want to allow 0x01000000.
            if (newPos < 0 || newPos > CPM.MAX_FILE_LEN) {
                throw new IOException("Invalid seek offset " + newPos);
            }
            mMark = (int)newPos;
            return newPos;
        }

        /// <summary>
        /// Finds the next data area or hole, starting from the specified absolute file offset.
        /// </summary>
        /// <param name="offset">Initial offset.</param>
        /// <param name="findData">If true, look for data; if false, for a hole.</param>
        /// <returns>Offset to data or hole.  If it's in the current block, the current offset
        ///   will be returned.  Otherwise, the offset of the start of the disk block (or
        ///   block-sized hole) will be returned.</returns>
        private int SeekDataOrHole(int offset, bool findData) {
            while (offset < mEOF) {
                int blockIndex = offset / BLOCK_SIZE;
                uint blockNum = GetBlockNum(blockIndex, out int unused1, out int unused2,
                    out int unused3);

                if (blockNum == uint.MaxValue) {
                    if (!findData) {
                        // in a hole
                        return offset;
                    }
                } else {
                    if (findData) {
                        // in a data region
                        return offset;
                    }
                }

                // Advance to next block, aligning the position with the block boundary.
                offset = (offset + BLOCK_SIZE) & ~(BLOCK_SIZE - 1);
            }
            if (offset >= mEOF) {
                // We're positioned in the "hole" at the end of the file.  There's no more
                // data to be found, so return EOF either way.
                return mEOF;
            }
            return offset;
        }

        // Stream
        public override void SetLength(long newEof) {
            CheckValid();
            if (mIsReadOnly) {
                throw new NotSupportedException("File was opened read-only");
            }
            if (newEof < 0 || newEof > CPM.MAX_FILE_LEN) {
                throw new ArgumentOutOfRangeException(nameof(newEof), newEof, "Invalid length");
            }
            if (newEof == mEOF) {
                return;
            }

            // len=0 needs 0 blocks, 1-1024 needs 1 block, 1025-2048 needs 2.  So we add 1023
            // and divide by 1024 (or whatever the allocation size is).
            uint allocSize = FileSystem.AllocUnitSize;
            int oldAllocNeeded = (int)((mEOF + allocSize - 1) / allocSize);
            int newAllocNeeded = (int)((newEof + allocSize - 1) / allocSize);
            if (oldAllocNeeded == newAllocNeeded) {
                // No change to number of blocks used.  Just update the EOF.
                mEOF = (int)newEof;
                Flush();
                return;
            }

            if (newEof > mEOF) {
                // Grow the file as much as possible.  This will throw when we run out of space.
                // The new area will be filled with 0xe5.
                int origEOF = mEOF;
                try {
                    FSUtil.ExtendFile(this, newEof, BLOCK_SIZE, true);
                } catch {
                    // Probably a DiskFullException.  Undo the growth and re-throw.
                    FileEntry.TruncateTo(origEOF);
                    mEOF = origEOF;
                    throw;
                }
            } else {
                // Truncate the storage space.
                FileEntry.TruncateTo(newEof);
            }

            mEOF = (int)newEof;
            Flush();
        }

        // Stream
        public override void Flush() {
            CheckValid();
            if (mIsReadOnly) {
                return;
            }
            // Update the record and byte counts in the various extents to match the EOF.
            FileEntry.UpdateLength(mEOF);
            FileEntry.SaveChanges();
        }

        // IDisposable generic finalizer.
        ~CPM_FileDesc() {
            Dispose(false);
        }
        protected override void Dispose(bool disposing) {
            if (disposing) {
                // Explicit close, via Close() call or "using" statement.
                try {
                    Flush();
                } catch {
                    Debug.Assert(false, "FileDesc dispose-time flush failed: " + mDebugPathName);
                }
                try {
                    // Tell the OS to forget about us.
                    FileSystem.CloseFile(this);
                } catch (Exception ex) {
                    Debug.WriteLine("FileSystem.CloseFile from fd close failed: " + ex.Message);
                }
            } else {
                // Finalizer dispose.
                if (FileEntry == null) {
                    Debug.Assert(false, "Finalization didn't get suppressed?");
                } else {
                    Debug.WriteLine("NOTE: GC disposing of file desc object " + this);
                    Debug.Assert(false, "Finalizing unclosed fd: " + this);
                }
            }

            // Mark the fd invalid so future calls will crash or do nothing.
            Invalidate();
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
            return "[CPM_FileDesc '" + mDebugPathName +
                (FileEntry == null ? " *INVALID*" : "") + "']";
        }
    }
}
