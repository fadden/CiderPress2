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
using System.Text;

using CommonUtil;

namespace DiskArc.Disk {
    /// <summary>
    /// Base class for objects that hold WOZ/MOOF INFO chunk data.
    /// </summary>
    /// <remarks>
    /// <para>INFO values in a read-only disk image can be edited, but the changes will be
    /// discarded.</para>
    /// </remarks>
    public class Wozoof_Info {
        /// <summary>
        /// True if modifications should be blocked.
        /// </summary>
        public bool IsReadOnly { get; internal set; }

        /// <summary>
        /// True if one or more fields have been modified.
        /// </summary>
        public bool IsDirty { get; internal set; }

        /// <summary>
        /// Raw INFO chunk data.  All values are read from and written to this.
        /// </summary>
        protected byte[] mData;

        /// <summary>
        /// Constructs an empty object.
        /// </summary>
        public Wozoof_Info() {
            mData = new byte[Wozoof.INFO_LENGTH];
        }

        /// <summary>
        /// Constructs the object from an existing INFO chunk.
        /// </summary>
        /// <remarks>
        /// <para>This does not attempt to validate the data read.</para>
        /// </remarks>
        /// <param name="buffer">Buffer of INFO data.</param>
        /// <param name="offset">Offset of start of INFO data (past the chunk header).</param>
        public Wozoof_Info(byte[] buffer, int offset) {
            mData = new byte[Wozoof.INFO_LENGTH];
            Array.Copy(buffer, offset, mData, 0, Wozoof.INFO_LENGTH);
        }

        /// <summary>
        /// Obtains the raw chunk data buffer, for output to the disk image file.
        /// </summary>
        internal byte[] GetData() {
            // This is internal-only, so no need to make a copy.
            return mData;
        }

        protected void CheckAccess() {
            if (IsReadOnly) {
                throw new InvalidOperationException("Image is read-only");
            }
        }

        public virtual string? GetValue(string key, bool verbose) {
            throw new NotImplementedException();
        }

        public virtual bool TestValue(string key, string value) {
            throw new NotImplementedException();
        }

        public virtual void SetValue(string key, string value) {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Converts the string to UTF-8 bytes, and pads the end with spaces.  The encoded
        /// form is limited to 32 bytes.  Excess characters will be dropped.
        /// </summary>
        /// <param name="name">String to encode.</param>
        /// <returns>32-element byte array, end padded with ASCII spaces.</returns>
        protected static byte[] EncodeCreator(string name) {
            byte[] enc = Encoding.UTF8.GetBytes(name);
            int i;
            byte[] result = new byte[Wozoof.CREATOR_ID_LEN];
            // TODO: this will produce bad data if a multi-byte encoded value starts at +31
            for (i = 0; i < Math.Min(Wozoof.CREATOR_ID_LEN, enc.Length); i++) {
                result[i] = enc[i];
            }
            for (; i < Wozoof.CREATOR_ID_LEN; i++) {
                result[i] = (byte)' ';
            }
            return result;
        }
    }
}
