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
using DiskArc.FS;
using static DiskArc.Defs;
using static DiskArc.FileAnalyzer.DiskLayoutEntry;

namespace DiskArc.Multi {
    /// <summary>
    /// OzDOS multi-partition disk layout.  Two 400KB DOS volumes on an 800KB disk, spanning
    /// the full disk but splitting each block.
    /// </summary>
    public class OzDOS : DOS800 {

        // Delegate: test image to see if it's ours.
        public static TestResult TestImage(IChunkAccess chunkAccess, AppHook appHook) {
            if (DoTest(chunkAccess, appHook, out ChunkSubset? chunk1, out ChunkSubset? chunk2)) {
                return TestResult.Yes;
            } else {
                return TestResult.No;
            }
        }

        private static bool DoTest(IChunkAccess chunkAccess, AppHook appHook,
                out ChunkSubset? chunk1, out ChunkSubset? chunk2) {
            chunk1 = chunk2 = null;
            if (chunkAccess.FormattedLength != 800 * 1024) {
                return false;
            }
            if (!chunkAccess.HasBlocks) {
                return false;
            }

            chunk1 = ChunkSubset.CreateTracksOnSplitBlocks(chunkAccess, false);
            chunk2 = ChunkSubset.CreateTracksOnSplitBlocks(chunkAccess, true);
            if (DOS.TestImage(chunk1, appHook) != TestResult.Yes ||
                    DOS.TestImage(chunk2, appHook) != TestResult.Yes) {
                return false;
            }

            return true;
        }

        // Delegate: returns true if the size (in bytes) is valid for this filesystem.
        public static bool IsSizeAllowed(long size) {
            return false;
        }

        public OzDOS(IChunkAccess chunkAccess, AppHook appHook)
                : base(chunkAccess, appHook) {
            if (!DoTest(chunkAccess, appHook,out ChunkSubset? chunk1, out ChunkSubset? chunk2)) {
                throw new InvalidDataException("Incorrect disk image contents");
            }
            Debug.Assert(chunk1 != null && chunk2 != null);

            Partition part1 = new Partition(chunk1, new DOS(chunk1, appHook), appHook);
            Partition part2 = new Partition(chunk2, new DOS(chunk2, appHook), appHook);

            mPartitions.Add(part1);
            mPartitions.Add(part2);
        }
    }
}
