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

using CommonUtil;
using DiskArc;

namespace FileConv.Gfx {
    /// <summary>
    /// Applesoft BASIC shape table renderer.
    /// </summary>
    public class ShapeTable : Converter {
        public const string TAG = "ashape";
        public const string LABEL = "Applesoft Shape Table";
        public const string DESCRIPTION =
            "Generates a bitmap grid with the contents of an Applesoft BASIC shape table file.";
        public const string DISCRIMINATOR = "ProDOS BIN or DOS B, with correct structure.";
        public override string Tag => TAG;
        public override string Label => LABEL;
        public override string Description => DESCRIPTION;
        public override string Discriminator => DISCRIMINATOR;

        private const int MAX_FILE_LEN = 32768;     // could fill all of $4000-bfff, in theory

        private static readonly Palette8 SHAPE_PALETTE = new Palette8("Shape",
            new int[] {
                ConvUtil.MakeRGB(0x00, 0x00, 0x00),     // 0=black (cell background)
                ConvUtil.MakeRGB(0xff, 0xff, 0xff),     // 1=white (shape foreground)
                ConvUtil.MakeRGB(0x80, 0x80, 0x80),     // 2=dark gray (frame around cells)
                ConvUtil.MakeRGB(0x30, 0x30, 0xe0),     // 3=blue (hex labels)
        });
        private const int COLOR_BG = 0;
        private const int COLOR_FG = 1;
        private const int COLOR_FRAME = 2;
        private const int COLOR_LABEL = 3;

        private ShapeTable() { }

        public ShapeTable(FileAttribs attrs, Stream? dataStream, Stream? rsrcStream,
                ResourceMgr? resMgr, ConvFlags convFlags, AppHook appHook)
                : base(attrs, dataStream, rsrcStream, resMgr, convFlags, appHook) {
            Applic = TestApplicability();
        }

        protected override Applicability TestApplicability() {
            if (DataStream == null || IsRawDOS) {
                return Applicability.Not;
            }
            if (FileAttrs.FileType != FileAttribs.FILE_TYPE_BIN ||
                    DataStream.Length > MAX_FILE_LEN) {
                return Applicability.Not;
            }

            // It's a relatively small BIN file.  Read it in and see if the contents work.
            byte[] fullBuf = new byte[DataStream.Length];
            DataStream.Position = 0;
            DataStream.ReadExactly(fullBuf, 0, (int)DataStream.Length);
            return EvaluateAsShapeTable(fullBuf);
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

            return RenderShapes(fullBuf);
        }

        /// <summary>
        /// Attempts to determine whether a file's contents look like a valid shape table.
        /// </summary>
        /// <remarks>
        /// <para>These files were often formed by hand, and if somebody didn't feel like
        /// moving bytes around they might have unused entries with bad offsets, or extra
        /// bytes between shapes.  It's valid, though uncommon, for one shape to blend into
        /// the next.  This all works because the Applesoft program can simply choose not to
        /// use the bad entries.</para>
        /// <para>We can tell if something looks like a perfect shape table, but we have
        /// to accept a lot of files in the gray zone.</para>
        /// </remarks>
        /// <param name="buf">Shape table data.</param>
        /// <returns>True if this could be a shape table.</returns>
        private static Applicability EvaluateAsShapeTable(byte[] buf) {
            int shapeCount = buf[0];
            if (shapeCount == 0) {
                // If it's a shape table, it's completely empty.  Reject.
                return Applicability.Not;
            }
            // buf[offset + 1] is officially "unused", but is not required to be zero.  It looks
            // like Shape Mechanic uses it to identify whether a font is normal size (0x01) or
            // double size (0x02).  Ignore it.

            // Test whether the offset table fits in the file.  If it doesn't, there's no room
            // in the file for shape data.  Reject.
            if (shapeCount * 2 >= buf.Length) {
                return Applicability.Not;
            }

            bool allGoodOffsets = true;
            bool allGoodShapes = true;
            bool noOverlaps = true;

            // Create a parallel array to use for checking for overlaps.
            bool[] touched = new bool[buf.Length];
            for (int i = 0; i < shapeCount * 2; i++) {
                touched[i] = true;
            }

            // Scan the shapes.
            for (int shnum = 1; shnum <= shapeCount; shnum++) {
                ushort tblOff = RawData.GetU16LE(buf, shnum * 2);
                if (tblOff < (shapeCount+1) * 2 || tblOff >= buf.Length) {
                    // Entry's data starts in the offset table, or starts off the end.  Could be
                    // a bad file or just a hole in the table.
                    allGoodOffsets = false;
                    continue;
                }

                // See if the shape starts with a nonzero value.  Empty shapes are allowed, but
                // useless, so this suggests that this is not a shape table.
                if (buf[tblOff] == 0x00) {
                    allGoodShapes = false;
                }

                // Check for overlapping shapes.
                for (int i = tblOff; i < buf.Length; i++) {
                    if (touched[i]) {
                        noOverlaps = true;
                    }
                    touched[i] = true;
                    if (buf[i] == 0x00) {
                        break;
                    }
                }
            }

            // Count up the number of unused bytes are at the end of the file.  Hand-made files
            // may well have a few bytes here, but if we have a tiny shape table followed by a
            // significant amount of data, chances are that this isn't really a shape table.
            int unusedEndCount = 0;
            int scanPosn;
            for (scanPosn = buf.Length - 1; scanPosn >= 0; scanPosn--) {
                if (!touched[scanPosn]) {
                    unusedEndCount++;
                } else {
                    break;
                }
            }
            int totalUnused = unusedEndCount;
            for (; scanPosn >= 0; scanPosn--) {
                if (!touched[scanPosn]) {
                    totalUnused++;
                }
            }

            Applicability result;
            int tenPercent = buf.Length / 10;
            int twentyPercent = buf.Length / 5;
            if (allGoodOffsets && allGoodShapes && noOverlaps &&
                    unusedEndCount < 2 && totalUnused < tenPercent) {
                // Just about perfect.
                result = Applicability.Yes;
            } else if (allGoodOffsets && allGoodShapes && noOverlaps &&
                    unusedEndCount < tenPercent && totalUnused < twentyPercent) {
                // Good offsets and shapes, reasonable amount of unused stuff.  The Beagle Bros
                // fonts generally land here because they have a lot of unused space.
                result = Applicability.Probably;
            } else if (allGoodOffsets && noOverlaps && unusedEndCount < 2) {
                // Valid table with some empty entries.
                result = Applicability.Probably;
            } else if (allGoodOffsets && allGoodShapes && unusedEndCount < tenPercent) {
                // Valid table with some overlapping shapes.  Could be unused entries that
                // share a common offset.
                result = Applicability.Maybe;
            } else {
                // Could be a valid table with some weird entries, but more likely it's some
                // other type of file that just happens to have a valid-looking header.
                result = Applicability.ProbablyNot;
            }
            Debug.WriteLine("Shape table eval: gOff=" + allGoodOffsets + " gShp=" + allGoodShapes +
                " noOvr=" + noOverlaps + " unEnd=" + unusedEndCount +
                " unTot=" + totalUnused + " --> " + result);
            return result;
        }

        private class ShapeInfo {
            public int mIndex;
            public int mOffset;
            public int mMinX = 1000;
            public int mMaxX = -1000;
            public int mMinY = 1000;
            public int mMaxY = -1000;

            public int Width => mMaxX - mMinX + 1;
            public int Height => mMaxY - mMinY + 1;
        }

        /// <summary>
        /// Renders all viable shapes into a grid.
        /// </summary>
        private static IConvOutput RenderShapes(byte[] buf) {
            Notes loadNotes = new Notes();

            // Determine how many shapes are actually present, and what the dimensions of the
            // largest shape are.
            List<ShapeInfo> shapes = new List<ShapeInfo>();
            int shapeCount = buf[0];
            int maxWidth = 0;
            int maxHeight = 0;
            for (int shnum = 1; shnum <= shapeCount; shnum++) {
                ushort tblOff = RawData.GetU16LE(buf, shnum * 2);
                if (tblOff < (shapeCount + 1) * 2 || tblOff >= buf.Length) {
                    loadNotes.AddW("Shape #" + shnum +
                        ": invalid table offset $" + tblOff.ToString("x4"));
                    continue;
                }

                ShapeInfo shInfo = new ShapeInfo();
                shInfo.mIndex = shnum;
                shInfo.mOffset = tblOff;
                if (DrawShape(buf, shInfo, null)) {
                    shapes.Add(shInfo);

                    if (shInfo.Width > maxWidth) {
                        maxWidth = shInfo.Width;
                    }
                    if (shInfo.Height > maxHeight) {
                        maxHeight = shInfo.Height;
                    }
                } else {
                    loadNotes.AddW("Unable to draw shape #" + shnum);
                }
            }

            if (shapes.Count == 0 || maxWidth == 0 || maxHeight == 0) {
                return new ErrorText("No viable shapes found");
            }

            int gridCols = shapeCount + 1;
            if (gridCols >= 64) {
                gridCols = 16;
            } else if (gridCols >= 16) {
                gridCols = 8;
            }

            ImageGrid.Builder bob = new ImageGrid.Builder();
            bob.SetGeometry(maxWidth, maxHeight, gridCols);
            bob.SetRange(1, shapeCount);
            bob.SetColors(SHAPE_PALETTE, COLOR_LABEL, COLOR_BG, COLOR_FRAME, COLOR_FRAME);
            bob.SetLabels(hasLeftLabels: true, hasTopLabels: true, leftLabelIsRow: false, 16);
            ImageGrid grid;
            try {
                grid = bob.Create();    // this will throw if bitmap would be too big
            } catch (BadImageFormatException ex) {
                return new ErrorText("Unable to generate grid: " + ex.Message);
            }
            grid.Bitmap.Notes.MergeFrom(loadNotes);

            foreach (ShapeInfo shInfo in shapes) {
                DrawShape(buf, shInfo, grid);
            }
            return grid.Bitmap;
        }

        private delegate void SetPixelFunc(int xc, int yc);

        private static bool DrawShape(byte[] buf, ShapeInfo shInfo, ImageGrid? grid) {
            SetPixelFunc? setPixel = null;
            if (grid != null) {
                setPixel = delegate (int xc, int yc) {
                    grid.DrawPixel(shInfo.mIndex, xc, yc, COLOR_FG);
                };
            }

            int xmin = shInfo.mMinX;
            int xmax = shInfo.mMaxX;
            int ymin = shInfo.mMinY;
            int ymax = shInfo.mMaxY;

            int xc = 0;
            int yc = 0;
            int offset = shInfo.mOffset;
            while (true) {
                if (offset >= buf.Length) {
                    // Shape definition ran off end of buffer.
                    return false;
                }
                byte val = buf[offset++];
                if (val == 0) {
                    if (offset == shInfo.mOffset) {
                        // Found end of shape, but the shape was empty.
                        return false;
                    }
                    break;      // done!
                }

                // "If all the remaining sections of the byte contain only zeroes, then those
                // sections are ignored."  We ignore C if it's zero, and if B and C are both zero
                // then both parts are ignored.
                int abits = val & 0x07;
                int bbits = (val >> 3) & 0x07;
                int cbits = val >> 6;

                DrawVector(abits, ref xc, ref yc, ref xmin, ref xmax, ref ymin, ref ymax, setPixel);
                if (bbits != 0 || cbits != 0) {
                    DrawVector(bbits, ref xc, ref yc, ref xmin, ref xmax, ref ymin, ref ymax,
                        setPixel);
                }
                if (cbits != 0) {
                    DrawVector(cbits, ref xc, ref yc, ref xmin, ref xmax, ref ymin, ref ymax,
                        setPixel);
                }
            }
            shInfo.mMinX = xmin;
            shInfo.mMaxX = xmax;
            shInfo.mMinY = ymin;
            shInfo.mMaxY = ymax;
            return true;
        }

        private static void DrawVector(int bits, ref int xc, ref int yc,
                ref int xmin, ref int xmax, ref int ymin, ref int ymax, SetPixelFunc? func) {
            if ((bits & 0x04) != 0) {
                // Plot point, then update bounds.
                if (func != null) {
                    func(xc - xmin, yc - ymin);     // adjust position to be within bounding box
                }
                if (xmin > xc) {
                    xmin = xc;
                }
                if (xmax < xc) {
                    xmax = xc;
                }
                if (ymin > yc) {
                    ymin = yc;
                }
                if (ymax < yc) {
                    ymax = yc;
                }
            }
            switch (bits & 0x03) {
                case 0x00:  // move up
                    yc--;
                    break;
                case 0x01:  // move right
                    xc++;
                    break;
                case 0x02:  // move down
                    yc++;
                    break;
                case 0x03:  // move left
                    xc--;
                    break;
            }
        }
    }
}
