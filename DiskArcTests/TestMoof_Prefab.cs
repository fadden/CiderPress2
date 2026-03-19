/*
 * Copyright 2026 faddenSoft
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
using DiskArc.Disk;
using static DiskArc.Defs;
using static DiskArc.IFileSystem;

namespace DiskArcTests {
    /// <summary>
    /// Tests some MOOF files to ensure we can successfully open images created by others.
    /// </summary>
    public class TestMoof_Prefab : ITest {
        public static void TestSimple(AppHook appHook) {
            Helper.SimpleDiskCheck("moof/Zork I r76-840509.moof", FileKind.Moof, 8, appHook);
        }

        public static void TestFlux(AppHook appHook) {
            const string PATHNAME = "moof/Oids v1.4.moof";
            const int EXP_COUNT = 3;
            const int EXP_BAD = 1;

            using (Stream dataFile = Helper.OpenTestFile(PATHNAME, true, appHook)) {
                using (IDiskImage? diskImage = FileAnalyzer.PrepareDiskImage(dataFile,
                        FileKind.Moof, appHook)) {
                    if (diskImage == null) {
                        throw new Exception("Failed to prepare MOOF disk image (name='" +
                            PATHNAME + "')");
                    }
                    // Should be no complaints about the disk image.
                    Helper.CheckNotes(diskImage, 0, 0);

                    // Find the filesystem and check that too.
                    diskImage.AnalyzeDisk();
                    IFileSystem fs = (IFileSystem)diskImage.Contents!;
                    fs.PrepareFileAccess(true);
                    Helper.CheckNotes(fs, 0, 0);

                    List<IFileEntry> entries = fs.GetVolDirEntry().ToList();
                    if (entries.Count != EXP_COUNT) {
                        throw new Exception("Incorrect file count: expected=" + EXP_COUNT +
                            ", actual=" + entries.Count);
                    }

                    fs.PrepareRawAccess();
                    int unreadable = fs.RawAccess.CountUnreadableChunks();
                    if (unreadable != EXP_BAD) {
                        throw new Exception("Incorrect unreadable file count: expected=" + EXP_BAD +
                            ", actual=" + unreadable);
                    }
                }
            }
        }
    }
}
