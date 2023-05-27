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

using CommonUtil;
using DiskArc;
using DiskArc.Arc;
using static DiskArc.Defs;
using static DiskArc.IFileSystem;

// A few notes...
//
// The DiskArc library has no notion of hierarchy.  A disk image or file archive that sits
// inside another disk image or file archive is just another file entry.  This class organizes
// IDiskImage and IArchive objects into a tree so that, if an entry is updated, the changes can
// be propagated upward to the root.  There are no nodes for IFileSystem, IMultiPart, or
// Partition, even though those are logically independent entities that the user might want to
// reference, because they are wholly contained within an IDiskImage.
//
// After changes are made, the UpdateChanges() function is called at the appropriate level.
// Changes may be made to leaves or internal nodes, but not to the root itself, which is always a
// file on the host system.  For any update we need to consider how the change affects the
// disk/archive entry, and how that change is handled in the parent.
//
// If a disk image is modified, we need to flush any data that is cached in IFileSystem or
// elsewhere, so that the underlying stream is in sync.  If a file archive is modified, we need
// to commit the transaction to a new stream and discard the old when the transaction completes.
// There are four scenarios to consider.
//
// Parent is disk, child is disk: flush changes.  The child blocks are mapped directly onto the
// parent, so any writes to the child are effectively visible in the parent immediately.  We just
// have to be sure that the data structures aren't caching anything.
//
// Parent is disk, child is archive: commit the transaction to a new file on the same disk (either
// host or disk image).  When the transaction succeeds, delete the original and rename the new
// file in its place.
//
// Parent is archive, child is disk: execute a transaction that replaces the contents of the disk
// image file.
//
// Parent is archive, child is archive: commit the child's transaction to a temporary stream.
// Execute a transaction that replaces the child archive entry in the parent.
//
// This gets a little complicated because we may not be able to rename or delete a file while
// it's open.  (Even if the host filesystem allows it, IFileSystem does not.)  Rotating an updated
// IArchive file requires closing and re-opening the underlying stream, but if we do that we'll
// invalidate all of our IFileEntry objects, which is a significant issue if we change an archive
// that isn't a leaf node.  We could be required to reopen all of the children.  To work around
// this, IArchive has a ReopenStream() method that can be used to replace the Stream object
// without disrupting the data structures.
//
// Another note: when something is stored in a file archive, it must be extracted to a temporary
// stream, in memory or on disk, because IArchive and IFileSystem require a seekable stream.  This
// means the parent entry is not actually held open.  Disk images or file archives stored in a
// disk image or the host filesystem are kept open because they're accessed directly.  This
// matters when trying to rename or delete an open object.

namespace AppCommon {
    /// <summary>
    /// <para>Tree of nodes that wrap nested IDiskImage and IArchive objects.  When a disk image
    /// or archive is modified, use this to manage upward propagation of changes.</para>
    /// </summary>
    /// <remarks>
    /// <para>This allows changes to be made to disk images and archives stored inside other
    /// disk images and archives.</para>
    /// </remarks>
    public abstract class DiskArcNode : IDisposable {
        internal const int MAX_TMP_ITER = 1000;

        /// <summary>
        /// Short string used to identify this node.
        /// </summary>
        public string DebugIdentifier { get; protected set; } = string.Empty;

        /// <summary>
        /// Reference to parent node.
        /// </summary>
        internal protected DiskArcNode? Parent { get; protected set; }

        /// <summary>
        /// File entry for this node in the parent node.  Will be NO_ENTRY for HostFileNodes.
        /// </summary>
        public IFileEntry EntryInParent { get; protected set; }

        /// <summary>
        /// Stream for the IDiskImage or IArchive that this nodes wraps.  Those objects don't
        /// own the streams they use; we do.
        /// </summary>
        protected Stream NodeStream { get; set; }

        /// <summary>
        /// List of child nodes.
        /// </summary>
        protected List<DiskArcNode> Children { get; } = new List<DiskArcNode>();

        // Application hook reference.
        protected AppHook AppHook { get; set; }


        /// <summary>
        /// Base class constructor.
        /// </summary>
        /// <param name="parent">Reference to parent node.  Will be null for root.</param>
        /// <param name="nodeStream">Stream associated with this node.</param>
        /// <param name="entryInParent">File entry for this node in parent.</param>
        /// <param name="appHook">Application hook reference.</param>
        public DiskArcNode(DiskArcNode? parent, Stream nodeStream, IFileEntry entryInParent,
                AppHook appHook) {
            Parent = parent;
            NodeStream = nodeStream;
            EntryInParent = entryInParent;
            AppHook = appHook;

            parent?.Children.Add(this);
        }

        ~DiskArcNode() {
            Dispose(false);
        }
        public void Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        protected virtual void Dispose(bool disposing) {
            Debug.Assert(!mDisposed, this + " disposed twice");
            if (disposing) {
                Debug.Assert(NodeStream.CanRead);
                NodeStream.Dispose();
            } else {
                Debug.Assert(false, "GC disposing DiskArcNode created:\r\n" + mCreationStackTrace);
            }
            mDisposed = true;
        }
        private bool mDisposed = false;
#if DEBUG
        private string mCreationStackTrace = Environment.StackTrace + Environment.NewLine + "---";
#else
        private string mCreationStackTrace = "  (stack trace not enabled)";
#endif

        /// <summary>
        /// Disposes of a specific single child node, and removes it from the tree.
        /// </summary>
        /// <param name="child"></param>
        public void DisposeChild(DiskArcNode child) {
            if (!Children.Remove(child)) {
                Debug.Assert(false, "asked to remove non-existent child");
                return;
            }
            child.Dispose();
        }

        protected void DisposeChildren() {
            foreach (DiskArcNode child in Children) {
                child.Dispose();
            }
            Children.Clear();
        }

        /// <summary>
        /// Call after changes have been made to a disk image or archive.  For an archive, this
        /// will handle the details of committing the file to the parent archive or disk image.
        /// When this returns, all changes must be flushed to the host filesystem, ensuring that
        /// the file will be in a consistent state even if the app is immediately killed.
        /// </summary>
        /// <param name="doCompress">If true, the default compression method will be applied
        ///   to files added to archives.</param>
        /// <exception cref="IOException">Error occurred while performing update.</exception>
        /// <exception cref="ConversionException">Error occurred during file import.</exception>
        public abstract void SaveUpdates(bool doCompress);

        /// <summary>
        /// Creates an archive output file for use by a child archive node.  Called by IArchive
        /// nodes when an update is ready.  The Stream returned should be used to hold the
        /// output of the transaction commit.
        /// </summary>
        /// <remarks>
        /// <para>If the archive is in a disk image or the host filesystem, this creates a
        /// temporary file next to the archive file.  If the archive is inside another archive,
        /// a temporary file is created on the host filesystem.</para>
        /// </remarks>
        /// <param name="entryHere">File entry object for the archive within this object.</param>
        /// <returns>Stream to use with <see cref="IArchive.CommitTransaction"/>.</returns>
        internal protected abstract Stream CreateArchiveOutputFile(IFileEntry entryHere);

        /// <summary>
        /// Completes the archive update by replacing the original archive file with the
        /// updated version.  This could either delete/rename (on disk) or replace the entry
        /// (in an archive).  This must be called at some point after
        /// <see cref="CreateArchiveOutputFile"/>, to either rotate the file, copy the temp
        /// archive file in, or, on failure, simply delete the temporary files.
        /// </summary>
        /// <remarks>
        /// <para>Renaming open files isn't allowed on some systems, so on successful update we
        /// may need to close, rename, and reopen the archive.  The reopened stream is returned,
        /// and should be used to replace the "newArcStream" object in the caller.</para>
        /// </remarks>
        /// <param name="entryHere">File entry object for the archive within this object.  May
        ///   be updated if the file is renamed on a disk image.</param>
        /// <param name="doCompress">If true, perform compression when updating an archive.</param>
        /// <param name="success">If false, instead of performing the update, we just clean
        ///   up the temporary files (if any).</param>
        /// <returns>Re-opened archive stream.  May be the stream that was returned by
        ///   CreateArchiveOutputFile.</returns>
        internal protected abstract Stream? FinishArchiveOutputFile(ref IFileEntry entryHere,
            bool doCompress, bool success);

        /// <summary>
        /// Notifies a node that a child disk image node has been updated.  The disk image stream
        /// is used by IArchive parents to update their entries.
        /// </summary>
        /// <param name="diskImageStream">Stream with the disk image data.</param>
        /// <param name="entryHere">File entry object for the disk image within this object.</param>
        /// <param name="doCompress">If true, perform compression when updating an archive.</param>
        internal protected abstract void DiskUpdated(Stream diskImageStream, IFileEntry entryHere,
            bool doCompress);

        /// <summary>
        /// Scans the entire node tree, looking for problems.
        /// </summary>
        /// <returns>True if all is healthy.</returns>
        public bool CheckHealth() {
            DiskArcNode rootNode = this;
            while (rootNode.Parent != null) {
                rootNode = rootNode.Parent;
            }
            return rootNode.DoHealthCheck();
        }

        /// <summary>
        /// Performs a "health check" to confirm that all is well.
        /// </summary>
        /// <returns>True if healthy.</returns>
        protected virtual bool DoHealthCheck() {
            if (!NodeStream.CanRead) {
                Debug.Assert(false, "Health check failed: stream closed: " + this);
                return false;
            }
            foreach (DiskArcNode child in Children) {
                if (!child.DoHealthCheck()) {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Generates a list of nodes, from a leaf to the root.
        /// </summary>
        /// <returns>Human-readable string.</returns>
        public string DebugDumpBranch() {
            StringBuilder sb = new StringBuilder();
            sb.Append(this.GetType().Name);
            sb.Append(':');
            if (EntryInParent == IFileEntry.NO_ENTRY) {
                sb.Append("<NO_ENTRY>");
            } else {
                sb.Append(EntryInParent.FullPathName);
            }
            if (Parent != null) {
                sb.Append(" - ");
                sb.Append(Parent.DebugDumpBranch());
            }
            return sb.ToString();
        }

        /// <summary>
        /// Generates an ASCII-art diagram of the work tree.
        /// </summary>
        public string GenerateTreeSummary() {
            StringBuilder sb = new StringBuilder();
            GenerateTreeSummary(this, 0, "", true, sb);
            return sb.ToString();
        }

        /// <summary>
        /// Recursively generates an ASCII-art diagram of the work tree.
        /// </summary>
        private static void GenerateTreeSummary(DiskArcNode node, int depth, string indent,
                bool isLastSib, StringBuilder sb) {
            sb.Append(indent);
            sb.Append("+-");
            sb.Append(node.GetType().Name);
            sb.Append(": ");
            sb.Append(node.EntryInParent == IFileEntry.NO_ENTRY ?
                "(no entry)" : node.EntryInParent.FileName);
            if (!node.NodeStream.CanRead) {
                sb.Append(" *CLOSED*");
            }
            sb.AppendLine();

            for (int i = 0; i < node.Children.Count; i++) {
                string newIndent = indent + (isLastSib ? "  " : "| ");
                GenerateTreeSummary(node.Children[i], depth + 1, newIndent,
                    i == node.Children.Count - 1, sb);
            }
        }

        public override string ToString() {
            string result = DebugIdentifier;
            if (mDisposed) {
                result = "*DISPOSED* " + result;
            } else if (!NodeStream.CanRead) {
                result = "*BAD STREAM* " + result;
            }
            return result;
        }
    }

    /// <summary>
    /// Node representing an IArchive.
    /// </summary>
    public class ArchiveNode : DiskArcNode {
        public IArchive Archive { get; protected set; }

        private Stream? mTmpStream;

        public ArchiveNode(DiskArcNode? parent, Stream nodeStream, IArchive archive,
                IFileEntry entryInParent, AppHook appHook) :
                base(parent, nodeStream, entryInParent, appHook) {
            Archive = archive;

            DebugIdentifier = ((parent != null) ? parent.DebugIdentifier + "/" : "") +
                "Archive";
        }

        protected override void Dispose(bool disposing) {
            if (disposing) {
                DisposeChildren();
                Archive.Dispose();
            }
            base.Dispose(disposing);
        }

        // Save updates to a file archive.
        public override void SaveUpdates(bool doCompress) {
            Debug.Assert(Parent != null);
            // Create a new file on disk, or a temp file on the host filesystem.
            Stream outputStream = Parent.CreateArchiveOutputFile(EntryInParent);
            try {
                // Commit the transaction to the new file.
                Archive.CommitTransaction(outputStream);
                NodeStream.Close();
                NodeStream = outputStream;
                // Rotate the file, or write it to the archive.
                IFileEntry eip = EntryInParent;
                Stream? reOutStream = Parent.FinishArchiveOutputFile(ref eip, doCompress, true);
                EntryInParent = eip;
                // Should return outputStream (if we're in an archive) or should have closed it
                // (if we're in a disk image / host and the file needed to be deleted).
                Debug.Assert(reOutStream != null);
                Debug.Assert(reOutStream == outputStream || !outputStream.CanRead);
                Stream? oldStream = Archive.ReopenStream(reOutStream);
                Debug.Assert(oldStream == NodeStream);
                if (NodeStream != reOutStream) {
                    NodeStream.Close();
                    NodeStream = reOutStream;
                }
                return;
            } catch {
                // Failed; try to clean up.
                try {
                    outputStream.Close();
                    IFileEntry eip = EntryInParent;
                    Parent.FinishArchiveOutputFile(ref eip, doCompress, false);
                    EntryInParent = eip;
                } catch (Exception inex) {
                    AppHook.LogE("Archive cleanup failed: " + inex.Message);
                }
                throw;
            }
        }

        // Start of update of an archive inside us (an archive).
        internal protected override Stream CreateArchiveOutputFile(IFileEntry entryHere) {
            // Create a temporary file on the host filesystem.
            string tmpPath = Path.GetTempFileName();
            FileAttributes attribs = File.GetAttributes(tmpPath);
            File.SetAttributes(tmpPath, attribs | FileAttributes.Temporary);
            mTmpStream = new FileStream(tmpPath, FileMode.Open, FileAccess.ReadWrite,
                FileShare.None, 8192, FileOptions.DeleteOnClose);
            return mTmpStream;
        }

        // Finish update of an archive inside us (an archive).
        internal protected override Stream? FinishArchiveOutputFile(ref IFileEntry entryHere,
                bool doCompress, bool success) {
            Debug.Assert(Parent != null);
            if (success) {
                Debug.Assert(mTmpStream != null && mTmpStream.CanRead);
                AppHook.LogI("Finishing arc-in-arc, success=" + success + " " + entryHere);
                CompressionFormat fmt =
                    doCompress ? CompressionFormat.Default : CompressionFormat.Uncompressed;

                // Create a new file on disk, or a temp file on the host filesystem.
                Stream outputStream = Parent.CreateArchiveOutputFile(EntryInParent);
                try {
                    // Our child is an archive that was updated.  Replace our entry with the
                    // new archive.
                    Archive.StartTransaction();
                    Archive.DeletePart(entryHere, FilePart.DataFork);
                    SimplePartSource source = new SimplePartSource(mTmpStream, true);
                    Archive.AddPart(entryHere, FilePart.DataFork, source, fmt);
                    Archive.CommitTransaction(outputStream);

                    // Close our old IArchive stream, so the old file can be deleted.
                    Debug.Assert(NodeStream.CanRead);
                    NodeStream.Close();

                    // Do the file replacement in the parent.
                    IFileEntry eip = EntryInParent;
                    Stream? reOutStream = Parent.FinishArchiveOutputFile(ref eip, doCompress, true);
                    Debug.Assert(reOutStream != null);

                    EntryInParent = eip;
                    Stream? oldStream = Archive.ReopenStream(reOutStream);
                    Debug.Assert(oldStream == outputStream && !oldStream.CanRead);
                    NodeStream = reOutStream;

                    // Return the stream with the new archive in it.  Technically what we should
                    // do here is open the new NodeStream, extract the archive from it (to
                    // memory or a temp file), and return that stream.  However, it's a temporary
                    // stream either way, and the contents will be identical, so there's no
                    // reason we can't just hand back the stream we created to hold the output.
                    //
                    // The "entryHere" value doesn't need to change, because we used
                    // ReopenStream.
                    Stream newChildStream = mTmpStream;
                    mTmpStream = null;
                    return newChildStream;
                } finally {
                    Archive.CancelTransaction();        // does nothing after commit
                }
            } else {
                // The temp file on host system self-deleted when stream was closed.
                AppHook.LogW("Arc-in-arc update failed, discarding temp stream");
                Debug.Assert(!mTmpStream!.CanRead);   // should have been closed
                mTmpStream = null;
                return null;
            }
        }

        // Update a disk image inside of us (an archive).
        internal protected override void DiskUpdated(Stream diskImageStream, IFileEntry entryHere,
                bool doCompress) {
            Debug.Assert(Parent != null);
            AppHook.LogI("Disk-in-arc updated, entry=" + entryHere);
            CompressionFormat fmt =
                doCompress ? CompressionFormat.Default : CompressionFormat.Uncompressed;
            // Ask the parent to create a temp file.
            Stream outputStream = Parent.CreateArchiveOutputFile(EntryInParent);
            FilePart part = Archive is NuFX ? FilePart.DiskImage : FilePart.DataFork;
            try {
                // Rewrite the disk image entry.  Set the "leave open" flag on the part source
                // so our disk image Stream doesn't get closed.
                Archive.StartTransaction();
                Archive.DeletePart(entryHere, part);
                SimplePartSource source = new SimplePartSource(diskImageStream, true);
                Archive.AddPart(entryHere, part, source, fmt);

                // Commit our changes to the temp file.
                Archive.CommitTransaction(outputStream);
                NodeStream.Close();
                NodeStream = outputStream;

                // Rotate the temp file.
                IFileEntry eip = EntryInParent;
                Stream? reOutStream = Parent.FinishArchiveOutputFile(ref eip, doCompress, true);
                Debug.Assert(reOutStream != null);
                EntryInParent = eip;
                Stream? oldStream = Archive.ReopenStream(reOutStream);
                Debug.Assert(oldStream == NodeStream);
                if (NodeStream != reOutStream) {
                    NodeStream.Close();
                    NodeStream = reOutStream;
                }
            } catch {
                outputStream.Close();
                IFileEntry eip = EntryInParent;
                Parent.FinishArchiveOutputFile(ref eip, doCompress, false);
            } finally {
                Archive.CancelTransaction();        // does nothing after commit
            }
        }
    }

    /// <summary>
    /// Node representing an IDiskImage.
    /// </summary>
    public class DiskImageNode : DiskArcNode {
        private const string SHORT_TEMP_PREFIX = "cp2_";

        public IDiskImage DiskImage { get; protected set; }

        private IFileEntry? mTmpFileEntry;
        private DiskFileStream? mTmpStream;

        public DiskImageNode(DiskArcNode? parent, Stream nodeStream, IDiskImage diskImage,
                IFileEntry entryInParent, AppHook appHook) :
                base(parent, nodeStream, entryInParent, appHook) {
            DiskImage = diskImage;

            DebugIdentifier = ((parent != null) ? parent.DebugIdentifier + "/" : "") +
                "DiskImage";
        }

        protected override void Dispose(bool disposing) {
            if (disposing) {
                DisposeChildren();
                DiskImage.Dispose();
            }
            base.Dispose(disposing);
        }

        // Save updates to a disk image.
        public override void SaveUpdates(bool doCompress) {
            AppHook.LogI("Disk image updated");
            Debug.Assert(Parent != null);
            DiskImage.Flush();
            // Tell our parent that we've been updated.
            Parent.DiskUpdated(NodeStream, EntryInParent, doCompress);
            // All changes should be flushed, clear the "modified" flag.
            DiskImage.IsModified = false;
        }

        // Start of update of archive inside us (a disk image).
        internal protected override Stream CreateArchiveOutputFile(IFileEntry entryHere) {
            // Create a temporary file in the same directory where the archive lives.
            IFileSystem fs = entryHere.GetFileSystem()!;
            IFileEntry parentDir = entryHere.ContainingDir;
            string tmpBaseName = SHORT_TEMP_PREFIX + entryHere.FileName;

            // Loop until we find an unused name.
            string adjName = fs.AdjustFileName(tmpBaseName);
            int extra = 0;
            while (fs.TryFindFileEntry(parentDir, adjName, out IFileEntry unused) &&
                    extra < 1000) {
                extra++;
                adjName = fs.AdjustFileName(tmpBaseName + extra.ToString());
            }
            if (extra == MAX_TMP_ITER) {
                throw new IOException("Unable to create temp file in " + parentDir.FullPathName +
                    "; last attempt was '" + adjName + "'");
            }

            mTmpFileEntry = fs.CreateFile(parentDir, adjName, CreateMode.File);
            mTmpStream = fs.OpenFile(mTmpFileEntry, FileAccessMode.ReadWrite,
                FilePart.DataFork);
            AppHook.LogI("Created archive output file: " + mTmpFileEntry.FullPathName);
            return mTmpStream;
        }

        // Finish update of archive inside us (a disk image).
        internal protected override Stream? FinishArchiveOutputFile(ref IFileEntry entryHere,
                bool doCompress, bool success) {
            Debug.Assert(mTmpFileEntry != null);
            IFileSystem fs = entryHere.GetFileSystem()!;
            IFileEntry parentDir = entryHere.ContainingDir;
            if (success) {
                AppHook.LogI("Arc-in-disk update succeeded, replacing '" + entryHere.FullPathName +
                    "' with '" + mTmpFileEntry.FullPathName + "'");

                // Close the stream so we can rename the file.
                Debug.Assert(mTmpStream != null && mTmpStream.CanRead);
                mTmpStream.Close();
                mTmpStream = null;

                string fileName = entryHere.FileName;
                fs.DeleteFile(entryHere);
                fs.MoveFile(mTmpFileEntry, parentDir, fileName);
                entryHere = mTmpFileEntry;      // replace the EntryInParent value
                DiskFileStream reNewStream = fs.OpenFile(entryHere, FileAccessMode.ReadWrite,
                    FilePart.DataFork);
                mTmpFileEntry = null;

                // We have been altered; inform our parent.  We need to flush here in case
                // we're a format that caches data (e.g. WOZ).
                DiskImage.Flush();
                Parent!.DiskUpdated(NodeStream, EntryInParent, doCompress);

                return reNewStream;
            } else {
                AppHook.LogW("Arc-in-disk update failed, removing '" +
                    mTmpFileEntry.FullPathName + "'");
                mTmpStream!.Close();
                fs.DeleteFile(mTmpFileEntry);
                mTmpFileEntry = null;
                mTmpStream = null;
                return null;
            }
        }

        // Update a disk image inside us (a disk image).
        internal protected override void DiskUpdated(Stream diskImageStream, IFileEntry entryHere,
                bool doCompress) {
            // We don't need to do much, because child disk images are overlaid, and anything
            // they write is already part of our stream.  We do need to propagate the message
            // up the tree in case there's an archive above us.
            AppHook.LogD("Disk child of disk updated: " + entryHere);
            DiskImage.Flush();
            Parent!.DiskUpdated(NodeStream, EntryInParent, doCompress);
        }
    }

    /// <summary>
    /// Node representing the host filesystem.  There will be only one of these, at the root,
    /// and it will have only one child.
    /// </summary>
    public class HostFileNode : DiskArcNode {
        private const string TEMP_PREFIX = "cp2tmp_";

        public string PathName { get; private set; }

        private string mTmpPathName = string.Empty;
        private Stream? mTmpStream = null;

        public HostFileNode(string pathName, AppHook appHook) :
                base(null, new MemoryStream(new byte[0]), IFileEntry.NO_ENTRY, appHook) {
            PathName = pathName;

            DebugIdentifier = "Host(" + pathName + ")";
        }

        protected override void Dispose(bool disposing) {
            if (disposing) {
                if (CheckHealth()) {
                    AppHook.LogI("Disposing DiskArcNode tree; health check OK");
                } else {
                    AppHook.LogE("DiskArcNode tree health check failed");
                }
                DisposeChildren();
            }
            base.Dispose(disposing);
        }

        public override void SaveUpdates(bool doCompress) {
            // nothing to do here
            throw new NotImplementedException("should not call here");
        }

        // Start of update of archive inside us (host FS).
        internal protected override Stream CreateArchiveOutputFile(IFileEntry entryHere) {
            Debug.Assert(entryHere == IFileEntry.NO_ENTRY);
            string dirName = Path.GetDirectoryName(PathName)!;
            string fileName = Path.GetFileName(PathName);
            string tmpFileName = TEMP_PREFIX + fileName;
            string tmpBaseName = Path.Combine(dirName, tmpFileName);

            // Don't overwrite existing file.
            string tmpPathName = tmpBaseName;
            int extra = 0;
            while (File.Exists(tmpPathName) || Directory.Exists(tmpPathName) &&
                    extra < MAX_TMP_ITER) {
                extra++;
                tmpPathName = tmpBaseName + extra.ToString();
            }
            if (extra == MAX_TMP_ITER) {
                throw new IOException("Unable to create temp file; last attempt was " +
                    tmpPathName);
            }

            // Create output file.  This must NOT be delete-on-close, since on success we
            // will be keeping it.
            mTmpPathName = tmpPathName;
            mTmpStream = new FileStream(tmpPathName, FileMode.CreateNew, FileAccess.ReadWrite);
            AppHook.LogI("Created host archive output file: '" + mTmpPathName + "'");
            return mTmpStream;
        }

        // Finish update of archive inside us (host FS).
        internal protected override Stream? FinishArchiveOutputFile(ref IFileEntry entryHere,
                bool doCompress, bool success) {
            if (success) {
                AppHook.LogI("Arc-in-host update succeeded, replacing '" + PathName + "' with '" +
                    mTmpPathName + "'");
                Debug.Assert(mTmpStream != null && mTmpStream.CanRead);
                mTmpStream.Close();
                File.Delete(PathName);
                File.Move(mTmpPathName, PathName);
                Stream reNewStream = new FileStream(PathName, FileMode.Open, FileAccess.ReadWrite,
                    FileShare.Read);
                mTmpPathName = string.Empty;
                mTmpStream = null;
                return reNewStream;
            } else {
                AppHook.LogW("Arc-in-host update failed, removing '" + mTmpPathName + "'");
                File.Delete(mTmpPathName);
                mTmpPathName = string.Empty;
                mTmpStream = null;
                return null;
            }
        }

        // Update a disk image inside us (host FS).
        internal protected override void DiskUpdated(Stream diskImageStream, IFileEntry entryHere,
                bool doCompress) {
            diskImageStream.Flush();
            // nothing to do here
            AppHook.LogI("Host-level disk image updated");
        }
    }
}
