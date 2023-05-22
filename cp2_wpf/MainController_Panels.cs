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
            ArchiveTreeItem item = tvRoot[0];
            while (item.Items.Count > 0) {
                // More items farther down.  Do we need to go deeper?
                if (item.WorkTreeNode.DAObject is GZip) {
                    // we need to go deeper
                } else if (item.WorkTreeNode.DAObject is NuFX) {
                    IFileEntry firstEntry = ((NuFX)item.WorkTreeNode.DAObject).GetFirstEntry();
                    if (!firstEntry.IsDiskImage) {
                        break;
                    }
                    // first entry is disk image, go deeper
                } else if (item.WorkTreeNode.DAObject is IFileSystem ||
                           item.WorkTreeNode.DAObject is IArchive) {
                    break;      // no, stop here
                }
                item = item.Items[0];
            }
            item.IsSelected = true;

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
        /// Generates a one-line blurb about the specified object, which may be any type that
        /// can be found in the archive tree.
        /// </summary>
        private static string GetInfoString(object workObj) {
            if (workObj is IArchive) {
                return ThingString.IArchive((IArchive)workObj) + " file archive";
            } else if (workObj is IDiskImage) {
                IDiskImage disk = (IDiskImage)workObj;
                // ChunkAccess will be unavailable
                StringBuilder sb = new StringBuilder();
                sb.Append(ThingString.IDiskImage(disk));
                sb.Append(" disk image");
                if (disk is INibbleDataAccess) {
                    sb.Append(" (nibble)");
                }
                return sb.ToString();
            } else if (workObj is IFileSystem) {
                IFileSystem fs = (IFileSystem)workObj;
                StringBuilder sb = new StringBuilder();
                sb.Append(ThingString.IFileSystem(fs));
                sb.Append(" filesystem");
                IChunkAccess chunks = fs.RawAccess;
                if (chunks.NibbleCodec != null) {
                    sb.Append(", nibble codec=" + chunks.NibbleCodec.Name);
                } else if (chunks.HasSectors && chunks.NumSectorsPerTrack == 16) {
                    sb.Append(", order=" + ThingString.SectorOrder(chunks.FileOrder));
                }
                return sb.ToString();
            } else if (workObj is IMultiPart) {
                return ThingString.IMultiPart((IMultiPart)workObj) + " multi-part image";
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
        /// Formats a value that represents the size of something stored on a disk, such as a
        /// file or a partition.
        /// </summary>
        /// <param name="length">Value to format.</param>
        /// <param name="baseUnit">Base units: sectors, blocks, or KiB.</param>
        /// <returns>Formatted string.</returns>
        internal static string FormatSizeOnDisk(long length, int baseUnit) {
            if (baseUnit == KBLOCK_SIZE || length >= 10 * 1024 * 1024) {
                if (length >= 1024 * 1024 * 1024) {
                    return string.Format("{0:F1}GB", length / (1024.0 * 1024.0 * 1024.0));
                } else if (length >= 10 * 1024 * 1024) {
                    return string.Format("{0:F1}MB", length / (1024.0 * 1024.0));
                } else {
                    return string.Format("{0:F0}KB", length / 1024.0);
                }
            } else {
                long num = length / baseUnit;
                string unitStr;
                switch (baseUnit) {
                    case SECTOR_SIZE:
                        unitStr = "sector";
                        break;
                    case BLOCK_SIZE:
                        unitStr = "block";
                        break;
                    default:
                        unitStr = "unit";
                        break;
                }
                return string.Format("{0:D0} {1}{2}", num, unitStr, (num == 1) ? "" : "s");
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
                sb.Append(FormatSizeOnDisk(fs.FreeSpace, baseUnit));
                sb.Append(" free");
            }

            mMainWin.CenterStatusText = sb.ToString();
        }

        private void ClearEntryCounts() {
            mMainWin.CenterStatusText = string.Empty;
        }

        /// <summary>
        /// Handles a double-click on a line in the file list grid.
        /// </summary>
        /// <param name="item">Item that was double-clicked.</param>
        public void HandleFileListDoubleClick(FileListItem item, int row, int col,
                ArchiveTreeItem arcTreeSel, DirectoryTreeItem dirTreeSel) {
            Debug.WriteLine("DCLICK: r=" + row + " c=" + col + " item=" + item);

            //
            // Something has been double-clicked.  Process:
            // (1) If it's a directory, select the appropriate entry in the directory tree.
            // (2) See if it exists in the archive tree, i.e. it's an archive or disk image
            //   that has already been identified and opened.
            // (3) Test to see if it looks like an archive or disk image.  If so, add it
            //   to the archive tree and select it.
            // (4) Open the file viewer with archive+entry or image+entry.
            //

            // Get the currently-selected file archive or filesystem.  If something else is
            // selected (disk image file, partition map), we shouldn't be here.
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
                // Double-click was on something other than list item before anything was selected.
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
                        if (!DirectoryTreeItem.SelectItemByEntry(mMainWin.DirectoryTreeRoot,
                                entry)) {
                            Debug.WriteLine("Unable to find dir tree entry for " + entry);
                        }
                    } else {
                        // Directory file in a file archive.  Nothing for us to do.
                    }
                    return;
                }

                ArchiveTreeItem? ati =
                    ArchiveTreeItem.FindItemByEntry(mMainWin.ArchiveTreeRoot, entry);
                if (ati != null) {
                    ati.IsSelected = true;
                    return;
                }

                // TODO: if it looks like an archive or disk image, scan it; if it works out,
                //   add it to the tree and open it.  Might want to make that an explicit
                //   action, but there's no reason to open "Archive.SHK" in the file viewer.
                //   (No need to filter things out when multiple items selected.)
            }

            // View the selection.
            ViewFiles();
        }
    }
}
