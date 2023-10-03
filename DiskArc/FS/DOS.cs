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
using DiskArc.Multi;
using static DiskArc.Defs;
using static DiskArc.FileAnalyzer.DiskLayoutEntry;
using static DiskArc.IFileSystem;

namespace DiskArc.FS {
    /// <summary>
    /// DOS filesystem implementation, for DOS 3.3 and 3.2.
    /// </summary>
    /// <remarks>
    /// <para>DOS filesystems require special handling, primarily because the length of a file
    /// is not explicitly stored in the directory entry.  I/A/B files have a 16-bit length embedded
    /// in the first few bytes of the file.  Files opened in "raw data" mode are treated equally
    /// regardless of type, and always have an EOF that is a multiple of 256.</para>
    /// <para>Files are initially created with the 'S' type, which has no formal definition.
    /// If you want to write a BASIC or binary data file (types I/A/B) in non-raw mode, you
    /// must change the file's type before opening it.  Changing the file type will cause the
    /// file's length to be re-evaluated, which may require scanning the entire file.</para>
    /// <para>DOS text files are encoded in high ASCII, with CR indicating end-of-line.  Sequential
    /// text files end when the first $00 byte is encountered.</para>
    /// </remarks>
    public class DOS : IFileSystem {
        public const int MAX_TRACKS = 50;               // VTOC limitation
        public const int MAX_SECTORS = 32;              // VTOC limitation
        public const int DEFAULT_VOL_NUM = 254;
        public const int DEFAULT_VTOC_TRACK = 17;
        public const int DEFAULT_VTOC_SECTOR = 0;
        public const int FIRST_TS_OFFSET = 0x0c;        // first T/S entry in T/S list starts here
        public const int MAX_TS_PER_TSLIST = 122;       // total T/S pairs in a T/S list sector
        public const int MAX_VOL_SIZE = MAX_TRACKS * MAX_SECTORS * SECTOR_SIZE; // 400KB
        public const int CATALOG_ENTRY_START = 0x0b;    // start offset of catalog entries
        public const int CATALOG_ENTRY_LEN = 35;
        public const int MAX_FILE_NAME_LEN = 30;
        public const int SLOT_DELETED = 0xff;
        public const int SLOT_UNUSED = 0x00;

        // The maximum length of a sparse file is limited only by the number of sectors that
        // can be used to hold T/S lists.  Setting it equal to the ProDOS limit seems reasonable.
        // (A file with one data block at the very end would fill 539 sectors.)
        public const int MAX_FILE_LEN = 256 * 256 * 256;
        public const int MAX_IAB_FILE_LEN = 65535;   // max length of cooked I/A/B file

        internal const int MIN_SECT_PER_TRACK = 13;
        internal const int MIN_VOL_SIZE =
            (DEFAULT_VTOC_TRACK + 1) * MIN_SECT_PER_TRACK * SECTOR_SIZE;
        internal const int MAX_CATALOG_SECTORS = 31;    // two tracks, or one 32-sector track
        internal const int MAX_TS_CHAIN = 540;          // 16-bit TS sector offset would roll over

        private const string FILENAME_RULES =
            "1-30 upper-case letters, numbers, and symbols.  Must start with a letter, and must " +
            "not include ','.";
        private const string VOLNAME_RULES =
            "Volume number, 0-254.";
        private static FSCharacteristics sCharacteristics = new FSCharacteristics(
                name: "DOS 3.2/3.3",
                canWrite: true,
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

        public bool IsReadOnly { get { return ChunkAccess.IsReadOnly || IsDubious; } }

        public bool IsDubious { get; internal set; }

        public Notes Notes { get; } = new Notes();

        public GatedChunkAccess RawAccess { get; private set; }

        public long FreeSpace {
            get {
                if (VTOC == null) {
                    return -1;
                }
                return VTOC.CalcFreeSectors() * SECTOR_SIZE;
            }
        }

        //
        // Implementation-specific.
        //

        /// <summary>
        /// Data source.  Contents may be shared in various ways.
        /// </summary>
        internal IChunkAccess ChunkAccess { get; private set; }

        /// <summary>
        /// List of embedded volumes.
        /// </summary>
        private IMultiPart? mEmbeds;

        /// <summary>
        /// Application-specified options and message logging.
        /// </summary>
        internal AppHook AppHook { get; set; }

        /// <summary>
        /// "Fake" volume directory entry, used to hold catalog entries.
        /// </summary>
        private IFileEntry mVolDirEntry;

        /// <summary>
        /// List of open files.
        /// </summary>
        private OpenFileTracker mOpenFiles = new OpenFileTracker();

        /// <summary>
        /// Disk VTOC and volume usage.
        /// Will be null when the filesystem is in raw-access mode.
        /// </summary>
        internal DOS_VTOC? VTOC { get; private set; }

        /// <summary>
        /// DOS volume number, from VTOC.
        /// </summary>
        public byte VolumeNum { get { return VTOC == null ? (byte)0 : VTOC.VolumeNum; } }

        /// <summary>
        /// Number of tracks on the disk.  Based on file geometry, not VTOC.
        /// </summary>
        /// <remarks>
        /// This is needed for raw-access mode, when the VTOC is unavailable.
        /// </remarks>
        public uint TotalTrks { get { return ChunkAccess.NumTracks; } }

        /// <summary>
        /// Number of sectors on the disk.  Based on file geometry, not VTOC.
        /// </summary>
        public uint TotalScts { get { return ChunkAccess.NumSectorsPerTrack; } }

        /// <summary>
        /// True if the object is in file-access mode.
        /// </summary>
        private bool IsPreppedForFileAccess { get { return VTOC != null; } }


        // Define a set of standard disk configurations.  Anything with a size that doesn't
        // match an entry in the list will be ignored.
        //
        // All sizes created by DOS MASTER are supported.
        private struct ExpectedSize {
            public int Tracks { get; }
            public int Sectors { get; }
            public int Size { get; }
            public ExpectedSize(int tracks, int sectors) {
                Tracks = tracks;
                Sectors = sectors;
                Size = tracks * sectors * SECTOR_SIZE;
            }
        }
        private static ExpectedSize[] sExpectedSizes = {
            new ExpectedSize(35, 13),       // standard 5.25" floppy, 13 sector (113KB)
            new ExpectedSize(35, 16),       // standard 5.25" floppy, 16 sector (140KB)
            new ExpectedSize(40, 16),       // 40-track 5.25" floppy (160KB)
            new ExpectedSize(80, 16),       // 80-track 5.25" floppy (320KB)
            new ExpectedSize(50, 16),       // 50-track, 16 sector embedded volume (200KB)
            new ExpectedSize(50, 32),       // 50-track, 32 sector embedded volume (400KB)
        };

        /// <summary>
        /// Calculates the expected track/sector geometry of a disk image, based on the total size.
        /// </summary>
        /// <param name="length">Length of disk image, in bytes.</param>
        /// <param name="expectedTrks">Result: expected number of tracks.</param>
        /// <param name="expectedScts">Result: expected number of sectors.</param>
        /// <returns>True if geometry was found, false if not.</returns>
        public static bool CalcGeometry(long length, out int expectedTrks, out int expectedScts) {
            foreach (ExpectedSize exp in sExpectedSizes) {
                if (length == exp.Size) {
                    expectedTrks = exp.Tracks;
                    expectedScts = exp.Sectors;
                    return true;
                }
            }
            expectedTrks = expectedScts = -1;
            return false;
        }

        /// <summary>
        /// Gets the track/sector to use for the VTOC.  Normally this will be the default T17 S0,
        /// but we want to allow the possibility of overriding that.
        /// </summary>
        /// <param name="appHook">Application hook reference.</param>
        /// <param name="trk">Result: VTOC track number.</param>
        /// <param name="sct">Result: VTOC sector number.</param>
        private static void GetVTOCLocation(AppHook appHook, out byte trk, out byte sct) {
            trk = (byte)appHook.GetOptionInt(DAAppHook.DOS_VTOC_TRACK, DEFAULT_VTOC_TRACK);
            sct = (byte)appHook.GetOptionInt(DAAppHook.DOS_VTOC_SECTOR, DEFAULT_VTOC_SECTOR);
        }


        // Delegate: test image to see if it's ours.
        public static TestResult TestImage(IChunkAccess chunkSource, AppHook appHook) {
            if (!chunkSource.HasSectors) {
                return TestResult.No;
            }
            GetVTOCLocation(appHook, out byte vtocTrack, out byte vtocSector);

            byte[] dataBuf = new byte[SECTOR_SIZE];
            chunkSource.ReadSector(vtocTrack, vtocSector, dataBuf, 0);
            if (!DOS_VTOC.ValidateVTOC(chunkSource, dataBuf)) {
                return TestResult.No;
            }
            Debug.Assert(dataBuf != null);

            // This looks like a DOS disk, but because the VTOC is in sector 0, we don't yet
            // know whether we're using the correct sector ordering.  We need to try to walk
            // the directory chain and see how far we get.  Some disks deliberately have long
            // or short directories, so what matters is how many sectors we find relative to
            // other orderings.
            byte catTrk = dataBuf[0x01];
            byte catSct = dataBuf[0x02];
            byte numTrks = dataBuf[0x34];
            byte numScts = dataBuf[0x35];
            int goodCount = 0;
            int iterCount = 0;
            while (catTrk != 0 && iterCount < MAX_CATALOG_SECTORS) {
                if (catTrk == vtocTrack && catSct == vtocSector) {
                    // Weird -- catalog sector points back at VTOC?
                    break;
                }
                if (catTrk >= numTrks || catSct >= numScts) {
                    // Invalid track/sector.
                    break;
                }
                try {
                    chunkSource.ReadSector(catTrk, catSct, dataBuf, 0);
                } catch (BadBlockException) {
                    // Skip this one.
                    Debug.WriteLine("Unable to read T" + catTrk + " S" + catSct);
                    if (catSct > 1) {
                        catSct--;       // try the next in descending sequence
                    } else {
                        catTrk = 0;     // give up
                    }
                    continue;
                }

                // Peek at file entry #1.  The filename is allowed to hold anything, so that's
                // no help, but the track number field should hold $00, $ff, or a valid track.
                // And if the track number is valid, then so should the sector number.
                byte tsTrk = dataBuf[0x0b];
                byte tsSct = dataBuf[0x0c];
                if (tsTrk == 0x00 || tsTrk == 0xff) {
                    // Unused or deleted; this is fine.
                } else if (tsTrk < numTrks && tsSct < numScts) {
                    // This could be a valid entry.
                } else {
                    // Nope.
                    break;
                }

                // Get next track/sector in chain.
                byte nextTrk = dataBuf[0x01];
                byte nextSct = dataBuf[0x02];
                if (nextTrk >= numTrks || nextSct >= numScts) {
                    // Invalid link to next sector.
                    break;
                }

                // Looks like a winner.  Add it to the tally.  Double the score if the sectors
                // are in descending order.
                if (nextTrk == catTrk && nextSct == catSct - 1) {
                    goodCount += 2;
                } else {
                    goodCount++;
                }
                catTrk = nextTrk;
                catSct = nextSct;
                iterCount++;
            }
            if (iterCount > MAX_CATALOG_SECTORS) {
                // Infinite loop.  Known to be caused by EOL conversions.
                Debug.WriteLine("Found infinite loop in DOS catalog, rejecting");
                return TestResult.No;
            }

            // Scores for a standard disk in DOS order:
            //  - DOS order: 29
            //  - Physical order: 15
            //  - CPM order: 7
            //  - ProDOS order: 3
            // Scores for a standard disk in ProDOS order:
            //  - ProDOS order: 29
            //  - Physical order: 5
            //  - DOS order: 3
            //  - CPM order: 3
            // Scores for a standard disk in physical order:
            //  - Physical order: 29
            //  - DOS order: 15
            //  - ProDOS order: 5
            //  - CPM order: 2
            //
            // Some "specialty" disks have a valid VTOC and one good catalog sector, resulting
            // in a score of 1.  Return a "barely" result so that we'll use it only if we can't
            // find anything better.  It's not enough to determine the sector order, but if we've
            // established that some other way (e.g. it's an embedded volume) then it's enough.
            //
            // Some hybrid disks use a short catalog track, with a score of 11/4/2/2.
            //
            // 13-sector disks, which are always "physical" order, will get a score of 23 if
            // they have a full catalog track.
            Debug.WriteLine("DOS order=" + chunkSource.FileOrder + " goodCount=" + goodCount);
            if (goodCount == 0) {
                return TestResult.No;
            } else if (goodCount == 1) {
                return TestResult.Barely;
            } else if (goodCount < 11) {
                return TestResult.Maybe;
            } else if (goodCount < 29) {
                return TestResult.Good;
            } else {
                return TestResult.Yes;
            }
        }

        // Delegate: returns true if the size (in bytes) is valid for this filesystem.
        public static bool IsSizeAllowed(long size) {
            if (size % SECTOR_SIZE != 0) {
                return false;       // must be sectors
            }
            // Use a whitelist to limit the possibilities.
            foreach (ExpectedSize exp in sExpectedSizes) {
                if (size == exp.Size) {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Constructor.
        /// </summary>
        public DOS(IChunkAccess dataSource, AppHook appHook) {
            Debug.Assert(dataSource != null && appHook != null);
            ChunkAccess = dataSource;
            AppHook = appHook;
            if (!dataSource.HasSectors) {
                throw new ArgumentException("Chunk source does not have sectors");
            }

            RawAccess = new GatedChunkAccess(dataSource);

            mVolDirEntry = IFileEntry.NO_ENTRY;
        }

        public override string ToString() {
            string id = VTOC == null ? "(raw)" : "vol #" + VTOC.VolumeNum.ToString("D3");
            return "[DOS " + id + "]";
        }


        // IDisposable generic finalizer.
        ~DOS() {
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
                AppHook.LogW("Attempting to dispose of DOS object twice");
                return;
            }
            if (!disposing) {
                // This is a GC finalization.  We can't know if the objects we have references
                // to have already been finalized, so there's nothing we can do but complain.
                AppHook.LogW("GC disposing of filesystem object " + this);
                if (mOpenFiles.Count != 0) {
                    AppHook.LogW("DOS FS finalized while " + mOpenFiles.Count + " files open");
                }
                return;
            }

            AppHook.LogD("DOS.Dispose(" + disposing + "): Vol" + VolumeNum);

            // This can happen easily if we have the filesystem in a "using" block and
            // something throws with a file open.  Post a warning and close all files.
            if (mOpenFiles.Count != 0) {
                AppHook.LogI("DOS FS disposed with " + mOpenFiles.Count + " files open; closing");
                CloseAll();
            }

            VTOC?.Flush();

            if (IsPreppedForFileAccess) {
                // Invalidate all associated file entry objects.
                InvalidateFileEntries();
                mEmbeds?.Dispose();
                mEmbeds = null;
            } else {
                Debug.Assert(mEmbeds == null);
            }

            RawAccess.AccessLevel = GatedChunkAccess.AccessLvl.Closed;
            mDisposed = true;
        }

        // IFileSystem
        public void Flush() {
            mOpenFiles.FlushAll();
            VTOC?.Flush();
            // TODO: should we do SaveChanges() across all entries, including those not open?
        }

        public uint TSToChunk(uint trk, uint sct) {
            return trk * TotalScts + sct;
        }
        public uint ChunkToTrk(uint chunk) {
            return chunk / TotalScts;
        }
        public uint ChunkToSct(uint chunk) {
            return chunk % TotalScts;
        }

        /// <summary>
        /// Validates the track/sector numbers.  Track 0 is considered invalid.
        /// </summary>
        internal bool IsSectorValid(uint trk, uint sct) {
            return trk > 0 && trk < TotalTrks && sct < TotalScts;
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
                ScanVolume(doScan);
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
            if (mEmbeds != null) {
                foreach (Partition part in mEmbeds) {
                    if (part.FileSystem != null) {
                        part.FileSystem.PrepareRawAccess();     // will fail if files are open
                    }
                }
            }

            // This may be called by PrepareFileAccess() when something fails part-way through,
            // so expect that things may be partially initialized.

            if (VTOC != null && VTOC.IsDirty) {
                AppHook.LogI("PrepareRawAccess flushing VTOC");
                VTOC.Flush();
            }
            if (mVolDirEntry != IFileEntry.NO_ENTRY) {
                // Invalidate the FileEntry tree.  If we don't do this the application could
                // try to use a retained object after it was switched back to file access.
                InvalidateFileEntries();
            }

            mEmbeds?.Dispose();
            mEmbeds = null;

            VTOC = null;
            mVolDirEntry = IFileEntry.NO_ENTRY;
            IsDubious = false;
            RawAccess.AccessLevel = GatedChunkAccess.AccessLvl.Open;
        }

        /// <summary>
        /// Marks all file entry objects as invalid.
        /// </summary>
        private void InvalidateFileEntries() {
            Debug.Assert(mVolDirEntry != IFileEntry.NO_ENTRY);
            DOS_FileEntry dosVolDir = (DOS_FileEntry)mVolDirEntry;
            if (!dosVolDir.IsValid) {
                // Already done?  Shouldn't happen.
                return;
            }
            foreach (IFileEntry child in dosVolDir) {
                DOS_FileEntry entry = (DOS_FileEntry)child;
                if (entry.IsDirty) {
                    AppHook.LogI("Dispose flushing changes for file entry " + entry);
                    // In theory we could have an I/O error here, which could cascade and
                    // cause a Dispose() to generally fail.  In practice we should have failed
                    // already unless the error is transient or only affects writing.
                    entry.SaveChanges();
                }
                entry.Invalidate();
            }
            dosVolDir.Invalidate();
        }

        /// <summary>
        /// Scans the contents of the catalog.
        /// </summary>
        /// <param name="doScan">If set, examine the structure of all files.</param>
        /// <exception cref="IOException">Disk access failure.</exception>
        /// <exception cref="DAException">Invalid filesystem.</exception>
        private void ScanVolume(bool doScan) {
            GetVTOCLocation(AppHook, out byte vtocTrack, out byte vtocSector);

            // Re-validate the VTOC, in case it has been edited since we initially checked
            // the volume.
            byte[] vtocBuf = new byte[SECTOR_SIZE];
            ChunkAccess.ReadSector(vtocTrack, vtocSector, vtocBuf, 0);
            if (!DOS_VTOC.ValidateVTOC(ChunkAccess, vtocBuf)) {
                throw new DAException("VTOC not valid");
            }

            // Looks good, allocate a VTOC object.
            VTOC = new DOS_VTOC(ChunkAccess, vtocTrack, vtocSector, vtocBuf);

            // Scan the full catalog, creating a very shallow tree.
            mVolDirEntry = DOS_FileEntry.ScanCatalog(this, VTOC, doScan);

            // Assign "system" usage to the DOS tracks, if nothing else has claimed them.  This
            // should always be the case for track 0, but some disks don't have a DOS image,
            // and some that do free up a couple of sectors on track 2 that aren't actually
            // needed to hold DOS.
            VolumeUsage vu = VTOC.VolUsage;
            for (byte dosTrk = 0; dosTrk < 3; dosTrk++) {
                for (byte dosSct = 0; dosSct < TotalScts; dosSct++) {
                    uint chunk = TSToChunk(dosTrk, dosSct);
                    if (VTOC.IsSectorInUse(dosTrk, dosSct) && vu.GetUsage(chunk) == null) {
                        vu.SetUsage(chunk, IFileEntry.NO_ENTRY);
                    } else if (dosTrk == 0) {
                        // We can't allocate files on track 0, but it should still be marked
                        // as in-use in the bitmap.
                        Notes.AddW("Track 0 sector " + dosSct + " is marked free");
                    }
                }
            }

            //
            // Check the results of the volume usage scan for problems.
            //

            vu.Analyze(out int markedUsed, out int unusedMarked,
                    out int notMarkedUsed, out int conflicts);

            AppHook.LogI("Usage counts: " + markedUsed + " in use, " +
                unusedMarked + " unused but marked, " +
                notMarkedUsed + " used but not marked, " +
                conflicts + " conflicts");

            if (notMarkedUsed != 0) {
                // Danger!
                Notes.AddE("Found " + notMarkedUsed + " used blocks that are marked free");
                IsDubious = true;
            }
            if (conflicts != 0) {
                // Individual files are marked "dubious", and can't be modified.
                Notes.AddW("Found " + conflicts + " blocks in use by more than one file");
                //IsDamaged = true;
            }

            if (unusedMarked != 0) {
                bool doWarnUnused = AppHook.GetOptionBool(DAAppHook.WARN_MARKED_BUT_UNUSED, false);
                if (doWarnUnused) {
                    Notes.AddW("Found " + unusedMarked + " unused sectors that are marked used");
                } else {
                    Notes.AddI("Found " + unusedMarked + " unused sectors that are marked used");
                }
            }
        }

        // IFileSystem
        public IMultiPart? FindEmbeddedVolumes() {
            if (mEmbeds != null) {
                return mEmbeds;
            }
            if (!IsPreppedForFileAccess) {
                throw new IOException("Must be in file access mode");
            }
            // We always do a full file scan, so no need to worry about incomplete VolumeUsage.
            mEmbeds = DOS_Hybrid.FindEmbeddedVolumes(this, AppHook);
            Debug.Assert(mEmbeds == null || mEmbeds.Count > 0);
            return mEmbeds;
        }

        // IFileSystem
        public void Format(string volumeName, int volumeNum, bool makeBootable) {
            // We only reject the call if the underlying storage is read-only.  If the filesystem
            // is read-only because of file damage, reformatting it is fine.
            if (ChunkAccess.IsReadOnly) {
                throw new IOException("Can't format read-only data");
            }
            if (IsPreppedForFileAccess) {
                throw new IOException("Must be in raw access mode");
            }
            if (!IsSizeAllowed(ChunkAccess.FormattedLength)) {
                throw new ArgumentException("Invalid length for DOS volume");
            }

            // Validate volume number.  Use default if invalid.  This allows the caller to pass -1
            // as an "I don't care" value, and have it work for all possible filesystems.
            if (volumeNum != (byte)volumeNum) {
                Debug.WriteLine("Invalid volume num for Format, using default");
                volumeNum = DEFAULT_525_VOLUME_NUM;
            }

            if (makeBootable) {
                if (TotalScts == 13) {
                    WriteDOSTracks(DOS_OSImage.DOS_321_Tracks);
                } else if (TotalScts == 16 && TotalTrks <= 50) {
                    WriteDOSTracks(DOS_OSImage.DOS_33_Tracks);
                } else {
                    // We don't have a boot image for 80-track or 32-sector disks.  We'll mark
                    // the DOS tracks as in-use so a custom version can be installed.
                }
            } else {
                // TODO(maybe): write a T0S0 that prints "this disk is not bootable"... should
                //   probably make it an explicit option, in case somebody really just wants
                //   track 0 to be empty.
            }

            // Create a VTOC.
            GetVTOCLocation(AppHook, out byte vtocTrack, out byte vtocSector);
            byte[] vtoc = new byte[SECTOR_SIZE];
            vtoc[0x00] = (TotalScts == 13) ? (byte)2 : (byte)4;
            vtoc[0x01] = vtocTrack;                 // track 17
            vtoc[0x02] = (byte)(TotalScts - 1);     // sector 12 or 15
            vtoc[0x03] = (TotalScts == 13) ? (byte)2 : (byte)3;
            vtoc[0x06] = (byte)volumeNum;
            vtoc[0x27] = MAX_TS_PER_TSLIST;         // 122
            vtoc[0x30] = (byte)(vtocTrack - 1);
            vtoc[0x31] = 0xff;
            vtoc[0x34] = (byte)TotalTrks;
            vtoc[0x35] = (byte)TotalScts;
            vtoc[0x36] = 0x00;
            vtoc[0x37] = 0x01;

            // Mark everything as free.
            for (byte i = 0; i < TotalTrks; i++) {
                DOS_VTOC.MarkTrack(vtoc, i, false, TotalTrks, TotalScts);
            }
            // Mark tracks 0 and 17 as in use.  If bootable, do the same for tracks 1 and 2.
            // (We can probably leave sectors 16-31 free on a 32-sector disk, but since we're
            // not writing a DOS image for those yet I can't be sure.)
            DOS_VTOC.MarkTrack(vtoc, 0, true, TotalTrks, TotalScts);
            if (makeBootable) {
                DOS_VTOC.MarkTrack(vtoc, 1, true, TotalTrks, TotalScts);
                DOS_VTOC.MarkTrack(vtoc, 2, true, TotalTrks, TotalScts);
            }
            DOS_VTOC.MarkTrack(vtoc, vtocTrack, true, TotalTrks, TotalScts);
            // Write the VTOC to disk.
            ChunkAccess.WriteSector(vtocTrack, vtocSector, vtoc, 0);

            // Create catalog sectors.
            byte[] catData = new byte[SECTOR_SIZE];
            catData[0x01] = vtocTrack;
            for (byte sct = (byte)(TotalScts - 1); sct > 0; sct--) {
                if (sct == 1) {
                    // Sector 1 is the end of the catalog.
                    catData[0x01] = 0;
                }
                catData[0x02] = (byte)(sct - 1);
                ChunkAccess.WriteSector(vtocTrack, sct, catData, 0);
            }

            // Write everything to disk and reset state.
            PrepareRawAccess();
        }

        /// <summary>
        /// Writes tracks 0-2 onto the disk image.
        /// </summary>
        private void WriteDOSTracks(byte[] dataSrc) {
            int offset = 0;
            for (byte trk = 0; trk < 3; trk++) {
                for (byte sct = 0; sct < TotalScts; sct++) {
                    ChunkAccess.WriteSector(trk, sct, dataSrc, offset);
                    offset += SECTOR_SIZE;
                }
            }
        }

        #region File Access

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
            DOS_FileEntry? entry = ientry as DOS_FileEntry;
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
            if (!IsPreppedForFileAccess) {
                throw new IOException("Filesystem object not prepared for file access");
            }
            return mVolDirEntry;
        }

        // IFileSystem
        public void AddRsrcFork(IFileEntry entry) {
            throw new IOException("Filesystem does not support resource forks");
        }

        // IFileSystem
        public DiskFileStream OpenFile(IFileEntry ientry, FileAccessMode mode, FilePart part) {
            CheckFileAccess("open", ientry, mode != FileAccessMode.ReadOnly, part);
            if (mode != FileAccessMode.ReadOnly &&
                    mode != FileAccessMode.ReadWrite) {
                throw new ArgumentException("Unknown file access mode " + mode);
            }
            if (part != FilePart.DataFork && part != FilePart.RawData) {
                throw new ArgumentException("Requested file part not found");
            }

            DOS_FileEntry entry = (DOS_FileEntry)ientry;
            DOS_FileDesc pfd = DOS_FileDesc.CreateFD(entry, mode, part, false);
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
            DOS_FileDesc fd = (DOS_FileDesc)ifd;
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

            // Flush any pending changes.  The VTOC ref could be null if the descriptor
            // close is happening as part of finalization.
            VTOC?.Flush();
        }

        // IFileSystem
        public void CloseAll() {
            mOpenFiles.CloseAll();

            // This is probably being called as part of closing the filesystem, so make sure
            // pending changes have been flushed.  In normal operation this shouldn't be needed,
            // because changes are flushed when files are closed.
            if (VTOC != null && VTOC.IsDirty) {
                AppHook.LogI("CloseAll flushing VTOC changes");
                VTOC.Flush();
            }
        }

        // IFileSystem
        public IFileEntry CreateFile(IFileEntry idirEntry, string fileName, CreateMode mode) {
            CheckFileAccess("create", idirEntry, true, FilePart.Unknown);
            Debug.Assert(idirEntry == mVolDirEntry);
            if (mode != CreateMode.File) {
                throw new ArgumentException("Invalid DOS creation mode: " + mode);
            }
            DOS_FileEntry dirEntry = (DOS_FileEntry)idirEntry;
            if (fileName == null || !DOS_FileEntry.IsFileNameValid(fileName)) {
                throw new ArgumentException("Invalid filename '" + fileName + "'");
            }

            Debug.Assert(VTOC != null);

            // Check for an entry with a duplicate filename in the list of children.
            foreach (IFileEntry entry in dirEntry) {
                if (entry.CompareFileName(fileName) == 0) {
                    throw new IOException("A file with that name already exists");
                }
            }

            // Find the first empty slot in the catalog.  If the catalog is full, this will
            // throw an exception.
            byte[] catBuf = FindCatalogSlot(out byte catTrk, out byte catSct, out int catOffset,
                out int catIndex);

            DOS_FileEntry newEntry = new DOS_FileEntry(this);

            // Allocate a sector for the track/sector list.  If we're unable to write to it,
            // we'll want to release it before returning.
            VTOC.AllocSector(newEntry, out byte tsTrk, out byte tsSct);
            try {
                // The track/sector list has no "next" link, a sector offset of zero, and no
                // allocated sectors.  So at this point, it's entirely zero.
                ChunkAccess.WriteSector(tsTrk, tsSct, DOS_FileDesc.sZeroSector, 0);
            } catch {
                VTOC.MarkSectorUnused(tsTrk, tsSct);
                throw;
            }

            // Fill in the directory entry.
            catBuf[catOffset + 0x00] = tsTrk;
            catBuf[catOffset + 0x01] = tsSct;
            catBuf[catOffset + 0x02] = 0x08;                // type=S, unlocked
            RawData.SetU16LE(catBuf, catOffset + 0x21, 1);  // size, in sectors

            byte[]? rawName = DOS_FileEntry.GenerateRawName(fileName);
            Debug.Assert(rawName != null);      // checked earlier
            Array.Copy(rawName, 0, catBuf, catOffset + 0x03, MAX_FILE_NAME_LEN);
            if (!newEntry.ExtractFileEntry(catBuf, catOffset, catTrk, catSct, mVolDirEntry,
                    false)) {
                throw new DAException("New entry was rejected");
            }
            Debug.Assert(!newEntry.IsDamaged && !newEntry.IsDubious);

            // Write the catalog sector.
            ChunkAccess.WriteSector(catTrk, catSct, catBuf, 0);
            VTOC.Flush();

            // Insert it into the list of children.
            dirEntry.ChildList.Insert(catIndex, newEntry);

            return newEntry;
        }

        /// <summary>
        /// Finds the first unused slot in the disk catalog.
        /// </summary>
        /// <remarks>
        /// <para>If we're here, we know the disk is undamaged, so we don't need to be
        /// overly cautious when walking through it.</para>
        /// </remarks>
        /// <param name="catTrk">Result: catalog track number.</param>
        /// <param name="catSct">Result: catalog sector number.</param>
        /// <param name="catOffset">Result: byte offset of entry within sector.</param>
        /// <param name="catIndex">Result: absolute index in catalog (0-N).</param>
        /// <returns>Disk buffer with the catalog sector.</returns>
        private byte[] FindCatalogSlot(out byte catTrk, out byte catSct, out int catOffset,
                out int catIndex) {
            Debug.Assert(VTOC != null);
            byte trk = VTOC.FirstCatTrk;
            byte sct = VTOC.FirstCatSct;
            catIndex = 0;

            byte[] dataBuf = new byte[SECTOR_SIZE];
            while (trk != 0) {
                ChunkAccess.ReadSector(trk, sct, dataBuf, 0);

                for (int offset = CATALOG_ENTRY_START; offset < SECTOR_SIZE;
                        offset += CATALOG_ENTRY_LEN) {
                    byte tsTrk = dataBuf[offset + 0x00];
                    if (tsTrk == SLOT_UNUSED || tsTrk == SLOT_DELETED) {
                        catTrk = trk;
                        catSct = sct;
                        catOffset = offset;
                        return dataBuf;
                    }

                    catIndex++;
                }

                trk = dataBuf[0x01];
                sct = dataBuf[0x02];
            }

            throw new DiskFullException("Catalog is full");
        }

        // IFileSystem
        public void MoveFile(IFileEntry ientry, IFileEntry destDir, string newFileName) {
            CheckFileAccess("move", ientry, true, FilePart.Unknown);
            if (destDir != mVolDirEntry) {
                throw new IOException("Destination directory is invalid");
            }

            // Just a rename.
            ientry.FileName = newFileName;
            ientry.SaveChanges();
        }

        // IFileSystem
        public void DeleteFile(IFileEntry ientry) {
            CheckFileAccess("delete", ientry, true, FilePart.Unknown);
            if (ientry == mVolDirEntry) {
                throw new IOException("Can't delete volume directory");
            }
            DOS_FileEntry entry = (DOS_FileEntry)ientry;

            using (DOS_FileDesc fd = DOS_FileDesc.CreateFD(entry, FileAccessMode.ReadWrite,
                    FilePart.RawData, true)) {
                fd.ReleaseStorage();
            }

            entry.DeleteEntry();
            entry.SaveChanges();
            Debug.Assert(VTOC != null);
            VTOC.Flush();

            ((DOS_FileEntry)mVolDirEntry).ChildList.Remove(entry);

            // This entry may no longer be used.
            entry.Invalidate();
        }

        #endregion File Access
    }
}
