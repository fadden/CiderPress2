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
    /// One entry in an AddFileSet.
    /// </summary>
    public class AddFileEntry {
        public enum SourceType {
            Unknown = 0,
            Plain,          // file simply holds data
            AppleSingle,    // file holds one or both forks + file attributes
            AppleDouble,    // file holds resource fork + file attributes (only for "header" file)
            Import,         // file holds data that will be converted
        }

        /// <summary>
        /// True if this file has a data fork.
        /// </summary>
        public bool HasDataFork { get; set; } = false;

        /// <summary>
        /// Absolute path to data fork data file, or empty if no data fork is included.
        /// </summary>
        public string FullDataPath { get; set; } = string.Empty;

        /// <summary>
        /// Data fork source type.
        /// </summary>
        public SourceType DataSource { get; set; } = SourceType.Unknown;

        /// <summary>
        /// True if this file has a resource fork.  The fork may be empty.
        /// </summary>
        /// <remarks>
        /// There may be an AppleDouble resource fork file even if this is false, because the
        /// file attribute data is also stored there.
        /// </remarks>
        public bool HasRsrcFork { get; set; } = false;

        /// <summary>
        /// Absolute path to resource fork data file in the host filesystem, or empty if no
        /// resource fork is included.
        /// </summary>
        public string FullRsrcPath { get; set; } = string.Empty;

        /// <summary>
        /// Resource fork source type.
        /// </summary>
        public SourceType RsrcSource { get; set; } = SourceType.Unknown;

        /// <summary>
        /// True if we have an ADF header file with attributes.  This may be set even if we
        /// don't have a resource fork.
        /// </summary>
        public bool HasADFAttribs { get; set; } = false;

        /// <summary>
        /// Relative path to create in the archive or disk image.  This will be empty if the
        /// file should be created in the volume directory.
        /// </summary>
        /// <remarks>
        /// <para>Treating it like a host path allows us to use the .NET Path calls, but limits
        /// the set of characters that can be used in the directory filenames.  For flexibility
        /// we use <see cref="StorageDirSep"/> to separate the paths.  The filename is not
        /// included here, so NAPS-unescaped and AppleSingle filenames can be stored there.</para>
        /// </remarks>
        public string StorageDir { get; set; } = string.Empty;

        /// <summary>
        /// Separator character used for the path in <see cref="StorageDir"/>.
        /// </summary>
        public char StorageDirSep { get; set; } = IFileEntry.NO_DIR_SEP;

        /// <summary>
        /// Filename to create in the archive or disk image.  This may contain characters that
        /// are not legal on the host filesystem, such as directory name separators or nulls.
        /// </summary>
        public string StorageName { get; set; } = string.Empty;

        /// <summary>
        /// Modification date.
        /// </summary>
        /// <remarks>
        /// We use the stored value if available (ADF/AS), host value if not.  For host, the data
        /// fork file's date is preferred.
        /// </remarks>
        public DateTime ModWhen { get; set; } = TimeStamp.NO_DATE;

        /// <summary>
        /// Creation date.
        /// </summary>
        /// <remarks>
        /// Only set for ADF/AS.
        /// </remarks>
        public DateTime CreateWhen { get; set; } = TimeStamp.NO_DATE;

        /// <summary>
        /// ProDOS file type.
        /// </summary>
        /// <remarks>
        /// We use the stored value if available (ADF/AS/NAPS).
        /// </remarks>
        public byte FileType { get; set; } = 0;

        /// <summary>
        /// ProDOS aux type.
        /// </summary>
        /// <remarks>
        /// We use the stored value if available (ADF/AS/NAPS).
        /// </remarks>
        public ushort AuxType { get; set; } = 0;

        /// <summary>
        /// HFS file type.
        /// </summary>
        /// <remarks>
        /// We use the stored value if available (ADF/AS/NAPS/host).
        /// </remarks>
        public uint HFSFileType { get; set; } = 0;

        /// <summary>
        /// HFS creator.
        /// </summary>
        /// <remarks>
        /// We use the stored value if available (ADF/AS/NAPS/host).
        /// </remarks>
        public uint HFSCreator { get; set; } = 0;

        /// <summary>
        /// ProDOS access flags.
        /// </summary>
        /// <remarks>
        /// We use the stored value if available (ADF/AS).  Otherwise we just look at the host
        /// permission bits.  Default to ProDOS "unlocked".
        /// </remarks>
        public byte Access { get; set; } = FileAttribs.FILE_ACCESS_UNLOCKED;

        /// <summary>
        /// True if any of the ProDOS or HFS file types is nonzero.
        /// </summary>
        /// <remarks>
        /// This is one of the tests used to determine whether MacZip needs to create an ADF
        /// header file entry.
        /// </remarks>
        public bool HasNonZeroTypes {
            get {
                return FileType != 0 || AuxType != 0 || HFSFileType != 0 || HFSCreator != 0;
            }
        }

        public override string ToString() {
            return "[AddFileEntry: hasData=" + HasDataFork + " '" + FullDataPath +
                "' hasRsrc=" + HasRsrcFork + " '" + FullRsrcPath +
                "' type=$" + FileType.ToString("x2") + " aux=$" + AuxType.ToString("x4") +
                " hfsType=" + MacChar.StringifyMacConstant(HFSFileType) +
                " hfsCreator=" + MacChar.StringifyMacConstant(HFSCreator) +
                " stPath='" + StorageDir + "' stName='" + StorageName + "']";
        }
    }
}
