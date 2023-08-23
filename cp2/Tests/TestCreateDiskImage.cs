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
using System.Text;

namespace cp2.Tests {
    /// <summary>
    /// Tests "create-disk-image".
    /// </summary>
    internal static class TestCreateDiskImage {
        public static void RunTest(ParamsBag parms) {
            Controller.Stdout.WriteLine("  CreateDiskImage...");
            Controller.PrepareTestTmp(parms);

            // Confirm "list" fails if it can't find the filesystem.
            string checkFile = Path.Combine(Controller.TEST_TMP, "check-fail.do");
            if (!DiskUtil.HandleCreateDiskImage("cdi", new string[] { checkFile, "140k" }, parms)) {
                throw new Exception("cdi " + checkFile + " failed");
            }
            if (Catalog.HandleList("list", new string[] { checkFile }, parms)) {
                throw new Exception("list " + checkFile + " succeeded");
            }

            TestCreate(parms);

            Controller.RemoveTestTmp(parms);
        }

        // Args for disk images that are 16-sector or block-oriented.
        private static string[][] sTestArgs = new string[][] {
            new string[] { "140k-cpm.do", "140K", "cpm" },
            new string[] { "800k-cpm.po", "800K", "cpm" },

            new string[] { "140k-dos.do", "140KiB", "dos" },
            new string[] { "35trk-dos.do", "35tracks", "dos" },
            new string[] { "40trk-dos.do", "40trk", "dos" },
            new string[] { "50trk-dos.do", "50Trk", "dos" },
            new string[] { "80trk-dos.do", "80TRK", "dos" },

            new string[] { "400k-hfs.po", "400k", "hfs" },
            new string[] { "800k-hfs.po", "800k", "hfs" },
            new string[] { "32m-hfs.po", "32M", "hfs" },

            new string[] { "140k-pascal.do", "140KB", "pascal" },
            new string[] { "800k-pascal.po", "800KB", "pascal" },

            new string[] { "140k-prodos.po", "140k", "prodos" },
            new string[] { "35trk-prodos.po", "35trk", "prodos" },
            new string[] { "40trk-prodos.po", "40trk", "prodos" },
            new string[] { "800k-prodos.po", "800K", "prodos" },
            new string[] { "319m-prodos.po", "65535blk", "prodos" },
            new string[] { "32m-prodos.po", "32m", "prodos" },
        };

        // Create a bunch of disk images with different characteristics, and then verify that
        // we can open the images we just created.
        private static void TestCreate(ParamsBag parms) {
            string oldCurrentDir = Environment.CurrentDirectory;
            try {
                Environment.CurrentDirectory = Controller.TEST_TMP;

                foreach (string[] args in sTestArgs) {
                    if (!DiskUtil.HandleCreateDiskImage("cdi", args, parms)) {
                        throw new Exception("cdi " + args[0] + " " + args[1] + " " + args[2] +
                            " failed");
                    }
                    string[] catArgs = new string[] { args[0] };
                    if (!Catalog.HandleList("list", catArgs, parms)) {
                        throw new Exception("list " + catArgs[0] + " failed");
                    }
                }

                // 400KB DOS disk (50 tracks * 32 sectors)
                parms.Sectors = 32;
                string[] args32 = new string[] { "400k-dos.do", "400 kb", "dos" };
                if (!DiskUtil.HandleCreateDiskImage("cdi", args32, parms)) {
                    throw new Exception("cdi 32/400k failed");
                }
                // DOS 3.2 disk (35 tracks * 13 sectors)
                parms.Sectors = 13;
                string[] args13 = new string[] { "dos32.do", "35 tracks", "dos" };
                if (!DiskUtil.HandleCreateDiskImage("cdi", args13, parms)) {
                    throw new Exception("cdi 13/35trk failed");
                }
                parms.Sectors = 16;
            } finally {
                Environment.CurrentDirectory = oldCurrentDir;
            }
        }
    }
}
