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

using AppCommon;
using CommonUtil;
using DiskArc;
using static DiskArc.Defs;
using static DiskArc.IFileSystem;

namespace Examples.AddFile {
    /// <summary>
    /// Simple example of a program that adds one file to a file archive or to the volume
    /// directory of a filesystem in a disk image.
    /// </summary>
    internal class AddFileMain {
        private const string CMD_NAME = "AddFile";

        private AppHook mAppHook = new AppHook(new SimpleMessageLog());


        /// <summary>
        /// OS entry point.
        /// </summary>
        static void Main(string[] args) {
            AddFileMain app = new AddFileMain();
            Environment.ExitCode = app.Execute(args);
        }

        /// <summary>
        /// Executes the command specified on the command line.
        /// </summary>
        /// <param name="args">Command arguments; does not include command name.</param>
        /// <returns>Zero on success, nonzero on failure.</returns>
        private int Execute(string[] args) {
            if (args.Length != 2) {
                Console.Error.WriteLine("Usage: " + CMD_NAME + " <archive> <file-to-add>");
                return 2;
            }

            try {
                // Open the archive and the data file.  Use "using" statements to ensure they're
                // closed before we return.
                using FileStream arcFile = new FileStream(args[0], FileMode.Open,
                    FileAccess.ReadWrite);
                using FileStream dataFile = new FileStream(args[1], FileMode.Open, FileAccess.Read);

                // Analyze the file.  Disk images and file archives are handled differently.
                string? ext = Path.GetExtension(args[0]);
                FileAnalyzer.AnalysisResult result = FileAnalyzer.Analyze(arcFile, ext,
                    mAppHook, out FileKind kind, out SectorOrder orderHint);
                if (result != FileAnalyzer.AnalysisResult.Success) {
                    Console.Error.WriteLine("Archive or disk image not recognized");
                    return 1;
                }

                if (IsDiskImageFile(kind)) {
                    if (!HandleDiskImage(arcFile, kind, orderHint, dataFile, args[1])) {
                        return 1;
                    }
                } else {
                    if (!HandleFileArchive(arcFile, kind, dataFile, args[1])) {
                        return 1;
                    }
                }
            } catch (IOException ex) {
                // Probably a FileNotFoundException.
                Console.Error.WriteLine("ERROR: " + ex.Message);
                return 1;
            }

            return 0;
        }

        /// <summary>
        /// Adds a file to the root directory of a disk image.
        /// </summary>
        private bool HandleDiskImage(Stream arcFile, FileKind kind, SectorOrder orderHint,
                Stream dataFile, string dataFileName) {
            using IDiskImage? diskImage = FileAnalyzer.PrepareDiskImage(arcFile, kind, mAppHook);
            if (diskImage == null) {
                Console.Error.WriteLine("Unable to prepare disk image");
                return false;
            }

            // Analyze the contents of the disk to determine file order and filesystem.
            if (!diskImage.AnalyzeDisk(null, orderHint, IDiskImage.AnalysisDepth.Full)) {
                Console.Error.WriteLine("Failed to analyze disk image");
                return false;
            }
            if (diskImage.Contents is IMultiPart) {
                Console.Error.WriteLine(
                    "Multi-partition disk formats are not supported by this command");
                return false;
            }

            // Get the filesystem, found earlier by AnalyzeDisk(), and prepare it for file access.
            IFileSystem fs = (IFileSystem)diskImage.Contents!;
            fs.PrepareFileAccess(true);

            try {
                // Create a new file in the root directory, using the original filename.
                IFileEntry volDir = fs.GetVolDirEntry();
                IFileEntry newEntry = fs.CreateFile(volDir, Path.GetFileName(dataFileName),
                    CreateMode.File);

                // Set the last modification date just to show how.
                FileInfo info = new FileInfo(dataFileName);
                newEntry.ModWhen = info.LastWriteTime;
                newEntry.SaveChanges();

                // Open the new file and copy the data over.
                using (DiskFileStream stream = fs.OpenFile(newEntry, FileAccessMode.ReadWrite,
                        FilePart.DataFork)) {
                    dataFile.CopyTo(stream);
                }
                return true;
            } catch (Exception ex) {
                Console.Error.WriteLine("ERROR: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Adds a file to a file archive.
        /// </summary>
        private bool HandleFileArchive(Stream arcFile, FileKind kind, Stream dataFile,
                string dataFileName) {
            using IArchive? archive = FileAnalyzer.PrepareArchive(arcFile, kind, mAppHook);
            if (archive == null) {
                Console.Error.WriteLine("Unable to open file archive");
                return false;
            }

            try {
                // Create a new record, and set the filename.  This will fail for single-record
                // formats like AppleSingle.
                archive.StartTransaction();
                IFileEntry newEntry = archive.CreateRecord();
                newEntry.FileName = Path.GetFileName(dataFileName);

                // Set the last modification date just to show how.
                FileInfo info = new FileInfo(dataFileName);
                newEntry.ModWhen = info.LastWriteTime;

                // Create a source object for the data file, and add it to the new record.
                SimplePartSource source = new SimplePartSource(dataFile);
                archive.AddPart(newEntry, FilePart.DataFork, source, CompressionFormat.Default);

                // Normally we would direct the commit to a new file, then after completion delete
                // the original and rename the new file in its place.  For this demo we're just
                // going to overwrite the original in place, so we commit to a memory stream.
                MemoryStream newStream = new MemoryStream();
                archive.CommitTransaction(newStream);

                // Truncate the original archive file and copy the new stream onto it.
                newStream.Position = 0;
                arcFile.Position = 0;
                arcFile.SetLength(0);
                newStream.CopyTo(arcFile);
                return true;
            } catch (Exception ex) {
                Console.Error.WriteLine("ERROR: " + ex.Message);
                archive.CancelTransaction();
                return false;
            }
        }
    }
}
