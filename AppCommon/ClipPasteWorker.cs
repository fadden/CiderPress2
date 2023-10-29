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
using DiskArc;
using DiskArc.Arc;
using static DiskArc.Defs;
using static DiskArc.IFileSystem;

namespace AppCommon {
    /// <summary>
    /// <para>Adds a collection of files to an IArchive or IFileSystem object, where the files are
    /// provided as a pairing of metadata and open-on-demand read-only streams.  Uses callbacks
    /// to display progress and warning messages, and to query for handling of conflicts.</para>
    /// <para>This fills the same role as <see cref="AddFileWorker"/>, but for a platform-specific
    /// clipboard paste function.</para>
    /// </summary>
    public class ClipPasteWorker {
        /// <summary>
        /// Callback function interface definition.
        /// </summary>
        public delegate CallbackFacts.Results CallbackFunc(CallbackFacts what);

        /// <summary>
        /// Stream generator function interface definition.
        /// </summary>
        /// <param name="clipEntry">Entry to generate a stream for.</param>
        /// <returns>Write-only, non-seekable output stream, or null if no stream is
        ///   available for the specified entry.</returns>
        public delegate Stream? ClipStreamGenerator(ClipFileEntry clipEntry);

        /// <summary>
        /// If set, files added to archives with compression features will be compressed using
        /// the default compression format.
        /// </summary>
        public bool DoCompress { get; set; } = true;

        /// <summary>
        /// If set, files added to a ZIP archive that have resource forks or HFS types will
        /// be stored as AppleDouble with a "__MACOSX" prefix.
        /// </summary>
        public bool EnableMacOSZip { get; set; } = false;

        /// <summary>
        /// If set, strip pathnames off of files before adding them.  For a filesystem, all
        /// files will be added to the target directory.
        /// </summary>
        public bool StripPaths { get; set; } = false;

        /// <summary>
        /// If set, use raw mode when adding files to filesystems (notably DOS 3.x).
        /// </summary>
        public bool RawMode { get; set; } = false;

        /// <summary>
        /// Callback function, for progress updates, warnings, and problem resolution.
        /// </summary>
        private CallbackFunc mFunc;

        /// <summary>
        /// Application hook reference.
        /// </summary>
        private AppHook mAppHook;


        public ClipPasteWorker(List<ClipFileEntry>? ClipEntries, ClipStreamGenerator clipStreamGen,
                CallbackFunc func, bool doCompress, bool macZip, bool stripPaths, bool rawMode,
                AppHook appHook) {
            mFunc = func;
            DoCompress = doCompress;
            EnableMacOSZip = macZip;
            StripPaths = stripPaths;
            RawMode = rawMode;
            mAppHook = appHook;
        }

        public void AddFilesToArchive(IArchive archive, out bool isCancelled) {
            isCancelled = false;
        }

        public void AddFilesToDisk(IFileSystem fileSystem, IFileEntry targetDir,
                out bool isCancelled) {
            isCancelled = false;
        }
    }
}
