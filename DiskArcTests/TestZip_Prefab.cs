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

using CommonUtil;
using DiskArc;
using DiskArc.Arc;
using static DiskArc.Defs;

namespace DiskArcTests {
    /// <summary>
    /// ZIP archive tests.
    /// </summary>
    public class TestZip_Prefab : ITest {
        // Archive with filename character set exercises.
        private const string FANCY_NAME1 =
            "name-cp437-\u00e2\u00eb\u00ec\u00d6\u00dc";    // name-cp437-âëìÖÜ
        private const string FANCY_NAME2 =
            "name-utf8-\u00ab\u2022\u00a9\uf8ff\u2122";     // name-utf8-«•©™
        private static List<Helper.FileAttr> sFancyNames = new List<Helper.FileAttr>() {
            new Helper.FileAttr("name (plain)", 6, 6),
            new Helper.FileAttr(FANCY_NAME1, 6, 6),
            new Helper.FileAttr(FANCY_NAME2, 6, 6),
        };

        public static void TestSimple(AppHook appHook) {
            using (Stream dataFile = Helper.OpenTestFile("zip/fancy-names.zip", true, appHook)) {
                FileAnalyzer.Analyze(dataFile, ".zip", appHook, out FileKind kind,
                    out SectorOrder orderHint);
                if (kind != FileKind.Zip) {
                    throw new Exception("Failed to identify archive - " + kind);
                }
                using (IArchive archive = FileAnalyzer.PrepareArchive(dataFile, kind, appHook)!) {
                    Helper.ValidateContents(archive, sFancyNames);
                    Helper.CheckNotes(archive, 0, 0);

                    // Confirm that the test archive is using both encoding forms.
                    bool isUnicode =
                        ((Zip_FileEntry)archive.FindFileEntry(FANCY_NAME1)).HasUnicodeText;
                    Helper.ExpectBool(false, isUnicode, "cp437 filename encoding");
                    isUnicode =
                        ((Zip_FileEntry)archive.FindFileEntry(FANCY_NAME2)).HasUnicodeText;
                    Helper.ExpectBool(true, isUnicode, "utf8 filename encoding");
                }
            }

            // Try another archive that uses UTF-8 without setting the flag.  Some archives
            // require checking the OS identifier to determine the "default" character set.
            const string FANCY_NAME3 = "hello\u2022\u2197.as";  // hello•↗.as
            using (Stream dataFile = Helper.OpenTestFile("zip/hello-as.zip", true, appHook)) {
                using (IArchive archive = Zip.OpenArchive(dataFile, appHook)) {
                    Helper.CheckNotes(archive, 0, 0);

                    bool isUnicode =
                        ((Zip_FileEntry)archive.FindFileEntry(FANCY_NAME3)).HasUnicodeText;
                    Helper.ExpectBool(true, isUnicode, "utf8 filename encoding");
                }
            }
        }

        // Quick test to confirm that we're parsing data descriptors and extra data correctly.
        public static void TestStreamed(AppHook appHook) {
            using (Stream dataFile = Helper.OpenTestFile("zip/unix-streamed.zip", true, appHook)) {
                FileAnalyzer.Analyze(dataFile, ".zip", appHook, out FileKind kind,
                    out SectorOrder orderHint);
                if (kind != FileKind.Zip) {
                    throw new Exception("Failed to identify ZIP archive - " + kind);
                }
                using (IArchive archive = FileAnalyzer.PrepareArchive(dataFile, kind, appHook)!) {
                    Helper.CheckNotes(archive, 0, 0);
                    // Entry is compressed, but larger than original, because archiver couldn't
                    // back up while streaming.
                    IFileEntry entry = archive.FindFileEntry("no-comp");
                    if (entry.DataLength > entry.StorageSize) {
                        throw new Exception("hmm");
                    }
                    byte[] buf = new byte[entry.DataLength];
                    using ArcReadStream stream = archive.OpenPart(entry, FilePart.DataFork);
                    if (stream.Read(buf, 0, (int)entry.DataLength) != entry.DataLength) {
                        throw new Exception("read failed");
                    }
                    if (stream.ChkResult != ArcReadStream.CheckResult.Success) {
                        throw new Exception("checksum didn't happen");
                    }
                }
            }
        }

        public static void TestExtra(AppHook appHook) {
            MemoryStream newStream;
            // We happen to know that GSHK11.zip has 32-byte extra fields in the local and
            // central directory file headers.  Make a simple change to cause the archive to
            // be rewritten, so we can confirm that we're not breaking anything horribly.
            using (Stream dataFile = Helper.OpenTestFile("zip/GSHK11.zip", false, appHook)) {
                using (IArchive archive = FileAnalyzer.PrepareArchive(dataFile,
                        FileKind.Zip, appHook)!) {
                    Helper.CheckNotes(archive, 0, 0);
                    archive.StartTransaction();
                    IFileEntry entry = archive.GetFirstEntry();
                    entry.FileName = "just/need/a/change";
                    archive.CommitTransaction(newStream = new MemoryStream());
                }
            }

            using (IArchive archive = Zip.OpenArchive(newStream, appHook)) {
                Helper.CheckNotes(archive, 0, 0);
            }
        }

        public static void TestDiskImage(AppHook appHook) {
            using (Stream dataFile = Helper.OpenTestFile("zip/new-init-po.zip", true, appHook)) {
                using (IArchive archive = FileAnalyzer.PrepareArchive(dataFile,
                        FileKind.Zip, appHook)!) {
                    Helper.CheckNotes(archive, 0, 0);

                    // Extract the disk image.
                    IFileEntry entry = archive.FindFileEntry("new-init-po.dsk");
                    byte[] diskBuf = new byte[entry.DataLength];
                    using (ArcReadStream readStream = archive.OpenPart(entry, FilePart.DataFork)) {
                        if (readStream.Read(diskBuf, 0, diskBuf.Length) != diskBuf.Length) {
                            throw new Exception("short read");
                        }
                        Debug.Assert(readStream.ChkResult == ArcReadStream.CheckResult.Success);
                    }

                    // Do something with the disk image.
                    MemoryStream diskStream = new MemoryStream(diskBuf);
                    FileAnalyzer.Analyze(diskStream, ".dsk", appHook, out FileKind kind,
                        out SectorOrder orderHint);
                    if (!IsDiskImageFile(kind)) {
                        throw new Exception("didn't analyze as disk image - " + kind);
                    }

                    using (IDiskImage? img = FileAnalyzer.PrepareDiskImage(diskStream,
                            kind, appHook)) {
                        if (img == null || !img.AnalyzeDisk(null, orderHint,
                                IDiskImage.AnalysisDepth.Full)) {
                            throw new Exception("Unable to prepare disk image");
                        }
                        IFileSystem fs = (IFileSystem)img.Contents!;
                        fs.PrepareFileAccess(true);
                        IFileEntry volDir = fs.GetVolDirEntry();
                        IFileEntry fsEntry = fs.FindFileEntry(volDir, "HELLO");
                    }
                }
            }
        }
    }
}
