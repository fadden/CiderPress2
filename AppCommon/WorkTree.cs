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
    /// <para>Tree of DiskArc library objects we're working on.  This includes all multi-part
    /// formats, filesystems, embedded volumes, and file archives, which may be nested inside
    /// each other.</para>
    /// </summary>
    /// <remarks>
    /// <para>This is intended for use from a GUI application, possibly running on a background
    /// thread, so progress updates and some of the customization are handled through
    /// callbacks.</para>
    /// <para><see cref="DiskArcNode"/> handles propagation of modifications.</para>
    /// </remarks>
    public class WorkTree {
        /// <summary>
        /// True if we number partitions and embedded volumes from 1, instead of 0.
        /// </summary>
        /// <remarks>
        /// <para>If this changes, also update ExtArchive for consistency with the CLI app.</para>
        /// </remarks>
        public const bool ONE_BASED_INDEX = true;

        /// <summary>
        /// One node in the work tree.
        /// </summary>
        public class Node {
            /// <summary>
            /// DiskArc library object.  May be IArchive, IDiskImage, IMultiPart, IFileSystem, or
            /// Partition.
            /// </summary>
            public object DAObject { get; private set; }

            /// <summary>
            /// DiskArcNode associated with this element.  Will be null if this node represents
            /// part of a disk, e.g. IFileSystem or Partition.
            /// </summary>
            public DiskArcNode? DANode { get; private set; }

            public enum Status { None = 0, OK, Dubious, Warning, Error };

            public List<Node> Children { get; private set; } = new List<Node>();

            public Node? Parent { get; private set; }

            public Node(object daObject) {
                DAObject = daObject;
                DANode = null;
            }

            public Node(object daObject, DiskArcNode daNode) {
                DAObject = daObject;
                DANode = daNode;
            }
        }

        public Node RootNode { get; private set; }

        private string mHostPathName;
        private HostFileNode mHostFileNode;
        private AppHook mAppHook;

        /// <summary>
        /// Callback function used to decide if we want to go deeper.
        /// </summary>
        private DepthLimiter mDepthLimitFunc;
        public delegate bool DepthLimiter(bool parentIsArc, bool childIsArc);

        /// <summary>
        /// Opens a file, unfolding a work tree.
        /// </summary>
        /// <param name="hostPathName">Pathname to file.</param>
        /// <param name="depthLimitFunc">Callback function that determines whether or not we
        ///   descend into children.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <exception cref="IOException">File open or read failed.</exception>
        /// <exception cref="InvalidDataException">File contents not recognized.</exception>
        public WorkTree(string hostPathName, DepthLimiter depthLimitFunc, AppHook appHook) {
            mHostPathName = hostPathName;
            mDepthLimitFunc = depthLimitFunc;
            mAppHook = appHook;

            mHostFileNode = new HostFileNode(hostPathName, appHook);

            Stream? hostStream = null;
            try {
                try {
                    // Try to open with read-write access.
                    hostStream = new FileStream(hostPathName, FileMode.Open, FileAccess.ReadWrite,
                        FileShare.Read);
                } catch (IOException) {
                    // Retry with read-only access.
                    appHook.LogI("R/W open failed, retrying R/O: '" + hostPathName + "'");
                    hostStream = new FileStream(hostPathName, FileMode.Open, FileAccess.Read,
                        FileShare.Read);
                }

                string ext = Path.GetExtension(hostPathName);
                IDisposable? leafObj = IdentifyStreamContents(hostStream, ext, mHostPathName,
                    appHook, out string errorMsg, out bool hasFiles);
                if (leafObj == null) {
                    throw new InvalidDataException(errorMsg);
                }

                // TODO: build tree
            } finally {
                hostStream?.Close();
            }

        }

        /// <summary>
        /// Analyzes the contents of a stream, and returns the result of the analysis.
        /// </summary>
        /// <param name="stream">Stream to analyze.</param>
        /// <param name="ext">Filename extension associated with the stream.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <returns>IArchive, IDiskImage, or (if analysis fails) null.</returns>
        public static IDisposable? IdentifyStreamContents(Stream stream, string ext,
                AppHook appHook) {
            return IdentifyStreamContents(stream, ext, string.Empty, appHook, out string unused1,
                out bool unused2);
        }

        /// <summary>
        /// Analyzes the contents of a stream, and returns the result of the analysis.
        /// </summary>
        /// <param name="stream">Stream to analyze.</param>
        /// <param name="ext">Filename extension associated with the stream.</param>
        /// <param name="label">Label to include in error messages.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <param name="errorMsg">Result: error message string.</param>
        /// <param name="hasFiles">Result: true if we think there are files here.</param>
        /// <returns>IArchive, IDiskImage, or (if analysis fails) null.</returns>
        public static IDisposable? IdentifyStreamContents(Stream stream, string ext,
                string label, AppHook appHook, out string errorMsg, out bool hasFiles) {
            errorMsg = string.Empty;
            hasFiles = false;

            ext = ext.ToLowerInvariant();
            if (stream.Length == 0 && ext != ".bny" && ext != ".bqy") {
                errorMsg = "file is empty";
                return null;
            }
            // Analyze file structure.
            FileAnalyzer.AnalysisResult result = FileAnalyzer.Analyze(stream, ext,
                appHook, out FileKind kind, out SectorOrder orderHint);
            if (result != FileAnalyzer.AnalysisResult.Success) {
                errorMsg = "unable to recognize contents of '" + label + "': " +
                    ThingString.AnalysisResult(result) + " (ext='" + ext + "')";
                return null;
            }
            if (IsDiskImageFile(kind)) {
                IDiskImage? diskImage = FileAnalyzer.PrepareDiskImage(stream, kind, appHook);
                if (diskImage == null) {
                    errorMsg = "unable to open " + ThingString.FileKind(kind) + ": " + label;
                    return null;
                }
                // Do the disk analysis while we have the order hint handy.
                diskImage.AnalyzeDisk(null, orderHint, IDiskImage.AnalysisDepth.Full);
                hasFiles = (diskImage.Contents != null);
                return diskImage;
            } else {
                IArchive? archive = FileAnalyzer.PrepareArchive(stream, kind, appHook);
                if (archive == null) {
                    errorMsg = "unable to open " + ThingString.FileKind(kind) + ": " + label;
                    return null;
                }
                hasFiles = true;
                return archive;
            }
        }
    }
}
