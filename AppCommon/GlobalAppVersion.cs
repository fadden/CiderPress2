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

using CommonUtil;

namespace AppCommon {
    /// <summary>
    /// This exists so we can have a single "global" version for all parts of the application.
    /// "cp2", "cp2_wpf", and whatever else we come up with can report the same version without
    /// needing to update it in multiple places.  This really ought to be a build property.
    /// </summary>
    public static class GlobalAppVersion {
        /// <summary>
        /// Overall application version.
        /// </summary>
        public static readonly CommonUtil.Version AppVersion =
            new CommonUtil.Version(1, 1, 1, CommonUtil.Version.PreRelType.Final, 0);
        // NOTE: there's an independent version in DiskArc/Defs.cs
    }
}
