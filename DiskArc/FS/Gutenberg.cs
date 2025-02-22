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
using static DiskArc.FileAnalyzer.DiskLayoutEntry;
using static DiskArc.IFileSystem;

namespace DiskArc.FS {
    /// <summary>
    /// Gutenberg filesystem implementation.
    /// </summary>
    public class Gutenberg : IFileSystem {
        public const byte FIRST_CAT_TRACK = 17;         // track # of catalog start
        public const byte FIRST_CAT_SECTOR = 7;         // sector # of catalog start
        public const byte VOL_BITMAP_SECTOR = 6;        // sector number of bitmap (on track 17)
        public const int SECTOR_HEADER_LEN = 6;         // length of linked list header
        public const int MAX_VOLNAME_LEN = 9;           // max length of a volume name
        public const int MAX_FILENAME_LEN = 12;         // max length of a filename

        internal const int MAX_CAT_SECTORS = 64;        // arbitrary limit
        internal const int MAX_FILE_SECTORS = 256;      // arbitrary limit
        internal const long MAX_FILE_LEN = MAX_FILE_SECTORS * SECTOR_SIZE;

        private const string VOLNAME_RULES =
            "1-9 ASCII characters, no spaces or control characters.";
        private const string FILENAME_RULES =
            "1-12 ASCII characters, no spaces, '/', or control characters.";
        private static FSCharacteristics sCharacteristics = new FSCharacteristics(
            name: "Gutenberg",
            canWrite: false,
            isHierarchical: false,
            dirSep: IFileEntry.NO_DIR_SEP,
            hasResourceForks: false,
            fnSyntax: FILENAME_RULES,
            vnSyntax: VOLNAME_RULES,
            tsStart: DateTime.MinValue,
            tsEnd: DateTime.MinValue
        );

        //
        // IFileSystem interfaces.
        //

        public FSCharacteristics Characteristics => sCharacteristics;
        public static FSCharacteristics SCharacteristics => sCharacteristics;

        public Notes Notes { get; } = new Notes();

        public bool IsReadOnly => true;

        public bool IsDubious { get; internal set; }

        public long FreeSpace { get; private set; }

        public GatedChunkAccess RawAccess { get; }

        //
        // Implementation-specific.
        //

        /// <summary>
        /// Data source.  Contents may be shared in various ways.
        /// </summary>
        internal IChunkAccess ChunkAccess { get; private set; }

        /// <summary>
        /// Volume usage map.
        /// Only valid in file-access mode.
        /// </summary>
        internal VolumeUsage? VolUsage { get; private set; }

        /// <summary>
        /// Application-specified options and message logging.
        /// </summary>
        internal AppHook AppHook { get; private set; }

        /// <summary>
        /// List of open files.
        /// </summary>
        private OpenFileTracker mOpenFiles = new OpenFileTracker();

        /// <summary>
        /// "Fake" volume directory entry, used to hold directory entries.
        /// </summary>
        private IFileEntry mVolDirEntry;

        /// <summary>
        /// True if we're in file-access mode, false if raw-access mode.
        /// </summary>
        private bool IsPreppedForFileAccess { get { return mVolDirEntry != IFileEntry.NO_ENTRY; } }


        // Delegate: test image to see if it's ours.
        public static TestResult TestImage(IChunkAccess chunkSource, AppHook appHook) {
            if (!chunkSource.HasSectors) {
                return TestResult.No;
            }
            if (chunkSource.FormattedLength != 13 * 35 * SECTOR_SIZE &&
                    chunkSource.FormattedLength != 16 * 35 * SECTOR_SIZE) {
                return TestResult.No;
            }

            byte[] sctBuf = new byte[SECTOR_SIZE];

            // Scan the disk catalog.  Any irregularity is regarded as a failure.
            uint trk = FIRST_CAT_TRACK;
            uint sct = FIRST_CAT_SECTOR;
            int iterations = 0;
            while (iterations++ < MAX_CAT_SECTORS) {
                chunkSource.ReadSector(trk, sct, sctBuf, 0);
                byte curTrk = sctBuf[2];
                byte curSct = sctBuf[3];
                if (curTrk != trk || (curSct & 0x7f) != sct) {
                    // Mismatch.  Wrong sector ordering?
                    return TestResult.No;
                }
                // Check the end-of-line markers to confirm this is a catalog sector.  We could
                // be more thorough here but that doesn't seem necessary.
                for (int i = 0x0f; i <= 0xff; i += 16) {
                    if (sctBuf[i] != 0x8d) {
                        return TestResult.No;
                    }
                }
                trk = sctBuf[4];
                sct = sctBuf[5];
                if ((trk & 0x80) != 0) {
                    // Reached the end.
                    break;
                }
                if (trk >= chunkSource.NumTracks || sct >= chunkSource.NumSectorsPerTrack) {
                    return TestResult.No;
                }
            }
            if (iterations == MAX_CAT_SECTORS) {
                return TestResult.No;       // infinite loop
            }

            return TestResult.Yes;
        }

        // Delegate: returns true if the size (in bytes) is valid for this filesystem.
        public static bool IsSizeAllowed(long size) {
            return size == 35 * 16 * SECTOR_SIZE;
        }

        public Gutenberg(IChunkAccess chunks, AppHook appHook) {
            Debug.Assert(chunks.HasSectors);
            ChunkAccess = chunks;
            AppHook = appHook;

            RawAccess = new GatedChunkAccess(chunks);
            mVolDirEntry = IFileEntry.NO_ENTRY;
        }

        public override string ToString() {
            string id = mVolDirEntry == IFileEntry.NO_ENTRY ? "raw" : mVolDirEntry.FileName;
            return "[Gutenberg (" + id + ")]";
        }

        // IFileSystem
        public string? DebugDump() { return null; }

        // IDisposable generic finalizer.
        ~Gutenberg() {
            Dispose(false);
        }
        // IDisposable generic Dispose() implementation.
        public void Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        private bool mDisposed;
        protected virtual void Dispose(bool disposing) {
            if (mDisposed) {
                AppHook.LogW("Attempting to dispose of Gutenberg object twice");
                return;
            }
            AppHook.LogD("Gutenberg.Dispose");
            if (disposing) {
                if (mOpenFiles.Count != 0) {
                    CloseAll();
                }
                if (mVolDirEntry != IFileEntry.NO_ENTRY) {
                    // Invalidate all associated file entry objects.
                    InvalidateFileEntries();
                }
                RawAccess.AccessLevel = GatedChunkAccess.AccessLvl.Closed;
                mDisposed = true;
            }
        }

        // IFileSystem
        public void Flush() { }

        /// <summary>
        /// Converts track+sector to a chunk number, for VolumeUsage.
        /// </summary>
        internal uint TSToChunk(uint trk, uint sct) {
            return trk * ChunkAccess.NumSectorsPerTrack + sct;
        }

        // IFileSystem
        public void PrepareFileAccess(bool doScan) {
            if (IsPreppedForFileAccess) {
                Debug.WriteLine("Volume already prepared for file access");
                return;
            }

            try {
                // Reset all values and scan the volume.
                IsDubious = false;
                Notes.Clear();
                ScanVolume();
                RawAccess.AccessLevel = GatedChunkAccess.AccessLvl.ReadOnly;
            } catch (Exception ex) {
                // Failed; reset for raw.
                AppHook.LogE("Unable to prepare file access: " + ex.Message);
                PrepareRawAccess();
                throw new DAException("Unable to prepare file access", ex);
            }
        }

        // IFileSystem
        public void PrepareRawAccess() {
            if (mOpenFiles.Count != 0) {
                throw new DAException("Cannot switch to raw access mode with files open");
            }

            if (mVolDirEntry != IFileEntry.NO_ENTRY) {
                // Invalidate the FileEntry tree.  If we don't do this the application could
                // try to use a retained object after it was switched back to file access.
                InvalidateFileEntries();
            }
            mVolDirEntry = IFileEntry.NO_ENTRY;
            VolUsage = null;
            IsDubious = false;
            RawAccess.AccessLevel = GatedChunkAccess.AccessLvl.Open;
        }

        /// <summary>
        /// Marks all file entry objects as invalid.
        /// </summary>
        private void InvalidateFileEntries() {
            Debug.Assert(mVolDirEntry != IFileEntry.NO_ENTRY);
            Gutenberg_FileEntry volDir = (Gutenberg_FileEntry)mVolDirEntry;
            if (!volDir.IsValid) {
                // Already done?  Shouldn't happen.
                return;
            }
            foreach (IFileEntry child in volDir) {
                Gutenberg_FileEntry entry = (Gutenberg_FileEntry)child;
                entry.SaveChanges();
                entry.Invalidate();
            }
            volDir.Invalidate();
        }

        /// <summary>
        /// Scans the contents of the volume directory.
        /// </summary>
        /// <exception cref="IOException">Disk access failure.</exception>
        /// <exception cref="DAException">Invalid filesystem.</exception>
        private void ScanVolume() {
            VolUsage = new VolumeUsage((int)(ChunkAccess.FormattedLength / SECTOR_SIZE));

            // TODO: T17 S6 appears to have a volume bitmap, but I haven't tried to figure
            // out the layout.  Once we figure that out we should use it to set the chunk
            // states in VolUsage.

            // Mark the bitmap as used by the system.  The directory file should mark T17 S7.
            VolUsage.SetUsage(TSToChunk(17, 6), IFileEntry.NO_ENTRY);

            mVolDirEntry = Gutenberg_FileEntry.ScanDirectory(this);

            // Check the results of the volume usage scan for problems.
            VolUsage.Analyze(out int markedUsed, out int unusedMarked,
                    out int notMarkedUsed, out int conflicts);

            AppHook.LogI("Usage counts: " + markedUsed + " in use, " +
                unusedMarked + " unused but marked, " +
                notMarkedUsed + " used but not marked, " +
                conflicts + " conflicts");
            if (conflicts != 0) {
                Notes.AddW("Found " + conflicts + " blocks in use by more than one file");
            }
            //Debug.WriteLine(VolUsage.DebugDump());

            // Since we're not parsing the volume bitmap, compute the free space remaining by
            // counting up the chunks not associated with a file that appear on track 3 or later.
            uint chunkT3S0 = TSToChunk(3, 0);
            int unusedCount = 0;
            for (uint chunk = chunkT3S0; chunk < VolUsage.Count; chunk++) {
                if (VolUsage.GetUsage(chunk) == null) {
                    unusedCount++;
                }
            }
            //Notes.AddI("There are " + unusedCount + " free sectors");
            FreeSpace = unusedCount * SECTOR_SIZE;
        }

        // IFileSystem
        public IMultiPart? FindEmbeddedVolumes() {
            return null;
        }

        // IFileSystem
        public void Format(string volumeName, int volumeNum, bool makeBootable) {
            throw new IOException("Not supported for this filesystem");
        }

        /// <summary>
        /// Performs general checks on file-access calls, throwing exceptions when something
        /// is amiss.  An exception here generally indicates an error in the program calling
        /// into the library.
        /// </summary>
        /// <param name="op">Short string describing the operation.</param>
        /// <param name="ientry">File being accessed.</param>
        /// <param name="wantWrite">True if this operation might modify the file.</param>
        /// <param name="part">Which part of the file we want access to.  Pass "Unknown" to
        ///   match on any part.</param>
        /// <exception cref="IOException">Various.</exception>
        /// <exception cref="ArgumentException">Various.</exception>
        private void CheckFileAccess(string op, IFileEntry ientry, bool wantWrite, FilePart part) {
            if (mDisposed) {
                throw new ObjectDisposedException("Object was disposed");
            }
            if (!IsPreppedForFileAccess) {
                throw new IOException("Filesystem object not prepared for file access");
            }
            if (wantWrite && IsReadOnly) {
                throw new IOException("Filesystem is read-only");
            }
            if (ientry == IFileEntry.NO_ENTRY) {
                throw new ArgumentException("Cannot operate on NO_ENTRY");
            }
            if (ientry.IsDamaged) {
                throw new IOException("File '" + ientry.FileName +
                    "' is too damaged to access");
            }
            if (ientry.IsDubious && wantWrite) {
                throw new IOException("File '" + ientry.FileName +
                    "' is too damaged to modify");
            }
            Gutenberg_FileEntry? entry = ientry as Gutenberg_FileEntry;
            if (entry == null || entry.FileSystem != this) {
                if (entry != null && entry.FileSystem == null) {
                    // Invalid entry; could be a deleted file, or from before a raw-mode switch.
                    throw new IOException("File entry is invalid");
                } else {
                    throw new FileNotFoundException("File entry is not part of this filesystem");
                }
            }
            if (part == FilePart.RsrcFork) {
                throw new IOException("File does not have a resource fork");
            }
            if (!mOpenFiles.CheckOpenConflict(entry, wantWrite, FilePart.Unknown)) {
                throw new IOException("File is already open; cannot " + op);
            }
        }

        // IFileSystem
        public IFileEntry GetVolDirEntry() {
            return mVolDirEntry;
        }

        // IFileSystem
        public DiskFileStream OpenFile(IFileEntry ientry, FileAccessMode mode, FilePart part) {
            if (part == FilePart.RawData) {
                part = FilePart.DataFork;   // do this before is-file-open check
            }
            CheckFileAccess("open", ientry, mode != FileAccessMode.ReadOnly, part);
            if (mode != FileAccessMode.ReadOnly && mode != FileAccessMode.ReadWrite) {
                throw new ArgumentException("Unknown file access mode " + mode);
            }
            if (part != FilePart.DataFork) {
                throw new ArgumentException("Requested file part not found");
            }

            Gutenberg_FileEntry entry = (Gutenberg_FileEntry)ientry;
            Gutenberg_FileDesc pfd = Gutenberg_FileDesc.CreateFD(entry, mode, part, false);
            mOpenFiles.Add(this, entry, pfd);
            return pfd;
        }

        /// <summary>
        /// Closes a file, removing it from our list.  Do not call this directly -- this is
        /// called from the file descriptor Dispose() call.
        /// </summary>
        /// <param name="ifd">Descriptor to close.</param>
        /// <exception cref="IOException">File descriptor was already closed, or was opened
        ///   by a different filesystem.</exception>
        internal void CloseFile(DiskFileStream ifd) {
            Gutenberg_FileDesc fd = (Gutenberg_FileDesc)ifd;
            if (fd.FileSystem != this) {
                // Should be impossible, though it could be null if previous close invalidated it.
                if (fd.FileSystem == null) {
                    throw new IOException("Invalid file descriptor");
                } else {
                    throw new IOException("File descriptor was opened by a different filesystem");
                }
            }

            // Find the file record, searching by descriptor.
            if (!mOpenFiles.RemoveDescriptor(ifd)) {
                throw new IOException("Open file record not found: " + fd);
            }
        }

        // IFileSystem
        public void CloseAll() {
            mOpenFiles.CloseAll();
        }

        // IFileSystem
        public IFileEntry CreateFile(IFileEntry idirEntry, string fileName, CreateMode mode) {
            CheckFileAccess("create", idirEntry, true, FilePart.Unknown);
            throw new IOException("Filesystem is read-only");
        }

        // IFileSystem
        public void AddRsrcFork(IFileEntry entry) {
            throw new IOException("Filesystem does not support resource forks");
        }

        // IFileSystem
        public void MoveFile(IFileEntry ientry, IFileEntry destDir, string newFileName) {
            CheckFileAccess("move", ientry, true, FilePart.Unknown);
            throw new IOException("Filesystem is read-only");
        }

        // IFileSystem
        public void DeleteFile(IFileEntry ientry) {
            CheckFileAccess("delete", ientry, true, FilePart.Unknown);
            throw new IOException("Filesystem is read-only");
        }
    }
}
