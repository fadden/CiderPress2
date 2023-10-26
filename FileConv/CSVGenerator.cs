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
using System.Text;

namespace FileConv {
    /// <summary>
    /// Comma-Separated Value file generator.  There is no true standard for CSV, but
    /// <see href="https://datatracker.ietf.org/doc/html/rfc4180">RFC 4180</see> does the job.
    /// See also <see href="https://en.wikipedia.org/wiki/Comma-separated_values"/>.
    /// </summary>
    public static class CSVGenerator {
        public const string FILE_EXT = ".csv";

        /// <summary>
        /// Generates a CSV document.
        /// </summary>
        /// <param name="cells">Cell source.</param>
        /// <param name="outStream">Output stream.  The stream will be left open.</param>
        public static void Generate(CellGrid cells, Stream outStream) {
            Encoding enc = new UTF8Encoding();  // no BOM
            using (StreamWriter txtOut = new StreamWriter(outStream, enc, -1, true)) {
                StringBuilder sb = new StringBuilder();
                for (int row = 0; row < cells.NumRows; row++) {
                    sb.Clear();
                    AppendRow(cells, row, sb);
                    txtOut.Write(sb);
                    txtOut.Write("\r\n");   // RFC 4180 says CRLF for everyone
                }
            }
        }

        /// <summary>
        /// Generates a CSV document in a string.
        /// </summary>
        /// <param name="cells">Cell source.</param>
        /// <param name="showRowNum">Prefix each row with the row number.  Useful for display
        ///   but yields an incorrect CSV file.</param>
        /// <param name="sb">Output StringBuilder.</param>
        public static void GenerateString(CellGrid cells, bool showRowNum, StringBuilder sb) {
            for (int row = 0; row < cells.NumRows; row++) {
                if (showRowNum) {
                    sb.Append(row).Append(':');
                }
                AppendRow(cells, row, sb);
                sb.AppendLine();        // use host EOL
            }
        }

        /// <summary>
        /// Appends a row of cells.
        /// </summary>
        /// <param name="cells">Cell grid object.</param>
        /// <param name="row">Row index.</param>
        /// <param name="sb">StringBuilder to append the output to.</param>
        private static void AppendRow(CellGrid cells, int row, StringBuilder sb) {
            for (int col = 0; col < cells.NumCols; col++) {
                if (col != 0) {
                    sb.Append(',');
                }
                if (cells.TryGetCellValue(col, row, out string? value)) {
                    AppendEscapedValue(value, sb);
                }
            }
        }

        /// <summary>
        /// Appends a value to the StringBuilder, applying CSV escaping if necessary.
        /// </summary>
        /// <param name="value">Value to output.</param>
        /// <param name="sb">String builder with row under construction.</param>
        private static void AppendEscapedValue(string value, StringBuilder sb) {
            // Nothing for us to do with an empty string.
            if (string.IsNullOrEmpty(value)) {
                return;
            }

            // Check if we need to escape this cell.  If it contains a comma, double-quote,
            // CR, or LF, we must escape it.  Some implementations will fail to interpret
            // the data correctly if it has leading or trailing spaces, so we also quote those.
            bool needQuote = false;
            if (value[0] == ' ' || value[value.Length - 1] == ' ') {
                needQuote = true;
            } else {
                foreach (char ch in value) {
                    if (ch == ',' || ch == '"' || ch == '\r' || ch == '\n') {
                        needQuote = true;
                        break;
                    }
                }
            }

            // If it doesn't need quoting, just throw it on the end.
            if (!needQuote) {
                sb.Append(value);
                return;
            }

            // Quote the entire value.  We need to escape any double-quotes that appear.
            sb.Append('"');
            foreach (char ch in value) {
                if (ch == '"') {
                    sb.Append("\"\"");
                } else {
                    sb.Append(ch);
                }
            }
            sb.Append('"');
        }
    }
}
