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
using System.Diagnostics;
using System.Text;

using CommonUtil;
using static DiskArc.FS.HFS_Struct;

namespace DiskArc.FS {
    /// <summary>
    /// <para>HFS B*-tree nodes contain a collection of records.</para>
    /// <para>A node record is a variable-length key followed by data.  The length of the
    /// key is stored in the first byte of the record.  The length of the data is
    /// determined by the starting offset of the following record.  IM:F 2-66</para>
    /// <para>Records are parsed differently depending on whether they're part of the
    /// catalog data file or the extents overflow file.  Records are not self-identifying
    /// across trees, so it's up to the caller to invoke the appropriate parsing routine.</para>
    /// </summary>
    internal class HFS_Record {
        /// <summary>
        /// Reference to node data buffer.  Do not modify contents.
        /// </summary>
        public byte[] DataBuf { get; private set; }

        /// <summary>
        /// Offset of start of record within node data buffer.
        /// </summary>
        public int RecordOffset { get; private set; }

        /// <summary>
        /// Full length of record.
        /// </summary>
        public int RecordLength { get; private set; }

        /// <summary>
        /// Offset of key data (one past the key length byte).
        /// </summary>
        public int KeyOffset { get; private set; }

        /// <summary>
        /// Length of key, as stored in record (does not include initial length byte).
        /// </summary>
        /// <remarks>
        /// IM:F 2-71 says about ckrKeyLen: "if this field contains 0, the key
        /// indicates a deleted record".  It's not clear how this situation would occur.
        /// </remarks>
        public int KeyLength { get; private set; }

        /// <summary>
        /// Offset to start of record data.
        /// </summary>
        public int DataOffset { get; private set; }

        /// <summary>
        /// Length of record data.
        /// </summary>
        public int DataLength { get; private set; }


        /// <summary>
        /// Constructor.  Set "hasKey" to false for header nodes.
        /// </summary>
        public HFS_Record(byte[] data, int offset, int length, bool hasKey) {
            DataBuf = data;
            RecordOffset = offset;
            RecordLength = length;
            if (!hasKey) {
                KeyOffset = KeyLength = 0;
                DataOffset = offset;
                DataLength = length;
            } else {
                KeyLength = data[offset];
                KeyOffset = offset + 1;
                if (1 + KeyLength > length) {
                    throw new IOException("Key length (" + KeyLength +
                        " + 1) exceeds record length (" + length + ")");
                }
                DataOffset = offset + 1 + KeyLength;
                if ((DataOffset & 0x01) != 0) {
                    // The key area is padded with null bytes to a 16-bit word boundary;
                    // see IM:F 2-71.  This only matters for catalog records in leaf nodes,
                    // because all other keys are guaranteed to have an odd length (which
                    // becomes an even offset when you add the key length byte).  The padding
                    // is *not* included in the key length.
                    DataOffset++;
                }
                DataLength = length - (DataOffset - offset);
            }
        }

        public override string ToString() {
            // Records are not inherently self-identifying, but the lengths of the key and
            // data fields can uniquely identify the contents.
            StringBuilder sb = new StringBuilder();
            sb.Append("[Record: ");
            switch (DataLength) {
                case 4:                     // index record
                    if (KeyLength == HFS.EXT_INDEX_KEY_LEN) {
                        sb.Append("ext-idx ");
                        sb.Append(UnpackExtKey().ToString());
                    } else if (KeyLength == HFS.CAT_INDEX_KEY_LEN) {
                        sb.Append("cat-idx ");
                        sb.Append(UnpackCatKey().ToString());
                    } else {
                        sb.Append("???-index ");
                    }
                    uint nodeIndex = RawData.GetU32BE(DataBuf, DataOffset);
                    sb.Append(": nodeIndex=");
                    sb.Append(nodeIndex);
                    break;
                case CatDataDirRec.LENGTH:
                    sb.Append("cat-dir ");
                    sb.Append(UnpackCatKey().ToString());
                    CatDataDirRec drec = ParseCatDataDirRec();
                    sb.Append(": cnid=#" + drec.dirDirID.ToString("x4"));
                    break;
                case CatDataFilRec.LENGTH:
                    sb.Append("cat-fil ");
                    sb.Append(UnpackCatKey().ToString());
                    CatDataFilRec frec = ParseCatDataFilRec();
                    sb.Append(": cnid=#" + frec.filFlNum.ToString("x4"));
                    break;
                case CatDataThreadRec.LENGTH:
                    sb.Append("cat-thd ");
                    sb.Append(UnpackCatKey().ToString());
                    CatDataThreadRec trec = ParseCatDataThreadRec();
                    sb.AppendFormat(": parent=#{0:x4} name='{1}'", trec.thdParID,
                        MacChar.MacStrToUnicode(trec.thdCName, 0, MacChar.Encoding.RomanShowCtrl));
                    break;
                case ExtDataRec.LENGTH:
                    sb.Append("ext-rec ");
                    sb.Append(UnpackExtKey().ToString());
                    ExtDataRec erec = ParseExtDataRec();
                    sb.Append(erec.ToString());
                    break;
                case HFS_BTree.MapNode.MAP_BYTES:
                    sb.Append("map-rec");
                    break;
                default:
                    sb.AppendFormat("UNKNOWN keyLen={0}, dataLen={1}", + KeyLength, DataLength);
                    break;
            }
            sb.Append(" (recLen=");
            sb.Append(RecordLength);
            sb.Append(")]");
            return sb.ToString();
        }

        /// <summary>
        /// Interface to faciliate key comparisons.  The comparison function is different for
        /// each tree.
        /// </summary>
        public interface IKey {
            /// <summary>
            /// Compares the object's contents to the key in the specified record.  Equivalent
            /// to Compare(IKey, rec).
            /// </summary>
            /// <param name="rec">Record to compare against.</param>
            /// <returns>Less than, greater than, or equal to zero depending on whether the
            /// key object's value is less then, greater than, or equal to the
            /// record's value.</returns>
            int Compare(HFS_Record rec);
        }

        /// <summary>
        /// Extracts the node index value from an index record.
        /// </summary>
        /// <returns></returns>
        /// <exception cref="IOException"></exception>
        public uint GetIndexNodeData() {
            if (DataLength != 4) {
                throw new IOException("Unexpected length for index data: " + DataLength);
            }
            return RawData.GetU32BE(DataBuf, DataOffset);
        }

        /// <summary>
        /// Creates an index record, from a key and an index.
        /// </summary>
        /// <param name="tree">Tree in which record will live.</param>
        /// <param name="recWithKey">Record with key to use.</param>
        /// <param name="nodeIndex">Index of node this record will point to.</param>
        /// <returns>New record object.</returns>
        public static HFS_Record CreateIndexRecord(HFS_BTree tree, HFS_Record recWithKey,
                uint nodeIndex) {
            // Index records have a fixed key length that depends on the tree type.
            int keyLen = tree.Header.MaxKeyLen;
            int recLen = keyLen + 1 + 4;
            byte[] newRecBuf = new byte[recLen];

            // Copy key from record.
            Array.Copy(recWithKey.DataBuf, recWithKey.KeyOffset, newRecBuf, 1, recWithKey.KeyLength);
            newRecBuf[0] = (byte)keyLen;

            // Set the data to the node index.
            RawData.SetU32BE(newRecBuf, keyLen + 1, nodeIndex);

            return new HFS_Record(newRecBuf, 0, recLen, true);
        }

        /// <summary>
        /// Unpacks the key from this record.  The return value's class is determined by the
        /// tree that the record is part of.
        /// </summary>
        public IKey UnpackKey(HFS_BTree.Type type) {
            if (type == HFS_BTree.Type.Extents) {
                return UnpackExtKey();
            } else if (type == HFS_BTree.Type.Catalog) {
                return UnpackCatKey();
            } else {
                throw new DAException("Unexpected tree type " + type);
            }
        }

        #region Catalog

        /// <summary>
        /// Catalog record type, for cdrType field.  IM:F 2-73
        /// </summary>
        public enum CatDataType : byte {
            Unknown = 0,
            Directory = 1,
            File = 2,
            DirectoryThread = 3,
            FileThread = 4
        }

        /// <summary>
        /// Catalog record key.  May be extracted from a record, or formed to use for comparisons.
        /// </summary>
        public class CatKey : IKey {
            /// <summary>
            /// Parent CNID.
            /// </summary>
            public uint ParentID { get; private set; }

            /// <summary>
            /// Data buffer that holds the key.
            /// </summary>
            public byte[] Data { get; private set; }

            /// <summary>
            /// Offset of length byte at start of filename.
            /// </summary>
            public int NameOffset { get; private set; }

            /// <summary>
            /// Length of key.
            /// </summary>
            public int KeyLength {
                get {
                    if (Data.Length == 0) {
                        // thread key: reserved=1, CNID=4, namelen=1
                        // The key is only 6 bytes long, but we return 7 so that it's word-aligned.
                        // The pad byte isn't normally included in the key length, but it is here.
                        return 7;
                    } else {
                        // dir/file key: reserved=1, CNID=4, namelen=1, filename=[1,31]
                        return 1 + 4 + 1 + Data[NameOffset];
                    }
                }
            }

            /// <summary>
            /// Number of bytes required to store the key, including the key length byte and
            /// the 16-bit padding byte at the end.
            /// </summary>
            public int KeyStorageLen {
                get { return ((1 + KeyLength) + 1) & ~1; }
            }

            /// <summary>
            /// Constructor for a key without a filename (for threads).
            /// </summary>
            /// <param name="entryID">Entry directory CNID.</param>
            public CatKey(uint entryID) {
                ParentID = entryID;
                Data = RawData.EMPTY_BYTE_ARRAY;
                NameOffset = -1;
            }

            /// <summary>
            /// Constructor for a key with a filename (for files and directories).
            /// </summary>
            /// <param name="parentID">Parent directory CNID.</param>
            /// <param name="data">Buffer with filename data.</param>
            /// <param name="nameOffset">Offset to filename data (starting with
            ///   length byte).</param>
            /// <exception cref="IOException">Invalid filename length.</exception>
            public CatKey(uint parentID, byte[] data, int nameOffset) {
                ParentID = parentID;
                Data = data;
                NameOffset = nameOffset;

                int nameLen = data[nameOffset];
                if (nameLen > HFS.MAX_FILE_NAME_LEN) {
                    throw new IOException("Filename too long for cat key (len=" + nameLen + ")");
                }
            }

            /// <summary>
            /// Constructor for a key with a Unicode filename.
            /// </summary>
            /// <param name="parentID">Parent directory CNID.</param>
            /// <param name="name">Unicode string.</param>
            public CatKey(uint parentID, string name) :
                    this(parentID, MacChar.UnicodeToMacStr(name, MacChar.Encoding.RomanShowCtrl), 0) {
            }

            // IKey
            public int Compare(HFS_Record rec) {
                uint recParentID = RawData.GetU32BE(rec.DataBuf, rec.KeyOffset + 1);
                if (recParentID != ParentID) {
                    return (int)(ParentID - recParentID);
                }

                // Compare filenames.  If the key was created without a filename, it won't have
                // a data buffer, so we can't just call the full compare function.
                int nameLen = (NameOffset < 0) ? 0 : Data[NameOffset];
                byte recNameLen = rec.DataBuf[rec.KeyOffset + 5];
                if (nameLen == 0 && recNameLen == 0) {
                    return 0;       // exact match
                } else if (nameLen == 0) {
                    return -1;      // key is shorter, so we come first
                } else {
                    return MacChar.CompareHFSFileNames(Data, NameOffset,
                        rec.DataBuf, rec.KeyOffset + 5);
                }
            }

            /// <summary>
            /// Serializes the key into a buffer.
            /// </summary>
            public void Store(byte[] buf, int offset) {
                RawData.WriteU8(buf, ref offset, (byte)KeyLength);
                RawData.WriteU8(buf, ref offset, 0);        // ckrResrv1
                RawData.WriteU32BE(buf, ref offset, ParentID);
                if (Data.Length == 0) {
                    RawData.WriteU8(buf, ref offset, 0);
                } else {
                    byte len = Data[NameOffset];
                    RawData.WriteU8(buf, ref offset, len);
                    Array.Copy(Data, NameOffset + 1, buf, offset, len);
                }
            }

            public override string ToString() {
                string name;
                if (NameOffset < 0) {
                    name = string.Empty;
                } else {
                    name = MacChar.MacStrToUnicode(Data, NameOffset, MacChar.Encoding.RomanShowCtrl);
                }
                return string.Format("<#{0:x4}/{1}>", ParentID, name);
            }
        }

        /// <summary>
        /// Unpacks the record's key as a catalog key.
        /// </summary>
        /// <exception cref="IOException">Invalid name length.</exception>
        public CatKey UnpackCatKey() {
            // Keys in catalog records have the form (IM:F 2-71):
            //  header:
            //   +00    ckrKeyLen: key length
            //  body:
            //   +00    ckrResrv1: (reserved)
            //   +01-04 ckrParID: parent directory ID
            //   +05-?? ckrCName: catalog node name (Str31, which starts with a length byte)
            // The name may be blank for thread records.

            uint ckrParID = RawData.GetU32BE(DataBuf, KeyOffset + 1);
            int nameOffset = KeyOffset + 5;
            if (DataBuf[nameOffset] > KeyLength - 6) {
                throw new IOException("Bad name string in catalog record key (len=" +
                    DataBuf[nameOffset] + ")");
            }
            return new CatKey(ckrParID, DataBuf, nameOffset);
        }

        /// <summary>
        /// Obtains the record data type from the record's data area.
        /// </summary>
        /// <returns>Type found.</returns>
        /// <exception cref="IOException">Invalid data found.</exception>
        public CatDataType GetCatDataType() {
            byte cdrType = DataBuf[DataOffset];
            if (cdrType != (byte)CatDataType.Directory &&
                    cdrType != (byte)CatDataType.File &&
                    cdrType != (byte)CatDataType.DirectoryThread &&
                    cdrType != (byte)CatDataType.FileThread) {
                throw new IOException("Unexpected cat record data type: " + cdrType);
            }
            return (CatDataType)cdrType;
        }

        public CatDataDirRec ParseCatDataDirRec() {
            Debug.Assert(DataBuf[DataOffset] == (byte)CatDataType.Directory);
            Debug.Assert(DataLength == CatDataDirRec.LENGTH);
            CatDataDirRec parsed = new CatDataDirRec();
            parsed.Load(DataBuf, DataOffset);
            return parsed;
        }

        public void UpdateCatDataDirRec(CatDataDirRec dirRec) {
            Debug.Assert(DataLength == CatDataDirRec.LENGTH);
            dirRec.Store(DataBuf, DataOffset);
        }

        public static HFS_Record GenerateCatDataDirRec(CatKey key, CatDataDirRec dirRec) {
            // Variable-length key, fixed-length data.
            int keyStoreLen = key.KeyStorageLen;
            byte[] buffer = new byte[keyStoreLen + CatDataDirRec.LENGTH];
            key.Store(buffer, 0);
            dirRec.Store(buffer, keyStoreLen);
            return new HFS_Record(buffer, 0, buffer.Length, true);
        }

        public CatDataFilRec ParseCatDataFilRec() {
            Debug.Assert(DataBuf[DataOffset] == (byte)CatDataType.File);
            Debug.Assert(DataLength == CatDataFilRec.LENGTH);
            CatDataFilRec parsed = new CatDataFilRec();
            parsed.Load(DataBuf, DataOffset);
            return parsed;
        }

        public void UpdateCatDataFilRec(CatDataFilRec filRec) {
            Debug.Assert(DataLength == CatDataFilRec.LENGTH);
            filRec.Store(DataBuf, DataOffset);
        }

        public static HFS_Record GenerateCatDataFilRec(CatKey key, CatDataFilRec filRec) {
            // Variable-length key, fixed-length data.
            int keyStoreLen = key.KeyStorageLen;
            byte[] buffer = new byte[keyStoreLen + CatDataFilRec.LENGTH];
            key.Store(buffer, 0);
            filRec.Store(buffer, keyStoreLen);
            return new HFS_Record(buffer, 0, buffer.Length, true);
        }

        public CatDataThreadRec ParseCatDataThreadRec() {
            Debug.Assert(DataBuf[DataOffset] == (byte)CatDataType.DirectoryThread ||
                         DataBuf[DataOffset] == (byte)CatDataType.FileThread);
            Debug.Assert(DataLength == CatDataThreadRec.LENGTH);
            CatDataThreadRec parsed = new CatDataThreadRec();
            int offset = DataOffset;
            parsed.Load(DataBuf, ref offset);
            return parsed;
        }

        public static CatDataThreadRec CreateCatThread(uint parentCNID, string fileName,
                bool isDirThread) {
            CatDataThreadRec threadData = new CatDataThreadRec();
            threadData.cdrType = isDirThread ?
                (byte)CatDataType.DirectoryThread : (byte)CatDataType.FileThread;
            threadData.cdrResrv2 = 0;
            threadData.thdResrv[0] = threadData.thdResrv[1] = 0;
            threadData.thdParID = parentCNID;
            threadData.thdCName[0] = (byte)fileName.Length;
            MacChar.UnicodeToMac(fileName, threadData.thdCName, 1, MacChar.Encoding.RomanShowCtrl);
            return threadData;
        }

        public static HFS_Record GenerateCatDataThreadRec(CatKey key, CatDataThreadRec thdRec) {
            // Fixed-length key, fixed-length data.
            int keyStoreLen = key.KeyStorageLen;
            Debug.Assert(keyStoreLen == 8);
            byte[] buffer = new byte[keyStoreLen + CatDataThreadRec.LENGTH];
            key.Store(buffer, 0);
            int offset = keyStoreLen;
            thdRec.Store(buffer, ref offset);
            return new HFS_Record(buffer, 0, buffer.Length, true);
        }

        #endregion Catalog

        #region Extents

        /// <summary>
        /// Fork type values for extents overflow keys.
        /// </summary>
        public enum ForkType : byte {
            Data = 0x00, Rsrc = 0xff
        }

        /// <summary>
        /// Extents overflow record key.  May be extracted from a record, or formed to use
        /// for comparisons.
        /// </summary>
        public class ExtKey : IKey {
            public const int KEY_STORAGE_LEN = 8;

            public uint FileCNID { get; private set; }
            public byte Fork { get; private set; }
            public ushort IndexOfFirst { get; private set; }

            public ExtKey(bool isRsrcFork, uint fileNum, ushort indexOfFirst) {
                Fork = isRsrcFork ? (byte)ForkType.Rsrc : (byte)ForkType.Data;
                FileCNID = fileNum;
                IndexOfFirst = indexOfFirst;
            }

            // IKey
            public int Compare(HFS_Record rec) {
                uint recFileCNID = RawData.GetU32BE(rec.DataBuf, rec.KeyOffset + 1);
                if (FileCNID != recFileCNID) {
                    return (int)(FileCNID - recFileCNID);
                }
                byte recForkType = rec.DataBuf[rec.KeyOffset];
                if (Fork != recForkType) {
                    return Fork - recForkType;
                }
                ushort recIndexOfFirst = RawData.GetU16BE(rec.DataBuf, rec.KeyOffset + 5);
                if (IndexOfFirst != recIndexOfFirst) {
                    return IndexOfFirst - recIndexOfFirst;
                }
                return 0;
            }

            /// <summary>
            /// Serializes the key into a buffer.
            /// </summary>
            public void Store(byte[] buf, int offset) {
                RawData.WriteU8(buf, ref offset, HFS.EXT_INDEX_KEY_LEN);
                RawData.WriteU8(buf, ref offset, Fork);
                RawData.WriteU32BE(buf, ref offset, FileCNID);
                RawData.WriteU16BE(buf, ref offset, IndexOfFirst);
            }

            public override string ToString() {
                string forkStr;
                if (Fork == (byte)ForkType.Data) {
                    forkStr = "data";
                } else if (Fork == (byte)ForkType.Rsrc) {
                    forkStr = "rsrc";
                } else {
                    forkStr = "????";
                }
                return string.Format("<#{0:x4}-{1}-{2}>", FileCNID, forkStr, IndexOfFirst);
            }
        }

        /// <summary>
        /// Unpacks the record's key as an extents overflow key.
        /// </summary>
        public ExtKey UnpackExtKey() {
            // Extents overflow tree keys have the form (IM:F 2-75):
            //  header:
            //   +00    xkrKeyLen: key length (always 7)
            //  body:
            //   +00    xkrFkType: fork type ($00=data, $ff=rsrc)
            //   +01-04 xkrFNum: file CNID
            //   +05-06 xkrFABN: starting file allocation block
            if (KeyLength != HFS.EXT_INDEX_KEY_LEN) {
                throw new IOException("Bad extents overflow key length: " + KeyLength);
            }
            byte xkrFkType = DataBuf[KeyOffset];
            uint xkrFNum = RawData.GetU32BE(DataBuf, KeyOffset + 1);
            ushort xkrFABN = RawData.GetU16BE(DataBuf, KeyOffset + 5);
            return new ExtKey(xkrFkType == (byte)ForkType.Rsrc, xkrFNum, xkrFABN);
        }

        public ExtDataRec ParseExtDataRec() {
            Debug.Assert(DataLength == ExtDataRec.LENGTH);
            ExtDataRec parsed = new ExtDataRec();
            int offset = DataOffset;
            parsed.Load(DataBuf, ref offset);
            return parsed;
        }

        public void UpdateExtDataRec(ExtDataRec extRec) {
            Debug.Assert(DataLength == ExtDataRec.LENGTH);
            int offset = DataOffset;
            extRec.Store(DataBuf, ref offset);
        }

        public static HFS_Record GenerateExtDataRec(ExtKey key, ExtDataRec extRec) {
            // Fixed-length key, fixed-length data.
            byte[] buffer = new byte[ExtKey.KEY_STORAGE_LEN + ExtDataRec.LENGTH];
            key.Store(buffer, 0);
            int offset = ExtKey.KEY_STORAGE_LEN;
            extRec.Store(buffer, ref offset);
            return new HFS_Record(buffer, 0, buffer.Length, true);
        }

        #endregion Extents
    }
}
