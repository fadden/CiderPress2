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
using DiskArc.Arc;

namespace DiskArc {
    /// <summary>
    /// <para>File attribute holder, for times when you need a collection of file attributes that
    /// aren't tied to an IFileEntry.  Some entries receive additional processing, e.g. ProDOS
    /// file types stored in HFS type fields are transferred to the ProDOS fields.</para>
    /// <para>Also has a collection of useful constants.</para>
    /// </summary>
    public class FileAttribs {
        #region Constants

        // Some common ProDOS file type constants, for use by application or test code.
        public const int FILE_TYPE_NON = 0x00;
        public const int FILE_TYPE_TXT = 0x04;
        public const int FILE_TYPE_BIN = 0x06;
        public const int FILE_TYPE_FOT = 0x08;
        public const int FILE_TYPE_DIR = 0x0f;
        public const int FILE_TYPE_GWP = 0x50;
        public const int FILE_TYPE_S16 = 0xb3;
        public const int FILE_TYPE_PNT = 0xc0;
        public const int FILE_TYPE_PIC = 0xc1;
        public const int FILE_TYPE_MDI = 0xd7;
        public const int FILE_TYPE_SND = 0xd8;
        public const int FILE_TYPE_LBR = 0xe0;
        public const int FILE_TYPE_F2 = 0xf2;
        public const int FILE_TYPE_F3 = 0xf3;
        public const int FILE_TYPE_F4 = 0xf4;
        public const int FILE_TYPE_INT = 0xfa;
        public const int FILE_TYPE_BAS = 0xfc;
        public const int FILE_TYPE_REL = 0xfe;
        public const int FILE_TYPE_SYS = 0xff;

        // Some HFS file and creator types.
        public const uint CREATOR_CPII = 0x43504949;        // 'CPII' (CiderPress II)
        public const uint CREATOR_PDOS = 0x70646f73;        // 'pdos'
        public const uint CREATOR_DCPY = 0x64437079;        // 'dCpy'
        public const uint TYPE_AIFC = 0x41494643;           // 'AIFC'
        public const uint TYPE_AIFF = 0x41494646;           // 'AIFF'
        public const uint TYPE_BINA = 0x42494e41;           // 'BINA'
        public const uint TYPE_DIMG = 0x64496d67;           // 'dImg'
        public const uint TYPE_MIDI = 0x4d494449;           // 'MIDI'
        public const uint TYPE_PSYS = 0x50535953;           // 'PSYS'
        public const uint TYPE_PS16 = 0x50533136;           // 'PS16'
        public const uint TYPE_TEXT = 0x54455854;           // 'TEXT'

        /// <summary>
        /// Access flags, based on ProDOS / GS/OS definition.
        /// </summary>
        [Flags]
        public enum AccessFlags : byte {
            Read        = 0x01,
            Write       = 0x02,
            Invisible   = 0x04,
            Backup      = 0x20,
            Rename      = 0x40,
            Delete      = 0x80,
        }

        // Common configurations.  On disk, access flags will usually include the "backup" bit.
        public const byte FILE_ACCESS_LOCKED = (byte) AccessFlags.Read;
        public const byte FILE_ACCESS_UNLOCKED = (byte)
            (AccessFlags.Read |
            AccessFlags.Write |
            AccessFlags.Rename |
            AccessFlags.Delete);

        #endregion Constants

        //
        // This used to be in AppCommon's ExtractFileWorker, but it was more generally useful.
        // The AppleSingle stuff feels a little out of place here, and might be more appropriate
        // as extension methods in the cp2 app.
        //

        public string FullPathName { get; set; } = string.Empty;
        public char FullPathSep { get; set; } = IFileEntry.NO_DIR_SEP;
        public string FileNameOnly { get; set; } = string.Empty;
        public byte FileType { get; set; }
        public ushort AuxType { get; set; }
        public uint HFSFileType { get; set; }
        public uint HFSCreator { get; set; }
        public byte Access { get; set; }
        public DateTime CreateWhen { get; set; } = TimeStamp.NO_DATE;
        public DateTime ModWhen { get; set; } = TimeStamp.NO_DATE;

        public long DataLength { get; set; }
        public long RsrcLength { get; set; }

        /// <summary>
        /// True if we have a nonzero value in one or more file type fields.
        /// </summary>
        public bool HasTypeInfo {
            get {
                return FileType != 0 || AuxType != 0 || HFSFileType != 0 || HFSCreator != 0;
            }
        }

        /// <summary>
        /// Nullary constructor.  Creates object with all fields set to defaults.
        /// </summary>
        public FileAttribs() { }

        /// <summary>
        /// Copy constructor.
        /// </summary>
        public FileAttribs(FileAttribs src) {
            FullPathName = src.FullPathName;
            FullPathSep = src.FullPathSep;
            FileNameOnly = src.FileNameOnly;
            FileType = src.FileType;
            AuxType = src.AuxType;
            HFSFileType = src.HFSFileType;
            HFSCreator = src.HFSCreator;
            Access = src.Access;
            CreateWhen = src.CreateWhen;
            ModWhen = src.ModWhen;
            DataLength = src.DataLength;
            RsrcLength = src.RsrcLength;
        }

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="entry">IFileEntry to draw attributes from.</param>
        public FileAttribs(IFileEntry entry) {
            FullPathName = entry.FullPathName;
            // Want just the filename, not the partial path.  Trim if necessary.
            FullPathSep = entry.DirectorySeparatorChar;
            FileNameOnly = entry.FileName;
            DataLength = entry.DataLength;
            RsrcLength = entry.RsrcLength;
            if (FullPathSep != IFileEntry.NO_DIR_SEP) {
                int lastIndex = entry.FileName.LastIndexOf(FullPathSep);
                if (lastIndex > 0 && lastIndex != entry.FileName.Length - 1) {
                    FileNameOnly = entry.FileName.Substring(lastIndex + 1);
                }
            }
            CopyAttrsFrom(entry);
        }

        /// <summary>
        /// Overwrites attributes with values from an AppleSingle/AppleDouble file.
        /// </summary>
        /// <param name="asStream">Stream with AppleSingle/AppleDouble data.</param>
        /// <param name="appHook">Application hook reference.</param>
        public void GetFromAppleSingle(Stream asStream, AppHook appHook) {
            using (AppleSingle asArc = AppleSingle.OpenArchive(asStream, appHook)) {
                IFileEntry asEntry = asArc.GetFirstEntry();
                GetFromAppleSingle(asEntry);
            }
        }

        /// <summary>
        /// Overwrites attributes with values from an AppleSingle/AppleDouble file.
        /// </summary>
        /// <param name="entry">Entry from AppleSingle/AppleDouble archive.</param>
        public void GetFromAppleSingle(IFileEntry entry) {
            Debug.Assert(entry is AppleSingle_FileEntry);
            FileNameOnly = entry.FileName;    // may be empty; not really used
            RsrcLength = entry.RsrcLength;
            CopyAttrsFrom(entry);
        }

        /// <summary>
        /// Copies file attributes out of an IFileEntry.  If the entry lacks ProDOS types,
        /// but has ProDOS types stored in HFS fields, extract them.
        /// </summary>
        private void CopyAttrsFrom(IFileEntry entry) {
            HFSFileType = entry.HFSFileType;
            HFSCreator = entry.HFSCreator;
            if (entry.FileType == 0 && entry.AuxType == 0 && entry.HasHFSTypes &&
                    ProDOSFromHFS(entry.HFSFileType, entry.HFSCreator,
                        out byte proType, out ushort proAux)) {
                // ProDOS encoded in HFS.
                FileType = proType;
                AuxType = proAux;
            } else {
                // Keep existing values.
                FileType = entry.FileType;
                AuxType = entry.AuxType;
            }
            Access = entry.Access;
            // Only overwrite dates if we have a value.
            if (TimeStamp.IsValidDate(entry.CreateWhen)) {
                CreateWhen = entry.CreateWhen;
            }
            if (TimeStamp.IsValidDate(entry.ModWhen)) {
                ModWhen = entry.ModWhen;
            }
        }

        /// <summary>
        /// Copies file attributes to an IFileEntry.  We use this when extracting to ADF/AS,
        /// and when applying attribute changes.
        /// </summary>
        /// <param name="entry">Entry to copy entries into.</param>
        /// <param name="useFileNameOnly">If true, we use the FileNameOnly field as the new filename
        ///   instead of the FullPathName field.</param>
        public void CopyAttrsTo(IFileEntry entry, bool useFileNameOnly) {
            if (useFileNameOnly) {
                entry.FileName = FileNameOnly;
            } else {
                entry.FileName = FullPathName;
            }
            entry.FileType = FileType;
            entry.AuxType = AuxType;
            entry.HFSFileType = HFSFileType;
            entry.HFSCreator = HFSCreator;
            entry.Access = Access;
            entry.CreateWhen = CreateWhen;
            entry.ModWhen = ModWhen;
        }


        //
        // The definitive document for ProDOS/HFS file type interaction appears to be "GS/OS
        // AppleShare File System Translator External ERS, Version 0.26CD" (March 5 1992), part
        // of the IIgs System 6.0 release notes.
        //
        // AppleShare 2.0: ProDOS to Macintosh conversion:
        //  ProDOS type/aux            Macintosh creator/type
        //   $00 / $0000                'pdos' 'BINA'
        //   $B0 (SRC) / any            'pdos' 'TEXT'
        //   $04 (TXT) / $0000          'pdos' 'TEXT'
        //   $FF (SYS) / any            'pdos' 'PSYS'
        //   $B3 (S16) / any            'pdos' 'PS16'
        //   $uv / $wxyz                'pdos' 'p' $uv $wx $yz
        //
        // AppleShare 2.0: Macintosh to ProDOS conversion:
        //  Macintosh creator/type     ProDOS type/aux
        //   (any) / 'BINA'             $00 / $0000
        //   (any) / 'TEXT'             $04 (TXT) / $0000
        //   'pdos' / 'PSYS'            $FF (SYS) / $0000
        //   'pdos' / 'PS16'            $B3 (S16) / $0000
        //   'pdos' / 'XY  '            $XY / $0000
        //   'pdos' / 'p' $uv $wx $yz   $uv / $wxyz
        //   (any) / (any)              $00 / $0000
        //
        // System 6.0 FST: ProDOS to Macintosh conversion (extends/overrules AppleShare):
        //  ProDOS type/aux            Macintosh creator/type
        //   $B3 / $DBxy                'pdos' 'p' $B3 $DB $xy
        //   $D7 / $0000                'pdos' 'MIDI'
        //   $D8 / $0000                'pdos' 'AIFF'
        //   $D8 / $0001                'pdos' 'AIFC'
        //
        // System 6.0 FST: Macintosh to ProDOS conversion (extends AppleShare):
        //  Macintosh creator/type     ProDOS type/aux
        //   (any) / 'MIDI'             $D7 / $0000
        //   (any) / 'AIFF'             $D8 / $0000
        //   (any) / 'AIFC'             $D8 / $0001
        //
        // Not mentioned in the FST documentation: DiskCopy files are stored as dCpy/dImg, which
        // converts to $e0/0005.
        //
        // (This was initially in AppCommon, but the FileConv library wants it too.)

        /// <summary>
        /// Converts HFS creator/filetype to ProDOS type/auxtype.
        /// </summary>
        /// <param name="hfsCreator">HFS creator.</param>
        /// <param name="hfsType">HFS filetype.</param>
        /// <param name="proType">Result: ProDOS file type, or 0 if no conversion.</param>
        /// <param name="proAux">Result: ProDOS aux type, or 0 if no conversion.</param>
        /// <returns>True if the creator was 'pdos', indicating that the ProDOS type should be
        ///   preferred over the HFS type.</returns>
        public static bool ProDOSFromHFS(uint hfsType, uint hfsCreator,
                out byte proType, out ushort proAux) {
            bool isPdos = (hfsCreator == CREATOR_PDOS);

            // Handle the file types for which the creator type doesn't matter.
            proAux = 0x0000;
            switch (hfsType) {
                case TYPE_BINA:
                    proType = FileAttribs.FILE_TYPE_NON;
                    return isPdos;
                case TYPE_TEXT:
                    proType = FileAttribs.FILE_TYPE_TXT;
                    return isPdos;
                case TYPE_MIDI:
                    proType = FileAttribs.FILE_TYPE_MDI;
                    return isPdos;
                case TYPE_AIFF:
                    proType = FileAttribs.FILE_TYPE_SND;
                    return isPdos;
                case TYPE_AIFC:
                    proType = FileAttribs.FILE_TYPE_SND;
                    proAux = 0x0001;
                    return isPdos;
            }

            // DiskCopy special.
            if (hfsCreator == CREATOR_DCPY && hfsType == TYPE_DIMG) {
                proType = 0xe0;
                proAux = 0x0005;
                return false;
            }

            // Handle the 'pdos' conversions.
            if (hfsCreator == CREATOR_PDOS) {
                proAux = proType = 0;
                if ((byte)(hfsType >> 24) == (byte)'p') {
                    // Format used by AppleShare and HFS FST: 'p' $uv $wx $yz --> type=$uv aux=$wxyz
                    proType = (byte)(hfsType >> 16);
                    proAux = (ushort)hfsType;
                } else if ((hfsType & 0xffff) == 0x2020) {
                    // Less-common format: "XY  " --> type=$XY aux=$0000
                    // 'X' and 'Y' must be [0-9][A-F].
                    int digit1 = CharToHex((byte)(hfsType >> 24));
                    int digit2 = CharToHex((byte)(hfsType >> 16));
                    if (digit1 < 0 || digit2 < 0) {
                        Debug.WriteLine("Found bad XY file type");
                    } else {
                        proType = (byte)((digit1 << 4) | digit2);
                    }
                } else {
                    // Check specific file types that are 'pdos'-only.
                    switch (hfsType) {
                        case TYPE_PSYS:
                            proType = FileAttribs.FILE_TYPE_SYS;
                            break;
                        case TYPE_PS16:
                            proType = FileAttribs.FILE_TYPE_S16;
                            break;
                    }
                }
                return true;
            }

            // No meaningful conversion, use NON/$0000.
            proType = 0;
            return false;
        }

        private static int CharToHex(byte hexChar) {
            if (hexChar >= '0' && hexChar <= '9')
                return hexChar - '0';
            else if (hexChar >= 'a' && hexChar <= 'f')
                return hexChar - 'a' + 10;
            else if (hexChar >= 'A' && hexChar <= 'F')
                return hexChar - 'A' + 10;
            else
                return -1;
        }

        /// <summary>
        /// Encodes a ProDOS file type in the HFS file type / creator fields.
        /// </summary>
        /// <param name="proType">ProDOS file type.</param>
        /// <param name="proAux">ProDOS auxiliary type.</param>
        /// <param name="hfsFileType">Result: HFS file type.</param>
        /// <param name="hfsCreator">Result: HFS creator type.</param>
        public static void ProDOSToHFS(byte proType, ushort proAux, out uint hfsFileType,
                out uint hfsCreator) {
            hfsCreator = CREATOR_PDOS;

            if (proType == FileAttribs.FILE_TYPE_NON && proAux == 0x0000) {
                hfsFileType = TYPE_BINA;
            } else if (proType == FileAttribs.FILE_TYPE_TXT && proAux == 0x0000) {
                hfsFileType = TYPE_TEXT;
            } else if (proType == FileAttribs.FILE_TYPE_SYS) {
                hfsFileType = TYPE_PSYS;
            } else if (proType == FileAttribs.FILE_TYPE_S16 && (proAux & 0xff00) != 0xdb00) {
                hfsFileType = TYPE_PS16;
            } else if (proType == FileAttribs.FILE_TYPE_MDI && proAux == 0x0000) {
                hfsFileType = TYPE_MIDI;
            } else if (proType == FileAttribs.FILE_TYPE_SND && proAux == 0x0000) {
                hfsFileType = TYPE_AIFF;
            } else if (proType == FileAttribs.FILE_TYPE_SND && proAux == 0x0001) {
                hfsFileType = TYPE_AIFC;
            } else if (proType == FileAttribs.FILE_TYPE_LBR && proAux == 0x0005) {
                hfsCreator = CREATOR_DCPY;
                hfsFileType = TYPE_DIMG;
            } else {
                hfsFileType = (byte)'p' << 24;
                hfsFileType |= (uint)(proType << 16);
                hfsFileType |= proAux;
            }
        }
    }
}
