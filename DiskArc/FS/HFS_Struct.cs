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

using CommonUtil;

namespace DiskArc.FS {
    /// <summary>
    /// Accessors for HFS data structures.
    /// </summary>
    /// <remarks>
    /// <para>All multi-byte integer values are stored in big-endian order.  In the docs,
    /// "integers" are 16-bit, "long integers" are 32-bit, and string types like Str31 start
    /// with a length byte and usually occupy a fixed amount of memory (in this case,
    /// 32 bytes).</para>
    /// </remarks>
    internal static class HFS_Struct {
        /// <summary>
        /// Extent descriptor.  IM:F 2-75
        /// </summary>
        public class ExtDescriptor {
            public const int LENGTH = 4;

            /// <summary>First allocation block.</summary>
            public ushort xdrStABN;

            /// <summary>Number of allocation blocks.</summary>
            public ushort xdrNumAblks;

            public void Load(byte[] buf, ref int offset) {
                int startOffset = offset;
                xdrStABN = RawData.ReadU16BE(buf, ref offset);
                xdrNumAblks = RawData.ReadU16BE(buf, ref offset);
                Debug.Assert(offset == startOffset + LENGTH);
            }
            public void Store(byte[] buf, ref int offset) {
                int startOffset = offset;
                RawData.WriteU16BE(buf, ref offset, xdrStABN);
                RawData.WriteU16BE(buf, ref offset, xdrNumAblks);
                Debug.Assert(offset == startOffset + LENGTH);
            }

            public static bool operator ==(ExtDescriptor a, ExtDescriptor b) {
                return a.xdrStABN == b.xdrStABN && a.xdrNumAblks == b.xdrNumAblks;
            }
            public static bool operator !=(ExtDescriptor a, ExtDescriptor b) {
                return !(a == b);
            }
            public override bool Equals(object? obj) {
                return obj is ExtDescriptor && this == (ExtDescriptor)obj;
            }
            public override int GetHashCode() {
                return (xdrStABN << 16) | xdrNumAblks;
            }
            public override string ToString() {
                return "[ExtDescriptor st=" + xdrStABN + " num=" + xdrNumAblks + "]";
            }
        }

        /// <summary>
        /// Extent data record, which is simply three extent descriptors.  IM:F 2-75
        /// </summary>
        public class ExtDataRec {
            public const int NUM_RECS = 3;
            public const int LENGTH = ExtDescriptor.LENGTH * NUM_RECS;

            /// <summary>
            /// Descriptor array (3 entries).  Reference to array element is returned (not a copy).
            /// </summary>
            public ExtDescriptor[] Descs { get; private set; }

            /// <summary>
            /// The number of allocation blocks spanned by this record, equal to the sum of
            /// the block counts in each of the extent descriptors.
            /// </summary>
            public ushort AllocBlockSpan {
                get {
                    return (ushort)
                        (Descs[0].xdrNumAblks + Descs[1].xdrNumAblks + Descs[2].xdrNumAblks);
                }
            }
            
            public ExtDataRec() {
                Descs = new ExtDescriptor[NUM_RECS];
                for (int i = 0; i < NUM_RECS; i++) {
                    Descs[i] = new ExtDescriptor();
                }
            }

            /// <summary>
            /// Copy constructor.  Makes a deep copy.
            /// </summary>
            public ExtDataRec(ExtDataRec src) : this() {
                CopyFrom(src);
            }

            public void CopyFrom(ExtDataRec src) {
                for (int i = 0; i < NUM_RECS; i++) {
                    Descs[i].xdrStABN = src.Descs[i].xdrStABN;
                    Descs[i].xdrNumAblks = src.Descs[i].xdrNumAblks;
                }
            }

            public void Load(byte[] buf, ref int offset) {
                int startOffset = offset;
                for (int i = 0; i < Descs.Length; i++) {
                    Descs[i].Load(buf, ref offset);
                }
                Debug.Assert(offset == startOffset + LENGTH);
            }
            public void Store(byte[] buf, ref int offset) {
                int startOffset = offset;
                for (int i = 0; i < Descs.Length; i++) {
                    Descs[i].Store(buf, ref offset);
                }
                Debug.Assert(offset == startOffset + LENGTH);
            }

            public static bool operator ==(ExtDataRec a, ExtDataRec b) {
                for (int i = 0; i < NUM_RECS; i++) {
                    if (a.Descs[i] != b.Descs[i]) {
                        return false;
                    }
                }
                return true;
            }
            public static bool operator !=(ExtDataRec a, ExtDataRec b) {
                return !(a == b);
            }
            public override bool Equals(object? obj) {
                return obj is ExtDataRec && this == (ExtDataRec)obj;
            }
            public override int GetHashCode() {
                int hashCode = 0;
                for (int i = 0; i < NUM_RECS; i++) {
                    hashCode ^= Descs[i].GetHashCode();
                }
                return hashCode;
            }

            public override string ToString() {
                return "[ExtDataRec " +
                    "0:" + Descs[0].xdrStABN + "/" + Descs[0].xdrNumAblks + ", " +
                    "1:" + Descs[1].xdrStABN + "/" + Descs[1].xdrNumAblks + ", " +
                    "2:" + Descs[2].xdrStABN + "/" + Descs[2].xdrNumAblks + "]";
            }
        }

        /// <summary>
        /// Catalog File Data Record: Directory.  IM:F 2-72
        /// </summary>
        public class CatDataDirRec {
            public const int LENGTH = 70;

            /// <summary>Catalog data record type.</summary>
            public byte cdrType;

            /// <summary>Reserved.</summary>
            public byte cdrResrv2;

            /// <summary>Directory flags.  (No values defined?)</summary>
            public ushort dirFlags;

            /// <summary>Valence (number of files in directory).</summary>
            public ushort dirVal;

            /// <summary>Directory CNID.</summary>
            public uint dirDirID;

            /// <summary>Date and time directory was created.</summary>
            public uint dirCrDat;

            /// <summary>Date and time directory was last modified.</summary>
            public uint dirMdDat;

            /// <summary>Date and time directory was last backed up.</summary>
            public uint dirBkDat;

            /// <summary>Information used by Finder.</summary>
            public DInfo dirUsrInfo = new DInfo();

            /// <summary>Additional information used by Finder.</summary>
            public DXInfo dirFndrInfo = new DXInfo();

            /// <summary>Reserved.</summary>
            public uint[] dirResrv = new uint[4];

            public void Load(byte[] buf, int offset) {
                Debug.Assert((offset & 1) == 0);
                int startOffset = offset;
                cdrType = RawData.ReadU8(buf, ref offset);
                cdrResrv2 = RawData.ReadU8(buf, ref offset);
                dirFlags = RawData.ReadU16BE(buf, ref offset);
                dirVal = RawData.ReadU16BE(buf, ref offset);
                dirDirID = RawData.ReadU32BE(buf, ref offset);
                dirCrDat = RawData.ReadU32BE(buf, ref offset);
                dirMdDat = RawData.ReadU32BE(buf, ref offset);
                dirBkDat = RawData.ReadU32BE(buf, ref offset);
                dirUsrInfo.Load(buf, ref offset);
                dirFndrInfo.Load(buf, ref offset);
                for (int i = 0; i < dirResrv.Length; i++) {
                    dirResrv[i] = RawData.ReadU32BE(buf, ref offset);
                }
                Debug.Assert(offset == startOffset + LENGTH);
                Debug.Assert(cdrType == (byte)HFS_Record.CatDataType.Directory);
            }
            public void Store(byte[] buf, int offset) {
                Debug.Assert((offset & 1) == 0);
                int startOffset = offset;
                RawData.WriteU8(buf, ref offset, cdrType);
                RawData.WriteU8(buf, ref offset, cdrResrv2);
                RawData.WriteU16BE(buf, ref offset, dirFlags);
                RawData.WriteU16BE(buf, ref offset, dirVal);
                RawData.WriteU32BE(buf, ref offset, dirDirID);
                RawData.WriteU32BE(buf, ref offset, dirCrDat);
                RawData.WriteU32BE(buf, ref offset, dirMdDat);
                RawData.WriteU32BE(buf, ref offset, dirBkDat);
                dirUsrInfo.Store(buf, ref offset);
                dirFndrInfo.Store(buf, ref offset);
                for (int i = 0; i < dirResrv.Length; i++) {
                    RawData.WriteU32BE(buf, ref offset, dirResrv[i]);
                }
                Debug.Assert(offset == startOffset + LENGTH);
                Debug.Assert(cdrType == (byte)HFS_Record.CatDataType.Directory);
            }
        }

        /// <summary>
        /// Catalog File Data Record: File.  IM:F 2-72
        /// </summary>
        public class CatDataFilRec {
            public const int LENGTH = 102;

            /// <summary>Catalog data record type.</summary>
            public byte cdrType;

            /// <summary>Reserved.</summary>
            public byte cdrResrv2;

            /// <summary>
            /// File flags:
            /// <list type="bullet">
            ///   <item>bit 0 (0x01): file is locked and cannot be written to</item>
            ///   <item>bit 1 (0x02): a file thread record exists for this file</item>
            ///   <item>bit 7 (0x80): "if set, the file record is used"</item>
            /// </list>
            /// </summary>
            public byte filFlags;
            public const byte FLAG_LOCKED = 0x01;

            /// <summary><para>File type.</para><para>NOTE: not used, set to zero.</para></summary>
            public byte filTyp;

            /// <summary>Information used by Finder.</summary>
            public FInfo filUsrWds = new FInfo();

            /// <summary>File CNID.</summary>
            public uint filFlNum;

            /// <summary><para>First allocation block of the data fork.</para>
            /// <para>NOTE: this field is obsolete, and should be set to zero.  (fsck.hfs
            /// considers it a reserved field, and forces it to zero.)</para></summary>
            public ushort filStBlk;

            /// <summary>Logical EOF of the data fork.</summary>
            public uint filLgLen;

            /// <summary>Physical EOF of the data fork.</summary>
            public uint filPyLen;

            /// <summary><para>First allocation block of the resource fork.</para>
            /// <para>NOTE: this field is obsolete, and should be set to zero.</para></summary>
            public ushort filRStBlk;

            /// <summary>Logical EOF of the resource fork.</summary>
            public uint filRLgLen;

            /// <summary>Physical EOF of the resource fork.</summary>
            public uint filRPyLen;

            /// <summary>Date/time when the file was created.</summary>
            public uint filCrDat;

            /// <summary>Date/time when the file was last modified.</summary>
            public uint filMdDat;

            /// <summary>Date/time when the file was last backed up.</summary>
            public uint filBkDat;

            /// <summary>Additional information used by Finder.</summary>
            public FXInfo filFndrInfo = new FXInfo();

            /// <summary><para>File's clump size.</para><para>NOTE: not used.</para></summary>
            public ushort filClpSize;

            /// <summary>First extent record of the file's data fork.</summary>
            public ExtDataRec filExtRec = new ExtDataRec();

            /// <summary>First extent record of the file's resource fork.</summary>
            public ExtDataRec filRExtRec = new ExtDataRec();

            /// <summary>Reserved.</summary>
            public uint filResrv;

            public void Load(byte[] buf, int offset) {
                Debug.Assert((offset & 1) == 0);
                int startOffset = offset;
                cdrType = RawData.ReadU8(buf, ref offset);
                cdrResrv2 = RawData.ReadU8(buf, ref offset);
                filFlags = RawData.ReadU8(buf, ref offset);
                filTyp = RawData.ReadU8(buf, ref offset);
                filUsrWds.Load(buf, ref offset);
                filFlNum = RawData.ReadU32BE(buf, ref offset);
                filStBlk = RawData.ReadU16BE(buf, ref offset);
                filLgLen = RawData.ReadU32BE(buf, ref offset);
                filPyLen = RawData.ReadU32BE(buf, ref offset);
                filRStBlk = RawData.ReadU16BE(buf, ref offset);
                filRLgLen = RawData.ReadU32BE(buf, ref offset);
                filRPyLen = RawData.ReadU32BE(buf, ref offset);
                filCrDat = RawData.ReadU32BE(buf, ref offset);
                filMdDat = RawData.ReadU32BE(buf, ref offset);
                filBkDat = RawData.ReadU32BE(buf, ref offset);
                filFndrInfo.Load(buf, ref offset);
                filClpSize = RawData.ReadU16BE(buf, ref offset);
                filExtRec.Load(buf, ref offset);
                filRExtRec.Load(buf, ref offset);
                filResrv = RawData.ReadU32BE(buf, ref offset);
                Debug.Assert(offset == startOffset + LENGTH);
                Debug.Assert(cdrType == (byte)HFS_Record.CatDataType.File);
            }
            public void Store(byte[] buf, int offset) {
                Debug.Assert((offset & 1) == 0);
                int startOffset = offset;
                RawData.WriteU8(buf, ref offset, cdrType);
                RawData.WriteU8(buf, ref offset, cdrResrv2);
                RawData.WriteU8(buf, ref offset, filFlags);
                RawData.WriteU8(buf, ref offset, filTyp);
                filUsrWds.Store(buf, ref offset);
                RawData.WriteU32BE(buf, ref offset, filFlNum);
                RawData.WriteU16BE(buf, ref offset, filStBlk);
                RawData.WriteU32BE(buf, ref offset, filLgLen);
                RawData.WriteU32BE(buf, ref offset, filPyLen);
                RawData.WriteU16BE(buf, ref offset, filRStBlk);
                RawData.WriteU32BE(buf, ref offset, filRLgLen);
                RawData.WriteU32BE(buf, ref offset, filRPyLen);
                RawData.WriteU32BE(buf, ref offset, filCrDat);
                RawData.WriteU32BE(buf, ref offset, filMdDat);
                RawData.WriteU32BE(buf, ref offset, filBkDat);
                filFndrInfo.Store(buf, ref offset);
                RawData.WriteU16BE(buf, ref offset, filClpSize);
                filExtRec.Store(buf, ref offset);
                filRExtRec.Store(buf, ref offset);
                RawData.WriteU32BE(buf, ref offset, filResrv);
                Debug.Assert(offset == startOffset + LENGTH);
                Debug.Assert(cdrType == (byte)HFS_Record.CatDataType.File);
            }
        }

        /// <summary>
        /// Catalog File Data Record: Directory or File Thread.  IM:F 2-73
        /// </summary>
        public class CatDataThreadRec {
            public const int LENGTH = 46;

            /// <summary>Catalog data record type.</summary>
            public byte cdrType;

            /// <summary>Reserved.</summary>
            public byte cdrResrv2;

            /// <summary>Reserved.</summary>
            public uint[] thdResrv = new uint[2];

            /// <summary>CNID of the parent of the associated directory or file.</summary>
            public uint thdParID;

            /// <summary>Name of this directory or file.</summary>
            public byte[] thdCName = new byte[HFS.MAX_FILE_NAME_LEN + 1];

            public void Load(byte[] buf, ref int offset) {
                Debug.Assert((offset & 1) == 0);
                int startOffset = offset;
                cdrType = RawData.ReadU8(buf, ref offset);
                cdrResrv2 = RawData.ReadU8(buf, ref offset);
                for (int i = 0; i < thdResrv.Length; i++) {
                    thdResrv[i] = RawData.ReadU32BE(buf, ref offset);
                }
                thdParID = RawData.ReadU32BE(buf, ref offset);
                Array.Copy(buf, offset, thdCName, 0, thdCName.Length);
                offset += thdCName.Length;
                Debug.Assert(offset == startOffset + LENGTH);
                Debug.Assert(cdrType == (byte)HFS_Record.CatDataType.DirectoryThread ||
                             cdrType == (byte)HFS_Record.CatDataType.FileThread);
            }
            public void Store(byte[] buf, ref int offset) {
                Debug.Assert((offset & 1) == 0);
                int startOffset = offset;
                RawData.WriteU8(buf, ref offset, cdrType);
                RawData.WriteU8(buf, ref offset, cdrResrv2);
                for (int i = 0; i < thdResrv.Length; i++) {
                    RawData.WriteU32BE(buf, ref offset, thdResrv[i]);
                }
                RawData.WriteU32BE(buf, ref offset, thdParID);
                Array.Copy(thdCName, 0, buf, offset, thdCName.Length);
                offset += thdCName.Length;
                Debug.Assert(offset == startOffset + LENGTH);
                Debug.Assert(cdrType == (byte)HFS_Record.CatDataType.DirectoryThread ||
                             cdrType == (byte)HFS_Record.CatDataType.FileThread);
            }
        }

        /// <summary>
        /// Directory Information Record.  IM:MTE 7-50
        /// </summary>
        public class DInfo {
            public const int LENGTH = 16;

            public static readonly DInfo ZERO = new DInfo();    // leave all fields init to zero

            /// <summary>Rectangle for the window the Finder displays when the directory is
            /// opened (top).</summary>
            public ushort frRectT;

            /// <summary>Rectangle for the window the Finder displays when the directory is
            /// opened (left).</summary>
            public ushort frRectL;

            /// <summary>Rectangle for the window the Finder displays when the directory is
            /// opened (bottom).</summary>
            public ushort frRectB;

            /// <summary>Rectangle for the window the Finder displays when the directory is
            /// opened (right).</summary>
            public ushort frRectR;

            /// <summary>Reserved.</summary>
            public ushort frFlags;

            /// <summary>Location of folder in parent window (vertical).</summary>
            public ushort frLocationV;

            /// <summary>Location of folder in parent window (horizontal).</summary>
            public ushort frLocationH;

            /// <summary>The manner in which folders are displayed.</summary>
            public ushort frView;

            public void Load(byte[] buf, ref int offset) {
                int startOffset = offset;
                frRectT = RawData.ReadU16BE(buf, ref offset);
                frRectL = RawData.ReadU16BE(buf, ref offset);
                frRectB = RawData.ReadU16BE(buf, ref offset);
                frRectR = RawData.ReadU16BE(buf, ref offset);
                frFlags = RawData.ReadU16BE(buf, ref offset);
                frLocationV = RawData.ReadU16BE(buf, ref offset);
                frLocationH = RawData.ReadU16BE(buf, ref offset);
                frView = RawData.ReadU16BE(buf, ref offset);
                Debug.Assert(offset == startOffset + LENGTH);
            }
            public void Store(byte[] buf, ref int offset) {
                int startOffset = offset;
                RawData.WriteU16BE(buf, ref offset, frRectT);
                RawData.WriteU16BE(buf, ref offset, frRectL);
                RawData.WriteU16BE(buf, ref offset, frRectB);
                RawData.WriteU16BE(buf, ref offset, frRectR);
                RawData.WriteU16BE(buf, ref offset, frFlags);
                RawData.WriteU16BE(buf, ref offset, frLocationV);
                RawData.WriteU16BE(buf, ref offset, frLocationH);
                RawData.WriteU16BE(buf, ref offset, frView);
                Debug.Assert(offset == startOffset + LENGTH);
            }
        }

        /// <summary>
        /// Extended Directory Information Record.  IM:MTE 7-50
        /// </summary>
        public class DXInfo {
            public const int LENGTH = 16;

            public static readonly DXInfo ZERO = new DXInfo();  // leave all fields init to zero

            /// <summary>Scroll position within the Finder window (vertical).</summary>
            public ushort frScrollV;

            /// <summary>Scroll position within the Finder window (horizontal).</summary>
            public ushort frScrollH;

            /// <summary>Chain of directory IDs for open folders.</summary>
            public uint frOpenChain;

            /// <summary>Script system for displaying the file's name.</summary>
            public byte frScript;

            /// <summary>Reserved.</summary>
            public byte frXFlags;

            /// <summary>ID number for the file comment.</summary>
            public ushort frComment;

            /// <summary>Directory ID of folder from which a file on the desktop came.</summary>
            public uint frPutAway;

            public void Load(byte[] buf, ref int offset) {
                int startOffset = offset;
                frScrollV = RawData.ReadU16BE(buf, ref offset);
                frScrollH = RawData.ReadU16BE(buf, ref offset);
                frOpenChain = RawData.ReadU32BE(buf, ref offset);
                frScript = RawData.ReadU8(buf, ref offset);
                frXFlags = RawData.ReadU8(buf, ref offset);
                frComment = RawData.ReadU16BE(buf, ref offset);
                frPutAway = RawData.ReadU32BE(buf, ref offset);
                Debug.Assert(offset == startOffset + LENGTH);
            }
            public void Store(byte[] buf, ref int offset) {
                int startOffset = offset;
                RawData.WriteU16BE(buf, ref offset, frScrollV);
                RawData.WriteU16BE(buf, ref offset, frScrollH);
                RawData.WriteU32BE(buf, ref offset, frOpenChain);
                RawData.WriteU8(buf, ref offset, frScript);
                RawData.WriteU8(buf, ref offset, frXFlags);
                RawData.WriteU16BE(buf, ref offset, frComment);
                RawData.WriteU32BE(buf, ref offset, frPutAway);
                Debug.Assert(offset == startOffset + LENGTH);
            }
        }

        /// <summary>
        /// File Information Record.  IM:MTE 7-47
        /// </summary>
        public class FInfo {
            public const int LENGTH = 16;

            public static readonly FInfo ZERO = new FInfo();    // leave all fields init to zero

            /// <summary>File type, often a 4-character value.</summary>
            public uint fdType;

            /// <summary>File creator signature.</summary>
            public uint fdCreator;

            /// <summary>
            /// Finder flags:
            /// <list type="bullet">
            ///   <item>bit 15 (0x8000): for files, indicates an alias file; 0 for dir</item>
            ///   <item>bit 14 (0x4000): file or directory is invisible</item>
            ///   <item>bit 13 (0x2000): for files, indicates file contains a bundle resource;
            ///     0 for dir</item>
            ///   <item>bit 12 (0x1000): filename and icon are locked</item>
            ///   <item>bit 11 (0x0800): file is a stationery pad; 0 for dir</item>
            ///   <item>bit 10 (0x0400): file or directory has a customized icon</item>
            ///   <item>bit  9 (0x0200): reserved, set to 0</item>
            ///   <item>bit  8 (0x0100): file's bundle resource has been initialized</item>
            ///   <item>bit  7 (0x0080): file contains no 'INIT' resources; 0 for dir</item>
            ///   <item>bit  6 (0x0040): file is an app that can be executed by multiple users;
            ///     0 for non-apps and dirs</item>
            ///   <item>bit  5 (0x0020): reserved, set to 0</item>
            ///   <item>bit  4 (0x0010): reserved, set to 0</item>
            ///   <item>bit  3 (0x0008): color coding</item>
            ///   <item>bit  2 (0x0004): color coding</item>
            ///   <item>bit  1 (0x0002): color coding</item>
            ///   <item>bit  0 (0x0001): reserved, set to 0</item>
            /// </list>
            /// </summary>
            public ushort fdFlags;

            /// <summary>File icon position within its window (vertical).</summary>
            public ushort fdLocationV;

            /// <summary>File icon position within its window (horizontal).</summary>
            public ushort fdLocationH;

            /// <summary>Window in which file's icon appears.</summary>
            public ushort fdFldr;

            public void Load(byte[] buf, ref int offset) {
                int startOffset = offset;
                fdType = RawData.ReadU32BE(buf, ref offset);
                fdCreator = RawData.ReadU32BE(buf, ref offset);
                fdFlags = RawData.ReadU16BE(buf, ref offset);
                fdLocationV = RawData.ReadU16BE(buf, ref offset);
                fdLocationH = RawData.ReadU16BE(buf, ref offset);
                fdFldr = RawData.ReadU16BE(buf, ref offset);
                Debug.Assert(offset == startOffset + LENGTH);
            }
            public void Store(byte[] buf, ref int offset) {
                int startOffset = offset;
                RawData.WriteU32BE(buf, ref offset, fdType);
                RawData.WriteU32BE(buf, ref offset, fdCreator);
                RawData.WriteU16BE(buf, ref offset, fdFlags);
                RawData.WriteU16BE(buf, ref offset, fdLocationV);
                RawData.WriteU16BE(buf, ref offset, fdLocationH);
                RawData.WriteU16BE(buf, ref offset, fdFldr);
                Debug.Assert(offset == startOffset + LENGTH);
            }
        }

        /// <summary>
        /// Extended File Information Record.  IM:MTE 7-49
        /// </summary>
        public class FXInfo {
            public const int LENGTH = 16;

            public static readonly FXInfo ZERO = new FXInfo();  // leave all fields init to zero

            /// <summary>ID number for the file's icon.</summary>
            public ushort fdIconID;
            /// <summary>Reserved.</summary>
            public ushort[] fdUnused = new ushort[3];
            /// <summary>Script system for displaying the file's name.</summary>
            public byte fdScript;
            /// <summary>Reserved.</summary>
            public byte fdXFlags;
            /// <summary>ID number for the file comment.</summary>
            public ushort fdComment;
            /// <summary>Directory ID of folder from which a file on the desktop came.</summary>
            public uint fdPutAway;

            public void Load(byte[] buf, ref int offset) {
                int startOffset = offset;
                fdIconID = RawData.ReadU16BE(buf, ref offset);
                for (int i = 0; i < fdUnused.Length; i++) {
                    fdUnused[i] = RawData.ReadU16BE(buf, ref offset);
                }
                fdScript = RawData.ReadU8(buf, ref offset);
                fdXFlags = RawData.ReadU8(buf, ref offset);
                fdComment = RawData.ReadU16BE(buf, ref offset);
                fdPutAway = RawData.ReadU32BE(buf, ref offset);
                Debug.Assert(offset == startOffset + LENGTH);
            }
            public void Store(byte[] buf, ref int offset) {
                int startOffset = offset;
                RawData.WriteU16BE(buf, ref offset, fdIconID);
                for (int i = 0; i < fdUnused.Length; i++) {
                    RawData.WriteU16BE(buf, ref offset, fdUnused[i]);
                }
                RawData.WriteU8(buf, ref offset, fdScript);
                RawData.WriteU8(buf, ref offset, fdXFlags);
                RawData.WriteU16BE(buf, ref offset, fdComment);
                RawData.WriteU32BE(buf, ref offset, fdPutAway);
                Debug.Assert(offset == startOffset + LENGTH);
            }
        }
    }
}
