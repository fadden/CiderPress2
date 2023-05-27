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
        /// Name of file that holds our settings.  This lives in the same directory as the
        /// executable.
        /// </summary>
        public const string SETTINGS_FILE_NAME = "CiderPress2-settings.json";

        /// <summary>
        /// Global application settings object.
        /// </summary>
        public static SettingsHolder Global { get; private set; } = new SettingsHolder();

        //
        // Setting name constants.
        //

        // Main window position and panel sizing.
        public const string MAIN_WINDOW_PLACEMENT = "main-window-placement";
        public const string MAIN_LEFT_PANEL_WIDTH = "main-left-panel-width";
        public const string MAIN_RIGHT_PANEL_VISIBLE = "main-right-panel-visible";
        public const string MAIN_WORK_TREE_HEIGHT = "main-work-tree-height";

        public const string RECENT_FILES_LIST = "recent-files-list";
        public const string LAST_EXTRACT_DIR = "last-extract-dir";

        // File handling features.
        public const string MAC_ZIP_ENABLED = "mac-zip-enabled";
        public const string AUTO_OPEN_DEPTH = "auto-open-depth";

        public const string DEBUG_MENU_ENABLED = "debug-menu-enabled";
    }
}
