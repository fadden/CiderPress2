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
using System.Text;

using CommonUtil;
using DiskArc;
using DiskArc.Multi;
using static DiskArc.Defs;

namespace Examples.ListContents {
    /// <summary>
    /// Simple example of a program that lists the contents of a file archive or disk image.
    /// </summary>
    internal class ListContentsMain {
        private const string CMD_NAME = "ListContents";

        private AppHook mAppHook = new AppHook(new SimpleMessageLog());


        /// <summary>
        /// OS entry point.
        /// </summary>
        static void Main(string[] args) {
            ListContentsMain app = new ListContentsMain();
            Environment.ExitCode = app.Execute(args);
        }

        /// <summary>
        /// Executes the command specified on the command line.
        /// </summary>
        /// <param name="args">Command arguments; does not include command name.</param>
        /// <returns>Zero on success, nonzero on failure.</returns>
        private int Execute(string[] args) {
            if (args.Length != 1) {
                Console.Error.WriteLine("Usage: " + CMD_NAME + " <archive>");
                return 2;
            }

            try {
                // Open the archive file.
                using FileStream arcFile = new FileStream(args[0], FileMode.Open,
                    FileAccess.ReadWrite);

                // Analyze the file.  Disk images and file archives are handled differently.
                string? ext = Path.GetExtension(args[0]);
                FileAnalyzer.AnalysisResult result = FileAnalyzer.Analyze(arcFile, ext,
                    mAppHook, out FileKind kind, out SectorOrder orderHint);
                if (result != FileAnalyzer.AnalysisResult.Success) {
                    Console.Error.WriteLine("Archive or disk image not recognized");
                    return 1;
                }

                if (IsDiskImageFile(kind)) {
                    if (!HandleDiskImage(arcFile, kind, orderHint)) {
                        return 1;
                    }
                } else {
                    if (!HandleFileArchive(arcFile, kind)) {
                        return 1;
                    }
                }
            } catch (IOException ex) {
                // Probably a FileNotFoundException.
                Console.Error.WriteLine("Error: " + ex.Message);
                return 1;
            }

            return 0;
        }

        /// <summary>
        /// List the contents of a disk image.
        /// </summary>
        private bool HandleDiskImage(Stream arcFile, FileKind kind, SectorOrder orderHint) {
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
            if (diskImage.Contents is IFileSystem) {
                PrintFileSystemContents((IFileSystem)diskImage.Contents);
                return true;
            } else if (diskImage.Contents is IMultiPart) {
                return HandleMultiPart((IMultiPart)diskImage.Contents);
            } else {
                Console.Error.WriteLine("ARRRGH!");     // this shouldn't be possible
                return false;
            }
        }

        private bool HandleMultiPart(IMultiPart partitions) {
            // We could dive into each one, but for this simple example we'll just list them.
            Console.WriteLine("Found multi-part image with " + partitions.Count + " partitions:");
            StringBuilder sb = new StringBuilder();
            foreach (Partition part in partitions) {
                sb.Clear();
                sb.AppendFormat("  start={0,-9} count={1,-9}",
                    part.StartOffset / BLOCK_SIZE, part.Length / BLOCK_SIZE);
                APM_Partition? apmPart = part as APM_Partition;
                if (apmPart != null) {
                    sb.AppendFormat(" name='{0}' type='{1}'",
                        apmPart.PartitionName, apmPart.PartitionType);
                }
                Console.WriteLine(sb.ToString());
            }
            return true;
        }

        private void PrintFileSystemContents(IFileSystem fs) {
            fs.PrepareFileAccess(true);
            IFileEntry volDir = fs.GetVolDirEntry();
            PrintDirectory(volDir);
        }

        private void PrintDirectory(IFileEntry dirEntry) {
            foreach (IFileEntry entry in dirEntry) {
                Console.WriteLine(entry.FullPathName);
                if (entry.IsDirectory) {
                    PrintDirectory(entry);
                }
            }
        }

        /// <summary>
        /// Lists the contents of a file archive.
        /// </summary>
        private bool HandleFileArchive(Stream arcFile, FileKind kind) {
            using IArchive? archive = FileAnalyzer.PrepareArchive(arcFile, kind, mAppHook);
            if (archive == null) {
                Console.Error.WriteLine("Unable to open file archive");
                return false;
            }

            foreach (IFileEntry entry in archive) {
                Console.WriteLine(entry.FullPathName);
            }
            return true;
        }
    }
}
