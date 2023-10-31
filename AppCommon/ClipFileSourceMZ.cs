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
    /// Variation of ClipFileSource, used for MacOSZip mode.  We need to generate an AppleDouble
    /// "header" file on the fly.
    /// </summary>
    public class ClipFileSourceMZ : IPartSource {
        private string mAdjPath;
        private char mDirSep;
        private ClipFileEntry mClipEntry;
        private int mProgressPercent;
        private ClipPasteWorker.CallbackFunc mFunc;
        private ClipPasteWorker.ClipStreamGenerator mClipStreamGen;
        private AppHook mAppHook;

        private bool mDisposed;
        private MemoryStream? mHeaderStream;


        public ClipFileSourceMZ(string adjPath, char dirSep, ClipFileEntry clipEntry,
                ClipPasteWorker.CallbackFunc func, int progressPercent,
                ClipPasteWorker.ClipStreamGenerator streamGen, AppHook appHook) {
            mAdjPath = adjPath;
            mDirSep = dirSep;
            mClipEntry = clipEntry;
            mProgressPercent = progressPercent;
            mFunc = func;
            mClipStreamGen = streamGen;
            mAppHook = appHook;
        }

        // IPartSource
        public void Open() {
            // Generate the AppleDouble "header" file.
            using AppleSingle header = AppleSingle.CreateDouble(2, mAppHook);
            header.StartTransaction();
            IFileEntry hdrEntry = header.GetFirstEntry();
            hdrEntry.FileType = mClipEntry.Attribs.FileType;
            hdrEntry.AuxType = mClipEntry.Attribs.AuxType;
            hdrEntry.HFSFileType = mClipEntry.Attribs.HFSFileType;
            hdrEntry.HFSCreator = mClipEntry.Attribs.HFSCreator;
            hdrEntry.CreateWhen = mClipEntry.Attribs.CreateWhen;
            hdrEntry.ModWhen = mClipEntry.Attribs.ModWhen;
            hdrEntry.Access = mClipEntry.Attribs.Access;

            // Read the resource fork from whatever source was provided.
            if (mClipEntry.Part == FilePart.RsrcFork) {
                // We don't have the adjusted output path handy.
                ClipFileSource subSource = new ClipFileSource(mAdjPath, mDirSep, mClipEntry,
                    mClipEntry.Part, mFunc, mProgressPercent, mClipStreamGen, mAppHook);
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

        ~ClipFileSourceMZ() {
            Dispose(false);
        }
        public void Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        protected virtual void Dispose(bool disposing) {
            if (mDisposed) {
                Debug.Assert(!mDisposed, "Double dispose ClipFileSourceMZ (disposing=" +
                    disposing + ")");
                return;
            }

            if (disposing) {
                Close();
            } else {
                mAppHook.LogW("GC disposing ClipFileSourceMZ: " + mClipEntry);
                Debug.Assert(false, "GC disposing ClipFileSourceMZ: " + mClipEntry);
            }
            mDisposed = true;
        }
    }
}
