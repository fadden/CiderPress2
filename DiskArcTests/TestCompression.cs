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
using System.IO;
using System.IO.Compression;

using CommonUtil;
using DiskArc;
using DiskArc.Comp;

namespace DiskArcTests {
    /// <summary>
    /// Test compression infrastructure and methods.
    /// </summary>
    public class TestCompression : ITest {
        /// <summary>
        /// Three buffers: one for uncompressed input, one for compressed output, and one for
        /// expanded output.
        /// </summary>
        private class WorkBuffers {
            private const int DEFAULT_SIZE = 128*1024;

            public byte[] SrcBuf { get; }
            public byte[] CompBuf { get; }
            public byte[] ExpBuf { get; }
            public MemoryStream Src { get; }
            public MemoryStream Comp { get; }
            public MemoryStream Exp { get; }

            public WorkBuffers() {
                SrcBuf = new byte[DEFAULT_SIZE];
                Src = new MemoryStream(SrcBuf);
                CompBuf = new byte[DEFAULT_SIZE];
                Comp = new MemoryStream(CompBuf);
                ExpBuf = new byte[DEFAULT_SIZE];
                Exp = new MemoryStream(ExpBuf);
            }

            /// <summary>
            /// Truncates and rewinds all memory streams.
            /// </summary>
            public void Reset() {
                Src.Position = Comp.Position = Exp.Position = 0;
                Src.SetLength(0);
                Comp.SetLength(0);
                Exp.SetLength(0);
            }

            /// <summary>
            /// Resets the buffers, then initializes the Src buffer to the provided data.
            /// </summary>
            public void ResetSrc(byte[] buf, int offset, int count) {
                Reset();
                Src.SetLength(count);
                Array.Copy(buf, offset, SrcBuf, 0, count);
            }
        }

        public static void TestSqueeze(AppHook appHook) {
            WorkBuffers bufs = new WorkBuffers();
            CodecStreamCreator creator =
                delegate (Stream compStream, CompressionMode mode, long compressedLength,
                        long expandedLength) {
                    return new SqueezeStream(compStream, mode, true, true, "NAME");
                };
            TestBasics(bufs, creator);

            // Confirm that the checksum stored in the full header is being checked.
            bufs.ResetSrc(Patterns.sBytePattern, 0, Patterns.sBytePattern.Length);

            Stream compressor = creator(bufs.Comp, CompressionMode.Compress, -1, -1);
            bufs.Src.CopyTo(compressor);
            compressor.Close();

            // Rewind compressed data and damage it.  The trick is damaging it in a way that
            // is only detectable as a checksum failure (vs. a buffer overrun).
            bufs.Comp.Position = bufs.Exp.Position = 0;
            bufs.CompBuf[515]++;
            // Reset output buffer.
            bufs.Exp.SetLength(0);
            Stream expander = creator(bufs.Comp, CompressionMode.Decompress, -1, bufs.Src.Length);
            try {
                int actual = expander.Read(bufs.ExpBuf, 0, (int)bufs.Src.Length);
                if (actual != bufs.Src.Length) {
                    throw new Exception("Read the wrong amount of data: actual=" + actual +
                        " expected=" + bufs.Src.Length);
                }
                // We'll get here, because the checksum isn't triggered until we read EOF.
                int overVal = expander.ReadByte();
                throw new Exception("Damage went undetected");
            } catch (InvalidDataException) { /*expected*/ }


            //
            // Do a few more tests, without the file header.
            //

            creator = delegate (Stream compStream, CompressionMode mode, long compressedLength,
                        long expandedLength) {
                    return new SqueezeStream(compStream, mode, true, false, string.Empty);
                };

            // Repeat basic edge cases.
            TestBasics(bufs, creator);

            // Generate bytes with frequencies that match the Fibonacci sequence.  This creates
            // a degenerate binary tree, which should cause the encoding pass to fail and
            // re-scale.  (The pre-1.5 SQ code would generate bad output for this case.)
            bufs.Reset();
            int count = 1;
            int prevCount = 1;
            byte[] srcBuf = bufs.SrcBuf;
            for (int i = 0; i < 16; i++) {
                for (int j = 0; j < count; j++) {
                    bufs.Src.WriteByte((byte)(i * 2));
                    bufs.Src.WriteByte(0x80);       // break up runs so RLE doesn't defeat us
                }
                int tmpCount = count;
                count = count + prevCount;
                prevCount = tmpCount;
            }
            bufs.Src.Position = 0;                  // rewind
            TestCopyStream(bufs, creator);
        }

        public static void TestNuLZW1(AppHook appHook) {
            WorkBuffers bufs = new WorkBuffers();
            CodecStreamCreator creator =
                delegate (Stream compStream, CompressionMode mode, long compressedLength,
                        long expandedLength) {
                    return new NuLZWStream(compStream, mode, true, false, expandedLength);
                };
            TestBasics(bufs, creator);

            // We want a CRC mismatch, not a RLE overrun, so fill the buffer with an alternating
            // byte pattern.
            byte[] patBuf = new byte[4096];
            byte val = 0;
            for (int i = 0; i < patBuf.Length; i++) {
                patBuf[i] = val;
                val = (byte)(1 - val);
            }
            bufs.ResetSrc(patBuf, 0, patBuf.Length);

            // Compress data.
            Stream compressor = creator(bufs.Comp, CompressionMode.Compress, -1, -1);
            bufs.Src.CopyTo(compressor);
            compressor.Close();

            // Damage the first data byte.  4-byte file header, 3-byte chunk header.  First byte
            // of output will be the first byte of input as a 9-bit code, so we can safely edit
            // the byte value.
            bufs.CompBuf[7]++;

            bufs.Comp.Position = 0;     // rewind compressed data
            Stream expander = creator(bufs.Comp, CompressionMode.Decompress, bufs.Comp.Length,
                bufs.Src.Length);
            try {
                expander.CopyTo(bufs.Exp);
                throw new Exception("Damage went undetected");
            } catch (InvalidDataException) { /*expected*/ }

            // Expand again, but this time read the exact amount.  The goal is to ensure that
            // the CRC check is triggered even if we don't read EOF.
            bufs.Comp.Position = 0;
            bufs.Exp.Position = 0;
            expander = creator(bufs.Comp, CompressionMode.Decompress, bufs.Comp.Length,
                bufs.Src.Length);
            try {
                int actual = expander.Read(bufs.ExpBuf, 0, patBuf.Length);
                if (actual != patBuf.Length) {
                    throw new Exception("Read the wrong amount of data: " + actual);
                }
                throw new Exception("Damage went undetected (2)");
            } catch (InvalidDataException) { /*expected*/ }
        }

        public static void TestNuLZW2(AppHook appHook) {
            WorkBuffers bufs = new WorkBuffers();
            CodecStreamCreator creator =
                delegate (Stream compStream, CompressionMode mode, long compressedLength,
                        long expandedLength) {
                    return new NuLZWStream(compStream, mode, true, true, expandedLength);
                };
            TestBasics(bufs, creator);

            //
            // Additional LZW/2 tests.
            //

            bufs.Reset();
            bufs.Src.SetLength(bufs.SrcBuf.Length);     // set length to max

            // Throw a bunch of text into the buffer to fill up the table.
            int len = Patterns.StringToBytes(Patterns.sUlysses, bufs.SrcBuf, 0);
            len += Patterns.StringToBytes(Patterns.sGettysburg, bufs.SrcBuf, len);
            len += Patterns.StringToBytes(Patterns.sUlysses, bufs.SrcBuf, len);
            len += Patterns.StringToBytes(Patterns.sGettysburg, bufs.SrcBuf, len);
            // Output a simple repeating pattern that RLE can't compress.
            byte val = 0;
            for (int i = len; i < bufs.SrcBuf.Length; i++) {
                bufs.SrcBuf[i] = val;
                val = (byte)(1 - val);
            }
            // If everything lines up, this will trigger the block-end table clear behavior.
            TestCopyStream(bufs, creator);
        }

        public static void TestLZC(AppHook appHook) {
            WorkBuffers bufs = new WorkBuffers();
            CodecStreamCreator creator =
                delegate (Stream compStream, CompressionMode mode, long compressedLength,
                    long expandedLength) {
                        return new LZCStream(compStream, mode, true, compressedLength, 16, true);
                    };
            TestBasics(bufs, creator);

            creator = delegate (Stream compStream, CompressionMode mode, long compressedLength,
                    long expandedLength) {
                        return new LZCStream(compStream, mode, true, compressedLength, 12, true);
                    };
            TestBasics(bufs, creator);

            creator = delegate (Stream compStream, CompressionMode mode, long compressedLength,
                    long expandedLength) {
                        return new LZCStream(compStream, mode, true, compressedLength, 9, true);
                    };
            TestBasics(bufs, creator);

            creator = delegate (Stream compStream, CompressionMode mode, long compressedLength,
                    long expandedLength) {
                        return new LZCStream(compStream, mode, true, compressedLength, 16, false);
                    };
            TestBasics(bufs, creator);

#if false   // this takes about 8 seconds to run
            //
            // Test compression of 2GB of zeroes, to verify that the LZC buffers are large
            // enough to handle the largest possible code.
            //
            // A single code can represent about 2^16-2^8=65280 bytes.  If the input stream is
            // all zeroes, each code will be one byte longer than the previous code, so to max
            // out the codes, we need (65280*65281)/2 bytes of input.  With block-mode disabled
            // we get one extra code, so we actually need (65281*65282)/2.
            //
            long zeroLen = (65281L * 65282) / 2 + 256;
            ZeroStream zs = new ZeroStream(zeroLen);
            MemoryStream compStream = new MemoryStream();
            LZCStream lzcs =
                new LZCStream(compStream, CompressionMode.Compress, true, -1, 16, true);
            zs.CopyTo(lzcs);
#endif
        }


        delegate Stream CodecStreamCreator(Stream compStream, CompressionMode mode,
            long compressedLength, long expandedLength);

        private static void TestBasics(WorkBuffers bufs, CodecStreamCreator creator) {
            // Edge cases: no data or tiny amount of compressible data.
            TestSimpleRun(bufs, creator, 0);
            TestSimpleRun(bufs, creator, 1);
            TestSimpleRun(bufs, creator, 2);
            TestSimpleRun(bufs, creator, 3);
            TestSimpleRun(bufs, creator, 4);
            TestSimpleRun(bufs, creator, 10);

            // Simple but big.
            TestSimpleRun(bufs, creator, bufs.SrcBuf.Length);

            // Test patterns that exercise RLE encoders.
            TestRLE(bufs, creator);

            //
            // Try some patterns.
            //

            const int MID_SIZE = 32771;     // prime number, ~32K
            byte[] patternBuffer = new byte[MID_SIZE];
            Patterns.GenerateUncompressible(patternBuffer, 0, MID_SIZE);
            bufs.ResetSrc(patternBuffer, 0, MID_SIZE);
            TestCopyStream(bufs, creator);
            bufs.ResetSrc(patternBuffer, 0, MID_SIZE);
            TestSingleStream(bufs, creator);

            Patterns.GenerateTestPattern1(patternBuffer, 0, MID_SIZE);
            bufs.ResetSrc(patternBuffer, 0, MID_SIZE);
            TestCopyStream(bufs, creator);
            bufs.ResetSrc(patternBuffer, 0, MID_SIZE);
            TestSingleStream(bufs, creator);

            Patterns.GenerateTestPattern2(patternBuffer, 0, MID_SIZE);
            bufs.ResetSrc(patternBuffer, 0, MID_SIZE);
            TestCopyStream(bufs, creator);
            bufs.ResetSrc(patternBuffer, 0, MID_SIZE);
            TestSingleStream(bufs, creator);

            int len = Patterns.StringToBytes(Patterns.sUlysses, patternBuffer, 0);
            bufs.ResetSrc(patternBuffer, 0, MID_SIZE);
            TestCopyStream(bufs, creator);
            bufs.ResetSrc(patternBuffer, 0, MID_SIZE);
            TestSingleStream(bufs, creator);
        }

        private static void TestSimpleRun(WorkBuffers bufs, CodecStreamCreator creator, int count) {
            bufs.Reset();
            bufs.Src.SetLength(count);
            RawData.MemSet(bufs.SrcBuf, 0, count, 0xcc);
            TestCopyStream(bufs, creator);

            bufs.Reset();
            bufs.Src.SetLength(count);
            RawData.MemSet(bufs.SrcBuf, 0, count, 0xcc);
            TestSingleStream(bufs, creator);
        }

        // Include run and Squeeze delimiters, end with delim.
        private static readonly byte[] sRLE1 = new byte[] {
            0x90, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 0x90, 0x90, 0x90, 1, 1, 1, 0x90
        };
        // Runs of varying lengths, ending with non-run.
        private static readonly byte[] sRLE2 = new byte[] {
            0, 1, 2, 2, 3, 4, 4, 4, 5, 6, 6, 6, 6, 8, 8, 8, 8, 8, 9
        };
        // Runs of varying lengths, ending with run.
        private static readonly byte[] sRLE3 = new byte[] {
            0, 1, 2, 2, 3, 4, 4, 4, 5, 6, 6, 6, 6, 7, 8, 8, 8, 8, 8
        };

        private static void TestRLE(WorkBuffers bufs, CodecStreamCreator creator) {
            // Do a few simple patterns.

            bufs.ResetSrc(sRLE1, 0, sRLE1.Length);
            TestSingleStream(bufs, creator);
            bufs.ResetSrc(sRLE2, 0, sRLE2.Length);
            TestSingleStream(bufs, creator);
            bufs.ResetSrc(sRLE3, 0, sRLE3.Length);
            TestSingleStream(bufs, creator);

            // Buffer with two runs, each longer than 256 bytes.
            bufs.Reset();
            RawData.MemSet(bufs.SrcBuf, 0, 272, 0x11);
            bufs.SrcBuf[272] = 0x22;
            RawData.MemSet(bufs.SrcBuf, 273, 272, 0x33);
            bufs.Src.SetLength(272 + 1 + 272);
            TestSingleStream(bufs, creator);
        }

        private static void TestCopyStream(WorkBuffers bufs, CodecStreamCreator creator) {
            Stream compressor = creator(bufs.Comp, CompressionMode.Compress, -1, -1);
            bufs.Src.CopyTo(compressor);
            compressor.Close();

            bufs.Comp.Position = 0;     // rewind compressed data
            Stream expander = creator(bufs.Comp, CompressionMode.Decompress, bufs.Comp.Length,
                bufs.Src.Length);
            expander.CopyTo(bufs.Exp);

            if (bufs.Exp.Length != bufs.Src.Length ||
                    !RawData.CompareBytes(bufs.SrcBuf, 0, bufs.ExpBuf, 0, (int)bufs.Src.Length)) {
                throw new Exception("Source and expanded data don't match");
            }
        }

        private static void TestSingleStream(WorkBuffers bufs, CodecStreamCreator creator) {
            Stream compressor = creator(bufs.Comp, CompressionMode.Compress, -1, -1);
            for (int i = 0; i < bufs.Src.Length; i++) {
                compressor.WriteByte((byte)bufs.Src.ReadByte());
            }
            compressor.Close();

            bufs.Comp.Position = 0;     // rewind compressed data
            Stream expander = creator(bufs.Comp, CompressionMode.Decompress, bufs.Comp.Length,
                bufs.Src.Length);
            while (true) {
                int val = expander.ReadByte();
                if (val < 0) {
                    break;
                }
                bufs.Exp.WriteByte((byte)val);
            }

            if (bufs.Exp.Length != bufs.Src.Length ||
                    !RawData.CompareBytes(bufs.SrcBuf, 0, bufs.ExpBuf, 0, (int)bufs.Src.Length)) {
                throw new Exception("Source and expanded data don't match");
            }
        }
    }
}
