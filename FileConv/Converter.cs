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

using CommonUtil;
using DiskArc;
using FileConv.Generic;

namespace FileConv {
    /// <summary>
    /// File export converter base class definition.  Export converters are expected to work
    /// equally well with files from disk images, file archives, and the host filesystem.
    /// </summary>
    /// <remarks>
    /// <para>This class does not inherit from IDisposable.  Instances do not own the streams
    /// passed into the constructor.</para>
    /// </remarks>
    public abstract class Converter {
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
        /// Brief description of the characteristics that are used to identify a file as
        /// being appropriate for this converter.
        /// </summary>
        /// <remarks>
        /// <para>If applicable, the file type and auxtype values should appears first, followed
        /// by length limitations and signature information.  For example:</para>
        /// <example>
        /// (SHR image) <c>ProDOS PIC/$0000, 32KB. May be BIN if it also has extension ".PIC" or ".SHR".</c>
        /// (Applesoft) <c>ProDOS BAS, DOS A.</c>
        /// <para>These do not need to be machine-readable.</para>
        /// </example>
        /// </remarks>
        public abstract string Discriminator { get; }

        /// <summary>
        /// Converter applicability ratings, from worst to best.
        /// </summary>
        public enum Applicability {
            Unknown = 0,
            Not,            // this converter is not compatible with the data
            NotSelectable,  // it applies, but shouldn't be shown as an option
            ProbablyNot,    // unlikely to be relevant, but put it on the list anyway
            Always_Hex,     // generic converter, always applicable
            Always_Text,    // generic converter, always applicable
            Maybe,          // could work, probably not the best option
            Probably,       // good option, might be something better
            Yes,            // perfect match
        }

        /// <summary>
        /// Applicability rating.  Used to filter and sort converters.
        /// </summary>
        public Applicability Applic { get; protected set; } = Applicability.Unknown;

        [Flags]
        public enum ConvFlags {
            None = 0,
            IsRawDOS = 0x01,
        }

        /// <summary>
        /// True if the source is a DOS filesystem and the "raw" flag is set.
        /// </summary>
        public bool IsRawDOS { get { return (Flags & ConvFlags.IsRawDOS) != 0; } }

        /// <summary>
        /// Converter option definition.
        /// </summary>
        public class OptionDefinition {
            /// <summary>
            /// Option tag, used for command-line configuration and settings storage.
            /// Lower-case letters and numbers only.
            /// </summary>
            public string OptTag { get; private set; }

            /// <summary>
            /// Option label, used in the GUI interface.
            /// </summary>
            public string OptLabel { get; private set; }

            /// <summary>
            /// Array of tags, for "Multi" item.
            /// </summary>
            public string[]? MultiTags { get; private set; }

            /// <summary>
            /// Array of descriptions, for "Multi" item.
            /// </summary>
            public string[]? MultiDescrs { get; private set; }

            /// <summary>
            /// Option type definition.
            /// </summary>
            public enum OptType { Unknown = 0, Boolean, Multi, IntValue };

            /// <summary>
            /// Option type, for parsing or GUI presentation.
            /// </summary>
            public OptType Type { get; private set; }

            /// <summary>
            /// Default value for option.
            /// </summary>
            public string DefaultVal { get; private set; }

            // Do we want a "Hidden" flag for debugging options that don't get mapped in GUI
            // but are available to CLI?

            /// <summary>
            /// Constructor for everything except "Multi" items.
            /// </summary>
            /// <param name="tag">Option tag.  Lower-case letters and numbers only.</param>
            /// <param name="label">Full name, for display to user.</param>
            /// <param name="type">Option type.</param>
            /// <param name="defaultVal">Default value for option.</param>
            public OptionDefinition(string tag, string label, OptType type,
                    string defaultVal) : this(tag, label, type, defaultVal, null, null)
                { }

            /// <summary>
            /// Constructor for "Multi" items.
            /// </summary>
            /// <param name="tag">Option tag.  Lower-case letters and numbers only.</param>
            /// <param name="label">Full name, for display to user.</param>
            /// <param name="type">Option type.</param>
            /// <param name="defaultVal">Default value for option.</param>
            /// <param name="multiTags">Tags for each multi item.</param>
            /// <param name="multiLabels">UI label for each multi item.</param>
            public OptionDefinition(string tag, string label, OptType type,
                    string defaultVal, string[]? multiTags, string[]? multiLabels) {
                OptTag = tag;
                OptLabel = label;
                Type = type;
                DefaultVal = defaultVal;
                MultiTags = multiTags;
                MultiDescrs = multiLabels;

                if (type == OptType.Multi) {
                    Debug.Assert(multiTags != null && multiLabels != null);
                    Debug.Assert(multiTags.Length == multiLabels.Length);
                } else {
                    Debug.Assert(multiTags == null && multiLabels == null);
                }
            }

            public override string ToString() {
                return "[OptDef: tag=" + OptTag + " name='" + OptLabel + "' type=" + Type +
                    " def=" + DefaultVal + "]";
            }
        }

        /// <summary>
        /// Option definitions.  These describe the options that can be configured by the
        /// application.
        /// </summary>
        public virtual List<OptionDefinition> OptionDefs { get; protected set; } =
            new List<OptionDefinition>();

        /// <summary>
        /// File attributes of the file being converted.
        /// </summary>
        protected FileAttribs FileAttrs { get; set; } = NO_ATTRS;
        private static readonly FileAttribs NO_ATTRS = new FileAttribs();

        /// <summary>
        /// Data fork stream; may be null.
        /// </summary>
        /// <remarks>
        /// <para>If this is coming from a disk image, this will be a DiskFileStream, which has
        /// special handling for sparse files.</para>
        /// </remarks>
        protected Stream? DataStream { get; set; }

        /// <summary>
        /// Resource fork stream; may be null.
        /// </summary>
        protected Stream? RsrcStream { get; set; }

        /// <summary>
        /// Resource manager; will be null if the resource fork stream is null or we were unable
        /// to parse the contents of the fork.
        /// </summary>
        protected ResourceMgr? ResMgr { get; set; }

        /// <summary>
        /// Additional conversion flags.
        /// </summary>
        protected ConvFlags Flags { get; set; }

        protected AppHook? mAppHook;
        protected bool mRsrcAsHex;


        /// <summary>
        /// Nullary constructor, so we can extract converter properties.
        /// </summary>
        protected Converter() { }

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="attrs">File attributes.</param>
        /// <param name="dataStream">Data fork stream; may be null.</param>
        /// <param name="rsrcStream">Resource fork stream; may be null.</param>
        /// <param name="resMgr">Resource manager; may be null.</param>
        /// <param name="convFlags">Conversion flags.</param>
        /// <param name="appHook">Application hook reference.</param>
        public Converter(FileAttribs attrs, Stream? dataStream, Stream? rsrcStream,
                ResourceMgr? resMgr, ConvFlags convFlags, AppHook appHook) {
            FileAttrs = attrs;
            DataStream = dataStream;
            RsrcStream = rsrcStream;
            ResMgr = resMgr;
            Flags = convFlags;
            mAppHook = appHook;

            // Leave Applic set to "unknown".  The derived class needs to call TestApplicability()
            // at the end of its constructor.
        }

        /// <summary>
        /// Determines how applicable this converter is to the input data.
        /// </summary>
        /// <returns>Applicability rating.</returns>
        protected abstract Applicability TestApplicability();

        /// <summary>
        /// Performs conversion with current options.  Data from both forks may be used.
        /// </summary>
        /// <remarks>
        /// <para>The data streams are passed in for the benefit of archive formats, for which the
        /// data must be uncompressed to a temporary file.  It's also useful for MacZip, which
        /// has the data and resource forks in separate entries.</para>
        /// <para>The streams will be seeked to the start by the converter implementation.
        /// It's okay to call this method multiple times.</para>
        /// </remarks>
        /// <param name="options">Configuration options.</param>
        /// <returns>Object with converted data.</returns>
        public abstract IConvOutput ConvertFile(Dictionary<string, string> options);

        /// <summary>
        /// Type of object that the converter is expected to generate.  This will be a subclass
        /// of Converter.
        /// </summary>
        /// <remarks>
        /// <para>Useful for clipboard operations, where we want to know the nature of the output
        /// immediately, but don't actually generate it until necessary.</para>
        /// <para>The actual output may depend on the current settings and the contents of the
        /// file itself.  In the latter case, examining the file may be necessary.</para>
        /// <para>All Bitmap classes return IBitmap.  Document classes that return FancyText
        /// with the "prefer simple" flag set should return SimpleText.</para>
        /// </remarks>
        public abstract Type GetExpectedType(Dictionary<string, string> options);

        /// <summary>
        /// Formats the resource fork, if present.
        /// </summary>
        public IConvOutput? FormatResources(Dictionary<string, string> options) {
            if (RsrcStream == null || RsrcStream.Length == 0) {
                return null;
            }
            if (mRsrcAsHex) {
                // When in hex dump mode, we dump both forks as hex.
                HexDump hex = new HexDump(FileAttrs, RsrcStream, null, ResMgr, Flags, mAppHook!);
                return hex.ConvertFile(options);
            } else if (ResMgr == null) {
                // Resource fork parsing failed, dump as plain text.
                PlainText conv =
                    new PlainText(FileAttrs, RsrcStream, null, ResMgr, Flags, mAppHook!);
                return conv.ConvertFile(options);
            } else {
                // Dump resource fork contents.
                return ResourceFork.FormatResourceFork(ResMgr);
            }
        }

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

        /// <summary>
        /// Parses a boolean option.  Returns the default value if the option is not defined
        /// or is not "true" or "false" (case-insensitive).
        /// </summary>
        public static bool GetBoolOption(Dictionary<string, string> options, string optName,
                bool defaultVal) {
            if (!options.TryGetValue(optName, out string? valueStr)) {
                return defaultVal;
            }
            if (!bool.TryParse(valueStr, out bool value)) {
                return defaultVal;
            }
            return value;
        }

        /// <summary>
        /// Parses a decimal integer option.  Returns the default value if the option is not
        /// defined or is not an integer.
        /// </summary>
        public static int GetIntOption(Dictionary<string, string> options, string optName,
                int defaultVal) {
            if (!options.TryGetValue(optName, out string? valueStr)) {
                return defaultVal;
            }
            if (!int.TryParse(valueStr, out int value)) {
                return defaultVal;
            }
            return value;
        }

        /// <summary>
        /// Parses a string option.  Returns the default value if the option is not defined.
        /// </summary>
        public static string GetStringOption(Dictionary<string, string> options, string optName,
                string defaultVal) {
            if (!options.TryGetValue(optName, out string? value)) {
                return defaultVal;
            }
            return value;
        }

        public override string ToString() {
            return "[" + GetType().Name + " tag=" + Tag + "]";
        }
    }
}
