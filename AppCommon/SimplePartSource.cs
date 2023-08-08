/*
 * Copyright 2022 faddenSoft
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

using DiskArc;

namespace AppCommon {
    /// <summary>
    /// IPartSource implementation for a stream that is currently open.  The provided stream will
    /// be disposed when this object is disposed, unless specifically requested.
    /// </summary>
    /// <remarks>
    /// <para>This is not recommended for general use with filesystem streams, because doing
    /// so requires tying up a potentially large number of operating system open-file resources
    /// for the full extent of an operation.</para>
    /// </remarks>
    public class SimplePartSource : IPartSource {
        private Stream mStream;
        private bool mDisposed;
        private bool mLeaveOpen;

        public SimplePartSource(Stream stream) {
            mStream = stream;
            mLeaveOpen = false;
        }

        public SimplePartSource(Stream stream, bool leaveOpen) {
            mStream = stream;
            mLeaveOpen = leaveOpen;
        }

        public void Open() {
            mStream.Position = 0;
        }

        public int Read(byte[] buf, int offset, int count) {
            return mStream.Read(buf, offset, count);
        }

        public void Rewind() {
            mStream.Position = 0;
        }

        public void Close() {
            // We have no way to re-open the stream, so we can't close it.
        }

        //
        // This class is used extensively by the test code, so we want to be very rigorous about
        // proper cleanup.  In practice, failing to explicitly dispose of this object just means
        // that a read-only Stream doesn't get cleaned up until the next finalization pass.
        //
        // Double-dispose is a bit more risky, since it means we might have been disposed before
        // we were used.  It could also mean that the same source was used for multiple parts,
        // which won't actually break for a single archive because everything is disposed at once,
        // but could cause other problems because the stream position will be off.
        //

        ~SimplePartSource() {
            Dispose(false);
        }
        public void Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        protected virtual void Dispose(bool disposing) {
            if (mDisposed) {
                Debug.Assert(!mDisposed, "Double dispose SimplePartSource (disposing=" +
                    disposing + "), created:\r\n" + mCreationStackTrace);
                return;
            }

            if (disposing) {
                if (!mLeaveOpen) {
                    mStream.Dispose();
                }
            } else {
                // Only get cranky if we're leaving a Stream open.
                if (!mLeaveOpen) {
                    Debug.Assert(false, "GC disposing SimplePartSource, created:\r\n" +
                        mCreationStackTrace);
                }
            }
            mDisposed = true;
        }
#if DEBUG
        private string mCreationStackTrace = Environment.StackTrace + Environment.NewLine + "---";
#else
        private string mCreationStackTrace = "  (stack trace not enabled)";
#endif
    }
}
