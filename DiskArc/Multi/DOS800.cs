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
using System.Collections;

using CommonUtil;

namespace DiskArc.Multi {
    /// <summary>
    /// Base class for DOS-on-800K-disk classes.
    /// </summary>
    public abstract class DOS800 : IMultiPart {
        //
        // IMultiPart interfaces.
        //

        public bool IsDubious { get; private set; }

        public Notes Notes { get; } = new Notes();

        public GatedChunkAccess RawAccess { get; }

        public int Count { get { return mPartitions.Count; } }
        public Partition this[int key] { get { return mPartitions[key]; } }

        public IEnumerator<Partition> GetEnumerator() {
            return mPartitions.GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() {
            return mPartitions.GetEnumerator();
        }

        //
        // Innards.
        //

        protected List<Partition> mPartitions = new List<Partition>(2);
        protected AppHook mAppHook;

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="chunkAccess">Chunk access object for the disk image.</param>
        /// <param name="appHook">Application hook reference.</param>
        protected DOS800(IChunkAccess chunkAccess, AppHook appHook) {
            mAppHook = appHook;
            RawAccess = new GatedChunkAccess(chunkAccess);
            RawAccess.AccessLevel = GatedChunkAccess.AccessLvl.ReadOnly;
        }

        public void Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        protected virtual void Dispose(bool disposing) {
            RawAccess.AccessLevel = GatedChunkAccess.AccessLvl.Closed;
            if (disposing) {
                foreach (Partition part in mPartitions) {
                    part.Dispose();
                }
            }
        }
    }
}
