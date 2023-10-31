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
    /// IPartSource used by ClipPasteWorker.  Receives input from an IStream opened through
    /// a callback.
    /// </summary>
    public class ClipFileSource : IPartSource {
        private string mStorageName;
        private char mStorageDirSep;
        private ClipFileEntry mClipEntry;
        private FilePart mPart;
        private int mProgressPercent;
        private ClipPasteWorker.CallbackFunc mFunc;
        private ClipPasteWorker.ClipStreamGenerator mClipStreamGen;
        private AppHook mAppHook;

        private bool mDisposed;
        private Stream? mInStream = null;

        public ClipFileSource(string storageName, char dirSep, ClipFileEntry clipEntry,
                FilePart part, ClipPasteWorker.CallbackFunc func, int progressPercent,
                ClipPasteWorker.ClipStreamGenerator streamGen, AppHook appHook) {
            mStorageName = storageName;
            mStorageDirSep = dirSep;
            mPart = part;
            mFunc = func;
            mProgressPercent = progressPercent;
            mClipEntry = clipEntry;
            mClipStreamGen = streamGen;
            mAppHook = appHook;
        }

        // IPartSource
        public void Open() {
            if (mInStream != null) {
                throw new InvalidOperationException("Stream is already open");
            }

            CallbackFacts facts = new CallbackFacts(CallbackFacts.Reasons.QueryCancel);
            if (mFunc(facts) == CallbackFacts.Results.Cancel) {
                // Asynchronous cancel requested.
                throw new CancelException("Cancelled in AddFileSource");
            }

            // Configure progress updater with before/after filenames.
            facts = new CallbackFacts(CallbackFacts.Reasons.Progress);
            facts.OrigPathName = mClipEntry.Attribs.FullPathName;
            facts.OrigDirSep = mClipEntry.Attribs.FullPathSep;
            facts.NewPathName = mStorageName;
            facts.NewDirSep = mStorageDirSep;
            facts.Part = mPart;
            facts.ProgressPercent = mProgressPercent;
            mFunc(facts);

            // Open the stream.
            mInStream = mClipStreamGen(mClipEntry);
        }

        // IPartSource
        public int Read(byte[] buf, int offset, int count) {
            Debug.Assert(mInStream != null);
            return mInStream.Read(buf, offset, count);
        }

        // IPartSource
        public void Rewind() {
            Debug.Assert(mInStream != null && mInStream.CanSeek);
            mInStream.Position = 0;
        }

        // IPartSource
        public void Close() {
            mInStream?.Dispose();
            mInStream = null;
        }

        ~ClipFileSource() {
            Dispose(false);     // cleanup check
        }
        public void Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        protected virtual void Dispose(bool disposing) {
            if (mDisposed) {
                Debug.Assert(!mDisposed, "Double dispose ClipFileSource (disposing=" +
                    disposing + ")");
                return;
            }

            if (disposing) {
                Close();
            } else {
                mAppHook.LogW("GC disposing AddFileSource: " + mStorageName);
                Debug.Assert(false, "GC disposing AddFileSource: " + mStorageName);
            }
            mDisposed = true;
        }
    }
}
