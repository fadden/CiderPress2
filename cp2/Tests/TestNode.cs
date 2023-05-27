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

using AppCommon;
using CommonUtil;
using DiskArc;
using DiskArc.Arc;
using DiskArc.Disk;
using DiskArc.FS;
using static DiskArc.Defs;
using static DiskArc.IFileSystem;

namespace cp2.Tests {
    /// <summary>
    /// Tests the DiskArcNode update mechanism.
    /// </summary>
    /// <remarks>
    /// This is primarily a test of the AppCommon infrastructure rather than a test of cp2
    /// application code, but it's convenient to do it here.  It also exercises ExtArchive.
    /// </remarks>
    internal static class TestNode {
        private const string NAME_ONE = "ONE";
        private const string NAME_TWO = "TWO";
        private const string NAME_THREE = "THREE";

        public static void RunTest(ParamsBag parms) {
            Controller.Stdout.WriteLine("  ExtArchive...");
            Controller.PrepareTestTmp(parms);

            //
            // Test six basic configurations, all on top of the host filesystem:
            // - file archive (ZIP)
            // - disk image (ProDOS on 5.25" WOZ)
            // - archive in disk image (NuFX on unadorned HFS)
            // - disk image in disk image (DOS nibble in 2IMG)
            // - disk image in archive (ProDOS on 3.5" WOZ in Binary II)
            // - archive in archive (NuFX in ZIP)
            //
            // It's also important to test disk-in-disk-in-arc, to ensure that disk-in-disk updates
            // don't get lazy.  Disks are just subsets of disk parents, so any changes propagate
            // automatically, but may require an explicit "flush" call.  Putting an archive on
            // top ensures that the sequence is calling upward.  We also need arc-disk-arc, to
            // verify that the disk "child archive was updated" code is also notifying the parent.
            // It's best to use a variety of formats, especially nibble formats like WOZ that cache
            // track data until explicitly flushed.
            //
            // In each case we want to create the file with the specified structure, with one
            // file in the deepest part.  We then do two consecutive updates, adding one more file
            // each time, then close the node and verify the file contents.
            //

            TestArc(parms);
            TestDisk(parms);
            TestArcInDisk(parms);
            TestDiskInDisk(parms);
            TestDiskInArc(parms);
            TestArcInArc(parms);

            TestArcInDiskInArc(parms);
            TestDiskInDiskInArcInArc(parms);

            TestArcInDiskOverflow(parms);

            // Give any finalizer-based checks a chance to run.
            GC.Collect();
            GC.WaitForPendingFinalizers();

            Controller.RemoveTestTmp(parms);
        }

        /// <summary>
        /// Runs tests with a ZIP archive.
        /// </summary>
        private static void TestArc(ParamsBag parms) {
            string fileName = Path.Combine(Controller.TEST_TMP, "Test.zip");
            using (FileStream file = new FileStream(fileName, FileMode.CreateNew)) {
                using (IArchive newArc = Zip.CreateArchive(parms.AppHook)) {
                    newArc.StartTransaction();
                    IFileEntry entry = newArc.CreateRecord();
                    entry.FileName = NAME_ONE;
                    SimplePartSource source = Controller.CreateSimpleSource(1024, 0x01);
                    newArc.AddPart(entry, FilePart.DataFork, source,
                        CompressionFormat.Uncompressed);
                    newArc.CommitTransaction(file);
                }
            }

            if (!ExtArchive.OpenExtArc(fileName, false, false, parms,
                    out DiskArcNode? rootNode, out DiskArcNode? leafNode, out object? leafObj,
                    out IFileEntry endDirEntry)) {
                throw new Exception("Failed to open " + fileName);
            }
            using (rootNode) {
                AddArchiveEntries((IArchive)leafObj, leafNode);
            }

            // Verify.
            using (FileStream file = new FileStream(fileName, FileMode.Open)) {
                using (IArchive checkArc = Zip.OpenArchive(file, parms.AppHook)) {
                    CheckEntries(checkArc.ToList());
                }
            }
        }

        /// <summary>
        /// Runs tests with a WOZ disk image.
        /// </summary>
        private static void TestDisk(ParamsBag parms) {
            string fileName = Path.Combine(Controller.TEST_TMP, "Test.woz");
            using (FileStream file = new FileStream(fileName, FileMode.CreateNew)) {
                SectorCodec codec =
                        StdSectorCodec.GetCodec(StdSectorCodec.CodecIndex525.Std_525_16);
                using (IDiskImage newDisk = Woz.CreateDisk525(file, 35, codec,
                        DEFAULT_525_VOLUME_NUM, parms.AppHook)) {
                    using (IFileSystem fs = new ProDOS(newDisk.ChunkAccess!, parms.AppHook)) {
                        fs.Format("NodeTest", 0, false);
                        fs.PrepareFileAccess(true);
                        IFileEntry volDir = fs.GetVolDirEntry();
                        IFileEntry oneEntry = fs.CreateFile(volDir, NAME_ONE, CreateMode.File);
                        using (Stream oneStream = fs.OpenFile(oneEntry, FileAccessMode.ReadWrite,
                                FilePart.DataFork)) {
                            Controller.WriteByteValue(oneStream, 1024, 0x01);
                        }
                    }
                }
            }

            if (!ExtArchive.OpenExtArc(fileName, false, false, parms,
                    out DiskArcNode? rootNode, out DiskArcNode? leafNode, out object? leafObj,
                    out IFileEntry endDirEntry)) {
                throw new Exception("Failed to open " + fileName);
            }
            using (rootNode) {
                AddDiskEntries((IDiskImage)leafObj, leafNode);
            }

            // Verify.
            using (FileStream file = new FileStream(fileName, FileMode.Open)) {
                using (IDiskImage checkDisk = Woz.OpenDisk(file, parms.AppHook)) {
                    checkDisk.AnalyzeDisk();
                    IFileSystem fs = (IFileSystem)checkDisk.Contents!;
                    fs.PrepareFileAccess(true);
                    IFileEntry volDir = fs.GetVolDirEntry();
                    CheckEntries(volDir.ToList());
                }
            }
        }

        /// <summary>
        /// Runs tests with a NuFX archive inside an unadorned HFS disk image.
        /// </summary>
        private static void TestArcInDisk(ParamsBag parms) {
            string fileName = Path.Combine(Controller.TEST_TMP, "Test.po");
            string arcName = "Archive.shk";
            using (FileStream file = new FileStream(fileName, FileMode.CreateNew)) {
                using (IDiskImage newDisk = UnadornedSector.CreateBlockImage(file, 1600,
                        parms.AppHook)) {
                    using (IFileSystem fs = new HFS(newDisk.ChunkAccess!, parms.AppHook)) {
                        fs.Format("NodeTest/ArcInDisk", 0, false);
                        fs.PrepareFileAccess(true);
                        IFileEntry volDir = fs.GetVolDirEntry();
                        IFileEntry arcEntry = fs.CreateFile(volDir, arcName, CreateMode.File);
                        using (Stream arcFile = fs.OpenFile(arcEntry, FileAccessMode.ReadWrite,
                                FilePart.DataFork)) {
                            using (IArchive newArc = NuFX.CreateArchive(parms.AppHook)) {
                                newArc.StartTransaction();
                                IFileEntry oneEntry = newArc.CreateRecord();
                                oneEntry.FileName = NAME_ONE;
                                SimplePartSource source = Controller.CreateSimpleSource(1024, 0x01);
                                newArc.AddPart(oneEntry, FilePart.DataFork, source,
                                    CompressionFormat.Uncompressed);
                                newArc.CommitTransaction(arcFile);
                            }
                        }
                    }
                }
            }

            string extArcName = fileName + ExtArchive.SPLIT_CHAR + arcName;

            if (!ExtArchive.OpenExtArc(extArcName, false, false, parms,
                    out DiskArcNode? rootNode, out DiskArcNode? leafNode, out object? leafObj,
                    out IFileEntry endDirEntry)) {
                throw new Exception("Failed to open " + extArcName);
            }
            using (rootNode) {
                AddArchiveEntries((IArchive)leafObj, leafNode);
            }

            // Verify.
            using (FileStream file = new FileStream(fileName, FileMode.Open)) {
                using (IDiskImage checkDisk = UnadornedSector.OpenDisk(file, parms.AppHook)) {
                    checkDisk.AnalyzeDisk();
                    IFileSystem fs = (IFileSystem)checkDisk.Contents!;
                    fs.PrepareFileAccess(true);
                    IFileEntry volDir = fs.GetVolDirEntry();
                    IFileEntry arcEntry = fs.FindFileEntry(volDir, arcName);
                    using (Stream arcStream = fs.OpenFile(arcEntry, FileAccessMode.ReadOnly,
                            FilePart.DataFork)) {
                        using (IArchive checkArc = NuFX.OpenArchive(arcStream, parms.AppHook)) {
                            CheckEntries(checkArc.ToList());
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Runs tests with a DOS nibble image inside a 2IMG disk image.
        /// </summary>
        private static void TestDiskInDisk(ParamsBag parms) {
            string fileName = Path.Combine(Controller.TEST_TMP, "Test.2img");
            string nibName = "dosdisk.nib";
            using (FileStream file = new FileStream(fileName, FileMode.CreateNew)) {
                using (IDiskImage twoDisk = TwoIMG.CreateProDOSBlockImage(file, 1600,
                        parms.AppHook)) {
                    using (IFileSystem twoFs = new HFS(twoDisk.ChunkAccess!, parms.AppHook)) {
                        twoFs.Format("NodeTest/DiskInDisk", 0, false);
                        twoFs.PrepareFileAccess(true);

                        // Create inner image.
                        SectorCodec codec =
                                StdSectorCodec.GetCodec(StdSectorCodec.CodecIndex525.Std_525_16);
                        using MemoryStream nibStream = new MemoryStream();
                        using (IDiskImage nibDisk = UnadornedNibble525.CreateDisk(nibStream,
                                codec, 252, parms.AppHook)) {
                            using (IFileSystem nibFs = new DOS(nibDisk.ChunkAccess!,
                                    parms.AppHook)) {
                                nibFs.Format(string.Empty, 252, false);
                                nibFs.PrepareFileAccess(true);
                                IFileEntry nibVolDir = nibFs.GetVolDirEntry();
                                IFileEntry oneEntry = nibFs.CreateFile(nibVolDir, NAME_ONE,
                                    CreateMode.File);
                                using (Stream oneStream = nibFs.OpenFile(oneEntry,
                                        FileAccessMode.ReadWrite, FilePart.DataFork)) {
                                    Controller.WriteByteValue(oneStream, 1024, 0x01);
                                }
                            }
                        }

                        IFileEntry volDir = twoFs.GetVolDirEntry();
                        IFileEntry nibEntry = twoFs.CreateFile(volDir, nibName, CreateMode.File);
                        using (Stream nibFileStream = twoFs.OpenFile(nibEntry,
                                FileAccessMode.ReadWrite, FilePart.DataFork)) {
                            nibStream.Position = 0;
                            nibStream.CopyTo(nibFileStream);
                        }
                    }
                }
            }

            string extArcName = fileName + ExtArchive.SPLIT_CHAR + nibName;

            if (!ExtArchive.OpenExtArc(extArcName, false, false, parms,
                    out DiskArcNode? rootNode, out DiskArcNode? leafNode, out object? leafObj,
                    out IFileEntry endDirEntry)) {
                throw new Exception("Failed to open " + extArcName);
            }
            using (rootNode) {
                AddDiskEntries((IDiskImage)leafObj, leafNode);
            }

            // Verify.
            using (FileStream file = new FileStream(fileName, FileMode.Open)) {
                using (IDiskImage checkDisk = TwoIMG.OpenDisk(file, parms.AppHook)) {
                    checkDisk.AnalyzeDisk();
                    IFileSystem fs = (IFileSystem)checkDisk.Contents!;
                    fs.PrepareFileAccess(true);
                    IFileEntry volDir = fs.GetVolDirEntry();
                    IFileEntry nibEntry = fs.FindFileEntry(volDir, nibName);
                    using (Stream nibStream = fs.OpenFile(nibEntry, FileAccessMode.ReadOnly,
                            FilePart.DataFork)) {
                        using (IDiskImage nibDisk = UnadornedNibble525.OpenDisk(nibStream,
                                parms.AppHook)) {
                            nibDisk.AnalyzeDisk();
                            IFileSystem nibFs = (IFileSystem)nibDisk.Contents!;
                            nibFs.PrepareFileAccess(true);
                            IFileEntry nibVolDir = nibFs.GetVolDirEntry();
                            CheckEntries(nibVolDir.ToList());
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Runs tests with ProDOS on a 3.5" WOZ disk image in a Binary II archive.
        /// </summary>
        private static void TestDiskInArc(ParamsBag parms) {
            string fileName = Path.Combine(Controller.TEST_TMP, "Test.bqy");
            string diskName = "DISK.WOZ";
            using (FileStream file = new FileStream(fileName, FileMode.CreateNew)) {
                using (IArchive newArc = Binary2.CreateArchive(parms.AppHook)) {
                    SectorCodec codec = StdSectorCodec.GetCodec(StdSectorCodec.CodecIndex35.Std_35);
                    MemoryStream tmpStream = new MemoryStream();
                    using (IDiskImage newDisk = Woz.CreateDisk35(tmpStream, MediaKind.GCR_DSDD35,
                            4, codec, parms.AppHook)) {
                        using (IFileSystem fs = new ProDOS(newDisk.ChunkAccess!, parms.AppHook)) {
                            fs.Format("SubNodeTest", 0, false);
                            fs.PrepareFileAccess(true);
                            IFileEntry volDir = fs.GetVolDirEntry();
                            IFileEntry oneEntry = fs.CreateFile(volDir, NAME_ONE, CreateMode.File);
                            using (Stream oneStream = fs.OpenFile(oneEntry,
                                    FileAccessMode.ReadWrite, FilePart.DataFork)) {
                                Controller.WriteByteValue(oneStream, 1024, 0x01);
                            }
                        }
                    }
                    newArc.StartTransaction();
                    IFileEntry entry = newArc.CreateRecord();
                    entry.FileName = diskName;
                    SimplePartSource source = new SimplePartSource(tmpStream);
                    newArc.AddPart(entry, FilePart.DataFork, source, CompressionFormat.Default);
                    newArc.CommitTransaction(file);
                }
            }

            string extArcName = fileName + ExtArchive.SPLIT_CHAR + diskName;

            if (!ExtArchive.OpenExtArc(extArcName, false, false, parms,
                    out DiskArcNode? rootNode, out DiskArcNode? leafNode, out object? leafObj,
                    out IFileEntry endDirEntry)) {
                throw new Exception("Failed to open " + extArcName);
            }
            using (rootNode) {
                AddDiskEntries((IDiskImage)leafObj, leafNode);
            }

            // Verify.
            using (FileStream file = new FileStream(fileName, FileMode.Open)) {
                using (IArchive checkArc = Binary2.OpenArchive(file, parms.AppHook)) {
                    IFileEntry diskEntry = checkArc.FindFileEntry(diskName);
                    MemoryStream tmpStream = new MemoryStream();
                    using (Stream diskStream = checkArc.OpenPart(diskEntry, FilePart.DataFork)) {
                        diskStream.CopyTo(tmpStream);
                    }
                    using (IDiskImage checkDisk = Woz.OpenDisk(tmpStream, parms.AppHook)) {
                        checkDisk.AnalyzeDisk();
                        IFileSystem fs = (IFileSystem)checkDisk.Contents!;
                        fs.PrepareFileAccess(true);
                        IFileEntry volDir = fs.GetVolDirEntry();
                        CheckEntries(volDir.ToList());
                    }
                }
            }
        }

        /// <summary>
        /// Runs tests with NuFX in a ZIP archive.
        /// </summary>
        private static void TestArcInArc(ParamsBag parms) {
            string fileName = Path.Combine(Controller.TEST_TMP, "shk-in.zip");
            string nufxName = "archive.shk";
            using (FileStream file = new FileStream(fileName, FileMode.CreateNew)) {
                using (IArchive zipArc = Zip.CreateArchive(parms.AppHook)) {
                    MemoryStream nufxStream = new MemoryStream();
                    using (IArchive nufxArc = NuFX.CreateArchive(parms.AppHook)) {
                        nufxArc.StartTransaction();
                        IFileEntry oneEntry = nufxArc.CreateRecord();
                        oneEntry.FileName = NAME_ONE;
                        SimplePartSource nufxSource = Controller.CreateSimpleSource(1024, 0x01);
                        nufxArc.AddPart(oneEntry, FilePart.DataFork, nufxSource,
                            CompressionFormat.Uncompressed);
                        nufxArc.CommitTransaction(nufxStream);
                    }
                    zipArc.StartTransaction();
                    IFileEntry entry = zipArc.CreateRecord();
                    entry.FileName = nufxName;
                    SimplePartSource source = new SimplePartSource(nufxStream);
                    zipArc.AddPart(entry, FilePart.DataFork, source, CompressionFormat.Default);
                    zipArc.CommitTransaction(file);
                }
            }

            string extArcName = fileName + ExtArchive.SPLIT_CHAR + nufxName;

            if (!ExtArchive.OpenExtArc(extArcName, false, false, parms,
                    out DiskArcNode? rootNode, out DiskArcNode? leafNode, out object? leafObj,
                    out IFileEntry endDirEntry)) {
                throw new Exception("Failed to open " + extArcName);
            }
            using (rootNode) {
                AddArchiveEntries((IArchive)leafObj, leafNode);
            }

            // Verify.
            using (FileStream file = new FileStream(fileName, FileMode.Open)) {
                using (IArchive zipArc = Zip.OpenArchive(file, parms.AppHook)) {
                    IFileEntry diskEntry = zipArc.FindFileEntry(nufxName);
                    MemoryStream tmpStream = new MemoryStream();
                    using (Stream nufxStream = zipArc.OpenPart(diskEntry, FilePart.DataFork)) {
                        nufxStream.CopyTo(tmpStream);
                    }
                    using (IArchive nufxArc = NuFX.OpenArchive(tmpStream, parms.AppHook)) {
                        CheckEntries(nufxArc.ToList());
                    }
                }
            }
        }

        /// <summary>
        /// Runs tests with NuFX in a WOZ in a ZIP.
        /// </summary>
        private static void TestArcInDiskInArc(ParamsBag parms) {
            const string d800name = "test800.woz";
            const string nufxName = "inner.shk";
            string fileName = Path.Combine(Controller.TEST_TMP, "ArcInDiskIn.zip");

            using (FileStream file = new FileStream(fileName, FileMode.CreateNew)) {
                using (IArchive zipArc = Zip.CreateArchive(parms.AppHook)) {
                    using MemoryStream d800stream = new MemoryStream();
                    SectorCodec codec = StdSectorCodec.GetCodec(StdSectorCodec.CodecIndex35.Std_35);
                    using (IDiskImage d800 = Woz.CreateDisk35(d800stream, MediaKind.GCR_DSDD35,
                            4, codec, parms.AppHook)) {
                        using (IFileSystem fs = new ProDOS(d800.ChunkAccess!, parms.AppHook)) {
                            fs.Format("Deep800", 0, false);
                            fs.PrepareFileAccess(true);
                            IFileEntry volDir = fs.GetVolDirEntry();
                            IFileEntry arcEntry = fs.CreateFile(volDir, nufxName, CreateMode.File);
                            using (Stream arcFile = fs.OpenFile(arcEntry, FileAccessMode.ReadWrite,
                                    FilePart.DataFork)) {
                                using (IArchive newArc = NuFX.CreateArchive(parms.AppHook)) {
                                    newArc.StartTransaction();
                                    IFileEntry oneEntry = newArc.CreateRecord();
                                    oneEntry.FileName = NAME_ONE;
                                    SimplePartSource source =
                                        Controller.CreateSimpleSource(1024, 0x01);
                                    newArc.AddPart(oneEntry, FilePart.DataFork, source,
                                        CompressionFormat.Uncompressed);
                                    newArc.CommitTransaction(arcFile);
                                }
                            }
                        }
                    }
                    // Put the 800K disk in the ZIP archive.
                    zipArc.StartTransaction();
                    IFileEntry zipEntry = zipArc.CreateRecord();
                    zipEntry.FileName = d800name;
                    IPartSource zipSource = new SimplePartSource(d800stream);
                    zipArc.AddPart(zipEntry, FilePart.DataFork, zipSource,
                        CompressionFormat.Uncompressed);
                    zipArc.CommitTransaction(file);
                }
            }

            // Add a couple of files at the deepest level.

            string extArcName = fileName +
                ExtArchive.SPLIT_CHAR + d800name +
                ExtArchive.SPLIT_CHAR + nufxName;

            if (!ExtArchive.OpenExtArc(extArcName, false, false, parms,
                    out DiskArcNode? rootNode, out DiskArcNode? leafNode, out object? leafObj,
                    out IFileEntry endDirEntry)) {
                throw new Exception("Failed to open " + extArcName);
            }

            using (rootNode) {
                AddArchiveEntries((IArchive)leafObj, leafNode);
            }

            // Verify.
            using (FileStream file = new FileStream(fileName, FileMode.Open)) {
                // Extract 800K WOZ.
                MemoryStream d800stream = new MemoryStream();
                using (IArchive zipArc = Zip.OpenArchive(file, parms.AppHook)) {
                    IFileEntry diskEntry = zipArc.FindFileEntry(d800name);
                    using (Stream diskStream = zipArc.OpenPart(diskEntry, FilePart.DataFork)) {
                        diskStream.CopyTo(d800stream);
                    }
                }

                using (IDiskImage check800 = Woz.OpenDisk(d800stream, parms.AppHook)) {
                    check800.AnalyzeDisk();
                    IFileSystem fs800 = (IFileSystem)check800.Contents!;
                    fs800.PrepareFileAccess(true);
                    IFileEntry vol800 = fs800.GetVolDirEntry();
                    IFileEntry nufxEntry = fs800.FindFileEntry(vol800, nufxName);
                    using (Stream nufxStream = fs800.OpenFile(nufxEntry, FileAccessMode.ReadOnly,
                            FilePart.DataFork)) {
                        using (IArchive checkArc = NuFX.OpenArchive(nufxStream, parms.AppHook)) {
                            CheckEntries(checkArc.ToList());
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Runs tests with a 140K disk in an 800K disk in a ZIP in a NuFX.
        /// </summary>
        private static void TestDiskInDiskInArcInArc(ParamsBag parms) {
            const string zipName = "test.zip";
            const string d800name = "test800.po";
            const string d140name = "test140.po";
            string fileName = Path.Combine(Controller.TEST_TMP, "DeepTest.shk");

            using (FileStream file = new FileStream(fileName, FileMode.CreateNew)) {
                using (IArchive nufxArc = NuFX.CreateArchive(parms.AppHook)) {
                    using MemoryStream zipStream = new MemoryStream();
                    using (IArchive zipArc = Zip.CreateArchive(parms.AppHook)) {
                        using MemoryStream d800stream = new MemoryStream();
                        using (IDiskImage d800 = UnadornedSector.CreateBlockImage(d800stream,
                                1600, parms.AppHook)) {
                            using MemoryStream d140stream = new MemoryStream();
                            using (IDiskImage d140 = UnadornedSector.CreateBlockImage(d140stream,
                                    280, parms.AppHook)) {
                                // Put a file on the 140K disk.
                                using (IFileSystem fs = new ProDOS(d140.ChunkAccess!,
                                        parms.AppHook)) {
                                    fs.Format("Deep140", 0, false);
                                    fs.PrepareFileAccess(true);
                                    IFileEntry volDir = fs.GetVolDirEntry();
                                    IFileEntry oneEntry = fs.CreateFile(volDir, NAME_ONE,
                                        CreateMode.File);
                                    using (Stream oneStream = fs.OpenFile(oneEntry,
                                            FileAccessMode.ReadWrite, FilePart.DataFork)) {
                                        Controller.WriteByteValue(oneStream, 1024, 0x01);
                                    }
                                }
                            }
                            // Put the 140K disk on the 800K disk.
                            using (IFileSystem fs = new ProDOS(d800.ChunkAccess!, parms.AppHook)) {
                                fs.Format("Deep800", 0, false);
                                fs.PrepareFileAccess(true);
                                IFileEntry volDir = fs.GetVolDirEntry();
                                IFileEntry diskEntry = fs.CreateFile(volDir, d140name,
                                    CreateMode.File);
                                using (Stream oneStream = fs.OpenFile(diskEntry,
                                        FileAccessMode.ReadWrite, FilePart.DataFork)) {
                                    d140stream.Position = 0;
                                    d140stream.CopyTo(oneStream);
                                }
                            }
                        }
                        // Put the 800K disk in the ZIP archive.
                        zipArc.StartTransaction();
                        IFileEntry zipEntry = zipArc.CreateRecord();
                        zipEntry.FileName = d800name;
                        IPartSource zipSource = new SimplePartSource(d800stream);
                        zipArc.AddPart(zipEntry, FilePart.DataFork, zipSource,
                            CompressionFormat.Uncompressed);
                        zipArc.CommitTransaction(zipStream);
                    }
                    // Put the ZIP archive in the NuFX archive.
                    nufxArc.StartTransaction();
                    IFileEntry nufxEntry = nufxArc.CreateRecord();
                    nufxEntry.FileName = zipName;
                    IPartSource source = new SimplePartSource(zipStream);
                    nufxArc.AddPart(nufxEntry, FilePart.DataFork, source,
                        CompressionFormat.Uncompressed);
                    nufxArc.CommitTransaction(file);    // commit to disk file
                }
            }

            // Add a couple of files at the deepest level.

            string extArcName = fileName +
                ExtArchive.SPLIT_CHAR + zipName +
                ExtArchive.SPLIT_CHAR + d800name +
                ExtArchive.SPLIT_CHAR + d140name;

            if (!ExtArchive.OpenExtArc(extArcName, false, false, parms,
                    out DiskArcNode? rootNode, out DiskArcNode? leafNode, out object? leafObj,
                    out IFileEntry endDirEntry)) {
                throw new Exception("Failed to open " + extArcName);
            }

            using (rootNode) {
                AddDiskEntries((IDiskImage)leafObj, leafNode);
            }

            // Verify.
            using (FileStream file = new FileStream(fileName, FileMode.Open)) {
                // Extract ZIP.
                MemoryStream zipStream = new MemoryStream();
                using (IArchive checkArc = NuFX.OpenArchive(file, parms.AppHook)) {
                    IFileEntry diskEntry = checkArc.FindFileEntry(zipName);
                    using (Stream diskStream = checkArc.OpenPart(diskEntry, FilePart.DataFork)) {
                        diskStream.CopyTo(zipStream);
                    }
                }
                // Extract 800K disk.
                MemoryStream d800stream = new MemoryStream();
                using (IArchive checkArc = Zip.OpenArchive(zipStream, parms.AppHook)) {
                    IFileEntry diskEntry = checkArc.FindFileEntry(d800name);
                    using (Stream diskStream = checkArc.OpenPart(diskEntry, FilePart.DataFork)) {
                        diskStream.CopyTo(d800stream);
                    }
                }

                using (IDiskImage check800 = UnadornedSector.OpenDisk(d800stream, parms.AppHook)) {
                    check800.AnalyzeDisk();
                    IFileSystem fs800 = (IFileSystem)check800.Contents!;
                    fs800.PrepareFileAccess(true);
                    IFileEntry vol800 = fs800.GetVolDirEntry();
                    IFileEntry d140entry = fs800.FindFileEntry(vol800, d140name);
                    using (Stream st140 = fs800.OpenFile(d140entry, FileAccessMode.ReadOnly,
                            FilePart.DataFork)) {
                        using (IDiskImage check140 = UnadornedSector.OpenDisk(st140,
                                parms.AppHook)) {
                            check140.AnalyzeDisk();
                            IFileSystem fs140 = (IFileSystem)check140.Contents!;
                            fs140.PrepareFileAccess(true);
                            IFileEntry vol140 = fs140.GetVolDirEntry();
                            CheckEntries(vol140.ToList());
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Runs tests with a ZIP archive inside an unadorned and undersized DOS disk image.
        /// </summary>
        private static void TestArcInDiskOverflow(ParamsBag parms) {
            string fileName = Path.Combine(Controller.TEST_TMP, "Undersize.do");
            string arcName = "Archive.ZIP";
            long freeSpace;
            using (FileStream file = new FileStream(fileName, FileMode.CreateNew)) {
                using (IDiskImage newDisk = UnadornedSector.CreateSectorImage(file, 35, 16,
                        SectorOrder.DOS_Sector, parms.AppHook)) {
                    using (IFileSystem fs = new DOS(newDisk.ChunkAccess!, parms.AppHook)) {
                        fs.Format(string.Empty, 254, true);
                        fs.PrepareFileAccess(true);
                        IFileEntry volDir = fs.GetVolDirEntry();
                        IFileEntry arcEntry = fs.CreateFile(volDir, arcName, CreateMode.File);
                        using (Stream arcFile = fs.OpenFile(arcEntry, FileAccessMode.ReadWrite,
                                FilePart.DataFork)) {
                            using (IArchive newArc = Zip.CreateArchive(parms.AppHook)) {
                                newArc.StartTransaction();
                                IFileEntry oneEntry = newArc.CreateRecord();
                                oneEntry.FileName = NAME_ONE;
                                SimplePartSource source =
                                    Controller.CreateRandomSource(80 * 1024, 0);
                                newArc.AddPart(oneEntry, FilePart.DataFork, source,
                                    CompressionFormat.Uncompressed);
                                newArc.CommitTransaction(arcFile);
                            }
                        }
                        freeSpace = fs.FreeSpace;
                    }
                }
            }

            string extArcName = fileName + ExtArchive.SPLIT_CHAR + arcName;

            if (!ExtArchive.OpenExtArc(extArcName, false, false, parms,
                    out DiskArcNode? rootNode, out DiskArcNode? leafNode, out object? leafObj,
                    out IFileEntry endDirEntry)) {
                throw new Exception("Failed to open " + extArcName);
            }
            using (rootNode) {
                IArchive arc = (IArchive)leafObj;
                arc.StartTransaction();
                IFileEntry entry = arc.CreateRecord();
                entry.FileName = NAME_TWO;
                SimplePartSource source = Controller.CreateRandomSource(80 * 1024, 0);
                arc.AddPart(entry, FilePart.DataFork, source, CompressionFormat.Uncompressed);
                // Save updates.
                try {
                    leafNode.SaveUpdates(false);
                    throw new Exception("Overflow update succeeded");
                } catch (IOException ex) {
                    /*expected*/
                    Debug.WriteLine("Got exception for overflow: " + ex.Message);
                }
                leafNode.CheckHealth();
            }

            // Verify.
            using (FileStream file = new FileStream(fileName, FileMode.Open)) {
                using (IDiskImage checkDisk = UnadornedSector.OpenDisk(file, parms.AppHook)) {
                    checkDisk.AnalyzeDisk();
                    IFileSystem fs = (IFileSystem)checkDisk.Contents!;
                    fs.PrepareFileAccess(true);
                    if (fs.Notes.WarningCount != 0 || fs.Notes.ErrorCount != 0) {
                        throw new Exception("Something went wrong with filesystem");
                    }
                    // Verify no change in space used in filesystem.
                    if (fs.FreeSpace != freeSpace) {
                        throw new Exception("Free space changed: was " + freeSpace + ", now " +
                            fs.FreeSpace);
                    }
                    IFileEntry volDir = fs.GetVolDirEntry();
                    IFileEntry arcEntry = fs.FindFileEntry(volDir, arcName);
                    using (Stream arcStream = fs.OpenFile(arcEntry, FileAccessMode.ReadOnly,
                            FilePart.DataFork)) {
                        using (IArchive checkArc = Zip.OpenArchive(arcStream, parms.AppHook)) {
                            int count = checkArc.ToList().Count;
                            if (count != 1) {
                                throw new Exception("Unexpected number of files: " + count);
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Adds entries TWO and THREE to the specified leaf node.
        /// </summary>
        private static void AddDiskEntries(IDiskImage disk, DiskArcNode leafNode) {
            leafNode.CheckHealth();

            IFileSystem fs = (IFileSystem)disk.Contents!;   // disk image already analyzed
            fs.PrepareFileAccess(true);
            IFileEntry volDir = fs.GetVolDirEntry();
            // Create a file.
            IFileEntry twoEntry = fs.CreateFile(volDir, NAME_TWO, CreateMode.File);
            using (Stream twoStream = fs.OpenFile(twoEntry, FileAccessMode.ReadWrite,
                    FilePart.DataFork)) {
                Controller.WriteByteValue(twoStream, 2048, 0x02);
            }
            // Save updates.
            try {
                leafNode.SaveUpdates(false);
            } catch (Exception ex) {
                Debug.WriteLine("SaveUpdates failed: " + ex);
                throw;
            }
            leafNode.CheckHealth();

            // Create another file.
            IFileEntry threeEntry = fs.CreateFile(volDir, NAME_THREE, CreateMode.File);
            using (Stream threeStream = fs.OpenFile(threeEntry, FileAccessMode.ReadWrite,
                    FilePart.DataFork)) {
                Controller.WriteByteValue(threeStream, 3072, 0x03);
            }
            // Save updates.
            try {
                leafNode.SaveUpdates(false);
            } catch (Exception ex) {
                Debug.WriteLine("SaveUpdates failed: " + ex);
                throw;
            }

            leafNode.CheckHealth();
        }

        /// <summary>
        /// Adds entries TWO and THREE to the specified leaf node.
        /// </summary>
        private static void AddArchiveEntries(IArchive arc, DiskArcNode leafNode) {
            leafNode.CheckHealth();

            // Add a file.
            arc.StartTransaction();
            IFileEntry entry = arc.CreateRecord();
            entry.FileName = NAME_TWO;
            SimplePartSource source = Controller.CreateSimpleSource(2048, 0x02);
            arc.AddPart(entry, FilePart.DataFork, source, CompressionFormat.Uncompressed);
            // Save updates.
            try {
                leafNode.SaveUpdates(false);
            } catch (Exception ex) {
                Debug.WriteLine("SaveUpdates failed: " + ex);
                throw;
            }
            leafNode.CheckHealth();

            // Add a second file.
            arc.StartTransaction();
            entry = arc.CreateRecord();
            entry.FileName = NAME_THREE;
            source = Controller.CreateSimpleSource(3072, 0x03);
            arc.AddPart(entry, FilePart.DataFork, source, CompressionFormat.Uncompressed);
            // Save updates.
            try {
                leafNode.SaveUpdates(false);
            } catch (Exception ex) {
                Debug.WriteLine("SaveUpdates failed: " + ex);
                throw;
            }

            leafNode.CheckHealth();
        }

        /// <summary>
        /// Verifies that entries ONE, TWO, and THREE are correct.
        /// </summary>
        private static void CheckEntries(List<IFileEntry> entries) {
            // Currently just checking name and length.  Shouldn't be necessary to check
            // the byte values.
            if (entries.Count != 3) {
                throw new Exception("Incorrect number of entries");
            }
            if (entries[0].FileName != NAME_ONE || entries[0].DataLength != 1024) {
                throw new Exception("Bad entry #1");
            }
            if (entries[1].FileName != NAME_TWO || entries[1].DataLength != 2048) {
                throw new Exception("Bad entry #2");
            }
            if (entries[2].FileName != NAME_THREE || entries[2].DataLength != 3072) {
                throw new Exception("Bad entry #3");
            }
        }
    }
}
