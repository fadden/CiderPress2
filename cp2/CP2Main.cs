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
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

using AppCommon;
using CommonUtil;
using DiskArc;
using FileConv;

namespace cp2 {
    /// <summary>
    /// Command-line entry point.
    /// </summary>
    static class CP2Main {
        public static readonly CommonUtil.Version APP_VERSION = GlobalAppVersion.AppVersion;

        private const string APP_NAME = "cp2";
        private const string APP_DEBUG_STR =
#if DEBUG
            " (DEBUG)";
#else
            "";
#endif

        /// <summary>
        /// Command-handling delegate definition.  Handlers are static methods.
        /// </summary>
        /// <param name="cmdName">Command name string used on command line.  Mostly redundant
        ///   but occasionally useful for error messages.</param>
        /// <param name="args">Additional arguments found on command line, unmodified.</param>
        /// <param name="parms">Command-line option values, plus internal object refs.</param>
        /// <returns>True on success, false on failure.</returns>
        public delegate bool CommandHandler(string cmdName, string[] args, ParamsBag parms);

        /// <summary>
        /// Command definition.
        /// </summary>
        private class Command {
            public string[] Names { get; private set; }
            public string Summary { get; private set; }
            public string Usage { get; private set; }
            public bool NeedsArchive { get; private set; }
            public bool AcceptsMultipleArgs { get; private set; }
            public CommandHandler Handler { get; private set; }

            public Command(string[] names, string summary, string usage, bool needsArchive,
                    bool acceptsMultipleArgs, CommandHandler handler) {
                Names = names;
                Summary = summary;
                Usage = usage;
                NeedsArchive = needsArchive;
                AcceptsMultipleArgs = acceptsMultipleArgs;
                Handler = handler;
            }
        }

        /// <summary>
        /// List of supported commands.
        /// </summary>
        private static Command[] sCommands = {
            new Command(names: new string[] { "add", "a" },
                summary: "adds files to an archive",
                usage: "<ext-archive> <file-or-dir> [file-or-dir...]",
                needsArchive: true, acceptsMultipleArgs: true,
                handler: Add.HandleAdd),
            new Command(names: new string[] { "bless" },
                summary: "blesses a Macintosh HFS image for booting",
                usage: "<ext-archive> <system-folder> [{boot-image|-}]",
                needsArchive: true, acceptsMultipleArgs: true,
                handler: DiskUtil.HandleBless),
            new Command(names: new string[] { "catalog", "v" },
                summary: "displays a detailed listing of an archive",
                usage: "<ext-archive>",
                needsArchive: true, acceptsMultipleArgs: false,
                handler: Catalog.HandleCatalog),
            new Command(names: new string[] { "copy", "cp" },
                summary: "copies files from one archive to another",
                usage: "<src-ext-archive> [file-in-archive...] <dst-ext-archive>",
                needsArchive: true, acceptsMultipleArgs: true,
                handler: Copy.HandleCopy),
            new Command(names: new string[] { "copy-blocks" },  // dangerous, don't abbreviate
                summary: "copies blocks between archives",
                usage: "<src-ext-archive> <dst-ext-archive> [<src-start> <dst-start> <count>]",
                needsArchive: true, acceptsMultipleArgs: true,
                handler: DiskUtil.HandleCopyBlocks),
            new Command(names: new string[] { "copy-sectors" },  // dangerous, don't abbreviate
                summary: "copies sectors between archives",
                usage: "<src-ext-archive> <dst-ext-archive>",
                needsArchive: true, acceptsMultipleArgs: true,
                handler: DiskUtil.HandleCopySectors),
            new Command(names: new string[] { "create-disk-image", "cdi" },
                summary: "creates a new disk image",
                usage: "<new-archive> <size> [filesystem]",
                needsArchive: true, acceptsMultipleArgs: true,
                handler: DiskUtil.HandleCreateDiskImage),
            new Command(names: new string[] { "create-file-archive", "cfa" },
                summary: "creates a new file archive",
                usage: "<new-archive>",
                needsArchive: true, acceptsMultipleArgs: false,
                handler: ArcUtil.HandleCreateFileArchive),
            new Command(names: new string[] { "defrag" },
                summary: "defragments a filesystem",
                usage: "<ext-archive>",
                needsArchive: true, acceptsMultipleArgs: false,
                handler: DiskUtil.HandleDefrag),
            new Command(names: new string[] { "delete", "rm" },
                summary: "deletes files from an archive",
                usage: "<ext-archive> <file-in-archive> [file-in-archive...]",
                needsArchive: true, acceptsMultipleArgs: true,
                handler: Delete.HandleDelete),
            new Command(names: new string[] { "export", "xp" },
                summary: "exports files from an archive",
                usage: "<ext-archive> <export-spec> [file-in-archive...]",
                needsArchive: true, acceptsMultipleArgs: true,
                handler: Extract.HandleExport),
            new Command(names: new string[] { "extract", "x" },
                summary: "extracts files from an archive",
                usage: "<ext-archive> [file-in-archive...]",
                needsArchive: true, acceptsMultipleArgs: true,
                handler: Extract.HandleExtract),
            new Command(names: new string[] { "extract-partition", "expart" },
                summary: "extracts a partition to a disk image",
                usage: "<ext-archive> <output-file>",
                needsArchive: true, acceptsMultipleArgs: true,
                handler: DiskUtil.HandleExtractPartition),
            new Command(names: new string[] { "get-attr", "ga" },
                summary: "gets file attributes",
                usage: "<ext-archive> <file-in-archive> [attr]",
                needsArchive: true, acceptsMultipleArgs: true,
                handler: FileAttr.HandleGetAttr),
            new Command(names: new string[] { "get-metadata", "gm" },
                summary: "displays metadata values",
                usage: "<ext-archive> [field]",
                needsArchive: true, acceptsMultipleArgs: true,
                handler: Metadata.HandleGetMetadata),
            new Command(names: new string[] { "help" },
                summary: "displays program usage information",
                usage: "[command]",
                needsArchive: false, acceptsMultipleArgs: true,
                handler: HandleHelp),
            new Command(names: new string[] { "import", "ip" },
                summary: "imports files into an archive",
                usage: "<ext-archive> <import-spec> <file-or-dir> [file-or-dir...]",
                needsArchive: true, acceptsMultipleArgs: true,
                handler: Add.HandleImport),
            new Command(names: new string[] { "list", "l" },
                summary: "displays a succinct file listing",
                usage: "<ext-archive>",
                needsArchive: true, acceptsMultipleArgs: false,
                handler: Catalog.HandleList),
            new Command(names: new string[] { "mkdir", "md" },
                summary: "creates a new directory in a disk image",
                usage: "<ext-archive> dir-name",
                needsArchive: true, acceptsMultipleArgs: true,
                handler: Mkdir.HandleMkdir),
            new Command(names: new string[] { "move", "rename", "mv" },
                summary: "moves or renames files within an archive",
                usage: "<ext-archive> <file-in-archive> [file-in-archive...] <new-name>",
                needsArchive: true, acceptsMultipleArgs: true,
                handler: Move.HandleMove),
            new Command(names: new string[] { "multi-disk-catalog", "mdc" },
                summary: "generates a catalog listing for multiple archives",
                usage: "<archive-or-dir> [archive-or-dir...]",
                needsArchive: true, acceptsMultipleArgs: true,
                handler: Catalog.HandleMultiDiskCatalog),
            new Command(names: new string[] { "print" },
                summary: "extracts files and displays them on stdout",
                usage: "<ext-archive> [file-in-archive...]",
                needsArchive: true, acceptsMultipleArgs: true,
                handler: Print.HandlePrint),
            new Command(names: new string[] { "read-track", "rt" },
                summary: "reads a track from a nibble disk image",
                usage: "<ext-archive> <track-num>",
                needsArchive: true, acceptsMultipleArgs: true,
                handler: SectorEdit.HandleReadTrack),
            new Command(names: new string[] { "read-block", "rb" },
                summary: "reads a block from a disk image as a hex dump",
                usage: "<ext-archive> <block-num>",
                needsArchive: true, acceptsMultipleArgs: true,
                handler: SectorEdit.HandleReadBlock),
            new Command(names: new string[] { "read-block-cpm", "rbc" },
                summary: "reads a block from a disk image as a hex dump (CP/M skew)",
                usage: "<ext-archive> <block-num>",
                needsArchive: true, acceptsMultipleArgs: true,
                handler: SectorEdit.HandleReadBlockCPM),
            new Command(names: new string[] { "read-sector", "rs" },
                summary: "reads a sector from a disk image as a hex dump",
                usage: "<ext-archive> <track-num> <sector-num>",
                needsArchive: true, acceptsMultipleArgs: true,
                handler: SectorEdit.HandleReadSector),
            new Command(names: new string[] { "replace-partition", "repart" },
                summary: "replaces the contents of a partition with a disk image",
                usage: "<src-ext-archive> <dst-ext-archive>",
                needsArchive: true, acceptsMultipleArgs: true,
                handler: DiskUtil.HandleReplacePartition),
            new Command(names: new string[] { "set-attr", "sa" },
                summary: "sets file attributes",
                usage: "<ext-archive> <attrs> <file-in-archive> [file-in-archive...]",
                needsArchive: true, acceptsMultipleArgs: true,
                handler: FileAttr.HandleSetAttr),
            new Command(names: new string[] { "set-metadata", "sm" },
                summary: "sets metadata values",
                usage: "<ext-archive> <field> [value]",
                needsArchive: true, acceptsMultipleArgs: true,
                handler: Metadata.HandleSetMetadata),
            new Command(names: new string[] { "test" },
                summary: "tests the contents of an archive",
                usage: "<ext-archive>",
                needsArchive: true, acceptsMultipleArgs: false,
                handler: Test.HandleTest),
            new Command(names: new string[] { "version" },
                summary: "displays program version information",
                usage: "",
                needsArchive: false, acceptsMultipleArgs: false,
                handler: HandleVersion),
            new Command(names: new string[] { "write-block", "wb" },
                summary: "writes a block to a disk image from a hex dump",
                usage: "<ext-archive> <block-num> <data-file>",
                needsArchive: true, acceptsMultipleArgs: true,
                handler: SectorEdit.HandleWriteBlock),
            new Command(names: new string[] { "write-block-cpm", "wbc" },
                summary: "writes a block to a disk image from a hex dump (CP/M skew)",
                usage: "<ext-archive> <block-num> <data-file>",
                needsArchive: true, acceptsMultipleArgs: true,
                handler: SectorEdit.HandleWriteBlockCPM),
            new Command(names: new string[] { "write-sector", "ws" },
                summary: "writes a sector to a disk image from a hex dump",
                usage: "<ext-archive> <track-num> <sector-num> <data-file>",
                needsArchive: true, acceptsMultipleArgs: true,
                handler: SectorEdit.HandleWriteSector),

            // For debugging / QA; omitted from "help" command listing unless --debug set.
            new Command(names: new string[] { "debug-crash" },
                summary: "crashes",
                usage: "",
                needsArchive: false, acceptsMultipleArgs: false,
                handler: HandleCrash),
            new Command(names: new string[] { "debug-show-info" },
                summary: "displays detailed archive-specific information",
                usage: "<ext-archive>",
                needsArchive: true, acceptsMultipleArgs: false,
                handler: DebugCmd.HandleShowInfo),
            new Command(names: new string[] { "debug-wtree" },
                summary: "displays work tree for an archive",
                usage: "<archive>",
                needsArchive: true, acceptsMultipleArgs: false,
                handler: DebugCmd.HandleDumpWTree),
            new Command(names: new string[] { "debug-test" },
                summary: "executes program tests",
                usage: "",
                needsArchive: false, acceptsMultipleArgs: false,
                handler: Tests.Controller.HandleRunTests),
            new Command(names: new string[] { "debug-test-da" },
                summary: "executes DiskArc library tests",
                usage: "[test-class [test-method]]",
                needsArchive: false, acceptsMultipleArgs: true,
                handler: Tests.LibTestRunner.HandleRunDATests),
            new Command(names: new string[] { "debug-test-fc" },
                summary: "executes FileConv library tests",
                usage: "[test-class [test-method]]",
                needsArchive: false, acceptsMultipleArgs: true,
                handler: Tests.LibTestRunner.HandleRunFCTests),
        };


        /// <summary>
        /// OS entry point.
        /// </summary>
        internal static void Main(string[] args) {
            // Can use Environment.GetCommandLineArgs() to get command name in args[0] if desired.

            Environment.ExitCode = 2;       // use code 2 for usage problems
            Console.OutputEncoding = Encoding.UTF8;

            if (args.Length == 0) {
                Usage();
                return;
            }

            // Process args.
            string cmdName = args[0];
            Command? cmd = null;
            foreach (Command cmdEntry in sCommands) {
                foreach (string name in cmdEntry.Names) {
                    if (cmdName == name) {
                        cmd = cmdEntry;
                        break;
                    }
                }
            }
            if (cmd == null) {
                if (cmdName.StartsWith("--")) {
                    Console.Error.WriteLine("Unknown command '" + cmdName +
                        "' (commands don't start with \"--\")");
                } else {
                    Console.Error.WriteLine("Unknown command '" + cmdName + "'");
                }
                return;
            }

            ParamsBag parms = new ParamsBag(new AppHook(new SimpleMessageLog()));
            ProcessConfigFile(cmd, parms);
            int argStart = parms.ParseOptions(args, 1);
            if (argStart < 0) {
                return;
            }
            string[] extraArgs = new string[args.Length - argStart];
            for (int i = 0; i < extraArgs.Length; i++) {
                extraArgs[i] = args[argStart + i];
            }

            // Do a simple arg count check.
            int minArgs = 0;
            int maxArgs = int.MaxValue;
            if (cmd.NeedsArchive) {
                minArgs++;
            }
            if (!cmd.AcceptsMultipleArgs) {
                maxArgs = minArgs;
            }
            if (extraArgs.Length < minArgs || extraArgs.Length > maxArgs) {
                ShowUsage(cmdName);
                return;
            }

            // Invoke appropriate command handler.
            CommandHandler handler = cmd.Handler;
            Environment.ExitCode = handler(cmdName, extraArgs, parms) ? 0 : 1;

#if DEBUG
            // DEBUG: give finalizers a chance to run
            GC.Collect();
            GC.WaitForPendingFinalizers();
#endif
            if (parms.ShowLog) {
                parms.AppHook.LogI("Application exiting");
                SimpleMessageLog log = (SimpleMessageLog)parms.AppHook.Log;
                Console.WriteLine("Log:");
                Console.WriteLine(log.WriteToString());
            }
        }

        /// <summary>
        /// Prints general usage summary.
        /// </summary>
        private static void Usage() {
            Console.WriteLine("Usage: " + APP_NAME + " <command> [options...] [args...]");
            Console.WriteLine("(Use \"" + APP_NAME + " help\" to see a list of commands.)");
        }

        /// <summary>
        /// Handles the "help" command.
        /// </summary>
        private static bool HandleHelp(string cmdName, string[] args, ParamsBag parms) {
            if (args.Length > 0) {
                ShowUsage(args[0]);
                // TODO: show relevant options?
                return true;
            }
            Console.WriteLine("CiderPress II Command-Line Utility v" + APP_VERSION + APP_DEBUG_STR);
            Console.WriteLine("Usage: " + APP_NAME + " <command> [options...] [args...]");
            Console.WriteLine();
            Console.WriteLine("Commands:");
            foreach (Command cmd in sCommands) {
                if (!parms.Debug && cmd.Names[0].StartsWith("debug-")) {
                    continue;
                }
                StringBuilder sb = new StringBuilder();
                sb.Append("  ");
                for (int i = 0; i < cmd.Names.Length; i++) {
                    if (i != 0) {
                        sb.Append(" | ");
                    }
                    sb.Append(cmd.Names[i]);
                }
                sb.Append(" : ");
                sb.Append(cmd.Summary);
                Console.WriteLine(sb.ToString());
            }

            Console.WriteLine();
            Console.WriteLine("Options:                                Default:");
            List<string> options = ParamsBag.GetOptionDescriptions(parms.Debug);
            options.Sort();
            foreach (string opt in options) {
                Console.WriteLine("  " + opt);
            }
            return true;
        }

        /// <summary>
        /// Displays the usage for a single command on stdout.
        /// </summary>
        /// <param name="cmdName">Name of command to show usage for.</param>
        internal static void ShowUsage(string cmdName) {
            Command? matchCmd = null;
            foreach (Command cmd in sCommands) {
                foreach (string name in cmd.Names) {
                    if (name == cmdName) {
                        matchCmd = cmd;
                        break;
                    }
                }
            }
            if (matchCmd == null) {
                Console.WriteLine("Internal error: unknown command '" + cmdName + "'");
                return;
            }

            Console.Write("Usage: " + APP_NAME + " ");

            for (int i = 0; i < matchCmd.Names.Length; i++) {
                if (i != 0) {
                    Console.Write('|');
                }
                Console.Write(matchCmd.Names[i]);
            }
            Console.Write(" [options] ");
            Console.WriteLine(matchCmd.Usage);

            Console.WriteLine(cmdName + ": " + matchCmd.Summary);

            // For import/export, show a quick summary of known converters.
            List<string>? tags = null;
            if (matchCmd.Handler == Add.HandleImport) {
                tags = ImportFoundry.GetConverterTags();
            } else if (matchCmd.Handler == Extract.HandleExport) {
                tags = ExportFoundry.GetConverterTags();
            }
            if (tags != null) {
                Console.WriteLine();
                Console.Write("Converters:");
                foreach (string tag in tags) {
                    Console.Write(" " + tag);
                }
                Console.WriteLine();
            }
        }

        /// <summary>
        /// Handles an unimplemented command.
        /// </summary>
        private static bool HandleNotImplemented(string cmdName, string[] args, ParamsBag parms) {
            Console.WriteLine("Not yet implemented (sorry)");
            return false;
        }

        /// <summary>
        /// Throws an uncaught exception, to see how things behave.
        /// </summary>
        private static bool HandleCrash(string cmdName, string[] args, ParamsBag parms) {
            Debug.Assert(false, "this fails if asserts are enabled");
            throw new NotImplementedException("Catch me if you can");
        }

        /// <summary>
        /// Handles the "version" command.
        /// </summary>
        private static bool HandleVersion(string cmdName, string[] args, ParamsBag parms) {
            Console.WriteLine("CiderPress II Command-Line Utility v" + APP_VERSION + APP_DEBUG_STR);
            Console.WriteLine("+ DiskArc Library v" + Defs.LibVersion +
                (string.IsNullOrEmpty(Defs.BUILD_TYPE) ? "" : " (" + Defs.BUILD_TYPE + ")"));
            Console.WriteLine("+ Runtime: " + RuntimeInformation.FrameworkDescription + " / " +
                RuntimeInformation.RuntimeIdentifier);
            if (parms.Debug) {
                Console.WriteLine("  E_V=" + Environment.Version);
                Console.WriteLine("  E_OV=" + Environment.OSVersion);
                Console.WriteLine("  E_OV_P=" + Environment.OSVersion.Platform);
                Console.WriteLine("  E_64=" + Environment.Is64BitOperatingSystem + " / " +
                    Environment.Is64BitProcess);
                Console.WriteLine("  E_MACH=\"" + Environment.MachineName + "\" cpus=" +
                    Environment.ProcessorCount);
                Console.WriteLine("  E_CD=\"" + Environment.CurrentDirectory + "\"");
                Console.WriteLine("  RI_FD=" + RuntimeInformation.FrameworkDescription);
                Console.WriteLine("  RI_OSA=" + RuntimeInformation.OSArchitecture);
                Console.WriteLine("  RI_OSD=" + RuntimeInformation.OSDescription);
                Console.WriteLine("  RI_PA=" + RuntimeInformation.ProcessArchitecture);
                Console.WriteLine("  RI_RI=" + RuntimeInformation.RuntimeIdentifier);
                Console.WriteLine(" " +
                    " IsFreeBSD=" + RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD) +
                    " IsOSX=" + RuntimeInformation.IsOSPlatform(OSPlatform.OSX) +
                    " IsLinux=" + RuntimeInformation.IsOSPlatform(OSPlatform.Linux) +
                    " IsWindows=" + RuntimeInformation.IsOSPlatform(OSPlatform.Windows));
            }
            Console.WriteLine();

            string settingsPath = GetSettingsFilePath();
            FileInfo info = new FileInfo(settingsPath);
            Console.WriteLine("Settings file " + (info.Exists ? "is" : "would be") +
                " '" + settingsPath + "'");
            Console.WriteLine();
            Console.WriteLine("Copyright 2025 faddenSoft - https://ciderpress2.com/");
            Console.WriteLine("See http://www.apache.org/licenses/LICENSE-2.0 for license.");
            return true;
        }

        #region Config file

        private const string SETTINGS_FILE = "cp2rc";
        private const string GLOBAL_TAG = "global";

        private const string EXPORT_CFG = ">export ";
        private const string IMPORT_CFG = ">import ";

        /// <summary>
        /// One line in the config file, "cmd: option [option...]".  Commands and options are
        /// words and hyphens, interspersed with whitespace.  Options must start with a hyphen.
        /// Most options are strictly letters, but a few are name=value.
        /// </summary>
        private static readonly Regex sConfigLineRegex = new Regex(CONFIG_LINE_PATTERN);
        private const string CONFIG_LINE_PATTERN = @"^([a-zA-Z-]+)\s*:(\s*-[a-zA-Z-]+(?:=\S+)?)+$";

        /// <summary>
        /// Returns the path to the user's home directory.
        /// </summary>
        /// <remarks>
        /// <para>On Windows this is something like "C:\Users\fadden", under UNIX it's $HOME,
        /// e.g. "/home/fadden".</para>
        /// </remarks>
        /// <returns>Full pathname.</returns>
        private static string GetHomePath() {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        /// <summary>
        /// Returns the setting directory.
        /// </summary>
        /// <remarks>
        /// <para>On Windows, we may want to use %LOCALAPPDATA% ("\Users\fadden\AppData\Local"),
        /// because the home directory ("\users\fadden") seems like the wrong place to dump
        /// settings files.  OTOH, "vim" puts the vimrc file there.</para>
        /// </remarks>
        /// <returns></returns>
        private static string GetSettingsFilePath() {
            string dir = GetHomePath();
            string prefix = ".";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
                prefix = "_";
            }
            return Path.Combine(dir, prefix + SETTINGS_FILE);
        }

        /// <summary>
        /// Reads the application configuration file and applies the options to the parameter
        /// object.
        /// </summary>
        /// <remarks>
        /// <para>Errors result in messages on stderr, but do not halt execution.</para>
        /// </remarks>
        /// <param name="cmd">Command being executed.</param>
        /// <param name="parms">Application parameters.</param>
        private static void ProcessConfigFile(Command cmd, ParamsBag parms) {
            string[]? globalOpts = null;
            string[]? cmdOpts = null;

            string[] lines;
            try {
                lines = File.ReadAllLines(GetSettingsFilePath());
            } catch (IOException ex) {
                // Unable to read config file.  They probably don't have one.
                Debug.WriteLine("Unable to read config file: " + ex.Message);
                return;
            }

            foreach (string line in lines) {
                string trim = line.Trim();  // remove leading/trailing whitespace
                if (trim.Length == 0 || trim[0] == '#') {
                    continue;               // skip blank lines and comments
                }

                // Simple string check.
                if (line.StartsWith(EXPORT_CFG) || line.StartsWith(IMPORT_CFG)) {
                    Debug.Assert(EXPORT_CFG.Length == IMPORT_CFG.Length);
                    parms.ProcessImportExport(line.Substring(EXPORT_CFG.Length),
                        line.StartsWith(EXPORT_CFG));
                    continue;
                }

                // Parse command option.
                MatchCollection matches = sConfigLineRegex.Matches(trim);
                if (matches.Count != 1) {
                    Console.Error.WriteLine("Ignoring garbage in config file: '" + trim + "'");
                } else {
                    string cmdName = matches[0].Groups[1].Value;
                    int optCount = matches[0].Groups[2].Captures.Count;
                    string[] options = new string[optCount];
                    for (int i = 0; i < optCount; i ++) {
                        options[i] = matches[0].Groups[2].Captures[i].Value.Trim();
                    }

                    if (cmdName == GLOBAL_TAG) {
                        globalOpts = options;
                    } else {
                        // See if this matches the current command.
                        foreach (string name in cmd.Names) {
                            if (name == cmdName) {
                                //Console.WriteLine("+++ match on " + name);
                                cmdOpts = options;
                                break;
                            }
                        }
                    }
                }
            }

            // Process globals first, then replace with command-specific options.
            if (globalOpts != null) {
                parms.ProcessOptionList(globalOpts);
            }
            if (cmdOpts != null) {
                parms.ProcessOptionList(cmdOpts);
            }
        }

        #endregion Config file
    }
}
