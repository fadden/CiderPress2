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
using System.Diagnostics;
using System.Text;

using CommonUtil;
using DiskArc;

namespace AppCommon {
    /// <summary>
    /// Moves a collection of files to a new directory on a disk image.  This is a very fast
    /// operation, but we want to be able to show progress updates and potentially query for name
    /// changes.  This operation is only used for hierarchical filesystems.
    /// </summary>
    public class MoveFileWorker {
        /// <summary>
        /// Callback function interface definition.
        /// </summary>
        public delegate CallbackFacts.Results CallbackFunc(CallbackFacts what);

        private CallbackFunc mFunc;
        private AppHook mAppHook;


        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="func">Callback function, for progress messages.</param>
        /// <param name="appHook">Application hook reference.</param>
        public MoveFileWorker(CallbackFunc func, AppHook appHook) {
            mFunc = func;
            mAppHook = appHook;
        }

        /// <summary>
        /// Moves multiple files into a directory, retaining their current filenames.
        /// </summary>
        /// <remarks>
        /// <para>For best results, the caller should screen out invalid operations, such as
        /// moving a directory into itself or one of its children.</para>
        /// </remarks>
        /// <returns>True on success, false on failure or cancellation.</returns>
        public bool MoveDiskEntries(IFileSystem fs, List<IFileEntry> entries, IFileEntry destDir,
                out bool wasCancelled) {
            wasCancelled = false;
            int doneCount = 0;
            foreach (IFileEntry entry in entries) {
                if (IsCancelPending()) {
                    wasCancelled = true;
                    return false;
                }
                CallbackFacts facts = new CallbackFacts(CallbackFacts.Reasons.Progress,
                    entry.FullPathName, entry.DirectorySeparatorChar);
                if (destDir.ContainingDir == IFileEntry.NO_ENTRY) {
                    facts.NewPathName = entry.FileName;     // don't show vol dir name
                } else {
                    facts.NewPathName = destDir.FullPathName + fs.Characteristics.DirSep +
                        entry.FileName;
                }
                facts.NewDirSep = fs.Characteristics.DirSep;
                facts.ProgressPercent = (100 * doneCount) / entries.Count;
                if (entry.ContainingDir == destDir) {
                    facts.NewPathName = "(no change)";
                }
                mFunc(facts);

                try {
                    // This will catch attempts to move a directory inside itself.
                    fs.MoveFile(entry, destDir, entry.FileName);
                } catch (IOException ex) {
                    ReportFailure("Error: unable to move '" + entry.FullPathName +
                        "': " + ex.Message);
                    return false;
                } catch (Exception ex) {
                    ReportFailure("Error: unexpected exception: " + ex.Message);
                    return false;
                }

                doneCount++;
            }
            return true;
        }

        private void ReportFailure(string msg) {
            CallbackFacts facts = new CallbackFacts(CallbackFacts.Reasons.Failure);
            facts.FailMessage = msg;
            mFunc(facts);
        }

        private bool IsCancelPending() {
            CallbackFacts facts = new CallbackFacts(CallbackFacts.Reasons.QueryCancel);
            return mFunc(facts) == CallbackFacts.Results.Cancel;
        }
    }
}
