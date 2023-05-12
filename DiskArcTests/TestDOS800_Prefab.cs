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
    public class TestDOS800_Prefab : ITest {
        // Simple test to verify recognition of various disk configurations.
        public static void TestSimple(AppHook appHook) {
            CheckEmbed("dos800/AmDOS-sample.po", 1, appHook);
            CheckEmbed("dos800/UniDOS-sample.po", 1, appHook);
            CheckEmbed("dos800/OzDOS-sample.po", 1, appHook);
        }

        private static void CheckEmbed(string pathName, int expFileCount, AppHook appHook) {
            using (Stream dataFile = Helper.OpenTestFile(pathName, true, appHook)) {
                using (IDiskImage diskImage = FileAnalyzer.PrepareDiskImage(dataFile,
                        FileKind.UnadornedSector, appHook)!) {
                    diskImage.AnalyzeDisk();
                    if (diskImage.Contents is IFileSystem) {
                        throw new Exception("Not expecting filesystem here");
                    }
                    if (diskImage.Contents == null) {
                        throw new Exception("Unable to locate partitions");
                    }
                    Helper.CheckNotes(diskImage, 0, 0);

                    int partNum = 0;
                    foreach (Partition part in (IMultiPart)diskImage.Contents) {
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

        public static void TestUpdate(AppHook appHook) {
            DoTestUpdate("dos800/AmDOS-sample.po", 0, appHook);
            DoTestUpdate("dos800/UniDOS-sample.po", 1, appHook);
            DoTestUpdate("dos800/OzDOS-sample.po", 1, appHook);
        }
        private static void DoTestUpdate(string fileName, int partNum, AppHook appHook) {
            using (Stream dataFile = Helper.OpenTestFile(fileName, false, appHook)) {
                using (IDiskImage diskImage = FileAnalyzer.PrepareDiskImage(dataFile,
                        FileKind.UnadornedSector, appHook)!) {
                    diskImage.AnalyzeDisk();
                    IMultiPart parts = (IMultiPart)diskImage.Contents!;
                    Partition part = parts[partNum];
                    part.AnalyzePartition();
                    IFileSystem fs = part.FileSystem!;
                    fs.PrepareFileAccess(true);

                    IFileEntry volDir = fs.GetVolDirEntry();
                    IFileEntry newEntry = fs.CreateFile(volDir, "STUFF", CreateMode.File);
                    using (DiskFileStream stuff = fs.OpenFile(newEntry,
                            FileAccessMode.ReadWrite, FilePart.DataFork)) {
                        stuff.Write(Patterns.sRunPattern, 0, Patterns.sRunPattern.Length);
                    }
                }

                using (IDiskImage diskImage = FileAnalyzer.PrepareDiskImage(dataFile,
                        FileKind.UnadornedSector, appHook)!) {
                    diskImage.AnalyzeDisk();
                    IMultiPart parts = (IMultiPart)diskImage.Contents!;
                    Partition part = parts[partNum];
                    part.AnalyzePartition();
                    IFileSystem fs = part.FileSystem!;
                    fs.PrepareFileAccess(true);

                    IFileEntry volDir = fs.GetVolDirEntry();
                    IFileEntry entry = fs.FindFileEntry(volDir, "STUFF");
                    using (DiskFileStream stuff = fs.OpenFile(entry,
                            FileAccessMode.ReadWrite, FilePart.DataFork)) {
                        byte[] buf = new byte[Patterns.sRunPattern.Length];
                        stuff.Read(buf, 0, buf.Length);
                        if (!RawData.CompareBytes(buf, Patterns.sRunPattern,
                                Patterns.sRunPattern.Length)) {
                            throw new Exception("data mismatch");
                        }
                    }
                }
            }
        }
    }
}
