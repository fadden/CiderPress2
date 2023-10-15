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
using System.Text;

using CommonUtil;
using DiskArc;
using DiskArc.Arc;
using DiskArc.FS;
using FileConv;
using static DiskArc.Defs;
using static DiskArc.IFileSystem;

namespace AppCommon {
    /// <summary>
    /// File extraction manager.  This does most of the work.
    /// </summary>
    public class ExtractFileWorker {
        /// <summary>
        /// Preservation mode, used when extracting files.
        /// </summary>
        public enum PreserveMode {
            Unknown = 0, None, ADF, AS, Host, NAPS
        }

        /// <summary>
        /// Callback function interface definition.
        /// </summary>
        public delegate CallbackFacts.Results CallbackFunc(CallbackFacts what);

        /// <summary>
        /// If set, files added to a ZIP archive that have resource forks or HFS types will
        /// be stored as AppleDouble with a "__MACOSX" prefix.
        /// </summary>
        public bool IsMacZipEnabled { get; set; } = false;

        /// <summary>
        /// File preservation mode to use when extracting.
        /// </summary>
        public PreserveMode Preserve { get; set; }

        /// <summary>
        /// If set, use raw mode when adding files to filesystems (notably DOS 3.x).
        /// </summary>
        public bool RawMode { get; set; } = false;

        /// <summary>
        /// If set, strip pathnames off of files before adding them.  For a filesystem, all
        /// files will be added to the target directory.
        /// </summary>
        public bool StripPaths { get; set; } = false;

        private CallbackFunc mFunc;
        private AppHook mAppHook;


        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="func">Callback function, for progress messages and file overwrite
        ///   queries.</param>
        /// <param name="appHook">Application hook reference.</param>
        public ExtractFileWorker(CallbackFunc func, bool macZip, PreserveMode preserve,
                bool rawMode, bool stripPaths, AppHook appHook) {
            mFunc = func;
            IsMacZipEnabled = macZip;
            Preserve = preserve;
            RawMode = rawMode;
            StripPaths = stripPaths;
            mAppHook = appHook;
        }

        /// <summary>
        /// Extracts a list of files from a file archive to the current directory.
        /// </summary>
        public bool ExtractFromArchive(IArchive archive, List<IFileEntry> entries,
                ConvConfig.FileConvSpec? exportSpec, out bool wasCancelled) {
            wasCancelled = false;

            // Fix number of entries to extract, removing directories and MacZip headers.
            int entryCount = entries.Count;
            foreach (IFileEntry entry in entries) {
                if (entry.IsDirectory) {
                    entryCount--;
                } else if (archive is Zip && IsMacZipEnabled) {
                    if (entry.IsMacZipHeader()) {
                        entryCount--;
                    }
                }
            }
            if (entryCount == 0) {
                return true;        // must have been nothing but headers and directories
            }

            int doneCount = 0;
            foreach (IFileEntry entry in entries) {
                if (IsCancelPending()) {
                    wasCancelled = true;
                    return false;
                }
                if (entry.IsDirectory) {
                    continue;
                }
                string extractPath;
                if (Preserve == PreserveMode.NAPS) {
                    extractPath = PathName.AdjustEscapePathName(entry.FullPathName,
                        entry.DirectorySeparatorChar, PathName.DEFAULT_REPL_CHAR);
                } else {
                    extractPath = PathName.AdjustPathName(entry.FullPathName,
                        entry.DirectorySeparatorChar, PathName.DEFAULT_REPL_CHAR);
                }
                if (StripPaths) {
                    extractPath = Path.GetFileName(extractPath);
                }

                FileAttribs attrs = new FileAttribs(entry);

                // Handle MacZip archives, if enabled.
                IFileEntry adfEntry = IFileEntry.NO_ENTRY;
                if (archive is Zip && IsMacZipEnabled) {
                    // Only handle __MACOSX/ entries when paired with other entries.
                    if (entry.IsMacZipHeader()) {
                        continue;
                    }
                    // Check to see if we have a paired entry.
                    string macZipName = Zip.GenerateMacZipName(entry.FullPathName);
                    if (!string.IsNullOrEmpty(macZipName)) {
                        archive.TryFindFileEntry(macZipName, out adfEntry);
                    }
                }

                // When extracting from NuFX, extract the resource fork regardless of length,
                // to preserve the fact that the file was extended (it probably came from ProDOS).
                // For other sources, ignore zero-length resource forks.
                ArcReadStream? dataStream = null;
                ArcReadStream? rsrcStream = null;
                AppleSingle? adfArchive = null;
                Stream? adfStream = null;
                try {
                    if (entry.HasDataFork) {
                        dataStream = archive.OpenPart(entry, FilePart.DataFork);
                    }

                    if (adfEntry != IFileEntry.NO_ENTRY) {
                        // Handle paired MacZip entry.  Need to extract attributes and check for
                        // a resource fork.
                        try {
                            // Copy to temp file; can't use unseekable stream as archive source.
                            adfStream =
                                ArcTemp.ExtractToTemp(archive, adfEntry, FilePart.DataFork);
                            adfArchive = AppleSingle.OpenArchive(adfStream, mAppHook);
                            IFileEntry adfArchiveEntry = adfArchive.GetFirstEntry();
                            attrs.GetFromAppleSingle(adfArchiveEntry);
                            if (adfArchiveEntry.HasRsrcFork && adfArchiveEntry.RsrcLength > 0) {
                                rsrcStream =
                                    adfArchive.OpenPart(adfArchiveEntry, FilePart.RsrcFork);
                            }
                        } catch (Exception ex) {
                            // Never mind.
                            ReportFailure("Unable to get ADF attrs for '" +
                                entry.FullPathName + "': " + ex.Message);
                            // keep going
                            Debug.Assert(rsrcStream == null);
                        }
                    }

                    // If we didn't get a resource fork from ADF, see if the entry has one.
                    if (rsrcStream == null) {
                        if (entry.HasRsrcFork &&
                                (entry is NuFX_FileEntry || entry.RsrcLength > 0)) {
                            rsrcStream = archive.OpenPart(entry, FilePart.RsrcFork);
                        }
                    }

                    try {
                        int dataPerc = (100 * doneCount) / entryCount;
                        int rsrcPerc = (int)((100 * (doneCount + 0.5)) / entryCount);
                        bool doContinue;
                        if (exportSpec == null) {
                            doContinue = DoExtract(extractPath, attrs, dataStream, rsrcStream,
                                dataPerc, rsrcPerc);
                        } else {
                            doContinue = DoExport(extractPath, attrs, dataStream, rsrcStream,
                                dataPerc, exportSpec);
                        }
                        if (!doContinue) {
                            // Cancel requested.
                            wasCancelled = true;
                            return false;
                        }
                    } catch (IOException ex) {
                        ReportFailure("Error during extraction of '" +
                            entry.FullPathName + "': " + ex.Message);
                        return false;
                    }
                } finally {
                    dataStream?.Dispose();
                    rsrcStream?.Dispose();
                    adfArchive?.Dispose();
                    adfStream?.Dispose();
                }

                doneCount++;
            }
            return true;
        }

        /// <summary>
        /// Extracts a list of files from a filesystem to the current directory.
        /// </summary>
        public bool ExtractFromDisk(IFileSystem fs, List<IFileEntry> entries,
                IFileEntry startDirEntry, ConvConfig.FileConvSpec? exportSpec,
                out bool wasCancelled) {
            wasCancelled = false;

            if (entries.Count == 0) {
                return true;
            }

            // Get entry count, excluding directories.
            int entryCount = entries.Count;
            foreach (IFileEntry entry in entries) {
                if (entry.IsDirectory) {
                    entryCount--;
                }
            }
            int doneCount = 0;

            foreach (IFileEntry entry in entries) {
                if (IsCancelPending()) {
                    wasCancelled = true;
                    return false;
                }
                if (StripPaths && entry.IsDirectory) {
                    continue;
                }
                IFileEntry aboveRootEntry;
                if (startDirEntry == IFileEntry.NO_ENTRY) {
                    aboveRootEntry = IFileEntry.NO_ENTRY;       // same as start==volDir
                } else {
                    aboveRootEntry = startDirEntry.ContainingDir;
                }
                string extractPath;
                if (Preserve == PreserveMode.NAPS && !entry.IsDirectory) {
                    extractPath = GetAdjEscPathName(entry, aboveRootEntry,
                        Path.DirectorySeparatorChar);
                } else {
                    extractPath = GetAdjPathName(entry, aboveRootEntry,
                        Path.DirectorySeparatorChar);
                }
                if (StripPaths) {
                    extractPath = Path.GetFileName(extractPath);
                }

                FileAttribs attrs = new FileAttribs(entry);

                if (entry.IsDirectory) {
                    // We don't do a progress update here because directory creation should
                    // be near-instantaneous.  We don't include them in "entryCount", so the
                    // progress bar won't show a hiccup.
                    bool ok = CreateDirectory(extractPath, attrs, out wasCancelled);
                    if (!ok || wasCancelled) {
                        return false;
                    }
                    continue;
                }

                // When extracting from ProDOS, do extract zero-length resource forks, because
                // we want to preserve the fact that the file was extended.  For HFS, all files
                // have resource forks, so skip the ones with zero length.
                DiskFileStream? dataStream = null;
                DiskFileStream? rsrcStream = null;
                try {
                    if (entry.HasDataFork) {
                        // Handle "raw mode" for DOS 3.x.
                        FilePart part = FilePart.DataFork;
                        if (RawMode) {
                            part = FilePart.RawData;
                        }
                        dataStream = fs.OpenFile(entry, FileAccessMode.ReadOnly, part);
                    }

                    if (entry.HasRsrcFork &&
                            (entry is ProDOS_FileEntry || entry.RsrcLength > 0)) {
                        rsrcStream = fs.OpenFile(entry, FileAccessMode.ReadOnly, FilePart.RsrcFork);
                    }

                    try {
                        int dataPerc = (100 * doneCount) / entryCount;
                        int rsrcPerc = (int)((100 * (doneCount + 0.5)) / entryCount);
                        bool ok;
                        if (exportSpec == null) {
                            ok = DoExtract(extractPath, attrs, dataStream, rsrcStream,
                                dataPerc, rsrcPerc);
                        } else {
                            ok = DoExport(extractPath, attrs, dataStream, rsrcStream,
                                dataPerc, exportSpec);
                        }
                        if (!ok) {
                            // Cancel requested.
                            wasCancelled = true;
                            return false;
                        }
                    } catch (IOException ex) {
                        ReportFailure("Error during extraction of '" + entry.FullPathName +
                            "': " + ex.Message);
                        return false;
                    }
                } finally {
                    dataStream?.Dispose();
                    rsrcStream?.Dispose();
                }

                doneCount++;
            }
            return true;
        }

        /// <summary>
        /// Extracts data and/or resource fork streams to the host filesystem, using the
        /// requested attribute preservation mode.
        /// </summary>
        /// <param name="extractPath">Host path to extract the file to.</param>
        /// <param name="attrs">Attributes.</param>
        /// <param name="dataStream">Data fork stream, doesn't have to be seekable.</param>
        /// <param name="rsrcStream">Resource fork stream, doesn't have to be seekable.</param>
        /// <param name="dataPercent">Percent completion of operation for data fork (0-99).</param>
        /// <param name="rsrcPercent">Percent completion of operation for rsrc fork (0-99).</param>
        /// <returns>True if we should continue, false if we should cancel.</returns>
        /// <exception cref="IOException">Error creating or writing to file.</exception>
        private bool DoExtract(string extractPath, FileAttribs attrs, Stream? dataStream,
                Stream? rsrcStream, int dataPercent, int rsrcPercent) {
            // Cleanup list, in case we fail partway through.
            string? cleanPath1 = null;
            string? cleanPath2 = null;
            Stream? dataCopy = null;
            Stream? rsrcCopy = null;

            bool doSetAccess = true;
            bool doCancel;

            try {
                switch (Preserve) {
                    case PreserveMode.None:
                        // Extract data fork to named file.  Ignore rsrc fork.
                        // Set dates and access.
                        if (dataStream != null) {
                            if (!PrepareOutputFile(extractPath, out doCancel)) {
                                return !doCancel;
                            }
                            cleanPath1 = extractPath;
                            ShowProgress(attrs, extractPath, false, dataPercent);
                            using (Stream fileStream = new FileStream(cleanPath1,
                                    FileMode.CreateNew)) {
                                dataStream.CopyTo(fileStream);
                            }
                        }
                        if (rsrcStream != null) {
                            CallbackFacts rfacts = new CallbackFacts(
                                CallbackFacts.Reasons.ResourceForkIgnored, extractPath,
                                Path.DirectorySeparatorChar);
                            rfacts.Part = FilePart.RsrcFork;
                            mFunc(rfacts);
                        }
                        // We might not have done anything, if the record only had a resource fork.
                        break;

                    case PreserveMode.ADF:
                        // Create named file, extract data fork if it exists.
                        // If has resource fork or file type attributes, create ADF header file.
                        // Set dates and access on BOTH files.
                        if (!PrepareOutputFile(extractPath, out doCancel)) {
                            return !doCancel;
                        }
                        cleanPath1 = extractPath;
                        if (dataStream != null) {
                            ShowProgress(attrs, extractPath, false, dataPercent);
                            using (Stream fileStream = new FileStream(cleanPath1,
                                    FileMode.CreateNew)) {
                                dataStream.CopyTo(fileStream);
                            }
                        }
                        if (rsrcStream != null || attrs.HasTypeInfo) {
                            // Form ADF header file name.
                            string adfPath = Path.Combine(Path.GetDirectoryName(extractPath)!,
                                AppleSingle.ADF_PREFIX + Path.GetFileName(extractPath));
                            if (!PrepareOutputFile(adfPath, out doCancel)) {
                                return !doCancel;
                            }
                            cleanPath2 = adfPath;
                            using (Stream outFile = new FileStream(adfPath, FileMode.CreateNew)) {
                                // Make copy of stream, if necessary.
                                if (rsrcStream != null) {
                                    ShowProgress(attrs, adfPath, true, rsrcPercent);
                                    if (rsrcStream.CanSeek) {
                                        rsrcCopy = rsrcStream;
                                    } else {
                                        rsrcCopy = TempFile.CopyToTemp(rsrcStream);
                                    }
                                }
                                using (AppleSingle adfArchive =
                                        AppleSingle.CreateDouble(2, mAppHook)) {
                                    adfArchive.StartTransaction();
                                    IFileEntry adfEntry = adfArchive.GetFirstEntry();
                                    // Create sources for rsrc fork, if any.  We want to manage the
                                    // stream lifetime, so we set the leaveOpen flag.
                                    if (rsrcCopy != null) {
                                        SimplePartSource rsrcSource =
                                            new SimplePartSource(rsrcCopy, true);
                                        adfArchive.AddPart(adfEntry, FilePart.RsrcFork, rsrcSource,
                                            CompressionFormat.Default);
                                    }
                                    attrs.CopyAttrsTo(adfEntry, true);
                                    adfArchive.CommitTransaction(outFile);
                                }
                            }
                        }
                        break;

                    case PreserveMode.AS:
                        // Create AppleSingle file, add data fork and resource fork, set attrs.
                        // May need to copy input stream to a seekable stream.
                        // Set dates on file.  Don't set access, it's an archive.
                        doSetAccess = false;
                        string asPath = extractPath + AppleSingle.AS_EXT;
                        if (!PrepareOutputFile(asPath, out doCancel)) {
                            return !doCancel;
                        }
                        cleanPath1 = asPath;
                        using (Stream outFile = new FileStream(asPath, FileMode.CreateNew)) {
                            // Make copies of streams, if necessary.
                            if (dataStream != null) {
                                ShowProgress(attrs, asPath, false, dataPercent);
                                if (dataStream.CanSeek) {
                                    dataCopy = dataStream;
                                } else {
                                    dataCopy = TempFile.CopyToTemp(dataStream);
                                }
                            }
                            if (rsrcStream != null) {
                                ShowProgress(attrs, asPath, true, rsrcPercent);
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
                                    SimplePartSource dataSource =
                                        new SimplePartSource(dataCopy, true);
                                    asArchive.AddPart(asEntry, FilePart.DataFork, dataSource,
                                        CompressionFormat.Default);
                                }
                                if (rsrcCopy != null) {
                                    SimplePartSource rsrcSource =
                                        new SimplePartSource(rsrcCopy, true);
                                    asArchive.AddPart(asEntry, FilePart.RsrcFork, rsrcSource,
                                        CompressionFormat.Default);
                                }
                                attrs.CopyAttrsTo(asEntry, true);
                                asArchive.CommitTransaction(outFile);
                            }
                        }
                        break;

                    case PreserveMode.Host:
                        // Create named file, extract data fork if it exists.  Extract resource
                        // to {name}/..namedfork/rsrc.
                        // Set dates and access.
                        if (!PrepareOutputFile(extractPath, out doCancel)) {
                            return !doCancel;
                        }
                        cleanPath1 = extractPath;
                        if (dataStream != null) {
                            ShowProgress(attrs, extractPath, false, dataPercent);
                            using (Stream fileStream = new FileStream(cleanPath1,
                                    FileMode.CreateNew)) {
                                dataStream.CopyTo(fileStream);
                            }
                        }
                        if (rsrcStream != null) {
                            // Form resource fork pathname.  The file was newly-created, so we
                            // know it exists and doesn't already have a resource fork.
                            string rsrcPath = Path.Combine(extractPath, "..namedfork");
                            rsrcPath = Path.Combine(rsrcPath, "rsrc");
                            ShowProgress(attrs, rsrcPath, true, rsrcPercent);
                            using (Stream fileStream = new FileStream(rsrcPath,
                                    FileMode.CreateNew)) {
                                rsrcStream.CopyTo(fileStream);
                            }
                        }
                        Debug.Assert(cleanPath1 != null && cleanPath2 == null);
                        break;

                    case PreserveMode.NAPS:
                        // Extract data to file with extended name, extract rsrc to file +"r".
                        // Set dates and access on BOTH files.
                        string napsExt;
                        if (attrs.FileType == 0 && attrs.AuxType == 0 &&
                                (attrs.HFSFileType != 0 || attrs.HFSCreator != 0)) {
                            // Encode HFS types.
                            napsExt = string.Format("#{0:x8}{1:x8}",
                                attrs.HFSFileType, attrs.HFSCreator);
                        } else {
                            // Encode ProDOS types.
                            napsExt = string.Format("#{0:x2}{1:x4}",
                                attrs.FileType, attrs.AuxType);
                        }
                        if (dataStream != null) {
                            string napsPath = extractPath + napsExt;
                            if (!PrepareOutputFile(napsPath, out doCancel)) {
                                return !doCancel;
                            }
                            cleanPath1 = napsPath;
                            ShowProgress(attrs, napsPath, false, dataPercent);
                            using (Stream fileStream = new FileStream(napsPath,
                                    FileMode.CreateNew)) {
                                dataStream.CopyTo(fileStream);
                            }
                        }
                        if (rsrcStream != null) {
                            string napsPath = extractPath + napsExt + "r";
                            if (!PrepareOutputFile(napsPath, out doCancel)) {
                                return !doCancel;
                            }
                            cleanPath2 = napsPath;
                            ShowProgress(attrs, napsPath, true, rsrcPercent);
                            using (Stream fileStream = new FileStream(napsPath,
                                    FileMode.CreateNew)) {
                                rsrcStream.CopyTo(fileStream);
                            }
                        }
                        break;
                    default:
                        throw new NotImplementedException("mode not implemented: " + Preserve);
                }

                bool attrsGood = true;
                if (cleanPath1 != null) {
                    attrsGood &= SetFileAttributes(cleanPath1, attrs, doSetAccess);
                }
                if (cleanPath2 != null) {
                    attrsGood &= SetFileAttributes(cleanPath2, attrs, doSetAccess);
                }
                if (!attrsGood) {
                    CallbackFacts rfacts = new CallbackFacts(CallbackFacts.Reasons.AttrFailure,
                        extractPath, Path.DirectorySeparatorChar);
                    mFunc(rfacts);
                }
            } catch {
                // Try to clean up partial work, then re-throw exception.
                if (cleanPath1 != null) {
                    if (File.Exists(cleanPath1)) {
                        File.Delete(cleanPath1);
                    }
                }
                if (cleanPath2 != null) {
                    if (File.Exists(cleanPath2)) {
                        File.Delete(cleanPath2);
                    }
                }
                throw;
            } finally {
                dataCopy?.Dispose();
                rsrcCopy?.Dispose();
            }
            return true;
        }

        /// <summary>
        /// Exports file contents, transforming them with the specified converter.
        /// </summary>
        /// <param name="extractPath">Host path to extract the file to.</param>
        /// <param name="attrs">Attributes.</param>
        /// <param name="dataStream">Data fork stream, doesn't have to be seekable.</param>
        /// <param name="rsrcStream">Resource fork stream, doesn't have to be seekable.</param>
        /// <param name="dataPercent">Percent completion of operation (0-99).</param>
        /// <param name="exportSpec">Converter tag and options.</param>
        /// <returns>True if we should continue, false if we should cancel.</returns>
        /// <exception cref="IOException">Error creating or writing to file.</exception>
        private bool DoExport(string extractPath, FileAttribs attrs, Stream? dataStream,
                Stream? rsrcStream, int dataPercent, ConvConfig.FileConvSpec exportSpec) {
            // Cleanup list, in case we fail partway through.
            string? cleanPath1 = null;
            Stream? dataCopy = null;
            Stream? rsrcCopy = null;

            try {
                if (dataStream != null && !dataStream.CanSeek) {
                    dataCopy = TempFile.CopyToTemp(dataStream, attrs.DataLength);
                } else {
                    dataCopy = dataStream;
                }
                if (rsrcStream != null && !rsrcStream.CanSeek) {
                    rsrcCopy = TempFile.CopyToTemp(rsrcStream, attrs.RsrcLength);
                } else {
                    rsrcCopy = rsrcStream;
                }

                // Find the converter.
                Converter? conv;
                if (exportSpec.Tag == ConvConfig.BEST) {
                    List<Converter> applics = ExportFoundry.GetApplicableConverters(attrs,
                        dataCopy, rsrcCopy, mAppHook);
                    conv = applics[0];
                    // Should we clear the options from exportSpec?
                } else {
                    conv = ExportFoundry.GetConverter(exportSpec.Tag, attrs, dataCopy, rsrcCopy,
                        mAppHook);
                    if (conv == null) {
                        ReportConvFailure("no converter found for tag '" + exportSpec.Tag + "'");
                        return false;
                    }
                    if (conv.Applic <= Converter.Applicability.Not) {
                        ReportConvFailure("converter is not suitable for '" +
                            attrs.FileNameOnly + "'");
                        return false;
                    }
                }

                // Do the conversion.  This should rarely have cause to fail.
                IConvOutput convOutput = conv.ConvertFile(exportSpec.Options);
                // TODO: show converter notes if --show-notes is set
                if (convOutput is ErrorText) {
                    ReportConvFailure("conversion failed: " +
                        ((ErrorText)convOutput).Text.ToString());
                    return false;
                } else if (convOutput is FancyText && !((FancyText)convOutput).PreferSimple) {
                    string rtfPath = extractPath + ".rtf";
                    if (!PrepareOutputFile(rtfPath, out bool doCancel)) {
                        return !doCancel;
                    }
                    cleanPath1 = rtfPath;
                    ShowProgress(attrs, cleanPath1, false, dataPercent, conv.Tag);
                    using (Stream outStream = new FileStream(cleanPath1, FileMode.CreateNew)) {
                        RTFGenerator.Generate((FancyText)convOutput, outStream);
                    }
                } else if (convOutput is SimpleText) {
                    string txtPath = extractPath + ".txt";
                    if (!PrepareOutputFile(txtPath, out bool doCancel)) {
                        return !doCancel;
                    }
                    cleanPath1 = txtPath;
                    ShowProgress(attrs, cleanPath1, false, dataPercent, conv.Tag);
                    using (Stream outStream = new FileStream(cleanPath1, FileMode.CreateNew)) {
                        TXTGenerator.Generate((SimpleText)convOutput, outStream);
                    }
                } else if (convOutput is CellGrid) {
                    string csvPath = extractPath + ".csv";
                    if (!PrepareOutputFile(csvPath, out bool doCancel)) {
                        return !doCancel;
                    }
                    cleanPath1 = csvPath;
                    ShowProgress(attrs, cleanPath1, false, dataPercent, conv.Tag);
                    using (Stream outStream = new FileStream(cleanPath1, FileMode.CreateNew)) {
                        CSVGenerator.Generate((CellGrid)convOutput, outStream);
                    }
                } else if (convOutput is IBitmap) {
                    string pngPath = extractPath + ".png";
                    if (!PrepareOutputFile(pngPath, out bool doCancel)) {
                        return !doCancel;
                    }
                    cleanPath1 = pngPath;
                    ShowProgress(attrs, cleanPath1, false, dataPercent, conv.Tag);
                    using (Stream outStream = new FileStream(cleanPath1, FileMode.CreateNew)) {
                        PNGGenerator.Generate((IBitmap)convOutput, outStream);
                    }
                } else if (convOutput is HostConv) {
                    ReportConvFailure("GIF/JPEG/PNG should be extracted, not exported");
                    return false;
                } else {
                    Debug.Assert(false, "unknown IConvOutput impl " + convOutput);
                    ReportConvFailure("got weird output object: " + convOutput);
                    return false;
                }
            } catch {
                // Try to clean up partial work, then re-throw exception.
                if (cleanPath1 != null) {
                    if (File.Exists(cleanPath1)) {
                        File.Delete(cleanPath1);
                    }
                }
                throw;
            } finally {
                dataCopy?.Dispose();
                rsrcCopy?.Dispose();
            }
            return true;
        }

        private void ShowProgress(FileAttribs attrs, string extractPath, bool isRsrc,
                int percent, string convTag = "") {
            CallbackFacts facts = new CallbackFacts(CallbackFacts.Reasons.Progress,
                attrs.FullPathName, attrs.FullPathSep);
            facts.NewPathName = extractPath;
            facts.NewDirSep = Path.DirectorySeparatorChar;
            facts.ProgressPercent = percent;
            facts.Part = isRsrc ? FilePart.RsrcFork : FilePart.DataFork;
            facts.ConvTag = convTag;
            mFunc(facts);
        }

        private void ReportFailure(string msg) {
            CallbackFacts facts = new CallbackFacts(CallbackFacts.Reasons.Failure);
            facts.FailMessage = msg;
            mFunc(facts);
        }

        private void ReportConvFailure(string msg) {
            CallbackFacts facts = new CallbackFacts(CallbackFacts.Reasons.ConversionFailure);
            facts.FailMessage = msg;
            mFunc(facts);
        }

        private bool IsCancelPending() {
            CallbackFacts facts = new CallbackFacts(CallbackFacts.Reasons.QueryCancel);
            return mFunc(facts) == CallbackFacts.Results.Cancel;
        }

        /// <summary>
        /// Clears an existing file when extracting, or throws an exception.
        /// </summary>
        /// <param name="pathName">Pathname to extract to.</param>
        /// <param name="doCancel">True if the user has asked to cancel the operation, or
        ///   a severe problem was detected.</param>
        /// <returns>True if the way is clear.</returns>
        /// <exception cref="IOException">File operation failed.</exception>
        private bool PrepareOutputFile(string pathName, out bool doCancel) {
            doCancel = false;

            if (Directory.Exists(pathName)) {
                CallbackFacts dfacts = new CallbackFacts(CallbackFacts.Reasons.OverwriteFailure,
                    pathName, Path.DirectorySeparatorChar);
                dfacts.FailMessage = "existing directory with same name as file";
                mFunc(dfacts);
                return false;
            }
            FileInfo info = new FileInfo(pathName);
            if (!info.Exists) {
                // No such file or directory, all good.
                return CreateDirectories(pathName, out doCancel);
            }

            CallbackFacts facts = new CallbackFacts(CallbackFacts.Reasons.FileNameExists,
                pathName, Path.DirectorySeparatorChar);
            CallbackFacts.Results result = mFunc(facts);
            switch (result) {
                case CallbackFacts.Results.Cancel:
                    doCancel = true;
                    return false;
                case CallbackFacts.Results.Skip:
                    return false;
                case CallbackFacts.Results.Overwrite:
                    break;
                default:
                    Debug.Assert(false);
                    break;
            }

            // Mark the file read-write and delete it.
            if ((info.Attributes & FileAttributes.ReadOnly) != 0) {
                info.Attributes &= ~FileAttributes.ReadOnly;
            }
            File.Delete(pathName);
            return true;
        }

        /// <summary>
        /// Recursively creates directories referenced by the pathname.
        /// </summary>
        /// <param name="pathName">Pathname, including filename.</param>
        /// <param name="doCancel">Result: true if the operation was cancelled.</param>
        /// <returns>True on success, false if directory creation failed.</returns>
        private bool CreateDirectories(string pathName, out bool doCancel) {
            doCancel = false;
            string directoryName = Path.GetDirectoryName(pathName)!;
            if (string.IsNullOrEmpty(directoryName)) {
                return true;
            }
            if (Directory.Exists(directoryName)) {
                return true;
            }
            if (File.Exists(directoryName)) {
                // Don't overwrite file to replace with directory.  This could affect multiple
                // entries, so cancel the operation.
                CallbackFacts dfacts = new CallbackFacts(CallbackFacts.Reasons.OverwriteFailure,
                    directoryName, Path.DirectorySeparatorChar);
                dfacts.FailMessage = "existing file with same name as directory";
                mFunc(dfacts);
                doCancel = true;
                return false;
            }
            // Make sure the previous directories exist.
            if (!CreateDirectories(directoryName, out doCancel)) {
                return false;
            }
            // Create this one.
            Directory.CreateDirectory(directoryName);

            return true;
        }

        /// <summary>
        /// Creates a single directory.  Sets the directory file's attributes to match.
        /// </summary>
        /// <param name="pathName">Pathname of directory to create.</param>
        /// <param name="attrs">File attributes.</param>
        /// <param name="doCancel">Result: true if the operation was cancelled.</param>
        /// <returns>True on success.</returns>
        private bool CreateDirectory(string pathName, FileAttribs attrs, out bool doCancel) {
            string xname = Path.Combine(pathName, "X");
            if (!CreateDirectories(xname, out doCancel)) {
                return false;
            }
            return SetDirectoryAttributes(pathName, attrs);
        }

        /// <summary>
        /// Sets file attributes, such as the modification date and read-only flag.  In "host"
        /// preservation mode this also attempts to set the file types.
        /// </summary>
        /// <param name="pathName">Full or partial pathname of file.</param>
        /// <param name="attrs">File attributes.</param>
        /// <param name="doSetAccess">If true, set the read-only flag if file is locked.</param>
        private bool SetFileAttributes(string pathName, FileAttribs attrs, bool doSetAccess) {
            FileInfo info = new FileInfo(pathName);
            if (TimeStamp.IsValidDate(attrs.CreateWhen)) {
                info.CreationTime = attrs.CreateWhen;
            }
            if (TimeStamp.IsValidDate(attrs.ModWhen)) {
                info.LastWriteTime = attrs.ModWhen;
            }
            if (doSetAccess && (attrs.Access & (byte)AccessFlags.Write) == 0) {
                info.Attributes |= FileAttributes.ReadOnly;
            }

            if (Preserve == PreserveMode.Host) {
                byte[] buf = new byte[SystemXAttr.FINDERINFO_LENGTH];
                RawData.SetU32BE(buf, 0, attrs.HFSFileType);
                RawData.SetU32BE(buf, 4, attrs.HFSCreator);
                if (SystemXAttr.SetXAttr(pathName, SystemXAttr.XATTR_FINDERINFO_NAME, buf, 0) < 0) {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Sets directory file attributes, notably the creation and modification dates.
        /// </summary>
        /// <remarks>
        /// The modification date likely won't stick, because it gets changed as soon as we
        /// extract files into the directory.  I don't think there's value in trying to make
        /// that work correctly.
        /// </remarks>
        private bool SetDirectoryAttributes(string pathName, FileAttribs attrs) {
            DirectoryInfo info = new DirectoryInfo(pathName);
            if (TimeStamp.IsValidDate(attrs.CreateWhen)) {
                info.CreationTime = attrs.CreateWhen;
            }
            if (TimeStamp.IsValidDate(attrs.ModWhen)) {
                info.LastWriteTime = attrs.ModWhen;
            }
            return true;
        }

        /// <summary>
        /// Obtains the pathname of an arbitrary IFileEntry in a filesystem.  This can be the
        /// full path or a partial path, depending on the value of
        /// <paramref name="aboveRootEntry"/>.  The path components will be adjusted for
        /// compatibility with the host filesystem.
        /// </summary>
        /// <param name="entry">File entry object.</param>
        /// <param name="aboveRootEntry">Node just above the root of the tree.  If the root is
        ///   the volume directory, this should be NO_ENTRY.</param>
        /// <param name="dirSep">Path separator character to place between entries.</param>
        public static string GetAdjPathName(IFileEntry entry, IFileEntry aboveRootEntry,
                char pathSep) {
            StringBuilder sb = new StringBuilder();
            GetAdjPathName(entry, aboveRootEntry, pathSep, sb);
            return sb.ToString();
        }

        /// <summary>
        /// Recursively builds the full pathname, adjusting components to make them suitable for
        /// use on the host filesystem.
        /// </summary>
        private static void GetAdjPathName(IFileEntry entry, IFileEntry aboveRootEntry, char dirSep,
                StringBuilder sb) {
            if (entry.ContainingDir != aboveRootEntry) {
                GetAdjPathName(entry.ContainingDir, aboveRootEntry, dirSep, sb);
                if (sb.Length > 0) {
                    sb.Append(dirSep);
                }
                //// Adjust case and convert '.' to ' ' based on AppleWorks aux type.
                //string fileName =
                //    AWName.ChangeAppleWorksCase(entry.FileName, entry.FileType, entry.AuxType);
                string fileName = entry.FileName;
                // Adjust filename to be acceptable on host filesystem.
                fileName = PathName.AdjustFileName(fileName, PathName.DEFAULT_REPL_CHAR);
                sb.Append(fileName);
            }
        }

        /// <summary>
        /// Obtains the pathname of an arbitrary IFileEntry in a filesystem.  This can be the
        /// full path or a partial path, depending on the value of
        /// <paramref name="aboveRootEntry"/>.  The path components will be escaped for
        /// compatibility with the host filesystem.
        /// </summary>
        /// <param name="entry">File entry object.</param>
        /// <param name="aboveRootEntry">Node just above the root of the tree.  If the root is
        ///   the volume directory, this should be NO_ENTRY.</param>
        /// <param name="dirSep">Path separator character to place between entries.</param>
        public static string GetAdjEscPathName(IFileEntry entry, IFileEntry aboveRootEntry,
                char dirSep) {
            StringBuilder sb = new StringBuilder();
            GetAdjEscPathName(entry, aboveRootEntry, dirSep, true, sb);
            return sb.ToString();
        }

        /// <summary>
        /// Recursively builds the full pathname, escaping components to make them suitable for
        /// use on the host filesystem.
        /// </summary>
        private static void GetAdjEscPathName(IFileEntry entry, IFileEntry aboveRootEntry,
                char dirSep, bool isFileName, StringBuilder sb) {
            if (entry.ContainingDir != aboveRootEntry) {
                GetAdjEscPathName(entry.ContainingDir, aboveRootEntry, dirSep, false, sb);
                if (sb.Length > 0) {
                    sb.Append(dirSep);
                }
                if (isFileName) {
                    sb.Append(PathName.EscapeFileName(entry.FileName));
                } else {
                    sb.Append(PathName.AdjustFileName(entry.FileName, PathName.DEFAULT_REPL_CHAR));
                }
            }
        }
    }
}
