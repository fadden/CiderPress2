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
    public class Gutenberg_FileDesc : DiskFileStream {
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

        internal Gutenberg FileSystem { get; private set; }
        internal Gutenberg_FileEntry FileEntry { get; private set; }

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
        private byte[] mTmpSectorBuf = new byte[SECTOR_SIZE];

        /// <summary>
        /// A simple track/sector pair.
        /// </summary>
        private struct TrackSector {
            public byte Track { get; }
            public byte Sector { get; }
            public TrackSector(byte track, byte sector) {
                Debug.Assert(track < 0x40);
                Track = track;
                Sector = sector;
            }
        }

        /// <summary>
        /// List of tracks and sectors associated with this file.
        /// </summary>
        private List<TrackSector> mSectorList = new List<TrackSector>();


        /// <summary>
        /// Private constructor.
        /// </summary>
        private Gutenberg_FileDesc(Gutenberg_FileEntry entry, FileAccessMode mode, FilePart part,
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
        internal static Gutenberg_FileDesc CreateFD(Gutenberg_FileEntry entry, FileAccessMode mode,
                FilePart part, bool internalOpen) {
            Debug.Assert(!entry.IsDamaged);
            Debug.Assert(part == FilePart.DataFork);

            Gutenberg_FileDesc newFd = new Gutenberg_FileDesc(entry, mode, part, internalOpen);
            newFd.mEOF = (int)entry.DataLength;

            // Walk through the list of sectors to gather the full list.  This will throw an
            // exception if something looks wrong.
            newFd.GenerateSectorList();

            return newFd;
        }

        /// <summary>
        /// Normalizes a track/sector value.  The high bit is stripped, and 0x40 is converted
        /// to zero.
        /// </summary>
        /// <param name="trk">Track number.</param>
        /// <param name="sct">Sector number.</param>
        /// <param name="normTrk">Result: normalized track number (0-34).</param>
        /// <param name="normSct">Result: normalized sector number (0-15).</param>
        /// <returns>True if the high bit was set in the track or sector number.</returns>
        internal static bool NormalizeTrackSector(byte trk, byte sct,
                out byte normTrk, out byte normSct) {
            bool hiSet = ((trk & 0x80) != 0 || (sct & 0x80) != 0);
            trk &= 0x7f;
            sct &= 0x7f;
            normTrk = (trk == 0x40) ? (byte)0 : trk;
            normSct = (sct == 0x40) ? (byte)0 : sct;
            return hiSet;
        }

        /// <summary>
        /// Generates the list of tracks and sectors that make up this file.  The track/sector
        /// values are validated, and appropriate notes added.
        /// </summary>
        /// <exception cref="IOException">Disk access failure, due to bad block or invalid
        ///   track/sector link.  File is damaged and inaccessible.</exception>
        private void GenerateSectorList() {
            mSectorList.Clear();
            NormalizeTrackSector(FileEntry.FirstTrack, FileEntry.FirstSector,
                out byte trk, out byte sct);
            int iterations = 0;
            while (iterations++ < Gutenberg.MAX_FILE_SECTORS) {
                mSectorList.Add(new TrackSector(trk, sct));
                FileSystem.ChunkAccess.ReadSector(trk, sct, mTmpSectorBuf, 0);
                // Not currently validating backward links.
                NormalizeTrackSector(mTmpSectorBuf[0x02], mTmpSectorBuf[0x03],
                    out byte curTrk, out byte curSct);
                if (curTrk != trk || (curSct & 0x7f) != sct) {
                    FileSystem.Notes.AddW("found bad self-reference in T" + trk + " S" + sct +
                        " (T" + curTrk + " S" + curSct + "): " + FileEntry.FileName);
                    FileEntry.HasBadLink = true;
                    // keep going
                }
                bool endReached = NormalizeTrackSector(mTmpSectorBuf[0x04], mTmpSectorBuf[0x05],
                    out byte nextTrk, out byte nextSct);
                if ((nextTrk == 0 && nextSct == 0) ||
                        (nextTrk & 0x7f) >= FileSystem.ChunkAccess.NumTracks ||
                        nextSct >= FileSystem.ChunkAccess.NumSectorsPerTrack) {
                    FileSystem.Notes.AddW("found bad forward link in T" + trk + " S" + sct +
                        " to T" + nextTrk + " S" + nextSct + ": " + FileEntry.FileName);
                    FileEntry.HasBadLink = true;
                    endReached = true;
                }
                if (endReached) {
                    break;      // this was the last sector in the file
                }
                trk = nextTrk;
                sct = nextSct;
            }
            if (iterations == Gutenberg.MAX_FILE_SECTORS) {
                FileSystem.Notes.AddW("found circular file structure in " + FileEntry.FileName);
                throw new IOException("circular T/S references in " + FileEntry.FileName);
            }
        }

        internal void SetVolumeUsage() {
            VolumeUsage vu = FileSystem.VolUsage!;
            foreach (TrackSector ts in mSectorList) {
                vu.SetUsage(FileSystem.TSToChunk(ts.Track, ts.Sector), FileEntry);
            }
        }

        /// <summary>
        /// Gets the number of sectors used by the file.
        /// </summary>
        internal int GetSectorsUsed() {
            return mSectorList.Count;
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

            const int SECTOR_DATA_SIZE = SECTOR_SIZE - Gutenberg.SECTOR_HEADER_LEN;

            // Calculate block index from file position.
            int sectorIndex = mMark / SECTOR_DATA_SIZE;
            int sectorOffset = Gutenberg.SECTOR_HEADER_LEN + mMark % SECTOR_DATA_SIZE;
            while (count > 0) {
                GetTSByIndex(sectorIndex, out byte trk, out byte sct);
                FileSystem.ChunkAccess.ReadSector(trk, sct, mTmpSectorBuf, 0);

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
                sectorOffset = Gutenberg.SECTOR_HEADER_LEN;
            }

            return actual;
        }

        // Stream
        public override void Write(byte[] buffer, int offset, int count) {
            throw new NotSupportedException("File was opened read-only");
        }

        private void GetTSByIndex(int sectorIndex, out byte trk, out byte sct) {
            TrackSector ts = mSectorList[sectorIndex];
            trk = ts.Track;
            sct = ts.Sector;
        }

        // Stream
        public override long Seek(long seekOff, SeekOrigin origin) {
            CheckValid();
            if (seekOff < -Gutenberg.MAX_FILE_LEN || seekOff > Gutenberg.MAX_FILE_LEN) {
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

            if (newPos < 0 || newPos > Gutenberg.MAX_FILE_LEN) {
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
        ~Gutenberg_FileDesc() {
            Dispose(false);
        }
        protected override void Dispose(bool disposing) {
            //Debug.WriteLine("Disposing " + mDebugPathName + " (" + disposing + ")");
            if (disposing) {
                // Explicit close, via Close() call or "using" statement.
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
            return "[Gutenberg_FileDesc '" + mDebugPathName +
                (FileEntry == null ? " *INVALID*" : "") + "']";
        }
    }
}
