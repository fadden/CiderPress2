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
using System.Diagnostics;
using System.Text;

using CommonUtil;
using DiskArc;

namespace FileConv.Gfx {
    public class SuperHiRes_APF : Converter {
        public const string TAG = "shrapf";
        public const string LABEL = "Apple Preferred Format SHR image";
        public const string DESCRIPTION =
            "Converts an Apple Preferred Format graphics file to a bitmap.";
        public const string DISCRIMINATOR = "ProDOS PNT/$0002.";
        public override string Tag => TAG;
        public override string Label => LABEL;
        public override string Description => DESCRIPTION;
        public override string Discriminator => DISCRIMINATOR;

        public const int MIN_LEN = 4 + 5 + 12;      // somewhat arbitrary


        private SuperHiRes_APF() { }

        public SuperHiRes_APF(FileAttribs attrs, Stream? dataStream, Stream? rsrcStream,
                ResourceMgr? resMgr, ConvFlags convFlags, AppHook appHook)
                : base(attrs, dataStream, rsrcStream, resMgr, convFlags, appHook) {
            Applic = TestApplicability();
        }

        protected override Applicability TestApplicability() {
            if (DataStream == null) {
                return Applicability.Not;
            }
            if (FileAttrs.FileType != FileAttribs.FILE_TYPE_PNT || FileAttrs.AuxType != 0x0002) {
                return Applicability.Not;
            }
            if (DataStream.Length < MIN_LEN) {
                return Applicability.Not;
            }
            return Applicability.Yes;
        }

        public override Type GetExpectedType(Dictionary<string, string> options) {
            return typeof(IBitmap);
        }

        public override IConvOutput ConvertFile(Dictionary<string, string> options) {
            if (Applic <= Applicability.Not) {
                Debug.Assert(false);
                return new ErrorText("ERROR");
            }
            Debug.Assert(DataStream != null);

            byte[] fileBuf = new byte[DataStream.Length];
            DataStream.Position = 0;
            DataStream.ReadExactly(fileBuf, 0, (int)DataStream.Length);
            return ConvertBuffer(fileBuf);

        }

        private const string BLOCK_NAME_MAIN = "MAIN";
        private const string BLOCK_NAME_MASK = "MASK";
        private const string BLOCK_NAME_MULTIPAL = "MULTIPAL";
        private const string BLOCK_NAME_NOTE = "NOTE";

        private const int MAX_PIXELS_PER_SCAN = 1280;
        private const int MAX_SCAN_LINES = 1024;
        private const int MAX_MAIN_COLOR_TABLES = 16;       // SCB can only reference this many

        /// <summary>
        /// APF block.  The file is a list of these.
        /// </summary>
        private class Block {
            public int Offset { get; private set; }
            public int Length { get; private set; }
            public string Name { get; private set; }

            public int DataOffset { get; private set; }

            public Block(int offset, int length, string name) {
                Offset = offset;
                Length = length;
                Name = name;

                DataOffset = offset + 4 + 1 + name.Length;
            }
        }

        private class ColorTable {
            public const int COLORS_PER_TABLE = 16;
            public ushort[] mGSColors = new ushort[COLORS_PER_TABLE];   // array of ColorEntry
            public int[] mArgbColors = new int[COLORS_PER_TABLE];
        }

        private class DirEntry {
            public short mBytesToUnpack;
            public ushort mMode;
            public DirEntry(short bytesToUnpack, ushort mode) {
                mBytesToUnpack = bytesToUnpack;
                mMode = mode;
            }
        }

        /// <summary>
        /// MAIN block.  Theoretically optional, but we can't do much without it.
        /// </summary>
        private class MainBlock {
            public ushort mMasterMode;
            public short mPixelsPerScanLine;
            public short mNumColorTables;
            public ColorTable[] mColorTableArray = new ColorTable[0];
            public short mNumScanLines;
            public DirEntry[] mScanLineDirectory = new DirEntry[0];
            public int mPackedScanLinesOffset;      // offset of start in file data

            public MainBlock() { }

            public bool Load(byte[] buf, Block block, Notes loadNotes) {
                try {
                    int offset = block.DataOffset;
                    mMasterMode = RawData.ReadU16LE(buf, ref offset);
                    if ((mMasterMode & 0xff00) != 0) {
                        // High byte must be zero.
                        loadNotes.AddE("Invalid MasterMode $" + mMasterMode.ToString("x4"));
                        return false;
                    }
                    mPixelsPerScanLine = (short)RawData.ReadU16LE(buf, ref offset);
                    if (mPixelsPerScanLine == 0 || mPixelsPerScanLine > MAX_PIXELS_PER_SCAN) {
                        loadNotes.AddE("Invalid pixels per scan line: " + mPixelsPerScanLine);
                        return false;
                    }
                    mNumColorTables = (short)RawData.ReadU16LE(buf, ref offset);
                    if (mNumColorTables == 0) {
                        // Technically valid, but would result in a black screen since there's
                        // no default color table defined.
                        loadNotes.AddE("Don't know how to render image with no color tables");
                        return false;
                    } else if (mNumColorTables > MAX_MAIN_COLOR_TABLES) {
                        loadNotes.AddE("Invalid num color tables: " + mNumColorTables);
                        return false;
                    }

                    // Load the color tables.
                    mColorTableArray = new ColorTable[mNumColorTables];
                    for (int i = 0; i < mNumColorTables; i++) {
                        ColorTable newTable = new ColorTable();
                        for (int ent = 0; ent < ColorTable.COLORS_PER_TABLE; ent++) {
                            ushort gsColor = RawData.ReadU16LE(buf, ref offset);
                            newTable.mGSColors[ent] = gsColor;
                            newTable.mArgbColors[ent] = SuperHiRes.ARGBFromSHR(gsColor);
                        }
                        mColorTableArray[i] = newTable;
                    }

                    // Get the scanline count.
                    mNumScanLines = (short)RawData.ReadU16LE(buf, ref offset);
                    if (mNumScanLines == 0 || mNumScanLines > MAX_SCAN_LINES) {
                        loadNotes.AddE("Invalid num scan lines: " + mNumScanLines);
                        return false;
                    }

                    // Load the per-scanline DirEntry array.
                    mScanLineDirectory = new DirEntry[mNumScanLines];
                    for (int i = 0; i < mNumScanLines; i++) {
                        short packedLen = (short)RawData.ReadU16LE(buf, ref offset);
                        if (packedLen == 0 || packedLen > mPixelsPerScanLine) {
                            // Each byte holds 2 or 4 pixels, so packedLen exceeding that would
                            // be pretty weird.
                            loadNotes.AddE("Invalid packed len for line " + i + ": " + packedLen);
                            return false;
                        }
                        ushort mode = RawData.ReadU16LE(buf, ref offset);
                        if ((mode & 0xff00) != 0) {
                            loadNotes.AddW("Invalid mode for line " + i + ": $" +
                                mode.ToString("x4"));
                            mode = 0;
                        }
                        DirEntry newEntry = new DirEntry(packedLen, mode);
                        mScanLineDirectory[i] = newEntry;
                    }

                    mPackedScanLinesOffset = offset;

                    // Scan forward to confirm that we have the correct amount of data.
                    for (int i = 0; i < mNumScanLines; i++) {
                        offset += mScanLineDirectory[i].mBytesToUnpack;
                    }

                    if (offset > block.Offset + block.Length) {
                        loadNotes.AddE("MAIN block parsing ran off end of block");
                        return false;
                    } else if (offset != block.Offset + block.Length) {
                        loadNotes.AddW("Did not parse all data in MAIN block (" +
                            (block.Offset + block.Length - offset) + " left over)");
                        // it's fine?
                    }

                    loadNotes.AddI("Parsed MAIN block: " + this);
                    return true;
                } catch (IndexOutOfRangeException) {
                    loadNotes.AddW("Ran off end of file parsing MAIN block");
                    return false;
                }
            }

            /// <summary>
            /// Returns 16 sets of 16 ARGB colors.  Tables beyond those included in the APF file
            /// will be filled with opaque neon pink.
            /// </summary>
            public int[] GeneratePalette256() {
                const int MISSING_COLOR = (int)(0xffff10f0 - 0x100000000);

                int[] colors = new int[16 * 16];
                int index = 0;
                int tab;
                for (tab = 0; tab < mNumColorTables; tab++) {
                    for (int ent = 0; ent < 16; ent++) {
                        colors[index++] = mColorTableArray[tab].mArgbColors[ent];
                    }
                }
                for (; tab < 16; tab++) {
                    for (int ent = 0; ent < 16; ent++) {
                        colors[index++] = MISSING_COLOR;
                    }
                }
                Debug.Assert(index == 256);
                return colors;
            }

            public override string ToString() {
                SuperHiRes.UnpackSCB((byte)mMasterMode, out int paletteNum, out bool isFill,
                    out bool is640);
                return string.Format(
                    "MasterMode=${0:x4} ({1}), pix-per-line={2}, lines={3}, num ct={4}",
                    mMasterMode, is640 ? "640-mode" : "320-mode", mPixelsPerScanLine,
                    mNumScanLines, mNumColorTables);
            }
        }

        /// <summary>
        /// Optional MULTIPAL block.  Found in 3200-color images.
        /// </summary>
        private class MultipalBlock {
            public short mNumColorTables;
            public ColorTable[] mColorTableArray = new ColorTable[0];

            public bool Load(byte[] buf, Block block, Notes loadNotes) {
                try {
                    int offset = block.DataOffset;
                    // There should be one color table per line in the MAIN section.
                    mNumColorTables = (short)RawData.ReadU16LE(buf, ref offset);
                    if (mNumColorTables == 0) {
                        // Technically valid, but not useful.
                        loadNotes.AddE("Don't know how to render image with no color tables");
                        return false;
                    } else if (mNumColorTables > MAX_SCAN_LINES) {
                        loadNotes.AddE("Invalid num color tables: " + mNumColorTables);
                        return false;
                    }

                    // Load the color tables.
                    mColorTableArray = new ColorTable[mNumColorTables];
                    for (int i = 0; i < mNumColorTables; i++) {
                        ColorTable newTable = new ColorTable();
                        for (int ent = 0; ent < ColorTable.COLORS_PER_TABLE; ent++) {
                            ushort gsColor = RawData.ReadU16LE(buf, ref offset);
                            newTable.mGSColors[ent] = gsColor;
                            newTable.mArgbColors[ent] = SuperHiRes.ARGBFromSHR(gsColor);
                        }
                        mColorTableArray[i] = newTable;
                    }
                    return true;
                } catch (IndexOutOfRangeException) {
                    loadNotes.AddW("Ran off end of file parsing MULTIPAL block");
                    return false;
                }
            }
        }

        private static IConvOutput ConvertBuffer(byte[] fileBuf) {
            // Parse the contents of the blocks.
            Notes loadNotes = new Notes();
            Dictionary<string, Block> blocks = LoadBlocks(fileBuf, loadNotes);
            if (blocks.Count == 0) {
                return new ErrorText("This does not appear to be a valid APF file");
            }

            List<string> names = blocks.Keys.ToList();
            names.Sort();
            string blocksFound = string.Join(",", names);
            Debug.WriteLine("Blocks found: " + blocksFound);
            loadNotes.AddI("Blocks found: " + blocksFound);

            // If it doesn't have a MAIN, we have nothing to do.
            if (!blocks.TryGetValue(BLOCK_NAME_MAIN, out Block? mainBlock)) {
                IConvOutput errOut = new ErrorText("No pixel data found in APF file.");
                errOut.Notes.MergeFrom(loadNotes);
                return errOut;
            }

            // Try to load the MAIN block.
            MainBlock main = new MainBlock();
            if (!main.Load(fileBuf, mainBlock, loadNotes)) {
                IConvOutput errOut = new ErrorText("Failed while loading MAIN block.");
                errOut.Notes.MergeFrom(loadNotes);
                return errOut;
            }

            // Try to load the optional MULTIPAL block.
            MultipalBlock? multipal = null;
            if (blocks.TryGetValue(BLOCK_NAME_MULTIPAL, out Block? multipalBlock)) {
                multipal = new MultipalBlock();
                if (!multipal.Load(fileBuf, multipalBlock, loadNotes)) {
                    loadNotes.AddW("Failed to load MULTIPAL block.");
                    multipal = null;
                } else if (multipal.mNumColorTables != main.mNumScanLines) {
                    loadNotes.AddW("MULTIPAL has incorrect number of color tables (found " +
                        multipal.mNumColorTables + ", expected " + main.mNumScanLines + ")");
                    multipal = null;
                }
            }

            // Look for a MASK block.
            if (blocks.TryGetValue(BLOCK_NAME_MASK, out Block? maskBlock)) {
                loadNotes.AddI("Found an APF MASK block: off=$" + maskBlock.Offset.ToString("x") +
                    ", len=" + maskBlock.Length);
                // TODO: something useful with this; looks like Platinum Paint can create these?
            }

            // Look for a NOTE block.
            if (blocks.TryGetValue(BLOCK_NAME_NOTE, out Block? noteBlock)) {
                ShowNOTE(fileBuf, noteBlock, loadNotes);
            }

            IConvOutput output = ConvertImage(fileBuf, main, multipal);

            output.Notes.MergeFrom(loadNotes);
            return output;
        }

        private static IBitmap ConvertImage(byte[] fileBuf, MainBlock main,
                MultipalBlock? multipal) {
            int shortUnpackCount = 0;
            byte masterScb = (byte)main.mMasterMode;
            SuperHiRes.UnpackSCB(masterScb, out int unus1, out bool unus2, out bool masterIs640);
            int byteWidth = masterIs640 ? (main.mPixelsPerScanLine + 3) / 4 :
                (main.mPixelsPerScanLine + 1) / 2;

            int displayWidth = masterIs640 ? main.mPixelsPerScanLine : main.mPixelsPerScanLine * 2;
            IBitmap output;
            if (multipal == null) {
                output = new Bitmap8(displayWidth, main.mNumScanLines * 2);
                ((Bitmap8)output).SetPalette(new Palette8("APF Image", main.GeneratePalette256()));
            } else {
                output = new Bitmap32(displayWidth, main.mNumScanLines * 2);
            }

            int modeDiffCount = 0;
            int packOffset = main.mPackedScanLinesOffset;
            byte[] unpackBuf = new byte[1280];                  // oversized
            byte[] colorBuf = new byte[main.mPixelsPerScanLine + 3];    // oversize for odd widths

            for (int row = 0; row < main.mNumScanLines; row++) {
                ushort mode = main.mScanLineDirectory[row].mMode;
                SuperHiRes.UnpackSCB((byte)mode, out int paletteNum, out bool isFill,
                    out bool is640);
                if (is640 != masterIs640) {
                    // TODO: this situation seems to result in seeing only the left half of
                    //   the image when master=640 and line=320 (e.g. "jobs.apf").
                    modeDiffCount++;
                }

                int unpackCount = ApplePack.UnpackBytes(fileBuf, packOffset,
                    main.mScanLineDirectory[row].mBytesToUnpack, unpackBuf, 0, out bool unpackErr);
                if (unpackCount == byteWidth - 1) {
                    // Seeing this sometimes on images with an odd number of pixels.  Repeat
                    // the previous byte value.
                    unpackBuf[byteWidth - 1] = unpackBuf[byteWidth - 2];
                    shortUnpackCount++;
                } else if (unpackErr || unpackCount < byteWidth) {
                    output.Notes.AddW("UnpackBytes failed on line " + row + ": expected=" +
                        byteWidth + ", actual=" + unpackCount);
                    break;
                }

                SuperHiRes.ConvertLine(unpackBuf, 0, byteWidth, isFill, is640, colorBuf, 0);

                // Set pixels in bitmap.  For 320 mode we double up horizontally.
                int colorOffset = paletteNum << 4;
                for (int col = 0; col < displayWidth; col++) {
                    byte colorIndex;
                    if (is640) {
                        colorIndex = colorBuf[col];
                    } else {
                        colorIndex = colorBuf[col / 2];
                    }
                    if (multipal == null) {
                        // Factor in the palette index, and set the pixels.
                        colorIndex = (byte)(colorIndex | colorOffset);
                        ((Bitmap8)output).SetPixelIndex(col, row * 2, colorIndex);
                        ((Bitmap8)output).SetPixelIndex(col, row * 2 + 1, colorIndex);
                    } else {
                        ColorTable table = multipal.mColorTableArray[row];
                        int colorValue = table.mArgbColors[colorIndex];
                        ((Bitmap32)output).SetPixel(col, row * 2, colorValue);
                        ((Bitmap32)output).SetPixel(col, row * 2 + 1, colorValue);
                    }
                }

                packOffset += main.mScanLineDirectory[row].mBytesToUnpack;
            }

            if (modeDiffCount != 0) {
                output.Notes.AddI("Master mode is " + (masterIs640 ? "640" : "320") +
                    ", found other mode on " + modeDiffCount + " lines");
            }
            if (shortUnpackCount != 0) {
                output.Notes.AddI("Found a short unpack count on " + shortUnpackCount + " lines");
            }
            return output;
        }

        /// <summary>
        /// Displays the contents of a NOTE block.  This isn't defined by the file type note,
        /// so the definition could vary between images.  The notes found so far appear to have
        /// been added by DreamGrafix.
        /// </summary>
        private static void ShowNOTE(byte[] fileBuf, Block noteBlock, Notes loadNotes) {
            const int OVERHEAD = 4 + 1 + 4 + 2;     // block len, name, 2-byte string len
            if (noteBlock.Length < OVERHEAD) {
                loadNotes.AddI("Found a NOTE block but it was too short.");
                return;
            }
            int offset = noteBlock.DataOffset;
            ushort stringLen = RawData.ReadU16LE(fileBuf, ref offset);
            if (stringLen > noteBlock.Length - OVERHEAD) {
                loadNotes.AddI("Found a NOTE block but the string length was too long.");
                return;
            }
            // Not sure if we should expect Mac OS Roman here.
            string note = Encoding.ASCII.GetString(fileBuf, offset, stringLen);
            loadNotes.AddI("Found NOTE: \u201c" + note + "\u201d");
        }

        /// <summary>
        /// Parses the blocks out of the file.
        /// </summary>
        private static Dictionary<string, Block> LoadBlocks(byte[] fileBuf, Notes loadNotes) {
            Dictionary<string, Block> blocks = new Dictionary<string, Block>();
            int offset = 0;

            while (offset < fileBuf.Length) {
                int blockStart = offset;
                if (offset + 4 > fileBuf.Length) {
                    loadNotes.AddW("Found " + (fileBuf.Length - offset) + " extra bytes at end");
                    break;
                }

                // Get length of block.  Includes the 4-byte length itself.
                int blockLen = (int)RawData.GetU32LE(fileBuf, offset);
                offset += 4;
                if (blockLen < 4) {
                    loadNotes.AddW("Found invalid block length " + blockLen + " at +" + offset);
                    break;
                } else if (blockStart + blockLen > fileBuf.Length) {
                    loadNotes.AddW("Block " + blocks.Count + " runs off end of file");
                    break;
                }

                if (offset == fileBuf.Length) {
                    loadNotes.AddW("Ran off end of file while getting block name length");
                    break;
                }
                byte nameLen = fileBuf[offset++];
                if (offset + nameLen > fileBuf.Length) {
                    loadNotes.AddW("Ran off end of file while getting block name");
                    break;
                }

                string name = Encoding.ASCII.GetString(fileBuf, offset, nameLen);
                offset += nameLen;

                if (blocks.ContainsKey(name)) {
                    loadNotes.AddW("File contains multiple copies of block '" + name + "'");
                } else {
                    blocks.Add(name, new Block(blockStart, blockLen, name));
                }

                offset = blockStart + blockLen;
            }

            return blocks;
        }
    }
}
