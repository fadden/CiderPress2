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
    /// IPartSource used by AddFileWorker.  Handles input from plain files and
    /// AppleSingle/AppleDouble.
    /// </summary>
    public class AddFileSource : IPartSource {
        private bool mDisposed;

        private string mFullPath;
        private AddFileEntry.SourceType mSourceType;
        private FilePart mPart;
        private AddFileWorker.CallbackFunc mFunc;
        private int mProgressPercent;
        private string mStorageName;
        private char mStorageDirSep;
        private FileConv.Importer? mImporter;
        private Dictionary<string, string>? mImportOptions;
        private AppHook mAppHook;

        /// <summary>
        /// AppleSingle archive object we create to read data out of AS/ADF sources.
        /// </summary>
        private AppleSingle? mAppleSingle;

        /// <summary>
        /// "Outer" stream for AS/ADF.
        /// </summary>
        private Stream? mOuterStream;

        /// <summary>
        /// File data stream.  Readable but may not be seekable.
        /// </summary>
        private Stream? mFileStream;


        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="fullPath">Path to file to open.</param>
        /// <param name="sourceType">Type of file to open.</param>
        /// <param name="part">Which part of the file this represents (display only).</param>
        /// <param name="func">Progress callback function.</param>
        /// <param name="progressPercent">Percent complete for progress update.</param>
        /// <param name="storageName">Name used to store the file in the archive.  This is for
        ///   progress display only.</param>
        /// <param name="storageDirSep">Directory separator used in the storage name.</param>
        /// <param name="importer">Import converter.</param>
        /// <param name="importer">Import converter options.</param>
        /// <param name="appHook">Application hook reference.</param>
        public AddFileSource(string fullPath, AddFileEntry.SourceType sourceType,
                FilePart part, AddFileWorker.CallbackFunc func, int progressPercent,
                string storageName, char storageDirSep, FileConv.Importer? importer,
                Dictionary<string, string>? importOptions, AppHook appHook) {
            Debug.Assert(part == FilePart.DataFork || part == FilePart.RsrcFork);
            mFullPath = fullPath;
            mSourceType = sourceType;
            mPart = part;
            mFunc = func;
            mProgressPercent = progressPercent;
            mStorageName = storageName;
            mStorageDirSep = storageDirSep;
            mImporter = importer;
            mImportOptions = importOptions;
            mAppHook = appHook;
        }

        // IPartSource
        public void Open() {
            if (mOuterStream != null || mFileStream != null) {
                throw new InvalidOperationException("Stream is already open");
            }

            CallbackFacts facts = new CallbackFacts(CallbackFacts.Reasons.QueryCancel);
            if (mFunc(facts) == CallbackFacts.Results.Cancel) {
                // Asynchronous cancel requested.
                throw new CancelException("Cancelled in AddFileSource");
            }

            facts = new CallbackFacts(CallbackFacts.Reasons.Progress);
            // Configure before/after filenames.
            string curDir = Environment.CurrentDirectory;
            if (mFullPath.StartsWith(curDir)) {
                facts.OrigPathName = mFullPath.Substring(curDir.Length + 1);
            } else {
                facts.OrigPathName = mFullPath;
            }
            facts.OrigDirSep = Path.DirectorySeparatorChar;
            facts.NewPathName = mStorageName;
            facts.NewDirSep = mStorageDirSep;
            facts.Part = mPart;
            facts.ProgressPercent = mProgressPercent;
            mFunc(facts);

            switch (mSourceType) {
                case AddFileEntry.SourceType.Plain:
                case AddFileEntry.SourceType.Import:
                    mFileStream = new FileStream(mFullPath, FileMode.Open, FileAccess.Read);
                    break;
                case AddFileEntry.SourceType.AppleSingle:
                    mOuterStream = new FileStream(mFullPath, FileMode.Open, FileAccess.Read);
                    mAppleSingle = AppleSingle.OpenArchive(mOuterStream, mAppHook);
                    mFileStream = mAppleSingle.OpenPart(mAppleSingle.GetFirstEntry(), mPart);
                    break;
                case AddFileEntry.SourceType.AppleDouble:
                    mOuterStream = new FileStream(mFullPath, FileMode.Open, FileAccess.Read);
                    mAppleSingle = AppleSingle.OpenArchive(mOuterStream, mAppHook);
                    mFileStream = mAppleSingle.OpenPart(mAppleSingle.GetFirstEntry(), mPart);
                    break;
                default:
                    throw new NotImplementedException("Source type not handled: " + mSourceType);
            }

            if (mSourceType == AddFileEntry.SourceType.Import) {
                Debug.Assert(mImporter != null && mImportOptions != null);
                // Do the import conversion to a temporary stream.
                long estimatedLength = mImporter.EstimateOutputSize(mFileStream);
                // TODO: use a temporary file on disk if it's huge
                Stream tmpStream = new MemoryStream();
                Debug.Assert(mPart == FilePart.DataFork || mPart == FilePart.RsrcFork);
                if (mPart == FilePart.DataFork) {
                    mImporter.ConvertFile(mFileStream, mImportOptions, tmpStream, Stream.Null);
                } else {
                    mImporter.ConvertFile(mFileStream, mImportOptions, Stream.Null, tmpStream);
                }
                mFileStream.Close();
                mFileStream = tmpStream;
                mFileStream.Position = 0;
            }
        }

        // IPartSource
        public int Read(byte[] buf, int offset, int count) {
            Debug.Assert(mFileStream != null);
            return mFileStream.Read(buf, offset, count);
        }

        // IPartSource
        public void Rewind() {
            if (mOuterStream == null) {
                Debug.Assert(mSourceType == AddFileEntry.SourceType.Plain ||
                    mSourceType == AddFileEntry.SourceType.Import);
                mFileStream!.Position = 0;
            } else {
                // Archive stream, can't be rewound.  Open the part again.
                Debug.Assert(mAppleSingle != null);
                mFileStream!.Dispose();
                mFileStream = mAppleSingle.OpenPart(mAppleSingle.GetFirstEntry(), mPart);
            }
        }

        // IPartSource
        public void Close() {
            mFileStream?.Dispose();
            mFileStream = null;
            mAppleSingle?.Dispose();
            mAppleSingle = null;
            mOuterStream?.Dispose();
            mOuterStream = null;
        }

        ~AddFileSource() {
            Dispose(false);     // cleanup check
        }
        public void Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        protected virtual void Dispose(bool disposing) {
            if (mDisposed) {
                Debug.Assert(!mDisposed, "Double dispose AddFileSource (disposing=" +
                    disposing + ")");
                return;
            }
            mDisposed = true;

            if (disposing) {
                Close();
            } else {
                mAppHook.LogW("GC disposing AddFileSource: " + mFullPath);
                Debug.Assert(false, "GC disposing AddFileSource: " + mFullPath);
            }
        }
    }
}
