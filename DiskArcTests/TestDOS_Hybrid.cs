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
    /// <summary>
    /// Test DOS hybrids, e.g. DOS and ProDOS on the same 5.25" floppy.
    /// </summary>
    public class TestDOS_Hybrid : ITest {
        // Simple test to verify recognition of various disk configurations.
        public static void TestSimple(AppHook appHook) {
            CheckEmbed("hybrid/dos-prodos.do", 1, FileSystemType.ProDOS, 4, "PRODOS", appHook);
            CheckEmbed("cpm/CPAM51b.do", 3, FileSystemType.CPM, 14, "AUTORUN4.COM", appHook);
            // TODO: need a DOS+Pascal test
        }

        private static void CheckEmbed(string pathName, int expDosFileCount,
                FileSystemType otherKind, int expOtherFileCount, string testFileName,
                AppHook appHook) {
            using (Stream dataFile = Helper.OpenTestFile(pathName, true, appHook)) {
                using (IDiskImage diskImage = FileAnalyzer.PrepareDiskImage(dataFile,
                        FileKind.UnadornedSector, appHook)!) {
                    Helper.CheckNotes(diskImage, 0, 0);
                    diskImage.AnalyzeDisk();
                    IFileSystem fs = (IFileSystem)diskImage.Contents!;
                    fs.PrepareFileAccess(true);
                    // There are in-use but un-owned blocks (which is a warning for these tests).
                    Helper.CheckNotes(fs, 1, 0);

                    List<IFileEntry> entries = fs.GetVolDirEntry().ToList();
                    if (entries.Count != expDosFileCount) {
                        throw new Exception("Incorrect DOS file count: expected=" +
                            expDosFileCount + ", actual=" + entries.Count);
                    }

                    IMultiPart? embeds = fs.FindEmbeddedVolumes();
                    if (embeds == null) {
                        throw new Exception("No embedded volumes found");
                    }
                    if (embeds.Count != 1) {
                        throw new Exception("Embedded volume count mismatch");
                    }
                    Partition part = embeds[0];
                    IFileSystem embedFs = part.FileSystem!;
                    embedFs.PrepareFileAccess(true);
                    Helper.CheckNotes(embedFs, 1, 0);

                    if (embedFs.GetFileSystemType() != otherKind) {
                        throw new Exception("Found wrong type of embedded filesystem");
                    }

                    // Confirm the test file exists.
                    IFileEntry volDir = embedFs.GetVolDirEntry();
                    // This throws an exception if the file is not found.
                    IFileEntry testEntry = embedFs.FindFileEntry(volDir, testFileName);

                    Helper.ExpectInt(expOtherFileCount, volDir.Count,
                        "wrong number of files in embed");
                }
            }
        }

        // Create and verify a file in the ProDOS "partition".
        public static void TestUpdate_ProDOS(AppHook appHook) {
            using (Stream dataFile = Helper.OpenTestFile("hybrid/dos-prodos.do", false, appHook)) {
                DoTestUpdate(dataFile, appHook);
            }
        }

        private static void DoTestUpdate(Stream dataFile, AppHook appHook) {
            using (IDiskImage diskImage = FileAnalyzer.PrepareDiskImage(dataFile,
                    FileKind.UnadornedSector, appHook)!) {
                diskImage.AnalyzeDisk();
                IFileSystem fs = (IFileSystem)diskImage.Contents!;
                fs.PrepareFileAccess(true);
                IMultiPart embeds = fs.FindEmbeddedVolumes()!;
                Partition part = embeds[0];
                IFileSystem embedFs = part.FileSystem!;
                embedFs.PrepareFileAccess(true);

                IFileEntry volDir = embedFs.GetVolDirEntry();
                IFileEntry newEntry = embedFs.CreateFile(volDir, "STUFF", CreateMode.File);
                using (DiskFileStream stuff = embedFs.OpenFile(newEntry,
                        FileAccessMode.ReadWrite, FilePart.DataFork)) {
                    stuff.Write(Patterns.sUlyssesBytes, 0, Patterns.sUlyssesBytes.Length);
                }
            }

            // Reopen the disk image and confirm that the changes were successful.
            using (IDiskImage diskImage = FileAnalyzer.PrepareDiskImage(dataFile,
                    FileKind.UnadornedSector, appHook)!) {
                diskImage.AnalyzeDisk();
                IFileSystem fs = (IFileSystem)diskImage.Contents!;
                fs.PrepareFileAccess(true);
                IMultiPart embeds = fs.FindEmbeddedVolumes()!;
                Partition part = embeds[0];
                IFileSystem embedFs = part.FileSystem!;
                embedFs.PrepareFileAccess(true);

                IFileEntry volDir = embedFs.GetVolDirEntry();
                IFileEntry entry = embedFs.FindFileEntry(volDir, "STUFF");
                using (DiskFileStream stuff = embedFs.OpenFile(entry,
                        FileAccessMode.ReadOnly, FilePart.DataFork)) {
                    byte[] buf = new byte[Patterns.sUlyssesBytes.Length];
                    stuff.Read(buf, 0, buf.Length);
                    if (!RawData.CompareBytes(buf, Patterns.sUlyssesBytes,
                            Patterns.sUlyssesBytes.Length)) {
                        throw new Exception("data mismatch");
                    }

                    try {
                        // try to close the outer filesystem; should fail with inner file open
                        fs.PrepareRawAccess();
                    } catch (DAException) { /*expected*/ }
                }
            }
        }
    }
}
