/*
 * Copyright 2024 faddenSoft
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
    /// <summary>
    /// Render the icons from an Apple IIgs Finder icon file in a grid.  Each entry has four
    /// images: full-size, full-size mask, small, and small mask.  We can generate a grid with
    /// one icon per line, four columns across.
    /// </summary>
    public class GSFinderIcon : Converter {
        public const string TAG = "gsicon";
        public const string LABEL = "Apple IIgs Finder Icons File";
        public const string DESCRIPTION =
            "Converts an Apple IIgs Finder Icons File to a bitmap grid.  Small and large " +
            "versions are shown, with their masks.  Pixels are drawn with the standard " +
            "640-mode color palette.";
        public const string DISCRIMINATOR = "ProDOS ICN ($CA).";
        public override string Tag => TAG;
        public override string Label => LABEL;
        public override string Description => DESCRIPTION;
        public override string Discriminator => DISCRIMINATOR;

        private static byte COLOR_FRAME = (byte)QuickDrawII.sStdColor640.Length;
        private static byte COLOR_BKGND = (byte)(QuickDrawII.sStdColor640.Length + 1);
        private static byte COLOR_BLACK = (byte)(QuickDrawII.sStdColor640.Length + 2);
        private static byte COLOR_WHITE = (byte)(QuickDrawII.sStdColor640.Length + 3);
        private static readonly Palette8 ICON_PALETTE = CreatePalette();

        private static Palette8 CreatePalette() {
            // Use the 640-mode palette, since icons are destined to appear in the Finder.
            // We want to add a couple of entries for the grid frame and background.
            Debug.Assert(COLOR_FRAME != 0);     // define constants before declaring palette
            int[] colors = new int[QuickDrawII.sStdColor640.Length + 4];
            Array.Copy(QuickDrawII.sStdColor640, colors, QuickDrawII.sStdColor640.Length);
            colors[COLOR_FRAME] = ConvUtil.MakeRGB(0x00, 0xb6, 0xd7);       // tech blue
            colors[COLOR_BKGND] = ConvUtil.MakeRGB(0x40, 0x50, 0x50);       // dark gray
            colors[COLOR_BLACK] = ConvUtil.MakeRGB(0x00, 0x00, 0x00);
            colors[COLOR_WHITE] = ConvUtil.MakeRGB(0xff, 0xff, 0xff);
            return new Palette8("std640+", colors);
        }

        /// <summary>
        /// File header.
        /// </summary>
        private class IconFile {
            public uint IBlkNext { get; private set; }
            public ushort IBlkID { get; private set; }
            public uint IBlkPath { get; private set; }
            public byte[] IBlkName { get; private set; } = new byte[16];
            public List<IconDataRec> IconRecords = new List<IconDataRec>();

            public string BlkNameStr { get; private set; } = string.Empty;

            private const int BASE_LENGTH = 26;

            public bool Load(byte[] buf, ref int offset, Notes notes) {
                if (offset + BASE_LENGTH > buf.Length) {
                    notes.AddE("Ran off end of file reading icon data record");
                    return false;
                }

                int startOffset = offset;
                IBlkNext = RawData.ReadU32LE(buf, ref offset);
                IBlkID = RawData.ReadU16LE(buf, ref offset);
                IBlkPath = RawData.ReadU32LE(buf, ref offset);
                Array.Copy(buf, offset, IBlkName, 0, IBlkName.Length);
                offset += IBlkName.Length;
                Debug.Assert(offset - startOffset == BASE_LENGTH);

                if (IBlkName[0] >= IBlkName.Length) {
                    notes.AddW("Invalid iBlkName length");
                } else {
                    BlkNameStr = Encoding.ASCII.GetString(IBlkName, 1, IBlkName[0]);
                }

                while (true) {
                    IconDataRec newRec = new IconDataRec();
                    if (!newRec.Load(buf, ref offset, notes, out bool endOfList)) {
                        return false;
                    }
                    if (endOfList) {
                        break;
                    }
                    IconRecords.Add(newRec);
                }
                return true;
            }
        }

        /// <summary>
        /// Icon data record, one per icon stored in the file.
        /// </summary>
        private class IconDataRec {
            public ushort IDataLen { get; private set; }
            public byte[] IDataBoss { get; private set; } = new byte[64];
            public byte[] IDataName { get; private set; } = new byte[16];
            public ushort IDataType { get; private set; }
            public ushort IDataAux { get; private set; }
            public IconImageData IDataBig { get; private set; } = new IconImageData();
            public IconImageData IDataSmall { get; private set; } = new IconImageData();

            public string DataBossStr { get; private set; } = string.Empty;
            public string DataNameStr { get; private set; } = string.Empty;

            private const int BASE_LENGTH = 86;

            public bool Load(byte[] buf, ref int offset, Notes notes, out bool endOfList) {
                endOfList = false;
                if (offset + 2 == buf.Length) {
                    // This is likely the end of the list.  Read the data to be sure.
                    ushort check = RawData.GetU16LE(buf, offset);
                    if (check == 0) {
                        endOfList = true;
                        return true;
                    }
                }

                if (offset + BASE_LENGTH > buf.Length) {
                    notes.AddE("Ran off end of file reading icon data record");
                    return false;
                }

                int startOffset = offset;
                IDataLen = RawData.ReadU16LE(buf, ref offset);
                if (IDataLen == 0) {
                    notes.AddW("Found IDataLen==0 (end of list) before end of file");
                    endOfList = true;
                    return true;        // stop now in case it's just a bit of junk at the end
                }
                Array.Copy(buf, offset, IDataBoss, 0, IDataBoss.Length);
                offset += IDataBoss.Length;
                Array.Copy(buf, offset, IDataName, 0, IDataName.Length);
                offset += IDataName.Length;
                IDataType = RawData.ReadU16LE(buf, ref offset);
                IDataAux = RawData.ReadU16LE(buf, ref offset);
                Debug.Assert(offset - startOffset == BASE_LENGTH);

                if (IDataBoss[0] >= IDataBoss.Length) {
                    notes.AddW("Invalid iDataBoss length");
                } else {
                    DataBossStr = Encoding.ASCII.GetString(IDataBoss, 1, IDataBoss[0]);
                }
                if (IDataName[0] >= IDataName.Length) {
                    notes.AddW("Invalid iDataName length");
                } else {
                    DataNameStr = Encoding.ASCII.GetString(IDataName, 1, IDataName[0]);
                }

                if (!IDataBig.Load(buf, ref offset, notes)) {
                    return false;
                }
                if (!IDataSmall.Load(buf, ref offset, notes)) {
                    return false;
                }
                return true;
            }
        }

        /// <summary>
        /// Icon image data, two per icon stored in the file (big / small).
        /// </summary>
        private class IconImageData {
            public ushort IconType { get; private set; }
            public ushort IconSize { get; private set; }
            public ushort IconHeight { get; private set; }
            public ushort IconWidth { get; private set; }
            public byte[] IconImage { get; private set; } = RawData.EMPTY_BYTE_ARRAY;
            public byte[] IconMask { get; private set; } = RawData.EMPTY_BYTE_ARRAY;

            public bool IsBlackAndWhite { get { return (IconType & 0x8000) == 0; } }

            private const int BASE_LENGTH = 8;

            public bool Load(byte[] buf, ref int offset, Notes notes) {
                if (offset + BASE_LENGTH > buf.Length) {
                    notes.AddE("Ran off end of file reading icon image data");
                    return false;
                }

                int startOffset = offset;
                IconType = RawData.ReadU16LE(buf, ref offset);
                IconSize = RawData.ReadU16LE(buf, ref offset);
                IconHeight = RawData.ReadU16LE(buf, ref offset);
                IconWidth = RawData.ReadU16LE(buf, ref offset);
                Debug.Assert(offset - startOffset == BASE_LENGTH);

                if (IconSize == 0 || IconSize > 32767) {
                    notes.AddE("Bad IconSize: " + IconSize);
                    return false;
                }
                if (offset + IconSize * 2 > buf.Length) {
                    notes.AddE("Ran off end of file reading icon image data image/mask");
                    return false;
                }
                if (IconWidth == 0 || IconHeight == 0) {
                    notes.AddE("Found icon with width=" + IconWidth + " height=" + IconHeight);
                    return false;
                }

                IconImage = new byte[IconSize];
                Array.Copy(buf, offset, IconImage, 0, IconSize);
                offset += IconSize;
                IconMask = new byte[IconSize];
                Array.Copy(buf, offset, IconMask, 0, IconSize);
                offset += IconSize;
                return true;
            }
        }


        private GSFinderIcon() { }

        public GSFinderIcon(FileAttribs attrs, Stream? dataStream, Stream? rsrcStream,
                ResourceMgr? resMgr, ConvFlags convFlags, AppHook appHook)
                : base(attrs, dataStream, rsrcStream, resMgr, convFlags, appHook) {
            Applic = TestApplicability();
        }

        protected override Applicability TestApplicability() {
            if (DataStream == null) {
                return Applicability.Not;
            }
            if (FileAttrs.FileType == FileAttribs.FILE_TYPE_ICN) {
                return Applicability.Yes;
            }
            return Applicability.Not;
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

            byte[] fullBuf = new byte[DataStream.Length];
            DataStream.Position = 0;
            DataStream.ReadExactly(fullBuf, 0, (int)DataStream.Length);

            Notes loadNotes = new Notes();
            int offset = 0;
            IconFile iconFile = new IconFile();
            if (!iconFile.Load(fullBuf, ref offset, loadNotes)) {
                return new ErrorText("Unable to parse icon file", loadNotes);
            }
            if (iconFile.IconRecords.Count == 0) {
                return new ErrorText("No icons found in file", loadNotes);
            }

            return RenderIconGrid(iconFile, loadNotes);
        }

        private const int NUM_COLS = 4;

        private static IConvOutput RenderIconGrid(IconFile iconFile, Notes loadNotes) {
            ScanIcons(iconFile, loadNotes, out int maxIconWidth, out int maxIconHeight);

            // One row per icon entry, 4 columns each.
            ImageGrid.Builder bob = new ImageGrid.Builder();
            bob.SetGeometry(maxIconWidth, maxIconHeight, NUM_COLS);
            bob.SetRange(0, iconFile.IconRecords.Count * NUM_COLS);
            bob.SetColors(ICON_PALETTE, COLOR_FRAME, COLOR_BKGND, COLOR_FRAME, COLOR_FRAME);
            bob.SetLabels(hasLeftLabels: true, hasTopLabels: false, leftLabelIsRow: true);
            ImageGrid grid;
            try {
                grid = bob.Create();    // this will throw if bitmap would be too big
            } catch (BadImageFormatException ex) {
                return new ErrorText("Unable to generate grid: " + ex.Message);
            }
            grid.Bitmap.Notes.MergeFrom(loadNotes);

            for (int row = 0; row < iconFile.IconRecords.Count; row++) {
                DrawIconRec(grid, iconFile.IconRecords[row], row);
            }

            return grid.Bitmap;
        }

        /// <summary>
        /// Scans the icons, calculating the maximum width and height seen.  Adds notes with
        /// the file filter info.
        /// </summary>
        private static void ScanIcons(IconFile iconFile, Notes notes,
                out int maxIconWidth, out int maxIconHeight) {
            maxIconWidth = maxIconHeight = 0;

            notes.AddI("Found " + iconFile.IconRecords.Count + " icons, name='" +
                iconFile.BlkNameStr + "'");

            int index = 0;
            foreach (IconDataRec dataRec in iconFile.IconRecords) {
                notes.AddI("#" + index +
                    ": boss='" + dataRec.DataBossStr +
                    "', name='" + dataRec.DataNameStr +
                    "', type=" +
                    (dataRec.IDataType == 0 ? "any" : "$" + dataRec.IDataType.ToString("X2")) +
                    ", aux=" +
                    (dataRec.IDataAux == 0 ? "any" : "$" + dataRec.IDataAux.ToString("X4")) +
                    ", big=" +
                    dataRec.IDataBig.IconWidth + "x" + dataRec.IDataBig.IconHeight +
                    //(dataRec.IDataBig.IsBlackAndWhite ? " (B&W)" : "") +
                    ", small=" +
                    dataRec.IDataSmall.IconWidth + "x" + dataRec.IDataSmall.IconHeight
                    //(dataRec.IDataSmall.IsBlackAndWhite ? " (B&W)" : "")
                    );
                if (dataRec.IDataBig.IconWidth > maxIconWidth) {
                    maxIconWidth = dataRec.IDataBig.IconWidth;
                }
                if (dataRec.IDataSmall.IconWidth > maxIconWidth) {
                    maxIconWidth = dataRec.IDataSmall.IconWidth;
                }
                if (dataRec.IDataBig.IconHeight > maxIconHeight) {
                    maxIconHeight = dataRec.IDataBig.IconHeight;
                }
                if (dataRec.IDataSmall.IconHeight > maxIconHeight) {
                    maxIconHeight = dataRec.IDataSmall.IconHeight;
                }

                index++;
            }
        }

        private static void DrawIconRec(ImageGrid grid, IconDataRec iconRec, int rowIndex) {
            DrawIconPair(grid, iconRec.IDataBig, rowIndex * NUM_COLS);
            DrawIconPair(grid, iconRec.IDataSmall, rowIndex * NUM_COLS + 2);
        }

        private static void DrawIconPair(ImageGrid grid, IconImageData imageData, int index) {
            // TODO: should we do something with imageData.IsBlackAndWhite?
            grid.FillCell(index, COLOR_BKGND);
            DrawIcon(grid, imageData, imageData.IconImage, index, false);
            grid.FillCell(index + 1, COLOR_BKGND);
            DrawIcon(grid, imageData, imageData.IconMask, index + 1, true);
        }

        private static void DrawIcon(ImageGrid grid, IconImageData imageData, byte[] pixels,
                int index, bool isMask) {
            int centerX = (grid.CellWidth - imageData.IconWidth) / 2;
            int centerY = (grid.CellHeight - imageData.IconHeight) / 2;
            //grid.DrawRect(index, centerX, centerY, imageData.IconWidth, imageData.IconHeight,
            //    COLOR_ICON_BG);
            int byteWidth = 1 + (imageData.IconWidth - 1) / 2;
            for (int row = 0; row < imageData.IconHeight; row++) {
                int rowOff = row * byteWidth;
                for (int col = 0; col < imageData.IconWidth; col++) {
                    byte bval = pixels[rowOff + col / 2];
                    if ((col & 0x01) == 0) {
                        // even pixel in high nibble
                        bval >>= 4;
                    } else {
                        // odd pixel in low nibble
                        bval &= 0x0f;
                    }
                    if (isMask) {
                        // Black (zero) pixels are transparent, non-black (any nonzero) pixels
                        // are opaque.  Draw the transparent pixels in white, the opaque pixels
                        // in black.
                        if (bval == 0) {
                            grid.DrawPixel(index, centerX + col, centerY + row, COLOR_WHITE);
                        } else {
                            grid.DrawPixel(index, centerX + col, centerY + row, COLOR_BLACK);
                        }
                    } else {
                        grid.DrawPixel(index, centerX + col, centerY + row, bval);
                    }
                }
            }
        }
    }
}
