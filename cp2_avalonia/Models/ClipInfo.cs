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
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;

using AppCommon;

using cp2_avalonia.Services;

namespace cp2_avalonia.Models {
    /// <summary>
    /// Clipboard data model for CiderPress II file entries.  Cross-platform version that
    /// uses JSON text instead of Windows-specific binary clipboard formats.
    /// </summary>
    [Serializable]
    public class ClipInfo {
        private const string CLIP_PREFIX = "CiderPressII:clip:v1:";

        /// <summary>
        /// Custom drag-drop format name used to carry CP2 JSON between two CP2 instances.
        /// Must be a reverse-DNS UTI so that macOS NSPasteboardItem accepts it; Avalonia's
        /// DataObject treats it as an opaque key on all platforms.
        /// </summary>
        public const string CROSS_INSTANCE_FORMAT = "com.faddensoft.ciderpress2.clip";

        /// <summary>
        /// List of ClipFileEntry objects.  We receive one of these for every fork of every
        /// file on the clipboard.
        /// </summary>
        public List<ClipFileEntry>? ClipEntries { get; set; } 

        /// <summary>
        /// True if this was created by an "export" operation.
        /// </summary>
        public bool IsExport { get; set; }

        /// <summary>
        /// True if this was created by a Cut operation.  For same-process pastes, the source
        /// files are deleted after a successful paste.  Cross-process Cut behaves like Copy
        /// (no automatic source deletion).
        /// </summary>
        public bool IsCut { get; set; }

        /// <summary>
        /// True when the source operation was configured to strip directory paths.
        /// This preserves the sender's drag/copy intent for cross-instance paste.
        /// </summary>
        public bool StripPaths { get; set; }

        // Fields split out of a CommonUtil.Version object.
        public int AppVersionMajor { get; set; }
        public int AppVersionMinor { get; set; }
        public int AppVersionPatch { get; set; }

        public int ProcessId { get; set; }

        /// <summary>
        /// Nullary constructor, for the deserializer.
        /// </summary>
        public ClipInfo() { }

        /// <summary>
        /// Constructor.  Most of what we care about is in the ClipFileEntry list, but we want
        /// to add some application-level stuff like the version number.
        /// </summary>
        public ClipInfo(List<ClipFileEntry> clipEntries, CommonUtil.Version appVersion) {
            ClipEntries = clipEntries;
            AppVersionMajor = appVersion.Major;
            AppVersionMinor = appVersion.Minor;
            AppVersionPatch = appVersion.Patch;
            ProcessId = Environment.ProcessId;
        }

        /// <summary>
        /// Serializes this ClipInfo to a clipboard-ready string with a recognizable prefix.
        /// </summary>
        public string ToClipString() {
            return CLIP_PREFIX + JsonSerializer.Serialize(this);
        }

        /// <summary>
        /// Attempts to deserialize a ClipInfo from clipboard text.  Returns null if the text
        /// is not a CiderPress II clipboard string or cannot be parsed.
        /// </summary>
        public static ClipInfo? FromClipString(string? text) {
            if (text == null || !text.StartsWith(CLIP_PREFIX)) {
                return null;
            }
            try {
                ClipInfo? result = JsonSerializer.Deserialize<ClipInfo>(
                    text[CLIP_PREFIX.Length..]);
                if (result?.ClipEntries == null) {
                    AppLog.W("Clipboard data arrived without entries");
                    return null;
                }
                return result;
            } catch (JsonException ex) {
                AppLog.W("Clipboard deserialization failed", ex);
                return null;
            }
        }

        /// <summary>
        /// Returns true if the text looks like CiderPress II clipboard content.
        /// </summary>
        public static bool IsClipTextFromCP2(string? clipText) {
            return clipText != null && clipText.StartsWith(CLIP_PREFIX);
        }
    }
}
