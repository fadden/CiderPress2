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

using CommonUtil;
using DiskArc;

namespace AppCommon {
    /// <summary>
    /// File type utility functions.
    /// </summary>
    public static class FileTypes {
        /// <summary>
        /// Gets the 3-character ProDOS filetype abbreviation string.
        /// </summary>
        /// <param name="fileType">ProDOS file type, 0-255.</param>
        /// <returns>The abbreviation string, or an empty string if the file type value is out
        ///   of range.</returns>
        public static string GetFileTypeAbbrev(int fileType) {
            if (fileType >= 0 && fileType <= 255) {
                return sFileTypeNames[fileType];
            } else {
                return string.Empty;
            }
        }

        /// <summary>
        /// Gets a 1- or 2-character value for the DOS file type.
        /// </summary>
        /// <param name="fileType">ProDOS equivalent file type.</param>
        /// <returns>Abbreviation string.</returns>
        public static string GetDOSTypeAbbrev(int fileType) {
            switch (fileType) {
                case FileAttribs.FILE_TYPE_TXT:
                    return "T";
                case FileAttribs.FILE_TYPE_INT:
                    return "I";
                case FileAttribs.FILE_TYPE_BAS:
                    return "A";
                case FileAttribs.FILE_TYPE_BIN:
                    return "B";
                case FileAttribs.FILE_TYPE_F2:
                    return "S";
                case FileAttribs.FILE_TYPE_REL:
                    return "R";
                case FileAttribs.FILE_TYPE_F3:
                    return "AA";
                case FileAttribs.FILE_TYPE_F4:
                    return "BB";
                default:
                    return "?";
            }
        }

        /// <summary>
        /// Gets a short value for the Pascal file type.
        /// </summary>
        /// <param name="fileType">ProDOS equivalent file type.</param>
        /// <returns>Abbreviation string.</returns>
        public static string GetPascalTypeName(int fileType) {
            switch (fileType) {
                case FileAttribs.FILE_TYPE_NON:
                    return "Untyped";
                case FileAttribs.FILE_TYPE_BAD:
                    return "Bad";
                case FileAttribs.FILE_TYPE_PCD:
                    return "Code";
                case FileAttribs.FILE_TYPE_PTX:
                    return "Text";
                case FileAttribs.FILE_TYPE_F3:
                    return "Info";
                case FileAttribs.FILE_TYPE_PDA:
                    return "Data";
                case FileAttribs.FILE_TYPE_F4:
                    return "Graf";
                case FileAttribs.FILE_TYPE_FOT:
                    return "Foto";
                case FileAttribs.FILE_TYPE_F5:
                    return "S.dir";
                default:
                    return "?";
            }
        }

        /// <summary>
        /// Finds a ProDOS file type by 3-letter abbreviation.  Two-letter strings will have a
        /// space appended (for "OS " and "WP ").
        /// </summary>
        /// <param name="abbrev">Two- or three-character file type abbreviation.</param>
        /// <returns>File type (0-255), or -1 on failure.</returns>
        public static int GetFileTypeByAbbrev(string abbrev) {
            if (abbrev.Length < 2 || abbrev.Length > 3) {
                return -1;
            }
            string modAbbrev = abbrev.ToUpper();
            if (modAbbrev.Length == 2) {
                modAbbrev += " ";
            }
            for (int i = 0; i < sFileTypeNames.Length; i++) {
                if (sFileTypeNames[i] == modAbbrev) {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>
        /// Gets the description of the file contents.
        /// </summary>
        /// <param name="type">ProDOS file type.</param>
        /// <param name="auxType">ProDOS auxiliary type.</param>
        /// <returns>String with description, or an empty string if there's no match.</returns>
        public static string GetDescription(byte type, ushort auxType) {
            for (int i = sDescs.Length - 1; i >= 0; i--) {
                if (type == sDescs[i].mFileType &&
                        auxType >= sDescs[i].mMinAuxType &&
                        auxType <= sDescs[i].mMaxAuxType) {
                    return sDescs[i].mDesc;
                }
            }
            return string.Empty;
        }

        #region Tables

        /// <summary>
        /// ProDOS file type names.  Must be 3 chars and upper case.
        /// </summary>
        /// <remarks>
        /// This generally comes from the file type notes, but some customizations have been
        /// made, e.g. "DDD" for $DD.
        /// </remarks>
        private static readonly string[] sFileTypeNames = new string[] {
            "NON", "BAD", "PCD", "PTX", "TXT", "PDA", "BIN", "FNT",
            "FOT", "BA3", "DA3", "WPF", "SOS", "$0D", "$0E", "DIR",
            "RPD", "RPI", "AFD", "AFM", "AFR", "SCL", "PFS", "$17",
            "$18", "ADB", "AWP", "ASP", "$1C", "$1D", "$1E", "$1F",
            "TDM", "$21", "$22", "$23", "$24", "$25", "$26", "$27",
            "$28", "$29", "8SC", "8OB", "8IC", "8LD", "P8C", "$2F",
            "$30", "$31", "$32", "$33", "$34", "$35", "$36", "$37",
            "$38", "$39", "$3A", "$3B", "$3C", "$3D", "$3E", "$3F",
            "DIC", "OCR", "FTD", "$43", "$44", "$45", "$46", "$47",
            "$48", "$49", "$4A", "$4B", "$4C", "$4D", "$4E", "$4F",
            "GWP", "GSS", "GDB", "DRW", "GDP", "HMD", "EDU", "STN",
            "HLP", "COM", "CFG", "ANM", "MUM", "ENT", "DVU", "FIN",
            "$60", "$61", "$62", "$63", "$64", "$65", "$66", "$67",
            "$68", "$69", "$6A", "BIO", "$6C", "TDR", "PRE", "HDV",
            "$70", "$71", "$72", "$73", "$74", "$75", "$76", "$77",
            "$78", "$79", "$7A", "$7B", "$7C", "$7D", "$7E", "$7F",
            "$80", "$81", "$82", "$83", "$84", "$85", "$86", "$87",
            "$88", "$89", "$8A", "$8B", "$8C", "$8D", "$8E", "$8F",
            "$90", "$91", "$92", "$93", "$94", "$95", "$96", "$97",
            "$98", "$99", "$9A", "$9B", "$9C", "$9D", "$9E", "$9F",
            "WP ", "$A1", "$A2", "$A3", "$A4", "$A5", "$A6", "$A7",
            "$A8", "$A9", "$AA", "GSB", "TDF", "BDF", "$AE", "$AF",
            "SRC", "OBJ", "LIB", "S16", "RTL", "EXE", "PIF", "TIF",
            "NDA", "CDA", "TOL", "DVR", "LDF", "FST", "$BE", "DOC",
            "PNT", "PIC", "ANI", "PAL", "$C4", "OOG", "SCR", "CDV",
            "FON", "FND", "ICN", "$CB", "$CC", "$CD", "$CE", "$CF",
            "$D0", "$D1", "$D2", "$D3", "$D4", "MUS", "INS", "MDI",
            "SND", "$D9", "$DA", "DBM", "$DC", "DDD", "$DE", "$DF",
            "LBR", "$E1", "ATK", "$E3", "$E4", "$E5", "$E6", "$E7",
            "$E8", "$E9", "$EA", "$EB", "$EC", "$ED", "R16", "PAS",
            "CMD", "$F1", "$F2", "$F3", "$F4", "$F5", "$F6", "$F7",
            "$F8", "OS ", "INT", "IVR", "BAS", "VAR", "REL", "SYS"
        };

        private struct TypeDesc {
            public byte mFileType;          // file type
            public ushort mMinAuxType;      // start of aux type range
            public ushort mMaxAuxType;      // end of aux type range (inclusive)
            public string mDesc;            // description

            public TypeDesc(byte type, ushort minAux, ushort maxAux, string desc) {
                mFileType = type;
                mMinAuxType = minAux;
                mMaxAuxType = maxAux;
                mDesc = desc;
            }
        }

        /// <summary>
        /// Description table.  The first item that matches will be used, but the table is searched
        /// bottom-up, so it's important to have the most general entry first.
        /// </summary>
        /// <remarks>
        /// <para>GS/OS has a similar table in FType.Main/FType.Aux, documented in the file
        /// type note for FTD ($42).</para>
        /// <para>This list should be complete as of the May 1992 "about" note.</para>
        /// </remarks>
        private static readonly TypeDesc[] sDescs = {
            new TypeDesc(/*NON*/ 0x00, 0x0000, 0xffff, "Untyped file"),
            new TypeDesc(/*BAD*/ 0x01, 0x0000, 0xffff, "Bad blocks"),
            new TypeDesc(/*PCD*/ 0x02, 0x0000, 0xffff, "Pascal code"),
            new TypeDesc(/*PTX*/ 0x03, 0x0000, 0xffff, "Pascal text"),
            new TypeDesc(/*TXT*/ 0x04, 0x0000, 0xffff, "ASCII text"),
            new TypeDesc(/*PDA*/ 0x05, 0x0000, 0xffff, "Pascal data"),
            new TypeDesc(/*BIN*/ 0x06, 0x0000, 0xffff, "Binary"),
            new TypeDesc(/*FNT*/ 0x07, 0x0000, 0xffff, "Apple /// font"),
            new TypeDesc(/*FOT*/ 0x08, 0x0000, 0xffff, "Apple II or /// graphics"),
            new TypeDesc(/*   */ 0x08, 0x0000, 0x3fff, "Apple II graphics"),
            new TypeDesc(/*   */ 0x08, 0x4000, 0x4000, "Packed hi-res image"),
            new TypeDesc(/*   */ 0x08, 0x4001, 0x4001, "Packed double hi-res image"),
            new TypeDesc(/*   */ 0x08, 0x8001, 0x8001, "Printographer packed HGR file"),
            new TypeDesc(/*   */ 0x08, 0x8002, 0x8002, "Printographer packed DHGR file"),
            new TypeDesc(/*   */ 0x08, 0x8003, 0x8003, "Softdisk hi-res image"),
            new TypeDesc(/*   */ 0x08, 0x8004, 0x8004, "Softdisk double hi-res image"),
            new TypeDesc(/*   */ 0x08, 0x8066, 0x8066, "LZ4FH-compressed hi-res image"),
            new TypeDesc(/*BA3*/ 0x09, 0x0000, 0xffff, "Apple /// BASIC program"),
            new TypeDesc(/*DA3*/ 0x0a, 0x0000, 0xffff, "Apple /// BASIC data"),
            new TypeDesc(/*WPF*/ 0x0b, 0x0000, 0xffff, "Apple II or /// word processor"),
            new TypeDesc(/*   */ 0x0b, 0x8001, 0x8001, "Write This Way document"),
            new TypeDesc(/*   */ 0x0b, 0x8002, 0x8002, "Writing & Publishing document"),
            new TypeDesc(/*SOS*/ 0x0c, 0x0000, 0xffff, "Apple /// SOS system"),
            new TypeDesc(/*DIR*/ 0x0f, 0x0000, 0xffff, "Folder"),
            new TypeDesc(/*RPD*/ 0x10, 0x0000, 0xffff, "Apple /// RPS data"),
            new TypeDesc(/*RPI*/ 0x11, 0x0000, 0xffff, "Apple /// RPS index"),
            new TypeDesc(/*AFD*/ 0x12, 0x0000, 0xffff, "Apple /// AppleFile discard"),
            new TypeDesc(/*AFM*/ 0x13, 0x0000, 0xffff, "Apple /// AppleFile model"),
            new TypeDesc(/*AFR*/ 0x14, 0x0000, 0xffff, "Apple /// AppleFile report format"),
            new TypeDesc(/*SCL*/ 0x15, 0x0000, 0xffff, "Apple /// screen library"),
            new TypeDesc(/*PFS*/ 0x16, 0x0000, 0xffff, "PFS document"),
            new TypeDesc(/*   */ 0x16, 0x0001, 0x0001, "PFS:File document"),
            new TypeDesc(/*   */ 0x16, 0x0002, 0x0002, "PFS:Write document"),
            new TypeDesc(/*   */ 0x16, 0x0003, 0x0003, "PFS:Graph document"),
            new TypeDesc(/*   */ 0x16, 0x0004, 0x0004, "PFS:Plan document"),
            new TypeDesc(/*   */ 0x16, 0x0016, 0x0016, "PFS internal data"),
            new TypeDesc(/*ADB*/ 0x19, 0x0000, 0xffff, "AppleWorks data base"),
            new TypeDesc(/*AWP*/ 0x1a, 0x0000, 0xffff, "AppleWorks word processor"),
            new TypeDesc(/*ASP*/ 0x1b, 0x0000, 0xffff, "AppleWorks spreadsheet"),
            new TypeDesc(/*TDM*/ 0x20, 0x0000, 0xffff, "Desktop Manager document"),
            new TypeDesc(/*???*/ 0x21, 0x0000, 0xffff, "Instant Pascal source"),
            new TypeDesc(/*???*/ 0x22, 0x0000, 0xffff, "UCSD Pascal volume"),
            new TypeDesc(/*???*/ 0x29, 0x0000, 0xffff, "Apple /// SOS dictionary"),
            new TypeDesc(/*8SC*/ 0x2a, 0x0000, 0xffff, "Apple II source code"),
            new TypeDesc(/*   */ 0x2a, 0x8001, 0x8001, "EBBS command script"),
            new TypeDesc(/*8OB*/ 0x2b, 0x0000, 0xffff, "Apple II object code"),
            new TypeDesc(/*   */ 0x2b, 0x8001, 0x8001, "GBBS Pro object Code"),
            new TypeDesc(/*8IC*/ 0x2c, 0x0000, 0xffff, "Apple II interpreted code"),
            new TypeDesc(/*   */ 0x2c, 0x8003, 0x8003, "APEX Program File"),
            new TypeDesc(/*   */ 0x2c, 0x8005, 0x8005, "EBBS tokenized command script"),
            new TypeDesc(/*8LD*/ 0x2d, 0x0000, 0xffff, "Apple II language data"),
            new TypeDesc(/*   */ 0x2d, 0x8006, 0x8005, "EBBS message bundle"),
            new TypeDesc(/*   */ 0x2d, 0x8007, 0x8007, "EBBS compressed message bundle"),
            new TypeDesc(/*P8C*/ 0x2e, 0x0000, 0xffff, "ProDOS 8 code module"),
            new TypeDesc(/*   */ 0x2e, 0x8001, 0x8001, "Davex 8 Command"),
            new TypeDesc(/*PTP*/ 0x2e, 0x8002, 0x8002, "Point-to-Point drivers"),
            new TypeDesc(/*PTP*/ 0x2e, 0x8003, 0x8003, "Point-to-Point code"),
            new TypeDesc(/*   */ 0x2e, 0x8004, 0x8004, "Softdisk printer driver"),
            new TypeDesc(/*DIC*/ 0x40, 0x0000, 0xffff, "Dictionary file"),
            new TypeDesc(/*???*/ 0x41, 0x0000, 0xffff, "OCR data"),
            new TypeDesc(/*   */ 0x41, 0x8001, 0x8001, "InWords OCR font table"),
            new TypeDesc(/*FTD*/ 0x42, 0x0000, 0xffff, "File type names"),
            new TypeDesc(/*???*/ 0x43, 0x0000, 0xffff, "Peripheral data"),
            new TypeDesc(/*   */ 0x43, 0x8001, 0x8001, "Express document"),
            new TypeDesc(/*???*/ 0x44, 0x0000, 0xffff, "Personal information"),
            new TypeDesc(/*   */ 0x44, 0x8001, 0x8001, "ResuMaker personal information"),
            new TypeDesc(/*   */ 0x44, 0x8002, 0x8002, "ResuMaker resume"),
            new TypeDesc(/*   */ 0x44, 0x8003, 0x8003, "II Notes document"),
            new TypeDesc(/*   */ 0x44, 0x8004, 0x8004, "Softdisk scrapbook document"),
            new TypeDesc(/*   */ 0x44, 0x8005, 0x8005, "Don't Forget document"),
            new TypeDesc(/*   */ 0x44, 0x80ff, 0x80ff, "What To Do data"),
            new TypeDesc(/*   */ 0x44, 0xbeef, 0xbeef, "Table Scraps scrapbook"),
            new TypeDesc(/*???*/ 0x45, 0x0000, 0xffff, "Mathematical document"),
            new TypeDesc(/*   */ 0x45, 0x8001, 0x8001, "GSymbolix 3D graph document"),
            new TypeDesc(/*   */ 0x45, 0x8002, 0x8002, "GSymbolix formula document"),
            new TypeDesc(/*???*/ 0x46, 0x0000, 0xffff, "AutoSave profiles"),
            new TypeDesc(/*   */ 0x46, 0x8001, 0x8001, "AutoSave profiles"),
            new TypeDesc(/*GWP*/ 0x50, 0x0000, 0xffff, "Apple IIgs Word Processor"),
            new TypeDesc(/*   */ 0x50, 0x8001, 0x8001, "DeluxeWrite document"),
            new TypeDesc(/*   */ 0x50, 0x5445, 0x5445, "Teach document"),
            new TypeDesc(/*   */ 0x50, 0x8003, 0x8003, "Personal Journal document"),
            new TypeDesc(/*   */ 0x50, 0x8010, 0x8010, "AppleWorks GS word processor"),
            new TypeDesc(/*   */ 0x50, 0x8011, 0x8011, "Softdisk issue text"),
            new TypeDesc(/*GSS*/ 0x51, 0x0000, 0xffff, "Apple IIgs spreadsheet"),
            new TypeDesc(/*   */ 0x51, 0x8010, 0x8010, "AppleWorks GS spreadsheet"),
            new TypeDesc(/*   */ 0x51, 0x2358, 0x2358, "QC Calc spreadsheet "),
            new TypeDesc(/*GDB*/ 0x52, 0x0000, 0xffff, "Apple IIgs data base"),
            new TypeDesc(/*   */ 0x52, 0x8001, 0x8001, "GTv database"),
            new TypeDesc(/*   */ 0x52, 0x8010, 0x8010, "AppleWorks GS data base"),
            new TypeDesc(/*   */ 0x52, 0x8011, 0x8011, "AppleWorks GS DB template"),
            new TypeDesc(/*   */ 0x52, 0x8013, 0x8013, "GSAS database"),
            new TypeDesc(/*   */ 0x52, 0x8014, 0x8014, "GSAS accounting journals"),
            new TypeDesc(/*   */ 0x52, 0x8015, 0x8015, "Address Manager document"),
            new TypeDesc(/*   */ 0x52, 0x8016, 0x8016, "Address Manager defaults"),
            new TypeDesc(/*   */ 0x52, 0x8017, 0x8017, "Address Manager index"),
            new TypeDesc(/*DRW*/ 0x53, 0x0000, 0xffff, "Drawing"),
            new TypeDesc(/*   */ 0x53, 0x8002, 0x8002, "Graphic Disk Labeler document"),
            new TypeDesc(/*   */ 0x53, 0x8010, 0x8010, "AppleWorks GS graphics"),
            new TypeDesc(/*GDP*/ 0x54, 0x0000, 0xffff, "Desktop publishing"),
            new TypeDesc(/*   */ 0x54, 0x8002, 0x8002, "GraphicWriter document"),
            new TypeDesc(/*   */ 0x54, 0x8003, 0x8003, "Label It document"),
            new TypeDesc(/*   */ 0x54, 0x8010, 0x8010, "AppleWorks GS Page Layout"),
            new TypeDesc(/*   */ 0x54, 0xdd3e, 0xdd3e, "Medley document"),
            new TypeDesc(/*HMD*/ 0x55, 0x0000, 0xffff, "Hypermedia"),
            new TypeDesc(/*   */ 0x55, 0x0001, 0x0001, "HyperCard IIgs stack"),
            new TypeDesc(/*   */ 0x55, 0x8001, 0x8001, "Tutor-Tech document"),
            new TypeDesc(/*   */ 0x55, 0x8002, 0x8002, "HyperStudio document"),
            new TypeDesc(/*   */ 0x55, 0x8003, 0x8003, "Nexus document"),
            new TypeDesc(/*   */ 0x55, 0x8004, 0x8004, "HyperSoft stack"),
            new TypeDesc(/*   */ 0x55, 0x8005, 0x8005, "HyperSoft card"),
            new TypeDesc(/*   */ 0x55, 0x8006, 0x8006, "HyperSoft external command"),
            new TypeDesc(/*EDU*/ 0x56, 0x0000, 0xffff, "Educational Data"),
            new TypeDesc(/*   */ 0x56, 0x8001, 0x8001, "Tutor-Tech scores"),
            new TypeDesc(/*   */ 0x56, 0x8007, 0x8007, "GradeBook data"),
            new TypeDesc(/*STN*/ 0x57, 0x0000, 0xffff, "Stationery"),
            new TypeDesc(/*   */ 0x57, 0x8003, 0x8003, "Music Writer format"),
            new TypeDesc(/*HLP*/ 0x58, 0x0000, 0xffff, "Help file"),
            new TypeDesc(/*   */ 0x58, 0x8002, 0x8002, "Davex 8 help file"),
            new TypeDesc(/*   */ 0x58, 0x8005, 0x8005, "Micol Advanced Basic help file"),
            new TypeDesc(/*   */ 0x58, 0x8006, 0x8006, "Locator help document"),
            new TypeDesc(/*   */ 0x58, 0x8007, 0x8007, "Personal Journal help"),
            new TypeDesc(/*   */ 0x58, 0x8008, 0x8008, "Home Refinancer help"),
            new TypeDesc(/*   */ 0x58, 0x8009, 0x8009, "The Optimizer help"),
            new TypeDesc(/*   */ 0x58, 0x800a, 0x800a, "Text Wizard help"),
            new TypeDesc(/*   */ 0x58, 0x800b, 0x800b, "WordWorks Pro help system"),
            new TypeDesc(/*   */ 0x58, 0x800c, 0x800c, "Sound Wizard help"),
            new TypeDesc(/*   */ 0x58, 0x800d, 0x800d, "SeeHear help system"),
            new TypeDesc(/*   */ 0x58, 0x800e, 0x800e, "QuickForms help system"),
            new TypeDesc(/*   */ 0x58, 0x800f, 0x800f, "Don't Forget help system"),
            new TypeDesc(/*COM*/ 0x59, 0x0000, 0xffff, "Communications file"),
            new TypeDesc(/*   */ 0x59, 0x8002, 0x8002, "AppleWorks GS communications"),
            new TypeDesc(/*CFG*/ 0x5a, 0x0000, 0xffff, "Configuration file"),
            new TypeDesc(/*   */ 0x5a, 0x0000, 0x0000, "Sound settings files"),
            new TypeDesc(/*   */ 0x5a, 0x0002, 0x0002, "Battery RAM configuration"),
            new TypeDesc(/*   */ 0x5a, 0x0003, 0x0003, "AutoLaunch preferences"),
            new TypeDesc(/*   */ 0x5a, 0x0004, 0x0004, "SetStart preferences"),
            new TypeDesc(/*   */ 0x5a, 0x0005, 0x0005, "GSBug configuration"),
            new TypeDesc(/*   */ 0x5a, 0x0006, 0x0006, "Archiver preferences"),
            new TypeDesc(/*   */ 0x5a, 0x0007, 0x0007, "Archiver table of contents"),
            new TypeDesc(/*   */ 0x5a, 0x0008, 0x0008, "Font Manager data"),
            new TypeDesc(/*   */ 0x5a, 0x0009, 0x0009, "Print Manager data"),
            new TypeDesc(/*   */ 0x5a, 0x000a, 0x000a, "IR preferences"),
            new TypeDesc(/*   */ 0x5a, 0x8001, 0x8001, "Master Tracks Jr. preferences"),
            new TypeDesc(/*   */ 0x5a, 0x8002, 0x8002, "GraphicWriter preferences"),
            new TypeDesc(/*   */ 0x5a, 0x8003, 0x8003, "Z-Link configuration"),
            new TypeDesc(/*   */ 0x5a, 0x8004, 0x8004, "JumpStart configuration"),
            new TypeDesc(/*   */ 0x5a, 0x8005, 0x8005, "Davex 8 configuration"),
            new TypeDesc(/*   */ 0x5a, 0x8006, 0x8006, "Nifty List configuration"),
            new TypeDesc(/*   */ 0x5a, 0x8007, 0x8007, "GTv videodisc configuration"),
            new TypeDesc(/*   */ 0x5a, 0x8008, 0x8008, "GTv Workshop configuration"),
            new TypeDesc(/*PTP*/ 0x5a, 0x8009, 0x8009, "Point-to-Point preferences"),
            new TypeDesc(/*   */ 0x5a, 0x800a, 0x800a, "ORCA/Disassembler preferences"),
            new TypeDesc(/*   */ 0x5a, 0x800b, 0x800b, "SnowTerm preferences"),
            new TypeDesc(/*   */ 0x5a, 0x800c, 0x800c, "My Word! preferences"),
            new TypeDesc(/*   */ 0x5a, 0x800d, 0x800d, "Chipmunk configuration"),
            new TypeDesc(/*   */ 0x5a, 0x8010, 0x8010, "AppleWorks GS configuration"),
            new TypeDesc(/*   */ 0x5a, 0x8011, 0x8011, "SDE Shell preferences"),
            new TypeDesc(/*   */ 0x5a, 0x8012, 0x8012, "SDE Editor preferences"),
            new TypeDesc(/*   */ 0x5a, 0x8013, 0x8013, "SDE system tab ruler"),
            new TypeDesc(/*   */ 0x5a, 0x8014, 0x8014, "Nexus configuration"),
            new TypeDesc(/*   */ 0x5a, 0x8015, 0x8015, "DesignMaster preferences"),
            new TypeDesc(/*   */ 0x5a, 0x801a, 0x801a, "MAX/Edit keyboard template"),
            new TypeDesc(/*   */ 0x5a, 0x801b, 0x801b, "MAX/Edit tab ruler set"),
            new TypeDesc(/*   */ 0x5a, 0x801c, 0x801c, "Platinum Paint preferences"),
            new TypeDesc(/*   */ 0x5a, 0x801d, 0x801d, "Sea Scan 1000"),
            new TypeDesc(/*   */ 0x5a, 0x801e, 0x801e, "Allison preferences"),
            new TypeDesc(/*   */ 0x5a, 0x801f, 0x801f, "Gold of the Americas options"),
            new TypeDesc(/*   */ 0x5a, 0x8021, 0x8021, "GSAS accounting setup"),
            new TypeDesc(/*   */ 0x5a, 0x8022, 0x8022, "GSAS accounting document"),
            new TypeDesc(/*   */ 0x5a, 0x8023, 0x8023, "UtilityLaunch preferences"),
            new TypeDesc(/*   */ 0x5a, 0x8024, 0x8024, "Softdisk configuration"),
            new TypeDesc(/*   */ 0x5a, 0x8025, 0x8025, "Quit-To configuration"),
            new TypeDesc(/*   */ 0x5a, 0x8026, 0x8026, "Big Edit Thing"),
            new TypeDesc(/*   */ 0x5a, 0x8027, 0x8027, "ZMaker preferences"),
            new TypeDesc(/*   */ 0x5a, 0x8028, 0x8028, "Minstrel configuration"),
            new TypeDesc(/*   */ 0x5a, 0x8029, 0x8029, "WordWorks Pro preferences"),
            new TypeDesc(/*   */ 0x5a, 0x802b, 0x802b, "Pointless preferences"),
            new TypeDesc(/*   */ 0x5a, 0x802c, 0x802c, "Micol Advanced Basic config"),
            new TypeDesc(/*   */ 0x5a, 0x802e, 0x802e, "Label It configuration"),
            new TypeDesc(/*   */ 0x5a, 0x802f, 0x802f, "Cool Cursor document"),
            new TypeDesc(/*   */ 0x5a, 0x8030, 0x8030, "Locator preferences"),
            new TypeDesc(/*   */ 0x5a, 0x8031, 0x8031, "Replicator preferences"),
            new TypeDesc(/*   */ 0x5a, 0x8032, 0x8032, "Kangaroo configuration"),
            new TypeDesc(/*   */ 0x5a, 0x8033, 0x8033, "Kangaroo data"),
            new TypeDesc(/*   */ 0x5a, 0x8034, 0x8034, "TransProg III configuration"),
            new TypeDesc(/*   */ 0x5a, 0x8035, 0x8035, "Home Refinancer preferences"),
            new TypeDesc(/*   */ 0x5a, 0x8036, 0x8036, "Easy Eyes settings"),
            new TypeDesc(/*   */ 0x5a, 0x8037, 0x8037, "The Optimizer settings"),
            new TypeDesc(/*   */ 0x5a, 0x8038, 0x8038, "Text Wizard settings"),
            new TypeDesc(/*   */ 0x5a, 0x803b, 0x803b, "Disk Access II preferences"),
            new TypeDesc(/*   */ 0x5a, 0x803d, 0x803d, "Quick DA configuration"),
            new TypeDesc(/*   */ 0x5a, 0x803e, 0x803e, "Crazy 8s preferences"),
            new TypeDesc(/*   */ 0x5a, 0x803f, 0x803f, "Sound Wizard settings"),
            new TypeDesc(/*   */ 0x5a, 0x8041, 0x8041, "Quick Window configuration"),
            new TypeDesc(/*   */ 0x5a, 0x8044, 0x8044, "Universe Master disk map"),
            new TypeDesc(/*   */ 0x5a, 0x8046, 0x8046, "Autopilot configuration"),
            new TypeDesc(/*   */ 0x5a, 0x8047, 0x8047, "EGOed preferences"),
            new TypeDesc(/*   */ 0x5a, 0x8049, 0x8049, "Quick DA preferences"),
            new TypeDesc(/*   */ 0x5a, 0x804b, 0x804b, "HardPressed volume preferences"),
            new TypeDesc(/*   */ 0x5a, 0x804c, 0x804c, "HardPressed global preferences"),
            new TypeDesc(/*   */ 0x5a, 0x804d, 0x804d, "HardPressed profile"),
            new TypeDesc(/*   */ 0x5a, 0x8050, 0x8050, "Don't Forget settings"),
            new TypeDesc(/*   */ 0x5a, 0x8052, 0x8052, "ProBOOT preferences"),
            new TypeDesc(/*   */ 0x5a, 0x8054, 0x8054, "Battery Brain preferences"),
            new TypeDesc(/*   */ 0x5a, 0x8055, 0x8055, "Rainbow configuration"),
            new TypeDesc(/*   */ 0x5a, 0x8061, 0x8061, "TypeSet preferences"),
            new TypeDesc(/*   */ 0x5a, 0x8063, 0x8063, "Cool Cursor preferences"),
            new TypeDesc(/*   */ 0x5a, 0x806e, 0x806e, "Balloon preferences"),
            new TypeDesc(/*   */ 0x5a, 0x80fe, 0x80fe, "Special Edition configuration"),
            new TypeDesc(/*   */ 0x5a, 0x80ff, 0x80ff, "Sun Dial preferences"),
            new TypeDesc(/*ANM*/ 0x5b, 0x0000, 0xffff, "Animation file"),
            new TypeDesc(/*   */ 0x5b, 0x8001, 0x8001, "Cartooners movie"),
            new TypeDesc(/*   */ 0x5b, 0x8002, 0x8002, "Cartooners actors"),
            new TypeDesc(/*   */ 0x5b, 0x8005, 0x8005, "Arcade King Super document"),
            new TypeDesc(/*   */ 0x5b, 0x8006, 0x8006, "Arcade King DHRG document"),
            new TypeDesc(/*   */ 0x5b, 0x8007, 0x8007, "DreamVision movie"),
            new TypeDesc(/*MUM*/ 0x5c, 0x0000, 0xffff, "Multimedia document"),
            new TypeDesc(/*   */ 0x5c, 0x8001, 0x8001, "GTv multimedia playlist"),
            new TypeDesc(/*ENT*/ 0x5d, 0x0000, 0xffff, "Game/Entertainment document"),
            new TypeDesc(/*   */ 0x5d, 0x8001, 0x8001, "Solitaire Royale document"),
            new TypeDesc(/*   */ 0x5d, 0x8002, 0x8002, "BattleFront scenario"),
            new TypeDesc(/*   */ 0x5d, 0x8003, 0x8003, "BattleFront saved game"),
            new TypeDesc(/*   */ 0x5d, 0x8004, 0x8004, "Gold of the Americas game"),
            new TypeDesc(/*   */ 0x5d, 0x8006, 0x8006, "Blackjack Tutor document"),
            new TypeDesc(/*   */ 0x5d, 0x8008, 0x8008, "Canasta document"),
            new TypeDesc(/*   */ 0x5d, 0x800b, 0x800b, "Word Search document"),
            new TypeDesc(/*   */ 0x5d, 0x800c, 0x800c, "Tarot deal"),
            new TypeDesc(/*   */ 0x5d, 0x800d, 0x800d, "Tarot tournament"),
            new TypeDesc(/*   */ 0x5d, 0x800e, 0x800e, "Full Metal Planet game"),
            new TypeDesc(/*   */ 0x5d, 0x800f, 0x800f, "Full Metal Planet player"),
            new TypeDesc(/*   */ 0x5d, 0x8010, 0x8010, "Quizzical high scores"),
            new TypeDesc(/*   */ 0x5d, 0x8011, 0x8011, "Meltdown high scores"),
            new TypeDesc(/*   */ 0x5d, 0x8012, 0x8012, "BlockWords high scores"),
            new TypeDesc(/*   */ 0x5d, 0x8013, 0x8013, "Lift-A-Gon scores"),
            new TypeDesc(/*   */ 0x5d, 0x8014, 0x8014, "Softdisk Adventure"),
            new TypeDesc(/*   */ 0x5d, 0x8015, 0x8015, "Blankety Blank document"),
            new TypeDesc(/*   */ 0x5d, 0x8016, 0x8016, "Son of Star Axe champion"),
            new TypeDesc(/*   */ 0x5d, 0x8017, 0x8017, "Digit Fidget high scores"),
            new TypeDesc(/*   */ 0x5d, 0x8018, 0x8018, "Eddie map"),
            new TypeDesc(/*   */ 0x5d, 0x8019, 0x8019, "Eddie tile set"),
            new TypeDesc(/*   */ 0x5d, 0x8122, 0x8122, "Wolfenstein 3D scenario"),
            new TypeDesc(/*   */ 0x5d, 0x8123, 0x8123, "Wolfenstein 3D saved game"),
            new TypeDesc(/*DVU*/ 0x5e, 0x0000, 0xffff, "Development utility document"),
            new TypeDesc(/*   */ 0x5e, 0x0001, 0x0001, "Resource file"),
            new TypeDesc(/*   */ 0x5e, 0x8001, 0x8001, "ORCA/Disassembler template"),
            new TypeDesc(/*   */ 0x5e, 0x8003, 0x8003, "DesignMaster document"),
            new TypeDesc(/*   */ 0x5e, 0x8008, 0x8008, "ORCA/C symbol file"),
            new TypeDesc(/*FIN*/ 0x5f, 0x0000, 0xffff, "Financial document"),
            new TypeDesc(/*   */ 0x5f, 0x8001, 0x8001, "Your Money Matters document"),
            new TypeDesc(/*   */ 0x5f, 0x8002, 0x8002, "Home Refinancer document"),
            new TypeDesc(/*BIO*/ 0x6b, 0x0000, 0xffff, "PC Transporter BIOS"),
            new TypeDesc(/*TDR*/ 0x6d, 0x0000, 0xffff, "PC Transporter driver"),
            new TypeDesc(/*PRE*/ 0x6e, 0x0000, 0xffff, "PC Transporter pre-boot"),
            new TypeDesc(/*HDV*/ 0x6f, 0x0000, 0xffff, "PC Transporter volume"),
            new TypeDesc(/*WP */ 0xa0, 0x0000, 0xffff, "WordPerfect document"),
            new TypeDesc(/*GSB*/ 0xab, 0x0000, 0xffff, "Apple IIgs BASIC program"),
            new TypeDesc(/*TDF*/ 0xac, 0x0000, 0xffff, "Apple IIgs BASIC TDF"),
            new TypeDesc(/*BDF*/ 0xad, 0x0000, 0xffff, "Apple IIgs BASIC data"),
            new TypeDesc(/*SRC*/ 0xb0, 0x0000, 0xffff, "Apple IIgs source code"),
            new TypeDesc(/*   */ 0xb0, 0x0001, 0x0001, "APW Text file"),
            new TypeDesc(/*   */ 0xb0, 0x0003, 0x0003, "APW 65816 Assembly source code"),
            new TypeDesc(/*   */ 0xb0, 0x0005, 0x0005, "ORCA/Pascal source code"),
            new TypeDesc(/*   */ 0xb0, 0x0006, 0x0006, "APW command file"),
            new TypeDesc(/*   */ 0xb0, 0x0008, 0x0008, "ORCA/C source code"),
            new TypeDesc(/*   */ 0xb0, 0x0009, 0x0009, "APW Linker command file"),
            new TypeDesc(/*   */ 0xb0, 0x000a, 0x000a, "APW C source code"),
            new TypeDesc(/*   */ 0xb0, 0x000c, 0x000c, "ORCA/Desktop command file"),
            new TypeDesc(/*   */ 0xb0, 0x0015, 0x0015, "APW Rez source file"),
            new TypeDesc(/*   */ 0xb0, 0x0017, 0x0017, "Installer script"),
            new TypeDesc(/*   */ 0xb0, 0x001e, 0x001e, "TML Pascal source code"),
            new TypeDesc(/*   */ 0xb0, 0x0116, 0x0116, "ORCA/Disassembler script"),
            new TypeDesc(/*   */ 0xb0, 0x0503, 0x0503, "SDE Assembler source code"),
            new TypeDesc(/*   */ 0xb0, 0x0506, 0x0506, "SDE command script"),
            new TypeDesc(/*   */ 0xb0, 0x0601, 0x0601, "Nifty List data"),
            new TypeDesc(/*   */ 0xb0, 0x0719, 0x0719, "PostScript file"),
            new TypeDesc(/*OBJ*/ 0xb1, 0x0000, 0xffff, "Apple IIgs object code"),
            new TypeDesc(/*LIB*/ 0xb2, 0x0000, 0xffff, "Apple IIgs Library file"),
            new TypeDesc(/*S16*/ 0xb3, 0x0000, 0xffff, "GS/OS application"),
            new TypeDesc(/*RTL*/ 0xb4, 0x0000, 0xffff, "GS/OS run-time library"),
            new TypeDesc(/*EXE*/ 0xb5, 0x0000, 0xffff, "GS/OS shell application"),
            new TypeDesc(/*PIF*/ 0xb6, 0x0000, 0xffff, "Permanent initialization file"),
            new TypeDesc(/*TIF*/ 0xb7, 0x0000, 0xffff, "Temporary initialization file"),
            new TypeDesc(/*NDA*/ 0xb8, 0x0000, 0xffff, "New desk accessory"),
            new TypeDesc(/*CDA*/ 0xb9, 0x0000, 0xffff, "Classic desk accessory"),
            new TypeDesc(/*TOL*/ 0xba, 0x0000, 0xffff, "Tool"),
            new TypeDesc(/*DVR*/ 0xbb, 0x0000, 0xffff, "Apple IIgs device driver file"),
            new TypeDesc(/*   */ 0xbb, 0x7e01, 0x7e01, "GNO/ME terminal device driver"),
            new TypeDesc(/*   */ 0xbb, 0x7f01, 0x7f01, "GTv videodisc serial driver"),
            new TypeDesc(/*   */ 0xbb, 0x7f02, 0x7f02, "GTv videodisc game port driver"),
            new TypeDesc(/*LDF*/ 0xbc, 0x0000, 0xffff, "Load file (generic)"),
            new TypeDesc(/*   */ 0xbc, 0x4001, 0x4001, "Nifty List module"),
            new TypeDesc(/*   */ 0xbc, 0xc001, 0xc001, "Nifty List module"),
            new TypeDesc(/*   */ 0xbc, 0x4002, 0x4002, "Super Info module"),
            new TypeDesc(/*   */ 0xbc, 0xc002, 0xc002, "Super Info module"),
            new TypeDesc(/*   */ 0xbc, 0x4004, 0x4004, "Twilight document"),
            new TypeDesc(/*   */ 0xbc, 0xc004, 0xc004, "Twilight document"),
            new TypeDesc(/*   */ 0xbc, 0x4006, 0x4006, "Foundation resource editor"),
            new TypeDesc(/*   */ 0xbc, 0xc006, 0xc006, "Foundation resource editor"),
            new TypeDesc(/*   */ 0xbc, 0x4007, 0x4007, "HyperStudio new button action"),
            new TypeDesc(/*   */ 0xbc, 0xc007, 0xc007, "HyperStudio new button action"),
            new TypeDesc(/*   */ 0xbc, 0x4008, 0x4008, "HyperStudio screen transition"),
            new TypeDesc(/*   */ 0xbc, 0xc008, 0xc008, "HyperStudio screen transition"),
            new TypeDesc(/*   */ 0xbc, 0x4009, 0x4009, "DreamGrafix module"),
            new TypeDesc(/*   */ 0xbc, 0xc009, 0xc009, "DreamGrafix module"),
            new TypeDesc(/*   */ 0xbc, 0x400a, 0x400a, "HyperStudio Extra utility"),
            new TypeDesc(/*   */ 0xbc, 0xc00a, 0xc00a, "HyperStudio Extra utility"),
            new TypeDesc(/*   */ 0xbc, 0x400f, 0x400f, "HardPressed module"),
            new TypeDesc(/*   */ 0xbc, 0xc00f, 0xc00f, "HardPressed module"),
            new TypeDesc(/*   */ 0xbc, 0x4010, 0x4010, "Graphic Exchange translator"),
            new TypeDesc(/*   */ 0xbc, 0xc010, 0xc010, "Graphic Exchange translator"),
            new TypeDesc(/*   */ 0xbc, 0x4011, 0x4011, "Desktop Enhancer blanker"),
            new TypeDesc(/*   */ 0xbc, 0xc011, 0xc011, "Desktop Enhancer blanker"),
            new TypeDesc(/*   */ 0xbc, 0x4083, 0x4083, "Marinetti link layer module"),
            new TypeDesc(/*   */ 0xbc, 0xc083, 0xc083, "Marinetti link layer module"),
            new TypeDesc(/*FST*/ 0xbd, 0x0000, 0xffff, "GS/OS File System Translator"),
            new TypeDesc(/*DOC*/ 0xbf, 0x0000, 0xffff, "GS/OS document"),
            new TypeDesc(/*PNT*/ 0xc0, 0x0000, 0xffff, "Packed super hi-res picture"),
            new TypeDesc(/*   */ 0xc0, 0x0000, 0x0000, "Paintworks packed picture"),
            new TypeDesc(/*   */ 0xc0, 0x0001, 0x0001, "Packed super hi-res image"),
            new TypeDesc(/*   */ 0xc0, 0x0002, 0x0002, "Apple Preferred Format picture"),
            new TypeDesc(/*   */ 0xc0, 0x0003, 0x0003, "Packed QuickDraw II PICT file"),
            new TypeDesc(/*   */ 0xc0, 0x0080, 0x0080, "TIFF document"),
            new TypeDesc(/*   */ 0xc0, 0x0081, 0x0081, "JFIF (JPEG) document"),
            new TypeDesc(/*   */ 0xc0, 0x8001, 0x8001, "GTv background image"),
            new TypeDesc(/*   */ 0xc0, 0x8005, 0x8005, "DreamGrafix document"),
            new TypeDesc(/*   */ 0xc0, 0x8006, 0x8006, "GIF document"),
            new TypeDesc(/*PIC*/ 0xc1, 0x0000, 0xffff, "Super hi-res picture"),
            new TypeDesc(/*   */ 0xc1, 0x0000, 0x0000, "Super hi-res screen image"),
            new TypeDesc(/*   */ 0xc1, 0x0001, 0x0001, "QuickDraw PICT file"),
            new TypeDesc(/*   */ 0xc1, 0x0002, 0x0002, "Super hi-res 3200-color screen image"),
            new TypeDesc(/*   */ 0xc1, 0x8001, 0x8001, "Allison raw image doc"),
            new TypeDesc(/*   */ 0xc1, 0x8002, 0x8002, "ThunderScan image doc"),
            new TypeDesc(/*   */ 0xc1, 0x8003, 0x8003, "DreamGrafix document"),
            new TypeDesc(/*ANI*/ 0xc2, 0x0000, 0xffff, "Paintworks animation"),
            new TypeDesc(/*PAL*/ 0xc3, 0x0000, 0xffff, "Paintworks palette"),
            new TypeDesc(/*OOG*/ 0xc5, 0x0000, 0xffff, "Object-oriented graphics"),
            new TypeDesc(/*   */ 0xc5, 0x8000, 0x8000, "Draw Plus document"),
            new TypeDesc(/*   */ 0xc5, 0xc000, 0xc000, "DYOH architecture doc"),
            new TypeDesc(/*   */ 0xc5, 0xc001, 0xc001, "DYOH predrawn objects"),
            new TypeDesc(/*   */ 0xc5, 0xc002, 0xc002, "DYOH custom objects"),
            new TypeDesc(/*   */ 0xc5, 0xc003, 0xc003, "DYOH clipboard"),
            new TypeDesc(/*   */ 0xc5, 0xc004, 0xc004, "DYOH interiors document"),
            new TypeDesc(/*   */ 0xc5, 0xc005, 0xc005, "DYOH patterns"),
            new TypeDesc(/*   */ 0xc5, 0xc006, 0xc006, "DYOH landscape document"),
            new TypeDesc(/*   */ 0xc5, 0xc007, 0xc007, "PyWare Document"),
            new TypeDesc(/*SCR*/ 0xc6, 0x0000, 0xffff, "Script"),
            new TypeDesc(/*   */ 0xc6, 0x8001, 0x8001, "Davex 8 script"),
            new TypeDesc(/*   */ 0xc6, 0x8002, 0x8002, "Universe Master backup script"),
            new TypeDesc(/*   */ 0xc6, 0x8003, 0x8003, "Universe Master Chain script"),
            new TypeDesc(/*CDV*/ 0xc7, 0x0000, 0xffff, "Control Panel document"),
            new TypeDesc(/*FON*/ 0xc8, 0x0000, 0xffff, "Font"),
            new TypeDesc(/*   */ 0xc8, 0x0000, 0x0000, "Font (Standard Apple IIgs QuickDraw II Font)"),
            new TypeDesc(/*   */ 0xc8, 0x0001, 0x0001, "TrueType font resource"),
            new TypeDesc(/*   */ 0xc8, 0x0008, 0x0008, "Postscript font resource"),
            new TypeDesc(/*   */ 0xc8, 0x0081, 0x0081, "TrueType font file"),
            new TypeDesc(/*   */ 0xc8, 0x0088, 0x0088, "Postscript font file"),
            new TypeDesc(/*FND*/ 0xc9, 0x0000, 0xffff, "Finder data"),
            new TypeDesc(/*ICN*/ 0xca, 0x0000, 0xffff, "Icons"),
            new TypeDesc(/*MUS*/ 0xd5, 0x0000, 0xffff, "Music sequence"),
            new TypeDesc(/*   */ 0xd5, 0x0000, 0x0000, "Music Construction Set song"),
            new TypeDesc(/*   */ 0xd5, 0x0001, 0x0001, "MIDI Synth sequence"),
            new TypeDesc(/*   */ 0xd5, 0x0007, 0x0007, "SoundSmith document"),
            new TypeDesc(/*   */ 0xd5, 0x8002, 0x8002, "Diversi-Tune sequence"),
            new TypeDesc(/*   */ 0xd5, 0x8003, 0x8003, "Master Tracks Jr. sequence"),
            new TypeDesc(/*   */ 0xd5, 0x8004, 0x8004, "Music Writer document"),
            new TypeDesc(/*   */ 0xd5, 0x8005, 0x8005, "Arcade King Super music"),
            new TypeDesc(/*   */ 0xd5, 0x8006, 0x8006, "Music Composer file"),
            new TypeDesc(/*INS*/ 0xd6, 0x0000, 0xffff, "Instrument"),
            new TypeDesc(/*   */ 0xd6, 0x0000, 0x0000, "Music Construction Set instrument"),
            new TypeDesc(/*   */ 0xd6, 0x0001, 0x0001, "MIDI Synth instrument"),
            new TypeDesc(/*   */ 0xd6, 0x8002, 0x8002, "Diversi-Tune instrument"),
            new TypeDesc(/*MDI*/ 0xd7, 0x0000, 0xffff, "MIDI data"),
            new TypeDesc(/*   */ 0xd7, 0x0000, 0x0000, "MIDI standard data"),
            new TypeDesc(/*   */ 0xd7, 0x0080, 0x0080, "MIDI System Exclusive data"),
            new TypeDesc(/*   */ 0xd7, 0x8001, 0x8001, "MasterTracks Pro Sysex file"),
            new TypeDesc(/*SND*/ 0xd8, 0x0000, 0xffff, "Sampled sound"),
            new TypeDesc(/*   */ 0xd8, 0x0000, 0x0000, "Audio IFF document"),
            new TypeDesc(/*   */ 0xd8, 0x0001, 0x0001, "AIFF-C document"),
            new TypeDesc(/*   */ 0xd8, 0x0002, 0x0002, "ASIF instrument"),
            new TypeDesc(/*   */ 0xd8, 0x0003, 0x0003, "Sound resource file"),
            new TypeDesc(/*   */ 0xd8, 0x0004, 0x0004, "MIDI Synth wave data"),
            new TypeDesc(/*   */ 0xd8, 0x8001, 0x8001, "HyperStudio sound"),
            new TypeDesc(/*   */ 0xd8, 0x8002, 0x8002, "Arcade King Super sound"),
            new TypeDesc(/*   */ 0xd8, 0x8003, 0x8003, "SoundOff! sound bank"),
            new TypeDesc(/*DBM*/ 0xdb, 0x0000, 0xffff, "DB Master document"),
            new TypeDesc(/*   */ 0xdb, 0x0001, 0x0001, "DB Master document"),
            new TypeDesc(/*???*/ 0xdd, 0x0000, 0xffff, "DDD Deluxe archive"),   // unofficial
            new TypeDesc(/*LBR*/ 0xe0, 0x0000, 0xffff, "Archival library"),
            new TypeDesc(/*   */ 0xe0, 0x0000, 0x0000, "ALU library"),
            new TypeDesc(/*   */ 0xe0, 0x0001, 0x0001, "AppleSingle file"),
            new TypeDesc(/*   */ 0xe0, 0x0002, 0x0002, "AppleDouble header file"),
            new TypeDesc(/*   */ 0xe0, 0x0003, 0x0003, "AppleDouble data file"),
            new TypeDesc(/*   */ 0xe0, 0x0004, 0x0004, "Archiver archive"),
            new TypeDesc(/*   */ 0xe0, 0x0005, 0x0005, "DiskCopy 4.2 disk image"),
            new TypeDesc(/*   */ 0xe0, 0x0100, 0x0100, "Apple 5.25 disk image"),
            new TypeDesc(/*   */ 0xe0, 0x0101, 0x0101, "Profile 5MB disk image"),
            new TypeDesc(/*   */ 0xe0, 0x0102, 0x0102, "Profile 10MB disk image"),
            new TypeDesc(/*   */ 0xe0, 0x0103, 0x0103, "Apple 3.5 disk image"),
            new TypeDesc(/*   */ 0xe0, 0x0104, 0x0104, "SCSI device image"),
            new TypeDesc(/*   */ 0xe0, 0x0105, 0x0105, "SCSI hard disk image"),
            new TypeDesc(/*   */ 0xe0, 0x0106, 0x0106, "SCSI tape image"),
            new TypeDesc(/*   */ 0xe0, 0x0107, 0x0107, "SCSI CD-ROM image"),
            new TypeDesc(/*   */ 0xe0, 0x010e, 0x010e, "RAM disk image"),
            new TypeDesc(/*   */ 0xe0, 0x010f, 0x010f, "ROM disk image"),
            new TypeDesc(/*   */ 0xe0, 0x0110, 0x0110, "File server image"),
            new TypeDesc(/*   */ 0xe0, 0x0113, 0x0113, "Hard disk image"),
            new TypeDesc(/*   */ 0xe0, 0x0114, 0x0114, "Floppy disk image"),
            new TypeDesc(/*   */ 0xe0, 0x0115, 0x0115, "Tape image"),
            new TypeDesc(/*   */ 0xe0, 0x011e, 0x011e, "AppleTalk file server image"),
            new TypeDesc(/*   */ 0xe0, 0x0120, 0x0120, "DiskCopy 6 disk image"),
            new TypeDesc(/*   */ 0xe0, 0x0130, 0x0130, "Universal Disk Image file"),
            new TypeDesc(/*   */ 0xe0, 0x8000, 0x8000, "Binary II file"),
            new TypeDesc(/*   */ 0xe0, 0x8001, 0x8001, "AppleLink ACU document"),
            new TypeDesc(/*   */ 0xe0, 0x8002, 0x8002, "ShrinkIt (NuFX) document"),
            new TypeDesc(/*   */ 0xe0, 0x8003, 0x8003, "Universal Disk Image file"),
            new TypeDesc(/*   */ 0xe0, 0x8004, 0x8004, "Davex archived volume"),
            new TypeDesc(/*   */ 0xe0, 0x8006, 0x8006, "EZ Backup Saveset doc"),
            new TypeDesc(/*   */ 0xe0, 0x8007, 0x8007, "ELS DOS 3.3 volume"),
            new TypeDesc(/*   */ 0xe0, 0x8008, 0x8008, "UtilityWorks document"),
            new TypeDesc(/*   */ 0xe0, 0x800a, 0x800a, "Replicator document"),
            new TypeDesc(/*   */ 0xe0, 0x800b, 0x800b, "AutoArk compressed document"),
            new TypeDesc(/*   */ 0xe0, 0x800d, 0x800d, "HardPressed compressed data (data fork)"),
            new TypeDesc(/*   */ 0xe0, 0x800e, 0x800e, "HardPressed compressed data (rsrc fork)"),
            new TypeDesc(/*   */ 0xe0, 0x800f, 0x800f, "HardPressed compressed data (both forks)"),
            new TypeDesc(/*   */ 0xe0, 0x8010, 0x8010, "LHA archive"),
            new TypeDesc(/*ATK*/ 0xe2, 0x0000, 0xffff, "AppleTalk data"),
            new TypeDesc(/*   */ 0xe2, 0xffff, 0xffff, "EasyMount document"),
            new TypeDesc(/*R16*/ 0xee, 0x0000, 0xffff, "EDASM 816 relocatable file"),
            new TypeDesc(/*PAS*/ 0xef, 0x0000, 0xffff, "Pascal area"),
            new TypeDesc(/*CMD*/ 0xf0, 0x0000, 0xffff, "BASIC command"),
            new TypeDesc(/*???*/ 0xf1, 0x0000, 0xffff, "User type #1"),
            new TypeDesc(/*???*/ 0xf2, 0x0000, 0xffff, "User type #2"),
            new TypeDesc(/*???*/ 0xf3, 0x0000, 0xffff, "User type #3"),
            new TypeDesc(/*???*/ 0xf4, 0x0000, 0xffff, "User type #4"),
            new TypeDesc(/*???*/ 0xf5, 0x0000, 0xffff, "User type #5"),
            new TypeDesc(/*???*/ 0xf6, 0x0000, 0xffff, "User type #6"),
            new TypeDesc(/*???*/ 0xf7, 0x0000, 0xffff, "User type #7"),
            new TypeDesc(/*???*/ 0xf8, 0x0000, 0xffff, "User type #8"),
            new TypeDesc(/*OS */ 0xf9, 0x0000, 0xffff, "GS/OS system file"),
            new TypeDesc(/*INT*/ 0xfa, 0x0000, 0xffff, "Integer BASIC program"),
            new TypeDesc(/*IVR*/ 0xfb, 0x0000, 0xffff, "Integer BASIC variables"),
            new TypeDesc(/*BAS*/ 0xfc, 0x0000, 0xffff, "Applesoft BASIC program"),
            new TypeDesc(/*VAR*/ 0xfd, 0x0000, 0xffff, "Applesoft BASIC variables"),
            new TypeDesc(/*REL*/ 0xfe, 0x0000, 0xffff, "Relocatable code"),
            new TypeDesc(/*SYS*/ 0xff, 0x0000, 0xffff, "ProDOS 8 application"),
        };

        #endregion Tables
    }
}
