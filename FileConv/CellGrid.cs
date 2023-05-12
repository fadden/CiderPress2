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
using System.Diagnostics.CodeAnalysis;

using CommonUtil;

namespace FileConv {
    /// <summary>
    /// Output class for documents with a grid of cells, such as spreadsheets.
    /// </summary>
    /// <remarks>
    /// <para>We maintain this in a somewhat sparse fashion to reduce memory footprint, but
    /// generally speaking spreadsheets and such will have a concentrated set of data.</para>
    /// </remarks>
    public class CellGrid : IConvOutput {
        private const int MAX_DIM = 256 * 1024;

        /// <summary>
        /// The number of columns in the grid.
        /// </summary>
        public int NumCols { get; private set; }

        /// <summary>
        /// The number of rows in the grid.
        /// </summary>
        public int NumRows { get { return mRowLists.Count; } }

        // IConvOutput
        public Notes Notes { get; } = new Notes();

        /// <summary>
        /// This holds the grid as a list of lists.
        /// </summary>
        private List<List<string?>?> mRowLists = new List<List<string?>?>();


        /// <summary>
        /// Constructor.
        /// </summary>
        public CellGrid() { }

        /// <summary>
        /// Retrieves the value of a cell.
        /// </summary>
        /// <param name="col">Column number (0-N).</param>
        /// <param name="row">Row number (0-N).</param>
        /// <param name="value">Result: cell value, or null if not defined.</param>
        /// <returns>True if an entry is stored for this col/row.</returns>
        public bool TryGetCellValue(int col, int row, [NotNullWhen(true)] out string? value) {
            if (col < 0 || col >= NumCols) {
                throw new ArgumentOutOfRangeException(nameof(col), "invalid, num=" + NumCols);
            }
            if (row < 0 || row >= NumRows) {
                throw new ArgumentOutOfRangeException(nameof(row), "invalid, num=" + NumRows);
            }
            value = null;

            List<string?>? colList = mRowLists[row];
            if (colList == null) {
                return false;
            }
            if (col >= colList.Count) {
                return false;
            }
            value = colList[col];
            return value != null;
        }

        /// <summary>
        /// Sets the value of a specific cell.
        /// </summary>
        /// <param name="col">Column number (0-N).</param>
        /// <param name="row">Row number (0-N).</param>
        /// <param name="value">New cell value.  Pass null to un-set an entry.</param>
        public void SetCellValue(int col, int row, string? value) {
            if (col < 0 || col > MAX_DIM) {
                throw new ArgumentOutOfRangeException(nameof(col), "invalid, max=" + MAX_DIM);
            }
            if (row < 0 || row > MAX_DIM) {
                throw new ArgumentOutOfRangeException(nameof(row), "invalid, max=" + MAX_DIM);
            }

            while (row >= mRowLists.Count) {
                mRowLists.Add(null);
            }
            if (mRowLists[row] == null) {
                mRowLists[row] = new List<string?>();
            }

            List<string?> colList = mRowLists[row]!;
            while (col >= colList.Count) {
                colList.Add(null);
            }
            colList[col] = value;

            if (col >= NumCols) {
                NumCols = col + 1;
            }
        }
    }
}
