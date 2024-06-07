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
    /// <para>Each line in the grid has 16 cells.  We don't include empty lines, or draw
    /// partial lines.  It's valid for a grid to hold, say, indices $38-$89, in which case it
    /// will provide rows $30 through $80, spanning $30-$8f.  The padding cells are drawn in
    /// a different color.</para>
    /// <para>Rendering within a cell is bounds-checked, so we don't throw exceptions even
    /// if we get crazy values.  Instead, errors are noted in the log.  This allows us to
    /// provide best-effort output for partially broken inputs.</para>
    /// </remarks>
    internal class ImageGrid {
        // Input parameters.
        private int mFirstIndex;                // index of first "real" item
        private int mNumItems;                  // number of items in set
        private int mCellWidth;                 // width of a cell
        private int mCellHeight;                // height of a cell
        private byte mLabelFg;                  // foreground color for labels
        private byte mLabelBg;                  // background color for labels
        private byte mGridColor;                // grid line color
        private byte mPadCellColor;             // color for cells that pad the lines out

        /// <summary>
        /// Bitmap that we render into.
        /// </summary>
        public Bitmap8 Bitmap { get; private set; }

        private int mStartIndex;                // index of item in top-left cell (may be padding)
        private int mEndIndex;                  // index of item past bottom-right cell (may be pad)
        private int mNumRows;                   // number of grid rows
        private int mGridLeft;                  // horizontal pixel offset of leftmost grid line
        private int mGridTop;                   // vertical pixel offset of top grid line

        private const int LABEL_CHAR_WIDTH = 8;     // 8x8 font width
        private const int LABEL_CHAR_HEIGHT = 8;    // 8x8 font height
        private const int ITEMS_PER_ROW = 16;       // note: labels don't work for larger values
        private const int GRID_THICKNESS = 1;       // note: not fully implemented
        private const int EDGE_PAD = 1;             // extra padding on top/left edges

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="firstIndex">Index of first item.</param>
        /// <param name="numItems">Number of items in the list.</param>
        /// <param name="cellWidth">Width of a cell, in pixels.</param>
        /// <param name="cellHeight">Height of a cell, in pixels.</param>
        /// <param name="palette">Color palette.</param>
        /// <param name="labelFgColor">Color index to use for label foreground.</param>
        /// <param name="labelBgColor">Color index to use for label background.</param>
        /// <param name="gridColor">Color index to use for grid lines.</param>
        /// <param name="padCellColor">Color index to use for cells before/after actual.</param>
        public ImageGrid(int firstIndex, int numItems, int cellWidth, int cellHeight,
                Palette8 palette, byte labelFgColor, byte labelBgColor, byte gridColor,
                byte padCellColor) {
            Debug.Assert(firstIndex >= 0);
            Debug.Assert(numItems > 0);
            Debug.Assert(cellWidth > 0);
            Debug.Assert(cellHeight > 0);

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
            mLabelFg = labelFgColor;
            mLabelBg = labelBgColor;
            mGridColor = gridColor;
            mPadCellColor = padCellColor;

            // Width must include the left-side label, plus one space for padding, and a full
            // row of cells separated with grid lines.
            int lastIndex = firstIndex + numItems - 1;
            int numDigits = lastIndex.ToString("x").Length;     // a/k/a log16(numDigits)
            int width = EDGE_PAD + (numDigits * LABEL_CHAR_WIDTH) + (cellWidth * ITEMS_PER_ROW) +
                (GRID_THICKNESS * (ITEMS_PER_ROW + 1));

            mStartIndex = firstIndex - (firstIndex % ITEMS_PER_ROW);
            mGridLeft = EDGE_PAD + (numDigits * LABEL_CHAR_WIDTH);
            mGridTop = EDGE_PAD + LABEL_CHAR_HEIGHT;

            mEndIndex = (lastIndex + ITEMS_PER_ROW) - (lastIndex % ITEMS_PER_ROW);
            mNumRows = (mEndIndex - mStartIndex) / ITEMS_PER_ROW;
            int height = EDGE_PAD + LABEL_CHAR_HEIGHT + (mNumRows * cellHeight) +
                (GRID_THICKNESS * (mNumRows + 1));

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
            Debug.Assert(mStartIndex % ITEMS_PER_ROW == 0);
            Debug.Assert(endIndex % ITEMS_PER_ROW == 0);

            // Draw the labels down the left edge.
            int startRow = EDGE_PAD + LABEL_CHAR_HEIGHT + GRID_THICKNESS;
            startRow += (mCellHeight - LABEL_CHAR_HEIGHT) / 2;      // center
            int numRows = (endIndex - mStartIndex) / ITEMS_PER_ROW;
            for (int row = 0; row < numRows; row++) {
                int ypos = startRow + (mCellHeight + GRID_THICKNESS) * row;
                int xpos = EDGE_PAD + (numDigits - 1) * LABEL_CHAR_WIDTH;
                int rowLabel = mStartIndex + row * ITEMS_PER_ROW;

                // Draw label digits, right to left.
                for (int dg = 0; dg < numDigits; dg++) {
                    char digit = sHexDigit[rowLabel & 0x0f];
                    Bitmap.DrawChar(digit, xpos, ypos, mLabelFg, mLabelBg);

                    xpos -= LABEL_CHAR_WIDTH;
                    rowLabel >>= 4;
                }
            }

            // Draw the labels across the top edge.
            // NOTE: this only works for ITEMS_PER_ROW <= 16.  To handle more we'd want to
            // stack the label vertically, so we don't force the cells to be wider.
            int startCol = EDGE_PAD + (numDigits * LABEL_CHAR_WIDTH) + GRID_THICKNESS;
            startCol += (mCellWidth - LABEL_CHAR_WIDTH) / 2;        // center
            for (int col = 0; col < ITEMS_PER_ROW; col++) {
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

        /// <summary>
        /// Draws a grid around the cells.
        /// </summary>
        private void DrawGrid() {
            int xc = mGridLeft;
            int yc = mGridTop;

            for (int col = 0; col <= ITEMS_PER_ROW; col++) {
                DrawVerticalGridLine(xc + col * (mCellWidth + GRID_THICKNESS));
            }
            for (int row = 0; row <= mNumRows; row++) {
                DrawHorizontalGridLine(yc + row * (mCellHeight + GRID_THICKNESS));
            }
        }

        private void DrawVerticalGridLine(int xc) {
            Debug.Assert(GRID_THICKNESS == 1);
            int gridHeight = (mCellHeight + GRID_THICKNESS) * mNumRows;
            for (int yc = mGridTop; yc < mGridTop + gridHeight; yc++) {
                // TODO: handle non-1 values of GRID_THICKNESS
                Bitmap.SetPixelIndex(xc, yc, mGridColor);
            }
        }

        private void DrawHorizontalGridLine(int yc) {
            Debug.Assert(GRID_THICKNESS == 1);
            int gridWidth = (mCellWidth + GRID_THICKNESS) * ITEMS_PER_ROW;
            for (int xc = mGridLeft; xc < mGridLeft + gridWidth; xc++) {
                // TODO: handle non-1 values of GRID_THICKNESS
                Bitmap.SetPixelIndex(xc, yc, mGridColor);
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
            int cellCol = (cellIndex - mStartIndex) % ITEMS_PER_ROW;
            int cellRow = (cellIndex - mStartIndex) / ITEMS_PER_ROW;
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

        // Do the drawing.  Can be called directly if we want to avoid the range checks.
        private void DoDrawRect(int cellIndex, int left, int top, int width, int height,
                byte color) {
            CalcCellPosn(cellIndex, out int cellLeft, out int cellTop);
            Debug.Assert(cellLeft + mCellWidth <= Bitmap.Width);
            Debug.Assert(cellTop + mCellHeight <= Bitmap.Height);
            for (int row = 0; row < height; row++) {
                for (int col = 0; col < width; col++) {
                    Bitmap.SetPixelIndex(cellLeft + left + col, cellTop + top + row, color);
                }
            }
        }
    }
}
