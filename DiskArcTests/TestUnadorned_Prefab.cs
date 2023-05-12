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

using CommonUtil;
using DiskArc;
using static DiskArc.Defs;
using static DiskArc.IFileSystem;

namespace DiskArcTests {
    /// <summary>
    /// Test some unadorned sector and nibble images created by other programs.  We mostly
    /// cover block/sector images during the filesystem tests, so this is mostly concerned
    /// with nibble formats.
    /// </summary>
    public class TestUnadorned_Prefab : ITest {
        // Quick tests.
        public static void TestVarious(AppHook appHook) {
            Helper.SimpleDiskCheck("dos33/dos-forty.do", FileKind.UnadornedSector, 1, appHook);
            Helper.SimpleDiskCheck("nib/dos33-master.nb2", FileKind.UnadornedNibble525, 27, appHook);
            Helper.SimpleDiskCheck("nib/dos321-master.nib", FileKind.UnadornedNibble525, 14, appHook);
        }

        // Test a 13-sector .NIB formatted in AppleWin, which will have $00 bytes in gap 1.
        public static void TestAWFormat(AppHook appHook) {
            // Open read/write (creates a copy in memory).
            using (Stream dataFile = Helper.OpenTestFile("nib/aw-init.nib", false, appHook)) {
                using (IDiskImage diskImage = FileAnalyzer.PrepareDiskImage(dataFile,
                        FileKind.UnadornedNibble525, appHook)!) {
                    // Should be no complaints about the disk image.
                    Helper.CheckNotes(diskImage, 0, 0);

                    // Find the filesystem and check that too.
                    diskImage.AnalyzeDisk();
                    IFileSystem fs = (IFileSystem)diskImage.Contents!;
                    if (CountUnwritableChunks(fs.RawAccess) != 0) {
                        throw new Exception("Found unwritable blocks");
                    }
                    fs.PrepareFileAccess(true);
                    Helper.CheckNotes(fs, 0, 0);

                    // Write a file and read it back, to make sure we're not trashing things.
                    IFileEntry volDir = fs.GetVolDirEntry();
                    IFileEntry newEntry = fs.CreateFile(volDir, "TEST", CreateMode.File);
                    newEntry.FileType = FileAttribs.FILE_TYPE_BIN;
                    using (DiskFileStream fileStream =
                            fs.OpenFile(newEntry, FileAccessMode.ReadWrite, FilePart.DataFork)) {
                        // Write a byte pattern that uses all 256 values.
                        fileStream.Write(Patterns.sRunPattern, 0, Patterns.sRunPattern.Length);
                    }

                    fs.PrepareRawAccess();
                    fs.PrepareFileAccess(true);
                    Helper.CheckNotes(fs, 0, 0);

                    volDir = fs.GetVolDirEntry();
                    IFileEntry entry = fs.FindFileEntry(volDir, "TEST");
                    byte[] buffer = new byte[entry.DataLength];
                    using (DiskFileStream fileStream =
                            fs.OpenFile(entry, FileAccessMode.ReadOnly, FilePart.DataFork)) {
                        fileStream.ReadExactly(buffer, 0, (int)entry.DataLength);
                    }
                    if (!RawData.CompareBytes(Patterns.sRunPattern, buffer, buffer.Length)) {
                        throw new Exception("Data mismatch");
                    }
                }
            }
        }

        private static int CountUnwritableChunks(IChunkAccess chunkAccess) {
            int failCount = 0;
            uint numTrks = chunkAccess.NumTracks;
            for (uint trk = 0; trk < numTrks; trk++) {
                for (uint sct = 0; sct < chunkAccess.NumSectorsPerTrack; sct++) {
                    chunkAccess.TestSector(trk, sct, out bool writable);
                    if (!writable) {
                        //Debug.WriteLine("Unwritable: T" + trk + " S" + sct);
                        failCount++;
                    }
                }
            }
            return failCount;
        }
    }
}
