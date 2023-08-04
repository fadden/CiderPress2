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
using DiskArc.Multi;
using static DiskArc.Defs;

namespace DiskArcTests {
    /// <summary>
    /// Test a simple MicroDrive image.
    /// </summary>
    public class TestMicroDrive_Prefab : ITest {
        public static void TestVarious(AppHook appHook) {
            using (Stream stream = Helper.OpenTestFile("microdrive/HD120MB.po", true, appHook)) {
                FileAnalyzer.AnalysisResult res = FileAnalyzer.Analyze(stream, ".po", appHook,
                    out FileKind kind, out SectorOrder orderHint);
                if (res != FileAnalyzer.AnalysisResult.Success ||
                        kind != FileKind.UnadornedSector) {
                    throw new Exception("Unexpected file format");
                }
                using IDiskImage? diskImage = FileAnalyzer.PrepareDiskImage(stream, kind, appHook);
                if (diskImage == null) {
                    throw new Exception("Unable to prepare image");
                }
                if (!diskImage.AnalyzeDisk(null, orderHint, IDiskImage.AnalysisDepth.Full)) {
                    throw new Exception("Unable to analyze disk");
                }
                MicroDrive? md = diskImage.Contents as MicroDrive;
                if (md == null) {
                    throw new Exception("Didn't find a MicroDrive");
                }

                // Should be 10 empty ProDOS partitions.
                if (md.Count != 10) {
                    throw new Exception("Didn't find 10 partitions");
                }
            }
        }
    }
}
