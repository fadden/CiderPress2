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

using CommonUtil;
using DiskArc;
using DiskArc.Disk;
using static DiskArc.Defs;

namespace DiskArcTests {
    /// <summary>
    /// Test some DiskCopy disk images created by other programs.
    /// </summary>
    public class TestDiskCopy_Prefab : ITest {
        public static void TestVarious(AppHook appHook) {
            Helper.SimpleDiskCheck("diskcopy/Installer Disk 1.image",
                FileKind.DiskCopy, 8, appHook);
        }

        public static void TestMeta(AppHook appHook) {
            using (Stream dataFile = Helper.OpenTestFile("diskcopy/Installer Disk 1.image", true,
                    appHook)) {
                using (DiskCopy disk = DiskCopy.OpenDisk(dataFile, appHook)) {
                    string? desc = disk.GetMetaValue(DiskCopy.DESCRIPTION_NAME, false);
                    Helper.ExpectString("Installer Disk 1", desc, "incorrect metadata");
                }
            }
        }
    }
}
