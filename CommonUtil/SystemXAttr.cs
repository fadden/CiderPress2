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
using System.Runtime.InteropServices;

using size_t = System.UInt32;
using ssize_t = System.Int32;

namespace CommonUtil {
    /// <summary>
    /// System-specific extended attribute calls.
    /// </summary>
    /// <remarks>
    /// These are accessible from Mono, but aren't yet available in .NET
    /// (check <see href="https://github.com/dotnet/runtime/issues/49604"/>).
    /// </remarks>
    public static class SystemXAttr {
        /// <summary>
        /// Name of attribute that holds the 32-byte Finder info block.
        /// </summary>
        public const string XATTR_FINDERINFO_NAME = "com.apple.FinderInfo";
        public const int FINDERINFO_LENGTH = 32;

        /// <summary>
        /// True if the current runtime environment is Mac OS.
        /// </summary>
        /// <remarks>
        /// <para>.NET Core returns Unix, not MacOSX, for Environment.OSVersion.Platform.  The
        /// best way to check appears to be the IsOSPlatform() call.</para>
        /// </remarks>
        public static bool IsMacOS {
            get {
                return RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
                //return RuntimeInformation.OSDescription.StartsWith("Darwin");
            }
        }

        /// <summary>
        /// Issues the getxattr() system call.
        /// </summary>
        /// <remarks>
        /// <para>On success, a number of bytes up to the size of the buffer is returned.  If
        /// the buffer is null, the call returns the current size of the attribute.</para>
        /// </remarks>
        /// <param name="path">Full pathname.</param>
        /// <param name="name">Attribute name.</param>
        /// <param name="outBuf">Output buffer; may be null.</param>
        /// <returns>Number of bytes copied to buffer, or -1 on failure.</returns>
        /// <exception cref="IOException">Something unexpected happened.</exception>
        public static int GetXAttr(string path, string name, byte[]? outBuf) {
            // TODO: return value vs. exception is a little weird, but works for us.  Returning
            //   an error code requires translating errno values, so exception is better, but
            //   we want a cheap way to report that the attribute doesn't exist or the call
            //   isn't implemented for this platform.
            if (IsMacOS) {
                return MacOS.GetXAttr(path, name, outBuf, 0, 0);
            } else {
                //throw new NotSupportedException("Unavailable on " +
                //    RuntimeInformation.OSDescription);
                return -1;
            }
        }

        /// <summary>
        /// Issues the setxattr() system call.
        /// </summary>
        /// <param name="path">Full pathname.</param>
        /// <param name="name">Attribute name.</param>
        /// <param name="dataBuf">Data buffer.</param>
        /// <returns>Zero on success, or -1 on failure.</returns>
        /// <exception cref="IOException">Something unexpected happened.</exception>
        public static int SetXAttr(string path, string name, byte[] dataBuf, int flags) {
            if (IsMacOS) {
                return MacOS.SetXAttr(path, name, dataBuf, 0, (MacOS.Options)flags);
            } else {
                //throw new NotSupportedException("Unavailable on " +
                //    RuntimeInformation.OSDescription);
                return -1;
            }
        }

        #region Mac OS

        /// <summary>
        /// Mac OS implementation.
        /// </summary>
        public static class MacOS {
            private enum Error {
                EPERM = 1,
                EIO = 5,
                E2BIG = 7,
                EACCES = 13,
                EFAULT = 14,
                EEXIST = 17,
                ENOTDIR = 20,
                EISDIR = 21,
                EINVAL = 22,
                ENOSPC = 28,
                EROFS = 30,
                ERANGE = 34,
                ENOTSUP = 45,
                ELOOP = 62,
                ENAMETOOLONG = 63,
                ENOATTR = 93,
            }

            [Flags]
            public enum Options {
                XATTR_NOFOLLOW = 0x01,
                XATTR_CREATE = 0x02,
                XATTR_REPLACE = 0x04,
                XATTR_SHOWCOMPRESSION = 0x20,
            }

            [DllImport("libc", SetLastError=true)]
            public static extern ssize_t getxattr(string path, string name, IntPtr value,
                size_t size, uint position, int options);

            public static int GetXAttr(string path, string name, byte[]? outBuf, uint position,
                    Options options) {
                IntPtr nativeBuf;
                size_t nativeBufLen;
                if (outBuf == null) {
                    nativeBufLen = 0;
                    nativeBuf = IntPtr.Zero;
                } else {
                    nativeBufLen = (size_t)outBuf.Length;
                    nativeBuf = Marshal.AllocHGlobal(outBuf.Length);
                }
                try {
                    ssize_t outLen = getxattr(path, name, nativeBuf, nativeBufLen,
                        position, (int)options);
                    if (outLen < 0) {
                        Error errno = (Error)Marshal.GetLastWin32Error();
                        if (errno == Error.ENOATTR) {
                            // Expected behavior when the file doesn't happen to have the attr.
                            return -1;
                        }
                        // Something exotic happened.
                        throw new IOException("getxattr('" + path + "', '" + name + "' failed: " +
                            errno);
                    }
                    if (outBuf != null) {
                        Debug.Assert(outLen <= nativeBufLen);
                        Marshal.Copy(nativeBuf, outBuf, 0, outLen);
                    }
                    return outLen;
                } finally {
                    Marshal.FreeHGlobal(nativeBuf);
                }
            }

            [DllImport("libc", SetLastError = true)]
            public static extern int setxattr(string path, string name, IntPtr value,
                size_t size, uint position, int options);

            public static int SetXAttr(string path, string name, byte[] dataBuf, uint position,
                    Options options) {
                size_t nativeBufLen = (size_t)dataBuf.Length;
                IntPtr nativeBuf = Marshal.AllocHGlobal(dataBuf.Length);
                Marshal.Copy(dataBuf, 0, nativeBuf, dataBuf.Length);
                try {
                    if (setxattr(path, name, nativeBuf, nativeBufLen, position, (int)options) < 0) {
                        Error errno = (Error)Marshal.GetLastWin32Error();
                        // No benign errors are expected.  Something odd happened.
                        throw new IOException("setxattr('" + path + "', '" + name + "' failed: " +
                            errno);
                    }
                    return 0;
                } finally {
                    Marshal.FreeHGlobal(nativeBuf);
                }
            }
        }

        #endregion Mac OS
    }
}
