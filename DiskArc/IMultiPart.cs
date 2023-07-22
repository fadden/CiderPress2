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

using DiskArc.Multi;

namespace DiskArc {
    /// <summary>
    /// Interface definition for multi-partition disk formats.
    /// </summary>
    /// <remarks>
    /// <para>Partition layouts are effectively immutable.  They do not move or change size.
    /// Unlike filesystems, we don't need to create an object and then scan for what's present.
    /// Either we fully understand the partition layout, or we don't recognize it at all.</para>
    /// <para>It's theoretically possible for a multi-partition disk to have zero partitions,
    /// though in practice that's unlikely (and a bit useless).</para>
    ///
    /// <para>Instances of IMultiPart require the same static delegates that IFileSystem
    /// instances do: TestImage(IChunkAccess) and IsSizeAllowed(long).  The latter should
    /// always return false.</para>
    /// </remarks>
    public interface IMultiPart : IDiskContents, IDisposable, IEnumerable<Partition> {
        /// <summary>
        /// True if the partition map is damaged in a way that makes writing to one or more
        /// of the partitions hazardous, e.g. some partitions overlap. Such images should be
        /// configured read-only.
        /// </summary>
        bool IsDubious { get; }

        /// <summary>
        /// Errors, warnings, and points of interest noted during the partition scan.
        /// </summary>
        CommonUtil.Notes Notes { get; }

        /// <summary>
        /// Chunk access object for the entire multi-partition area.
        /// </summary>
        /// <remarks>
        /// This is currently always read-only.
        /// </remarks>
        GatedChunkAccess RawAccess { get; }

        /// <summary>
        /// Number of partitions.
        /// </summary>
        int Count { get; }

        /// <summary>
        /// Obtains a reference to the Nth partition.
        /// </summary>
        /// <param name="key">Partition index.</param>
        /// <returns>Partition reference.</returns>
        Partition this[int key] { get; }
    }
}
