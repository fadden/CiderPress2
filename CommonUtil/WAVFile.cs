/*
 * Copyright 2024 faddenSoft
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

namespace CommonUtil {
    /// <summary>
    /// This processes a RIFF audio file (.wav).
    /// </summary>
    /// <remarks>
    /// <para>The current implementation is for PCM WAVE data, which has a fixed size per sample,
    /// but the API is intended to support compressed formats as well.</para>
    /// <para>Thanks: https://stackoverflow.com/q/8754111/294248 ,
    /// http://soundfile.sapp.org/doc/WaveFormat/ , and
    /// https://www.mmsp.ece.mcgill.ca/Documents/AudioFormats/WAVE/Docs/riffmci.pdf .</para>
    /// </remarks>
    public class WAVFile {
        // Chunk signatures.
        public static readonly int SIG_RIFF = RawData.IntifyASCII("RIFF");
        public static readonly int SIG_WAVE = RawData.IntifyASCII("WAVE");
        public static readonly int SIG_FMT  = RawData.IntifyASCII("fmt ");
        public static readonly int SIG_DATA = RawData.IntifyASCII("data");

        // Selected constants from RFC 2361.
        public const int WAVE_FORMAT_UNKNOWN = 0x0000;
        public const int WAVE_FORMAT_PCM = 0x0001;
        public const int WAVE_FORMAT_ADPCM = 0x0002;
        public const int WAVE_FORMAT_IEEE_FLOAT = 0x0003;

        private const int MIN_LEN = 42;
        private const int RIFF_HEADER_LEN = 12;
        private const int CHUNK1_MIN_LEN = 16;
        private const int CHUNK1_MAX_LEN = 128;     // arbitrary

        /// <summary>
        /// Format tag.  Defines the format of the audio samples.  PCM is format 1.
        /// </summary>
        public int FormatTag { get; private set; }

        /// <summary>
        /// Number of channels.  1 for mono, 2 for stereo, etc.
        /// </summary>
        public int Channels { get; private set; }

        /// <summary>
        /// Number of samples per second.  Typically 8000, 22050, or 44100.
        /// </summary>
        public int SamplesPerSec { get; private set; }

        /// <summary>
        /// Average number of bytes per second.  For PCM this is exact, for compressed formats
        /// it isn't.
        /// </summary>
        public int AvgBytesPerSec { get; private set; }

        /// <summary>
        /// Block alignment, in bytes, of the waveform data.  This can be used by playback
        /// software when allocating buffers.
        /// </summary>
        public int BlockAlign { get; private set; }

        /// <summary>
        /// For PCM: number of bits per sample, usually 8 or 16.
        /// </summary>
        public int BitsPerSample { get; private set; }

        /// <summary>
        /// Offset of start of sample data.
        /// </summary>
        public long DataOffset { get; private set; }

        /// <summary>
        /// Length of sample data, in bytes.
        /// </summary>
        public int DataLength { get; private set; }

        /// <summary>
        /// Data stream reference.
        /// </summary>
        private Stream mStream;


        /// <summary>
        /// Private constructor.
        /// </summary>
        private WAVFile(Stream stream) {
            mStream = stream;
        }

        /// <summary>
        /// Parses the WAV file header.  Does not process the audio samples.
        /// </summary>
        /// <remarks>
        /// <para>The WAVFile object holds a reference to the Stream, but does not take
        /// ownership.</para>
        /// </remarks>
        /// <param name="stream">WAV file data stream, positioned at start.</param>
        /// <returns>A new object with properties set, or null on failure.</returns>
        public static WAVFile? Prepare(Stream stream) {
            WAVFile wav = new WAVFile(stream);
            if (!wav.ParseHeader()) {
                return null;
            }
            if (wav.FormatTag != WAVE_FORMAT_PCM ||
                    (wav.BitsPerSample != 8 && wav.BitsPerSample != 16 &&
                        wav.BitsPerSample != 32)) {
                // We don't know how to process this type of file.
                return null;
            }
            return wav;
        }

        /// <summary>
        /// Parses the headers out of the RIFF file.  On return, the stream will be positioned
        /// at the start of the audio sample data.
        /// </summary>
        /// <param name="mStream">Data stream, positioned at start of RIFF data.</param>
        /// <returns>True on success.</returns>
        private bool ParseHeader() {
            if (mStream.Length - mStream.Position < MIN_LEN) {
                return false;
            }

            // Read the RIFF header.
            byte[] riffHeader = new byte[RIFF_HEADER_LEN];
            mStream.ReadExactly(riffHeader, 0, RIFF_HEADER_LEN);
            uint chunkId = RawData.GetU32BE(riffHeader, 0);
            if (chunkId != SIG_RIFF) {
                Debug.WriteLine("Not a RIFF (.wav) file, stream now at " + mStream.Position);
                return false;
            }
            uint chunkSize = RawData.GetU32LE(riffHeader, 4);   // size of everything that follows
            if (mStream.Length - mStream.Position + 4 < chunkSize) {
                Debug.WriteLine("WAV file is too short");
                return false;
            }
            uint chunkFormat = RawData.GetU32BE(riffHeader, 8);
            if (chunkId != SIG_RIFF || chunkFormat != SIG_WAVE) {
                Debug.WriteLine("Incorrect WAVE file header, stream now at " + mStream.Position);
                return false;
            }

            // Read the sub-chunk #1 header and data.  We expect the "fmt " chunk to come first.
            // We don't know exactly how large it will be, but it's safe to assume that anything
            // we understand will have a reasonably-sized chunk here.
            bool ok;
            uint subChunk1Id = RawData.ReadU32BE(mStream, out ok);
            uint subChunk1Size = RawData.ReadU32LE(mStream, out ok);
            if (subChunk1Id != SIG_FMT ||
                    subChunk1Size < CHUNK1_MIN_LEN || subChunk1Size > CHUNK1_MAX_LEN) {
                Debug.WriteLine("Bad subchunk1 header");
                return false;
            }
            if (subChunk1Size > mStream.Length - mStream.Position) {
                Debug.WriteLine("Subchunk1 exceeds file length");
                return false;
            }
            byte[] subChunk1Header = new byte[subChunk1Size];
            mStream.ReadExactly(subChunk1Header, 0, (int)subChunk1Size);

            // Process the common fields.
            FormatTag = RawData.GetU16LE(subChunk1Header, 0);
            Channels = RawData.GetU16LE(subChunk1Header, 2);
            SamplesPerSec = (int)RawData.GetU32LE(subChunk1Header, 4);
            AvgBytesPerSec = (int)RawData.GetU32LE(subChunk1Header, 8);
            BlockAlign = RawData.GetU16LE(subChunk1Header, 12);

            if (SamplesPerSec <= 0) {
                Debug.WriteLine("Invalid sample rate " + SamplesPerSec);
                return false;
            }
            if (Channels == 0) {
                Debug.WriteLine("Invalid NumChannels " + Channels);
                return false;
            }

            // Process the format-specific fields.
            if (FormatTag == WAVE_FORMAT_PCM) {
                BitsPerSample = RawData.GetU16LE(subChunk1Header, 14);
                if (BitsPerSample == 0 || BitsPerSample > 256) {
                    Debug.WriteLine("Invalid bits per sample " + BitsPerSample);
                    return false;
                }
                // These equations are in the PCM-specific section of the Microsoft spec.
                if (AvgBytesPerSec != SamplesPerSec * Channels * BitsPerSample / 8) {
                    Debug.WriteLine("Warning: ByteRate has unexpected value " + AvgBytesPerSec);
                }
                if (BlockAlign != Channels * BitsPerSample / 8) {
                    Debug.WriteLine("Warning: BlockAlign has unexpected value " + BlockAlign);
                }
            } else {
                BitsPerSample = -1;
                Debug.WriteLine("Warning: audio format is not PCM: " + FormatTag);
            }

            // Find the "data" chunk.  We may encounter a "fact" chunk first; ignore it.  Keep
            // scanning until we find it or run out of file.
            uint subChunk2Id, subChunk2Size;
            while (true) {
                subChunk2Id = RawData.ReadU32BE(mStream, out ok);
                subChunk2Size = RawData.ReadU32LE(mStream, out ok);
                if (!ok || subChunk2Size == 0) {
                    Debug.WriteLine("Unable to find data chunk");
                    return false;
                }
                if (subChunk2Id == SIG_DATA) {
                    break;
                }
                Debug.WriteLine("Skipping chunk: '" +
                    RawData.StringifyU32BE(subChunk2Id) + "'");
                mStream.Seek(subChunk2Size, SeekOrigin.Current);
            }
            if (mStream.Length - mStream.Position < subChunk2Size) {
                Debug.WriteLine("Bad subchunk2size " + subChunk2Size);
                return false;
            }

            if (FormatTag == WAVE_FORMAT_PCM) {
                int bytesPerSample = ((BitsPerSample + 7) / 8) * Channels;
                if (subChunk2Size % bytesPerSample != 0) {
                    // Ignore partial sample data.
                    Debug.WriteLine("Warning: file ends with a partial sample; len=" +
                        subChunk2Size);
                    subChunk2Size -= (uint)(subChunk2Size % bytesPerSample);
                }
            }

            // All done.  Stream is positioned at the start of the data.
            DataOffset = mStream.Position;
            DataLength = (int)subChunk2Size;
            return true;
        }

        /// <summary>
        /// Seeks the file stream to the start of the sample area.
        /// </summary>
        public void SeekToStart() {
            mStream.Position = DataOffset;
        }

        /// <summary>
        /// Returns a string with a human-readable summary of the file format.
        /// </summary>
        public string GetInfoString() {
            StringBuilder sb = new StringBuilder();
            sb.Append("RIFF WAVE, format=");
            sb.Append(FormatTag);
            if (FormatTag == WAVE_FORMAT_PCM) {
                sb.Append(" (PCM)");
            }
            switch (Channels) {
                case 1:
                    sb.Append(" mono");
                    break;
                case 2:
                    sb.Append(" stereo");
                    break;
                default:
                    sb.Append(' ');
                    sb.Append(Channels);
                    sb.Append("-channel");
                    break;
            }
            sb.Append(' ');
            sb.Append(SamplesPerSec);
            sb.Append("Hz");
            if (FormatTag == WAVE_FORMAT_PCM) {
                sb.Append(' ');
                sb.Append(BitsPerSample);
                sb.Append("-bit");
            }
            return sb.ToString();
        }

        //
        // WAVE PCM encoding, briefly:
        //
        // Samples are stored sequentially, in whole bytes.  For sample sizes that aren't a
        // multiple of 8, data is stored in the most-significant bits.  The low bits are set
        // to zero.
        //
        // For bits per sample <= 8, values are stored as unsigned, e.g. 8 bits = [0,255].
        // For bits per sample > 8, values are stored as signed, e.g. 16 bits = [-32768,32767].
        //
        // Data packing for PCM WAVE files:
        //  8-bit mono: (ch0) (ch0) (ch0) (ch0) ...
        //  8-bit stereo: (ch0 ch1) (ch0 ch1) ...
        //  16-bit mono: (ch0l ch0h) (ch0l ch0h) ...
        //  16-bit stereo: (ch0l ch0h ch1l ch1h) ...
        //

        private const int READ_BUF_LEN = 65536;     // must be a multiple of 4
        private byte[]? mReadBuf = null;

        /// <summary>
        /// Reads samples from the current stream position, and converts them to floating point
        /// values, in the range [-1,1).  The method will attempt to fill the entire buffer,
        /// but may not be able to do so if the end of the file is reached or the internal
        /// read buffer is smaller than the request.
        /// </summary>
        /// <remarks>
        /// This always uses the samples from channel 0, which is the left channel in a stereo
        /// recording.
        /// </remarks>
        /// <param name="outBuf">Buffer that receives output.</param>
        /// <param name="outOffset">Offset to first location in output buffer that will
        ///   receive data.</param>
        /// <returns>Number of values stored in the output buffer, or 0 if EOF has been reached.
        ///   Returns -1 if we can't interpret this WAVE data.</returns>
        public int GetSamples(float[] outBuf, int outOffset) {
            if (FormatTag != WAVE_FORMAT_PCM || BitsPerSample > 16) {
                return -1;
            }
            int bytesPerSample = ((BitsPerSample + 7) / 8) * Channels;

            int desiredNumSamples = outBuf.Length - outOffset;
            int byteCount = desiredNumSamples * bytesPerSample;
            int bytesRemaining = DataLength - (int)(mStream.Position - DataOffset);
            if (byteCount > bytesRemaining) {
                byteCount = bytesRemaining;
            }

            if (mReadBuf == null) {
                mReadBuf = new byte[READ_BUF_LEN];
            }
            mStream.ReadExactly(mReadBuf, 0, byteCount);
            int offset = 0;

            if (BitsPerSample <= 8) {
                while (byteCount != 0) {
                    outBuf[outOffset++] = (mReadBuf[offset] - 128) / 128.0f;
                    offset += bytesPerSample;
                    byteCount -= bytesPerSample;
                }
            } else {
                while (byteCount != 0) {
                    int sample = mReadBuf[offset] | (mReadBuf[offset + 1] << 8);
                    outBuf[outOffset++] = sample / 32768.0f;
                    offset += bytesPerSample;
                    byteCount -= bytesPerSample;
                }
            }
            return outOffset;
        }
    }
}
