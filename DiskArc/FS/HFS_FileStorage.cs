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
using static DiskArc.Defs;
using static DiskArc.FS.HFS_Struct;

namespace DiskArc.FS {
    /// <summary>
    /// Tracks storage for one fork of an open file.  Also used for tree files.
    /// </summary>
    /// <remarks>
    /// <para>The physical length and first extent record for a file or tree are stored in the
    /// file catalog entry, or (for trees) in the MDB.  For a regular file, the changes will be
    /// copied to the file entry when the file is closed.  For a tree file, we need to actively
    /// push them to the MDB, so that other open files can see them immediately.</para>
    /// </remarks>
    internal class HFS_FileStorage {
        public uint PhyLength { get; private set; }

        private HFS mFileSystem;
        private IFileEntry mEntry;
        private uint mCNID;

        private uint mClumpSize;
        private bool IsTree { get; }
        private bool IsRsrcFork { get; }
        private bool IsExtentsOverflow { get; }

        private List<ExtDataRec> mExtents = new List<ExtDataRec>();

        public ExtDataRec FirstExtRec {
            get { return new ExtDataRec(mExtents[0]); }
        }


        /// <summary>
        /// Constructor.
        /// </summary>
        /// <remarks>
        /// <para>The first file this should be called on is the extents overflow tree, because
        /// we need that to fully process the rest.</para>
        /// </remarks>
        /// <param name="fs">Filesystem reference.</param>
        /// <param name="entry">File entry reference, for volume usage tracking.  Pass NO_ENTRY
        ///   for the tree files.</param>
        /// <param name="cnid">File CNID.</param>
        /// <param name="isRsrc">True if this is the resource fork of the file.</param>
        /// <param name="phyLength">Physical length, in bytes.</param>
        /// <param name="firstExtRec">Reference to first extent record.</param>
        /// <exception cref="IOException">Disk access failure.</exception>
        public HFS_FileStorage(HFS fs, IFileEntry entry, uint cnid, bool isRsrc, uint phyLength,
                ExtDataRec firstExtRec) {
            Debug.Assert(fs.VolMDB != null);
            Debug.Assert(cnid == HFS.EXTENTS_CNID || fs.VolMDB.ExtentsFile != null);
            Debug.Assert(entry == IFileEntry.NO_ENTRY || cnid == ((HFS_FileEntry)entry).EntryCNID);

            mFileSystem = fs;
            mEntry = entry;
            mCNID = cnid;
            IsRsrcFork = isRsrc;
            PhyLength = phyLength;

            HFS_MDB mdb = fs.VolMDB;
            switch (cnid) {
                case HFS.EXTENTS_CNID:
                    mClumpSize = mdb.ExtentsClumpSize;
                    IsTree = IsExtentsOverflow = true;
                    break;
                case HFS.CATALOG_CNID:
                    mClumpSize = mdb.CatalogClumpSize;
                    IsTree = true;
                    break;
                default:
                    Debug.Assert(cnid >= HFS.FIRST_CNID);
                    mClumpSize = mdb.ClumpSize;
                    break;
            }

            // Compute initial span.
            mExtents.Add(firstExtRec);

            // If this fork has extent records in the overflow tree, gather them up.  Don't
            // try to do this for the extents overflow tree file itself.
            if (cnid != HFS.EXTENTS_CNID) {
                HFS_BTree extTree = fs.VolMDB.ExtentsFile!;
                uint allocBlockSize = fs.VolMDB.BlockSize;

                int span = mExtents[0].AllocBlockSpan;

                // Gather up the rest, copying the data from the node records into our list.
                while (span * allocBlockSize < PhyLength) {
                    Debug.Assert(span == (ushort)span);
                    HFS_Record.ExtKey extKey =
                        new HFS_Record.ExtKey(IsRsrcFork, mCNID, (ushort)span);
                    if (!extTree.FindRecord(extKey, out HFS_Node? node, out int recordIndex)) {
                        throw new IOException("Unable to find extent overflow record: " + extKey);
                    }
                    Debug.Assert(node != null);
                    ExtDataRec edr = node.GetRecord(recordIndex).ParseExtDataRec();
                    mExtents.Add(new ExtDataRec(edr));

                    span += edr.AllocBlockSpan;
                }
            }
        }

        public uint GetAllocBlockNum(uint blockIndex) {
            Debug.Assert(mFileSystem.VolMDB != null);

            uint startIndex = 0;
            foreach (ExtDataRec extRec in mExtents) {
                for (int i = 0; i < ExtDataRec.NUM_RECS; i++) {
                    uint numAblks = extRec.Descs[i].xdrNumAblks;
                    if (numAblks > blockIndex - startIndex) {
                        // Found it.  Get the number of the Nth allocation block.
                        uint ablk = extRec.Descs[i].xdrStABN + (blockIndex - startIndex);
                        return ablk;
                    }
                    startIndex += numAblks;
                }
            }
            throw new IOException("Alloc block index " + blockIndex + " not found");
        }

        /// <summary>
        /// Extends the fork, adding a clump of blocks.  The exact number of allocation blocks
        /// added is not guaranteed (but will be nonzero).
        /// </summary>
        /// <exception cref="DiskFullException">No space available to expand.</exception>
        public void Extend() {
            HFS_MDB mdb = mFileSystem.VolMDB!;
            HFS_VolBitmap vbm = mFileSystem.VolBitmap!;

            // Try to allocate a full clump of blocks.  If we can't, we'll accept less.  The
            // blocks must be marked in-use immediately, because we may need to allocate more
            // storage for the extents overflow tree.
            ExtDescriptor desc = vbm.AllocBlocks(mClumpSize / mdb.BlockSize,
                mdb.NextAllocation, mEntry);
            try {
                Debug.Assert(desc.xdrNumAblks > 0);
                AddExtent(desc);

                // We can set this to the start or the end.  The motivation for setting it to the
                // start is that, if we trim the extent when the file closes, we won't isolate
                // a handful of blocks.  The allocator does a backward peek, so it doesn't really
                // matter.  Ultimately this is just an optimization to reduce the time spent
                // scanning through the volume bitmap.
                mdb.NextAllocation = desc.xdrStABN;
            } catch {
                // Failed; undo the volume bitmap change.
                vbm.ReleaseBlocks(desc);
                throw;
            }
        }

        /// <summary>
        /// Extends the file to (or beyond) the specified length.
        /// </summary>
        /// <remarks>
        /// On failure, the file may be partially extended.
        /// </remarks>
        /// <param name="newLength">Desired file size, in bytes.</param>
        /// <exception cref="DiskFullException">Ran out of disk space.</exception>
        public void ExtendTo(uint newLength) {
            while (PhyLength < newLength) {
                Extend();
            }
        }

        /// <summary>
        /// Adds a new extent to a file.
        /// </summary>
        /// <param name="newDesc">Extent descriptor.</param>
        /// <exception cref="IOException">Disk access failure, or corrupted structure
        ///   found.</exception>
        /// <exception cref="DiskFullException">Ran out of space while adding the
        ///   extent.</exception>
        private void AddExtent(ExtDescriptor newDesc) {
            // Find the extent record we need to update.  We can either search for the last
            // entry with a nonzero block count, or walk through based on the physical length.
            // The former doesn't work if there's junk in the extent record, but that's not
            // expected.
            HFS_MDB mdb = mFileSystem.VolMDB!;
            int fileBlockCount = (int)(PhyLength / mdb.BlockSize);
            int recNum, descNum;
            if (fileBlockCount == 0) {
                // Empty file.  We will need to create a new extent, in the first slot.
                ExtDescriptor firstDesc = mExtents[0].Descs[0];
                firstDesc.xdrStABN = newDesc.xdrStABN;
                firstDesc.xdrNumAblks = newDesc.xdrNumAblks;
                SaveExtChange(0, 0);
            } else {
                Debug.Assert(mExtents.Count >= 1);
                int blockCount = 0;
                descNum = 0;
                int recStart = 0;
                for (recNum = 0; recNum < mExtents.Count; recNum++) {
                    for (descNum = 0; descNum < ExtDataRec.NUM_RECS; descNum++) {
                        ushort count = mExtents[recNum].Descs[descNum].xdrNumAblks;
                        if (count == 0) {
                            throw new IOException("Found extent record with zero blocks");
                        }
                        blockCount += count;
                        if (blockCount == fileBlockCount) {
                            // Found the end, break out.
                            break;
                        } else if (blockCount > fileBlockCount) {
                            throw new IOException("Extents exceed file length");
                        }
                    }

                    if (blockCount == fileBlockCount) {
                        // Found the end, break out some more.
                        break;
                    }

                    // Number of allocation blocks to the start of the next record.  Used
                    // for forming the extents tree key.
                    recStart = blockCount;
                }

                if (blockCount != fileBlockCount) {
                    throw new IOException("File length exceeds extents");
                }

                ExtDescriptor lastDesc = mExtents[recNum].Descs[descNum];
                if (lastDesc.xdrStABN + lastDesc.xdrNumAblks == newDesc.xdrStABN) {
                    // The new descriptor just extends the existing descriptor.
                    lastDesc.xdrNumAblks += newDesc.xdrNumAblks;
                    SaveExtChange(recNum, recStart);
                } else {
                    // We need to add the descriptor.  If there's room in the record,
                    // add it there.
                    descNum++;
                    if (descNum < ExtDataRec.NUM_RECS) {
                        ExtDescriptor slotDesc = mExtents[recNum].Descs[descNum];
                        slotDesc.xdrStABN = newDesc.xdrStABN;
                        slotDesc.xdrNumAblks = newDesc.xdrNumAblks;
                        SaveExtChange(recNum, recStart);
                    } else {
                        // No room, create a new extent record.  This may require the extents
                        // overflow tree to grow.  We can't expand the extents tree itself
                        // beyond the initial extent record.
                        if (IsExtentsOverflow) {
                            throw new DiskFullException("Extents tree is full");
                        }
                        // Start by forming the record.
                        HFS_Record.ExtKey key = new HFS_Record.ExtKey(IsRsrcFork, mCNID,
                            (ushort)blockCount);
                        ExtDataRec extRec = new ExtDataRec();
                        extRec.Descs[0].xdrStABN = newDesc.xdrStABN;
                        extRec.Descs[0].xdrNumAblks = newDesc.xdrNumAblks;
                        HFS_Record newRec = HFS_Record.GenerateExtDataRec(key, extRec);

                        // Add it to the tree.  (Don't need to call SaveExtChange.)
                        HFS_BTree extTree = mdb.ExtentsFile!;
                        extTree.EnsureSpace(1);
                        extTree.Insert(key, newRec);

                        // Add it to our list.
                        mExtents.Add(extRec);
                    }
                }
            }

            // Update the physical length of the file.  For the tree files this is stored in
            // the MDB.
            uint newPhyLength = PhyLength + newDesc.xdrNumAblks * mdb.BlockSize;
            if (mCNID == HFS.EXTENTS_CNID) {
                Debug.Assert(PhyLength == mdb.ExtentsFileSize);
                mdb.ExtentsFileSize = newPhyLength;
            } else if (mCNID == HFS.CATALOG_CNID) {
                Debug.Assert(PhyLength == mdb.CatalogFileSize);
                mdb.CatalogFileSize = newPhyLength;
            }
            PhyLength = newPhyLength;
        }

        /// <summary>
        /// Trims excess storage off the end of a file fork.
        /// </summary>
        /// <remarks>
        /// <para>It is optional, but very helpful, to do this whenever the fork is closed.  This
        /// is especially true for disks with lots of small files.  The Linux implementation
        /// doesn't appear to do this.</para>
        /// <para>This is not used for tree files.</para>
        /// </remarks>
        /// <param name="logicalLength">Logical length of the fork.</param>
        /// <exception cref="IOException">Disk access failure or corrupt structure
        ///   found.</exception>
        public void Trim(int logicalLength) {
            Debug.Assert(mFileSystem.VolMDB != null);
            HFS_BTree extTree = mFileSystem.VolMDB.ExtentsFile!;
            HFS_VolBitmap vb = mFileSystem.VolBitmap!;

            // Round the logical length up to the next multiple of the allocation block size.
            uint allocSize = mFileSystem.VolMDB.BlockSize;
            uint neededBlocks = (uint)(logicalLength + allocSize - 1) / allocSize;
            uint currentBlocks = PhyLength / allocSize;

            if (neededBlocks > currentBlocks) {
                // Something went wrong along the way.
                throw new IOException("Logical length exceeds physical length");
            } else if (neededBlocks == currentBlocks) {
                // Nothing to do.
                return;
            }

            int discard = -1;
            uint keptBlocks = 0;
            int startBlock = 0;
            for (int ext = 0; ext < mExtents.Count; ext++) {
                ushort recSpan = mExtents[ext].AllocBlockSpan;

                if (neededBlocks == keptBlocks) {
                    // We have everything we need.  Discard the entire extent record.
                    if (ext == 0) {
                        // Special case: first descriptor is zeroed, not deleted.
                        for (int desc = 0; desc < ExtDataRec.NUM_RECS; desc++) {
                            ExtDescriptor ed = mExtents[0].Descs[desc];
                            vb.ReleaseBlocks(ed);
                            ed.xdrStABN = ed.xdrNumAblks = 0;
                        }
                    } else {
                        if (discard < 0) {
                            discard = ext;
                        }
                        HFS_Record.ExtKey extKey =
                            new HFS_Record.ExtKey(IsRsrcFork, mCNID, (ushort)startBlock);
                        extTree.Delete(extKey, false);
                        for (int desc = 0; desc < ExtDataRec.NUM_RECS; desc++) {
                            vb.ReleaseBlocks(mExtents[ext].Descs[desc]);
                        }
                    }
                } else {
                    // We need some or all of this extent record.
                    bool needSave = false;
                    for (int desc = 0; desc < ExtDataRec.NUM_RECS; desc++) {
                        ExtDescriptor ed = mExtents[ext].Descs[desc];
                        if (keptBlocks + ed.xdrNumAblks >= neededBlocks) {
                            uint keep = neededBlocks - keptBlocks;
                            if (keep == 0) {
                                // Don't need anything from this descriptor.
                                if (ed.xdrNumAblks != 0) {
                                    vb.ReleaseBlocks(ed);
                                    ed.xdrStABN = ed.xdrNumAblks = 0;
                                    needSave = true;
                                }
                            } else if (keep != ed.xdrNumAblks) {
                                // Only need some.  Truncate the descriptor, and mark the blocks
                                // as free in the volume bitmap.
                                ExtDescriptor rel = new ExtDescriptor();
                                rel.xdrStABN = (ushort)(ed.xdrStABN + keep);
                                rel.xdrNumAblks = (ushort)(ed.xdrNumAblks - keep);
                                vb.ReleaseBlocks(rel);
                                ed.xdrNumAblks = (ushort)keep;
                                needSave = true;
                            }

                            keptBlocks += keep;
                            Debug.Assert(keptBlocks <= neededBlocks);
                        } else {
                            // Need all of this descriptor.
                            keptBlocks += ed.xdrNumAblks;
                        }
                    }

                    if (needSave) {
                        SaveExtChange(ext, startBlock);
                    }
                }

                startBlock += recSpan;
            }

            // Remove excess objects from our list.
            if (discard > 0) {
                for (int i = mExtents.Count - 1; i >= discard; i--) {
                    mExtents.RemoveAt(i);
                }
            }

            // Adjust the file's physical length.
            uint newLength = neededBlocks * allocSize;
            Debug.Assert(newLength < PhyLength);
            PhyLength = newLength;
        }

        /// <summary>
        /// Writes a modified extent data record to disk.  Each record holds 3 extent descriptors.
        /// The first record is stored in the MDB or file catalog entry, the rest are stored in
        /// the extents overflow tree.
        /// </summary>
        /// <param name="recNum">Extent data record number.</param>
        /// <param name="startBlock"></param>
        /// <exception cref="DAException"></exception>
        /// <exception cref="IOException"></exception>
        private void SaveExtChange(int recNum, int startBlock) {
            HFS_MDB mdb = mFileSystem.VolMDB!;
            if (recNum == 0) {
                if (!IsTree) {
                    // This gets stored in the file's catalog entry when the fork is flushed.
                    // Nothing to do here.
                    return;
                } else {
                    // Update the MDB.
                    if (mCNID == HFS.EXTENTS_CNID) {
                        mdb.ExtentsFileRec = mExtents[0];
                    } else if (mCNID == HFS.CATALOG_CNID) {
                        mdb.CatalogFileRec = mExtents[0];
                    } else {
                        throw new DAException("should not be here: " + mCNID);
                    }
                }
            } else {
                // Find the extent record in the tree and update it.
                Debug.Assert(!IsExtentsOverflow);
                HFS_BTree extTree = mdb.ExtentsFile!;
                HFS_Record.ExtKey extKey =
                    new HFS_Record.ExtKey(IsRsrcFork, mCNID, (ushort)startBlock);
                if (!extTree.FindRecord(extKey, out HFS_Node? node, out int recordIndex)) {
                    throw new IOException("Unable to find extent: " + extKey);
                }
                Debug.Assert(node != null);

                // Replace the whole thing.
                HFS_Record rec = node.GetRecord(recordIndex);
                rec.UpdateExtDataRec(mExtents[recNum]);
                node.Write();
            }
        }

        public override string ToString() {
            string pathName;
            if (mCNID == HFS.EXTENTS_CNID) {
                pathName = ":extents:";
            } else if (mCNID == HFS.CATALOG_CNID) {
                pathName = ":catalog:";
            } else {
                pathName = mEntry.FullPathName;
            }
            return "[FileStorage: CNID=#" + mCNID.ToString("x4") + ", phyLen=" +
                PhyLength + ", name='" + pathName + "']";
        }
    }
}
