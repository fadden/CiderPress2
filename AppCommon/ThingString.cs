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

using DiskArc;
using DiskArc.Arc;
using DiskArc.Disk;
using DiskArc.FS;
using DiskArc.Multi;

namespace AppCommon {
    /// <summary>
    /// Functions that return a human-readable string for an object or enumerated type.
    /// </summary>
    public static class ThingString {
        /// <summary>
        /// Returns a short (&lt;=11 char) string with an archive class name.
        /// </summary>
        public static string IArchive(IArchive arc) {
            if (arc is AppleSingle) {
                return "AppleSingle";
            } else if (arc is Binary2) {
                return "Binary II";
            } else if (arc is GZip) {
                return "gzip";
            } else if (arc is NuFX) {
                return "ShrinkIt";
            } else if (arc is Zip) {
                return "ZIP";
            } else {
                return "?" + arc.GetType().Name;
            }
        }

        /// <summary>
        /// Returns a short (&lt;= 9 char) string with a disk image class name.
        /// </summary>
        public static string IDiskImage(IDiskImage disk) {
            if (disk is DiskCopy) {
                return "DiskCopy";
            } else if (disk is Trackstar) {
                return "Trackstar";
            } else if (disk is TwoIMG) {
                return "2IMG";
            } else if (disk is UnadornedNibble525) {
                return "Nibble525";
            } else if (disk is UnadornedSector) {
                return "Unadorned";
            } else if (disk is Woz) {
                return "WOZ";
            } else {
                return "?" + disk.GetType().Name;
            }
        }

        /// <summary>
        /// Returns a short (&lt;= 10 char) string with a multipart layout class name.
        /// </summary>
        public static string IMultiPart(IMultiPart parts) {
            if (parts is AmUniDOS) {
                return "Am/UniDOS";
            } else if (parts is APM) {
                return "APM";
            } else if (parts is CFFA) {
                return "CFFA";
            } else if (parts is FocusDrive) {
                return "FocusDrive";
            } else if (parts is MacTS) {
                return "MacTS";
            } else if (parts is MicroDrive) {
                return "MicroDrive";
            } else if (parts is OzDOS) {
                return "OzDOS";
            } else if (parts is DOS_Hybrid) {
                return "Hybrid";
            } else if (parts is ProDOS_Embedded) {
                return "Embedded";
            } else {
                return "?" + parts.GetType().Name;
            }
        }

        /// <summary>
        /// Returns a short (&lt;= 7 char) string with a filesystem class name.
        /// </summary>
        public static string IFileSystem(IFileSystem fs) {
            if (fs is CPM) {
                return "CP/M";
            } else if (fs is DOS) {
                return "DOS 3.x";
            } else if (fs is HFS) {
                return "HFS";
            } else if (fs is Pascal) {
                return "Pascal";
            } else if (fs is ProDOS) {
                return "ProDOS";
            } else if (fs is RDOS) {
                return "RDOS";
            } else {
                return "?" + fs.GetType().Name;
            }
        }

        /// <summary>
        /// Returns a short (&lt;= 8 char) string with an RDOS flavor description.
        /// </summary>
        public static string RDOSFlavor(RDOS.RDOSFlavor flavor) {
            switch (flavor) {
                case RDOS.RDOSFlavor.RDOS32:
                    return "RDOS 3.2";
                case RDOS.RDOSFlavor.RDOS33:
                    return "RDOS 3.3";
                case RDOS.RDOSFlavor.RDOS3:
                    return "RDOS 3";
                default:
                    return "?" + flavor.ToString();
            }
        }

        /// <summary>
        /// Returns a short string with a partition subclass name.
        /// </summary>
        public static string Partition(Partition fs) {
            if (fs is APM_Partition) {
                return "APM partition";
            } else if (fs is FocusDrive_Partition) {
                return "Focus partition";
            } else {
                return "Partition";
            }
        }

        /// <summary>
        /// Returns a short string with an explanation of the result code.
        /// </summary>
        public static string AnalysisResult(FileAnalyzer.AnalysisResult result) {
            switch (result) {
                case FileAnalyzer.AnalysisResult.Undefined:
                    return "undefined problem (?!)";
                case FileAnalyzer.AnalysisResult.Success:
                    return "success";
                case FileAnalyzer.AnalysisResult.DubiousSuccess:
                    return "some damage was found";
                case FileAnalyzer.AnalysisResult.FileDamaged:
                    return "file is damaged";
                case FileAnalyzer.AnalysisResult.UnknownExtension:
                    return "file extension not known";
                case FileAnalyzer.AnalysisResult.ExtensionMismatch:
                    return "file contents do not match file extension";
                case FileAnalyzer.AnalysisResult.NotImplemented:
                    return "something wasn't implemented";
                default:
                    return "?" + result.ToString();
            }
        }

        /// <summary>
        /// Returns a fairly brief string with the filesystem type name.
        /// </summary>
        public static string FileSystemType(Defs.FileSystemType type) {
            switch (type) {
                case Defs.FileSystemType.Unknown:
                    return "Unknown";
                case Defs.FileSystemType.ProDOS:
                    return "ProDOS";
                case Defs.FileSystemType.DOS33:
                case Defs.FileSystemType.DOS32:
                    return "DOS 3.x";
                case Defs.FileSystemType.Pascal:
                    return "Pascal";
                case Defs.FileSystemType.MFS:
                    return "MFS";
                case Defs.FileSystemType.HFS:
                    return "HFS";
                case Defs.FileSystemType.Lisa:
                    return "Lisa";
                case Defs.FileSystemType.CPM:
                    return "CP/M";
                case Defs.FileSystemType.MSDOS:
                    return "FAT12/16";
                case Defs.FileSystemType.HighSierra:
                    return "High Sierra";
                case Defs.FileSystemType.ISO9660:
                    return "ISO-9660";
                case Defs.FileSystemType.RDOS:
                    return "RDOS";
                case Defs.FileSystemType.Unix:
                    return "UNIX";
                case Defs.FileSystemType.APM:
                    return "Apple Partition Map";
                case Defs.FileSystemType.MacTS:
                    return "Mac TS";
                case Defs.FileSystemType.AmUniDOS:
                    return "Am/UniDOS";
                case Defs.FileSystemType.OzDOS:
                    return "OzDOS";
                default:
                    return "?" + type.ToString();
            }
        }

        /// <summary>
        /// Returns a fairly brief string with the file kind description.
        /// </summary>
        public static string FileKind(Defs.FileKind kind) {
            switch (kind) {
                case Defs.FileKind.Unknown:
                    return "Unknown";
                case Defs.FileKind.UnadornedSector:
                    return "Unadorned Sector";
                case Defs.FileKind.UnadornedNibble525:
                    return "Unadorned 5.25\" Nibble";
                case Defs.FileKind.Woz:
                    return "WOZ";
                case Defs.FileKind.TwoIMG:
                    return "2IMG";
                case Defs.FileKind.DiskCopy:
                    return "DiskCopy 4.2";
                case Defs.FileKind.Trackstar:
                    return "Trackstar";
                case Defs.FileKind.Zip:
                    return "ZIP";
                case Defs.FileKind.NuFX:
                    return "NuFX (ShrinkIt)";
                case Defs.FileKind.Binary2:
                    return "Binary II";
                case Defs.FileKind.ACU:
                    return "AppleLink Compression Utility";
                case Defs.FileKind.GZip:
                    return "gzip";
                case Defs.FileKind.AppleSingle:
                    return "AppleSingle";
                case Defs.FileKind.DDD:
                    return "Dalton's Disk Disintegrator";
                default:
                    return "?" + kind.ToString();
            }
        }

        /// <summary>
        /// Returns a short (&lt;= 7 char) string with the compression format name.
        /// </summary>
        public static string CompressionFormat(Defs.CompressionFormat format) {
            switch (format) {
                case Defs.CompressionFormat.Uncompressed:
                    return "Stored";
                case Defs.CompressionFormat.Squeeze:
                    return "Squeeze";
                case Defs.CompressionFormat.NuLZW1:
                    return "LZW/1";
                case Defs.CompressionFormat.NuLZW2:
                    return "LZW/2";
                case Defs.CompressionFormat.LZC12:
                    return "LZC-12";
                case Defs.CompressionFormat.LZC16:
                    return "LZC-16";
                case Defs.CompressionFormat.Deflate:
                    return "Deflate";
                case Defs.CompressionFormat.Bzip2:
                    return "Bzip2";
                case Defs.CompressionFormat.Shrink:
                    return "Shrink";
                case Defs.CompressionFormat.Implode:
                    return "Implode";
                default:
                    return "?" + format.ToString();
            }
        }

        /// <summary>
        /// Returns a brief string with the media kind description.
        /// </summary>
        public static string MediaKind(Defs.MediaKind kind) {
            switch (kind) {
                case Defs.MediaKind.Unknown:
                    return "Unknown";
                case Defs.MediaKind.GCR_525:
                    return "GCR 5.25\"";
                case Defs.MediaKind.GCR_SSDD35:
                    return "GCR SS/DD 3.5\"";
                case Defs.MediaKind.GCR_DSDD35:
                    return "GCR DS/DD 3.5\"";
                case Defs.MediaKind.MFM_DSDD35:
                    return "MFM DS/DD 3.5\" 720KB";
                case Defs.MediaKind.MFM_DSHD35:
                    return "MFM DS/HD 3.5\" 1440KB";
                default:
                    return "?" + kind.ToString();
            }
        }

        /// <summary>
        /// Returns a short (8 char) string with the sector order name.
        /// </summary>
        public static string SectorOrder(Defs.SectorOrder order) {
            switch (order) {
                case Defs.SectorOrder.Unknown:
                    return "unknown";
                case Defs.SectorOrder.Physical:
                    return "physical";
                case Defs.SectorOrder.DOS_Sector:
                    return "dos/sect";
                case Defs.SectorOrder.ProDOS_Block:
                    return "pdos/blk";
                case Defs.SectorOrder.CPM_KBlock:
                    return "cpm/kblk";
                default:
                    return "?" + order.ToString();
            }
        }
    }
}
