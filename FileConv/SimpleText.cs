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

using CommonUtil;

namespace FileConv {
    /// <summary>
    /// This represents a text document generated with minimal formatting (line breaks).
    /// </summary>
    /// <remarks>
    /// <para>We assume that the entire output file can be held in memory.  For most things this
    /// is a reasonable assumption, given that we're converting vintage files from hard drive
    /// images that fit comfortably in RAM on a modern system.  Still, some applications may
    /// need to limit the maximum size that they will try to convert.  We could avoid this by
    /// streaming the output to a temporary file, but that's much slower and less convenient.</para>
    /// <para>End-of-line markers use the platform-specific convention.  Add one by calling
    /// <see cref="AppendLine"/> or by appending <see cref="Environment.NewLine"/>.</para>
    /// </remarks>
    public class SimpleText : IConvOutput {
        /// <summary>
        /// Default size of the StringBuilder buffer.
        /// </summary>
        /// <remarks>
        /// <para>StringBuilders are a list of buffers, not a single buffer with contents that get
        /// copied around.  The buffers can become "chunky" as things are appended.  Starting
        /// with a large size can be wasteful for small documents, but for a medium-length
        /// document the default initial size of 16 is too small.</para>
        /// </remarks>
        private const int DEFAULT_SB_SIZE = 32768;

        /// <summary>
        /// Text holder.
        /// </summary>
        /// <remarks>
        /// <para>This may be accessed directly, for reading and writing.  It's not necessary
        /// to use a SimpleText call, though that is preferred in case the API changes.</para>
        /// <para>Accessing individual characters with operator[] in a large document that was
        /// built in small pieces may be O(n) rather than O(1).  If the text was accumulated
        /// without EnsureSize() it may be beneficial to convert to String before walking
        /// through it.</para>
        /// </remarks>
        public StringBuilder Text { get; private set; }

        // IConvOutput
        public Notes Notes { get; } = new Notes();

        /// <summary>
        /// This is set by the converter if the option flags indicate that the "simple" form
        /// is preferred.  This is not referenced internally, and is only useful in conjunction
        /// with "fancy" subclasses.
        /// </summary>
        public bool PreferSimple { get; set; }


        /// <summary>
        /// Default constructor.
        /// </summary>
        public SimpleText() : this(DEFAULT_SB_SIZE) { }

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="lengthGuess">Best guess at the length of the output.</param>
        public SimpleText(int lengthGuess) {
            if (lengthGuess <= 0) {
                throw new ArgumentOutOfRangeException("lengthGuess");
            }
            Text = new StringBuilder(lengthGuess);
        }

        /// <summary>
        /// Constructor for simple messages.
        /// </summary>
        /// <remarks>
        /// The document is not closed after the message is added; more text can be appended
        /// later.  However, this is primarily intended for creating short error messages.
        /// </remarks>
        /// <param name="msg">Message to add.</param>
        public SimpleText(string msg) {
            Text = new StringBuilder(msg);
        }

        //
        // StringBuilder pass-through methods.
        //

        public SimpleText Append(char ch) {
            Text.Append(ch);
            return this;
        }
        public SimpleText Append(string text) {
            Text.Append(text);
            return this;
        }
        public SimpleText Append(int val) {
            Text.Append(val);
            return this;
        }
        public SimpleText Append(StringBuilder sb) {
            Text.Append(sb);
            return this;
        }
        public SimpleText AppendLine(string text) {
            Text.AppendLine(text);
            return this;
        }
        public SimpleText AppendLine() {
            Text.AppendLine();
            return this;
        }
        public SimpleText AppendFormat(string format, params object[] args) {
            Text.AppendFormat(format, args);
            return this;
        }

        public SimpleText AppendPrintable(char val) {
            Text.Append(ASCIIUtil.MakePrintable((char)val));
            return this;
        }
    }
}
