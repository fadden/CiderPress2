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
        private bool mInternalOpen;                     // skip informing filesystem of close?

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
        private Pascal_FileDesc(Pascal_FileEntry entry, FileAccessMode mode, FilePart part,
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
        /// <exception cref="IOException">Disk access failure, or corrupted file
        ///   structure.</exception>
        /// <returns>File descriptor object.</returns>
        internal static Pascal_FileDesc CreateFD(Pascal_FileEntry entry, FileAccessMode mode,
                FilePart part, bool internalOpen) {
            Debug.Assert(!entry.IsDamaged);
            Debug.Assert(part == FilePart.DataFork);

            Pascal_FileDesc newFd = new Pascal_FileDesc(entry, mode, part, internalOpen);
            newFd.mEOF = (int)entry.DataLength;

            return newFd;
        }

        // Stream
        public override int Read(byte[] readBuf, int readOffset, int count) {
            CheckValid();
            if (readOffset < 0 || count < 0) {
                throw new ArgumentOutOfRangeException("Bad offset / count");
            }
            if (readBuf == null) {
                throw new ArgumentNullException("Buffer is null");
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
            throw new NotImplementedException();
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
            // 0x00ffffff, so the last byte is at mark=0x00fffffe, and we want to allow 0x00ffffff.
            if (newPos < 0 || newPos > Pascal.MAX_FILE_LEN) {
                throw new IOException("Invalid seek offset " + newPos);
            }
            mMark = (int)newPos;
            return newPos;
        }

        // Stream
        public override void SetLength(long value) {
            throw new NotImplementedException();
        }

        // Stream
        public override void Flush() {
            CheckValid();
            if (mIsReadOnly) {
                return;
            }
            throw new NotImplementedException();
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
                if (!mInternalOpen) {
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
            return "[Pascal_FileDesc '" + mDebugPathName +
                (FileEntry == null ? " *INVALID*" : "") + "']";
        }
    }
}
