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

namespace DiskArc.FS {
    /// <summary>
    /// A node in a B*-tree.  IM:F 2-65
    /// </summary>
    /// <remarks>
    /// <para>Mapping a node index to a disk block is a multi-step process:</para>
    /// <list type="bullet">
    ///   <item>Convert the node index to an allocation index (divide by the number of
    ///     nodes per allocation block).</item>
    ///   <item>Find the Nth allocation block in the list of extents for the tree.</item>
    ///   <item>Map the allocation block number to a logical block number (divide by the
    ///     number of logical blocks per allocation block, then add the position of the
    ///     first allocation block).</item>
    /// </list>
    ///
    /// <para>Nodes do not have a "dirty" flag.  Code that alters a node's properties or
    /// records is expected to write the node to disk immediately.</para>
    /// </remarks>
    internal class HFS_Node : IEnumerable<HFS_Record> {
        /// <summary>
        /// HFS nodes fit exactly into a logical block.
        /// </summary>
        public const int NODE_SIZE = Defs.BLOCK_SIZE;

        /// <summary>
        /// Absolute maximum number of records that will fit in a node.  Most nodes will have
        /// far fewer.
        /// </summary>
        /// <remarks>
        /// <para>Extent index records are the smallest entity, with a key size of 8 bytes,
        /// followed by a 4-byte node index.  Each record also has a 2-byte offset at the end
        /// of the node, for a total of 14 bytes.  Subtracting 16 bytes from the node size to
        /// account for the node descriptor and free-space offset leaves 496, yielding a
        /// maximum of 496/14=35 records per node.</para>
        /// <para>Extent data records follow the 8-byte key with a 12-byte ExtDataRec, allowing
        /// a maximum of 22 records per node.  Catalog index records have a 38-byte key and a
        /// 4-byte node index, allowing 11 records per node.</para>
        /// <para>IM:F 2-70 says, "an index node can have from 1 to 15 children, depending on
        /// the size of the pointer records that the index node contains."</para>
        /// </remarks>
        private const int MAX_RECORDS = 35;

        //
        // Node Descriptor fields.
        //

        public const int NODE_DESC_LENGTH = 14;

        /// <summary>
        /// Node type definitions (for NodeDescriptor ndType field).  IM:F 2-65
        /// </summary>
        public enum NodeType : byte {
            Index = 0x00, Header = 0x01, Map = 0x02, Leaf = 0xff
        }

        /// <summary>ndFLink: forward link, or 0 if none.</summary>
        public uint FLink { get; set; }

        /// <summary>ndBLink: backward link, or 0 if none.</summary>
        public uint BLink { get; set; }

        /// <summary>ndType: Node type (index, header, map, or leaf).</summary>
        public byte Kind { get; set; }

        /// <summary><para>ndHeight: level of node in tree (header and map are at zero,
        ///   leaves are at level 1, index nodes are 2-8).</para></summary>
        public byte Height { get; set; }

        /// <summary>ndNRecs: number of records in node.</summary>
        public ushort NumRecords { get; set; }

        /// <summary>ndResv2: reserved.</summary>
        public ushort Reserved { get; set; }

        private void LoadDescriptor(byte[] buf, int offset) {
            int startOffset = offset;
            FLink = RawData.ReadU32BE(buf, ref offset);
            BLink = RawData.ReadU32BE(buf, ref offset);
            Kind = RawData.ReadU8(buf, ref offset);
            Height = RawData.ReadU8(buf, ref offset);
            NumRecords = RawData.ReadU16BE(buf, ref offset);
            Reserved = RawData.ReadU16BE(buf, ref offset);
            Debug.Assert(offset == startOffset + NODE_DESC_LENGTH);
        }
        private void StoreDescriptor(byte[] buf, int offset) {
            int startOffset = offset;
            RawData.WriteU32BE(buf, ref offset, FLink);
            RawData.WriteU32BE(buf, ref offset, BLink);
            RawData.WriteU8(buf, ref offset, Kind);
            RawData.WriteU8(buf, ref offset, Height);
            RawData.WriteU16BE(buf, ref offset, NumRecords);
            RawData.WriteU16BE(buf, ref offset, Reserved);
            Debug.Assert(offset == startOffset + NODE_DESC_LENGTH);
        }

        //
        // Additional fields.
        //

        /// <summary>
        /// Index of this node within the tree.
        /// </summary>
        public uint NodeIndex { get; private set; }

        /// <summary>
        /// Node data buffer.
        /// </summary>
        private byte[] DataBuf { get; set; }

        /// <summary>
        /// Logical block where the node is stored.
        /// </summary>
        private uint mLogiBlock;

        /// <summary>
        /// Reference to B*-tree structure that this node is a part of.
        /// </summary>
        private HFS_BTree mTree;

        /// <summary>
        /// Convenient list of records.  The properties and HFS_Record structures become
        /// invalid if the data underlying them is altered or moved around, so the list must
        /// be regenerated if number or position of records within a node changes.  (Modifications
        /// to record data areas are fine, modifications to record keys are illegal.)
        /// </summary>
        /// <remarks>
        /// This allows us to do processing without re-parsing the node contents and allocating
        /// new HFS_Record objects at every step, but creates the risk of getting out of sync
        /// with the data buffer contents.
        /// </remarks>
        private List<HFS_Record> mRecords;

        /// <summary>
        /// Creates a new node object, reading data from disk.
        /// </summary>
        /// <param name="tree">B*-tree this node is a part of.</param>
        /// <param name="nodeIndex">This node's index in the tree.</param>
        /// <returns>Node object.</returns>
        public static HFS_Node ReadNode(HFS_BTree tree, uint nodeIndex) {
            byte[] data = new byte[NODE_SIZE];
            uint logiBlock = tree.LogiBlockFromNodeIndex(nodeIndex);
            tree.ChunkAccess.ReadBlock(logiBlock, data, 0);
            return new HFS_Node(tree, nodeIndex, logiBlock, data);
        }

        /// <summary>
        /// Creates a new node object, reading data from a byte array.
        /// </summary>
        /// <param name="tree">B*-tree this node is a part of.</param>
        /// <param name="nodeIndex">This node's index in the tree.</param>
        /// <param name="data">Node data buffer.</param>
        /// <returns>Node object.</returns>
        public static HFS_Node CreateNode(HFS_BTree tree, uint nodeIndex, byte[] data) {
            Debug.Assert(data.Length == NODE_SIZE);
            uint logiBlock = tree.LogiBlockFromNodeIndex(nodeIndex);
            return new HFS_Node(tree, nodeIndex, logiBlock, data);
        }

        /// <summary>
        /// Private constructor.  Parses the node descriptor and record offsets.
        /// </summary>
        /// <param name="tree">Tree that this node is a part of.</param>
        /// <param name="nodeIndex">Index of node within tree.</param>
        /// <exception cref="IOException">Node contents are malformed.</exception>
        private HFS_Node(HFS_BTree tree, uint nodeIndex, uint logiBlock, byte[] data) {
            mTree = tree;
            NodeIndex = nodeIndex;
            mLogiBlock = logiBlock;
            DataBuf = data;

            // Parse the node descriptor.
            LoadDescriptor(DataBuf, 0);

            if (NumRecords > MAX_RECORDS) {
                throw new IOException("Invalid node record count (" + NumRecords + ")");
            }

            // There is one more offset than there are records; it points to the start of
            // free space.
            mRecords = new List<HFS_Record>(NumRecords);
            RegenerateList();
        }

        /// <summary>
        /// Regenerates the list of records.  Call this whenever the byte[] buffer is modified
        /// in a way that alters the position of the records (e.g. insert/delete).  It's not
        /// necessary to call this when only the contents of a record change.
        /// </summary>
        /// <exception cref="IOException">Damaged structure found.</exception>
        private void RegenerateList() {
            bool hasKeys = !(Kind == (byte)NodeType.Header || Kind == (byte)NodeType.Map);
            mRecords.Clear();
            ushort curOffset = RawData.GetU16BE(DataBuf, NODE_SIZE - 2);
            for (int i = 0; i < NumRecords; i++) {
                ushort nextOffset = RawData.GetU16BE(DataBuf, NODE_SIZE - (i + 2) * 2);
                if (nextOffset == 0) {
                    Debug.WriteLine(RawData.SimpleHexDump(DataBuf));
                    throw new IOException("Offset for record " + i + " was not set");
                }
                mRecords.Add(new HFS_Record(DataBuf, curOffset, nextOffset - curOffset, hasKeys));
                curOffset = nextOffset;
            }
        }

        /// <summary>
        /// Initializes a node buffer.  Fills out the node descriptor and initializes the
        /// table of offsets.
        /// </summary>
        /// <param name="buf">Data buffer.</param>
        /// <param name="type">Value for node type field.</param>
        /// <param name="height">Position in tree.</param>
        public static void InitEmptyNode(byte[] buf, NodeType type, byte height) {
            Debug.Assert(buf.Length == NODE_SIZE);

            // Populate the node descriptor.  Most fields are just left zeroed.
            Array.Clear(buf, 0, NODE_DESC_LENGTH);
            buf[0x08] = (byte)type;             // ndType
            buf[0x09] = height;                 // ndNHeight

            // Set the free space to point after the node descriptor.
            RawData.SetU16BE(buf, NODE_SIZE - 2, NODE_DESC_LENGTH);
        }

        /// <summary>
        /// Writes the node contents to disk.
        /// </summary>
        public void Write() {
            StoreDescriptor(DataBuf, 0);
            mTree.ChunkAccess.WriteBlock(mLogiBlock, DataBuf, 0);
        }

        // IEnumerable<HFS_Record>
        public IEnumerator<HFS_Record> GetEnumerator() {
            return ((IEnumerable<HFS_Record>)mRecords).GetEnumerator();
        }
        // IEnumerable<HFS_Record>
        IEnumerator IEnumerable.GetEnumerator() {
            return mRecords.GetEnumerator();
        }

        /// <summary>
        /// Gets a reference to the Nth record.
        /// </summary>
        public HFS_Record GetRecord(int recNum) {
            Debug.Assert(recNum >= 0 && recNum < NumRecords);
            Debug.Assert(mRecords.Count == NumRecords);
            return mRecords[recNum];
        }

        /// <summary>
        /// Gets the offset to the start of the Nth record.
        /// </summary>
        public ushort GetOffset(int recNum) {
            Debug.Assert(recNum >= 0 && recNum <= NumRecords);
            return RawData.GetU16BE(DataBuf, OffsetOfOffset(recNum));
        }

        /// <summary>
        /// Sets the offset to the start of the Nth record.
        /// </summary>
        private void SetOffset(int recNum, ushort offset) {
            Debug.Assert(recNum >= 0 && recNum <= NumRecords);
            RawData.SetU16BE(DataBuf, OffsetOfOffset(recNum), offset);
        }

        /// <summary>
        /// Returns the offset of the Nth entry in the offset table.
        /// </summary>
        public static int OffsetOfOffset(int recNum) {
            int off = NODE_SIZE - (recNum + 1) * 2;
            Debug.Assert(off >= 0 && off < NODE_SIZE);
            return off;
        }

        /// <summary>
        /// Determines if the node has enough room to hold a record.
        /// </summary>
        /// <param name="length">Length of record to test for.</param>
        /// <returns>True if there is enough space.</returns>
        public bool HasSpace(int length) {
            // See if there's enough room to hold the record plus another offset table entry.
            return CalcFreeSpace() >= length + 2;
        }

        /// <summary>
        /// Computes the amount of free space in the node.
        /// </summary>
        public int CalcFreeSpace() {
            // The start of free space is identified by the last entry in the offset table.  The
            // free space ends when it hits the bottom of the offset table.
            int freeSpaceEnd = OffsetOfOffset(NumRecords);
            int freeSpaceStart = RawData.GetU16BE(DataBuf, freeSpaceEnd);
            int freeSpace = freeSpaceEnd - freeSpaceStart;
            return freeSpace;
        }

        /// <summary>
        /// Computes the amount of space used for records.  Does not include the offset table.
        /// </summary>
        public int CalcUsedSpace() {
            int recordStart = GetOffset(0);
            int freeSpaceStart = GetOffset(NumRecords);
            int usedSpace = freeSpaceStart - recordStart;
            Debug.Assert(usedSpace == NODE_SIZE -
                (NODE_DESC_LENGTH + CalcFreeSpace() + (NumRecords + 1) * 2));
            return usedSpace;
        }

        /// <summary>
        /// Searches for a record with a matching key.  If not found, the number of the record
        /// it would be inserted after is returned instead.
        /// </summary>
        /// <param name="key">Key to search for.</param>
        /// <param name="recNum">Result: index of record found, or record to insert after.
        ///   Will be -1 if the argument key should come first.</param>
        /// <returns>True if a matching record was found.</returns>
        public bool FindRecord(HFS_Record.IKey key, out int recNum) {
            // Simple linear search.
            // TODO(maybe): a binary search might be faster for index nodes.
            for (recNum = mRecords.Count - 1; recNum >= 0; --recNum) {
                HFS_Record rec = mRecords[recNum];

                // Check for deleted keys.  Not sure that's possible.
                if (rec.KeyLength == 0) {
                    Debug.Assert(false);
                    continue;
                }

                // compare(key, record)
                int cmp = key.Compare(rec);
                if (cmp >= 0) {
                    // Found a record that matches, or that comes before the key.
                    return (cmp == 0);
                }
            }
            // Doesn't come after anything, so it must come first.  Return -1.
            return false;
        }

        /// <summary>
        /// Replaces a record with a new record of equal size.
        /// </summary>
        /// <remarks>
        /// Useful for replacing the key of an index record, when a new record is inserted to
        /// the left of the leftmost record.
        /// </remarks>
        /// <param name="recNum">Number of record to replace.</param>
        /// <param name="newRec">New record.</param>
        public void ReplaceRecord(int recNum, HFS_Record newRec) {
            Debug.Assert(newRec.DataLength > 0);

            HFS_Record oldRec = mRecords[recNum];
            if (oldRec.RecordLength != newRec.RecordLength) {
                throw new DAException("Cannot replace record with different length");
            }
            Array.Copy(newRec.DataBuf, newRec.RecordOffset,
                oldRec.DataBuf, oldRec.RecordOffset, newRec.RecordLength);

            // Records didn't move, so no need to regenerate list.
        }

        /// <summary>
        /// Inserts a record into the node.  If there isn't enough space, the node will be
        /// split first.
        /// </summary>
        /// <param name="record">Record to insert.</param>
        /// <param name="afterNum">Number of record that should come before this one.</param>
        /// <returns>New index record for right half of split node, or null if the node
        ///   did not have to be split.</returns>
        public HFS_Record? InsertRecord(HFS_Record record, int afterNum) {
            if (HasSpace(record.RecordLength)) {
                DoInsertRecord(record, afterNum);
                return null;
            } else {
                return SplitNode(record, afterNum);
            }
        }

        /// <summary>
        /// Inserts the record into the node at the specified position.
        /// </summary>
        /// <param name="record">Record to insert.</param>
        /// <param name="afterNum">Index of record to insert after.  To insert a record
        ///   at the start of the node, pass -1.</param>
        private void DoInsertRecord(HFS_Record record, int afterNum) {
            Debug.Assert(afterNum >= -1 && afterNum < NumRecords);

            // Move the records that come after the new one.
            int srcStart = GetOffset(afterNum + 1);
            int srcEnd = GetOffset(NumRecords);
            if (srcStart != srcEnd) {
                Array.Copy(DataBuf, srcStart, DataBuf, srcStart + record.RecordLength,
                    srcEnd - srcStart);
            }

            NumRecords++;

            // Rewrite the offsets of the records that moved, expanding the table by 1.  We
            // don't need to change the entry for the record we're adding.
            for (int i = NumRecords; i > afterNum + 1; --i) {
                SetOffset(i, (ushort)(GetOffset(i - 1) + record.RecordLength));
            }

            // Copy the new record in.
            Array.Copy(record.DataBuf, record.RecordOffset,
                    DataBuf, GetOffset(afterNum + 1), record.RecordLength);

            Debug.Assert(NumRecords <= MAX_RECORDS);

            RegenerateList();

            // Save the modified node to disk.
            Write();
        }

        /// <summary>
        /// Splits a record in half, and adds a new record.
        /// </summary>
        /// <param name="newRecord">Record to add.</param>
        /// <param name="afterNum">Position to insert new record after.</param>
        /// <returns>New index record for node on right side of split.</returns>
        private HFS_Record SplitNode(HFS_Record newRecord, int afterNum) {
            // The goal is for the two new nodes to be equal in size.  If this is an index node
            // we can just count records, but for a leaf node we need to take into account the
            // variable sizes of the records.  The new record could be added to either node, so
            // we want to split the node based on how it will look after the record is inserted.
            //
            // The largest possible record is a catalog file entry, at 140 bytes (key area can
            // be up to 38 bytes long, data area is 102).
            //
            // NOTE: encountering a bad block can lead to corruption.  We can mitigate it by
            // writing to the new (right) node first, but failing to insert the new key in an
            // index block will cause files to be missing from the search hierarchy.  They can
            // still be found by a linear leaf walk, so a disk recovery tool can restore them.
            //
            // h/t: libhfs split()

            // Find the pivot point, including the new record's length and offset table bytes.
            int usedSpace = CalcUsedSpace() + NumRecords * 2;
            int remaining = (usedSpace + newRecord.RecordLength + 2) / 2;

            bool insertLeft = false;
            if (afterNum < 0) {
                // We won't hit this in the loop below, so handle it now.
                insertLeft = true;
                remaining -= newRecord.RecordLength + 2;
            }
            int keepCount = 0;
            for (int i = 0; i < NumRecords; i++) {
                if (remaining < 0) {
                    break;
                }
                remaining -= GetRecord(i).RecordLength + 2;
                keepCount++;

                if (i == afterNum) {
                    // New record will be inserted here.  Pick side based on whether we
                    // just flipped.
                    insertLeft = (remaining > 0);
                    remaining -= newRecord.RecordLength + 2;
                }
            }

            // Should be at least one node on either side.  If afterNum==NumRecords then it's
            // reasonable for keepCount==NumRecords, with only the new record on the right.
            Debug.Assert(keepCount > 0 && keepCount <= NumRecords);

            // We now know how many records to keep on the left, and which side to insert the
            // new record on.  Now we need to allocate a new node and move the records over.
            //
            // We pre-flighted the available storage space, so allocation should not fail.
            HFS_Node newNode = mTree.AllocNode((NodeType)Kind, Height);

            for (int i = keepCount; i < NumRecords; i++) {
                CopyRecord(i, newNode);
            }

            // Delete the records from the original node.  We just need to change NumRecords
            // and delete the extras from the object list.  (We could tidy up the record by
            // cleaning the offset list, but that's not necessary.)
#if DEBUG
            // DEBUG: scrub the data so it's obvious if we try to re-use it.
            for (int i = NumRecords - 1; i >= keepCount; i--) {
                RawData.MemSet(DataBuf, mRecords[i].RecordOffset, mRecords[i].RecordLength, 0xcc);
                int offOff = OffsetOfOffset(i + 1);
                RawData.SetU16BE(DataBuf, offOff, 0xc0de);
            }
#endif
            NumRecords = (ushort)keepCount;
            RegenerateList();
            newNode.RegenerateList();

            // Set the forward/backward links in the two nodes.
            newNode.FLink = FLink;
            newNode.BLink = NodeIndex;
            FLink = newNode.NodeIndex;

            // Now insert the new record in the appropriate node.
            if (insertLeft) {
                // The position should not have changed.
#if DEBUG
                HFS_Record.IKey debugKey = newRecord.UnpackKey(mTree.TreeType);
                FindRecord(debugKey, out int debugAfterNum);
                Debug.Assert(debugAfterNum == afterNum);
#endif
                DoInsertRecord(newRecord, afterNum);
                newNode.Write();
            } else {
                // The position has shifted by the number of nodes moved.
                afterNum -= keepCount;
#if DEBUG
                HFS_Record.IKey debugKey = newRecord.UnpackKey(mTree.TreeType);
                newNode.FindRecord(debugKey, out int debugAfterNum);
                Debug.Assert(debugAfterNum == afterNum);
#endif
                newNode.DoInsertRecord(newRecord, afterNum);
                Write();
            }

            // Create an index record that points to the new node.  The key we want to use is
            // in the first (leftmost) record.
            HFS_Record splitRec = HFS_Record.CreateIndexRecord(mTree, newNode.GetRecord(0),
                newNode.NodeIndex);

            if (mTree.Header.LastLeaf == NodeIndex) {
                // The left node used to be the last leaf.  Update the tree header.
                Debug.Assert((NodeType)Kind == NodeType.Leaf);
                mTree.Header.LastLeaf = newNode.NodeIndex;
            }

            // Update the backward link in the node to the right, if it exists.
            if (newNode.FLink != 0) {
                HFS_Node righty = mTree.GetNode(newNode.FLink);
                Debug.Assert(righty.BLink == NodeIndex);
                righty.BLink = newNode.NodeIndex;
                righty.Write();
            }

            return splitRec;
        }

        /// <summary>
        /// Copies a record to the end of another node.  Assumes the records are being copied
        /// in sorted order, and that the destination node has enough space.
        /// </summary>
        /// <remarks>
        /// The caller should call RegenerateList on destNode after all operations are complete.
        /// </remarks>
        /// <param name="recNum">Record number to copy.</param>
        /// <param name="destNode">Destination node.</param>
        private void CopyRecord(int recNum, HFS_Node destNode) {
            Debug.Assert(mRecords.Count == NumRecords);
            HFS_Record rec = mRecords[recNum];
            Debug.Assert(rec.DataLength != rec.RecordLength);   // we pass "has key" later
            Debug.Assert(CalcFreeSpace() + rec.RecordLength + 2 < NODE_SIZE);

            // Destination is the start of the free space.
            destNode.mRecords.Clear();      // debug - make sure we don't try to use this
            ushort destOffset = destNode.GetOffset(destNode.NumRecords);
            Array.Copy(DataBuf, rec.RecordOffset, destNode.DataBuf, destOffset, rec.RecordLength);
            destNode.SetOffset(destNode.NumRecords, destOffset);
            // Update number of records, and the offset to the start of the free space.
            destNode.NumRecords++;
            ushort newFreeSpaceStart = (ushort)(destOffset + rec.RecordLength);
            Debug.Assert(newFreeSpaceStart < NODE_SIZE);
            destNode.SetOffset(destNode.NumRecords, newFreeSpaceStart);

            // Add an HFS_Record object too.
            //destNode.RegenerateList();
            //destNode.mRecords.Add(new HFS_Record(destNode.DataBuf, destOffset, rec.RecordLength,
            //    true));
        }

        /// <summary>
        /// Deletes a record from the node.  When the last record is deleted, the node itself
        /// is deleted as well.  If the number of records in this node and the left sibling are
        /// both very small, the contents of this node may be moved over, after which this node
        /// will be deleted.
        /// </summary>
        /// <param name="recNum">Index of record to delete.</param>
        /// <param name="wasDeleted">Result: true if the node was emptied and deleted.</param>
        /// <returns>New index record for node, if the first record was deleted or the
        ///   node was joined with a sibling.  Will be null if the node was deleted.</returns>
        public HFS_Record? DeleteRecord(int recNum, out bool wasDeleted) {
            Debug.Assert(recNum >= 0 && recNum < NumRecords);

            if (NumRecords == 1) {
                // This was the only record in the node.
                NumRecords = 0;
                mTree.ReleaseNode(this);
                wasDeleted = true;
                return null;
            }
            wasDeleted = false;

            // If this isn't the last record, shift the ones that come after it.
            if (recNum != NumRecords - 1) {
                int dest = GetOffset(recNum);
                int srcStart = GetOffset(recNum + 1);
                int srcEnd = GetOffset(NumRecords);
                int diff = mRecords[recNum].RecordLength;
                Array.Copy(DataBuf, srcStart, DataBuf, dest, srcEnd - srcStart);

                // Shift the offsets of the records that moved.
                Debug.Assert(diff == srcStart - dest);
                for (int i = recNum + 1; i < NumRecords; i++) {
                    SetOffset(i, (ushort)(GetOffset(i + 1) - diff));
                }
            }

            // Remove from our list.
            NumRecords--;
            RegenerateList();

            // Optional space optimization: see if we want to merge the contents of this node
            // into the left sibling.  We can copy the records over without altering the key
            // of that node, and when we're done we can simply delete ourselves like we would
            // if we had been emptied.
            // (h/t: libhfs n_delete() / join())
            //
            // This adds an extra node read on every deletion, so this may represent a slight
            // performance loss (with some improvement potentially gained by having fewer nodes
            // to search when opening files).  It should allow us to fill nodes more completely
            // and reduce tree expansion, so overall it seems like a win.  If we're too
            // aggressive however we could end up in a state where file accesses are causing
            // frequent split/join behavior.
            //
            // Try this: if we're less than half full, read the node to the left.  If the combined
            // size is less then 3/4 full, do the join.  This should clean up severely undersized
            // nodes without significantly increasing general disk activity.
            int spaceUsed = CalcUsedSpace() + NumRecords * 2;
            if (BLink != 0 && spaceUsed < NODE_SIZE / 2) {
                HFS_Node leftNode = mTree.GetNode(BLink);
                int leftUsed = leftNode.CalcUsedSpace() + leftNode.NumRecords * 2;
                if (spaceUsed + leftUsed < NODE_SIZE * 3 / 4) {
                    // Copy all of our records to the left node.  Because they're to the left,
                    // we know that our records come later in the sort order, so we can just
                    // add ours to the end.
                    for (int i = 0; i < NumRecords; i++) {
                        CopyRecord(i, leftNode);
                    }
                    // don't need to call RegenerateList() since we're not keeping the object
                    leftNode.Write();

                    // Throw ourselves away.
                    NumRecords = 0;
                    mTree.ReleaseNode(this);
                    wasDeleted = true;
                    return null;
                }
            }

            Write();

            if (recNum == 0) {
                // The record that the parent uses to find this node was deleted.  Return a
                // new index record for this node, with the key for the first record.
                return HFS_Record.CreateIndexRecord(mTree, mRecords[0], NodeIndex);
            }
            return null;
        }

        public override string ToString() {
            return "[Node: type=" + (NodeType)Kind + ", index=" + NodeIndex +
                ", numRecs=" + NumRecords + "]";
        }
    }
}
