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
    /// Filesystem used for the UCSD Pascal system on the Apple II.
    /// </summary>
    public class Pascal : IFileSystem {
        public const int VOL_DIR_START_BLOCK = 2;
        public const int MIN_VOL_SIZE = 6 * BLOCK_SIZE;     // 2 boot blocks, 4 dir blocks
        public const int MAX_VOL_SIZE = BLOCK_SIZE * 65535; // one block shy of 32MB
        public const int MAX_VOL_NAME_LEN = 7;
        public const int MAX_FILE_NAME_LEN = 15;

        private const string FILENAME_RULES =
            "1-15 characters, must not include '$=?,[#:', spaces, or control characters.";
        private const string VOLNAME_RULES =
            "1-7 characters, must not include '$=?,[#:', spaces, or control characters.";
        private static FSCharacteristics sCharacteristics = new FSCharacteristics(
            name: "Apple Pascal",
            canWrite: true,
            isHierarchical: false,
            dirSep: IFileEntry.NO_DIR_SEP,
            hasResourceForks: false,
            fnSyntax: FILENAME_RULES,
            vnSyntax: VOLNAME_RULES,
            tsStart: TimeStamp.PASCAL_MIN_TIMESTAMP,
            tsEnd: TimeStamp.PASCAL_MAX_TIMESTAMP
        );

        //
        // IFileSystem interfaces.
        //

        public FSCharacteristics Characteristics => sCharacteristics;
        public static FSCharacteristics SCharacteristics => sCharacteristics;

        public Notes Notes { get; } = new Notes();

        public bool IsReadOnly { get { return ChunkAccess.IsReadOnly || IsDubious; } }

        public bool IsDubious { get; internal set; }

        public long FreeSpace { get { return CalcFreeBlocks() * BLOCK_SIZE; } }

        public GatedChunkAccess RawAccess { get; private set; }

        //
        // Implementation-specific.
        //

        /// <summary>
        /// Data source.  Contents may be shared in various ways.
        /// </summary>
        internal IChunkAccess ChunkAccess { get; private set; }

        /// <summary>
        /// True if a change has been made that requires writing out the volume directory.  This
        /// is set for any change to file attributes.
        /// </summary>
        internal bool IsVolDirDirty { get; set; }

        /// <summary>
        /// Volume usage map.  Only valid in file-access mode.
        /// </summary>
        internal VolumeUsage? VolUsage { get; private set; }

        /// <summary>
        /// Application-specified options and message logging.
        /// </summary>
        internal AppHook AppHook { get; private set; }

        /// <summary>
        /// Volume directory header, as read from disk.
        /// </summary>
        private VolDirHeader mVolDirHeader;

        /// <summary>
        /// "Fake" volume directory entry, used to hold catalog entries.
        /// </summary>
        private IFileEntry mVolDirEntry;

        /// <summary>
        /// Record of an open file.
        /// </summary>
        private class OpenFileRec {
            public DOS_FileEntry Entry { get; private set; }
            public DOS_FileDesc FileDesc { get; private set; }

            public OpenFileRec(DOS_FileEntry entry, DOS_FileDesc desc) {
                Debug.Assert(desc.FileEntry == entry);  // check consistency and !Invalid
                Entry = entry;
                FileDesc = desc;
            }

            public override string ToString() {
                return "[DOS OpenFile: '" + Entry.FullPathName + "' part=" +
                    FileDesc.Part + " rw=" + FileDesc.CanWrite + "]";
            }
        }

        /// <summary>
        /// List of open files.
        /// </summary>
        private List<OpenFileRec> mOpenFiles = new List<OpenFileRec>();

        /// <summary>
        /// Total blocks present in the filesystem, as determined by the value in the volume
        /// header.  This is meaningless in block-edit mode, where the bounds are determined
        /// by the ChunkAccess.
        /// </summary>
        public int TotalBlocks { get { return mVolDirHeader.mVolBlockCount; } }

        /// <summary>
        /// True if we're in file-access mode, false if raw-access mode.
        /// </summary>
        private bool IsPreppedForFileAccess { get { return mVolDirEntry != IFileEntry.NO_ENTRY; } }


        /// <summary>
        /// Volume directory header contents, from block 2.
        /// </summary>
        internal class VolDirHeader {
            public const int LENGTH = Pascal_FileEntry.DIR_ENTRY_LEN;

            public ushort mFirstBlock;
            public ushort mNextBlock;
            public ushort mFileType;
            public byte[] mVolumeName = new byte[MAX_VOL_NAME_LEN + 1];
            public ushort mVolBlockCount;
            public ushort mFileCount;
            public ushort mLastAccess;
            public ushort mLastDateSet;
            public uint mReserved;

            public void Load(byte[] buf, int offset) {
                int startOffset = offset;
                mFirstBlock = RawData.ReadU16LE(buf, ref offset);
                mNextBlock = RawData.ReadU16LE(buf, ref offset);
                mFileType = RawData.ReadU16LE(buf, ref offset);
                for (int i = 0; i < mVolumeName.Length; i++) {
                    mVolumeName[i] = buf[offset++];
                }
                mVolBlockCount = RawData.ReadU16LE(buf, ref offset);
                mFileCount = RawData.ReadU16LE(buf, ref offset);
                mLastAccess = RawData.ReadU16LE(buf, ref offset);
                mLastDateSet = RawData.ReadU16LE(buf, ref offset);
                mReserved = RawData.ReadU32LE(buf, ref offset);
                Debug.Assert(offset - startOffset == LENGTH);
            }
            public void Store(byte[] buf, int offset) {
                int startOffset = offset;
                RawData.WriteU16LE(buf, ref offset, mFirstBlock);
                RawData.WriteU16LE(buf, ref offset, mNextBlock);
                RawData.WriteU16LE(buf, ref offset, mFileType);
                for (int i = 0; i < mVolumeName.Length; i++) {
                    buf[offset++] = mVolumeName[i];
                }
                RawData.WriteU16LE(buf, ref offset, mVolBlockCount);
                RawData.WriteU16LE(buf, ref offset, mFileCount);
                RawData.WriteU16LE(buf, ref offset, mLastAccess);
                RawData.WriteU16LE(buf, ref offset, mLastDateSet);
                RawData.WriteU32LE(buf, ref offset, mReserved);
                Debug.Assert(offset - startOffset == LENGTH);
            }
        }

        // Delegate: test image to see if it's ours.
        public static TestResult TestImage(IChunkAccess chunks, AppHook appHook) {
            if (!chunks.HasBlocks) {
                return TestResult.No;
            }
            if (!IsSizeAllowed(chunks.FormattedLength)) {
                return TestResult.No;
            }

            byte[] dataBuf = new byte[BLOCK_SIZE];
            chunks.ReadBlock(VOL_DIR_START_BLOCK, dataBuf, 0);
            VolDirHeader hdr = new VolDirHeader();
            hdr.Load(dataBuf, 0);

            if (ValidateVolDirHeader(hdr, chunks.FormattedLength / BLOCK_SIZE)) {
                return TestResult.Yes;
            } else {
                return TestResult.No;
            }
        }

        /// <summary>
        /// Validates the contents of the volume directory header.
        /// </summary>
        /// <returns>True if all is well.</returns>
        private static bool ValidateVolDirHeader(VolDirHeader hdr, long chunkBlocks) {
            const int EXPECTED_FIRST_DATA = 6;
            const int MAX_FILE_COUNT = 77;      // floor(2048/26) - 1

            // Validate values.  We could allow the possibility of a volume with a nonstandard
            // directory length, but as far as I know such a thing has never been created.
            if (hdr.mFirstBlock != 0 || hdr.mNextBlock != EXPECTED_FIRST_DATA ||
                    hdr.mFileType != 0 ||
                    hdr.mVolumeName[0] == 0 || hdr.mVolumeName[0] > MAX_VOL_NAME_LEN ||
                    hdr.mVolBlockCount < EXPECTED_FIRST_DATA || hdr.mVolBlockCount > chunkBlocks ||
                    hdr.mFileCount > MAX_FILE_COUNT) {
                return false;
            }

            // Do a quick scan on the volume name.
            for (int i = 0; i < hdr.mVolumeName[0]; i++) {
                byte bch = hdr.mVolumeName[i + 1];
                if (bch <= 0x20 || bch >= 0x7f) {
                    return false;   // fail if we see spaces or control chars
                }
            }

            return true;
        }

        // Delegate: returns true if the size (in bytes) is valid for this filesystem.
        public static bool IsSizeAllowed(long size) {
            if (size % BLOCK_SIZE != 0) {
                return false;       // must be blocks
            }
            if (size == MAX_VOL_SIZE + BLOCK_SIZE) {
                // Not really expecting a 32MB Pascal volume, but somebody might try it.
                // Allow.
            } else if (size < MIN_VOL_SIZE || size > MAX_VOL_SIZE) {
                return false;
            }
            return true;
        }

        public Pascal(IChunkAccess chunks, AppHook appHook) {
            Debug.Assert(chunks.HasBlocks);
            ChunkAccess = chunks;
            AppHook = appHook;

            RawAccess = new GatedChunkAccess(chunks);
            mVolDirHeader = new VolDirHeader();
            mVolDirEntry = IFileEntry.NO_ENTRY;
        }

        public override string ToString() {
            string id = mVolDirEntry == IFileEntry.NO_ENTRY ? "(raw)" : mVolDirEntry.FileName;
            return "[Pascal vol '" + id + "']";
        }

        // IDisposable generic finalizer.
        ~Pascal() {
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
                AppHook.LogW("Attempting to dispose of Pascal object twice");
                return;
            }
            if (!disposing) {
                // This is a GC finalization.  We can't know if the objects we have references
                // to have already been finalized, so all we can do is complain.
                AppHook.LogW("GC disposing of filesystem object " + this);
                if (mOpenFiles.Count != 0) {
                    foreach (OpenFileRec rec in mOpenFiles) {
                        AppHook.LogW("ProDOS FS finalized while file open: '" +
                            rec.Entry.FullPathName + "'");
                    }
                }
                return;
            }

            string id = mVolDirEntry == IFileEntry.NO_ENTRY ? "(raw)" : mVolDirEntry.FileName;
            AppHook.LogD("Pascal.Dispose(" + disposing + "): " + id);

            // This can happen easily if we have the filesystem in a "using" block and
            // something throws with a file open.  Post a warning and close all files.
            if (mOpenFiles.Count != 0) {
                AppHook.LogI("Pascal FS disposed with " + mOpenFiles.Count +" files open; closing");
                CloseAll();
            }

            try {
                FlushVolumeDir();
            } catch (IOException) {
                AppHook.LogE("Failed while attempting to flush volume bitmap");
            }

            if (mVolDirEntry != IFileEntry.NO_ENTRY) {
                // Invalidate all associated file entry objects.
                InvalidateFileEntries();
            }

            RawAccess.AccessLevel = GatedChunkAccess.AccessLvl.Closed;
            mDisposed = true;
        }

        // IFileSystem
        public void Flush() {
            foreach (OpenFileRec rec in mOpenFiles) {
                rec.FileDesc.Flush();
                rec.Entry.SaveChanges();
            }
            FlushVolumeDir();
        }

        private void FlushVolumeDir() {
            if (!IsVolDirDirty) {
                return;
            }
            Debug.Assert(IsPreppedForFileAccess);
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

            FlushVolumeDir();
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
            Pascal_FileEntry volDir = (Pascal_FileEntry)mVolDirEntry;
            if (!volDir.IsValid) {
                // Already done?  Shouldn't happen.
                return;
            }
            foreach (IFileEntry child in volDir) {
                Pascal_FileEntry entry = (Pascal_FileEntry)child;
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
            byte[] blockBuf = new byte[BLOCK_SIZE];
            ChunkAccess.ReadBlock(VOL_DIR_START_BLOCK, blockBuf, 0);
            mVolDirHeader.Load(blockBuf, 0);
            if (!ValidateVolDirHeader(mVolDirHeader, ChunkAccess.FormattedLength / BLOCK_SIZE)) {
                throw new DAException("Invalid volume directory header");
            }

            // Create volume usage map.  Assign "system" usage to the boot and directory blocks.
            VolUsage = new VolumeUsage(mVolDirHeader.mVolBlockCount);
            for (uint block = 0; block < mVolDirHeader.mNextBlock; block++) {
                VolUsage.MarkInUse(block);
                VolUsage.SetUsage(block, IFileEntry.NO_ENTRY);
            }

            // Scan the full catalog.
            mVolDirEntry = Pascal_FileEntry.ScanDirectory(this, mVolDirHeader);

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

           Debug.WriteLine(VolUsage.DebugDump());
        }

        private int CalcFreeBlocks() {
            if (!IsPreppedForFileAccess) {
                return -1;
            }
            int freeBlocks = 0;
            int nextStart = mVolDirHeader.mNextBlock;
            foreach (IFileEntry ientry in mVolDirEntry) {
                Pascal_FileEntry entry = (Pascal_FileEntry)ientry;
                freeBlocks += entry.StartBlock - nextStart;
                nextStart = entry.NextBlock;
            }
            freeBlocks += mVolDirHeader.mVolBlockCount - nextStart;
            return freeBlocks;
        }

        // IFileSystem
        public IMultiPart? FindEmbeddedVolumes() {
            return null;
        }

        // IFileSystem
        public void Format(string volumeName, int volumeNum, bool makeBootable) {
            throw new NotImplementedException();
        }

        private void CheckFileAccess(string op, IFileEntry ientry, bool wantWrite,
                FilePart part) {
            throw new NotImplementedException();
        }

        // IFileSystem
        public IFileEntry GetVolDirEntry() {
            if (!IsPreppedForFileAccess) {
                throw new IOException("Filesystem object not prepared for file access");
            }
            return mVolDirEntry;
        }

        // IFileSystem
        public DiskFileStream OpenFile(IFileEntry entry, FileAccessMode mode, FilePart part) {
            throw new NotImplementedException();
        }

        // IFileSystem
        public void CloseAll() {
            throw new NotImplementedException();
        }

        // IFileSystem
        public IFileEntry CreateFile(IFileEntry dirEntry, string fileName, CreateMode mode) {
            throw new NotImplementedException();
        }

        // IFileSystem
        public void AddRsrcFork(IFileEntry entry) {
            throw new IOException("Filesystem does not support resource forks");
        }

        // IFileSystem
        public void MoveFile(IFileEntry entry, IFileEntry destDir, string newFileName) {
            throw new NotImplementedException();
        }

        // IFileSystem
        public void DeleteFile(IFileEntry entry) {
            throw new NotImplementedException();
        }
    }
}
