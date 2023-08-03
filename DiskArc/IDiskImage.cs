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

using static DiskArc.Defs;

namespace DiskArc {
    /// <summary>
    /// <para>Interface definition for classes representing a file that holds a single, usually
    /// uncompressed disk image.  The disk could be a 5.25" floppy or a 2GB hard drive.  The
    /// contents could be a single filesystem, a multi-partition format, or arbitrary data.</para>
    /// <para>Changes to the disk image are written directly to the stream, not to a temp file.
    /// This may happen immediately or be deferred until <see cref="Flush"/> is called.</para>
    /// </summary>
    /// <remarks>
    /// <para>There are four possible configurations when <see cref="AnalyzeDisk"/> returns:
    /// <list type="number">
    ///   <item>Sector format not recognized.  Only possible for nibble images.  The
    ///     <see cref="ChunkAccess"/> and <see cref="Contents"/> properties will be null.</item>
    ///   <item>Sector format was recognized, but filesystem was not.  Disk is using a custom
    ///     file format.  <see cref="ChunkAccess"/> will be available, but <see cref="Contents"/>
    ///     will be null.</item>
    ///   <item>Individual filesystem recognized.  <see cref="Contents"/> holds an IFileSystem
    ///     reference, and <see cref="ChunkAccess"/> is non-null but disabled.</item>
    ///   <item>Multi-partition format recognized.  <see cref="Contents"/> holds an IMultiPart
    ///     reference, and <see cref="ChunkAccess"/> is non-null but disabled.</item>
    /// </list></para>
    ///
    /// <para>IDisposable is required because some formats must modify their headers when
    /// changes have been made, e.g. WOZ and DiskCopy have checksums.  For the same reason, the
    /// filesystem object must be "owned" by this object, so that changes to it can be flushed
    /// before updating the disk image file.</para>
    /// <para>This class is risky for compressed data, because doing so would require truncating
    /// and then re-writing the original file in place.  There is also an expectation that
    /// flushing changes is relatively inexpensive for disk images.  Compressed single-disk formats
    /// like DDD might be best handled as single-file archives instead.</para>
    ///
    /// <para>All instances must also define a file identification routine:
    /// <code>  static bool TestKind(Stream stream, AppHook appHook)</code></para>
    ///
    /// <para>Implementations of IDiskImage should provide static "create image" methods that
    /// prepare new disk image streams, and "open image" methods that open existing streams.
    /// New nibble images should be formatted with an appropriate sector codec.</para>
    /// </remarks>
    public interface IDiskImage : IDisposable {
        /// <summary>
        /// True if this disk image is read-only.
        /// </summary>
        /// <remarks>
        /// <para>The image will be marked read-only if the stream is read-only, if updating
        /// this type of image is not supported, or if the image appears to be damaged.  This
        /// is unrelated to damage to the filesystem(s).</para>
        /// </remarks>
        bool IsReadOnly { get; }

        /// <summary>
        /// True if the disk image has un-saved updates.
        /// </summary>
        bool IsDirty { get; }

        /// <summary>
        /// <para>Modification flag, set to true whenever a write operation is performed.  The
        /// application should reset this to false when the modifications have been saved.
        /// Unlike the <see cref="IsDirty"/> flag, this does not reset automatically.</para>
        /// </summary>
        /// <remarks>
        /// <para>The state of this flag has no impact on the operation of IDiskImage instances.
        /// This exists purely for the benefit of applications, so that they can tell when a disk
        /// image has been modified (perhaps to show a "save needed" indicator, or know that a
        /// disk image must be recompressed into an archive).</para>
        /// <para>Pending changes must be flushed before the flag is cleared.</para>
        /// </remarks>
        bool IsModified { get; set; }
        // TODO: this isn't looking useful; remove it? (from here and IChunkAccess)

        /// <summary>
        /// True if the disk image file appears to be damaged.
        /// </summary>
        /// <remarks>
        /// <para>This indicates that the disk image itself is damaged, e.g. a checksum on the
        /// header or data didn't match.</para>
        /// </remarks>
        bool IsDubious { get; }

        /// <summary>
        /// Errors, warnings, and points of interest noted during the archive scan.
        /// </summary>
        /// <remarks>
        /// <para>There should be a direct correlation between the presence of error notes and
        /// the raising of the IsDubious flag.</para>
        /// </remarks>
        CommonUtil.Notes Notes { get; }

        /// <summary>
        /// <para>Chunk access object.  This is set by <see cref="AnalyzeDisk"/>.</para>
        /// <para>This is also set by "create disk" functions, to make it easy to format a new
        /// filesystem or partition layout on a new disk image.</para>
        /// </summary>
        /// <remarks>
        /// <para>If the filesystem has been detected and made available through the FileSystem
        /// property, chunk access will be read-only, regardless of the filesystem state.  Writes
        /// must be made through the <see cref="IFileSystem.RawAccess"/> property in the
        /// IFileSystem object instead.  This ensures that low-level editing isn't possible while
        /// the filesystem is open for file access.</para>
        /// <para>Changes made to the filesystem not be visible until <see cref="Flush"/> is
        /// called.</para>
        /// <para>Will be null if the disk layout hasn't been analyzed yet, or for nibble images
        /// for which the sector format cannot be determined.</para>
        /// </remarks>
        GatedChunkAccess? ChunkAccess { get; }

        /// <summary>
        /// <para>Object that represents the contents of the disk image, set by
        /// <see cref="AnalyzeDisk"/>.  This will refer to an IFileSystem object if a single
        /// filesystem was found, an IMultiPart if a multi-partition format was found, or null
        /// if the analysis failed or hasn't been performed.</para>
        /// </summary>
        IDiskContents? Contents { get; }

        /// <summary>
        /// Analysis depth argument for <see cref="AnalyzeDisk"/>.  Determines whether the
        /// analyzer stops after analyzing the sector format (leaving <see cref="ChunkAccess"/>
        /// open) or continues on to analyze the disk contents.
        /// </summary>
        enum AnalysisDepth {
            Unknown = 0, ChunksOnly, Full
        }

        /// <summary>
        /// Analyzes the disk format and filesystem layout.  Sets <see cref="ChunkAccess"/> to a
        /// non-null value if the disk layout can be determined, and sets <see cref="Contents"/>
        /// to a non-null value if a filesystem or multi-partition format was identified.
        /// </summary>
        /// <remarks>
        /// <para>If ChunkAccess or Contents are already set, the existing objects will be marked
        /// invalid and disposed before re-analysis begins.</para>
        /// </remarks>
        /// <param name="codec">Codec to use for the sector format.  If null, a set of standard
        ///   codecs will be tested.  Only meaningful for nibble images.</param>
        /// <param name="orderHint">Result from file analyzer.  This ordering will be tested
        ///   first if the file itself doesn't specify an ordering, and will be used as the
        ///   default if the ordering can't be verified.  May be Unknown.</param>
        /// <param name="depth">If ChunksOnly, analysis stops before attempting to recognize
        ///   the filesystem.  ChunkAccess will be valid and accessible, or null.</param>
        /// <returns>True on success.  If <paramref name="chunksOnly"/> is true, this returns
        ///   true if the chunk access object was created; otherwise it only returns true if
        ///   the filesystem or partition layout was recognized.</returns>
        bool AnalyzeDisk(SectorCodec? codec, SectorOrder orderHint, AnalysisDepth depth);

        /// <summary>
        /// Closes the IFileSystem or IMultiPart object referenced by <see cref="Contents"/>.
        /// The property will be set to null.
        /// </summary>
        /// <remarks>
        /// <para>This has no effect if Contents is already null.</para>
        /// </remarks>
        void CloseContents();

        /// <summary>
        /// Flushes any cached data to the disk image storage.
        /// </summary>
        /// <remarks>
        /// <para>This is performed automatically when the instance is disposed, unless the
        /// dispose is being performed by the garbage collector.</para>
        /// <para>For some disk image formats, this will cause the entire file to be
        /// written to disk.</para>
        /// </remarks>
        void Flush();
    }
}
