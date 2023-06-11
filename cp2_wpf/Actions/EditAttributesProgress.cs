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
        /// Performs the attribute update.  Errors are reported to the user.
        /// </summary>
        /// <returns>True on success.</returns>
        public bool DoUpdate(bool updateMacZip) {
            try {
                Mouse.OverrideCursor = Cursors.Wait;

                if (mArchiveOrFileSystem is IArchive) {
                    IArchive arc = (IArchive)mArchiveOrFileSystem;
                    if (updateMacZip) {
                        Debug.Assert(EnableMacOSZip);
                        // TODO: open mADFEntry and modify AppleDouble
                        throw new NotImplementedException("TODO");
                    }
                    try {
                        arc.StartTransaction();
                        // TODO: set attribs
                        mLeafNode.SaveUpdates(DoCompress);
                    } finally {
                        arc.CancelTransaction();    // no effect if transaction isn't open
                    }
                } else {
                    IFileSystem fs = (IFileSystem)mArchiveOrFileSystem;
                    // TODO: set attribs
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

        private bool HandleMacZip() {
            throw new NotImplementedException("TODO");
        }
    }
}
