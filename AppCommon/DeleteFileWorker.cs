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
using DiskArc.Arc;

namespace AppCommon {
    /// <summary>
    /// Deletes entries from file archives and disk images.
    /// </summary>
    public class DeleteFileWorker {
        /// <summary>
        /// Callback function interface definition.
        /// </summary>
        public delegate CallbackFacts.Results CallbackFunc(CallbackFacts what);

        /// <summary>
        /// If set, attempts to remove the "._" file will be ignored, and removal of the
        /// base file will also remove the header.
        /// </summary>
        public bool IsMacZipEnabled { get; set; } = false;

        private CallbackFunc mFunc;
        private AppHook mAppHook;


        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="func">Callback function, for progress messages.</param>
        /// <param name="appHook">Application hook reference.</param>
        public DeleteFileWorker(CallbackFunc func, AppHook appHook) {
            mFunc = func;
            mAppHook = appHook;
        }

        /// <summary>
        /// Deletes a list of files from a file archive.
        /// </summary>
        public bool DeleteFromArchive(IArchive arc, List<IFileEntry> entries) {
            int doneCount = 0;
            foreach (IFileEntry entry in entries) {
                ShowProgress(entry.FullPathName, entry.DirectorySeparatorChar,
                    (100 * doneCount) / entries.Count);

                IFileEntry adfEntry = IFileEntry.NO_ENTRY;
                if (arc is Zip && IsMacZipEnabled) {
                    // Only handle __MACOSX/ entries when paired with other entries.
                    if (entry.IsMacZipHeader()) {
                        continue;
                    }
                    // Check to see if we have a paired entry.
                    string? macZipName = Zip.GenerateMacZipName(entry.FullPathName);
                    if (!string.IsNullOrEmpty(macZipName)) {
                        arc.TryFindFileEntry(macZipName, out adfEntry);
                    }
                }

                arc.DeleteRecord(entry);
                if (adfEntry != IFileEntry.NO_ENTRY) {
                    arc.DeleteRecord(adfEntry);
                }
                doneCount++;
            }
            return true;
        }

        /// <summary>
        /// Deletes a list of files from a disk image.
        /// </summary>
        /// <remarks>
        /// <para>The list of files must be in sorted order.  Any ascending sort should work.
        /// We just need the contents of each subdirectory to appear after the entry for
        /// the subdirectory itself.</para>
        /// </remarks>
        public bool DeleteFromDisk(IFileSystem fs, List<IFileEntry> entries) {
            // We need to delete the files that live in a directory before we delete that
            // directory.  Recursive traversals generate the list in exactly the
            // wrong order, so we need to walk it from back to front.
            int doneCount = 0;
            for (int i = entries.Count - 1; i >= 0; i--) {
                IFileEntry entry = entries[i];
                try {
                    ShowProgress(entry.FullPathName, entry.DirectorySeparatorChar,
                        (100 * doneCount) / entries.Count);

                    fs.DeleteFile(entry);
                    doneCount++;
                } catch (IOException ex) {
                    ReportFailure("Error: unable to delete '" + entry.FullPathName +
                        "': " + ex.Message);
                    return false;
                }
            }
            return true;
        }

        private void ShowProgress(string deletePath, char dirSep, int percent) {
            CallbackFacts facts = new CallbackFacts(CallbackFacts.Reasons.Progress,
                deletePath, dirSep);
            facts.ProgressPercent = percent;
            mFunc(facts);
        }

        private void ReportFailure(string msg) {
            CallbackFacts facts = new CallbackFacts(CallbackFacts.Reasons.Failure);
            facts.FailMessage = msg;
            mFunc(facts);
        }
    }
}
