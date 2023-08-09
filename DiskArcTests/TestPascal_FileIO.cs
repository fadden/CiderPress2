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
using System.IO;
using System.Text;

using CommonUtil;
using DiskArc;
using static DiskArc.Defs;
using static DiskArc.IFileSystem;

namespace DiskArcTests {
    public class TestPascal_FileIO : ITest {
        // Simple create / delete test.
        public static void TestCreateDelete(AppHook appHook) {
            using (IFileSystem fs = Make525Floppy("Tester", appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();

                IFileEntry fileX = fs.CreateFile(volDir, "FILEx", CreateMode.File);
                IFileEntry file1 = fs.CreateFile(volDir, "FILE0", CreateMode.File);
                IFileEntry fileY = fs.CreateFile(volDir, "FILEy", CreateMode.File);
                IFileEntry file3 = fs.CreateFile(volDir, "FILE1", CreateMode.File);
                IFileEntry fileZ = fs.CreateFile(volDir, "FILEz", CreateMode.File);
                fs.DeleteFile(fileX);
                fs.DeleteFile(fileY);
                fs.DeleteFile(fileZ);
                IFileEntry file0 = fs.CreateFile(volDir, "FILE2", CreateMode.File);
                IFileEntry file2 = fs.CreateFile(volDir, "FILE3", CreateMode.File);
                IFileEntry file4 = fs.CreateFile(volDir, "FILE4", CreateMode.File);

                // Files are added to the largest gap, which means they will always be added
                // to the end.
                int index = 0;
                foreach (IFileEntry entry in volDir) {
                    string expName = "FILE" + index;
                    Helper.ExpectString(expName, entry.FileName, "file out of order");
                    index++;
                }

                //fs.DumpToFile(@"c:\src\ciderpress2\test.po");
            }
        }

        #region Utilities

        private static IFileSystem Make525Floppy(string volName, AppHook appHook) {
            return Helper.CreateTestImage(volName, FileSystemType.Pascal, 280, appHook,
                out MemoryStream memFile);
        }

        #endregion Utilities

    }
}
