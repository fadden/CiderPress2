﻿/*
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
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;
using System.Windows;

namespace cp2_wpf.WPFCommon {
    /// <summary>
    /// Classes that provide access to file streams dropped or pasted into a Windows app.
    /// </summary>
    /// <remarks>
    /// Substantially from <see href="https://stackoverflow.com/a/31972769/294248"/>, with some
    /// fixes and rearranging.  This is the "receiving" side of a clipboard operation; see
    /// <see cref="Delay.VirtualFileDataObject"/> for the "sending" side.
    /// </remarks>
    public static class ClipHelper {
        // Names of formats found in clipboard data object.
        public const string DESC_ARRAY_FORMAT = "FileGroupDescriptorW";
        public const string FILE_CONTENTS_FORMAT = "FileContents";

        /// <summary>
        /// Obtains a stream with the file's contents.  Do not call this on indices that don't
        /// have contents, e.g. directories.
        /// </summary>
        /// <param name="dataObject">Data object that has been dropped or pasted.</param>
        /// <param name="index">Index of file.</param>
        /// <returns>Stream with file contents.  It will be readable, but may not be
        ///   seekable.</returns>
        public static Stream? GetFileContents(System.Windows.IDataObject dataObject, int index) {
            int formatId = DataFormats.GetDataFormat("FileContents").Id;
            return GetFileContents(dataObject, index, (short)formatId);
        }

        internal static Stream? GetFileContents(System.Windows.IDataObject dataObject, int index,
                short formatId) {
            // cast the default IDataObject to a COM IDataObject
            System.Runtime.InteropServices.ComTypes.IDataObject comDataObject;
            comDataObject = (System.Runtime.InteropServices.ComTypes.IDataObject)dataObject;

            FORMATETC formatetc = new FORMATETC();
            formatetc.cfFormat = formatId;
            formatetc.dwAspect = DVASPECT.DVASPECT_CONTENT;
            formatetc.lindex = index;
            formatetc.tymed = TYMED.TYMED_ISTREAM | TYMED.TYMED_HGLOBAL;

            // using the COM IDataObject interface get the data using the defined FORMATETC
            STGMEDIUM medium;
            try {
                comDataObject.GetData(ref formatetc, out medium);
            } catch (COMException ex) {
                // GetData() will throw a COMException with message "Invalid FORMATETC structure"
                // if the file doesn't have contents (e.g. it's a directory).
                Debug.WriteLine("GetData throw a COMException for index=" + index +
                    ": " + ex.Message);
                return null;
            } catch (Exception ex) {
                // Unexpected.
                Debug.WriteLine("GetFileContents failed: " + ex);
                Debug.Assert(false);
                return null;
            }

            // TYpe of MEDium - https://learn.microsoft.com/en-us/windows/win32/api/objidl/ne-objidl-tymed
            switch (medium.tymed) {
                case TYMED.TYMED_ISTREAM:
                    //Debug.WriteLine("Got IStream, punk=" + medium.pUnkForRelease);
                    return GetIStream(medium);
                default:
                    throw new NotSupportedException();
            }
        }

        private const int STREAM_SEEK_SET = 0;
        private const int STREAM_SEEK_CUR = 1;

        /// <summary>
        /// Copies the IStream into a managed stream.
        /// </summary>
        /// <remarks>
        /// Currently this just copies the full amount of data to a MemoryStream, which would
        /// be bad for larger files.
        /// </remarks>
        private static MemoryStream GetIStream(STGMEDIUM medium) {
            // marshal the returned pointer to a IStream object
            IStream iStream = (IStream)Marshal.GetObjectForIUnknown(medium.unionmember);
            Marshal.Release(medium.unionmember);

            // get the STATSTG of the IStream to determine how many bytes are in it
            System.Runtime.InteropServices.ComTypes.STATSTG iStreamStat;
            iStream.Stat(out iStreamStat, 0);
            int iStreamSize = (int)iStreamStat.cbSize;

            // read the data from the IStream into a managed byte array
            // cf. https://stackoverflow.com/q/1958950/294248
            byte[] iStreamContent = new byte[iStreamSize];

            IntPtr argBuffer = Marshal.AllocCoTaskMem(Marshal.SizeOf(typeof(long)));
            try {
                // See if we're seeked to the beginning.  If not, seek there.
                iStream.Seek(0, STREAM_SEEK_CUR, argBuffer);
                long curPosn = Marshal.ReadInt64(argBuffer);
                if (curPosn != 0) {
                    Debug.WriteLine("NOTE: initial IStream position=" + curPosn);
                    iStream.Seek(0, STREAM_SEEK_SET, IntPtr.Zero);
                }
                iStream.Read(iStreamContent, iStreamContent.Length, argBuffer);
                // The 3rd argument, which gets the actual number of bytes read, is documented as
                // "pointer to ULONG", but that's in the C++ sense (32-bit value).
                long actual = Marshal.ReadInt32(argBuffer);
                if (actual != iStreamContent.Length) {
                    Debug.WriteLine("Data read mismatch: expected len=" + iStreamContent.Length +
                        " actual=" + actual);
                }
            } finally {
                Marshal.FreeCoTaskMem(argBuffer);
            }

            // wrap the managed byte array into a memory stream
            return new MemoryStream(iStreamContent);
        }

        /// <summary>
        /// Obtains the IStream from the clipboard, executing the code on a short-lived thread.
        /// This is necessary because Windows clipboard operations can only be called from
        /// Single Thread Apartment (STA) threads, and while the GUI thread qualifies, the
        /// BackgroundWorker pool threads do not.
        /// </summary>
        /// <remarks>
        /// <para>This is not generally needed for IDataObject, e.g. the object received from
        /// a drag &amp; drop operation can be accessed on a background worker thread.</para>
        /// </remarks>
        /// <param name="index">Stream index.</param>
        /// <param name="formatId">Clipboard format identifier.</param>
        /// <returns>Readable stream, or null if there was an error on the remote side.</returns>
        public static Stream? GetClipboardContentsSTA(int index, short formatId) {
            // Starting a new thread for every fork of every file is probably suboptimal.
            // We may want to have a dedicated thread for the span of a single operation.
            Stream? inStream = null;
            Thread staThread = new Thread(
                delegate () {
                    // Need to query for the clipboard data here or we get an error that says the
                    // object was marshalled for a different thread (RPC_E_WRONG_THREAD).
                    System.Windows.IDataObject dataObj = Clipboard.GetDataObject();

                    // The Stream will have all of the data written into it already.  The result
                    // will be null if there was a read error on the remote side.
                    inStream = GetFileContents(dataObj, index, formatId);
                });
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.Start();
            staThread.Join();
            return inStream;
        }


        /// <summary>
        /// Managed representation of a FileDescriptor structure.
        /// <see href="https://learn.microsoft.com/en-us/windows/win32/api/shlobj_core/ns-shlobj_core-filedescriptorw"/>
        /// </summary>
        internal sealed class FileDescriptor {
            /// <summary>
            /// Specifies which fields are valid.
            /// </summary>
            [Flags]
            internal enum FileDescriptorFlags : uint {
                ClsId = 0x00000001,
                SizePoint = 0x00000002,
                Attributes = 0x00000004,
                CreateTime = 0x00000008,
                AccessTime = 0x00000010,
                WritesTime = 0x00000020,
                FileSize = 0x00000040,
                ProgressUI = 0x00004000,
                LinkUI = 0x00008000,
                Unicode = 0x80000000,
            }

            // Bit flags, indicating which of the other fields are valid.
            public FileDescriptorFlags Flags { get; set; }
            // File type identifier.
            public Guid ClassId { get; set; }
            // Width and height of file icon.
            public Size Size { get; set; }
            // Screen coordinates of file object.
            public Point Point { get; set; }
            // File attribute flags (https://learn.microsoft.com/en-us/windows/win32/fileio/file-attribute-constants).
            public FileAttributes FileAttributes { get; set; }
            // FILETIME date value (https://learn.microsoft.com/en-us/windows/win32/api/minwinbase/ns-minwinbase-filetime).
            public DateTime CreationTime { get; set; } = DateTime.MinValue;
            public DateTime LastAccessTime { get; set; } = DateTime.MinValue;
            public DateTime LastWriteTime { get; set; } = DateTime.MinValue;
            // File size, stored as two 32-bit words, with the high part first.
            public Int64 FileSize { get; set; } = -1;
            // Null-terminated UCS-2 filename string.  There's no "valid" flag for this field.
            public string FileName { get; set; }

            public FileDescriptor(BinaryReader reader) {
                // The Flags field specifies which fields are actually valid, but the
                // incoming data is fixed-size so we need to read all fields.  Some of the
                // constructors throw exceptions if the values are out of whack.  (Try
                // copying files and directories from a Zip archive opened in Windows Explorer.)

                Flags = (FileDescriptorFlags)reader.ReadUInt32();
                byte[] guid = reader.ReadBytes(16);
                int sizeWidth = reader.ReadInt32();
                int sizeHeight = reader.ReadInt32();
                int pointWidth = reader.ReadInt32();
                int pointHeight = reader.ReadInt32();
                uint fileAttr = reader.ReadUInt32();
                long creationTicks = reader.ReadInt64();
                long lastAccessTicks = reader.ReadInt64();
                long lastWriteTicks = reader.ReadInt64();
                uint fileSizeHi = reader.ReadUInt32();      // high word comes first
                uint fileSizeLo = reader.ReadUInt32();
                byte[] nameBytes = reader.ReadBytes(520);

                if ((Flags & FileDescriptorFlags.ClsId) != 0) {
                    ClassId = new Guid(guid);
                }
                if ((Flags & FileDescriptorFlags.SizePoint) != 0) {
                    try {
                        // Size throws an exception on negative values.
                        Size = new Size(sizeWidth, sizeHeight);
                        Point = new Point(pointWidth, pointHeight);
                    } catch (ArgumentException) {
                        Debug.WriteLine("Unable to set size/point");
                    }
                }
                if ((Flags & FileDescriptorFlags.Attributes) != 0) {
                    FileAttributes = (FileAttributes)fileAttr;
                }
                if ((Flags & FileDescriptorFlags.CreateTime) != 0) {
                    try {
                        CreationTime = new DateTime(1601, 1, 1).AddTicks(creationTicks);
                    } catch (ArgumentOutOfRangeException) {
                        Debug.WriteLine("Unable to set creation time, ticks=" + creationTicks);
                    }
                }
                if ((Flags & FileDescriptorFlags.AccessTime) != 0) {
                    try {
                        LastAccessTime = new DateTime(1601, 1, 1).AddTicks(lastAccessTicks);
                    } catch (ArgumentOutOfRangeException) {
                        Debug.WriteLine("Unable to set last access time, ticks=" + lastAccessTicks);
                    }
                }
                if ((Flags & FileDescriptorFlags.WritesTime) != 0) {
                    try {
                        LastWriteTime = new DateTime(1601, 1, 1).AddTicks(lastWriteTicks);
                    } catch (ArgumentOutOfRangeException) {
                        Debug.WriteLine("Unable to set last write time, ticks=" + lastWriteTicks);
                    }
                }
                if ((Flags & FileDescriptorFlags.FileSize) != 0) {
                    FileSize = fileSizeLo | ((long)fileSizeHi << 32);
                }
                if ((Flags & FileDescriptorFlags.Unicode) != 0) {
                    // This flag doesn't seem to be set by Windows Explorer's Zip handling,
                    // though the filename is UTF-16.  We know from the format name that this
                    // structure was passed via "FileGroupDescriptorW", not "...A", so we can
                    // probably just ignore the flag.
                }

                // Extract null-terminated UTF-16 filename.
                int idx;
                for (idx = 0; idx < nameBytes.Length; idx += 2) {
                    if (nameBytes[idx] == 0 && nameBytes[idx + 1] == 0)
                        break;
                }
                FileName = UnicodeEncoding.Unicode.GetString(nameBytes, 0, idx);
            }
        }

        internal static class FileDescriptorReader {
            public static IEnumerable<FileDescriptor> Read(Stream fileDescriptorStream) {
                BinaryReader reader = new BinaryReader(fileDescriptorStream);
                var count = reader.ReadUInt32();
                while (count > 0) {
                    FileDescriptor descriptor = new FileDescriptor(reader);

                    yield return descriptor;

                    count--;
                }
            }

            public static IEnumerable<string> ReadFileNames(Stream fileDescriptorStream) {
                BinaryReader reader = new BinaryReader(fileDescriptorStream);
                var count = reader.ReadUInt32();
                while (count > 0) {
                    FileDescriptor descriptor = new FileDescriptor(reader);

                    yield return descriptor.FileName;

                    count--;
                }
            }
        }
    }
}
