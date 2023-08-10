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
using System.Diagnostics;
using System.IO.Compression;
using System.Text;

using CommonUtil;
using DiskArc;
using static DiskArc.Defs;

namespace DiskArcTests {
    internal static class Helper {
        private const int MAX_MEM_FILE = 32 * 1024 * 1024;  // 32MB
        private const string TEST_DATA_DIR = "TestData";

        // @"C:\Src\CiderPress2\TestData\";
        public static string DebugFileDir { get; set; } = string.Empty;

        /// <summary>
        /// <para>File attributes, used to form a set of expectations against which the actual
        /// contents will be compared.  Because most filesystems are case-preserving, name
        /// comparisons are case-sensitive.</para>
        ///
        /// <para>The list may be sorted by filename, for the benefit of filesystems that
        /// automatically sort their files (e.g. HFS).</para>
        ///
        /// <para>Empty strings and -1 are used to indicate "don't care" values.</para>
        /// </summary>
        public class FileAttr : IComparable<FileAttr> {
            public string FileName { get; }
            public int DataLength { get; }
            public int RsrcLength { get; }
            public int StorageSize { get; }
            public int FileType { get; }
            public int AuxType { get; }

            public FileAttr(string fileName, int dataLength, int storageSize) :
                    this(fileName, dataLength, -1, storageSize, -1, -1) {
            }
            public FileAttr(string fileName, int dataLength, int rsrcLength, int storageSize,
                    int fileType, int auxType) {
                FileName = fileName;
                DataLength = dataLength;
                RsrcLength = rsrcLength;
                StorageSize = storageSize;
                FileType = fileType;
                AuxType = auxType;
            }

            public int CompareTo(FileAttr? other) {
                if (other == null) {
                    return 1;
                }
                return string.Compare(FileName, other.FileName);
            }

            public override string ToString() {
                return "[Attr: '" + FileName + "' dl=" + DataLength + " rl=" + RsrcLength + "]";
            }
        }

        /// <summary>
        /// Opens a file from the TestData directory.  If read-only access is requested, the
        /// file is accessed directly from disk, otherwise the file is read into memory.
        /// </summary>
        /// <param name="fileName">Name of file to open.</param>
        /// <param name="isReadOnly">True if returned stream will not be written to.</param>
        /// <returns>File or memory stream.</returns>
        /// <exception cref="IOException">Thrown on failure.</exception>
        public static Stream OpenTestFile(string fileName, bool isReadOnly, AppHook appHook) {
            string basePath = appHook.GetOption(DAAppHook.LIB_TEST_ROOT, string.Empty);
            string pathName = Path.Join(basePath, TEST_DATA_DIR, fileName);
            if (File.Exists(pathName)) {
                if (isReadOnly) {
                    // Just use original.
                    return new FileStream(pathName, FileMode.Open, FileAccess.Read);
                } else {
                    // Load it into memory.
                    using (FileStream stream = File.Open(pathName, FileMode.Open, FileAccess.Read,
                            FileShare.Read)) {
                        if (stream.Length > MAX_MEM_FILE) {
                            throw new IOException("File too large");
                        }
                        MemoryStream outStream = new MemoryStream();
                        stream.CopyTo(outStream);
                        return outStream;
                    }
                }
            } else if (File.Exists(pathName + ".gz")) {
                // File is compressed, read it in with gzip.
                using (FileStream stream = File.Open(pathName + ".gz", FileMode.Open,
                        FileAccess.Read, FileShare.Read)) {
                    MemoryStream outStream = new MemoryStream();
                    GZipStream gzStream = new GZipStream(stream, CompressionMode.Decompress);
                    gzStream.CopyTo(outStream);
                    return outStream;
                }
            } else {
                throw new FileNotFoundException("File not found: '" + fileName + "'");
            }
        }

        /// <summary>
        /// Creates a formatted block image in a memory stream, and prepares it for file access.
        /// </summary>
        /// <remarks>
        /// The Stream is returned, but it's not necessary to explicitly Dispose() it, because
        /// it has no unmanaged resources.
        /// </remarks>
        public static IFileSystem CreateTestImage(string volumeName, FileSystemType fsType,
                uint blocks, AppHook appHook, out MemoryStream memStream) {
            uint size = blocks * BLOCK_SIZE;
            CreateParams parms = new CreateParams() {
                FSType = fsType,
                VolumeName = volumeName,
                Size = size
            };

            byte[] buffer = new byte[size];
            memStream = new MemoryStream(buffer);
            IFileSystem fs = CreateNewFS(memStream, parms, appHook);
            fs.PrepareFileAccess(true);
            return fs;
        }

        /// <summary>
        /// Creates a formatted sector image in a memory stream, and prepares it for file access.
        /// </summary>
        /// <remarks>
        /// The Stream is returned, but it's not necessary to explicitly Dispose() it, because
        /// it has no unmanaged resources.
        /// </remarks>
        public static IFileSystem CreateTestImage(string volumeName, FileSystemType fsType,
                uint tracks, uint sectors, byte volumeNum, bool makeBootable,
                AppHook appHook, out MemoryStream memStream) {
            uint size = tracks * sectors * SECTOR_SIZE;
            CreateParams parms = new CreateParams() {
                FSType = fsType,
                Size = size,
                VolumeName = volumeName,
                VolumeNum = volumeNum,
                SectorsPerTrack = sectors,
                MakeBootable = makeBootable,
                FileOrder = SectorOrder.DOS_Sector,
            };

            byte[] buffer = new byte[size];
            memStream = new MemoryStream(buffer, 0, (int)size);
            IFileSystem fs = CreateNewFS(memStream, parms, appHook);
            fs.PrepareFileAccess(true);
            return fs;
        }

        /// <summary>
        /// Verifies that the contents of a directory exactly match expectations.  We must
        /// find the same number of files, in the same order.
        /// </summary>
        /// <param name="dirEntry"></param>
        /// <param name="attrs"></param>
        /// <exception cref="ArgumentException"></exception>
        public static void ValidateDirContents(IFileEntry dirEntry, List<FileAttr> attrs) {
            if (!dirEntry.IsDirectory) {
                throw new ArgumentException();
            }

            ValidateContents(dirEntry, attrs);
        }

        public static void ValidateContents(IEnumerable<IFileEntry> dirEntry,
                List<FileAttr> attrs) {
            List<IFileEntry> children = dirEntry.ToList();
            if (children.Count != attrs.Count) {
                throw new Exception("Directory contents don't match: dir count=" +
                    children.Count + ", expected " + attrs.Count);
            }
            for (int i = 0; i < attrs.Count; i++) {
                IFileEntry cmpEntry = children[i];
                if (!string.IsNullOrEmpty(attrs[i].FileName) &&
                        cmpEntry.FileName != attrs[i].FileName) {
                    throw new Exception("FileName mismatch: found '" + cmpEntry.FileName +
                        "', expected '" + attrs[i].FileName + "'");
                }
                if (attrs[i].DataLength >= 0 && cmpEntry.DataLength != attrs[i].DataLength) {
                    throw new Exception("DataLength mismatch: found " + cmpEntry.DataLength +
                        ", expected " + attrs[i].DataLength + ": " + cmpEntry.FullPathName);
                }
                if (attrs[i].RsrcLength >= 0 && cmpEntry.RsrcLength != attrs[i].RsrcLength) {
                    throw new Exception("RsrcLength mismatch: found " + cmpEntry.RsrcLength +
                        ", expected " + attrs[i].RsrcLength + ": " + cmpEntry.FullPathName);
                }
                if (attrs[i].StorageSize >= 0 && cmpEntry.StorageSize != attrs[i].StorageSize) {
                    throw new Exception("StorageSize mismatch: found " + cmpEntry.StorageSize +
                        ", expected " + attrs[i].StorageSize + ": " + cmpEntry.FullPathName);
                }
                if (attrs[i].FileType >= 0 && cmpEntry.FileType != attrs[i].FileType) {
                    throw new Exception("FileType mismatch: found " + cmpEntry.FileType +
                        ", expected " + attrs[i].FileType + ": " + cmpEntry.FullPathName);
                }
                if (attrs[i].AuxType >= 0 && cmpEntry.AuxType != attrs[i].AuxType) {
                    throw new Exception("AuxType mismatch: found " + cmpEntry.AuxType +
                        ", expected " + attrs[i].AuxType + ": " + cmpEntry.FullPathName);
                }
            }
        }

        /// <summary>
        /// Compares the contents of a file in the archive to a data stream.  The stream does
        /// not need to be seekable.  "tmpBuf" must be big enough to hold the uncompressed file.
        /// Throws an exception if they don't match.
        /// </summary>
        public static void CompareFileToStream(IArchive archive, string fileName,
                FilePart part, byte[] expectBuf, long expectLen, byte[] tmpBuf) {
            IFileEntry entry = archive.FindFileEntry(fileName);
            using Stream arcStream = archive.OpenPart(entry, part);
            // we're assuming Read() won't return short for this
            int actual = arcStream.Read(tmpBuf, 0, tmpBuf.Length);
            Debug.Assert(actual != tmpBuf.Length || actual == expectLen);
            if (actual != expectLen) {
                throw new Exception("Length mismatch on " + fileName + ": expected=" +
                    expectLen + ", actual=" + actual);
            }
            if (!RawData.CompareBytes(expectBuf, 0, tmpBuf, 0, actual)) {
                throw new Exception("Content mismatch on " + fileName);
            }
        }

        /// <summary>
        /// Generates a sequential repeating byte test pattern (0x00, 0x01, 0x02, ...).  The
        /// pattern uses 0x00-fe, not 0xff, so that the pattern isn't block-aligned.  The
        /// pattern resets every 8KB.
        /// </summary>
        public static byte[] SeqByteTestPattern(int count) {
            byte[] buffer = new byte[count];
            byte val = 0;
            for (int i = 0; i < count; i++) {
                if ((i & 0x1fff) == 0) {
                    val = 0;
                } else if (val == 255) {
                    val = 0;
                }
                buffer[i] = val++;
            }
            return buffer;
        }

        /// <summary>
        /// Reads the full contents of a file into a buffer, starting from the current file
        /// offset.  The amount of data read on each call alternates between two values to
        /// exercise the read routine.
        /// </summary>
        /// <returns>The total amount of data read.</returns>
        public static int ReadFileChunky(DiskFileStream desc, byte[] buffer,
                int count1, int count2) {
            int dataRead = 0;
            while (true) {
                int count = count1;
                if (dataRead + count > buffer.Length) {
                    count = buffer.Length - dataRead;
                }
                int actual = desc.Read(buffer, dataRead, count);
                dataRead += actual;
                if (actual == 0) {
                    break;
                }

                count = count2;
                if (dataRead + count > buffer.Length) {
                    count = buffer.Length - dataRead;
                }
                actual = desc.Read(buffer, dataRead, count);
                dataRead += actual;
                if (actual == 0) {
                    break;
                }
            }
            return dataRead;
        }

        /// <summary>
        /// Creates a buffer, filled with a single value.
        /// </summary>
        /// <param name="value">Value to fill with.</param>
        /// <returns>New buffer.</returns>
        public static byte[] CreateFilledBlock(byte value, int size) {
            byte[] blk = new byte[size];
            for (int i = 0; i < size; i++) {
                blk[i] = value;
            }
            return blk;
        }

        /// <summary>
        /// Fills a file with data, in 512-byte-block increments.
        /// </summary>
        /// <param name="fs">Filesystem object.</param>
        /// <param name="entry">File entry object.</param>
        /// <param name="value">Byte value to use for filler.</param>
        /// <param name="dataBlocks">Number of data fork blocks to write.</param>
        /// <param name="rsrcBlocks">Number of rsrc fork blocks to write.</param>
        public static void PopulateFile(IFileSystem fs, IFileEntry entry, byte value,
                int dataBlocks, int rsrcBlocks) {
            byte[] block = CreateFilledBlock(value, BLOCK_SIZE);
            if (dataBlocks > 0) {
                using (DiskFileStream fd = fs.OpenFile(entry, IFileSystem.FileAccessMode.ReadWrite,
                        FilePart.DataFork)) {
                    while (dataBlocks-- != 0) {
                        fd.Write(block, 0, BLOCK_SIZE);
                    }
                }
            }
            if (rsrcBlocks > 0) {
                using (DiskFileStream fd = fs.OpenFile(entry, IFileSystem.FileAccessMode.ReadWrite,
                        FilePart.RsrcFork)) {
                    while (rsrcBlocks-- != 0) {
                        fd.Write(block, 0, BLOCK_SIZE);
                    }
                }
            }
        }

        /// <summary>
        /// Verifies the contents of a file populated with a single byte value.
        /// </summary>
        /// <param name="fs">Filesystem object.</param>
        /// <param name="entry">File entry object.</param>
        /// <param name="value">Byte value to use for filler.</param>
        /// <param name="dataBlocks">Number of data fork blocks to expect.</param>
        /// <param name="rsrcBlocks">Number of rsrc fork blocks to expect.</param>
        public static void CheckPopulatedFile(IFileSystem fs, IFileEntry entry, byte value,
                int dataBlocks, int rsrcBlocks) {
            byte[] block = CreateFilledBlock(value, BLOCK_SIZE);
            byte[] readBuf = new byte[BLOCK_SIZE];
            if (dataBlocks > 0) {
                using (DiskFileStream fd = fs.OpenFile(entry, IFileSystem.FileAccessMode.ReadOnly,
                        FilePart.DataFork)) {
                    while (dataBlocks-- != 0) {
                        fd.ReadExactly(readBuf, 0, BLOCK_SIZE);
                        if (RawData.MemCmp(readBuf, block, BLOCK_SIZE) != 0) {
                            throw new Exception("mismatch in data fork");
                        }
                    }
                    if (fd.ReadByte() != -1) {
                        throw new Exception("data fork too long");
                    }
                }
            }
            if (rsrcBlocks > 0) {
                using (DiskFileStream fd = fs.OpenFile(entry, IFileSystem.FileAccessMode.ReadOnly,
                        FilePart.RsrcFork)) {
                    while (rsrcBlocks-- != 0) {
                        fd.ReadExactly(readBuf, 0, BLOCK_SIZE);
                        if (RawData.MemCmp(readBuf, block, BLOCK_SIZE) != 0) {
                            throw new Exception("mismatch in rsrc fork");
                        }
                    }
                    if (fd.ReadByte() != -1) {
                        throw new Exception("rsrc fork too long");
                    }
                }
            }
        }

        public static void ExpectLong(long expected, long actual, string desc) {
            if (expected != actual) {
                throw new Exception(desc + ": expected=" + expected + ", actual=" + actual);
            }
        }
        public static void ExpectInt(int expected, int actual, string desc) {
            if (expected != actual) {
                throw new Exception(desc + ": expected=" + expected + ", actual=" + actual);
            }
        }
        public static void ExpectUInt(uint expected, uint actual, string desc) {
            if (expected != actual) {
                throw new Exception(desc + ": expected=" + expected + ", actual=" + actual);
            }
        }
        public static void ExpectByte(byte expected, byte actual, string desc) {
            if (expected != actual) {
                throw new Exception(desc + ": expected=" + expected + " '" + (char)expected +
                    "', actual=" + actual + " '" + (char)actual + "'");
            }
        }
        public static void ExpectBool(bool expected, bool actual, string desc) {
            if (expected != actual) {
                throw new Exception(desc + ": expected=" + expected + ", actual=" + actual);
            }
        }

        public static void ExpectString(string? expected, string? actual, string desc) {
            if (expected != actual) {
                throw new Exception(desc + ": expected='" + expected +
                    "', actual='" + actual + "'");
            }
        }

        public static void CheckNotes(IArchive arc, int expWarn, int expErr) {
            Notes notes = arc.Notes;
            DoCheckNotes(notes, expWarn, expErr);
        }
        public static void CheckNotes(IFileSystem fs, int expWarn, int expErr) {
            Notes notes = fs.Notes;
            DoCheckNotes(notes, expWarn, expErr);
        }
        public static void CheckNotes(IDiskImage di, int expWarn, int expErr) {
            Notes notes = di.Notes;
            DoCheckNotes(notes, expWarn, expErr);
        }
        private static void DoCheckNotes(Notes notes, int expWarn, int expErr) {
            if (notes.WarningCount != expWarn || notes.ErrorCount != expErr) {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("Expected warn/err=" + expWarn + "/" + expErr +
                    ", actual=" + notes.WarningCount + "/" + notes.ErrorCount);
                sb.Append(notes.ToString());

                throw new Exception(sb.ToString());
            }
        }

        /// <summary>
        /// Performs a simple disk check.  Useful for prefab disk tests.  Compares the number
        /// of files found to the expected value, and scans for filesystem errors and unreadable
        /// blocks.
        /// </summary>
        /// <param name="pathName">Pathname of file on disk.</param>
        /// <param name="expKind">Expected file kind.</param>
        /// <param name="expFileCount">Expected number of files in root directory.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <exception cref="Exception">Something went wrong.</exception>
        public static void SimpleDiskCheck(string pathName, FileKind expKind, int expFileCount,
                AppHook appHook) {
            using (Stream dataFile = OpenTestFile(pathName, true, appHook)) {
                using (IDiskImage? diskImage = FileAnalyzer.PrepareDiskImage(dataFile, expKind,
                        appHook)) {
                    if (diskImage == null) {
                        throw new Exception("Failed to prepare disk image (name='" + pathName +
                            "' expKind=" + expKind + ")");
                    }
                    // Should be no complaints about the disk image.
                    CheckNotes(diskImage, 0, 0);

                    // Find the filesystem and check that too.
                    diskImage.AnalyzeDisk();
                    IFileSystem fs = (IFileSystem)diskImage.Contents!;
                    fs.PrepareFileAccess(true);
                    CheckNotes(fs, 0, 0);

                    List<IFileEntry> entries = fs.GetVolDirEntry().ToList();
                    if (entries.Count != expFileCount) {
                        throw new Exception("Incorrect file count: expected=" + expFileCount +
                            ", actual=" + entries.Count);
                    }

                    fs.PrepareRawAccess();
                    if (fs.RawAccess.CountUnreadableChunks() != 0) {
                        throw new Exception("Found unreadable blocks");
                    }
                }
            }
        }

        /// <summary>
        /// Calls EntryFromPath(), throwing an exception if the file isn't found.
        /// (EntryFromPath now throws, making this unnecessary.)
        /// </summary>
        public static IFileEntry EntryFromPathThrow(IFileSystem fs, string path, char fssep) {
            IFileEntry result = fs.EntryFromPath(path, fssep);
            if (result == IFileEntry.NO_ENTRY) {
                throw new FileNotFoundException("Unable to open path", path);
            }
            return result;
        }

        public static string GetArchiveSummary(IArchive archive) {
            StringBuilder sb = new StringBuilder();
            foreach (IFileEntry entry in archive) {
                long length, storageSize;
                CompressionFormat format;
                sb.AppendLine("  " + entry.FileName);
                if (entry.GetPartInfo(FilePart.DataFork, out length, out storageSize, out format)) {
                    sb.AppendFormat("    data: len={0} storage={1} fmt={2}\r\n",
                        length, storageSize, format);
                }
                if (entry.GetPartInfo(FilePart.RsrcFork, out length, out storageSize, out format)) {
                    sb.AppendFormat("    rsrc: len={0} storage={1} fmt={2}\r\n",
                        length, storageSize, format);
                }
                if (entry.GetPartInfo(FilePart.DiskImage, out length, out storageSize, out format)){
                    sb.AppendFormat("    disk: len={0} storage={1} fmt={2}\r\n",
                        length, storageSize, format);
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Copies a stream to a file in the TestData directory.
        /// </summary>
        public static void CopyToFile(Stream source, string fileName) {
            string pathName = DebugFileDir + fileName;
            using (FileStream output =
                    new FileStream(pathName, FileMode.Create, FileAccess.Write)) {
                source.Position = 0;
                source.CopyTo(output);
            }
        }


        /// <summary>
        /// Parameters for creating a single-filesystem image file.
        /// </summary>
        public class CreateParams {
            // Type of filesystem to create.
            public FileSystemType FSType;

            // Size in bytes.  Must be a multiple of 256 (for sectors), 512, or 1024 depending
            // on filesystem type.
            public long Size { get; set; }

            // Name for volume.  Needed for ProDOS, HFS, Pascal.  If the string is empty or
            // invalid, a default value will be used.
            public string VolumeName { get; set; } = string.Empty;

            // Number for volume.  Only needed for DOS and 5.25" floppy images.
            public byte VolumeNum { get; set; } = 254;

            // Number of sectors per track.  Only needed for 5.25" floppy images and embedded
            // DOS volumes.
            public uint SectorsPerTrack { get; set; } = 0;

            // Order in which sectors are written to file.  Only needed for 5.25" floppy images.
            public SectorOrder FileOrder { get; set; } = SectorOrder.Unknown;

            // If false, don't e.g. reserve DOS tracks 1 and 2.
            public bool MakeBootable { get; set; } = true;
        }

        /// <summary>
        /// <para>Populates a data file with a single filesystem.  The contents of the data file
        /// will be overwritten.</para>
        ///
        /// <para>Not for use with multi-partition formats.</para>
        ///
        /// <para>The filesystem will initially be in raw (sector-edit) mode.</para>
        /// </summary>
        /// <param name="file"></param>
        /// <param name="parms"></param>
        /// <param name="log"></param>
        /// <returns>IFileSystem reference.</returns>
        public static IFileSystem CreateNewFS(Stream file, CreateParams parms, AppHook appHook) {
            if (!file.CanWrite) {
                throw new IOException("Can't create filesystem in read-only file");
            }
            foreach (FileAnalyzer.DiskLayoutEntry fse in FileAnalyzer.DiskLayouts) {
                if (fse.FSType == parms.FSType) {
                    // found it
                    if (!fse.IsSizeAllowed(parms.Size)) {
                        throw new ArgumentException("Size not valid for " + fse.Name + ": " +
                            parms.Size);
                    }

                    appHook.LogI("Creating FS " + fse.Name + " size=" + (parms.Size / 1024) + "KB");
                    // These may throw; let exception reach caller.
                    IChunkAccess chunkSource;
                    if (parms.SectorsPerTrack == 0) {
                        Debug.Assert(file.Length % BLOCK_SIZE == 0);
                        chunkSource = new GeneralChunkAccess(file, 0,
                            (uint)file.Length / BLOCK_SIZE);
                    } else {
                        Debug.Assert(file.Length % (SECTOR_SIZE * parms.SectorsPerTrack) == 0);
                        uint numTracks = (uint)file.Length / SECTOR_SIZE / parms.SectorsPerTrack;
                        chunkSource = new GeneralChunkAccess(file, 0, numTracks,
                            parms.SectorsPerTrack, parms.FileOrder);
                    }
                    fse.CreateInstance(chunkSource, appHook, out IDiskContents? contents);
                    if (contents is not IFileSystem) {
                        throw new NotImplementedException("not a filesystem");
                    }
                    //chunkSource.Format(parms.VolumeNum);
                    IFileSystem fs = (IFileSystem)contents;
                    fs.Format(parms.VolumeName, parms.VolumeNum, parms.MakeBootable);
                    return fs;
                }
            }
            throw new ArgumentException("Unsupported filesystem " + parms.FSType);
        }
    }
}
