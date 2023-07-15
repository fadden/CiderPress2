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

// TODO(maybe): add an indexer for accessing file slots in a directory.  We might want
// to populate empty / deleted slots with NO_ENTRY, so that creating or deleting files
// doesn't cause a file's index to change.  This could also be a path toward implementing
// an "undelete" feature.

namespace DiskArc {
    /// <summary>
    /// Information for a single file entry.  These are created by the filesystem or file
    /// archive object.
    /// </summary>
    /// <remarks>
    /// <para>Various fields may be modified directly, but the behavior is slightly different
    /// for filesystems and file archives.  In general, the actual change will be limited to
    /// what the filesystem or archive allows.  Attempting to set illegal values, e.g. setting
    /// the file type to SYS for a ProDOS directory or a DOS file, will be ignored or mapped to
    /// correct behavior.  Attempting to set the filename to an illegal value will cause an
    /// exception.</para>
    /// <para>The goal is to allow attributes to be copied without regard to the target format,
    /// with the implementation doing its best to represent the values.  The only exception
    /// to this rule is filenames.  Illegal values will be rejected with an exception.</para>
    ///
    /// <strong>Disk images:</strong>
    /// <para>This object generally represents the data in the directory entry.</para>
    ///
    /// <para>Modifications are visible to the API immediately but, for efficiency reasons,
    /// might not be saved to disk until <see cref="SaveChanges"/> is called.  Closing the
    /// filesystem without saving the file entry changes could result in those changes being lost.
    /// While a file is open and modified, the values returned may not be consistent
    /// with the file's actual contents.  For example, the EOF may not be updated until
    /// the file descriptor is flushed or closed.</para>
    ///
    /// <para>A "fake" entry may be created for the volume directory, which is not part of
    /// any other directory.  Some fields may behave differently, e.g. the syntax for the
    /// filename (volume name) may be different.</para>
    ///
    /// <para>Iterating on a directory entry will provide a list of all the files in the
    /// directory.  If you prefer a simple list, or you need a copy to iterate over while
    /// modifying, use e.g. dirEntry.ToList() (requires "using System.Linq" to define an
    /// extension method).</para>
    ///
    /// <strong>File archives:</strong>
    /// <para>Entries are part of a single archive list, and therefore do not have parents or
    /// children.</para>
    /// <para>All changes must be made as part of a transaction.  The attribute changes may
    /// not be visible until the transaction is committed.</para>
    /// </remarks>
    //
    // Implementation note:
    // Disk images and file archives are very different.  Simple filenames and partial paths
    // require different handling, and data compression affects file size reporting (including
    // whether or not they can even be known inexpensively).  It is tempting to split this class
    // into separate interfaces for disk vs. archive.
    //
    // In practice, files on DOS / ProDOS / HFS are very different from each other, as are files
    // in ShrinkIt / Binary II / ZIP.  The set of allowable file types, access flags, and the
    // possible range of dates in time stamps are all very different.  Even if we narrow the scope
    // to a single general category, we're still dealing with many implementation-specific issues.
    //
    // Splitting into filesystem vs. archive is more harmful than helpful, because having a
    // common way to reference a file entry can be very useful.
    public interface IFileEntry : IEnumerable<IFileEntry> {
        /// <summary>
        /// Object to use for a nonexistent entry.
        /// </summary>
        /// <remarks>
        /// Could just use null, but I'm trying to play by the C# nullable rules, and this
        /// is cleaner than having null checks everywhere.
        /// </remarks>
        static IFileEntry NO_ENTRY = new NullFileEntry();

        /// <summary>
        /// Value to use when no pathname directory separator character is defined.
        /// </summary>
        /// <remarks>
        /// This is Unicode noncharacter-FFFE, which is guaranteed not to be a valid character.
        /// </remarks>
        const char NO_DIR_SEP = '\ufffe';

        /// <summary>
        /// True if this file entry is valid.  Entries may become invalid if they are
        /// deleted from the filesystem or archive, or if the containing object is closed.
        /// </summary>
        bool IsValid { get; }

        /// <summary>
        /// True if the file's coherency is in doubt.
        /// </summary>
        /// <remarks>
        /// Set if the file's structure is intact, but suspicious.  For example, if the file
        /// shares blocks with other files.  Reading is allowed, modifications to the file
        /// (writing to it, deleting it, etc.) are not.
        /// </remarks>
        bool IsDubious { get; }

        /// <summary>
        /// True if the file's structure is suspicious or damaged.  The file cannot be accessed.
        /// </summary>
        /// <remarks>
        /// The filesystem will also be marked "dubious", which disables writes.
        /// </remarks>
        bool IsDamaged { get; }

        /// <summary>
        /// True if this entry is a volume directory or subdirectory.
        /// </summary>
        bool IsDirectory { get; }

        /// <summary>
        /// True if the file has a data fork (even if it's empty).
        /// </summary>
        /// <remarks>
        /// All regular files on disk images have a data fork, but directory files may not have
        /// one.  File archive entries may only have a resource fork, or might be disk images.
        /// </remarks>
        bool HasDataFork { get; }

        /// <summary>
        /// True if the file has a resource fork (even if it's empty).
        /// </summary>
        bool HasRsrcFork { get; }

        /// <summary>
        /// File archives: true if this entry is a disk image.
        /// </summary>
        /// <remarks>
        /// <para>This allows applications to display disk image records differently.
        /// None of the other fields here will change meaning, although some (like file
        /// type) may not be settable.</para>
        /// <para>A disk image entry does not have data or resource forks, though some of
        /// the data fork fields (like DataLength) are meaningful for disk images.</para>
        /// <para>This is never true for files on a filesystem.</para>
        /// </remarks>
        bool IsDiskImage { get; }

        /// <summary>
        /// Disk images: entry of directory that contains this file, or NO_ENTRY if this
        /// is the volume dir.
        /// </summary>
        /// <remarks>
        /// <para>This wouldn't make sense if we allowed "hard" links, but none of our
        /// filesystems do.</para>
        /// <para>File archives: always NO_ENTRY.</para>
        /// </remarks>
        IFileEntry ContainingDir { get; }

        /// <summary>
        /// Disk images: number of files and directories that are children of this
        /// directory.
        /// </summary>
        /// <remarks>
        /// <para>Only meaningful for directories.  For regular files this will always
        /// return zero.</para>
        /// <para>File archives: always zero.</para>
        /// </remarks>
        int Count { get; }

        /// <summary>
        /// Filename, converted to printable Unicode (Basic Multilingual Plane only).  For
        /// file archives, this may be a partial pathname.
        /// </summary>
        /// <remarks>
        /// <para>The original may be ASCII, high ASCII, or Mac OS Roman.  All 8-bit values
        /// are allowed, so some filenames may include null and control characters.  These are
        /// mapped to and from code points with printable glyphs, in a filesystem-specific
        /// or archive-specific fashion.  The transformation may not be reversible.</para>
        ///
        /// <para>Illegal filenames found on the disk or archive are presented without
        /// correction.  Attempting to set a filename to an illegal value causes an exception
        /// to be thrown.  The application is responsible for making names compatible.</para>
        ///
        /// <para>Filenames are expected to be unique on a disk image, though they may not be
        /// on a damaged filesystem.  You may not set a filename to be the same as an existing
        /// file in the same directory.  In a file archive, duplicates may be allowed.</para>
        ///
        /// <para>Setting the filename on a disk image may require writing to multiple blocks,
        /// e.g. renaming a ProDOS directory requires updating the directory header.  Other
        /// pending changes may be flushed at the same time.</para>
        ///
        /// <para>Renaming the volume directory entry renames the volume.  In some cases the
        /// syntax rules are different for volumes.  If the filesystem doesn't have a volume
        /// name at all, the call has no effect.</para>
        /// </remarks>
        /// <exception cref="ArgumentException">Invalid file name.</exception>
        /// <exception cref="IOException">Disk access failure, or duplicate filename.</exception>
        string FileName { get; set; }

        /// <summary>
        /// Character used to separate directory names in a pathname.  Must be ASCII.
        /// </summary>
        /// <remarks>
        /// <para>File archives: this is used to break a partial pathname down into individual
        /// filename components.  The value will be set to a default value when a record is
        /// created.</para>
        /// <para>In most types of archives (NuFX being an exception), the character cannot be
        /// changed, and attempts to do so will be ignored.  It is best to work with the existing
        /// value.  Newly-created records will have an appropriate default set.</para>
        /// <para>Disk images: this will return the "standard" separator character for the
        /// filesystem.  If the filesystem is not hierarchical, this will return
        /// <see cref="NO_DIR_SEP"/>.</para>
        /// </remarks>
        /// <exception cref="ArgumentException">Invalid separator character.</exception>
        char DirectorySeparatorChar { get; set; }

        /// <summary>
        /// Filename as it is stored natively.
        /// </summary>
        /// <remarks>
        /// <para>Useful for certain filesystem-specific applications, like viewing or adding
        /// flashing characters in a DOS filename, but not something most applications will need
        /// to use.  Implementations may choose to respect some or all of the filesystem's naming
        /// rules, so attempts to set an illegal filename may be rejected.</para>
        /// <para>The array length will be the exact length of the filename.  There will usually
        /// be a 1:1 correspondence between bytes here and characters in
        /// <see cref="FileName"/> (i.e. FileName.Length == RawFileName.Length), but that
        /// is not guaranteed.</para>
        /// <para>When setting the field, the byte[] buffer must be the exact length of the
        /// filename.  Do not put a length byte at the start or padding at the end.</para>
        /// </remarks>
        /// <exception cref="ArgumentException">Invalid file name.</exception>
        byte[] RawFileName { get; set; }

        /// <summary>
        /// Full pathname to file.  This is intended for display, e.g. in error messages, not
        /// parsing.  The exact format is specific to the filesystem or archive.
        /// </summary>
        /// <remarks>
        /// <para>File archives: this is the same as FileName.</para>
        /// <para>Disk images: the name is generated from the file hierarchy.  Don't try to
        /// parse this string.  If a filename has illegal characters, e.g. filename separators
        /// like '/' or ':', they will appear un-escaped.  To get a list of filename components,
        /// walk up the hierarchy with <see cref="ContainingDir"/> instead.</para>
        /// </remarks>
        string FullPathName { get; }

        /// <summary>
        /// True if this file entry is capable of storing ProDOS-style file types.
        /// </summary>
        /// <remarks>
        /// In some situations the set of types will be limited, e.g. DOS 3.3 only allows
        /// eight types.
        /// </remarks>
        bool HasProDOSTypes { get; }

        /// <summary>
        /// ProDOS file type.
        /// </summary>
        /// <remarks>
        /// <para>GS/OS expanded the field to 16 bits, but didn't appear to use the extra bits for
        /// anything.  The HFS file type requires 32 bits, so the field was too wide for ProDOS
        /// and too narrow for HFS.</para>
        ///
        /// <para>Not all filesystems support all 256 possible values, and not all files may
        /// have their filetypes set (e.g. ProDOS directories are always $0f, and non-directory
        /// files are not allowed to use that).  Invalid values will be mapped to something
        /// appropriate when setting.</para>
        ///
        /// <para>Setting this field on an HFS volume has no effect.  It will not cause the AFE
        /// equivalent to be written to the HFS type fields.</para>
        /// </remarks>
        byte FileType { get; set; }

        /// <summary>
        /// ProDOS auxiliary type.
        /// </summary>
        /// <remarks>
        /// <para>GS/OS expanded this to 32 bits, but didn't appear to use the extra bits for
        /// anything.  The wider field has enough room for the HFS creator, but GS/OS didn't
        /// use it that way, preferring to return something ProDOS-compatible for HFS files
        /// (like BIN $0000).</para>
        ///
        /// <para>Most filesystems don't support arbitrary auxtypes.  If a value isn't
        /// acceptable, or the auxtype cannot be changed, the new value will be ignored
        /// by the setter.</para>
        /// </remarks>
        ushort AuxType { get; set; }

        /// <summary>
        /// True if this file entry is capable of storing HFS file types.
        /// </summary>
        /// <remarks>
        /// <para>File archives: for archives that support multiple operating systems, this will
        /// only be true if the OS type is set to HFS, or is set to ProDOS and the archive format
        /// also supports option lists.</para>
        /// <para>Disk images: on ProDOS, only forked files can store HFS file types.</para>
        /// </remarks>
        bool HasHFSTypes { get; }

        /// <summary>
        /// HFS file type, or 0 if unset / not present.
        /// </summary>
        uint HFSFileType { get; set; }

        /// <summary>
        /// HFS creator, or 0 if unset / not present.
        /// </summary>
        uint HFSCreator { get; set; }

        /// <summary>
        /// ProDOS-style access flags.
        /// </summary>
        /// <remarks>
        /// For filesystems like DOS or HFS, where files are simply locked or unlocked, the
        /// "writable" bit determines the state.  The "get" call will treat an unlocked file
        /// as having full permissions, while a locked file will have the appropriate set of
        /// permissions removed.
        /// (<see cref="Defs.ACCESS_UNLOCKED"/> or <see cref="Defs.ACCESS_LOCKED"/>).
        /// </remarks>
        byte Access { get; set; }

        /// <summary>
        /// Creation date/time.
        /// </summary>
        /// <remarks>
        /// The granularity and range of supported dates is type-specific.
        /// </remarks>
        DateTime CreateWhen { get; set; }

        /// <summary>
        /// Modification date/time.
        /// </summary>
        /// <remarks>
        /// The granularity and range of supported dates is type-specific.
        /// </remarks>
        DateTime ModWhen { get; set; }

        /// <summary>
        /// Number of bytes actually required to store all parts of the file.
        /// </summary>
        /// <remarks>
        /// <para>Disk images: the storage size is in bytes, but is always a multiple of
        /// the allocation unit size.</para>
        /// <para>On some filesystems, this value is stored in the directory entry and may not
        /// be accurate.  If the initial filesystem scan is not performed, this value may
        /// reflect the directory entry contents rather than the true size.</para>
        /// <para>File archives: this is the sum of the compressed lengths of the data and
        /// resource forks.</para>
        /// </remarks>
        long StorageSize { get; }

        /// <summary>
        /// Length, in bytes, of data fork, or 0 if the entry has no data fork.
        /// </summary>
        /// <remarks>
        /// <para>In a file archive, this is the uncompressed length.  If the value cannot be
        /// known without uncompressing the data, -1 will be returned.</para>
        /// <para>If the entry represents a disk image, this will hold the length in bytes of
        /// the image file.</para>
        /// </remarks>
        long DataLength { get; }

        /// <summary>
        /// Length, in bytes, of resource fork, or 0 if the entry has no resource fork.
        /// </summary>
        /// <remarks>
        /// <para>In a file archive, this is the uncompressed length.  If the value cannot be
        /// known without uncompressing the data, -1 will be returned.</para>
        /// <para>Use the <see cref="HasRsrcFork"/> property, rather than this value, to
        /// determine whether a file has a resource fork.</para>
        /// </remarks>
        long RsrcLength { get; }

        /// <summary>
        /// Comment for this entry, or an empty string if there isn't one.
        /// </summary>
        /// <remarks>
        /// <para>These are usually found only in file archives.</para>
        /// <para>The character set and maximum length of the comment vary.  Unsupported
        /// characters will be replaced, excess characters will be ignored.</para>
        /// </remarks>
        string Comment { get; set; }


        /// <summary>
        /// Obtains information for the specified file part.
        /// </summary>
        /// <remarks>
        /// <para>In some cases the sum of the storage sizes of the data and resource forks may
        /// not match the StorageSize property value, e.g. ProDOS files have an additional
        /// "extended file info" block that is not part of either fork.</para>
        /// </remarks>
        /// <param name="part">Which part to query.</param>
        /// <param name="length">Result: length of part, in bytes.  Will be -1 if the part does
        ///   not exist.</param>
        /// <param name="storageLength">Result: storage size of part, in bytes.  Will be zero
        ///   if the part does not exist.</param>
        /// <param name="format">Result: compression format used to store part.</param>
        /// <returns>True if part exists.  If this returns false, the other result values
        ///   are invalid.</returns>
        bool GetPartInfo(Defs.FilePart part, out long length, out long storageSize,
            out Defs.CompressionFormat format);

        /// <summary>
        /// Disk images: saves all pending file entry changes to disk.  If no changes are pending,
        /// this returns without doing anything.  Failure to call this may result in changes
        /// being discarded.
        /// </summary>
        /// <remarks>
        /// <para>While changes made to the file entry happen immediately to the internal object,
        /// they may not be not written to disk until explicitly requested or the filesystem is
        /// disposed.  This call exists so that changes can be made to multiple attributes without
        /// having to write the new values to disk after each.  The filesystem code will do
        /// its best to flush changes to disk before the object is discarded, but explicitly
        /// saving them is recommended.</para>
        /// <para>File archives: changes to archive entry attributes are made through the
        /// transaction mechanism, so this does nothing.</para>
        /// </remarks>
        /// <exception cref="IOException">One of the following:
        ///   <list type="bullet">
        ///     <item>The filesystem is read-only.</item>
        ///     <item>Disk access failure.</item>
        ///     <item>Entry is invalid, damaged, or dubious.</item>
        ///   </list>
        /// </exception>
        void SaveChanges();

        /// <summary>
        /// Compares the argument to the entry's filename.  The comparison is performed
        /// according to the rules of the filesystem or archive; it may be case-sensitive
        /// or case-insensitive, or follow a custom order (e.g. Mac OS Roman).
        /// </summary>
        /// <remarks>
        /// <para>This compares the filename as a simple string.  For file archives, this means
        /// that the comparison is performed without taking filename separators into account.  The
        /// other form of the call is usually more appropriate.</para>
        /// <para>This doesn't test the validity of either filename.</para>
        /// </remarks>
        /// <param name="fileName">String to compare.</param>
        /// <returns>Less than zero if the entry's filename precedes the argument in
        ///   lexical sort order, zero if it's in the same position, or greater than
        ///   zero if it follows the argument.</returns>
        int CompareFileName(string fileName);

        /// <summary>
        /// Compares the argument to the entry's filename.  The comparison is performed
        /// according to the rules of the filesystem or archive; it may be case-sensitive
        /// or case-insensitive, or follow a custom order (e.g. Mac OS Roman).
        /// </summary>
        /// <remarks>
        /// <para>This only useful for file archives, because filesystem entries are always
        /// simple filenames.  Partial pathnames are broken into individual components before
        /// they are compared.</para>
        /// <para>This doesn't test the validity of either filename.</para>
        /// </remarks>
        /// <param name="fileName">String to compare.</param>
        /// <returns>Less than zero if the entry's filename precedes the argument in
        ///   lexical sort order, zero if it's in the same position, or greater than
        ///   zero if it follows the argument.</returns>
        int CompareFileName(string fileName, char fileNameSeparator);
    }

    internal class NullFileEntry : IFileEntry {
        public bool IsValid => throw new NotImplementedException();
        public bool IsDubious => throw new NotImplementedException();
        public bool IsDamaged => throw new NotImplementedException();

        public bool IsDirectory => throw new NotImplementedException();
        public bool HasDataFork => throw new NotImplementedException();
        public bool HasRsrcFork => throw new NotImplementedException();
        public bool IsDiskImage => throw new NotImplementedException();

        public IFileEntry ContainingDir => throw new NotImplementedException();
        public int Count => throw new NotImplementedException();

        public string FileName {
            get => throw new NotImplementedException();
            set => throw new NotImplementedException();
        }
        public char DirectorySeparatorChar {
            get => throw new NotImplementedException();
            set => throw new NotImplementedException();
        }
        public byte[] RawFileName {
            get => throw new NotImplementedException();
            set => throw new NotImplementedException();
        }
        public string FullPathName => throw new NotImplementedException();

        public bool HasProDOSTypes => throw new NotImplementedException();
        public byte FileType {
            get => throw new NotImplementedException();
            set => throw new NotImplementedException();
        }
        public ushort AuxType {
            get => throw new NotImplementedException();
            set => throw new NotImplementedException();
        }

        public bool HasHFSTypes => throw new NotImplementedException();
        public uint HFSFileType {
            get => throw new NotImplementedException();
            set => throw new NotImplementedException();
        }
        public uint HFSCreator {
            get => throw new NotImplementedException();
            set => throw new NotImplementedException();
        }

        public byte Access {
            get => throw new NotImplementedException();
            set => throw new NotImplementedException();
        }
        public DateTime CreateWhen {
            get => throw new NotImplementedException();
            set => throw new NotImplementedException();
        }
        public DateTime ModWhen {
            get => throw new NotImplementedException();
            set => throw new NotImplementedException();
        }
        public long StorageSize => throw new NotImplementedException();
        public long DataLength => throw new NotImplementedException();
        public long RsrcLength => throw new NotImplementedException();

        public string Comment {
            get => throw new NotImplementedException();
            set => throw new NotImplementedException();
        }

        public bool GetPartInfo(Defs.FilePart part, out long length, out long storageSize,
                out Defs.CompressionFormat format) {
            throw new NotImplementedException();
        }
        public void SaveChanges() { throw new NotImplementedException(); }
        public int CompareFileName(string fileName) { throw new NotImplementedException(); }
        public int CompareFileName(string fileName, char fileNameSeparator) {
            throw new NotImplementedException();
        }

        public IEnumerator<IFileEntry> GetEnumerator() { throw new NotImplementedException(); }
        IEnumerator IEnumerable.GetEnumerator() { throw new NotImplementedException(); }
    }
}
