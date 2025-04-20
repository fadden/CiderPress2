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
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

using AppCommon;
using CommonUtil;
using FileConv;

namespace cp2 {
    /// <summary>
    /// Command-line options and other bits that we need to pass to handlers.
    /// </summary>
    public class ParamsBag {
        /// <summary>
        /// Application hook reference, for general use by the application.
        /// </summary>
        public AppHook AppHook { get; private set; }

        /// <summary>
        /// True if the operating system supports resource forks and extended file attributes.
        /// (This does not indicate whether the target filesystem supports them.)
        /// </summary>
        public static bool HasHostPreservation { get; } =
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX);


        #region Options

        /// <summary>
        /// Option type.  Most options are boolean flags, but a few are name=value.
        /// </summary>
        private enum OptionType {
            Unknown = 0, Bool, String, Depth, Sectors, Preserve, ConfigInt
        }

        /// <summary>
        /// Option definition.  This allows generalized handling of options, as well as
        /// auto-generated "help" output.
        /// </summary>
        private class Option {
            public string Name { get; }
            public OptionType Type { get; }
            public object DefValue { get; }
            public object CurValue { get; set; }

            public Option(string name, OptionType type, object defValue) {
                Name = name;
                Type = type;
                CurValue = DefValue = defValue;
            }
        }

        /// <summary>
        /// Scan depth values.  Deeper depths should have greater numeric values.
        /// </summary>
        public enum ScanDepth {
            Unknown = 0, Shallow, SubVol, Max
        }

        /// <summary>
        /// Option set, indexed by option name.
        /// </summary>
        private Dictionary<string, Option> mOptions = InitOptions();

        private static Option OptAddExt = new Option("add-ext", OptionType.Bool, true);
        private static Option OptClassic = new Option("classic", OptionType.Bool, false);
        private static Option OptCompress = new Option("compress", OptionType.Bool, true);
        private static Option OptConvertDOSText = new Option("convert-dos-text",
            OptionType.Bool, true);
        private static Option OptDebug = new Option("debug", OptionType.Bool, false);
        private static Option OptDepth = new Option("depth", OptionType.Depth, ScanDepth.SubVol);
        private static Option OptExDir = new Option("exdir", OptionType.String, string.Empty);
        private static Option OptFastScan = new Option("fast-scan", OptionType.Bool, false);
        private static Option OptFromADF = new Option("from-adf", OptionType.Bool, true);
        private static Option OptFromAS = new Option("from-as", OptionType.Bool, true);
        private static Option OptFromHost = new Option("from-host", OptionType.Bool, true);
        private static Option OptFromNAPS = new Option("from-naps", OptionType.Bool, true);
        private static Option OptInteractive = new Option("interactive", OptionType.Bool, false);
        private static Option OptLatch = new Option("latch", OptionType.Bool, true);
        private static Option OptMacZip = new Option("mac-zip", OptionType.Bool, true);
        private static Option OptReserveBoot = new Option("reserve-boot", OptionType.Bool, true);
        private static Option OptOverwrite = new Option("overwrite", OptionType.Bool, false);
        private static Option OptPreserve = new Option("preserve", OptionType.Preserve,
            ExtractFileWorker.PreserveMode.None);
        private static Option OptRaw = new Option("raw", OptionType.Bool, false);
        private static Option OptRecurse = new Option("recurse", OptionType.Bool, true);
        private static Option OptSectors = new Option("sectors", OptionType.Sectors, 16);
        private static Option OptSetInt = new Option("set-int", OptionType.ConfigInt, "");
        private static Option OptShowLog = new Option("show-log", OptionType.Bool, false);
        private static Option OptShowNotes = new Option("show-notes", OptionType.Bool, false);
        private static Option OptSkipSimple = new Option("skip-simple", OptionType.Bool, true);
        private static Option OptStripExt = new Option("strip-ext", OptionType.Bool, true);
        private static Option OptStripPaths = new Option("strip-paths", OptionType.Bool, false);
        private static Option OptVerbose = new Option("verbose", OptionType.Bool, true);
        private static Option OptWide = new Option("wide", OptionType.Bool, false);

        // Meta-option.
        private const string FROM_ANY = "from-any";


        /// <summary>
        /// Generates a dictionary of options, indexed by option name, with default values set.
        /// </summary>
        private static Dictionary<string, Option> InitOptions() {
            Dictionary<string, Option> opts = new Dictionary<string, Option> {
                { OptAddExt.Name, OptAddExt },
                { OptClassic.Name, OptClassic },
                { OptCompress.Name, OptCompress },
                { OptConvertDOSText.Name, OptConvertDOSText },
                { OptDebug.Name, OptDebug },
                { OptDepth.Name, OptDepth },
                { OptExDir.Name, OptExDir },
                { OptFastScan.Name, OptFastScan },
                { OptFromADF.Name, OptFromADF },
                { OptFromAS.Name, OptFromAS },
                { OptFromHost.Name, OptFromHost },
                { OptFromNAPS.Name, OptFromNAPS },
                { OptInteractive.Name, OptInteractive },
                { OptLatch.Name, OptLatch },
                { OptMacZip.Name, OptMacZip },
                { OptReserveBoot.Name, OptReserveBoot },
                { OptOverwrite.Name, OptOverwrite },
                { OptPreserve.Name, OptPreserve },
                { OptRaw.Name, OptRaw },
                { OptRecurse.Name, OptRecurse },
                { OptSectors.Name, OptSectors },
                { OptSetInt.Name, OptSetInt },
                { OptShowLog.Name, OptShowLog },
                { OptShowNotes.Name, OptShowNotes },
                { OptSkipSimple.Name, OptSkipSimple },
                { OptStripExt.Name, OptStripExt },
                { OptStripPaths.Name, OptStripPaths },
                { OptVerbose.Name, OptVerbose },
                { OptWide.Name, OptWide },
            };

            if (!HasHostPreservation) {
                // Don't bother checking for native resource forks and HFS attributes on non-OSX
                // systems.  These won't necessarily work on OSX unless the filesystem is HFS+,
                // but they definitely won't work anywhere else (because we don't implement xattr
                // and the system doesn't have /..namedfork/ support), so we can save ourselves
                // the trouble of trying on non-OSX platforms.  (This can always be re-enabled
                // from the command line.)
                opts[OptFromHost.Name].CurValue = false;
            }
            return opts;
        }

        private bool GetBoolOption(string name) {
            return (bool)mOptions[name].CurValue;
        }
        private object GetOption(string name) {
            return mOptions[name].CurValue;
        }
        private void SetOption(string name, object value) {
            mOptions[name].CurValue = value;
        }

        public bool AddExt {
            get => GetBoolOption(OptAddExt.Name);
            set => SetOption(OptAddExt.Name, value);
        }
        public bool Classic {
            get => GetBoolOption(OptClassic.Name);
            set => SetOption(OptClassic.Name, value);
        }
        public bool Compress {
            get => GetBoolOption(OptCompress.Name);
            set => SetOption(OptCompress.Name, value);
        }
        public bool ConvertDOSText {
            get => GetBoolOption(OptConvertDOSText.Name);
            set => SetOption(OptConvertDOSText.Name, value);
        }
        public bool Debug {
            get => GetBoolOption(OptDebug.Name);
            set => SetOption(OptDebug.Name, value);
        }
        public ScanDepth Depth {
            get => (ScanDepth)GetOption(OptDepth.Name);
            set => SetOption(OptDepth.Name, value);
        }
        public string ExDir {
            get => (string)GetOption(OptExDir.Name);
            set => SetOption(OptExDir.Name, value);
        }
        public bool FastScan {
            get => GetBoolOption(OptFastScan.Name);
            set => SetOption(OptFastScan.Name, value);
        }
        public bool FromADF {
            get => GetBoolOption(OptFromADF.Name);
            set => SetOption(OptFromADF.Name, value);
        }
        public bool FromAS {
            get => GetBoolOption(OptFromAS.Name);
            set => SetOption(OptFromAS.Name, value);
        }
        public bool FromHost {
            get => GetBoolOption(OptFromHost.Name);
            set => SetOption(OptFromHost.Name, value);
        }
        public bool FromNAPS {
            get => GetBoolOption(OptFromNAPS.Name);
            set => SetOption(OptFromNAPS.Name, value);
        }
        public bool Interactive {
            get => GetBoolOption(OptInteractive.Name);
            set => SetOption(OptInteractive.Name, value);
        }
        public bool Latch {
            get => GetBoolOption(OptLatch.Name);
            set => SetOption(OptLatch.Name, value);
        }
        public bool MacZip {
            get => GetBoolOption(OptMacZip.Name);
            set => SetOption(OptMacZip.Name, value);
        }
        public bool MakeBootable {
            get => GetBoolOption(OptReserveBoot.Name);
            set => SetOption(OptReserveBoot.Name, value);
        }
        public bool Overwrite {
            get => GetBoolOption(OptOverwrite.Name);
            set => SetOption(OptOverwrite.Name, value);
        }
        public ExtractFileWorker.PreserveMode Preserve {
            get => (ExtractFileWorker.PreserveMode)GetOption(OptPreserve.Name);
            set => SetOption(OptPreserve.Name, value);
        }
        public bool Raw {
            get => GetBoolOption(OptRaw.Name);
            set => SetOption(OptRaw.Name, value);
        }
        public bool Recurse {
            get => GetBoolOption(OptRecurse.Name);
            set => SetOption(OptRecurse.Name, value);
        }
        public int Sectors {
            get => (int)GetOption(OptSectors.Name);
            set => SetOption(OptSectors.Name, value);
        }
        public bool ShowLog {
            get => GetBoolOption(OptShowLog.Name);
            set => SetOption(OptShowLog.Name, value);
        }
        public bool ShowNotes {
            get => GetBoolOption(OptShowNotes.Name);
            set => SetOption(OptShowNotes.Name, value);
        }
        public bool SkipSimple {
            get => GetBoolOption(OptSkipSimple.Name);
            set => SetOption(OptSkipSimple.Name, value);
        }
        public bool StripPaths {
            get => GetBoolOption(OptStripPaths.Name);
            set => SetOption(OptStripPaths.Name, value);
        }
        public bool StripExt {
            get => GetBoolOption(OptStripExt.Name);
            set => SetOption(OptStripExt.Name, value);
        }
        public bool Verbose {
            get => GetBoolOption(OptVerbose.Name);
            set => SetOption(OptVerbose.Name, value);
        }
        public bool Wide {
            get => GetBoolOption(OptWide.Name);
            set => SetOption(OptWide.Name, value);
        }


        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="appHook">Application hook reference.</param>
        public ParamsBag(AppHook appHook) {
            AppHook = appHook;
        }

        /// <summary>
        /// Parses the option strings.
        /// </summary>
        /// <param name="args">Full list of command-line arguments.</param>
        /// <param name="optIndex">Index at which first option may be found.</param>
        /// <returns>Index of first argument past the options, or -1 on error.</returns>
        public int ParseOptions(string[] args, int optIndex) {
            while (optIndex < args.Length) {
                string optStr = args[optIndex];
                if (!optStr.StartsWith("-")) {
                    // Found a non-option string, must be start of arguments.
                    break;
                } else if (optStr == "--") {
                    optIndex++;
                    break;
                }

                if (!ProcessOption(optStr)) {
                    return -1;
                }
                optIndex++;
            }

            return optIndex;
        }

        /// <summary>
        /// Feeds an array of option strings into the option parser.
        /// </summary>
        public void ProcessOptionList(string[] options) {
            foreach (string opt in options) {
                //Console.WriteLine("+++ opt: " + opt);
                ProcessOption(opt);     // errors are from config file; ignore
            }
        }

        public bool ProcessOption(string optStr) {
            if (optStr.StartsWith("--")) {
                return ProcessLongOption(optStr);
            } else if (optStr.StartsWith("-")) {
                // Check our short aliases.
                return ProcessShortOption(optStr);
            } else {
                // Shouldn't be here; could be junk from config file.
                Console.Error.WriteLine("Found non-option option: '" + optStr + "'");
                return false;
            }
        }

        /// <summary>
        /// Processes a "long" (double-hyphen) option.
        /// </summary>
        private bool ProcessLongOption(string optStr) {
            // If the argument looks like "name=value", split it into "name" and "value".
            string optArg = string.Empty;
            int equalIndex = optStr.IndexOf('=');
            if (equalIndex >= 0) {
                optArg = optStr.Substring(equalIndex + 1);
                optStr = optStr.Substring(0, equalIndex);
            }

            string name;
            bool boolVal;
            if (optStr.StartsWith("--no-")) {
                name = optStr.Substring(5).ToLowerInvariant();
                boolVal = false;
            } else {
                name = optStr.Substring(2).ToLowerInvariant();
                boolVal = true;
            }
            if (name == FROM_ANY) {
                OptFromADF.CurValue = boolVal;
                OptFromAS.CurValue = boolVal;
                OptFromHost.CurValue = boolVal;
                OptFromNAPS.CurValue = boolVal;
            } else if (mOptions.TryGetValue(name, out Option? opt)) {
                switch (opt.Type) {
                    case OptionType.Bool:
                        opt.CurValue = boolVal;
                        break;
                    case OptionType.String:
                        opt.CurValue = optArg;
                        break;
                    case OptionType.Depth:
                        switch (optArg.ToLowerInvariant()) {
                            case "shallow":
                                opt.CurValue = ScanDepth.Shallow;
                                break;
                            case "subvol":
                                opt.CurValue = ScanDepth.SubVol;
                                break;
                            case "max":
                                opt.CurValue = ScanDepth.Max;
                                break;
                            default:
                                Console.Error.WriteLine("Unknown scan depth '" + optArg +
                                    "', keeping default (" + opt.CurValue + ")");
                                // keep going, with default value
                                break;
                        }
                        break;
                    case OptionType.Preserve:
                        switch (optArg.ToLowerInvariant()) {
                            case "none":
                                opt.CurValue = ExtractFileWorker.PreserveMode.None;
                                break;
                            case "adf":
                                opt.CurValue = ExtractFileWorker.PreserveMode.ADF;
                                break;
                            case "as":
                                opt.CurValue = ExtractFileWorker.PreserveMode.AS;
                                break;
                            case "host":
                                opt.CurValue = ExtractFileWorker.PreserveMode.Host;
                                break;
                            case "naps":
                                opt.CurValue = ExtractFileWorker.PreserveMode.NAPS;
                                break;
                            default:
                                Console.Error.WriteLine("Unknown preserve mode '" + optArg +
                                    "', keeping default");
                                // keep going, with default value
                                break;
                        }
                        break;
                    case OptionType.Sectors:
                        switch (optArg) {
                            case "13":
                                opt.CurValue = 13;
                                break;
                            case "16":
                                opt.CurValue = 16;
                                break;
                            case "32":
                                opt.CurValue = 32;
                                break;
                            default:
                                Console.Error.WriteLine("Invalid sectors per track '" + optArg +
                                    "', keeping default");
                                // keep going, with default value
                                break;
                        }
                        break;
                    case OptionType.ConfigInt:
                        if (!ProcessConfigInt(optArg)) {
                            // This is a parsing error, not a reference to an unknown thing,
                            // so we want to fail now.
                            return false;
                        }
                        break;
                    default:
                        Console.Error.WriteLine("Unknown option type: " + opt.Type);
                        break;
                }
            } else {
                Console.Error.WriteLine("Unknown option '" + optStr + "'");
                return false;
            }
            return true;
        }

        /// <summary>
        /// Processes a "short" (single-hyphen) option.
        /// </summary>
        private bool ProcessShortOption(string optStr) {
            for (int i = 1; i < optStr.Length; i++) {
                switch (optStr[i]) {
                    case 'f':
                        ProcessLongOption("--overwrite");
                        break;
                    case 'i':
                        ProcessLongOption("--interactive");
                        break;
                    case 'j':
                        ProcessLongOption("--strip-paths");
                        break;
                    case '0':
                        ProcessLongOption("--no-compress");
                        break;
                    case 'p':
                        switch (optStr[++i]) {
                            case '0':
                                ProcessLongOption("--preserve=none");
                                break;
                            case 'a':
                                ProcessLongOption("--preserve=as");
                                break;
                            case 'd':
                                ProcessLongOption("--preserve=adf");
                                break;
                            case 'h':
                                ProcessLongOption("--preserve=host");
                                break;
                            case 'n':
                                ProcessLongOption("--preserve=naps");
                                break;
                            default:
                                Console.Error.WriteLine("Unknown option: -p" + optStr[i]);
                                return false;
                        }
                        break;
                    default:
                        Console.Error.WriteLine("Unknown option: -" + optStr[i]);
                        return false;
                }
            }
            return true;
        }


        // General pattern for configuration options.
        private static readonly Regex sConfigLineRegex = new Regex(CONFIG_PATTERN);
        private const string CONFIG_PATTERN = @"^([a-zA-Z0-9-]+):(\S+)$";

        /// <summary>
        /// Processes a configuration option set command, "name:value".
        /// </summary>
        /// <param name="optStr">Option string.</param>
        /// <returns>True on success, false if parsing failed.</returns>
        private bool ProcessConfigInt(string optStr) {
            MatchCollection matches = sConfigLineRegex.Matches(optStr);
            if (matches.Count != 1) {
                Console.Error.WriteLine("Invalid configuration option string '" + optStr + "'");
                return false;
            }

            string name = matches[0].Groups[1].Value;
            string value = matches[0].Groups[2].Value;

            // Convert the value to an integer.
            if (!StringToValue.TryParseInt(value, out int intVal, out int intBase)) {
                Console.Error.WriteLine("Value '" + value + "' must be an integer");
                return false;
            }

            AppHook.SetOptionInt(name, intVal);
            return true;
        }

        /// <summary>
        /// Generates a list of option descriptions for display by the "help" command.
        /// </summary>
        public static List<string> GetOptionDescriptions(bool debugEnabled) {
            List<string> result = new List<string>();

            // Get fresh list of options with default values.
            //
            // If we want to show the effects of the "global" options in the config file,
            // we might need to separate them from the command-specific options.  Otherwise
            // any options set on the "help" command itself would appear to be defaults.
            Dictionary<string, Option> opts = InitOptions();

            StringBuilder sb = new StringBuilder();
            foreach (Option opt in opts.Values) {
                if (!debugEnabled && opt == OptDebug) {
                    continue;
                }
                sb.Clear();

                switch (opt.Type) {
                    case OptionType.Bool:
                        sb.Append("--[no-]");
                        sb.Append(opt.Name);
                        break;
                    case OptionType.String:
                        sb.Append("--");
                        sb.Append(opt.Name);
                        sb.Append("=");
                        // default is an empty string
                        break;
                    case OptionType.Depth:
                        sb.Append("--");
                        sb.Append(opt.Name);
                        sb.Append("=");
                        sb.Append("{shallow,subvol,max}");
                        break;
                    case OptionType.Preserve:
                        sb.Append("--");
                        sb.Append(opt.Name);
                        sb.Append("=");
                        sb.Append("{none,adf,as,host,naps}");
                        break;
                    case OptionType.Sectors:
                        sb.Append("--");
                        sb.Append(opt.Name);
                        sb.Append("=");
                        sb.Append("{13,16,32}");
                        break;
                    case OptionType.ConfigInt:
                        sb.Append("--");
                        sb.Append(opt.Name);
                        sb.Append("=");
                        sb.Append("name:value");
                        break;
                    default:
                        System.Diagnostics.Debug.Assert(false);
                        break;
                }
                for (int i = sb.Length; i < 40; i++) {
                    sb.Append(' ');
                }
                sb.Append(opt.DefValue.ToString()!.ToLowerInvariant());

                result.Add(sb.ToString());
            }

            // Add the meta-option.
            result.Add("--[no-]" + FROM_ANY);

            return result;
        }

        #endregion Options

        #region Import/Export

        internal static Dictionary<string, ConvConfig.FileConvSpec> sImportSpecs =
            new Dictionary<string, ConvConfig.FileConvSpec>();
        internal static Dictionary<string, ConvConfig.FileConvSpec> sExportSpecs =
            new Dictionary<string, ConvConfig.FileConvSpec>();

        /// <summary>
        /// Processes an import/export config file line.
        /// </summary>
        /// <param name="line">Config file line to parse, with leading ">import" or ">export"
        ///   string removed.</param>
        /// <param name="isExport">True if this is an "export" line.</param>
        /// <returns>True if parsing was successful.</returns>
        internal bool ProcessImportExport(string line, bool isExport) {
            string trim = line.Trim();
            ConvConfig.FileConvSpec? spec = ConvConfig.CreateSpec(trim);
            if (spec == null) {
                Console.Error.WriteLine("Error: bad import/export spec in config file: " + trim);
                return false;
            }
            if (isExport) {
                sExportSpecs[spec.Tag] = spec;
            } else {
                sImportSpecs[spec.Tag] = spec;
            }
            return true;
        }

        internal ConvConfig.FileConvSpec? GetImportSpec(string tag) {
            sImportSpecs.TryGetValue(tag, out ConvConfig.FileConvSpec? spec);
            return spec;
        }

        internal ConvConfig.FileConvSpec? GetExportSpec(string tag) {
            sExportSpecs.TryGetValue(tag, out ConvConfig.FileConvSpec? spec);
            return spec;
        }

        /// <summary>
        /// Clears the import/export specs loaded from the config file.  Useful during testing.
        /// </summary>
        internal void EraseImportExportSpecs() {
            sImportSpecs.Clear();
            sExportSpecs.Clear();
        }

        #endregion Import/Export
    }
}
