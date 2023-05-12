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
using System.IO;

using CommonUtil;
using DiskArc;
using static DiskArc.Defs;
using static DiskArc.IFileSystem;

namespace DiskArcTests {
    /// <summary>
    /// Test behavior when various problems are encountered.  The disk images are damaged
    /// via the raw data interface.
    /// </summary>
    public class TestHFS_Damage : ITest {
        public static void TestBadNextCNID(AppHook appHook) {
            using (IFileSystem fs = Make35Floppy("Dmg", appHook)) {
                // Create a file.  Next CNID should now be 17.
                IFileEntry volDir = fs.GetVolDirEntry();
                fs.CreateFile(volDir, "File1", CreateMode.File);

                // Get the MDB.
                fs.PrepareRawAccess();
                byte[] blockBuf = new byte[BLOCK_SIZE];
                fs.RawAccess.ReadBlock(DiskArc.FS.HFS.MDB_BLOCK_NUM, blockBuf, 0);
                uint nextCNID = RawData.GetU32BE(blockBuf, 0x1e);
                if (nextCNID != 17) {
                    throw new Exception("Unexpected next CNID value: " + nextCNID);
                }

                // Modify the value and write it back, then verify the error is detected.
                RawData.SetU32BE(blockBuf, 0x1e, nextCNID - 1);
                fs.RawAccess.WriteBlock(DiskArc.FS.HFS.MDB_BLOCK_NUM, blockBuf, 0);

                fs.PrepareFileAccess(true);
                if (!fs.IsDubious) {
                    throw new Exception("Bad next CNID went unnoticed");
                }
            }
        }

        // TODO: test having two file entries with the same extent


        #region Utilities

        // Make a 3.5" floppy image (800KB).
        public static IFileSystem Make35Floppy(string volName, AppHook appHook) {
            return Helper.CreateTestImage(volName, FileSystemType.HFS, 1600, appHook,
                out MemoryStream memFile);
        }

        #endregion Utilities
    }
}
