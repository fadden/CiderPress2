/*
 * Copyright 2026 faddenSoft
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
using System.IO.Compression;

using CommonUtil;

namespace DiskArc.Comp {
    /// <summary>
    /// Streaming implementation of StuffIt's Huffman compression (algorithm 3).
    /// </summary>
    /// <remarks>
    /// <para>It might be more accurate to refer to this as "PackIt's Huffman compression",
    /// but StuffIt is more widely known.</para>
    /// <para>The code here is intended to replicate the behavior of PackIt's implementation,
    /// producing identical output for all inputs, and so mimics the original PackIt code as
    /// closely as possible - at the expense of performance.</para>
    /// <para>The original was written by Harry R. Chesley.</para>
    /// </remarks>
    public class StuffHuffStream : Stream {
        // Stream characteristics.
        public override bool CanRead {
            get { return mCompressionMode == CompressionMode.Decompress; }
        }
        public override bool CanWrite {
            get { return mCompressionMode == CompressionMode.Compress; }
        }
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        /// <summary>
        /// Compressed data is read from or written to this stream.
        /// </summary>
        private Stream mCompDataStream;

        /// <summary>
        /// Leave the compressed data stream open when we are disposed?
        /// </summary>
        private bool mLeaveOpen;

        /// <summary>
        /// To compress or not to compress?
        /// </summary>
        private CompressionMode mCompressionMode;

        private long mExpOutputRemaining;           // uncompressed data length, when expanding


        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="compDataStream">Stream that holds compressed data.  Must be positioned
        ///   at the start.  May be part of a larger stream (i.e. file archive).</param>
        /// <param name="mode">Flag that indicates whether we will be compressing or
        ///   expanding data.</param>
        /// <param name="leaveOpen">Flag that determines whether we close the compressed
        ///   data stream when we are disposed.</param>
        /// <param name="compDataStreamLen">Decompression: length of compressed data.</param>
        public StuffHuffStream(Stream compDataStream, CompressionMode mode, bool leaveOpen,
                long uncompDataStreamLen) {
            mCompDataStream = compDataStream;
            mCompressionMode = mode;
            mLeaveOpen = leaveOpen;
            mExpOutputRemaining = uncompDataStreamLen;

            if (mode == CompressionMode.Compress) {
                Debug.Assert(compDataStream.CanWrite);
                InitCompress();
            } else if (mode == CompressionMode.Decompress) {
                Debug.Assert(compDataStream.CanRead);
                if (uncompDataStreamLen < 0) {
                    throw new ArgumentException("Invalid value for " + nameof(uncompDataStreamLen));
                }
                InitExpand();
            } else {
                throw new ArgumentException("Invalid mode: " + mode);
            }
        }

        // IDisposable, from Stream
        protected override void Dispose(bool disposing) {
            if (disposing && mCompDataStream != null) {
                if (mCompressionMode == CompressionMode.Compress) {
                    FinishCompression();
                }

                if (!mLeaveOpen) {
                    mCompDataStream.Close();
                }

#pragma warning disable CS8625
                mCompDataStream = null;
#pragma warning restore CS8625
            }
            base.Dispose(disposing);
        }

        // Single-byte buffer for ReadByte/WriteByte.
        private byte[] mSingleBuf = new byte[1];

        // Stream
        public override int ReadByte() {
            if (Read(mSingleBuf, 0, 1) == 0) {
                return -1;      // EOF reached
            }
            return mSingleBuf[0];
        }

        // Stream
        public override void WriteByte(byte value) {
            mSingleBuf[0] = value;
            Write(mSingleBuf, 0, 1);
        }

        // Stream
        public override void Flush() {
            throw new NotSupportedException();
        }

        // Stream
        public override long Seek(long offset, SeekOrigin origin) {
            throw new NotSupportedException();
        }

        // Stream
        public override void SetLength(long value) {
            throw new NotSupportedException();
        }

        #region Common

        private class TreeNode {
            public TreeNode? Parent { get; set; }   // link to parent, or null at top
            public bool Terminal { get; set; }      // true if leaf
            public bool Marked { get; set; }        // used during compression
            public int Count { get; set; }          // number of times this value appears in input
            public byte Value { get; set; }         // for terminal (leaf) nodes: symbol value
            public TreeNode? Right { get; set; }    // right branch for non-terminal nodes
            public TreeNode? Left { get; set; }     // left branch for non-terminal nodes

            public TreeNode() { }

            /// <summary>
            /// Need at most 256 for leaves and 255 for internal nodes.
            /// </summary>
            public const int MAX_TREE_NODES = 511;

            public static TreeNode[] CreateTree() {
                TreeNode[] tree = new TreeNode[MAX_TREE_NODES];
                for (int i = 0; i < MAX_TREE_NODES; i++) {
                    tree[i] = new TreeNode();
                }
                return tree;
            }

            public override string ToString() {
                if (Terminal) {
                    return "[TreeNode leaf value=$" + Value.ToString("x2") + "]";
                } else {
                    return "[TreeNode internal left=" +
                        (Left == null ? "null" : Left.Terminal ? "leaf" : "internal") +
                        " right=" +
                        (Right == null ? "null" : Right.Terminal ? "leaf" : "internal") + "]";
                }
            }
        }

        /// <summary>
        /// Pool of nodes that make up the tree.  Values are initialized as the nodes are
        /// added to the tree.
        /// </summary>
        private TreeNode[] mTree = TreeNode.CreateTree();

        /// <summary>
        /// Index of next free node.
        /// </summary>
        private int mNextNode;

        /// <summary>
        /// Mapping of byte values to tree nodes.
        /// </summary>
        private TreeNode?[] mValToTree = new TreeNode[256];

        /// <summary>
        /// Node at the top of the tree.
        /// </summary>
        private TreeNode mTreeTop = new TreeNode();

        /// <summary>
        /// Initialize the tree.
        /// </summary>
        private void HuffInit() {
            mNextNode = 0;
            for (int i = 0; i < mValToTree.Length; i++) {
                mValToTree[i] = null;
            }
        }

        #endregion Common

        #region Compress

        private MemoryStream? mCompCacheStream;
        private byte[] mCompData = RawData.EMPTY_BYTE_ARRAY;
        private int mCompLen;

        private int mCompBitCount;      // bsCnt - number of bits free in current byte
        private byte mCompByteVal;      // *bsPtr - current byte in bitstream


        /// <summary>
        /// Prepares object for compression.
        /// </summary>
        private void InitCompress() {
            HuffInit();
            mCompCacheStream = new MemoryStream();
            mCompBitCount = 8;
        }

        /// <summary>
        /// Receives uncompressed data to be compressed.  (Stream call.)
        /// </summary>
        public override void Write(byte[] buffer, int offset, int count) {
            // Copy all data to our internal cache.
            mCompCacheStream!.Write(buffer, offset, count);
        }

        /// <summary>
        /// Finishes compression operation.  This method is called when this object is closed,
        /// indicating that all data has been provided.
        /// </summary>
        private void FinishCompression() {
            mCompData = mCompCacheStream!.GetBuffer();
            mCompLen = (int)mCompCacheStream!.Length;

            if (mCompLen == 0) {
                return;
            }

            // Perform the frequency analysis.
            HuffCount();
            // Generate the Huffman encoding tree.
            HuffCode();
            // Output the encoding table.
            HuffBTable();
            // Output the encoded data.
            HuffBData();

            if (mCompBitCount != 8) {
                // Output final partial byte.
                mCompDataStream.WriteByte(mCompByteVal);
            }
        }

        /// <summary>
        /// Count the number of times each byte value appears.  For bytes already in the tree,
        /// increment the count.  For those we haven't seen yet, allocate a tree node.
        /// </summary>
        private void HuffCount() {
            for (int i = 0; i < mCompLen; i++) {
                byte val = mCompData[i];
                TreeNode? treePtr = mValToTree[val];
                if (treePtr == null) {
                    mValToTree[val] = treePtr = mTree[mNextNode];
                    treePtr.Terminal = true;
                    treePtr.Marked = false;
                    treePtr.Count = 1;
                    treePtr.Value = val;
                    mNextNode++;
                } else {
                    treePtr.Count++;
                }
            }
        }

        /// <summary>
        /// Takes the list of terminal nodes and builds a binary tree from them.  We repeatedly
        /// find the two lowest-count nodes and combine them, until there's only one uncombined
        /// node left.
        /// </summary>
        private void HuffCode() {
            TreeNode? treePtr, n1, n2;

            // Handle special case of single-symbol input by synthesizing a second value.
            if (mNextNode == 1) {
                treePtr = mTree[1];
                treePtr.Terminal = true;
                treePtr.Marked = false;
                treePtr.Count = 0;
                treePtr.Value = (byte)(mTree[0].Value + 1);
                mNextNode++;
            }

            while (true) {
                n1 = n2 = null;
                for (int i = 0; i < mNextNode; i++) {
                    treePtr = mTree[i];
                    if (treePtr.Marked) {
                        continue;
                    }
                    if (n1 == null) {
                        if (n2 == null) {
                            n2 = treePtr;
                        } else if (n2.Count <= treePtr.Count) {
                            n1 = treePtr;
                        } else {
                            n1 = n2;
                            n2 = treePtr;
                        }
                    } else if (n1.Count > treePtr.Count) {
                        if (n2!.Count > treePtr.Count) {
                            n1 = n2;
                            n2 = treePtr;
                        } else {
                            n1 = treePtr;
                        }
                    }
                }
                if (n1 == null) {
                    Debug.Assert(n2 != null);
                    mTreeTop = n2;
                    return;
                }
                n1.Marked = n2!.Marked = true;
                n1.Parent = n2.Parent = mTree[mNextNode];
                mTree[mNextNode].Parent = null;
                mTree[mNextNode].Terminal = mTree[mNextNode].Marked = false;
                mTree[mNextNode].Count = n1.Count + n2.Count;
                mTree[mNextNode].Left = n1;
                mTree[mNextNode].Right = n2;
                mNextNode++;
            }
        }

        /// <summary>
        /// Serializes the encoding tree into the bit stream.  The table will be at most 320
        /// bytes long.
        /// </summary>
        private void HuffBTable() {
            BitTrav(mTreeTop);
        }
        private void BitTrav(TreeNode treePtr) {
            if (treePtr.Terminal) {
                BitEncode(1);
                ByteEncode(treePtr.Value);
            } else {
                Debug.Assert(treePtr.Left != null && treePtr.Right != null);
                BitEncode(0);
                BitTrav(treePtr.Left!);
                BitTrav(treePtr.Right!);
            }
        }

        /// <summary>
        /// Outputs one Huffman code for each byte of input.
        /// </summary>
        private void HuffBData() {
            for (int i = 0; i < mCompLen; i++) {
                TreeNode? leafPtr = mValToTree[mCompData[i]];
                Debug.Assert(leafPtr != null);
                DataTrav(leafPtr);
            }
        }
        private void DataTrav(TreeNode treePtr) {
            // Recursively walk up the tree to the root, then output codes on the way back down.
            TreeNode? pPtr = treePtr.Parent;
            if (pPtr != null) {
                DataTrav(pPtr);
                if (pPtr.Left == treePtr) {
                    BitEncode(0);
                } else {
                    BitEncode(1);
                }
            }
        }

        /// <summary>
        /// Outputs a single bit.  The low bit of the argument is used.
        /// </summary>
        private void BitEncode(int val) {
            if (mCompBitCount == 0) {
                mCompDataStream.WriteByte(mCompByteVal);
                mCompByteVal = 0;
                mCompBitCount = 8;
            }
            mCompByteVal |= (byte)((val & 0x01) << (--mCompBitCount));
        }

        private void ByteEncode(byte val) {
            BitEncode(val >> 7);
            BitEncode(val >> 6);
            BitEncode(val >> 5);
            BitEncode(val >> 4);
            BitEncode(val >> 3);
            BitEncode(val >> 2);
            BitEncode(val >> 1);
            BitEncode(val);
        }

        #endregion Compress

        #region Expand

        private bool mExpHeaderRead;    // true if encoding table has been read
        private int mExpBitCount;       // bsCnt - number of bits left in current byte
        private byte mExpByteVal;       // *bsPtr - current byte in bitstream

        /// <summary>
        /// Prepares object for decompression.
        /// </summary>
        private void InitExpand() {
            HuffInit();
            mExpHeaderRead = false;
            mExpBitCount = 0;
        }

        /// <summary>
        /// Reads uncompressed data.  (Stream call.)
        /// </summary>
        public override int Read(byte[] buffer, int offset, int count) {
            if (mExpOutputRemaining == 0) {
                // This handles the special case of zero-length input to the compressor.
                return 0;
            }
            if (!mExpHeaderRead) {
                HuffDTable();
                mExpHeaderRead = true;
            }

            if (count > mExpOutputRemaining) {
                count = (int)mExpOutputRemaining;
            }

            HuffDData(buffer, offset, count);

            mExpOutputRemaining -= count;
            return count;
        }

        /// <summary>
        /// Decodes the Huffman encoding table.
        /// </summary>
        private void HuffDTable() {
            mTreeTop = TravDTab();
        }
        private TreeNode TravDTab() {
            TreeNode tPtr = mTree[mNextNode++];
            if (BitDecode() == 1) {
                tPtr.Terminal = true;
                tPtr.Value = ByteDecode();
            } else {
                tPtr.Terminal = false;
                tPtr.Left = TravDTab();
                tPtr.Right = TravDTab();
            }
            return tPtr;
        }

        /// <summary>
        /// Decodes the Huffman data stream.
        /// </summary>
        /// <exception cref="InvalidDataException">Invalid code sequence found.</exception>
        private int HuffDData(byte[] buffer, int offset, int count) {
            int actual = count;
            while (count-- > 0) {
                TreeNode? tPtr = mTreeTop;
                while (!tPtr.Terminal) {
                    if (BitDecode() == 0) {
                        tPtr = tPtr.Left;
                    } else {
                        tPtr = tPtr.Right;
                    }
                    if (tPtr == null) {
                        throw new InvalidDataException("invalid code encountered");
                    }
                }
                buffer[offset++] = tPtr.Value;
            }
            return actual;
        }

        /// <summary>
        /// Returns the next bit from the compressed input, as 0 or 1.
        /// </summary>
        private int BitDecode() {
            if (mExpBitCount == 0) {
                int val = mCompDataStream.ReadByte();
                if (val == -1) {
                    throw new InvalidDataException("ran out of input");
                }
                mExpByteVal = (byte)val;
                mExpBitCount = 8;
            }
            return (mExpByteVal >> (--mExpBitCount)) & 0x01;
        }

        /// <summary>
        /// Returns the next 8-bit byte from the compressed input.
        /// </summary>
        private byte ByteDecode() {
            int res;
            res = BitDecode() << 7;
            res |= BitDecode() << 6;
            res |= BitDecode() << 5;
            res |= BitDecode() << 4;
            res |= BitDecode() << 3;
            res |= BitDecode() << 2;
            res |= BitDecode() << 1;
            res |= BitDecode();
            return (byte)res;
        }

        #endregion Expand
    }
}
