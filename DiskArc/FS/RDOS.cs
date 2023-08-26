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
using System.Text;

using CommonUtil;
using static DiskArc.Defs;
using static DiskArc.FileAnalyzer.DiskLayoutEntry;
using static DiskArc.IFileSystem;

namespace DiskArc.FS {
    /// <summary>
    /// RDOS filesystem implementation.
    /// </summary>
    public class RDOS : IFileSystem {
        public const int CAT_TRACK = 1;                 // disk catalog lives on track 1
        public const int CAT_ENTRY_LEN = 32;            // number of bytes in one catalog entry
        public const int MAX_FILENAME_LEN = 24;         // max length of a filename
        public const int MAX_FILE_LEN = 256 * SECTOR_SIZE;  // size is a single-byte sector count

        private static readonly byte[] SIG_NAME = Encoding.ASCII.GetBytes("<NAME>");

        private const string FILENAME_RULES =
            "1-24 characters, no double quotes or trailing spaces.";
        private static FSCharacteristics sCharacteristics = new FSCharacteristics(
            name: "RDOS",
            canWrite: false,
            isHierarchical: false,
            dirSep: IFileEntry.NO_DIR_SEP,
            hasResourceForks: false,
            fnSyntax: FILENAME_RULES,
            vnSyntax: string.Empty,
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

        public long FreeSpace { get { return CalcFreeSectors() * SECTOR_SIZE; } }

        public GatedChunkAccess RawAccess { get; }

        //
        // Implementation-specific.
        //

        /// <summary>
        /// RDOS "flavor" identifiers, based on ProDOS RDOS 1.1 definitions.
        /// </summary>
        public enum RDOSFlavor {
            Unknown = 0, RDOS32, RDOS33, RDOS3
        }

        /// <summary>
        /// Data source.  Contents may be shared in various ways.
        /// </summary>
        internal IChunkAccess ChunkAccess { get; private set; }

        /// <summary>
        /// Which "flavor" of RDOS this is.
        /// Only valid in file-access mode.
        /// </summary>
        public RDOSFlavor Flavor { get; private set; }

        /// <summary>
        /// Volume usage map.
        /// Only valid in file-access mode.
        /// </summary>
        internal VolumeUsage? VolUsage { get; private set; }

        /// <summary>
        /// Number of sectors in the disk catalog.  This is 11 for RDOS32/RDOS3, but might be 16
        /// for RDOS33.
        /// Only valid when in file-access mode.
        /// </summary>
        public int NumCatSectors => 11;

        /// <summary>
        /// Filesystem sector order.
        /// Only valid when in file-access mode.
        /// </summary>
        public SectorOrder FSOrder {
            get {
                if (Flavor == RDOSFlavor.RDOS32 || Flavor == RDOSFlavor.RDOS3) {
                    return SectorOrder.Physical;
                } else if (Flavor == RDOSFlavor.RDOS33) {
                    return SectorOrder.ProDOS_Block;
                } else {
                    Debug.Assert(false, "flavor not set");
                    return SectorOrder.Unknown;
                }
            }
        }

        /// <summary>
        /// Total sectors present in the filesystem.
        /// Only valid when in file-access mode.
        /// </summary>
        public int TotalSectors {
            get {
                if (Flavor == RDOSFlavor.RDOS33) {
                    return 16 * 35;
                } else {
                    return 13 * 35;
                }
            }
        }

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

            // Read T1S0 in to see if it looks like an RDOS catalog.  Because it's in sector 0
            // this won't tell us anything about the disk image sector order, or whether this
            // is RDOS33 vs RDOS3.
            chunkSource.ReadSector(CAT_TRACK, 0, sctBuf, 0);

            char[] testNameChars = new char[MAX_FILENAME_LEN];
            for (int i = 0; i < MAX_FILENAME_LEN; i++) {
                if (sctBuf[i] < 0xa0 || sctBuf[i] > 0xdf) {
                    return TestResult.No;       // not a high-ASCII upper-case letter or symbol
                }
                testNameChars[i] = (char)(sctBuf[i] & 0x7f);
            }
            string testName = new string(testNameChars);
            if (!testName.Contains("RDOS ") && !testName.Contains("SSI ")) {
                return TestResult.No;
            }

            // The catalog code lives on T1S12 on 13-sector disks, and T0S1 on 16-sector disks.
            // Look for the string "<NAME>".
            RDOSFlavor flavor = DetectFlavor(chunkSource);
            if (flavor == RDOSFlavor.Unknown) {
                return TestResult.No;
            }

            return TestResult.Yes;
        }

        /// <summary>
        /// Detects which flavor of RDOS lives on this disk image by scanning the contents.
        /// </summary>
        private static RDOSFlavor DetectFlavor(IChunkAccess chunks) {
            byte[] sctBuf = new byte[SECTOR_SIZE];
            bool foundMismatch = false;

            // RDOS33: low-ASCII string at +$98 in T0S1.
            try {
                chunks.ReadSector(0, 1, sctBuf, 0, SectorOrder.ProDOS_Block);
                for (int i = 0; i < SIG_NAME.Length; i++) {
                    if (sctBuf[0x98 + i] != SIG_NAME[i]) {
                        foundMismatch = true;
                        break;
                    }
                }
                if (!foundMismatch) {
                    return RDOSFlavor.RDOS33;
                }
            } catch (BadBlockException) {
                // Expected result on 13-sector game-save disks, because the 13-sector formatter
                // doesn't write the data field.
            }

            // RDOS32/RDOS3: high-ASCII string at +$a2 in T1S12.
            foundMismatch = false;
            chunks.ReadSector(1, 12, sctBuf, 0, SectorOrder.Physical);
            for (int i = 0; i < SIG_NAME.Length; i++) {
                if (sctBuf[0xa2 + i] != (byte)(SIG_NAME[i] | 0x80)) {
                    foundMismatch = true;
                    break;
                }
            }
            if (!foundMismatch) {
                if (chunks.NumSectorsPerTrack == 13) {
                    return RDOSFlavor.RDOS32;
                } else {
                    return RDOSFlavor.RDOS3;
                }
            }

            return RDOSFlavor.Unknown;
        }

        // Delegate: returns true if the size (in bytes) is valid for this filesystem.
        public static bool IsSizeAllowed(long size) {
            return size == 35 * 13 * SECTOR_SIZE || size == 35 * 16 * SECTOR_SIZE;
        }

        public RDOS(IChunkAccess chunks, AppHook appHook) {
            Debug.Assert(chunks.HasSectors);
            ChunkAccess = chunks;
            AppHook = appHook;

            RawAccess = new GatedChunkAccess(chunks);
            mVolDirEntry = IFileEntry.NO_ENTRY;
        }

        public override string ToString() {
            string id = mVolDirEntry == IFileEntry.NO_ENTRY ? "raw" : mVolDirEntry.FileName;
            return "[RDOS (" + id + ")]";
        }

        // IDisposable generic finalizer.
        ~RDOS() {
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
                AppHook.LogW("Attempting to dispose of RDOS object twice");
                return;
            }
            AppHook.LogD("RDOS.Dispose");
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
            Flavor = RDOSFlavor.Unknown;
            IsDubious = false;
            RawAccess.AccessLevel = GatedChunkAccess.AccessLvl.Open;
        }

        /// <summary>
        /// Marks all file entry objects as invalid.
        /// </summary>
        private void InvalidateFileEntries() {
            Debug.Assert(mVolDirEntry != IFileEntry.NO_ENTRY);
            RDOS_FileEntry volDir = (RDOS_FileEntry)mVolDirEntry;
            if (!volDir.IsValid) {
                // Already done?  Shouldn't happen.
                return;
            }
            foreach (IFileEntry child in volDir) {
                RDOS_FileEntry entry = (RDOS_FileEntry)child;
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
            // Start by detecting which flavor of RDOS this is.  We can't do this in "raw" mode
            // because we can't distinguish RDOS33 from RDOS3 by disk format.
            Flavor = DetectFlavor(ChunkAccess);
            if (Flavor == RDOSFlavor.Unknown) {
                throw new DAException("RDOS not recognized");
            }

            // Create volume usage map.  The boot/directory blocks are covered by a directory
            // entry, so no need for special treatment.
            VolUsage = new VolumeUsage(TotalSectors);

            // Scan the full catalog.
            mVolDirEntry = RDOS_FileEntry.ScanDirectory(this);

            // Check the results of the volume usage scan for problems.
            VolUsage.Analyze(out int markedUsed, out int unusedMarked,
                    out int notMarkedUsed, out int conflicts);

            AppHook.LogI("Usage counts: " + markedUsed + " in use, " +
                unusedMarked + " unused but marked, " +
                notMarkedUsed + " used but not marked, " +
                conflicts + " conflicts");

            // There's no volume bitmap, so certain things aren't possible.
            Debug.Assert(unusedMarked == 0);
            Debug.Assert(notMarkedUsed == 0);

            if (conflicts != 0) {
                Notes.AddW("Found " + conflicts + " blocks in use by more than one file");
            }

            //Debug.WriteLine(VolUsage.DebugDump());
        }

        /// <summary>
        /// Calculates the total number of free sectors.
        /// </summary>
        private int CalcFreeSectors() {
            if (!IsPreppedForFileAccess) {
                return -1;
            }
            if (IsDubious) {
                return 0;       // not safe to traverse (e.g. a sector count could be zero)
            }
            int freeBlocks = 0;
            int nextStart = 0;
            foreach (IFileEntry ientry in mVolDirEntry) {
                RDOS_FileEntry entry = (RDOS_FileEntry)ientry;
                freeBlocks += entry.StartIndex - nextStart;
                nextStart = entry.StartIndex + entry.SectorCount;
            }
            freeBlocks += TotalSectors - nextStart;
            return freeBlocks;
        }

        /// <summary>
        /// Converts a file sector index into track/sector.
        /// </summary>
        /// <param name="sectorNum">Sector index number.</param>
        /// <param name="trk">Result: track (0-34).</param>
        /// <param name="sct">Result: sector (0-12 or 0-15).</param>
        internal void SectorIndexToTrackSector(uint sectorNum, out uint trk, out uint sct) {
            uint sctPerTrk = (Flavor == RDOSFlavor.RDOS33 ? 16U : 13U);
            trk = sectorNum / sctPerTrk;
            sct = sectorNum % sctPerTrk;
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
            RDOS_FileEntry? entry = ientry as RDOS_FileEntry;
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

            RDOS_FileEntry entry = (RDOS_FileEntry)ientry;
            RDOS_FileDesc pfd = RDOS_FileDesc.CreateFD(entry, mode, part);
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
            RDOS_FileDesc fd = (RDOS_FileDesc)ifd;
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
