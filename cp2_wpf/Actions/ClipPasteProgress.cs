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
using System.ComponentModel;
using System.Diagnostics;

using AppCommon;
using CommonUtil;
using cp2_wpf.WPFCommon;
using DiskArc;
using FileConv;

namespace cp2_wpf.Actions {
    /// <summary>
    /// This runs a ClipPasteWorker inside a WorkProgress dialog.
    /// </summary>
    internal class ClipPasteProgress : WorkProgress.IWorker {
        private object mArchiveOrFileSystem;
        private DiskArcNode mLeafNode;
        private IFileEntry mTargetDir;
        private ClipInfo mClipInfo;
        private ClipPasteWorker.ClipStreamGenerator mStreamGen;
        private AppHook mAppHook;

        public bool DoCompress { get; set; }
        public bool EnableMacOSZip { get; set; }
        public bool ConvertDOSText { get; set; }
        public bool StripPaths { get; set; }
        public bool RawMode { get; set; }


        public ClipPasteProgress(object archiveOrFileSystem, DiskArcNode leafNode,
                IFileEntry targetDir, ClipInfo clipInfo,
                ClipPasteWorker.ClipStreamGenerator streamGen, AppHook appHook) {
            mArchiveOrFileSystem = archiveOrFileSystem;
            mLeafNode = leafNode;
            mTargetDir = targetDir;
            mClipInfo = clipInfo;
            mStreamGen = streamGen;
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
            string curDir = Environment.CurrentDirectory;

            ClipPasteWorker.CallbackFunc cbFunc = delegate (CallbackFacts what) {
                return ProgressUtil.HandleCallback(what, "paste", mLeafNode, bkWorker);
            };
            if (mClipInfo.ClipEntries == null) {
                throw new NullReferenceException("No ClipEntries in ClipInfo");
            }
            bool isSameProcess = (Process.GetCurrentProcess().Id == mClipInfo.ProcessId);
            ClipPasteWorker pasteWorker = new ClipPasteWorker(mClipInfo.ClipEntries, mStreamGen,
                cbFunc, doCompress: DoCompress, macZip: EnableMacOSZip,
                convDosText: ConvertDOSText, stripPaths: StripPaths,
                rawMode: RawMode, isSameProcess: isSameProcess, mAppHook);

            if (mArchiveOrFileSystem is IArchive) {
                IArchive arc = (IArchive)mArchiveOrFileSystem;
                try {
                    arc.StartTransaction();
                    pasteWorker.AddFilesToArchive(arc, out bool isCancelled);
                    if (isCancelled) {
                        //ProgressUtil.ShowCancelled(bkWorker);
                        return false;
                    }
                    bkWorker.ReportProgress(100, ProgressUtil.FINISHING_MSG);
                    mLeafNode.SaveUpdates(DoCompress);
                } catch (AppCommon.CancelException) {
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
            } else if (mArchiveOrFileSystem is IFileSystem) {
                IFileSystem fs = (IFileSystem)mArchiveOrFileSystem;
                bool success = true;
                string? failMsg = null;
                try {
                    pasteWorker.AddFilesToDisk(fs, mTargetDir, out bool isCancelled);
                    if (isCancelled) {
                        //ProgressUtil.ShowCancelled(bkWorker);
                        // continue; some changes may have been made
                        success = false;
                    }
                } catch (ConversionException ex) {
                    failMsg = "Import error: " + ex.Message;
                    success = false;
                } catch (Exception ex) {
                    failMsg = "Error: " + ex.Message;
                    success = false;
                }
                // Finish writing changes, whether or not the operation succeeded.
                try {
                    // If we're failing, leave the problematic filename on screen.
                    if (failMsg == null) {
                        bkWorker.ReportProgress(100, ProgressUtil.FINISHING_MSG);
                    }
                    mLeafNode.SaveUpdates(DoCompress);
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
            } else {
                Debug.Assert(false);
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
