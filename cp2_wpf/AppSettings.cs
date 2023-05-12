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

using CommonUtil;

namespace cp2_wpf {
    internal static class AppSettings {
        /// <summary>
        /// Global application settings object.
        /// </summary>
        public static SettingsHolder Global { get; private set; } = new SettingsHolder();

        //
        // Setting name constants.
        //

        // If set, the GUI app will scan the contents of archives and/or filesystems.
        public const string DEEP_SCAN_ARCHIVES = "deep-scan-archives";
        public const string DEEP_SCAN_FILESYSTEMS = "deep-scan-filesystems";
    }
}
