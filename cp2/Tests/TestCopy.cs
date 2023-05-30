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
    /// Tests the "copy" command.
    /// </summary>
    internal static class TestCopy {
        public static void RunTest(ParamsBag parms) {
            Controller.Stdout.WriteLine("  Copy...");
            Controller.PrepareTestTmp(parms);

            // Configure parameters.
            parms.Compress = true;
            parms.ConvertDOSText = false;       // disable DOS text conversions
            parms.MacZip = false;               // matrix test uses bare ZIP
            parms.Overwrite = false;
            parms.Raw = false;                  // shouldn't matter
            parms.Recurse = true;
            parms.Sectors = 16;
            parms.StripPaths = false;

            // Generate test files.
            CreateBinary2(parms);
            CreateNuFX(parms);
            CreateZip(parms);
            CreateDOS(parms);
            CreateHFS(parms);
            CreateProDOS(parms);

            // Run the NxN matrix of tests.
            Console.WriteLine("*** Testing Binary2 destination...");
            TestCopyToArchive("bny-out.bny", TweakForBinary2, parms);
            Console.WriteLine("*** Testing NuFX destination...");
            TestCopyToArchive("nufx-out.shk", TweakForNuFX, parms);
            Console.WriteLine("*** Testing ZIP destination...");
            TestCopyToArchive("zip-out.zip", TweakForZip, parms);
            Console.WriteLine("*** Testing DOS destination...");
            TestCopyToDisk("dos-out.do", "dos", TweakForDOS, parms);
            Console.WriteLine("*** Testing HFS destination...");
            TestCopyToDisk("hfs-out.po", "hfs", TweakForHFS, parms);
            Console.WriteLine("*** Testing ProDOS destination...");
            TestCopyToDisk("prodos-out.po", "prodos", TweakForProDOS, parms);

            TestDOSSparse(parms);
            TestDOSText(parms);
            TestLongBNY(parms);
            TestMacZip(parms);
            TestExtArchive(parms);
            TestEmptyDir(parms);
            TestTurducken(parms);

            Controller.RemoveTestTmp(parms);
        }

        /// <summary>
        /// Tests copying a sparse text file between two DOS volumes.
        /// </summary>
        private static void TestDOSSparse(ParamsBag parms) {
            const string TXTFILE = "TXTFILE";
            const string BINFILE = "BINFILE";

            // Create the disk images and format them for DOS.
            string srcPath = Path.Combine(Controller.TEST_TMP, "dos-src.do");
            string dstPath = Path.Combine(Controller.TEST_TMP, "dos-dst.do");
            if (!DiskUtil.HandleCreateDiskImage("cdi",
                    new string[] { srcPath, "140k", "dos" }, parms)) {
                throw new Exception("cdi " + srcPath + " failed");
            }
            if (!DiskUtil.HandleCreateDiskImage("cdi",
                    new string[] { dstPath, "140k", "dos" }, parms)) {
                throw new Exception("cdi " + dstPath + " failed");
            }

            using (FileStream stream = new FileStream(srcPath, FileMode.Open)) {
                using (IDiskImage image = UnadornedSector.OpenDisk(stream, parms.AppHook)) {
                    image.AnalyzeDisk();
                    IFileSystem fs = (IFileSystem)image.Contents!;
                    fs.PrepareFileAccess(true);
                    IFileEntry volDir = fs.GetVolDirEntry();

                    // Create a sparse 'T' file.
                    IFileEntry txtFile = fs.CreateFile(volDir, TXTFILE, CreateMode.File,
                        FileAttribs.FILE_TYPE_TXT);
                    using (Stream txtStream = fs.OpenFile(txtFile, FileAccessMode.ReadWrite,
                            FilePart.DataFork)) {
                        txtStream.WriteByte(0xaa);
                        txtStream.Position = 4000;
                        txtStream.WriteByte(0xab);
                    }

                    // Create a 'B' file with a length that is shorter than the file contents.
                    IFileEntry binFile = fs.CreateFile(volDir, BINFILE, CreateMode.File,
                        FileAttribs.FILE_TYPE_BIN);
                    using (Stream binStream = fs.OpenFile(binFile, FileAccessMode.ReadWrite,
                            FilePart.RawData)) {
                        RawData.WriteU16LE(binStream, 0x4000);  // addr
                        RawData.WriteU16LE(binStream, 200);     // length
                        for (int i = 0; i < 1000; i++) {
                            binStream.WriteByte(0xcc);
                        }
                    }
                }
            }

            // Copy files.
            if (!Copy.HandleCopy("cp", new string[] { srcPath, BINFILE, TXTFILE, dstPath },
                    parms)) {
                throw new Exception("copy " + srcPath + " <files> " + dstPath + " failed");
            }

            // Test files in destination.
            using (FileStream stream = new FileStream(dstPath, FileMode.Open)) {
                using (IDiskImage image = UnadornedSector.OpenDisk(stream, parms.AppHook)) {
                    image.AnalyzeDisk();
                    IFileSystem fs = (IFileSystem)image.Contents!;
                    fs.PrepareFileAccess(true);
                    IFileEntry volDir = fs.GetVolDirEntry();

                    // Length is sequential access (to first 0x00), size is number of sectors
                    // (one for 0xaa, one for 0xab, one for T/S list).
                    IFileEntry txtFile = fs.FindFileEntry(volDir, TXTFILE);
                    if (txtFile.DataLength != 1 || txtFile.StorageSize != 768) {
                        throw new Exception("Unexpected text file length");
                    }
                    using (Stream txtStream = fs.OpenFile(txtFile, FileAccessMode.ReadOnly,
                            FilePart.RawData)) {
                        // Raw file EOF is to last byte, rounded up to nearest 256.
                        if (txtStream.Length != 4096) {
                            throw new Exception("Unexpected text file open size: " +
                                txtStream.Length);
                        }
                    }

                    // Length is specified value, storage size is 1000 rounded up to nearest 256
                    // plus T/S list sector.
                    IFileEntry binFile = fs.FindFileEntry(volDir, BINFILE);
                    if (binFile.DataLength != 200 || binFile.StorageSize != 1024+256) {
                        throw new Exception("Unexpected bin file length");
                    }
                }
            }

            // Clean up.
            File.Delete(srcPath);
            File.Delete(dstPath);
        }

        /// <summary>
        /// Confirms text conversion when copying to and from a DOS volume.  Exercises --overwrite.
        /// </summary>
        private static void TestDOSText(ParamsBag parms) {
            const string LOW_D = "LOW.D";
            const string LOW_P = "LOW.P";
            const string HIGH_D = "HIGH.D";
            const string HIGH_P = "HIGH.P";

            // Create the disk images and format them.
            string dosPath = Path.Combine(Controller.TEST_TMP, "textconv-dos.do");
            string proPath = Path.Combine(Controller.TEST_TMP, "textconv-pro.po");
            if (!DiskUtil.HandleCreateDiskImage("cdi",
                    new string[] { dosPath, "140k", "dos" }, parms)) {
                throw new Exception("cdi " + dosPath + " failed");
            }
            if (!DiskUtil.HandleCreateDiskImage("cdi",
                    new string[] { proPath, "140k", "prodos" }, parms)) {
                throw new Exception("cdi " + proPath + " failed");
            }

            byte[] lowPattern = new byte[] { 0x2a, 0x0d, 0x2a, 0x20, 0x48, 0x49, 0x0d };
            byte[] highPattern = new byte[] { 0xaa, 0x8d, 0xaa, 0xa0, 0xc8, 0xc9, 0x8d };

            // Set up the DOS disk.
            using (FileStream stream = new FileStream(dosPath, FileMode.Open)) {
                using (IDiskImage image = UnadornedSector.OpenDisk(stream, parms.AppHook)) {
                    image.AnalyzeDisk();
                    IFileSystem fs = (IFileSystem)image.Contents!;
                    fs.PrepareFileAccess(true);
                    IFileEntry volDir = fs.GetVolDirEntry();

                    IFileEntry lowFile = fs.CreateFile(volDir, LOW_D, CreateMode.File,
                        FileAttribs.FILE_TYPE_TXT);
                    using (Stream txtStream = fs.OpenFile(lowFile, FileAccessMode.ReadWrite,
                            FilePart.DataFork)) {
                        txtStream.Write(lowPattern);
                    }
                    IFileEntry hiFile = fs.CreateFile(volDir, HIGH_D, CreateMode.File,
                        FileAttribs.FILE_TYPE_TXT);
                    using (Stream txtStream = fs.OpenFile(hiFile, FileAccessMode.ReadWrite,
                            FilePart.DataFork)) {
                        txtStream.Write(highPattern);
                    }
                }
            }

            // Set up the ProDOS disk.
            using (FileStream stream = new FileStream(proPath, FileMode.Open)) {
                using (IDiskImage image = UnadornedSector.OpenDisk(stream, parms.AppHook)) {
                    image.AnalyzeDisk();
                    IFileSystem fs = (IFileSystem)image.Contents!;
                    fs.PrepareFileAccess(true);
                    IFileEntry volDir = fs.GetVolDirEntry();

                    IFileEntry lowFile = fs.CreateFile(volDir, LOW_P, CreateMode.File,
                        FileAttribs.FILE_TYPE_TXT);
                    using (Stream txtStream = fs.OpenFile(lowFile, FileAccessMode.ReadWrite,
                            FilePart.DataFork)) {
                        txtStream.Write(lowPattern);
                    }
                    IFileEntry hiFile = fs.CreateFile(volDir, HIGH_P, CreateMode.File,
                        FileAttribs.FILE_TYPE_TXT);
                    using (Stream txtStream = fs.OpenFile(hiFile, FileAccessMode.ReadWrite,
                            FilePart.DataFork)) {
                        txtStream.Write(highPattern);
                    }
                }
            }

            // Copy with conversion enabled.
            parms.ConvertDOSText = true;
            if (!Copy.HandleCopy("cp", new string[] { dosPath, LOW_D, HIGH_D, proPath }, parms)) {
                throw new Exception("Enabled copy " + dosPath + " to " + proPath + " failed");
            }
            if (!Copy.HandleCopy("cp", new string[] { proPath, LOW_P, HIGH_P, dosPath }, parms)) {
                throw new Exception("Enabled copy " + proPath + " to " + dosPath + " failed");
            }

            // Check contents.
            CheckFileContents(dosPath, LOW_P, highPattern, parms);
            CheckFileContents(dosPath, HIGH_P, highPattern, parms);
            CheckFileContents(proPath, LOW_D, lowPattern, parms);
            CheckFileContents(proPath, HIGH_D, lowPattern, parms);

            // Repeat with conversion disabled.
            parms.ConvertDOSText = false;
            parms.Overwrite = true;
            if (!Copy.HandleCopy("cp", new string[] { dosPath, LOW_D, HIGH_D, proPath }, parms)) {
                throw new Exception("Enabled copy " + dosPath + " to " + proPath + " failed");
            }
            if (!Copy.HandleCopy("cp", new string[] { proPath, LOW_P, HIGH_P, dosPath }, parms)) {
                throw new Exception("Enabled copy " + proPath + " to " + dosPath + " failed");
            }

            // Check contents.
            CheckFileContents(dosPath, LOW_P, lowPattern, parms);
            CheckFileContents(dosPath, HIGH_P, highPattern, parms);
            CheckFileContents(proPath, LOW_D, lowPattern, parms);
            CheckFileContents(proPath, HIGH_D, highPattern, parms);

            File.Delete(dosPath);
            File.Delete(proPath);
        }

        private static void CheckFileContents(string diskPath, string fileName, byte[] expected,
                ParamsBag parms) {
            using (FileStream diskStream = new FileStream(diskPath, FileMode.Open)) {
                using (IDiskImage image = UnadornedSector.OpenDisk(diskStream, parms.AppHook)) {
                    image.AnalyzeDisk();
                    IFileSystem fs = (IFileSystem)image.Contents!;
                    fs.PrepareFileAccess(true);
                    IFileEntry volDir = fs.GetVolDirEntry();

                    IFileEntry entry = fs.FindFileEntry(volDir, fileName);
                    using (Stream stream = fs.OpenFile(entry, FileAccessMode.ReadOnly,
                            FilePart.DataFork)) {
                        foreach (byte b in expected) {
                            if (b != stream.ReadByte()) {
                                throw new Exception("Data mismatch in " + diskPath + " " +
                                    fileName + " (first=$" + expected[0].ToString("x2") + ")");
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Confirms that long filenames are rejected for BNY.  Also does a quick check on
        /// wildcards and recursion.
        /// </summary>
        private static void TestLongBNY(ParamsBag parms) {
            string zipPath = Path.Combine(Controller.TEST_TMP, "long-bny-src.zip");
            string bnyPath = Path.Combine(Controller.TEST_TMP, "long-bny-dst.bny");

            // Create and populate ZIP archive.
            using (FileStream zipStream = new FileStream(zipPath, FileMode.CreateNew)) {
                using (IArchive zipArchive = Zip.CreateArchive(parms.AppHook)) {
                    zipArchive.StartTransaction();
                    IFileEntry entry = zipArchive.CreateRecord();
                    entry.FileName = "MultiPartName1/MultiPartName2/MultiPartName3/MultiPartName4";
                    MemoryStream shortStream = new MemoryStream(1);
                    shortStream.WriteByte(0x01);
                    zipArchive.AddPart(entry, FilePart.DataFork, new SimplePartSource(shortStream),
                        CompressionFormat.Uncompressed);

                    entry = zipArchive.CreateRecord();
                    entry.FileName = "MultiPartName1/MultiPartName2/MultiPartName3/MultiPartName4" +
                        "/MultiPartName5";
                    shortStream = new MemoryStream(1);
                    shortStream.WriteByte(0x02);
                    zipArchive.AddPart(entry, FilePart.DataFork, new SimplePartSource(shortStream),
                        CompressionFormat.Uncompressed);
                    zipArchive.CommitTransaction(zipStream);
                }
            }

            // Create an empty BNY archive.
            if (!ArcUtil.HandleCreateFileArchive("cfa", new string[] { bnyPath }, parms)) {
                throw new Exception("cfa " + bnyPath + " failed");
            }

            // Copy the first part.  Should work.
            parms.Recurse = false;
            parms.Overwrite = true;
            if (!Copy.HandleCopy("cp", new string[] { zipPath, "*/*/*/*", bnyPath }, parms)) {
                throw new Exception("First long BNY copy failed");
            }
            // Enable recursion, which should try to copy both items.  And fail.
            parms.Recurse = true;
            if (Copy.HandleCopy("cp", new string[] { zipPath, "*/*/*/*", bnyPath }, parms)) {
                throw new Exception("Long BNY copy unexpectedly succeeded");
            }

            File.Delete(zipPath);
            File.Delete(bnyPath);
        }

        /// <summary>
        /// Tests MacZip behavior.
        /// </summary>
        private static void TestMacZip(ParamsBag parms) {
            string hfsSrcPath = Path.Combine(Controller.TEST_TMP, HFS_TEST_NAME);
            string hfsZipPath = Path.Combine(Controller.TEST_TMP, "hfs-maczip.zip");
            if (!ArcUtil.HandleCreateFileArchive("cfa", new string[] { hfsZipPath }, parms)) {
                throw new Exception("cfa " + hfsZipPath + " failed");
            }

            // Copy the HFS disk from the matrix test.
            parms.MacZip = true;
            if (!Copy.HandleCopy("cp", new string[] { hfsSrcPath, hfsZipPath }, parms )) {
                throw new Exception("cp " + hfsSrcPath + " to " + hfsZipPath + " failed");
            }

            // Do a quick verification of the contents.  (This will break whenever the matrix
            // test is updated.)
            string[] expected = new string[] {
                ",W?X?Y\\Z ",
                "__MACOSX/._,W?X?Y\\Z ",
                "Dir-1/Dir-2/All_Attrs.dat",
                "__MACOSX/Dir-1/Dir-2/._All_Attrs.dat",
                "Dir?1/Dir?2/!Confusion",
                "__MACOSX/Dir?1/Dir?2/._!Confusion",
                "MINIMAL",
                "ResourceOnlyAnd..ryLongFileName",
                "__MACOSX/._ResourceOnlyAnd..ryLongFileName",
                "SIMPLE.TXT",
                "__MACOSX/._SIMPLE.TXT",
                "zero/length/Z.resource",
                "__MACOSX/zero/length/._Z.resource",
            };

            using (FileStream stream = new FileStream(hfsZipPath, FileMode.Open)) {
                using (IArchive zipArchive = Zip.OpenArchive(stream, parms.AppHook)) {
                    List<IFileEntry> entries = zipArchive.ToList();
                    for (int i = 0; i < expected.Length; i++) {
                        if (expected[i] != entries[i].FileName) {
                            throw new Exception("Entry " + i + ": expected '" + expected[i] +
                                "', actual='" + entries[i].FileName + "'");
                        }
                    }

                    // Do a detailed check on a single entry.  Don't call TweakForZip, because
                    // we preserved the attributes in AppleDouble.
                    FileDefinition tweaked = TweakForHFS(sFileFull);
                    IFileEntry mainEnt = zipArchive.FindFileEntry("Dir-1/Dir-2/All_Attrs.dat");
                    if (mainEnt.DataLength != tweaked.DataLength) {
                        throw new Exception("Unexpected data length");
                    }

                    // Extract the AppleDouble part to a temp file.
                    IFileEntry headerEnt =
                        zipArchive.FindFileEntry("__MACOSX/Dir-1/Dir-2/._All_Attrs.dat");
                    Stream adfStream =
                        ArcTemp.ExtractToTemp(zipArchive, headerEnt, FilePart.DataFork);
                    using (IArchive adfArchive =
                            AppleSingle.OpenArchive(adfStream, parms.AppHook)) {
                        IFileEntry adfEntry = adfArchive.GetFirstEntry();
                        if (adfEntry.FileType != tweaked.FileType ||
                                adfEntry.AuxType != tweaked.AuxType ||
                                adfEntry.HFSFileType != tweaked.HFSFileType ||
                                adfEntry.HFSCreator != tweaked.HFSCreator) {
                            throw new Exception("MacZip type info doesn't match");
                        }
                        if (adfEntry.CreateWhen != tweaked.CreateWhen ||
                                adfEntry.ModWhen != tweaked.ModWhen ||
                                adfEntry.Access != tweaked.Access) {
                            throw new Exception("MacZip secondary attrs don't match");
                        }
                        if (adfEntry.RsrcLength != tweaked.RsrcLength) {
                            throw new Exception("MacZip resource length doesn't match");
                        }
                    }
                }
            }

            // Clean up.
            File.Delete(hfsZipPath);
        }

        /// <summary>
        /// Tests copying with an ext-archive specification.
        /// </summary>
        public static void TestExtArchive(ParamsBag parms) {
            // Create the disk images and format them for ProDOS.
            string inPath = Path.Combine(Controller.TEST_TMP, PRODOS_TEST_NAME);
            string srcPath = Path.Combine(Controller.TEST_TMP, "ext-src.po");
            string dstPath = Path.Combine(Controller.TEST_TMP, "ext-dst.po");
            if (!DiskUtil.HandleCreateDiskImage("cdi",
                    new string[] { srcPath, "140k", "prodos" }, parms)) {
                throw new Exception("cdi " + srcPath + " failed");
            }
            if (!DiskUtil.HandleCreateDiskImage("cdi",
                    new string[] { dstPath, "140k", "prodos" }, parms)) {
                throw new Exception("cdi " + dstPath + " failed");
            }

            // Full-entry copying should happen even with --no-recurse.
            parms.Recurse = false;
            parms.StripPaths = false;

            // Copy the matrix test disk onto both volumes.
            if (!Copy.HandleCopy("cp", new string[] { inPath, srcPath }, parms)) {
                throw new Exception("copy " + inPath + " to " + srcPath + " failed");
            }
            if (!Copy.HandleCopy("cp", new string[] { inPath, dstPath }, parms)) {
                throw new Exception("copy " + inPath + " to " + dstPath + " failed");
            }

            // We now have a file called "Dir.1:Dir.2:All.Attrs.Dat".  We want to copy
            // "Dir.2:All.Attrs.Dat" into the "Dir.2" directory on the destination volume,
            // which should leave us with "Dir.1:Dir.2:Dir.2:All.Attrs.Dat".
            string extArchive1 = inPath + ExtArchive.SPLIT_CHAR + "Dir.1";
            string extArchive2 = dstPath + ExtArchive.SPLIT_CHAR + "Dir.1:Dir.2";
            string[] copyArgs = new string[] { extArchive1, "Dir.2:All.Attrs.Dat", extArchive2 };
            if (!Copy.HandleCopy("cp", copyArgs, parms)) {
                throw new Exception("ext-archive copy failed");
            }

            // Try a malformed command, to be sure it fails nicely.  A simple mistake is to
            // include the file to be copied in the ext-archive spec.
            copyArgs = new string[] { extArchive1 + ":Dir.2:All.Attrs.Dat", extArchive2 };
            if (Copy.HandleCopy("cp", copyArgs, parms)) {
                throw new Exception("bad ext-archive copy succeeded");
            }

            // Do another, this time with path stripping.  Copy "zero/length/Z.resource" to
            // the "Dir.1" directory.
            parms.StripPaths = true;
            extArchive1 = inPath + ExtArchive.SPLIT_CHAR + "zero";
            extArchive2 = dstPath + ExtArchive.SPLIT_CHAR + "Dir.1";
            copyArgs = new string[] { extArchive1, "length/Z.resource", extArchive2 };
            if (!Copy.HandleCopy("cp", copyArgs, parms)) {
                throw new Exception("ext-archive copy failed");
            }

            using (FileStream stream = new FileStream(dstPath, FileMode.Open)) {
                using (IDiskImage disk = UnadornedSector.OpenDisk(stream, parms.AppHook)) {
                    disk.AnalyzeDisk();
                    IFileSystem fs = (IFileSystem)disk.Contents!;
                    fs.PrepareFileAccess(true);
                    IFileEntry volDir = fs.GetVolDirEntry();

                    IFileEntry entry = LocateFileEntry(fs, "Dir.1/Dir.2/Dir.2/All.Attrs.Dat");
                    if (entry.DataLength != sFileFull.DataLength ||
                            entry.RsrcLength != sFileFull.RsrcLength) {
                        throw new Exception("Incorrect ext-archive All.Attrs.Dat len");
                    }

                    entry = LocateFileEntry(fs, "Dir.1/Z.resource");
                    if (entry.DataLength != sFileRsrcZero.DataLength ||
                            entry.RsrcLength != sFileRsrcZero.RsrcLength) {
                        throw new Exception("Incorrect ext-archive Z.resource len");
                    }
                }
            }


            File.Delete(srcPath);
            File.Delete(dstPath);
        }

        public static void TestEmptyDir(ParamsBag parms) {
            parms.StripPaths = false;
            parms.Recurse = true;

            string arcPath = Path.Combine(Controller.TEST_TMP, "empty-src.zip");
            string srcPath = Path.Combine(Controller.TEST_TMP, "empty-src.po");
            string dstPath = Path.Combine(Controller.TEST_TMP, "empty-dst.po");
            if (!DiskUtil.HandleCreateDiskImage("cdi",
                    new string[] { srcPath, "140k", "prodos" }, parms)) {
                throw new Exception("cdi " + srcPath + " failed");
            }
            if (!DiskUtil.HandleCreateDiskImage("cdi",
                    new string[] { dstPath, "140k", "prodos" }, parms)) {
                throw new Exception("cdi " + dstPath + " failed");
            }

            const string subdir1 = "SUBDIR1";
            const string subdir2 = "SUBDIR2";
            if (!Mkdir.HandleMkdir("mkdir", new string[] { srcPath, "subdir1" }, parms)) {
                throw new Exception("mkdir " + subdir1 + " failed");
            }
            if (!Copy.HandleCopy("cp", new string[] { srcPath, dstPath }, parms)) {
                throw new Exception("cp " + srcPath + " " + dstPath + " failed");
            }
            using (FileStream stream = new FileStream(dstPath, FileMode.Open)) {
                using (IDiskImage disk = UnadornedSector.OpenDisk(stream, parms.AppHook)) {
                    disk.AnalyzeDisk();
                    IFileSystem fs = (IFileSystem)disk.Contents!;
                    fs.PrepareFileAccess(true);
                    IFileEntry volDir = fs.GetVolDirEntry();
                    IFileEntry dirEntry = fs.FindFileEntry(volDir, subdir1);
                    if (!dirEntry.IsDirectory) {
                        throw new Exception("created file wasn't a directory");
                    }
                }
            }

            // Create a ZIP archive with two directory entries (zero-length, name ends with '/').
            const string arcName1 = subdir1 + "/";
            const string arcName2 = subdir1 + "/" + subdir2 + "/";
            using (FileStream stream = new FileStream(arcPath, FileMode.Create)) {
                using (IArchive arc = Zip.CreateArchive(parms.AppHook)) {
                    arc.StartTransaction();
                    IFileEntry dir1 = arc.CreateRecord();
                    dir1.FileName = arcName1;
                    arc.AddPart(dir1, FilePart.DataFork, Controller.CreateSimpleSource(0, 0),
                        CompressionFormat.Default);
                    IFileEntry dir2 = arc.CreateRecord();
                    dir2.FileName = arcName2;
                    arc.AddPart(dir2, FilePart.DataFork, Controller.CreateSimpleSource(0, 0),
                        CompressionFormat.Default);
                    arc.CommitTransaction(stream);
                }
            }

            // Copy the directories from the archive to the disk image.  The first directory
            // already exists, the second should cause an empty directory to be created.
            if (!Copy.HandleCopy("cp", new string[] { arcPath, dstPath }, parms)) {
                throw new Exception("cp " + srcPath + " " + dstPath + " failed");
            }

            using (FileStream stream = new FileStream(dstPath, FileMode.Open)) {
                using (IDiskImage disk = UnadornedSector.OpenDisk(stream, parms.AppHook)) {
                    disk.AnalyzeDisk();
                    IFileSystem fs = (IFileSystem)disk.Contents!;
                    fs.PrepareFileAccess(true);
                    IFileEntry volDir = fs.GetVolDirEntry();
                    IFileEntry dirEntry = LocateFileEntry(fs, arcName2);
                    if (!dirEntry.IsDirectory) {
                        throw new Exception("created file wasn't a directory");
                    }
                }
            }
        }

        private static void TestTurducken(ParamsBag parms) {
            // Make a copy of a turducken file.
            string inputFile = Path.Join(Controller.TEST_DATA, "turducken", "MultiPart.hdv");
            string tdnFile = Path.Join(Controller.TEST_TMP, "tdn-test.hdv");
            FileUtil.CopyFile(inputFile, tdnFile);
            parms.SkipSimple = true;

            // Copy a file into the gzip-compressed disk volume.
            string srcArc = Path.Combine(Controller.TEST_TMP, PRODOS_TEST_NAME);
            string extArc = tdnFile + ExtArchive.SPLIT_CHAR + "2" + ExtArchive.SPLIT_CHAR +
                "small.woz.gz";
            if (!Copy.HandleCopy("cp", new string[] { srcArc, "simple.txt", extArc }, parms)) {
                throw new Exception("cp " + srcArc + " simple.txt " + extArc + " failed");
            }

            // List the contents.
            MemoryStream stdout = Controller.CaptureConsoleOut();
            if (!Catalog.HandleList("list", new string[] { extArc }, parms)) {
                throw new Exception("list '" + extArc + "' failed");
            }
            string[] expected = new string[] {
                "hello.txt",
                "again.txt",
                "SIMPLE.TXT"
            };
            Controller.CompareLines(expected, stdout);

            // Repeat the procedure with a ZIP archive.
            extArc = tdnFile + ExtArchive.SPLIT_CHAR + "2" + ExtArchive.SPLIT_CHAR +
                "Some.Files.zip";
            if (!Copy.HandleCopy("cp", new string[] { srcArc, "simple.txt", extArc }, parms)) {
                throw new Exception("cp " + srcArc + " simple.txt " + extArc + " failed");
            }
            stdout = Controller.CaptureConsoleOut();
            if (!Catalog.HandleList("list", new string[] { extArc }, parms)) {
                throw new Exception("list '" + extArc + "' failed");
            }
            expected = new string[] {
                "file1.txt",
                "file2.txt",
                "subdir/file3.txt",
                "SIMPLE.TXT"
            };
            Controller.CompareLines(expected, stdout);
        }

        #region Matrix test

        private class MatrixItem {
            public string FileName { get; }
            public Tweaker SrcTweak { get; }

            public MatrixItem(string fileName, Tweaker srcTweak) {
                FileName = fileName;
                SrcTweak = srcTweak;
            }
        }

        private const string BINARY2_TEST_NAME = "bny-test.bny";
        private const string NUFX_TEST_NAME = "nufx-test.shk";
        private const string ZIP_TEST_NAME = "zip-test.zip";
        private const string DOS_TEST_NAME = "dos-test.do";
        private const string HFS_TEST_NAME = "hfs-test.po";
        private const string PRODOS_TEST_NAME = "prodos-test.po";

        private static MatrixItem[] sMatrixTestSet = new MatrixItem[] {
            new MatrixItem(BINARY2_TEST_NAME, TweakForBinary2),
            new MatrixItem(NUFX_TEST_NAME, TweakForNuFX),
            new MatrixItem(ZIP_TEST_NAME, TweakForZip),
            new MatrixItem(DOS_TEST_NAME, TweakForDOS),
            new MatrixItem(HFS_TEST_NAME, TweakForHFS),
            new MatrixItem(PRODOS_TEST_NAME, TweakForProDOS),
        };

        private delegate string PathFilter(string pathName);
        private delegate FileDefinition Tweaker(FileDefinition def);


        /// <summary>
        /// Tests copying of a disk or file archive to a file archive.
        /// </summary>
        private static void TestCopyToArchive(string outFileName, Tweaker dstTweak,
                ParamsBag parms) {
            string outPath = Path.Combine(Controller.TEST_TMP, outFileName);
            foreach (MatrixItem item in sMatrixTestSet) {
                // Create the archive, and copy the contents of the source into it.
                if (!ArcUtil.HandleCreateFileArchive("cfa", new string[] { outPath }, parms)) {
                    throw new Exception("cfa " + outPath + " failed");
                }
                string srcPath = Path.Combine(Controller.TEST_TMP, item.FileName);
                Console.WriteLine(" * Copying(a) " + srcPath + " to " + outPath);
                if (!Copy.HandleCopy("copy", new string[] { srcPath, outPath }, parms)) {
                    throw new Exception("copy " + srcPath + " to " + outPath + " failed");
                }

                VerifyOutput(outPath, item.SrcTweak, dstTweak, parms);

                File.Delete(outPath);
            }
        }

        /// <summary>
        /// Tests copying of a disk or file archive to a disk filesystem.
        /// </summary>
        private static void TestCopyToDisk(string outFileName, string fsName, Tweaker dstTweak,
                ParamsBag parms) {
            string outPath = Path.Combine(Controller.TEST_TMP, outFileName);
            foreach (MatrixItem item in sMatrixTestSet) {
                // Create the disk image, and copy the contents of the source into it.
                string sizeStr = (fsName == "dos") ? "140k" : "800k";
                if (!DiskUtil.HandleCreateDiskImage("cdi",
                        new string[] { outPath, sizeStr, fsName }, parms)) {
                    throw new Exception("cdi " + outPath + " failed");
                }
                string srcPath = Path.Combine(Controller.TEST_TMP, item.FileName);
                Console.WriteLine(" * Copying(d) " + srcPath + " to " + outPath);
                if (!Copy.HandleCopy("copy", new string[] { srcPath, outPath }, parms)) {
                    throw new Exception("copy " + srcPath + " to " + outPath + " failed");
                }

                VerifyOutput(outPath, item.SrcTweak, dstTweak, parms);

                File.Delete(outPath);
            }
        }

        /// <summary>
        /// Verifies that the output matches expectations.  Each definition is passed through
        /// the source file tweaker and the destination file tweaker before being compared
        /// against actual values.
        /// </summary>
        /// <remarks>
        /// <para>This tests every file in the list.  It does not verify that there are no
        /// unexpected files in the destination.</para>
        /// </remarks>
        private static void VerifyOutput(string pathName, Tweaker srcTweak,
                Tweaker dstTweak, ParamsBag parms) {
            using (FileStream stream = new FileStream(pathName, FileMode.Open, FileAccess.Read)) {
                FileAnalyzer.AnalysisResult result = FileAnalyzer.Analyze(stream,
                    Path.GetExtension(pathName), parms.AppHook, out FileKind kind,
                    out SectorOrder unused);
                if (result != FileAnalyzer.AnalysisResult.Success) {
                    throw new Exception("Failed to analyze " + pathName);
                }
                if (Defs.IsDiskImageFile(kind)) {
                    IDiskImage disk = FileAnalyzer.PrepareDiskImage(stream, kind, parms.AppHook)!;
                    using (disk) {
                        disk.AnalyzeDisk();
                        IFileSystem fs = (IFileSystem)disk.Contents!;
                        fs.PrepareFileAccess(true);

                        Console.WriteLine(" * Checking(d) " + pathName);
                        foreach (FileDefinition baseDef in sAllDefs) {
                            FileDefinition srcDef = srcTweak(baseDef);
                            FileDefinition dstDef = dstTweak(srcDef);
                            if (srcDef == NO_DEF || dstDef == NO_DEF) {
                                Console.WriteLine("  * skipping '" + baseDef.PathName + "'");
                                continue;
                            }
                            IFileEntry entry = LocateFileEntry(fs, dstDef.PathName);
                            VerifyFile(fs, entry, dstDef, "check " + pathName + " '" +
                                dstDef.PathName + "'");
                        }
                    }
                } else {
                    IArchive archive = FileAnalyzer.PrepareArchive(stream, kind, parms.AppHook)!;
                    using (archive) {
                        Console.WriteLine(" * Checking(a) " + pathName);
                        foreach (FileDefinition baseDef in sAllDefs) {
                            FileDefinition srcDef = srcTweak(baseDef);
                            FileDefinition dstDef = dstTweak(srcDef);
                            if (srcDef == NO_DEF || dstDef == NO_DEF) {
                                Console.WriteLine("  * skipping '" + baseDef.PathName + "'");
                                continue;
                            }
                            IFileEntry entry = archive.FindFileEntry(dstDef.PathName);
                            VerifyFile(archive, entry, dstDef, "check " + pathName + " '" +
                                dstDef.PathName + "'", true);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Finds a matching entry in a filesystem, walking the directory tree.
        /// </summary>
        /// <exception cref="FileNotFoundException">File was not found.</exception>
        /// <exception cref="ArgumentException">Directory entry does not reference a
        ///   directory.</exception>
        private static IFileEntry LocateFileEntry(IFileSystem fs, string pathName) {
            IFileEntry curEntry = fs.GetVolDirEntry();
            if (fs.Characteristics.IsHierarchical) {
                // Use RemoveEmptyEntries to remove trailing '/' from ZIP directories.
                string[] paths = pathName.Split(fs.Characteristics.DirSep,
                    StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < paths.Length; i++) {
                    curEntry = fs.FindFileEntry(curEntry, paths[i]);
                }
                return curEntry;
            } else {
                return fs.FindFileEntry(curEntry, pathName);
            }
        }

        private static void VerifyFile(IArchive archive, IFileEntry entry, FileDefinition expected,
        string label, bool doTestFiles) {
            VerifyAttrs(entry, expected, label);
            if (!doTestFiles) {
                return;
            }
            if (expected.DataType == ContentType.Unknown) {
                if (entry.HasDataFork && entry.DataLength > 0) {
                    throw new Exception(label + ": unexpected data fork");
                }
            } else {
                MemoryStream expStream = new MemoryStream();
                GenerateContents(expStream, expected.DataType, expected.DataValue,
                    expected.DataLength);
                expStream.Position = 0;
                using (Stream actStream = archive.OpenPart(entry, FilePart.DataFork)) {
                    if (CompareStreams(expStream, actStream) != FileCmpResult.Match) {
                        throw new Exception(label + ": data stream content mismatch");
                    }
                }
            }

            if (expected.RsrcType == ContentType.Unknown) {
                if (entry.HasRsrcFork && entry.RsrcLength > 0) {
                    throw new Exception(label + ": unexpected rsrc fork");
                }
            } else {
                MemoryStream expStream = new MemoryStream();
                GenerateContents(expStream, expected.RsrcType, expected.RsrcValue,
                    expected.RsrcLength);
                expStream.Position = 0;
                using (Stream actStream = archive.OpenPart(entry, FilePart.RsrcFork)) {
                    if (CompareStreams(expStream, actStream) != FileCmpResult.Match) {
                        throw new Exception(label + ": rsrc stream content mismatch");
                    }
                }
            }
        }

        private static void VerifyFile(IFileSystem fs, IFileEntry entry, FileDefinition expected,
                string label) {
            VerifyAttrs(entry, expected, label);
            if (expected.DataType == ContentType.Unknown) {
                if (entry.HasDataFork && entry.DataLength > 0) {
                    throw new Exception(label + ": unexpected data fork");
                }
            } else {
                MemoryStream expStream = new MemoryStream();
                GenerateContents(expStream, expected.DataType, expected.DataValue,
                    expected.DataLength);
                expStream.Position = 0;
                using (Stream actStream = fs.OpenFile(entry, FileAccessMode.ReadOnly,
                        FilePart.DataFork)) {
                    FileCmpResult res = CompareStreams(expStream, actStream);
                    if (res == FileCmpResult.Match ||
                            (res == FileCmpResult.End1 && entry is DOS_FileEntry)) {
                        // Perfect match, or this is a DOS disk and they were matching until
                        // the "expected" stream stopped.  This can happen for non-I/A/B files
                        // because they're always a multiple of 256 bytes.
                    } else {
                        throw new Exception(label + ": data stream content mismatch");
                    }
                }
            }

            if (expected.RsrcType == ContentType.Unknown) {
                if (entry.HasRsrcFork && entry.RsrcLength > 0) {
                    throw new Exception(label + ": unexpected rsrc fork");
                }
            } else {
                MemoryStream expStream = new MemoryStream();
                GenerateContents(expStream, expected.RsrcType, expected.RsrcValue,
                    expected.RsrcLength);
                expStream.Position = 0;
                using (Stream actStream = fs.OpenFile(entry, FileAccessMode.ReadOnly,
                        FilePart.RsrcFork)) {
                    if (CompareStreams(expStream, actStream) != FileCmpResult.Match) {
                        throw new Exception(label + ": rsrc stream content mismatch");
                    }
                }
            }
        }

        private static void VerifyAttrs(IFileEntry entry, FileDefinition expected, string label) {
            if (entry.FullPathName != expected.PathName) {
                throw new Exception(label + ": PathName mismatch: exp=" +
                    expected.PathName + " act=" + entry.FullPathName);
            }
            if (entry.FileType != expected.FileType) {
                throw new Exception(label + ": FileType mismatch: exp=$" +
                    expected.FileType.ToString("x2") + " act=$" + entry.FileType.ToString("x2"));
            }
            if (entry.AuxType != expected.AuxType) {
                throw new Exception(label + ": AuxType mismatch: exp=$" +
                    expected.AuxType.ToString("x4") + " act=$" + entry.AuxType.ToString("x4"));
            }
            if (entry.HFSFileType != expected.HFSFileType) {
                throw new Exception(label + ": HFSFileType mismatch: exp=$" +
                    expected.HFSFileType.ToString("x8") +
                    " act=$" + entry.HFSFileType.ToString("x8"));
            }
            if (entry.HFSCreator != expected.HFSCreator) {
                throw new Exception(label + ": HFSCreator mismatch: exp=$" +
                    expected.HFSCreator.ToString("x8") +
                    " act=$" + entry.HFSCreator.ToString("x8"));
            }
            if (entry.Access != expected.Access) {
                throw new Exception(label + ": Access mismatch: exp=$" +
                    expected.Access.ToString("x2") + " act=$" + entry.Access.ToString("x2"));
            }
            if (entry.CreateWhen != expected.CreateWhen) {
                throw new Exception(label + ": CreateWhen mismatch: exp=" +
                    expected.CreateWhen + " act=" + entry.CreateWhen);
            }
            if (entry.ModWhen != expected.ModWhen) {
                throw new Exception(label + ": ModWhen mismatch: exp=" +
                    expected.ModWhen + " act=" + entry.ModWhen);
            }
        }

        #endregion Matrix test

        #region File creators and tweakers

        private static void CreateBinary2(ParamsBag parms) {
            string pathName = Path.Combine(Controller.TEST_TMP, BINARY2_TEST_NAME);
            using (FileStream stream = new FileStream(pathName, FileMode.CreateNew)) {
                // Create Binary II archive.
                using (IArchive archive = Binary2.CreateArchive(parms.AppHook)) {
                    archive.StartTransaction();
                    foreach (FileDefinition def in sAllDefs) {
                        FileDefinition tweaked = TweakForBinary2(def);
                        AddFile(archive, tweaked, "Binary2 set", parms);
                    }
                    archive.CommitTransaction(stream);
                }
            }
        }

        private static FileDefinition TweakForBinary2(FileDefinition def) {
            if (def.DataType == ContentType.Unknown) {
                return NO_DEF;
            }
            string adjPathName = AdjustPathName(def.PathName, def.DirSep,
                Binary2.AdjustFileName, Binary2.SCharacteristics.DefaultDirSep);

            // Date range is limited, and we don't store seconds.  Convert it in and out
            // of ProDOS format to fix both issues.
            uint proCreateWhen = TimeStamp.ConvertDateTime_ProDOS(def.CreateWhen);
            uint proModWhen = TimeStamp.ConvertDateTime_ProDOS(def.ModWhen);
            DateTime fixedCreateWhen = TimeStamp.ConvertDateTime_ProDOS(proCreateWhen);
            DateTime fixedModWhen = TimeStamp.ConvertDateTime_ProDOS(proModWhen);

            // Recover ProDOS type from HFS if possible, matching GS/OS behavior.
            byte proType = def.FileType;
            ushort proAux = def.AuxType;
            if (def.FileType == 0 && def.AuxType == 0 &&
                    (def.HFSFileType != 0 || def.HFSCreator != 0)) {
                if (FileAttribs.ProDOSFromHFS(def.HFSFileType, def.HFSCreator, out byte ctype,
                        out ushort caux)) {
                    proType = ctype;
                    proAux = caux;
                }
            }
            return new FileDefinition(adjPathName, Binary2.SCharacteristics.DefaultDirSep,
                proType, proAux,
                0, 0,
                def.Access, fixedCreateWhen, fixedModWhen,
                def.DataType, def.DataValue, def.DataLength,
                ContentType.Unknown, 0, 0);
        }

        private static void CreateNuFX(ParamsBag parms) {
            string pathName = Path.Combine(Controller.TEST_TMP, NUFX_TEST_NAME);
            using (FileStream stream = new FileStream(pathName, FileMode.CreateNew)) {
                // Create NuFX archive.
                using (IArchive archive = NuFX.CreateArchive(parms.AppHook)) {
                    archive.StartTransaction();
                    foreach (FileDefinition def in sAllDefs) {
                        FileDefinition tweaked = TweakForNuFX(def);
                        AddFile(archive, tweaked, "NuFX set", parms);
                    }
                    archive.CommitTransaction(stream);
                }
            }
        }

        private static FileDefinition TweakForNuFX(FileDefinition def) {
            string adjPathName = AdjustPathName(def.PathName, def.DirSep,
                NuFX.AdjustFileName, NuFX.SCharacteristics.DefaultDirSep);
            ulong nufxCreateWhen = TimeStamp.ConvertDateTime_GS(def.CreateWhen);
            ulong nufxModWhen = TimeStamp.ConvertDateTime_GS(def.ModWhen);
            // Recover ProDOS type from HFS if possible, matching GS/OS behavior.
            byte proType = def.FileType;
            ushort proAux = def.AuxType;
            if (def.FileType == 0 && def.AuxType == 0 &&
                    (def.HFSFileType != 0 || def.HFSCreator != 0)) {
                if (FileAttribs.ProDOSFromHFS(def.HFSFileType, def.HFSCreator, out byte ctype,
                        out ushort caux)) {
                    proType = ctype;
                    proAux = caux;
                }
            }
            return new FileDefinition(adjPathName, NuFX.SCharacteristics.DefaultDirSep,
                proType, proAux,
                def.HFSFileType, def.HFSCreator,
                def.Access,
                TimeStamp.ConvertDateTime_GS(nufxCreateWhen),
                TimeStamp.ConvertDateTime_GS(nufxModWhen),
                def.DataType, def.DataValue, def.DataLength,
                def.RsrcType, def.RsrcValue, def.RsrcLength);
        }

        private static void CreateZip(ParamsBag parms) {
            string pathName = Path.Combine(Controller.TEST_TMP, ZIP_TEST_NAME);
            using (FileStream stream = new FileStream(pathName, FileMode.CreateNew)) {
                // Create ZIP archive.
                using (IArchive archive = Zip.CreateArchive(parms.AppHook)) {
                    archive.StartTransaction();
                    foreach (FileDefinition def in sAllDefs) {
                        FileDefinition tweaked = TweakForZip(def);
                        AddFile(archive, tweaked, "ZIP set", parms);
                    }
                    archive.CommitTransaction(stream);
                }
            }
        }

        private static FileDefinition TweakForZip(FileDefinition def) {
            if (def.DataType == ContentType.Unknown) {
                return NO_DEF;
            }
            string adjPathName = AdjustPathName(def.PathName, def.DirSep,
                Zip.AdjustFileName, Zip.SCharacteristics.DefaultDirSep);

            // ZIP uses MS-DOS timestamps.  Date range is limited and can't represent odd seconds.
            TimeStamp.ConvertDateTime_MSDOS(def.CreateWhen, out ushort msCDate, out ushort msCTime);
            TimeStamp.ConvertDateTime_MSDOS(def.ModWhen, out ushort msMDate, out ushort msMTime);

            return new FileDefinition(adjPathName, Zip.SCharacteristics.DefaultDirSep,
                0, 0,
                0, 0,
                FileAttribs.FILE_ACCESS_UNLOCKED,   // access flags not currently preserved
                TimeStamp.ConvertDateTime_MSDOS(msCDate, msCTime),
                TimeStamp.ConvertDateTime_MSDOS(msMDate, msMTime),
                def.DataType, def.DataValue, def.DataLength,
                ContentType.Unknown, 0, 0);
        }

        private static void CreateDOS(ParamsBag parms) {
            string pathName = Path.Combine(Controller.TEST_TMP, DOS_TEST_NAME);
            using (FileStream stream = new FileStream(pathName, FileMode.CreateNew)) {
                // Create 140K DOS disk image.
                using (IDiskImage image = UnadornedSector.CreateSectorImage(stream, 35, 16,
                        SectorOrder.DOS_Sector, parms.AppHook)) {
                    image.FormatDisk(FileSystemType.DOS33, string.Empty, 254, false, parms.AppHook);
                    IFileSystem fs = (IFileSystem)image.Contents!;
                    fs.PrepareFileAccess(true);

                    foreach (FileDefinition def in sAllDefs) {
                        FileDefinition expected = TweakForDOS(def);
                        AddFile(fs, expected, "DOS set");
                    }
                }
            }
        }

        private static FileDefinition TweakForDOS(FileDefinition def) {
            if (def.DataType == ContentType.Unknown) {
                return NO_DEF;
            }
            string adjPathName = AdjustPathName(def.PathName, def.DirSep,
                DOS_FileEntry.AdjustFileName, DOS.SCharacteristics.DirSep);

            // We can only store a subset of ProDOS types, and can't store the aux type on
            // anything but 'B' files.
            byte proType = def.FileType;
            ushort proAux = def.AuxType;
            if (def.FileType == 0 && def.AuxType == 0 &&
                    (def.HFSFileType != 0 || def.HFSCreator != 0)) {
                if (FileAttribs.ProDOSFromHFS(def.HFSFileType, def.HFSCreator, out byte ctype,
                        out ushort caux)) {
                    proType = ctype;
                    proAux = caux;
                }
            }
            if (proType != FileAttribs.FILE_TYPE_BIN) {
                proAux = 0;
            }
            switch (proType) {
                case FileAttribs.FILE_TYPE_TXT:
                case FileAttribs.FILE_TYPE_INT:
                case FileAttribs.FILE_TYPE_BAS:
                case FileAttribs.FILE_TYPE_BIN:
                case FileAttribs.FILE_TYPE_F2:
                case FileAttribs.FILE_TYPE_REL:
                case FileAttribs.FILE_TYPE_F3:
                case FileAttribs.FILE_TYPE_F4:
                    // These are fine.
                    break;
                default:
                    // Type can't be represented, use $f2 ('S').
                    proType = FileAttribs.FILE_TYPE_F2;
                    break;
            }
            // Access is either locked or unlocked.
            byte access = ((def.Access & (byte)AccessFlags.Write) != 0) ?
                (byte)ACCESS_UNLOCKED : (byte)ACCESS_LOCKED;

            return new FileDefinition(adjPathName, DOS.SCharacteristics.DirSep,
                proType, proAux,
                0, 0,
                access,
                TimeStamp.NO_DATE, TimeStamp.NO_DATE,
                def.DataType, def.DataValue, def.DataLength,
                ContentType.Unknown, 0, 0);
        }

        private static void CreateHFS(ParamsBag parms) {
            string pathName = Path.Combine(Controller.TEST_TMP, HFS_TEST_NAME);
            using (FileStream stream = new FileStream(pathName, FileMode.CreateNew)) {
                // Create 800K ProDOS disk image.
                using (IDiskImage image = UnadornedSector.CreateBlockImage(stream, 1600,
                        parms.AppHook)) {
                    image.FormatDisk(FileSystemType.HFS, "Copy Test", 0, true, parms.AppHook);
                    IFileSystem fs = (IFileSystem)image.Contents!;
                    fs.PrepareFileAccess(true);

                    foreach (FileDefinition def in sAllDefs) {
                        FileDefinition expected = TweakForHFS(def);
                        AddFile(fs, expected, "HFS set");
                    }
                }
            }
        }

        private static FileDefinition TweakForHFS(FileDefinition def) {
            string adjPathName = AdjustPathName(def.PathName, def.DirSep,
                HFS_FileEntry.AdjustFileName, HFS.SCharacteristics.DirSep);

            // We can't store the ProDOS types, but if we don't have HFS types then we can
            // encode the ProDOS types in the HFS fields.
            uint hfsType = def.HFSFileType;
            uint hfsCreator = def.HFSCreator;
            if (def.HFSFileType == 0 && def.HFSCreator == 0 &&
                    (def.FileType != 0 || def.AuxType != 0)) {
                FileAttribs.ProDOSToHFS(def.FileType, def.AuxType, out hfsType, out hfsCreator);
            }
            // Date range is limited.
            uint hfsCreateWhen = TimeStamp.ConvertDateTime_HFS(def.CreateWhen);
            uint hfsModWhen = TimeStamp.ConvertDateTime_HFS(def.ModWhen);
            // Access is either unlocked or unwritable.
            byte access = ((def.Access & (byte)AccessFlags.Write) != 0) ?
                (byte)ACCESS_UNLOCKED : (byte)ACCESS_UNWRITABLE;
            ContentType rsrcType = def.RsrcType;
            if (def.RsrcType != ContentType.Unknown && def.RsrcLength == 0) {
                // Treat zero-length resource forks as nonexistent.  This matches the "copy" logic,
                // which retains zero-length forks for NuFX/ProDOS.
                rsrcType = ContentType.Unknown;
            }
            return new FileDefinition(adjPathName, HFS.SCharacteristics.DirSep,
                0, 0,
                hfsType, hfsCreator,
                access,
                TimeStamp.ConvertDateTime_HFS(hfsCreateWhen),
                TimeStamp.ConvertDateTime_HFS(hfsModWhen),
                def.DataType, def.DataValue, def.DataLength,
                rsrcType, def.RsrcValue, def.RsrcLength);
        }

        private static void CreateProDOS(ParamsBag parms) {
            string pathName = Path.Combine(Controller.TEST_TMP, PRODOS_TEST_NAME);
            using (FileStream stream = new FileStream(pathName, FileMode.CreateNew)) {
                // Create 800K ProDOS disk image.
                using (IDiskImage image = UnadornedSector.CreateBlockImage(stream, 1600,
                        parms.AppHook)) {
                    image.FormatDisk(FileSystemType.ProDOS, "COPY.TEST", 0, true, parms.AppHook);
                    IFileSystem fs = (IFileSystem)image.Contents!;
                    fs.PrepareFileAccess(true);

                    foreach (FileDefinition def in sAllDefs) {
                        FileDefinition expected = TweakForProDOS(def);
                        AddFile(fs, expected, "ProDOS set");
                    }
                }
            }
        }

        private static FileDefinition TweakForProDOS(FileDefinition def) {
            string adjPathName = AdjustPathName(def.PathName, def.DirSep,
                ProDOS_FileEntry.AdjustFileName, ProDOS.SCharacteristics.DirSep);
            bool isExtended = def.RsrcType != ContentType.Unknown;

            // Date range is limited, and we don't store seconds.  Convert it in and out
            // of ProDOS format to fix both issues.
            uint proCreateWhen = TimeStamp.ConvertDateTime_ProDOS(def.CreateWhen);
            uint proModWhen = TimeStamp.ConvertDateTime_ProDOS(def.ModWhen);
            DateTime fixedCreateWhen = TimeStamp.ConvertDateTime_ProDOS(proCreateWhen);
            DateTime fixedModWhen = TimeStamp.ConvertDateTime_ProDOS(proModWhen);

            // Recover ProDOS type from HFS if possible, matching GS/OS behavior.
            byte proType = def.FileType;
            ushort proAux = def.AuxType;
            if (def.FileType == 0 && def.AuxType == 0 &&
                    (def.HFSFileType != 0 || def.HFSCreator != 0)) {
                if (FileAttribs.ProDOSFromHFS(def.HFSFileType, def.HFSCreator, out byte ctype,
                        out ushort caux)) {
                    proType = ctype;
                    proAux = caux;
                }
            }
            //Debug.WriteLine("HEY: " + def.FileType.ToString("x2") + "/" +
            //    def.AuxType.ToString("x4") + " - " + def.HFSFileType.ToString("x8") + "/" +
            //    def.HFSCreator.ToString("x8"));
            return new FileDefinition(adjPathName, ProDOS.SCharacteristics.DirSep,
                proType, proAux,
                isExtended ? def.HFSFileType : 0, isExtended ? def.HFSCreator : 0,
                def.Access, fixedCreateWhen, fixedModWhen,
                def.DataType, def.DataValue, def.DataLength,
                def.RsrcType, def.RsrcValue, def.RsrcLength);
        }

        #endregion File creators and tweakers


        /// <summary>
        /// Adds a file to an archive.
        /// </summary>
        private static void AddFile(IArchive archive, FileDefinition tweaked, string label,
                ParamsBag parms) {
            if (tweaked == NO_DEF) {
                return;
            }

            // Adjust the path.
            string adjPathName = AdjustPathName(tweaked.PathName, tweaked.DirSep, archive);
            IFileEntry newEntry = archive.CreateRecord();
            CopyAttributes(tweaked, adjPathName, newEntry);

            CompressionFormat fmt = parms.Compress ?
                CompressionFormat.Default : CompressionFormat.Uncompressed;

            if (tweaked.DataType != ContentType.Unknown) {
                MemoryStream sourceStream = new MemoryStream();
                GenerateContents(sourceStream, tweaked.DataType, tweaked.DataValue,
                    tweaked.DataLength);
                archive.AddPart(newEntry, FilePart.DataFork, new SimplePartSource(sourceStream),
                    fmt);
            }
            if (tweaked.RsrcType != ContentType.Unknown) {
                MemoryStream sourceStream = new MemoryStream();
                GenerateContents(sourceStream, tweaked.RsrcType, tweaked.RsrcValue,
                    tweaked.RsrcLength);
                archive.AddPart(newEntry, FilePart.RsrcFork, new SimplePartSource(sourceStream),
                    fmt);
            }

            // Confirm that newly-created file matches expectations.  We can't test the file
            // contents because the transaction hasn't been committed yet.  (Not really important
            // to do it here; if we need to we can just commit each entry individually.)
            VerifyFile(archive, newEntry, tweaked, label, false);
        }

        /// <summary>
        /// Adds a file to a filesystem.
        /// </summary>
        private static void AddFile(IFileSystem fs, FileDefinition tweaked, string label) {
            if (tweaked == NO_DEF) {
                return;
            }

            IFileEntry targetDir = fs.GetVolDirEntry();
            string adjFileName;
            if (tweaked.DirSep != IFileEntry.NO_DIR_SEP) {
                // Path has directory components.  If the filesystem is hierarchical, we need
                // to create the directories.
                string[] parts = tweaked.PathName.Split(tweaked.DirSep);
                if (fs.Characteristics.IsHierarchical) {
                    for (int i = 0; i < parts.Length - 1; i++) {
                        string adjDirName = fs.AdjustFileName(parts[i]);
                        if (fs.TryFindFileEntry(targetDir, adjDirName, out IFileEntry dirEntry)) {
                            // Directory already exists.
                            targetDir = dirEntry;
                        } else {
                            // Directory doesn't exist, create it.
                            targetDir = fs.CreateFile(targetDir, adjDirName,
                                CreateMode.Directory);
                        }
                    }
                }
                // Keep the last part as the filename.
                adjFileName = fs.AdjustFileName(parts[parts.Length - 1]);
            } else {
                // Keeping the whole thing.
                adjFileName = fs.AdjustFileName(tweaked.PathName);
            }

            // Create the file.
            CreateMode mode;
            if (!fs.Characteristics.HasResourceForks || tweaked.RsrcType == ContentType.Unknown) {
                mode = CreateMode.File;
            } else {
                mode = CreateMode.Extended;
            }
            IFileEntry newEntry = fs.CreateFile(targetDir, adjFileName, mode);
            CopyAttributes(tweaked, adjFileName, newEntry);

            if (tweaked.DataType != ContentType.Unknown) {
                using (Stream stream = fs.OpenFile(newEntry, FileAccessMode.ReadWrite,
                        FilePart.DataFork)) {
                    GenerateContents(stream, tweaked.DataType, tweaked.DataValue, tweaked.DataLength);
                }
            }
            if (tweaked.RsrcType != ContentType.Unknown) {
                using (Stream stream = fs.OpenFile(newEntry, FileAccessMode.ReadWrite,
                        FilePart.RsrcFork)) {
                    GenerateContents(stream, tweaked.RsrcType, tweaked.RsrcValue, tweaked.RsrcLength);
                }
            }

            // Confirm that newly-created file matches expectations.
            VerifyFile(fs, newEntry, tweaked, label);
        }

        private static void CopyAttributes(FileDefinition def, string adjFileName,
                IFileEntry newEntry) {
            newEntry.FileName = adjFileName;
            newEntry.FileType = def.FileType;
            newEntry.AuxType = def.AuxType;
            newEntry.HFSFileType = def.HFSFileType;
            newEntry.HFSCreator = def.HFSCreator;
            newEntry.Access = def.Access;
            newEntry.CreateWhen = def.CreateWhen;
            newEntry.ModWhen = def.ModWhen;
            newEntry.SaveChanges();
        }

        /// <summary>
        /// Generates the contents of a data stream.
        /// </summary>
        /// <param name="stream">Stream to write to.</param>
        /// <param name="type">Type of data to write.</param>
        /// <param name="value">Value to use for the bytes or for the random seed.</param>
        /// <param name="count">Number of bytes to write.</param>
        private static void GenerateContents(Stream stream, ContentType type, int value,
                int count) {
            switch (type) {
                case ContentType.SingleByte:
                    for (int i = 0; i < count; i++) {
                        stream.WriteByte((byte)value);
                    }
                    break;
                case ContentType.Random:
                    Random rnd = new Random(value);
                    for (int i = 0; i < count; i++) {
                        stream.WriteByte((byte)rnd.Next(256));
                    }
                    break;
                case ContentType.Unknown:
                default:
                    throw new Exception("Bad ContentType: " + type);
            }
        }

        private enum FileCmpResult { Unknown = 0, Match, End1, End2, Mismatch };

        /// <summary>
        /// Compares the contents of two streams.  The streams must be positioned at the start,
        /// but do not need to be seekable.
        /// </summary>
        private static FileCmpResult CompareStreams(Stream stream1, Stream stream2) {
            int offset = 0;
            while (true) {
                int i1 = stream1.ReadByte();
                int i2 = stream2.ReadByte();
                if (i1 != i2) {
                    if (i1 < 0) {
                        // Stream 1 ended early.
                        return FileCmpResult.End1;
                    }
                    if (i2 < 0) {
                        // Stream 2 ended early.
                        return FileCmpResult.End2;
                    }
                    Console.WriteLine("Stream mismatch at " + offset + ": s1=0x" +
                        i1.ToString("x2") + " s2=0x" + i2.ToString("x2"));
                    return FileCmpResult.Mismatch;
                }
                if (i1 == -1) {
                    // Both streams ended, perfect match.
                    return FileCmpResult.Match;
                }
                offset++;
            }
        }

        /// <summary>
        /// Adjusts a pathname, using the specified filter.  Directory separators will be
        /// replaced with a new value.
        /// </summary>
        private static string AdjustPathName(string pathName, char dirSep, PathFilter filter,
                char outDirSep) {
            if (dirSep != IFileEntry.NO_DIR_SEP) {
                string[] parts = pathName.Split(dirSep);
                if (outDirSep == IFileEntry.NO_DIR_SEP) {
                    // Output is non-hierarchical, keep last part only.
                    return filter(parts[parts.Length - 1]);
                } else {
                    StringBuilder sb = new StringBuilder(pathName.Length);
                    for (int i = 0; i < parts.Length; i++) {
                        sb.Append(filter(parts[i]));
                        if (i != parts.Length - 1) {
                            sb.Append(outDirSep);
                        }
                    }
                    return sb.ToString();
                }
            } else {
                // Input is non-hierarchical, keep whole thing.
                return filter(pathName);
            }
        }

        /// <summary>
        /// Adjusts a pathname for the specified archive.
        /// </summary>
        private static string AdjustPathName(string pathName, char dirSep, IArchive archive) {
            if (dirSep != IFileEntry.NO_DIR_SEP) {
                StringBuilder sb = new StringBuilder(pathName.Length);
                string[] parts = pathName.Split(dirSep);
                for (int i = 0; i < parts.Length; i++) {
                    sb.Append(archive.AdjustFileName(parts[i]));
                    if (i != parts.Length - 1) {
                        sb.Append(archive.Characteristics.DefaultDirSep);
                    }
                }
                return sb.ToString();
            } else {
                return archive.AdjustFileName(pathName);
            }
        }

        private enum ContentType { Unknown = 0, SingleByte, Random };

        private class FileDefinition {
            //
            // Attributes.
            //

            public string PathName { get; }        // partial path
            public char DirSep { get; }
            public byte FileType { get; }
            public ushort AuxType { get; }
            public uint HFSFileType { get; }
            public uint HFSCreator { get; }
            public byte Access { get; }
            public DateTime CreateWhen { get; }
            public DateTime ModWhen { get; }

            //
            // Content.
            //

            public ContentType DataType { get; }
            public int DataValue { get; }
            public int DataLength { get; }
            public ContentType RsrcType { get; }
            public int RsrcValue { get; }
            public int RsrcLength { get; }

            /// <summary>
            /// Constructor.
            /// </summary>
            public FileDefinition(string pathName, char dirSep, byte fileType, ushort auxType,
                    uint hfsFileType, uint hfsCreator, byte access,
                    DateTime createWhen, DateTime modWhen,
                    ContentType dataType, int dataValue, int dataLength,
                    ContentType rsrcType, int rsrcValue, int rsrcLength) {
                PathName = pathName;
                DirSep = dirSep;
                FileType = fileType;
                AuxType = auxType;
                HFSFileType = hfsFileType;
                HFSCreator = hfsCreator;
                Access = access;
                CreateWhen = createWhen;
                ModWhen = modWhen;
                DataType = dataType;
                DataValue = dataValue;
                DataLength = dataLength;
                RsrcType = rsrcType;
                RsrcValue = rsrcValue;
                RsrcLength = rsrcLength;
            }

            public override string ToString() {
                return "[FileDef: " + PathName + "]";
            }
        }

        // Special definition used to mean "no definition".  Not included in test set.
        private static readonly FileDefinition NO_DEF = new FileDefinition(
            "<NO DEFINITION>", IFileEntry.NO_DIR_SEP, 0x00, 0x0000,
            0, 0,
            (byte)0,
            TimeStamp.NO_DATE, TimeStamp.NO_DATE,
            ContentType.Unknown, 0, 0, ContentType.Unknown, 0, 0);

        // Zero-length typeless file.
        private static readonly FileDefinition sFileMinimal = new FileDefinition(
            "MINIMAL", IFileEntry.NO_DIR_SEP, 0x00, 0x0000,
            0, 0,
            (byte)0,
            TimeStamp.NO_DATE, TimeStamp.NO_DATE,
            ContentType.SingleByte, 0, 0, ContentType.Unknown, 0, 0);

        // Simple text file.  Need to disable DOS text conversion.
        private static readonly FileDefinition sFileSimple = new FileDefinition(
            "SIMPLE.TXT", IFileEntry.NO_DIR_SEP, FileAttribs.FILE_TYPE_TXT, 0x0000,
            0, 0,
            (byte)ACCESS_UNLOCKED,
            TimeStamp.NO_DATE, TimeStamp.NO_DATE,
            ContentType.SingleByte, 0x2a, 2000, ContentType.Unknown, 0, 0);

        // File with all the trimmings.
        private static readonly FileDefinition sFileFull = new FileDefinition(
            "Dir-1/Dir-2/All_Attrs.dat", '/', FileAttribs.FILE_TYPE_BIN, 0x2000,
            MacChar.IntifyMacConstantString("HTYP"), MacChar.IntifyMacConstantString("HCRE"),
            (byte)ACCESS_UNWRITABLE,
            new DateTime(1977, 6, 1, 1, 2, 3), new DateTime(1986, 9, 15, 4, 5, 6),
            ContentType.Random, 12345, 2500, ContentType.SingleByte, 0xee, 3000);

        // File with name that is invalid on every known filesystem and archive.  DOS dislikes
        // commas and can't store trailing spaces.
        private static readonly FileDefinition sFileIllegalName = new FileDefinition(
            @",W/X:Y\Z ", IFileEntry.NO_DIR_SEP, FileAttribs.FILE_TYPE_BIN, 0x1000,
            0, 0,
            (byte)ACCESS_LOCKED,
            TimeStamp.NO_DATE, new DateTime(1986, 9, 15, 4, 5, 6),
            ContentType.Random, 5678, 2200, ContentType.Unknown, 0, 0);

        // File with 'pdos' type in HFS fields.  Content is zeroed to test sparse behavior.
        private static readonly FileDefinition sFileProTypes = new FileDefinition(
            "Dir/1:Dir/2:!Confusion", ':', 0x00, 0x0000,
            0x70061234, FileAttribs.CREATOR_PDOS,     // BIN $1234
            (byte)ACCESS_UNLOCKED,
            new DateTime(1977, 6, 1, 1, 2, 3), TimeStamp.NO_DATE,
            ContentType.SingleByte, 0x00, 4200, ContentType.Unknown, 0, 0);

        // File that only has a resource fork.  Most formats can't (or won't) represent this.
        private static readonly FileDefinition sFileRsrcOnly = new FileDefinition(
            "ResourceOnlyAndHasAVeryLongFileName", IFileEntry.NO_DIR_SEP, 0x00, 0x0000,
            0, 0,
            (byte)0,
            new DateTime(1977, 6, 1, 1, 2, 3), new DateTime(1986, 9, 15, 4, 5, 6),
            ContentType.Unknown, 0, 0, ContentType.SingleByte, 0x2a, 2000);

        // Zero-length resource fork.  On HFS this is considered to not have a resource fork,
        // on ProDOS this is an extended file for which the resource fork is empty.  Data fork
        // length must be a multiple of 256 or DOS can't store it correctly.
        private static readonly FileDefinition sFileRsrcZero = new FileDefinition(
            @"zero\length\Z.resource", '\\', 0xe0, 0x5678,
            0, 0,
            (byte)ACCESS_UNLOCKED,
            new DateTime(1977, 6, 1, 1, 2, 3), new DateTime(1986, 9, 15, 4, 5, 6),
            ContentType.Random, 111, 1536, ContentType.SingleByte, 0x00, 0);

        private static readonly FileDefinition[] sAllDefs = new FileDefinition[] {
            sFileMinimal,
            sFileSimple,
            sFileFull,
            sFileIllegalName,
            sFileProTypes,
            sFileRsrcOnly,
            sFileRsrcZero,
        };
    }
}
