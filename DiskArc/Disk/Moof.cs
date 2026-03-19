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
using System.Diagnostics.CodeAnalysis;

using CommonUtil;
using DiskArc.Multi;
using static DiskArc.Defs;
using static DiskArc.IDiskImage;
using static DiskArc.IMetadata;

namespace DiskArc.Disk {
    /// <summary>
    /// MOOF disk image format.
    /// </summary>
    /// <remarks>
    /// <para>The MOOF v1.0 image format is a minor variation on WOZ v2.1, so this implementation
    /// is just bolted onto the side of Woz.</para>
    /// <para>We could make this a subclass of Woz, but then "is Woz" would evaluate to
    /// true.  In practice, WOZ and MOOF are two parallel disk formats that share most of
    /// their implementation, so it would be more correct to have Woz and Moof be subclasses of
    /// a common parent.  (TODO?)</para>
    /// </remarks>
    public class Moof : IDiskImage, INibbleDataAccess, IMetadata {
        internal static readonly byte[] SIGNATURE = new byte[8] {
            0x4d, 0x4f, 0x4f, 0x46, 0xff, 0x0a, 0x0d, 0x0a      // MOOF
        };

        private static readonly NibCharacteristics sCharacteristics = new NibCharacteristics(
            name: "MOOF",
            isByteAligned: false,
            isFixedLength: false,
            hasPartial525Tracks: false);

        private Woz mWoz;

        //
        // IDiskImage properties.
        //

        public bool IsReadOnly => mWoz.IsReadOnly;

        public bool IsDirty => mWoz.IsDirty;

        public bool IsModified {
            get => mWoz.IsModified;
            set { mWoz.IsModified = value; }
        }

        public bool IsDubious => mWoz.IsDubious;

        public Notes Notes => mWoz.Notes;

        public GatedChunkAccess? ChunkAccess {
            get => mWoz.ChunkAccess;
            set { mWoz.ChunkAccess = value; }
        }

        public IDiskContents? Contents {
            get => mWoz.Contents;
            set { mWoz.Contents = value; }
        }

        //
        // INibbleDataAccess properties.
        //

        public NibCharacteristics Characteristics => sCharacteristics;

        public MediaKind DiskKind => mWoz.DiskKind;

        //
        // MOOF/WOZ-specific properties.
        //

        public bool HasMeta => mWoz.HasMeta;


        // IDiskImage-required delegate
        public static bool TestKind(Stream stream, AppHook appHook) {
            stream.Position = 0;
            if (stream.Length < Woz.MIN_FILE_LEN || stream.Length > Woz.MAX_FILE_LEN) {
                // Too big, or too small to hold signature/INFO/TMAP, much less TRKS.
                return false;
            }
            byte[] sigBuf = new byte[SIGNATURE.Length];
            stream.ReadExactly(sigBuf, 0, SIGNATURE.Length);
            if (RawData.CompareBytes(sigBuf, SIGNATURE, SIGNATURE.Length)) {
                return true;
            }
            return false;
        }

        /// <summary>
        /// Private constructor.
        /// </summary>
        private Moof(Woz woz) {
            mWoz = woz;
        }

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
            Moof newDisk = new Moof(new Woz(stream, appHook));
            newDisk.mWoz.Parse();

            // Propagate read-only flag.  (See notes in Woz.OpenDisk().)
            bool metaReadOnly = newDisk.IsReadOnly;
            newDisk.mWoz.MoofInfo.IsReadOnly = newDisk.IsReadOnly;
            if (newDisk.mWoz.MetaChunk != null) {
                newDisk.mWoz.MetaChunk.IsReadOnly = newDisk.IsReadOnly;
            }
            return newDisk;
        }

        // IDiskImage
        public bool AnalyzeDisk(SectorCodec? codec, SectorOrder orderHint, AnalysisDepth depth) {
            return mWoz.AnalyzeDisk(codec, orderHint, depth);
        }

        // IDiskImage
        public virtual void CloseContents() {
            mWoz.CloseContents();
        }

        // IDiskImage
        public void Flush() {
            mWoz.Flush();
        }

        public void Dispose() {
            mWoz.Dispose();
        }

        public static bool CanCreateDisk35(MediaKind mediaKind, int interleave, out string errMsg) {
            return Woz.CanCreateDisk35(mediaKind, interleave, out errMsg);
        }
        public static Moof CreateDisk35(Stream stream, MediaKind mediaKind, int interleave,
                SectorCodec codec, AppHook appHook) {
            return new Moof(Woz.CreateDisk35(stream, mediaKind, interleave, codec, appHook, true));
        }

        // INibbleDataAccess
        public bool GetTrackBits(uint trackNum, uint trackFraction,
                [NotNullWhen(true)] out CircularBitBuffer? cbb) {
            return mWoz.GetTrackBits(trackNum, trackFraction, out cbb);
        }

        #region Metadata

        /// <summary>
        /// Adds a META chunk to a file that doesn't have one.  No effect if the chunk exists.
        /// </summary>
        public void AddMETA() {
            mWoz.AddMETA();
        }

        // IMetadata
        public bool CanAddNewEntries => true;

        // IMetadata
        public List<MetaEntry> GetMetaEntries() => mWoz.GetMetaEntries();

        // IMetadata
        public string? GetMetaValue(string key, bool verbose) => mWoz.GetMetaValue(key, verbose);

        // IMetadata
        public bool TestMetaValue(string key, string value) => mWoz.TestMetaValue(key, value);

        // IMetadata
        public void SetMetaValue(string key, string value) => mWoz.SetMetaValue(key, value);

        // IMetadata
        public bool DeleteMetaEntry(string key) => mWoz.DeleteMetaEntry(key);

        /// <summary>
        /// This allows the application to overwrite the creator string, so that it can replace
        /// the library identifier with its own.  Normally info:creator is not editable.
        /// </summary>
        public void SetCreator(string value) => mWoz.SetCreator(value);

        #endregion Metadata

        public override string ToString() {
            return "[MOOF " + DiskKind + "]";
        }
    }
}
