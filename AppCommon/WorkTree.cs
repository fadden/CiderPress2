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
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

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
    /// <para>This is the tree that we show the user, so that they can examine every part of
    /// every file.  The expectation is that there will be a 1:1 mapping between this and a
    /// TreeView control.</para>
    /// </summary>
    /// <remarks>
    /// <para>This is intended for use from a GUI application, possibly running on a background
    /// thread, so progress updates and some of the customization are handled through
    /// callbacks.  Raising or listening for events requires multi-thread awareness.</para>
    /// <para><see cref="DiskArcNode"/> handles propagation of modifications.  This tree holds
    /// a superset of the DiskArcNode tree nodes, but does not include the host file.</para>
    /// </remarks>
    public class WorkTree : IDisposable {
        /// <summary>
        /// True if we number partitions and embedded volumes from 1, instead of 0.
        /// </summary>
        /// <remarks>
        /// I think it looks better to start at 1, but it sure feels weird.  If everything
        /// that cares references this constant it'll be easier to undo.
        /// </remarks>
        public const bool ONE_BASED_INDEX = true;

        /// <summary>
        /// One node in the work tree.
        /// </summary>
        /// <remarks>
        /// <para>The node's children are available through enumeration.</para>
        /// </remarks>
        public class Node : IEnumerable<Node> {
            /// <summary>
            /// Human-readable label.
            /// </summary>
            public string Label { get; set; } = string.Empty;

            /// <summary>
            /// Human-readable type string for DAObject.
            /// </summary>
            public string TypeStr { get; set; } = string.Empty;

            /// <summary>
            /// Node status indicator.
            /// </summary>
            public Status NodeStatus { get; set; } = Status.Unknown;
            public enum Status { Unknown = 0, OK, Dubious, Warning, Error };

            /// <summary>
            /// True if DAObject is read-only.
            /// </summary>
            public bool IsReadOnly { get; set; } = false;

            /// <summary>
            /// Place to stash the order hint for IDiskImage nodes, in case we need to
            /// reprocess them after a sector edit.
            /// </summary>
            public SectorOrder OrderHint { get; set; }

            /// <summary>
            /// DiskArc library object.  May be IArchive, IDiskImage, IMultiPart, IFileSystem, or
            /// Partition.
            /// </summary>
            /// <remarks>
            /// This is not owned by the work tree node.  Disposable objects are owned by
            /// the DiskArcNode hierarchy (e.g. DiskImageNode owns IDiskImage, which owns
            /// the IFileSystem, etc).
            /// </remarks>
            public object DAObject { get; private set; }

            /// <summary>
            /// DiskArcNode associated with this element.  Will be null if this node represents
            /// part of a disk, e.g. IFileSystem or Partition.
            /// </summary>
            public DiskArcNode? DANode { get; private set; }

            /// <summary>
            /// Reference to parent node.
            /// </summary>
            public Node? Parent { get; private set; }

            /// <summary>
            /// List of children.  Accessible via enumeration or <see cref="GetChildren"/>.
            /// </summary>
            private List<Node> mChildren = new List<Node>();

            public IEnumerator<Node> GetEnumerator() {
                return ((IEnumerable<Node>)mChildren).GetEnumerator();
            }
            IEnumerator IEnumerable.GetEnumerator() {
                return ((IEnumerable)mChildren).GetEnumerator();
            }
            public int Count { get { return mChildren.Count; } }


            /// <summary>
            /// Constructor for objects without a DiskArcNode.
            /// </summary>
            /// <param name="daObject">DiskArc object.</param>
            /// <param name="parent">Parent node reference.</param>
            public Node(object daObject, Node? parent) {
                DAObject = daObject;
                Parent = parent;
                DANode = null;
            }

            /// <summary>
            /// Constructor for objects with a DiskArcNode.
            /// </summary>
            /// <param name="daObject">DiskArc object.</param>
            /// <param name="parent">Parent node reference.</param>
            /// <param name="daNode">DiskArcNode reference.</param>
            public Node(object daObject, Node? parent, DiskArcNode daNode) {
                DAObject = daObject;
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

            /// <summary>
            /// Copy of the list of child nodes.
            /// </summary>
            public Node[] GetChildren() {
                return mChildren.ToArray();
            }

            /// <summary>
            /// Finds the DiskArcNode for this node.  If the node is an IDiskImage child such as
            /// IFileSystem, this will walk up the tree to find the closest ancestor.
            /// </summary>
            /// <returns>DiskArcNode from current node or closest ancestor.</returns>
            public DiskArcNode FindDANode() {
                Node node = this;
                while (node.DANode == null) {
                    if (node.Parent == null) {
                        throw new Exception("Internal error: ran off top of tree");
                    }
                    node = node.Parent;
                }
                return node.DANode;
            }

            /// <summary>
            /// Closes all of the children of the specified node, but leaves the node itself open.
            /// This is typically called on IDiskImage (which has a DANode) and Partition (which
            /// does not).
            /// </summary>
            /// <param name="node">Target node.</param>
            public void CloseChildren() {
                foreach (Node child in mChildren) {
                    CloseNode(child);
                }
                mChildren.Clear();
            }

            /// <summary>
            /// Recursively closes the specified node and all of its children.
            /// </summary>
            /// <param name="node">Target node.</param>
            private static void CloseNode(Node node) {
                foreach (Node child in node) {
                    CloseNode(child);
                }
                node.mChildren.Clear();
                // Close the corresponding DiskArcNode, and remove it from the tree.
                if (node.DANode != null) {
                    DiskArcNode? daParent = node.DANode.Parent;
                    if (daParent != null) {
                        daParent.DisposeChild(node.DANode);
                    }
                }
            }

            /// <summary>
            /// Adds a new child to this node.
            /// </summary>
            public void AddChild(Node child) {
                mChildren.Add(child);
            }

            public override string ToString() {
                return "[WTNode: label='" + Label + "' daObject=" + DAObject.GetType().Name +
                    " daNode=" + (DANode == null ? "(null)" : DANode.GetType().Name) + "]";
            }
        }

        /// <summary>
        /// Root of the tree.  This will hold an IArchive or IDiskImage.
        /// </summary>
        public Node RootNode { get; private set; }

        /// <summary>
        /// True if the host file is writable.
        /// </summary>
        public bool CanWrite { get; private set; }

        private string mHostPathName;
        private HostFileNode mHostFileNode;
        private BackgroundWorker? mWorker;
        private AppHook mAppHook;

        /// <summary>
        /// Callback function used to decide if we want to go deeper.
        /// </summary>
        /// <remarks>
        /// <para>We want to avoid scanning through child objects if we have no possible
        /// interest in opening them, especially if they're stored compressed in an archive.</para>
        /// <para>This feels a bit ad hoc, and it is: it exactly fits the current requirements.
        /// We don't really have anything more appropriate to use at the moment.</para>
        /// </remarks>
        /// <param name="parentKind">Kind of file we're currently in.</param>
        /// <param name="childKind">Kind of file we're thinking about prying open.</param>
        /// <returns>True if we want to descend.</returns>
        public DepthLimiter DepthLimitFunc { get; set; }

        public delegate bool DepthLimiter(DepthParentKind parentKind, DepthChildKind childKind);
        public enum DepthParentKind {
            Unknown = 0, Zip, GZip, NuFX, Archive, FileSystem, MultiPart };
        public enum DepthChildKind {
            Unknown = 0, AnyFile, FileArchive, DiskImage, DiskPart, Embed };


        /// <summary>
        /// Constructor.  Opens a file, unfolding a work tree.
        /// </summary>
        /// <param name="hostPathName">Pathname to file.</param>
        /// <param name="depthLimitFunc">Callback function that determines whether or not we
        ///   descend into children.</param>
        /// <param name="readOnly">If true, try to open the file read-only first.</param>
        /// <param name="worker">Background worker, for cancel requests and progress
        ///   updates.  Only used during construction.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <exception cref="IOException">File open or read failed.</exception>
        /// <exception cref="InvalidDataException">File contents not recognized.</exception>
        public WorkTree(string hostPathName, DepthLimiter depthLimitFunc, bool readOnly,
                BackgroundWorker? worker, AppHook appHook) {
            mHostPathName = hostPathName;
            DepthLimitFunc = depthLimitFunc;
            mWorker = worker;
            mAppHook = appHook;

            DateTime startWhen = DateTime.Now;
            appHook.LogI("Constructing work tree for '" + hostPathName + "'");
            mHostFileNode = new HostFileNode(hostPathName, appHook);

            Stream? hostStream = null;
            try {
                try {
                    // Try to open with read-write access unless read-only requested.
                    FileAccess access = readOnly ? FileAccess.Read : FileAccess.ReadWrite;
                    hostStream = new FileStream(hostPathName, FileMode.Open, access,
                        FileShare.Read);
                } catch (IOException ex) {
                    // Retry with read-only access unless we did that the first time around.
                    if (readOnly) {
                        throw;
                    }
                    appHook.LogI("R/W open failed (" + ex.Message + "), retrying R/O: '" +
                        hostPathName + "'");
                    hostStream = new FileStream(hostPathName, FileMode.Open, FileAccess.Read,
                        FileShare.Read);
                }

                string ext = Path.GetExtension(hostPathName);
                // This will throw InvalidDataException if the stream isn't recognized.  Let
                // the caller handle the exception (it has the error message).
                Node? newNode = ProcessStream(hostStream, ext, Path.GetFileName(hostPathName),
                        null, mHostFileNode, IFileEntry.NO_ENTRY, out string errorMsg);
                if (newNode == null) {
                    throw new InvalidDataException(errorMsg);
                }
                if (mWorker != null && mWorker.CancellationPending) {
                    appHook.LogW("Work tree construction was cancelled");
                    throw new Exception("cancelled");   // kind doesn't matter, just want cleanup
                }
                RootNode = newNode;
                CanWrite = hostStream.CanWrite;

                hostStream = null;      // we're good, don't let "finally" close it
            } catch {
                mHostFileNode.Dispose();
                throw;
            } finally {
                hostStream?.Close();
            }

            appHook.LogD("Work tree construction finished in " +
                (DateTime.Now - startWhen).TotalMilliseconds + " ms");

            // Clear this so we don't try to use it for updates later.
            mWorker = null;
        }

        /// <summary>
        /// Evalutes whether a file entry is a disk image or file archive.  If so, the item
        /// is opened and added to the tree, using the defined auto-open rules.
        /// </summary>
        /// <remarks>
        /// Do not call here if the entry is already in the tree.
        /// </remarks>
        /// <param name="workNode">Node for the IArchive or IFileSystem that the entry is a
        ///   part of.</param>
        /// <param name="entry">File entry in the archive or filesystem.</param>
        /// <returns>New node, if the disk image or archive was added to the tree.</returns>
        public Node? TryCreateSub(Node workNode, IFileEntry entry) {
            IArchive? arc = workNode.DAObject as IArchive;
            IFileSystem? fs = workNode.DAObject as IFileSystem;
            if (!(arc == null ^ fs == null)) {
                Debug.Assert(false, "Unexpected: arc=" + arc + " fs=" + fs);
                return null;
            }

            if (entry.IsDirectory) {
                return null;        // this should probably have been caught by caller
            }

            if (arc != null) {
                Debug.Assert(workNode.DANode != null);
                DepthChildKind childKind = ExamineArchiveEntry(arc, entry, workNode.Label,
                    out string ext);
                if (childKind == DepthChildKind.Unknown) {
                    return null;
                }

                FilePart part = entry.IsDiskImage ? FilePart.DiskImage : FilePart.DataFork;
                try {
                    Debug.WriteLine("+++ extract to temp: " + entry.FullPathName);
                    Stream tmpStream = ArcTemp.ExtractToTemp(arc, entry, part);
                    Node? newNode = ProcessStream(tmpStream, ext, entry.FullPathName, workNode,
                        workNode.DANode, entry, out string errorMsg);
                    if (newNode != null) {
                        workNode.AddChild(newNode);
                        return newNode;
                    }
                } catch (InvalidDataException ex) {
                    Debug.WriteLine("Failed to extract " + entry + ": " + ex.Message);
                }
                return null;
            } else if (fs != null) {
                // IFileSystem doesn't have a DiskArcNode, so we need to search up the tree for
                // the parent disk image.
                Debug.Assert(workNode.DANode == null);
                DiskArcNode daParent = workNode.Parent!.FindDANode();

                string ext;
                DepthChildKind childKind = ExamineFileSystemEntry(fs, entry, out ext);
                if (childKind == DepthChildKind.Unknown) {
                    return null;
                }

                FilePart part = entry.IsDiskImage ? FilePart.DiskImage : FilePart.DataFork;
                Stream? stream = null;
                try {
                    Debug.WriteLine("+++ scanning: " + entry.FullPathName);
                    FileAccessMode accessMode = fs.IsReadOnly ?
                        FileAccessMode.ReadOnly : FileAccessMode.ReadWrite;

                    stream = fs.OpenFile(entry, accessMode, part);
                    Node? newNode = ProcessStream(stream, ext, entry.FullPathName, workNode,
                        daParent, entry, out string errorMsg);
                    if (newNode != null) {
                        stream = null;      // now held by the DiskArcNode
                        workNode.AddChild(newNode);
                        return newNode;
                    }
                } catch (IOException ex) {
                    Debug.WriteLine("Failed to extract " + entry + ": " + ex.Message);
                } finally {
                    stream?.Close();
                }
                return null;
            } else {
                throw new NotImplementedException("Neither IArchive nor IFileSystem: " + workNode);
            }
        }

        /// <summary>
        /// Opens the Nth partition in an IMultiPart.  The partition must not already be open.
        /// </summary>
        /// <param name="workNode">IMultiPart node.</param>
        /// <param name="index">Index, 0-based or 1-based (<see cref="ONE_BASED_INDEX"/>).</param>
        /// <returns>New node, if the partition was opened and added to the tree.</returns>
        public Node? TryCreatePartition(Node workNode, int index) {
            IMultiPart? partitions = workNode.DAObject as IMultiPart;
            if (partitions == null) {
                Debug.Assert(false, "expected IMultiPart, got: " + workNode.DAObject);
                return null;
            }

            // All Partition objects exist in the IMultiPart, but we may not have created
            // WorkTree nodes for them.
            int partIndex = index - (ONE_BASED_INDEX ? 1 : 0);
            if (partIndex < 0 || partIndex >= partitions.Count) {
                Debug.Assert(false, "bad index " + index + ", count=" + partitions.Count);
                return null;
            }
            Partition part = partitions[partIndex];

            // See if there's a child node with the Partition object.
            foreach (Node childNode in workNode) {
                if (childNode.DAObject == part) {
                    Debug.Assert(false, "partition already has a tree node");
                    return null;
                }
            }

            DiskArcNode daParent = workNode.Parent!.FindDANode();
            Node newNode = ProcessPartition(part, index, workNode, daParent);
            return newNode;
        }

        /// <summary>
        /// Tries to add a multi-part object to the tree.
        /// </summary>
        /// <param name="workNode">Parent node (will be IFileSystem for embeds).</param>
        /// <param name="partitions">IMultiPart reference.</param>
        /// <returns>New node, or null on failure.</returns>
        public Node? TryCreateMultiPart(Node workNode, IMultiPart partitions) {
            DiskArcNode daParent = workNode.Parent!.FindDANode();
            return ProcessMultiPart(partitions, workNode, daParent);
        }

        /// <summary>
        /// Processes a stream with uncertain contents to see if it contains a disk image or
        /// file archive.  If so, new nodes are created for the work tree and the DiskArcNode
        /// tree.
        /// </summary>
        /// <param name="stream">Data stream.</param>
        /// <param name="ext">File extension to use when analyzing the stream.</param>
        /// <param name="pathName">Original pathname.  The extension may differ in certain
        ///   cases (e.g. gzip and .SDK contents).</param>
        /// <param name="parentNode">Work tree node parent.</param>
        /// <param name="daParent">DiskArcNode tree parent.</param>
        /// <param name="entryInParent">File entry in parent node's DAObject.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <param name="errorMsg">Error message with failure information.</param>
        /// <returns>New work tree node, or null if the stream isn't disk or archive.</returns>
        private Node? ProcessStream(Stream stream, string ext, string pathName,
                Node? parentNode, DiskArcNode daParent, IFileEntry entryInParent,
                out string errorMsg) {
            if (mWorker != null) {
                if (mWorker.CancellationPending) {
                    errorMsg = "cancelled";
                    return null;
                }
                mWorker.ReportProgress(0, pathName);
            }
            IDisposable? leafObj = IdentifyStreamContents(stream, ext, pathName, mAppHook,
                out errorMsg, out bool hasFiles, out SectorOrder orderHint);
            if (leafObj == null) {
                return null;
            }

            DiskArcNode? leafNode;
            string typeStr;
            Node.Status status;
            Node newNode;
            if (leafObj is IArchive) {
                IArchive arc = (IArchive)leafObj;
                leafNode = new ArchiveNode(daParent, stream, arc, entryInParent, mAppHook);

                typeStr = ThingString.IArchive(arc);
                if (arc.IsDubious) {
                    status = Node.Status.Dubious;
                } else if (arc.Notes.WarningCount > 0 || arc.Notes.ErrorCount > 0) {
                    status = Node.Status.Warning;
                } else {
                    status = Node.Status.OK;
                }
                newNode = new Node(leafObj, parentNode, leafNode) {
                    Label = pathName,
                    TypeStr = typeStr,
                    NodeStatus = status,
                    IsReadOnly = arc.IsDubious
                };

                HandleFileArchive((IArchive)leafObj, newNode, leafNode, pathName);
            } else if (leafObj is IDiskImage) {
                IDiskImage disk = (IDiskImage)leafObj;
                leafNode = new DiskImageNode(daParent, stream, disk, entryInParent, mAppHook);

                typeStr = ThingString.IDiskImage(disk);
                if (disk.IsDubious) {
                    status = Node.Status.Dubious;
                } else if (disk.Notes.WarningCount > 0 || disk.Notes.ErrorCount > 0) {
                    status = Node.Status.Warning;
                } else {
                    status = Node.Status.OK;
                }
                newNode = new Node(leafObj, parentNode, leafNode) {
                    Label = pathName,
                    TypeStr = typeStr,
                    NodeStatus = status,
                    IsReadOnly = disk.IsReadOnly,
                    OrderHint = orderHint
                };

                HandleDiskImage((IDiskImage)leafObj, newNode, leafNode);
            } else {
                throw new NotImplementedException(
                    "Unexpected result from IdentifyStreamContents: " + leafObj);
            }
            return newNode;
        }

        /// <summary>
        /// Processes the contents of a file archive.
        /// </summary>
        private void HandleFileArchive(IArchive arc, Node arcNode, DiskArcNode arcDANode,
                string arcPathName) {
            DepthParentKind parentKind;

            string firstExt = string.Empty;
            if (arc is GZip) {
                parentKind = DepthParentKind.GZip;
            } else if (arc is NuFX) {
                parentKind = DepthParentKind.NuFX;
            } else if (arc is Zip) {
                parentKind = DepthParentKind.Zip;
            } else {
                parentKind = DepthParentKind.Archive;
            }

            // If we're not interested in opening any sort of child, bail out now.
            if (!DepthLimitFunc(parentKind, DepthChildKind.AnyFile)) {
                return;
            }

            // Evaluate entries to see if they look like archives or disk images.  If the
            // attributes are favorable, open the file and peek at it.
            foreach (IFileEntry entry in arc) {
                string ext;
                DepthChildKind childKind = ExamineArchiveEntry(arc, entry, arcPathName, out ext);
                if (childKind == DepthChildKind.Unknown) {
                    continue;
                }
                if (!DepthLimitFunc(parentKind, childKind)) {
                    continue;
                }

                FilePart part = entry.IsDiskImage ? FilePart.DiskImage : FilePart.DataFork;
                try {
                    Debug.WriteLine("+++ extract to temp: " + entry.FullPathName);
                    Stream tmpStream = ArcTemp.ExtractToTemp(arc, entry, part);
                    Node? newNode = ProcessStream(tmpStream, ext, entry.FullPathName, arcNode,
                        arcDANode, entry, out string errorMsg);
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

        /// <summary>
        /// Processes a disk image.  It may hold a filesystem, multi-partition layout, or
        /// nothing recognizable (which can still be sector-edited).
        /// </summary>
        public void HandleDiskImage(IDiskImage disk, Node diskImageNode,
                DiskArcNode diskDANode) {
            Debug.Assert(diskImageNode.Count == 0);

            // AnalyzeDisk() has already been called.  We want to set up tree items for the
            // filesystem, or for multiple partitions.
            if (disk.Contents is IFileSystem) {
                ProcessFileSystem((IFileSystem)disk.Contents, diskImageNode, diskDANode);
            } else if (disk.Contents is IMultiPart) {
                ProcessMultiPart((IMultiPart)disk.Contents, diskImageNode, diskDANode);
            } else {
                // Nothing recognizable.  Sector editing is still possible.
            }
        }

        /// <summary>
        /// Reprocesses a disk image node.  Used when reopening children after a sector edit.
        /// </summary>
        /// <param name="diskImageNode">Target node.</param>
        public void ReprocessDiskImage(Node diskImageNode) {
            Debug.Assert(diskImageNode.Count == 0);
            Debug.Assert(diskImageNode.DANode is DiskImageNode);
            Debug.Assert(mHostFileNode.CheckHealth());

            IDiskImage disk = (IDiskImage)diskImageNode.DAObject;
            disk.AnalyzeDisk(null, diskImageNode.OrderHint, IDiskImage.AnalysisDepth.Full);
            HandleDiskImage((IDiskImage)diskImageNode.DAObject, diskImageNode,
                diskImageNode.DANode);
            Debug.Assert(mHostFileNode.CheckHealth());
        }

        /// <summary>
        /// Processes the partitions in a multi-partition layout.
        /// </summary>
        private Node? ProcessMultiPart(IMultiPart partitions, Node parentNode,
                DiskArcNode daParent) {
            // Create an entry for the partition map object.
            string typeStr = ThingString.IMultiPart(partitions);
            Node.Status status = partitions.IsDubious ? Node.Status.Dubious : Node.Status.OK;
            Node multiNode = new Node(partitions, parentNode) {
                Label = "partitions",
                TypeStr = typeStr,
                NodeStatus = status,
                IsReadOnly = false
            };
            parentNode.AddChild(multiNode);

            if (DepthLimitFunc(DepthParentKind.MultiPart, DepthChildKind.AnyFile)) {
                int index = ONE_BASED_INDEX ? 1 : 0;
                foreach (Partition part in partitions) {
                    ProcessPartition(part, index, multiNode, daParent);
                    index++;
                }
            }
            return multiNode;
        }

        /// <summary>
        /// Processes a single partition.  If we find a filesystem, go process that.
        /// </summary>
        private Node ProcessPartition(Partition part, int index, Node parentNode,
                DiskArcNode daParent) {
            Debug.Assert(parentNode.DAObject is IMultiPart);
            // Create an entry for this partition.
            string typeStr = ThingString.Partition(part);
            string label = "#" + index;
            if (part is APM_Partition) {
                label += ": " + ((APM_Partition)part).PartitionName;
            }
            Node partNode = new Node(part, parentNode) {
                Label = label,
                TypeStr = typeStr,
                NodeStatus = Node.Status.OK,
                IsReadOnly = false
            };
            parentNode.AddChild(partNode);

            part.AnalyzePartition();
            if (part.FileSystem != null) {
                ProcessFileSystem(part.FileSystem, partNode, daParent);
            }
            return partNode;
        }

        /// <summary>
        /// Reprocesses a partition node.  Used when reopening children after a sector edit.
        /// </summary>
        /// <param name="partNode">Target node.</param>
        public void ReprocessPartition(Node partNode) {
            Debug.Assert(partNode.Count == 0);
            Debug.Assert(partNode.DANode == null);
            Debug.Assert(mHostFileNode.CheckHealth());
            Partition part = (Partition)partNode.DAObject;
            DiskArcNode daParent = partNode.FindDANode();
            part.AnalyzePartition();
            if (part.FileSystem != null) {
                ProcessFileSystem(part.FileSystem, partNode, daParent);
            }
            Debug.Assert(mHostFileNode.CheckHealth());
        }

        /// <summary>
        /// Processes a filesystem, looking for file archives and disk images.
        /// </summary>
        private void ProcessFileSystem(IFileSystem fs, Node diskImageNode, DiskArcNode daParent) {
            // Generate a label for the filesystem.
            string label;
            try {
                // Use the filesystem volume name rather than the disk image pathname.
                fs.PrepareFileAccess(true);
                label = fs.GetVolDirEntry().FileName;
            } catch (DAException) {
                label = "(unknown)";
            }
            string typeStr = ThingString.IFileSystem(fs);
            Node.Status status;
            if (fs.IsDubious) {
                status = Node.Status.Dubious;
            } else if (fs.Notes.WarningCount > 0 || fs.Notes.ErrorCount > 0) {
                status = Node.Status.Warning;
            } else {
                status = Node.Status.OK;
            }

            // Add a node for the filesystem.  No DiskArcNode.
            Node fsNode = new Node(fs, diskImageNode) {
                Label = label,
                TypeStr = typeStr,
                NodeStatus = status,
                IsReadOnly = fs.IsReadOnly
            };
            diskImageNode.AddChild(fsNode);

            // Check for embedded volumes.
            if (DepthLimitFunc(DepthParentKind.FileSystem, DepthChildKind.Embed)) {
                IMultiPart? embeds = fs.FindEmbeddedVolumes();
                if (embeds != null) {
                    ProcessMultiPart(embeds, fsNode, daParent);
                }
            }

            if (DepthLimitFunc(DepthParentKind.FileSystem, DepthChildKind.AnyFile)) {
                ScanFileSystem(fs, fsNode, daParent);
            }
        }

        /// <summary>
        /// Scans a filesystem, looking for disk images and file archives.
        /// </summary>
        private void ScanFileSystem(IFileSystem fs, Node parentNode, DiskArcNode daParent) {
            Debug.Assert(DepthLimitFunc(DepthParentKind.FileSystem, DepthChildKind.AnyFile));
            IFileEntry volDir = fs.GetVolDirEntry();
            ScanDirectory(fs, volDir, parentNode, daParent);
        }

        /// <summary>
        /// Recursively scans the directory structure.
        /// </summary>
        private void ScanDirectory(IFileSystem fs, IFileEntry dirEntry, Node fsNode,
                DiskArcNode daParent) {
            foreach (IFileEntry entry in dirEntry) {
                if (entry.IsDirectory) {
                    ScanDirectory(fs, entry, fsNode, daParent);
                } else {
                    string ext;
                    DepthChildKind childKind = ExamineFileSystemEntry(fs, entry, out ext);
                    if (childKind == DepthChildKind.Unknown) {
                        continue;
                    }
                    if (!DepthLimitFunc(DepthParentKind.FileSystem, childKind)) {
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
                            daParent, entry, out string errorMsg);
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

        private static DepthChildKind ExamineArchiveEntry(IArchive arc, IFileEntry entry,
                string arcPathName, out string ext) {
            DepthChildKind childKind;
            if (entry.IsDiskImage) {
                childKind = DepthChildKind.DiskPart;
                // For .SDK we always want to treat it as a ProDOS-ordered image.
                ext = ".po";
            } else {
                if (FileIdentifier.HasDiskImageAttribs(entry, out ext)) {
                    childKind = DepthChildKind.DiskImage;
                } else if (FileIdentifier.HasFileArchiveAttribs(entry, out ext)) {
                    childKind = DepthChildKind.FileArchive;
                } else {
                    // Doesn't look like disk image or file archive.
                    return DepthChildKind.Unknown;
                }

                // For gzip we want to use the name of the gzip archive without the ".gz".
                // The name stored inside the archive may be out of date.
                if (arc is GZip) {
                    if (arcPathName.EndsWith(".gz", StringComparison.InvariantCultureIgnoreCase)) {
                        ext = Path.GetExtension(arcPathName.Substring(0, arcPathName.Length - 3));
                    }
                    // If it doesn't end with ".gz", use whatever we found earlier.
                }
            }
            return childKind;
        }

        private static DepthChildKind ExamineFileSystemEntry(IFileSystem fs, IFileEntry entry,
                out string ext) {
            DepthChildKind childKind;
            if (FileIdentifier.HasDiskImageAttribs(entry, out ext)) {
                childKind = DepthChildKind.DiskImage;
            } else if (FileIdentifier.HasFileArchiveAttribs(entry, out ext)) {
                childKind = DepthChildKind.FileArchive;
            } else {
                childKind = DepthChildKind.Unknown;
            }
            return childKind;
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
                out bool unused2, out SectorOrder unused3);
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
                string label, AppHook appHook, out string errorMsg, out bool hasFiles,
                out SectorOrder orderHint) {
            errorMsg = string.Empty;
            hasFiles = false;
            orderHint = SectorOrder.Unknown;

            ext = ext.ToLowerInvariant();
            if (stream.Length == 0 && ext != ".bny" && ext != ".bqy") {
                errorMsg = "file is empty";
                return null;
            }
            // Analyze file structure.
            FileAnalyzer.AnalysisResult result = FileAnalyzer.Analyze(stream, ext,
                appHook, out FileKind kind, out orderHint);
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

        public bool CheckHealth() {
            //Debug.Write(mHostFileNode.GenerateTreeSummary());
            return mHostFileNode.CheckHealth();
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
                mAppHook.LogD("Disposing work tree (disp=" + disposing + ")");
                mHostFileNode.Dispose();
            }
        }

        /// <summary>
        /// Generates an ASCII-art diagram of the work tree.
        /// </summary>
        public string GenerateTreeSummary() {
            StringBuilder sb = new StringBuilder();
            GenerateTreeSummary(RootNode, 0, "", true, sb);
            return sb.ToString();
        }

        /// <summary>
        /// Recursively generates an ASCII-art diagram of the work tree.
        /// </summary>
        private static void GenerateTreeSummary(Node node, int depth, string indent,
                bool isLastSib, StringBuilder sb) {
            sb.Append(indent);
            sb.Append("+-");
            sb.Append(node.TypeStr);
            sb.Append(": ");
            sb.Append(node.Label);
            if (node.IsReadOnly) {
                sb.Append(" -RO-");
            }
            if (node.NodeStatus != Node.Status.OK) {
                sb.Append(" *");
                sb.Append(node.NodeStatus);
                sb.Append('*');
            }
            if (node.DANode != null) {
                sb.Append(" [");
                sb.Append(node.DANode.GetType().Name);
                sb.Append("]");
            }
            sb.AppendLine();

            Node[] children = node.GetChildren();
            for (int i = 0; i < children.Length; i++) {
                string newIndent = indent + (isLastSib ? "  " : "| ");
                GenerateTreeSummary(children[i], depth + 1, newIndent, i == children.Length - 1,
                    sb);
            }
        }
    }
}
