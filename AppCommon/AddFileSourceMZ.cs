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

using CommonUtil;
using DiskArc;
using DiskArc.Arc;
using static DiskArc.Defs;

namespace AppCommon {
    /// <summary>
    /// Variation of AddFileSource, used for MacOSZip mode.  We need to generate an AppleDouble
    /// "header" file on the fly, from whatever source was provided.
    /// </summary>
    public class AddFileSourceMZ : IPartSource {
        private bool mDisposed;

        private AddFileEntry mEntry;
        private AddFileWorker.CallbackFunc mFunc;
        private int mProgressPercent;
        private FileConv.Importer? mImporter;
        private Dictionary<string, string>? mImportOptions;
        private AppHook mAppHook;

        private MemoryStream? mHeaderStream;

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="entry">File entry to add.</param>
        /// <param name="func">Progress callback function.</param>
        /// <param name="progressPercent">Percent complete for progress update.</param>
        /// <param name="appHook">Application hook reference.</param>
        public AddFileSourceMZ(AddFileEntry entry, AddFileWorker.CallbackFunc func,
                int progressPercent, FileConv.Importer? importer,
                Dictionary<string, string>? importOptions, AppHook appHook) {
            mEntry = entry;
            mFunc = func;
            mProgressPercent = progressPercent;
            mImporter = importer;
            mImportOptions = importOptions;
            mAppHook = appHook;
        }

        // IPartSource
        public void Open() {
            // Generate the AppleDouble "header" file.
            using AppleSingle header = AppleSingle.CreateDouble(2, mAppHook);
            header.StartTransaction();
            IFileEntry hdrEntry = header.GetFirstEntry();
            hdrEntry.FileType = mEntry.FileType;
            hdrEntry.AuxType = mEntry.AuxType;
            hdrEntry.HFSFileType = mEntry.HFSFileType;
            hdrEntry.HFSCreator = mEntry.HFSCreator;
            hdrEntry.CreateWhen = mEntry.CreateWhen;
            hdrEntry.ModWhen = mEntry.ModWhen;
            hdrEntry.Access = mEntry.Access;
            //hdrEntry.Comment = mEntry.Comment;

            // Read the resource fork from whatever source was provided.
            if (mEntry.HasRsrcFork) {
                string storageDisplayName;
                if (string.IsNullOrEmpty(mEntry.StorageDir)) {
                    storageDisplayName = mEntry.StorageName;
                } else {
                    storageDisplayName = mEntry.StorageDir + mEntry.StorageDirSep +
                        mEntry.StorageName;
                }
                AddFileSource subSource = new AddFileSource(mEntry.FullRsrcPath, mEntry.RsrcSource,
                    FilePart.RsrcFork, mFunc, mProgressPercent, storageDisplayName,
                    mEntry.StorageDirSep, mImporter, mImportOptions, mAppHook);
                header.AddPart(hdrEntry, FilePart.RsrcFork, subSource, CompressionFormat.Default);
            }

            // Commit it to a stream; the AppleSingle instance will be discarded.  We assume
            // it will fit in memory (reasonable for a resource fork).
            header.CommitTransaction(mHeaderStream = new MemoryStream());
            mHeaderStream.Position = 0;
        }

        // IPartSource
        public int Read(byte[] buf, int offset, int count) {
            return mHeaderStream!.Read(buf, offset, count);
        }

        // IPartSource
        public void Rewind() {
            mHeaderStream!.Position = 0;
        }

        // IPartSource
        public void Close() {
            // Disposing a MemoryStream doesn't actually do anything.  We just want to
            // dereference it so it can be discarded by the next GC.
            mHeaderStream?.Dispose();
            mHeaderStream = null;
        }

        ~AddFileSourceMZ() {
            Dispose(false);
        }
        public void Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        protected virtual void Dispose(bool disposing) {
            if (mDisposed) {
                Debug.Assert(!mDisposed, "Double dispose AddFileSourceMZ (disposing=" +
                    disposing + ")");
                return;
            }

            if (disposing) {
                Close();
            } else {
                mAppHook.LogW("GC disposing AddFileSourceMZ: " + mEntry);
                Debug.Assert(false, "GC disposing AddFileSourceMZ: " + mEntry);
            }
            mDisposed = true;
        }
    }
}
