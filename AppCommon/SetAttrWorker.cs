/*
 * Copyright 2024 faddenSoft
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
using static DiskArc.Defs;

namespace AppCommon {
    /// <summary>
    /// Sets file and volume directory attributes.
    /// </summary>
    public class SetAttrWorker {
        public enum AttrID {
            Unknown = 0, Type, AuxType, HFSFileType, HFSCreator, Access, CreateWhen, ModWhen
        }

        /// <summary>
        /// Attribute edit action.
        /// </summary>
        public class AttrEdit {
            public AttrID ID { get; private set; }
            public object Value { get; private set; }

            public AttrEdit(AttrID id, object value) {
                ID = id;
                Value = value;
            }
        }


        /// <summary>
        /// Callback function interface definition.
        /// </summary>
        public delegate CallbackFacts.Results CallbackFunc(CallbackFacts what);

        /// <summary>
        /// If true, the attributes of the encapsulated file are set instead.
        /// </summary>
        public bool IsMacZipEnabled { get; set; } = false;

        private CallbackFunc mFunc;
        private AppHook mAppHook;


        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="func">Callback function, for progress messages.</param>
        /// <param name="macZip">True if MacZip is enabled.</param>
        /// <param name="appHook">Application hook reference.</param>
        public SetAttrWorker(CallbackFunc func, bool macZip, AppHook appHook) {
            mFunc = func;
            IsMacZipEnabled = macZip;
            mAppHook = appHook;
        }

        /// <summary>
        /// Sets attributes for a set of files in an archive.  Do not start an archive transaction
        /// before calling here (interferes with MacZip).  On return, the transaction will be
        /// open unless something failed early.
        /// </summary>
        public bool SetAttrInArchive(IArchive arc, List<IFileEntry> entries,
                List<AttrEdit> editList, out bool wasCancelled) {
            wasCancelled = false;

            // Unfortunately, we can't read from an archive while we're modifying it, so we
            // need to create the modified MacZip entries before we start the transaction.
            // This could require a very large amount of memory or a large number of temp files.
            // It might make more sense to open the archive file a second time, but that has its
            // own share of problems.  Fortunately, we only need to edit the ADF entry, which
            // will only be large if it has a big resource fork.
            //
            // We make a dictionary with the AppleDouble entry as key, and a memory stream with
            // the new contents as value.
            Dictionary<IFileEntry, Stream>? macZipEdits = null;
            if (arc is Zip && IsMacZipEnabled) {
                macZipEdits = new Dictionary<IFileEntry, Stream>();
                foreach (IFileEntry entry in entries) {
                    if (IsCancelPending()) {
                        wasCancelled = true;
                        return false;
                    }

                    if (entry.IsMacZipHeader()) {
                        // Ignore AppleDouble "header" entries.  They won't be present at all if
                        // the entry list was generated from wildcards, and if they don't have a
                        // paired entry we want to ignore them.
                        continue;
                    }
                    // See if this file has a paired header.
                    if (Zip.HasMacZipHeader(arc, entry, out IFileEntry adfEntry)) {
                        Stream? newStream = HandleMacZip(arc, adfEntry, editList,
                                out IFileEntry adfArchiveEntry);
                        if (newStream == null) {
                            ReportFailure("Error: unable to set attrs on '" + entry.FullPathName +
                                "' - '" + adfArchiveEntry.FullPathName + "'");
                            return false;
                        }
                        macZipEdits.Add(adfEntry, newStream);
                    }
                }
            }

            arc.StartTransaction();

            int doneCount = 0;
            foreach (IFileEntry entry in entries) {
                if (IsCancelPending()) {
                    wasCancelled = true;
                    return false;
                }
                ShowProgress(entry.FullPathName, entry.DirectorySeparatorChar,
                    (100 * doneCount) / entries.Count);

                if (arc is Zip && IsMacZipEnabled) {
                    Debug.Assert(macZipEdits != null);
                    if (entry.IsMacZipHeader()) {
                        continue;
                    }
                    if (Zip.HasMacZipHeader(arc, entry, out IFileEntry adfEntry)) {
                        Stream newStream = macZipEdits[adfEntry];
                        // Replace AppleSingle file.  Compression isn't relevant for AS.
                        arc.DeletePart(adfEntry, FilePart.DataFork);
                        arc.AddPart(adfEntry, FilePart.DataFork, new SimplePartSource(newStream),
                            CompressionFormat.Default);
                    } else {
                        if (!SetAttrs(entry, editList)) {
                            ReportFailure("Error: unable to set attrs on '" +
                                entry.FullPathName + "'");
                            return false;
                        }
                    }
                } else {
                    if (!SetAttrs(entry, editList)) {
                        ReportFailure("Error: unable to set attrs on '" + entry.FullPathName + "'");
                        return false;
                    }
                }
                doneCount++;
            }
            return true;
        }

        /// <summary>
        /// Handles all processing for a MacZip entry.
        /// </summary>
        private Stream? HandleMacZip(IArchive arc, IFileEntry adfEntry, List<AttrEdit> editList,
                out IFileEntry adfArchiveEntry) {
            // We need to extract the AppleDouble "header" data as an archive, update the first
            // record, and record it back to the parent archive.  This currently holds the
            // contents in a memory stream.  Might want to use a delete-on-exit temp file instead.
            MemoryStream tmpMem;

            // Extract the AppleSingle file, and display or update the attributes.
            using (Stream adfStream = ArcTemp.ExtractToTemp(arc, adfEntry, FilePart.DataFork)) {
                using (IArchive adfArchive = AppleSingle.OpenArchive(adfStream, mAppHook)) {
                    adfArchiveEntry = adfArchive.GetFirstEntry();

                    // Make the edits.
                    adfArchive.StartTransaction();
                    if (!SetAttrs(adfArchiveEntry, editList)) {
                        return null;
                    }
                    adfArchive.CommitTransaction(tmpMem = new MemoryStream());
                }
            }
            return tmpMem;
        }

        /// <summary>
        /// Sets attributes for a set of files in a disk image.
        /// </summary>
        public bool SetAttrInDisk(IFileSystem fs, List<IFileEntry> entries,
                List<AttrEdit> editList, out bool wasCancelled) {
            wasCancelled = false;

            int doneCount = 0;
            foreach (IFileEntry entry in entries) {
                ShowProgress(entry.FullPathName, entry.DirectorySeparatorChar,
                    (100 * doneCount) / entries.Count);

                if (!SetAttrs(entry, editList)) {
                    ReportFailure("Error: unable to set attrs on '" + entry.FullPathName + "'");
                    return false;
                }
                doneCount++;
            }
            return true;
        }

        /// <summary>
        /// Sets the attributes specified in the edit list.
        /// </summary>
        private static bool SetAttrs(IFileEntry entry, List<AttrEdit> editList) {
            foreach (AttrEdit edit in editList) {
                switch (edit.ID) {
                    case AttrID.Type:
                        entry.FileType = (byte)edit.Value;
                        break;
                    case AttrID.AuxType:
                        entry.AuxType = (ushort)edit.Value;
                        break;
                    case AttrID.HFSFileType:
                        entry.HFSFileType = (uint)edit.Value;
                        break;
                    case AttrID.HFSCreator:
                        entry.HFSCreator = (uint)edit.Value;
                        break;
                    case AttrID.Access:
                        entry.Access = (byte)edit.Value;
                        break;
                    case AttrID.CreateWhen:
                        entry.CreateWhen = (DateTime)edit.Value;
                        break;
                    case AttrID.ModWhen:
                        entry.ModWhen = (DateTime)edit.Value;
                        break;
                    default:
                        Console.Error.WriteLine("Unknown attr ID: " + edit.ID);
                        return false;
                }
            }
            entry.SaveChanges();
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

        private bool IsCancelPending() {
            CallbackFacts facts = new CallbackFacts(CallbackFacts.Reasons.QueryCancel);
            return mFunc(facts) == CallbackFacts.Results.Cancel;
        }
    }
}
