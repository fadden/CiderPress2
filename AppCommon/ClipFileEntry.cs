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
using DiskArc.Arc;
using DiskArc.FS;
using FileConv;
using static DiskArc.Defs;
using static DiskArc.IFileSystem;

namespace AppCommon {
    /// <summary>
    /// <para>File entry information for one fork of one file.  This holds serializable data
    /// that can be placed on the system clipboard.</para>
    /// <para>This includes file attributes and a reference to the file contents, but only the
    /// attributes can be serialized.  The file contents must be transmitted through some other
    /// means.</para>
    /// </summary>
    [Serializable]
    public sealed class ClipFileEntry {
        /// <summary>
        /// Object that "generates" a stream of data for one part of a file.  It's actually
        /// opening the archive or filesystem entry and just copying data out.
        /// </summary>
        /// <remarks>
        /// <para>This class is NOT serializable.  It only exists on the local side, and is
        /// invoked when the remote side requests file contents.</para>
        /// </remarks>
        public class StreamGenerator {
            private object mArchiveOrFileSystem;
            private IFileEntry mEntry;
            private IFileEntry mAdfEntry;
            private FilePart mPart;
            private FileAttribs mAttribs;
            private ExtractFileWorker.PreserveMode mPreserve;
            private ConvConfig.FileConvSpec? mExportSpec;
            Dictionary<string, ConvConfig.FileConvSpec>? mDefaultSpecs;
            private Type? mExpectedType;
            private AppHook mAppHook;

            /// <summary>
            /// Constructor.
            /// </summary>
            /// <param name="archiveOrFileSystem">IArchive or IFileSystem instance.</param>
            /// <param name="entry">Entry to access.</param>
            /// <param name="adfEntry">ADF header entry for MacZip archives.  Will be NO_ENTRY
            ///   if this is not a MacZip file.</param>
            /// <param name="part">File part.  This can also specify whether the fork should
            ///   be opened in "raw" mode.  This may be ignored for "export" mode.</param>
            /// <param name="attribs">File attributes.</param>
            /// <param name="preserveMode">Preservation mode used for source data.</param>
            /// <param name="expectedType">File converter type to use, for "export" mode.</param>
            /// <param name="appHook">Application hook reference.</param>
            public StreamGenerator(object archiveOrFileSystem, IFileEntry entry,
                    IFileEntry adfEntry, FilePart part, FileAttribs attribs,
                    ExtractFileWorker.PreserveMode preserveMode,
                    ConvConfig.FileConvSpec? exportSpec,
                    Dictionary<string, ConvConfig.FileConvSpec>? defaultSpecs,
                    Type? expectedType, AppHook appHook) {
                Debug.Assert(adfEntry == IFileEntry.NO_ENTRY || archiveOrFileSystem is Zip);

                mArchiveOrFileSystem = archiveOrFileSystem;
                mEntry = entry;
                mAdfEntry = adfEntry;
                mPart = part;
                mAttribs = attribs;
                mPreserve = preserveMode;
                mExportSpec = exportSpec;
                mDefaultSpecs = defaultSpecs;
                mExpectedType = expectedType;
                mAppHook = appHook;
            }

            /// <summary>
            /// Determines the length of the contents of this entry.  Generated streams, such as
            /// AppleSingle/ADF header files and export converter output, will return -1, as will
            /// compressed streams with indeterminate length (gzip, Squeeze).
            /// </summary>
            /// <remarks>
            /// Accuracy matters.  If the stated length is too short, the receiver may stop
            /// reading early.  Better to return -1 than report an inaccurate value.  The only
            /// value in providing this is so that the conflict resolution dialog can report
            /// the sizes of the "old" and "new" versions.
            /// </remarks>
            public long GetOutputLength() {
                if (mExportSpec != null) {
                    return -1;      // size of export conversions is not known
                }
                switch (mPreserve) {
                    case ExtractFileWorker.PreserveMode.Unknown:        // for direct xfer
                    case ExtractFileWorker.PreserveMode.None:
                    case ExtractFileWorker.PreserveMode.Host:
                    case ExtractFileWorker.PreserveMode.NAPS:
                        if (mPart == FilePart.RsrcFork) {
                            // Always use the RsrcLength from the FileAttribs because it has
                            // the actual resource fork length from MacZip entries.
                            return mAttribs.RsrcLength;
                        } else if (mPart == FilePart.DataFork) {
                            return mAttribs.DataLength;
                        } else if (mPart == FilePart.RawData) {
                            // We could test this for DOS_FileEntry and report its RawDataLength
                            // value, and just return DataLength for anything else.  However, that
                            // would be fragile if we ever use RawData for something else.
                            return -1;
                        } else {
                            // Disk image, don't bother.
                            return -1;
                        }
                    case ExtractFileWorker.PreserveMode.ADF:
                        if (mPart == FilePart.DataFork) {
                            return mAttribs.DataLength;
                        } else {
                            // Resource fork is generated.
                            return -1;
                        }
                    case ExtractFileWorker.PreserveMode.AS:
                    default:
                        return -1;
                }
            }

            /// <summary>
            /// Copies the file entry contents to a stream.
            /// </summary>
            /// <remarks>
            /// <para>The common use case is a Windows clipboard paste or drop operation.  In
            /// the current implementation, the output stream is not seekable.</para>
            /// </remarks>
            /// <param name="outStream">Output stream.  Must be writable, but does not need to
            ///   be readable or seekable.</param>
            /// <exception cref="IOException">Error while reading data.</exception>
            /// <exception cref="InvalidDataException">Corrupted data found.</exception>
            public void OutputToStream(Stream outStream) {
                Debug.Assert(outStream.CanWrite);

                if (mExportSpec != null) {
                    Type? exportType = ClipFileSet.DoClipExport(mArchiveOrFileSystem, mEntry,
                        mAdfEntry, mAttribs, mPart == FilePart.RawData, mExportSpec,
                        mDefaultSpecs, outStream, mAppHook);
                    if (exportType != mExpectedType) {
                        // This isn't terrible, but it means we probably have a file with the
                        // wrong file extension.  This should only be possible if the converter
                        // encountered something that caused it to change output types (e.g. to
                        // display an error).
                        Debug.Assert(false, "Export type mismatch: " + exportType +
                            " vs " + mExpectedType);
                    }
                } else {
                    switch (mPreserve) {
                        case ExtractFileWorker.PreserveMode.Unknown:    // direct xfer
                        case ExtractFileWorker.PreserveMode.None:
                        case ExtractFileWorker.PreserveMode.Host:
                        case ExtractFileWorker.PreserveMode.NAPS:
                            // Just copy the data from the appropriate fork to the output file.
                            using (Stream inStream = OpenPart()) {
                                inStream.CopyTo(outStream);
                            }
                            break;
                        case ExtractFileWorker.PreserveMode.ADF:
                            if (mPart != FilePart.RsrcFork) {
                                // The data fork is simply copied out.
                                using (Stream inStream = OpenPart()) {
                                    inStream.CopyTo(outStream);
                                }
                            } else {
                                // This is the "header" file, which holds the (optional) resource
                                // fork and type info.
                                GenerateADFHeader(outStream);
                            }
                            break;
                        case ExtractFileWorker.PreserveMode.AS:
                            // Generate an AppleSingle archive with both forks and the type info.
                            GenerateAS(outStream);
                            break;
                        default:
                            Debug.Assert(false);
                            return;
                    }

                    // Set the position back to zero.  This matters because the remote side
                    // shares the seek pointer, and might not remember to reset the position.
                    outStream.Position = 0;
                }
            }

            /// <summary>
            /// Opens one fork of a file entry, in a disk image or file archive.
            /// </summary>
            /// <returns>Opened stream.</returns>
            private Stream OpenPart(FilePart part = FilePart.Unknown) {
                if (part == FilePart.Unknown) {
                    part = mPart;       // "default" part
                }
                if (mArchiveOrFileSystem is IArchive) {
                    IArchive arc = (IArchive)mArchiveOrFileSystem;
                    if (part == FilePart.RsrcFork && mAdfEntry != IFileEntry.NO_ENTRY) {
                        // We need to open the resource fork in the ADF header entry.  This is
                        // used for MacZip.
                        Debug.Assert(!mEntry.HasRsrcFork);
                        return OpenMacZipRsrc();
                    } else {
                        return arc.OpenPart(mEntry, part);
                    }
                } else if (mArchiveOrFileSystem is IFileSystem) {
                    IFileSystem fs = (IFileSystem)mArchiveOrFileSystem;
                    return fs.OpenFile(mEntry, FileAccessMode.ReadOnly, part);
                } else {
                    throw new NotImplementedException("Unexpected: " + mArchiveOrFileSystem);
                }
            }

            /// <summary>
            /// Opens the resource fork in the ADF header file.
            /// </summary>
            /// <remarks>
            /// This is a little awkward because we can't dispose of the ADF header archive until
            /// we're done with the stream.  Rather than try to juggle the lifetimes we just copy
            /// the data out.  Resource forks are generally small (a few MB at most), so we
            /// should just be paying for an extra memory copy.
            /// </remarks>
            /// <returns>Resource fork stream.</returns>
            private Stream OpenMacZipRsrc() {
                IArchive arc = (IArchive)mArchiveOrFileSystem;

                // Copy to temp file; can't use unseekable stream as archive source.
                using Stream adfStream = ArcTemp.ExtractToTemp(arc, mAdfEntry, FilePart.DataFork);
                using AppleSingle adfArchive = AppleSingle.OpenArchive(adfStream, mAppHook);
                IFileEntry adfArchiveEntry = adfArchive.GetFirstEntry();
                using Stream rsrcStream = adfArchive.OpenPart(adfArchiveEntry, FilePart.RsrcFork);

                return ArcTemp.ExtractToTemp(adfArchive, adfArchiveEntry, FilePart.RsrcFork);
            }

            /// <summary>
            /// Generates an ADF "header" file, for files with a resource fork or that have
            /// nonzero file types.
            /// </summary>
            /// <param name="outStream">Output stream; need not be seekable.</param>
            private void GenerateADFHeader(Stream outStream) {
                bool hasRsrcFork = (mAdfEntry != IFileEntry.NO_ENTRY && mAttribs.RsrcLength > 0) ||
                    mEntry.HasRsrcFork && (mEntry is ProDOS_FileEntry || mEntry.RsrcLength > 0);

                Stream? rsrcStream = null;
                Stream? rsrcCopy = null;
                try {
                    using (Stream tmpOut = TempFile.CreateTempFile()) {
                        if (hasRsrcFork) {
                            // Make copy of stream, if necessary.
                            Debug.Assert(mPart == FilePart.RsrcFork);
                            rsrcStream = OpenPart();
                            if (rsrcStream.CanSeek) {
                                rsrcCopy = rsrcStream;
                            } else {
                                rsrcCopy = TempFile.CopyToTemp(rsrcStream);
                            }
                        }

                        using (AppleSingle adfArchive = AppleSingle.CreateDouble(2, mAppHook)) {
                            adfArchive.StartTransaction();
                            IFileEntry adfEntry = adfArchive.GetFirstEntry();
                            // Create sources for rsrc fork, if any.  We want to manage the
                            // stream lifetime, so we set the leaveOpen flag.
                            if (rsrcCopy != null) {
                                SimplePartSource rsrcSource = new SimplePartSource(rsrcCopy, true);
                                adfArchive.AddPart(adfEntry, FilePart.RsrcFork, rsrcSource,
                                    CompressionFormat.Default);
                            }
                            mAttribs.CopyAttrsTo(adfEntry, true);
                            adfArchive.CommitTransaction(tmpOut);
                        }

                        // Copy the archive stream to the output.
                        tmpOut.Position = 0;
                        tmpOut.CopyTo(outStream);
                    }
                } finally {
                    rsrcStream?.Dispose();
                    rsrcCopy?.Dispose();
                }
            }

            /// <summary>
            /// Generates an AppleSingle archive, which contains all file forks and file type
            /// information.
            /// </summary>
            /// <param name="outStream">Output stream; need not be seekable.</param>
            private void GenerateAS(Stream outStream) {
                bool hasRsrcFork = (mAdfEntry != IFileEntry.NO_ENTRY && mAttribs.RsrcLength > 0) ||
                    mEntry.HasRsrcFork && (mEntry is ProDOS_FileEntry || mEntry.RsrcLength > 0);

                Stream? dataStream = null;
                Stream? dataCopy = null;
                Stream? rsrcStream = null;
                Stream? rsrcCopy = null;
                try {
                    using (Stream tmpOut = TempFile.CreateTempFile()) {
                        if (mEntry.HasDataFork || mEntry.IsDiskImage) {
                            // Make copy of stream, if necessary.
                            // Open part specified for entry; should be data fork, could be
                            // raw data (or potentially even disk image).
                            Debug.Assert(mPart != FilePart.RsrcFork);
                            dataStream = OpenPart();
                            if (dataStream.CanSeek) {
                                dataCopy = dataStream;
                            } else {
                                dataCopy = TempFile.CopyToTemp(dataStream);
                            }
                        }
                        if (hasRsrcFork) {
                            // Make copy of stream, if necessary.
                            rsrcStream = OpenPart(FilePart.RsrcFork);
                            if (rsrcStream.CanSeek) {
                                rsrcCopy = rsrcStream;
                            } else {
                                rsrcCopy = TempFile.CopyToTemp(rsrcStream);
                            }
                        }

                        using (AppleSingle asArchive = AppleSingle.CreateArchive(2, mAppHook)) {
                            asArchive.StartTransaction();
                            IFileEntry asEntry = asArchive.GetFirstEntry();
                            // Create sources for data/rsrc forks.  We want to manage the
                            // stream lifetime, so we set the leaveOpen flag.
                            if (dataCopy != null) {
                                SimplePartSource dataSource = new SimplePartSource(dataCopy, true);
                                asArchive.AddPart(asEntry, FilePart.DataFork, dataSource,
                                    CompressionFormat.Default);
                            }
                            if (rsrcCopy != null) {
                                SimplePartSource rsrcSource = new SimplePartSource(rsrcCopy, true);
                                asArchive.AddPart(asEntry, FilePart.RsrcFork, rsrcSource,
                                    CompressionFormat.Default);
                            }
                            mAttribs.CopyAttrsTo(asEntry, true);
                            asArchive.CommitTransaction(tmpOut);
                        }

                        // Copy the archive stream to the output.
                        tmpOut.Position = 0;
                        tmpOut.CopyTo(outStream);
                    }
                } finally {
                    dataStream?.Dispose();
                    dataCopy?.Dispose();
                    rsrcStream?.Dispose();
                    rsrcCopy?.Dispose();
                }
            }
        }

        /// <summary>
        /// Stream generator.
        /// </summary>
        /// <remarks>
        /// The NonSerialized attribute can only be applied to fields, not properties.
        /// </remarks>
        [NonSerialized]
        public StreamGenerator? mStreamGen = null;

        /// <summary>
        /// Filesystem on which the file lives.  Will be Unknown for file archives.  Useful for
        /// certain special cases, such as transferring text files from DOS.
        /// </summary>
        public FileSystemType FSType { get; set; } = FileSystemType.Unknown;

        /// <summary>
        /// Which fork of the file this is.  Also indicates "raw" file access.
        /// </summary>
        public FilePart Part { get; set; } = FilePart.Unknown;

        /// <summary>
        /// File attributes.
        /// </summary>
        public FileAttribs Attribs { get; set; } = new FileAttribs();

        /// <summary>
        /// Platform-specific partial path.  This is the same as Attribs.FullPathName, but
        /// adjusted for the host filesystem and possibly escaped for NAPS.
        /// </summary>
        public string ExtractPath { get; set; } = string.Empty;

        /// <summary>
        /// Estimated length of this stream.  May be -1 if the length can't be determined easily.
        /// </summary>
        public long OutputLength { get; set; }

        /// <summary>
        /// Hash code for object on source side.  Used for detecting self-overwrite when
        /// copying & pasting within a single application instance.
        /// </summary>
        public int EntryHashCode { get; set; }

        // TODO? sparse map to preserve DOS file structure


        /// <summary>
        /// Nullary constructor, for the deserializer.
        /// </summary>
        public ClipFileEntry() { }

        /// <summary>
        /// Constructor for "foreign transfer" entries.  These require a host-compatible extract
        /// filename, and can take export conversion parameters.
        /// </summary>
        /// <param name="archiveOrFileSystem">IArchive or IFileSystem instance.</param>
        /// <param name="entry">File entry this represents.  This may be NO_ENTRY for
        ///   synthetic directory entries (which have no contents).</param>
        /// <param name="adfEntry">MacZip ADF header entry, or NO_ENTRY if this is not part of
        ///   a MacZip pair.</param>
        /// <param name="part">Which part of the file this is.  RawData and DiskImage are
        ///   possible.</param>
        /// <param name="attribs">File attributes.  May come from the file entry or from the
        ///   MacZip ADF header.</param>
        /// <param name="extractPath">Filename to use when extracting the file on the host
        ///   filesystem.</param>
        /// <param name="preserveMode">File attribute preservation mode used when generating
        ///   the data.</param>
        /// <param name="exportSpec">Export conversion specification (only for "export").</param>
        /// <param name="defaultSpecs">Default export specs for "best" mode (only for
        ///   "export").</param>
        /// <param name="expectedType">Expected output from export conversion (only for
        ///   "export").</param>
        /// <param name="appHook">Application hook reference.</param>
        public ClipFileEntry(object archiveOrFileSystem, IFileEntry entry, IFileEntry adfEntry,
                FilePart part, FileAttribs attribs, string extractPath,
                ExtractFileWorker.PreserveMode preserveMode,
                ConvConfig.FileConvSpec? exportSpec,
                Dictionary<string, ConvConfig.FileConvSpec>? defaultSpecs,
                Type? expectedType, AppHook appHook) {
            Debug.Assert(!string.IsNullOrEmpty(attribs.FileNameOnly));
            Debug.Assert(!string.IsNullOrEmpty(attribs.FullPathName));
            mStreamGen = new StreamGenerator(archiveOrFileSystem, entry, adfEntry, part, attribs,
                preserveMode, exportSpec, defaultSpecs, expectedType, appHook);

            IFileSystem? fs = entry.GetFileSystem();
            if (fs != null) {
                FSType = fs.GetFileSystemType();
            }
            Part = part;
            Attribs = attribs;
            ExtractPath = extractPath;
            OutputLength = mStreamGen.GetOutputLength();
            EntryHashCode = entry.GetHashCode();
        }

        /// <summary>
        /// Constructor for "direct transfer" entries.
        /// </summary>
        /// <param name="archiveOrFileSystem">IArchive or IFileSystem instance.</param>
        /// <param name="entry">File entry this represents.  This may be NO_ENTRY for
        ///   synthetic directory entries (which have no contents).</param>
        /// <param name="adfEntry">MacZip ADF header entry, or NO_ENTRY if this is not part of
        ///   a MacZip pair.</param>
        /// <param name="part">Which part of the file this is.  RawData and DiskImage are
        ///   possible.</param>
        /// <param name="attribs">File attributes.  May come from the file entry or from the
        ///   MacZip ADF header.</param>
        /// <param name="appHook">Application hook reference.</param>
        public ClipFileEntry(object archiveOrFileSystem, IFileEntry entry, IFileEntry adfEntry,
                FilePart part, FileAttribs attribs, AppHook appHook) :
            this(archiveOrFileSystem, entry, adfEntry, part, attribs, attribs.FullPathName,
                ExtractFileWorker.PreserveMode.Unknown, null, null, null, appHook) {
        }

        public override string ToString() {
            return "[ClipFileEntry " + ExtractPath + " - " + Part + "]";
        }
    }
}
