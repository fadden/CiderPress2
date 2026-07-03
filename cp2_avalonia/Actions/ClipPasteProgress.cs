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
using System.ComponentModel;
using System.Diagnostics;

using AppCommon;
using CommonUtil;
using cp2_avalonia.Models;
using cp2_avalonia.ViewModels;
using DiskArc;
using FileConv;

namespace cp2_avalonia.Actions {
    /// <summary>
    /// This runs a ClipPasteWorker inside a WorkProgress dialog.
    /// </summary>
    internal class ClipPasteProgress(
        object archiveOrFileSystem,
        DiskArcNode leafNode,
        IFileEntry targetDir,
        ClipInfo clipInfo,
        ClipPasteWorker.ClipStreamGenerator streamGen,
        AppHook appHook)
        : WorkProgressViewModel.IWorker
    {
        public bool DoCompress { get; init; }
        public bool EnableMacOSZip { get; init; }
        public bool ConvertDOSText { get; init; }
        public bool StripPaths { get; init; }
        public bool RawMode { get; init; }


        /// <summary>
        /// Performs the operation.
        /// </summary>
        /// <remarks>
        /// THIS RUNS ON THE WORKER THREAD.  Do not try to access GUI objects.
        /// </remarks>
        /// <param name="bkWorker">Background worker object.</param>
        /// <returns>Operation results.</returns>
        public object DoWork(BackgroundWorker bkWorker) {
            ProgressUtil.PersistentChoices choices = new ();

            CallbackFacts.Results CbFunc(CallbackFacts what)
            {
                return ProgressUtil.HandleCallback(what, "paste", choices, leafNode, bkWorker);
            }

            if (clipInfo.ClipEntries == null) {
                throw new NullReferenceException("No ClipEntries in ClipInfo");
            }
            bool isSameProcess = (Environment.ProcessId == clipInfo.ProcessId);
            ClipPasteWorker pasteWorker = new ClipPasteWorker(clipInfo.ClipEntries, streamGen,
                CbFunc, doCompress: DoCompress, macZip: EnableMacOSZip,
                convDosText: ConvertDOSText, stripPaths: StripPaths,
                rawMode: RawMode, isSameProcess: isSameProcess, appHook);

            switch (archiveOrFileSystem)
            {
                case IArchive system:
                {
                    IArchive arc = system;
                    try {
                        arc.StartTransaction();
                        pasteWorker.AddFilesToArchive(arc, out bool isCancelled);
                        if (isCancelled) {
                            return false;
                        }
                        bkWorker.ReportProgress(100, ProgressUtil.FINISHING_MSG);
                        leafNode.SaveUpdates(DoCompress);
                    } catch (CancelException) {
                        Debug.Assert(bkWorker.CancellationPending);
                        return false;
                    } catch (ConversionException ex) {
                        ProgressUtil.ShowMessage("Import error: " + ex.Message, true, bkWorker);
                        return false;
                    } catch (Exception ex) {
                        ProgressUtil.ShowMessage("Error: " + ex.Message, true, bkWorker);
                        return false;
                    } finally {
                        arc.CancelTransaction();    // no effect if transaction isn't open
                    }

                    break;
                }
                case IFileSystem system:
                {
                    IFileSystem fs = system;
                    bool success = true;
                    string? failMsg = null;
                    try {
                        pasteWorker.AddFilesToDisk(fs, targetDir, out bool isCancelled);
                        if (isCancelled) {
                            success = false;
                        }
                    } catch (ConversionException ex) {
                        failMsg = "Import error: " + ex.Message;
                        success = false;
                    } catch (Exception ex) {
                        failMsg = "Error: " + ex.Message;
                        success = false;
                    }
                    // Finish writing changes, regardless of if the operation succeeded.
                    try {
                        if (failMsg == null) {
                            bkWorker.ReportProgress(100, ProgressUtil.FINISHING_MSG);
                        }
                        leafNode.SaveUpdates(DoCompress);
                    } catch (Exception ex) {
                        ProgressUtil.ShowMessage("Error: update failed: " + ex.Message, true, bkWorker);
                        return false;
                    }
                    if (failMsg != null) {
                        ProgressUtil.ShowMessage(failMsg, true, bkWorker);
                    }
                    if (!success) {
                        return false;
                    }

                    break;
                }
                default:
                    Debug.Assert(false);
                    break;
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
