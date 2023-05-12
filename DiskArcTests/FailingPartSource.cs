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
using System.IO;

using DiskArc;

namespace DiskArcTests {
    /// <summary>
    /// An IPartSource that throws a FakeException after a certain amount of data.  Used to
    /// evaluate error handling and recovery.  Behaves like a simple data source until it
    /// fails to satisfy a read request.
    /// </summary>
    public class FailingPartSource : IPartSource {
        private Stream mStream;

        public FailingPartSource(Stream stream) {
            mStream = stream;
        }

        public void Open() {
            mStream.Position = 0;
        }

        public int Read(byte[] buf, int offset, int count) {
            int actual = mStream.Read(buf, offset, count);
            if (actual == 0) {
                throw new FakeException("Synthetic test failure");
            }
            return actual;
        }

        public void Rewind() {
            mStream.Position = 0;
        }

        public void Close() { }


        // Cleanup check.

        ~FailingPartSource() {
            Dispose(false);
        }
        public void Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        protected virtual void Dispose(bool disposing) {
            if (disposing) {
                mStream.Dispose();
            } else {
                Debug.Assert(false, "GC disposing FailingPartSource");
            }
        }
    }

    public class FakeException : Exception {
        public FakeException() { }
        public FakeException(string message) : base(message) { }
        public FakeException(string message, Exception inner) : base(message, inner) { }
    }
}
