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
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;

using CommonUtil;
using DiskArc;
using DiskArc.Comp;
using DiskArc.Multi;
using static DiskArc.Defs;
using static DiskArc.IDiskImage;
using static DiskArc.IFileSystem;

namespace cp2_wpf.LibTest {
    /// <summary>
    /// Test a compression codec by extracting, compressing, and expanding every file in a
    /// disk image or file archive.  Ideally used on a large ShrinkIt archive to test
    /// compatibility.  This is meant to be run in the background, reporting progress through
    /// a BackgroundWorker object.
    /// </summary>
    internal class BulkCompressTest {
        private const int BUFFER_SIZE = 4 * 1024 * 1024;
        private byte[] mSrcBuf = new byte[BUFFER_SIZE];
        private byte[] mCompBuf = new byte[BUFFER_SIZE * 2];    // oversized to allow expansion
        private byte[] mExpBuf = new byte[BUFFER_SIZE];

        private BackgroundWorker mWorker;
        private CompressionFormat mFormat;
        private string mCodecName;
        private bool mVerbose = false;
        private AppHook mAppHook;

        private int mFileCount;
        private int mForkCount;
        private int mDirectoryCount;
        private int mFailureCount;
        private long mTotalSrcLen;
        private long mTotalCompLen;


        /// <summary>
        /// Runs the test.
        /// </summary>
        /// <param name="worker">Background worker object.</param>
        /// <param name="pathName">Pathname to disk image or file archive.</param>
        /// <param name="format">Type of compression to use.</param>
        public static void RunTest(BackgroundWorker worker, string pathName,
                CompressionFormat format, AppHook appHook) {
            BulkCompressTest test = new BulkCompressTest(worker, format, appHook);
            test.Run(pathName);
        }

        /// <summary>
        /// Private constructor.
        /// </summary>
        /// <param name="worker"></param>
        private BulkCompressTest(BackgroundWorker worker, CompressionFormat format,
                AppHook appHook) {
            mWorker = worker;
            mFormat = format;
            mCodecName = format.ToString();
            mAppHook = appHook;
        }

        private void Run(string pathName) {
            PrintLogLine(string.Empty);  // weird: text box is ignoring first CRLF
            PrintLogLine("Opening " + pathName + " ...");
            try {
                using (FileStream stream = new FileStream(pathName, FileMode.Open,
                        FileAccess.Read)) {
                    PrintLogLine("Analyzing ...");
                    string ext = Path.GetExtension(pathName);
                    FileAnalyzer.AnalysisResult result = FileAnalyzer.Analyze(stream, ext, mAppHook,
                        out FileKind kind, out SectorOrder orderHint);
                    if (result != FileAnalyzer.AnalysisResult.Success) {
                        PrintLogLine("Analysis failed: " + result);
                        return;
                    }
                    PrintLogLine("  Analysis: kind=" + kind + " orderHint=" + orderHint);

                    PrintLogLine("Compressing all files with " + mCodecName + " ...");

                    DateTime startWhen = DateTime.Now;

                    if (IsDiskImageFile(kind)) {
                        TestDiskImage(stream, kind, orderHint, mAppHook);
                    } else {
                        TestArchive(stream, kind, mAppHook);
                    }

                    PrintLogLine("Elapsed time: " +
                        (DateTime.Now - startWhen).TotalSeconds.ToString("F1") + " sec");
                }
            } catch (IOException ex) {
                PrintLogLine("Unable to open file: " + ex.Message);
            }

            PrintSummary();

            PrintLogLine("--- end of test ---");
        }

        private void PrintLogLine(string msg) {
            mWorker?.ReportProgress(0, new ProgressMessage(msg + "\r\n"));
        }

        private void SetProgress(string msg) {
            mWorker?.ReportProgress(0, msg);
        }

        private void PrintSummary() {
            PrintLogLine(string.Empty);
            PrintLogLine("Files / forks: " + mFileCount + " / " + mForkCount);
            if (mDirectoryCount != 0) {
                PrintLogLine("Directories: " + mDirectoryCount);
            }
            PrintLogLine("Failures: " + mFailureCount);
            double perc = ((double)mTotalCompLen / (double)mTotalSrcLen) * 100.0;
            PrintLogLine("Total bytes compressed: " + mTotalSrcLen + " --> " + mTotalCompLen +
                " (" + perc.ToString("F2") + "%)");
        }

        private void TestDiskImage(FileStream fileStream, FileKind kind, SectorOrder orderHint,
                AppHook appHook) {
            using IDiskImage? diskImage =
                FileAnalyzer.PrepareDiskImage(fileStream, kind, appHook);
            if (diskImage == null ||
                    !diskImage.AnalyzeDisk(null, orderHint, AnalysisDepth.Full)) {
                PrintLogLine("Unable to prepare disk image");
                return;
            }
            if (diskImage.Contents is IFileSystem) {
                IFileSystem fileSys = (IFileSystem)diskImage.Contents;

                PrintLogLine("Processing disk image...");
                fileSys.PrepareFileAccess(true);
                IFileEntry rootDir = fileSys.GetVolDirEntry();
                TestDiskFiles(fileSys, rootDir, string.Empty);
            } else if (diskImage.Contents is IMultiPart) {
                foreach (Partition part in (IMultiPart)diskImage.Contents) {
                    PrintLogLine("Processing partition...");
                    part.AnalyzePartition();
                    if (part.FileSystem != null) {
                        IFileSystem fileSys = part.FileSystem;
                        fileSys.PrepareFileAccess(true);
                        IFileEntry rootDir = fileSys.GetVolDirEntry();
                        TestDiskFiles(fileSys, rootDir, string.Empty);
                    }
                }
            } else {
                // Failed
                return;
            }
        }

        private void TestDiskFiles(IFileSystem fs, IEnumerable<IFileEntry> dirEntry,
                string pathBase) {
            mDirectoryCount++;
            foreach (IFileEntry ent in dirEntry) {
                if (mWorker.CancellationPending) {
                    return;
                }
                string pathName;
                if (string.IsNullOrEmpty(pathBase)) {
                    pathName = ent.FileName;
                } else {
                    pathName = pathBase + ":" + ent.FileName;
                }

                //Debug.WriteLine("  " + pathName);
                if (ent.IsDirectory) {
                    TestDiskFiles(fs, ent, pathName);
                    continue;
                }
                mFileCount++;
                using (DiskFileStream stream =
                        fs.OpenFile(ent, FileAccessMode.ReadOnly, FilePart.DataFork)) {
                    mForkCount++;
                    TestFile(stream, pathName);
                }

                if (ent.HasRsrcFork) {
                    using (DiskFileStream stream =
                            fs.OpenFile(ent, FileAccessMode.ReadOnly, FilePart.RsrcFork)) {
                        mForkCount++;
                        TestFile(stream, pathName + " [rsrc]");
                    }
                }
            }
        }

        private void TestArchive(FileStream fileStream, FileKind kind, AppHook appHook) {
            using IArchive? archive = FileAnalyzer.PrepareArchive(fileStream, kind, appHook);
            if (archive == null) {
                PrintLogLine("Unable to prepare archive");
                return;
            }

            PrintLogLine("Processing archive...");
            foreach (IFileEntry ent in archive) {
                if (mWorker.CancellationPending) {
                    return;
                }

                mFileCount++;
                using (ArcReadStream stream = archive.OpenPart(ent, FilePart.DataFork)) {
                    mForkCount++;
                    TestFile(stream, ent.FullPathName);
                }

                if (ent.HasRsrcFork) {
                    using (ArcReadStream stream = archive.OpenPart(ent, FilePart.RsrcFork)) {
                        mForkCount++;
                        TestFile(stream, ent.FullPathName + " [rsrc]");
                    }
                }
            }
        }

        private static Stream CreateCodecStream(CompressionFormat format, Stream compStream,
                CompressionMode mode, long expandedLength) {
            switch (format) {
                case CompressionFormat.Uncompressed:
                    throw new NotImplementedException("Nope");
                case CompressionFormat.Squeeze:
                    return new SqueezeStream(compStream, mode, true, false, string.Empty);
                case CompressionFormat.NuLZW1:
                    return new NuLZWStream(compStream, mode, true, false, expandedLength);
                case CompressionFormat.NuLZW2:
                    return new NuLZWStream(compStream, mode, true, true, expandedLength);
                case CompressionFormat.Deflate:
                    if (mode == CompressionMode.Compress) {
                        return new DeflateStream(compStream, CompressionLevel.Optimal, true);
                        //return new DeflateStream(compStream, CompressionLevel.SmallestSize, true);
                    } else {
                        return new DeflateStream(compStream, CompressionMode.Decompress, true);
                    }
                default:
                    throw new NotImplementedException("Compression format not implemented");
            }
        }

        private void TestFile(Stream fileStream, string pathName) {
            SetProgress(pathName);

            MemoryStream srcStream = new MemoryStream(mSrcBuf);
            MemoryStream compStream = new MemoryStream(mCompBuf);
            MemoryStream expStream = new MemoryStream(mExpBuf);
            compStream.SetLength(0);
            expStream.SetLength(0);

            // Load the file into memory, or at least as much of it as will fit in the buffer.
            try {
                // Extending the length of a MemoryStream causes it to zero out the bytes in
                // the extended region, so we start full and truncate.
                srcStream.SetLength(mSrcBuf.Length);
                int actual = fileStream.Read(mSrcBuf, 0, mSrcBuf.Length);
                srcStream.SetLength(actual);
                if (actual == mSrcBuf.Length) {
                    //PrintLogLine("Long file: " + pathName);
                }
            } catch (Exception ex) {
                // Disk error or corrupted archive.
                mFailureCount++;
                PrintLogLine("ERROR: unable to read source file: " + pathName);
                PrintLogLine(ex.ToString());
                PrintLogLine(string.Empty);
                return;
            }
            mTotalSrcLen += srcStream.Length;

            // Create a compression stream.
            Stream compressor = CreateCodecStream(mFormat, compStream, CompressionMode.Compress,
                -1);

            // Rewind the source stream, and compress the data.
            srcStream.Position = 0;
            srcStream.CopyTo(compressor);
            compressor.Close();
            mTotalCompLen += compStream.Length;

            // Rewind compressed data stream.
            compStream.Position = 0;

            // Create an expansion stream.
            Stream expander = CreateCodecStream(mFormat, compStream, CompressionMode.Decompress,
                srcStream.Length);

            try {
                // Expand the data.
                expander.CopyTo(expStream);

                // Check it.
                if (srcStream.Length != expStream.Length ||
                        !RawData.CompareBytes(mSrcBuf, 0, mExpBuf, 0, (int)srcStream.Length)) {
                    mFailureCount++;
                    PrintLogLine("ERROR: data mismatch: " + pathName);
                }

                if (mVerbose) {
                    int perc;
                    if (srcStream.Length == 0) {
                        perc = 0;
                    } else {
                        perc = (int)((compStream.Length * 100) / srcStream.Length);
                    }
                    PrintLogLine("  " + pathName + " - " + srcStream.Length + " -> " +
                        compStream.Length + " (" + perc + "%)");
                }

            } catch (InvalidDataException ex) {
                mFailureCount++;
                PrintLogLine("ERROR: bad compressed data detected: " + pathName);
                PrintLogLine(ex.ToString());
            } catch (Exception ex) {
                mFailureCount++;
                PrintLogLine("ERROR: exception thrown: " + pathName);
                PrintLogLine(ex.ToString());
            }
        }
    }
}
