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
using static DiskArc.Defs;
using static DiskArc.FS.HFS_Struct;
using static DiskArc.IFileSystem;

namespace DiskArc.FS {
    /// <summary>
    /// HFS file descriptor.
    /// </summary>
    public class HFS_FileDesc : DiskFileStream {
        internal static readonly byte[] sZeroBlock = new byte[BLOCK_SIZE];

        // Stream
        public override bool CanRead { get { return FileEntry != null; } }
        public override bool CanSeek { get { return FileEntry != null; } }
        public override bool CanWrite { get { return FileEntry != null && !mIsReadOnly; } }
        public override long Length => mEndOfFile;
        public override long Position {
            get { return mMark; }
            set { Seek(value, SeekOrigin.Begin); }
        }

        // DiskFileStream
        public override FilePart Part { get; }
        private bool IsRsrcFork { get { return Part == FilePart.RsrcFork; } }

        internal HFS FileSystem { get; private set; }
        internal HFS_FileEntry FileEntry { get; private set; }
        internal string DebugPathName { get; private set; }

        private bool mIsReadOnly;
        private bool mInternalOpen;

        /// <summary>
        /// Current "logical" length.
        /// </summary>
        private int mEndOfFile;

        /// <summary>
        /// File storage tracker.  The file's physical length and extents are held here.
        /// </summary>
        HFS_FileStorage mFileStorage;

        /// <summary>
        /// Current file position.  Files are limited to 2GB.
        /// </summary>
        private int mMark;

        // Placeholder during init.
        private static readonly ExtDataRec NO_DATA_REC = new ExtDataRec();

        // General-purpose temporary disk buffer, to reduce allocations.  Don't call other methods
        // while holding data here.
        private byte[] mTmpBlockBuf = new byte[BLOCK_SIZE];


        /// <summary>
        /// Private constructor.
        /// </summary>
        private HFS_FileDesc(HFS_FileEntry entry, FileAccessMode mode,
                FilePart part, bool internalOpen) {
            FileSystem = entry.FileSystem;
            FileEntry = entry;
            Part = part;
            mInternalOpen = internalOpen;

            bool isRsrc = (Part == FilePart.RsrcFork);
            mFileStorage = new HFS_FileStorage(entry.FileSystem, entry, entry.EntryCNID, isRsrc,
                isRsrc ? entry.RsrcPhyLength : entry.DataPhyLength,
                isRsrc ? entry.RsrcExtentRec : entry.DataExtentRec);

            DebugPathName = entry.FullPathName;
            mIsReadOnly = (mode == FileAccessMode.ReadOnly);
        }

        /// <summary>
        /// Creates a file descriptor for the specified file.
        /// </summary>
        /// <param name="entry">File to open.</param>
        /// <param name="mode">Access mode.</param>
        /// <param name="part">Data or resource fork.</param>
        /// <param name="internalOpen">If true, don't call back into the filesystem when
        ///   the file is closed.</param>
        /// <returns>File descriptor.</returns>
        /// <exception cref="ArgumentException">Attempt to open directory read-write.</exception>
        internal static HFS_FileDesc CreateFD(HFS_FileEntry entry,
                FileAccessMode mode, FilePart part, bool internalOpen) {
            Debug.Assert(!entry.IsDamaged);

            HFS_FileDesc newFd = new HFS_FileDesc(entry, mode, part, internalOpen);
            if (part == FilePart.RsrcFork) {
                newFd.mEndOfFile = (int)entry.RsrcLength;
            } else {
                newFd.mEndOfFile = (int)entry.DataLength;
            }
            return newFd;
        }

        /// <summary>
        /// Determines the logical block number for the specified logical block index.
        /// </summary>
        private uint GetLogicalBlockNum(uint logiIndex) {
            Debug.Assert(FileSystem.VolMDB != null);
            uint abIndex = logiIndex / FileSystem.VolMDB.LogicalPerAllocBlock;
            uint abOffset = logiIndex % FileSystem.VolMDB.LogicalPerAllocBlock;

            // Convert index into an absolute allocation block number.
            uint ablk = mFileStorage.GetAllocBlockNum(abIndex);

            // Convert ABN to a logical block number.
            uint logiBlock = FileSystem.AllocBlockToLogiBlock(ablk);
            return logiBlock + abOffset;
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
            if (count == 0 || mMark >= mEndOfFile) {
                return 0;
            }
            if (readOffset >= readBuf.Length || count > readBuf.Length - readOffset) {
                throw new ArgumentException("Buffer overrun");
            }

            // Trim request to fit file bounds.
            if ((long)mMark + count > mEndOfFile) {
                count = mEndOfFile - mMark;
            }
            if ((long)readOffset + count > readBuf.Length) {
                throw new ArgumentOutOfRangeException("Buffer overflow");
            }
            int actual = count;    // we read it all, or fail

            // Calculate logical block index from file position.
            int blockIndex = mMark / BLOCK_SIZE;
            int blockOffset = mMark % BLOCK_SIZE;
            while (count > 0) {
                // Read the block's contents into a temporary buffer.
                uint blockNum = GetLogicalBlockNum((uint)blockIndex);

                // Read data from source.
                byte[] data = mTmpBlockBuf;
                FileSystem.ChunkAccess.ReadBlock(blockNum, data, 0);

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
        public override void Write(byte[] buf, int offset, int count) {
            CheckValid();
            if (mIsReadOnly) {
                throw new NotSupportedException("File was opened read-only");
            }
            if (offset < 0 || count < 0) {
                throw new ArgumentOutOfRangeException("Bad offset / count");
            }
            if (buf == null) {
                throw new ArgumentNullException("Buffer is null");
            }
            if (count == 0) {
                return;
            }
            if (offset >= buf.Length || count > buf.Length - offset) {
                throw new ArgumentException("Buffer overrun");
            }

            if (offset < 0 || count < 0) {
                throw new ArgumentOutOfRangeException("Bad offset or length");
            }
            if (offset + count > buf.Length) {
                throw new ArgumentOutOfRangeException("Buffer underflow");
            }
            Debug.Assert(mMark >= 0 && mMark <= HFS.MAX_FILE_LEN);
            if (mMark + count > HFS.MAX_FILE_LEN) {
                // We don't do partial writes, so we just throw if we're off the end.
                // The last byte in the last block is not accessible.
                throw new IOException("Write would exceed max file size (mark=" +
                    mMark + " len=" + count + ")");
            }

            while (count > 0) {
                // Set block index according to file position.
                int blockIndex = mMark / BLOCK_SIZE;
                int blockOffset = mMark % BLOCK_SIZE;

                // Add storage until we're caught up with the file mark.  If this is the first
                // write after a Seek call, we might need to do this several times.  Note this
                // does not zero out the storage.
                if (mMark >= mFileStorage.PhyLength) {
                    mFileStorage.Extend();
                    continue;
                }

                byte[] writeSource;
                int writeSourceOff;
                int writeLen;

                uint blockNum = GetLogicalBlockNum((uint)blockIndex);

                if (blockOffset != 0 || count < BLOCK_SIZE) {
                    // Partial write to the start or end of a block.  Read previous contents,
                    // then copy the new data in.
                    FileSystem.ChunkAccess.ReadBlock(blockNum, mTmpBlockBuf, 0);
                    writeLen = BLOCK_SIZE - blockOffset;
                    if (writeLen > count) {
                        writeLen = count;
                    }
                    Array.Copy(buf, offset, mTmpBlockBuf, blockOffset, writeLen);
                    writeSource = mTmpBlockBuf;
                    writeSourceOff = 0;
                } else {
                    // Full sector.
                    writeSource = buf;
                    writeSourceOff = offset;
                    writeLen = BLOCK_SIZE;
                }

                FileSystem.ChunkAccess.WriteBlock(blockNum, writeSource, writeSourceOff);

                // Advance file position.
                mMark += writeLen;
                offset += writeLen;
                count -= writeLen;
                if (mMark > mEndOfFile) {
                    mEndOfFile = mMark;
                }
            }
        }

        // Stream
        public override long Seek(long seekOff, SeekOrigin origin) {
            CheckValid();
            if (seekOff < -HFS.MAX_FILE_LEN || seekOff > HFS.MAX_FILE_LEN) {
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
                    newPos = mEndOfFile + seekOff;
                    break;
                case SEEK_ORIGIN_DATA:
                    newPos = seekOff;       // no holes
                    break;
                case SEEK_ORIGIN_HOLE:
                    newPos = mEndOfFile;    // no holes
                    break;
                default:
                    throw new ArgumentException("Invalid seek mode");
            }

            // It's okay to be positioned one byte past the end of the file.  The largest file is
            // 0x7fffffff, so the last byte is at mark=0x7ffffffe, and we want to allow 0x7fffffff.
            if (newPos < 0 || newPos > HFS.MAX_FILE_LEN) {
                throw new IOException("Invalid seek offset " + newPos);
            }
            mMark = (int)newPos;
            return newPos;
        }

        // Stream
        public override void Flush() {
            CheckValid();
            if (mIsReadOnly) {
                return;
            }

            // Copy any updates to the FileEntry.
            if (IsRsrcFork) {
                FileEntry.RsrcLength = mEndOfFile;
                FileEntry.RsrcPhyLength = mFileStorage.PhyLength;
                FileEntry.RsrcExtentRec = mFileStorage.FirstExtRec;
            } else {
                FileEntry.DataLength = mEndOfFile;
                FileEntry.DataPhyLength = mFileStorage.PhyLength;
                FileEntry.DataExtentRec = mFileStorage.FirstExtRec;
            }

            // Write the catalog record.
            FileEntry.SaveChanges();

            // VolBitmap and MDB are flushed when the file is closed.
        }

        // Stream
        public override void SetLength(long newEof) {
            CheckValid();
            if (mIsReadOnly) {
                throw new NotSupportedException("File was opened read-only");
            }
            if (newEof < 0 || newEof > HFS.MAX_FILE_LEN) {
                throw new ArgumentOutOfRangeException(nameof(newEof), newEof, "Invalid length");
            }
            if (newEof == mEndOfFile) {
                return;
            }

            if (newEof > mEndOfFile) {
                // Extend the physical storage space immediately.  We can't simply call
                // mFileStorage.ExtendTo, because that won't zero out the existing data
                // in the disk blocks.  Use repeated Write calls instead.
                //
                // If we run out of space or hit a bad block, an exception will be thrown.  We
                // don't need to discard the storage because it will be truncated automatically
                // when the file is closed.
                int origEOF = mEndOfFile;
                try {
                    int chunkSize = (int)FileSystem.VolMDB!.BlockSize;
                    FSUtil.ExtendFile(this, newEof, chunkSize);
                } catch {
                    mEndOfFile = origEOF;
                    throw;
                }
            } else {
                // Defer truncation to the point where the file is closed.
            }
            mEndOfFile = (int)newEof;

            // Flush now to make changes visible.
            Flush();
        }

        /// <summary>
        /// Trims excess storage off the end of a file fork.
        /// </summary>
        internal void Trim() {
            CheckValid();
            if (mIsReadOnly) {
                return;
            }

            mFileStorage.Trim(mEndOfFile);
        }

        // IDisposable generic finalizer.
        ~HFS_FileDesc() {
            Dispose(false);
        }
        protected override void Dispose(bool disposing) {
            bool tryToSave = false;

            if (disposing) {
                // Explicit close, via Close() call or "using" statement.
                if (FileEntry != null) {
                    // First time.  Don't blow up if they do both Close() and "using".
                    tryToSave = true;
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

            if (tryToSave) {
                Debug.Assert(FileEntry != null);
                if (FileEntry.IsDirty && mIsReadOnly) {
                    // Valid but weird.
                    Debug.WriteLine("NOTE: dirent changes pending for read-only file");
                    Debug.Assert(false);        // TODO: remove
                }

                // Trim excess storage, then flush all pending changes.  This will fail if
                // the file entry object has been invalidated.
                try {
                    Trim();
                    Flush();
                } catch (Exception) {
                    Debug.Assert(false, "FileDesc dispose-time flush failed: " + DebugPathName);
                }

                if (!mInternalOpen) {
                    try {
                        // Tell the OS to forget about us.
                        FileSystem.CloseFile(this);
                    } catch (Exception ex) {
                        // Oh well.
                        Debug.WriteLine("FileSystem.CloseFile from fd close failed: " + ex.Message);
                    }
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
                    DebugPathName + ")");
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
            return "[HFS_FileDesc '" + DebugPathName + "' isRsrc=" + IsRsrcFork +
                (FileEntry == null ? " *INVALID*" : "") + "]";
        }
    }
}
