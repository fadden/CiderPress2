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
using System.Text;

using CommonUtil;
using static DiskArc.Defs;
using static DiskArc.FS.HFS_Struct;

namespace DiskArc.FS {
    /// <summary>
    /// For debugging, generate a formatted listing of an HFS volume's structure.
    /// </summary>
    public static class HFS_Dump {
        /// <summary>
        /// Generates formatted listing, as a multi-line string.
        /// </summary>
        /// <param name="fs">Filesystem object.</param>
        /// <returns>String with formatted output.</returns>
        public static string Dump(HFS fs) {
            StringBuilder sb = new StringBuilder(10240);

            fs.PrepareFileAccess(true);

            HFS_MDB? mdb = fs.VolMDB;
            Debug.Assert(mdb != null);

            sb.AppendLine("*** MDB ***");
            sb.AppendLine("Volume Name        : '" + mdb.VolumeName + "'");
            sb.AppendLine("Created            : " + mdb.CreateDate);
            sb.AppendLine("Last Modified      : " + mdb.ModifyDate);
            sb.AppendLine("Attrib Flags       : " + mdb.Attributes.ToString("x4") +
                ((mdb.Attributes & (ushort)HFS_MDB.AttribFlags.WasUnmounted) == 0 ?
                    " [needs check]" : ""));
            sb.AppendLine("Volume Write Count : " + mdb.WriteCount);

            sb.AppendLine("Files in Root      : " + mdb.RootFileCount);
            sb.AppendLine("Dirs in Root       : " + mdb.RootDirCount);
            sb.AppendLine("Total Files        : " + mdb.FileCount);
            sb.AppendLine("Total Dirs         : " + mdb.DirCount);
            sb.AppendLine("Next CNID          : " + mdb.NextCatalogID);
            sb.AppendLine("Blessed system CNID: " + mdb.FndrInfo[0]);

            sb.AppendLine("Vol Bitmap Start   : " + mdb.VolBitmapStart);
            sb.AppendLine("Free Blocks        : " + mdb.FreeBlocks);
            sb.AppendLine("Num Alloc Blocks   : " + mdb.TotalBlocks);
            sb.AppendLine("Alloc Block Size   : " + mdb.BlockSize +
                " (" + (mdb.BlockSize / BLOCK_SIZE) + " logical blocks)");
            sb.AppendLine("Alloc Block Start  : " + mdb.AllocBlockStart);
            sb.AppendLine("Next Alloc Search  : " + mdb.NextAllocation);
            sb.AppendLine("Clump Size         : " + mdb.ClumpSize +
                " (" + (mdb.ClumpSize / BLOCK_SIZE) + " logical blocks)");

            sb.AppendLine("Extents File Size  : " + mdb.ExtentsFileSize);
            sb.AppendLine("Extents Clump Size : " + mdb.ExtentsClumpSize);
            sb.AppendLine("Extents File Exts  : " + mdb.ExtentsFileRec);
            sb.AppendLine("Catalog File Size  : " + mdb.CatalogFileSize);
            sb.AppendLine("Catalog Clump Size : " + mdb.CatalogClumpSize);
            sb.AppendLine("Catalog File Exts  : " + mdb.CatalogFileRec);

            // Explore extents overflow file.
            sb.AppendLine();
            sb.AppendLine("*** Extents Overflow File ***");
            if (mdb.ExtentsFile != null) {
                DumpBTree(sb, mdb.ExtentsFile);
            }

            // Explore catalog file.
            sb.AppendLine();
            sb.AppendLine("*** Catalog File ***");
            if (mdb.CatalogFile != null) {
                DumpBTree(sb, mdb.CatalogFile);
            }

            return sb.ToString();
        }

        private static void DumpBTree(StringBuilder sb, HFS_BTree btree) {
            DumpHeaderNode(sb, btree);
            sb.AppendLine("-- Tree --");

            HFS_BTree.HeaderNode hdrNode = btree.Header;
            ushort height = hdrNode.Depth;
            uint nodeIndex = hdrNode.RootNode;
            for (int level = 0; level < height; level++) {
                HFS_Node node = btree.GetNode(nodeIndex);
                switch ((HFS_Node.NodeType)node.Kind) {
                    case HFS_Node.NodeType.Index:
                        // Dump this level.
                        DumpIndexNodes(sb, btree, nodeIndex, level);

                        // Find the leftmost node on the next level.
                        nodeIndex = GetFirstChildNodeIndex(node);
                        break;
                    case HFS_Node.NodeType.Leaf:
                        DumpLeafNodes(sb, btree, nodeIndex);
                        break;
                    case HFS_Node.NodeType.Map:
                    case HFS_Node.NodeType.Header:
                    default:
                        // Unexpected.
                        sb.AppendLine("Unexpected node type " + node.Kind +
                            " at tree level " + level);
                        break;
                }
            }
        }

        private static void DumpHeaderNode(StringBuilder sb, HFS_BTree btree) {
            HFS_BTree.HeaderNode hnode = btree.Header;
            sb.AppendLine("-- Header Node --");
            // The header node descriptor should have type 1 (header), level/height 0, 3 records.
            // The forward link will be zero unless a map node has been allocated.
            DumpNodeDescriptor(sb, hnode.Node, string.Empty);
            DumpHeaderRec(sb, hnode);

            // No value in dumping 128-byte empty area.

            uint nodeIndex = hnode.Node.FLink;
            while (nodeIndex != 0) {
                HFS_Node node = btree.GetNode(nodeIndex);
                sb.AppendLine("  Map node " + nodeIndex + ": len=" +
                    node.GetRecord(0).RecordLength);
                nodeIndex = node.FLink;
            }
        }

        private static void DumpNodeDescriptor(StringBuilder sb, HFS_Node node, string pfx) {
            sb.AppendLine(pfx + "- Node Descriptor");
            sb.AppendLine(pfx + "  Forward Link  : " + node.FLink);
            sb.AppendLine(pfx + "  Backward Link : " + node.BLink);
            string typeStr;
            switch ((HFS_Node.NodeType)node.Kind) {
                case HFS_Node.NodeType.Index: typeStr = "index"; break;
                case HFS_Node.NodeType.Header: typeStr = "header"; break;
                case HFS_Node.NodeType.Map: typeStr = "map"; break;
                case HFS_Node.NodeType.Leaf: typeStr = "leaf"; break;
                default: typeStr = "UNKNOWN"; break;
            }
            sb.AppendLine(pfx + "  Type          : " + node.Kind + " (" + typeStr + ")");
            sb.AppendLine(pfx + "  Level         : " + node.Height);
            sb.AppendLine(pfx + "  Record Count  : " + node.NumRecords);
        }

        private static void DumpHeaderRec(StringBuilder sb, HFS_BTree.HeaderNode hnode) {
            sb.AppendLine("- Header Record");
            sb.AppendLine("  Depth         : " + hnode.Depth);
            sb.AppendLine("  Root          : " + hnode.RootNode);
            sb.AppendLine("  Data Records  : " + hnode.NumDataRecs);
            sb.AppendLine("  First Leaf    : " + hnode.FirstLeaf);
            sb.AppendLine("  Last Leaf     : " + hnode.LastLeaf);
            sb.AppendLine("  Node Size     : " + hnode.NodeSize);
            sb.AppendLine("  Max Key Len   : " + hnode.MaxKeyLen);
            sb.AppendLine("  Total Nodes   : " + hnode.NumNodes);
            sb.AppendLine("  Free Nodes    : " + hnode.FreeNodes);
        }

        private static void DumpIndexNodes(StringBuilder sb, HFS_BTree btree, uint nodeIndex,
                int level) {
            uint maxIterations = btree.Header.NumNodes;

            sb.AppendLine("Index nodes at level " + level + ":");
            int count = 0;
            while (nodeIndex != 0) {
                HFS_Node node = btree.GetNode(nodeIndex);
                DumpIndexNode(sb, node);

                nodeIndex = node.FLink;
                if (++count > maxIterations) {
                    Debug.Assert(false);
                    throw new IOException("Cycle in index node links");
                }
            }
            sb.Append(" (num=" + count + ")");
            sb.AppendLine();
        }

        private static void DumpIndexNode(StringBuilder sb, HFS_Node node) {
            sb.AppendLine("  Index node " + node.NodeIndex + ":");
            DumpNodeDescriptor(sb, node, "    ");
            for (int i = 0; i < node.NumRecords; i++) {
                sb.Append("    ");
                sb.Append(i.ToString("d2"));
                sb.Append(": ");
                sb.AppendLine(node.GetRecord(i).ToString());
            }
        }

        private static void DumpLeafNodes(StringBuilder sb, HFS_BTree btree, uint nodeIndex) {
            uint maxIterations = btree.Header.NumNodes;

            sb.AppendLine("Leaf nodes:");
            int count = 0;
            while (nodeIndex != 0) {
                sb.AppendFormat("  {0,5}: ", nodeIndex);
                HFS_Node node = btree.GetNode(nodeIndex);

                if (btree.TreeType == HFS_BTree.Type.Extents) {
                    DumpExtentNode(sb, node);
                } else {
                    DumpCatalogNode(sb, node);
                }
                sb.AppendLine();

                nodeIndex = node.FLink;
                if (++count > maxIterations) {
                    Debug.Assert(false);
                    throw new IOException("Cycle in leaf node links");
                }
            }
            sb.Append(" (num=" + count + ")");
            sb.AppendLine();
        }

        private static void DumpCatalogNode(StringBuilder sb, HFS_Node node) {
            Debug.Assert(node.Kind == (byte)HFS_Node.NodeType.Leaf);

            //byte[] testName = { 3, (byte)'E', (byte)'m', (byte)'q' };
            //HFS_Record.CatKey testKey = new HFS_Record.CatKey(0x0002, testName, 0);

            bool first = true;
            foreach (HFS_Record rec in node) {
                HFS_Record.CatKey key = rec.UnpackCatKey();
                string name = MacChar.MacStrToUnicode(key.Data, key.NameOffset,
                        MacChar.Encoding.RomanShowCtrl);

                if (!first) {
                    sb.Append(", ");
                } else {
                    first = false;
                }

                HFS_Record.CatDataType recType = rec.GetCatDataType();
                switch (recType) {
                    case HFS_Record.CatDataType.Directory:
                        CatDataDirRec dirRec = rec.ParseCatDataDirRec();
                        sb.AppendFormat("dir #{0:x4}/'{1}' [#{2:x4}]",
                            key.ParentID, name, dirRec.dirDirID);
                        break;
                    case HFS_Record.CatDataType.File:
                        CatDataFilRec filRec = rec.ParseCatDataFilRec();
                        sb.AppendFormat("file #{0:x4}/'{1}' [#{2:x4}]",
                            key.ParentID, name, filRec.filFlNum);
                        break;
                    case HFS_Record.CatDataType.DirectoryThread:
                        CatDataThreadRec thdRec = rec.ParseCatDataThreadRec();
                        sb.AppendFormat("dir-thread #{0:x4}/'{1}' [#{2:x4}/{3}]",
                            key.ParentID, name, thdRec.thdParID,
                            MacChar.MacToUnicode(thdRec.thdCName, 1, thdRec.thdCName[0],
                                MacChar.Encoding.RomanShowCtrl));
                        //sb.Append(" [keyLen=" + rec.KeyLength + " dataLen=" + rec.DataLength + "]");
                        break;
                    case HFS_Record.CatDataType.FileThread:
                        CatDataThreadRec fthdRec = rec.ParseCatDataThreadRec();
                        sb.AppendFormat("file-thread #{0:x4}/'{1}' [#{2:x4}/{3}]",
                            key.ParentID, name, fthdRec.thdParID,
                            MacChar.MacToUnicode(fthdRec.thdCName, 1, fthdRec.thdCName[0],
                                MacChar.Encoding.RomanShowCtrl));
                        break;
                    default:
                        sb.Append("UNKNOWN:" + recType);
                        break;
                }

                //int cmp = testKey.Compare(rec);
                //sb.Append("<" + cmp + ">");
            }
        }

        private static void DumpExtentNode(StringBuilder sb, HFS_Node node) {
            Debug.Assert(node.Kind == (byte)HFS_Node.NodeType.Leaf);
            bool first = true;
            foreach (HFS_Record rec in node) {
                if (!first) {
                    sb.Append(", ");
                } else {
                    first = false;
                }

                HFS_Record.ExtKey key = rec.UnpackExtKey();
                string forkStr;
                if (key.Fork == (byte)HFS_Record.ForkType.Data) {
                    forkStr = "data";
                } else if (key.Fork == (byte)HFS_Record.ForkType.Rsrc) {
                    forkStr = "rsrc";
                } else {
                    forkStr = "????";
                }
                ExtDataRec extRec = rec.ParseExtDataRec();
                sb.AppendFormat("{0}-#{1:x4}-{2}: {3}",
                    forkStr, key.FileCNID, key.IndexOfFirst, extRec);
            }
        }

        /// <summary>
        /// Gets the index of the leftmost child of the given node.
        /// </summary>
        /// <param name="node">Node to examine.</param>
        /// <returns>Child node index.</returns>
        /// <exception cref="IOException">Inappropriate node type.</exception>
        internal static uint GetFirstChildNodeIndex(HFS_Node node) {
            if (node.Kind != (byte)HFS_Node.NodeType.Index) {
                // Headers, maps, and leaves have siblings but no children.
                Debug.Assert(false);        // shouldn't be calling this for anything but index
                throw new IOException("Can't get child of node with type " + node.Kind);
            }
            if (node.NumRecords == 0) {
                Debug.Assert(false);        // shouldn't be calling this for empty node
                throw new IOException("Can't get child of empty node");
            }
            // Get the first non-empty record.  (IM:F refers to deleted records as having a key
            // length of zero, but as far as I can tell records are expected to be stored
            // compactly, with no gaps.)
            HFS_Record rec = node.GetRecord(0);
            return rec.GetIndexNodeData();
        }
    }
}
