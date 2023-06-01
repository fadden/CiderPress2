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
using System.Collections;
using System.Diagnostics;

using CommonUtil;
using static DiskArc.Defs;

namespace DiskArc.FS {
    /// <summary>
    /// This represents the contents of a directory entry.
    /// </summary>
    /// <remarks>
    /// <para>As noted in the <see cref="CalcFileLength"/> comments, the "cooked" file length
    /// may be stored in the file.  This also means that the data in I/A/B files starts at an
    /// offset.  It is imperative that the file type be set correctly before data is written
    /// in "cooked" mode.  (Changing a file's type is a significant event.)</para>
    ///
    /// <para>The "cooked" file access mode is the default, because if we way we have a 700-byte
    /// Applesoft BASIC program, the caller will expect to open the file and read 700 bytes of
    /// Applesoft, without needing to scrutinize the filesystem type first.  "Raw" mode is more
    /// natural for a file archiver when attempting to preserve the full file, but when
    /// extracting T/I/A/B or transferring to/from ProDOS the caller shouldn't need to understand
    /// the file structure.  DOS-to-DOS copies and random-access text file viewing are the
    /// primary use cases for "raw" mode.</para>
    /// </remarks>
    public class DOS_FileEntry : IFileEntryExt, IDisposable {
        // This format string determines the "volume name" of the DOS disk.  Arg 0 is volume num.
        private const string VOL_NAME_FMT = "DOS-{0:D3}";

        // DOS filetype byte values.
        public const byte TYPE_T = 0x00;
        public const byte TYPE_I = 0x01;
        public const byte TYPE_A = 0x02;
        public const byte TYPE_B = 0x04;
        public const byte TYPE_S = 0x08;
        public const byte TYPE_R = 0x10;
        public const byte TYPE_AA = 0x20;
        public const byte TYPE_BB = 0x40;
        // file is locked if 0x80 is set

        #region IFileEntry

        public bool IsDubious { get { return mHasConflict || mHasBadTS; } }

        public bool IsDamaged { get; private set; }

        public bool IsDirectory { get { return IsVolumeDirectory; } }

        public bool HasDataFork => true;
        public bool HasRsrcFork => false;
        public bool IsDiskImage => false;

        public IFileEntry ContainingDir { get; private set; }

        public int Count { get { return ChildList.Count; } }       // fake vol dir only

        public string FileName {
            get { return mFileName; }
            set {
                CheckChangeAllowed();
                if (IsVolumeDirectory) {
                    // TODO: accept a 1-3 char decimal string to set the disk volume number?
                    return;
                }
                byte[]? rawName = GenerateRawName(value);   // this tests validity
                if (rawName == null) {
                    throw new ArgumentException("Invalid filename");
                }
                mFileName = value;
                Array.Copy(rawName, mRawFileName, mRawFileName.Length);
                IsDirty = true;
            }
        }
        public char DirectorySeparatorChar {
            get { return IFileEntry.NO_DIR_SEP; }
            set { }
        }
        public string FullPathName {
            get {
                string pathName;
                pathName = mFileName;       // formatted vol num for root dir
                if (FileSystem == null) {
                    pathName = "!INVALID! was:" + pathName;
                }
                return pathName;
            }
        }

        public byte[] RawFileName {
            get {
                // Use the "cooked" length, which is exactly the same as the trimmed "raw" length.
                byte[] result = new byte[mFileName.Length];
                Array.Copy(mRawFileName, result, result.Length);
                return result;
            }
            set {
                // Don't test filename validity; we want a way to set wacky DOS filenames.
                CheckChangeAllowed();
                if (IsVolumeDirectory) {
                    return;
                }
                if (value.Length > DOS.MAX_FILE_NAME_LEN) {
                    throw new ArgumentException("Invalid filename");
                }
                string cookedName = GenerateCookedName(value);  // this does NOT test validity
                mFileName = cookedName;
                // Copy new value in, and pad the rest of the field with high-ASCII spaces.
                Array.Copy(value, mRawFileName, value.Length);
                for (int i = value.Length; i < mRawFileName.Length; i++) {
                    mRawFileName[i] = 0xa0;
                }
                IsDirty = true;
            }
        }

        public bool HasProDOSTypes { get { return true; } }
        public byte FileType {
            get {
                if (IsVolumeDirectory) {
                    return FileAttribs.FILE_TYPE_DIR;
                } else {
                    return TypeToProDOS(mTypeAndFlags);
                }
            }
            set {
                CheckChangeAllowed();
                byte origType = mTypeAndFlags;
                byte newType = TypeFromProDOS(value);
                mTypeAndFlags = (byte)((newType & 0x7f) | (mTypeAndFlags & 0x80));
                // Re-evaluate length.  This is only necessary if the original type or the new
                // type is T/I/A/B, and not equal to the original (with I and A are equivalent).
                // For now, just keep it simple and recalculate the length every time.
                //
                // We want the new lengths to be available immediately, so we can't defer this
                // to SaveChanges().
                //
                // This could throw if we changed the type to 'T' and ran into a bad block
                // while doing the full-file scan.  It's a bit late to be marking the file
                // as damaged, since it might already be open, but we can't ignore the fact
                // that we don't know how long the file is supposed to be.
                //
                // Note this overwrites mBinLoadAddr with whatever it finds.
                try {
                    CalcFileLength(null);
                } catch (IOException) {
                    // Undo the change and throw the exception.
                    mTypeAndFlags = origType;
                    IsDamaged = true;
                    throw;
                }
                IsDirty = true;
            }
        }
        public ushort AuxType {
            get {
                int type = mTypeAndFlags & 0x7f;
                if (type == TYPE_A) {
                    return 0x0801;
                } else if (type == TYPE_B) {
                    return mBinLoadAddr;
                } else {
                    return 0;
                }
            }
            set {
                CheckChangeAllowed();
                // Set the 'B' load address.  If it's not a 'B' file, the value is ignored by
                // the "get" call.
                mBinLoadAddr = value;
                IsDirty = true;
            }
        }

        public bool HasHFSTypes { get { return false; } }
        public uint HFSFileType { get { return 0; } set { } }
        public uint HFSCreator { get { return 0; } set { } }

        public byte Access {
            get {
                if ((mTypeAndFlags & 0x80) != 0) {
                    return (byte)ACCESS_LOCKED;
                } else {
                    return (byte)ACCESS_UNLOCKED;
                }
            }
            set {
                CheckChangeAllowed();
                // The "write-enabled" flag determines the locked/unlocked status.
                if ((value & (int)AccessFlags.Write) != 0) {
                    mTypeAndFlags &= 0x7f;      // unlock
                } else {
                    mTypeAndFlags |= 0x80;      // lock
                }
                IsDirty = true;
            }
        }
        public DateTime CreateWhen { get { return TimeStamp.NO_DATE; } set { } }
        public DateTime ModWhen { get { return TimeStamp.NO_DATE; } set { } }

        // This is based purely on the sector count.  If an entry is damaged or dubious, we may
        // not have an accurate value here.  If the value in the catalog sector is way off, this
        // value may be crazily large.
        public long StorageSize { get { return mSectorCount * SECTOR_SIZE; } }

        // Don't set the "dirty" flag for this one, because we're either tracking the raw length,
        // or updating this to match the value stored inside the file.
        public long DataLength { get; internal set; }

        public long RsrcLength { get { return 0; } }

        public string Comment { get { return string.Empty; } set { } }

        public bool GetPartInfo(FilePart part, out long length, out long storageSize,
                out CompressionFormat format) {
            format = CompressionFormat.Uncompressed;
            if (part == FilePart.DataFork) {
                length = DataLength;
                storageSize = StorageSize;
                return true;
            } else if (part == FilePart.RawData) {
                length = RawDataLength;
                storageSize = StorageSize;
                return true;
            } else {
                length = storageSize = -1;
                return false;
            }
        }

        public IEnumerator<IFileEntry> GetEnumerator() {
            if (!IsValid) {
                throw new DAException("Invalid file entry object");
            }
            return ChildList.GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() {
            if (!IsValid) {
                throw new DAException("Invalid file entry object");
            }
            return ChildList.GetEnumerator();
        }

        /// <summary>
        /// Throws an exception if attribute changes are not allowed.
        /// </summary>
        private void CheckChangeAllowed() {
            if (FileSystem == null || FileSystem.IsReadOnly || IsDubious || IsDamaged) {
                throw new IOException("Changes not allowed");
            }
        }

        #endregion IFileEntry

        //
        // DOS-specific properties.
        //

        // Data parsed from directory file entry.
        internal byte TSTrk { get; private set; }
        internal byte TSSct { get; private set; }
        private byte mTypeAndFlags;
        // We want a full-width field, because deleted files store the track number in the
        // last byte of the filename.
        private readonly byte[] mRawFileName = new byte[DOS.MAX_FILE_NAME_LEN];

        /// <summary>
        /// Total number of sectors used by this file, as stored in the catalog entry.
        /// </summary>
        public ushort SectorCount {
            get {
                return mSectorCount;
            }
            internal set {
                mSectorCount = value;
                IsDirty = true;
            }
        }
        private ushort mSectorCount;

        // Processed data.
        private string mFileName = string.Empty;

        internal bool IsDirty { get; private set; }

        /// <summary>
        /// If set, recompute length as part of SaveChanges().
        /// </summary>
        /// <remarks>
        /// Set by the file descriptor code when certain actions are performed, like writing
        /// to the first sector of an I/A/B file while in raw data mode.
        /// </remarks>
        internal bool NeedRecalcLength { get; set; }

        /// <summary>
        /// True if the file shares storage with another file or the system.
        /// </summary>
        private bool mHasConflict;

        /// <summary>
        /// True if the file has a bad T/S list pointer or entry.
        /// </summary>
        private bool mHasBadTS;

        /// <summary>
        /// Length of file when treated as "raw" data.  Determined by the position of the
        /// last nonzero T/S list entry.
        /// </summary>
        public int RawDataLength {
            get { return mRawDataLength; }
            internal set {
                mRawDataLength = value;
                IsDirty = true;
            }
        }
        private int mRawDataLength;

        /// <summary>
        /// Reference to filesystem object.
        /// </summary>
        public DOS FileSystem { get; private set; }

        /// <summary>
        /// True if this object has not been invalidated.
        /// </summary>
        internal bool IsValid { get { return FileSystem != null; } }

        /// <summary>
        /// True if this is the "fake" volume directory object.
        /// </summary>
        private bool IsVolumeDirectory { get; set; }

        /// <summary>
        /// List of files contained in this entry.  Only the volume dir has children.
        /// </summary>
        internal List<IFileEntry> ChildList { get; } = new List<IFileEntry>();

        // Location of the directory entry in the catalog.
        private byte mCatalogTrk;
        private byte mCatalogSct;
        private int mCatalogOffset;

        /// <summary>
        /// DOS file type.
        /// </summary>
        internal byte DOSFileType { get { return (byte)(mTypeAndFlags & 0x7f); } }

        /// <summary>
        /// Load address for 'B' files, obtained from the first sector of the file.
        /// </summary>
        private ushort mBinLoadAddr;

        public static byte TypeToProDOS(byte typeAndFlags) {
            switch (typeAndFlags & 0x7f) {
                case TYPE_T:
                    return FileAttribs.FILE_TYPE_TXT;
                case TYPE_I:
                    return FileAttribs.FILE_TYPE_INT;
                case TYPE_A:
                    return FileAttribs.FILE_TYPE_BAS;
                case TYPE_B:
                    return FileAttribs.FILE_TYPE_BIN;
                case TYPE_S:
                    return FileAttribs.FILE_TYPE_F2;
                case TYPE_R:
                    return FileAttribs.FILE_TYPE_REL;
                case TYPE_AA:
                    return FileAttribs.FILE_TYPE_F3;
                case TYPE_BB:
                    return FileAttribs.FILE_TYPE_F4;
                default:
                    // Multiple bits set.  Leave it typeless.
                    return FileAttribs.FILE_TYPE_NON;
            }
        }

        public static byte TypeFromProDOS(byte fileType) {
            switch (fileType) {
                case FileAttribs.FILE_TYPE_TXT:
                    return TYPE_T;
                case FileAttribs.FILE_TYPE_INT:
                    return TYPE_I;
                case FileAttribs.FILE_TYPE_BAS:
                    return TYPE_A;
                case FileAttribs.FILE_TYPE_BIN:
                    return TYPE_B;
                case FileAttribs.FILE_TYPE_F2:
                    return TYPE_S;
                case FileAttribs.FILE_TYPE_REL:
                    return TYPE_R;
                case FileAttribs.FILE_TYPE_F3:
                    return TYPE_AA;
                case FileAttribs.FILE_TYPE_F4:
                    return TYPE_BB;
                default:
                    // Unknown, use 'S'.
                    return TYPE_S;
            }
        }

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="fileSystem"></param>
        public DOS_FileEntry(DOS fileSystem) {
            ContainingDir = IFileEntry.NO_ENTRY;
            FileSystem = fileSystem;
        }

        // IDisposable generic finalizer.
        ~DOS_FileEntry() {
            Dispose(false);
        }
        // IDisposable generic Dispose() implementation.
        public void Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        protected virtual void Dispose(bool disposing) {
            // The only reason for doing this is to ensure that we're flushing changes.  This
            // can happen if the application chooses not to call SaveChanges before closing
            // the filesystem or switching to raw mode.
            //
            // (IFileEntry is not IDisposable, so this is normally only called by the GC.)
            if (IsDirty) {
                if (disposing) {
                    Debug.WriteLine("Disposing of entry with unsaved changes: " + this);
                    Debug.Assert(false);    // TODO: remove
                    SaveChanges();
                } else {
                    // Calling SaveChanges() would be risky.
                    Debug.Assert(false, "GC disposing of entry with unsaved changes: " + this);
                }
            }
        }

        /// <summary>
        /// Invalidates the file entry object.  This is called when the filesystem is switched
        /// to "raw" access mode, so that any objects retained by the application stop working.
        /// </summary>
        internal void Invalidate() {
            Debug.Assert(FileSystem != null); // harmless, but we shouldn't be invalidating twice
#pragma warning disable CS8625
            FileSystem = null;  // ensure that "is this part of our filesystem" check fails
#pragma warning restore CS8625
            ContainingDir = IFileEntry.NO_ENTRY;
            ChildList.Clear();
        }

        /// <summary>
        /// Scans the catalog.  If damage to the catalog is found, such as a bad sector or a
        /// bad pointer, the IsDamaged flag is raised at the filesystem level.
        /// </summary>
        /// <remarks>
        /// <para>We need to get the length of every file, which requires walking through the
        /// file structure, so the "deep scan" argument is irrelevant here.</para>
        /// <para>We don't generally mark the volume directory as damaged, because doing so
        /// means "you can't read the directory at all".  Bad links between catalog sectors
        /// will stop us from reading the later stuff but not the earlier stuff.  Still, we
        /// want to mark the volume as dubious to ensure that we don't try to create files
        /// in the damaged area.</para>
        /// </remarks>
        /// <param name="fileSystem">Filesystem object.</param>
        /// <param name="vtoc">Volume Table of Contents for this disk.</param>
        /// <param name="doDeepScan">(not used)</param>
        /// <returns>Volume directory object.</returns>
        /// <exception cref="IOException">Disk access failure.</exception>
        internal static IFileEntry ScanCatalog(DOS fileSystem, DOS_VTOC vtoc, bool doDeepScan) {
            DOS_FileEntry volDir = new DOS_FileEntry(fileSystem);
            volDir.IsVolumeDirectory = true;
            volDir.mFileName = string.Format(VOL_NAME_FMT, vtoc.VolumeNum);

            VolumeUsage vu = vtoc.VolUsage;
            // Set VTOC sector usage.
            vu.SetUsage(fileSystem.TSToChunk(vtoc.VTOCTrk, vtoc.VTOCSct), IFileEntry.NO_ENTRY);

            byte[] dataBuf = new byte[SECTOR_SIZE];
            byte catTrk = vtoc.FirstCatTrk;
            byte catSct = vtoc.FirstCatSct;

            bool doSkipRemaining = false;
            int iterCount = 0;
            while (catTrk != 0 && ++iterCount <= DOS.MAX_CATALOG_SECTORS) {
                if ((catTrk == DOS.VTOC_TRACK && catSct == DOS.VTOC_SECTOR) ||
                        catTrk >= vtoc.NumTrks || catSct >= vtoc.NumScts) {
                    // Reference back to VTOC, or other invalid values.
                    fileSystem.Notes.AddW("Found bad catalog link, to T" + catTrk + " S " + catSct);
                    fileSystem.IsDubious = true;
                    return volDir;
                }
                try {
                    fileSystem.ChunkAccess.ReadSector(catTrk, catSct, dataBuf, 0);
                } catch (BadBlockException) {
                    // Unable to read sector.  Mark the filesystem as dubious, mark the sector
                    // as used, and try to move on to the next sector.
                    fileSystem.Notes.AddE("Unable to read catalog T" + catTrk + " S" + catSct);
                    fileSystem.IsDubious = true;
                    vu.SetUsage(fileSystem.TSToChunk(catTrk, catSct), IFileEntry.NO_ENTRY);
                    if (catSct > 1) {
                        catSct--;       // try the next in descending sequence
                    } else {
                        catTrk = 0;     // give up
                    }
                    continue;
                }

                // Set catalog sector usage.
                vu.SetUsage(fileSystem.TSToChunk(catTrk, catSct), IFileEntry.NO_ENTRY);

                // Walk through all entries in this sector.
                for (int offset = DOS.CATALOG_ENTRY_START; offset < SECTOR_SIZE;
                        offset += DOS.CATALOG_ENTRY_LEN) {
                    if (doSkipRemaining) {
                        break;
                    }
                    byte tsTrk = dataBuf[offset + 0x00];
                    if (tsTrk == DOS.SLOT_UNUSED) {
                        // We're supposed to stop at the first unused slot.  Some disks have
                        // garbage in the catalog track after the "real" entries, and if we keep
                        // scanning we might find something real enough to make us think that two
                        // entries have overlapping content, and mark a valid disk as dubious.
                        //
                        // We don't actually *stop* when we hit this, because we still want to
                        // traverse the entire catalog to check the sector usage.
                        if (fileSystem.AppHook.GetOptionBool(DAAppHook.DOS_STOP_FIRST_UNUSED,
                                true)) {
                            doSkipRemaining = true;
                            break;
                        } else {
                            continue;       // skip
                        }
                    }
                    if (tsTrk == DOS.SLOT_DELETED) {
                        continue;           // skip
                    }

                    // Create an entry for the file, even if the T/S for it is broken.  We'll
                    // mark it as "damaged" later.
                    DOS_FileEntry newEntry = new DOS_FileEntry(fileSystem);
                    if (!newEntry.ExtractFileEntry(dataBuf, offset, catTrk, catSct, volDir, true)) {
                        throw new DAException("Internal failure");
                    }
                    volDir.ChildList.Add(newEntry);
                }

                // Advance to the next sector.
                if (catTrk == dataBuf[0x01] && catSct == dataBuf[0x02]) {
                    // This will eventually set off the iteration limiter, which marks the disk
                    // unusable.  If we stop now, we can show what we've uncovered so far.
                    fileSystem.Notes.AddW("Found self-referential catalog sector");
                    fileSystem.IsDubious = true;
                    break;
                }
                catTrk = dataBuf[0x01];
                catSct = dataBuf[0x02];
            }
            if (iterCount > DOS.MAX_CATALOG_SECTORS) {
                // Exited due to infinite loop.  Without a more careful approach we can't know
                // at what point we started repeating.  Assume the disk is bad and show nothing.
                // TODO(maybe): keep a list of where we've been, watch for re-visit.
                fileSystem.Notes.AddE("Infinite loop on catalog track");
                volDir.IsDamaged = fileSystem.IsDubious = true;
                volDir.ChildList.Clear();
                return volDir;
            }

            return volDir;
        }

        /// <summary>
        /// Extract the contents of a catalog entry into the file entry object.  Calculates the
        /// file length.
        /// </summary>
        /// <param name="dataBuf">Catalog sector buffer.</param>
        /// <param name="offset">Byte offset of entry in catalog sector.</param>
        /// <param name="volDir">Reference to volume directory object.</param>
        /// <returns>True on success, false if the entry appears to be unused or deleted.</returns>
        internal bool ExtractFileEntry(byte[] dataBuf, int offset, byte catTrk, byte catSct,
                IFileEntry volDir, bool scanUsage) {
            ContainingDir = volDir;
            TSTrk = dataBuf[offset + 0x00];
            if (TSTrk == DOS.SLOT_DELETED || TSTrk == DOS.SLOT_UNUSED) {
                return false;
            }
            TSSct = dataBuf[offset + 0x01];
            mTypeAndFlags = dataBuf[offset + 0x02];
            Array.Copy(dataBuf, offset + 0x03, mRawFileName, 0, DOS.MAX_FILE_NAME_LEN);
            mSectorCount = RawData.GetU16LE(dataBuf, offset + 0x21);

            // Get a readable form of the filename.  There's no real validation to be
            // performed, because all strings are allowed.
            mFileName = GenerateCookedName(mRawFileName);

            mCatalogTrk = catTrk;
            mCatalogSct = catSct;
            mCatalogOffset = offset;

            DOS_VTOC? vtoc = FileSystem.VTOC;
            Debug.Assert(vtoc != null);

            // Validate some fields.
            if (TSTrk >= vtoc.NumTrks || TSSct >= vtoc.NumScts) {
                FileSystem.Notes.AddW("Invalid track/sector found (T" + TSTrk +
                    " S" + TSSct + "): " + FullPathName);
                IsDamaged = true;
            }

            if (!IsDamaged) {
                try {
                    CalcFileLength(scanUsage ? vtoc.VolUsage : null);
                } catch (IOException) {
                    IsDamaged = true;
                }
            }

            return true;
        }

        // Allocate a couple of buffers to hold sector data while scanning files.  This avoids
        // allocating new buffers for every file.
        private byte[] mScanTSBuf = new byte[SECTOR_SIZE];
        private byte[] mScanDataBuf = new byte[SECTOR_SIZE];

        /// <summary>
        /// <para>Calculates the file's length and storage size by traversing the track/sector
        /// list chain.  The DataLength, RawDataLength, and StorageSize properties will be
        /// updated.  For a 'B' file, this will also extract the load address for the
        /// auxtype field.</para>
        /// <para>This also validates the T/S list, and will flag files with questionable
        /// entries as dubious rather than damaged (allowing the undamaged portion to be
        /// read).</para>
        /// </summary>
        ///
        /// <remarks>
        /// <para>The storage size is determined by counting the non-sparse members of the
        /// T/S list chain.  The "raw" data length is determined by the position of the last
        /// valid T/S entry, and is always a multiple of the sector size.  The "cooked"
        /// DataLength value is determined in a type-specific fashion:</para>
        /// <list type="bullet">
        ///   <item>I/A/B: data length is retrieved from first sector of file</item>
        ///   <item>T: file ends at first 0x00; requires reading entire file</item>
        ///   <item>S/R/A+/B+: data length equals raw length</item>
        /// </list>
        /// <para>The "cooked" length is clamped to the "raw" length.  If the first sector
        /// of an I/A/B file is missing, the "cooked" length will be set to zero.  (Note
        /// this situation is illegal.)  It's not uncommon for there to be data past the "cooked"
        /// EOF; the situation can be created from the command line by using BSAVE twice,
        /// with a shorter length on the second invocation.</para>
        /// <para>If an I/A/B file has data written to it that exceeds 65535 bytes, the
        /// length will be set to zero.</para>
        /// <para>Random-access text files cannot generally be read in their entirety using
        /// the "cooked" interface.  Use the "raw" interface instead.  There is no way to
        /// tell the difference between sequential and random-access text files.</para>
        ///
        /// <para>This method should be called during the initial volume scan, and whenever the
        /// filetype changes.  The file descriptor code is responsible for updating the
        /// length in the first sector of I/A/B files, and can either update the properties
        /// directly or call here for a full re-evaluation.  The handling is based on the type
        /// the file had when it was opened; changes to the type of an open file will not
        /// affect the fd.</para>
        ///
        /// <para>If invalid T/S entries are discovered, the file will be marked "dubious"
        /// and the list will be considered to end at the last nonzero entry prior to the
        /// first bad entry.  This should allow full recovery of valid files that happen to
        /// have junk in the T/S list.</para>
        ///
        /// <para>This should only throw an exception if a bad sector is read (nibble image
        /// or physical media).</para>
        /// </remarks>
        ///
        /// <param name="vu">If non-null, add the file's disk usage to the VolumeUsage
        ///   object.</param>
        /// <exception cref="IOException">Disk access failure.</exception>
        internal void CalcFileLength(VolumeUsage? vu) {
            uint totalTrks = FileSystem.TotalTrks;
            uint totalScts = FileSystem.TotalScts;
            uint trk = TSTrk;
            uint sct = TSSct;
            Debug.Assert(trk < totalTrks && sct < totalScts);

            mBinLoadAddr = 0;

            byte[] tsBuf = mScanTSBuf;
            byte[] dataBuf = mScanDataBuf;
            int expSctOffset = 0;

            int fileType = mTypeAndFlags & 0x7f;

            byte firstDataTrk = 0;
            byte firstDataSct = 0;

            int storageSizeCount = 0;
            int seqTextLength = 0;
            int iterations = 0;
            bool first = true;
            bool foundTextEnd = false;
            int lastTSEntry = -1;
            int lastTSSctOffset = 0;
            while (trk != 0) {
                FileSystem.ChunkAccess.ReadSector(trk, sct, tsBuf, 0);
                storageSizeCount++;       // count the T/S entry
                if (vu != null) {
                    vu.SetUsage(FileSystem.TSToChunk(trk, sct), this);
                }

                ushort sectorOffset = RawData.GetU16LE(tsBuf, 0x05);
                if (sectorOffset != expSctOffset) {
                    // I'm not sure anything uses this; we don't, so no need to flag file.
                    FileSystem.Notes.AddW("Incorrect sector offset in T/S list (found " +
                        sectorOffset + ", expected " + expSctOffset + "): " + FullPathName);
                }

                bool foundDamage = false;
                for (int offset = DOS.FIRST_TS_OFFSET; offset < SECTOR_SIZE; offset += 2) {
                    byte dataTrk = tsBuf[offset];
                    byte dataSct = tsBuf[offset + 1];
                    if (dataTrk == 0) {
                        // Sparse entry, or past the end of the file in this T/S list sector.
                        foundTextEnd = true;
                    } else if (dataTrk >= totalTrks || dataSct >= totalScts) {
                        // Damaged entry, stop scan.  The file contents up to the point of the
                        // damage can be read.
                        foundDamage = true;
                        break;
                    } else {
                        storageSizeCount++;
                        lastTSEntry = (offset - DOS.FIRST_TS_OFFSET) / 2;
                        lastTSSctOffset = expSctOffset;
                        if (vu != null) {
                            vu.SetUsage(FileSystem.TSToChunk(dataTrk, dataSct), this);
                        }
                    }

                    // For text files, scan the sector for the first zero byte.  For a sequential
                    // text file this should only appear in the last sector, but for a
                    // random-access text file this could happen anywhere.
                    if (!foundTextEnd && fileType == TYPE_T) {
                        // See if there's a zero byte in this sector.
                        FileSystem.ChunkAccess.ReadSector(dataTrk, dataSct, dataBuf, 0);
                        int firstZero = RawData.FirstZero(dataBuf, 0, SECTOR_SIZE);
                        if (firstZero == -1) {
                            // Didn't find a zero.
                            seqTextLength += SECTOR_SIZE;
                        } else {
                            seqTextLength += firstZero;
                            foundTextEnd = true;
                        }
                    }
                }

                if (first) {
                    firstDataTrk = tsBuf[DOS.FIRST_TS_OFFSET];
                    firstDataSct = tsBuf[DOS.FIRST_TS_OFFSET + 1];
                    first = false;
                }

                if (foundDamage) {
                    FileSystem.Notes.AddW("Found bad T/S entry in T" + trk + " S" + sct + ": " +
                        FullPathName);
                    mHasBadTS = true;
                    break;
                }

                trk = tsBuf[0x01];
                sct = tsBuf[0x02];
                if (trk >= totalTrks || sct >= totalScts) {
                    // Invalid T/S pointer found.
                    FileSystem.Notes.AddW("Found bad T/S link (T" + trk + " S" + sct + "): " +
                        FullPathName);
                    mHasBadTS = true;
                    break;
                }

                expSctOffset += DOS.MAX_TS_PER_TSLIST;

                if (++iterations >= DOS.MAX_TS_CHAIN) {
                    FileSystem.Notes.AddE("Found circular T/S list: " + FullPathName);
                    IsDamaged = true;
                    //FileSystem.IsDamaged = true;
                    // The computed length values are meaningless, so leave the properties set to 0.
                    return;
                }
            }


            // If the sector count in the catalog differs from our computation, update the value.
            // Skip this if the T/S list is damaged.
            if (!mHasBadTS && storageSizeCount != mSectorCount) {
                FileSystem.Notes.AddI("Sector count wrong in catalog entry: read " +
                    mSectorCount + ", calculated " + storageSizeCount + ": " + FullPathName);
                // Fix the sector count, but don't set the "dirty" flag.  That way we only
                // write the updated entry if something else changes.
                mSectorCount = (ushort)storageSizeCount;
            }

            // Set the raw data length based on the position of the last nonzero T/S list entry.
            //
            // We may encounter a completely empty T/S list.  If it's in the middle, we add
            // all 122 entries to the raw length.  If it's at the end (or is the only one), then
            // we ignore those entries.
            //
            // This means that, if we copy a file with empty T/S list sectors at the end, we
            // won't exactly reproduce the original file.  Preserving useless T/S list sectors
            // is not a priority.
            int rawDataCount;
            if (lastTSEntry < 0) {
                // No T/S entries were found.
                rawDataCount = 0;
            } else {
                // 122 * number of sectors, plus the index of the last entry, plus 1 to convert
                // the offset to a count.
                rawDataCount = lastTSSctOffset + lastTSEntry + 1;
            }
            mRawDataLength = rawDataCount * SECTOR_SIZE;

            // For I/A/B, get the data from the first block.
            if (IsDamaged) {
                Debug.Assert(false);    // shouldn't be here if file is damaged
            } else if (fileType == TYPE_I || fileType == TYPE_A || fileType == TYPE_B) {
                if (firstDataTrk != 0 && firstDataTrk < totalTrks && firstDataSct < totalScts) {
                    FileSystem.ChunkAccess.ReadSector(firstDataTrk, firstDataSct, dataBuf, 0);
                    int startOffset;
                    if (fileType == TYPE_B) {
                        startOffset = 4;
                        mBinLoadAddr = RawData.GetU16LE(dataBuf, 0);
                        DataLength = RawData.GetU16LE(dataBuf, 2);
                    } else {
                        startOffset = 2;
                        DataLength = RawData.GetU16LE(dataBuf, 0);
                    }
                    if (DataLength + startOffset > mRawDataLength) {
                        // Issue a warning.  No need to mark as dubious; reading past the end
                        // of the file is allowed (returns a partial buffer).
                        FileSystem.Notes.AddW("I/A/B stored length (" + DataLength +
                            " + " + startOffset + ") exceeds actual file length (" +
                            mRawDataLength + "): " + FullPathName);
                        // We could truncate the length, but that seems like we might be throwing
                        // away a value that we shouldn't be messing with.
                        //DataLength = mRawDataLength;
                    }
                } else {
                    // Reference to first block is missing or broken.  Set length to zero.
                    DataLength = 0;
                }
            } else if (fileType == TYPE_T) {
                // Assume sequential text.
                DataLength = seqTextLength;
            } else {
                // S/R/A+/B+ just use the raw length.
                DataLength = mRawDataLength;
            }
        }

        // Work buffers for SaveChanges(), allocated on first use.
        private byte[]? mBeforeBuf;
        private byte[]? mEditBuf;

        // IFileEntry
        public void SaveChanges() {
            if (!IsValid) {
                throw new DAException("Cannot save changes on invalid file entry object");
            }
            if (FileSystem.IsReadOnly) {
                Debug.WriteLine("Ignoring SaveChanges on read-only volume");
                return;
            }
            if (IsDubious || IsDamaged) {
                Debug.WriteLine("Ignoring SaveChanges on dubious/damaged file");
                return;
            }
            if (!IsDirty) {
                return;
            }

            // This is set by the file descriptor code.
            if (NeedRecalcLength) {
                Debug.Assert(TSTrk != DOS.SLOT_DELETED);
                try {
                    CalcFileLength(null);
                } catch {
                    FileSystem.AppHook.LogE("Found damage while saving changes to " + this);
                    IsDamaged = true;
                    throw;
                }

                NeedRecalcLength = false;
            }

            if (mBeforeBuf == null) {
                mBeforeBuf = new byte[SECTOR_SIZE];
                mEditBuf = new byte[SECTOR_SIZE];
            } else {
                Debug.Assert(mEditBuf != null);
            }

            // Read the catalog sector, apply the changes, save it if something changed.
            FileSystem.ChunkAccess.ReadSector(mCatalogTrk, mCatalogSct, mBeforeBuf, 0);
            Array.Copy(mBeforeBuf, mEditBuf, SECTOR_SIZE);
            mEditBuf[mCatalogOffset + 0x00] = TSTrk;
            mEditBuf[mCatalogOffset + 0x01] = TSSct;
            mEditBuf[mCatalogOffset + 0x02] = mTypeAndFlags;
            Array.Copy(mRawFileName, 0, mEditBuf, mCatalogOffset + 0x03, DOS.MAX_FILE_NAME_LEN);
            RawData.SetU16LE(mEditBuf, mCatalogOffset + 0x21, mSectorCount);

            if (!RawData.CompareBytes(mBeforeBuf, mEditBuf, mEditBuf.Length)) {
                FileSystem.ChunkAccess.WriteSector(mCatalogTrk, mCatalogSct, mEditBuf, 0);
            }

            // For 'B' files, see if the aux type (load address) has changed.
            if (TSTrk != DOS.SLOT_DELETED && DOSFileType == TYPE_B) {
                // Get first T/S list sector.
                FileSystem.ChunkAccess.ReadSector(TSTrk, TSSct, mBeforeBuf, 0);
                byte trk = mBeforeBuf[0x0c];
                byte sct = mBeforeBuf[0x0d];
                if (trk != 0) {
                    // Read first data sector, get the old address.
                    FileSystem.ChunkAccess.ReadSector(trk, sct, mBeforeBuf, 0);
                    ushort oldAddr = RawData.GetU16LE(mBeforeBuf, 0);
                    if (oldAddr != mBinLoadAddr) {
                        // Address was changed.  Update it and write the sector.
                        RawData.SetU16LE(mBeforeBuf, 0, mBinLoadAddr);
                        FileSystem.ChunkAccess.WriteSector(trk, sct, mBeforeBuf, 0);
                    }
                }
            }

            IsDirty = false;
        }

        /// <summary>
        /// Marks an entry as deleted.
        /// </summary>
        /// <exception cref="DAException">File was already deleted.</exception>
        internal void DeleteEntry() {
            // Copy track to end of filename, set track to $ff.
            if (TSTrk == DOS.SLOT_DELETED) {
                throw new DAException("DeleteEntry called twice");
            }
            Debug.Assert(TSTrk != DOS.SLOT_UNUSED);     // shouldn't be here

            // Copy the track number to the end of the filename field.
            mRawFileName[DOS.MAX_FILE_NAME_LEN - 1] = TSTrk;
            TSTrk = DOS.SLOT_DELETED;
            IsDirty = true;
        }

        // IFileEntryExt
        public void AddConflict(uint chunk, IFileEntry entry) {
            if (!mHasConflict) {
                // Only report when the dubious flag isn't set, so that we don't flood the Notes.
                // This means we won't dump a full report if there are multiple files overlapping
                // in multiple places, but we will mention each problematic file at least once.
                string name = (entry == IFileEntry.NO_ENTRY) ?
                    VolumeUsage.SYSTEM_STR : entry.FullPathName;
                if (entry == IFileEntry.NO_ENTRY) {
                    // The file overlaps with some part of the system, such as the boot image
                    // or the catalog track.  The latter is very dangerous, because adding more
                    // files to the catalog could cause corruption.
                    FileSystem.Notes.AddE(FullPathName + " overlaps with " + name);
                    FileSystem.IsDubious = true;
                } else {
                    FileSystem.Notes.AddW(FullPathName + " overlaps with " + name);
                }
            }
            mHasConflict = true;
        }

        #region Filenames

        public int CompareFileName(string fileName) {
            // Filenames are case-sensitive.  Use simple ordinal sort.
            return string.Compare(mFileName, fileName, StringComparison.Ordinal);
        }
        public int CompareFileName(string fileName, char fileNameSeparator) {
            return CompareFileName(fileName);
            //return PathName.ComparePathNames(mFileName, IFileEntry.NO_DIR_SEP, fileName,
            //    fileNameSeparator, PathName.CompareAlgorithm.Ordinal);
        }

        // Start/end of the Unicode "control pictures" block for the C0 characters (0x00-0x1f).
        private const int CTRL_PIC_START = 0x2400;
        private const int CTRL_PIC_END = 0x241f;
        // Control picture for DEL (0x7f).
        private const int CTRL_PIC_DEL = 0x2421;

        /// <summary>
        /// Determines whether the filename can be represented on a DOS disk.
        /// </summary>
        /// <remarks>
        /// <para>DOS filenames are expected to start with a letter and may not contain a
        /// comma.  The actual requirement on the initial character appears to be >= 0x40, so
        /// "/FOO" and ":FOO" will choke, but "@FOO" and "^FOO" work fine.  Lower case is
        /// possible but discouraged.  The names are stored in high ASCII (though low ASCII can
        /// be used for special effects), so we reject values > 0x7e in strings.</para>
        /// <para>Control characters are possible, so we map those to the printable range.</para>
        /// <para>DOS implicitly removes trailing spaces, so we reject any filename that has them
        /// (since they can't actually be part of the filename).</para>
        /// </remarks>
        public static bool IsFileNameValid(string fileName) {
            if (fileName.Length > DOS.MAX_FILE_NAME_LEN) {
                return false;
            }
            if (fileName.Length == 0) {
                return true;        // allowed
            }
            if (fileName.Length > 0 && fileName[fileName.Length - 1] == ' ') {
                return false;
            }
            if (fileName[0] < 0x40 || fileName[0] > 0x7e) {     // reject 0x7f (DEL)
                return false;
            }
            foreach (char ch in fileName) {
                // The control character range gets remapped to the "control pictures" block.
                if (!((ch >= 0x20 && ch <= 0x7e) || (ch >= CTRL_PIC_START && ch <= CTRL_PIC_END) ||
                        ch == CTRL_PIC_DEL)) {
                    return false;
                }
                if (ch == ',') {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Generates the "cooked" form of the filename.  Illegal characters are accepted, so the
        /// result may not be a legal DOS filename.
        /// </summary>
        /// <remarks>
        /// <para>DOS has few restrictions on filename characters.  Most filenames are upper-case
        /// high ASCII, but Apple II screen encoding allows inverse and flashing text, as well as
        /// control characters with special effects (Ctrl+G beeps, Ctrl+H backspaces).</para>
        /// <para>We strip the high bit, and convert control characters to printable glyphs.
        /// Note the transformation is not fully reversible.</para>
        /// </remarks>
        /// <param name="rawFileName">Raw filename data.</param>
        /// <returns>Filename string.  May be empty.</returns>
        public static string GenerateCookedName(byte[] rawFileName) {
            Debug.Assert(rawFileName.Length <= DOS.MAX_FILE_NAME_LEN);

            // Trim trailing high-ASCII spaces.
            int length = 0;
            for (int idx = rawFileName.Length; idx > 0; idx--) {
                if (rawFileName[idx - 1] != (' ' | 0x80)) {
                    length = idx;
                    break;
                }
            }

            // Convert to printable form.  Some catalogs use inverse and flashing text, which
            // we want to display as normal, and control characters (especially Ctrl+H and Ctrl+G)
            // require special treatment.
            char[] printable = new char[length];
            for (int idx = 0; idx < length; idx++) {
                int bch = rawFileName[idx];
                if (bch < 0x20) {
                    // $00-1f: inverse upper-case letters.
                    bch += 0x40;
                } else if (bch < 0x40) {
                    // $20-3f: inverse numbers and symbols.
                } else if (bch < 0x60) {
                    // $40-5f: flashing upper-case letters.
                } else if (bch < 0x80) {
                    // $60-7f: flashing numbers and symbols.
                    bch -= 0x40;
                } else if (bch < 0xa0) {
                    // $80-9f: control characters.
                    bch += CTRL_PIC_START - 0x80;
                } else if (bch < 0xff) {
                    // $a0-fe: high ASCII
                    bch -= 0x80;
                } else {
                    // $ff: high ASCII DEL
                    Debug.Assert(bch == 0xff);
                    bch = CTRL_PIC_DEL;
                }
                printable[idx] = (char)bch;
            }

            return new string(printable);
        }

        /// <summary>
        /// Generates the "raw" DOS filename.
        /// </summary>
        /// <param name="fileName">DOS-compatible filename.</param>
        /// <returns>Raw filename, in a maximum-length byte buffer, or null if the conversion
        ///   failed.</returns>
        public static byte[]? GenerateRawName(string fileName) {
            if (!IsFileNameValid(fileName)) {
                return null;
            }
            byte[] rawName = new byte[DOS.MAX_FILE_NAME_LEN];

            // Copy values in the range [0x00,0xff], remapping the C0 control pictures range back
            // to control characters.
            int idx;
            for (idx = 0; idx < fileName.Length; idx++) {
                char ch = fileName[idx];
                if (ch >= 0x20 && ch <= 0xff) {
                    rawName[idx] = (byte)(ch | 0x80);
                } else if (ch >= CTRL_PIC_START && ch <= CTRL_PIC_END) {
                    rawName[idx] = (byte)((ch - CTRL_PIC_START) | 0x80);
                } else if (ch == CTRL_PIC_DEL) {
                    rawName[idx] = 0xff;
                } else {
                    return null;
                }
            }

            // Pad the rest of the buffer with high-ASCII spaces.
            for (; idx < DOS.MAX_FILE_NAME_LEN; idx++) {
                rawName[idx] = ' ' | 0x80;
            }

            return rawName;
        }

        /// <summary>
        /// Adjusts a filename to be compatible with this filesystem, removing invalid characters
        /// and shortening the name.
        /// </summary>
        /// <remarks>
        /// We're still working with strings, so we don't set the high bit here.
        /// </remarks>
        /// <param name="fileName">Filename to adjust.</param>
        /// <returns>Adjusted filename.</returns>
        public static string AdjustFileName(string fileName) {
            fileName = fileName.TrimEnd();      // remove trailing whitepace
            if (string.IsNullOrEmpty(fileName)) {
                return "Q";
            }

            char[] chars = fileName.ToCharArray();

            // Convert the string to ASCII values, stripping diacritical marks.
            ASCIIUtil.ReduceToASCII(chars, '?');
            // Remove commas.
            for (int i = 0; i < chars.Length; i++) {
                char ch = chars[i];
                if (ch == ',') {
                    chars[i] = '?';
                }
            }

            // First character must be >= 0x40.
            string cleaned;
            if (chars[0] < 0x40) {
                cleaned = 'X' + new string(chars);
            } else {
                cleaned = new string(chars);
            }

            // Clamp to max length by removing characters from the middle.
            if (cleaned.Length > DOS.MAX_FILE_NAME_LEN) {
                int firstLen = DOS.MAX_FILE_NAME_LEN / 2;        // 15
                int lastLen = DOS.MAX_FILE_NAME_LEN - (firstLen + 2);
                cleaned = cleaned.Substring(0, firstLen) + ".." +
                    cleaned.Substring(fileName.Length - lastLen, lastLen);
                Debug.Assert(cleaned.Length == DOS.MAX_FILE_NAME_LEN);
            }

            // Convert to upper case.  Not strictly required, but a good idea.
            return cleaned.ToUpperInvariant();
        }

        /// <summary>
        /// Adjusts a volume name to be compatible with this filesystem.
        /// </summary>
        /// <param name="volName">Volume name to adjust.</param>
        /// <returns>Adjusted volume name, or null if this filesystem doesn't support
        ///   volume names.</returns>
        public static string? AdjustVolumeName(string volName) {
            return null;
        }

        #endregion Filenames

        public override string ToString() {
            return "[DOS file entry: '" + mFileName + "']";
        }
    }
}
