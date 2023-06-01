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
    /// Archive file, which can contain one or more files or disk images.  The contents may
    /// be compressed individually or collectively.  Some archives, such as gzip and
    /// AppleSingle, are limited to a single file.
    /// </summary>
    ///
    /// <remarks>
    /// <para>Some changes can be made directly to the archive, but any change that requires
    /// moving parts of the file around could leave the archive in an unusable state.  Some
    /// major changes, like deleting records off the end, might be doable with a simple
    /// truncation and record count decrement, while minor changes like renaming an entry might
    /// require shifting most of the file by one byte.  Minor edits can be made in place, but
    /// major edits require generating an entirely new archive and replacing the original when
    /// the operation is complete.</para>
    /// <para>If a new archive file needs to be created, the application is responsible for
    /// creating the new file and replacing the old file with it.</para>
    /// <para>Because file entries are always opened read-only, there is no limit on the number
    /// of times that an entry may be opened.  For safety, the archive object should keep track
    /// of open objects so that the data streams can be disposed if the archive stream is
    /// modified.</para>
    /// <para>The archive object does not own the archive stream that is passed to the constructor
    /// or to CommitTransaction().  The application is responsible for managing the streams.  An
    /// application that wishes to read entries from the archive while modifying it can create
    /// two archive objects on the same stream, and read data from one archive object while writing
    /// to the other.</para>
    /// <para>Iterate on the archive object to get a list of all entries.  To get a simple list,
    /// use archive.ToList() (requires "using System.Linq" to define an extension method).</para>
    /// </remarks>
    ///
    /// <para>All implementations must also define a file identification routine:
    /// <code>  static bool TestKind(Stream stream, AppHook appHook)</code></para>
    /// <para>And a function that converts a filename to an acceptable form:
    /// <code>  static string AdjustFileName(string fileName)</code></para>
    /// <para>Implementations of IArchive should also provide a static "open archive" method and
    /// one or more static "create archive" methods.</para>
    ///
    /// <para>Some things that could be supported but currently aren't:</para>
    /// <list type="bullet">
    ///   <item>Record sorting (via Swap(record1, record2)).</item>
    /// </list>
    public interface IArchive : IDisposable, IEnumerable<IFileEntry> {
        /// <summary>
        /// Characteristics of this class of file archive.  All instances of a given class
        /// return the same values.
        /// </summary>
        Arc.ArcCharacteristics Characteristics { get; }

        /// <summary>
        /// True if the archive structure is dubious.
        /// </summary>
        /// <remarks>
        /// The archive cannot be reconstructed without potentially losing data, so modifications
        /// are not allowed.
        /// </remarks>
        bool IsDubious { get; }

        /// <summary>
        /// Errors, warnings, and points of interest noted during the archive scan.
        /// </summary>
        /// <remarks>
        /// There should be a direct correlation between the presence of error notes and
        /// the raising of the IsDubious flag.
        /// </remarks>
        CommonUtil.Notes Notes { get; }

        /// <summary>
        /// True if pending changes will require a full reconstruction of the archive.
        /// </summary>
        /// <remarks>
        /// <para>This will always be true if the archive stream is not writable.</para>
        /// <para>This is intended as an optional optimization for minor operations, such as
        /// changing a file type or modification date, that can be performed in a single write
        /// operation.  Changes that alter the size of the archive or require writes in two
        /// places must not use this mechanism.  Implementations are allowed to ignore this
        /// and always require a rewrite.</para>
        /// </remarks>
        bool IsReconstructionNeeded { get; }

        /// <summary>
        /// Archive-level comment, or an empty string if there isn't one.
        /// </summary>
        /// <remarks>
        /// <para>Only supported for certain formats, such as ZIP.</para>
        /// </remarks>
        string Comment { get; set; }

        /// <summary>
        /// Number of entries in archive.
        /// </summary>
        int Count { get; }


        /// <summary>
        /// Provides a re-opened stream to the archive object.  Useful if the underlying file
        /// must be closed and reopened.
        /// </summary>
        /// <remarks>
        /// <para>When updating an archive, it's safest to write the archive to a new file, then
        /// on success delete the original archive and rename the new file in its place.  Some
        /// operating systems don't allow a file to be renamed while it's open.  Discarding the
        /// IArchive instance and re-opening it after the rename is inefficient and inconvenient.
        /// This allows the file to be closed and re-opened without discarding state.</para>
        /// <para>The re-opened stream must match the original exactly, or the behavior is
        /// undefined.  Implementations are not required to verify the contents of the new
        /// stream.  The old stream may already have been closed when this is called, so it's
        /// not possible to verify stream contents by reading the old stream.</para>
        /// </remarks>
        /// <param name="newStream">New stream.</param>
        /// <returns>Old data stream.  May be null for a newly-created archive (though this
        ///   shouldn't be getting called for that).</returns>
        Stream? ReopenStream(Stream newStream);

        /// <summary>
        /// Opens one part of a record in the archive for reading.
        /// </summary>
        /// <remarks>
        /// <para>Parts may not be opened while a transaction is in progress.</para>
        /// <para>When finished, Dispose the stream.</para>
        /// </remarks>
        /// <param name="entry">Entry to open.</param>
        /// <param name="part">Which part of the entry to open.</param>
        /// <returns>Read-only, non-seekable data stream.</returns>
        /// <exception cref="FileNotFoundException">Part does not exist.</exception>
        /// <exception cref="InvalidOperationException">A transaction is in progress.</exception>
        /// <exception cref="NotImplementedException">Compression format not supported.</exception>
        ArcReadStream OpenPart(IFileEntry entry, Defs.FilePart part);
        // TODO(maybe): use part=RawData to allow direct access to compressed data.

        /// <summary>
        /// Starts a transaction.  This must be called before any modifications are made to
        /// the archive or its entries.
        /// </summary>
        /// <remarks>
        /// <para>Only one transaction may be open at a time.</para>
        /// <para>Transactions may not be started if there are files open.  (The real problem
        /// is committing with files open, since that will invalidate all file entries, but
        /// I don't think it makes a difference in terms of what you can do.)</para>
        /// </remarks>
        /// <exception cref="InvalidOperationException">A transaction has already been
        ///   started, or there are open file parts.</exception>
        /// <exception cref="NotImplementedException">The implementation of this archive format
        ///   does not support modification.</exception>
        void StartTransaction();

        /// <summary>
        /// <para>Commits all pending changes.  If an output stream is provided, a complete copy
        /// of the archive will be written to it.  If an archive rewrite is required, an
        /// output stream must be provided.</para>
        /// <para>Upon successfully creating a new archive, the output stream argument becomes
        /// the archive stream.  The previous stream is dereferenced but not closed.</para>
        /// </summary>
        /// <remarks>
        /// <para>Failures will cause an exception to be thrown.  Exceptions do not abort the
        /// transaction, but will rewind and truncate the output stream.</para>
        /// <para>The output generation process may want to roll back and rewrite a section if
        /// compression failed to make a file smaller.  It may also be useful to write the
        /// archive header at the very end if it has full-file information, because conflicts
        /// could cause some operations to be skipped.</para>
        /// <para>This will generate a new archive even if no changes have been made.</para>
        /// <para>Most archives require records to have some amount of data.  The requirements
        /// vary by archive type.</para>
        /// </remarks>
        /// <param name="outputStream">Stream to output the rewritten archive to.  The stream
        ///   must be writable and seekable, and must not be the archive's stream.
        ///   If reconstruction is not needed, the caller can pass null to cause the modifications
        ///   to be made in place to the original file.</param>
        /// <exception cref="ArgumentException">Output stream was required but null, or was
        ///   provided but cannot be written or seeked.</exception>
        /// <exception cref="InvalidOperationException">Invalid state, e.g. a record is missing
        ///   a required filename or data part, or attempting to commit to the archive source
        ///   stream.</exception>
        /// <exception cref="InvalidDataException">Disk image data was not a multiple of
        ///   512 bytes.</exception>
        /// <exception cref="DAException">Archive-specific failure.</exception>
        void CommitTransaction(Stream? outputStream);

        /// <summary>
        /// Cancels all pending changes.
        /// </summary>
        /// <remarks>
        /// Does nothing if no transaction is in progress.
        /// </remarks>
        void CancelTransaction();

        /// <summary>
        /// Adds a new, empty record to the archive.
        /// </summary>
        /// <remarks>
        /// <para>In general, the record must be populated with a filename and parts before the
        /// transaction is committed, or the commit operation will fail.</para>
        /// </remarks>
        /// <returns>New file entry.</returns>
        /// <exception cref="InvalidOperationException">No transaction in progress.</exception>
        /// <exception cref="NotSupportedException">Cannot add a record to a single-record
        ///  archive.</exception>
        IFileEntry CreateRecord();

        /// <summary>
        /// Deletes an entire record.
        /// </summary>
        /// <remarks>
        /// <para>If the record was created earlier in the same commit, record creation will
        /// be cancelled, and any added parts deleted.</para>
        /// </remarks>
        /// <param name="entry">Record to delete.</param>
        /// <exception cref="InvalidOperationException">No transaction in progress.</exception>
        /// <exception cref="NotSupportedException">Cannot delete the record from a single-record
        ///  archive.</exception>
        void DeleteRecord(IFileEntry entry);

        /// <summary>
        /// Adds a part to a record.
        /// </summary>
        /// <remarks>
        /// <para>The file entry might be newly created, or might be an existing entry with other
        /// parts, e.g. when adding a resource fork to a record that only had a data fork before.
        /// To replace an existing part, delete the old part first.</para>
        /// <para>The IPartSource will not be accessed until the changes are committed.  The
        /// source stream does not need to be seekable or have a predetermined length.  The
        /// source will be disposed when the transaction is committed or cancelled.</para>
        /// <para>A compression format may be selected.  Not all combinations make sense, e.g.
        /// a file compressed with LZW/2 in a Binary II archive would not be usable.  If the
        /// compression algorithm fails to make the file smaller, it will be stored without
        /// compression.</para>
        ///
        /// <para>This feels like it should be part of IFileEntry, but IFileEntry objects are
        /// intended to be passive references to file entries, and are also used by disk
        /// images.</para>
        /// </remarks>
        /// <param name="entry">Entry to add a part to.</param>
        /// <param name="part">Which part to add.</param>
        /// <param name="partSource">Part data source.</param>
        /// <param name="compressFmt">How to compress the file.  Set to "Default" to allow the
        ///   archive to make the selection.  The part will be stored uncompressed if it
        ///   doesn't get smaller.</param>
        /// <exception cref="InvalidOperationException">No transaction in progress.</exception>
        /// <exception cref="ArgumentException">Part already exists, conflicts with an existing
        ///   part, or is not valid for this type of archive.</exception>
        /// <exception cref="NotSupportedException">This type of archive does not allow this
        ///   part.</exception>
        void AddPart(IFileEntry entry, Defs.FilePart part, IPartSource partSource,
            Defs.CompressionFormat compressFmt);

        /// <summary>
        /// Deletes part of a record.
        /// </summary>
        /// <remarks>
        /// <para>Removing all parts of a record does not delete the record.</para>
        /// <para>If the part was added by <see cref="AddPart"/> in this commit, the add will
        /// be cancelled, and the part source disposed.</para>
        /// </remarks>
        /// <param name="entry">Record to delete the part from.</param>
        /// <param name="part">Part to delete.</param>
        /// <exception cref="InvalidOperationException">No transaction in progress.</exception>
        /// <exception cref="FileNotFoundException">Part does not exist, because it hasn't been
        ///   added yet, or was deleted earlier.</exception>
        /// <exception cref="NotSupportedException">This type of archive does not allow this
        ///   part.</exception>
        void DeletePart(IFileEntry entry, Defs.FilePart part);
    }
}
