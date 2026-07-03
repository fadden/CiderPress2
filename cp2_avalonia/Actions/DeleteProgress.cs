/*
 * Copyright 2023 faddenSoft
 * Copyright 2026 Lydian Scale Software
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
using cp2_avalonia.ViewModels;
using DiskArc;

namespace cp2_avalonia.Actions {
    /// <summary>
    /// This runs a DeleteFileWorker inside a WorkProgress dialog.
    /// </summary>
    internal class DeleteProgress(
        object archiveOrFileSystem,
        DiskArcNode leafNode,
        List<IFileEntry> selected,
        AppHook appHook)
        : WorkProgressViewModel.IWorker
    {
        public bool DoCompress { get; init; }
        public bool EnableMacOSZip { get; init; }


        /// <summary>
        /// Performs the operation.
        /// </summary>
        /// <remarks>
        /// THIS RUNS ON THE WORKER THREAD.  Do not try to access GUI objects.
        /// </remarks>
        /// <param name="bkWorker">Background worker object.</param>
        /// <returns>Operation results.</returns>
        public object DoWork(BackgroundWorker bkWorker) {
            ProgressUtil.PersistentChoices choices = new();
            CallbackFacts.Results CbFunc(CallbackFacts what) => ProgressUtil.HandleCallback(what, "deleting", choices, leafNode, bkWorker);
            var worker = new DeleteFileWorker(CbFunc, macZip: EnableMacOSZip,
                    appHook);

            if (archiveOrFileSystem is IArchive arc) {
                try {
                    arc.StartTransaction();
                    if (!worker.DeleteFromArchive(arc, selected, out bool wasCancelled)) {
                        return false;
                    }
                    bkWorker.ReportProgress(100, ProgressUtil.FINISHING_MSG);
                    leafNode.SaveUpdates(DoCompress);
                } catch (Exception ex) {
                    leafNode.FlushStreams();
                    ProgressUtil.ShowMessage("Error: " + ex.Message, true, bkWorker);
                    return false;
                } finally {
                    arc.CancelTransaction();    // no effect if transaction isn't open
                }

            } else {
                IFileSystem fs = (IFileSystem)archiveOrFileSystem;
                bool success = worker.DeleteFromDisk(fs, selected, out bool wasCancelled);
                try {
                    // Save the deletions we managed to handle.
                    bkWorker.ReportProgress(100, ProgressUtil.FINISHING_MSG);
                    leafNode.SaveUpdates(DoCompress);
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
