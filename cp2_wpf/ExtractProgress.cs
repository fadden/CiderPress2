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
using System.Windows;

using AppCommon;
using CommonUtil;
using cp2_wpf.WPFCommon;
using DiskArc;

namespace cp2_wpf {
    /// <summary>
    /// This runs an ExtractFileWorker inside a WorkProgress dialog.
    /// </summary>
    internal class ExtractProgress : WorkProgress.IWorker {
        private object mArchiveOrFileSystem;
        private IFileEntry mSelectionDir;
        private List<IFileEntry> mSelected;
        private string mOutputDir;
        private AppHook mAppHook;

        public ExtractProgress(object archiveOrFileSystem, IFileEntry selectionDir,
                List<IFileEntry> selected, string outputDir, AppHook appHook) {
            mArchiveOrFileSystem = archiveOrFileSystem;
            mSelectionDir = selectionDir;
            mSelected = selected;
            mOutputDir = outputDir;
            mAppHook = appHook;
        }

        /// <summary>
        /// Perform the operation.
        /// </summary>
        /// <remarks>
        /// THIS RUNS ON THE WORKER THREAD.  Do not try to access GUI objects.
        /// </remarks>
        /// <param name="bkWorker">Background worker object.</param>
        /// <returns>Operation results.</returns>
        public object DoWork(BackgroundWorker bkWorker) {
            string curDir = Environment.CurrentDirectory;
            try {
                // Copy this to object for the benefit of the extract worker callbacks.

                ExtractFileWorker extWorker = new ExtractFileWorker(
                    delegate (CallbackFacts what, object? obj) {
                        return HandleCallback(what, "extract", bkWorker);
                    }, mAppHook);
                // TODO: get these from settings
                extWorker.EnableMacOSZip = true;
                extWorker.Preserve = ExtractFileWorker.PreserveMode.NAPS;
                extWorker.RawMode = false;
                extWorker.StripPaths = false;

                // Switch to output directory.
                Environment.CurrentDirectory = mOutputDir;

                if (mArchiveOrFileSystem is IArchive) {
                    IArchive arc = (IArchive)mArchiveOrFileSystem;
                    if (!extWorker.ExtractFromArchive(arc, mSelected, null,
                            out bool wasCancelled)) {
                        // failed
                        if (wasCancelled) {
                        }
                        return false;
                    }
                } else if (mArchiveOrFileSystem is IFileSystem) {
                    IFileSystem fs = (IFileSystem)mArchiveOrFileSystem;
                    if (!extWorker.ExtractFromDisk(fs, mSelected, mSelectionDir, null,
                            out bool wasCancelled)) {
                        // failed
                        if (wasCancelled) {
                        }
                        return false;
                    }
                } else {
                    Debug.Assert(false);
                }
            } finally {
                Environment.CurrentDirectory = curDir;
            }
            return true;
        }

        public void RunWorkerCompleted(object? results) {
            bool success = (results is true);
            Debug.WriteLine("Operation completed, success=" + success);
        }

        public CallbackFacts.Results HandleCallback(CallbackFacts what, string actionStr,
                BackgroundWorker bkWorker) {
            CallbackFacts.Results result = CallbackFacts.Results.Unknown;
            switch (what.Reason) {
                case CallbackFacts.Reasons.Progress:
                    if (bkWorker.CancellationPending) {
                        // TODO: the AppCommon code is currently ignoring this
                        result = CallbackFacts.Results.Cancel;
                        break;
                    }
                    bkWorker.ReportProgress(what.ProgressPercent, what.OrigPathName);
                    // DEBUG: sleep briefly so we can see the progress
                    //System.Threading.Thread.Sleep(500);
                    break;
                case CallbackFacts.Reasons.ResourceForkIgnored:
                case CallbackFacts.Reasons.PathTooLong:
                case CallbackFacts.Reasons.FileNameExists:
                    string ovwr = "Overwrite '" + what.OrigPathName + "' ?";
                    WorkProgress.MessageBoxQuery query = new WorkProgress.MessageBoxQuery(ovwr,
                        "Overwrite File?", MessageBoxButton.YesNoCancel,
                        MessageBoxImage.Question);
                    bkWorker.ReportProgress(0, query);
                    MessageBoxResult res = query.WaitForResult();
                    Debug.WriteLine("Overwrite '" + what.OrigPathName + "' -> " + res);
                    switch (res) {
                        case MessageBoxResult.Cancel:
                            result = CallbackFacts.Results.Cancel;
                            break;
                        case MessageBoxResult.OK:
                        case MessageBoxResult.Yes:
                            result = CallbackFacts.Results.Overwrite;
                            break;
                        case MessageBoxResult.No:
                        default:
                            result = CallbackFacts.Results.Skip;
                            break;
                    }
                    break;
                case CallbackFacts.Reasons.AttrFailure:
                case CallbackFacts.Reasons.OverwriteFailure:
                case CallbackFacts.Reasons.Failure:
                    break;
            }
            return result;
        }
    }
}
