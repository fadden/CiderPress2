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
using System.Collections.Generic;
using System.IO;
using System.Linq;

using CommonUtil;
using DiskArc;
using DiskArc.Multi;
using static DiskArc.Defs;
using static DiskArc.IFileSystem;

namespace DiskArcTests {
    public class TestProDOS_Embedded : ITest {
        // Simple test to verify recognition of various disk configurations.
        public static void TestSimple(AppHook appHook) {
            CheckEmbed("dos.master/dm-140x1.po", 3, 1, appHook);
            CheckEmbed("dos.master/dm-140x5.po", 3, 5, appHook);
            CheckEmbed("dos.master/dm-160x5.po", 0, 5, appHook);
            CheckEmbed("dos.master/dm-160x5+.po", 2, 5, appHook);
            CheckEmbed("dos.master/dm-200x3.po", 3, 3, appHook);
            CheckEmbed("dos.master/dm-200x4+.po", 2, 4, appHook);
            CheckEmbed("dos.master/dm-400x2+.po", 2, 2, appHook);
        }

        private static void CheckEmbed(string pathName, int expFileCount, int expEmbedCount,
                AppHook appHook) {
            using (Stream dataFile = Helper.OpenTestFile(pathName, true, appHook)) {
                using (IDiskImage diskImage = FileAnalyzer.PrepareDiskImage(dataFile,
                        FileKind.UnadornedSector, appHook)!) {
                    Helper.CheckNotes(diskImage, 0, 0);
                    diskImage.AnalyzeDisk();
                    IFileSystem fs = (IFileSystem)diskImage.Contents!;
                    fs.PrepareFileAccess(true);
                    // One warning, about the big chunk of in-use blocks.
                    Helper.CheckNotes(fs, 1, 0);

                    List<IFileEntry> entries = fs.GetVolDirEntry().ToList();
                    if (entries.Count != expFileCount) {
                        throw new Exception("Incorrect file count: expected=" + expFileCount +
                            ", actual=" + entries.Count);
                    }

                    IMultiPart? embeds = fs.FindEmbeddedVolumes();
                    if (embeds == null) {
                        throw new Exception("No embedded volumes found");
                    }
                    if (embeds.Count != expEmbedCount) {
                        throw new Exception("Embedded volume count mismatch");
                    }
                    int partNum = 0;
                    foreach (Partition part in embeds) {
                        IFileSystem embedFs = part.FileSystem!;
                        embedFs.PrepareFileAccess(true);

                        // Confirm we found the right partition by checking for the presence
                        // of a file.  It's "HELLO" in the first partition, and "HELLO#" in
                        // every subsequent partition.
                        partNum++;
                        string helloName = "HELLO" + (partNum == 1 ? "" : partNum);
                        IFileEntry volDir = embedFs.GetVolDirEntry();
                        // This throws an exception if the file is not found.
                        IFileEntry hello = embedFs.FindFileEntry(volDir, helloName);

                        // They should all be short, around 32 bytes.  The length is stored in
                        // the first two bytes of the file, so this is reasonable then we can
                        // assume that we correctly found the file.
                        if (hello.DataLength < 20 || hello.DataLength > 100) {
                            throw new Exception("Wrong length for HELLO?");
                        }
                    }
                }
            }
        }

        public static void TestTurducken(AppHook appHook) {
            using (Stream dataFile = Helper.OpenTestFile("apm/apm-prodos-hfs.iso", false,
                    appHook)) {
                using (IDiskImage diskImage = FileAnalyzer.PrepareDiskImage(dataFile,
                        FileKind.UnadornedSector, appHook)!) {
                    Helper.CheckNotes(diskImage, 0, 0);
                    diskImage.AnalyzeDisk();
                    if (diskImage.Contents is IFileSystem) {
                        throw new Exception("Not expecting a filesystem here");
                    }
                    IMultiPart parts = (IMultiPart)diskImage.Contents!;
                    Partition firstPart = parts[0];
                    firstPart.AnalyzePartition();
                    IFileSystem? proFs = firstPart.FileSystem;
                    if (proFs == null) {
                        throw new Exception("Didn't find ProDOS in first partition");
                    }

                    // Prepare file access and find the DOS embeds.
                    proFs.PrepareFileAccess(true);
                    Helper.CheckNotes(proFs, 1, 0);
                    IMultiPart? dosParts = proFs.FindEmbeddedVolumes();
                    if (dosParts == null) {
                        throw new Exception("Couldn't find DOS embeds");
                    }
                    Partition firstDos = dosParts[0];
                    IFileSystem? dosFs = firstDos.FileSystem;
                    if (dosFs == null) {
                        throw new Exception("First DOS partition not analyzed");
                    }

                    // Find the "HELLO" file.
                    dosFs.PrepareFileAccess(true);
                    Helper.CheckNotes(dosFs, 0, 0);
                    IFileEntry volDir = dosFs.GetVolDirEntry();
                    IFileEntry hello = dosFs.FindFileEntry(volDir, "HELLO");

                    if (diskImage.IsModified) {
                        throw new Exception("Disk image already modified");
                    }

                    // Rename the file and confirm that the "is modified" flag propagates up.
                    hello.FileName = "GOODBYE";
                    hello.SaveChanges();
                    diskImage.Flush();
                    if (!diskImage.IsModified) {
                        throw new Exception("Disk image not showing modified");
                    }

                    // Explicitly clear the "is modified" flag.
                    diskImage.IsModified = false;
                    if (diskImage.IsModified) {
                        throw new Exception("Flag failed to clear");
                    }

                    // Create something a bit bigger to double-check our I/O.
                    IFileEntry newEntry = dosFs.CreateFile(volDir, "STUFF", CreateMode.File);
                    using (DiskFileStream stuff = dosFs.OpenFile(newEntry,
                            FileAccessMode.ReadWrite, FilePart.DataFork)) {
                        stuff.Write(Patterns.sRunPattern, 0, Patterns.sRunPattern.Length);
                    }
                }

                // Reopen the disk image and confirm that the changes were successful.
                using (IDiskImage diskImage = FileAnalyzer.PrepareDiskImage(dataFile,
                        FileKind.UnadornedSector, appHook)!) {
                    diskImage.AnalyzeDisk();
                    IMultiPart parts = (IMultiPart)diskImage.Contents!;
                    Partition firstPart = parts[0];
                    firstPart.AnalyzePartition();
                    IFileSystem proFs = firstPart.FileSystem!;
                    proFs.PrepareFileAccess(true);
                    IMultiPart dosParts = proFs.FindEmbeddedVolumes()!;
                    Partition firstDos = dosParts[0];
                    IFileSystem dosFs = firstDos.FileSystem!;
                    dosFs.PrepareFileAccess(true);

                    IFileEntry volDir = dosFs.GetVolDirEntry();
                    IFileEntry hello = dosFs.FindFileEntry(volDir, "GOODBYE");

                    IFileEntry stuffEntry = dosFs.FindFileEntry(volDir, "STUFF");
                    // The disk file length will be rounded up to the nearest 256-byte boundary.
                    if (stuffEntry.DataLength != ((Patterns.sRunPattern.Length + 255) & ~255)) {
                        throw new Exception("Unexpected length");
                    }
                    using (DiskFileStream stuff = dosFs.OpenFile(stuffEntry,
                            FileAccessMode.ReadOnly, FilePart.DataFork)) {
                        byte[] buf = new byte[Patterns.sRunPattern.Length];
                        stuff.Read(buf, 0, buf.Length);
                        if (!RawData.CompareBytes(buf, Patterns.sRunPattern,
                                Patterns.sRunPattern.Length)) {
                            throw new Exception("data mismatch");
                        }

                        try {
                            // try to close the outer filesystem; should fail with inner file open
                            proFs.PrepareRawAccess();
                        } catch (DAException) { /*expected*/ }
                    }

                    if (diskImage.IsModified) {
                        throw new Exception("Shouldn't be reporting modifications");
                    }
                }
            }
        }
    }
}
