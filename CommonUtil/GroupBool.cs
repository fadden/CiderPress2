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

namespace CommonUtil {
    /// <summary>
    /// A simple boolean flag property that can be shared among a group of objects.
    /// Not intended for multi-threading.
    /// </summary>
    /// <remarks>
    /// <para>For example, all members of a group of CircularBitBuffers can share a single
    /// instance.  If one of them is modified, a flag is set that essentially marks all of
    /// them as dirty, and can be cleared with a single update.  The flag object should be
    /// created by the object that holds the collection.</para>
    /// </remarks>
    public sealed class GroupBool {
        public bool IsSet { get; set; }
    }
}
