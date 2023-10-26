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
    /// Text document generator.  Outputs a text file in UTF-8 format with host-specific
    /// end-of-line markers.
    /// </summary>
    public static class TXTGenerator {
        public const string FILE_EXT = ".txt";

        /// <summary>
        /// Generates a host-compatible text file from a text document.
        /// </summary>
        /// <remarks>
        /// <para>I'm not outputting the byte-order mark because it's neither required nor
        /// recommended (https://stackoverflow.com/a/2223926/294248).  In some circumstances
        /// it may be desirable, however, because it can help software recognize that a file is
        /// encoded with UTF-8.</para>
        /// </remarks>
        /// <param name="text">Text source.</param>
        /// <param name="outStream">Output stream.  The stream will be left open.</param>
        public static void Generate(SimpleText text, Stream outStream) {
            Encoding enc = new UTF8Encoding();  // no BOM
            using (StreamWriter txtOut = new StreamWriter(outStream, enc, -1, true)) {
                // Write the StringBuilder directly to the stream.
                txtOut.Write(text.Text);
            }
        }
    }
}
