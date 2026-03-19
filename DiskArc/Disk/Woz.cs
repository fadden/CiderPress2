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
    /// WOZ disk image format.
    /// </summary>
    public class Woz : Wozoof {
        private static readonly NibCharacteristics sCharacteristics = new NibCharacteristics(
            name: "WOZ",
            isByteAligned: false,
            isFixedLength: false,
            hasPartial525Tracks: true);
        public override NibCharacteristics Characteristics => sCharacteristics;


        // IDiskImage-required delegate
        public static bool TestKind(Stream stream, AppHook appHook) {
            stream.Position = 0;
            if (stream.Length < MIN_FILE_LEN || stream.Length > MAX_FILE_LEN) {
                // Too big, or too small to hold signature/INFO/TMAP, much less TRKS.
                return false;
            }
            byte[] sigBuf = new byte[SIGNATURE_W1.Length];
            stream.ReadExactly(sigBuf, 0, SIGNATURE_W1.Length);
            if (RawData.CompareBytes(sigBuf, SIGNATURE_W1, SIGNATURE_W1.Length) ||
                    RawData.CompareBytes(sigBuf, SIGNATURE_W2, SIGNATURE_W2.Length)) {
                return true;
            }
            return false;
        }

        protected Woz(Stream stream, AppHook appHook) : base(stream, appHook) { }

        /// <summary>
        /// Opens a WOZ disk image file.
        /// </summary>
        /// <param name="stream">Disk image stream.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <returns>WOZ instance.</returns>
        /// <exception cref="NotSupportedException">Data stream is not a WOZ disk image.</exception>
        public static Woz OpenDisk(Stream stream, AppHook appHook) {
            if (!TestKind(stream, appHook)) {
                throw new NotSupportedException("Incompatible data stream");
            }
            Woz newDisk = new Woz(stream, appHook);
            newDisk.Parse();

            // Propagate read-only flag.  It depends on the stream and IsDubious (but not on the
            // presence of the FLUX section), so we have to defer this until the file is parsed.
            // TODO: there's some logic that assumes files with FLUX can't be modified.  We need
            // to separate read-only image (stream / dubious) from read-only tracks (FLUX).  Until
            // then we block META updates when FLUX is present.
            newDisk.Info.IsReadOnly = newDisk.IsReadOnly;
            if (newDisk.MetaChunk != null) {
                newDisk.MetaChunk.IsReadOnly = newDisk.IsReadOnly;
            }
            return newDisk;
        }

        /// <summary>
        /// Determines whether the disk configuration is supported.
        /// </summary>
        /// <param name="numTracks">Number of tracks.</param>
        /// <param name="errMsg">Error message, or empty string on success.</param>
        /// <returns>True on success.</returns>
        public static bool CanCreateDisk525(uint numTracks, out string errMsg) {
            errMsg = string.Empty;
            if (numTracks != 35 && numTracks != 40) {
                errMsg = "Number of tracks must be 35 or 40";
            }
            return errMsg == string.Empty;
        }

        /// <summary>
        /// Creates a new 5.25" disk image.  The disk will be fully formatted.
        /// </summary>
        /// <remarks>
        /// <para>This interface supports a limited number of configurations.  We want to support
        /// creation of blank images for testing, and conversion of other images to WOZ format,
        /// not provide a fully general WOZ creation mechanism.</para>
        /// </remarks>
        /// <param name="stream">Stream into which the new disk image will be written.</param>
        /// <param name="numTracks">Number of whole tracks; must be 35 or 40.</param>
        /// <param name="codec">Sector codec.</param>
        /// <param name="volume">Low-level volume number.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <returns>Newly-created disk image.</returns>
        public static Woz CreateDisk525(Stream stream, uint numTracks, SectorCodec codec,
                byte volume, AppHook appHook) {
            if (!CanCreateDisk525(numTracks, out string errMsg)) {
                throw new ArgumentException(errMsg);
            }

            Woz newDisk = new Woz(stream, appHook);
            try {
                newDisk.DoCreateDisk525(stream, numTracks, codec, volume, appHook);
            } catch {
                newDisk.Dispose();
                throw;
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
        /// Creates a new 400KB or 800KB 3.5" disk image.  The disk will be fully formatted.
        /// </summary>
        /// <param name="stream">Stream into which the new disk image will be written.</param>
        /// <param name="mediaKind">Type of disk to create (SSDD or DSDD).</param>
        /// <param name="interleave">Disk interleave (2 or 4).</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <returns>Newly-created disk image.</returns>
        public static Woz CreateDisk35(Stream stream, MediaKind mediaKind, int interleave,
                SectorCodec codec, AppHook appHook) {
            if (!CanCreateDisk35(mediaKind, interleave, out string errMsg)) {
                throw new ArgumentException(errMsg);
            }

            Woz newDisk = new Woz(stream, appHook);
            try {
                newDisk.DoCreateDisk35(stream, mediaKind, interleave, codec, appHook, false);
            } catch {
                newDisk.Dispose();
                throw;
            }
            return newDisk;
        }

        public override string ToString() {
            return "[WOZ" + FileRevision + " " + DiskKind + "]";
        }
    }
}
