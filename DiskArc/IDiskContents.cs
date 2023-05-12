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

namespace DiskArc {
    /// <summary>
    /// Common root for classes that represent the contents of a disk image, notably
    /// IMultiPart and IFileSystem.
    /// </summary>
    public interface IDiskContents { }

    // IMultiPart and IFileSystem are both IDisposable, so it's tempting to apply that here
    // so we can dispose of an IDiskContents without having to cast it to something else.
    // I'm resisting that for two reasons: (1) this interface is supposed to be one small step
    // above "object", with no functionality defined, and (2) as it's currently used, it's
    // made available through a property, which means the application shouldn't be trying to
    // dispose of it.  Omitting IDisposable makes that sort of error less likely.
}
