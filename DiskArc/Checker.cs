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
using CommonUtil;

namespace DiskArc {
    /// <summary>
    /// Checksum verification helper.
    /// </summary>
    internal abstract class Checker {
        protected uint mSeed;
        protected uint mCRC;
        protected uint mExpectedValue;

        /// <summary>
        /// Current value of CRC.
        /// </summary>
        public uint Value { get { return mCRC; } }

        /// <summary>
        /// True if the current value of the CRC matches the expected value (if applicable).
        /// </summary>
        public bool IsMatch { get { return mCRC == mExpectedValue; } }

        /// <summary>
        /// Updates the CRC with the bytes in the buffer.
        /// </summary>
        /// <param name="buf">Data buffer.</param>
        /// <param name="offset">Start offset.</param>
        /// <param name="length">Data length.</param>
        public abstract void Update(byte[] buf, int offset, int length);

        /// <summary>
        /// Resets the CRC to the initial seed value.
        /// </summary>
        public void Reset() {
            mCRC = mSeed;
        }

        public override string ToString() {
            return "[" + GetType().Name + ": value=0x" + mCRC.ToString("x8") +
                " expect=0x" + mExpectedValue.ToString("x8") +
                " seed=0x" + mSeed.ToString("x8") + "]";
        }
    }

    /// <summary>
    /// Checks a CRC using the CRC-16/XMODEM algorithm, if passed a seed of zero.  Pass in a
    /// seed of 0xffff to get CRC-16/CCITT-FALSE instead.
    /// </summary>
    internal class CheckXMODEM16 : Checker {
        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="seed">Initial value for CRC (zero or 0xffff).</param>
        /// <param name="expectedValue">Expected result, if this being used for verification.
        ///   Otherwise pass zero.</param>
        public CheckXMODEM16(ushort seed, ushort expectedValue) {
            mCRC = mSeed = seed;
            mExpectedValue = expectedValue;
        }

        public override void Update(byte[] buf, int offset, int length) {
            mCRC = CRC16.XMODEM_OnBuffer((ushort)mCRC, buf, offset, length);
        }
    }

    /// <summary>
    /// Checks a CRC using the CRC-32 algorithm.
    /// </summary>
    internal class CheckCRC32 : Checker {
        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="seed">Initial value for CRC (usually zero).</param>
        /// <param name="expectedValue">Expected result, if this being used for verification.
        ///   Otherwise pass zero.</param>
        public CheckCRC32(uint seed, uint expectedValue) {
            mCRC = mSeed = seed;
            mExpectedValue = expectedValue;
        }

        public override void Update(byte[] buf, int offset, int length) {
            mCRC = CRC32.OnBuffer(mCRC, buf, offset, length);
        }
    }
}
