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
using System.IO;

using AppCommon;
using CommonUtil;
using cp2_wpf.WPFCommon;
using DiskArc;
using DiskArc.Arc;
using static DiskArc.Defs;
using static DiskArc.IFileSystem;

namespace cp2_wpf.Actions {
    /// <summary>
    /// Tests files inside a WorkProgress dialog.
    /// </summary>
    /// <remarks>
    /// <para>This always uses "raw data" for DOS, and performs recursive descent on filesystems.
    /// We need to pay attention to the MacZip setting because we only test the files that are
    /// selected, and when it's enabled you can only select the main entry in the UI.</para>
    /// </remarks>
    internal class TestProgress : WorkProgress.IWorker {
        private object mArchiveOrFileSystem;
        private List<IFileEntry> mSelected;
        private AppHook mAppHook;

        public bool EnableMacOSZip { get; set; }

        public class Failure {
            public IFileEntry Entry { get; private set; }
            public FilePart Part { get; private set; }

            public Failure(IFileEntry entry, FilePart part) {
                Entry = entry;
                Part = part;
            }
        }

        /// <summary>
        /// List of results.  If all goes well, the list will be empty.
        /// </summary>
        public List<Failure>? FailureResults { get; set; } = null;


        public TestProgress(object archiveOrFileSystem, List<IFileEntry> selected,
                AppHook appHook) {
            mArchiveOrFileSystem = archiveOrFileSystem;
            mSelected = selected;
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
            List<Failure> failures = new List<Failure>();

            if (mArchiveOrFileSystem is IArchive) {
                IArchive arc = (IArchive)mArchiveOrFileSystem;
                //if (!Misc.StdChecks(arc, needWrite: false, needMulti: false)) {
                //    return false;
                //}

                TestArchive(arc, mSelected, EnableMacOSZip, failures);
            } else {
                IFileSystem fs = (IFileSystem)mArchiveOrFileSystem;
                //if (!Misc.StdChecks(fs, needWrite: false, fastScan: false)) {
                //    return false;
                //}

                TestFileSystem(fs, mSelected, failures);

                // Don't descend into embedded volumes.  If they want to test those, they can
                // select the specific filesystem and test it.
            }

            return failures;
        }

        private static void TestArchive(IArchive arc, List<IFileEntry> selected,
                bool doMacZip, List<Failure> failures) {
            foreach (IFileEntry entry in selected) {
                if (doMacZip && Zip.HasMacZipHeader(arc, entry, out IFileEntry adfEntry)) {
                    // Test the full ADF entry.  To be thorough we should open the ADF
                    // entry and confirm the validity of the resource fork.
                    if (!TestArchiveFork(arc, adfEntry, FilePart.DataFork)) {
                        failures.Add(new Failure(adfEntry, FilePart.DataFork));
                    }
                }
                if (entry.IsDiskImage) {
                    if (!TestArchiveFork(arc, entry, FilePart.DiskImage)) {
                        failures.Add(new Failure(entry, FilePart.DiskImage));
                    }
                }
                if (entry.HasDataFork) {
                    if (!TestArchiveFork(arc, entry, FilePart.DataFork)) {
                        failures.Add(new Failure(entry, FilePart.DataFork));
                    }
                }
                if (entry.HasRsrcFork) {
                    if (!TestArchiveFork(arc, entry, FilePart.RsrcFork)) {
                        failures.Add(new Failure(entry, FilePart.RsrcFork));
                    }
                }
            }
        }

        private static bool TestArchiveFork(IArchive arc, IFileEntry entry, FilePart part) {
            //Debug.WriteLine("TestA: " + part + " " + entry.FullPathName);
            try {
                using (Stream stream = arc.OpenPart(entry, part)) {
                    stream.CopyTo(Stream.Null);
                }
            } catch (IOException) {
                return false;
            } catch (InvalidDataException) {
                return false;
            } catch {
                Debug.Assert(false);
                return false;
            }
            return true;
        }

        private static void TestFileSystem(IFileSystem fs, List<IFileEntry> selected,
                List<Failure> failures) {
            foreach (IFileEntry entry in selected) {
                if (entry.IsDirectory) {
                    // The selection set contains all entries (full recursive descent), so we
                    // don't need to handle that here.
                    continue;
                } else {
                    if (entry.HasDataFork) {
                        // Use RawData to more fully scan DOS 3.3 disks.
                        if (!TestDiskFork(fs, entry, FilePart.RawData)) {
                            failures.Add(new Failure(entry, FilePart.RawData));
                        }
                    }
                    if (entry.HasRsrcFork) {
                        if (!TestDiskFork(fs, entry, FilePart.RsrcFork)) {
                            failures.Add(new Failure(entry, FilePart.RsrcFork));
                        }
                    }
                }
            }
        }

        private static bool TestDiskFork(IFileSystem fs, IFileEntry entry, FilePart part) {
            //Debug.WriteLine("TestF: " + part + " " + entry.FullPathName);
            try {
                using (Stream stream = fs.OpenFile(entry, FileAccessMode.ReadOnly, part)) {
                    stream.CopyTo(Stream.Null);
                }
            } catch (IOException) {
                return false;
            } catch {
                Debug.Assert(false);
                return false;
            }
            return true;
        }

        public bool RunWorkerCompleted(object? results) {
            FailureResults = (List<Failure>?)results;
            return (FailureResults != null);
        }
    }
}
