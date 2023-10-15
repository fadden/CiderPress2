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
using System.Collections;
using System.Diagnostics;
using System.Text.RegularExpressions;

using CommonUtil;
using DiskArc;
using DiskArc.Arc;

// Regarding pathname components...
//
// The command line may specify files from different directories.  If the user is in /home/fadden,
// they might add "foo.txt", "bar/blah.as", "../slurie/stuff.jpg", and "/bin/sh".  We don't want
// to add everything with full absolute pathnames, but neither do we want to discard the paths
// from everything that isn't in a subdirectory.
//
// Programs like Info-Zip and NuLib2 generally work off of the argument pathname.  For example,
// zip would store the files as they are given in the example above, except for the last which
// loses the leading slash to become "bin/sh".  NuLib2 goes a step farther and removes the
// leading "../" from the next-to-last filename.
//
// The .NET Path class has pathname canonicalization features (which it calls "normalization")
// that make it easy to remove odd bits if we work with absolute paths.  This leads to a simple
// approach:
//  - convert all arguments to canonical absolute paths
//  - if the path starts with the current working directory, treat it as relative;
//    otherwise, strip the root off (subtract Path.GetPathRoot() string) and keep the rest
//
// It's not quite that simple, as removing the path root may or may not leave a leading slash,
// but it's workable.

// TODO: set a flag so we can generate a warning when the same fork is added more than once.
//   For example, "GSHK" and "GSHK#b3db07" are currently blended together, and the only
//   indication is a warning in the log file.

namespace AppCommon {
    /// <summary>
    /// Process and record a plan for adding files to a disk image or file archive.
    /// </summary>
    /// <remarks>
    /// <para>This will check for the presence of various files to find resource forks and
    /// file attribute storage.  Since naming conventions vary, this will need to open some
    /// of the files to verify their contents, particularly for AppleSingle/AppleDouble.</para>
    /// <para>We can't keep the files open, because if there are a large number of them we
    /// might exceed an operating system limit.  This will result in some files being opened and
    /// processed twice, which is inefficient, but it's easier to fully characterize the set of
    /// input files that need to be included before we start doing the disk/archive updates.</para>
    /// <para>This code is unaware of the ultimate destination of the files, so it doesn't
    /// need to worry about the target archive's capabilities (filename length, support for
    /// forked files, etc).</para>
    ///
    /// <para>The set of elements can be obtained through the enumerator.  The order in which
    /// they appear is not defined.</para>
    /// </remarks>
    public class AddFileSet : IEnumerable<AddFileEntry> {
        #region AddOpts

        /// <summary>
        /// Options that affect adding files.
        /// </summary>
        public class AddOpts {
            /// <summary>
            /// If set, unpack AppleDouble Files.  Because the files are usually hidden in some
            /// way, they may not be part of the file set unless we found them in a
            /// subdirectory.
            /// </summary>
            public bool ParseADF { get; set; } = true;

            /// <summary>
            /// If set, unpack AppleSingle files.
            /// </summary>
            public bool ParseAS { get; set; } = true;

            /// <summary>
            /// If set, parse and remove NuLib2 Attribute Preservation Strings.
            /// </summary>
            public bool ParseNAPS { get; set; } = true;

            /// <summary>
            /// Check for a "/..namedfork/rsrc" file.  Only expected on HFS/HFS+ on a
            /// Macintosh.
            /// </summary>
            public bool CheckNamed { get; set; } = false;

            /// <summary>
            /// Check getxattr(path, XATTR_FINDERINFO_NAME, ...) for file type / creator.  Only
            /// expected on a Macintosh.
            /// </summary>
            /// <remarks>
            /// ADF/AS/NAPS have higher priority, so this won't be queried if we have type
            /// info from some other source.
            /// </remarks>
            public bool CheckFinderInfo { get; set; } = false;

            /// <summary>
            /// If set, descend into directories.
            /// </summary>
            public bool Recurse { get; set; } = true;

            /// <summary>
            /// If set, strip redundant filename extensions when importing files.
            /// </summary>
            public bool StripExt { get; set; } = true;

            /// <summary>
            /// Constructs an object with platform-specific defaults.
            /// </summary>
            public AddOpts() { }
        }

        #endregion AddOpts

        /// <summary>
        /// Regular expression for a NAPS filename string with 6 digits and an optional type
        /// specification character.
        /// </summary>
        /// <remarks>
        /// <para>Filenames look like "TEXTFILE#040000.txt" or "GSHK#b3db07r".  The storage
        /// name comes first, followed by '#' and a 6-digit hex value, an optional 'r' or 'i'
        /// to indicate resource fork or disk image, and then an optional extension to make
        /// the file easier to work with on the host system.</para>
        /// <para>The regex splits the string into three parts: (1) storage name; (2) '#' followed
        /// by type info; and (3) host-convenience extension.  When the file is added to an
        /// archive, we want to drop everything past the first group.</para>
        /// </remarks>
        internal static Regex sNaps6Regex = new Regex(NAPS6_PATTERN);
        private const string NAPS6_PATTERN = @"^(.+)(#[A-Fa-f0-9]{6}[RrIi]?)(\..*)?$";

        /// <summary>
        /// Regular expression for a NAPS filename string with 16 digits and an optional type
        /// specification character.  Used for HFS file type / creator.
        /// </summary>
        internal static Regex sNaps16Regex = new Regex(NAPS16_PATTERN);
        private const string NAPS16_PATTERN = @"^(.+)(#[A-Fa-f0-9]{16}[RrIi]?)(\..*)?$";

        /// <summary>
        /// Append this to a filename to access the resource fork.  This is only expected to
        /// work on Mac OS, so we can assume the path component separator char is '/'.
        /// </summary>
        public const string RSRC_FORK_SUFFIX = "/..namedfork/rsrc";

        /// <summary>
        /// List of file entries, keyed by the full path to the data fork.  For a NAPS file the
        /// attribute string will have been stripped, so do not assume that the keys are valid
        /// paths to actual files.
        /// </summary>
        private Dictionary<string, AddFileEntry> mFiles = new Dictionary<string, AddFileEntry>();

        /// <summary>
        /// Count of entries in set.
        /// </summary>
        public int Count { get { return mFiles.Count; } }

        // Returns the enumerator for the Dictionary values.  Order is undefined.
        public IEnumerator<AddFileEntry> GetEnumerator() {
            return mFiles.Values.GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() {
            return mFiles.Values.GetEnumerator();
        }

        /// <summary>
        /// File import converter.
        /// </summary>
        /// <remarks>
        /// TODO: we currently have a single Importer defined for all files.  If we want to
        /// support a "best" option that switches on file extension we'll need to make this
        /// per-file (possibly creating one copy of each Importer and sharing the instance).
        /// We can't defer the decision until later because we need to know which forks are
        /// going to be created.
        /// </remarks>
        internal FileConv.Importer? Importer { get; private set; }

        /// <summary>
        /// File import options.
        /// </summary>
        internal Dictionary<string, string>? ImportOptions { get; private set; }

        private AddOpts mAddOpts;
        private string mBasePath;
        private AppHook mAppHook;


        /// <summary>
        /// Creates a set of files to be added.
        /// </summary>
        /// <param name="basePath">Base directory, for relative paths.</param>
        /// <param name="pathNames">List of full or relative pathnames.</param>
        /// <param name="options">Options.</param>
        /// <param name="importSpec">For import operations, the conversion spec.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <exception cref="ArgumentException">Bad base path.</exception>
        /// <exception cref="DirectoryNotFoundException"><paramref name="basePath"/> does not
        ///   exist.</exception>
        /// <exception cref="FileNotFoundException">A file listed in the pathname list does
        ///   not exist.</exception>
        public AddFileSet(string basePath, string[] pathNames, AddOpts options,
                FileConv.ConvConfig.FileConvSpec? importSpec, AppHook appHook) {
            mAddOpts = options;
            mAppHook = appHook;

            if (importSpec != null) {
                // This is an "import" operation.  All of the preservation parsing options
                // should be false, since our inputs should be host files.
                Importer = FileConv.ImportFoundry.GetConverter(importSpec.Tag, appHook);
                if (Importer == null) {
                    throw new ArgumentException("unknown import converter '" +
                        importSpec.Tag + "'");
                }
                ImportOptions = importSpec.Options;
            }

            string oldCurrentDir = Environment.CurrentDirectory;
            try {
                // This will throw a variety of things if the path is bad.
                mBasePath = Path.GetFullPath(basePath);

                // Set the current directory; will throw if it doesn't exist.
                Environment.CurrentDirectory = mBasePath;

                foreach (string path in pathNames) {
                    string fullPath = Path.GetFullPath(path);
                    ProcessPath(fullPath);
                }

                // We'll pick up AppleDouble "._" files if they're in a subdirectory, but not if
                // a group of files were selected with e.g. shell wildcards.  Look for a "._"
                // file for everything that was explicitly listed.
                if (options.ParseADF) {
                    Debug.Assert(Importer == null);
                    ScanForADF(pathNames);
                }

                // If host attributes and resource forks are enabled, we need to scan for them
                // explicitly on every file.
                if (options.CheckNamed || options.CheckFinderInfo) {
                    Debug.Assert(Importer == null);
                    ScanForHostAttr();
                }
            } finally {
                Environment.CurrentDirectory = oldCurrentDir;
            }
        }

        /// <summary>
        /// Scans all explicitly-listed files for AppleDouble components.
        /// </summary>
        private void ScanForADF(string[] pathNames) {
            foreach (string path in pathNames) {
                string fullPath = Path.GetFullPath(path);
                if (!mFiles.TryGetValue(fullPath, out AddFileEntry? testEntry)) {
                    // No entry for this, which means we used a modified form of the
                    // pathname for the key.  Or it's a directory.  That should never be the
                    // case for AppleDouble.
                    continue;
                }
                if (testEntry.HasRsrcFork || testEntry.FileType != 0 ||
                        testEntry.AuxType != 0) {
                    // If it has a resource fork or type information, either it's not
                    // AppleDouble, or it is and we've already processed the header file.
                    continue;
                }
                string fileName = Path.GetFileName(fullPath);
                if (fileName.StartsWith(AppleSingle.ADF_PREFIX)) {
                    // This is the AppleDouble header file, and we've already handled it.
                    // (Which is weird, since it shouldn't be stored with "._" in the key.)
                    Debug.Assert(false, "Weird key: " + fullPath);
                    continue;
                }
                string adfName = AppleSingle.ADF_PREFIX + fileName;
                string checkPath = Path.Combine(Path.GetDirectoryName(fullPath)!, adfName);
                if (File.Exists(checkPath)) {       // File.Exists() will not find directories
                    ProcessPath(checkPath);
                }
            }
        }

        /// <summary>
        /// Scans all files for host-filesystem attributes.
        /// </summary>
        private void ScanForHostAttr() {
            foreach (AddFileEntry ent in mFiles.Values) {
                if (ent.IsDirectory) {
                    continue;
                }

                // Check for values, but don't override what we got from NAPS or AppleDouble.
                if (mAddOpts.CheckFinderInfo && ent.HFSFileType == 0 && ent.HFSCreator == 0 &&
                        ent.HasDataFork) {
                    byte[] fiBuf = new byte[SystemXAttr.FINDERINFO_LENGTH];
                    int count = SystemXAttr.GetXAttr(ent.FullDataPath,
                        SystemXAttr.XATTR_FINDERINFO_NAME, fiBuf);
                    if (count == SystemXAttr.FINDERINFO_LENGTH) {
                        uint fileType = RawData.GetU32BE(fiBuf, 0);
                        uint creator = RawData.GetU32BE(fiBuf, 4);
                        if (fileType != 0 || creator != 0) {
                            ent.HFSFileType = fileType;
                            ent.HFSCreator = creator;
                        }
                    }
                }

                // Check for a resource fork if it doesn't already have one.
                if (mAddOpts.CheckNamed && !ent.HasRsrcFork) {
                    Debug.Assert(ent.HasDataFork);
                    string rsrcPath = ent.FullDataPath + RSRC_FORK_SUFFIX;
                    if (File.Exists(rsrcPath)) {
                        ent.HasRsrcFork = true;
                        ent.FullRsrcPath = rsrcPath;
                        ent.RsrcSource = AddFileEntry.SourceType.Plain;
                    }
                }
            }
        }

        /// <summary>
        /// Processes a file or directory.  On success, a new entry is created for the file, or
        /// an existing entry is updated.
        /// </summary>
        /// <param name="fullPath">Full normalized pathname.</param>
        /// <exception cref="FileNotFoundException">Path name wasn't found.</exception>
        private void ProcessPath(string fullPath) {
            if (Directory.Exists(fullPath)) {
                if (mAddOpts.Recurse) {
                    // Add an entry for the directory.  This is really only necessary if the
                    // directory is empty, but it does allow us to capture the timestamps.
                    AddFileEntry dirEntry = new AddFileEntry();
                    mFiles.Add(fullPath, dirEntry);
                    dirEntry.IsDirectory = true;
                    DirectoryInfo info = new DirectoryInfo(fullPath);
                    dirEntry.CreateWhen = info.CreationTime;
                    dirEntry.ModWhen = info.LastWriteTime;
                    SetStoragePath(dirEntry, fullPath, string.Empty, false);

                    ProcessDirectory(fullPath);
                }
                return;
            }
            if (!File.Exists(fullPath)) {
                throw new FileNotFoundException("File not found", fullPath);
            }

            string fileName = Path.GetFileName(fullPath);

            if (Importer != null) {
                // This is a host file that we are processing as an import.
                AddFileEntry newEntry = new AddFileEntry();
                mFiles.Add(fullPath, newEntry);
                if (Importer.HasDataFork) {
                    newEntry.HasDataFork = true;
                    newEntry.FullDataPath = fullPath;
                    newEntry.DataSource = AddFileEntry.SourceType.Import;
                }
                if (Importer.HasRsrcFork) {
                    newEntry.HasRsrcFork = true;
                    newEntry.FullRsrcPath = fullPath;
                    newEntry.RsrcSource = AddFileEntry.SourceType.Import;
                }
                Importer.GetFileTypes(out byte proType, out ushort proAux, out uint hfsFileType,
                    out uint hfsCreator);
                newEntry.FileType = proType;
                newEntry.AuxType = proAux;
                newEntry.HFSFileType = hfsFileType;
                newEntry.HFSCreator = hfsCreator;
                FileInfo info = new FileInfo(fullPath);
                newEntry.CreateWhen = info.CreationTime;
                newEntry.ModWhen = info.LastWriteTime;
                if ((info.Attributes & FileAttributes.ReadOnly) != 0) {
                    newEntry.Access = FileAttribs.FILE_ACCESS_LOCKED;
                }
                // Try to clip the extension off if configured to do so.
                string clipPath = fullPath;
                if (mAddOpts.StripExt) {
                    clipPath = Importer.StripExtension(fullPath);
                }
                SetStoragePath(newEntry, clipPath, string.Empty, false);
                return;
            }

            if (mAddOpts.ParseADF && fileName.StartsWith(AppleSingle.ADF_PREFIX)) {
                // Confirm that the file is in AppleDouble format.  If so, extract the file
                // type information and look for a resource fork.  Ignore data forks.
                if (CheckAppleDouble(fullPath)) {
                    // Entry added / updated.
                    return;
                }
            }

            //string ext = Path.GetExtension(fileName);

            if (mAddOpts.ParseAS && fileName.EndsWith(AppleSingle.AS_EXT,
                    StringComparison.InvariantCultureIgnoreCase)) {
                // Confirm that the file is in AppleSingle format.  If so, extract file
                // attributes and fork info.
                if (CheckAppleSingle(fullPath)) {
                    return;
                }
            }

            if (mAddOpts.ParseNAPS) {
                MatchCollection matches = sNaps6Regex.Matches(fileName);    // #XXYYYY
                if (matches.Count == 1) {
                    // Group 0 is full string, 1 is storage name, 2 is "hashtag", 3 is extra ext.
                    HandleNAPS(fullPath, matches[0].Groups[1].Value, matches[0].Groups[2].Value);
                    return;
                }
                matches = sNaps16Regex.Matches(fileName);                   // #XXXXXXXXYYYYYYYY
                if (matches.Count == 1) {
                    HandleNAPS(fullPath, matches[0].Groups[1].Value, matches[0].Groups[2].Value);
                    return;
                }
            }

            // This is a plain data file.  Add it or pair it.
            AddFileEntry? fileEntry;
            if (!mFiles.TryGetValue(fullPath, out fileEntry)) {
                fileEntry = new AddFileEntry();
                mFiles.Add(fullPath, fileEntry);
            }
            if (fileEntry.HasDataFork) {
                mAppHook.LogW("Data fork added twice: " + fullPath);
            }
            fileEntry.HasDataFork = true;
            fileEntry.FullDataPath = fullPath;
            fileEntry.DataSource = AddFileEntry.SourceType.Plain;
            if (!fileEntry.HasADFAttribs) {
                // Get the file dates and access mode, unless this is the data fork part of
                // an AppleDouble file, in which case we don't want to stomp on what we pulled
                // out of the ADF file earlier.
                FileInfo info = new FileInfo(fullPath);
                fileEntry.CreateWhen = info.CreationTime;
                fileEntry.ModWhen = info.LastWriteTime;
                if ((info.Attributes & FileAttributes.ReadOnly) != 0) {
                    fileEntry.Access = FileAttribs.FILE_ACCESS_LOCKED;
                }
            }
            SetStoragePath(fileEntry, fullPath, string.Empty, false);
        }

        /// <summary>
        /// Processes a directory.  Grabs the list of files, sorts it, and passes each entry
        /// to ProcessPath().
        /// </summary>
        private void ProcessDirectory(string pathName) {
            // TODO(maybe): create entries for directories, so we can add empty directories
            string[] names = Directory.GetFileSystemEntries(pathName);
            Array.Sort(names, StringComparer.OrdinalIgnoreCase);

            foreach (string entryPath in names) {
                ProcessPath(entryPath);
            }
        }

        /// <summary>
        /// Checks to see if a file is AppleSingle.  If so, the file is fully processed.
        /// </summary>
        /// <param name="fullPath">Path to file.</param>
        /// <returns>True if the file was handled.</returns>
        private bool CheckAppleSingle(string fullPath) {
            Debug.Assert(mAddOpts.ParseAS);
            using (FileStream stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read)) {
                if (!AppleSingle.TestKind(stream, mAppHook)) {
                    return false;
                }

                AddFileEntry? fileEntry;
                if (mFiles.TryGetValue(fullPath, out fileEntry)) {
                    mAppHook.LogW("AppleSingle added twice: " + fullPath);
                    return true;        // file has been handled
                } else {
                    fileEntry = new AddFileEntry();
                    mFiles.Add(fullPath, fileEntry);
                }

                using (IArchive archive = AppleSingle.OpenArchive(stream, mAppHook)) {
                    // Extract attributes.
                    IFileEntry asEnt = archive.GetFirstEntry();
                    Debug.Assert(asEnt != IFileEntry.NO_ENTRY);
                    if (!asEnt.HasDataFork && !asEnt.HasRsrcFork) {
                        mAppHook.LogI("Found content-free AppleSingle file: " + fullPath);
                        return true;        // file has been handled
                    }
                    if (TimeStamp.IsValidDate(asEnt.CreateWhen)) {
                        fileEntry.CreateWhen = asEnt.CreateWhen;
                    } else {
                        fileEntry.CreateWhen = TimeStamp.NO_DATE;
                    }
                    if (TimeStamp.IsValidDate(asEnt.ModWhen)) {
                        fileEntry.ModWhen = asEnt.ModWhen;
                    } else {
                        fileEntry.ModWhen = TimeStamp.NO_DATE;
                    }
                    fileEntry.FileType = asEnt.FileType;
                    fileEntry.AuxType = asEnt.AuxType;
                    fileEntry.HFSFileType = asEnt.HFSFileType;
                    fileEntry.HFSCreator = asEnt.HFSCreator;
                    fileEntry.Access = asEnt.Access;

                    if (asEnt.HasDataFork) {
                        fileEntry.HasDataFork = true;
                        fileEntry.FullDataPath = fullPath;
                        fileEntry.DataSource = AddFileEntry.SourceType.AppleSingle;
                    }
                    if (asEnt.HasRsrcFork) {
                        fileEntry.HasRsrcFork = true;
                        fileEntry.FullRsrcPath = fullPath;
                        fileEntry.RsrcSource = AddFileEntry.SourceType.AppleSingle;
                    }

                    string storedName = asEnt.FileName;
                    if (string.IsNullOrEmpty(storedName)) {
                        // Use the filename portion of the pathname, without the ".as".
                        storedName = Path.GetFileName(fullPath);
                        if (storedName.EndsWith(".as", StringComparison.InvariantCultureIgnoreCase)) {
                            storedName = storedName.Substring(0, storedName.Length - 3);
                        }
                    }

                    SetStoragePath(fileEntry, fullPath, storedName, false);
                }
            }
            return true;
        }

        /// <summary>
        /// Checks to see if a file is AppleDouble.  If so, the file is fully processed.
        /// </summary>
        /// <param name="fullPath">Path to file.</param>
        /// <returns>True if the file was handled.</returns>
        private bool CheckAppleDouble(string fullPath) {
            Debug.Assert(mAddOpts.ParseADF);
            using (FileStream stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read)) {
                if (!AppleSingle.TestDouble(stream, mAppHook)) {
                    return false;
                }

                // Look for a matching entry.  Clip the leading "._".
                string fileName = Path.GetFileName(fullPath);
                fileName = fileName.Substring(2);
                string clipPath = Path.Combine(Path.GetDirectoryName(fullPath)!, fileName);

                AddFileEntry? fileEntry;
                if (mFiles.TryGetValue(clipPath, out fileEntry)) {
                    Debug.Assert(fileEntry != null);
                } else {
                    fileEntry = new AddFileEntry();
                    mFiles.Add(clipPath, fileEntry);
                }

                using (IArchive archive = AppleSingle.OpenArchive(stream, mAppHook)) {
                    // Extract attributes.  Update timestamps if present in file.
                    IFileEntry dubEnt = archive.GetFirstEntry();
                    Debug.Assert(dubEnt != IFileEntry.NO_ENTRY);
                    if (TimeStamp.IsValidDate(dubEnt.CreateWhen)) {
                        fileEntry.CreateWhen = dubEnt.CreateWhen;
                    } else {
                        fileEntry.CreateWhen = TimeStamp.NO_DATE;
                    }
                    if (TimeStamp.IsValidDate(dubEnt.ModWhen)) {
                        fileEntry.ModWhen = dubEnt.ModWhen;
                    } else {
                        fileEntry.ModWhen = TimeStamp.NO_DATE;
                    }
                    fileEntry.FileType = dubEnt.FileType;
                    fileEntry.AuxType = dubEnt.AuxType;
                    fileEntry.HFSFileType = dubEnt.HFSFileType;
                    fileEntry.HFSCreator = dubEnt.HFSCreator;
                    fileEntry.Access = dubEnt.Access;
                    fileEntry.HasADFAttribs = true;

                    // Got resources?
                    if (dubEnt.HasRsrcFork) {
                        if (fileEntry.HasRsrcFork) {
                            mAppHook.LogW("Resource fork added twice: " + fullPath);
                        }
                        fileEntry.HasRsrcFork = true;
                        fileEntry.FullRsrcPath = fullPath;
                        fileEntry.RsrcSource = AddFileEntry.SourceType.AppleDouble;
                    }
                    SetStoragePath(fileEntry, clipPath, string.Empty, false);
                }
            }
            return true;
        }

        /// <summary>
        /// Handles a NAPS (NuLib2 Attribute Preservation String) pathname.
        /// </summary>
        /// <param name="fullPath">Full pathname to file on host filesystem.</param>
        /// <param name="storageName">Filename, without NAPS tag.</param>
        /// <param name="hashString">NAPS tag, with leading '#'.</param>
        private void HandleNAPS(string fullPath, string storageName, string hashString) {
            uint fileType;
            uint auxType;
            char partChar = '\0';

            // Parse the hash string.  '#' + 6 digits + optional char, or same with 16 digits.
            // It matched the regex, so all digits should be valid hex.
            if (hashString.Length <= 8) {
                fileType = Convert.ToUInt32(hashString.Substring(1, 2), 16);
                auxType = Convert.ToUInt32(hashString.Substring(3, 4), 16);
                if (hashString.Length == 8) {
                    partChar = char.ToLower(hashString[7]);
                }
            } else {
                fileType = Convert.ToUInt32(hashString.Substring(1, 8), 16);
                auxType = Convert.ToUInt32(hashString.Substring(9, 8), 16);
                if (hashString.Length == 18) {
                    partChar = char.ToLower(hashString[17]);
                }
            }

            if (partChar == 'i') {
                // Ignore disk images.
                mAppHook.LogW("Skipping NAPS disk image: " + fullPath);
                return;
            } else if (partChar != '\0' && partChar != 'd' && partChar != 'r') {
                mAppHook.LogW("Skipping unknown NAPS part: " + fullPath);
                return;
            }

            // Clip the NAPS and file extension off.
            string dirName = Path.GetDirectoryName(fullPath)!;
            string clipPath = Path.Combine(dirName, storageName);

            // Look for an existing entry.  Because of our filename manipulation, finding multiple
            // files that resolve to the same storage name is entirely possible.
            AddFileEntry? fileEntry;
            if (!mFiles.TryGetValue(clipPath, out fileEntry)) {
                fileEntry = new AddFileEntry();
                mFiles.Add(clipPath, fileEntry);
            }

            if (fileType <= 0xff && auxType <= 0xffff) {
                fileEntry.FileType = (byte)fileType;
                fileEntry.AuxType = (ushort)auxType;
            } else {
                fileEntry.HFSFileType = fileType;
                fileEntry.HFSCreator = auxType;
            }
            FileInfo info = new FileInfo(fullPath);
            fileEntry.CreateWhen = info.CreationTime;
            fileEntry.ModWhen = info.LastWriteTime;
            if ((info.Attributes & FileAttributes.ReadOnly) != 0) {
                fileEntry.Access = FileAttribs.FILE_ACCESS_LOCKED;
            }

            if (partChar == 'r') {
                if (fileEntry.HasRsrcFork) {
                    mAppHook.LogW("Resource fork added twice: " + fullPath);
                }
                fileEntry.HasRsrcFork = true;
                fileEntry.FullRsrcPath = fullPath;
                fileEntry.RsrcSource = AddFileEntry.SourceType.Plain;
            } else {
                if (fileEntry.HasDataFork) {
                    mAppHook.LogW("Data fork added twice: " + fullPath);
                }
                fileEntry.HasDataFork = true;
                fileEntry.FullDataPath = fullPath;
                fileEntry.DataSource = AddFileEntry.SourceType.Plain;
            }
            SetStoragePath(fileEntry, clipPath, string.Empty, true);
        }

        /// <summary>
        /// Sets the StoragePath and StorageName fields of the AddFileEntry.
        /// </summary>
        /// <param name="fileEntry">Entry to modify.</param>
        /// <param name="clipPath">Pathname with "clipped" filename.  The filename should have
        ///   leading "._" and trailing "#xxyyyy" removed.</param>
        /// <param name="storedName">Name stored within the source file, for AppleSingle.  This
        ///   may have characters that are not legal on the host filesystem.  May be empty.</param>
        private void SetStoragePath(AddFileEntry fileEntry, string clipPath, string storedName,
                bool doUnescape) {
            string dirName = Path.GetDirectoryName(clipPath)!;
            string fileName = Path.GetFileName(clipPath);

            string storageDir;
            if (dirName.StartsWith(mBasePath)) {
                // Save relative path.
                storageDir = dirName.Substring(mBasePath.Length +
                    (dirName.Length > mBasePath.Length ? 1 : 0));
            } else {
                // Use most of the full path.  The "root path" is "C:\" for a local Windows
                // file, but "\\webby\fadden" for a network share, so simply removing the
                // root may or may not leave a leading path separator.
                string rootName = Path.GetPathRoot(clipPath)!;
                string noRoot = dirName.Substring(rootName.Length);
                if (noRoot.StartsWith(Path.DirectorySeparatorChar)) {
                    noRoot = noRoot.Substring(1);
                }
                storageDir = noRoot;
            }
            fileEntry.StorageDir = storageDir;
            fileEntry.StorageDirSep = Path.DirectorySeparatorChar;

            // Use name stored in AppleSingle if available; otherwise use modified host filename.
            if (!string.IsNullOrEmpty(storedName)) {
                Debug.Assert(!doUnescape);
                fileEntry.StorageName = storedName;
            } else {
                if (doUnescape) {
                    fileName = PathName.UnescapeFileName(fileName,
                        PathName.NO_DIR_SEP /*mTargetDirSep*/);
                    fileName = PathName.PrintifyControlChars(fileName);
                }
                fileEntry.StorageName = fileName;
            }
        }
    }
}
