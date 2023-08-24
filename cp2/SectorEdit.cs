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
using System.Text.RegularExpressions;

using AppCommon;
using CommonUtil;
using DiskArc;
using DiskArc.Disk;
using DiskArc.Multi;
using static DiskArc.Defs;


namespace cp2 {
    internal static class SectorEdit {
        #region Read Track

        /// <summary>
        /// Handles the "read-track" command.
        /// </summary>
        public static bool HandleReadTrack(string cmdName, string[] args, ParamsBag parms) {
            if (args.Length != 2) {
                CP2Main.ShowUsage(cmdName);
                return false;
            }
            // TODO: could dump a list of valid tracks if no args provided.  Currently no
            // interface to generate that, but we could add something WOZ-specific.

            if (!ExtArchive.OpenExtArc(args[0], false, true, parms, out DiskArcNode? rootNode,
                    out DiskArcNode? leafNode, out object? leaf, out IFileEntry endDirEntry)) {
                return false;
            }
            using (rootNode) {
                if (leaf is IArchive || leaf is Partition) {
                    Console.Error.WriteLine("Error: this can only be used with nibble images");
                    return false;
                }
                IDiskImage disk = (IDiskImage)leaf;
                if (disk is not INibbleDataAccess) {
                    Console.Error.WriteLine("Error: this can only be used with nibble images");
                    return false;
                }
                if (!Misc.StdChecks(disk, needWrite: false)) {
                    return false;
                }

                INibbleDataAccess nibs = (INibbleDataAccess)disk;
                bool is525 = (nibs.DiskKind == MediaKind.GCR_525);
                if (!ParseTrackNum(args[1], is525, out uint trackNum, out uint trackFraction)) {
                    return false;
                }
                try {
                    if (!nibs.GetTrackBits(trackNum, trackFraction, out CircularBitBuffer? cbb)) {
                        Console.Error.WriteLine("Error: track not found");
                        return false;
                    }
                    Console.WriteLine("Disk type: " + ThingString.MediaKind(nibs.DiskKind));
                    DumpTrack(trackNum, trackFraction, is525, cbb, parms);
                } catch (ArgumentOutOfRangeException) {
                    Console.Error.WriteLine("Error: track number out of range");
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Track number format.  This allows "0", "0.", and "0.5", with the integer portion in
        /// group 1 and the fractional portion in optional group 2.  We accept both '.' and ','.
        /// </summary>
        private static readonly Regex sTrackNumRegex = new Regex(TRACK_NUM_PATTERN);
        private const string TRACK_NUM_PATTERN = @"^(\d+)[\.,]?(\d+)?$";

        /// <summary>
        /// Parses a fractional track number.
        /// </summary>
        /// <param name="trackStr">String to parse.</param>
        /// <param name="trackNum">Result: Track number, 0-39 (5.25") or 0-79 (3.5").</param>
        /// <param name="trackFraction">Result: Track fraction, 0-3 (5.25" quarter track) or
        ///   0-1 (3.5" disk side).</param>
        /// <returns>True if parsing was successful.</returns>
        private static bool ParseTrackNum(string trackStr, bool is525, out uint trackNum,
                out uint trackFraction) {
            trackNum = trackFraction = 0;

            MatchCollection matches = sTrackNumRegex.Matches(trackStr);
            if (matches.Count != 1) {
                Console.Error.WriteLine("Error: invalid track number: " + trackStr);
                return false;
            }
            string trackNumStr = matches[0].Groups[1].Value;
            string trackFracStr = (matches[0].Groups.Count < 3) ? string.Empty :
                matches[0].Groups[2].Value;

            if (!uint.TryParse(trackNumStr, out trackNum)) {
                // Possible if number exceeds uint limit.
                Console.Error.WriteLine("Error: bad track integer value");
                return false;
            }
            if (!string.IsNullOrEmpty(trackFracStr)) {
                if (!uint.TryParse(trackFracStr, out trackFraction)) {
                    Console.Error.WriteLine("Error: bad track fraction value");
                    return false;
                }
                if (is525) {
                    // Convert N.0, N.25, N.5, N.75 to 0/1/2/3.
                    switch (trackFraction) {
                        case 0:
                            break;
                        case 25:
                            trackFraction = 1;
                            break;
                        case 5:
                            trackFraction = 2;
                            break;
                        case 75:
                            trackFraction = 3;
                            break;
                        default:
                            Console.Error.WriteLine("Error: unknown track fraction value");
                            return false;
                    }
                }
            }
            return true;
        }

        /// <summary>
        /// Dumps the contents of the circular bit buffer.
        /// </summary>
        /// <param name="trackNum">Track number (for display).</param>
        /// <param name="trackFraction">Track fraction (for display).</param>
        /// <param name="cbb">Circular bit buffer with track data.</param>
        /// <param name="parms">Application parameters.</param>
        private static void DumpTrack(uint trackNum, uint trackFraction, bool is525,
                CircularBitBuffer cbb, ParamsBag parms) {
            // Copy the byte data into a memory stream.
            MemoryStream trackBuf = new MemoryStream();
            int startPosn = cbb.BitPosition = 0;
            while (true) {
                byte val;
                if (parms.Latch) {
                    val = cbb.LatchNextByte();
                } else {
                    val = cbb.ReadOctet();
                }
                int curPosn = cbb.BitPosition;
                trackBuf.WriteByte(val);
                if (curPosn <= startPosn) {
                    break;      // wrapped
                }
                startPosn = curPosn;
            }

            StringBuilder sb = new StringBuilder();
            sb.Append("# Track ");
            if (is525) {
                double fracVal;
                if (is525) {
                    fracVal = trackFraction * 0.25;
                } else {
                    fracVal = trackFraction * 0.1;
                }
                sb.Append((trackNum + fracVal).ToString("N2"));
            } else {
                sb.Append(trackNum);
                sb.Append(" / side ");
                sb.Append(trackFraction);
            }
            sb.Append(": ");
            sb.Append(trackBuf.Length);
            sb.Append(" ($");
            sb.Append(trackBuf.Length.ToString("x4"));
            sb.Append(") bytes");
            if (parms.Latch) {
                sb.Append(" (latched)");
            } else {
                sb.Append(" (octets)");
            }
            Console.WriteLine(sb.ToString());

            trackBuf.Position = 0;
            Misc.OutputStreamHexDump(trackBuf);
        }

        #endregion Read Track

        #region Read Sector

        /// <summary>
        /// Handles the "read-sector" command.
        /// </summary>
        public static bool HandleReadSector(string cmdName, string[] args, ParamsBag parms) {
            if (args.Length != 3) {
                CP2Main.ShowUsage(cmdName);
                return false;
            }

            if (!ExtArchive.OpenExtArc(args[0], false, true, parms, out DiskArcNode? rootNode,
                    out DiskArcNode? leafNode, out object? leaf, out IFileEntry endDirEntry)) {
                return false;
            }
            using (rootNode) {
                if (leaf is IArchive || leaf is Partition) {
                    Console.Error.WriteLine("Error: this can only be used with disk images");
                    return false;
                }
                IDiskImage disk = (IDiskImage)leaf;
                if (!Misc.StdChecks(disk, needWrite: false)) {
                    return false;
                }
                // We currently work from the IChunkAccess object.  If we want to handle half
                // tracks and 3.5" disks, we'll need to access the nibble data directly.
                IChunkAccess? chunks = DiskUtil.GetChunkAccess(disk);
                if (chunks == null) {
                    return false;
                }

                return DoReadSector(chunks, args[1], args[2], parms);
            }
        }

        /// <summary>
        /// Reads the specified track/sector and prints it to the console.
        /// </summary>
        private static bool DoReadSector(IChunkAccess chunks, string trackStr, string sectorStr,
                ParamsBag parms) {
            if (!chunks.HasSectors) {
                Console.Error.WriteLine("Error: this image cannot be accessed as sectors");
                return false;
            }
            if (!uint.TryParse(trackStr, out uint trackNum) || trackNum >= chunks.NumTracks) {
                Console.Error.WriteLine("Error: invalid track number (whole tracks only, max " +
                    chunks.NumTracks + ")");
                return false;
            }
            if (!uint.TryParse(sectorStr, out uint sectorNum) ||
                    sectorNum >= chunks.NumSectorsPerTrack) {
                Console.Error.WriteLine("Error: invalid sector number (max " +
                    chunks.NumSectorsPerTrack + ")");
                return false;
            }

            byte[] sctBuf = new byte[SECTOR_SIZE];
            try {
                chunks.ReadSector(trackNum, sectorNum, sctBuf, 0);
            } catch (IOException ex) {
                Console.Error.WriteLine("Error: " + ex.Message);
                return false;
            }

            // Let the formatter do the work.
            Formatter.FormatConfig cfg = new Formatter.FormatConfig();
            cfg.UpperHexDigits = false;
            cfg.HexDumpConvFunc = HighASCIICharConv;
            Formatter fmt = new Formatter(cfg);
            StringBuilder sb = fmt.FormatHexDump(sctBuf);

            Console.WriteLine("# track " + trackNum + ", sector " + sectorNum);
            Console.Write(sb.ToString());
            return true;
        }

        #endregion Read Sector

        #region Write Sector

        /// <summary>
        /// Handles the "write-sector" command.
        /// </summary>
        public static bool HandleWriteSector(string cmdName, string[] args, ParamsBag parms) {
            if (args.Length != 4) {
                CP2Main.ShowUsage(cmdName);
                return false;
            }

            if (!ExtArchive.OpenExtArc(args[0], false, false, parms, out DiskArcNode? rootNode,
                    out DiskArcNode? leafNode, out object? leaf, out IFileEntry endDirEntry)) {
                return false;
            }
            using (rootNode) {
                if (leaf is IArchive || leaf is Partition) {
                    Console.Error.WriteLine("Error: this can only be used with disk images");
                    return false;
                }
                IDiskImage disk = (IDiskImage)leaf;
                if (!Misc.StdChecks(disk, needWrite: true)) {
                    return false;
                }
                IChunkAccess? chunks = DiskUtil.GetChunkAccess(disk);
                if (chunks == null) {
                    return false;
                }

                bool success = DoWriteSector(chunks, args[1], args[2], args[3], parms);
                try {
                    leafNode.SaveUpdates(parms.Compress);
                } catch (Exception ex) {
                    Console.Error.WriteLine("Error: update failed: " + ex.Message);
                    return false;
                }
                if (!success) {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Writes the specified track/sector, reading data from a file or stdin.
        /// </summary>
        private static bool DoWriteSector(IChunkAccess chunks, string trackStr, string sectorStr,
                string inputPathName, ParamsBag parms) {
            if (!chunks.HasSectors) {
                Console.Error.WriteLine("Error: this image cannot be accessed as sectors");
                return false;
            }
            if (!uint.TryParse(trackStr, out uint trackNum) || trackNum >= chunks.NumTracks) {
                Console.Error.WriteLine("Error: invalid track number (whole tracks only, max " +
                    chunks.NumTracks + ")");
                return false;
            }
            if (!uint.TryParse(sectorStr, out uint sectorNum) ||
                    sectorNum >= chunks.NumSectorsPerTrack) {
                Console.Error.WriteLine("Error: invalid sector number (max " +
                    chunks.NumSectorsPerTrack + ")");
                return false;
            }

            int actual = Misc.ReadHexDump(inputPathName, SECTOR_SIZE, out byte[] dataBuf);
            if (actual != SECTOR_SIZE) {
                if (actual > 0) {
                    Console.Error.WriteLine("Error: read partial sector (" + actual + " bytes)");
                } else {
                    Console.Error.WriteLine("Error: failed to read sector data");
                }
                return false;
            }
            Debug.Assert(dataBuf.Length == SECTOR_SIZE);

            if (parms.Debug) {
                Formatter.FormatConfig cfg = new Formatter.FormatConfig();
                cfg.UpperHexDigits = false;
                cfg.HexDumpConvFunc = HighASCIICharConv;
                Formatter fmt = new Formatter(cfg);
                StringBuilder sb = fmt.FormatHexDump(dataBuf);

                Console.WriteLine("Data read:");
                Console.Write(sb.ToString());
            }


            if (parms.Verbose) {
                byte[] sctBuf = new byte[SECTOR_SIZE];
                try {
                    chunks.ReadSector(trackNum, sectorNum, sctBuf, 0);
                    int diffs = CountDifferences(sctBuf, dataBuf);
                    Console.WriteLine("Found changes to " + diffs + " bytes");
                } catch (IOException) {
                    // This could fail on DOS 3.2 sectors that have never been written.
                }
            }
            try {
                chunks.WriteSector(trackNum, sectorNum, dataBuf, 0);
            } catch (IOException ex) {
                Console.Error.WriteLine("Error: " + ex.Message);
                return false;
            }
            return true;
        }

        #endregion Write Sector

        #region Read Block

        /// <summary>
        /// Handles the "read-block" command.
        /// </summary>
        public static bool HandleReadBlock(string cmdName, string[] args, ParamsBag parms) {
            return DoHandleReadBlock(cmdName, args, false, parms);
        }

        /// <summary>
        /// Handles the "read-block-cpm" command.
        /// </summary>
        public static bool HandleReadBlockCPM(string cmdName, string[] args, ParamsBag parms) {
            return DoHandleReadBlock(cmdName, args, true, parms);
        }

        public static bool DoHandleReadBlock(string cmdName, string[] args, bool isCpm,
                ParamsBag parms) {
            if (args.Length != 2) {
                CP2Main.ShowUsage(cmdName);
                return false;
            }

            if (!ExtArchive.OpenExtArc(args[0], false, true, parms, out DiskArcNode? rootNode,
                    out DiskArcNode? leafNode, out object? leaf, out IFileEntry endDirEntry)) {
                return false;
            }
            using (rootNode) {
                if (leaf is IArchive) {
                    Console.Error.WriteLine("Error: this can only be used with disk images");
                    return false;
                }
                IChunkAccess? chunks;
                if (leaf is IDiskImage) {
                    IDiskImage disk = (IDiskImage)leaf;
                    if (!Misc.StdChecks(disk, needWrite: false)) {
                        return false;
                    }
                    chunks = DiskUtil.GetChunkAccess(disk);
                } else {
                    Debug.Assert(leaf is Partition);
                    Partition part = (Partition)leaf;
                    chunks = DiskUtil.GetChunkAccess(part);
                }
                if (chunks == null) {
                    return false;
                }

                return DoReadBlock(chunks, args[1], isCpm, parms);
            }
        }

        /// <summary>
        /// Reads the specified block and prints it to the console.
        /// </summary>
        private static bool DoReadBlock(IChunkAccess chunks, string blockStr, bool isCpm,
                ParamsBag parms) {
            if (!chunks.HasBlocks) {
                Console.Error.WriteLine("Error: this image cannot be accessed as blocks");
                return false;
            }
            uint totalBlocks = (uint)(chunks.FormattedLength / BLOCK_SIZE);
            if (!uint.TryParse(blockStr, out uint blockNum) || blockNum >= totalBlocks) {
                Console.Error.WriteLine("Error: invalid block number (max " + totalBlocks + ")");
                return false;
            }

            byte[] blkBuf = new byte[BLOCK_SIZE];
            try {
                if (isCpm) {
                    chunks.ReadBlock(blockNum, blkBuf, 0, SectorOrder.CPM_KBlock);
                } else {
                    chunks.ReadBlock(blockNum, blkBuf, 0);
                }
            } catch (IOException ex) {
                Console.Error.WriteLine("Error: " + ex.Message);
                return false;
            }

            // Let the formatter do the work.
            Formatter.FormatConfig cfg = new Formatter.FormatConfig();
            cfg.UpperHexDigits = false;
            cfg.HexDumpConvFunc = HighASCIICharConv;
            Formatter fmt = new Formatter(cfg);
            StringBuilder sb = fmt.FormatHexDump(blkBuf);

            Console.WriteLine("# block " + blockNum + (isCpm ? " (CP/M order)" : ""));
            Console.Write(sb.ToString());
            return true;
        }

        #endregion Read Block

        #region Write Block

        /// <summary>
        /// Handles the "write-block" command.
        /// </summary>
        public static bool HandleWriteBlock(string cmdName, string[] args, ParamsBag parms) {
            return DoHandleWriteBlock(cmdName, args, false, parms);
        }

        /// <summary>
        /// Handles the "write-block-cpm" command.
        /// </summary>
        public static bool HandleWriteBlockCPM(string cmdName, string[] args, ParamsBag parms) {
            return DoHandleWriteBlock(cmdName, args, true, parms);
        }

        public static bool DoHandleWriteBlock(string cmdName, string[] args, bool isCpm,
                ParamsBag parms) {
            if (args.Length != 3) {
                CP2Main.ShowUsage(cmdName);
                return false;
            }

            if (!ExtArchive.OpenExtArc(args[0], false, false, parms, out DiskArcNode? rootNode,
                    out DiskArcNode? leafNode, out object? leaf, out IFileEntry endDirEntry)) {
                return false;
            }
            using (rootNode) {
                if (leaf is IArchive) {
                    Console.Error.WriteLine("Error: this can only be used with disk images");
                    return false;
                }
                IChunkAccess? chunks;
                if (leaf is IDiskImage) {
                    IDiskImage disk = (IDiskImage)leaf;
                    if (!Misc.StdChecks(disk, needWrite: true)) {
                        return false;
                    }
                    chunks = DiskUtil.GetChunkAccess(disk);
                } else {
                    Debug.Assert(leaf is Partition);
                    Partition part = (Partition)leaf;
                    chunks = DiskUtil.GetChunkAccess(part);
                }
                if (chunks == null) {
                    return false;
                }

                bool success = DoWriteBlock(chunks, args[1], args[2], isCpm, parms);
                try {
                    leafNode.SaveUpdates(parms.Compress);
                } catch (Exception ex) {
                    Console.Error.WriteLine("Error: update failed: " + ex.Message);
                    return false;
                }
                if (!success) {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Writes the specified block, reading data from a file or stdin.
        /// </summary>
        private static bool DoWriteBlock(IChunkAccess chunks, string blockStr,
                string inputPathName, bool isCpm, ParamsBag parms) {
            if (!chunks.HasBlocks) {
                Console.Error.WriteLine("Error: this image cannot be accessed as blocks");
                return false;
            }
            uint totalBlocks = (uint)(chunks.FormattedLength / BLOCK_SIZE);
            if (!uint.TryParse(blockStr, out uint blockNum) || blockNum >= totalBlocks) {
                Console.Error.WriteLine("Error: invalid block number");
                return false;
            }

            int actual = Misc.ReadHexDump(inputPathName, BLOCK_SIZE, out byte[] dataBuf);
            if (actual != BLOCK_SIZE) {
                if (actual > 0) {
                    Console.Error.WriteLine("Error: read partial block (" + actual + " bytes)");
                } else {
                    Console.Error.WriteLine("Error: failed to read block data");
                }
                return false;
            }
            Debug.Assert(dataBuf.Length == BLOCK_SIZE);

            if (parms.Debug) {
                Formatter.FormatConfig cfg = new Formatter.FormatConfig();
                cfg.UpperHexDigits = false;
                cfg.HexDumpConvFunc = HighASCIICharConv;
                Formatter fmt = new Formatter(cfg);
                StringBuilder sb = fmt.FormatHexDump(dataBuf);

                Console.WriteLine("Data read:");
                Console.Write(sb.ToString());
            }


            if (parms.Verbose) {
                byte[] blkBuf = new byte[BLOCK_SIZE];
                try {
                    if (isCpm) {
                        chunks.ReadBlock(blockNum, blkBuf, 0, SectorOrder.CPM_KBlock);
                    } else {
                        chunks.ReadBlock(blockNum, blkBuf, 0);
                    }
                    int diffs = CountDifferences(blkBuf, dataBuf);
                    Console.WriteLine("Found changes to " + diffs + " bytes");
                } catch (IOException) {
                    // Not expected; will likely fail on the write.
                }
            }
            try {
                if (isCpm) {
                    chunks.WriteBlock(blockNum, dataBuf, 0, SectorOrder.CPM_KBlock);
                } else {
                    chunks.WriteBlock(blockNum, dataBuf, 0);
                }
            } catch (IOException ex) {
                Console.Error.WriteLine("Error: " + ex.Message);
                return false;
            }
            return true;
        }

        #endregion Write Block

        #region Misc

        /// <summary>
        /// Converts a high-ASCII character to printable form, by stripping the high bit and
        /// replacing control characters.
        /// </summary>
        /// <remarks>
        /// This is used for hex dumps, which traditionally show '.' for control characters,
        /// so we don't use the Control Pictures block here.
        /// </remarks>
        /// <param name="val">Byte value to convert.</param>
        /// <returns>Printable character.</returns>
        public static char HighASCIICharConv(byte val) {
            val &= 0x7f;
            if (val < 0x20) {
                return '.'; //(char)(val + ASCIIUtil.CTRL_PIC_START);
            } else if (val == 0x7f) {
                return '.'; //(char)ASCIIUtil.CTRL_PIC_DEL;
            } else {
                return (char)val;
            }
        }

        private static int CountDifferences(byte[] buf1, byte[] buf2) {
            Debug.Assert(buf1.Length == buf2.Length);
            int count = 0;
            for (int i = 0; i < buf1.Length; i++) {
                if (buf1[i] != buf2[i]) {
                    count++;
                }
            }
            return count;
        }

        #endregion Misc
    }
}
