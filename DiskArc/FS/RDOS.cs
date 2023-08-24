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
        public const int CAT_TRACK = 1;             // disk catalog lives on track 1
        public const int CAT_ENTRY_LEN = 32;        // number of bytes in one catalog entry
        public const int MAX_FILENAME_LEN = 24;     // max length of a filename

        private static readonly byte[] SIG_PREFIX = new byte[] {
            (byte)'R' | 0x80, (byte)'D' | 0x80, (byte)'O' | 0x80, (byte)'S' | 0x80,
            (byte)' ' | 0x80
        };
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

        public long FreeSpace => throw new NotImplementedException();

        public GatedChunkAccess RawAccess { get; }

        //
        // Implementation-specific.
        //

        public enum RDOSFlavor {
            Unknown = 0, RDOS32, RDOS33, RDOS3
        }

        public RDOSFlavor Flavor { get; private set; }

        /// <summary>
        /// Data source.  Contents may be shared in various ways.
        /// </summary>
        internal IChunkAccess ChunkAccess { get; private set; }

        internal AppHook AppHook { get; private set; }

        /// <summary>
        /// List of open files.
        /// </summary>
        private OpenFileTracker mOpenFiles = new OpenFileTracker();


        // Delegate: test image to see if it's ours.
        public static TestResult TestImage(IChunkAccess chunkSource, AppHook appHook) {
            if (!chunkSource.HasSectors) {
                return TestResult.No;
            }
            bool is13Sector;
            if (chunkSource.FormattedLength == 13 * 35 * SECTOR_SIZE) {
                is13Sector = true;
            } else if (chunkSource.FormattedLength == 16 * 35 * SECTOR_SIZE) {
                is13Sector = false;
            } else {
                return TestResult.No;
            }

            byte[] sctBuf = new byte[SECTOR_SIZE];

            // Read T1S0 in to see if it looks like an RDOS header.  Because it's in sector 0
            // this won't tell us anything about the disk image sector order, or whether this
            // is RDOS33 vs RDOS3.
            chunkSource.ReadSector(CAT_TRACK, 0, sctBuf, 0);

            if (!RawData.CompareBytes(sctBuf, SIG_PREFIX, SIG_PREFIX.Length)) {
                return TestResult.No;
            }

            // "RDOS 2.1" or "RDOS 3.3"; check the major version number.
            char majVers = (char)(sctBuf[SIG_PREFIX.Length] & 0x7f);
            byte sysBlockCount = sctBuf[0x19];
            ushort sysBlockStart = RawData.GetU16LE(sctBuf, 0x1e);
            if ((majVers != '2' && majVers != '3') || sysBlockStart != 0 ||
                    (sysBlockCount != 13 * 2 && sysBlockCount != 16 * 2)) {
                return TestResult.No;
            }

            // This looks like an RDOS disk.  Now we need to determine which flavor and whether
            // the disk image sector order is being interpreted correctly.
            RDOSFlavor flavor;
            if (is13Sector) {
                flavor = RDOSFlavor.RDOS32;
            } else {
                if (majVers == '3') {
                    flavor = RDOSFlavor.RDOS33;
                } else {
                    flavor = RDOSFlavor.RDOS3;
                }
            }

            // The catalog code lives on T1S12 on 13-sector disks, and T0S1 on 16-sector disks.
            // Look for the string "<NAME>".
            int nameOffset, orMask;
            if (flavor == RDOSFlavor.RDOS32 || flavor == RDOSFlavor.RDOS3) {
                chunkSource.ReadSector(1, 12, sctBuf, 0, SectorOrder.Physical);
                nameOffset = 0xa2;
                orMask = 0x80;
            } else {
                chunkSource.ReadSector(0, 1, sctBuf, 0, SectorOrder.ProDOS_Block);
                nameOffset = 0x98;
                orMask = 0x00;
            }
            for (int i = 0; i < SIG_NAME.Length; i++) {
                if (sctBuf[nameOffset + i] != (SIG_NAME[i] | orMask)) {
                    Debug.WriteLine("Rejecting order=" + chunkSource.FileOrder);
                    return TestResult.No;
                }
            }

            {
                StringBuilder sb = new StringBuilder();
                for (uint sct = 0; sct < 10; sct++) {
                    if (flavor == RDOSFlavor.RDOS32 || flavor == RDOSFlavor.RDOS3) {
                        chunkSource.ReadSector(CAT_TRACK, sct, sctBuf, 0, SectorOrder.Physical);
                    } else {
                        chunkSource.ReadSector(CAT_TRACK, sct, sctBuf, 0, SectorOrder.ProDOS_Block);
                    }
                    bool done = false;
                    for (int offset = 0; offset < SECTOR_SIZE; offset += CAT_ENTRY_LEN) {
                        if (sctBuf[offset] == 0x00) {
                            done = true;
                            break;          // end of catalog
                        } else if (sctBuf[offset] == 0x80) {
                            continue;       // deleted
                        }
                        sb.Clear();
                        for (int i = 0; i < MAX_FILENAME_LEN; i++) {
                            sb.Append((char)(sctBuf[offset + i] & 0x7f));
                        }
                        Debug.WriteLine("GOT: '" + sb.ToString() + "'");
                    }

                    if (done) {
                        break;
                    }
                }
            }

            //return TestResult.Yes;
            return TestResult.No;
        }

        // Delegate: returns true if the size (in bytes) is valid for this filesystem.
        public static bool IsSizeAllowed(long size) {
            throw new NotImplementedException();
        }

        public RDOS(IChunkAccess chunks, AppHook appHook) {
            AppHook = appHook;
            throw new NotImplementedException();
        }

        public override string ToString() {
            return "RDOS";
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
            if (disposing) {
                // TODO
                RawAccess.AccessLevel = GatedChunkAccess.AccessLvl.Closed;
                mDisposed = true;
            }
        }

        // IFileSystem
        public void Flush() {
            mOpenFiles.FlushAll();
            throw new NotImplementedException();
        }

        // IFileSystem
        public void PrepareFileAccess(bool doScan) {
            throw new NotImplementedException();
        }

        // IFileSystem
        public void PrepareRawAccess() {
            throw new NotImplementedException();
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
            throw new NotImplementedException();
        }

        // IFileSystem
        public DiskFileStream OpenFile(IFileEntry entry, FileAccessMode mode, FilePart part) {
            throw new NotImplementedException();
        }

        internal void CloseFile(DiskFileStream ifd) {
            throw new NotImplementedException();
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
