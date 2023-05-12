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
    /// Test some 2IMG disk images created by other programs.
    /// </summary>
    public class TestTwoIMG_Prefab : ITest {
        // Simple test to open and scan a few disk images.
        public static void TestVarious(AppHook appHook) {
            Helper.SimpleDiskCheck("2img/dos33-disk.2mg", FileKind.TwoIMG, 1, appHook);
            Helper.SimpleDiskCheck("2img/nonsense.2mg", FileKind.TwoIMG, 5, appHook);
            Helper.SimpleDiskCheck("2img/prodos-disk.2mg", FileKind.TwoIMG, 2, appHook);
            Helper.SimpleDiskCheck("2img/dos32-nib-disk.2mg", FileKind.TwoIMG, 14, appHook);
        }

        // Confirm that we can read metadata set by other programs.
        public static void TestMeta(AppHook appHook) {
            using (Stream dataFile = Helper.OpenTestFile("2img/prodos-disk.2mg", true, appHook)) {
                using (TwoIMG disk = TwoIMG.OpenDisk(dataFile, appHook)) {
                    // Confirm we read what we expect.
                    if (disk.Creator != "CdrP") {
                        throw new Exception("Test image wrong creator");
                    }
                    if (disk.ImageFormat != 1) {
                        throw new Exception("Test image wrong image format");
                    }
                    if (!disk.WriteProtected) {
                        throw new Exception("Test image not write-protected");
                    }
                    if (disk.VolumeNumber != 200) {
                        throw new Exception("Test image had incorrect volume number");
                    }
                    if (disk.Comment.Length < 50 || disk.RawComment.Length < 50) {
                        throw new Exception("Test image missing comment");
                    }

                    // Make sure the read-only test works.
                    try {
                        disk.VolumeNumber = 100;
                        throw new Exception("Changed vol num on read-only disk");
                    } catch (IOException) { /*expected*/ }
                    try {
                        disk.Comment = "hello";
                        throw new Exception("Changed comment on read-only disk");
                    } catch (IOException) { /*expected*/ }
                }
            }

            using (Stream dataFile = Helper.OpenTestFile("2img/dos33-disk.2mg", true, appHook)) {
                using (TwoIMG disk = TwoIMG.OpenDisk(dataFile, appHook)) {
                    // These should be defaults.
                    if (disk.Creator != "CdrP") {
                        throw new Exception("Test image wrong creator");
                    }
                    if (disk.ImageFormat != 0) {
                        throw new Exception("Test image wrong image format");
                    }
                    if (disk.WriteProtected) {
                        throw new Exception("Test image is write-protected");
                    }
                    if (disk.VolumeNumber != -1) {
                        throw new Exception("Test image volume number set");
                    }
                    if (disk.Comment.Length != 0 || disk.RawComment.Length != 0) {
                        throw new Exception("Test image has comment");
                    }
                }
            }
        }
    }
}
