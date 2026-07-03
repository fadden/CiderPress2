/*
 * Copyright 2023 faddenSoft
 * Copyright 2026 Lydian Scale Software
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

namespace cp2_avalonia.Services {
    internal static class AppSettings {
        /// <summary>
        /// Name of file that holds our settings.  This lives in the same directory as the
        /// executable.
        /// </summary>
        public const string SETTINGS_FILE_NAME = "CiderPress2-settings.json";

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
        public const string ASCII_CHART_WINDOW_WIDTH = "ascii-chart-window-width";
        public const string ASCII_CHART_WINDOW_HEIGHT = "ascii-chart-window-height";

        public const string FILE_LIST_PREFER_SINGLE = "file-list-prefer-single";

        public const string RECENT_FILES_LIST = "recent-files-list";
        public const string LAST_ADD_DIR = "last-add-dir";
        public const string LAST_EXTRACT_DIR = "last-extract-dir";

        // File handling features.
        public const string MAC_ZIP_ENABLED = "mac-zip-enabled";
        public const string DOS_TEXT_CONV_ENABLED = "dos-text-conv-enabled";
        public const string AUTO_OPEN_DEPTH = "auto-open-depth";
        public const string AUDIO_DECODE_ALG = "audio-decode-alg";

        // Add/extract/import/export.  NOTE: update PublishSideOptions().
        public const string DDCP_ADD_EXTRACT = "ddcp-add-extract";

        public const string ADD_RECURSE_ENABLED = "add-recurse-enabled";
        public const string ADD_COMPRESS_ENABLED = "add-compress-enabled";
        public const string ADD_STRIP_PATHS_ENABLED = "add-strip-paths-enabled";
        public const string ADD_STRIP_EXT_ENABLED = "add-strip-ext-enabled";
        public const string ADD_RAW_ENABLED = "add-raw-enabled";
        public const string ADD_PRESERVE_ADF = "add-preserve-adf";
        public const string ADD_PRESERVE_AS = "add-preserve-as";
        public const string ADD_PRESERVE_NAPS = "add-preserve-naps";

        public const string EXT_ADD_EXPORT_EXT = "ext-add-export-ext";
        public const string EXT_RAW_ENABLED = "ext-raw-enabled";
        public const string EXT_STRIP_PATHS_ENABLED = "ext-strip-paths-enabled";
        public const string EXT_PRESERVE_MODE = "ext-preserve-mode";

        public const string CONV_IMPORT_TAG = "conv-import-tag";
        public const string CONV_EXPORT_TAG = "conv-export-tag";
        public const string CONV_EXPORT_BEST = "conv-export-best";

        // File viewer.
        public const string VIEW_RAW_ENABLED = "view-raw-enabled";

        // File viewer dialog suppression.
        public const string VIEWER_SUPPRESS_UNAVAILABLE_CLOSE = "viewer-suppress-unavailable-close";
        public const string VIEWER_SUPPRESS_UNAVAILABLE_REVERT = "viewer-suppress-unavailable-revert";
        public const string VIEWER_SUPPRESS_SECTOR_EDIT_WARNING = "viewer-suppress-sector-edit-warning";

        // New archive/disk.
        public const string NEW_ARC_MODE = "new-arc-mode";
        public const string NEW_DISK_SIZE = "new-disk-size";
        public const string NEW_DISK_FILESYSTEM = "new-disk-filesystem";
        public const string NEW_DISK_FILE_TYPE = "new-disk-file-type";
        public const string NEW_DISK_CUSTOM_SIZE = "new-disk-custom-size";
        public const string NEW_DISK_VOLUME_NAME = "new-disk-volume-name";
        public const string NEW_DISK_VOLUME_NUM = "new-disk-volume-num";
        public const string NEW_DISK_RESERVE_BOOT = "new-disk-reserve-boot";

        public const string SAVE_DISK_FILE_TYPE = "save-disk-file-type";

        // Physical disks.
        public const string PHYS_OPEN_READ_ONLY = "phys-open-read-only";

        // Theme.
        public const string THEME_MODE = "theme-mode";

        // Sector editing.
        public const string SCTED_TEXT_CONV_MODE = "scted-text-conv-mode";

        // Debug.
        public const string DEBUG_MENU_ENABLED = "debug-menu-enabled";
        public const string DEBUG_LOG_MIN_PRIORITY = "debug-log-min-priority";
    }
}
