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

using AppCommon;
using CommonUtil;
using DiskArc;
using DiskArc.Disk;
using DiskArc.Multi;
using static DiskArc.Defs;
using static DiskArc.IFileSystem;

namespace cp2 {
    /// <summary>
    /// Handles the "test" command.
    /// </summary>
    internal static class Test {
        public static bool HandleTest(string cmdName, string[] args, ParamsBag parms) {
            if (args.Length != 1) {
                CP2Main.ShowUsage(cmdName);
                return false;
            }

            if (!ExtArchive.OpenExtArc(args[0], true, true, parms, out DiskArcNode? rootNode,
                    out DiskArcNode? leafNode, out object? leaf, out IFileEntry endDirEntry)) {
                return false;
            }
            using (rootNode) {
                if (leaf is IArchive) {
                    IArchive arc = (IArchive)leaf;
                    if (!Misc.StdChecks(arc, needWrite: false, needMulti: false)) {
                        return false;
                    }

                    return TestArchive(arc, parms);
                } else if (leaf is IDiskImage || leaf is Partition) {
                    // There are Notes in IMultiPart objects, but those get skipped over by
                    // ExtArchive.  There are no Notes in Partitions.
                    bool hasDiskErrors = false;
                    if (leaf is IDiskImage) {
                        IDiskImage disk = (IDiskImage)leaf;
                        hasDiskErrors = (disk.Notes.ErrorCount != 0);
                        PrintNotes(disk.Notes, "Disk image");
                        if (disk is INibbleDataAccess) {
                            if (ScanDisk(disk, ref hasDiskErrors, parms)) {
                                //Console.WriteLine("Block/sector scan completed");
                            }
                        }
                        if (disk.Contents is IMultiPart) {
                            // This is a multi-part image.  Dump any Notes and bail.
                            IMultiPart partitions = (IMultiPart)disk.Contents;
                            if (partitions.IsDubious) {
                                Console.WriteLine("* Partition container marked as dubious");
                            }
                            PrintNotes(partitions.Notes, "Partition map");
                            return !partitions.IsDubious;
                        }
                    }
                    IFileSystem? fs = Misc.GetTopFileSystem(leaf);
                    if (!Misc.StdChecks(fs, needWrite: false, fastScan: false)) {
                        return false;
                    }

                    Console.WriteLine("Scanning " + ThingString.IFileSystem(fs) + " filesystem '"
                        + fs.GetVolDirEntry().FileName + "'");
                    bool ok = TestFileSystem(fs, parms);

                    IMultiPart? embeds = fs.FindEmbeddedVolumes();
                    if (embeds != null) {
                        Console.WriteLine();
                        Console.WriteLine("Scanning embedded volumes...");
                        if (embeds.IsDubious) {
                            Console.WriteLine("* Embed container marked as dubious");
                            ok = false;
                        }
                        PrintNotes(embeds.Notes, "Embed container");
                        int index = ExtArchive.ONE_BASED_INDEX ? 1 : 0;
                        foreach (Partition part in embeds) {
                            IFileSystem? embedFs = part.FileSystem;
                            if (!Misc.StdChecks(embedFs, needWrite: false, fastScan: false)) {
                                Console.WriteLine("Unable to scan filesystem in embed #" + index);
                                ok = false;
                            } else {
                                ok &= TestFileSystem(embedFs, parms);
                            }
                            index++;
                        }
                    }

                    return (ok && !hasDiskErrors);
                } else {
                    Debug.Assert(false);
                    return false;
                }
            }
        }

        private static bool TestArchive(IArchive arc, ParamsBag parms) {
            if (arc.Notes.Count != 0) {
                Console.WriteLine("Archive notes (" + arc.Notes.WarningCount + " warning/" +
                    arc.Notes.ErrorCount + " error):");
                List<Notes.Note> noteList = arc.Notes.GetNotes();
                foreach (Notes.Note note in noteList) {
                    Console.WriteLine("   " + note.ToString());
                }
            }

            // We're currently ignoring MacZip, because failures will generally take the form of
            // unreadable compressed data, but to be thorough it might make sense to verify the
            // integrity of resource forks stored in an ADF entry.

            int fileCount = 0;
            int forkCount = 0;
            int failCount = 0;
            foreach (IFileEntry entry in arc) {
                fileCount++;
                if (entry.IsDiskImage) {
                    forkCount++;
                    if (!TestArchiveFork(arc, entry, FilePart.DiskImage)) {
                        failCount++;
                    }
                }
                if (entry.HasDataFork) {
                    forkCount++;
                    if (!TestArchiveFork(arc, entry, FilePart.DataFork)) {
                        failCount++;
                    }
                }
                if (entry.HasRsrcFork) {
                    forkCount++;
                    if (!TestArchiveFork(arc, entry, FilePart.RsrcFork)) {
                        failCount++;
                    }
                }
            }

            if (parms.Verbose) {
                ReportResults(fileCount, forkCount, failCount);
            }
            return (failCount == 0 && arc.Notes.ErrorCount == 0);
        }

        private static bool TestArchiveFork(IArchive arc, IFileEntry entry, FilePart part) {
            try {
                using (Stream stream = arc.OpenPart(entry, part)) {
                    stream.CopyTo(Stream.Null);
                }
            } catch (Exception ex) {
                // Try not to show the filename twice.
                if (ex.Message.Contains(entry.FileName)) {
                    Console.Error.WriteLine("Failure: " + ex.Message);
                } else {
                    Console.Error.WriteLine("Failed reading " +
                        (part == FilePart.RsrcFork ? "rsrc" : "data") + " fork of '" +
                        entry.FullPathName + "': " + ex.Message);
                }
                return false;
            }
            return true;
        }

        /// <summary>
        /// Scans a disk image for bad blocks/sectors.
        /// </summary>
        private static bool ScanDisk(IDiskImage disk, ref bool errorsFound, ParamsBag parms) {
            if (disk.ChunkAccess == null) {
                Console.WriteLine("Disk format not recognized, skipping bad block scan");
                return false;
            }

            // ExtArchive has already analyzed the disk.  Analyzing it again is awkward
            // without the sector order hint, so just pick out the best chunk access.
            IChunkAccess chunks;
            if (disk.ChunkAccess.AccessLevel != GatedChunkAccess.AccessLvl.Closed) {
                chunks = disk.ChunkAccess;
            } else {
                if (disk.Contents == null) {
                    // Shouldn't happen.
                    Console.Error.WriteLine("Error: can't get filesystem object");
                    return false;
                }
                chunks = ((IFileSystem)disk.Contents).RawAccess;
            }

            string scanType;
            int checkCount, unreadCount, unwriteCount;
            checkCount = unreadCount = unwriteCount = 0;
            if (chunks.HasSectors) {
                scanType = "sectors";
                for (uint trk = 0; trk < chunks.NumTracks; trk++) {
                    for (uint sct = 0; sct < chunks.NumSectorsPerTrack; sct++) {
                        checkCount++;
                        if (!chunks.TestSector(trk, sct, out bool isWritable)) {
                            unreadCount++;
                            if (!isWritable) {
                                unwriteCount++;
                            }
                        }
                    }
                }
            } else if (chunks.HasBlocks) {
                scanType = "blocks";
                uint numBlocks = (uint)(chunks.FormattedLength / BLOCK_SIZE);
                for (uint blk = 0; blk < numBlocks; blk++) {
                    checkCount++;
                    if (!chunks.TestBlock(blk, out bool isWritable)) {
                        unreadCount++;
                        if (!isWritable) {
                            unwriteCount++;
                        }
                    }
                }
            } else {
                // Shouldn't happen.  KBlocks test as blocks.
                Console.Error.WriteLine("Error: disk image has neither blocks nor sectors");
                return false;
            }

            if (unreadCount > 0 || unwriteCount > 0) {
                StringBuilder sb = new StringBuilder();
                sb.Append("Scanned ");
                sb.Append(checkCount);
                sb.Append(' ');
                sb.Append(scanType);
                sb.Append("; found ");
                if (unreadCount == unwriteCount) {
                    sb.Append(unreadCount);
                    sb.Append(" errors");
                } else if (unwriteCount == 0) {
                    sb.Append(unreadCount);
                    sb.Append(" unreadable (okay for 13-sector disks)");
                } else {
                    sb.Append(unreadCount);
                    sb.Append(" unreadable, ");
                    sb.Append(unwriteCount);
                    sb.Append(" unwritable");
                }

                Console.Error.WriteLine(sb.ToString());
            } else if (parms.Verbose) {
                Console.WriteLine("Disk scan found no bad " + scanType);
            }
            if (unwriteCount > 0) {
                errorsFound = true;
            }
            return true;
        }

        private static bool TestFileSystem(IFileSystem fs, ParamsBag parms) {
            PrintNotes(fs.Notes, "Filesystem '" + fs.GetVolDirEntry().FileName + "'");
            if (fs.IsDubious) {
                Console.WriteLine("* filesystem is marked as dubious");
            }

            IFileEntry volDir = fs.GetVolDirEntry();
            int fileCount, forkCount, failCount;
            fileCount = forkCount = failCount = 0;
            TestDirectory(fs, volDir, ref fileCount, ref forkCount, ref failCount, parms);

            if (parms.Verbose) {
                ReportResults(fileCount, forkCount, failCount);
            }
            return (failCount == 0 && fs.Notes.ErrorCount == 0 && !fs.IsDubious);
        }

        private static void TestDirectory(IFileSystem fs, IFileEntry dirEnt,
                ref int fileCount, ref int forkCount, ref int failCount, ParamsBag parms) {
            foreach (IFileEntry entry in dirEnt) {
                if (entry.IsDirectory) {
                    TestDirectory(fs, entry, ref fileCount, ref forkCount, ref failCount, parms);
                } else {
                    fileCount++;
                    if (entry.HasDataFork) {
                        forkCount++;
                        // Use RawData to more fully scan DOS 3.3 disks.
                        if (!TestDiskFork(fs, entry, FilePart.RawData)) {
                            failCount++;
                        }
                    }
                    if (entry.HasRsrcFork) {
                        forkCount++;
                        if (!TestDiskFork(fs, entry, FilePart.RsrcFork)) {
                            failCount++;
                        }
                    }
                }
            }
        }

        private static bool TestDiskFork(IFileSystem fs, IFileEntry entry, FilePart part) {
            try {
                using (Stream stream = fs.OpenFile(entry, FileAccessMode.ReadOnly, part)) {
                    stream.CopyTo(Stream.Null);
                }
            } catch (Exception ex) {
                //Console.Error.WriteLine("Failed reading " +
                //    (part == FilePart.RsrcFork ? "rsrc" : "data") + " fork of '" +
                //    entry.FullPathName + "': " + ex.Message);
                Console.Error.WriteLine("Failure: " + ex.Message);
                return false;
            }
            return true;
        }

        /// <summary>
        /// Prints notes, if any.
        /// </summary>
        private static void PrintNotes(Notes notes, string title) {
            if (notes.Count == 0) {
                return;
            }
            Console.WriteLine(title + " notes (" + notes.WarningCount +
                " warning/" + notes.ErrorCount + " error):");
            List<Notes.Note> noteList = notes.GetNotes();
            foreach (Notes.Note note in noteList) {
                Console.WriteLine("   " + note.ToString());
            }
            Console.WriteLine();
        }

        private static void ReportResults(int fileCount, int forkCount, int failCount) {
            StringBuilder sb = new StringBuilder();
            sb.Append("Tested ");
            if (forkCount != fileCount) {
                sb.Append(forkCount);
                sb.Append(" forks in ");
            }
            sb.Append(fileCount);
            sb.Append(" files; ");
            if (failCount == 0) {
                sb.Append("no failures");
            } else {
                sb.Append("failures: ");
                sb.Append(failCount);
            }
            Console.WriteLine(sb.ToString());
        }
    }
}
