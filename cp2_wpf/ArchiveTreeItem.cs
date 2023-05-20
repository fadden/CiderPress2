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
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

using AppCommon;
using CommonUtil;
using DiskArc;
using DiskArc.Arc;
using DiskArc.Multi;
using static DiskArc.Defs;
using static DiskArc.IFileSystem;

namespace cp2_wpf {
    /// <summary>
    /// One entry in the archive tree hierarchy.
    /// </summary>
    public class ArchiveTreeItem : INotifyPropertyChanged {
        /// <summary>
        /// True if we number partitions and embedded volumes from 1, instead of 0.
        /// </summary>
        /// <remarks>
        /// <para>If this changes, also update ExtArchive for consistency with the CLI app.</para>
        /// </remarks>
        public static readonly bool ONE_BASED_INDEX = true;

        /// <summary>
        /// Type string to show in the GUI.
        /// </summary>
        public string TypeStr { get; set; }

        /// <summary>
        /// Name to show in the GUI.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// DiskArc library object.  May be IArchive, IDiskImage, IMultiPart, IFileSystem, or
        /// Partition.  The object's lifetime (i.e. when it's Disposed) is managed by DANode,
        /// either here or in the parent.
        /// </summary>
        public object DAObject { get; set; }

        public enum Status { None = 0, OK, Dubious, Warning, Error };
        public ControlTemplate? StatusIcon { get; set; }
        public ControlTemplate? ReadOnlyIcon { get; set; }

        /// <summary>
        /// Children.
        /// </summary>
        public ObservableCollection<ArchiveTreeItem> Items { get; set; }

        /// <summary>
        /// DiskArcNode object, for updates.  Will be null for partitions and sub-volumes,
        /// because they are wholly contained within a disk image.
        /// </summary>
        public DiskArcNode? DANode { get; set; }

        // INotifyPropertyChanged
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string propertyName = "") {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// Tied to TreeViewItem.IsExpanded.
        /// </summary>
        public bool IsExpanded {
            get { return mIsExpanded; }
            set {
                if (value != mIsExpanded) {
                    Debug.WriteLine("Tree: expanded '" + Name + "'=" + value);
                    mIsExpanded = value;
                    OnPropertyChanged();
                    // If we track the parent, we can propagate the expansion upward.
                }
            }
        }
        private bool mIsExpanded = true;

        /// <summary>
        /// Tied to TreeViewItem.IsSelected.
        /// </summary>
        public bool IsSelected {
            get { return mIsSelected; }
            set {
                //Debug.WriteLine("Tree: selected '" + Name + "'=" + value);
                if (value != mIsSelected) {
                    mIsSelected = value;
                    OnPropertyChanged();
                }
            }
        }
        private bool mIsSelected;


        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="typeStr">Type string to show in the TreeView.</param>
        /// <param name="name">Name to show in the TreeView.</param>
        /// <param name="status">Status icon to show in the TreeView.</param>
        /// <param name="isReadOnly">True if the daObject is read-only.</param>
        /// <param name="daObject">DiskArc library object (IArchive, IFileSystem, etc).</param>
        /// <param name="daNode">Leaf node associated with this element.  Will be null if
        ///   this is a partition/sub-volume, i.e. something that is part of a
        ///   greater object.</param>
        public ArchiveTreeItem(string typeStr, string name, Status status, bool isReadOnly,
                object daObject, DiskArcNode? daNode) {
            TypeStr = typeStr;
            Name = name;
            DAObject = daObject;
            DANode = daNode;

            // Should we cache resource refs for performance?  We shouldn't be using them often.
            switch (status) {
                case Status.None:
                    StatusIcon = null;
                    break;
                case Status.Dubious:
                    StatusIcon =
                        (ControlTemplate)Application.Current.FindResource("icon_StatusInvalid");
                    break;
                case Status.Warning:
                    StatusIcon =
                        (ControlTemplate)Application.Current.FindResource("icon_StatusWarning");
                    break;
                case Status.Error:
                    StatusIcon =
                        (ControlTemplate)Application.Current.FindResource("icon_StatusError");
                    break;
                default:
                    Debug.Assert(false);
                    break;
            }
            if (isReadOnly) {
                ReadOnlyIcon =
                    (ControlTemplate)Application.Current.FindResource("icon_StatusNoNoColor");
            }

            Items = new ObservableCollection<ArchiveTreeItem>();
        }

        public static ArchiveTreeItem? FindItemByEntry(ObservableCollection<ArchiveTreeItem> tvRoot,
                IFileEntry fileEntry) {
            foreach (ArchiveTreeItem treeItem in tvRoot) {
                if (treeItem.DANode != null && treeItem.DANode.EntryInParent == fileEntry) {
                    return treeItem;
                }
                ArchiveTreeItem? found = FindItemByEntry(treeItem.Items, fileEntry);
                if (found != null) {
                    // TODO: shouldn't do this here.  Add an "expand sub-tree" method.  Need
                    //   to add a parent reference?
                    treeItem.IsExpanded = true;
                    return found;
                }
            }
            return null;
        }

        public override string ToString() {
            return "[ArchiveTreeItem: name='" + Name + "' daObject=" + DAObject +
                " daNode=" + DANode + "]";
        }


        #region Construction

        /// <summary>
        /// Constructs a section of the archive tree.  This is called recursively.
        /// </summary>
        /// <param name="tvRoot">Root of sub-tree.</param>
        /// <param name="pathName">Name of entry to process.  This could be on the host filesystem
        ///   or within the archive.</param>
        /// <param name="ext">Filename extension, usually from the pathname.</param>
        /// <param name="stream">Stream for the entry.</param>
        /// <param name="leafObj">Object (IArchive or IDiskImage) for the entry.</param>
        /// <param name="parentNode">Parent node in the DiskArcNode tree.</param>
        /// <param name="entryInParent">File entry for this entry.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <returns>Error string, or the empty string on success.</returns>
        public static string ConstructTree(ObservableCollection<ArchiveTreeItem> tvRoot,
                string pathName, string ext, Stream stream, IDisposable leafObj,
                DiskArcNode parentNode, IFileEntry entryInParent, AppHook appHook) {
            appHook.LogD("Scanning '" + pathName + "' entry=" + entryInParent);

            // Add a node for this item.
            DiskArcNode? leafNode;
            string typeStr;
            Status status;
            bool isReadOnly;
            if (leafObj is IArchive) {
                IArchive arc = (IArchive)leafObj;
                leafNode = new ArchiveNode(parentNode, stream, arc, entryInParent, appHook);
                typeStr = ThingString.IArchive(arc);
                if (arc.IsDubious) {
                    status = Status.Dubious;
                } else if (arc.Notes.WarningCount > 0 || arc.Notes.ErrorCount > 0) {
                    status = Status.Warning;
                } else {
                    status = Status.None;
                }
                isReadOnly = arc.IsDubious;
            } else if (leafObj is IDiskImage) {
                IDiskImage disk = (IDiskImage)leafObj;
                leafNode = new DiskImageNode(parentNode, stream, disk, entryInParent, appHook);
                typeStr = ThingString.IDiskImage(disk);
                if (disk.IsDubious) {
                    status = Status.Dubious;
                } else if (disk.Notes.WarningCount > 0 || disk.Notes.ErrorCount > 0) {
                    status = Status.Warning;
                } else {
                    status = Status.None;
                }
                isReadOnly = disk.IsReadOnly;
            } else {
                string errMsg = "Unexpected result from IdentifyStreamContents: " + leafObj;
                leafObj.Dispose();
                return errMsg;
            }
            ArchiveTreeItem newItem = new ArchiveTreeItem(typeStr, Path.GetFileName(pathName),
                status, isReadOnly, leafObj, leafNode);
            tvRoot.Add(newItem);

            if (leafObj is IArchive) {
                ProcessArchive(newItem.Items, (IArchive)leafObj, pathName, leafNode, appHook);
            } else if (leafObj is IDiskImage) {
                ProcessDiskImage(newItem.Items, (IDiskImage)leafObj, leafNode, appHook);
            } else {
                Debug.Assert(false);
                return "Unexpected leaf obj: " + leafObj;
            }

            return string.Empty;
        }

        private static void ProcessArchive(ObservableCollection<ArchiveTreeItem> tvRoot,
                IArchive archive, string pathName, DiskArcNode parentNode, AppHook appHook) {
            bool doOpen = AppSettings.Global.GetBool(AppSettings.DEEP_SCAN_ARCHIVES, false);

            // Even if SCAN_ARCHIVE_TREE is false, we want to open whatever's inside gzip and
            // SDK archives to save the user a double-click.  Also, for gzip, we want to use
            // the file extension from the archive's pathname, not the stored filename.
            string firstExt = string.Empty;
            if (archive is GZip) {
                if (pathName.EndsWith(".gz", StringComparison.InvariantCultureIgnoreCase)) {
                    firstExt = Path.GetExtension(pathName.Substring(0, pathName.Length - 3));
                } else {
                    firstExt = Path.GetExtension(pathName);
                }
                doOpen = true;
            } else if (archive is NuFX) {
                IEnumerator<IFileEntry> numer = archive.GetEnumerator();
                if (numer.MoveNext()) {
                    // has at least one entry
                    if (numer.Current.IsDiskImage && !numer.MoveNext()) {
                        // single entry, is disk image
                        firstExt = ".po";
                        doOpen = true;
                    }
                }
            }

            if (!doOpen) {
                return;
            }

            // Evaluate entries to see if they look like archives or disk images.  If the
            // attributes are favorable, open the file and peek at it.
            foreach (IFileEntry entry in archive) {
                string ext;
                if (firstExt == string.Empty) {
                    if (!FileIdentifier.HasDiskImageAttribs(entry, out ext) &&
                            !FileIdentifier.HasFileArchiveAttribs(entry, out ext)) {
                        continue;
                    }
                } else {
                    ext = firstExt;
                }
                FilePart part = entry.IsDiskImage ? FilePart.DiskImage : FilePart.DataFork;
                try {
                    Debug.WriteLine("+++ extract to temp: " + entry.FullPathName);
                    Stream stream = ArcTemp.ExtractToTemp(archive, entry, part);
                    IDisposable? obj = IdentifyStreamContents(stream, ext, appHook,
                        out string errorMsg, out bool hasFiles);
                    if (obj == null || !hasFiles) {
                        obj?.Dispose();
                        stream.Close();
                    } else {
                        ConstructTree(tvRoot, entry.FullPathName, ext, stream, obj, parentNode,
                            entry, appHook);
                    }
                } catch (InvalidDataException ex) {
                    Debug.WriteLine("Failed to extract " + entry + ": " + ex.Message);
                    // continue with next entry
                }
            }
        }

        private static void ProcessDiskImage(ObservableCollection<ArchiveTreeItem> tvRoot,
                IDiskImage diskImage, DiskArcNode parentNode, AppHook appHook) {
            // AnalyzeDisk() has already been called.  We want to set up tree items for the
            // filesystem, or for multiple partitions.
            if (diskImage.Contents is IFileSystem) {
                ProcessFileSystem(tvRoot, (IFileSystem)diskImage.Contents, parentNode, appHook);
            } else if (diskImage.Contents is IMultiPart) {
                ProcessPartitions(tvRoot, (IMultiPart)diskImage.Contents, parentNode, appHook);
            } else {
                // Nothing recognizable.  Sector editing is still possible.
            }
        }

        private static void ProcessFileSystem(ObservableCollection<ArchiveTreeItem> tvRoot,
                IFileSystem fs, DiskArcNode parentNode, AppHook appHook) {
            string typeStr = ThingString.IFileSystem(fs);
            string name;
            try {
                // Use the filesystem volume name rather than the disk image pathname.
                fs.PrepareFileAccess(true);
                name = fs.GetVolDirEntry().FileName;
            } catch (DAException) {
                name = "(unknown)";
            }
            Status status;
            if (fs.IsDubious) {
                status = Status.Dubious;
            } else if (fs.Notes.WarningCount > 0 || fs.Notes.ErrorCount > 0) {
                status = Status.Warning;
            } else {
                status = Status.None;
            }
            ArchiveTreeItem newItem = new ArchiveTreeItem(typeStr, name,
                status, fs.IsReadOnly, fs, parentNode
                /*TODO: use parent for updates; this is a temporary hack*/);
            // TODO: would be better to be able to walk up the tree and find the parent.  This
            //   currently works but I worry that it's fragile.
            tvRoot.Add(newItem);

            // Check for embedded volumes.
            IMultiPart? embeds = fs.FindEmbeddedVolumes();
            if (embeds != null) {
                ProcessPartitions(newItem.Items, embeds, parentNode, appHook);
            }

            // If deep scan is enabled, scan for more stuff to open.
            bool doDeepScan = AppSettings.Global.GetBool(AppSettings.DEEP_SCAN_FILESYSTEMS, false);
            if (doDeepScan) {
                ScanFileSystem(newItem.Items, fs, parentNode, appHook);
            }
        }

        private static void ProcessPartitions(ObservableCollection<ArchiveTreeItem> tvRoot,
                IMultiPart partitions, DiskArcNode parentNode, AppHook appHook) {
            // Create an entry for the partition map object.
            string typeStr = ThingString.IMultiPart(partitions);
            string name = "partitions";
            ArchiveTreeItem newItem = new ArchiveTreeItem(typeStr, name,
                partitions.IsDubious ? Status.Dubious : Status.None, false, partitions, null);
            tvRoot.Add(newItem);

            int index = ONE_BASED_INDEX ? 1 : 0;
            foreach (Partition part in partitions) {
                ProcessPartition(newItem.Items, part, index, parentNode, appHook);
                index++;
            }
        }

        private static void ProcessPartition(ObservableCollection<ArchiveTreeItem> tvRoot,
                Partition part, int index, DiskArcNode parentNode, AppHook appHook) {
            // Create entries for each partition.
            string name = "Part #" + index;
            if (part is APM_Partition) {
                name += ": " + ((APM_Partition)part).PartitionName;
            }
            ArchiveTreeItem newItem = new ArchiveTreeItem(string.Empty, name,
                Status.None, false, part, null);
            tvRoot.Add(newItem);

            part.AnalyzePartition();
            if (part.FileSystem != null) {
                ProcessFileSystem(newItem.Items, part.FileSystem, parentNode, appHook);
            }
        }

        private static void ScanFileSystem(ObservableCollection<ArchiveTreeItem> tvRoot,
                IFileSystem fs, DiskArcNode parentNode, AppHook appHook) {
            IFileEntry volDir = fs.GetVolDirEntry();
            ScanDirectory(tvRoot, fs, volDir, parentNode, appHook);
        }

        private static void ScanDirectory(ObservableCollection<ArchiveTreeItem> tvRoot,
                IFileSystem fs, IFileEntry dirEntry, DiskArcNode parentNode, AppHook appHook) {
            foreach (IFileEntry entry in dirEntry) {
                if (entry.IsDirectory) {
                    ScanDirectory(tvRoot, fs, entry, parentNode, appHook);
                } else {
                    string ext;
                    if (!FileIdentifier.HasDiskImageAttribs(entry, out ext) &&
                            !FileIdentifier.HasFileArchiveAttribs(entry, out ext)) {
                        continue;
                    }
                    FilePart part = entry.IsDiskImage ? FilePart.DiskImage : FilePart.DataFork;
                    try {
                        Debug.WriteLine("+++ scanning: " + entry.FullPathName);
                        FileAccessMode accessMode = fs.IsReadOnly ?
                            FileAccessMode.ReadOnly : FileAccessMode.ReadWrite;

                        Stream stream = fs.OpenFile(entry, accessMode, part);
                        IDisposable? obj = IdentifyStreamContents(stream, ext, appHook,
                            out string errorMsg, out bool hasFiles);
                        if (obj == null || !hasFiles) {
                            obj?.Dispose();
                            stream.Close();
                        } else {
                            ConstructTree(tvRoot, entry.FullPathName, ext, stream, obj, parentNode,
                                entry, appHook);
                        }
                    } catch (InvalidDataException ex) {
                        Debug.WriteLine("Failed to extract " + entry + ": " + ex.Message);
                        // continue with next entry
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
        /// <param name="errorMsg">Result: error message string.</param>
        /// <param name="hasFiles">Result: true if we think there are files here.</param>
        /// <returns>IArchive, IDiskImage, or (if analysis fails) null.</returns>
        internal static IDisposable? IdentifyStreamContents(Stream stream, string ext,
                AppHook appHook, out string errorMsg, out bool hasFiles) {
            errorMsg = string.Empty;
            hasFiles = false;

            ext = ext.ToLowerInvariant();
            if (stream.Length == 0 && ext != ".bny" && ext != ".bqy") {
                errorMsg = "File is empty";
                return null;
            }
            // Analyze file structure.
            FileAnalyzer.AnalysisResult result = FileAnalyzer.Analyze(stream, ext,
                appHook, out FileKind kind, out SectorOrder orderHint);
            if (result != FileAnalyzer.AnalysisResult.Success) {
                errorMsg = "Unable to recognize file contents: " +
                    ThingString.AnalysisResult(result);
                return null;
            }
            if (IsDiskImageFile(kind)) {
                IDiskImage? diskImage = FileAnalyzer.PrepareDiskImage(stream, kind, appHook);
                if (diskImage == null) {
                    errorMsg = "Unable to open file as " + ThingString.FileKind(kind);
                    return null;
                }
                // Do the disk analysis while we have the order hint handy.
                diskImage.AnalyzeDisk(null, orderHint, IDiskImage.AnalysisDepth.Full);
                hasFiles = (diskImage.Contents != null);
                return diskImage;
            } else {
                IArchive? archive = FileAnalyzer.PrepareArchive(stream, kind, appHook);
                if (archive == null) {
                    errorMsg = "Unable to open file as " + ThingString.FileKind(kind);
                    return null;
                }
                hasFiles = true;
                return archive;
            }
        }

        #endregion Construction
    }
}
