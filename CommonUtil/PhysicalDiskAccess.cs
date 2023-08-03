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
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CommonUtil {
    /// <summary>
    /// Functions for getting access to physical disks.  This is system-specific and may
    /// require elevated privileges.
    /// </summary>
    public static class PhysicalDiskAccess {
        /// <summary>
        /// Physical disk information.
        /// </summary>
        public class DiskInfo {
            public enum MediaTypes {
                Unknown = 0,
                Fixed,              // hard drives
                Removable,          // USB sticks, CF cards
                Other,              // floppies, CD-ROMs
            }

            /// <summary>
            /// Device number, or -1 if this platform doesn't use device numbers.
            /// </summary>
            public int Number { get; private set; }

            /// <summary>
            /// Device filename, or an empty string if this platform doesn't use filenames.
            /// </summary>
            public string Name { get; private set; } = string.Empty;

            /// <summary>
            /// Media type.
            /// </summary>
            public MediaTypes MediaType { get; private set; }

            /// <summary>
            /// Size of the disk, in bytes.  Expected to be a multiple of 512.
            /// </summary>
            public long Size { get; private set; }

            public DiskInfo(int num, string name, MediaTypes mediaType, long size) {
                Number = num;
                Name = name;
                MediaType = mediaType;
                Size = size;
            }
        }

        /// <summary>
        /// Generates a list of disk devices found on this system.
        /// </summary>
        /// <returns>List, or null if the feature isn't supported.</returns>
        public static List<DiskInfo>? GetDiskList() {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
                return Win.GetDiskList();
            } else {
                return null;
            }
        }

        #region Windows

        public static class Win {
            // There are various ways to enumerate the set of physical disk devices on a
            // modern Windows system:
            //
            //  1. Use the Windows Management Instrumentation (WMI) interfaces.  This is available
            //     to .NET code via the System.Management package, which is Windows-only and must
            //     be added to the project with NuGet.
            //  2. Query the registry key
            //     "HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\disk\Enum".
            //  3. Get the list of logical devices and extract the physical device numbers from the
            //     extent lists (only works if the drive is formatted in a way Windows recognizes).
            //  4. Try a range of physical device numbers and see what works.
            //
            // The original CiderPress used approach #4.  This seems like a bad way to go about
            // it, but it's far simpler than the others and it seems to work.
            //
            // https://stackoverflow.com/q/327718/294248
            // https://stackoverflow.com/a/39869074/294248

            const int MAX_DEVICES = 16;     // arbitrary, not based on anything

            /// <summary>
            /// Generates a list of disk devices found on a Windows system.
            /// </summary>
            /// <returns>List of disk devices found.</returns>
            public static List<DiskInfo> GetDiskList() {
                List<DiskInfo> diskList = new List<DiskInfo>();

                Debug.WriteLine("Scanning for physical disks...");
                for (int dev = 0; dev < MAX_DEVICES; dev++) {
                    string fileName = @"\\.\PhysicalDrive" + dev;
                    SafeFileHandle handle = CreateFile(
                        fileName,                           // lpFileName
                        0,                                  // dwDesiredAccess = none
                        FILE_SHARE_READ | FILE_SHARE_WRITE, // dwShareMode
                        IntPtr.Zero,                        // lpSecurityAttributes
                        OPEN_EXISTING,                      // dwCreationDisposition
                        0,                                  // dwFlagsAndAttributes
                        IntPtr.Zero);                       // hTemplateFile
                    if (handle.IsInvalid) {
                        //Debug.WriteLine("Unable to open device " + dev);
                        continue;
                    }

                    if (GetGeometry(handle, out DISK_GEOMETRY_EX geometry)) {
                        DiskInfo.MediaTypes mtype;
                        switch (geometry.Geometry.MediaType) {
                            case MEDIA_TYPE.RemovableMedia:
                                mtype = DiskInfo.MediaTypes.Removable;
                                break;
                            case MEDIA_TYPE.FixedMedia:
                                mtype = DiskInfo.MediaTypes.Fixed;
                                break;
                            case MEDIA_TYPE.Unknown:
                                mtype = DiskInfo.MediaTypes.Unknown;
                                break;
                            default:        // various floppy disk values
                                mtype = DiskInfo.MediaTypes.Other;
                                break;
                        }
                        DiskInfo di = new DiskInfo(dev, fileName, mtype, geometry.DiskSize);
                        diskList.Add(di);
                    } else {
                        int err = Marshal.GetLastWin32Error();
                        Debug.WriteLine("DeviceIOControl(GET_DRIVE_GEOMETRY_EX) failed with " +
                            "err 0x" + err.ToString("x8"));
                    }

                    handle.Dispose();
                }

                return diskList;
            }

            /// <summary>
            /// Opens a disk device, e.g. @"\\.\PhysicalDrive0".
            /// </summary>
            /// <param name="deviceName">Disk device name.</param>
            /// <param name="isReadOnly">True if we should open the disk read-only.</param>
            /// <param name="errCode">Error code, if the open call failed.</param>
            /// <returns>Handle to disk device.</returns>
            public static SafeFileHandle OpenDisk(string deviceName, bool isReadOnly,
                    out long size, out int errCode) {
                uint access = GENERIC_READ | (isReadOnly ? 0 : GENERIC_WRITE);

                SafeFileHandle handle = CreateFile(
                    deviceName,                             // lpFileName
                    access,                                 // dwDesiredAccess
                    FILE_SHARE_READ | FILE_SHARE_WRITE,     // dwShareMode
                    IntPtr.Zero,                            // lpSecurityAttributes
                    OPEN_EXISTING,                          // dwCreationDisposition
                    0,                                      // dwFlagsAndAttributes
                    IntPtr.Zero);                           // hTemplateFile
                if (handle.IsInvalid) {
                    errCode = Marshal.GetLastWin32Error();
                    size = -1;
                    return handle;
                }

                // Need to get the device size.  It's not available from the handle, so the
                // stream we create won't have it.
                if (!GetGeometry(handle, out DISK_GEOMETRY_EX geometry)) {
                    errCode = Marshal.GetLastWin32Error();
                    size = -1;
                    return handle;
                }
                size = geometry.DiskSize;
                errCode = 0;
                return handle;
            }

            public const short FILE_ATTRIBUTE_NORMAL = 0x80;
            public const short INVALID_HANDLE_VALUE = -1;
            public const uint FILE_SHARE_READ = 0x01;
            public const uint FILE_SHARE_WRITE = 0x02;
            public const uint GENERIC_READ = 0x80000000;
            public const uint GENERIC_WRITE = 0x40000000;
            public const uint CREATE_NEW = 1;
            public const uint CREATE_ALWAYS = 2;
            public const uint OPEN_EXISTING = 3;

            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess,
                uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition,
                uint dwFlagsAndAttributes, IntPtr hTemplateFile);

            // thanks: https://pinvoke.net/default.aspx/kernel32/DeviceIoControl.html
            public enum EMethod : uint {
                Buffered = 0,
                InDirect = 1,
                OutDirect = 2,
                Neither = 3
            }
            public enum EFileDevice : uint {
                Disk = 0x00000007,
            }
            public enum EIOControlCode : uint {
                DiskGetDriveGeometry = (EFileDevice.Disk << 16) | (0x0000 << 2) |
                    EMethod.Buffered | (0 << 14),
                DiskGetDriveGeometryEx = (EFileDevice.Disk << 16) | (0x0028 << 2) |
                    EMethod.Buffered | (0 << 14),
                DiskGetPartitionInfo = (EFileDevice.Disk << 16) | (0x0001 << 2) |
                    EMethod.Buffered | (FileAccess.Read << 14),
                DiskGetPartitionInfoEx = (EFileDevice.Disk << 16) | (0x0012 << 2) |
                    EMethod.Buffered | (0 << 14),
            }

            [DllImport("Kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
            public static extern bool DeviceIoControl(
                SafeFileHandle hDevice,
                EIOControlCode IoControlCode,
                [In] IntPtr InBuffer,
                [In] uint nInBufferSize,
                [Out] IntPtr OutBuffer,
                [In] uint nOutBufferSize,
                out uint pBytesReturned,
                [In] IntPtr Overlapped);

            public enum MEDIA_TYPE : uint {
                Unknown,
                F5_1Pt2_512,
                F3_1Pt44_512,
                F3_2Pt88_512,
                F3_20Pt8_512,
                F3_720_512,
                F5_360_512,
                F5_320_512,
                F5_320_1024,
                F5_180_512,
                F5_160_512,
                RemovableMedia,
                FixedMedia,
                F3_120M_512,
                F3_640_512,
                F5_640_512,
                F5_720_512,
                F3_1Pt2_512,
                F3_1Pt23_1024,
                F5_1Pt23_1024,
                F3_128Mb_512,
                F3_230Mb_512,
                F8_256_128,
                F3_200Mb_512,
                F3_240M_512,
                F3_32M_512
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct DISK_GEOMETRY {
                public long Cylinders;
                public MEDIA_TYPE MediaType;
                public int TracksPerCylinder;
                public int SectorsPerTrack;
                public int BytesPerSector;
            }
            [StructLayout(LayoutKind.Sequential)]
            public struct DISK_GEOMETRY_EX {
                public DISK_GEOMETRY Geometry;
                public long DiskSize;
                // DISK_PARTITION_INFO and DISK_DETECTION_INFO follow if there's room
                public byte MoreStuff;
            }

            private static bool GetGeometry(SafeFileHandle handle, out DISK_GEOMETRY_EX geometry) {
                uint bytesReturned;
                int bufSize = Marshal.SizeOf(typeof(DISK_GEOMETRY_EX));
                IntPtr geoBuf = Marshal.AllocHGlobal(bufSize);
                bool ok = DeviceIoControl(
                    handle,                                 // hDevice
                    EIOControlCode.DiskGetDriveGeometryEx,  // dwIoControlCode
                    IntPtr.Zero,                            // lpInBuffer
                    0,                                      // nInBufferSize
                    geoBuf,                                 // lpOutBuffer
                    (uint)bufSize,                          // nOutBufferSize
                    out bytesReturned,                      // lpBytesReturned
                    IntPtr.Zero);                           // lpOverlapped

                if (ok) {
                    geometry = Marshal.PtrToStructure<DISK_GEOMETRY_EX>(geoBuf);
                    return true;
                } else {
                    geometry = new DISK_GEOMETRY_EX();
                    return false;
                }
            }
        }

        #endregion Windows
    }
}
