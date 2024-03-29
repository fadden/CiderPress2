﻿/*
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
    /// Test some Trackstar disk images created by original hardware or by other programs.
    /// </summary>
    public class TestTrackstar_Prefab : ITest {
        public static void TestVarious(AppHook appHook) {
            Helper.SimpleDiskCheck("trackstar/DOS33MAS.APP", FileKind.Trackstar, 19, appHook);
        }

        public static void TestMeta(AppHook appHook) {
            using (Stream dataFile = Helper.OpenTestFile("trackstar/DOS33MAS.APP", true, appHook)) {
                using (Trackstar disk = Trackstar.OpenDisk(dataFile, appHook)) {
                    string? desc = disk.GetMetaValue(Trackstar.DESCRIPTION_NAME, false);
                    Helper.ExpectString("Apple DOS 3.3 System Master - January 1, 1983",
                        desc, "incorrect metadata");
                }
            }
        }
    }
}
