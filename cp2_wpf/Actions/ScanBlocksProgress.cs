/*
 * Copyright 2023 faddenSoft
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
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;

using AppCommon;
using CommonUtil;
using cp2_wpf.WPFCommon;
using DiskArc;
using static DiskArc.Defs;


namespace cp2_wpf.Actions {
    /// <summary>
    /// Scans for bad blocks, inside a WorkProgress dialog.
    /// </summary>
    internal class ScanBlocksProgress : WorkProgress.IWorker {
        private IDiskImage mDiskImage;
        private AppHook mAppHook;

        public class Failure {
            public uint BlockOrTrack { get; private set; }
            public uint Sector { get; private set; }
            public bool IsUnreadable { get; set; }
            public bool IsUnwritable { get; set; }

            public bool IsBlock { get { return Sector == uint.MaxValue; } }

            public Failure(uint track, uint sector, bool isUnreadable, bool isUnwritable) {
                BlockOrTrack = track;
                Sector = sector;
                IsUnreadable = isUnreadable;
                IsUnwritable = isUnwritable;
            }
            public Failure(uint block, bool isUnreadable, bool isUnwritable) {
                BlockOrTrack = block;
                Sector = uint.MaxValue;
                IsUnreadable = isUnreadable;
                IsUnwritable = isUnwritable;
            }
        }

        public List<Failure>? FailureResults { get; private set; } = null;


        public ScanBlocksProgress(IDiskImage diskImage, AppHook appHook) {
            mDiskImage = diskImage;
            mAppHook = appHook;
        }

        public object DoWork(BackgroundWorker worker) {
            List<Failure> failures = new List<Failure>();

            ScanDisk(mDiskImage, worker, failures);
            return failures;
        }

        /// <summary>
        /// Scans a disk image for bad blocks/sectors.
        /// </summary>
        private static bool ScanDisk(IDiskImage disk, BackgroundWorker bkWorker,
                List<Failure> failures) {
            //System.Threading.Thread.Sleep(1000);
            if (disk.ChunkAccess == null) {
                Debug.Assert(false, "Disk format not recognized, skipping bad block scan");
                return false;
            }

            // ExtArchive has already analyzed the disk.  Analyzing it again is awkward
            // without the sector order hint, so just pick out the best chunk access.
            IChunkAccess chunks;
            if (disk.ChunkAccess.AccessLevel != GatedChunkAccess.AccessLvl.Closed) {
                chunks = disk.ChunkAccess;
            } else {
                if (disk.Contents == null) {
                    // Shouldn't happen.
                    Debug.Assert(false, "Error: can't get filesystem object");
                    return false;
                }
                chunks = ((IFileSystem)disk.Contents).RawAccess;
            }

            int checkCount, unreadCount, unwriteCount;
            checkCount = unreadCount = unwriteCount = 0;
            if (chunks.HasSectors) {
                for (uint trk = 0; trk < chunks.NumTracks; trk++) {
                    for (uint sct = 0; sct < chunks.NumSectorsPerTrack; sct++) {
                        if (bkWorker.CancellationPending) {
                            return false;
                        }
                        checkCount++;
                        if (!chunks.TestSector(trk, sct, out bool isWritable)) {
                            failures.Add(new Failure(trk, sct, true, !isWritable));
                            unreadCount++;
                            if (!isWritable) {
                                unwriteCount++;
                            }
                        }
                    }
                }
            } else if (chunks.HasBlocks) {
                uint numBlocks = (uint)(chunks.FormattedLength / BLOCK_SIZE);
                for (uint blk = 0; blk < numBlocks; blk++) {
                    if (bkWorker.CancellationPending) {
                        return false;
                    }
                    checkCount++;
                    if (!chunks.TestBlock(blk, out bool isWritable)) {
                        failures.Add(new Failure(blk, true, !isWritable));
                        unreadCount++;
                        if (!isWritable) {
                            unwriteCount++;
                        }
                    }
                }
            } else {
                // Shouldn't happen.  KBlocks test as blocks.
                Debug.Assert(false, "Error: disk image has neither blocks nor sectors");
                return false;
            }
            Debug.WriteLine("Totals: check=" + checkCount + " unreadable=" + unreadCount +
                " unwritable=" + unwriteCount);
            return true;
        }

        public bool RunWorkerCompleted(object? results) {
            FailureResults = (List<Failure>?)results;
            return (FailureResults != null);
        }
    }
}
