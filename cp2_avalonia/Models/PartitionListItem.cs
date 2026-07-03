/*
 * Copyright 2023 faddenSoft
 * Copyright 2026 Lydian Scale Software
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
using DiskArc;
using DiskArc.Multi;

namespace cp2_avalonia.Models {
    /// <summary>
    /// Item displayed in the partition layout list in the center info panel.
    /// </summary>
    public class PartitionListItem(int index, Partition part)
    {
        public int Index { get; } = index;
        public long StartBlock { get; } = part.StartOffset / Defs.BLOCK_SIZE;
        public long BlockCount { get; } = part.Length / Defs.BLOCK_SIZE;
        public string PartName { get; } = string.Empty;
        public string PartType { get; } = string.Empty;
        public Partition PartRef { get; } = part;

        public override string ToString() {
            return "[Part: start=" + StartBlock + " count=" + BlockCount + "]";
        }
    }
}