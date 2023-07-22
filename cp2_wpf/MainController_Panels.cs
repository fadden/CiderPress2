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
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using AppCommon;
using DiskArc;
using DiskArc.Arc;
using DiskArc.Disk;
using DiskArc.FS;
using DiskArc.Multi;
using static DiskArc.Defs;

//
// The user can select a new directory in the tree by clicking on an entry.  If they double-click
// on a directory in the file list, we want to change the directory tree selection from code.
// Both of these actions cause a SelectedItemChanged event to fire, which is where we perform
// various actions.  While these actions should result in the same updates to the data structures,
// they have different behavior when it comes to which control ends up with the focus after the
// changes have been made.  If the user clicked in the directory list we'd like the focus to stay
// there, but in other circumstances we might want the focus to be on the file list.  A fundamental
// requirement for managing focus is knowing whether a SelectedItemChanged event is firing as a
// result of user activity or a programmatic change, but WPF provides no way for us to tell the
// difference.
//
// We can wrap accesses to IsSelected and Clear() with a boolean flag that suppresses the usual
// SelectedItemChange behavior for programmatic changes, but unfortunately that doesn't always
// work.  For example, if the user clicks on a new entry in the archive tree, the change event
// for that tree will repopulate the directory tree and select the first entry.  The directory
// tree's SelectedItemChanged event doesn't fire until later (possibly delayed by virtualization?).
//
// One possible approach is to try to track which thing happened first, i.e. set an enum that
// doesn't get overwritten by subsequent event handlers.  On exit, if the event handler sees a
// matching enum value, it can do the appropriate focusing and then clear the enum.  This
// requires that things happen in a specific order, however, which may not be the case because
// sometimes WPF events are added to a queue instead of firing immediately.
//
// Detecting mouse and keyboard activity might work if they can be tied to a subsequent event,
// but that approach seems error-prone.
//

namespace cp2_wpf {
    public partial class MainController {
        private bool mSwitchFocusToFileList = false;

        /// <summary>
        /// Currently-selected DiskArc library object in the archive tree (i.e.
        /// WorkTreeNode.DAObject).  May be IDiskImage, IArchive, IMultiPart, IFileSystem, or
        /// Partition.
        /// </summary>
        private object? CurrentWorkObject { get; set; }

        // True if blocks/sectors are readable.  If we support custom sector codecs we will want
        // to relax this restriction for floppy disk images.
        public bool CanEditBlocks { get { return CanAccessChunk(false); } }
        public bool CanEditSectors { get { return CanAccessChunk(true); } }

        public bool HasChunks { get { return GetCurrentWorkChunks() != null; } }

        /// <summary>
        /// Determines whether the current work object can be sector-edited as blocks or sectors.
        /// This only works for IDiskImage and Partition.
        /// </summary>
        /// <remarks>
        /// <para>IFileSystem and IMultiPart also have chunk access objects, but filesystems have
        /// to be closed before they can be edited, and the multi-part chunks are really just
        /// meant to be carved up into smaller pieces.</para>
        /// <para>A "True" result does not indicate that the storage is writable.</para>
        /// </remarks>
        private bool CanAccessChunk(bool asSectors) {
            IChunkAccess? chunks = GetCurrentWorkChunks();
            if (chunks != null) {
                if (asSectors) {
                    return chunks.HasSectors;
                } else {
                    return chunks.HasBlocks;
                }
            }
            return false;
        }

        /// <summary>
        /// Obtains the IChunkAccess object from CurrentWorkObject.  Returns null if the
        /// current work object is not IDiskImage/Partition, or is a disk image with an
        /// unrecognized sector format.
        /// </summary>
        private IChunkAccess? GetCurrentWorkChunks() {
            if (CurrentWorkObject is IDiskImage) {
                return ((IDiskImage)CurrentWorkObject).ChunkAccess;
            } else if (CurrentWorkObject is Partition) {
                return ((Partition)CurrentWorkObject).ChunkAccess;
            } else {
                return null;
            }
        }

        /// <summary>
        /// True if the currently selected archive tree item is a writable node.
        /// </summary>
        public bool CanWrite {
            get {
                ArchiveTreeItem? arcTreeSel = mMainWin.SelectedArchiveTreeItem;
                if (arcTreeSel != null) {
                    return !arcTreeSel.WorkTreeNode.IsReadOnly;
                }
                return false;
            }
        }

        /// <summary>
        /// True if one or more entries are selected.
        /// </summary>
        public bool AreFileEntriesSelected {
            get { return mMainWin.fileListDataGrid.SelectedIndex >= 0; }
        }

        /// <summary>
        /// True if exactly one entry is selected in the file list.
        /// </summary>
        public bool IsSingleEntrySelected {
            get { return mMainWin.fileListDataGrid.SelectedItems.Count == 1; }
        }

        /// <summary>
        /// True if the item selected in the archive tree is a disk image.
        /// </summary>
        public bool IsDiskImageSelected { get { return CurrentWorkObject is IDiskImage; } }

        /// <summary>
        /// True if the item selected in the archive tree is a partition.
        /// </summary>
        public bool IsPartitionSelected { get { return CurrentWorkObject is Partition; } }

        /// <summary>
        /// True if the item selected in the archive tree is a disk image or a partition.
        /// </summary>
        public bool IsDiskOrPartitionSelected {
            get { return CurrentWorkObject is IDiskImage || CurrentWorkObject is Partition; }
        }

        /// <summary>
        /// True if the item selected in the archive tree is a nibble disk image.
        /// </summary>
        public bool IsNibbleImageSelected { get { return CurrentWorkObject is INibbleDataAccess; } }

        /// <summary>
        /// True if the item selected in the archive tree is a filesystem.
        /// </summary>
        public bool IsFileSystemSelected { get { return CurrentWorkObject is IFileSystem; } }

        /// <summary>
        /// True if the selected item in the archive tree is a hierarchical filesystem
        /// (ProDOS or HFS).
        /// </summary>
        public bool IsHierarchicalFileSystemSelected {
            get {
                IFileSystem? fs = CurrentWorkObject as IFileSystem;
                if (fs != null) {
                    return fs.Characteristics.IsHierarchical;
                }
                return false;
            }
        }

        /// <summary>
        /// True if the entry selected in the directory tree is at the root (has no parent).
        /// </summary>
        public bool IsSelectedDirRoot {
            get {
                DirectoryTreeItem? dirSel = mMainWin.SelectedDirectoryTreeItem;
                return (dirSel != null && dirSel.Parent == null);
            }
        }

        /// <summary>
        /// True if the item selected in the archive tree view is a closable sub-tree.
        /// </summary>
        public bool IsClosableTreeSelected {
            get {
                ArchiveTreeItem? arcSel = mMainWin.SelectedArchiveTreeItem;
                return (arcSel != null && arcSel.CanClose);
            }
        }

        /// <summary>
        /// Clears the contents of the archive tree.
        /// </summary>
        private void ClearArchiveTree() {
            mMainWin.ArchiveTreeRoot.Clear();
        }

        /// <summary>
        /// Prepares a work file for use.
        /// </summary>
        /// <param name="pathName">File pathname.</param>
        /// <param name="stream">Open file stream.</param>
        /// <returns>True on success.</returns>
        private bool PopulateArchiveTree() {
            Debug.Assert(mWorkTree != null);

            // Tree should be empty.
            ObservableCollection<ArchiveTreeItem> tvRoot = mMainWin.ArchiveTreeRoot;
            Debug.Assert(tvRoot.Count == 0);

            AppHook.LogI("Constructing archive trees...");
            DateTime startWhen = DateTime.Now;

            ArchiveTreeItem.ConstructTree(tvRoot, mWorkTree.RootNode);

            AppHook.LogI("Finished archive tree construction in " +
                (DateTime.Now - startWhen).TotalMilliseconds + " ms");

            // Select the first filesystem or archive we encounter while chasing the first child.
            ArchiveTreeItem.SelectBestFrom(mMainWin.archiveTree, tvRoot[0]);

            return true;
        }

        /// <summary>
        /// Handles selection change in archive tree view.
        /// </summary>
        /// <param name="newSel">Newly-selected item.  May be null, notably when the work file
        ///   has been closed.</param>
        internal void ArchiveTree_SelectionChanged(ArchiveTreeItem? newSel) {
            Debug.WriteLine("Archive tree selection now: " + (newSel == null ? "none" : newSel));
            if (newSel == null) {
                mMainWin.DirectoryTreeRoot.Clear();
                CurrentWorkObject = null;
                return;
            }

            mMainWin.DirectoryTreeRoot.Clear();
            mMainWin.ClearCenterInfo();

            CurrentWorkObject = newSel.WorkTreeNode.DAObject;
            mMainWin.CenterInfoText1 = "Entry: " + newSel.Name;
            mMainWin.CenterInfoText2 = GetInfoString(CurrentWorkObject);
            if (CurrentWorkObject is IFileSystem) {
                ObservableCollection<DirectoryTreeItem> tvRoot = mMainWin.DirectoryTreeRoot;
                IFileSystem fs = (IFileSystem)CurrentWorkObject;
                PopulateDirectoryTree(null, tvRoot, fs.GetVolDirEntry());
                tvRoot[0].IsSelected = true;
                mMainWin.SetNotesList(fs.Notes);
            } else {
                string title = "Information";
                if (CurrentWorkObject is IArchive) {
                    IArchive arc = (IArchive)CurrentWorkObject;
                    title = "File Archive Entry List";
                    mMainWin.SetNotesList(arc.Notes);
                } else if (CurrentWorkObject is IDiskImage) {
                    IDiskImage disk = (IDiskImage)CurrentWorkObject;
                    title = "Disk Image Information";
                    mMainWin.SetNotesList(disk.Notes);

                    mMainWin.ShowSectorEditButtons = true;

                    if (CurrentWorkObject is IMetadata) {
                        mMainWin.SetMetadataList((IMetadata)CurrentWorkObject);
                    }
                } else if (CurrentWorkObject is IMultiPart) {
                    IMultiPart parts = (IMultiPart)CurrentWorkObject;
                    title = "Multi-Partition Information";
                    mMainWin.SetNotesList(parts.Notes);
                    mMainWin.SetPartitionList(parts);
                } else if (CurrentWorkObject is Partition) {
                    Partition part = (Partition)CurrentWorkObject;
                    title = "Partition Information";
                    // no notes
                    mMainWin.ShowSectorEditButtons = true;
                }
                DirectoryTreeItem newItem = new DirectoryTreeItem(null, title, IFileEntry.NO_ENTRY);
                mMainWin.DirectoryTreeRoot.Add(newItem);
                newItem.IsSelected = true;
            }
        }

        /// <summary>
        /// Scans for filesystem sub-volumes.
        /// </summary>
        public void ScanForSubVol() {
            IFileSystem? fs = CurrentWorkObject as IFileSystem;
            if (fs == null) {
                Debug.Assert(false);        // shouldn't be able to get here
                return;
            }
            ArchiveTreeItem? arcTreeSel = mMainWin.SelectedArchiveTreeItem;
            if (arcTreeSel == null) {
                Debug.Assert(false);
                return;
            }
            Debug.Assert(arcTreeSel.WorkTreeNode.DAObject == fs);

            IMultiPart? embeds = fs.FindEmbeddedVolumes();
            if (embeds == null) {
                Debug.WriteLine("No sub-volumes found");
                return;
            }

            // Is it already open?
            ArchiveTreeItem? ati =
                ArchiveTreeItem.FindItemByDAObject(mMainWin.ArchiveTreeRoot, embeds);
            if (ati != null) {
                ati.IsSelected = true;
                return;
            }


            // Add it to the tree.
            WorkTree.Node? newNode = mWorkTree!.TryCreateMultiPart(arcTreeSel.WorkTreeNode, embeds);
            if (newNode != null) {
                // Successfully opened.  Update the TreeView and select the multipart.
                ArchiveTreeItem newItem =
                    ArchiveTreeItem.ConstructTree(arcTreeSel, newNode);
                newItem.IsSelected = true;
            }
        }

        /// <summary>
        /// Generates a one-line blurb about the specified object, which may be any type that
        /// can be found in the archive tree.
        /// </summary>
        private string GetInfoString(object workObj) {
            if (workObj is IArchive) {
                return ThingString.IArchive((IArchive)workObj) + " file archive";
            } else if (workObj is IDiskImage) {
                IDiskImage disk = (IDiskImage)workObj;
                IChunkAccess? chunks = disk.ChunkAccess;
                StringBuilder sb = new StringBuilder();
                sb.Append(ThingString.IDiskImage(disk));
                sb.Append(" disk image");
                if (chunks != null && chunks.NibbleCodec != null) {
                    sb.Append(", nibble codec=" + chunks.NibbleCodec.Name);
                } else if (disk is INibbleDataAccess) {
                    sb.Append(" (nibble)");
                }
                if (chunks != null) {
                    if (chunks.HasSectors && chunks.NumSectorsPerTrack == 16) {
                        sb.Append(", order=" + ThingString.SectorOrder(chunks.FileOrder));
                    }
                    sb.Append(", ");
                    sb.Append(mFormatter.FormatSizeOnDisk(chunks.FormattedLength, KBLOCK_SIZE));
                }
                return sb.ToString();
            } else if (workObj is IFileSystem) {
                IFileSystem fs = (IFileSystem)workObj;
                StringBuilder sb = new StringBuilder();
                sb.Append(ThingString.IFileSystem(fs));
                sb.Append(" filesystem");
                IChunkAccess chunks = fs.RawAccess;
                sb.Append(", size=");
                if (fs is DOS) {
                    sb.Append(mFormatter.FormatSizeOnDisk(chunks.FormattedLength, SECTOR_SIZE));
                } else {
                    sb.Append(mFormatter.FormatSizeOnDisk(chunks.FormattedLength, BLOCK_SIZE));
                }
                return sb.ToString();
            } else if (workObj is IMultiPart) {
                IMultiPart partitions = (IMultiPart)workObj;
                StringBuilder sb = new StringBuilder();
                sb.Append(ThingString.IMultiPart((IMultiPart)workObj));
                sb.Append(" multi-part image, with ");
                sb.Append(partitions.Count);
                sb.Append(" partitions");
                return sb.ToString();
            } else if (workObj is Partition) {
                Partition part = (Partition)workObj;
                StringBuilder sb = new StringBuilder();
                sb.Append(ThingString.Partition(part));
                sb.Append(", start block=");
                sb.Append(part.StartOffset / Defs.BLOCK_SIZE);
                sb.Append(", block count=");
                sb.Append(part.Length / Defs.BLOCK_SIZE);
                return sb.ToString();
            } else {
                return string.Empty;
            }
        }

        /// <summary>
        /// Handles selection change in directory tree view.
        /// </summary>
        /// <param name="newSel">Newly-selected item.  May be null.</param>
        internal void DirectoryTree_SelectionChanged(DirectoryTreeItem? newSel) {
            Debug.WriteLine("Directory tree selection now: " + (newSel == null ? "none" : newSel));
            if (newSel == null) {
                mMainWin.FileList.Clear();
                return;
            }

            if (CurrentWorkObject is IArchive) {
                // There isn't really any content in the directory tree, but we'll get this event
                // when the entry representing the entire archive is selected.
                bool hasRsrc = ((IArchive)CurrentWorkObject).Characteristics.HasResourceForks;
                if (CurrentWorkObject is Zip) {
                    // TODO: should reconfigure columns when settings are applied.
                    hasRsrc = AppSettings.Global.GetBool(AppSettings.MAC_ZIP_ENABLED, true);
                }
                mMainWin.ConfigureCenterPanel(isInfoOnly:false, isArchive:true,
                    isHierarchic:true, hasRsrc:hasRsrc, hasRaw:false);
                RefreshDirAndFileList(false);
            } else if (CurrentWorkObject is IFileSystem) {
                bool hasRsrc = ((IFileSystem)CurrentWorkObject).Characteristics.HasResourceForks;
                bool isHier = ((IFileSystem)CurrentWorkObject).Characteristics.IsHierarchical;
                mMainWin.ConfigureCenterPanel(isInfoOnly:false, isArchive:false,
                    isHierarchic:isHier, hasRsrc:hasRsrc, hasRaw:CurrentWorkObject is DOS);
                if (mMainWin.ShowSingleDirFileList) {
                    RefreshDirAndFileList(false);
                } else {
                    RefreshDirAndFileList(false);

                    // Find the directory entry in the full file list.  This will fail for the
                    // volume dir.  We need to select by item, not index, because the file list
                    // uses the DataGrid header sorting.
                    FileListItem? dirItem =
                        FileListItem.FindItemByEntry(mMainWin.FileList, newSel.FileEntry);
                    if (dirItem != null) {
                        mMainWin.SelectedFileListItem = dirItem;
                        mMainWin.fileListDataGrid.ScrollIntoView(dirItem);
                    }
                }
            } else {
                mMainWin.ConfigureCenterPanel(isInfoOnly:true, isArchive:false,
                    isHierarchic:false, hasRsrc:false, hasRaw:false);
                ClearEntryCounts();
            }
        }

        /// <summary>
        /// Refreshes the contents of the directory tree and file list.  Call this after a
        /// change has been made, such as adding or deleting files, or when the file list mode
        /// has been toggled from single-directory to full list.
        /// </summary>
        /// <remarks>
        /// <para>All of the file information from filesystems and archives is cached in
        /// memory, so re-scanning the existing contents is very fast, especially when compared
        /// to re-rendering the strings and having WPF repopulate the control.  HFS and ProDOS
        /// are effectively limited to 64K files per volume, though file archives can be much
        /// larger (but rarely are).  The small size and fast access means we can do a quick
        /// "should we re-render" check here instead of trying to keep track of whether the
        /// contents are dirty.  (It also correctly identifies the list as dirty when a file
        /// is renamed on a sorted filesystem like HFS.)</para>
        /// <para>The test for differences is made by comparing lists of IFileEntry, so this
        /// will not automatically detect attribute changes.  Those are handled by replacing
        /// the dir/file tree item object, triggering the ObservableCollection event.  If the
        /// attribute change didn't rearrange the list (which can happen on an HFS rename),
        /// there's no need to reload the lists.</para>
        /// </remarks>
        /// <param name="focusOnFileList">If true, put the focus on the file list when done.  This
        ///   should be set for all file operations, but not during archive or directory
        ///   tree traversal.</param>
        internal void RefreshDirAndFileList(bool focusOnFileList = true) {
            mSwitchFocusToFileList |= focusOnFileList;
            if (CurrentWorkObject == null) {
                return;
            }

            // Get item currently selected in file list, if any.
            FileListItem? selectedItem = mMainWin.SelectedFileListItem;
            IFileEntry selFileEntry = IFileEntry.NO_ENTRY;
            if (selectedItem != null) {
                if (selectedItem.FileEntry.IsValid) {
                    selFileEntry = selectedItem.FileEntry;
                } else {
                    Debug.WriteLine("Refresh: selected file entry is no longer valid");
                }
            } else {
                Debug.WriteLine("Refresh: no file list sel");
            }

            if (CurrentWorkObject is IFileSystem) {
                IFileSystem fs = (IFileSystem)CurrentWorkObject;

                // Get item currently selected in directory tree, if any.
                IFileEntry curSel = IFileEntry.NO_ENTRY;
                DirectoryTreeItem? dirTreeSel = mMainWin.SelectedDirectoryTreeItem;
                if (dirTreeSel != null) {
                    curSel = dirTreeSel.FileEntry;
                } else {
                    Debug.WriteLine("Refresh: no dir tree sel");
                }

                // Walk through population of the directory tree to see if it matches what
                // we currently have.
                ObservableCollection<DirectoryTreeItem> rootList = mMainWin.DirectoryTreeRoot;
                IFileEntry volDir = fs.GetVolDirEntry();
                if (!VerifyDirectoryTree(rootList, volDir, 0)) {
                    Debug.WriteLine("Re-populate directory tree");
                    rootList.Clear();
                    PopulateDirectoryTree(null, rootList, volDir);

                    // Try to restore the previous selection.  If there wasn't a selection, or
                    // the previous selection no longer exists, select the root node.
                    if (curSel == IFileEntry.NO_ENTRY ||
                            !DirectoryTreeItem.SelectItemByEntry(mMainWin, curSel)) {
                        rootList[0].IsSelected = true;
                    }
                } else {
                    Debug.WriteLine("Not repopulating directory tree");
                }
            }

            if (!VerifyFileList()) {
                PopulateFileList(selFileEntry, focusOnFileList);
            } else {
                // Set the selection.  The list didn't change, but the directory tree selection
                // change might have cleared the selection.  We can't just set it to the
                // previously-selected item because we might have recreated the item.
                FileListItem? item = FileListItem.FindItemByEntry(mMainWin.FileList, selFileEntry);
                if (item != null) {
                    mMainWin.SelectedFileListItem = item;
                }
                // Don't set focus, or dir tree navigation in full-list mode will break.
                //FileListItem.SetSelectionFocusByEntry(mMainWin.FileList, mMainWin.fileListDataGrid,
                //    selFileEntry);
                Debug.WriteLine("Not repopulating file list");
            }
            if (mSwitchFocusToFileList) {
                Debug.WriteLine("++ focus to file list requested");
                mMainWin.FileList_SetSelectionFocus();
                mSwitchFocusToFileList = false;
            }
        }

        /// <summary>
        /// Recursively populates the directory tree view from an IFileSystem object.
        /// </summary>
        /// <param name="tvRoot">Level where the directory should be added.</param>
        /// <param name="dirEntry">Directory entry object to add.</param>
        private static void PopulateDirectoryTree(DirectoryTreeItem? parent,
                ObservableCollection<DirectoryTreeItem> tvRoot, IFileEntry dirEntry) {
            DirectoryTreeItem newItem = new DirectoryTreeItem(parent, dirEntry.FileName, dirEntry);
            tvRoot.Add(newItem);
            foreach (IFileEntry entry in dirEntry) {
                if (entry.IsDirectory) {
                    PopulateDirectoryTree(newItem, newItem.Items, entry);
                }
            }
        }

        /// <summary>
        /// Recursively verifies that the directory tree matches the current filesystem layout.
        /// </summary>
        /// <param name="tvRoot">Level to check.</param>
        /// <param name="dirEntry">Directory entry we expect to find here.</param>
        /// <param name="index">Index of entry in tvRoot.</param>
        /// <returns>True if all is well, false if a mismatch was found.</returns>
        private static bool VerifyDirectoryTree(ObservableCollection<DirectoryTreeItem> tvRoot,
                IFileEntry dirEntry, int index) {
            if (index >= tvRoot.Count) {
                // Filesystem has more directories; we added one, or the list got cleared.
                return false;
            }
            DirectoryTreeItem item = tvRoot[index];
            if (item.FileEntry != dirEntry) {
                return false;
            }
            int childIndex = 0;
            foreach (IFileEntry entry in dirEntry) {
                if (entry.IsDirectory) {
                    if (!VerifyDirectoryTree(item.Items, entry, childIndex++)) {
                        return false;
                    }
                }
            }
            if (childIndex != item.Items.Count) {
                // Filesystem has fewer directories.
                return false;
            }
            return true;
        }

        /// <summary>
        /// Verifies that the file list matches the current configuration.
        /// </summary>
        /// <returns>True if all is well, false if a mismatch was found.</returns>
        private bool VerifyFileList() {
            if (CurrentWorkObject is IArchive) {
                return VerifyFileList(mMainWin.FileList, (IArchive)CurrentWorkObject);
            } else if (CurrentWorkObject is IFileSystem) {
                if (mMainWin.ShowSingleDirFileList) {
                    DirectoryTreeItem? dirTreeSel = mMainWin.SelectedDirectoryTreeItem;
                    if (dirTreeSel == null) {
                        Debug.WriteLine("Can't verify file list, no dir tree sel");
                        return false;
                    }
                    return VerifyFileList(mMainWin.FileList, dirTreeSel.FileEntry);
                } else {
                    return VerifyFileList(mMainWin.FileList, (IFileSystem)CurrentWorkObject);
                }
            } else {
                Debug.Assert(false, "can't verify " + CurrentWorkObject);
                return false;
            }
        }

        internal void PopulateFileList(IFileEntry selEntry, bool focusOnFileList) {
            if (selEntry != IFileEntry.NO_ENTRY) {
                Debug.WriteLine("Populate: current item is " + selEntry.FileName);
            } else {
                Debug.WriteLine("Populate: no selected item in file list");
            }
            ObservableCollection<FileListItem> fileList = mMainWin.FileList;

            DateTime clearWhen = DateTime.Now;
            fileList.Clear();
            DateTime startWhen = DateTime.Now;

            int dirCount = 0;
            int fileCount = 0;
            if (CurrentWorkObject is IArchive) {
                PopulateEntriesFromArchive((IArchive)CurrentWorkObject,
                    ref dirCount, ref fileCount, fileList);
            } else if (CurrentWorkObject is IFileSystem) {
                if (mMainWin.ShowSingleDirFileList) {
                    DirectoryTreeItem? dirTreeSel = mMainWin.SelectedDirectoryTreeItem;
                    if (dirTreeSel != null) {
                        PopulateEntriesFromSingleDir(dirTreeSel.FileEntry,
                            ref dirCount, ref fileCount, fileList);
                    } else {
                        Debug.WriteLine("Can't repopulate file list, no dir tree sel");
                    }
                } else {
                    PopulateEntriesFromFullDisk(((IFileSystem)CurrentWorkObject!).GetVolDirEntry(),
                        ref dirCount, ref fileCount, fileList);
                }
            } else {
                Debug.Assert(false, "work object is " + CurrentWorkObject);
            }

            DateTime endWhen = DateTime.Now;
            AppHook.LogD("File list refresh done in " +
                (endWhen - startWhen).TotalMilliseconds + " ms (clear took " +
                (startWhen - clearWhen).TotalMilliseconds + " ms)");

            // If the list isn't empty, select something, preferrably whatever was selected before.
            if (fileList.Count != 0) {
                // Don't do this unless we're working on the file list.  The file list selection
                // change can cause a problematic refresh.
                if (focusOnFileList || mSwitchFocusToFileList) {
                    FileListItem.SetSelectionFocusByEntry(fileList, mMainWin.fileListDataGrid,
                        selEntry);
                }
            }

            if (mSwitchFocusToFileList) {
                Debug.WriteLine("+ focus to file list requested");
                mMainWin.FileList_SetSelectionFocus();
                mSwitchFocusToFileList = false;
            }

            SetEntryCounts(CurrentWorkObject as IFileSystem, dirCount, fileCount);
        }

        private void PopulateEntriesFromArchive(IArchive arc, ref int dirCount,
                ref int fileCount, ObservableCollection<FileListItem> fileList) {
            bool macZipMode = AppSettings.Global.GetBool(AppSettings.MAC_ZIP_ENABLED, true);
            foreach (IFileEntry entry in arc) {
                IFileEntry adfEntry = IFileEntry.NO_ENTRY;
                FileAttribs? adfAttrs = null;
                if (macZipMode && arc is Zip) {
                    if (entry.IsMacZipHeader()) {
                        // Ignore headers we don't explicitly look for.
                        continue;
                    }
                    // Look for paired entry.
                    if (Zip.HasMacZipHeader(arc, entry, out adfEntry)) {
                        try {
                            // Can't use un-seekable archive stream as archive source.
                            using (Stream adfStream = ArcTemp.ExtractToTemp(arc,
                                    adfEntry, FilePart.DataFork)) {
                                adfAttrs = new FileAttribs(entry);
                                adfAttrs.GetFromAppleSingle(adfStream, AppHook);
                            }
                        } catch (Exception ex) {
                            // Never mind.
                            Debug.WriteLine("Unable to get ADF attrs for '" +
                                entry.FullPathName + "': " + ex.Message);
                            adfAttrs = null;
                        }
                    }
                }
                if (entry.IsDirectory) {
                    dirCount++;
                } else {
                    fileCount++;
                }
                fileList.Add(new FileListItem(entry, adfEntry, adfAttrs, mFormatter));
            }
        }

        private void PopulateEntriesFromSingleDir(IFileEntry dirEntry, ref int dirCount,
                ref int fileCount, ObservableCollection<FileListItem> fileList) {
            foreach (IFileEntry entry in dirEntry) {
                if (entry.IsDirectory) {
                    dirCount++;
                } else {
                    fileCount++;
                }
                fileList.Add(new FileListItem(entry, mFormatter));
            }
        }

        private void PopulateEntriesFromFullDisk(IFileEntry curDirEntry, ref int dirCount,
                ref int fileCount, ObservableCollection<FileListItem> fileList) {
            foreach (IFileEntry entry in curDirEntry) {
                fileList.Add(new FileListItem(entry, mFormatter));
                if (entry.IsDirectory) {
                    dirCount++;
                    PopulateEntriesFromFullDisk(entry, ref dirCount, ref fileCount, fileList);
                } else {
                    fileCount++;
                }
            }
        }

        /// <summary>
        /// Verifies that the file list matches the contents of an archive.
        /// </summary>
        /// <param name="fileList">List of files to compare to.</param>
        /// <param name="arc">Archive reference.</param>
        /// <returns>True if all is well.</returns>
        private static bool VerifyFileList(ObservableCollection<FileListItem> fileList,
                IArchive arc) {
            if (fileList.Count != arc.Count) {
                return false;
            }
            int index = 0;
            foreach (IFileEntry entry in arc) {
                if (fileList[index].FileEntry != entry) {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Verifies that the file list matches the contents of a single directory.
        /// </summary>
        private static bool VerifyFileList(ObservableCollection<FileListItem> fileList,
                IFileEntry dirEntry) {
            int index = 0;
            bool ok = VerifyFileList(fileList, ref index, dirEntry, false);
            return (ok && index == fileList.Count);
        }

        /// <summary>
        /// Verifies that the file list matches the contents of a full filesystem.
        /// </summary>
        private static bool VerifyFileList(ObservableCollection<FileListItem> fileList,
                IFileSystem fs) {
            int index = 0;
            bool ok = VerifyFileList(fileList, ref index, fs.GetVolDirEntry(), true);
            return (ok && index == fileList.Count);
        }

        /// <summary>
        /// Recursively verifies that disk contents match.
        /// </summary>
        private static bool VerifyFileList(ObservableCollection<FileListItem> fileList,
                ref int index, IFileEntry dirEntry, bool doRecurse) {
            if (index >= fileList.Count) {
                return false;
            }
            foreach (IFileEntry entry in dirEntry) {
                if (index >= fileList.Count || fileList[index].FileEntry != entry) {
                    return false;
                }
                index++;
                if (doRecurse && entry.IsDirectory) {
                    if (!VerifyFileList(fileList, ref index, entry, doRecurse)) {
                        return false;
                    }
                }
            }
            return true;
        }

        /// <summary>
        /// Sets the file/directory counts for the status bar.
        /// </summary>
        /// <param name="fs">Filesystem reference, or null if this is an IArchive.</param>
        /// <param name="dirCount">Number of directories.</param>
        /// <param name="fileCount">Number of files.</param>
        private void SetEntryCounts(IFileSystem? fs, int dirCount, int fileCount) {
            StringBuilder sb = new StringBuilder();
            sb.Append(fileCount);
            if (fileCount == 1) {
                sb.Append(" file, ");
            } else {
                sb.Append(" files, ");
            }
            sb.Append(dirCount);
            if (dirCount == 1) {
                sb.Append(" directory");
            } else {
                sb.Append(" directories");
            }
            if (fs != null) {
                int baseUnit;
                if (fs is DOS) {
                    baseUnit = SECTOR_SIZE;
                } else if (fs is ProDOS) {
                    baseUnit = BLOCK_SIZE;
                } else {
                    baseUnit = KBLOCK_SIZE;
                }
                sb.Append(", ");
                sb.Append(mFormatter.FormatSizeOnDisk(fs.FreeSpace, baseUnit));
                sb.Append(" free");
            }

            mMainWin.CenterStatusText = sb.ToString();
        }

        /// <summary>
        /// Clears the file/directory count text.
        /// </summary>
        private void ClearEntryCounts() {
            mMainWin.CenterStatusText = string.Empty;
        }

        /// <summary>
        /// Handles a double-click on an item in the file list grid.
        /// </summary>
        public void HandleFileListDoubleClick(FileListItem item, int row, int col,
                ArchiveTreeItem arcTreeSel, DirectoryTreeItem dirTreeSel) {
            //Debug.WriteLine("DCLICK: r=" + row + " c=" + col + " item=" + item);

            //
            // Something has been double-clicked.  If it's a single entry:
            // (1) If it's a directory, select the appropriate entry in the directory tree.
            // (2) See if it exists in the archive tree, i.e. it's an archive or disk image
            //   that has already been identified and opened.
            // (3) Test to see if it looks like an archive or disk image.  If so, add it
            //   to the archive tree and select it.
            // (4) Open the file viewer with archive+entry or image+entry.
            //
            // If multiple items are selected, go directly to #4.  We could probably do a
            // multi-open on sub-files, but that seems peculiar.
            //

            // Get the currently-selected file archive or filesystem.  If something without files
            // is selected (disk image file, partition map), we shouldn't be here.
            IArchive? arc = arcTreeSel.WorkTreeNode.DAObject as IArchive;
            IFileSystem? fs = arcTreeSel.WorkTreeNode.DAObject as IFileSystem;
            if (!(arc == null ^ fs == null)) {
                Debug.Assert(false, "Unexpected: arc=" + arc + " fs=" + fs);
                return;
            }

            // Get selection.
            DataGrid dg = mMainWin.fileListDataGrid;
            int treeSelIndex = dg.SelectedIndex;    // index of first item in selection
            if (treeSelIndex < 0) {
                // Double-click was on something other than list item, with nothing selected.
                Debug.WriteLine("Double-click but no selection");
                return;
            }

            // If only one item is selected, we need to check if it's a directory, file archive
            // or disk image and requires special handling.  Otherwise we'll just hand everything
            // over to the file viewer.
            if (dg.SelectedItems.Count == 1) {
                IFileEntry entry = item.FileEntry;
                if (entry.IsDirectory) {
                    if (fs != null) {
                        // Select the entry in the dir tree.  This may rewrite the file list.
                        if (!DirectoryTreeItem.SelectItemByEntry(mMainWin, entry)) {
                            Debug.WriteLine("Unable to find dir tree entry for " + entry);
                        } else {
                            // TODO: the focus will move to the directory tree.  We may want
                            // to put it on the first item in the file list instead.
                        }
                    } else {
                        // Directory file in a file archive.  Nothing for us to do.
                    }
                    return;
                }

                // Is it a disk image or file archive that's already open in the tree?
                ArchiveTreeItem? ati =
                    ArchiveTreeItem.FindItemByEntry(mMainWin.ArchiveTreeRoot, entry);
                if (ati != null) {
                    ArchiveTreeItem.SelectBestFrom(mMainWin.archiveTree, ati);
                    mSwitchFocusToFileList = true;
                    return;
                }

                try {
                    Mouse.OverrideCursor = Cursors.Wait;

                    // Evalute this file to see if it could be a disk image or file archive.  If it
                    // is, add it to the work tree, and select it.
                    WorkTree.Node? newNode = mWorkTree!.TryCreateSub(arcTreeSel.WorkTreeNode, entry);
                    if (newNode != null) {
                        // Successfully opened.  Update the TreeView.
                        ArchiveTreeItem newItem =
                            ArchiveTreeItem.ConstructTree(arcTreeSel, newNode);
                        // Select something in what we just added.  If it was a disk image, we want
                        // to select the first filesystem, not the disk image itself.
                        ArchiveTreeItem.SelectBestFrom(mMainWin.archiveTree, newItem);
                        mSwitchFocusToFileList = true;
                        return;
                    }
                } finally {
                    Mouse.OverrideCursor = null;
                }
            }

            // View the selection.
            ViewFiles();
        }

        /// <summary>
        /// Handles a double-click on an item in the partition layout grid shown for
        /// IMultiPart entries.
        /// </summary>
        public void HandlePartitionLayoutDoubleClick(MainWindow.PartitionListItem item,
                int row, int col, ArchiveTreeItem arcTreeSel) {
            ArchiveTreeItem? ati = ArchiveTreeItem.FindItemByDAObject(mMainWin.ArchiveTreeRoot,
                item.PartRef);
            if (ati != null) {
                ArchiveTreeItem.SelectBestFrom(mMainWin.archiveTree, ati);
                return;
            }

            try {
                Mouse.OverrideCursor = Cursors.Wait;

                WorkTree.Node? newNode =
                    mWorkTree!.TryCreatePartition(arcTreeSel.WorkTreeNode, item.Index);
                if (newNode != null) {
                    // Successfully opened.  Update the TreeView.
                    ArchiveTreeItem newItem =
                        ArchiveTreeItem.ConstructTree(arcTreeSel, newNode);
                    // Select something in what we just added.  If it was a disk image, we want
                    // to select the first filesystem, not the disk image itself.
                    ArchiveTreeItem.SelectBestFrom(mMainWin.archiveTree, newItem);
                }
            } finally {
                Mouse.OverrideCursor = null;
            }
        }
    }
}
