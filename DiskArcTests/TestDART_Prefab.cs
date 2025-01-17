/*
 * Copyright 2025 faddenSoft
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
using DiskArc.Arc;
using DiskArc.Disk;
using DiskArc.FS;
using static DiskArc.Defs;

namespace DiskArcTests {
    /// <summary>
    /// Test some DART disk images created on a Mac.
    /// </summary>
    internal class TestDART_Prefab : ITest {
        private const string IMAGE_ZIP = "dart/test1.zip";
        private const string IMAGE_FILE = "test1-best";

        public static void TestImages(AppHook appHook) {
            // The images are stored in a ZIP file.  Most of the DART images are actually stored
            // inside an unadorned disk image, but for this test we can just look at the one
            // that's sitting in the ZIP archive.
            using (Stream dataFile = Helper.OpenTestFile(IMAGE_ZIP, true, appHook)) {
                using (Zip zip = Zip.OpenArchive(dataFile, appHook)) {
                    IFileEntry entry = zip.FindFileEntry(IMAGE_FILE);
                    MemoryStream copy = new MemoryStream();
                    using (Stream diskStream = zip.OpenPart(entry, FilePart.DataFork)) {
                        // Copy to seekable stream.
                        diskStream.CopyTo(copy);
                    }
                    using (DART dart = DART.OpenDisk(copy, appHook)) {
                        Helper.CheckNotes(dart, 0, 0);

                        dart.AnalyzeDisk();
                        HFS hfs = (HFS)dart.Contents!;
                        hfs.PrepareFileAccess(true);
                        Helper.CheckNotes(hfs, 0, 0);
                    }
                }
            }
        }
    }
}
