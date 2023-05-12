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
using DiskArc.Multi;
using static DiskArc.Defs;

namespace DiskArcTests {
    /// <summary>
    /// Exercise APM partitioned images.
    /// </summary>
    public class TestAPM_Prefab : ITest {
        // Confirm that we can process an APM file.
        public static void TestSimple(AppHook appHook) {
            using (Stream dataFile = Helper.OpenTestFile("apm/apm-prodos-hfs.iso", true, appHook)) {
                using (IDiskImage diskImage = FileAnalyzer.PrepareDiskImage(dataFile,
                        FileKind.UnadornedSector, appHook)!) {
                    diskImage.AnalyzeDisk();
                    if (diskImage.Contents is not IMultiPart) {
                        throw new Exception("Unable to locate partitions");
                    }
                    Helper.CheckNotes(diskImage, 0, 0);

                    IMultiPart parts = (IMultiPart)diskImage.Contents;
                    for (int i = 0; i < parts.Count; i++) {
                        Partition part = parts[i];

                        string nameStr = ((APM_Partition)part).PartitionName;
                        string typeStr = ((APM_Partition)part).PartitionType;
                        switch (nameStr) {
                            case "ProDOS_Part":
                                if (typeStr != "Apple_PRODOS") {
                                    throw new Exception("Unexpected type");
                                }
                                part.AnalyzePartition();
                                IFileSystem fs1 = part.FileSystem!;
                                fs1.PrepareFileAccess(true);
                                // One warning, for DOS MASTER embedded volume.
                                Helper.CheckNotes(fs1, 1, 0);
                                IFileEntry rootDir1 = fs1.GetVolDirEntry();
                                if (rootDir1.ChildCount != 3) {
                                    throw new Exception("Unexpected number of files");
                                }
                                break;
                            case "HFS_Part":
                                if (typeStr != "Apple_HFS") {
                                    throw new Exception("Unexpected type");
                                }
                                part.AnalyzePartition();
                                IFileSystem fs2 = part.FileSystem!;
                                fs2.PrepareFileAccess(true);
                                Helper.CheckNotes(fs2, 0, 0);
                                IFileEntry rootDir2 = fs2.GetVolDirEntry();
                                if (rootDir2.ChildCount != 1) {
                                    throw new Exception("Unexpected number of files");
                                }
                                break;
                            case "Apple":
                                if (typeStr != "Apple_partition_map") {
                                    throw new Exception("Unexpected type");
                                }
                                if (part.Length != 3 * BLOCK_SIZE) {
                                    throw new Exception("Unexpected partition map length");
                                }
                                break;
                            default:
                                throw new Exception("Unexpected partition");
                        }
                    }
                }
            }
        }
    }
}
