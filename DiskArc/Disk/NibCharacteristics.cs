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

namespace DiskArc.Disk {
    /// <summary>
    /// Characteristics and capabilities of a nibble image format.  These apply to all instances of
    /// the class.
    /// </summary>
    /// <remarks>
    /// <para>Immutable.</para>
    /// </remarks>
    public class NibCharacteristics {
        /// <summary>
        /// Human-readable name.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// True if the nibble data is stored byte-aligned.
        /// </summary>
        /// <remarks>
        /// <para>This is true for formats like .nib, which don't attempt to represent
        /// self-sync bytes.</para>
        /// </remarks>
        public bool IsByteAligned { get; }

        /// <summary>
        /// True if the nibbles were captured as a fixed-length stream.
        /// </summary>
        /// <remarks>
        /// <para>This is true for formats like .nib, which don't try to determine the actual
        /// length of a track, and so will likely have a repeated section.</para>
        /// </remarks>
        public bool IsFixedLength { get; }

        /// <summary>
        /// True if the format supports half-tracks and quarter-tracks for 5.25" disks.
        /// </summary>
        public bool HasPartial525Tracks { get; }

        public NibCharacteristics(string name, bool isByteAligned, bool isFixedLength,
                bool hasPartial525Tracks) {
            Name = name;
            IsByteAligned = isByteAligned;
            IsFixedLength = isFixedLength;
            HasPartial525Tracks = hasPartial525Tracks;
        }
    }
}
