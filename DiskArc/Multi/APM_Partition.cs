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
using static DiskArc.Defs;

namespace DiskArc.Multi {
    /// <summary>
    /// One partition in an Apple Partition Map.
    /// </summary>
    public class APM_Partition : Partition {
        /// <summary>
        /// Partition name string.
        /// </summary>
        public string PartitionName { get; private set; } = string.Empty;

        /// <summary>
        /// Partition type string.
        /// </summary>
        public string PartitionType { get; private set; } = string.Empty;

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="chunkAccess">Chunk access object for partition contents.</param>
        /// <param name="startOffset">Partition start offset, in bytes.</param>
        /// <param name="length">Partition length, in bytes.</param>
        /// <param name="name">Partition name, from partition map entry.</param>
        /// <param name="typeStr">Partition type, from partition map entry.</param>
        /// <param name="appHook">Application hook reference.</param>
        public APM_Partition(IChunkAccess chunkAccess, long startOffset, long length,
                string name, string typeStr, AppHook appHook)
                : base(chunkAccess, startOffset, length, appHook) {
            PartitionName = name;
            PartitionType = typeStr;
        }

        public override string ToString() {
            return "[APM: name='" + PartitionName + "' type='" + PartitionType +
                "' start=" + (StartOffset / BLOCK_SIZE) + " length=" + (Length / BLOCK_SIZE) +
                " (" + (Length / (1024 * 1024.0)).ToString("F2") + " MiB)]";
        }
    }
}
