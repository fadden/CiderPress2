﻿/*
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
using DiskArc.FS;
using DiskArc.Multi;
using static DiskArc.Defs;
using static DiskArc.IDiskImage;

namespace DiskArc.Disk {
    /// <summary>
    /// Plain block/sector disk image.
    /// </summary>
    /// <remarks>
    /// <para>All reads and writes are made directly to the file stream.  This object does
    /// not own the stream, and will not dispose of it.</para>
    /// </remarks>
    public class UnadornedSector : IDiskImage {
        //
        // IDiskImage properties.
        //

        public bool IsReadOnly { get { return !mStream.CanWrite; } }
        public bool IsDirty => false;           // all changes are written through
        public bool IsDubious => false;         // nothing to damage

        public bool IsModified {
            get {
                if (ChunkAccess != null) {
                    return ChunkAccess.IsModified;
                } else {
                    return false;
                }
            }
            set {
                // A little weird to set it to true, but that's fine.
                if (ChunkAccess != null) {
                    ChunkAccess.IsModified = value;
                }
            }
        }

        public Notes Notes { get; } = new Notes();
        public GatedChunkAccess? ChunkAccess { get; private set; }
        public IDiskContents? Contents { get; private set; }

        //
        // Innards.
        //

        // Smallest file we'll recognize as a possible block image.  This helps screen out
        // small files, notably those named ".IMG".
        private const int MIN_BLOCK_IMG_LEN = ProDOS.MIN_VOL_SIZE;

        private Stream mStream;
        private AppHook mAppHook;


        // IDiskImage-required delegate
        public static bool TestKind(Stream stream, AppHook appHook) {
            // The contents of an unadorned image are not restricted.  All we have to go on
            // is the length of the file.

            if (stream.Length == SECTOR_SIZE * 13 * 35) {
                // Special case for .d13 image; everything else has an even number of sectors,
                // so we can test mod BLOCK_SIZE rather than SECTOR_SIZE.
                return true;
            } else {
                // Don't test vs. MIN_BLOCK_IMG_LEN, as there might not be a filesystem on it.
                return stream.Length >= 0 && stream.Length % BLOCK_SIZE == 0;
            }
        }

        /// <summary>
        /// Private constructor.
        /// </summary>
        private UnadornedSector(Stream stream, AppHook appHook) {
            Debug.Assert(TestKind(stream, appHook));
            mStream = stream;
            mAppHook = appHook;
        }

        /// <summary>
        /// Open a disk image file.
        /// </summary>
        /// <param name="stream">Disk image data stream.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <exception cref="NotSupportedException">Incompatible data stream.</exception>
        public static UnadornedSector OpenDisk(Stream stream, AppHook appHook) {
            if (!TestKind(stream, appHook)) {
                throw new NotSupportedException("Incompatible data stream");
            }
            return new UnadornedSector(stream, appHook);
        }

        /// <summary>
        /// Determines whether the disk configuration is supported.
        /// </summary>
        /// <param name="numTracks">Number of tracks.</param>
        /// <param name="sectorsPerTrack">Number of sectors.</param>
        /// <param name="sectorOrder">Desired sector order.</param>
        /// <param name="errMsg">Error message, or empty string on success.</param>
        /// <returns>True on success.</returns>
        public static bool CanCreateSectorImage(uint numTracks, uint sectorsPerTrack,
                SectorOrder sectorOrder, out string errMsg) {
            errMsg = string.Empty;
            if (numTracks != 35 && numTracks != 40 && numTracks != 50 && numTracks != 80) {
                errMsg = "Invalid number of tracks: " + numTracks;
            }
            if (sectorsPerTrack != 13 && sectorsPerTrack != 16 && sectorsPerTrack != 32) {
                errMsg = "Invalid number of sectors: " + sectorsPerTrack;
            }
            if (sectorOrder == SectorOrder.Unknown) {
                errMsg = "Must choose a sector order";
            }
            if (sectorOrder != SectorOrder.DOS_Sector && sectorsPerTrack != 16) {
                errMsg = "Must use DOS order for non-16-sector disks";
            }
            return errMsg == string.Empty;
        }

        /// <summary>
        /// Creates a new disk image as a collection of tracks and 256-byte sectors.
        /// </summary>
        /// <remarks>
        /// <para>The disk will be zeroed out.  It will have an IChunkAccess configured for the
        /// specified file order, but no filesystem.</para>
        /// <para>The disk will be written at the start of the stream.  The stream's length
        /// will be set to the disk image length.</para>
        /// </remarks>
        /// <param name="stream">Stream into which the new image will be written.  The stream
        ///   must be readable, writable, and seekable.</param>
        /// <param name="numTracks">Number of tracks (35, 40, 50, or 80).</param>
        /// <param name="sectorsPerTrack">Number of sectors per track (13, 16, or 32).</param>
        /// <param name="fileOrder">Order in which sectors are stored in the file.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <returns>Disk image object.</returns>
        public static UnadornedSector CreateSectorImage(Stream stream, uint numTracks,
                uint sectorsPerTrack, SectorOrder fileOrder, AppHook appHook) {
            if (!stream.CanRead || !stream.CanWrite || !stream.CanSeek) {
                throw new ArgumentException("Invalid stream capabilities");
            }
            if (!CanCreateSectorImage(numTracks, sectorsPerTrack, fileOrder, out string errMsg)) {
                throw new ArgumentException(errMsg);
            }
            stream.Position = 0;
            stream.SetLength(0);
            stream.SetLength(numTracks * sectorsPerTrack * SECTOR_SIZE);
            UnadornedSector disk = new UnadornedSector(stream, appHook);
            disk.ChunkAccess = new GatedChunkAccess(
                new GeneralChunkAccess(stream, 0, numTracks, sectorsPerTrack, fileOrder));
            return disk;
        }

        /// <summary>
        /// Determines whether the disk configuration is supported.
        /// </summary>
        /// <param name="numBlocks">Number of blocks.</param>
        /// <param name="errMsg">Error message, or empty string on success.</param>
        /// <returns>True on success.</returns>
        public static bool CanCreateBlockImage(uint numBlocks, out string errMsg) {
            errMsg = string.Empty;
            if (numBlocks == 0 || numBlocks > 8 * 1024 * 1024) {        // arbitrary 4GB limit
                errMsg = "Invalid block count: " + numBlocks;
            }
            return errMsg == string.Empty;
        }

        /// <summary>
        /// Creates a new disk image as a collection of 512-byte blocks.
        /// </summary>
        /// <remarks>
        /// <para>The disk will be zeroed out.  It will have an IChunkAccess configured for
        /// ProDOS block order, but no filesystem.</para>
        /// <para>The disk will be written at the start of the stream.  The stream's length
        /// will be set to the disk image length.</para>
        /// </remarks>
        /// <param name="stream">Stream into which the new image will be written.  The stream
        ///   must be readable, writable, and seekable.</param>
        /// <param name="numBlocks">Number of blocks in the disk image.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <returns>Disk image object.</returns>
        public static UnadornedSector CreateBlockImage(Stream stream, uint numBlocks,
                AppHook appHook) {
            if (!stream.CanRead || !stream.CanWrite || !stream.CanSeek) {
                throw new ArgumentException("Invalid stream capabilities");
            }
            if (!CanCreateBlockImage(numBlocks, out string errMsg)) {
                throw new ArgumentException(errMsg);
            }
            stream.Position = 0;
            stream.SetLength(numBlocks * (long)BLOCK_SIZE);
            UnadornedSector disk = new UnadornedSector(stream, appHook);
            disk.ChunkAccess = new GatedChunkAccess(new GeneralChunkAccess(stream, 0, numBlocks));
            return disk;
        }

        // IDiskImage
        public bool AnalyzeDisk(SectorCodec? codec, SectorOrder orderHint, AnalysisDepth depth) {
            // Discard existing FileSystem and ChunkAccess objects (filesystem will be flushed).
            CloseContents();
            if (ChunkAccess != null) {
                ChunkAccess.AccessLevel = GatedChunkAccess.AccessLvl.Closed;
                ChunkAccess = null;
            }

            // Guess at the disk layout based on the stream length.
            IChunkAccess chunks;
            if (mStream.Length == SECTOR_SIZE * 13 * 35) {
                // 5.25" floppy, 13 sectors; should be DOS sector order
                if (orderHint == SectorOrder.Unknown) {
                    orderHint = SectorOrder.DOS_Sector;
                }
                chunks = new GeneralChunkAccess(mStream, 0, 35, 13, orderHint);
            } else if (mStream.Length == SECTOR_SIZE * 16 * 35) {
                // 35-track 5.25" floppy, 16 sectors; could be DOS sector or ProDOS block order
                if (orderHint == SectorOrder.Unknown) {
                    orderHint = SectorOrder.DOS_Sector;
                }
                chunks = new GeneralChunkAccess(mStream, 0, 35, 16, orderHint);
            } else if (mStream.Length == SECTOR_SIZE * 16 * 40) {
                // 40-track 5.25" floppy, 16 sectors; could be DOS sector or ProDOS block order
                if (orderHint == SectorOrder.Unknown) {
                    orderHint = SectorOrder.DOS_Sector;
                }
                chunks = new GeneralChunkAccess(mStream, 0, 40, 16, orderHint);
            } else if (mStream.Length == SECTOR_SIZE * 16 * 50) {
                // 50-track 5.25" floppy (not really a thing except for embedded volumes; we
                // only handle it here for the sake of generality)
                chunks = new GeneralChunkAccess(mStream, 0, 50, 16, orderHint);
            } else if (mStream.Length == SECTOR_SIZE * 32 * 50) {
                if (orderHint == SectorOrder.DOS_Sector) {
                    // 50-track, 32-sector DOS volume (not really a thing except for 2x400KB on a
                    // 3.5" floppy, but somebody might extract one and expect it to work)
                    chunks = new GeneralChunkAccess(mStream, 0, 50, 32, orderHint);
                } else {
                    // Not DOS ordered, treat as ProDOS blocks.
                    chunks = new GeneralChunkAccess(mStream, 0,
                        (uint)(mStream.Length / BLOCK_SIZE));
                }
            } else if (mStream.Length == SECTOR_SIZE * 16 * 80) {
                // 80-track 5.25" floppy (rare)
                chunks = new GeneralChunkAccess(mStream, 0, 80, 16, orderHint);
            } else if (mStream.Length % BLOCK_SIZE == 0 && mStream.Length > MIN_BLOCK_IMG_LEN) {
                // Assume block device of arbitrary size.
                chunks = new GeneralChunkAccess(mStream, 0, (uint)(mStream.Length / BLOCK_SIZE));
            } else {
                return false;
            }
            ChunkAccess = new GatedChunkAccess(chunks);

            if (depth == AnalysisDepth.ChunksOnly) {
                return true;
            }
            // Figure out the disk layout.
            if (!FileAnalyzer.AnalyzeFileSystem(chunks, true, mAppHook,
                    out IDiskContents? contents)) {
                return false;
            }
            // Got the disk layout, disable direct chunk access.
            Contents = contents;
            ChunkAccess.AccessLevel = GatedChunkAccess.AccessLvl.ReadOnly;
            return true;
        }

        // IDiskImage
        public virtual void CloseContents() {
            if (Contents is IFileSystem) {
                ((IFileSystem)Contents).Dispose();
            } else if (Contents is IMultiPart) {
                ((IMultiPart)Contents).Dispose();
            }
            Contents = null;
            if (ChunkAccess != null) {
                ChunkAccess.AccessLevel = GatedChunkAccess.AccessLvl.Open;
            }
        }

        ~UnadornedSector() {
            Dispose(false);     // cleanup check
        }
        // IDisposable generic Dispose() implementation.
        public void Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        protected virtual void Dispose(bool disposing) {
            Debug.Assert(!mDisposed, this + " disposed twice");

            // If we're being disposed explicitly, e.g. by a using statement or declaration,
            // dispose of the filesystem object.  If we're being finalized, don't, because
            // the order of finalization is undefined.
            if (disposing) {
                Flush();
                if (Contents is IFileSystem) {
                    ((IFileSystem)Contents).Dispose();
                } else if (Contents is IMultiPart) {
                    ((IMultiPart)Contents).Dispose();
                }
                Contents = null;
            } else {
                Debug.Assert(false, "GC disposing UnadornedSector, dirty=" + IsDirty +
                    " created:\r\n" + mCreationStackTrace);
            }
            mDisposed = true;
        }
        private bool mDisposed = false;
#if DEBUG
        private string mCreationStackTrace = Environment.StackTrace + Environment.NewLine + "---";
#else
        private string mCreationStackTrace = "  (stack trace not enabled)";
#endif

        // IDiskImage
        public void Flush() {
            // Flush any pending filesystem changes.
            if (Contents is IFileSystem) {
                ((IFileSystem)Contents).Flush();
            } else if (Contents is IMultiPart) {
                foreach (Partition part in (IMultiPart)Contents) {
                    part.FileSystem?.Flush();
                }
            }
            // Flush anything sitting in file buffer.
            mStream.Flush();

            // Nothing more to do -- the chunk access object writes directly to the stream.
        }
    }
}
