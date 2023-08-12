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
using System.Collections;
using System.Diagnostics;

using CommonUtil;
using DiskArc.Multi;
using static DiskArc.Defs;

namespace DiskArc.FS {
    /// <summary>
    /// Code for finding a filesystem peacefully co-existing with DOS on a 5.25" disk image.
    /// </summary>
    /// <remarks>
    /// <para>Re-analyzing a partition can be problematic, because it will find DOS first and
    /// lose track of the other filesystem.  The Partition class will prevent this.</para>
    /// </remarks>
    public class DOS_Hybrid : IMultiPart {
        //
        // IMultiPart interfaces.
        //

        public bool IsDubious { get; private set; }

        public Notes Notes { get; } = new Notes();

        public GatedChunkAccess RawAccess { get; }

        public int Count { get { return mPartitions.Count; } }
        public Partition this[int key] { get { return mPartitions[key]; } }

        public IEnumerator<Partition> GetEnumerator() {
            return mPartitions.GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() {
            return mPartitions.GetEnumerator();
        }

        //
        // Innards.
        //

        private DOS mFileSystem;
        private AppHook mAppHook;

        private List<Partition> mPartitions = new List<Partition>();

        /// <summary>
        /// Private constructor.
        /// </summary>
        /// <param name="fs">DOS filesystem object.</param>
        /// <param name="appHook">Application hook reference.</param>
        private DOS_Hybrid(DOS fs, AppHook appHook) {
            mFileSystem = fs;
            mAppHook = appHook;
            RawAccess = new GatedChunkAccess(fs.ChunkAccess);
        }

        /// <summary>
        /// Looks for filesystems coexisting with a DOS filesystem.
        /// </summary>
        /// <param name="chunkAccess">Chunk access object for DOS filesystem.</param>
        /// <param name="vu">Volume usage for DOS filesystem.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <returns>List of embedded volumes as an IMultiPart instance, or null if no embedded
        ///   volumes were found.</returns>
        internal static IMultiPart? FindEmbeddedVolumes(DOS fs, AppHook appHook) {
            IChunkAccess chunkAccess = fs.ChunkAccess;
            DOS_VTOC? vtoc = fs.VTOC;
            if (vtoc == null) {
                return null;
            }

            // We're only expecting to find these on 16-sector 5.25" floppies, because the other
            // OS will be block-oriented.  Accept 35 or 40 tracks.
            if (chunkAccess.NumSectorsPerTrack != 16 ||
                    (chunkAccess.NumTracks != 35 && chunkAccess.NumTracks != 40)) {
                return null;
            }
            DOS_Hybrid embeds = new DOS_Hybrid(fs, appHook);
            if (!embeds.FindVolumes()) {
                return null;
            } else {
                return embeds;
            }
        }

        public void Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        protected virtual void Dispose(bool disposing) {
            RawAccess.AccessLevel = GatedChunkAccess.AccessLvl.Closed;
            if (disposing) {
                foreach (Partition part in mPartitions) {
                    part.Dispose();
                }
            }
        }

        private bool FindVolumes() {
            IChunkAccess chunkAccess = mFileSystem.ChunkAccess;
            DOS_VTOC vtoc = mFileSystem.VTOC!;

            // Quick check to confirm that VolumeUsage is populated.  T17 S0 should be marked as
            // in-use by system.  (We currently always do a full DOS scan, so this should always
            // pass unless the DOS implementation changes.)
            VolumeUsage vu = vtoc.VolUsage;
            if (vu.GetUsage(mFileSystem.TSToChunk(DOS.VTOC_TRACK, DOS.VTOC_SECTOR))
                    != IFileEntry.NO_ENTRY) {
                Debug.Assert(false);        // shouldn't be here without full VU data
                return false;
            }

            // Identify tracks used by DOS.  This doesn't currently take into account partial
            // tracks, e.g. the split catalog track formed by Doubleboot.  To handle that we'd
            // need to do this at block/sector granularity, taking into account the sector skew
            // employed by the alternate filesystem.
            bool[] dosUse = new bool[chunkAccess.NumTracks];
            uint firstDosUse = uint.MaxValue;
            for (uint trk = 0; trk < chunkAccess.NumTracks; trk++) {
                for (uint sct = 0; sct < chunkAccess.NumSectorsPerTrack; sct++) {
                    uint chunkNum = mFileSystem.TSToChunk(trk, sct);
                    // The sector is available to or used by DOS if it's marked free, or marked
                    // as in use but there is no associated file.
                    //
                    // This is a little tricky because the DOS image sits on tracks 0-2, and the
                    // volume usage algorithm changes those to be owned by system if nothing else
                    // claims them.  For those tracks, don't mark as used by DOS unless there is
                    // an actual file reference.
                    if (!vu.GetInUse(chunkNum) ||
                            (vu.GetUsage(chunkNum) != null && trk > 2)) {
                        if (!dosUse[trk]) {
                            dosUse[trk] = true;
                            //dosTrkCount++;
                        }
                        if (firstDosUse == uint.MaxValue) {
                            firstDosUse = trk;
                        }
                    }
                }
            }

            // Test for the presence of compatible filesystems.  ProDOS, UCSD Pascal, and CP/M
            // are viable candidates.  HFS is unlikely, RDOS is possible but silly.
            if (ProDOS.TestImage(chunkAccess, mAppHook) ==
                    FileAnalyzer.DiskLayoutEntry.TestResult.Yes) {
                mAppHook.LogI("Found DOS+ProDOS hybrid");
                ProDOS proFs = new ProDOS(chunkAccess, mAppHook);
                proFs.PrepareFileAccess(true);
                ProDOS_VolBitmap? vbm = proFs.VolBitmap;
                if (vbm == null) {
                    return false;
                }
                VolumeUsage proVu = vbm.VolUsage;
                bool hasConflict = false;
                uint blocksPerTrack = chunkAccess.NumSectorsPerTrack / 2;
                for (uint trk = 0; trk < chunkAccess.NumTracks; trk++) {
                    for (uint blk = 0; blk < blocksPerTrack; blk++) {
                        uint chunkNum = trk * blocksPerTrack + blk;
                        if (chunkNum < proFs.TotalBlocks &&
                                (!proVu.GetInUse(chunkNum) || proVu.GetUsage(chunkNum) != null) &&
                                dosUse[trk]) {
                            // Available to or used by ProDOS.  This check is insufficient,
                            // because DOS and ProDOS could be splitting the track.  A full test
                            // would need to test the individual sectors, factoring the sector
                            // skew in.
                            Notes.AddW("Possible DOS/ProDOS overlap on track " + trk);
                            hasConflict = true;
                            break;
                        }
                    }
                }
                if (hasConflict) {
                    IsDubious = true;
                }
                proFs.PrepareRawAccess();

                // Prepare the "partition".
                ChunkSubset subChunk = ChunkSubset.CreateBlocksOnTracks(chunkAccess,
                    chunkAccess.NumSectorsPerTrack, chunkAccess.NumTracks,
                    SectorOrder.ProDOS_Block);
                Partition part = new Partition(subChunk, proFs, mAppHook);
                mPartitions.Add(part);
                return true;
            } else if (Pascal.TestImage(chunkAccess, mAppHook) ==
                    FileAnalyzer.DiskLayoutEntry.TestResult.Yes) {
                mAppHook.LogI("Found DOS+Pascal hybrid; first DOS use is track " + firstDosUse);
                Pascal pascalFs = new Pascal(chunkAccess, mAppHook);

                // Look for a .BAD file that spans the volume from the first track used by DOS
                // to the end of the disk.
                pascalFs.PrepareFileAccess(true);
                IFileEntry volDir = pascalFs.GetVolDirEntry();
                bool foundBad = false;
                foreach (IFileEntry entry in volDir) {
                    if (entry.FileType == FileAttribs.FILE_TYPE_BAD) {
                        Pascal_FileEntry pentry = (Pascal_FileEntry)entry;
                        if (pentry.StartBlock == firstDosUse * 16 / 2 &&
                                pentry.NextBlock == pascalFs.TotalBlocks) {
                            Debug.WriteLine("Found the .BAD file");
                            foundBad = true;
                            break;
                        }
                    }
                }
                if (!foundBad) {
                    Notes.AddW("Did not find .BAD file that spanned DOS area");
                    IsDubious = true;
                } else {
                    // Make sure DOS isn't using anything in the Pascal area.
                    for (int i = 0; i < DOS.VTOC_TRACK; i++) {
                        if (dosUse[i]) {
                            Notes.AddW("Track " + i +
                                " used by DOS (should be reserved for Pascal)");
                            IsDubious = true;
                            break;
                        }
                    }
                }
                pascalFs.PrepareRawAccess();

                // Prepare the "partition".
                ChunkSubset subChunk = ChunkSubset.CreateBlocksOnTracks(chunkAccess,
                    chunkAccess.NumSectorsPerTrack, chunkAccess.NumTracks,
                    SectorOrder.ProDOS_Block);
                Partition part = new Partition(subChunk, pascalFs, mAppHook);
                mPartitions.Add(part);
                return true;
            } else {
                // TODO - test for CP/M
                return false;
            }
        }
    }
}
