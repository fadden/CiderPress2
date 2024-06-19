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

        private int mStartIndex;                // index of item in top-left cell (may be padding)
        private int mEndIndex;                  // index of item past bottom-right cell (may be pad)
        private int mNumRows;                   // number of grid rows
        private int mGridLeft;                  // horizontal pixel offset of leftmost grid line
        private int mGridTop;                   // vertical pixel offset of top grid line

        // Constants.  These can become variables.
        private const int LABEL_CHAR_WIDTH = 8;     // 8x8 font width
        private const int LABEL_CHAR_HEIGHT = 8;    // 8x8 font height
        private const int GRID_THICKNESS = 1;       // note: values != 1 are not implemented
        private const int EDGE_PAD = 1;             // extra padding before top/left labels

        /// <summary>
        /// Builder class, so we don't have to call a constructor with tons of arguments.
        /// </summary>
        public class Builder {
            public Builder() { }

            public ImageGrid Create() {
                Debug.Assert(FirstIndex >= 0 && CellWidth >= 0 && LabelFgColor != 255);
                return new ImageGrid(FirstIndex, NumItems,
                    CellWidth, CellHeight, MaxPerRow,
                    Palette, LabelFgColor, LabelBgColor, BorderColor, PadCellColor,
                    HasLeftLabels, HasTopLabels, LeftLabelIsRow, LabelRadix);
            }

            public int FirstIndex { get; private set; } = -1;
            public int NumItems { get; private set; }
            public int CellWidth { get; private set; } = -1;
            public int CellHeight { get; private set; }
            public int MaxPerRow { get; private set; }
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

        /// <summary>
        /// Internal constructor.
        /// </summary>
        private ImageGrid(int firstIndex, int numItems, int cellWidth, int cellHeight,
                int maxPerRow, Palette8 palette, byte labelFgColor, byte labelBgColor,
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
            mLabelFg = labelFgColor;
            mLabelBg = labelBgColor;
            mBorderColor = borderColor;
            mPadCellColor = padCellColor;
            mHasLeftLabels = hasLeftLabels;
            mHasTopLabels = hasTopLabels;
            mLeftLabelIsRow = leftLabelIsRow;
            mLabelRadix = labelRadix;

            mStartIndex = firstIndex - (firstIndex % mMaxPerRow);
            int lastIndex = firstIndex + numItems - 1;
            mEndIndex = (lastIndex + mMaxPerRow) - (lastIndex % mMaxPerRow);
            mNumRows = (mEndIndex - mStartIndex) / mMaxPerRow;

            // Width must include the left-side label, plus one space for padding, and a full
            // row of cells separated with grid lines.
            int numDigits;
            string labelFmt = (mLabelRadix == 16) ? "x" : "";   // log10 or log16 of max num
            if (leftLabelIsRow) {
                numDigits = mNumRows.ToString(labelFmt).Length;
            } else {
                numDigits = lastIndex.ToString(labelFmt).Length;
            }

            mGridLeft = 0;
            int width = (cellWidth * mMaxPerRow) + (GRID_THICKNESS * (mMaxPerRow + 1));
            if (hasLeftLabels) {
                width += EDGE_PAD + (numDigits * LABEL_CHAR_WIDTH);
                mGridLeft = EDGE_PAD + (numDigits * LABEL_CHAR_WIDTH);
            }

            mGridTop = 0;
            int height = (mNumRows * cellHeight) + (GRID_THICKNESS * (mNumRows + 1));
            if (hasTopLabels) {
                height += EDGE_PAD + LABEL_CHAR_HEIGHT;
                mGridTop = EDGE_PAD + LABEL_CHAR_HEIGHT;
            }

            if (width > Bitmap8.MAX_DIMENSION || height > Bitmap8.MAX_DIMENSION) {
                throw new BadImageFormatException("bitmap would be " + width + "x" + height +
                    ", exceeding maximum allowed size");
            }

            Bitmap = new Bitmap8(width, height);
            Bitmap.SetPalette(palette);
            DrawLabels(numDigits, mEndIndex);
            DrawGrid();
            DrawPadFiller();
        }

        private static readonly char[] sHexDigit = {
            '0', '1', '2', '3', '4', '5', '6', '7',
            '8', '9', 'A', 'B', 'C', 'D', 'E', 'F' };

        /// <summary>
        /// Draws the hex labels across the top and down the left edge.
        /// </summary>
        private void DrawLabels(int numDigits, int endIndex) {
            Debug.Assert(mStartIndex % mMaxPerRow == 0);
            Debug.Assert(endIndex % mMaxPerRow == 0);
            string labelFmt = (mLabelRadix == 16) ? "X" : "";

            if (mHasLeftLabels) {
                // Draw the labels down the left edge.
                int startRow = 0;
                if (mHasTopLabels) {
                    startRow += EDGE_PAD + LABEL_CHAR_HEIGHT;
                }
                Bitmap.FillRect(0, startRow, EDGE_PAD + LABEL_CHAR_WIDTH * numDigits,
                    Bitmap.Height - startRow, mLabelBg);
                startRow += GRID_THICKNESS;

                startRow += (mCellHeight - LABEL_CHAR_HEIGHT) / 2;      // center
                int numRows = (endIndex - mStartIndex) / mMaxPerRow;
                for (int row = 0; row < numRows; row++) {
                    // Generate the label string.
                    string rowLabelStr;
                    if (mLeftLabelIsRow) {
                        rowLabelStr = row.ToString(labelFmt);
                    } else {
                        rowLabelStr = (mStartIndex + row * mMaxPerRow).ToString(labelFmt);
                    }

                    int ypos = startRow + (mCellHeight + GRID_THICKNESS) * row;
                    int xpos = EDGE_PAD;
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
                int startCol = 0;
                if (mHasLeftLabels) {
                    startCol += EDGE_PAD + (numDigits * LABEL_CHAR_WIDTH);
                }
                Bitmap.FillRect(startCol, 0, Bitmap.Width - startCol,
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
                        EDGE_PAD, mLabelFg, mLabelBg);
                }
            }

            if (mHasLeftLabels && mHasTopLabels) {
                // Fill in the top-left corner, if we have both vertical and horizontal labels.
                Bitmap.FillRect(0, 0, EDGE_PAD + (numDigits * LABEL_CHAR_WIDTH) + GRID_THICKNESS,
                    EDGE_PAD + LABEL_CHAR_HEIGHT + GRID_THICKNESS, mLabelBg);
            }
        }

        /// <summary>
        /// Draws a grid around the cells.
        /// </summary>
        private void DrawGrid() {
            int xc = mGridLeft;
            int yc = mGridTop;

            for (int col = 0; col <= mMaxPerRow; col++) {
                DrawVerticalGridLine(xc + col * (mCellWidth + GRID_THICKNESS));
            }
            for (int row = 0; row <= mNumRows; row++) {
                DrawHorizontalGridLine(yc + row * (mCellHeight + GRID_THICKNESS));
            }

            // Set the bottom-right pixel.
            Bitmap.SetPixelIndex(Bitmap.Width - 1, Bitmap.Height - 1, mBorderColor);
        }

        private void DrawVerticalGridLine(int xc) {
            Debug.Assert(GRID_THICKNESS == 1);
            int gridHeight = (mCellHeight + GRID_THICKNESS) * mNumRows;
            for (int yc = mGridTop; yc < mGridTop + gridHeight; yc++) {
                // TODO: handle non-1 values of GRID_THICKNESS
                Bitmap.SetPixelIndex(xc, yc, mBorderColor);
            }
        }

        private void DrawHorizontalGridLine(int yc) {
            Debug.Assert(GRID_THICKNESS == 1);
            int gridWidth = (mCellWidth + GRID_THICKNESS) * mMaxPerRow;
            for (int xc = mGridLeft; xc < mGridLeft + gridWidth; xc++) {
                // TODO: handle non-1 values of GRID_THICKNESS
                Bitmap.SetPixelIndex(xc, yc, mBorderColor);
            }
        }

        /// <summary>
        /// Fills in the cells at the start and end that are just there for padding.
        /// </summary>
        private void DrawPadFiller() {
            for (int i = mStartIndex; i < mFirstIndex; i++) {
                DoDrawRect(i, 0, 0, mCellWidth, mCellHeight, mPadCellColor);
            }
            for (int i = mFirstIndex + mNumItems; i < mEndIndex; i++) {
                DoDrawRect(i, 0, 0, mCellWidth, mCellHeight, mPadCellColor);
            }
        }

        /// <summary>
        /// Calculates the pixel position of the specified cell.
        /// </summary>
        private void CalcCellPosn(int cellIndex, out int cellLeft, out int cellTop) {
            int cellCol = (cellIndex - mStartIndex) % mMaxPerRow;
            int cellRow = (cellIndex - mStartIndex) / mMaxPerRow;
            cellLeft = mGridLeft + GRID_THICKNESS + (mCellWidth + GRID_THICKNESS) * cellCol;
            cellTop = mGridTop + GRID_THICKNESS + (mCellHeight + GRID_THICKNESS) * cellRow;
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
