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
using System.IO.Compression;

using CommonUtil;

namespace DiskArc.Comp {
    /// <summary>
    /// "Squeeze" (RLE + semi-adaptive Huffman) compression.
    /// </summary>
    /// <remarks>
    /// <para>This mimics as closely as possible the original squeeze/unsqueeze tools,
    /// developed by Richard Greenlaw.  There are more elegant and efficient ways to do
    /// this, but I wanted the output to be an exact match.</para>
    /// <para>Compression requires two passes, so streaming the input is impossible.</para>
    /// <para>The full file header is optional.  When included, a checksum will be included.
    /// During expansion, the check happens when EOF is encountered, so a program that exactly
    /// fills an output buffer and then stops won't trigger the test.</para>
    /// </remarks>
    public class SqueezeStream : Stream {
        public const int MIN_FILE_HEADER_LEN = 5;           // magic, checksum, empty filename
        public const int MAX_FILE_HEADER_LEN = 4 + 256;     // arbitrary; 255-char filename limit
        public const int MIN_FULL_HEADER_LEN = 5 + 2;       // empty filename, zero nodes

        // Various constants defined by the format.
        public const byte MAGIC0 = 0x76;                    // first byte of file header
        public const byte MAGIC1 = 0xff;                    // second byte of file header
        private const byte RLE_DELIM = 0x90;                // value used to indicate run of bytes
        private const int EOF_TOKEN = 256;                  // end-of-file symbol
        private const int NUM_VALS = 257;                   // number of possible input symbols
        private const int NUM_NODES = NUM_VALS + NUM_VALS - 1;  // nodes in encoding tree
        //private const int ERROR_TOKEN = 1024;               // internal value, used to signal error

        // Buffer for holding header data as bytes are fed in or out.
        //
        // The maximum length of the header isn't defined.  If the filename is 255 chars and
        // the input has all 256 possible values, we'd need 2+2+256+2+(257*4)=1290 bytes.  A
        // buffer that holds 2048 should be plenty.
        private const int BUFFER_SIZE = 2048;


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
        /// If true, read/write the full file header.
        /// </summary>
        private bool mWithFullHeader;

        public string StoredFileName { get; private set; } = string.Empty;

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

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="compDataStream">Stream that holds compressed data.  Must be positioned
        ///   at the start.</param>
        /// <param name="mode">Flag that indicates whether we will be compressing or
        ///   expanding data.</param>
        /// <param name="leaveOpen">Flag that determines whether we close the compressed
        ///   data stream when we are disposed.</param>
        /// <param name="fullHeader">True if a full header should be created or expected.</param>
        /// <param name="storedFileName">Filename to embed when compressing a file, if full
        ///   headers are enabled.  May be empty.</param>
        public SqueezeStream(Stream compDataStream, CompressionMode mode, bool leaveOpen,
                bool fullHeader, string storedFileName) {
            mCompDataStream = compDataStream;
            mCompressionMode = mode;
            mLeaveOpen = leaveOpen;
            mWithFullHeader = fullHeader;
            StoredFileName = storedFileName;

            if (mode == CompressionMode.Compress) {
                Debug.Assert(compDataStream.CanWrite);
                InitCompress();
            } else if (mode == CompressionMode.Decompress) {
                Debug.Assert(compDataStream.CanRead);
                InitExpand();
            } else {
                throw new ArgumentException("Invalid mode: " + mode);
            }
        }

        // Note:
        //  - Stream.Dispose() calls Close()
        //  - Stream.Close() calls Dispose(true) and GC.SuppressFinalize(this)

        // IDisposable
        protected override void Dispose(bool disposing) {
            if (!disposing) {
                return;     // GC at work, nothing useful to do
            }
            if (mCompDataStream == null) {
                return;     // already disposed; could be Close + "using"
            }

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

        // Single-byte buffer for ReadByte/WriteByte, allocated on first use.
        private byte[]? mSingleBuf;

        // Stream
        public override int ReadByte() {
            if (mSingleBuf == null) {
                mSingleBuf = new byte[1];
            }
            if (Read(mSingleBuf, 0, 1) == 0) {
                return -1;      // EOF reached
            }
            return mSingleBuf[0];
        }

        // Stream
        public override void WriteByte(byte value) {
            if (mSingleBuf == null) {
                mSingleBuf = new byte[1];
            }
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

        #region Compress

        private MemoryStream? mCompRLEOutStream;

        private ushort mCompChecksum;

        private bool mCompRLEHavePrev;
        private int mCompRLECount;
        private byte mCompRLEPrevValue;


        /// <summary>
        /// Prepares object for compression.
        /// </summary>
        public void InitCompress() {
            mCompRLEOutStream = new MemoryStream();
            mCompChecksum = 0;
            mCompRLEHavePrev = false;
            mCompRLECount = 0;
            mCompRLEPrevValue = 0;

            mNodes = new Node[NUM_NODES];
            mCodeLen = new int[NUM_VALS];
            mCodes = new int[NUM_VALS];
        }

        /// <summary>
        /// Copies input to our internal buffer, compressing with RLE.
        /// </summary>
        /// <remarks>
        /// RLE compression may make things larger or smaller, depending on the frequency of the
        /// delimiter value.  Mostly it's just nice to get the first pass out of the way so it
        /// doesn't clutter up the second pass.
        /// </remarks>
        public override void Write(byte[] buf, int offset, int count) {
            Debug.Assert(mCompRLEOutStream != null);

            for (int i = offset; i < offset + count; i++) {
                // Get the next byte of input, and update the checksum.
                byte val = buf[i];
                mCompChecksum += val;

                if (!mCompRLEHavePrev) {
                    // No "previous char", so no run to test against.  Just output this one.
                    mCompRLEOutStream.WriteByte(val);
                    if (val == RLE_DELIM) {
                        // Escape the delimiter by encoding it as a zero-length run.
                        mCompRLEOutStream.WriteByte(0);
                    } else {
                        mCompRLEPrevValue = val;
                        mCompRLEHavePrev = true;
                        mCompRLECount = 1;
                    }
                } else {
                    // See if we match the previous character.  If so, continue the run.
                    if (val == mCompRLEPrevValue) {
                        Debug.Assert(val != RLE_DELIM);
                        mCompRLECount++;
                    }
                    // If it didn't match, or we're one past the max run length, output the run.
                    if (val != mCompRLEPrevValue || mCompRLECount == 256) {
                        if (mCompRLECount == 256) {
                            // Subtract one here; 256th entry becomes start of next potential run.
                            mCompRLECount = 255;
                        }
                        switch (mCompRLECount) {
                            case 1:
                                // Already written.
                                break;
                            case 2:
                                // Write one more.
                                mCompRLEOutStream.WriteByte(mCompRLEPrevValue);
                                break;
                            default:
                                // Write the delimiter and count.
                                mCompRLEOutStream.WriteByte(RLE_DELIM);
                                mCompRLEOutStream.WriteByte((byte)mCompRLECount);
                                break;
                        }

                        if (val == RLE_DELIM) {
                            // Output the escaped delimiter char, and cancel history.
                            mCompRLEOutStream.WriteByte(val);
                            mCompRLEOutStream.WriteByte(0);
                            mCompRLEHavePrev = false;
                            mCompRLECount = 0;
                        } else {
                            // Use the byte we just read as the potential start of a new run.
                            mCompRLEOutStream.WriteByte(val);
                            mCompRLEPrevValue = val;
                            mCompRLECount = 1;
                        }
                    }
                }
            }
        }

        private void FinishCompression() {
            Debug.Assert(mCompRLEOutStream != null);
            // Output the last of the RLE.
            if (mCompRLEHavePrev) {
                switch (mCompRLECount) {
                    case 1:
                        break;
                    case 2:
                        mCompRLEOutStream.WriteByte(mCompRLEPrevValue);
                        break;
                    default:
                        mCompRLEOutStream.WriteByte(RLE_DELIM);
                        mCompRLEOutStream.WriteByte((byte)mCompRLECount);
                        break;
                }
            }

            // Perform the Huffman encoding.
            AnalyzeData();
            OutputHeader();
            DoCompress();
        }


        /// <summary>
        /// Nodes of the binary trees.
        /// </summary>
        /// <remarks>
        /// The first NUM_VALS nodes become the leaves of the final tree, and represent the
        /// values of the data bytes being encoded, plus the EOF marker.  The remaining nodes
        /// become the internal nodes of the final tree.
        /// </remarks>
        private struct Node {
            public int mWeight;             // number of appearances
            public int mDepth;              // length on longest path in tree
            public int mLeft, mRight;       // indexes to next level
            public override string ToString() {
                return "[Node: wt=" + mWeight + " dp=" + mDepth +
                    " lf=" + mLeft + " rt=" + mRight + "]";
            }
        }
        private Node[]? mNodes;
        private const int NO_CHILD = -1;    // indicates end of path through tree

        /// <summary>
        /// List of code lengths, one entry per symbol.
        /// </summary>
        private int[]? mCodeLen;

        /// <summary>
        /// List of code bits, one entry per symbol.
        /// </summary>
        private int[]? mCodes;

        /// <summary>
        /// Temporary code value.
        /// </summary>
        private int mTempCode;

        /// <summary>
        /// Index of tree head.
        /// </summary>
        private int mTreeHead;


        /// <summary>
        /// Builds an encoding tree, and uses it to generate the code tables.
        /// </summary>
        private void AnalyzeData() {
            Debug.Assert(mNodes != null && mCodeLen != null);

            const int MAX_COUNT = 65535;                        // ushort.MaxValue

            // Initialize the tree.
            for (int i = 0; i < NUM_NODES; i++) {
                mNodes[i].mWeight = 0;
                mNodes[i].mDepth = 0;
                mNodes[i].mLeft = NO_CHILD;
                mNodes[i].mRight = NO_CHILD;
            }

            // List of intermediate binary trees.
            int[] btList = new int[NUM_VALS];

            // Generate frequency counts.  If we hit the maximum value, we clamp it to max
            // instead of rescaling the counts.  This would work poorly for larger files, but
            // this type of compression is a bad choice for large files anyway.
            byte[] data = mCompRLEOutStream!.GetBuffer();
            for (int i = 0; i < mCompRLEOutStream.Length; i++) {
                byte val = data[i];
                if (mNodes[val].mWeight != MAX_COUNT) {
                    mNodes[val].mWeight++;
                }
            }
            mNodes[EOF_TOKEN].mWeight = 1;

            // Try to build the encoding table.  If we fail, because a code is longer than 16 bits,
            // cut the ceiling in half and try again.
            int ceiling = MAX_COUNT;
            do {
                Scale(ceiling);
                ceiling /= 2;       // in case we loop around and rescale

                // Build list of single-node binary trees having leaves for the input values
                // with non-zero counts.
                int listLen = 0;
                for (int i = 0; i < NUM_VALS; i++) {
                    if (mNodes[i].mWeight != 0) {
                        mNodes[i].mDepth = 0;
                        btList[listLen++] = i;
                    }
                }

                // Arrange list of trees into a heap with the entry indexing the node with the
                // least weight at the top.
                Heap(btList, listLen);

                // Convert the list of trees to a single decoding tree.
                mTreeHead = BuildTree(btList, listLen);

                // Initialize the encoding table.
                for (int i = 0; i < NUM_VALS; i++) {
                    mCodeLen[i] = 0;
                }

                mTempCode = 0;
            } while (!BuildEncoding(0, mTreeHead));
        }

        /// <summary>
        /// Scale values so that their sum doesn't exceed ceiling and yet no non-zero count can
        /// become zero.
        /// </summary>
        /// <param name="ceiling">Upper limit on total weight.</param>
        private void Scale(int ceiling) {
            Debug.Assert(mNodes != null);
            bool increased;
            int divisor;

            do {
                int sum = 0;
                int ovflw = 0;
                for (int i = 0; i < NUM_VALS; i++) {
                    if (mNodes[i].mWeight > (ceiling - sum)) {
                        ovflw++;
                    }
                    sum += mNodes[i].mWeight;
                }

                divisor = ovflw + 1;

                // Ensure no non-zero values are lost.
                increased = false;
                for (int i = 0; i < NUM_VALS; i++) {
                    int w = mNodes[i].mWeight;
                    if (w < divisor && w > 0) {
                        // Don't fail to provide a code if it's used at all.
                        mNodes[i].mWeight = divisor;
                        increased = true;
                    }
                }
            } while (increased);

            // Scaling factor chosen, now scale.
            if (divisor > 1) {
                for (int i = 0; i < NUM_VALS; i++) {
                    mNodes[i].mWeight /= divisor;
                }
            }
        }

        // From TR2.C:
        // heap() and adjust() maintain a list of binary trees as a
        // heap with the top indexing the binary tree on the list
        // which has the least weight or, in case of equal weights,
        // least depth in its longest path. The depth part is not
        // strictly necessary, but tends to avoid long codes which
        // might provoke rescaling.

        private void Heap(int[] list, int length) {
            for (int i = (length - 2) / 2; i >= 0; i--) {
                HeapAdjust(list, i, length - 1);
            }
        }

        private void HeapAdjust(int[] list, int top, int bottom) {
            int k, temp;
            k = 2 * top + 1;        // left child of top
            temp = list[top];       // remember root node of top tree
            if (k <= bottom) {
                if (k < bottom && CmpTrees(list[k], list[k + 1])) {
                    k++;
                }

                // k indexes "smaller" child (in heap of trees) of top; now make top index
                // "smaller" of old top and smallest child.
                if (CmpTrees(temp, list[k])) {
                    list[top] = list[k];
                    list[k] = temp;
                    // Make the changed list a heap.
                    HeapAdjust(list, k, bottom);        // recursive
                }
            }
        }

        /// <summary>
        /// Compare two trees, based on weight.  If the weights are equal, compare depths.
        /// </summary>
        /// <returns>True if a > b.</returns>
        private bool CmpTrees(int a, int b) {
            Debug.Assert(mNodes != null);
            if (mNodes[a].mWeight > mNodes[b].mWeight) {
                return true;
            }
            if (mNodes[a].mWeight == mNodes[b].mWeight) {
                if (mNodes[a].mDepth > mNodes[b].mDepth) {
                    return true;
                }
            }
            return false;
        }

        // From TR2.C:
        // HUFFMAN ALGORITHM: develops the single element trees
        // into a single binary tree by forming subtrees rooted in
        // interior nodes having weights equal to the sum of weights of all
        // their descendents and having depth counts indicating the
        // depth of their longest paths.
        //
        // When all trees have been formed into a single tree satisfying
        // the heap property (on weight, with depth as a tie breaker)
        // then the binary code assigned to a leaf (value to be encoded)
        // is then the series of left (0) and right (1)
        // paths leading from the root to the leaf.
        // Note that trees are removed from the heaped list by
        // moving the last element over the top element and
        // reheaping the shorter list.

        private int BuildTree(int[] list, int len) {
            Debug.Assert(mNodes != null);

            // Next free node in tree.  Initialize to next available (non-leaf) node.
            int freeNode = NUM_VALS;

            while (len > 1) {
                int leftChild, rightChild;

                // Take from list two btrees with least weight and build an interior node
                // pointing to them.  This forms a new tree.
                leftChild = list[0];

                // Delete top (least) tree from the list of trees.
                list[0] = list[--len];
                HeapAdjust(list, 0, len - 1);

                // Take new top (least) tree.  Reuse list slot later.
                rightChild = list[0];

                // Form a new tree from the two least trees using a free nodes as root.  Put
                // the new tree in the list.
                int thisNode = freeNode;
                list[0] = freeNode++;           // put at top for now
                mNodes[thisNode].mLeft = leftChild;
                mNodes[thisNode].mRight = rightChild;
                mNodes[thisNode].mWeight = mNodes[leftChild].mWeight + mNodes[rightChild].mWeight;
                mNodes[thisNode].mDepth = 1 +
                    Math.Max(mNodes[leftChild].mDepth, mNodes[rightChild].mDepth);
                // Reheap list to get least tree at top.
                HeapAdjust(list, 0, len - 1);
            }

            return list[0];     // head of final tree
        }

        /// <summary>
        /// Recursively generates the encoding table.
        /// </summary>
        /// <returns>True if all is well, false if we found an invalid code.</returns>
        private bool BuildEncoding(int level, int root) {
            Debug.Assert(mNodes != null && mCodeLen != null && mCodes != null);
            int left = mNodes[root].mLeft;
            int right = mNodes[root].mRight;

            if (left == NO_CHILD && right == NO_CHILD) {
                // Leaf.  Previous path determines bit string code of length level
                // (bits 0 to level - 1).  Mask to ensure unused code bits are zero.
                mCodeLen[root] = level;
                mCodes[root] = mTempCode & (0x0000ffff >> (16 - level));
                return (level <= 16);
            } else {
                if (left != NO_CHILD) {
                    // Clear bit and continue deeper.
                    mTempCode &= ~(1 << level);
                    if (!BuildEncoding(level + 1, left)) {
                        return false;
                    }
                }
                if (right != NO_CHILD) {
                    // Set bit and continue deeper.
                    mTempCode |= 1 << level;
                    if (!BuildEncoding(level + 1, right)) {
                        return false;
                    }
                }
            }
            return true;
        }

        /// <summary>
        /// Generates the file header and node table into our internal buffer.
        /// </summary>
        private void OutputHeader() {
            Debug.Assert(mNodes != null);

            // Generate the file header, if requested.
            if (mWithFullHeader) {
                mCompDataStream.WriteByte(MAGIC0);
                mCompDataStream.WriteByte(MAGIC1);
                RawData.WriteU16LE(mCompDataStream, mCompChecksum);
                for (int i = 0; i < StoredFileName.Length; i++) {
                    mCompDataStream.WriteByte((byte)StoredFileName[i]);
                }
                mCompDataStream.WriteByte(0x00);
            }

            // Generate the node table.
            //
            // Only the interior nodes are written.  The tree will be empty for an empty file.
            int numNodes = mTreeHead < NUM_VALS ? 0 : mTreeHead - (NUM_VALS - 1);
            RawData.WriteU16LE(mCompDataStream, (ushort)numNodes);
            int j = mTreeHead;
            for (int k = 0; k < numNodes; k++, j--) {
                int left = mNodes[j].mLeft;
                int right = mNodes[j].mRight;
                // Output as negative value for literal, positive for node index.
                left = left < NUM_VALS ? -(left + 1) : mTreeHead - left;
                right = right < NUM_VALS ? -(right + 1) : mTreeHead - right;
                RawData.WriteU16LE(mCompDataStream, (ushort)left);
                RawData.WriteU16LE(mCompDataStream, (ushort)right);
            }
        }

        /// <summary>
        /// Compresses the data from the memory buffer to the output, using Huffman encoding.
        /// The RLE has already been applied.
        /// </summary>
        private void DoCompress() {
            Debug.Assert(mCodes != null && mCodeLen != null);
            byte[] buf = mCompRLEOutStream!.GetBuffer();

            int bits = 0;           // must hold 16+7=23 bits
            int bitsLen = 0;
            for (int i = 0; i < mCompRLEOutStream.Length; i++) {
                byte val = buf[i];

                bits |= mCodes[val] << bitsLen;
                bitsLen += mCodeLen[val];

                while (bitsLen > 7) {
                    mCompDataStream.WriteByte((byte)bits);
                    bits >>= 8;
                    bitsLen -= 8;
                }
            }

            // Write the EOF token, and flush any spare bits.
            bits |= mCodes[EOF_TOKEN] << bitsLen;
            bitsLen += mCodeLen[EOF_TOKEN];
            while (bitsLen > -8) {
                mCompDataStream.WriteByte((byte)bits);
                bits >>= 8;
                bitsLen -= 8;
            }
        }

        #endregion Compress


        #region Expand

        private byte[] mExpInput = RawData.EMPTY_BYTE_ARRAY;    // buffer for reading header
        private int mExpInputLen;           // bytes in header buffer

        private bool mExpHeaderRead;
        private bool mExpNodesRead;
        private bool mExpInputEnded;
        private bool mExpOutputComplete;

        private ushort mExpFileChecksum;    // checksum from file header
        private ushort mExpCalcChecksum;    // checksum calculated from data

        private int mExpBits;               // 0-24 bits from input
        private int mExpBitCount;           // number of valid bits in mExpBits

        private bool mExpRLESawDelim;       // prev char was RLE delimiter
        private int mExpRLECount;           // length of run, or 0 if we're not in one
        private byte mExpRLELastChar;       // last char seen
        private byte mExpRLEValue;          // repeated value


        /// <summary>
        /// Decoding tree with left/right kids.  Positive values are indices to another node,
        /// negative values are literals (add one and negate).
        /// </summary>
        /// <remarks>
        /// In C this was an array of struct { short child[2]; }, but that's awkward in C#,
        /// so we just make it an array of ints and double the index.
        /// </remarks>
        private int[] mExpTree = RawData.EMPTY_INT_ARRAY;


        /// <summary>
        /// Prepares object for expansion.
        /// </summary>
        public void InitExpand() {
            mExpInput = new byte[BUFFER_SIZE];
            mExpInputLen = 0;
            mExpCalcChecksum = 0;
            mExpBits = 0;
            mExpBitCount = 0;
            mExpRLECount = 0;
            mExpRLESawDelim = false;
        }

        // Stream
        public override int Read(byte[] buffer, int offset, int count) {
            if (mWithFullHeader && !mExpHeaderRead) {
                ReadHeader();
                mExpHeaderRead = true;
            }
            if (!mExpNodesRead) {
                ReadNodes();
                if (mExpTree.Length == 0) {
                    // Empty file.
                    mExpOutputComplete = true;
                }
                mExpNodesRead = true;
            }

            if (mExpOutputComplete) {
                return 0;
            }

            int actual = ExpandData(buffer, offset, count);
            if (mExpOutputComplete) {
                if (mWithFullHeader && mExpFileChecksum != mExpCalcChecksum) {
                    throw new InvalidDataException("Checksum mismatch");
                }
            }
            return actual;
        }

        /// <summary>
        /// Reads and validates all data up to the end of the full-file header.  Does not consume
        /// any data beyond that.
        /// </summary>
        /// <returns>True if we have found the full header.</returns>
        private void ReadHeader() {
            // Copy the header into our input buffer, stopping when we find
            // the terminating null or we exceed the maximum header length.
            while (true) {
                if (mExpInputLen >= MAX_FILE_HEADER_LEN) {
                    // Didn't find the end of the filename.
                    throw new InvalidDataException("Squeeze header too long");
                }

                int val = mCompDataStream.ReadByte();
                if (val < 0) {
                    throw new InvalidDataException("Didn't find all of squeeze header");
                }
                mExpInput[mExpInputLen++] = (byte)val;
                if (mExpInputLen >= MIN_FILE_HEADER_LEN && val == 0x00) {
                    break;
                }
            }

            // Found the terminating null byte.  Extract and validate the header.
            if (mExpInput[0] != MAGIC0 || mExpInput[1] != MAGIC1) {
                throw new InvalidDataException("Did not find squeeze header magic");
            }
            mExpFileChecksum = RawData.GetU16LE(mExpInput, 2);
            // Do an ASCII conversion of the filename.  This is not guaranteed to be
            // anything reasonable.
            char[] nameChars = new char[mExpInputLen - MIN_FILE_HEADER_LEN];
            for (int i = 0; i < nameChars.Length; i++) {
                nameChars[i] = (char)mExpInput[i + 4];
            }
            StoredFileName = new string(nameChars);
        }

        /// <summary>
        /// Reads the nodes from the header.
        /// </summary>
        private void ReadNodes() {
            int nodeCount = RawData.ReadU16LE(mCompDataStream, out bool ok);
            if (!ok) {
                throw new InvalidDataException("Unable to read node count");
            }
            try {
                mCompDataStream.ReadExactly(mExpInput, 0, nodeCount * 4);
            } catch (EndOfStreamException) {
                throw new InvalidDataException("Unable to read nodes");
            }

            // We have the nodes.  Load them into our data structure.  Note that the
            // literals don't become nodes; they are essentially just pointers that we
            // know not to follow.
            mExpTree = new int[nodeCount * 2];
            for (int i = 0; i < nodeCount; i++) {
                short left = (short)RawData.GetU16LE(mExpInput, i * 4);      // left child
                short right = (short)RawData.GetU16LE(mExpInput, i * 4 + 2); // right child
                if (left < -(EOF_TOKEN+1) || left >= 256 ||
                        right < -(EOF_TOKEN+1) || right >= 256) {
                    // Invalid as literal or pointer.
                    throw new InvalidDataException("Bad squeeze node entry");
                }
                mExpTree[i * 2] = left;
                mExpTree[i * 2 + 1] = right;
            }
        }

        /// <summary>
        /// Expands the compressed data.  Returns the number of bytes output.
        /// </summary>
        private int ExpandData(byte[] buffer, int offset, int count) {
            int startOffset = offset;

            while (count != 0) {
                // If we're in the middle of a run, write out as much of it as we can.
                while (mExpRLECount > 0 && count != 0) {
                    buffer[offset++] = mExpRLEValue;
                    mExpCalcChecksum += mExpRLEValue;
                    mExpRLECount--;
                    count--;
                }

                // We need to decode a Huffman-encoded symbol and feed it into the RLE expander.
                // The compressor limited the length of the Huffman codes to 16 bits, so we want
                // to have at least that much data available at each step (unless we've reached
                // the end of the input stream).
                while (mExpBitCount < 16 && !mExpInputEnded) {
                    int rdval = mCompDataStream.ReadByte();
                    if (rdval < 0) {
                        mExpInputEnded = true;
                    } else {
                        mExpBits |= rdval << mExpBitCount;
                        mExpBitCount += 8;
                    }
                }

                if (count == 0) {
                    break;
                }

                int val = DecodeHuffSymbol(false);
                if (val == EOF_TOKEN) {
                    // End of file reached.
                    mExpOutputComplete = true;
                    break;
                }

                // Feed it into the RLE decoder.
                if (mExpRLESawDelim) {
                    // Previous char was RLE delimiter.  The character before that was the
                    // character being repeated.  Because we use a count of zero to escape
                    // the delimiter, the earlier character is included in the count.
                    if (val == 0) {
                        // Delimiter escape.
                        buffer[offset++] = RLE_DELIM;
                        count--;
                        mExpCalcChecksum += RLE_DELIM;
                    } else {
                        // We may not have room for all of it, so set up persistent state.
                        mExpRLECount = val - 1;
                        mExpRLEValue = mExpRLELastChar;
                    }
                    mExpRLESawDelim = false;
                } else {
                    if (val == RLE_DELIM) {
                        mExpRLESawDelim = true;
                    } else {
                        buffer[offset++] = (byte)val;
                        count--;
                        mExpCalcChecksum += (byte)val;
                        mExpRLELastChar = (byte)val;
                    }
                }
            }

            return offset - startOffset;
        }

        /// <summary>
        /// Decodes the next symbol.
        /// </summary>
        private int DecodeHuffSymbol(bool peekOnly) {
            int val = 0;
            int expBitCount = mExpBitCount;
            int expBits = mExpBits;
            do {
                if (expBitCount == 0) {
                    // Tree height was > 16, or we ran out of input.  We don't actually have a
                    // problem with excessively long codes, but it suggests that the node tree
                    // was damaged.
                    throw new InvalidDataException("Invalid code found");
                }
                val = mExpTree[val * 2 + (expBits & 0x01)];
                expBits >>= 1;
                expBitCount--;
            } while (val >= 0);

            if (!peekOnly) {
                mExpBitCount = expBitCount;
                mExpBits = expBits;
            }

            // Value is a literal; add one to make it zero-based, then negate it.
            return -(val + 1);
        }

        #endregion Expand
    }
}
