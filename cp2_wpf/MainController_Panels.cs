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

namespace cp2_wpf {
    public partial class MainController {
        /// <summary>
        /// Currently-selected DiskArc library object in the archive tree (i.e.
        /// WorkTreeNode.DAObject).  May be IDiskImage, IArchive, IMultiPart, IFileSystem, or
        /// Partition.
        /// </summary>
        private object? CurrentWorkObject { get; set; }

        public bool CanEditBlocks { get { return CanEditChunk(false); } }
        public bool CanEditSectors { get { return CanEditChunk(true); } }

        /// <summary>
        /// Determines whether the current work object can be sector-edited by blocks or sectors.
        /// This only works for IDiskImage and Partition.  IFileSystem and IMultiPart also have
        /// chunk access objects, but filesystems have to be closed before they can be edited,
        /// and the multi-part chunks are really just meant to be carved up into smaller pieces.
        /// </summary>
        private bool CanEditChunk(bool asSectors) {
            IChunkAccess? chunks = null;
            if (CurrentWorkObject is IDiskImage) {
                chunks = ((IDiskImage)CurrentWorkObject).ChunkAccess;
            } else if (CurrentWorkObject is Partition) {
                chunks = ((Partition)CurrentWorkObject).ChunkAccess;
            }
            if (chunks != null) {
                if (asSectors) {
                    return chunks.HasSectors;
                } else {
                    return chunks.HasBlocks;
                }
            }
            return false;
        }

        public bool AreFilesSelected {
            get { return mMainWin.fileListDataGrid.SelectedIndex >= 0; }
        }

        /// <summary>
        /// True if the object selected in the archive tree is a disk image.
        /// </summary>
        public bool IsDiskImageSelected { get { return CurrentWorkObject is IDiskImage; } }

        /// <summary>
        /// True if the object selected in the archive tree is a filesystem.
        /// </summary>
        public bool IsFileSystemSelected { get { return CurrentWorkObject is IFileSystem; } }


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

            mAppHook.LogI("Constructing content trees...");
            DateTime startWhen = DateTime.Now;

            ArchiveTreeItem.ConstructTree(mWorkTree.RootNode, tvRoot);

            mAppHook.LogI("Finished tree element construction in " +
                (DateTime.Now - startWhen).TotalMilliseconds + " ms");

            // Select the first filesystem or archive we encounter while chasing the first child.
            ArchiveTreeItem.SelectBestFrom(tvRoot[0]);

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
                DirectoryTreeItem.PopulateDirectoryTree(tvRoot, fs, fs.GetVolDirEntry(), mAppHook);
                tvRoot[0].IsSelected = true;
                mMainWin.SetNotesList(fs.Notes);
            } else {
                string title = "Information";
                if (CurrentWorkObject is IArchive) {
                    IArchive arc = (IArchive)CurrentWorkObject;
                    title = "File List";
                    mMainWin.SetNotesList(arc.Notes);
                } else if (CurrentWorkObject is IDiskImage) {
                    IDiskImage disk = (IDiskImage)CurrentWorkObject;
                    mMainWin.SetNotesList(disk.Notes);

                    if (CurrentWorkObject is IMetadata) {
                        mMainWin.SetMetadataList((IMetadata)CurrentWorkObject);
                    }
                } else if (CurrentWorkObject is IMultiPart) {
                    IMultiPart parts = (IMultiPart)CurrentWorkObject;
                    mMainWin.SetNotesList(parts.Notes);
                    mMainWin.SetPartitionList(parts);
                } else if (CurrentWorkObject is Partition) {
                    Partition part = (Partition)CurrentWorkObject;
                    // no notes
                }
                DirectoryTreeItem newItem = new DirectoryTreeItem(title, IFileEntry.NO_ENTRY);
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
            ArchiveTreeItem? arcTreeSel = mMainWin.archiveTree.SelectedItem as ArchiveTreeItem;
            if (arcTreeSel == null) {
                Debug.Assert(false);
                return;
            }

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
                    ArchiveTreeItem.ConstructTree(newNode, arcTreeSel.Items);
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

            bool usingFileList = true;
            if (CurrentWorkObject is IArchive) {
                AddArchiveItems((IArchive)CurrentWorkObject);
                mMainWin.SetCenterPanelContents(MainWindow.CenterPanelContents.FileArchive);
                mMainWin.SetColumnConfig(MainWindow.ColumnMode.FileArchive);
            } else if (CurrentWorkObject is IFileSystem) {
                AddDiskItems(newSel.FileEntry);
                mMainWin.SetCenterPanelContents(MainWindow.CenterPanelContents.DiskImage);
                mMainWin.SetColumnConfig(CurrentWorkObject is DOS ?
                    MainWindow.ColumnMode.DOSDiskImage : MainWindow.ColumnMode.DiskImage);
            } else {
                mMainWin.SetCenterPanelContents(MainWindow.CenterPanelContents.InfoOnly);
                usingFileList = false;
            }

            SetEntryCounts(usingFileList, CurrentWorkObject as IFileSystem);

            if (usingFileList && mMainWin.FileList.Count > 0) {
                // Set selection to first item, and scroll up if needed.
                mMainWin.fileListDataGrid.SelectedIndex = 0;
                mMainWin.fileListDataGrid.ScrollIntoView(mMainWin.fileListDataGrid.SelectedItem);
            }
        }

        private void AddArchiveItems(IArchive arc) {
            mMainWin.FileList.Clear();
            foreach (IFileEntry entry in arc) {
                mMainWin.FileList.Add(new FileListItem(entry, mFormatter));
            }
        }

        private void AddDiskItems(IFileEntry dirEntry) {
            DateTime startWhen = DateTime.Now;
            mMainWin.FileList.Clear();
            foreach (IFileEntry entry in dirEntry) {
                mMainWin.FileList.Add(new FileListItem(entry, mFormatter));
            }
            Debug.WriteLine("Added disk items for " + dirEntry + " in " +
                (DateTime.Now - startWhen).TotalMilliseconds + " ms");
        }

        /// <summary>
        /// Sets the file/directory counts for the status bar.
        /// </summary>
        private void SetEntryCounts(bool usingFileList, IFileSystem? fs) {
            if (!usingFileList) {
                mMainWin.CenterStatusText = string.Empty;
                return;
            }
            int dirCount = 0;
            int fileCount = 0;
            foreach (FileListItem item in mMainWin.FileList) {
                if (item.FileEntry.IsDirectory) {
                    dirCount++;
                } else {
                    fileCount++;
                }
            }
            StringBuilder sb = new StringBuilder();
            sb.Append(fileCount);
            sb.Append(" files, ");
            sb.Append(dirCount);
            sb.Append(" directories");
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
                        if (!DirectoryTreeItem.SelectItemByEntry(mMainWin.DirectoryTreeRoot,
                                entry)) {
                            Debug.WriteLine("Unable to find dir tree entry for " + entry);
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
                    ArchiveTreeItem.SelectBestFrom(ati);
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
                            ArchiveTreeItem.ConstructTree(newNode, arcTreeSel.Items);
                        // Select something in what we just added.  If it was a disk image, we want
                        // to select the first filesystem, not the disk image itself.
                        ArchiveTreeItem.SelectBestFrom(newItem);
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
                ArchiveTreeItem.SelectBestFrom(ati);
                return;
            }

            try {
                Mouse.OverrideCursor = Cursors.Wait;

                WorkTree.Node? newNode =
                    mWorkTree!.TryCreatePartition(arcTreeSel.WorkTreeNode, item.Index);
                if (newNode != null) {
                    // Successfully opened.  Update the TreeView.
                    ArchiveTreeItem newItem =
                        ArchiveTreeItem.ConstructTree(newNode, arcTreeSel.Items);
                    // Select something in what we just added.  If it was a disk image, we want
                    // to select the first filesystem, not the disk image itself.
                    ArchiveTreeItem.SelectBestFrom(newItem);
                }
            } finally {
                Mouse.OverrideCursor = null;
            }
        }
    }
}
