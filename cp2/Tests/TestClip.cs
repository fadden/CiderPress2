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
using System.Text;

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
    /// Test clipboard copy/paste infrastructure.  This does not actually use the system clipboard.
    /// </summary>
    /// <remarks>
    /// This is primarily a test of the AppCommon infrastructure rather than a test of cp2
    /// application code, but it's convenient to do it here.
    /// </remarks>
    internal static class TestClip {
        public static void RunTest(ParamsBag parms) {
            Controller.Stdout.WriteLine("  Clip...");
            Controller.PrepareTestTmp(parms);

            //
            // Basic plan:
            // - Use ClipFileSet to generate a list of ClipFileEntry objects.
            // - Simple test: compare the generated set to a set of expected results to confirm
            //   that the generation is working.
            // - Full test: use ClipFileWorker to add the entries to a different archive or to
            //   extract to the host filesystem.  A custom stream generator is used in place of the
            //   system clipboard mechanism.
            //
            // Some things to test:
            // - Direct file transfer of entries with data, rsrc, data+rsrc.
            // - Export/extract to filesystem with attribute preservation.
            // - Filesystem copy with re-rooted source/target.
            // - MacZip at source and destination (but not at the same time).
            // - DOS text conversion and path stripping.
            //

            parms.Preserve = ExtractFileWorker.PreserveMode.NAPS;
            parms.StripPaths = false;
            parms.Raw = false;
            parms.MacZip = true;

            using NuFX nufxSrc = NuFX.CreateArchive(parms.AppHook);
            CreateTestArchive(nufxSrc, sSampleFiles, parms);

            using Zip zipSrc = Zip.CreateArchive(parms.AppHook);
            CreateTestArchive(zipSrc, sSampleFiles, parms);

            using IDiskImage testProFS = CreateTestProDisk(sSampleFiles, parms);
            IFileSystem proFs = (IFileSystem)testProFS.Contents!;

            TestForeignExtract(nufxSrc, parms);
            TestBasicXfer(proFs, parms);
            TestRerootXfer(proFs, parms);
            TestZipToProDOS(zipSrc, parms);

            Controller.RemoveTestTmp(parms);
        }

        private static void TestForeignExtract(IArchive arc, ParamsBag parms) {
            List<IFileEntry> entries = GatherArchiveEntries(arc, parms);
            ClipFileSet clipSet = new ClipFileSet(arc, entries, IFileEntry.NO_ENTRY,
                parms.Preserve, parms.Raw, parms.StripPaths, parms.MacZip,
                null, null, parms.AppHook);

            CheckForeign(clipSet, sForeignExtractNAPS, parms);
        }

        private static void TestBasicXfer(IFileSystem srcFs, ParamsBag parms) {
            List<IFileEntry> entries = GatherDiskEntries(srcFs, srcFs.GetVolDirEntry());

            ClipFileSet clipSet = new ClipFileSet(srcFs, entries, IFileEntry.NO_ENTRY,
                parms.Preserve, parms.Raw, parms.StripPaths, parms.MacZip,
                null, null, parms.AppHook);
            List<MemoryStream?> xferStreams = GenerateXferStreams(srcFs, clipSet);
            Debug.Assert(clipSet.XferEntries.Count == xferStreams.Count);

            CheckXfer(clipSet, sXferFromProDOS, parms);

            using IDiskImage disk = CreateTestDisk(FileSystemType.ProDOS, parms);
            CheckOutput(entries, clipSet, (IFileSystem)disk.Contents!, IFileEntry.NO_ENTRY,
                xferStreams, sOutputProDOS, parms);
        }

        private static void TestRerootXfer(IFileSystem srcFs, ParamsBag parms) {
            IFileEntry srcRootDir = srcFs.GetVolDirEntry();
            IFileEntry srcBaseDir = srcFs.FindFileEntry(srcRootDir, SUBDIR1);
            // Deliberately use rootDir here rather than baseDir to exercise exclusion of
            // entries that live below the root.
            List<IFileEntry> entries = GatherDiskEntries(srcFs, srcRootDir);

            ClipFileSet clipSet = new ClipFileSet(srcFs, entries, srcBaseDir,
                parms.Preserve, parms.Raw, parms.StripPaths, parms.MacZip,
                null, null, parms.AppHook);
            List<MemoryStream?> xferStreams = GenerateXferStreams(srcFs, clipSet);
            Debug.Assert(clipSet.XferEntries.Count == xferStreams.Count);

            CheckXfer(clipSet, sXferFromProDOSReroot, parms);
            using IDiskImage dstDisk = CreateTestDisk(FileSystemType.ProDOS, parms);
            IFileSystem dstFs = (IFileSystem)dstDisk.Contents!;
            IFileEntry dstRootDir = dstFs.GetVolDirEntry();
            IFileEntry dstBaseDir = dstFs.CreateFile(dstRootDir, SUBDIR1, CreateMode.Directory);
            CheckOutput(entries, clipSet, dstFs, dstBaseDir, xferStreams, sOutputProDOSReroot,
                parms);
        }

        private static void TestZipToProDOS(Zip zip, ParamsBag parms) {
            parms.MacZip = true;
            List<IFileEntry> entries = GatherArchiveEntries(zip, parms);

            ClipFileSet clipSet = new ClipFileSet(zip, entries, IFileEntry.NO_ENTRY,
                parms.Preserve, parms.Raw, parms.StripPaths, parms.MacZip,
                null, null, parms.AppHook);
            List<MemoryStream?> xferStreams = GenerateXferStreams(zip, clipSet);
            Debug.Assert(clipSet.XferEntries.Count == xferStreams.Count);

            CheckXfer(clipSet, sXferFromZip, parms);

            using IDiskImage disk = CreateTestDisk(FileSystemType.ProDOS, parms);
            CheckOutput(entries, clipSet, (IFileSystem)disk.Contents!, IFileEntry.NO_ENTRY,
                xferStreams, sOutputProDOS, parms);
        }


        private const string DATA_ONLY_NAME = "DataOnly";
        private const string RSRC_ONLY_NAME = "RsrcOnly";
        private const string DATA_RSRC_NAME = "DataRsrc";
        private const string ANOTHER_NAME = "Another";
        private const string SUBDIR1 = "subdir1";
        private const string SUBDIR2 = "subdir2";
        private const int DATA_ONLY_DLEN = 1234;
        private const int RSRC_ONLY_RLEN = 4321;
        private const int DATA_RSRC_DLEN = 2345;
        private const int DATA_RSRC_RLEN = 5432;
        private const int ANOTHER_DLEN = 1122;
        private const byte ARC_DATA_VAL = 0x11;
        private const byte ARC_RSRC_VAL = 0x22;
        private const byte FS_DATA_VAL = 0x33;
        private const byte FS_RSRC_VAL = 0x44;

        private static readonly DateTime CREATE_WHEN = new DateTime(1977, 6, 1, 1, 2, 0);
        private static readonly DateTime MOD_WHEN = new DateTime(1986, 9, 15, 4, 5, 0);
        private const byte PRODOS_FILETYPE = 0x06;
        private const ushort PRODOS_AUXTYPE = 0x12cd;
        private const uint HFS_FILETYPE = 0x54455354;       // 'TEST'
        private const uint HFS_CREATOR = 0x23435032;        // '#CP2'
        private const string NAPS_STR = "#0612cd";
        private const string NAPS_STR_R = NAPS_STR + "r";
        private const char DIR_SEP = ':';
        private const char PRO_SEP = '/';

        private class SampleFile {
            public string PathName { get; private set; }
            public int DataLength { get; private set; }
            public int RsrcLength { get; private set; }

            public SampleFile(string pathName, int dataLength, int rsrcLength) {
                PathName = pathName;
                DataLength = dataLength;
                RsrcLength = rsrcLength;
            }
        }

        private static SampleFile[] sSampleFiles = new SampleFile[] {
            new SampleFile(DATA_ONLY_NAME, DATA_ONLY_DLEN, -1),
            new SampleFile(RSRC_ONLY_NAME, -1, RSRC_ONLY_RLEN),
            new SampleFile(DATA_RSRC_NAME, DATA_RSRC_DLEN, DATA_RSRC_RLEN),
            new SampleFile(SUBDIR1 + DIR_SEP + DATA_ONLY_NAME, DATA_ONLY_DLEN, -1),
            new SampleFile(SUBDIR1 + DIR_SEP + SUBDIR2 + DIR_SEP + DATA_ONLY_NAME,
                DATA_ONLY_DLEN, -1),
            new SampleFile(ANOTHER_NAME, ANOTHER_DLEN, -1),
        };

        private static void CreateTestArchive(IArchive arc, SampleFile[] specs, ParamsBag parms) {
            arc.StartTransaction();
            foreach (SampleFile sample in specs) {
                string pathName =
                    sample.PathName.Replace(DIR_SEP, arc.Characteristics.DefaultDirSep);
                IFileEntry newEntry = arc.CreateRecord();
                newEntry.FileName = pathName;
                newEntry.FileType = PRODOS_FILETYPE;
                newEntry.AuxType = PRODOS_AUXTYPE;
                newEntry.CreateWhen = CREATE_WHEN;
                newEntry.ModWhen = MOD_WHEN;
                if (sample.DataLength >= 0) {
                    IPartSource dataSource =
                        Controller.CreateSimpleSource(sample.DataLength, ARC_DATA_VAL);
                    arc.AddPart(newEntry, FilePart.DataFork, dataSource, CompressionFormat.Default);
                } else if (arc is Zip) {
                    // Create an empty data fork record.
                    IPartSource dataSource = Controller.CreateSimpleSource(0, ARC_DATA_VAL);
                    arc.AddPart(newEntry, FilePart.DataFork, dataSource, CompressionFormat.Default);
                }

                if (arc is Zip) {
                    // Need to create a second entry in the ZIP archive, with the AppleDouble
                    // header, to hold the type info and possibly a resource fork.
                    MemoryStream headerStream = new MemoryStream();
                    using AppleSingle header = AppleSingle.CreateDouble(2, parms.AppHook);
                    header.StartTransaction();
                    IFileEntry hdrEntry = header.GetFirstEntry();
                    hdrEntry.FileType = PRODOS_FILETYPE;
                    hdrEntry.AuxType = PRODOS_AUXTYPE;
                    hdrEntry.CreateWhen = CREATE_WHEN;
                    hdrEntry.ModWhen = MOD_WHEN;
                    if (sample.RsrcLength > 0) {
                        IPartSource rsrcSource =
                            Controller.CreateSimpleSource(sample.RsrcLength, ARC_RSRC_VAL);
                        header.AddPart(hdrEntry, FilePart.RsrcFork, rsrcSource,
                            CompressionFormat.Default);
                    }
                    header.CommitTransaction(headerStream);

                    // Add the ADF data to the archive.
                    IFileEntry adfEntry = arc.CreateRecord();
                    adfEntry.FileName = Zip.GenerateMacZipName(pathName);
                    arc.AddPart(adfEntry, FilePart.DataFork, new SimplePartSource(headerStream),
                        CompressionFormat.Default);
                } else if (sample.RsrcLength > 0) {
                    // Just add a resource fork part to the entry we're creating.
                    IPartSource rsrcSource =
                        Controller.CreateSimpleSource(sample.RsrcLength, ARC_RSRC_VAL);
                    arc.AddPart(newEntry, FilePart.RsrcFork, rsrcSource,
                        CompressionFormat.Default);
                }
            }
            MemoryStream outStream = new MemoryStream();
            arc.CommitTransaction(outStream);

            //if (arc is Zip) {
            //    outStream.Position = 0;
            //    using (FileStream stream = new FileStream(@"c:\src\ciderpress2\foo.zip",
            //            FileMode.Create)) {
            //        outStream.CopyTo(stream);
            //    }
            //}
        }

        private static IDiskImage CreateTestProDisk(SampleFile[] specs, ParamsBag parms) {
            IDiskImage disk = CreateTestDisk(FileSystemType.ProDOS, parms);
            IFileSystem fs = (IFileSystem)disk.Contents!;

            foreach (SampleFile sample in specs) {
                string dirName = PathName.GetDirectoryName(sample.PathName, DIR_SEP);
                string fileName = PathName.GetFileName(sample.PathName, DIR_SEP);

                IFileEntry subDir = fs.CreateSubdirectories(fs.GetVolDirEntry(), dirName, DIR_SEP);
                IFileEntry newEntry = fs.CreateFile(subDir, fs.AdjustFileName(fileName),
                    sample.RsrcLength >= 0 ? CreateMode.Extended : CreateMode.File);
                newEntry.FileName = fileName;
                newEntry.FileType = PRODOS_FILETYPE;
                newEntry.AuxType = PRODOS_AUXTYPE;
                newEntry.CreateWhen = CREATE_WHEN;
                newEntry.ModWhen = MOD_WHEN;
                if (sample.DataLength >= 0) {
                    using (Stream stream = fs.OpenFile(newEntry, FileAccessMode.ReadWrite,
                            FilePart.DataFork)) {
                        for (int i = 0; i < sample.DataLength; i++) {
                            stream.WriteByte(FS_DATA_VAL);
                        }
                    }
                }
                if (sample.RsrcLength >= 0) {
                    using (Stream stream = fs.OpenFile(newEntry, FileAccessMode.ReadWrite,
                            FilePart.RsrcFork)) {
                        for (int i = 0; i < sample.RsrcLength; i++) {
                            stream.WriteByte(FS_RSRC_VAL);
                        }
                    }
                }
            }
            return disk;
        }

        /// <summary>
        /// Creates a formatted disk image in a memory stream.
        /// </summary>
        private static IDiskImage CreateTestDisk(FileSystemType fsType, ParamsBag parms) {
            UnadornedSector disk;
            if (fsType == FileSystemType.DOS33) {
                disk = UnadornedSector.CreateSectorImage(new MemoryStream(), 35, 16,
                    SectorOrder.DOS_Sector, parms.AppHook);
            } else {
                disk = UnadornedSector.CreateBlockImage(new MemoryStream(), 1600, parms.AppHook);

            }
            disk.FormatDisk(fsType, "Test", 254, false, parms.AppHook);
            IFileSystem fs = (IFileSystem)disk.Contents!;
            fs.PrepareFileAccess(true);
            return disk;
        }

        /// <summary>
        /// Expected contents of the ForeignEntries list.
        /// </summary>
        private class ExpectedForeign {
            public string PathName { get; private set; }
            public int MinLength { get; private set; }
            public int MaxLength { get; private set; }

            public ExpectedForeign(string fileName, int length)
                    : this(fileName, length, length) {
            }
            public ExpectedForeign(string fileName, int minLength, int maxLength) {
                PathName = fileName;
                MinLength = minLength;
                MaxLength = maxLength;
            }
        }

        private static ExpectedForeign[] sForeignExtractNAPS = new ExpectedForeign[] {
            new ExpectedForeign(DATA_ONLY_NAME + NAPS_STR, DATA_ONLY_DLEN),
            new ExpectedForeign(RSRC_ONLY_NAME + NAPS_STR_R, RSRC_ONLY_RLEN),
            new ExpectedForeign(DATA_RSRC_NAME + NAPS_STR, DATA_RSRC_DLEN),
            new ExpectedForeign(DATA_RSRC_NAME + NAPS_STR_R, DATA_RSRC_RLEN),
            new ExpectedForeign(SUBDIR1, -1),
            new ExpectedForeign(Path.Join(SUBDIR1, DATA_ONLY_NAME + NAPS_STR), DATA_ONLY_DLEN),
            new ExpectedForeign(Path.Join(SUBDIR1, SUBDIR2), -1),
            new ExpectedForeign(Path.Join(SUBDIR1, SUBDIR2, DATA_ONLY_NAME + NAPS_STR),
                DATA_ONLY_DLEN),
            new ExpectedForeign(ANOTHER_NAME + NAPS_STR, ANOTHER_DLEN),
        };

        private static void CheckForeign(ClipFileSet clipSet, ExpectedForeign[] expected,
                ParamsBag parms) {
            if (clipSet.ForeignEntries.Count != expected.Length) {
                throw new Exception("Foreign extract count mismatch: expected=" + expected.Length +
                    ", actual=" + clipSet.ForeignEntries.Count);
            }

            // Check the foreign entries.
            for (int i = 0; i < clipSet.ForeignEntries.Count; i++) {
                ClipFileEntry clipEntry = clipSet.ForeignEntries[i];
                ExpectedForeign exp = expected[i];
                if (clipEntry.ExtractPath != exp.PathName) {
                    throw new Exception("Foreign mismatch " + i + ": expected='" + exp.PathName +
                        "', actual='" + clipEntry.ExtractPath + "'");
                }
                if (clipEntry.Attribs.IsDirectory) {
                    continue;
                }
                if (clipEntry.Attribs.FileType != PRODOS_FILETYPE ||
                        clipEntry.Attribs.AuxType != PRODOS_AUXTYPE ||
                        clipEntry.Attribs.CreateWhen != CREATE_WHEN ||
                        clipEntry.Attribs.ModWhen != MOD_WHEN) {
                    throw new Exception("Foreign attribute mismatch " + i + ": " +
                        clipEntry.ExtractPath);
                }
                // Test reported length.  In many cases the output length can't be known.
                if (clipEntry.OutputLength >= 0 &&
                        (clipEntry.OutputLength < exp.MinLength ||
                            clipEntry.OutputLength > exp.MaxLength)) {
                    throw new Exception("Foreign output length mismatch " + i + ": expected=" +
                        exp.MinLength + "," + exp.MaxLength + ", actual=" + clipEntry.OutputLength);
                }
            }

            // This would be sent to Windows Explorer or another application, so there isn't
            // really more to test from here.
        }

        /// <summary>
        /// Expected contents of the XferEntries list.
        /// </summary>
        private class ExpectedXfer {
            public string PathName { get; private set; }
            public FilePart Part { get; private set; }
            public int Length { get; private set; }

            public ExpectedXfer(string pathName, FilePart part, int length) {
                PathName = pathName;
                Part = part;
                Length = length;
            }
            public override string ToString() {
                return "[ExpXfer: '" + PathName + "' part=" + Part + " len=" + Length + "]";
            }
        }

        /// <summary>
        /// Expected result of pasting to a file archive or disk image.
        /// </summary>
        private class ExpectedOutput {
            public string PathName { get; private set; }
            public int DataLengthMin { get; private set; }
            public int DataLengthMax { get; private set; }
            public int RsrcLength { get; private set; }
            public bool IsDirectory { get; private set; }

            public ExpectedOutput(string pathName, int dataLength, int rsrcLength,
                    bool hasFileType)
                    : this(pathName, dataLength, dataLength, rsrcLength, hasFileType) {
            }
            public ExpectedOutput(string pathName, int dataLengthMin, int dataLengthMax,
                    int rsrcLength, bool isDirectory) {
                PathName = pathName;
                DataLengthMin = dataLengthMin;
                DataLengthMax = dataLengthMax;
                RsrcLength = rsrcLength;
                IsDirectory = isDirectory;
            }
        }

        private static ExpectedXfer[] sXferFromProDOS = new ExpectedXfer[] {
            new ExpectedXfer(DATA_ONLY_NAME, FilePart.DataFork, DATA_ONLY_DLEN),
            new ExpectedXfer(RSRC_ONLY_NAME, FilePart.DataFork, 0),
            new ExpectedXfer(RSRC_ONLY_NAME, FilePart.RsrcFork, RSRC_ONLY_RLEN),
            new ExpectedXfer(DATA_RSRC_NAME, FilePart.DataFork, DATA_RSRC_DLEN),
            new ExpectedXfer(DATA_RSRC_NAME, FilePart.RsrcFork, DATA_RSRC_RLEN),
            new ExpectedXfer(SUBDIR1, FilePart.Unknown, -1),
            new ExpectedXfer(SUBDIR1 + PRO_SEP + DATA_ONLY_NAME, FilePart.DataFork, DATA_ONLY_DLEN),
            new ExpectedXfer(SUBDIR1 + PRO_SEP + SUBDIR2, FilePart.Unknown, -1),
            new ExpectedXfer(SUBDIR1 + PRO_SEP + SUBDIR2 + PRO_SEP + DATA_ONLY_NAME,
                FilePart.DataFork, DATA_ONLY_DLEN),
            new ExpectedXfer(ANOTHER_NAME, FilePart.DataFork, ANOTHER_DLEN),
        };

        private static ExpectedOutput[] sOutputProDOS = new ExpectedOutput[] {
            new ExpectedOutput(DATA_ONLY_NAME, DATA_ONLY_DLEN, -1, false),
            new ExpectedOutput(RSRC_ONLY_NAME, -1, RSRC_ONLY_RLEN, false),
            new ExpectedOutput(DATA_RSRC_NAME, DATA_RSRC_DLEN, DATA_RSRC_RLEN, false),
            new ExpectedOutput(SUBDIR1, -1, -1, true),
            new ExpectedOutput(SUBDIR1 + PRO_SEP + DATA_ONLY_NAME, DATA_ONLY_DLEN, -1, false),
            new ExpectedOutput(SUBDIR1 + PRO_SEP + SUBDIR2, -1, -1, true),
            new ExpectedOutput(SUBDIR1 + PRO_SEP + SUBDIR2 + PRO_SEP + DATA_ONLY_NAME,
                DATA_ONLY_DLEN, -1, false),
            new ExpectedOutput(ANOTHER_NAME, ANOTHER_DLEN, -1, false),
        };

        private static ExpectedXfer[] sXferFromProDOSReroot = new ExpectedXfer[] {
            new ExpectedXfer(DATA_ONLY_NAME, FilePart.DataFork, DATA_ONLY_DLEN),
            new ExpectedXfer(SUBDIR2, FilePart.Unknown, -1),
            new ExpectedXfer(SUBDIR2 + PRO_SEP + DATA_ONLY_NAME, FilePart.DataFork, DATA_ONLY_DLEN),
        };

        private static ExpectedOutput[] sOutputProDOSReroot = new ExpectedOutput[] {
            new ExpectedOutput(SUBDIR1 + PRO_SEP + DATA_ONLY_NAME, DATA_ONLY_DLEN, -1, false),
            new ExpectedOutput(SUBDIR1 + PRO_SEP + SUBDIR2, -1, -1, true),
            new ExpectedOutput(SUBDIR1 + PRO_SEP + SUBDIR2 + PRO_SEP + DATA_ONLY_NAME,
                DATA_ONLY_DLEN, -1, false),
        };

        private static ExpectedXfer[] sXferFromZip = new ExpectedXfer[] {
            new ExpectedXfer(DATA_ONLY_NAME, FilePart.DataFork, DATA_ONLY_DLEN),
            new ExpectedXfer(RSRC_ONLY_NAME, FilePart.DataFork, 0),
            new ExpectedXfer(RSRC_ONLY_NAME, FilePart.RsrcFork, RSRC_ONLY_RLEN),
            new ExpectedXfer(DATA_RSRC_NAME, FilePart.DataFork, DATA_RSRC_DLEN),
            new ExpectedXfer(DATA_RSRC_NAME, FilePart.RsrcFork, DATA_RSRC_RLEN),
            new ExpectedXfer(SUBDIR1, FilePart.Unknown, -1),
            new ExpectedXfer(SUBDIR1 + PRO_SEP + DATA_ONLY_NAME, FilePart.DataFork, DATA_ONLY_DLEN),
            new ExpectedXfer(SUBDIR1 + PRO_SEP + SUBDIR2, FilePart.Unknown, -1),
            new ExpectedXfer(SUBDIR1 + PRO_SEP + SUBDIR2 + PRO_SEP + DATA_ONLY_NAME,
                FilePart.DataFork, DATA_ONLY_DLEN),
            new ExpectedXfer(ANOTHER_NAME, FilePart.DataFork, ANOTHER_DLEN),
        };


        private static List<IFileEntry> GatherArchiveEntries(IArchive arc, ParamsBag parms) {
            List<IFileEntry> entryList = new List<IFileEntry>();
            foreach (IFileEntry entry in arc) {
                if (arc is Zip && parms.MacZip && entry.IsMacZipHeader()) {
                    continue;
                }
                entryList.Add(entry);
            }
            return entryList;
        }

        private static List<IFileEntry> GatherDiskEntries(IFileSystem fs, IFileEntry rootDir) {
            List<IFileEntry> entryList = new List<IFileEntry>();
            GetDiskEntries(fs, rootDir, entryList);
            return entryList;
        }

        private static void GetDiskEntries(IFileSystem fs, IFileEntry dir,
                List<IFileEntry> entryList) {
            foreach (IFileEntry entry in dir) {
                entryList.Add(entry);
                if (entry.IsDirectory) {
                    GetDiskEntries(fs, entry, entryList);
                }
            }
        }

        /// <summary>
        /// Synthesizes a parallel set of memory streams for the file contents.
        /// </summary>
        private static List<MemoryStream?> GenerateXferStreams(object archiveOrFileSystem,
                ClipFileSet clipSet) {
            List<ClipFileEntry> entries = clipSet.XferEntries;
            List<MemoryStream?> xferStreams = new List<MemoryStream?>();
            foreach (ClipFileEntry entry in entries) {
                if (entry.Attribs.IsDirectory) {
                    xferStreams.Add(null);
                    continue;
                }

                byte val;
                long length;
                if (entry.Part == FilePart.RsrcFork) {
                    length = entry.Attribs.RsrcLength;
                    Debug.Assert(length >= 0);
                    if (archiveOrFileSystem is IArchive) {
                        val = ARC_RSRC_VAL;
                    } else {
                        val = FS_RSRC_VAL;
                    }
                } else {
                    length = entry.Attribs.DataLength;
                    Debug.Assert(length >= 0);
                    if (archiveOrFileSystem is IArchive) {
                        val = ARC_DATA_VAL;
                    } else {
                        val = FS_DATA_VAL;
                    }
                }

                byte[] buf = new byte[length];
                RawData.MemSet(buf, 0, (int)length, val);
                xferStreams.Add(new MemoryStream(buf));
            }

            Debug.Assert(xferStreams.Count == clipSet.XferEntries.Count);
            return xferStreams;
        }

        private static void CheckXfer(ClipFileSet clipSet, ExpectedXfer[] expected,
                ParamsBag parms) {
            if (clipSet.XferEntries.Count != expected.Length) {
                throw new Exception("Xfer extract count mismatch: expected=" + expected.Length +
                    ", actual=" + clipSet.XferEntries.Count);
            }

            // Check the xfer entries.
            for (int i = 0; i < clipSet.XferEntries.Count; i++) {
                ClipFileEntry clipEntry = clipSet.XferEntries[i];
                ExpectedXfer exp = expected[i];
                if (clipEntry.ExtractPath != exp.PathName) {
                    throw new Exception("Xfer mismatch " + i + ": expected='" + exp.PathName +
                        "', actual='" + clipEntry.ExtractPath + "'");
                }
                if (clipEntry.Attribs.IsDirectory) {
                    continue;
                }
                if (clipEntry.Part != exp.Part) {
                    throw new Exception("Xfer part mismatch " + i + ": expected=" + exp.Part +
                        ", actual=" + clipEntry.Part);
                }
                if (clipEntry.Attribs.FileType != PRODOS_FILETYPE ||
                        clipEntry.Attribs.AuxType != PRODOS_AUXTYPE ||
                        clipEntry.Attribs.CreateWhen != CREATE_WHEN ||
                        clipEntry.Attribs.ModWhen != MOD_WHEN) {
                    throw new Exception("Xfer attribute mismatch " + i + ": " +
                        clipEntry.ExtractPath);
                }
                // Test reported length.  In many cases the output length can't be known.
                if (clipEntry.OutputLength >= 0 && clipEntry.OutputLength != exp.Length) {
                    throw new Exception("Xfer output length mismatch " + i + ": expected=" +
                        exp.Length + ", actual=" + clipEntry.OutputLength);
                }
            }
        }

        private static void CheckOutput(List<IFileEntry> srcEntries, ClipFileSet clipSet,
                object archiveOrFileSystem, IFileEntry dstBaseDir, List<MemoryStream?> xferStreams,
                ExpectedOutput[] expected, ParamsBag parms) {
            ClipPasteWorker.CallbackFunc cbFunc = delegate (CallbackFacts what) {
                // Do nothing.
                return CallbackFacts.Results.Unknown;
            };

            ClipPasteWorker.ClipStreamGenerator streamGen = delegate (ClipFileEntry clipEntry) {
                int index = clipSet.XferEntries.IndexOf(clipEntry);
                Stream? stream = xferStreams[index];
                if (stream == null) {
                    Debug.WriteLine("StreamGen: returning null stream");
                } else {
                    Debug.Assert(stream.Position == 0);
                    Debug.WriteLine("StreamGen: returning stream, length=" + stream.Length);
                }
                return xferStreams[index];
            };

            ClipPasteWorker worker = new ClipPasteWorker(clipSet.XferEntries, streamGen,
                cbFunc, parms.Compress, parms.MacZip, parms.ConvertDOSText, parms.StripPaths,
                parms.Raw, true, parms.AppHook);

            bool isCancelled;
            if (archiveOrFileSystem is IArchive) {
                worker.AddFilesToArchive((IArchive)archiveOrFileSystem, out isCancelled);
            } else {
                worker.AddFilesToDisk((IFileSystem)archiveOrFileSystem, dstBaseDir, out isCancelled);
            }
            Debug.Assert(!isCancelled);

            // Confirm contents match expectations.
            List<IFileEntry> dstEntries;
            if (archiveOrFileSystem is IArchive) {
                IArchive arc = (IArchive)archiveOrFileSystem;
                dstEntries = GatherArchiveEntries(arc, parms);
            } else {
                IFileSystem fs = (IFileSystem)archiveOrFileSystem;
                if (dstBaseDir == IFileEntry.NO_ENTRY) {
                    dstBaseDir = fs.GetVolDirEntry();
                }
                dstEntries = GatherDiskEntries(fs, dstBaseDir);
            }

            if (dstEntries.Count != expected.Length) {
                throw new Exception("Output has different number of entries: dst=" +
                    dstEntries.Count + " expect=" + expected.Length);
            }
            for (int i = 0; i < dstEntries.Count; i++) {
                CompareEntries(i, dstEntries[i], expected[i]);
            }
        }

        private static void CompareEntries(int index, IFileEntry dstEntry, ExpectedOutput exp) {
            string prefix = "Output " + index + " '" + exp.PathName + "': ";

            if (dstEntry.FullPathName != exp.PathName) {
                throw new Exception(prefix + "pathname mismatch dst='" +
                    dstEntry.FullPathName + " exp=" + exp.PathName);
            }
            if (!exp.IsDirectory) {
                if (dstEntry.FileType != PRODOS_FILETYPE || dstEntry.AuxType != PRODOS_AUXTYPE) {
                    throw new Exception(prefix + "lacks types");
                }
                if (dstEntry.CreateWhen != CREATE_WHEN || dstEntry.ModWhen != MOD_WHEN) {
                    throw new Exception(prefix + "lacks dates");
                }

                // Confirm the file lengths.
                // TODO? confirm contents too
                if (exp.DataLengthMin < 0) {
                    if (dstEntry.DataLength > 0) {
                        throw new Exception(prefix + "has data fork");
                    }
                } else {
                    if (dstEntry.DataLength < exp.DataLengthMin ||
                            dstEntry.DataLength > exp.DataLengthMax) {
                        throw new Exception(prefix + "incorrect data length: " +
                            dstEntry.DataLength);
                    }
                }
                if (exp.RsrcLength < 0) {
                    if (dstEntry.RsrcLength > 0) {
                        throw new Exception(prefix + "has rsrc fork");
                    }
                } else {
                    if (dstEntry.RsrcLength != exp.RsrcLength) {
                        throw new Exception(prefix + "incorrect rsrc length: " +
                            dstEntry.RsrcLength);
                    }
                }
            }
        }
    }
}
