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
using static DiskArc.FS.HFS_Struct;

namespace DiskArc.FS {
    /// <summary>
    /// Management of a B*-tree file on an HFS volume.  There are two: the extents overflow file,
    /// and the disk catalog file.
    /// </summary>
    internal class HFS_BTree {
        /// <summary>
        /// Tree type.
        /// </summary>
        public enum Type {
            Unknown = 0, Extents, Catalog
        }

        /// <summary>
        /// What type of data is stored in this tree.
        /// </summary>
        public Type TreeType { get; }

        /// <summary>
        /// True if damage has been found that prevents us from safely modifying the tree.
        /// </summary>
        public bool IsDubious { get; private set; }

        /// <summary>
        /// File storage tracker.
        /// </summary>
        private HFS_FileStorage mFileStorage;

        /// <summary>
        /// Filesystem object reference.
        /// </summary>
        private HFS mFileSystem;

        /// <summary>
        /// Block access object.  Used by the HFS_Node management code.
        /// </summary>
        internal IChunkAccess ChunkAccess { get { return mFileSystem.ChunkAccess; } }

        /// <summary>
        /// Number of logical blocks per allocation block.
        /// </summary>
        private uint mLogiPerAllocBlock;


        #region Header Node

        /// <summary>
        /// Parsed header node.
        /// </summary>
        public HeaderNode Header { get; private set; }

        /// <summary>
        /// Contents of the header node that appears in node 0 of a B*-tree.  IM:F 2-67
        /// </summary>
        internal class HeaderNode {
            public const int MAP_REC_LEN = FREE_OFFSET - MAP_OFFSET;   // 256 bytes

            // Header nodes have a specific record layout.
            private const int NUM_RECS = 3;
            public enum Rec {
                Header = 0,
                Unused = 1,
                Map = 2
            };
            private const ushort HDR_OFFSET = 0x000e;       // offset to header record (106 bytes)
            private const ushort UNUSED_OFFSET = 0x0078;    // offset to unused record (128 bytes)
            private const ushort MAP_OFFSET = 0x00f8;       // offset to map record (256 bytes)
            private const ushort FREE_OFFSET = 0x01f8;      // offset to free space (0 bytes)

            public const int HDR_REC_LENGTH = 106;
            private const int RESV_LEN = 76;

            /// <summary>bthDepth: current depth of tree.</summary>
            public ushort Depth {
                get { return bthDepth; }
                set { bthDepth = value; IsDirty = true; }
            }
            private ushort bthDepth;

            /// <summary>bthRoot: number of root node, or 0 if the tree is completely empty.
            /// While this is usually an index node, it could be a leaf node in a very
            /// small tree.</summary>
            public uint RootNode {
                get { return bthRoot; }
                set { bthRoot = value; IsDirty = true; }
            }
            private uint bthRoot;

            /// <summary>bthNRecs: number of data records, i.e. records contained in leaf
            /// nodes.</summary>
            public uint NumDataRecs {
                get { return bthNRecs; }
                set { bthNRecs = value; IsDirty = true; }
            }
            private uint bthNRecs;

            /// <summary>bthFNode: number of first leaf node.</summary>
            public uint FirstLeaf {
                get { return bthFNode; }
                set { bthFNode = value; IsDirty = true; }
            }
            private uint bthFNode;

            /// <summary>bthLNode: number of last leaf node.</summary>
            public uint LastLeaf {
                get { return bthLNode; }
                set { bthLNode = value; IsDirty = true; }
            }
            private uint bthLNode;

            /// <summary>bthNodeSize: size of a node, in bytes.  Always 512.</summary>
            public ushort NodeSize {
                get { return bthNodeSize; }
                set { Debug.Assert(value == HFS_Node.NODE_SIZE);
                      bthNodeSize = value; IsDirty = true; }
            }
            private ushort bthNodeSize;

            /// <summary>bthKeyLen: maximum length of a key, determined by tree type.</summary>
            public ushort MaxKeyLen {
                get { return bthKeyLen; }
                set { bthKeyLen = value; IsDirty = true; }
            }
            private ushort bthKeyLen;

            /// <summary>bthNNodes: total number of nodes in tree.</summary>
            public uint NumNodes {
                get { return bthNNodes; }
                set { bthNNodes = value; IsDirty = true; }
            }
            private uint bthNNodes;

            /// <summary>bthFree: number of free nodes in tree.</summary>
            public uint FreeNodes {
                get { return bthFree; }
                set { bthFree = value; IsDirty = true; }
            }
            private uint bthFree;

            public void Load(byte[] buf, int offset) {
                int startOffset = offset;
                bthDepth = RawData.ReadU16BE(buf, ref offset);
                bthRoot = RawData.ReadU32BE(buf, ref offset);
                bthNRecs = RawData.ReadU32BE(buf, ref offset);
                bthFNode = RawData.ReadU32BE(buf, ref offset);
                bthLNode = RawData.ReadU32BE(buf, ref offset);
                bthNodeSize = RawData.ReadU16BE(buf, ref offset);
                bthKeyLen = RawData.ReadU16BE(buf, ref offset);
                bthNNodes = RawData.ReadU32BE(buf, ref offset);
                bthFree = RawData.ReadU32BE(buf, ref offset);
                //Array.Copy(buf, offset, bthResv, 0, RESV_LEN);
                offset += RESV_LEN;
                Debug.Assert(offset == startOffset + HDR_REC_LENGTH);
            }
            public void Store(byte[] buf, int offset) {
                int startOffset = offset;
                RawData.WriteU16BE(buf, ref offset, bthDepth);
                RawData.WriteU32BE(buf, ref offset, bthRoot);
                RawData.WriteU32BE(buf, ref offset, bthNRecs);
                RawData.WriteU32BE(buf, ref offset, bthFNode);
                RawData.WriteU32BE(buf, ref offset, bthLNode);
                RawData.WriteU16BE(buf, ref offset, bthNodeSize);
                RawData.WriteU16BE(buf, ref offset, bthKeyLen);
                RawData.WriteU32BE(buf, ref offset, bthNNodes);
                RawData.WriteU32BE(buf, ref offset, bthFree);
                //Array.Copy(bthResv, 0, buf, offset, RESV_LEN);
                offset += RESV_LEN;
                Debug.Assert(offset == startOffset + HDR_REC_LENGTH);
            }

            /// <summary>
            /// Reference to node header is stored in.
            /// </summary>
            public HFS_Node Node { get; private set; }

            public HeaderNode(HFS_BTree tree, uint nodeIndex, uint logiBlock) {
                Node = HFS_Node.ReadNode(tree, nodeIndex);

                // Do a quick check to confirm that this is actually a header node.
                if (Node.NumRecords != NUM_RECS) {
                    throw new IOException("Unexpected number of records in header node (" +
                        Node.NumRecords + ")");
                }
                if (Node.GetRecord(0).RecordOffset != HDR_OFFSET ||
                        Node.GetRecord(1).RecordOffset != UNUSED_OFFSET ||
                        Node.GetRecord(2).RecordOffset != MAP_OFFSET) {
                    throw new IOException("Unexpected header node offsets: " +
                        Node.GetRecord(0).RecordOffset + " / " +
                        Node.GetRecord(1).RecordOffset + " / " +
                        Node.GetRecord(2).RecordOffset);
                }

                // Load the header record fields.  Header node records don't have keys, so just
                // use the record offset.
                HFS_Record rec = Node.GetRecord((int)Rec.Header);
                Load(rec.DataBuf, rec.RecordOffset);

                Debug.Assert(Node.CalcFreeSpace() == 0);
                Debug.Assert(!IsDirty);
            }

            public bool IsDirty { get; private set; }

            public void SetDirty() {
                IsDirty = true;
            }

            public void Flush() {
                if (IsDirty) {
                    Write();
                }
            }

            public void Write() {
                // Copy header values out to byte buffer.
                HFS_Record rec = Node.GetRecord((int)Rec.Header);
                Store(rec.DataBuf, rec.RecordOffset);
                // Write data to disk.
                Node.Write();
                IsDirty = false;
            }

            public HFS_Record GetMapRecord() {
                return Node.GetRecord((int)Rec.Map);
            }

            /// <summary>
            /// Initializes the node descriptor and B*-tree header record field in a buffer.
            /// Used when creating the header node for a new tree.
            /// </summary>
            /// <param name="keyLen">Maximum length of a key in this tree.</param>
            /// <param name="numNodes">Number of nodes initially allocated.</param>
            public static void CreateHeaderNode(IChunkAccess chunkAccess, uint logiBlock,
                    byte keyLen, uint numNodes) {
                byte[] buf = new byte[HFS_Node.NODE_SIZE];

                HFS_Node.InitEmptyNode(buf, HFS_Node.NodeType.Header, 0);

                // Set number of records to 3.
                RawData.SetU16BE(buf, 0x0a, 3);                                 // ndNRecs

                // Init the B*-tree header node.  Most fields can be left set to zero.
                RawData.SetU16BE(buf, HDR_OFFSET + 0x12, HFS_Node.NODE_SIZE);  // bthNodeSize
                RawData.SetU16BE(buf, HDR_OFFSET + 0x14, keyLen);              // bthKeyLen
                RawData.SetU32BE(buf, HDR_OFFSET + 0x16, numNodes);            // bthNNodes
                RawData.SetU32BE(buf, HDR_OFFSET + 0x1a, numNodes - 1);        // bthFree

                // Set the offsets for the three header records and the (non-)free space.
                RawData.SetU16BE(buf, 0x1f8, FREE_OFFSET);
                RawData.SetU16BE(buf, 0x1fa, MAP_OFFSET);
                RawData.SetU16BE(buf, 0x1fc, UNUSED_OFFSET);
                RawData.SetU16BE(buf, 0x1fe, HDR_OFFSET);

                // Mark node 0 as in-use in the bitmap.
                buf[MAP_OFFSET] = 0x80;

                chunkAccess.WriteBlock(logiBlock, buf, 0);
            }

            public override string ToString() {
                return "[HeaderNode: root=" + RootNode + " count=" + NumNodes + " free=" +
                    FreeNodes + "]";
            }
        }

        #endregion Header Node

        #region Map Nodes

        internal class MapNode  {
            // Number of bytes in the bitmap, which is the only record in the node.  There's a
            // 14-byte header, 2 bytes for the offset, and 2 bytes for the free-space offset.
            // Result is 512-18=494 bytes, or 3952 bits.  So says logic and IM:F page 2-69.
            // In practice, the record must be 492 bytes.
            public const int MAP_BYTES = HFS_Node.NODE_SIZE - HFS_Node.NODE_DESC_LENGTH - 4 - 2;

            public bool IsDirty { get; private set; }

            public HFS_Node Node { get; private set; }

            public void SetDirty() {
                IsDirty = true;
            }

            public void Flush() {
                if (IsDirty) {
                    Node.Write();
                    IsDirty = false;
                }
            }

            public MapNode(HFS_BTree tree, uint nodeIndex) {
                Node = HFS_Node.ReadNode(tree, nodeIndex);
                if (Node.NumRecords != 1 || Node.GetRecord(0).RecordLength != MAP_BYTES) {
                    tree.mFileSystem.Notes.AddE("Invalid map node found in tree (" +
                        tree.TreeType + ")");
                    throw new IOException("Invalid map node found");
                }
                Debug.Assert(!IsDirty);
            }

            public HFS_Record GetRecord() {
                return Node.GetRecord(0);      // bitmap is first and only record in the node
            }
        }

        /// <summary>
        /// Map nodes, used to extend the node usage bitmap for large tree files.
        /// </summary>
        private List<MapNode> mMapNodes = new List<MapNode>();

        /// <summary>
        /// Populates the map node list by reading nodes out of the tree.
        /// </summary>
        /// <param name="createNodes">True when formatting a new volume.</param>
        private void LoadMapNodes(bool createNodes) {
            Debug.Assert(mMapNodes.Count == 0);

            uint numNodes = Header.NumNodes;
            if (numNodes <= HeaderNode.MAP_REC_LEN * 8) {
                // The bitmap in the header node covers everything.
                return;
            }

            // A tree is initially created as 1/128th of the size of the volume.  The 256-byte
            // map in the header (which holds bits for the first 2048 nodes) will cover the
            // first extent of a tree on a volume up to roughly 2048 * 128 * NODE_SIZE = 128MB.
            // (If we have 65535 allocation blocks at 2KB each, each tree file is initially created
            // at 65535/128=511 allocation blocks.  At one node per logical block, the tree
            // holds 511*(2KB/512)=2044 nodes.)
            //
            // Looking at the worst case, a 4GB volume has at most 65535 allocation blocks at
            // 64KB each.  The tree file is again initially created at 65535/128=511 allocation
            // blocks, which will hold 511*(64KB/512)=65408 nodes.  The header node holds 2048
            // bits, after which we need ceil((65408-2048)/(494*8)) = 17 map blocks to hold the
            // rest.  A 2GB file needs 8 map blocks.
            //
            // Additional map blocks may be needed if the tree expands.

            if (createNodes) {
                ExpandMapCoverage();
            } else {
                try {
                    // Map nodes are singly-linked using the node descriptor forward/back links.
                    uint nodeIndex = Header.Node.FLink;
                    int iter = 0;
                    while (nodeIndex != 0) {
                        MapNode newMap = new MapNode(this, nodeIndex);
                        mMapNodes.Add(newMap);
                        nodeIndex = newMap.Node.FLink;
                        if (++iter > HFS.MAX_MAP_NODES) {
                            throw new IOException("Circular map nodes");
                        }
                    }
                } catch (IOException) {
                    // We don't need the map to read files, so just mark the volume "dubious".
                    mFileSystem.Notes.AddE("Failed parsing tree map nodes");
                    IsDubious = true;
                }
            }
        }

        #endregion Map Nodes


        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="extRec">Reference to extent data record, held in the MDB.</param>
        /// <exception cref="IOException">Disk access failure.</exception>
        public HFS_BTree(HFS fileSystem, uint treeCNID, uint phyLength, ExtDataRec extRec,
                bool createMapNodes) {
            mFileSystem = fileSystem;

            switch (treeCNID) {
                case HFS.EXTENTS_CNID:
                    TreeType = Type.Extents;
                    break;
                case HFS.CATALOG_CNID:
                    TreeType = Type.Catalog;
                    break;
                default:
                    throw new ArgumentException("Unknown cnid #" + treeCNID.ToString("x4"));
            }

            mFileStorage = new HFS_FileStorage(fileSystem, IFileEntry.NO_ENTRY, treeCNID, false,
                phyLength, extRec);

            Debug.Assert(fileSystem.VolMDB != null);
            mLogiPerAllocBlock = fileSystem.VolMDB.BlockSize / BLOCK_SIZE;

            // Read the tree's header node.
            uint firstLogi = mFileSystem.ExtentToLogiBlocks(extRec.Descs[0], out uint unused);
            Header = new HeaderNode(this, 0, firstLogi);

            // Load the map nodes, creating them if we're formatting the volume.
            LoadMapNodes(createMapNodes);
        }

        /// <summary>
        /// Performs some basic checks on the tree structure.  This is done after the tree has
        /// been prepared for use, so we can assume that the basic header fields are fine.
        /// </summary>
        public bool CheckTree() {
            Notes notes = mFileSystem.Notes;
            string treeName = (TreeType == Type.Extents) ? "extents" : "catalog";
            bool isOkay = true;

            uint nodeIndex = Header.RootNode;
            List<uint> nextLevel = new List<uint>();
            nextLevel.Add(nodeIndex);
            for (int level = Header.Depth; level > 0; level--) {
                uint prevNodeIndex = 0;
                uint leftChildIndex = 0;
                int count = 0;
                List<uint> prevLevel = nextLevel;
                nextLevel = new List<uint>();

                while (nodeIndex != 0) {
                    // See if our sibling traversal matches the previous level's child pointers.
                    if (nodeIndex != prevLevel[count]) {
                        notes.AddW("Node traversal doesn't match indices: expected " +
                            prevLevel[count] + ", found " + nodeIndex);
                        isOkay = false;
                    }
                    count++;

                    HFS_Node node = GetNode(nodeIndex);
                    if (node.Height != level) {
                        notes.AddW("Incorrect node height; index=" + node.NodeIndex);
                        isOkay = false;
                    }
                    if (node.NumRecords == 0) {
                        notes.AddW("Found empty node; index=" + node.NodeIndex);
                    }
                    if (level == 1) {
                        if (node.Kind != (byte)HFS_Node.NodeType.Leaf) {
                            notes.AddW("Found non-leaf node on level 1; index=" + node.NodeIndex);
                            isOkay = false;
                        }
                    } else {
                        if (node.Kind != (byte)HFS_Node.NodeType.Index) {
                            notes.AddW("Found leaf node on level 2+; index=" + node.NodeIndex);
                            isOkay = false;
                        } else {
                            // Add all child nodes to next-level list.
                            foreach (HFS_Record rec in node) {
                                nextLevel.Add(rec.GetIndexNodeData());
                            }
                        }
                    }

                    if (node.BLink != prevNodeIndex) {
                        notes.AddW("Incorrect backward link in index node " + node.NodeIndex);
                        isOkay = false;
                    }

                    if (prevNodeIndex == 0 && node.Kind == (byte)HFS_Node.NodeType.Index &&
                            node.NumRecords > 0) {
                        // First index node on level.  Save pointer to leftmost child.
                        leftChildIndex = node.GetRecord(0).GetIndexNodeData();
                    }

                    // Walk across the level.
                    prevNodeIndex = nodeIndex;
                    nodeIndex = node.FLink;
                    if (nodeIndex == 0) {
                        // Reached the end.
                        break;
                    }
                }

                // Drop down a level.
                if (leftChildIndex == 0) {
                    if (level != 1) {
                        notes.AddW("Unable to drop down to leaf level");
                        isOkay = false;
                        break;
                    }
                }
                nodeIndex = leftChildIndex;
            }

            return isOkay;
        }

        /// <summary>
        /// Reads from disk and parses the contents of the Nth node in the tree.
        /// </summary>
        /// <param name="index">Index into tree nodes; 0 is the header node.</param>
        /// <returns>Node object.</returns>
        /// <exception cref="IOException">Disk access failure, or invalid node contents.</exception>
        public HFS_Node GetNode(uint index) {
            return HFS_Node.ReadNode(this, index);
        }

        /// <summary>
        /// Searches for a record with a matching key.  If not found, the record with the
        /// largest value smaller than the argument is returned instead.  (In an index node,
        /// the value indicates the link that should be followed; in a leaf node, the new record
        /// should be inserted after the one referenced.)
        /// </summary>
        /// <remarks>
        /// <para>The key type must match the tree type.</para>
        /// </remarks>
        /// <param name="key">Key to search for.</param>
        /// <param name="node">Result: node where record is or should be.  Will be null if the tree
        ///   is completely empty.</param>
        /// <param name="recordNum">Result: number of record within node.  Will be -1 if the record
        ///   should become the first one.</param>
        /// <returns>True if a matching record was found.</returns>
        public bool FindRecord(HFS_Record.IKey key, out HFS_Node? node, out int recordNum) {
            Debug.Assert((key is HFS_Record.CatKey && TreeType == Type.Catalog) ||
                         (key is HFS_Record.ExtKey && TreeType == Type.Extents));

            int iter = 0;

            uint nodeIndex = Header.RootNode;
            if (nodeIndex == 0) {
                // Empty tree.
                node = null;
                recordNum = -1;
                return false;
            }
            while (true) {
                node = GetNode(nodeIndex);
                bool found = node.FindRecord(key, out recordNum);
                if (node.Kind == (byte)HFS_Node.NodeType.Leaf) {
                    // End of the line.
                    return found;
                } else if (node.Kind == (byte)HFS_Node.NodeType.Index) {
                    // Pull the child node index number out of the record.
                    HFS_Record rec = node.GetRecord(recordNum);
                    nodeIndex = rec.GetIndexNodeData();
                } else {
                    throw new IOException("Unexpected node type " + node.Kind +
                        "encountered");
                }

                if (++iter > HFS.MAX_BTREE_HEIGHT) {
                    throw new IOException("Tree is cyclical");
                }
            }
        }

        /// <summary>
        /// Ensures that the tree has enough space to hold the specified number of records.
        /// If there isn't, the tree's node pool will be expanded.  If that fails, an
        /// exception will be thrown.
        /// </summary>
        /// <remarks>
        /// <para>The easiest way to do this is to assume the worst-case scenario: the
        /// addition of each record splits the leaf node, and the result propagates all the
        /// way up the tree, splitting the root.  So we need there to be enough free nodes to
        /// hold the current height of the tree, plus one.</para>
        /// <para>In some cases this might cause us to report the disk as full when the
        /// operation would have succeeded, but that will only happen if the tree is almost
        /// full *and* the disk is so full that the tree can't grow.</para>
        /// </remarks>
        /// <param name="numRecs">Number of data records we plan to add.</param>
        /// <exception cref="DiskFullException">Insufficient space exists.</exception>
        /// <exception cref="IOException">Disk access failure.</exception>
        public void EnsureSpace(int numRecs) {
            // Assume full split.
            // Strictly speaking, we might need to add one more to hold a new map node if the
            // tree expansion runs past the current bitmap.  This is only a concern for
            // larger volumes, which will have chunk sizes that significantly exceed the
            // tree height, so this is unlikely to be an issue.
            uint nodesNeeded = (uint)(numRecs * (Header.Depth + 1));
            if (Header.FreeNodes >= nodesNeeded) {
                return;
            }
            uint desiredSize = (Header.NumNodes + nodesNeeded - Header.FreeNodes) *
                    HFS_Node.NODE_SIZE;

            // Add extents until the file reaches the desired size.
            while (mFileStorage.PhyLength < desiredSize) {
                // Extend by a chunk.  We may not get a full one, but we will get at least one
                // allocation block unless the disk is completely out of space.
                mFileStorage.Extend();

                uint newTotalCount = mFileStorage.PhyLength / HFS_Node.NODE_SIZE;
                Debug.Assert(newTotalCount > Header.NumNodes);

                // Expand the node bitmap if necessary.
                ExpandMapCoverage();

                // Update the counts.
                uint diff = newTotalCount - Header.NumNodes;
                Header.NumNodes += diff;
                Header.FreeNodes += diff;
                if (TreeType == Type.Catalog) {
                    Debug.Assert(Header.NumNodes * 512 == mFileSystem.VolMDB!.CatalogFileSize);
                }
            }

            Header.Flush();
        }

        /// <summary>
        /// Expands the node bitmap if the current set of map nodes doesn't cover the entire
        /// tree file.  The new nodes are added to the list.
        /// </summary>
        /// <remarks>
        /// Used when formatting a new volume or expanding the tree's storage.
        /// </remarks>
        private void ExpandMapCoverage() {
            uint nodeCount = mFileStorage.PhyLength / HFS_Node.NODE_SIZE;
            int coveredNodes = HeaderNode.MAP_REC_LEN * 8 +
                    mMapNodes.Count * MapNode.MAP_BYTES;
            while (coveredNodes < nodeCount) {
                // Allocate a new map node.
                //
                // Warning: this will fail if the current map is completely full when the tree
                // needs to expand.  This can only happen if the number of nodes in the tree
                // falls exactly on a map boundary, and only if we managed to fill the tree
                // completely in spite of our careful "ensure space" over-allocation.  It's
                // very unlikely, but not impossible; to avoid the situation we'd need to allow
                // the map node to be allocated out of the unmapped space we just created.
                HFS_Node newNode = AllocNode(HFS_Node.NodeType.Map, 0);
                byte[] emptyMap = new byte[MapNode.MAP_BYTES];
                HFS_Record rec = new HFS_Record(emptyMap, 0, MapNode.MAP_BYTES, false);
                newNode.InsertRecord(rec, -1);

                // Set the forward link pointer on the last node in the chain.
                if (Header.Node.FLink == 0) {
                    Header.Node.FLink = newNode.NodeIndex;
                    //newNode.Node.BLink = 0;
                    Header.Write();
                } else {
                    HFS_Node lastNode = mMapNodes[mMapNodes.Count - 1].Node;
                    lastNode.FLink = newNode.NodeIndex;
                    newNode.BLink = lastNode.NodeIndex;
                    lastNode.Write();
                }
                newNode.Write();

                mMapNodes.Add(new MapNode(this, newNode.NodeIndex));
                coveredNodes += MapNode.MAP_BYTES * 8;
            }
        }

        /// <summary>
        /// Inserts a new record into the tree.
        /// </summary>
        /// <remarks>
        /// <para>Call <see cref="EnsureSpace>"/> before this.</para>
        /// </remarks>
        /// <param name="key">Key of record to insert.</param>
        /// <param name="record">Serialized record (key and data).</param>
        public void Insert(HFS_Record.IKey key, HFS_Record record) {
            // TODO(maybe): if this fails partway through, e.g. because a bad block is
            //   encountered when we allocate a new node for a split, we can leave the
            //   filesystem in a bad state.  Ideally we'd cache all tree nodes and the
            //   in-use bitmap, and write them all at once, with newly-allocated blocks
            //   written first so that failures happen before the existing structure is modified.

            // Get the root node.
            HFS_Node rootNode;
            if (Header.RootNode == 0) {
                // Special case for completely empty tree.
                Debug.Assert(Header.Depth == 0);

                // Alloc root node as a leaf.
                rootNode = AllocNode(HFS_Node.NodeType.Leaf, 1);
                // Save the empty node in case something fails later.
                rootNode.Write();

                Header.Depth = 1;
                Header.RootNode = rootNode.NodeIndex;
                Header.FirstLeaf = rootNode.NodeIndex;
                Header.LastLeaf = rootNode.NodeIndex;
                Header.Write();
            } else {
                // Normal case.
                rootNode = GetNode(Header.RootNode);
            }

            // So long as EnsureSpace() was called, this should not fail unless we encounter
            // a bad block.
            HFS_Record? splitRec = DoInsert(key, record, rootNode);
            if (splitRec != null) {
                // Root node was split.  Create an index record that points at the current root,
                // which will become the leftmost node. (splitRec holds an index record for the
                // new rightmost node.)
                HFS_Record leftRec = HFS_Record.CreateIndexRecord(this,
                    rootNode.GetRecord(0), rootNode.NodeIndex);

                // Alloc a new root index node.
                HFS_Node newRoot = AllocNode(HFS_Node.NodeType.Index, Header.Depth + 1);

                // Update the tree.
                Header.Depth++;
                Header.RootNode = newRoot.NodeIndex;

                // Insert the records into the new root node.
                HFS_Record? split1 = newRoot.InsertRecord(leftRec, -1);
                HFS_Record? split2 = newRoot.InsertRecord(splitRec, 0);
                Debug.Assert(split1 == null && split2 == null);

                newRoot.Write();
            }

            // Update the record count in the header node.
            Header.NumDataRecs++;
            Header.Flush();
        }

        /// <summary>
        /// Recursively insert record.
        /// </summary>
        private HFS_Record? DoInsert(HFS_Record.IKey key, HFS_Record record, HFS_Node node) {
            // h/t: libhfs bt_insert() / insertx()

            if (node.FindRecord(key, out int recNum)) {
                throw new IOException("Record with key already exists: " + key);
            }

            HFS_Record? splitRec;
            switch ((HFS_Node.NodeType)node.Kind) {
                case HFS_Node.NodeType.Leaf:
                    // Found the leaf node.  Insert the record.
                    splitRec = node.InsertRecord(record, recNum);
                    break;
                case HFS_Node.NodeType.Index:
                    // If the new record is to be inserted to the left of the first record,
                    // follow the leftmost pointer.
                    int indexRecNum = recNum;
                    if (indexRecNum < 0) {
                        indexRecNum = 0;
                    }
                    HFS_Record indexRec = node.GetRecord(indexRecNum);
                    uint nodeIndex = indexRec.GetIndexNodeData();
                    HFS_Node childNode = HFS_Node.ReadNode(this, nodeIndex);
                    splitRec = DoInsert(key, record, childNode);

                    // (coming back up)

                    if (recNum < 0) {
                        // Record was inserted to the left of the leftmost key.  Replace the key.
                        HFS_Record newIndexRec = HFS_Record.CreateIndexRecord(this,
                            record, nodeIndex);
                        node.ReplaceRecord(indexRecNum, newIndexRec);
                        if (splitRec == null) {
                            // Not doing anything further in this node, so save it and bail.
                            node.Write();
                            break;
                        }
                    }

                    // If the child node split, insert an index record for the new node on the
                    // right side of the split.  We inserted the new record into a child
                    // identified by recNum, so we want the split index record to be inserted
                    // after recNum.
                    if (splitRec != null) {
                        // This insertion could cause another split.  If so, return the new
                        // index record for the next level up.
                        splitRec = node.InsertRecord(splitRec, recNum);
                    }
                    break;
                default:
                    throw new DAException("Unexpected kind: " + node.Kind);
            }

            return splitRec;
        }

        /// <summary>
        /// Deletes a record from the tree.
        /// </summary>
        /// <param name="key">Key of record to delete.</param>
        /// <param name="okayIfMissing">If false, an exception is thrown if the record
        ///   can't be found.</param>
        /// <exception cref="IOException">Record is missing or structure is damaged.</exception>
        public void Delete(HFS_Record.IKey key, bool okayIfMissing) {
            // Get the root node.
            if (Header.RootNode == 0) {
                // Completely empty tree.
                if (okayIfMissing) {
                    return;
                } else {
                    throw new IOException("Can't delete record from empty tree");
                }
            }
            HFS_Node rootNode = GetNode(Header.RootNode);

            DoDelete(key, rootNode, out bool nodeDeleted, out bool wasFound);
            if (!wasFound) {
                // Record wasn't found, nothing was done.
                if (okayIfMissing) {
                    return;
                } else {
                    throw new IOException("Unable to delete record, because key not found");
                }
            }

            // See if the root node was affected.  If so, update the header.
            if (Header.Depth > 1 && rootNode.NumRecords == 1) {
                // Root is an index node with only one record.  Delete the node and make
                // the child the root.
                HFS_Record rec = rootNode.GetRecord(0);
                uint childIndex = rec.GetIndexNodeData();
                ReleaseNode(rootNode);
                Header.RootNode = childIndex;
                Header.Depth--;
            } else if (Header.Depth == 1 && nodeDeleted) {
                // Root node was deleted.
                Header.Depth = 0;
                Header.RootNode = 0;
            }

            Header.NumDataRecs--;
            Header.Flush();
        }

        /// <summary>
        /// Recursively delete record.
        /// </summary>
        private HFS_Record? DoDelete(HFS_Record.IKey key, HFS_Node node,
                out bool nodeDeleted, out bool wasFound) {
            wasFound = node.FindRecord(key, out int recNum);
            HFS_Record? newIndexRec;

            switch ((HFS_Node.NodeType)node.Kind) {
                case HFS_Node.NodeType.Leaf:
                    // Reached the leaf node.  Delete the record.
                    if (!wasFound) {
                        // Didn't find the record in the leaf node; fail.
                        nodeDeleted = false;
                        return null;
                    }
                    newIndexRec = node.DeleteRecord(recNum, out nodeDeleted);
                    break;
                case HFS_Node.NodeType.Index:
                    if (recNum < 0) {
                        // This means the key is to the left of the leftmost node, which can't
                        // happen if the thing we're searching for is in the tree.
                        nodeDeleted = wasFound = false;
                        return null;
                    }
                    HFS_Record indexRec = node.GetRecord(recNum);
                    uint nodeIndex = indexRec.GetIndexNodeData();
                    HFS_Node childNode = HFS_Node.ReadNode(this, nodeIndex);
                    newIndexRec = DoDelete(key, childNode, out nodeDeleted, out wasFound);

                    // (coming back up)

                    if (nodeDeleted) {
                        // Child node was deleted.  Delete the record that points to it.  If
                        // that causes the current node to be deleted, or its key to be altered,
                        // we want to propagate that up the chain.
                        Debug.Assert(newIndexRec == null);
                        newIndexRec = node.DeleteRecord(recNum, out nodeDeleted);
                    } else {
                        // If the child needs us to change the index record, do so now.
                        if (newIndexRec != null) {
                            node.ReplaceRecord(recNum, newIndexRec);
                            node.Write();
                            newIndexRec = null;
                        }
                        if (recNum == 0) {
                            // The index record for the record we deleted was the leftmost, which
                            // means it's what the parent used to find us.  Pass the update up.
                            newIndexRec = HFS_Record.CreateIndexRecord(this, node.GetRecord(0),
                                node.NodeIndex);
                        }
                    }
                    break;
                default:
                    throw new DAException("Unexpected kind: " + node.Kind);
            }

            return newIndexRec;
        }

        #region Node Alloc

        /// <summary>
        /// Allocates a new tree node from the existing storage.
        /// </summary>
        internal HFS_Node AllocNode(HFS_Node.NodeType type, int height) {
            Debug.Assert(height >= 0 && height <= HFS.MAX_BTREE_HEIGHT);
            if (Header.FreeNodes == 0) {
                throw new DiskFullException("Unable to allocate tree node: no nodes available");
            }

            // Find a place for it.
            uint nodeIndex = FindFreeNode();
            Debug.Assert(nodeIndex != 0);

            // Fill out a new node descriptor.
            byte[] blockBuf = new byte[HFS_Node.NODE_SIZE];
            HFS_Node.InitEmptyNode(blockBuf, type, (byte)height);

            // Create a node object.
            uint logiBlock = LogiBlockFromNodeIndex(nodeIndex);
            HFS_Node newNode = HFS_Node.CreateNode(this, nodeIndex, blockBuf);

            // Mark the node as in use, and decrement the free node count.
            Debug.Assert(!IsNodeInUse(nodeIndex));
            SetNodeInUse(nodeIndex, true);
            Header.FreeNodes--;
            return newNode;
        }

        /// <summary>
        /// Releases a tree node.
        /// </summary>
        internal void ReleaseNode(HFS_Node node) {
            // If the header reference this, update the pointers there.
            if (Header.FirstLeaf == node.NodeIndex) {
                Header.FirstLeaf = node.FLink;
            }
            if (Header.LastLeaf == node.NodeIndex) {
                Header.LastLeaf = node.BLink;
            }

            // Update the links in the nodes to the left and right.
            if (node.FLink != 0) {
                HFS_Node sib = GetNode(node.FLink);
                Debug.Assert(sib.BLink == node.NodeIndex);
                sib.BLink = node.BLink;
                sib.Write();
            }
            if (node.BLink != 0) {
                HFS_Node sib = GetNode(node.BLink);
                Debug.Assert(sib.FLink == node.NodeIndex);
                sib.FLink = node.FLink;
                sib.Write();
            }

            // Update the node in-use bitmap.
            Debug.Assert(IsNodeInUse(node.NodeIndex));
            SetNodeInUse(node.NodeIndex, false);
            Header.FreeNodes++;
        }

        /// <summary>
        /// Finds the first available slot for a new node.
        /// </summary>
        /// <returns>Node index.</returns>
        /// <exception cref="DiskFullException">Unable to find a free node.</exception>
        private uint FindFreeNode() {
            int start = 0;

            // TODO: could optimize this by keeping track of the last successful alloc / free
            HFS_Record hdrMap = Header.GetMapRecord();
            int num = FindFirstZero(hdrMap.DataBuf, hdrMap.DataOffset, hdrMap.DataLength);
            if (num >= 0) {
                return (uint)num;
            }
            start += hdrMap.DataLength * 8;

            foreach (MapNode mapNd in mMapNodes) {
                hdrMap = mapNd.GetRecord();
                num = FindFirstZero(hdrMap.DataBuf, hdrMap.DataOffset, hdrMap.DataLength);
                if (num >= 0) {
                    return (uint)(start + num);
                }
                start += hdrMap.DataLength * 8;
            }
            Debug.Assert(false, "Should not be here; is bthFree out of sync?");
            throw new DiskFullException("Tree node allocator failed");
        }

        /// <summary>
        /// Returns true if the node is in use.
        /// </summary>
        private bool IsNodeInUse(uint nodeIndex) {
            Debug.Assert(nodeIndex < Header.NumNodes);
            int byteNum = (int)(nodeIndex >> 3);
            int bitNum = (int)(nodeIndex & 0x07);

            // Is this covered by the header?
            if (byteNum < HeaderNode.MAP_REC_LEN) {
                HFS_Record hdrMap = Header.GetMapRecord();
                return GetInUseBit(hdrMap.DataBuf, hdrMap.DataOffset + byteNum, bitNum);
            }
            byteNum -= HeaderNode.MAP_REC_LEN;

            // Find the appropriate map node.
            int mapNum = byteNum / MapNode.MAP_BYTES;
            byteNum = byteNum % MapNode.MAP_BYTES;
            MapNode mapNd = mMapNodes[mapNum];
            HFS_Record mapRec = mapNd.GetRecord();
            Debug.Assert(mapRec.DataLength == MapNode.MAP_BYTES);

            Debug.Assert(byteNum < mapRec.DataLength);
            return GetInUseBit(mapRec.DataBuf, mapRec.DataOffset + byteNum, bitNum);
        }

        /// <summary>
        /// Sets the node's in-use state.
        /// </summary>
        private void SetNodeInUse(uint nodeIndex, bool inUse) {
            Debug.Assert(nodeIndex < Header.NumNodes);
            int byteNum = (int)(nodeIndex >> 3);
            int bitNum = (int)(nodeIndex & 0x07);

            // Start with the chunk in the header node.
            if (byteNum < HeaderNode.MAP_REC_LEN) {
                HFS_Record hdrMap = Header.GetMapRecord();
                SetInUseBit(hdrMap.DataBuf, hdrMap.DataOffset + byteNum, bitNum, inUse);
                Header.SetDirty();
                return;
            }
            byteNum -= HeaderNode.MAP_REC_LEN;

            // Find the appropriate map node.
            int mapNum = byteNum / MapNode.MAP_BYTES;
            byteNum = byteNum % MapNode.MAP_BYTES;
            MapNode mapNd = mMapNodes[mapNum];
            HFS_Record mapRec = mapNd.GetRecord();
            Debug.Assert(mapRec.DataLength == MapNode.MAP_BYTES);

            Debug.Assert(byteNum < mapRec.DataLength);
            SetInUseBit(mapRec.DataBuf, mapRec.DataOffset + byteNum, bitNum, inUse);
            mapNd.SetDirty();
        }

        /// <summary>
        /// Returns the state of the Nth bit.
        /// </summary>
        private bool GetInUseBit(byte[] data, int byteNum, int bitNum) {
            byte val = data[byteNum];
            return (val & (0x80 >> bitNum)) != 0;
        }

        /// <summary>
        /// Sets the state of the Nth bit.
        /// </summary>
        private void SetInUseBit(byte[] data, int offset, int bitNum, bool inUse) {
            byte val = data[offset];
            if (inUse) {
                val |= (byte)(0x80 >> bitNum);
            } else {
                val &= (byte)~(0x80 >> bitNum);
            }
            data[offset] = val;
        }

        /// <summary>
        /// Finds the first zero bit in the buffer.
        /// </summary>
        /// <param name="data">Data buffer.</param>
        /// <param name="startOffset">Byte offset of start of bitmap.</param>
        /// <param name="length">Length of the bitmap, in bytes.</param>
        /// <returns>Bit index of the first zero bit found.  Bit 0 is the high bit of the first
        ///   byte, bit 7 is the low bit.  Returns -1 if no zero bit was found.</returns>
        public static int FindFirstZero(byte[] data, int startOffset, int length) {
            for (int i = 0; i < length; i++) {
                int val = data[startOffset + i];
                if (val != 0xff) {
                    int bit = 0;
                    while (true) {
                        if ((val & 0x80) == 0) {
                            return i * 8 + bit;
                        }
                        val <<= 1;
                        bit++;
                    }
                }
            }
            return -1;
        }

        #endregion Node Alloc

        /// <summary>
        /// Flushes any pending updates to the header node and map nodes.
        /// </summary>
        /// <remarks>
        /// Index and leaf nodes are written immediately when modified.
        /// </remarks>
        public void Flush() {
            Header.Flush();
            foreach (MapNode mapn in mMapNodes) {
                mapn.Flush();
            }
        }

        public override string ToString() {
            return "[BTree: type=" + TreeType + " hdr:" + Header + "]";
        }

        /// <summary>
        /// Calculates the logical block number from a node index.
        /// </summary>
        /// <param name="index">Node index.</param>
        /// <returns>Logical block number.</returns>
        /// <exception cref="IOException">Node index is not contained within tree.</exception>
        internal uint LogiBlockFromNodeIndex(uint index) {
            uint abIndex = index / mLogiPerAllocBlock;
            uint abOffset = index % mLogiPerAllocBlock;

            // Convert index into an absolute allocation block number.
            uint ablk = mFileStorage.GetAllocBlockNum(abIndex);

            // Convert it to a logical block number.
            uint logiBlock = mFileSystem.AllocBlockToLogiBlock(ablk);
            return logiBlock + abOffset;
        }
    }
}
