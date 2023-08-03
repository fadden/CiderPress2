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
using System.IO;

namespace CommonUtil {
    /// <summary>
    /// Wrapper class for Streams that have a fixed length but are unable to report it.  The
    /// situation arises when a Windows disk device is opened.
    /// </summary>
    public class FixedLengthStream : Stream {
        private Stream mBaseStream;
        private long mLength;

        public FixedLengthStream(Stream baseStream, long length) {
            Debug.Assert(mLength >= 0);
            mBaseStream = baseStream;
            mLength = length;
        }

        public override bool CanRead => mBaseStream.CanRead;
        public override bool CanSeek => mBaseStream.CanSeek;
        public override bool CanWrite => mBaseStream.CanWrite;
        public override long Length => mLength;

        public override long Position {
            get => mBaseStream.Position;
            set => mBaseStream.Position = value;
        }

        public override void Flush() {
            mBaseStream.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count) {
            return mBaseStream.Read(buffer, offset, count);
        }

        public override long Seek(long offset, SeekOrigin origin) {
            return mBaseStream.Seek(offset, origin);
        }

        public override void SetLength(long value) {
            throw new NotImplementedException("can't set the length of this stream");
        }

        public override void Write(byte[] buffer, int offset, int count) {
            mBaseStream.Write(buffer, offset, count);
        }

        protected override void Dispose(bool disposing) {
            Debug.Assert(mLength >= 0);
            if (disposing) {
                mBaseStream.Dispose();
                mLength = -1;
            }
        }
    }
}
