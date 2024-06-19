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
using DiskArc;
using static DiskArc.Defs;

namespace AppCommon {
    /// <summary>
    /// An object that holds some data to pass to a callback when adding or extracting files.
    /// </summary>
    public class CallbackFacts {
        /// <summary>
        /// The reason for invoking the callback method.
        /// </summary>
        public enum Reasons {
            Unknown = 0,

            // Just a progress update.  Percent complete in ProgressPercent, storage name
            // of file being added in Message.
            Progress,

            // Query cancellation status.  This is useful for GUI interfaces with a "cancel"
            // button, allowing an operation to be interrupted with an async cancel request.
            // (Using this is easier than checking the result from Progress, because that's issued
            // in multiple deep places.)
            // Options: Continue, Cancel
            QueryCancel,

            // Warning: resource fork was ignored due to archive capabilities (e.g. add to ZIP).
            // Options: Continue, Cancel
            ResourceForkIgnored,

            // File with same name already exists.  Filename in Message.
            // Options: Overwrite, Skip, Rename, Cancel
            FileNameExists,

            // Partial pathname exceeded archive storage limits.
            // Options: Skip, Cancel   (Rename seems awkward)
            PathTooLong,

            // Failed to set all file attributes.
            // Options: Continue, Cancel
            AttrFailure,

            // Unable to overwrite existing file.
            // Options: Continue, Cancel
            OverwriteFailure,

            // An import or export conversion failed.
            // Options: Continue, Cancel
            ConversionFailure,

            // Operation has failed completely.  Reason in Message.
            // Options: Cancel
            Failure,
        }

        /// <summary>
        /// DOS text conversion mode.
        /// </summary>
        public enum DOSConvMode { Unknown = 0, None, FromDOS, ToDOS };

        /// <summary>
        /// Result reported by the callback.
        /// </summary>
        public enum Results {
            Unknown = 0,
            Continue,           // keep going
            Cancel,             // cancel remaining steps; abort entire operation if possible
            Skip,               // skip adding this entry
            Overwrite,          // overwrite existing entry with same name
            //Rename,
        }

        public Reasons Reason { get; set; }

        public string OrigPathName { get; set; } = string.Empty;
        public char OrigDirSep { get; set; } = IFileEntry.NO_DIR_SEP;
        public DateTime OrigModWhen { get; set; } = TimeStamp.NO_DATE;

        public string NewPathName { get; set; } = string.Empty;
        public char NewDirSep { get; set; } = IFileEntry.NO_DIR_SEP;
        public DateTime NewModWhen { get; set; } = TimeStamp.NO_DATE;

        public FilePart Part { get; set; } = FilePart.Unknown;

        public int ProgressPercent { get; set; } = -1;

        public DOSConvMode DOSConv { get; set; } = DOSConvMode.Unknown;

        public string ConvTag { get; set; } = string.Empty;

        public string FailMessage { get; set; } = string.Empty;


        public CallbackFacts(Reasons reason) {
            Reason = reason;
        }

        public CallbackFacts(Reasons reason, string origName, char origDirSep) {
            Reason = reason;
            OrigPathName = origName;
            OrigDirSep = origDirSep;
        }

        public override string ToString() {
            return "[CbF: reason=" + Reason + " origPath=" + OrigPathName + "]";
        }
    }
}
