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

namespace FileConv.Gfx {
    /// <summary>
    /// Grid of equal-sized images, framed with index values in hex.  Intended for fonts and
    /// sprite atlases.
    /// </summary>
    /// <remarks>
    /// <para>Each line in the grid has a fixed number of cells (say, 16).  Items are indexed, but
    /// the indices don't have to start from zero.  We don't include empty lines, or draw partial
    /// lines.  It's valid for a grid to hold, say, indices $38-$89, in which case it will
    /// provide rows $30 through $80, spanning $30-$8f.  The padding cells are drawn in a
    /// different color.</para>
    /// <para>Some files, like Finder icons, can be very large in one dimension.  It makes sense
    /// to render these as multiple independent grids on the same bitmap, each with their own
    /// set of side labels.  These are called "chunks".</para>
    /// <para>Rendering within a cell is bounds-checked, so we don't throw exceptions even
    /// if we get crazy values.  Instead, errors are noted in the log.  This allows us to
    /// provide best-effort output for partially broken inputs.</para>
    /// <para>The grid cells are initially filled with color 0.</para>
    /// </remarks>
    internal class ImageGrid {
        // Input parameters.
        private int mFirstIndex;                // index of first "real" item
        private int mNumItems;                  // number of items in set
        private int mCellWidth;                 // width of a cell
        private int mCellHeight;                // height of a cell
        private int mMaxPerRow;                 // max number of items in a row
        private int mMaxRowsPerChunk;           // max number of rows in a chunk
        private int mHorizChunkSep;             // horizontal pixel distance between chunks
        private byte mLabelFg;                  // foreground color for labels
        private byte mLabelBg;                  // background color for labels
        private byte mBorderColor;              // grid border line color
        private byte mPadCellColor;             // color for cells that pad the lines out
        private bool mHasLeftLabels;            // if true, draw labels on left
        private bool mHasTopLabels;             // if true, draw labels across top
        private bool mLeftLabelIsRow;           // if true, left labels are *row* num (vs. item)
        private int mLabelRadix;                // label radix, 10 or 16

        /// <summary>
        /// Bitmap that we render into.
        /// </summary>
        public Bitmap8 Bitmap { get; private set; }
        public int CellWidth => mCellWidth;
        public int CellHeight => mCellHeight;

        /// <summary>
        /// One chunk of the grid.
        /// </summary>
        /// <remarks>
        /// Most things only need one chunk.  For Finder icons, which are very tall and narrow,
        /// we want to split large files into multiple grids to avoid blowing out the 4096x4096
        /// limit on the bitmap.
        /// </remarks>
        private class Chunk {
            public int mStartIndex;             // index of item in top-left cell (may be padding)
            public int mEndIndex;               // index of item past bottom-right cell (may be pad)
            public int mNumRows;                // number of grid rows
            public int mLeft;                   // horizontal pixel offset of left edge of chunk
            public int mTop;                    // vertical pixel offet of top edge of chunk
            public int mGridLeft;               // horizontal pixel offset of leftmost grid line
            public int mGridTop;                // vertical pixel offset of top grid line
        }

        private Chunk[] mChunks;

        // Constants.  These can become variables.
        private const int LABEL_CHAR_WIDTH = 8;     // 8x8 font width
        private const int LABEL_CHAR_HEIGHT = 8;    // 8x8 font height
        private const int GRID_THICKNESS = 1;       // note: values != 1 are not implemented
        private const int EDGE_PAD = 1;             // extra padding before top/left labels

        #region Builder

        /// <summary>
        /// Builder class, so we don't have to call a constructor with tons of arguments.
        /// </summary>
        public class Builder {
            public Builder() { }

            public ImageGrid Create() {
                Debug.Assert(FirstIndex >= 0 && CellWidth >= 0 && LabelFgColor != 255);
                return new ImageGrid(FirstIndex, NumItems,
                    CellWidth, CellHeight, MaxPerRow, MaxRowsPerChunk, HorizChunkSep,
                    Palette, LabelFgColor, LabelBgColor, BorderColor, PadCellColor,
                    HasLeftLabels, HasTopLabels, LeftLabelIsRow, LabelRadix);
            }

            public int FirstIndex { get; private set; } = -1;
            public int NumItems { get; private set; }
            public int CellWidth { get; private set; } = -1;
            public int CellHeight { get; private set; }
            public int MaxPerRow { get; private set; }
            public int MaxRowsPerChunk { get; private set; } = int.MaxValue;
            public int HorizChunkSep { get; private set; }
            public Palette8 Palette { get; private set; } = Palette8.EMPTY_PALETTE;
            public byte LabelFgColor { get; private set; } = 255;
            public byte LabelBgColor { get; private set; }
            public byte BorderColor { get; private set; }
            public byte PadCellColor { get; private set; }
            public bool HasLeftLabels { get; private set; }
            public bool HasTopLabels { get; private set; }
            public bool LeftLabelIsRow { get; private set; }
            public int LabelRadix { get; private set; }

            /// <summary>
            /// Sets the geometry entries.  Mandatory
            /// </summary>
            /// <param name="cellWidth">Width of a cell, in pixels.</param>
            /// <param name="cellHeight">Height of a cell, in pixels.</param>
            /// <param name="maxPerRow">Maximum cells per row of the grid.</param>
            public void SetGeometry(int cellWidth, int cellHeight, int maxPerRow) {
                Debug.Assert(cellWidth > 0);
                Debug.Assert(cellHeight > 0);
                Debug.Assert(maxPerRow > 0);
                CellWidth = cellWidth;
                CellHeight = cellHeight;
                MaxPerRow = maxPerRow;
            }

            /// <summary>
            /// Sets the chunk geometry.  Optional.
            /// </summary>
            /// <param name="maxRowsPerChunk">Maximum number of rows we display in each
            ///   chunk.</param>
            /// <param name="horizSep">Horizontal pixel distance between chunks.</param>
            public void SetChunkGeometry(int maxRowsPerChunk, int horizSep) {
                Debug.Assert(maxRowsPerChunk > 0);
                Debug.Assert(horizSep > 0);
                MaxRowsPerChunk = maxRowsPerChunk;
                HorizChunkSep = horizSep;
            }

            /// <summary>
            /// Sets the item range entries.  Mandatory.
            /// </summary>
            /// <param name="firstIndex">Index of first item.</param>
            /// <param name="numItems">Number of items in the list.</param>
            public void SetRange(int firstIndex, int numItems) {
                Debug.Assert(firstIndex >= 0);
                Debug.Assert(numItems > 0);
                FirstIndex = firstIndex;
                NumItems = numItems;
            }

            /// <summary>
            /// Sets the color entries.  Mandatory.
            /// </summary>
            /// <param name="palette">Color palette.</param>
            /// <param name="labelFgColor">Color index to use for label foreground.</param>
            /// <param name="labelBgColor">Color index to use for label background.</param>
            /// <param name="borderColor">Color index to use for grid border lines.</param>
            /// <param name="padCellColor">Color index to use for cells before/after actual.</param>
            public void SetColors(Palette8 palette, byte labelFgColor, byte labelBgColor,
                    byte borderColor, byte padCellColor) {
                Palette = palette;
                LabelFgColor = labelFgColor;
                LabelBgColor = labelBgColor;
                BorderColor = borderColor;
                PadCellColor = padCellColor;

                if (labelFgColor == labelBgColor ||
                        palette.GetColor(labelFgColor) == palette.GetColor(labelBgColor)) {
                    Debug.Assert(false, "label foreground/background colors are the same");
                }
            }

            /// <summary>
            /// Configures the labels.  Optional.
            /// </summary>
            /// <param name="hasLeftLabels">If true, show labels down left side.</param>
            /// <param name="hasTopLabels">If true, show labels across top.</param>
            /// <param name="leftLabelIsRow">If true, the left label is the row number
            ///   rather than the item number.</param>
            /// <param name="labelRadix">Label radix, must be 10 or 16.</param>
            public void SetLabels(bool hasLeftLabels, bool hasTopLabels, bool leftLabelIsRow,
                    int labelRadix) {
                Debug.Assert(labelRadix == 10 || labelRadix == 16);
                HasLeftLabels = hasLeftLabels;
                HasTopLabels = hasTopLabels;
                LeftLabelIsRow = leftLabelIsRow;
                LabelRadix = labelRadix;
            }
        }

        #endregion Builder

        /// <summary>
        /// Internal constructor.
        /// </summary>
        private ImageGrid(int firstIndex, int numItems, int cellWidth, int cellHeight,
                int maxPerRow, int maxRowsPerChunk, int horizChunkSep,
                Palette8 palette, byte labelFgColor, byte labelBgColor,
                byte borderColor, byte padCellColor, bool hasLeftLabels, bool hasTopLabels,
                bool leftLabelIsRow, int labelRadix) {
            // If the items are smaller than the frame labels, increase the size.
            if (cellWidth < LABEL_CHAR_WIDTH) {
                cellWidth = LABEL_CHAR_WIDTH;
            }
            if (cellHeight < LABEL_CHAR_HEIGHT) {
                cellHeight = LABEL_CHAR_HEIGHT;
            }

            mFirstIndex = firstIndex;
            mNumItems = numItems;
            mCellWidth = cellWidth;
            mCellHeight = cellHeight;
            mMaxPerRow = maxPerRow;
            mMaxRowsPerChunk = maxRowsPerChunk;
            mHorizChunkSep = horizChunkSep;
            mLabelFg = labelFgColor;
            mLabelBg = labelBgColor;
            mBorderColor = borderColor;
            mPadCellColor = padCellColor;
            mHasLeftLabels = hasLeftLabels;
            mHasTopLabels = hasTopLabels;
            mLeftLabelIsRow = leftLabelIsRow;
            mLabelRadix = labelRadix;

            // First index, padded to fill row.
            int startIndex = firstIndex - (firstIndex % mMaxPerRow);
            // Last index (inclusive), not padded.
            int lastIndex = firstIndex + numItems - 1;
            // Last index (exclusive), padded to fill row.
            int endIndex = (lastIndex + mMaxPerRow) - (lastIndex % mMaxPerRow);
            // Total number of rows needed.
            int totalNumRows = (endIndex - startIndex) / mMaxPerRow;
            // Number of chunks.
            if (mMaxRowsPerChunk > totalNumRows) {
                mMaxRowsPerChunk = totalNumRows;
            }
            int numChunks = 1 + (totalNumRows - 1) / mMaxRowsPerChunk;
            Debug.Assert(numChunks > 0);

            // Width must include the left-side label, plus one space for padding, and a full
            // row of cells separated with grid lines.
            int numDigits;
            string labelFmt = (mLabelRadix == 16) ? "x" : "";   // log10 or log16 of max num
            if (leftLabelIsRow) {
                numDigits = totalNumRows.ToString(labelFmt).Length;
            } else {
                numDigits = lastIndex.ToString(labelFmt).Length;
            }

            int totalWidth = 0;
            int totalHeight = 0;
            int leftOffset = 0;
            int topOffset = 0;

            // Create chunks.
            mChunks = new Chunk[numChunks];
            for (int chn = 0; chn < numChunks; chn++) {
                Chunk chunk = mChunks[chn] = new Chunk();
                if (chn != 0) {
                    leftOffset += mHorizChunkSep;
                }

                chunk.mStartIndex = startIndex;
                chunk.mEndIndex = startIndex + mMaxRowsPerChunk * mMaxPerRow;
                chunk.mNumRows = (chunk.mEndIndex - chunk.mStartIndex) / mMaxPerRow;
                // Last chunk may have a partial set of rows.
                if (mMaxRowsPerChunk * (chn + 1) > totalNumRows) {
                    Debug.Assert(chn > 0);
                    chunk.mNumRows = totalNumRows - mMaxRowsPerChunk * chn;
                    // Adjust the end index so we don't draw padding cells in unused rows.
                    chunk.mEndIndex = startIndex + chunk.mNumRows * mMaxPerRow;
                }

                chunk.mLeft = chunk.mGridLeft = leftOffset;
                int width = (cellWidth * mMaxPerRow) + (GRID_THICKNESS * (mMaxPerRow + 1));
                if (hasLeftLabels) {
                    width += EDGE_PAD + (numDigits * LABEL_CHAR_WIDTH);
                    chunk.mGridLeft += EDGE_PAD + (numDigits * LABEL_CHAR_WIDTH);
                }

                chunk.mTop = chunk.mGridTop = topOffset;
                int height = (chunk.mNumRows * cellHeight) + (GRID_THICKNESS * (chunk.mNumRows + 1));
                if (hasTopLabels) {
                    height += EDGE_PAD + LABEL_CHAR_HEIGHT;
                    chunk.mGridTop += EDGE_PAD + LABEL_CHAR_HEIGHT;
                }

                if (chn != 0) {
                    totalWidth += mHorizChunkSep;
                }
                totalWidth += width;
                totalHeight = Math.Max(totalHeight, height);

                leftOffset += width;

                startIndex += chunk.mEndIndex - chunk.mStartIndex;
            }

            // Create bitmap.
            if (totalWidth > Bitmap8.MAX_DIMENSION || totalHeight > Bitmap8.MAX_DIMENSION) {
                throw new BadImageFormatException("bitmap would be " + totalWidth + "x" +
                    totalHeight + ", exceeding maximum allowed size");
            }
            Bitmap = new Bitmap8(totalWidth, totalHeight);
            Bitmap.SetPalette(palette);

            // Draw labels, grids, and filler for each chunk.
            foreach (Chunk chunk in mChunks) {
                DrawLabels(chunk, numDigits);
                DrawGrid(chunk);
                DrawPadFiller(chunk);
            }
        }

        private static readonly char[] sHexDigit = {
            '0', '1', '2', '3', '4', '5', '6', '7',
            '8', '9', 'A', 'B', 'C', 'D', 'E', 'F' };

        /// <summary>
        /// Draws the hex labels across the top and down the left edge.
        /// </summary>
        private void DrawLabels(Chunk chunk, int numDigits) {
            Debug.Assert(chunk.mStartIndex % mMaxPerRow == 0);
            Debug.Assert(chunk.mEndIndex % mMaxPerRow == 0);
            string labelFmt = (mLabelRadix == 16) ? "X" : "";

            int labelBase = chunk.mStartIndex / mMaxPerRow;

            if (mHasLeftLabels) {
                // Draw the labels down the left edge.
                int startLine = chunk.mTop;
                if (mHasTopLabels) {
                    startLine += EDGE_PAD + LABEL_CHAR_HEIGHT;
                }
                Bitmap.FillRect(chunk.mLeft, startLine, EDGE_PAD + LABEL_CHAR_WIDTH * numDigits,
                    chunk.mNumRows * (mCellHeight + GRID_THICKNESS) + 1, mLabelBg);
                startLine += GRID_THICKNESS;

                startLine += (mCellHeight - LABEL_CHAR_HEIGHT) / 2;      // center
                for (int row = 0; row < chunk.mNumRows; row++) {
                    // Generate the label string.
                    string rowLabelStr;
                    if (mLeftLabelIsRow) {
                        rowLabelStr = (labelBase + row).ToString(labelFmt);
                    } else {
                        rowLabelStr = (chunk.mStartIndex + row * mMaxPerRow).ToString(labelFmt);
                    }

                    int ypos = startLine + (mCellHeight + GRID_THICKNESS) * row;
                    int xpos = chunk.mLeft + EDGE_PAD;
                    foreach (char ch in rowLabelStr) {
                        Bitmap.DrawChar(ch, xpos, ypos, mLabelFg, mLabelBg);
                        xpos += LABEL_CHAR_WIDTH;
                    }
                }
            }

            if (mHasTopLabels) {
                // Draw the labels across the top edge.
                // NOTE: this only works for ITEMS_PER_ROW <= 16.  To handle more we'd want to
                // stack the label vertically, so we don't force the cells to be wider.
                int startCol = chunk.mLeft;
                if (mHasLeftLabels) {
                    startCol += EDGE_PAD + (numDigits * LABEL_CHAR_WIDTH);
                }
                Bitmap.FillRect(startCol, chunk.mTop, Bitmap.Width - startCol,
                    EDGE_PAD + LABEL_CHAR_HEIGHT, mLabelBg);
                startCol += GRID_THICKNESS;

                startCol += (mCellWidth - LABEL_CHAR_WIDTH) / 2;        // center
                for (int col = 0; col < Math.Min(mMaxPerRow, 16); col++) {
                    char labelCh;
                    if (col < sHexDigit.Length) {
                        labelCh = sHexDigit[col];
                    } else {
                        labelCh = '*';
                    }
                    Bitmap.DrawChar(labelCh, startCol + col * (mCellWidth + GRID_THICKNESS),
                        chunk.mTop + EDGE_PAD, mLabelFg, mLabelBg);
                }
            }

            if (mHasLeftLabels && mHasTopLabels) {
                // Fill in the top-left corner, if we have both vertical and horizontal labels.
                Bitmap.FillRect(chunk.mLeft, chunk.mTop,
                    EDGE_PAD + (numDigits * LABEL_CHAR_WIDTH) + GRID_THICKNESS,
                    EDGE_PAD + LABEL_CHAR_HEIGHT + GRID_THICKNESS, mLabelBg);
            }
        }

        /// <summary>
        /// Draws a grid around the cells.
        /// </summary>
        private void DrawGrid(Chunk chunk) {
            for (int col = 0; col <= mMaxPerRow; col++) {
                DrawVerticalGridLine(chunk, chunk.mGridLeft + col * (mCellWidth + GRID_THICKNESS));
            }
            for (int row = 0; row <= chunk.mNumRows; row++) {
                DrawHorizontalGridLine(chunk,chunk.mGridTop + row * (mCellHeight + GRID_THICKNESS));
            }

            // Set the bottom-right pixel.
            int xc = chunk.mGridLeft + mMaxPerRow * (mCellWidth + GRID_THICKNESS);
            int yc = chunk.mGridTop + chunk.mNumRows * (mCellHeight + GRID_THICKNESS);
            Bitmap.SetPixelIndex(xc, yc, mBorderColor);
        }

        private void DrawVerticalGridLine(Chunk chunk, int xc) {
            Debug.Assert(GRID_THICKNESS == 1);
            int gridHeight = (mCellHeight + GRID_THICKNESS) * chunk.mNumRows;
            for (int yc = chunk.mGridTop; yc < chunk.mGridTop + gridHeight; yc++) {
                // TODO: handle non-1 values of GRID_THICKNESS
                Bitmap.SetPixelIndex(xc, yc, mBorderColor);
            }
        }

        private void DrawHorizontalGridLine(Chunk chunk,int yc) {
            Debug.Assert(GRID_THICKNESS == 1);
            int gridWidth = (mCellWidth + GRID_THICKNESS) * mMaxPerRow;
            for (int xc = chunk.mGridLeft; xc < chunk.mGridLeft + gridWidth; xc++) {
                // TODO: handle non-1 values of GRID_THICKNESS
                Bitmap.SetPixelIndex(xc, yc, mBorderColor);
            }
        }

        /// <summary>
        /// Fills in the cells at the start and end that are just there for padding.
        /// </summary>
        private void DrawPadFiller(Chunk chunk) {
            for (int i = chunk.mStartIndex; i < mFirstIndex; i++) {
                DoDrawRect(i, 0, 0, mCellWidth, mCellHeight, mPadCellColor);
            }
            for (int i = mFirstIndex + mNumItems; i < chunk.mEndIndex; i++) {
                DoDrawRect(i, 0, 0, mCellWidth, mCellHeight, mPadCellColor);
            }
        }

        /// <summary>
        /// Calculates the pixel position of the specified cell.
        /// </summary>
        private void CalcCellPosn(int cellIndex, out int cellLeft, out int cellTop) {
            int chunkNum = (cellIndex / mMaxPerRow) / mMaxRowsPerChunk;
            Debug.Assert(chunkNum < mChunks.Length);
            Chunk chunk = mChunks[chunkNum];
            int cellCol = (cellIndex - chunk.mStartIndex) % mMaxPerRow;
            int cellRow = (cellIndex - chunk.mStartIndex) / mMaxPerRow;
            cellLeft = chunk.mGridLeft + GRID_THICKNESS + (mCellWidth + GRID_THICKNESS) * cellCol;
            cellTop = chunk.mGridTop + GRID_THICKNESS + (mCellHeight + GRID_THICKNESS) * cellRow;
        }

        /// <summary>
        /// Draws a pixel in a cell.
        /// </summary>
        /// <param name="cellIndex">Index of cell.</param>
        /// <param name="xc">X coordinate.</param>
        /// <param name="yc">Y coordinate.</param>
        /// <param name="color">Color index.</param>
        public void DrawPixel(int cellIndex, int xc, int yc, byte color) {
            // Verify if falls within cell bounds.  Make some noise but don't throw an exception.
            if (xc < 0 || xc >= mCellWidth || yc < 0 || yc >= mCellHeight) {
                Bitmap.Notes.AddE("Invalid pixel draw request: index=" + cellIndex +
                    " xc=" + xc + " yc=" + yc);
                return;
            }
            if (cellIndex < mFirstIndex || cellIndex >= mFirstIndex + mNumItems) {
                Bitmap.Notes.AddE("Invalid pixel draw request: index=" + cellIndex);
                return;
            }

            // This has to do some math for every pixel.  We could reduce this by either
            // caching the results from the previous call, or returning a "draw delegate" that
            // has the results embedded (at the cost of one allocation per cell).  Not really
            // concerned about performance here though.
            CalcCellPosn(cellIndex, out int cellLeft, out int cellTop);
            Bitmap.SetPixelIndex(cellLeft + xc, cellTop + yc, color);
        }

        /// <summary>
        /// Fills all pixels in a cell with the specified color.
        /// </summary>
        /// <param name="cellIndex">Index of cell.</param>
        /// <param name="color">Color index.</param>
        public void FillCell(int cellIndex, byte color) {
            DrawRect(cellIndex, 0, 0, mCellWidth, mCellHeight, color);
        }

        /// <summary>
        /// Draws a filled rectangle in a cell.
        /// </summary>
        /// <param name="cellIndex">Index of cell.</param>
        /// <param name="left">Left coordinate.</param>
        /// <param name="top">Top coordinate.</param>
        /// <param name="width">Rect width.</param>
        /// <param name="height">Rect height.</param>
        /// <param name="color">Color index.</param>
        public void DrawRect(int cellIndex, int left, int top, int width, int height, byte color) {
            if (left < 0 || top < 0 ||
                    width < 0 || left + width > mCellWidth ||
                    height < 0 || top + height > mCellHeight) {
                Bitmap.Notes.AddE("Invalid rect draw request: index=" + cellIndex +
                    " l=" + left + " t=" + top + " w=" + width + " h=" + height);
                return;
            }
            if (cellIndex < mFirstIndex || cellIndex >= mFirstIndex + mNumItems) {
                Bitmap.Notes.AddE("Invalid pixel draw request: index=" + cellIndex);
                return;
            }
            if (width == 0 || height == 0) {
                return;     // nothing to do
            }
            DoDrawRect(cellIndex, left, top, width, height, color);
        }

        // Do the drawing.  Can be called directly if we want to avoid the cell range checks,
        // which disallow drawing in pad cells.
        private void DoDrawRect(int cellIndex, int left, int top, int width, int height,
                byte color) {
            CalcCellPosn(cellIndex, out int cellLeft, out int cellTop);
            Debug.Assert(cellLeft + mCellWidth <= Bitmap.Width);
            Debug.Assert(cellTop + mCellHeight <= Bitmap.Height);
            Bitmap.FillRect(cellLeft + left, cellTop + top, width, height, color);
        }
    }
}
