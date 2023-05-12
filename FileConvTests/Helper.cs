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
using System.IO.Compression;

using CommonUtil;
using DiskArc;
using DiskArc.Arc;
using DiskArc.Disk;
using FileConv;
using static DiskArc.Defs;
using static DiskArc.IFileSystem;

namespace FileConvTests {
    internal static class Helper {
        private const int MAX_MEM_FILE = 32 * 1024 * 1024;  // 32MB
        private const string TEST_DATA_DIR = "TestData";

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
        /// Gets a copy of a file from the "fileconv/test-files.sdk" disk archive.  Does not
        /// keep the archive open.
        /// </summary>
        /// <param name="fileName">Name of the file in the archive to extract.  Use ':' as the
        ///   directory name separator.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <param name="attribs">Result: file attributes.</param>
        /// <returns>Stream with a copy of the file.</returns>
        public static Stream GetCopyOfTestFile(string fileName, AppHook appHook,
                out FileAttribs attribs) {
            using Stream testFilesStream = OpenTestFile("fileconv/test-files.sdk", true, appHook);
            using IArchive arc = NuFX.OpenArchive(testFilesStream, appHook);
            IFileEntry diskEntry = arc.GetFirstEntry();
            using ArcReadStream entryStream = arc.OpenPart(diskEntry, FilePart.DiskImage);
            using Stream diskStream = TempFile.CopyToTemp(entryStream, diskEntry.DataLength);
            using UnadornedSector disk = UnadornedSector.OpenDisk(diskStream, appHook);
            disk.AnalyzeDisk();
            IFileSystem fs = (IFileSystem)disk.Contents!;
            fs.PrepareFileAccess(true);
            IFileEntry entry = fs.EntryFromPath(fileName, ':');
            attribs = new FileAttribs(entry);
            using Stream goodBasStream =
                fs.OpenFile(entry, FileAccessMode.ReadOnly, FilePart.DataFork);

            MemoryStream newStream = new MemoryStream();
            goodBasStream.CopyTo(newStream);
            return newStream;
        }

        /// <summary>
        /// Checks to see if the bitmap has the expected number of colors.  Useful for detecting
        /// whether an image is color or black & white.
        /// </summary>
        /// <param name="bits">Bitmap to check.</param>
        /// <param name="expColors">Expected number of colors.</param>
        public static void CheckColors(Bitmap8 bits, int expColors) {
            int paletteSize = bits.GetColors()!.Length;
            if (paletteSize < expColors) {
                throw new Exception("expected " + expColors + " colors, palette only has " +
                    paletteSize);
            }

            int count = 0;
            bool[] indexFound = new bool[paletteSize];
            for (int row = 0; row < bits.Height; row++) {
                for (int col = 0; col < bits.Width; col++) {
                    int pixIndex = bits.GetPixelIndex(col, row);
                    if (!indexFound[pixIndex]) {
                        indexFound[pixIndex] = true;
                        count++;
                    }
                }
            }

            if (count != expColors) {
                throw new Exception("expected " + expColors + " colors, but found " + count);
            }
        }
    }
}
