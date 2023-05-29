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

namespace cp2_wpf.Actions {
    /// <summary>
    /// This opens the work file inside a WorkProgress dialog.
    /// </summary>
    internal class OpenProgress : WorkProgress.IWorker {
        public class Result {
            public Exception? mException;
            public WorkTree? mWorkTree;
        }

        public Result Results { get; private set; } = new Result();

        private string mPathName;
        private WorkTree.DepthLimiter mLimiter;
        private bool mAsReadOnly;
        private AppHook mAppHook;


        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="pathName">Pathname of file to open.</param>
        /// <param name="limiter">Auto-open depth limiter.</param>
        /// <param name="asReadOnly">True if we want to open the file read-only.</param>
        /// <param name="appHook">Application hook reference.</param>
        public OpenProgress(string pathName, WorkTree.DepthLimiter limiter, bool asReadOnly,
                AppHook appHook) {
            mPathName = pathName;
            mLimiter = limiter;
            mAsReadOnly = asReadOnly;
            mAppHook = appHook;
        }

        /// <summary>
        /// Perform the operation.
        /// </summary>
        /// <remarks>
        /// THIS RUNS ON THE WORKER THREAD.  Do not try to access GUI objects.
        /// </remarks>
        /// <param name="bkWorker">Background worker object.</param>
        /// <returns>Result object.</returns>
        public object DoWork(BackgroundWorker worker) {
            Result results = new Result();
            try {
                WorkTree workTree = new WorkTree(mPathName, mLimiter, mAsReadOnly, worker,
                    mAppHook);
                results.mWorkTree = workTree;
            } catch (Exception ex) {
                // We'll get an exception if the user cancelled the operation.  The WorkProgress
                // dialog's RunWorkerCompleted handler can sort that out.
                results.mException = ex;
            }
            return results;
        }

        /// <summary>
        /// Called on GUI thread after completion, to report results.
        /// </summary>
        /// <param name="results">Result object returned by work thread.</param>
        public void RunWorkerCompleted(object? results) {
            Results = (Result)results!;
        }
    }
}
