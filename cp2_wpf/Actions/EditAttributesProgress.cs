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
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;

using AppCommon;
using CommonUtil;
using cp2_wpf.WPFCommon;
using DiskArc;
using DiskArc.Arc;
using static DiskArc.Defs;

namespace cp2_wpf.Actions {
    /// <summary>
    /// Manages file entry attribute update.  This will usually be simple enough that executing
    /// it on a background thread inside a WorkProgress dialog is unnecessary.
    /// </summary>
    internal class EditAttributesProgress {
        private Window mParent;
        private object mArchiveOrFileSystem;
        private DiskArcNode mLeafNode;
        private IFileEntry mFileEntry;
        private IFileEntry mADFEntry;
        private FileAttribs mNewAttribs;
        private AppHook mAppHook;

        public bool DoCompress { get; set; }
        public bool EnableMacOSZip { get; set; }


        public EditAttributesProgress(Window parent, object archiveOrFileSystem,
                DiskArcNode leafNode, IFileEntry fileEntry, IFileEntry adfEntry,
                FileAttribs newAttribs, AppHook appHook) {
            mParent = parent;
            mArchiveOrFileSystem = archiveOrFileSystem;
            mLeafNode = leafNode;
            mFileEntry = fileEntry;
            mADFEntry = adfEntry;
            mNewAttribs = newAttribs;
            mAppHook = appHook;
        }

        /// <summary>
        /// Performs the attribute update.  Errors are reported directly to the user.
        /// </summary>
        /// <param name="updateMacZip">True if we want to update a MacZip header entry.</param>
        /// <returns>True on success.</returns>
        public bool DoUpdate(bool updateMacZip) {
            try {
                Mouse.OverrideCursor = Cursors.Wait;

                if (mArchiveOrFileSystem is IArchive) {
                    IArchive arc = (IArchive)mArchiveOrFileSystem;
                    if (updateMacZip) {
                        try {
                            Debug.Assert(EnableMacOSZip);
                            if (!HandleMacZip(arc, mFileEntry, mADFEntry, mNewAttribs, mAppHook)) {
                                return false;
                            }
                            mLeafNode.SaveUpdates(DoCompress);
                        } finally {
                            arc.CancelTransaction();
                        }
                    } else {
                        try {
                            arc.StartTransaction();
                            mNewAttribs.CopyAttrsTo(mFileEntry, false);
                            mLeafNode.SaveUpdates(DoCompress);
                        } finally {
                            arc.CancelTransaction();    // no effect if transaction isn't open
                        }
                    }
                } else {
                    IFileSystem fs = (IFileSystem)mArchiveOrFileSystem;
                    mNewAttribs.CopyAttrsTo(mFileEntry, false);
                    mFileEntry.SaveChanges();
                    mLeafNode.SaveUpdates(DoCompress);
                }
            } catch (Exception ex) {
                MessageBox.Show(mParent, ex.Message, "Failed", MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return false;
            } finally {
                Mouse.OverrideCursor = null;
            }
            return true;
        }

        /// <summary>
        /// Makes changes to a MacZip header entry.  This opens a transaction in
        /// <paramref name="arc"/>, and leaves it open for the caller to commit.
        /// </summary>
        /// <param name="arc">Zip archive object.</param>
        /// <param name="adfEntry">File entry for the ADF header.</param>
        /// <param name="newAttribs">New attributes to save in the header.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <returns>True on success.</returns>
        private static bool HandleMacZip(IArchive arc, IFileEntry mainEntry, IFileEntry adfEntry,
                FileAttribs newAttribs, AppHook appHook) {
            // Currently assuming that a single MacZip header entry will fit in a memory stream.
            // Mac resource forks are limited to 16MB, so this seems reasonable.
            MemoryStream tmpMem;

            // Rewrite the AppleSingle file.
            using (Stream adfStream = ArcTemp.ExtractToTemp(arc, adfEntry, FilePart.DataFork)) {
                using (IArchive adfArchive = AppleSingle.OpenArchive(adfStream, appHook)) {
                    IFileEntry adfArchiveEntry = adfArchive.GetFirstEntry();
                    adfArchive.StartTransaction();
                    newAttribs.CopyAttrsTo(adfArchiveEntry, true);
                    adfArchive.CommitTransaction(tmpMem = new MemoryStream());
                }
            }

            // Replace AppleDouble header entry.
            arc.StartTransaction();
            arc.DeletePart(adfEntry, FilePart.DataFork);
            arc.AddPart(adfEntry, FilePart.DataFork, new SimplePartSource(tmpMem),
                CompressionFormat.Default);

            // Rename both files.
            string? hdrName = Zip.GenerateMacZipName(newAttribs.FullPathName);
            if (hdrName == null) {
                Debug.Assert(false, "can't make name out of '" + newAttribs.FullPathName + "'");
                return false;
            }
            mainEntry.FileName = newAttribs.FullPathName;
            adfEntry.FileName = hdrName;
            return true;
        }
    }
}
