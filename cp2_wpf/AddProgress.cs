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
using System.Windows;

using AppCommon;
using CommonUtil;
using cp2_wpf.WPFCommon;
using DiskArc;
using FileConv;

namespace cp2_wpf {
    /// <summary>
    /// This runs an AddFileWorker inside a WorkProgress dialog.
    /// </summary>
    internal class AddProgress : WorkProgress.IWorker {
        private object mArchiveOrFileSystem;
        private DiskArcNode mLeafNode;
        private AddFileSet mAddFileSet;
        private IFileEntry mTargetDir;
        private bool mDoCompress = true;        // TODO: configure
        private AppHook mAppHook;

        public AddProgress(object archiveOrFileSystem, DiskArcNode leafNode, AddFileSet addSet,
                IFileEntry targetDir, AppHook appHook) {
            mArchiveOrFileSystem = archiveOrFileSystem;
            mLeafNode = leafNode;
            mAddFileSet = addSet;
            mTargetDir = targetDir;
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

            AddFileWorker addWorker = new AddFileWorker(mAddFileSet,
                delegate (CallbackFacts what, object? obj) {
                    return HandleCallback(what, "add", bkWorker);
                }, mAppHook);

            if (mArchiveOrFileSystem is IArchive) {
                IArchive arc = (IArchive)mArchiveOrFileSystem;
                // TODO: Misc.StdChecks equivalent (maybe in caller?)
                try {
                    arc.StartTransaction();
                    addWorker.AddFilesToArchive(arc, out bool isCancelled);
                    if (isCancelled) {
                        Console.Error.WriteLine("Cancelled.");
                        return false;
                    }
                    mLeafNode.SaveUpdates(mDoCompress);
                } catch (ConversionException ex) {
                    Console.Error.WriteLine("Import error: " + ex.Message);
                    return false;
                } catch (Exception ex) {
                    Console.Error.WriteLine("Error: " + ex.Message);
                    return false;
                } finally {
                    arc.CancelTransaction();    // no effect if transaction isn't open
                }
            } else if (mArchiveOrFileSystem is IFileSystem) {
                IFileSystem fs = (IFileSystem)mArchiveOrFileSystem;
                // TODO: Misc.StdChecks equivalent (maybe in caller?)
                bool success = true;
                try {
                    addWorker.AddFilesToDisk(fs, mTargetDir, out bool isCancelled);
                    if (isCancelled) {
                        Console.Error.WriteLine("Cancelled.");
                        // continue; some changes may have been made
                        success = false;
                    }
                } catch (ConversionException ex) {
                    Console.Error.WriteLine("Import error: " + ex.Message);
                    success = false;
                } catch (Exception ex) {
                    Console.Error.WriteLine("Error: " + ex.Message);
                    success = false;
                }
                try {
                    mLeafNode.SaveUpdates(mDoCompress);
                } catch (Exception ex) {
                    Console.Error.WriteLine("Error: update failed: " + ex.Message);
                    return false;
                }
                if (!success) {
                    return false;
                }
            } else {
                Debug.Assert(false);
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
                    bkWorker.ReportProgress(-1, what.OrigPathName);
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
