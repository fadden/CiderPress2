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

using static DiskArc.Defs;

namespace DiskArc {
    /// <summary>
    /// Wrapper class for an IChunkAccess instance.  When the gate is open, all calls are simply
    /// passed through.  When the gate is closed, property "get" operations pass through, but
    /// "set" operations and method calls cause an exception.  When read-only, read calls are
    /// allowed but "set" operations and write calls are not.
    /// </summary>
    /// <remarks>
    /// <para>Nested arrangements are possible.</para>
    /// <para>There is currently no way to extract the "base" chunk access, by design.  If we
    /// don't have access to the base object, it's probably incorrect to extract it.  If we
    /// decide that nested arrangements are undesirable, consider altering the constructor here
    /// to do the base extraction instead of publishing the base itself.</para>
    /// </remarks>
    public class GatedChunkAccess : IChunkAccess {
        private IChunkAccess mBase;

        /// <summary>
        /// Current access level.
        /// </summary>
        public AccessLvl AccessLevel { get; internal set; } = AccessLvl.Open;
        public enum AccessLvl { Unknown = 0, Open, ReadOnly, Closed }

        /// <summary>
        /// Constructor.  Pass in the chunk access object to wrap.
        /// </summary>
        /// <param name="baseAcc">Chunk access object.</param>
        public GatedChunkAccess(IChunkAccess baseAcc) {
            mBase = baseAcc;
        }

        private void CheckAccess(bool isChange) {
            Debug.Assert(AccessLevel != AccessLvl.Unknown);
            if (AccessLevel == AccessLvl.Closed ||
                    (AccessLevel == AccessLvl.ReadOnly && isChange)) {
                throw new DAException("Access to chunk data is currently disallowed");
            }
        }

        public bool IsReadOnly => mBase.IsReadOnly;
        public bool IsModified {
            get => mBase.IsModified;
            set { mBase.IsModified = value; }       // always mutable, even when gate is closed
        }
        public long ReadCount => mBase.ReadCount;
        public long WriteCount => mBase.WriteCount;

        public long FormattedLength => mBase.FormattedLength;

        public uint NumTracks => mBase.NumTracks;
        public uint NumSectorsPerTrack => mBase.NumSectorsPerTrack;

        public bool HasSectors => mBase.HasSectors;
        public bool HasBlocks => mBase.HasBlocks;

        public SectorOrder FileOrder {
            get => mBase.FileOrder;
            set { CheckAccess(true); mBase.FileOrder = value; }
        }

        public SectorCodec? NibbleCodec {
            get => mBase.NibbleCodec;
        }

        public void ReadSector(uint track, uint sect, byte[] data, int offset) {
            CheckAccess(false);
            mBase.ReadSector(track, sect, data, offset);
        }

        public void ReadBlock(uint block, byte[] data, int offset) {
            CheckAccess(false);
            mBase.ReadBlock(block, data, offset);
        }

        public void WriteSector(uint track, uint sect, byte[] data, int offset) {
            CheckAccess(true);
            mBase.WriteSector(track, sect, data, offset);
        }

        public void WriteBlock(uint block, byte[] data, int offset) {
            CheckAccess(true);
            mBase.WriteBlock(block, data, offset);
        }

        public bool TestSector(uint trk, uint sct, out bool writable) {
            CheckAccess(false);
            return mBase.TestSector(trk, sct, out writable);
        }

        public bool TestBlock(uint block, out bool writable) {
            CheckAccess(false);
            return mBase.TestBlock(block, out writable);
        }

        public void Initialize() {
            CheckAccess(true);
            mBase.Initialize();
        }
    }
}
