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
using System.Text.RegularExpressions;

using CommonUtil;

namespace FileConv {
    /// <summary>
    /// Helper functions for file conversion.
    /// </summary>
    public static class ConvConfig {
        public const string BEST = "best";

        /// <summary>
        /// Processed form of a file conversion specification string.  Includes the converter
        /// tag and name/value pairs for the configuration options.
        /// </summary>
        public class FileConvSpec {
            /// <summary>
            /// Converter tag.
            /// </summary>
            public string Tag { get; private set; }

            /// <summary>
            /// Name/value option pairs.
            /// </summary>
            public Dictionary<string, string> Options { get; private set; }

            /// <summary>
            /// Constructor.  Creates a new object with an empty option list.
            /// </summary>
            /// <param name="tag">Converter tag.</param>
            public FileConvSpec(string tag) {
                Tag = tag;
                Options = new Dictionary<string, string>();
            }

            /// <summary>
            /// Merges the options from another FileConvSpec object into this one.
            /// </summary>
            /// <param name="from">Object to merge from.</param>
            public void MergeSpec(FileConvSpec from) {
                // Not problematic, but it suggests there's a bug.
                Debug.Assert(Tag == from.Tag, "merging mismatched specs");

                foreach (KeyValuePair<string, string> kvp in from.Options) {
                    Options[kvp.Key] = kvp.Value;
                }
            }

            public override string ToString() {
                StringBuilder sb = new StringBuilder();
                sb.Append("[ConvSpec: ");
                sb.Append(Tag);
                foreach (KeyValuePair<string, string> kvp in Options) {
                    sb.Append(',');
                    sb.Append(kvp.Key);
                    sb.Append('=');
                    sb.Append(kvp.Value);
                }
                sb.Append(']');
                return sb.ToString();
            }
        }

        /// <summary>
        /// One converter spec blob.  "convtag[,opt1=val1][,opt2=val2][,...]"
        /// Keys are always lower case, values may be mixed case.
        /// </summary>
        private static readonly Regex sConvSpecRegex = new Regex(CONV_SPEC_PATTERN);
        private const string CONV_SPEC_PATTERN = @"^([a-z0-9]+)((?:,)([a-z0-9]+)=([a-zA-Z0-9]+))*$";

        /// <summary>
        /// Creates a file conversion spec object from a command-line specification string.
        /// </summary>
        /// <remarks>
        /// This does not try to determine whether the tag and option strings match known objects,
        /// or whether the values are valid.  It just verifies basic syntax and extracts
        /// the strings.
        /// </remarks>
        /// <param name="specStr">Specification string, without whitespace.</param>
        /// <returns>Export spec object, or null if parsing failed.</returns>
        public static FileConvSpec? CreateSpec(string specStr) {
            MatchCollection matches = sConvSpecRegex.Matches(specStr);
            if (matches.Count != 1) {
                return null;
            }

            string convTag = matches[0].Groups[1].Value;
            FileConvSpec spec = new FileConvSpec(convTag);

            int optCount = matches[0].Groups[3].Captures.Count;
            for (int i = 0; i < optCount; i++) {
                string name = matches[0].Groups[3].Captures[i].Value;
                string value = matches[0].Groups[4].Captures[i].Value;
                spec.Options[name] = value;
            }
            return spec;
        }

        /// <summary>
        /// The options portion of the converter spec blob.
        /// </summary>
        private static readonly Regex sConvOptsRegex = new Regex(CONV_OPTS_PATTERN);
        private const string CONV_OPTS_PATTERN =
            @"^([a-z0-9]+)=([a-zA-Z0-9]+)((?:,)([a-z0-9]+)=([a-zA-Z0-9]+))*$";

        /// <summary>
        /// Parses an option string, which is a comma-separated list of name=value pairs.
        /// </summary>
        /// <param name="optStr">Option string.</param>
        /// <param name="dict">Output dictionary.  May be pre-loaded with defaults.</param>
        /// <returns>True on success.</returns>
        public static bool ParseOptString(string optStr, Dictionary<string, string> dict) {
            MatchCollection matches = sConvOptsRegex.Matches(optStr);
            if (matches.Count != 1) {
                return false;
            }

            dict[matches[0].Groups[1].Value] = matches[0].Groups[2].Value;
            int optCount = matches[0].Groups[4].Captures.Count;
            for (int i = 0; i < optCount; i++) {
                string name = matches[0].Groups[4].Captures[i].Value;
                string value = matches[0].Groups[5].Captures[i].Value;
                dict[name] = value;
            }

            return true;
        }

        public static string GenerateOptString(Dictionary<string, string> dict) {
            if (dict.Count == 0) {
                return string.Empty;
            }

            // Sort the dictionary by copying it into a SortedList.
            SortedList<string, string> sorted = new SortedList<string, string>(dict.Count);
            foreach (KeyValuePair<string, string> kvp in dict) {
                sorted[kvp.Key] = kvp.Value;
            }

            StringBuilder sb = new StringBuilder();
            foreach (KeyValuePair<string, string> kvp in sorted) {
                if (sb.Length != 0) {
                    sb.Append(',');
                }
                sb.Append(kvp.Key);
                sb.Append('=');
                sb.Append(kvp.Value);
            }
            string result = sb.ToString();
            Debug.Assert(ParseOptString(result, new Dictionary<string, string>()));
            return result;
        }
    }
}
