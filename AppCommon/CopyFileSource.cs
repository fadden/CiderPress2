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
using static DiskArc.IFileSystem;

namespace AppCommon {
    /// <summary>
    /// IPartSource used by CopyFileWorker.  Allows entries in archives and filesystems to be used
    /// as source streams.  Supports MacZip sources and destinations.
    /// </summary>
    public class CopyFileSource : IPartSource {
        private bool mDisposed;

        private object mSrcObj;
        private IFileEntry mSrcEntry;
        private FilePart mPart;
        private FileAttribs? mAdfAttrs;
        private bool mSrcIsMacZip;
        private CopyFileWorker.CallbackFunc mFunc;
        private int mProgressPercent;
        private bool mDstIsADF;
        private AppHook mAppHook;
        private bool mShowedProgress;

        private Stream? mFileStream = null;
        private Stream? mAdfStream = null;
        private IArchive? mAdfArchive = null;

        public CopyFileSource(object srcObj, IFileEntry srcEntry, FilePart part,
                bool srcIsMacZip, CopyFileWorker.CallbackFunc func, int progressPercent,
                AppHook appHook) :
            this(srcObj, srcEntry, part, srcIsMacZip, func, progressPercent, false, null,
                appHook) {
        }

        public CopyFileSource(object srcObj, IFileEntry srcEntry, FilePart part,
                bool srcIsMacZip, CopyFileWorker.CallbackFunc func, int progressPercent,
                FileAttribs adfAttrs, AppHook appHook) :
            this(srcObj, srcEntry, part, srcIsMacZip, func, progressPercent, true, adfAttrs,
                appHook) {
        }

        private CopyFileSource(object srcObj, IFileEntry srcEntry, FilePart part,
                bool srcIsMacZip, CopyFileWorker.CallbackFunc func, int progressPercent,
                bool dstIsADF, FileAttribs? adfAttrs, AppHook appHook) {
            mSrcObj = srcObj;
            mSrcEntry = srcEntry;
            mPart = part;
            mAdfAttrs = adfAttrs;
            mSrcIsMacZip = srcIsMacZip;
            mFunc = func;
            mProgressPercent = progressPercent;
            mDstIsADF = dstIsADF;
            mAppHook = appHook;
        }

        // IPartSource
        public void Open() {
            if (mFileStream != null) {
                throw new InvalidOperationException("Stream is already open");
            }
            if (mDstIsADF) {
                OpenADF();
                return;
            }

            if (!mShowedProgress) {
                CallbackFacts facts = new CallbackFacts(CallbackFacts.Reasons.Progress,
                    mSrcEntry.FullPathName, mSrcEntry.DirectorySeparatorChar);
                facts.ProgressPercent = mProgressPercent;
                facts.Part = mPart;
                mFunc(facts, null);
                mShowedProgress = true;     // don't show this again if we reopen
            }

            if (mSrcObj is IArchive) {
                IArchive srcArchive = (IArchive)mSrcObj;
                if (mSrcIsMacZip) {
                    // Copy AppleDouble archive to temp file, and open the resource fork.
                    mAdfStream = ArcTemp.ExtractToTemp(srcArchive, mSrcEntry, FilePart.DataFork);
                    mAdfArchive = AppleSingle.OpenArchive(mAdfStream, mAppHook);
                    mFileStream =
                        mAdfArchive.OpenPart(mAdfArchive.GetFirstEntry(), FilePart.RsrcFork);
                } else {
                    mFileStream = srcArchive.OpenPart(mSrcEntry, mPart);
                }
            } else {
                IFileSystem srcFs = (IFileSystem)mSrcObj;
                mFileStream = srcFs.OpenFile(mSrcEntry, FileAccessMode.ReadOnly, mPart);
            }
        }

        /// <summary>
        /// Generates an AppleDouble "header" file when the source is opened.
        /// </summary>
        private void OpenADF() {
            Debug.Assert(mAdfAttrs != null);

            using AppleSingle header = AppleSingle.CreateDouble(2, mAppHook);
            header.StartTransaction();
            IFileEntry hdrEntry = header.GetFirstEntry();
            hdrEntry.FileType = mAdfAttrs.FileType;
            hdrEntry.AuxType = mAdfAttrs.AuxType;
            hdrEntry.HFSFileType = mAdfAttrs.HFSFileType;
            hdrEntry.HFSCreator = mAdfAttrs.HFSCreator;
            hdrEntry.CreateWhen = mAdfAttrs.CreateWhen;
            hdrEntry.ModWhen = mAdfAttrs.ModWhen;
            hdrEntry.Access = mAdfAttrs.Access;
            //hdrEntry.Comment = mAdfAttrs.Comment;

            // Read the resource fork from whatever source was provided.
            if (mAdfAttrs.RsrcLength > 0) {
                CopyFileSource subSource = new CopyFileSource(mSrcObj, mSrcEntry, mPart,
                    mSrcIsMacZip, mFunc, mProgressPercent, false, mAdfAttrs, mAppHook);
                header.AddPart(hdrEntry, FilePart.RsrcFork, subSource, CompressionFormat.Default);
            }

            // Commit it to a stream; the AppleSingle instance will be discarded.  We assume
            // it will fit in memory (reasonable for a resource fork).
            header.CommitTransaction(mFileStream = new MemoryStream());
            mFileStream.Position = 0;
        }

        // IPartSource
        public int Read(byte[] buf, int offset, int count) {
            Debug.Assert(mFileStream != null);
            return mFileStream.Read(buf, offset, count);
        }

        // IPartSource
        public void Rewind() {
            Debug.Assert(mFileStream != null);
            if (mFileStream.CanSeek) {
                // IFileSystem source or MacZip ADF header.
                mFileStream.Position = 0;
            } else if (mAdfArchive != null) {
                // MacZip AppleDouble stream, reopen.
                Debug.Assert(mSrcObj is Zip);
                mFileStream.Dispose();
                mFileStream = mAdfArchive.OpenPart(mAdfArchive.GetFirstEntry(), FilePart.RsrcFork);
            } else {
                // IArchive un-seekable stream; close and re-open from archive.
                mFileStream.Dispose();
                mFileStream = null;
                Open();
            }
        }

        // IPartSource
        public void Close() {
            mFileStream?.Dispose();
            mFileStream = null;
            mAdfArchive?.Dispose();
            mAdfArchive = null;
            mAdfStream?.Dispose();
            mAdfStream = null;
        }

        ~CopyFileSource() {
            Dispose(false);     // cleanup check
        }
        public void Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        protected virtual void Dispose(bool disposing) {
            if (mDisposed) {
                Debug.Assert(!mDisposed, "Double dispose CopyFileSource (disposing=" +
                    disposing + ")");
                return;
            }
            mDisposed = true;

            if (disposing) {
                Close();
            } else {
                mAppHook.LogW("GC disposing CopyFileSource: " + mSrcEntry);
                Debug.Assert(false, "GC disposing CopyFileSource: " + mSrcEntry);
            }
        }

        public override string ToString() {
            return "[CopyFileSource: " + mSrcEntry + " srcZip=" + mSrcIsMacZip +
                " dstAdf=" + mDstIsADF + "]";
        }
    }
}
