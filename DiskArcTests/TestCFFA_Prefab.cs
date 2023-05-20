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
using CommonUtil;
using DiskArc;
using DiskArc.Disk;
using DiskArc.FS;
using DiskArc.Multi;
using static DiskArc.Defs;

namespace DiskArcTests {
    /// <summary>
    /// Tests some CFFA images.
    /// </summary>
    /// <remarks>
    /// <para>We can generate the files ahead of time, but an image with four 32MB ProDOS
    /// partitions and one 1GB HFS partition is 1.1MB when compressed with gzip, and takes
    /// well over a second to unpack to a temp file on a fairly fast machine.  (FWIW, the file
    /// size can be reduced to 8KB by compressing the compressed output.)</para>
    /// <para>It's better to generate the files on the fly.  This allows us to test a wide
    /// variety of configurations efficiently, and add new configurations without having to
    /// cons a bunch of parts into a new file.</para>
    /// <para>Currently the slowest part of the experience is formatting a 1GB HFS volume.
    /// The PrepareTrees() function is slow (~400ms on the same machine).  This goes down to
    /// almost nothing if the temp file is kept in memory.</para>
    /// </remarks>
    public class TestCFFA_Prefab : ITest {
        private const uint SIZE_5MB = 5 * 1024 * 1024 / BLOCK_SIZE;
        private const uint SIZE_32MB = 32 * 1024 * 1024 / BLOCK_SIZE;
        private const uint SIZE_1GB = 1024 * 1024 * 1024 / BLOCK_SIZE;

        private class TestPart {
            public enum Fmt { Unknown = 0, Blank, ProDOS, HFS };

            public uint BlockCount { get; private set; }
            public Fmt Format { get; private set; }

            public TestPart(uint blockCount, Fmt format) {
                BlockCount = blockCount;
                Format = format;
            }
        }

        /// <summary>
        /// Test image definitions.
        /// </summary>
        private static TestPart[][] sTests = new TestPart[][] {
            // This is either a CFFA image with a single partition, or a simple unadorned
            // image with excess baggage.
            new TestPart[] {
                new TestPart(SIZE_32MB, TestPart.Fmt.ProDOS),
                new TestPart(SIZE_5MB, TestPart.Fmt.Blank),
            },
            // Two parts.
            new TestPart[] {
                new TestPart(SIZE_32MB, TestPart.Fmt.ProDOS),
                new TestPart(SIZE_5MB, TestPart.Fmt.ProDOS),
            },
            // Two parts, first is blank.
            new TestPart[] {
                new TestPart(SIZE_32MB, TestPart.Fmt.Blank),
                new TestPart(SIZE_5MB, TestPart.Fmt.ProDOS),
            },
            // Three parts, with some HFS thrown in.
            new TestPart[] {
                new TestPart(SIZE_32MB, TestPart.Fmt.ProDOS),
                new TestPart(SIZE_32MB, TestPart.Fmt.HFS),
                new TestPart(SIZE_5MB, TestPart.Fmt.ProDOS),
            },
            // Four parts, exact fit.
            new TestPart[] {
                new TestPart(SIZE_32MB, TestPart.Fmt.ProDOS),
                new TestPart(SIZE_32MB, TestPart.Fmt.Blank),
                new TestPart(SIZE_32MB, TestPart.Fmt.ProDOS),
                new TestPart(SIZE_32MB, TestPart.Fmt.HFS),
            },
            // Five parts, some extra space on the end to make it ambiguous as to whether the
            // last is a 1GB region or a pair of 32GB regions.
            // (This makes the original CiderPress unhappy.)
            new TestPart[] {
                new TestPart(SIZE_32MB, TestPart.Fmt.ProDOS),
                new TestPart(SIZE_32MB, TestPart.Fmt.HFS),
                new TestPart(SIZE_32MB, TestPart.Fmt.ProDOS),
                new TestPart(SIZE_32MB, TestPart.Fmt.HFS),
                new TestPart(SIZE_32MB, TestPart.Fmt.ProDOS),
                new TestPart(SIZE_5MB, TestPart.Fmt.Blank),
            },
            // Five parts, 5th is 1GB.
            new TestPart[] {
                new TestPart(SIZE_32MB, TestPart.Fmt.HFS),
                new TestPart(SIZE_32MB, TestPart.Fmt.ProDOS),
                new TestPart(SIZE_32MB, TestPart.Fmt.HFS),
                new TestPart(SIZE_32MB, TestPart.Fmt.ProDOS),
                new TestPart(SIZE_1GB, TestPart.Fmt.HFS),
            },
            // Six parts, 5th is 1GB, 6th is small.
            new TestPart[] {
                new TestPart(SIZE_32MB, TestPart.Fmt.ProDOS),
                new TestPart(SIZE_32MB, TestPart.Fmt.HFS),
                new TestPart(SIZE_32MB, TestPart.Fmt.ProDOS),
                new TestPart(SIZE_32MB, TestPart.Fmt.HFS),
                new TestPart(SIZE_1GB, TestPart.Fmt.ProDOS),    // 32MB volume in 1GB slot
                new TestPart(SIZE_5MB, TestPart.Fmt.HFS),
            },
            // Eight 32MB parts.
            new TestPart[] {
                new TestPart(SIZE_32MB, TestPart.Fmt.HFS),
                new TestPart(SIZE_32MB, TestPart.Fmt.ProDOS),
                new TestPart(SIZE_32MB, TestPart.Fmt.HFS),
                new TestPart(SIZE_32MB, TestPart.Fmt.ProDOS),
                new TestPart(SIZE_32MB, TestPart.Fmt.HFS),
                new TestPart(SIZE_32MB, TestPart.Fmt.ProDOS),
                new TestPart(SIZE_32MB, TestPart.Fmt.HFS),
                new TestPart(SIZE_32MB, TestPart.Fmt.ProDOS),
            },
        };

        public static void TestVarious(AppHook appHook) {
            // The temp file on disk is much slower, but if we keep it in memory we'll need
            // ~1.2GB of RAM.  If we're in 64-bit mode the RAM shouldn't be a problem.
            Stream tmpStream;
            if (Environment.Is64BitProcess) {
                tmpStream = new MemoryStream();
            } else {
                //appHook.LogD("Temp files are here: " + Path.GetTempFileName());
                tmpStream = TempFile.CreateTempFile();
            }

            //int debugCount = 0;
            double totalTimeMsec = 0;
            foreach (TestPart[] partList in sTests) {
                uint totalBlocks = 0;
                foreach (TestPart part in partList) {
                    totalBlocks += part.BlockCount;
                }

                // "If the stream is expanded, the contents of the stream between the old and
                // the new length are not defined."
                // Should be zeroed if it's part of the filesystem [citation needed], but if we
                // want the MemoryStream zeroed we'll need to do it ourselves.  Doing so negates
                // most of the performance advantage of keeping the temp file in RAM, at least on
                // a system with lots of RAM (big file cache) and M.2 storage.  We could probably
                // get away with just zeroing the first few blocks every 32MB to ensure we're
                // not getting a false-positive from a previous volume.
                tmpStream.SetLength(0);
                tmpStream.SetLength((long)totalBlocks * BLOCK_SIZE);
                if (tmpStream is MemoryStream) {
                    byte[] backing = ((MemoryStream)tmpStream).GetBuffer();
                    Array.Clear(backing);
                }
                IChunkAccess chunks = new GeneralChunkAccess(tmpStream, 0, totalBlocks);

                DateTime startWhen = DateTime.Now;
                uint startBlock = 0;
                for (int index = 0; index < partList.Length; index++) {
                    string volumeName = "Part" + (index + 1);
                    GeneratePart(partList[index], volumeName, chunks, startBlock, appHook);
                    startBlock += partList[index].BlockCount;
                }
                totalTimeMsec += (DateTime.Now - startWhen).TotalMilliseconds;

#if false
                {   // save a copy for verification
                    string fileName = @"C:\Src\CiderPress2\tmp\test" + debugCount + ".po";
                    using (Stream stream = new FileStream(fileName, FileMode.Create)) {
                        tmpStream.Position = 0;
                        tmpStream.CopyTo(stream);
                    }
                    debugCount++;
                }
#endif
                // Verify that the image has the expected number of partitions.
                using (IDiskImage? disk = FileAnalyzer.PrepareDiskImage(tmpStream,
                        FileKind.UnadornedSector, appHook)) {
                    if (disk == null) {
                        throw new Exception("Failed to prepare disk image");
                    }
                    disk.AnalyzeDisk();
                    if (disk.Contents is not CFFA) {
                        throw new Exception("Disk image is not CFFA");
                    }
                    IMultiPart parts = (CFFA)disk.Contents;
                    if (parts.Count != partList.Length) {
                        throw new Exception("Expected " + partList.Length + " partitions, found " +
                            parts.Count);
                    }
                }
            }

            appHook.LogI("Total generation time: " + totalTimeMsec + " ms");
            tmpStream.Close();
        }

        // Create a large empty file to verify that CFFA rejects it.
        public static void TestNot(AppHook appHook) {
            using Stream tmpStream = TempFile.CreateTempFile();
            tmpStream.SetLength(SIZE_32MB * BLOCK_SIZE * 5);
            using (IDiskImage? disk = FileAnalyzer.PrepareDiskImage(tmpStream,
                    FileKind.UnadornedSector, appHook)) {
                if (disk == null) {
                    throw new Exception("Failed to prepare disk image");
                }
                disk.AnalyzeDisk();
                if (disk.Contents is CFFA) {
                    throw new Exception("Disk was accepted as CFFA");
                }
            }
        }

        /// <summary>
        /// Generates the filesystem for one part.
        /// </summary>
        /// <param name="part">Part definition.</param>
        /// <param name="volumeName">Volume name for filesystem.</param>
        /// <param name="fileChunks">Chunk access for full file.</param>
        /// <param name="startBlock">Start block within the file.</param>
        /// <param name="appHook">Application hook reference.</param>
        private static void GeneratePart(TestPart part, string volumeName, IChunkAccess fileChunks,
                uint startBlock, AppHook appHook) {
            uint modBlockCount = part.BlockCount;

            // If we're asked to put ProDOS in a 1GB region, reduce the size to 32MB.  The rest
            // of the space will be wasted.
            if (part.Format == TestPart.Fmt.ProDOS && modBlockCount > 65536) {
                modBlockCount = 65536;
            }

            IChunkAccess partChunks = ChunkSubset.CreateBlockSubset(fileChunks, startBlock,
                modBlockCount);
            switch (part.Format) {
                case TestPart.Fmt.ProDOS:
                    using (IFileSystem fs = new ProDOS(partChunks, appHook)) {
                        fs.Format(volumeName, 0, true);
                    }
                    break;
                case TestPart.Fmt.HFS:
                    using (IFileSystem fs = new HFS(partChunks, appHook)) {
                        fs.Format(volumeName, 0, true);
                    }
                    break;
                case TestPart.Fmt.Blank:
                    break;
                default:
                    throw new NotImplementedException("Bad format: " + part.Format);
            }
        }
    }
}
