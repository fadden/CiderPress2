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
    public class RDOS_FileDesc : DiskFileStream {
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

        internal RDOS FileSystem { get; private set; }
        internal RDOS_FileEntry FileEntry { get; private set; }

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
        private byte[] mTmpSectorBuf = new byte[SECTOR_SIZE];


        /// <summary>
        /// Private constructor.
        /// </summary>
        private RDOS_FileDesc(RDOS_FileEntry entry, FileAccessMode mode, FilePart part) {
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
        internal static RDOS_FileDesc CreateFD(RDOS_FileEntry entry, FileAccessMode mode,
                FilePart part) {
            Debug.Assert(!entry.IsDamaged);
            Debug.Assert(part == FilePart.DataFork);

            RDOS_FileDesc newFd = new RDOS_FileDesc(entry, mode, part);
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
            int sectorIndex = mMark / SECTOR_SIZE;
            int sectorOffset = mMark % SECTOR_SIZE;
            while (count > 0) {
                uint sectorNum = FileEntry.StartIndex + (uint)sectorIndex;
                FileSystem.SectorIndexToTrackSector(sectorNum, out uint trk, out uint sct);
                FileSystem.ChunkAccess.ReadSector(trk, sct, mTmpSectorBuf, 0, FileSystem.FSOrder);

                // Copy everything we need out of this block.
                int bufLen = SECTOR_SIZE - sectorOffset;
                if (bufLen > count) {
                    bufLen = count;
                }
                Array.Copy(mTmpSectorBuf, sectorOffset, readBuf, readOffset, bufLen);

                readOffset += bufLen;
                count -= bufLen;
                mMark += bufLen;

                sectorIndex++;
                sectorOffset = 0;
            }

            return actual;
        }

        // Stream
        public override void Write(byte[] buffer, int offset, int count) {
            throw new NotSupportedException("File was opened read-only");
        }

        // Stream
        public override long Seek(long seekOff, SeekOrigin origin) {
            CheckValid();
            if (seekOff < -RDOS.MAX_FILE_LEN || seekOff > RDOS.MAX_FILE_LEN) {
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

            if (newPos < 0 || newPos > RDOS.MAX_FILE_LEN) {
                throw new IOException("Invalid seek offset " + newPos);
            }
            mMark = (int)newPos;
            return newPos;
        }

        // Stream
        public override void SetLength(long value) {
            throw new NotSupportedException("File was opened read-only");
        }

        // Stream
        public override void Flush() { }

        // IDisposable generic finalizer.
        ~RDOS_FileDesc() {
            Dispose(false);
        }
        protected override void Dispose(bool disposing) {
            if (disposing) {
                // Explicit close, via Close() call or "using" statement.
                try {
                    // Tell the OS to forget about us.
                    FileSystem.CloseFile(this);
                } catch (Exception ex) {
                    Debug.WriteLine("FileSystem.CloseFile from fd close failed: " + ex.Message);
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
            return "[RDOS_FileDesc '" + mDebugPathName +
                (FileEntry == null ? " *INVALID*" : "") + "']";
        }
    }
}
