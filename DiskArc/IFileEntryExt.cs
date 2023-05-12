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

namespace DiskArc {
    /// <summary>
    /// Additional internal-only interfaces.
    /// </summary>
    internal interface IFileEntryExt : IFileEntry {
        /// <summary>
        /// This is an internal housekeeping routine, called during the volume scan when
        /// another file appears to be using the same blocks.
        /// </summary>
        /// <param name="chunk">Chunk with multiple users.</param>
        /// <param name="entry">Conflicting entry, or NO_ENTRY for system blocks.</param>
        void AddConflict(uint chunk, IFileEntry entry);
    }
}
