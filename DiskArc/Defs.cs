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

namespace DiskArc {
    /// <summary>
    /// General DiskArc library definitions.
    /// </summary>
    public static class Defs {
        /// <summary>
        /// DiskArc library version number.
        /// </summary>
        public static CommonUtil.Version LibVersion { get; } =
            new CommonUtil.Version(0, 2, 0, CommonUtil.Version.PreRelType.Dev, 1);

        public const string BUILD_TYPE =
#if DEBUG
            "DEBUG";
#else
            "";
#endif

        /// <summary>
        /// Library name and version number.
        /// </summary>
        /// <remarks>
        /// Try to limit this to 32 characters so it'll fit in .WOZ "creator" field.
        /// </remarks>
        public static readonly string NAME_AND_VERSION = "CP2 DiskArc v" + LibVersion.ToString();

        // I/O unit constants.
        public const int SECTOR_SIZE = 256;
        public const int BLOCK_SIZE = 512;
        public const int KBLOCK_SIZE = 1024;

        // Default volume number for 5.25" floppy sectors.
        public const int DEFAULT_525_VOLUME_NUM = 254;

        // Default volume name for ProDOS/Pascal/HFS (max 7 chars).
        public const string DEFAULT_VOL_NAME = "NEWDISK";

        /// <summary>
        /// SeekOrigin enumeration value for origin=Data: set the file position to the next
        /// location greater than or equal to the offset argument that contains data.  If the
        /// offset points to data, then the file position is set there.  If there is no data
        /// at or beyond the offset, the position is set to the file's length.
        /// </summary>
        /// <remarks>
        /// <para>Only valid for certain classes, e.g. <see cref="DiskFileStream"/>.</para>
        /// <para>A trivial implementation will just return the offset argument.</para>
        /// </remarks>
        public const SeekOrigin SEEK_ORIGIN_DATA = (SeekOrigin)100;

        /// <summary>
        /// SeekOrigin enumeration value for origin=Hole: set the file position to the next
        /// location greater than or equal to the offset argument that contains a hole.  If
        /// the offset points to a hole, then the file position is set there.  If there is no hole
        /// at or beyond the offset, the position is set to the end of the file.
        /// </summary>
        /// <remarks>
        /// <para>Only valid for certain classes, e.g. <see cref="DiskFileStream"/>.</para>
        /// <para>A trivial implementation will just return the file's length.</para>
        /// </remarks>
        public const SeekOrigin SEEK_ORIGIN_HOLE = (SeekOrigin)101;

        /// <summary>
        /// Floppy disk media kind.
        /// </summary>
        public enum MediaKind {
            Unknown = 0,
            GCR_525,                    // single-sided, 35-40 tracks (140KB+)
            GCR_SSDD35,                 // single-sided, 80 tracks (400KB), 524 bytes/block
            GCR_DSDD35,                 // double-sided, 80 tracks (800KB), 524 bytes/block
            MFM_DSDD35,                 // double-sided double-density (720KB), 9 sectors/track
            MFM_DSHD35,                 // double-sided high-density (1.44MB), 18 sectors/track
        }

        /// <summary>
        /// File part argument, used when opening a file.
        /// </summary>
        public enum FilePart {
            Unknown = 0,
            DataFork,       // used for files and disk images; data fork of an extended file
            RsrcFork,       // resource fork, on ProDOS/HFS and certain archive formats
            RawData,        // "raw" access in some situations on disk images
            //RawRsrc,        // "raw" access in some situations; otherwise same as RsrcFork
            DiskImage,      // archives: disk image; needed when adding to a record
        }

        /// <summary>
        /// File analyzer result.
        /// </summary>
        public enum FileKind {
            Unknown = 0,

            // Single disk image files.  We must be able to map the stream directly to
            // filesystem chunks, so compressed data formats are not in this section.
            UnadornedSector,            // .po, .do, .d13, .iso, .dsk*, .hdv, .img, .raw*, .dc6
            UnadornedNibble525,         // .nib, .nb2, .raw*
            Woz,                        // .woz
            TwoIMG,                     // .2mg, .2img
            DiskCopy,                   // .dsk*, .dc
            Trackstar,                  // .app
            // ? Sim //e HDV
            // ? FDI
            // ? Davex image ($e0/8004)

            // Multi-file archives.
            Zip,                        // .zip
            NuFX,                       // .shk, .sdk, .bxy, .sea, .bse
            Binary2,                    // .bny, bqy
            ACU,                        // .acu
            // ? StuffIt

            // Single-file wrappers.
            GZip,                       // .gz
            AppleSingle,                // .as (includes AppleDouble)
            DDD,                        // .ddd
            // ? DDDDeluxe
        }
        public static bool IsDiskImageFile(FileKind kind) {
            switch (kind) {
                case FileKind.UnadornedSector:
                case FileKind.UnadornedNibble525:
                case FileKind.Woz:
                case FileKind.TwoIMG:
                case FileKind.DiskCopy:
                case FileKind.Trackstar:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Filesystem type.
        /// </summary>
        /// <remarks>
        /// Numeric assignments come from the GS/OS FST definition.
        /// </remarks>
        public enum FileSystemType : byte {
            Unknown = 0,
            ProDOS = 1,                 // ProDOS / SOS
            DOS33 = 2,                  // DOS 3.3 [actually all 3.x]
            DOS32 = 3,                  // DOS 3.2 or 3.1 [not used internally]
            Pascal = 4,                 // Apple II UCSD Pascal
            MFS = 5,                    // Macintosh MFS
            HFS = 6,                    // Macintosh HFS
            Lisa = 7,                   // Lisa computer filesystem; very rare
            CPM = 8,                    // Apple II CP/M
            //CharFST = 9
            MSDOS = 10,                 // FAT filesystem
            HighSierra = 11,            // CD-ROM (early)
            ISO9660 = 12,               // CD-ROM
            //kFormatAppleShare = 13

            //
            // Non-GS/OS additions.
            //

            RDOS = 20,                  // RDOS (SSI) filesystem
            Unix = 21,                  // filesystem with UNIX timestamps; used for AppleSingle

            //kFormatGutenberg = 47,      // Gutenberg word processor format

            //
            // Multi-partition formats.  These aren't really filesystems, but it's handy to have a
            // single enumerated value to describe a disk's format.
            //

            APM = 40,                   // Apple Partition Map
            MacTS = 41,                 // old Apple partition format
            CFFA = 42,                  // CFFA with 4/6 or 8 partitions
            MicroDrive = 43,            // ///SHH Systeme's MicroDrive format
            FocusDrive = 44,            // Parsons Engineering FocusDrive format

            AmUniDOS = 50,              // two 400KB DOS 3.3 volumes on an 800KB disk
            OzDOS = 51,                 // two 400KB DOS 3.3 volumes on an 800KB split-block disk
        }

        /// <summary>
        /// Sector order, for 16-sector 5.25" disk images.  This specifies the order in which
        /// the data was read from the original disk when the image was created.  The variance
        /// is due to the skew mapping applied to the sector number.
        /// </summary>
        public enum SectorOrder {
            Unknown = 0,
            Physical,                   // no skew was applied when sectors were read
            DOS_Sector,                 // sectors written by DOS
            ProDOS_Block,               // blocks written by ProDOS (or Pascal)
            CPM_KBlock,                 // 1K blocks written by CP/M
        }

        /// <summary>
        /// GS/OS-style access flags.
        /// </summary>
        [Flags]
        public enum AccessFlags : byte {
            Destroy = 0x80,
            Rename = 0x40,
            BackupNeeded = 0x20,
            Invisible = 0x04,
            Write = 0x02,
            Read = 0x01,
        }
        public const AccessFlags ACCESS_UNLOCKED =
            AccessFlags.Destroy | AccessFlags.Rename | /*AccessFlags.BackupNeeded |*/
            AccessFlags.Write | AccessFlags.Read;
        public const AccessFlags ACCESS_LOCKED =
            /*AccessFlags.BackupNeeded |*/ AccessFlags.Read;
        public const AccessFlags ACCESS_UNWRITABLE =
            AccessFlags.Destroy | AccessFlags.Rename | /*AccessFlags.BackupNeeded |*/
            AccessFlags.Read;

        /// <summary>
        /// Compression formats, with values as defined by NuFX archive specification.
        /// </summary>
        public enum CompressionFormat : byte {
            Uncompressed = 0,
            Squeeze = 1,
            NuLZW1 = 2,
            NuLZW2 = 3,
            LZC12 = 4,
            LZC16 = 5,

            // NuFX Addendum additions.  Also used by other archive types.
            Deflate = 6,
            Bzip2 = 7,

            // Some old ZIP archive types.
            Shrink = 21,
            Implode = 26,

            Unknown = 0x7e,
            Default = 0x7f
        }
    }
}
