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
            private FilePart mPart;
            private FileAttribs mAttribs;
            private ExtractFileWorker.PreserveMode mPreserve;
            private Converter? mConv;
            private AppHook mAppHook;

            /// <summary>
            /// Constructor.
            /// </summary>
            /// <param name="archiveOrFileSystem">IArchive or IFileSystem instance.</param>
            /// <param name="entry">Entry to access.</param>
            /// <param name="part">File part.  This can also specify whether the fork should
            ///   be opened in "raw" mode.  This may be ignored for "export" mode.</param>
            /// <param name="attribs">File attributes.</param>
            /// <param name="preserveMode">Preservation mode used for source data.</param>
            /// <param name="conv">File converter to use, for "export" mode.</param>
            /// <param name="appHook">Application hook reference.</param>
            public StreamGenerator(object archiveOrFileSystem, IFileEntry entry, FilePart part,
                    FileAttribs attribs, ExtractFileWorker.PreserveMode preserveMode,
                    Converter? conv, AppHook appHook) {
                mArchiveOrFileSystem = archiveOrFileSystem;
                mEntry = entry;
                mPart = part;
                mAttribs = attribs;
                mPreserve = preserveMode;
                mConv = conv;
                mAppHook = appHook;
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

                if (mConv != null) {
                    // TODO: do export instead of extract
                    throw new NotImplementedException();
                } else {
                    switch (mPreserve) {
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
                                // This is the "header" file, which holds the (optiona) resource
                                // fork and type info.
                                GenerateADFHeader(outStream);
                            }
                            break;
                        case ExtractFileWorker.PreserveMode.AS:
                            // Generate an AppleSingle archive with both forks and the type info.
                            GenerateAS(outStream);
                            break;
                        case ExtractFileWorker.PreserveMode.Unknown:
                        default:
                            Debug.Assert(false);
                            return;
                    }
                }
            }

            /// <summary>
            /// Opens one fork of a file entry, in a disk image or file archive.
            /// </summary>
            /// <returns>Opened stream.</returns>
            private Stream OpenPart(FilePart part = FilePart.Unknown) {
                if (part == FilePart.Unknown) {
                    part = mPart;
                }
                if (mArchiveOrFileSystem is IArchive) {
                    IArchive arc = (IArchive)mArchiveOrFileSystem;
                    return arc.OpenPart(mEntry, part);
                } else if (mArchiveOrFileSystem is IFileSystem) {
                    IFileSystem fs = (IFileSystem)mArchiveOrFileSystem;
                    return fs.OpenFile(mEntry, FileAccessMode.ReadOnly, part);
                } else {
                    throw new NotImplementedException("Unexpected: " + mArchiveOrFileSystem);
                }
            }

            /// <summary>
            /// Generates an ADF "header" file, for files with a resource fork or that have
            /// nonzero file types.
            /// </summary>
            /// <param name="outStream">Output stream; need not be seekable.</param>
            private void GenerateADFHeader(Stream outStream) {
                bool hasRsrcFork = mEntry.HasRsrcFork &&
                    (mEntry is ProDOS_FileEntry || mEntry.RsrcLength > 0);

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
                bool hasRsrcFork = mEntry.HasRsrcFork &&
                    (mEntry is ProDOS_FileEntry || mEntry.RsrcLength > 0);

                Stream? dataStream = null;
                Stream? dataCopy = null;
                Stream? rsrcStream = null;
                Stream? rsrcCopy = null;
                try {
                    using (Stream tmpOut = TempFile.CreateTempFile()) {
                        if (mEntry.HasDataFork) {
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

        ///// <summary>
        ///// Partial path to use when dragging the file to a system-specific file area, such as
        ///// Windows Explorer.  The pathname must be compatible with the host system.
        ///// </summary>
        //public string OutputPath { get; set; } = string.Empty;

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
        /// Preservation mode.  The remote side needs this to tell it how to interpret the
        /// contents of the incoming file stream.
        /// </summary>
        public ExtractFileWorker.PreserveMode Preserve { get; set; } =
            ExtractFileWorker.PreserveMode.Unknown;

        // TODO? sparse map to preserve DOS file structure


        /// <summary>
        /// Nullary constructor, for the deserializer.
        /// </summary>
        public ClipFileEntry() { }

        /// <summary>
        /// Standard constructor.
        /// </summary>
        public ClipFileEntry(object archiveOrFileSystem, IFileEntry entry, FilePart part,
                FileAttribs attribs, string extractPath,
                ExtractFileWorker.PreserveMode preserveMode, Converter? conv, AppHook appHook) {
            mStreamGen = new StreamGenerator(archiveOrFileSystem, entry, part, attribs,
                preserveMode, conv, appHook);

            IFileSystem? fs = entry.GetFileSystem();
            if (fs != null) {
                FSType = fs.GetFileSystemType();
            }
            Part = part;
            Attribs = attribs;
            ExtractPath = extractPath;
            Preserve = preserveMode;
        }

        public override string ToString() {
            return "[ClipFileEntry " + ExtractPath + " - " + Part + "]";
        }
    }
}
