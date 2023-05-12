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
using System.Diagnostics.CodeAnalysis;

using CommonUtil;

namespace DiskArc.Disk {
    /// <summary>
    /// Interfaces for accessing raw nibble data from a nibble-oriented floppy disk image.
    /// </summary>
    public interface INibbleDataAccess {
        /// <summary>
        /// Characteristics of this class of nibble image.  All instances of a given class
        /// return the same values.
        /// </summary>
        NibCharacteristics Characteristics { get; }

        /// <summary>
        /// True if modifying the disk data is not allowed.
        /// </summary>
        bool IsReadOnly { get; }

        /// <summary>
        /// What kind of floppy disk this is.
        /// </summary>
        Defs.MediaKind DiskKind { get; }

        /// <summary>
        /// Returns a reference to the raw data for the specified track.  Modifications to the
        /// data will be saved to the file when changes are flushed.
        /// </summary>
        /// <remarks>
        /// <para>If the underlying data is bit-oriented, the nibble data may or may not be
        /// byte-aligned.  The data should be treated as a bit stream that is read until the
        /// high bit of an 8-bit latch is set.</para>
        /// <para>The meaning of the track fraction argument depends on the disk type.  For
        /// 5.25" floppies it specifies the quarter track: 0 for the whole track, 1 for +0.25,
        /// 2 for +0.5, and 3 for +0.75.  For 3.5" floppies it indicates the disk side: 0 for
        /// side 0, 1 for side 1.</para>
        /// <para>Modifications to the bit buffer may not be immediately visible in calls to
        /// IChunkAccess or IFileSystem objects layered on top, because those objects can cache
        /// data.  For best results, nibble editing should be performed before performing disk
        /// image analysis.</para>
        /// <para>Calling this on the same track number multiple times will generate a
        /// different CircularBitBuffer object each time, each of which has its own current
        /// position.  The underlying data buffer is shared.</para>
        /// <para>The CircularBitBuffer will be marked read-only if the disk image is
        /// read-only.</para>
        /// </remarks>
        /// <param name="trackNum">Track number, 0-39 (5.25") or 0-79 (3.5").</param>
        /// <param name="trackFraction">Track fraction, 0-3 (5.25" quarter track) or
        ///   0-1 (3.5" disk side).</param>
        /// <param name="cbb">Result: circular bit buffer with track data.  Buffer will be
        ///   marked read-only if nibble data source is read-only.  Value will be null if the
        ///   track was not found.</param>
        /// <returns>True if the track was found, false if not.  It is possible for a track number
        ///   to be in the valid range but not stored in the file.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Track or track-fraction exceeds the
        ///   maximum allowed value for this type of disk.</exception>
        bool GetTrackBits(uint trackNum, uint trackFraction,
            [NotNullWhen(true)] out CircularBitBuffer? cbb);

        /// <summary>
        /// Returns the number of tracks supported by the media.  This indicates the maximum
        /// track number, not the total number of tracks (which could include quarter tracks or
        /// multiple disk sides).
        /// </summary>
        /// <param name="kind">Media kind.</param>
        /// <returns>Maximum number of tracks possible.</returns>
        //static int MaxTracks(Defs.MediaKind kind) {
        //    if (kind == Defs.MediaKind.GCR_525) {
        //        return SectorCodec.MAX_TRACK_525;
        //    } else {
        //        return SectorCodec.MAX_TRACK_35;
        //    }
        //}
    }
}
