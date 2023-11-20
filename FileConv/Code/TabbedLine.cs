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

namespace FileConv.Code {
    /// <summary>
    /// Helper class for assembly sources.  This holds a line of output in a buffer, so that
    /// we can tab to specific character positions.
    /// </summary>
    internal class TabbedLine {
        private StringBuilder mLineBuf = new StringBuilder(80);

        public int Length => mLineBuf.Length;

        private int[] mTabStops;


        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="tabStops">List of tab stops.</param>
        public TabbedLine(int[] tabStops) {
            mTabStops = tabStops;
        }

        /// <summary>
        /// Clears the contents of the line buffer.
        /// </summary>
        public void Clear() {
            mLineBuf.Clear();
        }

        /// <summary>
        /// Appends a single character to the line buffer.
        /// </summary>
        /// <param name="ch">Character to add.</param>
        public void Append(char ch) {
            mLineBuf.Append(ch);
        }

        /// <summary>
        /// Appends a single character to the line buffer, converting it to printable form.
        /// </summary>
        /// <param name="ch">Character to add.</param>
        public void AppendPrintable(char ch) {
            mLineBuf.Append(ASCIIUtil.MakePrintable(ch));
        }

        /// <summary>
        /// Appends a string to the line buffer.
        /// </summary>
        /// <param name="str">String to add.</param>
        public void Append(string str) {
            mLineBuf.Append(str);
        }

        /// <summary>
        /// Appends spaces so that the next character will be output at the position specified
        /// by the specified tab stop.
        /// </summary>
        /// <param name="tabIndex">Index of tab to advance to.</param>
        public void Tab(int tabIndex) {
            if (tabIndex < 0 || tabIndex >= mTabStops.Length) {
                Debug.Assert(false, "bad tab index " + tabIndex);
                return;
            }

            int spaceCount = mTabStops[tabIndex] - mLineBuf.Length;
            if (spaceCount <= 0) {
                spaceCount = 1;
            }
            for (int i = 0; i < spaceCount; i++) {
                mLineBuf.Append(' ');
            }
        }

        /// <summary>
        /// Copies the buffered line to the output, then clears the buffer.
        /// </summary>
        /// <param name="output">Text destination.</param>
        public void MoveLineTo(SimpleText output) {
            output.Append(mLineBuf);
            output.AppendLine();
            mLineBuf.Clear();
        }
    }
}
