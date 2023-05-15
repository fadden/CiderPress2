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
using System.Collections;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

using CommonUtil;
using static DiskArc.IMetadata;

namespace DiskArc.Disk {
    /// <summary>
    /// WOZ file META chunk management.
    /// </summary>
    /// <remarks>
    /// <para>The META chunk is the same in WOZ v1 and v2.  Duplicate keys are not allowed.  The
    /// order in which entries are stored doesn't matter.</para>
    /// <para>The enumeration provides the keys for all entries.</para>
    /// </remarks>
    public class Woz_Meta {
        public const char NV_SEP_CHAR = '\t';           // tab (0x09)
        public const char ENTRY_SEP_CHAR = '\n';        // linefeed (0x0a)

        /// <summary>
        /// General key syntax validation.
        /// </summary>
        /// <remarks>
        /// <para>The specification says almost nothing about valid key syntax.  To keep things
        /// simple we limit it to A-Z, a-z, 0-9, and '_'.  All standard keys much match.</para>
        /// </remarks>
        public static readonly Regex VALID_KEY_REGEX = new Regex(VALID_KEY_PATTERN);
        private const string VALID_KEY_PATTERN = @"^\w+$";

        /// <summary>
        /// General value syntax validation.
        /// </summary>
        /// <remarks>
        /// <para>The 2.1 specification says, "values cannot contain pipe, linefeed, or tab
        /// characters". It then shows a couple of standard entries with pipe characters used
        /// to separate list elements.  It's unclear what this means; one interpretation is
        /// that pipe characters are only valid for certain standard keys.</para>
        /// </remarks>
        public static readonly Regex VALID_VALUE_REGEX = new Regex(VALID_VALUE_PATTERN);
        private const string VALID_VALUE_PATTERN = @"^[^\t\n]*$";

        /// <summary>
        /// Validate value of "side" entry.
        /// </summary>
        /// <remarks>
        /// <para>This is really just a disk number and 'A' or 'B' for the side.  It's formed in
        /// a human-readable fashion, which makes parsing and validation a little harder.  We
        /// currently treat this as case-sensitive, so "DISK 5, side b" would fail.</para>
        /// </remarks>
        public static readonly Regex VALID_SIDE_REGEX = new Regex(VALID_SIDE_PATTERN);
        private const string VALID_SIDE_PATTERN = @"^Disk (\d+), Side ([AB])$";
        public const int SIDE_GROUP_DISK = 1;
        public const int SIDE_GROUP_SIDE = 2;

        //
        // Data tables and constants.
        //

        // Keys that require special handling.
        public const string LANGUAGE_KEY = "language";
        public const string REQUIRES_RAM_KEY = "requires_ram";
        public const string REQUIRES_MACHINE_KEY = "requires_machine";
        public const string SIDE_KEY = "side";
        public const string IMAGE_DATE_KEY = "image_date";

        /// <summary>
        /// Standard keys, defined by the WOZ specification.  All WOZ files with a META chunk
        /// are expected to have these.  They don't have to appear in a specific order.  Some
        /// fields have a restricted set of values or a rigidly defined format.
        /// </summary>
        internal static readonly MetaEntry[] sStandardEntries = new MetaEntry[] {
            new MetaEntry("title", MetaEntry.ValType.String, canEdit:true),
            new MetaEntry("subtitle", MetaEntry.ValType.String, canEdit:true),
            new MetaEntry("publisher", MetaEntry.ValType.String, canEdit:true),
            new MetaEntry("developer", MetaEntry.ValType.String, canEdit:true),
            new MetaEntry("copyright", MetaEntry.ValType.String, canEdit:true),
            new MetaEntry("version", MetaEntry.ValType.String, canEdit:true),
            new MetaEntry(LANGUAGE_KEY, MetaEntry.ValType.String, canEdit:true),
            new MetaEntry(REQUIRES_RAM_KEY, MetaEntry.ValType.String, canEdit:true),
            new MetaEntry(REQUIRES_MACHINE_KEY, MetaEntry.ValType.String, canEdit:true),
            new MetaEntry("notes", MetaEntry.ValType.String, canEdit:true),
            new MetaEntry(SIDE_KEY, MetaEntry.ValType.String, canEdit:true),
            new MetaEntry("side_name", MetaEntry.ValType.String, canEdit:true),
            new MetaEntry("contributor", MetaEntry.ValType.String, canEdit:true),
            new MetaEntry(IMAGE_DATE_KEY, MetaEntry.ValType.String, canEdit:true),
        };

        // Valid values for specific keys.
        private static readonly string[] sLanguage = new string[] {
            "English", "Spanish", "French", "German",
            "Chinese", "Japanese", "Italian", "Dutch",
            "Portuguese", "Danish", "Finnish", "Norweigian",
            "Swedish", "Russian", "Polish", "Turkish",
            "Arabic", "Thai", "Czech", "Hungarian",
            "Catalan", "Croatian", "Greek", "Hebrew",
            "Romanian", "Slovak", "Ukrainian", "Indonesian",
            "Malay", "Vietnamese", "Other"
        };
        private static readonly string[] sRequiresRam = new string[] {
            "16K", "24K", "32K", "48K",
            "64K", "128K", "256K", "512K",
            "768K", "1M", "1.25M", "1.5M+",
            "Unknown"
        };
        private static readonly string[] sRequiresMachine = new string[] {
            "2", "2+", "2e", "2c",
            "2e+", "2gs", "2c+", "3",
            "3+",
        };

        /// <summary>
        /// True if modifications should be blocked.
        /// </summary>
        public bool IsReadOnly { get; internal set; }

        /// <summary>
        /// Set to true if newly created or if an entry has been altered.
        /// </summary>
        public bool IsDirty { get; set; }

        /// <summary>
        /// List of entries.
        /// </summary>
        private Dictionary<string, string> mEntries = new Dictionary<string, string>();


        /// <summary>
        /// Private constructor.
        /// </summary>
        private Woz_Meta() { }

        /// <summary>
        /// Creates a metadata object for a new disk image.
        /// </summary>
        public static Woz_Meta CreateMETA() {
            Woz_Meta metadata = new Woz_Meta();
            // Create standard entries.  Leave them all blank except for image_date.
            foreach (MetaEntry met in sStandardEntries) {
                metadata.mEntries.Add(met.Key, string.Empty);
            }
            metadata.mEntries[IMAGE_DATE_KEY] =
                XmlConvert.ToString(DateTime.Now, XmlDateTimeSerializationMode.Utc);
            metadata.IsDirty = true;
            return metadata;
        }

        /// <summary>
        /// Creates a new metadata object from a META chunk.
        /// </summary>
        /// <remarks>
        /// <para>Blatantly bad META entries will be discarded.  If the UTF-8 encoding is broken,
        /// the entire set may be discarded.</para>
        /// </remarks>
        public static Woz_Meta? ParseMETA(byte[] data, int offset, int length, AppHook appHook) {
            Woz_Meta metadata = new Woz_Meta();
            if (!metadata.Parse(data, offset, length, appHook)) {
                return null;
            }
            return metadata;
        }

        /// <summary>
        /// Makes a copy of the key/value dictionary.
        /// </summary>
        internal Dictionary<string, string> GetEntryDict() {
            Dictionary<string, string> newDict = new Dictionary<string, string>(mEntries.Count);
            foreach (KeyValuePair<string, string> entry in mEntries) {
                newDict.Add(entry.Key, entry.Value);
            }
            return newDict;
        }

        /// <summary>
        /// Retrieves the value for the specified key.
        /// </summary>
        /// <param name="key">Key of entry to get the value of.</param>
        /// <returns>Value, or null if the key was not found.</returns>
        public string? GetValue(string key) {
            if (mEntries.TryGetValue(key, out string? value)) {
                Debug.Assert(value != null);
                return value;
            } else {
                return null;
            }
        }

        /// <summary>
        /// Sets the specified entry.  If no entry for the key exists, a new entry will be added.
        /// </summary>
        /// <param name="key">Key of entry to update.</param>
        /// <param name="value">New value.</param>
        /// <exception cref="ArgumentException">Invalid value for standard key, key is null, or
        ///   value is null.</exception>
        /// <exception cref="InvalidOperationException">Disk image is read-only.</exception>
        public void SetValue(string key, string value) {
            if (key == null || value == null) {
                throw new ArgumentNullException("Key and value must not be null");
            }
            if (IsReadOnly) {
                throw new InvalidOperationException("Image is read-only");
            }

            // Validate the key.  I can't figure out how to make Regex not ignore an end-of-line
            // linefeed, so we explicitly confirm that the captured group matches the original.
            MatchCollection matches = VALID_KEY_REGEX.Matches(key);
            if (matches.Count != 1 || key != matches[0].Groups[0].Value) {
                throw new ArgumentException("Invalid key: '" + key + "'");
            }
            // Empty values are always allowed, even for the special keys.
            if (value == string.Empty) {
                mEntries[key] = value;
                IsDirty = true;
                return;
            }
            // Test the general form of the value string.  Only tabs and linefeeds are banned.
            matches = VALID_VALUE_REGEX.Matches(value);
            if (matches.Count != 1 || value != matches[0].Groups[0].Value) {
                throw new ArgumentException("Invalid characters in value: '" + value + "'");
            }

            // Perform special value validation for certain standard keys.
            switch (key) {
                case LANGUAGE_KEY:
                    // Confirm value is a known language.
                    if (!IsInList(value, sLanguage)) {
                        throw new ArgumentException("Invalid language: '" + value + "'");
                    }
                    break;
                case REQUIRES_RAM_KEY:
                    // Confirm value is a valid RAM configuration.
                    if (!IsInList(value, sRequiresRam)) {
                        throw new ArgumentException("Invalid requires_ram: '" + value + "'");
                    }
                    break;
                case REQUIRES_MACHINE_KEY:
                    // Confirm value is a pipe-separated list of machines.
                    string[] list = value.Split('|');
                    foreach (string listItem in list) {
                        // This returns false for empty strings, so we should catch minor
                        // syntactical issues like "2+|2e|" and "||2gs".
                        if (!IsInList(listItem, sRequiresMachine)) {
                            throw new ArgumentException("Invalid requires_machine list item: '" +
                                listItem + "'");
                        }
                    }
                    break;
                case SIDE_KEY:
                    // Entry has form "Disk #, Side [A|B]", e.g. "Disk 1, Side A".
                    matches = VALID_SIDE_REGEX.Matches(value);
                    if (matches.Count != 1) {
                        throw new ArgumentException("Invalid side value: '" + value + "'");
                    }
                    break;
                case IMAGE_DATE_KEY:
                    // Confirm value is an RFC3339 date.
                    // https://stackoverflow.com/a/91146/294248 says we can use XmlConvert.  The
                    // class documentation doesn't really say much, but it seems to work.
                    try {
                        DateTime test = XmlConvert.ToDateTime(value,
                            XmlDateTimeSerializationMode.Utc);
                    } catch (FormatException) {
                        throw new ArgumentException("Bad date format: '" + value + "'");
                    }
                    break;
                default:
                    // Standard or user-defined key with no restrictions on value, except maybe
                    // that pipe characters are bad.
                    if (value.Contains('|')) {
                        throw new ArgumentException("Pipe character not allowed for this key");
                    }
                    break;
            }

            // All good.  Add or update the key.
            mEntries[key] = value;
            IsDirty = true;
        }

        /// <summary>
        /// Deletes the specified entry from the metadata set.
        /// </summary>
        /// <param name="key">Key of entry to remove.</param>
        /// <returns>True on success, false if the key wasn't found or cannot be removed.</returns>
        public bool DeleteEntry(string key) {
            // Don't validate key name -- we want to be able to delete bad keys.
            if (IsStandardKey(key)) {
                return false;
            }
            bool didRemove = mEntries.Remove(key);
            if (didRemove) {
                IsDirty = true;
            }
            return didRemove;
        }

        /// <summary>
        /// Determines whether a string is in the standard key set.  Case-sensitive.
        /// </summary>
        /// <param name="str">String to search for.</param>
        /// <returns>True if the value was found, false if not.</returns>
        private static bool IsStandardKey(string key) {
            foreach (MetaEntry met in sStandardEntries) {
                if (met.Key == key) {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Determines whether a string is part of a string array.  Case-sensitive.
        /// </summary>
        /// <param name="str">String to search for.</param>
        /// <param name="list">Array of strings to search.</param>
        /// <returns>True if the value was found, false if not.</returns>
        private static bool IsInList(string str, string[] list) {
            // We could create hash tables for this, but they're short lists and we do it rarely.
            foreach (string value in list) {
                if (str == value) {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Parses the serialized form of the metadata.
        /// </summary>
        /// <param name="buffer">Buffer with UTF-8 data.</param>
        /// <param name="offset">Offset of start of data.</param>
        /// <param name="length">Length of data.</param>
        /// <param name="appHook">Application hook reference.</param>
        public bool Parse(byte[] buffer, int offset, int length, AppHook appHook) {
            // Convert the entire thing to a string and manipulate that.
            string pool;
            try {
                pool = Encoding.UTF8.GetString(buffer, offset, length);
            } catch (ArgumentException) {
                // Invalid UTF-8 data found.  We could potentially recover some of the entries
                // by converting each entry individually and just throwing out the bad ones, but
                // I'm not expecting this to be a common problem.
                appHook.LogE("Found bad UTF-8 data in META chunk; discarding");
                return false;
            }

            if (pool[pool.Length - 1] != ENTRY_SEP_CHAR) {
                appHook.LogW("META chunk does not end with linefeed, may have been truncated");
            }

            // Split the pool into entries.
            string[] nvStrings = pool.Split(ENTRY_SEP_CHAR);

            // Split each entry into name/value, and create a list entry if they look vaguely
            // reasonable.  We don't try to validate the values of the standard keys.
            for (int i = 0; i < nvStrings.Length; i++) {
                string str = nvStrings[i];
                int splitPoint = str.IndexOf(NV_SEP_CHAR);
                if (string.IsNullOrEmpty(str)) {
                    // This is normal if it's the last one.
                    if (i != nvStrings.Length - 1) {
                        appHook.LogW("META chunk has a blank entry");
                    }
                } else if (splitPoint < 0) {
                    appHook.LogW("META chunk entry has no value: '" + str + "'");
                } else if (splitPoint == 0) {
                    appHook.LogW("META chunk entry has no name: '" + str + "'");
                } else {
                    string name = str.Substring(0, splitPoint);
                    string value = str.Substring(splitPoint + 1);

                    try {
                        mEntries.Add(name, value);
                    } catch (ArgumentException) {
                        appHook.LogW("META chunk has duplicate key: '" + name + "'");
                    }
                }
            }
            return true;
        }

        /// <summary>
        /// Generates the serialized form of the metadata.
        /// </summary>
        /// <remarks>
        /// No attempt is made to impose a particular order of keys.  The specification says
        /// that key order doesn't matter, and the examples in the standard set have keys
        /// in various orders (so whatever generated them is probably also using a hash table
        /// with undefined ordering).
        /// </remarks>
        /// <param name="offset">Result: offset of start of data within buffer.</param>
        /// <param name="length">Result: length of data.</param>
        /// <returns>Byte array with serialized data (may be over-sized).</returns>
        public byte[] Serialize(out int offset, out int length) {
            MemoryStream stream = new MemoryStream();
            foreach (KeyValuePair<string, string> kvp in mEntries) {
                Debug.Assert(!kvp.Value.Contains('\n'));
                stream.Write(Encoding.UTF8.GetBytes(kvp.Key));
                stream.WriteByte((byte)NV_SEP_CHAR);
                stream.Write(Encoding.UTF8.GetBytes(kvp.Value));
                stream.WriteByte((byte)ENTRY_SEP_CHAR);
            }

            offset = 0;
            length = (int)stream.Length;
            return stream.GetBuffer();
        }


        /// <summary>
        /// Exercises metadata handling.
        /// </summary>
        public static bool DebugTest() {
            Woz_Meta meta = Woz_Meta.CreateMETA();

            // General get/set.
            DebugExpect(meta, "key1", null);
            meta.SetValue("key1", "value1");
            DebugExpect(meta, "key1", "value1");
            meta.SetValue("key1", "value1a");
            DebugExpect(meta, "key1", "value1a");

            meta.SetValue("2legit_KEY", "2quit");       // starts with digit, has '_'
            DebugExpect(meta, "2legit_KEY", "2quit");
            DebugExpect(meta, "2legit_key", null);      // check case-sensitivity

            // Throw some non-ASCII in.
            const string UNI_TEST_STR = "Br\u00f8derbund Software\u2022";
            meta.SetValue("publisher", UNI_TEST_STR);
            DebugExpect(meta, "publisher", UNI_TEST_STR);

            // Delete an entry.
            meta.DeleteEntry("key1");
            DebugExpect(meta, "key1", null);

            // Try some generally bad arguments.
            try {
                meta.SetValue("key!!", "value1");           // key syntax is restricted
                throw new Exception("bad key allowed");
            } catch (ArgumentException) { /*expected*/ }
            try {
                meta.SetValue("key\n", "value1");           // test newline at end
                throw new Exception("bad key allowed");
            } catch (ArgumentException) { /*expected*/ }
            try {
                meta.SetValue("key2", "val\nue");           // test newline in middle
                throw new Exception("value newline allowed");
            } catch (ArgumentException) { /*expected*/ }
            try {
                meta.SetValue("key2", "value\n");           // test newline at end
                throw new Exception("value newline allowed");
            } catch (ArgumentException) { /*expected*/ }
            try {
                meta.SetValue("key2", "\tvalue");           // test tab chars
                throw new Exception("value tab allowed");
            } catch (ArgumentException) { /*expected*/ }
            try {
                meta.SetValue("key2", "a|b");               // no pipe chars generally
                throw new Exception("value pipe allowed");
            } catch (ArgumentException) { /*expected*/ }
#pragma warning disable CS8625
            try {
                meta.SetValue(null, "value2");
                throw new Exception("null key allowed");
            } catch (ArgumentNullException) { /*expected*/ }
            try {
                meta.SetValue("key2", null);
                throw new Exception("null value allowed");
            } catch (ArgumentNullException) { /*expected*/ }
#pragma warning restore CS8625

            // Test "standard" keys.
            meta.SetValue(LANGUAGE_KEY, "English");
            meta.SetValue(LANGUAGE_KEY, string.Empty);
            if (meta.DeleteEntry(LANGUAGE_KEY)) {
                throw new Exception("standard key deletion allowed");
            }
            try {
                meta.SetValue(LANGUAGE_KEY, "english");     // test case-sensitivity
                throw new Exception("value not case-sensitive");
            } catch (ArgumentException) { /*expected*/ }

            meta.SetValue(REQUIRES_RAM_KEY, "64K");
            try {
                meta.SetValue(REQUIRES_RAM_KEY, "65K");
                throw new Exception("invalid requires_ram allowed");
            } catch (ArgumentException) { /*expected*/ }

            meta.SetValue(REQUIRES_MACHINE_KEY, "2gs");
            meta.SetValue(REQUIRES_MACHINE_KEY, "2+|2e|2c|2gs");
            try {
                meta.SetValue(REQUIRES_MACHINE_KEY, "2+,2e");
                throw new Exception("invalid requires_machine allowed");
            } catch (ArgumentException) { /*expected*/ }
            try {
                meta.SetValue(REQUIRES_MACHINE_KEY, "|2e");
                throw new Exception("invalid requires_machine allowed");
            } catch (ArgumentException) { /*expected*/ }
            try {
                meta.SetValue(REQUIRES_MACHINE_KEY, "2e|");
                throw new Exception("invalid requires_machine allowed");
            } catch (ArgumentException) { /*expected*/ }
            try {
                meta.SetValue(REQUIRES_MACHINE_KEY, "2e||2gs");
                throw new Exception("invalid requires_machine allowed");
            } catch (ArgumentException) { /*expected*/ }

            meta.SetValue(SIDE_KEY, "Disk 1, Side A");
            meta.SetValue(SIDE_KEY, "Disk 12, Side B");
            try {
                meta.SetValue(SIDE_KEY, "Disk 1, Side A ");     // extra whitespace
                throw new Exception("invalid side allowed");
            } catch (ArgumentException) { /*expected*/ }
            try {
                meta.SetValue(SIDE_KEY, "Disk 1, Side b");      // case sensitivity
                throw new Exception("invalid side allowed");
            } catch (ArgumentException) { /*expected*/ }
            try {
                meta.SetValue(SIDE_KEY, "disk 1, Side A");      // case sensitivity
                throw new Exception("invalid side allowed");
            } catch (ArgumentException) { /*expected*/ }
            try {
                meta.SetValue(SIDE_KEY, "Disk A, Side B");
                throw new Exception("invalid side allowed");
            } catch (ArgumentException) { /*expected*/ }

            meta.SetValue(IMAGE_DATE_KEY, string.Empty);
            meta.SetValue(IMAGE_DATE_KEY, "2018-01-07T05:00:02.511Z");
            //meta.SetValue(IMAGE_DATE_KEY, "2018-01-07t05:00:02.511z");      // should be allowed?
            meta.SetValue(IMAGE_DATE_KEY, "2018-01-07Z");                   // no time specified
            meta.SetValue(IMAGE_DATE_KEY, "2018-01-07T16:39:57-08:00");     // explicit time zone
            meta.SetValue(IMAGE_DATE_KEY, "2018-01-07T05:00:02.511");       // no 'Z'; UTC assumed
            try {
                meta.SetValue(IMAGE_DATE_KEY, "2018-13-07T05:00:02.511Z");  // bad month
                throw new Exception("invalid image_date allowed");
            } catch (ArgumentException) { /*expected*/ }

            byte[] cereal = meta.Serialize(out int offset, out int length);

            Woz_Meta? newMeta = Woz_Meta.ParseMETA(cereal, offset, length,
                new AppHook(new CommonUtil.SimpleMessageLog()));
            DebugExpect(newMeta!, "publisher", UNI_TEST_STR);

            return true;
        }

        private static void DebugExpect(Woz_Meta meta, string key, string? expectedValue) {
            string? actualValue = meta.GetValue(key);
            if (expectedValue != actualValue) {
                throw new Exception("META fail: key='" + key +
                    "', expected='" + (expectedValue == null ? "(null)" : expectedValue) +
                    "', actual='" + (actualValue == null ? "(null)" : actualValue) +
                    "'");
            }
        }
    }
}
