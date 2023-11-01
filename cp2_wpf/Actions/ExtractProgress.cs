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
using FileConv;

namespace cp2_wpf.Actions {
    /// <summary>
    /// This runs an ExtractFileWorker inside a WorkProgress dialog.
    /// </summary>
    internal class ExtractProgress : WorkProgress.IWorker {
        private object mArchiveOrFileSystem;
        private IFileEntry mSelectionDir;
        private List<IFileEntry> mSelected;
        private string mOutputDir;
        ConvConfig.FileConvSpec? mExportSpec;
        private AppHook mAppHook;

        public ExtractFileWorker.PreserveMode Preserve { get; set; }
        public bool EnableMacOSZip { get; set; }
        public bool StripPaths { get; set; }
        public bool RawMode { get; set; }
        public Dictionary<string, ConvConfig.FileConvSpec>? DefaultSpecs { get; set; }


        public ExtractProgress(object archiveOrFileSystem, IFileEntry selectionDir,
                List<IFileEntry> selected, string outputDir,
                ConvConfig.FileConvSpec? exportSpec, AppHook appHook) {
            mArchiveOrFileSystem = archiveOrFileSystem;
            mSelectionDir = selectionDir;
            mSelected = selected;
            mOutputDir = outputDir;
            mExportSpec = exportSpec;
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
            try {
                ExtractFileWorker.CallbackFunc cbFunc = delegate (CallbackFacts what) {
                    return ProgressUtil.HandleCallback(what, "extract", null, bkWorker);
                };
                ExtractFileWorker extWorker = new ExtractFileWorker(cbFunc, macZip: EnableMacOSZip,
                    preserve: Preserve, rawMode: RawMode, stripPaths: StripPaths, DefaultSpecs,
                    mAppHook);

                // Switch to output directory.
                Environment.CurrentDirectory = mOutputDir;

                if (mArchiveOrFileSystem is IArchive) {
                    IArchive arc = (IArchive)mArchiveOrFileSystem;
                    if (!extWorker.ExtractFromArchive(arc, mSelected, mExportSpec,
                            out bool wasCancelled)) {
                        // failed
                        if (wasCancelled) {
                            //ProgressUtil.ShowCancelled(bkWorker);
                            return false;
                        }
                        return false;
                    }
                } else if (mArchiveOrFileSystem is IFileSystem) {
                    IFileSystem fs = (IFileSystem)mArchiveOrFileSystem;
                    if (!extWorker.ExtractFromDisk(fs, mSelected, mSelectionDir, mExportSpec,
                            out bool wasCancelled)) {
                        // failed
                        if (wasCancelled) {
                            //ProgressUtil.ShowCancelled(bkWorker);
                            return false;
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

        public bool RunWorkerCompleted(object? results) {
            bool success = (results is true);
            Debug.WriteLine("Operation completed, success=" + success);
            return success;
        }
    }
}
