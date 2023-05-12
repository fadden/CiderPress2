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

using DiskArc;

namespace DiskArcTests {
    public class CustomSectorCodec : SectorCodec {
        private static readonly byte[] sD5AA97 = { 0xd5, 0xaa, 0x97 };
        private static readonly byte[] sD5AAAE = { 0xd5, 0xaa, 0xae };

        public CustomSectorCodec() {
            this.Name = "Custom Test Codec";
            this.DataEncoding = NibbleEncoding.GCR62;
            this.DecodedSectorSize = 256;
            this.EncodedSectorSize = 342 + 1;
            this.mAddressProlog = sD5AA97;
            this.mAddressEpilog = sDEAAEB;
            this.mDataProlog = sD5AAAE;
            this.mDataEpilog = sDEAAEB;
            this.AddrEpilogReadCount = 2;
            this.AddrChecksumSeed = 0xff;           // 4&4 encoded, value can be anything
            this.DoTestAddrTrack = true;
            this.DoTestAddrChecksum = true;
            this.DataEpilogReadCount = 2;
            this.DataChecksumSeed = 0x3f;           // 6&2 encoded, must be [0x00,0x3f]
            this.DoTestDataChecksum = true;
        }
    }
}
