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

using static DiskArc.Defs;

namespace DiskArc {
    /// <summary>
    /// <para>General-purpose chunk access for data stored as sectors or blocks
    /// (not nibbles).</para>
    /// </summary>
    public class GeneralChunkAccess : IChunkAccess {
        // Buffer full of zeroes.
        internal static readonly byte[] ZEROES = new byte[1024];

        //
        // IChunkAccess properties.
        //

        public bool IsReadOnly { get; private set; }
        public bool IsModified { get; set; }

        public long FormattedLength { get; private set; }
        public uint NumTracks { get; private set; }
        public uint NumSectorsPerTrack { get; private set; }

        public bool HasSectors { get { return NumSectorsPerTrack != 0; } }
        public bool HasBlocks { get { return NumSectorsPerTrack != 13; } }

        public SectorOrder FileOrder { get; set; }

        public SectorCodec? NibbleCodec => null;

        //
        // Sector skew tables.
        //
        internal static readonly uint[] sPhys2Dos = new uint[16] {
            0, 7, 14, 6, 13, 5, 12, 4, 11, 3, 10, 2, 9, 1, 8, 15
        };
        internal static readonly uint[] sDos2Phys = new uint[16] {
            0, 13, 11, 9, 7, 5, 3, 1, 14, 12, 10, 8, 6, 4, 2, 15
        };
        internal static readonly uint[] sPhys2Prodos = new uint[16] {
            0, 8, 1, 9, 2, 10, 3, 11, 4, 12, 5, 13, 6, 14, 7, 15
        };
        internal static readonly uint[] sProdos2Phys = new uint[16] {
            0, 2, 4, 6, 8, 10, 12, 14, 1, 3, 5, 7, 9, 11, 13, 15
        };
        internal static readonly uint[] sPhys2Cpm = new uint[16] {
            0, 11, 6, 1, 12, 7, 2, 13, 8, 3, 14, 9, 4, 15, 10, 5
        };
        internal static readonly uint[] sCpm2Phys = new uint[16] {
            0, 3, 6, 9, 12, 15, 2, 5, 8, 11, 14, 1, 4, 7, 10, 13
        };

        /// <summary>
        /// Offset of block 0 within file stream.
        /// </summary>
        private long mStartOffset;

        /// <summary>
        /// File stream.
        /// </summary>
        private Stream mFile;


        /// <summary>
        /// Constructor for a file stored as a series of 512-byte blocks.
        /// </summary>
        /// <param name="file">Data file.</param>
        /// <param name="startOffset">Offset of block 0 within file.</param>
        /// <param name="blockCount">Number of 512-byte disk blocks.</param>
        /// <exception cref="ArgumentException">Bad argument.</exception>
        public GeneralChunkAccess(Stream file, long startOffset, uint blockCount)
                : this(file, startOffset) {
            if (startOffset < 0) {
                throw new ArgumentOutOfRangeException(nameof(startOffset));
            }
            long length = blockCount * (long)BLOCK_SIZE;
            if (length > file.Length || startOffset > file.Length - length) {
                throw new ArgumentException("Invalid start/blockCount");
            }

            FormattedLength = length;
            NumTracks = 0;
            NumSectorsPerTrack = 0;
            FileOrder = SectorOrder.ProDOS_Block;
        }

        /// <summary>
        /// Constructor for a file stored as a series of 256-byte sectors.  The filesystem
        /// might use blocks, but the underlying storage is 5.25" disk sectors.
        /// </summary>
        /// <param name="file">Data file.</param>
        /// <param name="startOffset">Offset of T0S0 within file.</param>
        /// <param name="numTracks">Number of tracks (1-255).</param>
        /// <param name="sectorsPerTrack">Number of sectors per track (13, 16, or 32).</param>
        /// <param name="fileOrder">Order in which sectors appear in the file.</param>
        /// <exception cref="ArgumentException">Bad argument.</exception>
        public GeneralChunkAccess(Stream file, long startOffset, uint numTracks,
                uint sectorsPerTrack, SectorOrder fileOrder)
                : this(file, startOffset) {
            if (fileOrder == SectorOrder.Unknown) {
                throw new ArgumentException("Sector ordering must be specified for sector access");
            }
            if (numTracks == 0 || numTracks > 255) {
                // No real need to limit the upper bound.  Cap it for now in case we decide
                // it really needs to fit in a byte.
                throw new ArgumentException("Invalid number of tracks: " + numTracks);
            }
            if (sectorsPerTrack != 13 && sectorsPerTrack != 16 && sectorsPerTrack != 32) {
                throw new ArgumentException("Invalid value for sectors per track (" +
                    sectorsPerTrack + ")");
            }
            if (sectorsPerTrack != 16 && fileOrder != SectorOrder.DOS_Sector) {
                // 13-sector disks are always physical order, 32-sector disks are only used
                // for embedded volumes, so there's no skew translation table for them.
                throw new ArgumentException("Must use DOS order for disks with 13 or 32 sectors");
            }

            long length = numTracks * sectorsPerTrack * SECTOR_SIZE;
            if (length > file.Length || startOffset > file.Length - length) {
                throw new ArgumentException("Invalid length");
            }

            FormattedLength = length;
            NumTracks = numTracks;
            NumSectorsPerTrack = sectorsPerTrack;
            FileOrder = fileOrder;
        }

        /// <summary>
        /// Private constructor, invoked by the public constructors.
        /// </summary>
        /// <param name="file">Data file.</param>
        /// <param name="startOffset">Offset of T0S0 or block 0 within file.</param>
        /// <exception cref="ArgumentException">Bad argument.</exception>
        private GeneralChunkAccess(Stream file, long startOffset) {
            if (!file.CanSeek) {
                throw new ArgumentException("Stream must be seekable");
            }
            if (startOffset < 0) {
                throw new ArgumentException("Invalid start/length");
            }

            mFile = file;
            mStartOffset = startOffset;
            IsReadOnly = !mFile.CanWrite;
        }

        /// <summary>
        /// Mark the chunks as read-only.  This may be called by an IDiskImage if the file's
        /// structure is damaged.
        /// </summary>
        public void ReduceAccess() {
            IsReadOnly = true;
        }

        // Performs standard checks before accessing a track/sector.
        private void CheckSectorArgs(uint trk, uint sct, bool isWrite) {
            if (!HasSectors) {
                throw new InvalidOperationException("No sectors");
            }
            if (trk > NumTracks) {
                throw new ArgumentOutOfRangeException("Track out of range: " + trk +
                    " (max " + NumTracks + ")");
            }
            if (sct > NumSectorsPerTrack) {
                throw new ArgumentOutOfRangeException("Sector out of range: " + sct +
                    " (max " + NumSectorsPerTrack + ")");
            }
            if (isWrite && IsReadOnly) {
                throw new InvalidOperationException("Chunk access is read-only");
            }
        }

        // Performs standard checks before accessing a block.
        private void CheckBlockArgs(uint block, bool isWrite) {
            if (!HasBlocks) {
                throw new InvalidOperationException("No blocks");
            }
            if (block >= FormattedLength / BLOCK_SIZE) {
                throw new ArgumentOutOfRangeException("block", block, "block number out of range");
            }
            if (isWrite && IsReadOnly) {
                throw new InvalidOperationException("Chunk access is read-only");
            }
        }

        // IChunkAccess
        public void ReadSector(uint track, uint sect, byte[] data, int offset) {
            CheckSectorArgs(track, sect, false);
            long fileOffset;
            if (NumSectorsPerTrack == 16 && FileOrder != SectorOrder.DOS_Sector) {
                uint physSector = sDos2Phys[sect];
                uint logicalSector;
                switch (FileOrder) {
                    case SectorOrder.Physical:
                        logicalSector = physSector;
                        break;
                    case SectorOrder.DOS_Sector:
                        logicalSector = sPhys2Dos[physSector];
                        break;
                    case SectorOrder.ProDOS_Block:
                        logicalSector = sPhys2Prodos[physSector];
                        break;
                    case SectorOrder.CPM_KBlock:
                        logicalSector = sPhys2Cpm[physSector];
                        break;
                    default:
                        throw new DAException("Unexpected file order " + FileOrder);
                }
                fileOffset = (track * NumSectorsPerTrack + logicalSector) * SECTOR_SIZE;
            } else {
                // Simple linear mapping.
                fileOffset = (track * NumSectorsPerTrack + sect) * SECTOR_SIZE;
            }
            if (fileOffset >= FormattedLength) {
                throw new ArgumentOutOfRangeException("T/S out of range: " + track + "/" + sect);
            }
            mFile.Seek(mStartOffset + fileOffset, SeekOrigin.Begin);
            int actual = mFile.Read(data, offset, SECTOR_SIZE);
            if (actual == 0) {
                throw new IOException("Reached or passed EOF for chunk data source");
            } else if (actual != SECTOR_SIZE) {
                throw new IOException("Read partial sector (actual=" + actual + ")");
            }
            DAUtil.TotalReads++;
        }

        // IChunkAccess
        public void ReadBlock(uint block, byte[] data, int offset) {
            CheckBlockArgs(block, false);
            if (NumSectorsPerTrack == 16 && FileOrder == SectorOrder.DOS_Sector) {
                // Read as a pair of sectors, using DOS skewing.
                uint track = block >> 3;            // block / (NumSectorsPerTrack/2)
                uint sect = (block & 0x07) << 1;    // (block % (NumSectorsPerTrack/2)) * 2
                for (int i = 0; i < 2; i++) {
                    uint physSector = sProdos2Phys[sect + i];
                    uint logicalSector;
                    switch (FileOrder) {
                        case SectorOrder.Physical:
                            logicalSector = physSector;
                            break;
                        case SectorOrder.DOS_Sector:
                            logicalSector = sPhys2Dos[physSector];
                            break;
                        case SectorOrder.ProDOS_Block:
                            logicalSector = sPhys2Prodos[physSector];
                            break;
                        case SectorOrder.CPM_KBlock:
                            logicalSector = sPhys2Cpm[physSector];
                            break;
                        default:
                            throw new DAException("Unexpected file order " + FileOrder);
                    }
                    long fileOffset = (track * NumSectorsPerTrack + logicalSector) * SECTOR_SIZE;
                    if (fileOffset >= FormattedLength) {
                        throw new ArgumentOutOfRangeException("Block out of range: " + block);
                    }

                    mFile.Seek(mStartOffset + fileOffset, SeekOrigin.Begin);
                    int actual = mFile.Read(data, offset + SECTOR_SIZE * i, SECTOR_SIZE);
                    if (actual == 0) {
                        throw new IOException("Reached or passed EOF for chunk data source");
                    } else if (actual != SECTOR_SIZE) {
                        throw new IOException("Read partial block (actual=" + actual + ")");
                    }
                }
            } else {
                // Simple linear mapping.
                long fileOffset = block * BLOCK_SIZE;
                Debug.Assert(fileOffset < FormattedLength);

                mFile.Seek(mStartOffset + fileOffset, SeekOrigin.Begin);
                int actual = mFile.Read(data, offset, BLOCK_SIZE);
                if (actual == 0) {
                    throw new IOException("Reached or passed EOF for chunk data source");
                } else if (actual != BLOCK_SIZE) {
                    throw new IOException("Read partial block (actual=" + actual + ")");
                }
            }
            DAUtil.TotalReads++;
        }

        // IChunkAccess
        public void WriteSector(uint track, uint sect, byte[] data, int offset) {
            CheckSectorArgs(track, sect, true);
            if (sect > NumSectorsPerTrack) {
                throw new ArgumentOutOfRangeException("Sector out of range: " + sect +
                    " (max " + NumSectorsPerTrack + ")");
            }
            long fileOffset;
            if (NumSectorsPerTrack == 16 && FileOrder != SectorOrder.DOS_Sector) {
                uint physSector = sDos2Phys[sect];
                uint logicalSector;
                switch (FileOrder) {
                    case SectorOrder.Physical:
                        logicalSector = physSector;
                        break;
                    case SectorOrder.DOS_Sector:
                        logicalSector = sPhys2Dos[physSector];
                        break;
                    case SectorOrder.ProDOS_Block:
                        logicalSector = sPhys2Prodos[physSector];
                        break;
                    case SectorOrder.CPM_KBlock:
                        logicalSector = sPhys2Cpm[physSector];
                        break;
                    default:
                        throw new DAException("Unexpected file order " + FileOrder);
                }
                fileOffset = (track * NumSectorsPerTrack + logicalSector) * SECTOR_SIZE;
            } else {
                // Simple linear mapping.
                fileOffset = (track * NumSectorsPerTrack + sect) * SECTOR_SIZE;
            }
            if (fileOffset >= FormattedLength) {
                throw new ArgumentOutOfRangeException("T/S out of range: " + track + "/" + sect);
            }
            mFile.Seek(mStartOffset + fileOffset, SeekOrigin.Begin);
            mFile.Write(data, offset, SECTOR_SIZE);
            IsModified = true;
            DAUtil.TotalWrites++;
        }

        // IChunkAccess
        public void WriteBlock(uint block, byte[] data, int offset) {
            CheckBlockArgs(block, true);
            if (NumSectorsPerTrack == 16 && FileOrder == SectorOrder.DOS_Sector) {
                // Write as a pair of sectors.
                uint track = block >> 3;            // block / (NumSectorsPerTrack/2)
                uint sect = (block & 0x07) << 1;    // (block % (NumSectorsPerTrack/2)) * 2
                for (int i = 0; i < 2; i++) {
                    uint physSector = sProdos2Phys[sect + i];
                    uint logicalSector;
                    switch (FileOrder) {
                        case SectorOrder.Physical:
                            logicalSector = physSector;
                            break;
                        case SectorOrder.DOS_Sector:
                            logicalSector = sPhys2Dos[physSector];
                            break;
                        case SectorOrder.ProDOS_Block:
                            logicalSector = sPhys2Prodos[physSector];
                            break;
                        case SectorOrder.CPM_KBlock:
                            logicalSector = sPhys2Cpm[physSector];
                            break;
                        default:
                            throw new DAException("Unexpected file order " + FileOrder);
                    }
                    long fileOffset = (track * NumSectorsPerTrack + logicalSector) * SECTOR_SIZE;
                    if (fileOffset >= FormattedLength) {
                        throw new ArgumentOutOfRangeException("Block out of range: " + block);
                    }

                    mFile.Seek(mStartOffset + fileOffset, SeekOrigin.Begin);
                    mFile.Write(data, offset + SECTOR_SIZE * i, SECTOR_SIZE);
                }
            } else {
                // Simple linear mapping.
                long fileOffset = block * BLOCK_SIZE;
                if (fileOffset >= FormattedLength) {
                    throw new ArgumentOutOfRangeException("Block out of range: " + block);
                }
                mFile.Seek(mStartOffset + fileOffset, SeekOrigin.Begin);
                mFile.Write(data, offset, BLOCK_SIZE);
            }
            IsModified = true;
            DAUtil.TotalWrites++;
        }

        // IChunkAccess
        public bool TestSector(uint trk, uint sct, out bool writable) {
            CheckSectorArgs(trk, sct, false);
            writable = true;
            return true;
        }

        // IChunkAccess
        public bool TestBlock(uint block, out bool writable) {
            CheckBlockArgs(block, false);
            writable = true;
            return true;
        }

        // IChunkAccess
        public void Initialize() {
            if (IsReadOnly) {
                throw new InvalidOperationException("Chunk access is read-only");
            }
            IsModified = true;
            mFile.Seek(mStartOffset, SeekOrigin.Begin);
            for (int i = 0; i < FormattedLength / SECTOR_SIZE; i++) {
                // not worried about exception here
                mFile.Write(ZEROES, 0, SECTOR_SIZE);
            }
        }

        public override string ToString() {
            return "[GCA: tracks=" + NumTracks + " sectors=" + NumSectorsPerTrack +
                " size=" + (FormattedLength / 1024) + "KB order=" + FileOrder + "]";
        }
    }
}
