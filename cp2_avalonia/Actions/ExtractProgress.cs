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
using FileConv;

namespace cp2_avalonia.Actions {
    /// <summary>
    /// This runs an ExtractFileWorker inside a WorkProgress dialog.
    /// </summary>
    internal class ExtractProgress(
        object archiveOrFileSystem,
        IFileEntry selectionDir,
        List<IFileEntry> selected,
        string outputDir,
        ConvConfig.FileConvSpec? exportSpec,
        AppHook appHook)
        : WorkProgressViewModel.IWorker
    {
        public ExtractFileWorker.PreserveMode Preserve { get; init; }
        public bool AddExportExt { get; init; }
        public bool EnableMacOSZip { get; init; }
        public bool StripPaths { get; init; }
        public bool RawMode { get; init; }
        public Dictionary<string, ConvConfig.FileConvSpec>? DefaultSpecs { get; init; }


        /// <summary>
        /// Performs the operation.
        /// </summary>
        /// <remarks>
        /// THIS RUNS ON THE WORKER THREAD.  Do not try to access GUI objects.
        /// </remarks>
        public object DoWork(BackgroundWorker bkWorker) {
            string curDir = Environment.CurrentDirectory;
            try {
                ProgressUtil.PersistentChoices choices = new();

                CallbackFacts.Results CbFunc(CallbackFacts what)
                {
                    return ProgressUtil.HandleCallback(what, "extract", choices, null, bkWorker);
                }

                ExtractFileWorker extWorker = new ExtractFileWorker(CbFunc,
                    addExportExt: AddExportExt, macZip: EnableMacOSZip, preserve: Preserve,
                    rawMode: RawMode, basePath: string.Empty, stripPaths: StripPaths, DefaultSpecs,
                    appHook);

                // Switch to output directory.
                Environment.CurrentDirectory = outputDir;

                if (archiveOrFileSystem is IArchive arc) {
                    if (!extWorker.ExtractFromArchive(arc, selected, exportSpec,
                            out bool wasCancelled)) {
                        return false;
                    }
                } else if (archiveOrFileSystem is IFileSystem fs) {
                    if (!extWorker.ExtractFromDisk(fs, selected, selectionDir, exportSpec,
                            out bool wasCancelled)) {
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
            var success = (results is true);
            Debug.WriteLine("ExtractProgress completed, success=" + success);
            return success;
        }
    }
}
