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
using AppCommon;
using CommonUtil;
using cp2_avalonia.ViewModels;

namespace cp2_avalonia.Actions {
    /// <summary>
    /// Opens the work file inside a WorkProgress dialog.
    /// </summary>
    internal class OpenProgress : WorkProgressViewModel.IWorker {
        public class Result {
            public Exception? mException;
            public WorkTree? mWorkTree;
        }

        public Result Results { get; private set; } = new Result();

        private readonly string mPathName;
        private readonly WorkTree.DepthLimiter mLimiter;
        private readonly bool mAsReadOnly;
        private readonly AppHook mAppHook;

        /// <summary>
        /// Constructor for file open.
        /// </summary>
        public OpenProgress(string pathName, WorkTree.DepthLimiter limiter, bool asReadOnly,
                AppHook appHook) {
            mPathName = pathName;
            mLimiter = limiter;
            mAsReadOnly = asReadOnly;
            mAppHook = appHook;
        }

        /// <summary>
        /// Performs the file-open operation on the worker thread.
        /// </summary>
        /// <remarks>THIS RUNS ON THE WORKER THREAD.  Do not access GUI objects.</remarks>
        public object DoWork(BackgroundWorker worker) {
            Result results = new Result();
            try {
                WorkTree workTree = new WorkTree(mPathName, mLimiter, mAsReadOnly, worker,
                    mAppHook);
                results.mWorkTree = workTree;
            } catch (Exception ex) {
                results.mException = ex;
            }
            return results;
        }

        /// <summary>
        /// Called on the GUI thread after completion.
        /// </summary>
        public bool RunWorkerCompleted(object? results) {
            Results = (Result)results!;
            return (Results.mWorkTree != null);
        }
    }
}
