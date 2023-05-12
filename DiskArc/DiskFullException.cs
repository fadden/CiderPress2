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
    /// Exception thrown when the disk is full, or when a fixed-size volume directory has
    /// run out of space.
    /// </summary>
    /// <remarks>
    /// This is a subclass of IOException, so that it can be handled generally with other
    /// I/O issues.
    /// </remarks>
    public class DiskFullException : IOException {
        public DiskFullException() { }
        public DiskFullException(string message) : base(message) { }
        public DiskFullException(string message, Exception inner) : base(message, inner) { }
    }
}
