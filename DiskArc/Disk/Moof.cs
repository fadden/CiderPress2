/*
 * Copyright 2026 faddenSoft
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
using static DiskArc.Defs;

namespace DiskArc.Disk {
    /// <summary>
    /// MOOF disk image format.
    /// </summary>
    public class Moof : Wozoof {
        private static readonly NibCharacteristics sCharacteristics = new NibCharacteristics(
            name: "MOOF",
            isByteAligned: false,
            isFixedLength: false,
            hasPartial525Tracks: false);
        public override NibCharacteristics Characteristics => sCharacteristics;


        // IDiskImage-required delegate
        public static bool TestKind(Stream stream, AppHook appHook) {
            stream.Position = 0;
            if (stream.Length < MIN_FILE_LEN || stream.Length > MAX_FILE_LEN) {
                // Too big, or too small to hold signature/INFO/TMAP, much less TRKS.
                return false;
            }
            byte[] sigBuf = new byte[SIGNATURE_M.Length];
            stream.ReadExactly(sigBuf, 0, SIGNATURE_M.Length);
            if (RawData.CompareBytes(sigBuf, SIGNATURE_M, SIGNATURE_M.Length)) {
                return true;
            }
            return false;
        }

        protected Moof(Stream stream, AppHook appHook) : base(stream, appHook) { }

        /// <summary>
        /// Opens a MOOF disk image file.
        /// </summary>
        /// <param name="stream">Disk image stream.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <returns>MOOF instance.</returns>
        /// <exception cref="NotSupportedException">Data stream is not a MOOF disk image.</exception>
        public static Moof OpenDisk(Stream stream, AppHook appHook) {
            if (!TestKind(stream, appHook)) {
                throw new NotSupportedException("Incompatible data stream");
            }
            Moof newDisk = new Moof(stream, appHook);
            newDisk.Parse();

            // Propagate read-only flag.  (See notes in Woz.OpenDisk().)
            newDisk.Info.IsReadOnly = newDisk.IsReadOnly;
            if (newDisk.MetaChunk != null) {
                newDisk.MetaChunk.IsReadOnly = newDisk.IsReadOnly;
            }
            return newDisk;
        }

        /// <summary>
        /// Determines whether the disk configuration is supported.
        /// </summary>
        /// <param name="mediaKind">Kind of disk to create.</param>
        /// <param name="interleave">Disk interleave (2 or 4).</param>
        /// <param name="errMsg">Result: error message, or empty string on success.</param>
        /// <returns>True on success.</returns>
        public static bool CanCreateDisk35(MediaKind mediaKind, int interleave, out string errMsg) {
            errMsg = string.Empty;
            if (mediaKind != MediaKind.GCR_SSDD35 && mediaKind != MediaKind.GCR_DSDD35) {
                errMsg = "Unsupported value for media kind: " + mediaKind;
            }
            if (interleave != 2 && interleave != 4) {
                errMsg = "Interleave must be 2:1 or 4:1";
            }
            return errMsg == string.Empty;
        }


        /// <summary>
        /// Creates a new 3.5" disk image.  The disk will be fully formatted.
        /// </summary>
        /// <param name="stream">Stream into which the new disk image will be written.</param>
        /// <param name="mediaKind">Type of disk to create (SSDD or DSDD).</param>
        /// <param name="interleave">Disk interleave (2 or 4).</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <returns>Newly-created disk image.</returns>
        public static Moof CreateDisk35(Stream stream, MediaKind mediaKind, int interleave,
                SectorCodec codec, AppHook appHook) {
            if (!CanCreateDisk35(mediaKind, interleave, out string errMsg)) {
                throw new ArgumentException(errMsg);
            }

            Moof newDisk = new Moof(stream, appHook);
            try {
                newDisk.DoCreateDisk35(stream, mediaKind, interleave, codec, appHook, true);
            } catch {
                newDisk.Dispose();
                throw;
            }
            return newDisk;
        }

        public override string ToString() {
            return "[MOOF " + DiskKind + "]";
        }
    }
}
