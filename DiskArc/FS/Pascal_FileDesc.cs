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
    public class Pascal_FileDesc : DiskFileStream {
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

        internal Pascal FileSystem { get; private set; }
        internal Pascal_FileEntry FileEntry { get; private set; }

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


        /// <summary>
        /// Private constructor.
        /// </summary>
        private Pascal_FileDesc(Pascal_FileEntry entry, FileAccessMode mode, FilePart part) {
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
        internal static Pascal_FileDesc CreateFD(Pascal_FileEntry entry, FileAccessMode mode,
                FilePart part) {
            Debug.Assert(!entry.IsDamaged);
            Debug.Assert(part == FilePart.DataFork);

            Pascal_FileDesc newFd = new Pascal_FileDesc(entry, mode, part);
            newFd.mEOF = (int)entry.DataLength;

            return newFd;
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
                uint blockNum = FileEntry.StartBlock + (uint)blockIndex;
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
            Debug.Assert(mMark >= 0 && mMark <= Pascal.MAX_FILE_LEN);
            if (mMark + count > Pascal.MAX_FILE_LEN) {
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

                FileEntry.ExpandIfNeeded(blockIndex);
                uint blockNum = FileEntry.StartBlock + (uint)blockIndex;
                if (blockOffset != 0 || count < BLOCK_SIZE) {
                    // Partial write to the start or end of a block.  Read the block and merge
                    // the contents.
                    FileSystem.ChunkAccess.ReadBlock(blockNum, mTmpBlockBuf, 0);
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
                FileSystem.ChunkAccess.WriteBlock(blockNum, writeSource, writeSourceOff);

                // Advance file position.
                mMark += writeLen;
                offset += writeLen;
                count -= writeLen;
                if (mMark > mEOF) {
                    mEOF = mMark;
                }
            }
        }

        // Stream
        public override long Seek(long seekOff, SeekOrigin origin) {
            CheckValid();
            if (seekOff < -Pascal.MAX_FILE_LEN || seekOff > Pascal.MAX_FILE_LEN) {
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

            // It's okay to be positioned one byte past the end of the file.  The largest file is
            // 0x01000000, so the last byte is at mark=0x00ffffff, and we want to allow 0x01000000.
            if (newPos < 0 || newPos > Pascal.MAX_FILE_LEN) {
                throw new IOException("Invalid seek offset " + newPos);
            }
            mMark = (int)newPos;
            return newPos;
        }

        private static readonly byte[] sZeroBuf = new byte[BLOCK_SIZE];

        // Stream
        public override void SetLength(long newEof) {
            CheckValid();
            if (mIsReadOnly) {
                throw new NotSupportedException("File was opened read-only");
            }
            if (newEof < 0 || newEof > Pascal.MAX_FILE_LEN) {
                throw new ArgumentOutOfRangeException(nameof(newEof), newEof, "Invalid length");
            }
            if (newEof == mEOF) {
                return;
            }

            // If the end of file is 0, 511, or 512, we need one block; at 513 we need 2.  So
            // the number of blocks required is ((EOF - 1) / 512) + 1, with a special case
            // for zero because we still want to allocate a block even if we don't have data.
            // We want the index of the last block, which is (length - 1).
            //
            // Note (-1 / BLOCK_SIZE) == 0.
            int oldLastBlockIndex = (mEOF - 1) / BLOCK_SIZE;
            int newLastBlockIndex = ((int)newEof - 1) / BLOCK_SIZE;
            if (oldLastBlockIndex == newLastBlockIndex) {
                // No change to number of blocks used.  Just update the EOF.
                mEOF = (int)newEof;
                Flush();
                return;
            }

            Debug.Assert(FileEntry.StartBlock + oldLastBlockIndex < FileEntry.NextBlock);

            if (newEof > mEOF) {
                // Grow the file as much as possible.  This will throw when we run out of space.
                // We could do a simple "FileEntry.ExpandIfNeeded(newLastBlockIndex)", but that
                // would leave the previous contents of the disk in the file.  Better to zero it.
                int origEOF = mEOF;
                try {
                    FSUtil.ExtendFile(this, newEof, BLOCK_SIZE);
                } catch {
                    // Probably a DiskFullException.  Undo the growth and re-throw.
                    FileEntry.TruncateTo(oldLastBlockIndex);
                    mEOF = origEOF;
                    throw;
                }
            } else {
                // Truncate the storage space.
                FileEntry.TruncateTo(newLastBlockIndex);
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

            // Changes to NextBlock are made directly to the file entry, but we don't update
            // the byte count on every write.  Update it here.
            //
            // This is a little tricky because a 512-byte file has a byte count of 512, not zero.
            ushort bytesInLastBlock = (ushort)(mEOF % BLOCK_SIZE);
            if (mEOF != 0 && bytesInLastBlock == 0) {
                bytesInLastBlock = 512;     // exactly filled out last block
            }
            FileEntry.ByteCount = bytesInLastBlock;
            FileEntry.SaveChanges();
        }

        // IDisposable generic finalizer.
        ~Pascal_FileDesc() {
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
            return "[Pascal_FileDesc '" + mDebugPathName +
                (FileEntry == null ? " *INVALID*" : "") + "']";
        }
    }
}
