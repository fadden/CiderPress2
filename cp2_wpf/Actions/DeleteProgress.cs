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

using AppCommon;
using CommonUtil;
using cp2_wpf.WPFCommon;
using DiskArc;

namespace cp2_wpf.Actions {
    /// <summary>
    /// This runs a DeleteFileWorker inside a WorkProgress dialog.
    /// </summary>
    internal class DeleteProgress : WorkProgress.IWorker {
        private object mArchiveOrFileSystem;
        private DiskArcNode mLeafNode;
        private List<IFileEntry> mSelected;
        private AppHook mAppHook;

        public bool DoCompress { get; set; }
        public bool EnableMacOSZip { get; set; }


        public DeleteProgress(object archiveOrFileSystem, DiskArcNode leafNode,
                List<IFileEntry> selected, AppHook appHook) {
            mArchiveOrFileSystem = archiveOrFileSystem;
            mLeafNode = leafNode;
            mSelected = selected;
            mAppHook = appHook;
        }

        /// <summary>
        /// Performs the operation.
        /// </summary>
        /// <remarks>
        /// THIS RUNS ON THE WORKER THREAD.  Do not try to access GUI objects.
        /// </remarks>
        /// <param name="bkWorker">Background worker object.</param>
        /// <returns>Operation results.</returns>
        public object DoWork(BackgroundWorker bkWorker) {
            ProgressUtil.PersistentChoices choices = new ProgressUtil.PersistentChoices();
            DeleteFileWorker.CallbackFunc cbFunc = delegate (CallbackFacts what) {
                return ProgressUtil.HandleCallback(what, "deleting", choices, mLeafNode, bkWorker);
            };
            DeleteFileWorker worker = new DeleteFileWorker(cbFunc, macZip: EnableMacOSZip,
                    mAppHook);

            if (mArchiveOrFileSystem is IArchive) {
                IArchive arc = (IArchive)mArchiveOrFileSystem;
                try {
                    arc.StartTransaction();
                    if (!worker.DeleteFromArchive(arc, mSelected, out bool wasCancelled)) {
                        return false;
                    }
                    bkWorker.ReportProgress(100, ProgressUtil.FINISHING_MSG);
                    mLeafNode.SaveUpdates(DoCompress);
                } catch (Exception ex) {
                    mLeafNode.FlushStreams();
                    ProgressUtil.ShowMessage("Error: " + ex.Message, true, bkWorker);
                    return false;
                } finally {
                    arc.CancelTransaction();    // no effect if transaction isn't open
                }

            } else {
                IFileSystem fs = (IFileSystem)mArchiveOrFileSystem;
                bool success = worker.DeleteFromDisk(fs, mSelected, out bool wasCancelled);
                try {
                    // Save the deletions we managed to handle.
                    bkWorker.ReportProgress(100, ProgressUtil.FINISHING_MSG);
                    mLeafNode.SaveUpdates(DoCompress);
                } catch (Exception ex) {
                    ProgressUtil.ShowMessage("Error: update failed: " + ex.Message, true, bkWorker);
                    return false;
                }
                if (!success) {
                    return false;
                }
            }
            return true;
        }

        public bool RunWorkerCompleted(object? results) {
            bool success = (results is true);
            Debug.WriteLine("Operation completed, success=" + success);
            return success;
        }
    }
}
