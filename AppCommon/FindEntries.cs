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

namespace AppCommon {
    /// <summary>
    /// Functions for gathering a set of file entries in a disk image or file archive, based on
    /// a set of pathname glob patterns.
    /// </summary>
    public static class FindEntries {
        private static readonly List<Glob> EMPTY_GLOB_LIST = new List<Glob>();

        /// <summary>
        /// Finds all matching entries in the file archive.
        /// </summary>
        /// <param name="arc">File archive to search.</param>
        /// <param name="globList">List of match patterns.</param>
        /// <param name="doRecurse">Imitate recursive directory selection.</param>
        /// <returns>List of file entries.</returns>
        public static List<IFileEntry> FindMatchingEntries(IArchive arc, List<Glob> globList,
                bool doRecurse) {
            List<IFileEntry> result = new List<IFileEntry>();

            char dirSep = arc.Characteristics.DefaultDirSep;
            foreach (IFileEntry entry in arc) {
                string entryName = entry.FileName;
                if (globList.Count == 0) {
                    result.Add(entry);
                } else {
                    bool matched = false;
                    foreach (Glob glob in globList) {
                        // If we allow matching on the entry name prefix, we get equivalent
                        // behavior to recursive directory descent.  Otherwise, we get complete
                        // matches only.
                        if (glob.IsMatch(entryName, dirSep, doRecurse)) {
                            if (!matched) {
                                matched = true;
                                result.Add(entry);
                            }
                            // don't break; need to test all globs (for glob.HasMatched)
                        }
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// Finds all matching entries in the disk filesystem.
        /// </summary>
        /// <param name="fs">Filesystem to search.</param>
        /// <param name="startDirEntry">Start point of search.</param>
        /// <param name="globList">List of match patterns.</param>
        /// <param name="doRecurse">Perform recursive directory selection.</param>
        /// <returns>List of file entries.</returns>
        public static List<IFileEntry> FindMatchingEntries(IFileSystem fs,
                IFileEntry startDirEntry, List<Glob> globList, bool doRecurse) {
            if (startDirEntry != IFileEntry.NO_ENTRY) {
                Debug.Assert(startDirEntry.GetFileSystem() == fs);
                Debug.Assert(startDirEntry.IsDirectory);
            } else {
                startDirEntry = fs.GetVolDirEntry();
            }
            List<IFileEntry> result = new List<IFileEntry>();
            char dirSep = fs.Characteristics.DirSep;
            ScanDirectory(startDirEntry, string.Empty, dirSep, globList, doRecurse, result);
            return result;
        }

        /// <summary>
        /// Recursively scans a filesystem for matching entries.
        /// </summary>
        /// <param name="dirEntry">Current directory.</param>
        /// <param name="pathName">Partial pathname of current directory.  Will be an empty
        ///   string initially, whether we're at the volume dir or the root of the ext-archive
        ///   specification.</param>
        /// <param name="dirSep">Directory separator character for the filesystem.</param>
        /// <param name="globList">List of glob match patterns.  Will be empty if we are
        ///   matching everything.</param>
        /// <param name="entries">List of entries to update.</param>
        private static void ScanDirectory(IFileEntry dirEntry, string pathName, char dirSep,
                List<Glob> globList, bool doRecurse, List<IFileEntry> entries) {
            foreach (IFileEntry entry in dirEntry) {
                string entryPath;
                //// Adjust for AppleWorks, so that the user can enter a pattern that matches
                //// what we show in the "catalog" output (' ' vs. '.').
                //string fileName =
                //    AWName.ChangeAppleWorksCase(entry.FileName, entry.FileType, entry.AuxType);
                string fileName = entry.FileName;
                if (pathName.Length == 0) {
                    entryPath = fileName;
                } else {
                    entryPath = pathName + dirSep + fileName;
                }
                bool matched = false;
                if (globList.Count == 0) {
                    matched = true;
                    entries.Add(entry);
                } else {
                    foreach (Glob glob in globList) {
                        // Accept complete matches only.
                        if (glob.IsMatch(entryPath, dirSep, false)) {
                            if (!matched) {
                                matched = true;
                                entries.Add(entry);
                            }
                            // don't break; need to test all globs (for glob.HasMatched)
                        }
                    }
                }

                if (entry.IsDirectory) {
                    // If this directory matched, and we want recursive directory descent, pass
                    // an empty glob list so everything beneath is included.
                    ScanDirectory(entry, entryPath, dirSep,
                        matched && doRecurse ? EMPTY_GLOB_LIST : globList, doRecurse, entries);
                }
            }
        }
    }
}
