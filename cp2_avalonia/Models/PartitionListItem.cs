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
    public class PartitionListItem
    {
        public int Index { get; }
        public long StartBlock { get; }
        public long BlockCount { get; }
        public string PartName { get; }
        public string PartType { get; }
        public Partition PartRef { get; }

        public PartitionListItem(int index, Partition part)
        {
            PartRef = part;

            // Not every partition type carries a name/type; pull them out for the ones
            // that do (matches the WPF version).
            string name = string.Empty;
            string type = string.Empty;
            if (part is APM_Partition apm) {
                name = apm.PartitionName;
                type = apm.PartitionType;
            } else if (part is FocusDrive_Partition focus) {
                name = focus.PartitionName;
            } else if (part is PPM_Partition ppm) {
                name = ppm.PartitionName;
            }

            Index = index;
            StartBlock = part.StartOffset / Defs.BLOCK_SIZE;
            BlockCount = part.Length / Defs.BLOCK_SIZE;
            PartName = name;
            PartType = type;
        }

        public override string ToString() {
            return "[Part: start=" + StartBlock + " count=" + BlockCount + " name=" +
                PartName + "]";
        }
    }
}