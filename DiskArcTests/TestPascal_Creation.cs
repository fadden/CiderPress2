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
using System.IO;

using CommonUtil;
using DiskArc;
using DiskArc.FS;
using static DiskArc.Defs;
using static DiskArc.IDiskImage;
using static DiskArc.IFileSystem;

namespace DiskArcTests {
    /// <summary>
    /// Test creation of Apple Pascal volumes.
    /// </summary>
    public class TestPascal_Creation : ITest {
        private const int SYSTEM_SPACE = 6 * BLOCK_SIZE;

        // Basic volume creation tests.
        public static void TestCreateVol(AppHook appHook) {
            // 140KB floppy.
            using (IFileSystem fs = Helper.CreateTestImage("NEW140K", FileSystemType.Pascal,
                    35, 16, 1, true, appHook, out MemoryStream memFile)) {
                if (fs.Notes.WarningCount != 0 || fs.Notes.ErrorCount != 0) {
                    throw new Exception("Source image isn't clean: " + fs.Notes.ToString());
                }

                if (fs.FreeSpace != 35 * 16 * SECTOR_SIZE - SYSTEM_SPACE) {
                    throw new Exception("Incorrect free space #1: " + fs.FreeSpace);
                }
                Helper.ExpectString("NEW140K", fs.GetVolDirEntry().FileName, "wrong volume name");
            }

            // 800KB floppy.
            using (IFileSystem fs = Helper.CreateTestImage("NEW800K", FileSystemType.Pascal,
                    1600, appHook, out MemoryStream memFile)) {
                if (fs.Notes.WarningCount != 0 || fs.Notes.ErrorCount != 0) {
                    throw new Exception("Source image isn't clean: " + fs.Notes.ToString());
                }

                if (fs.FreeSpace != 1600 * BLOCK_SIZE - SYSTEM_SPACE) {
                    throw new Exception("Incorrect free space #2: " + fs.FreeSpace);
                }
                Helper.ExpectString("NEW800K", fs.GetVolDirEntry().FileName, "wrong volume name");
            }
        }
    }
}
