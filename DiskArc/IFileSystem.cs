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

namespace DiskArc {
    /// <summary>
    /// <para>General interface for access to a disk filesystem.  This is focused on the
    /// filesystems used on the Apple II and early Macintosh computers.</para>
    /// <para>The filesystem may be in "file access" or "raw access" mode.  In file-access mode,
    /// files may be created, opened, deleted, and so on, but reading and writing blocks and
    /// sectors directly is not allowed.  In raw-access mode, file operations are unavailable,
    /// but reads and writes of blocks and sectors are allowed.  When initially created, the
    /// object will be in raw-access mode.</para>
    /// <para>Switching to raw-access mode has the secondary effect of purging all cached
    /// file data.  Switching to file-access mode will cause a re-scan of the volume, which
    /// can either be a simple "is this still something I recognize" or a deep verification
    /// pass.</para>
    /// </summary>
    ///
    /// <remarks>
    /// <para>There is no provision for filesystem-specific activities, such as "crunching" a
    /// UCSD Pascal volume, in this interface.  Such features may be exposed directly by the
    /// specific filesystem instance.</para>
    ///
    /// <para>While "file" and "raw" access modes are mutually exclusive, both may effectively
    /// be in use when a filesystem has an embedded volume.  In such a case, the embedded
    /// volume must exist exclusively in an area inaccessible to file commands.  Any files
    /// that overlap must be marked "dubious" to prevent modification or deletion.</para>
    ///
    /// <para>Implementations are encouraged, but not required, to have all modifications
    /// flushed whenever all files are closed, to reduce the possibility of a program crash
    /// leading to a disk file in an inconsistent state.  The interface includes IDisposable
    /// so that callers can use "using" directives to ensure that pending modifications are
    /// written before the object is discarded.  Implementations are required to discard
    /// all cached state when switching to raw-access mode, even if opened read-only.  This
    /// provides a way for the application to make arbitrary changes to the underlying chunk
    /// data without having to recreate the filesystem object.</para>
    ///
    /// <para>The full interface definition also includes a few static functions:</para>
    /// <para><c>static TestResult TestImage(IChunkAccess chunkAccess)</c>:
    ///   Returns a "yes", "no", or "maybe" value depending on whether the disk image
    ///   appears to contain the filesystem.  If the test result is "yes", PrepareFileAccess()
    ///   will succeed.</para>
    /// <para><c>static bool IsSizeAllowed(long size)</c>:
    ///   Returns true if a volume of the specified size (in bytes) can be created by
    ///   the Format() method.</para>
    /// <para><c>static string AdjustFileName(string fileName)</c>:
    ///   Returns a copy of the filename that has been adjusted to be accepted by the
    ///   filesystem (shortened, illegal chars removed, etc).</para>
    /// </remarks>
    ///
    /// <para>Some things that could be supported but currently aren't:</para>
    /// <list type="bullet">
    ///   <item>Empty block zeroing (for compression efficiency or privacy); a full
    ///     scrub could also erase filenames of deleted files.</item>
    ///   <item>Directory entry sorting (via Swap(ent1, ent2), where ent1 and ent2 are files in
    ///     the same directory).</item>
    ///   <item>Un-deleting files (requires multiple interfaces to find/test/undelete).</item>
    ///   <item>Direct manipulation of block/sector in-use bitmaps (e.g. to create embedded
    ///     sub-volumes).</item>
    ///   <item>Volume usage map generation.  (Currently done, just not exposed.)  Retrieve
    ///     map, with option to identify the file using block N.</item>
    /// </list>
    public interface IFileSystem : IDiskContents, IDisposable {
        /// <summary>
        /// Characteristics of this class of filesystem.  All instances of a given class
        /// return the same values.
        /// </summary>
        FS.FSCharacteristics Characteristics { get; }

        /// <summary>
        /// True if filesystem was opened in read-only mode, or if damaged was detected and
        /// we're in file-access mode.
        /// </summary>
        bool IsReadOnly { get; }

        /// <summary>
        /// True if the filesystem is damaged in a way that makes file modification hazardous.
        /// When set, files may be read but not written.
        /// </summary>
        /// <remarks>
        /// <para>This value is for the filesystem as a whole, e.g. when there are files whose
        /// blocks are not marked as in-use in the volume bitmap.  For situations where the
        /// volume is safe but files are at risk, such as two files that overlap, only the
        /// individual files are marked as dubious.</para>
        /// <para>This flag only applies to file-access mode, and is not meaningful in
        /// raw-access mode.</para>
        /// <para>A false value here does not mean that the filesystem is undamaged.  It only
        /// means that no damage has been detected thus far.  The value may change from false
        /// to true as files are accessed, though doing so is discouraged.</para>
        /// </remarks>
        bool IsDubious { get; }

        /// <summary>
        /// Errors, warnings, and points of interest noted during the filesystem scan.
        /// </summary>
        /// <remarks>
        /// There should be a direct correlation between the presence of error notes and
        /// the raising of the IsDubious flag.
        /// </remarks>
        CommonUtil.Notes Notes { get; }

        /// <summary>
        /// Switches to file-access mode.  Must be called before accessing files.
        /// </summary>
        /// <remarks>
        /// <para>This may perform a minimal series of checks, or may do a complete
        /// filesystem scan.  If significant structural issues are discovered, the
        /// <see cref="IsDubious"/> flag will be set.</para>
        ///
        /// <para>If a scan is performed, any issues found will be reported in the Notes.  This
        /// call will generally not throw an exception unless the filesystem has been modified to
        /// the point where can no longer be recognized (i.e. the TestImage() call would fail).
        /// If an exception is thrown, the filesystem will remain in raw-access mode.</para>
        /// <para>This has no effect if the filesystem is already in file-access mode.</para>
        /// </remarks>
        /// <param name="doScan">If true, do a full filesystem scan.  If false, do the
        ///   minimum work necessary to prepare the filesystem.</param>
        /// <exception cref="DAException">Volume is not recognizable.</exception>
        void PrepareFileAccess(bool doScan);

        /// <summary>
        /// Prepares filesystem for sector editing.  This will fail if any files are open.
        /// </summary>
        /// <remarks>
        /// <para>Files open in embedded volumes will also prevent the transition.  The embedded
        /// volumes will be disposed.</para>
        /// <para>On success, all IFileEntry objects associated with the filesystem will be
        /// invalidated.</para>
        /// <para>This has no effect if the filesystem is already in raw-access mode.</para>
        /// </remarks>
        /// <exception cref="DAException">One or more files are open.</exception>
        void PrepareRawAccess();

        /// <summary>
        /// A chunk source that may be used to read and write blocks in the filesystem.  Access
        /// will be limited unless the filesystem is configured for "raw" access.
        /// </summary>
        /// <remarks>
        /// <para>If the filesystem is contained within an IDiskImage or Partition, this will
        /// reference the same chunk access object available there (but will be gated
        /// independently).  This may not reflect changes cached by the filesystem code unless
        /// <see cref="Flush"/> is called.</para>
        /// </remarks>
        GatedChunkAccess RawAccess { get; }

        /// <summary>
        /// Flushes pending changes.  Issues Flush/SaveChanges calls on all open files.
        /// </summary>
        /// <remarks>
        /// <para>This has no effect in "raw" access mode.</para>
        /// <para>This may not call SaveChanges on file entries that aren't associated with
        /// open files (i.e. attribute edits).</para>
        /// </remarks>
        void Flush();

        /// <summary>
        /// Returns the amount of free space remaining, in bytes.
        /// </summary>
        /// <remarks>
        /// This will be in multiples of the chunk size.  This is only valid when in
        /// file-access mode, and will return -1 if not.
        /// </remarks>
        long FreeSpace { get; }

        /// <summary>
        /// <para>Searches for filesystems that are embedded within the current filesystem.
        /// Useful for "hybrid" disk images.</para>
        /// <para>This call can only be made while the filesystem is in file-access mode.
        /// Further, the PrepareFileAccess() call must have been made with "doScan" set to true, so
        /// that we can tell which blocks are in use but not associated with files.</para>
        /// <para>All partitions will be analyzed before returning.  There is no need to
        /// call AnalyzePartition() on the partitions.  (At present, calling it may actually
        /// cause problems, as the wrong part of the hybrid disk might be found.)</para>
        /// <para>If embedded volumes were found by an earlier call, the same IMultiPart
        /// object will be returned.</para>
        /// </summary>
        /// <remarks>
        /// <para>Embedded volumes aren't arranged like partitions -- they can overlap with the
        /// host filesystem -- so IMultiPart isn't quite right.  It does have a number of
        /// things we want, and it's convenient, so we use it anyway.</para>
        /// <para>The object returned is owned by the filesystem object, and will be disposed when
        /// the filesystem is disposed or switched to raw-access mode.  Do not wrap uses of the
        /// return value with "using".</para>
        /// </remarks>
        /// <returns>List of volumes found, or null if there were none.</returns>
        /// <exception cref="IOException">Filesystem is not in file-access mode.</exception>
        IMultiPart? FindEmbeddedVolumes();

        /// <summary>
        /// Formats the volume, overwriting existing data.  The filesystem must be in
        /// raw-access mode.
        /// <remarks>
        /// <para>The disk will be formatted to fill the chunk source completely.  If the chunk
        /// source is over-sized (e.g. 70000 blocks for a 32MB ProDOS volume), the call will
        /// fail.  To create a filesystem inside a larger image, create a new IChunkSource
        /// with tighter bounds.</para>
        /// <para>This does not zero out the disk blocks, so a disk formatted for both DOS and
        /// ProDOS could be recognized as either.  Use <see cref="IChunkAccess.Initialize()"/>
        /// to zero the disk before calling here.  (Exception: CP/M erases all blocks to
        /// 0xe5.)</para>
        /// </remarks>
        /// </summary>
        /// <param name="volumeName">Volume name.</param>
        /// <param name="volumeNum">Volume number.  If invalid, a default number will be
        ///   substituted.</param>
        /// <param name="reserveBoot">If set, perform the minimum steps needed to create a
        ///   bootable disk.  If not set, some storage normally reserved for the
        ///   OS boot process may be reclaimed.</param>
        /// <exception cref="IOException">Error accessing underlying storage, or storage
        ///   is read-only, or filesystem is in file-access mode.</exception>
        /// <exception cref="ArgumentException">Invalid volume name.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The size of the underlying storage
        ///   is incompatible with this filesystem.</exception>
        void Format(string volumeName, int volumeNum, bool reserveBoot);

        /// <summary>
        /// Obtains the file entry for the top-level volume directory.  The filesystem must
        /// be in file-access mode.
        /// </summary>
        /// <returns>Volume directory file entry.</returns>
        /// <exception cref="IOException">Filesystem is not in file-access mode.</exception>
        IFileEntry GetVolDirEntry();

        /// <summary>
        /// Argument to <see cref="CreateFile"/>.
        /// </summary>
        /// <remarks>
        /// The "Extended" mode is only meaningful on ProDOS disks.  It's accepted on HFS, but
        /// unnecessary.
        /// </remarks>
        enum CreateMode {
            Unknown = 0, File, Extended, Directory
        }

        /// <summary>
        /// Creates a new file or directory.  The file will have default values for file
        /// type, aux type, and access flags, and will have the current date and time for
        /// creation and modification timestamps (or something appropriate if the filesystem
        /// doesn't support the current year).
        /// </summary>
        /// <param name="dirEntry">Directory in which to create the file.</param>
        /// <param name="fileName">Name of file to create.</param>
        /// <param name="mode">Regular file, extended file, or directory.</param>
        /// <returns>Entry for new file.</returns>
        /// <exception cref="IOException">One of the following:
        ///   <list type="bullet">
        ///     <item>The filesystem is read-only.</item>
        ///     <item>Invalid filename.</item>
        ///     <item>Extended or directory file specified but not supported.</item>
        ///     <item>Directory file is open.</item>
        ///     <item>Directory file argument is not a directory.</item>
        ///     <item>Directory file argument is from a different filesystem.</item>
        ///     <item>A file with that name already exists.</item>
        ///   </list>
        /// </exception>
        /// <exception cref="DiskFullException">Disk full, or volume directory full.</exception>
        IFileEntry CreateFile(IFileEntry dirEntry, string fileName, CreateMode mode);

        /// <summary>
        /// Adds a resource fork to an existing file.  If the file already has a resource fork,
        /// the call does nothing.
        /// </summary>
        /// <param name="entry">File to extend.</param>
        /// <exception cref="IOException">One of the following:
        ///   <list type="bullet">
        ///     <item>The filesystem is read-only.</item>
        ///     <item>File is open.</item>
        ///     <item>File can't be extended (is a directory).</item>
        ///     <item>Filesystem does not support extended files.</item>
        ///   </list>
        /// </exception>
        /// <exception cref="FileNotFoundException">File is from a different filesystem.</exception>
        /// <exception cref="DiskFullException">Disk full.</exception>
        void AddRsrcFork(IFileEntry entry);

        /// <summary>
        /// Moves a file or directory to another directory on the same filesystem.
        /// </summary>
        /// <remarks>
        /// <para>This is only meaningful on hierarchical filesystems.  On flat filesystems, or
        /// when the old and new directories are the same, this simply renames the file.</para>
        /// <para>Moving a directory into one of its own descendants is not allowed, as doing so
        /// would render that part of the tree inaccessible.</para>
        /// <para>The <paramref name="newFileName"/> argument exists because moving dir1::file1
        /// to dir2 isn't possible if dir2::file1 already exists, unless file1 is renamed.
        /// Renaming it before moving it could be complicated if the desired new name is currently
        /// in use by another file in dir1.</para>
        /// <para>This cannot be used on the volume directory, even for a simple rename.</para>
        /// </remarks>
        /// <param name="entry">File to move.</param>
        /// <param name="destDir">Destination directory.</param>
        /// <param name="newName">Name to give file in new directory.</param>
        /// <exception cref="IOException">One of the following:
        ///   <list type="bullet">
        ///     <item>The filesystem is read-only.</item>
        ///     <item>File is open.</item>
        ///     <item>Duplicate filename.</item>
        ///     <item>The file being moved is a directory, and the destination directory is
        ///       a child of the file being moved.</item>
        ///   </list>
        /// </exception>
        /// <exception cref="ArgumentException">One of the following:
        ///   <list type="bullet">
        ///     <item>Invalid file name.</item>
        ///     <item>Attempting to move the volume directory.</item>
        ///     <item>Source and destination directories have different dirent lengths.</item>
        ///   </list>
        /// </exception>
        /// <exception cref="FileNotFoundException">File is from a different filesystem.</exception>
        /// <exception cref="DiskFullException">Disk full.</exception>
        void MoveFile(IFileEntry entry, IFileEntry destDir, string newFileName);

        /// <summary>
        /// Deletes a file or directory, releasing the file's storage.
        /// </summary>
        /// <remarks>
        /// <para>The file's access permissions are ignored.  It is not necessary to "unlock" a
        /// file before deleting it.</para>
        /// <para>Directories cannot be deleted if they still contain any files.</para>
        ///
        /// <para>On success, the IFileEntry will be marked as invalid.  Attempting to access
        /// a deleted file will fail.</para>
        /// </remarks>
        /// <param name="fileEntry">File to delete.</param>
        /// <exception cref="ArgumentException">File is the volume directory.</exception>
        /// <exception cref="IOException">One of the following:
        ///   <list type="bullet">
        ///     <item>The filesystem is read-only.</item>
        ///     <item>File is open.</item>
        ///     <item>File is from a different filesystem.</item>
        ///     <item>File is a non-empty directory.</item>
        ///   </list>
        /// </exception>
        /// <exception cref="FileNotFoundException">File is from a different filesystem.</exception>
        void DeleteFile(IFileEntry entry);

        /// <summary>
        /// Access mode argument, used when opening a file.
        /// </summary>
        enum FileAccessMode {
            Unknown = 0,
            ReadOnly,       // file may be read, but not modified
            ReadWrite       // file may be read and written
        }

        /// <summary>
        /// Opens the specified file entry.
        /// </summary>
        /// <remarks>
        /// <para>There are very few reasons why this should fail in the normal course of
        /// operation.  It will fail if the file is already open, or if it is too damaged
        /// to access.</para>
        ///
        /// <para>Read-write access is not affected by the permission bits stored in filesystem
        /// directories.  It is not necessary to "unlock" a file before opening it for
        /// writing.</para>
        ///
        /// <para>A given file part may be opened multiple times simultaneously for read-only
        /// access, but only once for read-write.  Having the data and resource forks open at the
        /// same time is allowed.</para>
        ///
        /// <para>Directory files may not be opened on most filesystems.  On those where it
        /// is allowed, the file must be opened read-only.</para>
        /// </remarks>
        /// <param name="entry">File to open.</param>
        /// <param name="mode">File mode (read-only or read/write).</param>
        /// <param name="part">Which fork of the file to open.</param>
        /// <returns>The file descriptor.</returns>
        /// <exception cref="IOException">One of the following:
        ///   <list type="bullet">
        ///     <item>File is already open, and cannot be opened again.</item>
        ///     <item>File is too damaged to be accessed.</item>
        ///     <item>The requested file part (fork) does not exist.</item>
        ///     <item>The filesystem is read-only, and read-write access requested.</item>
        ///     <item>Entry is a directory, but opening directories is not allowed, or
        ///       it is allowed but read-write access was requested.</item>
        ///   </list>
        /// </exception>
        /// <exception cref="FileNotFoundException">File entry is not part of this
        ///   filesystem.</exception>
        /// <exception cref="ArgumentException">Disk image part requested.</exception>
        DiskFileStream OpenFile(IFileEntry entry, FileAccessMode mode, Defs.FilePart part);

        /// <summary>
        /// Closes all open files.
        /// </summary>
        void CloseAll();

        /// <summary>
        /// Generates detailed human-readable information about the filesystem.
        /// </summary>
        /// <returns>Formatted text, or null if not implemented.</returns>
        string? DebugDump();
    }
}
