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
using DiskArc.FS;
using static DiskArc.Defs;
using static DiskArc.IFileSystem;

// TODO: rename a file with multiple extents

namespace DiskArcTests {
    /// <summary>
    /// CP/M filesystem I/O tests.
    /// </summary>
    public class TestCPM_FileIO : ITest {
        private struct StringPair {
            public string String1 { get; }
            public string String2 { get; }
            public StringPair(string str1, string str2) {
                String1 = str1;
                String2 = str2;
            }
        }
        private static readonly StringPair[] sAdjustNames = new StringPair[] {
            new StringPair("FULLNAME.TXT", "FULLNAME.TXT"),
            new StringPair("SHORT.T", "SHORT.T"),
            new StringPair("NOEXT", "NOEXT"),
            new StringPair("bad[]chr.<x>", "BAD__CHR._X_"),
            new StringPair("", "Q"),
            new StringPair(".Foo", "Q.FOO"),
            new StringPair(" SPACES . z ", "_SPACES_._Z_"),
            new StringPair("Ctrl\r\n.\t", "CTRL__._"),
            new StringPair("NameTooLong.Extended", "NAME_ONG.EXT"),
            new StringPair("LOTS.OF.DOTS", "LOTS_OF.DOT"),
        };

        public static void TestAdjustName(AppHook appHook) {
            foreach (StringPair sp in sAdjustNames) {
                string adjusted = CPM_FileEntry.AdjustFileName(sp.String1);
                if (adjusted != sp.String2) {
                    throw new Exception("Adjustment failed: '" + sp.String1 + "' -> '" +
                        adjusted + "', expected '" + sp.String2 + "'");
                }
            }
        }

        // Simple create / delete test.
        public static void TestCreateDelete(AppHook appHook) {
            using (IFileSystem fs = Make525Floppy("Tester", appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();

                IFileEntry fileX = fs.CreateFile(volDir, "FILEx", CreateMode.File);
                try {
                    fs.CreateFile(volDir, "FILEx", CreateMode.File);
                    throw new Exception("Created a second file with same name");
                } catch (IOException) { /*expected*/ }
                IFileEntry file0 = fs.CreateFile(volDir, "FILE0", CreateMode.File);
                IFileEntry fileY = fs.CreateFile(volDir, "FILEy", CreateMode.File);
                IFileEntry file1 = fs.CreateFile(volDir, "FILE1", CreateMode.File);
                IFileEntry fileZ = fs.CreateFile(volDir, "FILEz", CreateMode.File);
                fs.DeleteFile(fileX);
                fs.DeleteFile(fileY);
                fs.DeleteFile(fileZ);
                IFileEntry file2 = fs.CreateFile(volDir, "FILE2", CreateMode.File);
                IFileEntry file3 = fs.CreateFile(volDir, "FILE3", CreateMode.File);
                IFileEntry file4 = fs.CreateFile(volDir, "FILE4", CreateMode.File);

                // Even though we're re-using extents, we currently just add files to the end
                // of the list.  If the filesystem is reloaded the order will be different.
                int index = 0;
                foreach (IFileEntry entry in volDir) {
                    string expName = "FILE" + index;
                    Helper.ExpectString(expName, entry.FileName, "file out of order");
                    index++;
                }
            }
        }

        public static void TestAttributes(AppHook appHook) {
            using (IFileSystem fs = Make525Floppy("Tester", appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();
                IFileEntry file0 = fs.CreateFile(volDir, "FILE0", CreateMode.File);
                IFileEntry file1 = fs.CreateFile(volDir, "FILE1.$$$", CreateMode.File);
                IFileEntry file2 = fs.CreateFile(volDir, "FILE2", CreateMode.File);

                Helper.ExpectString("FILE1.$$$", file1.FileName, "wrong filename");
                byte[] raw = file1.RawFileName;
                if (raw.Length != 11 || Encoding.ASCII.GetString(raw) != "FILE1   $$$") {
                    throw new Exception("Incorrect raw filename");
                }
                Helper.ExpectByte(0, file1.FileType, "wrong file type");
                Helper.ExpectInt(0, file1.AuxType, "wrong aux type");
                // not currently handling timestamps
                if (file1.CreateWhen != TimeStamp.NO_DATE || file1.ModWhen != TimeStamp.NO_DATE) {
                    throw new Exception("Incorrect create/mod date");
                }

                file0.RawFileName = new byte[11] {
                    (byte)'F', (byte)'i', (byte)'l', (byte)'e', (byte)'0', (byte)'A',
                    (byte)' ', (byte)' ', (byte)'W', (byte)' ', (byte)' '
                };
                Helper.ExpectString("File0A.W", file0.FileName, "failed to set raw filename");
                file1.FileName = "FILE1A.X";
                Helper.ExpectString("FILE1A.X", file1.FileName, "failed to set filename");
                try {
                    file1.FileName = "ARG<>!";
                    throw new Exception("bad name accepted");
                } catch (ArgumentException) { /*expected*/ }

                Helper.ExpectByte(FileAttribs.FILE_ACCESS_UNLOCKED, file0.Access,
                    "bad initial access flags");
                file1.Access = (byte)(FileAttribs.AccessFlags.Invisible |
                    FileAttribs.AccessFlags.Backup);    // read, rename, delete always present
                Helper.ExpectByte((byte)(FileAttribs.AccessFlags.Read |
                    FileAttribs.AccessFlags.Rename | FileAttribs.AccessFlags.Delete |
                    FileAttribs.AccessFlags.Invisible | FileAttribs.AccessFlags.Backup),
                    file1.Access, "failed to change access flags");

                fs.MoveFile(file2, volDir, "FILE2A");

                // Bounce the filesystem without calling SaveChanges().
                fs.PrepareRawAccess();
                fs.PrepareFileAccess(true);
                Helper.CheckNotes(fs, 0, 0);

                Helper.ExpectString("File0A.W", file0.FileName, "failed to keep raw filename");
                Helper.ExpectString("FILE1A.X", file1.FileName, "failed to keep filename");
                Helper.ExpectByte((byte)(FileAttribs.AccessFlags.Read |
                    FileAttribs.AccessFlags.Rename | FileAttribs.AccessFlags.Delete |
                    FileAttribs.AccessFlags.Invisible | FileAttribs.AccessFlags.Backup),
                    file1.Access, "failed to keep access flags");
            }
        }

        public static void TestVolDirFull(AppHook appHook) {
            const int MAX_FILE_COUNT = 64;      // 2048 / 32
            using (IFileSystem fs = Make525Floppy("FILLER", appHook)) {
                IFileEntry volDir = fs.GetVolDirEntry();
                for (int i = 0; i < MAX_FILE_COUNT; i++) {
                    fs.CreateFile(volDir, "FILE_" + i, CreateMode.File);
                }

                try {
                    fs.CreateFile(volDir, "STRAW", CreateMode.File);
                    throw new Exception("created too many files");
                } catch (DiskFullException) { /*expected*/ }
            }
        }

        #region Utilities

        private static IFileSystem Make525Floppy(string volName, AppHook appHook) {
            return Helper.CreateTestImage(volName, FileSystemType.CPM, 280, appHook,
                out MemoryStream memFile);
        }

        #endregion Utilities
    }
}
