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
using static DiskArc.IFileSystem;

namespace DiskArc.FS {
    /// <summary>
    /// DOS 3.2/3.3 file descriptor.
    /// </summary>
    /// <remarks>
    /// <para>Files whose length is changed, by writing past the end of the file or allocating
    /// a sparse block, will have their length recalculated when the file is closed.  This
    /// will also be done if the first block of an I/A/B file was written.</para>
    /// <para>Files may be opened in "raw data" mode to bypass the file-type-specific rules
    /// for end-of-file determination.  The end-of-file will be a multiple of 256, and the
    /// length set in the first sector of I/A/B files will not be altered when the
    /// file is closed.</para>
    /// <para>The file's type is latched when the file is opened.  Changing the type of an
    /// open file will not change what happens when it is closed.</para>
    /// </remarks>
    public class DOS_FileDesc : DiskFileStream {
        internal static readonly byte[] sZeroSector = new byte[SECTOR_SIZE];

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

        private bool IsRaw { get { return Part == FilePart.RawData; } }

        internal DOS FileSystem { get; set; }
        internal DOS_FileEntry FileEntry { get; set; }
        private string DebugPathName { get; set; }

        private bool mIsReadOnly;
        private bool mInternalOpen;

        /// <summary>
        /// Current file length.  Will be either the "raw" or "cooked" length, depending on how
        /// the file was opened.
        /// The value may change if the file is modified.
        /// </summary>
        private int mEOF;

        /// <summary>
        /// Sectors used to hold the file.
        /// The value may change if the file is modified.
        /// </summary>
        private ushort mSectorsUsed;

        /// <summary>
        /// Current file position.  May extend past EOF.
        /// In a "cooked" file, the type-specific offset is subtracted, so it still starts at 0.
        /// </summary>
        private int mMark;

        /// <summary>
        /// Type-specific data start offset, used for "cooked" I/A/B.
        /// </summary>
        private int mDataStartOffset;

        /// <summary>
        /// Type-specific length limitation, will be shortened for "cooked" I/A/B.
        /// </summary>
        private int mMaxFileLength;

        /// <summary>
        /// Copy of raw data length from file entry.
        /// </summary>
        private int mRawDataLength;

        /// <summary>
        /// DOS file type (without the "locked" flag).
        /// </summary>
        private byte mDOSFileType;

        // General-purpose temporary buffer, to reduce allocations.  Don't call other methods
        // while holding data here.
        private byte[] mTmpSectorBuf = new byte[SECTOR_SIZE];

        /// <summary>
        /// This holds a track/sector list sector.
        /// </summary>
        internal class TSSector {
            public byte Trk { get; private set; }
            public byte Sct { get; private set; }
            public byte[] Data { get; private set; }
            public bool IsDirty { get; private set; }

            public byte NextTrk {
                get { return Data[0x01]; }
                set { Data[0x01] = value; IsDirty = true; }
            }
            public byte NextSct {
                get { return Data[0x02]; }
                set { Data[0x02] = value; IsDirty = true; }
            }

            /// <summary>
            /// Constructor, for a newly-allocated TS list sector.
            /// </summary>
            /// <param name="trk">Disk track number.</param>
            /// <param name="sct">Disk sector number.</param>
            /// <param name="sectorOffset">Sector offset value (a multiple of 122).</param>
            public TSSector(byte trk, byte sct, ushort sectorOffset) {
                Trk = trk;
                Sct = sct;
                Data = new byte[SECTOR_SIZE];
                RawData.SetU16LE(Data, 0x05, sectorOffset);
                IsDirty = true;
            }

            /// <summary>
            /// Constructor, for a sector loaded from disk.
            /// </summary>
            /// <param name="trk">Disk track number.</param>
            /// <param name="sct">Disk sector number.</param>
            /// <param name="chunkSource">Sector I/O object.</param>
            public TSSector(byte trk, byte sct, IChunkAccess chunkSource) {
                Trk = trk;
                Sct = sct;
                Data = new byte[SECTOR_SIZE];
                chunkSource.ReadSector(trk, sct, Data, 0);
            }

            /// <summary>
            /// If the block is dirty, write the changes to disk.
            /// </summary>
            /// <param name="chunkSource">Block I/O object.</param>
            /// <exception cref="IOException">Disk access failure.</exception>
            public void SaveChanges(IChunkAccess chunkSource) {
                if (!IsDirty) {
                    return;
                }
                chunkSource.WriteSector(Trk, Sct, Data, 0);
                IsDirty = false;
            }

            /// <summary>
            /// Gets the Nth track/sector pair.
            /// </summary>
            /// <param name="index">T/S entry index (0-121).</param>
            public void GetTS(int index, out byte trk, out byte sct) {
                Debug.Assert(index >= 0 && index < DOS.MAX_TS_PER_TSLIST);
                trk = Data[DOS.FIRST_TS_OFFSET + index * 2];
                sct = Data[DOS.FIRST_TS_OFFSET + 1 + index * 2];
            }

            /// <summary>
            /// Sets the Nth block number.
            /// </summary>
            public void SetTS(int index, byte trk, byte sct) {
                Debug.Assert(index >= 0 && index < DOS.MAX_TS_PER_TSLIST);
                Data[DOS.FIRST_TS_OFFSET + index * 2] = trk;
                Data[DOS.FIRST_TS_OFFSET + 1 + index * 2] = sct;
                IsDirty = true;
            }

            public override string ToString() {
                return "[TSList T" + Trk + " S" + Sct + " dirty=" + IsDirty + "]";
            }
        }

        /// <summary>
        /// Full set of track/sector list sectors for this file.
        /// </summary>
        private List<TSSector> mTSLists = new List<TSSector>();


        /// <summary>
        /// Constructor.
        /// </summary>
        private DOS_FileDesc(DOS_FileEntry entry, FileAccessMode mode, FilePart part,
                bool internalOpen) {
            FileSystem = entry.FileSystem;
            FileEntry = entry;
            Part = part;
            mIsReadOnly = (mode == FileAccessMode.ReadOnly);
            mInternalOpen = internalOpen;

            DebugPathName = entry.FullPathName;     // latch name when file opened
            mDOSFileType = entry.DOSFileType;
            mSectorsUsed = entry.SectorCount;
            mRawDataLength = entry.RawDataLength;
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
        internal static DOS_FileDesc CreateFD(DOS_FileEntry entry, FileAccessMode mode,
                FilePart part, bool internalOpen) {
            Debug.Assert(!entry.IsDamaged);

            DOS_FileDesc newFd = new DOS_FileDesc(entry, mode, part, internalOpen);

            // Configure values based on file type and requested part.
            if (part == FilePart.RawData) {
                // File type is irrelevant.  Use "raw" length.
                newFd.mEOF = entry.RawDataLength;
                newFd.mMaxFileLength = DOS.MAX_FILE_LEN;
            } else {
                // File type matters.  Use "cooked" length.
                newFd.mEOF = (int)entry.DataLength;

                // Configure data offset and maximum file size.
                switch (entry.DOSFileType) {
                    case DOS_FileEntry.TYPE_I:
                    case DOS_FileEntry.TYPE_A:
                        newFd.mDataStartOffset = 2;
                        newFd.mMaxFileLength = 0xffff;
                        break;
                    case DOS_FileEntry.TYPE_B:
                        newFd.mDataStartOffset = 4;
                        newFd.mMaxFileLength = 0xffff;
                        break;
                    default:
                        newFd.mDataStartOffset = 0;
                        newFd.mMaxFileLength = DOS.MAX_FILE_LEN;
                        break;
                }
            }

            DOS fs = entry.FileSystem;

            // Load the full track/sector list chain.  If the file is "dubious", we may have
            // to stop early due to damage.  This should have been taken into account already
            // by the file length analyzer.
            //
            // Circular lists are caught during analysis, and cause the file to be marked as
            // damaged, so we don't need to count iterations here.
            TSSector ts = new TSSector(entry.TSTrk, entry.TSSct, fs.ChunkAccess);
            newFd.mTSLists.Add(ts);
            while (true) {
                if (ts.NextTrk == 0 || !fs.IsSectorValid(ts.NextTrk, ts.NextSct)) {
                    break;
                }
                ts = new TSSector(ts.NextTrk, ts.NextSct, fs.ChunkAccess);
                newFd.mTSLists.Add(ts);
            }

            return newFd;
        }

        /// <summary>
        /// Locates the Nth entry in the T/S list.  If the index is outside the current file
        /// bounds, 0/0 is returned.
        /// </summary>
        /// <param name="sectorIndex">Index of entry to find.</param>
        /// <param name="trk">Result: track number.</param>
        /// <param name="sct">Result: sector number.</param>
        private void GetSectorByIndex(int sectorIndex, out byte trk, out byte sct) {
            int listIndex = sectorIndex / DOS.MAX_TS_PER_TSLIST;
            if (listIndex >= mTSLists.Count) {
                // T/S list sector not allocated for this sector.
                trk = sct = 0;
                return;
            }
            TSSector ts = mTSLists[listIndex];
            ts.GetTS(sectorIndex % DOS.MAX_TS_PER_TSLIST, out trk, out sct);
        }

        /// <summary>
        /// Sets the Nth entry in the T/S list.  If the index is outside the current file
        /// bounds, a new T/S list sector is allocated.
        /// </summary>
        /// <param name="sectorIndex">Index of sector to update.</param>
        /// <param name="trk">Data sector track number.</param>
        /// <param name="sct">Data sector sector number.</param>
        /// <exception cref="DiskFullException">Disk full.</exception>
        private void SetSectorByIndex(int sectorIndex, byte trk, byte sct) {
            Debug.Assert(trk != 0);     // can't create sparse regions this way

            int listIndex = sectorIndex / DOS.MAX_TS_PER_TSLIST;
            while (listIndex >= mTSLists.Count) {
                // We're off the end of the T/S list chain.  Allocate a new T/S list sector.
                Debug.Assert(listIndex < DOS.MAX_TS_CHAIN);
                Debug.Assert(FileSystem.VTOC != null);
                FileSystem.VTOC.AllocSector(FileEntry, out byte newTrk, out byte newSct);
                mSectorsUsed++;

                // Add a link to the new sector to the last entry in the chain.
                TSSector lastTs = mTSLists[mTSLists.Count - 1];
                Debug.Assert(lastTs.NextTrk == 0);
                lastTs.NextTrk = newTrk;
                lastTs.NextSct = newSct;
                lastTs.SaveChanges(FileSystem.ChunkAccess);

                int thisIndex = mTSLists.Count;
                mTSLists.Add(new TSSector(newTrk, newSct,
                    (ushort)(thisIndex * DOS.MAX_TS_PER_TSLIST)));
            }
            TSSector ts = mTSLists[listIndex];
            ts.SetTS(sectorIndex % DOS.MAX_TS_PER_TSLIST, trk, sct);
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

            // Factor in the type-specific data offset.
            int filePos = mMark + mDataStartOffset;
            int sectorIndex = filePos / SECTOR_SIZE;
            int sectorOffset = filePos % SECTOR_SIZE;
            while (count > 0) {
                // Read the block's contents into a temporary buffer.
                GetSectorByIndex(sectorIndex, out byte trk, out byte sct);

                byte[] data;
                if (trk == 0) {
                    // Sparse sector, just copy zeroes.
                    data = sZeroSector;
                } else {
                    // Read data from source.
                    data = mTmpSectorBuf;
                    FileSystem.ChunkAccess.ReadSector(trk, sct, data, 0);
                }

                // Copy everything we need out of this block.
                int bufLen = SECTOR_SIZE - sectorOffset;
                if (bufLen > count) {
                    bufLen = count;
                }
                Array.Copy(data, sectorOffset, readBuf, readOffset, bufLen);

                readOffset += bufLen;
                count -= bufLen;
                mMark += bufLen;

                sectorIndex++;
                sectorOffset = 0;
            }

            return actual;
        }

        // Stream
        public override void Write(byte[] buf, int offset, int count) {
            CheckValid();
            if (mIsReadOnly) {
                throw new NotSupportedException("File was opened read-only");
            }
            Debug.Assert(!FileEntry.IsDamaged && !FileEntry.IsDubious);
            if (offset < 0 || count < 0) {
                throw new ArgumentOutOfRangeException("Bad offset / count");
            }
            if (buf == null) {
                throw new ArgumentNullException("Buffer is null");
            }
            bool specialCaseZero = false;
            if (count == 0) {
                if (mEOF == 0 && mSectorsUsed == 1 && mDataStartOffset != 0) {
                    // This is a zero-length I/A/B file, being written in "cooked" mode.  We
                    // allow writing zero bytes as a special case to cause the first sector to
                    // be allocated.  To minimize the special-case handling, convert this to a
                    // single-byte write.
                    //
                    // This will mark the file entry as dirty, so when the changes are saved
                    // we'll write any pending change to the aux type.
                    Debug.WriteLine("Zero-length I/A/B special case");
                    specialCaseZero = true;
                    buf = new byte[1];
                    count = 1;
                    offset = 0;
                } else {
                    return;
                }
            }
            if (offset >= buf.Length || count > buf.Length - offset) {
                throw new ArgumentException("Buffer overrun");
            }
            if (offset + count > buf.Length) {
                throw new ArgumentOutOfRangeException("Buffer underflow");
            }
            Debug.Assert(mMark >= 0 && mMark <= mMaxFileLength);
            if (mMark + count > mMaxFileLength) {
                // We don't do partial writes, so we just throw if we're off the end.
                throw new IOException("Write would exceed max file size (mark=" +
                    mMark + " len=" + count + ")");
            }

            DOS_VTOC? vtoc = FileSystem.VTOC;
            Debug.Assert(vtoc != null);

            // Factor in the type-specific data offset.
            int filePos = mMark + mDataStartOffset;
            int sectorIndex = filePos / SECTOR_SIZE;
            int sectorOffset = filePos % SECTOR_SIZE;
            while (count > 0) {
                byte[] writeSource;
                int writeSourceOff;
                int writeLen;

                GetSectorByIndex(sectorIndex, out byte trk, out byte sct);
                if (sectorOffset != 0 || count < SECTOR_SIZE) {
                    // Partial write to the start or end of a sector.  If there's an existing
                    // non-sparse sector here, we need to read it and merge the contents.
                    if (trk != 0) {
                        FileSystem.ChunkAccess.ReadSector(trk, sct, mTmpSectorBuf, 0);
                    } else {
                        Array.Clear(mTmpSectorBuf);
                    }
                    // Try to fill out the rest of the sector, but clamp to write length.
                    writeLen = SECTOR_SIZE - sectorOffset;
                    if (writeLen > count) {
                        writeLen = count;
                    }
                    Array.Copy(buf, offset, mTmpSectorBuf, sectorOffset, writeLen);
                    writeSource = mTmpSectorBuf;
                    writeSourceOff = 0;
                } else {
                    // Writing a full sector, so just use the source data.
                    writeSource = buf;
                    writeSourceOff = offset;
                    writeLen = SECTOR_SIZE;
                }

                // Unlike ProDOS, we don't create sparse sectors when they're zero-filled.  The
                // only way to create a sparse sector is to seek past EOF.

                if (trk == 0) {
                    // Off the end of the list, or in a sparse hole.  Allocate a new sector.
                    // This could throw if the disk is full.
                    vtoc.AllocSector(FileEntry, out trk, out sct);
                    // This will allocate a new T/S list sector if we're off the end of the list,
                    // which could cause a disk full exception.
                    try {
                        SetSectorByIndex(sectorIndex, trk, sct);
                    } catch {
                        // Undo the previous alloc.
                        vtoc.MarkSectorUnused(trk, sct);
                        throw;
                    }

                    mSectorsUsed++;
                }

                // Write the data to disk.
                FileSystem.ChunkAccess.WriteSector(trk, sct, writeSource, writeSourceOff);

                // See if we've potentially altered the "cooked" length calculation.
                switch (mDOSFileType) {
                    case DOS_FileEntry.TYPE_T:
                        // We don't restrict what can be written, or check to see if a $00 byte
                        // has been replaced, so any write requires a full re-scan.
                        FileEntry.NeedRecalcLength = true;
                        break;
                    case DOS_FileEntry.TYPE_I:
                    case DOS_FileEntry.TYPE_A:
                    case DOS_FileEntry.TYPE_B:
                        if (IsRaw && sectorIndex == 0) {
                            // May have modified the embedded length word.
                            FileEntry.NeedRecalcLength = true;
                        }
                        break;
                }

                if (specialCaseZero) {
                    writeLen = count = 0;
                }

                // Advance file position.
                mMark += writeLen;
                offset += writeLen;
                count -= writeLen;
                if (mMark > mEOF) {
                    mEOF = mMark;
                }
                sectorIndex++;
                sectorOffset = 0;

                // See if we've altered the "raw" length value.  For example, if the raw data
                // length is 512, the size will change if our new mark is > 512.  If we're
                // writing in "cooked" mode, we need to add the start offset.  The raw length
                // does not treat sparse sectors specially, so this is easy to compute.
                if (mMark + mDataStartOffset > mRawDataLength) {
                    int dataSectorCount =
                        (mMark + mDataStartOffset + (SECTOR_SIZE - 1)) / SECTOR_SIZE;
                    mRawDataLength = dataSectorCount * SECTOR_SIZE;
                }
            }
        }

        // Stream
        public override void SetLength(long newEof) {
            CheckValid();
            if (mIsReadOnly) {
                throw new NotSupportedException("File was opened read-only");
            }
            if (newEof < 0 || newEof > mMaxFileLength) {
                throw new ArgumentOutOfRangeException(nameof(newEof), newEof, "Invalid length");
            }

            if (newEof == mEOF) {
                // No change.
                return;
            } else if (newEof > mEOF) {
                if (IsRaw) {
                    // We can't extend the EOF and leave a sparse data hole, because the length
                    // scanner ignores all T/S list entries with track=0 after the last nonzero
                    // entry.  So we either need to allocate a bunch of zero-filled sectors, or
                    // just ignore the call.
                    FileSystem.AppHook.LogW("Ignoring attempt to extend EOF of raw data file");
                } else {
                    // Update the EOF value, which SaveChanges() will store in I/A/B files.  For
                    // I/A/B we're potentially creating a badly-formed file, but if the caller
                    // wanted the file padded out with empty blocks they'd be calling Write
                    // instead of SetLength.
                    //
                    // For non-I/A/B, this will generally have no effect.
                    mEOF = (int)newEof;
                }

                return;
            }

            // File is getting smaller.  Truncate the storage.
            mEOF = (int)newEof;
            int truncPoint = (int)(newEof + mDataStartOffset);

            DOS_VTOC? vtoc = FileSystem.VTOC;
            Debug.Assert(vtoc != null);

            // Index of first entry to be deleted.
            int firstDelSctIndex = (truncPoint + (SECTOR_SIZE - 1)) / SECTOR_SIZE;

            // Update raw data length.  (This may be wrong if the file is sparse.)
            mRawDataLength = firstDelSctIndex * SECTOR_SIZE;

            // See if we need to do a partial erase.
            if (firstDelSctIndex == 0 || firstDelSctIndex % DOS.MAX_TS_PER_TSLIST != 0) {
                // Removing some or all of the contents of this list sector, but not
                // removing the sector itself.  Set the deleted entries to 0/0, and clear
                // the link to the next entry.
                TSSector partialTsl = mTSLists[firstDelSctIndex / DOS.MAX_TS_PER_TSLIST];
                for (int idx = firstDelSctIndex % DOS.MAX_TS_PER_TSLIST;
                        idx < DOS.MAX_TS_PER_TSLIST; idx++) {
                    partialTsl.GetTS(idx, out byte trk, out byte sct);
                    if (trk != 0) {
                        // Non-sparse sector.  Free it and clear the entry.
                        vtoc.MarkSectorUnused(trk, sct);
                        mSectorsUsed--;
                        partialTsl.SetTS(idx, 0, 0);
                    }
                }
                partialTsl.NextTrk = partialTsl.NextSct = 0;

                // Advance to first entry in next TS list.
                firstDelSctIndex = firstDelSctIndex + DOS.MAX_TS_PER_TSLIST -
                    (firstDelSctIndex % DOS.MAX_TS_PER_TSLIST);
                Debug.Assert(firstDelSctIndex % DOS.MAX_TS_PER_TSLIST == 0);
            } else {
                // Removing this entire list sector.  Need to remove the forward link from the
                // previous entry.
                TSSector prevTsl = mTSLists[(firstDelSctIndex - 1) / DOS.MAX_TS_PER_TSLIST];
                prevTsl.NextTrk = prevTsl.NextSct = 0;
            }

            // Free everything in the remaining T/S list sectors.  We don't need to modify the
            // list sectors or write them, since we broke the chain earlier.
            int firstDelList = firstDelSctIndex / DOS.MAX_TS_PER_TSLIST;
            for (int listIdx = firstDelList; listIdx < mTSLists.Count; listIdx++) {
                TSSector tsl = mTSLists[listIdx];
                for (int i = 0; i < DOS.MAX_TS_PER_TSLIST; i++) {
                    tsl.GetTS(i, out byte trk, out byte sct);
                    if (trk != 0) {
                        vtoc.MarkSectorUnused(trk, sct);
                        mSectorsUsed--;
                    }
                }
                vtoc.MarkSectorUnused(tsl.Trk, tsl.Sct);
                mSectorsUsed--;
            }

            // Remove the excess T/S lists.
            for (int i = mTSLists.Count - 1; i >= firstDelList; --i) {
                TSSector delTsl = mTSLists[i];
                if (delTsl.IsDirty) {
                    // Some pending change from earlier?  Flush it just to be safe.
                    delTsl.SaveChanges(FileSystem.ChunkAccess);
                }
                mTSLists.RemoveAt(i);
            }

            // We generally need to recalculate the length.  If we didn't have to worry about
            // sparse files, we could just set the raw data length based on the first entry
            // we deleted, but I don't want to deal with scanning through sparseness here.
            FileEntry.NeedRecalcLength = true;

            // Save our partially-updated list sector, and anything else that's pending.
            Flush();
        }

        // Stream
        public override void Flush() {
            CheckValid();
            if (mIsReadOnly) {
                return;
            }

            // Save changes to track/sector lists.
            foreach (TSSector ts in mTSLists) {
                if (ts.IsDirty) {
                    ts.SaveChanges(FileSystem.ChunkAccess);
                }
            }

            // The file entry's DataLength value is equal to the explicit length (for I/A/B),
            // the first scanned $00 (for T), or the raw data length (for S/R/A+/B+).  We may
            // need to update the DataLength value:
            //
            // - If we're in raw mode, and we wrote to the first sector of the file, we need
            //   to fully re-calculate the length (entry.NeedRecalcLength should be set).
            // - If we're in raw mode, and we added more data to the end of the file, we DO NOT
            //   update the DataLength of I/A/B.  We DO need to re-calculate for text files
            //   (entry.NeedRecalcLength should be set).  We DO need to set DataLength equal
            //   to the raw data length.
            // - If we're in cooked mode, and we added more data to the end of the file, we DO
            //   need to update the length of I/A/B.
            switch (mDOSFileType) {
                case DOS_FileEntry.TYPE_I:
                case DOS_FileEntry.TYPE_A:
                case DOS_FileEntry.TYPE_B:
                    if (!IsRaw) {
                        // Get the length word and see if it needs an update.
                        int lenOffset = 0;
                        if (mDOSFileType == DOS_FileEntry.TYPE_B) {
                            lenOffset = 2;
                        }
                        GetSectorByIndex(0, out byte trk, out byte sct);
                        if (trk != 0) {
                            FileSystem.ChunkAccess.ReadSector(trk, sct, mTmpSectorBuf, 0);
                            ushort oldLen = RawData.GetU16LE(mTmpSectorBuf, lenOffset);
                            if (oldLen != mEOF) {
                                Debug.Assert(mEOF <= 0xffff);
                                RawData.SetU16LE(mTmpSectorBuf, lenOffset, (ushort)mEOF);
                                FileSystem.ChunkAccess.WriteSector(trk, sct, mTmpSectorBuf, 0);
                            }
                            FileEntry.DataLength = mEOF;
                        } else {
                            // I/A/B for which the first sector hasn't yet been allocated.  We
                            // can't update the length at this time.  This can happen if SetLength
                            // is called on an empty file.  If the first sector is written before
                            // the file is closed, we'll revisit this.
                        }
                    }
                    break;
                case DOS_FileEntry.TYPE_T:
                    // Any modification to text files requires a re-scan.
                    break;
                default:
                    // S/R/A+/B+ just track the raw length.
                    FileEntry.DataLength = mRawDataLength;
                    break;
            }

            // Save changes to file attributes.
            FileEntry.SectorCount = mSectorsUsed;
            FileEntry.RawDataLength = mRawDataLength;
            FileEntry.SaveChanges();

            // This could happen after a SetLength on a sparse file.
            if (FileEntry.RawDataLength != mRawDataLength) {
                Debug.WriteLine("Raw data length changed");
                mRawDataLength = FileEntry.RawDataLength;
            }
        }

        // Stream
        public override long Seek(long seekOff, SeekOrigin origin) {
            CheckValid();
            if (seekOff < -mMaxFileLength || seekOff > mMaxFileLength) {
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
                    if (Part == FilePart.RawData) {
                        newPos = SeekDataOrHole((int)seekOff, true);
                    } else {
                        newPos = seekOff;
                    }
                    break;
                case SEEK_ORIGIN_HOLE:
                    if (Part == FilePart.RawData) {
                        newPos = SeekDataOrHole((int)seekOff, false);
                    } else {
                        newPos = mEOF;
                    }
                    break;
                default:
                    throw new ArgumentException("Invalid seek mode");
            }

            // It's okay to be positioned one byte past the end of the file.
            if (newPos < 0 || newPos > mMaxFileLength) {
                throw new IOException("Invalid seek offset " + newPos);
            }
            mMark = (int)newPos;
            return newPos;
        }

        /// <summary>
        /// Finds the next data area or hole, starting from the specified absolute file offset.
        /// </summary>
        private int SeekDataOrHole(int offset, bool findData) {
            while (offset < mEOF) {
                int sectorIndex = offset / SECTOR_SIZE;
                GetSectorByIndex(sectorIndex, out byte trk, out byte sct);

                if (trk == 0) {
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
                offset = (offset + SECTOR_SIZE) & ~(SECTOR_SIZE - 1);
            }
            if (offset >= mEOF) {
                // We're positioned in the "hole" at the end of the file.  There's no more
                // data to be found, so return EOF either way.
                return mEOF;
            }
            return offset;
        }

        /// <summary>
        /// Marks all sectors associated with the open file as unallocated.
        /// </summary>
        internal void ReleaseStorage() {
            Debug.Assert(!FileEntry.IsDamaged && !FileEntry.IsDubious);

            DOS_VTOC? vtoc = FileSystem.VTOC;
            Debug.Assert(vtoc != null);

            // Walk through the T/S list sectors, freeing everything.
            foreach (TSSector tsl in mTSLists) {
                for (int i = 0; i < DOS.MAX_TS_PER_TSLIST; i++) {
                    tsl.GetTS(i, out byte trk, out byte sct);
                    if (trk != 0) {
                        vtoc.MarkSectorUnused(trk, sct);
                    }
                }
                vtoc.MarkSectorUnused(tsl.Trk, tsl.Sct);
            }
        }

        // IDisposable generic finalizer.
        ~DOS_FileDesc() {
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

                // Flush all pending changes.  This will fail if the file entry object has
                // been invalidated.
                try {
                    Flush();
                } catch {
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
            return "[DOS_FileDesc '" + DebugPathName + "' raw=" + IsRaw + " type=$" +
                mDOSFileType.ToString("x2") + (FileEntry == null ? " *INVALID*" : "") + "]";
        }
    }
}
