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
using System.Collections;
using System.Diagnostics;

using CommonUtil;

namespace FileConv {
    /// <summary>
    /// Parses Mac and IIgs resource forks.
    /// </summary>
    /// <remarks>
    /// A single instance may be shared among multiple converters when doing applicability testing.
    /// </remarks>
    public class ResourceMgr : IEnumerable<ResourceMgr.ResourceEntry> {
        /// <summary>
        /// True if the resource fork is for the Mac, false if for the IIgs.
        /// </summary>
        public bool IsMac { get; private set; }

        public Notes Notes { get; } = new Notes();

        /// <summary>
        /// One entry in the resource file.
        /// </summary>
        public class ResourceEntry {
            // Apple IIgs resource type name definitions.
            //
            // The most complete list appears to be the System 6.0.1 NList.Data from NiftyList.
            // This list comes from there.
            private const string UNKNOWN_SYS_RSRC = "system resource";
            private const string APP_RSRC = "application-defined resource";
            private static readonly string[] sRsrc8000 = new string[0x30] {
                // 0x8000-802f
                UNKNOWN_SYS_RSRC,   "rIcon",            "rPicture",         "rControlList",
                "rControlTemplate", "rC1InputString",   "rPString",         "rStringList",
                "rMenuBar",         "rMenu",            "rMenuItem",        "rTextForLETextBox2",
                "rCtlDefProc",      "rCtlColorTbl",     "rWindParam1",      "rWindParam2",

                "rWindColor",       "rTextBlock",       "rStyleBlock",      "rToolStartup",
                "rResName",         "rAlertString",     "rText",            "rCodeResource",
                "rCDEVCode",        "rCDEVFlags",       "rTwoRects",        "rFileType",
                "rListRef",         "rCString",         "rXCMD",            "rXFCN",

                "rErrorString",     "rKTransTable",     "rWString",         "rC1OutputString",
                "rSoundSample",     "rTERuler",         "rFSequence",       "rCursor",
                "rItemStruct",      "rVersion",         "rComment",         "rBundle",
                "rFinderPath",      "rPaletteWindow",   "rTaggedStrings",   "rPatternList",
            };
            private static readonly string[] sRsrcC000 = new string[0x04] {
                // 0xc000-c003
                UNKNOWN_SYS_RSRC,    "rRectList",       "rPrintRecord",     "rFont",
            };

            /// <summary>
            /// Resource type.  4-byte packed char value for Mac, 2-byte unsigned int for IIgs.
            /// </summary>
            public uint Type { get; private set; }

            /// <summary>
            /// Human-readable type string.  For Mac this is just the stringification of the
            /// Type, for the IIgs this is the name from a table lookup.
            /// </summary>
            public string TypeStr { get; private set; }

            /// <summary>
            /// Resource ID.  2-byte signed int for Mac, 4-byte unsigned int for IIgs.
            /// </summary>
            public uint ID { get; private set; }

            /// <summary>
            /// Resource name, from Mac table.  May be empty.
            /// </summary>
            public string Name { get; private set; }

            /// <summary>
            /// Offset to resource data from start of file.
            /// </summary>
            public long FileOffset { get; private set; }

            /// <summary>
            /// Length of resource data.
            /// </summary>
            public int Length { get; private set; }

            /// <summary>
            /// Construct a IIgs resource entry.
            /// </summary>
            /// <param name="type">Resource type.</param>
            /// <param name="id">Resource ID.</param>
            /// <param name="fileOffset">Offset to data from start of file.</param>
            /// <param name="length">Length of data.</param>
            public ResourceEntry(ushort type, uint id, long fileOffset, int length) {
                Type = type;
                ID = id;
                Name = string.Empty;
                FileOffset = fileOffset;
                Length = length;

                if (type < 0x8000) {
                    TypeStr = APP_RSRC;
                } else if (type >= 0x8000 && type < 0x8000 + sRsrc8000.Length) {
                    TypeStr = sRsrc8000[type - 0x8000];
                } else if (type >= 0xc000 && type < 0xc000 + sRsrcC000.Length) {
                    TypeStr = sRsrcC000[type - 0xc000];
                } else {
                    TypeStr = UNKNOWN_SYS_RSRC;
                }
            }

            /// <summary>
            /// Construct a Mac resource entry.
            /// </summary>
            /// <param name="type">Resource type.</param>
            /// <param name="id">Resource ID.</param>
            /// <param name="name">Resource name.</param>
            /// <param name="fileOffset">Offset to data from start of file.</param>
            /// <param name="length">Length of data.</param>
            public ResourceEntry(uint type, int id, string name, long fileOffset, int length) {
                Type = type;
                ID = (uint)id;
                Name = name;
                FileOffset = fileOffset;
                Length = length;

                TypeStr = MacChar.StringifyMacConstant(type);
            }
        }

        public IEnumerator<ResourceEntry> GetEnumerator() {
            return ((IEnumerable<ResourceEntry>)mEntries).GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() {
            return ((IEnumerable)mEntries).GetEnumerator();
        }

        /// <summary>
        /// Reference to resource fork file stream.  This is not owned by ResourceMgr.
        /// </summary>
        private Stream mRsrcStream;

        /// <summary>
        /// List of entries parsed from resource fork.
        /// </summary>
        private List<ResourceEntry> mEntries = new List<ResourceEntry>();


        /// <summary>
        /// Private constructor.
        /// </summary>
        /// <param name="rsrcStream"></param>
        private ResourceMgr(Stream rsrcStream) {
            mRsrcStream = rsrcStream;
        }

        /// <summary>
        /// Creates a new resource manager instance, for the resource fork of a file.
        /// </summary>
        /// <param name="rsrcStream">Resource fork stream.</param>
        /// <returns>Resource manager object, or null if this doesn't appear to be a
        ///   valid resource fork.</returns>
        public static ResourceMgr? Create(Stream rsrcStream) {
            if (rsrcStream.Length < 128) {
                return null;
            }

            ResourceMgr newMgr = new ResourceMgr(rsrcStream);
            try {
                rsrcStream.Position = 0;
                uint rFileVersion = RawData.ReadU32LE(rsrcStream, out bool ok);
                if (!ok) {
                    return null;
                }
                if (rFileVersion == 0) {
                    ok = newMgr.ParseIIgs();
                } else {
                    ok = newMgr.ParseMac();
                }
                if (!ok) {
                    Debug.WriteLine("Unable to parse resource fork");
                    return null;
                }
            } catch (IOException ex) {
                Debug.WriteLine("Failed reading resource fork: " + ex.Message);
                return null;
            } catch (Exception ex) {
                Debug.WriteLine("General failure parsing resource fork: " + ex.Message);
                return null;
            }
            return newMgr;
        }

        /// <summary>
        /// Parses the resource fork as a IIgs file.
        /// </summary>
        /// <returns>True on success.</returns>
        private bool ParseIIgs() {
            bool ok;
            mRsrcStream.Position = 0;
            uint rFileVersion = RawData.ReadU32LE(mRsrcStream, out ok);
            uint rFileToMap = RawData.ReadU32LE(mRsrcStream, out ok);
            uint rFileMapSize = RawData.ReadU32LE(mRsrcStream, out ok);
            Debug.Assert(ok);       // no reason this should be failing now

            if (rFileToMap > mRsrcStream.Length || rFileMapSize > mRsrcStream.Length ||
                    mRsrcStream.Length - rFileMapSize < rFileToMap) {
                Debug.WriteLine("Invalid IIgs rsrc header values: toMap=" +
                    rFileToMap + " size=" + rFileMapSize + " len=" + mRsrcStream.Length);
                return false;
            }
            if (rFileMapSize > 1024 * 1024 * 16) {
                Debug.WriteLine("Huge map: " + rFileMapSize);
                return false;
            }

            // Read the resource map into memory.
            byte[]? mapBytes = ReadChunk(rFileToMap, (int)rFileMapSize);
            if (mapBytes == null) {
                return false;
            }
            // Parse a few things out of the header.
            ushort mapToIndex = RawData.GetU16LE(mapBytes, 0x0e);
            uint mapIndexSize = RawData.GetU32LE(mapBytes, 0x14);
            uint mapIndexUsed = RawData.GetU32LE(mapBytes, 0x18);

            // Add entries to linked list.
            const int RES_REF_REC_LENGTH = 20;
            int offset = (int)mapToIndex;
            for (int i = 0; i < mapIndexUsed; i++) {
                if (offset + RES_REF_REC_LENGTH > mapBytes.Length) {
                    Notes.AddW("Glitch: overran resource map");
                    break;
                }
                ushort resType = RawData.GetU16LE(mapBytes, offset + 0x00);
                if (resType == 0) {
                    // End of list reached.  Should happen when count==mapIndexUsed.
                    if (i != mapIndexUsed) {
                        Notes.AddW("Glitch: count doesn't match mapIndexUsed (i= " +
                            " used=" + mapIndexUsed);
                    }
                    break;
                }
                uint resID = RawData.GetU32LE(mapBytes, offset + 0x02);
                uint resOffset = RawData.GetU32LE(mapBytes, offset + 0x06);
                //ushort resAttr = RawData.GetU16LE(mapBytes, offset + 0x0a);
                uint resSize = RawData.GetU32LE(mapBytes, offset + 0x0c);
                //uint resHandle = RawData.GetU32LE(mapBytes, offset + 0x10);

                ResourceEntry newEntry = new ResourceEntry(resType, resID, resOffset, (int)resSize);
                mEntries.Add(newEntry);

                offset += RES_REF_REC_LENGTH;
            }

            return true;
        }

        /// <summary>
        /// Parses the resource fork as a Macintosh file.
        /// </summary>
        /// <returns>True on success.</returns>
        private bool ParseMac() {
            bool ok;
            mRsrcStream.Position = 0;
            uint offsetToData = RawData.ReadU32BE(mRsrcStream, out ok);
            uint offsetToMap = RawData.ReadU32BE(mRsrcStream, out ok);
            uint dataLength = RawData.ReadU32BE(mRsrcStream, out ok);
            uint mapLength = RawData.ReadU32BE(mRsrcStream, out ok);
            Debug.Assert(ok);       // no reason this should be failing now

            //Notes.AddI("Macintosh-style resource fork");

            if (offsetToMap > mRsrcStream.Length || mapLength > mRsrcStream.Length ||
                    mRsrcStream.Length - mapLength < offsetToMap) {
                Debug.WriteLine("Invalid Mac rsrc header values: toMap=" +
                    offsetToMap + " size=" + mapLength + " len=" + mRsrcStream.Length);
                return false;
            }
            if (mapLength > 1024 * 1024 * 16) {
                Debug.WriteLine("Huge map: " + mapLength);
                return false;
            } else if (mapLength < 0x1c) {
                Debug.WriteLine("Map too small: " + mapLength);
                return false;
            }

            // Read the resource map into memory.
            byte[]? mapBytes = ReadChunk(offsetToMap, (int)mapLength);
            if (mapBytes == null) {
                return false;
            }
            // Parse a few things out of the header.
            ushort mapToTypeList = RawData.GetU16BE(mapBytes, 0x18);
            ushort mapToNameList = RawData.GetU16BE(mapBytes, 0x1a);
            if (mapToTypeList > mapBytes.Length || mapToNameList > mapBytes.Length) {
                Debug.WriteLine("Invalid Mac rsrc map: toType=" + mapToTypeList +
                    " toName=" + mapToNameList + " len=" + mapBytes.Length);
                return false;
            }

            // Type list offset points to a 16-bit type count, which is the number of entries
            // in the list minus 1 (an empty list will be 0xffff).
            ushort typeCount = RawData.GetU16BE(mapBytes, mapToTypeList);
            typeCount++;
            int offset = mapToTypeList + 2;
            const int TYPE_LIST_ENTRY_SIZE = 8;
            const int REF_LIST_ENTRY_SIZE = 12;
            for (int i = 0; i < typeCount; i++) {
                if (offset + 8 > mapBytes.Length) {
                    Notes.AddW("Type list ended unexpectedly: i=" + i);
                    break;
                }
                uint resType = RawData.GetU32BE(mapBytes, offset);
                short resCount = (short)RawData.GetU16BE(mapBytes, offset + 0x04);
                short resRefOffset = (short)RawData.GetU16BE(mapBytes, offset + 0x06);

                int refListOffset = mapToTypeList + resRefOffset;
                for (int ri = 0; ri <= resCount; ri++) {
                    short resID = (short)RawData.GetU16BE(mapBytes, refListOffset);
                    short resNameOffset = (short)RawData.GetU16BE(mapBytes, refListOffset + 0x02);
                    byte resAttr = mapBytes[refListOffset + 0x04];
                    uint resOffset = RawData.GetU24BE(mapBytes, refListOffset + 0x05);

                    string name = string.Empty;
                    if (resNameOffset >= 0) {
                        int nameOffset = mapToNameList + resNameOffset;
                        name = GetPascalString(mapBytes, nameOffset);
                    }

                    // Absolute file offset of start of first resource.  The first 4 bytes are the
                    // length.
                    int fileOffset = (int)(offsetToData + resOffset);
                    mRsrcStream.Position = fileOffset;
                    int resLength = (int)RawData.ReadU32BE(mRsrcStream, out ok);
                    if (!ok) {
                        Notes.AddW("Unable to get resource length");
                    }

                    ResourceEntry newEntry =
                        new ResourceEntry(resType, resID, name, fileOffset + 4, resLength);
                    mEntries.Add(newEntry);

                    refListOffset += REF_LIST_ENTRY_SIZE;
                }

                offset += TYPE_LIST_ENTRY_SIZE;
            }

            return true;
        }

        /// <summary>
        /// Searches for an entry with a matching resource type and ID.
        /// </summary>
        /// <param name="resType">Resource type.</param>
        /// <param name="resID">Resource ID.</param>
        /// <returns>Entry found, or null.</returns>
        public ResourceEntry? FindEntry(int resType, int resID) {
            foreach (ResourceEntry entry in mEntries) {
                if (entry.Type == (uint)resType && entry.ID == (uint)resID) {
                    return entry;
                }
            }
            return null;
        }

        /// <summary>
        /// Reads an entry from the resource fork into a newly-allocated buffer.
        /// </summary>
        /// <param name="entry">Entry to read.</param>
        /// <returns>New buffer on success, null on failure.</returns>
        public byte[]? ReadEntry(ResourceEntry entry) {
            byte[] buf = new byte[entry.Length];
            if (GetEntryData(entry, buf, 0)) {
                return buf;
            } else {
                return null;
            }
        }

        /// <summary>
        /// Reads the data for the specified entry into a new buffer.
        /// </summary>
        /// <param name="entry">Entry to read.</param>
        /// <param name="buf">Buffer to read data into.</param>
        /// <param name="offset">Byte offset in buffer of start of data.</param>
        /// <returns>True on success.</returns>
        public bool GetEntryData(ResourceEntry entry, byte[] buf, int offset) {
            if (entry.Length > buf.Length - offset) {
                Debug.Assert(false, "bad buffer args");
                return false;
            }
            try {
                mRsrcStream.Position = entry.FileOffset;
                mRsrcStream.ReadExactly(buf, offset, entry.Length);
            } catch (IOException ex) {
                Debug.WriteLine("GetEntryData failed: " + ex.Message);
                return false;
            }
            return true;
        }

        /// <summary>
        /// Extracts a Pascal (length-prefixed) string from a buffer.  The character set is
        /// assumed to be Mac OS Roman.
        /// </summary>
        /// <param name="buf">Data buffer.</param>
        /// <param name="offset">Offset of length byte.</param>
        /// <returns>String found, or an empty string if invalid.</returns>
        private string GetPascalString(byte[] buf, int offset) {
            if (offset > buf.Length) {
                Notes.AddW("Invalid string offset: " + offset);
                return string.Empty;
            }
            byte strLen = buf[offset];
            if (offset + strLen > buf.Length) {
                Notes.AddW("String exceeds buffer len: offset=" + offset + " strLen=" + strLen +
                    " length=" + buf.Length);
                return string.Empty;
            }
            return MacChar.MacStrToUnicode(buf, offset, MacChar.Encoding.RomanShowCtrl);
        }

        /// <summary>
        /// Reads a chunk of a file into a newly-allocated buffer.
        /// </summary>
        /// <param name="fileOffset">File offset.</param>
        /// <param name="count">Number of bytes to read.</param>
        /// <returns>Data buffer, or null if the read failed.</returns>
        private byte[]? ReadChunk(long fileOffset, int count) {
            byte[] buf = new byte[count];
            try {
                mRsrcStream.Position = fileOffset;
                mRsrcStream.ReadExactly(buf, 0, count);
            } catch (IOException ex) {
                Debug.WriteLine("ReadChunk failed: offset=" + fileOffset + " count=" + count +
                    ex.Message);
                return null;
            }
            return buf;
        }
    }
}
