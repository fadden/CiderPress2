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

using static DiskArc.Defs;

namespace DiskArc {
    /// <summary>
    /// <para>This class provides access to "chunked" data, i.e. sectors/blocks, as
    /// they would be read by a filesystem.  This may represent a subset of the underlying
    /// storage, and may access the bytes in a non-linear order.  The goal is to provide the
    /// abstraction of a simple block device, whether the actual storage is a file on disk or
    /// in memory, physical media, or an embedded sub-volume.  File sector order conversions are
    /// performed here.</para>
    ///
    /// <para>Access to finer-grained features of the underlying media, such as raw nibble
    /// data, half tracks, or the 12 "tag" bytes on 800KB floppies, is not provided through
    /// this interface.</para>
    ///
    /// <para>Construction of these objects is instance-specific.</para>
    /// </summary>
    /// <remarks>
    /// <para>These are typically held internally by an <see cref="IDiskImage"/> or
    /// <see cref="IFileSystem"/> (or both), and are only accessible to application code when
    /// a filesystem object is in "raw access" mode.  Unique ownership of IChunkAccess objects
    /// is not required, as they are not Disposable.</para>
    ///
    /// <para>Unreadable blocks are possible with nibble images and physical media.  Callers
    /// must recognize that the read and write calls can throw I/O errors.  Generally speaking,
    /// failures will be hard rather than transient, but anything is possible with physical
    /// media.</para>
    ///
    /// <para>The object will hold a reference to a Stream that it does not manage.  Multiple
    /// chunk sources may be referencing separate or (if read-only) overlapping portions of
    /// the same file.  The caller is expected to manage the lifespan of the Stream.</para>
    ///
    /// <para>IChunkAccess instances that cache data use write-through caching, i.e. all writes
    /// are made to the underlying storage.  This allows the instance to be discarded without
    /// needing to flush it first (and obviates the need for a Flush call).  Modifications made
    /// to the underlying storage by the application may not be noticed by the IChunkAccess
    /// instance if it has cached the data, so bypassing the IChunkAccess should be avoided.</para>
    /// </remarks>
    //
    // The underlying media could be:
    // - Sectors from a 16-sector 5.25" floppy.
    // - Sectors from a 13-sector 5.25" floppy.
    // - An embedded DOS image with 50 tracks and 32 sectors.
    // - Fixed or variable-length nibble data from a floppy.
    // - A 3.5" floppy with 80 tracks (40 per side) and 8-12 512-byte blocks per track.
    // - A SCSI hard drive or SD card.
    // - A CD-ROM image.
    // However, what we present to the world is one of two things:
    // - A sector device, with N tracks and M 256-byte sectors per track.
    // - A block device, with N blocks (512-byte).
    //
    // The filesystem code doesn't need access to raw nibbles, or to know that track 0 has
    // multiple boot sectors, so that's not part of this interface.  Filesystems with larger
    // block sizes (e.g. CP/M, which ranges from 1KB to 16KB per block) will need to provide
    // their own block aggregation.
    public interface IChunkAccess {
        /// <summary>
        /// True if writing is not allowed.
        /// </summary>
        bool IsReadOnly { get; }

        /// <summary>
        /// <para>Modification flag, set to true whenever a write operation is performed.  The
        /// application should reset this to false when the modifications have been saved.</para>
        /// </summary>
        /// <remarks>
        /// <para>The value may be set even on a "gated" chunk access object that has been
        /// marked inaccessible.</para>
        /// <para>This is not a "dirty" flag.  IChunkAccess instances do not cache data, and
        /// thus cannot be dirty.</para>
        /// <para>The state of this flag has no impact on the operation of IChunkAccess instances.
        /// This exists purely for the benefit of applications, so that they can tell when a disk
        /// image has been modified (perhaps to show a "save needed" indicator, or know that a
        /// disk image must be recompressed into an archive).  Because this can be set freely
        /// by the application, IDiskImage implementations should not rely on this as a dirty
        /// block indicator.</para>
        /// </remarks>
        //[Obsolete("this may go away")]
        bool IsModified { get; set; }

        /// <summary>
        /// Block/sector read counter.  Incremented whenever a read is made.
        /// </summary>
        long ReadCount { get; }

        /// <summary>
        /// Block/sector write counter.  Incremented whenever a write is made.
        /// </summary>
        long WriteCount { get; }

        /// <summary>
        /// The total formatted size of the media, in bytes.
        /// </summary>
        /// <remarks>
        /// <para>The length will be a multiple of the largest chunk size, e.g. if 512-byte blocks
        /// are supported then the length will be a multiple of 512.</para>
        /// <para>For a block/sector image this is just the length of the data.  For a nibble
        /// image this is how much data can be stored in blocks or sectors.  This is intended for
        /// filesystem calculations, and so ignores features like tag bytes on
        /// 800KB floppies.</para>
        /// </remarks>
        long FormattedLength { get; }

        /// <summary>
        /// For sector-based images, such as 5.25" floppy disks and embedded DOS volumes, this
        /// holds the number of whole tracks.  For all other images, this will hold zero.
        /// </summary>
        /// <remarks>
        /// <para>Usually 35 or 40 for 5.25" disks, but could be 80.  35, 40, or 50 for embedded
        /// DOS images.</para>
        /// </remarks>
        uint NumTracks { get; }

        /// <summary>
        /// For sector-based images, such as 5.25" floppy disks and embedded DOS volumes, this
        /// holds the number of 256-byte sectors per track.  For all other images, this will
        /// hold zero.
        /// </summary>
        /// <remarks>
        /// <para>Value will be 13, 16, or (for embedded DOS volumes) 32.</para>
        /// </remarks>
        uint NumSectorsPerTrack { get; }

        /// <summary>
        /// True if the disk can be addressed as 256-byte track/sector.
        /// </summary>
        /// <remarks>
        /// True for 5.25" disk images and embedded DOS volumes, false for everything else.  It
        /// might be more accurate to call this "HasFixedSizeTracks".  If this is set, NumTracks
        /// and NumSectorsPerTrack will be nonzero.
        /// </remarks>
        bool HasSectors { get; }

        /// <summary>
        /// True if the data can be accessed as 512-byte blocks.
        /// </summary>
        /// <remarks>
        /// False for 13-sector 5.25" disk images; otherwise true.
        /// </remarks>
        bool HasBlocks { get; }

        /// <summary>
        /// Order in which sectors of data are stored in the file.
        /// </summary>
        /// <remarks>
        /// <para>Only meaningful for 16-sector 5.25" floppy disk block/sector images.  May be
        /// changed after object creation (e.g. when probing filesystems).</para>
        /// <para>Nibble images are always in "physical" order, and 13-sector ".d13" images
        /// are always DOS order.</para>
        /// </remarks>
        SectorOrder FileOrder { get; set; }

        /// <summary>
        /// Nibble data encoder/decoder used to convert raw nibbles to block/sector data.
        /// </summary>
        /// <remarks>
        /// <para>Only used for nibble formats.  Will be null otherwise.</para>
        /// </remarks>
        SectorCodec? NibbleCodec { get; }


        /// <summary>
        /// Reads a 256-byte sector from the underlying storage, as if requested by DOS.
        /// </summary>
        /// <remarks>
        /// It is possible for the read to fail if the underlying media has real or emulated
        /// errors.
        /// </remarks>
        /// <param name="trk">Track number.</param>
        /// <param name="sct">Sector number.</param>
        /// <param name="data">Data buffer.</param>
        /// <param name="offset">Initial offset within data buffer.</param>
        /// <exception cref="ArgumentOutOfRangeException">Track or sector number out
        ///   of range.</exception>
        /// <exception cref="BadBlockException">The storage is damaged, rendering the data
        ///   unreadable.</exception>
        /// <exception cref="InvalidOperationException">Disk does not have sectors.</exception>
        void ReadSector(uint trk, uint sct, byte[] data, int offset);

        /// <summary>
        /// Reads a 512-byte block from the underlying storage, as if requested by ProDOS/Pascal.
        /// </summary>
        /// <param name="block">Block number.</param>
        /// <param name="data">Data buffer.</param>
        /// <param name="offset">Initial offset within data buffer.</param>
        /// <exception cref="ArgumentOutOfRangeException">Block number out of range.</exception>
        /// <exception cref="BadBlockException">The storage is damaged, rendering the data
        ///   unreadable.</exception>
        /// <exception cref="InvalidOperationException">Disk does not have blocks.</exception>
        void ReadBlock(uint block, byte[] data, int offset);

        /// <summary>
        /// Reads a 512-byte block from the underlying storage, as if requested by CP/M.
        /// </summary>
        /// <remarks>
        /// On anything but 5.25" media, this is equivalent to ReadBlock().
        /// </remarks>
        /// <param name="block">Block number.</param>
        /// <param name="data">Data buffer.</param>
        /// <param name="offset">Initial offset within data buffer.</param>
        /// <exception cref="ArgumentOutOfRangeException">Block number out of range.</exception>
        /// <exception cref="BadBlockException">The storage is damaged, rendering the data
        ///   unreadable.</exception>
        /// <exception cref="InvalidOperationException">Disk does not have blocks.</exception>
        void ReadBlockCPM(uint block, byte[] data, int offset);

        /// <summary>
        /// Writes a 256-byte sector to the underlying storage.
        /// </summary>
        /// <remarks>
        /// It is possible for the write to fail if the underlying media has real or emulated
        /// errors.
        /// </remarks>
        /// <param name="trk">Track number.</param>
        /// <param name="sct">Sector number.</param>
        /// <param name="data">Data buffer.</param>
        /// <param name="offset">Initial offset within data buffer.</param>
        /// <exception cref="ArgumentOutOfRangeException">Track or sector number out
        ///   of range.</exception>
        /// <exception cref="BadBlockException">The storage is damaged.</exception>
        /// <exception cref="NotSupportedException">Storage was opened read-only.</exception>
        /// <exception cref="InvalidOperationException">Disk does not have sectors.</exception>
        void WriteSector(uint trk, uint sct, byte[] data, int offset);

        /// <summary>
        /// Writes a 512-byte block to the underlying storage, as if requested by ProDOS/Pascal.
        /// </summary>
        /// <param name="block">Block number.</param>
        /// <param name="data">Data buffer.</param>
        /// <param name="offset">Initial offset within data buffer.</param>
        /// <exception cref="ArgumentOutOfRangeException">Block number out of range.</exception>
        /// <exception cref="BadBlockException">The storage is damaged.</exception>
        /// <exception cref="NotSupportedException">Storage was opened read-only.</exception>
        /// <exception cref="InvalidOperationException">Disk does not have blocks.</exception>
        void WriteBlock(uint block, byte[] data, int offset);

        /// <summary>
        /// Writes a 512-byte block to the underlying storage, as if requested by CP/M.
        /// </summary>
        /// <remarks>
        /// On anything but 5.25" media, this is equivalent to WriteBlock().
        /// </remarks>
        /// <param name="block">Block number.</param>
        /// <param name="data">Data buffer.</param>
        /// <param name="offset">Initial offset within data buffer.</param>
        /// <exception cref="ArgumentOutOfRangeException">Block number out of range.</exception>
        /// <exception cref="BadBlockException">The storage is damaged.</exception>
        /// <exception cref="NotSupportedException">Storage was opened read-only.</exception>
        /// <exception cref="InvalidOperationException">Disk does not have blocks.</exception>
        void WriteBlockCPM(uint block, byte[] data, int offset);

        /// <summary>
        /// Tests the validity of a sector.
        /// </summary>
        /// <remarks>
        /// This is only useful for nibble images.  Block/sector images will always return true.
        /// </remarks>
        /// <param name="trk">Track number.</param>
        /// <param name="sct">Sector number.</param>
        /// <param name="writable">Result: true if sector is writable.</param>
        /// <returns>True if sector is readable and writable.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Track or sector number out
        ///   of range.</exception>
        /// <exception cref="InvalidOperationException">Disk does not have sectors.</exception>
        bool TestSector(uint trk, uint sct, out bool writable);

        /// <summary>
        /// Tests the validity of a block.
        /// </summary>
        /// <remarks>
        /// This is only useful for nibble images.  Block/sector images will always return true.
        /// </remarks>
        /// <param name="block">Block number.</param>
        /// <param name="writable">Result: true if sector is writable.</param>
        /// <returns>True if block is readable and writable.</returns>
        bool TestBlock(uint block, out bool writable);

        /// <summary>
        /// Initializes the storage to zeroes.  Call this before calling a filesystem format
        /// routine when re-formatting an existing disk.
        /// </summary>
        void Initialize();
    }
}
