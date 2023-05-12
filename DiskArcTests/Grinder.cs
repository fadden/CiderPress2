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
using static DiskArc.IFileSystem;

namespace DiskArcTests {
    /// <summary>
    /// Filesystem stress test.  Intended for filesystems that support subdirectories (ProDOS
    /// and HFS).
    /// </summary>
    /// <remarks>
    /// <para>The test runs in two alternating phases:</para>
    /// <list type="number">
    ///   <item>Expansion.  Create directories, create files in random directories,
    ///     expand files by writing data to them.</item>
    ///   <item>Contraction.  Truncate files, delete files, delete empty directories.</item>
    /// </list>
    /// <para>Expansion continues until the disk runs out of space.  Contraction continues
    /// until all files created by the test code have been deleted.  Any error other than
    /// "disk full" causes the test to halt.  If no errors are thrown, the test ends after a
    /// specified number of iterations.</para>
    /// <para>All actions are chosen at random.  The random number seed is passed in, so the
    /// behavior is reproducible.  (The docs for the Random class caution that different versions
    /// of .NET may generate different sequences.)</para>
    /// <para>File contents are verified when the contraction phase starts.</para>
    /// <para>Files already on the disk are ignored.</para>
    /// </remarks>
    public class Grinder {
        private IFileSystem mFileSystem;
        private Random mRandom;
        private int mSerial;

        private enum State {
            PreExpand,
            Expand,
            PreContract,
            Contract
        }

        private List<IFileEntry> mFiles = new List<IFileEntry>();
        private List<IFileEntry> mDirs = new List<IFileEntry>();

        private const int MIN_WRITE = 500;
        private const int MAX_WRITE = 30000;
        byte[] mDataBuffer = new byte[MAX_WRITE];

        private const string FILE_NAME_FMT = "FILE{0:X8}";
        private const string DIR_NAME_FMT = "DIR{0:X8}";


        public Grinder(IFileSystem fs, int seed) {
            mFileSystem = fs;
            mRandom = new Random(seed);

            mSerial = 0;
        }

        public void Execute(int iterations) {
            Debug.Assert(iterations > 0);
            DateTime startWhen = DateTime.Now;

            mFiles.Clear();
            mDirs.Clear();
            mDirs.Add(mFileSystem.GetVolDirEntry());

            State state = State.PreExpand;
            int curIter = 0;
            while (++curIter <= iterations) {
                if (curIter % 5000 == 0) {
                    Debug.WriteLine("Current iteration: " + curIter + " (of " + iterations + ")");
                }
                switch (state) {
                    case State.PreExpand:
                        // Create some files and directories to kick things off.
                        PreExpand();
                        state = State.Expand;
                        break;
                    case State.Expand:
                        if (!Expand()) {
                            Debug.WriteLine("Switching to contract mode at " + iterations);
                            // Verify file contents before switching away.
                            Verify();
                            state = State.PreContract;
                        }
                        break;
                    case State.PreContract:
                        PreContract();
                        state = State.Contract;
                        break;
                    case State.Contract:
                        if (!Contract()) {
                            Debug.WriteLine("Switching to expand mode at " + iterations);
                            state = State.PreExpand;
                        }
                        break;
                    default:
                        throw new Exception("Unexpected state " + state);
                }
            }

            DateTime endWhen = DateTime.Now;
            Debug.WriteLine("Finished " + iterations + " iterations in " +
                (endWhen - startWhen).TotalMilliseconds + " msec");
        }

        private void PreExpand() {
            // Get things started by creating a few directories and some empty files.  If we
            // run out of space here, there's not enough room to run the test, so we don't try
            // to catch exceptions.
            for (int i = 0; i < 5; i++) {
                mDirs.Add(CreateDir());
            }
            for (int i = 0; i < 10; i++) {
                mFiles.Add(CreateFile());
            }
        }

        private bool Expand() {
            int action = mRandom.Next(100);

            // 70%: write a random number of bytes to a random fork of a random file
            // 20%: create a new empty file
            // 10%: create a new subdirectory
            try {
                if (action < 70) {
                    AddToFile();
                } else if (action < 90) {
                    mFiles.Add(CreateFile());
                } else {
                    mDirs.Add(CreateDir());
                }
            } catch (DiskFullException) {
                Debug.WriteLine("Got disk full, switching modes");
                return false;
            }
            return true;
        }


        /// <summary>
        /// Creates a sequentially-named directory, in a directory chosen at random.
        /// </summary>
        private IFileEntry CreateDir() {
            int dirIndex = mRandom.Next(mDirs.Count);
            string fileName = string.Format(DIR_NAME_FMT, ++mSerial);
            return mFileSystem.CreateFile(mDirs[dirIndex], fileName, CreateMode.Directory);
        }

        /// <summary>
        /// Creates a sequentially-named file, in a directory chosen at random.  The file will
        /// be created as regular or extended at random.
        /// </summary>
        /// <returns></returns>
        private IFileEntry CreateFile() {
            int dirIndex = mRandom.Next(mDirs.Count);
            string fileName = string.Format(FILE_NAME_FMT, ++mSerial);
            bool isExt = mRandom.Next(2) == 1;
            return mFileSystem.CreateFile(mDirs[dirIndex], fileName,
                isExt ? CreateMode.Extended : CreateMode.File);
        }

        private void AddToFile() {
            int fileIndex = mRandom.Next(mFiles.Count);
            IFileEntry entry = mFiles[fileIndex];
            bool useRsrc = false;
            if (entry.HasRsrcFork) {
                useRsrc = mRandom.Next(2) == 1;
            }
            int count = mRandom.Next(MIN_WRITE, MAX_WRITE);
            byte value = NameChecksum(entry.FileName);
            RawData.MemSet(mDataBuffer, 0, count, value);
            using (DiskFileStream fd = mFileSystem.OpenFile(entry, FileAccessMode.ReadWrite,
                    useRsrc ? FilePart.RsrcFork : FilePart.DataFork)) {
                fd.Seek(0, SeekOrigin.End);
                fd.Write(mDataBuffer, 0, count);
            }
        }

        private void Verify() {
            Debug.WriteLine("Verifying " + mFiles.Count + " files in " + mDirs.Count +
                " directories");
            foreach (IFileEntry entry in mFiles) {
                VerifyFork(entry, false);
                if (entry.HasRsrcFork) {
                    VerifyFork(entry, true);
                }
            }
        }
        private void VerifyFork(IFileEntry entry, bool checkRsrc) {
            long len = checkRsrc ? entry.RsrcLength : entry.DataLength;
            if (len == 0) {
                return;
            }
            byte sum = NameChecksum(entry.FileName);
            using (DiskFileStream fd = mFileSystem.OpenFile(entry, FileAccessMode.ReadOnly,
                    checkRsrc ? FilePart.RsrcFork : FilePart.DataFork)) {
                while (len != 0) {
                    int actual = fd.Read(mDataBuffer, 0, mDataBuffer.Length);
                    if (!RawData.IsAllValue(mDataBuffer, 0, actual, sum)) {
                        throw new Exception("Invalid value found in " + entry.FileName);
                    }

                    len -= actual;
                }
            }
        }

        private void PreContract() {
            // We remove directories as they are emptied, but the contraction algorithm won't
            // see anything that's already empty.  We want to remove any empty directories now.
            // Removing a directory could make its parent empty, so we need to do a depth-first
            // traversal rather than a linear list walk.
            int beforeCount = mDirs.Count;
            DeleteEmptyDirs(mFileSystem.GetVolDirEntry());
            Debug.WriteLine("Removed " + (beforeCount - mDirs.Count) + " empty directories");
        }

        private void DeleteEmptyDirs(IFileEntry dirEntry) {
            // Recursively check all children.
            if (dirEntry.ChildCount != 0) {
                // Get a copy of the directory's child list, so we're not iterating over
                // the list that gets modified when we delete a dir.
                List<IFileEntry> kids = dirEntry.ToList();
                foreach (IFileEntry kid in kids) {
                    if (kid.IsDirectory) {
                        DeleteEmptyDirs(kid);
                    }
                }
            }

            // If we're now empty, delete ourselves.
            if (dirEntry.ChildCount == 0 && dirEntry.ContainingDir != IFileEntry.NO_ENTRY) {
                mFileSystem.DeleteFile(dirEntry);
                if (!mDirs.Remove(dirEntry)) {
                    throw new Exception("Failed to discard deleted directory entry");
                }
            }
        }

        private bool Contract() {
            int action = mRandom.Next(100);

            if (mFiles.Count == 0) {
                if (mDirs.Count != 1) {
                    // Somehow we removed all the files, but failed to remove all the
                    // directories.
                    throw new Exception("Grinder issue: failed to remove all directories");
                }

                // Filesystem has been scrubbed.  Expand.
                return false;
            }

            // 90%: remove a random number of bytes from a random fork of a random file;
            //      if both forks are empty, delete the file
            // 10%: remove file
            //
            // If all files have been removed from a directory, remove the directory.
            if (action < 90) {
                ReduceFile();
            } else {
                RemoveFile();
            }
            return true;
        }

        private void ReduceFile() {
            int fileIndex = mRandom.Next(mFiles.Count);
            IFileEntry entry = mFiles[fileIndex];
            bool useRsrc = mRandom.Next(2) == 1;
            int count = mRandom.Next(MIN_WRITE, MAX_WRITE);

            // Pick a fork to reduce.
            long len = useRsrc ? entry.RsrcLength : entry.DataLength;
            if (len == 0) {
                // Already truncated, hit the other fork.
                useRsrc = !useRsrc;
                len = useRsrc ? entry.RsrcLength : entry.DataLength;
            }
            if (len != 0) {
                long newLen = len - count;
                if (newLen < 0) {
                    newLen = 0;
                }
                using (DiskFileStream fd = mFileSystem.OpenFile(entry, FileAccessMode.ReadWrite,
                        useRsrc ? FilePart.RsrcFork : FilePart.DataFork)) {
                    fd.SetLength(newLen);
                }
            } else {
                Debug.Assert(entry.DataLength == 0 && entry.RsrcLength == 0);
            }

            if (entry.DataLength == 0 && entry.RsrcLength == 0) {
                DoRemoveFile(fileIndex);
            }
        }

        private void RemoveFile() {
            DoRemoveFile(mRandom.Next(mFiles.Count));
        }

        private void DoRemoveFile(int fileIndex) {
            IFileEntry entry = mFiles[fileIndex];
            IFileEntry parent = entry.ContainingDir;
            mFileSystem.DeleteFile(entry);
            mFiles.RemoveAt(fileIndex);

            // Walk up the tree, removing empty directories.
            while (parent.ChildCount == 0) {
                if (parent.ContainingDir == IFileEntry.NO_ENTRY) {
                    // This is the volume dir, which we can't remove.  (This means we're done
                    // with the contraction phase; that'll get noticed later.)
                    break;
                }
                // Find the parent in the list, and remove it.
                int dirIndex = mDirs.IndexOf(parent);
                if (dirIndex < 0) {
                    throw new Exception("Unable to find parent dir in list: " + parent.FileName);
                }
                IFileEntry dirEntry = parent;
                parent = parent.ContainingDir;
                mFileSystem.DeleteFile(dirEntry);
                mDirs.RemoveAt(dirIndex);
            }
        }


        /// <summary>
        /// Computes a simple checksum on a string.
        /// </summary>
        private static byte NameChecksum(string str) {
            int sum = 0;
            for (int i = 0; i < str.Length; i++) {
                sum += str[i];
            }
            return (byte)sum;
        }
    }
}
