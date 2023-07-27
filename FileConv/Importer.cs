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

using CommonUtil;
using static FileConv.Converter;

namespace FileConv {
    /// <summary>
    /// File import converter base class definition.
    /// </summary>
    /// <remarks>
    /// <para>These may be executed as part of adding files to a file archive, which means they
    /// need to work from an IFileSource.  The conversion may be run more than once.  A single
    /// instance may be used to convert multiple input files.</para>
    /// </remarks>
    public abstract class Importer {
        /// <summary>
        /// Short tag string, used for configuration options.  Must be lower-case letters and
        /// numbers only.
        /// </summary>
        public abstract string Tag { get; }

        /// <summary>
        /// Human-readable label, for use in UI controls like pop-up menus.
        /// </summary>
        public abstract string Label { get; }

        /// <summary>
        /// Verbose description of the converter's purpose and features.  May span multiple
        /// lines, using '\n' as line break.  The full Unicode character set is allowed.
        /// </summary>
        public abstract string Description { get; }

        /// <summary>
        /// True if the converted file will have a data fork.
        /// </summary>
        public bool HasDataFork { get; protected set; } = true;

        /// <summary>
        /// True if the converted file will have a resource fork.
        /// </summary>
        public bool HasRsrcFork { get; protected set; } = false;

        /// <summary>
        /// Option definitions.  These describe the options that can be configured by the
        /// application.
        /// </summary>
        public virtual List<OptionDefinition> OptionDefs { get; protected set; } =
            new List<OptionDefinition>();

        protected AppHook? mAppHook;


        /// <summary>
        /// Nullary constructor, so we can extract importer properties.
        /// </summary>
        protected Importer() { }

        /// <summary>
        /// Constructor.
        /// </summary>
        public Importer(AppHook appHook) {
            mAppHook = appHook;
        }

        /// <summary>
        /// Tests whether the file seems like a good fit, based solely on the filename.
        /// </summary>
        /// <param name="fileName">Filename to test.</param>
        /// <returns>Applicability rating.</returns>
        public abstract Converter.Applicability TestApplicability(string fileName);

        /// <summary>
        /// Strips redundant filename extension from pathname.
        /// </summary>
        /// <param name="fullPath">Full normalized pathname.</param>
        /// <returns>Modified pathname.</returns>
        public virtual string StripExtension(string fullPath) {
            return fullPath;
        }

        /// <summary>
        /// Provides the file type values for the imported file.
        /// </summary>
        /// <param name="proType">Result: ProDOS file type.</param>
        /// <param name="proAux">Result: ProDOS aux type.</param>
        /// <param name="hfsFileType">Result: HFS file type.</param>
        /// <param name="hfsCreator">Result: HFS creator.</param>
        public virtual void GetFileTypes(out byte proType, out ushort proAux,
                out uint hfsFileType, out uint hfsCreator) {
            hfsCreator = hfsFileType = proAux = proType = 0;
        }

        /// <summary>
        /// Estimates the size of the converted stream based on the input stream.  This is not
        /// expected to be accurate; it just needs to be within an order of magnitude, so the
        /// caller can decide if the output can be held in memory.
        /// </summary>
        /// <param name="inStream">Input stream, from host file.</param>
        /// <returns>Vague estimate of generated length, or -1 if no estimate is possible.</returns>
        public virtual long EstimateOutputSize(Stream inStream) {
            return inStream.Length;
        }

        /// <summary>
        /// Converts data from a host file stream to data + resource fork output streams.
        /// </summary>
        /// <remarks>
        /// <para>If only one fork is of interest, pass Stream.Null for the other output
        /// stream.  If this will result in a painful double generation (which is also
        /// possible if compression fails to make a stream shorter), the conversion should
        /// be output to temporary files in memory or on disk, and then those streams used
        /// as the source.</para>
        /// <para>The streams must be positioned by the caller.</para>
        /// </remarks>
        /// <param name="inStream">Input stream, from host file.</param>
        /// <param name="options">Conversion options.</param>
        /// <param name="dataOutStream">Data fork output stream.</param>
        /// <param name="rsrcOutStream">Rsrc fork output stream.</param>
        public abstract void ConvertFile(Stream inStream, Dictionary<string, string> options,
            Stream dataOutStream, Stream rsrcOutStream);

        /// <summary>
        /// Determines whether an option with the specified tag is listed in the definitions.
        /// </summary>
        /// <param name="tag">Tag to find.</param>
        /// <returns>True if the option exists.</returns>
        public bool HasOption(string tag) {
            foreach (OptionDefinition def in OptionDefs) {
                if (def.OptTag == tag) {
                    return true;
                }
            }
            return false;
        }
    }
}
