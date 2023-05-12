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
using System.Diagnostics;
using System.IO;
using System.Linq;

using CommonUtil;
using DiskArc;
using static DiskArc.Defs;

namespace DiskArcTests {
    /// <summary>
    /// File archive stress test.  Intended for general-purpose archive formats (ZIP, NuFX), not
    /// single-entry formats like gzip.
    /// </summary>
    public class Grindarc {
        private const int MIN_OPS = 0;
        private const int MAX_OPS = 12;
        private const int MAX_ARCHIVE_SIZE = 2 * 1024 * 1024;
        private const int INITIAL_FLAIR = 0;
        private const int MIN_FILE_LENGTH = 0;
        private const int MAX_FILE_LENGTH = 32768;

        private bool VERBOSE = false;
        private bool mHasDiskImages;
        private bool mHasRsrcForks;

        private IArchive mArchive;
        private Random mRandom;
        private int mSerial;

        private byte[] mTmpBuf = new byte[MAX_FILE_LENGTH];

        private enum State {
            Expand,
            Contract
        }

        private class FileRec {
            private const string FILE_NAME_FMT = "FILE{0:X8}-{1:D3}";
            private const int NAME_CONST_LEN = 4 + 8 + 1;

            public int Serial { get; private set; }
            private int mNameFlair;

            public int DataLength { get; set; }
            public uint DataCrc32 { get; set; }
            public int RsrcLength { get; set; }
            public uint RsrcCrc32 { get; set; }
            public bool IsDisk { get; set; }

            public string FileName { get; private set; } = string.Empty;
            public IFileEntry FileEntry { get; set; } = IFileEntry.NO_ENTRY;

            public FileRec(int serial, int flair) {
                Serial = serial;
                mNameFlair = flair;
                BumpFileName();

                DataLength = RsrcLength = -1;
                DataCrc32 = RsrcCrc32 = 0xcccccccc;
            }
            public FileRec Clone() {
                return (FileRec)MemberwiseClone();
            }

            public void BumpFileName() {
                mNameFlair++;
                FileName = string.Format(FILE_NAME_FMT, Serial, mNameFlair);
            }
            public bool CompareFileName(string otherName) {
                // Compare the non-flair portion of the filename for equality.
                return FileName.Substring(0, NAME_CONST_LEN) ==
                    otherName.Substring(0, NAME_CONST_LEN);
            }
        }

        // We keep track of what we expect the archive to hold, and then compare that to what
        // it actually holds.  Because changes are queued up for the next commit, our list will
        // only match up immediately after the commit.  We need to work of our list, rather than
        // the archive list, to avoid redundant operations like deleting the same file twice, and
        // to ensure that renaming a file twice works correctly.
        //
        // If we're planning to cancel the operations, we still need to track our progress, but
        // we want to discard the results.  To handle that, we use an "ephemeral list" when
        // making non-permanent changes.
        private List<FileRec> mFiles = new List<FileRec>();
        private List<FileRec> mEphFiles = new List<FileRec>();

        public Grindarc(IArchive archive, int seed) {
            mArchive = archive;
            mRandom = new Random(seed);

            mSerial = 0;

            mHasDiskImages = mArchive.Characteristics.HasDiskImages;
            mHasRsrcForks = mArchive.Characteristics.HasResourceForks;
        }

        public void Execute(int iterations) {
            Debug.WriteLine("Grind start, iterations=" + iterations);
            Debug.Assert(iterations > 0);
            DateTime startWhen = DateTime.Now;

            mFiles.Clear();

            MemoryStream curStream = new MemoryStream();
            State state = State.Expand;
            int curIter = 0;
            while (++curIter <= iterations) {
                if (curIter % 100 == 0) {
                    Debug.WriteLine("Current iteration: " + curIter + " (of " + iterations + ")");
                }
                int numOps = mRandom.Next(MIN_OPS, MAX_OPS);
                bool doCommit = mRandom.Next(100) < 90;         // 10% chance of canceling ops
                mEphFiles.Clear();
                if (!doCommit) {
                    // Create an "ephemeral" copy so we can track a to-be-cancelled transaction.
                    foreach (FileRec rec in mFiles) {
                        mEphFiles.Add(rec.Clone());
                    }
                }
                mArchive.StartTransaction();
                for (int i = 0; i < numOps; i++) {
                    switch (state) {
                        case State.Expand:
                            Expand(doCommit ? mFiles : mEphFiles);
                            break;
                        case State.Contract:
                            Contract(doCommit ? mFiles : mEphFiles);
                            break;
                        default:
                            throw new Exception("Unexpected state " + state);
                    }
                }
                if (doCommit) {
                    if (VERBOSE) {
                        Debug.WriteLine("Commit numOps=" + numOps);
                    }
                    mArchive.CommitTransaction(curStream = new MemoryStream());
                    // Could re-open the stream 20% of the time; requires a delegate or reflection
                    // to get the OpenArchive method.
                } else {
                    if (VERBOSE) {
                        Debug.WriteLine("Cancel numOps=" + numOps);
                    }
                    mArchive.CancelTransaction();
                }

                Verify();

                if (curStream.Length > MAX_ARCHIVE_SIZE && state == State.Expand) {
                    state = State.Contract;
                    Debug.WriteLine("Mode switch: contracting (size=" + curStream.Length + ")");
                } else if (mFiles.Count < MAX_OPS / 2 && state == State.Contract) {
                    state = State.Expand;
                    Debug.WriteLine("Mode switch: expanding (count=" + mFiles.Count + ")");
                }
            }

            DateTime endWhen = DateTime.Now;
            Debug.WriteLine("Finished " + iterations + " iterations in " +
                (endWhen - startWhen).TotalMilliseconds + " msec");
        }

        private void Expand(List<FileRec> fileList) {
            int action = mRandom.Next(100);

            // Primarily expand:
            // 50% add new record
            // 20% add or replace part in existing record
            // 20% tweak filename
            // 10% remove part from record
            if (action < 50) {
                // add new record
                AddRecord(fileList);
            } else if (action < 70) {
                // add/replace part in existing record (skip if no records)
                ModifyRecord(fileList);
            } else if (action < 90) {
                // tweak filename
                TweakMetadata(fileList);
            } else {
                // remove part from record; removes record if last part
                RemovePart(fileList);
            }
        }

        private void Contract(List<FileRec> fileList) {
            int action = mRandom.Next(100);

            // Primarily contract:
            // 50% remove part from record; removes record if last part
            // 20% add or replace part in existing record
            // 20% tweak filename
            // 10% add new record
            if (action < 50) {
                // remove part from record; removes record if last part
                RemovePart(fileList);
            } else if (action < 70) {
                // add/replace part in existing record (skip if no records)
                ModifyRecord(fileList);
            } else if (action < 90) {
                // tweak filename
                TweakMetadata(fileList);
            } else {
                // add new record
                AddRecord(fileList);
            }
        }

        /// <summary>
        /// Verifies that the archive's state matches expectations.  Refreshes the IFileEntry
        /// references.
        /// </summary>
        /// <exception cref="Exception"></exception>
        private void Verify() {
            List<IFileEntry> entries = mArchive.ToList();
            if (mFiles.Count != entries.Count) {
                throw new Exception("File count mismatch: archive has " + entries.Count +
                    ", local list has " + mFiles.Count);
            }
            for (int i = 0; i < entries.Count; i++) {
                IFileEntry entry = entries[i];
                FileRec frec = mFiles[i];
                if (entry.FileName != frec.FileName) {
                    throw new Exception("Filename mismatch " + i + ": archive='" +
                        entry.FileName + "', local='" + frec.FileName + "'");
                }

                // Update the file entry object.
                frec.FileEntry = entry;

                // Compare contents.
                if (entry.IsDiskImage) {
                    using ArcReadStream stream = mArchive.OpenPart(entry, FilePart.DiskImage);
                    uint crc32 = CRC32.OnStream(stream);
                    if (crc32 != frec.DataCrc32) {
                        throw new Exception("CRC32 mismatch on disk image");
                    }
                } else {
                    if (entries[i].HasRsrcFork) {
                        using ArcReadStream stream = mArchive.OpenPart(entry, FilePart.RsrcFork);
                        uint crc32 = CRC32.OnStream(stream);
                        if (crc32 != frec.RsrcCrc32) {
                            throw new Exception("CRC32 mismatch on rsrc fork");
                        }
                    }
                    if (entries[i].DataLength != 0) {
                        using ArcReadStream stream = mArchive.OpenPart(entry, FilePart.DataFork);
                        uint crc32 = CRC32.OnStream(stream);
                        if (crc32 != frec.DataCrc32) {
                            throw new Exception("CRC32 mismatch on data fork");
                        }
                    }
                }
            }
        }

        private void AddRecord(List<FileRec> fileList) {
            int serial = mSerial++;

            // Set up the data source.
            int action = mRandom.Next(8);
            FilePart part;
            if (mHasDiskImages && action == 0) {
                part = FilePart.DiskImage;
            } else if (mHasRsrcForks && action == 1) {
                part = FilePart.RsrcFork;
            } else {
                part = FilePart.DataFork;
            }
            int length = mRandom.Next(MIN_FILE_LENGTH, MAX_FILE_LENGTH);
            if (part == FilePart.DiskImage) {
                // Ensure length is a multiple of 512 and > 0.
                length &= ~511;
                if (length == 0) {
                    length = 512;
                }
            }
            IPartSource source = GetRandomSource(length, serial, out uint crc32);

            FileRec newRec = new FileRec(serial, INITIAL_FLAIR);
            if (part == FilePart.RsrcFork) {
                newRec.RsrcLength = length;
                newRec.RsrcCrc32 = crc32;
            } else {
                newRec.DataLength = length;
                newRec.DataCrc32 = crc32;
                newRec.IsDisk = part == FilePart.DiskImage;
            }

            IFileEntry newEntry = mArchive.CreateRecord();
            newEntry.FileName = newRec.FileName;
            mArchive.AddPart(newEntry, part, source, CompressionFormat.Default);

            newRec.FileEntry = newEntry;
            fileList.Add(newRec);
            if (VERBOSE) {
                Debug.WriteLine("Added " + newRec.FileName + ", len=" + length);
            }
        }

        /// <summary>
        /// Modifies a record, adding a new part or replacing an existing part.
        /// </summary>
        /// <param name="fileList"></param>
        private void ModifyRecord(List<FileRec> fileList) {
            if (fileList.Count == 0) {
                return;
            }
            int recNum = mRandom.Next(0, fileList.Count);
            FileRec rec = fileList[recNum];
            IFileEntry entry = rec.FileEntry;
            Debug.Assert(rec.CompareFileName(entry.FileName));

            string modString = "?";

            if (rec.IsDisk) {
                // replace disk image
                int length = mRandom.Next(MIN_FILE_LENGTH, MAX_FILE_LENGTH) & ~511;
                if (length == 0) {
                    length = 512;
                }
                IPartSource source = GetRandomSource(length, rec.Serial, out uint crc32);
                rec.DataLength = length;
                rec.DataCrc32 = crc32;
                Debug.Assert(rec.IsDisk);
                mArchive.DeletePart(entry, FilePart.DiskImage);
                mArchive.AddPart(entry, FilePart.DiskImage, source, CompressionFormat.Default);
                modString = "repl disk image";
            } else if (rec.RsrcLength == -1 && mHasRsrcForks) {
                // have a data fork; add a resource fork
                Debug.Assert(rec.DataLength >= 0);
                int length = mRandom.Next(MIN_FILE_LENGTH, MAX_FILE_LENGTH);
                IPartSource source = GetRandomSource(length, rec.Serial, out uint crc32);
                rec.RsrcLength = length;
                rec.RsrcCrc32 = crc32;
                mArchive.AddPart(entry, FilePart.RsrcFork, source, CompressionFormat.Default);
                modString = "add rsrc fork";
            } else if (rec.DataLength == -1) {
                // have a resource fork; add a data fork
                Debug.Assert(rec.RsrcLength >= 0);
                int length = mRandom.Next(MIN_FILE_LENGTH, MAX_FILE_LENGTH);
                IPartSource source = GetRandomSource(length, rec.Serial, out uint crc32);
                rec.DataLength = length;
                rec.DataCrc32 = crc32;
                mArchive.AddPart(entry, FilePart.DataFork, source, CompressionFormat.Default);
                modString = "add data fork";
            } else {
                int length = mRandom.Next(MIN_FILE_LENGTH, MAX_FILE_LENGTH);
                IPartSource source = GetRandomSource(length, rec.Serial, out uint crc32);
                if (rec.RsrcLength != -1 && mRandom.Next(0, 2) == 0) {
                    // replace the resource fork
                    mArchive.DeletePart(entry, FilePart.RsrcFork);
                    mArchive.AddPart(entry, FilePart.RsrcFork, source, CompressionFormat.Default);
                    rec.RsrcLength = length;
                    rec.RsrcCrc32 = crc32;
                    modString = "replace rsrc fork";
                } else {
                    // replace the data fork
                    mArchive.DeletePart(entry, FilePart.DataFork);
                    mArchive.AddPart(entry, FilePart.DataFork, source, CompressionFormat.Default);
                    rec.DataLength = length;
                    rec.DataCrc32 = crc32;
                    modString = "replace data fork";
                }
            }

            if (VERBOSE) {
                Debug.WriteLine("Modified " + rec.FileName + ": " + modString);
            }
        }

        /// <summary>
        /// Removes a part of a record, or an entire record.
        /// </summary>
        /// <remarks>
        /// We may be removing a part or record that hasn't been committed.
        /// </remarks>
        private void RemovePart(List<FileRec> fileList) {
            if (fileList.Count == 0) {
                return;
            }
            int recNum = mRandom.Next(0, fileList.Count);
            FileRec rec = fileList[recNum];
            IFileEntry entry = rec.FileEntry;
            Debug.Assert(rec.CompareFileName(entry.FileName));

            if (rec.RsrcLength != -1 && (rec.DataLength == -1 || mRandom.Next(2) == 0)) {
                // Remove resource fork.  If there's no data fork, delete the record.  (Our
                // internal tracking doesn't do Miranda threads, so it's possible for a record
                // to not have a data fork.)
                if (rec.DataLength == -1) {
                    mArchive.DeleteRecord(entry);
                    fileList.RemoveAt(recNum);
                    if (VERBOSE) {
                        Debug.WriteLine("Removed rsrc record " + rec.FileName);
                    }
                } else {
                    mArchive.DeletePart(entry, FilePart.DataFork);
                    rec.DataLength = -1;
                    rec.DataCrc32 = 0xcccccccd;
                    if (VERBOSE) {
                        Debug.WriteLine("Removed data fork " + rec.FileName);
                    }
                }
            } else if (rec.IsDisk) {
                // Remove disk image.  This must be the only part, so we delete the record.
                mArchive.DeleteRecord(entry);
                fileList.RemoveAt(recNum);
                if (VERBOSE) {
                    Debug.WriteLine("Removed disk image record " + rec.FileName);
                }
            } else {
                // Remove data fork.  If there's no resource fork, delete the record.
                if (rec.RsrcLength == -1) {
                    mArchive.DeleteRecord(entry);
                    fileList.RemoveAt(recNum);
                    if (VERBOSE) {
                        Debug.WriteLine("Removed data record " + rec.FileName);
                    }
                } else {
                    mArchive.DeletePart(entry, FilePart.RsrcFork);
                    rec.RsrcLength = -1;
                    rec.RsrcCrc32 = 0xccccccce;
                    if (VERBOSE) {
                        Debug.WriteLine("Removed rsrc fork " + rec.FileName);
                    }
                }
            }
        }

        private void TweakMetadata(List<FileRec> fileList) {
            if (fileList.Count == 0) {
                return;
            }
            int recNum = mRandom.Next(0, fileList.Count);
            FileRec rec = fileList[recNum];
            IFileEntry entry = rec.FileEntry;
            Debug.Assert(rec.CompareFileName(entry.FileName));

            rec.BumpFileName();
            entry.FileName = rec.FileName;
        }

        private IPartSource GetRandomSource(int length, int serial, out uint crc32) {
            IPartSource source;
            int which = mRandom.Next(2);
            if (which == 0) {
                source = new RandomPartSource(length, serial);
            } else {
                source = new FixedPartSource(length, (byte)serial);
            }
            source.Open();
            crc32 = 0;
            while (true) {
                int actual = source.Read(mTmpBuf, 0, mTmpBuf.Length);
                if (actual == 0) {
                    break;
                }
                crc32 = CRC32.OnBuffer(crc32, mTmpBuf, 0, actual);
            }
            source.Rewind();
            return source;
        }
    }

    #region Data sources

    /// <summary>
    /// A part source that generates a fixed sequence of pseudo-random numbers.
    /// </summary>
    class RandomPartSource : IPartSource {
        private int mLength;
        private int mSeed;
        private Random mRand;

        private int mPosition;

        public RandomPartSource(int length, int seed) {
            mLength = length;
            mSeed = seed;
            mRand = new Random(seed);
        }

        public void Open() { }

        public int Read(byte[] buf, int offset, int count) {
            if (count < 0) {
                throw new ArgumentException("invalid count");
            }
            if (count > mLength - mPosition) {
                count = mLength - mPosition;
            }
            if (count > 8) {
                count /= 2;     // use deliberately short reads to test Read() usage
            }
            for (int i = 0; i < count; i++) {
                buf[offset++] = (byte)mRand.Next(0, 256);
            }
            mPosition += count;
            return count;
        }

        public void Rewind() {
            // Restart sequence.
            mRand = new Random(mSeed);
            mPosition = 0;
        }

        public void Close() { }

        public void Dispose() { }
    }

    /// <summary>
    /// A part source that generates a stream of a single value.
    /// </summary>
    class FixedPartSource : IPartSource {
        private int mLength;
        private byte mValue;

        private int mPosition;

        public FixedPartSource(int length, byte value) {
            mLength = length;
            mValue = value;
        }

        public void Open() { }

        public int Read(byte[] buf, int offset, int count) {
            if (count < 0) {
                throw new ArgumentException("invalid count");
            }
            if (count > mLength - mPosition) {
                count = mLength - mPosition;
            }
            if (count > 8) {
                count /= 2;     // use deliberately short reads to test Read() usage
            }
            for (int i = 0; i < count; i++) {
                buf[offset++] = mValue;
            }
            mPosition += count;
            return count;
        }

        public void Rewind() {
            mPosition = 0;
        }

        public void Close() { }

        public void Dispose() { }
    }

    #endregion Data sources
}
