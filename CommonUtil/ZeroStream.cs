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
using System.IO;

namespace CommonUtil {
    /// <summary>
    /// Simple "/dev/zero" stream that, when read, returns nothing but zeroes.  The stream can
    /// be given a length, and is seekable.
    /// </summary>
    public class ZeroStream : Stream {
        private long mLength;
        private long mPosition;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => mLength;

        public override long Position {
            get => mPosition;
            set => Seek(value, SeekOrigin.Begin);
        }

        /// <summary>
        /// Constructs an infinitely long stream.
        /// </summary>
        /// <remarks>
        /// Not actually infinite in the current implementation, but close enough.
        /// </remarks>
        public ZeroStream() : this(long.MaxValue) { }

        /// <summary>
        /// Constructs a stream with a specific length.
        /// </summary>
        /// <param name="length"></param>
        public ZeroStream(long length) {
            mLength = length;
            mPosition = 0;
        }

        public override int Read(byte[] buffer, int offset, int count) {
            if (offset > buffer.Length - count) {
                throw new ArgumentException("bad offset/count");
            }
            if (mPosition >= mLength) {
                return 0;
            }
            if (mPosition + count > mLength) {
                count = (int)(mLength - mPosition);
            }
            for (int i = offset; i < offset + count; i++) {
                buffer[i] = 0x00;
            }
            mPosition += count;
            return count;
        }

        public override void Write(byte[] buffer, int offset, int count) {
            throw new NotSupportedException("can't write to this stream");
        }

        public override void Flush() {
            // do nothing
        }

        public override long Seek(long offset, SeekOrigin origin) {
            long newPosition;
            switch (origin) {
                case SeekOrigin.Begin:
                    newPosition = offset;
                    break;
                case SeekOrigin.Current:
                    newPosition = mPosition + offset;
                    break;
                case SeekOrigin.End:
                    newPosition = mLength - offset;
                    break;
                default:
                    throw new ArgumentException("bad origin " + origin);
            }
            if (newPosition < 0) {
                throw new ArgumentException("invalid offset " + offset);
            }
            mPosition = newPosition;
            return newPosition;
        }

        public override void SetLength(long newLength) {
            if (newLength < 0) {
                throw new ArgumentException("invalid length " + newLength);
            }
            mLength = newLength;
            // it's okay if current position is past EOF
        }
    }
}
