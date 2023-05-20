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
using DiskArc.Arc;
using DiskArc.Multi;
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
    public class WorkTree : IDisposable {
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
            public string Label { get; private set; }

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

            /// <summary>
            /// Node status indicator.
            /// </summary>
            public Status NodeStatus { get; private set; }
            public enum Status { None = 0, OK, Dubious, Warning, Error };

            // TODO: IsReadOnly, TypeString

            /// <summary>
            /// Copy of list of child nodes.
            /// </summary>
            public Node[] Children { get { return mChildren.ToArray(); } }

            /// <summary>
            /// Reference to parent node.
            /// </summary>
            public Node? Parent { get; private set; }

            private List<Node> mChildren = new List<Node>();


            /// <summary>
            /// Constructor for objects without a DiskArcNode.
            /// </summary>
            /// <param name="daObject">DiskArc object.</param>
            /// <param name="parent">Parent node reference.</param>
            public Node(object daObject, string label, Node? parent) {
                DAObject = daObject;
                Label = label;
                Parent = parent;
                DANode = null;
            }

            /// <summary>
            /// Constructor for objects with a DiskArcNode.
            /// </summary>
            /// <param name="daObject">DiskArc object.</param>
            /// <param name="parent">Parent node reference.</param>
            /// <param name="daNode">DiskArcNode reference.</param>
            public Node(object daObject, string label, Node? parent, DiskArcNode daNode) {
                DAObject = daObject;
                Label = label;
                Parent = parent;
                DANode = daNode;
            }

            /// <summary>
            /// Saves any changes made to the associated DAObject.
            /// </summary>
            /// <param name="doCompress">True if compression should be used for archives.</param>
            public void SaveUpdates(bool doCompress) {
                if (DANode != null) {
                    DANode.SaveUpdates(doCompress);
                } else {
                    Parent!.SaveUpdates(doCompress);
                }
            }

            public void AddChild(Node child) {
                mChildren.Add(child);
            }
        }

        /// <summary>
        /// Root of the tree.  This will hold an IArchive or IDiskImage.
        /// </summary>
        public Node RootNode { get; private set; }

        private string mHostPathName;
        private HostFileNode mHostFileNode;
        private AppHook mAppHook;

        /// <summary>
        /// Callback function used to decide if we want to go deeper.  Returns true if we should
        /// proceed.
        /// </summary>
        private DepthLimiter mDepthLimitFunc;

        public delegate bool DepthLimiter(FileKind parentKind, FileKind childKind,
            string candidateName);

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
                // This will throw InvalidDataException if the stream isn't recognized.  Let
                // the caller handle the exception (it has the error message).
                Node? newNode = ProcessStream(hostStream, ext, hostPathName, null, mHostFileNode,
                    IFileEntry.NO_ENTRY, appHook, out string errorMsg);
                if (newNode == null) {
                    throw new InvalidDataException(errorMsg);
                }
                RootNode = newNode;

                hostStream = null;      // don't close in "finally"
            } finally {
                hostStream?.Close();
            }
        }

        private static Node? ProcessStream(Stream stream, string ext, string pathName,
                Node? parentNode, DiskArcNode daParent, IFileEntry entryInParent, AppHook appHook,
                out string errorMsg) {
            IDisposable? leafObj = IdentifyStreamContents(stream, ext, pathName,
                appHook, out errorMsg, out bool hasFiles);
            if (leafObj == null) {
                return null;
            }

            DiskArcNode? leafNode;
            Node newNode;
            if (leafObj is IArchive) {
                IArchive arc = (IArchive)leafObj;
                leafNode = new ArchiveNode(daParent, stream, arc, entryInParent, appHook);
                newNode = new Node(leafObj, pathName, parentNode, leafNode);
                ProcessFileArchive((IArchive)leafObj, stream, newNode, leafNode, pathName, appHook);
            } else if (leafObj is IDiskImage) {
                IDiskImage disk = (IDiskImage)leafObj;
                leafNode = new DiskImageNode(daParent, stream, disk, entryInParent, appHook);
                newNode = new Node(leafObj, pathName, parentNode, leafNode);
                ProcessDiskImage((IDiskImage)leafObj, stream, newNode, leafNode, appHook);
            } else {
                throw new NotImplementedException(
                    "Unexpected result from IdentifyStreamContents: " + leafObj);
            }
            return newNode;
        }

        private static void ProcessFileArchive(IArchive arc, Stream UNUSED, Node arcNode,
                DiskArcNode daParent, string pathName, AppHook appHook) {
            string firstExt = string.Empty;
            if (arc is GZip) {
                if (pathName.EndsWith(".gz", StringComparison.InvariantCultureIgnoreCase)) {
                    firstExt = Path.GetExtension(pathName.Substring(0, pathName.Length - 3));
                } else {
                    firstExt = Path.GetExtension(pathName);
                }
            } else if (arc is NuFX) {
                IEnumerator<IFileEntry> numer = arc.GetEnumerator();
                if (numer.MoveNext()) {
                    // has at least one entry
                    if (numer.Current.IsDiskImage && !numer.MoveNext()) {
                        // single entry, is disk image
                        firstExt = ".po";
                    }
                }
            }

            // Evaluate entries to see if they look like archives or disk images.  If the
            // attributes are favorable, open the file and peek at it.
            foreach (IFileEntry entry in arc) {
                string ext;
                if (firstExt == string.Empty) {
                    if (!FileIdentifier.HasDiskImageAttribs(entry, out ext) &&
                            !FileIdentifier.HasArchiveAttribs(entry, out ext)) {
                        continue;
                    }
                } else {
                    ext = firstExt;
                }
                FilePart part = entry.IsDiskImage ? FilePart.DiskImage : FilePart.DataFork;
                try {
                    Debug.WriteLine("+++ extract to temp: " + entry.FullPathName);
                    Stream tmpStream = ArcTemp.ExtractToTemp(arc, entry, part);
                    Node? newNode = ProcessStream(tmpStream, ext, entry.FullPathName, arcNode,
                        daParent, entry, appHook, out string errorMsg);
                    if (newNode == null) {
                        // Nothing was recognized.
                        return;
                    }
                    arcNode.AddChild(newNode);
                } catch (InvalidDataException ex) {
                    Debug.WriteLine("Failed to extract " + entry + ": " + ex.Message);
                    // continue with next entry
                }
            }
        }

        private static void ProcessDiskImage(IDiskImage disk, Stream UNUSED, Node diskImageNode,
                DiskArcNode daParent, AppHook appHook) {
            // AnalyzeDisk() has already been called.  We want to set up tree items for the
            // filesystem, or for multiple partitions.
            if (disk.Contents is IFileSystem) {
                ProcessFileSystem((IFileSystem)disk.Contents, diskImageNode, daParent, appHook);
            } else if (disk.Contents is IMultiPart) {
                ProcessPartitions((IMultiPart)disk.Contents, diskImageNode, daParent, appHook);
            } else {
                // Nothing recognizable.  Sector editing is still possible.
            }
        }

        private static void ProcessFileSystem(IFileSystem fs, Node diskImageNode,
                DiskArcNode daParent, AppHook appHook) {
            // Generate a label for the filesystem.
            string label;
            try {
                // Use the filesystem volume name rather than the disk image pathname.
                fs.PrepareFileAccess(true);
                label = fs.GetVolDirEntry().FileName;
            } catch (DAException) {
                label = "(unknown)";
            }

            // Add a node for the filesystem.  No DiskArcNode.
            Node fsNode = new Node(fs, label, diskImageNode);
            diskImageNode.AddChild(fsNode);

            // Check for embedded volumes.
            IMultiPart? embeds = fs.FindEmbeddedVolumes();
            if (embeds != null) {
                ProcessPartitions(embeds, fsNode, daParent, appHook);
            }

            ScanFileSystem(fs, fsNode, daParent, appHook);
        }

        private static void ProcessPartitions(IMultiPart partitions, Node parentNode,
                DiskArcNode daParent, AppHook appHook) {
            // Create an entry for the partition map object.
            string typeStr = ThingString.IMultiPart(partitions);
            string label = "partitions";
            Node multiNode = new Node(partitions, label, parentNode);
            parentNode.AddChild(multiNode);

            int index = ONE_BASED_INDEX ? 1 : 0;
            foreach (Partition part in partitions) {
                ProcessPartition(part, index, multiNode, daParent, appHook);
                index++;
            }
        }

        private static void ProcessPartition(Partition part, int index, Node parentNode,
                DiskArcNode daParent, AppHook appHook) {
            // Create an entry for this partition.
            string label = "Part #" + index;
            if (part is APM_Partition) {
                label += ": " + ((APM_Partition)part).PartitionName;
            }
            Node partNode = new Node(part, label, parentNode);
            parentNode.AddChild(partNode);

            part.AnalyzePartition();
            if (part.FileSystem != null) {
                ProcessFileSystem(part.FileSystem, partNode, daParent, appHook);
            }
        }

        private static void ScanFileSystem(IFileSystem fs, Node parentNode, DiskArcNode daParent,
                AppHook appHook) {
            IFileEntry volDir = fs.GetVolDirEntry();
            ScanDirectory(fs, volDir, parentNode, daParent, appHook);
        }

        private static void ScanDirectory(IFileSystem fs, IFileEntry dirEntry,
                Node fsNode, DiskArcNode daParent, AppHook appHook) {
            foreach (IFileEntry entry in dirEntry) {
                if (entry.IsDirectory) {
                    ScanDirectory(fs, entry, fsNode, daParent, appHook);
                } else {
                    string ext;
                    if (!FileIdentifier.HasDiskImageAttribs(entry, out ext) &&
                            !FileIdentifier.HasArchiveAttribs(entry, out ext)) {
                        continue;
                    }
                    FilePart part = entry.IsDiskImage ? FilePart.DiskImage : FilePart.DataFork;
                    Stream? stream = null;
                    try {
                        Debug.WriteLine("+++ scanning: " + entry.FullPathName);
                        FileAccessMode accessMode = fs.IsReadOnly ?
                            FileAccessMode.ReadOnly : FileAccessMode.ReadWrite;

                        stream = fs.OpenFile(entry, accessMode, part);
                        Node? newNode = ProcessStream(stream, ext, entry.FullPathName, fsNode,
                            daParent, entry, appHook, out string errorMsg);
                        if (newNode == null) {
                            // Nothing was recognized.
                            return;
                        }
                        stream = null;      // now held by a DiskArcNode
                        fsNode.AddChild(newNode);
                    } catch (IOException ex) {
                        Debug.WriteLine("Failed to extract " + entry + ": " + ex.Message);
                        // continue with next entry
                    } finally {
                        stream?.Close();
                    }
                }
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

        ~WorkTree() {
            Dispose(false);
        }
        public void Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        protected virtual void Dispose(bool disposing) {
            if (disposing) {
                mHostFileNode.Dispose();
            }
        }
    }
}
