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

namespace DiskArc {
    /// <summary>
    /// Additional internal-only interfaces.
    /// </summary>
    internal interface IArchiveExt : IArchive {
        /// <summary>
        /// Data stream with the file archive data.  Will be null for a new archive.
        /// </summary>
        /// <remarks>
        /// This isn't meant to be public, but since the stream was passed in to us by the
        /// application we're not really exposing anything here.
        /// </remarks>
        Stream? DataStream { get; }

        /// <summary>
        /// Notifies the archive object that an entry has been closed.
        /// </summary>
        /// <remarks>
        /// This is only called when the stream is explicitly disposed, with Close() or
        /// Dispose().
        /// </remarks>
        /// <param name="stream">Archive entry stream.</param>
        void StreamClosing(ArcReadStream stream);
    }
}
