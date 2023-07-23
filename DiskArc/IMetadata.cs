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
using System.Diagnostics;

namespace DiskArc {
    /// <summary>
    /// Metadata access interface, for formats like WOZ and 2IMG with editable metadata.
    /// </summary>
    public interface IMetadata {
        /// <summary>
        /// Metadata entry descriptor.  Immutable.
        /// </summary>
        class MetaEntry {
            public const string BOOL_DESC = "Value may be \"true\" or \"false\".";

            /// <summary>
            /// Entry key.
            /// </summary>
            public string Key { get; private set; }

            /// <summary>
            /// Type of data that the value holds.
            /// </summary>
            /// <remarks>
            /// This can be used for basic syntax checks or to alter the UI presentation (e.g.
            /// a checkbox instead of an input field).  We don't provide integer range limits
            /// or string validity patterns, so this isn't enough to actually validate the input.
            /// </remarks>
            public ValType ValueType { get; private set; }
            public enum ValType { Unknown = 0, String, Bool, Int };

            /// <summary>
            /// Human-readable description of the meaning of the key.  This will be empty for
            /// user-defined keys.
            /// </summary>
            public string Description { get; private set; }

            /// <summary>
            /// Syntax description for the value.  This will be empty for user-defined and
            /// non-editable keys.
            /// </summary>
            public string ValueSyntax { get; private set; }

            /// <summary>
            /// True if the entry can be edited.
            /// </summary>
            public bool CanEdit { get; private set; }

            /// <summary>
            /// True if the entry can be deleted.
            /// </summary>
            public bool CanDelete { get; private set; }

            /// <summary>
            /// Constructor used when creating a static table of "standard" entries.
            /// </summary>
            /// <param name="key">Entry key.</param>
            /// <param name="valueType">General type of value.</param>
            /// <param name="desc">Description of key.</param>
            /// <param name="syntax">Value syntax.</param>
            /// <param name="canEdit">True if this entry can be edited.</param>
            public MetaEntry(string key, ValType valueType, string desc, string syntax,
                    bool canEdit) {
                Key = key;
                ValueType = valueType;
                Description = desc;
                ValueSyntax = syntax;
                CanEdit = canEdit;
                CanDelete = false;
                Debug.Assert(canEdit ^ string.IsNullOrEmpty(syntax),
                    "syntax iff editable; key=" + key);
            }

            /// <summary>
            /// Constructor used when generating a list for the application.
            /// </summary>
            /// <param name="key">Entry key.</param>
            /// <param name="valueType">General type of value.</param>
            /// <param name="canEdit">True if this entry can be edited.</param>
            /// <param name="canDelete">True if this entry can be deleted.</param>
            public MetaEntry(string key, ValType valueType, string desc, string syntax,
                    bool canEdit, bool canDelete) {
                Key = key;
                ValueType = valueType;
                Description = desc;
                ValueSyntax = syntax;
                CanEdit = canEdit;
                CanDelete = canDelete;
            }

            /// <summary>
            /// Constructor used when generating an entry for a user-defined key.  These are
            /// always strings, and can be edited and deleted.
            /// </summary>
            /// <param name="key">Entry key.</param>
            public MetaEntry(string key) {
                Key = key;
                ValueType = ValType.String;
                Description = string.Empty;
                ValueSyntax = string.Empty;
                CanEdit = CanDelete = true;
            }
        }

        /// <summary>
        /// True if we're allowed to add new entries.
        /// </summary>
        bool CanAddNewEntries { get; }

        /// <summary>
        /// Generates a list of all metadata entries.
        /// </summary>
        /// <returns></returns>
        List<MetaEntry> GetMetaEntries();

        /// <summary>
        /// Retrieves the value for a single entry.
        /// </summary>
        /// <param name="key">Key of entry to get the value of.</param>
        /// <param name="verbose">If true, the value may have additional information.  As such,
        ///   it will not easily parse back to the original value.</param>
        /// <returns>Value in string form, or null if the entry wasn't found.</returns>
        string? GetMetaValue(string key, bool verbose);

        /// <summary>
        /// Tests whether the value would be accepted as valid for the key.  Entries that are
        /// not editable always return false.
        /// </summary>
        /// <param name="key">Key of entry to test.</param>
        /// <param name="value">Value to test, in string form.</param>
        /// <returns>True if the value would be accepted.</returns>
        bool TestMetaValue(string key, string value);

        /// <summary>
        /// Sets the value for one entry.
        /// </summary>
        /// <param name="key">Key of entry to set.</param>
        /// <param name="value">Value to set the entry to, in string form.  May be empty, but
        ///   may not be null.</param>
        /// <exception cref="ArgumentException">Invalid key or invalid value.</exception>
        /// <exception cref="InvalidOperationException">Object is read-only.</exception>
        void SetMetaValue(string key, string value);

        /// <summary>
        /// Deletes an entry.  This will fail if the entry doesn't exist, or if the entry cannot
        /// be deleted.
        /// </summary>
        /// <param name="key">Key of entry to delete.</param>
        /// <returns>True if the entry was deleted.</returns>
        bool DeleteMetaEntry(string key);
    }
}
