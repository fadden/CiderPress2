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

        public const string VIEW_SETTING_PREFIX = "view-conv-";
        public const string IMPORT_SETTING_PREFIX = "import-conv-";
        public const string EXPORT_SETTING_PREFIX = "export-conv-";

        // Main window position and panel sizing.
        public const string MAIN_WINDOW_PLACEMENT = "main-window-placement";
        public const string MAIN_LEFT_PANEL_WIDTH = "main-left-panel-width";
        public const string MAIN_RIGHT_PANEL_VISIBLE = "main-right-panel-visible";
        public const string MAIN_WORK_TREE_HEIGHT = "main-work-tree-height";
        public const string MAIN_FILE_COL_WIDTHS = "main-file-col-widths";

        public const string FILE_LIST_PREFER_SINGLE = "file-list-prefer-single";

        public const string RECENT_FILES_LIST = "recent-files-list";
        public const string LAST_ADD_DIR = "last-add-dir";
        public const string LAST_EXTRACT_DIR = "last-extract-dir";

        // File handling features.
        public const string MAC_ZIP_ENABLED = "mac-zip-enabled";
        public const string AUTO_OPEN_DEPTH = "auto-open-depth";

        // Add/extract/import/export.
        public const string ADD_RECURSE_ENABLED = "add-recurse-enabled";
        public const string ADD_COMPRESS_ENABLED = "add-compress-enabled";
        public const string ADD_STRIP_PATHS_ENABLED = "add-strip-paths-enabled";
        public const string ADD_STRIP_EXT_ENABLED = "add-strip-ext-enabled";
        public const string ADD_RAW_ENABLED = "add-raw-enabled";
        public const string ADD_PRESERVE_ADF = "add-preserve-adf";
        public const string ADD_PRESERVE_AS = "add-preserve-as";
        public const string ADD_PRESERVE_NAPS = "add-preserve-naps";

        public const string EXT_RAW_ENABLED = "ext-raw-enabled";
        public const string EXT_STRIP_PATHS_ENABLED = "ext-strip-paths-enabled";
        public const string EXT_PRESERVE_MODE = "ext-preserve-mode";

        public const string CONV_IMPORT_TAG = "conv-import-tag";
        public const string CONV_EXPORT_TAG = "conv-export-tag";
        public const string CONV_EXPORT_BEST = "conv-export-best";

        // File viewer.
        public const string VIEW_RAW_ENABLED = "view-raw-enabled";

        // New archive/disk.
        public const string NEW_ARC_MODE = "new-arc-mode";
        public const string NEW_DISK_SIZE = "new-disk-size";
        public const string NEW_DISK_FILESYSTEM = "new-disk-filesystem";
        public const string NEW_DISK_FILE_TYPE = "new-disk-file-type";
        public const string NEW_DISK_CUSTOM_SIZE = "new-disk-custom-size";
        public const string NEW_DISK_VOLUME_NAME = "new-disk-volume-name";
        public const string NEW_DISK_VOLUME_NUM = "new-disk-volume-num";
        public const string NEW_DISK_MAKE_BOOTABLE = "new-disk-make-bootable";

        public const string SAVE_DISK_FILE_TYPE = "save-disk-file-type";

        // Physical disks.
        public const string PHYS_OPEN_READ_ONLY = "phys-open-read-only";

        // Sector editing.
        public const string SCTED_TEXT_CONV_MODE = "scted-text-conv-mode";

        // Debug.
        public const string DEBUG_MENU_ENABLED = "debug-menu-enabled";
    }
}
